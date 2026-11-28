using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuotaAI.Core;

public sealed class CursorProvider : IUsageProvider
{
    private const string Endpoint = "https://cursor.com/api/usage-summary";

    public string Id => "cursor";
    public string DisplayName => "Cursor";
    public string ShortCode => "Cu";
    public string? DashboardUrl => "https://cursor.com/dashboard";
    public string? CredentialHelpText => "Reads the local session token from Cursor (`state.vscdb`, read-only).";

    private static string StateDbPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb");

    public bool IsDetected => File.Exists(StateDbPath);

    public async Task<ProviderUsage> FetchAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var token = ReadAccessTokenFromSqlite(StateDbPath);
        var sub = ExtractJwtSubject(token);
        var cookieValue = Uri.EscapeDataString($"{sub}::{token}");

        using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={cookieValue}");

        // No redirects: a hand-set Cookie header would be forwarded to the redirect target.
        using var resp = await SharedHttp.NoRedirectClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderUnavailableException("Cursor session expired — sign in to Cursor again.");
        if (!resp.IsSuccessStatusCode)
            throw new ProviderUnavailableException($"cursor.com returned HTTP {(int)resp.StatusCode}.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return ParseSummary(doc.RootElement, DateTimeOffset.Now);
    }

    internal static ProviderUsage ParseSummary(JsonElement root, DateTimeOffset now)
    {
        var resetsAt = root.TryGetProperty("billingCycleEnd", out var bce) && bce.ValueKind == JsonValueKind.String
            ? SharedHttp.ParseIsoTimestamp(bce.GetString())
            : null;
        var windows = new List<UsageWindow>();

        if (root.TryGetProperty("individualUsage", out var ind) && ind.ValueKind == JsonValueKind.Object)
        {
            if (ind.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Object)
            {
                double? total = plan.TryGetProperty("totalPercentUsed", out var tp) && tp.TryGetDouble(out var tpv) ? tpv : null;
                if (total is null
                    && plan.TryGetProperty("used", out var u) && u.TryGetDouble(out var used)
                    && plan.TryGetProperty("limit", out var l) && l.TryGetDouble(out var lim) && lim > 0)
                {
                    total = used / lim * 100.0;
                }
                if (total is { } t && double.IsFinite(t))
                    windows.Add(new UsageWindow("Included usage", Math.Clamp(t, 0, 100), resetsAt));
                if (plan.TryGetProperty("autoPercentUsed", out var ap) && ap.TryGetDouble(out var apv) && double.IsFinite(apv))
                    windows.Add(new UsageWindow("Auto usage", Math.Clamp(apv, 0, 100), resetsAt));
                if (plan.TryGetProperty("apiPercentUsed", out var kp) && kp.TryGetDouble(out var kpv) && double.IsFinite(kpv))
                    windows.Add(new UsageWindow("API usage", Math.Clamp(kpv, 0, 100), resetsAt));
            }

            if (windows.Count == 0 && TryReadPoolPercent(ind, "overall", out var overallPct))
                windows.Add(new UsageWindow("Personal cap", overallPct, resetsAt));
        }

        if (windows.Count == 0
            && root.TryGetProperty("teamUsage", out var team) && team.ValueKind == JsonValueKind.Object
            && TryReadPoolPercent(team, "pooled", out var teamPct))
        {
            windows.Add(new UsageWindow("Team pool", teamPct, resetsAt));
        }

        var membership = root.TryGetProperty("membershipType", out var mt) && mt.ValueKind == JsonValueKind.String
            ? mt.GetString()
            : null;
        var planName = string.IsNullOrWhiteSpace(membership)
            ? null
            : char.ToUpperInvariant(membership[0]) + membership[1..];
        return new ProviderUsage(planName, windows, now);
    }

    private static bool TryReadPoolPercent(JsonElement parent, string propName, out double pct)
    {
        pct = 0;
        if (parent.TryGetProperty(propName, out var pool) && pool.ValueKind == JsonValueKind.Object
            && pool.TryGetProperty("used", out var u) && u.TryGetDouble(out var used)
            && pool.TryGetProperty("limit", out var l) && l.TryGetDouble(out var lim) && lim > 0)
        {
            pct = Math.Clamp(used / lim * 100.0, 0, 100);
            return true;
        }
        return false;
    }

    internal static string ExtractJwtSubject(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
            throw new ProviderUnavailableException("Cursor session token has an unexpected format.");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - payload.Length % 4) % 4);
        try
        {
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(sub.GetString()))
            {
                return sub.GetString()!;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }
        throw new ProviderUnavailableException("Cursor session token has an unexpected format.");
    }

    private static string ReadAccessTokenFromSqlite(string dbPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(dbPath))
            throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

        var db = IntPtr.Zero;
        try
        {
            if (WinSqlite.sqlite3_open_v2(Encoding.UTF8.GetBytes(dbPath + '\0'), out db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0)
                throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

            const string sql = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1\0";
            if (WinSqlite.sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql), -1, out var stmt, IntPtr.Zero) != 0)
                throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

            try
            {
                if (WinSqlite.sqlite3_step(stmt) != 100 /* SQLITE_ROW */)
                    throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

                var ptr = WinSqlite.sqlite3_column_blob(stmt, 0);
                var len = WinSqlite.sqlite3_column_bytes(stmt, 0);
                if (ptr == IntPtr.Zero || len <= 0)
                    throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");

                var raw = new byte[len];
                Marshal.Copy(ptr, raw, 0, len);
                var token = Encoding.UTF8.GetString(raw).Trim();
                if (string.IsNullOrEmpty(token))
                    throw new ProviderUnavailableException("Cursor session token not found (is Cursor signed in?).");
                return token;
            }
            finally
            {
                _ = WinSqlite.sqlite3_finalize(stmt);
            }
        }
        catch (DllNotFoundException)
        {
            throw new ProviderUnavailableException("System SQLite library unavailable.");
        }
        finally
        {
            if (db != IntPtr.Zero)
                _ = WinSqlite.sqlite3_close(db);
        }
    }

    private static class WinSqlite
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr ppDb, int flags, IntPtr zVfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close(IntPtr db);
    }
}
