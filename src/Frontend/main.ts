import { icon, escape } from './icons';
import { kind, typeNames, type Device, type Snapshot, type Project, type Pose, type RuntimeSnapshot, type JointDefinition, type RobotGeometry } from './types';
import { Viewport } from './viewport';
import { RobotPanel } from './robot-panel';
import { TeachingPanel } from './teaching-panel';
import { ProgramPanel } from './program-panel';
import { CameraPanel } from './camera-panel';
import { startCaptureWorker } from './capture-worker';

const $ = <T extends HTMLElement = HTMLElement>(selector: string) => document.querySelector<T>(selector)!;
let snapshot: Snapshot;
let selected: string | null = null;
let viewport: Viewport;
let robotPanel: RobotPanel;
let teachingPanel: TeachingPanel;
let programPanel: ProgramPanel;
let activeTab = 'logs';
let cameraPanel: CameraPanel;
let runtime: RuntimeSnapshot;
let activeRobotId: string | null = null;
let formDirty = false;
let pending = false;
let toastTimer: ReturnType<typeof setTimeout>;
const logs: { time: string; message: string; error: boolean }[] = [];

class ApiError extends Error {
  constructor(message: string, public code: string) { super(message); }
}
async function api<T>(path: string, method = 'GET', data?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method, headers: data === undefined ? {} : { 'Content-Type': 'application/json' },
    body: data === undefined ? undefined : JSON.stringify(data), signal: AbortSignal.timeout(10000),
  });
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw new ApiError(body.message ?? `请求失败 (${response.status})`, body.code ?? 'RequestFailed');
  }
  return response.json() as Promise<T>;
}
function log(message: string, error = false) {
  logs.unshift({ time: new Date().toLocaleTimeString('zh-CN', { hour12: false }), message, error });
  logs.splice(80);
  if ($('#logs')) $('#logs').innerHTML = logs.map(l => `<div class="log-entry"><span class="log-time">${l.time}</span><span class="log-badge ${l.error ? 'error' : ''}">${l.error ? '错误' : '信息'}</span><span class="log-message">${escape(l.message)}</span></div>`).join('');
}
function toast(message: string, error = false) {
  clearTimeout(toastTimer);
  const element = $('#toast'); element.textContent = message;
  element.className = `toast${error ? ' error' : ''}`;
  toastTimer = setTimeout(() => element.classList.add('hidden'), 4500);
}
async function perform(action: () => Promise<void>) {
  if (pending) return;
  pending = true;
  try { await action(); }
  catch (error) {
    const message = error instanceof Error ? error.message : '操作失败';
    toast(message, true); log(message, true);
    if (error instanceof ApiError && error.code === 'RevisionConflict') {
      accept(await api<Snapshot>('/v1/project')); toast('已加载服务端最新工程，请重新编辑。', true);
    } else if (snapshot) viewport.setDevices(snapshot.project.devices, runtime);
  } finally { pending = false; }
}

function dialog<T>(title: string, content: string, actions: { label: string; value: T; primary?: boolean; danger?: boolean }[]): Promise<T | null> {
  const element = $('#dialog') as HTMLDialogElement;
  if (element.open) return Promise.resolve(null);
  element.innerHTML = `<div class="dialog-head"><h2>${escape(title)}</h2><button class="tool-button" id="dialog-close" aria-label="关闭">${icon('close')}</button></div><div class="dialog-body">${content}</div><div class="dialog-actions">${actions.map((a, i) => `<button class="btn ${a.primary ? 'primary' : a.danger ? 'danger' : 'outline'}" data-choice="${i}">${escape(a.label)}</button>`).join('')}</div>`;
  return new Promise(resolve => {
    let resolved = false;
    const finish = (result: T | null) => { if (resolved) return; resolved = true; element.close(); resolve(result); };
    element.querySelectorAll<HTMLButtonElement>('[data-choice]').forEach(button => {
      button.onclick = () => finish(actions[Number(button.dataset.choice)].value);
    });
    $('#dialog-close').onclick = () => finish(null);
    element.oncancel = event => { event.preventDefault(); finish(null); };
    element.onclose = () => finish(null);
    element.showModal();
  });
}
async function discardForm() {
  if (!formDirty) return true;
  return await dialog('未应用的属性修改', '<p>属性面板有尚未应用的修改。是否放弃这些修改？</p>',
    [{ label: '继续编辑', value: false }, { label: '放弃修改', value: true, primary: true }]) === true;
}
async function discardProject() {
  if (!snapshot.dirty && !formDirty && !programPanel.isDirty && $('#project-name') instanceof HTMLInputElement &&
    ($('#project-name') as HTMLInputElement).value === snapshot.project.name) return true;
  return await dialog('当前工程尚未保存', '<p>继续会替换当前工作区。请先保存需要保留的工程，或确认放弃当前修改。</p>',
    [{ label: '取消', value: false }, { label: '放弃并继续', value: true, primary: true }]) === true;
}

