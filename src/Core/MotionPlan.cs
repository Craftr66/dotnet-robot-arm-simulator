using System.Diagnostics;

namespace AxisLab.Core;

public record CartesianRequest(Pose Target, string Frame = "base", string Kind = "MoveJ", double SpeedPercent = 25, bool PositionOnly = false);
public record MotionPreview(double[] Joints, double Duration, string Kind, int Samples, bool NearSingularity, string? Warning);

internal sealed class MotionPath(double[][] nodes, double duration, string kind, bool nearSingularity = false, Guid? teachPointId = null)
{
    public double[][] Nodes { get; } = nodes;
    public double Duration { get; set; } = duration;
    public string Kind { get; } = kind;
    public bool NearSingularity { get; } = nearSingularity;
    public Guid? TeachPointId { get; } = teachPointId;
    public double[] Evaluate(double s)
    {
        s = Math.Clamp(s, 0, 1); var n = Nodes.Length - 1;
        var k = Math.Min(n - 1, (int)(s * n)); var t = s * n - k;
        return Enumerable.Range(0, Nodes[0].Length).Select(j =>
        {
            var a = Nodes[k][j]; var b = Nodes[k + 1][j];
            var v1 = k == 0 ? b - a : (b - Nodes[k - 1][j]) / 2;
            var v2 = k + 2 > n ? b - a : (Nodes[k + 2][j] - a) / 2;
            return (2*t*t*t - 3*t*t + 1)*a + (t*t*t - 2*t*t + t)*v1 + (-2*t*t*t + 3*t*t)*b + (t*t*t-t*t)*v2;
        }).ToArray();
    }
    public MotionPreview Preview() => new([.. Nodes[^1]], Duration, Kind, Nodes.Length, NearSingularity,
        NearSingularity ? "路径接近运动学奇异位形，速度已限制到 10%；请留意关节运动方向。" : null);
    public void SetDuration(RobotSettings settings, double speed)
    {
        if (!double.IsFinite(speed) || speed < 1 || speed > 100) throw new DomainException("InvalidSpeed", "速度倍率须为 1 至 100%。");
        if (NearSingularity && speed > 10) { Duration *= speed/10; speed = 10; }
        var definitions = RobotModels.Definitions(settings.ModelId); var n = Nodes.Length - 1;
        double time = .04;
        // Bound cubic derivatives over each interval, then apply quintic time scaling.
        for (var k = 0; k < n; k++) for (var j = 0; j < definitions.Length; j++)
        {
            var a = Nodes[k][j]; var b = Nodes[k+1][j];
            var v1 = k == 0 ? b-a : (b-Nodes[k-1][j])/2;
            var v2 = k+2 > n ? b-a : (Nodes[k+2][j]-a)/2;
            var c3 = 2*a-2*b+v1+v2; var c2 = -3*a+3*b-2*v1-v2;
            double D(double t) => 3*c3*t*t+2*c2*t+v1;
            var d1 = Math.Max(Math.Abs(D(0)), Math.Abs(D(1)));
            if (Math.Abs(c3) > 1e-10) { var root = -c2/(3*c3); if (root is > 0 and < 1) d1 = Math.Max(d1, Math.Abs(D(root))); }
            d1 *= n;
            var d2 = Math.Max(Math.Abs(2*c2), Math.Abs(6*c3+2*c2))*n*n;
            var velocity = definitions[j].MaxVelocity*speed/100;
            time = Math.Max(time, 1.875*d1/velocity);
            time = Math.Max(time, Math.Sqrt((5.774*d1+3.516*d2)/(2*velocity)));
        }
        Duration = Math.Max(Duration, time);
        if (Duration > 120) throw new DomainException("MotionTooLong", "当前动作超过 120 s，请提高速度或缩短目标距离。");
    }
}

