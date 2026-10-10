using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WOMicNext;

internal interface IAudioOutput : IDisposable
{
    bool LocalPlayback { get; set; }
    bool MicrophoneEnabled { get; set; }
    void Write(short[] samples, int count);
}

internal sealed class OutputUnavailableException(string message) : IOException(message);

internal sealed class WomicOutput : IAudioOutput
{
    private readonly SafeFileHandle handle;
    private readonly LocalPlayer player = new();
    private readonly byte[] pcm = new byte[1920];
    public bool LocalPlayback { get; set; }
    public bool MicrophoneEnabled { get; set; } = true;
    public byte DriverVersion { get; }
    public Action<string>? MonitorWarning { get; set; }

    public WomicOutput()
    {
        // Do not share/reset the global driver ring while the official client is running.
        if (Process.GetProcessesByName("WOMicClient").Length != 0)
            throw new OutputUnavailableException(Localization.T("请先退出 WO Mic Client，再使用这个客户端。", "Close WO Mic Client before using this app."));
        handle = Native.CreateFile(@"\\.\WOMic", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new OutputUnavailableException(Localization.F("无法打开 WO Mic 虚拟麦克风：{0}（{1}）。请确认驱动已安装。", "Could not open the WO Mic virtual microphone: {0} ({1}). Check that the driver is installed.", new Win32Exception(code).Message, code));
        }
        try
        {
            byte[] version = new byte[1];
            Control(0x22200C, null, version);
            DriverVersion = version[0];
            if (DriverVersion != 3)
                throw new OutputUnavailableException(Localization.F("当前 WO Mic 驱动接口版本为 {0}；此版本已适配接口 3。", "WO Mic driver interface version {0} is not supported; this app targets interface 3.", DriverVersion));
            byte[] challenge = new byte[1];
            Control(0x222004, null, challenge);
            Control(0x222008, new byte[] { (byte)(challenge[0] ^ 0x3B) }, null);
            // Same ring size/enable sequence used by the locally installed official client.
            Control(0x222014, new byte[] { 0x16, 0x80 }, null);
            Control(0x222010, new byte[] { 1 }, null);
        }
        catch { handle.Dispose(); throw; }
    }

    private void Control(uint code, byte[]? input, byte[]? output)
    {
        if (!Native.DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0), output,
                (uint)(output?.Length ?? 0), out uint returned, IntPtr.Zero))
            throw new OutputUnavailableException(Localization.F("WO Mic 驱动初始化失败（{0}）。", "WO Mic driver initialization failed ({0}).", Marshal.GetLastWin32Error()));
        if (output != null && returned != output.Length)
            throw new OutputUnavailableException(Localization.T("WO Mic 驱动返回的接口数据不完整。", "The WO Mic driver returned incomplete interface data."));
    }

    public void Write(short[] samples, int count)
    {
        // The known driver handler is a bounded copy into a ring; never send oversized buffers.
        for (int offset = 0; offset < count; offset += 960)
        {
            int n = Math.Min(960, count - offset);
            if (MicrophoneEnabled) Buffer.BlockCopy(samples, offset * 2, pcm, 0, n * 2);
            else Array.Clear(pcm, 0, n * 2);
            if (!Native.WriteFile(handle, pcm, (uint)(n * 2), out _, IntPtr.Zero))
                throw new OutputUnavailableException(Localization.F("写入虚拟麦克风失败（{0}）。", "Could not write to the virtual microphone ({0}).", Marshal.GetLastWin32Error()));
        }
        if (LocalPlayback)
        {
            try { player.Write(samples, count); }
            catch (IOException ex) { LocalPlayback = false; player.Stop(); MonitorWarning?.Invoke(ex.Message); }
        }
        else player.Stop();
    }

    public void Dispose()
    {
        player.Dispose();
        // Closing this handle follows the official client; leave installed driver intact.
        handle.Dispose();
    }

    internal static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint size, out uint written, IntPtr overlapped);
    }
}

internal sealed class LocalPlayer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SamplesPerSecond, BytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    private sealed record Block(IntPtr Header, IntPtr Data);
    private IntPtr wave;
    private readonly Queue<Block> blocks = new();
    private bool unavailable;
    public void Write(short[] samples, int count)
    {
        if (unavailable) return;
        if (wave == IntPtr.Zero)
        {
            WaveFormat format = new() { Tag = 1, Channels = 1, SamplesPerSecond = 48000,
                BytesPerSecond = 96000, BlockAlign = 2, BitsPerSample = 16 };
            if (waveOutOpen(out wave, unchecked((uint)-1), ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            { unavailable = true; throw new IOException(Localization.T("本地播放设备不可用。请取消本地播放，或检查默认扬声器。", "Local playback device unavailable. Turn off local playback or check the default speakers.")); }
        }
        ReleaseFinished();
        if (blocks.Count >= 6) return; // Bounded monitor latency; never stall microphone delivery.
        IntPtr data = Marshal.AllocHGlobal(count * 2);
        IntPtr header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        Marshal.Copy(samples, 0, data, count);
        Marshal.StructureToPtr(new WaveHeader { Data = data, Length = (uint)(count * 2) }, header, false);
        uint prepared = waveOutPrepareHeader(wave, header, (uint)Marshal.SizeOf<WaveHeader>());
        if (prepared != 0)
        { Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data); throw new IOException(Localization.T("本地播放初始化失败。", "Could not initialize local playback.")); }
        if (waveOutWrite(wave, header, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
        {
            waveOutUnprepareHeader(wave, header, (uint)Marshal.SizeOf<WaveHeader>());
            Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(data);
            throw new IOException(Localization.T("本地播放失败。", "Local playback failed."));
        }
        blocks.Enqueue(new(header, data));
    }
    private void ReleaseFinished()
    {
        while (blocks.TryPeek(out var block) && (Marshal.PtrToStructure<WaveHeader>(block.Header).Flags & 1) != 0)
        { blocks.Dequeue(); Free(block); }
    }
    private void Free(Block block)
    {
        waveOutUnprepareHeader(wave, block.Header, (uint)Marshal.SizeOf<WaveHeader>());
        Marshal.FreeHGlobal(block.Header); Marshal.FreeHGlobal(block.Data);
    }
    public void Stop()
    {
        if (wave == IntPtr.Zero) return;
        waveOutReset(wave);
        while (blocks.TryDequeue(out var block)) Free(block);
        waveOutClose(wave); wave = IntPtr.Zero; unavailable = false;
    }
    public void Dispose() => Stop();
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr handle, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr handle);
}
