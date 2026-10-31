namespace KRVSpeed;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "KRVSpeed-single-instance", out bool first);
        if (!first) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.Run(new SpeedOverlay());
    }
}
