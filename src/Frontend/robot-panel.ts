import { escape, icon } from './icons';
import type { Device, JointDefinition, RobotState } from './types';

export class RobotPanel {
  private id: string | null = null;
  private state: RobotState | null = null;
  private targetEdited = false;
  private definitions: JointDefinition[] = [];
  constructor(private root: HTMLElement, private models: Record<string, JointDefinition[]>,
    private move: (id: string, joints: number[], speed: number) => void, private stop: (id: string) => void) {}
  show(device: Device, state: RobotState) {
    this.definitions = this.models[device.robot!.modelId];
    if (this.id === device.id && this.root.querySelector('.joint-card')) {
      this.root.querySelector('.robot-name')!.textContent = device.name; this.update(state); return;
    }
    this.id = device.id; this.targetEdited = false;
    this.root.innerHTML = `<div class="motion-header"><strong class="robot-name">${escape(device.name)}</strong><span id="motion-state">就绪</span><label>步长 <select id="jog-step"><option value="1">1°</option><option value="5" selected>5°</option><option value="10">10°</option></select></label><label>速度 <select id="joint-speed"><option value="10">10%</option><option value="25" selected>25%</option><option value="50">50%</option><option value="100">100%</option></select></label></div>
      <div class="joint-grid">${this.definitions.map((d, i) => `<div class="joint-card"><div class="joint-label"><strong>${d.name}</strong><span data-current="${i}">0.00°</span><small>${d.minimum} ~ ${d.maximum}°</small></div><div class="joint-inputs"><button type="button" data-jog="${i}" data-direction="-1" title="${d.name} 减小">−</button><input type="number" data-target="${i}" min="${d.minimum}" max="${d.maximum}" step="any" value="${state.joints[i]}" aria-label="${d.name} 目标角度"><button type="button" data-jog="${i}" data-direction="1" title="${d.name} 增大">+</button></div></div>`).join('')}</div>
      <div class="motion-actions"><button class="btn primary" id="execute-joints">${icon('check')}执行关节目标</button><button class="btn outline" id="zero-joints">关节回零</button><button class="btn danger" id="stop-robot">停止运动</button><span id="motion-progress"></span></div>
      <div class="motion-readout"><div><b>TCP · 基坐标</b><span id="tcp-readout"></span></div><div><b>法兰 · 基坐标</b><span id="flange-readout"></span></div><small>五次时间插补与基础碰撞预检；F 为法兰，T 为 TCP；位姿与回放见“运动与示教”。</small></div>`;
    const speed = () => Number(this.root.querySelector<HTMLSelectElement>('#joint-speed')!.value);
    this.root.querySelectorAll<HTMLInputElement>('[data-target]').forEach(input => input.oninput = () => { this.targetEdited = true; });
    this.root.querySelectorAll<HTMLButtonElement>('[data-jog]').forEach(button => button.onclick = () => {
      if (!this.state) return;
      const targets = [...this.state.joints]; const index = Number(button.dataset.jog);
      targets[index] += Number(button.dataset.direction) * Number(this.root.querySelector<HTMLSelectElement>('#jog-step')!.value);
      this.move(device.id, targets, speed());
    });
    this.root.querySelector<HTMLButtonElement>('#execute-joints')!.onclick = () => {
      const inputs = [...this.root.querySelectorAll<HTMLInputElement>('[data-target]')];
      if (inputs.some(input => !input.reportValidity() || input.value === '')) return;
      this.targetEdited = false; this.move(device.id, inputs.map(input => Number(input.value)), speed());
    };
    this.root.querySelector<HTMLButtonElement>('#zero-joints')!.onclick = () => this.move(device.id, this.definitions.map(() => 0), speed());
    this.root.querySelector<HTMLButtonElement>('#stop-robot')!.onclick = () => this.stop(device.id);
    this.update(state);
  }
  clear() { this.id = null; this.state = null; this.root.innerHTML = '<div class="motion-placeholder">添加或选择机械臂，开始关节点动与正运动学练习。</div>'; }
  update(state: RobotState) {
    if (this.id !== state.deviceId) return;
    const finished = this.state?.status === 'Running' && state.status !== 'Running';
    this.state = state;
    const moving = state.status === 'Running';
    const external = !!state.controlOwnerId;
    this.root.querySelectorAll<HTMLElement>('[data-current]').forEach(el => el.textContent = state.joints[Number(el.dataset.current)].toFixed(2) + '°');
    this.root.querySelectorAll<HTMLInputElement>('[data-target]').forEach(input => {
      input.disabled = moving || external;
      if (finished || !this.targetEdited && document.activeElement !== input) input.value = state.joints[Number(input.dataset.target)].toFixed(3);
    });
    if (finished) this.targetEdited = false;
    this.root.querySelectorAll<HTMLButtonElement>('[data-jog],#execute-joints,#zero-joints').forEach(button => button.disabled = moving || external);
    this.root.querySelector<HTMLButtonElement>('#stop-robot')!.disabled = !moving && !external;
    this.root.querySelector('#motion-state')!.textContent = external ? '外部控制' : { Idle: '就绪', Running: '运动中', Completed: '已完成', Cancelled: '已停止', Failed: '碰撞停止' }[state.status];
    this.root.querySelector('#motion-progress')!.textContent = moving ? (state.progress * 100).toFixed(0) + '%' : '';
    const readout = (frame: RobotState['tcpBase']) => {
      const p = frame.pose;
      return `X ${p.x.toFixed(2)}  Y ${p.y.toFixed(2)}  Z ${p.z.toFixed(2)} mm   |   Rx ${p.rx.toFixed(1)}  Ry ${p.ry.toFixed(1)}  Rz ${p.rz.toFixed(1)}°`;
    };
    this.root.querySelector('#tcp-readout')!.textContent = readout(state.tcpBase);
    this.root.querySelector('#flange-readout')!.textContent = readout(state.flangeBase);
  }
}
