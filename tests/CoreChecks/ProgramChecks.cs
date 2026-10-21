using AxisLab.Core;
using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

static class ProgramChecks
{
    public static int Run(string root)
    {
        int checks=0;
        void Check(bool yes,string text){if(!yes)throw new Exception("FAIL: "+text);Console.WriteLine("PASS: "+text);checks++;}
        void Reject(Action action,string code){try{action();throw new Exception("Expected "+code);}catch(DomainException e){Check(e.Code==code,code+" (program)");}}
        Dictionary<string,object?> B(string type,object? fields=null,Dictionary<string,object?>? inputs=null)=>new(){["type"]=type,["id"]=Guid.NewGuid().ToString(),["fields"]=fields??new{},["inputs"]=inputs?.ToDictionary(p=>p.Key,p=>(object)new{block=p.Value})??[]};
        object Num(double n)=>B("ax_number",new{NUM=n});
        object Bool(bool v)=>B("ax_boolean",new{BOOL=v});
        object Var(string name)=>B("ax_variable",new{NAME=name});
        ProgramDefinition P(params Dictionary<string,object?>[] nodes)
        {
            for(var i=0;i<nodes.Length-1;i++)nodes[i]["next"]=new{block=nodes[i+1]};
            return new(1,"核心程序测试",JsonSerializer.SerializeToElement(new{blocks=new{languageVersion=0,blocks=new[]{B("ax_start",inputs:nodes.Length>0?new(){["BODY"]=nodes[0]}:[])}}},new JsonSerializerOptions{MaxDepth=2048}));
        }
        var clock=new ProgramClock();var store=new ProjectStore(Path.Combine(root,"program6"),clock);
        var robot=store.Add("robot.generic3r",store.Get().Revision).Project.Devices[0];
        var camera=store.Add("camera.pinhole",store.Get().Revision).Project.Devices.Last();
        camera=camera with{Parameters=camera.Parameters with{Width=64,Height=64,Cx=32,Cy=32}};store.Update(camera.Id,camera,store.Get().Revision);
        var captures=new CaptureService(store,clock);var runner=new ProgramRunner(store,captures,clock);
        void Set(ProgramDefinition p)=>store.SetProgram(p,store.Get().Revision);
        void Start(bool step=false)=>runner.Run(store.Get().Revision,step);
        void Tick(double dt=.02){clock.Advance(dt);store.AdvanceMotions(dt);runner.Tick();}
        void Finish(int limit=3000){for(int i=0;i<limit && runner.Get().Active;i++)Tick();if(runner.Get().Active)throw new Exception("Program did not terminate");}
        Dictionary<string,object?> Move(double j1)=>B("ax_move_j",new{ROBOT=robot.Id.ToString(),J1=j1,J2=0,J3=0,J4=0,J5=0,J6=0,SPEED=100,TIMEOUT=120});
        Dictionary<string,object?> Capture(bool proceed=true,double timeout=15)=>B("ax_capture",new{CAMERA=camera.Id.ToString(),CONTINUE=proceed,TIMEOUT=timeout});
        var migrated=store.Import(store.Get().Project with{SchemaVersion=4,Program=null},store.Get().Revision);
        Check(migrated.Project.SchemaVersion==5 && migrated.Project.Program?.Version==1,"v4 migrates to program schema5 with stable devices");
        Set(ProgramDefinition.Empty());Check(!runner.Validate().Valid,"empty program saves as draft but fails execution validation");
        Reject(()=>Start(),"ProgramInvalid");Check(store.GetRuntime().ProgramRunId is null,"rejected program does not reserve workspace");
        var counter=B("ax_change",new{NAME="计数"},new(){["VALUE"]=Num(1)});
        var repeat=B("ax_repeat",inputs:new(){["COUNT"]=Num(3),["BODY"]=counter});
        var condition=B("ax_compare",new{OP="EQ"},new(){["A"]=Var("计数"),["B"]=Num(3)});
        var choose=B("ax_if",inputs:new(){["CONDITION"]=condition,["BODY"]=B("ax_log",new{TEXT="计数={计数}"}),["ELSE"]=B("ax_log",new{TEXT="错误分支"})});
        var program=P(B("ax_set",new{NAME="计数"},new(){["VALUE"]=Num(0)}),repeat,choose);
        Set(program);Start();Finish();
        Check(runner.Get().Status=="Completed" && runner.Get().Variables["计数"]==3,"numeric variables repeat and conditional execute in core");
        Check(runner.Get().Logs.Any(l=>l.Message=="计数=3") && !runner.Get().Logs.Any(l=>l.Message=="错误分支"),"conditional logs bind evaluated variable values");
        var saved=store.Save("积木程序保存",store.Get().Revision);var source=saved.Project.Program!.Workspace.GetRawText();
        var disk=new ProjectStore(Path.Combine(root,"program6"));var reopened=disk.Open(saved.Project.Id.ToString(),disk.Get().Revision);
        Check(JsonElement.DeepEquals(reopened.Project.Program!.Workspace,JsonDocument.Parse(source).RootElement),"program source and block identities survive disk reopen");
        Set(P(Enumerable.Range(0,150).Select(i=>B("ax_log",new{TEXT="顺序块 "+i})).ToArray()));
        var longSaved=store.Save("长程序",store.Get().Revision);store.Open(longSaved.Project.Id.ToString(),store.Get().Revision);Start();Finish();
        Check(runner.Get().Status=="Completed" && runner.Get().ExecutedBlocks==150,"150 linked instructions survive compact project save reopen and execution");
        Reject(()=>Set(new(99,"bad",program.Workspace)),"InvalidProgram");
        var unsupported=P(B("arbitrary_javascript",new{TEXT="eval(1)"}));Reject(()=>Set(unsupported),"UnsupportedBlock");
        var duplicate=B("ax_log",new{TEXT="duplicate"});var dupe=B("ax_log",new{TEXT="duplicate"});dupe["id"]=duplicate["id"];Reject(()=>Set(P(duplicate,dupe)),"InvalidProgram");
        Set(P(B("ax_if",inputs:new(){["CONDITION"]=Num(1)})));Check(runner.Validate().Diagnostics.Any(d=>d.Code=="ExpressionType"),"typed compiler rejects number in boolean condition");
        Set(P(B("ax_wait")));Check(runner.Validate().Diagnostics.Any(d=>d.Code=="MissingInput"),"missing expression is an actionable validation error");
        Set(P(B("ax_set",new{NAME="变量"},new(){["VALUE"]=B("ax_math",new{OP="DIV"},new(){["A"]=Num(1),["B"]=Num(0)})})));Start();Finish();
        Check(runner.Get().Status=="Failed" && runner.Get().ErrorCode=="DivisionByZero" && store.GetRuntime().ProgramRunId is null,"division by zero fails and releases ownership");
        Set(P(B("ax_wait",inputs:new(){["SECONDS"]=Num(-1)})));Start();Finish();Check(runner.Get().ErrorCode=="InvalidWait","negative wait fails without blocking scheduler");
        Set(P(B("ax_repeat",inputs:new(){["COUNT"]=Num(1.5)})));Start();Finish();Check(runner.Get().ErrorCode=="InvalidRepeat","fractional loop count fails explicitly");
        Set(P(B("ax_while",inputs:new(){["CONDITION"]=Bool(true)})));Start();for(int i=0;i<10010 && runner.Get().Active;i++)Tick(.001);
        Check(runner.Get().ErrorCode=="ProgramStepLimit" && runner.Get().Logs.Length<=100,"infinite loop bounded by step budget and log retention");
        Set(P(B("ax_wait",inputs:new(){["SECONDS"]=Num(300)}),B("ax_wait",inputs:new(){["SECONDS"]=Num(300)})));Start();Tick();Tick(301);Tick();Tick(301);
        Check(runner.Get().ErrorCode=="ProgramTimeout" && !runner.Get().Active,"active program runtime is bounded independently of loops");
        Set(P(Move(10),B("ax_break"),B("ax_wait",inputs:new(){["SECONDS"]=Num(.1)})));Start(true);
        for(int i=0;i<1000 && runner.Get().Status!="Paused";i++)Tick();
        Check(runner.Get().Status=="Paused" && Math.Abs(store.GetRuntime().Robots[0].Joints[0]-10)<1e-8,"single step waits for a real robot move to finish");
        Reject(()=>store.Save("locked",store.Get().Revision),"ProgramActive");
        Reject(()=>store.MoveJoints(robot.Id,[0,0,0],100,store.Get().Revision),"ProgramActive");
        Reject(()=>store.ClaimRobot(robot.Id,Guid.NewGuid()),"ProgramActive");
        Reject(()=>store.Add("geometry.box",store.Get().Revision),"ProgramActive");
        runner.Resume();Tick();Check(runner.Get().Status=="Paused" && runner.Get().Message!.Contains("断点"),"breakpoint pauses while keeping program reservation");
        var before=runner.Get().ActiveSeconds;clock.Advance(1000);runner.Tick();runner.Resume();Finish();
        Check(runner.Get().Status=="Completed" && runner.Get().ActiveSeconds-before<1,"paused wall time does not consume active runtime budget");
        Set(P(B("ax_wait",inputs:new(){["SECONDS"]=Num(.1)}),B("ax_log",new{TEXT="边界后"})));Start();Tick();runner.Pause();
        Check(runner.Get().Status=="Pausing","pause request distinguishes in-flight block from paused boundary");
        for(int i=0;i<20 && runner.Get().Status!="Paused";i++)Tick();Check(runner.Get().Status=="Paused" && !runner.Get().Logs.Any(l=>l.Message=="边界后"),"pause completes current wait and blocks following instruction");
        runner.Stop();Check(runner.Get().Status=="Cancelled" && store.GetRuntime().ProgramRunId is null,"stop releases paused workspace");
        Set(P(Move(90),B("ax_log",new{TEXT="不能执行"})));Start();Tick();Tick(.1);runner.Stop();var halted=store.GetRuntime().Robots[0].Joints[0];Tick(20);
        Check(store.GetRuntime().Robots[0].Joints[0]==halted && !runner.Get().Logs.Any(l=>l.Message=="不能执行"),"program stop cancels motion without a later target jump");
        Set(P(B("ax_wait",inputs:new(){["SECONDS"]=Num(100)})));Start();Tick();store.StopAllRobots();Tick();
        Check(runner.Get().Status=="Cancelled" && store.GetRuntime().ProgramRunId is null,"global robot stop also cancels a program waiting without robot motion");
        var target=RobotModels.Forward(store.Get().Project.Devices.First(d=>d.Id==robot.Id).Robot!,[15,-10,20]).Tcp.Pose;
        Set(P(B("ax_move_pose",new{ROBOT=robot.Id.ToString(),FRAME="base",KIND="MoveL",X=target.X,Y=target.Y,Z=target.Z,RX=target.Rx,RY=target.Ry,RZ=target.Rz,SPEED=100,POSITION=true,TIMEOUT=120})));Start();Finish();
        Check(runner.Get().Status=="Completed" && (V3.Position(store.GetRuntime().Robots[0].TcpBase.Matrix)-new V3(target.X,target.Y,target.Z)).Length<.02,"Cartesian MoveL block executes through common IK planner");
        var taught=store.RecordTeachPoint(robot.Id,"程序点","base",store.Get().Revision).Project.TeachPoints!.Single();
        Set(P(B("ax_teach",new{ROBOT=robot.Id.ToString(),POINT=taught.Id.ToString(),KIND="MoveJ",SPEED=100,TIMEOUT=120})));Start();Finish();
        Check(runner.Get().Status=="Completed","teach-point block executes persisted stable point UUID");
        var toolDevice=store.Get().Project.Devices.First(d=>d.Id==robot.Id);store.Update(robot.Id,toolDevice with{Robot=toolDevice.Robot! with{ToolTransform=new(0,0,85)}},store.Get().Revision);
        Check(runner.Validate().Diagnostics.Any(d=>d.Code=="StaleTeachPoint"),"program preflight detects stale teach point before motion");
        Reject(()=>Start(),"ProgramInvalid");
        store.EditTeachPoint(taught.Id,taught.Name,"revalidate",store.Get().Revision);Start();Finish();
        Check(runner.Get().Status=="Completed","program resumes after explicit point revalidation");
        Set(P(Capture(),B("ax_if",inputs:new(){["CONDITION"]=B("ax_capture_ok"),["BODY"]=B("ax_set",new{NAME="结果"},new(){["VALUE"]=Num(1)}),["ELSE"]=B("ax_set",new{NAME="结果"},new(){["VALUE"]=Num(9)})})));Start();Finish();
        Check(runner.Get().Status=="Completed" && runner.Get().Variables["结果"]==9 && runner.Get().LastCapture?.Success==false,"capture failure can drive explicit false branch");
        Set(P(Capture(false)));Start();Finish();Check(runner.Get().Status=="Failed" && runner.Get().ErrorCode=="RendererUnavailable","capture fail-fast aborts program");
        var renderer=captures.Register();Set(P(Capture()));Start();Tick();var work=captures.TakeWork(renderer.RendererId,renderer.Token)!;
        runner.Stop();Check(captures.Get(work.CaptureId).ErrorCode=="CaptureCancelled","stop cancels only its queued or rendering capture");
        captures.Heartbeat(renderer.RendererId,renderer.Token);
        Set(P(Capture(),B("ax_log",new{TEXT="真实帧完成"})));Start();Tick();work=captures.TakeWork(renderer.RendererId,renderer.Token)!;
        captures.Complete(renderer.RendererId,renderer.Token,work.CaptureId,Png());Tick();Finish();
        Check(runner.Get().LastCapture is {Success:true,FrameId:not null,ImageUrl:not null} && runner.Get().Status=="Completed","program capture binds matching immutable frame identity");
        captures.Heartbeat(renderer.RendererId,renderer.Token);Set(P(Capture(true,.1)));Start();Tick();Tick(.2);Finish();
        Check(runner.Get().LastCapture?.ErrorCode=="CaptureTimeout" && runner.Get().Status=="Completed","per-block capture timeout remains cancellable and typed");
        var owner=Guid.NewGuid();store.ClaimRobot(robot.Id,owner);Reject(()=>Start(),"ExternalControlActive");store.ReleaseRobot(robot.Id,owner);
        Set(P(Move(0)));var renamed=store.Get().Project.Devices.First(d=>d.Id==robot.Id);store.Update(robot.Id,renamed with{Name="改名机器人"},store.Get().Revision);
        Check(runner.Validate().Valid,"device rename keeps stable program reference");
        store.Delete(robot.Id,store.Get().Revision,true);Check(runner.Validate().Diagnostics.Any(d=>d.Code=="MissingDevice"),"deleting robot leaves explicit missing UUID reference");
        Reject(()=>Start(),"ProgramInvalid");
        File.WriteAllText(Path.Combine(root,"program6-summary.json"),JsonSerializer.Serialize(new{checks},Catalog.Json));return checks;
    }
    private static byte[] Png()
    {
        using var output=new MemoryStream();output.Write([137,80,78,71,13,10,26,10]);
        void Chunk(string name,byte[] data){var tag=Encoding.ASCII.GetBytes(name);var n=new byte[4];BinaryPrimitives.WriteInt32BigEndian(n,data.Length);output.Write(n);output.Write(tag);output.Write(data);uint crc=uint.MaxValue;foreach(var x in tag.Concat(data)){crc^=x;for(int i=0;i<8;i++)crc=(crc&1)!=0?0xedb88320^(crc>>1):crc>>1;}BinaryPrimitives.WriteUInt32BigEndian(n,crc^uint.MaxValue);output.Write(n);}
        var header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header,64);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),64);header[8]=8;header[9]=6;Chunk("IHDR",header);using var compressed=new MemoryStream();using(var zip=new ZLibStream(compressed,CompressionLevel.Fastest,true))zip.Write(new byte[64*(64*4+1)]);Chunk("IDAT",compressed.ToArray());Chunk("IEND",[]);return output.ToArray();
    }
    private sealed class ProgramClock:TimeProvider
    {
        private long ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;public override long GetTimestamp()=>ticks;
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch.AddTicks(ticks);
        public void Advance(double seconds)=>ticks+=(long)(seconds*TimeSpan.TicksPerSecond);
    }
}
