using System.Diagnostics;
using System.Text;
using FaraVrcPhotoServer;
using Xunit;

namespace FaraVrcPhotoServer.Core.Tests;

/// <summary>ジャンクションのリンク先の読み取り（リンクをたどれない環境でも、先のフォルダを直接見張れるように）。</summary>
public sealed class ReparseTargetTests : IDisposable
{
    private readonly string _root = TestEnv.NewFolder("fara-reparse");

    public ReparseTargetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // ジャンクションは、先に（先の中身をたどらずに）リンクだけを消す
        foreach (var dir in Directory.GetDirectories(_root))
            if (ReparseTarget.TryGet(dir) is not null) Directory.Delete(dir, recursive: false);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>マウントポイントの再解析データ（タグ・長さ・予約・4 つの数・パス）を作る。</summary>
    private static byte[] MountPoint(string substitute, string print)
    {
        var sub = Encoding.Unicode.GetBytes(substitute);
        var prt = Encoding.Unicode.GetBytes(print);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0xA0000003u);
        w.Write((ushort)(8 + sub.Length + 2 + prt.Length + 2));
        w.Write((ushort)0);
        w.Write((ushort)0); w.Write((ushort)sub.Length);
        w.Write((ushort)(sub.Length + 2)); w.Write((ushort)prt.Length);
        w.Write(sub); w.Write((ushort)0);
        w.Write(prt); w.Write((ushort)0);
        return ms.ToArray();
    }

    [Fact]
    public void ParseReadsThePrintNameOrTheSubstituteNameWithoutTheInternalPrefix()
    {
        Assert.Equal(@"F:\Pictures\VRChat", ReparseTarget.Parse(MountPoint(@"\??\F:\Pictures\VRChat", @"F:\Pictures\VRChat")));
        Assert.Equal(@"F:\Pictures\VRChat", ReparseTarget.Parse(MountPoint(@"\??\F:\Pictures\VRChat", "")));            // 表示名が空なら、内部の表記から
        Assert.Null(ReparseTarget.Parse([1, 2, 3]));
        Assert.Null(ReparseTarget.Parse(new byte[32]));                                                                   // タグが違う
    }

    [Fact]
    public void ARealJunctionGivesItsTargetAndAPlainFolderOrAFileGivesNothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var link = Path.Combine(_root, "link");
        using (var cmd = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            cmd.StandardOutput.ReadToEnd();
            cmd.WaitForExit();
        }
        Assert.True(Directory.Exists(link), "ジャンクションを作れなかったので、この確認はできません（mklink /J が使えるか確かめてください）");

        Assert.Equal(target, ReparseTarget.TryGet(link), ignoreCase: true);
        Assert.Null(ReparseTarget.TryGet(target));
        Assert.Null(ReparseTarget.TryGet(Path.Combine(_root, "missing")));
    }

    [Fact]
    public async Task AWatcherOnAJunctionSeesNewPhotosInTheTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Directory.CreateDirectory(Path.Combine(_root, "t2")).FullName;
        var link = Path.Combine(_root, "l2");
        using (var cmd = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            cmd.StandardOutput.ReadToEnd();
            cmd.WaitForExit();
        }
        Assert.True(Directory.Exists(link), "ジャンクションを作れなかったので、この確認はできません（mklink /J が使えるか確かめてください）");
        var seen = new List<string>();
        using var watcher = new PhotoWatcher(link, p => { lock (seen) seen.Add(p); });

        await File.WriteAllBytesAsync(Path.Combine(target, "VRChat_2026-10-28_01-55-05.000_8x6.png"), TestPng.MakePng());
        for (var i = 0; i < 100 && seen.Count == 0; i++) await Task.Delay(100);

        lock (seen) Assert.Single(seen);
    }
}
