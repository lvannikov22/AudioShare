using Concentus;
using Concentus.Enums;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioShare;

/// <summary>Единый формат внутри программы: 48 кГц, 16 бит, стерео, кадры по 10 мс.</summary>
public static class Fmt
{
    public const int Rate = 48000;
    public const int FrameSamples = 480;           // на канал (10 мс)
    public const int FrameShorts = FrameSamples * 2;
    public const int BytesPerMs = 192;             // 48000 * 2 канала * 2 байта / 1000
}

public static class Dev
{
    public static List<(string id, string name)> List()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                 .Select(d => (d.ID, d.FriendlyName)).ToList();
    }

    public static MMDevice Find(string id)
    {
        var en = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try { return en.GetDevice(id); } catch { }
        }
        return en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
}

public sealed class OpusEnc
{
    readonly IOpusEncoder _e;
    public OpusEnc(int bitrate)
    {
        _e = OpusCodecFactory.CreateEncoder(Fmt.Rate, 2, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _e.Bitrate = bitrate;
    }
    public int Encode(short[] pcm, Span<byte> dst) => _e.Encode(pcm.AsSpan(), Fmt.FrameSamples, dst, dst.Length);
}

/// <summary>Берёт первые два канала у многоканального устройства (5.1 / 7.1).</summary>
sealed class TakeStereo : ISampleProvider
{
    readonly ISampleProvider _src;
    readonly int _ch;
    float[] _tmp = Array.Empty<float>();
    public WaveFormat WaveFormat { get; }
    public TakeStereo(ISampleProvider src)
    {
        _src = src;
        _ch = src.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
    }
    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        if (_tmp.Length < frames * _ch) _tmp = new float[frames * _ch];
        int got = _src.Read(_tmp, 0, frames * _ch) / _ch;
        for (int i = 0; i < got; i++)
        {
            buffer[offset + 2 * i] = _tmp[i * _ch];
            buffer[offset + 2 * i + 1] = _tmp[i * _ch + 1];
        }
        return got * 2;
    }
}

/// <summary>Захват всего звука, который играет на выбранном устройстве вывода (WASAPI loopback), нарезка на кадры по 10 мс.</summary>
public sealed class LoopbackSource : IDisposable
{
    readonly WasapiLoopbackCapture _cap;
    readonly BufferedWaveProvider _buf;
    readonly ISampleProvider _sp;
    readonly float[] _acc = new float[Fmt.FrameShorts];
    int _filled;

    public event Action<short[]>? Frame;

    public LoopbackSource(MMDevice dev)
    {
        _cap = new WasapiLoopbackCapture(dev);
        _buf = new BufferedWaveProvider(_cap.WaveFormat)
        {
            ReadFully = false,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromMilliseconds(300)
        };
        ISampleProvider sp = _buf.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) sp = new TakeStereo(sp);
        if (sp.WaveFormat.SampleRate != Fmt.Rate) sp = new WdlResamplingSampleProvider(sp, Fmt.Rate);
        _sp = sp;
        _cap.DataAvailable += OnData;
    }

    public void Start() => _cap.StartRecording();

    void OnData(object? s, WaveInEventArgs e)
    {
        _buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
        int n;
        while ((n = _sp.Read(_acc, _filled, _acc.Length - _filled)) > 0)
        {
            _filled += n;
            if (_filled < _acc.Length) continue;
            var pcm = new short[_acc.Length];
            for (int i = 0; i < pcm.Length; i++)
                pcm[i] = (short)Math.Clamp(_acc[i] * 32767f, -32768f, 32767f);
            _filled = 0;
            Frame?.Invoke(pcm);
        }
    }

    public void Dispose()
    {
        try { _cap.StopRecording(); } catch { }
        _cap.Dispose();
    }
}

/// <summary>
/// Очередь PCM-кадров с «целевым» уровнем заполнения.
/// Воспроизведение начинается, когда накоплено TargetMs; при опустошении — снова копит.
/// Используется и как джиттер-буфер клиента, и как линия задержки на хосте.
/// </summary>
public sealed class JitterBuffer : IWaveProvider
{
    readonly Queue<byte[]> _q = new();
    readonly object _l = new();
    int _bytes;
    bool _playing;
    byte[]? _cur;
    int _curPos;

    public WaveFormat WaveFormat { get; } = new(Fmt.Rate, 16, 2);
    public int TargetMs { get; set; }
    /// <summary>Сколько мс сверх цели можно накопить, прежде чем старые кадры начнут выбрасываться.</summary>
    public int SlackMs { get; set; } = 100;
    /// <summary>Если уровень упал ниже цели — подкладывать тишину (для линии задержки на хосте).</summary>
    public bool GrowWithSilence { get; set; }

    public int BufferedMs { get { lock (_l) return _bytes / Fmt.BytesPerMs; } }

    public void Add(byte[] pcm)
    {
        lock (_l)
        {
            if (GrowWithSilence && _playing)
                while (_bytes / Fmt.BytesPerMs < TargetMs - SlackMs)
                {
                    _q.Enqueue(new byte[pcm.Length]);
                    _bytes += pcm.Length;
                }
            _q.Enqueue(pcm);
            _bytes += pcm.Length;
            while (_bytes / Fmt.BytesPerMs > TargetMs + SlackMs && _q.Count > 1)
                _bytes -= _q.Dequeue().Length;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_l)
        {
            if (!_playing && _bytes / Fmt.BytesPerMs >= TargetMs) _playing = true;
            int written = 0;
            if (_playing)
            {
                while (written < count)
                {
                    if (_cur == null)
                    {
                        if (_q.Count == 0) break;
                        _cur = _q.Dequeue();
                        _curPos = 0;
                    }
                    int n = Math.Min(count - written, _cur.Length - _curPos);
                    Buffer.BlockCopy(_cur, _curPos, buffer, offset + written, n);
                    _curPos += n;
                    written += n;
                    _bytes -= n;
                    if (_curPos >= _cur.Length) _cur = null;
                }
                if (written < count) _playing = false; // underrun → снова накопить буфер
            }
            Array.Clear(buffer, offset + written, count - written);
            return count;
        }
    }
}

/// <summary>Вывод JitterBuffer на выбранное устройство (WASAPI shared, event-driven, малая задержка).</summary>
public sealed class AudioPlayer : IDisposable
{
    readonly WasapiOut _out;
    public JitterBuffer Jitter { get; }

    public AudioPlayer(MMDevice device, int targetMs, int latencyMs = 20)
    {
        Jitter = new JitterBuffer { TargetMs = targetMs };
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        _out.Init(Jitter);
        _out.Play();
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
