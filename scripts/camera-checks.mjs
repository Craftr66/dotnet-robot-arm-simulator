import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import path from 'node:path';

const base = process.env.AXISLAB_TEST_URL ?? 'http://127.0.0.1:5091';
const output = path.resolve('artifacts/camera-checks'); await mkdir(output, { recursive: true });
const checks = [], errors = [];
const check = (name, okay = true) => { assert.ok(okay, name); checks.push(name); console.log('PASS: ' + name); };
async function api(endpoint, method = 'GET', data) {
  const r = await fetch(base + '/api/v1' + endpoint, { method, headers: data ? { 'Content-Type': 'application/json' } : {}, body: data ? JSON.stringify(data) : undefined });
  const value = await r.json(); if (!r.ok) throw new Error(JSON.stringify(value)); return value;
}
let state = await api('/project');
state = await api('/project/new', 'POST', { name: '阶段3相机验收', expectedRevision: state.revision });
const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1536, height: 1050 } });
page.on('pageerror', e => errors.push(e.message)); page.setDefaultTimeout(20000);
const snapshot = () => api('/project');
async function add(typeId) { state = await snapshot(); state = await api('/devices', 'POST', { typeId, expectedRevision: state.revision }); return state.project.devices.at(-1); }
async function update(device) { state = await snapshot(); state = await api('/devices/' + device.id, 'PUT', { device, expectedRevision: state.revision }); return state.project.devices.find(d => d.id === device.id); }
async function expose(id) {
  let s = await api(`/cameras/${id}/captures`, 'POST'); const deadline = Date.now() + 16000;
  while (s.status === 'Queued' || s.status === 'Rendering') {
    if (Date.now() > deadline) throw new Error('Capture timeout');
    await new Promise(r => setTimeout(r, 120)); s = await api('/captures/' + s.captureId);
  }
  assert.equal(s.status, 'Completed', s.message);
  const meta = await api(`/cameras/${id}/frames/${s.frameId}`);
  const response = await fetch(base + meta.imageUrl); assert.equal(response.status, 200);
  const png = Buffer.from(await response.arrayBuffer());
  assert.equal(createHash('sha256').update(png).digest('hex').toUpperCase(), meta.sha256);
  return { meta, png };
}
async function pixels(png) {
  return page.evaluate(async data => {
    const image = await createImageBitmap(await (await fetch('data:image/png;base64,' + data)).blob());
    const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
    const c = canvas.getContext('2d'); c.drawImage(image, 0, 0);
    return { width: image.width, height: image.height, values: Array.from(c.getImageData(0, 0, canvas.width, canvas.height).data) };
  }, png.toString('base64'));
}
try {
  await page.goto(base); await page.waitForFunction(() => document.querySelector('#renderer-state')?.textContent === '相机成像就绪');
  const robot = await add('robot.generic6r');
  await page.waitForSelector(`[data-device="${robot.id}"]`); await page.locator(`[data-device="${robot.id}"]`).click();
  check('robot exposes all 13 geometry fields', await page.locator('[name$="Diameter"]').count() === 6 && await page.locator('[name="upperArm"]').inputValue() === '300');
  await page.locator('[name="upperArm"]').fill('420'); await page.locator('[name="joint2Diameter"]').fill('150');
  await page.locator('#property-form button[type="submit"]').click();
  await page.waitForFunction(() => document.querySelector('#tcp-readout')?.textContent.includes('665.00'));
  check('geometry editor updates FK and diameters in core', (await snapshot()).project.devices[0].robot.geometry.joint2Diameter === 150);
  await page.locator('#reset-geometry').click(); await page.locator('#property-form button[type="submit"]').click();
  await page.waitForFunction(() => document.querySelector('#tcp-readout')?.textContent.includes('545.00'));
  check('restore default geometry recalculates TCP', (await snapshot()).project.devices[0].robot.geometry.upperArm === 300);
  let camera = await add('camera.pinhole');
  const zero = { x: 0, y: 0, z: 1000, rx: 180, ry: 0, rz: 0 };
  camera = await update({ ...camera, name: '独立顶部相机', transform: zero,
    parameters: { ...camera.parameters, width: 640, height: 480, fx: 500, fy: 450, cx: 270, cy: 190 } });
  let camera2 = await add('camera.pinhole'); camera2 = await update({ ...camera2, transform: { ...zero, x: 600, rz: 180 } });
  // Keep the robot outside the calibration image until the motion-capture check.
  await update({ ...robot, visible: false });
  let box = await add('geometry.box'); box = await update({ ...box, transform: { x: 100, y: 150, z: 100, rx: 0, ry: 0, rz: 0 },
    parameters: { ...box.parameters, sizeX: 80, sizeY: 60, sizeZ: 60 } });
  const first = await expose(camera.id), second = await expose(camera2.id);
  check('two real camera poses produce different image contents', first.meta.cameraId !== second.meta.cameraId && first.meta.sha256 !== second.meta.sha256);
  check('PNG is served with fixed frame SHA and expected dimensions', first.png.readUInt32BE(16) === 640 && first.png.readUInt32BE(20) === 480);
  await writeFile(path.join(output, 'top-camera.png'), first.png); await writeFile(path.join(output, 'second-camera.png'), second.png);
  box = await update({ ...box, visible: false }); const blank = await expose(camera.id);
  const a = await pixels(first.png), b = await pixels(blank.png);
  const bounds = { minX: 9999, minY: 9999, maxX: -1, maxY: -1, count: 0 };
  for (let i = 0; i < a.values.length; i += 4) if (a.values.slice(i, i + 3).some((v, c) => Math.abs(v - b.values[i + c]) > 20)) {
    const x = (i / 4) % a.width, y = Math.floor(i / 4 / a.width);
    bounds.minX = Math.min(bounds.minX, x); bounds.maxX = Math.max(bounds.maxX, x);
    bounds.minY = Math.min(bounds.minY, y); bounds.maxY = Math.max(bounds.maxY, y); bounds.count++;
  }
  // Independent analytic projection of all box vertices using CV convention (u=fx*X/Z+cx).
  const us = [], vs = [];
  for (const x of [60, 140]) for (const y of [120, 180]) for (const z of [70, 130]) {
    us.push(500 * x / (1000 - z) + 270); vs.push(450 * -y / (1000 - z) + 190);
  }
  const expected = [Math.min(...us), Math.min(...vs), Math.max(...us), Math.max(...vs)];
  check('off-centre intrinsics and CV axes match analytic pixel bounds',
    bounds.count > 500 && [bounds.minX, bounds.minY, bounds.maxX, bounds.maxY].every((v, i) => Math.abs(v - expected[i]) < 3));
  check('hiding an object changes the next actual camera image', first.meta.sha256 !== blank.meta.sha256);
  box = await update({ ...box, visible: true, transform: { ...box.transform, x: 160 } });
  const shifted = await expose(camera.id); check('moving an object changes the next captured pixels', shifted.meta.sha256 !== first.meta.sha256);
  // UI preview, native browser download, metadata and HTTP must refer to exactly the same frame.
  await page.waitForSelector(`[data-device="${camera.id}"]`); await page.locator(`[data-device="${camera.id}"]`).click();
  await page.locator('#capture-camera-now').click(); await page.waitForSelector('#camera-image');
  await page.waitForFunction(() => !document.querySelector('#capture-camera-now')?.disabled);
  const downloadPromise = page.waitForEvent('download'); await page.locator('#save-camera-image').click();
  const download = await downloadPromise; const downloaded = await readFile(await download.path());
  const metaPromise = page.waitForEvent('download'); await page.locator('#save-frame-metadata').click();
  const metadataDownload = await metaPromise; const uiMeta = JSON.parse(await readFile(await metadataDownload.path(), 'utf8'));
  const httpImage = Buffer.from(await (await fetch(base + uiMeta.imageUrl)).arrayBuffer());
  const previewBytes = await page.locator('#camera-image').evaluate(async img => Array.from(new Uint8Array(await (await fetch(img.src)).arrayBuffer())));
  check('UI preview, PNG download and HTTP frame are byte-identical', downloaded.equals(httpImage) && downloaded.equals(Buffer.from(previewBytes)));
  check('exported frame metadata contains exposure pose and intrinsics', uiMeta.intrinsics.cx === 270 && uiMeta.cameraWorld.matrix.length === 16 && uiMeta.robots[0].poseId > 0);
  // Capturing during motion must retain the trigger-time pose, not the pose at download.
  state = await snapshot(); const motionRobot = state.project.devices.find(d => d.id === robot.id);
  await api(`/robots/${robot.id}/joints`, 'POST', { joints: [90, 0, 0, 0, 0, 0], speedPercent: 25, expectedRevision: state.revision });
  const moving = await expose(camera.id); await api(`/robots/${robot.id}/stop`, 'POST');
  check('moving exposure returns a recorded robot pose and command', moving.meta.robots[0].status === 'Running' && moving.meta.robots[0].commandId && moving.meta.robots[0].joints[0] < 90);
  // Run the actual independent .NET client against this live renderer.
  const client = await promisify(execFile)('dotnet', [path.resolve('samples/CSharpClient/bin/Release/net10.0/AxisLab.CaptureClient.dll'), base, camera.id, path.join(output, 'csharp')], { windowsHide: true, timeout: 30000 });
  check('standalone C# client triggers and verifies a real rendered PNG', client.stdout.includes('SHA256'));
  await page.screenshot({ path: path.join(output, 'camera-ui.png'), fullPage: true });
  const sample = JSON.parse(await readFile('samples/camera-capture.scene.json', 'utf8'));
  state = await snapshot(); state = await api('/project/import', 'POST', { project: sample, expectedRevision: state.revision });
  const sampleRobot = sample.devices.find(d => d.robot);
  const sampleFixed = sample.devices.find(d => d.typeId === 'camera.pinhole' && !d.parentId);
  const sampleHand = sample.devices.find(d => d.typeId === 'camera.pinhole' && d.parentId);
  await page.waitForSelector(`[data-device="${sampleFixed.id}"]`);
  check('packaged scene opens with configured geometry and two cameras', state.project.schemaVersion === 5 && sample.devices.filter(d => d.typeId === 'camera.pinhole').length === 2 && state.project.devices.find(d => d.robot).robot.geometry.upperArm === 300);
  const fixedPhoto = await expose(sampleFixed.id), handPhoto = await expose(sampleHand.id);
  await writeFile(path.join(output, 'sample-fixed.png'), fixedPhoto.png); await writeFile(path.join(output, 'sample-hand.png'), handPhoto.png);
  check('packaged fixed and flange cameras render their own views', fixedPhoto.meta.width === 800 && handPhoto.meta.width === 800 && fixedPhoto.meta.sha256 !== handPhoto.meta.sha256);
  state = await snapshot(); await api(`/robots/${sampleRobot.id}/joints`, 'POST', { joints: [20, 0, 0, 0, 0, 0], speedPercent: 100, expectedRevision: state.revision });
  let movingState = await api('/runtime');
  while (movingState.robots[0].status === 'Running') { await new Promise(r => setTimeout(r, 100)); movingState = await api('/runtime'); }
  const handAfter = await expose(sampleHand.id);
  check('flange motion changes real camera pixels and exposure world matrix', handAfter.meta.sha256 !== handPhoto.meta.sha256 && handAfter.meta.cameraWorld.matrix.some((v, i) => Math.abs(v - handPhoto.meta.cameraWorld.matrix[i]) > 1e-5));
  await page.locator(`#tree [data-device="${sampleFixed.id}"]`).click();
  await page.locator('#capture-camera-now').click(); await page.waitForSelector('#camera-image');
  await page.screenshot({ path: path.join(output, 'sample-ui.png'), fullPage: true });
  // Lease loss must fail instead of serving an old cached frame as a new photo.
  await page.close(); await new Promise(r => setTimeout(r, 4400));
  const disconnected = await fetch(base + `/api/v1/cameras/${sampleFixed.id}/captures`, { method: 'POST' });
  check('closed renderer returns 503 instead of a stale success', disconnected.status === 503 && (await disconnected.json()).code === 'RendererUnavailable');
  check('no unhandled browser errors', errors.length === 0);
  await writeFile(path.join(output, 'results.json'), JSON.stringify({ checks, errors, bounds, expected, firstFrame: first.meta, csharp: client.stdout }, null, 2));
  console.log(`${checks.length} camera/geometry checks passed.`);
} catch (e) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }).catch(() => {}); throw e; }
finally { await browser.close(); }
