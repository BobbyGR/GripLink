using System.Diagnostics;
using GripLink.UsbIp;

namespace GripLink.DualSense;

/// <summary>
/// A virtual DualSense exported as a USB/IP device.
///  * Usb transport: a byte-for-byte copy of a real USB DualSense (descriptors captured from
///    real hardware), including its USB audio speaker/microphone interfaces.
///  * Bluetooth transport: the Bluetooth report protocol (0x01 simple -> 0x31 enhanced, CRC).
/// </summary>
public sealed class VirtualDualSense : IUsbIpDevice
{
    private readonly Func<DualSenseState> _readState;
    private readonly DualSenseModel _model;
    private readonly DualSenseTransport _transport;
    private readonly HashSet<byte> _featuresSeen = new();
    private readonly bool _startEnhanced;
    private readonly byte _triggerThreshold;
    private readonly Action<string> _log;

    private readonly byte[] _deviceDesc, _configDesc, _hidDesc, _reportDesc;
    private readonly Dictionary<byte, byte[]> _features = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private volatile bool _enhanced;
    private byte _seq, _simpleCounter, _configuration;

    // Audio state (Usb transport)
    private readonly byte[] _altSetting = new byte[4];
    private readonly Dictionary<(int Unit, int Control), short> _audio = new();
    private readonly Dictionary<int, byte[]> _sampleRate = new();

    public event Action<OutputFeedback>? Output;
    public event Action<bool>? ConnectionChanged;
    /// <summary>An app (game, Remote Play, Steam) read the controller's feature reports.</summary>
    public event Action? HostActivity;

    public VirtualDualSense(Func<DualSenseState> readState, DualSenseModel model, DualSenseTransport transport, byte[] mac,
                            TimeSpan interval, bool startEnhanced, byte triggerThreshold, Action<string> log)
    {
        _readState = readState;
        _model = model;
        _transport = transport;
        _startEnhanced = startEnhanced;
        _triggerThreshold = triggerThreshold;
        _log = log;
        InterruptInterval = interval;

        bool usb = transport == DualSenseTransport.Usb;
        _reportDesc = usb ? Descriptors.HidReportUsb() : Descriptors.HidReport();
        _deviceDesc = Descriptors.Device(model);
        _configDesc = usb ? Descriptors.ConfigurationUsb() : Descriptors.Configuration(_reportDesc.Length);
        _hidDesc = Descriptors.HidClassDescriptor(_configDesc);

        if (usb)
        {
            foreach (var (id, len) in Descriptors.UsbFeatureReports)
            {
                var r = new byte[len + 1];
                r[0] = id;
                _features[id] = r;
            }
            var cal = DualSenseReports.BuildCalibration();
            cal.AsSpan(^4).Clear(); // no CRC over USB
            _features[0x05] = cal;
            _features[0x09] = DualSenseReports.BuildUsbPairingInfo(mac);
            _features[0x0B] = DualSenseReports.BuildUsbPairingInfo2(mac);
            _features[0x20] = DualSenseReports.BuildUsbFirmwareInfo();

            _audio[(Descriptors.SpeakerFeatureUnit, 1)] = 0;         // mute off
            _audio[(Descriptors.SpeakerFeatureUnit, 2)] = 0;         // 0 dB
            _audio[(Descriptors.MicFeatureUnit, 1)] = 0;
            _audio[(Descriptors.MicFeatureUnit, 2)] = 0;
        }
        else
        {
            _features[0x05] = DualSenseReports.BuildCalibration();
            _features[0x09] = DualSenseReports.BuildPairingInfo(mac);
            _features[0x20] = DualSenseReports.BuildFirmwareInfo();
        }

        Info = new UsbIpDeviceInfo(
            BusId: "1-1", BusNum: 1, DevNum: 2, Speed: 3 /* high speed, like the real controller */,
            VendorId: Descriptors.SonyVid, ProductId: Descriptors.Pid(model), BcdDevice: 0x0100,
            DeviceClass: 0, DeviceSubClass: 0, DeviceProtocol: 0,
            ConfigurationValue: 1, NumConfigurations: 1,
            Interfaces: usb
                ? [(0x01, 0x01, 0x00), (0x01, 0x02, 0x00), (0x01, 0x02, 0x00), (0x03, 0x00, 0x00)]
                : [(0x03, 0x00, 0x00)]);
    }

