import * as Blockly from 'blockly/core';
import { registerProgramBlocks,setBlockContext,toolbox,exampleWorkspace } from './program-blocks';
import { escape } from './icons';
import type { ProgramDefinition,ProgramState,ProgramValidation,Snapshot } from './types';

export class ProgramPanel {
  readonly workspace:Blockly.WorkspaceSvg;
  private snapshot!:Snapshot;
  private state:ProgramState|null=null;
  private session='';
  private applied='';
  private serverSource='';
  private contextKey='';
  private loading=false;
  private working=false;
  private conflict=false;
  private lastHighlight:string|null=null;
  private logKey='';
  private frameKey='';
  private polling=false;
  constructor(private root:HTMLElement,
    private request:<T>(path:string,method?:string,data?:unknown)=>Promise<T>,
    private accept:(snapshot:Snapshot)=>void,private act:(action:()=>Promise<void>)=>void,
    private report:(message:string,error?:boolean)=>void,private saveProject:()=>Promise<void>,
    private confirmDiscard:()=>Promise<boolean>) {
    registerProgramBlocks();
    root.innerHTML=`<div class="program-toolbar"><input id="program-name" maxlength="80" placeholder="程序名称" aria-label="程序名称"><button class="btn outline" id="program-demo">载入示例</button><button class="btn outline" id="program-save">保存程序</button><button class="btn outline" id="program-validate">校验</button><button class="btn primary" id="program-run">运行</button><button class="btn outline" id="program-step">单步</button><button class="btn outline" id="program-pause">暂停</button><button class="btn outline" id="program-continue">继续</button><button class="btn danger" id="program-stop">停止</button><button class="btn" id="program-export">导出程序</button><button class="btn" id="program-import">导入程序</button><button class="btn" id="program-reload">重新载入</button><button class="btn" id="program-undo" title="撤销">↶</button><button class="btn" id="program-redo" title="重做">↷</button><span id="program-draft"></span></div><div class="program-layout"><div class="program-workspace" id="blockly-host"></div><aside class="program-inspector"><strong id="program-state">程序就绪</strong><div id="program-message" class="program-message">拖拽开始块，再连接运动、拍照与逻辑积木。</div><div id="program-diagnostics"></div><div class="program-inspector-title">变量</div><div id="program-variables">—</div><div class="program-inspector-title">最近拍照</div><div id="program-frame">尚未拍照</div><div class="program-inspector-title">执行日志 <button id="program-export-log">导出</button></div><div id="program-logs"></div></aside></div><input type="file" id="program-file" accept=".json,application/json">`;
    const theme=Blockly.Theme.defineTheme('axislab',{name:'axislab',base:Blockly.Themes.Classic,componentStyles:{workspaceBackgroundColour:'#17212d',toolboxBackgroundColour:'#14202c',toolboxForegroundColour:'#dce4ec',flyoutBackgroundColour:'#213246',flyoutForegroundColour:'#e0ecf7',flyoutOpacity:.98,scrollbarColour:'#667e91',insertionMarkerColour:'#64ddbe',insertionMarkerOpacity:.4},fontStyle:{family:'Segoe UI, Microsoft YaHei, sans-serif',size:12}});
    this.workspace=Blockly.inject(root.querySelector('#blockly-host') as HTMLElement,{toolbox,media:'./blockly-media/',theme,trashcan:true,sounds:false,maxBlocks:500,move:{scrollbars:true,drag:true,wheel:true},zoom:{controls:true,wheel:true,startScale:.75,minScale:.35,maxScale:1.5},grid:{spacing:24,length:2,colour:'#304356',snap:false}});
    new ResizeObserver(()=>this.resize()).observe(root);
    this.workspace.addChangeListener(event=>{if(!this.loading && !event.isUiEvent){this.updateDraft();if(event.type===Blockly.Events.BLOCK_CHANGE)setBlockContext(this.snapshot,this.workspace);}});
    const el=<T extends HTMLElement>(id:string)=>root.querySelector<T>('#'+id)!;
    const run=(task:()=>Promise<void>)=>this.act(async()=>{this.working=true;this.updateButtons();try{await task();}finally{this.working=false;this.updateButtons();}});
    el<HTMLInputElement>('program-name').oninput=()=>this.updateDraft();
    el('program-save').onclick=()=>run(this.saveProject);
    el('program-validate').onclick=()=>run(async()=>{await this.validate();});
    el('program-run').onclick=()=>run(()=>this.start(false));
    el('program-step').onclick=()=>run(async()=>{if(this.state?.status==='Paused')this.update(await this.request<ProgramState>('/v1/program/step','POST'));else await this.start(true);});
    el('program-pause').onclick=()=>run(async()=>this.update(await this.request<ProgramState>('/v1/program/pause','POST')));
    el('program-continue').onclick=()=>run(async()=>this.update(await this.request<ProgramState>('/v1/program/continue','POST')));
    el('program-stop').onclick=()=>void this.stop();
    el('program-demo').onclick=()=>run(async()=>{
      if(this.isDirty && !await this.confirmDiscard())return;
      if(!this.snapshot.project.devices.some(d=>d.robot) || !this.snapshot.project.devices.some(d=>d.typeId==='camera.pinhole'))throw new Error('请先添加一台机械臂和一台相机，再载入示例。');
      this.load({version:1,name:'运动拍照循环',workspace:exampleWorkspace(this.snapshot)},false);this.report('已载入示例草稿；按场景检查目标后校验并运行。');
    });
    el('program-reload').onclick=()=>run(async()=>{if(this.isDirty && !await this.confirmDiscard())return;this.load(this.snapshot.project.program,true);this.conflict=false;this.updateDraft();});
    el('program-export').onclick=()=>this.download('AxisLab-program.json',this.definition(),false);
    el('program-export-log').onclick=()=>this.download('AxisLab-program-log.json',this.state);
    el('program-import').onclick=()=>el<HTMLInputElement>('program-file').click();
    el<HTMLInputElement>('program-file').onchange=()=>run(async()=>{
      const input=el<HTMLInputElement>('program-file'),file=input.files?.[0];input.value='';if(!file)return;
      if(file.size>262144)throw new Error('程序文件不能超过 256 KiB。');
      if(this.isDirty && !await this.confirmDiscard())return;
      const value=JSON.parse(await file.text()) as ProgramDefinition;
      await this.request('/v1/program/validate','POST',{program:value});
      this.load(value,false);this.report('已导入程序草稿，请检查设备引用并保存工程。');
    });
    el('program-undo').onclick=()=>this.workspace.undo(false);el('program-redo').onclick=()=>this.workspace.undo(true);
    setInterval(()=>void this.poll(),100);
  }
  private definition():ProgramDefinition {return {version:1,name:(this.root.querySelector<HTMLInputElement>('#program-name')!.value.trim()),workspace:Blockly.serialization.workspaces.save(this.workspace)};}
  get isDirty(){return !!this.snapshot && JSON.stringify(this.definition())!==this.applied;}
  private load(definition:ProgramDefinition,clean:boolean) {
    this.loading=true;Blockly.Events.disable();
    try{setBlockContext(this.snapshot,this.workspace,definition.workspace);Blockly.serialization.workspaces.load(definition.workspace,this.workspace);this.root.querySelector<HTMLInputElement>('#program-name')!.value=definition.name;setBlockContext(this.snapshot,this.workspace);}
    finally{Blockly.Events.enable();this.loading=false;}
    if(clean)this.applied=JSON.stringify(this.definition());
    this.root.querySelector('#program-diagnostics')!.textContent='';
    this.workspace.clearUndo();this.workspace.getToolbox()?.clearSelection();this.lastHighlight=null;this.updateDraft();this.resize();
  }
  setSnapshot(snapshot:Snapshot) {
    const key=JSON.stringify(snapshot.project.program);const changedSession=this.session!==snapshot.sessionId;
    const dirty=this.snapshot?this.isDirty:false;this.snapshot=snapshot;
    if(changedSession){this.session=snapshot.sessionId;this.conflict=false;this.load(snapshot.project.program,true);this.state=null;}
    else if(key!==this.serverSource){
      if(!dirty)this.load(snapshot.project.program,true);
      else if(JSON.stringify(this.definition())!==key)this.conflict=true;
    }
    this.serverSource=key;
    const contextKey=JSON.stringify([snapshot.project.devices.map(d=>[d.id,d.name,d.robot?.modelId]),snapshot.project.teachPoints.map(p=>[p.id,p.robotId,p.name])]);
    if(contextKey!==this.contextKey){this.contextKey=contextKey;setBlockContext(snapshot,this.workspace);}
    this.updateDraft();
  }
  private updateDraft(){this.root.querySelector('#program-draft')!.textContent=this.conflict?'服务端程序已更新；请导出草稿或重新载入。':this.isDirty?'● 程序草稿未保存':'程序已同步';this.updateButtons();}
  private updateButtons() {
    const active=this.state?.active || !!this.snapshot?.runtime.programRunId;
    const edit=this.working || active;
    for(const id of ['program-name','program-demo','program-save','program-validate','program-run','program-import','program-reload','program-undo','program-redo'])this.root.querySelector<HTMLInputElement|HTMLButtonElement>('#'+id)!.disabled=!!edit;
    this.root.querySelector<HTMLButtonElement>('#program-step')!.disabled=this.working || !!active && this.state?.status!=='Paused';
    this.root.querySelector<HTMLButtonElement>('#program-pause')!.disabled=this.working || this.state?.status!=='Running';
    this.root.querySelector<HTMLButtonElement>('#program-continue')!.disabled=this.working || this.state?.status!=='Paused';
    this.root.querySelector<HTMLButtonElement>('#program-stop')!.disabled=!active;
    this.workspace.setIsReadOnly(!!active);
  }
  async apply() {
    if(this.conflict)throw new Error('服务端程序已更新。请先导出需要保留的草稿，再重新载入。');
    if(!this.isDirty)return;
    const definition=this.definition();
    const next=await this.request<Snapshot>('/v1/program','PUT',{expectedRevision:this.snapshot.revision,program:definition});
    this.applied=JSON.stringify(this.definition());this.serverSource=JSON.stringify(next.project.program);this.accept(next);this.updateDraft();
  }
  private async validate() {
    const result=await this.request<ProgramValidation>('/v1/program/validate','POST',{program:this.definition()});
    for(const b of this.workspace.getAllBlocks(false))b.setWarningText(null,'validation');
    for(const d of result.diagnostics)if(d.blockId)this.workspace.getBlockById(d.blockId)?.setWarningText(d.message,'validation');
    this.root.querySelector('#program-diagnostics')!.innerHTML=result.valid?'<span class="program-valid">校验通过</span>':result.diagnostics.map(d=>`<button data-block="${escape(d.blockId??'')}">${escape(d.code)} · ${escape(d.message)}</button>`).join('');
    this.root.querySelectorAll<HTMLButtonElement>('[data-block]').forEach(b=>b.onclick=()=>{if(b.dataset.block && this.workspace.getBlockById(b.dataset.block))this.workspace.centerOnBlock(b.dataset.block);});
    return result;
  }
  private async start(step:boolean) {
    const valid=await this.validate();if(!valid.valid)throw new Error('程序校验未通过，请查看右侧提示。');
    await this.apply();this.update(await this.request<ProgramState>('/v1/program/run','POST',{expectedRevision:this.snapshot.revision,singleStep:step}));
  }
  async stop(){try{this.update(await this.request<ProgramState>('/v1/program/stop','POST'));}catch(e){this.report(e instanceof Error?e.message:'停止失败',true);}}
  private async poll(){if(this.polling || !this.snapshot)return;this.polling=true;try{this.update(await this.request<ProgramState>('/v1/program/status'));}catch{/* health is reported by the main view */}finally{this.polling=false;}}
  private update(state:ProgramState) {
    if(this.snapshot && state.sessionId!==this.snapshot.sessionId)return;this.state=state;
    const labels={Idle:'就绪',Running:'运行中',Pausing:'当前块完成后暂停',Paused:'已暂停',Completed:'已完成',Cancelled:'已停止',Failed:'运行失败'};
    this.root.querySelector('#program-state')!.textContent=`${labels[state.status]} · ${state.executedBlocks} 步 · ${state.activeSeconds.toFixed(1)} s`;
    this.root.querySelector('#program-message')!.textContent=[state.errorCode,state.message].filter(Boolean).join(' · ') || (state.active?'运行和暂停期间锁定场景；停止会取消后续动作及等待。':'保存程序同时保存工程。断点块会暂停，使用单步或继续。');
    this.root.querySelector('#program-variables')!.innerHTML=Object.entries(state.variables).map(([k,v])=>`<div>${escape(k)} <b>${v.toFixed(4).replace(/\.?0+$/,'')}</b></div>`).join('')||'—';
    if(state.activeBlockId!==this.lastHighlight){this.workspace.highlightBlock(state.activeBlockId);this.lastHighlight=state.activeBlockId;}
    const key=`${state.runId}:${state.logs.at(-1)?.sequence}`;
    if(key!==this.logKey){this.root.querySelector('#program-logs')!.innerHTML=[...state.logs].reverse().map(l=>`<div class="program-log ${l.level.toLowerCase()}"><time>${new Date(l.at).toLocaleTimeString('zh-CN',{hour12:false})}</time> ${escape(l.message)}</div>`).join('');this.logKey=key;}
    const frame=state.lastCapture;const frameKey=JSON.stringify(frame);
    if(frameKey!==this.frameKey){this.frameKey=frameKey;const host=this.root.querySelector('#program-frame')!;host.innerHTML=frame?.success && frame.imageUrl?`<a href="${escape(frame.imageUrl)}" target="_blank" rel="noopener"><img src="${escape(frame.imageUrl)}" alt="程序最近拍照"></a><small>Frame ${escape(frame.frameId??'')}</small>`:frame?escape(frame.message??'拍照失败'):'尚未拍照';}
    this.updateButtons();
  }
  resize(){if(this.root.clientWidth && this.root.clientHeight)requestAnimationFrame(()=>Blockly.svgResize(this.workspace));}
  private download(name:string,value:unknown,pretty=true){const url=URL.createObjectURL(new Blob([JSON.stringify(value,null,pretty?2:undefined)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}
}
