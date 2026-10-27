using System.Buffers.Binary;
using System.Text;

namespace FaraVrcPhotoServer;

/// <summary>PNG の 1 チャンク（種類 4 文字と、本体。長さと CRC は書き出すときに付ける）。</summary>
public sealed record PngChunk(string Type, byte[] Data);

/// <summary>
/// PNG ファイルをチャンクの並びとして読み、書き戻す（画素のデータ IDAT などは、そのままのバイトで持つ）。
/// 読むときに、シグネチャ・IHDR が最初・IEND が最後・すべてのチャンクの CRC を検査する。合わなければ <see cref="InvalidDataException"/>。
/// 書き出しは、読んだチャンクをそのまま並べ直すだけなので、変えていないチャンクは元とバイトまで同じ。
/// </summary>
public sealed class PngFile(IReadOnlyList<PngChunk> chunks)
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public IReadOnlyList<PngChunk> Chunks { get; } = chunks;

    public static PngFile Parse(byte[] bytes)
    {
        if (bytes.Length < Signature.Length + 12 || !bytes.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("PNG ではありません（先頭の印が違います）");
        var chunks = new List<PngChunk>();
        var position = Signature.Length;
        while (position < bytes.Length)
        {
            if (bytes.Length - position < 12) throw new InvalidDataException("PNG のチャンクが途中で切れています");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            if (length > int.MaxValue || position + 12L + length > bytes.Length) throw new InvalidDataException("PNG のチャンクの長さが合いません");
            var typeBytes = bytes.AsSpan(position + 4, 4);
            foreach (var b in typeBytes)
                if (!char.IsAsciiLetter((char)b)) throw new InvalidDataException("PNG のチャンクの種類が不正です");
            var crcExpected = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position + 8 + (int)length, 4));
            if (Crc32.Compute(bytes.AsSpan(position + 4, 4 + (int)length)) != crcExpected) throw new InvalidDataException("PNG のチャンクの CRC が合いません（壊れています）");
            chunks.Add(new PngChunk(Encoding.ASCII.GetString(typeBytes), bytes.AsSpan(position + 8, (int)length).ToArray()));
            position += 12 + (int)length;
        }
        if (chunks.Count < 2 || chunks[0].Type != "IHDR") throw new InvalidDataException("PNG の最初が IHDR ではありません");
        if (chunks[^1].Type != "IEND") throw new InvalidDataException("PNG の最後が IEND ではありません（途中で切れているかもしれません）");
        return new PngFile(chunks);
    }

    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        stream.Write(Signature);
        Span<byte> number = stackalloc byte[4];
        foreach (var chunk in Chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(number, (uint)chunk.Data.Length);
            stream.Write(number);
            var typeAndData = new byte[4 + chunk.Data.Length];
            Encoding.ASCII.GetBytes(chunk.Type, typeAndData);
            chunk.Data.CopyTo(typeAndData, 4);
            stream.Write(typeAndData);
            BinaryPrimitives.WriteUInt32BigEndian(number, Crc32.Compute(typeAndData));
            stream.Write(number);
        }
        return stream.ToArray();
    }

    /// <summary>画素のデータ（IDAT をつなげたもの）。書き換えの前後で同じか確かめるのに使う。</summary>
    public byte[] PixelData()
    {
        using var stream = new MemoryStream();
        foreach (var chunk in Chunks.Where(c => c.Type == "IDAT")) stream.Write(chunk.Data);
        return stream.ToArray();
    }
}

/// <summary>PNG の CRC-32（チャンクの種類と本体にかかる）。</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
