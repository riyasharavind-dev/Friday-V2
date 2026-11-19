using System;
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

        var messages = request.Messages.Select(message => new
        {
            role = message.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                _ => throw new ArgumentOutOfRangeException()
            },
            content = message.Content
        }).ToArray();

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.groq.com/openai/v1/chat/completions");

        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _apiKey);

        httpRequest.Content = JsonContent.Create(new
        {
            model = _model,
            messages,
            max_completion_tokens = 1024,
            stream = false
        });

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

        if (!messageElement.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException(
                "Groq returned no text content.");
        }

        string? text = content.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Groq returned an empty response.");
        }

        return new AiResponse(text, Id, _model);
    }
}