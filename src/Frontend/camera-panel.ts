import { escape, icon } from './icons';
import type { CaptureState, Device, FrameMetadata } from './types';

async function request<T>(path: string, method = 'GET'): Promise<T> {
  const response = await fetch('/api/v1' + path, { method, signal: AbortSignal.timeout(10000) });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new Error(body.message ?? `请求失败 (${response.status})`);
  }
  return response.json() as Promise<T>;
}
interface Preview { metadata: FrameMetadata; url: string }
export class CameraPanel {
  private sessionId = '';
  private devices: Device[] = [];
  private cameraId: string | null = null;
  private previews = new Map<string, Preview>();
  private busy = new Set<string>();
  private messages = new Map<string, string>();
  private service = '相机成像连接中';
  constructor(private element: HTMLElement, private aim: (id: string) => void,
    private notify: (message: string, error?: boolean) => void) {}
  setDevices(devices: Device[], sessionId: string, selected: string | null) {
    if (sessionId !== this.sessionId) {
      for (const p of this.previews.values()) URL.revokeObjectURL(p.url);
      this.previews.clear(); this.messages.clear(); this.busy.clear(); this.cameraId = null;
    }
    this.sessionId = sessionId; this.devices = devices.filter(d => d.typeId === 'camera.pinhole');
    for (const [id, p] of this.previews) if (!this.devices.some(d => d.id === id)) {
      URL.revokeObjectURL(p.url); this.previews.delete(id);
    }
    if (this.devices.some(d => d.id === selected)) this.cameraId = selected;
    if (!this.devices.some(d => d.id === this.cameraId)) this.cameraId = this.devices[0]?.id ?? null;
    this.draw();
  }
  rendererStatus(message: string) {
    this.service = message;
    const text = this.element.querySelector('#renderer-state'); if (text) text.textContent = message;
  }
  async receive(state: CaptureState) {
    if (state.status !== 'Completed' || !state.frameId) return;
    const session = this.sessionId;
    try {
      const metadata = await request<FrameMetadata>(`/cameras/${state.cameraId}/frames/${state.frameId}`);
      if (session !== this.sessionId || metadata.sessionId !== session) return;
      const response = await fetch(metadata.imageUrl, { signal: AbortSignal.timeout(10000) });
      if (!response.ok) throw new Error('图像缓存已清理，请重新拍照。');
      const blob = await response.blob();
      if (session !== this.sessionId || !this.devices.some(d => d.id === state.cameraId)) return;
      const old = this.previews.get(state.cameraId);
      if (old && old.metadata.completedAt > metadata.completedAt) return;
      if (old) URL.revokeObjectURL(old.url);
      this.previews.set(state.cameraId, { metadata, url: URL.createObjectURL(blob) });
      this.messages.set(state.cameraId, '拍照完成'); this.draw();
    } catch (error) { this.notify(error instanceof Error ? error.message : '图像读取失败', true); }
  }
  private async capture(id: string) {
    if (this.busy.has(id)) return;
    const session = this.sessionId;
    const old = this.previews.get(id); if (old) URL.revokeObjectURL(old.url);
    this.previews.delete(id); this.busy.add(id); this.messages.set(id, '正在拍照…'); this.draw();
    try {
      let state = await request<CaptureState>(`/cameras/${id}/captures`, 'POST');
      const deadline = Date.now() + 16000;
      while (state.status === 'Queued' || state.status === 'Rendering') {
        if (session !== this.sessionId) return;
        if (Date.now() > deadline) throw new Error('等待成像超时，请重试。');
        await new Promise(resolve => setTimeout(resolve, 180));
        state = await request<CaptureState>(`/captures/${state.captureId}`);
      }
      if (session !== this.sessionId) return;
      if (state.status === 'Failed') throw new Error(state.message ?? '拍照失败');
      // The renderer may be another window, so the triggering panel also reads the result.
      if (this.previews.get(id)?.metadata.frameId !== state.frameId) await this.receive(state);
    } catch (error) {
      if (session === this.sessionId) {
        const message = error instanceof Error ? error.message : '拍照失败';
        this.messages.set(id, message); this.notify(message, true);
      }
    } finally { if (session === this.sessionId) { this.busy.delete(id); this.draw(); } }
  }
  private save(url: string, name: string) {
    const a = document.createElement('a'); a.href = url; a.download = name; a.click();
  }
  private draw() {
    const camera = this.devices.find(d => d.id === this.cameraId);
    const preview = this.cameraId ? this.previews.get(this.cameraId) : null;
    this.element.innerHTML = `<div class="camera-panel"><div class="camera-preview">${preview ? `<img id="camera-image" src="${preview.url}" alt="${escape(camera?.name ?? '')}的仿真照片">` : `<span>${icon('camera')}<br>${camera ? '点击拍照，生成此相机的实际视野' : '先从设备库添加一台相机'}</span>`}</div><div class="camera-tools"><div class="camera-toolbar"><select id="capture-camera" aria-label="拍照相机">${this.devices.map(d => `<option value="${d.id}" ${d.id === this.cameraId ? 'selected' : ''}>${escape(d.name)} · #${d.deviceNumber}</option>`).join('')}</select><button class="btn primary" id="capture-camera-now" ${!camera || this.busy.has(camera.id) ? 'disabled' : ''}>${icon('camera')}拍照</button><button class="btn outline" id="aim-camera" ${!camera ? 'disabled' : ''}>朝向机械臂</button></div><div class="camera-toolbar"><button class="btn outline" id="save-camera-image" ${!preview ? 'disabled' : ''}>保存 PNG</button><button class="btn outline" id="save-frame-metadata" ${!preview ? 'disabled' : ''}>导出帧信息</button><small id="renderer-state">${escape(this.service)}</small></div><div id="capture-status" role="status">${escape((this.cameraId && this.messages.get(this.cameraId)) || '支持界面拍照与外部 HTTP 触发；相机坐标 +X 向右、+Y 向下、+Z 向前。')}</div><div class="frame-details">${preview ? `${preview.metadata.width} × ${preview.metadata.height} px · PNG · ${new Date(preview.metadata.exposureAt).toLocaleTimeString('zh-CN', { hour12: false })}<br>Frame ${preview.metadata.frameId}<br>曝光姿态 ${preview.metadata.robots.map(r => 'Pose ' + r.poseId).join(' / ') || '无机械臂'} · 配置 v${preview.metadata.cameraConfigurationRevision}` : '拍照包含可见设备和地面；编辑坐标轴与辅助网格不进入照片。'}</div></div></div>`;
    this.element.querySelector<HTMLSelectElement>('#capture-camera')!.onchange = e => {
      this.cameraId = (e.target as HTMLSelectElement).value; this.draw();
    };
    this.element.querySelector<HTMLButtonElement>('#capture-camera-now')!.onclick = () => { if (camera) void this.capture(camera.id); };
    this.element.querySelector<HTMLButtonElement>('#aim-camera')!.onclick = () => { if (camera) this.aim(camera.id); };
    this.element.querySelector<HTMLButtonElement>('#save-camera-image')!.onclick = () => {
      if (preview) this.save(preview.url, `${preview.metadata.cameraName.replace(/[<>:"/\\|?*]/g, '_')}-${preview.metadata.frameId}.png`);
    };
    this.element.querySelector<HTMLButtonElement>('#save-frame-metadata')!.onclick = () => {
      if (!preview) return;
      const url = URL.createObjectURL(new Blob([JSON.stringify(preview.metadata, null, 2)], { type: 'application/json' }));
      this.save(url, `${preview.metadata.frameId}.frame.json`); setTimeout(() => URL.revokeObjectURL(url), 1000);
    };
  }
}
