using System.Text.Json;

namespace AxisLab.WpfDemo;

public static class AxisLabTypes
{
    public const ushort Robot6R = 1;
    public const ushort Robot3R = 2;
    public const ushort Camera = 10;
    public const ushort Box = 20;
    public const ushort CalibrationBoard = 21;
    public const ushort CalibrationMarker = 22;

    public const ushort CapConfigure = 1 << 0;
    public const ushort CapJointMotion = 1 << 1;
    public const ushort CapForwardKinematics = 1 << 2;
    public const ushort CapFlange = 1 << 3;
    public const ushort CapTool = 1 << 4;
    public const ushort CapCapture = 1 << 5;
    public const ushort CapReadImage = 1 << 6;

    public static string TypeName(ushort type) => type switch
    {
        Robot6R => "六轴机械臂",
        Robot3R => "三轴机械臂",
        Camera => "针孔相机",
        Box => "长方体",
        CalibrationBoard => "九点标定板",
        CalibrationMarker => "TCP 标定圆点",
        _ => $"未知({type})"
    };

    public static string CapabilityText(ushort caps)
    {
        var list = new List<string>();
        if ((caps & CapConfigure) != 0) list.Add("配置");
        if ((caps & CapJointMotion) != 0) list.Add("关节运动");
        if ((caps & CapForwardKinematics) != 0) list.Add("FK");
        if ((caps & CapFlange) != 0) list.Add("法兰");
        if ((caps & CapTool) != 0) list.Add("工具");
        if ((caps & CapCapture) != 0) list.Add("拍照");
        if ((caps & CapReadImage) != 0) list.Add("读图");
        return string.Join(" / ", list);
    }

    public static ushort InferType(string typeId) => typeId switch
    {
        "robot.generic6r" => Robot6R,
        "robot.generic3r" => Robot3R,
        "camera.pinhole" => Camera,
        "geometry.box" => Box,
        "calibration.board" => CalibrationBoard,
        "calibration.marker" => CalibrationMarker,
        _ => 0
    };

    public static ushort InferCapabilities(string typeId) => typeId switch
    {
        "robot.generic6r" or "robot.generic3r" => (ushort)(CapConfigure | CapJointMotion | CapForwardKinematics | CapFlange | CapTool),
        "camera.pinhole" => (ushort)(CapConfigure | CapCapture | CapReadImage),
        "geometry.box" or "calibration.board" or "calibration.marker" => CapConfigure,
        _ => 0
    };
}

public sealed class HealthInfo
{
    public string Status { get; init; } = "";
    public string Version { get; init; } = "";
    public int Stage { get; init; }
    public string Mode { get; init; } = "";
}

public sealed class CommunicationStatus
{
    public string BindAddress { get; init; } = "";
    public int ModbusPort { get; init; }
    public bool Listening { get; init; }
    public string? Error { get; init; }
    public int HttpPort { get; init; }
    public int UnitId { get; init; }
    public int ProtocolVersion { get; init; }
    public int ActiveClients { get; init; }
    public string SessionToken { get; init; } = "";
}

public sealed class ProjectListEntry
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int Devices { get; init; }
    public DateTimeOffset Modified { get; init; }
    public override string ToString() => $"{Name}  ({Devices} 台设备)";
}

public sealed class ProjectSnapshot
{
    public Guid SessionId { get; init; }
    public int Revision { get; init; }
    public bool Dirty { get; init; }
    public ProjectDocument Project { get; init; } = new();
    public bool HasSavedFile { get; init; }
    public RuntimeSnapshot Runtime { get; init; } = new();
}

public sealed class ProjectDocument
{
    public int SchemaVersion { get; init; }
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int NextDeviceNumber { get; init; }
    public List<DeviceDocument> Devices { get; init; } = [];
}

public sealed class DeviceDocument
{
    public Guid Id { get; init; }
    public string TypeId { get; init; } = "";
    public string Name { get; init; } = "";
    public ushort DeviceNumber { get; init; }
    public Guid? ParentId { get; init; }
    public TransformInfo Transform { get; init; } = new();
    public DeviceParameters Parameters { get; init; } = new();
    public uint ConfigurationRevision { get; init; }
    public bool Visible { get; init; }
    public string AttachmentFrameId { get; init; } = "base";
    public RobotDocument? Robot { get; init; }
}

public sealed class TransformInfo
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Rx { get; init; }
    public double Ry { get; init; }
    public double Rz { get; init; }
}

public sealed class DeviceParameters
{
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fx { get; init; }
    public double Fy { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }
    public double SizeX { get; init; }
    public double SizeY { get; init; }
    public double SizeZ { get; init; }
}

public sealed class RobotDocument
{
    public string ModelId { get; init; } = "";
    public double[] JointPositions { get; init; } = [];
    public ToolTransform ToolTransform { get; init; } = new();
    public RobotGeometry Geometry { get; init; } = new();
}

public sealed class ToolTransform
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Rx { get; init; }
    public double Ry { get; init; }
    public double Rz { get; init; }
}

public sealed class RobotGeometry
{
    public double BaseHeight { get; init; }
    public double ShoulderHeight { get; init; }
    public double UpperArm { get; init; }
    public double Forearm { get; init; }
    public double Wrist1 { get; init; }
    public double Wrist2 { get; init; }
    public double FlangeLength { get; init; }
}

public sealed class RuntimeSnapshot
{
    public Guid SessionId { get; init; }
    public int ProjectRevision { get; init; }
    public List<RobotRuntime> Robots { get; init; } = [];
    public List<DeviceWorld> Devices { get; init; } = [];
}

