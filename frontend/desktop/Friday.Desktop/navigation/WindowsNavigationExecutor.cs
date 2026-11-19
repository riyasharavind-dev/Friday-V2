using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Friday.Core.Navigation;

namespace Friday.Desktop.Navigation;

public sealed record InstalledApp(string Id, string Name, string Target, bool IsAppId);
public interface IInstalledAppCatalog
{
    Task<IReadOnlyList<InstalledApp>> GetAsync(bool refresh, CancellationToken cancellationToken);
}

public sealed class WindowsNavigationExecutor : INavigationExecutor, IDisposable
{
    private readonly IInstalledAppCatalog _apps;
    private readonly WindowTracker _windows;
    private readonly NavigationConfiguration _config;
    private readonly AsyncLocal<NavigationCommandContext?> _context = new();
    public NavigationCommandContext CaptureContext(long expiresAt = long.MaxValue, bool allowRemembered = true) => _windows.Capture(expiresAt, allowRemembered);
    public IDisposable UseContext(NavigationCommandContext context)
    {
        var previous = _context.Value; _context.Value = context;
        return new ContextScope(() => _context.Value = previous);
    }
    private sealed class ContextScope(Action clear) : IDisposable { public void Dispose() => clear(); }

    private (string Key, string Name, string Uri, string[] Aliases)[] Pages => _config.SystemTargets
        .Select(t => (t.Key, t.Name, t.Uri, t.Aliases)).ToArray();

    public WindowsNavigationExecutor() : this(NavigationConfiguration.Load()) { }
    public WindowsNavigationExecutor(IInstalledAppCatalog apps) : this(NavigationConfiguration.Load(), apps) { }
    public WindowsNavigationExecutor(NavigationConfiguration config, IInstalledAppCatalog? apps = null)
    {
        config.Validate();
        _config = config;
        _apps = apps ?? new WindowsAppCatalog();
        _windows = new WindowTracker(config);
    }

    public IReadOnlyList<NavigationTarget> KnownTargets => Pages
        .Select(p => new NavigationTarget("settings:" + p.Key, p.Name, p.Aliases)).ToArray();

    public async Task<IReadOnlyList<NavigationTarget>> ListTargetsAsync(bool refresh, CancellationToken token)
    {
        var result = Pages.Select(p => new NavigationTarget("settings:" + p.Key, p.Name, p.Aliases)).ToList();
        // Settings owns its own resolver entry, so the OS app entry must not create a duplicate.
        result.AddRange((await _apps.GetAsync(refresh, token))
            .Where(a => NavigationParser.Normalize(a.Name) is not ("settings" or "windows settings"))
            .Select(a => new NavigationTarget("app:" + a.Id, a.Name, [])));
        return result;
    }

