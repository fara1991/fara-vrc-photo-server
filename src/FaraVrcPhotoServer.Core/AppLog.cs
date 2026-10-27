using System.Text;

namespace FaraVrcPhotoServer;

/// <summary>
/// アプリの記録（app.log。エラーと、写真の処理の結果）。落ちたときの原因をたどるためのもの。表示名・ユーザー ID は書かない。
/// 書けなくても（権限・ディスク）、アプリは止めない。大きくなったら（256 KB）、古い分を app.log.old に移す（1 世代だけ）。
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    /// <summary>記録の置き場所。テストが差し替える。</summary>
    public static string Path { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaraVrcPhotoServer", "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
                if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes) File.Move(Path, Path + ".old", overwrite: true);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても、止めない
        }
    }

    public static void Write(string context, Exception e) => Write($"{context}: {e.GetType().Name}: {e.Message}");
}
