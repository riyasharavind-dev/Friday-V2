using System;

namespace Friday.Desktop.Island;

public sealed class IslandController
{
    // Voice engine will connect to this event later.
    public event EventHandler? InterruptRequested;

    public void RequestInterrupt()
    {
        InterruptRequested?.Invoke(this, EventArgs.Empty);
    }
}