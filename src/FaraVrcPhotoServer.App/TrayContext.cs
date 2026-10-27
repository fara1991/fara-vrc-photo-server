using System.Diagnostics;
using System.Drawing.Drawing2D;
using FaraVrcPhotoServer;

namespace FaraVrcPhotoServer.App;

/// <summary>
/// タスクトレイのアイコンとメニュー。状態（見張り中・設定が足りない）を出し、設定の画面・最近の処理・フォルダを開く・終了を持つ。
/// 設定を保存したら、常駐の本体（<see cref="PhotoServerService"/>）を作り直す。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly SettingsStore _store;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly Icon _appIcon = IconArt.CreateTrayIcon();
    private readonly Control _ui = new();                                                                     // 別のスレッドからの通知を、画面のスレッドに移すための、見えない部品
    private PhotoServerService _service = null!;
    private SettingsForm? _settingsForm;
    private RecentForm? _recentForm;

    public TrayContext(SettingsStore store, bool openSettingsAtStart)
    {
        _store = store;
        _ = _ui.Handle;                                                                                       // ハンドルを作っておく（BeginInvoke に要る）
        _statusItem = new ToolStripMenuItem("状態") { Enabled = false, ForeColor = Theme.AccentDark };
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new Theme.MenuColors()) { RoundedEdges = false }, Font = Theme.BaseFont };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("設定(&S)…", null, (_, _) => OpenSettings());
        menu.Items.Add("最近の処理(&R)…", null, (_, _) => OpenRecent());
        menu.Items.Add("写真のフォルダを開く(&P)", null, (_, _) => OpenFolder(_store.Load().EffectivePhotoFolder));
        menu.Items.Add("履歴のフォルダを開く(&H)", null, (_, _) => OpenFolder(Path.GetDirectoryName(_store.Load().EffectiveHistoryPath)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了(&X)", null, (_, _) => ExitThread());

        _icon = new NotifyIcon { Icon = _appIcon, ContextMenuStrip = menu, Visible = true, Text = AppInfo.Name };
        _icon.DoubleClick += (_, _) => OpenSettings();

        _service = StartService();
        if (openSettingsAtStart) OpenSettings();
    }

    private PhotoServerService StartService()
    {
        var service = new PhotoServerService(_store.Load());
        service.Changed += () =>
        {
            if (_ui.IsDisposed) return;
            try
            {
                _ui.BeginInvoke(() => { if (_service is null || ReferenceEquals(service, _service)) Refresh(service); });
            }
            catch (InvalidOperationException)
            {
                // 終了のとき
            }
        };
        service.Start();
        Refresh(service);
        return service;
    }

    private void Refresh(PhotoServerService service)
    {
        var status = service.Status;
        _statusItem.Text = status.Length > 60 ? status[..57] + "…" : status;
        var last = service.Recent.LastOrDefault();
        var tip = last is null ? $"{AppInfo.Name}\n{status}" : $"{AppInfo.Name}\n{last}";
        _icon.Text = tip.Length > 120 ? tip[..117] + "…" : tip;                                              // NotifyIcon の文字数の上限（127）
    }

    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_store.Load() with { AutoStart = AutoStart.IsEnabled() });          // 自動起動は、実際の登録（インストーラーで入れた分も）に合わせる
        _settingsForm.Saved += settings =>
        {
            _store.Save(settings);
            AutoStart.Apply(settings.AutoStart);
            var old = _service;
            _service = StartService();
            old.Dispose();
        };
        _settingsForm.Show();
    }

    private void OpenRecent()
    {
        if (_recentForm is { IsDisposed: false })
        {
            _recentForm.Activate();
            return;
        }
        _recentForm = new RecentForm(() => _service.Recent, () => _service.Status);
        _recentForm.Show();
    }

    private static void OpenFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service.Dispose();
            _ui.Dispose();
            _icon.Dispose();
            _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
