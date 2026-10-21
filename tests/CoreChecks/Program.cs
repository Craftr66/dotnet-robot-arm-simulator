using AxisLab.Core;
using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

var root = Path.GetFullPath(Path.Combine("artifacts", "core-checks", Guid.NewGuid().ToString("N")));
var store = new ProjectStore(root);
var checks = 0;
void Assert(bool okay, string name)
{
    if (!okay) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); checks++;
}
void Reject(Action action, string code)
{
    try { action(); throw new Exception("Expected rejection: " + code); }
    catch (DomainException ex) { Assert(ex.Code == code, code); }
}
var initial = store.Get();
Assert(initial.Project.Devices.Length == 0 && !initial.Dirty && !initial.HasSavedFile, "new workspace is empty and not shown as saved");
var cameraState = store.Add("camera.pinhole", initial.Revision);
var camera = cameraState.Project.Devices.Single();
Assert(camera.ParentId is null, "camera can exist without a robot");
Reject(() => store.Add("robot.generic6r", initial.Revision), "RevisionConflict");
var state = store.Add("robot.generic6r", cameraState.Revision);
var robot = state.Project.Devices.Last();
state = store.Update(camera.Id, camera with { ParentId = robot.Id }, state.Revision);
Reject(() => store.Update(robot.Id, robot with { ParentId = camera.Id }, state.Revision), "HierarchyCycle");
Reject(() => store.Update(camera.Id, camera with { ParentId = Guid.NewGuid() }, state.Revision), "ParentNotFound");
Reject(() => store.Update(camera.Id, camera with { DeviceNumber = 99 }, state.Revision), "ImmutableIdentity");
Reject(() => store.Delete(robot.Id, state.Revision, false), "HasChildren");
Reject(() => store.Update(camera.Id, camera with { Parameters = camera.Parameters with { Fx = 0 } }, state.Revision), "InvalidParameters");
Reject(() => store.Update(camera.Id, camera with { Transform = new Pose(double.NaN) }, state.Revision), "InvalidTransform");
Assert(store.Get().Revision == state.Revision, "failed operations do not change revision");
state = store.Duplicate(camera.Id, state.Revision);
var copy = state.Project.Devices.Last();
Assert(copy.Id != camera.Id && copy.DeviceNumber != camera.DeviceNumber && copy.ParentId == robot.Id,
    "duplicate has fresh identity and preserves parent");
var saved = store.Save("标定练习工作区", state.Revision);
Assert(!saved.Dirty && saved.HasSavedFile && Directory.GetFiles(root, "*.scene.json").Length == 1, "atomic project save");
var savedIds = saved.Project.Devices.Select(d => (d.Id, d.DeviceNumber)).ToArray();
var sameFile = Path.Combine(root, saved.Project.Id + ".scene.json");
var exact = File.ReadAllText(sameFile);
var reopenedStore = new ProjectStore(root);
var opened = reopenedStore.Open(saved.Project.Id.ToString("D"), reopenedStore.Get().Revision);
Assert(opened.Project.Devices.Select(d => (d.Id, d.DeviceNumber)).SequenceEqual(savedIds), "disk reopen preserves IDs and numbers");
Assert(opened.Project.Devices.Single(d => d.Id == camera.Id).ParentId == robot.Id, "disk reopen preserves mount");
Assert(opened.SessionId != saved.SessionId && !opened.Dirty, "reopen uses a new runtime session");
Reject(() => store.Open("../../outside", saved.Revision), "InvalidFileId");
var invalid = saved.Project with { SchemaVersion = 999 };
Reject(() => store.Import(invalid, saved.Revision), "UnsupportedSchema");
Reject(() => store.Import(saved.Project with { Devices = [camera, camera] }, saved.Revision), "DuplicateIdentity");
Reject(() => store.Import(saved.Project with { NextDeviceNumber = 1 }, saved.Revision), "InvalidNumber");
Reject(() => store.Import(null!, saved.Revision), "InvalidProject");
Reject(() => store.Update(camera.Id, null!, saved.Revision), "InvalidDevice");
Reject(() => store.Save(null!, saved.Revision), "InvalidProject");
Assert(File.ReadAllText(sameFile) == exact, "failed save preserves the existing file");
var deleted = store.Delete(robot.Id, saved.Revision, true);
Assert(deleted.Project.Devices.Length == 0, "cascade delete removes descendants");
var next = store.Add("geometry.box", deleted.Revision);
Assert(next.Project.Devices[0].DeviceNumber > copy.DeviceNumber, "deleted device numbers are not reused");
File.WriteAllText(Path.Combine(root, Guid.NewGuid() + ".scene.json"), "not json");
Assert(store.ListFiles().Length == 1, "file picker skips malformed JSON");
var imported = JsonSerializer.Deserialize<ProjectDocument>(exact, Catalog.Json)!;
Assert(imported.Devices.Length == 3, "export format round trip");
bool Near(double a, double b, double tolerance = 1e-8) => Math.Abs(a - b) < tolerance;
void Position(CoordinateFrame frame, double x, double y, double z, string name) =>
    Assert(Near(frame.Pose.X, x) && Near(frame.Pose.Y, y) && Near(frame.Pose.Z, z), name);
