using AxisLab.Core;
using AxisLab.WpfDemo;
using System.Text.Json;
using CorePose = AxisLab.Core.Pose;

var output = Path.GetFullPath(Path.Combine("artifacts", "calibration-core", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); Console.WriteLine("PASS: " + label); }
void Reject(Action action, string label) { try { action(); } catch (InvalidOperationException) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
var store = new ProjectStore(Path.Combine(output, "projects"));
var snapshot = store.Add("robot.generic6r", store.Get().Revision);
var robot = snapshot.Project.Devices[0];
store.MoveJoints(robot.Id, [0, 10, 0, 0, -100, 0], 100, snapshot.Revision); store.AdvanceMotions(100);
Check(store.GetRuntime().Robots[0].Status == "Completed", "training robot reaches an unobstructed, upward-facing TCP");
var centre = store.GetRuntime().Robots[0].TcpBase.Pose;
snapshot = store.Add("camera.pinhole", store.Get().Revision); var camera = snapshot.Project.Devices.Last();
snapshot = store.Update(camera.Id, camera with { Name = "固定标定相机", Transform = new(centre.X, centre.Y, centre.Z + 600, Rx: 180), Parameters = camera.Parameters with { Width = 800, Height = 600, Fx = 900, Fy = 900, Cx = 400, Cy = 300 } }, snapshot.Revision);
snapshot = store.Add("calibration.marker", snapshot.Revision); var marker = snapshot.Project.Devices.Last();
snapshot = store.Update(marker.Id, marker with { Name = "TCP 零偏移标定圆点", ParentId = robot.Id, AttachmentFrameId = "tool", Transform = new() }, snapshot.Revision);
var source = snapshot.Project with { Name = "固定相机九点标定练习（六轴＋TCP圆点）" };
File.WriteAllText(Path.Combine(output, "nine-point-calibration.scene.json"), JsonSerializer.Serialize(source, Catalog.Json));
Check(ModbusProtocol.TypeCode(marker) == 22 && ModbusProtocol.Capabilities(marker) == 1, "marker is discoverable without robot or camera capabilities");
var boardSnapshot = store.Add("calibration.board", snapshot.Revision); var board = boardSnapshot.Project.Devices.Last();
Check(ModbusProtocol.TypeCode(board) == 21 && board.Parameters.SizeX == 180, "nine-dot board has stable protocol type and physical defaults");
snapshot = store.Delete(board.Id, boardSnapshot.Revision, false);
var docs = JsonSerializer.Deserialize<DeviceDocument[]>(JsonSerializer.Serialize(snapshot.Project.Devices, Catalog.Json), JsonDefaults.Web)!;
var centreTarget = new TransformInfo { X = centre.X, Y = centre.Y, Z = centre.Z, Rx = centre.Rx, Ry = centre.Ry, Rz = centre.Rz };
var points = CalibrationPlan.Grid(centreTarget, 20);
Check(points.Count == 12 && points.Count(p => !p.Validation) == 9 && points.Skip(9).All(p => p.Validation), "nine row-major calibration points and three separate validation points");
Check(points.Select(p => (p.Target.X, p.Target.Y)).Distinct().Count() == 12 && points.All(p => p.Target.Z == centre.Z), "independent points do not overlap the calibration grid and share its physical plane");
Reject(() => CalibrationPlan.Grid(centreTarget, double.NaN), "non-finite grid spacing rejected");
var plan = new CalibrationPlan { Session = snapshot.SessionId, RobotId = robot.Id, CameraId = camera.Id, MarkerId = marker.Id, Endpoint = "http://127.0.0.1:1", ConfigurationKey = CalibrationPlan.Key(docs), Directory = Path.Combine(output, "samples"), Points = points };
Reject(plan.Export, "incomplete sampling cannot be exported as a complete calibration set");
foreach (var point in points)
{
    var t = point.Target;
    var motion = new CartesianRequest(new CorePose(t.X, t.Y, t.Z, t.Rx, t.Ry, t.Rz));
    store.PreviewCartesian(robot.Id, motion); store.MoveCartesian(robot.Id, motion, store.Get().Revision); store.AdvanceMotions(100);
    var runtime = store.GetRuntime().Robots.Single();
    var wpf = JsonSerializer.Deserialize<RobotRuntime>(JsonSerializer.Serialize(runtime, Catalog.Json), JsonDefaults.Web)!;
    CalibrationPlan.CheckPose(point, wpf);
    Check(runtime.Status == "Completed", $"point {point.Number:D2}: IK, collision preview and real motion reach the target");
    point.Actual = wpf.TcpBase.Pose; point.FrameId = Guid.NewGuid(); point.Image = $"p{point.Number:D2}.png";
}
plan.Export();
Check(File.ReadAllLines(Path.Combine(plan.Directory, "points.csv")).Length == 13, "CSV keeps all measured pairs with a header and independent validation roles");
Check(!File.ReadAllText(Path.Combine(plan.Directory, "plan.json")).Contains("intrinsics", StringComparison.OrdinalIgnoreCase), "calibration export does not provide hidden camera intrinsics");
var wpfRuntime = JsonSerializer.Deserialize<AxisLab.WpfDemo.RuntimeSnapshot>(JsonSerializer.Serialize(store.GetRuntime(), Catalog.Json), JsonDefaults.Web)!;
plan.Validate(wpfRuntime, docs, plan.Endpoint, robot.Id, camera.Id);
Reject(() => plan.Validate(wpfRuntime, docs, plan.Endpoint, Guid.NewGuid(), camera.Id), "switching robots invalidates sampling");
Reject(() => plan.Validate(wpfRuntime, docs[..^1], plan.Endpoint, robot.Id, camera.Id), "removing an installation device invalidates sampling");
foreach (var p in points.Take(9)) { p.PixelX = 100; p.PixelY = 100; }
Reject(plan.Export, "duplicate or degenerate pixel points rejected");
foreach (var p in points) { p.PixelX = null; p.PixelY = null; }
var solution = Path.Combine(output, "validation-fixture.sol"); File.WriteAllText(solution, "TEST FIXTURE ONLY; NOT A VISIONMASTER SOLUTION");
var settings = new VmSettings { Solution = solution, Flow = "test", Source = "test" };
foreach (var p in points.Where(p => p.Validation))
{
    var result = new VmResult { RequestId = p.Number, CalibrationOk = true, Found = true, X = p.Actual!.X + .1, Y = p.Actual.Y + .2, FrameId = p.FrameId!.Value.ToString("D"), SettingsKey = settings.Key, SolutionHash = settings.SolutionHash };
    var otherFrame = new VmResult { RequestId = result.RequestId, CalibrationOk = true, Found = true, FrameId = Guid.NewGuid().ToString("D"), SettingsKey = settings.Key, SolutionHash = settings.SolutionHash };
    Reject(() => plan.RecordValidation(p, otherFrame, settings), "another image cannot count as an independent verification result");
    plan.RecordValidation(p, result, settings);
}
plan.Report(.5);
using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(plan.Directory, "validation-report.json")));
Check(report.RootElement.GetProperty("passed").GetBoolean() && Math.Abs(report.RootElement.GetProperty("rms").GetDouble() - Math.Sqrt(.05)) < 1e-8, "independent residual report computes RMS, maximum and threshold from measured points");
Console.WriteLine($"{checks.Count} checks passed. Artifacts: {output}");
File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { success = true, checks }, JsonDefaults.Web));
