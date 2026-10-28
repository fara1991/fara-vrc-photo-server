using System.Security.Cryptography;
using System.Text.Json;

namespace FaraVrcPhotoServer;

/// <summary>
/// 写真をその場で書き換えている最中の、元の内容の退避（既定は %LOCALAPPDATA%\FaraVrcPhotoServer\pending）。
/// 書く前に、元のバイト列と「どの写真か（元のパス）」の記録を残し、書いて確かめ終えたら消す。
/// 電源断・強制終了で途中で止まったときは、次回の起動の <see cref="RecoverAll"/> で、写真を検査し、壊れていれば退避から元に戻す。
/// 退避は、ユーザーの写真のフォルダにも backup にも残さない。
/// </summary>
public sealed class PendingJournal
{
    private const string RecordSuffix = ".json";
    private const string OriginalSuffix = ".orig";

    /// <summary>退避の置き場所。テストが差し替える。</summary>
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaraVrcPhotoServer", "pending");

    /// <summary>直接書き込みと、起動時の復元を、同時に動かさないための鍵。</summary>
    internal static readonly object Gate = new();

    /// <summary>記録（どの写真か・書く前と書いたあとの内容の指紋・元の更新日時）。記録を書き終えたとき、退避が有効になる。</summary>
    internal sealed record Record(string PhotoPath, string OriginalSha256, string UpdatedSha256, DateTime LastWriteUtc, DateTime CreationUtc);

    private readonly string _recordPath;
    private readonly string _originalPath;

    private PendingJournal(string recordPath, string originalPath)
    {
        _recordPath = recordPath;
        _originalPath = originalPath;
    }

    /// <summary>
    /// 退避を作る（元のバイト列を書いて、記録を書く）。作れなければ null（写真には、まだ何も書いていない）。
    /// </summary>
    public static PendingJournal? TryBegin(string photoPath, byte[] original, byte[] updated, DateTime lastWriteUtc, DateTime creationUtc)
    {
        var id = Guid.NewGuid().ToString("N");
        var originalPath = Path.Combine(Folder, id + OriginalSuffix);
        var recordPath = Path.Combine(Folder, id + RecordSuffix);
        var recordTemp = recordPath + ".tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            WriteAndFlush(originalPath, original);
            WriteAndFlush(recordTemp, JsonSerializer.SerializeToUtf8Bytes(new Record(photoPath, Sha256(original), Sha256(updated), lastWriteUtc, creationUtc)));
            File.Move(recordTemp, recordPath, overwrite: true);
            return new PendingJournal(recordPath, originalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("退避の作成", e);
            TryDelete(originalPath);
            TryDelete(recordTemp);
            TryDelete(recordPath);
            return null;
        }
    }

    /// <summary>退避を消す（記録を先に消す）。写真の書き換えが済んで確かめたあとに呼ぶ。</summary>
    public void Complete()
    {
        TryDelete(_recordPath);
        TryDelete(_originalPath);
    }

    /// <summary>
    /// 前回の途中で止まった退避を、起動のときに片付ける。写真を検査して、書きかけなら元の内容に戻し、そうでなければ退避を消す。
    /// 戻せなかったもの（写真が開けない・書けない・見つからない）は、退避を残して、次の起動でもう一度試す。結果は、人が読む文で返す。
    /// </summary>
    public static IReadOnlyList<string> RecoverAll()
    {
        var messages = new List<string>();
        lock (Gate)
        {
            if (!Directory.Exists(Folder)) return messages;
            try
            {
                foreach (var recordPath in Directory.EnumerateFiles(Folder, "*" + RecordSuffix).Order(StringComparer.Ordinal).ToList())
                    messages.Add(RecoverOne(recordPath));
                // 記録の無い退避（記録を書く前に止まった）は、写真には何も書いていないので消す
                foreach (var orphan in Directory.EnumerateFiles(Folder, "*" + OriginalSuffix).ToList())
                    if (!File.Exists(Path.ChangeExtension(orphan, RecordSuffix))) TryDelete(orphan);
                foreach (var temp in Directory.EnumerateFiles(Folder, "*" + RecordSuffix + ".tmp").ToList()) TryDelete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("退避の確認", e);
                messages.Add("退避の確認に失敗しました（次の起動でもう一度試します）: " + e.Message);
            }
        }
        return messages;
    }

