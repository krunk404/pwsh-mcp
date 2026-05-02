using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PowerShell.MCP.Proxy.Models;

namespace PowerShell.MCP.Proxy.Services;

public class PowerShellProcessManager
{
    private const string PowerShellExecutableName = "pwsh";

    /// <summary>
    /// Checks if a PowerShell process is running
    /// </summary>
    /// <returns>true if PowerShell process is found</returns>
    public static bool IsPowerShellProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(PowerShellExecutableName);
            var found = processes.Length > 0;

            // Release process object resources
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return found;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error checking PowerShell process: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts PowerShell process with PowerShell.MCP module imported
    /// </summary>
    /// <param name="agentId">Agent ID for console isolation</param>
    /// <param name="startupCommands">Optional PowerShell commands to execute after module import (e.g. Write-Host statements)</param>
    /// <returns>true if startup succeeded</returns>
    public static async Task<bool> StartPowerShellWithModuleAsync(string agentId, string? startupCommands = null)
    {
        var (success, _) = await StartPowerShellWithModuleAndPipeNameAsync(agentId, startupCommands);
        return success;
    }

    /// <summary>
    /// Starts PowerShell process with PowerShell.MCP module imported and returns pipe name
    /// </summary>
    /// <param name="agentId">Agent ID for console isolation</param>
    /// <param name="startupCommands">Optional PowerShell commands to execute after module import (e.g. Write-Host statements)</param>
    /// <param name="startLocation">Starting directory path</param>
    /// <returns>Tuple of (success, pipeName)</returns>
    public static async Task<(bool Success, string PipeName)> StartPowerShellWithModuleAndPipeNameAsync(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        int pid = 0;
        HashSet<string>? existingPipes = null;

        // macOS/Linux: Capture existing pipes BEFORE launching
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var sessionManager = ConsoleSessionManager.Instance;
            existingPipes = sessionManager.EnumeratePipes(sessionManager.ProxyPid, agentId).ToHashSet();
        }

        var launcher = LauncherRegistry.Resolve();
        Console.Error.WriteLine($"[INFO] PowerShellProcessManager: using launcher '{launcher.Name}'");
        var maybePid = await launcher.LaunchPwshAsync(agentId, startupCommands, startLocation);
        pid = maybePid ?? 0;

        // Wait for Named Pipe to be ready
        string? pipeName;
        if (pid != 0)
        {
            // Windows: We know the PID, construct pipe name with proxy PID, agent ID, and pwsh PID
            var proxyPid = Process.GetCurrentProcess().Id;
            pipeName = ConsoleSessionManager.GetPipeNameForPids(proxyPid, agentId, pid);
        }
        else
        {
            // macOS/Linux: Poll for a NEW standby pipe (exclude existing pipes)
            pipeName = await WaitForNewStandbyPipeAsync(agentId, existingPipes!, maxWaitSeconds: 30);
            if (pipeName == null)
            {
                return (false, string.Empty);
            }
        }

        var success = await NamedPipeClient.WaitForPipeReadyAsync(pipeName);

