# MSI GPUSwitch 交接文档

**最近更新**: 2026-05-02 — **双向切换全部打通** (Hybrid ↔ Discrete 双向切换均已实测成功)  
**详细原理**: 见 [`BREAKTHROUGH.md`](./BREAKTHROUGH.md)

---

## 🎯 2026-05-02 突破: 双向切换全部打通

### 核心发现

之前以为 FM 装着了 "某个神秘内核组件"。实际上 FM 安装时至少做了**两件可直接确认的事**:
1. 复制 `msiapcfg.dll` (16KB BMF-in-PE) 到 `C:\Windows\SysWOW64\`
2. 设置注册表 `HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi\MofImagePath`

`msiapcfg.dll` 是 BMF (Binary MOF) 包装在 PE 里. Windows 内置驱动 `wmiacpi.sys` 通过 `MofImagePath` 加载它, 把 `MSI_ACPI`/`Package_32` 等 ACPI WMI 类绑定到 BIOS 的 `_WMI` 方法.

### MSI GPU 切换的完整公式

```
1. WMI ACPI 引导  : msiapcfg.dll → SysWOW64 + MofImagePath 注册表 (一次性)
2. MSI 辅助服务   : 按需启动 MSIAPService (OS-cooperation gate, Discrete→Hybrid 必需)
3. 注册表写入     : FW_GPU_CH = 目标模式 + FW_CurrentNewGPU != 目标
4. EC 命令        : Set_Data(0xD1, byte[1]|=0x01) → Set_Data(0xBE, 0x02)
5. UEFI 变量      : MsiDCVarData byte[5] bit0/bit1 = 模式编码
6. 清理           : 自动 Kill FM Service + 停止 MSIAPService (避免关机崩溃)
7. 关机提示       : 可选执行 shutdown -f -s -t 0
8. 冷启动 (S5→S0) : 必须关机+开机, 不能热重启!
```

### 实测结果 (绝影 14)

| 方向 | 状态 |
|---|---|
| **Hybrid → Discrete** | ✅ 成功 (自动辅助模式) |
| **Discrete → Hybrid** | ✅ 成功 (自动辅助模式) |
| **Hybrid → Eco/iGPU** | ✅ 成功 |
| **Discrete → Eco/iGPU** | ✅ 成功 |
| **Eco/iGPU → Hybrid** | ✅ 成功 |
| **Eco/iGPU → Discrete** | ✅ 成功 |

**关键**: Discrete → Hybrid 需要 MSIAPService 在用户态跑着 (OS-cooperation gate). 工具已集成自动管理: 按需启动, 切换完成后自动停止, 服务设为手动启动 (开机不自启).

### 代码架构

| 文件 | 说明 |
|---|---|
| `AcpiProbe.cs` | WMI ACPI 调用核心, 探测 + 切换逻辑 + `CleanupMsiHelpers()` |
| `Program.cs` | 命令行入口, 主菜单 (精简) + 高级调试子菜单 (`dbg`) |
| `MsiApService.cs` | MSIAPService 自动管理 (安装/启动/停止/卸载, 自动设为手动启动) |
| `WmiAcpiBootstrap.cs` | WMI ACPI 一次性引导 (`bs`/`boot`/`unboot`) |
| `UefiVariable.cs` | UEFI 变量读写 (`uv`/`uvw`) |
| `WmiProbe.cs` | WMI 类枚举和探测 (只读) |
| `SystemInfo.cs` | 系统信息显示 (机型/BIOS/显卡) |

---

**日期**: 2026-04-27  
**作者**: Cascade (AI 辅助开发)

---

## 一、已完成的工作

### 1. GPU 切换逆向工程

完整逆向了 MSI Center 的 GPU 三模式切换机制，包括:
- WMI ACPI 接口 (`MSI_ACPI` 类的 `Get_AP` / `Set_Data` 方法)
- 注册表机制 (`FW_GPU_CH` / `FW_CurrentNewGPU`)
- MSI 后台服务依赖 (`MSIAPService.exe` + `Feature Manager Service.exe`)
- 完整切换流程 (注册表 → Get_AP → Set_Data(0xD1) → 等待 → Set_Data(0xBE))

### 2. 探测工具 (GpuSwitch.exe)

交互式命令行工具，支持:
- GPU 模式切换 (`gpuD` / `gpuH` / `gpuE`)
- WMI ACPI 全量探测 (`q`, `d`, `8`, `9`)
- GPU 状态快速查看 (`qs`, `r`)
- 全状态快照 (`snap`)

### 3. IL 反编译工具 (tools/ILDump/)

基于 Mono.Cecil 的 IL 反编译器，用于分析 `Feature_Manager.exe` 和 `MSIService.exe`。

### 4. Feature Manager 依赖确认

经过大量测试确认: **Feature Manager 必须安装**，卸载后 WMI ACPI 方法调用永久挂起。

已验证的失败方案:
- `mofcomp` 注册完整 MOF schema (15 个 WMI 类) → 方法调用仍挂起
- WMI 仓库重建 → 无效
- 系统重启 → 无效
- PNP0C14 设备禁用/启用 → 无效

### 5. WMI ACPI 类结构文档化

完整记录了 15 个 MSI WMI 类及其 GUID:
- `Package` (ABBC0F60), `Package_1` (ABBC0F61), `Package_10` (ABBC0F62), `Package_32` (ABBC0F63)
- `MSI_ACPI` (ABBC0F6E) — 主方法类，29 个方法
- 9 个数据块类: MSI_AP, MSI_CPU, MSI_Device, MSI_Event, MSI_Master_Battery, MSI_Power, MSI_Slave_Battery, MSI_Software, MSI_System, MSI_VGA

### 6. README 更新

- 添加 FM 依赖说明和安装包信息
- 添加陷阱 5: FM 卸载后 WMI ACPI 永久挂起
- 中英文 README 均已更新

---

## 二、当前状态与已知限制

### ✅ 已解决

1. **WMI ACPI 引导问题** — 已通过 `msiapcfg.dll + MofImagePath` 注册表解决, 完全脱离 Feature Manager
2. **双向 GPU 切换** — Hybrid ↔ Discrete 双向切换全部打通, 6 个方向均已验证
3. **MSIAPService 开机自启** — 安装后自动设为 `start=demand` (手动启动), 开机不自启
4. **FM Service 关机崩溃 (0xe0434352)** — 切换后自动 `CleanupMsiHelpers()` 终止 FM Service + 停止 MSIAPService
5. **菜单臃肿** — 主菜单精简为核心功能, 调试命令通过 `dbg` 子菜单访问

### 🟡 已知限制

1. **仅测试了绝影 14 (Stealth 14)** — 所有测试均在 MSI 绝影 14 上进行, 其他机型的行为可能不同

2. **Eco 模式与 Hybrid 模式在 WMI 层面无法区分** — `Get_AP` 和 `Get_BIOS` 的状态位只有 dGPU/非dGPU 两种, 只能通过注册表 `FW_GPU_CH` 判断

3. **GPU 切换需要冷启动** — 硬件限制, BIOS 需要在 POST 阶段读取 EC 寄存器配置 MUX

4. **FM 卸载导致 WMI ACPI 永久挂起的根因** — 已通过 `boot` 命令绕过 (不需要安装 FM), 但 FM 卸载时的底层机制仍未完全查明

---

## 三、关键文件索引

| 文件 | 说明 |
|---|---|
| `AcpiProbe.cs` | WMI ACPI 调用核心, 探测 + 切换逻辑 + `CleanupMsiHelpers()` |
| `Program.cs` | 命令行入口, 主菜单 (精简) + 高级调试子菜单 (`dbg`) |
| `MsiApService.cs` | MSIAPService 自动管理 (安装/启动/停止/卸载, 自动设为手动启动) |
| `WmiAcpiBootstrap.cs` | WMI ACPI 一次性引导 (`bs`/`boot`/`unboot`) |
| `UefiVariable.cs` | UEFI 变量读写 (`uv`/`uvw`) |
| `WmiProbe.cs` | WMI 类枚举和探测 (只读) |
| `SystemInfo.cs` | 系统信息显示 (机型/BIOS/显卡) |
| `FeatureManager/` | FM 必要文件 (MSIAPService.exe, Feature Manager Service.exe 等) |
| `Feature Manager_1.0.2312.2201.exe` | FM 安装包 |
| `tools/ILDump/` | IL 反编译工具 |
| `gpu_il.txt` | Feature_Manager.MainWindow 的 IL 反编译 (GPU 切换部分) |
| `scm_il.txt` | API_Dynamic.SCM 的 IL 反编译 (Graphics_switch 部分) |
| `PROGRESS.md` | 研发过程记录 |

---

## 四、后续工作建议

### 优先级 1: 多机型适配

1. 在不同 MSI 笔记本上测试 GPU 切换
2. 记录不同机型的 EC 寄存器差异
3. 建立机型-配置映射表

### 优先级 2: 完善文档

1. 补充更多 WMI 方法的逆向分析（如 `Get_EC`, `Set_EC` 等）
2. 记录 ACPI BIOS WMI 数据块的 GUID 映射关系
3. 编写开发者指南

### 优先级 3: 查明 FM 卸载导致 WMI 挂起的根因 (学术兴趣)

已通过 `boot` 命令绕过此问题, 但原理仍未完全查明:
1. FM 安装时通过 `ServiceInstall.exe` 注册了什么?
2. `KernCoreLib64.Sys` (Subsystem=0) 的作用?
3. FM 安装时是否修改了 ACPI BIOS 的 WMI 数据块配置?
