using System.Text;

namespace FaraVrcPhotoExtension;

/// <summary>ログのフォルダを読んで、履歴に追記した結果。</summary>
public sealed record ScanResult(int Files, int Events, int Added);

/// <summary>
/// VRChat のログのフォルダ（output_log_*.txt）をすべて読み、イベントを履歴に追記する（起動時の読み込み）。
/// ログは VRChat が書き込み中なので、読み取りだけ・他の書き込みを許す共有で開く（VRChat の書き込みを邪魔しない）。
/// </summary>
public static class LogScanner
{
    /// <summary>VRChat のログのフォルダの既定の場所。</summary>
    public static string DefaultLogFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "VRChat", "VRChat");

    public static ScanResult Scan(string logFolder, string displayName, HistoryStore history, TimeZoneInfo? zone = null)
    {
        if (!Directory.Exists(logFolder)) return new ScanResult(0, 0, 0);
        var files = Directory.EnumerateFiles(logFolder, "output_log_*.txt").Order(StringComparer.Ordinal).ToList();
        var events = new List<HistoryEvent>();
        foreach (var file in files)
        {
            var parser = new LogParser(displayName, zone);                                                  // ログのファイルごとに状態を捨てる
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (reader.ReadLine() is { } line) events.AddRange(parser.Feed(line));
                events.AddRange(parser.Flush());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 読めないログ（消された・権限）は、読み飛ばす
            }
        }
        return new ScanResult(files.Count, events.Count, history.Append(events));
    }
}