    private static string RecoverOne(string recordPath)
    {
        var originalPath = Path.ChangeExtension(recordPath, OriginalSuffix);
        Record? record;
        try
        {
            record = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(recordPath));
        }
        catch (JsonException)
        {
            // 記録の中身が壊れていれば、どの写真か分からない。写真には触れず、退避だけ消す
            TryDelete(recordPath);
            TryDelete(originalPath);
            return "退避の記録が壊れていたので、消しました（写真は変えていません）";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "退避の記録を読めないので、次の起動でもう一度試します: " + e.Message;
        }
        if (record is null)
        {
            TryDelete(recordPath);
            TryDelete(originalPath);
            return "退避の記録が空だったので、消しました（写真は変えていません）";
        }

        var photo = record.PhotoPath;
        if (!File.Exists(originalPath))
        {
            TryDelete(recordPath);
            return "退避の元の写真が無いので、戻せませんでした（写真は変えていません）: " + photo;
        }
        byte[] original;
        try
        {
            original = File.ReadAllBytes(originalPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "退避を読めないので、次の起動でもう一度試します: " + photo + "（" + e.Message + "）";
        }
        if (!IsValidPng(original))
        {
            // 退避そのものが壊れていれば、戻すと写真をさらに壊すので、何もしない
            TryDelete(recordPath);
            TryDelete(originalPath);
            return "退避が壊れていたので、戻しませんでした（写真は変えていません）: " + photo;
        }

        byte[]? current;
        try
        {
            if (!File.Exists(photo))
                return "写真が見つからないので、退避は残しました（次の起動でもう一度確かめます）: " + photo;
            current = File.ReadAllBytes(photo);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "写真を開けないので、次の起動でもう一度試します: " + photo + "（" + e.Message + "）";
        }

        var hash = Sha256(current);
        if (hash == record.OriginalSha256)
        {
            TryDelete(recordPath);
            TryDelete(originalPath);
            return "前回の書き込みは、写真に届いていなかったので、写真は元のままでした: " + photo;
        }
        if (hash == record.UpdatedSha256)
        {
            TryDelete(recordPath);
            TryDelete(originalPath);
            return "前回の書き込みは最後まで済んでいたので、そのままにしました: " + photo;
        }
        if (IsValidPng(current))
        {
            // 壊れていない写真（書き終えたあとに、ほかで変えられた）は、戻すと変更を消すので、そのままにする
            TryDelete(recordPath);
            TryDelete(originalPath);
            return "前回の記録がありましたが、写真は壊れていなかったので、そのままにしました: " + photo;
        }

        if (RestoreFile(photo, original, record.LastWriteUtc, record.CreationUtc))
        {
            TryDelete(recordPath);
            TryDelete(originalPath);
            return "途中で止まった写真を、元に戻しました: " + photo;
        }
        return "途中で止まった写真を元に戻せませんでした（次の起動で、もう一度試します）: " + photo;
    }

    /// <summary>
    /// 写真の内容を、指定のバイト列にする（先頭から書いて長さを合わせる）。書いたあと読み直して同じか確かめ、更新日時を戻す。
    /// できたら true。開けない・書けない・読み直して違うときは false（写真は、そのときの状態のまま）。
    /// </summary>
    internal static bool RestoreFile(string path, byte[] bytes, DateTime lastWriteUtc, DateTime creationUtc)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.SetLength(bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("写真を元に戻す", e);
            return false;
        }
        try
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            File.SetCreationTimeUtc(path, creationUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("写真の更新日時を戻す", e);
        }
        return true;
    }

    internal static bool IsValidPng(byte[] bytes)
    {
        try
        {
            PngFile.Parse(bytes);
            return true;
        }
        catch (Exception e) when (e is InvalidDataException or PngTruncatedException)
        {
            return false;
        }
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void WriteAndFlush(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("退避の片付け", e);
        }
    }
}
