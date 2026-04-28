# 🎯 BREAKTHROUGH — 完全摆脱 Feature Manager 的 MSI GPU 切换

**日期**: 2026-04-28  
**状态**: ✅ **已实测验证成功** (绝影 14, 卸载 FM 状态下完成 Hybrid → Discrete 切换)

---

## 一句话总结

**MSI GPU 切换的真正完整公式 = WMI ACPI 引导 + EC 写入 + UEFI 变量写入 + 冷启动。完全不需要 Feature Manager / MSI Center 任何进程或服务。**

四个关键发现:
1. `msiapcfg.dll` + `MofImagePath` 注册表 — 让 wmiacpi.sys 加载 MSI ACPI BMF
2. EC 写入 0xD1 / 0xBE — 通知 BIOS 切换请求
3. **UEFI 变量 MsiDCVarData byte[5] bit0/bit1** — BIOS POST 时读取的真正提交位
4. **必须冷启动 (关机+开机), 不能热重启** — EC 不断电时 BIOS 不应用 MUX 切换

---

## 发现过程

### 1. 反编译 MSIWMIACPI2.dll 找到 InstallService()

`MSIWMIACPI2.dll` 是 .NET 程序集，里面 `WmiAcpiLib2.InstallService()` 方法的 IL 揭示了 FM 安装时做的关键操作:

```il
// 1. 找 entry assembly 同目录的 msiapcfg.dll
ldstr "msiapcfg.dll"
Path::Combine(entryDir, "msiapcfg.dll")

// 2. 复制到 SystemX86 (= C:\Windows\SysWOW64 on x64)
ldc.i4.s 41   // SpecialFolder.SystemX86
Environment::GetFolderPath(...)
XcopyFile(src, dst)

// 3. 写注册表
HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi
  MofImagePath = <SysWOW64>\msiapcfg.dll

// 4. 提示重启
"Installed WMIACPI2 service. Please reboot to activate it."
```

### 2. 验证当前注册表

```
HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi
    ImagePath        = \SystemRoot\System32\drivers\wmiacpi.sys  ← Windows 自带驱动
    MofImagePath     = %windir%\sysWOW64\msiapcfg.dll            ← MSI 注入点 ⭐
    Type             = 0x1 (kernel driver)
    Start            = 0x3 (demand)
```

### 3. 验证文件本质

```
C:\Windows\SysWOW64\msiapcfg.dll
  Size: 16,624 bytes
  Magic: 4D 5A 90 00 (MZ — 伪装 PE)
  Contains: "FOMB" (Binary MOF 魔术字反转, 即 "BMOF" 大端)
```

**结论**: `msiapcfg.dll` 不是真的 DLL, 是把 BMF (Binary MOF) 包装在 PE 资源段里的"皮"。Windows `wmiacpi.sys` 加载时通过 `MofImagePath` 找到它, 提取里面的 BMF, 把 `MSI_ACPI`/`Package_32` 等 ACPI WMI 类注册到 `root\wmi` 命名空间, 并把类的 ACPI 方法绑定到 ACPI BIOS 的对应 _WMI 方法 (PNP0C14 设备暴露的)。

---

## 为什么之前所有"修复"方案都失败

| 方案 | 失败原因 |
|---|---|
| `mofcomp MSI_ACPI.mof` | mofcomp 注册到 WMI 仓库 (`%SystemRoot%\System32\wbem\Repository`), 不会让 `wmiacpi.sys` 加载 ACPI 方法绑定 |
| `winmgmt /salvagerepository` | 同上, 跟 ACPI 驱动加载无关 |
| 重启 | 重启时 `wmiacpi.sys` 仍然没有 `MofImagePath`, 自然不加载任何 MOF |
| 禁用/启用 PNP0C14 | 设备本身没问题, 缺的是驱动加载时的 BMF |
| 完整 MOF schema | MOF 类定义没问题, 但缺乏 ACPI 方法实现的绑定信息 |

**关键认知**: `wmiacpi.sys` 加载 BMF 时, 不只是注册类定义, **还把每个 WMI 方法 (Get_AP, Set_Data 等) 绑定到 ACPI BIOS 中对应的 `_WMI` 方法**。这个绑定信息编码在 BMF 里 (用 GUID 关联), mofcomp 走的是不同代码路径, 不会做这个绑定。

---

## 完美解决方案

### 一次性引导 (重启即生效, 永久有效)

