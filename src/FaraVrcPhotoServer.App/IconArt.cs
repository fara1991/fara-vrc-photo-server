using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FaraVrcPhotoServer.App;

/// <summary>
/// アプリのアイコン（紫の角丸の四角とカメラ）。画像ファイルは持たず、コードで描く。
/// トレイのアイコンと、実行ファイルのアイコン（app.ico。<c>FaraVrcPhotoServer.exe --write-icon &lt;ファイル&gt;</c> で作り直せる）は、同じ絵。
/// </summary>
internal static class IconArt
{
    private static readonly int[] IcoSizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>32 の格子で描いた絵を、size の大きさに拡大・縮小して描く。背景は透明。</summary>
    public static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var k = size / 32f;
        using var back = new GraphicsPath();
        var r = 10 * k;
        back.AddArc(1 * k, 1 * k, r, r, 180, 90);
        back.AddArc(21 * k, 1 * k, r, r, 270, 90);
        back.AddArc(21 * k, 21 * k, r, r, 0, 90);
        back.AddArc(1 * k, 21 * k, r, r, 90, 90);
        back.CloseFigure();
        using var fill = new SolidBrush(Theme.Accent);
        g.FillPath(fill, back);
        using var white = new SolidBrush(Color.White);
        g.FillRectangle(white, 7 * k, 11 * k, 18 * k, 12 * k);                                                // カメラの本体
        g.FillRectangle(white, 12 * k, 8 * k, 8 * k, 4 * k);                                                  // ファインダー
        g.FillEllipse(fill, 12 * k, 13 * k, 8 * k, 8 * k);
        using var glass = new SolidBrush(Color.FromArgb(0xE8, 0x79, 0xF9));
        g.FillEllipse(glass, 14 * k, 15 * k, 4 * k, 4 * k);
        return bitmap;
    }

    /// <summary>トレイに出すアイコン（画面の設定の小さいアイコンの大きさ）。</summary>
    public static Icon CreateTrayIcon()
    {
        using var bitmap = Draw(Math.Max(16, SystemInformation.SmallIconSize.Width));
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>複数の大きさ（PNG 形式）を 1 つにした .ico を書く。</summary>
    public static void WriteIco(string path)
    {
        var frames = IcoSizes.Select(size =>
        {
            using var bitmap = Draw(size);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return png.ToArray();
        }).ToList();

        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((ushort)0);                                                                          // 予約
            writer.Write((ushort)1);                                                                          // 種類: アイコン
            writer.Write((ushort)frames.Count);
            var offset = 6 + 16 * frames.Count;
            for (var i = 0; i < frames.Count; i++)
            {
                var dimension = IcoSizes[i] >= 256 ? (byte)0 : (byte)IcoSizes[i];                              // 256 は 0 で表す
                writer.Write(dimension);
                writer.Write(dimension);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frames[i].Length);
                writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, output.ToArray());
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);
}
