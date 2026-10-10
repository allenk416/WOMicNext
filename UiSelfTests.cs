using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WOMicNext;

internal static class UiSelfTests
{
    public static void Run()
    {
        var phone = new SelfTests.MockPhone();
        var output = new SelfTests.MemoryOutput();
        var controller = new ConnectionController(() => output);
        using var form = new MainForm(new Settings(), startHidden: true, connection: controller);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        List<string> results = [];
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        int phase = 0;
        long packetsWhenHidden = 0;
        long packetsWhenMuted = 0;
        int mutedBaseline = 0;
        void Check(bool value, string description)
        { if (!value) throw new Exception("FAIL: " + description); results.Add("PASS: " + description); }
        timer.Tick += (_, _) =>
        {
            try
            {
                if (clock.Elapsed.TotalSeconds > 8) throw new TimeoutException("UI lifecycle test timed out");
                switch (phase)
                {
                    case 0:
                        Check(form.IsHandleCreated && !form.Visible, "Startup opens in tray without displaying the main window");
                        form.RestoreWindow();
                        Check(form.Visible && form.ShowInTaskbar, "Restore from tray shows the main window");
                        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
                        {
                            int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
                            udp.Dispose();
                            controller.Start(new("127.0.0.1", phone.Port, port));
                        }
                        phase++; break;
                    case 1:
                        if (controller.State != ConnectionState.Connected) return;
                        form.LanguageSelector.SelectedIndex = 1;
                        Check(form.ConnectionButton.Text == "Disconnect", "English can be selected while connected");
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "ui-preview-en.png"));
                        }
                        form.MicrophoneToggle.Checked = false;
                        Check(!controller.MicrophoneEnabled && controller.State == ConnectionState.Connected,
                            "Microphone can be muted without ending the connection");
                        packetsWhenMuted = controller.Packets;
                        phase++; break;
                    case 2:
                        if (controller.Packets <= packetsWhenMuted + 4) return;
                        mutedBaseline = output.Nonzero;
                        phase++; break;
                    case 3:
                        if (controller.Packets <= packetsWhenMuted + 9) return;
                        Check(controller.IsRunning && output.Nonzero == mutedBaseline,
                            "Audio keeps arriving while mute suppresses virtual microphone output");
                        form.MicrophoneToggle.Checked = true;
                        phase++; break;
                    case 4:
                        if (output.Nonzero <= mutedBaseline) return;
                        form.LanguageSelector.SelectedIndex = 0;
                        packetsWhenHidden = controller.Packets;
                        form.Close();
                        Check(!form.Visible && !form.IsDisposed && controller.State == ConnectionState.Connected,
                            "Close hides the window without ending the active microphone connection");
                        phase++; break;
                    case 5:
                        if (controller.Packets <= packetsWhenHidden + 5) return;
                        Check(controller.IsRunning, "Audio continues to be delivered while the main window is hidden");
                        form.RestoreWindow(); Check(form.Visible, "Tray restore works after close");
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "ui-preview.png"));
                        }
                        phase++; timer.Stop(); form.RequestExit(); break;
                }
            }
            catch (Exception ex) { failure = ex; timer.Stop(); form.RequestExit(); }
        };
        timer.Start(); Application.Run(form);
        phone.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (failure != null) throw failure;
        Check(!controller.IsRunning && controller.State == ConnectionState.Disconnected && form.IsDisposed,
            "Tray Exit shuts down audio and releases the window");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "ui-test-results.txt"), results);
    }
}
