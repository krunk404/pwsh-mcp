using System.Runtime.InteropServices;

namespace PowerShell.MCP.Proxy.Services;

/// <summary>
/// Resolves which IPwshLauncher to use for the current process. Reads the
/// PWSH_MCP_LAUNCHER env var; falls back to the platform default if the
/// requested launcher is unavailable (or unset). With env var unset the
/// resolved launcher matches the one the previous if/else picked, so this
/// is a transparent refactor for existing users.
/// </summary>
public static class LauncherRegistry
{
    public static IPwshLauncher Resolve()
    {
        var preferred = Environment.GetEnvironmentVariable("PWSH_MCP_LAUNCHER")?.Trim().ToLowerInvariant();
        var candidates = ForCurrentPlatform().ToList();

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = candidates.FirstOrDefault(l => l.Name == preferred);
            if (match != null && match.IsAvailable())
            {
                return match;
            }
            Console.Error.WriteLine($"[INFO] LauncherRegistry: requested launcher '{preferred}' unavailable, falling back to platform default");
        }

        var def = candidates.FirstOrDefault(l => l.IsAvailable());
        if (def == null)
        {
            throw new PlatformNotSupportedException("No usable pwsh launcher for the current platform.");
        }
        return def;
    }

    private static IEnumerable<IPwshLauncher> ForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return new PwshLauncherWindows();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Opt-in launcher first so PWSH_MCP_LAUNCHER=termius can pick it.
            // PwshLauncherMacOS is the default fallback (always available).
            yield return new PwshLauncherTermius();
            yield return new PwshLauncherMacOS();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            yield return new PwshLauncherLinux();
        }
    }
}