function accept(value: Snapshot, preferId: string | null = selected) {
  if (snapshot && value.sessionId === snapshot.sessionId && value.revision < snapshot.revision) return;
  if (snapshot && snapshot.sessionId !== value.sessionId) { activeRobotId = null; robotPanel.clear(); }
  const nameInput = $('#project-name') as HTMLInputElement;
  const preserveDraftName = snapshot && snapshot.sessionId === value.sessionId && nameInput.value !== snapshot.project.name;
  const draftName = nameInput.value;
  snapshot = value;
  programPanel.setSnapshot(value);
  selected = value.project.devices.some(d => d.id === preferId) ? preferId : null;
  formDirty = false;
  nameInput.value = preserveDraftName ? draftName : value.project.name;
  $('#unsaved').textContent = nameInput.value !== value.project.name ? '● 名称未保存' :
    value.dirty ? '● 未保存' : value.hasSavedFile ? '已保存' : '新建工程';
  $('#device-count').textContent = String(value.project.devices.length).padStart(2, '0');
  $('#status-count').textContent = `${value.project.devices.length} 个对象`;
  $('#revision').textContent = `REV ${value.revision.toString().padStart(3, '0')}`;
  $('#empty-scene').classList.toggle('hidden', value.project.devices.length > 0);
  viewport.setDevices(value.project.devices, value.runtime);
  viewport.select(selected);
  cameraPanel.setDevices(value.project.devices, value.sessionId, selected);
  renderTree(); renderProperties();
  updateRuntime(value.runtime);
  if(activeTab==='program')setTab('program');
  else {
    if (value.project.devices.find(d => d.id === selected)?.robot) setTab(activeTab === 'teaching' ? 'teaching' : 'motion');
    if (value.project.devices.find(d => d.id === selected)?.typeId === 'camera.pinhole') setTab('camera');
  }
}
async function choose(id: string | null) {
  if (selected === id || !await discardForm()) return;
  selected = id; formDirty = false; viewport.select(id); renderTree(); renderProperties();
  cameraPanel.setDevices(snapshot.project.devices, snapshot.sessionId, id);
  updateRuntime(runtime);
  if(activeTab!=='program') {
    if (snapshot.project.devices.find(d => d.id === selected)?.robot) setTab('motion');
    if (snapshot.project.devices.find(d => d.id === selected)?.typeId === 'camera.pinhole') setTab('camera');
  }
}

function setTab(tab: 'motion' | 'logs' | 'camera' | 'teaching' | 'program') {
  activeTab = tab;
  $('#program-controls').classList.toggle('hidden',tab!=='program');
  $('#program-tab').classList.toggle('active',tab==='program');
  document.querySelector('.center')!.classList.toggle('program-mode',tab==='program');
  if(tab==='program')programPanel.resize();
  $('#teaching-controls').classList.toggle('hidden', tab !== 'teaching');
  $('#teaching-tab').classList.toggle('active', tab === 'teaching');
  $('#robot-controls').classList.toggle('hidden', tab !== 'motion');
  $('#logs').classList.toggle('hidden', tab !== 'logs');
  $('#robot-tab').classList.toggle('active', tab === 'motion');
  $('#log-tab').classList.toggle('active', tab === 'logs');
  $('#camera-controls').classList.toggle('hidden', tab !== 'camera');
  $('#camera-tab').classList.toggle('active', tab === 'camera');
}
function updateRuntime(value: RuntimeSnapshot) {
  runtime = value; snapshot.runtime=value; viewport.updateRuntime(value);
  const selectedDevice = snapshot.project.devices.find(d => d.id === selected);
  if (selectedDevice?.robot) activeRobotId = selectedDevice.id;
  const robot = snapshot.project.devices.find(d => d.id === activeRobotId);
  const state = value.robots.find(r => r.deviceId === activeRobotId);
  if (robot && state) { robotPanel.show(robot, state); teachingPanel.show(robot, state, snapshot); }
  else { activeRobotId = null; robotPanel.clear(); teachingPanel.clear(); }
  if(value.programRunId){
    document.querySelectorAll<HTMLButtonElement|HTMLInputElement|HTMLSelectElement>('#robot-controls input,#robot-controls select,#robot-controls button,#teaching-controls button').forEach(c=>c.disabled=true);
    for(const id of ['stop-robot','teach-stop']){const stop=document.getElementById(id) as HTMLButtonElement|null;if(stop)stop.disabled=false;}
  }
  const currentFrame = value.devices.find(d => d.deviceId === selected)?.world.pose;
  const readout = document.querySelector('#world-pose');
  if (readout && currentFrame) readout.textContent = `X ${currentFrame.x.toFixed(2)}  Y ${currentFrame.y.toFixed(2)}  Z ${currentFrame.z.toFixed(2)} mm`;
  const moving = !!value.programRunId || value.robots.some(r => r.status === 'Running' || r.controlOwnerId);
  document.querySelectorAll<HTMLButtonElement | HTMLInputElement | HTMLSelectElement>(
    '.library button,[data-add],#new,#save,#open,#import,#export,#project-name,#property-form input,#property-form select,#property-form button,#aim-camera'
  ).forEach(control => control.disabled = moving);
  ($('#stop-all') as HTMLButtonElement).disabled = !moving;
}
async function moveRobot(id: string, joints: number[], speed: number) {
  if (formDirty) { toast('请先应用或放弃对象属性修改，再执行运动。', true); return; }
  await perform(async () => {
    accept(await api<Snapshot>(`/v1/robots/${id}/joints`, 'POST', { joints, speedPercent: speed, expectedRevision: snapshot.revision }));
    log('已提交关节运动 · ' + joints.map(j => j.toFixed(1)).join(', ') + ' deg');
  });
}
async function stopRobot(id: string) {
  try { accept(await api<Snapshot>(`/v1/robots/${id}/stop`, 'POST')); log('机械臂运动已停止'); }
  catch (error) { toast(error instanceof Error ? error.message : '停止失败', true); }
}
async function saveProject() {
  if(formDirty)throw new Error('请先应用属性面板中的修改，再保存工程。');
  await programPanel.apply();
  accept(await api<Snapshot>('/v1/project/save','POST',{expectedRevision:snapshot.revision,name:($('#project-name') as HTMLInputElement).value}));
  log(`工程“${snapshot.project.name}”已保存到本地`);toast('工程和程序已保存');
}
function renderTree() {
  const devices = snapshot.project.devices;
  if (!devices.length) { $('#tree').innerHTML = '<div class="tree-empty">场景还是空的<br>从上方设备库添加第一个对象</div>'; return; }
  const row = (device: Device, depth: number): string =>
    `<button class="tree-row ${selected === device.id ? 'selected' : ''} ${device.visible ? '' : 'hidden-device'}" data-device="${device.id}" style="padding-left:${9 + depth * 15}px" title="${escape(device.name)}" aria-pressed="${selected === device.id}">${icon(kind(device.typeId))}<span class="device-name">${escape(device.name)}</span><span class="device-no">${String(device.deviceNumber).padStart(2, '0')}</span></button>` +
    devices.filter(d => d.parentId === device.id).map(d => row(d, depth + 1)).join('');
  $('#tree').innerHTML = devices.filter(d => !d.parentId).map(d => row(d, 0)).join('');
  $('#tree').querySelectorAll<HTMLButtonElement>('[data-device]').forEach(button => {
    button.onclick = () => void choose(button.dataset.device!);
  });
}

