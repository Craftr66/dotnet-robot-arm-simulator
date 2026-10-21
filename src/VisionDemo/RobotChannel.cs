using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AxisLab.VisionDemo
{
    public sealed class Modbus : IDisposable
    {
        private readonly TcpClient tcp = new TcpClient { NoDelay = true };
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        private ushort transaction;
        public async Task Connect(string host, int port, CancellationToken ct)
        {
            using (ct.Register(() => tcp.Close())) await tcp.ConnectAsync(host, port);
        }
        public static ushort Word(byte[] a, int i) => (ushort)((a[i] << 8) | a[i + 1]);
        private static void Put(byte[] a, int i, ushort value) { a[i] = (byte)(value >> 8); a[i + 1] = (byte)value; }
        public static uint U32(ushort[] a, int i) => ((uint)a[i] << 16) | a[i + 1];
        public static ushort[] Words(uint value) => new[] { (ushort)(value >> 16), (ushort)value };
        public static void Float(ushort[] a, int i, double value)
        {
            if (!Json.Finite(value) || Math.Abs(value) > float.MaxValue) throw new InvalidOperationException("运动数值超出 Float32 范围。");
            Words(BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0)).CopyTo(a, i);
        }
        private async Task<byte[]> Request(byte[] pdu, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(15000);
                    using (timeout.Token.Register(() => tcp.Close()))
                    {
                        var id = ++transaction; var packet = new byte[pdu.Length + 7]; Put(packet, 0, id); Put(packet, 4, (ushort)(pdu.Length + 1)); packet[6] = 1; pdu.CopyTo(packet, 7);
                        var stream = tcp.GetStream(); await stream.WriteAsync(packet, 0, packet.Length, timeout.Token);
                        async Task<byte[]> Exact(int count)
                        {
                            var bytes = new byte[count]; var offset = 0;
                            while (offset < count) { var read = await stream.ReadAsync(bytes, offset, count - offset, timeout.Token); if (read == 0) throw new EndOfStreamException("Modbus 已断开。"); offset += read; }
                            return bytes;
                        }
                        var head = await Exact(7); var length = Word(head, 4);
                        if (Word(head, 0) != id || Word(head, 2) != 0 || head[6] != 1 || length < 2 || length > 254) throw new InvalidDataException("Modbus MBAP 身份不匹配。");
                        var reply = await Exact(length - 1);
                        if (reply[0] == (pdu[0] | 128)) throw new InvalidOperationException("Modbus 异常 " + reply.ElementAtOrDefault(1));
                        if (reply[0] != pdu[0] || pdu[0] == 16 && !reply.SequenceEqual(pdu.Take(5))) throw new InvalidDataException("Modbus 响应不匹配。");
                        return reply;
                    }
                }
            }
            finally { gate.Release(); }
        }
        public async Task<ushort[]> Read(ushort address, ushort count, CancellationToken ct)
        {
            var pdu = new byte[5]; pdu[0] = 3; Put(pdu, 1, address); Put(pdu, 3, count); var reply = await Request(pdu, ct);
            if (reply.Length != 2 + count * 2 || reply[1] != count * 2) throw new InvalidDataException("Modbus 寄存器长度不匹配。");
            return Enumerable.Range(0, count).Select(i => Word(reply, 2 + i * 2)).ToArray();
        }
        public async Task Write(ushort address, ushort[] values, CancellationToken ct)
        {
            var pdu = new byte[6 + values.Length * 2]; pdu[0] = 16; Put(pdu, 1, address); Put(pdu, 3, (ushort)values.Length); pdu[5] = (byte)(values.Length * 2);
            for (var i = 0; i < values.Length; i++) Put(pdu, 6 + i * 2, values[i]); await Request(pdu, ct);
        }
        public void Dispose() { tcp.Close(); }
    }
    public sealed class CommunicationInfo { public bool listening { get; set; } public int modbusPort { get; set; } public string error { get; set; } }
    public static class RobotChannel
    {
        public static async Task Move(AxisClient axis, DeviceInfo robot, Pose target, double speed, Func<Task> revalidate, CancellationToken ct)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var mb = new Modbus())
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(3)); ct = deadline.Token;
                using var disconnectOnCancel = ct.Register(() => mb.Dispose());
                var state = await axis.Request<CommunicationInfo>("v1/communication", ct);
                if (!state.listening) throw new InvalidOperationException("AxisLab Modbus 未监听：" + state.error);
                await mb.Connect(new Uri(axis.Endpoint).Host, state.modbusPort, ct);
                await mb.Write(200, new[] { checked((ushort)robot.deviceNumber) }, ct);
                var head = await mb.Read(0, 32, ct); var config = await mb.Read(98, 2, ct);
                if (head[0] != 0x4158 || head[1] != 1 || head[11] != robot.deviceNumber || Modbus.U32(config, 0) != robot.configurationRevision)
                    throw new InvalidOperationException("Modbus 设备身份或配置已改变。");
                uint sequence = 0;
                async Task Command(ushort operation)
                {
                    var latest = await mb.Read(0, 32, ct);
                    if (!latest.Skip(4).Take(4).SequenceEqual(head.Skip(4).Take(4))) throw new InvalidOperationException("Modbus 工程会话已变化。");
                    var bank = new ushort[28]; bank[0] = operation;
                    if (operation == 7)
                    {
                        bank[1] = head[13] == 3 ? (ushort)1 : (ushort)0; bank[2] = head[13];
                        var values = new[] { target.x, target.y, target.z, target.rx, target.ry, target.rz };
                        for (var i = 0; i < 6; i++) Modbus.Float(bank, 4 + i * 2, values[i]); Modbus.Float(bank, 16, speed);
                    }
                    Array.Copy(head, 4, bank, 18, 4); config.CopyTo(bank, 22);
                    await mb.Write(202, bank, ct); var submitted = ++sequence; await mb.Write(232, Modbus.Words(submitted), ct);
                    while (true)
                    {
                        var reply = await mb.Read(0, 32, ct);
                        if (Modbus.U32(reply, 18) != submitted) throw new InvalidOperationException("运动命令序号不匹配。");
                        if (reply[15] >= 3)
                        {
                            if (reply[15] != 3) throw new InvalidOperationException($"运动未完成：状态 {reply[15]}，错误 {reply[16]}。"); return;
                        }
                        await Task.Delay(80, ct);
                    }
                }
                await revalidate(); await Command(1);
                using (var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var heartbeat = Task.Run(async () =>
                    {
                        try { uint counter = 0; while (true) { await mb.Write(230, Modbus.Words(++counter), heartbeatStop.Token); await Task.Delay(750, heartbeatStop.Token); } }
                        catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested) { }
                        catch { deadline.Cancel(); throw; }
                    });
                    try { await revalidate(); await Command(7); await Command(2); }
                    finally { heartbeatStop.Cancel(); try { await heartbeat; } catch (Exception) when (ct.IsCancellationRequested) { } }
                }
                // Closing this private socket releases control and cancels an unfinished command.
            }
        }
    }
}
