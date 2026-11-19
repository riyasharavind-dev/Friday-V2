using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Friday.Contracts;
using Friday.Core;
using Friday.Core.Configuration;
using Friday.Core.Interaction;
using Friday.Core.Navigation;
using Friday.Core.Preferences;
using Friday.Core.Providers;
using Friday.Core.Storage.Sqlite;
using Friday.Desktop.Captions;
using Friday.Desktop.Navigation;
using Friday.Desktop.Panels;
using Friday.Desktop.Voice;

namespace Friday.Desktop;

public partial class MainWindow : Window
{
    private readonly WindowsVoiceService _voice = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TurnCoordinator _turns = new();
    private readonly NavigationConfiguration _config;
    private readonly InteractionOptions _options;
    private readonly InputRouter _router;
    private readonly WindowsNavigationExecutor _executor;
    private readonly PreferenceService _preferences;
    private readonly FridayAssistant _assistant;
    private readonly NavigationService _navigation;
    private readonly DispatcherTimer _idleClock;
    private IReadOnlyList<NavigationTarget> _targets = [];
    private LocalNavigationListener? _commands;
    private OutputPanel? _panel;
    private CaptionWindow? _captions;
    private bool _ready, _busy, _speaking, _paused, _closing;
    private long _conversationUntil;
    private PendingConfirmation? _confirmation;
    private sealed record PendingConfirmation(string Phrase, NavigationCommandContext Context, long ExpiresAt);
    private bool Conversing => Environment.TickCount64 < _conversationUntil;

    public MainWindow()
    {
        InitializeComponent();
        EnvFileLoader.Load();
        _config = NavigationConfiguration.Load();
        _options = InteractionOptions.Load();
        _router = new(_options);
        _executor = new(_config);
        var store = new SqlitePreferenceStore(PreferencePaths.GetDatabasePath());
        _preferences = new(store);
        var provider = new GroqAiProvider(_http,
            Environment.GetEnvironmentVariable("FRIDAY_GROQ_API_KEY") ?? "",
            Environment.GetEnvironmentVariable("FRIDAY_GROQ_MODEL") ?? "openai/gpt-oss-20b");
        // The assistant interprets every request and drives navigation through
        // desktop tools; execution stays inside FRIDAY.
        _assistant = new FridayAssistant(provider, store);
        string database = Path.Combine(Path.GetDirectoryName(PreferencePaths.GetDatabasePath())!, "local-navigation.db");
        _navigation = new(_executor, new SqliteNavigationRecipeStore(database), _config,
            token => _preferences.ReadAsync(token));
        _voice.WaveformChanged += OnWaveform;
        _voice.SpeechProgress += OnSpeechProgress;
        PreviewKeyDown += OnShortcuts;
        _idleClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleClock.Tick += (_, _) => { if (!_busy) UpdateState(); };
        _idleClock.Start();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 8;
        ToolTip = "Starting FRIDAY voice…";
        try
        {
            VoiceTrace.Write("=== FRIDAY startup: API agent with desktop tools ===");
            _targets = await _executor.ListTargetsAsync(false, _shutdown.Token);
            var commands = new LocalNavigationListener(_config, _targets, _options, () => _executor.CaptureContext(allowRemembered: false));
            _commands = commands;
            commands.InputRecognized += OnVoiceInput;
            commands.Ready += OnReady;
            commands.Failed += OnRecognitionFailed;
            await commands.StartAsync().WaitAsync(TimeSpan.FromSeconds(15), _shutdown.Token);
            if (_closing || !ReferenceEquals(_commands, commands)) return;
            _ready = true; UpdateState();
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error)
        {
            DisableLocalVoice();
            ShowOutputPanel("Voice unavailable", error.Message + "\n\nTyped requests and API chat are still available.");
        }
    }