const numberField = (label: string, name: string, value: number, extra = '') =>
  `<label class="field"><span>${label}</span><input type="number" name="${name}" value="${value}" ${extra.includes('step=') ? '' : 'step="any"'} ${extra} required></label>`;
const geometryFields: [keyof RobotGeometry, string, number][] = [
  ['baseHeight', '基座高度 / J1', 135], ['shoulderHeight', '肩部高度 / J1→J2', 110],
  ['upperArm', '上臂 / J2→J3', 300], ['forearm', '前臂 / J3→J4', 280],
  ['wrist1', '腕段 / J4→J5', 80], ['wrist2', '腕段 / J5→J6', 70], ['flangeLength', 'J6→法兰', 55],
  ['joint1Diameter', 'J1 直径', 110], ['joint2Diameter', 'J2 直径', 110], ['joint3Diameter', 'J3 直径', 110],
  ['joint4Diameter', 'J4 直径', 74], ['joint5Diameter', 'J5 直径', 74], ['joint6Diameter', 'J6 直径', 74],
];
function renderProperties() {
  const device = snapshot.project.devices.find(d => d.id === selected);
  if (!device) {
    $('#properties-content').innerHTML = `<div class="props-empty">${icon('move')}<h2>选择一个场景对象</h2><p>点击场景或左侧对象树<br>查看和编辑设备属性</p></div><div class="props-status"><strong>阶段 7B · 标定采样</strong>支持三轴/六轴、相机拍照与 Modbus TCP 外部控制。</div>`;
    return;
  }
  const descendants = new Set([device.id]);
  let added = true;
  while (added) { added = false; for (const d of snapshot.project.devices) if (d.parentId && descendants.has(d.parentId) && !descendants.has(d.id)) { descendants.add(d.id); added = true; } }
  const parentOptions = snapshot.project.devices.filter(d => !descendants.has(d.id));
  const t = device.transform;
  const vector = (labels: string[], keys: (keyof Pose)[], unit: string) => `<div class="section-caption">${unit === 'mm' ? '位置' : '旋转'}<span>${unit} · ${device.parentId ? '父对象局部坐标' : '世界坐标'}</span></div><div class="vector-fields">${keys.map((k, i) => `<label class="vector-field"><span>${labels[i]}</span><input name="${k}" type="number" value="${Number(t[k].toFixed(3))}" min="-1000000" max="1000000" step="any" required></label>`).join('')}</div>`;
  const p = device.parameters;
  const frames = device.robot ? `<div class="field-section"><div class="section-caption">工具 TCP · 相对法兰 F</div><div class="frame-fields">${(['x','y','z','rx','ry','rz'] as const).map(k=>numberField(k.toUpperCase()+(k.length===1?' mm':' deg'), 'tool-'+k, device.robot!.toolTransform[k], 'min="-10000" max="10000"')).join('')}</div><div class="section-caption">工件坐标 W · 相对基坐标</div><div class="frame-fields">${(['x','y','z','rx','ry','rz'] as const).map(k=>numberField(k.toUpperCase()+(k.length===1?' mm':' deg'), 'work-'+k, device.robot!.workTransform[k], 'min="-1000000" max="1000000"')).join('')}</div><small>修改后已有示教点须重新校验。</small></div>` : '';
  const currentGeometryFields = geometryFields.filter(([key]) => device.typeId !== 'robot.generic3r' || !['wrist1', 'wrist2', 'joint4Diameter', 'joint5Diameter', 'joint6Diameter'].includes(key));
  const labelForGeometry = (key: keyof RobotGeometry, label: string) => device.typeId !== 'robot.generic3r' ? label :
    key === 'forearm' ? '前臂 / J3→末端' : key === 'flangeLength' ? '末端→法兰' : label;
  const geometry = device.robot ? `<div class="field-section"><div class="section-caption">机械臂尺寸<span>mm</span></div><div class="field-pair">${currentGeometryFields.map(([key, label]) => numberField(labelForGeometry(key, label), key, device.robot!.geometry[key], key.includes('Diameter') ? 'min="10" max="500"' : `min="${key === 'baseHeight' ? 40 : 5}" max="3000"`)).join('')}</div><button class="btn outline" type="button" id="reset-geometry">恢复默认尺寸</button><p class="properties-note">${device.typeId === 'robot.generic3r' ? '通用 3R：底座 Z、肩 Y、肘 Y；前臂末端连接法兰，腕段参数不参与三轴模型。' : ''}连杆长度同步参与正运动学；直径用于外观。修改后重新校核工具与相机位姿，现有标定数据需要重做。</p></div>` : '';
  const params = device.typeId === 'camera.pinhole' ? `<div class="field-section"><div class="section-caption">相机内参<span>针孔模型</span></div><div class="field-pair">${numberField('图像宽度 / px', 'width', p.width, 'min="64" max="4096" step="1"')}${numberField('图像高度 / px', 'height', p.height, 'min="64" max="4096" step="1"')}${numberField('焦距 fx / px', 'fx', p.fx, 'min="0.001" max="100000"')}${numberField('焦距 fy / px', 'fy', p.fy, 'min="0.001" max="100000"')}${numberField('主点 cx / px', 'cx', p.cx, 'min="0"')}${numberField('主点 cy / px', 'cy', p.cy, 'min="0"')}</div><label class="field"><small>绿色视锥显示成像方向。修改分辨率时请同步调整主点与焦距。</small></label></div>` :
    (device.typeId === 'geometry.box' || device.typeId.startsWith('calibration.')) ? `<div class="field-section"><div class="section-caption">几何尺寸<span>mm · 标定图案位于局部 Z=0</span></div>${numberField('X 方向', 'sizeX', p.sizeX, 'min="0.001" max="10000"')}${numberField('Y 方向', 'sizeY', p.sizeY, 'min="0.001" max="10000"')}${numberField('Z 方向', 'sizeZ', p.sizeZ, 'min="0.001" max="10000"')}</div>` : '';
  $('#properties-content').innerHTML = `<div class="property-heading">${icon(kind(device.typeId))}<div><strong>${typeNames[device.typeId]}</strong><small>${escape(device.typeId)}</small></div></div>
    <form id="property-form"><div class="field-section"><label class="field"><span>对象名称</span><input name="name" value="${escape(device.name)}" maxlength="80" required></label><div class="id-row"><span>设备编号</span><span class="id-value">#${device.deviceNumber.toString().padStart(4, '0')}</span></div><div class="id-row"><span>稳定 ID</span><span class="id-value" title="${device.id}">${device.id.slice(0, 18)}…</span></div></div>
    <div class="field-section">${vector(['X', 'Y', 'Z'], ['x', 'y', 'z'], 'mm')}<div style="height:8px"></div>${vector(['Rx', 'Ry', 'Rz'], ['rx', 'ry', 'rz'], 'deg')}</div>
    <div class="field-section"><div class="section-caption">挂载关系${icon('link')}</div><label class="field"><span>父对象</span><select name="parentId"><option value="">世界坐标（独立安装）</option>${parentOptions.map(d => `<option value="${d.id}" ${device.parentId === d.id ? 'selected' : ''}>${escape(d.name)}</option>`).join('')}</select></label><label class="field"><span>挂载坐标</span><select name="attachmentFrameId"><option value="base">父对象基坐标</option><option value="flange">机器人法兰 F</option><option value="tool">机器人 TCP / 工具 T</option></select><small>更改挂载时保留局部数值；法兰和工具挂载随关节运动。</small></label></div>
    ${params}${geometry}${frames}<div class="field-section"><label class="checkbox-label"><input type="checkbox" name="visible" ${device.visible ? 'checked' : ''}>在场景中显示</label><span class="properties-note">配置版本 ${device.configurationRevision}</span></div>
    <div class="props-actions"><button type="submit" class="btn primary">${icon('check')}应用属性</button><button type="button" class="btn outline" id="duplicate" title="仅复制此设备">${icon('copy')}复制</button><button type="button" class="btn danger" id="delete" aria-label="删除设备">${icon('trash')}</button></div></form>
    <p class="properties-note" id="property-hint">修改后点击“应用属性”。${device.typeId === 'camera.pinhole' ? '使用下方“相机画面”拍照、预览和保存。' : device.typeId === 'robot.generic6r' ? '使用下方“关节点动”操作六轴。支持路径预检和基础碰撞检测。' : ''}</p><div class="runtime-pose"><strong>对象原点 · 世界坐标</strong><span id="world-pose"></span></div>`;
  const form = $('#property-form') as HTMLFormElement;
  const mountFrame = form.querySelector<HTMLSelectElement>('[name="attachmentFrameId"]')!;
  mountFrame.value = device.attachmentFrameId;
  const parentSelect = form.querySelector<HTMLSelectElement>('[name="parentId"]')!;
  const updateMountOptions = () => {
    const robotParent = snapshot.project.devices.find(d => d.id === parentSelect.value)?.robot;
    [...mountFrame.options].forEach(option => option.disabled = option.value !== 'base' && !robotParent);
    if (!robotParent) mountFrame.value = 'base';
  };
  updateMountOptions(); parentSelect.addEventListener('change', updateMountOptions);
  form.oninput = () => { formDirty = true; $('#property-hint').textContent = '有未应用的修改，点击“应用属性”提交到核心服务。'; };
  form.onchange = form.oninput;
  const resetGeometry = form.querySelector<HTMLButtonElement>('#reset-geometry');
  if (resetGeometry) resetGeometry.onclick = () => {
    for (const [key, , value] of currentGeometryFields) form.querySelector<HTMLInputElement>(`[name="${key}"]`)!.value = String(value);
    form.dispatchEvent(new Event('input'));
  };
  form.onsubmit = event => {
    event.preventDefault();
    if (!form.reportValidity()) return;
    void perform(async () => {
      const values = new FormData(form);
      const updated = structuredClone(device);
      updated.name = String(values.get('name')).trim();
      updated.parentId = String(values.get('parentId')) || null;
      updated.attachmentFrameId = String(values.get('attachmentFrameId')) as Device['attachmentFrameId'];
      updated.visible = values.has('visible');
      for (const key of ['x', 'y', 'z', 'rx', 'ry', 'rz'] as const) updated.transform[key] = Number(values.get(key));
      for (const key of ['width', 'height', 'fx', 'fy', 'cx', 'cy', 'sizeX', 'sizeY', 'sizeZ'] as const) {
        if (values.has(key)) updated.parameters[key] = Number(values.get(key));
      }
      if (updated.robot) for (const [key] of currentGeometryFields) updated.robot.geometry[key] = Number(values.get(key));
      if (updated.robot) for (const key of ['x','y','z','rx','ry','rz'] as const) {
        updated.robot.toolTransform[key] = Number(values.get('tool-'+key));
        updated.robot.workTransform[key] = Number(values.get('work-'+key));
      }
      accept(await api<Snapshot>(`/v1/devices/${device.id}`, 'PUT', { expectedRevision: snapshot.revision, device: updated }));
      log(`已更新 ${updated.name} 的属性`); toast('属性已应用');
    });
  };
  $('#duplicate').onclick = () => void perform(async () => {
    if (!await discardForm()) return;
    const response = await api<Snapshot>(`/v1/devices/${device.id}/duplicate`, 'POST', { expectedRevision: snapshot.revision });
    accept(response, response.project.devices.at(-1)!.id); log(`已复制 ${device.name}（仅复制当前对象）`);
  });
  $('#delete').onclick = () => void perform(async () => {
    const children = descendants.size - 1;
    const okay = await dialog('删除场景对象', `<p>删除“${escape(device.name)}”${children ? `及其下挂载的 ${children} 个子对象` : ''}？这项修改会在保存工程后保留。</p>`,
      [{ label: '取消', value: false }, { label: children ? '连同子对象删除' : '删除', value: true, danger: true }]);
    if (!okay) return;
    accept(await api<Snapshot>(`/v1/devices/${device.id}?expectedRevision=${snapshot.revision}&cascade=${children > 0}`, 'DELETE'), null);
    log(`已删除 ${device.name}${children ? `及 ${children} 个子对象` : ''}`);
  });
}