public sealed class RobotRuntime
{
    public Guid DeviceId { get; init; }
    public ulong PoseId { get; init; }
    public double[] Joints { get; init; } = [];
    public string Status { get; init; } = "";
    public string? CommandId { get; init; }
    public double Progress { get; init; }
    public PoseMatrix FlangeBase { get; init; } = new();
    public PoseMatrix TcpBase { get; init; } = new();
    public PoseMatrix FlangeWorld { get; init; } = new();
    public PoseMatrix TcpWorld { get; init; } = new();
    public JsonElement? ControlOwnerId { get; init; }
    public int ControlRemainingMilliseconds { get; init; }
}

public sealed class DeviceWorld
{
    public Guid DeviceId { get; init; }
    public PoseMatrix World { get; init; } = new();
}

public sealed class PoseMatrix
{
    public PoseInfo Pose { get; init; } = new();
    public double[] Matrix { get; init; } = [];
}

public sealed class PoseInfo
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Rx { get; init; }
    public double Ry { get; init; }
    public double Rz { get; init; }
}

public sealed class RobotModelInfo
{
    public string ModelId { get; init; } = "";
    public List<JointDefinition> Joints { get; init; } = [];
}

public sealed class JointDefinition
{
    public string Name { get; init; } = "";
    public string Axis { get; init; } = "";
    public OffsetInfo Offset { get; init; } = new();
    public double Minimum { get; init; }
    public double Maximum { get; init; }
    public double MaxVelocity { get; init; }
}

public sealed class OffsetInfo
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double Rx { get; init; }
    public double Ry { get; init; }
    public double Rz { get; init; }
}

/// <summary>合并 HTTP 设备信息与 Modbus 目录信息后的设备视图。</summary>
public sealed class DeviceInfo
{
    public required ushort Number { get; init; }
    public required string Name { get; init; }
    public required string TypeId { get; init; }
    public required ushort Type { get; init; }
    public required ushort Axes { get; init; }
    public required ushort Capabilities { get; init; }
    public Guid Id { get; init; }
    public Guid? ParentId { get; init; }
    public TransformInfo Transform { get; init; } = new();
    public DeviceParameters Parameters { get; init; } = new();
    public RobotDocument? Robot { get; init; }
    public uint ConfigurationRevision { get; init; }
    public bool IsRobot => Type is AxisLabTypes.Robot3R or AxisLabTypes.Robot6R;
    public bool IsCamera => Type == AxisLabTypes.Camera;
    public string TypeName => AxisLabTypes.TypeName(Type);
    public string CapabilityText => AxisLabTypes.CapabilityText(Capabilities);
    public string NumberText => $"#{Number}";
    public string AxisText => Axes == 0 ? "-" : Axes.ToString();
    public string UuidText => Id.ToString();
    public override string ToString() => $"#{Number} {Name} ({TypeName})";
}

/// <summary>Modbus 状态区 0—127 的解析结果。</summary>
public sealed class DeviceState
{
    public ushort Magic { get; init; }
    public ushort ProtocolVersion { get; init; }
    public ushort Application { get; init; }
    public bool Listening { get; init; }
    public ulong SessionToken { get; init; }
    public uint SnapshotSequence { get; init; }
    public ushort DeviceCount { get; init; }
    public ushort SelectedDeviceNumber { get; init; }
    public ushort SelectedType { get; init; }
    public ushort Axes { get; init; }
    public ushort ControlOwner { get; init; }
    public ushort CommandState { get; init; }
    public ushort ErrorCode { get; init; }
    public ushort Capabilities { get; init; }
    public uint SubmittedSequence { get; init; }
    public uint AcceptedSequence { get; init; }
    public uint TerminalSequence { get; init; }
    public uint FrameToken { get; init; }
    public uint ClientId { get; init; }
    public ushort HeartbeatTimeoutSeconds { get; init; }
    public bool IdentityValid { get; init; }
    public ushort ModbusPort { get; init; }
    public ushort HttpPort { get; init; }
    public double[] Joints { get; init; } = new double[6];
    public double[] TcpBase { get; init; } = new double[6];
    public double[] FlangeBase { get; init; } = new double[6];
    public double[] TcpWorld { get; init; } = new double[6];
    public double[] DeviceWorldPose { get; init; } = new double[6];
    public ulong PoseId { get; init; }
    public ushort MotionStatus { get; init; }
    public ushort ProgressPermille { get; init; }
    public uint ConfigurationRevision { get; init; }
    public Guid DeviceUuid { get; init; }
    public double Fx { get; init; }
    public double Fy { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }
    public ushort CameraWidth { get; init; }
    public ushort CameraHeight { get; init; }
    public ushort HeartbeatRemainingMs { get; init; }

    public string SessionTokenHex => SessionToken.ToString("X16");
    public bool IsOwnedByThisConnection => ControlOwner == 1;
    public bool IsOwnedByOther => ControlOwner == 2;
    public bool IsTerminalState => CommandState >= 3;
    public bool IsSuccess => CommandState == 3;
    public double ProgressPercent => ProgressPermille / 10.0;
}

public sealed class ModbusCommandException : Exception
{
    public ushort ErrorCode { get; }
    public ushort CommandState { get; }
    public string? Detail { get; }

    public ModbusCommandException(ushort state, ushort errorCode, string message, string? detail = null)
        : base(message)
    {
        CommandState = state;
        ErrorCode = errorCode;
        Detail = detail;
    }
}

public sealed class CapturedFrame
{
    public required byte[] Png { get; init; }
    public required JsonElement Metadata { get; init; }
    public required string MetadataJson { get; init; }
    public required Guid FrameId { get; init; }
    public required Guid CaptureId { get; init; }
    public required Guid CameraId { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Sha256 { get; init; }
    public required string SessionToken { get; init; }
    public uint FrameToken { get; init; }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 2048,
        WriteIndented = true
    };
}

