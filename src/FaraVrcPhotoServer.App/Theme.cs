using System.Drawing;

namespace FaraVrcPhotoServer.App;

/// <summary>画面の配色（写真ブログのアプリ fara-vrc-photo-album と同じ、深い紫が主色）。</summary>
internal static class Theme
{
    public static readonly Color Accent = ColorTranslator.FromHtml("#673AB7");
    public static readonly Color AccentDark = ColorTranslator.FromHtml("#512DA8");
    public static readonly Color AccentLight = ColorTranslator.FromHtml("#EDE7F6");
    public static readonly Color Panel = ColorTranslator.FromHtml("#F3EEF8");
    public static readonly Color Border = ColorTranslator.FromHtml("#CDBEE0");
    public static readonly Color Muted = ColorTranslator.FromHtml("#6B6B6B");
    public static readonly Color Danger = ColorTranslator.FromHtml("#B42318");
    public static readonly Color Good = ColorTranslator.FromHtml("#2E7D32");
    public static readonly Font BaseFont = new("Yu Gothic UI", 9.5F);

    /// <summary>ウィンドウ全体の見た目（背景・文字・フォント）。</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Color.White;
        form.ForeColor = Color.FromArgb(0x21, 0x21, 0x21);
        form.Font = BaseFont;
    }

    /// <summary>上の見出しの帯（紫の地に白い題と説明）。</summary>
    public static Panel Header(string title, string description)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Accent, Padding = new Padding(16, 10, 16, 8) };
        var heading = new Label { Text = title, ForeColor = Color.White, Font = new Font("Yu Gothic UI", 13F, FontStyle.Bold), AutoSize = true, Location = new Point(16, 10) };
        var note = new Label { Text = description, ForeColor = AccentLight, AutoSize = true, Location = new Point(16, 40) };
        panel.Controls.Add(heading);
        panel.Controls.Add(note);
        return panel;
    }

    /// <summary>主のボタン（紫の塗り・白い文字）。</summary>
    public static Button Primary(string text)
    {
        var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White, Padding = new Padding(10, 2, 10, 2), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = AccentDark;
        button.FlatAppearance.MouseDownBackColor = AccentDark;
        return button;
    }

    /// <summary>副のボタン（白い地・紫の枠と文字）。</summary>
    public static Button Secondary(string text)
    {
        var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Accent, Padding = new Padding(10, 2, 10, 2), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = AccentLight;
        button.FlatAppearance.MouseDownBackColor = AccentLight;
        return button;
    }

    /// <summary>入力欄（細い枠・薄い紫の地）。</summary>
    public static TextBox Input() => new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(0xFB, 0xF9, 0xFE), Margin = new Padding(3, 3, 3, 0) };

    public static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Color.FromArgb(0x21, 0x21, 0x21), FlatStyle = FlatStyle.System };

    /// <summary>メニューの配色（選んだ項目が薄い紫）。</summary>
    public sealed class MenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => AccentLight;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelectedGradientBegin => AccentLight;
        public override Color MenuItemSelectedGradientEnd => AccentLight;
        public override Color MenuItemPressedGradientBegin => AccentLight;
        public override Color MenuItemPressedGradientEnd => AccentLight;
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color MenuBorder => Border;
    }
}
