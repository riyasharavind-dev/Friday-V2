using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Friday.Contracts;

namespace Friday.Core.Providers;

public sealed class GroqAiProvider : IAiProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;

    public string Id => "groq";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_apiKey) &&
        !string.IsNullOrWhiteSpace(_model);

    public GroqAiProvider(
        HttpClient httpClient,
        string apiKey,
        string model)
    {
        _httpClient = httpClient;
        _apiKey = apiKey.Trim();
        _model = model.Trim();
    }

    public async Task<AiResponse> GenerateAsync(
        AiRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Groq API key or model is missing.");
        }

        var messages = request.Messages.Select(ToGroqMessage).ToArray();

        var tools = request.Tools is { Count: > 0 }
            ? request.Tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = tool.Description,
                    parameters = JsonSerializer.Deserialize<JsonElement>(
                        tool.ParametersJson)
                }
            }).ToArray()
            : null;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["max_completion_tokens"] = 2048,
            ["stream"] = false
        };

        if (tools is not null)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.groq.com/openai/v1/chat/completions");

        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);

        httpRequest.Content = JsonContent.Create(payload);

        using var response = await _httpClient.SendAsync(
            httpRequest,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                "Groq usage limit reached. Try again after quota resets.");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(
                "Groq rejected the API key. Check your key.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Groq returned HTTP {(int)response.StatusCode}. " +
                "Check your selected model and account access.");
        }

        await using var body = await response.Content.ReadAsStreamAsync(
            cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            body,
            cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "Groq returned no response choices.");
        }

        var messageElement = choices[0].GetProperty("message");

        string text = messageElement.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? ""
            : "";

        var toolCalls = ReadToolCalls(messageElement);

        if (string.IsNullOrWhiteSpace(text) && toolCalls.Count == 0)
        {
            throw new InvalidOperationException(
                "Groq returned an empty response.");
        }

        return new AiResponse(
            text,
            Id,
            _model,
            toolCalls.Count == 0 ? null : toolCalls);
    }

    private static object ToGroqMessage(ChatMessage message) => message.Role switch
    {
        ChatRole.System => new { role = "system", content = message.Content },
        ChatRole.User => new { role = "user", content = message.Content },
        ChatRole.Tool => new
        {
            role = "tool",
            tool_call_id = message.ToolCallId ?? "",
            content = message.Content
        },
        ChatRole.Assistant when message.ToolCalls is { Count: > 0 } => new
        {
            role = "assistant",
            content = string.IsNullOrEmpty(message.Content)
                ? null
                : message.Content,
            tool_calls = message.ToolCalls.Select(call => new
            {
                id = call.Id,
                type = "function",
                function = new
                {
                    name = call.Name,
                    arguments = call.ArgumentsJson
                }
            }).ToArray()
        },
        ChatRole.Assistant => new { role = "assistant", content = message.Content },
        _ => throw new ArgumentOutOfRangeException(nameof(message))
    };

    private static List<AiToolCall> ReadToolCalls(JsonElement messageElement)
    {
        var result = new List<AiToolCall>();
        if (!messageElement.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in toolCalls.EnumerateArray())
        {
            if (!item.TryGetProperty("function", out var function))
                continue;

            string name = function.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? ""
                : "";

            if (name.Length == 0) continue;

            string id = item.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? ""
                : "";

            string arguments =
                function.TryGetProperty("arguments", out var argsElement) &&
                argsElement.ValueKind == JsonValueKind.String
                ? argsElement.GetString() ?? "{}"
                : "{}";

            result.Add(new AiToolCall(id, name, arguments));
        }

        return result;
    }
}
