namespace AxisLab.Core;

public record RobotSettings(string ModelId, double[] JointPositions, Pose ToolTransform, RobotGeometry? Geometry = null, Pose? WorkTransform = null);
public record RobotGeometry(double BaseHeight = 135, double ShoulderHeight = 110, double UpperArm = 300,
    double Forearm = 280, double Wrist1 = 80, double Wrist2 = 70, double FlangeLength = 55,
    double Joint1Diameter = 110, double Joint2Diameter = 110, double Joint3Diameter = 110,
    double Joint4Diameter = 74, double Joint5Diameter = 74, double Joint6Diameter = 74);
public record JointDefinition(string Name, string Axis, Pose Offset, double Minimum, double Maximum, double MaxVelocity);
public record CoordinateFrame(Pose Pose, double[] Matrix);
public record KinematicResult(double[][] JointMatrices, CoordinateFrame Flange, CoordinateFrame Tcp);
public record RobotState(Guid DeviceId, long PoseId, double[] Joints, string Status, Guid? CommandId,
    double Progress, double[][] JointMatrices, CoordinateFrame FlangeBase, CoordinateFrame TcpBase,
    CoordinateFrame FlangeWorld, CoordinateFrame TcpWorld, Guid? ControlOwnerId = null, int ControlRemainingMilliseconds = 0,
    CoordinateFrame? WorkWorld = null, string MotionKind = "Idle", int StepIndex = 0, int StepCount = 0,
    string? MotionMessage = null, string? MotionErrorCode = null, Guid? ActiveTeachPointId = null);
public record DeviceFrame(Guid DeviceId, CoordinateFrame World);
public record RuntimeSnapshot(Guid SessionId, long ProjectRevision, RobotState[] Robots, DeviceFrame[] Devices, Guid? ProgramRunId = null);

/// <summary>Double precision, column-major rigid matrices; p_A = T_A_B * p_B.</summary>
public sealed class RigidTransform
{
    public double[] Values { get; }
    public RigidTransform(double[] values) => Values = [.. values];
    public static RigidTransform Identity => FromPose(new());
    public RigidTransform Inverse()
    {
        var m = Values;
        return new([m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0,
            -(m[0]*m[12]+m[1]*m[13]+m[2]*m[14]), -(m[4]*m[12]+m[5]*m[13]+m[6]*m[14]), -(m[8]*m[12]+m[9]*m[13]+m[10]*m[14]), 1]);
    }
    public static RigidTransform FromPose(Pose p)
    {
        const double d = Math.PI / 180;
        var cx = Math.Cos(p.Rx * d); var sx = Math.Sin(p.Rx * d);
        var cy = Math.Cos(p.Ry * d); var sy = Math.Sin(p.Ry * d);
        var cz = Math.Cos(p.Rz * d); var sz = Math.Sin(p.Rz * d);
        return new([
            cz * cy, sz * cy, -sy, 0,
            cz * sy * sx - sz * cx, sz * sy * sx + cz * cx, cy * sx, 0,
            cz * sy * cx + sz * sx, sz * sy * cx - cz * sx, cy * cx, 0,
            p.X, p.Y, p.Z, 1]);
    }
    public RigidTransform Then(RigidTransform child)
    {
        var result = new double[16];
        for (var c = 0; c < 4; c++)
            for (var r = 0; r < 4; r++)
                for (var k = 0; k < 4; k++) result[c * 4 + r] += Values[k * 4 + r] * child.Values[c * 4 + k];
        return new(result);
    }
    public CoordinateFrame Frame()
    {
        var m = Values;
        var ry = Math.Asin(Math.Clamp(-m[2], -1, 1));
        double rx, rz;
        if (Math.Abs(Math.Cos(ry)) > 1e-8) { rx = Math.Atan2(m[6], m[10]); rz = Math.Atan2(m[1], m[0]); }
        else { rx = 0; rz = Math.Atan2(-m[4], m[5]); }
        const double degrees = 180 / Math.PI;
        return new(new(m[12], m[13], m[14], rx * degrees, ry * degrees, rz * degrees), [.. m]);
    }
}

