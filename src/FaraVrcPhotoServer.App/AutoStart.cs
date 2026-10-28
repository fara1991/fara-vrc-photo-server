using Microsoft.Win32;

namespace FaraVrcPhotoServer.App;

/// <summary>Windows の起動時に自動で起動する設定（現在のユーザーの Run キー。管理者の権限は要らない）。</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FaraVrcPhotoServer";

    /// <summary>今、自動起動が登録されているか（インストーラーの設定とも、設定の画面とも、同じ Run キーを見る）。</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
