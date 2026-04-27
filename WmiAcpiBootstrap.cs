using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;

namespace MsiGpuSwitch;

/// <summary>
/// 完全摆脱 Feature Manager 的关键引导器.
///
/// 工作原理:
///   Windows 内置的 wmiacpi.sys 驱动加载时, 会读取注册表
///   HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi\MofImagePath
///   指向的 PE 文件, 提取里面的 BMF (Binary MOF) 资源,
///   并把 MSI_ACPI/Package_32 等 ACPI WMI 类绑定到 BIOS 的 _WMI 方法.
///
/// 因此只要我们:
///   1. 把 msiapcfg.dll (16KB 的 BMF-in-PE) 放到 C:\Windows\SysWOW64\
///   2. 设置 MofImagePath 指向它
///   3. 重启
///
/// 就能让 wmiacpi.sys 加载 MSI 的 ACPI 方法绑定,
/// 之后即使没有 Feature Manager, WMI ACPI 调用也能正常工作.
///
/// 这是从反编译 MSIWMIACPI2.dll 的 InstallService() 方法发现的真相.
/// </summary>
internal static class WmiAcpiBootstrap
{
    private const string WmiAcpiServiceKey =
        @"SYSTEM\CurrentControlSet\Services\WmiAcpi";
    private const string MofImagePathValue = "MofImagePath";
    private const string ExpectedMofImagePath = @"%windir%\sysWOW64\msiapcfg.dll";
    private const string MsiApCfgFileName = "msiapcfg.dll";

    public sealed record Status(
        bool DllInPlace,
        bool RegistryConfigured,
        string? CurrentMofImagePath,
        string DstDllPath,
        long? DstDllSize,
        bool IsAdmin)
    {
        public bool IsFullyConfigured => DllInPlace && RegistryConfigured;
    }

    /// <summary>检查当前 wmiacpi.sys 是否已配置加载 MSI 的 BMF.</summary>
    public static Status Check()
    {
        string sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        string dst = Path.Combine(sysWow64, MsiApCfgFileName);

        bool dllInPlace = File.Exists(dst);
        long? dstSize = dllInPlace ? new FileInfo(dst).Length : (long?)null;

        string? current = null;
        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(WmiAcpiServiceKey, writable: false);
            current = k?.GetValue(MofImagePathValue) as string;
        }
        catch { /* permission, ignore */ }

        bool regOk = !string.IsNullOrEmpty(current) &&
            current.IndexOf("msiapcfg.dll", StringComparison.OrdinalIgnoreCase) >= 0;

