using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FaraVrcPhotoServer;

/// <summary>
/// 写真のフォルダ（サブフォルダを含む）の新しい PNG を見張る。ファイルができた直後は VRChat が書き込み中のことがあるので、
/// <b>ファイルの大きさが 1 秒間変わらなくなってから</b>（最大 30 秒待つ）、コールバックに渡す。
/// 自分で書き換えた写真（一時ファイル・入れ替えのイベント）は、しばらく無視する。コールバックは 1 枚ずつ順に呼ぶ。
/// </summary>
public sealed class PhotoWatcher : IDisposable
{
    public static readonly TimeSpan StableFor = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IgnoreAfterWrite = TimeSpan.FromSeconds(5);

    private readonly FileSystemWatcher _watcher;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _onPhoto;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public PhotoWatcher(string folder, Action<string> onPhoto)
    {
        _onPhoto = onPhoto;
        _watcher = StartWatching(folder, Enqueue);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// フォルダの見張りを始める。始められなかったとき（フォルダがリンクで、先のドライブが使えない・権限など）は、
    /// リンクの先のフォルダでもやり直す。どちらもだめなら、原因を調べた文つきの例外にする（.NET は、どんな失敗でも「ディレクトリを読めません」と言うため）。
    /// </summary>
    private static FileSystemWatcher StartWatching(string folder, Action<string> enqueue)
    {
        var candidates = new List<string> { folder };
        // リンク（ジャンクション）の先も、あとの候補にする。Windows は、管理者でない人が作ったジャンクションを「信頼されていないマウントポイント」として、
        // たどれないことがある。そのときも、リンクをたどらずに先のパスを読んで、直接見張れる
        if (ReparseTarget.TryGet(folder) is { } linked && Directory.Exists(linked)) candidates.Add(linked);

        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(candidate, "*.png")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                };
                watcher.Created += (_, e) => enqueue(e.FullPath);
                watcher.Renamed += (_, e) => enqueue(e.FullPath);
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                watcher?.Dispose();
                failures.Add($"{candidate}（{Probe(candidate)}）");
            }
        }
        throw new IOException("写真のフォルダを見張れません: " + string.Join(" / ", failures));
    }

    /// <summary>見張れなかったフォルダの様子（開けるか・中を読めるか）。原因をたどる手がかり。</summary>
    private static string Probe(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return "フォルダが見つかりません";
            _ = Directory.EnumerateFileSystemEntries(folder).FirstOrDefault();
            return "フォルダは読めるが、変更の監視を始められません";
        }
        catch (UnauthorizedAccessException)
        {
            return "アクセスが拒否されました";
        }
        catch (IOException e)
        {
            return e.Message;
        }
    }

    /// <summary>自分で書き換えた写真を、しばらく無視する（入れ替えで起きるイベントを、また処理しないため）。</summary>
    public void IgnoreForAWhile(string path) => _recent[path] = DateTime.UtcNow;

    private void Enqueue(string path)
    {
        if (path.EndsWith(PhotoXmpWriter.TempSuffix, StringComparison.OrdinalIgnoreCase)) return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
        _queue.Writer.TryWrite(path);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                if (_recent.TryGetValue(path, out var at) && DateTime.UtcNow - at < IgnoreAfterWrite) continue;
                if (!await WaitUntilStableAsync(path, _cts.Token)) continue;
                try
                {
                    _onPhoto(path);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"写真の処理で例外: {e.GetType().Name}: {e.Message}");           // 見張りを止めない
                }
                _recent[path] = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            // 終了
        }
    }

    /// <summary>ファイルの大きさが <see cref="StableFor"/> のあいだ変わらず、読み取りで開けるまで待つ。時間内に安定しなければ false。</summary>
    public static async Task<bool> WaitUntilStableAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GiveUpAfter;
        long lastSize = -1;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var size = new FileInfo(path).Length;
                if (size != lastSize)
                {
                    lastSize = size;
                    stableSince = DateTime.UtcNow;
                }
                else if (size > 0 && DateTime.UtcNow - stableSince >= StableFor)
                {
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);     // 書き込み中なら、ここで開けない
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lastSize = -1;                                                                                 // まだ使えない（作成直後・書き込み中・消えた）
                stableSince = DateTime.UtcNow;
            }
            await Task.Delay(Poll, ct);
        }
        return false;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
