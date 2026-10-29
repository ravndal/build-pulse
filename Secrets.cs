using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DevOpsWatcher;

/// <summary>Keeps PATs out of settings.json: DPAPI-encrypted on Windows, stored in the login Keychain on macOS.</summary>
static class Secrets
{
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        if (OperatingSystem.IsWindows()) return Dpapi.Protect(plain);
        if (OperatingSystem.IsMacOS()) return Keychain.Add(plain);
        throw new PlatformNotSupportedException("No secure store for tokens on this OS.");
    }

    /// <summary>Null when there's nothing stored or it can't be read here (e.g. a DPAPI blob on a Mac).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (stored.StartsWith(Keychain.Prefix, StringComparison.Ordinal))
            return OperatingSystem.IsMacOS() ? Keychain.Find(stored[Keychain.Prefix.Length..]) : null;
        return OperatingSystem.IsWindows() ? Dpapi.Unprotect(stored) : null;
    }
}

/// <summary>Windows DPAPI (CurrentUser scope) without an extra NuGet dependency.</summary>
static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr handle);

    const int UiForbidden = 0x1;

    public static string? Protect(string? plain) =>
        string.IsNullOrEmpty(plain) ? null : Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plain), protect: true));

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return Encoding.UTF8.GetString(Run(Convert.FromBase64String(cipher), protect: false)); }
        catch { return null; }
    }

    static byte[] Run(byte[] input, bool protect)
    {
        var dataIn = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var dataOut = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, dataIn.pbData, input.Length);
            bool ok = protect
                ? CryptProtectData(ref dataIn, "DevOpsWatcher", 0, 0, 0, UiForbidden, ref dataOut)
                : CryptUnprotectData(ref dataIn, 0, 0, 0, 0, UiForbidden, ref dataOut);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(dataIn.pbData);
            if (dataOut.pbData != 0) LocalFree(dataOut.pbData);
        }
    }
}

/// <summary>
/// macOS login Keychain via Security.framework's generic-password calls. settings.json only holds
/// "keychain:&lt;account&gt;"; the token itself stays in the Keychain under the "DevOpsWatcher" service.
/// </summary>
static class Keychain
{
    public const string Prefix = "keychain:";
    const string Service = "DevOpsWatcher";
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const int ErrSecItemNotFound = -25300;

    [DllImport(Security)]
    static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr itemRef);

    [DllImport(Security)]
    static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, IntPtr itemRef);

    [DllImport(Security)]
    static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    /// <summary>Stores the token under a fresh account name and returns the reference to keep in settings.</summary>
    public static string Add(string plain)
    {
        var account = Guid.NewGuid().ToString("N");
        byte[] s = Encoding.UTF8.GetBytes(Service), a = Encoding.UTF8.GetBytes(account), p = Encoding.UTF8.GetBytes(plain);
        int status = SecKeychainAddGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, (uint)p.Length, p, 0);
        if (status != 0) throw new InvalidOperationException($"Couldn't save the token in the Keychain (OSStatus {status}).");
        return Prefix + account;
    }

    public static string? Find(string account)
    {
        byte[] s = Encoding.UTF8.GetBytes(Service), a = Encoding.UTF8.GetBytes(account);
        int status = SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, 0);
        if (status == ErrSecItemNotFound) return null;
        if (status != 0) { Log.Write($"Keychain lookup failed (OSStatus {status})"); return null; }
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { SecKeychainItemFreeContent(0, data); }
    }
}
