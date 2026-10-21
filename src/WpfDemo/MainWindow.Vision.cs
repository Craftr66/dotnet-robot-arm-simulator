using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace AxisLab.WpfDemo;

public partial class MainWindow
{
    private VmBridgeClient? _vmBridge;
    private VisionFrameContext? _visionContext;
    private VmResult? _vmResult;
    private VisionBinding? _visionBinding;
    private string? _vmSourceKey, _visionPreview;
    private CancellationTokenSource? _vmCts;
    private bool _visionBusy;
    private void InitializeVision()
    {
        var data = Environment.GetEnvironmentVariable("AXISLAB_WPF_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AxisLab.WpfDemo");
        VmSampleDirectory.Text = Path.Combine(data, "calibration-samples");
    }
    private VmSettings VisionSettings() => new()
    {
        Solution = VmSolutionPath.Text.Trim(), Flow = VmFlow.Text.Trim(), Source = VmSource.Text.Trim(), Mono = VmMono.IsChecked == true,
        Map = new VmOutputMap { RequestInput = VmRequestIn.Text.Trim(), RequestOutput = VmRequestOut.Text.Trim(), Calibration = VmCalib.Text.Trim(), Found = VmFound.Text.Trim(), X = VmX.Text.Trim(), Y = VmY.Text.Trim(), Rz = VmRz.Text.Trim() }
    };
    private void ClearVisionResult()
    { _vmResult = null; _visionPreview = null; VmResultText.Text = "等待新的 VM 定位结果"; }
    private void ResetVision(bool resetBinding)
    {
        ClearVisionResult(); _visionContext = null;
        if (resetBinding) { _visionBinding = null; VmBindingText.Text = "未绑定标定方案"; }
    }
    private void UpdateVisionUi()
    {
        var busy = _visionBusy || _isBusy || _isMoving || _isCapturing;
        SpeedSlider.IsEnabled = !_visionBusy; JointPanel.IsEnabled = !_visionBusy;
        VmSettingsPanel.IsEnabled = !busy; VisionPositionPanel.IsEnabled = !busy;
        SamplingPanel.IsEnabled = !busy;
        VmVerify.IsEnabled = !busy && _visionContext is not null && _vmBridge?.IsLoaded == true;
        VmRun.IsEnabled = VmVerify.IsEnabled;
        VmBind.IsEnabled = !busy && _vmResult?.CalibrationOk == true;
        VmPreview.IsEnabled = !busy && _vmResult?.Found == true && _visionBinding is not null;
        VmMove.IsEnabled = VmPreview.IsEnabled && _visionPreview is not null;
        VmSaveSample.IsEnabled = !busy && _visionContext is not null;
        if (_visionBusy)
        {
            foreach (var button in new Button[] { AutoDetectButton, ConnectButton, DisconnectButton, RefreshButton, ImportButton, OpenProjectButton, AcquireButton, MoveButton, ZeroButton, ReleaseButton, CaptureButton }) button.IsEnabled = false;
            DeviceList.IsEnabled = false; ProjectCombo.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        if (_isCapturing) DeviceList.IsEnabled = false;
    }
    private async void VisionWork(Func<CancellationToken, Task> action)
    {
        if (_visionBusy || _isBusy || _isCapturing || _isMoving) return;
        _visionBusy = true; var cts = _vmCts = new CancellationTokenSource(); UpdateUiState();
        try { await action(cts.Token); }
        catch (Exception e) { ClearVisionResult(); VmStateText.Text = cts.IsCancellationRequested ? "VM 任务已取消，旧结果失效" : "失败：" + e.Message; Log(VmStateText.Text); SetStatus(VmStateText.Text); }
        finally { _visionBusy = false; _vmCts = null; cts.Dispose(); UpdateUiState(); }
    }
    private async Task PrepareVisionFrame(CapturedFrame frame, CancellationToken ct = default)
    {
        _visionContext = null; ClearVisionResult();
        if (_selectedRobot is null || _selectedCamera is null || _connection?.BaseUri is null) return;
        var robot = _selectedRobot; var camera = _selectedCamera;
        if (frame.CameraId != camera.Id) throw new InvalidOperationException("相机选择已经改变，请重新拍照。");
        var meta = frame.Metadata; var session = meta.GetProperty("sessionId").GetGuid(); var revision = meta.GetProperty("sceneRevision").GetInt64();
        var devices = await _connection.Http.GetDevicesAsync(ct); var runtime = await _connection.Http.GetRuntimeAsync(ct);
        if (runtime.SessionId != session || runtime.ProjectRevision != revision || devices.Single(d => d.Id == robot.Id).ConfigurationRevision != robot.ConfigurationRevision)
            throw new InvalidOperationException("取图后工程或机器人配置已变化，请刷新后重拍。");
        var exposure = meta.GetProperty("robots").EnumerateArray().Single(r => r.GetProperty("deviceId").GetGuid() == robot.Id).Deserialize<RobotRuntime>(JsonDefaults.Web)!;
        var movingCamera = false; var parent = camera.ParentId;
        for (var i = 0; parent is not null && i < devices.Length; i++) { var p = devices.Single(d => d.Id == parent); movingCamera |= p.TypeId.StartsWith("robot.", StringComparison.Ordinal); parent = p.ParentId; }
        _visionContext = new VisionFrameContext { Frame = frame, Camera = camera, Robot = robot, ExposureRobot = exposure, Endpoint = _connection.BaseUri.GetLeftPart(UriPartial.Authority), Session = session, Revision = revision, Received = DateTimeOffset.UtcNow, MovingCamera = movingCamera };
    }
    private void NeedVisionFrame()
    {
        if (_visionContext is null || _connection?.BaseUri is null || _selectedRobot?.Id != _visionContext.Robot.Id || _selectedCamera?.Id != _visionContext.Camera.Id)
            throw new InvalidOperationException("请先选择机器人和相机，再拍摄当前帧。");
    }
    private void NeedVm()
    {
        if (_vmBridge?.IsLoaded != true || _vmSourceKey != VisionSettings().SourceKey || VisionSettings().SolutionHash != _vmBridge.LoadedHash)
            throw new InvalidOperationException("请加载当前 VM 方案；路径、流程或方案内容变化后须重新加载。");
    }
    private async Task LoadVision(CancellationToken ct)
    {
        ClearVisionResult(); _visionBinding = null; VmBindingText.Text = "新方案待绑定"; _vmSourceKey = null;
        _vmBridge ??= new VmBridgeClient(); var settings = VisionSettings();
        await _vmBridge.Load(settings.Solution, VmPassword.Password, settings.Flow, settings.Source, ct);
        _vmSourceKey = settings.SourceKey; VmStateText.Text = "VM 方案已加载"; Log("已通过配套 VM 服务加载用户方案，主界面仍使用原 WPF 框架。");
    }
    private async Task RunVision(CancellationToken ct)
    {
        NeedVisionFrame(); NeedVm(); ClearVisionResult(); var settings = VisionSettings(); var context = _visionContext!;
        var result = await _vmBridge!.Run(context.Frame, settings, ct); ct.ThrowIfCancellationRequested();
        if (result.FrameId != context.Frame.FrameId.ToString("D") || result.RequestId == 0 || result.SolutionHash != settings.SolutionHash) throw new InvalidDataException("VM 帧、请求编号或方案身份不匹配。");
        result.SettingsKey = settings.Key; _vmResult = result;
        VmResultText.Text = result.Found && result.CalibrationOk ? $"基坐标：X {result.X:F3} / Y {result.Y:F3} mm\nRz {result.Rz:F3}° · 请求 {result.RequestId}" : $"VM NG：标定有效={result.CalibrationOk}，目标有效={result.Found}";
        VmStateText.Text = result.Found && result.CalibrationOk ? "本帧视觉流程完成" : "VM 返回 NG，禁止运动"; Log(VmStateText.Text);
    }
    private async Task ValidateVision(CancellationToken ct)
    {
        NeedVisionFrame(); NeedVm();
        if (_visionBinding is null || _vmResult is null) throw new InvalidOperationException("请运行 VM 并绑定用户标定方案。");
        _visionBinding.Validate(_visionContext!, _vmResult, VisionSettings(), await _connection!.Http.GetRuntimeAsync(ct), await _connection.Http.GetDevicesAsync(ct), _connection.BaseUri!.GetLeftPart(UriPartial.Authority));
    }
    private static double VisionNumber(TextBox box)
    { if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new InvalidOperationException("目标参数须为有限数，请使用小数点。"); return value; }
    private TransformInfo VisionTarget()
    {
        if (_vmResult?.Found != true || !_vmResult.CalibrationOk) throw new InvalidOperationException("没有有效视觉目标。");
        return new TransformInfo { X = _vmResult.X, Y = _vmResult.Y, Rz = _vmResult.Rz, Z = VisionNumber(VmTargetZ), Rx = VisionNumber(VmTargetRx), Ry = VisionNumber(VmTargetRy) };
    }
    private string VisionTargetKey(TransformInfo target) => JsonSerializer.Serialize(new { _visionContext!.Frame.FrameId, target, speed = SpeedSlider.Value, settings = VisionSettings().Key });
    private async Task PreviewVision(CancellationToken ct)
    {
        _visionPreview = null; await ValidateVision(ct); var target = VisionTarget();
        var preview = await _connection!.Http.PreviewCartesianAsync(_selectedRobot!.Id, target, SpeedSlider.Value, _selectedRobot.Axes == 3, ct);
        await ValidateVision(ct); _visionPreview = VisionTargetKey(target); Log("视觉目标预检通过：" + preview.GetRawText()); SetStatus("视觉目标预检通过");
    }
    private async Task MoveVision(CancellationToken ct)
    {
        var target = VisionTarget();
        if (_visionPreview != VisionTargetKey(target)) throw new InvalidOperationException("请先预检当前视觉结果及目标参数。");
        await ValidateVision(ct); await PreviewVision(ct); var connection = _connection!;
        var session = connection.RobotSession ?? throw new InvalidOperationException("请先选择机器人。");
        var acquired = !session.IsHoldingControl;
        try
        {
            if (acquired) await connection.AcquireRobotControlAsync(ct);
            await ValidateVision(ct); _isMoving = true; UpdateUiState();
            ApplyRobotState(await session.MoveCartesianAsync(target, SpeedSlider.Value, ct));
            Log("视觉定位完成；再次运动需重新拍照检测。");
        }
        catch
        {
            // Stop uses a separate Modbus connection, so it never queues behind a movement.
            try { await connection.StopRobotAsync(); } catch { }
            throw;
        }
        finally
        {
            if (acquired && session.IsHoldingControl) try { await connection.ReleaseRobotControlAsync(); } catch { }
            _isMoving = false; ClearVisionResult(); UpdateUiState();
        }
    }
    private async Task CloseVision()
    {
        _vmCts?.Cancel();
        if (_vmBridge is not null) { await _vmBridge.DisposeAsync(); _vmBridge = null; }
        _vmSourceKey = null; ResetVision(true); VmStateText.Text = "VM 服务已关闭";
    }
    private void VmBrowse_Click(object sender, RoutedEventArgs e) { var dialog = new OpenFileDialog { Filter = "VisionMaster 方案|*.sol" }; if (dialog.ShowDialog(this) == true) VmSolutionPath.Text = dialog.FileName; }
    private void VmLoad_Click(object sender, RoutedEventArgs e) => VisionWork(LoadVision);
    private void VmVerify_Click(object sender, RoutedEventArgs e) => VisionWork(async ct => { NeedVisionFrame(); NeedVm(); ClearVisionResult(); VmStateText.Text = await _vmBridge!.Verify(_visionContext!.Frame, VisionSettings(), ct); Log(VmStateText.Text); });
    private void VmRun_Click(object sender, RoutedEventArgs e) => VisionWork(RunVision);
    private void VmBind_Click(object sender, RoutedEventArgs e) => VisionWork(ct =>
    {
        NeedVisionFrame(); NeedVm(); if (_vmResult is null) throw new InvalidOperationException("请先运行当前 VM 流程。");
        _visionBinding = VisionBinding.Create(VmBindingLabel.Text, _visionContext!, _vmResult, VisionSettings()); VmBindingText.Text = "用户标定已绑定：" + _visionBinding.Label;
        Directory.CreateDirectory(VmSampleDirectory.Text); File.WriteAllText(Path.Combine(VmSampleDirectory.Text, "binding-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(_visionBinding, JsonDefaults.Web)); return Task.CompletedTask;
    });
    private void VmReadPose_Click(object sender, RoutedEventArgs e) => VisionWork(async ct =>
    {
        if (_connection is null || _selectedRobot is null) throw new InvalidOperationException("请选择机器人。");
        var pose = (await _connection.Http.GetRuntimeAsync(ct)).Robots.Single(r => r.DeviceId == _selectedRobot.Id).TcpBase.Pose;
        VmTargetZ.Text = pose.Z.ToString("G9", CultureInfo.InvariantCulture); VmTargetRx.Text = pose.Rx.ToString("G9", CultureInfo.InvariantCulture); VmTargetRy.Text = pose.Ry.ToString("G9", CultureInfo.InvariantCulture); _visionPreview = null;
    });
    private void VmPreview_Click(object sender, RoutedEventArgs e) => VisionWork(PreviewVision);
    private void VmMove_Click(object sender, RoutedEventArgs e) => VisionWork(MoveVision);
    private void VmSaveSample_Click(object sender, RoutedEventArgs e) => VisionWork(ct => { NeedVisionFrame(); _visionContext!.SaveSample(Path.GetFullPath(VmSampleDirectory.Text)); Log("已保存图像及曝光机器人姿态；相机真值未写入标定样本。"); return Task.CompletedTask; });
}
