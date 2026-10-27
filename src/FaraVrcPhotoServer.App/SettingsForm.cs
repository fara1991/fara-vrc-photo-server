using FaraVrcPhotoServer;

namespace FaraVrcPhotoServer.App;

/// <summary>設定の画面（docs/spec.md の第 7 節）。保存したときだけ <see cref="Saved"/> が起きる。空のフォルダ・ファイルの欄は、既定を使う。</summary>
internal sealed class SettingsForm : Form
{
    private readonly TextBox _name = Theme.Input();
    private AppSettings _original;
    private readonly TextBox _history = Theme.Input();
    private readonly CheckBox _writeXmp = Theme.Check("写真にアバターの情報を追記する（オフなら、履歴だけに残す）");
    private readonly CheckBox _backupOriginals = Theme.Check("元の写真を、写真と同じ場所の backup フォルダに残す");
    private readonly CheckBox _autoStart = Theme.Check("Windows の起動時に自動で起動する");
    private readonly Label _message = new() { AutoSize = true, ForeColor = Theme.Danger };

    public SettingsForm(AppSettings settings)
    {
        Text = AppInfo.Name + " の設定";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(700, 450);
        Theme.Apply(this);

        _name.Text = settings.DisplayName;
        _original = settings;
        _history.Text = settings.EffectiveHistoryPath;                                                       // 初期値（既定の場所）を入れておく
        _writeXmp.Checked = settings.WriteXmp;
        _backupOriginals.Checked = settings.BackupOriginals;
        _autoStart.Checked = settings.AutoStart;

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(16, 12, 16, 12), AutoSize = false, BackColor = Color.White };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        AddRow(table, "VRChat の表示名", _name, null, "ログから自分の行を選ぶためにだけ使います。履歴・写真には書きません。");
        AddRow(table, "履歴ファイル", _history, null, "撮影時のアバターの記録を残すファイルです。ふつうは、このままにしてください。");
        table.Controls.Add(_writeXmp, 1, table.RowCount);
        table.SetColumnSpan(_writeXmp, 2);
        table.RowCount++;
        table.Controls.Add(_backupOriginals, 1, table.RowCount);
        table.SetColumnSpan(_backupOriginals, 2);
        table.RowCount++;
        table.Controls.Add(new Label { Text = "タグを埋め込んだ写真は、元の場所にそのまま置き、書き換える前の写真を backup フォルダに移します（容量が増えます）。", AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(22, 0, 0, 6) }, 1, table.RowCount);
        table.RowCount++;
        table.Controls.Add(_autoStart, 1, table.RowCount);
        table.SetColumnSpan(_autoStart, 2);
        table.RowCount++;
        table.Controls.Add(_message, 1, table.RowCount);
        table.SetColumnSpan(_message, 2);
        table.RowCount++;

        var save = Theme.Primary("保存(&S)");
        var cancel = Theme.Secondary("閉じる(&C)");
        cancel.Click += (_, _) => Close();                                                                       // 別のウィンドウとして開いているので、自分で閉じる
        save.Click += (_, _) => Save();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52, Padding = new Padding(12, 8, 12, 8), BackColor = Theme.Panel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(table);
        Controls.Add(buttons);
        Controls.Add(Theme.Header(AppInfo.Name, "VRChat で写真を撮ったときのアバターを、写真に自動で記録します"));
    }

    /// <summary>保存したとき（検査に通った設定）。</summary>
    public event Action<AppSettings>? Saved;

    private static void AddRow(TableLayoutPanel table, string label, TextBox box, Action? browse, string hint)
    {
        var row = table.RowCount;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        table.Controls.Add(box, 1, row);
        if (browse is not null)
        {
            var button = Theme.Secondary("選ぶ…");
            button.Anchor = AnchorStyles.Left;
            button.Click += (_, _) => browse();
            table.Controls.Add(button, 2, row);
        }
        table.RowCount++;
        var note = new Label { Text = hint, AutoSize = true, ForeColor = Theme.Muted, Padding = new Padding(0, 0, 0, 6) };
        table.Controls.Add(note, 1, table.RowCount);
        table.RowCount++;
    }

    /// <summary>履歴ファイルの欄が既定の場所のままなら、空（= 既定に従う）で保存する。変えたときだけ、その場所を保存する。</summary>
    private string HistoryPathToSave()
    {
        var typed = _history.Text.Trim();
        return typed.Length == 0 || string.Equals(typed, HistoryStore.DefaultPath, StringComparison.OrdinalIgnoreCase) ? "" : typed;
    }

    private void Save()
    {
        var settings = _original with                                                                          // 画面に出さない項目は、元のまま
        {
            DisplayName = _name.Text.Trim(),
            HistoryPath = HistoryPathToSave(),
            WriteXmp = _writeXmp.Checked,
            BackupOriginals = _backupOriginals.Checked,
            AutoStart = _autoStart.Checked,
        };
        if (string.IsNullOrWhiteSpace(settings.DisplayName))
        {
            _message.Text = "VRChat の表示名を入れてください（空のあいだは、何も記録しません）。";
            return;
        }
        // 保存はできるが、足りない点（フォルダが無い）は知らせる。トレイの状態にも出る
        var problems = settings.Problems();
        _message.ForeColor = problems.Count == 0 ? Theme.Good : Theme.Danger;
        _message.Text = problems.Count == 0 ? "保存しました。" : "保存しました。ただし: " + string.Join(" / ", problems);
        try
        {
            Saved?.Invoke(settings);
            _original = settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Write("設定の保存", e);
            _message.ForeColor = Theme.Danger;
            _message.Text = "保存できませんでした: " + e.Message;                                            // 落とさず、画面に出す
        }
    }
}
