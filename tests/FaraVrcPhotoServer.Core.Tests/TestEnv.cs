using System.Runtime.CompilerServices;
using FaraVrcPhotoServer;
using Xunit;

// テストは、静的な置き場所（記録・退避）を共有するので、1 つずつ順に動かす
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FaraVrcPhotoServer.Core.Tests;

/// <summary>
/// テストの一時フォルダ・記録（app.log）・退避（pending）を、本物の置き場所（%LOCALAPPDATA%\FaraVrcPhotoServer）から分ける。
/// 一時フォルダは、環境変数 FARA_TEST_TEMP があればその下（無ければ Path.GetTempPath()。Windows では TMP / TEMP に従う）。
/// </summary>
internal static class TestEnv
{
    private static readonly string TempRoot =
        Environment.GetEnvironmentVariable("FARA_TEST_TEMP") is { Length: > 0 } t ? t : Path.GetTempPath();

    /// <summary>テストごとの一時フォルダを作る（名前に、接頭語と乱数を付ける）。</summary>
    public static string NewFolder(string prefix)
    {
        var path = Path.Combine(TempRoot, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>テストの既定の置き場所。個々のテストは、自分のフォルダに差し替える。</summary>
    [ModuleInitializer]
    internal static void RedirectSharedFiles()
    {
        var root = Path.Combine(TempRoot, "fara-test-module-" + Guid.NewGuid().ToString("N")[..8]);
        AppLog.Path = Path.Combine(root, "app.log");
        PendingJournal.Folder = Path.Combine(root, "pending");
    }
}
