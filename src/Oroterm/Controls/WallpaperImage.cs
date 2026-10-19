using Microsoft.Graphics.Canvas;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Foundation;

namespace Oroterm.Controls;

// One decoded image per window, shared by all panes. GPU copies are per device.
internal sealed class WallpaperImage(string path) : IDisposable
{
    private readonly Dictionary<CanvasDevice,DirectXBitmap> _bitmaps=[];
    private byte[]? _pixels;
    private uint _width,_height;
    private bool _disposed;
    internal event Action? Ready;
    internal event Action<string>? Failed;
    internal bool ReadyForCheck=>_pixels is not null;
    internal int DeviceCountForCheck=>_bitmaps.Count;
    internal async Task Load()
    {
        try
        {
            await Task.Delay(100); if(_disposed) return; // Coalesce rapid cycling before decoding.
            var file=await StorageFile.GetFileFromPathAsync(path);
            var properties=await file.GetBasicPropertiesAsync();
            if(properties.Size>32*1024*1024) throw new IOException("Wallpaper file exceeds 32 MiB.");
            using var stream=await file.OpenReadAsync();
            if(stream.Size>32*1024*1024) throw new IOException("Wallpaper stream exceeds 32 MiB.");
            var decoder=await BitmapDecoder.CreateAsync(stream);
            if(_disposed) return;
            if((ulong)decoder.PixelWidth*decoder.PixelHeight>16*1024*1024 || decoder.PixelWidth==0 || decoder.PixelHeight==0) throw new IOException("Wallpaper exceeds 16 megapixels.");
            var data=await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,new BitmapTransform(),ExifOrientationMode.RespectExifOrientation,ColorManagementMode.ColorManageToSRgb);
            if(_disposed) return;
            _pixels=data.DetachPixelData(); _width=decoder.OrientedPixelWidth; _height=decoder.OrientedPixelHeight; Ready?.Invoke();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        { if(!_disposed) Failed?.Invoke("Could not load wallpaper: "+ex.Message); }
    }
    internal void Draw(DirectXRenderer renderer,CanvasDevice device,DirectXDrawingSession drawing,float width,float height,string fit,float opacity)
    {
        if(_disposed || _pixels is null || opacity<=0) return;
        if(!_bitmaps.TryGetValue(device,out var bitmap))
        {
            if(_bitmaps.Count>=2) ForgetDevice(_bitmaps.Keys.First());
            bitmap=renderer.CreateBitmap(_pixels,(int)_width,(int)_height);
            _bitmaps.Add(device,bitmap);
        }
        var source=new Rect(0,0,_width,_height); var destination=new Rect(0,0,width,height);
        if(fit=="cover")
        {
            double scale=Math.Max(width/_width,height/_height); double w=width/scale,h=height/scale;
            source=new Rect((_width-w)/2,(_height-h)/2,w,h);
        }
        else if(fit=="contain")
        {
            double scale=Math.Min(width/_width,height/_height),w=_width*scale,h=_height*scale;
            destination=new Rect((width-w)/2,(height-h)/2,w,h);
        }
        drawing.DrawImage(bitmap,destination,source,opacity,CanvasImageInterpolation.Linear);
    }
    internal void ForgetDevice(CanvasDevice device) { if(_bitmaps.Remove(device,out var bitmap)) bitmap.Dispose(); }
    public void Dispose() { if(_disposed) return; _disposed=true; _pixels=null; foreach(var bitmap in _bitmaps.Values) bitmap.Dispose(); _bitmaps.Clear(); }
}
