using System.Text.Json;

namespace QuotaAI.Core;

public sealed record WidgetSettings(int? X = null, int? Y = null, bool AlwaysOnTop = true)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.IsPathFullyQualified(localAppData) ? localAppData : Path.GetTempPath();
        return Path.Combine(root, "QuotaAI", "widget.json");
    }

    public static WidgetSettings Load(string? path = null)
    {
        var target = path ?? DefaultPath();
        try
        {
            if (!File.Exists(target))
                return new WidgetSettings();

            using var stream = File.OpenRead(target);
            return JsonSerializer.Deserialize<WidgetSettings>(stream, JsonOptions) ?? new WidgetSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WidgetSettings();
        }
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath();
        try
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var temp = $"{target}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore transient file-system errors; widget settings are non-critical UI state.
        }
    }

    public static (int X, int Y) ClampToWorkingArea(
        int x, int y, int width, int height,
        int areaLeft, int areaTop, int areaRight, int areaBottom)
    {
        var maxX = Math.Max(areaLeft, areaRight - width);
        var maxY = Math.Max(areaTop, areaBottom - height);
        return (Math.Clamp(x, areaLeft, maxX), Math.Clamp(y, areaTop, maxY));
    }
}
