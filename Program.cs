using System.Diagnostics;
using System.Runtime.InteropServices;
using GripLink.DualSense;
using GripLink.Input;
using GripLink.UI;

namespace GripLink;

internal static class Program
{
    private const string InstanceMutex = @"Local\GripLink.SingleInstance";
    private const string ShowEvent = @"Local\GripLink.Show";

    [STAThread]
    private static int Main(string[] args)
    {
        // ---- command-line tools (print to the console that started us)
        if (args.Contains("--selftest")) { AttachConsole(-1); return SelfTest.RunAsync().GetAwaiter().GetResult(); }
        if (args.Contains("--dump")) { AttachConsole(-1); return Dump(args); }
        if (args.Length >= 2 && args[0] == "--write-icon") { AppIcon.SaveIco(args[1]); return 0; }
        if (args.Length >= 2 && args[0] == "--render-ui")
        {
            // Saves a picture of the window (layout check) without turning anything on.
            ApplicationConfiguration.Initialize();
            using var e = new Engine(AppConfig.LoadUser());
            using var f = new MainForm(e, AppConfig.LoadUser(), startHidden: false);
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new System.Drawing.Point(-10000, -10000);
            f.ShowInTaskbar = false;
            if (args.Length >= 3) f.Height = int.Parse(args[2]); // tall render to see every section
            f.Show();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(25); }
            using var bmp = new System.Drawing.Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(args[1]);
            return 0;
        }

        // ---- single instance: a second launch just shows the running window
        using var mutex = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            try { using var ev = EventWaitHandle.OpenExisting(ShowEvent); ev.Set(); } catch { }
            return 0;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);

        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }

        ApplicationConfiguration.Initialize();
        var cfg = AppConfig.LoadUser();
        using var engine = new Engine(cfg);
        bool hidden = args.Contains("--tray") || cfg.App.StartMinimized;
        var form = new MainForm(engine, cfg, hidden);

        // Bring the window forward when another copy is launched.
        var waiter = new Thread(() =>
        {
            while (showEvent.WaitOne())
                try { form.BeginInvoke(form.ShowFromTray); } catch { return; }
        }) { IsBackground = true };
        waiter.Start();

        if (cfg.App.EnableOnLaunch)
            form.Shown += async (_, _) => await form.TogglePowerAsync(true);
        if (hidden && cfg.App.EnableOnLaunch)
            form.HandleCreated += async (_, _) => await form.TogglePowerAsync(true);

        Application.Run(form);
        return 0;
    }

    /// <summary>--dump: show live Claw input as DualSense values (no USB/IP).</summary>
    private static int Dump(string[] args)
    {
        var cfg = AppConfig.LoadUser();
        var mapper = new ClawMapper(cfg, Console.WriteLine);
        Console.WriteLine("Live Claw input as a DualSense. Ctrl+C to stop.");
        byte seq = 0;
        while (true)
        {
            var s = mapper.Read();
            var r = DualSenseReports.BuildUsbInput(s, seq++, 0, cfg.Model == DualSenseModel.DualSenseEdge, cfg.TriggerDigitalThreshold);
            Console.Write($"\rLX{s.LX,4} LY{s.LY,4} RX{s.RX,4} RY{s.RY,4} L2{s.L2,4} R2{s.R2,4} " +
                          $"gyro({s.GyroX,6},{s.GyroY,6},{s.GyroZ,6}) acc({s.AccelX,6},{s.AccelY,6},{s.AccelZ,6}) " +
                          $"[{string.Join(' ', s.Buttons.OrderBy(b => b))}]".PadRight(60));
            if (args.Contains("--hex")) Console.Write("\n" + Convert.ToHexString(r) + "\n");
            Thread.Sleep(50);
        }
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
}
