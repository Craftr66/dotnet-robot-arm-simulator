using System.Buffers.Binary;
using System.Globalization;

namespace AxisLab.Core;

public record CommunicationStatus(string BindAddress, int ModbusPort, bool Listening, string? Error,
    int HttpPort, int UnitId, int ProtocolVersion, int ActiveClients, string SessionToken);
public record ExternalCommand(uint Sequence, int Opcode, Guid? DeviceId, int State, ushort Error,
    string? ErrorCode, string? Message, Guid? CommandId, Guid? CaptureId, uint FrameToken);

public sealed class CommunicationService(ProjectStore projects, CaptureService captures)
{
    private readonly object gate = new();
    public sealed class Client
    {
        public uint Id { get; internal set; }
        internal Guid Owner = Guid.NewGuid();
        internal bool Connected = true;
        internal Guid? DeviceId;
        internal Guid SelectedSession;
        internal ushort Number, Page;
        internal ushort[] Bank = new ushort[28];
        internal uint Heartbeat, Highest, Accepted, Terminal;
        internal Job? Latest;
        internal Dictionary<uint, Job> Jobs = [];
    }
    internal sealed class Job(uint sequence, ushort[] payload, Guid? deviceId, Guid session)
    {
        internal uint Sequence = sequence;
        internal ushort[] Payload = payload;
        internal Guid? DeviceId = deviceId;
        internal Guid Session = session;
        internal int State = 1;
        internal string? ErrorCode, Message;
        internal Guid? CommandId, CaptureId;
        internal uint FrameToken;
        internal bool Active => State is 1 or 2;
        internal ExternalCommand Info => new(Sequence, Payload[0], DeviceId, State, ModbusProtocol.Error(ErrorCode), ErrorCode, Message, CommandId, CaptureId, FrameToken);
    }
    private readonly Dictionary<uint, Client> clients = [];
    private readonly Dictionary<uint, (Guid Session, Guid Camera, Guid Frame)> frames = [];
    private uint nextClient, nextFrame, snapshotSequence;
    private string bindAddress = "127.0.0.1";
    private int port = 1502, httpPort;
    private bool listening;
    private string? listenerError;
    public void SetListener(string address, int actualPort, bool running, string? error = null)
    { lock (gate) { bindAddress = address; port = actualPort; listening = running; listenerError = error; } }
    public void SetHttpPort(int value) { lock (gate) httpPort = value; }
    public CommunicationStatus Status()
    {
        lock (gate) return new(bindAddress, port, listening, listenerError, httpPort, 1, 1,
            clients.Values.Count(c => c.Connected), ModbusProtocol.SessionToken(projects.SessionId).ToString("X16"));
    }
    public Client Connect()
    {
        lock (gate)
        {
            if (clients.Values.Count(c => c.Connected) >= 16) throw new DomainException("TooManyClients", "最多 16 个 Modbus 连接。", 503);
            while (clients.Count >= 64)
            {
                var old = clients.OrderBy(p => p.Key).FirstOrDefault(p => !p.Value.Connected);
                if (old.Value is null) break; clients.Remove(old.Key);
            }
            if (nextClient == uint.MaxValue) throw new DomainException("ClientIdExhausted", "连接编号已用尽，请重启服务。", 503);
            var client = new Client { Id = ++nextClient }; clients.Add(client.Id, client); return client;
        }
    }
    public void Disconnect(Client client)
    {
        lock (gate)
        {
            client.Connected = false; projects.DisconnectOwner(client.Owner);
            foreach (var job in client.Jobs.Values.Where(j => j.Active && j.CaptureId.HasValue))
                try { captures.Cancel(job.CaptureId!.Value); } catch (DomainException) { }
            Refresh();
        }
    }
    private static void Fail(Job job, string code, string? message = null, int state = 4)
    { job.State = state; job.ErrorCode = code; job.Message = message ?? code; }
    public void Tick() { lock (gate) Refresh(); }
    private WorkspaceSnapshot Refresh()
    {
        var snapshot = projects.Get();
        foreach (var c in clients.Values)
            foreach (var j in c.Jobs.Values.Where(j => j.Active))
            {
                if (j.Session != snapshot.SessionId) Fail(j, "SessionChanged");
                else if (j.CommandId.HasValue)
                {
                    var robot = snapshot.Runtime.Robots.FirstOrDefault(r => r.DeviceId == j.DeviceId);
                    if (robot is null) Fail(j, "DeviceNotFound");
                    else if (robot.CommandId != j.CommandId || robot.Status == "Cancelled") j.State = 5;
                    else if (robot.Status == "Completed") j.State = 3;
                    else if (robot.Status == "Failed") Fail(j, robot.MotionErrorCode ?? "InternalError", robot.MotionMessage);
                }
                else if (j.CaptureId.HasValue)
                {
                    try
                    {
                        var capture = captures.Get(j.CaptureId.Value);
                        if (capture.Status == "Failed") Fail(j, capture.ErrorCode ?? "RenderFailed", capture.Message, capture.ErrorCode == "CaptureCancelled" ? 5 : 4);
                        else if (capture.Status == "Completed")
                        {
                            if (nextFrame == uint.MaxValue) { Fail(j, "InternalError", "帧令牌已用尽，请重启。"); continue; }
                            j.FrameToken = ++nextFrame; frames[j.FrameToken] = (j.Session, j.DeviceId!.Value, capture.FrameId!.Value);
                            while (frames.Count > 512) frames.Remove(frames.Keys.Min());
                            j.State = 3;
                        }
                    }
                    catch (DomainException e) { Fail(j, e.Code, e.Message); }
                }
                if (!j.Active) c.Terminal = Math.Max(c.Terminal, j.Sequence);
            }
        return snapshot;
    }
    public ExternalCommand Command(uint clientId, uint sequence)
    {
        lock (gate)
        {
            Refresh();
            return clients.GetValueOrDefault(clientId)?.Jobs.GetValueOrDefault(sequence)?.Info ??
                throw new DomainException("CommandNotFound", "命令历史已清理或不存在。", 404);
        }
    }
    public FrameMetadata Frame(string sessionToken, uint frameToken)
    {
        lock (gate)
        {
            Refresh();
            if (!ulong.TryParse(sessionToken, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var token) || token != ModbusProtocol.SessionToken(projects.SessionId))
                throw new DomainException("FrameGone", "工程会话已变化。", 410);
            if (!frames.TryGetValue(frameToken, out var f)) throw new DomainException("FrameNotFound", "帧令牌不存在或已清理。", 404);
            if (ModbusProtocol.SessionToken(f.Session) != token) throw new DomainException("FrameGone", "帧会话已失效。", 410);
            return captures.Metadata(f.Camera, f.Frame);
        }
    }
    public byte[] Process(Client client, byte[] pdu)
    {
        lock (gate)
        {
            var function = pdu[0];
            try
            {
                if (!client.Connected) throw new ModbusException(4);
                if (function is not (3 or 6 or 16)) throw new ModbusException(1);
                if (pdu.Length < 5) throw new ModbusException(3);
                var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                var value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                if (function == 3)
                {
                    if (pdu.Length != 5 || value is < 1 or > 125) throw new ModbusException(3);
                    if (address + value > ModbusProtocol.RegisterCount) throw new ModbusException(2);
                    var words = Read(client); var data = ModbusProtocol.Words(words.AsSpan(address, value).ToArray());
                    return [3, (byte)data.Length, .. data];
                }
                ushort[] values;
                if (function == 6)
                {
                    if (pdu.Length != 5) throw new ModbusException(3); values = [value];
                }
                else
                {
                    if (value is < 1 or > 123 || pdu.Length != 6 + value * 2 || pdu[5] != value * 2) throw new ModbusException(3);
                    values = Enumerable.Range(0, value).Select(i => BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + i * 2))).ToArray();
                }
                Write(client, address, values, function == 16);
                return function == 6 ? pdu : pdu[..5];
            }
            catch (ModbusException e) { return [(byte)(function | 128), e.Code]; }
        }
    }
    private ushort[] Read(Client c)
    {
        var s = Refresh(); var a = new ushort[ModbusProtocol.RegisterCount];
        var d = c.SelectedSession == s.SessionId ? s.Project.Devices.FirstOrDefault(d => d.Id == c.DeviceId) : null;
        var robot = s.Runtime.Robots.FirstOrDefault(r => r.DeviceId == d?.Id);
        a[0] = 0x4158; a[1] = 1; a[2] = 6; a[3] = listening ? (ushort)1 : (ushort)0;
        ModbusProtocol.Put64(a, 4, ModbusProtocol.SessionToken(s.SessionId)); ModbusProtocol.Put32(a, 8, ++snapshotSequence);
        a[10] = (ushort)s.Project.Devices.Length; a[11] = c.Number; a[12] = d is null ? (ushort)0 : ModbusProtocol.TypeCode(d);
        a[13] = (ushort)(robot?.Joints.Length ?? 0); a[14] = robot?.ControlOwnerId is null ? (ushort)0 : robot.ControlOwnerId == c.Owner ? (ushort)1 : (ushort)2;
        if(robot is not null && s.Runtime.ProgramRunId.HasValue)a[14]=2;
        a[15] = (ushort)(c.Latest?.State ?? 0); a[16] = ModbusProtocol.Error(c.Latest?.ErrorCode); a[17] = d is null ? (ushort)0 : ModbusProtocol.Capabilities(d);
        ModbusProtocol.Put32(a, 18, c.Latest?.Sequence ?? 0); ModbusProtocol.Put32(a, 20, c.Accepted); ModbusProtocol.Put32(a, 22, c.Terminal);
        ModbusProtocol.Put32(a, 24, c.Latest?.FrameToken ?? 0); ModbusProtocol.Put32(a, 26, c.Id); a[28] = 5; a[29] = d is null ? (ushort)0 : (ushort)1;
        a[30] = (ushort)port; a[31] = (ushort)httpPort;
        void PoseAt(int offset, Pose p) { var v = new[] { p.X, p.Y, p.Z, p.Rx, p.Ry, p.Rz }; for (var i = 0; i < v.Length; i++) ModbusProtocol.PutFloat(a, offset + i * 2, v[i]); }
        if (robot is not null)
        {
            for (var i = 0; i < robot.Joints.Length; i++) ModbusProtocol.PutFloat(a, 32 + i * 2, robot.Joints[i]);
            PoseAt(44, robot.TcpBase.Pose); PoseAt(56, robot.FlangeBase.Pose); PoseAt(68, robot.TcpWorld.Pose);
            ModbusProtocol.Put64(a, 92, (ulong)robot.PoseId); a[96] = robot.Status switch { "Running" => 1, "Completed" => 2, "Cancelled" => 3, "Failed" => 4, _ => (ushort)0 };
            a[97] = (ushort)(robot.Progress * 1000); a[118] = (ushort)robot.ControlRemainingMilliseconds;
            a[119] = (ushort)robot.StepIndex; a[120] = (ushort)robot.StepCount; a[121] = robot.MotionKind == "MoveL" ? (ushort)2 : robot.MotionKind == "MoveJ" ? (ushort)1 : (ushort)0;
            a[122] = ModbusProtocol.Error(robot.MotionErrorCode);
        }
        a[123] = 1;
        if (d is not null)
        {
            PoseAt(80, s.Runtime.Devices.Single(f => f.DeviceId == d.Id).World.Pose);
            ModbusProtocol.Put32(a, 98, (uint)d.ConfigurationRevision);
            var hex = d.Id.ToString("N"); for (var i = 0; i < 8; i++) a[100 + i] = ushort.Parse(hex.AsSpan(i * 4, 4), NumberStyles.HexNumber);
            if (d.TypeId == "camera.pinhole")
            {
                var p = d.Parameters; ModbusProtocol.PutFloat(a, 108, p.Fx); ModbusProtocol.PutFloat(a, 110, p.Fy);
                ModbusProtocol.PutFloat(a, 112, p.Cx); ModbusProtocol.PutFloat(a, 114, p.Cy); a[116] = (ushort)p.Width; a[117] = (ushort)p.Height;
            }
        }
        var page = s.Project.Devices.OrderBy(d => d.DeviceNumber).Skip(c.Page * 16).Take(16).ToArray();
        for (var i = 0; i < page.Length; i++)
        { var item = page[i]; var offset = 128 + i * 4; a[offset] = (ushort)item.DeviceNumber; a[offset + 1] = ModbusProtocol.TypeCode(item); a[offset + 2] = (ushort)(item.Robot?.JointPositions.Length ?? 0); a[offset + 3] = ModbusProtocol.Capabilities(item); }
        a[200] = c.Number; a[201] = c.Page; c.Bank.CopyTo(a, 202); ModbusProtocol.Put32(a, 230, c.Heartbeat);
        return a;
    }
    private void Write(Client c, int address, ushort[] values, bool multiple)
    {
        var s = Refresh();
        if (address is 200 or 201 && values.Length == 1)
        {
            if (address == 201) { c.Page = values[0]; return; }
            if (c.Jobs.Values.Any(j => j.Active) || s.Runtime.Robots.Any(r => r.ControlOwnerId == c.Owner)) throw new ModbusException(6);
            var d = s.Project.Devices.FirstOrDefault(d => d.DeviceNumber == values[0]);
            if (values[0] != 0 && d is null) throw new ModbusException(2);
            c.DeviceId = d?.Id; c.Number = values[0]; c.SelectedSession = s.SessionId; c.Bank = new ushort[28]; c.Latest = null; return;
        }
        if (address >= 202 && address + values.Length <= 230) { values.CopyTo(c.Bank, address - 202); return; }
        if (multiple && address == 230 && values.Length == 2)
        { c.Heartbeat = ModbusProtocol.U32(values, 0); projects.HeartbeatOwner(c.Owner, c.Heartbeat); return; }
        if (multiple && address == 232 && values.Length == 2) { SubmitCommand(c, ModbusProtocol.U32(values, 0), s); return; }
        throw new ModbusException(2);
    }
    private void SubmitCommand(Client c, uint sequence, WorkspaceSnapshot s)
    {
        var payload = c.Bank.ToArray();
        var job = new Job(sequence, payload, c.DeviceId, c.SelectedSession); c.Latest = job;
        if (c.Jobs.TryGetValue(sequence, out var existing))
        {
            if (existing.DeviceId == c.DeviceId && existing.Session == c.SelectedSession && existing.Payload.SequenceEqual(payload)) c.Latest = existing;
            else Fail(job, "SequenceConflict", state: 6);
            return;
        }
        if (sequence == 0 || sequence <= c.Highest) { Fail(job, sequence == 0 ? "InvalidSequence" : "SequenceTooOld", state: 6); return; }
        c.Highest = sequence;
        while (c.Jobs.Count >= 128)
        {
            var old = c.Jobs.OrderBy(p => p.Key).FirstOrDefault(p => !p.Value.Active);
            if (old.Value is null) { Fail(job, "RobotBusy", state: 6); return; } c.Jobs.Remove(old.Key);
        }
        c.Jobs[sequence] = job;
        try
        {
            if (!c.DeviceId.HasValue) throw new DomainException("NoDevice", "请先选择设备。");
            if (c.SelectedSession != s.SessionId || ModbusProtocol.U64(payload, 18) != ModbusProtocol.SessionToken(s.SessionId))
                throw new DomainException("SessionChanged", "会话已变化，请重新选择设备并读取状态。");
            var cartesian = payload[0] is 7 or 8;
            if ((cartesian ? payload[1] > 5 : payload[1] != 0) || payload[3] != 0 || payload.Skip(payload[0] == 9 ? 25 : 24).Any(v => v != 0)) throw new DomainException("InvalidParameters", "标志或保留字段无效。");
            var config = ModbusProtocol.U32(payload, 22);
            if (payload[0] == 5)
            { job.CaptureId = captures.Trigger(c.DeviceId.Value, c.SelectedSession, config).CaptureId; job.State = 2; }
            else if (payload[0] == 6)
            {
                projects.FreezeCamera(c.DeviceId.Value, c.SelectedSession, config);
                foreach (var active in c.Jobs.Values.Where(j => j != job && j.DeviceId == c.DeviceId && j.Active && j.CaptureId.HasValue))
                    captures.Cancel(active.CaptureId!.Value);
                job.State = 3;
            }
            else projects.WithExternalDevice(c.SelectedSession, c.DeviceId.Value, config, device =>
            {
                switch (payload[0])
                {
                    case 1: projects.ClaimRobot(device.Id, c.Owner); job.State = 3; break;
                    case 2: projects.ReleaseRobot(device.Id, c.Owner); job.State = 3; break;
                    case 3:
                        if (device.Robot is null) throw new DomainException("NotRobot", "此设备不是机械臂。");
                        var count = device.Robot.JointPositions.Length;
                        if (payload[2] != count || Enumerable.Range(count, 6 - count).Any(i => ModbusProtocol.Float(payload, 4 + i * 2) != 0))
                            throw new DomainException("InvalidJoints", "目标轴数不匹配，未使用轴应为零。");
                        var q = Enumerable.Range(0, count).Select(i => ModbusProtocol.Float(payload, 4 + i * 2)).ToArray();
                        var motion = projects.MoveJoints(device.Id, q, ModbusProtocol.Float(payload, 16), projects.Get().Revision, c.Owner);
                        job.CommandId = motion.Runtime.Robots.Single(r => r.DeviceId == device.Id).CommandId; job.State = 2; break;
                    case 4: projects.StopRobot(device.Id); job.State = 3; break;
                    case 7: case 8:
                        if (device.Robot is null) throw new DomainException("NotRobot", "此设备不是机械臂。");
                        if (payload[2] != device.Robot.JointPositions.Length) throw new DomainException("InvalidJoints", "机器人轴数不匹配。");
                        var v = Enumerable.Range(0, 6).Select(i => ModbusProtocol.Float(payload, 4+i*2)).ToArray();
                        var frame = (payload[1] >> 1) switch { 1 => "world", 2 => "work", _ => "base" };
                        var cart = projects.MoveCartesian(device.Id, new(new(v[0],v[1],v[2],v[3],v[4],v[5]), frame, payload[0] == 7 ? "MoveJ" : "MoveL", ModbusProtocol.Float(payload,16), (payload[1]&1) != 0), projects.Get().Revision, c.Owner);
                        job.CommandId = cart.Runtime.Robots.Single(r => r.DeviceId == device.Id).CommandId; job.State = 2; break;
                    case 9:
                        var teach = projects.PlayTeachNumber(device.Id, payload[24], ModbusProtocol.Float(payload,16), c.Owner);
                        job.CommandId = teach.Runtime.Robots.Single(r => r.DeviceId == device.Id).CommandId; job.State = 2; break;
                    default: throw new DomainException("UnsupportedCommand", "不支持此操作码。");
                }
                return true;
            });
            c.Accepted = sequence; if (!job.Active) c.Terminal = sequence;
        }
        catch (DomainException e) { Fail(job, e.Code, e.Message, 6); c.Terminal = sequence; }
    }
}
