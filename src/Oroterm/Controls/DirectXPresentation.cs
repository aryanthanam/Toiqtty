using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace Oroterm.Controls;

/// <summary>Pane-owned composition objects. Mode changes select a retained surface;
/// they do not create another projection waiting for a managed collection.</summary>
internal sealed unsafe class DirectXPresentation(DirectXRenderer.RendererHandle renderer) : IDisposable
{
    private nint _swapchain;
    private FrameworkElement? _host;
    private SpriteVisual? _visual;
    private CompositionSurfaceBrush? _brush;
    private ICompositionSurface? _swapchainSurface;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _drawingSurface;
    private bool _drawingMode;
    private uint _width,_height,_surfaceWidth,_surfaceHeight;
    internal uint SurfaceCreations { get; private set; }
    internal ulong TextureBytes => _drawingSurface is null?0:(ulong)_surfaceWidth*_surfaceHeight*8;
    internal CompositionDrawingSurface? ActiveDrawingSurface => _drawingMode?_drawingSurface:null;

    internal void Resize(uint width,uint height)
    {
        _width=width;_height=height;
    }

    internal void Attach(FrameworkElement panel,bool drawingMode)
    {
        // Retain an inactive transparency surface at its last size. Bring it
        // current only when selected, including a same-mode size-only update.
        if(drawingMode && _drawingSurface is { } surface && (_surfaceWidth!=_width || _surfaceHeight!=_height))
        {
            DirectXRenderer.Check(DirectXRenderer.Native.ResizeSurface(((WinRT.IWinRTObject)surface).NativeObject.ThisPtr,_width,_height));
            _surfaceWidth=_width;_surfaceHeight=_height;
        }
        if(ReferenceEquals(panel,_host) && drawingMode==_drawingMode)return;
        var compositor=ElementCompositionPreview.GetElementVisual(panel).Compositor;
        ICompositionSurface projected=drawingMode?DrawingSurface(compositor):SwapchainSurface(compositor);
        _brush??=compositor.CreateSurfaceBrush();_brush.Stretch=CompositionStretch.Fill;_brush.Surface=projected;
        _visual??=compositor.CreateSpriteVisual();_visual.RelativeSizeAdjustment=Vector2.One;
        _visual.Brush=_brush;_visual.CompositeMode=CompositionCompositeMode.SourceOver;
        if(!ReferenceEquals(panel,_host))
        {
            if(_host is { } previous)Detach(previous);
            ElementCompositionPreview.SetElementChildVisual(panel,_visual);
        }
        _host=panel;_drawingMode=drawingMode;
    }

    private CompositionDrawingSurface DrawingSurface(Compositor compositor)
    {
        if(_drawingSurface is not null)return _drawingSurface;
        if(_graphicsDevice is null)
        {
            var iid=new Guid("FAB19398-6D19-4D8A-B752-8F096C396069");nint interop=0,graphics=0,device=0;
            try
            {
                DirectXRenderer.Check(Marshal.QueryInterface(((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr,in iid,out interop));
                DirectXRenderer.Check(DirectXRenderer.Native.Device(renderer,out device));
                var create=(delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)(*(nint**)interop)[3];
                DirectXRenderer.Check(create(interop,device,&graphics));
                _graphicsDevice=WinRT.MarshalInspectable<CompositionGraphicsDevice>.FromAbi(graphics);
            }
            finally{if(graphics!=0)Marshal.Release(graphics);if(device!=0)Marshal.Release(device);if(interop!=0)Marshal.Release(interop);GC.KeepAlive(compositor);}
        }
        _drawingSurface=_graphicsDevice.CreateDrawingSurface(new Size(_width,_height),Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        _surfaceWidth=_width;_surfaceHeight=_height;
        SurfaceCreations++;return _drawingSurface;
    }

    private ICompositionSurface SwapchainSurface(Compositor compositor)
    {
        if(_swapchainSurface is not null)return _swapchainSurface;
        var iid=new Guid("FC084699-67D8-40E1-ADE7-08901D84FFDA");nint interop=0,surface=0,swapchain=0;
        try
        {
            DirectXRenderer.Check(DirectXRenderer.Native.Swapchain(renderer,out swapchain));
            DirectXRenderer.Check(Marshal.QueryInterface(((WinRT.IWinRTObject)compositor).NativeObject.ThisPtr,in iid,out interop));
            var create=(delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)(*(nint**)interop)[5];
            DirectXRenderer.Check(create(interop,swapchain,&surface));
            _swapchainSurface=WinRT.MarshalInterface<ICompositionSurface>.FromAbi(surface);
            // Keep COM identity for as long as its cached WinRT surface lives.
            _swapchain=swapchain;swapchain=0;SurfaceCreations++;return _swapchainSurface;
        }
        finally{if(surface!=0)Marshal.Release(surface);if(interop!=0)Marshal.Release(interop);if(swapchain!=0)Marshal.Release(swapchain);GC.KeepAlive(compositor);}
    }

    internal static void Detach(FrameworkElement panel) => ElementCompositionPreview.SetElementChildVisual(panel,null);

    public void Dispose()
    {
        if(_host is { } host)Detach(host);_host=null;
        if(_brush is { } brush)brush.Surface=null;
        if(_visual is { } visual)visual.Brush=null;
        _visual?.Dispose();_visual=null;_brush?.Dispose();_brush=null;
        _drawingSurface?.Dispose();_drawingSurface=null;
        // Swapchain surfaces expose no IClosable. Preserve the C#/WinRT identity
        // cache contract; never manually disconnect their projection.
        (_swapchainSurface as IDisposable)?.Dispose();_swapchainSurface=null;
        _graphicsDevice?.Dispose();_graphicsDevice=null;
        if(_swapchain!=0){Marshal.Release(_swapchain);_swapchain=0;}
        _drawingMode=false;
    }
}
