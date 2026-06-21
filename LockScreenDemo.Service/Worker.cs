using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

            // Ensure certificate is installed in LocalMachine store
            EnsureServerCertificateInstalled();

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
                // Open the current process token
                IntPtr hCurrentProcess = NativeMethods.GetCurrentProcess();
                if (!NativeMethods.OpenProcessToken(hCurrentProcess, NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_ASSIGN_PRIMARY | NativeMethods.TOKEN_QUERY, out hToken))
                {
                    _logger.LogError($"Failed to open current process token. Error: {Marshal.GetLastWin32Error()}");
                    return;
                }

                _logger.LogInformation("Successfully opened current process token. Duplicating token...");

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

                // Set token session ID to target session
                _logger.LogInformation($"Setting duplicated token Session ID to {sessionId}...");
                uint targetSessionId = sessionId;
                if (!NativeMethods.SetTokenInformation(
                    hTokenDup,
                    NativeMethods.TokenSessionId,
                    ref targetSessionId,
                    sizeof(uint)))
                {
                    _logger.LogError($"SetTokenInformation failed to set Session ID. Error: {Marshal.GetLastWin32Error()}");
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
                    NativeMethods.DETACHED_PROCESS | NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                    lpEnv,
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

        private void EnsureServerCertificateInstalled()
        {
            try
            {
                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    var certs = store.Certificates.Find(X509FindType.FindBySubjectName, "LockScreenDemo", false);
                    if (certs.Count > 0)
                    {
                        _logger.LogInformation("LockScreenDemo certificate is already installed in LocalMachine store.");
                        return;
                    }

                    _logger.LogInformation("Generating and installing LockScreenDemo certificate in LocalMachine store...");
                    using (RSA rsa = RSA.Create(2048))
                    {
                        var request = new CertificateRequest("cn=LockScreenDemo", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                        using (X509Certificate2 tempCert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)))
                        {
                            byte[] pfxData = tempCert.Export(X509ContentType.Pkcs12, "password");
                            var cert = new X509Certificate2(pfxData, "password", X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                            store.Add(cert);
                        }
                    }
                    _logger.LogInformation("Successfully installed LockScreenDemo certificate in LocalMachine store.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ensure LockScreenDemo certificate is installed in LocalMachine store.");
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
    }
}
