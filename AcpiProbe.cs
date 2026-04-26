using System.Linq;
using System.Management;
using System.Text;

namespace MsiGpuSwitch;

/// <summary>
/// MSI_ACPI (root\wmi, 实例 ACPI\PNP0C14\0_0) 的方法调用器.
/// v1 阶段: 只调用 Get_* 方法, 绝不调 Set_*.
///
/// MSI 的 ACPI-over-WMI 管道:
///   - 每个 Get_*/Set_* 方法接受一个 Data 参数 (byte[])
///   - Data 的第一个 (或前两个) 字节通常是命令码 / 数据块 ID
///   - 返回的 Data 是 EC/BIOS 返回的裸字节包
/// </summary>
internal static class AcpiProbe
{
    private const string WmiScope = "root\\wmi";
    private const string AcpiClass = "MSI_ACPI";

    /// <summary>调用 Get_WMI: 无参方法, 返回支持的 WMI 数据块 ID 列表.</summary>
    public static void InvokeGetWmi()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 调用 MSI_ACPI.Get_WMI (列出支持的数据块) ─────────");
        Console.ResetColor();

        byte[]? result = InvokeGetMethod("Get_WMI", null);
        if (result is null)
        {
            Console.WriteLine("  (调用返回空)");
            return;
        }

