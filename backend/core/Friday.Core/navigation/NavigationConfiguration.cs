using System.Text.Json;
using System.Text.RegularExpressions;

namespace Friday.Core.Navigation;

public sealed class NavigationConfiguration
{
    public int Version { get; set; } = 1;
    public float RecognitionConfidence { get; set; } = 0.55f;
    public List<NavigationCommandRule> Commands { get; set; } = [];
    public List<NavigationSystemTarget> SystemTargets { get; set; } = [];
    public Dictionary<string, string> TargetAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Confirmations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> BrowserProcesses { get; set; } = [];
    public Dictionary<string, string[]> Shortcuts { get; set; } = [];
    public List<string> IgnoredTargetWords { get; set; } = [];

    public static NavigationConfiguration Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("FRIDAY_NAVIGATION_CONFIG");
        if (string.IsNullOrWhiteSpace(path))
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                DirectoryInfo? folder = new(start);
                while (folder is not null)
                {
                    string candidate = Path.Combine(folder.FullName, "config", "navigation.json");
                    if (File.Exists(candidate)) { path = candidate; break; }
                    folder = folder.Parent;
                }
                if (path is not null) break;
            }
        }
        if (string.IsNullOrWhiteSpace(path))
            throw new FileNotFoundException("config/navigation.json is missing. Copy the complete local-navigation configuration.");
        var config = JsonSerializer.Deserialize<NavigationConfiguration>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip
        }) ?? throw new InvalidDataException("Navigation configuration is empty.");
        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (Version != 1 || Commands.Count == 0 || Commands.Count > 100)
            throw new InvalidDataException("Unsupported navigation config version or command count.");
        if (RecognitionConfidence is < 0.3f or > 0.95f)
            throw new InvalidDataException("Recognition confidence must be between 0.30 and 0.95.");
        foreach (var command in Commands)
        {
            if (!Enum.TryParse<NavigationAction>(command.Action, true, out _) || command.Phrases.Count == 0)
                throw new InvalidDataException("Unknown navigation action: " + command.Action);
            foreach (var phrase in command.Phrases)
            {
                if (phrase.Length is < 1 or > 100 || phrase.Any(char.IsControl)
                    || phrase.Split("{target}").Length > 2
                    || phrase.Replace("{target}", "").IndexOfAny(['{', '}']) >= 0)
                    throw new InvalidDataException("Invalid command phrase: " + phrase);
            }
        }
        if (SystemTargets.Select(t => t.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != SystemTargets.Count)
            throw new InvalidDataException("System destination keys must be unique.");
        foreach (var target in SystemTargets)
            if (!Regex.IsMatch(target.Key, @"^[a-z0-9-]+$") || string.IsNullOrWhiteSpace(target.Name)
                || !Regex.IsMatch(target.Uri, @"^ms-settings:[a-z0-9-]*$"))
                throw new InvalidDataException("System destinations must use a supported ms-settings URI, never a shell command.");
        if (TargetAliases.Count > 500) throw new InvalidDataException("Too many target aliases.");
        foreach (var alias in TargetAliases)
            if (string.IsNullOrWhiteSpace(alias.Key) || string.IsNullOrWhiteSpace(alias.Value))
                throw new InvalidDataException("Target aliases cannot be empty.");
        foreach (var keys in Shortcuts.Values)
            foreach (string key in keys) _ = VirtualKey(key);
    }

    public string ResolveAlias(string name) => TargetAliases.FirstOrDefault(p =>
        NavigationParser.Normalize(p.Key) == NavigationParser.Normalize(name)).Value ?? name;

    public string Reply(string key, string? address, string? target = null)
    {
        string template = Confirmations.GetValueOrDefault(key) ?? key + "{address}.";
        return template.Replace("{address}", string.IsNullOrWhiteSpace(address) ? "" : " " + address)
            .Replace("{target}", target ?? "");
    }

    public ushort[] Keys(NavigationAction action) => Shortcuts.TryGetValue(action.ToString(), out var keys)
        ? keys.Select(VirtualKey).ToArray() : [];

    private static ushort VirtualKey(string name) => name.ToUpperInvariant() switch
    {
        "CTRL" => 0x11, "SHIFT" => 0x10, "ALT" => 0x12, "TAB" => 0x09,
        "LEFT" => 0x25, "RIGHT" => 0x27, "UP" => 0x26, "DOWN" => 0x28,
        "PAGEUP" => 0x21, "PAGEDOWN" => 0x22, "HOME" => 0x24, "END" => 0x23,
        _ => throw new InvalidDataException("Unsupported keyboard key: " + name)
    };
}

public sealed class NavigationCommandRule
{
    public string Action { get; set; } = "";
    public List<string> Phrases { get; set; } = [];
}

public sealed class NavigationSystemTarget
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Uri { get; set; } = "";
    public string[] Aliases { get; set; } = [];
}
