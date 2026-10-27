namespace FaraVrcPhotoServer;

/// <summary>写真への追記の結果。</summary>
public enum WriteOutcome
{
    /// <summary>追記した。</summary>
    Written,

    /// <summary>すでに faraxml の項目がある。何も変えていない。</summary>
    AlreadyTagged,

    /// <summary>PNG として読めない・追記先が見つからない・検査に通らない。何も変えていない。</summary>
    Invalid,
}

public sealed record WriteResult(WriteOutcome Outcome, string Message);

/// <summary>
/// 写真（PNG）の XMP に、アバターの情報を追記する（docs/spec.md の第 5 節）。元のファイルを壊さない手順:
/// <list type="number">
/// <item>元のファイルを読み、PNG として検査する（すべてのチャンクの CRC）。</item>
/// <item>新しい内容をメモリに作り、<b>もう一度読んで検査する</b>（画素のデータが元と同じ・ほかのチャンクが元と同じ・追記した内容が読める）。</item>
/// <item>（設定があれば）元のファイルを、控えのフォルダにコピーする。</item>
/// <item>同じフォルダに一時ファイルを書き、入れ替える（File.Replace）。更新日時は元のままにする。</item>
/// </list>
/// どの段階で失敗しても、元のファイルは変えない（一時ファイルは消す）。
/// </summary>
public static class PhotoXmpWriter
{
    public const string TempSuffix = ".faraxml.tmp";

    public static WriteResult Write(string path, FaraXmlInfo info, string? backupFolder = null)
    {
        byte[] original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WriteResult(WriteOutcome.Invalid, "写真を読めません: " + e.Message);
        }

        PngFile png;
        try
        {
            png = PngFile.Parse(original);
        }
        catch (InvalidDataException e)
        {
            return new WriteResult(WriteOutcome.Invalid, e.Message);
        }

        var chunks = png.Chunks.ToList();
        var index = chunks.FindIndex(c => c.Type == "iTXt" && FaraXml.ReadXmpChunk(c.Data) is not null);
        string newXmp;
        if (index >= 0)
        {
            var xmp = FaraXml.ReadXmpChunk(chunks[index].Data)!;
            if (FaraXml.Has(xmp)) return new WriteResult(WriteOutcome.AlreadyTagged, "すでに faraxml の項目があります");
            var added = FaraXml.AddTo(xmp, info);
            if (added is null) return new WriteResult(WriteOutcome.Invalid, "XMP の追記先（rdf:RDF）が見つかりません");
            newXmp = added;
            chunks[index] = new PngChunk("iTXt", FaraXml.BuildXmpChunk(newXmp, chunks[index].Data));
        }
        else
        {
            newXmp = FaraXml.AddTo(null, info)!;
            var firstPixels = chunks.FindIndex(c => c.Type == "IDAT");
            if (firstPixels < 0) return new WriteResult(WriteOutcome.Invalid, "画素のデータ（IDAT）が見つかりません");
            chunks.Insert(firstPixels, new PngChunk("iTXt", FaraXml.BuildXmpChunk(newXmp)));
        }

        var updated = new PngFile(chunks).ToBytes();
        if (Verify(png, updated, newXmp, info) is { } problem) return new WriteResult(WriteOutcome.Invalid, "書き込み前の検査に通りません: " + problem);

        var temp = path + TempSuffix;
        try
        {
            if (backupFolder is { Length: > 0 }) Backup(path, original, backupFolder);
            File.WriteAllBytes(temp, updated);
            var written = File.GetLastWriteTimeUtc(path);
            var created = File.GetCreationTimeUtc(path);
            File.Replace(temp, path, null);
            File.SetLastWriteTimeUtc(path, written);
            File.SetCreationTimeUtc(path, created);
            return new WriteResult(WriteOutcome.Written, "追記しました");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new WriteResult(WriteOutcome.Invalid, "書き込めません（元のファイルは変えていません）: " + e.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>新しい内容の検査。問題があれば理由、無ければ null。</summary>
    private static string? Verify(PngFile before, byte[] updatedBytes, string expectedXmp, FaraXmlInfo info)
    {
        PngFile after;
        try
        {
            after = PngFile.Parse(updatedBytes);
        }
        catch (InvalidDataException e)
        {
            return "新しい内容が PNG として読めません（" + e.Message + "）";
        }
        if (!before.PixelData().AsSpan().SequenceEqual(after.PixelData())) return "画素のデータが元と違います";

        // XMP のチャンク以外は、元と同じ（種類・本体・並び）
        bool IsXmp(PngChunk c) => c.Type == "iTXt" && FaraXml.ReadXmpChunk(c.Data) is not null;
        var others = before.Chunks.Where(c => !IsXmp(c)).ToList();
        var othersAfter = after.Chunks.Where(c => !IsXmp(c)).ToList();
        if (others.Count != othersAfter.Count) return "チャンクの数が合いません";
        for (var i = 0; i < others.Count; i++)
            if (others[i].Type != othersAfter[i].Type || !others[i].Data.AsSpan().SequenceEqual(othersAfter[i].Data)) return "XMP 以外のチャンクが元と違います";

        var xmpAfter = after.Chunks.Where(IsXmp).Select(c => FaraXml.ReadXmpChunk(c.Data)).SingleOrDefault();
        if (xmpAfter != expectedXmp) return "XMP の内容が想定と違います";
        var read = FaraXml.Read(xmpAfter);
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
}
