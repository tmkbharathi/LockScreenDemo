using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LockScreenDemo.Shared;

namespace LockScreenDemo.Viewer
{
    public partial class MainWindow : Window
    {
        private const string SharedDir = @"C:\ProgramData\LockScreenDemo";
        private static readonly string ServiceStatusFile = Path.Combine(SharedDir, "service_status.txt");
        private static readonly string AgentInfoFile = Path.Combine(SharedDir, "agent_info.txt");
        private static readonly string AgentLogFile = Path.Combine(SharedDir, "agent_log.txt");
        private static readonly string ScreenshotFile = Path.Combine(SharedDir, "lockscreen.png");
        private static readonly string SavedHostsFile = Path.Combine(SharedDir, "saved_hosts.txt");

        private readonly DispatcherTimer _timer;

        // Network connection state
        private TcpClient? _tcpClient;
        private SslStream? _sslStream;
        private bool _isConnected = false;
        private Thread? _receiverThread;
        private Thread? _clipboardThread;

        // Clipboard sync tracking
        private string _lastClipboardText = "";
        private readonly object _networkLock = new object();

        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        public MainWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Perform initial tick
            Timer_Tick(this, EventArgs.Empty);
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdateStatus();
            LoadScreenshot();
            LoadLogs();
        }

        private void UpdateStatus()
        {
            bool isServiceRunning = Process.GetProcessesByName("LockScreenDemo.Service").Length > 0;
            if (isServiceRunning)
            {
                ServiceStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                ServiceStatusTxt.Text = "Running (Active)";
            }
            else
            {
                ServiceStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                ServiceStatusTxt.Text = "Stopped / Not Installed";
            }

            bool isAgentRunning = Process.GetProcessesByName("LockScreenDemo.Agent").Length > 0;
            if (isAgentRunning)
            {
                AgentStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                
                string agentInfoStr = "Running (Active)";
                if (File.Exists(AgentInfoFile))
                {
                    try
                    {
                        var lines = File.ReadAllLines(AgentInfoFile);
                        string pid = "";
                        string session = "";
                        foreach (var l in lines)
                        {
                            if (l.StartsWith("PID:")) pid = l.Substring(4);
                            if (l.StartsWith("Session:")) session = l.Substring(8);
                        }
                        if (!string.IsNullOrEmpty(pid)) agentInfoStr += $" (PID: {pid})";
                        if (!string.IsNullOrEmpty(session)) SessionIdTxt.Text = session;
                    }
                    catch { }
                }
                AgentStatusTxt.Text = agentInfoStr;
            }
            else
            {
                AgentStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                AgentStatusTxt.Text = "Not Running";
                SessionIdTxt.Text = "-";
            }
        }

