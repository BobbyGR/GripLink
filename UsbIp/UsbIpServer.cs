using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace GripLink.UsbIp;

/// <summary>
/// Minimal USB/IP server (protocol version 1.1.1) that exports one virtual device.
/// The Windows client (usbip-win2) attaches it and Windows then sees a real USB HID device.
/// </summary>
public sealed class UsbIpServer
{
    private const ushort ProtocolVersion = 0x0111;
    private const ushort OpReqDevList = 0x8005, OpRepDevList = 0x0005;
    private const ushort OpReqImport = 0x8003, OpRepImport = 0x0003;
    private const uint CmdSubmit = 1, CmdUnlink = 2, RetSubmit = 3, RetUnlink = 4;
    private const uint DirOut = 0, DirIn = 1;

    private readonly IUsbIpDevice _device;
    private readonly IPAddress _bind;
    private readonly int _port;
    private readonly Action<string> _log;
    private int _busy;

    public UsbIpServer(IUsbIpDevice device, IPAddress bind, int port, Action<string> log)
    {
        _device = device; _bind = bind; _port = port; _log = log;
    }

    public async Task RunAsync(TaskCompletionSource? listening, CancellationToken ct)
    {
        var listener = new TcpListener(_bind, _port);
        listener.Start();
        listening?.TrySetResult();
        _log($"USB/IP server listening on {_bind}:{_port}, bus id {_device.Info.BusId}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    // ------------------------------------------------------------------ operation phase

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            var hdr = new byte[8];
            await stream.ReadExactlyAsync(hdr, ct);
            ushort code = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(2));

            if (code == OpReqDevList)
            {
                var reply = new List<byte>();
                AppendOpHeader(reply, OpRepDevList, 0);
                AppendU32(reply, 1);
                AppendDevice(reply, _device.Info);
                foreach (var (cls, sub, proto) in _device.Info.Interfaces)
                    reply.AddRange([cls, sub, proto, 0]);
                await stream.WriteAsync(reply.ToArray(), ct);
                return;
            }

            if (code != OpReqImport)
            {
                _log($"USB/IP: unknown op 0x{code:X4}");
                return;
            }

            var busIdBytes = new byte[32];
            await stream.ReadExactlyAsync(busIdBytes, ct);
            string busId = Encoding.ASCII.GetString(busIdBytes).TrimEnd('\0');

            bool ok = busId == _device.Info.BusId && Interlocked.CompareExchange(ref _busy, 1, 0) == 0;
            var rep = new List<byte>();
            AppendOpHeader(rep, OpRepImport, ok ? 0u : 1u);
            if (ok) AppendDevice(rep, _device.Info);
            await stream.WriteAsync(rep.ToArray(), ct);
            if (!ok)
            {
                _log($"USB/IP: import of '{busId}' refused (unknown bus id or already attached)");
                return;
            }

            _log("USB/IP: client attached the virtual controller");
            try
            {
                _device.OnAttached();
                await new Session(this, stream).RunAsync(ct);
            }
            finally
            {
                _device.OnDetached();
                Volatile.Write(ref _busy, 0);
                _log("USB/IP: client detached");
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or SocketException)
        {
            // connection closed
        }
        catch (Exception ex)
        {
            _log($"USB/IP: session error: {ex}");
        }
    }

    private static void AppendOpHeader(List<byte> b, ushort code, uint status)
    {
        AppendU16(b, ProtocolVersion); AppendU16(b, code); AppendU32(b, status);
    }

    private static void AppendDevice(List<byte> b, UsbIpDeviceInfo d)
    {
        var path = new byte[256];
        Encoding.ASCII.GetBytes($"/sys/devices/platform/vhci_hcd/usb{d.BusNum}/{d.BusId}").CopyTo(path, 0);
        var busId = new byte[32];
        Encoding.ASCII.GetBytes(d.BusId).CopyTo(busId, 0);
        b.AddRange(path); b.AddRange(busId);
        AppendU32(b, d.BusNum); AppendU32(b, d.DevNum); AppendU32(b, d.Speed);
        AppendU16(b, d.VendorId); AppendU16(b, d.ProductId); AppendU16(b, d.BcdDevice);
        b.AddRange([d.DeviceClass, d.DeviceSubClass, d.DeviceProtocol,
                    d.ConfigurationValue, d.NumConfigurations, (byte)d.Interfaces.Length]);
    }

    private static void AppendU16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void AppendU32(List<byte> b, uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }

    // ------------------------------------------------------------------ URB phase

    private sealed class PendingIn
    {
        public uint SeqNum;
        public int Endpoint;
        public int BufferLength;
        public uint NumberOfPackets;
    }

