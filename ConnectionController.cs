using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Concentus;

namespace WOMicNext;

internal sealed class ConnectionController
{
    private readonly Func<IAudioOutput> outputFactory;
    private CancellationTokenSource? cancellation;
    private Task? operation;
    private DisconnectPolicy policy = new(DisconnectAction.Reconnect, AlertKind.None, 12);
    private int monitor, microphone = 1, state;
    private long packets, dropped;
    private double level;
    public PacketStatistics Statistics { get; } = new();
    public event Action<StatusUpdate>? Status;
    public event Action<AlertKind, string>? Alert;
    public event Action<string>? MonitorWarning;
    public ConnectionState State => (ConnectionState)Volatile.Read(ref state);
    public bool IsRunning => operation is { IsCompleted: false };
    public long Packets => Interlocked.Read(ref packets);
    public long Dropped => Interlocked.Read(ref dropped);
    public double Level => Volatile.Read(ref level);
    public bool LocalPlayback { get => Volatile.Read(ref monitor) != 0; set => Volatile.Write(ref monitor, value ? 1 : 0); }
    public bool MicrophoneEnabled { get => Volatile.Read(ref microphone) != 0; set => Volatile.Write(ref microphone, value ? 1 : 0); }
    public DisconnectPolicy Policy { get => Volatile.Read(ref policy); set => Volatile.Write(ref policy, value); }
    internal TimeSpan EstablishTimeout { get; set; } = TimeSpan.FromSeconds(4);
    internal TimeSpan AudioTimeout { get; set; } = TimeSpan.FromSeconds(3);
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public ConnectionController(Func<IAudioOutput>? factory = null) => outputFactory = factory ?? (() => new WomicOutput());

    public void Start(ConnectionOptions options)
    {
        if (IsRunning) return;
        cancellation?.Dispose();
        cancellation = new();
        var token = cancellation.Token;
        Interlocked.Exchange(ref packets, 0); Interlocked.Exchange(ref dropped, 0);
        Statistics.Reset();
        Publish(ConnectionState.Connecting, Localization.T("正在连接手机… 可随时取消。", "Connecting to phone… you can cancel anytime."));
        operation = Task.Run(() => RunAsync(options, token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        Publish(ConnectionState.Stopping, Localization.T("正在断开…", "Disconnecting…"));
        cancellation?.Cancel();
    }

    public async Task ShutdownAsync()
    {
        Stop();
        if (operation != null) await operation.ConfigureAwait(false);
        cancellation?.Dispose(); cancellation = null;
    }

    private void Publish(ConnectionState next, string message)
    {
        Volatile.Write(ref state, (int)next);
        if (next != ConnectionState.Connected) Volatile.Write(ref level, 0);
        Status?.Invoke(new(next, message));
    }

    private async Task RunAsync(ConnectionOptions options, CancellationToken ct)
    {
        bool connectedEver = false, reconnected = false;
        long retryStarted = 0;
        int attempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                CancellationTokenSource? budget = null;
                try
                {
                    CancellationToken sessionToken = ct;
                    if (retryStarted != 0)
                    {
                        var remaining = TimeSpan.FromSeconds(Policy.BudgetSeconds) - Stopwatch.GetElapsedTime(retryStarted);
                        if (remaining <= TimeSpan.Zero) break;
                        budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        budget.CancelAfter(remaining);
                        sessionToken = budget.Token;
                        attempt++;
                        Publish(ConnectionState.Reconnecting, Localization.F("正在重连 · 第 {0} 次 · 可随时断开", "Reconnecting · attempt {0} · cancel anytime", attempt));
                    }
                    await RunSessionAsync(options, sessionToken, () =>
                    {
                        connectedEver = true;
                        reconnected = retryStarted != 0;
                        retryStarted = 0; attempt = 0;
                        // The retry budget applies only until a valid audio packet arrives.
                        budget?.CancelAfter(Timeout.InfiniteTimeSpan);
                        Publish(ConnectionState.Connected, reconnected
                            ? Localization.T("已恢复连接，音频正在传输。", "Reconnected; audio is streaming.")
                            : Localization.T("连接成功，音频正在传输。", "Connected; audio is streaming."));
                    });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    string reason = Describe(ex);
                    if (ex is OutputUnavailableException || !connectedEver)
                    {
                        Publish(ConnectionState.Disconnected, reason);
                        return;
                    }
                    if (retryStarted == 0)
                    {
                        var current = Policy;
                        if (current.Action != DisconnectAction.DoNothing)
                            Alert?.Invoke(current.Alert, reason);
                        if (current.Action != DisconnectAction.Reconnect)
                        {
                            Publish(ConnectionState.Disconnected, Localization.T("连接意外中断。", "Connection interrupted unexpectedly. ") + reason);
                            return;
                        }
                        retryStarted = Stopwatch.GetTimestamp();
                    }
                    if (Policy.Action != DisconnectAction.Reconnect) break;
                    var remaining = TimeSpan.FromSeconds(Policy.BudgetSeconds) - Stopwatch.GetElapsedTime(retryStarted);
                    if (remaining <= TimeSpan.Zero) break;
                    Publish(ConnectionState.Reconnecting, Localization.F("连接已中断，将继续重试（最多 {0} 秒）。", "Connection interrupted; retrying for up to {0} sec. ", Math.Ceiling(remaining.TotalSeconds)) + reason);
                    await Task.Delay(remaining < RetryDelay ? remaining : RetryDelay, ct);
                }
                finally { budget?.Dispose(); }
            }
            ct.ThrowIfCancellationRequested();
            Publish(ConnectionState.Disconnected, Policy.Action == DisconnectAction.Reconnect
                ? Localization.T("重连已超时，已停止尝试。你可以修改地址后再次连接。", "Reconnect timed out. You can edit the address and try again.")
                : Localization.T("已停止自动重连。你可以手动连接。", "Automatic reconnect stopped. You can connect manually."));
        }
        catch (OperationCanceledException) { Publish(ConnectionState.Disconnected, Localization.T("已断开。", "Disconnected.")); }
        catch (Exception ex) { Publish(ConnectionState.Disconnected, Describe(ex)); }
    }

