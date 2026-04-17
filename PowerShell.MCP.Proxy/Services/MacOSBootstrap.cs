using System.Runtime.InteropServices;

namespace PowerShell.MCP.Proxy.Services;

/// <summary>
/// macOS-specific startup bootstrap.
/// MCP hosts (e.g. Claude Code) spawn the Proxy without TMPDIR set. On macOS
/// .NET named pipes live as Unix domain sockets under Path.GetTempPath(), which
/// resolves to the per-user directory returned by confstr(_CS_DARWIN_USER_TEMP_DIR)
/// when TMPDIR is set, and to "/tmp" when it isn't. A pwsh session launched via
/// Terminal.app inherits the real user TMPDIR, so its pipe ends up in
/// /var/folders/.../T/ — which the Proxy then can neither enumerate nor connect to.
/// Resolve TMPDIR ourselves before any pipe I/O happens.
/// </summary>
internal static class MacOSBootstrap
{
    private const int _CS_DARWIN_USER_TEMP_DIR = 65537;

    [DllImport("libc", EntryPoint = "confstr", SetLastError = true)]
    private static extern UIntPtr confstr(int name, IntPtr buf, UIntPtr len);

    public static void EnsureTmpDir()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;

        var existing = Environment.GetEnvironmentVariable("TMPDIR");
        if (!string.IsNullOrEmpty(existing)) return;

        try
        {
            // Probe required buffer size
            var required = confstr(_CS_DARWIN_USER_TEMP_DIR, IntPtr.Zero, UIntPtr.Zero);
            var size = (int)required.ToUInt64();
            if (size <= 1) return;

            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var written = confstr(_CS_DARWIN_USER_TEMP_DIR, buf, (UIntPtr)size);
                if (written.ToUInt64() == 0) return;

                var path = Marshal.PtrToStringAnsi(buf);
                if (string.IsNullOrEmpty(path)) return;

                Environment.SetEnvironmentVariable("TMPDIR", path);
                Console.Error.WriteLine($"[INFO] MacOSBootstrap: Resolved TMPDIR={path}");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] MacOSBootstrap: Failed to resolve per-user TMPDIR: {ex.Message}");
        }
    }
}
