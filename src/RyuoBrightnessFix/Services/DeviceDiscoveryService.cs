using HidSharp;
using HidSharp.Reports;
using RyuoBrightnessFix.Models;
using Serilog;
using DeviceFilter = RyuoBrightnessFix.Models.DeviceFilter;

namespace RyuoBrightnessFix.Services;

/// <summary>
/// Enumerates local HID devices via HidSharp and flattens them into
/// <see cref="DeviceInfo"/> records, with ASUS/ROG heuristics and filtering.
/// </summary>
public sealed class DeviceDiscoveryService
{
    public const int AsusVendorId = 0x0B05;

    private static readonly string[] AsusNameHints =
        { "asus", "rog", "ryuo", "aura", "armoury", "republic of gamers" };

    private readonly ILogger _log;

    public DeviceDiscoveryService(ILogger log) => _log = log.ForContext<DeviceDiscoveryService>();

    /// <summary>Enumerate every HID device, returning one DeviceInfo per top-level usage.</summary>
    public IReadOnlyList<(DeviceInfo Info, HidDevice Device)> Enumerate()
    {
        var results = new List<(DeviceInfo, HidDevice)>();

        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices().ToList();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to enumerate HID devices.");
            throw;
        }

        foreach (var device in devices)
        {
            foreach (var info in Describe(device))
                results.Add((info, device));
        }

        return results;
    }

    /// <summary>
    /// Build DeviceInfo records for a single HidDevice. A device may expose several
    /// top-level usages; we emit one record each. If the report descriptor can't be
    /// read we still emit a single record with null usage page/usage.
    /// </summary>
    private IEnumerable<DeviceInfo> Describe(HidDevice device)
    {
        int vid = device.VendorID;
        int pid = device.ProductID;
        string? manufacturer = Safe(() => device.GetManufacturer());
        string? product = Safe(() => device.GetProductName());
        string? serial = Safe(() => device.GetSerialNumber());
        string? path = device.DevicePath;

        int maxIn = SafeInt(() => device.GetMaxInputReportLength());
        int maxOut = SafeInt(() => device.GetMaxOutputReportLength());
        int maxFeat = SafeInt(() => device.GetMaxFeatureReportLength());

        bool likelyAsus = vid == AsusVendorId || NameLooksAsus(manufacturer) || NameLooksAsus(product);

        var usages = new List<(int page, int usage)>();
        try
        {
            var descriptor = device.GetReportDescriptor();
            foreach (var item in descriptor.DeviceItems)
            {
                foreach (uint u in item.Usages.GetAllValues())
                {
                    int page = (int)(u >> 16);
                    int usage = (int)(u & 0xFFFF);
                    if (!usages.Contains((page, usage)))
                        usages.Add((page, usage));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not read report descriptor for {Path}", path);
        }

        DeviceInfo Make(int? page, int? usage) => new()
        {
            VendorId = vid,
            ProductId = pid,
            UsagePage = page,
            Usage = usage,
            DevicePath = path,
            Manufacturer = manufacturer,
            ProductName = product,
            SerialNumber = serial,
            MaxInputReportLength = maxIn,
            MaxOutputReportLength = maxOut,
            MaxFeatureReportLength = maxFeat,
            LikelyAsus = likelyAsus,
        };

        if (usages.Count == 0)
        {
            yield return Make(null, null);
        }
        else
        {
            foreach (var (page, usage) in usages)
                yield return Make(page, usage);
        }
    }

    /// <summary>
    /// Apply a config filter and return the matching distinct devices (one entry per
    /// physical HidDevice, even if it has multiple usages).
    /// </summary>
    public IReadOnlyList<(DeviceInfo Info, HidDevice Device)> Match(DeviceFilter filter)
    {
        var all = Enumerate();
        var matched = all.Where(x => Matches(x.Info, filter)).ToList();

        // Collapse to distinct physical devices by path, preferring the most informative usage.
        var byPath = matched
            .GroupBy(x => x.Device.DevicePath)
            .Select(g => g.First())
            .ToList();

        return byPath;
    }

    private static bool Matches(DeviceInfo info, DeviceFilter filter)
    {
        if (filter.VendorIdValue is int vid && info.VendorId != vid) return false;
        if (filter.ProductIdValue is int pid && info.ProductId != pid) return false;
        if (filter.UsagePage is int up && info.UsagePage != up) return false;
        if (filter.Usage is int us && info.Usage != us) return false;

        if (!string.IsNullOrWhiteSpace(filter.PathContains) &&
            (info.DevicePath is null ||
             !info.DevicePath.Contains(filter.PathContains, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!string.IsNullOrWhiteSpace(filter.SerialNumber) &&
            !string.Equals(info.SerialNumber, filter.SerialNumber, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>Resolve a config filter to exactly one device, or throw a clear error.</summary>
    public (DeviceInfo Info, HidDevice Device) ResolveSingle(DeviceFilter filter)
    {
        var matches = Match(filter);
        if (matches.Count == 0)
            throw new InvalidOperationException(
                "No HID device matched the configured filter. Run 'list-devices' and refine device {} in your config.");
        if (matches.Count > 1)
        {
            var summary = string.Join(Environment.NewLine,
                matches.Select(m => "  - " + m.Info.DevicePath));
            throw new InvalidOperationException(
                $"Filter matched {matches.Count} devices; tighten it (add productId / pathContains / usagePage):{Environment.NewLine}{summary}");
        }
        return matches[0];
    }

    private static bool NameLooksAsus(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var lower = name.ToLowerInvariant();
        return AsusNameHints.Any(h => lower.Contains(h));
    }

    private static string? Safe(Func<string?> f)
    {
        try { return f(); } catch { return null; }
    }

    private static int SafeInt(Func<int> f)
    {
        try { return f(); } catch { return 0; }
    }
}