        return new Status(dllInPlace, regOk, current, dst, dstSize, IsAdmin());
    }

    public static void PrintStatus()
    {
        var s = Check();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("── WMI ACPI 引导状态 ───────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  msiapcfg.dll 路径    : {s.DstDllPath}");
        Console.WriteLine($"  msiapcfg.dll 存在    : {Yes(s.DllInPlace)}{(s.DstDllSize.HasValue ? $" ({s.DstDllSize} bytes)" : "")}");
        Console.WriteLine($"  MofImagePath 注册表  : {(s.CurrentMofImagePath ?? "<未设置>")}");
        Console.WriteLine($"  MofImagePath 已配置  : {Yes(s.RegistryConfigured)}");
        Console.WriteLine($"  当前管理员权限       : {Yes(s.IsAdmin)}");
        Console.WriteLine();
        if (s.IsFullyConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ✅ 已完全配置. WMI ACPI 应该能正常工作 (即使没有 Feature Manager).");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠️ 未完全配置. 调用 [boot] 命令进行引导, 重启后生效.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// 一次性引导: 复制 msiapcfg.dll 到 SysWOW64, 设置 MofImagePath 注册表.
    /// 重启后即使卸载 Feature Manager, WMI ACPI 仍可工作.
    /// </summary>
    public static bool Install(string? sourceDll = null)
    {
        if (!IsAdmin())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ 需要管理员权限. 请以管理员身份重新运行本程序.");
            Console.ResetColor();
            return false;
        }

        // 1. 找源 dll
        string? src = sourceDll ?? FindSourceDll();
        if (src is null || !File.Exists(src))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ 找不到 msiapcfg.dll 源文件. 期望位置:");
            Console.WriteLine("   - <程序目录>\\msiapcfg.dll");
            Console.WriteLine("   - <程序目录>\\FeatureManager\\msiapcfg.dll");
            Console.WriteLine("   - C:\\Windows\\SysWOW64\\msiapcfg.dll (备份用)");
            Console.ResetColor();
            return false;
        }

        // 2. 复制到 SysWOW64
        string sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        string dst = Path.Combine(sysWow64, MsiApCfgFileName);

        try
        {
            if (File.Exists(dst))
            {
                Console.WriteLine($"  目标文件已存在, 跳过复制: {dst}");
            }
            else
            {
                File.Copy(src, dst, overwrite: false);
                Console.WriteLine($"  ✓ 已复制 {src} → {dst}");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ 复制失败: {ex.Message}");
            Console.ResetColor();
            return false;
        }

        // 3. 写注册表 MofImagePath
        try
        {
            using RegistryKey k = Registry.LocalMachine.OpenSubKey(WmiAcpiServiceKey, writable: true)
                ?? throw new InvalidOperationException($"无法打开 HKLM\\{WmiAcpiServiceKey}");

            string? current = k.GetValue(MofImagePathValue) as string;
            if (string.Equals(current, ExpectedMofImagePath, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  注册表 {MofImagePathValue} 已正确配置.");
            }
            else
            {
                k.SetValue(MofImagePathValue, ExpectedMofImagePath, RegistryValueKind.String);
                Console.WriteLine($"  ✓ 已设置 HKLM\\...\\WmiAcpi\\{MofImagePathValue} = {ExpectedMofImagePath}");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ 写注册表失败: {ex.Message}");
            Console.ResetColor();
            return false;
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("✅ 引导完成. 请重启电脑使配置生效.");
        Console.WriteLine("   重启后, WMI ACPI 调用将完全独立于 Feature Manager.");
        Console.ResetColor();
        return true;
    }

    /// <summary>
    /// 卸载: 删除 msiapcfg.dll, 清除 MofImagePath 注册表项.
    /// 警告: 这会让所有依赖 wmiacpi.sys + MSI ACPI 的工具失效, 包括 Feature Manager 本身.
    /// </summary>
    public static bool Uninstall()
    {
        if (!IsAdmin())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ 需要管理员权限.");
            Console.ResetColor();
            return false;
        }

        try
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(WmiAcpiServiceKey, writable: true);
            k?.DeleteValue(MofImagePathValue, throwOnMissingValue: false);
            Console.WriteLine($"  ✓ 已删除注册表项 {MofImagePathValue}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️ 删除注册表项失败: {ex.Message}");
        }

        try
        {
            string sysWow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            string dst = Path.Combine(sysWow64, MsiApCfgFileName);
            if (File.Exists(dst))
            {
                File.Delete(dst);
                Console.WriteLine($"  ✓ 已删除 {dst}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️ 删除文件失败 (可能被 wmiacpi.sys 占用, 重启后再删): {ex.Message}");
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⚠️ 卸载完成. 重启后 WMI ACPI 调用将不再可用.");
        Console.ResetColor();
        return true;
    }

    private static string? FindSourceDll()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, MsiApCfgFileName),
            Path.Combine(baseDir, "FeatureManager", MsiApCfgFileName),
            // 项目源码目录 (开发期)
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "FeatureManager", MsiApCfgFileName)),
            // FM 安装位置 (兼容已装 FM 的机器)
            @"C:\Program Files (x86)\Feature Manager\msiapcfg.dll",
        };
        foreach (var p in candidates)
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string Yes(bool v) => v ? "✅ 是" : "❌ 否";
}
