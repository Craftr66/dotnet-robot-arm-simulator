using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AxisLab.VisionDemo
{
    public partial class MainWindow : Window
    {
        private AxisClient axis;
        private VisionService vision;
        private Frame frame;
        private VisionResult result;
        private CalibrationBinding binding;
        private CancellationTokenSource operation;
        private string loadedKey, previewKey;
        private bool closingAllowed, closingStarted;
        private DeviceInfo[] devices;
        private DeviceInfo Camera => Cameras.SelectedItem as DeviceInfo ?? throw new InvalidOperationException("请选择相机。");
        private DeviceInfo Robot => Robots.SelectedItem as DeviceInfo ?? throw new InvalidOperationException("请选择机器人。");
        public MainWindow()
        {
            InitializeComponent();
            DatasetDirectory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxisLab.VisionDemo", "Samples");
            var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxisLab", "desktop.log");
            if (File.Exists(log)) { var urls = Regex.Matches(File.ReadAllText(log), @"DesktopReady (http://[^\s]+)"); if (urls.Count > 0) Address.Text = urls[urls.Count - 1].Groups[1].Value; }
            Closing += async (sender, e) =>
            {
                if (closingAllowed) return;
                e.Cancel = true; if (closingStarted) return; closingStarted = true;
                await ShutdownServices(); closingAllowed = true; Close();
            };
        }
        public async Task ShutdownServices()
        {
            operation?.Cancel(); Body.IsEnabled = false;
            try { if (vision != null) await vision.Close(); } catch (Exception error) { Log("VM 关闭：" + error.Message); }
            axis?.Dispose(); closingAllowed = true;
        }
        private void Log(string text) { Status.Text = text; Logs.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + text); if (Logs.Items.Count > 200) Logs.Items.RemoveAt(Logs.Items.Count - 1); }
        private async void Work(Func<CancellationToken, Task> action)
        {
            if (operation != null) return;
            operation = new CancellationTokenSource(); Body.IsEnabled = false;
            try { await action(operation.Token); }
            catch (OperationCanceledException) { Log("任务已取消，旧检测结果不能用于新动作。"); ClearResult(); }
            catch (Exception error) { Log("失败：" + error.Message); ClearResult(); }
            finally { operation.Dispose(); operation = null; Body.IsEnabled = true; }
        }
        private void NeedAxis() { if (axis == null || axis.Endpoint != Address.Text.Trim().TrimEnd('/')) throw new InvalidOperationException("请先连接当前地址。"); }
        private void ClearResult() { result = null; previewKey = null; if (ResultSummary != null) ResultSummary.Text = "等待新的 VM 定位结果"; }
        private void DeviceChanged(object sender, SelectionChangedEventArgs e)
        { frame = null; binding = null; ClearResult(); if (BindingSummary != null) BindingSummary.Text = "设备选择变化，请重新绑定标定方案"; if (PreviewImage != null) PreviewImage.Source = null; }
        private OutputMap Map() => new OutputMap { RequestInput = RequestInput.Text.Trim(), RequestOutput = RequestOutput.Text.Trim(), Calibration = CalibOutput.Text.Trim(), Found = FoundOutput.Text.Trim(), X = XOutput.Text.Trim(), Y = YOutput.Text.Trim(), Rz = RzOutput.Text.Trim() };
        private string SourceKey() => Json.Write(new[] { Path.GetFullPath(SolutionPath.Text), FlowName.Text.Trim(), SourceName.Text.Trim() });
        private string Signature() => VisionService.Signature(FlowName.Text.Trim(), SourceName.Text.Trim(), Mono.IsChecked == true, Map());
        private void NeedVm()
        { if (vision == null || !vision.Loaded || loadedKey != SourceKey()) throw new InvalidOperationException("方案路径或流程已变化，请重新加载 VM 方案。"); }
        private void NeedFrame() { NeedAxis(); if (frame == null || frame.Info.cameraId != Camera.id || frame.Robot.deviceId != Robot.id) throw new InvalidOperationException("请先为当前设备拍照。"); }
        private async Task Connect(CancellationToken ct)
        {
            operation?.Token.ThrowIfCancellationRequested(); axis?.Dispose(); axis = null;
            var client = new AxisClient(Address.Text);
            try { await client.Request<object>("health", ct); devices = await client.Devices(ct); axis = client; }
            catch { client.Dispose(); throw; }
            Cameras.ItemsSource = devices.Where(d => d.typeId == "camera.pinhole").ToArray(); Robots.ItemsSource = devices.Where(d => d.typeId.StartsWith("robot.")).ToArray();
            Cameras.SelectedIndex = 0; Robots.SelectedIndex = 0; frame = null; binding = null; ClearResult();
            Log($"已连接 {axis.Endpoint}；{Cameras.Items.Count} 台相机，{Robots.Items.Count} 台机器人。");
        }
        private async Task Capture(CancellationToken ct)
        {
            NeedAxis(); ClearResult(); frame = null;
            var next = await axis.Capture(Camera, Robot, ct); ct.ThrowIfCancellationRequested(); frame = next;
            PreviewImage.Source = VisionService.Decode(frame.Png); ImageHint.Visibility = Visibility.Collapsed;
            FrameSummary.Text = $"Frame {frame.Info.frameId}\n{frame.Info.width} × {frame.Info.height} · 曝光 {frame.Info.exposureAt}\n机器人 PoseId {frame.Robot.poseId} · {frame.Robot.status} · {(frame.MovingCamera ? "末端相机" : "固定相机")}";
            Log("拍照完成，固定帧 SHA-256 已验证。");
        }
        private async Task LoadVm()
        {
            ClearResult(); binding = null; BindingSummary.Text = "加载新方案后需重新绑定";
            if (vision == null) vision = new VisionService(); loadedKey = null;
            await vision.Load(SolutionPath.Text, SolutionPassword.Password, FlowName.Text.Trim(), SourceName.Text.Trim());
            loadedKey = SourceKey(); Log("VM 用户方案已加载，图像源已在内存中设为 SDK 模式。");
        }
        private async Task RunVm(CancellationToken ct)
        {
            NeedFrame(); NeedVm(); ClearResult();
            var value = await vision.Run(frame, Mono.IsChecked == true, Map(), Signature()); ct.ThrowIfCancellationRequested(); result = value;
            ResultSummary.Text = value.CalibrationOk && value.Found ? $"VM 基坐标：X {value.X:F3} · Y {value.Y:F3} mm · Rz {value.Rz:F3}°" : $"VM 返回：CalibrationOk={value.CalibrationOk}，TargetOk={value.Found}";
            Log(value.CalibrationOk && value.Found ? "已读取本帧的 VM 单目标结果。" : "本帧 VM 返回 NG，禁止运动。");
        }
        private static double Number(TextBox box)
        {
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !Json.Finite(value)) throw new InvalidOperationException("请输入有限数值，使用小数点。"); return value;
        }
        private Pose Target()
        {
            if (result == null || !result.Found || !result.CalibrationOk) throw new InvalidOperationException("没有有效的 VM 定位结果。");
            return new Pose { x = result.X, y = result.Y, z = Number(TargetZ), rx = Number(TargetRx), ry = Number(TargetRy), rz = result.Rz };
        }
        private double Velocity() { var speed = Number(Speed); if (speed < 1 || speed > 100) throw new InvalidOperationException("速度须为 1—100%。"); return speed; }
        private async Task Validate(CancellationToken ct)
        {
            NeedFrame(); NeedVm(); if (binding == null) throw new InvalidOperationException("请先绑定用户已经完成的 VM 标定方案。");
            binding.Validate(frame, result, await axis.Runtime(ct), await axis.Devices(ct), vision.CurrentHash(), Signature(), axis.Endpoint);
        }
        private string TargetKey(Pose target, double speed) => Json.Write(new { frameId = frame.Info.frameId, target, speed, signature = Signature() });
        private async Task Preview(CancellationToken ct)
        {
            previewKey = null; await Validate(ct); var target = Target(); var speed = Velocity();
            var preview = await axis.Request<object>($"v1/robots/{Robot.id}/preview", ct, new { target, frame = "base", kind = "MoveJ", speedPercent = speed, positionOnly = Robot.typeId == "robot.generic3r" });
            await Validate(ct); previewKey = TargetKey(target, speed); Log("运动预检通过：" + Json.Write(preview));
        }
        private void ConnectClick(object sender, RoutedEventArgs e) => Work(Connect);
        private void CaptureClick(object sender, RoutedEventArgs e) => Work(Capture);
        private void BrowseClick(object sender, RoutedEventArgs e)
        { var dialog = new OpenFileDialog { Filter = "VisionMaster 方案|*.sol", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) SolutionPath.Text = dialog.FileName; }
        private void LoadVmClick(object sender, RoutedEventArgs e) => Work(async ct => { await LoadVm(); ct.ThrowIfCancellationRequested(); });
        private void VerifyVmClick(object sender, RoutedEventArgs e) => Work(async ct => { NeedFrame(); NeedVm(); ClearResult(); var text = await vision.VerifyInput(frame, Mono.IsChecked == true); ct.ThrowIfCancellationRequested(); Log(text); });
        private void RunVmClick(object sender, RoutedEventArgs e) => Work(RunVm);
        private void SaveFrameClick(object sender, RoutedEventArgs e) => Work(ct => { NeedFrame(); frame.Save(Path.GetFullPath(DatasetDirectory.Text)); Log("配对样本已保存到 " + DatasetDirectory.Text); return Task.CompletedTask; });
        private void ReadPoseClick(object sender, RoutedEventArgs e) => Work(async ct =>
        {
            NeedAxis(); var state = (await axis.Runtime(ct)).robots.Single(r => r.deviceId == Robot.id).tcpBase.pose;
            TargetZ.Text = state.z.ToString("G9", CultureInfo.InvariantCulture); TargetRx.Text = state.rx.ToString("G9", CultureInfo.InvariantCulture); TargetRy.Text = state.ry.ToString("G9", CultureInfo.InvariantCulture); previewKey = null; Log("已读取当前 TCP 的 Z/Rx/Ry；X/Y/Rz 仍由 VM 提供。");
        });
        private void BindClick(object sender, RoutedEventArgs e) => Work(ct =>
        {
            NeedFrame(); NeedVm(); if (result?.FlowSignature != Signature() || result.SolutionHash != vision.CurrentHash()) throw new InvalidOperationException("请先按当前配置重新运行 VM 流程。");
            binding = CalibrationBinding.Bind(CalibrationLabel.Text, frame, result); BindingSummary.Text = "用户 VM 方案已绑定：" + binding.Label;
            Directory.CreateDirectory(DatasetDirectory.Text); File.WriteAllText(Path.Combine(DatasetDirectory.Text, "binding-" + Guid.NewGuid().ToString("N") + ".json"), Json.Write(binding));
            Log("已记录方案绑定；标定系数由你的 VM 方案保存。"); return Task.CompletedTask;
        });
        private void PreviewClick(object sender, RoutedEventArgs e) => Work(Preview);
        private void MoveClick(object sender, RoutedEventArgs e) => Work(async ct =>
        {
            var target = Target(); var speed = Velocity();
            if (frame == null || previewKey != TargetKey(target, speed)) throw new InvalidOperationException("请先对当前检测结果和目标参数完成预检。");
            await Validate(ct); await Preview(ct);
            await RobotChannel.Move(axis, Robot, target, speed, () => Validate(ct), ct);
            ClearResult(); Log("本次 Modbus 定位完成，控制权已释放；再次运动需重新拍照检测。");
        });
        private async void StopClick(object sender, RoutedEventArgs e)
        {
            operation?.Cancel(); ClearResult();
            try { if (axis != null && Robots.SelectedItem is DeviceInfo robot) await axis.Stop(robot.id); Log("已发送停止，旧检测结果已清除。"); }
            catch (Exception error) { Log("停止通信失败：" + error.Message); }
        }
        public sealed class SmokeSettings { public string url { get; set; } public string solution { get; set; } public string flow { get; set; } public string source { get; set; } public string directory { get; set; } }
        public async Task Smoke(string settingsPath, string reportPath)
        {
            var settings = Json.Read<SmokeSettings>(File.ReadAllText(settingsPath)); var checks = new List<string>();
            void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); Log("PASS " + name); }
            Address.Text = settings.url; DatasetDirectory.Text = settings.directory; await Connect(CancellationToken.None);
            Check("real WPF connects and discovers camera and robot", Cameras.Items.Count > 0 && Robots.Items.Count > 0);
            await Capture(CancellationToken.None); Check("fixed PNG is decoded in WPF with matching metadata", PreviewImage.Source.Width == frame.Info.width && frame.Info.sha256 == Json.Hash(frame.Png));
            frame.Save(settings.directory); var observation = File.ReadAllText(Path.Combine(settings.directory, frame.Info.frameId + ".observation.json"));
            Check("paired training observation includes robot pose and excludes camera truth", observation.Contains("flangeBase") && !observation.Contains("cameraWorld") && !observation.Contains("intrinsics"));
            SolutionPath.Text = settings.solution; FlowName.Text = settings.flow; SourceName.Text = settings.source; await LoadVm();
            Check("installed VisionMaster loads an actual solution", vision.Loaded);
            Log(await vision.VerifyInput(frame, false)); Check("AxisLab RGB24 pixels round trip through real VM SDK", true);
            Log(await vision.VerifyInput(frame, true)); Check("AxisLab MONO8 pixels round trip through real VM SDK", true);
            var missingRejected = false;
            try { await vision.Run(frame, false, new OutputMap { Calibration = "Missing_AxisLab_Calibration", Found = "Missing_AxisLab_Target" }, "diagnostic"); }
            catch (Exception) { missingRejected = true; }
            Check("a real VM flow without contracted outputs cannot fabricate a visual target", missingRejected);
            try { await Preview(CancellationToken.None); throw new InvalidOperationException("Motion was incorrectly authorized."); } catch (InvalidOperationException e) when (e.Message.Contains("绑定")) { Check("missing user calibration cannot authorize a motion", true); }
            await Dispatcher.InvokeAsync(() => { UpdateLayout(); var screenshot = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); screenshot.Render(this); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(screenshot)); using (var output = File.Create(Path.Combine(settings.directory, "wpf-demo.png"))) encoder.Save(output); });
            File.WriteAllText(reportPath, Json.Write(new { success = true, checks, frameId = frame.Info.frameId, sha256 = frame.Info.sha256 }));
        }
    }
}
