using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AxisLab.WpfDemo;

public partial class MainWindow
{
    private CalibrationPlan? _samplingPlan;

    private async Task<(RuntimeSnapshot Runtime, DeviceDocument[] Devices)> SamplingContext(CancellationToken ct)
    {
        if (_connection?.BaseUri is null || _selectedRobot?.Axes != 6 || _selectedCamera is null)
            throw new InvalidOperationException("九点向导先支持六轴机械臂与固定相机；请选择对应设备。");
        var devices = await _connection.Http.GetDevicesAsync(ct);
        var runtime = await _connection.Http.GetRuntimeAsync(ct);
        var camera = devices.Single(d => d.Id == _selectedCamera.Id);
        for (var parent = camera.ParentId; parent.HasValue;)
        {
            var device = devices.Single(d => d.Id == parent.Value);
            if (device.Robot is not null) throw new InvalidOperationException("九点平面标定须使用固定相机。");
            parent = device.ParentId;
        }
        if (_samplingPlan is not null) _samplingPlan.Validate(runtime, devices, _connection.BaseUri.GetLeftPart(UriPartial.Authority), _selectedRobot.Id, _selectedCamera.Id);
        return (runtime, devices);
    }

    private TransformInfo SamplingCentre() => new() { X = VisionNumber(SampleX), Y = VisionNumber(SampleY), Z = VisionNumber(SampleZ), Rx = VisionNumber(SampleRx), Ry = VisionNumber(SampleRy), Rz = VisionNumber(SampleRz) };
    private CalibrationPoint SamplingPoint() => SamplingGrid.SelectedItem as CalibrationPoint ?? throw new InvalidOperationException("请选择一个采样点。");
    private void SamplingRefresh(string text) { SamplingGrid.Items.Refresh(); SampleState.Text = text; Log(text); }

