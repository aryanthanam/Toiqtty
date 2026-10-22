using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Toiqtty.Hosting;

/// <summary>Rejects a named-pipe server running below this process's integrity level.</summary>
/// <remarks>Pipe names are derived from the data folder, so a sandboxed low-integrity process
/// of the same user could create one first. CurrentUserOnly only checks the pipe owner, which
/// such a process satisfies. Servers also create their first instance with FirstPipeInstance.
/// Verified server process IDs are cached, so repeated connections cost one system call.</remarks>
internal static partial class PipeServerTrust
{
    private static readonly ConcurrentDictionary<uint, byte> Trusted = new();
    private static readonly Lazy<uint> Own = new(() => IntegrityOf(GetCurrentProcess(), owned: false) ?? uint.MaxValue);

    internal static void Verify(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint server)) throw new IOException("Could not identify the pipe server.", new Win32Exception());
        if (Trusted.ContainsKey(server)) return;
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, server);
        // A server this process cannot query runs at a higher integrity level or is protected.
        if (process != 0 && IntegrityOf(process, owned: true) is { } level && level < Own.Value)
            throw new IOException("A lower-integrity process owns this toiqtty pipe; refusing to connect.");
        if (Trusted.Count > 64) Trusted.Clear();
        Trusted.TryAdd(server, 0);
    }

    private static uint? IntegrityOf(nint process, bool owned)
    {
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out nint token)) return null;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out int size);
                if (size <= 0) return null;
                nint buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return null;
                    nint sid = Marshal.ReadIntPtr(buffer);
                    byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return count == 0 ? null : (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        finally { if (owned) CloseHandle(process); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x8;
    private const int TokenIntegrityLevel = 25;
    [LibraryImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [LibraryImport("kernel32.dll")] private static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint handle);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);
    [LibraryImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, nint information, int length, out int returned);
    [LibraryImport("advapi32.dll")] private static partial nint GetSidSubAuthorityCount(nint sid);
    [LibraryImport("advapi32.dll")] private static partial nint GetSidSubAuthority(nint sid, uint index);
}
