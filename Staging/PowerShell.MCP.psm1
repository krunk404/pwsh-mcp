# PowerShell.MCP Module Script
# Provides automatic cleanup when Remove-Module is executed


# On Linux/macOS, PSReadLine interferes with timer events.
# Remove it and replace with a custom PSConsoleHostReadLine that polls
# Console.KeyAvailable instead of blocking on Console.ReadLine().
# This allows the MCP timer event action block to run between input polls.
if (-not $IsWindows) {
    Remove-Module PSReadLine -ErrorAction SilentlyContinue

    function global:PSConsoleHostReadLine {
        $line = [System.Text.StringBuilder]::new()
        while ($true) {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                switch ($key.Key) {
                    'Enter' {
                        [Console]::WriteLine()
                        return $line.ToString()
                    }
                    'Backspace' {
                        if ($line.Length -gt 0) {
                            $line.Length--
                            [Console]::Write("`b `b")
                        }
                    }
                    default {
                        # Ctrl+C: cancel current line
                        if ($key.Key -eq 'C' -and ($key.Modifiers -band [ConsoleModifiers]::Control)) {
                            [Console]::WriteLine("^C")
                            return ""
                        }
                        if ($key.KeyChar -ge ' ') {
                            $line.Append($key.KeyChar) | Out-Null
                            [Console]::Write($key.KeyChar)
                        }
                    }
                }
            } else {
                # No input available - sleep briefly.
                # PowerShell processes the event queue between pipeline statements,
                # so the MCP timer action block can run during this Sleep.
                Start-Sleep -Milliseconds 50
            }
        }
    }
}

# Set OnRemove script block to execute cleanup automatically
<#
.SYNOPSIS
    Registers PowerShell.MCP as an MCP server in Claude Desktop.

.DESCRIPTION
    Adds or updates the "pwsh" entry in Claude Desktop's
    claude_desktop_config.json. Existing settings in the file are preserved.
    If a legacy "PowerShell" entry pointing to PowerShell.MCP.Proxy exists,
    it is removed and replaced with the new "pwsh" entry.

.EXAMPLE
    Register-PwshToClaudeDesktop
    Registers PowerShell.MCP in Claude Desktop configuration.

.OUTPUTS
    None. Writes status messages to the host.
