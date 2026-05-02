using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace MsiGpuSwitch;

/// <summary>
/// 管理 MSI Foundation Service (MSIAPService.exe) 的安装/启动/停止/卸载.
/// 
/// 这是 .NET Framework 4 的 Windows Service, 用 InstallUtil.exe 注册.
/// 实测在 FM 卸载后, 即使 msiapcfg.dll + MofImagePath 都装了,
/// EC 写入仍可能不生效 — 怀疑 MSIAPService 在跑时为 ACPI 方法提供 "会话上下文".
/// </summary>
internal static class MsiApService
{
    public const string ServiceName = "MSI Foundation Service";

    private static string MsiApSvcPath => FindMsiApServiceExe()
        ?? throw new FileNotFoundException("找不到 MSIAPService.exe (项目 FeatureManager 目录或 ProgramData)");

    private static string InstallUtilExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe");

    private static string? FindMsiApServiceExe()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "FeatureManager", "MSIAPService.exe"),
            @"C:\ProgramData\MSI Flux\FeatureManager\MSIAPService.exe",
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "FeatureManager", "MSIAPService.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static void PrintStatus()
    {
        Console.WriteLine("── MSI Foundation Service 状态 ────────────────────");
        Console.WriteLine($"  exe 路径: {FindMsiApServiceExe() ?? "(找不到)"}");
        Console.WriteLine($"  服务已注册: {(IsRegistered() ? "✅" : "❌ (未注册)")}");
        try
        {
            using var sc = new ServiceController(ServiceName);
            Console.WriteLine($"  当前状态: {sc.Status}");
            Console.WriteLine($"  启动类型: {sc.StartType}");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("  当前状态: <无法读取>");
        }
    }

    public static bool IsRegistered()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static bool StartIfInstalled()
    {
        if (!IsRegistered()) return false;
        return Start();
    }

    public static bool Install()
    {
        string exe = FindMsiApServiceExe() ?? "";
        if (!File.Exists(exe))
        {
            Console.WriteLine($"  ❌ 找不到 MSIAPService.exe");
            return false;
        }
        if (!File.Exists(InstallUtilExe))
        {
            Console.WriteLine($"  ❌ 找不到 InstallUtil.exe ({InstallUtilExe})");
            return false;
        }

        Console.WriteLine($"  使用 InstallUtil 安装服务 ({exe})...");
        var psi = new ProcessStartInfo(InstallUtilExe)
        {
            Arguments = $"\"{exe}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        Console.WriteLine($"  退出码: {p.ExitCode}");
        if (p.ExitCode != 0)
        {
            Console.WriteLine($"  stdout: {stdout}");
            Console.WriteLine($"  stderr: {stderr}");
            return false;
        }

        try
        {
            var sc = new ProcessStartInfo("sc.exe")
            {
                Arguments = $"config \"{ServiceName}\" start=demand",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var cp = Process.Start(sc)!;
            cp.WaitForExit(5000);
            if (cp.ExitCode == 0)
                Console.WriteLine("  ✓ 已设为手动启动 (开机不会自启)");
            else
                Console.WriteLine("  ⚠️ 设置手动启动失败, 服务可能开机自启");
        }
        catch { Console.WriteLine("  ⚠️ 设置手动启动失败"); }

        Console.WriteLine("  ✓ 安装完成");
        return true;
    }

    public static bool Uninstall()
    {
        if (!File.Exists(InstallUtilExe)) return false;
        string exe = FindMsiApServiceExe() ?? "";
        if (!File.Exists(exe)) return false;
        Console.WriteLine($"  使用 InstallUtil /u 卸载服务...");
        var psi = new ProcessStartInfo(InstallUtilExe)
        {
            Arguments = $"/u \"{exe}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
        Console.WriteLine($"  退出码: {p.ExitCode}");
        return p.ExitCode == 0;
    }

    public static bool Start()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                Console.WriteLine($"  服务已在运行");
                return true;
            }
            Console.WriteLine($"  启动服务...");
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            Console.WriteLine($"  ✓ 已启动");
            return true;
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"  ❌ 服务未注册. 先用 [srv-install] 安装.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 启动失败: {ex.Message}");
            return false;
        }
    }

    public static bool Stop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped) { Console.WriteLine("  已停止"); return true; }
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            Console.WriteLine("  ✓ 已停止");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 停止失败: {ex.Message}");
            return false;
        }
    }
}