    public DualSenseTransport Transport => _transport;
    public UsbIpDeviceInfo Info { get; }
    public TimeSpan InterruptInterval { get; }
    public bool Enhanced => _enhanced;

    /// <summary>Log every control request (shows when any app opens/queries the controller).</summary>
    public bool TraceControl { get; set; }

    public void OnAttached()
    {
        _enhanced = _startEnhanced;
        Array.Clear(_altSetting);
        lock (_featuresSeen) _featuresSeen.Clear();
        if (_transport == DualSenseTransport.Usb)
            _log("Controller connected (USB DualSense with speaker + microphone, 64-byte report 0x01)");
        else
            _log(_enhanced ? "Controller connected (Bluetooth, enhanced mode)" : "Controller connected (Bluetooth, simple mode until the host reads calibration)");
        ConnectionChanged?.Invoke(true);
    }

    public void OnDetached() => ConnectionChanged?.Invoke(false);

    private void SwitchToEnhanced(string why)
    {
        if (_enhanced) return;
        _enhanced = true;
        _log($"Switched to Bluetooth enhanced mode (report 0x31) after {why}");
    }

    // ------------------------------------------------------------------ interrupt endpoints

    public byte[] InterruptIn(int endpoint)
    {
        var state = _readState();
        bool edge = _model == DualSenseModel.DualSenseEdge;
        // Sensor timestamp unit is 1/3 microsecond (wraps at 32 bits like the real counter).
        uint ts = unchecked((uint)(ulong)(_clock.Elapsed.TotalMicroseconds * 3));

        if (_transport == DualSenseTransport.Usb)
            return DualSenseReports.BuildUsbInput(state, _seq++, ts, edge, _triggerThreshold);

        if (!_enhanced)
            return DualSenseReports.BuildSimpleInput(state, _simpleCounter++, _triggerThreshold);
        return DualSenseReports.BuildBtInput(state, _seq++, ts, edge, _triggerThreshold);
    }

    private byte[] _lastSent = [];

    public byte[]? PollInterruptIn(int endpoint, bool force)
    {
        byte seq = _seq, counter = _simpleCounter;
        var report = InterruptIn(endpoint);
        if (!force && !InputChanged(_lastSent, report))
        {
            _seq = seq; _simpleCounter = counter; // not sent: keep counters contiguous
            return null;
        }
        _lastSent = report;
        return report;
    }

    /// <summary>Compares sticks, triggers, buttons and touch; ignores counters, timestamps and motion noise.</summary>
    private bool InputChanged(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return true;
        int o = _transport == DualSenseTransport.Usb || !_enhanced ? 1 : 2; // start of payload
        if (!_enhanced && _transport == DualSenseTransport.Bluetooth)
            return !a.AsSpan(1, 7).SequenceEqual(b.AsSpan(1, 7)) || a[8] != b[8] || a[9] != b[9] || (a[7] & 0x03) != (b[7] & 0x03);
        return !a.AsSpan(o, 6).SequenceEqual(b.AsSpan(o, 6))          // sticks + triggers
            || !a.AsSpan(o + 7, 3).SequenceEqual(b.AsSpan(o + 7, 3))  // buttons
            || !a.AsSpan(o + 32, 8).SequenceEqual(b.AsSpan(o + 32, 8)); // touch points
    }

    public void InterruptOut(int endpoint, ReadOnlySpan<byte> data) => HandleOutputReport(data);

    private void HandleOutputReport(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        if (data[0] == DualSenseReports.BtOutputId) SwitchToEnhanced("output report 0x31");
        var fb = OutputFeedback.Parse(data);
        if (fb is not null) Output?.Invoke(fb);
    }

    // ------------------------------------------------------------------ isochronous audio

    /// <summary>Receives speaker audio Windows sends to the controller: 48 kHz, 4 channels, 16-bit.</summary>
    public AudioSink? SpeakerSink { get; set; }

    public delegate void AudioFill(Span<byte> destination);
    public delegate void AudioSink(ReadOnlySpan<byte> data);

    /// <summary>Fills microphone packets: 48 kHz, 2 channels, 16-bit. Null = silence.</summary>
    public AudioFill? MicSource { get; set; }

    public void IsoOut(int endpoint, ReadOnlySpan<byte> data)
    {
        if (endpoint == Descriptors.SpeakerEndpoint && data.Length > 0) SpeakerSink?.Invoke(data);
    }