async function add(typeId: string) {
  if (!await discardForm()) return;
  const response = await api<Snapshot>('/v1/devices', 'POST', { typeId, expectedRevision: snapshot.revision });
  const device = response.project.devices.at(-1)!;
  accept(response, device.id); log(`已添加 ${device.name} · #${device.deviceNumber.toString().padStart(4, '0')}`);
}
function download(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
  const a = document.createElement('a'); a.href = url; a.download = name; a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

function mount() {
  $('#app').innerHTML = `<div class="shell"><header class="topbar"><div class="brand"><span class="brand-mark">A</span><span class="brand-name">AxisLab</span><span class="version">v0.6</span><span class="brand-caption">机械臂 · 视觉仿真</span></div><div class="project-heading">${icon('file')}<input id="project-name" value="未命名工程" maxlength="80" aria-label="工程名称"><span class="unsaved" id="unsaved"></span></div><div class="connection" id="connection"><span class="dot"></span><span>本地核心已连接</span></div></header>
    <nav class="toolbar" aria-label="工程工具栏"><button class="btn" id="new">${icon('plus')}新建</button><button class="btn" id="open">${icon('folder')}打开</button><button class="btn primary" id="save">${icon('save')}保存工程</button><span class="separator"></span><button class="btn" id="import">${icon('upload')}导入</button><button class="btn" id="export">${icon('download')}导出 JSON</button><button class="btn danger" id="stop-all" disabled>停止运动</button><span class="toolbar-spacer"></span><button class="btn outline" id="external-connect">外部连接</button><span class="stage-chip">阶段 7B · 标定采样</span><span class="separator"></span><span class="mode"><span class="dot"></span>编辑模式</span><button class="tool-button" id="help" title="操作说明" aria-label="操作说明">${icon('info')}</button></nav>
    <main class="workspace"><aside class="left-panel"><div class="panel-title">设备库<span class="label">DEVICE LIBRARY</span></div><div class="library">${[['robot.generic6r', '通用 6R · 关节点动 / FK'], ['robot.generic3r', '通用 3R · 底座 / 肩 / 肘'], ['camera.pinhole', '可独立安装或挂载'], ['geometry.box', '工作台 / 工件 / 障碍物'], ['calibration.board', '3×3 黑圆 / 固定平面'], ['calibration.marker', '单圆靶标 / TCP 零偏移挂载']].map(([type, description]) => `<button class="device-card ${kind(type)}" data-add="${type}" title="添加${typeNames[type]}"><span class="device-symbol">${icon(kind(type))}</span><span><strong>${typeNames[type]}</strong><small>${description}</small></span>${icon('plus')}</button>`).join('')}</div><div class="panel-title tree-header"><span>${icon('layers')} 场景对象</span><span id="device-count" class="tree-count">00</span></div><div class="tree" id="tree"></div><div class="left-foot">自由搭建你的仿真场景<br>设备编号与 ID 随工程稳定保存</div></aside>
    <section class="center"><div class="viewport" id="viewport"><div class="viewport-breadcrumb">${icon('layers')}<strong>三维场景</strong><span>/</span><span>透视视图</span></div><div class="view-settings"><button class="tool-button active" id="translate" title="平移选中对象 (W)" aria-label="平移对象">${icon('move')}</button><button class="tool-button" id="rotate" title="旋转选中对象 (E)" aria-label="旋转对象">${icon('rotate')}</button><span class="separator"></span><button class="tool-button active" id="grid" title="切换网格" aria-label="切换网格">${icon('grid')}</button><button class="tool-button" id="focus" title="聚焦选中对象 (F)" aria-label="聚焦对象">${icon('eye')}</button><button class="tool-button" id="home" title="复位视角" aria-label="复位视角">${icon('home')}</button></div><div class="empty-scene" id="empty-scene">${icon('layers')}<h1>从一个空白场景开始</h1><p>添加机械臂、相机或几何体<br>搭建属于你的第一个仿真工作单元</p><div class="empty-actions"><button class="btn primary" data-add="robot.generic6r">${icon('plus')}添加机械臂</button><button class="btn outline" data-add="camera.pinhole">${icon('camera')}添加相机</button></div><div class="empty-foot">也可以从左侧设备库添加对象</div></div><div class="viewport-hint">左键旋转视角 · 右键平移 · 滚轮缩放 · 拖动坐标轴编辑对象</div><div class="axis-labels"><span>X</span><span>Y</span><span>Z</span></div><div class="viewport-meta"><span>GRID 100 mm</span><span id="fps">— FPS</span></div></div><div class="bottom-panel"><div class="bottom-tabs"><button class="bottom-tab-button" id="robot-tab">关节点动</button><button class="bottom-tab-button active" id="log-tab">事件日志</button><button class="bottom-tab-button" id="teaching-tab">运动与示教</button><button class="bottom-tab-button" id="camera-tab">相机画面</button><button class="bottom-tab-button" id="program-tab">积木编程</button><small id="revision">REV 001</small></div><div class="logs" id="logs"></div><div id="robot-controls" class="hidden"></div><div id="camera-controls" class="hidden"></div><div id="teaching-controls" class="hidden"></div><div id="program-controls" class="hidden"></div></div></section>
    <aside class="properties"><div class="panel-title">对象属性<span class="label">PROPERTIES</span></div><div id="properties-content"></div></aside></main><footer class="statusbar"><span><span class="dot"></span>场景编辑就绪</span><span id="status-count">0 个对象</span><span>长度 mm · 角度 deg</span><span class="right">Modbus TCP / HTTP · 支持三轴与六轴</span></footer></div><dialog id="dialog"></dialog><div class="toast hidden" id="toast" role="status"></div><input type="file" id="import-file" accept=".json,application/json">`;
  viewport = new Viewport($('#viewport'), id => void choose(id), (id, pose) => void perform(async () => {
    if (!await discardForm()) { viewport.setDevices(snapshot.project.devices, runtime); return; }
    const current = snapshot.project.devices.find(d => d.id === id);
    if (!current) return;
    accept(await api<Snapshot>(`/v1/devices/${id}`, 'PUT', {
      expectedRevision: snapshot.revision, device: { ...current, transform: pose },
    }));
    log(`已调整 ${current.name} 的${$('#translate').classList.contains('active') ? '位置' : '旋转'}`);
  }));
  document.querySelectorAll<HTMLButtonElement>('[data-add]').forEach(button => {
    button.onclick = () => void perform(() => add(button.dataset.add!));
  });
  $('#new').onclick = () => void perform(async () => {
    if (!await discardProject()) return;
    accept(await api<Snapshot>('/v1/project/new', 'POST', { expectedRevision: snapshot.revision, name: '未命名工程' }), null);
    viewport.home(); log('已创建空白工程');
  });
  $('#save').onclick = () => void perform(async () => {
    await saveProject();
  });
  $('#project-name').oninput = () => { $('#unsaved').textContent = '● 名称未保存'; };
  $('#open').onclick = () => void perform(async () => {
    const files = await api<{ id: string; name: string; devices: number; modified: string }[]>('/v1/projects');
    const content = files.length ? files.map(f => `<button class="project-file" data-open="${f.id}">${icon('folder')}<span><strong>${escape(f.name)}</strong><small>${f.devices} 个对象 · ${new Date(f.modified).toLocaleString('zh-CN')}</small></span>${icon('chevron')}</button>`).join('') : '<p>还没有保存的工程。编辑场景后点击“保存工程”，或导入已有 JSON。</p>';
    // Close the file picker before opening the unsaved-changes dialog.
    const choice = dialog('打开本地工程', content, [{ label: '取消', value: '' }]);
    let fileId = '';
    $('#dialog').querySelectorAll<HTMLButtonElement>('[data-open]').forEach(button => {
      button.onclick = () => { fileId = button.dataset.open!; ($('#dialog') as HTMLDialogElement).close(); };
    });
    await choice;
    if (!fileId || !await discardProject()) return;
    accept(await api<Snapshot>('/v1/project/open', 'POST', { fileId, expectedRevision: snapshot.revision }), null);
    viewport.home(); log(`已打开工程“${snapshot.project.name}”`);
  });
  $('#export').onclick = () => {
    if (formDirty) { toast('请先应用属性修改，再导出。', true); return; }
    if(programPanel.isDirty){toast('请先保存程序，或在积木面板单独导出草稿。',true);return;}
    const project = { ...snapshot.project, name: ($('#project-name') as HTMLInputElement).value.trim() || snapshot.project.name };
    download(`${project.name.replace(/[<>:"/\\|?*]/g, '_')}.scene.json`, JSON.stringify(project, null, 2));
    log('已导出工程 JSON（包含当前关节位置，图像缓存不写入工程）');
  };
  $('#import').onclick = () => ($('#import-file') as HTMLInputElement).click();
  $('#import-file').onchange = () => void perform(async () => {
    const input = $('#import-file') as HTMLInputElement;
    const file = input.files?.[0]; input.value = '';
    if (!file) return;
    if (file.size > 2 * 1024 * 1024) throw new Error('工程文件不能超过 2 MiB。');
    const document = JSON.parse(await file.text()) as Project;
    if (!await discardProject()) return;
    accept(await api<Snapshot>('/v1/project/import', 'POST', { project: document, expectedRevision: snapshot.revision }), null);
    viewport.home(); log(`已导入工程“${snapshot.project.name}”，请保存到本地`);
  });
  $('#home').onclick = () => viewport.home(); $('#focus').onclick = () => viewport.focus();
  $('#robot-tab').onclick = () => setTab('motion');
  $('#program-tab').onclick = () => setTab('program');
  $('#teaching-tab').onclick = () => setTab('teaching');
  $('#log-tab').onclick = () => setTab('logs');
  $('#external-connect').onclick = () => void perform(async () => {
    const state = await api<{ bindAddress: string; modbusPort: number; listening: boolean; error: string | null; unitId: number; activeClients: number; sessionToken: string }>('/v1/communication');
    const action = await dialog('外部连接 · Modbus TCP / HTTP', `<p>HTTP：<strong>${escape(location.origin)}</strong></p><p>Modbus TCP：<strong>${escape(location.hostname)}:${state.modbusPort}</strong> · Unit ID ${state.unitId}<br>${state.listening ? '正在监听' : escape(state.error ?? '尚未启动')} · ${state.activeClients} 个客户端</p><p>项目会话：<code>${state.sessionToken}</code></p><p>先读取设备列表与状态，再申请机器人控制权。外部控制期间每秒递增心跳，5 秒无更新自动停止。本地“停止运动”可撤销外部控制。</p><p>相机由 Modbus 触发，按返回的帧令牌通过 HTTP 下载 PNG。程序目录 Samples/ModbusClient 提供 C# 示例。</p><p>其他电脑连接时使用本机局域网 IP，并按随包《外部连接快速开始》启用局域网监听。寄存器地址采用零基偏移、Float32 高字在前。</p>`, [
      { label: '关闭', value: false }, { label: '导出寄存器映射', value: true, primary: true },
    ]);
    if (action) download('AxisLab-Modbus-v1.json', JSON.stringify(await api('/v1/communication/map'), null, 2));
  });
  $('#camera-tab').onclick = () => setTab('camera');
  $('#stop-all').onclick = async () => {
    await programPanel.stop();
    for (const robot of runtime.robots.filter(r => r.status === 'Running' || r.controlOwnerId)) await stopRobot(robot.deviceId);
  };
  const mode = (value: 'translate' | 'rotate') => {
    viewport.setMode(value); $('#translate').classList.toggle('active', value === 'translate'); $('#rotate').classList.toggle('active', value === 'rotate');
  };
  $('#translate').onclick = () => mode('translate'); $('#rotate').onclick = () => mode('rotate');
  $('#grid').onclick = () => $('#grid').classList.toggle('active', viewport.toggleGrid());
  $('#help').onclick = () => void dialog('场景编辑操作说明', '<p>从设备库添加对象，在左侧对象树或三维场景中选中它。</p><p>右侧属性支持安装位置、旋转、父对象和设备参数，修改后点击“应用属性”。也可拖动三维坐标轴调整对象。</p><p>W 切换平移，E 切换旋转，F 聚焦选中对象。左键旋转视角、右键平移、滚轮缩放。</p><p>保存工程写入本机项目目录，导出/导入用于交换 JSON 文件。选择机器人后可在下方执行关节点动、回零和停止；相机可挂载到法兰或 TCP。可编辑机械臂尺寸；相机画面支持拍照、PNG 保存和帧信息导出。HTTP 取图需要保持本软件界面打开。顶部“外部连接”可查看 HTTP / Modbus 端口及下载协议映射。外部控制时需每秒更新心跳，5 秒未更新自动停止。运动前进行关节限位与基础碰撞预检；“运动与示教”支持位姿 MoveJ、直线 MoveL 和示教点回放。工具与工件坐标在右侧属性设置。</p>', [{ label: '知道了', value: true, primary: true }]);
  document.addEventListener('keydown', e => {
    if(activeTab==='program')return;
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement || ($('#dialog') as HTMLDialogElement).open) return;
    if (e.key.toLowerCase() === 'w') mode('translate');
    if (e.key.toLowerCase() === 'e') mode('rotate');
    if (e.key.toLowerCase() === 'f') viewport.focus();
  });
  const hasUnsavedChanges = () => !!(snapshot?.dirty || formDirty || programPanel?.isDirty ||
    ($('#project-name') as HTMLInputElement).value !== snapshot?.project.name);
  (window as Window & { axisLabHasUnsavedChanges?: () => boolean }).axisLabHasUnsavedChanges = hasUnsavedChanges;
  window.addEventListener('beforeunload', e => { if (hasUnsavedChanges()) e.preventDefault(); });
}

