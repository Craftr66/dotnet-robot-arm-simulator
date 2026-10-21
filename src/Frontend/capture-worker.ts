import type { CaptureWork, RendererLease, CaptureState } from './types';

// Only the leased viewport renders. Other open windows can still trigger and read captures.
export function startCaptureWorker(render: (work: CaptureWork) => Promise<Blob>,
  completed: (state: CaptureState) => void, status: (message: string) => void) {
  let lease: RendererLease | null = null;
  let polling = false;
  let heartbeating = false;
  let nextRegistration = 0;
  async function request(path: string, method = 'GET', body?: BodyInit, contentType?: string) {
    const response = await fetch('/api/v1' + path, { method, body,
      headers: { ...(lease ? { 'X-AxisLab-Renderer-Token': lease.token } : {}),
        ...(contentType ? { 'Content-Type': contentType } : {}) }, signal: AbortSignal.timeout(10000) });
    if (!response.ok) {
      if (response.status === 403) { lease = null; status('渲染连接恢复中'); }
      const error = await response.json().catch(() => ({}));
      throw new Error(error.message ?? `成像请求失败 (${response.status})`);
    }
    return response;
  }
  setInterval(async () => {
    if (!lease || heartbeating) return;
    heartbeating = true;
    try { await request(`/renderers/${lease.rendererId}/heartbeat`, 'POST'); status('相机成像就绪'); }
    catch { status('渲染连接恢复中'); }
    finally { heartbeating = false; }
  }, 1000);
  setInterval(async () => {
    if (polling) return;
    polling = true;
    try {
      if (!lease) {
        if (Date.now() < nextRegistration) return;
        nextRegistration = Date.now() + 2000;
        const response = await fetch('/api/v1/renderers', { method: 'POST', signal: AbortSignal.timeout(5000) });
        if (response.status === 409) { status('成像由另一窗口提供'); return; }
        if (!response.ok) throw new Error('渲染服务连接失败');
        lease = await response.json() as RendererLease; status('相机成像就绪');
      }
      const rendererId = lease.rendererId;
      const response = await request(`/renderers/${rendererId}/captures`);
      if (response.status === 204) return;
      const work = await response.json() as CaptureWork;
      try {
        const png = await render(work);
        const uploaded = await request(`/renderers/${rendererId}/captures/${work.captureId}/image`, 'PUT', png, 'image/png');
        completed(await uploaded.json() as CaptureState);
      } catch (error) {
        await request(`/renderers/${rendererId}/captures/${work.captureId}/fail`, 'POST',
          JSON.stringify({ message: error instanceof Error ? error.message : '成像失败' }), 'application/json').catch(() => {});
      }
    } catch { status('渲染连接恢复中'); }
    finally { polling = false; }
  }, 150);
}
