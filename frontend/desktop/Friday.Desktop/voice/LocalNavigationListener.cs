using System.Globalization;
using System.Speech.Recognition;
using System.Windows.Threading;
using Friday.Core.Navigation;
using Friday.Core.Interaction;
using Friday.Desktop.Navigation;

namespace Friday.Desktop.Voice;

public sealed record VoiceInput(string Text, float Confidence, bool NeedsConfirmation,
    NavigationCommandContext Context, long ReceivedAt, long InputEpoch, bool Interrupt = false);

// One native capture owner, with local commands, addressing grammar and English dictation.
public sealed class LocalNavigationListener : IDisposable
{
    private readonly NavigationConfiguration _config;
    private readonly InteractionOptions _options;
    private readonly IReadOnlyList<NavigationTarget> _targets;
    private readonly Func<NavigationCommandContext> _captureContext;
    private readonly InputRouter _router;
    private Grammar? _commandGrammar;
    private DictationGrammar? _conversationGrammar;
    private int _conversationEnabled;
    private SpeechRecognitionEngine? _engine;
    private Dispatcher? _dispatcher;
    private Task? _startup;
    private volatile bool _disposed;
    private int _enabled, _captureEnabled = 1;
    private bool _recognizing;
    private NavigationCommandContext? _speechContext;
    private long _acceptAfterTicks, _lastLevelLog;
    private int _peak;
    private long _inputEpoch, _speechEpoch;
    public long InputEpoch => Interlocked.Read(ref _inputEpoch);
    public void InvalidatePendingInput() => Interlocked.Increment(ref _inputEpoch);
    private string _spokenText = "";
    private long _spokenUntil;
    public void SetOutputText(string text)
    {
        if (text.Length > 0)
        {
            Volatile.Write(ref _spokenText, NavigationParser.Normalize(text));
            Interlocked.Exchange(ref _spokenUntil, long.MaxValue);
        }
        else Interlocked.Exchange(ref _spokenUntil, Environment.TickCount64 + 1000);
    }

    public event Action<VoiceInput>? InputRecognized;
    public event Action? Ready;
    public event Action<string>? Failed;

    public LocalNavigationListener(NavigationConfiguration config, IReadOnlyList<NavigationTarget> targets,
        InteractionOptions options, Func<NavigationCommandContext> captureContext)
    {
        _config = config; _targets = targets; _options = options;
        _captureContext = captureContext; _router = new(options);
    }

    public void SetMode(bool normalInput, bool conversation)
    {
        bool normalChanged = Volatile.Read(ref _enabled) != (normalInput ? 1 : 0);
        SetEnabled(normalInput);
        int next = normalInput && conversation ? 1 : 0;
        int previous = Interlocked.Exchange(ref _conversationEnabled, next);
        if (!normalChanged && previous == next) return;
        _dispatcher?.BeginInvoke(new Action(ApplyGrammarMode));
    }

    private void ApplyGrammarMode()
    {
        if (_disposed || _engine is null) return;
        try
        {
            if (_commandGrammar is not null) _commandGrammar.Enabled = Volatile.Read(ref _enabled) == 1;
            if (_conversationGrammar is not null) _conversationGrammar.Enabled = Volatile.Read(ref _conversationEnabled) == 1;
        }
        catch (Exception error) { Fail(error); }
    }

    // Commands/dictation may be gated during a turn; deliberate addressing remains live.
    public void SetEnabled(bool normalInput)
    {
        int next = normalInput ? 1 : 0;
        if (Interlocked.Exchange(ref _enabled, next) == next || !normalInput) return;
        _dispatcher?.BeginInvoke(new Action(() =>
        {
            if (_disposed || _engine is null) return;
            try { Interlocked.Exchange(ref _acceptAfterTicks, _engine.AudioPosition.Ticks); }
            catch (Exception error) { Fail(error); }
        }));
    }

