using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;

namespace Friday.Desktop.Voice;

public sealed class LiveMicrophoneService : IDisposable
{
    private readonly AdaptiveSpeechGate _speechGate = new();
    private const double EndSilenceMs = 500;
    private const double MinimumSpeechMs = 150;
    private const double MaximumTurnMs = 12000;
    private const double PreRollMs = 600;

    private sealed record AudioFrame(
        byte[] Bytes,
        double DurationMs,
        bool IsSpeech);

    private readonly object _sync = new();
    private readonly MemoryStream _segment = new();
    private readonly Queue<AudioFrame> _preRoll = new();

    private WaveInEvent? _capture;

    private bool _paused = true;
    private bool _disposed;
    private bool _active;
    private long _lastDiagnostic;
    private long _speechStartedTicks;
    public DateTimeOffset SpeechStartedAt => new(
        System.Threading.Interlocked.Read(ref _speechStartedTicks), TimeSpan.Zero);

    public bool HasActiveSpeech { get { lock (_sync) return _active && !_paused; } }

    private double _preRollDuration;
    private double _silenceMs;
    private double _speechMs;
    private double _totalMs;

    public event Action<byte[]>? PcmAvailable;
    public event Action<byte[]>? UtteranceReady;
    public event Action<string>? Failed;

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                nameof(LiveMicrophoneService));
        }

        if (_capture is not null)
        {
            Resume();
            return;
        }

        if (WaveIn.DeviceCount == 0)
        {
            throw new InvalidOperationException(
                "No microphone found.");
        }

        // -1 is the Windows default input mapper; 0 may be an unrelated device.
        int device = int.TryParse(Environment.GetEnvironmentVariable("FRIDAY_MIC_DEVICE"), out int configured)
            && configured >= -1 && configured < WaveIn.DeviceCount ? configured : -1;
        VoiceTrace.Write($"Starting microphone: selected={device}; device count={WaveIn.DeviceCount}.");
        var capture = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        _capture = capture;
        Resume();

        try
        {
            capture.StartRecording();
        }
        catch
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();

            _capture = null;
            Pause();

            throw;
        }
    }

    public void SuspendCapture()
    {
        WaveInEvent? capture;
        lock (_sync)
        {
            _paused = true;
            ResetSegment();
            _preRoll.Clear();
            _preRollDuration = 0;
            capture = _capture;
            _capture = null;
        }
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); }
            finally { capture.Dispose(); }
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _paused = true;
            ResetSegment();
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (!_paused) return; // A status refresh must not erase an in-progress word.
            ResetSegment();
            _paused = false;
        }
    }

    public void ResumeFromWake()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ResetSegment();

            // Include audio immediately preceding keyword detection.
            foreach (AudioFrame frame in _preRoll)
            {
                _segment.Write(
                    frame.Bytes,
                    0,
                    frame.Bytes.Length);

                _totalMs += frame.DurationMs;

                if (frame.IsSpeech)
                {
                    _speechMs += frame.DurationMs;
                }
            }

            _active = true;
            _paused = false;
            _silenceMs = 0;
        }
    }

    private void OnDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _capture)) return;

        // Feed local recognition even while turn segmentation is paused.
        byte[] livePcm = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, livePcm, 0, e.BytesRecorded);
        PcmAvailable?.Invoke(livePcm);

        byte[]? completedAudio = null;

        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(sender, _capture))
            {
                return;
            }

            byte[] bytes = new byte[e.BytesRecorded];
            Buffer.BlockCopy(
                e.Buffer,
                0,
                bytes,
                0,
                e.BytesRecorded);

            double sum = 0;
            int samples = bytes.Length / 2;

            for (int i = 0; i < samples; i++)
            {
                short sample = BitConverter.ToInt16(bytes, i * 2);
                double normalized = sample / 32768.0;

                sum += normalized * normalized;
            }

            double rms = samples == 0
                ? 0
                : Math.Sqrt(sum / samples);

            long now = Environment.TickCount64;
            if (now - _lastDiagnostic >= 5000)
            {
                _lastDiagnostic = now;
                VoiceTrace.Write($"MIC RMS={rms:F4}; speech threshold={_speechGate.Threshold:F4}; paused={_paused}; active={_active}.");
            }
            // Capture is paused during FRIDAY's own playback. Its speaker output must
            // not become the next question's learned microphone noise floor.
            bool speech = _speechGate.IsSpeech(rms, _active, learn: !_paused);
            double frameMs = bytes.Length / 32000.0 * 1000;

            _preRoll.Enqueue(
                new AudioFrame(bytes, frameMs, speech));

            _preRollDuration += frameMs;

            while (_preRollDuration > PreRollMs &&
                   _preRoll.Count > 1)
            {
                _preRollDuration -=
                    _preRoll.Dequeue().DurationMs;
            }

            if (_paused)
            {
                return;
            }

            if (!_active)
            {
                if (!speech)
                {
                    return;
                }

                _active = true;
                System.Threading.Interlocked.Exchange(ref _speechStartedTicks, DateTimeOffset.UtcNow.UtcTicks);
                VoiceTrace.Write("Utterance started.");
                // Preserve the quiet beginning of a word instead of clipping its first consonant.
                foreach (var frame in _preRoll.Take(_preRoll.Count - 1))
                {
                    _segment.Write(frame.Bytes, 0, frame.Bytes.Length);
                    _totalMs += frame.DurationMs;
                }
            }

            _segment.Write(bytes, 0, bytes.Length);
            _totalMs += frameMs;

            if (speech)
            {
                _speechMs += frameMs;
                _silenceMs = 0;
            }
            else
            {
                _silenceMs += frameMs;
            }

            if (_silenceMs < EndSilenceMs &&
                _totalMs < MaximumTurnMs)
            {
                return;
            }

            if (_speechMs >= MinimumSpeechMs)
            {
                using var wav = new MemoryStream();

                using (var writer = new WaveFileWriter(
                    wav,
                    new WaveFormat(16000, 16, 1)))
                {
                    byte[] pcm = _segment.ToArray();
                    writer.Write(pcm, 0, pcm.Length);
                }

                completedAudio = wav.ToArray();
                VoiceTrace.Write($"Utterance completed: duration_ms={_totalMs:F0}; voiced_ms={_speechMs:F0}; endpoint={(_silenceMs >= EndSilenceMs ? "silence" : "length limit")}.");
                _paused = true;
            }

            ResetSegment();
        }

        if (completedAudio is not null)
        {
            UtteranceReady?.Invoke(completedAudio);
        }
    }

    private void OnRecordingStopped(
        object? sender,
        StoppedEventArgs e)
    {
        if (e.Exception is not null && !_disposed && ReferenceEquals(sender, _capture))
        {
            Pause();
            Failed?.Invoke(e.Exception.Message);
        }
    }

    private void ResetSegment()
    {
        _segment.SetLength(0);
        _segment.Position = 0;

        _active = false;
        _silenceMs = 0;
        _speechMs = 0;
        _totalMs = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        SuspendCapture();
        lock (_sync)
        {
            _disposed = true;
            _segment.Dispose();
        }
    }
}