    private async Task GenerateSampling(CancellationToken ct)
    {
        // A new plan explicitly starts a fresh binding; old files are never overwritten.
        var previous = _samplingPlan; _samplingPlan = null;
        try
        {
            var context = await SamplingContext(ct);
            var markers = context.Devices.Where(d => d.Visible && d.TypeId == "calibration.marker" && d.ParentId == _selectedRobot!.Id && d.AttachmentFrameId == "tool").ToArray();
            if (markers.Length != 1 || new[] { markers[0].Transform.X, markers[0].Transform.Y, markers[0].Transform.Z, markers[0].Transform.Rx, markers[0].Transform.Ry, markers[0].Transform.Rz }.Any(v => Math.Abs(v) > 1e-8))
                throw new InvalidOperationException("请在 AxisLab 添加一个可见 TCP 标定圆点，挂到所选机器人的 tool，局部位置和旋转全部设为 0。示例已配置。");
            var points = CalibrationPlan.Grid(SamplingCentre(), VisionNumber(SampleSpacing));
            var plan = new CalibrationPlan { Session = context.Runtime.SessionId, RobotId = _selectedRobot!.Id, CameraId = _selectedCamera!.Id, MarkerId = markers[0].Id,
                Endpoint = _connection!.BaseUri!.GetLeftPart(UriPartial.Authority), ConfigurationKey = CalibrationPlan.Key(context.Devices),
                Directory = Path.Combine(Path.GetFullPath(VmSampleDirectory.Text), "nine-point-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]), Points = points };
            plan.Save(); _samplingPlan = plan; SamplingGrid.ItemsSource = plan.Points; SamplingGrid.SelectedIndex = 0;
            SamplingRefresh("已生成 9 个标定点＋3 个独立验证点。逐点移动后采集；目录：" + plan.Directory);
        }
        catch { _samplingPlan = previous; throw; }
    }

    private async Task MoveSampling(CancellationToken ct)
    {
        if (_samplingPlan is null) throw new InvalidOperationException("请先生成九点计划。");
        var point = SamplingPoint(); await SamplingContext(ct);
        await _connection!.Http.PreviewCartesianAsync(_selectedRobot!.Id, point.Target, SpeedSlider.Value, false, ct);
        var session = _connection.RobotSession!; var acquired = !session.IsHoldingControl;
        try
        {
            if (acquired) await _connection.AcquireRobotControlAsync(ct);
            await SamplingContext(ct); ResetVision(false);
            ApplyRobotState(await session.MoveCartesianAsync(point.Target, SpeedSlider.Value, ct));
            var context = await SamplingContext(ct); CalibrationPlan.CheckPose(point, context.Runtime.Robots.Single(r => r.DeviceId == _selectedRobot.Id));
            SamplingRefresh($"点 {point.Number} 已到位，请采集。移动经过运动预检，轨迹为 MoveJ。");
        }
        catch { try { await _connection.StopRobotAsync(); } catch { } throw; }
        finally { if (acquired && session.IsHoldingControl) try { await _connection.ReleaseRobotControlAsync(); } catch { } }
    }

    private async Task CaptureSampling(CancellationToken ct)
    {
        if (_samplingPlan is null) throw new InvalidOperationException("请先生成九点计划。");
        var point = SamplingPoint(); var context = await SamplingContext(ct);
        CalibrationPlan.CheckPose(point, context.Runtime.Robots.Single(r => r.DeviceId == _selectedRobot!.Id));
        ResetVision(false);
        var frame = await _connection!.CaptureAsync(_selectedCamera!, ct);
        await PrepareVisionFrame(frame, ct); var exposure = _visionContext!;
        CalibrationPlan.CheckPose(point, exposure.ExposureRobot); await SamplingContext(ct);
        var prefix = $"p{point.Number:D2}-{frame.FrameId:D}";
        exposure.SaveSample(_samplingPlan.Directory);
        File.Move(Path.Combine(_samplingPlan.Directory, frame.FrameId + ".png"), Path.Combine(_samplingPlan.Directory, prefix + ".png"));
        File.Move(Path.Combine(_samplingPlan.Directory, frame.FrameId + ".observation.json"), Path.Combine(_samplingPlan.Directory, prefix + ".observation.json"));
        // Observation's image name must match its renamed PNG.
        var observationPath = Path.Combine(_samplingPlan.Directory, prefix + ".observation.json");
        var observation = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(observationPath))!; observation["image"] = prefix + ".png";
        File.WriteAllText(observationPath, observation.ToJsonString(JsonDefaults.Web));
        point.FrameId = frame.FrameId; point.Image = prefix + ".png"; point.Actual = exposure.ExposureRobot.TcpBase.Pose;
        point.PixelX = point.PixelY = point.VmX = point.VmY = null;
        _lastFrame = frame; DisplayFrame(frame); _samplingPlan.Save(); SamplingRefresh($"点 {point.Number} 已采集，记录曝光时实际 TCP。可运行 VM 或选择下一点。");
    }

    private void SampleCurrent_Click(object sender, RoutedEventArgs e) => VisionWork(async ct =>
    {
        if (_connection is null || _selectedRobot is null) throw new InvalidOperationException("请选择六轴机械臂。");
        var p = (await _connection.Http.GetRuntimeAsync(ct)).Robots.Single(r => r.DeviceId == _selectedRobot.Id).TcpBase.Pose;
        foreach (var pair in new[] { (SampleX, p.X), (SampleY, p.Y), (SampleZ, p.Z), (SampleRx, p.Rx), (SampleRy, p.Ry), (SampleRz, p.Rz) }) pair.Item1.Text = pair.Item2.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
    });
    private void SampleGenerate_Click(object sender, RoutedEventArgs e) => VisionWork(GenerateSampling);
    private void SampleMove_Click(object sender, RoutedEventArgs e) => VisionWork(MoveSampling);
    private void SampleCapture_Click(object sender, RoutedEventArgs e) => VisionWork(CaptureSampling);
    private void SampleExport_Click(object sender, RoutedEventArgs e)
    {
        SamplingGrid.CommitEdit(DataGridEditingUnit.Cell, true); SamplingGrid.CommitEdit(DataGridEditingUnit.Row, true);
        VisionWork(async ct => { await SamplingContext(ct); (_samplingPlan ?? throw new InvalidOperationException("请先生成计划。")).Export(); SamplingRefresh("已导出 points.csv。仅前九行用于标定；后三行必须留作独立验证。"); });
    }
    private void SampleRecord_Click(object sender, RoutedEventArgs e) => VisionWork(async ct =>
    {
        await SamplingContext(ct); NeedVm();
        if (_samplingPlan is null || _vmResult is null) throw new InvalidOperationException("请采集验证点并运行 VM。");
        _samplingPlan.RecordValidation(SamplingPoint(), _vmResult, VisionSettings()); SamplingRefresh("已记录本帧 VM 平面定位误差。");
    });
    private void SampleReport_Click(object sender, RoutedEventArgs e) => VisionWork(async ct =>
    {
        await SamplingContext(ct); (_samplingPlan ?? throw new InvalidOperationException("请先生成计划。")).Report(VisionNumber(SampleTolerance));
        SamplingRefresh("已生成 validation-report.json，包含独立点 RMS、最大误差和门限判定。");
    });
}
