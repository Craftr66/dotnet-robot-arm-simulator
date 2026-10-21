using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace AxisLab.Core;

public record TeachPoint(Guid Id, int Number, Guid RobotId, string Name, double[] Joints, string Frame,
    Pose Target, string DependencySignature);
public record TeachPointView(TeachPoint Point, bool Stale, string? Reason);

public sealed partial class ProjectStore
{
    private static void ValidateTeaching(ProjectDocument doc)
    {
        var points = doc.TeachPoints ?? [];
        if (points.Length > 256 || points.Any(p => p is null) || points.Select(p => p.Id).Distinct().Count() != points.Length ||
            points.Select(p => p.Number).Distinct().Count() != points.Length || doc.NextTeachPointNumber is < 1 or > 65536)
            throw new DomainException("InvalidTeachPoints", "示教点列表无效或超过 256 个上限。");
        foreach (var p in points)
        {
            var robot = doc.Devices.FirstOrDefault(d => d.Id == p.RobotId)?.Robot;
            if (p.Id == Guid.Empty || p.Number < 1 || p.Number >= doc.NextTeachPointNumber || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80 ||
                robot is null || p.Frame is not ("base" or "world" or "work") || p.DependencySignature is null || p.DependencySignature.Length != 64)
                throw new DomainException("InvalidTeachPoints", "示教点身份、名称、机器人或坐标记录无效。");
            RobotModels.ValidateJoints(robot.ModelId, p.Joints); MotionMath.ValidatePose(p.Target);
        }
    }
    private string Signature(Guid robotId)
    {
        var d = Find(robotId); var runtime = BuildRuntime();
        // Names and current robot joints do not affect calibration dependencies.
        var text = JsonSerializer.Serialize(new { d.Robot!.ModelId, d.Robot.Geometry, d.Robot.ToolTransform, d.Robot.WorkTransform,
            Basis = runtime.Devices.Single(f => f.DeviceId == robotId).World.Matrix,
            Obstacles = project.Devices.Where(o => o.TypeId == "geometry.box" || o.Robot is not null && o.Id != robotId)
                .OrderBy(o => o.Id).Select(o => new { o.Id, o.Visible, o.ParentId, o.AttachmentFrameId, o.Transform, o.Parameters.SizeX, o.Parameters.SizeY, o.Parameters.SizeZ, o.Robot?.ModelId, o.Robot?.Geometry, o.Robot?.ToolTransform }) }, Catalog.Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
    private TeachPointView[] TeachingViews()
    {
        var signatures = (project.TeachPoints ?? []).Select(p => p.RobotId).Distinct().ToDictionary(id => id, Signature);
        return (project.TeachPoints ?? []).Select(p => {
        var stale = p.DependencySignature != signatures[p.RobotId];
        return new TeachPointView(p with { Joints = [.. p.Joints] }, stale, stale ? "机械臂、工具、工件坐标或场景障碍物已改变，请重新校验。" : null);
        }).ToArray();
    }
    private Pose RecordedPose(Guid robotId, double[] joints, string frame)
    {
        var d = Find(robotId); var tcp = new RigidTransform(RobotModels.Forward(d.Robot!, joints).Tcp.Matrix);
        return frame switch
        {
            "base" => tcp.Frame().Pose,
            "world" => new RigidTransform(BuildRuntime().Devices.Single(f => f.DeviceId == robotId).World.Matrix).Then(tcp).Frame().Pose,
            "work" => RigidTransform.FromPose(d.Robot!.WorkTransform!).Inverse().Then(tcp).Frame().Pose,
            _ => throw new DomainException("InvalidFrame", "坐标系须为 base/world/work。")
        };
    }
    public WorkspaceSnapshot RecordTeachPoint(Guid robotId, string name, string frame, long expected)
    {
        lock (gate)
        {
            Check(expected); EnsureIdle(); CheckRobotAvailable(robotId, null);
            if (string.IsNullOrWhiteSpace(name)) throw new DomainException("InvalidTeachPoints", "示教点名称不能为空。");
            if (project.NextTeachPointNumber > 65535) throw new DomainException("TeachNumberExhausted", "示教点编号已用尽。");
            var q = motions[robotId].Joints;
            var issue = Collision.Check(project, BuildRuntime(), robotId);
            if (issue is not null) throw new DomainException("CollisionDetected", issue, 409);
            var point = new TeachPoint(Guid.NewGuid(), project.NextTeachPointNumber, robotId, name.Trim(), [.. q], frame, RecordedPose(robotId, q, frame), Signature(robotId));
            return Commit(project with { TeachPoints = [.. project.TeachPoints!, point], NextTeachPointNumber = project.NextTeachPointNumber+1 });
        }
    }
    public WorkspaceSnapshot EditTeachPoint(Guid id, string name, string action, long expected)
    {
        lock (gate)
        {
            Check(expected); EnsureIdle();
            var point = project.TeachPoints!.FirstOrDefault(p => p.Id == id) ?? throw new DomainException("TeachPointNotFound", "示教点不存在。", 404);
            if (action == "delete") return Commit(project with { TeachPoints = project.TeachPoints!.Where(p => p.Id != id).ToArray() });
            if (string.IsNullOrWhiteSpace(name)) throw new DomainException("InvalidTeachPoints", "示教点名称不能为空。");
            if (action is not ("rename" or "update" or "revalidate")) throw new DomainException("InvalidTeachAction", "示教操作无效。");
            var updated = point with { Name = name.Trim() };
            if (action != "rename")
            {
                var q = action == "update" ? motions[point.RobotId].Joints : point.Joints;
                PlanJoints(point.RobotId, q, q, 25);
                updated = updated with { Joints = [.. q], Target = RecordedPose(point.RobotId, q, point.Frame), DependencySignature = Signature(point.RobotId) };
            }
            return Commit(project with { TeachPoints = project.TeachPoints!.Select(p => p.Id == id ? updated : p).ToArray() });
        }
    }
    public WorkspaceSnapshot PlayTeachPoints(Guid robotId, Guid[] ids, string kind, double speed, long expected, Guid? controller = null)
    {
        lock (gate)
        {
            Check(expected); CheckRobotAvailable(robotId, controller);
            if (ids is null || ids.Length is < 1 or > 256) throw new DomainException("InvalidTeachPoints", "请选择 1 至 256 个示教点。");
            if (kind is not ("MoveJ" or "MoveL")) throw new DomainException("InvalidMotionKind", "运动类型须为 MoveJ 或 MoveL。");
            var plans = new List<MotionPath>(); var start = motions[robotId].Joints;
            var signature = Signature(robotId);
            var budget = Stopwatch.StartNew();
            foreach (var id in ids)
            {
                var p = project.TeachPoints!.FirstOrDefault(p => p.Id == id && p.RobotId == robotId) ?? throw new DomainException("TeachPointNotFound", "找不到此机械臂的示教点。", 404);
                if (p.DependencySignature != signature) throw new DomainException("StaleTeachPoint", $"「{p.Name}」依赖已改变，请重新校验后执行。", 409);
                var plan = kind == "MoveJ" ? PlanJoints(robotId, start, p.Joints, speed, p.Id, budget) :
                    PlanCartesian(robotId, start, new(p.Target, p.Frame, "MoveL", speed, Find(robotId).Robot!.ModelId == RobotModels.ThreeAxis), p.Id, budget);
                plans.Add(plan); start = plan.Nodes[^1];
            }
            return StartPaths(robotId, plans.ToArray());
        }
    }
    public WorkspaceSnapshot PlayTeachNumber(Guid robotId, int number, double speed, Guid controller)
    {
        lock (gate)
        {
            var point = project.TeachPoints!.FirstOrDefault(p => p.Number == number && p.RobotId == robotId) ?? throw new DomainException("TeachPointNotFound", "示教点编号不存在。", 404);
            return PlayTeachPoints(robotId, [point.Id], "MoveJ", speed, revision, controller);
        }
    }
}
