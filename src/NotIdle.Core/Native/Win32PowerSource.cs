using System.Runtime.InteropServices;

namespace NotIdle.Core.Native;

/// <summary>
/// Reads AC power and battery telemetry using Windows <c>GetSystemPowerStatus</c>.
/// Near-free kernel syscall that allows laptop power saving without intrusive hooks.
/// </summary>
public sealed class Win32PowerSource : ISystemPowerSource
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0 = Offline (battery), 1 = Online (AC), 255 = Unknown
        public byte BatteryFlag;         // 1 = High, 2 = Low, 4 = Critical, 8 = Charging, 128 = No battery, 255 = Unknown
        public byte BatteryLifePercent;  // 0-100, 255 = Unknown
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    public bool IsOnBattery
    {
        get
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                return status.ACLineStatus == 0;
            }
            return false;
        }
    }

    public int BatteryPercentage
    {
        get
        {
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS status))
            {
                if (status.BatteryLifePercent <= 100)
                {
                    return status.BatteryLifePercent;
                }
            }
            return -1;
        }
    }
}
