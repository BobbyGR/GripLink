using System.Text;

namespace GripLink.DualSense;

public enum DualSenseModel { DualSense, DualSenseEdge }

/// <summary>
/// Usb       = byte-for-byte like a DualSense on a USB cable (composite device, HID on interface 3,
///             64-byte report 0x01, output 0x02). This is what PS Remote Play accepts.
/// Bluetooth = Bluetooth report protocol (simple 0x01 then 78-byte 0x31 with CRC).
/// </summary>
public enum DualSenseTransport { Usb, Bluetooth }

/// <summary>USB and HID descriptors for the virtual controller.</summary>
public static class Descriptors
{
    public const ushort SonyVid = 0x054C;
    public const ushort DualSensePid = 0x0CE6;
    public const ushort DualSenseEdgePid = 0x0DF2;

    public const byte InEndpoint = 0x84;  // interrupt IN  (EP 4)
    public const byte OutEndpoint = 0x03; // interrupt OUT (EP 3)
    public const ushort MaxPacket = 128;  // must hold the 78-byte BT report

    public static ushort Pid(DualSenseModel m) => m == DualSenseModel.DualSenseEdge ? DualSenseEdgePid : DualSensePid;

    public static string ProductName(DualSenseModel m) =>
        m == DualSenseModel.DualSenseEdge ? "DualSense Edge Wireless Controller" : "DualSense Wireless Controller";

    public const string Manufacturer = "Sony Interactive Entertainment";

    public static byte[] Device(DualSenseModel m)
    {
        ushort pid = Pid(m);
        return
        [
            18, 0x01,              // bLength, DEVICE
            0x00, 0x02,            // bcdUSB 2.00
            0x00, 0x00, 0x00,      // class/subclass/protocol defined per interface
            64,                    // bMaxPacketSize0
            unchecked((byte)SonyVid), (byte)(SonyVid >> 8),
            (byte)pid, (byte)(pid >> 8),
            0x00, 0x01,            // bcdDevice 1.00
            1, 2, 0,               // iManufacturer, iProduct, iSerial
            1,                     // bNumConfigurations
        ];
    }

    public static byte[] Configuration(int reportDescriptorLength)
    {
        const int total = 9 + 9 + 9 + 7 + 7;
        return
        [
            9, 0x02, total, 0x00, 1, 1, 0, 0xA0, 250,     // CONFIGURATION: 1 iface, bus powered + remote wakeup, 500 mA
            9, 0x04, 0, 0, 2, 0x03, 0x00, 0x00, 0,         // INTERFACE 0: HID, 2 endpoints
            9, 0x21, 0x11, 0x01, 0x00, 1, 0x22,            // HID 1.11, 1 descriptor: REPORT
                (byte)reportDescriptorLength, (byte)(reportDescriptorLength >> 8),
            7, 0x05, InEndpoint, 0x03, (byte)MaxPacket, (byte)(MaxPacket >> 8), 4,  // interrupt IN, 1 ms (HS)
            7, 0x05, OutEndpoint, 0x03, (byte)MaxPacket, (byte)(MaxPacket >> 8), 4, // interrupt OUT, 1 ms (HS)
        ];
    }

    /// <summary>
    /// Configuration descriptor captured from a real USB DualSense (high speed):
    ///   interface 0   audio control (speaker feature unit ID 2, mic feature unit ID 5)
    ///   interface 1   speaker streaming, alt 1: iso OUT EP 0x01, 4 ch, 16 bit, 48 kHz
    ///   interface 2   microphone streaming, alt 1: iso IN EP 0x82, 2 ch, 16 bit, 48 kHz
    ///   interface 3   HID, interrupt IN 0x84 / OUT 0x03, 64 bytes, every 4 ms
    /// </summary>
    public static byte[] ConfigurationUsb() => Convert.FromHexString(
        "0902E300040100C0FA0904000000010100000A2401000149000201020C240201" +
        "01010604330000000C24060201010300000000000924030301030402000C2402" +
        "0402040302030000000924060504010300000924030601010105000904010000" +
        "01020000090401010101020000072401010101000B2402010402100180BB0009" +
        "0501098801040000072501000000000904020000010200000904020101010200" +
        "00072401060101000B2402010202100180BB0009058205C40004000007250100" +
        "0000000904030002030000000921110100012221010705840340000607050303" +
        "400006");

    public const byte SpeakerFeatureUnit = 2, MicFeatureUnit = 5;
    public const int SpeakerEndpoint = 0x01, MicEndpoint = 0x02;
    /// <summary>Mic: 48 samples x 2 channels x 2 bytes per 1 ms packet.</summary>
    public const int MicBytesPerPacket = 192;

    /// <summary>The 9-byte HID class descriptor inside a configuration descriptor.</summary>
    public static byte[] HidClassDescriptor(byte[] configuration)
    {
        for (int i = 0; i + 1 < configuration.Length; i += configuration[i])
            if (configuration[i + 1] == 0x21) return configuration.AsSpan(i, 9).ToArray();
        throw new InvalidOperationException("No HID descriptor in configuration");
    }