public static class RobotModels
{
    public const string ThreeAxis = "axislab-3r-v1";
    public static bool IsRobot(string type) => type is "robot.generic6r" or "robot.generic3r";
    public static RobotSettings Default(string type) => type == "robot.generic3r"
        ? new(ThreeAxis, [0, 0, 0], new(0, 0, 80), new(), new()) : Generic6R.Default();
    public static JointDefinition[] Definitions(string model) => model switch
    {
        Generic6R.ModelId => Generic6R.Definitions,
        ThreeAxis => Generic6R.Definitions.Take(3).ToArray(),
        _ => throw new DomainException("UnsupportedRobot", "不支持此运动学模型。")
    };
    public static void Validate(RobotSettings settings, string type)
    {
        if (settings.ModelId != (type == "robot.generic3r" ? ThreeAxis : Generic6R.ModelId))
            throw new DomainException("UnsupportedRobot", "机器人类型与模型不匹配。");
        ValidateJoints(settings.ModelId, settings.JointPositions);
        Generic6R.ValidateGeometry(settings.Geometry!);
        var p = settings.ToolTransform;
        if (p is null || new[] { p.X, p.Y, p.Z, p.Rx, p.Ry, p.Rz }.Any(x => !double.IsFinite(x) || Math.Abs(x) > 10000))
            throw new DomainException("InvalidTool", "工具位姿须有限且绝对值不超过 10000。");
        MotionMath.ValidatePose(settings.WorkTransform!, "InvalidWorkFrame");
    }
    public static void ValidateJoints(string model, double[] joints)
    {
        var definitions = Definitions(model);
        if (joints is null || joints.Length != definitions.Length || joints.Any(x => !double.IsFinite(x)))
            throw new DomainException("InvalidJoints", $"需要 {definitions.Length} 个有限关节角，单位 deg。");
        for (var i = 0; i < joints.Length; i++)
            if (joints[i] < definitions[i].Minimum || joints[i] > definitions[i].Maximum)
                throw new DomainException("JointLimit", $"J{i + 1} 超限：{definitions[i].Minimum} 至 {definitions[i].Maximum} deg。");
    }
    public static KinematicResult Forward(RobotSettings settings, double[] joints)
    {
        if (settings.ModelId == Generic6R.ModelId) return Generic6R.Forward(joints, settings.ToolTransform, settings.Geometry);
        ValidateJoints(settings.ModelId, joints);
        var g = settings.Geometry!; Generic6R.ValidateGeometry(g);
        var current = RigidTransform.Identity; var frames = new double[3][];
        var definitions = Generic6R.DefinitionsFor(g);
        for (var i = 0; i < 3; i++)
        {
            current = current.Then(RigidTransform.FromPose(definitions[i].Offset))
                .Then(RigidTransform.FromPose(i == 0 ? new(Rz: joints[i]) : new(Ry: joints[i])));
            frames[i] = [.. current.Values];
        }
        var flange = current.Then(RigidTransform.FromPose(new(g.Forearm + g.FlangeLength, 0, 0, Ry: 90)));
        return new(frames, flange.Frame(), flange.Then(RigidTransform.FromPose(settings.ToolTransform)).Frame());
    }
}

