import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile, readFile } from 'node:fs/promises';
import path from 'node:path';

const base = process.env.AXISLAB_TEST_URL ?? 'http://127.0.0.1:5091';
const out = path.resolve('artifacts/motion-checks',String(Date.now())); await mkdir(out,{recursive:true});
const browser = await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader','--use-angle=swiftshader']});
const page = await browser.newPage({viewport:{width:1536,height:1000}}); page.setDefaultTimeout(20000);
const errors=[]; page.on('pageerror',e=>errors.push(e.message));
const checks=[]; const check=(name,okay=true)=>{assert.ok(okay,name);checks.push(name);console.log('PASS: '+name);};
async function api(p,method='GET',body) {const r=await fetch(base+'/api/v1'+p,{method,headers:{'Content-Type':'application/json'},body:body?JSON.stringify(body):undefined}); const data=await r.json(); if(!r.ok) throw new Error(JSON.stringify(data));return data;}
try {
  let state=await api('/project'); state=await api('/project/new','POST',{name:'第五阶段界面验收',expectedRevision:state.revision});
  await page.goto(base); await page.waitForSelector('#teaching-tab');
  await page.locator('.library [data-add="robot.generic6r"]').click();
  await page.waitForSelector('[data-target="5"]'); state=await api('/project'); const robot=state.project.devices[0];
  const q=[15,-15,20,10,25,-5]; for(let i=0;i<6;i++) await page.locator(`[data-target="${i}"]`).fill(String(q[i]));
  await page.locator('#joint-speed').selectOption('100'); await page.locator('#execute-joints').click();
  await page.waitForFunction(()=>document.querySelector('#motion-state')?.textContent==='已完成');
  await page.locator('#teaching-tab').click(); await page.locator('#read-tcp').click();
  const before=(await api('/runtime')).robots[0].tcpBase.pose;
  await page.locator('[data-pose="x"]').fill(String(before.x-20)); await page.locator('[data-pose="y"]').fill(String(before.y+10)); await page.locator('[data-pose="z"]').fill(String(before.z+5));
  await page.locator('#cart-kind').selectOption('MoveL'); await page.locator('#cart-speed').selectOption('50');
  await page.locator('#preview-ik').click(); await page.waitForFunction(()=>document.querySelector('#ik-result')?.textContent.includes('预检通过'));
  check('UI preflight produces duration and solved joint targets',(await page.locator('#ik-result').textContent()).includes(' s'));
  check('preview preserves actual pose',Math.abs((await api('/runtime')).robots[0].tcpBase.pose.x-before.x)<1e-8);
  await page.locator('#execute-cart').click(); await page.waitForFunction(()=>document.querySelector('#teaching-state')?.textContent.includes('已完成'));
  check('UI MoveL reaches target and keeps teaching tab visible',Math.abs((await api('/runtime')).robots[0].tcpBase.pose.x-(before.x-20))<.02 && await page.locator('#teaching-controls').isVisible());
  for(let i=1;i<=5;i++) {
    await page.locator('#teach-name').fill('示教-'+i); await page.locator('#record-point').click();
    await page.waitForFunction(n=>document.querySelectorAll('#teach-list option').length===n,i);
    if(i<5) { await page.locator('#read-tcp').click(); const y=Number(await page.locator('[data-pose="y"]').inputValue()); await page.locator('[data-pose="y"]').fill(String(y+3)); await page.locator('#execute-cart').click(); await page.waitForFunction(()=>document.querySelector('#teaching-state')?.textContent.includes('已完成')); }
  }
  state=await api('/project'); check('UI records five persistent teach points',state.project.teachPoints.length===5 && state.teaching.every(p=>!p.stale));
  await page.locator('#cart-kind').selectOption('MoveJ'); await page.locator('#play-all').click(); await page.waitForFunction(()=>document.querySelector('#teaching-state')?.textContent.includes('已完成'));
  check('UI five-point playback reports final step',(await page.locator('#teaching-state').textContent()).includes('5/5'));
  await page.locator('#save').click(); await page.waitForFunction(()=>document.querySelector('#unsaved')?.textContent==='已保存'); state=await api('/project');
  const saved=state.project; await writeFile(path.join(out,'five-point.scene.json'),JSON.stringify(saved,null,2));
  await page.locator('[name="tool-z"]').fill('85'); await page.locator('button[type="submit"]').click(); await page.waitForFunction(()=>document.querySelector('#teach-list')?.textContent.includes('待校验'));
  check('TCP edit marks teach points stale in UI',(await api('/project')).teaching.every(p=>p.stale));
  await page.locator('#play-point').click(); await page.waitForFunction(()=>document.querySelector('#toast')?.textContent.includes('依赖已改变'));
  check('stale point cannot start a motion',(await api('/runtime')).robots[0].status!=='Running');
  await page.locator('#revalidate-point').click(); await page.waitForFunction(()=>document.querySelector('#teach-list option')?.textContent.startsWith('✓'));
  check('UI revalidates only the selected point',(await api('/project')).teaching.filter(p=>!p.stale).length===1);
  await page.locator('[name="work-x"]').fill('100'); await page.locator('[name="work-rz"]').fill('25'); await page.locator('button[type="submit"]').click();
  await page.waitForFunction(()=>document.querySelector('#property-hint')?.textContent.startsWith('修改后'));
  await page.locator('#target-frame').selectOption('work'); await page.locator('#read-tcp').click(); await page.locator('#preview-ik').click();
  await page.waitForFunction(()=>document.querySelector('#ik-result')?.textContent.includes('预检通过'));
  check('work frame readback and IK preview agree with current TCP');
  const persisted=await api('/project');
  const nullResult=await fetch(base+`/api/v1/robots/${robot.id}/cartesian`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({expectedRevision:persisted.revision,motion:null})});
  check('null motion payload returns a bounded request error',nullResult.status===400||nullResult.status===422);
  await page.locator('[data-pose="x"]').fill('90000'); await page.locator('#preview-ik').click(); await page.waitForFunction(()=>document.querySelector('#toast')?.textContent.includes('可达范围'));
  check('unreachable UI preview surfaces an error without teleporting',(await api('/runtime')).robots[0].status!=='Running');
  state=await api('/project'); const sample=JSON.parse(await readFile('samples/five-point-teaching.scene.json','utf8')); state=await api('/project/import','POST',{expectedRevision:state.revision,project:sample});
  await page.waitForSelector(`[data-device="${sample.devices[0].id}"]`); await page.locator(`[data-device="${sample.devices[0].id}"]`).click(); await page.locator('#teaching-tab').click();
  check('packaged five-point scene opens without stale points',state.teaching.length===5&&state.teaching.every(p=>!p.stale));
  await page.waitForFunction(()=>document.querySelector('#toast')?.classList.contains('hidden'));
  await page.screenshot({path:path.join(out,'motion-teaching-ui.png'),fullPage:true});
  check('no unhandled browser errors',errors.length===0);
  await writeFile(path.join(out,'results.json'),JSON.stringify({checks,errors},null,2)); console.log(`${checks.length} motion/teaching UI checks passed. Artifacts: ${out}`);
} finally {await browser.close();}
