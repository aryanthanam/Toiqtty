using System.Text;
using Oroterm.Terminal;

internal static class ImageMediumChecks
{
    internal static async Task Transport(Action<bool, string> check)
    {
        string file = Path.Combine(Path.GetTempPath(), "oroterm-image-" + Guid.NewGuid().ToString("N") + ".rgb");
        string temporary = Path.Combine(Path.GetTempPath(), "tty-graphics-protocol-" + Guid.NewGuid().ToString("N") + ".rgba");
        File.WriteAllBytes(file, [255, 0, 0]); File.WriteAllBytes(temporary, [0, 255, 0, 128]);
        try
        {
            string command = "[Console]::Write(\"$([char]27)_Ga=T,t=f,i=301,f=24,s=1,v=1,C=1;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(file)) +
                "$([char]27)\\$([char]27)_Ga=T,t=t,i=302,f=32,s=1,v=1,C=1;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(temporary)) + "$([char]27)\\\")";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
            using var process = new ConPtyProcess($"{Oroterm.Models.ShellDefaults.Command} -NoProfile -EncodedCommand {encoded}", Environment.CurrentDirectory, 20, 5);
            using var output = new MemoryStream();
            await process.Output.CopyToAsync(output).WaitAsync(TimeSpan.FromSeconds(15));
            using var terminal = new GhosttyTerminal(20, 5);
            byte[] bytes = output.ToArray(); terminal.Write(bytes, bytes.Length);
            var images = terminal.ReadSnapshot(new TerminalCell[100]).Graphics;
            check(images.Any(g => g.ImageId == 301 && g.Image.Pixels.SequenceEqual(new byte[] { 255, 0, 0 })), "real PowerShell/ConPTY file-medium transfer reaches the Ghostty pixel snapshot");
            check(images.Any(g => g.ImageId == 302 && g.Image.Pixels.SequenceEqual(new byte[] { 0, 255, 0, 128 })) && !File.Exists(temporary), "real PowerShell/ConPTY temporary-file transfer displays pixels and consumes the protocol file");
        }
        finally { File.Delete(file); File.Delete(temporary); }
    }
    internal static void Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "oroterm-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var ownedFiles = new List<string>();
        string Fixture(string name, byte[] bytes)
        {
            string path = Path.Combine(folder, name); ownedFiles.Add(path); File.WriteAllBytes(path, bytes); return path;
        }
        using var terminal = new GhosttyTerminal(20, 5);
        var cells = new TerminalCell[100];
        string responses = "";
        terminal.Response += bytes => responses += Encoding.UTF8.GetString(bytes);
        void Write(string text) { var bytes = Encoding.UTF8.GetBytes(text); terminal.Write(bytes, bytes.Length); }
        void Send(string metadata, string path, bool fragmented = false)
        {
            responses = "";
            var bytes = Encoding.UTF8.GetBytes("\x1b_G" + metadata + ";" + Convert.ToBase64String(Encoding.UTF8.GetBytes(path)) + "\x1b\\");
            if (fragmented) foreach (byte value in bytes) terminal.Write([value], 1);
            else terminal.Write(bytes, bytes.Length);
        }
        bool Pixels(uint id, params byte[] expected) => terminal.ReadSnapshot(cells).Graphics.Any(g => g.ImageId == id && g.Image.Pixels.SequenceEqual(expected));
        bool Rejected(uint id) => responses.Contains($"i={id};") && !responses.Contains($"i={id};OK");
        try
        {
            string raw = Fixture("image café 中.rgb", [99, 98, 97, 96, 255, 0, 0, 11]);
            Send("a=T,t=f,i=201,f=24,s=1,v=1,O=4,S=3,C=1", raw, fragmented: true);
            check(Pixels(201, 255, 0, 0) && File.Exists(raw), "Kitty file transfers preserve UTF-8 paths, byte offsets, exact lengths and source files");
            Send("a=q,t=f,i=202,f=24,s=1,v=1,O=4,S=3", raw);
            check(responses.Contains("i=202;OK") && !terminal.ReadSnapshot(cells).Graphics.Any(g => g.ImageId == 202), "Kitty file-medium capability queries load and validate without displaying an image");
            string png = Fixture("image.png", Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
            Send("a=T,t=f,i=203,f=100,C=1", png);
            check(Pixels(203, 255, 0, 0, 255), "Kitty file-medium PNG transfers use the existing Windows decoder");
            string temporary = Fixture("tty-graphics-protocol-image.rgba", [0, 255, 0, 128]);
            Send("a=T,t=t,i=204,f=32,s=1,v=1,C=1", temporary);
            check(Pixels(204, 0, 255, 0, 128) && !File.Exists(temporary), "Kitty temporary-file transfers consume pixels and remove the owned protocol file");
            string queryTemp = Fixture("tty-graphics-protocol-query.rgb", [0, 0, 255]);
            Send("a=q,t=t,i=212,f=24,s=1,v=1", queryTemp);
            check(responses.Contains("i=212;OK") && !File.Exists(queryTemp), "Kitty temporary-medium capability queries consume only the designated protocol file");
            string outside = Path.Combine(Environment.CurrentDirectory, "artifacts", "tty-graphics-protocol-" + Guid.NewGuid().ToString("N") + ".rgb");
            ownedFiles.Add(outside); File.WriteAllBytes(outside, [255, 0, 0]);
            Send("a=T,t=t,i=213,f=24,s=1,v=1,C=1", outside);
            check(Rejected(213) && File.Exists(outside), "Ghostty refuses to consume a protocol-named temporary image outside the Windows temp directory");
            Send("a=T,t=t,i=205,f=24,s=1,v=1,O=4,S=3,C=1", raw);
            check(Rejected(205) && File.Exists(raw), "Ghostty rejects temporary transfers without the required protocol filename and preserves the file");
            Send("a=T,t=f,i=206,f=24,s=1,v=1,S=100,C=1", raw);
            check(Rejected(206), "Kitty file transfer rejects a requested length beyond EOF");
            Send("a=T,t=f,i=207,f=24,s=1,v=1,C=1", Path.Combine(folder, "missing.rgb"));
            check(Rejected(207), "Kitty missing-file transfers return a protocol error");
            foreach (string path in new[] { "NUL", "\\\\.\\pipe\\oroterm-test", "\\\\oroterm-invalid-host\\share\\image.rgb" })
            {
                Send("a=T,t=f,i=208,f=24,s=1,v=1,C=1", path);
                check(Rejected(208), "Ghostty rejects Windows device/UNC image path: " + path);
            }
            Send("a=q,t=s,i=209,f=24,s=1,v=1", "/oroterm-unavailable");
            check(Rejected(209), "Windows shared-memory image transfer stays unavailable without upstream support");
            Write("\x1b[?1049h");
            Send("a=T,t=f,i=210,f=24,s=1,v=1,O=4,S=3,C=1", raw);
            check(Pixels(210, 255, 0, 0), "Kitty file-medium configuration also applies to the alternate screen");
            Write("\x1b[?1049l\x1b" + "c");
            Send("a=T,t=f,i=211,f=24,s=1,v=1,O=4,S=3,C=1", raw);
            check(Pixels(211, 255, 0, 0), "Kitty file-medium configuration survives terminal reset");
            responses = ""; Write("\x1b[H\x1b[6n\x1b]66;w=2; \a\x1b[6n");
            check(responses == "\x1b[1;1R\x1b[1;1R", "OSC 66 remains ignored by the unchanged pinned upstream parser");
        }
        finally
        {
            foreach (string file in ownedFiles) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
