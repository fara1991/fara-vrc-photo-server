using System.Collections.Concurrent;
using System.Threading.Channels;

namespace FaraVrcPhotoServer;

/// <summary>
/// 写真のフォルダ（サブフォルダを含む）の新しい PNG を見張る。ファイルができた直後は VRChat が書き込み中のことがあるので、
/// <b>ファイルの大きさが 1 秒間変わらなくなってから</b>（最大 30 秒待つ）、コールバックに渡す。コールバックは 1 枚ずつ順に呼ぶ。
/// 自分で書き換えた写真（一時ファイル・入れ替えのイベント）は、しばらく無視する。
/// 見張りが壊れた（<see cref="FileSystemWatcher.Error"/>）ときは、見張りを作り直し、見張りを始めてからの写真を読み直して、取りこぼしを拾う。
/// backup という名前のフォルダと、設定の控えのフォルダの中の写真は、見ない（控えに、また追記しないため）。
/// 失敗（処理の例外・待ち切れなかった写真・見張りの作り直し）は、<see cref="Problem"/> で知らせ、記録（app.log）にも残す。
/// </summary>
public sealed class PhotoWatcher : IDisposable
{
    public static readonly TimeSpan StableFor = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(30);

    /// <summary>止めるとき、写真の書き込みの終わりを待つ上限。</summary>
    public static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IgnoreAfterWrite = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecreateEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RescanMargin = TimeSpan.FromMinutes(2);
    private const int InternalBufferSize = 64 * 1024;

    private readonly string _folder;
    private readonly string? _excludedPrefix;
    private readonly Action<string> _onPhoto;
    private readonly DateTime _since = DateTime.UtcNow;
    private readonly object _watchGate = new();
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private FileSystemWatcher? _watcher;
    private string _root = "";
    private bool _disposed;

    /// <summary>知らせ（写真ごとの失敗・見張りの作り直し）。文は、ファイル名を含む 1 行。呼ばれるスレッドは決まっていない。</summary>
    public event Action<string>? Problem;

