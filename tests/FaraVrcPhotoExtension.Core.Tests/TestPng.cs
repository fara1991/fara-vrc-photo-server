using System.IO.Compression;
using System.Text;
using FaraVrcPhotoExtension;

namespace FaraVrcPhotoExtension.Core.Tests;

/// <summary>テスト用の小さな PNG を作る。</summary>
internal static class TestPng
{
    /// <summary>VRChat が書く形に近い XMP（属性ではなく子要素・複数の rdf:Description）。</summary>
    public const string VrcXmp = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
          <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:xmp="http://ns.adobe.com/xap/1.0/">
            <rdf:Description>
              <xmp:CreatorTool>VRChat</xmp:CreatorTool>
              <xmp:CreateDate>2026-10-28T01:50:12.0000000+09:00</xmp:CreateDate>
            </rdf:Description>
            <rdf:Description xmlns:vrc="http://ns.vrchat.com/vrc/1.0/">
              <vrc:WorldID>wrld_528e5412-7bb4-42c1-aab2-2b38a629f5de</vrc:WorldID>
              <vrc:WorldDisplayName>Crystal Garden</vrc:WorldDisplayName>
            </rdf:Description>
          </rdf:RDF>
        </x:xmpmeta>
        """;

    private static byte[] Pixels(int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width * 3; x++) raw[y * (1 + width * 3) + 1 + x] = (byte)(x * 7 + y * 13);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        return output.ToArray();
    }

    private static byte[] Be(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>小さな PNG。xmp が null なら XMP なし。compressedXmp なら、iTXt を圧縮して入れる。</summary>
    public static byte[] MakePng(string? xmp = VrcXmp, bool compressedXmp = false, bool twoIdat = false)
    {
        var chunks = new List<PngChunk> { new("IHDR", [.. Be(8), .. Be(6), 8, 2, 0, 0, 0]) };
        if (xmp is not null)
        {
            using var data = new MemoryStream();
            data.Write(Encoding.Latin1.GetBytes(FaraXml.XmpKeyword));
            data.WriteByte(0);
            data.WriteByte(compressedXmp ? (byte)1 : (byte)0);
            data.WriteByte(0);
            data.WriteByte(0);
            data.WriteByte(0);
            var text = Encoding.UTF8.GetBytes(xmp);
            if (compressedXmp)
            {
                using var packed = new MemoryStream();
                using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(text);
                text = packed.ToArray();
            }
            data.Write(text);
            chunks.Add(new PngChunk("iTXt", data.ToArray()));
        }
        var pixels = Pixels(8, 6);
        if (twoIdat)
        {
            chunks.Add(new PngChunk("IDAT", pixels[..10]));
            chunks.Add(new PngChunk("IDAT", pixels[10..]));
        }
        else
        {
            chunks.Add(new PngChunk("IDAT", pixels));
        }
        chunks.Add(new PngChunk("IEND", []));
        return new PngFile(chunks).ToBytes();
    }
}
