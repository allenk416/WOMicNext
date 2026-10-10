using System.Net;

namespace WOMicNext;

internal sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(28, 39, 56);
    private static readonly Color Muted = Color.FromArgb(99, 115, 137);
    private static readonly Color Blue = Color.FromArgb(39, 100, 229);
    private static readonly Color Red = Color.FromArgb(188, 45, 59);
    private static readonly Color Green = Color.FromArgb(23, 132, 88);
    private readonly Settings settings;
    private readonly ConnectionController controller;
    private readonly TextBox ip = new();
    private readonly NumericUpDown controlPort = PortBox(), mediaPort = PortBox();
    private readonly NumericUpDown retrySeconds = new() { Minimum = 5, Maximum = 60, Width = 74, TextAlign = HorizontalAlignment.Center };
    private readonly Button connect = new();
    private readonly CheckBox microphone = new();
    private readonly CheckBox playback = new();
    private readonly CheckBox startup = new();
    private readonly ComboBox action = new(), alert = new();
    private readonly ComboBox language = new();
    private readonly Label status = new(), message = new(), stats = new();
    private readonly AudioMeter meter = new();
    private readonly NotifyIcon tray = new();
    private readonly DisconnectSound disconnectSound = new();
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 120 };
    private readonly ToolTip tips = new();
    private bool closing;
    private bool exitRequested;
    private bool startHiddenPending;
    private bool loading = true;
    private readonly List<Icon> ownedIcons = [];
    private readonly Dictionary<(ConnectionState State, bool Microphone), Icon> trayIcons = [];
    private ToolStripMenuItem? restoreItem, exitItem;
    internal CheckBox MicrophoneToggle => microphone;
    internal ComboBox LanguageSelector => language;
    internal Button ConnectionButton => connect;

    public MainForm(Settings settings, bool startHidden = false, ConnectionController? connection = null)
    {
        this.settings = settings;
        controller = connection ?? new();
        Localization.English = settings.Language == "en-US";
        startHiddenPending = startHidden;
        Text = "WO Mic Next";
        var appIcon = TrayIcons.Create(ConnectionState.Connected, true, taskbar: true);
        ownedIcons.Add(appIcon); Icon = appIcon;
        Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(243, 246, 250);
        ForeColor = Ink;
        ClientSize = new Size(760, 700);
        MinimumSize = new Size(720, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BuildLayout();
        ip.Text = settings.PhoneIp;
        controlPort.Value = settings.ControlPort; mediaPort.Value = settings.MediaPort;
        retrySeconds.Value = settings.ReconnectSeconds;
        microphone.Checked = settings.MicrophoneEnabled;
        playback.Checked = settings.LocalPlayback;
        action.SelectedIndex = (int)settings.DisconnectAction; alert.SelectedIndex = (int)settings.AlertKind;
        language.SelectedIndex = Localization.English ? 1 : 0;
        controller.MicrophoneEnabled = microphone.Checked;
        controller.LocalPlayback = playback.Checked;
        UpdatePolicy(); loading = false; ApplyLanguage();
        UpdateState(new(ConnectionState.Disconnected, Localization.T("在手机 WO Mic 中启动 Wi‑Fi 服务，然后连接。", "Start the Wi-Fi service in the WO Mic phone app, then connect.")));
        try { startup.Checked = StartupRegistration.IsEnabled; }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
        { message.Text = Localization.T("无法读取开机启动设置。", "Could not read the startup setting."); }
        controller.Status += update => OnUi(() => UpdateState(update));
        controller.Alert += (kind, text) => OnUi(() => ShowAlert(kind, text));
        controller.MonitorWarning += text => OnUi(() => { playback.Checked = false; message.Text = text + " " + Localization.T("麦克风传输继续。", "Microphone transmission continues."); });
        connect.Click += (_, _) => ToggleConnection();
        microphone.CheckedChanged += (_, _) =>
        {
            controller.MicrophoneEnabled = microphone.Checked;
            SaveSettings(); UpdateTrayIcon();
            if (controller.State == ConnectionState.Connected)
                message.Text = microphone.Checked
                    ? Localization.T("麦克风传输已开启，连接保持不变。", "Microphone transmission is on; the connection remains active.")
                    : Localization.T("麦克风已静音，连接继续保持。", "Microphone muted; the connection remains active.");
        };
        playback.CheckedChanged += (_, _) => { controller.LocalPlayback = playback.Checked; SaveSettings(); };
        language.SelectedIndexChanged += (_, _) => { if (!loading) ApplyLanguage(); };
        action.SelectedIndexChanged += (_, _) => { UpdatePolicy(); SaveSettings(); };
        alert.SelectedIndexChanged += (_, _) => { UpdatePolicy(); SaveSettings(); };
        retrySeconds.ValueChanged += (_, _) => { UpdatePolicy(); SaveSettings(); };
        ip.Leave += (_, _) => SaveSettings();
        controlPort.ValueChanged += (_, _) => SaveSettings();
        mediaPort.ValueChanged += (_, _) => SaveSettings();
        startup.CheckedChanged += (_, _) =>
        {
            if (loading) return;
            try { StartupRegistration.SetEnabled(startup.Checked); }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
            {
                loading = true; startup.Checked = !startup.Checked; loading = false;
                message.Text = Localization.T("无法修改开机启动设置：", "Could not change startup setting: ") + ex.Message;
            }
        };
        refresh.Tick += (_, _) =>
        {
            meter.Level = controller.Level;
            RefreshStatisticsText();
            if (!controller.IsRunning && controller.State == ConnectionState.Disconnected) connect.Enabled = true;
        };
        refresh.Start();
        UpdateTrayIcon(); tray.Text = "WO Mic Next"; tray.Visible = true;
        tray.DoubleClick += (_, _) => RestoreWindow();
        var menu = new ContextMenuStrip();
        restoreItem = new ToolStripMenuItem(); restoreItem.Click += (_, _) => RestoreWindow();
        exitItem = new ToolStripMenuItem(); exitItem.Click += (_, _) => RequestExit();
        menu.Items.Add(restoreItem); menu.Items.Add(exitItem);
        tray.ContextMenuStrip = menu;
        FormClosing += CloseAsync;
    }

    private static NumericUpDown PortBox() => new() { Minimum = 1, Maximum = 65535, Width = 100, TextAlign = HorizontalAlignment.Center };
    private static Label LabelFor(string chinese, int size = 10, Color? color = null, string? english = null)
    {
        var label = new Label { ForeColor = color ?? Ink, Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), size),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        SetText(label, chinese, english ?? chinese);
        return label;
    }

    private static Label InlineLabel(string chinese, string english, Padding margin, Color? color = null)
    {
        var label = new Label { AutoSize = true, Margin = margin, ForeColor = color ?? Ink };
        SetText(label, chinese, english);
        return label;
    }

    private static void SetText(Control control, string chinese, string english)
    {
        control.Tag = new LocalizedPair(chinese, english);
        control.Text = Localization.T(chinese, english);
    }
    private static TableLayoutPanel Table(int columns, int rows) => new()
    { ColumnCount = columns, RowCount = rows, Dock = DockStyle.Fill, Margin = Padding.Empty };

    private void BuildLayout()
    {
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        Controls.Add(scroll);
        var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(24, 16, 24, 16) };
        scroll.Controls.Add(root);
        int[] heights = { 68, 184, 58, 148, 40, 78, 60 };
        for (int row = 0; row < heights.Length; row++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, heights[row]));

        var header = Table(2, 2);
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 132));
        header.RowStyles.Add(new(SizeType.Absolute, 38)); header.RowStyles.Add(new(SizeType.Percent, 100));
        var title = LabelFor("WO Mic Next", 22, null, "WO Mic Next"); title.Font = new Font("Segoe UI", 23, FontStyle.Bold);
        header.Controls.Add(title, 0, 0);
        language.DropDownStyle = ComboBoxStyle.DropDownList; language.Dock = DockStyle.Fill;
        language.Items.AddRange(["简体中文", "English"]); language.Margin = new Padding(2, 4, 0, 4);
        header.Controls.Add(language, 1, 0);
        var subtitle = LabelFor("手机麦克风，连接和音频控制都在这里。", 10, Muted, "Phone microphone, connection and audio controls in one place.");
        header.Controls.Add(subtitle, 0, 1); header.SetColumnSpan(subtitle, 2);
        root.Controls.Add(header, 0, 0);

        var connectionCard = Table(1, 4);
        connectionCard.BackColor = Color.White; connectionCard.Padding = new Padding(18, 10, 18, 10);
        connectionCard.Margin = new Padding(0, 8, 0, 4);
        connectionCard.RowStyles.Add(new(SizeType.Absolute, 29));
        connectionCard.RowStyles.Add(new(SizeType.Absolute, 57));
        connectionCard.RowStyles.Add(new(SizeType.Absolute, 36));
        connectionCard.RowStyles.Add(new(SizeType.Percent, 100));
        connectionCard.Controls.Add(LabelFor("连接到手机  ·  Wi‑Fi", 11, null, "Connect to phone  ·  Wi-Fi"), 0, 0);
        var connectionRow = Table(2, 1);
        connectionRow.ColumnStyles.Add(new(SizeType.Percent, 100)); connectionRow.ColumnStyles.Add(new(SizeType.Absolute, 174));
        ip.Font = new Font("Segoe UI", 18); ip.Dock = DockStyle.Fill;
        ip.Margin = new Padding(0, 7, 16, 7); ip.BorderStyle = BorderStyle.FixedSingle;
        ip.AccessibleName = Localization.T("手机 IP 地址", "Phone IP address"); ip.MaxLength = 45;
        SetText(connect, "连接", "Connect"); connect.Dock = DockStyle.Fill; connect.Margin = new Padding(0, 2, 0, 3);
        connect.BackColor = Blue; connect.ForeColor = Color.White; connect.FlatStyle = FlatStyle.Flat;
        connect.FlatAppearance.BorderSize = 0; connect.Font = new Font("Microsoft YaHei UI", 14, FontStyle.Bold);
        connect.Cursor = Cursors.Hand;
        connectionRow.Controls.Add(ip, 0, 0); connectionRow.Controls.Add(connect, 1, 0);
        connectionCard.Controls.Add(connectionRow, 0, 1);
        var ports = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        ports.Controls.Add(InlineLabel("控制端口", "Control port", new Padding(0, 5, 8, 0)));
        controlPort.Margin = new Padding(0, 2, 20, 0); ports.Controls.Add(controlPort);
        ports.Controls.Add(InlineLabel("音频端口", "Audio port", new Padding(0, 5, 8, 0)));
        mediaPort.Margin = new Padding(0, 2, 0, 0); ports.Controls.Add(mediaPort);
        controlPort.AccessibleName = Localization.T("控制端口", "Control port"); mediaPort.AccessibleName = Localization.T("音频端口", "Audio port");
        connectionCard.Controls.Add(ports, 0, 2);
        connectionCard.Controls.Add(LabelFor("手机与电脑连接同一网络；控制端口需与手机设置一致。", 9, Muted, "Connect the phone and PC to the same network; the control port must match the phone setting."), 0, 3);
        root.Controls.Add(connectionCard, 0, 1);

        var audioRow = Table(4, 1); audioRow.Padding = new Padding(4, 4, 4, 4);
        audioRow.ColumnStyles.Add(new(SizeType.Absolute, 166)); audioRow.ColumnStyles.Add(new(SizeType.Absolute, 174)); audioRow.ColumnStyles.Add(new(SizeType.Absolute, 58)); audioRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        SetText(microphone, "麦克风传输", "Microphone"); microphone.AutoSize = true; microphone.Dock = DockStyle.Fill;
        microphone.AccessibleName = Localization.T("麦克风传输开关", "Microphone transmission toggle");
        SetText(playback, "本地播放", "Local playback"); playback.AutoSize = true; playback.Dock = DockStyle.Fill;
        audioRow.Controls.Add(microphone, 0, 0); audioRow.Controls.Add(playback, 1, 0); audioRow.Controls.Add(LabelFor("音量", 9, Muted, "Level"), 2, 0);
        meter.Dock = DockStyle.Fill; meter.Margin = new Padding(0, 13, 0, 13); audioRow.Controls.Add(meter, 3, 0);
        root.Controls.Add(audioRow, 0, 2);

        var recovery = Table(2, 4); recovery.BackColor = Color.White; recovery.Padding = new Padding(18, 10, 18, 10);
        recovery.Margin = new Padding(0, 4, 0, 4);
        recovery.ColumnStyles.Add(new(SizeType.Percent, 50)); recovery.ColumnStyles.Add(new(SizeType.Percent, 50));
        recovery.RowStyles.Add(new(SizeType.Absolute, 27)); recovery.RowStyles.Add(new(SizeType.Absolute, 38));
        recovery.RowStyles.Add(new(SizeType.Absolute, 32)); recovery.RowStyles.Add(new(SizeType.Percent, 100));
        recovery.Controls.Add(LabelFor("意外断开时", 10, null, "On unexpected disconnect"), 0, 0); recovery.Controls.Add(LabelFor("提醒方式", 10, null, "Alert"), 1, 0);
        ConfigureCombo(action, ("自动重连", "Reconnect automatically"), ("仅提醒，不重连", "Notify only"), ("无作为", "Do nothing"));
        ConfigureCombo(alert, ("不提醒", "None"), ("专用提示音", "Custom sound"), ("Windows 通知", "Windows notification"), ("专用提示音 + Windows 通知", "Custom sound + Windows notification"));
        action.Margin = new Padding(0, 1, 18, 3); alert.Margin = new Padding(0, 1, 8, 3);
        action.AccessibleName = Localization.T("意外断线处理", "Disconnect action"); alert.AccessibleName = Localization.T("断线提醒方式", "Disconnect alert");
        var alertRow = Table(2, 1);
        alertRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        alertRow.ColumnStyles.Add(new(SizeType.Absolute, 62));
        var previewSound = new Button
        {
            AccessibleName = Localization.T("试听断线提示音", "Preview disconnect alert"), Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 0, 3), FlatStyle = FlatStyle.Flat, BackColor = Color.White,
            ForeColor = Blue, Cursor = Cursors.Hand
        };
        SetText(previewSound, "试听", "Test");
        previewSound.FlatAppearance.BorderColor = Color.FromArgb(205, 217, 237);
        previewSound.Click += (_, _) => disconnectSound.Play();
        alertRow.Controls.Add(alert, 0, 0); alertRow.Controls.Add(previewSound, 1, 0);
        recovery.Controls.Add(action, 0, 1); recovery.Controls.Add(alertRow, 1, 1);
        var retryRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        retryRow.Controls.Add(InlineLabel("最多重试", "Retry for up to", new Padding(0, 5, 8, 0)));
        retrySeconds.Margin = new Padding(0, 2, 7, 0); retrySeconds.AccessibleName = Localization.T("重连时限（秒）", "Reconnect limit (seconds)"); retryRow.Controls.Add(retrySeconds);
        retryRow.Controls.Add(InlineLabel("秒", "sec", new Padding(0, 5, 0, 0)));
        recovery.Controls.Add(retryRow, 0, 2);
        recovery.SetColumnSpan(retryRow, 2);
        var note = LabelFor("后台重连可随时停止；主动断开不会触发重连或提醒。", 9, Muted, "Background retries can be stopped anytime; manual disconnects do not trigger alerts.");
        recovery.Controls.Add(note, 0, 3); recovery.SetColumnSpan(note, 2);
        root.Controls.Add(recovery, 0, 3);

        var startupRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(4, 5, 0, 0) };
        SetText(startup, "开机启动（托盘）", "Start with Windows (tray)"); startup.AutoSize = true;
        startup.AccessibleName = Localization.T("开机启动到托盘", "Start minimized to tray");
        startupRow.Controls.Add(startup);
        startupRow.Controls.Add(InlineLabel("关闭窗口后继续运行；托盘右键可退出。", "Closing the window minimizes to tray; right-click the tray icon to exit.", new Padding(22, 4, 0, 0), Muted));
        root.Controls.Add(startupRow, 0, 4);

        message.Dock = DockStyle.Fill; message.TextAlign = ContentAlignment.MiddleLeft;
        message.Padding = new Padding(12, 4, 12, 4); message.BackColor = Color.FromArgb(231, 238, 250);
        message.ForeColor = Ink; message.Margin = new Padding(0, 8, 0, 4);
        message.AccessibleName = Localization.T("连接消息", "Connection message");
        root.Controls.Add(message, 0, 5);
        var footer = Table(2, 1);
        footer.ColumnStyles.Add(new(SizeType.Absolute, 180)); footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft;
        status.Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), 16, FontStyle.Bold); status.AccessibleName = Localization.T("连接状态", "Connection status");
        stats.Dock = DockStyle.Fill; stats.TextAlign = ContentAlignment.MiddleRight;
        stats.Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), 9); stats.ForeColor = Muted;
        tips.SetToolTip(stats, Localization.T("仅统计最近约10秒，旧数据会自动移出。\n丢包按音频序号缺口估算（含迟到）；异常包包括重复、迟到、损坏或解码失败的包。\n连接恢复后开始新的统计窗口。", "Shows the last 10 seconds; older data expires automatically.\nPacket loss is estimated from sequence gaps; invalid packets include duplicates, late, corrupt, or undecodable packets.\nStatistics reset after reconnecting."));
        tips.SetToolTip(microphone, Localization.T("取消勾选会静音 WO Mic 虚拟麦克风，同时保持手机连接和本地监听。", "Uncheck to mute the WO Mic virtual microphone while keeping the phone connection and local monitoring active."));
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(stats, 1, 0);
        root.Controls.Add(footer, 0, 6);
        AcceptButton = connect;
    }

    private static void ConfigureCombo(ComboBox combo, params (string Chinese, string English)[] choices)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.Dock = DockStyle.Fill;
        combo.Tag = choices.Select(x => new LocalizedPair(x.Chinese, x.English)).ToArray();
        combo.Items.AddRange(choices.Select(x => Localization.T(x.Chinese, x.English)).Cast<object>().ToArray());
    }

    private void ApplyLanguage()
    {
        Localization.English = language.SelectedIndex == 1;
        settings.Language = Localization.English ? "en-US" : "zh-CN";
        ApplyLocalizedText(this);
        ip.AccessibleName = Localization.T("手机 IP 地址", "Phone IP address");
        controlPort.AccessibleName = Localization.T("控制端口", "Control port");
        mediaPort.AccessibleName = Localization.T("音频端口", "Audio port");
        microphone.AccessibleName = Localization.T("麦克风传输开关", "Microphone transmission toggle");
        playback.AccessibleName = Localization.T("本地播放（扬声器）", "Local playback (speakers)");
        action.AccessibleName = Localization.T("意外断线处理", "Disconnect action");
        alert.AccessibleName = Localization.T("断线提醒方式", "Disconnect alert");
        startup.AccessibleName = Localization.T("开机启动到托盘", "Start minimized to tray");
        status.AccessibleName = Localization.T("连接状态", "Connection status");
        message.AccessibleName = Localization.T("连接消息", "Connection message");
        meter.AccessibleName = Localization.T("输入音量", "Input level");
        tips.SetToolTip(stats, Localization.T("仅统计最近约10秒，旧数据会自动移出。\n丢包按音频序号缺口估算（含迟到）；异常包包括重复、迟到、损坏或解码失败的包。\n连接恢复后开始新的统计窗口。", "Shows the last 10 seconds; older data expires automatically.\nPacket loss is estimated from sequence gaps; invalid packets include duplicates, late, corrupt, or undecodable packets.\nStatistics reset after reconnecting."));
        tips.SetToolTip(microphone, Localization.T("取消勾选会静音 WO Mic 虚拟麦克风，同时保持手机连接和本地监听。", "Uncheck to mute the WO Mic virtual microphone while keeping the phone connection and local monitoring active."));
        if (restoreItem != null) restoreItem.Text = Localization.T("打开主界面", "Open WO Mic Next");
        if (exitItem != null) exitItem.Text = Localization.T("退出", "Exit");
        Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), 10F);
        connect.Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), 14, FontStyle.Bold);
        status.Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), 16, FontStyle.Bold);
        stats.Font = new Font(Localization.T("Microsoft YaHei UI", "Segoe UI"), 9);
        RefreshStatisticsText();
        UpdateState(new(controller.State, Localization.T("界面语言已切换。", "Interface language changed.")));
        SaveSettings();
    }

    private static void ApplyLocalizedText(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is ComboBox combo && combo.Tag is LocalizedPair[] choices)
            {
                int selected = combo.SelectedIndex;
                combo.BeginUpdate(); combo.Items.Clear();
                combo.Items.AddRange(choices.Select(x => Localization.T(x.Chinese, x.English)).Cast<object>().ToArray());
                if (selected >= 0 && selected < combo.Items.Count) combo.SelectedIndex = selected;
                combo.EndUpdate();
            }
            else if (control.Tag is LocalizedPair pair)
                control.Text = Localization.T(pair.Chinese, pair.English);
            ApplyLocalizedText(control);
        }
    }

    private void UpdateTrayIcon()
    {
        var key = (State: controller.State, Microphone: microphone.Checked);
        if (!trayIcons.TryGetValue(key, out var icon))
        {
            icon = TrayIcons.Create(key.State, key.Microphone);
            trayIcons.Add(key, icon);
        }
        tray.Icon = icon;
        string stateText = controller.State switch
        {
            ConnectionState.Connected => Localization.T("已连接", "Connected"),
            ConnectionState.Connecting => Localization.T("正在连接", "Connecting"),
            ConnectionState.Reconnecting => Localization.T("正在重连", "Reconnecting"),
            ConnectionState.Stopping => Localization.T("正在断开", "Disconnecting"),
            _ => Localization.T("未连接", "Disconnected")
        };
        tray.Text = "WO Mic Next · " + stateText + (microphone.Checked ? "" : " · " + Localization.T("麦克风关闭", "Mic muted"));
    }

    private void RefreshStatisticsText()
    {
        if (controller.State == ConnectionState.Connected)
        {
            var recent = controller.Statistics.Snapshot();
            stats.Text = Localization.F("近10秒 · 收到 {0:N0} · 丢包 {1:N0}（{2:F1}%）\n异常包 {3:N0} · 48 kHz 单声道", "Last 10 sec · RX {0:N0} · lost {1:N0} ({2:F1}%)\nInvalid {3:N0} · 48 kHz mono", recent.Received, recent.Lost, recent.LossPercent, recent.Discarded);
        }
        else stats.Text = Localization.T("Wi‑Fi  ·  输出到 WO Mic Device", "Wi-Fi  ·  Output to WO Mic Device");
    }

    private sealed record LocalizedPair(string Chinese, string English);

    private void ToggleConnection()
    {
        if (controller.IsRunning) { controller.Stop(); return; }
        string address = ip.Text.Trim();
        if (!IPAddress.TryParse(address, out var parsed) || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) || parsed.IsIPv6Multicast)
        { message.Text = Localization.T("请输入有效的手机 IP 地址，例如 192.168.1.100。", "Enter a valid phone IP address, for example 192.168.1.100."); ip.Focus(); return; }
        SaveSettings();
        controller.Start(new(address, (int)controlPort.Value, (int)mediaPort.Value));
    }

    private void UpdateState(StatusUpdate update)
    {
        message.Text = update.Message;
        bool editable = update.State == ConnectionState.Disconnected;
        ip.Enabled = editable; controlPort.Enabled = editable; mediaPort.Enabled = editable;
        connect.Enabled = update.State != ConnectionState.Stopping;
        connect.Text = update.State switch
        { ConnectionState.Disconnected => Localization.T("连接", "Connect"), ConnectionState.Connecting => Localization.T("取消连接", "Cancel"), ConnectionState.Stopping => Localization.T("正在断开", "Disconnecting"), _ => Localization.T("断开", "Disconnect") };
        connect.BackColor = update.State == ConnectionState.Disconnected ? Blue : Color.FromArgb(63, 78, 103);
        status.Text = update.State switch
        { ConnectionState.Connected => "● " + Localization.T("已连接", "Connected"), ConnectionState.Connecting => "● " + Localization.T("正在连接", "Connecting"), ConnectionState.Reconnecting => "● " + Localization.T("正在重连", "Reconnecting"), ConnectionState.Stopping => "● " + Localization.T("正在断开", "Disconnecting"), _ => "● " + Localization.T("未连接", "Disconnected") };
        status.ForeColor = update.State switch
        { ConnectionState.Connected => Green, ConnectionState.Disconnected => Red, _ => Color.FromArgb(163, 106, 17) };
        UpdateTrayIcon();
    }

    private void UpdatePolicy()
    {
        if (action.SelectedIndex < 0 || alert.SelectedIndex < 0) return;
        var behavior = (DisconnectAction)action.SelectedIndex;
        retrySeconds.Enabled = behavior == DisconnectAction.Reconnect;
        alert.Enabled = behavior != DisconnectAction.DoNothing;
        controller.Policy = new(behavior, behavior == DisconnectAction.DoNothing ? AlertKind.None : (AlertKind)alert.SelectedIndex, (int)retrySeconds.Value);
    }
    private void SaveSettings()
    {
        if (loading) return;
        settings.PhoneIp = ip.Text.Trim(); settings.ControlPort = (int)controlPort.Value; settings.MediaPort = (int)mediaPort.Value;
        settings.LocalPlayback = playback.Checked; settings.MicrophoneEnabled = microphone.Checked;
        settings.Language = Localization.English ? "en-US" : "zh-CN";
        settings.ReconnectSeconds = (int)retrySeconds.Value;
        settings.DisconnectAction = (DisconnectAction)Math.Max(0, action.SelectedIndex);
        settings.AlertKind = (AlertKind)Math.Max(0, alert.SelectedIndex);
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        { message.Text = Localization.T("当前目录无法保存设置。设置在本次运行中仍然有效。", "Settings could not be saved here. They remain active for this session."); }
    }
    private void ShowAlert(AlertKind kind, string text)
    {
        if (kind is AlertKind.Sound or AlertKind.SoundAndWindows) disconnectSound.Play();
        if (kind is AlertKind.Windows or AlertKind.SoundAndWindows)
            tray.ShowBalloonTip(5000, Localization.T("WO Mic Next：麦克风已断线", "WO Mic Next: microphone disconnected"), text, ToolTipIcon.Warning);
    }
    private void OnUi(Action action)
    {
        if (IsDisposed || Disposing || closing || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private async void CloseAsync(object? sender, FormClosingEventArgs e)
    {
        if (closing) return;
        if (e.CloseReason == CloseReason.UserClosing && !exitRequested)
        {
            e.Cancel = true; SaveSettings(); Hide(); ShowInTaskbar = false;
            return;
        }
        // Windows shutdown/logoff must not be canceled by the close-to-tray behavior.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            closing = true; refresh.Stop(); controller.Stop(); SaveSettings(); DisposeTray();
            return;
        }
        e.Cancel = true; closing = true; refresh.Stop(); connect.Enabled = false;
        message.Text = Localization.T("正在关闭并释放音频连接…", "Closing and releasing the audio connection…");
        SaveSettings(); await controller.ShutdownAsync();
        DisposeTray();
        Close();
    }

    private void DisposeTray()
    {
        tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); refresh.Dispose(); tips.Dispose();
        disconnectSound.Dispose();
        foreach (var icon in ownedIcons) icon.Dispose();
        ownedIcons.Clear();
        foreach (var icon in trayIcons.Values) icon.Dispose();
        trayIcons.Clear();
    }

    internal void RestoreWindow()
    {
        ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate();
    }

    internal void RequestExit() { exitRequested = true; Close(); }

    protected override void SetVisibleCore(bool value)
    {
        if (startHiddenPending && value)
        {
            startHiddenPending = false; value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TrayWindow.ShowMessage) { RestoreWindow(); return; }
        base.WndProc(ref m);
    }
}

// Status text is rendered from the controller's message, which is localized when published.

internal sealed class AudioMeter : Control
{
    private double level;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Level { get => level; set { level = value; Invalidate(); } }
    public AudioMeter() { DoubleBuffered = true; AccessibleName = "输入音量"; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.FromArgb(220, 229, 239));
        // Log display makes normal speaking levels visible without changing the audio.
        double visible = level <= 0.0001 ? 0 : Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1);
        using var brush = new SolidBrush(level > 0.9 ? Color.FromArgb(188, 45, 59) : Color.FromArgb(23, 132, 88));
        e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * visible), Height);
    }
}
