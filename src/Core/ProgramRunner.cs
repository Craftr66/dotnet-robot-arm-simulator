using System.Globalization;
using System.Text.RegularExpressions;

namespace AxisLab.Core;

public record ProgramLog(long Sequence, DateTimeOffset At, string? BlockId, string Level, string Message);
public record ProgramCaptureResult(Guid CameraId, Guid? CaptureId, bool Success, Guid? FrameId, string? ImageUrl, string? ErrorCode, string? Message);
public record ProgramRunState(Guid SessionId, Guid? RunId, string Status, string? ActiveBlockId, string? ActiveOperation,
    long ExecutedBlocks, double ActiveSeconds, string? ErrorCode, string? Message, Dictionary<string,double> Variables,
    ProgramCaptureResult? LastCapture, ProgramLog[] Logs)
{
    public bool Active => Status is "Running" or "Pausing" or "Paused";
}

/// <summary>One workspace program. Polls bounded primitive commands, preserving server-side ownership through breakpoints.</summary>
public sealed class ProgramRunner(ProjectStore projects, CaptureService captures, TimeProvider? timeProvider=null)
{
    private readonly object gate=new();
    private readonly TimeProvider clock=timeProvider??TimeProvider.System;
    private Guid session;
    private Guid? runId;
    private string status="Idle";
    private ProgramInstruction? current;
    private IEnumerator<ProgramInstruction>? iterator;
    private bool done,started,stepping,pauseRequested;
    private Guid? commandId,captureId;
    private long instructionStarted,lastTick,executed,logSequence;
    private double activeSeconds,waitSeconds;
    private string? errorCode,message;
    private readonly Dictionary<string,double> variables=[];
    private readonly List<ProgramLog> logs=[];
    private ProgramCaptureResult? lastCapture;
    private bool Active=>status is "Running" or "Pausing" or "Paused";
    private void Log(string text,string level="Info")
    {
        logs.Add(new(++logSequence,clock.GetUtcNow(),current?.Id,level,text.Length>400?text[..400]:text));
        if(logs.Count>100)logs.RemoveAt(0);
    }
    private ProgramRunState Snapshot()=>new(session,runId,status,current?.Id,current?.Operation,executed,activeSeconds,errorCode,message,
        new(variables),lastCapture,logs.ToArray());
    public ProgramRunState Get()
    {
        lock(gate)
        {
            var next=projects.SessionId;
            if(!Active && session!=next){session=next;status="Idle";runId=null;current=null;lastCapture=null;variables.Clear();logs.Clear();executed=0;activeSeconds=0;errorCode=null;message=null;}
            return Snapshot();
        }
    }
    private static IEnumerable<ProgramInstruction> All(IEnumerable<ProgramInstruction> nodes)
    {
        foreach(var n in nodes){yield return n;foreach(var child in All(n.Body))yield return child;foreach(var child in All(n.Else))yield return child;}
    }
    private CompiledProgram Compile(WorkspaceSnapshot snapshot,ProgramDefinition? definition=null)
    {
        var compiled=ProgramCompiler.Compile(snapshot.Project with{Program=definition??snapshot.Project.Program});
        var stale=All(compiled.Body).Where(n=>n.Operation=="ax_teach" && snapshot.Teaching!.Any(p=>p.Point.Id==n.PointId && p.Stale))
            .Select(n=>new ProgramDiagnostic(n.Id,"StaleTeachPoint","示教点依赖已改变，请先重新校验该点。")).ToArray();
        var diagnostics=compiled.Validation.Diagnostics.Concat(stale).ToArray();
        return compiled with{Validation=compiled.Validation with{Valid=diagnostics.Length==0,Diagnostics=diagnostics}};
    }
    public ProgramValidation Validate(ProgramDefinition? definition=null)
    {lock(gate)return Compile(projects.Get(),definition).Validation;}
    public ProgramRunState Run(long expected,bool singleStep=false)
    {
        lock(gate)
        {
            if(Active)throw new DomainException("ProgramBusy","已有程序运行或暂停，请继续或先停止。",409);
            var snapshot=projects.Get();var compiled=Compile(snapshot);
            if(!compiled.Validation.Valid)throw new DomainException("ProgramInvalid",string.Join("；",compiled.Validation.Diagnostics.Take(5).Select(d=>d.Message)));
            var id=Guid.NewGuid();projects.ReserveProgram(id,expected);
            session=snapshot.SessionId;runId=id;status="Running";current=null;done=false;started=false;stepping=singleStep;pauseRequested=false;
            iterator?.Dispose();iterator=Expand(compiled.Body).GetEnumerator();commandId=null;captureId=null;executed=0;activeSeconds=0;
            variables.Clear();foreach(var name in compiled.Variables)variables[name]=0;
            logs.Clear();logSequence=0;lastCapture=null;errorCode=null;message=null;lastTick=clock.GetTimestamp();
            Log(singleStep?"程序以单步模式启动。":"程序启动。");return Snapshot();
        }
    }
    public ProgramRunState Pause()
    {
        lock(gate)
        {
            if(status is not("Running" or "Pausing"))throw new DomainException("ProgramNotRunning","程序当前未运行。",409);
            pauseRequested=true;status=current is null || done?"Paused":"Pausing";message="完成当前积木后暂停。";return Snapshot();
        }
    }
    public ProgramRunState Resume(bool singleStep=false)
    {
        lock(gate)
        {
            if(status!="Paused")throw new DomainException("ProgramNotPaused","请先暂停程序。",409);
            if(runId is null || projects.ProgramStopRequested(runId.Value)) {Finish("Cancelled","ProgramStopped","程序控制权已撤销。");return Snapshot();}
            status="Running";stepping=singleStep;pauseRequested=false;message=null;lastTick=clock.GetTimestamp();Log(singleStep?"执行下一步。":"继续程序。");return Snapshot();
        }
    }
    public ProgramRunState Stop()
    {
        lock(gate){if(Active)Finish("Cancelled","ProgramStopped","程序已停止，当前动作及等待已取消。");return Snapshot();}
    }
    private void Finish(string final,string? code,string? text)
    {
        if(captureId.HasValue)try{captures.Cancel(captureId.Value);}catch(DomainException){}
        if(runId.HasValue)projects.EndProgram(runId.Value);
        status=final;errorCode=code;message=text;iterator?.Dispose();iterator=null;captureId=null;
        Log(text??"程序已完成。",final=="Failed"?"Error":"Info");
    }
    private double Numeric(ProgramExpression? e)
    {
        if(e is null || e.Type!="number")throw new DomainException("ExpressionType","期望数值表达式。");
        var value=e.Kind switch
        {
            "literal"=>e.Number,"variable"=>variables.GetValueOrDefault(e.Name!),
            "math"=>e.Operator switch
            {
                "ADD"=>Numeric(e.Left)+Numeric(e.Right),"SUB"=>Numeric(e.Left)-Numeric(e.Right),"MUL"=>Numeric(e.Left)*Numeric(e.Right),
                "DIV"=>Math.Abs(Numeric(e.Right))<1e-12?throw new DomainException("DivisionByZero","表达式除数不能为零。"):Numeric(e.Left)/Numeric(e.Right),
                _=>throw new DomainException("InvalidOperator","数值运算无效。")
            },_=>throw new DomainException("ExpressionType","数值表达式无效。")
        };
        if(!double.IsFinite(value) || Math.Abs(value)>1e12)throw new DomainException("ExpressionRange","变量或表达式结果超出 ±1e12。");return value;
    }
    private bool Boolean(ProgramExpression? e)
    {
        if(e is null || e.Type!="boolean")throw new DomainException("ExpressionType","期望布尔条件。");
        return e.Kind switch
        {
            "literal"=>e.Boolean,"capture"=>lastCapture?.Success==true,"not"=>!Boolean(e.Left),
            "logic"=>e.Operator=="AND"?Boolean(e.Left)&&Boolean(e.Right):Boolean(e.Left)||Boolean(e.Right),
            "compare"=>e.Operator switch{"EQ"=>Numeric(e.Left)==Numeric(e.Right),"NE"=>Numeric(e.Left)!=Numeric(e.Right),"LT"=>Numeric(e.Left)<Numeric(e.Right),"LE"=>Numeric(e.Left)<=Numeric(e.Right),"GT"=>Numeric(e.Left)>Numeric(e.Right),"GE"=>Numeric(e.Left)>=Numeric(e.Right),_=>false},
            _=>throw new DomainException("ExpressionType","布尔条件无效。")
        };
    }
    private IEnumerable<ProgramInstruction> Expand(ProgramInstruction[] nodes)
    {
        foreach(var node in nodes)
        {
            if(node.Operation=="ax_while")
            {
                while(true){yield return node;if(!Boolean(node.Expression))break;foreach(var child in Expand(node.Body))yield return child;}
            }
            else
            {
                yield return node;
                if(node.Operation=="ax_repeat")
                {
                    var count=Numeric(node.Expression);
                    if(count<0 || count>10000 || count!=Math.Truncate(count))throw new DomainException("InvalidRepeat","循环次数须为 0—10000 的整数。");
                    for(var i=0;i<(int)count;i++)foreach(var child in Expand(node.Body))yield return child;
                }
                else if(node.Operation=="ax_if")foreach(var child in Expand(Boolean(node.Expression)?node.Body:node.Else))yield return child;
            }
        }
    }
    private void Complete()
    {
        done=true;executed++;
        if(stepping || pauseRequested || current?.Operation=="ax_break")
        {status="Paused";message=current?.Operation=="ax_break"?"已到达断点。":"已完成当前积木，等待下一步。";pauseRequested=false;}
    }
    private void CaptureFailed(string code,string text)
    {
        lastCapture=new(current!.DeviceId,captureId,false,null,null,code,text);captureId=null;
        Log("拍照失败："+text,current.AllowFailure?"Warning":"Error");
        if(!current.AllowFailure)throw new DomainException(code,text);
        Complete();
    }
    private void StartInstruction()
    {
        var node=current!;started=true;instructionStarted=clock.GetTimestamp();Log("执行 "+node.Operation);
        WorkspaceSnapshot Motion(Func<WorkspaceSnapshot> action)
        {
            var snapshot=action();commandId=snapshot.Runtime.Robots.Single(r=>r.DeviceId==node.DeviceId).CommandId;return snapshot;
        }
        switch(node.Operation)
        {
            case "ax_move_j":Motion(()=>projects.MoveJoints(node.DeviceId,node.Joints,node.Speed,projects.Get().Revision,runId));break;
            case "ax_move_pose":Motion(()=>projects.MoveCartesian(node.DeviceId,node.Motion!,projects.Get().Revision,runId));break;
            case "ax_teach":Motion(()=>projects.PlayTeachPoints(node.DeviceId,[node.PointId],node.Kind,node.Speed,projects.Get().Revision,runId));break;
            case "ax_capture":
                try{captureId=captures.Trigger(node.DeviceId,session).CaptureId;lastCapture=null;}catch(DomainException e){CaptureFailed(e.Code,e.Message);}break;
            case "ax_wait":waitSeconds=Numeric(node.Expression);if(waitSeconds<0 || waitSeconds>300)throw new DomainException("InvalidWait","等待时间须为 0—300 秒。");if(waitSeconds==0)Complete();break;
            case "ax_set":case "ax_change":
                var value=Numeric(node.Expression)+(node.Operation=="ax_change"?variables.GetValueOrDefault(node.Name):0);
                if(!double.IsFinite(value)||Math.Abs(value)>1e12)throw new DomainException("ExpressionRange","变量超出 ±1e12。");
                variables[node.Name]=value;Log(node.Name+" = "+value.ToString("G8",CultureInfo.InvariantCulture));Complete();break;
            case "ax_log":Log(Regex.Replace(node.Text,@"\{([\p{L}_][\p{L}\p{N}_]*)\}",m=>variables.TryGetValue(m.Groups[1].Value,out var v)?v.ToString("G8",CultureInfo.InvariantCulture):m.Value));Complete();break;
            default:Complete();break;
        }
    }
    public void Tick()
    {
        lock(gate)
        {
            if(!Active)return;
            try
            {
                if(runId is null || projects.ProgramStopRequested(runId.Value) || projects.SessionId!=session){Finish("Cancelled","ProgramStopped","程序已被本地停止或会话切换取消。");return;}
                var now=clock.GetTimestamp();var elapsed=clock.GetElapsedTime(lastTick,now).TotalSeconds;lastTick=now;
                if(status=="Paused")return;
                activeSeconds+=elapsed;
                if(activeSeconds>600)throw new DomainException("ProgramTimeout","程序活动时间超过 600 秒。");
                if(current is null || done)
                {
                    if(iterator is null || !iterator.MoveNext()){Finish("Completed",null,null);return;}
                    if(executed>=10000)throw new DomainException("ProgramStepLimit","程序执行超过 10000 个积木步骤。");
                    current=iterator.Current;started=false;done=false;commandId=null;captureId=null;
                }
                if(!started){StartInstruction();return;}
                var duration=clock.GetElapsedTime(instructionStarted).TotalSeconds;
                if(current.Operation=="ax_wait") {if(duration>=waitSeconds)Complete();return;}
                if(duration>current.Timeout)
                {
                    if(current.Operation=="ax_capture")
                    {if(captureId.HasValue)captures.Cancel(captureId.Value);CaptureFailed("CaptureTimeout","程序拍照等待超时。");return;}
                    throw new DomainException("InstructionTimeout","当前动作超过积木配置的等待时限。");
                }
                if(current.Operation=="ax_capture" && captureId.HasValue)
                {
                    var capture=captures.Get(captureId.Value);
                    if(capture.Status=="Failed"){CaptureFailed(capture.ErrorCode??"CaptureFailed",capture.Message??"拍照失败。");return;}
                    if(capture.Status=="Completed")
                    {
                        var metadata=captures.Metadata(current.DeviceId,capture.FrameId!.Value);
                        lastCapture=new(current.DeviceId,captureId,true,capture.FrameId,metadata.ImageUrl,null,null);captureId=null;
                        Log("拍照完成，Frame "+capture.FrameId);Complete();
                    }
                }
                else if(current.Operation is "ax_move_j" or "ax_move_pose" or "ax_teach")
                {
                    var robot=projects.GetRuntime().Robots.Single(r=>r.DeviceId==current.DeviceId);
                    if(robot.CommandId!=commandId)throw new DomainException("CommandChanged","执行中的机器人命令已改变。");
                    if(robot.Status=="Failed")throw new DomainException(robot.MotionErrorCode??"MotionFailed",robot.MotionMessage??"运动失败。");
                    if(robot.Status=="Cancelled")throw new DomainException("MotionCancelled","机器人运动已取消。");
                    if(robot.Status=="Completed")Complete();
                }
            }
            catch(DomainException e){Finish("Failed",e.Code,e.Message);}
            catch(Exception){Finish("Failed","ProgramInternalError","程序执行遇到内部错误，已停止动作。");}
        }
    }
}
