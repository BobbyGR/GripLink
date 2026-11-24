using System.Buffers.Binary;
using System.Text;

namespace GripLink.DualSense;

/// <summary>
/// Builds DualSense reports exactly as a real controller sends them over Bluetooth.
///
/// Bluetooth behaviour being emulated:
///  * After pairing, the controller starts in "simple" mode and sends a 10-byte report 0x01.
///  * Once the host reads feature report 0x05 (calibration) or sends output report 0x31,
///    it switches to "enhanced" mode and sends 78-byte report 0x31
///    (sticks, buttons, gyro, accel, touchpad, battery and a trailing CRC-32).
///  * Feature reports carry a CRC-32 as well (seed 0xA3).
/// The input/output sizes (10 and 78) are what SDL, Steam and the Linux hid-playstation
/// driver use to tell that a DualSense is connected over Bluetooth rather than USB (64 bytes).
/// </summary>
public static class DualSenseReports
{
    public const byte SimpleInputId = 0x01;
    public const byte BtInputId = 0x31;
    public const byte BtOutputId = 0x31;
    public const byte UsbOutputId = 0x02;

    public const byte FeatureCalibration = 0x05;
    public const byte FeaturePairingInfo = 0x09;
    public const byte FeatureFirmwareInfo = 0x20;

    public const int SimpleInputLength = 10;
    public const int BtInputLength = 78;
    public const int BtOutputLength = 78;
    public const int CommonInputLength = 63;
    public const int CalibrationLength = 41;
    public const int PairingInfoLength = 20;
    public const int FirmwareInfoLength = 64;

    // Calibration values we advertise. They make the raw units simple:
    //   gyro : 16 raw = 1 deg/s   ( (540+540) / (8640 - -8640) = 1/16 )
    //   accel: 8192 raw = 1 g     ( 2g / (8192 - -8192) )
    public const short GyroPlus = 8640, GyroMinus = -8640, GyroSpeed = 540;
    public const short AccelPlus = 8192, AccelMinus = -8192;
    public const double GyroRawPerDegS = 16.0;
    public const double AccelRawPerG = 8192.0;

    private static byte Buttons0(DualSenseState s) =>
        (byte)((byte)s.ComputeDPad()
             | (s.Has(DsButton.Square) ? 0x10 : 0)
             | (s.Has(DsButton.Cross) ? 0x20 : 0)
             | (s.Has(DsButton.Circle) ? 0x40 : 0)
             | (s.Has(DsButton.Triangle) ? 0x80 : 0));

    private static byte Buttons1(DualSenseState s, byte triggerThreshold) =>
        (byte)((s.Has(DsButton.L1) ? 0x01 : 0)
             | (s.Has(DsButton.R1) ? 0x02 : 0)
             | (s.L2 > triggerThreshold ? 0x04 : 0)
             | (s.R2 > triggerThreshold ? 0x08 : 0)
             | (s.Has(DsButton.Create) ? 0x10 : 0)
             | (s.Has(DsButton.Options) ? 0x20 : 0)
             | (s.Has(DsButton.L3) ? 0x40 : 0)
             | (s.Has(DsButton.R3) ? 0x80 : 0));

    private static byte Buttons2(DualSenseState s, bool edge) =>
        (byte)((s.Has(DsButton.PS) ? 0x01 : 0)
             | (s.Has(DsButton.TouchpadClick) ? 0x02 : 0)
             | (s.Has(DsButton.Mute) ? 0x04 : 0)
             | (edge && s.Has(DsButton.EdgeFnL) ? 0x10 : 0)
             | (edge && s.Has(DsButton.EdgeFnR) ? 0x20 : 0)
             | (edge && s.Has(DsButton.EdgeBackL) ? 0x40 : 0)
             | (edge && s.Has(DsButton.EdgeBackR) ? 0x80 : 0));

    private static void WriteTouch(Span<byte> d, TouchPoint p)
    {
        int x = Math.Clamp((int)p.X, 0, 1919);
        int y = Math.Clamp((int)p.Y, 0, 1079);
        d[0] = (byte)((p.Active ? 0x00 : 0x80) | (p.Id & 0x7F)); // bit 7 set = NOT touching
        d[1] = (byte)(x & 0xFF);
        d[2] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        d[3] = (byte)((y >> 4) & 0xFF);
    }