    public void SetCaptureEnabled(bool enabled)
    {
        int next = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _captureEnabled, next) == next) return;
        InvalidatePendingInput();
        _dispatcher?.BeginInvoke(new Action(() =>
        {
            if (_disposed || _engine is null) return;
            try
            {
                if (Volatile.Read(ref _captureEnabled) == 0)
                {
                    if (_recognizing) _engine.RecognizeAsyncCancel();
                    else _engine.SetInputToNull();
                }
                else if (!_recognizing) StartCapture();
            }
            catch (Exception error) { Fail(error); }
        }));
    }

    private void StartCapture()
    {
        if (_disposed || _engine is null || Volatile.Read(ref _captureEnabled) == 0) return;
        _speechContext = null;
        Interlocked.Exchange(ref _acceptAfterTicks, 0);
        _engine.SetInputToDefaultAudioDevice();
        _engine.RecognizeAsync(RecognizeMode.Multiple);
        _recognizing = true;
        VoiceTrace.Write("Hybrid voice: native microphone attached; recognition started.");
    }

    public Task StartAsync()
    {
        if (_startup is not null) return _startup;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _startup = completion.Task;
        var thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                var installed = SpeechRecognitionEngine.InstalledRecognizers();
                var selected = installed.FirstOrDefault(r => r.Culture.Name.Equals("en-GB", StringComparison.OrdinalIgnoreCase))
                    ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
                if (selected is null) throw new InvalidOperationException("No local English speech recognizer is available. Typed commands and chat still work.");
                var engine = _engine = new SpeechRecognitionEngine(selected);
                var grammars = CreateGrammarSet(_config, _targets, _options, selected.Culture);
                _commandGrammar = grammars.Local;
                _conversationGrammar = grammars.Conversation;
                engine.LoadGrammar(grammars.Local);
                engine.LoadGrammar(grammars.Address);
                engine.LoadGrammar(grammars.Conversation);
                engine.InitialSilenceTimeout = TimeSpan.Zero;
                engine.BabbleTimeout = TimeSpan.FromSeconds(8);
                engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(250);
                engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(400);
                engine.SpeechDetected += OnDetected;
                engine.SpeechRecognized += OnRecognized;
                engine.SpeechRecognitionRejected += OnRejected;
                engine.RecognizeCompleted += OnCompleted;
                engine.AudioLevelUpdated += OnLevel;
                if (_disposed) { engine.Dispose(); completion.TrySetCanceled(); return; }
                StartCapture();
                VoiceTrace.Write("Hybrid voice READY; culture=" + selected.Culture.Name);
                completion.TrySetResult(); Ready?.Invoke();
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                try { _engine?.Dispose(); } catch { }
                _engine = null;
                completion.TrySetException(error); Fail(error);
            }
        }) { IsBackground = true, Name = "FRIDAY hybrid recognizer" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return _startup;
    }

    public static (Grammar Local, Grammar Address, DictationGrammar Conversation) CreateGrammarSet(
        NavigationConfiguration config, IReadOnlyList<NavigationTarget> targets, InteractionOptions options, CultureInfo culture) =>
        (new Grammar(CreateGrammar(config, targets, culture)) { Name = "LocalCommands", Priority = 1 },
         new Grammar(CreateAddressGrammar(options, culture)) { Name = "AddressFriday", Priority = 5 },
         new DictationGrammar { Name = "Conversation", Enabled = false });

    public static GrammarBuilder CreateAddressGrammar(InteractionOptions options, CultureInfo culture)
    {
        var choices = new Choices();
        foreach (string phrase in options.WakePhrases)
        {
            choices.Add(new GrammarBuilder(phrase) { Culture = culture });
            var request = new GrammarBuilder(phrase) { Culture = culture };
            request.AppendDictation(); choices.Add(request);
            foreach (string command in options.InterruptRequests.Concat(options.ConfirmRequests))
                choices.Add(new GrammarBuilder(phrase + " " + command) { Culture = culture });
        }
        foreach (string phrase in options.InterruptPhrases)
            choices.Add(new GrammarBuilder(phrase) { Culture = culture });
        // Bare barge-in words stay loaded so a plain "stop" can interrupt speech.
        // The router ignores them while idle; only an active turn accepts them.
        foreach (string interrupt in options.InterruptRequests)
            choices.Add(new GrammarBuilder(interrupt) { Culture = culture });
        var builder = new GrammarBuilder { Culture = culture }; builder.Append(choices); return builder;
    }

    public static GrammarBuilder CreateGrammar(NavigationConfiguration config,
        IReadOnlyList<NavigationTarget> targets, CultureInfo culture)
    {
        var names = NavigationParser.SpokenTargets(targets, config);
        if (names.Length == 0) throw new InvalidOperationException("No spoken application/destination names were discovered.");
        var targetChoices = new Choices(names);
        var alternatives = new Choices();
        foreach (string phrase in config.Commands.SelectMany(c => c.Phrases).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var branch = new GrammarBuilder { Culture = culture };
            int target = phrase.IndexOf("{target}", StringComparison.Ordinal);
            if (target < 0) branch.Append(phrase);
            else
            {
                string before = phrase[..target].Trim();
                string after = phrase[(target + 8)..].Trim();
                if (before.Length > 0) branch.Append(before);
                branch.Append(targetChoices);
                if (after.Length > 0) branch.Append(after);
            }
            alternatives.Add(branch);
        }
        // Target-only replies are executed only when the service has a pending choice.
        var selection = new GrammarBuilder { Culture = culture };
        selection.Append(targetChoices); alternatives.Add(selection);
        alternatives.Add(new GrammarBuilder("cancel") { Culture = culture });
        var builder = new GrammarBuilder { Culture = culture };
        builder.Append(alternatives);
        return builder;
    }

    private void OnDetected(object? sender, SpeechDetectedEventArgs e)
    {
        if (!_disposed) { _speechContext = _captureContext(); _speechEpoch = InputEpoch; }
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        if (_disposed || Volatile.Read(ref _captureEnabled) == 0 || _engine is null) return;
        try
        {
            var result = e.Result;
            string normalized = NavigationParser.Normalize(result.Text);
            string spoken = Volatile.Read(ref _spokenText);
            if (Environment.TickCount64 <= Interlocked.Read(ref _spokenUntil) && spoken.Length > 0 && normalized.Length > 0
                && (" " + spoken + " ").Contains(" " + normalized + " ", StringComparison.Ordinal)) return;
            var routed = _router.Route(result.Text, false, true, Volatile.Read(ref _enabled) == 0);
            if (routed.Kind == InputKind.Ignore) return;
            bool interrupt = routed.Kind == InputKind.Interrupt;
            if (!routed.Addressed && !interrupt && _speechEpoch != InputEpoch) return;
            float minimum = routed.Addressed || interrupt ? _options.WakeConfidence : _config.RecognitionConfidence;
            if (result.Confidence < minimum)
            {
                VoiceTrace.Write($"Speech below acceptance floor: score={result.Confidence:F2}; required={minimum:F2}; grammar={result.Grammar.Name}.");
                return;
            }
            var audio = result.Audio;
            long now = Environment.TickCount64;
            long lag = audio is null ? 0 : Math.Max(0, (long)(_engine.AudioPosition - audio.AudioPosition - audio.Duration).TotalMilliseconds);
            // Ignore audio buffered before normal listening re-armed. Interruptions are exempt.
            if (!routed.Addressed && !interrupt && audio is not null && audio.AudioPosition.Ticks < Interlocked.Read(ref _acceptAfterTicks)) return;
            // A late "stop" must still interrupt when its recognition result arrives.
            if (!interrupt && lag > _options.LocalDeadlineMilliseconds) { VoiceTrace.Write("Dropped expired speech recognition result."); return; }
            bool uncertain = result.Confidence < _options.CommandConfidence
                || result.Words.Any(w => w.Confidence < _options.TargetWordConfidence);
            var request = NavigationParser.Parse(routed.Text, _config);
            if (request is not null)
                uncertain |= result.Alternates.Any(a => a.Text != result.Text
                    && result.Confidence - a.Confidence < .12f
                    && NavigationParser.Parse(a.Text, _config) is { } other && other != request);
            var context = (_speechContext ?? _captureContext()) with
                { ExpiresAt = now - lag + _options.LocalDeadlineMilliseconds };
            _speechContext = null;
            VoiceTrace.Write($"Hybrid recognition result: confidence={result.Confidence:F2}; grammar={result.Grammar.Name}; age_ms={lag}; confirmation={uncertain}.");
            InputRecognized?.Invoke(new(result.Text, result.Confidence, uncertain, context, now, InputEpoch, interrupt));
        }
        catch (Exception error) { Fail(error); }
    }

    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs e)
    { if (!_disposed) VoiceTrace.Write($"Hybrid speech rejected; confidence={e.Result.Confidence:F2}; no action sent."); }
    private void OnLevel(object? sender, AudioLevelUpdatedEventArgs e)
    {
        if (_disposed) return;
        _peak = Math.Max(_peak, e.AudioLevel);
        long now = Environment.TickCount64;
        if (now - _lastLevelLog < 5000) return;
        _lastLevelLog = now;
        VoiceTrace.Write($"Native microphone peak={_peak}/100; normal listening={Volatile.Read(ref _enabled) == 1}.");
        _peak = 0;
    }
    private void OnCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        if (_disposed) return;
        _dispatcher?.BeginInvoke(new Action(() =>
        {
            if (_disposed || _engine is null) return;
            _recognizing = false;
            if (e.Error is not null) { Fail(e.Error); return; }
            try
            {
                if (Volatile.Read(ref _captureEnabled) == 0) _engine.SetInputToNull();
                else StartCapture();
            }
            catch (Exception error) { Fail(error); }
        }));
    }
    private void Fail(Exception error)
    {
        VoiceTrace.Write("Hybrid recognition error: " + error);
        if (!_disposed) Failed?.Invoke(error.Message);
    }
    public void Dispose()
    {
        _disposed = true;
        _dispatcher?.BeginInvoke(new Action(() =>
        {
            var engine = _engine; _engine = null;
            if (engine is not null)
            {
                engine.SpeechDetected -= OnDetected;
                engine.SpeechRecognized -= OnRecognized;
                engine.SpeechRecognitionRejected -= OnRejected;
                engine.RecognizeCompleted -= OnCompleted;
                engine.AudioLevelUpdated -= OnLevel;
                try { engine.RecognizeAsyncCancel(); } finally { engine.Dispose(); }
            }
            _dispatcher!.BeginInvokeShutdown(DispatcherPriority.Background);
        }));
    }
}
