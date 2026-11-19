using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Friday.Core.Providers;

public sealed class GroqSpeechToText
{
    private readonly HttpClient _client;
    private readonly string _key;

    public GroqSpeechToText(HttpClient client, string key)
    {
        _client = client;
        _key = key.Trim();
    }

    public async Task<string> TranscribeAsync(
        byte[] audio,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_key))
        {
            throw new InvalidOperationException(
                "Groq API key missing. Check .env.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.groq.com/openai/v1/audio/transcriptions");

        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _key);

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent("whisper-large-v3-turbo"), "model");
        form.Add(new StringContent("json"), "response_format");

        request.Content = form;

        try
        {
        using var response = await _client.SendAsync(
            request,
            deadline.Token);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                "Speech quota reached. Wait for its reset; typed local navigation remains available.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Speech recognition failed: HTTP {(int)response.StatusCode}.");
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(deadline.Token);

        using var json = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: deadline.Token);

        return json.RootElement.TryGetProperty("text", out var text)
            ? text.GetString()?.Trim() ?? ""
            : "";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Voice recognition took too long. Please try again; listening remains enabled.");
        }

    }
}