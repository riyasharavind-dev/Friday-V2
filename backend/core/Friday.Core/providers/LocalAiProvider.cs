using System;
using System.Threading;
using System.Threading.Tasks;
using Friday.Contracts;

namespace Friday.Core.Providers;

public sealed class LocalAiProvider : IAiProvider
{
    public string Id => "local";

    // Enabled only after a local runtime is configured.
    public bool IsConfigured => false;

    public Task<AiResponse> GenerateAsync(
        AiRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        throw new InvalidOperationException(
            "Offline AI is not configured. " +
            "Configure a local runtime and model first.");
    }
}