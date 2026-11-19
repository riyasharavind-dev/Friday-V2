using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Friday.Core.Storage.Interfaces;

public interface IPreferenceStore
{
    Task<IReadOnlyDictionary<string, string>> ReadAllAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);
}
