using System.Buffers.Binary;
using System.Net.Sockets;

public sealed class ModbusTcpClient : IDisposable
{
    private readonly TcpClient tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim gate = new(1);
    private ushort transaction;
    public async Task Connect(string host, int port, CancellationToken ct) => await tcp.ConnectAsync(host, port, ct);
    private async Task<byte[]> Request(byte[] pdu, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(3000);
            var ct = timeout.Token; var id = ++transaction;
            var packet = new byte[pdu.Length + 7]; BinaryPrimitives.WriteUInt16BigEndian(packet, id);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(pdu.Length + 1)); packet[6] = 1; pdu.CopyTo(packet, 7);
            var stream = tcp.GetStream(); await stream.WriteAsync(packet, ct);
            var header = new byte[7]; await stream.ReadExactlyAsync(header, ct);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt16BigEndian(header) != id || header[2] != 0 || header[3] != 0 || header[6] != 1 || length is < 2 or > 254)
                throw new InvalidDataException("Modbus MBAP 响应不匹配。");
            var result = new byte[length - 1]; await stream.ReadExactlyAsync(result, ct);
            if (result[0] == (pdu[0] | 128)) throw new InvalidOperationException($"Modbus 标准异常：{result.ElementAtOrDefault(1)}");
            if (result[0] != pdu[0]) throw new InvalidDataException("Modbus 功能码不匹配。");
            if (pdu[0] != 3 && !result.SequenceEqual(pdu[..5])) throw new InvalidDataException("Modbus 写响应不匹配。");
            return result;
        }
        finally { gate.Release(); }
    }
    public async Task<ushort[]> Read(ushort address, ushort count, CancellationToken ct)
    {
        var request = new byte[5]; request[0] = 3; BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), address); BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), count);
        var pdu = await Request(request, ct);
        if (pdu.Length != 2 + count * 2 || pdu[1] != count * 2) throw new InvalidDataException("寄存器长度不匹配。");
        return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(2 + i * 2))).ToArray();
    }
    public async Task Write(ushort address, ushort[] values, CancellationToken ct)
    {
        var pdu = new byte[6 + values.Length * 2]; pdu[0] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address); BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Length); pdu[5] = (byte)(values.Length * 2);
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + 2 * i), values[i]);
        await Request(pdu, ct);
    }
    public static uint U32(ushort[] a, int i) => ((uint)a[i] << 16) | a[i + 1];
    public static ulong U64(ushort[] a, int i) => ((ulong)U32(a, i) << 32) | U32(a, i + 2);
    public static float Float(ushort[] a, int i) => BitConverter.Int32BitsToSingle(unchecked((int)U32(a, i)));
    public static ushort[] UInt32Words(uint v) => [(ushort)(v >> 16), (ushort)v];
    public static void PutFloat(ushort[] a, int i, float value) { var words = UInt32Words(unchecked((uint)BitConverter.SingleToInt32Bits(value))); words.CopyTo(a, i); }
    public void Dispose() { tcp.Dispose(); gate.Dispose(); }
}