try {
  mount();
  const models = await api<{ modelId: string; joints: JointDefinition[] }[]>('/v1/robot-models');
  robotPanel = new RobotPanel($('#robot-controls'), Object.fromEntries(models.map(m => [m.modelId, m.joints])), (id, joints, speed) => void moveRobot(id, joints, speed), id => void stopRobot(id));
  teachingPanel = new TeachingPanel($('#teaching-controls'), api, action => {
    if (formDirty) { toast('请先应用对象属性修改，再执行运动或示教。', true); return; }
    void perform(action);
  }, value => accept(value), id => void stopRobot(id));
  programPanel=new ProgramPanel($('#program-controls'),api,value=>accept(value),action=>{
    if(formDirty){toast('请先应用对象属性修改。',true);return;}
    void perform(action);
  },(message,error)=>{toast(message,error);log(message,error);},saveProject,async()=>await dialog('程序草稿尚未保存','<p>是否放弃当前积木草稿？可先导出程序保留。</p>',[{label:'继续编辑',value:false},{label:'放弃草稿',value:true,primary:true}])===true);
  cameraPanel = new CameraPanel($('#camera-controls'), id => void perform(async () => {
    if (!await discardForm()) return;
    const camera = snapshot.project.devices.find(d => d.id === id);
    if (!camera) return;
    const pose = viewport.aimCamera(id);
    accept(await api<Snapshot>(`/v1/devices/${id}`, 'PUT', {
      expectedRevision: snapshot.revision, device: { ...camera, transform: pose },
    }));
    setTab('camera'); log(`已将 ${camera.name} 朝向机械臂`);
  }), (message, error) => { toast(message, error); log(message, error); });
  accept(await api<Snapshot>('/v1/project'));
  startCaptureWorker(work => viewport.capture(work), state => void cameraPanel.receive(state),
    message => cameraPanel.rendererStatus(message));
  log('C# 核心服务已连接 · 工程格式 v5（兼容读取 v1 / v2 / v3 / v4）'); log('场景编辑器就绪，可从设备库添加第一个对象');
  let polling = false;
  setInterval(async () => {
    if (polling || pending) return;
    polling = true;
    try {
      const requestSession = snapshot.sessionId;
      const next = await api<RuntimeSnapshot>('/v1/runtime');
      if (requestSession !== snapshot.sessionId || next.projectRevision < snapshot.revision) return;
      const finished = runtime?.robots.some(r => r.status === 'Running') && !next.robots.some(r => r.status === 'Running');
      const programChanged=next.programRunId!==runtime?.programRunId;
      if (next.sessionId !== snapshot.sessionId || (!formDirty && (finished || programChanged || next.projectRevision !== snapshot.revision))) {
        accept(await api<Snapshot>('/v1/project'));
      } else updateRuntime(next);
    } catch { /* Health indicator is updated separately, without flooding event logs. */ }
    finally { polling = false; }
  }, 100);
  setInterval(async () => {
    try {
      await api('/health'); $('#connection').classList.remove('offline');
      $('#connection').innerHTML = '<span class="dot"></span><span>本地核心已连接</span>';
    } catch {
      $('#connection').classList.add('offline'); $('#connection').innerHTML = '<span class="dot"></span><span>核心连接中断</span>';
    }
  }, 10000);
} catch (error) {
  $('#app').innerHTML = `<div class="fatal"><h1>AxisLab 启动失败</h1><p>${escape(error instanceof Error ? error.message : String(error))}</p><p>请确认本地服务正常运行，浏览器支持 WebGL 2，然后刷新页面重试。</p><button class="btn primary" onclick="location.reload()">重新连接</button></div>`;
}




