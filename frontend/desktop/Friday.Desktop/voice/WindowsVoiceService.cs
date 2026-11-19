using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.Wave;

namespace Friday.Desktop.Voice;

public sealed class WindowsVoiceService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BytesPerSecond = SampleRate * 2;
    private const int BarCount = 32;

    private sealed record SpeechMark(
        TimeSpan AudioPosition,
        int CharacterPosition,
        int CharacterCount);

    private readonly object _marksLock = new();
    private readonly List<SpeechMark> _marks = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly DispatcherTimer _clock;

    private SpeechSynthesizer? _synthesizer;
    private Prompt? _currentPrompt;
    private TaskCompletionSource<bool>? _renderFinished;
    private TaskCompletionSource<bool>? _playbackFinished;

    private WaveOutEvent? _player;
    private byte[]? _pcm;
    private SpeechMark[] _playbackMarks = Array.Empty<SpeechMark>();

    private int _lastMark = -1;
    private int _version;
    private bool _disposed;
    private bool _synthesisFaulted;
    private readonly ConcurrentDictionary<Prompt, RenderBuffer> _renderBuffers = new();

    public event Action<int, int>? SpeechProgress;
    public event Action<float[]>? WaveformChanged;

    public string? SelectedVoiceName { get; private set; }

    public WindowsVoiceService()
    {
        _clock = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _clock.Tick += OnPlaybackTick;

        SpeechSynthesizer? synthesizer = null;

        try
        {
            synthesizer = new SpeechSynthesizer();

            var voices = synthesizer.GetInstalledVoices()
                .Where(v => v.Enabled)
                .Select(v => v.VoiceInfo)
                .ToList();

            var selected = voices.FirstOrDefault(v =>
                    IsIndianEnglish(v) && v.Gender == VoiceGender.Female)
                ?? voices.FirstOrDefault(v =>
                    IsEnglish(v) && v.Gender == VoiceGender.Female)
                ?? voices.FirstOrDefault(IsIndianEnglish)
                ?? voices.FirstOrDefault(IsEnglish);

            if (selected is null)
                throw new InvalidOperationException(
                    "No compatible English voice is installed.");

            synthesizer.SelectVoice(selected.Name);
            synthesizer.Volume = 100;
            synthesizer.Rate = 0;
            synthesizer.SpeakProgress += OnSynthesisProgress;
            synthesizer.SpeakCompleted += OnSynthesisCompleted;

            _synthesizer = synthesizer;
            SelectedVoiceName = selected.Name;
        }
        catch (Exception exception)
        {
            synthesizer?.Dispose();
            Debug.WriteLine(exception.Message);
        }
    }

    private static bool IsIndianEnglish(VoiceInfo voice) =>
        voice.Culture.Name.Equals(
            "en-IN", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnglish(VoiceInfo voice) =>
        voice.Culture.TwoLetterISOLanguageName.Equals(
            "en", StringComparison.OrdinalIgnoreCase);

    public void SetSpeechRate(int rate)
    {
        if (rate < -10 || rate > 10)
            throw new ArgumentOutOfRangeException(nameof(rate));
        if (!_disposed && _synthesizer is not null)
            _synthesizer.Rate = rate;
    }

    public async Task<bool> SpeakAsync(string text)
    {
        Stop();
        int version = _version;

        if (_synthesisFaulted) return false;
        if (!await _serial.WaitAsync(TimeSpan.FromSeconds(3)))
        {
            VoiceTrace.Write("Speech serial wait timed out; input must remain usable.");
            return false;
        }

        try
        {
            if (_disposed || version != _version) return true;

            var synth = _synthesizer;
            if (synth is null || string.IsNullOrWhiteSpace(text))
                return false;

            using var renderStream = new RenderBuffer();

            lock (_marksLock)
            {
                _marks.Clear();
            }

            var rendered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _renderFinished = rendered;
            _currentPrompt = new Prompt(text);
            _renderBuffers[_currentPrompt] = renderStream;

            synth.SetOutputToAudioStream(
                renderStream,
                new SpeechAudioFormatInfo(
                    SampleRate,
                    AudioBitsPerSample.Sixteen,
                    AudioChannel.Mono));

            synth.SpeakAsync(_currentPrompt);

            bool renderSucceeded;
            try { renderSucceeded = await rendered.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException)
            {
                _synthesisFaulted = true; // Retain at most one native-writer buffer until it finishes.
                VoiceTrace.Write("Speech synthesis callback timed out. Text/input remain usable; restart to retry voice.");
                synth.SpeakAsyncCancelAll();
                return false;
            }

            if (_disposed || version != _version) return true;
            if (!renderSucceeded) return false;

            // Release the synthesis output before reading the finished PCM.
            synth.SetOutputToNull();

            byte[] pcm = renderStream.ToArray();
            if (pcm.Length == 0) return false;

            lock (_marksLock)
            {
                _playbackMarks = _marks
                    .OrderBy(mark => mark.AudioPosition)
                    .ToArray();
            }

            _pcm = pcm;
            _lastMark = -1;

            using var pcmStream = new MemoryStream(pcm, writable: false);
            using var source = new RawSourceWaveStream(
                pcmStream, new WaveFormat(SampleRate, 16, 1));
            using var player = new WaveOutEvent
            {
                DesiredLatency = 100
            };

            var played = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            player.PlaybackStopped += (_, e) =>
                played.TrySetResult(e.Exception is null);

            _player = player;
            _playbackFinished = played;

            player.Init(source);
            player.Play();

            _clock.Start();

            bool playbackSucceeded;
            try
            {
                playbackSucceeded = await played.Task.WaitAsync(
                    TimeSpan.FromSeconds(pcm.Length / (double)BytesPerSecond + 3));
            }
            catch (TimeoutException)
            {
                VoiceTrace.Write("Audio playback completion timed out; stopping output and releasing turn.");
                player.Stop();
                return false;
            }

            return version != _version || playbackSucceeded;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"FRIDAY voice error: {exception.Message}");
            return _disposed || version != _version;
        }
        finally
        {
            _clock.Stop();
            _player = null;
            _pcm = null;
            _playbackMarks = Array.Empty<SpeechMark>();
            _renderFinished = null;
            _playbackFinished = null;
            _currentPrompt = null;

            WaveformChanged?.Invoke(new float[BarCount]);
            _serial.Release();
        }
    }

    private void OnSynthesisProgress(
        object? sender, SpeakProgressEventArgs e)
    {
        if (_disposed || !ReferenceEquals(e.Prompt, _currentPrompt))
            return;

        lock (_marksLock)
        {
            _marks.Add(new SpeechMark(
                e.AudioPosition,
                e.CharacterPosition,
                e.CharacterCount));
        }
    }

    private void OnSynthesisCompleted(
        object? sender, SpeakCompletedEventArgs e)
    {
        if (_renderBuffers.TryRemove(e.Prompt, out var buffer)) buffer.WriterFinished();
        if (ReferenceEquals(e.Prompt, _currentPrompt))
            _renderFinished?.TrySetResult(
                e.Error is null && !e.Cancelled);
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        var player = _player;
        byte[]? pcm = _pcm;

        if (_disposed || player is null || pcm is null) return;

        try
        {
            long bytePosition = Math.Clamp(
                player.GetPosition(), 0L, (long)pcm.Length);

            TimeSpan position = TimeSpan.FromSeconds(
                bytePosition / (double)BytesPerSecond);

            int next = _lastMark;
            while (next + 1 < _playbackMarks.Length &&
                   _playbackMarks[next + 1].AudioPosition <= position)
            {
                next++;
            }

            if (next != _lastMark)
            {
                _lastMark = next;
                SpeechMark mark = _playbackMarks[next];

                SpeechProgress?.Invoke(
                    mark.CharacterPosition,
                    mark.CharacterCount);
            }

            // Each bar represents 4 ms of the most recently played PCM.
            const int samplesPerBar = 64;

            int endSample = (int)(bytePosition / 2);
            int firstSample = endSample - BarCount * samplesPerBar;

            var levels = new float[BarCount];

            for (int bar = 0; bar < BarCount; bar++)
            {
                double sum = 0;
                int count = 0;
                int start = firstSample + bar * samplesPerBar;

                for (int i = 0; i < samplesPerBar; i++)
                {
                    int sampleIndex = start + i;
                    if (sampleIndex < 0 || sampleIndex >= pcm.Length / 2)
                        continue;

                    short sample = BitConverter.ToInt16(
                        pcm, sampleIndex * 2);

                    double normalized = sample / 32768.0;
                    sum += normalized * normalized;
                    count++;
                }

                double rms = count == 0 ? 0 : Math.Sqrt(sum / count);

                // Compress the visual range so quiet speech stays visible.
                levels[bar] = (float)Math.Clamp(
                    Math.Sqrt(rms) * 1.8, 0, 1);
            }

            WaveformChanged?.Invoke(levels);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.Message);
        }
    }

    public void Stop()
    {
        if (_disposed) return;

        _version++;
        _clock.Stop();
        // Release the canceled turn immediately; a delayed native callback must
        // not occupy the speech queue and block the next request.
        _renderFinished?.TrySetResult(false);
        _playbackFinished?.TrySetResult(false);

        try
        {
            _synthesizer?.SpeakAsyncCancelAll();
        }
        catch (InvalidOperationException exception)
        {
            Debug.WriteLine(exception.Message);
        }

        try
        {
            _player?.Stop();
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception.Message);
        }

        WaveformChanged?.Invoke(new float[BarCount]);
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _disposed = true;

        _clock.Tick -= OnPlaybackTick;

        if (_synthesizer is not null)
        {
            _synthesizer.SpeakProgress -= OnSynthesisProgress;
            _synthesizer.SpeakCompleted -= OnSynthesisCompleted;
            _synthesizer.Dispose();
            _synthesizer = null;
        }

        foreach (var buffer in _renderBuffers.Values) buffer.WriterFinished();
        _renderBuffers.Clear();
        _renderFinished?.TrySetResult(false);
        _playbackFinished?.TrySetResult(false);
    }

    // A timed-out SAPI writer may complete late. Do not dispose its output underneath it.
    private sealed class RenderBuffer : MemoryStream
    {
        private readonly object _sync = new();
        private bool _finished, _disposeRequested;
        public void WriterFinished()
        {
            lock (_sync)
            {
                _finished = true;
                if (_disposeRequested) base.Dispose(true);
            }
        }
        protected override void Dispose(bool disposing)
        {
            lock (_sync)
            {
                _disposeRequested = true;
                if (_finished) base.Dispose(disposing);
            }
        }
    }

}
