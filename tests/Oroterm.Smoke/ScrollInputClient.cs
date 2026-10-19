using System.Runtime.InteropServices;
using System.Text;

// A literal-input TUI fixture: no received input is ever executed as a command.
internal static class ScrollInputClient
{
    public static void Run()
    {
        nint inputHandle=GetStdHandle(-10);
        if(!GetConsoleMode(inputHandle,out uint oldMode) || !SetConsoleMode(inputHandle,(oldMode & ~0x47u) | 0x280u))
            throw new IOException("Could not enable raw VT input for the scrolling fixture.");
        Console.OutputEncoding=new UTF8Encoding(false);
        int up=0,down=0,middle=0;
        using var input=Console.OpenStandardInput();
        var pending=new StringBuilder(); byte[] bytes=new byte[512];
        try
        {
            Console.Write("\x1b[?1049h\x1b[?1000h\x1b[?1006h");
            Draw();
            while(true)
            {
                int count=input.Read(bytes,0,bytes.Length); if(count==0) return;
                pending.Append(Encoding.UTF8.GetString(bytes,0,count));
                while(true)
                {
                    string text=pending.ToString();
                    var match=System.Text.RegularExpressions.Regex.Match(text,@"\x1b\[<(\d+);(\d+);(\d+)([Mm])");
                    if(!match.Success) { if(pending.Length>4096) pending.Clear(); break; }
                    pending.Remove(0,match.Index+match.Length);
                    int code=int.Parse(match.Groups[1].Value);
                    if(match.Groups[4].Value=="M")
                    {
                        if(code==64) up++;
                        else if(code==65) down++;
                        else if(code==2) middle++;
                    }
                    Draw();
                }
            }
        }
        finally { Console.Write("\x1b[?1006l\x1b[?1000l\x1b[?1049l"); SetConsoleMode(inputHandle,oldMode); }
        void Draw() => Console.Write($"\x1b[HAPP_WHEEL_READY\x1b[K\r\nAPP_WHEEL_UP={up:D4}\x1b[K\r\nAPP_WHEEL_DOWN={down:D4}\x1b[K\r\nAPP_MIDDLE={middle:D4}\x1b[K\r\n");
    }
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle,out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint handle,uint mode);
}
