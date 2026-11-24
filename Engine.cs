using System.Diagnostics;
using System.Net;
using GripLink.Audio;
using GripLink.DualSense;
using GripLink.Input;
using GripLink.UsbIp;

namespace GripLink;

public sealed record EngineStatus(
    bool Enabled, bool Attached, bool ClawConnected, bool GameTalking,
    (byte R, byte G, byte B)? Lightbar, string Message, string? KnownApp = null);

/// <summary>Turns the virtual controller on and off and wires up input, rumble and audio.</summary>
public sealed class Engine : IDisposable
{
    private readonly object _gate = new();
    private AppConfig _cfg;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private ClawMapper? _mapper;
    private VirtualDualSense? _device;
    private AudioBridge? _audio;
    private DefaultDeviceGuard? _guard;
    private volatile bool _attached;
    private long _lastHostActivity;
    private (byte, byte, byte)? _lightbar;
    private string _message = "Off";
    private readonly System.Threading.Timer _statusTimer;

    public event Action<string>? Log;
    public event Action<EngineStatus>? StatusChanged;

    public Engine(AppConfig cfg)
    {
        _cfg = cfg;
        _statusTimer = new System.Threading.Timer(_ => RaiseStatus(), null, 1000, 1000);
    }

    public bool Enabled { get; private set; }

    private void Write(string msg) => Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {msg}");

    public EngineStatus Status => new(
        Enabled, _attached, _mapper?.IsClawConnected ?? false,
        Environment.TickCount64 - Interlocked.Read(ref _lastHostActivity) < 15000,
        _lightbar, _message, _attached ? RunningControllerApp() : null);

    /// <summary>
    /// Apps that read the controller's input quietly (no USB traffic we can see), detected by process name.
    /// </summary>
    private static readonly (string Process, string Name)[] ControllerApps =
    [
        ("RemotePlay", "PS Remote Play"),
        ("steam", "Steam"),
    ];

    private static string? RunningControllerApp()
    {
        foreach (var (proc, name) in ControllerApps)
        {
            var found = Process.GetProcessesByName(proc);
            foreach (var p in found) p.Dispose();
            if (found.Length > 0) return name;
        }
        return null;
    }

    private void RaiseStatus() { try { StatusChanged?.Invoke(Status); } catch { } }

    // ------------------------------------------------------------------ on / off

    public async Task StartAsync()
    {
        lock (_gate) { if (Enabled) return; Enabled = true; }
        var cfg = _cfg;
        _message = "Starting...";
        RaiseStatus();
        try
        {
            _mapper = new ClawMapper(cfg, Write);
            _device = new VirtualDualSense(
                _mapper.Read, cfg.Model, cfg.Transport, DualSenseReports.ParseMac(cfg.MacAddress),
                TimeSpan.FromMilliseconds(Math.Clamp(cfg.ReportIntervalMs, 1, 100)),
                cfg.StartInEnhancedMode, cfg.TriggerDigitalThreshold, Write);

            OutputFeedback? last = null;
            _device.Output += fb =>
            {
                Interlocked.Exchange(ref _lastHostActivity, Environment.TickCount64);
                _mapper?.ApplyFeedback(fb);
                if (fb.Lightbar is { } lb) _lightbar = lb;
                string text = fb.ToString();
                if (last?.ToString() != text) { Write($"Game -> controller: {text}"); last = fb; }
            };
            _device.HostActivity += () => Interlocked.Exchange(ref _lastHostActivity, Environment.TickCount64);
            _device.ConnectionChanged += connected =>
            {
                _attached = connected;
                _message = connected ? "Connected" : "Waiting for Windows to attach...";
                if (!connected) _mapper?.StopRumble();
                RaiseStatus();
            };

            _audio = new AudioBridge(Write);
            ApplyAudio(cfg);
            _device.MicSource = _audio.FillMic;
            _device.SpeakerSink = _audio.PlaySpeaker;

            _cts = new CancellationTokenSource();
            var server = new UsbIpServer(_device, IPAddress.Loopback, cfg.Port, Write);
            var listening = new TaskCompletionSource();
            _serverTask = Task.Run(() => server.RunAsync(listening, _cts.Token));
            await Task.WhenAny(listening.Task, _serverTask);
            if (_serverTask.IsFaulted) throw _serverTask.Exception!.GetBaseException();

            if (cfg.Audio.KeepDefaultDevices && cfg.Transport == DualSenseTransport.Usb)
            {
                _guard = new DefaultDeviceGuard(Write);
                _guard.Remember();
            }

            _message = "Waiting for Windows to attach...";
            RaiseStatus();
            if (cfg.AutoAttach) await AttachWithRetryAsync(cfg, _cts.Token);
            if (_guard is not null) _ = _guard.RestoreAfterAttachAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Write($"Could not start: {ex.Message}");
            _message = $"Error: {ex.Message}";
            await StopAsync();
            _message = $"Error: {ex.Message}";
            RaiseStatus();
        }
    }

