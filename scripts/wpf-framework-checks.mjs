import { spawn, execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { createServer } from 'node:net';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const output=path.resolve('artifacts/wpf-framework-checks',String(Date.now()));await mkdir(output,{recursive:true});
const probe=createServer();await new Promise(r=>probe.listen(0,'127.0.0.1',r));const port=probe.address().port;await new Promise(r=>probe.close(r));
const base=`http://127.0.0.1:${port}`;
const host=spawn('dotnet',[path.resolve('src/Host/bin/Release/net10.0/AxisLab.Host.dll')],{cwd:path.resolve('src/Host'),windowsHide:true,env:{...process.env,AXISLAB_URL:base,AXISLAB_DATA:path.join(output,'projects'),AXISLAB_MODBUS_PORT:'0',Logging__LogLevel__Default:'Warning'}});
let log='';host.stdout.on('data',b=>log+=b);host.stderr.on('data',b=>log+=b);let browser;
async function wait(fn){const end=Date.now()+20000;while(Date.now()<end){if(await fn().catch(()=>false))return;await new Promise(r=>setTimeout(r,100));}throw new Error('Host timeout');}
const exec=promisify(execFile);
try{
 await wait(async()=>(await fetch(base+'/api/health')).ok);
 const project=JSON.parse(await readFile('samples/vm-bridge.scene.json','utf8'));project.schemaVersion=5;project.name='原 WPF 框架联调';
 let chain;for(let i=149;i>=0;i--)chain={type:'ax_log',id:'framework-log-'+i,fields:{TEXT:'检查 '+i},...(chain?{next:{block:chain}}:{})};
 project.program={version:1,name:'深度兼容检查',workspace:{blocks:{languageVersion:0,blocks:[{type:'ax_start',id:'framework-start',inputs:{BODY:{block:chain}}}]}}};
 const current=await(await fetch(base+'/api/v1/project')).json();
 const imported=await fetch(base+'/api/v1/project/import',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:current.revision,project})});assert.ok(imported.ok);const snapshot=await imported.json();
 const save=await fetch(base+'/api/v1/project/save',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:snapshot.revision,name:project.name})});assert.ok(save.ok);
 browser=await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader','--use-angle=swiftshader']});const page=await browser.newPage();await page.goto(base);await page.waitForSelector('#viewport canvas');await new Promise(r=>setTimeout(r,1200));
 const config=path.join(output,'settings.json'),report=path.join(output,'results.json');
 await writeFile(config,JSON.stringify({url:base,directory:output,solution:'C:\\Program Files\\VisionMaster4.3.0\\Development\\V4.x\\Samples\\C#\\PlatformSDKSampleCS\\CircleFind\\CircleFind.sol',flow:'Flow1',source:'Image Source1'}));
 const exe=path.resolve(process.env.AXISLAB_WPF_EXE??'src/WpfDemo/bin/Release/net9.0-windows/AxisLab.WpfDemo.exe');
 await exec(exe,['--self-test',config,report],{windowsHide:true,cwd:output,timeout:120000,env:{...process.env,AXISLAB_WPF_DATA:path.join(output,'data'),...(!process.env.AXISLAB_WPF_EXE?{AXISLAB_VM_BRIDGE:path.resolve('src/WpfDemo/VmBridge/bin/Release/net48/AxisLab.VmBridge.exe')}:{DOTNET_ROOT:path.join(output,'no-dotnet'),DOTNET_ROOT_X64:path.join(output,'no-dotnet'),DOTNET_MULTILEVEL_LOOKUP:'0'})}});
 const result=JSON.parse(await readFile(report,'utf8'));assert.equal(result.success,true);
 for(const name of result.checks)console.log('PASS: '+name);console.log(`${result.checks.length} original-framework checks passed. Artifacts: ${output}`);
}catch(error){console.error(log.slice(-1000));console.error('Artifacts: '+output);console.error(await readFile(path.join(output,'results.json'),'utf8').catch(()=>''));throw error;}
finally{if(browser)await browser.close();host.kill();await writeFile(path.join(output,'host.log'),log);}
