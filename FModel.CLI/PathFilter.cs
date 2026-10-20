using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider.Objects;

namespace FModel.CLI;

/// <summary>
/// selects game files by virtual path: exact path (with or without extension), folder ("Game/Content/Weapons/"),
/// glob ("**" any depth, "*" inside one folder, "?" one character) or --regex; always case insensitive
/// </summary>
public sealed class PathFilter
{
    private readonly List<Regex> _regexes = [];
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _regexes.Count == 0 && _exact.Count == 0;

    public PathFilter(IEnumerable<string> patterns, bool regex)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Replace('\\', '/').TrimStart('/');
            if (regex)
            {
                _regexes.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }
            else if (p.EndsWith('/'))
            {
                _regexes.Add(Glob(p + "**"));
            }
            else if (p.IndexOfAny(['*', '?']) >= 0)
            {
                _regexes.Add(Glob(p));
            }
            else
            {
                _exact.Add(p);
                _regexes.Add(Glob(p + "/**")); // a folder given without the trailing slash
            }
        }
    }

    public bool Match(GameFile file)
    {
        if (_exact.Count > 0 && (_exact.Contains(file.Path) || _exact.Contains(file.PathWithoutExtension))) return true;
        foreach (var r in _regexes)
            if (r.IsMatch(file.Path)) return true;
        return false;
    }

    private static Regex Glob(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    sb.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') i++; // "**/" also matches no folder
                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
