using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FaraVrcPhotoServer;

/// <summary>
/// ジャンクション（マウントポイント）・シンボリックリンクの、リンク先のパスを読む。リンクをたどらずに、リンクそのものの情報（再解析ポイント）だけを読む。
/// Windows は、管理者でない利用者が作ったジャンクションを「信頼されていないマウントポイント」とみなし、たどろうとするアプリによっては
/// 「パスをスキャンできません」と拒否する（ERROR_UNTRUSTED_MOUNT_POINT）。そのときも、リンク先のフォルダを直接見張れるようにするために使う。
/// </summary>
public static class ReparseTarget
{
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const uint ReparseTagSymlink = 0xA000000C;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileReadAttributes = 0x0080;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;

    /// <summary>リンク先のフォルダ。リンクでない・読めない・Windows でないときは null。</summary>
    public static string? TryGet(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var handle = CreateFile(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new byte[16 * 1024];
            if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero)) return null;
            return Parse(buffer.AsSpan(0, (int)returned).ToArray());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>再解析ポイントのデータ（REPARSE_DATA_BUFFER）から、リンク先のパスを取り出す。形が違えば null。</summary>
    public static string? Parse(byte[] data)
    {
        if (data.Length < 16) return null;
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan());
        // 本体の前に、タグ(4)・長さ(2)・予約(2) の 8 バイト。そのあと、4 つの (オフセット, 長さ)、シンボリックリンクだけフラグ(4) が続く
        int pathStart;
        if (tag == ReparseTagMountPoint) pathStart = 8 + 8;
        else if (tag == ReparseTagSymlink) pathStart = 8 + 8 + 4;
        else return null;
        if (data.Length < pathStart) return null;

        var substituteOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        var substituteLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        var printOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        var printLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));

        string? Read(int offset, int length) =>
            length > 0 && pathStart + offset + length <= data.Length ? Encoding.Unicode.GetString(data, pathStart + offset, length) : null;

        var target = Read(printOffset, printLength) ?? Read(substituteOffset, substituteLength);
        if (string.IsNullOrEmpty(target)) return null;
        if (target.StartsWith(@"\??\", StringComparison.Ordinal)) target = target[4..];                       // 内部の表記（\??\C:\…）の頭を取る
        return target.Length > 0 ? target : null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize, byte[] outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
