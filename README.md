# MSI GPUSwitch — MSI GPU 模式切换底层原理与接口文档

**[English](README.en.md)** | **中文**

> 逆向工程成果: 完全破解 MSI 笔记本 (绝影 14 / Stealth 14) 的 GPU 三模式切换机制,
> 可脱离 MSI Center 独立完成 Hybrid / Discrete / Eco(iGPU) 切换。

---

## 目录

1. [背景与目标](#1-背景与目标)
2. [发现历程](#2-发现历程)
3. [底层架构总览](#3-底层架构总览)
4. [WMI ACPI 接口详解](#4-wmi-acpi-接口详解)
5. [注册表机制详解](#5-注册表机制详解)
6. [MSI 后台服务依赖](#6-msi-后台服务依赖)
7. [完整切换流程 (已验证)](#7-完整切换流程-已验证)
8. [三模式切换: Eco/核显模式](#8-三模式切换-econ核显模式)
9. [GPU 状态读取](#9-gpu-状态读取)
10. [GpuSwitch.exe 命令参考](#10-gpuswitchexe-命令参考)
11. [已验证的测试矩阵](#11-已验证的测试矩阵)
12. [已知陷阱与踩坑记录](#12-已知陷阱与踩坑记录)
13. [整合进 YAMDCC-3.0](#13-整合进-yamdcc-30)
14. [相关文件索引](#14-相关文件索引)

---

## 1. 背景与目标

MSI 笔记本 (绝影 14 / Stealth 14 等) 支持三种 GPU 输出模式:

| 模式 | 名称 | 含义 |
|---|---|---|
| **Eco / iGPU** | 核显模式 | 仅使用集成显卡, 独显完全关闭, 最省电 |
| **Hybrid / MSHybrid** | 混合输出 | 核显输出, 独显可通过 Optimus 渲染, 平衡性能与功耗 |
| **Discrete / dGPU** | 独显直连 | 独显直连输出, 最高性能, 无法关闭独显 |

官方仅能通过 MSI Center 的 Feature Manager 切换, 且 MSI Center 与第三方控制工具 (如 YAMDCC) 冲突。
本项目的目标是逆向出切换的底层机制, 实现脱离 MSI Center 的独立切换。

---

## 2. 发现历程

### 2.1 第一阶段: WMI ACPI 探测

从社区零散资料得知, MSI Center 底层通过 `root\wmi` 命名空间下的 `MSI_ACPI` 类与 BIOS 通信。

**探测方法**: 使用 `ManagementObjectSearcher` 枚举 `root\wmi` 下的 WMI 类, 发现:

```
MSI_ACPI (实例: ACPI\PNP0C14\0_0)
  方法:
    Get_WMI, Get_BIOS, Get_Device, Get_Power, Get_Data,
    Get_Thermal, Get_Fan, Get_Temperature, Get_MasterBattery,
    Get_Debug, Get_AP
    Set_WMI, Set_BIOS, Set_Device, Set_Power, Set_Data,
    Set_Thermal, Set_Fan, Set_Temperature, Set_MasterBattery,
    Set_Debug, Set_AP
```

所有方法签名统一: 入参 `Data` 类型为 `Package_32` (32 字节数组), 出参 `Data` 也是 `Package_32`。

**Package_32 的传递方式**:
```csharp
using var pkgClass = new ManagementClass(
    new ManagementScope("root\\wmi"), new ManagementPath("Package_32"), null);
ManagementObject pkg = pkgClass.CreateInstance();
pkg["Bytes"] = inputBytes;  // byte[32]

// 调用
ManagementBaseObject inParams = mo.GetMethodParameters("Get_BIOS");
inParams["Data"] = pkg;
ManagementBaseObject outParams = mo.InvokeMethod("Get_BIOS", inParams, null);
var dataOut = outParams?["Data"] as ManagementBaseObject;
byte[] result = ExtractBytesFromPackage(dataOut);
```

### 2.2 第二阶段: 找到 GPU 状态位

对每个 `Get_*` 方法, 用 cmd=0x00~0x20 逐个扫描, 找到多处 GPU 相关的状态位:

| 方法 | cmd | 字节偏移 | dGPU 值 | Hybrid 值 | 含义 |
|---|---|---|---|---|---|
| `Get_BIOS` | 0x02 | byte[1] | 0x40 | 0x00 | BIOS 持久位 (重启后仍反映实际模式) |
| `Get_Device` | 0x01 | byte[1] bit6 | 1 | 0 | 设备运行状态位 |
| `Get_Data` | 0x04 | byte[1] | 0x83 | 0x00 | 数据块状态 |
| `Get_AP` | 0x00 | byte[3] | 0xC2 | 0xC0 | AP 状态 (bit1 差异) |
| `Get_AP` | 0x01 | byte[3] | 0x05 | 0x03 | AP 辅助状态 |
| `Get_BIOS` | 0x00 | byte[2] | 0x50 | 0x51 | BIOS 辅助位 |
| `Get_BIOS` | 0x04 | byte[1] | 0x07 | 0x03 | BIOS 数据块 |

其中 `Get_BIOS(0x02).byte[1]` 是最可靠的 GPU 模式指示器:
- `0x40` = 独显直连
- `0x00` = 混合输出

### 2.3 第三阶段: 反编译 Feature_Manager.exe

使用自研 ILDump 工具 (基于 Mono.Cecil) 反编译 `C:\Program Files (x86)\Feature Manager\Feature_Manager.exe`,
找到关键类 `Feature_Manager.MainWindow` 和 `Feature_Manager.GPU_Swtich_Window`。

**关键发现 1 — 注册表写入**:
```il
IL_0068: ldc.i4.0                              // Registry root = HKLM
IL_0069: ldstr  SOFTWARE\WOW6432Node\MSI\Feature Manager\Component\Base Module\User Scenario
IL_006E: ldstr  FW_GPU_CH
IL_0074: ldfld  cboBox_GraphicsSwithc
IL_0079: callvirt get_SelectedIndex()           // FW_GPU_CH = ComboBox.SelectedIndex
```

**关键发现 2 — 三模式 ComboBox**:
- `uxIntegratedMode` (ComboBoxItem) — 核显选项, Index=2
- `uxDiscreteMode` (ComboBoxItem) — 独显选项, Index=1
- 默认项 (MSHybrid) — Index=0

因此: **`FW_GPU_CH = 0`(Hybrid), `1`(Discrete), `2`(Eco/iGPU)**

**关键发现 3 — 模式支持检测**:
```il
// 读取 FW_SupportUMA, 若=1 则显示核显选项
ldstr  FW_SupportUMA
call   Microsoft.Win32.Registry::GetValue(...)
// 读取 FW_SupportDiscrete, 若=1 则显示独显选项
ldstr  FW_SupportDiscrete
call   Microsoft.Win32.Registry::GetValue(...)
```

**关键发现 4 — 初始化选中项**:
```il
// VGA_Type == false → SelectedIndex = 0 (Hybrid)
// VGA_Type == true  → SelectedIndex = 1 (Discrete)
```

### 2.4 第四阶段: Procmon 抓包 MSI Center

用 Process Monitor 监控 MSI Center 执行 GPU 切换时的完整行为序列:

1. 写注册表 `FW_CurrentNewGPU` = 当前模式反值
2. 写注册表 `FW_GPU_CH` = 目标模式
3. 调用 `Set_Data(cmd=0xD1, ...)` — 写 EC 寄存器
4. 等待 2 秒
5. 调用 `Set_Data(cmd=0xBE, byte[1]=0x02)` — 确认写入
6. 提示重启

### 2.5 第五阶段: 服务依赖验证

通过控制变量法测试各 MSI 服务的必要性:

| MSIAPService | FM Service | FM UI | SCM Service | 切换结果 |
|---|---|---|---|---|
| ✅ | ✅ | ❌ | ❌ | ✅ 成功 |
| ✅ | ❌ | ❌ | ❌ | ❌ 失败 |
| ❌ | ✅ | ❌ | ❌ | ❌ 失败 (FM Service 立即退出) |
| ❌ | ❌ | ✅ | ❌ | ✅ 成功 (FM UI 自动拉起两个服务) |

结论: **MSIAPService.exe + Feature Manager Service.exe 是必要条件**, 不需要 FM UI 和 SCM。

---

## 3. 底层架构总览

```
┌──────────────────────────────────────────────────────────────┐
│                    MSI GPU 切换架构                           │
│                                                              │
│  ┌──────────────┐    ┌──────────────────┐                   │
│  │ Feature       │    │ MSIAPService.exe │                   │
│  │ Manager       │    │ (MSI Foundation  │                   │
│  │ Service.exe   │◄──►│  Service)        │                   │
│  └──────┬───────┘    └────────┬─────────┘                   │
│         │                     │                              │
│         │  注册表 + WMI ACPI  │                              │
│         ▼                     ▼                              │
│  ┌──────────────────────────────────────────────────┐        │
│  │              Windows Registry                    │        │
│  │  HKLM\..\Feature Manager\..\User Scenario       │        │
│  │    FW_GPU_CH        = 目标模式 (0/1/2)           │        │
│  │    FW_CurrentNewGPU = 当前实际模式 (必须≠目标)    │        │
│  │    FW_SupportUMA    = 是否支持核显模式            │        │
│  │    FW_SupportDiscrete = 是否支持独显模式          │        │
│  └──────────────────────┬───────────────────────────┘        │
│                         │                                    │
│                         ▼                                    │
│  ┌──────────────────────────────────────────────────┐        │
│  │           WMI ACPI (root\wmi)                     │        │
│  │  MSI_ACPI 类 → Package_32 (32 bytes)             │        │
│  │                                                    │        │
│  │  Set_Data(0xD1, ...) → 写 EC 寄存器 (GPU 持久位) │        │
│  │  Set_Data(0xBE, 0x02) → 确认写入                  │        │
│  └──────────────────────┬───────────────────────────┘        │
│                         │                                    │
│                         ▼                                    │
│  ┌──────────────────────────────────────────────────┐        │
│  │           EC (Embedded Controller)                │        │
│  │  寄存器 0xD1 — GPU 模式持久化位                    │        │
│  │  寄存器 0xBE — 确认/提交寄存器                     │        │
│  │  (写入即使重启也保留, BIOS 在 POST 阶段读取)       │        │
│  └──────────────────────┬───────────────────────────┘        │
│                         │                                    │
│                         ▼                                    │
│  ┌──────────────────────────────────────────────────┐        │
│  │           BIOS / MUX                              │        │
│  │  重启时 BIOS 读取 EC 寄存器 + 注册表              │        │
│  │  配置 PCIe/DP MUX 路由 → 选择 GPU 输出通道        │        │
│  └──────────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────────┘
```

---

## 4. WMI ACPI 接口详解

### 4.1 基本调用模式

所有 `Get_*/Set_*` 方法使用统一的 `Package_32` 格式:

- **入参**: `byte[32]`, 其中 `byte[0]` 是命令码 (cmd), 其余字节是数据
- **出参**: `byte[32]`, 其中 `byte[0]` 是 ACK (0x01=成功), 其余字节是返回数据

### 4.2 Get_AP — GPU 状态读取

```
Get_AP(cmd=0x00):
  byte[0] = 0x01 (ACK)
  byte[1] = 状态字节 (bit0: 1=dGPU, 0=Hybrid)
  byte[2] = BIOS 响应字节 (bit1: 1=BIOS 已确认)
  byte[3] = 0xC2 (dGPU) / 0xC0 (Hybrid)
```

### 4.3 Get_BIOS — BIOS 持久状态

```
Get_BIOS(cmd=0x02):
  byte[0] = 0x01 (ACK)
  byte[1] = 0x40 (dGPU 直连) / 0x00 (MSHybrid 混合)
```

这是最可靠的 GPU 模式指示器, 反映 BIOS 层面的持久化配置。

### 4.4 Set_Data — EC 寄存器写入

**核心写入接口**, 用于修改 EC 寄存器:

```
Set_Data(cmd=0xD1, byte[1]=<修改后的AP状态字节>):
  → 写入 EC 寄存器 0xD1 (GPU 模式持久位)
  → 返回 ACK=0x01 表示成功

Set_Data(cmd=0xBE, byte[1]=0x02):
  → 确认/提交上一次的 EC 写入
  → 返回 ACK=0x01 表示成功
```

### 4.5 调用代码示例 (C#)

```csharp
// 读取 GPU 状态
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
        // 从 dataOut 提取 byte[] ...
    }
}

// 写入 EC 寄存器 (GPU 切换)
void WriteEcRegister(byte cmd, byte value)
{
    var pkg = new byte[32];
    pkg[0] = cmd;     // EC 地址 (0xD1 或 0xBE)
    pkg[1] = value;   // 要写入的值

    // 同上, 调用 Set_Data 而非 Get_AP
    ManagementBaseObject inParams = mo.GetMethodParameters("Set_Data");
    inParams["Data"] = pkgInstance;
    ManagementBaseObject outParams = mo.InvokeMethod("Set_Data", inParams, null);
    // 检查 outParams byte[0] == 0x01 确认成功
}
```

---

## 5. 注册表机制详解

### 5.1 注册表路径

```
HKLM\SOFTWARE\WOW6432Node\MSI\Feature Manager\Component\Base Module\User Scenario
```

### 5.2 关键值

| 值名 | 类型 | 含义 | 取值 |
|---|---|---|---|
| `FW_GPU_CH` | DWORD | **目标 GPU 模式** (重启后生效) | 0=Hybrid, 1=Discrete, 2=Eco/iGPU |
| `FW_CurrentNewGPU` | DWORD | **当前实际 GPU 模式** | 必须与 FW_GPU_CH **不同**, 否则切换不触发 |
| `FW_SupportNewGPU` | DWORD | 是否支持新版 EC 切换 | 1=支持 |
| `FW_SupportUMA` | DWORD | 是否支持核显 (UMA) 模式 | 1=支持 |
| `FW_SupportDiscrete` | DWORD | 是否支持独显直连模式 | 1=支持 |

### 5.3 FW_CurrentNewGPU 的陷阱

这是最容易踩坑的地方:

- **切换过程中**, `FW_CurrentNewGPU` 被设为**目标模式的反值** (即当前实际模式)
- **切换完成后**, `FW_CurrentNewGPU` **不会**被更新为新的实际模式
- **重启后**, `FW_CurrentNewGPU` 仍然是切换时写入的旧值
- **因此**: 不能用 `FW_CurrentNewGPU` 判断当前 GPU 模式! 必须用 `FW_GPU_CH`

**正确做法**: 切换时先读当前 `FW_GPU_CH` 的值, 将其写入 `FW_CurrentNewGPU`, 然后写入新的目标值到 `FW_GPU_CH`。这样确保两者不同, 启动服务才会触发切换。

```csharp
// 正确的注册表写入顺序
int currentMode = (int)k.GetValue("FW_GPU_CH");  // 读当前值
if (currentMode == targetMode)
    currentMode = targetMode == 0 ? 1 : 0;        // 确保不同
k.SetValue("FW_CurrentNewGPU", currentMode);       // 先写当前值
k.SetValue("FW_GPU_CH", targetMode);               // 再写目标值
```

---

## 6. MSI 后台服务依赖

### 6.1 MSI Foundation Service (MSIAPService.exe)

- **路径**: `C:\Program Files (x86)\Feature Manager\MSIAPService.exe`
- **类型**: Windows 服务 (Service)
- **启动方式**: `sc start "MSI Foundation Service"` 或 `ServiceController.Start()`
- **作用**: WMI ACPI 的后端, 提供 `\\.\pipe\simple` named pipe 通信
- **依赖**: 无, 可独立启动

### 6.2 Feature Manager Service.exe

- **路径**: `C:\Program Files (x86)\Feature Manager\Feature Manager Service.exe`
- **类型**: 普通进程 (非 Windows 服务)
- **启动方式**: `Process.Start()` 直接运行
- **作用**: 在后台执行某些关键动作, 使 EC 写入 + 注册表修改的组合在重启后生效
- **依赖**: **必须** MSIAPService 先启动, 否则立即退出
- **初始化时间**: 启动后需等待 ~2 秒

### 6.3 不需要的服务

| 服务 | 原因 |
|---|---|
| Feature Manager UI (Feature_Manager.exe) | 仅提供图形界面, 切换逻辑在 Service 中 |
| Micro Star SCM (MSIService.exe) | `Graphics_switch` 调用返回 `WBEM_E_NOT_SUPPORTED`, 不影响切换 |
| MSI Center Service | 完全无关 |

---

## 7. 完整切换流程 (已验证)

以下是经过实际验证的、与 MSI Center 行为完全一致的 GPU 模式切换流程:

### 前置条件

1. `MSI Foundation Service` (MSIAPService.exe) 正在运行
2. `Feature Manager Service.exe` 正在运行

### 切换步骤

```
步骤 0: 写注册表
  a) 读取当前 FW_GPU_CH 的值 → 作为 FW_CurrentNewGPU
     (如果恰好等于目标值, 人为设为其他值, 确保不同)
  b) 写入 FW_CurrentNewGPU = 当前值 (确保与目标不同)
  c) 写入 FW_GPU_CH = 目标模式 (0=Hybrid, 1=Discrete, 2=Eco)

步骤 1: 读取当前 AP 状态
  → Get_AP(cmd=0x00)
  → 取 byte[1]

步骤 2: 修改 byte[1]
  → 清 bit0, 清 bit1, 然后置 bit0=1
  → mod = (orig & ~0x03) | 0x01

步骤 3: 写入 EC 寄存器 0xD1
  → Set_Data(cmd=0xD1, byte[1]=mod)
  → 检查 ACK == 0x01

步骤 4: 等待 BIOS 响应
  → Sleep(2000)
  → 重读 Get_AP(cmd=0x00)
  → 检查 byte[2] bit1 是否置位 (BIOS 已确认)

步骤 5: 确认写入
  → Set_Data(cmd=0xBE, byte[1]=0x02)
  → 检查 ACK == 0x01

步骤 6: 提示用户重启
  → 重启后 BIOS 读取 EC 寄存器 + 注册表, 配置 MUX
```

### 流程图

```
写注册表 → Get_AP(0) → 改 byte[1] → Set_Data(0xD1) → 等2s → 检查bit1 → Set_Data(0xBE) → 重启
   ↓           ↓            ↓            ↓                ↓          ↓            ↓
 FW_GPU_CH   读状态     bit0=1      写EC 0xD1       BIOS处理   BIOS确认     提交写入
 FW_Current  bit1=0     bit1=0      ACK=0x01        bit1=1?    ACK=0x01     生效
```

---

## 8. 三模式切换: Eco/核显模式

### 8.1 发现过程

通过 ILDump 反编译 `Feature_Manager.exe` 的 `MainWindow` 类, 发现:

1. GPU 模式 ComboBox 有三个选项: Index 0=Hybrid, 1=Discrete, 2=Eco/UMA
2. `FW_GPU_CH = ComboBox.SelectedIndex` — 直接写入下拉框索引
3. `uxIntegratedMode` (ComboBoxItem) 是核显选项, 由 `isSupport_UMA_Switch` 控制可见性
4. `uxDiscreteMode` (ComboBoxItem) 是独显选项, 由 `isSupport_Discrete_Switch` 控制可见性

### 8.2 模式支持检测

注册表值决定了 UI 显示哪些选项:

```
FW_SupportUMA = 1     → 显示核显模式选项 (本机支持)
FW_SupportDiscrete = 1 → 显示独显模式选项 (本机支持)
FW_SupportNewGPU = 1   → 支持新版 EC 切换方式
```

### 8.3 Eco 模式切换的关键

**EC 写入流程完全相同** — 0xD1 → 0xBE 的 WMI ACPI 序列对三种模式都一样。
区别**仅在** `FW_GPU_CH` 的值:

| FW_GPU_CH | 模式 | 说明 |
|---|---|---|
| 0 | Hybrid / MSHybrid | 核显输出, 独显可 Optimus 渲染 |
| 1 | Discrete / dGPU | 独显直连输出 |
| 2 | Eco / iGPU / UMA | 仅核显, 独显完全关闭 |

这意味着: **BIOS 在 POST 阶段读取 `FW_GPU_CH` 的值来决定 MUX 配置**, EC 寄存器写入只是通知 BIOS "有变更待处理"。

---

## 9. GPU 状态读取

### 9.1 推荐方法: 注册表

```csharp
using var k = Registry.LocalMachine.OpenSubKey(MsiRegPath, writable: false);
int mode = (int)k.GetValue("FW_GPU_CH");
// 0=Hybrid, 1=Discrete, 2=Eco, 其他→默认Hybrid
```

**优点**: 简单可靠, 反映重启后的实际模式。
**注意**: 不要用 `FW_CurrentNewGPU`, 它在切换后是反值, 重启后也不更新。

### 9.2 备用方法: WMI ACPI

```csharp
// Get_AP(0) byte[1] bit0
byte[] ap0 = WmiCallGet("Get_AP", 0x00);
bool isDGpu = (ap0[1] & 0x01) != 0;

// Get_BIOS(0x02) byte[1]
byte[] bios = WmiCallGet("Get_BIOS", 0x02);
bool isDGpu = bios[1] == 0x40;
```

**优点**: 反映当前运行时状态 (不需要重启就能看到)。
**缺点**: 无法区分 Eco 和 Hybrid (WMI 位只有 dGPU/非dGPU 两种)。

### 9.3 快速状态检查 (qs 命令)

MSI GPUSwitch 的 `qs` 命令同时读取 7 个 GPU 状态位, 可交叉验证:

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

## 10. MSI GPUSwitch 命令参考

### 构建与运行

```powershell
dotnet build "MSI GPUSwitch\GpuSwitch.csproj" -c Release
# 以管理员身份运行
.\MSI GPUSwitch\bin\Release\net8.0-windows\win-x64\MSI GPUSwitch.exe
```

> **前置依赖**: GPU 切换依赖 MSI 的 WMI ACPI 基础设施, 需要先安装 **Feature Manager** (MSI Center 组件)。
> 项目根目录包含 `Feature Manager_1.0.2312.2201.exe` 安装包, 也可从 MSI 官网下载 MSI Center。
> 安装后, WMI ACPI 方法调用才能正常工作; 卸载 FM 会导致 WMI ACPI 永久挂起。
> 项目自带 `FeatureManager/` 目录 (包含 `MSIAPService.exe` 和 `Feature Manager Service.exe`), 构建时自动复制到输出目录。

### 命令列表

| 命令 | 功能 |
|---|---|
| `gpuD` | 切换到独显直连 (Discrete) |
| `gpuH` | 切换到混合输出 (Hybrid) |
| `gpuE` | 切换到核显模式 (Eco/iGPU) |
| `qs` | 快速查看 7 处 GPU 状态位 |
| `r` | 完整读取 GPU 模式状态 |
| `snap` | 保存全状态快照到 snapshot.txt |
| `7` | 调用 Get_WMI (列出支持的数据块) |
| `8` | 批量探测 Get_* / cmd 0x00~0x20 |
| `9` | 手动调用任意 Get_* 方法 |
| `p` | 描述 Package_32 类定义 |
| `q` | 用 Package_32 扫描所有 Get_* × cmd |
| `0` | 退出 |

### 切换示例

```
输入编号并回车: gpuE

── 🎯 GPU 切换到 核显模式 (Eco/iGPU) ──
  ✓ MSI Foundation Service 已在运行
  ✓ Feature Manager Service.exe 已在运行
  切换前注册表状态:
    FW_GPU_CH        = 0
    FW_CurrentNewGPU = 1
  步骤 0a: 注册表 FW_CurrentNewGPU: 1 -> 0 (当前实际模式)
  步骤 0b: 注册表 FW_GPU_CH: 0 -> 2 (目标模式)
  步骤 1: Get_AP(0) = 01 00 02 ...
  步骤 2: 改 byte[1]: 0x00 -> 0x01 (bit0=1, bit1=0)
  ⚠️ 此操作会真正写入 EC 寄存器 0xD1, 输入 YES 继续:
  > YES
  步骤 4: Set_Data(0xD1) ACK=0x01 (成功)
  步骤 5: 等待 2 秒...
          byte[2] bit1 = 1 (BIOS 已确认)
  步骤 6: Set_Data(0xBE) ACK=0x01
  ✓ 写入流程完成. 请重启电脑使 GPU MUX 切换生效.
```

---

## 11. 已验证的测试矩阵

### 服务依赖测试

| # | MSIAPService | FM Service | FM UI | SCM | 结果 |
|---|---|---|---|---|---|
| 1 | ✅ | ✅ | ❌ | ❌ | ✅ 切换成功 |
| 2 | ✅ | ❌ | ❌ | ❌ | ❌ 切换失败 |
| 3 | ❌ | ✅ | ❌ | ❌ | ❌ FM Service 立即退出 |
| 4 | ❌ | ❌ | ✅ | ❌ | ✅ FM UI 自动拉起服务 |

### 模式切换测试

| 源模式 | 目标模式 | FW_GPU_CH | 结果 |
|---|---|---|---|
| Hybrid | Discrete | 0→1 | ✅ 重启后生效 |
| Discrete | Hybrid | 1→0 | ✅ 重启后生效 |
| Hybrid | Eco/iGPU | 0→2 | ✅ 重启后生效 |
| Discrete | Eco/iGPU | 1→2 | ✅ 重启后生效 |
| Eco/iGPU | Hybrid | 2→0 | ✅ 重启后生效 |
| Eco/iGPU | Discrete | 2→1 | ✅ 重启后生效 |

### Graphics_switch (SCM) 测试

| 情况 | 结果 |
|---|---|
| 调用 SCM Graphics_switch 成功 | 切换成功 |
| SCM 管道不存在 (Win32Error=2) | 切换仍然成功 |
| SCM 返回 WBEM_E_NOT_SUPPORTED | 切换仍然成功 |

**结论**: `Graphics_switch` 不是必要步骤, 仅用于通知 SCM 更新 UI。

---

## 12. 已知陷阱与踩坑记录

### 陷阱 1: FW_CurrentNewGPU 反值

**现象**: 切换后 UI 显示的模式与实际相反。
**原因**: `FW_CurrentNewGPU` 在切换时被设为目标模式的反值, 且重启后不更新。
**解决**: 读取 `FW_GPU_CH` 而非 `FW_CurrentNewGPU` 来判断当前模式。

### 陷阱 2: FW_CurrentNewGPU == FW_GPU_CH

**现象**: 切换命令执行成功, 但重启后模式未变。
**原因**: 启动服务检查 `FW_CurrentNewGPU == FW_GPU_CH` 时认为无需切换。
**解决**: 切换时先读当前 `FW_GPU_CH` 的值写入 `FW_CurrentNewGPU`, 确保两者不同。

### 陷阱 3: Feature Manager Service 立即退出

**现象**: 启动 FM Service 后进程立刻消失。
**原因**: FM Service 依赖 MSIAPService, 后者未运行时 FM Service 自动退出。
**解决**: 先启动 MSI Foundation Service, 等 2 秒后再启动 FM Service。

### 陷阱 4: YAMDCC 服务与 MSI Foundation Service 冲突

**现象**: YAMDCC 服务启动时崩溃。
**原因**: YAMDCC 的 `IsMSIServiceRunning` 检测到 MSI Foundation Service 运行后故意崩溃。
**解决**: 从冲突检测列表中移除 MSI Foundation Service (GPU 切换需要它)。

### 陷阱 5: Feature Manager 卸载后 WMI ACPI 永久挂起

**现象**: 卸载 Feature Manager 后, WMI ACPI 方法调用 (Get_AP, Set_Data 等) 永久挂起。
**原因**: FM 安装时注册了内核级组件或 ACPI BIOS 交互, 卸载时被撤销。mofcomp 注册 MOF schema 无法修复。
**解决**: **不要卸载 Feature Manager**。如需避免 FM 服务自启, 将 MSI Foundation Service 设为手动启动, 禁用 Micro Star SCM。

### 陷阱 6: Set_BIOS / Set_Device 切换无效

**现象**: 调用 `Set_BIOS(0x02, 0x40)` 或 `Set_Device(0x01, bit6=1)` 后, 寄存器值改变了但重启后不生效。
**原因**: 这些方法只修改运行时状态, 不触发 BIOS 的 MUX 重配置流程。
**解决**: 必须使用 `Set_Data(0xD1)` + `Set_Data(0xBE)` 的两步写入流程。

---

## 13. 整合进 YAMDCC-3.0

MSI GPUSwitch 的切换逻辑已整合进 YAMDCC-3.0 的 `FanControlService.cs`:

- **`GetGpuMode()`**: 读取 `FW_GPU_CH` 注册表值, 返回 0/1/2/-1
- **`SetGpuMode(int mode)`**: 完整执行服务启动 → 注册表写入 → EC 写入流程
- **IPC 命令**: `SetGpuMode 0/1/2`, `GetGpuMode`
- **UI**: 三个 GPU 模式按钮 (核显/混合/独显), 彩色边框指示当前模式

---

## 14. 相关文件索引

| 文件 | 说明 |
|---|---|
| `AcpiProbe.cs` | WMI ACPI 调用核心, 包含所有探测和切换逻辑 |
| `Program.cs` | 命令行入口, 命令解析 |
| `PROGRESS.md` | 研发过程记录 |
| `gpu_il.txt` | Feature_Manager.MainWindow 的 IL 反编译 (GPU 切换部分) |
| `scm_il.txt` | API_Dynamic.SCM 的 IL 反编译 (Graphics_switch 部分) |
| `load_il.txt` | API_Dynamic.SCM.LoadWMIValue 的 IL 反编译 |
| `mainwindow_il.txt` | Feature_Manager.MainWindow 完整 IL 反编译 |
| `tools/ILDump/` | 自研 IL 反编译工具 (基于 Mono.Cecil) |

### 外部文件

| 路径 | 说明 |
|---|---|
| `C:\Program Files (x86)\Feature Manager\` | Feature Manager 安装目录 |
| `C:\Program Files (x86)\Feature Manager\MSIAPService.exe` | MSI Foundation Service |
| `C:\Program Files (x86)\Feature Manager\Feature Manager Service.exe` | Feature Manager Service |
| `C:\Program Files (x86)\Feature Manager\Feature_Manager.exe` | Feature Manager UI |
| `C:\Program Files (x86)\Feature Manager\WMILib.dll` | WMI 通信库 |
| `C:\Program Files (x86)\Feature Manager\MSIWMIACPI2.dll` | WMI ACPI 通信库 |

---

## 许可与免责

本工具仅用于学习和研究目的。使用本工具修改 EC 寄存器和 BIOS 设置存在风险,
请确保你了解自己在做什么。作者不对任何因使用本工具导致的损坏负责。

`FeatureManager/` 目录中包含的文件为 Micro-Star International (MSI) 所有,
仅出于互操作性和研究目的包含于此, 不主张任何所有权或许可。

详见 [DISCLAIMER](DISCLAIMER)。
