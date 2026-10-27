using FaraVrcPhotoServer;
using Xunit;

namespace FaraVrcPhotoServer.Core.Tests;

/// <summary>ログの読み取りと履歴（実ログの行の形を、架空の名前で再現して確かめる）。</summary>
public sealed class LogAndHistoryTests : IDisposable
{
    private static readonly TimeZoneInfo Jst = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
    private const string Me = "テスト／Me";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fara-vrcserver-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string World = "wrld_528e5412-7bb4-42c1-aab2-2b38a629f5de";
    private const string Avatar1 = "avtr_71bd1202-157a-4264-b888-a133209de9ca";

    private static string Line(string time, string body) => $"2026.10.28 {time} Debug      -  {body}";

    private static List<HistoryEvent> Parse(string displayName, params string[] lines)
    {
        var parser = new LogParser(displayName, Jst);
        var events = new List<HistoryEvent>();
        foreach (var line in lines) events.AddRange(parser.Feed(line));
        events.AddRange(parser.Flush());
        return events;
    }

    [Fact]
    public void AWorldJoinGivesTheWorldNameAndIdAndNeverTheInstance()
    {
        var events = Parse(Me,
            Line("01:49:39", "[Behaviour] Entering Room: Crystal Garden"),
            Line("01:49:39", $"[Behaviour] Joining {World}:49024~private(usr_49161266-8b29-4a76-be2a-0e9773ebd8af)~region(jp)"));

        var world = Assert.Single(events);
        Assert.Equal(HistoryEventTypes.World, world.Type);
        Assert.Equal(World, world.WorldId);
        Assert.Equal("Crystal Garden", world.WorldName);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 1, 49, 39, TimeSpan.FromHours(9)), world.Time);
        Assert.DoesNotContain("usr_", world.ToJsonLine());
        Assert.DoesNotContain("49024", world.ToJsonLine());
    }

    [Fact]
    public void MyAvatarSwitchGetsTheIdFromTheFollowingLoadingLineWithinThirtySeconds()
    {
        var events = Parse(Me,
            Line("01:49:51", $"[Behaviour] Switching {Me} to avatar FaraFrillBlood"),
            Line("01:49:51", "[Behaviour] OnPlayerJoined 何か"),
            Line("01:49:53", $"Loading Avatar Data:{Avatar1}"));

        var avatar = Assert.Single(events);
        Assert.Equal("FaraFrillBlood", avatar.AvatarName);
        Assert.Equal(Avatar1, avatar.AvatarId);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 1, 49, 51, TimeSpan.FromHours(9)), avatar.Time);   // 時刻は切り替えの行
    }

    [Fact]
    public void ALoadingLineAfterThePatienceWindowOrWithoutASwitchGivesNoId()
    {
        var late = Parse(Me,
            Line("01:49:51", $"[Behaviour] Switching {Me} to avatar A"),
            Line("01:50:30", $"Loading Avatar Data:{Avatar1}"));
        var only = Assert.Single(late);
        Assert.Null(only.AvatarId);                                                                       // 間隔を過ぎたら、ID なしで確定

        var none = Parse(Me, Line("01:49:51", $"Loading Avatar Data:{Avatar1}"));
        Assert.Empty(none);                                                                               // 切り替えの無い読み込みは、何も出さない
    }

    [Fact]
    public void ASwitchWithoutAnIdIsStillEmittedAtTheNextSwitchAndAtTheEnd()
    {
        var events = Parse(Me,
            Line("01:49:51", $"[Behaviour] Switching {Me} to avatar A"),
            Line("01:49:55", $"[Behaviour] Switching {Me} to avatar B"),
            Line("01:49:56", $"Loading Avatar Data:{Avatar1}"));

        Assert.Equal(2, events.Count);
        Assert.Equal(("A", (string?)null), (events[0].AvatarName, events[0].AvatarId));
        Assert.Equal(("B", (string?)Avatar1), (events[1].AvatarName, events[1].AvatarId));
    }

    [Fact]
    public void OtherPlayersSwitchesAndAnEmptyDisplayNameRecordNothing()
    {
        var lines = new[]
        {
            Line("01:49:51", "[Behaviour] Switching 他の人 to avatar OtherAvatar"),
            Line("01:49:52", $"[Behaviour] Switching {Me} to avatar Mine"),
        };
        var mine = Assert.Single(Parse(Me, lines));
        Assert.Equal("Mine", mine.AvatarName);
        Assert.Empty(Parse("", lines));                                                                   // 表示名が空のあいだは、何も記録しない
        Assert.Empty(Parse("   ", lines));
        Assert.Empty(Parse("テスト", lines));                                                             // 前方一致では拾わない（「テスト／Me」の行は別人）
    }

    [Fact]
    public void LinesThatDoNotLookLikeLogLinesAreIgnoredAndTheDisplayNameNeverReachesTheOutput()
    {
        var events = Parse(Me,
            "ただの続きの行",
            "",
            Line("01:49:51", $"[Behaviour] Switching {Me} to avatar Mine"),
            "    at SomeStackTrace()",
            Line("01:49:52", "[Behaviour] Entering Room: "));                                              // 名前が空の行は無視

        var avatar = Assert.Single(events);
        Assert.DoesNotContain(Me, avatar.ToJsonLine());
    }

    [Fact]
    public void TheJsonLineHasTheSpecFormatAndRoundTrips()
    {
        var avatar = HistoryEvent.Avatar(new DateTimeOffset(2026, 10, 28, 1, 49, 51, TimeSpan.FromHours(9)), "FaraFrillBlood", Avatar1);
        Assert.Equal($"{{\"t\":\"2026-10-28T01:49:51+09:00\",\"type\":\"avatar\",\"avatarName\":\"FaraFrillBlood\",\"avatarId\":\"{Avatar1}\"}}", avatar.ToJsonLine());
        var noId = HistoryEvent.Avatar(avatar.Time, "日本語のアバター", null);
        Assert.Contains("日本語のアバター", noId.ToJsonLine());                                           // 日本語はそのまま
        Assert.DoesNotContain("avatarId", noId.ToJsonLine());                                             // 無い項目は書かない
        Assert.Equal(avatar, HistoryEvent.TryParse(avatar.ToJsonLine()));
        Assert.Null(HistoryEvent.TryParse("{ broken"));
        Assert.Null(HistoryEvent.TryParse("{\"t\":\"2026-10-28T01:49:51+09:00\",\"type\":\"unknown\"}"));
        Assert.Null(HistoryEvent.TryParse("{\"type\":\"avatar\"}"));
    }

    [Fact]
    public void TheHistoryNeverAppendsTheSameEventTwiceAndSkipsBrokenLines()
    {
        var history = new HistoryStore(Path.Combine(_root, "history.jsonl"));
        var a = HistoryEvent.Avatar(new DateTimeOffset(2026, 10, 28, 1, 49, 51, TimeSpan.FromHours(9)), "A", Avatar1);
        var w = HistoryEvent.World(new DateTimeOffset(2026, 10, 28, 1, 49, 39, TimeSpan.FromHours(9)), World, "Crystal Garden");

        Assert.Equal(2, history.Append([a, w, a]));                                                       // 同じイベントは 1 つ
        Assert.Equal(0, history.Append([a, w]));                                                          // 読み直しても増えない
        File.AppendAllText(history.Path, "これは壊れた行");                                                // 改行なしで途切れた行
        var b = HistoryEvent.Avatar(a.Time.AddMinutes(5), "B", null);
        Assert.Equal(1, history.Append([b]));

        var loaded = history.Load();
        Assert.Equal([w, a, b], loaded);                                                                  // 時刻順・壊れた行は読み飛ばす
    }

    [Fact]
    public void TheAvatarAtAPhotoTimeIsTheLatestSwitchBeforeItWithinTwentyFourHours()
    {
        var t = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.FromHours(9));
        var history = new[]
        {
            HistoryEvent.Avatar(t.AddHours(-30), "Old", null),
            HistoryEvent.Avatar(t.AddHours(-2), "A", null),
            HistoryEvent.World(t.AddHours(-1), World, "W"),
            HistoryEvent.Avatar(t.AddMinutes(-10), "B", Avatar1),
            HistoryEvent.Avatar(t.AddMinutes(5), "After", null),
        };

        Assert.Equal("B", HistoryStore.AvatarAt(history, t)!.AvatarName);
        Assert.Equal("A", HistoryStore.AvatarAt(history, t.AddMinutes(-30))!.AvatarName);
        Assert.Null(HistoryStore.AvatarAt(history, t.AddHours(-40)));                                      // それ以前の切り替えが無い
        Assert.Null(HistoryStore.AvatarAt([history[0]], t));                                               // 24 時間より前の切り替えは使わない
    }

    [Fact]
    public void ScanningTheLogFolderReadsEveryLogAndIsRepeatable()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "logs")).FullName;
        File.WriteAllLines(Path.Combine(logs, "output_log_2026-10-28_01-30-36.txt"),
        [
            Line("01:49:39", "[Behaviour] Entering Room: Crystal Garden"),
            Line("01:49:39", $"[Behaviour] Joining {World}:1~private(usr_x)"),
            Line("01:49:51", $"[Behaviour] Switching {Me} to avatar FaraFrillBlood"),
            Line("01:49:53", $"Loading Avatar Data:{Avatar1}"),
        ]);
        File.WriteAllLines(Path.Combine(logs, "output_log_2026-10-28_03-00-00.txt"),
        [
            Line("03:01:00", $"[Behaviour] Switching {Me} to avatar Other"),
        ]);
        File.WriteAllText(Path.Combine(logs, "ignored.txt"), "x");
        var history = new HistoryStore(Path.Combine(_root, "h", "history.jsonl"));

        var first = LogScanner.Scan(logs, Me, history, Jst);
        var again = LogScanner.Scan(logs, Me, history, Jst);

        Assert.Equal(new ScanResult(2, 3, 3), first);
        Assert.Equal(new ScanResult(2, 3, 0), again);
        Assert.Equal(new ScanResult(0, 0, 0), LogScanner.Scan(Path.Combine(_root, "none"), Me, history, Jst));   // フォルダが無ければ、何もしない
        Assert.Equal(["Crystal Garden", "FaraFrillBlood", "Other"], history.Load().Select(e => e.WorldName ?? e.AvatarName));
    }

    [Fact]
    public void ALogThatVRChatIsStillWritingCanBeRead()
    {
        var logs = Directory.CreateDirectory(Path.Combine(_root, "live")).FullName;
        var path = Path.Combine(logs, "output_log_2026-10-28_01-30-36.txt");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);       // VRChat が開いたまま
        var bytes = System.Text.Encoding.UTF8.GetBytes(Line("01:49:51", $"[Behaviour] Switching {Me} to avatar Mine") + "\n");
        writer.Write(bytes);
        writer.Flush();

        var result = LogScanner.Scan(logs, Me, new HistoryStore(Path.Combine(_root, "h2.jsonl")), Jst);

        Assert.Equal(1, result.Added);
    }
}