    private sealed class Session
    {
        private readonly UsbIpServer _server;
        private readonly NetworkStream _stream;
        private readonly object _writeLock = new();
        private readonly object _pendingLock = new();
        private readonly LinkedList<PendingIn> _pending = new();
        private readonly List<PendingIso> _iso = new();
        private readonly Dictionary<(int Ep, uint Dir), double> _isoNextDue = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private volatile bool _running = true;

        private sealed class PendingIso
        {
            public uint SeqNum, Dir, StartFrame;
            public int Endpoint;
            public double DueMs;
            public int[] Offsets = [], Lengths = [];
        }

        public Session(UsbIpServer server, NetworkStream stream) { _server = server; _stream = stream; }

        public async Task RunAsync(CancellationToken ct)
        {
            var pump = new Thread(PumpInterruptIn) { IsBackground = true, Name = "USB/IP interrupt IN", Priority = ThreadPriority.Highest };
            pump.Start();
            try
            {
                var hdr = new byte[48];
                while (!ct.IsCancellationRequested)
                {
                    await _stream.ReadExactlyAsync(hdr, ct);
                    uint cmd = BE32(hdr, 0);
                    if (cmd == CmdSubmit) await HandleSubmitAsync(hdr, ct);
                    else if (cmd == CmdUnlink) HandleUnlink(hdr);
                    else throw new InvalidDataException($"USB/IP: unknown command {cmd}");
                }
            }
            finally
            {
                _running = false;
                pump.Join(500);
            }
        }

        private static uint BE32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

        private async Task HandleSubmitAsync(byte[] hdr, CancellationToken ct)
        {
            uint seq = BE32(hdr, 4);
            uint dir = BE32(hdr, 12);
            int ep = (int)BE32(hdr, 16);
            int length = (int)BE32(hdr, 24);
            uint packets = BE32(hdr, 32);
            var setup = hdr.AsSpan(40, 8).ToArray();

            byte[] outData = [];
            if (dir == DirOut && length > 0)
            {
                outData = new byte[length];
                await _stream.ReadExactlyAsync(outData, ct);
            }
            if (packets != 0 && packets != 0xFFFFFFFF)
            {
                // Isochronous (audio): one packet per 1 ms. Complete the URB in real time.
                var desc = new byte[16 * packets];
                await _stream.ReadExactlyAsync(desc, ct);
                var urb = new PendingIso
                {
                    SeqNum = seq, Dir = dir, Endpoint = ep, StartFrame = BE32(hdr, 28),
                    Offsets = new int[packets], Lengths = new int[packets],
                };
                for (int i = 0; i < packets; i++)
                {
                    urb.Offsets[i] = (int)BE32(desc, i * 16);
                    urb.Lengths[i] = (int)BE32(desc, i * 16 + 4);
                }
                if (dir == DirOut) _server._device.IsoOut(ep, outData);
                lock (_pendingLock)
                {
                    double now = _clock.Elapsed.TotalMilliseconds;
                    double start = _isoNextDue.TryGetValue((ep, dir), out var n) && n > now ? n : now;
                    urb.DueMs = start + packets;
                    _isoNextDue[(ep, dir)] = urb.DueMs;
                    _iso.Add(urb);
                }
                return;
            }

            if (ep == 0)
            {
                var sp = SetupPacket.Parse(setup);
                UrbResult result;
                try { result = _server._device.Control(sp, outData); }
                catch (Exception ex) { _server._log($"control {sp} failed: {ex.Message}"); result = UrbResult.Stall; }
                if (dir == DirIn && result.Data.Length > length)
                    result = result with { Data = result.Data[..length] };
                SendRetSubmit(seq, result, dir, packets, dir == DirOut ? length : result.Data.Length);
            }
            else if (dir == DirOut)
            {
                _server._device.InterruptOut(ep, outData);
                SendRetSubmit(seq, UrbResult.Ok(), dir, packets, length);
            }
            else
            {
                // Interrupt IN: hold the URB until the next poll interval.
                lock (_pendingLock)
                    _pending.AddLast(new PendingIn { SeqNum = seq, Endpoint = ep, BufferLength = length, NumberOfPackets = packets });
            }
        }

