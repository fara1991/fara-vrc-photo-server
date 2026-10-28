using System.Text;

namespace FaraVrcPhotoServer;

/// <summary>
/// VRChat のログのフォルダを、追記された分だけ読み続ける（<see cref="Poll"/> を定期的に呼ぶ）。
/// ファイルごとに、読み終えた位置と <see cref="LogParser"/> の状態を持つ。行の途中までしか書かれていなければ、次の回に続きを読む。
/// ログが短くなったとき（作り直し）は、先頭から読み直す。読むのは共有つき（VRChat の書き込みを邪魔しない）。
/// アバターの切り替えは、ID の行を待つあいだも、<b>ID なしで先に履歴に書く</b>（写真がその直後に撮られても、アバターが分かるように）。
/// </summary>
public sealed class LogTailer(string logFolder, string displayName, HistoryStore history, TimeZoneInfo? zone = null)
{
    private sealed class FileState(LogParser parser)
    {
        public long Position;
        public byte[] Partial = [];
        public LogParser Parser { get; } = parser;
    }

    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>新しく読んだ分を履歴に追記して、追記した件数を返す。</summary>
    public int Poll()
    {
        if (!Directory.Exists(logFolder)) return 0;
        var paths = Directory.EnumerateFiles(logFolder, "output_log_*.txt").ToList();
        foreach (var gone in _files.Keys.Where(k => !paths.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) _files.Remove(gone);

        var events = new List<HistoryEvent>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            if (!_files.TryGetValue(path, out var state)) _files[path] = state = new FileState(new LogParser(displayName, zone));
            try
            {
                ReadNew(path, state, events);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 読めないログ（消された・権限）は、次の回にまた試す
            }
            if (state.Parser.PendingAvatar() is { } pending) events.Add(pending);                              // ID を待っている切り替えも、先に書く
        }
        return history.Append(events);
    }

    private static void ReadNew(string path, FileState state, List<HistoryEvent> events)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < state.Position)                                                                       // 作り直された
        {
            state.Position = 0;
            state.Partial = [];
        }
        if (stream.Length == state.Position) return;
        stream.Seek(state.Position, SeekOrigin.Begin);
        var buffer = new byte[stream.Length - state.Position];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        state.Position += read;

        // 前回の途中までと、今読んだ分を、つなげて 1 つの配列にする（LINQ で 1 バイトずつ連結しない）
        byte[] data;
        if (state.Partial.Length == 0)
        {
            data = read == buffer.Length ? buffer : buffer[..read];
        }
        else
        {
            data = new byte[state.Partial.Length + read];
            state.Partial.CopyTo(data, 0);
            buffer.AsSpan(0, read).CopyTo(data.AsSpan(state.Partial.Length));
        }

        var start = 0;
        int newline;
        while ((newline = Array.IndexOf(data, (byte)'\n', start)) >= 0)
        {
            var line = Encoding.UTF8.GetString(data, start, newline - start).TrimEnd('\r');
            events.AddRange(state.Parser.Feed(line));
            start = newline + 1;
        }
        state.Partial = data.AsSpan(start).ToArray();                                                             // 行の途中までのもの
    }
}
