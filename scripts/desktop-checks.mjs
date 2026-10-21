import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { once } from 'node:events';
import { createServer, createConnection } from 'node:net';
import { readFile, mkdir, writeFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const executable = path.resolve(process.env.AXISLAB_DESKTOP_EXE ?? 'dist/AxisLab-0.6.0/AxisLab.exe');
const expectSelfContained = !process.env.AXISLAB_DESKTOP_EXE || process.env.AXISLAB_EXPECT_SELF_CONTAINED === '1';
const home = path.resolve('artifacts/desktop-checks', String(Date.now()));
await mkdir(home, { recursive: true });
const exec = promisify(execFile);
const temporary = createServer();
await new Promise(resolve => temporary.listen(0, '127.0.0.1', resolve));
const debugPort = temporary.address().port;
await new Promise(resolve => temporary.close(resolve));
const app = spawn(executable, [], { cwd: home, windowsHide: true, env: { ...process.env,
  ...(expectSelfContained ? { DOTNET_ROOT: path.join(home, 'no-installed-dotnet'),
    DOTNET_ROOT_X64: path.join(home, 'no-installed-dotnet'), DOTNET_MULTILEVEL_LOOKUP: '0' } : {}),
  AXISLAB_DESKTOP_HOME: home, AXISLAB_MODBUS_PORT: "0", AXISLAB_LISTEN_ADDRESS: "127.0.0.1",
  AXISLAB_TEST_BROWSER_ARGUMENTS: `--remote-debugging-port=${debugPort} --enable-unsafe-swiftshader --use-angle=swiftshader`,
} });
const exited = once(app, 'exit');
let browser;
let base;
const checks = [];
const check = (name, okay = true) => { assert.ok(okay, name); checks.push(name); console.log(`PASS: ${name}`); };
async function waitFor(probe, description, timeout = 45000) {
  const deadline = Date.now() + timeout;
  let lastError;
  while (Date.now() < deadline) {
    const result = await probe().catch(error => { lastError = error.message; return null; });
    if (result) return result;
    if (app.exitCode !== null) throw new Error('Desktop exited early with code ' + app.exitCode);
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error('Timeout: ' + description + (lastError ? '\n' + lastError : ''));
}
async function windowAction(action, destination) {
  const result = await exec('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    path.resolve('scripts/desktop-window.ps1'), '-ProcessId', String(app.pid), '-Action', action,
    ...(destination ? ['-Destination', destination] : [])],
  { windowsHide: true, encoding: 'utf8' });
  return JSON.parse(result.stdout.trim());
}
try {
  const ready = await waitFor(async () => {
    const log = await readFile(path.join(home, 'desktop.log'), 'utf8');
    return log.match(/DesktopReady (http:\/\/127\.0\.0\.1:\d+)/)?.[1];
  }, 'desktop ready');
  base = ready;
  console.log(`Desktop URL: ${base}; CDP: http://127.0.0.1:${debugPort}`);
  check('desktop starts its own embedded server on a dynamic loopback port', base !== 'http://127.0.0.1:5080');
  check('native application has a real Windows window', (await windowAction('State')).handle > 0);
  const health = await (await fetch(base + '/api/health')).json();
  check('embedded service is healthy', health.status === 'ok');
  if (expectSelfContained) {
    const config = JSON.parse(await readFile(path.join(path.dirname(executable), 'AxisLab.runtimeconfig.json'), 'utf8'));
    check('self-contained package declares included runtimes', !config.runtimeOptions.frameworks && config.runtimeOptions.includedFrameworks?.length >= 3);
  }
  await waitFor(async () => (await fetch(`http://127.0.0.1:${debugPort}/json/version`)).ok, 'WebView debugging');
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`, { noDefaults: true });
  const page = await waitFor(async () => browser.contexts().flatMap(c => c.pages()).find(p => p.url().startsWith(base)), 'WebView page');
  page.setDefaultTimeout(15000);
  await page.waitForFunction(() => document.querySelector('#logs')?.textContent.includes('场景编辑器就绪'));
  check('WebView renders scene editor inside the application', await page.locator('#viewport canvas').count() === 1);
  const second = spawn(executable, [], { cwd: home, windowsHide: true, env: { ...process.env, AXISLAB_DESKTOP_HOME: home } });
  const [secondCode] = await Promise.race([once(second, 'exit'), new Promise((_, reject) => setTimeout(() => reject(new Error('Second instance did not exit')), 10000))]);
  check('second launch activates existing window instead of another service', secondCode === 0 && app.exitCode === null);
  await page.locator('.library [data-add="camera.pinhole"]').click();
  await page.waitForFunction(() => document.querySelectorAll('#tree .tree-row').length === 1);
  await page.locator('#project-name').fill('桌面版验收工程');
  await page.locator('#save').click();
  await page.waitForFunction(() => document.querySelector('#unsaved')?.textContent === '已保存');
  check('project saves in desktop user-data location', (await readdir(path.join(home, 'Projects'))).some(f => f.endsWith('.scene.json')));
  await page.locator('[name="name"]').fill('未应用的属性修改');
  const beforeClose = await (await fetch(base + '/api/v1/project')).json();
  check('native dirty check includes form drafts with a clean core', !beforeClose.dirty && await page.evaluate(() => window.axisLabHasUnsavedChanges()));
  await windowAction('Close');
  const rejected = await waitFor(async () => (await windowAction('DeclineClose')).found, 'unsaved native close confirmation', 15000);
  check('unsaved close can be cancelled in the native dialog', rejected && app.exitCode === null);
  await page.locator('button[type="submit"]').click();
  await page.waitForFunction(() => document.querySelector('#property-hint')?.textContent.includes('修改后点击'));
  await page.locator('#save').click();
  await page.waitForFunction(() => document.querySelector('#unsaved')?.textContent === '已保存');
  await page.locator('.library [data-add="robot.generic6r"]').click();
  await page.waitForSelector('[data-target="0"]');
  await page.locator('[name="upperArm"]').fill('420');
  await page.locator('#property-form button[type="submit"]').click();
  await page.waitForFunction(() => document.querySelector('#tcp-readout')?.textContent.includes('665.00'));
  check('desktop geometry editor updates authoritative TCP', (await (await fetch(base + '/api/v1/runtime')).json()).robots[0].tcpBase.pose.z === 665);
  const camera = (await (await fetch(base + '/api/v1/devices')).json()).find(d => d.typeId === 'camera.pinhole');
  await page.locator(`#tree [data-device="${camera.id}"]`).click();
  await page.waitForFunction(() => document.querySelector('#renderer-state')?.textContent === '相机成像就绪');
  await page.locator('#aim-camera').click();
  await page.waitForFunction(() => document.querySelector('#logs')?.textContent.includes('朝向机械臂'));
  await page.locator('#capture-camera-now').click();
  await page.waitForSelector('#camera-image');
  await page.waitForFunction(() => !document.querySelector('#capture-camera-now')?.disabled);
  const frameText = await page.locator('.frame-details').textContent();
  const frameId = frameText.match(/Frame ([0-9a-f-]+)/)[1];
  const metadata = await (await fetch(`${base}/api/v1/cameras/${camera.id}/frames/${frameId}`)).json();
  const photo = Buffer.from(await (await fetch(base + metadata.imageUrl)).arrayBuffer());
  check('native WebView camera renders a real PNG and HTTP serves its frame', photo.readUInt32BE(16) === 640 && metadata.robots[0].tcpBase.pose.z === 665);
  await writeFile(path.join(home, 'desktop-camera.png'), photo);
  const pngClick = page.locator('#save-camera-image').click();
  const savedPng = path.join(home, 'native-export.png');
  await waitFor(async () => (await windowAction('SaveExport', savedPng)).found, 'native PNG save dialog', 15000);
  await pngClick;
  const pngOnDisk = await waitFor(() => readFile(savedPng), 'native PNG file');
  check('native Windows save dialog writes the identical PNG', pngOnDisk.equals(photo));
  const jsonClick = page.locator('#save-frame-metadata').click();
  const savedJson = path.join(home, 'native-frame.json');
  await waitFor(async () => (await windowAction('SaveExport', savedJson)).found, 'native metadata save dialog', 15000);
  await jsonClick;
  const jsonOnDisk = JSON.parse(await waitFor(() => readFile(savedJson, 'utf8'), 'native metadata file'));
  check('native JSON export preserves frame identity', jsonOnDisk.frameId === metadata.frameId);
  await page.screenshot({ path: path.join(home, 'desktop-camera-ui.png') });
  const client = await exec('dotnet', [path.resolve('samples/CSharpClient/bin/Release/net10.0/AxisLab.CaptureClient.dll'), base, camera.id, path.join(home, 'CSharpCaptures')], { windowsHide: true, timeout: 30000 });
  check('C# connects to desktop dynamic port and downloads verified PNG', client.stdout.includes('SHA256'));
  await page.locator(`#tree [data-device="${(await (await fetch(base + '/api/v1/runtime')).json()).robots[0].deviceId}"]`).click();
  await page.locator('[data-target="0"]').fill('90');
  await page.locator('#joint-speed').selectOption('25');
  await page.locator('#execute-joints').click();
  await page.waitForFunction(() => document.querySelector('#motion-state')?.textContent === '运动中');
  check('desktop joint controls start real core motion', (await (await fetch(base + '/api/v1/runtime')).json()).robots[0].status === 'Running');
  await windowAction('Close');
  await waitFor(async () => (await windowAction('DeclineClose')).found, 'moving close confirmation', 15000);
  const frozen = (await (await fetch(base + '/api/v1/runtime')).json()).robots[0];
  check('attempting desktop close stops motion before confirmation', frozen.status === 'Cancelled');
  await new Promise(resolve => setTimeout(resolve, 300));
  check('cancelled close keeps the robot stopped', (await (await fetch(base + '/api/v1/runtime')).json()).robots[0].joints[0] === frozen.joints[0]);
  await page.locator('.library [data-add="robot.generic3r"]').click();
  await page.waitForFunction(() => document.querySelectorAll('[data-target]').length === 3);
  check('desktop offers a real three-axis robot with three joint controls');
  const devices = await (await fetch(base + '/api/v1/devices')).json(); const arm3 = devices.find(d => d.typeId === 'robot.generic3r');
  const communication = await (await fetch(base + '/api/v1/communication')).json();
  check('desktop starts a Modbus listener and reports its actual port', communication.listening && communication.modbusPort > 0);
  const modbusCli = path.resolve('samples/ModbusClient/bin/Release/net10.0/AxisLab.ModbusClient.dll');
  const movement = await exec('dotnet', [modbusCli, base, 'move', String(arm3.deviceNumber), '15,-10,20', '50'], { windowsHide: true, timeout: 30000 });
  check('independent C# controls desktop three-axis robot via Modbus', movement.stdout.includes('MoveJ completed'));
  await page.locator('#teaching-tab').click(); await page.locator('#read-tcp').click();
  await page.locator('#preview-ik').click(); await page.waitForFunction(()=>document.querySelector('#ik-result')?.textContent.includes('预检通过'));
  check('native motion panel solves three-axis Cartesian targets');
  await page.locator('#teach-name').fill('桌面示教点'); await page.locator('#record-point').click(); await page.waitForFunction(()=>document.querySelectorAll('#teach-list option').length===1);
  const teachState=await (await fetch(base+'/api/v1/project')).json(); const teachPoint=teachState.project.teachPoints[0];
  check('native teaching records a persistent point',teachPoint.name==='桌面示教点' && !teachState.teaching[0].stale);
  const teachMove = await exec('dotnet',[modbusCli,base,'teach',String(arm3.deviceNumber),String(teachPoint.number),'50'],{windowsHide:true,timeout:30000});
  check('C# executes a desktop teach point over Modbus',teachMove.stdout.includes('teach completed'));
  await page.screenshot({path:path.join(home,'desktop-teaching-ui.png')});
  const mbCapture = await exec('dotnet', [modbusCli, base, 'capture', String(camera.deviceNumber), path.join(home, 'ModbusCaptures')], { windowsHide: true, timeout: 30000 });
  check('desktop camera is triggered over Modbus and downloaded over HTTP', mbCapture.stdout.includes('SHA256='));
  await page.locator('#external-connect').click();
  await page.waitForFunction(port => document.querySelector('#dialog')?.textContent.includes(String(port)), communication.modbusPort);
  check('desktop connection dialog displays the actual Modbus port', (await page.locator('#dialog').textContent()).includes(String(communication.modbusPort)));
  await page.locator('#dialog-close').click();
  await page.locator('#program-tab').click();
  check('native desktop loads the local Blockly workspace',await page.locator('.blocklySvg').count()>0);
  await page.locator('#program-demo').click();await page.waitForFunction(()=>document.querySelector('#program-name')?.value==='运动拍照循环');
  await page.locator('#program-save').click();await page.waitForFunction(()=>document.querySelector('#unsaved')?.textContent==='已保存');
  await page.locator('#program-run').click();await waitFor(async()=>{const s=await(await fetch(base+'/api/v1/program/status')).json();return s.status==='Completed'&&s.variables['成功次数']===3;},'native Blockly capture loop');
  check('native block program completes real robot and camera loop');
  const programCli=path.resolve('samples/ProgramClient/bin/Release/net10.0/AxisLab.ProgramClient.dll');
  const programResult=await exec('dotnet',[programCli,base,'run'],{windowsHide:true,timeout:30000});
  check('independent C# runs a saved desktop block program',programResult.stdout.includes('Program Completed'));
  await page.screenshot({path:path.join(home,'desktop-blockly-ui.png')});
  const currentProject=await(await fetch(base+'/api/v1/project')).json();
  const waitProgram={version:1,name:'退出停止程序验收',workspace:{blocks:{languageVersion:0,blocks:[{type:'ax_start',id:'desktop-start',inputs:{BODY:{block:{type:'ax_wait',id:'desktop-wait',inputs:{SECONDS:{block:{type:'ax_number',id:'desktop-seconds',fields:{NUM:300}}}}}}}}]}}};
  const put=await fetch(base+'/api/v1/program',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:currentProject.revision,program:waitProgram})});const nextProject=await put.json();
  const runResponse=await fetch(base+'/api/v1/program/run',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:nextProject.revision})});check('desktop can run a program wait without robot motion',runResponse.ok);
  await windowAction('Close');await waitFor(async()=>(await windowAction('DeclineClose')).found,'program close confirmation');
  check('native close cancels a waiting program before dirty confirmation',(await(await fetch(base+'/api/v1/program/status')).json()).status==='Cancelled');
  await waitFor(async()=>!await page.locator('#save').isDisabled(),'scene unlock after program cancellation');
  await page.locator('#save').click();
  await page.waitForFunction(() => document.querySelector('#unsaved')?.textContent === '已保存');
  const webgl = await page.locator('#viewport canvas').evaluate(el => {
    const context = el.getContext('webgl2');
    return !!context && !context.isContextLost() && context.drawingBufferWidth > 0;
  });
  check('embedded WebView has working WebGL', webgl);
  await page.screenshot({ path: path.join(home, 'desktop-webview.png') });
  await windowAction('Close');
  const [exitCode] = await Promise.race([exited, new Promise((_, reject) => setTimeout(() => reject(new Error('Desktop did not exit')), 15000))]);
  check('clean close exits the native application normally', exitCode === 0);
  const available = await fetch(base + '/api/health', { signal: AbortSignal.timeout(1500) }).then(() => true, () => false);
  check('closing application stops embedded HTTP service', !available);
  const modbusClosed = await new Promise(resolve => {
    const socket = createConnection(communication.modbusPort, '127.0.0.1'); socket.on('connect', () => { socket.destroy(); resolve(false); }); socket.on('error', () => resolve(true));
  });
  check('closing application also stops its Modbus listener', modbusClosed);
  const log = await readFile(path.join(home, 'desktop.log'), 'utf8');
  check('desktop shutdown is recorded', log.includes('DesktopStopped'));
  await writeFile(path.join(home, 'results.json'), JSON.stringify({ executable, base, checks }, null, 2));
  console.log(`${checks.length} desktop checks passed. Artifacts: ${home}`);
} catch (error) {
  console.error(await readFile(path.join(home, 'desktop.log'), 'utf8').catch(() => 'No desktop log'));
  throw error;
} finally {
  if (app.exitCode === null) { app.kill(); await exited.catch(() => {}); }
  if (browser?.isConnected()) await browser.close().catch(() => {});
}



