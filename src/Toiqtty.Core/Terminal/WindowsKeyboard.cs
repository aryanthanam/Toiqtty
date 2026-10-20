using System.Runtime.InteropServices;
using System.Text;

namespace Toiqtty.Terminal;

internal static class WindowsKeyboard
{
    // These keys belong to Windows text services, including VK_PROCESSKEY
    // delivered in place of a physical key while the IME owns it.
    internal static bool IsImeKey(uint key) => key is 0x15 or 0x17 or 0x18 or 0x19 or 0x1c or 0x1d or 0x1f or 0xe5;

    public static uint PhysicalKey(uint key, uint scanCode, bool extended) => key switch
    {
        13 when extended => 0x1000d,
        16 => scanCode == 0x36 ? 161u : 160u,
        17 => extended ? 163u : 162u,
        18 => extended ? 165u : 164u,
        45 when !extended => 96, 35 when !extended => 97, 40 when !extended => 98,
        34 when !extended => 99, 37 when !extended => 100, 12 when !extended => 101,
        39 when !extended => 102, 36 when !extended => 103, 38 when !extended => 104,
        33 when !extended => 105, 46 when !extended => 110,
        _ => key
    };
    // Flag 4 leaves the keyboard's dead-key state untouched (Windows 10 1607+).
    public static (string Text, uint Unshifted) Translate(uint key, uint scanCode)
    {
        var state = new byte[256];
        if (!GetKeyboardState(state)) return ("", 0);
        var layout = GetKeyboardLayout(0);
        return Translate(key, scanCode, state, layout);
    }
    internal static (string Text, uint Unshifted) Translate(uint key, uint scanCode, byte[] keyboardState, nint layout)
    {
        var state = (byte[])keyboardState.Clone();
        // Text for shortcuts, before Ctrl turns letters into control characters.
        state[0x11] = state[0xa2] = state[0xa3] = state[0x12] = state[0xa4] = state[0xa5] = 0;
        string value = PreviewText(key, scanCode, state, layout).Text;
        if (value.Any(char.IsControl)) value = "";
        state[0x10] = state[0xa0] = state[0xa1] = state[0x14] = 0;
        string plain = PreviewText(key, scanCode, state, layout).Text;
        uint unshifted = plain.Length > 0 ? (uint)char.ConvertToUtf32(plain, 0) : 0;
        return (value, unshifted);
    }
    internal static (string Text, bool IsDeadKey) PreviewText(uint key, uint scanCode, byte[] state, nint layout)
    {
        if (state.Length != 256) throw new ArgumentException("Keyboard state must contain 256 keys.", nameof(state));
        var text = new StringBuilder(16);
        int count = ToUnicodeEx(key, scanCode, state, text, text.Capacity, 4, layout);
        return (count > 0 ? text.ToString(0, Math.Min(count, text.Length)) : "", count < 0);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scanCode, byte[] state, StringBuilder text, int capacity, uint flags, nint layout);
}
