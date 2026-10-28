namespace FaraVrcPhotoServer;

/// <summary>写真への追記の結果。</summary>
public enum WriteOutcome
{
    /// <summary>追記した。</summary>
    Written,

    /// <summary>すでに faraxml の項目がある。何も変えていない。</summary>
    AlreadyTagged,

    /// <summary>PNG として読めない・追記先が見つからない・検査に通らない・書けない。Message に、写真が今どうなっているか（変えていない・元に戻した・戻せなかった）を書く。</summary>
    Invalid,
}

/// <summary>
/// 写真への追記の結果。Message は、写真が今どうなっているかを含む文。
/// Warning は、追記は済んだが、あとの後始末（更新日時を戻す）だけ失敗したときの文（追記は成功として扱う）。
/// </summary>
public sealed record WriteResult(WriteOutcome Outcome, string Message, string? Warning = null);

/// <summary>
/// 写真（PNG）の XMP に、アバターの情報を追記する（docs/spec.md の第 5 節）。元のファイルを壊さない手順:
/// <list type="number">
/// <item>元のファイルを読み、PNG として検査する（すべてのチャンクの CRC）。書き込みの途中かもしれないときは、少し待って読み直す（3 回まで）。</item>
/// <item>新しい内容をメモリに作り、<b>もう一度読んで検査する</b>（画素のデータが元と同じ・ほかのチャンクが元と同じ・追記した内容が読める）。</item>
/// <item>控えを残さないとき（既定）: 写真のファイルに<b>直接</b>書く。書く前に、元の内容と記録を「退避」（%LOCALAPPDATA%\FaraVrcPhotoServer\pending）に残す。
///       書いたあと読み直して確かめ、合わなければ元の内容に戻す。確かめ終えたら退避を消す。
///       直接開けないとき（ほかのプログラムが開いている・読み取り専用）は、次の方法にする。</item>
/// <item>控えを残すとき（または直接開けないとき）: 元のファイルを控えのフォルダにコピーしてから、同じフォルダに一時ファイルを書き、入れ替える（File.Replace）。更新日時は元のままにする。</item>
/// </list>
/// 電源断・強制終了で直接書き込みの途中で止まったときは、退避が残る。次回の起動で <see cref="PendingJournal.RecoverAll"/> が、写真を検査して、壊れていれば退避から元に戻す。
/// 入れ替えのときは、入れ替えの前か後のどちらかの状態になる（一時ファイルは消す）。
/// </summary>
public static class PhotoXmpWriter
{
    public const string TempSuffix = ".faraxml.tmp";

    private const int ParseAttempts = 3;
    private static readonly TimeSpan ParseRetryDelay = TimeSpan.FromSeconds(1);

    // テストが、途中で失敗する場面を作るための差し込み口（普段は何もしない）
    internal static Action<string>? AfterDirectWriteForTests { get; set; }
    internal static Action<string>? AfterReplaceForTests { get; set; }

