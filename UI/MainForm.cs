using System.Drawing;
using System.Text;
using GripLink.Audio;
using GripLink.DualSense;
using NAudio.CoreAudioApi;

namespace GripLink.UI;

/// <summary>The settings window plus the tray icon.</summary>
internal sealed class MainForm : Form
{
    private readonly Engine _engine;
    private AppConfig _cfg;
    private bool _loading;
    private bool _exiting;
    private bool _trayHintShown;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _trayToggle;
    private readonly Icon _iconOn = AppIcon.Create(32, AppIcon.OnColor), _iconOff = AppIcon.Create(32, AppIcon.OffColor);

    private readonly CheckBox _power = new();
    private readonly Label _statusMain = new(), _statusWindows = new(), _statusClaw = new(), _statusGame = new();
    private readonly Panel _lightbar = new();

    private readonly ComboBox _model = new(), _transport = new();
    private readonly CheckBox _rumble = new();
    private readonly TrackBar _rumbleStrength = new(), _antiDeadZone = new(), _deadZone = new();
    private readonly Label _rumbleValue = new(), _antiValue = new(), _deadValue = new();

    private readonly ComboBox _micMode = new(), _micDevice = new(), _speakerMode = new(), _speakerDevice = new();
    private readonly TrackBar _micGain = new();
    private readonly Label _micGainValue = new();
    private readonly CheckBox _micMuted = new(), _keepDefaults = new();

    private readonly CheckBox _startWithWindows = new(), _startHidden = new(), _enableOnLaunch = new(), _closeToTray = new();
    private readonly TextBox _log = new();
    private readonly StringBuilder _logText = new();