var zero = Generic6R.Forward([0, 0, 0, 0, 0, 0], new(0, 0, 80));
Position(zero.Flange, 485, 0, 545, "analytic zero flange position");
Position(zero.Tcp, 565, 0, 545, "analytic zero TCP position");
Position(Generic6R.Forward([90, 0, 0, 0, 0, 0], new(0, 0, 80)).Tcp, 0, 565, 545, "J1 rotates arm around base Z");
Position(Generic6R.Forward([0, 90, 0, 0, 0, 0], new(0, 0, 80)).Tcp, 300, 0, -320, "J2 analytical right-angle configuration");
Position(Generic6R.Forward([0, 0, 90, 0, 0, 0], new(0, 0, 80)).Tcp, 0, 0, -20, "J3 analytical right-angle configuration");
Position(Generic6R.Forward([0, 0, 0, 0, 0, 0], new(10, 20, 30)).Tcp, 515, 20, 535, "tool offset uses flange orientation");
var random = new Random(3426);
var rigidOkay = true;
var eulerOkay = true;
for (var sample = 0; sample < 100; sample++)
{
    var q = Generic6R.Definitions.Select(d => d.Minimum + random.NextDouble() * (d.Maximum - d.Minimum)).ToArray();
    var frame = Generic6R.Forward(q, new(0, 0, 80)).Tcp;
    var m = frame.Matrix;
    for (var column = 0; column < 3; column++)
        for (var other = 0; other < 3; other++)
            rigidOkay &= Near(Enumerable.Range(0, 3).Sum(row => m[column * 4 + row] * m[other * 4 + row]), column == other ? 1 : 0);
    var determinant = m[0] * (m[5] * m[10] - m[9] * m[6]) - m[4] * (m[1] * m[10] - m[9] * m[2]) + m[8] * (m[1] * m[6] - m[5] * m[2]);
    rigidOkay &= Near(determinant, 1) && m[15] == 1;
    eulerOkay &= RigidTransform.FromPose(frame.Pose).Values.Zip(m).All(v => Near(v.First, v.Second));
}
Assert(rigidOkay, "100 sampled FK rotations remain rigid and right handed");
Assert(eulerOkay, "100 FK Euler displays reconstruct the authoritative matrices");
foreach (var y in new[] { -90d, 90d })
{
    var singular = RigidTransform.FromPose(new(1, 2, 3, 38, y, -65));
    Assert(RigidTransform.FromPose(singular.Frame().Pose).Values.Zip(singular.Values).All(v => Near(v.First, v.Second)), "Euler singular display preserves matrix at pitch " + y);
}
var movementRoot = Path.Combine(root, "movement");
var movement = new ProjectStore(movementRoot);
var robotWorkspace = movement.Add("robot.generic6r", movement.Get().Revision);
var movingRobot = robotWorkspace.Project.Devices[0];
var configurationRevision = movingRobot.ConfigurationRevision;
Reject(() => movement.MoveJoints(movingRobot.Id, [181, 0, 0, 0, 0, 0], 25, robotWorkspace.Revision), "JointLimit");
Reject(() => movement.MoveJoints(movingRobot.Id, [0, 0], 25, robotWorkspace.Revision), "InvalidJoints");
Reject(() => movement.MoveJoints(movingRobot.Id, [double.NaN, 0, 0, 0, 0, 0], 25, robotWorkspace.Revision), "InvalidJoints");
Reject(() => movement.MoveJoints(movingRobot.Id, [0, 0, 0, 0, 0, 0], 0, robotWorkspace.Revision), "InvalidSpeed");
Reject(() => movement.MoveJoints(movingRobot.Id, [180, 0, 0, 0, 0, 0], 1, robotWorkspace.Revision), "MotionTooLong");
var goal = new double[] { 90, 0, 0, 0, 0, 0 };
var started = movement.MoveJoints(movingRobot.Id, goal, 50, robotWorkspace.Revision);
goal[0] = -90;
Reject(() => movement.MoveJoints(movingRobot.Id, [0, 0, 0, 0, 0, 0], 50, started.Revision), "RobotBusy");
Reject(() => movement.Save("moving", started.Revision), "MotionActive");
Reject(() => movement.New(started.Revision, "new"), "MotionActive");
Reject(() => movement.Delete(movingRobot.Id, started.Revision, false), "MotionActive");
movement.AdvanceMotions(0.5);
Assert(Near(movement.GetRuntime().Robots[0].Joints[0], 90*MotionMath.Smooth(.5/3.75)), "quintic interpolation at a known time");
Assert(movement.Get().Project.Devices[0].ConfigurationRevision == configurationRevision, "motion does not change mounting configuration version");
movement.AdvanceMotions(0.5);
var stopped = movement.StopRobot(movingRobot.Id);
var stoppedAngle = stopped.Runtime.Robots[0].Joints[0];
movement.AdvanceMotions(100);
Assert(Near(stoppedAngle, 90*MotionMath.Smooth(1/3.75)) && Near(movement.GetRuntime().Robots[0].Joints[0], stoppedAngle), "stop cancels and preserves intermediate joint position");
var stoppedSaved = movement.Save("运动后保存", stopped.Revision);
var disk = new ProjectStore(movementRoot);
var diskOpen = disk.Open(stoppedSaved.Project.Id.ToString("D"), disk.Get().Revision);
Assert(Near(diskOpen.Runtime.Robots[0].Joints[0], stoppedAngle) && diskOpen.Runtime.Robots[0].Status == "Idle" && diskOpen.Runtime.Robots[0].CommandId is null,
    "saved joint pose reopens without restoring a running command");
