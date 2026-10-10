using System.Globalization;

namespace Sidetrack.Cli;

internal sealed class UsageException(string message) : Exception(message);

/// <summary>Minimal argv parser: a command, positionals, <c>--name value</c> options and bare flags.</summary>
internal sealed class Args
{
    private static readonly HashSet<string> FlagNames = ["--keep-temp"];
    private readonly List<(string Name, string? Value)> _options = [];

    public string? Command { get; private init; }
    public List<string> Positional { get; } = [];

    public static Args Parse(string[] argv)
    {
        var args = new Args { Command = argv.Length > 0 ? argv[0].ToLowerInvariant() : null };
        for (int i = 1; i < argv.Length; i++)
        {
            var a = argv[i];
            if (!a.StartsWith("--", StringComparison.Ordinal)) args.Positional.Add(a);
            else if (FlagNames.Contains(a)) args._options.Add((a, null));
            else if (a.IndexOf('=') is var eq and > 2) args._options.Add((a[..eq], a[(eq + 1)..]));
            else if (i + 1 < argv.Length) args._options.Add((a, argv[++i]));
            else throw new UsageException($"{a} needs a value.");
        }
        return args;
    }

    public bool Flag(string name) => _options.Any(o => o.Name == name);

    public string? Value(string name) => _options.LastOrDefault(o => o.Name == name).Value;

    public List<string> Values(string name) => _options.Where(o => o.Name == name && o.Value is not null).Select(o => o.Value!).ToList();

    public int Int(string name, int fallback) =>
        Value(name) is { } v
            ? int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : throw new UsageException($"{name} must be a positive whole number.")
            : fallback;
}
