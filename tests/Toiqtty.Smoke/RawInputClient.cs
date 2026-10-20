using System.Runtime.InteropServices;
using System.Text;

internal static class RawInputClient
{
    internal static void Run()
    {
        nint handle=GetStdHandle(-10);
        if(!GetConsoleMode(handle,out uint mode) || !SetConsoleMode(handle,(mode&~0x47u)|0x280u)) throw new IOException("Raw input setup failed.");
        Console.OutputEncoding=new UTF8Encoding(false);
        using var input=Console.OpenStandardInput();
        using var received=new MemoryStream();
        byte[] buffer=new byte[1024];
        try
        {
            Console.Write("\x1b[>3u\x1b[?2004h\x1b[?1004h\x1b[?1000h\x1b[?1006h\x1b[?25l"); Draw();
            int count;
            while((count=input.Read(buffer,0,buffer.Length))>0)
            { if(received.Length+count>4096) throw new IOException("Raw fixture input limit exceeded."); received.Write(buffer,0,count); Draw(); }
        }
        finally { SetConsoleMode(handle,mode); }
        void Draw()=>Console.Write("\x1b[2J\x1b[HRAW_INPUT_READY\r\nHEX="+Convert.ToHexString(received.ToArray())+"\r\n");
    }
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(nint handle,out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(nint handle,uint mode);
}
