using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text;

namespace QuotaAI.Tray;

internal static class ProviderIcons
{
    private static readonly Lazy<Bitmap?> CodexBitmap = new(() => LoadIcnsResource("codex.icns"));
    private static readonly Lazy<Bitmap?> AntigravityBitmap = new(() => LoadIcnsResource("antigravity.icns"));
    private static readonly Lazy<Bitmap?> ClaudeBitmap = new(() => LoadPngResource("claude.png"));
    private static readonly Lazy<Bitmap?> CursorBitmap = new(() => LoadPngResource("cursor.png"));

    public static Bitmap? GetIconBitmap(string providerId)
    {
        if (providerId.Equals("codex", StringComparison.OrdinalIgnoreCase)
            || providerId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase))
        {
            return CodexBitmap.Value;
        }

        if (providerId.Equals("gemini", StringComparison.OrdinalIgnoreCase))
            return AntigravityBitmap.Value;

        if (providerId.Equals("cursor", StringComparison.OrdinalIgnoreCase))
            return CursorBitmap.Value;

        if (providerId.Equals("claude", StringComparison.OrdinalIgnoreCase)
            || providerId.Equals("claude-api", StringComparison.OrdinalIgnoreCase))
        {
            return ClaudeBitmap.Value;
        }

        return null;
    }

    public static void DrawProviderIconOrBadge(
        Graphics g,
        string providerId,
        string shortCode,
        Rectangle bounds,
        Font badgeFont)
    {
        if (GetIconBitmap(providerId) is { } bmp)
        {
            var prevInterp = g.InterpolationMode;
            var prevOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, bounds);
            g.InterpolationMode = prevInterp;
            g.PixelOffsetMode = prevOffset;

            // Distinguish extra Codex accounts (e.g. CxW -> 'W' corner pill)
            if (providerId.StartsWith("codex:", StringComparison.OrdinalIgnoreCase) && shortCode.Length > 2)
            {
                var tag = shortCode[2..];
                var pillSize = Math.Max(10, bounds.Width * 45 / 100);
                var pillRect = new Rectangle(bounds.Right - pillSize, bounds.Bottom - pillSize, pillSize, pillSize);
                using var bg = new SolidBrush(Color.FromArgb(15, 166, 140));
                using var path = RoundedPath(pillRect, pillSize / 2);
                g.FillPath(bg, path);
                TextRenderer.DrawText(g, tag, badgeFont, pillRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            return;
        }

        var radius = Math.Max(4, (int)Math.Round(bounds.Width * 0.24));
        using (var brush = new SolidBrush(ProviderBadgePalette.ColorFor(providerId)))
        using (var path = RoundedPath(bounds, radius))
        {
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(
            g,
            shortCode,
            badgeFont,
            bounds,
            Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    public static Bitmap CreateBadgeImage(string providerId, string shortCode, int sizePx, Font badgeFont)
    {
        var outBmp = new Bitmap(sizePx, sizePx);
        using var g = Graphics.FromImage(outBmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        DrawProviderIconOrBadge(g, providerId, shortCode, new Rectangle(0, 0, sizePx, sizePx), badgeFont);
        return outBmp;
    }

    private static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 1)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap? LoadPngResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;
        using var temp = new Bitmap(stream);
        return new Bitmap(temp);
    }

    private static Bitmap? LoadIcnsResource(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var pos = 8;
        byte[]? chosenPng = null;

        while (pos + 8 <= bytes.Length)
        {
            var type = Encoding.ASCII.GetString(bytes, pos, 4);
            var len = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(pos + 4, 4));
            if (len <= 8 || pos + len > bytes.Length)
                break;

            if (type is "ic07" or "ic08" or "ic12"
                && len > 16 && bytes[pos + 8] == 0x89 && bytes[pos + 9] == 0x50)
            {
                chosenPng = bytes[(pos + 8)..(pos + len)];
                if (type == "ic07")
                    break;
            }
            pos += len;
        }

        if (chosenPng is null)
            return null;

        using var pngStream = new MemoryStream(chosenPng);
        using var temp = new Bitmap(pngStream);
        return new Bitmap(temp);
    }
}
