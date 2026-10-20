using System.Text;
using Toiqtty.Models;
using Toiqtty.Terminal;

internal static class SelectionCoordinateChecks
{
    internal static void Run(Action<bool,string> check)
    {
        foreach (double scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 })
        foreach (var pixels in new[] { (8, 21), (12, 37), (17, 34), (24, 40) })
        {
            float width = (float)(pixels.Item1 / scale), height = (float)(pixels.Item2 / scale);
            var geometry=new TerminalGridGeometry(80,40,width,height,(uint)pixels.Item1,(uint)pixels.Item2,scale);
            using var terminal = new GhosttyTerminal(80, 40);
            terminal.Resize(80, 40, TerminalCellMetrics.PhysicalSize(width, scale), TerminalCellMetrics.PhysicalSize(height, scale));
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            string Line(int row) => string.Concat(Enumerable.Range(0, 79).Select(i => alphabet[(i + row * 7) % alphabet.Length]));
            byte[] output = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 39).Select(row => $"\x1b[{row + 1};1H{Line(row)}")));
            terminal.Write(output, output.Length);
            foreach (var cell in new[] { (0, 0), (40, 20), (70, 37) })
            {
                terminal.Select(-1, -1);
                var start=geometry.ToNative((cell.Item1+.1)*width,(cell.Item2+.5)*height);
                var end=geometry.ToNative((cell.Item1+4.9)*width,(cell.Item2+.5)*height);
                float x1=start.X, x2=end.X, y=start.Y;
                terminal.SelectionGesture(0, x1, y);
                terminal.SelectionGesture(2, x2, y);
                terminal.SelectionGesture(1, x2, y);
                string selected = terminal.CopySelection();
                check(selected == Line(cell.Item2).Substring(cell.Item1, 5),
                    $"Pointer selection at {scale * 100}% / {pixels.Item1}x{pixels.Item2}px / cell {cell} copies the five cells under the drag (actual '{selected}')");
            }
        }
        var retained=new TerminalGridGeometry(80,40,12,21,13,22,1.75);
        var point=retained.ToNative(70.5*12,37.5*21);
        check((int)(point.X/13)==70 && (int)(point.Y/22)==37,
            "Retained frame hit testing uses its native cell sizes instead of assuming the current display scale");
        var local=retained.FromScreen(-1200+70.5*12*1.75,200+37.5*21*1.75,-1200,200);
        check(retained.CellAt(local.X,local.Y)==37*80+70,"Screen-point conversion respects pane origin, negative monitor coordinates and fractional DPI");
    }
}
