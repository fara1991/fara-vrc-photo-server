using FaraVrcPhotoServer;

namespace FaraVrcPhotoServer.App;

/// <summary>最近の処理の一覧（新しいものが上）。開いているあいだ、1 秒ごとに読み直す。</summary>
internal sealed class RecentForm : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ItemHeight = 22 };
    private readonly Label _status = new() { Dock = DockStyle.Top, Padding = new Padding(16, 8, 8, 8), AutoSize = false, Height = 34, BackColor = Theme.Panel, ForeColor = Theme.AccentDark };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Func<IReadOnlyList<string>> _recent;
    private readonly Func<string> _currentStatus;

    public RecentForm(Func<IReadOnlyList<string>> recent, Func<string> status)
    {
        _recent = recent;
        _currentStatus = status;
        Text = AppInfo.Name + " の最近の処理";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(700, 420);
        Theme.Apply(this);
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(Theme.Header(AppInfo.Name, "最近の処理（新しいものが上）"));
        _timer.Tick += (_, _) => Reload();
        _timer.Start();
        Reload();
    }

    private void Reload()
    {
        _status.Text = "状態: " + _currentStatus();
        var items = _recent().Reverse().ToArray();
        if (_list.Items.Count == items.Length && items.Length > 0 && Equals(_list.Items[0], items[0])) return;   // 変わっていなければ、触らない
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Items.AddRange(items);
        _list.EndUpdate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
