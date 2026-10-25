using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AudioShare;

public sealed class HostSettings
{
    public string CaptureDeviceId { get; set; } = "";   // "" = устройство по умолчанию
    public bool SyncLocal { get; set; }                  // true = хост задерживает собственный звук
    public string LocalOutDeviceId { get; set; } = "";
    public int ExtraDelayMs { get; set; }
    public int BitrateKbps { get; set; } = 128;
}

public sealed class HostService : IDisposable
{
    public event Action<string>? Log, Status;
    public event Action<string>? ClipText;
    public event Action<byte[]>? ClipImage;

    /// <summary>Ручная подстройка задержки хоста, мс (можно менять на лету).</summary>
    public int ExtraDelayMs { get; set; }
    public string Code { get; }

    readonly HostSettings _s;
    readonly CancellationTokenSource _cts = new();
    readonly TcpListener _listener = new(IPAddress.Any, Ports.Control);
    readonly UdpClient _udp = new();
    Peer? _peer;
    IPEndPoint? _dest;
    LoopbackSource? _src;
    OpusEnc? _enc;
    AudioPlayer? _local;
    uint _seq;
    int _clientBuf = 40;
    bool _disposed;

    public HostService(HostSettings s, string code)
    {
        _s = s;
        Code = code;
        ExtraDelayMs = s.ExtraDelayMs;
    }

    public void Start()
    {
        _listener.Start();
        _ = Discovery.RunHostAsync(Environment.MachineName, _cts.Token);
        _ = AcceptLoop();
        _ = StatusLoop();
        Log?.Invoke($"Хост запущен. Код подключения: {Code}. Жду клиента…");
    }

    async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(tcp));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) Log?.Invoke("Ошибка сервера: " + ex.Message); }
    }

    async Task HandleAsync(TcpClient tcp)
    {
        try
        {
            if (_peer != null) { tcp.Dispose(); return; } // один клиент за раз
            var remote = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            var peer = await Peer.AcceptAsync(tcp, Code, _cts.Token);
            if (peer == null)
            {
                Log?.Invoke($"{remote}: неверный код, отключён");
                tcp.Dispose();
                return;
            }
            _peer = peer;
            peer.Info += OnClientInfo;
            peer.ClipText += t => ClipText?.Invoke(t);
            peer.ClipImage += b => ClipImage?.Invoke(b);
            peer.Closed += OnClosed;
            Log?.Invoke($"Клиент подключён: {remote}");
            _ = peer.PingLoopAsync();
            await peer.RunAsync();
        }
        catch (Exception ex)
        {
            if (!_disposed) Log?.Invoke("Ошибка подключения: " + ex.Message);
            try { tcp.Dispose(); } catch { }
        }
    }

    void OnClientInfo(string json)
    {
        var info = JsonSerializer.Deserialize<ClientInfo>(json);
        var peer = _peer;
        if (info == null || peer == null) return;
        _clientBuf = info.BufferMs;
        _dest = new IPEndPoint(peer.RemoteAddress, info.UdpPort);
        if (_src == null) StartAudio();
        UpdateDelay();
    }

    void StartAudio()
    {
        try
        {
            var capDev = Dev.Find(_s.CaptureDeviceId);
            _enc = new OpusEnc(_s.BitrateKbps * 1000);

            if (_s.SyncLocal)
            {
                var outDev = Dev.Find(_s.LocalOutDeviceId);
                if (outDev.ID == capDev.ID)
                {
                    Log?.Invoke("⚠ Устройство захвата и устройство вывода хоста совпадают — локальное воспроизведение отключено (иначе получится петля). Выбери виртуальный кабель для захвата.");
                }
                else
                {
                    _local = new AudioPlayer(outDev, ComputeDelay(), 20);
                    _local.Jitter.SlackMs = 40;
                    _local.Jitter.GrowWithSilence = true;
                    Log?.Invoke($"Звук на хосте играет на «{outDev.FriendlyName}» с задержкой под клиента");
                }
            }

            _src = new LoopbackSource(capDev);
            _src.Frame += OnFrame;
            _src.Start();
            Log?.Invoke($"Транслирую звук устройства: {capDev.FriendlyName}");
        }
        catch (Exception ex)
        {
            Log?.Invoke("Ошибка аудио: " + ex.Message);
        }
    }

    // Вызывается из потока захвата WASAPI каждые 10 мс
    void OnFrame(short[] pcm)
    {
        var dest = _dest;
        var enc = _enc;
        if (dest != null && enc != null)
        {
            var pkt = new byte[4 + 1500];
            BitConverter.TryWriteBytes(pkt.AsSpan(0, 4), _seq++);
            int n = enc.Encode(pcm, pkt.AsSpan(4));
            try { _udp.Send(pkt, 4 + n, dest); } catch { }
        }

        var local = _local;
        if (local != null)
        {
            var b = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, b, 0, b.Length);
            local.Jitter.Add(b);
        }
    }

    /// <summary>
    /// Задержка локального звука хоста = половина RTT (сеть) + буфер клиента + ручная подстройка.
    /// Задержка вывода у обоих ПК считается одинаковой (оба играют через WASAPI 20 мс) и сокращается.
    /// </summary>
    int ComputeDelay()
    {
        double rtt = _peer?.RttMs ?? 0;
        return Math.Max(0, (int)Math.Round(rtt / 2) + _clientBuf + ExtraDelayMs);
    }

    void UpdateDelay()
    {
        var local = _local;
        if (local != null) local.Jitter.TargetMs = ComputeDelay();
    }

    async Task StatusLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token);
                var peer = _peer;
                if (peer == null) { Status?.Invoke("Ожидание клиента…"); continue; }
                UpdateDelay();
                var s = $"Клиент {peer.RemoteAddress} | RTT {peer.RttMs:F1} мс";
                if (_local != null) s += $" | звук хоста задержан на {ComputeDelay()} мс";
                Status?.Invoke(s);
            }
        }
        catch { }
    }

    void OnClosed()
    {
        _peer = null;
        _dest = null;
        var src = _src; _src = null;
        var local = _local; _local = null;
        _enc = null;
        src?.Dispose();
        local?.Dispose();
        if (!_disposed) Log?.Invoke("Клиент отключился. Жду нового подключения…");
    }

    public Task SendClipText(string t) => _peer?.SendClipText(t) ?? Task.CompletedTask;
    public Task SendClipImage(byte[] png) => _peer?.SendClipImage(png) ?? Task.CompletedTask;

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _peer?.Dispose();
        OnClosed();
        _udp.Dispose();
    }
}
