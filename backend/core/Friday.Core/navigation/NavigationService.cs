using Friday.Core.Preferences;

namespace Friday.Core.Navigation;

// Entirely local: no IAiProvider, HttpClient, network resolver, or chat fallback.
public sealed class NavigationService(
    INavigationExecutor executor,
    INavigationRecipeStore recipes,
    NavigationConfiguration config,
    Func<CancellationToken, Task<UserPreferences>>? readPreferences = null) : INavigationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<NavigationTarget>? _choices;
    private string? _pendingKey;
    private DateTimeOffset _expires;
    public bool IsAwaitingChoice => _pendingKey is not null && DateTimeOffset.UtcNow < _expires;

    public async Task<string?> TryHandleAsync(string message, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            string? address = readPreferences is null ? null : (await readPreferences(token)).PreferredAddress;
            var request = NavigationParser.Parse(message, config);
            if (_pendingKey is not null && DateTimeOffset.UtcNow < _expires && request is null)
            {
                if (NavigationParser.Normalize(message) is "cancel" or "never mind")
                { Clear(); return config.Reply("Cancelled", address); }
                var targets = _choices ?? (await executor.ListTargetsAsync(false, token)).ToList();
                var candidates = NavigationParser.Match(targets, config.ResolveAlias(message));
                if (candidates.Count == 1)
                {
                    string key = _pendingKey == "open:" ? "open:" + NavigationParser.Normalize(message) : _pendingKey;
                    Clear();
                    return await OpenAsync(candidates[0], key, address, token);
                }
                return config.Reply("Choose", address) + " " + string.Join(", ", targets.Take(8).Select(t => t.Name));
            }
            Clear();
            if (request is null) return null;
            if (request.Action != NavigationAction.Open)
            {
                var result = await executor.ControlAsync(request, token);
                return result.Accepted ? config.Reply(result.ConfirmationKey ?? request.Action.ToString(), address) : result.Text;
            }
            string keyToSave = "open:" + NavigationParser.Normalize(request.Target);
            if (request.Target.Length == 0)
            {
                _pendingKey = keyToSave; _expires = DateTimeOffset.UtcNow.AddMinutes(1);
                return config.Reply("WhichApplication", address);
            }
            // Cache is a hint only. Resolve the requested name against today's catalog
            // before trusting a stored target ID; never execute a poisoned/stale recipe.
            var known = NavigationParser.Match(executor.KnownTargets, request.Target);
            var matches = known.Count > 0 ? known : NavigationParser.Match(
                await executor.ListTargetsAsync(false, token), request.Target);
            token.ThrowIfCancellationRequested();
            NavigationRecipe? cached = null;
            try { cached = await recipes.ReadAsync(keyToSave, token); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
            if (cached is not null && !matches.Any(t => t.Id == cached.TargetId))
            {
                try { await recipes.RemoveAsync(keyToSave, token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
            }
            if (matches.Count == 0) return config.Reply("NotFound", address, request.Target);
            if (matches.Count == 1) return await OpenAsync(matches[0], keyToSave, address, token);
            _choices = matches.Take(8).ToList(); _pendingKey = keyToSave; _expires = DateTimeOffset.UtcNow.AddMinutes(1);
            return config.Reply("Choose", address) + " " + string.Join(", ", _choices.Select(t => t.Name));
        }
        finally { _gate.Release(); }
    }

    public void CancelPending() => Clear();

    private void Clear() { _choices = null; _pendingKey = null; }

    private async Task<string> OpenAsync(NavigationTarget target, string key, string? address, CancellationToken token)
    {
        var result = await executor.OpenAsync(target.Id, token);
        if (!result.Accepted) return result.Text;
        try { await recipes.SaveAsync(key, new(target.Id, target.Name), token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException) { }
        return config.Reply("Open", address);
    }
}
