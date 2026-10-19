using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ParentControl.Core;

public static class LocalSecret
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("Kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] data, bool machine = false) => Transform(data, true, machine);
    public static byte[] Unprotect(byte[] data) => Transform(data, false, false);

    private static byte[] Transform(byte[] data, bool protect, bool machine)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Blob output;
            var success = protect
                ? CryptProtectData(ref input, "ParentControl", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, machine ? 4 : 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
