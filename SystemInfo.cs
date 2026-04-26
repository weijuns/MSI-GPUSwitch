using System.Management;

namespace MsiGpuSwitch;

/// <summary>
/// 打印机器基本信息 (型号 / BIOS / CPU / GPU), 用于和社区其他用户比对.
/// </summary>
internal static class SystemInfo
{
    public static void PrintSystemInfo()
    {
        PrintSection("Win32_ComputerSystem", "root\\cimv2", "Win32_ComputerSystem",
            new[] { "Manufacturer", "Model", "SystemFamily", "SystemSKUNumber", "TotalPhysicalMemory" });

        PrintSection("Win32_BIOS", "root\\cimv2", "Win32_BIOS",
            new[] { "Manufacturer", "Name", "Version", "SMBIOSBIOSVersion", "ReleaseDate" });

        PrintSection("Win32_Processor", "root\\cimv2", "Win32_Processor",
            new[] { "Name", "NumberOfCores", "NumberOfLogicalProcessors" });

        PrintSection("Win32_VideoController", "root\\cimv2", "Win32_VideoController",
            new[] { "Name", "VideoProcessor", "AdapterRAM", "DriverVersion", "PNPDeviceID", "Status" });

        PrintSection("Win32_OperatingSystem", "root\\cimv2", "Win32_OperatingSystem",
            new[] { "Caption", "Version", "BuildNumber", "OSArchitecture" });
    }

    private static void PrintSection(string title, string scope, string className, string[] props)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"── {title} ─────────────────────────────────────────");
        Console.ResetColor();

        try
        {
            using var searcher = new ManagementObjectSearcher(scope, $"SELECT * FROM {className}");
            int idx = 0;
            foreach (ManagementObject mo in searcher.Get())
            {
                if (idx > 0) Console.WriteLine("  ---");
                foreach (var p in props)
                {
                    object? val = null;
                    try { val = mo[p]; } catch { }
                    Console.WriteLine($"  {p,-28}= {FormatValue(val)}");
                }
                idx++;
            }
            if (idx == 0) Console.WriteLine("  (无实例)");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  查询失败: {ex.Message}");
            Console.ResetColor();
        }
    }

    public static string FormatValue(object? val)
    {
        if (val is null) return "<null>";
        if (val is byte[] bytes)
            return $"byte[{bytes.Length}] = {BitConverter.ToString(bytes)}";
        if (val is Array arr)
        {
            var parts = new List<string>();
            foreach (var item in arr) parts.Add(item?.ToString() ?? "<null>");
            return "[" + string.Join(", ", parts) + "]";
        }
        return val.ToString() ?? "<null>";
    }
}
