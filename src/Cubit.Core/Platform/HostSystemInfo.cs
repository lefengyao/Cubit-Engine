using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Cubit.Core.Platform;

/// <summary>通用宿主设备诊断；只读取平台提供的静态信息，不创建设备或后台线程。</summary>
public static class HostSystemInfo
{
    /// <summary>CPU 品牌/型号；Windows 优先返回系统登记的正式名称。</summary>
    public static string CpuModel => ReadCpuModel();

    public static int LogicalProcessorCount => Environment.ProcessorCount;

    private static string ReadCpuModel()
    {
        var value = TryReadWindowsCpuName();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = RuntimeInformation.ProcessArchitecture.ToString();
        }

        return Normalize(value);
    }

    private static string? TryReadWindowsCpuName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                writable: false);
            return key?.GetValue("ProcessorNameString") as string;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
