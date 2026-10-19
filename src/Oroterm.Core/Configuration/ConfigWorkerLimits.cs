using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Oroterm.Configuration;

internal sealed class ConfigWorkerLimits : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Counters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits
    {
        public BasicLimits Basic; public Counters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref Limits limits, uint length);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
    private readonly SafeFileHandle _job;
    private ConfigWorkerLimits(SafeFileHandle job) => _job = job;
    public static ConfigWorkerLimits Attach(Process process)
    {
        var job = CreateJobObjectW(0, null);
        var limits = new Limits { Basic = new BasicLimits { Flags = 0x100 | 0x2000 }, ProcessMemory = 256 * 1024 * 1024 };
        if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Limits>()) || !AssignProcessToJobObject(job, process.Handle))
        { int error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error, "Could not constrain the Lua configuration worker."); }
        return new(job);
    }
    public void Dispose() => _job.Dispose();
}
