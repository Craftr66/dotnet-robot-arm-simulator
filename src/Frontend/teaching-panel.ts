import { escape } from './icons';
import type { Device, Pose, RobotState, Snapshot, TeachPointView } from './types';

export class TeachingPanel {
  private id: string | null = null;
  private session = '';
  private snapshot!: Snapshot;
  private state!: RobotState;
  private points: TeachPointView[] = [];
  private listKey = '';
  private working = false;
  constructor(private root: HTMLElement,
    private request: <T>(path: string, method: string, data: unknown) => Promise<T>,
    private act: (action: () => Promise<void>) => void,
    private accept: (snapshot: Snapshot) => void, private stop: (id: string) => void) {}
  clear() { this.id = null; this.root.innerHTML = '<div class="motion-placeholder">选择机械臂，设置目标位姿、记录示教点并回放。</div>'; }
  show(device: Device, state: RobotState, snapshot: Snapshot) {
    this.snapshot = snapshot; this.state = state;
    if (this.id !== device.id || this.session !== snapshot.sessionId) {
      this.id = device.id; this.session = snapshot.sessionId; this.listKey = '';
      const fields = (['x','y','z','rx','ry','rz'] as const).map(k => `<label>${k.toUpperCase()}<input data-pose="${k}" type="number" step="any" value="${state.tcpBase.pose[k].toFixed(3)}" aria-label="目标 ${k.toUpperCase()}"></label>`).join('');
      this.root.innerHTML = `<div class="teaching-panel"><section><div class="teach-toolbar"><strong>目标位姿</strong><select id="target-frame" aria-label="目标坐标系"><option value="base">机械臂基坐标</option><option value="world">世界坐标</option><option value="work">工件坐标 W</option></select><select id="cart-kind" aria-label="运动类型"><option>MoveJ</option><option>MoveL</option></select><select id="cart-speed" aria-label="笛卡尔速度"><option value="10">10%</option><option value="25" selected>25%</option><option value="50">50%</option><option value="100">100%</option></select></div><div class="pose-grid">${fields}</div><label class="checkbox-label"><input type="checkbox" id="position-only" ${device.typeId === 'robot.generic3r' ? 'checked disabled' : ''}>只约束位置（三轴固定启用） · mm / deg</label><div class="teach-actions"><button class="btn outline" id="read-tcp">读取当前 TCP</button><button class="btn outline" id="preview-ik">预检目标</button><button class="btn primary" id="execute-cart">执行目标</button><button class="btn danger" id="teach-stop">停止</button></div><div id="ik-result" class="teach-message">MoveJ 按关节路径运动；MoveL 保持 TCP 直线路径。先预检再执行。</div></section><section><div class="teach-toolbar"><strong>示教点</strong><input id="teach-name" maxlength="80" value="P${snapshot.project.nextTeachPointNumber}" aria-label="示教点名称"><button class="btn outline" id="record-point">记录当前</button></div><select id="teach-list" size="4" aria-label="示教点列表"></select><div class="teach-actions"><button class="btn outline" id="rename-point">改名</button><button class="btn outline" id="update-point">覆盖当前</button><button class="btn outline" id="revalidate-point">重新校验</button><button class="btn danger" id="delete-point">删除</button></div><div class="teach-actions"><button class="btn outline" id="play-point">单步运行</button><button class="btn primary" id="play-all">顺序回放</button><small>按列表顺序 · 逐点停稳</small></div><div id="teach-detail" class="teach-message"></div></section></div><div id="teaching-state" class="teaching-state"></div>`;
      const el = <T extends HTMLElement>(s: string) => this.root.querySelector<T>(s)!;
      const value = (s: string) => el<HTMLInputElement>(s).value;
      const run = (f: () => Promise<void>) => this.act(async () => {
        this.working = true; this.update(this.state);
        try { await f(); } finally { this.working = false; this.update(this.state); }
      });
      const name = () => { const text = value('#teach-name').trim(); if (!text) throw new Error('请填写示教点名称。'); return text; };
      const motion = () => {
        const target = {} as Pose;
        this.root.querySelectorAll<HTMLInputElement>('[data-pose]').forEach(input => {
          if (input.value === '' || !Number.isFinite(Number(input.value))) throw new Error('目标位姿需填写有限数值。');
          target[input.dataset.pose as keyof Pose] = Number(input.value);
        });
        return { target, frame: value('#target-frame'), kind: value('#cart-kind'), speedPercent: Number(value('#cart-speed')), positionOnly: el<HTMLInputElement>('#position-only').checked };
      };
      const read = () => {
        const frame = value('#target-frame');
        let pose = this.state.tcpBase.pose;
        if (frame === 'world') pose = this.state.tcpWorld.pose;
        if (frame === 'work') {
          // Runtime matrices are column-major; invert the rigid work frame.
          const a = this.state.workWorld.matrix, b = this.state.tcpWorld.matrix;
          const inv = [a[0],a[4],a[8],0,a[1],a[5],a[9],0,a[2],a[6],a[10],0,-(a[0]*a[12]+a[1]*a[13]+a[2]*a[14]),-(a[4]*a[12]+a[5]*a[13]+a[6]*a[14]),-(a[8]*a[12]+a[9]*a[13]+a[10]*a[14]),1];
          const m = Array(16).fill(0) as number[];
          for(let c=0;c<4;c++) for(let r=0;r<4;r++) for(let k=0;k<4;k++) m[c*4+r] += inv[k*4+r]*b[c*4+k];
          const ry = Math.asin(Math.max(-1,Math.min(1,-m[2]))); const normal = Math.abs(Math.cos(ry)) > 1e-8;
          pose = {x:m[12],y:m[13],z:m[14],rx:(normal?Math.atan2(m[6],m[10]):0)*180/Math.PI,ry:ry*180/Math.PI,rz:(normal?Math.atan2(m[1],m[0]):Math.atan2(-m[4],m[5]))*180/Math.PI};
        }
        this.root.querySelectorAll<HTMLInputElement>('[data-pose]').forEach(input => input.value = pose[input.dataset.pose as keyof Pose].toFixed(3));
      };
      el('#read-tcp').onclick = read; el('#target-frame').onchange = () => { el('#ik-result').textContent = '坐标系已切换，请读取当前 TCP 或填写该坐标系下的目标。'; };
      el('#preview-ik').onclick = () => run(async () => {
        el('#ik-result').textContent = '正在预检目标…';
        const result = await this.request<{ joints: number[]; duration: number; samples: number; warning: string | null }>(`/v1/robots/${device.id}/preview`, 'POST', motion());
        el('#ik-result').textContent = `预检通过 · ${result.duration.toFixed(2)} s · J ${result.joints.map(q=>q.toFixed(2)).join(', ')}°${result.warning ? ' · '+result.warning : ''}`;
      });
      el('#execute-cart').onclick = () => run(async () => this.accept(await this.request<Snapshot>(`/v1/robots/${device.id}/cartesian`, 'POST', { expectedRevision: this.snapshot.revision, motion: motion() })));
      el('#teach-stop').onclick = () => this.stop(device.id);
      el('#record-point').onclick = () => run(async () => { this.accept(await this.request<Snapshot>(`/v1/robots/${device.id}/teach-points`, 'POST', {expectedRevision:this.snapshot.revision,name:name(),frame:value('#target-frame')})); el<HTMLInputElement>('#teach-name').value = `P${this.snapshot.project.nextTeachPointNumber}`; });
      for (const action of ['rename','update','revalidate','delete']) el(`#${action}-point`).onclick = () => run(async () => {
        const pointId = value('#teach-list'); if (!pointId) throw new Error('请先选择示教点。');
        const selectedPoint = this.points.find(p=>p.point.id === pointId)!;
        this.accept(await this.request<Snapshot>(`/v1/teach-points/${pointId}`, 'PUT', {expectedRevision:this.snapshot.revision,name:action === 'rename' ? name() : selectedPoint.point.name,action}));
      });
      const play = async (all: boolean) => {
        const pointIds = all ? this.points.map(p=>p.point.id) : [value('#teach-list')].filter(Boolean);
        if (!pointIds.length) throw new Error('请先记录或选择示教点。');
        this.accept(await this.request<Snapshot>(`/v1/robots/${device.id}/play`, 'POST', {expectedRevision:this.snapshot.revision,pointIds,kind:value('#cart-kind'),speedPercent:Number(value('#cart-speed'))}));
      };
      el('#play-point').onclick = () => run(()=>play(false)); el('#play-all').onclick = () => run(()=>play(true));
      el('#teach-list').onchange = () => {
        const selected = this.points.find(p=>p.point.id === value('#teach-list'));
        if (selected) el<HTMLInputElement>('#teach-name').value = selected.point.name;
        this.detail();
      };
    }
    this.points = snapshot.teaching.filter(p=>p.point.robotId === device.id);
    const key = JSON.stringify(this.points);
    if (key !== this.listKey) {
      const list = this.root.querySelector<HTMLSelectElement>('#teach-list')!; const previous = list.value;
      list.innerHTML = this.points.map(p=>`<option value="${p.point.id}">${p.stale ? '⚠ 待校验' : '✓'} #${p.point.number} ${escape(p.point.name)}</option>`).join('');
      list.value = this.points.some(p=>p.point.id === previous) ? previous : this.points[0]?.point.id ?? '';
      this.listKey = key; this.detail();
    }
    this.update(state);
  }
  private detail() {
    const p = this.points.find(p=>p.point.id === this.root.querySelector<HTMLSelectElement>('#teach-list')?.value);
    this.root.querySelector('#teach-detail')!.textContent = p ? (p.stale ? p.reason : `${p.point.frame} · J ${p.point.joints.map(q=>q.toFixed(1)).join(', ')}°`) : '记录当前实际关节；修改几何、工具、工件坐标或障碍物后须重新校验。';
  }
  update(state: RobotState) {
    if (state.deviceId !== this.id) return; this.state = state;
    const busy = this.working || state.status === 'Running' || !!state.controlOwnerId;
    this.root.querySelectorAll<HTMLButtonElement>('button:not(#teach-stop)').forEach(b=>b.disabled=busy);
    this.root.querySelector<HTMLButtonElement>('#teach-stop')!.disabled = !busy;
    const statuses = {Idle:'就绪',Running:'运行中',Completed:'已完成',Cancelled:'已停止',Failed:'运行失败'};
    this.root.querySelector('#teaching-state')!.textContent = this.working ? '正在校验并提交…' : `${statuses[state.status]} · ${state.motionKind} ${state.stepCount ? `· 步骤 ${state.stepIndex}/${state.stepCount} · ${(state.progress*100).toFixed(0)}%` : ''}${state.motionMessage ? ' · '+state.motionMessage : ''}`;
  }
}