    /// <param name="folder">見張るフォルダ。</param>
    /// <param name="onPhoto">新しい写真（書き終わったもの）を、1 枚ずつ渡す。</param>
    /// <param name="excludedFolder">見ない控えのフォルダ（設定の控えのフォルダ。空なら無し）。</param>
    public PhotoWatcher(string folder, Action<string> onPhoto, string? excludedFolder = null)
    {
        _folder = folder;
        _onPhoto = onPhoto;
        if (excludedFolder is { Length: > 0 } ex)
            _excludedPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ex)) + Path.DirectorySeparatorChar;
        _watcher = StartWatching(folder);
        _worker = Task.Run(RunAsync);
    }

    /// <summary>
    /// フォルダの見張りを始める。始められなかったとき（フォルダがリンクで、先のドライブが使えない・権限など）は、
    /// リンクの先のフォルダでもやり直す。どちらもだめなら、原因を調べた文つきの例外にする（.NET は、どんな失敗でも「ディレクトリを読めません」と言うため）。
    /// </summary>
    private FileSystemWatcher StartWatching(string folder)
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
                var created = new FileSystemWatcher(candidate, "*.png")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = InternalBufferSize,                                                  // 大きいほど、書き込みが多いときに取りこぼしにくい
                };
                watcher = created;
                created.Created += (_, e) => Enqueue(e.FullPath);
                created.Renamed += (_, e) => Enqueue(e.FullPath);
                created.Error += (_, e) => OnError(created, e.GetException());
                created.EnableRaisingEvents = true;
                _root = candidate;
                return created;
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

    /// <summary>見張っているフォルダの下の、backup という名前のフォルダの中か（見張っているフォルダ自身の名前や、その上の階層は見ない）。</summary>
    private bool IsInBackupFolder(string path)
    {
        var relative = Path.GetRelativePath(_root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return false;
        var directories = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[..^1];
        return directories.Any(d => string.Equals(d, PhotoOptions.BackupDirectoryName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>設定の控えのフォルダの中か。</summary>
    private bool IsInExcludedFolder(string path) =>
        _excludedPrefix is not null && Path.GetFullPath(path).StartsWith(_excludedPrefix, StringComparison.OrdinalIgnoreCase);

    private void Enqueue(string path)
    {
        if (path.EndsWith(PhotoXmpWriter.TempSuffix, StringComparison.OrdinalIgnoreCase)) return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
        if (IsInBackupFolder(path) || IsInExcludedFolder(path)) return;                                        // 控えは見ない。見ると、控えにまた追記してしまう
        _queue.Writer.TryWrite(path);
    }

    /// <summary>見張りが壊れた。その見張りを捨てて、作り直す（別のスレッドで）。</summary>
    private void OnError(FileSystemWatcher failed, Exception e)
    {
        lock (_watchGate)
        {
            if (_disposed || !ReferenceEquals(failed, _watcher)) return;                                       // 止めたあとや、作り直したあとのエラーは無視
            _watcher = null;
        }
        AppLog.Write($"写真のフォルダの見張りでエラー: {e.GetType().Name}: {e.Message}");
        _ = Task.Run(() => RestartAsync(failed));
    }

    private async Task RestartAsync(FileSystemWatcher failed)
    {
        try
        {
            failed.EnableRaisingEvents = false;
            failed.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            AppLog.Write("古い見張りの片付け", e);
        }

        while (!_cts.IsCancellationRequested)
        {
            if (TryStartAgain())
            {
                try
                {
                    RescanSinceWatchStarted();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write("写真のフォルダの読み直し", e);
                }
                Report($"{_folder}: 見張りを作り直しました（写真を読み直します）");
                return;
            }
            try
            {
                await Task.Delay(RecreateEvery, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool TryStartAgain()
    {
        lock (_watchGate)
        {
            if (_disposed) return false;
            try
            {
                _watcher = StartWatching(_folder);
                return true;
            }
            catch (IOException e)
            {
                AppLog.Write("見張りの作り直し", e);
                return false;
            }
        }
    }

    /// <summary>見張りを始めた時刻より少し前からの写真を、すべて読み直して、まだ処理していないものを渡す（取りこぼしを拾うため）。</summary>
    private void RescanSinceWatchStarted()
    {
        var since = _since - RescanMargin;
        foreach (var path in Directory.EnumerateFiles(_root, "*.png", SearchOption.AllDirectories).ToList())
            if (File.GetLastWriteTimeUtc(path) >= since) Enqueue(path);
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var path in _queue.Reader.ReadAllAsync(_cts.Token))
            {
                if (_recent.TryGetValue(path, out var at) && DateTime.UtcNow - at < IgnoreAfterWrite) continue;
                if (!await WaitUntilStableAsync(path, _cts.Token))
                {
                    Report(Path.GetFileName(path) + ": " + (File.Exists(path)
                        ? $"{GiveUpAfter.TotalSeconds:0} 秒たっても書き終わらなかったので、処理しませんでした（写真は変えていません）"
                        : "ファイルが無くなったので、処理しませんでした"));
                    continue;
                }
                try
                {
                    _onPhoto(path);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Report($"{Path.GetFileName(path)}: 処理中に例外が起きました（{e.GetType().Name}: {e.Message}）");     // 見張りは止めない
                }
                _recent[path] = DateTime.UtcNow;
            }
        }
        catch (OperationCanceledException)
        {
            // 終了
        }
    }

    /// <summary>知らせる。記録にも残す。知らせる側の失敗で、見張りは止めない。</summary>
    private void Report(string message)
    {
        AppLog.Write(message);
        try
        {
            Problem?.Invoke(message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            AppLog.Write("知らせの処理", e);
        }
    }

    /// <summary>ファイルの大きさが <see cref="StableFor"/> のあいだ変わらず、読み取りで開けるまで待つ。時間内に安定しなければ false。ファイルが消えたら、すぐ false。</summary>
    public static async Task<bool> WaitUntilStableAsync(string path, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + GiveUpAfter;
        long lastSize = -1;
        var stableSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;                                                              // 消えた。待っても、もう来ない
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

    /// <summary>テスト用: 見張りが壊れた（Error）ことにする。</summary>
    internal void RaiseErrorForTests()
    {
        FileSystemWatcher? current;
        lock (_watchGate) current = _watcher;
        if (current is not null) OnError(current, new IOException("テスト用のエラー"));
    }

    /// <summary>
    /// 止める。写真を書いているあいだ（<c>_onPhoto</c> の中）は、終わるまで待つ（上限は <see cref="ExitGrace"/>）。
    /// 途中で止めると、写真が書きかけになる。待ち切れなければ、次の起動で退避から検査して戻す。
    /// </summary>
    public void Dispose()
    {
        lock (_watchGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_watcher is { } watcher)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watcher = null;
        }
        _queue.Writer.TryComplete();
        _cts.Cancel();
        bool finished;
        try
        {
            finished = _worker.Wait(ExitGrace);
        }
        catch (AggregateException)
        {
            finished = true;
        }
        if (!finished) AppLog.Write("写真の処理が終わるのを待ちきれませんでした（書き込みの途中かもしれません。次の起動で、途中の写真を検査して戻します）");
    }
}