    public MainForm(Engine engine, AppConfig cfg, bool startHidden)
    {
        _engine = engine;
        _cfg = cfg;

        Text = "GripLink";
        Icon = _iconOn;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(600), LogicalToDeviceUnits(860));
        MinimumSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(500));
        BackColor = Color.FromArgb(0xF6, 0xF7, 0xF9);

        // ---- tray
        _trayToggle = new ToolStripMenuItem("Controller on", null, async (_, _) => await TogglePowerAsync(!_engine.Enabled));
        var menu = new ContextMenuStrip();
        menu.Items.Add(_trayToggle);
        menu.Items.Add("Open GripLink", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _tray = new NotifyIcon { Icon = _iconOff, Text = "GripLink - off", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        BuildLayout();
        LoadValues();

        _engine.Log += line => UiAsync(() => AppendLog(line));
        _engine.StatusChanged += st => UiAsync(() => ShowStatus(st));
        ShowStatus(_engine.Status);

        if (startHidden)
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Load += (_, _) => Hide();
        }
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        // Stacked full-width sections in a vertically scrolling panel.
        var root = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 10, 14, 10) };
        Controls.Add(root);
        void Section(Control c)
        {
            c.Dock = DockStyle.Top;
            root.Controls.Add(c);
            c.BringToFront(); // docking order = add order (top to bottom)
            var gap = new Panel { Height = 10, Dock = DockStyle.Top };
            root.Controls.Add(gap);
            gap.BringToFront();
        }

        // Header: title + big power switch
        var header = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label { Text = "GripLink", Font = new Font("Segoe UI Semibold", 18f), AutoSize = true, Margin = new Padding(0) };
        var subtitle = new Label { Text = "Virtual PS5 controller for your MSI Claw", ForeColor = Color.DimGray, AutoSize = true, Margin = new Padding(2, 0, 0, 0) };
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0) };
        titles.Controls.Add(title); titles.Controls.Add(subtitle);
        header.Controls.Add(titles, 0, 0);

        _power.Appearance = Appearance.Button;
        _power.Size = new Size(120, 46);
        _power.FlatStyle = FlatStyle.Flat;
        _power.FlatAppearance.BorderSize = 0;
        _power.Font = new Font("Segoe UI Semibold", 12f);
        _power.TextAlign = ContentAlignment.MiddleCenter;
        _power.Cursor = Cursors.Hand;
        _power.Anchor = AnchorStyles.Right;
        _power.Click += async (_, _) => await TogglePowerAsync(_power.Checked);
        header.Controls.Add(_power, 1, 0);
        Section(header);

        // Status card
        var status = Card("Status");
        _statusMain.Font = new Font("Segoe UI Semibold", 10.5f);
        _statusMain.AutoSize = true;
        _statusWindows.AutoSize = _statusClaw.AutoSize = true;
        AddRow(status, _statusMain);
        AddRow(status, _statusWindows);
        AddRow(status, _statusClaw);
        var gameRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
        _statusGame.AutoSize = true;
        _statusGame.Margin = new Padding(0);
        _lightbar.Size = new Size(36, 14);
        _lightbar.Margin = new Padding(10, 4, 0, 0);
        _lightbar.BorderStyle = BorderStyle.FixedSingle;
        var tip = new ToolTip();
        tip.SetToolTip(_lightbar, "Light bar colour the game asked for");
        gameRow.Controls.Add(_statusGame); gameRow.Controls.Add(_lightbar);
        AddRow(status, gameRow);
        Section(status.Parent!);

        // Controller card
        var ctl = Card("Controller");
        _model.DropDownStyle = ComboBoxStyle.DropDownList;
        _model.Items.AddRange(["DualSense", "DualSense Edge (M1/M2 as back paddles)"]);
        AddField(ctl, "Model", _model);
        _transport.DropDownStyle = ComboBoxStyle.DropDownList;
        _transport.Items.AddRange(["USB cable (works with PS Remote Play)", "Bluetooth protocol"]);
        AddField(ctl, "Connection", _transport);
        _rumble.Text = "Rumble on the Claw's motors";
        _rumble.AutoSize = true;
        AddRow(ctl, _rumble);
        AddSlider(ctl, "Rumble strength", _rumbleStrength, _rumbleValue, 0, 100);
        AddSlider(ctl, "Stick anti-dead zone", _antiDeadZone, _antiValue, 0, 30);
        AddSlider(ctl, "Stick dead zone", _deadZone, _deadValue, 0, 20);
        Section(ctl.Parent!);

        // Audio card
        var audio = Card("Audio");
        _micMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _micMode.Items.AddRange(["Silent", "Use a real microphone"]);
        AddField(audio, "Controller mic", _micMode);
        _micDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        AddField(audio, "Microphone", _micDevice);
        AddSlider(audio, "Mic volume", _micGain, _micGainValue, 0, 300);
        _micMuted.Text = "Mute microphone";
        _micMuted.AutoSize = true;
        AddRow(audio, _micMuted);
        _speakerMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _speakerMode.Items.AddRange(["Discard", "Play on a real speaker"]);
        AddField(audio, "Controller speaker", _speakerMode);
        _speakerDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        AddField(audio, "Speaker", _speakerDevice);
        _keepDefaults.Text = "Keep my default sound devices (don't switch to the controller)";
        _keepDefaults.AutoSize = true;
        AddRow(audio, _keepDefaults);
        Section(audio.Parent!);

        // App card
        var app = Card("App");
        _startWithWindows.Text = "Start with Windows";
        _startHidden.Text = "Start hidden in the tray";
        _enableOnLaunch.Text = "Turn the controller on when GripLink starts";
        _closeToTray.Text = "Closing the window keeps GripLink running in the tray";
        foreach (var c in new[] { _startWithWindows, _startHidden, _enableOnLaunch, _closeToTray }) { c.AutoSize = true; AddRow(app, c); }
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        var openFolder = new Button { Text = "Open settings folder", AutoSize = true };
        openFolder.Click += (_, _) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppConfig.UserSettingsPath)!);
            System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(AppConfig.UserSettingsPath)!);
        };
        var copyLog = new Button { Text = "Copy log", AutoSize = true };
        copyLog.Click += (_, _) => { if (_log.TextLength > 0) Clipboard.SetText(_log.Text); };
        buttons.Controls.Add(openFolder); buttons.Controls.Add(copyLog);
        AddRow(app, buttons);
        Section(app.Parent!);

        // Log
        var logCard = Card("Log");
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Height = 140;
        _log.Dock = DockStyle.Fill;
        _log.Font = new Font("Consolas", 8.5f);
        _log.BackColor = Color.White;
        AddRow(logCard, _log);
        Section(logCard.Parent!);

        // Change handlers
        foreach (var cb in new[] { _model, _transport, _micMode, _micDevice, _speakerMode, _speakerDevice })
            cb.SelectedIndexChanged += async (_, _) => await OnChangedAsync();
        foreach (var c in new[] { _rumble, _micMuted, _keepDefaults, _startHidden, _enableOnLaunch, _closeToTray })
            c.CheckedChanged += async (_, _) => await OnChangedAsync();
        foreach (var t in new[] { _rumbleStrength, _antiDeadZone, _deadZone, _micGain })
            t.ValueChanged += async (_, _) => { UpdateSliderLabels(); await OnChangedAsync(); };
        _startWithWindows.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            try { AutoStart.Set(_startWithWindows.Checked); AppendLog($"Start with Windows: {(_startWithWindows.Checked ? "on" : "off")}"); }
            catch (Exception ex) { AppendLog($"Could not change start-up setting: {ex.Message}"); }
        };
        _micDevice.DropDown += (_, _) => RefreshDevices(_micDevice, DataFlow.Capture, _cfg.Audio.MicDeviceId);
        _speakerDevice.DropDown += (_, _) => RefreshDevices(_speakerDevice, DataFlow.Render, _cfg.Audio.SpeakerDeviceId);
    }

    private static TableLayoutPanel Card(string title)
    {
        var group = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0),
            Font = new Font("Segoe UI Semibold", 10f), BackColor = Color.White,
        };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Font = new Font("Segoe UI", 9.5f) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        group.Controls.Add(t);
        return t;
    }

    private static void AddRow(TableLayoutPanel t, Control c)
    {
        c.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(c, 0, t.RowCount);
        t.SetColumnSpan(c, 3);
        t.RowCount++;
    }

    private static void AddField(TableLayoutPanel t, string label, Control c)
    {
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 6, 3) };
        c.Dock = DockStyle.Fill;
        c.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(l, 0, t.RowCount);
        t.Controls.Add(c, 1, t.RowCount);
        t.SetColumnSpan(c, 2);
        t.RowCount++;
    }

    private static void AddSlider(TableLayoutPanel t, string label, TrackBar bar, Label value, int min, int max)
    {
        bar.Minimum = min; bar.Maximum = max;
        bar.TickStyle = TickStyle.None;
        bar.AutoSize = false;
        bar.Height = 30;
        bar.Dock = DockStyle.Fill;
        bar.Margin = new Padding(0, 2, 0, 0);
        value.AutoSize = true;
        value.Anchor = AnchorStyles.Left;
        value.Margin = new Padding(4, 7, 0, 0);
        var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 6, 3) };
        t.Controls.Add(l, 0, t.RowCount);
        t.Controls.Add(bar, 1, t.RowCount);
        t.Controls.Add(value, 2, t.RowCount);
        t.RowCount++;
    }

    // ------------------------------------------------------------------ values <-> settings

    private sealed record DeviceItem(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private static void RefreshDevices(ComboBox box, DataFlow flow, string? selectedId)
    {
        var items = new List<DeviceItem> { new(null, "Windows default") };
        try { items.AddRange(AudioBridge.ListDevices(flow).Select(d => new DeviceItem(d.Id, d.Name))); } catch { }
        var current = (box.SelectedItem as DeviceItem)?.Id ?? selectedId;
        box.BeginUpdate();
        box.Items.Clear();
        box.Items.AddRange(items.ToArray());
        int idx = items.FindIndex(i => i.Id == current);
        box.SelectedIndex = idx >= 0 ? idx : 0;
        box.EndUpdate();
    }

    private void LoadValues()
    {
        _loading = true;
        _model.SelectedIndex = _cfg.Model == DualSenseModel.DualSenseEdge ? 1 : 0;
        _transport.SelectedIndex = _cfg.Transport == DualSenseTransport.Bluetooth ? 1 : 0;
        _rumble.Checked = _cfg.RumblePassthrough;
        _rumbleStrength.Value = (int)Math.Round(Math.Clamp(_cfg.RumbleStrength, 0, 1) * 100);
        _antiDeadZone.Value = (int)Math.Round(Math.Clamp(_cfg.StickAntiDeadZone, 0, 0.3) * 100);
        _deadZone.Value = (int)Math.Round(Math.Clamp(_cfg.StickDeadZone, 0, 0.2) * 100);

        _micMode.SelectedIndex = _cfg.Audio.Mic == MicMode.ClawMicrophone ? 1 : 0;
        RefreshDevices(_micDevice, DataFlow.Capture, _cfg.Audio.MicDeviceId);
        _micGain.Value = (int)Math.Round(Math.Clamp(_cfg.Audio.MicGain, 0, 3) * 100);
        _micMuted.Checked = _cfg.Audio.MicMuted;
        _speakerMode.SelectedIndex = _cfg.Audio.Speaker == SpeakerMode.ClawSpeakers ? 1 : 0;
        RefreshDevices(_speakerDevice, DataFlow.Render, _cfg.Audio.SpeakerDeviceId);
        _keepDefaults.Checked = _cfg.Audio.KeepDefaultDevices;

        _startWithWindows.Checked = AutoStart.IsEnabled;
        _startHidden.Checked = _cfg.App.StartMinimized;
        _enableOnLaunch.Checked = _cfg.App.EnableOnLaunch;
        _closeToTray.Checked = _cfg.App.CloseToTray;
        UpdateSliderLabels();
        UpdateEnabledStates();
        _loading = false;
    }

    private void UpdateSliderLabels()
    {
        _rumbleValue.Text = $"{_rumbleStrength.Value}%";
        _antiValue.Text = $"{_antiDeadZone.Value}%";
        _deadValue.Text = $"{_deadZone.Value}%";
        _micGainValue.Text = $"{_micGain.Value}%";
    }

    private void UpdateEnabledStates()
    {
        _rumbleStrength.Enabled = _rumble.Checked;
        bool usb = _transport.SelectedIndex == 0;
        bool mic = usb && _micMode.SelectedIndex == 1;
        _micMode.Enabled = usb;
        _micDevice.Enabled = _micGain.Enabled = _micMuted.Enabled = mic;
        _speakerMode.Enabled = usb;
        _speakerDevice.Enabled = usb && _speakerMode.SelectedIndex == 1;
        _keepDefaults.Enabled = usb;
    }

    private async Task OnChangedAsync()
    {
        if (_loading) return;
        UpdateEnabledStates();
        var c = _cfg.Clone();
        c.Model = _model.SelectedIndex == 1 ? DualSenseModel.DualSenseEdge : DualSenseModel.DualSense;
        c.Transport = _transport.SelectedIndex == 1 ? DualSenseTransport.Bluetooth : DualSenseTransport.Usb;
        c.RumblePassthrough = _rumble.Checked;
        c.RumbleStrength = _rumbleStrength.Value / 100.0;
        c.StickAntiDeadZone = _antiDeadZone.Value / 100.0;
        c.StickDeadZone = _deadZone.Value / 100.0;
        c.Audio.Mic = _micMode.SelectedIndex == 1 ? MicMode.ClawMicrophone : MicMode.Silence;
        c.Audio.MicDeviceId = (_micDevice.SelectedItem as DeviceItem)?.Id;
        c.Audio.MicGain = _micGain.Value / 100.0;
        c.Audio.MicMuted = _micMuted.Checked;
        c.Audio.Speaker = _speakerMode.SelectedIndex == 1 ? SpeakerMode.ClawSpeakers : SpeakerMode.Discard;
        c.Audio.SpeakerDeviceId = (_speakerDevice.SelectedItem as DeviceItem)?.Id;
        c.Audio.KeepDefaultDevices = _keepDefaults.Checked;
        c.App.StartMinimized = _startHidden.Checked;
        c.App.EnableOnLaunch = _enableOnLaunch.Checked;
        c.App.CloseToTray = _closeToTray.Checked;
        _cfg = c;
        try { c.Save(); } catch (Exception ex) { AppendLog($"Could not save settings: {ex.Message}"); }
        await _engine.ApplyAsync(c);
    }

    // ------------------------------------------------------------------ power + status

    public async Task TogglePowerAsync(bool on)
    {
        _power.Enabled = false;
        _trayToggle.Enabled = false;
        try
        {
            if (on) await _engine.StartAsync(); else await _engine.StopAsync();
        }
        finally
        {
            _power.Enabled = true;
            _trayToggle.Enabled = true;
            ShowStatus(_engine.Status);
        }
    }

    private void ShowStatus(EngineStatus st)
    {
        _power.Checked = st.Enabled;
        _power.Text = st.Enabled ? "ON" : "OFF";
        _power.BackColor = st.Enabled ? AppIcon.OnColor : Color.FromArgb(0xD5, 0xD8, 0xDD);
        _power.ForeColor = st.Enabled ? Color.White : Color.FromArgb(0x40, 0x44, 0x4A);
        _trayToggle.Checked = st.Enabled;
        _tray.Icon = st.Enabled ? _iconOn : _iconOff;
        Icon = st.Enabled ? _iconOn : _iconOff;

        string main = !st.Enabled ? "Controller is off"
                    : st.Attached ? "PS5 controller is plugged in" : st.Message;
        if (!st.Enabled && st.Message.StartsWith("Error")) main = st.Message;
        _statusMain.Text = main;
        _tray.Text = Truncate($"GripLink - {main}", 63);

        Dot(_statusWindows, st.Attached, "Windows sees the virtual controller", "Not connected to Windows");
        Dot(_statusClaw, st.ClawConnected, "Claw controls found", "Claw controls not found (XInput mode?)");
        if (st.KnownApp is { } app)
            Dot(_statusGame, true, st.GameTalking ? $"{app} is using it" : $"{app} is running and can use it", "");
        else
            Dot(_statusGame, st.GameTalking, "A game is using it", "No game is using it right now");
        if (st.Lightbar is { } lb && st.Enabled) { _lightbar.Visible = true; _lightbar.BackColor = Color.FromArgb(lb.R, lb.G, lb.B); }
        else _lightbar.Visible = false;
    }

    private static void Dot(Label l, bool ok, string yes, string no)
    {
        l.AutoSize = true;
        l.Text = (ok ? "●  " : "○  ") + (ok ? yes : no);
        l.ForeColor = ok ? Color.FromArgb(0x1A, 0x7F, 0x37) : Color.DimGray;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void AppendLog(string line)
    {
        _logText.AppendLine(line);
        if (_logText.Length > 40_000) _logText.Remove(0, _logText.Length - 30_000);
        _log.Text = _logText.ToString();
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void UiAsync(Action a)
    {
        if (IsDisposed || Disposing) return;
        try { if (IsHandleCreated) BeginInvoke(a); else a(); } catch { }
    }

    // ------------------------------------------------------------------ window / tray behaviour

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _cfg.App.CloseToTray) Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && _cfg.App.CloseToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _tray.ShowBalloonTip(3000, "GripLink is still running",
                    "The controller stays on. Right-click the tray icon to turn it off or exit.", ToolTipIcon.Info);
                _trayHintShown = true;
            }
            return;
        }
        if (!_exiting)
        {
            e.Cancel = true;
            _ = ExitAsync();
            return;
        }
        base.OnFormClosing(e);
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Hide();
        await _engine.StopAsync();
        _tray.Visible = false;
        _tray.Dispose();
        Close();
        Application.Exit();
    }
}