    public async Task<NavigationResult> OpenAsync(string targetId, CancellationToken token)
    {
        _context.Value?.EnsureFresh();
        var page = Pages.FirstOrDefault(p => "settings:" + p.Key == targetId);
        if (page.Key is not null)
            return await LaunchAsync(new ProcessStartInfo(page.Uri) { UseShellExecute = true }, page.Name, token, _context.Value);
        var app = (await _apps.GetAsync(false, token)).FirstOrDefault(a => "app:" + a.Id == targetId);
        if (app is null) return new(false, "That saved application is no longer available.", true);
        ProcessStartInfo info;
        if (app.IsAppId)
        {
            info = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
            { UseShellExecute = true };
            info.ArgumentList.Add("shell:AppsFolder\\" + app.Target);
        }
        else
        {
            if (!Path.IsPathFullyQualified(app.Target) || !File.Exists(app.Target)
                || !(Path.GetExtension(app.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(app.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                return new(false, "That saved application target has moved or been removed.", true);
            info = new ProcessStartInfo(app.Target) { UseShellExecute = true };
        }
        return await LaunchAsync(info, app.Name, token, _context.Value);
    }

    private static Task<NavigationResult> LaunchAsync(ProcessStartInfo info, string name, CancellationToken token, NavigationCommandContext? context)
    {
        var completion = new TaskCompletionSource<NavigationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                context?.EnsureFresh();
                Process.Start(info)?.Dispose();
                completion.TrySetResult(new(true, "Opening " + name + "."));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception) { completion.TrySetResult(new(false, "Windows couldn't open " + name + ".")); }
        }) { IsBackground = true, Name = "FRIDAY navigation launch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(token);
    }

    public Task<NavigationResult> ControlAsync(NavigationRequest request, CancellationToken token)
    {
        // Bind identity before dispatch, never choose the active window in a delayed worker.
        var context = _context.Value ?? CaptureContext(Environment.TickCount64 + 2000);
        return Task.Run(() => _windows.Execute(request, context, token), token);
    }

    public void Dispose() => _windows.Dispose();
}

// Remembers the external foreground window before FRIDAY's own input panel takes focus.
internal sealed class WindowTracker : IDisposable
{
    private nint _last;
    private readonly WinEventProc _callback;
    private readonly nint _hook;
    private volatile bool _disposed;
    private readonly NavigationConfiguration _config;

    public WindowTracker(NavigationConfiguration config)
    {
        _config = config;
        _callback = OnForeground;
        Remember(GetForegroundWindow());
        _hook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
    }

    private void OnForeground(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time) => Remember(window);
    private void Remember(nint window)
    {
        if (_disposed || !Usable(window)) return;
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") return;
        Interlocked.Exchange(ref _last, window);
    }

    private static bool Usable(nint window)
    {
        if (window == 0 || !IsWindow(window) || !IsWindowVisible(window)) return false;
        GetWindowThreadProcessId(window, out uint process);
        return process != Environment.ProcessId;
    }

    public NavigationCommandContext Capture(long expiresAt, bool allowRemembered)
    {
        nint foreground = GetForegroundWindow();
        Remember(foreground);
        nint window = Usable(foreground) ? foreground : allowRemembered ? Interlocked.CompareExchange(ref _last, 0, 0) : 0;
        GetWindowThreadProcessId(window, out uint pid);
        return new(window, pid, foreground, expiresAt);
    }

    public NavigationResult Execute(NavigationRequest request, NavigationCommandContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        context.EnsureFresh();
        if (_disposed) return new(false, "Navigation has stopped.");
        if (request.Action == NavigationAction.NextPage)
            return new(false, "Do you mean the next browser tab, scrolling down, or a Next button on this page? Page-element navigation isn't connected yet.");
        nint window;
        if (!string.IsNullOrWhiteSpace(request.Target))
        {
            var matches = FindWindows(request.Target, token);
            if (matches.Count != 1)
                return new(false, matches.Count == 0 ? "I couldn't find that application's open window."
                    : "More than one window matches. Focus the one you want, then say close this window.");
            window = matches[0];
        }
        else
        {
            window = context.Window;
            GetWindowThreadProcessId(window, out uint currentPid);
            if (!context.Matches(currentPid, GetForegroundWindow()))
                return new(false, "The active window changed. Please repeat the command in the intended window.");
        }
        if (!Usable(window)) return new(false, "Focus the application you want to control, then try again.");
        token.ThrowIfCancellationRequested();
        context.EnsureFresh();
        if (string.IsNullOrWhiteSpace(request.Target) && GetForegroundWindow() != context.Foreground)
            return new(false, "The active window changed. Nothing was changed.");
        if (request.Action == NavigationAction.Close)
        {
            if (!PostMessage(window, 0x0010, 0, 0)) return new(false, "Windows rejected the close request.");
            for (int i = 0; i < 10 && IsWindow(window); i++)
            {
                token.ThrowIfCancellationRequested();
                Thread.Sleep(30);
            }
            return IsWindow(window)
                ? new(true, "Close requested.", ConfirmationKey: "CloseRequested")
                : new(true, "Closed.", ConfirmationKey: "Close");
        }
        if (request.Action is NavigationAction.Minimize or NavigationAction.Maximize or NavigationAction.Restore)
        {
            int code = request.Action == NavigationAction.Minimize ? 6 : request.Action == NavigationAction.Maximize ? 3 : 9;
            ShowWindowAsync(window, code);
            for (int i = 0; i < 6; i++)
            {
                token.ThrowIfCancellationRequested();
                bool changed = request.Action == NavigationAction.Minimize ? IsIconic(window)
                    : request.Action == NavigationAction.Maximize ? IsZoomed(window)
                    : !IsIconic(window) && !IsZoomed(window);
                if (changed) return new(true, "Window changed.", ConfirmationKey: request.Action.ToString());
                Thread.Sleep(25);
            }
            return new(true, "Window action requested.", ConfirmationKey: "WindowRequested");
        }
        if (!IsBrowser(window))
            return new(false, "Focus a supported browser window first. I won't send browser shortcuts to another application.");
        if (GetForegroundWindow() != window && !SetForegroundWindow(window))
            return new(false, "I couldn't focus the browser. Click it and try again.");
        // Do not send keys if another app took foreground after focus was requested.
        if (GetForegroundWindow() != window) return new(false, "The active window changed. Try again in the browser.");
        ushort[] keys = _config.Keys(request.Action);
        if (keys.Length == 0) return new(false, "That navigation action isn't supported yet.");
        token.ThrowIfCancellationRequested();
        var inputs = keys.Select(k => Key(k, false)).Concat(keys.Reverse().Select(k => Key(k, true))).ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            // Release any modifier even if Windows rejected part of the batch.
            var releases = keys.Reverse().Select(k => Key(k, true)).ToArray();
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
            return new(false, "Windows couldn't complete that keyboard action.");
        }
        return new(true, "Navigation shortcut sent.");
    }

    private bool IsBrowser(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return _config.BrowserProcesses.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static List<nint> FindWindows(string query, CancellationToken token)
    {
        var result = new List<nint>();
        EnumWindows((window, _) =>
        {
            if (token.IsCancellationRequested) return false;
            if (!Usable(window)) return true;
            var title = new StringBuilder(1024);
            GetWindowText(window, title, title.Capacity);
            if (title.Length == 0) return true;
            GetWindowThreadProcessId(window, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                // A web page/document title mentioning Notepad is not the Notepad app.
                // Match executable identity and product metadata, never arbitrary title words.
                var aliases = new List<string> { process.ProcessName };
                try
                {
                    string? file = process.MainModule?.FileName;
                    if (file is not null)
                    {
                        var info = FileVersionInfo.GetVersionInfo(file);
                        if (!string.IsNullOrWhiteSpace(info.FileDescription)) aliases.Add(info.FileDescription);
                        if (!string.IsNullOrWhiteSpace(info.ProductName)) aliases.Add(info.ProductName);
                    }
                }
                catch { }
                var candidate = new NavigationTarget("", process.ProcessName, aliases.ToArray());
                if (NavigationParser.Match([candidate], query).Count == 1) result.Add(window);
            }
            catch { }
            return true;
        }, 0);
        token.ThrowIfCancellationRequested();
        return result;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_hook != 0) UnhookWinEvent(_hook);
        GC.KeepAlive(_callback);
    }

    private static INPUT Key(ushort key, bool up) => new()
    {
        type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { key = key, flags = up ? 2u : 0u } }
    };
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public MOUSEINPUT mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    { public ushort key, scan; public uint flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    { public int x, y; public uint mouseData, flags, time; public nuint extra; }
    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    private delegate bool EnumWindowsProc(nint window, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint data);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
