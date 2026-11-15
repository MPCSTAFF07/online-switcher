using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Ocs.Core;

if (args.Length == 0)
{
    await StudioControlTests.Run();
    var ice = new SIPSorcery.Net.RTCIceCandidate(SIPSorcery.Net.RTCIceProtocol.udp, IPAddress.Loopback, 54321, SIPSorcery.Net.RTCIceCandidateType.host);
    var browserIce = IceSignaling.ForBrowser(ice);
    if (!((string?)browserIce["candidate"])!.StartsWith("candidate:") || (string?)browserIce["sdpMid"] != "0" || (int?)browserIce["sdpMLineIndex"] != 0)
        throw new Exception("ICE candidate must use the browser's RTCIceCandidateInit representation");
    Console.WriteLine("PASS native ICE candidates include browser prefix and media identification");
    void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
    Check(Rtp.Parse([0x80,0xe0,0x12,0x34,0,0,0,9,0,0,0,1,0x65,2]) is { Timestamp:9, Sequence:0x1234, Marker:1, Data.Length:2 }, "RTP header / marker / payload");
    Check(Rtp.Parse([0x90,96,0,1,0,0,0,2,0,0,0,1,0xbe,0xde,0,1,0,0,0,0,0x65]) is { Data.Length:1 }, "RTP extension");
    Check(Rtp.Parse([0xa0,96,0,1,0,0,0,2,0,0,0,1,0x65,0,0,3]) is { Data.Length:1 }, "RTP padding");
    Check(Rtp.Parse([0x90,96,0,1,0,0,0,2,0,0,0,1]) == null, "RTP truncated extension rejected");
    Check(Rtp.Parse([0xa0,96,0,1,0,0,0,2,0,0,0,1,0x65,0]) == null, "RTP invalid padding rejected");
    return;
}
await using var link = new Link(); link.Log += Console.WriteLine; link.Status += Console.WriteLine; link.Fatal += Console.WriteLine;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; _ = link.DisposeAsync(); };
await link.Run(new SendSettings(args[0], "loopback"));

public sealed record SendSettings(string Code, string Audio);
// Linux-only test capture. Production Link.cs is compiled unchanged above.
public sealed class Capture : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly List<Process> processes = [];
    readonly List<UdpClient> sockets = [];
    public string Encoder => "test / libx264";
    public event Action<RtpPayload, bool>? Packet;
    public event Action<string>? Log;
    public event Action<string>? Failed;
    public Task Start(SendSettings s, CancellationToken ct)
    {
        foreach (bool audio in new[] { false, true })
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); sockets.Add(udp);
            int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            string[] args = audio ? ["-hide_banner","-loglevel","error","-re","-f","lavfi","-i","sine=frequency=440:sample_rate=48000","-c:a","libopus","-ar","48000","-ac","2","-frame_duration","20","-payload_type","111","-f","rtp",$"rtp://127.0.0.1:{port}?pkt_size=1200&rtcpport=9"] : ["-hide_banner","-loglevel","error","-re","-f","lavfi","-i","testsrc2=size=640x360:rate=30","-c:v","libx264","-profile:v","baseline","-level:v","5.1","-preset","ultrafast","-tune","zerolatency","-g","30","-bf","0","-b:v","1200k","-payload_type","96","-f","rtp",$"rtp://127.0.0.1:{port}?pkt_size=1200&rtcpport=9"];
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!; processes.Add(p); _ = p.StandardOutput.ReadToEndAsync(); _ = Task.Run(async()=> { var e=await p.StandardError.ReadToEndAsync(); if(e.Length>0) Log?.Invoke(e); });
            _ = Task.Run(async()=>{try{while(!stop.IsCancellationRequested){var d=await udp.ReceiveAsync(stop.Token);if(Rtp.Parse(d.Buffer) is RtpPayload r)Packet?.Invoke(r,audio);}}catch(OperationCanceledException){}catch(Exception e){if(!stop.IsCancellationRequested)Failed?.Invoke(e.Message);}});
        }
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync(){stop.Cancel();foreach(var p in processes){try{p.Kill(true);}catch{}p.Dispose();}processes.Clear();foreach(var u in sockets)u.Dispose();sockets.Clear();return ValueTask.CompletedTask;}
}
