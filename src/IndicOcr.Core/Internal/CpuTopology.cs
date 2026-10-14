using System.Runtime.InteropServices;

namespace IndicOcr.Core.Internal;

/// <summary>Counts physical CPU cores (SMT/hyper-threads excluded). Measured: CPU inference gains nothing from SMT siblings.</summary>
internal static class CpuTopology
{
    static readonly Lazy<int> Cached = new(() =>
    {
        int logical = Math.Max(1, Environment.ProcessorCount); // honours affinity and container CPU limits
        int physical = 0;
        try
        {
            if (OperatingSystem.IsWindows()) physical = WindowsPhysicalCores();
            else if (OperatingSystem.IsLinux()) physical = LinuxPhysicalCores(
                "/sys/devices/system/cpu", "/proc/cpuinfo");
        }
        catch { physical = 0; }
        return physical > 0 ? Math.Min(physical, logical) : logical;
    });

    /// <summary>Physical core count available to this process (at least 1).</summary>
    public static int PhysicalCores => Cached.Value;

    // ---- Linux -------------------------------------------------------------------------------------------------

    /// <summary>Counts distinct (package, core) pairs from sysfs topology, else from /proc/cpuinfo; 0 when unknown.</summary>
    internal static int LinuxPhysicalCores(string sysCpuDir, string cpuInfoPath)
    {
        var set = new HashSet<string>();
        if (Directory.Exists(sysCpuDir))
        {
            foreach (var d in Directory.EnumerateDirectories(sysCpuDir, "cpu*"))
            {
                var name = Path.GetFileName(d);
                if (name.Length < 4 || !name.Skip(3).All(char.IsDigit)) continue;
                var online = Path.Combine(d, "online");
                if (File.Exists(online) && ReadTrim(online) == "0") continue;
                var core = Path.Combine(d, "topology", "core_id");
                if (!File.Exists(core)) continue;
                var pkgFile = Path.Combine(d, "topology", "physical_package_id");
                set.Add((File.Exists(pkgFile) ? ReadTrim(pkgFile) : "0") + ":" + ReadTrim(core));
            }
            if (set.Count > 0) return set.Count;
        }
        if (File.Exists(cpuInfoPath))
            return CountFromCpuInfo(File.ReadAllLines(cpuInfoPath));
        return 0;
    }

    internal static int CountFromCpuInfo(IEnumerable<string> lines)
    {
        var set = new HashSet<string>();
        string phys = "0", core = "";
        bool have = false;
        foreach (var l in lines)
        {
            if (l.Length == 0)
            {
                if (have && core.Length > 0) set.Add(phys + ":" + core);
                phys = "0"; core = ""; have = false;
                continue;
            }
            int c = l.IndexOf(':');
            if (c < 0) continue;
            var key = l[..c].Trim(); var val = l[(c + 1)..].Trim();
            if (key == "physical id") phys = val;
            else if (key == "core id") { core = val; have = true; }
        }
        if (have && core.Length > 0) set.Add(phys + ":" + core);
        return set.Count;
    }

    static string ReadTrim(string p) => File.ReadAllText(p).Trim();

    // ---- Windows -----------------------------------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int returnedLength);

    /// <summary>GetLogicalProcessorInformationEx(RelationProcessorCore): one variable-length record per physical core.</summary>
    static int WindowsPhysicalCores()
    {
        const int RelationProcessorCore = 0;
        int len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len); // fails with the needed size
        if (len <= 0) return 0;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return 0;
            int count = 0, off = 0;
            while (off + 8 <= len)
            {
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: int Relationship; int Size; ...
                int rel = Marshal.ReadInt32(buf, off);
                int size = Marshal.ReadInt32(buf, off + 4);
                if (size <= 0) break;
                if (rel == RelationProcessorCore) count++;
                off += size;
            }
            return count;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
