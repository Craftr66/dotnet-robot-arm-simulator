import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createServer } from 'node:net';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const out=path.resolve('artifacts/vision-demo-checks',String(Date.now()));await mkdir(out,{recursive:true});
const probe=createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
const base=`http://127.0.0.1:${port}`;
const host=spawn('dotnet',[path.resolve('src/Host/bin/Release/net10.0/AxisLab.Host.dll')],{cwd:path.resolve('src/Host'),windowsHide:true,env:{...process.env,AXISLAB_URL:base,AXISLAB_DATA:path.join(out,'projects'),AXISLAB_MODBUS_PORT:'0',Logging__LogLevel__Default:'Warning'}});
let hostLog='';host.stdout.on('data',b=>hostLog+=b);host.stderr.on('data',b=>hostLog+=b);
const exec=promisify(execFile);let browser;
async function wait(fn){const end=Date.now()+20000;while(Date.now()<end){if(await fn().catch(()=>false))return;await new Promise(r=>setTimeout(r,100));}throw new Error('Host ready timeout');}
try{
  await wait(async()=>(await fetch(base+'/api/health')).ok);
  const state=await(await fetch(base+'/api/v1/project')).json();
  const project=JSON.parse(await readFile('samples/vm-bridge.scene.json','utf8'));
  const imported=await fetch(base+'/api/v1/project/import',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:state.revision,project})});assert.ok(imported.ok);
  browser=await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader','--use-angle=swiftshader']});
  const page=await browser.newPage();await page.goto(base);await page.waitForSelector('#viewport canvas');
  await new Promise(r=>setTimeout(r,1500));
  const modelChecks=await exec(path.resolve('tests/VisionDemoChecks/bin/Release/net48/AxisLab.VisionDemoChecks.exe'),[base,path.join(out,'model-checks')],{windowsHide:true,timeout:45000});console.log(modelChecks.stdout);
  const settings=path.join(out,'settings.json'),report=path.join(out,'results.json');
  await writeFile(settings,JSON.stringify({url:base,directory:out,solution:process.env.AXISLAB_VM_TEST_SOLUTION??'C:\\Program Files\\VisionMaster4.3.0\\Development\\V4.x\\Samples\\C#\\PlatformSDKSampleCS\\CircleFind\\CircleFind.sol',flow:'Flow1',source:'Image Source1'}));
  const executable=path.resolve(process.env.AXISLAB_VISION_DEMO_EXE??'src/VisionDemo/bin/Release/net48/AxisLab.VisionDemo.exe');
  await exec(executable,['--smoke',settings,report],{cwd:out,windowsHide:true,timeout:65000});
  const result=JSON.parse(await readFile(report,'utf8'));assert.equal(result.success,true);
  for(const check of result.checks)console.log('PASS: '+check);
  console.log(`${result.checks.length} WPF/VM integration checks passed. Artifacts: ${out}`);
}catch(e){console.error(hostLog.slice(-1500));console.error('Artifacts: '+out);const report=await readFile(path.join(out,'results.json'),'utf8').catch(()=>null);if(report)console.error(report);throw e;}
finally{if(browser)await browser.close();host.kill();await writeFile(path.join(out,'host.log'),hostLog);}