var cloned = movement.Duplicate(movingRobot.Id, movement.Get().Revision);
Assert(Near(cloned.Runtime.Robots.Last().Joints[0], stoppedAngle), "duplicate starts at current pose with fresh runtime");
var withCamera = movement.Add("camera.pinhole", cloned.Revision);
var handCamera = withCamera.Project.Devices.Last();
var mountedState = movement.Update(handCamera.Id, handCamera with { ParentId = movingRobot.Id, AttachmentFrameId = "flange", Transform = new(10, 20, 30) }, withCamera.Revision);
var still = movement.Add("camera.pinhole", mountedState.Revision);
var fixedCamera = still.Project.Devices.Last();
var fixedBefore = still.Runtime.Devices.Single(d => d.DeviceId == fixedCamera.Id).World;
var mountingAtBase = movement.Add("camera.pinhole", still.Revision);
var baseCamera = mountingAtBase.Project.Devices.Last();
var beforeMove = movement.Update(baseCamera.Id, baseCamera with { ParentId = movingRobot.Id, Transform = new(20, 30, 40) }, mountingAtBase.Revision);
movement.MoveJoints(movingRobot.Id, [90, 0, 0, 0, 0, 0], 100, beforeMove.Revision);
movement.AdvanceMotions(10);
var finishedRuntime = movement.GetRuntime();
Position(finishedRuntime.Devices.Single(d => d.DeviceId == handCamera.Id).World, -20, 515, 535, "flange mounted camera follows analytical endpoint");
Assert(finishedRuntime.Devices.Single(d => d.DeviceId == fixedCamera.Id).World.Matrix.Zip(fixedBefore.Matrix).All(v => Near(v.First, v.Second)), "independent camera remains still");
Position(finishedRuntime.Devices.Single(d => d.DeviceId == baseCamera.Id).World, 20, 30, 40, "base mounted camera does not follow joints");
Assert(finishedRuntime.Robots.Single(r => r.DeviceId == movingRobot.Id).Status == "Completed" && Near(finishedRuntime.Robots[0].Joints[0], 90), "completion reaches cloned target without drift");
var movedBase = movement.Get();
var currentRobot = movedBase.Project.Devices.Single(d => d.Id == movingRobot.Id);
var moved = movement.Update(movingRobot.Id, currentRobot with { Transform = new(100, 200, 300, Rz: 90) }, movedBase.Revision);
Position(moved.Runtime.Devices.Single(d => d.DeviceId == handCamera.Id).World, -415, 180, 835, "base installation rotation propagates to mounted camera");
Reject(() => movement.Update(handCamera.Id, moved.Project.Devices.Single(d => d.Id == handCamera.Id) with { ParentId = fixedCamera.Id }, moved.Revision), "InvalidAttachment");
var runtimeBypass = currentRobot with { Robot = currentRobot.Robot! with { JointPositions = [0, 0, 0, 0, 0, 0] } };
Reject(() => movement.Update(currentRobot.Id, runtimeBypass, moved.Revision), "UseMotionCommand");
var legacy = saved.Project with { SchemaVersion = 1, Devices = saved.Project.Devices.Select(d => d with { Robot = null, AttachmentFrameId = "base" }).ToArray() };
var legacyStore = new ProjectStore(Path.Combine(root, "legacy"));
var migrated = legacyStore.Import(legacy, legacyStore.Get().Revision);
Assert(migrated.Project.SchemaVersion == 5 && migrated.Project.Devices.Select(d => d.Id).SequenceEqual(legacy.Devices.Select(d => d.Id)), "legacy schema1 migrates without changing IDs");
Assert(migrated.Project.Devices.Where(d => d.TypeId == "robot.generic6r").All(d => d.Robot?.ModelId == Generic6R.ModelId), "legacy robots receive valid kinematic defaults");
Position(Generic6R.Forward([0, 0, 0, 0, 0, 0], new(0, 0, 80), new(UpperArm: 420)).Tcp, 565, 0, 665, "longer upper arm changes analytic TCP height");
Position(Generic6R.Forward([0, 0, 0, 0, 0, 0], new(0, 0, 80), new(Forearm: 380)).Tcp, 665, 0, 545, "longer forearm changes analytic TCP reach");
Position(Generic6R.Forward([0, 0, 0, 0, 0, 0], new(0, 0, 80), new(Joint1Diameter: 250)).Tcp, 565, 0, 545, "joint diameters do not change kinematics");
Reject(() => Generic6R.ValidateGeometry(new(UpperArm: double.NaN)), "InvalidGeometry");
Reject(() => Generic6R.ValidateGeometry(new(BaseHeight: 39)), "InvalidGeometry");
Reject(() => Generic6R.ValidateGeometry(new(Joint6Diameter: 501)), "InvalidGeometry");
var geometryStore = new ProjectStore(Path.Combine(root, "geometry"));
var geo = geometryStore.Add("robot.generic6r", geometryStore.Get().Revision);
var geoRobot = geo.Project.Devices[0];
geo = geometryStore.Update(geoRobot.Id, geoRobot with { Robot = geoRobot.Robot! with { Geometry = new(UpperArm: 420, Joint2Diameter: 150) } }, geo.Revision);
Position(geo.Runtime.Robots[0].TcpBase, 565, 0, 665, "updated geometry reaches authoritative runtime");
geo = geometryStore.Save("尺寸保存", geo.Revision);
var geoOpen = geometryStore.Open(geo.Project.Id.ToString(), geo.Revision);
Assert(geoOpen.Project.Devices[0].Robot!.Geometry == new RobotGeometry(UpperArm: 420, Joint2Diameter: 150), "dimensions survive disk save and reopen");
var version2 = geo.Project with { SchemaVersion = 2, Devices = geo.Project.Devices.Select(d => d with { Robot = d.Robot! with { Geometry = null } }).ToArray() };
var upgrade2 = geometryStore.Import(version2, geoOpen.Revision);
Assert(upgrade2.Project.SchemaVersion == 5 && upgrade2.Project.Devices[0].Id == geoRobot.Id && upgrade2.Project.Devices[0].Robot!.Geometry == new RobotGeometry(), "schema2 receives dimensions with stable identity");
geometryStore.MoveJoints(geoRobot.Id, [90, 0, 0, 0, 0, 0], 50, upgrade2.Revision);
Reject(() => geometryStore.Update(geoRobot.Id, upgrade2.Project.Devices[0], geometryStore.Get().Revision), "MotionActive");

