using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GripLink.Audio;

public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// Connects the virtual controller's audio to real Windows devices:
///  * microphone: captures a real mic (e.g. the Claw's) and feeds it to the controller's mic
///  * speaker: plays what Windows sends to the controller's speaker on a real output
/// </summary>
internal sealed class AudioBridge : IDisposable
{
    public const int Rate = 48000;
    private const int MicFrame = 4;      // 2 ch x 16 bit
    private const int SpeakerFrame = 8;  // 4 ch x 16 bit

    private readonly Action<string> _log;
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private IWaveProvider? _captureConverted;
    private readonly ByteRing _micRing = new(Rate * MicFrame, Rate * MicFrame / 50 /*20 ms*/, Rate * MicFrame / 12 /*~80 ms*/, MicFrame);
    private volatile float _micGain = 1f;
    private volatile bool _micMuted;

    private WasapiOut? _output;
    private BufferedWaveProvider? _outputBuffer;
    private byte[] _stereo = new byte[4096];

    public AudioBridge(Action<string> log) => _log = log;

    public static bool IsVirtualController(MMDevice d) =>
        d.FriendlyName.Contains("DualSense", StringComparison.OrdinalIgnoreCase) ||
        d.FriendlyName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase);

    public static List<AudioDeviceInfo> ListDevices(DataFlow flow)
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(flow, DeviceState.Active)
                 .Where(d => !IsVirtualController(d))
                 .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
                 .ToList();
    }

    private static MMDevice? Pick(MMDeviceEnumerator en, DataFlow flow, string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            try { var d = en.GetDevice(id); if (d.State == DeviceState.Active) return d; } catch { }
        }
        try
        {
            var def = en.GetDefaultAudioEndpoint(flow, Role.Communications);
            if (!IsVirtualController(def)) return def;
        }
        catch { }
        // Default is the virtual controller itself (or missing): use the first real device.
        return en.EnumerateAudioEndPoints(flow, DeviceState.Active).FirstOrDefault(d => !IsVirtualController(d));
    }

    // ------------------------------------------------------------------ microphone

    public float MicGain { set => _micGain = Math.Clamp(value, 0f, 4f); }
    public bool MicMuted { set => _micMuted = value; }

    public void StartMic(string? deviceId)
    {
        StopMic();
        using var en = new MMDeviceEnumerator();
        var dev = Pick(en, DataFlow.Capture, deviceId);
        if (dev is null) { _log("Microphone: no capture device found - sending silence"); return; }

        _capture = new WasapiCapture(dev, useEventSync: true, audioBufferMillisecondsLength: 10);
        _captureBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            ReadFully = false, DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(200),
        };

        // Any format -> float -> stereo -> 48 kHz -> 16-bit PCM
        ISampleProvider s = _captureBuffer.ToSampleProvider();
        if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        else if (s.WaveFormat.Channels > 2) s = new FirstTwoChannels(s);
        if (s.WaveFormat.SampleRate != Rate) s = new WdlResamplingSampleProvider(s, Rate);
        _captureConverted = new SampleToWaveProvider16(new GainProvider(s, this));

        var tmp = new byte[Rate * MicFrame / 10];
        _capture.DataAvailable += (_, e) =>
        {
            _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            int n;
            while ((n = _captureConverted.Read(tmp, 0, tmp.Length)) > 0)
                _micRing.Write(tmp.AsSpan(0, n));
        };
        _capture.RecordingStopped += (_, e) => { if (e.Exception is not null) _log($"Microphone stopped: {e.Exception.Message}"); };
        _capture.StartRecording();
        _log($"Microphone: using \"{dev.FriendlyName}\"");
    }

    public void StopMic()
    {
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;
        _micRing.Clear();
    }

    /// <summary>Called for each 1 ms USB packet of the virtual mic.</summary>
    public void FillMic(Span<byte> dst)
    {
        if (_capture is null || _micMuted) { dst.Clear(); return; }
        _micRing.Read(dst);
    }

    private sealed class GainProvider(ISampleProvider src, AudioBridge owner) : ISampleProvider
    {
        public WaveFormat WaveFormat => src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int n = src.Read(buffer, offset, count);
            float g = owner._micGain;
            if (g != 1f) for (int i = 0; i < n; i++) buffer[offset + i] = Math.Clamp(buffer[offset + i] * g, -1f, 1f);
            return n;
        }
    }

    private sealed class FirstTwoChannels(ISampleProvider src) : ISampleProvider
    {
        private float[] _buf = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int ch = src.WaveFormat.Channels, frames = count / 2;
            if (_buf.Length < frames * ch) _buf = new float[frames * ch];
            int got = src.Read(_buf, 0, frames * ch) / ch;
            for (int f = 0; f < got; f++) { buffer[offset + f * 2] = _buf[f * ch]; buffer[offset + f * 2 + 1] = _buf[f * ch + 1]; }
            return got * 2;
        }
    }

    // ------------------------------------------------------------------ speaker

    public void StartSpeaker(string? deviceId)
    {
        StopSpeaker();
        using var en = new MMDeviceEnumerator();
        var dev = Pick(en, DataFlow.Render, deviceId);
        if (dev is null) { _log("Speaker: no output device found - discarding controller audio"); return; }
        _outputBuffer = new BufferedWaveProvider(new WaveFormat(Rate, 16, 2))
        {
            DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(200),
        };
        _output = new WasapiOut(dev, AudioClientShareMode.Shared, useEventSync: true, latency: 40);
        _output.Init(_outputBuffer);
        _output.Play();
        _log($"Speaker: playing controller audio on \"{dev.FriendlyName}\"");
    }

    public void StopSpeaker()
    {
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _output = null;
        _outputBuffer = null;
    }

    /// <summary>Speaker audio from the virtual controller: 4 channels (1-2 speaker, 3-4 haptics).</summary>
    public void PlaySpeaker(ReadOnlySpan<byte> data)
    {
        var buf = _outputBuffer;
        if (buf is null) return;
        int frames = data.Length / SpeakerFrame;
        if (_stereo.Length < frames * 4) _stereo = new byte[frames * 4];
        for (int f = 0; f < frames; f++) data.Slice(f * SpeakerFrame, 4).CopyTo(_stereo.AsSpan(f * 4));
        if (buf.BufferedDuration.TotalMilliseconds > 120) buf.ClearBuffer(); // keep latency low
        buf.AddSamples(_stereo, 0, frames * 4);
    }

    public void Dispose() { StopMic(); StopSpeaker(); }
}
