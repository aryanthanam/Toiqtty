# Own the initial suspended thread only long enough for ETW to subscribe. The
# caller owns the Process wrapper and its normal window/fixture shutdown.
function New-ToiqttySuspendedTestProcess([Diagnostics.ProcessStartInfo]$StartInfo) {
    if(!('ToiqttySuspendedTestProcess' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public sealed class ToiqttySuspendedTestProcess : IDisposable {
    [StructLayout(LayoutKind.Sequential)] private struct Startup {
        public uint Size; public IntPtr Reserved,Desktop,Title;
        public uint X,Y,Width,Height,XCharacters,YCharacters,Fill,Flags;
        public ushort ShowWindow,ReservedBytes; public IntPtr ReservedData,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Created {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out Created created);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process,uint exitCode);
    private readonly SafeProcessHandle processHandle;
    private readonly SafeWaitHandle threadHandle;
    private bool resumed,disposed;
    public Process Process {get;}
    private static string Quote(string text) {
        if(text.Length>0 && !text.Any(c=>char.IsWhiteSpace(c) || c=='"'))return text;
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char value in text) {
            if(value=='\\'){slashes++;continue;}
            if(value=='"')result.Append('\\',slashes*2+1);else result.Append('\\',slashes);
            result.Append(value);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
    public ToiqttySuspendedTestProcess(ProcessStartInfo start) {
        if(start.UseShellExecute || start.RedirectStandardInput || start.RedirectStandardOutput || start.RedirectStandardError)
            throw new ArgumentException("The suspended GUI fixture uses direct process creation without redirected console handles.");
        var command=new StringBuilder(Quote(start.FileName));foreach(string argument in start.ArgumentList)command.Append(' ').Append(Quote(argument));
        string block=string.Join("\0",start.Environment.OrderBy(pair=>pair.Key,StringComparer.OrdinalIgnoreCase).Select(pair=>pair.Key+"="+pair.Value))+"\0\0";
        IntPtr environment=Marshal.StringToHGlobalUni(block);Created created;
        try {
            var startup=new Startup{Size=(uint)Marshal.SizeOf<Startup>()};
            if(!CreateProcessW(start.FileName,command,IntPtr.Zero,IntPtr.Zero,false,0x404,environment,start.WorkingDirectory,ref startup,out created))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally {Marshal.FreeHGlobal(environment);}
        processHandle=new SafeProcessHandle(created.Process,true);threadHandle=new SafeWaitHandle(created.Thread,true);
        try {Process=System.Diagnostics.Process.GetProcessById((int)created.ProcessId);_ = Process.Handle;}
        catch {TerminateProcess(processHandle,1);threadHandle.Dispose();processHandle.Dispose();throw;}
    }
    public void Resume() {
        if(disposed)throw new ObjectDisposedException(nameof(ToiqttySuspendedTestProcess));
        if(resumed)throw new InvalidOperationException("The fixture was already resumed.");
        if(ResumeThread(threadHandle)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());
        resumed=true;threadHandle.Dispose();
    }
    public void Dispose() {
        if(disposed)return;disposed=true;
        try {if(!resumed)TerminateProcess(processHandle,1);}
        finally {threadHandle.Dispose();processHandle.Dispose();}
    }
}
'@
    }
    return [ToiqttySuspendedTestProcess]::new($StartInfo)
}
