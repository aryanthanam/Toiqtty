using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Text;

namespace Oroterm.Controls;

/// <summary>Reads DirectWrite's original cluster metrics into a renderer-owned buffer.</summary>
internal sealed unsafe class TerminalClusterMetrics
{
    // Microsoft.Graphics.Canvas.native.h: ICanvasResourceWrapperNative.
    private static readonly Guid ResourceWrapperId=new("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A");
    private static readonly Guid TextLayoutId=new("53737037-6D14-410B-9BFE-0B182BB70961");
    [StructLayout(LayoutKind.Sequential)]
    internal struct Cluster
    {
        public float Width;
        public ushort CharacterCount;
        public ushort Properties;
    }
    private Cluster[] _buffer=new Cluster[256];
    internal bool UseProjectedMetricsForCheck { get; set; }
    internal int NativeReads { get; private set; }
    internal int RetainedCapacityForCheck=>_buffer.Length;
    internal ReadOnlySpan<Cluster> Read(CanvasTextLayout layout,int textLength)
    {
        var storage=_buffer;
        if(storage.Length<textLength)
        {
            storage=new Cluster[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)textLength)];
            if(storage.Length<=8192) _buffer=storage;
        }
        if(UseProjectedMetricsForCheck)
        {
            var projected=layout.ClusterMetrics;
            for(int i=0;i<projected.Length;i++)
                storage[i]=new() { Width=projected[i].Width,CharacterCount=checked((ushort)projected[i].CharacterCount) };
            return storage.AsSpan(0,projected.Length);
        }
        nint wrapper=0,native=0;
        try
        {
            nint inspectable=((WinRT.IWinRTObject)layout).NativeObject.ThisPtr;
            var wrapperId=ResourceWrapperId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable,in wrapperId,out wrapper));
            var layoutId=TextLayoutId;
            var getNative=(delegate* unmanaged[Stdcall]<nint,nint,float,Guid*,nint*,int>)(*(nint**)wrapper)[3];
            Marshal.ThrowExceptionForHR(getNative(wrapper,0,0,&layoutId,&native));
            // IUnknown(3) + IDWriteTextFormat(25) + GetClusterMetrics(34).
            // This ABI is defined by the Windows SDK's dwrite.h.
            var getMetrics=(delegate* unmanaged[Stdcall]<nint,Cluster*,uint,uint*,int>)(*(nint**)native)[62];
            uint count=0;
            fixed(Cluster* buffer=storage)
                Marshal.ThrowExceptionForHR(getMetrics(native,buffer,(uint)storage.Length,&count));
            if(count>textLength) throw new InvalidOperationException("DirectWrite returned an invalid cluster count.");
            NativeReads++;
            return storage.AsSpan(0,(int)count);
        }
        finally
        {
            if(native!=0) Marshal.Release(native);
            if(wrapper!=0) Marshal.Release(wrapper);
            GC.KeepAlive(layout);
        }
    }
}
