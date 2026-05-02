---
document type: cmdlet
external help file: PowerShell.MCP-Help.xml
HelpUri: ''
Locale: en-US
Module Name: PowerShell.MCP
ms.date: 05/02/2026
PlatyPS schema version: 2024-05-01
title: Register-PwshToTermius
---

# Register-PwshToTermius

## SYNOPSIS

Installs the Termius launcher shim and prints setup instructions for using
Termius as the launcher for pwsh-mcp consoles on macOS.

## SYNTAX

### __AllParameterSets

```
Register-PwshToTermius [<CommonParameters>]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Configures macOS Termius as an alternative to Terminal.app for pwsh-mcp
consoles. Writes a wrapper shim to `~/.local/bin/pwsh-mcp-termius-shim` and
creates the handoff queue at `~/.cache/PowerShell.MCP/queue/`.

After running this cmdlet, two manual steps remain:

1. In Termius: open Settings -> Terminal -> Local Terminal Path and paste
   the absolute path to the shim that was just installed.
2. In your MCP host config, set the environment variable
   `PWSH_MCP_LAUNCHER=termius` for the pwsh server entry.

The shim becomes Termius's default local shell. When pwsh-mcp launches a
session it drops a one-shot handoff script into the queue and activates
Termius; the next-spawned local tab claims and execs it. Tabs opened
manually see no fresh handoff and fall through to the user's real login
shell, so normal Termius use is unaffected.

This integration is macOS-only.

## EXAMPLES

### EXAMPLE 1

Register-PwshToTermius

## PARAMETERS

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

## OUTPUTS

### None. Writes status messages to the host.

None.

## NOTES

## RELATED LINKS

- [Register-PwshToClaudeCode]()
- [Register-PwshToClaudeDesktop]()
