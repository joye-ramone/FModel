using System.Globalization;

namespace FModel.CLI;

/// <summary>
/// command line: FModel.CLI &lt;command&gt; [patterns...] [--option value] [--flag]
/// </summary>
public sealed class CliOptions
{
    private static readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "verbose", "all", "exports", "no-materials", "no-morphs", "all-mips", "hdr-as-png", "regex", "strict", "raw-sound", "stdout"
    };

    private static readonly HashSet<string> _values = new(StringComparer.OrdinalIgnoreCase)
    {
        "game", "ue", "platform", "aes", "aes-file", "usmap", "out", "data", "log", "threads", "kind",
        "mesh-format", "nanite", "lod", "sockets", "materials", "texture-format", "texture-quality", "compression", "tsv"
    };

    public string Command { get; private set; } = string.Empty;
    public List<string> Patterns { get; } = [];
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "/?")
            {
                o.Add("help", "true");
                continue;
            }
            if (a == "-v")
            {
                o.Add("verbose", "true");
                continue;
            }

            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var name = a[2..];
                string? inline = null;
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    inline = name[(eq + 1)..];
                    name = name[..eq];
                }

                if (_flags.Contains(name))
                {
                    o.Add(name, inline ?? "true");
                }
                else if (_values.Contains(name))
                {
                    if (inline == null)
                    {
                        if (i + 1 >= args.Length) throw new CliException($"option --{name} needs a value");
                        inline = args[++i];
                    }
                    o.Add(name, inline);
                }
                else
                {
                    throw new CliException($"unknown option --{name}");
                }
                continue;
            }

            if (string.IsNullOrEmpty(o.Command)) o.Command = a.ToLowerInvariant();
            else o.Patterns.Add(a);
        }

        return o;
    }

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var list)) _options[name] = list = [];
        list.Add(value);
    }

    public bool Has(string name) => _options.TryGetValue(name, out var l) && l.Count > 0 && !l[^1].Equals("false", StringComparison.OrdinalIgnoreCase);
    public string? Get(string name) => _options.TryGetValue(name, out var l) && l.Count > 0 ? l[^1] : null;
    public IReadOnlyList<string> GetAll(string name) => _options.TryGetValue(name, out var l) ? l : [];

    public string Require(string name) => Get(name) ?? throw new CliException($"option --{name} is required");

    public int GetInt(string name, int fallback)
    {
        var v = Get(name);
        if (v == null) return fallback;
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : throw new CliException($"--{name}: '{v}' is not a number");
    }

    /// <summary>
    /// enum value by its name or by a short alias, case insensitive
    /// </summary>
    public T GetEnum<T>(string name, T fallback, IReadOnlyDictionary<string, T>? aliases = null) where T : struct, Enum
    {
        var v = Get(name);
        if (v == null) return fallback;
        if (aliases != null && aliases.TryGetValue(v, out var a)) return a;
        if (Enum.TryParse<T>(v, true, out var r)) return r;

        var names = (aliases?.Keys ?? []).Concat(Enum.GetNames<T>()).ToList();
        if (names.Count > 20) // EGame: only the names that look like the given value
        {
            var key = v.Replace("GAME_", "", StringComparison.OrdinalIgnoreCase).Replace("_", "");
            names = names.Where(x => x.Replace("_", "").Contains(key, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
            throw new CliException($"--{name}: unknown value '{v}'" + (names.Count > 0 ? $" (similar: {string.Join(", ", names)})" : ""));
        }
        throw new CliException($"--{name}: unknown value '{v}' (known: {string.Join(", ", names)})");
    }
}

public sealed class CliException(string message, int exitCode = ExitCodes.Usage) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int Mount = 2;
    public const int ExportFailed = 3;
    public const int NoMatch = 4;
}
