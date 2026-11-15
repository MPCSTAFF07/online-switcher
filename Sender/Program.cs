using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

ApplicationConfiguration.Initialize();
Application.Run(new SenderForm());

public sealed class SenderForm : Form
{
    readonly TextBox code = new() { Multiline = true, Height = 58, ScrollBars = ScrollBars.Vertical };
    readonly ComboBox target = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox encoder = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox audio = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Button start = new() { Text = "送信開始", Height = 44, BackColor = Color.FromArgb(234, 200, 55), ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
    readonly Label state = new() { Text = "停止中", AutoSize = true };
    readonly Label metrics = new() { Text = "受信先 0 / 0.00 Mbps", AutoSize = true };
    readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 120 };
    readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "OCS Sender", Visible = true };
    readonly List<string> audioIds = ["none", "loopback", "mic"];
    Link? link; Task? run; bool busy; long lastBytes;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OCS-Link", "sender.json");
    public SenderForm()
    {
        Text = "OCS LINK / Sender"; Width = 580; Height = 790; MinimumSize = new(520, 740); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 10); BackColor = Color.FromArgb(19, 20, 22); ForeColor = Color.White;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, AutoScroll = true };
        Controls.Add(layout);
        void Add(Control control) { control.Dock = DockStyle.Top; control.Margin = new Padding(0, 0, 0, 12); layout.Controls.Add(control); }
        void Label(string text) => Add(new Label { Text = text, AutoSize = true, ForeColor = Color.Silver });
        Add(new Label { Text = "OCS LINK", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Height = 45 });
        Label("画面送信 / WebRTC / ブラウザー不要"); Label("接続コード"); Add(code);
        Label("送信する画面・ウィンドウ"); Add(target);
        var refresh = new Button { Text = "画面一覧を更新", Height = 29, FlatStyle = FlatStyle.Flat }; refresh.Click += (_, _) => RefreshTargets(); Add(refresh);
        Label("画質"); quality.Items.AddRange(["軽量 / 1280×720・30fps・2.5Mbps", "標準 / 1920×1080・30fps・5Mbps", "動き優先 / 1920×1080・60fps・8Mbps"]); quality.SelectedIndex = 0; Add(quality);
        Label("エンコーダー"); encoder.Items.AddRange(["自動判定", "NVIDIA NVENC", "Intel Quick Sync", "AMD AMF", "CPU / 最大720p30"]); encoder.SelectedIndex = 0; Add(encoder);
        Label("音声（1種類を選択）"); audio.Items.AddRange(["音声を送らない", "PCの再生音 / 既定の出力", "マイク / 既定の入力"]);
        try { foreach (var d in new MMDeviceEnumerator().EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) { audio.Items.Add(d.FriendlyName); audioIds.Add(d.ID); } } catch { }
        audio.SelectedIndex = 1; Add(audio); Add(start); Add(state); Add(metrics); Add(log);
        RefreshTargets(); LoadSettings();
        start.Click += async (_, _) => await Toggle();
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        var menu = new ContextMenuStrip(); menu.Items.Add("表示", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; }); menu.Items.Add("終了", null, (_, _) => Close()); tray.ContextMenuStrip = menu;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        timer.Tick += (_, _) => { if (link != null) { long b = link.Bytes; metrics.Text = $"受信先 {link.Viewers} / {(b - lastBytes) * 8 / 1_000_000.0:0.00} Mbps"; lastBytes = b; } }; timer.Start();
        FormClosing += async (_, e) => { if (link != null) { e.Cancel = true; await Stop(); Close(); } else { tray.Visible = false; tray.Dispose(); } };
    }
    void Ui(Action a) { if (IsDisposed || !IsHandleCreated) return; if (InvokeRequired) BeginInvoke(a); else a(); }
    void Append(string s) => Ui(() => { if (log.TextLength > 16000) log.Text = log.Text[^8000..]; log.AppendText($"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); });
    async Task Toggle()
    {
        if (busy) return;
        if (link != null) { await Stop(); return; }
        if (target.SelectedItem is not CaptureTarget selected || code.Text.Trim().Length == 0) { MessageBox.Show("画面と接続コードを指定してください"); return; }
        SaveSettings(); busy = true; start.Enabled = false;
        try
        {
            var q = quality.SelectedIndex; var s = new SendSettings(code.Text, selected, q == 0 ? 1280 : 1920, q == 0 ? 720 : 1080, q == 2 ? 60 : 30, q == 0 ? 2500 : q == 1 ? 5000 : 8000, new[] { "auto", "h264_nvenc", "h264_qsv", "h264_amf", "libx264" }[encoder.SelectedIndex], audioIds[audio.SelectedIndex]);
            link = new Link(); link.Log += Append; link.Status += s => Ui(() => state.Text = s); link.Fatal += s => { Append(s); Ui(() => state.Text = s); };
            var current = link; lastBytes = 0; SetInputs(false); start.Text = "送信停止";
            run = Task.Run(async () => { try { await current.Run(s); } catch (OperationCanceledException) { } catch (Exception e) { Append(e.Message); } finally { Ui(() => { if (ReferenceEquals(link, current)) _ = Stop(); }); } });
        }
        finally { busy = false; start.Enabled = true; }
    }
    async Task Stop()
    {
        var old = link; link = null;
        if (old != null) { await old.DisposeAsync(); if (run != null) try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }
        SetInputs(true); state.Text = "停止中"; start.Text = "送信開始"; metrics.Text = "受信先 0 / 0.00 Mbps";
    }
    void SetInputs(bool enabled) { code.Enabled = target.Enabled = quality.Enabled = encoder.Enabled = audio.Enabled = enabled; }
    void RefreshTargets()
    {
        target.Items.Clear(); int i = 1;
        foreach (var screen in Screen.AllScreens) target.Items.Add(new CaptureTarget($"画面 {i++} / {screen.Bounds.Width}×{screen.Bounds.Height}", "desktop", screen.Bounds));
        EnumWindows((handle, _) => { int n = GetWindowTextLength(handle); if (IsWindowVisible(handle) && n > 0) { var text = new StringBuilder(n + 1); GetWindowText(handle, text, text.Capacity); if (text.ToString() != Text) target.Items.Add(new CaptureTarget("ウィンドウ / " + text, "title=" + text, Rectangle.Empty)); } return true; }, IntPtr.Zero);
        if (target.Items.Count > 0) target.SelectedIndex = 0;
    }
    void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        // 接続コードは保存しない。終了後も秘密が平文で残ることを避ける。
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { quality = quality.SelectedIndex, encoder = encoder.SelectedIndex, audio = audioIds[audio.SelectedIndex] }));
    }
    void LoadSettings()
    {
        try { using var d = JsonDocument.Parse(File.ReadAllText(SettingsPath)); quality.SelectedIndex = Math.Clamp(d.RootElement.GetProperty("quality").GetInt32(), 0, 2); encoder.SelectedIndex = Math.Clamp(d.RootElement.GetProperty("encoder").GetInt32(), 0, 4); int a = audioIds.IndexOf(d.RootElement.GetProperty("audio").GetString() ?? ""); if (a >= 0) audio.SelectedIndex = a; } catch { }
    }
    delegate bool EnumProc(IntPtr handle, IntPtr param);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr handle);
}
