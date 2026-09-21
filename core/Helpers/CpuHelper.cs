using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace core.Helpers;

public partial class CpuHelper : IDisposable
{
    private readonly PerformanceCounter _totalCounter;
    private bool _disposed;

    public CpuHelper()
    {
        _totalCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
    }

    public void WarmUp() => _totalCounter.NextValue();

    public double GetUsage() => Math.Round(_totalCounter.NextValue(), 0);

    public static string GetProcessorCoreName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "Unknown CPU";
        }
        catch
        {
            return "Unknown CPU";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RTL_OSVERSIONINFOW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        public fixed char szCSDVersion[128];
    }

    [LibraryImport("ntdll.dll")]
    private static partial int RtlGetVersion(ref RTL_OSVERSIONINFOW versionInfo);

    private static uint GetTrueBuildNumber()
    {
        var info = new RTL_OSVERSIONINFOW
        {
            dwOSVersionInfoSize = (uint) Marshal.SizeOf<RTL_OSVERSIONINFOW>()
        };

        int status = RtlGetVersion(ref info);
        if (status != 0)
        {
            throw new InvalidOperationException($"RtlGetVersion failed (NTSTATUS 0x{status:X8}).");
        }

        return info.dwBuildNumber;
    }

    public static string GetOSName()
    {
        var build = GetTrueBuildNumber();
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var productName = key?.GetValue("ProductName") as string ?? "Windows";

        if (build >= 22000 && productName.StartsWith("Windows 10"))
        {
            productName = productName.Replace("Windows 10", "Windows 11");
        }

        var displayVersion = key?.GetValue("DisplayVersion")?.ToString();

        return $"{productName} {displayVersion}";
    }

    public static (string? MainName, string? MainVer, string? BIOSVer) GetMainboardInfo()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        if (key is null)
        {
            return ("Unknown", "Unknown", "Unknown");
        }

        var baseProd = key.GetValue("BaseBoardProduct")?.ToString();
        var baseVer = key.GetValue("BaseBoardVersion")?.ToString();
        var biosVer = key.GetValue("BIOSVersion")?.ToString();

        return (baseProd, baseVer, biosVer);
    }

    // Source - https://stackoverflow.com/a/66459322
    // Posted by Steven Rands
    // Retrieved 2026-02-28, License - CC BY-SA 4.0
    public static TimeSpan GetUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public void Dispose()
    {
        if (_disposed)
            return;
        _totalCounter.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
