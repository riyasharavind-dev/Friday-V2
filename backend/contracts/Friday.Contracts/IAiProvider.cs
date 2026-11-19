using System.Threading;
using System.Threading.Tasks;

namespace Friday.Contracts;

public interface IAiProvider
{
    string Id { get; }

    bool IsConfigured { get; }

    Task<AiResponse> GenerateAsync(
        AiRequest request,
        CancellationToken cancellationToken = default);
}