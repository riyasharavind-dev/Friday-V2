using Friday.Core.Navigation;

namespace Friday.Core.Interaction;

public enum InputKind { Ignore, Wake, Interrupt, Confirm, Question }
public sealed record RoutedInput(InputKind Kind, string Text, bool Addressed);

public sealed class InputRouter(InteractionOptions options)
{
    public RoutedInput Route(string text, bool typed, bool conversation, bool busy)
    {
        string normalized = NavigationParser.Normalize(text);
        if (normalized.Length == 0) return new(InputKind.Ignore, "", false);
        if (options.InterruptPhrases.Any(p => NavigationParser.Normalize(p) == normalized))
            return new(InputKind.Interrupt, "", true);
        bool addressed = TryStripAddress(text, out string request);
        string key = NavigationParser.Normalize(request);
        if (addressed && options.InterruptRequests.Any(p => NavigationParser.Normalize(p) == key))
            return new(InputKind.Interrupt, "", true);
        if (addressed && options.ConfirmRequests.Any(p => NavigationParser.Normalize(p) == key))
            return new(InputKind.Confirm, "", true);
        if (addressed && key.Length == 0) return new(InputKind.Wake, "", true);
        // Bare stop/wait/cancel is allowed only as a barge-in during an active
        // turn. While idle it is ignored to avoid accidental interruptions.
        if (options.InterruptRequests.Any(p => NavigationParser.Normalize(p) == key))
            return busy ? new(InputKind.Interrupt, "", addressed) : new(InputKind.Ignore, "", addressed);
        if (busy && !typed && !addressed) return new(InputKind.Ignore, "", false);
        // Every command, including desktop navigation, is interpreted by the
        // AI agent. Only interruption and confirmation stay local.
        return new(typed || addressed || conversation ? InputKind.Question : InputKind.Ignore, request, addressed);
    }

    public bool TryStripAddress(string text, out string request)
    {
        // Keep the original request spelling for chat/code. Match prefix at word boundaries.
        foreach (string prefix in options.WakePhrases.OrderByDescending(p => p.Length))
        {
            string pattern = @"^\s*" + System.Text.RegularExpressions.Regex.Escape(prefix).Replace(@"\ ", @"\s+")
                + @"(?=$|[\s,!.?:;\-])[\s,!.?:;\-]*(?<request>.*)$";
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success) { request = match.Groups["request"].Value.Trim(); return true; }
        }
        request = text.Trim(); return false;
    }
}
