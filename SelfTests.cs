using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Concentus;
using Concentus.Enums;

namespace WOMicNext;

internal static class SelfTests
{
    private static readonly List<string> results = [];
    internal static readonly ConcurrentQueue<string> Trace = new();
    private static void Assert(bool condition, string detail)
    { if (!condition) throw new Exception("FAIL: " + detail); results.Add("PASS: " + detail); }
    public static async Task RunAsync(bool driver)
    {
        results.Clear();
        TestRecentStatistics();
        Assert(Protocol.IsNewer(0, 65535) && !Protocol.IsNewer(65535, 0), "16-bit sequence rollover and stale packet rejection");
        Assert(!Protocol.TryAudio(new byte[11], out _, out _), "Reject empty or truncated UDP frames");
        byte[] oversized = { 0x65, 0x00, 0x00, 0x10, 0x01 };
        try { await Protocol.ReadAsync(new MemoryStream(oversized), 0x65, default); throw new Exception("Oversized response accepted"); }
        catch (IOException) { results.Add("PASS: TCP payload size is bounded before allocation"); }

        await TestConnectedAsync();
        await TestCancelHandshakeAsync();
        await TestReconnectAsync();
        await TestBudgetAsync();
        await TestDoNothingAsync();
        await TestInitialFailureAsync();
        if (driver) await TestDriverAsync();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), string.Join(Environment.NewLine, results));
    }
    private static ConnectionController Controller(MemoryOutput output)
    {
        var controller = new ConnectionController(() => output)
        {
            EstablishTimeout = TimeSpan.FromMilliseconds(900), AudioTimeout = TimeSpan.FromMilliseconds(300),
            RetryDelay = TimeSpan.FromMilliseconds(100)
        };
        controller.Status += update => Trace.Enqueue(update.State + ": " + update.Message);
        return controller;
    }
    private static ConnectionOptions Options(MockPhone phone) => new("127.0.0.1", phone.Port, FreeUdpPort());
    private static int FreeUdpPort() { using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)udp.Client.LocalEndPoint!).Port; }
    private static async Task WaitAsync(Func<bool> condition, int milliseconds = 3500)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Test condition timed out"); await Task.Delay(10); }
    }
    private static async Task TestConnectedAsync()
    {
        await using var phone = new MockPhone();
        var output = new MemoryOutput(); var controller = Controller(output);
        int alerts = 0; controller.Alert += (_, _) => alerts++;
        controller.Start(Options(phone));
        await WaitAsync(() => controller.Packets > 10);
        Assert(controller.State == ConnectionState.Connected && output.Nonzero > 0, "Fragmented handshake, Opus decode, and real PCM delivery before Connected");
        int sentBeforeMute = output.Nonzero;
        controller.MicrophoneEnabled = false;
        await Task.Delay(120);
        int mutedCount = output.Nonzero;
        long packetsWhileMuted = controller.Packets;
        await Task.Delay(120);
        Assert(output.Nonzero == mutedCount && controller.Packets > packetsWhileMuted && mutedCount >= sentBeforeMute,
            "Microphone mute keeps the network session and packet reception active while suppressing driver audio");
        controller.MicrophoneEnabled = true;
        await WaitAsync(() => output.Nonzero > sentBeforeMute);
        Assert(controller.Dropped >= 2, "Duplicate and reordered audio packets are discarded");
        var recent = controller.Statistics.Snapshot();
        Assert(recent.Lost == 1 && recent.Discarded == 1 && recent.Received > 0, "Recent missing packets are separated from late/invalid packets");
        controller.LocalPlayback = true;
        await WaitAsync(() => output.SawMonitor);
        var sw = Stopwatch.StartNew(); await controller.ShutdownAsync();
        Assert(sw.ElapsedMilliseconds < 700 && alerts == 0 && controller.State == ConnectionState.Disconnected,
            "Manual disconnect cancels promptly without reconnect or notification");
    }
    private static async Task TestCancelHandshakeAsync()
    {
        await using var phone = new MockPhone { StallHandshake = true };
        var controller = Controller(new MemoryOutput()); controller.Start(Options(phone));
        await WaitAsync(() => phone.Accepted > 0);
        var sw = Stopwatch.StartNew(); await controller.ShutdownAsync();
        Assert(sw.ElapsedMilliseconds < 700, "Cancel and close while phone handshake is stalled");
    }
    private static void TestRecentStatistics()
    {
        long now = 0;
        var statistics = new PacketStatistics(() => now);
        statistics.Record(received: 200);
        now = 8000; statistics.Record(lost: 2, discarded: 3);
        now = 9900; var current = statistics.Snapshot();
        Assert(current.Received == 200 && current.Lost == 2 && current.Discarded == 3 && Math.Abs(current.LossPercent - 100.0 * 2 / 202) < 0.0001,
            "10-second statistics distinguish network gaps from discarded packets");
        now = 10000; current = statistics.Snapshot();
        Assert(current.Received == 0 && current.Lost == 2, "Old received counts expire without new traffic");
        now = 18000;
        Assert(statistics.Snapshot() == new RecentPackets(0, 0, 0), "Loss statistics return to zero after their rolling window expires");
        now = 98765400; statistics.Record(received: 5); statistics.Reset();
        Assert(statistics.Snapshot() == new RecentPackets(0, 0, 0), "Reconnect reset removes stale samples after long runtimes");
    }
    private static async Task TestReconnectAsync()
    {
        await using var phone = new MockPhone { CutFirst = true };
        var controller = Controller(new MemoryOutput());
        int alerts = 0; controller.Alert += (_, _) => Interlocked.Increment(ref alerts);
        controller.Policy = new(DisconnectAction.Reconnect, AlertKind.Windows, 2);
        controller.Start(Options(phone));
        await WaitAsync(() => phone.Accepted >= 2 && controller.State == ConnectionState.Connected);
        Assert(alerts == 1, "Unexpected disconnection reconnects and alerts once per outage");
        // Connected session must outlive the original retry deadline.
        await Task.Delay(2200);
        Assert(controller.State == ConnectionState.Connected && phone.Accepted == 2, "Successful recovery cancels the retry deadline");
        await controller.ShutdownAsync();
    }
    private static async Task TestBudgetAsync()
    {
        await using var phone = new MockPhone { CutFirst = true, StallAfterFirst = true };
        var controller = Controller(new MemoryOutput()); controller.Policy = new(DisconnectAction.Reconnect, AlertKind.None, 1);
        controller.Start(Options(phone));
        await WaitAsync(() => controller.State == ConnectionState.Connected);
        var sw = Stopwatch.StartNew();
        await WaitAsync(() => !controller.IsRunning);
        Assert(sw.ElapsedMilliseconds < 2300 && controller.State == ConnectionState.Disconnected, "Overall reconnect budget bounds a stalled retry and stops automatically");
        await controller.ShutdownAsync();
    }
    private static async Task TestDoNothingAsync()
    {
        await using var phone = new MockPhone { CutFirst = true };
        var controller = Controller(new MemoryOutput()); controller.Policy = new(DisconnectAction.DoNothing, AlertKind.SoundAndWindows, 1);
        int alerts = 0; controller.Alert += (_, _) => alerts++;
        controller.Start(Options(phone)); await WaitAsync(() => controller.Packets > 0); await WaitAsync(() => !controller.IsRunning);
        Assert(phone.Accepted == 1 && alerts == 0, "Do Nothing disables both reconnect and reminders");
        await controller.ShutdownAsync();
    }
    private static async Task TestInitialFailureAsync()
    {
        await using var phone = new MockPhone { StallHandshake = true };
        var controller = Controller(new MemoryOutput()); controller.Start(Options(phone));
        await WaitAsync(() => !controller.IsRunning);
        Assert(phone.Accepted == 1 && controller.State == ConnectionState.Disconnected,
            "Initial connection timeout returns an inline error without automatic retry");
        await controller.ShutdownAsync();
    }
    private static async Task TestDriverAsync()
    {
        // Capture only the existing virtual WO Mic endpoint, using synthetic test audio.
        using var output = new WomicOutput();
        Assert(output.DriverVersion == 3, "Installed WO Mic driver opens and initializes without replacing it");
        using var capture = new VirtualCapture();
        await using var phone = new MockPhone();
        var controller = new ConnectionController(() => new WomicOutput()) { EstablishTimeout = TimeSpan.FromSeconds(2) };
        controller.Start(Options(phone));
        await WaitAsync(() => controller.Packets > 35, 4000);
        await controller.ShutdownAsync();
        var samples = capture.ReadSamples();
        int peaks = samples.Count(s => Math.Abs((int)s) > 500);
        Assert(peaks > 500, $"End-to-end mock phone → Opus → installed WO Mic Device → capture: {peaks} nonzero samples");
    }

    internal sealed class MemoryOutput : IAudioOutput
    {
        public bool LocalPlayback { get; set; }
        public bool MicrophoneEnabled { get; set; } = true;
        public volatile bool SawMonitor;
        public int Nonzero;
        public void Write(short[] samples, int count)
        { if (MicrophoneEnabled && samples.Take(count).Any(s => s != 0)) Interlocked.Increment(ref Nonzero); SawMonitor |= LocalPlayback; }
        public void Dispose() { }
    }

    internal sealed class MockPhone : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancel = new();
        private readonly ConcurrentBag<Task> sessions = [];
        private readonly Task loop;
        public bool StallHandshake, CutFirst, StallAfterFirst;
        public int Accepted;
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public MockPhone() { listener.Start(); loop = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(cancel.Token);
                    int ordinal = Interlocked.Increment(ref Accepted);
                    sessions.Add(ServeAsync(client, ordinal));
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (cancel.IsCancellationRequested) { }
        }
        private async Task ServeAsync(TcpClient client, int ordinal)
        {
            using (client)
            {
                try
                {
                    var ct = cancel.Token; var stream = client.GetStream();
                    if (StallHandshake || (StallAfterFirst && ordinal > 1)) { await Task.Delay(Timeout.Infinite, ct); return; }
                    await Protocol.ReadAsync(stream, 0x65, ct);
                    await FragmentedAsync(stream, 0x65, new byte[] { 0, 4 }, ct);
                    byte[] media = await Protocol.ReadAsync(stream, 0x66, ct);
                    int port = BinaryPrimitives.ReadUInt16BigEndian(media.AsSpan(4));
                    await FragmentedAsync(stream, 0x66, new byte[] { 0 }, ct);
                    await Protocol.ReadAsync(stream, 0x67, ct);
                    await FragmentedAsync(stream, 0x67, new byte[] { 0 }, ct);
                    using var udp = new UdpClient(AddressFamily.InterNetwork);
                    using IOpusEncoder encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
                    short[] pcm = new short[960]; byte[] encoded = new byte[1275];
                    for (int i = 0; ; i++)
                    {
                        for (int j = 0; j < 960; j++) pcm[j] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * (i * 960 + j) / 48000));
                        int size = encoder.Encode(pcm, 960, encoded, encoded.Length);
                        byte[] packet = new byte[size + 11]; packet[0] = 4;
                        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(size + 7));
                        ushort seq = (ushort)(i == 2 ? 4 : i == 3 ? 3 : i + 1);
                        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), seq);
                        encoded.AsSpan(0, size).CopyTo(packet.AsSpan(11));
                        await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, port), ct);
                        if (CutFirst && ordinal == 1 && i == 9) return;
                        await Task.Delay(20, ct);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is OperationCanceledException || ex is SocketException) { }
            }
        }
        private static async Task FragmentedAsync(Stream stream, byte command, byte[] payload, CancellationToken ct)
        {
            using var packet = new MemoryStream(); await Protocol.SendAsync(packet, command, payload, ct);
            foreach (byte b in packet.ToArray()) { await stream.WriteAsync(new byte[] { b }, ct); await Task.Delay(1, ct); }
        }
        public async ValueTask DisposeAsync()
        {
            cancel.Cancel(); listener.Stop();
            await loop; await Task.WhenAll(sessions); cancel.Dispose();
        }
    }

    private sealed class VirtualCapture : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Caps { public ushort Manufacturer, Product; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; }
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct Format { public ushort Tag, Channels; public uint Rate, Bytes; public ushort Align, Bits, Extra; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Header { public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved; }
        private readonly IntPtr wave, data, header;
        public VirtualCapture()
        {
            uint found = uint.MaxValue;
            for (uint i = 0; i < waveInGetNumDevs(); i++)
                if (waveInGetDevCapsW((UIntPtr)i, out Caps caps, (uint)Marshal.SizeOf<Caps>()) == 0 && caps.Name.Contains("WO Mic", StringComparison.OrdinalIgnoreCase)) { found = i; break; }
            if (found == uint.MaxValue) throw new IOException("WO Mic input endpoint not found");
            Format format = new() { Tag = 1, Channels = 1, Rate = 48000, Bytes = 96000, Align = 2, Bits = 16 };
            if (waveInOpen(out wave, found, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) throw new IOException("Cannot capture WO Mic endpoint");
            data = Marshal.AllocHGlobal(96000 * 2); header = Marshal.AllocHGlobal(Marshal.SizeOf<Header>());
            Marshal.StructureToPtr(new Header { Data = data, Length = 96000 * 2 }, header, false);
            if (waveInPrepareHeader(wave, header, (uint)Marshal.SizeOf<Header>()) != 0 || waveInAddBuffer(wave, header, (uint)Marshal.SizeOf<Header>()) != 0 || waveInStart(wave) != 0)
                throw new IOException("Cannot start WO Mic capture");
        }
        public short[] ReadSamples()
        {
            waveInStop(wave); waveInReset(wave);
            int count = (int)(Marshal.PtrToStructure<Header>(header).Recorded / 2);
            short[] samples = new short[count]; Marshal.Copy(data, samples, 0, count); return samples;
        }
        public void Dispose()
        { waveInReset(wave); waveInUnprepareHeader(wave, header, (uint)Marshal.SizeOf<Header>()); waveInClose(wave); Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); }
        [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCapsW(UIntPtr device, out Caps caps, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr handle);
    }
}
