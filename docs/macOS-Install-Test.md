# Installing & testing the macOS fix

This covers the AppleScript-quoting fix for `PwshLauncherMacOS` — replacing `-Command '…'` (which broke under three layers of quoting) with `-EncodedCommand <BASE64>`. Published module (v1.7.6 on PSGallery) ships the buggy binary; you need to overwrite the `osx-arm64` proxy with a locally-built one.

## Prerequisites

```bash
brew install --cask powershell         # PowerShell 7 — skip if `pwsh --version` already works
brew install dotnet@9                  # .NET 9 SDK (needed only to rebuild the proxy)
export DOTNET_ROOT="/opt/homebrew/opt/dotnet@9/libexec"
export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"
```

Confirm: `pwsh --version` → `PowerShell 7.x`, `dotnet --version` → `9.x`.

## 1 — Install the module from PSGallery (baseline)

```powershell
# inside pwsh
Install-PSResource PowerShell.MCP
chmod +x "$(Get-MCPProxyPath)"
```

This installs to `~/.local/share/powershell/Modules/PowerShell.MCP/1.7.6/` with the unfixed osx-arm64 proxy.

## 2 — Build the fixed proxy and overwrite the binary

From the repo root (`/Users/panda/repo/PowerShell.MCP` or your clone):

```bash
dotnet publish PowerShell.MCP.Proxy -c Release -r osx-arm64 --self-contained \
  -o /tmp/pwshmcp-osx-arm64

cp /tmp/pwshmcp-osx-arm64/PowerShell.MCP.Proxy \
   ~/.local/share/powershell/Modules/PowerShell.MCP/1.7.6/bin/osx-arm64/PowerShell.MCP.Proxy

# Re-sign adhoc in place. On macOS 26, `cp` can invalidate the .NET adhoc
# signature and the kernel kills the proxy on launch with SIGKILL /
# "Code Signature Invalid". `codesign -f -s -` reseals the sig.
codesign --force --sign - \
   ~/.local/share/powershell/Modules/PowerShell.MCP/1.7.6/bin/osx-arm64/PowerShell.MCP.Proxy

file ~/.local/share/powershell/Modules/PowerShell.MCP/1.7.6/bin/osx-arm64/PowerShell.MCP.Proxy
# expect: Mach-O 64-bit executable arm64
```

## 3 — Register with your MCP client

```powershell
# Claude Code
Register-PwshToClaudeCode

# Claude Desktop
Register-PwshToClaudeDesktop
```

Then fully quit and relaunch the client so it re-spawns the proxy.

## 4 — Smoke test

### 4a. Offline sanity check (no MCP client required)

Confirms the `-EncodedCommand` path round-trips a tricky agent ID with an embedded apostrophe:

```bash
INIT="Set-Location ~; \$global:PowerShellMCPProxyPid = 99999; \$global:PowerShellMCPAgentId = 'test-''agent'; Write-Host \"AgentId=[\$global:PowerShellMCPAgentId]\""
ENCODED=$(python3 -c "import sys,base64; print(base64.b64encode(sys.argv[1].encode('utf-16le')).decode())" "$INIT")
pwsh -NoProfile -EncodedCommand "$ENCODED"
```

Expected output includes `AgentId=[test-'agent]`. If instead you see `The term 'test-' is not recognized`, the fix didn't take effect.

### 4b. End-to-end via MCP client

1. Restart Claude Code / Desktop.
2. Ask the client to run a PowerShell command (e.g. "run `Get-Date` in PowerShell").
3. Expected:
   - A new **Terminal.app** window opens automatically.
   - pwsh prompt appears with **no** `The term 'default' is not recognized` error.
   - Inside that window, `$global:PowerShellMCPAgentId` returns a real agent id string.
   - `ls /tmp/CoreFxPipe_PowerShell.MCP.*` (or `$TMPDIR/CoreFxPipe_*`) shows a matching pipe.
   - The MCP tool call returns output, not a timeout.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| MCP client logs `Server transport closed unexpectedly` / proxy exits immediately | macOS killed the proxy with `SIGKILL (Code Signature Invalid)`. Run the `codesign --force --sign -` step from step 2; confirm via `~/Library/Logs/DiagnosticReports/PowerShell.MCP.Proxy-*.ips`. |
| Terminal.app opens, `default: The term 'default' is not recognized` | Old proxy binary still in place — step 2 didn't overwrite. Re-run `file` on the target path. |
| No Terminal.app window at all | `osascript` blocked in System Settings → Privacy & Security → Automation. Grant your MCP client permission to control Terminal. |
| `pwsh: command not found` inside Terminal.app | Homebrew's pwsh isn't on login-shell PATH. Add `eval "$(/opt/homebrew/bin/brew shellenv)"` to `~/.zprofile`. |
| Pipe never connects / MCP call times out | Stale pipes from prior runs. `pkill pwsh; rm -f /tmp/CoreFxPipe_PowerShell.MCP.* "$TMPDIR"CoreFxPipe_PowerShell.MCP.*` and retry. |

## Revert

```bash
Update-PSResource PowerShell.MCP -Reinstall     # pulls the unpatched osx-arm64 proxy back
```

## What changed

Single file: `PowerShell.MCP.Proxy/Services/PowerShellProcessManager.cs`, `PwshLauncherMacOS.LaunchPwsh`. The init script is now base64-encoded (UTF-16LE) and passed via `pwsh -NoExit -EncodedCommand <BASE64>` — same approach `PwshLauncherLinux` already uses. No module-side changes.
