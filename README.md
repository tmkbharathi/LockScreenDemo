# LockScreenDemo - Windows Lock Screen Access & Remote Control PoC

This project is a complete C# (.NET 10) Proof-of-Concept demonstrating the **Windows Service-Agent Session Token Duplication** architecture. It shows how a background service can access, stream, and control a Windows session—even when the screen is locked, logged out, or displaying a Secure UAC (User Account Control) prompt.

---

## Technical Architecture

The project consists of three main components:

1. **Windows Service (`LockScreenDemo.Service`)**:
   * Runs in **Session 0** with `LocalSystem` privileges.
   * Monitors active session IDs. When a session is active, it locates `winlogon.exe` (the logon interface) for that session, duplicates its security token, and calls `CreateProcessAsUserW` to spawn the Agent inside that interactive session under the `SYSTEM` context.
2. **Session Agent (`LockScreenDemo.Agent`)**:
   * Runs inside the active user session as `SYSTEM`.
   * **TCP SSL Server**: Listens on port `5800` using secure `SslStream` with an in-memory self-signed certificate.
   * **Desktop Switcher**: Regularly calls `OpenInputDesktop` and `SetThreadDesktop` to bind itself to the active desktop context (e.g. `Default` when unlocked, `Winlogon` when locked).
   * **Screen Streamer & Input Injector**: Captures screen frames using GDI, compresses them into JPEGs, and streams them over SSL. Receives keyboard/mouse commands and simulates them using the `SendInput` API.
   * **Native Clipboard Sync**: Hooks and synchronizes clipboard text using native Win32 clipboard functions.
3. **Viewer Client (`LockScreenDemo.Viewer`)**:
   * An interactive, dark-themed WPF application.
   * Connects to port `5800` using `SslStream` (trusting the self-signed cert).
   * Renders the real-time screen stream and intercepts mouse moves, clicks, scrolls, and keystrokes, routing them over the network.
   * Translates client viewport coordinates to remote coordinates using aspect-ratio letterbox compensation.
   * Syncs the local client clipboard with the remote host clipboard.

---

## Folder Structure

* `LockScreenDemo.slnx` - Visual Studio XML Solution file.
* **`LockScreenDemo.Shared`** - P/Invoke bindings (`NativeMethods.cs`) and network packet serialization (`Protocol.cs`).
* **`LockScreenDemo.Service`** - Windows Service lifecycle worker.
* **`LockScreenDemo.Agent`** - Socket server, screen capture, and Win32 simulation loops.
* **`LockScreenDemo.Viewer`** - WPF client app.
* `install.ps1` - PowerShell script to publish, register, and start the service.
* `uninstall.ps1` - PowerShell script to stop and uninstall the service.

---

## How to Install and Run

### Prerequisites

* Windows OS
* .NET 10 SDK or Runtime installed
* Administrator privileges (to register services)

### Installation

1. Open a **PowerShell** window.
2. Run this command to launch the installer with Administrator permissions:

   ```powershell
   cd "c:\Users\Manikanda Bharathi\Desktop\pooj\rustdesk-master\LockScreenDemo"
   Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File .\install.ps1" -Verb RunAs
   ```