// Technical health only: never writes audio, transcripts, API keys or conversation text.
internal static class VoiceTrace
{
    private static readonly object Sync = new();
    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                string directory = Path.GetDirectoryName(
                    Friday.Core.Preferences.PreferencePaths.GetDatabasePath())!;
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "voice-diagnostics.log");
                if (File.Exists(path) && new FileInfo(path).Length > 262144)
                    File.WriteAllText(path, "");
                string line = DateTimeOffset.Now.ToString("HH:mm:ss") + " " + message;
                Console.WriteLine(line);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* Diagnostics must not stop microphone capture. */ }
    }
}


// Background noise from the real log exceeds the old fixed 0.006 threshold.
// Learn a lower percentile while idle, then freeze the threshold during each utterance.
internal sealed class AdaptiveSpeechGate
{
    private readonly Queue<double> _recent = new();
    public double Threshold { get; private set; } = 0.025;
    public bool IsSpeech(double rms, bool active, bool learn = true)
    {
        if (!active && learn)
        {
            _recent.Enqueue(rms);
            while (_recent.Count > 60) _recent.Dequeue();
            if (_recent.Count >= 10)
            {
                var values = _recent.OrderBy(v => v).ToArray();
                double noise = values[(int)((values.Length - 1) * 0.35)];
                Threshold = Math.Clamp(noise * 2.8, 0.018, 0.12);
            }
        }
        return rms >= (active ? Threshold * 0.70 : Threshold);
    }
}
