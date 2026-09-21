using System.Runtime.InteropServices;

namespace core.Helpers;

public class NativeProcessSnapshot
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public long WorkingSetBytes { get; set; }
    public TimeSpan CpuTime { get; set; }
    public DateTime? StartTime { get; set; }
}

public static partial class NativeProcessManager
{
    private const uint SystemProcessInformation = 5;
    private const uint STATUS_SUCCESS = 0x00000000;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    [LibraryImport("ntdll.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint NtQuerySystemInformation(
        uint SystemInformationClass,
        IntPtr SystemInformation,
        uint SystemInformationLength,
        out uint ReturnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        private long SpareLi1;
        private long SpareLi2;
        private long SpareLi3;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public UIntPtr PageDirectoryBase;
        public UIntPtr PeakVirtualSize;
        public UIntPtr VirtualSize;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivatePageCount;
    }

    public static List<NativeProcessSnapshot> GetAllProcesses()
    {
        var result = new List<NativeProcessSnapshot>();
        uint bufferSize = 512 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal((int) bufferSize);

        try
        {
            uint status;
            while ((status = NtQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, out uint returnLength)) == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(buffer);
                bufferSize = Math.Max(bufferSize * 2, returnLength + 32768);
                buffer = Marshal.AllocHGlobal((int) bufferSize);
            }

            if (status != STATUS_SUCCESS)
                return result;

            IntPtr currentPtr = buffer;

            while (true)
            {
                var pi = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(currentPtr);
                int pid = pi.UniqueProcessId.ToInt32();
                string name;

                if (pi.ImageName.Buffer != IntPtr.Zero && pi.ImageName.Length > 0)
                {
                    name = Marshal.PtrToStringUni(pi.ImageName.Buffer, pi.ImageName.Length / sizeof(char)) ?? "Unknown";
                }
                else
                {
                    name = pid == 0 ? "System Idle Process" : (pid == 4 ? "System" : "Unknown");
                }

                DateTime? startTime = null;
                if (pi.CreateTime > 0)
                {
                    try
                    { startTime = DateTime.FromFileTimeUtc(pi.CreateTime).ToLocalTime(); }
                    catch { }
                }

                var cpuTime = TimeSpan.FromTicks(pi.UserTime + pi.KernelTime);

                result.Add(new NativeProcessSnapshot
                {
                    Id = pid,
                    Name = name,
                    WorkingSetBytes = (long) pi.WorkingSetSize.ToUInt64(),
                    CpuTime = cpuTime,
                    StartTime = startTime
                });

                if (pi.NextEntryOffset == 0)
                    break;

                currentPtr = IntPtr.Add(currentPtr, (int) pi.NextEntryOffset);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }
}