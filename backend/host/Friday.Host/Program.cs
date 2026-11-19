using Friday.Core;
using Friday.Core.Configuration;
using Friday.Core.Providers;

Console.Title = "FRIDAY Backend";

// Load local configuration before reading provider settings.
EnvFileLoader.Load();

string apiKey =
    Environment.GetEnvironmentVariable("FRIDAY_GROQ_API_KEY") ?? "";

string model =
    Environment.GetEnvironmentVariable("FRIDAY_GROQ_MODEL") ?? "";

if (string.IsNullOrWhiteSpace(apiKey) ||
    string.IsNullOrWhiteSpace(model))
{
    Console.WriteLine("Groq configuration is missing.");
    Console.WriteLine(
        "Check FRIDAY_GROQ_API_KEY and FRIDAY_GROQ_MODEL in your .env file.");

    return;
}

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(60)
};

var provider = new GroqAiProvider(
    httpClient,
    apiKey,
    model);

var assistant = new FridayAssistant(provider);

using var shutdown = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine("FRIDAY online chat");
Console.WriteLine($"Provider: {provider.Id}");
Console.WriteLine($"Model: {model}");
Console.WriteLine("Session memory: latest 10 exchanges");
Console.WriteLine("/clear — reset conversation");
Console.WriteLine("/exit  — close");
Console.WriteLine();

while (!shutdown.IsCancellationRequested)
{
    Console.Write("You: ");

    string? input = Console.ReadLine();

    if (input is null ||
        input.Trim().Equals(
            "/exit",
            StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    input = input.Trim();

    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    try
    {
        if (input.Equals(
            "/clear",
            StringComparison.OrdinalIgnoreCase))
        {
            await assistant.ClearHistoryAsync(shutdown.Token);

            Console.WriteLine("Conversation cleared.");
            Console.WriteLine();

            continue;
        }

        var response = await assistant.ReplyAsync(
            input,
            shutdown.Token);

        Console.WriteLine();
        Console.WriteLine($"FRIDAY: {response.Text}");
        Console.WriteLine();
    }
    catch (OperationCanceledException)
    {
        if (shutdown.IsCancellationRequested)
        {
            break;
        }

        Console.WriteLine("Request timed out. Try again.");
    }
    catch (HttpRequestException)
    {
        Console.WriteLine(
            "Cannot reach Groq. Check your internet connection.");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"Error: {exception.Message}");
    }
}