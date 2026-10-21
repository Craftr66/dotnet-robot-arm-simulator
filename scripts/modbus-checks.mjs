import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { once } from 'node:events';
import { createConnection, createServer } from 'node:net';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const out = path.resolve('artifacts/modbus-checks', String(Date.now())); await mkdir(out, { recursive: true });
const free = createServer(); await new Promise(r => free.listen(0, '127.0.0.1', r)); const port = free.address().port; await new Promise(r => free.close(r));
const base = `http://127.0.0.1:${port}`;
const host = spawn('dotnet', [path.resolve('src/Host/bin/Release/net10.0/AxisLab.Host.dll')], { cwd: path.resolve('src/Host'), windowsHide: true,
  env: { ...process.env, AXISLAB_URL: base, AXISLAB_DATA: path.join(out, 'Projects'), AXISLAB_MODBUS_PORT: '0', AXISLAB_LISTEN_ADDRESS: '127.0.0.1', Logging__LogLevel__Default: 'Warning' } });
let log = ''; host.stdout.on('data', d => log += d); host.stderr.on('data', d => log += d);
const checks = [], errors = [], connections = []; let browser;
const check = (name, okay = true) => { assert.ok(okay, name); checks.push(name); console.log('PASS: ' + name); };
const delay = ms => new Promise(r => setTimeout(r, ms));
async function wait(probe, timeout = 20000) { const end = Date.now() + timeout; while (Date.now() < end) { const r = await probe().catch(() => null); if (r) return r; await delay(100); } throw new Error('Wait timed out'); }
async function api(endpoint, method = 'GET', data) { const r = await fetch(base + '/api/v1' + endpoint, { method, headers: data ? { 'Content-Type': 'application/json' } : {}, body: data ? JSON.stringify(data) : undefined }); const v = await r.json(); if (!r.ok) throw new Error(JSON.stringify(v)); return v; }
const u32 = (a, i) => (a[i] * 65536 + a[i + 1]) >>> 0;
const pair = n => [n >>> 16, n & 65535];
const float = (a, i) => { const b = Buffer.alloc(4); b.writeUInt32BE(u32(a, i)); return b.readFloatBE(); };
const putFloat = (a, i, n) => { const b = Buffer.alloc(4); b.writeFloatBE(n); a[i] = b.readUInt16BE(); a[i + 1] = b.readUInt16BE(2); };
class Client {
  constructor(socket) { this.socket = socket; this.buffer = Buffer.alloc(0); this.tid = 0; socket.on('data', d => this.buffer = Buffer.concat([this.buffer, d])); socket.on('error', () => {}); }
  static async connect(port) { const s = createConnection(port, '127.0.0.1'); await once(s, 'connect'); const c = new Client(s); connections.push(c); return c; }
  packet(pdu, tid = ++this.tid, unit = 1) { const h = Buffer.alloc(7); h.writeUInt16BE(tid); h.writeUInt16BE(pdu.length + 1, 4); h[6] = unit; return Buffer.concat([h, pdu]); }
  async receive() {
    await wait(async () => this.buffer.length >= 7 && this.buffer.length >= 6 + this.buffer.readUInt16BE(4), 6000);
    const len = 6 + this.buffer.readUInt16BE(4); const adu = this.buffer.subarray(0, len); this.buffer = this.buffer.subarray(len); return adu;
  }
  async request(pdu, unit = 1) { const packet = this.packet(pdu, ++this.tid, unit); this.socket.write(packet); const adu = await this.receive(); assert.equal(adu.readUInt16BE(), this.tid); return adu.subarray(7); }
  async read(address = 0, count = 120) { const p = Buffer.alloc(5); p[0] = 3; p.writeUInt16BE(address, 1); p.writeUInt16BE(count, 3); const r = await this.request(p); assert.equal(r[0], 3); return Array.from({ length: count }, (_, i) => r.readUInt16BE(2 + i * 2)); }
  async write(address, values) { const p = Buffer.alloc(6 + values.length * 2); p[0] = 16; p.writeUInt16BE(address, 1); p.writeUInt16BE(values.length, 3); p[5] = values.length * 2; values.forEach((v, i) => p.writeUInt16BE(v, 6 + i * 2)); const r = await this.request(p); assert.equal(r[0], 16); }
  async select(number) { await this.write(200, [number]); }
  async bank(opcode, target = []) { const h = await this.read(); const a = Array(28).fill(0); a[0] = opcode; a[2] = target.length; target.forEach((v, i) => putFloat(a, 4 + i * 2, v)); putFloat(a, 16, 25); a.splice(18, 4, ...h.slice(4, 8)); a.splice(22, 2, ...h.slice(98, 100)); return a; }
  async submit(sequence, bank) { await this.write(202, bank); await this.write(232, pair(sequence)); return this.read(); }
  async finish(sequence) { return wait(async () => { const h = await this.read(); return u32(h, 18) === sequence && h[15] >= 3 ? h : null; }, 18000); }
}
try {
  const status = await wait(async () => { const s = await api('/communication'); return s.listening ? s : null; });
  check('host exposes the actual dynamic Modbus and HTTP ports', status.modbusPort > 0 && status.httpPort === port);
  await writeFile(path.join(out, 'Modbus-v1-map.json'), JSON.stringify(await api('/communication/map'), null, 2));
  browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] });
  const page = await browser.newPage({ viewport: { width: 1536, height: 1050 } }); page.on('pageerror', e => errors.push(e.message));
  await page.goto(base); await page.waitForFunction(() => document.querySelector('#renderer-state')?.textContent === '相机成像就绪');
  await page.locator('.library [data-add="robot.generic3r"]').click(); await page.waitForSelector('[data-target="2"]');
  check('UI creates a three-axis robot with exactly three controls', await page.locator('[data-target]').count() === 3 && await page.locator('[name="joint4Diameter"]').count() === 0);
  let state = await api('/project'); const arm = state.project.devices[0];
  await page.locator('[data-target="0"]').fill('20'); await page.locator('#joint-speed').selectOption('100'); await page.locator('#execute-joints').click();
  await page.waitForFunction(() => document.querySelector('#motion-state')?.textContent === '已完成');
  check('three-axis UI drives actual core motion', Math.abs((await api('/runtime')).robots[0].joints[0] - 20) < 1e-6);
  for (const typeId of ['robot.generic6r', 'camera.pinhole', 'camera.pinhole']) { state = await api('/project'); await api('/devices', 'POST', { typeId, expectedRevision: state.revision }); }
  state = await api('/project'); const six = state.project.devices.find(d => d.typeId === 'robot.generic6r'), cameras = state.project.devices.filter(d => d.typeId === 'camera.pinhole');
  let a = await Client.connect(status.modbusPort), b = await Client.connect(status.modbusPort);
  const pdu = Buffer.from([3, 0, 0, 0, 32]); const fragmented = a.packet(pdu, 90); a.socket.write(fragmented.subarray(0, 3)); await delay(20); a.socket.write(fragmented.subarray(3, 8)); await delay(20); a.socket.write(fragmented.subarray(8));
  const fragmentResult = await a.receive(); check('MBAP parser handles a request split across TCP packets', fragmentResult.readUInt16BE() === 90 && fragmentResult[7] === 3 && fragmentResult.readUInt16BE(9) === 0x4158);
  a.socket.write(Buffer.concat([a.packet(pdu, 91), a.packet(pdu, 92)]));
  const sticky1 = await a.receive(), sticky2 = await a.receive(); check('MBAP parser handles two coalesced requests and echoes transaction IDs', sticky1.readUInt16BE() === 91 && sticky2.readUInt16BE() === 92);
  const unitError = await a.request(pdu, 2); check('wrong Unit ID gets documented exception 0B', unitError[0] === 131 && unitError[1] === 11);
  const unknown = await a.request(Buffer.from([1, 0, 0, 0, 1])); check('unsupported function gets standard exception 01', unknown[0] === 129 && unknown[1] === 1);
  const malformed = await Client.connect(status.modbusPort); const closed = new Promise(resolve => malformed.socket.once('close', resolve)); const invalid = malformed.packet(pdu); invalid[3] = 1; malformed.socket.write(invalid);
  await Promise.race([closed, delay(5000).then(() => { throw new Error('Invalid MBAP not closed'); })]);
  check('invalid MBAP protocol closes only the offending connection');
  await a.select(arm.deviceNumber); await b.select(arm.deviceNumber); const h = await a.read();
  check('network registers identify three axes and Float32 ABCD TCP', h[13] === 3 && Math.abs(float(h, 44) - 415 * Math.cos(Math.PI / 9)) < .001);
  check('first client acquires control over TCP', (await a.submit(1, await a.bank(1)))[14] === 1);
  check('second client cannot take another client control', (await b.submit(1, await b.bank(1)))[16] === 11);
  await page.waitForFunction(() => document.querySelector('#motion-state')?.textContent === '外部控制');
  check('local UI shows external control and disables motion edits', await page.locator('#execute-joints').isDisabled() && await page.locator('#save').isDisabled() && await page.locator('#stop-robot').isEnabled());
  await a.write(230, [0, 1]); const bank = await a.bank(3, [90, 0, 0]); await a.submit(2, bank);
  const original = (await api('/runtime')).robots[0].commandId; await a.submit(2, bank);
  check('network retry same sequence preserves command identity', (await api('/runtime')).robots[0].commandId === original);
  await a.submit(3, await a.bank(4)); check('external stop revokes control', (await a.read())[14] === 0);
  const exec = promisify(execFile); const clientDll = path.resolve('samples/ModbusClient/bin/Release/net10.0/AxisLab.ModbusClient.dll');
  async function cli(...args) { return exec('dotnet', [clientDll, base, ...args], { windowsHide: true, timeout: 90000 }); }
  check('independent C# discovers both robot models through Modbus', (await cli('list')).stdout.includes('axes=3') && (await cli('list')).stdout.includes('axes=6'));
  check('independent C# controls three axes with a heartbeat', (await cli('move', String(arm.deviceNumber), '15,-20,25', '50')).stdout.includes('MoveJ completed'));
  check('independent C# controls six axes', (await cli('move', String(six.deviceNumber), '5,10,15,0,0,0', '100')).stdout.includes('MoveJ completed'));
  const threeState = (await api('/runtime')).robots.find(r=>r.deviceId === arm.id).tcpBase.pose;
  const threePose = [threeState.x-5,threeState.y,threeState.z+5,0,0,0].join(',');
  check('C# opcode7 moves three-axis TCP to a Cartesian position', (await cli('movepose',String(arm.deviceNumber),threePose,'50')).stdout.includes('movepose completed'));
  await cli('move',String(six.deviceNumber),'15,-15,20,10,25,-5','100');
  const sixState = (await api('/runtime')).robots.find(r=>r.deviceId === six.id).tcpBase.pose;
  const sixPose = [sixState.x-10,sixState.y+5,sixState.z+5,sixState.rx,sixState.ry,sixState.rz].join(',');
  check('C# opcode8 completes full-pose six-axis MoveL', (await cli('movel',String(six.deviceNumber),sixPose,'50')).stdout.includes('movel completed'));
  state = await api('/project'); state = await api(`/robots/${arm.id}/teach-points`,'POST',{expectedRevision:state.revision,name:'外部示教点',frame:'base'});
  const point = state.project.teachPoints[0];
  check('C# opcode9 executes persisted teach point number', (await cli('teach',String(arm.deviceNumber),String(point.number),'50')).stdout.includes('teach completed'));
  const rejectClient = await Client.connect(status.modbusPort); await rejectClient.select(six.deviceNumber); await rejectClient.submit(1,await rejectClient.bank(1));
  check('unreachable Cartesian target returns new business error30', (await rejectClient.submit(2,await rejectClient.bank(7,[90000,0,0,0,0,0])))[16] === 30);
  await rejectClient.submit(3,await rejectClient.bank(2)); rejectClient.socket.destroy();
  const cancelledCli = cli('move',String(arm.deviceNumber),'90,0,0','10').then(result=>({code:0,...result}),error=>error);
  await wait(async ()=>(await api('/runtime')).robots.find(r=>r.deviceId === arm.id).status === 'Running');
  await api(`/robots/${arm.id}/stop`,'POST'); const cancelledResult = await cancelledCli;
  await writeFile(path.join(out,'csharp-cancel.json'),JSON.stringify({code:cancelledResult.code,stdout:cancelledResult.stdout,stderr:cancelledResult.stderr},null,2));
  check('C# preserves the cancelled motion result after local stop',cancelledResult.code !== 0 && cancelledResult.stderr.includes('状态=5') && !cancelledResult.stderr.includes('错误=12'));
  // C# examples use independent connections; reconnect probes after the 15s idle window.
  a.socket.destroy(); b.socket.destroy(); a = await Client.connect(status.modbusPort); b = await Client.connect(status.modbusPort);
  await a.select(arm.deviceNumber); await b.select(arm.deviceNumber);
  await a.submit(4, await a.bank(1)); await a.submit(5, await a.bank(3, [-160, 0, 0])); await delay(5300);
  const lostHeartbeat = (await api('/runtime')).robots.find(r => r.deviceId === arm.id);
  check('real five-second heartbeat loss stops intermediate motion', lostHeartbeat.status === 'Cancelled' && lostHeartbeat.controlOwnerId === null && lostHeartbeat.joints[0] > -160);
  await b.submit(2, await b.bank(1)); await b.submit(3, await b.bank(3, [150, 0, 0])); b.socket.destroy();
  await wait(async () => (await api('/runtime')).robots.find(r => r.deviceId === arm.id).status === 'Cancelled');
  check('closing TCP connection immediately stops owned motion');
  await a.select(cameras[0].deviceNumber); const c = await Client.connect(status.modbusPort); await c.select(cameras[1].deviceNumber);
  await Promise.all([a.submit(6, await a.bank(5)), c.submit(1, await c.bank(5))]);
  const [f1, f2] = await Promise.all([a.finish(6), c.finish(1)]);
  const session = f1.slice(4, 8).map(v => v.toString(16).padStart(4, '0')).join('');
  const m1 = await api(`/communication/frames/${session}/${u32(f1, 24)}`), m2 = await api(`/communication/frames/${session}/${u32(f2, 24)}`);
  check('two Modbus camera results map to separate fixed camera frames', m1.cameraId === cameras[0].id && m2.cameraId === cameras[1].id && m1.frameId !== m2.frameId);
  const png = Buffer.from(await (await fetch(base + m1.imageUrl)).arrayBuffer()); await writeFile(path.join(out, 'modbus-camera.png'), png);
  check('mapped HTTP PNG matches frame SHA256', createHash('sha256').update(png).digest('hex').toUpperCase() === m1.sha256);
  check('C# triggers camera over Modbus and downloads verified HTTP PNG', (await cli('capture', String(cameras[0].deviceNumber), path.join(out, 'CSharpCaptures'))).stdout.includes('SHA256='));
  await page.locator('#external-connect').click(); await page.waitForSelector('#dialog[open]');
  check('connection dialog shows actual Modbus port and control guidance', (await page.locator('#dialog').textContent()).includes(String(status.modbusPort)) && (await page.locator('#dialog').textContent()).includes('5 秒'));
  await page.locator('#dialog-close').click(); await page.screenshot({ path: path.join(out, 'three-axis-ui.png'), fullPage: true });
  state = await api('/project'); await api('/project/new', 'POST', { name: '会话失效测试', expectedRevision: state.revision });
  check('project replacement invalidates selected Modbus binding', (await a.read())[29] === 0 && (await a.submit(7, await a.bank(5)))[16] === 2);
  const old = await fetch(base + `/api/v1/communication/frames/${session}/${u32(f1, 24)}`); check('old frame token cannot cross project sessions', old.status === 410);
  check('no unhandled browser errors', errors.length === 0);
  await writeFile(path.join(out, 'results.json'), JSON.stringify({ base, status, checks, errors }, null, 2));
  console.log(`${checks.length} Modbus/3R integration checks passed. Artifacts: ${out}`);
} catch (e) { console.error(log.slice(-3000)); throw e; }
finally { for (const c of connections) c.socket.destroy(); if (browser) await browser.close(); host.kill(); await writeFile(path.join(out, 'host.log'), log); }
