using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioShare;

/// <summary>Следит за локальным буфером обмена и применяет пришедший от другого ПК. Всё вызывается из UI-потока.</summary>
public sealed class ClipboardSync
{
    public bool Enabled { get; set; } = true;
    public Func<string, Task>? SendText { get; set; }
    public Func<byte[], Task>? SendImage { get; set; }

    string _lastHash = "";
    long _suppressUntil;

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    /// <summary>Вызывать по WM_CLIPBOARDUPDATE.</summary>
    public void OnLocalChanged()
    {
        // Окно подавления нужно, чтобы не отправлять обратно то, что мы сами только что записали
        if (!Enabled || Environment.TickCount64 < _suppressUntil) return;
        try
        {
            if (Clipboard.ContainsText())
            {
                var t = Clipboard.GetText();
                if (string.IsNullOrEmpty(t) || t.Length > 5_000_000) return;
                var h = Hash(Encoding.UTF8.GetBytes(t));
                if (h == _lastHash) return;
                _lastHash = h;
                SendText?.Invoke(t);
            }
            else if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                if (img == null) return;
                using var ms = new MemoryStream();
                img.Save(ms, ImageFormat.Png);
                var png = ms.ToArray();
                if (png.Length > 30_000_000) return;
                var h = Hash(png);
                if (h == _lastHash) return;
                _lastHash = h;
                SendImage?.Invoke(png);
            }
        }
        catch (ExternalException) { /* буфер занят другим приложением */ }
    }

    public void SetText(string t)
    {
        if (!Enabled || string.IsNullOrEmpty(t)) return;
        _suppressUntil = Environment.TickCount64 + 700;
        _lastHash = Hash(Encoding.UTF8.GetBytes(t));
        Retry(() => Clipboard.SetText(t));
    }

    public void SetImage(byte[] png)
    {
        if (!Enabled) return;
        _suppressUntil = Environment.TickCount64 + 700;
        _lastHash = Hash(png);
        try
        {
            using var ms = new MemoryStream(png);
            using var img = Image.FromStream(ms);
            Retry(() => Clipboard.SetImage(img));
        }
        catch { }
    }

    static void Retry(Action a)
    {
        for (int i = 0; i < 6; i++)
        {
            try { a(); return; }
            catch (ExternalException) { Thread.Sleep(40); }
        }
    }
}

public sealed class Settings
{
    public bool IsHost { get; set; } = true;
    public string HostCode { get; set; } = "";
    public string CaptureDevice { get; set; } = "";
    public bool Sync { get; set; }
    public string LocalOutDevice { get; set; } = "";
    public int ExtraMs { get; set; }
    public int Bitrate { get; set; } = 128;
    public string HostAddress { get; set; } = "";
    public string Code { get; set; } = "";
    public string ClientOutDevice { get; set; } = "";
    public int BufferMs { get; set; } = 40;
    public bool SyncClipboard { get; set; } = true;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioShare", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
