using System;
using System.Collections.Generic;
using System.Globalization;

namespace Friday.Core.Preferences;

public sealed record UserPreferences(
    string? PreferredAddress = null,
    string DefaultLanguage = "English",
    string ResponseLength = "balanced",
    int SpeechRate = 0)
{
    public IReadOnlyDictionary<string, string> ToValues()
    {
        var values = new Dictionary<string, string>
        {
            ["default_language"] = DefaultLanguage,
            ["response_length"] = ResponseLength,
            ["speech_rate"] = SpeechRate.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(PreferredAddress))
            values["preferred_address"] = PreferredAddress;

        return values;
    }

    public static UserPreferences FromValues(
        IReadOnlyDictionary<string, string> values)
    {
        values.TryGetValue("preferred_address", out string? address);
        values.TryGetValue("default_language", out string? language);
        values.TryGetValue("response_length", out string? length);
        values.TryGetValue("speech_rate", out string? rateText);

        int.TryParse(rateText, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int rate);

        return new UserPreferences(
            address,
            language is "English" or "Tamil" or "Tanglish"
                ? language : "English",
            length is "short" or "balanced" or "detailed"
                ? length : "balanced",
            Math.Clamp(rate, -10, 10));
    }

    public string WakeGreeting() => DefaultLanguage switch
    {
        "Tanglish" => PreferredAddress is null
            ? "Sollu, naan kekkuren."
            : $"Sollu {PreferredAddress}, naan kekkuren.",
        "Tamil" => PreferredAddress is null
            ? "சொல்லுங்க, நான் கேட்கிறேன்."
            : $"சொல்லுங்க {PreferredAddress}, நான் கேட்கிறேன்.",
        _ => PreferredAddress is null
            ? "Yes, I'm listening."
            : $"Yes, {PreferredAddress}. I'm listening."
    };
}
