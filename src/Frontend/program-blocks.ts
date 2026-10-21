import * as Blockly from 'blockly/core';
import * as Zh from 'blockly/msg/zh-hans';
import type { Snapshot } from './types';

Blockly.setLocale(Object.fromEntries(Object.entries(Zh).filter(([,value])=>typeof value==='string')) as Record<string,string>);
let context: Snapshot | null = null;
const references:Record<string,Set<string>>={ROBOT:new Set(),CAMERA:new Set(),POINT:new Set()};
type SavedBlock = {type:string;id:string;fields?:Record<string,unknown>;inputs?:Record<string,{block?:SavedBlock}>;next?:{block:SavedBlock};x?:number;y?:number};
const uid=()=>crypto.randomUUID();
export const block = (type:string,fields:Record<string,unknown>={},inputs:Record<string,SavedBlock>={},next?:SavedBlock):SavedBlock => ({type,id:uid(),fields,
  ...(Object.keys(inputs).length?{inputs:Object.fromEntries(Object.entries(inputs).map(([key,b])=>[key,{block:b}]))}:{}),...(next?{next:{block:next}}:{})});
const number=(n:number)=>block('ax_number',{NUM:n});
export function exampleWorkspace(snapshot:Snapshot) {
  const robot=snapshot.project.devices.find(d=>d.robot), camera=snapshot.project.devices.find(d=>d.typeId==='camera.pinhole');
  const point=snapshot.project.teachPoints.find(p=>p.robotId===robot?.id);
  const yes=block('ax_change',{NAME:'成功次数'},{VALUE:number(1)},block('ax_log',{TEXT:'拍照完成，成功次数={成功次数}'}));
  const decision=block('ax_if',{}, {CONDITION:block('ax_capture_ok'),BODY:yes,ELSE:block('ax_log',{TEXT:'本次拍照失败，请检查相机与渲染状态。'})});
  const capture=block('ax_capture',{CAMERA:camera?.id??'',CONTINUE:true,TIMEOUT:15},{},decision);
  const move=point?block('ax_teach',{ROBOT:robot?.id??'',POINT:point.id,KIND:'MoveJ',SPEED:25,TIMEOUT:120},{},capture):
    block('ax_move_j',{ROBOT:robot?.id??'',J1:0,J2:0,J3:0,J4:0,J5:0,J6:0,SPEED:25,TIMEOUT:120},{},capture);
  const repeat=block('ax_repeat',{}, {COUNT:number(3),BODY:move});
  const init=block('ax_set',{NAME:'成功次数'},{VALUE:number(0)},repeat);
  const start=block('ax_start',{}, {BODY:init});start.x=25;start.y=25;
  return {blocks:{languageVersion:0,blocks:[start]}};
}
function options(kind:'robot'|'camera'|'point',field:Blockly.FieldDropdown):[string,string][] {
  const current=field.getValue()??'';
  const robot=field.getSourceBlock()?.getFieldValue('ROBOT');
  const choices:[string,string][] = kind==='point' ? (context?.project.teachPoints??[]).filter(p=>!robot || p.robotId===robot).map(p=>[p.name,p.id]) :
    (context?.project.devices??[]).filter(d=>kind==='robot'?!!d.robot:d.typeId==='camera.pinhole').map(d=>[d.name,d.id]);
  for(const id of references[kind==='robot'?'ROBOT':kind==='camera'?'CAMERA':'POINT'])if(!choices.some(c=>c[1]===id))choices.push(['⚠ 缺失 / 不匹配 '+id.slice(0,8),id]);
  if(current && !choices.some(c=>c[1]===current))choices.push(['⚠ 缺失引用 '+current.slice(0,8),current]);
  return choices.length?choices:[['请选择设备 / 点位','']];
}
class DeviceDropdown extends Blockly.FieldDropdown {
  constructor(readonly kind:'robot'|'camera'|'point'){super(function(){return options(kind,this);});}
  refresh(){const current=this.getValue();this.setOptions(function(){return options((this as DeviceDropdown).kind,this);});this.setValue(current);if(current!==null)this.doValueUpdate_(current);this.forceRerender();}
}
function dropdown(kind:'robot'|'camera'|'point') {return new DeviceDropdown(kind);}
const numeric=(value:number,min=-1000000,max=1000000)=>new Blockly.FieldNumber(value,min,max);
const speed=(b:Blockly.Block)=>b.appendDummyInput().appendField('速度 %').appendField(numeric(25,1,100),'SPEED').appendField('超时 s').appendField(numeric(120,.1,300),'TIMEOUT');
function statement(b:Blockly.Block,colour:number){b.setPreviousStatement(true);b.setNextStatement(true);b.setColour(colour);}
function expression(b:Blockly.Block,output:string,colour=210){b.setOutput(true,output);b.setColour(colour);}
function value(b:Blockly.Block,name:string,label:string,type:string){return b.appendValueInput(name).setCheck(type).appendField(label);}

