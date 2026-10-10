using System.Runtime.InteropServices;

namespace Sidetrack.Core.Capture;

/// <summary>Finds the root process of an app so process loopback covers its whole tree.</summary>
public static class ProcessTree
{
    /// <summary>
    /// The oldest-listed process named <paramref name="exeName"/> whose parent
    /// is not also <paramref name="exeName"/> (Discord, browsers and similar
    /// apps play audio from a child process). Null when the app is not running.
    /// </summary>
    public static uint? FindRoot(string exeName)
    {
        var all = Snapshot();
        var matches = all.Where(p => string.Equals(p.Exe, exeName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return null;
        var ids = matches.Select(p => p.Pid).ToHashSet();
        var root = matches.FirstOrDefault(p => !ids.Contains(p.ParentPid));
        return root.Pid != 0 ? root.Pid : matches[0].Pid;
    }

    public static bool IsAlive(uint pid) => Snapshot().Any(p => p.Pid == pid);

    private static List<(uint Pid, uint ParentPid, string Exe)> Snapshot()
    {
        var list = new List<(uint, uint, string)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                list.Add((entry.ProcessId, entry.ParentProcessId, entry.ExeFile));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
