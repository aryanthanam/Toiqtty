using System.Runtime.InteropServices;
using Oroterm.Terminal;

internal static class KeyboardLayoutChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Load into this process without activating a layout or notifying the
        // shell. No user language list or keyboard preference is changed.
        WithLayout("0000040c", layout =>
        {
            var state = new byte[256];
            check(WindowsKeyboard.Translate(0x32, 0x03, state, layout) == ("é", (uint)'é'), "French AZERTY translation produces its accented number-row symbol");
            state[0x10] = state[0xa0] = 0x80;
            check(WindowsKeyboard.Translate(0x32, 0x03, state, layout) == ("2", (uint)'é') && state[0x10] == 0x80,
                "shifted layout text preserves its unshifted codepoint without mutating keyboard state");
        });
        WithLayout("00000407", layout =>
        {
            var state = new byte[256]; state[0x11] = state[0xa2] = state[0x12] = state[0xa5] = 0x80;
            check(WindowsKeyboard.PreviewText(0x51, 0x10, state, layout).Text == "@", "Windows German AltGr translation produces the expected printable symbol");
            check(WindowsKeyboard.Translate(0x51, 0x10, state, layout).Unshifted == 'q' && state[0xa5] == 0x80,
                "AltGr preview retains the physical layout's unshifted key without consuming right Alt");
        });
        WithLayout("00020409", layout =>
        {
            var state = new byte[256];
            check(WindowsKeyboard.PreviewText(0xde, 0x28, state, layout).IsDeadKey, "US International accent is recognized as a dead key");
            check(WindowsKeyboard.PreviewText(0x45, 0x12, state, layout).Text == "e", "non-consuming dead-key preview does not leak accent state into later text input");
        });
    }
    private static void WithLayout(string name, Action<nint> action)
    {
        var existing = new nint[GetKeyboardLayoutList(0, null)]; GetKeyboardLayoutList(existing.Length, existing);
        nint layout = LoadKeyboardLayout(name, 0x80);
        if (layout == 0) throw new InvalidOperationException("Windows keyboard layout unavailable: " + name);
        try { action(layout); }
        finally { if (!existing.Contains(layout)) UnloadKeyboardLayout(layout); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadKeyboardLayout(string name, uint flags);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnloadKeyboardLayout(nint layout);
}
