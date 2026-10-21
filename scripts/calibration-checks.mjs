import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createServer } from 'node:net';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const output = path.resolve('artifacts/calibration-checks', String(Date.now()));
await mkdir(output, { recursive: true });
const probe = createServer(); await new Promise(r => probe.listen(0, '127.0.0.1', r));
const port = probe.address().port; await new Promise(r => probe.close(r));
const base = `http://127.0.0.1:${port}`;
const host = spawn('dotnet', [path.resolve('src/Host/bin/Release/net10.0/AxisLab.Host.dll')], {
  cwd: path.resolve('src/Host'), windowsHide: true,
  env: { ...process.env, AXISLAB_URL: base, AXISLAB_DATA: path.join(output, 'projects'), AXISLAB_MODBUS_PORT: '0', Logging__LogLevel__Default: 'Warning' }
});
let hostLog = ''; host.stdout.on('data', b => hostLog += b); host.stderr.on('data', b => hostLog += b);
let browser;
try {
  const deadline = Date.now() + 20000;
  while (!(await fetch(base + '/api/health').then(r => r.ok).catch(() => false))) {
    if (Date.now() > deadline) throw new Error('Host startup timeout: ' + hostLog);
    await new Promise(r => setTimeout(r, 100));
  }
  const project = JSON.parse(await readFile('samples/nine-point-calibration.scene.json', 'utf8'));
  const current = await (await fetch(base + '/api/v1/project')).json();
  const response = await fetch(base + '/api/v1/project/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project, expectedRevision: current.revision }) });
  assert.ok(response.ok, await response.text());
  browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.goto(base); await page.waitForSelector('#viewport canvas');
  await page.waitForTimeout(1200);
  assert.equal(await page.locator('[data-add="calibration.board"]').count(), 1);
  assert.equal(await page.locator('[data-add="calibration.marker"]').count(), 1);
  await page.screenshot({ path: path.join(output, 'axis-calibration-scene.png'), fullPage: true });
  const config = path.join(output, 'settings.json'); const report = path.join(output, 'wpf-results.json');
  await writeFile(config, JSON.stringify({ url: base, directory: output }));
  const exe = path.resolve(process.env.AXISLAB_WPF_EXE ?? 'src/WpfDemo/bin/Release/net9.0-windows/AxisLab.WpfDemo.exe');
  await promisify(execFile)(exe, ['--sampling-test', config, report], {
    cwd: output, windowsHide: true, timeout: 240000, env: { ...process.env, AXISLAB_WPF_DATA: path.join(output, 'data') }
  });
  const result = JSON.parse(await readFile(report, 'utf8')); assert.equal(result.success, true, result.error);
  const plan = JSON.parse(await readFile(path.join(result.samplingDirectory, 'plan.json'), 'utf8'));
  const camera = project.devices.find(d => d.typeId === 'camera.pinhole');
  const checks = [...result.checks];
  for (const point of plan.points) {
    const png = await readFile(path.join(result.samplingDirectory, point.image));
    const expected = { x: 400 + (point.actual.x - camera.transform.x) * 1.5, y: 300 - (point.actual.y - camera.transform.y) * 1.5 };
    const pixels = await page.evaluate(async ({ src, expected }) => {
      const image = new Image(); image.src = src; await image.decode();
      const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
      const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0); const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
      let n = 0, sx = 0, sy = 0;
      for (let y = Math.floor(expected.y - 20); y <= expected.y + 20; y++) for (let x = Math.floor(expected.x - 20); x <= expected.x + 20; x++) {
        const i = (y * canvas.width + x) * 4;
        if (data[i] < 45 && data[i + 1] < 45 && data[i + 2] < 45) { n++; sx += x + .5; sy += y + .5; }
      }
      return { n, x: sx / n, y: sy / n };
    }, { src: 'data:image/png;base64,' + png.toString('base64'), expected });
    assert.ok(pixels.n > 550 && Math.hypot(pixels.x - expected.x, pixels.y - expected.y) < 0.8, JSON.stringify({ point: point.number, pixels, expected }));
    checks.push(`point ${point.number}: rendered black circle follows measured TCP to within 0.8 pixel (truth used only by test)`);
  }
  // A separate fixed board verifies all nine visible marks, independently of the TCP-marker workflow.
  const board = structuredClone(project.devices.find(d => d.typeId === 'calibration.marker'));
  board.typeId = 'calibration.board'; board.name = '固定九点板'; board.parentId = null; board.attachmentFrameId = 'base';
  board.transform = { x: 0, y: 0, z: 100, rx: 0, ry: 0, rz: 0 };
  board.parameters.sizeX = board.parameters.sizeY = 180; board.parameters.sizeZ = 3;
  const boardCamera = structuredClone(camera); boardCamera.transform = { x: 0, y: 0, z: 700, rx: 180, ry: 0, rz: 0 };
  const boardProject = { ...project, devices: [boardCamera, board], name: '固定九点板成像验证' };
  const latest = await (await fetch(base + '/api/v1/project')).json();
  const importedBoard = await fetch(base + '/api/v1/project/import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ project: boardProject, expectedRevision: latest.revision }) });
  assert.ok(importedBoard.ok, await importedBoard.text());
  // Keep the existing renderer lease alive across the imported project; reloading would leave its old lease until expiry.
  await page.waitForTimeout(1200);
  const capture = await (await fetch(base + `/api/v1/cameras/${boardCamera.id}/captures`, { method: 'POST' })).json();
  let state = capture;
  for (let i = 0; i < 150 && state.status !== 'Completed'; i++) { await page.waitForTimeout(100); state = await (await fetch(base + `/api/v1/captures/${capture.captureId}`)).json(); if (state.status === 'Failed') throw new Error(JSON.stringify(state)); }
  assert.equal(state.status, 'Completed');
  const boardPng = Buffer.from(await (await fetch(base + `/api/v1/cameras/${boardCamera.id}/frames/${state.frameId}/image`)).arrayBuffer());
  await writeFile(path.join(output, 'nine-dot-board.png'), boardPng);
  const circles = await page.evaluate(async src => {
    const img = new Image(); img.src = src; await img.decode(); const canvas = document.createElement('canvas'); canvas.width = img.width; canvas.height = img.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(img, 0, 0); const data = ctx.getImageData(0, 0, img.width, img.height).data;
    const result = [];
    for (const dy of [-90, 0, 90]) for (const dx of [-90, 0, 90]) {
      let n = 0;
      for (let y = 300 + dy - 18; y <= 300 + dy + 18; y++) for (let x = 400 + dx - 18; x <= 400 + dx + 18; x++) {
        const i = (y * img.width + x) * 4; if (data[i] < 45 && data[i + 1] < 45 && data[i + 2] < 45) n++;
      }
      result.push(n);
    }
    return result;
  }, 'data:image/png;base64,' + boardPng.toString('base64'));
  assert.ok(circles.every(n => n > 550 && n < 800), JSON.stringify(circles));
  checks.push('fixed nine-dot board renders all nine black circles at the configured physical pitch');
  await writeFile(path.join(output, 'results.json'), JSON.stringify({ success: true, checks, samplingDirectory: result.samplingDirectory }, null, 2));
  console.log(`${checks.length} calibration integration checks passed. Artifacts: ${output}`);
} catch (error) {
  console.error('Artifacts: ' + output);
  console.error(await readFile(path.join(output, 'wpf-results.json'), 'utf8').catch(() => ''));
  throw error;
} finally {
  if (browser) await browser.close(); host.kill(); await writeFile(path.join(output, 'host.log'), hostLog);
}
