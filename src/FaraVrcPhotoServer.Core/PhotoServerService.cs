namespace FaraVrcPhotoServer;

/// <summary>
/// 常駐ソフトの本体（画面なし）: ログを読み続けて履歴に残し、写真フォルダを見張って、新しい写真にアバターを追記する。
/// 設定が足りない・おかしいときは動かさず、理由を <see cref="Status"/> に出す。画面側は、設定を変えたら作り直す。
/// 状態と最近の処理（<see cref="Recent"/>）は、画面（トレイ）に出す文。表示名は出さない。
/// <b>写真のフォルダを見張り始められなかったときも、落ちない</b>: 理由を状態に出し、一定の間隔でやり直す（ログの読み続けは、そのあいだも動く）。
/// </summary>
public sealed class PhotoServerService : IDisposable
{
    private const int MaxRecent = 50;

    private readonly AppSettings _settings;
    private readonly TimeZoneInfo? _zone;
    private readonly Func<string, Action<string>, IDisposable> _startWatching;
    private readonly object _gate = new();
    private readonly List<string> _recent = [];
    private HistoryStore? _history;
    private LogTailer? _tailer;
    private PhotoProcessor? _processor;
    private IDisposable? _watcher;
    private Timer? _timer;
    private DateTime _lastWatchAttempt;
    private string _status = "停止中";
    private bool _disposed;

    /// <param name="startWatching">写真のフォルダの見張りを始める（テストが、失敗する版に差し替える）。</param>
    public PhotoServerService(AppSettings settings, TimeZoneInfo? zone = null, Func<string, Action<string>, IDisposable>? startWatching = null)
    {
        _settings = settings;
        _zone = zone;
        // 設定の控えのフォルダの中は、見張らない（控えに、また追記しないため）
        var excluded = settings.BackupFolder is { Length: > 0 } b ? b : null;
        _startWatching = startWatching ?? ((folder, onPhoto) => new PhotoWatcher(folder, onPhoto, excluded));
    }

    /// <summary>ログを読む間隔。</summary>
    public TimeSpan LogPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>写真のフォルダを見張れなかったときの、やり直しの間隔。</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>状態の変化（状態の文・最近の処理が変わったとき）。呼ばれるスレッドは決まっていない。</summary>
    public event Action? Changed;

    /// <summary>今の状態（例: 見張っています／VRChat の表示名が未設定です）。</summary>
    public string Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>写真のフォルダを見張っているか。</summary>
    public bool IsRunning => _watcher is not null;

    /// <summary>最近の処理（新しいものが最後）。</summary>
    public IReadOnlyList<string> Recent
    {
        get { lock (_gate) return _recent.ToList(); }
    }

    /// <summary>始める。設定に問題があれば動かさず、false（理由は <see cref="Status"/>）。見張りを始められなくても true（やり直す）。</summary>
    public bool Start()
    {
        // 前回の途中で止まった写真の書き込みを、先に戻す（表示名の設定が無くても、写真は戻す）
        foreach (var message in PendingJournal.RecoverAll())
        {
            AppLog.Write(message);
            Note(message);
        }

        // 表示名が無いときだけ、動かさない。写真・ログのフォルダが無いときは、見つかるまで待つ（ログは、待つあいだも読み続ける）
        var problems = _settings.StartProblems();
        if (problems.Count > 0)
        {
            SetStatus(string.Join(" / ", problems));
            return false;
        }
        _history = new HistoryStore(_settings.EffectiveHistoryPath);
        _tailer = new LogTailer(_settings.EffectiveLogFolder, _settings.DisplayName, _history, _zone);
        try
        {
            Note($"ログを読みました（履歴に {_tailer.Poll()} 件を追記）");                                       // 今あるログを、先頭から読む（履歴に無い分だけ追記）
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("ログの読み込み", e);
            Note("ログを読めませんでした（あとでやり直します）: " + e.Message);
        }

        _processor = new PhotoProcessor(() =>
        {
            Poll();                                                                                           // 写真の処理の前に、ログの最新の分を読む
            return _history.Load();
        }, new PhotoOptions(_settings.WriteXmp, _settings.BackupFolder is { Length: > 0 } b ? b : null, _settings.BackupOriginals), _zone);

        TryStartWatching();
        _timer = new Timer(_ => Tick(), null, LogPollInterval, LogPollInterval);
        return true;
    }

