using System.Text.Json;

namespace WOMicNext;

internal sealed class Settings
{
    public string PhoneIp { get; set; } = "192.168.1.100";
    public int ControlPort { get; set; } = 8125;
    public int MediaPort { get; set; } = 49152;
    public int ReconnectSeconds { get; set; } = 12;
    public DisconnectAction DisconnectAction { get; set; } = DisconnectAction.Reconnect;
    public AlertKind AlertKind { get; set; } = AlertKind.None;
    public bool LocalPlayback { get; set; }
    public bool MicrophoneEnabled { get; set; } = true;
    public string Language { get; set; } = "zh-CN";
    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
            settings.ControlPort = Math.Clamp(settings.ControlPort, 1, 65535);
            settings.MediaPort = Math.Clamp(settings.MediaPort, 1, 65535);
            settings.ReconnectSeconds = Math.Clamp(settings.ReconnectSeconds, 5, 60);
            if (!Enum.IsDefined(settings.DisconnectAction)) settings.DisconnectAction = DisconnectAction.Reconnect;
            if (!Enum.IsDefined(settings.AlertKind)) settings.AlertKind = AlertKind.None;
            if (settings.Language is not ("zh-CN" or "en-US")) settings.Language = "zh-CN";
            return settings;
        }
        catch { return new(); }
    }

    public void Save()
    {
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}

internal enum DisconnectAction { Reconnect, NotifyOnly, DoNothing }
internal enum AlertKind { None, Sound, Windows, SoundAndWindows }
internal enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting, Stopping }
internal sealed record ConnectionOptions(string Ip, int ControlPort, int MediaPort);
internal sealed record DisconnectPolicy(DisconnectAction Action, AlertKind Alert, int BudgetSeconds);
internal sealed record StatusUpdate(ConnectionState State, string Message);
