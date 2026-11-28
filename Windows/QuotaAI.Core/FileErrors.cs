namespace QuotaAI.Core;

/// <summary>Shared handling for the expected, non-fatal file-system failures.</summary>
public static class FileErrors
{
    /// <summary>True for errors a best-effort file operation should swallow (locked, missing, denied).</summary>
    public static bool Is(Exception ex) => ex is IOException or UnauthorizedAccessException;

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (Is(ex)) { }
    }
}
