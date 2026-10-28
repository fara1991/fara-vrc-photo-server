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
    private readonly string _root = TestEnv.NewFolder("fara-photo");

    public PhotoTests()
    {
        // 退避と記録は、このテストのフォルダの中に作る（本物の置き場所・ほかのテストと分ける）
        PendingJournal.Folder = Path.Combine(_root, "pending");
        AppLog.Path = Path.Combine(_root, "app.log");
    }

    private IReadOnlyList<string> JournalFiles() =>
        Directory.Exists(PendingJournal.Folder) ? Directory.EnumerateFiles(PendingJournal.Folder).ToList() : [];

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
        Assert.Throws<PngTruncatedException>(() => PngFile.Parse(png[..^12]));                             // IEND が無い（途中で切れた）
        Assert.Throws<PngTruncatedException>(() => PngFile.Parse(png[..^5]));                              // チャンクの途中で切れた
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

    // ---------------------------------------------------------------- 控えを残さないときは、写真に直接書く

    [Fact]
    public void WithoutABackupTheTagIsWrittenIntoThePhotoFileItselfNotIntoAReplacement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Photo("direct.png");
        var link = Path.Combine(_root, "direct-hardlink.png");
        using (var cmd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{path}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            cmd.StandardOutput.ReadToEnd();
            cmd.WaitForExit();
        }
        Assert.True(File.Exists(link), "ハードリンクを作れなかったので、この確認はできません（写真のフォルダが NTFS か確かめてください）");
        var stamp = new DateTime(2026, 10, 28, 1, 50, 13, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var result = PhotoXmpWriter.Write(path, Info);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.Contains("直接", result.Message);
        // ファイルそのものに書いたので、同じファイルを指すハードリンクにも、タグが見える（入れ替えだと、リンクは古い内容のまま切れる）
        Assert.Equal("FaraFrillBlood", FaraXml.Read(XmpOf(File.ReadAllBytes(link)))!.AvatarName);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(link));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*" + PhotoXmpWriter.TempSuffix));
    }

    [Fact]
    public void WithABackupTheOriginalIsKeptAndThePhotoIsReplacedAsBefore()
    {
        var backup = Path.Combine(_root, "backup2");
        var path = Photo("kept.png");
        var original = File.ReadAllBytes(path);

        var result = PhotoXmpWriter.Write(path, Info, backup);

        Assert.Equal(WriteOutcome.Written, result.Outcome);
        Assert.DoesNotContain("直接", result.Message);                                                      // 控えを残すときは、今までの入れ替え
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(backup, "kept.png")));
    }

    [Fact]
    public void APhotoThatCannotBeOpenedForWritingIsLeftUntouchedAndSaysSo()
    {
        var locked = Photo("locked.png");
        var before = File.ReadAllBytes(locked);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))                       // ほかのプログラムが、書き込みを許さずに開いている
        {
            var result = PhotoXmpWriter.Write(locked, Info);
            Assert.Equal(WriteOutcome.Invalid, result.Outcome);
        }
        Assert.Equal(before, File.ReadAllBytes(locked));
        Assert.Empty(Directory.EnumerateFiles(_root, "*" + PhotoXmpWriter.TempSuffix));

        var readOnly = Photo("readonly.png");
        var readOnlyBefore = File.ReadAllBytes(readOnly);
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(WriteOutcome.Invalid, PhotoXmpWriter.Write(readOnly, Info).Outcome);
            Assert.Equal(readOnlyBefore, File.ReadAllBytes(readOnly));
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    // ---------------------------------------------------------------- 途中で止まったとき（退避・復元）

    [Fact]
    public void ADirectWriteThatDoesNotReadBackTheSameIsPutBackAndTheJournalIsGone()
    {
        var path = Photo("restore.png");
        var original = File.ReadAllBytes(path);
        var stamp = new DateTime(2026, 10, 28, 1, 50, 13, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        PhotoXmpWriter.AfterDirectWriteForTests = p => File.WriteAllBytes(p, new byte[] { 1, 2, 3 });   // 書いたあとに、内容が違っていた
        try
        {
            var result = PhotoXmpWriter.Write(path, Info);

            Assert.Equal(WriteOutcome.Invalid, result.Outcome);
            Assert.Contains("元の内容に戻しました", result.Message);
        }
        finally
        {
            PhotoXmpWriter.AfterDirectWriteForTests = null;
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Empty(JournalFiles());                                                                      // 戻せたので、退避は消えている
    }

    [Fact]
    public void WhenThePhotoCannotBeReopenedTheJournalStaysUntilTheNextStart()
    {
        var path = Photo("kept-journal.png");
        var original = File.ReadAllBytes(path);
        FileStream? holder = null;
        PhotoXmpWriter.AfterDirectWriteForTests = p =>
        {
            File.WriteAllBytes(p, new byte[] { 1, 2, 3 });                                                 // 壊れた内容にして
            holder = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);                    // ほかから開けないようにする（確かめも戻すことも、できない）
        };
        try
        {
            var result = PhotoXmpWriter.Write(path, Info);

            Assert.Equal(WriteOutcome.Invalid, result.Outcome);
            Assert.Contains("次の起動で元に戻します", result.Message);
            Assert.Single(JournalFiles(), f => f.EndsWith(".json", StringComparison.Ordinal));
        }
        finally
        {
            PhotoXmpWriter.AfterDirectWriteForTests = null;
            holder?.Dispose();
        }
        Assert.NotEqual(original, File.ReadAllBytes(path));                                                // まだ戻していない

        var messages = PendingJournal.RecoverAll();                                                        // 次の起動で
        Assert.Contains(messages, m => m.Contains("元に戻しました"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public void AStartAfterACrashPutsBackADamagedPhotoAndOnlyCleansUpTheRest()
    {
        var original = MakePng();
        var copy = Photo("crash-copy.png", original);
        Assert.Equal(WriteOutcome.Written, PhotoXmpWriter.Write(copy, Info).Outcome);
        var updated = File.ReadAllBytes(copy);                                                              // 書き終えた内容（正しいタグつき）

        // 書きかけで止まった: 退避はあり、写真は途中まで
        var torn = Photo("torn.png", original);
        var stamp = new DateTime(2026, 10, 28, 1, 50, 13, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(torn, stamp);
        Assert.NotNull(PendingJournal.TryBegin(torn, original, updated, stamp, stamp));
        File.WriteAllBytes(torn, updated[..^40]);

        // 書き終えて、退避を消す前に止まった: 写真は正しい内容
        Assert.NotNull(PendingJournal.TryBegin(copy, original, updated, stamp, stamp));

        var messages = PendingJournal.RecoverAll();

        Assert.Equal(original, File.ReadAllBytes(torn));                                                   // 壊れていた写真は元に戻る
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(torn));
        Assert.Equal(updated, File.ReadAllBytes(copy));                                                    // 正しい写真には触れない
        Assert.Contains(messages, m => m.Contains("元に戻しました") && m.Contains("torn.png"));
        Assert.Contains(messages, m => m.Contains("最後まで済んでいた") && m.Contains("crash-copy.png"));
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public void ABrokenRecordIsDroppedAndThePhotoIsNotTouched()
    {
        var path = Photo("pending-check.png");
        var before = File.ReadAllBytes(path);
        Assert.NotNull(PendingJournal.TryBegin(path, before, [9, 9, 9], DateTime.UtcNow, DateTime.UtcNow));
        var record = JournalFiles().Single(f => f.EndsWith(".json", StringComparison.Ordinal));
        File.WriteAllText(record, "{ 壊れた記録");                                                       // 記録の中身が壊れている
        var messages = PendingJournal.RecoverAll();
        Assert.Contains(messages, m => m.Contains("記録が壊れていた"));
        Assert.Empty(JournalFiles());
        Assert.Equal(before, File.ReadAllBytes(path));                                                     // 写真には触れない
    }

    [Fact]
    public void APhotoWithTwoXmpChunksIsRefusedWithoutAnException()
    {
        var chunks = PngFile.Parse(MakePng()).Chunks.ToList();
        chunks.Insert(chunks.FindIndex(c => c.Type == "IDAT"), new PngChunk("iTXt", FaraXml.BuildXmpChunk("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>")));
        var path = Photo("two-xmp.png", new PngFile(chunks).ToBytes());
        var before = File.ReadAllBytes(path);

        var result = PhotoXmpWriter.Write(path, Info);

        Assert.Equal(WriteOutcome.Invalid, result.Outcome);
        Assert.Contains("2 つ", result.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(JournalFiles());
    }

    [Fact]
    public void WhenOnlyTheTimestampCannotBeSetAfterTheReplaceTheTagStillCountsAsWritten()
    {
        var path = Photo("stamp.png");
        PhotoXmpWriter.AfterReplaceForTests = _ => throw new IOException("時刻を設定できません（テスト）");
        try
        {
            var result = PhotoXmpWriter.Write(path, Info, Path.Combine(_root, "backup-stamp"));

            Assert.Equal(WriteOutcome.Written, result.Outcome);
            Assert.NotNull(result.Warning);
            Assert.Contains("更新日時", result.Warning);
            Assert.Equal("FaraFrillBlood", FaraXml.Read(XmpOf(File.ReadAllBytes(path)))!.AvatarName);
        }
        finally
        {
            PhotoXmpWriter.AfterReplaceForTests = null;
        }
    }

    [Fact]
    public async Task APhotoStillBeingWrittenIsReadAgainInsteadOfBeingRefused()
    {
        var full = MakePng();
        var path = Photo("growing.png", full[..^12]);                                                      // IEND の手前まで
        var finishing = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await File.WriteAllBytesAsync(path, full);                                                      // 少し遅れて書き終わる
        });

        var result = PhotoXmpWriter.Write(path, Info);

        await finishing;
        Assert.Equal(WriteOutcome.Written, result.Outcome);
    }

    // ---------------------------------------------------------------- 見張りの失敗・待ち切れ・控え

    [Fact]
    public async Task AWatcherThatReportsAnErrorIsRebuiltAndPicksUpPhotosItMissed()
    {
        var before = Path.Combine(_root, "VRChat_2026-10-28_01-50-12.345_8x6.png");
        await File.WriteAllBytesAsync(before, MakePng());                                                  // 見張りを始める前にできた写真（イベントは来ない）
        var seen = new List<string>();
        var messages = new List<string>();
        using var watcher = new PhotoWatcher(_root, p => { lock (seen) seen.Add(p); });
        watcher.Problem += m => { lock (messages) messages.Add(m); };

        watcher.RaiseErrorForTests();
        for (var i = 0; i < 100 && seen.Count == 0; i++) await Task.Delay(100);

        lock (seen) Assert.Equal(new[] { before }, seen);
        lock (messages) Assert.Contains(messages, m => m.Contains("作り直し"));
    }

    [Fact]
    public async Task APhotoThatFailsInTheHandlerIsReportedInsteadOfSilenced()
    {
        var messages = new List<string>();
        using var watcher = new PhotoWatcher(_root, _ => throw new InvalidOperationException("テスト"));
        watcher.Problem += m => { lock (messages) messages.Add(m); };

        await File.WriteAllBytesAsync(Path.Combine(_root, "x.png"), MakePng());
        for (var i = 0; i < 100 && messages.Count == 0; i++) await Task.Delay(100);

        lock (messages) Assert.Contains(messages, m => m.Contains("例外") && m.Contains("x.png"));
    }

    [Fact]
    public async Task WaitingForAPhotoThatIsGoneGivesUpAtOnce()
    {
        var started = DateTime.UtcNow;
        Assert.False(await PhotoWatcher.WaitUntilStableAsync(Path.Combine(_root, "gone.png"), CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));                                   // 30 秒は待たない
    }

    [Fact]
    public async Task APhotoInTheSettingsBackupFolderIsNotWatchedButItsSiblingIs()
    {
        var kept = Directory.CreateDirectory(Path.Combine(_root, "kept-copies")).FullName;
        var seen = new List<string>();
        using var watcher = new PhotoWatcher(_root, p => { lock (seen) seen.Add(Path.GetFileName(p)); }, kept);

        await File.WriteAllBytesAsync(Path.Combine(kept, "VRChat_2026-10-28_01-55-01.000_8x6.png"), MakePng());
        await File.WriteAllBytesAsync(Path.Combine(_root, "VRChat_2026-10-28_01-55-02.000_8x6.png"), MakePng());
        for (var i = 0; i < 100 && seen.Count == 0; i++) await Task.Delay(100);
        await Task.Delay(2000);

        lock (seen) Assert.Equal(new[] { "VRChat_2026-10-28_01-55-02.000_8x6.png" }, seen);
    }
}
