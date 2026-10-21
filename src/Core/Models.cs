using System.Text.Json;

namespace AxisLab.Core;

public record Pose(double X = 0, double Y = 0, double Z = 0,
    double Rx = 0, double Ry = 0, double Rz = 0);
public record DeviceParameters(int Width = 640, int Height = 480,
    double Fx = 554.256, double Fy = 554.256, double Cx = 320, double Cy = 240,
    double SizeX = 120, double SizeY = 120, double SizeZ = 120);
public record Device(Guid Id, string TypeId, string Name, int DeviceNumber,
    Guid? ParentId, Pose Transform, DeviceParameters Parameters,
    long ConfigurationRevision = 1, bool Visible = true,
    string AttachmentFrameId = "base", RobotSettings? Robot = null);
public record ProjectDocument(int SchemaVersion, Guid Id, string Name,
    int NextDeviceNumber, Device[] Devices, TeachPoint[]? TeachPoints = null, int NextTeachPointNumber = 1, ProgramDefinition? Program = null);
public record WorkspaceSnapshot(Guid SessionId, long Revision, bool Dirty,
    ProjectDocument Project, bool HasSavedFile, RuntimeSnapshot Runtime, TeachPointView[]? Teaching = null);
public record DeviceType(string TypeId, string Name, string Description, string[] Capabilities);

public static class Catalog
{
    public static readonly DeviceType[] Types = [
        new("robot.generic6r", "六轴机械臂", "通用 6R · 关节/位姿运动与示教", ["Configure", "JointMotion", "ForwardKinematics", "Flange", "Tool", "InverseKinematics", "CartesianMotion", "LinearMotion", "Teaching", "WorkFrame", "Collision"]),
        new("robot.generic3r", "三轴机械臂", "通用 3R · 位置运动与示教", ["Configure", "JointMotion", "ForwardKinematics", "Flange", "Tool", "InverseKinematics", "CartesianPosition", "LinearMotion", "Teaching", "WorkFrame", "Collision"]),
        new("camera.pinhole", "针孔相机", "针孔成像 · PNG 拍照与传图", ["Configure", "Mount", "Capture", "ReadImage"]),
        new("geometry.box", "长方体", "工作台、工件与场景障碍物", ["Configure"]),
        new("calibration.board", "九点标定板", "局部 XY 平面 · 3×3 黑圆阵列，中心间距为板宽/高的三分之一", ["Configure", "CalibrationPattern"]),
        new("calibration.marker", "TCP 标定圆点", "黑圆中心为对象原点 · 可零偏移挂载 TCP", ["Configure", "Mount", "CalibrationPattern"])
    ];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, MaxDepth = 2048, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
}

public sealed class DomainException(string code, string message, int status = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
