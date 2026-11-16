namespace NothingElseCompares2u.Cli;

/// <summary>--name value / --flag / -n 6 options plus positional sources.</summary>
internal sealed class CliArgs
{
    private static readonly HashSet<string> Flags =
    [
        "titles", "verbose", "auto-sync", "curve", "full", "random", "chapters", "no-aspect", "deinterlace", "no-native",
        "all-playlists", "no-sync", "no-video", "no-audio", "no-crop", "whole", "edits", "smart", "hw", "no-cache", "slowpics", "public",
    ];

    private readonly Dictionary<string, List<string>> _opts = new(StringComparer.Ordinal);
    public List<string> Positional { get; } = [];

    public static CliArgs Parse(string[] args)
    {
        var c = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal) || (a.StartsWith('-') && a.Length == 2 && char.IsLetter(a[1])))
            {
                var name = a.TrimStart('-');
                string value;
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    value = name[(eq + 1)..];
                    name = name[..eq];
                }
                else if (Flags.Contains(name) || (name == "metrics" && (i + 1 >= args.Length || args[i + 1].StartsWith('-') || LooksLikePath(args[i + 1]))))
                {
                    value = "true";
                }
                else
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException($"--{name} needs a value.");
                    value = args[++i];
                }
                if (!c._opts.TryGetValue(name, out var list))
                    c._opts[name] = list = [];
                list.Add(value);
            }
            else
            {
                c.Positional.Add(a);
            }
        }
        return c;
    }

    private static bool LooksLikePath(string s) => s.Contains('/') || s.Contains('\\') || File.Exists(s) || Directory.Exists(s);

    public bool Has(string name) => _opts.ContainsKey(name);

    public string? Get(string name) => _opts.TryGetValue(name, out var l) ? l[^1] : null;

    public IEnumerable<string> All(string name) => _opts.TryGetValue(name, out var l) ? l : [];
}
