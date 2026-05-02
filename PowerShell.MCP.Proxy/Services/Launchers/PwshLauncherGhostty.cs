using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PowerShell.MCP.Proxy.Services;

/// <summary>
/// macOS launcher that lands the pwsh-mcp session in Ghostty instead of
/// Terminal.app. Ghostty 1.3+ ships a native AppleScript dictionary with a
/// `new surface configuration` record that takes a `command` property
/// directly — so the integration is structurally identical to the Terminal.app
/// launcher (osascript piped via stdin), just with different verbs.
///
/// Opt in by setting PWSH_MCP_LAUNCHER=ghostty on the MCP host.
/// </summary>
public class PwshLauncherGhostty : IPwshLauncher
{
    public string Name => "ghostty";

    private static readonly string[] GhosttyAppPaths =
    [
        "/Applications/Ghostty.app",
        $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Applications/Ghostty.app",
    ];

    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
        return GhosttyAppPaths.Any(Directory.Exists);
    }

    public Task<int?> LaunchPwshAsync(string agentId, string? startupCommands, string? startLocation)
    {
        // Same Base64-UTF16LE encoded init script as PwshLauncherMacOS — Base64
        // is safe at every shell layer without escaping, which keeps the three-
        // way quoting (AppleScript -> shell -> pwsh) painless.
        var proxyPid = Process.GetCurrentProcess().Id;
        var setLocation = string.IsNullOrEmpty(startLocation)
            ? "Set-Location ~; "
            : $"Set-Location -LiteralPath '{startLocation.Replace("'", "''")}'; ";
        var initCommand = string.IsNullOrEmpty(startupCommands)
            ? $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue"
            : $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue; {startupCommands}";
        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(initCommand));

        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            RedirectStandardInput = true,
            // Drain stdout/stderr so osascript doesn't leak return values into
            // the MCP JSON-RPC channel (same defense as PwshLauncherMacOS).
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process != null)
        {
            // Ghostty 1.3+ AppleScript: build a surface configuration with the
            // command, then create a new window from it. The user's login shell
            // gets bypassed because `command` runs directly — pwsh's path must
            // be on the PATH that Ghostty itself sees (which inherits launchd's
            // PATH; usually fine for Homebrew /opt/homebrew/bin via shellenv).
            process.StandardInput.WriteLine("tell application \"Ghostty\"");
            process.StandardInput.WriteLine("    activate");
            process.StandardInput.WriteLine("    set cfg to new surface configuration");
            process.StandardInput.WriteLine($"    set command of cfg to \"pwsh -NoExit -EncodedCommand {encodedCommand}\"");
            process.StandardInput.WriteLine("    new window with configuration cfg");
            process.StandardInput.WriteLine("end tell");
            process.StandardInput.Close();

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
        }

        // PID unknown — Proxy polls for the new standby pipe.
        return Task.FromResult<int?>(null);
    }
}
