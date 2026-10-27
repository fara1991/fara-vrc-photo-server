using FaraVrcPhotoServer;

namespace FaraVrcPhotoServer.App;

internal static class Program
{
    /// <summary>
    /// 起動。同じ PC で 2 つ動かさない（2 つ目は、何もせず終わる）。設定が空（初回）のときは、設定の画面を開く。
    /// <c>--settings</c> を付けると、設定の画面を開く（確認用）。
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        // 実行ファイルのアイコン（app.ico）を作り直す（開発用。画面は出さない）
        if (args.Length == 2 && args[0] == "--write-icon")
        {
            IconArt.WriteIco(args[1]);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\FaraVrcPhotoServer", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        // 想定外の例外でも、アプリを落とさず（JIT デバッグの画面を出さず）、記録して知らせる
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("想定外の例外", e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString()));
        var store = new SettingsStore(SettingsStore.DefaultPath);
        using var tray = new TrayContext(store, openSettingsAtStart: args.Contains("--settings") || string.IsNullOrWhiteSpace(store.Load().DisplayName));
        Application.Run(tray);
    }

    private static void Report(Exception e)
    {
        AppLog.Write("画面の処理で例外", e);
        MessageBox.Show(
            $"処理に失敗しました: {e.Message}\n\n詳しい記録は、%LOCALAPPDATA%\\FaraVrcPhotoServer\\app.log にあります。",
            AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
