using System.Text.RegularExpressions;

namespace Friday.Core.Navigation;

public static class NavigationParser
{
    public static string Normalize(string value) => Regex.Replace(
        value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    public static NavigationRequest? Parse(string message) => Parse(message, NavigationConfiguration.Load());

    public static NavigationRequest? Parse(string message, NavigationConfiguration config)
    {
        string text = Regex.Replace(message.Trim().TrimEnd('.', '!', '?'), @"\s+", " ");
        if (text.Length is < 1 or > 180 || text.IndexOfAny(['"', '\'', '`', ';', '|', '&', '$', '\\', '/', ':']) >= 0)
            return null;
        // Specific literals such as "close this" take precedence over "close {target}".
        foreach (var item in config.Commands.SelectMany(rule => rule.Phrases.Select(phrase => new { rule, phrase }))
            .OrderBy(p => p.phrase.Contains("{target}", StringComparison.Ordinal)))
        {
            var rule = item.rule;
            string phrase = item.phrase;
            string pattern = Regex.Escape(Regex.Replace(phrase.Trim(), @"\s+", " "))
                .Replace(Regex.Escape("{target}"), "(?<target>.+)");
            var match = Regex.Match(text, "^" + pattern + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            if (!match.Success) continue;
            string target = match.Groups["target"].Value.Trim();
            return new(Enum.Parse<NavigationAction>(rule.Action, true), config.ResolveAlias(target));
        }
        return null;
    }

    public static List<NavigationTarget> Match(IEnumerable<NavigationTarget> targets, string query)
    {
        string key = Normalize(query);
        if (key.Length == 0) return [];
        var all = targets.ToArray();
        var exact = all.Where(t => t.Aliases.Append(t.Name)
            .Any(n => Normalize(n).Replace(" ", "") == key.Replace(" ", ""))).ToList();
        if (exact.Count > 0) return exact;
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return all.Where(t => t.Aliases.Append(t.Name).Any(n =>
            words.All(w => Normalize(n).Split(' ').Contains(w, StringComparer.Ordinal)))).ToList();
    }

    public static string[] SpokenTargets(IEnumerable<NavigationTarget> targets, NavigationConfiguration config)
    {
        return targets.SelectMany(t => t.Aliases.Append(t.Name))
            .Concat(config.TargetAliases.Keys)
            .Select(Normalize)
            .Where(n => n.Length is > 0 and <= 100 && Regex.IsMatch(n, @"^[a-z0-9 ]+$"))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).Take(1500).ToArray();
    }
}
