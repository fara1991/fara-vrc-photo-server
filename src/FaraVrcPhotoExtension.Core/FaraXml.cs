using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace FaraVrcPhotoExtension;

/// <summary>写真に追記するアバターの情報（docs/spec.md の第 5 節）。</summary>
public sealed record FaraXmlInfo(string AvatarName, string? AvatarId, string Source = "log");

/// <summary>
/// 写真の XMP（PNG の iTXt チャンク「XML:com.adobe.xmp」）に、独自の名前空間 faraxml の項目を足す・読む。
/// 足すときは、元の XMP の文字列は 1 文字も変えず、閉じタグ <c>&lt;/rdf:RDF&gt;</c> の直前に 1 つの <c>rdf:Description</c> を挿入するだけ。
/// すでに faraxml の項目がある写真は、何もしない（書き換えない）。XMP が無い写真は、最小の XMP を新しく作る。
/// </summary>
public static partial class FaraXml
{
    public const string Namespace = "http://fara-labs.com/ns/faraxml/1.0/";
    public const string Prefix = "faraxml";
    public const string XmpKeyword = "XML:com.adobe.xmp";
    public const string Version = "1";

    /// <summary>項目の値の上限（異常に長い名前を写真に入れない）。</summary>
    public const int MaxValueLength = 200;

    /// <summary>XMP の文字列に、faraxml の名前空間が使われているか。</summary>
    public static bool Has(string xmp) => xmp.Contains(Namespace, StringComparison.Ordinal);

    /// <summary>アバターの情報を足した XMP。すでにある（<see cref="Has"/>）・差し込み先が見つからないときは null。xmp が null なら、新しい XMP。</summary>
    public static string? AddTo(string? xmp, FaraXmlInfo info)
    {
        var description = BuildDescription(info);
        if (xmp is null)
        {
            return "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n  <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n"
                + description + "\n  </rdf:RDF>\n</x:xmpmeta>";
        }
        if (Has(xmp)) return null;
        var close = xmp.LastIndexOf("</rdf:RDF>", StringComparison.Ordinal);
        if (close < 0) return null;
        return xmp[..close] + description + "\n  " + xmp[close..];
    }

    /// <summary>XMP から faraxml の項目を読む（属性でも子要素でも。無ければ null）。</summary>
    public static FaraXmlInfo? Read(string xmp)
    {
        if (!Has(xmp)) return null;
        var name = Find(xmp, "AvatarName");
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new FaraXmlInfo(name, Find(xmp, "AvatarId"), Find(xmp, "Source") ?? "");
    }

    private static string? Find(string xmp, string local)
    {
        var attribute = Regex.Match(xmp, $@"\b{Prefix}:{local}=""(?<v>[^""]*)""");
        if (attribute.Success) return Unescape(attribute.Groups["v"].Value);
        var element = Regex.Match(xmp, $@"<{Prefix}:{local}>(?<v>[^<]*)</{Prefix}:{local}>");
        return element.Success ? Unescape(element.Groups["v"].Value) : null;
    }

    private static string Unescape(string value) =>
        value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    private static string BuildDescription(FaraXmlInfo info)
    {
        var builder = new StringBuilder();
        builder.Append($"    <rdf:Description xmlns:{Prefix}=\"{Namespace}\"");
        builder.Append($" {Prefix}:Version=\"{Version}\"");
        builder.Append($" {Prefix}:AvatarName=\"{Escape(Limit(info.AvatarName))}\"");
        if (!string.IsNullOrWhiteSpace(info.AvatarId)) builder.Append($" {Prefix}:AvatarId=\"{Escape(Limit(info.AvatarId))}\"");
        builder.Append($" {Prefix}:Source=\"{Escape(Limit(info.Source))}\"/>");
        return builder.ToString();
    }

    private static string Limit(string value)
    {
        // 制御文字は空白にして、長すぎるものは切る（XML に書けない文字を入れない）
        var clean = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return clean.Length <= MaxValueLength ? clean : clean[..MaxValueLength];
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? "";

    // ---------------------------------------------------------------- iTXt チャンク

    /// <summary>iTXt チャンクの本体から、XMP の文字列（キーワードが XMP のものだけ）。圧縮されていれば展開する。違えば null。</summary>
    public static string? ReadXmpChunk(byte[] data)
    {
        var parsed = ParseItxt(data);
        return parsed is { Keyword: XmpKeyword } ? parsed.Text : null;
    }

    /// <summary>XMP の文字列から、iTXt チャンクの本体（圧縮しない形）を作る。言語・翻訳つきキーワードは、元のものを引き継ぐ。</summary>
    public static byte[] BuildXmpChunk(string xmp, byte[]? original = null)
    {
        var parsed = original is null ? null : ParseItxt(original);
        using var stream = new MemoryStream();
        stream.Write(Encoding.Latin1.GetBytes(XmpKeyword));
        stream.WriteByte(0);
        stream.WriteByte(0);                                                                               // 圧縮しない
        stream.WriteByte(0);
        stream.Write(Encoding.UTF8.GetBytes(parsed?.Language ?? ""));
        stream.WriteByte(0);
        stream.Write(Encoding.UTF8.GetBytes(parsed?.Translated ?? ""));
        stream.WriteByte(0);
        stream.Write(new UTF8Encoding(false).GetBytes(xmp));
        return stream.ToArray();
    }

    private sealed record Itxt(string Keyword, string Language, string Translated, string Text);

    private static Itxt? ParseItxt(byte[] data)
    {
        var nul = Array.IndexOf(data, (byte)0);
        if (nul <= 0 || nul + 3 > data.Length) return null;
        var keyword = Encoding.Latin1.GetString(data, 0, nul);
        var compressed = data[nul + 1] == 1;
        var position = nul + 3;
        var languageEnd = Array.IndexOf(data, (byte)0, position);
        if (languageEnd < 0) return null;
        var translatedEnd = Array.IndexOf(data, (byte)0, languageEnd + 1);
        if (translatedEnd < 0) return null;
        var language = Encoding.UTF8.GetString(data, position, languageEnd - position);
        var translated = Encoding.UTF8.GetString(data, languageEnd + 1, translatedEnd - languageEnd - 1);
        var body = data.AsSpan(translatedEnd + 1).ToArray();
        if (compressed)
        {
            try
            {
                using var input = new MemoryStream(body);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                body = output.ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }
        return new Itxt(keyword, language, translated, Encoding.UTF8.GetString(body));
    }
}
