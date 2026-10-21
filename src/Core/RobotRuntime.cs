namespace AxisLab.Core;

public sealed partial class ProjectStore
{
    private sealed class Motion(double[] joints)
    {
        public double[] Joints = [.. joints];
        public MotionPath[] Paths = [];
        public int PathIndex;
        public double Elapsed;
        public double Progress;
        public string Status = "Idle";
        public Guid? CommandId;
        public long PoseId = 1;
        public string? Message;
        public string? ErrorCode;
    }
    private readonly Dictionary<Guid, Motion> motions = [];
    public bool HasActiveMotion { get { lock (gate) return motions.Values.Any(m => m.Status == "Running"); } }
    private void EnsureIdle()
    {
        ExpireControls();
        CheckProgramController(null);
        if (motions.Values.Any(m => m.Status == "Running"))
            throw new DomainException("MotionActive", "机械臂正在运动，请先停止后再编辑、保存或切换工程。", 409);
        if (controls.Count > 0) throw new DomainException("ExternalControlActive", "外部客户端持有控制权，请先停止或释放控制权。", 409);
    }
    private void ReconcileMotions(bool reset)
    {
        if (reset) motions.Clear();
        foreach (var id in motions.Keys.Where(id => !project.Devices.Any(d => d.Id == id && d.Robot is not null)).ToArray()) motions.Remove(id);
        foreach (var robot in project.Devices.Where(d => d.Robot is not null))
            if (!motions.ContainsKey(robot.Id)) motions[robot.Id] = new(robot.Robot!.JointPositions);
    }
    private ProjectDocument WithJointPositions() => project with
    {
        TeachPoints = project.TeachPoints!.Select(p => p with { Joints = [.. p.Joints] }).ToArray(),
        Devices = project.Devices.Select(d => d.Robot is null ? d : d with
        { Robot = d.Robot with { JointPositions = [.. motions[d.Id].Joints] } }).ToArray()
    };
    public WorkspaceSnapshot MoveJoints(Guid id, double[] target, double speedPercent, long expected, Guid? controller = null)
    {
        lock (gate)
        {
            Check(expected);
            CheckRobotAvailable(id, controller);
            return StartPaths(id, [PlanJoints(id, motions[id].Joints, target, speedPercent)]);
        }
    }
    public WorkspaceSnapshot StopRobot(Guid id)
    {
        lock (gate)
        {
            Find(id);
            if (!motions.TryGetValue(id, out var motion)) throw new DomainException("NotRobot", "此设备不是机械臂。");
            controls.Remove(id);
            if(programReservation.HasValue)programStopRequested=true;
            if (motion.Status == "Running") { motion.Status = "Cancelled"; revision++; dirty = true; }
            return Snapshot();
        }
    }
    public void StopAllRobots()
    {
        lock (gate)
        {
            controls.Clear();
            if(programReservation.HasValue)programStopRequested=true;
            var active = motions.Values.Where(m => m.Status == "Running").ToArray();
            foreach (var motion in active) motion.Status = "Cancelled";
            if (active.Length > 0) { revision++; dirty = true; }
        }
    }
    public void AdvanceMotions(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (gate)
        {
            ExpireControls();
            while (seconds > 1e-9 && motions.Values.Any(m => m.Status == "Running"))
            {
                var step = Math.Min(.02, seconds);
                var active = motions.Where(p => p.Value.Status == "Running").ToArray();
                var candidates = new Dictionary<Guid, double[]>();
                foreach (var (id, motion) in active)
                {
                    var path = motion.Paths[motion.PathIndex];
                    var next = path.Evaluate(MotionMath.Smooth(Math.Min(1, (motion.Elapsed+step)/path.Duration)));
                    var g = Find(id).Robot!.Geometry!; var tool = Find(id).Robot!.ToolTransform;
                    var reach = g.BaseHeight+g.ShoulderHeight+g.UpperArm+g.Forearm+g.Wrist1+g.Wrist2+g.FlangeLength+Math.Sqrt(tool.X*tool.X+tool.Y*tool.Y+tool.Z*tool.Z);
                    var travel = next.Zip(motion.Joints).Sum(p => Math.Abs(p.First-p.Second))*Math.PI/180*reach;
                    if (travel > 2) step = Math.Min(step, step*2/travel);
                }
                seconds -= step;
                foreach (var (id, motion) in active)
                {
                    var path = motion.Paths[motion.PathIndex]; motion.Elapsed += step;
                    candidates[id] = path.Evaluate(MotionMath.Smooth(Math.Min(1, motion.Elapsed/path.Duration)));
                }
                var runtime = BuildRuntime(candidates);
                var issue = active.Select(p => Collision.Check(project, runtime, p.Key, 0)).FirstOrDefault(x => x is not null);
                if (issue is not null)
                {
                    foreach (var (id, motion) in active) { motion.Status = "Failed"; motion.Message = issue; motion.ErrorCode = "CollisionDetected"; controls.Remove(id); }
                    revision++; dirty = true; break;
                }
                foreach (var (id, motion) in active)
                {
                    var path = motion.Paths[motion.PathIndex]; motion.Joints = candidates[id]; motion.PoseId++;
                    motion.Progress = (motion.PathIndex+Math.Min(1, motion.Elapsed/path.Duration))/motion.Paths.Length;
                    if (motion.Elapsed >= path.Duration)
                    {
                        motion.Joints = [.. path.Nodes[^1]];
                        if (motion.PathIndex+1 == motion.Paths.Length) { motion.Status = "Completed"; motion.Progress = 1; }
                        else { motion.PathIndex++; motion.Elapsed = 0; }
                    }
                }
            }
        }
    }
    public RuntimeSnapshot GetRuntime() { lock (gate) { ExpireControls(); return BuildRuntime(); } }
    private RuntimeSnapshot BuildRuntime(IReadOnlyDictionary<Guid, double[]>? overrides = null)
    {
        var fk = project.Devices.Where(d => d.Robot is not null).ToDictionary(d => d.Id,
            d => RobotModels.Forward(d.Robot!, overrides?.GetValueOrDefault(d.Id) ?? motions[d.Id].Joints));
        var byId = project.Devices.ToDictionary(d => d.Id);
        var world = new Dictionary<Guid, RigidTransform>();
        RigidTransform World(Device d)
        {
            if (world.TryGetValue(d.Id, out var cached)) return cached;
            var parent = d.ParentId.HasValue ? World(byId[d.ParentId.Value]) : RigidTransform.Identity;
            if (d.ParentId.HasValue && d.AttachmentFrameId != "base")
            {
                var parentFk = fk[d.ParentId.Value];
                parent = parent.Then(new(d.AttachmentFrameId == "flange" ? parentFk.Flange.Matrix : parentFk.Tcp.Matrix));
            }
            return world[d.Id] = parent.Then(RigidTransform.FromPose(d.Transform));
        }
        foreach (var d in project.Devices) World(d);
        return new(sessionId, revision,
            fk.Select(pair =>
            {
                var m = motions[pair.Key]; var k = pair.Value; var basis = world[pair.Key];
                var path = m.Paths.ElementAtOrDefault(m.PathIndex);
                return new RobotState(pair.Key, m.PoseId, [.. (overrides?.GetValueOrDefault(pair.Key) ?? m.Joints)], m.Status, m.CommandId, m.Progress,
                    k.JointMatrices, k.Flange, k.Tcp, basis.Then(new(k.Flange.Matrix)).Frame(), basis.Then(new(k.Tcp.Matrix)).Frame(),
                    controls.GetValueOrDefault(pair.Key)?.Owner, ControlRemaining(pair.Key),
                    basis.Then(RigidTransform.FromPose(Find(pair.Key).Robot!.WorkTransform ?? new())).Frame(), path?.Kind ?? "Idle",
                    path is null ? 0 : m.PathIndex+1, m.Paths.Length, m.Message, m.ErrorCode, path?.TeachPointId);
            }).ToArray(), project.Devices.Select(d => new DeviceFrame(d.Id, world[d.Id].Frame())).ToArray(),programReservation);
    }
}
