namespace CrashBandicoot.ModTools;

sealed class CliArgs
{
    public string Command { get; init; } = "";
    public Dictionary<string, string> Flags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; init; } = [];

    public static CliArgs Parse(string[] args)
    {
        if (args.Length == 0)
            return new CliArgs();

        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionals = new List<string>();
        string command = args[0].Trim().ToLowerInvariant();

        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                string value = "true";
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                flags[key] = value;
            }
            else
            {
                positionals.Add(a);
            }
        }

        return new CliArgs
        {
            Command = command,
            Flags = flags,
            Positionals = positionals,
        };
    }

    public string? Get(string name) =>
        Flags.TryGetValue(name, out var v) ? v : null;

    public string Require(string name)
    {
        var v = Get(name);
        if (string.IsNullOrWhiteSpace(v) || v == "true")
            throw new ArgumentException($"Missing required --{name}");
        return v;
    }

    public bool Has(string name) => Flags.ContainsKey(name);
}
