using System.IO.Compression;
using System.Text;

namespace Cubit.Core.Imaging;

/// <summary>极简 PNG 编码器（RGBA，8 位），仅用于调试截图，无第三方依赖。</summary>
public static class SimplePng
{
    public static void Save(string path, int width, int height, byte[] rgba)
    {
        using var stream = File.Create(path);
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        WriteChunk(stream, "IHDR", BuildHeader(width, height));

        // 每行前面加 filter 字节 0
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Array.Copy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        WriteChunk(stream, "IDAT", compressed.ToArray());
        WriteChunk(stream, "IEND", []);
    }

    private static byte[] BuildHeader(int width, int height)
    {
        var header = new byte[13];
        WriteInt32(header, 0, width);
        WriteInt32(header, 4, height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // color type RGBA
        header[10] = 0; // compression
        header[11] = 0; // filter
        header[12] = 0; // interlace
        return header;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = BitConverter.GetBytes(data.Length);
        if (BitConverter.IsLittleEndian) Array.Reverse(length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);

        var crcInput = new byte[typeBytes.Length + data.Length];
        Array.Copy(typeBytes, crcInput, typeBytes.Length);
        Array.Copy(data, 0, crcInput, typeBytes.Length, data.Length);
        var crc = BitConverter.GetBytes(Crc32(crcInput));
        if (BitConverter.IsLittleEndian) Array.Reverse(crc);

        stream.Write(data);
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        Array.Copy(bytes, 0, buffer, offset, 4);
    }
}