        return (success, pipeName);
    }

    /// <summary>
    /// Waits for a NEW standby pipe to become available (for macOS/Linux)
    /// Polls every 500ms until a new standby pipe is found or timeout
    /// </summary>
    private static async Task<string?> WaitForNewStandbyPipeAsync(string agentId, HashSet<string> existingPipes, int maxWaitSeconds)
    {
        var endTime = DateTime.UtcNow.AddSeconds(maxWaitSeconds);
        Console.Error.WriteLine($"[DBG] WaitForNewStandbyPipe: proxyPid={ConsoleSessionManager.Instance.ProxyPid} agentId={agentId} existingPipes=[{string.Join(",", existingPipes)}] tmpdir={Environment.GetEnvironmentVariable("TMPDIR")}");

        int pollCount = 0;
        while (DateTime.UtcNow < endTime)
        {
            var pipe = await FindNewStandbyPipeAsync(agentId, existingPipes, pollCount++);
            if (pipe != null)
            {
                Console.Error.WriteLine($"[DBG] WaitForNewStandbyPipe: found pipe={pipe}");
                return pipe;
            }

            await Task.Delay(500);
        }

        Console.Error.WriteLine($"[DBG] WaitForNewStandbyPipe: TIMEOUT after {maxWaitSeconds}s (polls={pollCount})");
        return null;
    }

    /// <summary>
    /// Finds a NEW standby pipe from available pipes (excludes existing pipes)
    /// </summary>
    private static async Task<string?> FindNewStandbyPipeAsync(string agentId, HashSet<string> existingPipes, int pollIdx = 0)
    {
        var client = new NamedPipeClient();
        var enumerated = ConsoleSessionManager.Instance.EnumeratePipes(ConsoleSessionManager.Instance.ProxyPid, agentId).ToList();
        if (pollIdx < 3 || pollIdx % 10 == 0)
        {
            Console.Error.WriteLine($"[DBG] FindNewStandbyPipe poll#{pollIdx}: enumerated [{string.Join(",", enumerated)}]");
        }

        foreach (var pipe in enumerated)
        {
            // Skip pipes that existed before launching
            if (existingPipes.Contains(pipe))
            {
                continue;
            }

            try
            {
                var request = "{\"name\":\"get_status\"}";
                var response = await client.SendRequestToAsync(pipe, request);
                Console.Error.WriteLine($"[DBG] FindNewStandbyPipe: probe {pipe} -> response=[{response}] (len={response?.Length})");

                using var doc = System.Text.Json.JsonDocument.Parse(response!);
                var status = doc.RootElement.GetProperty("status").GetString();

                if (status == PipeStatus.Standby || status == PipeStatus.Completed)
                {
                    return pipe;
                }
                else
                {
                    Console.Error.WriteLine($"[DBG] FindNewStandbyPipe: pipe {pipe} returned status={status} — not ready");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DBG] FindNewStandbyPipe: probe {pipe} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        return null;
    }
}

/// <summary>
/// Windows-specific launcher using Win32 API to create a new console window
/// </summary>
public class PwshLauncherWindows : IPwshLauncher
{
    public string Name => "windows";
    public bool IsAvailable() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public Task<int?> LaunchPwshAsync(string agentId, string? startupCommands, string? startLocation)
        => Task.FromResult<int?>(LaunchPwsh(agentId, startupCommands, startLocation));

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    public int LaunchPwsh(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        IntPtr hToken = IntPtr.Zero;
        IntPtr env = IntPtr.Zero;
        IntPtr hProcess = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        int pid = 0;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out hToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // false = do not inherit current process environment
            // This uses only system/user default environment variables (Control Panel settings)
            if (!CreateEnvironmentBlock(out env, hToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var si = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
            var pi = new PROCESS_INFORMATION();

            // Build command with optional startup commands (pre-built Write-Host statements)
            // Set global variables with proxy PID and agent ID before importing module
            var proxyPid = Process.GetCurrentProcess().Id;
            string command;
            if (!string.IsNullOrEmpty(startupCommands))
            {
                command = $"$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Import-Module PSReadLine; {startupCommands}";
            }
            else
            {
                command = $"$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Import-Module PSReadLine";
            }
            string commandLine = $"pwsh.exe -NoExit -Command \"{command}\"";

            bool ok = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                env,
                startLocation ?? userProfile,
                ref si,
                out pi);

            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            pid = (int)pi.dwProcessId;
            hProcess = pi.hProcess;
            hThread = pi.hThread;
        }
        finally
        {
            if (env != IntPtr.Zero)
                DestroyEnvironmentBlock(env);

            if (hToken != IntPtr.Zero)
                CloseHandle(hToken);

            if (hProcess != IntPtr.Zero)
                CloseHandle(hProcess);

            if (hThread != IntPtr.Zero)
                CloseHandle(hThread);
        }

        return pid;
    }
}

/// <summary>
/// macOS-specific launcher using AppleScript to open Terminal.app
/// </summary>
public class PwshLauncherMacOS : IPwshLauncher
{
    public string Name => "macos";
    public bool IsAvailable() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public Task<int?> LaunchPwshAsync(string agentId, string? startupCommands, string? startLocation)
    {
        LaunchPwsh(agentId, startupCommands, startLocation);
        return Task.FromResult<int?>(null);
    }

    public void LaunchPwsh(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            RedirectStandardInput = true,
            // Redirect stdout/stderr so AppleScript's return value (e.g.
            // "tab 1 of window id N of application process \"Terminal\"") does not
            // leak into the Proxy's stdout, which is the MCP JSON-RPC channel.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Build the pwsh init script. Encode as UTF-16LE Base64 and pass via
        // -EncodedCommand so the three-layer quoting path (AppleScript -> shell -> pwsh)
        // doesn't mangle single quotes. Base64 contains only [A-Za-z0-9+/=], which is
        // safe at every layer without escaping.
        var proxyPid = Process.GetCurrentProcess().Id;

        var setLocation = string.IsNullOrEmpty(startLocation)
            ? "Set-Location ~; "
            : $"Set-Location -LiteralPath '{startLocation.Replace("'", "''")}'; ";

        var initCommand = string.IsNullOrEmpty(startupCommands)
            ? $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue"
            : $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue; {startupCommands}";

        var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(initCommand));

        using var process = Process.Start(psi);
        if (process != null)
        {
            // Terminal.app opens a new window with a login shell (typically zsh)
            // which reads ~/.zprofile and sets up the user's environment including PATH.
            // This ensures pwsh is found regardless of installation method (Homebrew, pkg, etc.).
            process.StandardInput.WriteLine("tell application \"Terminal\"");
            process.StandardInput.WriteLine("    activate");
            process.StandardInput.WriteLine($"    do script \"pwsh -NoExit -EncodedCommand {encodedCommand}\"");
            process.StandardInput.WriteLine("end tell");
            process.StandardInput.Close();
            // Drain redirected streams so osascript doesn't block on a full pipe.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
        }
    }
}

