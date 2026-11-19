using System.Collections.Generic;

namespace Friday.Contracts;

public sealed record AiResponse(
    string Text,
    string Provider,
    string Model,
    IReadOnlyList<AiToolCall>? ToolCalls = null);
