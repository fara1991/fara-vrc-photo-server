using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaraVrcPhotoServer;

/// <summary>アプリ名（画面に出す名前。名前を変えるときは、ここと、画面の文だけ）。</summary>
public static class AppInfo
{
    public const string Name = "FaraVRCPhotoServer";
}

/// <summary>
/// 設定（docs/spec.md の第 7 節。settings.json）。空の項目は既定を使う。
/// 表示名は、ログから自分の行を選ぶためにだけ使う（履歴・写真には書かない）。
/// </summary>
public sealed record AppSettings
{
    /// <summary>自分の VRChat の表示名。空のあいだは、何も記録しない。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>写真を見張るフォルダ。空なら、ユーザーの「ピクチャ」の下の VRChat（VRChat の既定の保存先。みんな同じなので、設定の画面には出さない。テスト・特別な環境のための項目）。</summary>
    public string PhotoFolder { get; init; } = "";

    /// <summary>VRChat のログのフォルダ。空なら、既定の場所。</summary>
    public string LogFolder { get; init; } = "";

    /// <summary>履歴ファイル。空なら、既定の場所。</summary>
    public string HistoryPath { get; init; } = "";

    /// <summary>写真に追記するか（false なら履歴だけ）。</summary>
    public bool WriteXmp { get; init; } = true;

    /// <summary>書く前の元の写真のコピー先（フォルダを決めて控える。CLI・特別な環境のための項目。画面には出さない）。空ならコピーしない。</summary>
    public string BackupFolder { get; init; } = "";

    /// <summary>書く前の元の写真を、写真と同じ場所の backup フォルダに残すか（既定は残さない）。タグを埋め込んだ写真は、元の場所にそのまま置く。</summary>
    public bool BackupOriginals { get; init; }

    /// <summary>Windows の起動時に自動で起動するか。</summary>
    public bool AutoStart { get; init; }

    [JsonIgnore]
    public string EffectivePhotoFolder => PhotoFolder.Trim() is { Length: > 0 } p ? p : PhotoNaming.DefaultPhotoFolder;

    [JsonIgnore]
    public string EffectiveLogFolder => LogFolder.Trim() is { Length: > 0 } p ? p : LogScanner.DefaultLogFolder;

    [JsonIgnore]
    public string EffectiveHistoryPath => HistoryPath.Trim() is { Length: > 0 } p ? p : HistoryStore.DefaultPath;

    /// <summary>動かすのに足りない・おかしい点（無ければ空）。</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(DisplayName)) problems.Add("VRChat の表示名が未設定です");
        if (!Directory.Exists(EffectivePhotoFolder)) problems.Add($"写真のフォルダがありません: {EffectivePhotoFolder}");
        if (!Directory.Exists(EffectiveLogFolder)) problems.Add($"VRChat のログのフォルダがありません: {EffectiveLogFolder}");
        return problems;
    }
}

/// <summary>設定ファイルの読み書き。読めなければ既定値（壊れたファイルで起動を止めない）。書くときは一時ファイル → 入れ替え。</summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Path { get; } = path;

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FaraVrcPhotoServer", "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options) + "\n");
        File.Move(temp, Path, overwrite: true);
    }
}
