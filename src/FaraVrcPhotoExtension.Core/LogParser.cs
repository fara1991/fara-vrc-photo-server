using System.Globalization;
using System.Text.RegularExpressions;

namespace FaraVrcPhotoExtension;

/// <summary>
/// VRChat のログ（output_log_*.txt）の行から、ワールドとアバターのイベントを取り出す（docs/spec.md の第 3 節）。
/// 1 行ずつ <see cref="Feed"/> に渡し、最後に <see cref="Flush"/> を呼ぶ。行の形が合わないものは、何も出さずに読み飛ばす（失敗させない）。
/// <list type="bullet">
/// <item>アバターは、<b>設定の自分の表示名</b>の切り替えの行だけ。表示名は、履歴にも出力にも入れない。</item>
/// <item>アバターの ID は、切り替えの行のあと <see cref="IdWindow"/> 以内の最初の <c>Loading Avatar Data:avtr_…</c> の行。無ければ ID なし。</item>
/// <item>ワールドは、ワールド名（Entering Room）と ID（Joining）。インスタンスの部分（ユーザー ID を含む）は捨てる。</item>
/// </list>
/// </summary>
public sealed partial class LogParser(string displayName, TimeZoneInfo? zone = null)
{
    /// <summary>切り替えの行から、アバターの ID の行までの、最大の間隔。</summary>
    public static readonly TimeSpan IdWindow = TimeSpan.FromSeconds(30);

    /// <summary>「Entering Room」の行から、ワールドの ID の行までの、最大の間隔。</summary>
    private static readonly TimeSpan RoomWindow = TimeSpan.FromSeconds(30);

    private const string SwitchingPrefix = "[Behaviour] Switching ";
    private const string SwitchingInfix = " to avatar ";

    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;
    private readonly string _displayName = displayName.Trim();

    private (DateTimeOffset Time, string Name)? _pendingSwitch;
    private (DateTimeOffset Time, string Name)? _pendingRoom;

    [GeneratedRegex(@"^(?<t>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}) +\w+ +- +(?<body>.*)$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"^\[Behaviour\] Entering Room: (?<name>.+)$")]
    private static partial Regex EnteringRoom();

    [GeneratedRegex(@"^\[Behaviour\] Joining (?<id>wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?::|$)")]
    private static partial Regex JoiningWorld();

    [GeneratedRegex(@"^Loading Avatar Data:(?<id>avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*$")]
    private static partial Regex LoadingAvatar();

    /// <summary>ログの 1 行を渡す。取り出したイベントを（無ければ空で）返す。</summary>
    public IReadOnlyList<HistoryEvent> Feed(string line)
    {
        var events = new List<HistoryEvent>();
        if (LogLine().Match(line) is not { Success: true } m) return events;                              // 行頭が時刻でない行（続きの行など）
        if (!DateTime.TryParseExact(m.Groups["t"].Value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return events;
        var time = new DateTimeOffset(local, _zone.GetUtcOffset(local));
        var body = m.Groups["body"].Value.TrimEnd();

        // 待っていた ID が来なかった（間隔を過ぎた）切り替えは、ID なしで確定する
        if (_pendingSwitch is { } stale && time - stale.Time > IdWindow) FlushSwitch(events);

        if (EnteringRoom().Match(body) is { Success: true } room)
        {
            _pendingRoom = (time, room.Groups["name"].Value.Trim());
        }
        else if (JoiningWorld().Match(body) is { Success: true } join)
        {
            var name = _pendingRoom is { } r && time - r.Time <= RoomWindow ? r.Name : null;
            events.Add(HistoryEvent.World(time, join.Groups["id"].Value, name));
            _pendingRoom = null;
        }
        else if (TrySwitching(body) is { } avatarName)
        {
            FlushSwitch(events);                                                                              // 前の切り替えは、ID なしで確定
            _pendingSwitch = (time, avatarName);
        }
        else if (LoadingAvatar().Match(body) is { Success: true } loading && _pendingSwitch is { } pending && time - pending.Time <= IdWindow)
        {
            events.Add(HistoryEvent.Avatar(pending.Time, pending.Name, loading.Groups["id"].Value));
            _pendingSwitch = null;
        }
        return events;
    }

    /// <summary>ログの終わり。ID を待っている切り替えを、ID なしで確定する。</summary>
    public IReadOnlyList<HistoryEvent> Flush()
    {
        var events = new List<HistoryEvent>();
        FlushSwitch(events);
        _pendingRoom = null;
        return events;
    }

    private void FlushSwitch(List<HistoryEvent> events)
    {
        if (_pendingSwitch is { } p) events.Add(HistoryEvent.Avatar(p.Time, p.Name, null));
        _pendingSwitch = null;
    }

    /// <summary>「Switching ＜自分の表示名＞ to avatar ＜アバター名＞」の行なら、アバター名。自分の行でなければ null。</summary>
    private string? TrySwitching(string body)
    {
        if (_displayName.Length == 0) return null;                                                          // 表示名が空のあいだは、何も記録しない
        var prefix = SwitchingPrefix + _displayName + SwitchingInfix;
        if (!body.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var name = body[prefix.Length..].Trim();
        return name.Length == 0 || name.Length > 200 ? null : name;
    }
}