// Valid compressed PNG fixtures exercise the server's actual decoding boundaries.
byte[] Png(int width = 640, int height = 480, bool invalidFilter = false)
{
    using var output = new MemoryStream(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    void Chunk(string kind, byte[] data)
    {
        var name = Encoding.ASCII.GetBytes(kind); var length = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length); output.Write(name); output.Write(data);
        uint crc = 0xffffffff;
        foreach (var b in name.Concat(data)) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
        BinaryPrimitives.WriteUInt32BigEndian(length, crc ^ 0xffffffff); output.Write(length);
    }
    var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    header[8] = 8; header[9] = 6; Chunk("IHDR", header);
    using var data = new MemoryStream();
    using (var z = new ZLibStream(data, CompressionLevel.Fastest, true))
        for (var y = 0; y < height; y++) { z.WriteByte(invalidFilter ? (byte)5 : (byte)0); z.Write(new byte[width * 4]); }
    Chunk("IDAT", data.ToArray()); Chunk("IEND", []); return output.ToArray();
}
var png = Png(); PngValidation.Validate(png, 640, 480);
Assert(true, "valid PNG passes bounded decompression and CRC checks");
Reject(() => PngValidation.Validate(png, 320, 480), "InvalidPng");
var corrupt = png.ToArray(); corrupt[40] ^= 1;
Reject(() => PngValidation.Validate(corrupt, 640, 480), "InvalidPng");
Reject(() => PngValidation.Validate(png[..^5], 640, 480), "InvalidPng");
Reject(() => PngValidation.Validate(Png(invalidFilter: true), 640, 480), "InvalidPng");

