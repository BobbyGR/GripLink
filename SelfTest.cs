using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GripLink.DualSense;
using GripLink.UsbIp;

namespace GripLink;

/// <summary>
/// Offline checks: report layouts, CRCs, the HID descriptor, and a full USB/IP session
/// against the virtual device over a loopback socket (no driver or Claw needed).
/// </summary>
public static class SelfTest
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("CRC-32");
        Check(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926, "standard check value 0xCBF43926");

        Console.WriteLine("Reports");
        var s = new DualSenseState { LX = 0, LY = 255, RX = 10, RY = 20, L2 = 200, R2 = 0, GyroX = -5, AccelY = 8192 };
        s.Buttons.UnionWith([DsButton.Cross, DsButton.Triangle, DsButton.R1, DsButton.Options, DsButton.PS, DsButton.DPadUp, DsButton.DPadRight]);
        s.Touch0 = new TouchPoint { Active = true, Id = 3, X = 1234, Y = 567 };

        var bt = DualSenseReports.BuildBtInput(s, 7, 123456, false, 0);
        Check(bt.Length == 78 && bt[0] == 0x31, "BT input is 78 bytes, id 0x31");
        Check(Crc32.Verify(Crc32.InputSeed, bt), "BT input CRC valid");
        Check(bt[2] == 0 && bt[3] == 255 && bt[6] == 200, "sticks and L2 at the right offsets");
        Check(bt[8] == 7, "sequence number");
        Check((bt[9] & 0x0F) == (byte)DPad.NorthEast && (bt[9] & 0x20) != 0 && (bt[9] & 0x80) != 0, "d-pad NE + cross + triangle");
        Check(bt[10] == (0x02 | 0x04 | 0x20), "R1 + L2 digital + options");
        Check(bt[11] == 0x01, "PS button");
        Check(BinaryPrimitives.ReadInt16LittleEndian(bt.AsSpan(17)) == -5, "gyro X at offset 17");
        Check(BinaryPrimitives.ReadInt16LittleEndian(bt.AsSpan(25)) == 8192, "accel Y at offset 25");
        Check(BinaryPrimitives.ReadUInt32LittleEndian(bt.AsSpan(29)) == 123456, "sensor timestamp at offset 29");
        int tx = bt[35] | (bt[36] & 0x0F) << 8, ty = (bt[36] >> 4) | bt[37] << 4;
        Check(bt[34] == 3 && tx == 1234 && ty == 567, "touch point 0 encoding");
        Check(bt[38] == 0x80, "touch point 1 inactive");

        var simple = DualSenseReports.BuildSimpleInput(s, 1, 0);
        Check(simple.Length == 10 && simple[0] == 0x01 && simple[8] == 200, "simple report 0x01 is 10 bytes");

        Check(Crc32.Verify(Crc32.FeatureSeed, DualSenseReports.BuildCalibration()), "calibration 0x05 CRC");
        Check(Crc32.Verify(Crc32.FeatureSeed, DualSenseReports.BuildFirmwareInfo()), "firmware 0x20 CRC");
        var pairing = DualSenseReports.BuildPairingInfo(DualSenseReports.ParseMac("02:C1:A5:5E:00:01"));
        Check(pairing[1] == 0x01 && pairing[6] == 0x02 && Crc32.Verify(Crc32.FeatureSeed, pairing), "pairing 0x09 MAC little endian + CRC");

        var outRep = new byte[78];
        outRep[0] = 0x31; outRep[2] = 0x10;
        outRep[3] = 0x03; outRep[4] = 0x04; outRep[5] = 0x40; outRep[6] = 0x80;
        outRep[3 + 44] = 0xFF; outRep[3 + 45] = 0x10; outRep[3 + 46] = 0x20;
        Crc32.Seal(Crc32.OutputSeed, outRep);
        var fb = OutputFeedback.Parse(outRep);
        Check(fb is { CrcValid: true, MotorRight: 0x40, MotorLeft: 0x80 } && fb.Lightbar == (0xFF, 0x10, 0x20), "output 0x31 rumble + lightbar parse");

        Console.WriteLine("HID descriptor");
        Check(WalkDescriptor(Descriptors.HidReport()), "report descriptor items are well formed and collections balanced");

        Check(WalkDescriptor(Descriptors.HidReportUsb()), "USB report descriptor items are well formed and collections balanced");
        var usbIn = DualSenseReports.BuildUsbInput(s, 7, 123456, false, 0);
        Check(usbIn.Length == 64 && usbIn[0] == 0x01 && usbIn.AsSpan(1, 63).SequenceEqual(bt.AsSpan(2, 63)), "USB input 0x01 is 64 bytes with the same payload as BT 0x31");
        var usbOut = new byte[48];
        usbOut[0] = 0x02; usbOut[1] = 0x01; usbOut[3] = 0x55; usbOut[4] = 0x66;
        Check(OutputFeedback.Parse(usbOut) is { MotorRight: 0x55, MotorLeft: 0x66 }, "USB output 0x02 rumble parse");

        Console.WriteLine("USB/IP loopback session (Bluetooth transport)");
        await UsbIpLoopback(s);
        Console.WriteLine("USB/IP loopback session (USB transport)");
        await UsbModeLoopback(s);

        Console.WriteLine(_failures == 0 ? "\nAll checks passed." : $"\n{_failures} check(s) FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    private static bool WalkDescriptor(byte[] d)
    {
        int i = 0, depth = 0;
        while (i < d.Length)
        {
            byte p = d[i];
            int size = (p & 3) == 3 ? 4 : p & 3;
            if (p == 0xA1) depth++;
            if (p == 0xC0) depth--;
            if (depth < 0) return false;
            i += 1 + size;
        }
        return i == d.Length && depth == 0;
    }

    private static async Task UsbModeLoopback(DualSenseState state)
    {
        var dev = new VirtualDualSense(() => state, DualSenseModel.DualSense, DualSenseTransport.Usb,
                                       DualSenseReports.ParseMac("02:C1:A5:5E:00:01"), TimeSpan.FromMilliseconds(4), false, 0, _ => { });
        OutputFeedback? got = null;
        dev.Output += f => got = f;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = FreePort();
        var listening = new TaskCompletionSource();
        _ = new UsbIpServer(dev, IPAddress.Loopback, port, _ => { }).RunAsync(listening, cts.Token);
        await listening.Task;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var import = new byte[40];
        import[0] = 0x01; import[1] = 0x11; import[2] = 0x80; import[3] = 0x03;
        Encoding.ASCII.GetBytes("1-1").CopyTo(import, 8);
        await stream.WriteAsync(import);
        var importRep = new byte[8 + 312];
        await stream.ReadExactlyAsync(importRep);
        Check(BinaryPrimitives.ReadUInt32BigEndian(importRep.AsSpan(4)) == 0 && importRep[8 + 311] == 4, "import accepted, 4 interfaces");

        uint seq = 1;
        async Task<(int Status, byte[] Data)> Submit(uint dir, uint ep, byte[] setup, int len, byte[]? outData = null)
        {
            var h = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seq++);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), dir);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), ep);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(24), (uint)len);
            setup.CopyTo(h, 40);
            await stream.WriteAsync(h);
            if (outData is not null) await stream.WriteAsync(outData);
            var r = new byte[48];
            await stream.ReadExactlyAsync(r);
            int status = BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(20));
            int actual = BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(24));
            var data = new byte[dir == 1 ? actual : 0];
            if (data.Length > 0) await stream.ReadExactlyAsync(data);
            return (status, data);
        }

        var (st, dd) = await Submit(1, 0, [0x80, 0x06, 0x00, 0x02, 0, 0, 0xFF, 0], 0xFF);
        bool hidOnIf3 = false;
        for (int i = 0; i + 1 < dd.Length; i += dd[i])
            if (dd[i + 1] == 0x04 && dd[i + 2] == 3 && dd[i + 5] == 0x03) hidOnIf3 = true;
        Check(st == 0 && dd.Length == BinaryPrimitives.ReadUInt16LittleEndian(dd.AsSpan(2)) && hidOnIf3, "config descriptor: HID is interface 3 (MI_03)");

        (st, dd) = await Submit(1, 0, [0xA1, 0x01, 0x09, 0x03, 3, 0, 20, 0], 20);
        Check(st == 0 && dd.Length == 20 && dd[0] == 0x09, "GET_REPORT(feature 0x09 MAC)");
        (st, dd) = await Submit(1, 0, [0xA1, 0x01, 0x80, 0x03, 3, 0, 64, 0], 64);
        Check(st == 0 && dd.Length == 64 && dd[0] == 0x80, "GET_REPORT(other USB feature) answers instead of stalling");
        (st, dd) = await Submit(1, 0, [0x81, 0x06, 0x00, 0x22, 3, 0, 0xFF, 0x01], 0x1FF);
        Check(st == 0 && dd.Length == 289, "HID report descriptor is the real 289-byte one");

        (st, dd) = await Submit(1, 4, new byte[8], 64);
        Check(st == 0 && dd.Length == 64 && dd[0] == 0x01, "interrupt IN: 64-byte USB report 0x01");

        // Latency: a held IN transfer must complete within a few ms of an input change,
        // even though the regular interval is far away.
        var slow = new VirtualDualSense(() => state, DualSenseModel.DualSense, DualSenseTransport.Usb,
                                        DualSenseReports.ParseMac("02:C1:A5:5E:00:01"), TimeSpan.FromSeconds(1), false, 0, _ => { });
        Check(slow.PollInterruptIn(4, force: true) is not null && slow.PollInterruptIn(4, force: false) is null, "unchanged input is not re-sent early");
        state.Buttons.Add(DsButton.Circle);
        Check(slow.PollInterruptIn(4, force: false) is { } changed && (changed[8] & 0x40) != 0, "changed input is sent immediately");
        state.Buttons.Remove(DsButton.Circle);

        var outRep = new byte[48];
        outRep[0] = 0x02; outRep[1] = 0x01; outRep[3] = 0x33; outRep[4] = 0x44;
        (st, _) = await Submit(0, 3, new byte[8], 48, outRep);
        await Task.Delay(50);
        Check(st == 0 && got is { MotorRight: 0x33, MotorLeft: 0x44 }, "interrupt OUT 0x02 rumble reaches the app");

        (st, dd) = await Submit(1, 0, [0xA1, 0x81, 0x00, 0x02, 0x00, 0x02, 2, 0], 2);
        Check(st == 0 && dd.Length == 2, "audio GET_CUR volume on speaker feature unit");
        (st, dd) = await Submit(1, 0, [0xA2, 0x81, 0x00, 0x01, 0x82, 0x00, 3, 0], 3);
        Check(st == 0 && dd.SequenceEqual(new byte[] { 0x80, 0xBB, 0x00 }), "audio GET_CUR sample rate = 48000");
        (st, _) = await Submit(0, 0, [0x01, 0x0B, 0x01, 0x00, 0x02, 0x00, 0, 0], 0);
        Check(st == 0, "SET_INTERFACE mic alt 1");

        // Isochronous IN on the mic endpoint: 4 packets of up to 196 bytes -> 4 x 192 bytes of silence.
        var h = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0), 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), 999);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(24), 4 * 196);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(32), 4);
        var desc = new byte[64];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(i * 16), (uint)(i * 196));
            BinaryPrimitives.WriteUInt32BigEndian(desc.AsSpan(i * 16 + 4), 196);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await stream.WriteAsync(h); await stream.WriteAsync(desc);
        var rh = new byte[48];
        await stream.ReadExactlyAsync(rh);
        int isoActual = BinaryPrimitives.ReadInt32BigEndian(rh.AsSpan(24));
        var isoData = new byte[isoActual]; await stream.ReadExactlyAsync(isoData);
        var isoDesc = new byte[64]; await stream.ReadExactlyAsync(isoDesc);
        double ms = sw.Elapsed.TotalMilliseconds;
        Check(BinaryPrimitives.ReadUInt32BigEndian(rh.AsSpan(4)) == 999 && isoActual == 768 &&
              BinaryPrimitives.ReadUInt32BigEndian(isoDesc.AsSpan(8)) == 192 && BinaryPrimitives.ReadUInt32BigEndian(isoDesc.AsSpan(48)) == 3 * 196,
              "iso IN mic: 4 x 192 bytes, descriptors returned");
        Check(ms >= 3, $"iso URB paced in real time ({ms:F1} ms for 4 packets)");
        cts.Cancel();
    }

    private static async Task UsbIpLoopback(DualSenseState state)
    {
        var dev = new VirtualDualSense(() => state, DualSenseModel.DualSense, DualSenseTransport.Bluetooth, DualSenseReports.ParseMac("02:C1:A5:5E:00:01"),
                                       TimeSpan.FromMilliseconds(4), false, 0, _ => { });
        OutputFeedback? got = null;
        dev.Output += f => got = f;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var port = FreePort();
        var server = new UsbIpServer(dev, IPAddress.Loopback, port, _ => { });
        var listening = new TaskCompletionSource();
        _ = server.RunAsync(listening, cts.Token);
        await listening.Task;

        // Device list
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, port);
            var ns = c.GetStream();
            await ns.WriteAsync(new byte[] { 0x01, 0x11, 0x80, 0x05, 0, 0, 0, 0 });
            var rep = new byte[8 + 4 + 312 + 4];
            await ns.ReadExactlyAsync(rep);
            ushort vid = BinaryPrimitives.ReadUInt16BigEndian(rep.AsSpan(12 + 256 + 32 + 12));
            ushort pid = BinaryPrimitives.ReadUInt16BigEndian(rep.AsSpan(12 + 256 + 32 + 14));
            Check(BinaryPrimitives.ReadUInt32BigEndian(rep.AsSpan(8)) == 1 && vid == 0x054C && pid == 0x0CE6, "devlist shows 054C:0CE6");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var import = new byte[8 + 32];
        import[0] = 0x01; import[1] = 0x11; import[2] = 0x80; import[3] = 0x03;
        Encoding.ASCII.GetBytes("1-1").CopyTo(import, 8);
        await stream.WriteAsync(import);
        var importRep = new byte[8 + 312];
        await stream.ReadExactlyAsync(importRep);
        Check(BinaryPrimitives.ReadUInt32BigEndian(importRep.AsSpan(4)) == 0, "import accepted");

        uint seq = 1;
        async Task<(int Status, byte[] Data)> Submit(uint dir, uint ep, byte[] setup, int len, byte[]? outData = null)
        {
            var h = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(0), 1);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seq++);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), 0x00010002);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), dir);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), ep);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(24), (uint)len);
            setup.CopyTo(h, 40);
            await stream.WriteAsync(h);
            if (outData is not null) await stream.WriteAsync(outData);
            var r = new byte[48];
            await stream.ReadExactlyAsync(r);
            int status = BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(20));
            int actual = BinaryPrimitives.ReadInt32BigEndian(r.AsSpan(24));
            var data = new byte[dir == 1 ? actual : 0];
            if (data.Length > 0) await stream.ReadExactlyAsync(data);
            return (status, data);
        }

        var (st, dd) = await Submit(1, 0, [0x80, 0x06, 0x00, 0x01, 0, 0, 18, 0], 18);
        Check(st == 0 && dd.Length == 18 && dd[8] == 0x4C && dd[9] == 0x05, "GET_DESCRIPTOR(device)");

        (st, dd) = await Submit(1, 0, [0x81, 0x06, 0x00, 0x22, 0, 0, 0xFF, 0x01], 0x1FF);
        Check(st == 0 && dd.SequenceEqual(Descriptors.HidReport()), "GET_DESCRIPTOR(HID report)");

        (st, dd) = await Submit(1, 4, new byte[8], 78);
        Check(st == 0 && dd.Length == 10 && dd[0] == 0x01, "interrupt IN starts in BT simple mode (10-byte report 0x01)");

        (st, dd) = await Submit(1, 0, [0xA1, 0x01, 0x05, 0x03, 0, 0, 41, 0], 41);
        Check(st == 0 && dd.Length == 41 && dd[0] == 0x05, "GET_REPORT(feature 0x05 calibration)");

        (st, dd) = await Submit(1, 4, new byte[8], 78);
        Check(st == 0 && dd.Length == 78 && dd[0] == 0x31 && Crc32.Verify(Crc32.InputSeed, dd), "after calibration read: 78-byte report 0x31 with valid CRC");

        var outRep = new byte[78];
        outRep[0] = 0x31; outRep[3] = 0x01; outRep[5] = 0x11; outRep[6] = 0x22;
        Crc32.Seal(Crc32.OutputSeed, outRep);
        (st, _) = await Submit(0, 3, new byte[8], 78, outRep);
        await Task.Delay(50);
        Check(st == 0 && got is { MotorRight: 0x11, MotorLeft: 0x22 }, "interrupt OUT rumble reaches the app");

        cts.Cancel();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
