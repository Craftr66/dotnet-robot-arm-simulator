using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxisLab.WpfDemo;

public sealed class VmOutputMap
{
    public string RequestInput { get; set; } = "AxisLabRequestId";
    public string RequestOutput { get; set; } = "ResultRequestId";
    public string Calibration { get; set; } = "CalibrationOk";
    public string Found { get; set; } = "TargetOk";
    public string X { get; set; } = "RobotX";
    public string Y { get; set; } = "RobotY";
    public string Rz { get; set; } = "RobotRz";
}
public sealed class VmSettings
{
    public string Solution { get; set; } = "";
    public string Flow { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Mono { get; set; }
    public VmOutputMap Map { get; set; } = new();
    public string SourceKey => JsonSerializer.Serialize(new[] { Path.GetFullPath(Solution), Flow, Source });
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, new JsonSerializerOptions { IgnoreReadOnlyProperties = true }))));
    public string SolutionHash => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Solution)));
}
public sealed class VmResult
{
    public int RequestId { get; set; }
    public bool CalibrationOk { get; set; }
    public bool Found { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Rz { get; set; }
    public string FrameId { get; set; } = "";
    public string SolutionHash { get; set; } = "";
    public string FlowSignature { get; set; } = "";
    public string SettingsKey { get; set; } = "";
}
public sealed class VisionFrameContext
{
    public required CapturedFrame Frame { get; init; }
    public required DeviceInfo Camera { get; init; }
    public required DeviceInfo Robot { get; init; }
    public required RobotRuntime ExposureRobot { get; init; }
    public required string Endpoint { get; init; }
    public required Guid Session { get; init; }
    public required long Revision { get; init; }
    public required DateTimeOffset Received { get; init; }
    public bool MovingCamera { get; init; }
    public void SaveSample(string directory)
    {
        if (ExposureRobot.Status == "Running") throw new InvalidOperationException("机器人运动中的帧不能保存为静态标定样本。");
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, Frame.FrameId.ToString("D"));
        File.WriteAllBytes(path + ".png", Frame.Png);
        File.WriteAllText(path + ".observation.json", JsonSerializer.Serialize(new { version = 1, image = Frame.FrameId + ".png", Frame.FrameId, Frame.CaptureId, Frame.CameraId, Session,
            exposureAt = Frame.Metadata.GetProperty("exposureAt").GetString(), Frame.Width, Frame.Height, Frame.Sha256, robotId = Robot.Id, cameraConfigurationRevision = Camera.ConfigurationRevision,
            robotConfigurationRevision = Robot.ConfigurationRevision, robot = new { ExposureRobot.PoseId, ExposureRobot.Joints, ExposureRobot.FlangeBase, ExposureRobot.TcpBase }, units = "mm/deg", matrixLayout = "column-major" }, JsonDefaults.Web));
    }
}
public sealed class VisionBinding
{
    public string Label { get; init; } = "";
    public required string Endpoint { get; init; }
    public Guid Session { get; init; }
    public Guid CameraId { get; init; }
    public Guid RobotId { get; init; }
    public uint CameraRevision { get; init; }
    public uint RobotRevision { get; init; }
    public required string SolutionHash { get; init; }
    public required string SettingsKey { get; init; }
    public static VisionBinding Create(string label, VisionFrameContext context, VmResult result, VmSettings settings)
    {
        if (string.IsNullOrWhiteSpace(label) || !result.CalibrationOk || result.RequestId == 0 || result.FrameId != context.Frame.FrameId.ToString("D") || result.SettingsKey != settings.Key || result.SolutionHash != settings.SolutionHash)
            throw new InvalidOperationException("先运行当前 VM 流程确认 CalibrationOk=1，再填写用户标定方案标识。");
        return new VisionBinding { Label = label.Trim(), Endpoint = context.Endpoint, Session = context.Session, CameraId = context.Camera.Id, RobotId = context.Robot.Id,
            CameraRevision = context.Camera.ConfigurationRevision, RobotRevision = context.Robot.ConfigurationRevision, SolutionHash = result.SolutionHash, SettingsKey = settings.Key };
    }
    public void Validate(VisionFrameContext frame, VmResult result, VmSettings settings, RuntimeSnapshot runtime, IReadOnlyList<DeviceDocument> devices, string endpoint)
    {
        if (result.RequestId == 0 || !result.Found || !result.CalibrationOk || result.FrameId != frame.Frame.FrameId.ToString("D")) throw new InvalidOperationException("本帧没有有效的已标定单目标结果。");
        if (frame.MovingCamera) throw new InvalidOperationException("末端相机需要后续手眼变换；首版定位只支持固定相机。");
        if (endpoint != Endpoint || frame.Endpoint != Endpoint || frame.Session != Session || runtime.SessionId != Session) throw new InvalidOperationException("连接或工程会话已变化，请重新绑定标定方案。");
        var camera = devices.SingleOrDefault(d => d.Id == CameraId); var robot = devices.SingleOrDefault(d => d.Id == RobotId);
        if (camera is null || robot is null || frame.Camera.Id != CameraId || frame.Robot.Id != RobotId || camera.ConfigurationRevision != CameraRevision || robot.ConfigurationRevision != RobotRevision ||
            frame.Camera.ConfigurationRevision != CameraRevision || frame.Robot.ConfigurationRevision != RobotRevision) throw new InvalidOperationException("设备或安装参数已改变，请重新验证标定。");
        if (settings.Key != SettingsKey || result.SettingsKey != SettingsKey || settings.SolutionHash != SolutionHash || result.SolutionHash != SolutionHash) throw new InvalidOperationException("VM 方案或映射已改变，请重新加载、运行并绑定。");
        if (runtime.ProjectRevision != frame.Revision || DateTimeOffset.UtcNow - frame.Received > TimeSpan.FromSeconds(30)) throw new InvalidOperationException("帧已过期或拍照后场景/机器人已改变，请重新拍照检测。");
        if (!double.IsFinite(result.X) || !double.IsFinite(result.Y) || !double.IsFinite(result.Rz)) throw new InvalidOperationException("VM 结果包含非有限数。");
    }
}
