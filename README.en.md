# MSI GPUSwitch — MSI GPU Mode Switching: Under the Hood

**English** | **[中文](README.md)**

> Reverse-engineering成果: Fully cracked MSI laptop (Stealth 14) GPU triple-mode switching mechanism,
> enabling standalone Hybrid / Discrete / Eco(iGPU) switching without MSI Center.

---

## Table of Contents

1. [Background & Goals](#1-background--goals)
2. [Discovery Journey](#2-discovery-journey)
3. [Architecture Overview](#3-architecture-overview)
4. [WMI ACPI Interface Details](#4-wmi-acpi-interface-details)
5. [Registry Mechanism Details](#5-registry-mechanism-details)
6. [MSI Background Service Dependencies](#6-msi-background-service-dependencies)
7. [Complete Switching Procedure (Verified)](#7-complete-switching-procedure-verified)
8. [Triple-Mode Switching: Eco/iGPU Mode](#8-triple-mode-switching-ecoigpu-mode)
9. [GPU Status Reading](#9-gpu-status-reading)
10. [MSI GPUSwitch Command Reference](#10-msi-gpuswitch-command-reference)
11. [Verified Test Matrix](#11-verified-test-matrix)
12. [Known Pitfalls & Gotchas](#12-known-pitfalls--gotchas)
13. [Integration into YAMDCC-3.0](#13-integration-into-yamdcc-30)
14. [File Index](#14-file-index)

---

## 1. Background & Goals

MSI laptops (Stealth 14, etc.) support three GPU output modes:

| Mode | Name | Description |
|---|---|---|
| **Eco / iGPU** | Integrated Only | Uses only the integrated GPU; discrete GPU is completely off; best battery life |
| **Hybrid / MSHybrid** | Hybrid Output | iGPU drives display; dGPU can render via Optimus; balanced performance & power |
| **Discrete / dGPU** | Discrete Only | dGPU directly drives display; maximum performance; dGPU cannot be turned off |

Official switching is only available through MSI Center's Feature Manager, which conflicts with third-party tools (e.g., YAMDCC).
The goal of this project is to reverse-engineer the underlying switching mechanism and enable standalone switching without MSI Center.

---

## 2. Discovery Journey

### 2.1 Phase 1: WMI ACPI Probing

From scattered community information, MSI Center communicates with the BIOS through the `MSI_ACPI` class in the `root\wmi` namespace.

**Probing method**: Using `ManagementObjectSearcher` to enumerate WMI classes under `root\wmi`, discovered:

```
MSI_ACPI (instance: ACPI\PNP0C14\0_0)
  Methods:
    Get_WMI, Get_BIOS, Get_Device, Get_Power, Get_Data,
    Get_Thermal, Get_Fan, Get_Temperature, Get_MasterBattery,
    Get_Debug, Get_AP
    Set_WMI, Set_BIOS, Set_Device, Set_Power, Set_Data,
    Set_Thermal, Set_Fan, Set_Temperature, Set_MasterBattery,
    Set_Debug, Set_AP
```

All methods share a uniform signature: input parameter `Data` is of type `Package_32` (32-byte array), output `Data` is also `Package_32`.

**Package_32 transfer method**:
```csharp
using var pkgClass = new ManagementClass(
    new ManagementScope("root\\wmi"), new ManagementPath("Package_32"), null);
ManagementObject pkg = pkgClass.CreateInstance();
pkg["Bytes"] = inputBytes;  // byte[32]

// Invoke
ManagementBaseObject inParams = mo.GetMethodParameters("Get_BIOS");
inParams["Data"] = pkg;
ManagementBaseObject outParams = mo.InvokeMethod("Get_BIOS", inParams, null);
var dataOut = outParams?["Data"] as ManagementBaseObject;
byte[] result = ExtractBytesFromPackage(dataOut);
```

### 2.2 Phase 2: Finding GPU Status Bits

Scanning each `Get_*` method with cmd=0x00~0x20, found multiple GPU-related status bits:

| Method | cmd | Byte offset | dGPU value | Hybrid value | Meaning |
|---|---|---|---|---|---|
| `Get_BIOS` | 0x02 | byte[1] | 0x40 | 0x00 | BIOS persistent bit (reflects actual mode after reboot) |
| `Get_Device` | 0x01 | byte[1] bit6 | 1 | 0 | Device runtime status bit |
| `Get_Data` | 0x04 | byte[1] | 0x83 | 0x00 | Data block status |
| `Get_AP` | 0x00 | byte[3] | 0xC2 | 0xC0 | AP status (bit1 difference) |
| `Get_AP` | 0x01 | byte[3] | 0x05 | 0x03 | AP auxiliary status |
| `Get_BIOS` | 0x00 | byte[2] | 0x50 | 0x51 | BIOS auxiliary bit |
| `Get_BIOS` | 0x04 | byte[1] | 0x07 | 0x03 | BIOS data block |

`Get_BIOS(0x02).byte[1]` is the most reliable GPU mode indicator:
- `0x40` = Discrete Only
- `0x00` = MSHybrid

### 2.3 Phase 3: Decompiling Feature_Manager.exe

Using a custom ILDump tool (based on Mono.Cecil) to decompile `C:\Program Files (x86)\Feature Manager\Feature_Manager.exe`,
found key class `Feature_Manager.MainWindow` and `Feature_Manager.GPU_Swtich_Window`.

**Key Finding 1 — Registry Write**:
```il
IL_0068: ldc.i4.0                              // Registry root = HKLM
IL_0069: ldstr  SOFTWARE\WOW6432Node\MSI\Feature Manager\Component\Base Module\User Scenario
IL_006E: ldstr  FW_GPU_CH
IL_0074: ldfld  cboBox_GraphicsSwithc
IL_0079: callvirt get_SelectedIndex()           // FW_GPU_CH = ComboBox.SelectedIndex
```

**Key Finding 2 — Triple-Mode ComboBox**:
- `uxIntegratedMode` (ComboBoxItem) — Eco/iGPU option, Index=2
- `uxDiscreteMode` (ComboBoxItem) — Discrete option, Index=1
- Default item (MSHybrid) — Index=0

Therefore: **`FW_GPU_CH = 0` (Hybrid), `1` (Discrete), `2` (Eco/iGPU)**

**Key Finding 3 — Mode Support Detection**:
```il
// Read FW_SupportUMA, if =1 show iGPU option
ldstr  FW_SupportUMA
call   Microsoft.Win32.Registry::GetValue(...)
// Read FW_SupportDiscrete, if =1 show Discrete option
ldstr  FW_SupportDiscrete
call   Microsoft.Win32.Registry::GetValue(...)
```

**Key Finding 4 — Initial Selection**:
```il
// VGA_Type == false → SelectedIndex = 0 (Hybrid)
// VGA_Type == true  → SelectedIndex = 1 (Discrete)
```

### 2.4 Phase 4: Procmon Tracing of MSI Center

Using Process Monitor to capture MSI Center's complete behavior sequence during GPU switching:

1. Write registry `FW_CurrentNewGPU` = inverse of current mode
2. Write registry `FW_GPU_CH` = target mode
3. Call `Set_Data(cmd=0xD1, ...)` — write EC register
4. Wait 2 seconds
5. Call `Set_Data(cmd=0xBE, byte[1]=0x02)` — confirm write
6. Prompt for reboot

### 2.5 Phase 5: Service Dependency Verification

Using controlled variable testing to verify the necessity of each MSI service:

| MSIAPService | FM Service | FM UI | SCM Service | Switch Result |
|---|---|---|---|---|
| ✅ | ✅ | ❌ | ❌ | ✅ Success |
| ✅ | ❌ | ❌ | ❌ | ❌ Failure |
| ❌ | ✅ | ❌ | ❌ | ❌ Failure (FM Service exits immediately) |
| ❌ | ❌ | ✅ | ❌ | ✅ Success (FM UI auto-starts both services) |

Conclusion: **MSIAPService.exe + Feature Manager Service.exe are required**, FM UI and SCM are not needed.

---

## 3. Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│                    MSI GPU Switching Architecture            │
│                                                              │
│  ┌──────────────┐    ┌──────────────────┐                   │
│  │ Feature       │    │ MSIAPService.exe │                   │
│  │ Manager       │    │ (MSI Foundation  │                   │
│  │ Service.exe   │◄──►│  Service)        │                   │
│  └──────┬───────┘    └────────┬─────────┘                   │
│         │                     │                              │
│         │  Registry + WMI ACPI│                              │
│         ▼                     ▼                              │
│  ┌──────────────────────────────────────────────────┐        │
│  │              Windows Registry                    │        │
│  │  HKLM\..\Feature Manager\..\User Scenario       │        │
│  │    FW_GPU_CH        = Target mode (0/1/2)        │        │
│  │    FW_CurrentNewGPU = Current mode (must ≠ target)│       │
│  │    FW_SupportUMA    = Eco mode supported          │       │
│  │    FW_SupportDiscrete = Discrete mode supported   │       │
│  └──────────────────────┬───────────────────────────┘        │
│                         │                                    │
│                         ▼                                    │
│  ┌──────────────────────────────────────────────────┐        │
│  │           WMI ACPI (root\wmi)                     │        │
│  │  MSI_ACPI class → Package_32 (32 bytes)          │        │
│  │                                                    │        │
│  │  Set_Data(0xD1, ...) → Write EC register (GPU)   │        │
│  │  Set_Data(0xBE, 0x02) → Confirm write             │        │
│  └──────────────────────┬───────────────────────────┘        │
│                         │                                    │
│                         ▼                                    │
│  ┌──────────────────────────────────────────────────┐        │
│  │           EC (Embedded Controller)                │        │
│  │  Register 0xD1 — GPU mode persistent bit          │        │
│  │  Register 0xBE — Confirm/commit register          │        │
│  │  (Writes persist across reboots; BIOS reads at POST)│      │
│  └──────────────────────┬───────────────────────────┘        │
│                         │                                    │
│                         ▼                                    │
│  ┌──────────────────────────────────────────────────┐        │
│  │           BIOS / MUX                              │        │
│  │  At reboot, BIOS reads EC register + registry    │        │
│  │  Configures PCIe/DP MUX routing → GPU output path │       │
│  └──────────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────────┘
```

---

## 4. WMI ACPI Interface Details

### 4.1 Basic Calling Pattern

All `Get_*/Set_*` methods use the unified `Package_32` format:

- **Input**: `byte[32]`, where `byte[0]` is the command code (cmd), remaining bytes are data
- **Output**: `byte[32]`, where `byte[0]` is ACK (0x01=success), remaining bytes are return data

### 4.2 Get_AP — GPU Status Read

```
Get_AP(cmd=0x00):
  byte[0] = 0x01 (ACK)
  byte[1] = Status byte (bit0: 1=dGPU, 0=Hybrid)
  byte[2] = BIOS response byte (bit1: 1=BIOS acknowledged)
  byte[3] = 0xC2 (dGPU) / 0xC0 (Hybrid)
```

### 4.3 Get_BIOS — BIOS Persistent State

```
Get_BIOS(cmd=0x02):
  byte[0] = 0x01 (ACK)
  byte[1] = 0x40 (dGPU Direct) / 0x00 (MSHybrid)
```

This is the most reliable GPU mode indicator, reflecting the BIOS-level persistent configuration.

### 4.4 Set_Data — EC Register Write

**Core write interface** for modifying EC registers:

```
Set_Data(cmd=0xD1, byte[1]=<modified AP status byte>):
  → Write EC register 0xD1 (GPU mode persistent bit)
  → Return ACK=0x01 on success

Set_Data(cmd=0xBE, byte[1]=0x02):
  → Confirm/commit the previous EC write
  → Return ACK=0x01 on success
```

### 4.5 Code Example (C#)

```csharp
// Read GPU status
byte[] ReadGpuStatus()
{
    var input = new byte[32]; input[0] = 0x00;  // cmd=0x00
    using var pkgClass = new ManagementClass(
        new ManagementScope("root\\wmi"), new ManagementPath("Package_32"), null);
    ManagementObject pkg = pkgClass.CreateInstance();
    pkg["Bytes"] = input;

    using var searcher = new ManagementObjectSearcher("root\\wmi", "SELECT * FROM MSI_ACPI");
    foreach (ManagementObject mo in searcher.Get())
    {
        ManagementBaseObject inParams = mo.GetMethodParameters("Get_AP");
        inParams["Data"] = pkg;
        ManagementBaseObject outParams = mo.InvokeMethod("Get_AP", inParams, null);
        var dataOut = outParams?["Data"] as ManagementBaseObject;
        // Extract byte[] from dataOut ...
    }
}

// Write EC register (GPU switch)
void WriteEcRegister(byte cmd, byte value)
{
    var pkg = new byte[32];
    pkg[0] = cmd;     // EC address (0xD1 or 0xBE)
    pkg[1] = value;   // Value to write

    // Same as above, call Set_Data instead of Get_AP
    ManagementBaseObject inParams = mo.GetMethodParameters("Set_Data");
    inParams["Data"] = pkgInstance;
    ManagementBaseObject outParams = mo.InvokeMethod("Set_Data", inParams, null);
    // Check outParams byte[0] == 0x01 to confirm success
}
```

---

## 5. Registry Mechanism Details

### 5.1 Registry Path

```
HKLM\SOFTWARE\WOW6432Node\MSI\Feature Manager\Component\Base Module\User Scenario
```

### 5.2 Key Values

| Value | Type | Meaning | Values |
|---|---|---|---|
| `FW_GPU_CH` | DWORD | **Target GPU mode** (effective after reboot) | 0=Hybrid, 1=Discrete, 2=Eco/iGPU |
| `FW_CurrentNewGPU` | DWORD | **Current actual GPU mode** | Must **differ** from FW_GPU_CH, or switch won't trigger |
| `FW_SupportNewGPU` | DWORD | Whether new EC switching is supported | 1=supported |
| `FW_SupportUMA` | DWORD | Whether Eco/iGPU (UMA) mode is supported | 1=supported |
| `FW_SupportDiscrete` | DWORD | Whether Discrete mode is supported | 1=supported |

### 5.3 The FW_CurrentNewGPU Trap

This is the easiest pitfall to fall into:

- **During switching**, `FW_CurrentNewGPU` is set to the **inverse of the target** (i.e., the current actual mode)
- **After switching**, `FW_CurrentNewGPU` is **NOT** updated to the new actual mode
- **After reboot**, `FW_CurrentNewGPU` still holds the old value written during the switch
- **Therefore**: Do NOT use `FW_CurrentNewGPU` to determine the current GPU mode! Use `FW_GPU_CH` instead.

**Correct approach**: During a switch, read the current `FW_GPU_CH` value, write it to `FW_CurrentNewGPU`, then write the new target value to `FW_GPU_CH`. This ensures they differ, so the startup service will trigger the switch.

```csharp
// Correct registry write order
int currentMode = (int)k.GetValue("FW_GPU_CH");  // Read current value
if (currentMode == targetMode)
    currentMode = targetMode == 0 ? 1 : 0;        // Ensure they differ
k.SetValue("FW_CurrentNewGPU", currentMode);       // Write current value first
k.SetValue("FW_GPU_CH", targetMode);               // Then write target value
```

---

## 6. MSI Background Service Dependencies

### 6.1 MSI Foundation Service (MSIAPService.exe)

- **Path**: `C:\Program Files (x86)\Feature Manager\MSIAPService.exe`
- **Type**: Windows Service
- **Start method**: `sc start "MSI Foundation Service"` or `ServiceController.Start()`
- **Role**: WMI ACPI backend, provides `\\.\pipe\simple` named pipe communication
- **Dependencies**: None, can start independently

### 6.2 Feature Manager Service.exe

- **Path**: `C:\Program Files (x86)\Feature Manager\Feature Manager Service.exe`
- **Type**: Regular process (not a Windows Service)
- **Start method**: `Process.Start()` directly
- **Role**: Performs certain critical actions in the background, enabling the EC write + registry modification combination to take effect after reboot
- **Dependencies**: **Requires** MSIAPService to be running first, otherwise exits immediately
- **Initialization time**: ~2 seconds after start

### 6.3 Unnecessary Services

| Service | Reason |
|---|---|
| Feature Manager UI (Feature_Manager.exe) | Only provides GUI; switching logic is in the Service |
| Micro Star SCM (MSIService.exe) | `Graphics_switch` call returns `WBEM_E_NOT_SUPPORTED`; doesn't affect switching |
| MSI Center Service | Completely unrelated |

---

## 7. Complete Switching Procedure (Verified)

The following is a verified GPU mode switching procedure that is fully consistent with MSI Center's behavior:

### Prerequisites

1. `MSI Foundation Service` (MSIAPService.exe) is running
2. `Feature Manager Service.exe` is running

### Switching Steps

```
Step 0: Write registry
  a) Read current FW_GPU_CH value → use as FW_CurrentNewGPU
     (If it happens to equal the target, set it to another value to ensure they differ)
  b) Write FW_CurrentNewGPU = current value (ensure it differs from target)
  c) Write FW_GPU_CH = target mode (0=Hybrid, 1=Discrete, 2=Eco)

Step 1: Read current AP status
  → Get_AP(cmd=0x00)
  → Take byte[1]

Step 2: Modify byte[1]
  → Clear bit0, clear bit1, then set bit0=1
  → mod = (orig & ~0x03) | 0x01

Step 3: Write EC register 0xD1
  → Set_Data(cmd=0xD1, byte[1]=mod)
  → Check ACK == 0x01

Step 4: Wait for BIOS response
  → Sleep(2000)
  → Re-read Get_AP(cmd=0x00)
  → Check if byte[2] bit1 is set (BIOS acknowledged)

Step 5: Confirm write
  → Set_Data(cmd=0xBE, byte[1]=0x02)
  → Check ACK == 0x01

Step 6: Prompt user to reboot
  → After reboot, BIOS reads EC register + registry, configures MUX
```

### Flow Diagram

```
Write Registry → Get_AP(0) → Modify byte[1] → Set_Data(0xD1) → Wait 2s → Check bit1 → Set_Data(0xBE) → Reboot
      ↓              ↓              ↓               ↓               ↓           ↓              ↓
  FW_GPU_CH      Read status    bit0=1         Write EC 0xD1   BIOS proc   BIOS ack      Commit write
  FW_Current     bit1=0         bit1=0         ACK=0x01        bit1=1?     ACK=0x01      Takes effect
```

---

## 8. Triple-Mode Switching: Eco/iGPU Mode

### 8.1 Discovery Process

By decompiling `Feature_Manager.exe`'s `MainWindow` class with ILDump, discovered:

1. GPU mode ComboBox has three options: Index 0=Hybrid, 1=Discrete, 2=Eco/UMA
2. `FW_GPU_CH = ComboBox.SelectedIndex` — directly writes the dropdown index
3. `uxIntegratedMode` (ComboBoxItem) is the Eco/iGPU option, visibility controlled by `isSupport_UMA_Switch`
4. `uxDiscreteMode` (ComboBoxItem) is the Discrete option, visibility controlled by `isSupport_Discrete_Switch`

### 8.2 Mode Support Detection

Registry values determine which options appear in the UI:

```
FW_SupportUMA = 1       → Show Eco/iGPU mode option (supported on this machine)
FW_SupportDiscrete = 1  → Show Discrete mode option (supported on this machine)
FW_SupportNewGPU = 1    → New EC switching method supported
```

### 8.3 Key Insight for Eco Mode Switching

**The EC write procedure is identical** — the 0xD1 → 0xBE WMI ACPI sequence is the same for all three modes.
The difference is **only** in the `FW_GPU_CH` value:

| FW_GPU_CH | Mode | Description |
|---|---|---|
| 0 | Hybrid / MSHybrid | iGPU output, dGPU can render via Optimus |
| 1 | Discrete / dGPU | dGPU directly drives output |
| 2 | Eco / iGPU / UMA | iGPU only, dGPU completely off |

This means: **BIOS reads the `FW_GPU_CH` value during POST to determine MUX configuration**, and the EC register write merely notifies BIOS that "a change is pending."

---

## 9. GPU Status Reading

### 9.1 Recommended Method: Registry

```csharp
using var k = Registry.LocalMachine.OpenSubKey(MsiRegPath, writable: false);
int mode = (int)k.GetValue("FW_GPU_CH");
// 0=Hybrid, 1=Discrete, 2=Eco, other→default Hybrid
```

**Pros**: Simple and reliable, reflects the actual mode after reboot.
**Note**: Do NOT use `FW_CurrentNewGPU`; it holds the inverse value after a switch and is not updated after reboot.

### 9.2 Fallback Method: WMI ACPI

```csharp
// Get_AP(0) byte[1] bit0
byte[] ap0 = WmiCallGet("Get_AP", 0x00);
bool isDGpu = (ap0[1] & 0x01) != 0;

// Get_BIOS(0x02) byte[1]
byte[] bios = WmiCallGet("Get_BIOS", 0x02);
bool isDGpu = bios[1] == 0x40;
```

**Pros**: Reflects current runtime state (no reboot needed to see changes).
**Cons**: Cannot distinguish Eco from Hybrid (WMI bits only have dGPU/non-dGPU states).

### 9.3 Quick Status Check (qs command)

MSI GPUSwitch's `qs` command reads 7 GPU status bits simultaneously for cross-verification:

```
Get_BIOS[0x02].byte[1]   — dGPU=0x40, Hybrid=0x00
Get_Device[0x01].byte[1] — bit6: dGPU=1, Hybrid=0
Get_Data[0x04].byte[1]   — dGPU=0x83, Hybrid=0x00
Get_AP[0x00].byte[3]     — dGPU=0xC2, Hybrid=0xC0
Get_AP[0x01].byte[3]     — dGPU=0x05, Hybrid=0x03
Get_BIOS[0x00].byte[2]   — dGPU=0x50, Hybrid=0x51
Get_BIOS[0x04].byte[1]   — dGPU=0x07, Hybrid=0x03
```

---

## 10. MSI GPUSwitch Command Reference

### Build & Run

```powershell
dotnet build "MSI GPUSwitch\GpuSwitch.csproj" -c Release
# Run as administrator
."MSI GPUSwitch\bin\Release\net8.0-windows\win-x64\MSI GPUSwitch.exe"
```

> **Prerequisite**: GPU switching depends on MSI's WMI ACPI infrastructure, which requires **Feature Manager** (an MSI Center component) to be installed.
> The project root includes `Feature Manager_1.0.2312.2201.exe` installer, or download MSI Center from MSI's official website.
> After installation, WMI ACPI method calls work correctly; uninstalling FM causes WMI ACPI to hang permanently.
> The project bundles a `FeatureManager/` directory (containing `MSIAPService.exe` and `Feature Manager Service.exe`), auto-copied to output during build.

### Command List

Main Menu:

| Command | Function |
|---|---|
| `gpuD` | Switch to Discrete Only (dGPU), auto auxiliary services + shutdown prompt |
| `gpuH` | Switch to Hybrid Output (MSHybrid), auto auxiliary services + shutdown prompt |
| `gpuE` | Switch to Eco/iGPU Only, auto auxiliary services + shutdown prompt |
| `qs` | Quick view of 7 GPU status bits |
| `uv` | Read UEFI variable MsiDCVarData (persistent GPU mode) |
| `bs` | Check WMI ACPI bootstrap status |
| `boot` | Bootstrap: copy msiapcfg.dll + set MofImagePath registry |
| `dbg` | Enter advanced debug mode (all probe/switch commands preserved) |
| `0` | Exit |

Advanced Debug Mode (enter with `dbg`):

| Command | Function |
|---|---|
| `1` | Display system info (model/BIOS/GPU) |
| `2` | Enumerate all MSI_* classes under root\wmi |
| `3` | Enumerate GPU-related classes under root\cimv2 |
| `4` | Dump all instances of a specified WMI class |
| `5` | Dump all method signatures of a specified WMI class |
| `6` | One-click full dump (writes to dump.txt) |
| `7` | Call Get_WMI (list supported data blocks) |
| `8` | Bulk probe Get_* / cmd 0x00~0x20 |
| `9` | Manually call any Get_* method |
| `p` | Describe Package_32 class definition |
| `q` | Scan all Get_* × cmd with Package_32 |
| `r` | Full GPU mode status read |
| `snap` | Save full status snapshot to snapshot.txt |
| `d` | One-click full probe + write to acpi_probe.txt |
| `s1/s0` | Set_BIOS switch to Discrete/Hybrid (legacy method) |
| `d1/d0` | Set_Device switch to Discrete/Hybrid (legacy method) |
| `dt1/dt0` | Set_Data cmd=0x04 switch to Discrete/Hybrid |
| `ap1/ap0` | Set_AP cmd=0x00 switch to Discrete/Hybrid |
| `b41/b40` | Set_BIOS cmd=0x04 switch to Discrete/Hybrid |
| `cc` | Dump MSI_CentralControl method signatures |
| `hb` | Write OS heartbeat (EC 0xD9 bit0) |
| `uvw` | Debug: directly write UEFI byte[5] |
| `unboot` | Uninstall bootstrap (delete msiapcfg.dll + clear registry) |
| `srv` | View MSI Foundation Service status |
| `srv-install/start/stop/remove` | Service management |
| `auto` | Auto-prepare MSI auxiliary components and switch GPU |
| `0` | Return to main menu |

### Switching Example

```
Enter command: gpuE

── 🎯 GPU switch to Eco/iGPU Mode ──
  ✓ MSI Foundation Service is running
  ✓ Feature Manager Service.exe is running
  Pre-switch registry state:
    FW_GPU_CH        = 0
    FW_CurrentNewGPU = 1
  Step 0a: Registry FW_CurrentNewGPU: 1 -> 0 (current actual mode)
  Step 0b: Registry FW_GPU_CH: 0 -> 2 (target mode)
  Step 1: Get_AP(0) = 01 00 02 ...
  Step 2: Modify byte[1]: 0x00 -> 0x01 (bit0=1, bit1=0)
  ⚠️ This will write to EC register 0xD1. Type YES to continue:
  > YES
  Step 4: Set_Data(0xD1) ACK=0x01 (success)
  Step 5: Waiting 2 seconds...
          byte[2] bit1 = 1 (BIOS acknowledged)
  Step 6: Set_Data(0xBE) ACK=0x01
  ✓ Write procedure complete. Please reboot to apply GPU MUX switch.
```

---

## 11. Verified Test Matrix

### Service Dependency Tests

| # | MSIAPService | FM Service | FM UI | SCM | Result |
|---|---|---|---|---|---|
| 1 | ✅ | ✅ | ❌ | ❌ | ✅ Switch successful |
| 2 | ✅ | ❌ | ❌ | ❌ | ❌ Switch failed |
| 3 | ❌ | ✅ | ❌ | ❌ | ❌ FM Service exits immediately |
| 4 | ❌ | ❌ | ✅ | ❌ | ✅ FM UI auto-starts services |

### Mode Switch Tests (Updated 2026-05-02)

> **Hybrid ↔ Discrete bidirectional switching fully working!** All 6 directions verified successful.
> Tool auto-manages MSI auxiliary services (start on demand, stop after switch), prevents FM Service shutdown crash.

| Source Mode | Target Mode | FW_GPU_CH | Result |
|---|---|---|---|
| Hybrid | Discrete | 0→1 | ✅ Effective after reboot |
| Discrete | Hybrid | 1→0 | ✅ Effective after reboot |
| Hybrid | Eco/iGPU | 0→2 | ✅ Effective after reboot |
| Discrete | Eco/iGPU | 1→2 | ✅ Effective after reboot |
| Eco/iGPU | Hybrid | 2→0 | ✅ Effective after reboot |
| Eco/iGPU | Discrete | 2→1 | ✅ Effective after reboot |

### MSI Auxiliary Service Management Tests

| Test | Result |
|---|---|
| MSIAPService installed as Windows service | ✅ Auto-set to manual start |
| Auto-start MSIAPService before switch | ✅ On-demand start, no user interference |
| Auto-stop MSIAPService after switch | ✅ CleanupMsiHelpers() after switch |
| Auto-terminate FM Service process | ✅ Prevents 0xe0434352 shutdown crash |
| MSIAPService not auto-starting on boot | ✅ start=demand (manual) |

### Graphics_switch (SCM) Tests

| Scenario | Result |
|---|---|
| SCM Graphics_switch call succeeds | Switch succeeds |
| SCM pipe doesn't exist (Win32Error=2) | Switch still succeeds |
| SCM returns WBEM_E_NOT_SUPPORTED | Switch still succeeds |

**Conclusion**: `Graphics_switch` is not a necessary step; it only notifies SCM to update its UI.

---

## 12. Known Pitfalls & Gotchas

### Pitfall 1: FW_CurrentNewGPU Inverse Value

**Symptom**: UI shows the opposite mode from actual after switching.
**Cause**: `FW_CurrentNewGPU` is set to the inverse of the target during switching and is not updated after reboot.
**Fix**: Read `FW_GPU_CH` instead of `FW_CurrentNewGPU` to determine the current mode.

### Pitfall 2: FW_CurrentNewGPU == FW_GPU_CH

**Symptom**: Switch command executes successfully, but mode doesn't change after reboot.
**Cause**: Startup service sees `FW_CurrentNewGPU == FW_GPU_CH` and assumes no switch is needed.
**Fix**: During switching, read the current `FW_GPU_CH` value and write it to `FW_CurrentNewGPU` to ensure they differ.

### Pitfall 3: Feature Manager Service Exits Immediately

**Symptom**: FM Service process disappears right after starting.
**Cause**: FM Service depends on MSIAPService; it auto-exits when the latter isn't running.
**Fix**: Start MSI Foundation Service first, wait 2 seconds, then start FM Service.

### Pitfall 4: YAMDCC Service Conflicts with MSI Foundation Service

**Symptom**: YAMDCC service crashes on startup.
**Cause**: YAMDCC's `IsMSIServiceRunning` detects MSI Foundation Service and intentionally crashes.
**Fix**: Remove MSI Foundation Service from the conflict detection list (GPU switching requires it).

### Pitfall 5: WMI ACPI Hangs Permanently After Feature Manager Uninstall

**Symptom**: After uninstalling Feature Manager, WMI ACPI method calls (Get_AP, Set_Data, etc.) hang permanently.
**Cause**: FM installs a kernel-level component or ACPI BIOS interaction during installation, which is removed on uninstall. mofcomp MOF schema registration cannot fix this.
**Fix**: **Do not uninstall Feature Manager**. To prevent FM services from auto-starting, set MSI Foundation Service to Manual start and disable Micro Star SCM.

### Pitfall 6: Set_BIOS / Set_Device Switching Doesn't Work

**Symptom**: After calling `Set_BIOS(0x02, 0x40)` or `Set_Device(0x01, bit6=1)`, register values change but switching doesn't take effect after reboot.
**Cause**: These methods only modify runtime state; they don't trigger BIOS MUX reconfiguration.
**Fix**: Must use the `Set_Data(0xD1)` + `Set_Data(0xBE)` two-step write procedure.

### Pitfall 7: MSIAPService Auto-Starts on Boot (Unnecessary)

**Symptom**: After installing MSIAPService as a Windows service, it runs in the background on every boot, wasting resources.
**Cause**: `InstallUtil.exe` registers the service with Automatic start type by default.
**Fix**: After installation, run `sc.exe config "MSI Foundation Service" start=demand` to set manual start. The tool handles this automatically in `MsiApService.Install()`.

### Pitfall 8: Feature Manager Service.exe Shutdown Crash (0xe0434352)

**Symptom**: After GPU switching, shutting down shows "Feature Manager Service.exe - Application Error" (unknown software exception 0xe0434352). The switch itself succeeds.
**Cause**: After switching, MSIAPService remains alive; its dependent FM Service process receives a termination signal during Windows shutdown, causing an unhandled .NET CLR exception.
**Fix**: The tool auto-calls `CleanupMsiHelpers()` after switching: kills the FM Service process, then stops MSIAPService, ensuring these processes don't exist at shutdown time.

---

## 13. Integration into YAMDCC-3.0

MSI GPUSwitch's switching logic has been integrated into YAMDCC-3.0's `FanControlService.cs`:

- **`GetGpuMode()`**: Reads `FW_GPU_CH` registry value, returns 0/1/2/-1
- **`SetGpuMode(int mode)`**: Full execution of service startup → registry write → EC write procedure
- **IPC commands**: `SetGpuMode 0/1/2`, `GetGpuMode`
- **UI**: Three GPU mode buttons (Eco/Hybrid/Discrete), colored border indicates current mode

---

## 14. File Index

| File | Description |
|---|---|
| `AcpiProbe.cs` | WMI ACPI calling core, probing + switching logic + `CleanupMsiHelpers()` |
| `Program.cs` | CLI entry point, main menu (compact) + advanced debug submenu (`dbg`) |
| `MsiApService.cs` | MSIAPService auto management (install/start/stop/uninstall, auto-set manual start) |
| `WmiAcpiBootstrap.cs` | WMI ACPI one-time bootstrap (`bs`/`boot`/`unboot`) |
| `UefiVariable.cs` | UEFI variable read/write (`uv`/`uvw`) |
| `WmiProbe.cs` | WMI class enumeration and probing (read-only) |
| `SystemInfo.cs` | System info display (model/BIOS/GPU) |
| `PROGRESS.md` | Development process notes |
| `gpu_il.txt` | IL decompilation of Feature_Manager.MainWindow (GPU switch portion) |
| `scm_il.txt` | IL decompilation of API_Dynamic.SCM (Graphics_switch portion) |
| `load_il.txt` | IL decompilation of API_Dynamic.SCM.LoadWMIValue |
| `mainwindow_il.txt` | Full IL decompilation of Feature_Manager.MainWindow |
| `tools/ILDump/` | Custom IL decompilation tool (based on Mono.Cecil) |

### External Files

| Path | Description |
|---|---|
| `C:\Program Files (x86)\Feature Manager\` | Feature Manager installation directory |
| `C:\Program Files (x86)\Feature Manager\MSIAPService.exe` | MSI Foundation Service |
| `C:\Program Files (x86)\Feature Manager\Feature Manager Service.exe` | Feature Manager Service |
| `C:\Program Files (x86)\Feature Manager\Feature_Manager.exe` | Feature Manager UI |
| `C:\Program Files (x86)\Feature Manager\WMILib.dll` | WMI communication library |
| `C:\Program Files (x86)\Feature Manager\MSIWMIACPI2.dll` | WMI ACPI communication library |

---

## License & Disclaimer

This tool is for learning and research purposes only. Modifying EC registers and BIOS settings carries risks.
Make sure you understand what you are doing. The author is not responsible for any damage caused by using this tool.

The `FeatureManager/` directory contains binaries that are property of Micro-Star International (MSI).
They are included solely for interoperability and research purposes. No ownership or license is claimed.

See [DISCLAIMER](DISCLAIMER) for details.