    /// <summary>The 63-byte payload shared by USB report 0x01 and BT report 0x31.</summary>
    public static void WriteCommonInput(Span<byte> d, DualSenseState s, byte seq, uint sensorTimestamp,
                                        bool edge, byte triggerThreshold)
    {
        d[..CommonInputLength].Clear();
        d[0] = s.LX; d[1] = s.LY; d[2] = s.RX; d[3] = s.RY;
        d[4] = s.L2; d[5] = s.R2;
        d[6] = seq;
        d[7] = Buttons0(s);
        d[8] = Buttons1(s, triggerThreshold);
        d[9] = Buttons2(s, edge);
        // d[10] reserved, d[11..14] reserved
        BinaryPrimitives.WriteInt16LittleEndian(d[15..], s.GyroX);
        BinaryPrimitives.WriteInt16LittleEndian(d[17..], s.GyroY);
        BinaryPrimitives.WriteInt16LittleEndian(d[19..], s.GyroZ);
        BinaryPrimitives.WriteInt16LittleEndian(d[21..], s.AccelX);
        BinaryPrimitives.WriteInt16LittleEndian(d[23..], s.AccelY);
        BinaryPrimitives.WriteInt16LittleEndian(d[25..], s.AccelZ);
        BinaryPrimitives.WriteUInt32LittleEndian(d[27..], sensorTimestamp);
        // d[31] reserved (temperature)
        WriteTouch(d[32..], s.Touch0);
        WriteTouch(d[36..], s.Touch1);
        // d[40..51] reserved
        int level = Math.Clamp((s.BatteryPercent + 5) / 10, 0, 10);
        int charge = s.Charging ? (s.BatteryPercent >= 100 ? 2 : 1) : 0;
        d[52] = (byte)((charge << 4) | level);
    }

    /// <summary>Enhanced-mode Bluetooth input report 0x31 (78 bytes, CRC sealed).</summary>
    public static byte[] BuildBtInput(DualSenseState s, byte seq, uint sensorTimestamp, bool edge, byte triggerThreshold)
    {
        var r = new byte[BtInputLength];
        r[0] = BtInputId;
        r[1] = (byte)(seq << 4); // BT sequence tag
        WriteCommonInput(r.AsSpan(2), s, seq, sensorTimestamp, edge, triggerThreshold);
        Crc32.Seal(Crc32.InputSeed, r);
        return r;
    }

    public const int UsbInputLength = 64;

    /// <summary>USB input report 0x01 (64 bytes): report ID + the common payload.</summary>
    public static byte[] BuildUsbInput(DualSenseState s, byte seq, uint sensorTimestamp, bool edge, byte triggerThreshold)
    {
        var r = new byte[UsbInputLength];
        r[0] = SimpleInputId;
        WriteCommonInput(r.AsSpan(1), s, seq, sensorTimestamp, edge, triggerThreshold);
        return r;
    }

    /// <summary>Simple-mode Bluetooth input report 0x01 (10 bytes, DS4-like, no CRC).</summary>
    public static byte[] BuildSimpleInput(DualSenseState s, byte counter, byte triggerThreshold)
    {
        var r = new byte[SimpleInputLength];
        r[0] = SimpleInputId;
        r[1] = s.LX; r[2] = s.LY; r[3] = s.RX; r[4] = s.RY;
        r[5] = Buttons0(s);
        r[6] = Buttons1(s, triggerThreshold);
        r[7] = (byte)((Buttons2(s, false) & 0x03) | (counter << 2));
        r[8] = s.L2; r[9] = s.R2;
        return r;
    }

