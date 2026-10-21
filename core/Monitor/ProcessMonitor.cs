using core.Helpers;
using core.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace core.Monitor;

public partial class ProcessMonitor
{
    private volatile List<ProcessInfo> _cache = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Dictionary<int, (TimeSpan Cpu, TimeSpan Timestamp, DateTime StartTime)> _prev = [];
    private static readonly Dictionary<int, DateTime> _suspendedProcesses = [];
    private static readonly Dictionary<(int Pid, DateTime StartTime, int ThreadId), int> _ownedSuspendCounts = [];
    private static readonly Lock _suspendLock = new();

    [Flags]
    private enum ThreadAccess : int
    {
        SUSPEND_RESUME = 0x0002
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenThread(ThreadAccess dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwThreadId);

    [LibraryImport("kernel32.dll")]
    private static partial uint SuspendThread(IntPtr hThread);

    [LibraryImport("kernel32.dll")]
    private static partial int ResumeThread(IntPtr hThread);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    public static bool IsSuspended(int pid, DateTime startTime)
    {
        lock (_suspendLock)
        {
            return _suspendedProcesses.TryGetValue(pid, out var suspendedStart)
                && suspendedStart == startTime;
        }
    }

    public static bool Suspend(int pid, DateTime startTime)
    {
        lock (_suspendLock)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.StartTime != startTime)
                    return false;

                var suspendedThreads = new List<int>();
                foreach (ProcessThread thread in process.Threads)
                {
                    IntPtr hThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint) thread.Id);

