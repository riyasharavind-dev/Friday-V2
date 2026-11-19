using System;
using System.Text.RegularExpressions;

namespace Friday.Core.Conversation;

public sealed class ConversationSession
{
    private static readonly Regex WakePrefix = new(
        @"^\s*(?:(?:(?:hey|hi|hello|okay|ok)\s*[,!.]?\s*)?"
        + @"(?:friday|ஃபிரைடே|பிரைடே)(?=$|[\s,!.?:;\-])"
        + @"|(?:hi|hey|hello)(?=$|[\s,!.?:;\-]))"
        + @"[\s,!.?:;\-]*(?<request>.*)$",
        RegexOptions.IgnoreCase |
        RegexOptions.Singleline |
        RegexOptions.CultureInvariant);

    private readonly TimeSpan _followUpWindow;
    private DateTimeOffset _openedAt = DateTimeOffset.MaxValue;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public ConversationSession(TimeSpan followUpWindow)
    {
        _followUpWindow = followUpWindow;
    }

    public bool IsActive =>
        DateTimeOffset.UtcNow < _expiresAt;

    public bool TryAccept(
        string transcript,
        out string request)
    {
        request = "";

        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        string text = transcript.Trim();
        Match wake = WakePrefix.Match(text);

        if (wake.Success)
        {
            Refresh();
            request = wake.Groups["request"].Value.Trim();

            return true;
        }

        if (!IsActive)
        {
            return false;
        }

        request = text;
        return true;
    }

    public bool WasActiveAt(DateTimeOffset capturedAt) =>
        capturedAt >= _openedAt && capturedAt < _expiresAt;

    public void Refresh()
    {
        if (!IsActive) _openedAt = DateTimeOffset.UtcNow;
        _expiresAt = DateTimeOffset.UtcNow + _followUpWindow;
    }

    public void Close()
    {
        _expiresAt = DateTimeOffset.MinValue;
        _openedAt = DateTimeOffset.MaxValue;
    }
}