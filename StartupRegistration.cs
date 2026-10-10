using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace WOMicNext;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WOMicNext";
    public static bool IsEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) is string; }
    }

    public static string Command => $"\"{Path.Combine(AppContext.BaseDirectory, "WOMicNext.exe")}\" --start-hidden";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            if (Command.Length > 260) throw new IOException("程序所在路径过长，无法设置开机启动。请将程序移动到较短路径后重试。");
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, false);
    }
}

internal static class TrayWindow
{
    public static readonly int ShowMessage = (int)RegisterWindowMessage("WOMicNext.ShowMainWindow");
    public static void RestoreExisting()
    {
        IntPtr window = FindWindow(null, "WO Mic Next");
        if (window != IntPtr.Zero) PostMessage(window, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
