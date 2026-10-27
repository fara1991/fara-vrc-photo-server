namespace FaraVrcPhotoServer;

/// <summary>
/// 常駐ソフトの本体（画面なし）: ログを読み続けて履歴に残し、写真フォルダを見張って、新しい写真にアバターを追記する。
/// 設定が足りない・おかしいときは動かさず、理由を <see cref="Status"/> に出す。画面側は、設定を変えたら作り直す。
/// 状態と最近の処理（<see cref="Recent"/>）は、画面（トレイ）に出す文。表示名は出さない。
/// </summary>
public sealed class PhotoServerService : IDisposable
{
    public static readonly TimeSpan LogPollInterval = TimeSpan.FromSeconds(2);
    private const int MaxRecent = 50;

    private readonly AppSettings _settings;
    private readonly TimeZoneInfo? _zone;
    private readonly object _gate = new();
    private readonly List<string> _recent = [];
    private HistoryStore? _history;
    private LogTailer? _tailer;
    private PhotoWatcher? _watcher;
    private Timer? _timer;
    private string _status = "停止中";

    public PhotoServerService(AppSettings settings, TimeZoneInfo? zone = null)
    {
        _settings = settings;
        _zone = zone;
    }

    /// <summary>状態の変化（状態の文・最近の処理が変わったとき）。呼ばれるスレッドは決まっていない。</summary>
    public event Action? Changed;

    /// <summary>今の状態（例: 見張っています／VRChat の表示名が未設定です）。</summary>
    public string Status
    {
        get { lock (_gate) return _status; }
    }

    public bool IsRunning => _watcher is not null;

    /// <summary>最近の処理（新しいものが最後）。</summary>
    public IReadOnlyList<string> Recent
    {
        get { lock (_gate) return _recent.ToList(); }
    }

    /// <summary>始める。設定に問題があれば動かさず、false（理由は <see cref="Status"/>）。</summary>
    public bool Start()
    {
        var problems = _settings.Problems();
        if (problems.Count > 0)
        {
            SetStatus(string.Join(" / ", problems));
            return false;
        }
        _history = new HistoryStore(_settings.EffectiveHistoryPath);
        _tailer = new LogTailer(_settings.EffectiveLogFolder, _settings.DisplayName, _history, _zone);
        var first = _tailer.Poll();                                                                           // 今あるログを、先頭から読む（履歴に無い分だけ追記）
        Note($"ログを読みました（履歴に {first} 件を追記）");

        var processor = new PhotoProcessor(() =>
        {
            Poll();                                                                                           // 写真の処理の前に、ログの最新の分を読む
            return _history.Load();
        }, new PhotoOptions(_settings.WriteXmp, _settings.BackupFolder is { Length: > 0 } b ? b : null), _zone);
        _watcher = new PhotoWatcher(_settings.EffectivePhotoFolder, path =>
        {
            var result = processor.Process(path);
            _watcher?.IgnoreForAWhile(path);
            Note($"{Path.GetFileName(path)}: {Describe(result)}");
        });
        _timer = new Timer(_ => Poll(), null, LogPollInterval, LogPollInterval);
        SetStatus("写真のフォルダを見張っています");
        return true;
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

    private static string Describe(PhotoResult r) => r.Outcome switch
    {
        PhotoOutcome.Written => $"アバターを追記しました（{r.Info?.AvatarName}）",
        PhotoOutcome.AlreadyTagged => "すでに追記済みです",
        PhotoOutcome.NoAvatar => "アバターが分からないので、何も書きませんでした",
        PhotoOutcome.WriteDisabled => $"設定で写真には書きません（{r.Info?.AvatarName}）",
        PhotoOutcome.NotPhoto => "VRChat の写真ではないので、何もしませんでした",
        _ => "追記できませんでした（写真は変えていません）: " + r.Message,
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
        _timer?.Dispose();
        _watcher?.Dispose();
        _timer = null;
        _watcher = null;
        lock (_gate) _status = "停止中";
    }
}
