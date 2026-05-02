using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PowerShell.MCP.Proxy.Services;

/// <summary>
/// macOS launcher that lands the pwsh-mcp session in Termius instead of
/// Terminal.app. Termius has no AppleScript verb, URL scheme, or CLI flag for
/// "run this command in a new local tab," so we use a wrapper-shim approach:
///
///   1. The user pre-configures Termius's Local Terminal Path to point at
///      ~/.local/bin/pwsh-mcp-termius-shim (one-time, via Register-PwshToTermius).
///   2. On launch, we drop a per-session handoff script into
///      ~/.cache/PowerShell.MCP/queue/ and fire `open -na Termius` to spawn a
///      new local-terminal window.
///   3. The shim picks the oldest fresh handoff (mtime &lt; 10s), flock()s it,
///      atomically renames it to mark it consumed, and execs it. Concurrent
///      launches each get their own handoff file — no single-file race.
///   4. Manually-opened Termius tabs see no fresh handoff and fall through to
///      the user's real login shell.
/// </summary>
public class PwshLauncherTermius : IPwshLauncher
{
    public string Name => "termius";

    private static readonly string[] TermiusAppPaths =
    [
        "/Applications/Termius.app",
        $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Applications/Termius.app",
    ];

    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
        return TermiusAppPaths.Any(Directory.Exists);
    }

    public async Task<int?> LaunchPwshAsync(string agentId, string? startupCommands, string? startLocation)
    {
        // Build the same Base64-UTF16LE encoded init script as PwshLauncherMacOS.
        // Base64 is safe at every shell layer without escaping.
        var proxyPid = Process.GetCurrentProcess().Id;
        var setLocation = string.IsNullOrEmpty(startLocation)
            ? "Set-Location ~; "
            : $"Set-Location -LiteralPath '{startLocation.Replace("'", "''")}'; ";
        var initCommand = string.IsNullOrEmpty(startupCommands)
            ? $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue"
            : $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue; {startupCommands}";
        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(initCommand));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var queueDir = Path.Combine(home, ".cache", "PowerShell.MCP", "queue");
        Directory.CreateDirectory(queueDir);

        // Drop orphans older than 30s (handoffs the shim never consumed —
        // e.g. user dismissed the Termius window before it ran the shim).
        TryCleanupStaleHandoffs(queueDir);

        // Per-session filename includes a sortable timestamp so the shim can pick
        // FIFO order, plus a guid so concurrent launches never collide.
        var tsNs = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000).ToString("D19");
        var handoffPath = Path.Combine(queueDir, $"{tsNs}-{Guid.NewGuid():N}.sh");
        var handoffBody = $"#!/bin/bash\nexec pwsh -NoExit -EncodedCommand {encodedCommand}\n";
        await File.WriteAllTextAsync(handoffPath, handoffBody);

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(handoffPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Console.Error.WriteLine($"[INFO] PwshLauncherTermius: wrote handoff {handoffPath}");

        // Activate Termius and force a new local-terminal window. `open -na`
        // (new instance) reliably spawns a window even when Termius is already
        // running. No AppleScript, no Accessibility permission required.
        var psi = new ProcessStartInfo
        {
            FileName = "open",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-na");
        psi.ArgumentList.Add("Termius");

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            // Drain to keep `open` from blocking on a full pipe.
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
            {
                Console.Error.WriteLine($"[WARN] PwshLauncherTermius: 'open -na Termius' exited with {proc.ExitCode}; pipe poll will time out if Termius didn't open a tab");
            }
        }

        // PID unknown — Proxy polls for the new standby pipe.
        return null;
    }

    private static void TryCleanupStaleHandoffs(string queueDir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-30);
            foreach (var pattern in new[] { "*.sh", "*.consumed.*", "*.lock" })
            {
                foreach (var f in Directory.EnumerateFiles(queueDir, pattern))
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        try { File.Delete(f); } catch { /* best-effort */ }
                    }
                }
            }
        }
        catch { /* best-effort */ }
    }
}