        DumpBytes("Get_WMI 返回", result);
        Console.WriteLine();
        Console.WriteLine("  提示: 这串字节通常每个 bit/byte 表示一种能力是否支持.");
        Console.WriteLine("  MSI 机型普遍约定:");
        Console.WriteLine("    bit/byte 位置 -> 对应 Get_XXX 方法的启用状态");
    }

    /// <summary>对指定 Get_* 方法用给定的单字节命令码探测输出.</summary>
    public static void ProbeGetMethod(string methodName, byte? cmd)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 调用 MSI_ACPI.{methodName}  cmd=0x{cmd:X2} ──");
        Console.ResetColor();

        if (!methodName.StartsWith("Get_", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ❌ 拒绝: v1 阶段只允许调用 Get_* 方法, 不允许 Set_*.");
            Console.ResetColor();
            return;
        }

        byte[]? inputBuf = cmd is null ? null : BuildInputBuffer(cmd.Value, 32);
        byte[]? result = InvokeGetMethod(methodName, inputBuf);
        if (result is null)
        {
            Console.WriteLine("  (调用返回空或失败)");
            return;
        }

        DumpBytes($"{methodName} 返回 (cmd=0x{cmd:X2})", result);
    }

    /// <summary>
    /// 打印 Package_32 类本身的定义: 属性列表/类型/qualifier.
    /// 这是 MSI_ACPI 所有带参 Get_*/Set_* 方法的 Data 参数类型,
    /// 搞清楚它的字段布局是正确调用的前提.
    /// </summary>
    public static void DescribePackage32()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── root\\wmi: Package_32 类定义 ──────────────────────");
        Console.ResetColor();

        try
        {
            using var mc = new ManagementClass(
                new ManagementScope(WmiScope),
                new ManagementPath("Package_32"),
                null);
            mc.Options.UseAmendedQualifiers = true;

            Console.WriteLine("  类级限定符:");
            foreach (QualifierData qd in mc.Qualifiers)
            {
                Console.WriteLine($"    {qd.Name,-20} = {SystemInfo.FormatValue(qd.Value)}");
            }

            Console.WriteLine();
            Console.WriteLine("  属性列表:");
            foreach (PropertyData pd in mc.Properties)
            {
                Console.WriteLine($"    {pd.Name,-20}  type={pd.Type,-10} IsArray={pd.IsArray}");
                foreach (QualifierData qd in pd.Qualifiers)
                {
                    Console.WriteLine($"      [{qd.Name}] = {SystemInfo.FormatValue(qd.Value)}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  探测 Package_32 失败: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// v1.2 正确调用: 用 Package_32 实例作为 Data 参数调用 Get_* 方法.
    /// 支持对 Package_32 的不同填充方式做矩阵扫描.
    /// </summary>
    public static void InvokeWithPackage32(string methodName, byte cmd, bool silent = false)
    {
        if (!methodName.StartsWith("Get_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("v1 阶段仅允许 Get_*.");

        try
        {
            // 1. 构造 Package_32 实例
            using var pkgClass = new ManagementClass(
                new ManagementScope(WmiScope),
                new ManagementPath("Package_32"),
                null);
            ManagementObject pkg = pkgClass.CreateInstance();

            // 2. 填充属性. Package_32 的字段很可能是:
            //    - 一个名为 "Bytes" (或类似) 的 uint8 array[32], 或
            //    - 32 个独立字段 bData0 ~ bData31
            //    先用内省的方式: 对第一个 array 属性填充 byte[32], cmd 放 [0]
            var fillBytes = new byte[32];
            fillBytes[0] = cmd;

            bool filled = false;
            foreach (PropertyData pd in pkg.Properties)
            {
                if (pd.IsArray && pd.Type == CimType.UInt8)
                {
                    try
                    {
                        pkg[pd.Name] = fillBytes;
                        filled = true;
                        break;
                    }
                    catch { }
                }
            }
            // 若没有 array 属性, 尝试把命令码写到第一个 UInt8 属性
            if (!filled)
            {
                foreach (PropertyData pd in pkg.Properties)
                {
                    if (pd.Type == CimType.UInt8 && !pd.IsArray)
                    {
                        try { pkg[pd.Name] = cmd; filled = true; break; } catch { }
                    }
                }
            }

            // 3. 在 MSI_ACPI 实例上调用方法
            using var searcher = new ManagementObjectSearcher(WmiScope,
                $"SELECT * FROM {AcpiClass}");
            foreach (ManagementObject mo in searcher.Get())
            {
                ManagementBaseObject inParams = mo.GetMethodParameters(methodName);
                inParams["Data"] = pkg;

                ManagementBaseObject outParams;
                try
                {
                    outParams = mo.InvokeMethod(methodName, inParams, null);
                }
                catch (ManagementException mex)
                {
                    if (!silent)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"    调用失败 [{methodName} cmd=0x{cmd:X2}]: {mex.ErrorCode}");
                        Console.ResetColor();
                    }
                    return;
                }

                // 4. 解析 Data 出参 (也是 Package_32)
                var dataOut = outParams?["Data"] as ManagementBaseObject;
                if (dataOut is null)
                {
                    if (!silent) Console.WriteLine("    (返回 Data 为空)");
                    return;
                }

                byte[]? outBytes = ExtractBytesFromPackage(dataOut);
                if (outBytes is null)
                {
                    if (!silent)
                    {
                        Console.WriteLine("    返回 Package_32 各字段:");
                        foreach (PropertyData pd in dataOut.Properties)
                            Console.WriteLine($"      {pd.Name,-20} = {SystemInfo.FormatValue(pd.Value)}");
                    }
                    return;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ {methodName,-18} cmd=0x{cmd:X2} => {outBytes.Length} bytes");
                Console.ResetColor();
                Console.WriteLine($"      hex   : {BitConverter.ToString(outBytes).Replace("-", " ")}");
                return;
            }
        }
        catch (Exception ex) when (silent)
        {
            _ = ex;
        }
    }

    // --------------------------------------------------------------------
    // 阶段 B: 写入能力 (仅在用户显式 YES 确认后调用)
    // --------------------------------------------------------------------

    /// <summary>
    /// 调用任意 Set_* 方法. 入参 32 字节, 出参 32 字节.
    /// ⚠️ 此方法会真正修改 BIOS/EC 状态, 调用方必须保证用户已 YES 确认.
    /// </summary>
    private static byte[]? CallSet(string methodName, byte[] inputBytes)
    {
        if (!methodName.StartsWith("Set_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CallSet 仅用于 Set_* 方法.");
        if (inputBytes is null || inputBytes.Length != 32)
            throw new ArgumentException("inputBytes 必须 32 字节.", nameof(inputBytes));

        using var pkgClass = new ManagementClass(
            new ManagementScope(WmiScope), new ManagementPath("Package_32"), null);
        ManagementObject pkg = pkgClass.CreateInstance();
        pkg["Bytes"] = inputBytes;

        using var searcher = new ManagementObjectSearcher(WmiScope,
            $"SELECT * FROM {AcpiClass}");
        foreach (ManagementObject mo in searcher.Get())
        {
            ManagementBaseObject inParams = mo.GetMethodParameters(methodName);
            inParams["Data"] = pkg;
            ManagementBaseObject outParams = mo.InvokeMethod(methodName, inParams, null);
            var dataOut = outParams?["Data"] as ManagementBaseObject;
            if (dataOut is null) return null;
            return ExtractBytesFromPackage(dataOut);
        }
        return null;
    }

    /// <summary>
    /// 路径 2: 用 Set_Device cmd=0x01 切换 (写 bit6 of byte[1]).
    /// Get_Device 反映的是真正的设备状态, 可能是持久化的正确入口.
    /// </summary>
    public static void SwitchGpuModeViaDevice(bool targetDGpu)
    {
        string targetName = targetDGpu ? "独显直连" : "MSHybrid";

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 路径 2: Set_Device cmd=0x01 切换 → {targetName} ──");
        Console.ResetColor();

        byte[]? current;
        try
        {
            var getInput = new byte[32]; getInput[0] = 0x01;
            current = CallGet("Get_Device", getInput);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  读取失败: {ex.Message}");
            return;
        }

        if (current is null || current.Length < 2 || current[0] != 0x01)
        {
            Console.WriteLine("  数据异常, 中止.");
            return;
        }

        byte currentByte = current[1];
        byte targetByte = targetDGpu ? (byte)(currentByte | 0x40) : (byte)(currentByte & ~0x40);

        Console.WriteLine($"  当前 byte[1] = 0x{currentByte:X2} (bit6={(currentByte >> 6) & 1})");
        Console.WriteLine($"  目标 byte[1] = 0x{targetByte:X2} (bit6={(targetByte >> 6) & 1})");

        if (currentByte == targetByte)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  已是目标模式, 无需切换.");
            Console.ResetColor();
            return;
        }

        var toWrite = new byte[32];
        Array.Copy(current, toWrite, 32);
        toWrite[0] = 0x01;          // cmd
        toWrite[1] = targetByte;    // bit6 flipped

        Console.WriteLine();
        Console.WriteLine($"    读回: {BitConverter.ToString(current).Replace("-", " ")}");
        Console.WriteLine($"    写入: {BitConverter.ToString(toWrite).Replace("-", " ")}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ⚠️ Set_Device 可能直接触发 MUX 切换, 屏幕会闪一下黑.");
        Console.WriteLine("     输入 YES 继续:");
        Console.ResetColor();
        Console.Write("  > ");
        if (Console.ReadLine() != "YES") { Console.WriteLine("  已取消."); return; }

        byte[]? setResult;
        try
        {
            setResult = CallSet("Set_Device", toWrite);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ Set_Device 异常: {ex.Message}");
            Console.ResetColor();
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Set_Device 返回: {(setResult is null ? "<null>" : BitConverter.ToString(setResult).Replace("-", " "))}");
        byte ack = setResult is { Length: > 0 } ? setResult[0] : (byte)0;
        Console.WriteLine($"  写入 ACK: 0x{ack:X2}  ({(ack == 0x01 ? "成功" : "⚠️ 失败")})");

        // 立即回读
        Console.WriteLine();
        Console.WriteLine("  回读验证...");
        try
        {
            var vIn = new byte[32]; vIn[0] = 0x01;
            byte[]? v = CallGet("Get_Device", vIn);
            if (v is { Length: > 1 })
            {
                Console.WriteLine($"    Get_Device: 0x{v[1]:X2} (bit6={(v[1] >> 6) & 1})");
            }

            // 同时查 Get_BIOS[0x02] 看它有没有同步更新
            var biosIn = new byte[32]; biosIn[0] = 0x02;
            byte[]? bios = CallGet("Get_BIOS", biosIn);
            if (bios is { Length: > 1 })
            {
                Console.WriteLine($"    Get_BIOS[0x02].byte[1]: 0x{bios[1]:X2}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  回读异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 通用: 对任意 (Get/Set, method, cmd, byteIndex, valueOrMask, mode) 的 GPU 模式切换.
    /// 读 Get_* 当前 32 字节 -> 原样复制 -> 改 byte[0]=cmd, byte[byteIndex] 按模式变 -> Set_* 写.
    /// mode=BitSet: 设置指定 bit 到目标值 (bit=1 表示 dGPU, bit=0 表示 Hybrid)
    /// mode=ByteReplace: 直接用 dgpuValue/hybridValue 替换整个字节
    /// </summary>
    public enum SwitchMode { BitMask, ByteReplace }
    public static void SwitchGeneric(
        string getMethod, string setMethod, byte cmd, int byteIndex,
        byte dgpuValue, byte hybridValue, SwitchMode mode, bool targetDGpu)
    {
        string targetName = targetDGpu ? "独显直连" : "MSHybrid";

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 通用切换: {setMethod} cmd=0x{cmd:X2} byte[{byteIndex}] → {targetName} ──");
        Console.ResetColor();

        byte[]? current;
        try
        {
            var getInput = new byte[32]; getInput[0] = cmd;
            current = CallGet(getMethod, getInput);
        }
        catch (Exception ex) { Console.WriteLine($"  Get 失败: {ex.Message}"); return; }

        if (current is null || current[0] != 0x01)
        {
            Console.WriteLine("  Get 返回异常, 中止.");
            return;
        }

        byte oldByte = current[byteIndex];
        byte newByte;
        if (mode == SwitchMode.BitMask)
        {
            byte mask = (byte)(dgpuValue ^ hybridValue);    // 差异 bit
            byte targetBits = targetDGpu ? (byte)(dgpuValue & mask) : (byte)(hybridValue & mask);
            newByte = (byte)((oldByte & ~mask) | targetBits);
        }
        else
        {
            newByte = targetDGpu ? dgpuValue : hybridValue;
        }

        Console.WriteLine($"  当前 byte[{byteIndex}] = 0x{oldByte:X2}");
        Console.WriteLine($"  目标 byte[{byteIndex}] = 0x{newByte:X2}  (mode={mode})");

        if (oldByte == newByte)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  已是目标值, 无需写入.");
            Console.ResetColor();
            return;
        }

        var toWrite = new byte[32];
        Array.Copy(current, toWrite, 32);
        toWrite[0] = cmd;
        toWrite[byteIndex] = newByte;

        Console.WriteLine($"    读回: {BitConverter.ToString(current).Replace("-", " ")}");
        Console.WriteLine($"    写入: {BitConverter.ToString(toWrite).Replace("-", " ")}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ⚠️ 输入 YES 继续:");
        Console.ResetColor();
        Console.Write("  > ");
        if (Console.ReadLine() != "YES") { Console.WriteLine("  已取消."); return; }

        try
        {
            byte[]? r = CallSet(setMethod, toWrite);
            byte ack = r is { Length: > 0 } ? r[0] : (byte)0;
            Console.WriteLine($"  {setMethod} 返回: {(r is null ? "<null>" : BitConverter.ToString(r).Replace("-", " "))}");
            Console.WriteLine($"  ACK: 0x{ack:X2}  ({(ack == 0x01 ? "成功" : "⚠️ 失败")})");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ {setMethod} 异常: {ex.Message}");
            Console.ResetColor();
            return;
        }

        // 立即回读 (多处)
        Console.WriteLine();
        Console.WriteLine("  立即回读 (多处 GPU 状态位):");
        PrintQuickStatus();
    }

    /// <summary>打印几个关键位置的 GPU 模式状态位, 用于快速检视当前系统.</summary>
    public static void PrintQuickStatus()
    {
        (string method, byte cmd, int idx, string desc)[] checks = new (string, byte, int, string)[]
        {
            ("Get_BIOS",   0x02, 1, "BIOS[0x02].byte[1]  (dGPU=0x40 Hyb=0x00)"),
            ("Get_Device", 0x01, 1, "Device[0x01].byte[1] (bit6: dGPU=1 Hyb=0)"),
            ("Get_Data",   0x04, 1, "Data[0x04].byte[1]  (dGPU=0x83 Hyb=0x00)"),
            ("Get_AP",     0x00, 3, "AP[0x00].byte[3]    (dGPU=0xC2 Hyb=0xC0)"),
            ("Get_AP",     0x01, 3, "AP[0x01].byte[3]    (dGPU=0x05 Hyb=0x03)"),
            ("Get_BIOS",   0x00, 2, "BIOS[0x00].byte[2]  (dGPU=0x50 Hyb=0x51)"),
            ("Get_BIOS",   0x04, 1, "BIOS[0x04].byte[1]  (dGPU=0x07 Hyb=0x03)"),
        };

        foreach (var c in checks)
        {
            try
            {
                var input = new byte[32]; input[0] = c.cmd;
                byte[]? r = CallGet(c.method, input);
                if (r is { Length: > 2 })
                {
                    Console.WriteLine($"    {c.desc,-48} = 0x{r[c.idx]:X2}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"    {c.desc,-48} <err: {ex.Message}>"); }
        }
    }

    const string MsiRegPath = @"SOFTWARE\WOW6432Node\MSI\Feature Manager\Component\Base Module\User Scenario";

    private static void DumpGpuRegistryState(string title)
    {
        Console.WriteLine($"  {title}");
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MsiRegPath, writable: false);
            if (k is null)
            {
                Console.WriteLine("    HKLM 键不存在或无法读取");
                return;
            }

            object? fwGpuCh = k.GetValue("FW_GPU_CH");
            object? fwCurrent = k.GetValue("FW_CurrentNewGPU");
            object? fwSupportNewGpu = k.GetValue("FW_SupportNewGPU");
            Console.WriteLine($"    FW_GPU_CH        = {fwGpuCh ?? "<null>"}");
            Console.WriteLine($"    FW_CurrentNewGPU = {fwCurrent ?? "<null>"}");
            Console.WriteLine($"    FW_SupportNewGPU = {fwSupportNewGpu ?? "<null>"}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    读取注册表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 🎯 完整复刻 MSI Center 的 GPU 模式切换 (已验证可用!).
    /// 
    /// 切换流程:
    ///   1. 写注册表 HKLM\...\FW_GPU_CH = (0=Hybrid, 1=dGPU, 2=Eco/iGPU)
    ///   2. Set_Data(cmd=0xD1, byte[1]=<Get_AP(0)[1] with bit0=1 bit1=0>)
    ///   3. Sleep 2s, 重读 Get_AP(0), 校验 BIOS 已响应 (byte[2] bit1 置位)
    ///   4. Set_Data(cmd=0xBE, byte[1]=0x02)
    ///   5. 提示用户重启
    /// </summary>
    /// <param name="targetMode">0=Hybrid, 1=Discrete, 2=Eco/iGPU</param>
    public static void ReplayMsiCenterSwitch(int targetMode = 1)
    {
        string target = targetMode switch
        {
            2 => "核显模式 (Eco/iGPU)",
            1 => "独显直连 (Discrete)",
            _ => "混合模式 (Hybrid)"
        };
        int targetChVal = targetMode;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 🎯 GPU 切换到 {target} ──");
        Console.ResetColor();

        // === 前置: 启动 MSI 后台服务 ===
        // 经测试验证: GPU 切换需要两个进程配合:
        //   1. MSIAPService.exe (MSI Foundation Service) - 必须先启动
        //   2. Feature Manager Service.exe - 依赖 MSIAPService, 单独运行会立即退出
        // 不需要 Feature Manager UI, 不需要 Micro Star SCM.

        // 查找 FeatureManager 目录: 优先项目自带, 其次系统安装
        string exeDir = AppContext.BaseDirectory;
        string[] fmDirCandidates =
        {
            System.IO.Path.GetFullPath(System.IO.Path.Combine(exeDir, "FeatureManager")),       // Bundled with MSI GPUSwitch
            @"C:\Program Files (x86)\Feature Manager",                                           // System install
        };
        string fmDir = fmDirCandidates.FirstOrDefault(d => System.IO.File.Exists(System.IO.Path.Combine(d, "MSIAPService.exe")))
            ?? fmDirCandidates[0];
        string msiApSvcPath = System.IO.Path.Combine(fmDir, "MSIAPService.exe");
        string fmSvcPath = System.IO.Path.Combine(fmDir, "Feature Manager Service.exe");
        Console.WriteLine($"  FeatureManager 目录: {fmDir}");

        // 步骤 1: 启动 MSI Foundation Service (MSIAPService.exe)
        const string svcName = "MSI Foundation Service";
        bool msiFoundationReady = false;
        try
        {
            using var svc = new System.ServiceProcess.ServiceController(svcName);
            if (svc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
            {
                Console.WriteLine($"  启动 {svcName}...");
                try
                {
                    svc.Start();
                    svc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                    msiFoundationReady = true;
                    Console.WriteLine($"  ✓ {svcName} 已启动");
                }
                catch
                {
                    // Windows 服务未注册或启动失败; 尝试直接运行 MSIAPService.exe
                    if (System.IO.File.Exists(msiApSvcPath))
                    {
                        Console.WriteLine($"  从 {msiApSvcPath} 启动...");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(msiApSvcPath)
                        {
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                        System.Threading.Thread.Sleep(3000);
                        msiFoundationReady = true;
                        Console.WriteLine($"  ✓ MSIAPService.exe 已启动");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  ❌ 找不到 {msiApSvcPath}, 也无法启动 Windows 服务");
                        Console.WriteLine("     GPU 切换不会生效, 中止.");
                        Console.ResetColor();
                        return;
                    }
                }
            }
            else
            {
                msiFoundationReady = true;
                Console.WriteLine($"  ✓ {svcName} 已在运行");
            }
        }
        catch (Exception ex)
        {
            // ServiceController 本身失败 (服务未注册); 尝试直接运行
            if (System.IO.File.Exists(msiApSvcPath))
            {
                Console.WriteLine($"  Windows 服务未注册, 从 {msiApSvcPath} 启动...");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(msiApSvcPath)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                System.Threading.Thread.Sleep(3000);
                msiFoundationReady = System.Diagnostics.Process.GetProcessesByName("MSIAPService").Length > 0;
                if (msiFoundationReady)
                    Console.WriteLine($"  ✓ MSIAPService.exe 已启动");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ❌ 无法启动 MSI Foundation Service: {ex.Message}");
                Console.WriteLine("     GPU 切换不会生效, 中止.");
                Console.ResetColor();
                return;
            }
        }

        if (!msiFoundationReady)
        {
            msiFoundationReady = System.Diagnostics.Process.GetProcessesByName("MSIAPService").Length > 0;
        }

        if (!msiFoundationReady)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ❌ MSI Foundation Service 未能启动, GPU 切换不会生效, 中止.");
            Console.ResetColor();
            return;
        }

        // 步骤 2: 启动 Feature Manager Service.exe
        bool fmSvcRunning = System.Diagnostics.Process.GetProcessesByName("Feature Manager Service").Length > 0;
        if (!fmSvcRunning)
        {
            if (!System.IO.File.Exists(fmSvcPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ❌ 找不到 {fmSvcPath}");
                Console.WriteLine("     GPU 切换不会生效, 中止.");
                Console.ResetColor();
                return;
            }
            Console.WriteLine($"  启动 Feature Manager Service.exe...");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(fmSvcPath)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
                System.Threading.Thread.Sleep(2000); // 等待它初始化
                fmSvcRunning = System.Diagnostics.Process.GetProcessesByName("Feature Manager Service").Length > 0;
                if (fmSvcRunning)
                    Console.WriteLine($"  ✓ Feature Manager Service.exe 已启动");
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ⚠️ Feature Manager Service.exe 启动后立即退出 (MSIAPService 未就绪?)");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ⚠️ 启动 Feature Manager Service.exe 失败: {ex.Message}");
                Console.ResetColor();
            }
        }
        else
        {
            Console.WriteLine($"  ✓ Feature Manager Service.exe 已在运行");
        }

        DumpGpuRegistryState("切换前注册表状态:");

        // === 步骤 0: 写注册表 ===
        // 关键: FW_CurrentNewGPU 必须与 FW_GPU_CH 不同, 否则启动服务认为无需切换.
        // FW_CurrentNewGPU = 当前实际模式 (与目标不同即可), FW_GPU_CH = 目标模式.
        // 读取当前 FW_GPU_CH 作为 FW_CurrentNewGPU (确保与目标不同)
        int currentGpuVal;
        try
        {
            using var rk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MsiRegPath, writable: false);
            object? existingCh = rk?.GetValue("FW_GPU_CH");
            currentGpuVal = existingCh is int v ? v : 0;
        }
        catch { currentGpuVal = 0; }
        // 如果当前值恰好等于目标, 人为设为其他值
        if (currentGpuVal == targetChVal)
            currentGpuVal = targetChVal == 0 ? 1 : 0;
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MsiRegPath, writable: true);
            if (k is null)
            {
                Console.WriteLine("  ❌ 无法打开 HKLM 注册表键 (是否以管理员运行?). 中止.");
                return;
            }
            // 先写 FW_CurrentNewGPU = 当前实际模式 (确保与目标不同)
            object? oldCurrent = k.GetValue("FW_CurrentNewGPU");
            Console.WriteLine($"  步骤 0a: 注册表 FW_CurrentNewGPU: {oldCurrent} -> {currentGpuVal} (当前实际模式)");
            k.SetValue("FW_CurrentNewGPU", currentGpuVal, Microsoft.Win32.RegistryValueKind.DWord);

            // 再写 FW_GPU_CH = 目标模式
            object? oldVal = k.GetValue("FW_GPU_CH");
            Console.WriteLine($"  步骤 0b: 注册表 FW_GPU_CH: {oldVal} -> {targetChVal} (目标模式)");
            k.SetValue("FW_GPU_CH", targetChVal, Microsoft.Win32.RegistryValueKind.DWord);
            object? newVal = k.GetValue("FW_GPU_CH");
            Console.WriteLine($"          写入后 FW_GPU_CH = {newVal}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 写注册表异常: {ex.Message}. 中止.");
            return;
        }
        DumpGpuRegistryState("步骤 0 后注册表状态:");

        // 1. 读 Get_AP(0)
        byte[]? ap0;
        try
        {
            var getInput = new byte[32]; getInput[0] = 0x00;
            ap0 = CallGet("Get_AP", getInput);
        }
        catch (Exception ex) { Console.WriteLine($"  Get_AP 失败: {ex.Message}"); return; }

        if (ap0 is null || ap0[0] != 0x01)
        {
            Console.WriteLine("  Get_AP 返回异常, 中止."); return;
        }
        Console.WriteLine($"  步骤 1: Get_AP(0) = {BitConverter.ToString(ap0, 0, 8).Replace("-", " ")}");

        // 2. 取 byte[1], 改 bit0=1, bit1=0
        byte orig = ap0[1];
        byte mod = orig;
        mod = (byte)(mod & ~0x01);      // clear bit0
        mod = (byte)(mod & ~0x02);      // clear bit1
        mod = (byte)(mod | 0x01);       // set bit0
        Console.WriteLine($"  步骤 2: 改 byte[1]: 0x{orig:X2} -> 0x{mod:X2} (bit0=1, bit1=0)");

        // 3. 准备 Set_Data(0xD1, Package_32 with byte[1]=mod)
        var pkg1 = new byte[32];
        pkg1[0] = 0xD1;         // cmd = EC 地址 0xD1
        pkg1[1] = mod;          // 要写的字节
        Console.WriteLine($"  步骤 3: 将调用 Set_Data cmd=0xD1, byte[1]=0x{mod:X2}");
        Console.WriteLine($"          Package_32: {BitConverter.ToString(pkg1, 0, 4).Replace("-", " ")} ...");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ⚠️  此操作会真正写入 EC 寄存器 0xD1 (GPU 模式持久位)");
        Console.WriteLine("      写入后需要重启生效. 输入 YES 继续:");
        Console.ResetColor();
        Console.Write("  > ");
        if (Console.ReadLine() != "YES") { Console.WriteLine("  已取消."); return; }

        // 执行 Set_Data(0xD1)
        byte[]? r1;
        try { r1 = CallSet("Set_Data", pkg1); }
        catch (Exception ex) { Console.WriteLine($"  ❌ Set_Data(0xD1) 异常: {ex.Message}"); return; }
        byte ack1 = r1 is { Length: > 0 } ? r1[0] : (byte)0;
        Console.WriteLine($"  步骤 4: Set_Data(0xD1) ACK=0x{ack1:X2}  ({(ack1 == 0x01 ? "成功" : "⚠️ 失败")})");
        if (r1 is not null)
            Console.WriteLine($"          返回: {BitConverter.ToString(r1, 0, 8).Replace("-", " ")} ...");

        // 5. Sleep 2s, 重读 Get_AP(0) 检查 bit1 有没有被 BIOS 置位
        Console.WriteLine("  步骤 5: 等待 2 秒, 让 BIOS 处理...");
        System.Threading.Thread.Sleep(2000);

        byte[]? ap0_after;
        try
        {
            var getInput = new byte[32]; getInput[0] = 0x00;
            ap0_after = CallGet("Get_AP", getInput);
        }
        catch (Exception ex) { Console.WriteLine($"  重读失败: {ex.Message}"); return; }
        if (ap0_after is null) { Console.WriteLine("  重读异常."); return; }
        Console.WriteLine($"          重读 Get_AP(0) = {BitConverter.ToString(ap0_after, 0, 8).Replace("-", " ")}");
        // MSI Center 的 Get_AP 返回时已剥掉 ACK 字节, 所以它的 byte[1] 对应我们的 byte[2]
        byte checkByte = ap0_after[2];
        Console.WriteLine($"          byte[2] (=MSI 侧 byte[1]) = 0x{checkByte:X2} (bit1 = {(checkByte >> 1) & 1})");

        // 6. 若 bit1 置位, 写 Set_Data(0xBE, 0x02)
        if (((checkByte >> 1) & 1) == 1)
        {
            Console.WriteLine("  步骤 6: BIOS 已响应 (bit1 置位), 继续写 Set_Data(0xBE, 0x02)...");
            var pkg2 = new byte[32];
            pkg2[0] = 0xBE;
            pkg2[1] = 0x02;
            try
            {
                byte[]? r2 = CallSet("Set_Data", pkg2);
                byte ack2 = r2 is { Length: > 0 } ? r2[0] : (byte)0;
                Console.WriteLine($"          Set_Data(0xBE) ACK=0x{ack2:X2}");
            }
            catch (Exception ex) { Console.WriteLine($"          ❌ Set_Data(0xBE) 异常: {ex.Message}"); }
        }
        else
        {
            Console.WriteLine("  步骤 7: bit1 未置位, 跳过 Set_Data(0xBE) (这与 MSI Center 行为一致)");
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("  ✓ 写入流程完成. 请重启电脑使 GPU MUX 切换生效.");
        Console.WriteLine("    重启后再跑 [qs] 或 [r] 验证是否切换成功.");
        Console.ResetColor();
        DumpGpuRegistryState("流程结束时注册表状态 (重启前):");
    }

    /// <summary>保存所有 Get_* 的当前 32 字节 dump 到文件, 用于 diff.</summary>
    public static void SnapshotAllToFile(string path)
    {
        Console.WriteLine($"保存 Get_* 全量 dump 到 {path} ...");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Snapshot @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        string[] methods = { "Get_BIOS", "Get_Device", "Get_Power", "Get_Data",
                             "Get_Thermal", "Get_Fan", "Get_Temperature",
                             "Get_MasterBattery", "Get_Debug", "Get_AP" };

        foreach (string m in methods)
        {
            sb.AppendLine($"== {m} ==");
            for (byte cmd = 0x00; cmd <= 0x10; cmd++)
            {
                try
                {
                    var input = new byte[32]; input[0] = cmd;
                    byte[]? r = CallGet(m, input);
                    if (r is { Length: > 0 })
                    {
                        // 只记录非全零 (byte[0]=ack 除外)
                        bool allZero = true;
                        for (int i = 1; i < r.Length; i++) if (r[i] != 0) { allZero = false; break; }
                        if (!allZero)
                            sb.AppendLine($"  cmd=0x{cmd:X2} : {BitConverter.ToString(r).Replace("-", " ")}");
                    }
                }
                catch { }
            }
            sb.AppendLine();
        }

        System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
        Console.WriteLine("完成.");
    }

    /// <summary>Dump MSI_CentralControl 或其它类的方法签名 (只读, 仅枚举元数据).</summary>
    public static void DumpClassMethodsForCentralControl()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── MSI_CentralControl 类方法签名 (root\\wmi) ─────────");
        Console.ResetColor();

        try
        {
            using var mc = new ManagementClass(new ManagementScope(WmiScope),
                new ManagementPath("MSI_CentralControl"), null);
            mc.Options.UseAmendedQualifiers = true;

            foreach (MethodData md in mc.Methods)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ► {md.Name}");
                Console.ResetColor();
                if (md.InParameters is not null)
                {
                    foreach (PropertyData pd in md.InParameters.Properties)
                        Console.WriteLine($"    [in ] {pd.Name,-20} type={pd.Type} IsArray={pd.IsArray}");
                }
                if (md.OutParameters is not null)
                {
                    foreach (PropertyData pd in md.OutParameters.Properties)
                        Console.WriteLine($"    [out] {pd.Name,-20} type={pd.Type} IsArray={pd.IsArray}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  失败: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// 交互式切换 GPU 模式. 读-改-写流程, 多重确认.
    /// </summary>
    /// <param name="targetDGpu">true=切到独显直连, false=切到 MSHybrid</param>
    public static void SwitchGpuMode(bool targetDGpu)
    {
        string targetName = targetDGpu ? "独显直连" : "MSHybrid";
        byte targetByte = targetDGpu ? (byte)0x40 : (byte)0x00;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 切换 GPU 模式 → {targetName} ──────────────────────");
        Console.ResetColor();

        // 1. 先读回当前 Get_BIOS cmd=0x02 的完整 32 字节 (保留其它 BIOS 设置原样)
        byte[]? current;
        try
        {
            var getInput = new byte[32]; getInput[0] = 0x02;
            current = CallGet("Get_BIOS", getInput);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  读取当前 BIOS 状态失败: {ex.Message}");
            Console.WriteLine("  中止, 未做任何修改.");
            Console.ResetColor();
            return;
        }

        if (current is null || current.Length < 2 || current[0] != 0x01)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  读到的数据异常 (ack != 0x01), 中止.");
            Console.ResetColor();
            return;
        }

        byte currentByte = current[1];
        string currentName = currentByte == 0x40 ? "独显直连"
                           : currentByte == 0x00 ? "MSHybrid"
                           : $"未知(0x{currentByte:X2})";

        Console.WriteLine($"  当前模式: {currentName}  (Get_BIOS[0x02].byte[1] = 0x{currentByte:X2})");
        Console.WriteLine($"  目标模式: {targetName}  (设置 byte[1] = 0x{targetByte:X2})");

        if (currentByte == targetByte)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  当前已是目标模式, 无需切换.");
            Console.ResetColor();
            return;
        }

        // 2. 构造写入包: 原样 32 字节, 只改 byte[1]
        //    注意: Get_BIOS 返回的 byte[0] 是 ack=0x01; 写入时 byte[0] 需还原为 cmd=0x02.
        var toWrite = new byte[32];
        Array.Copy(current, toWrite, 32);
        toWrite[0] = 0x02;          // 写入时 byte[0] 是 cmd code
        toWrite[1] = targetByte;    // byte[1] 是 GPU 模式位

        Console.WriteLine();
        Console.WriteLine("  将要写入的 32 字节 (byte[0]=cmd=0x02, byte[1]=目标值):");
        Console.WriteLine($"    读回: {BitConverter.ToString(current).Replace("-", " ")}");
        Console.WriteLine($"    写入: {BitConverter.ToString(toWrite).Replace("-", " ")}");
        Console.WriteLine();

        // 3. YES 确认
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  ⚠️ 此操作会修改 BIOS 设置, 需重启生效.");
        Console.WriteLine("     输入 YES (三个大写字母) 继续, 其它任何输入将取消:");
        Console.ResetColor();
        Console.Write("  > ");
        string? confirm = Console.ReadLine();
        if (confirm != "YES")
        {
            Console.WriteLine("  已取消, 未做任何修改.");
            return;
        }

        // 4. 调用 Set_BIOS
        byte[]? setResult;
        try
        {
            setResult = CallSet("Set_BIOS", toWrite);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ Set_BIOS 调用异常: {ex.Message}");
            Console.WriteLine("  BIOS 可能未接受写入. 建议立即按 [r] 查看当前状态.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Set_BIOS 返回: {(setResult is null ? "<null>" : BitConverter.ToString(setResult).Replace("-", " "))}");
        byte writeAck = setResult is { Length: > 0 } ? setResult[0] : (byte)0;
        Console.WriteLine($"  写入 ACK: 0x{writeAck:X2}  ({(writeAck == 0x01 ? "成功" : "⚠️ 非 0x01, 可能失败")})");

        // 5. 立即回读验证
        Console.WriteLine();
        Console.WriteLine("  回读验证...");
        try
        {
            var verifyInput = new byte[32]; verifyInput[0] = 0x02;
            byte[]? verify = CallGet("Get_BIOS", verifyInput);
            if (verify is { Length: > 1 })
            {
                byte newByte = verify[1];
                Console.WriteLine($"    回读 byte[1] = 0x{newByte:X2}");
                if (newByte == targetByte)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  ✓ 成功! BIOS 已记录目标模式: {targetName}");
                    Console.WriteLine("     请手动重启计算机使 GPU MUX 切换生效.");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ❌ 回读值 0x{newByte:X2} ≠ 目标 0x{targetByte:X2}, 写入未生效.");
                    Console.ResetColor();
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ❌ 回读失败.");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ 回读异常: {ex.Message}");
            Console.ResetColor();
        }
    }

    // --------------------------------------------------------------------

    /// <summary>
    /// 内部通用: 用 Package_32(bytes) 入参调用指定 Get_* 方法, 返回 byte[32].
    /// 这是 InvokeWithPackage32 的"静默数据版本", 供上层业务逻辑直接调用.
    /// </summary>
    public static byte[]? CallGet(string methodName, byte[] inputBytes)
    {
        if (!methodName.StartsWith("Get_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("v1 阶段仅允许调 Get_*.");
        if (inputBytes is null || inputBytes.Length != 32)
            throw new ArgumentException("inputBytes 必须为 32 字节 (Package_32).", nameof(inputBytes));

        using var pkgClass = new ManagementClass(
            new ManagementScope(WmiScope), new ManagementPath("Package_32"), null);
        ManagementObject pkg = pkgClass.CreateInstance();
        pkg["Bytes"] = inputBytes;

        using var searcher = new ManagementObjectSearcher(WmiScope,
            $"SELECT * FROM {AcpiClass}");
        foreach (ManagementObject mo in searcher.Get())
        {
            ManagementBaseObject inParams = mo.GetMethodParameters(methodName);
            inParams["Data"] = pkg;
            ManagementBaseObject outParams = mo.InvokeMethod(methodName, inParams, null);
            var dataOut = outParams?["Data"] as ManagementBaseObject;
            if (dataOut is null) return null;
            return ExtractBytesFromPackage(dataOut);
        }
        return null;
    }

    /// <summary>
    /// 只读: 读取当前 GPU 模式状态 (验证我们找到的两个位是否一致).
    /// 不写任何寄存器.
    /// </summary>
    public static void ReadGpuMode()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 读取 GPU 模式状态 (只读) ──────────────────────────");
        Console.ResetColor();

        byte[]? bios02 = null;
        byte[]? dev01 = null;

        try
        {
            var input = new byte[32]; input[0] = 0x02;
            bios02 = CallGet("Get_BIOS", input);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Get_BIOS cmd=0x02 失败: {ex.Message}");
        }

        try
        {
            var input = new byte[32]; input[0] = 0x01;
            dev01 = CallGet("Get_Device", input);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Get_Device cmd=0x01 失败: {ex.Message}");
        }

        Console.WriteLine();
        if (bios02 is not null)
        {
            byte ack = bios02[0], b1 = bios02[1];
            Console.WriteLine($"  [持久位] Get_BIOS  cmd=0x02 ack=0x{ack:X2} byte[1]=0x{b1:X2}");
            Console.WriteLine($"     hex: {BitConverter.ToString(bios02).Replace("-", " ")}");
        }
        if (dev01 is not null)
        {
            byte ack = dev01[0], b1 = dev01[1];
            Console.WriteLine($"  [运行位] Get_Device cmd=0x01 ack=0x{ack:X2} byte[1]=0x{b1:X2} (bit6={(b1 >> 6) & 1})");
            Console.WriteLine($"     hex: {BitConverter.ToString(dev01).Replace("-", " ")}");
        }

        Console.WriteLine();

        // 解读
        string? mode = null;
        string? reason = null;
        if (bios02 is not null && dev01 is not null)
        {
            bool biosSaysDGpu = bios02[1] == 0x40;
            bool biosSaysHybrid = bios02[1] == 0x00;
            bool devSaysDGpu = ((dev01[1] >> 6) & 1) == 1;

            if (biosSaysDGpu && devSaysDGpu)
            {
                mode = "独显直连 (Discrete Graphics Only)";
                reason = "两处指示一致 ✓";
            }
            else if (biosSaysHybrid && !devSaysDGpu)
            {
                mode = "MSHybrid (混合输出)";
                reason = "两处指示一致 ✓";
            }
            else
            {
                mode = "未知/不一致";
                reason = $"BIOS[0x02].byte[1]=0x{bios02[1]:X2}, Device[0x01].bit6={(dev01[1] >> 6) & 1} — 两处不匹配, 假设可能被推翻";
            }
        }

        Console.ForegroundColor = mode is not null && mode.StartsWith("未知") ? ConsoleColor.Red : ConsoleColor.Green;
        Console.WriteLine($"  ==> 当前 GPU 模式: {mode ?? "<读取失败>"}");
        if (reason is not null) Console.WriteLine($"      {reason}");
        Console.ResetColor();
    }

    /// <summary>用 Package_32 正确格式对一批方法 × cmd 做扫描.</summary>
    public static void BulkProbeWithPackage32()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 用 Package_32 作为 Data 调用 Get_* (方法 × cmd 扫描) ──");
        Console.ResetColor();

        string[] methods =
        {
            "Get_Device", "Get_BIOS", "Get_Power", "Get_Data",
            "Get_Thermal", "Get_Fan", "Get_Temperature",
            "Get_MasterBattery", "Get_Debug", "Get_AP",
        };

        foreach (string m in methods)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  ==== {m} ====");
            Console.ResetColor();
            for (byte c = 0x00; c <= 0x20; c++)
            {
                InvokeWithPackage32(m, c, silent: true);
            }
        }
    }

    /// <summary>
    /// 从 Package_32 实例里抠出字节流. 尝试两种布局:
    ///   A) 存在一个 UInt8 array 属性 (典型字段名 Bytes)
    ///   B) 32 个独立 UInt8 属性 (bData0..bData31 之类)
    /// </summary>
    private static byte[]? ExtractBytesFromPackage(ManagementBaseObject pkg)
    {
        // 方案 A: 找 array
        foreach (PropertyData pd in pkg.Properties)
        {
            if (pd.IsArray && pd.Type == CimType.UInt8 && pd.Value is byte[] b)
                return b;
            if (pd.IsArray && pd.Type == CimType.UInt8 && pd.Value is Array arr)
            {
                var r = new byte[arr.Length];
                for (int i = 0; i < arr.Length; i++) r[i] = Convert.ToByte(arr.GetValue(i));
                return r;
            }
        }
        // 方案 B: 拼接所有 UInt8 标量属性
        var list = new List<byte>();
        bool any = false;
        foreach (PropertyData pd in pkg.Properties)
        {
            if (pd.Type == CimType.UInt8 && !pd.IsArray)
            {
                any = true;
                try { list.Add(Convert.ToByte(pd.Value ?? (byte)0)); }
                catch { list.Add(0); }
            }
        }
        return any ? list.ToArray() : null;
    }

    /// <summary>
    /// 诊断: 对每个 Get_* 方法, 打印它 InParameters 里 Data 属性的真实
    /// CIM 类型, IsArray, Qualifier 列表等 — 用于确定该传什么样的值.
    /// </summary>
    public static void DiagnoseMethodSignatures()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 诊断: MSI_ACPI 各 Get_* 方法的 Data 入参真实类型 ──");
        Console.ResetColor();

        using var mc = new ManagementClass(new ManagementScope(WmiScope),
            new ManagementPath(AcpiClass), null);
        mc.Options.UseAmendedQualifiers = true;

        foreach (MethodData md in mc.Methods)
        {
            if (!md.Name.StartsWith("Get_", StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ► {md.Name}");
            Console.ResetColor();

            if (md.InParameters is null || md.InParameters.Properties.Count == 0)
            {
                Console.WriteLine("    (无入参 — 可直接无参调用)");
                continue;
            }

            foreach (PropertyData pd in md.InParameters.Properties)
            {
                Console.WriteLine($"    [{pd.Name}]");
                Console.WriteLine($"      CIM Type   : {pd.Type}");
                Console.WriteLine($"      IsArray    : {pd.IsArray}");
                Console.WriteLine($"      IsLocal    : {pd.IsLocal}");
                Console.WriteLine("      Qualifiers:");
                foreach (QualifierData qd in pd.Qualifiers)
                {
                    Console.WriteLine($"        {qd.Name,-18} = {SystemInfo.FormatValue(qd.Value)}");
                }
            }
        }
    }

    /// <summary>
    /// 尝试无参调用所有只有 [out] 参数 (或 in 可选) 的 Get_* 方法.
    /// Get_EC / Get_Thermal / Get_Fan / Get_Data 等若成功, 会直接返回一个
    /// 完整 buffer, 我们可以从中肉眼推断 GPU 模式位在哪里.
    /// </summary>
    public static void TryAllVoidGetters()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 无参调用所有 Get_* (失败的跳过) ──────────────────");
        Console.ResetColor();

        string[] methods =
        {
            "Get_WMI", "Get_EC", "Get_Package", "Get_Thermal", "Get_Fan",
            "Get_Device", "Get_BIOS", "Get_SMBUS", "Get_MasterBattery",
            "Get_SlaveBattery", "Get_Temperature", "Get_Power",
            "Get_Debug", "Get_AP", "Get_Data",
        };

        foreach (string m in methods)
        {
            try
            {
                byte[]? r = InvokeGetMethod(m, null, silent: true);
                if (r is null || r.Length == 0)
                {
                    Console.WriteLine($"  [{m,-20}] (无返回数据)");
                    continue;
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ [{m,-20}] {r.Length} bytes");
                Console.ResetColor();
                Console.WriteLine($"      hex : {BitConverter.ToString(r).Replace("-", " ")}");
            }
            catch (ManagementException mex)
            {
                Console.WriteLine($"  ✗ [{m,-20}] {mex.ErrorCode}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ [{m,-20}] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 对指定方法, 尝试多种长度的 byte[] 作为 Data 入参, 看哪个能被 BIOS
    /// 接受. 从 4/8/16/32/64/128/256 字节, 全 0 / 只设第 0 字节为 cmd / 填 0xFF
    /// 这几种组合都试.
    /// </summary>
    public static void ProbeLengths(string methodName, byte cmd)
    {
        if (!methodName.StartsWith("Get_", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("仅允许探测 Get_* 方法.");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 对 {methodName} 尝试不同 buffer 长度 (cmd=0x{cmd:X2}) ──");
        Console.ResetColor();

        int[] lengths = { 4, 8, 16, 32, 64, 128, 256 };
        byte[] fills = { 0x00, 0xFF };

        foreach (int len in lengths)
        {
            foreach (byte fill in fills)
            {
                var buf = new byte[len];
                if (fill != 0) Array.Fill(buf, fill);
                buf[0] = cmd;

                try
                {
                    byte[]? r = InvokeGetMethod(methodName, buf, silent: true);
                    if (r is not null && r.Length > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✓ len={len,-4} fill=0x{fill:X2}  -> 返回 {r.Length} bytes");
                        Console.ResetColor();
                        Console.WriteLine($"      hex : {FormatBytes(r, 48)}");
                    }
                }
                catch (ManagementException mex)
                {
                    // 只在极少数首次失败时打印, 避免刷屏 (其实不需要)
                    _ = mex;
                }
                catch { /* skip */ }
            }
        }
    }

    /// <summary>
    /// 对 Get_Device, Get_Power, Get_BIOS, Get_WMI 做一次批量探测.
    /// 常见命令码范围 0x00 ~ 0x10 都试一遍, 看哪个能返回非零数据.
    /// </summary>
    public static void BulkProbe()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 批量探测: Get_WMI, Get_Device, Get_BIOS, Get_Power ──");
        Console.WriteLine("   对每个方法尝试 cmd=0x00..0x20, 记录有效返回");
        Console.ResetColor();

        string[] methods = { "Get_WMI", "Get_Device", "Get_BIOS", "Get_Power", "Get_Data" };

        foreach (string m in methods)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  ==== {m} ====");
            Console.ResetColor();

            // 先试无参调用
            try
            {
                byte[]? r = InvokeGetMethod(m, null, silent: true);
                if (r is not null && r.Length > 0)
                    Console.WriteLine($"    [no arg      ] {FormatBytes(r, 32)}");
            }
            catch { /* 此方法需要入参或根本不支持, 静默跳过 */ }

            // 再遍历 cmd 0x00..0x20
            for (byte cmd = 0x00; cmd <= 0x20; cmd++)
            {
                try
                {
                    byte[] input = BuildInputBuffer(cmd, 32);
                    byte[]? r = InvokeGetMethod(m, input, silent: true);
                    if (r is not null && r.Length > 0 && !IsAllZeroOrEmpty(r))
                    {
                        Console.WriteLine($"    [cmd=0x{cmd:X2}   ] {FormatBytes(r, 32)}");
                    }
                }
                catch
                {
                    // 这个 cmd 不被此方法接受, 忽略
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("  说明: 能返回非全零数据的 (cmd, 方法) 组合值得后续重点研究.");
    }

    // --------------------------------------------------------------------
    // 底层: WMI 方法调用
    // --------------------------------------------------------------------

    /// <summary>
    /// 在 MSI_ACPI 的唯一实例上调用指定 Get_* 方法.
    /// </summary>
    /// <param name="methodName">方法名, 必须以 Get_ 开头.</param>
    /// <param name="inputData">
    /// Data 参数. null 表示无参调用. 非 null 表示传一个 byte[] 当作 ACPI Package 入参.
    /// </param>
    /// <returns>返回的 byte[] (解包自 Data 出参), 失败时 null.</returns>
    private static byte[]? InvokeGetMethod(string methodName, byte[]? inputData, bool silent = false)
    {
        if (!methodName.StartsWith("Get_", StringComparison.OrdinalIgnoreCase))
        {
            // 双保险: v1 阶段绝不能调 Set_*
            throw new InvalidOperationException($"Refused to invoke non-Get method: {methodName}");
        }

        using var searcher = new ManagementObjectSearcher(WmiScope, $"SELECT * FROM {AcpiClass}");
        foreach (ManagementObject mo in searcher.Get())
        {
            // 获取方法的入参模板
            ManagementBaseObject? inParams = null;
            try
            {
                inParams = mo.GetMethodParameters(methodName);
            }
            catch
            {
                // 此方法没有 InParameters (例如 Get_WMI 无参)
                inParams = null;
            }

            if (inParams is not null && inputData is not null)
            {
                // 尝试按属性名 "Data" 塞进去
                try { inParams["Data"] = inputData; }
                catch { }
            }

            ManagementBaseObject? outParams;
            try
            {
                outParams = mo.InvokeMethod(methodName, inParams, null);
            }
            catch (ManagementException mex)
            {
                if (!silent)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"    WMI 调用失败 [{methodName}]: {mex.ErrorCode} {mex.Message}");
                    Console.ResetColor();
                }
                if (silent) throw;  // 静默模式下把异常抛给上层决定
                return null;
            }

            if (outParams is null) return null;

            // 读 "Data" 出参
            object? dataOut = null;
            try { dataOut = outParams["Data"]; } catch { }
            if (dataOut is byte[] bytes) return bytes;
            if (dataOut is Array arr)
            {
                var result = new byte[arr.Length];
                int i = 0;
                foreach (var v in arr)
                {
                    result[i++] = Convert.ToByte(v);
                }
                return result;
            }
            return null;
        }

        return null;
    }

    // --------------------------------------------------------------------
    // 工具
    // --------------------------------------------------------------------

    /// <summary>
    /// MSI 的 ACPI 方法一般期待一个固定长度的字节包 (常见 32 字节),
    /// 第 0 个字节为命令码, 后面全填 0 或 0xFF.
    /// </summary>
    private static byte[] BuildInputBuffer(byte cmd, int length)
    {
        var buf = new byte[length];
        buf[0] = cmd;
        return buf;
    }

    private static void DumpBytes(string title, byte[] data)
    {
        Console.WriteLine($"  {title}  ({data.Length} bytes):");
        Console.WriteLine($"    hex : {BitConverter.ToString(data).Replace("-", " ")}");
        var sb = new StringBuilder();
        foreach (byte b in data)
            sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        Console.WriteLine($"    ascii: {sb}");
    }

    private static string FormatBytes(byte[] data, int max)
    {
        int n = Math.Min(data.Length, max);
        return BitConverter.ToString(data, 0, n).Replace('-', ' ') +
               (data.Length > max ? $" ... (共 {data.Length} 字节)" : "");
    }

    private static bool IsAllZeroOrEmpty(byte[] data)
    {
        foreach (byte b in data)
            if (b != 0) return false;
        return true;
    }
}
