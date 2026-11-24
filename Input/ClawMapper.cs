using GripLink.DualSense;

namespace GripLink.Input;

/// <summary>
/// Reads the MSI Claw's built-in controls (XInput pad, M1/M2 via keyboard keys, IMU, battery)
/// and turns them into a DualSense state. Also sends game rumble back to the Claw's motors.
/// </summary>
internal sealed class ClawMapper
{
    private volatile AppConfig _cfg;
    private readonly Action<string> _log;
    private readonly uint _slot;
    private volatile List<(ushort Mask, DsButton Target)> _buttonMap = new();
    private volatile List<(int Vk, DsButton Target)> _keys = new();
    private volatile List<(ushort Mask, DsButton Target, bool Suppress)> _combos = new();
    private readonly MotionSource _motion;

    private (byte Percent, bool Charging) _battery = (100, false);
    private long _batteryCheckedAt = long.MinValue;
    private bool _wasConnected = true;

    public ClawMapper(AppConfig cfg, Action<string> log)
    {
        _cfg = cfg;
        _log = log;
        _slot = (uint)Math.Clamp(cfg.XInputSlot, 0, 3);
        BuildBindings(cfg);
        _motion = new MotionSource(cfg.Motion, log);
    }

    /// <summary>Live update of mappings, sticks and rumble settings (no re-plug needed).</summary>
    public void UpdateConfig(AppConfig cfg)
    {
        try { BuildBindings(cfg); _cfg = cfg; }
        catch (Exception ex) { _log($"Mapping not applied: {ex.Message}"); }
    }

    private void BuildBindings(AppConfig cfg)
    {
        var buttons = new List<(ushort, DsButton)>();
        var keys = new List<(int, DsButton)>();
        var combos = new List<(ushort, DsButton, bool)>();
        foreach (var (src, dst) in cfg.ButtonMap)
            buttons.Add((ParseXButton(src), ParseDsButton(dst)));
        foreach (var k in cfg.KeyBindings)
            keys.Add((Keyboard.ParseVirtualKey(k.Key), ParseDsButton(k.Target)));
        foreach (var c in cfg.Combos)
        {
            ushort mask = 0;
            foreach (var b in c.Buttons) mask |= ParseXButton(b);
            if (mask != 0) combos.Add((mask, ParseDsButton(c.Target), c.SuppressSources));
        }
        _buttonMap = buttons; _keys = keys; _combos = combos;
    }

    private static ushort ParseXButton(string name) =>
        XInput.ButtonNames.TryGetValue(name, out var m) ? m
            : throw new FormatException($"Unknown Claw button '{name}'. Valid: {string.Join(", ", XInput.ButtonNames.Keys)}");

    private static DsButton ParseDsButton(string name) =>
        Enum.TryParse<DsButton>(name, true, out var b) ? b
            : throw new FormatException($"Unknown DualSense button '{name}'. Valid: {string.Join(", ", Enum.GetNames<DsButton>())}");

    public bool IsClawConnected => XInput.GetStateEx(_slot, out _) == XInput.ErrorSuccess;

    /// <summary>
    /// XInput stick -> DualSense bytes. Radial (circular) dead zone so diagonals aren't sticky,
    /// optional anti-dead-zone to cancel the game's own dead zone. Defaults send raw values,
    /// like a real DualSense, and leave dead-zone handling to the game.
    /// </summary>
    private (byte X, byte Y) Stick(short rawX, short rawY)
    {
        double x = Math.Clamp(rawX / 32767.0, -1, 1), y = Math.Clamp(rawY / 32767.0, -1, 1);
        double mag = Math.Sqrt(x * x + y * y);
        double dz = Math.Clamp(_cfg.StickDeadZone, 0, 0.9);
        double anti = Math.Clamp(_cfg.StickAntiDeadZone, 0, 0.9);
        if (mag <= dz || mag == 0) { x = y = 0; }
        else if (dz > 0 || anti > 0)
        {
            double scaled = (mag - dz) / (1 - dz);
            double outMag = Math.Min(1, anti + (1 - anti) * scaled);
            x *= outMag / mag; y *= outMag / mag;
        }
        return (ToByte(x), ToByte(-y)); // XInput: up = +, DualSense: up = 0
    }

    private static byte ToByte(double f) =>
        (byte)Math.Clamp(Math.Round(f >= 0 ? 128 + f * 127 : 128 + f * 128), 0, 255);

    private static short Clamp16(double v) => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue);

    public DualSenseState Read()
    {
        var s = new DualSenseState();

        bool connected = XInput.GetStateEx(_slot, out var x) == XInput.ErrorSuccess;
        if (connected != _wasConnected)
        {
            _log(connected ? $"Claw controller found on XInput slot {_slot}" : $"No controller on XInput slot {_slot} - sending neutral input");
            _wasConnected = connected;
        }

        if (connected)
        {
            var g = x.Gamepad;
            ushort buttons = g.Buttons;

            foreach (var (mask, target, suppress) in _combos)
            {
                if ((buttons & mask) == mask)
                {
                    s.Buttons.Add(target);
                    if (suppress) buttons &= (ushort)~mask;
                }
            }
            foreach (var (mask, target) in _buttonMap)
                if ((buttons & mask) != 0 && target != DsButton.None) s.Buttons.Add(target);

            (s.LX, s.LY) = Stick(g.ThumbLX, g.ThumbLY);
            (s.RX, s.RY) = Stick(g.ThumbRX, g.ThumbRY);
            s.L2 = g.LeftTrigger;
            s.R2 = g.RightTrigger;
        }

        foreach (var (vk, target) in _keys)
            if (Keyboard.IsDown(vk)) s.Buttons.Add(target);

        var (gx, gy, gz) = _motion.ReadGyro();
        double gs = DualSenseReports.GyroRawPerDegS * _cfg.Motion.GyroScale;
        s.GyroX = Clamp16(gx * gs); s.GyroY = Clamp16(gy * gs); s.GyroZ = Clamp16(gz * gs);

        var (ax, ay, az) = _motion.ReadAccel();
        s.AccelX = Clamp16(ax * DualSenseReports.AccelRawPerG);
        s.AccelY = Clamp16(ay * DualSenseReports.AccelRawPerG);
        s.AccelZ = Clamp16(az * DualSenseReports.AccelRawPerG);

        long now = Environment.TickCount64;
        if (now - _batteryCheckedAt > 5000) { _battery = Power.Read(); _batteryCheckedAt = now; }
        s.BatteryPercent = _battery.Percent;
        s.Charging = _battery.Charging;

        return s;
    }

    /// <summary>Apply what the game sent to the virtual DualSense back onto the Claw.</summary>
    public void ApplyFeedback(OutputFeedback fb)
    {
        if (!_cfg.RumblePassthrough || fb.MotorLeft is null) return;
        double k = Math.Clamp(_cfg.RumbleStrength, 0, 1) * 257.0;
        var v = new XInput.Vibration
        {
            LeftMotorSpeed = (ushort)Math.Clamp(fb.MotorLeft.Value * k, 0, 65535),
            RightMotorSpeed = (ushort)Math.Clamp((fb.MotorRight ?? 0) * k, 0, 65535),
        };
        XInput.SetState(_slot, ref v);
    }

    public void StopRumble()
    {
        var v = new XInput.Vibration();
        XInput.SetState(_slot, ref v);
    }
}