var captureStore = new ProjectStore(Path.Combine(root, "captures"));
var cams = captureStore.Add("camera.pinhole", captureStore.Get().Revision);
var cam1 = cams.Project.Devices[0];
cams = captureStore.Add("camera.pinhole", cams.Revision); var cam2 = cams.Project.Devices[1];
cams = captureStore.Add("robot.generic6r", cams.Revision); var photographedRobot = cams.Project.Devices[2];
cams = captureStore.Update(cam1.Id, cam1 with { ParentId = photographedRobot.Id, AttachmentFrameId = "flange" }, cams.Revision);
var time = new ManualClock(); var captures = new CaptureService(captureStore, time, framesPerCamera: 2);
Reject(() => captures.Trigger(cam1.Id), "RendererUnavailable");
Reject(() => captures.Trigger(photographedRobot.Id), "NotCamera");
var renderer = captures.Register();
Reject(() => captures.Register(), "RendererBusy");
Reject(() => captures.TakeWork(renderer.RendererId, "wrong-token"), "RendererUnauthorized");
var firstCapture = captures.Trigger(cam1.Id);
Reject(() => captures.Trigger(cam1.Id), "CameraBusy");
var secondCapture = captures.Trigger(cam2.Id);
Assert(firstCapture.CaptureId != secondCapture.CaptureId, "two cameras queue independent capture identities");
var work = captures.TakeWork(renderer.RendererId, renderer.Token)!;
Assert(work.CaptureId == firstCapture.CaptureId && captures.TakeWork(renderer.RendererId, renderer.Token) is null, "one leased renderer consumes FIFO work serially");
captureStore.MoveJoints(photographedRobot.Id, [90, 0, 0, 0, 0, 0], 100, captureStore.Get().Revision);
captureStore.AdvanceMotions(.25);
var finishedCapture = captures.Complete(renderer.RendererId, renderer.Token, work.CaptureId, png);
var firstFrame = finishedCapture.FrameId!.Value;
var metadata = captures.Metadata(cam1.Id, firstFrame);
Assert(metadata.Robots[0].Joints[0] == 0 && captureStore.GetRuntime().Robots[0].Joints[0] > 0 && metadata.CameraWorld.Matrix.SequenceEqual(work.Snapshot.Runtime.Devices.Single(d => d.DeviceId == cam1.Id).World.Matrix), "capture freezes robot pose and mounted camera at exposure despite later motion");
Assert(metadata.Sha256 == Convert.ToHexString(SHA256.HashData(png)) && captures.Image(cam1.Id, firstFrame).Bytes.SequenceEqual(png), "metadata SHA and binary image refer to the same immutable frame");
Reject(() => captures.Image(cam2.Id, firstFrame), "FrameNotFound");
Reject(() => captures.Complete(renderer.RendererId, renderer.Token, work.CaptureId, png), "CaptureNotActive");
var otherWork = captures.TakeWork(renderer.RendererId, renderer.Token)!;
Assert(otherWork.CaptureId == secondCapture.CaptureId, "second camera is not confused with the first camera result");
Reject(() => captures.Complete(renderer.RendererId, renderer.Token, otherWork.CaptureId, corrupt), "InvalidPng");
Assert(captures.Get(secondCapture.CaptureId).Status == "Failed", "invalid image upload fails capture and releases camera busy state");
CaptureState Capture(Guid id)
{
    var state = captures.Trigger(id); var item = captures.TakeWork(renderer.RendererId, renderer.Token)!;
    return captures.Complete(renderer.RendererId, renderer.Token, item.CaptureId, png);
}
var secondFrame = Capture(cam1.Id).FrameId!.Value;
var retainedBytes = captures.Image(cam1.Id, firstFrame).Bytes;
var thirdFrame = Capture(cam1.Id).FrameId!.Value;
Reject(() => captures.Image(cam1.Id, firstFrame), "FrameGone");
Assert(captures.Metadata(cam1.Id, secondFrame).FrameId == secondFrame && retainedBytes.SequenceEqual(png), "eviction retains recent frames and does not interrupt an in-progress read");
captureStore.StopAllRobots();
var changing = captures.Trigger(cam1.Id);
var changeState = captureStore.Get(); var cameraToChange = changeState.Project.Devices.Single(d => d.Id == cam1.Id);
captureStore.Update(cam1.Id, cameraToChange with { Name = "改动配置" }, changeState.Revision);
Assert(captures.Get(changing.CaptureId).ErrorCode == "SceneChanged", "scene edits cancel pending captures instead of returning a mismatched image");
var timeout = captures.Trigger(cam1.Id);
for (var i = 0; i < 4; i++) { time.Advance(3.1); captures.Heartbeat(renderer.RendererId, renderer.Token); }
Assert(captures.Get(timeout.CaptureId).ErrorCode == "CaptureTimeout", "capture deadline fails while renderer heartbeat remains healthy");
var disconnect = captures.Trigger(cam1.Id); time.Advance(4.1);
Assert(captures.Get(disconnect.CaptureId).ErrorCode == "RendererDisconnected", "renderer lease loss fails active captures");
Reject(() => captures.Trigger(cam1.Id), "RendererUnavailable");
renderer = captures.Register();
var recovery = Capture(cam1.Id);
Assert(recovery.Status == "Completed", "renderer can reconnect and capture again after disconnect");
captureStore.New(captureStore.Get().Revision, "新工程");
Reject(() => captures.Image(cam1.Id, thirdFrame), "FrameGone");

// A small byte budget tests global eviction separately from per-camera retention.
var budgetStore = new ProjectStore(Path.Combine(root, "budget")); var budgetCam = budgetStore.Add("camera.pinhole", budgetStore.Get().Revision).Project.Devices[0];
var budget = new CaptureService(budgetStore, maximumCacheBytes: png.Length + 1); var br = budget.Register();
Guid BudgetFrame() { var s = budget.Trigger(budgetCam.Id); budget.TakeWork(br.RendererId, br.Token); return budget.Complete(br.RendererId, br.Token, s.CaptureId, png).FrameId!.Value; }
var evicted = BudgetFrame(); var kept = BudgetFrame();
Reject(() => budget.Image(budgetCam.Id, evicted), "FrameGone");
Assert(budget.Image(budgetCam.Id, kept).Bytes.Length == png.Length, "global image byte budget evicts the oldest frame");
checks += MotionChecks.Run(root);
checks += ProgramChecks.Run(root);
Console.WriteLine($"{checks} core checks passed. Test artifacts: {root}");

sealed class ManualClock : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
    public void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
}
