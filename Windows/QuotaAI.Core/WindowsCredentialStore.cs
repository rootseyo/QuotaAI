using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace QuotaAI.Core;

/// <summary>
/// Stores user-provided API keys in the Windows Credential Manager (never in
/// plaintext files on disk) and reads CLI-owned generic credentials read-only.
/// </summary>
public static class WindowsCredentialStore
{
    private const string Prefix = "QuotaAI:";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    // In-memory fallback used only on non-Windows test hosts.
    private static readonly ConcurrentDictionary<string, string> NonWindowsMemoryStore = new(StringComparer.Ordinal);

    public static string? GetApiKey(string account)
    {
        if (!OperatingSystem.IsWindows())
            return NonWindowsMemoryStore.TryGetValue(account, out var val) ? val : null;

        var bytes = ReadRawCredentialBytes(Prefix + account);
        if (bytes is null || bytes.Length == 0)
            return null;

        return DecodeSecretBytes(bytes);
    }

    public static bool Exists(string account)
    {
        if (!OperatingSystem.IsWindows())
            return NonWindowsMemoryStore.ContainsKey(account);

        if (!CredReadW(Prefix + account, CredTypeGeneric, 0, out var ptr))
            return false;

        CredFree(ptr);
        return true;
    }

    public static bool SetApiKey(string account, string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            NonWindowsMemoryStore[account] = secret;
            return true;
        }

        var blob = Encoding.UTF8.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = Prefix + account,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName
            };
            return CredWriteW(ref cred, 0);
        }
        finally
        {
            handle.Free();
        }
    }

    public static bool DeleteApiKey(string account)
    {
        if (!OperatingSystem.IsWindows())
        {
            NonWindowsMemoryStore.TryRemove(account, out _);
            return true;
        }

        if (CredDeleteW(Prefix + account, CredTypeGeneric, 0))
            return true;

        var err = Marshal.GetLastWin32Error();
        return err == 1168; // ERROR_NOT_FOUND
    }

    public static bool ExternalExists(string targetName)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (!CredReadW(targetName, CredTypeGeneric, 0, out var ptr))
            return false;

        CredFree(ptr);
        return true;
    }

    public static byte[]? ReadRawCredentialBytes(string targetName)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        if (!CredReadW(targetName, CredTypeGeneric, 0, out var ptr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return null;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    internal static string DecodeSecretBytes(byte[] bytes)
    {
        // Prefer UTF-8 unless null-byte pattern indicates UTF-16LE
        if (bytes.Length >= 2 && bytes.Length % 2 == 0 && bytes[1] == 0)
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');

        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWriteW([In] ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDeleteW(string target, uint type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr cred);
}
