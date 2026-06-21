using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LockScreenDemo.Shared;

namespace LockScreenDemo.Service
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private uint _currentActiveSessionId = uint.MaxValue;
        private IntPtr _agentProcessHandle = IntPtr.Zero;
        private uint _agentPid = 0;

        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LockScreenDemo Windows Service started.");

            // Create shared local folder for screenshots and logs
            try
            {
                string sharedDir = @"C:\ProgramData\LockScreenDemo";
                if (!Directory.Exists(sharedDir))
                {
                    Directory.CreateDirectory(sharedDir);
                }
                File.WriteAllText(Path.Combine(sharedDir, "service_status.txt"), $"Started at {DateTime.Now}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create ProgramData directory.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    uint activeSessionId = NativeMethods.WTSGetActiveConsoleSessionId();
                    
                    if (activeSessionId == uint.MaxValue)
                    {
                        // No active console session (e.g. system starting up or locked without anyone logged in)
                        _logger.LogDebug("No active console session found.");
                    }
                    else if (activeSessionId != _currentActiveSessionId || IsAgentDead())
                    {
                        _logger.LogInformation($"Active session changed from {_currentActiveSessionId} to {activeSessionId} (or Agent died). Re-launching Agent.");
                        KillAgent();
                        _currentActiveSessionId = activeSessionId;
                        LaunchAgentInSession(activeSessionId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in service loop.");
                }

                await Task.Delay(2000, stoppingToken);
            }

            // Cleanup when service stops
            KillAgent();
            _logger.LogInformation("LockScreenDemo Windows Service stopped.");
        }

        private bool IsAgentDead()
        {
            if (_agentProcessHandle == IntPtr.Zero) return true;

            // Check if process has exited
            const uint STILL_ACTIVE = 259;
            if (GetExitCodeProcess(_agentProcessHandle, out uint exitCode))
            {
                if (exitCode != STILL_ACTIVE)
                {
                    _logger.LogWarning($"Agent process exited with code {exitCode}.");
                    NativeMethods.CloseHandle(_agentProcessHandle);
                    _agentProcessHandle = IntPtr.Zero;
                    _agentPid = 0;
                    return true;
                }
                return false;
            }

            return true;
        }

        private void KillAgent()
        {
            if (_agentPid != 0)
            {
                try
                {
                    var proc = Process.GetProcessById((int)_agentPid);
                    _logger.LogInformation($"Killing existing Agent process (PID {_agentPid}).");
                    proc.Kill();
                }
                catch (Exception)
                {
                    // Process might already be dead
                }
                _agentPid = 0;
            }

            if (_agentProcessHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_agentProcessHandle);
                _agentProcessHandle = IntPtr.Zero;
            }
        }

        private void LaunchAgentInSession(uint sessionId)
        {
            _logger.LogInformation($"Attempting to launch Agent in Session {sessionId}...");

            IntPtr hProcess = IntPtr.Zero;
            IntPtr hToken = IntPtr.Zero;
            IntPtr hTokenDup = IntPtr.Zero;
            IntPtr lpEnv = IntPtr.Zero;

            try
            {
                // Find winlogon process for the specified session
                uint winlogonPid = 0;
                var processes = Process.GetProcessesByName("winlogon");
                foreach (var p in processes)
                {
                    if (NativeMethods.ProcessIdToSessionId((uint)p.Id, out uint sid) && sid == sessionId)
                    {
                        winlogonPid = (uint)p.Id;
                        break;
                    }
                }

                if (winlogonPid == 0)
                {
                    _logger.LogError($"Could not find winlogon.exe process in Session {sessionId}. Retrying later.");
                    return;
                }

                _logger.LogInformation($"Found winlogon.exe in Session {sessionId} with PID {winlogonPid}. Duplicating token...");

                // Open the winlogon process
                hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_ALL_ACCESS, false, winlogonPid);
                if (hProcess == IntPtr.Zero)
                {
                    _logger.LogError($"Failed to open process winlogon.exe. Error: {Marshal.GetLastWin32Error()}");
                    return;
                }

                // Get the token from winlogon process
                if (!NativeMethods.OpenProcessToken(hProcess, NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_ASSIGN_PRIMARY | NativeMethods.TOKEN_QUERY, out hToken))
                {
                    _logger.LogError($"Failed to open process token. Error: {Marshal.GetLastWin32Error()}");
                    return;
                }

                // Duplicate the token to create a primary token
                if (!NativeMethods.DuplicateTokenEx(
                    hToken,
                    NativeMethods.TOKEN_ALL_ACCESS,
                    IntPtr.Zero,
                    NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    NativeMethods.TOKEN_TYPE.TokenPrimary,
                    out hTokenDup))
                {
                    _logger.LogError($"Failed to duplicate token. Error: {Marshal.GetLastWin32Error()}");
                    return;
                }

                // Create environment block
                if (!NativeMethods.CreateEnvironmentBlock(out lpEnv, hTokenDup, true))
                {
                    _logger.LogWarning($"Failed to create environment block. Error: {Marshal.GetLastWin32Error()}. Spawning without environment block.");
                }

                // Set up process parameters
                var si = new NativeMethods.STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                si.lpDesktop = @"winsta0\default"; // Spawns inside winsta0

                // Resolve path to the Agent executable
                string baseDir = AppContext.BaseDirectory;
                string agentPath = Path.Combine(baseDir, "LockScreenDemo.Agent.exe");

                // If running in development target folders, check peer folders
                if (!File.Exists(agentPath))
                {
                    agentPath = Path.Combine(baseDir, "..", "LockScreenDemo.Agent", "bin", "Debug", "net10.0-windows", "LockScreenDemo.Agent.exe");
                    if (!File.Exists(agentPath))
                    {
                        agentPath = Path.Combine(baseDir, "LockScreenDemo.Agent.exe"); // Fallback
                    }
                }

                _logger.LogInformation($"Spawning Agent at: {agentPath}");

                var pi = new NativeMethods.PROCESS_INFORMATION();

                // Create the agent process as the duplicated user (SYSTEM) in Session ID
                bool success = NativeMethods.CreateProcessAsUserW(
                    hTokenDup,
                    agentPath,
                    null,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.DETACHED_PROCESS,
                    IntPtr.Zero,
                    null,
                    ref si,
                    out pi);

                if (!success)
                {
                    _logger.LogError($"CreateProcessAsUserW failed. Error: {Marshal.GetLastWin32Error()}");
                    return;
                }

                _agentProcessHandle = pi.hProcess;
                _agentPid = (uint)pi.dwProcessId;
                NativeMethods.CloseHandle(pi.hThread);

                _logger.LogInformation($"Agent successfully spawned! PID: {_agentPid}");

                // Update shared file for Viewer to know
                File.WriteAllText(@"C:\ProgramData\LockScreenDemo\agent_info.txt", $"PID:{_agentPid}\nSession:{sessionId}\nTime:{DateTime.Now}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in LaunchAgentInSession.");
            }
            finally
            {
                if (hProcess != IntPtr.Zero) NativeMethods.CloseHandle(hProcess);
                if (hToken != IntPtr.Zero) NativeMethods.CloseHandle(hToken);
                if (hTokenDup != IntPtr.Zero) NativeMethods.CloseHandle(hTokenDup);
                if (lpEnv != IntPtr.Zero) NativeMethods.DestroyEnvironmentBlock(lpEnv);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    }
}
