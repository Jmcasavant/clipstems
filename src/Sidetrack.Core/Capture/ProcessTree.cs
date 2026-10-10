using System.Runtime.InteropServices;
using System.Text;

namespace Sidetrack.Core.Capture;

/// <summary>Finds the root process of an app so process loopback covers its whole tree.</summary>
public static class ProcessTree
{
    public readonly record struct ProcessEntry(uint Pid, uint ParentPid, string Exe);

    /// <summary>
    /// The root process of the first app in <paramref name="exeNames"/> that is
    /// running: a process with that name whose parent is not also that app
    /// (Discord, browsers and similar apps play audio from a child process).
    /// Null when none is running.
    /// </summary>
    public static (uint Pid, string Exe)? FindRoot(IEnumerable<string> exeNames)
    {
        var all = Snapshot();
        foreach (var exe in exeNames)
        {
            var matches = all.Where(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 0) return (Root(matches), matches[0].Exe);
        }
        return null;
    }

    /// <summary>
    /// The root of the processes whose image lives under
    /// <paramref name="directory"/>: how the running Steam game is found
    /// without guessing its exe name. Launchers inside the folder count as
    /// part of the game, which process loopback's tree mode wants anyway.
    /// </summary>
    public static (uint Pid, string Exe)? FindRootUnder(string directory)
    {
        var prefix = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        var matches = Snapshot()
            .Where(p => p.Pid > 4 && ImagePath(p.Pid) is { } path && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return null;
        uint root = Root(matches);
        return (root, matches.First(m => m.Pid == root).Exe);
    }

    public static bool IsAlive(uint pid) => Snapshot().Any(p => p.Pid == pid);

    /// <summary>Full image path of a process, or null when it cannot be queried (exited, protected).</summary>
    public static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static List<ProcessEntry> Snapshot()
    {
        var list = new List<ProcessEntry>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (bool ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
                list.Add(new ProcessEntry(entry.ProcessId, entry.ParentProcessId, entry.ExeFile));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    private static uint Root(List<ProcessEntry> matches)
    {
        var ids = matches.Select(p => p.Pid).ToHashSet();
        var root = matches.FirstOrDefault(p => !ids.Contains(p.ParentPid));
        return root.Pid != 0 ? root.Pid : matches[0].Pid;
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