3. A separate elevated window will open, build the solution in Release mode, copy the files to `C:\ProgramData\LockScreenDemo\bin\`, register the service, start it, and launch the **Viewer**.

---

## How to Test

### Test 1: Local Loopback (Same PC)

1. On the Viewer window, type `127.0.0.1` and click **Connect**.
2. You will see your own screen streamed inside the preview window.
3. Move and click your mouse inside the preview box. You will see it control your local cursor (looping back securely through SSL!).
4. Disconnect by clicking **Disconnect** or pressing `Esc`.

### Test 2: Remote Control (Different PCs / Virtual Machines)

To test connectivity between different PCs or using Virtual Machines (VMs):

1. **Server PC (Controlled PC / VM 1)**: Run the installation script. Note its IP Address (e.g. `192.168.1.100` or `192.168.56.101`).
2. **Client PC (Viewer PC / VM 2)**:
   * Copy the published binary folder `C:\ProgramData\LockScreenDemo\bin` to the Client PC.
   * Run `LockScreenDemo.Viewer.exe`.
   * Type the Server PC's IP address and click **Connect**.
3. You will now see the Server PC's screen. Move and click your mouse, or type on your keyboard to control the Server PC.

### Test 3: Bypassing Lock Screen & UAC Prompts

1. Establish a remote connection from Client PC to Server PC.
2. Lock the Server PC (e.g. click **Lock Windows (Win+L)** in the Viewer).
3. On the Client Viewer, verify that the display switches to show the Windows Logon/Sign-in screen.
4. Click the password field and type your password/PIN. Hit Enter to log in remotely!
5. Trigger any administrative program on the Server PC. When the UAC prompt dims the screen, verify that the Client Viewer can see the UAC dialog box and click "Yes" to elevate.

### Test 4: Clipboard Syncing

1. While connected, copy a paragraph of text on the Client PC.
2. Right-click and paste it on the Server PC.
3. Copy something on the Server PC, and paste it locally on the Client PC.
4. Inspect the Logs box on the Viewer window to see the clipboard transfer notifications.

---

## Testing Guide using Virtual Machines (VMs)

Using VMs allows you to test the service, lock screen transitions, and input simulation in an isolated environment.

### 1. Download Free Windows Developer VMs

Microsoft provides free virtual machines preconfigured for testing:

* Download Link: **[Windows 11 Development Environment VMs](https://developer.microsoft.com/en-us/windows/downloads/virtual-machines/)**
* Choose your hypervisor format (VirtualBox, VMware, Hyper-V, or Parallels).

### 2. Configure Virtual Networking

To allow the VM instances to communicate, change the Network settings in your hypervisor:

* **Option A: Host-Only Adapter** (Recommended for Offline Security):
  * Sets up a private network between the host PC and your VMs.
  * The VMs will get IPs in the `192.168.56.x` range.
* **Option B: Bridged Adapter** (Recommended for LAN Simulation):
  * Binds the VM to your physical network adapter, making it look like a physical PC connected to your router.
  * The VM gets an IP on your local home network (e.g., `192.168.1.x`).

### 3. Open Firewall Port in the Server VM

The Windows Defender Firewall in the Server VM will block incoming TCP connections by default.
Run this command in an **Administrator PowerShell** window inside the **Server VM** to allow incoming connections on port `5800`:

```powershell
New-NetFirewallRule -DisplayName "LockScreenDemo" -Direction Inbound -LocalPort 5800 -Protocol TCP -Action Allow
```

---

## How to Uninstall

To remove the Windows Service and delete the published binary files:

1. Open PowerShell.
2. Run the uninstaller elevated:

   ```powershell
   cd "c:\Users\Manikanda Bharathi\Desktop\pooj\rustdesk-master\LockScreenDemo"
   Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1" -Verb RunAs
   ```

---

## Troubleshooting

### Access Violation Crash (Exit Code `3221225794` / `0xC0000005`)

If the Windows Service logs show that the Agent process exited immediately with exit code `3221225794` (`0xC0000005`), this represents a Windows **Access Violation**.

This issue was diagnosed and resolved with two core fixes:
1. **Token Impersonation Level (Worker.cs)**:
   * **Problem**: The token was duplicated with `SECURITY_IMPERSONATION_LEVEL.SecurityIdentification`. Spawning processes via `CreateProcessAsUserW` to run inside interactive desktops (like Session 1's lock screen) requires the token to have `SecurityImpersonation` or `SecurityDelegation`. Using `SecurityIdentification` led to access violations during internal .NET runtime identity queries.
   * **Resolution**: Updated token duplication to use `SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation`.
2. **Ephemeral Cryptography Keystores (Program.cs)**:
   * **Problem**: Generating the self-signed SSL/TLS certificate using `X509KeyStorageFlags.MachineKeySet` attempted disk-based private key caching. Under the duplicated SYSTEM logon desktop session (where no user profile is fully loaded), this disk write triggered permissions issues.
   * **Resolution**: Switched to `X509KeyStorageFlags.EphemeralKeySet` to maintain the certificate and private key entirely in memory, bypassing disk keystore permission checks.

To deploy these fixes, open an **Administrator PowerShell** window and rerun the installation script:
```powershell
cd "c:\Users\Manikanda Bharathi\Desktop\pooj\rustdesk-master\LockScreenDemo"
Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File .\install.ps1" -Verb RunAs
```