    public static WriteResult Write(string path, FaraXmlInfo info, string? backupFolder = null)
    {
        byte[] original;
        PngFile png;
        try
        {
            (original, png) = ReadPng(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Invalid("写真を読めません（写真は変えていません）: " + e.Message);
        }
        catch (Exception e) when (e is InvalidDataException or PngTruncatedException)
        {
            return Invalid(e.Message + "（写真は変えていません）");
        }

        var chunks = png.Chunks.ToList();
        // XMP のチャンクが 2 つ以上ある写真は、どちらに書けばよいか決められないので、対応しない（例外にはしない）
        if (chunks.Count(IsXmpChunk) > 1) return Invalid("XMP のチャンクが 2 つあるので、対応していません（写真は変えていません）");
        var index = chunks.FindIndex(IsXmpChunk);
        string newXmp;
        if (index >= 0)
        {
            var xmp = FaraXml.ReadXmpChunk(chunks[index].Data)!;
            if (FaraXml.Has(xmp)) return new WriteResult(WriteOutcome.AlreadyTagged, "すでに faraxml の項目があります");
            var added = FaraXml.AddTo(xmp, info);
            if (added is null) return Invalid("XMP の追記先（rdf:RDF）が見つかりません（写真は変えていません）");
            newXmp = added;
            chunks[index] = new PngChunk("iTXt", FaraXml.BuildXmpChunk(newXmp, chunks[index].Data));
        }
        else
        {
            newXmp = FaraXml.AddTo(null, info)!;
            var firstPixels = chunks.FindIndex(c => c.Type == "IDAT");
            if (firstPixels < 0) return Invalid("画素のデータ（IDAT）が見つかりません（写真は変えていません）");
            chunks.Insert(firstPixels, new PngChunk("iTXt", FaraXml.BuildXmpChunk(newXmp)));
        }

        var updated = new PngFile(chunks).ToBytes();
        if (Verify(png, updated, newXmp, info) is { } problem) return Invalid("書き込み前の検査に通りません（写真は変えていません）: " + problem);

        // 控えを残さないとき（既定）は、撮った写真のファイルに、直接タグを書き込む（ファイルそのものを残す）。
        // 直接書き込めないとき（ほかのプログラムが開いている・読み取り専用など）は、下の「一時ファイル → 入れ替え」でやってみる。
        if (backupFolder is not { Length: > 0 })
        {
            var direct = TryWriteInPlace(path, original, updated);
            if (direct is not null) return direct;
        }
        return WriteByReplacing(path, original, updated, backupFolder);
    }

    /// <summary>
    /// 写真のファイルに、新しい内容を直接書く（先頭から書いて、長さを合わせる）。更新日時・作成日時は元のまま。
    /// 書く前に退避を作り、書いたあと読み直して確かめる。合わなければ、退避から元の内容に戻す。
    /// 開けなかったとき・退避を作れなかったとき（まだ何も書いていない）は null（別の方法でやり直す）。
    /// </summary>
    private static WriteResult? TryWriteInPlace(string path, byte[] original, byte[] updated)
    {
        lock (PendingJournal.Gate)
        {
            DateTime written;
            DateTime created;
            FileStream stream;
            try
            {
                written = File.GetLastWriteTimeUtc(path);
                created = File.GetCreationTimeUtc(path);
                stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);                  // 書いている間は、ほかから開けない
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;                                                                                      // 開けない。まだ何も変えていない
            }

            var journal = PendingJournal.TryBegin(path, original, updated, written, created);
            if (journal is null)
            {
                stream.Dispose();
                return null;                                                                                      // 退避が作れないときは、直接は書かない
            }

            try
            {
                using (stream)
                {
                    stream.Write(updated);
                    stream.SetLength(updated.Length);
                    stream.Flush(flushToDisk: true);
                }
            }
            catch (IOException e)
            {
                return Undo(path, original, written, created, journal, "書き込みの途中で失敗しました（" + e.Message + "）");
            }

            AfterDirectWriteForTests?.Invoke(path);
            string? reason = null;
            byte[]? readBack = null;
            try
            {
                readBack = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                reason = "書いたあとの確認に失敗しました（" + e.Message + "）";
            }
            if (reason is null && !readBack!.AsSpan().SequenceEqual(updated)) reason = "書いた内容が合いません";
            if (reason is not null) return Undo(path, original, written, created, journal, reason);

            string? warning = null;
            try
            {
                File.SetLastWriteTimeUtc(path, written);
                File.SetCreationTimeUtc(path, created);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warning = "写真の更新日時は元に戻せませんでした: " + e.Message;
            }
            journal.Complete();
            return new WriteResult(WriteOutcome.Written, "追記しました（写真に直接）", warning);
        }
    }

    /// <summary>直接書いた写真を、元の内容に戻す。戻せたら退避を消す。戻せなければ退避を残し、次の起動で戻す。</summary>
    private static WriteResult Undo(string path, byte[] original, DateTime written, DateTime created, PendingJournal journal, string reason)
    {
        if (PendingJournal.RestoreFile(path, original, written, created))
        {
            journal.Complete();
            return Invalid($"{reason}。元の内容に戻しました（写真は変えていません）");
        }
        return Invalid($"{reason}。元の内容に戻せませんでした（退避を残しています。次の起動で元に戻します）");
    }

