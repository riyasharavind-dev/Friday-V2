using System.Text.RegularExpressions;

namespace Friday.Desktop.Voice;

public static class SpeechTextFormatter
{
    public static bool HasCode(string text)
    {
        return text.Contains("```") || text.Contains("~~~");
    }

    public static string Prepare(string text)
    {
        // Remove fenced code blocks, including an unfinished final block.
        string result = Regex.Replace(
            text,
            @"(?ms)^[ \t]*(?<fence>`{3,}|~{3,})[^\r\n]*\r?\n.*?(?:^[ \t]*\k<fence>[ \t]*$|\z)",
            " ");

        // Remove inline code from the spoken explanation.
        result = Regex.Replace(result, @"`+[^`\r\n]*`+", " ");

        // Keep link labels without reading their URLs.
        result = Regex.Replace(
            result,
            @"!?\[([^\]]*)\]\([^)]+\)",
            "$1");

        result = Regex.Replace(
            result,
            @"https?://\S+",
            " ");

        // Remove common Markdown decoration.
        result = Regex.Replace(
            result,
            @"(?m)^\s{0,3}#{1,6}\s+",
            "");

        result = Regex.Replace(
            result,
            @"(?m)^\s*(?:[-*+]|\d+[.)])\s+",
            "");

        result = result.Replace("**", "")
                       .Replace("__", "")
                       .Replace("```", "")
                       .Replace("~~~", "");

        result = Regex.Replace(result, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(result) && HasCode(text))
        {
            return "I've displayed the code in the panel.";
        }

        return result;
    }
}