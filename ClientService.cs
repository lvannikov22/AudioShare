using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Concentus;

namespace AudioShare;

public sealed class ClientService : IDisposable
{
    public event Action<string>? Log, Status;
    public event Action<string>? ClipText;
    public event Action<byte[]>? ClipImage;
    public event Action? Disconnected;

    readonly CancellationTokenSource _cts = new();
    readonly short[] _pcm = new short[Fmt.FrameShorts];
    Peer? _peer;
    UdpClient? _udp;
    AudioPlayer? _player;
    IOpusDecoder? _dec;
    IPAddress _hostIp = IPAddress.None;
    int _bufferMs;
    int _lost;
    bool _disposed;

    public async Task ConnectAsync(IPAddress ip, string code, string outDeviceId, int bufferMs)
    {
        _hostIp = ip;
        _bufferMs = bufferMs;

        var peer = await Peer.ConnectAsync(ip, code, _cts.Token);
        _peer = peer;

        _dec = OpusCodecFactory.CreateDecoder(Fmt.Rate, 2);
        _player = new AudioPlayer(Dev.Find(outDeviceId), bufferMs, 20);
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Ports.Audio));
        _udp.Client.ReceiveBufferSize = 1 << 20;

        peer.ClipText += t => ClipText?.Invoke(t);
        peer.ClipImage += b => ClipImage?.Invoke(b);
        peer.Closed += () => { if (!_disposed) Disconnected?.Invoke(); };

        _ = peer.RunAsync();
        _ = Task.Run(ReceiveLoop);
        _ = StatusLoop();
        await SendInfo();
        Log?.Invoke($"Подключено к {ip}");
    }

    Task SendInfo() =>
        _peer?.SendInfo(JsonSerializer.Serialize(new ClientInfo(Ports.Audio, _bufferMs))) ?? Task.CompletedTask;

    /// <summary>Изменить размер буфера на лету (хост узнает об этом и подстроит свою задержку).</summary>
    public async Task SetBuffer(int ms)
    {
        _bufferMs = ms;
        if (_player != null) _player.Jitter.TargetMs = ms;
        await SendInfo();
    }

    async Task ReceiveLoop()
    {
        bool first = true;
        uint expect = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var r = await _udp!.ReceiveAsync(_cts.Token);
                if (!r.RemoteEndPoint.Address.Equals(_hostIp) || r.Buffer.Length <= 4) continue;

                uint seq = BitConverter.ToUInt32(r.Buffer, 0);
                if (!first)
                {
                    int gap = (int)(seq - expect);
                    if (gap < 0) continue; // опоздавший пакет или дубликат — пропускаем
                    if (gap > 0 && gap <= 3)
                    {
                        for (int i = 0; i < gap; i++) { DecodeAndPlay(ReadOnlySpan<byte>.Empty); _lost++; } // PLC от Opus
                    }
                    else if (gap > 3) _lost += gap;
                }
                first = false;
                expect = seq + 1;
                DecodeAndPlay(r.Buffer.AsSpan(4));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!_disposed) Log?.Invoke("Ошибка приёма звука: " + ex.Message); }
    }

    void DecodeAndPlay(ReadOnlySpan<byte> data)
    {
        int n = _dec!.Decode(data, _pcm.AsSpan(), Fmt.FrameSamples, false);
        if (n <= 0) return;
        var bytes = new byte[n * 4];
        Buffer.BlockCopy(_pcm, 0, bytes, 0, bytes.Length);
        _player!.Jitter.Add(bytes);
    }

    async Task StatusLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token);
                Status?.Invoke($"Подключено к {_hostIp} | буфер {_player?.Jitter.BufferedMs} мс | потеряно пакетов: {_lost}");
            }
        }
        catch { }
    }

    public Task SendClipText(string t) => _peer?.SendClipText(t) ?? Task.CompletedTask;
    public Task SendClipImage(byte[] png) => _peer?.SendClipImage(png) ?? Task.CompletedTask;

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _peer?.Dispose();
        try { _udp?.Dispose(); } catch { }
        _player?.Dispose();
    }
}