    public int IsoIn(int endpoint, Span<byte> packet)
    {
        if (endpoint != Descriptors.MicEndpoint) return 0;
        int n = Math.Min(packet.Length, Descriptors.MicBytesPerPacket);
        var mic = MicSource;
        if (mic is not null) mic(packet[..n]);
        else packet[..n].Clear();
        return n;
    }

    // ------------------------------------------------------------------ control endpoint

    public UrbResult Control(SetupPacket s, ReadOnlySpan<byte> outData)
    {
        var result = ControlCore(s, outData);
        if (TraceControl)
            _log($"ctrl {s} -> {(result.Status == 0 ? $"{result.Data.Length} bytes" : "STALL")}");
        return result;
    }

    private UrbResult ControlCore(SetupPacket s, ReadOnlySpan<byte> outData)
    {
        int recipient = s.RequestType & 0x1F;
        int kind = (s.RequestType >> 5) & 0x03; // 0 standard, 1 class
        int iface = s.Index & 0xFF;

        // Audio class requests (interfaces 0-2 or the iso endpoints).
        if (_transport == DualSenseTransport.Usb && kind == 1)
        {
            if (recipient == 1 && iface != 3) return AudioUnitRequest(s, outData);
            if (recipient == 2) return AudioEndpointRequest(s, outData);
        }

        switch (s.RequestType, s.Request)
        {
            // ---- standard requests ----
            case (0x80, 0x06): return GetDescriptor(s);
            case (0x81, 0x06): return GetInterfaceDescriptor(s);
            case (0x80, 0x00): return UrbResult.Ok([0x00, 0x00]);           // GET_STATUS (device)
            case (0x81, 0x00): return UrbResult.Ok([0x00, 0x00]);           // GET_STATUS (interface)
            case (0x82, 0x00): return UrbResult.Ok([0x00, 0x00]);           // GET_STATUS (endpoint)
            case (0x80, 0x08): return UrbResult.Ok([_configuration]);      // GET_CONFIGURATION
            case (0x00, 0x09): _configuration = (byte)s.Value; return UrbResult.Ok(); // SET_CONFIGURATION
            case (0x00, 0x05): return UrbResult.Ok();                       // SET_ADDRESS
            case (0x00, 0x01): case (0x00, 0x03): return UrbResult.Ok();    // CLEAR/SET_FEATURE (device)
            case (0x02, 0x01): case (0x02, 0x03): return UrbResult.Ok();    // CLEAR/SET_FEATURE (endpoint)
            case (0x81, 0x0A):                                              // GET_INTERFACE
                return UrbResult.Ok([iface < _altSetting.Length ? _altSetting[iface] : (byte)0]);
            case (0x01, 0x0B):                                              // SET_INTERFACE
                if (iface < _altSetting.Length)
                {
                    if (_altSetting[iface] != (byte)s.Value && iface is 1 or 2)
                        _log($"Audio {(iface == 1 ? "speaker" : "microphone")} stream {(s.Value != 0 ? "started" : "stopped")}");
                    _altSetting[iface] = (byte)s.Value;
                }
                return UrbResult.Ok();

            // ---- HID class requests (interface 3, or the only interface over Bluetooth) ----
            case (0x21, 0x0A): return UrbResult.Ok();                       // SET_IDLE
            case (0x21, 0x0B): return UrbResult.Ok();                       // SET_PROTOCOL
            case (0xA1, 0x02): return UrbResult.Ok([0x00]);                 // GET_IDLE
            case (0xA1, 0x03): return UrbResult.Ok([0x01]);                 // GET_PROTOCOL (report)
            case (0xA1, 0x01): return GetReport(s);
            case (0x21, 0x09): return SetReport(s, outData);
        }
        _log($"Unhandled control request: {s}");
        return UrbResult.Stall;
    }