public static class Generic6R
{
    public const string ModelId = "axislab-6r-v1";
    public static JointDefinition[] Definitions => [
        new("J1", "Z", new(0, 0, 135), -180, 180, 90),
        new("J2", "Y", new(0, 0, 110), -100, 130, 75),
        new("J3", "Y", new(0, 0, 300), -150, 150, 90),
        new("J4", "X", new(280, 0, 0), -180, 180, 150),
        new("J5", "Y", new(80, 0, 0), -120, 120, 150),
        new("J6", "X", new(70, 0, 0), -360, 360, 180)
    ];
    public static RobotSettings Default() => new(ModelId, [0, 0, 0, 0, 0, 0], new(0, 0, 80), new(), new());
    public static JointDefinition[] DefinitionsFor(RobotGeometry g)
    {
        var offsets = new Pose[] { new(0, 0, g.BaseHeight), new(0, 0, g.ShoulderHeight),
            new(0, 0, g.UpperArm), new(g.Forearm, 0, 0), new(g.Wrist1, 0, 0), new(g.Wrist2, 0, 0) };
        return Definitions.Select((d, i) => d with { Offset = offsets[i] }).ToArray();
    }
    public static void ValidateGeometry(RobotGeometry g)
    {
        if (g is null) throw new DomainException("InvalidGeometry", "缺少机械臂尺寸。");
        var lengths = new[] { g.BaseHeight, g.ShoulderHeight, g.UpperArm, g.Forearm, g.Wrist1, g.Wrist2, g.FlangeLength };
        if (lengths.Any(v => !double.IsFinite(v) || v < 5 || v > 3000) || g.BaseHeight < 40)
            throw new DomainException("InvalidGeometry", "连杆尺寸须为 5—3000 mm，基座高度至少 40 mm。");
        var diameters = new[] { g.Joint1Diameter, g.Joint2Diameter, g.Joint3Diameter, g.Joint4Diameter, g.Joint5Diameter, g.Joint6Diameter };
        if (diameters.Any(v => !double.IsFinite(v) || v < 10 || v > 500))
            throw new DomainException("InvalidGeometry", "关节直径须为 10—500 mm。");
    }
    public static void Validate(RobotSettings settings)
    {
        if (settings.ModelId != ModelId) throw new DomainException("UnsupportedRobot", "不支持此运动学模型。");
        ValidateJoints(settings.JointPositions);
        ValidateGeometry(settings.Geometry!);
        if (settings.ToolTransform is null) throw new DomainException("InvalidTool", "缺少工具坐标。");
        var p = settings.ToolTransform;
        if (new[] { p.X, p.Y, p.Z, p.Rx, p.Ry, p.Rz }.Any(x => !double.IsFinite(x) || Math.Abs(x) > 10000))
            throw new DomainException("InvalidTool", "工具坐标必须有限且绝对值不超过 10000。");
    }
    public static void ValidateJoints(double[] joints)
    {
        if (joints is null || joints.Length != 6 || joints.Any(x => !double.IsFinite(x)))
            throw new DomainException("InvalidJoints", "需要六个有限数值关节角，单位 deg。");
        var definitions = Definitions;
        for (var i = 0; i < 6; i++)
            if (joints[i] < definitions[i].Minimum || joints[i] > definitions[i].Maximum)
                throw new DomainException("JointLimit", $"J{i + 1} 超限：允许 {definitions[i].Minimum} 至 {definitions[i].Maximum} deg。");
    }
    public static KinematicResult Forward(double[] joints, Pose tool, RobotGeometry? geometry = null)
    {
        ValidateJoints(joints);
        var current = RigidTransform.Identity;
        var frames = new double[6][];
        var dimensions = geometry ?? new RobotGeometry();
        ValidateGeometry(dimensions);
        var definitions = DefinitionsFor(dimensions);
        for (var i = 0; i < 6; i++)
        {
            var angle = definitions[i].Axis switch { "X" => new Pose(Rx: joints[i]), "Y" => new Pose(Ry: joints[i]), _ => new Pose(Rz: joints[i]) };
            current = current.Then(RigidTransform.FromPose(definitions[i].Offset)).Then(RigidTransform.FromPose(angle));
            frames[i] = [.. current.Values];
        }
        var flange = current.Then(RigidTransform.FromPose(new(dimensions.FlangeLength, 0, 0, Ry: 90)));
        return new(frames, flange.Frame(), flange.Then(RigidTransform.FromPose(tool)).Frame());
    }
}
