using System.Management;
using System.Text;

namespace MsiGpuSwitch;

/// <summary>
/// WMI 命名空间/类/实例/方法探测器. 全部只读.
/// </summary>
internal static class WmiProbe
{
    /// <summary>
    /// 枚举 root\wmi (ACPI/WMI 接口) 下所有名字里带 MSI 的类.
    /// 这是 MSI 专有控制接口最可能出现的位置.
    /// </summary>
    public static void EnumerateMsiClasses()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 枚举 root\\wmi 下的 MSI_* 类 ─────────────────────");
        Console.ResetColor();

        var classes = EnumClassNames("root\\wmi")
            .Where(c => c.Contains("MSI", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c)
            .ToList();

        if (classes.Count == 0)
        {
            Console.WriteLine("  (root\\wmi 下未找到任何 MSI_* 类, 机器可能不暴露 MSI 专属 WMI 接口)");
        }
        else
        {
            foreach (var c in classes)
                Console.WriteLine($"  • {c}");
        }

        // 也扫一下 root\cimv2 和 root 本身, 以防万一
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── 枚举 root 下的 MSI 相关子命名空间 ─────────────────");
        Console.ResetColor();
        foreach (var ns in EnumNamespaces("root"))
        {
            if (ns.Contains("MSI", StringComparison.OrdinalIgnoreCase) ||
                ns.Contains("WMI", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  • root\\{ns}");
            }
        }
    }

    /// <summary>
    /// 枚举 root\cimv2 下与 GPU 模式相关的标准类.
    /// </summary>
    public static void EnumerateGpuRelatedClasses()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("── root\\cimv2 下 GPU 相关类的实例数量 ───────────────");
        Console.ResetColor();

        string[] candidates =
        {
            "Win32_VideoController",
            "Win32_DisplayConfiguration",
            "Win32_PnPEntity",
            "Win32_SystemDriver",
            "MSAcpi_ThermalZoneTemperature",
        };

        foreach (var cls in candidates)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\cimv2", $"SELECT * FROM {cls}");
                int count = 0;
                foreach (var _ in searcher.Get()) count++;
                Console.WriteLine($"  {cls,-40} 实例数: {count}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {cls,-40} 查询失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 列出指定 WMI 类的所有实例, 每个实例的所有属性都打印.
    /// 可直接接受 "ClassName" (默认 root\wmi) 或 "root\cimv2:ClassName" 形式.
    /// </summary>
    public static void DumpClassInstances(string fullName)
    {
        var (scope, cls) = ParseScopeAndClass(fullName);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 实例 dump: {scope}:{cls} ───────────────────────");
        Console.ResetColor();

        using var searcher = new ManagementObjectSearcher(scope, $"SELECT * FROM {cls}");
        int idx = 0;
        foreach (ManagementObject mo in searcher.Get())
        {
            Console.WriteLine($"  ── instance [{idx}] ──");
            foreach (PropertyData pd in mo.Properties)
            {
                object? val = null;
                try { val = pd.Value; } catch { }
                Console.WriteLine($"    {pd.Name,-28}({pd.Type}) = {SystemInfo.FormatValue(val)}");
            }
            idx++;
        }
        if (idx == 0) Console.WriteLine("  (无实例)");
    }

    /// <summary>
    /// Dump 指定 WMI 类定义的所有方法及其入参/出参签名.
    /// 这是找切换方法的关键: 方法名通常带 SET/GET, 参数里有 DataID/Data.
    /// </summary>
    public static void DumpClassMethods(string fullName)
    {
        var (scope, cls) = ParseScopeAndClass(fullName);

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── 方法签名 dump: {scope}:{cls} ───────────────────");
        Console.ResetColor();

        using var mc = new ManagementClass(new ManagementScope(scope), new ManagementPath(cls), null);
        mc.Options.UseAmendedQualifiers = true;

        if (mc.Methods.Count == 0)
        {
            Console.WriteLine("  (此类未定义方法)");
            return;
        }

        foreach (MethodData md in mc.Methods)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ► {md.Name}");
            Console.ResetColor();

            PrintQualifiers("    描述", md.Qualifiers);

            if (md.InParameters is not null && md.InParameters.Properties.Count > 0)
            {
                Console.WriteLine("    入参:");
                foreach (PropertyData pd in md.InParameters.Properties)
                    Console.WriteLine($"      [in ] {pd.Name,-20} {pd.Type}");
            }
            if (md.OutParameters is not null && md.OutParameters.Properties.Count > 0)
            {
                Console.WriteLine("    出参:");
                foreach (PropertyData pd in md.OutParameters.Properties)
                    Console.WriteLine($"      [out] {pd.Name,-20} {pd.Type}");
            }
        }

        // 也把类级别的 qualifier 打出来 (常包含 GUID 等信息)
        Console.WriteLine();
        Console.WriteLine("  类级限定符 (Class Qualifiers):");
        foreach (QualifierData qd in mc.Qualifiers)
        {
            Console.WriteLine($"    {qd.Name,-20} = {SystemInfo.FormatValue(qd.Value)}");
        }
    }

    /// <summary>
    /// 一次性把所有 MSI 相关的东西都 dump 进一个文本文件, 方便外发给别人对比.
    /// </summary>
    public static void DumpAllToFile(string path)
    {
        var sw = new StringWriter();
        var origOut = Console.Out;
        Console.SetOut(sw);
        try
        {
            SystemInfo.PrintSystemInfo();
            Console.WriteLine();
            EnumerateMsiClasses();
            Console.WriteLine();

            // 对 root\wmi 下每个 MSI_* 类都 dump 实例和方法
            var classes = EnumClassNames("root\\wmi")
                .Where(c => c.Contains("MSI", StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c);

            foreach (var cls in classes)
            {
                Console.WriteLine();
                Console.WriteLine($"############ {cls} ############");
                try { DumpClassInstances("root\\wmi:" + cls); }
                catch (Exception ex) { Console.WriteLine($"  实例 dump 失败: {ex.Message}"); }
                try { DumpClassMethods("root\\wmi:" + cls); }
                catch (Exception ex) { Console.WriteLine($"  方法 dump 失败: {ex.Message}"); }
            }
        }
        finally
        {
            Console.SetOut(origOut);
        }

        File.WriteAllText(path, sw.ToString(), Encoding.UTF8);
    }

    // --------------------------------------------------------------------
    // 底层工具函数
    // --------------------------------------------------------------------

    private static IEnumerable<string> EnumClassNames(string scope)
    {
        var ms = new ManagementScope(scope);
        ms.Connect();
        using var enumerator = new ManagementObjectSearcher(ms,
            new SelectQuery("meta_class"));
        foreach (ManagementClass cls in enumerator.Get())
        {
            yield return cls["__CLASS"]?.ToString() ?? string.Empty;
        }
    }

    private static IEnumerable<string> EnumNamespaces(string scope)
    {
        using var searcher = new ManagementObjectSearcher(scope, "SELECT * FROM __Namespace");
        foreach (ManagementObject mo in searcher.Get())
        {
            yield return mo["Name"]?.ToString() ?? string.Empty;
        }
    }

    private static (string scope, string cls) ParseScopeAndClass(string fullName)
    {
        int colon = fullName.IndexOf(':');
        if (colon > 0)
        {
            return (fullName.Substring(0, colon), fullName.Substring(colon + 1));
        }
        // 默认 root\wmi
        return ("root\\wmi", fullName);
    }

    private static void PrintQualifiers(string prefix, QualifierDataCollection qs)
    {
        foreach (QualifierData qd in qs)
        {
            if (qd.Name == "Description" || qd.Name == "WmiDataId" || qd.Name == "Implemented")
            {
                Console.WriteLine($"{prefix} [{qd.Name}] = {SystemInfo.FormatValue(qd.Value)}");
            }
        }
    }
}