/// <summary>
/// Linux-specific launcher that tries multiple terminal emulators
/// </summary>
public class PwshLauncherLinux : IPwshLauncher
{
    public string Name => "linux";
    public bool IsAvailable() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public Task<int?> LaunchPwshAsync(string agentId, string? startupCommands, string? startLocation)
    {
        LaunchPwsh(agentId, startupCommands, startLocation);
        return Task.FromResult<int?>(null);
    }

    // Terminal emulator configurations: (name, useShellWrapper, args...)
    // useShellWrapper: true = use "sh -c" to wrap the command (for terminals that need a single command string)
    private static readonly string[] SupportedTerminals =
    [
        "gnome-terminal",
        "konsole",
        "xfce4-terminal",
        "xterm",
        "lxterminal",
        "mate-terminal",
        "terminator",
        "tilix",
        "alacritty",
        "kitty",
    ];

    public void LaunchPwsh(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        foreach (var terminal in SupportedTerminals)
        {
            if (TryLaunchTerminal(terminal, agentId, startupCommands, startLocation))
            {
                return;
            }
        }

        // No terminal emulator found - launch pwsh directly (headless/CI mode)
        Console.Error.WriteLine("[INFO] No terminal emulator found, launching pwsh directly");
        LaunchPwshDirectly(agentId, startupCommands, startLocation);
    }

