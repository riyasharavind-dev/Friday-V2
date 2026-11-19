namespace Friday.Desktop.Navigation;

public sealed record NavigationCommandContext(nint Window, uint ProcessId, nint Foreground, long ExpiresAt)
{
    public bool Matches(uint processId, nint foreground) => ProcessId == processId && Foreground == foreground;
    public void EnsureFresh()
    {
        if (Environment.TickCount64 > ExpiresAt)
            throw new OperationCanceledException("The navigation command expired. Please repeat it.");
    }
}
