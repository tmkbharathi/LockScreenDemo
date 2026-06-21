using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using LockScreenDemo.Shared;

namespace LockScreenDemo.Agent
{
    internal class Program
    {
        private static readonly string LogPath = @"C:\ProgramData\LockScreenDemo\agent_log.txt";
        private static readonly string ScreenshotPath = @"C:\ProgramData\LockScreenDemo\lockscreen.png";
        private static TcpListener? _server;
        private static bool _isRunning = true;

        // Clipboard synchronization state
        private static string _lastClipboardText = "";
        private static readonly object _clipboardLock = new object();

        static void Main(string[] args)
        {
            ConfigureDpiAwareness();

            // Global Exception Handler for debugging crashes
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log($"UNHANDLED EXCEPTION: {e.ExceptionObject}");
            };

            Log("Agent started inside user session.");

            // Start thread for local fallback screenshot capture
            Thread localCaptureThread = new Thread(LocalCaptureLoop);
            localCaptureThread.IsBackground = true;
            localCaptureThread.Start();

            // Start TCP Server
            Thread serverThread = new Thread(StartServer);
            serverThread.IsBackground = true;
            serverThread.Start();

            while (_isRunning)
            {
                Thread.Sleep(5000);
            }
        }

        private static void StartServer()
        {
            try
            {
                // Generate a self-signed SSL certificate in memory
                Log("Generating self-signed SSL/TLS certificate...");
                X509Certificate2 serverCertificate = GenerateSelfSignedCertificate();
                Log("Certificate generated successfully.");

                _server = new TcpListener(IPAddress.Any, 5800);
                _server.Start();
                Log("Secure TCP Remote Desktop Server started on port 5800.");

                while (_isRunning)
                {
                    TcpClient client = _server.AcceptTcpClient();
                    Log($"Client connection received from: {client.Client.RemoteEndPoint}");

                    // Handle client securely in a separate thread
                    Thread clientThread = new Thread(() => HandleClientSecure(client, serverCertificate));
                    clientThread.IsBackground = true;
                    clientThread.Start();
                }
            }
            catch (Exception ex)
            {
                Log($"TCP Server Error: {ex.Message}");
            }
        }

