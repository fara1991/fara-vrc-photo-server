using System.Text;
using FaraVrcPhotoServer;
using Xunit;
using static FaraVrcPhotoServer.Core.Tests.TestPng;

namespace FaraVrcPhotoServer.Core.Tests;

/// <summary>設定・ログの読み続け・常駐の本体（実際の VRChat やタスクトレイは使わない）。</summary>
public sealed class ServiceTests : IDisposable
{
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    private const string Me = "テスト／Me";
    private const string Avatar1 = "avtr_71bd1202-157a-4264-b888-a133209de9ca";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fara-svc-" + Guid.NewGuid().ToString("N")[..8]);

    public ServiceTests()
    {
        Directory.CreateDirectory(_root);
        AppLog.Path = Path.Combine(_root, "log", "app.log");                                              // テストの記録を、本物の置き場所に書かない
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string Line(string time, string body) => $"2026.10.28 {time} Debug      -  {body}";

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }

    // ---------------------------------------------------------------- 設定

    [Fact]
    public void SettingsRoundTripUseDefaultsWhenEmptyAndReportWhatIsMissing()
    {
        var store = new SettingsStore(Path.Combine(_root, "s", "settings.json"));
        Assert.Equal(new AppSettings(), store.Load());                                                    // ファイルが無ければ既定
        Assert.Equal(PhotoNaming.DefaultPhotoFolder, new AppSettings().EffectivePhotoFolder);
        Assert.Equal(HistoryStore.DefaultPath, new AppSettings().EffectiveHistoryPath);

        var saved = new AppSettings { DisplayName = "名前", PhotoFolder = @"D:\p", WriteXmp = false, BackupFolder = @"D:\b", AutoStart = true };
        store.Save(saved);
        Assert.Equal(saved, store.Load());
        Assert.Equal(@"D:\p", saved.EffectivePhotoFolder);

        File.WriteAllText(store.Path, "{ broken");
        Assert.Equal(new AppSettings(), store.Load());                                                    // 壊れたファイルは、既定

        var problems = new AppSettings { PhotoFolder = Path.Combine(_root, "nope"), LogFolder = Path.Combine(_root, "nolog") }.Problems();
        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("表示名"));
        Assert.Empty(new AppSettings { DisplayName = "x", PhotoFolder = Dir("p"), LogFolder = Dir("l") }.Problems());
    }

    // ---------------------------------------------------------------- ログの読み続け

    [Fact]
    public void TheTailerReadsOnlyWhatWasAppendedAndWaitsForHalfWrittenLines()
    {
        var logs = Dir("logs");
        var path = Path.Combine(logs, "output_log_2026-10-28_01-30-36.txt");
        var history = new HistoryStore(Path.Combine(_root, "h.jsonl"));
        var tailer = new LogTailer(logs, Me, history, Jst);
        Assert.Equal(0, tailer.Poll());                                                                   // ログが無いあいだは何もしない

        Append(path, Line("01:49:39", "[Behaviour] Entering Room: Crystal Garden") + "\n" + Line("01:49:39", "[Behaviour] Joining wrld_528e5412-7bb4-42c1-aab2-2b38a629f5de:1~private(usr_x)") + "\n");
        Assert.Equal(1, tailer.Poll());
        Assert.Equal(0, tailer.Poll());                                                                   // 新しい行が無ければ、増えない

        var switching = Line("01:49:51", $"[Behaviour] Switching {Me} to avatar FaraFrillBlood");
        Append(path, switching[..40]);                                                                    // 行の途中まで
        Assert.Equal(0, tailer.Poll());
        Append(path, switching[40..] + "\n");
        Assert.Equal(1, tailer.Poll());                                                                   // ID を待つあいだも、先に履歴に入る
        Assert.Equal("FaraFrillBlood", history.Load().Last().AvatarName);
        Assert.Null(history.Load().Last().AvatarId);

        Append(path, Line("01:49:53", $"Loading Avatar Data:{Avatar1}") + "\n");
        Assert.Equal(1, tailer.Poll());                                                                   // ID が分かったら、ID つきを追記
        var photoTime = new DateTimeOffset(2026, 10, 28, 1, 50, 12, TimeSpan.FromHours(9));
        Assert.Equal(Avatar1, HistoryStore.AvatarAt(history.Load(), photoTime)!.AvatarId);                // 同じ時刻なら、ID つきを使う
        Assert.Equal(0, tailer.Poll());
    }

    [Fact]
    public void TheTailerStartsOverWhenALogIsRecreatedAndIgnoresOtherPlayers()
    {
        var logs = Dir("logs2");
        var path = Path.Combine(logs, "output_log_2026-10-28_01-30-36.txt");
        var history = new HistoryStore(Path.Combine(_root, "h2.jsonl"));
        var tailer = new LogTailer(logs, Me, history, Jst);

        File.WriteAllText(path, Line("01:49:51", "[Behaviour] Switching 他の人 to avatar Other") + "\n" + Line("01:50:00", $"[Behaviour] Switching {Me} to avatar Mine") + "\n");
        Assert.Equal(1, tailer.Poll());
        File.WriteAllText(path, Line("02:00:00", $"[Behaviour] Switching {Me} to avatar Short") + "\n");   // 前より短い（作り直された）
        Assert.Equal(1, tailer.Poll());
        Assert.Equal(["Mine", "Short"], history.Load().Select(e => e.AvatarName));
    }

    // ---------------------------------------------------------------- 常駐の本体

    private AppSettings Settings(string photos, string logs, string name = Me, bool writeXmp = true) => new()
    {
        DisplayName = name,
        PhotoFolder = photos,
        LogFolder = logs,
        HistoryPath = Path.Combine(_root, "service-history.jsonl"),
        WriteXmp = writeXmp,
    };

    [Fact]
    public void TheServiceDoesNotStartWithoutAUsableSettingAndSaysWhy()
    {
        using var service = new PhotoServerService(Settings(Dir("p0"), Dir("l0"), name: ""), Jst);
        Assert.False(service.Start());
        Assert.False(service.IsRunning);
        Assert.Contains("表示名が未設定", service.Status);
    }

    [Fact]
    public async Task ANewPhotoGetsTheAvatarFromTheLiveLogAndTheServiceNeverShowsTheDisplayName()
    {
        var photos = Dir("photos");
        var logs = Dir("logs3");
        var log = Path.Combine(logs, "output_log_2026-10-28_01-30-36.txt");
        Append(log, Line("01:49:51", $"[Behaviour] Switching {Me} to avatar FaraFrillBlood") + "\n" + Line("01:49:53", $"Loading Avatar Data:{Avatar1}") + "\n");
        using var service = new PhotoServerService(Settings(photos, logs), Jst);
        Assert.True(service.Start());
        Assert.True(service.IsRunning);

        // サービスが動いてから、アバターを切り替え、すぐに写真を撮る（ログの読み直しを待たずに処理される）
        Append(log, Line("01:55:00", $"[Behaviour] Switching {Me} to avatar Later") + "\n");
        var path = Path.Combine(photos, "VRChat_2026-10-28_01-55-05.000_8x6.png");
        await File.WriteAllBytesAsync(path, MakePng());

        var xmp = "";
        for (var i = 0; i < 100 && !xmp.Contains("faraxml:AvatarName"); i++)
        {
            await Task.Delay(100);
            xmp = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        }

        Assert.Contains("faraxml:AvatarName=\"Later\"", xmp);                                              // 直前の切り替え（ID を待っている途中のもの）が使われた
        Assert.DoesNotContain(Me, xmp);                                                                    // 表示名は写真に入らない
        Assert.Contains(service.Recent, r => r.Contains("Later") && r.Contains("追記しました"));
        Assert.DoesNotContain(service.Recent.Append(service.Status), s => s.Contains(Me));                 // 状態・最近の処理にも出ない
    }

    [Fact]
    public async Task WithWritingDisabledThePhotoIsLeftAloneButTheHistoryIsKept()
    {
        var photos = Dir("photos4");
        var logs = Dir("logs4");
        Append(Path.Combine(logs, "output_log_2026-10-28_01-30-36.txt"), Line("01:49:51", $"[Behaviour] Switching {Me} to avatar Mine") + "\n");
        using var service = new PhotoServerService(Settings(photos, logs, writeXmp: false), Jst);
        Assert.True(service.Start());
        var path = Path.Combine(photos, "VRChat_2026-10-28_01-55-05.000_8x6.png");
        var bytes = MakePng();
        await File.WriteAllBytesAsync(path, bytes);

        for (var i = 0; i < 100 && !service.Recent.Any(r => r.Contains(".png")); i++) await Task.Delay(100);

        Assert.Contains(service.Recent, r => r.Contains("設定で写真には書きません"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal("Mine", new HistoryStore(Path.Combine(_root, "service-history.jsonl")).Load().Single().AvatarName);
    }

    // ---------------------------------------------------------------- 見張りの失敗

    [Fact]
    public void TheSettingsFileDoesNotHoldTheComputedValues()
    {
        var store = new SettingsStore(Path.Combine(_root, "s2", "settings.json"));
        store.Save(new AppSettings { DisplayName = "名前" });
        var text = File.ReadAllText(store.Path);
        Assert.DoesNotContain("effective", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("名前", store.Load().DisplayName);
    }

    [Fact]
    public void AWatcherThatCannotStartSaysWhyInsteadOfJustFailing()
    {
        var missing = Path.Combine(_root, "no-such-folder");
        var e = Assert.Throws<IOException>(() => new PhotoWatcher(missing, _ => { }));
        Assert.Contains("見張れません", e.Message);
        Assert.Contains(missing, e.Message);
    }

    [Fact]
    public async Task TheServiceDoesNotCrashWhenTheWatchCannotStartAndKeepsRetryingUntilItWorks()
    {
        AppLog.Path = Path.Combine(_root, "log", "app.log");
        var attempts = 0;
        var settings = Settings(Dir("p5"), Dir("l5"));
        using var service = new PhotoServerService(settings, Jst, (folder, onPhoto) =>
        {
            if (++attempts < 3) throw new IOException("Error reading the " + folder + " directory.");
            return new PhotoWatcher(folder, onPhoto);
        })
        {
            LogPollInterval = TimeSpan.FromMilliseconds(50),
            RetryInterval = TimeSpan.FromMilliseconds(100),
        };

        Assert.True(service.Start());                                                                     // 失敗しても、落ちずに始まる
        Assert.False(service.IsRunning);
        Assert.Contains("見張れません", service.Status);

        for (var i = 0; i < 100 && !service.IsRunning; i++) await Task.Delay(50);

        Assert.True(service.IsRunning);
        Assert.Equal("サーバ起動中です", service.Status);
        Assert.True(attempts >= 3);
        var log = File.ReadAllText(AppLog.Path);
        Assert.Contains("写真のフォルダの見張り", log);
        Assert.Contains("見張りを始めました", log);
        Assert.DoesNotContain(Me, log);
    }
}
