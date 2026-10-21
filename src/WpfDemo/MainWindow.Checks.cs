using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AxisLab.WpfDemo;

public partial class MainWindow
{
    public sealed class FrameworkCheckSettings
    {
        public string Url { get; set; } = "";
        public string Directory { get; set; } = "";
        public string Solution { get; set; } = "";
        public string Flow { get; set; } = "Flow1";
        public string Source { get; set; } = "Image Source1";
    }
    public async Task RunFrameworkChecks(string settingsFile, string reportFile)
    {
        var config = JsonSerializer.Deserialize<FrameworkCheckSettings>(File.ReadAllText(settingsFile), JsonDefaults.Web)!;
        var checks = new List<string>();
        void Check(string label, bool valid) { if (!valid) throw new InvalidOperationException(label); checks.Add(label); Log("PASS " + label); File.WriteAllText(Path.Combine(config.Directory, "progress.json"), JsonSerializer.Serialize(checks)); }
        async Task Wait(Func<bool> condition, int seconds = 25)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException("WPF check timeout: " + LogBox.Text[^Math.Min(LogBox.Text.Length, 1500)..]); await Task.Delay(50); }
        }
        async Task Select(DeviceInfo device) { DeviceList!.SelectedItem = device; await Wait(() => !_isBusy && (device.IsRobot ? _connection!.RobotSession?.IsSelected == true : _selectedCamera?.Id == device.Id)); }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Screenshot(string file)
        {
            UpdateLayout(); var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(config.Directory, file)); encoder.Save(output);
        }
        Check("original white three-column WPF window is retained", new WindowInteropHelper(this).Handle != IntPtr.Zero && DeviceList is not null && JointPanel is not null && CameraImage is not null);
        AddressBox.Text = config.Url; Click(ConnectButton); await Wait(() => !_isBusy && _connection?.IsHttpConnected == true);
        Check("original connection discovers HTTP and Modbus on AxisLab 0.6", _connection!.IsModbusConnected && _devices.Count == 6 && _connection.Project!.Project.SchemaVersion == 5);
        Check("schema5 project with a long Blockly source can be read by the framework", _connection.Project!.Project.Name.Contains("框架"));
        Check("original saved-project selector remains functional", _projects.Count > 0);
        var oldSession = _connection.Project.SessionId; ProjectCombo.SelectedIndex = 0; Click(OpenProjectButton); await Wait(() => !_isBusy && _connection.Project!.SessionId != oldSession);
        Check("opening saved project changes session and clears selection", _selectedRobot is null && _selectedCamera is null);
        var robot = _devices.First(d => d.Type == AxisLabTypes.Robot3R); await Select(robot);
        Check("original robot selection creates exactly three joint rows", _joints.Count == 3 && _connection.RobotSession!.SelectedNumber == robot.Number);
        Click(AcquireButton); await Wait(() => !_isBusy && _connection.RobotSession!.IsHoldingControl);
        Check("original control acquisition and heartbeat are retained", (await _connection.ReadRobotStateAsync()).IsOwnedByThisConnection);
        await using (var other = new ExternalControlSession(_connection.Http, _connection.BaseUri!.Host, _connection.Communication!.ModbusPort))
        {
            await other.ConnectAsync(); await other.SelectDeviceAsync(robot.Number);
            var rejected = false; try { await other.AcquireControlAsync(); } catch (ModbusCommandException error) when (error.ErrorCode == 11) { rejected = true; }
            Check("a second client cannot steal manual control", rejected);
        }
        var denied = false;
        try { await _connection.Http.MoveLocalAsync(robot.Id, [10, -15, 30], 50, (await _connection.Http.GetRuntimeAsync()).ProjectRevision); }
        catch (AxisLabHttpException e) when ((int)e.StatusCode == 409) { denied = true; }
        Check("local HTTP motion is rejected during original external ownership", denied);
        _joints[0].Target = 10; SpeedSlider.Value = 50; Click(MoveButton); await Wait(() => !_isMoving);
        Check("original joint movement button drives real robot joints", Math.Abs((await _connection.ReadRobotStateAsync()).Joints[0] - 10) < .01);
        _joints[0].Target = 70; SpeedSlider.Value = 10; Click(MoveButton); await Wait(() => _isMoving); await Task.Delay(300);
        var stopTime = Stopwatch.StartNew(); Click(StopButton); await Wait(() => !_isMoving && !_isBusy);
        var stopped = await _connection.ReadRobotStateAsync();
        Check("independent Stop connection interrupts movement promptly", stopTime.Elapsed.TotalSeconds < 3 && stopped.MotionStatus == 3 && !stopped.IsOwnedByThisConnection && stopped.Joints[0] < 70);
        await Task.Delay(150); Check("stopped robot stays at its intermediate joint pose", (await _connection.ReadRobotStateAsync()).Joints.Take(3).SequenceEqual(stopped.Joints.Take(3)));
        await Select(_devices.First(d => d.IsCamera && d.ParentId is null)); AutoSaveCheckBox.IsChecked = false;
        Click(CaptureButton); await Wait(() => !_isCapturing && _lastFrame is not null && _visionContext is not null);
        Check("original camera button uses Modbus capture and fixed PNG preview", CameraImage!.Source is BitmapSource image && image.PixelWidth == _lastFrame!.Width);
        VmSampleDirectory.Text = Path.Combine(config.Directory, "samples"); _visionContext!.SaveSample(VmSampleDirectory.Text);
        var observation = File.ReadAllText(Path.Combine(VmSampleDirectory.Text, _lastFrame!.FrameId + ".observation.json"));
        Check("new training export pairs full exposure matrices without camera truth", observation.Contains("flangeBase") && observation.Contains("matrix") && !observation.Contains("intrinsics") && !observation.Contains("cameraWorld"));
        Screenshot("framework-camera.png"); VisionTabs.SelectedIndex = 1;
        VmSolutionPath.Text = config.Solution; VmFlow.Text = config.Flow; VmSource.Text = config.Source;
        await LoadVision(CancellationToken.None);
        Check("net9 framework loads actual VM SDK in its net48 worker", _vmBridge!.IsLoaded && _vmBridge.WorkerId is not null);
        var workerId = _vmBridge.WorkerId!.Value;
        Log(await _vmBridge.Verify(_lastFrame, VisionSettings(), CancellationToken.None)); Check("RGB24 image pixels survive the real cross-process SDK bridge", true);
        VmMono.IsChecked = true; Log(await _vmBridge.Verify(_lastFrame, VisionSettings(), CancellationToken.None)); Check("MONO8 image pixels survive the real cross-process SDK bridge", true);
        VmMono.IsChecked = false;
        var noResult = false; try { await RunVision(CancellationToken.None); } catch { noResult = true; }
        Check("a user flow without required inputs and outputs cannot invent a target", noResult && _vmResult is null);
        var noMotion = false; try { await PreviewVision(CancellationToken.None); } catch (InvalidOperationException) { noMotion = true; }
        Check("unbound visual result cannot pass motion preflight", noMotion && _visionPreview is null);
        // These are workflow fixtures, not claimed results of the user's unprovided calibration algorithm.
        var context = _visionContext!; var settings = VisionSettings();
        var fixture = new VmResult { RequestId = 42, CalibrationOk = true, Found = true, X = 100, Y = 20, Rz = 0, FrameId = context.Frame.FrameId.ToString("D"), SolutionHash = settings.SolutionHash, SettingsKey = settings.Key };
        var binding = VisionBinding.Create("校验夹具", context, fixture, settings);
        var current = await _connection.Http.GetRuntimeAsync(); var docs = await _connection.Http.GetDevicesAsync();
        void ValidateFixture(VmResult value) => binding.Validate(context, value, settings, current, docs, context.Endpoint);
        ValidateFixture(fixture); Check("matching workflow fixture passes binding validation without movement", true);
        void Reject(string name, Action action) { var rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(name, rejected); }
        void RejectResult(string name, Action<VmResult> edit) { var copy = JsonSerializer.Deserialize<VmResult>(JsonSerializer.Serialize(fixture))!; edit(copy); Reject(name, () => ValidateFixture(copy)); }
        RejectResult("zero request identity rejects movement", r => r.RequestId = 0);
        RejectResult("NG detection rejects movement", r => r.Found = false);
        RejectResult("invalid VM calibration rejects movement", r => r.CalibrationOk = false);
        RejectResult("another frame cannot reuse the result", r => r.FrameId = Guid.NewGuid().ToString("D"));
        RejectResult("non-finite visual coordinate rejects movement", r => r.X = double.NaN);
        RejectResult("changed VM solution rejects old result", r => r.SolutionHash = "changed");
        RejectResult("changed output mapping rejects old result", r => r.SettingsKey = "changed");
        Reject("project restart invalidates the binding", () => binding.Validate(context, fixture, settings, new RuntimeSnapshot { SessionId = Guid.NewGuid(), ProjectRevision = current.ProjectRevision }, docs, context.Endpoint));
        Reject("robot movement or scene revision invalidates old frame", () => binding.Validate(context, fixture, settings, new RuntimeSnapshot { SessionId = current.SessionId, ProjectRevision = current.ProjectRevision + 1 }, docs, context.Endpoint));
        Reject("deleted device cannot silently bind to another camera", () => binding.Validate(context, fixture, settings, current, docs.Where(d => d.Id != context.Camera.Id).ToArray(), context.Endpoint));
        foreach (var id in new[] { context.Camera.Id, context.Robot.Id })
        {
            var changed = docs.Select(d => d.Id == id ? new DeviceDocument { Id = d.Id, ConfigurationRevision = d.ConfigurationRevision + 1 } : d).ToArray();
            Reject(id == context.Camera.Id ? "camera installation change invalidates calibration" : "robot tool or geometry change invalidates calibration", () => binding.Validate(context, fixture, settings, current, changed, context.Endpoint));
        }
        VisionFrameContext CopyContext(DateTimeOffset received, bool moving) => new() { Frame = context.Frame, Camera = context.Camera, Robot = context.Robot, ExposureRobot = context.ExposureRobot, Endpoint = context.Endpoint, Session = context.Session, Revision = context.Revision, Received = received, MovingCamera = moving };
        Reject("expired image cannot authorize movement", () => binding.Validate(CopyContext(DateTimeOffset.UtcNow.AddSeconds(-31), false), fixture, settings, current, docs, context.Endpoint));
        Reject("eye-in-hand cannot take the fixed-camera shortcut", () => binding.Validate(CopyContext(context.Received, true), fixture, settings, current, docs, context.Endpoint));
        Screenshot("framework-visionmaster.png"); VisionTabs.SelectedIndex = 2; Screenshot("framework-position.png");
        await CloseVision();
        bool exited; try { using var process = Process.GetProcessById(workerId); exited = process.HasExited; } catch (ArgumentException) { exited = true; }
        Check("closing VM connection exits only the owned worker", exited);
        // Exercise the new Cartesian command using a known transport target; this is not a vision accuracy test.
        var pose = (await _connection.Http.GetRuntimeAsync()).Robots.Single(r => r.DeviceId == robot.Id).TcpBase.Pose;
        var target = new TransformInfo { X = pose.X + 10, Y = pose.Y, Z = pose.Z - 10 };
        await _connection.Http.PreviewCartesianAsync(robot.Id, target, 50, true); await _connection.AcquireRobotControlAsync();
        var moved = await _connection.RobotSession!.MoveCartesianAsync(target, 50); await _connection.ReleaseRobotControlAsync();
        Check("existing Modbus session now supports Cartesian opcode7", moved.IsSuccess && Math.Abs(moved.TcpBase[0] - target.X) < .05 && Math.Abs(moved.TcpBase[2] - target.Z) < .05);
        Check("Cartesian movement releases the original controller cleanly", !(await _connection.ReadRobotStateAsync()).IsOwnedByThisConnection);
        await Select(_devices.First(d => d.Type == AxisLabTypes.Robot6R)); Check("six-axis original panel still has six joint controls", _joints.Count == 6);
        await DisconnectCoreAsync(); Check("disconnect clears devices and stops owned sessions", _connection is null && _devices.Count == 0 && _vmBridge is null);
        File.WriteAllText(reportFile, JsonSerializer.Serialize(new { success = true, checks, workerId }, JsonDefaults.Web));
    }
}
