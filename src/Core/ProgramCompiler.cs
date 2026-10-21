using System.Text.Json;
using System.Text.RegularExpressions;

namespace AxisLab.Core;

/// <summary>Compile the persisted Blockly JSON to a bounded, typed instruction tree. Never execute generated script.</summary>
public sealed class ProgramCompiler
{
    private readonly List<ProgramDiagnostic> diagnostics=[];
    private readonly List<ProgramBlockInfo> blocks=[];
    private readonly HashSet<string> variables=[];
    private readonly ProjectDocument project;
    private ProgramCompiler(ProjectDocument project) => this.project=project;
    private static readonly HashSet<string> Allowed=["ax_start","ax_move_j","ax_move_pose","ax_teach","ax_capture","ax_wait","ax_log","ax_repeat","ax_while","ax_if","ax_set","ax_change","ax_break","ax_number","ax_boolean","ax_variable","ax_math","ax_compare","ax_logic","ax_not","ax_capture_ok"];
    public static void ValidateStructure(ProgramDefinition? definition)
    {
        if(definition is null || definition.Version!=1 || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length>80 || definition.Workspace.ValueKind!=JsonValueKind.Object)
            throw new DomainException("InvalidProgram","程序版本、名称或工作区格式无效。");
        if(System.Text.Encoding.UTF8.GetByteCount(definition.Workspace.GetRawText())>262144)throw new DomainException("ProgramTooLarge","积木工作区最多 256 KiB。");
        int count=0;var ids=new HashSet<string>();
        void Visit(JsonElement element,int depth)
        {
            if(depth>1536)throw new DomainException("InvalidProgram","积木存储嵌套过深。");
            if(element.ValueKind==JsonValueKind.Object)
            {
                if(element.TryGetProperty("type",out var type) && type.ValueKind==JsonValueKind.String)
                {
                    if(!Allowed.Contains(type.GetString()!))throw new DomainException("UnsupportedBlock","工作区含不支持的积木："+type.GetString());
                    if(++count>500)throw new DomainException("ProgramTooLarge","每个工作区最多 500 个积木（含表达式与影子块）。");
                    var id=String(element,"id");
                    if(id.Length is <1 or >100 || !ids.Add(id))throw new DomainException("InvalidProgram","积木 ID 缺失、重复或过长。");
                }
                foreach(var p in element.EnumerateObject())Visit(p.Value,depth+1);
            }
            else if(element.ValueKind==JsonValueKind.Array)foreach(var child in element.EnumerateArray())Visit(child,depth+1);
        }
        Visit(definition.Workspace,0);
    }
    private static JsonElement Property(JsonElement element,string name) => element.ValueKind==JsonValueKind.Object && element.TryGetProperty(name,out var value)?value:default;
    private static string String(JsonElement element,string name,string fallback="") {var v=Property(element,name);return v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;}
    private static JsonElement Input(JsonElement block,string name)
    {
        var input=Property(Property(block,"inputs"),name);var value=Property(input,"block");return value.ValueKind==JsonValueKind.Object?value:Property(input,"shadow");
    }
    private static string Field(JsonElement b,string name,string fallback="") => String(Property(b,"fields"),name,fallback);
    private static bool Disabled(JsonElement b)=>Property(b,"enabled").ValueKind==JsonValueKind.False || Property(b,"disabled").ValueKind==JsonValueKind.True || (Property(b,"disabledReasons") is var reasons && reasons.ValueKind==JsonValueKind.Array && reasons.GetArrayLength()>0);
    private void Error(JsonElement b,string code,string message)=>diagnostics.Add(new(String(b,"id"),code,message));
    private double Number(JsonElement b,string name,double fallback=0,double min=-1e6,double max=1e6)
    {
        var value=Property(Property(b,"fields"),name);double n;
        if(value.ValueKind==JsonValueKind.Number && value.TryGetDouble(out n) && double.IsFinite(n) && n>=min && n<=max)return n;
        Error(b,"InvalidBlockParameter",$"{name} 须为 {min} 至 {max} 的有限数值。");return fallback;
    }
    private bool Boolean(JsonElement b,string name)
    {
        var v=Property(Property(b,"fields"),name);return v.ValueKind==JsonValueKind.True || v.ValueKind==JsonValueKind.String && v.GetString()=="TRUE";
    }
    private string Variable(JsonElement b)
    {
        var name=Field(b,"NAME").Trim();
        if(!Regex.IsMatch(name,@"^[\p{L}_][\p{L}\p{N}_]{0,39}$"))Error(b,"InvalidVariable","变量名称须为字母、汉字或下划线开头，最长 40 字。");
        variables.Add(name);if(variables.Count>64)Error(b,"TooManyVariables","最多支持 64 个数值变量。");return name;
    }
    private Guid Device(JsonElement b,string field,bool robot)
    {
        var text=Field(b,field);Guid.TryParse(text,out var id);var d=project.Devices.FirstOrDefault(d=>d.Id==id);
        if(d is null || (robot?d.Robot is null:d.TypeId!="camera.pinhole"))Error(b,"MissingDevice",robot?"请选择有效的机械臂，原设备可能已删除。":"请选择有效的相机，原设备可能已删除。");
        return id;
    }
    private ProgramExpression Expr(JsonElement b,string expected,int depth=0)
    {
        if(b.ValueKind!=JsonValueKind.Object || depth>24){Error(b,"MissingInput","缺少表达式或表达式嵌套过深。");return new("literal",expected);}
        if(Disabled(b))Error(b,"DisabledBlock","表达式已禁用，请重新启用或删除。");
        var type=String(b,"type");ProgramExpression e;
        ProgramExpression Child(string name,string t)=>Expr(Input(b,name),t,depth+1);
        string Op(string[] allowed,string fallback){var value=Field(b,"OP");if(!allowed.Contains(value)){Error(b,"InvalidOperator","表达式运算符无效。");return fallback;}return value;}
        e=type switch
        {
            "ax_number"=>new("literal","number",Number(b,"NUM")),
            "ax_boolean"=>new("literal","boolean",Boolean:Boolean(b,"BOOL")),
            "ax_variable"=>new("variable","number",Name:Variable(b)),
            "ax_capture_ok"=>new("capture","boolean"),
            "ax_math"=>new("math","number",Operator:Op(["ADD","SUB","MUL","DIV"],"ADD"),Left:Child("A","number"),Right:Child("B","number")),
            "ax_compare"=>new("compare","boolean",Operator:Op(["EQ","NE","LT","LE","GT","GE"],"EQ"),Left:Child("A","number"),Right:Child("B","number")),
            "ax_logic"=>new("logic","boolean",Operator:Op(["AND","OR"],"AND"),Left:Child("A","boolean"),Right:Child("B","boolean")),
            "ax_not"=>new("not","boolean",Left:Child("VALUE","boolean")),
            _=>new("invalid",expected)
        };
        if(e.Kind=="invalid" || e.Type!=expected)Error(b,"ExpressionType",$"此输入需要 {(expected=="number"?"数值":"布尔条件")} 表达式。");
        return e;
    }
    private ProgramInstruction[] Chain(JsonElement start,int depth)
    {
        var result=new List<ProgramInstruction>();var b=start;
        while(b.ValueKind==JsonValueKind.Object)
        {
            if(depth>24){Error(b,"ProgramDepth","流程最多嵌套 24 层。");break;}
            var id=String(b,"id");var type=String(b,"type");
            if(Disabled(b))
            {Error(b,"DisabledBlock","请删除或重新启用禁用积木后执行。");}
            var node=new ProgramInstruction(id,type);blocks.Add(new(id,type,depth));
            switch(type)
            {
                case "ax_move_j":
                    var robot=Device(b,"ROBOT",true);var count=project.Devices.FirstOrDefault(d=>d.Id==robot)?.Robot?.JointPositions.Length??6;
                    var q=Enumerable.Range(1,6).Select(i=>Number(b,"J"+i)).ToArray();
                    if(count==3 && q.Skip(3).Any(v=>v!=0))Error(b,"InvalidJoints","三轴机器人未使用的 J4—J6 必须为零。");
                    if(project.Devices.FirstOrDefault(d=>d.Id==robot)?.Robot is {} settings)
                        try{RobotModels.ValidateJoints(settings.ModelId,q.Take(count).ToArray());}catch(DomainException e){Error(b,e.Code,e.Message);}
                    node=node with{DeviceId=robot,Joints=q.Take(count).ToArray(),Speed=Number(b,"SPEED",25,1,100),Timeout=Number(b,"TIMEOUT",120,.1,300)};break;
                case "ax_move_pose":
                    var frame=Field(b,"FRAME");var kind=Field(b,"KIND");
                    if(frame is not("base" or "world" or "work") || kind is not("MoveJ" or "MoveL"))Error(b,"InvalidMotion","坐标系或运动类型无效。");
                    node=node with{DeviceId=Device(b,"ROBOT",true),Motion=new(new(Number(b,"X"),Number(b,"Y"),Number(b,"Z"),Number(b,"RX"),Number(b,"RY"),Number(b,"RZ")),frame,kind,Number(b,"SPEED",25,1,100),Boolean(b,"POSITION")),Timeout=Number(b,"TIMEOUT",120,.1,300)};break;
                case "ax_teach":
                    var owner=Device(b,"ROBOT",true);Guid.TryParse(Field(b,"POINT"),out var pointId);
                    var point=project.TeachPoints?.FirstOrDefault(p=>p.Id==pointId && p.RobotId==owner);
                    if(point is null)Error(b,"MissingTeachPoint","所选示教点不存在或属于另一台机器人。");
                    var moveKind=Field(b,"KIND");if(moveKind is not("MoveJ" or "MoveL"))Error(b,"InvalidMotion","示教运动类型无效。");
                    node=node with{DeviceId=owner,PointId=pointId,Kind=moveKind,Speed=Number(b,"SPEED",25,1,100),Timeout=Number(b,"TIMEOUT",120,.1,300)};break;
                case "ax_capture":node=node with{DeviceId=Device(b,"CAMERA",false),AllowFailure=Boolean(b,"CONTINUE"),Timeout=Number(b,"TIMEOUT",15,.1,120)};break;
                case "ax_wait":node=node with{Expression=Expr(Input(b,"SECONDS"),"number")};break;
                case "ax_repeat":node=node with{Expression=Expr(Input(b,"COUNT"),"number"),Body=Chain(Input(b,"BODY"),depth+1)};break;
                case "ax_while":node=node with{Expression=Expr(Input(b,"CONDITION"),"boolean"),Body=Chain(Input(b,"BODY"),depth+1)};break;
                case "ax_if":node=node with{Expression=Expr(Input(b,"CONDITION"),"boolean"),Body=Chain(Input(b,"BODY"),depth+1),Else=Chain(Input(b,"ELSE"),depth+1)};break;
                case "ax_set":case "ax_change":node=node with{Name=Variable(b),Expression=Expr(Input(b,"VALUE"),"number")};break;
                case "ax_log":var text=Field(b,"TEXT");if(text.Length>200)Error(b,"InvalidText","日志文本最长 200 字。");node=node with{Text=text};break;
                case "ax_break":break;
                default:Error(b,"UnsupportedStatement","此积木不能作为流程指令执行。请接入开始块。 ");break;
            }
            result.Add(node);b=Property(Property(b,"next"),"block");
        }
        return result.ToArray();
    }
    internal static CompiledProgram Compile(ProjectDocument project)
    {
        var c=new ProgramCompiler(project);var definition=project.Program??ProgramDefinition.Empty();ValidateStructure(definition);
        var roots=Property(Property(definition.Workspace,"blocks"),"blocks");var body=Array.Empty<ProgramInstruction>();
        if(roots.ValueKind!=JsonValueKind.Array || roots.GetArrayLength()!=1 || String(roots[0],"type")!="ax_start")
            c.diagnostics.Add(new(null,"MissingStart","程序需要且只能有一个顶层“开始”块，其他积木须连接到流程中。"));
        else
        {
            var root=roots[0];c.blocks.Add(new(String(root,"id"),"ax_start",0));body=c.Chain(Input(root,"BODY"),1);
            if(Disabled(root))c.Error(root,"DisabledBlock","开始块已禁用。");
            if(body.Length==0)c.Error(root,"EmptyProgram","开始块中尚未连接执行指令。");
            if(Property(Property(root,"next"),"block").ValueKind==JsonValueKind.Object)c.Error(root,"InvalidStart","开始块只允许 BODY 输入。");
        }
        return new(new(c.diagnostics.Count==0,c.diagnostics.ToArray(),c.blocks.ToArray()),body,c.variables.ToArray());
    }
    public static ProgramValidation Validate(ProjectDocument project) => Compile(project).Validation;
}
