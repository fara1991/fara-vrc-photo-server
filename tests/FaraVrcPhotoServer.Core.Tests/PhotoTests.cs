using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FaraVrcPhotoServer;
using Xunit;
using static FaraVrcPhotoServer.Core.Tests.TestPng;

namespace FaraVrcPhotoServer.Core.Tests;

/// <summary>写真（PNG）の読み書き・XMP への追記・撮影日時からのアバターの当てはめ・見張り。実際の写真は使わず、小さな PNG を作って確かめる。</summary>
public sealed class PhotoTests : IDisposable
{
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fara-photo-" + Guid.NewGuid().ToString("N")[..8]);

    public PhotoTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Photo(string name, byte[]? bytes = null)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes ?? MakePng());
        return path;
    }

    private static readonly FaraXmlInfo Info = new("FaraFrillBlood", "avtr_71bd1202-157a-4264-b888-a133209de9ca");

    private static string XmpOf(byte[] png) =>
        PngFile.Parse(png).Chunks.Where(c => c.Type == "iTXt").Select(c => FaraXml.ReadXmpChunk(c.Data)).Single(x => x is not null)!;

    // ---------------------------------------------------------------- PNG

    [Fact]
    public void TheCrcMatchesTheStandardCheckValueAndAnIendChunkParses()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));                                         // CRC-32 の標準の検査値
        var minimal = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(new PngFile([new PngChunk("IHDR", new byte[13]), new PngChunk("IEND", [])]).ToBytes()[8..]).ToArray();
        Assert.Equal(0xAE426082u, BitConverter.ToUInt32(minimal.Skip(minimal.Length - 4).Reverse().ToArray()));   // IEND の CRC は、PNG で決まっている値
    }

    [Fact]
    public void AValidPngRoundTripsByteForByteAndBrokenOnesAreRefused()
    {
        var png = MakePng(twoIdat: true);
        Assert.Equal(png, PngFile.Parse(png).ToBytes());

        Assert.Throws<InvalidDataException>(() => PngFile.Parse("これは PNG ではない"u8.ToArray()));
        var badCrc = (byte[])png.Clone();
        badCrc[^20] ^= 0xFF;                                                                               // 本体を 1 バイト壊す
        Assert.Contains("CRC", Assert.Throws<InvalidDataException>(() => PngFile.Parse(badCrc)).Message);
        Assert.Throws<InvalidDataException>(() => PngFile.Parse(png[..^12]));                              // IEND が無い（途中で切れた）
        Assert.Throws<InvalidDataException>(() => PngFile.Parse(png[..^5]));                               // チャンクの途中で切れた
    }

    // ---------------------------------------------------------------- 追記

    [Fact]
    public void WritingAddsTheAvatarKeepsTheOriginalXmpAndLeavesThePixelsAndTimestampsAlone()
    {
        var path = Photo("a.png");
        var before = File.ReadAllBytes(path);
        var stamp = new DateTime(2026, 10, 28, 1, 50, 13, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var result = PhotoXmpWriter.Write(path, Info);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        var after = File.ReadAllBytes(path);
        var xmp = XmpOf(after);
        Assert.StartsWith(VrcXmp[..VrcXmp.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal)], xmp);   // 元の XMP は、1 文字も変えない
        Assert.EndsWith("</rdf:RDF>\n</x:xmpmeta>", xmp.Replace("\r\n", "\n"));
        XDocument.Parse(xmp);                                                                             // XML として正しい
        var read = FaraXml.Read(xmp)!;
        Assert.Equal(("FaraFrillBlood", "avtr_71bd1202-157a-4264-b888-a133209de9ca", "log"), (read.AvatarName, read.AvatarId, read.Source));
        Assert.Contains("faraxml:Version=\"1\"", xmp);
        Assert.Equal(PngFile.Parse(before).PixelData(), PngFile.Parse(after).PixelData());                // 画素は同じ
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));                                              // 更新日時は元のまま
        Assert.Empty(Directory.EnumerateFiles(_root, "*" + PhotoXmpWriter.TempSuffix));                   // 一時ファイルは残らない
    }

    [Fact]
    public void APhotoWithoutXmpGetsANewChunkBeforeThePixelsAndACompressedXmpIsHandled()
    {
        var plain = Photo("plain.png", MakePng(xmp: null, twoIdat: true));
        Assert.Equal(WriteOutcome.Written, PhotoXmpWriter.Write(plain, Info with { AvatarId = null }).Outcome);
        var parsed = PngFile.Parse(File.ReadAllBytes(plain));
        var types = parsed.Chunks.Select(c => c.Type).ToList();
        Assert.True(types.IndexOf("iTXt") < types.IndexOf("IDAT"));
        var xmp = XmpOf(File.ReadAllBytes(plain));
        XDocument.Parse(xmp);
        Assert.Null(FaraXml.Read(xmp)!.AvatarId);                                                         // ID が無ければ書かない
        Assert.DoesNotContain("AvatarId", xmp);

        var packed = Photo("packed.png", MakePng(compressedXmp: true));
        Assert.Equal(WriteOutcome.Written, PhotoXmpWriter.Write(packed, Info).Outcome);
        var packedXmp = XmpOf(File.ReadAllBytes(packed));
        Assert.Contains("<vrc:WorldDisplayName>Crystal Garden</vrc:WorldDisplayName>", packedXmp);        // 圧縮されていた元の内容も保たれる
        Assert.Equal("FaraFrillBlood", FaraXml.Read(packedXmp)!.AvatarName);
    }

    [Fact]
    public void ATaggedPhotoIsNeverRewrittenAndABrokenOneIsNeverTouched()
    {
        var path = Photo("t.png");
        Assert.Equal(WriteOutcome.Written, PhotoXmpWriter.Write(path, Info).Outcome);
        var tagged = File.ReadAllBytes(path);

        var again = PhotoXmpWriter.Write(path, Info with { AvatarName = "Other" });
        Assert.Equal(WriteOutcome.AlreadyTagged, again.Outcome);
        Assert.Equal(tagged, File.ReadAllBytes(path));                                                    // 二重に書かない・内容を変えない

        var broken = Photo("broken.png", MakePng()[..^20]);
        var brokenBytes = File.ReadAllBytes(broken);
        Assert.Equal(WriteOutcome.Invalid, PhotoXmpWriter.Write(broken, Info).Outcome);
        Assert.Equal(brokenBytes, File.ReadAllBytes(broken));
        Assert.Equal(WriteOutcome.Invalid, PhotoXmpWriter.Write(Path.Combine(_root, "missing.png"), Info).Outcome);
        Assert.Empty(Directory.EnumerateFiles(_root, "*" + PhotoXmpWriter.TempSuffix));

        var noTarget = Photo("noTarget.png", MakePng(xmp: "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"));    // 追記先（rdf:RDF）が無い XMP
        var noTargetBytes = File.ReadAllBytes(noTarget);
        Assert.Equal(WriteOutcome.Invalid, PhotoXmpWriter.Write(noTarget, Info).Outcome);
        Assert.Equal(noTargetBytes, File.ReadAllBytes(noTarget));
    }

    [Fact]
    public void ValuesAreEscapedAndLimitedSoTheXmpStaysValid()
    {
        var path = Photo("e.png");
        var name = "A&B <\"x\"> 'y' 日本語\u0001" + new string('z', 400);
        Assert.Equal(WriteOutcome.Written, PhotoXmpWriter.Write(path, new FaraXmlInfo(name, null)).Outcome);
        var xmp = XmpOf(File.ReadAllBytes(path));
        XDocument.Parse(xmp);                                                                             // 記号を入れても、XML として正しい
        var read = FaraXml.Read(xmp)!.AvatarName;
        Assert.StartsWith("A&B <\"x\"> 'y' 日本語 ", read);                                              // 制御文字は空白に
        Assert.True(read.Length <= FaraXml.MaxValueLength);
    }

    [Fact]
    public void TheBackupKeepsTheOriginalAndNeverOverwritesAnEarlierBackup()
    {
        var backup = Path.Combine(_root, "backup");
        var path = Photo("b.png");
        var original = File.ReadAllBytes(path);
        Assert.Equal(WriteOutcome.Written, PhotoXmpWriter.Write(path, Info, backup).Outcome);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(backup, "b.png")));

        File.WriteAllBytes(path, original);                                                                // 同じ名前の写真をもう一度（元に戻して）
        PhotoXmpWriter.Write(path, Info, backup);
        Assert.True(File.Exists(Path.Combine(backup, "b (2).png")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(backup, "b.png")));
    }

    // ---------------------------------------------------------------- 撮影日時と当てはめ

    [Fact]
    public void TheTakenTimeComesFromTheFileNameInBothFormats()
    {
        var expected = new DateTimeOffset(2026, 10, 28, 1, 50, 12, 345, TimeSpan.FromHours(9));
        Assert.Equal(expected, PhotoNaming.TryTakenAt(@"C:\x\VRChat_2026-10-28_01-50-12.345_3840x2160.png", Jst));
        Assert.Equal(new DateTimeOffset(2021, 11, 27, 3, 0, 13, 803, TimeSpan.FromHours(9)), PhotoNaming.TryTakenAt("VRChat_1920x1080_2021-11-27_03-00-13.803.png", Jst));
        Assert.Null(PhotoNaming.TryTakenAt("Screenshot_2026-10-28_01-50-12.345.png", Jst));
        Assert.Null(PhotoNaming.TryTakenAt("VRChat_2026-10-28_01-50-12.345_3840x2160.jpg", Jst));
        Assert.Null(PhotoNaming.TryTakenAt("VRChat_2026-13-45_99-50-12.345_3840x2160.png", Jst));
    }

    [Fact]
    public void ThePhotoGetsTheAvatarThatWasInUseWhenItWasTaken()
    {
        var history = new[]
        {
            HistoryEvent.Avatar(new DateTimeOffset(2026, 10, 28, 1, 40, 0, TimeSpan.FromHours(9)), "Before", null),
            HistoryEvent.Avatar(new DateTimeOffset(2026, 10, 28, 1, 49, 51, TimeSpan.FromHours(9)), "FaraFrillBlood", "avtr_71bd1202-157a-4264-b888-a133209de9ca"),
            HistoryEvent.Avatar(new DateTimeOffset(2026, 10, 28, 2, 10, 0, TimeSpan.FromHours(9)), "Later", null),
        };
        var processor = new PhotoProcessor(() => history, new PhotoOptions(), Jst);
        var path = Photo("VRChat_2026-10-28_01-50-12.345_8x6.png");

        var result = processor.Process(path);

        Assert.Equal(PhotoOutcome.Written, result.Outcome);
        Assert.Equal("FaraFrillBlood", result.Info!.AvatarName);
        Assert.Equal("FaraFrillBlood", FaraXml.Read(XmpOf(File.ReadAllBytes(path)))!.AvatarName);
        Assert.Equal(PhotoOutcome.AlreadyTagged, processor.Process(path).Outcome);                         // 2 回目は何もしない
    }

    [Fact]
    public void WithoutAnAvatarOrWithWritingDisabledNothingIsWritten()
    {
        var history = new[] { HistoryEvent.Avatar(new DateTimeOffset(2026, 10, 28, 1, 49, 51, TimeSpan.FromHours(9)), "FaraFrillBlood", null) };
        var early = Photo("VRChat_2026-10-28_01-00-00.000_8x6.png");
        var late = Photo("VRChat_2026-10-28_01-50-12.345_8x6.png");
        var other = Photo("holiday.png");
        var lateBytes = File.ReadAllBytes(late);

        Assert.Equal(PhotoOutcome.NoAvatar, new PhotoProcessor(() => history, new PhotoOptions(), Jst).Process(early).Outcome);   // 撮影より後のものは使わない
        Assert.Equal(PhotoOutcome.NotPhoto, new PhotoProcessor(() => history, new PhotoOptions(), Jst).Process(other).Outcome);
        Assert.Equal(PhotoOutcome.WriteDisabled, new PhotoProcessor(() => history, new PhotoOptions(WriteXmp: false), Jst).Process(late).Outcome);
        Assert.Equal(lateBytes, File.ReadAllBytes(late));
    }

    // ---------------------------------------------------------------- 見張り

    [Fact]
    public async Task TheWatcherWaitsUntilAPhotoIsCompletelyWrittenAndIgnoresTemporaryFiles()
    {
        var seen = new List<(string Path, long Size)>();
        using var watcher = new PhotoWatcher(_root, p =>
        {
            lock (seen) seen.Add((p, new FileInfo(p).Length));
        });
        var png = MakePng();
        var path = Path.Combine(_root, "VRChat_2026-10-28_01-50-12.345_8x6.png");

        await File.WriteAllBytesAsync(path + PhotoXmpWriter.TempSuffix, png);                              // 一時ファイルは見ない
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))        // VRChat のように、少しずつ書く
        {
            stream.Write(png, 0, png.Length / 2);
            stream.Flush();
            await Task.Delay(400);
            stream.Write(png, png.Length / 2, png.Length - png.Length / 2);
        }

        for (var i = 0; i < 100 && seen.Count == 0; i++) await Task.Delay(100);
        await Task.Delay(1500);                                                                           // 重複して呼ばれないか見る

        lock (seen)
        {
            var only = Assert.Single(seen);
            Assert.Equal(path, only.Path);
            Assert.Equal(png.Length, only.Size);                                                          // 書き終わってから渡される
        }
    }
}