                    if (hThread == IntPtr.Zero)
                    {
                        RollbackSuspends(pid, startTime, suspendedThreads);
                        return false;
                    }
                    try
                    {
                        uint previousCount = SuspendThread(hThread);
                        if (previousCount == unchecked((uint) -1))
                        {
                            RollbackSuspends(pid, startTime, suspendedThreads);
                            return false;
                        }
                        suspendedThreads.Add(thread.Id);
                    }
                    finally
                    {
                        CloseHandle(hThread);
                    }
                }

                if (suspendedThreads.Count == 0)
                    return false;

                foreach (int threadId in suspendedThreads)
                {
                    var key = (pid, startTime, threadId);

                    _ownedSuspendCounts.TryGetValue(key, out int count);
                    _ownedSuspendCounts[key] = count + 1;
                }
                _suspendedProcesses[pid] = startTime;

                return true;
            }
            catch { return false; }
        }
    }

    public static bool Resume(int pid)
    {
        lock (_suspendLock)
        {
            try
            {
                if (!_suspendedProcesses.TryGetValue(
                        pid,
                        out var startTime))
                {
                    return false;
                }

                using var process = Process.GetProcessById(pid);
                if (process.StartTime != startTime)
                {
                    RemoveOwnedSuspendState(pid, startTime);
                    return false;
                }

                var ownedThreads = _ownedSuspendCounts
                    .Where(x =>
                        x.Key.Pid == pid &&
                        x.Key.StartTime == startTime &&
                        x.Value > 0)
                    .Select(x => (x.Key.ThreadId, Count: x.Value))
                    .ToList();

                if (ownedThreads.Count == 0)
                {
                    _suspendedProcesses.Remove(pid);
                    return false;
                }

                bool allSuccessful = true;

                foreach (var (ThreadId, Count) in ownedThreads)
                {
                    IntPtr hThread = OpenThread(
                        ThreadAccess.SUSPEND_RESUME,
                        false,
                        (uint) ThreadId);

                    if (hThread == IntPtr.Zero)
                    {
                        _ownedSuspendCounts.Remove((pid, startTime, ThreadId));
                        allSuccessful = false;
                        continue;
                    }

                    try
                    {
                        int remaining = Count;

                        while (remaining > 0)
                        {
                            int previousCount = ResumeThread(hThread);

                            // -1 = failure.
                            if (previousCount == -1)
                            {
                                allSuccessful = false;
                                break;
                            }

                            // 0 = thread was not suspended.
                            if (previousCount == 0)
                            {
                                allSuccessful = false;
                                remaining = 0;
                                break;
                            }

                            // Exactly one suspend count owned by this
                            // monitor has been removed.
                            remaining--;
                        }

                        var key = (pid, startTime, ThreadId);
                        if (remaining == 0)
                        {
                            _ownedSuspendCounts.Remove(key);
                        }
                        else
                        {
                            _ownedSuspendCounts[key] = remaining;
                        }
                    }
                    finally
                    {
                        CloseHandle(hThread);
                    }
                }

                bool stillOwned = _ownedSuspendCounts.Any(x => x.Key.Pid == pid && x.Key.StartTime == startTime && x.Value > 0);
                if (!stillOwned)
                    _suspendedProcesses.Remove(pid);

                return allSuccessful && !stillOwned;
            }
            catch
            {
                return false;
            }
        }
    }

    private static void RollbackSuspends(int pid, DateTime startTime, List<int> suspendedThreads)
    {
        foreach (int threadId in suspendedThreads)
        {
            IntPtr hThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint) threadId);
            if (hThread == IntPtr.Zero)
                continue;

            try
            {
                int previousCount = ResumeThread(hThread);
                if (previousCount > 0)
                {
                    var key = (pid, startTime, threadId);
                    _ownedSuspendCounts.TryGetValue(key, out int count);
                    if (count <= 1)
                        _ownedSuspendCounts.Remove(key);
                    else
                        _ownedSuspendCounts[key] = count - 1;
                }
            }
            finally { CloseHandle(hThread); }
        }
    }

    private static void RemoveOwnedSuspendState(int pid, DateTime startTime)
    {
        _suspendedProcesses.Remove(pid);
        foreach (var key in _ownedSuspendCounts.Keys
                    .Where(x => x.Pid == pid && x.StartTime == startTime)
                    .ToList())
        {
            _ownedSuspendCounts.Remove(key);
        }
    }

    public static void TogglePause(int pid)
    {
        if (pid == Environment.ProcessId)
            return;

        try
        {
            using var process = Process.GetProcessById(pid);
            var startTime = process.StartTime;

            if (IsSuspended(pid, startTime))
                Resume(pid);
            else
                Suspend(pid, startTime);
        }
        catch { }
    }

    public void Refresh()
    {
        var now = _clock.Elapsed;
        var procs = NativeProcessManager.GetAllProcesses();
        var list = new List<ProcessInfo>(procs.Count);
        var current = new Dictionary<int, (TimeSpan, TimeSpan, DateTime)>();
        var activePids = new HashSet<int>(procs.Count);

        foreach (var p in procs)
            activePids.Add(p.Id);

        lock (_suspendLock)
        {
            foreach (var key in _suspendedProcesses.Keys.ToList())
            {
                if (!activePids.Contains(key))
                    _suspendedProcesses.Remove(key);
            }

            foreach (var key in _ownedSuspendCounts.Keys.ToList())
            {
                if (!activePids.Contains(key.Pid))
                    _ownedSuspendCounts.Remove(key);
            }
        }

        foreach (var p in procs)
        {
            double cpuPercent = 0;
            bool isSuspended = false;

            if (p.StartTime is DateTime validStart)
            {
                isSuspended = IsSuspended(p.Id, validStart);

                if (_prev.TryGetValue(p.Id, out var old) && old.StartTime == validStart)
                {
                    double elapsedMs = (now - old.Timestamp).TotalMilliseconds;
                    double usedMs = (p.CpuTime - old.Cpu).TotalMilliseconds;

                    if (elapsedMs > 0 && usedMs >= 0)
                    {
                        double raw = usedMs / (Environment.ProcessorCount * elapsedMs) * 100;
                        if (!double.IsNaN(raw) && !double.IsInfinity(raw) && raw >= 0)
                        {
                            cpuPercent = raw;
                        }
                    }
                }

                current[p.Id] = (p.CpuTime, now, validStart);
            }

            list.Add(new ProcessInfo(
                Id: p.Id,
                Name: p.Name,
                MemoryUsage: p.WorkingSetBytes / 1_048_576.0,
                CpuUsage: cpuPercent,
                IsSuspended: isSuspended
            ));
        }

        _prev = current;
        _cache = list;
    }

    public List<ProcessInfo> GetFiltered(string query, SortMode sort)
    {
        var snapshot = _cache;

        IEnumerable<ProcessInfo> result = string.IsNullOrEmpty(query)
                ? snapshot
                : snapshot.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        return sort switch
        {
            SortMode.NameAsc => result.OrderBy(p => p.Name).ToList(),
            SortMode.MemoryDesc => result.OrderByDescending(p => p.MemoryUsage).ToList(),
            SortMode.CpuDesc => result.OrderByDescending(p => p.CpuUsage).ToList(),
            _ => result.ToList()
        };
    }

    // Volatile snapshot of the last Refresh (list is replaced, never mutated).
    public List<ProcessInfo> Snapshot() => _cache;

    public static void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch { }
    }

    public static void KillAllByName(string name)
    {
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            { p.Kill(entireProcessTree: true); }
            catch { }
            finally { p.Dispose(); }
        }
    }
}