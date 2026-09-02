using System.Runtime.InteropServices;

namespace VaultLatch;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    public static uint GetIdleMilliseconds()
    {
        var lastInput = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref lastInput))
        {
            return 0;
        }

        return unchecked((uint)Environment.TickCount - lastInput.dwTime);
    }

    public static bool LockWindows() => LockWorkStation();
}
