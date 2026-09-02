using System.Runtime.InteropServices;

namespace VaultLatch;

internal sealed class PowerEventWindow : NativeWindow, IDisposable
{
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtPowerSettingChange = 0x8013;
    private const int DeviceNotifyWindowHandle = 0;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private IntPtr _registration;

    public PowerEventWindow()
    {
        CreateHandle(new CreateParams { Caption = "VaultLatchPowerWindow" });
        var setting = ConsoleDisplayState;
        _registration = RegisterPowerSettingNotification(Handle, ref setting, DeviceNotifyWindowHandle);
    }

    public event EventHandler? DisplayTurnedOff;
    public event EventHandler? SystemSuspending;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg != WmPowerBroadcast)
        {
            return;
        }

        var eventType = m.WParam.ToInt32();
        if (eventType == PbtApmSuspend)
        {
            SystemSuspending?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (eventType != PbtPowerSettingChange || m.LParam == IntPtr.Zero)
        {
            return;
        }

        var settingGuid = Marshal.PtrToStructure<Guid>(m.LParam);
        if (settingGuid != ConsoleDisplayState)
        {
            return;
        }

        const int dataOffset = 20;
        var displayState = Marshal.ReadInt32(m.LParam, dataOffset);
        if (displayState == 0)
        {
            DisplayTurnedOff?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_registration);
            _registration = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero)
        {
            ReleaseHandle();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
