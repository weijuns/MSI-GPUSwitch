# MSI GPUSwitch 交接文档

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
