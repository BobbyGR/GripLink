using System.Text.Json;
using GripLink.DualSense;

namespace GripLink;

public sealed class AppConfig
{
    /// <summary>"DualSense" or "DualSenseEdge" (Edge exposes the two back paddles and Fn buttons).</summary>
    public DualSenseModel Model { get; set; } = DualSenseModel.DualSense;

    /// <summary>"Usb" (what PS Remote Play accepts) or "Bluetooth" (BT report protocol).</summary>
    public DualSenseTransport Transport { get; set; } = DualSenseTransport.Usb;

    public int Port { get; set; } = 3240;

    /// <summary>Run "usbip attach" automatically after the server starts.</summary>
    public bool AutoAttach { get; set; } = true;

    /// <summary>Path to usbip.exe from usbip-win2. Null = search PATH and Program Files.</summary>
    public string? UsbIpPath { get; set; }

    /// <summary>Input report interval. A DualSense on Bluetooth reports roughly every 4 ms.</summary>
    public double ReportIntervalMs { get; set; } = 4.0;

    /// <summary>Skip the Bluetooth "simple mode" phase and send 0x31 reports straight away.</summary>
    public bool StartInEnhancedMode { get; set; } = false;

    /// <summary>Bluetooth MAC address reported in feature report 0x09.</summary>
    public string MacAddress { get; set; } = "02:C1:A5:5E:00:01";

    /// <summary>XInput slot of the Claw's built-in controller (0-3).</summary>
    public int XInputSlot { get; set; } = 0;

    /// <summary>Radial stick dead zone (0..1). 0 = raw values like a real DualSense (the game applies its own).</summary>
    public double StickDeadZone { get; set; } = 0.0;

    /// <summary>Anti-dead-zone (0..1): smallest output once the stick moves, to cancel a game's own dead zone.</summary>
    public double StickAntiDeadZone { get; set; } = 0.0;

    /// <summary>L2/R2 digital bit is set when the analog value is above this (0..255).</summary>
    public byte TriggerDigitalThreshold { get; set; } = 0;

    public bool RumblePassthrough { get; set; } = true;
    public double RumbleStrength { get; set; } = 1.0;

    /// <summary>Claw (XInput) button name -> DualSense button name.</summary>
    public Dictionary<string, string> ButtonMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A"] = "Cross", ["B"] = "Circle", ["X"] = "Square", ["Y"] = "Triangle",
        ["LB"] = "L1", ["RB"] = "R1", ["LS"] = "L3", ["RS"] = "R3",
        ["View"] = "Create", ["Menu"] = "Options", ["Guide"] = "PS",
        ["DPadUp"] = "DPadUp", ["DPadDown"] = "DPadDown", ["DPadLeft"] = "DPadLeft", ["DPadRight"] = "DPadRight",
    };

    /// <summary>Keyboard keys (e.g. what M1/M2 are set to send in MSI Center M) -> DualSense button.</summary>
    public List<KeyBinding> KeyBindings { get; set; } = new();

    /// <summary>Hold several Claw buttons together to press a DualSense button.</summary>
    public List<ComboBinding> Combos { get; set; } = new();

    public MotionConfig Motion { get; set; } = new();

    public AudioConfig Audio { get; set; } = new();

    public AppBehavior App { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>%APPDATA%\GripLink\settings.json - where the app saves its settings.</summary>
    public static string UserSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GripLink", "settings.json");

    /// <summary>appsettings.json next to the exe - the defaults used on first run.</summary>
    public static string DefaultsPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    /// <summary>Loads saved settings, or the defaults file on first run.</summary>
    public static AppConfig LoadUser() =>
        File.Exists(UserSettingsPath) ? Load(UserSettingsPath) : Load(DefaultsPath);

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) return new AppConfig();
        var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
        // Keep lookups case-insensitive after deserialisation.
        cfg.ButtonMap = new Dictionary<string, string>(cfg.ButtonMap, StringComparer.OrdinalIgnoreCase);
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserSettingsPath)!);
        var tmp = UserSettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, UserSettingsPath, overwrite: true);
    }

    public AppConfig Clone() =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
}

public enum MicMode { Silence, ClawMicrophone }
public enum SpeakerMode { Discard, ClawSpeakers }

public sealed class AudioConfig
{
    /// <summary>What the virtual controller's microphone sends.</summary>
    public MicMode Mic { get; set; } = MicMode.Silence;
    /// <summary>Capture device id, or null for the Windows default microphone.</summary>
    public string? MicDeviceId { get; set; }
    public double MicGain { get; set; } = 1.0;
    public bool MicMuted { get; set; }

    /// <summary>What happens to audio Windows/games send to the controller's speaker.</summary>
    public SpeakerMode Speaker { get; set; } = SpeakerMode.Discard;
    /// <summary>Playback device id, or null for the Windows default output.</summary>
    public string? SpeakerDeviceId { get; set; }

    /// <summary>Stop Windows from switching the default sound devices to the virtual controller.</summary>
    public bool KeepDefaultDevices { get; set; } = true;
}

public sealed class AppBehavior
{
    /// <summary>Turn the virtual controller on as soon as the app starts.</summary>
    public bool EnableOnLaunch { get; set; } = true;
    /// <summary>Start hidden in the tray (used when starting with Windows).</summary>
    public bool StartMinimized { get; set; } = false;
    /// <summary>Closing the window keeps GripLink running in the tray.</summary>
    public bool CloseToTray { get; set; } = true;
}

public sealed class KeyBinding
{
    public string Key { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class ComboBinding
{
    public List<string> Buttons { get; set; } = new();
    public string Target { get; set; } = "";
    /// <summary>When the combo fires, hide the source buttons from the game.</summary>
    public bool SuppressSources { get; set; } = true;
}

public sealed class MotionConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Report interval asked from the Windows sensors, in ms.</summary>
    public uint SensorIntervalMs { get; set; } = 4;

    // DualSense axis <- Claw sensor axis, as "+X", "-Z", etc.
    // Windows sensor axes: X = right, Y = top of screen, Z = out of the screen.
    public string GyroX { get; set; } = "+X";
    public string GyroY { get; set; } = "+Z";
    public string GyroZ { get; set; } = "-Y";
    public string AccelX { get; set; } = "-X";
    public string AccelY { get; set; } = "-Z";
    public string AccelZ { get; set; } = "+Y";

    public double GyroScale { get; set; } = 1.0;
}
