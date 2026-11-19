using System.Threading;
using System.Windows;

namespace Friday.Desktop;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // One FRIDAY process only: two instances fight over the microphone and
        // make listening/speaking behave unpredictably.
        _instanceMutex = new Mutex(initiallyOwned: true,
            @"Local\FRIDAY.Desktop.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show(
                "FRIDAY is already running. Close the existing window first.",
                "FRIDAY", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
