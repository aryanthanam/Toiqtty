using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Toiqtty.Browser;

internal static class BrowserSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32")] private static extern nint LocalFree(nint memory);
    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, 0, 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != 0) LocalFree(output.Data); }
    }
}