    public static byte[] BuildCalibration()
    {
        var r = new byte[CalibrationLength];
        var d = r.AsSpan();
        d[0] = FeatureCalibration;
        void W(int off, short v) => BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(off), v);
        W(1, 0); W(3, 0); W(5, 0);                 // gyro pitch/yaw/roll bias
        W(7, GyroPlus); W(9, GyroMinus);           // pitch +/-
        W(11, GyroPlus); W(13, GyroMinus);         // yaw +/-
        W(15, GyroPlus); W(17, GyroMinus);         // roll +/-
        W(19, GyroSpeed); W(21, GyroSpeed);        // speed +/-
        W(23, AccelPlus); W(25, AccelMinus);       // accel x +/-
        W(27, AccelPlus); W(29, AccelMinus);       // accel y +/-
        W(31, AccelPlus); W(33, AccelMinus);       // accel z +/-
        Crc32.Seal(Crc32.FeatureSeed, r);
        return r;
    }

    public static byte[] BuildPairingInfo(byte[] mac)
    {
        var r = new byte[PairingInfoLength];
        r[0] = FeaturePairingInfo;
        for (int i = 0; i < 6; i++) r[1 + i] = mac[5 - i]; // stored little endian
        Crc32.Seal(Crc32.FeatureSeed, r);
        return r;
    }

    public static byte[] BuildFirmwareInfo()
    {
        var r = new byte[FirmwareInfoLength];
        r[0] = FeatureFirmwareInfo;
        Encoding.ASCII.GetBytes("Jun 10 2024").CopyTo(r, 1);   // build date (11 chars)
        Encoding.ASCII.GetBytes("12:00:00").CopyTo(r, 12);     // build time (8 chars)
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(20), 0x0004); // fw type
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(22), 0x0001); // sw series
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), 0x00000617); // hardware info
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), 0x01060000); // firmware version
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(44), 0x0630);     // update version (>= 2.21 -> rumble v2)
        Crc32.Seal(Crc32.FeatureSeed, r);
        return r;
    }

    // ---- USB feature reports, laid out like a real USB DualSense (no CRC) ----

    private static readonly byte[] FakeHostMac = [0x02, 0x00, 0x00, 0x5C, 0x1A, 0x00]; // little endian

    /// <summary>0x09 (20 bytes): controller MAC, 08 25 00, paired host MAC.</summary>
    public static byte[] BuildUsbPairingInfo(byte[] mac)
    {
        var r = new byte[20];
        r[0] = 0x09;
        for (int i = 0; i < 6; i++) r[1 + i] = mac[5 - i];
        r[7] = 0x08; r[8] = 0x25; r[9] = 0x00;
        FakeHostMac.CopyTo(r, 10);
        return r;
    }

    /// <summary>0x0B (42 bytes): controller MAC, 08 25 00 00, host MAC; link data left zero.</summary>
    public static byte[] BuildUsbPairingInfo2(byte[] mac)
    {
        var r = new byte[42];
        r[0] = 0x0B;
        for (int i = 0; i < 6; i++) r[1 + i] = mac[5 - i];
        r[7] = 0x08; r[8] = 0x25;
        FakeHostMac.CopyTo(r, 11);
        return r;
    }

    /// <summary>0x20 (64 bytes): firmware info copied from a real controller (Jul 4 2025 firmware).</summary>
    public static byte[] BuildUsbFirmwareInfo() => Convert.FromHexString(
        "204A756C202034203230323531303A31303A333202000400140500002A001001" +
        "C1480000000000000000000030060000380001000A0002000600000000000000");

    public static byte[] ParseMac(string text)
    {
        var parts = text.Split(':', '-');
        if (parts.Length != 6) throw new FormatException($"Bad MAC address '{text}'");
        return parts.Select(p => Convert.ToByte(p, 16)).ToArray();
    }
}

/// <summary>Everything a game asked the controller to do in one output report.</summary>
public sealed class OutputFeedback
{
    public bool CrcValid = true;
    public byte? MotorLeft, MotorRight;          // 0..255 (left = heavy/low frequency)
    public (byte R, byte G, byte B)? Lightbar;
    public byte? PlayerLeds;
    public bool? MuteLed;
    public byte[]? RightTriggerEffect, LeftTriggerEffect; // 11 bytes each (adaptive triggers)

    public static OutputFeedback? Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length < 2) return null;
        int common;
        var fb = new OutputFeedback();
        if (report[0] == DualSenseReports.BtOutputId)
        {
            common = 3;
            if (report.Length >= DualSenseReports.BtOutputLength)
                fb.CrcValid = Crc32.Verify(Crc32.OutputSeed, report[..DualSenseReports.BtOutputLength]);
        }
        else if (report[0] == DualSenseReports.UsbOutputId) common = 1;
        else return null;

        var c = report[common..];
        if (c.Length < 47) return null;

        byte f0 = c[0], f1 = c[1], f2 = c[38];
        if ((f0 & 0x03) != 0 || (f2 & 0x04) != 0) { fb.MotorRight = c[2]; fb.MotorLeft = c[3]; }
        if ((f0 & 0x04) != 0) fb.RightTriggerEffect = c.Slice(10, 11).ToArray();
        if ((f0 & 0x08) != 0) fb.LeftTriggerEffect = c.Slice(21, 11).ToArray();
        if ((f1 & 0x01) != 0) fb.MuteLed = c[8] != 0;
        if ((f1 & 0x04) != 0) fb.Lightbar = (c[44], c[45], c[46]);
        if ((f1 & 0x10) != 0) fb.PlayerLeds = c[43];
        return fb;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (MotorLeft is not null) parts.Add($"rumble L={MotorLeft} R={MotorRight}");
        if (Lightbar is { } lb) parts.Add($"lightbar #{lb.R:X2}{lb.G:X2}{lb.B:X2}");
        if (PlayerLeds is not null) parts.Add($"player LEDs 0x{PlayerLeds:X2}");
        if (MuteLed is not null) parts.Add($"mute LED {(MuteLed.Value ? "on" : "off")}");
        if (RightTriggerEffect is not null) parts.Add($"R2 effect {Convert.ToHexString(RightTriggerEffect)}");
        if (LeftTriggerEffect is not null) parts.Add($"L2 effect {Convert.ToHexString(LeftTriggerEffect)}");
        if (!CrcValid) parts.Add("(bad CRC)");
        return parts.Count == 0 ? "(no changes)" : string.Join(", ", parts);
    }
}