public sealed partial class ProjectStore
{
    private RigidTransform TargetInBase(Guid id, Pose target, string frame)
    {
        MotionMath.ValidatePose(target); var d = Find(id);
        var basis = new RigidTransform(BuildRuntime().Devices.Single(f => f.DeviceId == id).World.Matrix);
        return frame switch
        {
            "base" => RigidTransform.FromPose(target),
            "world" => basis.Inverse().Then(RigidTransform.FromPose(target)),
            "work" => RigidTransform.FromPose(d.Robot!.WorkTransform!).Then(RigidTransform.FromPose(target)),
            _ => throw new DomainException("InvalidFrame", "坐标系须为 base/world/work。")
        };
    }
    private void CheckRobotAvailable(Guid id, Guid? controller)
    {
        CheckProgramController(controller);
        if (Find(id).Robot is null) throw new DomainException("NotRobot", "此设备不是机械臂。");
        ExpireControls();
        if (controls.TryGetValue(id, out var control) && control.Owner != controller) throw new DomainException("ControlBusy", "此机械臂已由外部客户端控制。", 409);
        if (controller.HasValue && controller!=programReservation && (control is null || control.Owner != controller)) throw new DomainException("NoControl", "请先申请机械臂控制权。", 409);
        if (motions[id].Status == "Running") throw new DomainException("RobotBusy", "机械臂正在运动。", 409);
        // Keep mounted robot bases stable throughout planning and execution.
        bool DescendsFrom(Device child)
        {
            while (child.ParentId.HasValue) { if (child.ParentId == id) return true; child = Find(child.ParentId.Value); }
            return false;
        }
        if (Find(id).ParentId.HasValue || project.Devices.Any(d => d.Robot is not null && DescendsFrom(d)))
            throw new DomainException("MountedRobotUnsupported", "运动中的机械臂基座须独立固定，请先取消机械臂之间的挂载。");
    }
    private MotionPath PlanJoints(Guid id, double[] start, double[] target, double speed, Guid? point = null, Stopwatch? budget = null)
    {
        var settings = Find(id).Robot!; RobotModels.ValidateJoints(settings.ModelId, target);
        var path = new MotionPath([[.. start], [.. target]], 0, "MoveJ", false, point);
        path.SetDuration(settings, speed); CheckPath(id, path, budget); return path;
    }
    private MotionPath PlanCartesian(Guid id, double[] start, CartesianRequest request, Guid? point = null, Stopwatch? budget = null)
    {
        var clock = budget ?? Stopwatch.StartNew();
        if (request is null) throw new DomainException("InvalidMotion", "缺少运动目标。");
        if (!double.IsFinite(request.SpeedPercent) || request.SpeedPercent is < 1 or > 100) throw new DomainException("InvalidSpeed", "速度倍率须为 1 至 100%。");
        if (request.Kind is not ("MoveJ" or "MoveL")) throw new DomainException("InvalidMotionKind", "运动类型须为 MoveJ 或 MoveL。");
        var settings = Find(id).Robot!; var positionOnly = request.PositionOnly || settings.ModelId == RobotModels.ThreeAxis;
        var target = TargetInBase(id, request.Target, request.Frame);
        var end = InverseKinematics.Solve(settings, target, start, positionOnly);
        if (request.Kind == "MoveJ")
        {
            var path = new MotionPath([[.. start], end.Joints], 0, "MoveJ", end.NearSingularity, point);
            path.SetDuration(settings, request.SpeedPercent); CheckPath(id, path, clock); return path;
        }
        var initial = new RigidTransform(RobotModels.Forward(settings, start).Tcp.Matrix);
        var length = (V3.Position(target)-V3.Position(initial)).Length;
        var angle = positionOnly ? 0 : MotionMath.RotationError(target, initial).Length;
        var count = Math.Max(2, (int)Math.Ceiling(Math.Max(length/5, angle*180/Math.PI)));
        while (count <= 512)
        {
            var nodes = new List<double[]> { start.ToArray() }; bool singular = false;
            for (var i = 1; i <= count; i++)
            {
                var pose = MotionMath.Interpolate(initial, target, (double)i/count);
                var ik = InverseKinematics.Solve(settings, pose, nodes[^1], positionOnly, false);
                if (ik.Joints.Zip(nodes[^1]).Any(p => Math.Abs(p.First-p.Second) > 20)) throw new DomainException("SingularPath", "直线路径关节分支不连续，请改变起点或目标。");
                nodes.Add(ik.Joints); singular |= ik.NearSingularity;
                if (clock.ElapsedMilliseconds > 2500) throw new DomainException("PlanningLimit", "直线路径求解超时，请缩短路径或避开奇异位形。");
            }
            var path = new MotionPath(nodes.ToArray(), 1.875*Math.Max(length/250, angle/(Math.PI/4))/(request.SpeedPercent/100), "MoveL", singular, point);
            bool accurate = true;
            for (var i = 0; i < count*4; i++)
            {
                var s = (i+.5)/(count*4); var actual = new RigidTransform(RobotModels.Forward(settings, path.Evaluate(s)).Tcp.Matrix);
                var wanted = MotionMath.Interpolate(initial, target, s);
                if ((V3.Position(actual)-V3.Position(wanted)).Length > .05 || !positionOnly && MotionMath.RotationError(wanted, actual).Length > .000873) { accurate = false; break; }
            }
            if (accurate) { path.SetDuration(settings, request.SpeedPercent); CheckPath(id, path, clock); return path; }
            count *= 2;
        }
        throw new DomainException("PlanningLimit", "直线路径插补误差过大，请缩短路径或避开奇异位形。");
    }
    private void CheckPath(Guid id, MotionPath path, Stopwatch? budget = null)
    {
        var settings = Find(id).Robot!; var g = settings.Geometry!;
        var tool = settings.ToolTransform;
        var reach = g.BaseHeight+g.ShoulderHeight+g.UpperArm+g.Forearm+g.Wrist1+g.Wrist2+g.FlangeLength+Math.Sqrt(tool.X*tool.X+tool.Y*tool.Y+tool.Z*tool.Z);
        var travel = 1.5*path.Nodes.Zip(path.Nodes.Skip(1)).Sum(p => p.First.Zip(p.Second).Sum(q => Math.Abs(q.First-q.Second)))*Math.PI/180*reach;
        var samples = Math.Max(2, (int)Math.Ceiling(travel/2));
        if (samples > 8192) throw new DomainException("PlanningLimit", "路径采样超出上限，请拆分动作。");
        var clock = budget ?? Stopwatch.StartNew();
        for (var i = 0; i <= samples; i++)
        {
            var q = path.Evaluate((double)i/samples); RobotModels.ValidateJoints(settings.ModelId, q);
            var issue = Collision.Check(project, BuildRuntime(new Dictionary<Guid, double[]> { [id] = q }), id);
            if (issue is not null) throw new DomainException("CollisionDetected", $"路径 {100.0*i/samples:F1}%：{issue}，动作未启动。", 409);
            if (clock.ElapsedMilliseconds > 2500) throw new DomainException("PlanningLimit", "路径预检超时，请减少点位、障碍物或拆分动作。");
        }
    }
    private WorkspaceSnapshot StartPaths(Guid id, MotionPath[] paths)
    {
        var motion = motions[id]; motion.Paths = paths; motion.PathIndex = 0; motion.Elapsed = 0; motion.Progress = 0;
        motion.Status = "Running"; motion.CommandId = Guid.NewGuid(); motion.ErrorCode = null; motion.Message = paths.Select(p => p.Preview().Warning).FirstOrDefault(w => w is not null);
        revision++; dirty = true; return Snapshot();
    }
    public MotionPreview PreviewCartesian(Guid id, CartesianRequest request)
    {
        lock (gate) { CheckRobotAvailable(id, null); return PlanCartesian(id, motions[id].Joints, request).Preview(); }
    }
    public WorkspaceSnapshot MoveCartesian(Guid id, CartesianRequest request, long expected, Guid? controller = null)
    {
        lock (gate) { Check(expected); CheckRobotAvailable(id, controller); return StartPaths(id, [PlanCartesian(id, motions[id].Joints, request)]); }
    }
}
