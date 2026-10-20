using Microsoft.Graphics.Canvas;
using Toiqtty.Terminal;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Toiqtty.Controls;

/// <summary>GPU textures for Ghostty placements; pixels are copied under the session lock.</summary>
internal sealed class TerminalImageRenderer : IDisposable
{
    private readonly Dictionary<ulong, DirectXBitmap> _textures = [];
    public int TextureCount => _textures.Count;
    internal long RetainedGpuBytes
    {
        get { long bytes=0; foreach(var texture in _textures.Values) bytes+=(long)texture.SizeInPixels.Width*(long)texture.SizeInPixels.Height*4; return bytes; }
    }

    public void Update(DirectXRenderer resource, IReadOnlyList<TerminalGraphic> graphics)
    {
        var used = graphics.Select(g => g.Image.Generation).ToHashSet();
        foreach (var generation in _textures.Keys.Where(key => !used.Contains(key)).ToArray())
        { _textures[generation].Dispose(); _textures.Remove(generation); }
        foreach (var graphic in graphics)
        {
            var image = graphic.Image;
            if (_textures.ContainsKey(image.Generation)) continue;
            int width = checked((int)image.Width), height = checked((int)image.Height), channels = checked((int)image.Channels);
            var bgra = new byte[checked(width * height * 4)];
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                int source = pixel * channels, destination = pixel * 4;
                byte r = image.Pixels[source], g = channels >= 3 ? image.Pixels[source + 1] : r;
                byte b = channels >= 3 ? image.Pixels[source + 2] : r;
                byte a = channels is 2 or 4 ? image.Pixels[source + channels - 1] : (byte)255;
                bgra[destination] = (byte)((b * a + 127) / 255);
                bgra[destination + 1] = (byte)((g * a + 127) / 255);
                bgra[destination + 2] = (byte)((r * a + 127) / 255);
                bgra[destination + 3] = a;
            }
            _textures.Add(image.Generation, resource.CreateBitmap(bgra,width,height));
        }
    }
    public void Draw(DirectXDrawingSession drawing, TerminalSnapshot snapshot, float cellWidth, float cellHeight, int layer)
    {
        float scaleX = cellWidth / snapshot.Frame.CellWidth, scaleY = cellHeight / snapshot.Frame.CellHeight;
        foreach (var graphic in snapshot.Graphics)
        {
            int graphicLayer = graphic.Z < int.MinValue / 2 ? 0 : graphic.Z < 0 ? 1 : 2;
            if (graphicLayer != layer || !_textures.TryGetValue(graphic.Image.Generation, out var texture)) continue;
            var destination = new Rect(graphic.Column * cellWidth + graphic.OffsetX * scaleX,
                graphic.Row * cellHeight + graphic.OffsetY * scaleY, graphic.PixelWidth * scaleX, graphic.PixelHeight * scaleY);
            var source = new Rect(graphic.SourceX, graphic.SourceY, graphic.SourceWidth, graphic.SourceHeight);
            drawing.DrawImage(texture, destination, source, 1, CanvasImageInterpolation.Linear);
        }
    }
    public void Dispose() { foreach (var texture in _textures.Values) texture.Dispose(); _textures.Clear(); }
}
