using System.Runtime.Versioning;
using System.Security.Principal;

namespace RyuoBrightnessFix.Util;

[SupportedOSPlatform("windows")]
public static class AdminUtil
{
    /// <summary>True if the current process is running elevated (member of the Administrators role).</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
