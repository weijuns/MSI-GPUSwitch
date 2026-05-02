using MsiGpuSwitch;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "MSI GPUSwitch - GPU Mode Switch Tool";

PrintBanner();

while (true)
{
    Console.WriteLine();
    Console.WriteLine("====================================================================");
    Console.WriteLine("  🎯  GPU 模式切换");
    Console.WriteLine("  [gpuD]  切换到 独显直连 (Discrete)");
    Console.WriteLine("  [gpuH]  切换到 混合模式 (Hybrid)");
    Console.WriteLine("  [gpuE]  切换到 核显模式 (Eco/iGPU)");
    Console.WriteLine();
    Console.WriteLine("  📊  状态查看");
    Console.WriteLine("  [qs]    快速查看多处 GPU 状态位");
    Console.WriteLine("  [uv]    读取 UEFI 变量 MsiDCVarData (当前持久化 GPU 模式)");
    Console.WriteLine();
    Console.WriteLine("  🔧  一次性配置 (首次使用需要)");
    Console.WriteLine("  [bs]    检查 WMI ACPI 引导状态");
    Console.WriteLine("  [boot]  引导: 复制 msiapcfg.dll + 设置 MofImagePath 注册表");
    Console.WriteLine();
    Console.WriteLine("  [dbg]   进入高级调试模式");
    Console.WriteLine("  [0]     退出");
    Console.WriteLine("====================================================================");
    Console.Write("输入命令: ");

    string? choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "gpud": case "GPUD": case "gpuD":
                AcpiProbe.ReplayMsiCenterSwitch(targetMode: 1);
                break;

            case "gpuh": case "GPUH": case "gpuH":
                AcpiProbe.ReplayMsiCenterSwitch(targetMode: 0);
                break;

            case "gpue": case "GPUE": case "gpuE":
                AcpiProbe.ReplayMsiCenterSwitch(targetMode: 2);
                break;

            case "qs": case "QS":
                AcpiProbe.PrintQuickStatus();
                break;

            case "uv": case "UV":
                UefiVariable.PrintCurrentMode();
                break;

            case "bs": case "BS":
                WmiAcpiBootstrap.PrintStatus();
                break;

            case "boot": case "BOOT":
                Console.WriteLine("⚠️ 此操作将:");
                Console.WriteLine("   1. 复制 msiapcfg.dll 到 C:\\Windows\\SysWOW64\\");
                Console.WriteLine("   2. 设置 HKLM\\SYSTEM\\CurrentControlSet\\Services\\WmiAcpi\\MofImagePath");
                Console.WriteLine("   重启后生效, 之后即使卸载 Feature Manager 也能 GPU 切换.");
                Console.Write("继续吗? 输入 YES 确认: ");
                if (Console.ReadLine()?.Trim() == "YES")
                    WmiAcpiBootstrap.Install();
                else
                    Console.WriteLine("已取消.");
                break;

            case "dbg": case "DBG":
                RunDebugMenu();
                break;

            case "0": case null: case "":
                return;
            default:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("未知命令. 输入 [dbg] 进入高级调试模式查看所有命令.");
                Console.ResetColor();
                break;
        }
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[权限不足] {ex.Message}");
        Console.WriteLine("请以管理员身份重新运行本程序.");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}");
        Console.ResetColor();
    }
}


