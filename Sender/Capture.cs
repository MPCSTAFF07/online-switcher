using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using Ocs.Core;

public sealed record CaptureTarget(string Label, string Input, Rectangle Bounds)
{
    public override string ToString() => Label;
}
public sealed record SendSettings(string Code, CaptureTarget Target, int Width, int Height, int Fps, int Kbps, string Encoder, string Audio);
public sealed class Capture : IAsyncDisposable
{
    readonly List<Process> processes = [];
    readonly List<UdpClient> sockets = [];
    readonly CancellationTokenSource stop = new();
    WasapiCapture? audio;
    public string Encoder { get; private set; } = "";
    public event Action<RtpPayload, bool>? Packet;
    public event Action<string>? Log;
    public event Action<string>? Failed;
    public static string FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Prepend(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links")))
        { var path = Path.Combine(dir.Trim('"'), "ffmpeg.exe"); if (File.Exists(path)) return path; }
        throw new Exception("FFmpegが見つかりません。先に SETUP-SENDER.bat を実行してください。");
    }
    public async Task Start(SendSettings s, CancellationToken ct)
    {
        string ffmpeg = FindFfmpeg();
        string[] candidates = s.Encoder == "auto" ? ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"] : [s.Encoder];
        foreach (string encoder in candidates)
        {
            Log?.Invoke($"エンコーダー確認: {encoder}");
            if (await Probe(ffmpeg, encoder, ct)) { Encoder = encoder; break; }
        }
        if (Encoder == "") throw new Exception("使用できるH.264エンコーダーがありません");
        int width = s.Width, height = s.Height, fps = s.Fps;
        if (Encoder == "libx264") { width = Math.Min(width, 1280); height = Math.Min(height, 720); fps = Math.Min(fps, 30); Log?.Invoke("GPUエンコード非対応。CPU／最大720p30で送信します。"); }
        var udp = OpenRtp(false); int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        List<string> args = ["-hide_banner", "-loglevel", "warning", "-nostdin", "-thread_queue_size", "16", "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "1"];
        if (s.Target.Input == "desktop") args.AddRange(["-offset_x", s.Target.Bounds.X.ToString(), "-offset_y", s.Target.Bounds.Y.ToString(), "-video_size", $"{s.Target.Bounds.Width}x{s.Target.Bounds.Height}"]);
        args.AddRange(["-i", s.Target.Input, "-an", "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease:flags=fast_bilinear,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2", "-c:v", Encoder]);
        args.AddRange(EncoderOptions(Encoder));
        args.AddRange(["-profile:v", "baseline", "-level:v", "5.1", "-pix_fmt", "yuv420p", "-b:v", $"{s.Kbps}k", "-maxrate", $"{s.Kbps}k", "-bufsize", $"{s.Kbps}k", "-g", fps.ToString(), "-bf", "0", "-threads", "2", "-payload_type", "96", "-f", "rtp", $"rtp://127.0.0.1:{port}?pkt_size=1200&rtcpport=9"]);
        Launch(ffmpeg, args);
        if (s.Audio != "none") StartAudio(ffmpeg, s.Audio);
    }
    static string[] EncoderOptions(string e) => e switch
    {
        "h264_nvenc" => ["-preset", "p1", "-tune", "ull", "-rc", "cbr", "-zerolatency", "1"],
        "h264_qsv" => ["-preset", "veryfast", "-look_ahead", "0"],
        "h264_amf" => ["-usage", "ultralowlatency", "-quality", "speed"],
        _ => ["-preset", "ultrafast", "-tune", "zerolatency"]
    };
    static async Task<bool> Probe(string ffmpeg, string encoder, CancellationToken ct)
    {
        var p = new Process { StartInfo = Info(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=size=1280x720:rate=30", "-frames:v", "3", "-c:v", encoder, .. EncoderOptions(encoder), "-profile:v", "baseline", "-pix_fmt", "yuv420p", "-f", "null", "-"]) };
        try
        {
            p.Start(); var errors = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct); await errors; return p.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
        finally { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
    }
    UdpClient OpenRtp(bool isAudio)
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); sockets.Add(udp);
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var data = await udp.ReceiveAsync(stop.Token);
                    if (Rtp.Parse(data.Buffer) is RtpPayload p) Packet?.Invoke(p, isAudio);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception e) { if (!stop.IsCancellationRequested) Failed?.Invoke(e.Message); }
        });
        return udp;
    }
    void StartAudio(string ffmpeg, string source)
    {
        audio = source == "loopback" ? new WasapiLoopbackCapture() : source == "mic" ? new WasapiCapture() : new WasapiCapture(new MMDeviceEnumerator().GetDevice(source));
        var wave = audio.WaveFormat;
        bool floating = wave.Encoding == WaveFormatEncoding.IeeeFloat || wave is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        string format = floating ? "f32le" : wave.BitsPerSample switch { 16 => "s16le", 24 => "s24le", 32 => "s32le", _ => throw new Exception("未対応の音声形式です") };
        var udp = OpenRtp(true); int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var p = Launch(ffmpeg, ["-hide_banner", "-loglevel", "warning", "-f", format, "-ar", wave.SampleRate.ToString(), "-ac", wave.Channels.ToString(), "-i", "pipe:0", "-vn", "-c:a", "libopus", "-ar", "48000", "-ac", "2", "-b:a", "96k", "-application", "lowdelay", "-frame_duration", "20", "-payload_type", "111", "-f", "rtp", $"rtp://127.0.0.1:{port}?pkt_size=1200&rtcpport=9"], true);
        var buffer = new BufferedWaveProvider(wave) { BufferDuration = TimeSpan.FromMilliseconds(200), DiscardOnBufferOverflow = true, ReadFully = true };
        audio.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        audio.RecordingStopped += (_, e) => { if (e.Exception != null && !stop.IsCancellationRequested) Failed?.Invoke("音声: " + e.Exception.Message); };
        // WASAPI loopback can stop raising events during silence. Feed zero samples on
        // a fixed clock so Opus timestamps retain those gaps instead of drifting.
        _ = Task.Run(async () =>
        {
            int count = wave.AverageBytesPerSecond / 50 / wave.BlockAlign * wave.BlockAlign;
            byte[] frame = new byte[count]; using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            try { while (await clock.WaitForNextTickAsync(stop.Token)) { buffer.Read(frame, 0, count); await p.StandardInput.BaseStream.WriteAsync(frame, stop.Token); } }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (!stop.IsCancellationRequested) Failed?.Invoke("音声送信: " + e.Message); }
        });
        audio.StartRecording();
    }
    static ProcessStartInfo Info(string file, IEnumerable<string> args, bool stdin = false)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = stdin };
        foreach (string arg in args) info.ArgumentList.Add(arg); return info;
    }
    Process Launch(string file, string[] args, bool stdin = false) => Launch(file, (IEnumerable<string>)args, stdin);
    Process Launch(string file, IEnumerable<string> args, bool stdin = false)
    {
        var p = new Process { StartInfo = Info(file, args, stdin), EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
        p.Exited += (_, _) => { if (!stop.IsCancellationRequested) Failed?.Invoke($"エンコーダーが終了しました（{p.ExitCode}）。ログを確認してください。"); };
        p.Start(); p.BeginErrorReadLine(); _ = p.StandardOutput.ReadToEndAsync(); processes.Add(p); return p;
    }
    public async ValueTask DisposeAsync()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel(); audio?.StopRecording(); audio?.Dispose();
        foreach (var p in processes) { try { if (!p.HasExited) p.Kill(true); } catch { } p.Dispose(); }
        foreach (var s in sockets) s.Dispose(); await Task.CompletedTask;
    }
}
