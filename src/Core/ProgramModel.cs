using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxisLab.Core;

public record ProgramDefinition(int Version, string Name, [property:JsonConverter(typeof(ProgramWorkspaceConverter))] JsonElement Workspace)
{
    public static ProgramDefinition Empty() => new(1,"主程序",JsonSerializer.SerializeToElement(new { blocks = new { languageVersion=0, blocks=Array.Empty<object>() } }));
}
/// <summary>Blockly uses linked next.block records; keep source compact even in indented project files.</summary>
public sealed class ProgramWorkspaceConverter:JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {using var document=JsonDocument.ParseValue(ref reader);return document.RootElement.Clone();}
    public override void Write(Utf8JsonWriter writer,JsonElement value,JsonSerializerOptions options)=>writer.WriteRawValue(value.GetRawText(),skipInputValidation:true);
}
public record ProgramDiagnostic(string? BlockId, string Code, string Message);
public record ProgramBlockInfo(string Id, string Operation, int Depth);
public record ProgramValidation(bool Valid, ProgramDiagnostic[] Diagnostics, ProgramBlockInfo[] Blocks);
internal record ProgramExpression(string Kind, string Type, double Number=0, bool Boolean=false, string? Name=null,
    string? Operator=null, ProgramExpression? Left=null, ProgramExpression? Right=null);
internal record ProgramInstruction(string Id, string Operation)
{
    public Guid DeviceId { get; init; }
    public Guid PointId { get; init; }
    public double[] Joints { get; init; } = [];
    public CartesianRequest? Motion { get; init; }
    public double Speed { get; init; } = 25;
    public double Timeout { get; init; } = 120;
    public bool AllowFailure { get; init; }
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    public string Kind { get; init; } = "MoveJ";
    public ProgramExpression? Expression { get; init; }
    public ProgramInstruction[] Body { get; init; } = [];
    public ProgramInstruction[] Else { get; init; } = [];
}
internal record CompiledProgram(ProgramValidation Validation, ProgramInstruction[] Body, string[] Variables);

public sealed partial class ProjectStore
{
    private Guid? programReservation;
    private bool programStopRequested;
    public bool ProgramStopRequested(Guid id) { lock(gate) return programReservation != id || programStopRequested; }
    public void ReserveProgram(Guid id, long expected)
    {
        lock(gate) { Check(expected); EnsureIdle(); programReservation=id;programStopRequested=false; }
    }
    public void EndProgram(Guid id)
    {
        lock(gate)
        {
            if(programReservation!=id)return;
            var active=motions.Values.Where(m=>m.Status=="Running").ToArray();
            foreach(var m in active)m.Status="Cancelled";
            if(active.Length>0){revision++;dirty=true;}
            programReservation=null; programStopRequested=false;
        }
    }
    public WorkspaceSnapshot SetProgram(ProgramDefinition definition,long expected)
    {
        lock(gate) { Check(expected); ProgramCompiler.ValidateStructure(definition); return Commit(project with { Program=definition }); }
    }
    private void CheckProgramController(Guid? owner)
    {
        if(programReservation.HasValue && programReservation!=owner)
            throw new DomainException("ProgramActive","积木程序正在运行或暂停，请先停止程序。",409);
    }
}
