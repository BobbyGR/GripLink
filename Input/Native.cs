using System.Runtime.InteropServices;

namespace GripLink.Input;

internal static class XInput
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint PacketNumber;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vibration
    {
        public ushort LeftMotorSpeed, RightMotorSpeed;
    }

    public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    public const ushort Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    public const uint ErrorSuccess = 0;

    /// <summary>Undocumented ordinal 100 = XInputGetStateEx, which also reports the Guide button.</summary>
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    public static extern uint GetStateEx(uint userIndex, out State state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    public static extern uint SetState(uint userIndex, ref Vibration vibration);

    public static readonly Dictionary<string, ushort> ButtonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DPadUp"] = DPadUp, ["DPadDown"] = DPadDown, ["DPadLeft"] = DPadLeft, ["DPadRight"] = DPadRight,
        ["Start"] = Start, ["Menu"] = Start, ["Back"] = Back, ["View"] = Back,
        ["LeftThumb"] = LeftThumb, ["LS"] = LeftThumb, ["RightThumb"] = RightThumb, ["RS"] = RightThumb,
        ["LeftShoulder"] = LeftShoulder, ["LB"] = LeftShoulder, ["RightShoulder"] = RightShoulder, ["RB"] = RightShoulder,
        ["Guide"] = Guide, ["A"] = A, ["B"] = B, ["X"] = X, ["Y"] = Y,
    };
}

internal static class Keyboard
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Accepts key names like "F13", "F14", "Insert" or a hex virtual-key code like "0x7C".</summary>
    public static int ParseVirtualKey(string name)
    {
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt32(name[2..], 16);
        if (Enum.TryParse<ConsoleKey>(name, true, out var key)) return (int)key; // ConsoleKey values are VK codes
        throw new FormatException($"Unknown key '{name}'. Use a name like F13 or a code like 0x7C.");
    }
}

internal static class Power
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static (byte Percent, bool Charging) Read()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryLifePercent == 255) return (100, true);
        return (Math.Min(s.BatteryLifePercent, (byte)100), s.ACLineStatus == 1);
    }
}
