namespace WOMicNext;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--enable-startup"))
        {
            StartupRegistration.SetEnabled(true);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-status.txt"), StartupRegistration.Command);
            return;
        }
        if (args.Contains("--ui-self-test"))
        {
            ApplicationConfiguration.Initialize();
            try { UiSelfTests.Run(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-test-results.txt"), ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--self-test"))
        {
            try { SelfTests.RunAsync(args.Contains("--driver-test")).GetAwaiter().GetResult(); Environment.ExitCode = 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), ex + "\n" + string.Join("\n", SelfTests.Trace)); Environment.ExitCode = 1; }
            return;
        }
        using var mutex = new Mutex(true, "Local\\WOMicNext.Singleton", out bool first);
        if (!first)
        {
            if (!args.Contains("--start-hidden")) TrayWindow.RestoreExisting();
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(Settings.Load(), args.Contains("--start-hidden")));
    }
}