export function registerProgramBlocks() {
  if(Blockly.Blocks.ax_start)return;
  Blockly.Blocks.ax_start={init(this:Blockly.Block){this.appendDummyInput().appendField('开始程序');this.appendStatementInput('BODY').appendField('执行');this.setColour(160);}};
  Blockly.Blocks.ax_move_j={init(this:Blockly.Block){statement(this,30);this.appendDummyInput().appendField('关节 MoveJ').appendField(dropdown('robot'),'ROBOT');
    for(let row=0;row<2;row++){const line=this.appendDummyInput();for(let i=row*3+1;i<=row*3+3;i++)line.appendField('J'+i).appendField(numeric(0,-360,360),'J'+i);}speed(this);
    this.setTooltip('三轴 J4—J6 必须为零；角度单位 deg。每条运动都会重新预检。');}};
  Blockly.Blocks.ax_move_pose={init(this:Blockly.Block){statement(this,30);this.appendDummyInput().appendField('位姿').appendField(new Blockly.FieldDropdown([['MoveJ','MoveJ'],['MoveL','MoveL']]),'KIND').appendField(dropdown('robot'),'ROBOT');
    this.appendDummyInput().appendField('坐标').appendField(new Blockly.FieldDropdown([['基坐标','base'],['世界','world'],['工件 W','work']]),'FRAME').appendField('仅位置').appendField(new Blockly.FieldCheckbox('FALSE'),'POSITION');
    for(const keys of [['X','Y','Z'],['RX','RY','RZ']]){const line=this.appendDummyInput();for(const key of keys)line.appendField(key).appendField(numeric(key==='X'?400:key==='Z'?500:0),key);}speed(this);}};
  Blockly.Blocks.ax_teach={init(this:Blockly.Block){statement(this,30);this.appendDummyInput().appendField('到示教点').appendField(dropdown('robot'),'ROBOT');this.appendDummyInput().appendField(dropdown('point'),'POINT').appendField(new Blockly.FieldDropdown([['MoveJ','MoveJ'],['MoveL','MoveL']]),'KIND');speed(this);}};
  Blockly.Blocks.ax_capture={init(this:Blockly.Block){statement(this,175);this.appendDummyInput().appendField('拍照并等待').appendField(dropdown('camera'),'CAMERA');this.appendDummyInput().appendField('失败继续').appendField(new Blockly.FieldCheckbox('TRUE'),'CONTINUE').appendField('超时 s').appendField(numeric(15,.1,120),'TIMEOUT');}};
  Blockly.Blocks.ax_wait={init(this:Blockly.Block){statement(this,210);value(this,'SECONDS','等待秒数','Number');}};
  Blockly.Blocks.ax_log={init(this:Blockly.Block){statement(this,210);this.appendDummyInput().appendField('日志').appendField(new Blockly.FieldTextInput('当前计数={计数}'),'TEXT');}};
  Blockly.Blocks.ax_repeat={init(this:Blockly.Block){statement(this,120);value(this,'COUNT','重复次数','Number');this.appendStatementInput('BODY').appendField('执行');}};
  Blockly.Blocks.ax_while={init(this:Blockly.Block){statement(this,120);value(this,'CONDITION','当条件为真时循环','Boolean');this.appendStatementInput('BODY').appendField('执行');}};
  Blockly.Blocks.ax_if={init(this:Blockly.Block){statement(this,210);value(this,'CONDITION','如果','Boolean');this.appendStatementInput('BODY').appendField('成立');this.appendStatementInput('ELSE').appendField('否则');}};
  for(const type of ['ax_set','ax_change'])Blockly.Blocks[type]={init(this:Blockly.Block){statement(this,285);this.appendDummyInput().appendField(type==='ax_set'?'设置变量':'增加变量').appendField(new Blockly.FieldTextInput('计数'),'NAME');value(this,'VALUE','数值','Number');}};
  Blockly.Blocks.ax_break={init(this:Blockly.Block){statement(this,0);this.appendDummyInput().appendField('断点 · 暂停程序');}};
  Blockly.Blocks.ax_number={init(this:Blockly.Block){expression(this,'Number',230);this.appendDummyInput().appendField(numeric(1),'NUM');}};
  Blockly.Blocks.ax_boolean={init(this:Blockly.Block){expression(this,'Boolean');this.appendDummyInput().appendField('真').appendField(new Blockly.FieldCheckbox('TRUE'),'BOOL');}};
  Blockly.Blocks.ax_variable={init(this:Blockly.Block){expression(this,'Number',285);this.appendDummyInput().appendField('变量').appendField(new Blockly.FieldTextInput('计数'),'NAME');}};
  Blockly.Blocks.ax_capture_ok={init(this:Blockly.Block){expression(this,'Boolean',175);this.appendDummyInput().appendField('最近一次拍照成功');}};
  for(const [type,output,check,choices] of [
    ['ax_math','Number','Number',[['+','ADD'],['−','SUB'],['×','MUL'],['÷','DIV']]],
    ['ax_compare','Boolean','Number',[['=','EQ'],['≠','NE'],['<','LT'],['≤','LE'],['>','GT'],['≥','GE']]],
    ['ax_logic','Boolean','Boolean',[['且','AND'],['或','OR']]],
  ] as [string,string,string,[string,string][]][])Blockly.Blocks[type]={init(this:Blockly.Block){expression(this,output);value(this,'A','',check);value(this,'B','',check).appendField(new Blockly.FieldDropdown(choices),'OP');this.setInputsInline(true);}};
  Blockly.Blocks.ax_not={init(this:Blockly.Block){expression(this,'Boolean');value(this,'VALUE','非','Boolean');}};
}
export function setBlockContext(snapshot:Snapshot,workspace:Blockly.WorkspaceSvg,source?:unknown) {
  context=snapshot;
  for(const values of Object.values(references))values.clear();
  const collect=(node:unknown)=>{if(!node || typeof node!=='object')return;const obj=node as Record<string,unknown>;if(obj.fields && typeof obj.fields==='object')for(const [name,value] of Object.entries(obj.fields))if(references[name] && typeof value==='string' && value)references[name].add(value);for(const child of Object.values(obj))collect(child);};
  collect(source??Blockly.serialization.workspaces.save(workspace));
  Blockly.Events.disable();
  try{for(const b of workspace.getAllBlocks(false))for(const [name,kind] of [['ROBOT','robot'],['CAMERA','camera'],['POINT','point']] as const){
    const field=b.getField(name);if(field instanceof DeviceDropdown)field.refresh();
  }}finally{Blockly.Events.enable();}
}
const shadow=(type:string,fields:Record<string,unknown>)=>({shadow:{type,fields}});
export const toolbox={kind:'categoryToolbox',contents:[
  {kind:'category',name:'流程与调试',colour:210,contents:[{kind:'block',type:'ax_start'},{kind:'block',type:'ax_wait',inputs:{SECONDS:shadow('ax_number',{NUM:1})}},{kind:'block',type:'ax_repeat',inputs:{COUNT:shadow('ax_number',{NUM:3})}},{kind:'block',type:'ax_if'},{kind:'block',type:'ax_while'},{kind:'block',type:'ax_break'},{kind:'block',type:'ax_log'}]},
  {kind:'category',name:'机械臂',colour:30,contents:['ax_move_j','ax_move_pose','ax_teach'].map(type=>({kind:'block',type}))},
  {kind:'category',name:'相机',colour:175,contents:['ax_capture','ax_capture_ok'].map(type=>({kind:'block',type}))},
  {kind:'category',name:'变量与数值',colour:285,contents:[{kind:'block',type:'ax_set',inputs:{VALUE:shadow('ax_number',{NUM:0})}},{kind:'block',type:'ax_change',inputs:{VALUE:shadow('ax_number',{NUM:1})}},...['ax_variable','ax_number','ax_math'].map(type=>({kind:'block',type}))]},
  {kind:'category',name:'条件判断',colour:210,contents:['ax_boolean','ax_compare','ax_logic','ax_not'].map(type=>({kind:'block',type}))},
]};