```csharp
public static class WmiAcpiBootstrap
{
    // 1. 把嵌入的 msiapcfg.dll 写到 SysWOW64
    var sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
    var dst = Path.Combine(sysWow64, "msiapcfg.dll");
    if (!File.Exists(dst))
    {
        File.WriteAllBytes(dst, EmbeddedResource.Read("msiapcfg.dll"));
    }
    
    // 2. 设置 MofImagePath 注册表
    using var key = Registry.LocalMachine.OpenSubKey(
        @"SYSTEM\CurrentControlSet\Services\WmiAcpi", writable: true);
    var current = key.GetValue("MofImagePath") as string;
    var expected = @"%windir%\sysWOW64\msiapcfg.dll";
    if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
    {
        key.SetValue("MofImagePath", expected, RegistryValueKind.String);
        // 提示重启
    }
}
```

### 然后一切正常

- 不需要 Feature Manager
- 不需要 MSI Foundation Service (MSIAPService.exe)
- 不需要 Feature Manager Service.exe
- 不需要 Micro Star SCM
- WMI ACPI 调用直接工作 (Get_AP / Set_Data 等)

### 我们仍然需要的

- WinRing0 / EC 直接访问 (已有, 用于风扇控制)
- `msiapcfg.dll` 文件本身 (16KB, 嵌入资源)
- 注册表写权限 (需要管理员)

---

## 与旧方案对比

| 项 | 旧方案 (依赖 FM) | 新方案 (msiapcfg 引导) |
|---|---|---|
| FM 安装 | 必须 | 不需要 |
| MSI Center | 推荐 | 不需要 |
| 服务管理 | 复杂 (停 SCM, 启 MFS) | 不涉及 |
| WMI 仓库 | 可能损坏 | 不依赖 |
| 用户体验 | 装了 MSI Center 还要禁用其服务 | 一键安装, 重启即用 |
| 卸载干净 | 困难 | 删一个 DLL + 一个注册表项 |
| 项目大小 | +20MB (FM 安装包) | +16KB (msiapcfg.dll) |

---

## 后续待实施

1. ✅ 已确认 `msiapcfg.dll` 本质 (BMF 包装在 PE)
2. ✅ 已复制 `msiapcfg.dll` 到两个项目的 `FeatureManager/` 目录
3. ✅ 在 MSI GPUSwitch 添加 `WmiAcpiBootstrap.cs` 实现引导逻辑
4. ✅ 在 MSI GPUSwitch 加菜单项触发 (`bs`/`boot`/`unboot`)
5. ✅ 添加 UEFI 变量 MsiDCVarData 读写 (`UefiVariable.cs`, `uv` 命令)
6. ✅ 卸载 FM 测试: 引导 + UEFI 写入 + 冷启动 → **GPU 切换成功**
7. ⏳ 在 MSI Flux Service 集成自动引导 + UEFI 写入
8. ⏳ 更新 README, 移除 FM 依赖说明
9. ⏳ 集成自动关机提示 (替代重启提示)

---

## 完整切换流程 (已验证, 无 FM)

### 一次性引导 (用户首次使用)

```csharp
WmiAcpiBootstrap.Install();
// 1. 复制 msiapcfg.dll → C:\Windows\SysWOW64\msiapcfg.dll
// 2. 设置 HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi\MofImagePath
// 3. 提示重启 (一次性, 永久生效)
```

### 每次切换 (用户主动操作)

```csharp
// 1. 注册表 (CreateSubKey 自动创建路径, FM 卸载后路径会被删)
HKLM\SOFTWARE\WOW6432Node\MSI\Feature Manager\Component\Base Module\User Scenario
  FW_CurrentNewGPU = 当前模式
  FW_GPU_CH        = 目标模式

// 2. WMI ACPI 写 EC
Get_AP(0)                         → byte[1] (现状)
mod = (byte[1] & ~0x03) | 0x01    → 设 bit0
Set_Data(0xD1, [mod])             → ACK=0x01
Sleep(2000)
Get_AP(0)                         → 检查 byte[2] bit1 (BIOS ack)
Set_Data(0xBE, [0x02])            → ACK=0x01

// 3. UEFI 变量
ReadFirmwareEnvironmentVariable("MsiDCVarData", {DD96BAAF-...})
  → 20 字节
byte[5] = (byte[5] & 0xFC) | mode_bits
  // mode=0 (Hybrid):   bit0=0, bit1=0
  // mode=1 (Discrete): bit0=1
  // mode=2 (Eco/UMA):  bit1=1
SetFirmwareEnvironmentVariable("MsiDCVarData", ..., 20)

// 4. 提示用户冷启动 (不是重启!)
Console.WriteLine("请关机后开机, 不要重启");
```

---

## 关键陷阱: 必须冷启动

**冷启动 (Cold Boot)** = 关机后断电几秒, 然后按电源键开机  
**热重启 (Warm Boot)** = `shutdown /r` 或 开始菜单 → 重启

| 启动类型 | EC 状态 | BIOS POST 行为 |
|---|---|---|
| 热重启 | EC 保留电源, 寄存器值不变 | 跳过 MUX 重配置 |
| 冷启动 | EC 短暂断电 + 重新初始化 | **完整 POST → 读 EC + UEFI → 应用 MUX** |

