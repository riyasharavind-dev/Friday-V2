using System.Collections.Generic;

namespace Friday.Contracts;

public sealed record AiRequest(
    IReadOnlyList<ChatMessage> Messages);