    public async Task StopAsync()
    {
        lock (_gate) { if (!Enabled) return; Enabled = false; }
        _message = "Stopping...";
        RaiseStatus();
        try { _cts?.Cancel(); } catch { }
        if (_serverTask is not null)
        {
            try { await _serverTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        _mapper?.StopRumble();
        _audio?.Dispose();
        _audio = null;
        _device = null;
        _mapper = null;
        _guard = null;
        _attached = false;
        _lightbar = null;
        _cts?.Dispose();
        _cts = null;
        _message = "Off";
        Write("Virtual controller turned off (unplugged from Windows)");
        RaiseStatus();
    }

    /// <summary>Applies new settings; restarts the virtual controller if a setting needs it.</summary>
    public async Task ApplyAsync(AppConfig cfg)
    {
        var old = _cfg;
        _cfg = cfg;
        if (!Enabled) return;

        bool restart = old.Model != cfg.Model || old.Transport != cfg.Transport || old.Port != cfg.Port
                    || old.XInputSlot != cfg.XInputSlot || old.ReportIntervalMs != cfg.ReportIntervalMs;
        if (restart)
        {
            Write("Settings changed - re-plugging the virtual controller");
            await StopAsync();
            await StartAsync();
            return;
        }
        _mapper?.UpdateConfig(cfg);
        if (_audio is not null)
        {
            bool micChanged = old.Audio.Mic != cfg.Audio.Mic || old.Audio.MicDeviceId != cfg.Audio.MicDeviceId;
            bool spkChanged = old.Audio.Speaker != cfg.Audio.Speaker || old.Audio.SpeakerDeviceId != cfg.Audio.SpeakerDeviceId;
            if (micChanged || spkChanged) ApplyAudio(cfg, micChanged, spkChanged);
            _audio.MicGain = (float)cfg.Audio.MicGain;
            _audio.MicMuted = cfg.Audio.MicMuted;
        }
    }

    private void ApplyAudio(AppConfig cfg, bool mic = true, bool speaker = true)
    {
        if (_audio is null) return;
        _audio.MicGain = (float)cfg.Audio.MicGain;
        _audio.MicMuted = cfg.Audio.MicMuted;
        try
        {
            if (mic)
            {
                if (cfg.Audio.Mic == MicMode.ClawMicrophone) _audio.StartMic(cfg.Audio.MicDeviceId);
                else { _audio.StopMic(); Write("Microphone: sending silence"); }
            }
            if (speaker)
            {
                if (cfg.Audio.Speaker == SpeakerMode.ClawSpeakers) _audio.StartSpeaker(cfg.Audio.SpeakerDeviceId);
                else { _audio.StopSpeaker(); Write("Speaker: controller audio is discarded"); }
            }
        }
        catch (Exception ex) { Write($"Audio setup failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ usbip attach

    public static string? FindUsbIp(AppConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.UsbIpPath) && File.Exists(cfg.UsbIpPath)) return cfg.UsbIpPath;
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "usbip.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private async Task AttachWithRetryAsync(AppConfig cfg, CancellationToken ct)
    {
        string? exe = FindUsbIp(cfg);
        if (exe is null)
        {
            Write("usbip.exe not found. Install usbip-win2 (github.com/vadimgrn/usbip-win2).");
            _message = "usbip-win2 is not installed";
            RaiseStatus();
            return;
        }
        // At Windows start-up the usbip driver may not be ready yet: retry for a while.
        for (int attempt = 1; attempt <= 10 && !ct.IsCancellationRequested; attempt++)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "attach", "-r", "127.0.0.1", "-b", "1-1" }) psi.ArgumentList.Add(a);
            if (cfg.Port != 3240) { psi.ArgumentList.Add("--tcp-port"); psi.ArgumentList.Add(cfg.Port.ToString()); }
            try
            {
                using var p = Process.Start(psi)!;
                string output = (await p.StandardOutput.ReadToEndAsync(ct)) + (await p.StandardError.ReadToEndAsync(ct));
                await p.WaitForExitAsync(ct);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    Write($"usbip: {line}");
                if (p.ExitCode == 0) return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Write($"usbip attach failed: {ex.Message}"); }
            if (_attached) return;
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ContinueWith(_ => { });
        }
    }

    public void Dispose()
    {
        _statusTimer.Dispose();
        StopAsync().GetAwaiter().GetResult();
    }
}
