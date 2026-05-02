# GPU Switch 完全破解

## 🎉 状态: 全部成功 (2026-04-26 / 2026-04-28 / 2026-05-02)

经过反编译 Feature_Manager.exe、`MSIWMIACPI2.dll` 以及大量测试, 我们已经完全复刻了 MSI 的 GPU 切换机制.

**2026-05-02 更新**: Hybrid ↔ Discrete 双向切换全部打通! 工具已集成 MSI 辅助服务自动管理:
- 按需启动 MSIAPService (OS-cooperation gate)
- 切换完成后自动停止 + Kill FM Service (避免关机崩溃 0xe0434352)
- 服务设为手动启动 (开机不自启)
- 菜单精简: 主页显示核心功能, 调试命令通过 `dbg` 子菜单访问

## 最终切换公式

**前置条件:** WMI ACPI 已引导 (一次性 `boot` 命令完成)

**切换步骤:**
1. 检查并按需启动 MSIAPService (自动管理, 无需手动操作)
2. 检查并按需启动 Feature Manager Service.exe 进程
3. 写注册表 `FW_CurrentNewGPU` = 当前实际模式 (目标的反值, 确保与 FW_GPU_CH 不同)
4. 写注册表 `FW_GPU_CH` = 目标模式 (1=Discrete, 0=Hybrid, 2=Eco/iGPU)
5. 写 UEFI 变量 MsiDCVarData byte[5] bit0/bit1 = 模式编码
6. `Get_AP(0)` → 取 byte[1], 改 bit0=1, bit1=0
7. `Set_Data(0xD1, [修改后的byte])` — 写 EC 寄存器
8. 等 2 秒, 重读 `Get_AP(0)` 检查 byte[2] bit1 (BIOS 置位)
9. `Set_Data(0xBE, [0x02])` — 确认写入
10. 清理: Kill FM Service 进程 + 停止 MSIAPService
11. 提示用户是否关机 (shutdown -f -s -t 0)
12. 关机后重新开机, BIOS POST 应用 MUX 切换

## 核心原理

- **EC 寄存器 `0xD1`/`0xBE`** 是 GPU 模式的持久化位, 写入即使重启也保留
- **注册表 `FW_GPU_CH`** 被 MSI 启动服务在开机时读取, 决定最终 MUX 配置
- **`FW_CurrentNewGPU` 必须与 `FW_GPU_CH` 不同**, 否则启动服务认为无需切换
- **`Feature Manager Service.exe` 是切换成功的必要条件** — 它在后台做了某些关键动作 (具体机制待深入分析)
- **`Graphics_switch` (SCM.SetWMIValue) 不是必要步骤** — 经测试验证, 跳过此步也能成功切换
- **不需要 Feature Manager UI, 不需要 Micro Star SCM 服务**

## 关键发现历程

### 1. EC 寄存器写入 (已验证)
- `Set_Data(0xD1)` ACK=0x01 → 成功
- `Set_Data(0xBE)` ACK=0x01 → 成功
- BIOS 在 2 秒内响应 bit1 置位

### 2. Graphics_switch 不是必要的
- IL 分析显示 MSI Center 会调用 `SCM.SetWMIValue("Graphics_switch", true, 0)`
- 通过 `\\.\pipe\simple` named pipe 发送, 管道由 `MSIService.exe` (Micro Star SCM) 创建
- 实测: 管道返回 `WBEM_E_NOT_SUPPORTED` 或管道不存在 (Win32Error=2), 但切换仍然成功
- 结论: 此步骤仅通知 MSI 服务更新 UI 状态, 不影响实际 MUX 切换

### 3. Feature Manager Service 是必要条件
- 测试矩阵:
  | MSIAPService | FM Service | FM UI | SCM Service | 切换结果 |
  |---|---|---|---|---|
  | ✅ | ✅ | ❌ | ❌ | ✅ 成功 |
  | ✅ | ❌ | ❌ | ❌ | ❌ 失败 |
  | ❌ | ✅ | ❌ | ❌ | ❌ 失败 (FM Service 立即退出) |
  | ❌ | ❌ | ✅ | ❌ | ✅ 成功 (FM UI 自动拉起两个服务) |
- `Feature Manager Service.exe` 是 `MSIAPService.exe` 的子进程
- 它在后台做了某些关键动作, 使得 EC 写入 + 注册表修改的组合在重启后生效
- 具体机制待 Procmon 分析

### 4. FW_CurrentNewGPU 的重要性
- 切换失败案例: `FW_GPU_CH=1` 且 `FW_CurrentNewGPU=1` (相同, 启动服务认为无需切换)
- 修复: 先写 `FW_CurrentNewGPU` 为当前实际模式 (目标的反值), 再写 `FW_GPU_CH` 为目标

## MSI GPUSwitch.exe 自动化

`MSI GPUSwitch.exe` 已实现:
- 自动检测并启动 `MSI Foundation Service`
- 自动启动 `Feature Manager Service.exe`
- 完整的 EC 写入流程 (0xD1 → 0xBE)
- 注册表状态同步 (FW_GPU_CH + FW_CurrentNewGPU)
- 三模式切换: `gpuD` (→Discrete), `gpuH` (→Hybrid), `gpuE` (→Eco/iGPU)
- 状态查询: `qs` (快速状态), `r` (完整读取)

## 核显模式 (Eco/iGPU) 破解 (2026-04-26)

通过反编译 `Feature_Manager.exe` 的 IL 代码, 确认了三模式的注册表映射:
- **ComboBox 顺序**: Index 0=Hybrid, 1=Discrete, 2=Eco/UMA
- **`FW_GPU_CH` = `ComboBox.SelectedIndex`**: 所以 `FW_GPU_CH=2` = 核显模式
- **`FW_SupportUMA=1`** 表示笔记本支持核显模式 (UMA = 统一内存架构)
- **EC 写入流程不变**: 0xD1 → 0xBE 的 WMI ACPI 序列对所有模式都一样
- **关键区别仅在 `FW_GPU_CH` 的值**: 0=Hybrid, 1=Discrete, 2=Eco

Feature Manager UI 中:
- `uxIntegratedMode` (ComboBoxItem) — 核显选项, 由 `isSupport_UMA_Switch` 控制可见性
- `uxDiscreteMode` (ComboBoxItem) — 独显选项, 由 `isSupport_Discrete_Switch` 控制可见性

## 待深入分析

- `Feature Manager Service.exe` 到底做了什么关键动作? (Procmon 抓包)
- 是否能绕过它, 直接完成其关键动作? (彻底摆脱所有 MSI 进程)
- `Micro Star SCM` 服务的作用? (已知非必要, 但可能有其他功能)
- 核显模式实际切换验证 (需用户测试 gpue 命令)

## 相关文件
- Feature Manager 安装目录: `C:\Program Files (x86)\Feature Manager\`
- MSIAPService.exe: `C:\Program Files (x86)\Feature Manager\MSIAPService.exe`
- Feature Manager Service.exe: `C:\Program Files (x86)\Feature Manager\Feature Manager Service.exe`
- 反编译 IL 输出: `gpu_il.txt`, `scm_il.txt`, `load_il.txt`, `npcl.txt`