    private void OnReady() => Dispatcher.BeginInvoke(new Action(() =>
    { if (!_closing && _commands is not null) { _ready = true; UpdateState(); } }));
    private void OnRecognitionFailed(string error) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (_closing) return;
        DisableLocalVoice();
        ShowOutputPanel("Voice recognition error", error);
    }));
    private void DisableLocalVoice()
    {
        _ready = false;
        if (_commands is { } commands)
        {
            _commands = null;
            commands.InputRecognized -= OnVoiceInput;
            commands.Ready -= OnReady;
            commands.Failed -= OnRecognitionFailed;
            commands.Dispose();
        }
        UpdateState();
    }

    private void OnVoiceInput(VoiceInput input) => Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(async () =>
    {
        if (_closing || _paused || _commands?.InputEpoch != input.InputEpoch) return;
        if (!input.Interrupt && Environment.TickCount64 > input.Context.ExpiresAt) return;
        try { await HandleInputAsync(input.Text, false, input, _shutdown.Token); }
        catch (OperationCanceledException) { }
    }));

    private void CancelCurrent(bool closeConversation = true, bool clearPending = true)
    {
        _turns.Cancel();
        _commands?.InvalidatePendingInput();
        _voice.Stop();
        _commands?.SetOutputText("");
        _captions?.Hide();
        _busy = false; _speaking = false;
        _confirmation = null;
        if (clearPending) _navigation.CancelPending();
        if (closeConversation) _conversationUntil = 0;
        UpdateState();
    }

    private Task<string> GetReplyAsync(string message, CancellationToken token) => HandleInputAsync(message, true, null, token);

    private async Task<string> HandleInputAsync(string text, bool typed, VoiceInput? input, CancellationToken token)
    {
        if (_closing) throw new OperationCanceledException();
        if (_paused) return "FRIDAY is paused. Click Play to resume.";
        var route = _router.Route(text, typed, Conversing, _busy);
        if (route.Kind == InputKind.Ignore) return "";
        bool continueConversation = route.Addressed || Conversing || (typed && route.Kind == InputKind.Question);
        if (route.Kind == InputKind.Interrupt)
        {
            CancelCurrent();
            _panel?.SetOutput("FRIDAY", "Stopped.");
            return "Stopped.";
        }
        var context = input?.Context ?? _executor.CaptureContext(Environment.TickCount64 + 30_000);
        bool confirmed = route.Kind == InputKind.Confirm;
        PendingConfirmation? pending = null;
        if (confirmed)
        {
            pending = _confirmation;
            if (pending is null || Environment.TickCount64 > pending.ExpiresAt)
                return "No command is waiting for confirmation.";
            _confirmation = null;
        }
        if (!confirmed) _confirmation = null;
        if (_busy || route.Addressed || typed) CancelCurrent(closeConversation: false, clearPending: pending is null);
        if (route.Addressed) _conversationUntil = Environment.TickCount64 + _options.FollowUpSeconds * 1000L;
        using var turn = _turns.Begin(token);
        _commands?.InvalidatePendingInput();
        _busy = true; UpdateState();
        string result = "";
        try
        {
            if (route.Kind == InputKind.Wake)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);
                limit.CancelAfter(2000);
                result = (await _preferences.ReadAsync(limit.Token)).WakeGreeting();
            }
            else if (confirmed && pending is not null)
            {
                using var local = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);
                local.CancelAfter(TimeSpan.FromSeconds(15));
                using var scope = _executor.UseContext(pending.Context with
                    { ExpiresAt = Environment.TickCount64 + 30_000 });
                result = await _navigation.TryHandleAsync(pending.Phrase, local.Token).WaitAsync(local.Token)
                    ?? "I couldn't execute that confirmed command.";
                _navigation.CancelPending();
                turn.EnsureCurrent();
            }
            else
            {
                var toolContext = context with { ExpiresAt = Environment.TickCount64 + 30_000 };
                bool uncertain = input?.NeedsConfirmation == true;
                using var api = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);
                api.CancelAfter(TimeSpan.FromSeconds(60));
                var answer = await _assistant.RunAgentAsync(
                    route.Text,
                    DesktopTools.Definitions,
                    (call, callToken) => ExecuteToolAsync(call, toolContext, uncertain, callToken),
                    api.Token).WaitAsync(api.Token);
                turn.EnsureCurrent();
                if (_confirmation is { } required)
                {
                    result = "I heard: " + required.Phrase +
                        ".\nSay ‘Friday, confirm’ to execute, or ‘Friday, cancel’ to discard.";
                    ShowOutputPanel("Confirm voice command", result);
                    await SpeakAsync("Please confirm the command shown in the panel.", turn);
                    return result;
                }
                result = answer.Text;
            }
            turn.EnsureCurrent();
            bool code = SpeechTextFormatter.HasCode(result);
            if (code) ShowOutputPanel("FRIDAY", result, true);
            else _panel?.SetOutput("FRIDAY", result);
            await SpeakAsync(SpeechTextFormatter.Prepare(result), turn);
            turn.EnsureCurrent();
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!turn.IsCurrent) throw;
            if (turn.IsCurrent)
            {
                result = "The request timed out or was stopped. Please try again.";
                ShowOutputPanel("FRIDAY", result);
            }
            return result;
        }
        catch (Exception error)
        {
            if (turn.IsCurrent)
            {
                result = error is HttpRequestException
                    ? "Cannot reach the AI service. Desktop control is unavailable until the connection returns."
                    : error.Message;
                ShowOutputPanel("FRIDAY", result);
            }
            return result;
        }
        finally
        {
            if (turn.IsCurrent)
            {
                _busy = false; _speaking = false;
                _commands?.SetOutputText("");
                _captions?.Hide();
                if (continueConversation) _conversationUntil = Environment.TickCount64 + _options.FollowUpSeconds * 1000L;
                UpdateState();
            }
        }
    }

    // Executes one model tool call. The model decides what to do; this method
    // validates the action, applies confirmation policy and runs it locally.
    private async Task<string> ExecuteToolAsync(AiToolCall call, NavigationCommandContext context,
        bool uncertain, CancellationToken token)
    {
        using var scope = _executor.UseContext(context);
        if (call.Name == "list_installed_apps")
        {
            var targets = await _executor.ListTargetsAsync(false, token);
            string names = string.Join(", ", targets.Select(t => t.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .Take(150));
            return "Available applications and settings: " + names;
        }
        string? phrase = DesktopTools.ToPhrase(call.Name, call.ArgumentsJson);
        if (phrase is null) return "Unsupported tool request or invalid arguments.";
        var request = NavigationParser.Parse(phrase, _config);
        if (request is null) return "The request could not be mapped to a supported desktop action.";
        if (_options.RequiresVoiceConfirmation(request, uncertain))
        {
            _confirmation = new(phrase, context,
                Environment.TickCount64 + _options.ConfirmationSeconds * 1000L);
            return "CONFIRMATION_REQUIRED: the user must say 'Friday, confirm' before this action runs. Do not say it ran.";
        }
        string? reply = await _navigation.TryHandleAsync(phrase, token);
        _navigation.CancelPending();
        return reply ?? "The desktop action could not be executed.";
    }

    private async Task SpeakAsync(string text, TurnCoordinator.Turn turn)
    {
        turn.EnsureCurrent();
        if (string.IsNullOrWhiteSpace(text)) return;
        var preferences = await _preferences.ReadAsync(turn.Token);
        turn.EnsureCurrent();
        _voice.SetSpeechRate(preferences.SpeechRate);
        _speaking = true;
        _commands?.SetOutputText(text);
        UpdateState();
        _captions ??= new CaptionWindow { Owner = this };
        _captions.ShowCaption(text);
        try
        {
            bool accepted = await _voice.SpeakAsync(text).WaitAsync(turn.Token);
            turn.EnsureCurrent();
            if (!accepted) _panel?.SetOutput("Speech unavailable", text);
        }
        finally
        {
            if (turn.IsCurrent)
            {
                _speaking = false; _commands?.SetOutputText("");
                _captions.Hide(); UpdateState();
            }
        }
    }

    private void UpdateState()
    {
        if (_closing) return;
        _commands?.SetMode(_ready && !_paused && !_busy,
            Conversing && _confirmation is null);
        PauseIcon.Visibility = _paused ? Visibility.Collapsed : Visibility.Visible;
        ResumeIcon.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.ToolTip = _paused ? "Resume FRIDAY" : "Pause FRIDAY";
        ToolTip = _paused ? "FRIDAY paused." : _busy ? "Working. Say Friday, wait or tap to interrupt."
            : !_ready ? "Voice starting/unavailable. Ctrl+Shift+P opens typed chat."
            : Conversing ? "Listening for your next question…"
            : "Ready: say a command, or Hey Friday for a question.";
    }
    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        if (_paused) CancelCurrent();
        _commands?.SetCaptureEnabled(!_paused);
        if (_paused) WaveformVisual.SetLevels(Array.Empty<float>());
        UpdateState();
    }
    private async void IslandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) CancelCurrent();
        else if (!_paused) await HandleInputAsync("Friday", true, null, _shutdown.Token);
    }
    private void OnWaveform(float[] levels)
    {
        if (_closing) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => OnWaveform(levels))); return; }
        WaveformVisual.SetLevels(_paused ? Array.Empty<float>() : levels);
    }
    private void OnSpeechProgress(int position, int count)
    {
        if (!_closing) Dispatcher.BeginInvoke(new Action(() =>
        { if (_speaking && !_closing) _captions?.HighlightSpeech(position, count); }));
    }
    private OutputPanel GetOrCreatePanel()
    {
        if (_panel is null)
        {
            var panel = new OutputPanel { Owner = this, ReplyAsync = GetReplyAsync };
            panel.Closed += (_, _) => { if (ReferenceEquals(_panel, panel)) _panel = null; };
            _panel = panel;
        }
        return _panel;
    }
    public void ShowOutputPanel(string title, string content, bool isCode = false)
    {
        if (_closing) return;
        var panel = GetOrCreatePanel(); panel.SetOutput(title, content, isCode);
        if (!panel.IsVisible) panel.Show();
        panel.Activate();
    }
    private void OnShortcuts(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.P && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var panel = GetOrCreatePanel(); if (!panel.IsVisible) panel.Show(); panel.Activate(); e.Handled = true;
        }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_busy || _speaking) CancelCurrent();
        else Close();
    }
    protected override void OnClosed(EventArgs e)
    {
        _closing = true; _idleClock.Stop(); _shutdown.Cancel(); CancelCurrent();
        _commands?.Dispose(); _voice.Dispose(); _executor.Dispose(); _http.Dispose();
        _captions?.Close(); _panel?.Close();
        base.OnClosed(e);
    }
}
