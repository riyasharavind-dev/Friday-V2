namespace Friday.Contracts;

public sealed record AiResponse(
    string Text,
    string Provider,
    string Model);