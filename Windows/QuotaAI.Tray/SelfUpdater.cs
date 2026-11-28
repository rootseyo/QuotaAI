using System.ComponentModel;
using System.Diagnostics;
using QuotaAI.Core;

namespace QuotaAI.Tray;

/// <summary>Swaps the running executable for a downloaded one and restarts.</summary>
internal static class SelfUpdater
{
    public static Version CurrentVersion =>
        UpdateChecker.Normalize(typeof(SelfUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));

    /// <summary>Development builds (Version 0.0.0) never replace themselves.</summary>
    public static bool IsDevBuild => CurrentVersion == new Version(0, 0, 0, 0);

    public static string VersionText => IsDevBuild ? "development build" : CurrentVersion.ToString();

    public static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return path is not null && string.Equals(Path.GetFileName(path), UpdateChecker.AssetName, StringComparison.OrdinalIgnoreCase)
                ? path
                : null;
        }
    }

    public static string StagingPath(string exe) => exe + ".new";

    private static string BackupPath(string exe) => exe + ".old";

    /// <summary>Removes the previous version left behind by the last update.</summary>
    public static void CleanupLeftovers()
    {
        if (ExePath is not { } exe)
            return;
        FileErrors.TryDelete(BackupPath(exe));
        FileErrors.TryDelete(StagingPath(exe));
    }

    /// <summary>
    /// Windows lets a running exe be renamed but not overwritten: move it aside, put the
    /// new file in its place (rolling back on failure), then start it. The new process
    /// waits for this one to exit before taking the single-instance lock.
    /// </summary>
    public static void ApplyAndRestart(string staged)
    {
        var exe = ExePath ?? throw new InvalidOperationException("Not running from QuotaAI.exe.");
        var old = BackupPath(exe);
        if (File.Exists(old))
            File.Delete(old);

        File.Move(exe, old);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }

        try
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.ArgumentList.Add("--restart-after");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            using var _ = Process.Start(start) ?? throw new Win32Exception("The new version did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Could not launch the new build: put the running version back so the
            // next launch still works, and keep this instance running.
            File.Move(exe, staged, overwrite: true);
            File.Move(old, exe);
            throw new UpdateException("The new version could not be started, so the update was rolled back.", ex);
        }
    }
}