    /// <summary>Feature reports of a real USB DualSense: (id, length without the ID byte).</summary>
    public static readonly (byte Id, int Length)[] UsbFeatureReports =
    [
        (0x05, 40), (0x08, 47), (0x09, 19), (0x0A, 26), (0x0B, 41), (0x0C, 41), (0x20, 63), (0x21, 4), (0x22, 63),
        (0x80, 63), (0x81, 63), (0x82, 9), (0x83, 63), (0x84, 63), (0x85, 2), (0xA0, 1),
        (0xE0, 63), (0xF0, 63), (0xF1, 63), (0xF2, 15), (0xF4, 63), (0xF5, 3),
    ];

    /// <summary>
    /// HID report descriptor captured from a real USB DualSense (289 bytes): input 0x01 (64 bytes),
    /// output 0x02 (48 bytes) and feature reports 0x05..0xF5 (max 64 bytes).
    /// </summary>
    public static byte[] HidReportUsb() => Convert.FromHexString(
        "05010905A1018501093009310932093509330934150026FF0075089506810206" +
        "00FF09209501810205010939150025073500463B016514750495018142650005" +
        "091901290F150025017501950F81020600FF0921950D81020600FF0922150026" +
        "FF0075089534810285020923952F9102850509339528B10285080934952FB102" +
        "850909249513B102850A0925951AB102850B09419529B102850C09429529B102" +
        "85200926953FB102852109279504B10285220940953FB10285800928953FB102" +
        "85810929953FB1028582092A9509B1028583092B953FB1028584092C953FB102" +
        "8585092D9502B10285A0092E9501B10285E0092F953FB10285F00930953FB102" +
        "85F10931953FB10285F20932950FB10285F40935953FB10285F509369503B102" +
        "C0");

    public static byte[] String(int index, DualSenseModel m)
    {
        if (index == 0) return [4, 0x03, 0x09, 0x04]; // English (US)
        string s = index switch { 1 => Manufacturer, 2 => ProductName(m), _ => "" };
        var body = Encoding.Unicode.GetBytes(s);
        var d = new byte[2 + body.Length];
        d[0] = (byte)d.Length; d[1] = 0x03;
        body.CopyTo(d, 2);
        return d;
    }

    /// <summary>
    /// HID report descriptor with the Bluetooth DualSense report layout:
    /// 0x01 simple gamepad input (10 bytes), 0x31 vendor input/output (78 bytes),
    /// and the feature reports games read (0x05 calibration, 0x09 pairing, 0x20 firmware).
    /// Report lengths below exclude the report ID byte.
    /// </summary>
    public static byte[] HidReport()
    {
        var b = new List<byte>();
        void Add(params byte[] bytes) => b.AddRange(bytes);

        Add(0x05, 0x01);             // Usage Page (Generic Desktop)
        Add(0x09, 0x05);             // Usage (Game Pad)
        Add(0xA1, 0x01);             // Collection (Application)

        // ---- Report 0x01: simple-mode gamepad ----
        Add(0x85, 0x01);             //   Report ID 1
        Add(0x09, 0x30, 0x09, 0x31, 0x09, 0x32, 0x09, 0x35); // X, Y, Z, Rz (LX, LY, RX, RY)
        Add(0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x04, 0x81, 0x02);
        Add(0x09, 0x39);             //   Hat switch
        Add(0x15, 0x00, 0x25, 0x07, 0x35, 0x00, 0x46, 0x3B, 0x01, 0x65, 0x14);
        Add(0x75, 0x04, 0x95, 0x01, 0x81, 0x42); // 4 bits, null state
        Add(0x65, 0x00);             //   Unit (none)
        Add(0x05, 0x09, 0x19, 0x01, 0x29, 0x0E); // Buttons 1..14
        Add(0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x0E, 0x81, 0x02);
        Add(0x06, 0x00, 0xFF, 0x09, 0x20); // vendor: 6-bit counter
        Add(0x75, 0x06, 0x95, 0x01, 0x15, 0x00, 0x25, 0x7F, 0x81, 0x02);
        Add(0x05, 0x01, 0x09, 0x33, 0x09, 0x34); // Rx, Ry (L2, R2 analog)
        Add(0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x02, 0x81, 0x02);

        // ---- Vendor reports (Bluetooth enhanced mode) ----
        Add(0x06, 0x00, 0xFF);       //   Usage Page (Vendor 0xFF00)
        Add(0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08);
        void Vendor(byte id, int length, byte main)
        {
            Add(0x85, id, 0x09, id);
            if (length < 256) Add(0x95, (byte)length); else Add(0x96, (byte)length, (byte)(length >> 8));
            Add(main, 0x02);
        }
        Vendor(DualSenseReports.BtInputId, DualSenseReports.BtInputLength - 1, 0x81);        // Input 0x31
        Vendor(DualSenseReports.BtOutputId, DualSenseReports.BtOutputLength - 1, 0x91);      // Output 0x31
        Vendor(DualSenseReports.FeatureCalibration, DualSenseReports.CalibrationLength - 1, 0xB1);
        Vendor(DualSenseReports.FeaturePairingInfo, DualSenseReports.PairingInfoLength - 1, 0xB1);
        Vendor(DualSenseReports.FeatureFirmwareInfo, DualSenseReports.FirmwareInfoLength - 1, 0xB1);

        Add(0xC0);                   // End Collection
        return b.ToArray();
    }
}