static void RunDebugMenu()
{
    while (true)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("====================================================================");
        Console.WriteLine("  🔍  高级调试模式 (所有原始探测/调试命令均保留)");
        Console.WriteLine("====================================================================");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("  --- 系统信息 ---");
        Console.WriteLine("  [1]  显示系统基本信息 (机型/BIOS/显卡)");
        Console.WriteLine("  [2]  枚举 root\\wmi 命名空间下的所有 MSI_* 类");
        Console.WriteLine("  [3]  枚举 root\\cimv2 下与 GPU 相关的类");
        Console.WriteLine("  [4]  Dump 某个指定 WMI 类的全部实例数据");
        Console.WriteLine("  [5]  Dump 某个指定 WMI 类的所有方法签名");
        Console.WriteLine("  [6]  一键全量 dump (写到 dump.txt)");
        Console.WriteLine();
        Console.WriteLine("  --- ACPI 探测 (只读) ---");
        Console.WriteLine("  [7]  调用 MSI_ACPI.Get_WMI (列出支持的数据块)");
        Console.WriteLine("  [8]  批量探测 Get_WMI/Get_Device/Get_BIOS/Get_Power");
        Console.WriteLine("  [9]  手动调用任意 Get_* 方法 (自填命令码)");
        Console.WriteLine("  [p]  描述 Package_32 类定义");
        Console.WriteLine("  [q]  用 Package_32 扫描所有 Get_* × cmd 0x00~0x20");
        Console.WriteLine("  [d]  一键完整探测 + 写到 acpi_probe.txt");
        Console.WriteLine("  [r]  读取当前 GPU 模式 (只读)");
        Console.WriteLine("  [snap] 保存全状态快照到 snapshot.txt");
        Console.WriteLine();
        Console.WriteLine("  --- GPU 模式切换 (旧方法, 仅用于对比调试) ---");
        Console.WriteLine("  [s1]  Set_BIOS 切换到 独显直连 (已知: 非持久化)");
        Console.WriteLine("  [s0]  Set_BIOS 切换到 MSHybrid");
        Console.WriteLine("  [d1]  Set_Device 切换到 独显直连 (会话级, 不持久)");
        Console.WriteLine("  [d0]  Set_Device 切换到 MSHybrid");
        Console.WriteLine("  [cc]  Dump MSI_CentralControl 方法签名");
        Console.WriteLine("  [dt1] Set_Data cmd=0x04 切独显");
        Console.WriteLine("  [dt0] Set_Data cmd=0x04 切 Hybrid");
        Console.WriteLine("  [ap1] Set_AP cmd=0x00 切独显");
        Console.WriteLine("  [ap0] Set_AP cmd=0x00 切 Hybrid");
        Console.WriteLine("  [b41] Set_BIOS cmd=0x04 切独显");
        Console.WriteLine("  [b40] Set_BIOS cmd=0x04 切 Hybrid");
        Console.WriteLine();
        Console.WriteLine("  --- 引导 & 服务管理 ---");
        Console.WriteLine("  [hb]       写 OS 在线心跳 (EC 0xD9 bit0)");
        Console.WriteLine("  [uvw]      调试: 直接写 UEFI byte[5]");
        Console.WriteLine("  [unboot]   卸载引导 (删除 msiapcfg.dll + 清除注册表)");
        Console.WriteLine("  [srv]      查看 MSI Foundation Service 状态");
        Console.WriteLine("  [srv-install] 安装 MSIAPService 为 Windows 服务");
        Console.WriteLine("  [srv-start]   启动服务");
        Console.WriteLine("  [srv-stop]    停止服务");
        Console.WriteLine("  [srv-remove]  卸载服务");
        Console.WriteLine("  [auto]     自动准备 MSI 辅助组件并切换 GPU");
        Console.WriteLine();
        Console.WriteLine("  [0]  返回主菜单");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("====================================================================");
        Console.ResetColor();
        Console.Write("输入命令: ");

        string? choice = Console.ReadLine()?.Trim();
        Console.WriteLine();

        if (choice is "0" or null or "")
            return;

        try
        {
            switch (choice)
            {
                case "1": SystemInfo.PrintSystemInfo(); break;
                case "2": WmiProbe.EnumerateMsiClasses(); break;
                case "3": WmiProbe.EnumerateGpuRelatedClasses(); break;
                case "4":
                    Console.Write("WMI 类名 (root\\wmi 或 root\\cimv2:ClassName): ");
                    var cls4 = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(cls4)) WmiProbe.DumpClassInstances(cls4);
                    break;
                case "5":
                    Console.Write("WMI 类名: ");
                    var cls5 = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrWhiteSpace(cls5)) WmiProbe.DumpClassMethods(cls5);
                    break;
                case "6":
                    string dumpPath = Path.Combine(AppContext.BaseDirectory, "dump.txt");
                    WmiProbe.DumpAllToFile(dumpPath);
                    Console.WriteLine($"已全量 dump 到: {dumpPath}");
                    break;

                case "7": AcpiProbe.InvokeGetWmi(); break;
                case "8": AcpiProbe.BulkProbe(); break;
                case "9":
                    Console.Write("方法名 (必须以 Get_ 开头, 例如 Get_Device): ");
                    string? mname = Console.ReadLine()?.Trim();
                    Console.Write("命令码 (十六进制, 例如 01 或 0x10, 留空=无参): ");
                    string? cmdStr = Console.ReadLine()?.Trim();
                    byte? cmd = null;
                    if (!string.IsNullOrEmpty(cmdStr))
                    {
                        cmdStr = cmdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? cmdStr[2..] : cmdStr;
                        cmd = Convert.ToByte(cmdStr, 16);
                    }
                    if (!string.IsNullOrWhiteSpace(mname)) AcpiProbe.ProbeGetMethod(mname, cmd);
                    break;

                case "p": case "P": AcpiProbe.DescribePackage32(); break;
                case "q": case "Q": AcpiProbe.BulkProbeWithPackage32(); break;
                case "r": case "R": AcpiProbe.ReadGpuMode(); break;

                case "s1": case "S1": AcpiProbe.SwitchGpuMode(targetDGpu: true); break;
                case "s0": case "S0": AcpiProbe.SwitchGpuMode(targetDGpu: false); break;
                case "d1": case "D1": AcpiProbe.SwitchGpuModeViaDevice(targetDGpu: true); break;
                case "d0": case "D0": AcpiProbe.SwitchGpuModeViaDevice(targetDGpu: false); break;
                case "cc": case "CC": AcpiProbe.DumpClassMethodsForCentralControl(); break;

                case "dt1": case "DT1":
                    AcpiProbe.SwitchGeneric("Get_Data", "Set_Data", 0x04, 1,
                        dgpuValue: 0x83, hybridValue: 0x00,
                        AcpiProbe.SwitchMode.ByteReplace, targetDGpu: true); break;
                case "dt0": case "DT0":
                    AcpiProbe.SwitchGeneric("Get_Data", "Set_Data", 0x04, 1,
                        dgpuValue: 0x83, hybridValue: 0x00,
                        AcpiProbe.SwitchMode.ByteReplace, targetDGpu: false); break;
                case "ap1": case "AP1":
                    AcpiProbe.SwitchGeneric("Get_AP", "Set_AP", 0x00, 3,
                        dgpuValue: 0xC2, hybridValue: 0xC0,
                        AcpiProbe.SwitchMode.ByteReplace, targetDGpu: true); break;
                case "ap0": case "AP0":
                    AcpiProbe.SwitchGeneric("Get_AP", "Set_AP", 0x00, 3,
                        dgpuValue: 0xC2, hybridValue: 0xC0,
                        AcpiProbe.SwitchMode.ByteReplace, targetDGpu: false); break;
                case "b41": case "B41":
                    AcpiProbe.SwitchGeneric("Get_BIOS", "Set_BIOS", 0x04, 1,
                        dgpuValue: 0x07, hybridValue: 0x03,
                        AcpiProbe.SwitchMode.ByteReplace, targetDGpu: true); break;
                case "b40": case "B40":
                    AcpiProbe.SwitchGeneric("Get_BIOS", "Set_BIOS", 0x04, 1,
                        dgpuValue: 0x07, hybridValue: 0x03,
                        AcpiProbe.SwitchMode.ByteReplace, targetDGpu: false); break;

                case "qs": case "QS": AcpiProbe.PrintQuickStatus(); break;
                case "snap": case "SNAP":
                    string snapPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                    AcpiProbe.SnapshotAllToFile(snapPath);
                    break;

                case "d": case "D":
                    string probePath = Path.Combine(AppContext.BaseDirectory, "acpi_probe.txt");
                    var origOut = Console.Out;
                    using (var sw = new StringWriter())
                    {
                        Console.SetOut(sw);
                        try { AcpiProbe.DescribePackage32(); Console.WriteLine(); AcpiProbe.BulkProbeWithPackage32(); }
                        finally { Console.SetOut(origOut); }
                        File.WriteAllText(probePath, sw.ToString(), System.Text.Encoding.UTF8);
                    }
                    Console.WriteLine($"已写入: {probePath}");
                    break;

                case "hb": case "HB": AcpiProbe.WriteOsHeartbeat(); break;

                case "uvw": case "UVW":
                    Console.Write("调试: 直接写入 byte[5] (输入 hex 值, 例如 30 表示 0x30): ");
                    string? hex = Console.ReadLine()?.Trim();
                    if (!string.IsNullOrEmpty(hex))
                    {
                        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                            UefiVariable.WriteByte5Raw(b);
                        else
                            Console.WriteLine("  ❌ 无效的十六进制值");
                    }
                    break;

                case "unboot": case "UNBOOT":
                    Console.Write("⚠️ 卸载后 WMI ACPI 将不再可用 (除非重装 FM 或重新 boot). 输入 YES 确认: ");
                    if (Console.ReadLine()?.Trim() == "YES")
                        WmiAcpiBootstrap.Uninstall();
                    else
                        Console.WriteLine("已取消.");
                    break;

                case "srv": MsiApService.PrintStatus(); break;
                case "srv-install": MsiApService.Install(); break;
                case "srv-start": MsiApService.Start(); break;
                case "srv-stop": MsiApService.Stop(); break;
                case "srv-remove": MsiApService.Uninstall(); break;

                case "auto": case "AUTO":
                    Console.WriteLine("自动模式：将按当前配置尽量自动准备并执行切换。\n");
                    AcpiProbe.ReplayMsiCenterSwitch(targetMode: 1);
                    break;

                default:
                    Console.WriteLine("未知调试命令.");
                    break;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[权限不足] {ex.Message}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[错误] {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
        }
    }
}


static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  MSI GPUSwitch - GPU Mode Switch Tool                             ║");
    Console.WriteLine("║  支持机型: MSI Stealth 14 及其他 MSI 笔记本                       ║");
    Console.WriteLine("║  三模式切换: Hybrid / Discrete / Eco(iGPU)                       ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
    Console.ResetColor();
}
