using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Friday.Core.Storage.Interfaces;

namespace Friday.Core.Preferences;

public sealed record PreferenceResult(string Text, bool Changed);

public sealed class PreferenceService
{
    private readonly IPreferenceStore _store;

    public PreferenceService(IPreferenceStore store)
    {
        _store = store;
    }

    public async Task<UserPreferences> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        return UserPreferences.FromValues(
            await _store.ReadAllAsync(cancellationToken));
    }

    public async Task<PreferenceResult?> TryHandleAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        PreferenceCommand? command = PreferenceCommands.Parse(message);
        if (command is null) return null;

        UserPreferences current = await ReadAsync(cancellationToken);
        UserPreferences updated = current;
        string response;

        switch (command.Action)
        {
            case "show":
                return new PreferenceResult(
                    $"Preferred address: {current.PreferredAddress ?? "not set"}.\n"
                    + $"Default language: {current.DefaultLanguage}.\n"
                    + $"Answer length: {current.ResponseLength}.\n"
                    + $"Speech rate: {current.SpeechRate} "
                    + "(zero is normal, negative is slower).", false);

            case "reset":
                updated = new UserPreferences();
                response = "Your saved preferences have been reset.";
                break;

            case "forget_address":
                updated = current with { PreferredAddress = null };
                response = "I've removed your saved preferred address.";
                break;

            case "address":
                updated = current with { PreferredAddress = command.Value };
                response =
                    $"Okay, {command.Value}. I'll call you {command.Value} from now on.";
                break;

            case "invalid_address":
                return new PreferenceResult(
                    "Please give me one name or title, up to forty characters. "
                    + "For example: Call me boss.", false);

            case "language":
                updated = current with { DefaultLanguage = command.Value! };
                response =
                    $"Your default language is now {command.Value}.";
                break;

            case "length":
                updated = current with { ResponseLength = command.Value! };
                response =
                    $"I'll use {command.Value} answers by default.";
                break;

            case "slower":
                updated = current with
                {
                    SpeechRate = Math.Max(-10, current.SpeechRate - 2)
                };
                response = $"Speech rate saved at {updated.SpeechRate}.";
                break;

            case "faster":
                updated = current with
                {
                    SpeechRate = Math.Min(10, current.SpeechRate + 2)
                };
                response = $"Speech rate saved at {updated.SpeechRate}.";
                break;

            case "rate":
                if (!int.TryParse(command.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int rate) ||
                    rate < -10 || rate > 10)
                    return new PreferenceResult(
                        "Speech rate must be between minus ten and ten.", false);

                updated = current with { SpeechRate = rate };
                response = $"Speech rate saved at {rate}.";
                break;

            default:
                return null;
        }

        bool changed = updated != current;

        // Confirm only after storage has completed successfully.
        if (changed)
            await _store.SaveAsync(updated.ToValues(), cancellationToken);

        return new PreferenceResult(response, changed);
    }
}
