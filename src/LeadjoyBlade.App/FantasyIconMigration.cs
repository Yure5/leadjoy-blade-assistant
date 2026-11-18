using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LeadjoyBlade.App.Dialogs;
using LeadjoyBlade.Core.Config;

namespace LeadjoyBlade.App;

/// <summary>
/// 旧版本生成并保存的幻想图标恢复为当前平面样式；用户上传的图片不会被覆盖。
/// </summary>
internal static class FantasyIconMigration
{
    public static void Migrate(ProfileStore store)
    {
        var changed = false;
        foreach (var item in store.FantasyLibrary.Items)
        {
            if (string.IsNullOrWhiteSpace(item.IconText)
                || string.IsNullOrWhiteSpace(item.ImageBase64)
                || !IsGeneratedIcon(item.ImageBase64))
                continue;

            var text = item.IconText.Trim();
            if (text.Length > 2) text = text[..2];
            var color = FantasyItemDialog.ParseColor(item.IconColor, Color.FromRgb(0x8B, 0x3A, 0x3A));
            item.ImageBase64 = FantasyItemDialog.MakeIconBase64(text, color);
            changed = true;
        }

        if (changed) store.SaveFantasyLibrary();
    }

    private static bool IsGeneratedIcon(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != 128 || frame.PixelHeight != 128) return false;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            Color At(int x, int y)
            {
                var i = y * stride + x * 4;
                return Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]);
            }

            // 生成图标有透明圆角、顶部米金描边和实心背景。
            if (At(0, 0).A > 24) return false;
            var border = At(64, 2);
            if (Math.Abs(border.R - 0xE8) > 45
                || Math.Abs(border.G - 0xD9) > 45
                || Math.Abs(border.B - 0xB5) > 45)
                return false;
            return At(64, 20).A > 200;
        }
        catch
        {
            return false;
        }
    }
}