实测: 在 UEFI 变量已正确写入、EC 已 ACK 的情况下:
- 热重启 → BIOS 仍是 Hybrid (切换失败)
- 冷启动 → BIOS 切到 Discrete (切换成功) ✅

MSI Center 的源码也证实这点: `shutdown.exe -f -s -t 0` 而非 `-r`.

---

## 核心代码位置

| 功能 | 文件 |
|---|---|
| WMI ACPI 引导 (msiapcfg + MofImagePath) | `WmiAcpiBootstrap.cs` |
| UEFI 变量读写 | `UefiVariable.cs` |
| GPU 切换主流程 | `AcpiProbe.cs` 中 `ReplayMsiCenterSwitch` |
| 嵌入资源 | `MSI GPUSwitch/FeatureManager/msiapcfg.dll` |

---

## 与 PROGRESS.md 旧结论的修正

PROGRESS.md 之前认为 "MSIAPService + FM Service 必须运行"。**实际上完全不必要**:
- 那次测试是装着 FM 的状态, MSIAPService 自动启动
- 真正起作用的是 FM 安装时 InstallService() 设置的 `MofImagePath` + `msiapcfg.dll`
- 服务在跑只是顺带, 跟切换无关

PROGRESS.md 也认为 "Graphics_switch named pipe 不必要"。**确认正确**, 我们没调用它也能成功 (只要冷启动)。

---

## ✅ 最终突破 (2026-04-28 上午): 双向切换全部打通

### 关键对照实验

| 测试场景 | gpuD (Hybrid→Discrete) | gpuH (Discrete→Hybrid) |
|---|---|---|
| 完全没 FM | ✅ 成功 | ❌ MUX 未执行 |
| 装回 FM (FM Service + MSIAPService 跑着) | ✅ 成功 | **✅ 成功** |

### 结论

**我们的代码逻辑 (注册表 + EC + UEFI + 冷启动) 100% 正确**.  
Discrete → Hybrid 在无 FM 时失败的根因是: **`MSIAPService.exe` (或 `Feature Manager Service.exe`) 必须在用户态跑着**.

具体机制 (推测):
- 这两个服务在跑时, 通过某个 ACPI 事件 / SMM 共享内存通信, 让 BIOS POST 能完成 MUX 物理切换 (清 byte[5] bit2, 实际切换显示输出 MUX)
- 缺这个用户态进程时, BIOS 看到切换请求但跳过 MUX 执行 (可能 MSI 故意做了 OS-cooperation gate)

### 修正之前的误解

byte[5] 状态机 (修正后):
```
0x30 = Hybrid 稳定          (bit 0,1 = 00 请求 Hybrid; bit 2 = 0 不锁)
0x31 = Discrete 锁定中      (BIOS POST 检测到请求, EC 写入后立即设)
0x35 = Discrete 已稳定/锁定 (bit 0 = 1 请求 Discrete, bit 2 = 1 BIOS 确认锁定)
0x34 = Hybrid 请求, 等待 BIOS 解锁 (bit 2 = 1 还在锁定 Discrete 状态)
```

之前误判: 以为 0x35 是中间态, 实际上 0x35 是 Discrete 的稳定状态.  
真正的"中间态"是 0x34 (从 0x35 改 bit 0 为 0, 但 bit 2 锁定位还在).

`AP[0x00].byte[3]`:
- 之前 PROGRESS.md 说 dGPU=0xC2 / Hyb=0xC0 — **这是错的**
- 实测两个模式都是 0xC2, 这个寄存器跟 GPU 模式无关

真正的 GPU 模式指示位:
- `Device[0x01].byte[1]` bit6 (Discrete=1, Hybrid=0)
- `BIOS[0x04].byte[1]` bit2 (Discrete=1, Hybrid=0)  
- `MsiDCVarData[5]` bit0,1 (请求位) 与 bit2 (锁定位)

### 实施方案

要让 MSI Flux 真正零依赖 FM, 需要嵌入并管理:
1. `msiapcfg.dll` (16KB BMF) → SysWOW64 + MofImagePath 注册表 (一次性)
2. `MSIAPService.exe` (~50KB .NET 服务) → 安装为 Windows Service 并保持运行
3. *(可能也需要)* `Feature Manager Service.exe` — 待进一步实验确认

### 待验证实验 (下一步)

| 实验 | 目标 |
|---|---|
| 完全卸 FM, 单独装 MSIAPService 让它跑, 然后 gpuH | 确认是否单 MSIAPService 就够 |
| 卸 FM, 装 FM Service 但不装 MSIAPService, gpuH | 确认 FM Service 是否必要 |
