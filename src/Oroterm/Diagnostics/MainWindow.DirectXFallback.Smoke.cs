using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Oroterm.Controls;
using Windows.Graphics.DirectX.Direct3D11;

namespace Oroterm;

public sealed partial class MainWindow
{
    private async Task CheckDirectXLowFeatureFallback(Action<bool,string> check)
    {
        nint native=0,dxgi=0,inspectable=0;
        try
        {
            uint[] levels=[0x9300]; // Real WARP device constrained to feature level 9.3.
            DirectXRenderer.Check(LowFeatureDevice.Create(0,5,0,0x20,levels,1,7,out native,out uint level,0));
            check(level==levels[0],"An owned D3D11 WARP device exposes actual feature level 9.3 for fallback validation");
            var iid=new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            DirectXRenderer.Check(Marshal.QueryInterface(native,in iid,out dxgi));
            DirectXRenderer.Check(LowFeatureDevice.Wrap(dxgi,out inspectable));
            using var projected=WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            using var low=CanvasDevice.CreateFromDirect3D11Device(projected);
            bool unsupported=false;
            try { using var rejected=new DirectXRenderer(low); }
            catch(COMException ex) when(ex.HResult==unchecked((int)0x887a0004)){unsupported=true;}
            check(unsupported,"The native renderer rejects unsupported shader feature levels with DXGI_ERROR_UNSUPPORTED");
            using var canvas=new TerminalCanvasSurface {Width=64,Height=64,ClearColor=Microsoft.UI.Colors.Magenta};
            var presented=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            canvas.Presented+=()=>presented.TrySetResult();
            Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(canvas,4);Root.Children.Add(canvas);
            try
            {
                canvas.CustomDevice=low;
                await presented.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(!ReferenceEquals(canvas.Device,low) && canvas.Renderer.GetStats().FeatureLevel>=0xa000,
                    "The actual terminal surface selects a supported shared WARP provider after low-feature initialization fails");
                using var target=new CanvasRenderTarget(canvas.Device,4,4,96);
                using(var draw=canvas.Renderer.CreateDrawingSession(target))draw.Clear(Microsoft.UI.Colors.Magenta);
                var pixels=target.GetPixelBytes();
                check(pixels[0]==255 && pixels[1]==0 && pixels[2]==255 && pixels[3]==255,
                    "The fallback terminal surface draws verified native GPU pixels and accepts a presentation");
            }
            finally {Root.Children.Remove(canvas);}
        }
        finally
        {
            if(inspectable!=0)Marshal.Release(inspectable);
            if(dxgi!=0)Marshal.Release(dxgi);
            if(native!=0)Marshal.Release(native);
        }
    }
    private static class LowFeatureDevice
    {
        [DllImport("d3d11.dll",EntryPoint="D3D11CreateDevice")]
        internal static extern int Create(nint adapter,uint driver,nint software,uint flags,uint[] levels,uint count,uint sdk,out nint device,out uint feature,nint context);
        [DllImport("d3d11.dll",EntryPoint="CreateDirect3D11DeviceFromDXGIDevice")]
        internal static extern int Wrap(nint dxgi,out nint inspectable);
    }
}
