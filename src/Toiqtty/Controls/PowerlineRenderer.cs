using Microsoft.Graphics.Canvas;
using Windows.UI;

namespace Toiqtty.Controls;

/// <summary>Filled native GPU separators with exact flat cell edges.</summary>
internal sealed class PowerlineRenderer(ICanvasResourceCreator resource,float width,float height) : IDisposable
{
    public bool TryDraw(DirectXDrawingSession drawing,string text,float x,float y,Color color)
    {
        if(text.Length!=1 || text[0] is not ('\ue0b0' or '\ue0b2' or '\ue0b4' or '\ue0b6'))return false;
        int shape=text[0] switch{'\ue0b0'=>1,'\ue0b2'=>2,'\ue0b4'=>3,_=>4};
        drawing.FillPowerline(x,y,width,height,color,shape);
        GC.KeepAlive(resource);
        return true;
    }
    public void Dispose() { }
}
