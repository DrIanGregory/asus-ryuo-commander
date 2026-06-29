namespace RyuoBrightnessFix.Models;

/// <summary>The set of verbs the CLI understands. No command strings live anywhere else.</summary>
public enum CliCommand
{
    Help,
    ListDevices,
    TestRead,
    SendCommand,
    SendSequence,
    SetBrightness100,
    MonitorResume,
    InstallTask,
    UninstallTask,
    ParseHex,
}

/// <summary>Single source of truth mapping kebab-case command tokens to <see cref="CliCommand"/>.</summary>
public static class CliCommands
{
    private static readonly IReadOnlyDictionary<string, CliCommand> Map =
        new Dictionary<string, CliCommand>(StringComparer.OrdinalIgnoreCase)
        {
            ["list-devices"] = CliCommand.ListDevices,
            ["test-read"] = CliCommand.TestRead,
            ["send-command"] = CliCommand.SendCommand,
            ["send-sequence"] = CliCommand.SendSequence,
            ["set-brightness-100"] = CliCommand.SetBrightness100,
            ["monitor-resume"] = CliCommand.MonitorResume,
            ["install-task"] = CliCommand.InstallTask,
            ["uninstall-task"] = CliCommand.UninstallTask,
            ["parse-hex"] = CliCommand.ParseHex,
            ["help"] = CliCommand.Help,
            ["--help"] = CliCommand.Help,
            ["-h"] = CliCommand.Help,
        };

    public static CliCommand? TryParse(string? token)
        => token is not null && Map.TryGetValue(token, out var cmd) ? cmd : null;

    /// <summary>True when the first argument is any recognized verb — i.e. the process should run headless/CLI.</summary>
    public static bool IsCliInvocation(string[] args)
        => args.Length > 0 && TryParse(args[0]) is not null;
}
