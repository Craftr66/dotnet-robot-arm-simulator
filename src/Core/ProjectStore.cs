using System.Text.Json;

namespace AxisLab.Core;

public sealed partial class ProjectStore
{
    private readonly object gate = new();
    private readonly string directory;
    private ProjectDocument project = Empty("未命名工程");
    private Guid sessionId = Guid.NewGuid();
    private long revision = 1;
    private bool dirty;

    public ProjectStore(string directory, TimeProvider? timeProvider = null)
    {
        controlClock = timeProvider ?? TimeProvider.System;
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }

    private static ProjectDocument Empty(string name) => new(5, Guid.NewGuid(), name, 1, [], [], Program:ProgramDefinition.Empty());
    private WorkspaceSnapshot Snapshot()
    {
        ExpireControls();
        return new(sessionId, revision, dirty, WithJointPositions(), File.Exists(FilePath(project.Id.ToString("D"))), BuildRuntime(), TeachingViews());
    }
    public WorkspaceSnapshot Get() { lock (gate) return Snapshot(); }
    private void Check(long expected)
    {
        if (expected != revision) throw new DomainException("RevisionConflict", "工程已被其他操作修改，请刷新后重试。", 409);
    }
    private WorkspaceSnapshot Commit(ProjectDocument candidate, bool resetRuntime = false)
    {
        EnsureIdle();
        candidate = Normalize(candidate);
        Validate(candidate);
        project = candidate; revision++; dirty = true;
        ReconcileMotions(resetRuntime);
        return Snapshot();
    }

    private static ProjectDocument Normalize(ProjectDocument doc)
    {
        if (doc is null) throw new DomainException("InvalidProject", "工程记录不能为空。");
        if (doc.SchemaVersion is not (1 or 2 or 3 or 4 or 5)) throw new DomainException("UnsupportedSchema", "不支持此工程版本，当前可读取 v1—v5。");
        var program=doc.Program??ProgramDefinition.Empty();
        if(program.Workspace.ValueKind!=JsonValueKind.Undefined)program=program with{Workspace=JsonSerializer.SerializeToElement(program.Workspace,new JsonSerializerOptions{MaxDepth=2048})};
        return doc with { SchemaVersion = 5, Program=program, TeachPoints = doc.TeachPoints?.Select(p => p is null ? p! : p with { Joints = p.Joints is null ? null! : [.. p.Joints] }).ToArray() ?? [], Devices = doc.Devices?.Select(d => d is null ? d! : d with
        { Robot = RobotModels.IsRobot(d.TypeId) ? d.Robot is null ? RobotModels.Default(d.TypeId) : d.Robot with
            { JointPositions = d.Robot.JointPositions is null ? null! : [.. d.Robot.JointPositions], Geometry = d.Robot.Geometry ?? new(), WorkTransform = d.Robot.WorkTransform ?? new() } : d.Robot }).ToArray()! };
    }

    public WorkspaceSnapshot Add(string typeId, long expected)
    {
        lock (gate)
        {
            Check(expected);
            var type = Catalog.Types.FirstOrDefault(t => t.TypeId == typeId)
                ?? throw new DomainException("UnknownType", "不支持的设备类型。");
            if (project.NextDeviceNumber > 65535) throw new DomainException("DeviceNumberExhausted", "设备编号已用尽，请新建工程。");
            var index = project.Devices.Count(d => d.TypeId == typeId) + 1;
            var transform = typeId switch
            {
                "camera.pinhole" => new Pose(400 + (index - 1) * 180, -400, 700, 145, 0, 215),
                "geometry.box" => new Pose(400, 200, 60),
                "calibration.board" => new Pose(450, 0, 250),
                "calibration.marker" => new Pose(450, 0, 400),
                _ when RobotModels.IsRobot(typeId) => new Pose(project.Devices.Count(d => d.Robot is not null) * 700, 0, 0),
                _ => new Pose((index - 1) * 700, 0, 0)
            };
            var device = new Device(Guid.NewGuid(), typeId, $"{type.Name} {index}",
                project.NextDeviceNumber, null, transform, typeId == "calibration.board" ? new(SizeX: 180, SizeY: 180, SizeZ: 3) :
                    typeId == "calibration.marker" ? new(SizeX: 60, SizeY: 60, SizeZ: 2) : new());
            return Commit(project with { NextDeviceNumber = project.NextDeviceNumber + 1, Devices = [.. project.Devices, device] });
        }
    }

