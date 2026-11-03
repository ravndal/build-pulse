using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BuildPulse;

/// <summary>
/// Keeps PATs out of settings.json: DPAPI-encrypted on Windows, stored in the login Keychain on macOS, and in
/// files only the user can read on Linux (and on a Mac whose Keychain is locked, as over SSH).
/// </summary>
static class Secrets
{
    /// <summary>Where tokens are kept on this machine, for hints next to the token field.</summary>
    public static string StorageText =>
        OperatingSystem.IsWindows() ? "Stored encrypted for your Windows user."
        : OperatingSystem.IsMacOS() ? "Stored in your login Keychain."
        : $"Stored in {TokenFiles.Dir}, readable only by you.";

    public static string? Protect(string? plain, string? previousStored = null)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        string stored;
        if (OperatingSystem.IsWindows()) stored = Dpapi.Protect(plain)!;
        else if (OperatingSystem.IsMacOS())
        {
            try { stored = Keychain.Add(plain); }
            catch (Exception ex)
            {
                Log.Write($"Keychain unavailable, keeping the token in a file instead: {ex.Message}");
                stored = TokenFiles.Add(plain);
            }
        }
        else stored = TokenFiles.Add(plain);
        Delete(previousStored);
        return stored;
    }

    /// <summary>Null when there's nothing stored or it can't be read here (e.g. a DPAPI blob on a Mac).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (stored.StartsWith(Keychain.Prefix, StringComparison.Ordinal))
            return OperatingSystem.IsMacOS() ? Keychain.Find(stored[Keychain.Prefix.Length..]) : null;
        if (stored.StartsWith(TokenFiles.Prefix, StringComparison.Ordinal))
            return TokenFiles.Find(stored[TokenFiles.Prefix.Length..]);
        return OperatingSystem.IsWindows() ? Dpapi.Unprotect(stored) : null;
    }

    /// <summary>Removes a token that's no longer referenced. DPAPI blobs live in settings.json and need nothing.</summary>
    public static void Delete(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return;
        try
        {
            if (stored.StartsWith(TokenFiles.Prefix, StringComparison.Ordinal)) TokenFiles.Delete(stored[TokenFiles.Prefix.Length..]);
            else if (stored.StartsWith(Keychain.Prefix, StringComparison.Ordinal) && OperatingSystem.IsMacOS())
                Keychain.Delete(stored[Keychain.Prefix.Length..]);
        }
        catch (Exception ex) { Log.Write($"Couldn't delete a stored token: {ex.Message}"); }
    }
}

/// <summary>
/// Tokens as files in a folder only the user can open (0700, files 0600), like the GitHub CLI keeps them where
/// there's no keyring: Linux desktops and headless servers. settings.json only holds "file:&lt;name&gt;".
/// </summary>
static class TokenFiles
{
    public const string Prefix = "file:";

    public static string Dir => Path.Combine(AppInfo.DataDir, "tokens");

    public static string Add(string plain)
    {
        if (!Directory.Exists(Dir))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Dir);
            else Directory.CreateDirectory(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var name = Guid.NewGuid().ToString("N");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new StreamWriter(Path.Combine(Dir, name), new UTF8Encoding(false), options)) file.Write(plain);
        return Prefix + name;
    }

    public static string? Find(string name)
    {
        try { return File.ReadAllText(PathOf(name)).Trim(); }
        catch { return null; }
    }

    public static void Delete(string name) => File.Delete(PathOf(name));

    /// <summary>The token file, refusing names that would reach outside the folder.</summary>
    static string PathOf(string name) =>
        name.Length > 0 && name.All(char.IsAsciiLetterOrDigit) ? Path.Combine(Dir, name) : throw new ArgumentException("Bad token reference.");
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
                ? CryptProtectData(ref dataIn, AppInfo.Id, 0, 0, 0, UiForbidden, ref dataOut)
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
/// "keychain:&lt;account&gt;"; the token itself stays in the Keychain under the "BuildPulse" service, or the
/// "DevOpsWatcher" one for tokens saved before the rename.
/// </summary>
static class Keychain
{
    public const string Prefix = "keychain:";
    const string Service = AppInfo.Id;
    static readonly string[] LookupServices = [Service, AppInfo.LegacyId];
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const int ErrSecItemNotFound = -25300;

    [DllImport(Security)]
    static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr itemRef);

    [DllImport(Security)]
    static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, IntPtr itemRef);

    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    static extern int SecKeychainFindGenericPasswordItem(IntPtr keychainOrArray, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, IntPtr passwordLength, IntPtr password, out IntPtr itemRef);

    [DllImport(Security)]
    static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    static extern void CFRelease(IntPtr cf);

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
        byte[] a = Encoding.UTF8.GetBytes(account);
        foreach (var service in LookupServices)
        {
            byte[] s = Encoding.UTF8.GetBytes(service);
            int status = SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out var length, out var data, 0);
            if (status == ErrSecItemNotFound) continue;
            if (status != 0) { Log.Write($"Keychain lookup failed (OSStatus {status})"); return null; }
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally { SecKeychainItemFreeContent(0, data); }
        }
        return null;
    }

    public static void Delete(string account)
    {
        byte[] a = Encoding.UTF8.GetBytes(account);
        foreach (var service in LookupServices)
        {
            byte[] s = Encoding.UTF8.GetBytes(service);
            int status = SecKeychainFindGenericPasswordItem(0, (uint)s.Length, s, (uint)a.Length, a, 0, 0, out var item);
            if (status == ErrSecItemNotFound) continue;
            if (status != 0) throw new InvalidOperationException($"OSStatus {status}");
            try
            {
                status = SecKeychainItemDelete(item);
                if (status != 0) throw new InvalidOperationException($"OSStatus {status}");
            }
            finally { CFRelease(item); }
        }
    }
}
