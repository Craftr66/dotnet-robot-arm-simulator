using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace AxisLab.VisionDemo
{
    public static class Json
    {
        private static JavaScriptSerializer Serializer() => new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024, RecursionLimit = 2048 };
        public static T Read<T>(string text) => Serializer().Deserialize<T>(text);
        public static string Write(object value) => Serializer().Serialize(value);
        public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
        public static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
    public sealed class DeviceInfo
    {
        public string id { get; set; }
        public string typeId { get; set; }
        public string name { get; set; }
        public int deviceNumber { get; set; }
        public string parentId { get; set; }
        public long configurationRevision { get; set; }
        public override string ToString() => $"{name} · #{deviceNumber}";
    }
    public sealed class Pose
    {
        public double x { get; set; }
        public double y { get; set; }
        public double z { get; set; }
        public double rx { get; set; }
        public double ry { get; set; }
        public double rz { get; set; }
    }
    public sealed class Coordinate { public Pose pose { get; set; } public double[] matrix { get; set; } }
    public sealed class RobotInfo
    {
        public string deviceId { get; set; }
        public long poseId { get; set; }
        public double[] joints { get; set; }
        public string status { get; set; }
        public string commandId { get; set; }
        public string motionErrorCode { get; set; }
        public string motionMessage { get; set; }
        public Coordinate flangeBase { get; set; }
        public Coordinate tcpBase { get; set; }
    }
    public sealed class RuntimeInfo
    {
        public string sessionId { get; set; }
        public long projectRevision { get; set; }
        public RobotInfo[] robots { get; set; }
    }
    public sealed class CaptureStatus
    {
        public string captureId { get; set; }
        public string cameraId { get; set; }
        public string frameId { get; set; }
        public string status { get; set; }
        public string errorCode { get; set; }
        public string message { get; set; }
    }
    // Intentionally omit cameraWorld/intrinsics from training observations.
    public sealed class FrameInfo
    {
        public string frameId { get; set; }
        public string captureId { get; set; }
        public string cameraId { get; set; }
        public string sessionId { get; set; }
        public long sceneRevision { get; set; }
        public long cameraConfigurationRevision { get; set; }
        public string exposureAt { get; set; }
        public int width { get; set; }
        public int height { get; set; }
        public string sha256 { get; set; }
        public RobotInfo[] robots { get; set; }
        public string imageUrl { get; set; }
    }
    public sealed class Frame
    {
        public FrameInfo Info { get; set; }
        public byte[] Png { get; set; }
        public RobotInfo Robot { get; set; }
        public DateTime ReceivedUtc { get; set; }
        public string Endpoint { get; set; }
        public long RobotConfiguration { get; set; }
        public bool MovingCamera { get; set; }
        public void Save(string directory)
        {
            if (Robot.status == "Running") throw new InvalidOperationException("标定样本需在机器人静止后采集。");
            Directory.CreateDirectory(directory);
            var stem = Path.Combine(directory, Info.frameId);
            File.WriteAllBytes(stem + ".png", Png);
            File.WriteAllText(stem + ".observation.json", Json.Write(new { version = 1, image = Info.frameId + ".png", Info.frameId,
                Info.captureId, Info.cameraId, Info.sessionId, Info.sceneRevision, Info.cameraConfigurationRevision,
                Info.exposureAt, Info.width, Info.height, Info.sha256, robotConfigurationRevision = RobotConfiguration,
                robot = new { Robot.deviceId, Robot.poseId, Robot.joints, Robot.flangeBase, Robot.tcpBase }, units = "mm/deg", matrixLayout = "column-major", transform = "T_A_B maps B to A" }));
        }
    }
    public sealed class OutputMap
    {
        public string RequestInput = "AxisLabRequestId";
        public string RequestOutput = "ResultRequestId";
        public string Calibration = "CalibrationOk";
        public string Found = "TargetOk";
        public string X = "RobotX";
        public string Y = "RobotY";
        public string Rz = "RobotRz";
    }
    public sealed class VisionResult
    {
        public int RequestId { get; set; }
        public bool CalibrationOk { get; set; }
        public bool Found { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Rz { get; set; }
        public string FrameId { get; set; }
        public string SolutionHash { get; set; }
        public string FlowSignature { get; set; }
    }
    public sealed class CalibrationBinding
    {
        public string Label { get; set; }
        public string Endpoint { get; set; }
        public string Session { get; set; }
        public string CameraId { get; set; }
        public string RobotId { get; set; }
        public long CameraRevision { get; set; }
        public long RobotRevision { get; set; }
        public string SolutionHash { get; set; }
        public string FlowSignature { get; set; }
        public static CalibrationBinding Bind(string label, Frame frame, VisionResult result)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new InvalidOperationException("请填写你已完成的 VM 标定方案标识。");
            if (frame == null || result == null || result.FrameId != frame.Info.frameId || !result.CalibrationOk)
                throw new InvalidOperationException("先运行本帧 VM 流程，并要求 CalibrationOk=1。");
            return new CalibrationBinding { Label = label.Trim(), Endpoint = frame.Endpoint, Session = frame.Info.sessionId,
                CameraId = frame.Info.cameraId, RobotId = frame.Robot.deviceId, CameraRevision = frame.Info.cameraConfigurationRevision,
                RobotRevision = frame.RobotConfiguration, SolutionHash = result.SolutionHash, FlowSignature = result.FlowSignature };
        }
        public void Validate(Frame frame, VisionResult result, RuntimeInfo current, DeviceInfo[] devices, string hash, string signature, string endpoint)
        {
            if (frame == null || result == null || result.FrameId != frame.Info.frameId || result.RequestId == 0 || !result.Found || !result.CalibrationOk)
                throw new InvalidOperationException("本帧没有有效的已标定单目标结果。");
            if (frame.MovingCamera) throw new InvalidOperationException("首版运动闭环只支持固定相机；末端相机可取图，运动需后续手眼变换。");
            if (current.sessionId != Session || frame.Info.sessionId != Session || frame.Endpoint != Endpoint || endpoint != Endpoint)
                throw new InvalidOperationException("连接或工程会话已改变，请重新标定绑定。");
            var camera = devices.SingleOrDefault(d => d.id == CameraId); var robot = devices.SingleOrDefault(d => d.id == RobotId);
            if (camera == null || robot == null || frame.Info.cameraId != CameraId || frame.Robot.deviceId != RobotId ||
                camera.configurationRevision != CameraRevision || robot.configurationRevision != RobotRevision ||
                frame.Info.cameraConfigurationRevision != CameraRevision || frame.RobotConfiguration != RobotRevision)
                throw new InvalidOperationException("设备、安装或参数已变化，请重新验证标定方案。");
            if (hash != SolutionHash || result.SolutionHash != hash || signature != FlowSignature || result.FlowSignature != signature)
                throw new InvalidOperationException("VM 方案、流程或输出映射已变化，请重新运行并绑定。");
            if ((DateTime.UtcNow - frame.ReceivedUtc).TotalSeconds > 30 || current.projectRevision != frame.Info.sceneRevision)
                throw new InvalidOperationException("结果已过期或拍照后场景/机器人已变化，请重新拍照检测。");
            if (!Json.Finite(result.X) || !Json.Finite(result.Y) || !Json.Finite(result.Rz)) throw new InvalidOperationException("VM 坐标不是有限数。");
        }
    }
}
