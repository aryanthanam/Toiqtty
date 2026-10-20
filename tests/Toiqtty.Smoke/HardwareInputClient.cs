using System.Text;
using System.Text.Json;

internal static class HardwareInputClient
{
    public static void Run(string folder)
    {
        if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).StartsWith("hardware-check-", StringComparison.Ordinal))
            throw new ArgumentException("Only a dedicated hardware-check directory is accepted.");
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.Write("\x1b]2;Hardware input receiver\a\x1b[2J\x1b[H");
        Console.WriteLine("TOIQTTY HARDWARE CHECK — literal input receiver");
        Console.WriteLine("Type here, then press Enter. Input is recorded, never executed.");
        Console.WriteLine("Expected sample: 日本語 café 中🙂");
        Console.WriteLine("Narrator sample: The quick brown fox. Terminal accessibility check.");
        Console.WriteLine("Drag the fixture file into this pane; press Enter to record its quoted path.");
        string red = Convert.ToBase64String(new byte[] { 255, 0, 0 });
        Console.Write($"\x1b_Ga=T,f=24,s=1,v=1,i=901,q=2,c=4,r=2;{red}\x1b\\\r\n\r\n\r\n");
        Console.Write("INPUT> ");
        var line = new StringBuilder();
        string report = Path.Combine(folder, "input-receiver.jsonl");
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                string text = line.ToString();
                File.AppendAllText(report, JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, text }) + "\n");
                Console.WriteLine(); Console.WriteLine("RECEIVED: " + text); Console.Write("INPUT> "); line.Clear();
            }
            else if (key.Key == ConsoleKey.Backspace && line.Length > 0)
            {
                line.Length = System.Globalization.StringInfo.ParseCombiningCharacters(line.ToString())[^1];
                Console.Write("\r\x1b[2KINPUT> " + line);
            }
            else if (key.KeyChar >= ' ' && line.Length < 8192) { line.Append(key.KeyChar); Console.Write(key.KeyChar); }
        }
    }
}