        private static void HandleClientSecure(TcpClient client, X509Certificate2 certificate)
        {
            SslStream? sslStream = null;
            try
            {
                sslStream = new SslStream(client.GetStream(), false);
                Log("Initiating SSL/TLS handshake...");
                sslStream.AuthenticateAsServer(certificate, false, System.Security.Authentication.SslProtocols.Tls13, false);
                Log("SSL/TLS handshake completed. Connection encrypted.");

                // Send host MAC address for Wake-on-LAN support
                try
                {
                    string mac = GetActiveMacAddress();
                    if (!string.IsNullOrEmpty(mac))
                    {
                        Log($"Sending host MAC address to client: {mac}");
                        byte[] macBytes = Encoding.UTF8.GetBytes(mac);
                        lock (sslStream)
                        {
                            ProtocolHelper.WritePacket(sslStream, PacketType.HostInfo, macBytes);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Failed to send host MAC address: {ex.Message}");
                }

                bool clientConnected = true;

                // 1. Spawning screen sender thread
                Thread senderThread = new Thread(() =>
                {
                    SwitchToInputDesktop(out _);
                    while (clientConnected && _isRunning)
                    {
                        try
                        {
                            SwitchToInputDesktop(out _);
                            byte[]? frameBytes = CaptureScreenJpeg();
                            if (frameBytes != null)
                            {
                                lock (sslStream)
                                {
                                    ProtocolHelper.WritePacket(sslStream, PacketType.ScreenFrame, frameBytes);
                                }
                            }
                        }
                        catch
                        {
                            break;
                        }
                        Thread.Sleep(80); // ~12 FPS
                    }
                });
                senderThread.IsBackground = true;
                senderThread.Start();

                // 2. Spawning clipboard monitor thread
                Thread clipboardThread = new Thread(() =>
                {
                    while (clientConnected && _isRunning)
                    {
                        try
                        {
                            SwitchToInputDesktop(out _);
                            string currentText = GetClipboardTextNative();
                            
                            lock (_clipboardLock)
                            {
                                if (currentText != _lastClipboardText && !string.IsNullOrEmpty(currentText))
                                {
                                    _lastClipboardText = currentText;
                                    byte[] textBytes = Encoding.UTF8.GetBytes(currentText);
                                    
                                    Log("Local clipboard changed. Sending update to client.");
                                    lock (sslStream)
                                    {
                                        ProtocolHelper.WritePacket(sslStream, PacketType.ClipboardSync, textBytes);
                                    }
                                }
                            }
                        }
                        catch
                        {
                            break;
                        }
                        Thread.Sleep(500); // Poll local clipboard twice per second
                    }
                });
                clipboardThread.IsBackground = true;
                clipboardThread.Start();

                // 3. Receive client loop
                while (clientConnected && _isRunning)
                {
                    PacketType packetType;
                    byte[] payload;

                    if (!ProtocolHelper.ReadPacket(sslStream, out packetType, out payload))
                    {
                        clientConnected = false;
                        break;
                    }

                    SwitchToInputDesktop(out _);

                    switch (packetType)
                    {
                        case PacketType.MouseInput:
                            MousePacket mPacket = MousePacket.Deserialize(payload);
                            SimulateMouse(mPacket.Type, mPacket.X, mPacket.Y, mPacket.WheelDelta);
                            break;

                        case PacketType.KeyboardInput:
                            KeyboardPacket kPacket = KeyboardPacket.Deserialize(payload);
                            SimulateKeyboard(kPacket.VirtualKeyCode, kPacket.ScanCode, kPacket.Flags, kPacket.IsKeyUp);
                            break;

                        case PacketType.ClipboardSync:
                            string receivedText = Encoding.UTF8.GetString(payload);
                            Log($"Received clipboard text from client (Length: {receivedText.Length}). Applying locally.");
                            
                            lock (_clipboardLock)
                            {
                                _lastClipboardText = receivedText;
                                SetClipboardTextNative(receivedText);
                            }
                            break;
                    }
                }

                clientConnected = false;
                senderThread.Join(1000);
                clipboardThread.Join(1000);
            }
            catch (Exception ex)
            {
                Log($"Secure Client Connection error: {ex.Message}");
            }
            finally
            {
                sslStream?.Dispose();
                client.Dispose();
                Log("Secure client connection closed.");
            }
        }

        private static X509Certificate2 GenerateSelfSignedCertificate()
        {
            RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("cn=LockScreenDemo", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            
            // Return the certificate with the private key directly bound to it.
            // This avoids both the disk-based MachineKeySet permissions issues (which cause 0xC0000005 crashes)
            // and the EphemeralKeySet SslStream handshake failures.
            return certificate.CopyWithPrivateKey(rsa);
        }

        private static void SimulateMouse(MouseMsgType type, int x, int y, int wheelDelta)
        {
            int screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

            if (screenWidth <= 0 || screenHeight <= 0) return;

            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_MOUSE;
            input.u.mi = new NativeMethods.MOUSEINPUT();

            input.u.mi.dx = (int)Math.Round((double)x * 65535 / (screenWidth - 1));
            input.u.mi.dy = (int)Math.Round((double)y * 65535 / (screenHeight - 1));

            uint flags = NativeMethods.MOUSEEVENTF_ABSOLUTE;

            switch (type)
            {
                case MouseMsgType.Move:
                    flags |= NativeMethods.MOUSEEVENTF_MOVE;
                    break;
                case MouseMsgType.LeftDown:
                    flags |= NativeMethods.MOUSEEVENTF_LEFTDOWN;
                    break;
                case MouseMsgType.LeftUp:
                    flags |= NativeMethods.MOUSEEVENTF_LEFTUP;
                    break;
                case MouseMsgType.RightDown:
                    flags |= NativeMethods.MOUSEEVENTF_RIGHTDOWN;
                    break;
                case MouseMsgType.RightUp:
                    flags |= NativeMethods.MOUSEEVENTF_RIGHTUP;
                    break;
                case MouseMsgType.Wheel:
                    flags |= NativeMethods.MOUSEEVENTF_WHEEL;
                    input.u.mi.mouseData = wheelDelta;
                    break;
            }

            input.u.mi.dwFlags = flags;
            NativeMethods.SendInput(1, new NativeMethods.INPUT[] { input }, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }

        private static void SimulateKeyboard(ushort virtualKey, ushort scanCode, uint flags, bool isKeyUp)
        {
            var input = new NativeMethods.INPUT();
            input.type = NativeMethods.INPUT_KEYBOARD;
            input.u.ki = new NativeMethods.KEYBDINPUT();
            
            input.u.ki.wVk = virtualKey;
            input.u.ki.wScan = scanCode;

            uint dwFlags = flags;
            if (isKeyUp)
            {
                dwFlags |= NativeMethods.KEYEVENTF_KEYUP;
            }
            input.u.ki.dwFlags = dwFlags;

            NativeMethods.SendInput(1, new NativeMethods.INPUT[] { input }, Marshal.SizeOf(typeof(NativeMethods.INPUT)));
        }

        private static string GetClipboardTextNative()
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero)) return "";
            IntPtr hData = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
            if (hData == IntPtr.Zero)
            {
                NativeMethods.CloseClipboard();
                return "";
            }
            IntPtr pText = NativeMethods.GlobalLock(hData);
            string text = "";
            if (pText != IntPtr.Zero)
            {
                text = Marshal.PtrToStringUni(pText) ?? "";
                NativeMethods.GlobalUnlock(hData);
            }
            NativeMethods.CloseClipboard();
            return text;
        }

        private static void SetClipboardTextNative(string text)
        {
            if (text == null) text = "";
            if (!NativeMethods.OpenClipboard(IntPtr.Zero)) return;
            NativeMethods.EmptyClipboard();
            
            int bytesCount = (text.Length + 1) * 2;
            IntPtr hGlobal = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytesCount);
            if (hGlobal != IntPtr.Zero)
            {
                IntPtr pText = NativeMethods.GlobalLock(hGlobal);
                if (pText != IntPtr.Zero)
                {
                    Marshal.Copy(text.ToCharArray(), 0, pText, text.Length);
                    Marshal.WriteInt16(pText, text.Length * 2, 0); // null terminator
                    NativeMethods.GlobalUnlock(hGlobal);
                    NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, hGlobal);
                }
            }
            NativeMethods.CloseClipboard();
        }

        private static byte[]? CaptureScreenJpeg()
        {
            IntPtr hDeskWnd = NativeMethods.GetDesktopWindow();
            IntPtr hDeskDC = NativeMethods.GetWindowDC(hDeskWnd);
            if (hDeskDC == IntPtr.Zero) return null; // Added safety check

            IntPtr hCompatibleDC = NativeMethods.CreateCompatibleDC(hDeskDC);
            int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

            if (width <= 0 || height <= 0)
            {
                NativeMethods.ReleaseDC(hDeskWnd, hDeskDC);
                return null;
            }

            IntPtr hBitmap = NativeMethods.CreateCompatibleBitmap(hDeskDC, width, height);
            if (hCompatibleDC == IntPtr.Zero || hBitmap == IntPtr.Zero)
            {
                if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
                if (hCompatibleDC != IntPtr.Zero) NativeMethods.DeleteDC(hCompatibleDC);
                NativeMethods.ReleaseDC(hDeskWnd, hDeskDC);
                return null;
            }

            IntPtr hOldBitmap = NativeMethods.SelectObject(hCompatibleDC, hBitmap);
            bool success = NativeMethods.BitBlt(hCompatibleDC, 0, 0, width, height, hDeskDC, 0, 0, NativeMethods.SRCCOPY);

            if (success)
            {
                // Draw mouse cursor on top of screen capture
                try
                {
                    var cursorInfo = new NativeMethods.CURSORINFO();
                    cursorInfo.cbSize = Marshal.SizeOf(cursorInfo);
                    if (NativeMethods.GetCursorInfo(out cursorInfo) && cursorInfo.flags == NativeMethods.CURSOR_SHOWING)
                    {
                        var iconInfo = new NativeMethods.ICONINFO();
                        if (NativeMethods.GetIconInfo(cursorInfo.hCursor, out iconInfo))
                        {
                            int x = cursorInfo.ptScreenPos.x - iconInfo.xHotspot;
                            int y = cursorInfo.ptScreenPos.y - iconInfo.yHotspot;
                            NativeMethods.DrawIcon(hCompatibleDC, x, y, cursorInfo.hCursor);
                            
                            // Delete GDI objects allocated by GetIconInfo to prevent resource leaks
                            if (iconInfo.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.hbmMask);
                            if (iconInfo.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(iconInfo.hbmColor);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Exception drawing cursor: {ex.Message}");
                }
            }

            byte[]? jpegBytes = null;
            if (success)
            {
                try
                {
                    using (System.Drawing.Bitmap bmp = System.Drawing.Bitmap.FromHbitmap(hBitmap))
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                        jpegBytes = ms.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    Log($"Exception converting GDI bitmap: {ex.Message}");
                }
            }

            NativeMethods.SelectObject(hCompatibleDC, hOldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(hCompatibleDC);
            NativeMethods.ReleaseDC(hDeskWnd, hDeskDC);

            return jpegBytes;
        }

        private static void LocalCaptureLoop()
        {
            int errorCount = 0;
            while (_isRunning)
            {
                try
                {
                    bool desktopSwitched = SwitchToInputDesktop(out _);
                    if (desktopSwitched)
                    {
                        byte[]? frame = CaptureScreenJpeg();
                        if (frame != null)
                        {
                            string? dir = Path.GetDirectoryName(ScreenshotPath);
                            if (dir != null && !Directory.Exists(dir))
                            {
                                Directory.CreateDirectory(dir);
                            }

                            using (var fs = new FileStream(ScreenshotPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                            {
                                fs.Write(frame, 0, frame.Length);
                            }
                        }
                        errorCount = 0;
                    }
                    else
                    {
                        errorCount++;
                        if (errorCount % 10 == 0 || errorCount == 1)
                        {
                            Log($"Failed to open input desktop. Error: {Marshal.GetLastWin32Error()}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Exception in local capture loop: {ex.Message}");
                }

                Thread.Sleep(1000);
            }
        }

        private static bool SwitchToInputDesktop(out string desktopName)
        {
            desktopName = "Unknown";
            IntPtr hInputDesktop = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_ALL);
            if (hInputDesktop == IntPtr.Zero)
            {
                return false;
            }

            desktopName = GetUserObjectName(hInputDesktop);
            bool success = NativeMethods.SetThreadDesktop(hInputDesktop);
            NativeMethods.CloseDesktop(hInputDesktop);
            return success;
        }

        private static string GetUserObjectName(IntPtr hObj)
        {
            const int UOI_NAME = 2;
            int length = 0;
            GetUserObjectInformationW(hObj, UOI_NAME, IntPtr.Zero, 0, out length);
            if (length > 0)
            {
                IntPtr lpszName = Marshal.AllocHGlobal(length);
                if (GetUserObjectInformationW(hObj, UOI_NAME, lpszName, length, out length))
                {
                    string name = Marshal.PtrToStringUni(lpszName) ?? "Unknown";
                    Marshal.FreeHGlobal(lpszName);
                    return name;
                }
                Marshal.FreeHGlobal(lpszName);
            }
            return "Unknown";
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static void ConfigureDpiAwareness()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
                if (!SetProcessDpiAwarenessContext((IntPtr)(-4)))
                {
                    SetProcessDPIAware();
                }
            }
            catch
            {
                try
                {
                    SetProcessDPIAware();
                }
                catch { }
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, IntPtr pvInfo, int nLength, out int lpnLengthNeeded);

        private static string GetActiveMacAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Up &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        string mac = nic.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(mac)) return mac;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to get MAC address: {ex.Message}");
            }
            return "";
        }

        private static void Log(string message)
        {
            try
            {
                string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                Console.WriteLine(message);
                File.AppendAllText(LogPath, logLine);
            }
            catch
            {
                // Ignore log errors
            }
        }
    }
}
