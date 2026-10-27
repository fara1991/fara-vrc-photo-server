using System.Globalization;
using System.Text.RegularExpressions;

namespace FaraVrcPhotoExtension;

/// <summary>写真の処理の設定（docs/spec.md の第 7 節）。</summary>
public sealed record PhotoOptions(bool WriteXmp = true, string? BackupFolder = null);

/// <summary>1 枚の写真の処理の結果。</summary>
public enum PhotoOutcome
{
    /// <summary>XMP に追記した。</summary>
    Written,

    /// <summary>すでに faraxml の項目があるので、何もしなかった。</summary>
    AlreadyTagged,

    /// <summary>撮影日時の直前のアバターが、履歴に無い。何も書かない。</summary>
    NoAvatar,

    /// <summary>VRChat の写真のファイル名ではない。</summary>
    NotPhoto,

    /// <summary>設定で、写真には書かない（履歴だけ）。</summary>
    WriteDisabled,

    /// <summary>PNG として読めない・書き込めないなど。元のファイルは変えていない。</summary>
    Failed,
}

public sealed record PhotoResult(PhotoOutcome Outcome, string Message, FaraXmlInfo? Info = null);

/// <summary>VRChat の写真のファイル名（撮影日時の読み取り）。</summary>
public static partial class PhotoNaming
{
    /// <summary>VRChat が写真を保存する既定のフォルダ（そのユーザーの「ピクチャ」の下の VRChat。環境のフォルダの設定に従うので、どのユーザーでも同じ形）。</summary>
    public static string DefaultPhotoFolder
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return Path.Combine(pictures.Length > 0 ? pictures : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures"), "VRChat");
        }
    }

    // 新しい形: VRChat_2026-10-25_09-15-30.500_3840x2160.png  古い形: VRChat_1920x1080_2021-11-27_03-00-13.803.png
    [GeneratedRegex(@"^VRChat_(?:\d+x\d+_)?(?<d>\d{4}-\d{2}-\d{2})_(?<t>\d{2}-\d{2}-\d{2})\.(?<ms>\d{3})(?:_\d+x\d+)?\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex PhotoName();

    /// <summary>ファイル名から撮影日時（PC のローカル時刻）。VRChat の写真の形でなければ null。</summary>
    public static DateTimeOffset? TryTakenAt(string path, TimeZoneInfo? zone = null)
    {
        if (PhotoName().Match(Path.GetFileName(path)) is not { Success: true } m) return null;
        var text = $"{m.Groups["d"].Value} {m.Groups["t"].Value}.{m.Groups["ms"].Value}";
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH-mm-ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
        return new DateTimeOffset(local, (zone ?? TimeZoneInfo.Local).GetUtcOffset(local));
    }
}

/// <summary>
/// 1 枚の写真を処理する: ファイル名から撮影日時 → 履歴から、その直前のアバター → 写真の XMP に追記。
/// 履歴は、処理のたびに <paramref name="loadHistory"/> で読み直す（呼び出し側が、ログを読み直してから返せる）。
/// </summary>
public sealed class PhotoProcessor(Func<IReadOnlyList<HistoryEvent>> loadHistory, PhotoOptions options, TimeZoneInfo? zone = null)
{
    public PhotoResult Process(string path)
    {
        if (PhotoNaming.TryTakenAt(path, zone) is not { } taken) return new PhotoResult(PhotoOutcome.NotPhoto, "VRChat の写真のファイル名ではありません");
        var avatar = HistoryStore.AvatarAt(loadHistory(), taken);
        if (avatar?.AvatarName is not { Length: > 0 } name) return new PhotoResult(PhotoOutcome.NoAvatar, "撮影日時の直前のアバターが履歴にありません");
        var info = new FaraXmlInfo(name, avatar.AvatarId);
        if (!options.WriteXmp) return new PhotoResult(PhotoOutcome.WriteDisabled, "設定で、写真には書きません", info);

        var result = PhotoXmpWriter.Write(path, info, options.BackupFolder);
        return result.Outcome switch
        {
            WriteOutcome.Written => new PhotoResult(PhotoOutcome.Written, result.Message, info),
            WriteOutcome.AlreadyTagged => new PhotoResult(PhotoOutcome.AlreadyTagged, result.Message),
            _ => new PhotoResult(PhotoOutcome.Failed, result.Message),
        };
    }
}
