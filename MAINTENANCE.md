# Fork maintenance

This fork (`krunk404/pwsh-mcp`) tracks upstream [`yotsuda/PowerShell.MCP`](https://github.com/yotsuda/PowerShell.MCP) and adds two macOS-specific fixes. This doc is the runbook for pulling upstream updates, keeping our patches applied, and publishing the module to the private ProGet feed.

## What this fork changes vs. upstream

Two localized patches. If upstream ever ships equivalent fixes, they become redundant and we drop them.

| File | Change | Why |
| --- | --- | --- |
| `PowerShell.MCP.Proxy/Services/MacOSBootstrap.cs` | **New file.** P/Invokes `confstr(_CS_DARWIN_USER_TEMP_DIR)` and sets `TMPDIR`. | MCP hosts spawn the Proxy without `TMPDIR`, so `.NET` named pipes land in `/tmp` while the pwsh child (launched via Terminal.app) writes its pipe under the per-user `/var/folders/…/T/`. Proxy couldn't enumerate or connect. |
| `PowerShell.MCP.Proxy/Program.cs` | Calls `MacOSBootstrap.EnsureTmpDir()` at top of `Main`. | Must run before any `Path.GetTempPath()` / pipe I/O. |
| `PowerShell.MCP.Proxy/Services/PowerShellProcessManager.cs` (`PwshLauncherMacOS`) | Redirects osascript's stdout/stderr. | AppleScript return value was leaking into the Proxy's stdout, which is the MCP JSON-RPC channel. |

When upstream touches any of those three files, expect a merge conflict. Everything else should merge cleanly.

## One-time setup: add the upstream remote

```bash
cd /Users/panda/repo/PowerShell.MCP
git remote add upstream https://github.com/yotsuda/PowerShell.MCP.git
git fetch upstream
```

After this, `origin` is your fork (`krunk404/pwsh-mcp`) and `upstream` is the original.

> Because this fork was initialized from a tarball (no upstream history preserved), the first merge from upstream needs `--allow-unrelated-histories`. See below.

## Checking for upstream updates

```bash
git fetch upstream
git log --oneline upstream/main ^main | head -20    # commits in upstream not in ours
git tag -l --contains upstream/main | sort -V       # latest upstream tags
```

Or watch the upstream repo on GitHub for releases.

## Pulling an upstream update

**Step 1 — Branch off a clean working state.**

```bash
git checkout main
git pull origin main                 # make sure we're current with our fork
git checkout -b merge-upstream-<version>
```

**Step 2 — Merge upstream.**

First time (unrelated histories):

```bash
git merge upstream/main --allow-unrelated-histories
```

Subsequent times:

```bash
git merge upstream/main
```

**Step 3 — Resolve conflicts.**

If the only conflicts are in the three files listed above, the resolution pattern is:

- `MacOSBootstrap.cs` — ours. It's a new file; upstream won't have it unless they added one with the same name.
- `Program.cs` — keep upstream's changes AND keep the `MacOSBootstrap.EnsureTmpDir()` call at the top of `Main`.
- `PowerShellProcessManager.cs` — keep upstream's restructuring of `PwshLauncherMacOS`, but ensure `RedirectStandardOutput = true` and `RedirectStandardError = true` remain on the osascript `ProcessStartInfo`, and that the drain reads (`process.StandardOutput.ReadToEnd()` etc.) still happen after sending the AppleScript.

Use `git diff HEAD~1 -- <file>` before committing to sanity-check each resolved file.

**Step 4 — Build and test locally.**

```bash
cd PowerShell.MCP.Proxy
dotnet publish -c Release -r osx-arm64 --self-contained
```

Swap the rebuilt binary into `~/.local/share/powershell/Modules/PowerShell.MCP/<version>/bin/osx-arm64/PowerShell.MCP.Proxy`, restart Claude Code, and exercise `start_console` + a round-trip `invoke_expression` to verify the two Mac behaviors still hold.

If TMPDIR handling regressed, you'll see "TIMEOUT after 30s" in the Proxy logs when `start_console` runs.
If the osascript stdout fix regressed, the very first MCP response will be corrupted JSON and tools will fail immediately.

**Step 5 — Merge back to `main` and publish.**

```bash
git checkout main
git merge --ff-only merge-upstream-<version>
git push origin main
```

**Step 6 — Tag and let CI publish to ProGet.**

If upstream bumped `ModuleVersion` in `Staging/PowerShell.MCP.psd1`, keep their version. Otherwise bump it yourself (e.g. `1.7.6` → `1.7.6.1` for a patch-on-patch release). Then:

```bash
git tag v<version>
git push origin v<version>
```

The `publish-proget.yml` workflow fires on `v*` tags and publishes to `https://repo.nerdforge.dev/feeds/PowerShell`.

To re-publish without a new tag, go to Actions → "Publish module to ProGet" → Run workflow.

## If upstream changes the pipe discovery or launcher architecture

Our TMPDIR fix assumes .NET named pipes live under `Path.GetTempPath()` on Unix, and that the pwsh child inherits `TMPDIR` from Terminal.app. If upstream switches to a different IPC mechanism (raw sockets, a fixed path, etc.), `MacOSBootstrap.cs` becomes unnecessary — delete it and remove the call from `Program.cs`.

Our osascript stdout redirect assumes the Mac launcher still uses `osascript` to drive Terminal.app. If upstream switches to a different Mac launch path (e.g. direct `Process.Start` of `pwsh` in a pty, or iTerm2), re-evaluate whether the redirect is still needed.

Both patches should be considered temporary — ideally upstreamed as PRs.

## Release versioning

| Scenario | Version to use |
| --- | --- |
| Upstream releases new minor (e.g. `1.8.0`), our patches merge cleanly | `1.8.0` |
| Upstream releases new minor, we had to adapt our patches | `1.8.0.1` |
| No upstream change, we fixed/improved our patches | bump the 4th segment: `1.7.6.1` → `1.7.6.2` |

`ModuleVersion` lives in `Staging/PowerShell.MCP.psd1`. ProGet rejects re-publishes of the same version, so every push must bump it.

## Known local quirks (not fork-specific but worth noting)

- **Claude Code sandbox** blocks writes to `.git/hooks/` and `.git/config` in this directory regardless of `dangerouslyDisableSandbox`. So `git init`, `git remote add`, and any config change must be run in a real shell. `git add`, `git commit`, `git push`, and `git log` work from the assistant.
- **`Build-AllPlatforms.ps1`** is Windows-biased (uses `$env:USERPROFILE`, backslash paths, `Get-Module PowerShell.MCP -ListAvailable` for the output directory). For CI we skip it and call `dotnet publish` per-RID directly — see `.github/workflows/publish-proget.yml`.
