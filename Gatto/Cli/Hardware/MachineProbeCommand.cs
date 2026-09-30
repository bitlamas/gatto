using System.Runtime.InteropServices;
using System.Text;

namespace Gatto.Cli.Hardware;

//prints key=value lines for Core to parse. a probe reads nothing under the home and writes nothing, and a section that throws still reports

//the whole class is Windows-only by construction, declared so CA1416 does not have to find it at every call site
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class MachineProbeCommand
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    //the kernel32 half, no driver and no spawn. installed is optional, a firmware that hides its modules makes the call return false
    public static IEnumerable<string> MemoryLines()
    {
        var lines = new List<string>();

        try
        {
            if (GetPhysicallyInstalledSystemMemory(out var kilobytes) && kilobytes > 0)
                lines.Add("installed=" + kilobytes * 1024);
        }
        catch (Exception ex) { lines.Add("error_installed=" + OneLine(ex.Message)); }

        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                lines.Add("visible=" + status.TotalPhys);
        }
        catch (Exception ex) { lines.Add("error_visible=" + OneLine(ex.Message)); }

        try
        {
            //read the processor name from the registry, a WMI query costs a CIM cold start this child exists to avoid
            var name = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString", null) as string;
            //the name is passed raw, trimming and case are display decisions made where they are tested
            if (!string.IsNullOrEmpty(name)) lines.Add("cpu=" + OneLine(name));
        }
        catch (Exception ex) { lines.Add("error_cpu=" + OneLine(ex.Message)); }

        return lines;
    }

    //the whole report, and it never throws
    public static string Report() => Report(VulkanReader.Dump);

    //the graphics section is a parameter so a test can run this without loading a driver. injecting one proves the assembly, and nothing about VulkanReader itself
    internal static string Report(Func<string> graphics)
    {
        var sb = new StringBuilder();
        foreach (var line in MemoryLines()) sb.AppendLine(line);

        try { sb.Append(graphics()); }
        catch (Exception ex) { sb.AppendLine("error_vulkan=" + OneLine(ex.Message)); }

        return sb.ToString();
    }

    //the protocol is line-based, so a newline in a value would split one fact in two
    private static string OneLine(string s) =>
        s.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
}
