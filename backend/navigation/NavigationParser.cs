using System.Text.RegularExpressions;

namespace Friday.Core.Navigation;

public static class NavigationParser
{
    public static string Normalize(string value) => Regex.Replace(
        value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    public static NavigationRequest? Parse(string message)
    {
        string text = message.Trim().TrimEnd('.', '!', '?').Trim();
        // Do not turn explanations, quoted commands or multi-step sentences into actions.
        if (text.Length > 180 || text.IndexOfAny(['"', '\'', '`', '\n', '\r', ';', '|', '&', '$', '\\', '/', ':']) >= 0)
            return null;
        text = Regex.Replace(text, @"^(?:hey\s+friday[, ]*|friday[, ]*)", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"^(?:(?:can|could|would) you\s+)?(?:please\s+)?", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+please$", "", RegexOptions.IgnoreCase);
        if (Regex.IsMatch(text, @"\b(?:and|then|because|instead)\b", RegexOptions.IgnoreCase)) return null;
        string normalized = Normalize(text);
        NavigationAction? direct = normalized switch
        {
            "next tab" or "switch to next tab" => NavigationAction.NextTab,
            "previous tab" or "last tab" or "switch to previous tab" => NavigationAction.PreviousTab,
            "go back" or "back pannu" => NavigationAction.Back,
            "go forward" or "forward pannu" => NavigationAction.Forward,
            "next page" => NavigationAction.NextPage,
            "minimize" or "minimise" or "minimize this" or "minimize this window" => NavigationAction.Minimize,
            "maximize" or "maximise" or "maximize this" or "maximize this window" => NavigationAction.Maximize,
            "restore this" or "restore this window" => NavigationAction.Restore,
            "close this" or "close this window" => NavigationAction.Close,
            _ => null
        };
        if (direct is not null) return new(direct.Value, "");
        var prefix = Regex.Match(text, @"^(open|launch|start|close)\s+(.+)$", RegexOptions.IgnoreCase);
        var suffix = Regex.Match(text, @"^(.+?)\s+(open|launch|start|close)\s+(?:pannu|pannunga)$", RegexOptions.IgnoreCase);
        string verb;
        string target;
        if (prefix.Success) { verb = prefix.Groups[1].Value; target = prefix.Groups[2].Value; }
        else if (suffix.Success) { verb = suffix.Groups[2].Value; target = suffix.Groups[1].Value; }
        else return null;
        target = Regex.Replace(target, @"\s+(?:pannu|pannunga)$", "", RegexOptions.IgnoreCase).Trim();
        if (Normalize(target).Length == 0) return null;
        return new(verb.Equals("close", StringComparison.OrdinalIgnoreCase)
            ? NavigationAction.Close : NavigationAction.Open, target);
    }

    public static List<NavigationTarget> Match(IEnumerable<NavigationTarget> targets, string query)
    {
        string key = Normalize(query);
        var all = targets.ToArray();
        var exact = all.Where(t => t.Aliases.Append(t.Name).Any(n => Normalize(n) == key)).ToList();
        if (exact.Count > 0) return exact;
        var words = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];
        return all.Where(t => t.Aliases.Append(t.Name).Any(n =>
            words.All(w => Normalize(n).Split(' ').Contains(w, StringComparer.Ordinal)))).ToList();
    }
}
