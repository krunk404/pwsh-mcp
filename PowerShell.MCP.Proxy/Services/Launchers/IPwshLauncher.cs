namespace PowerShell.MCP.Proxy.Services;

/// <summary>
/// A pluggable strategy for launching the user-facing pwsh console that the
/// Proxy will then talk to over a named pipe. Selected at runtime by
/// LauncherRegistry based on the PWSH_MCP_LAUNCHER env var (with platform
/// default as fallback).
/// </summary>
public interface IPwshLauncher
{
    /// <summary>
    /// Stable identifier for this launcher (lowercase). Matched against the
    /// PWSH_MCP_LAUNCHER env var.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Returns true if this launcher's host application/binary is present and
    /// usable on the current machine. Sync — only file/process checks.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Launches a pwsh process inside a visible terminal window with the
    /// PowerShell.MCP module imported. Returns the pwsh PID when known
    /// (Windows path), or null when the launcher can't observe the spawned
    /// process directly and the Proxy must discover the new pipe by polling.
    /// </summary>
    Task<int?> LaunchPwshAsync(string agentId, string? startupCommands, string? startLocation);
}