    /// <summary>控えを残すとき（または直接開けないとき）: 一時ファイルを書き、入れ替える。入れ替えの前までに失敗すれば、元のファイルは変えていない。</summary>
    private static WriteResult WriteByReplacing(string path, byte[] original, byte[] updated, string? backupFolder)
    {
        var temp = path + TempSuffix;
        DateTime written;
        DateTime created;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
            created = File.GetCreationTimeUtc(path);
            if (backupFolder is { Length: > 0 }) Backup(path, original, backupFolder);
            File.WriteAllBytes(temp, updated);
            File.Replace(temp, path, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Invalid("書き込めません（写真は変えていません）: " + e.Message);
        }
        finally
        {
            TryDelete(temp);
        }

        // 入れ替えは済んでいる。ここから先の失敗は、タグの有無に関係しない（更新日時だけの警告）
        try
        {
            AfterReplaceForTests?.Invoke(path);
            File.SetLastWriteTimeUtc(path, written);
            File.SetCreationTimeUtc(path, created);
            return new WriteResult(WriteOutcome.Written, "追記しました");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WriteResult(WriteOutcome.Written, "追記しました", "更新日時は元に戻せませんでした: " + e.Message);
        }
    }

    /// <summary>写真を読んで PNG として検査する。書き込みの途中で切れているらしいときは、少し待って読み直す。</summary>
    private static (byte[] Bytes, PngFile Png) ReadPng(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                return (bytes, PngFile.Parse(bytes));
            }
            catch (PngTruncatedException) when (attempt < ParseAttempts)
            {
                Thread.Sleep(ParseRetryDelay);
            }
        }
    }

    private static WriteResult Invalid(string message) => new(WriteOutcome.Invalid, message);

    private static bool IsXmpChunk(PngChunk c) => c.Type == "iTXt" && FaraXml.ReadXmpChunk(c.Data) is not null;

    /// <summary>新しい内容の検査。問題があれば理由、無ければ null。</summary>
    private static string? Verify(PngFile before, byte[] updatedBytes, string expectedXmp, FaraXmlInfo info)
    {
        PngFile after;
        try
        {
            after = PngFile.Parse(updatedBytes);
        }
        catch (Exception e) when (e is InvalidDataException or PngTruncatedException)
        {
            return "新しい内容が PNG として読めません（" + e.Message + "）";
        }
        if (!before.PixelData().AsSpan().SequenceEqual(after.PixelData())) return "画素のデータが元と違います";

        // XMP のチャンク以外は、元と同じ（種類・本体・並び）
        var others = before.Chunks.Where(c => !IsXmpChunk(c)).ToList();
        var othersAfter = after.Chunks.Where(c => !IsXmpChunk(c)).ToList();
        if (others.Count != othersAfter.Count) return "チャンクの数が合いません";
        for (var i = 0; i < others.Count; i++)
            if (others[i].Type != othersAfter[i].Type || !others[i].Data.AsSpan().SequenceEqual(othersAfter[i].Data)) return "XMP 以外のチャンクが元と違います";

        var xmps = after.Chunks.Where(IsXmpChunk).ToList();
        if (xmps.Count != 1) return "XMP の項目の数が想定と違います";
        var xmpAfter = FaraXml.ReadXmpChunk(xmps[0].Data);
        if (xmpAfter != expectedXmp) return "XMP の内容が想定と違います";
        var read = FaraXml.Read(xmpAfter!);
        if (read is null || !string.Equals(read.AvatarName, Clean(info.AvatarName), StringComparison.Ordinal)) return "追記した内容を読み戻せません";
        return null;
    }

    private static string Clean(string value)
    {
        var clean = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= FaraXml.MaxValueLength ? clean : clean[..FaraXml.MaxValueLength];
    }

    /// <summary>書く前の元のファイルを、控えのフォルダへコピーする（同じ名前があれば、番号を付ける。上書きしない）。</summary>
    private static void Backup(string path, byte[] original, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(path);
        var target = Path.Combine(folder, name + ".png");
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{name} ({n}).png");
        File.WriteAllBytes(target, original);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("一時ファイルの片付け", e);
        }
    }
}
