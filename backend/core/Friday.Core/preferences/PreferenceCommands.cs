using System;
using System.Text.RegularExpressions;

namespace Friday.Core.Preferences;

public sealed record PreferenceCommand(string Action, string? Value = null);

public static class PreferenceCommands
{
    public static PreferenceCommand? Parse(string input)
    {
        string text = input.Trim().TrimEnd('.', '!', '?').Trim();

        // Temporary instructions remain in the ordinary conversation context.
        if (Regex.IsMatch(text,
            @"\b(this (answer|reply|task|time)|just once|only once|for now)\b",
            RegexOptions.IgnoreCase))
            return null;

        string normalized = Regex.Replace(
            text.ToLowerInvariant(), @"\s+", " ");

        if (normalized is "what are my preferences" or
            "show my preferences" or "list my preferences" or
            "what preferences have you saved" or "/preferences")
            return new("show");

        if (normalized is "reset my preferences" or
            "reset all my preferences" or "reset all preferences")
            return new("reset");

        if (normalized is "forget my preferred name" or
            "forget my preferred address" or "forget my nickname" or
            "don't call me that" or "do not call me that")
            return new("forget_address");

        Match address = Regex.Match(text,
            @"^(?:please\s+)?(?:from now on[,\s]+)?"
            + @"(?:call me|address me as|refer to me as)\s+"
            + @"(?<value>.+?)(?:\s+from now on|\s+instead|\s+please)?$",
            RegexOptions.IgnoreCase);

        if (!address.Success)
        {
            address = Regex.Match(text,
                @"^(?:enna|ennai)\s+(?<value>.+?)\s+nu\s+"
                + @"(?:koopidu|koppidu|azhaiku)$",
                RegexOptions.IgnoreCase);
        }

        if (address.Success)
        {
            string value = address.Groups["value"].Value.Trim();

            if (value.Length <= 40 &&
                Regex.IsMatch(value,
                    @"^[\p{L}\p{N}][\p{L}\p{N} .'\-]*$") &&
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4 &&
                !Regex.IsMatch(value, @"\b(and|then)\b",
                    RegexOptions.IgnoreCase))
                return new("address", value);

            return new("invalid_address");
        }

        Match language = Regex.Match(normalized,
            @"^(?:set|change) (?:my |your |the )?default language to "
            + @"(?<value>english|tamil|tanglish)$");

        if (!language.Success)
            language = Regex.Match(normalized,
                @"^(?:from now on[, ]+)(?:speak|reply|respond)"
                + @"(?: to me)?(?: in)? (?<value>english|tamil|tanglish)$");

        if (!language.Success)
            language = Regex.Match(normalized,
                @"^(?:speak|reply|respond)(?: to me)?(?: in)? "
                + @"(?<value>english|tamil|tanglish) from now on$");

        if (!language.Success)
            language = Regex.Match(normalized,
                @"^inime (?<value>english|tamil|tanglish)"
                + @"(?: la| le)? (?:pesu|reply pannu)$");

        if (language.Success)
        {
            string value = language.Groups["value"].Value;
            return new("language",
                char.ToUpperInvariant(value[0]) + value[1..]);
        }

        Match length = Regex.Match(normalized,
            @"^(?:from now on[, ]+)?keep (?:your |my |the )?"
            + @"(?:answers|replies|responses) "
            + @"(?<value>short|brief|concise|balanced|detailed)$");

        if (!length.Success)
            length = Regex.Match(normalized,
                @"^(?:from now on[, ]+)?(?:give|use) "
                + @"(?<value>short|brief|concise|balanced|detailed) "
                + @"(?:answers|replies|responses)$");

        if (length.Success)
        {
            string value = length.Groups["value"].Value;
            return new("length",
                value is "brief" or "concise" ? "short" : value);
        }

        if (normalized is "speak slower" or "talk slower" or
            "slow down your voice" or "speak more slowly")
            return new("slower");

        if (normalized is "speak faster" or "talk faster" or
            "speed up your voice")
            return new("faster");

        if (normalized is "speak at normal speed" or
            "reset your speech speed" or "normal speech speed")
            return new("rate", "0");

        Match rate = Regex.Match(normalized,
            @"^set (?:your |my |the )?speech rate to (?<value>-?\d+)$");

        if (rate.Success)
            return new("rate", rate.Groups["value"].Value);

        return null;
    }
}
