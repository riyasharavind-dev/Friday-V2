namespace Friday.Contracts;

public sealed record AiToolDefinition(
    string Name,
    string Description,
    string ParametersJson);

public sealed record AiToolCall(
    string Id,
    string Name,
    string ArgumentsJson);