    public WorkspaceSnapshot Update(Guid id, Device replacement, long expected)
    {
        lock (gate)
        {
            Check(expected);
            var old = Find(id);
            if (replacement is null) throw new DomainException("InvalidDevice", "设备记录不能为空。");
            if (replacement.Id != id || replacement.TypeId != old.TypeId || replacement.DeviceNumber != old.DeviceNumber)
                throw new DomainException("ImmutableIdentity", "设备 ID、类型及编号不可修改。");
            if (old.Robot is not null)
            {
                replacement = replacement with { Robot = replacement.Robot ?? old.Robot };
                if (replacement.Robot!.JointPositions is null || !replacement.Robot.JointPositions.SequenceEqual(motions[id].Joints))
                    throw new DomainException("UseMotionCommand", "关节位置只能通过运动指令修改。");
            }
            var updated = replacement with { ConfigurationRevision = old.ConfigurationRevision + 1 };
            return Commit(project with { Devices = project.Devices.Select(d => d.Id == id ? updated : d).ToArray() });
        }
    }

    public WorkspaceSnapshot Duplicate(Guid id, long expected)
    {
        lock (gate)
        {
            Check(expected);
            var source = WithJointPositions().Devices.FirstOrDefault(d => d.Id == id) ?? Find(id);
            if (project.NextDeviceNumber > 65535) throw new DomainException("DeviceNumberExhausted", "设备编号已用尽。");
            var copy = source with { Id = Guid.NewGuid(), Name = source.Name + " 副本",
                DeviceNumber = project.NextDeviceNumber, ConfigurationRevision = 1,
                Transform = source.Transform with { X = source.Transform.X + (source.Robot is null ? 150 : 700) } };
            return Commit(project with { NextDeviceNumber = project.NextDeviceNumber + 1, Devices = [.. project.Devices, copy] });
        }
    }

    public WorkspaceSnapshot Delete(Guid id, long expected, bool cascade)
    {
        lock (gate)
        {
            Check(expected); Find(id);
            var removed = new HashSet<Guid> { id };
            bool added;
            do
            {
                added = false;
                foreach (var child in project.Devices.Where(d => d.ParentId.HasValue && removed.Contains(d.ParentId.Value)))
                    added |= removed.Add(child.Id);
            } while (added);
            if (removed.Count > 1 && !cascade)
                throw new DomainException("HasChildren", $"此设备下还挂载了 {removed.Count - 1} 个对象，请选择连同子对象删除或取消。", 409);
            return Commit(project with { Devices = project.Devices.Where(d => !removed.Contains(d.Id)).ToArray(), TeachPoints = project.TeachPoints!.Where(p => !removed.Contains(p.RobotId)).ToArray() });
        }
    }

    private Device Find(Guid id) => project.Devices.FirstOrDefault(d => d.Id == id)
        ?? throw new DomainException("DeviceNotFound", "设备不存在。", 404);

    public WorkspaceSnapshot New(long expected, string name)
    {
        lock (gate)
        {
            Check(expected);
            Commit(Empty(name), true); sessionId = Guid.NewGuid();
            return Snapshot();
        }
    }

    public WorkspaceSnapshot Import(ProjectDocument document, long expected)
    {
        lock (gate)
        {
            Check(expected);
            Commit(document, true); sessionId = Guid.NewGuid();
            return Snapshot();
        }
    }

