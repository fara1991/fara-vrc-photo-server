using System.Text;

namespace FaraVrcPhotoServer;

/// <summary>
/// 履歴ファイル（history.jsonl。1 行 1 イベント）。追記のみ。壊れた行は読み飛ばす。
/// 同じイベント（<see cref="HistoryEvent.Key"/>）は二重に追記しない（起動のたびに全ログを読み直せる）。
/// </summary>
public sealed class HistoryStore(string path)
{
    public string Path { get; } = path;

    /// <summary>履歴の既定の場所（%LOCALAPPDATA%\FaraVrcPhotoServer\history.jsonl）。</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaraVrcPhotoServer", "history.jsonl");

    /// <summary>履歴を時刻順に読む。ファイルが無い・読めない行は、空・読み飛ばし。</summary>
    public IReadOnlyList<HistoryEvent> Load()
    {
        if (!File.Exists(Path)) return [];
        var events = new List<HistoryEvent>();
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (HistoryEvent.TryParse(line) is { } e) events.Add(e);
        return events.OrderBy(e => e.Time).ToList();
    }

    /// <summary>まだ履歴に無いイベントだけを追記する。追記した数を返す。</summary>
    public int Append(IEnumerable<HistoryEvent> events)
    {
        var known = Load().Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var fresh = new List<HistoryEvent>();
        foreach (var e in events)
            if (known.Add(e.Key)) fresh.Add(e);
        if (fresh.Count == 0) return 0;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var text = string.Concat(fresh.OrderBy(e => e.Time).Select(e => e.ToJsonLine() + "\n"));
        using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
        // 前回が改行の前に途切れていたら、行を分けてから書く
        if (stream.Length > 0 && !EndsWithNewline()) stream.WriteByte((byte)'\n');
        stream.Write(new UTF8Encoding(false).GetBytes(text));
        return fresh.Count;
    }

    private bool EndsWithNewline()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0) return true;
        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() == '\n';
    }

    /// <summary>
    /// 撮影日時のときに操作していたアバター: 撮影日時以前でいちばん新しい avatar イベント。
    /// <paramref name="maxAge"/>（既定は 24 時間）より前のものは使わない。無ければ null。
    /// </summary>
    public static HistoryEvent? AvatarAt(IReadOnlyList<HistoryEvent> history, DateTimeOffset taken, TimeSpan? maxAge = null)
    {
        var limit = maxAge ?? TimeSpan.FromHours(24);
        return history
            .Where(e => e.Type == HistoryEventTypes.Avatar && e.Time <= taken && taken - e.Time <= limit)
            .OrderByDescending(e => e.Time)
            .ThenByDescending(e => e.AvatarId is not null)                                                  // 同じ時刻なら、ID が分かっているほうを使う（ID なしの先書きと、ID つきの確定が両方ある）
            .FirstOrDefault();
    }
}
