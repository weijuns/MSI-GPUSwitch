# MSI GPUSwitch 交接文档

**最近更新**: 2026-04-28 — 完成 **完全脱离 Feature Manager 的破解** (单向 Hybrid→Discrete 已实测成功)  
**详细原理**: 见 [`BREAKTHROUGH.md`](./BREAKTHROUGH.md)

---

## 🎯 2026-04-28 重大突破: 完全脱离 Feature Manager

### 核心发现

之前以为 FM 装着了 "某个神秘内核组件"。实际上 FM 安装时只做了**两件简单的事**:
1. 复制 `msiapcfg.dll` (16KB BMF-in-PE) 到 `C:\Windows\SysWOW64\`
2. 设置注册表 `HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi\MofImagePath`

`msiapcfg.dll` 是 BMF (Binary MOF) 包装在 PE 里. Windows 内置驱动 `wmiacpi.sys` 通过 `MofImagePath` 加载它, 把 `MSI_ACPI`/`Package_32` 等 ACPI WMI 类绑定到 BIOS 的 `_WMI` 方法.

### MSI GPU 切换的真正完整公式

```
1. WMI ACPI 引导  : msiapcfg.dll → SysWOW64 + MofImagePath 注册表
2. 注册表         : FW_GPU_CH = 目标模式 + FW_CurrentNewGPU != 目标
3. EC 写入        : Set_Data(0xD1, byte[1]|=0x01) → Set_Data(0xBE, 0x02)
4. UEFI 变量      : MsiDCVarData byte[5] bit0/bit1 = 模式编码
5. 冷启动 (S5→S0) : 必须关机+开机, 不能热重启!
```

完全不需要 Feature Manager / MSI Center 的任何进程或服务.

### 实测结果 (绝影 14)

| 方向 | 状态 |
|---|---|
| **Hybrid → Discrete** | ✅ 完全成功 (无 FM, 一次冷启动) |
| **Discrete → Hybrid** | ⚠️ 部分 (BIOS 持久位接受, 硬件 MUX 未执行) — 见 BREAKTHROUGH.md "已知限制" |

### 新增代码

- `WmiAcpiBootstrap.cs` — `bs`/`boot`/`unboot` 命令: 引导 wmiacpi.sys 加载 BMF
- `UefiVariable.cs` — `uv`/`uvw` 命令: 读写 UEFI MsiDCVarData
- `MsiApService.cs` — `srv*` 命令: 管理 MSIAPService 服务 (实测可选)
- `AcpiProbe.cs` `ReplayMsiCenterSwitch` — 集成 UEFI 写入 + 冷启动提示
- `FeatureManager/msiapcfg.dll` — 16KB BMF, 嵌入资源

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

## 二、仍然存在的问题和不足

### 🔴 严重问题

1. **FM 卸载导致 WMI ACPI 永久挂起的根因未查明** — 这是最大的未解之谜。FM 安装时做了某些底层操作（可能是内核驱动注册或 ACPI BIOS 交互），卸载时撤销，导致 WMI 方法调用挂起。已排除:
   - 不是 MOF schema 问题（mofcomp 注册完整 schema 无效）
   - 不是 WMI 仓库损坏（salvagerepository 无效）
   - 不是 WmiAcpi 驱动问题（驱动正常运行）
   - 不是 PNP0C14 设备问题（设备状态 OK）
   - 不是 `msisadrv.sys` 驱动问题（该驱动在 FM 卸载前后都存在）

   **可能的原因**:
   - FM 安装时通过 `ServiceInstall.exe` 注册了某个内核驱动或 ACPI 过滤器
   - `KernCoreLib64.Sys` (Subsystem=0) 可能是关键组件，但安装方式未知
   - FM 安装时修改了 ACPI BIOS 的 WMI 数据块配置（如 WMI 方法实现的 GUID 映射）

2. **Feature Manager Service.exe 无法独立运行** — 它是 WPF 应用，启动时在 `MainWindow..ctor()` 中因缺少 MSI Center 组件而崩溃。这意味着在没有 MSI Center 的机器上无法运行 FM Service。

### 🟡 中等问题

3. **仅测试了绝影 14 (Stealth 14)** — 所有测试均在 MSI 绝影 14 上进行，其他机型的行为可能不同。

4. **Eco 模式与 Hybrid 模式在 WMI 层面无法区分** — `Get_AP` 和 `Get_BIOS` 的状态位只有 dGPU/非dGPU 两种，无法通过 WMI 区分 Eco 和 Hybrid。只能通过注册表 `FW_GPU_CH` 判断。

5. **Set_BIOS / Set_Device 切换无效** — 这些方法只修改运行时状态，不触发 BIOS MUX 重配置。只有 `Set_Data(0xD1)` + `Set_Data(0xBE)` 的两步流程才有效。

### 🟢 轻微问题

6. **ILDump 工具功能有限** — 只能输出 IL 指令文本，不能还原高级 C# 代码。

7. **GPU 切换需要重启** — 硬件限制，BIOS 需要在 POST 阶段读取 EC 寄存器配置 MUX。

---

## 三、关键文件索引

| 文件 | 说明 |
|---|---|
| `AcpiProbe.cs` | WMI ACPI 调用核心，所有探测和切换逻辑 |
| `Program.cs` | 命令行入口，命令解析 |
| `FeatureManager/` | FM 必要文件 (MSIAPService.exe, Feature Manager Service.exe 等) |
| `Feature Manager_1.0.2312.2201.exe` | FM 安装包 |
| `tools/ILDump/` | IL 反编译工具 |
| `gpu_il.txt` | Feature_Manager.MainWindow 的 IL 反编译 (GPU 切换部分) |
| `scm_il.txt` | API_Dynamic.SCM 的 IL 反编译 (Graphics_switch 部分) |
| `PROGRESS.md` | 研发过程记录 |

---

## 四、后续工作建议

### 优先级 1: 查明 FM 卸载导致 WMI 挂起的根因

1. **用 Procmon 监控 FM 安装过程** — 过滤 `RegSetValue`, `WriteFile`, `CreateFile` 操作，找出 FM 注册了什么驱动或修改了什么系统配置
2. **反编译 ServiceInstall.exe** — FM 安装目录中的 `ServiceInstall.exe` 可能负责注册内核驱动
3. **对比 FM 安装前后的驱动列表** — `sc.exe query type= driver state= all` 对比差异
4. **检查 FM 安装时的 INF 文件** — 可能有 `.inf` 文件描述了驱动安装

### 优先级 2: 尝试独立安装关键组件

如果能找到 FM 安装时注册的内核组件，尝试:
1. 从 FM 安装目录提取该组件
2. 用 `sc.exe create` 或 `rundll32` 独立安装
3. 不安装 FM，只安装该组件，测试 WMI 是否可用

### 优先级 3: 多机型适配

1. 在不同 MSI 笔记本上测试 GPU 切换
2. 记录不同机型的 EC 寄存器差异
3. 建立机型-配置映射表

### 优先级 4: 完善文档

1. 补充更多 WMI 方法的逆向分析（如 `Get_EC`, `Set_EC` 等）
2. 记录 ACPI BIOS WMI 数据块的 GUID 映射关系
3. 编写开发者指南
