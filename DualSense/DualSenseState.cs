namespace GripLink.DualSense;

public enum DPad : byte
{
    North = 0, NorthEast = 1, East = 2, SouthEast = 3,
    South = 4, SouthWest = 5, West = 6, NorthWest = 7,
    Neutral = 8,
}

/// <summary>Every DualSense button a game can see. Edge-only buttons are ignored by games unless Model = DualSenseEdge.</summary>
public enum DsButton
{
    None,
    Cross, Circle, Square, Triangle,
    L1, R1, L3, R3,
    Create, Options, PS, TouchpadClick, Mute,
    DPadUp, DPadDown, DPadLeft, DPadRight,
    // DualSense Edge only
    EdgeFnL, EdgeFnR, EdgeBackL, EdgeBackR,
}

public struct TouchPoint
{
    public bool Active;
    public byte Id;      // 0..127, increments for every new touch
    public ushort X;     // 0..1919
    public ushort Y;     // 0..1079
}

/// <summary>Logical snapshot of a DualSense. Sticks: 0..255, 128 = centre, Y axis: 0 = up.</summary>
public sealed class DualSenseState
{
    public byte LX = 128, LY = 128, RX = 128, RY = 128;
    public byte L2, R2;

    public readonly HashSet<DsButton> Buttons = new();

    public short GyroX, GyroY, GyroZ;          // 16 raw units per deg/s (matches the calibration we report)
    public short AccelX, AccelY = 8192, AccelZ; // 8192 raw units per g

    public TouchPoint Touch0, Touch1;

    public byte BatteryPercent = 100;
    public bool Charging;

    public bool Has(DsButton b) => Buttons.Contains(b);

    public DPad ComputeDPad()
    {
        bool up = Has(DsButton.DPadUp), down = Has(DsButton.DPadDown);
        bool left = Has(DsButton.DPadLeft), right = Has(DsButton.DPadRight);
        if (up && down) { up = down = false; }
        if (left && right) { left = right = false; }
        return (up, down, left, right) switch
        {
            (true, _, false, true) => DPad.NorthEast,
            (true, _, true, false) => DPad.NorthWest,
            (true, _, _, _) => DPad.North,
            (_, true, false, true) => DPad.SouthEast,
            (_, true, true, false) => DPad.SouthWest,
            (_, true, _, _) => DPad.South,
            (_, _, _, true) => DPad.East,
            (_, _, true, _) => DPad.West,
            _ => DPad.Neutral,
        };
    }
}
