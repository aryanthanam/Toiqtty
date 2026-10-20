using System.IO.Compression;
using System.Text;
using Toiqtty.Terminal;

internal static class ProtocolChecks
{
    internal static async Task Transport(Action<bool, string> check)
    {
        string command = """
            $e=[char]27
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::Write("${e}[2J${e}[H${e}_Ga=T,i=91,f=24,s=1,v=1,C=1,q=2;/wAA${e}\")
            [Console]::Write("${e}[5;1H${e}[?2027h${e}_Ga=T,i=92,f=24,s=1,v=1,U=1,c=2,r=1,C=1,q=2;AP8A${e}\${e}[38;2;0;0;92m" + [char]::ConvertFromUtf32(0x10eeee) + [char]0x0305 + [char]0x0305 + [char]::ConvertFromUtf32(0x10eeee) + [char]0x0305 + [char]0x030d + "${e}[0m")
            Start-Sleep -Milliseconds 500
            """;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        using var process = new ConPtyProcess($"{Toiqtty.Models.ShellDefaults.Command} -NoProfile -EncodedCommand {encoded}", Environment.CurrentDirectory, 20, 5);
        using var output = new MemoryStream();
        await process.Output.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(15));
        using var terminal = new GhosttyTerminal(20, 5);
        byte[] bytes = output.ToArray(); terminal.Write(bytes, bytes.Length);
        var snapshot = terminal.ReadSnapshot(new TerminalCell[100]);
        if (snapshot.Graphics.Count == 0) Console.WriteLine("ConPTY protocol output: " + Encoding.UTF8.GetString(bytes).Replace("\x1b", "ESC"));
        check(snapshot.Graphics.Any(g => g.ImageId == 91), "real ConPTY preserves Kitty graphics APC from PowerShell output");
        if (!snapshot.Graphics.Any(g => g.ImageId == 92)) Console.WriteLine("ConPTY placeholder output: " + Encoding.UTF8.GetString(bytes).Replace("\x1b", "ESC"));
        check(snapshot.Graphics.Any(g => g.ImageId == 92), "real ConPTY preserves Unicode-placeholder image cells");
    }
    internal static void Run(Action<bool, string> check)
    {
        using var terminal = new GhosttyTerminal(20, 5);
        var cells = new TerminalCell[100];
        void Write(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); terminal.Write(bytes, bytes.Length); }
        TerminalSnapshot Snapshot() => terminal.ReadSnapshot(cells);
        void Graphics(string parameters, byte[]? bytes = null) => Write("\x1b_G" + parameters + ";" + (bytes is null ? "" : Convert.ToBase64String(bytes)) + "\x1b\\");
        string responses = "";
        terminal.Response += bytes => responses += Encoding.UTF8.GetString(bytes);
        terminal.Resize(20, 5, 8, 20);
        Graphics("a=q,i=41,f=24,s=1,v=1", [255, 0, 0]);
        check(responses.Contains("i=41;OK"), "Kitty graphics capability query returns a successful PTY response");
        Graphics("a=T,i=42,p=1,f=24,s=2,v=1,c=4,r=2,C=1", [255, 0, 0, 0, 0, 255]);
        var snapshot = Snapshot();
        check(snapshot.Graphics.Count == 1 && snapshot.Graphics[0].Image.Pixels.SequenceEqual(new byte[] {255, 0, 0, 0, 0, 255}),
            "Kitty RGB transmission reaches the graphics snapshot unchanged");
        var graphic = snapshot.Graphics[0];
        check(graphic.PixelWidth == 32 && graphic.PixelHeight == 40 && graphic.SourceWidth == 2 && graphic.SourceHeight == 1,
            "Ghostty computes image scaling and source geometry from cell dimensions");
        ulong generation = graphic.Image.Generation;
        Graphics("a=T,i=42,p=1,f=24,s=2,v=1,C=1", [0, 255, 0, 255, 255, 255]);
        snapshot = Snapshot();
        check(snapshot.Graphics[0].Image.Generation != generation && snapshot.Graphics[0].Image.Pixels[1] == 255,
            "same-size image replacement invalidates texture generations");
        Graphics("a=d,d=A");
        check(Snapshot().Graphics.Count == 0, "Kitty delete removes visible placements and cached snapshot images");
        // A real PNG decoded through Windows WIC, not an example decoder stub.
        Graphics("a=T,i=43,f=100,c=2,r=1,C=1", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
        snapshot = Snapshot();
        check(snapshot.Graphics.Count == 1 && snapshot.Graphics[0].Image.Channels == 4 &&
            snapshot.Graphics[0].Image.Pixels.SequenceEqual(new byte[] {255, 0, 0, 255}), "Kitty PNG images decode to actual RGBA pixels through WIC");
        Graphics("a=d,d=A");
        // One APC fragmented across byte reads, followed by a chunked transmission.
        var command = Encoding.ASCII.GetBytes("\x1b_Ga=T,i=44,f=32,s=1,v=1,C=1;/wAAgA==\x1b\\");
        foreach (byte value in command) terminal.Write([value], 1);
        check(Snapshot().Graphics.Single().Image.Pixels.SequenceEqual(new byte[] {255, 0, 0, 128}), "fragmented Kitty APC and RGBA alpha survive transport reads");
        Graphics("a=d,d=A");
        Write("\x1b_Ga=T,i=45,f=24,s=2,v=1,m=1,C=1;/wAA\x1b\\");
        check(Snapshot().Graphics.Count == 0, "incomplete Kitty chunks do not display a partial image");
        Write("\x1b_Gm=0;AAD/\x1b\\");
        check(Snapshot().Graphics.Single().Image.Width == 2, "Kitty continuation chunks assemble into one image");
        Graphics("a=d,d=A");
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(new byte[] {0, 255, 0});
            Graphics("a=T,i=46,f=24,s=1,v=1,o=z,C=1", compressed.ToArray());
        }
        check(Snapshot().Graphics.Single().Image.Pixels.SequenceEqual(new byte[] {0, 255, 0}), "Kitty zlib transmission is decompressed by Ghostty");
        Write("\x1b[?1049h");
        check(Snapshot().Graphics.Count == 0, "alternate screen isolates graphics storage");
        Write("\x1b[?1049l");
        check(Snapshot().Graphics.Single().ImageId == 46, "leaving alternate screen restores primary-screen images");
        Graphics("a=d,d=A");
        Write("\x1b[H");
        Graphics("a=T,i=47,p=7,f=24,s=2,v=1,x=1,y=0,w=1,h=1,c=3,r=2,X=2,Y=3,z=-1,C=1", [255, 0, 0, 0, 0, 255]);
        graphic = Snapshot().Graphics.Single();
        check(graphic.SourceX == 1 && graphic.SourceWidth == 1 && graphic.OffsetX == 2 && graphic.OffsetY == 3 && graphic.Z == -1,
            "Kitty cropping, pixel offsets, placement identifiers and z-order reach the renderer");
        Write("\x1b[5;1H\n\n");
        check(Snapshot().Graphics.Count == 0, "images scroll out of the viewport with their terminal anchors");
        terminal.Scroll(-2);
        check(Snapshot().Graphics.Count == 1, "scrollback reveals anchored images again");
        terminal.Scroll(0);
        Write("\x1b" + "c");
        check(Snapshot().Graphics.Count == 0, "terminal reset clears image storage");
        Graphics("a=T,i=48,f=100", Encoding.ASCII.GetBytes("invalid png"));
        check(Snapshot().Graphics.Count == 0 && !responses.EndsWith("i=48;OK\x1b\\"), "invalid PNG payloads are rejected without creating textures");

        Write("\x1b[HOLD\x1b[?2026h\rNEW");
        snapshot = Snapshot();
        check(snapshot.Frame.RenderHeld == 1 && Row(cells, 20).StartsWith("OLD"), "synchronized output captures the exact pre-hold frame within one write");
        Graphics("a=T,i=49,f=24,s=1,v=1,C=1", [255, 0, 0]);
        check(Snapshot().Graphics.Count == 0, "synchronized output freezes images as well as text");
        Write("\x1b[?2026l");
        snapshot = Snapshot();
        check(snapshot.Frame.RenderHeld == 0 && Row(cells, 20).StartsWith("NEW") && snapshot.Graphics.Count == 1, "ending synchronized output publishes text and images together");
        Write("\x1b[?2026h\rEND");
        Thread.Sleep(1050);
        snapshot = Snapshot();
        check(snapshot.Frame.RenderHeld == 0 && Row(cells, 20).StartsWith("END"), "synchronized-output timeout releases a stalled writer");

        Write("\x1b" + "c\x1b[4:3;58:2::10:20:30mX\x1b[0m\x1b[53mY\x1b[0m\x1b[4:2m中\x1b[0m");
        Snapshot();
        check(cells[0].UnderlineStyle == 3 && cells[0].UnderlineColor == 0xff0a141e, "curly underline and independent RGB underline color survive the bridge");
        check((cells[1].Flags & 128) != 0 && cells[2].Width == 2 && cells[2].UnderlineStyle == 2 && cells[3].Width == 0,
            "overline and double underline retain wide-cell decoration geometry");
        Write("\x1b[5 q\x1b]12;#123456\x1b\\");
        snapshot = Snapshot();
        check(snapshot.Frame.CursorBlinking == 1 && snapshot.Frame.CursorStyle == 0 && snapshot.Frame.CursorColor == 0xff123456,
            "cursor shape, blinking and OSC 12 cursor color reach the UI");
        Write("\x1b[2 q");
        snapshot = Snapshot();
        check(snapshot.Frame.CursorBlinking == 0 && snapshot.Frame.CursorStyle == 1, "steady block cursor disables blinking");
        Write("\x1b[H\x1b]8;id=test;https://ghostty.org/docs\x1b\\LINK\x1b]8;;\x1b\\X");
        check(terminal.Hyperlink(0, 0) == "https://ghostty.org/docs" && terminal.Hyperlink(4, 0) == "", "OSC 8 hyperlink targets are resolved by Ghostty at viewport cells");
        responses = ""; Write("\x1b[?996n\x1b[16t\x1b[18t");
        check(responses.Contains("\x1b[?997;1n") && responses.Contains("\x1b[6;20;8t") && responses.Contains("\x1b[8;5;20t"),
            "dark-mode and pixel/grid-size queries receive correct responses");
        responses = ""; Write("\x1b[>q\x1b[c\x1b[>c\x1b[=c");
        check(responses.Contains("toiqtty " + Toiqtty.Models.BuildInfo.Version) && responses.Contains("\x1b[?62;22;6c")
            && responses.Contains("544F4951"), "terminal identity and device attributes advertise toiqtty");
        responses = ""; Write("\x1b[?2048h"); terminal.Resize(22, 6, 9, 21);
        check(responses.Contains("\x1b[48;6;22;126;198t"), "in-band size reports follow terminal resize");
        Write("\x1b[>3u");
        check(Encoding.UTF8.GetString(terminal.EncodeKey(65, 0, "a", TerminalKeyAction.Release)) == "\x1b[97;1:3u", "Kitty keyboard reports physical key releases");
        check(Encoding.UTF8.GetString(terminal.EncodeKey(65, 0, "a", TerminalKeyAction.Repeat)) == "a", "Kitty repeat retains plain text when report-all is not requested");
        Write("\x1b[>11u");
        check(Encoding.UTF8.GetString(terminal.EncodeKey(65, 1, "A", TerminalKeyAction.Repeat, 97, 1)).Contains(":2"), "Kitty report-all distinguishes repeat from press");
        Write("\x1b[<2u");
        check(terminal.EncodeKey(38, 0, action: TerminalKeyAction.Release).Length == 0, "legacy keyboard mode suppresses key-release bytes");
        terminal.Resize(20, 5, 8, 20);
        Write("\x1b" + "c\x1b[?2027h");
        Graphics("a=T,i=50,p=1,f=24,s=1,v=1,U=1,c=2,r=1,C=1", [255, 0, 0]);
        check(Snapshot().Graphics.Count == 0, "virtual Kitty placements wait for Unicode placeholder cells");
        Write("\x1b[38;2;0;0;50m\U0010eeee\u0305\u0305\U0010eeee\u0305\u030d\x1b[0m");
        snapshot = Snapshot();
        check(snapshot.Graphics.Count > 0 && snapshot.Graphics.All(g => g.ImageId == 50 && g.Z == -1) && cells[0].Length == 0,
            "Unicode placeholder images use Ghostty's fragment geometry and suppress placeholder glyphs");
        Graphics("a=T,i=52,p=1,f=24,s=1,v=1,P=50,Q=1,H=2,V=1,c=1,r=1,C=1", [0, 255, 0]);
        graphic = Snapshot().Graphics.Single(g => g.ImageId == 52);
        check(graphic.Column == 2 && graphic.Row == 1, "relative images resolve their Unicode-placeholder parent's origin");
        Graphics("a=d,d=I,i=50");
        check(Snapshot().Graphics.All(g => g.ImageId != 52), "deleting a virtual parent removes its dependent placements");
        Write("\x1b" + "c");
        Graphics("a=T,i=51,f=24,s=1,v=1,C=1", [255, 0, 0]);
        Graphics("a=f,i=51,f=24,s=1,v=1,z=50", [0, 0, 255]);
        Graphics("a=a,i=51,r=1,z=50,s=3");
        snapshot = Snapshot();
        ulong firstFrame = snapshot.Graphics.Single().Image.Generation;
        check(snapshot.Frame.AnimationDelay <= 50, "Kitty animation schedules the next frame without shell output");
        Thread.Sleep(70);
        snapshot = Snapshot();
        check(snapshot.Graphics.Single().Image.Generation != firstFrame && snapshot.Graphics.Single().Image.Pixels[2] == 255,
            "Ghostty's animation clock changes pixels and invalidates textures");
        Graphics("a=a,i=51,s=1");
        check(Snapshot().Frame.AnimationDelay == uint.MaxValue, "stopped Kitty animations stop scheduling redraws");
        Write("\x1b" + "c");
        var pixels = new byte[64 * 64 * 4];
        bool bounded = true;
        TerminalImage? retained = null;
        for (int i = 0; i < 128; i++)
        {
            Array.Fill(pixels, (byte)i);
            Graphics("a=T,i=200,p=1,f=32,s=64,v=64,c=2,r=2,C=1,q=2", pixels);
            snapshot = Snapshot();
            retained ??= snapshot.Graphics.Single().Image;
            bounded &= snapshot.Graphics.Count == 1 && terminal.CachedImageBytes == (ulong)pixels.Length
                && snapshot.Graphics[0].Image.Pixels[0] == (byte)i;
        }
        check(bounded, "repeated image replacement releases obsolete native snapshot buffers and exposes current generations");
        check(retained!.Pixels.All(value => value == 0), "retained managed image snapshots remain immutable after native replacement");
        Graphics("a=d,d=A"); Snapshot();
        check(terminal.CachedImageBytes == 0, "deleting images releases every native cached pixel buffer");
        var largePixels = new byte[512 * 512 * 3];
        bool storageBounded = true;
        for (int i = 0; i < 96; i++)
        {
            Array.Fill(largePixels, (byte)i);
            Write("\x1b[H");
            Graphics($"a=T,i={1000 + i},f=24,s=512,v=512,c=2,r=2,C=1,q=2", largePixels);
            snapshot = Snapshot();
            storageBounded &= terminal.CachedImageBytes <= 64UL * 1024 * 1024;
        }
        check(storageBounded && snapshot.Graphics.Count < 96 && snapshot.Graphics.Any(g => g.ImageId == 1095),
            "graphics pressure beyond 64 MiB evicts old images and bounds native snapshots while retaining new output");
        Graphics("a=d,d=A"); Snapshot();
        check(terminal.CachedImageBytes == 0, "graphics eviction pressure leaves no native snapshot buffers after deletion");
        Graphics("a=T,i=3000,f=24,s=1,v=1,C=1,q=2", [255, 0, 0]);
        Graphics("a=f,i=3000,f=24,s=1,v=1,z=10,q=2", [0, 0, 255]);
        Graphics("a=a,i=3000,r=1,z=10,s=3,v=2"); Snapshot();
        var animationDeadline = DateTime.UtcNow.AddSeconds(2);
        do { Thread.Sleep(15); snapshot = Snapshot(); }
        while (snapshot.Frame.AnimationDelay != uint.MaxValue && DateTime.UtcNow < animationDeadline);
        check(snapshot.Frame.AnimationDelay == uint.MaxValue && snapshot.Graphics.Single().Image.Pixels[2] == 255,
            "finite Kitty animation stops scheduling after its requested loops and retains the final frame");
    }
    private static string Row(TerminalCell[] cells, int columns) => string.Concat(cells.Take(columns).Select(c => c.GetText()));
}
