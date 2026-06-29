using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RyuoBrightnessFix.Util;

/// <summary>
/// A WinExe app has no console. When invoked with CLI args from a terminal we attach to
/// the parent console so Serilog's console output is visible; if there's no parent
/// console (e.g. launched by Task Scheduler) we run headless (file log still works).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ConsoleHelper
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static void AttachToParentConsole()
    {
        try
        {
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // No console available — headless run; ignore.
        }
    }
}
