using AxisLab.Core;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); Console.WriteLine("PASS: " + name); checks++; }
void Reject(Action action, string code) { try { action(); throw new Exception("Expected " + code); } catch (DomainException e) { Check(e.Code == code, code); } }
bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
var time = new ManualClock(); var store = new ProjectStore(Path.Combine("artifacts", "communication-core", Guid.NewGuid().ToString("N")), time);
var s = store.Add("robot.generic3r", store.Get().Revision); var arm3 = s.Project.Devices[0];
s = store.Add("robot.generic6r", s.Revision); var arm6 = s.Project.Devices[1];
s = store.Add("camera.pinhole", s.Revision); var cam = s.Project.Devices[2];
s = store.Update(cam.Id, cam with { Parameters = cam.Parameters with { Width = 64, Height = 64, Cx = 32, Cy = 32 } }, s.Revision); cam = s.Project.Devices[2];
s = store.Add("camera.pinhole", s.Revision); var cam2 = s.Project.Devices[3];
s = store.Update(cam2.Id, cam2 with { Parameters = cam.Parameters }, s.Revision); cam2 = s.Project.Devices[3];
Check(arm3.Robot!.JointPositions.Length == 3 && arm3.Robot.ModelId == RobotModels.ThreeAxis, "3R creates exactly three axes");
var fk = RobotModels.Forward(arm3.Robot, [0, 0, 0]); Check(Near(fk.Tcp.Pose.X, 415) && Near(fk.Tcp.Pose.Z, 545) && fk.JointMatrices.Length == 3, "3R analytic zero TCP and joint matrix count");
var q90 = RobotModels.Forward(arm3.Robot, [90, 0, 0]).Tcp.Pose; Check(Near(q90.X, 0) && Near(q90.Y, 415), "3R J1 analytical rotation");
var elbow = RobotModels.Forward(arm3.Robot, [0, 0, 90]).Tcp.Pose; Check(Near(elbow.X, 0) && Near(elbow.Z, 130), "3R J3 analytical elbow rotation");
Reject(() => RobotModels.Forward(arm3.Robot, [0, 0, 0, 0, 0, 0]), "InvalidJoints");
var capture = new CaptureService(store, time); var bridge = new CommunicationService(store, capture); bridge.SetListener("127.0.0.1", 1502, true); bridge.SetHttpPort(5081);
var a = bridge.Connect(); var b = bridge.Connect();
byte[] Request(CommunicationService.Client c, byte fc, ushort address, ushort count, ushort[]? values = null)
{
    var bytes = new byte[values is null ? 5 : 6 + values.Length * 2]; bytes[0] = fc;
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1), address); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3), count);
    if (values is not null) { bytes[5] = (byte)(values.Length * 2); ModbusProtocol.Words(values).CopyTo(bytes, 6); }
    return bridge.Process(c, bytes);
}
ushort[] Read(CommunicationService.Client c, ushort address = 0, ushort count = 120)
{
    var pdu = Request(c, 3, address, count); if (pdu[0] != 3) throw new Exception("Read exception " + pdu[1]);
    return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(2 + i * 2))).ToArray();
}
void Write(CommunicationService.Client c, ushort address, ushort[] values) { var pdu = Request(c, 16, address, (ushort)values.Length, values); if (pdu[0] != 16) throw new Exception("Write exception " + pdu[1]); }
void Select(CommunicationService.Client c, int number) => Write(c, 200, [(ushort)number]);
ushort[] Bank(CommunicationService.Client c, ushort opcode, double[]? joints = null)
{
    var h = Read(c); var bank = new ushort[28]; bank[0] = opcode; bank[2] = (ushort)(joints?.Length ?? 0);
    if (joints is not null) for (var i = 0; i < joints.Length; i++) ModbusProtocol.PutFloat(bank, 4 + i * 2, joints[i]);
    ModbusProtocol.PutFloat(bank, 16, 25); Array.Copy(h, 4, bank, 18, 4); Array.Copy(h, 98, bank, 22, 2); return bank;
}
ushort[] Submit(CommunicationService.Client c, uint sequence, ushort[] bank)
{ Write(c, 202, bank); var pair = new ushort[2]; ModbusProtocol.Put32(pair, 0, sequence); Write(c, 232, pair); return Read(c); }
Check(Read(a)[0] == 0x4158 && Read(a)[30] == 1502 && Read(a)[31] == 5081, "public header discovers protocol and actual ports");
Check(Request(a, 1, 0, 1).SequenceEqual(new byte[] { 129, 1 }), "unsupported function uses exception 01");
Check(Request(a, 3, 0, 126).SequenceEqual(new byte[] { 131, 3 }), "FC03 quantity limit uses exception 03");
Check(Request(a, 3, 233, 2).SequenceEqual(new byte[] { 131, 2 }), "register out of range uses exception 02");
Check(Request(a, 6, 0, 1).SequenceEqual(new byte[] { 134, 2 }), "read-only header rejects writes");
Check(Request(a, 6, 232, 1).SequenceEqual(new byte[] { 134, 2 }), "submit requires atomic FC16 pair");
Check(Request(a, 16, 229, 2, [0, 0]).SequenceEqual(new byte[] { 144, 2 }), "write cannot straddle staging and heartbeat");
Select(a, arm3.DeviceNumber); Select(b, arm3.DeviceNumber);
var h3 = Read(a); Check(h3[13] == 3 && Near(ModbusProtocol.Float(h3, 44), 415) && h3.Skip(38).Take(6).All(v => v == 0), "3R register state pads unused axes with zero");
Check(Read(a, 128, 16)[6] == 6 && Read(a, 128, 16)[2] == 3, "discovery distinguishes three and six axes");
Check(Submit(a, 1, Bank(a, 1))[15] == 3, "first client acquires control");
Check(Submit(b, 1, Bank(b, 1))[16] == 11, "second client cannot acquire occupied robot");
Reject(() => store.MoveJoints(arm3.Id, [10, 0, 0], 25, store.Get().Revision), "ControlBusy");
Reject(() => store.Save("external", store.Get().Revision), "ExternalControlActive");
Write(a, 230, [0, 1]); var moveBank = Bank(a, 3, [90, 0, 0]); var moved = Submit(a, 2, moveBank);
Check(moved[15] == 2 && moved[14] == 1, "owner submits running three-axis motion");
store.AdvanceMotions(.5); var before = store.GetRuntime().Robots[0];
var duplicated = Submit(a, 2, moveBank); Check(duplicated[15] == 2 && store.GetRuntime().Robots[0].CommandId == before.CommandId && Near(store.GetRuntime().Robots[0].Joints[0], before.Joints[0]), "retry same sequence preserves command and progress");
var conflict = moveBank.ToArray(); ModbusProtocol.PutFloat(conflict, 4, -90);
Check(Submit(a, 2, conflict)[16] == 5, "same sequence with different payload is rejected");
var otherBank = Bank(b, 3, [-90, 0, 0]); Write(b, 202, otherBank);
Check(Read(a, 202, 28).SequenceEqual(conflict), "client staging windows are isolated");
var staleConfig = Bank(a, 3, [0, 0, 0]); ModbusProtocol.Put32(staleConfig, 22, 999);
Check(Submit(a, 3, staleConfig)[16] == 3, "configuration mismatch rejects old command");
time.Advance(5.1); store.AdvanceMotions(0); bridge.Tick();
Check(store.GetRuntime().Robots[0].Status == "Cancelled" && !store.GetRuntime().Robots[0].ControlOwnerId.HasValue, "missing heartbeat stops and revokes control");
Check(bridge.Command(a.Id, 2).State == 5, "expired owner command records cancelled terminal state");
Check(Submit(b, 2, Bank(b, 1))[15] == 3, "another client can acquire after expiry");
Write(b, 230, [0, 7]); time.Advance(4); Write(b, 230, [0, 7]); time.Advance(1.1);
Check(Read(b)[14] == 0, "unchanged heartbeat counter does not renew lease");
Submit(b, 3, Bank(b, 1)); Submit(b, 4, Bank(b, 3, [-90, 0, 0])); bridge.Disconnect(b);
Check(store.GetRuntime().Robots[0].Status == "Cancelled", "TCP disconnect stops the owned robot");
Select(a, arm6.DeviceNumber); Submit(a, 4, Bank(a, 1));
Check(Submit(a, 5, Bank(a, 3, [1, 2, 3, 4, 5, 6]))[15] == 2, "six-axis commands retain six independent targets");
Submit(a, 6, Bank(a, 4)); Check(Read(a)[14] == 0 && store.GetRuntime().Robots[1].Status == "Cancelled", "stop revokes control and cancels motion");
var renderer = capture.Register(); Select(a, cam.DeviceNumber); var c = bridge.Connect(); Select(c, cam2.DeviceNumber);
var captureBank = Bank(a, 5); var captureState = Submit(a, 7, captureBank);
var captureId = bridge.Command(a.Id, 7).CaptureId;
Submit(a, 7, captureBank); Check(bridge.Command(a.Id, 7).CaptureId == captureId, "capture retry does not trigger a second exposure");
Check(Request(a, 6, 200, (ushort)cam2.DeviceNumber)[1] == 6, "selection is locked while a capture is active");
Submit(c, 1, Bank(c, 5)); var work = capture.TakeWork(renderer.RendererId, renderer.Token)!;
byte[] Png()
{
    using var output = new MemoryStream(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
    void Chunk(string name, byte[] bytes)
    {
        var tag = Encoding.ASCII.GetBytes(name); var n = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(n, bytes.Length); output.Write(n); output.Write(tag); output.Write(bytes);
        uint crc = uint.MaxValue; foreach (var x in tag.Concat(bytes)) { crc ^= x; for (var j = 0; j < 8; j++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1; }
        BinaryPrimitives.WriteUInt32BigEndian(n, crc ^ uint.MaxValue); output.Write(n);
    }
    var header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, 64); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 64); header[8] = 8; header[9] = 6; Chunk("IHDR", header);
    using var zipped = new MemoryStream(); using (var z = new ZLibStream(zipped, CompressionLevel.Fastest, true)) z.Write(new byte[64 * (1 + 64 * 4)]);
    Chunk("IDAT", zipped.ToArray()); Chunk("IEND", []); return output.ToArray();
}
capture.Complete(renderer.RendererId, renderer.Token, work.CaptureId, Png()); bridge.Tick(); var frame = Read(a);
var token = ModbusProtocol.U32(frame, 24); var session = ModbusProtocol.U64(frame, 4).ToString("X16");
Check(frame[15] == 3 && token != 0 && bridge.Frame(session, token).CameraId == cam.Id, "Modbus result token maps to matching HTTP frame metadata");
Check(Read(c)[15] == 2 && ModbusProtocol.U32(Read(c), 24) == 0, "second camera does not inherit first camera result");
Submit(c, 2, Bank(c, 6)); bridge.Tick(); Check(bridge.Command(c.Id, 1).State == 5, "explicit cancel only cancels own capture");
for (uint seq = 8; seq < 140; seq++) Submit(a, seq, Bank(a, 999));
Check(Submit(a, 7, captureBank)[16] == 6, "evicted sequence cannot execute again");
var saved = store.Save("三轴通信保存", store.Get().Revision); var oldSession = store.SessionId;
store.Open(saved.Project.Id.ToString(), saved.Revision);
Check(store.Get().Project.Devices[0].Id == arm3.Id && store.GetRuntime().Robots[0].Joints.Length == 3, "3R save and reopen preserves identity and axes");
Check(Read(a)[29] == 0, "project reopen invalidates the selected device binding");
Check(Submit(a, 140, Bank(a, 5))[16] == 2, "fresh token cannot rescue a stale device binding");
Reject(() => bridge.Frame(session, token), "FrameGone");
Select(a, cam.DeviceNumber); var oldBank = Bank(a, 5); ModbusProtocol.Put64(oldBank, 18, ModbusProtocol.SessionToken(oldSession));
Check(Submit(a, 141, oldBank)[16] == 2, "reselected device rejects old project token");
time.Advance(5); Check(Submit(a, 142, Bank(a, 5))[16] == 17, "missing renderer surfaces a business error instead of fake capture success");
bridge.Disconnect(a); bridge.Disconnect(c);
Check(bridge.Status().ActiveClients == 0, "all connection control state is released");
Console.WriteLine($"{checks} communication/3R core checks passed.");

sealed class ManualClock : TimeProvider
{
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
    public void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
}

