using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AudioShare;

public static class Ports
{
    public const int Control = 48000;   // TCP: хост слушает (авторизация, буфер обмена, пинг)
    public const int Audio = 48001;     // UDP: клиент слушает (звук)
    public const int Discovery = 48002; // UDP: хост отвечает на широковещательный поиск
}

public enum Msg : byte { Challenge = 1, Auth = 2, AuthOk = 3, AuthFail = 4, Info = 5, Ping = 6, Pong = 7, ClipText = 8, ClipImage = 9 }

public sealed record ClientInfo(int UdpPort, int BufferMs);

/// <summary>Кадры по TCP: [тип 1 байт][длина 4 байта][данные].</summary>
public sealed class Framed
{
    readonly NetworkStream _s;
    readonly SemaphoreSlim _w = new(1, 1);
    public Framed(NetworkStream s) { _s = s; }

    public async Task SendAsync(Msg type, byte[] payload, CancellationToken ct = default)
    {
        var hdr = new byte[5];
        hdr[0] = (byte)type;
        BitConverter.TryWriteBytes(hdr.AsSpan(1), payload.Length);
        await _w.WaitAsync(ct);
        try
        {
            await _s.WriteAsync(hdr, ct);
            await _s.WriteAsync(payload, ct);
        }
        finally { _w.Release(); }
    }

    public async Task<(Msg type, byte[] data)> ReadAsync(CancellationToken ct)
    {
        var hdr = new byte[5];
        await _s.ReadExactlyAsync(hdr, ct);
        int len = BitConverter.ToInt32(hdr, 1);
        if (len < 0 || len > 64 * 1024 * 1024) throw new IOException("Некорректный кадр");
        var data = new byte[len];
        await _s.ReadExactlyAsync(data, ct);
        return ((Msg)hdr[0], data);
    }
}

public static class Crypto
{
    public static byte[] DeriveKey(string code) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes("AudioShare.v1"),
            100_000, HashAlgorithmName.SHA256, 32);

    public static byte[] Proof(byte[] key, byte[] nonce) => HMACSHA256.HashData(key, nonce);

    public static byte[] Encrypt(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var res = new byte[28 + cipher.Length];
        nonce.CopyTo(res, 0);
        tag.CopyTo(res, 12);
        cipher.CopyTo(res, 28);
        return res;
    }

    public static byte[] Decrypt(byte[] key, byte[] data)
    {
        var nonce = data.AsSpan(0, 12);
        var tag = data.AsSpan(12, 16);
        var cipher = data.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}

public static class Discovery
{
    public static async Task RunHostAsync(string name, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Ports.Discovery));
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(r.Buffer) == "AUDIOSHARE?")
                {
                    var reply = Encoding.UTF8.GetBytes("AUDIOSHARE!|" + name);
                    await udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
                }
            }
        }
        catch { /* отмена или порт занят — поиск просто не будет работать, подключение по IP остаётся */ }
    }

    public static async Task<List<(string name, IPAddress ip)>> FindHostsAsync(TimeSpan wait)
    {
        var res = new List<(string name, IPAddress ip)>();
        using var udp = new UdpClient { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var q = Encoding.UTF8.GetBytes("AUDIOSHARE?");
        await udp.SendAsync(q, q.Length, new IPEndPoint(IPAddress.Broadcast, Ports.Discovery));
        using var cts = new CancellationTokenSource(wait);
        try
        {
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var s = Encoding.UTF8.GetString(r.Buffer);
                if (s.StartsWith("AUDIOSHARE!|") && !res.Any(x => x.ip.Equals(r.RemoteEndPoint.Address)))
                    res.Add((s[12..], r.RemoteEndPoint.Address));
            }
        }
        catch (OperationCanceledException) { }
        return res;
    }
}

/// <summary>Управляющее TCP-соединение: авторизация по коду, пинг (RTT), буфер обмена, служебная информация.</summary>
public sealed class Peer : IDisposable
{
    readonly TcpClient _tcp;
    readonly Framed _io;
    readonly byte[] _key;
    readonly CancellationTokenSource _cts = new();
    readonly List<double> _rtts = new();
    int _closed;

