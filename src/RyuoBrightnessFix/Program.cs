using RyuoBrightnessFix.Services;

namespace RyuoBrightnessFix;

/// <summary>
/// Process entry point. Two modes selected by argument:
/// <list type="bullet">
/// <item><c>--supervise</c>: run the <see cref="CrashSupervisor"/> — launch the GUI as a child and
/// restart it with exponential backoff if it crashes. This is how the autostart registrations
/// (Task Scheduler / Run key) launch the app so an uncatchable native crash self-heals.</item>
/// <item>no arguments: run the WPF tray app itself.</item>
/// </list>
/// Set as the assembly entry point via <c>&lt;StartupObject&gt;</c> in the csproj, overriding the
/// Main WPF would otherwise generate from App.xaml.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains(CrashSupervisor.Switch, StringComparer.OrdinalIgnoreCase))
            return CrashSupervisor.Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