    private async Task RunSessionAsync(ConnectionOptions options, CancellationToken ct, Action connected)
    {
        var address = IPAddress.Parse(options.Ip);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var establish = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        establish.CancelAfter(EstablishTimeout);
        using var tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
        using var udp = new UdpClient(address.AddressFamily);
        udp.Client.ExclusiveAddressUse = true;
        udp.Client.ReceiveBufferSize = 64 * 1024;
        udp.Client.Bind(new IPEndPoint(address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, options.MediaPort));
        try
        {
            await tcp.ConnectAsync(address, options.ControlPort, establish.Token);
            using var stream = tcp.GetStream();
            await Protocol.HandshakeAsync(stream, options.MediaPort, establish.Token);
            using var output = outputFactory();
            if (output is WomicOutput womic)
                womic.MonitorWarning = msg => { LocalPlayback = false; MonitorWarning?.Invoke(msg); };
            using IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
            Statistics.Reset();
            Task heartbeat = HeartbeatAsync(stream, session.Token);
            Task control = DrainControlAsync(stream, session.Token);
            Task audio = ReceiveAudioAsync(udp, address, decoder, output, establish, session.Token, connected);
            try
            {
                Task finished = await Task.WhenAny(heartbeat, control, audio);
                await finished;
                throw new IOException(Localization.T("手机已关闭连接。", "The phone closed the connection."));
            }
            finally
            {
                session.Cancel(); tcp.Close(); udp.Close();
                try { await Task.WhenAll(heartbeat, control, audio); } catch { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException(Localization.T("连接或音频等待超时。请确认手机端已启动、IP 正确，且电脑允许接收音频。", "Connection or audio timed out. Check that the phone service is running, the IP is correct, and the PC allows incoming audio.")); }
    }

    private async Task ReceiveAudioAsync(UdpClient udp, IPAddress peer, IOpusDecoder decoder, IAudioOutput output,
        CancellationTokenSource establish, CancellationToken ct, Action connected)
    {
        short[] pcm = new short[5760];
        bool active = false, hasSequence = false;
        ushort previous = 0;
        long lastAudio = Stopwatch.GetTimestamp();
        int badFrames = 0;
        Task<UdpReceiveResult>? receive = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            CancellationToken timeoutToken = active ? ct : establish.Token;
            TimeSpan wait = active ? AudioTimeout - Stopwatch.GetElapsedTime(lastAudio) : EstablishTimeout;
            if (wait <= TimeSpan.Zero) throw new TimeoutException(Localization.T("超过 3 秒未收到有效音频。", "No valid audio received for more than 3 seconds."));
            receive ??= udp.ReceiveAsync(ct).AsTask();
            var result = await receive.WaitAsync(wait, timeoutToken);
            receive = null;
            if (!result.RemoteEndPoint.Address.Equals(peer)) continue;
            if (!Protocol.TryAudio(result.Buffer, out ushort sequence, out int size))
            { Interlocked.Increment(ref dropped); Statistics.Record(discarded: 1); continue; }
            if (hasSequence && !Protocol.IsNewer(sequence, previous))
            { Interlocked.Increment(ref dropped); Statistics.Record(discarded: 1); continue; }
            int count;
            try { count = decoder.Decode(result.Buffer.AsSpan(11, size), pcm, pcm.Length, false); }
            catch (Exception ex) when (ex is ArgumentException || ex is Concentus.OpusException)
            {
                Interlocked.Increment(ref dropped);
                Statistics.Record(discarded: 1);
                if (++badFrames >= 50) throw new IOException(Localization.T("连续收到无法解码的音频；手机协议可能不兼容。", "Repeated audio decode failures; the phone protocol may be incompatible."));
                continue;
            }
            if (count <= 0) continue;
            badFrames = 0;
            if (hasSequence)
            {
                int gap = unchecked((ushort)(sequence - previous));
                if (gap > 1) { Interlocked.Add(ref dropped, gap - 1); Statistics.Record(lost: gap - 1); }
            }
            previous = sequence; hasSequence = true;
            output.LocalPlayback = LocalPlayback;
            output.MicrophoneEnabled = MicrophoneEnabled;
            output.Write(pcm, count);
            Interlocked.Increment(ref packets);
            Statistics.Record(received: 1);
            double energy = 0;
            for (int i = 0; i < count; i++) energy += (double)pcm[i] * pcm[i];
            Volatile.Write(ref level, Math.Min(1, Math.Sqrt(energy / count) / 32768));
            lastAudio = Stopwatch.GetTimestamp();
            if (!active)
            {
                active = true;
                establish.CancelAfter(Timeout.InfiniteTimeSpan);
                connected();
            }
        }
    }

    private static async Task HeartbeatAsync(Stream stream, CancellationToken ct)
    {
        while (true)
        {
            await Protocol.SendAsync(stream, 0x69, ReadOnlyMemory<byte>.Empty, ct);
            await Task.Delay(1000, ct);
        }
    }
    private static async Task DrainControlAsync(Stream stream, CancellationToken ct)
    {
        byte[] scratch = new byte[256];
        while (await stream.ReadAsync(scratch, ct) != 0) { }
        throw new IOException(Localization.T("手机已断开控制连接。", "The phone closed the control connection."));
    }

    private static string Describe(Exception ex) => ex switch
    {
        SocketException socket when socket.SocketErrorCode == SocketError.AddressAlreadyInUse => Localization.T("音频端口已被占用。请关闭其他 WO Mic 客户端，或修改音频端口。", "Audio port is already in use. Close other WO Mic clients or change the audio port."),
        SocketException socket when socket.SocketErrorCode == SocketError.ConnectionRefused => Localization.T("手机拒绝连接。请在手机 WO Mic 中选择 Wi‑Fi 并点击开始。", "Phone refused the connection. Select Wi-Fi and tap Start in the WO Mic phone app."),
        SocketException socket when socket.SocketErrorCode == SocketError.NetworkUnreachable || socket.SocketErrorCode == SocketError.HostUnreachable => Localization.T("无法到达手机。请检查电脑和手机是否连接同一网络。", "Phone is unreachable. Check that the phone and PC are on the same network."),
        OperationCanceledException => Localization.T("本次连接尝试已超时。", "This connection attempt timed out."),
        _ => ex.Message
    };
}
