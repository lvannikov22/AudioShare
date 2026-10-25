using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AudioShare;

public sealed class MainForm : Form
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    readonly Settings _cfg = Settings.Load();
    readonly ClipboardSync _clip = new();
    readonly List<(string id, string name)> _devs = Dev.List();
    HostService? _host;
    ClientService? _client;

    // общие
    readonly RadioButton _rbHost = new() { Text = "Хост (главный ПК)", AutoSize = true };
    readonly RadioButton _rbClient = new() { Text = "Клиент (подключается к хосту)", AutoSize = true };
    readonly CheckBox _cbClip = new() { Text = "Общий буфер обмена (текст и картинки)", AutoSize = true };
    readonly Label _status = new() { Text = "Не запущено", AutoSize = true };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 480, Height = 150 };

    // хост
    readonly GroupBox _gbHost = new() { Text = "Хост", Width = 490, Height = 400 };
    readonly Label _lblCode = new() { AutoSize = true, Font = new Font("Segoe UI", 20f, FontStyle.Bold) };
    readonly Label _lblIp = new() { AutoSize = true };
    readonly ComboBox _cbCapture = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 450 };
    readonly RadioButton _rbNoSync = new() { Text = "Не синхронизировать: хост играет как обычно, у клиента звук чуть позже", Width = 450, Height = 34 };
    readonly RadioButton _rbSync = new() { Text = "Синхронизировать: хост задерживает свой звук, чтобы совпадать с клиентом (нужен виртуальный кабель, см. README)", Width = 450, Height = 48 };
    readonly ComboBox _cbLocalOut = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 450 };
    readonly TrackBar _tbExtra = new() { Minimum = -50, Maximum = 200, TickFrequency = 25, Width = 280, Value = 0 };
    readonly Label _lblExtra = new() { AutoSize = true, Text = "0 мс" };
    readonly NumericUpDown _nudBitrate = new() { Minimum = 32, Maximum = 320, Increment = 16, Value = 128, Width = 70 };
    readonly Button _btnHost = new() { Text = "Запустить хост", Width = 180, Height = 32 };

    // клиент
    readonly GroupBox _gbClient = new() { Text = "Клиент", Width = 490, Height = 300 };
    readonly ComboBox _cbHost = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 340 };
    readonly Button _btnFind = new() { Text = "Найти хост", Width = 100 };
    readonly TextBox _txtCode = new() { Width = 120 };
    readonly ComboBox _cbOut = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 450 };
    readonly NumericUpDown _nudBuf = new() { Minimum = 10, Maximum = 300, Value = 40, Width = 70 };
    readonly Button _btnClient = new() { Text = "Подключиться", Width = 180, Height = 32 };

    public MainForm()
    {
        Text = "AudioShare — общий звук и буфер обмена";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(530, 760);
        MinimumSize = new Size(550, 500);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(10)
        };
        Controls.Add(root);

        root.Controls.Add(Row(_rbHost, _rbClient));
        root.Controls.Add(BuildHostGroup());
        root.Controls.Add(BuildClientGroup());
        root.Controls.Add(_cbClip);
        root.Controls.Add(_status);
        root.Controls.Add(_log);

        FillCombo(_cbCapture); FillCombo(_cbLocalOut); FillCombo(_cbOut);
        LoadSettings();
        WireEvents();
        UpdateRoleUi();
    }

    // ---------- построение интерфейса ----------

    static FlowLayoutPanel Row(params Control[] c)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(c);
        return p;
    }

    static Label L(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(0, 6, 0, 2) };

    GroupBox BuildHostGroup()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6) };
        f.Controls.Add(L("Код подключения (назови его клиенту):"));
        f.Controls.Add(_lblCode);
        f.Controls.Add(_lblIp);
        f.Controls.Add(L("Звук какого устройства транслировать:"));
        f.Controls.Add(_cbCapture);
        f.Controls.Add(_rbNoSync);
        f.Controls.Add(_rbSync);
        f.Controls.Add(L("Играть звук на хосте через (для режима синхронизации):"));
        f.Controls.Add(_cbLocalOut);
        f.Controls.Add(Row(L("Подстройка задержки хоста:"), _tbExtra, _lblExtra));
        f.Controls.Add(Row(L("Битрейт Opus, кбит/с:"), _nudBitrate));
        f.Controls.Add(_btnHost);
        _gbHost.Controls.Add(f);
        return _gbHost;
    }

    GroupBox BuildClientGroup()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6) };
        f.Controls.Add(L("Хост (IP-адрес):"));
        f.Controls.Add(Row(_cbHost, _btnFind));
        f.Controls.Add(L("Код подключения:"));
        f.Controls.Add(_txtCode);
        f.Controls.Add(L("Играть звук через:"));
        f.Controls.Add(_cbOut);
        f.Controls.Add(L("Буфер, мс (меньше = меньше задержка, но возможны щелчки):"));
        f.Controls.Add(_nudBuf);
        f.Controls.Add(_btnClient);
        _gbClient.Controls.Add(f);
        return _gbClient;
    }

    void FillCombo(ComboBox cb)
    {
        cb.Items.Add("(по умолчанию)");
        foreach (var d in _devs) cb.Items.Add(d.name);
        cb.SelectedIndex = 0;
    }

    string DevId(ComboBox cb) => cb.SelectedIndex <= 0 ? "" : _devs[cb.SelectedIndex - 1].id;

    void SelectDev(ComboBox cb, string id)
    {
        int i = _devs.FindIndex(d => d.id == id);
        cb.SelectedIndex = i < 0 ? 0 : i + 1;
    }

    // ---------- настройки ----------

    void LoadSettings()
    {
        if (string.IsNullOrEmpty(_cfg.HostCode))
            _cfg.HostCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        _rbHost.Checked = _cfg.IsHost;
        _rbClient.Checked = !_cfg.IsHost;
        _lblCode.Text = _cfg.HostCode;
        _lblIp.Text = "IP этого ПК: " + string.Join(", ",
            Dns.GetHostEntry(Dns.GetHostName()).AddressList
               .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()));
        SelectDev(_cbCapture, _cfg.CaptureDevice);
        SelectDev(_cbLocalOut, _cfg.LocalOutDevice);
        _rbSync.Checked = _cfg.Sync;
        _rbNoSync.Checked = !_cfg.Sync;
        _tbExtra.Value = Math.Clamp(_cfg.ExtraMs, _tbExtra.Minimum, _tbExtra.Maximum);
        _nudBitrate.Value = Math.Clamp(_cfg.Bitrate, (int)_nudBitrate.Minimum, (int)_nudBitrate.Maximum);
        _cbHost.Text = _cfg.HostAddress;
        _txtCode.Text = _cfg.Code;
        SelectDev(_cbOut, _cfg.ClientOutDevice);
        _nudBuf.Value = Math.Clamp(_cfg.BufferMs, (int)_nudBuf.Minimum, (int)_nudBuf.Maximum);
        _cbClip.Checked = _cfg.SyncClipboard;
        _clip.Enabled = _cfg.SyncClipboard;
        UpdateExtraLabel();
    }

    void SaveSettings()
    {
        _cfg.IsHost = _rbHost.Checked;
        _cfg.CaptureDevice = DevId(_cbCapture);
        _cfg.Sync = _rbSync.Checked;
        _cfg.LocalOutDevice = DevId(_cbLocalOut);
        _cfg.ExtraMs = _tbExtra.Value;
        _cfg.Bitrate = (int)_nudBitrate.Value;
        _cfg.HostAddress = ParseIp(_cbHost.Text);
        _cfg.Code = _txtCode.Text.Trim();
        _cfg.ClientOutDevice = DevId(_cbOut);
        _cfg.BufferMs = (int)_nudBuf.Value;
        _cfg.SyncClipboard = _cbClip.Checked;
        _cfg.Save();
    }

    // ---------- события ----------

    void WireEvents()
    {
        _rbHost.CheckedChanged += (_, _) => UpdateRoleUi();
        _rbSync.CheckedChanged += (_, _) => UpdateSyncUi();
        _tbExtra.ValueChanged += (_, _) =>
        {
            UpdateExtraLabel();
            if (_host != null) _host.ExtraDelayMs = _tbExtra.Value;
        };
        _cbClip.CheckedChanged += (_, _) => _clip.Enabled = _cbClip.Checked;
        _nudBuf.ValueChanged += async (_, _) =>
        {
            if (_client != null) await _client.SetBuffer((int)_nudBuf.Value);
        };
        _btnHost.Click += (_, _) => ToggleHost();
        _btnClient.Click += (_, _) => ToggleClient();
        _btnFind.Click += (_, _) => FindHosts();
    }

    void UpdateExtraLabel() => _lblExtra.Text = $"{_tbExtra.Value:+#;-#;0} мс";

    void UpdateRoleUi()
    {
        _gbHost.Visible = _rbHost.Checked;
        _gbClient.Visible = _rbClient.Checked;
        UpdateSyncUi();
    }

    void UpdateSyncUi()
    {
        _cbLocalOut.Enabled = _rbSync.Checked;
        _tbExtra.Enabled = _rbSync.Checked;
    }

    void Ui(Action a)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
    }

    void AppendLog(string m) => _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {m}\r\n");

    // ---------- хост ----------

    void ToggleHost()
    {
        if (_host != null)
        {
            _host.Dispose();
            _host = null;
            _clip.SendText = null; _clip.SendImage = null;
            _btnHost.Text = "Запустить хост";
            _rbHost.Enabled = _rbClient.Enabled = true;
            _status.Text = "Хост остановлен";
            return;
        }

        SaveSettings();
        var s = new HostSettings
        {
            CaptureDeviceId = _cfg.CaptureDevice,
            SyncLocal = _cfg.Sync,
            LocalOutDeviceId = _cfg.LocalOutDevice,
            ExtraDelayMs = _cfg.ExtraMs,
            BitrateKbps = _cfg.Bitrate
        };
        var h = new HostService(s, _cfg.HostCode);
        h.Log += m => Ui(() => AppendLog(m));
        h.Status += m => Ui(() => _status.Text = m);
        h.ClipText += t => Ui(() => _clip.SetText(t));
        h.ClipImage += b => Ui(() => _clip.SetImage(b));
        try { h.Start(); }
        catch (Exception ex)
        {
            h.Dispose();
            MessageBox.Show("Не удалось запустить хост: " + ex.Message + "\nВозможно, порт " + Ports.Control + " уже занят.");
            return;
        }
        _host = h;
        _clip.SendText = h.SendClipText;
        _clip.SendImage = h.SendClipImage;
        _btnHost.Text = "Остановить хост";
        _rbHost.Enabled = _rbClient.Enabled = false;
    }

    // ---------- клиент ----------

    static string ParseIp(string s) => s.Split(' ')[0].Trim();

    async void FindHosts()
    {
        _btnFind.Enabled = false;
        try
        {
            AppendLog("Ищу хосты в сети…");
            var list = await Discovery.FindHostsAsync(TimeSpan.FromSeconds(1.5));
            _cbHost.Items.Clear();
            foreach (var h in list) _cbHost.Items.Add($"{h.ip} — {h.name}");
            if (list.Count > 0) { _cbHost.SelectedIndex = 0; AppendLog($"Найдено хостов: {list.Count}"); }
            else AppendLog("Хосты не найдены. Введи IP вручную (он показан в окне хоста).");
        }
        catch (Exception ex) { AppendLog("Ошибка поиска: " + ex.Message); }
        finally { _btnFind.Enabled = true; }
    }

    async void ToggleClient()
    {
        if (_client != null)
        {
            DisconnectClient();
            return;
        }
        if (!IPAddress.TryParse(ParseIp(_cbHost.Text), out var ip))
        {
            MessageBox.Show("Введи IP хоста или нажми «Найти хост».");
            return;
        }
        SaveSettings();
        var c = new ClientService();
        c.Log += m => Ui(() => AppendLog(m));
        c.Status += m => Ui(() => _status.Text = m);
        c.ClipText += t => Ui(() => _clip.SetText(t));
        c.ClipImage += b => Ui(() => _clip.SetImage(b));
        c.Disconnected += () => Ui(() => { AppendLog("Соединение с хостом потеряно"); DisconnectClient(); });
        _btnClient.Enabled = false;
        try
        {
            await c.ConnectAsync(ip, _cfg.Code, _cfg.ClientOutDevice, _cfg.BufferMs);
            _client = c;
            _clip.SendText = c.SendClipText;
            _clip.SendImage = c.SendClipImage;
            _btnClient.Text = "Отключиться";
            _rbHost.Enabled = _rbClient.Enabled = false;
        }
        catch (Exception ex)
        {
            c.Dispose();
            AppendLog("Не удалось подключиться: " + ex.Message);
        }
        finally { _btnClient.Enabled = true; }
    }

    void DisconnectClient()
    {
        _client?.Dispose();
        _client = null;
        _clip.SendText = null; _clip.SendImage = null;
        _btnClient.Text = "Подключиться";
        _rbHost.Enabled = _rbClient.Enabled = true;
        _status.Text = "Отключено";
    }

    // ---------- буфер обмена (слушатель Windows) ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) _clip.OnLocalChanged();
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveSettings();
        _host?.Dispose();
        _client?.Dispose();
        base.OnFormClosing(e);
    }
}