    public IPAddress RemoteAddress { get; }
    public double RttMs { get; private set; }

    public event Action<string>? ClipText;
    public event Action<byte[]>? ClipImage;
    public event Action<string>? Info;
    public event Action? Closed;

    Peer(TcpClient tcp, Framed io, byte[] key)
    {
        _tcp = tcp; _io = io; _key = key;
        RemoteAddress = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
    }

    public static async Task<Peer?> AcceptAsync(TcpClient tcp, string code, CancellationToken ct)
    {
        tcp.NoDelay = true;
        var io = new Framed(tcp.GetStream());
        var key = Crypto.DeriveKey(code);
        var nonce = RandomNumberGenerator.GetBytes(16);
        await io.SendAsync(Msg.Challenge, nonce, ct);
        var (t, d) = await io.ReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        if (t != Msg.Auth || !CryptographicOperations.FixedTimeEquals(d, Crypto.Proof(key, nonce)))
        {
            try { await io.SendAsync(Msg.AuthFail, Array.Empty<byte>(), ct); } catch { }
            return null;
        }
        await io.SendAsync(Msg.AuthOk, Array.Empty<byte>(), ct);
        return new Peer(tcp, io, key);
    }

    public static async Task<Peer> ConnectAsync(IPAddress ip, string code, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(ip, Ports.Control, ct);
            var io = new Framed(tcp.GetStream());
            var key = Crypto.DeriveKey(code);
            var (t, nonce) = await io.ReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (t != Msg.Challenge) throw new IOException("Неожиданный ответ хоста");
            await io.SendAsync(Msg.Auth, Crypto.Proof(key, nonce), ct);
            var (r, _) = await io.ReadAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            if (r != Msg.AuthOk) throw new UnauthorizedAccessException("Неверный код подключения");
            return new Peer(tcp, io, key);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (t, d) = await _io.ReadAsync(_cts.Token);
                switch (t)
                {
                    case Msg.Ping: await _io.SendAsync(Msg.Pong, d, _cts.Token); break;
                    case Msg.Pong: OnPong(d); break;
                    case Msg.Info: Info?.Invoke(Encoding.UTF8.GetString(d)); break;
                    case Msg.ClipText: ClipText?.Invoke(Encoding.UTF8.GetString(Crypto.Decrypt(_key, d))); break;
                    case Msg.ClipImage: ClipImage?.Invoke(Crypto.Decrypt(_key, d)); break;
                }
            }
        }
        catch { }
        finally
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke();
        }
    }

    /// <summary>Раз в секунду меряет RTT. Задержка сети считается как RTT/2 — часы ПК сверять не нужно.</summary>
    public async Task PingLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _io.SendAsync(Msg.Ping, BitConverter.GetBytes(Stopwatch.GetTimestamp()), _cts.Token);
                await Task.Delay(1000, _cts.Token);
            }
        }
        catch { }
    }

    void OnPong(byte[] d)
    {
        long sent = BitConverter.ToInt64(d, 0);
        double ms = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
        lock (_rtts)
        {
            _rtts.Add(ms);
            if (_rtts.Count > 9) _rtts.RemoveAt(0);
            var s = _rtts.OrderBy(x => x).ToArray();
            RttMs = s[s.Length / 2]; // медиана — устойчива к редким скачкам Wi-Fi
        }
    }

    public Task SendInfo(string json) => Safe(Msg.Info, Encoding.UTF8.GetBytes(json));
    public Task SendClipText(string text) => Safe(Msg.ClipText, Crypto.Encrypt(_key, Encoding.UTF8.GetBytes(text)));
    public Task SendClipImage(byte[] png) => Safe(Msg.ClipImage, Crypto.Encrypt(_key, png));

    async Task Safe(Msg t, byte[] payload)
    {
        try { await _io.SendAsync(t, payload, _cts.Token); } catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _tcp.Close(); } catch { }
    }
}
