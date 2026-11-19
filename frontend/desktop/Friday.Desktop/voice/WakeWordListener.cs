using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Friday.Desktop.Voice;

public sealed class WakeWordListener : IDisposable
{
    private Dispatcher? _dispatcher;
    private Task? _startup;
    private readonly PcmInputStream _input = new();

    private SpeechRecognitionEngine? _engine;
    private volatile bool _disposed;
    private int _armed;
    private int _wakeArmed;

    // Existing event remains the Stop interrupt for compatibility.
    public event Action? WakeDetected;
    public event Action<string>? GreetingDetected;
    public event Action? Ready;
    public event Action<string>? Failed;

    public void Start() => StartAsync().GetAwaiter().GetResult();

    public Task StartAsync()
    {
        if (_startup is not null) return _startup;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _startup = completed.Task;
        var worker = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                VoiceTrace.Write("Native init stage=enumeration");
                InitializeRecognizer();
                if (_disposed) return;
                completed.TrySetResult();
                Ready?.Invoke();
                Dispatcher.Run();
            }
            catch (Exception error)
            {
                VoiceTrace.Write("Native init failed: " + error.GetType().Name + "; " + error.Message);
                completed.TrySetException(error);
                if (!_disposed) Failed?.Invoke(error.Message);
            }
        }) { IsBackground = true, Name = "FRIDAY native speech STA" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return _startup;
    }

    private void InitializeRecognizer()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WakeWordListener));
        if (_engine is not null) return;

        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var selected = installed.FirstOrDefault(info =>
            info.Culture.Name.Equals("en-GB",
                StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(info =>
                info.Culture.TwoLetterISOLanguageName == "en");

        if (selected is null)
            throw new InvalidOperationException(
                "No compatible English speech recognizer is installed.");

        VoiceTrace.Write("Native init stage=engine; culture=" + selected.Culture.Name);
        var engine = new SpeechRecognitionEngine(selected);

        try
        {
            VoiceTrace.Write("Native init stage=grammars");
            var unit = new GrammarBuilder { Culture = selected.Culture };
            unit.Append(new Choices(
                "stop", "stop talking", "stop speaking",
                "please stop", "stop now"));

            var builder = new GrammarBuilder { Culture = selected.Culture };
            builder.Append(unit, 1, 6);

            engine.LoadGrammar(new Grammar(builder)
            {
                Name = "StopCommands"
            });

            var wakeBuilder = new GrammarBuilder { Culture = selected.Culture };
            wakeBuilder.Append(new Choices("hi", "hey", "hello", "friday",
                "hey friday", "hi friday", "hello friday"));
            engine.LoadGrammar(new Grammar(wakeBuilder) { Name = "WakeGreetings", Priority = 3 });

            engine.LoadGrammar(new DictationGrammar
            {
                Name = "BackgroundSpeech"
            });

            engine.InitialSilenceTimeout = TimeSpan.Zero;
            engine.BabbleTimeout = TimeSpan.Zero;
            engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(150);
            engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(250);

            engine.SpeechHypothesized += OnHypothesized;
            engine.SpeechRecognized += OnRecognized;
            engine.SpeechRecognitionRejected += OnRejected;
            engine.RecognizeCompleted += OnCompleted;

            // Use the exact same 16 kHz mono PCM as the main microphone.
            // No second default microphone connection is opened.
            VoiceTrace.Write("Native init stage=PCM input");
            engine.SetInputToAudioStream(
                _input,
                new SpeechAudioFormatInfo(
                    16000, AudioBitsPerSample.Sixteen,
                    AudioChannel.Mono));

            if (_disposed)
            {
                Detach(engine);
                engine.Dispose();
                return;
            }
            _engine = engine;
            VoiceTrace.Write("Native init stage=recognize start");
            engine.RecognizeAsync(RecognizeMode.Multiple);

            VoiceTrace.Write($"Local recognizer ready: {selected.Culture.Name}; wake + Stop grammars loaded.");
        }
        catch
        {
            Detach(engine);
            _input.Complete();
            engine.Dispose();
            _engine = null;
            throw;
        }
    }

    public void PushAudio(byte[] pcm)
    {
        if (!_disposed) _input.Push(pcm);
    }

    public void ListenForWake(bool enabled)
    {
        if (_disposed) return;
        int next = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _wakeArmed, next) != next)
            VoiceTrace.Write(enabled ? "Local wake ARMED." : "Local wake disarmed.");
    }

    public void Arm(bool enabled)
    {
        if (_disposed) return;

        int next = enabled ? 1 : 0;
        int previous = Interlocked.Exchange(ref _armed, next);

        if (previous != next)
            Console.WriteLine(enabled
                ? "[STOP] Armed for this reply."
                : "[STOP] Disarmed.");

        // Recognition stays alive. Only command acceptance is gated.
        // This avoids cancellation/restart races between speech turns.
    }

    private void OnHypothesized(
        object? sender, SpeechHypothesizedEventArgs e)
    {
        Inspect("Partial", e.Result, 0.30f);
    }

    private void OnRecognized(
        object? sender, SpeechRecognizedEventArgs e)
    {
        InspectGreeting(e.Result);
        Inspect("Final", e.Result, 0.45f);
    }

    private void InspectGreeting(RecognitionResult result)
    {
        if (_disposed || Volatile.Read(ref _wakeArmed) == 0) return;
        // The native engine can return the greeting alone or a full English sentence.
        // Use the same boundary rules as the conversation gate.
        var session = new Friday.Core.Conversation.ConversationSession(TimeSpan.Zero);
        if (!session.TryAccept(result.Text, out _)) return;
        VoiceTrace.Write($"Wake candidate grammar={result.Grammar.Name}; confidence={result.Confidence:F2}.");
        float threshold = result.Grammar.Name == "WakeGreetings" ? 0.40f : 0.55f;
        if (result.Confidence < threshold) return;
        if (Interlocked.CompareExchange(ref _wakeArmed, 0, 1) != 1) return;
        VoiceTrace.Write($"WAKE detected locally, confidence={result.Confidence:F2}.");
        GreetingDetected?.Invoke(result.Text);
    }

    private void Inspect(
        string stage, RecognitionResult result, float threshold)
    {
        if (_disposed || Volatile.Read(ref _armed) == 0) return;

        string[] words = Regex.Matches(
                result.Text.ToLowerInvariant(), @"[a-z]+")
            .Cast<Match>()
            .Select(match => match.Value)
            .ToArray();

        bool stopCommand = words.Contains("stop") &&
            words.All(word => word is
                "stop" or "please" or "talking" or "speaking" or "now");

        Console.WriteLine(
            $"[STOP] {stage}: {result.Text}; confidence={result.Confidence:F2}");

        if (!stopCommand || result.Confidence < threshold) return;

        if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            Console.WriteLine("[STOP] DETECTED.");
            WakeDetected?.Invoke();
        }
    }

    private void OnRejected(
        object? sender, SpeechRecognitionRejectedEventArgs e)
    {
        if (!_disposed && Volatile.Read(ref _wakeArmed) == 1)
            VoiceTrace.Write("Local wake heard speech but rejected recognition.");
        if (!_disposed && Volatile.Read(ref _armed) == 1)
            Console.WriteLine("[STOP] Speech heard but recognition rejected.");
    }

    private void OnCompleted(
        object? sender, RecognizeCompletedEventArgs e)
    {
        if (_disposed) return;

        if (e.Error is not null)
        {
            Arm(false);
            Console.WriteLine($"[STOP] Error: {e.Error.Message}");
            Failed?.Invoke(e.Error.Message);
            return;
        }

        if (_dispatcher is { HasShutdownStarted: false })
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || _engine is null) return;

                try
                {
                    _engine.RecognizeAsync(RecognizeMode.Multiple);
                    Console.WriteLine("[STOP] Recognition restarted.");
                }
                catch (Exception exception)
                {
                    Arm(false);
                    Failed?.Invoke(exception.Message);
                }
            }));
        }
    }

    private void Detach(SpeechRecognitionEngine engine)
    {
        engine.SpeechHypothesized -= OnHypothesized;
        engine.SpeechRecognized -= OnRecognized;
        engine.SpeechRecognitionRejected -= OnRejected;
        engine.RecognizeCompleted -= OnCompleted;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Interlocked.Exchange(ref _armed, 0);
        Interlocked.Exchange(ref _wakeArmed, 0);
        _input.Complete();

        void Cleanup()
        {
            var engine = _engine;
            _engine = null;
            if (engine is not null)
            {
                Detach(engine);
                try { engine.RecognizeAsyncCancel(); }
                finally { engine.Dispose(); }
            }
            if (_dispatcher is { HasShutdownStarted: false })
                _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
        if (_dispatcher is { HasShutdownStarted: false })
            _dispatcher.BeginInvoke(new Action(Cleanup));
        _input.Dispose();
    }

    private sealed class PcmInputStream : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new(20);
        private byte[]? _current;
        private int _offset;
        private volatile bool _finished;

        public void Push(byte[] data)
        {
            if (_finished || data.Length == 0) return;

            try
            {
                if (!_chunks.TryAdd(data))
                {
                    // Keep the feed bounded instead of building up latency.
                    _chunks.TryTake(out _);
                    _chunks.TryAdd(data);
                }
            }
            catch (InvalidOperationException) { }
        }

        public void Complete()
        {
            _finished = true;
            _chunks.CompleteAdding();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            if (_current is null || _offset >= _current.Length)
            {
                try
                {
                    _current = _chunks.Take();
                    _offset = 0;
                }
                catch (InvalidOperationException) { return 0; }
            }
            int copied = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, copied);
            _offset += copied;
            return copied;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Complete();
            base.Dispose(disposing);
        }
    }
}
