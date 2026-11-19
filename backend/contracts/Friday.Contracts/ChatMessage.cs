using System.Collections.Generic;

namespace Friday.Contracts;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    string? ToolCallId = null);