        private void LoadScreenshot()
        {
            if (_isConnected) return;

            if (File.Exists(ScreenshotFile))
            {
                try
                {
                    byte[] imageBytes = File.ReadAllBytes(ScreenshotFile);
                    using (var ms = new MemoryStream(imageBytes))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();

                        LockScreenImg.Source = bitmap;
                        NoFeedTxt.Visibility = Visibility.Collapsed;
                        
                        DateTime lastWrite = File.GetLastWriteTime(ScreenshotFile);
                        LastCaptureTxt.Text = lastWrite.ToString("HH:mm:ss") + $" ({Math.Round((DateTime.Now - lastWrite).TotalSeconds)}s ago) [Local Poll]";
                    }
                }
                catch
                {
                    // Ignore transient lock conflicts
                }
            }
            else
            {
                LockScreenImg.Source = null;
                NoFeedTxt.Visibility = Visibility.Visible;
                LastCaptureTxt.Text = "Never";
            }
        }

        private void LoadLogs()
        {
            if (File.Exists(AgentLogFile))
            {
                try
                {
                    using (var fs = new FileStream(AgentLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        string logs = sr.ReadToEnd();
                        LogsBox.Text = logs;
                        LogsBox.ScrollToEnd();
                    }
                }
                catch
                {
                    // Ignore transient lock conflicts
                }
            }
            else
            {
                LogsBox.Text = "Waiting for Agent logs...";
            }
        }

        private void LockBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool success = LockWorkStation();
                if (!success)
                {
                    MessageBox.Show("Failed to trigger Windows Lock screen.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Exception locking workstation: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // --- SECURE TCP CONNECTION CONTROLS ---

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            string ipAddress = IpInput.Text.Trim();
            if (string.IsNullOrEmpty(ipAddress))
            {
                MessageBox.Show("Please enter a valid IP Address.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                LogsBox.Text += $"\nConnecting securely to remote Agent at {ipAddress}:5800...\n";
                _tcpClient = new TcpClient();
                _tcpClient.Connect(ipAddress, 5800);
                
                // Wrap in SslStream and trust the self-signed certificate explicitly
                _sslStream = new SslStream(_tcpClient.GetStream(), false, (s, cert, chain, errs) => true);
                
                Log("Initiating SSL/TLS handshake...");
                _sslStream.AuthenticateAsClient(ipAddress);
                _isConnected = true;
                Log("SSL/TLS encrypted connection established!");

                // Stop local file polling
                _timer.Stop();

                // UI adjustments
                ConnectBtn.Visibility = Visibility.Collapsed;
                DisconnectBtn.Visibility = Visibility.Visible;
                IpInput.IsEnabled = false;
                WakeBtn.IsEnabled = false;
                LockScreenImg.Cursor = Cursors.Cross;
                NoFeedTxt.Text = "Connecting and waiting for secure screen stream...";

                // Start reader thread for secure packets
                _receiverThread = new Thread(ReceivePacketsLoop);
                _receiverThread.IsBackground = true;
                _receiverThread.Start();

                // Start clipboard monitor thread
                _clipboardThread = new Thread(MonitorClipboardLoop);
                _clipboardThread.IsBackground = true;
                _clipboardThread.Start();

                LogsBox.Text += "Secure Connection Successful!\n";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect securely: {ex.Message}", "Connection Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Disconnect();
            }
        }

        private void DisconnectBtn_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private void Disconnect()
        {
            _isConnected = false;
            
            try
            {
                _sslStream?.Close();
                _tcpClient?.Close();
            }
            catch { }

            _sslStream = null;
            _tcpClient = null;

            // UI adjustments
            ConnectBtn.Visibility = Visibility.Visible;
            DisconnectBtn.Visibility = Visibility.Collapsed;
            IpInput.IsEnabled = true;
            LockScreenImg.Cursor = Cursors.Arrow;
            NoFeedTxt.Text = "Disconnected. Enter IP to reconnect.";
            UpdateWakeButtonState();

            // Restart local polling
            _timer.Start();

            LogsBox.Text += "Secure connection closed.\n";
        }

        private void ReceivePacketsLoop()
        {
            while (_isConnected)
            {
                try
                {
                    if (_sslStream == null) break;

                    PacketType type;
                    byte[] payload;

                    if (!ProtocolHelper.ReadPacket(_sslStream, out type, out payload))
                    {
                        throw new IOException("Connection closed by host.");
                    }

                    if (type == PacketType.ScreenFrame)
                    {
                        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                        {
                            try
                            {
                                using (var ms = new MemoryStream(payload))
                                {
                                    var bitmap = new BitmapImage();
                                    bitmap.BeginInit();
                                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                    bitmap.StreamSource = ms;
                                    bitmap.EndInit();

                                    LockScreenImg.Source = bitmap;
                                    NoFeedTxt.Visibility = Visibility.Collapsed;
                                    LastCaptureTxt.Text = DateTime.Now.ToString("HH:mm:ss") + " [Live SSL Stream]";
                                }
                            }
                            catch { }
                        }));
                    }
                    else if (type == PacketType.ClipboardSync)
                    {
                        string text = Encoding.UTF8.GetString(payload);
                        
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                _lastClipboardText = text;
                                Clipboard.SetText(text);
                                LogsBox.Text += $"\n[{DateTime.Now:HH:mm:ss}] Clipboard synchronized from remote (Length: {text.Length})\n";
                                LogsBox.ScrollToEnd();
                            }
                            catch { }
                        }));
                    }
                    else if (type == PacketType.HostInfo)
                    {
                        string mac = Encoding.UTF8.GetString(payload);
                        string remoteIp = "127.0.0.1";
                        if (_tcpClient != null && _tcpClient.Client != null && _tcpClient.Client.RemoteEndPoint is IPEndPoint remoteEp)
                        {
                            remoteIp = remoteEp.Address.ToString();
                        }
                        
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log($"Received remote MAC address: {mac}");
                            SaveMacAddress(remoteIp, mac);
                        }));
                    }
                }
                catch
                {
                    Dispatcher.BeginInvoke(new Action(() => Disconnect()));
                    break;
                }
            }
        }

        private void MonitorClipboardLoop()
        {
            while (_isConnected)
            {
                try
                {
                    // Query clipboard text on the UI thread dispatcher
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            string localText = Clipboard.GetText();
                            if (localText != _lastClipboardText && !string.IsNullOrEmpty(localText))
                            {
                                _lastClipboardText = localText;
                                byte[] payload = Encoding.UTF8.GetBytes(localText);
                                
                                Log($"Clipboard updated locally. Syncing to remote host...");
                                if (_sslStream != null)
                                {
                                    lock (_networkLock)
                                    {
                                        ProtocolHelper.WritePacket(_sslStream, PacketType.ClipboardSync, payload);
                                    }
                                }
                            }
                        }
                        catch { }
                    });
                }
                catch
                {
                    break;
                }
                Thread.Sleep(500);
            }
        }

        private void Log(string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogsBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
                LogsBox.ScrollToEnd();
            }));
        }

        // --- INTERACTIVE INPUT ROUTING ---

        private void SendMouseInput(MouseMsgType type, MouseEventArgs e)
        {
            if (!_isConnected || _sslStream == null) return;
            if (LockScreenImg.Source is not BitmapSource bitmapSource) return;

            Point p = e.GetPosition(LockScreenImg);

            double imageWidth = bitmapSource.PixelWidth;
            double imageHeight = bitmapSource.PixelHeight;
            double actualWidth = LockScreenImg.ActualWidth;
            double actualHeight = LockScreenImg.ActualHeight;

            double ratioX = actualWidth / imageWidth;
            double ratioY = actualHeight / imageHeight;
            double ratio = Math.Min(ratioX, ratioY);

            double renderWidth = imageWidth * ratio;
            double renderHeight = imageHeight * ratio;

            double dx = (actualWidth - renderWidth) / 2;
            double dy = (actualHeight - renderHeight) / 2;

            double relativeX = p.X - dx;
            double relativeY = p.Y - dy;

            if (relativeX >= 0 && relativeX <= renderWidth && relativeY >= 0 && relativeY <= renderHeight)
            {
                int remoteX = (int)Math.Round(relativeX * imageWidth / renderWidth);
                int remoteY = (int)Math.Round(relativeY * imageHeight / renderHeight);

                int wheelDelta = 0;
                if (e is MouseWheelEventArgs wArgs)
                {
                    wheelDelta = wArgs.Delta;
                }

                var packet = new MousePacket
                {
                    Type = type,
                    X = remoteX,
                    Y = remoteY,
                    WheelDelta = wheelDelta
                };

                try
                {
                    byte[] data = packet.Serialize();
                    lock (_networkLock)
                    {
                        ProtocolHelper.WritePacket(_sslStream, PacketType.MouseInput, data);
                    }
                }
                catch
                {
                    Disconnect();
                }
            }
        }

        private void LockScreenImg_MouseDown(object sender, MouseButtonEventArgs e)
        {
            MouseMsgType? type = null;
            if (e.ChangedButton == MouseButton.Left) type = MouseMsgType.LeftDown;
            if (e.ChangedButton == MouseButton.Right) type = MouseMsgType.RightDown;

            if (type.HasValue)
            {
                LockScreenImg.CaptureMouse();
                SendMouseInput(type.Value, e);
                e.Handled = true;
                
                // Force focus on the image view to ensure Key Events are routed here
                this.Focus();
            }
        }

        private void LockScreenImg_MouseUp(object sender, MouseButtonEventArgs e)
        {
            MouseMsgType? type = null;
            if (e.ChangedButton == MouseButton.Left) type = MouseMsgType.LeftUp;
            if (e.ChangedButton == MouseButton.Right) type = MouseMsgType.RightUp;

            if (type.HasValue)
            {
                SendMouseInput(type.Value, e);
                LockScreenImg.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void LockScreenImg_MouseMove(object sender, MouseEventArgs e)
        {
            SendMouseInput(MouseMsgType.Move, e);
            e.Handled = true;
        }

        private void LockScreenImg_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            SendMouseInput(MouseMsgType.Wheel, e);
            e.Handled = true;
        }

        // --- KEYBOARD CAPTURE ---

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Do not capture keyboard inputs if the user is typing inside the IP input text box!
            if (IpInput.IsFocused) return;

            if (_isConnected && _sslStream != null)
            {
                int vk = KeyInterop.VirtualKeyFromKey(e.Key);
                
                // Construct keyboard packet
                var packet = new KeyboardPacket
                {
                    VirtualKeyCode = (ushort)vk,
                    ScanCode = 0,
                    Flags = 0,
                    IsKeyUp = false
                };

                try
                {
                    byte[] payload = packet.Serialize();
                    lock (_networkLock)
                    {
                        ProtocolHelper.WritePacket(_sslStream, PacketType.KeyboardInput, payload);
                    }
                }
                catch
                {
                    Disconnect();
                }

                // Prevent key event from triggering local shortcuts or navigation in the Viewer app
                e.Handled = true;
            }
        }

        private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (IpInput.IsFocused) return;

            if (_isConnected && _sslStream != null)
            {
                int vk = KeyInterop.VirtualKeyFromKey(e.Key);
                
                var packet = new KeyboardPacket
                {
                    VirtualKeyCode = (ushort)vk,
                    ScanCode = 0,
                    Flags = 0,
                    IsKeyUp = true
                };

                try
                {
                    byte[] payload = packet.Serialize();
                    lock (_networkLock)
                    {
                        ProtocolHelper.WritePacket(_sslStream, PacketType.KeyboardInput, payload);
                    }
                }
                catch
                {
                    Disconnect();
                }

                e.Handled = true;
            }
        }

        private void IpInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateWakeButtonState();
        }

        private void WakeBtn_Click(object sender, RoutedEventArgs e)
        {
            string ip = IpInput.Text.Trim();
            string mac = GetSavedMacAddress(ip);
            if (!string.IsNullOrEmpty(mac))
            {
                Log($"Attempting to Wake-on-LAN remote host {ip} [MAC: {mac}]...");
                try
                {
                    SendWakeOnLan(mac);
                    Log("Wake-on-LAN Magic Packet broadcast sent!");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to send Wake-on-LAN packet: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("No MAC address found for this IP. Connect at least once to save the remote MAC address.", "Wake-on-LAN", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void UpdateWakeButtonState()
        {
            if (WakeBtn == null || IpInput == null) return;
            if (_isConnected)
            {
                WakeBtn.IsEnabled = false;
                return;
            }
            string ip = IpInput.Text.Trim();
            string mac = GetSavedMacAddress(ip);
            WakeBtn.IsEnabled = !string.IsNullOrEmpty(mac);
        }

        private string GetSavedMacAddress(string ip)
        {
            if (!File.Exists(SavedHostsFile)) return "";
            try
            {
                var lines = File.ReadAllLines(SavedHostsFile);
                foreach (var line in lines)
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2 && parts[0].Trim().Equals(ip, StringComparison.OrdinalIgnoreCase))
                    {
                        return parts[1].Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Error reading saved MAC addresses: {ex.Message}");
            }
            return "";
        }

        private void SaveMacAddress(string ip, string mac)
        {
            try
            {
                var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(SavedHostsFile))
                {
                    var lines = File.ReadAllLines(SavedHostsFile);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            hosts[parts[0].Trim()] = parts[1].Trim();
                        }
                    }
                }

                hosts[ip] = mac;

                var outLines = new List<string>();
                foreach (var kvp in hosts)
                {
                    outLines.Add($"{kvp.Key}={kvp.Value}");
                }

                File.WriteAllLines(SavedHostsFile, outLines);
                Log($"Saved/Updated MAC mapping: {ip} -> {mac}");
                UpdateWakeButtonState();
            }
            catch (Exception ex)
            {
                Log($"Error saving MAC address: {ex.Message}");
            }
        }

        private void SendWakeOnLan(string macAddress)
        {
            string cleanMac = macAddress.Replace("-", "").Replace(":", "");
            if (cleanMac.Length != 12)
            {
                throw new ArgumentException("MAC address must be 12 hexadecimal characters long.");
            }

            byte[] macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                macBytes[i] = byte.Parse(cleanMac.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
            }

            byte[] packetContent = new byte[102];
            for (int i = 0; i < 6; i++)
            {
                packetContent[i] = 0xFF;
            }
            for (int i = 1; i <= 16; i++)
            {
                Buffer.BlockCopy(macBytes, 0, packetContent, i * 6, 6);
            }

            using (UdpClient client = new UdpClient())
            {
                client.Connect(IPAddress.Broadcast, 9);
                client.Send(packetContent, packetContent.Length);
            }
        }
    }
}