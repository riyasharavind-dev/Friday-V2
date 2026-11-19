namespace Friday.Core.Navigation;

public enum NavigationAction
{
    Open, Close, Minimize, Maximize, Restore,
    NextTab, PreviousTab, Back, Forward, NextPage
}
public sealed record NavigationRequest(NavigationAction Action, string Target);
public sealed record NavigationTarget(string Id, string Name, string[] Aliases);
public sealed record NavigationRecipe(string TargetId, string DisplayName);
public sealed record NavigationResult(bool Accepted, string Text, bool InvalidTarget = false, string? ConfirmationKey = null);

// An agent can call this execution boundary later. No model/provider dependency.
public interface INavigationExecutor
{
    IReadOnlyList<NavigationTarget> KnownTargets => Array.Empty<NavigationTarget>();
    Task<IReadOnlyList<NavigationTarget>> ListTargetsAsync(bool refresh, CancellationToken token);
    Task<NavigationResult> OpenAsync(string targetId, CancellationToken token);
    Task<NavigationResult> ControlAsync(NavigationRequest request, CancellationToken token);
}
public interface INavigationService
{
    Task<string?> TryHandleAsync(string message, CancellationToken token = default);
}
public interface INavigationRecipeStore
{
    Task<NavigationRecipe?> ReadAsync(string key, CancellationToken token);
    Task SaveAsync(string key, NavigationRecipe recipe, CancellationToken token);
    Task RemoveAsync(string key, CancellationToken token);
}