    /// <summary>UAC1 feature unit requests: mute (CS 1, 1 byte) and volume (CS 2, 2 bytes, 1/256 dB).</summary>
    private UrbResult AudioUnitRequest(SetupPacket s, ReadOnlySpan<byte> data)
    {
        int unit = s.Index >> 8, cs = s.Value >> 8;
        if (unit != Descriptors.SpeakerFeatureUnit && unit != Descriptors.MicFeatureUnit) return UrbResult.Stall;
        if (cs is not (1 or 2)) return UrbResult.Stall;
        bool isIn = (s.RequestType & 0x80) != 0;

        if (!isIn)
        {
            if (s.Request != 0x01) return UrbResult.Stall; // SET_CUR only
            short v = cs == 1 ? (data.Length > 0 ? data[0] : (short)0) : (short)(data.Length >= 2 ? data[0] | data[1] << 8 : 0);
            lock (_audio) _audio[(unit, cs)] = v;
            return UrbResult.Ok();
        }

        short value = s.Request switch
        {
            0x81 => GetAudio(unit, cs),          // GET_CUR
            0x82 => cs == 1 ? (short)0 : unchecked((short)0xC400), // GET_MIN: -60 dB
            0x83 => cs == 1 ? (short)1 : (short)0,                  // GET_MAX: 0 dB
            0x84 => cs == 1 ? (short)1 : (short)0x0100,             // GET_RES: 1 dB
            _ => short.MinValue,
        };
        if (value == short.MinValue && s.Request != 0x81) return UrbResult.Stall;
        return cs == 1 ? UrbResult.Ok([(byte)value]) : UrbResult.Ok([(byte)value, (byte)(value >> 8)]);
    }

    private short GetAudio(int unit, int cs) { lock (_audio) return _audio.TryGetValue((unit, cs), out var v) ? v : (short)0; }

    /// <summary>UAC1 endpoint requests: sampling frequency (3 bytes, 48000 Hz).</summary>
    private UrbResult AudioEndpointRequest(SetupPacket s, ReadOnlySpan<byte> data)
    {
        int ep = s.Index & 0x0F;
        if ((s.Value >> 8) != 0x01) return UrbResult.Stall;
        if ((s.RequestType & 0x80) == 0)
        {
            lock (_sampleRate) _sampleRate[ep] = data.ToArray();
            return UrbResult.Ok();
        }
        lock (_sampleRate)
            return UrbResult.Ok(_sampleRate.TryGetValue(ep, out var r) && r.Length == 3 ? r : [0x80, 0xBB, 0x00]);
    }

    private UrbResult GetDescriptor(SetupPacket s)
    {
        int type = s.Value >> 8, index = s.Value & 0xFF;
        return type switch
        {
            0x01 => UrbResult.Ok(_deviceDesc),
            0x02 => UrbResult.Ok(_configDesc),
            0x03 => index <= 2 ? UrbResult.Ok(Descriptors.String(index, _model)) : UrbResult.Stall,
            0x22 => UrbResult.Ok(_reportDesc),
            0x21 => UrbResult.Ok(_hidDesc),
            _ => UrbResult.Stall, // device qualifier, BOS, etc.: not supported -> STALL is valid
        };
    }

    private UrbResult GetInterfaceDescriptor(SetupPacket s) => (s.Value >> 8) switch
    {
        0x22 => UrbResult.Ok(_reportDesc),
        0x21 => UrbResult.Ok(_hidDesc),
        _ => UrbResult.Stall,
    };

    private UrbResult GetReport(SetupPacket s)
    {
        int type = s.Value >> 8;
        byte id = (byte)s.Value;
        if (type == 0x03) // feature
        {
            HostActivity?.Invoke();
            bool first;
            lock (_featuresSeen) first = _featuresSeen.Add(id);
            if (first) _log($"Host read feature report 0x{id:X2} (an app is talking to the controller)");

            if (id == DualSenseReports.FeatureCalibration && _transport == DualSenseTransport.Bluetooth)
                SwitchToEnhanced("host read calibration (feature 0x05)");
            return _features.TryGetValue(id, out var r) ? UrbResult.Ok(r) : UrbResult.Stall;
        }
        if (type == 0x01) // input
            return UrbResult.Ok(InterruptIn(Descriptors.InEndpoint & 0x0F));
        return UrbResult.Stall;
    }

    private UrbResult SetReport(SetupPacket s, ReadOnlySpan<byte> data)
    {
        int type = s.Value >> 8;
        if (type == 0x02) { HandleOutputReport(data); return UrbResult.Ok(); }
        if (type == 0x03) { _log($"Host wrote feature report 0x{(byte)s.Value:X2} ({data.Length} bytes) - ignored"); return UrbResult.Ok(); }
        return UrbResult.Stall;
    }
}