    private static bool TryLaunchTerminal(string terminal, string agentId, string? startupCommands, string? startLocation)
    {
        try
        {
            // Check if terminal exists using 'which'
            var whichPsi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = terminal,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var whichProcess = Process.Start(whichPsi);
            whichProcess?.WaitForExit(2000);

            if (whichProcess?.ExitCode != 0)
            {
                return false;
            }

            // Get user's default shell
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";

            // Launch terminal via setsid to detach from parent process
            // We use the user's login shell to ensure ~/.bash_profile or ~/.zprofile is loaded,
            // which sets up PATH and other environment variables correctly.
            // This mimics what happens when a user manually opens a terminal and types 'pwsh'.
            var psi = new ProcessStartInfo
            {
                FileName = "setsid",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Build initialization command and encode to Base64 to avoid shell quoting issues
            // Set global variables with proxy PID and agent ID before importing module
            var proxyPid = Process.GetCurrentProcess().Id;
            // Fix module directory case sensitivity on Linux: Install-PSResource may create lowercase 'powershell.mcp'
            var caseFix = "foreach ($p in ($env:PSModulePath -split [IO.Path]::PathSeparator)) { if ([string]::IsNullOrWhiteSpace($p)) { continue }; $lc = Join-Path $p 'powershell.mcp'; $uc = Join-Path $p 'PowerShell.MCP'; if ((Test-Path $lc) -and -not (Test-Path $uc)) { Rename-Item $lc $uc; break } }; ";

            // Set working directory via Set-Location inside the command to avoid shell quoting issues
            var setLocation = string.IsNullOrEmpty(startLocation)
                ? "Set-Location ~; "
                : $"Set-Location -LiteralPath '{startLocation.Replace("'", "''")}'; ";

            string initCommand;
            if (!string.IsNullOrEmpty(startupCommands))
            {
                initCommand = $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; {caseFix}Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue; {startupCommands}";
            }
            else
            {
                initCommand = $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; {caseFix}Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue";
            }

            // Encode command to Base64 (UTF-16LE) to bypass shell quoting/expansion issues
            var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(initCommand));

            // Command to launch pwsh with encoded initialization via login shell
            // exec replaces the shell with pwsh to keep the process tree clean
            var pwshCommand = $"exec pwsh -NoExit -EncodedCommand {encodedCommand}";

            // setsid <terminal> ... <shell> -l -c '<pwshCommand>'
            psi.ArgumentList.Add(terminal);

            // Configure arguments based on terminal type
            switch (terminal)
            {
                case "gnome-terminal":
                    psi.ArgumentList.Add("--");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "konsole":
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "xterm":
                case "lxterminal":
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "xfce4-terminal":
                case "mate-terminal":
                case "terminator":
                case "tilix":
                    // These terminals expect -e with a single command string
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add($"{shell} -l -c '{pwshCommand}'");
                    break;

                case "alacritty":
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "kitty":
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                default:
                    return false;
            }

            Process.Start(psi)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches pwsh directly without a terminal emulator (for CI/headless environments)
    /// </summary>
    private static void LaunchPwshDirectly(string agentId, string? startupCommands, string? startLocation)
    {
        var proxyPid = Process.GetCurrentProcess().Id;
        // Fix module directory case sensitivity on Linux: Install-PSResource may create lowercase 'powershell.mcp'
        var caseFix = "foreach ($p in ($env:PSModulePath -split [IO.Path]::PathSeparator)) { if ([string]::IsNullOrWhiteSpace($p)) { continue }; $lc = Join-Path $p 'powershell.mcp'; $uc = Join-Path $p 'PowerShell.MCP'; if ((Test-Path $lc) -and -not (Test-Path $uc)) { Rename-Item $lc $uc; break } }; ";
        var initCommand = string.IsNullOrEmpty(startupCommands)
            ? $"$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; {caseFix}Import-Module PowerShell.MCP -Force"
            : $"$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{agentId}'; {caseFix}Import-Module PowerShell.MCP -Force; {startupCommands}";

        var workingDir = string.IsNullOrEmpty(startLocation)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : startLocation;

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-WorkingDirectory");
        psi.ArgumentList.Add(workingDir);
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(initCommand);

        var process = Process.Start(psi);
        if (process != null)
        {
            // Drain stdout/stderr asynchronously to prevent buffer deadlocks
            // Log to stderr (which is separate from MCP stdio transport)
            process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[HEADLESS] {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[HEADLESS-ERR] {e.Data}"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
    }
}
