using System.Globalization;

namespace RyuoBrightnessFix;

/// <summary>
/// Minimal, dependency-free command-line parser.
///   arg[0]                 => Command
///   --key value            => option with value
///   --flag                 => boolean flag (no following value, or followed by another --option)
///   first non-option token => Positional (after the command)
/// </summary>
public sealed class ArgParser
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; }
    public string? Positional { get; }

    public ArgParser(string[] args)
    {
        if (args.Length == 0) return;

        int i = 0;
        if (!args[0].StartsWith('-'))
            Command = args[i++];

        string? positional = null;

        for (; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var key = token[2..];
                // Support --key=value as well as --key value.
                int eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    _options[key[..eq]] = key[(eq + 1)..];
                    continue;
                }

                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                if (hasValue)
                    _options[key] = args[++i];
                else
                    _flags.Add(key);
            }
            else
            {
                positional ??= token;
            }
        }

        Positional = positional;
    }

    public string? Get(string key) => _options.TryGetValue(key, out var v) ? v : null;

    public bool HasFlag(string key) => _flags.Contains(key) || _options.ContainsKey(key);

    public int? GetInt(string key)
    {
        var v = Get(key);
        if (v is null) return null;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
