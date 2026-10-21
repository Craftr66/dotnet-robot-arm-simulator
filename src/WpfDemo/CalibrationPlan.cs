using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxisLab.WpfDemo;

public sealed class CalibrationPoint
{
    public int Number { get; init; }
    public bool Validation { get; init; }
    public required TransformInfo Target { get; init; }
    public string Role => Validation ? "验证" : "标定";
    public string Position => $"{Target.X:F2}, {Target.Y:F2}, {Target.Z:F2}";
    public Guid? FrameId { get; set; }
    public string? Image { get; set; }
    public PoseInfo? Actual { get; set; }
    public double? PixelX { get; set; }
    public double? PixelY { get; set; }
    public double? VmX { get; set; }
    public double? VmY { get; set; }
    public string State => FrameId is null ? "待采集" : VmX.HasValue ? $"误差 {Error:F3} mm" : "已采集";
    public double? Error => Actual is null || VmX is null || VmY is null ? null : Math.Sqrt(Math.Pow(VmX.Value - Actual.X, 2) + Math.Pow(VmY.Value - Actual.Y, 2));
}

/// <summary>Measured robot/image pairs only. Camera extrinsics and intrinsics are never calibration inputs.</summary>
public sealed class CalibrationPlan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid Session { get; init; }
    public Guid RobotId { get; init; }
    public Guid CameraId { get; init; }
    public Guid MarkerId { get; init; }
    public required string Endpoint { get; init; }
    public required string ConfigurationKey { get; init; }
    public required string Directory { get; init; }
    public List<CalibrationPoint> Points { get; init; } = [];
    public string? ValidationSolutionHash { get; set; }
    public string? ValidationSettingsKey { get; set; }

    public static string Key(IEnumerable<DeviceDocument> devices) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("|", devices.OrderBy(d => d.Id).Select(d => $"{d.Id}:{d.ConfigurationRevision}")))));

    public static List<CalibrationPoint> Grid(TransformInfo centre, double spacing)
    {
        if (!double.IsFinite(spacing) || spacing < 5 || spacing > 200 ||
            new[] { centre.X, centre.Y, centre.Z, centre.Rx, centre.Ry, centre.Rz }.Any(v => !double.IsFinite(v) || Math.Abs(v) > 100000))
            throw new InvalidOperationException("中心位姿须有限；间距须为 5—200 mm。");
        var result = new List<CalibrationPoint>();
        // Deliberately explicit row-major correspondence: X varies first; no automatic point reordering.
        void Add(double x, double y, bool validation) => result.Add(new CalibrationPoint { Number = result.Count + 1, Validation = validation,
            Target = new TransformInfo { X = centre.X + x * spacing, Y = centre.Y + y * spacing, Z = centre.Z, Rx = centre.Rx, Ry = centre.Ry, Rz = centre.Rz } });
        for (var y = -1; y <= 1; y++) for (var x = -1; x <= 1; x++) Add(x, y, false);
        Add(-0.55, -0.35, true); Add(0.45, -0.6, true); Add(0.25, 0.55, true);
        return result;
    }

    public void Validate(RuntimeSnapshot runtime, DeviceDocument[] devices, string endpoint, Guid robot, Guid camera)
    {
        if (runtime.SessionId != Session || endpoint != Endpoint || robot != RobotId || camera != CameraId || Key(devices) != ConfigurationKey)
            throw new InvalidOperationException("采样工程、设备或配置已变化，请重新生成采样计划；已有样本保留在原目录。");
    }

    public static void CheckPose(CalibrationPoint point, RobotRuntime robot)
    {
        var p = robot.TcpBase.Pose; var t = point.Target;
        if (robot.Status == "Running" || Math.Sqrt(Math.Pow(p.X - t.X, 2) + Math.Pow(p.Y - t.Y, 2) + Math.Pow(p.Z - t.Z, 2)) > 0.05)
            throw new InvalidOperationException("机器人未在所选点停稳（位置容差 0.05 mm），请先移动到该点。");
        // Compare rotation matrices so equivalent Euler triples and ±180° boundaries remain valid.
        double[] Rotation(double rx, double ry, double rz)
        {
            var a = rx * Math.PI / 180; var b = ry * Math.PI / 180; var c = rz * Math.PI / 180;
            var sx = Math.Sin(a); var cx = Math.Cos(a); var sy = Math.Sin(b); var cy = Math.Cos(b); var sz = Math.Sin(c); var cz = Math.Cos(c);
            return [cz*cy, sz*cy, -sy, cz*sy*sx-sz*cx, sz*sy*sx+cz*cx, cy*sx, cz*sy*cx+sz*sx, sz*sy*cx-cz*sx, cy*cx];
        }
        var trace = Rotation(p.Rx, p.Ry, p.Rz).Zip(Rotation(t.Rx, t.Ry, t.Rz)).Sum(v => v.First * v.Second);
        if (Math.Acos(Math.Clamp((trace - 1) / 2, -1, 1)) > 0.1 * Math.PI / 180)
            throw new InvalidOperationException("TCP 姿态与采样平面不一致（姿态容差 0.1°）。");
    }

    public void Save() { System.IO.Directory.CreateDirectory(Directory); File.WriteAllText(Path.Combine(Directory, "plan.json"), JsonSerializer.Serialize(this, JsonDefaults.Web)); }

    public void Export()
    {
        if (Points.Count != 12 || Points.Any(p => p.FrameId is null || p.Actual is null || p.Image is null))
            throw new InvalidOperationException("请先采集 9 个标定点和 3 个独立验证点。");
        if (Points.Any(p => p.PixelX.HasValue != p.PixelY.HasValue || p.PixelX.HasValue && (!double.IsFinite(p.PixelX.Value) || !double.IsFinite(p.PixelY!.Value))))
            throw new InvalidOperationException("像素 X/Y 须成对填写有限数值；也可以全部留空后在 VM 中测量。");
        var pixels = Points.Where(p => !p.Validation && p.PixelX.HasValue).ToArray();
        if (pixels.Length != 0 && pixels.Length != 9) throw new InvalidOperationException("标定像素表须填写完整九行，或全部留空。");
        if (pixels.Length == 9)
        {
            var spread = pixels.Max(a => pixels.Max(b => pixels.Max(c => Math.Abs(
                (b.PixelX!.Value-a.PixelX!.Value)*(c.PixelY!.Value-a.PixelY!.Value) - (b.PixelY!.Value-a.PixelY!.Value)*(c.PixelX!.Value-a.PixelX!.Value)))));
            if (spread < 25 || pixels.Select(p => (p.PixelX, p.PixelY)).Distinct().Count() != 9)
                throw new InvalidOperationException("像素点重复或近共线，无法可靠标定。");
        }
        string F(double? value) => value?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
        var text = new StringBuilder("point,role,image,pixel_x,pixel_y,robot_x_mm,robot_y_mm,robot_z_mm,robot_rx_deg,robot_ry_deg,robot_rz_deg,frame_id\n");
        foreach (var p in Points) text.AppendLine(string.Join(",", p.Number, p.Validation ? "validation" : "calibration", p.Image,
            F(p.PixelX), F(p.PixelY), F(p.Actual!.X), F(p.Actual.Y), F(p.Actual.Z), F(p.Actual.Rx), F(p.Actual.Ry), F(p.Actual.Rz), p.FrameId));
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, "points.csv"), text.ToString(), new UTF8Encoding(true));
        Save();
    }

    public void RecordValidation(CalibrationPoint point, VmResult result, VmSettings settings)
    {
        if (!point.Validation || point.FrameId is null || result.FrameId != point.FrameId.Value.ToString("D") || !result.CalibrationOk || !result.Found || result.RequestId == 0 ||
            !double.IsFinite(result.X) || !double.IsFinite(result.Y) || result.SettingsKey != settings.Key || result.SolutionHash != settings.SolutionHash)
            throw new InvalidOperationException("先选独立验证点，采集其图像并运行当前 VM 流程；结果必须属于该帧。");
        if (ValidationSolutionHash is not null && (ValidationSolutionHash != result.SolutionHash || ValidationSettingsKey != settings.Key))
            throw new InvalidOperationException("验证过程中 VM 方案或字段映射已改变，请重新生成计划并采集验证样本。");
        ValidationSolutionHash = result.SolutionHash; ValidationSettingsKey = settings.Key;
        point.VmX = result.X; point.VmY = result.Y; Save();
    }

    public void Report(double tolerance)
    {
        var points = Points.Where(p => p.Validation).ToArray();
        if (!double.IsFinite(tolerance) || tolerance <= 0 || points.Length != 3 || points.Any(p => p.Error is null || !double.IsFinite(p.Error.Value)))
            throw new InvalidOperationException("先记录三个独立验证点的 VM 结果，并设置正的有限误差门限。");
        var rms = Math.Sqrt(points.Average(p => p.Error!.Value * p.Error.Value)); var maximum = points.Max(p => p.Error!.Value);
        File.WriteAllText(Path.Combine(Directory, "validation-report.json"), JsonSerializer.Serialize(new { planId = Id, units = "mm", scope = "fixed-camera XY plane; independent validation; no full 3D hand-eye or angle accuracy claim",
            solutionSha256 = ValidationSolutionHash, settingsKey = ValidationSettingsKey, tolerance, rms, maximum, passed = maximum <= tolerance,
            points = points.Select(p => new { p.Number, p.FrameId, expectedX = p.Actual!.X, expectedY = p.Actual.Y, measuredX = p.VmX, measuredY = p.VmY, error = p.Error }) }, JsonDefaults.Web));
    }
}