        private void HandleUnlink(byte[] hdr)
        {
            uint seq = BE32(hdr, 4);
            uint victim = BE32(hdr, 20);
            bool removed = false;
            lock (_pendingLock)
            {
                for (var n = _pending.First; n != null; n = n.Next)
                {
                    if (n.Value.SeqNum == victim) { _pending.Remove(n); removed = true; break; }
                }
                if (!removed) removed = _iso.RemoveAll(u => u.SeqNum == victim) > 0;
            }
            var r = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(0), RetUnlink);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), seq);
            BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(20), removed ? UrbResult.ECONNRESET : 0);
            Write(r, []);
        }

        private void SendRetSubmit(uint seq, UrbResult result, uint dir, uint packets, int actualLength)
        {
            var r = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(0), RetSubmit);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), seq);
            // devid, direction, ep are zero in replies
            BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(20), result.Status);
            BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(24), result.Status == 0 ? actualLength : 0);
            BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(32), packets);
            byte[] payload = dir == DirIn && result.Status == 0 ? result.Data : [];
            Write(r, payload);
        }

        private void Write(byte[] header, byte[] payload)
        {
            lock (_writeLock)
            {
                if (payload.Length == 0) _stream.Write(header);
                else
                {
                    var buf = new byte[header.Length + payload.Length];
                    header.CopyTo(buf, 0); payload.CopyTo(buf, header.Length);
                    _stream.Write(buf);
                }
            }
        }

        private void PumpInterruptIn()
        {
            TimeBeginPeriod(1);
            try
            {
                using var timer = new MillisecondTimer();
                double intervalMs = _server._device.InterruptInterval.TotalMilliseconds;
                double next = _clock.Elapsed.TotalMilliseconds + intervalMs;
                var dueIso = new List<PendingIso>();
                while (_running)
                {
                    double now = _clock.Elapsed.TotalMilliseconds;

                    dueIso.Clear();
                    lock (_pendingLock)
                    {
                        for (int i = _iso.Count - 1; i >= 0; i--)
                            if (_iso[i].DueMs <= now) { dueIso.Add(_iso[i]); _iso.RemoveAt(i); }
                    }
                    dueIso.Sort((a, b) => a.DueMs.CompareTo(b.DueMs));
                    try { foreach (var u in dueIso) CompleteIso(u); }
                    catch { _running = false; break; }

                    // Interrupt IN: send at once when the input changed, otherwise every interval.
                    PendingIn? urb = null;
                    byte[]? data = null;
                    lock (_pendingLock)
                    {
                        if (_pending.First is { } first)
                        {
                            try { data = _server._device.PollInterruptIn(first.Value.Endpoint, force: now >= next); }
                            catch (Exception ex) { _server._log($"report build failed: {ex.Message}"); data = []; }
                            if (data is not null) { urb = first.Value; _pending.RemoveFirst(); }
                        }
                    }
                    if (urb is not null && data is not null)
                    {
                        next = now + intervalMs;
                        if (data.Length > urb.BufferLength) data = data[..urb.BufferLength];
                        try { SendRetSubmit(urb.SeqNum, UrbResult.Ok(data), DirIn, urb.NumberOfPackets, data.Length); }
                        catch { _running = false; break; }
                    }

                    timer.Wait();
                }
            }
            finally { TimeEndPeriod(1); }
        }

        /// <summary>
        /// RET_SUBMIT for an isochronous URB: header, then (IN only) each packet's data packed
        /// back to back, then one 16-byte descriptor per packet (offset, length, actual, status).
        /// </summary>
        private void CompleteIso(PendingIso u)
        {
            int n = u.Lengths.Length;
            var actual = new int[n];
            int total = 0;
            byte[] inData = [];
            if (u.Dir == DirIn)
            {
                inData = new byte[u.Lengths.Sum()];
                for (int i = 0; i < n; i++)
                {
                    actual[i] = _server._device.IsoIn(u.Endpoint, inData.AsSpan(total, u.Lengths[i]));
                    total += actual[i]; // packets are packed back to back
                }
            }
            else
            {
                for (int i = 0; i < n; i++) { actual[i] = u.Lengths[i]; total += actual[i]; }
            }
            int dataLen = u.Dir == DirIn ? total : 0;
            var buf = new byte[48 + dataLen + 16 * n];
            if (dataLen > 0) inData.AsSpan(0, dataLen).CopyTo(buf.AsSpan(48));
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0), RetSubmit);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), u.SeqNum);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(20), 0);
            BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(24), total);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(28), u.StartFrame);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(32), (uint)n);
            int d = 48 + dataLen;
            for (int i = 0; i < n; i++, d += 16)
            {
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(d), (uint)u.Offsets[i]);
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(d + 4), (uint)u.Lengths[i]);
                BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(d + 8), (uint)actual[i]);
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(d + 12), 0);
            }
            lock (_writeLock) _stream.Write(buf);
        }

        /// <summary>Sleeps ~1 ms precisely using a high-resolution waitable timer (no busy-waiting).</summary>
        private sealed class MillisecondTimer : IDisposable
        {
            private readonly IntPtr _h = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);

            public void Wait()
            {
                if (_h == IntPtr.Zero) { Thread.Sleep(1); return; }
                long due = -10_000; // 1 ms, relative, in 100 ns units
                if (SetWaitableTimer(_h, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(_h, 50);
                else Thread.Sleep(1);
            }

            public void Dispose() { if (_h != IntPtr.Zero) CloseHandle(_h); }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWaitableTimerExW(IntPtr sa, string? name, uint flags, uint access);
            [DllImport("kernel32.dll")] private static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
            [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
            [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);
    }
}
