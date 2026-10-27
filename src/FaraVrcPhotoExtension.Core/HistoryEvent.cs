using System.Text.Json;
using System.Text.Json.Serialization;

namespace FaraVrcPhotoExtension;

/// <summary>履歴の種類（docs/spec.md の第 4 節）。</summary>
public static class HistoryEventTypes
{
    public const string World = "world";
    public const string Avatar = "avatar";
}

/// <summary>
/// 履歴の 1 イベント（1 行の JSON）。時刻は ISO 8601 のオフセットつき。
/// 表示名・インスタンス・ユーザー ID・位置は持たない（持てない形にしてある）。
/// </summary>
public sealed record HistoryEvent
{
    [JsonPropertyName("t")]
    public DateTimeOffset Time { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("worldId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorldId { get; init; }

    [JsonPropertyName("worldName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorldName { get; init; }

    [JsonPropertyName("avatarName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvatarName { get; init; }

    [JsonPropertyName("avatarId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AvatarId { get; init; }

    public static HistoryEvent World(DateTimeOffset time, string worldId, string? worldName) =>
        new() { Time = time, Type = HistoryEventTypes.World, WorldId = worldId, WorldName = worldName };

    public static HistoryEvent Avatar(DateTimeOffset time, string avatarName, string? avatarId) =>
        new() { Time = time, Type = HistoryEventTypes.Avatar, AvatarName = avatarName, AvatarId = avatarId };

    /// <summary>同じイベントかを判定する鍵（時刻の秒・種類・内容）。起動のたびに全ログを読み直しても、二重に追記しないため。</summary>
    [JsonIgnore]
    public string Key => string.Join('\u001f', Time.ToUnixTimeSeconds(), Type, WorldId, WorldName, AvatarName, AvatarId);

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 日本語をそのまま書く
    };

    /// <summary>1 行の JSON（改行なし）。</summary>
    public string ToJsonLine() => JsonSerializer.Serialize(this, Options);

    /// <summary>1 行の JSON から。読めない・種類が不明・時刻が無いときは null（壊れた行は読み飛ばす）。</summary>
    public static HistoryEvent? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var e = JsonSerializer.Deserialize<HistoryEvent>(line, Options);
            if (e is null || e.Time == default) return null;
            if (e.Type is not (HistoryEventTypes.World or HistoryEventTypes.Avatar)) return null;
            return e;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