    public WorkspaceSnapshot Save(string name, long expected)
    {
        lock (gate)
        {
            Check(expected);
            EnsureIdle();
            var candidate = WithJointPositions() with { Name = name?.Trim() ?? "" };
            Validate(candidate);
            var path = FilePath(candidate.Id.ToString("D"));
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(candidate, Catalog.Json));
            File.Move(temporary, path, true);
            project = candidate; revision++; dirty = false;
            return Snapshot();
        }
    }

    public object[] ListFiles()
    {
        lock (gate)
        {
            return Directory.GetFiles(directory, "*.scene.json").Select(path =>
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path), Catalog.Json);
                    return doc is null ? null : new { id = Path.GetFileName(path).Replace(".scene.json", ""),
                        name = doc.Name, devices = doc.Devices?.Length ?? 0, modified = File.GetLastWriteTimeUtc(path) };
                }
                catch (Exception ex) when (ex is JsonException or IOException) { return null; }
            }).Where(x => x is not null).OrderByDescending(x => x!.modified).Cast<object>().ToArray();
        }
    }

    public WorkspaceSnapshot Open(string fileId, long expected)
    {
        lock (gate)
        {
            Check(expected);
            var path = FilePath(fileId);
            if (!File.Exists(path)) throw new DomainException("ProjectNotFound", "找不到已保存工程。", 404);
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new DomainException("ProjectTooLarge", "工程文件超过 2 MiB。");
            ProjectDocument? loaded;
            try { loaded = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path), Catalog.Json); }
            catch (JsonException) { throw new DomainException("InvalidProject", "工程 JSON 无效或包含不支持的字段。"); }
            if (loaded is null) throw new DomainException("InvalidProject", "工程为空。");
            Commit(loaded, true); sessionId = Guid.NewGuid(); dirty = false;
            return Snapshot();
        }
    }

    private string FilePath(string id)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed)) throw new DomainException("InvalidFileId", "工程文件标识无效。");
        return Path.Combine(directory, parsed.ToString("D") + ".scene.json");
    }

    public static void Validate(ProjectDocument doc)
    {
        if (doc is null) throw new DomainException("InvalidProject", "工程记录不能为空。");
        if (doc.SchemaVersion != 5) throw new DomainException("UnsupportedSchema", "当前写入格式为 schemaVersion=5。");
        if (doc.Id == Guid.Empty || string.IsNullOrWhiteSpace(doc.Name) || doc.Name.Length > 80)
            throw new DomainException("InvalidProject", "工程 ID 无效或名称为空/超过 80 字。");
        if (doc.Devices is null || doc.Devices.Length > 128) throw new DomainException("InvalidDevices", "设备列表无效或超过当前版本的 128 个上限。");
        if (doc.Devices.Any(d => d is null) || doc.Devices.Select(d => d.Id).Distinct().Count() != doc.Devices.Length ||
            doc.Devices.Select(d => d.DeviceNumber).Distinct().Count() != doc.Devices.Length)
            throw new DomainException("DuplicateIdentity", "设备 ID/编号重复或设备记录为空。");
        if (doc.NextDeviceNumber is < 1 or > 65536 || doc.Devices.Any(d => d.DeviceNumber >= doc.NextDeviceNumber))
            throw new DomainException("InvalidNumber", "设备分配计数无效，不能复用已分配编号。");
        var byId = doc.Devices.ToDictionary(d => d.Id);
        foreach (var d in doc.Devices)
        {
            if (d.Id == Guid.Empty || d.DeviceNumber is < 1 or > 65535 || d.ConfigurationRevision is < 1 or > uint.MaxValue ||
                string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 80 || !Catalog.Types.Any(t => t.TypeId == d.TypeId))
                throw new DomainException("InvalidDevice", "设备名称、类型、ID、编号或配置版本无效。");
            if (d.Transform is null || d.Parameters is null) throw new DomainException("InvalidParameters", "缺少位姿或参数。");
            if (RobotModels.IsRobot(d.TypeId))
            {
                if (d.Robot is null) throw new DomainException("InvalidRobot", "缺少机器人模型参数。");
                RobotModels.Validate(d.Robot, d.TypeId);
            }
            else if (d.Robot is not null) throw new DomainException("InvalidRobot", "非机器人设备不能带运动学参数。");
            if (d.AttachmentFrameId is not ("base" or "flange" or "tool"))
                throw new DomainException("InvalidAttachment", "挂载坐标仅支持 base/flange/tool。");
            if (d.AttachmentFrameId != "base" && (!d.ParentId.HasValue || !byId.TryGetValue(d.ParentId.Value, out var mountingRobot) || mountingRobot.Robot is null))
                throw new DomainException("InvalidAttachment", "法兰或工具挂载必须选择机器人父对象。");
            var t = d.Transform;
            if (new[] { t.X, t.Y, t.Z, t.Rx, t.Ry, t.Rz }.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1_000_000))
                throw new DomainException("InvalidTransform", "位姿须为有效有限数值且绝对值不超过 1000000。");
            var p = d.Parameters;
            if (new[] { p.Fx, p.Fy, p.Cx, p.Cy, p.SizeX, p.SizeY, p.SizeZ }.Any(v => !double.IsFinite(v)))
                throw new DomainException("InvalidParameters", "设备参数必须是有限数值。");
            if (p.Width is < 64 or > 4096 || p.Height is < 64 or > 4096 || p.Fx is <= 0 or > 100000 || p.Fy is <= 0 or > 100000 ||
                p.Cx < 0 || p.Cx > p.Width || p.Cy < 0 || p.Cy > p.Height ||
                new[] { p.SizeX, p.SizeY, p.SizeZ }.Any(v => v <= 0 || v > 10000))
                throw new DomainException("InvalidParameters", "分辨率、焦距、主点或几何尺寸超出范围。");
            var seen = new HashSet<Guid> { d.Id };
            var parent = d.ParentId;
            while (parent.HasValue)
            {
                if (!seen.Add(parent.Value)) throw new DomainException("HierarchyCycle", "挂载关系不能包含自身或形成循环。");
                if (!byId.TryGetValue(parent.Value, out var ancestor)) throw new DomainException("ParentNotFound", "挂载父对象不存在。");
                parent = ancestor.ParentId;
            }
        }
        ValidateTeaching(doc);
        ProgramCompiler.ValidateStructure(doc.Program);
    }
}