#>
function Register-PwshToClaudeDesktop {
    [CmdletBinding()]
    param()

    $serverName = 'pwsh'
    $legacyName = 'PowerShell'
    $command = Get-MCPProxyPath

    # Determine config file path per platform
    # MSIX (Microsoft Store) installs virtualize %APPDATA% under Packages\
    $configPath = if ($IsWindows) {
        $msixDir = Get-ChildItem "$env:LOCALAPPDATA\Packages\Claude_*" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($msixDir) {
            Join-Path $msixDir.FullName 'LocalCache\Roaming\Claude\claude_desktop_config.json'
        } else {
            Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
        }
    } elseif ($IsMacOS) {
        Join-Path $HOME 'Library/Application Support/Claude/claude_desktop_config.json'
    } elseif ($IsLinux) {
        Join-Path $HOME '.config/Claude/claude_desktop_config.json'
    } else {
        throw "Unsupported platform."
    }

    # Load or create config
    if (Test-Path $configPath) {
        $json = Get-Content -Path $configPath -Raw -Encoding UTF8
        $config = $json | ConvertFrom-Json -AsHashtable
    } else {
        $configDir = Split-Path $configPath -Parent
        if (-not (Test-Path $configDir)) {
            New-Item -ItemType Directory -Path $configDir -Force | Out-Null
        }
        $config = @{}
    }

    if (-not $config.ContainsKey('mcpServers')) {
        $config['mcpServers'] = @{}
    }

    # Migrate legacy "PowerShell" entry if it points to PowerShell.MCP.Proxy
    if ($config['mcpServers'].ContainsKey($legacyName)) {
        $legacyCommand = $config['mcpServers'][$legacyName]['command']
        if ($legacyCommand -match 'PowerShell\.MCP\.Proxy') {
            $config['mcpServers'].Remove($legacyName)
            Write-Host "Removed legacy '$legacyName' entry." -ForegroundColor DarkYellow
        }
    }

    $action = if ($config['mcpServers'].ContainsKey($serverName)) { 'Updated' } else { 'Added' }
    $config['mcpServers'][$serverName] = @{ command = $command }

    $config | ConvertTo-Json -Depth 10 | Set-Content -Path $configPath -Encoding UTF8 -NoNewline
    Write-Host "$action '$serverName' in $configPath" -ForegroundColor Green
    Write-Host "  command: $command" -ForegroundColor Gray
    if ($action -eq 'Added') {
        Write-Host "Restart Claude Desktop to apply changes." -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
    Registers PowerShell.MCP as an MCP server in Claude Code.

.DESCRIPTION
    Runs 'claude mcp add pwsh -s user' with the current module's
    proxy executable path. If a legacy "PowerShell" entry pointing to
    PowerShell.MCP.Proxy exists, it is removed first.

.EXAMPLE
    Register-PwshToClaudeCode

.OUTPUTS
    None. Passes through output from the claude CLI.
#>
function Register-PwshToClaudeCode {
    [CmdletBinding()]
    param()

    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Error "Claude Code CLI ('claude') not found. Install it first: https://docs.anthropic.com/en/docs/claude-code"
        return
    }

    # Remove legacy "PowerShell" entry if it points to our proxy
    $legacyInfo = claude mcp get PowerShell -s user 2>&1
    if ($legacyInfo -match 'PowerShell\.MCP\.Proxy') {
        claude mcp remove PowerShell -s user
        Write-Host "Removed legacy 'PowerShell' entry from Claude Code." -ForegroundColor DarkYellow
    }

    $proxyPath = Get-MCPProxyPath
    claude mcp add pwsh -s user -- $proxyPath
}

<#
.SYNOPSIS
    Installs the Termius launcher shim and prints setup instructions.

.DESCRIPTION
    Configures macOS Termius as an alternative launcher for pwsh-mcp consoles.
    Writes a wrapper shim to ~/.local/bin/pwsh-mcp-termius-shim and creates the
    handoff queue at ~/.cache/PowerShell.MCP/queue/. The user must then point
    Termius's Local Terminal Path at the shim and set PWSH_MCP_LAUNCHER=termius
    in the MCP host's environment.

    The shim acts as Termius's default shell: when pwsh-mcp launches a session
    it drops a one-shot handoff script into the queue; the next-spawned Termius
    local tab picks it up and execs pwsh. Manually-opened tabs see no fresh
    handoff and drop into the user's normal login shell.

.EXAMPLE
    Register-PwshToTermius

.OUTPUTS
    None. Writes status messages to the host.
#>
function Register-PwshToTermius {
    [CmdletBinding()]
    param()

    if (-not $IsMacOS) {
        Write-Error "Register-PwshToTermius is only supported on macOS. Termius integration is not available on Windows or Linux."
        return
    }

    $shimPath = Join-Path $HOME '.local/bin/pwsh-mcp-termius-shim'
    $shimDir  = Split-Path $shimPath -Parent
    $queueDir = Join-Path $HOME '.cache/PowerShell.MCP/queue'

    foreach ($d in @($shimDir, $queueDir)) {
        if (-not (Test-Path $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
            Write-Host "Created: $d" -ForegroundColor Gray
        }
    }

    $shimContents = @'
#!/bin/bash
# pwsh-mcp-termius-shim — installed by Register-PwshToTermius.
# Configured as Termius's Local Terminal Path. When pwsh-mcp drops a fresh
# handoff into ~/.cache/PowerShell.MCP/queue/ and activates Termius, the
# next-spawned local tab claims and execs the handoff. Manually-opened tabs
# see no fresh handoff and fall through to the user's real login shell.

QUEUE="$HOME/.cache/PowerShell.MCP/queue"
mkdir -p "$QUEUE"

# Walk handoffs in mtime order (oldest first); claim via atomic mv.
# `mv` is atomic on a single filesystem: the first shim to succeed wins.
while IFS= read -r f; do
    [ -f "$f" ] || continue

    # Drop stale handoffs (>=30s old). The Proxy stops waiting at 30s, so a
    # handoff older than that is an orphan — don't accidentally run it.
    mtime=$(stat -f %m "$f" 2>/dev/null || echo 0)
    now=$(date +%s)
    if [ $(( now - mtime )) -ge 30 ]; then
        rm -f "$f" 2>/dev/null
        continue
    fi

    consumed="$f.consumed.$$"
    if mv "$f" "$consumed" 2>/dev/null; then
        chmod +x "$consumed" 2>/dev/null
        exec bash "$consumed"
    fi
done < <(ls -tr "$QUEUE"/*.sh 2>/dev/null)

# No fresh handoff — open the user's real login shell.
real_shell=$(dscl . -read "/Users/$USER" UserShell 2>/dev/null | awk '{print $2}')
exec "${real_shell:-/bin/zsh}" -l
'@

    Set-Content -Path $shimPath -Value $shimContents -Encoding UTF8 -NoNewline
    & chmod +x $shimPath
    Write-Host "Installed: $shimPath" -ForegroundColor Green

    Write-Host ""
    Write-Host "Two manual steps to finish setup:" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  1. Open Termius -> Settings -> Terminal -> Local Terminal Path." -ForegroundColor White
    Write-Host "     Paste this path:" -ForegroundColor White
    Write-Host "       $shimPath" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  2. In your MCP host config, set the env var:" -ForegroundColor White
    Write-Host "       PWSH_MCP_LAUNCHER=termius" -ForegroundColor Yellow
    Write-Host "     (Claude Desktop: 'env' under the pwsh server entry." -ForegroundColor White
    Write-Host "      Claude Code: 'claude mcp add pwsh -s user -e PWSH_MCP_LAUNCHER=termius -- <proxy-path>'.)" -ForegroundColor White
    Write-Host ""
    Write-Host "Then restart your MCP host so the Proxy picks up the new env var." -ForegroundColor White
}


$ExecutionContext.SessionState.Module.OnRemove = {
    try {
        #Write-Host "[PowerShell.MCP] Module removal detected, starting cleanup..." -ForegroundColor Yellow

        # Load and execute MCPCleanup.ps1 from embedded resources
        $assembly = [System.Reflection.Assembly]::GetAssembly([PowerShell.MCP.MCPModuleInitializer])
        $resourceName = "PowerShell.MCP.Resources.MCPCleanup.ps1"

        $stream = $assembly.GetManifestResourceStream($resourceName)
        if ($stream) {
            try {
                $reader = New-Object System.IO.StreamReader($stream)
                $cleanupScript = $reader.ReadToEnd()
            } finally {
                if ($reader) { $reader.Dispose() }
                if ($stream) { $stream.Dispose() }
            }

            # Execute cleanup script
            Invoke-Expression $cleanupScript
            #Write-Host "[PowerShell.MCP] OnRemove cleanup completed" -ForegroundColor Green
        } else {
            #Write-Warning "[PowerShell.MCP] MCPCleanup.ps1 resource not found"
        }
    }
    catch {
        #Write-Warning "[PowerShell.MCP] Error during module removal cleanup: $($_.Exception.Message)"
    }
}

#Write-Host "[PowerShell.MCP] Module loaded with OnRemove cleanup support" -ForegroundColor Green

<#
.SYNOPSIS
    Gets the path to the PowerShell.MCP.Proxy executable for the current platform.

.DESCRIPTION
    Returns the full path to the platform-specific PowerShell.MCP.Proxy executable.
    Use this path in your MCP client configuration.

.PARAMETER Escape
    If specified, escapes backslashes for use in JSON configuration files.

.EXAMPLE
    Get-MCPProxyPath
    Returns: C:\Program Files\PowerShell\7\Modules\PowerShell.MCP\bin\win-x64\PowerShell.MCP.Proxy.exe

.EXAMPLE
    Get-MCPProxyPath -Escape
    Returns: C:\\Program Files\\PowerShell\\7\\Modules\\PowerShell.MCP\\bin\\win-x64\\PowerShell.MCP.Proxy.exe

.OUTPUTS
    System.String
#>
function Get-MCPProxyPath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [switch]$Escape
    )

    $moduleBase = $PSScriptRoot
    $binFolder = Join-Path $moduleBase 'bin'

    # Determine RID based on OS and architecture
    $rid = if ($IsWindows) {
        switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
            'X64'  { 'win-x64' }
            'Arm64' { 'win-arm64' }
            default { 'win-x64' }
        }
    }
    elseif ($IsMacOS) {
        switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
            'X64'  { 'osx-x64' }
            'Arm64' { 'osx-arm64' }
            default { 'osx-x64' }
        }
    }
    elseif ($IsLinux) {
        switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
            'X64'  { 'linux-x64' }
            'Arm64' { 'linux-arm64' }
            'Arm'   { 'linux-arm' }
            default { 'linux-x64' }
        }
    }
    else {
        throw "Unsupported operating system"
    }

    # Determine executable name
    $exeName = if ($IsWindows) { 'PowerShell.MCP.Proxy.exe' } else { 'PowerShell.MCP.Proxy' }

    $proxyPath = Join-Path $binFolder $rid $exeName

    if (-not (Test-Path $proxyPath)) {
        throw "PowerShell.MCP.Proxy not found at: $proxyPath. Please ensure the module is properly installed for your platform ($rid)."
    }

    # NuGet's zip implementation drops Unix mode bits, so packages installed via
    # PSResourceGet land with the executable bit cleared. Repair on first lookup
    # so callers (Register-PwshToClaudeDesktop, etc.) don't have to know about it.
    if (-not $IsWindows) {
        $info = Get-Item -LiteralPath $proxyPath
        if (-not ($info.UnixMode -match 'x')) {
            & chmod +x $proxyPath
        }
    }

    if ($Escape) {
        return $proxyPath.Replace('\', '\\')
    }

    return $proxyPath
}


<#
.SYNOPSIS
    Gets information about the MCP client that owns this console.

.DESCRIPTION
    Returns ownership information for the current PowerShell console, including
    whether it is owned by an MCP proxy, the proxy's PID, the agent ID,
    and the client name (e.g., Claude Desktop, Claude Code, VS Code).

.EXAMPLE
    Get-MCPOwner

    Owned      : True
    ProxyPid   : 22208
    AgentId    : cc19706b
    ClientName : Claude Desktop

.EXAMPLE
    Get-MCPOwner

    Owned      : False
    ProxyPid   :
    AgentId    :
    ClientName :

.OUTPUTS
    PSCustomObject with Owned, ProxyPid, AgentId, and ClientName properties
#>
function Get-MCPOwner {
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param()

    $pipeName = [PowerShell.MCP.MCPModuleInitializer]::GetPipeName()

    if (-not $pipeName) {
        return [PSCustomObject]@{
            Owned      = $false
            ProxyPid   = $null
            AgentId    = $null
            ClientName = $null
        }
    }

    # Parse pipe name segments
    # Unowned: PSMCP.{pwshPid} (2 segments)
    # Owned:   PSMCP.{proxyPid}.{agentId}.{pwshPid} (4 segments)
    $segments = $pipeName.Split('.')

    if ($segments.Length -ne 4) {
        return [PSCustomObject]@{
            Owned      = $false
            ProxyPid   = $null
            AgentId    = $null
            ClientName = $null
        }
    }

    $proxyPid = [int]$segments[1]
    $agentId  = $segments[2]

    # Determine client name by examining process path and parent chain
    # Uses Get-Process Path and Parent properties (cross-platform, no Win32_Process)
    $clientName = $null
    try {
        $proxyProcess = Get-Process -Id $proxyPid -ErrorAction SilentlyContinue
        $currentProcess = $proxyProcess

        for ($i = 0; $currentProcess -and $i -lt 5; $i++) {
            $processName = $currentProcess.ProcessName.ToLower()
            $processPath = $currentProcess.Path

            # Check process name and path for known clients
            if ($processName -eq 'claude' -or $processPath -match 'AnthropicClaude') {
                $clientName = 'Claude Desktop'
                break
            }
            elseif ($processName -eq 'node' -or $processPath -match 'claude-code|claude_code') {
                $clientName = 'Claude Code'
                break
            }
            elseif ($processName -match '^code$|^code - insiders$') {
                $clientName = 'VS Code'
                break
            }
            elseif ($processName -match 'cursor') {
                $clientName = 'Cursor'
                break
            }

            # Get parent process (PowerShell 7.4+, cross-platform)
            $currentProcess = $currentProcess.Parent
        }

        # Fallback to proxy process name
        if (-not $clientName -and $proxyProcess) {
            $clientName = $proxyProcess.ProcessName
        }
    }
    catch {
        # Ignore errors in process lookup
    }

    return [PSCustomObject]@{
        Owned      = $true
        ProxyPid   = $proxyPid
        AgentId    = $agentId
        ClientName = $clientName
    }
}


<#
.SYNOPSIS
    Stops all pwsh processes to release DLL locks.

.DESCRIPTION
    Stops all pwsh processes on the system to release DLL locks.
    Useful for PowerShell module developers before dotnet build.

    With -PwshPath: starts a new pwsh session from the specified binary with
    PowerShell.MCP loaded, then stops all other pwsh processes.

    WARNING: This stops ALL pwsh processes, including those used by other users
    or other MCP clients on the same machine.

.PARAMETER PwshPath
    Path to the pwsh binary to start. If specified, a new session is started
    from this binary with PowerShell.MCP imported before stopping other processes.

.EXAMPLE
    Stop-AllPwsh
    Stops all pwsh processes. Use before rebuilding a PowerShell module to release DLL locks.

.EXAMPLE
    Stop-AllPwsh -PwshPath (Get-PSOutput)
    Starts a new session using the built pwsh binary, then stops all other pwsh processes.
    Use in the PowerShell repository after Start-PSBuild.
#>
function Stop-AllPwsh {
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
    param(
        [Parameter(Position = 0)]
        [string]$PwshPath
    )

    $targets = Get-Process pwsh -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $PID }
    $count = ($targets | Measure-Object).Count + 1  # +1 for self

    if (-not $PSCmdlet.ShouldProcess("All $count pwsh processes (including this session)", "Stop")) {
        return
    }

    if ($PwshPath) {
        if (-not (Test-Path $PwshPath)) {
            throw "pwsh not found at: $PwshPath"
        }

        # Start new pwsh with PowerShell.MCP loaded
        $newProc = Start-Process $PwshPath -ArgumentList '-NoExit', '-Command', 'Import-Module PowerShell.MCP' -PassThru
        Start-Sleep -Seconds 3

        # Stop all other pwsh processes (except the new one and self)
        Get-Process pwsh -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -notin @($newProc.Id, $PID) } |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    else {
        # Stop all other pwsh processes
        $targets | Stop-Process -Force -ErrorAction SilentlyContinue
    }

    # Stop self last
    Stop-Process -Id $PID -Force
}



