using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AxisLab.Core;

/// <summary>Validate the actual PNG stream, its CRCs and bounded scanlines, not only its filename.</summary>
public static class PngValidation
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        var value = (uint)i;
        for (var bit = 0; bit < 8; bit++) value = (value & 1) == 1 ? 0xedb88320u ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();
    public static void Validate(byte[] png, int width, int height)
    {
        void Invalid() => throw new DomainException("InvalidPng", "PNG 图像内容无效、尺寸不符或数据不完整。");
        if (png.Length < 45 || png.Length > MaximumBytes || !png.AsSpan(0, 8).SequenceEqual(Signature)) Invalid();
        var offset = 8; var header = false; var ended = false; var channels = 0; var chunkCount = 0;
        using var compressed = new MemoryStream();
        while (offset < png.Length)
        {
            if (++chunkCount > 1024 || png.Length - offset < 12) Invalid();
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > png.Length - offset - 12) Invalid();
            var size = (int)length;
            var kind = Encoding.ASCII.GetString(png, offset + 4, 4);
            var payload = png.AsSpan(offset + 8, size);
            var crc = 0xffffffffu;
            foreach (var b in png.AsSpan(offset + 4, size + 4)) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
            if ((crc ^ 0xffffffffu) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + size, 4))) Invalid();
            if (!header && kind != "IHDR") Invalid();
            if (kind == "IHDR")
            {
                if (header || size != 13 || BinaryPrimitives.ReadUInt32BigEndian(payload) != width ||
                    BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) != height || payload[8] != 8 ||
                    payload[9] is not (2 or 6) || payload[10] != 0 || payload[11] != 0 || payload[12] != 0) Invalid();
                channels = payload[9] == 6 ? 4 : 3; header = true;
            }
            else if (kind == "IDAT") compressed.Write(payload);
            else if (kind == "IEND") { if (size != 0 || compressed.Length == 0) Invalid(); ended = true; }
            else if ((png[offset + 4] & 32) == 0) Invalid(); // Unknown critical chunks are unsupported.
            offset += size + 12;
            if (ended) break;
        }
        if (!ended || offset != png.Length) Invalid();
        compressed.Position = 0;
        try
        {
            using var stream = new ZLibStream(compressed, CompressionMode.Decompress);
            var row = new byte[checked(width * channels)];
            for (var y = 0; y < height; y++)
            {
                var filter = stream.ReadByte(); if (filter is < 0 or > 4) Invalid();
                stream.ReadExactly(row);
            }
            if (stream.ReadByte() != -1) Invalid();
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        { throw new DomainException("InvalidPng", "PNG 压缩数据损坏或尺寸不符。"); }
    }
}
