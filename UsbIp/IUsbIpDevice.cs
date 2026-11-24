namespace GripLink.UsbIp;

public readonly record struct SetupPacket(byte RequestType, byte Request, ushort Value, ushort Index, ushort Length)
{
    public static SetupPacket Parse(ReadOnlySpan<byte> s) =>
        new(s[0], s[1], (ushort)(s[2] | s[3] << 8), (ushort)(s[4] | s[5] << 8), (ushort)(s[6] | s[7] << 8));

    public override string ToString() =>
        $"bmRequestType=0x{RequestType:X2} bRequest=0x{Request:X2} wValue=0x{Value:X4} wIndex=0x{Index:X4} wLength={Length}";
}

/// <summary>Result of one transfer. Status is a negative Linux errno (0 = success).</summary>
public readonly record struct UrbResult(int Status, byte[] Data)
{
    public const int EPIPE = -32;      // STALL
    public const int ECONNRESET = -104;

    public static UrbResult Ok(byte[]? data = null) => new(0, data ?? []);
    public static UrbResult Stall => new(EPIPE, []);
}

public sealed record UsbIpDeviceInfo(
    string BusId, uint BusNum, uint DevNum, uint Speed,
    ushort VendorId, ushort ProductId, ushort BcdDevice,
    byte DeviceClass, byte DeviceSubClass, byte DeviceProtocol,
    byte ConfigurationValue, byte NumConfigurations,
    (byte Class, byte SubClass, byte Protocol)[] Interfaces);

/// <summary>A USB device that can be exported over USB/IP.</summary>
public interface IUsbIpDevice
{
    UsbIpDeviceInfo Info { get; }

    /// <summary>Control transfer on endpoint 0.</summary>
    UrbResult Control(SetupPacket setup, ReadOnlySpan<byte> outData);

    /// <summary>Data for the next interrupt IN transfer on the given endpoint number.</summary>
    byte[] InterruptIn(int endpoint);

    /// <summary>
    /// Called about every millisecond while an interrupt IN transfer is waiting.
    /// Returns a report when the input changed (or when <paramref name="force"/> is set
    /// because the regular interval elapsed); null means "nothing new yet".
    /// </summary>
    byte[]? PollInterruptIn(int endpoint, bool force);

    /// <summary>Interrupt OUT transfer.</summary>
    void InterruptOut(int endpoint, ReadOnlySpan<byte> data);

    /// <summary>Isochronous OUT data (all packets concatenated).</summary>
    void IsoOut(int endpoint, ReadOnlySpan<byte> data);

    /// <summary>Fills one isochronous IN packet (packet.Length = max requested); returns bytes written.</summary>
    int IsoIn(int endpoint, Span<byte> packet);

    /// <summary>How often interrupt IN transfers are completed.</summary>
    TimeSpan InterruptInterval { get; }

    void OnAttached();
    void OnDetached();
}
