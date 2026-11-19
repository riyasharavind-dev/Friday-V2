using System.Text.Json;

namespace Friday.Core.Interaction;

public sealed class InteractionOptions
{
    public string[] WakePhrases { get; set; } = ["hey friday", "hi friday", "hello friday", "friday"];
    public string[] InterruptRequests { get; set; } = ["stop", "stop talking", "stop speaking", "wait", "hold on", "cancel", "be quiet"];
    public string[] InterruptPhrases { get; set; } = ["hold on friday"];
    public string[] ConfirmRequests { get; set; } = ["confirm", "yes confirm"];
    public string[] ConfirmBeforeVoiceOpen { get; set; } = ["command prompt", "cmd", "powershell", "terminal", "registry editor"];
    public int FollowUpSeconds { get; set; } = 15;
    public int LocalDeadlineMilliseconds { get; set; } = 2000;
    public int ConfirmationSeconds { get; set; } = 10;
    public float CommandConfidence { get; set; } = .80f;
    public float TargetWordConfidence { get; set; } = .65f;
    public float WakeConfidence { get; set; } = .40f;
    public bool RequiresVoiceConfirmation(Friday.Core.Navigation.NavigationRequest? request, bool uncertain) =>
        uncertain || (request is { Action: Friday.Core.Navigation.NavigationAction.Open }
        && ConfirmBeforeVoiceOpen.Any(p => Friday.Core.Navigation.NavigationParser.Normalize(request.Target)
            .Contains(Friday.Core.Navigation.NavigationParser.Normalize(p), StringComparison.Ordinal)));

    public static InteractionOptions Load()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (DirectoryInfo? folder = new(start); folder is not null; folder = folder.Parent)
        {
            string path = Path.Combine(folder.FullName, "config", "interaction.json");
            if (!File.Exists(path)) continue;
            var result = JsonSerializer.Deserialize<InteractionOptions>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Interaction settings are empty.");
            result.Validate();
            return result;
        }
        return new();
    }

    public void Validate()
    {
        if (FollowUpSeconds is < 5 or > 60 || LocalDeadlineMilliseconds is < 500 or > 5000
            || ConfirmationSeconds is < 3 or > 30 || CommandConfidence is < .6f or > .95f
            || WakeConfidence is < .4f or > .95f || TargetWordConfidence is < .4f or > .95f
            || WakePhrases.Length == 0)
            throw new InvalidDataException("Invalid interaction timing or confidence settings.");
        foreach (var phrase in WakePhrases.Concat(InterruptRequests).Concat(InterruptPhrases).Concat(ConfirmRequests))
            if (string.IsNullOrWhiteSpace(phrase) || phrase.Length > 100 || phrase.Any(char.IsControl))
                throw new InvalidDataException("Invalid interaction phrase.");
    }
}