    /// <summary>写真のフォルダの見張りを始める。失敗しても落とさず、状態に理由を出す。</summary>
    private void TryStartWatching()
    {
        _lastWatchAttempt = DateTime.UtcNow;
        var folder = _settings.EffectivePhotoFolder;
        if (!Directory.Exists(folder))
        {
            SetStatus($"写真のフォルダがありません（{(int)RetryInterval.TotalSeconds} 秒ごとに確かめます）: {folder}");
            return;
        }
        try
        {
            IDisposable? watcher = null;
            watcher = _startWatching(folder, path =>
            {
                var result = _processor!.Process(path);
                (watcher as PhotoWatcher)?.IgnoreForAWhile(path);
                var line = $"{Path.GetFileName(path)}: {Describe(result)}";
                AppLog.Write(line);
                Note(line);
            });
            if (watcher is PhotoWatcher photoWatcher) photoWatcher.Problem += Note;                             // 写真ごとの失敗・見張りの作り直しを、最近の処理に出す
            lock (_gate)
            {
                if (_disposed)
                {
                    watcher.Dispose();                                                                          // 止めたあとに始まったものは、残さない（Tick との競合）
                    return;
                }
                _watcher = watcher;
            }
            SetStatus("サーバ起動中です" + (Directory.Exists(_settings.EffectiveLogFolder) ? "" : "（VRChat のログのフォルダがありません。見つかるまで待ちます）"));
            AppLog.Write("写真のフォルダの見張りを始めました");
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            AppLog.Write("写真のフォルダの見張り", e);
            SetStatus($"写真のフォルダを見張れません（{(int)RetryInterval.TotalSeconds} 秒ごとにやり直します）: {e.Message}");
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        Poll();
        if (_watcher is null && DateTime.UtcNow - _lastWatchAttempt >= RetryInterval) TryStartWatching();
    }

    private void Poll()
    {
        try
        {
            lock (_gate)
            {
                _tailer?.Poll();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 履歴・ログに触れなかった回は、次の回にまた試す
        }
    }

    /// <summary>結果の文。失敗のときは、写真が今どうなっているか（変えていない・元に戻した・戻せなかった）を、処理の側が書いた文で出す。</summary>
    private static string Describe(PhotoResult r) => r.Outcome switch
    {
        PhotoOutcome.Written => $"アバターを追記しました（{r.Info?.AvatarName}）" + (r.Warning is null ? "" : "。" + r.Warning),
        PhotoOutcome.AlreadyTagged => "すでに追記済みです",
        PhotoOutcome.NoAvatar => "アバターが分からないので、何も書きませんでした",
        PhotoOutcome.WriteDisabled => $"設定で写真には書きません（{r.Info?.AvatarName}）",
        PhotoOutcome.NotPhoto => "VRChat の写真ではないので、何もしませんでした",
        _ => "追記できませんでした: " + r.Message,
    };

    private void Note(string message)
    {
        lock (_gate)
        {
            _recent.Add($"{DateTime.Now:HH:mm:ss} {message}");
            if (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    private void SetStatus(string status)
    {
        lock (_gate) _status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        IDisposable? watcher;
        lock (_gate)
        {
            _disposed = true;                                                                                    // 以後は、Tick も見張りを始めない
            watcher = _watcher;
            _watcher = null;
            _status = "停止中";
        }
        _timer?.Dispose();
        _timer = null;
        watcher?.Dispose();                                                                                      // 書き込みの終わりを待つ（PhotoWatcher.Dispose）
    }
}
