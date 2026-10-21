using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;

namespace AxisLab.WpfDemo;

/// <summary>最小 Modbus TCP 客户端：FC03 / FC06 / FC16，单连接串行请求。</summary>
public sealed class ModbusTcpClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ushort _transaction;
    private bool _connected;

    public string Host { get; private set; } = "";
    public int Port { get; private set; }
    public byte UnitId { get; set; } = 1;
    public bool IsConnected => _connected;

    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Host = host;
        Port = port;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        _connected = true;
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort address, ushort count, CancellationToken ct = default)
    {
        if (count is 0 or > 125) throw new ArgumentOutOfRangeException(nameof(count), "FC03 单次读取 1—125 个寄存器。");
        var pdu = new byte[5];
        pdu[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        var response = await RequestAsync(pdu, ct).ConfigureAwait(false);
        if (response.Length != 2 + count * 2 || response[1] != count * 2)
            throw new InvalidDataException("Modbus FC03 响应长度不匹配。");
        var values = new ushort[count];
        for (var i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2));
        return values;
    }

    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        var pdu = new byte[5];
        pdu[0] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        var response = await RequestAsync(pdu, ct).ConfigureAwait(false);
        if (response.Length != 5 || !response.AsSpan(0, 5).SequenceEqual(pdu))
            throw new InvalidDataException("Modbus FC06 写响应不匹配。");
    }

    public async Task WriteMultipleRegistersAsync(ushort address, ushort[] values, CancellationToken ct = default)
    {
        if (values.Length is 0 or > 123) throw new ArgumentOutOfRangeException(nameof(values), "FC16 单次写入 1—123 个寄存器。");
        var pdu = new byte[6 + values.Length * 2];
        pdu[0] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length);
        pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + i * 2), values[i]);
        var response = await RequestAsync(pdu, ct).ConfigureAwait(false);
        if (response.Length != 5 || response[0] != 16 ||
            BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(1)) != address ||
            BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3)) != values.Length)
            throw new InvalidDataException("Modbus FC16 写响应不匹配。");
    }

    private async Task<byte[]> RequestAsync(byte[] pdu, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!_connected) throw new IOException("Modbus TCP 尚未连接。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var ct = timeout.Token;
            var id = ++_transaction;
            var packet = new byte[pdu.Length + 7];
            BinaryPrimitives.WriteUInt16BigEndian(packet, id);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(pdu.Length + 1));
            packet[6] = UnitId;
            pdu.CopyTo(packet, 7);

            var stream = _tcp.GetStream();
            await stream.WriteAsync(packet, ct).ConfigureAwait(false);
            var header = new byte[7];
            await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt16BigEndian(header) != id ||
                BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 ||
                header[6] != UnitId || length is < 2 or > 254)
                throw new InvalidDataException("Modbus MBAP 响应不匹配。");
            var result = new byte[length - 1];
            await stream.ReadExactlyAsync(result, ct).ConfigureAwait(false);
            if (result[0] == (byte)(pdu[0] | 0x80))
            {
                var code = result.Length > 1 ? result[1] : (byte)0;
                throw new InvalidOperationException($"Modbus 标准异常 0x{code:X2}：{StandardExceptionText(code)}");
            }
            if (result[0] != pdu[0]) throw new InvalidDataException("Modbus 功能码不匹配。");
            return result;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException || cancellation.IsCancellationRequested)
                _connected = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string StandardExceptionText(byte code) => code switch
    {
        1 => "非法功能",
        2 => "非法数据地址（越界、只读或写入范围不合法）",
        3 => "非法数据值（长度/数量错误）",
        6 => "设备忙（有控制权或活动任务，不能切换设备）",
        11 => "Unit ID 不匹配",
        _ => "未知标准异常"
    };

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _tcp.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class ModbusCodec
{
    public static ushort U16(ushort[] a, int i) => a[i];
    public static uint U32(ushort[] a, int i) => ((uint)a[i] << 16) | a[i + 1];
    public static ulong U64(ushort[] a, int i) => ((ulong)U32(a, i) << 32) | U32(a, i + 2);
    public static float Float(ushort[] a, int i) => BitConverter.Int32BitsToSingle(unchecked((int)U32(a, i)));
    public static ushort[] UInt32Words(uint v) => [(ushort)(v >> 16), (ushort)v];

    public static void PutFloat(ushort[] a, int i, float value)
    {
        var words = UInt32Words(unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        words.CopyTo(a, i);
    }

    public static Guid UuidFromWords(ushort[] a, int start)
    {
        var hex = string.Concat(Enumerable.Range(0, 8).Select(i => a[start + i].ToString("X4")));
        var formatted = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
        return Guid.Parse(formatted);
    }

    public static double[] SixFloats(ushort[] a, int start) =>
        Enumerable.Range(0, 6).Select(i => (double)Float(a, start + i * 2)).ToArray();

    public static DeviceState ParseState(ushort[] r)
    {
        if (r.Length < 120) throw new ArgumentException("状态寄存器至少需要 120 个。", nameof(r));
        return new DeviceState
        {
            Magic = r[0],
            ProtocolVersion = r[1],
            Application = r[2],
            Listening = r[3] == 1,
            SessionToken = U64(r, 4),
            SnapshotSequence = U32(r, 8),
            DeviceCount = r[10],
            SelectedDeviceNumber = r[11],
            SelectedType = r[12],
            Axes = r[13],
            ControlOwner = r[14],
            CommandState = r[15],
            ErrorCode = r[16],
            Capabilities = r[17],
            SubmittedSequence = U32(r, 18),
            AcceptedSequence = U32(r, 20),
            TerminalSequence = U32(r, 22),
            FrameToken = U32(r, 24),
            ClientId = U32(r, 26),
            HeartbeatTimeoutSeconds = r[28],
            IdentityValid = r[29] == 1,
            ModbusPort = r[30],
            HttpPort = r[31],
            Joints = SixFloats(r, 32),
            TcpBase = SixFloats(r, 44),
            FlangeBase = SixFloats(r, 56),
            TcpWorld = SixFloats(r, 68),
            DeviceWorldPose = SixFloats(r, 80),
            PoseId = U64(r, 92),
            MotionStatus = r[96],
            ProgressPermille = r[97],
            ConfigurationRevision = U32(r, 98),
            DeviceUuid = UuidFromWords(r, 100),
            Fx = Float(r, 108),
            Fy = Float(r, 110),
            Cx = Float(r, 112),
            Cy = Float(r, 114),
            CameraWidth = r[116],
            CameraHeight = r[117],
            HeartbeatRemainingMs = r[118]
        };
    }
}


