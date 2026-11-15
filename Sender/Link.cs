using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ocs.Core;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

public sealed class Link : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, RTCPeerConnection> peers = new();
    readonly SemaphoreSlim sending = new(1);
    ClientWebSocket? socket;
    Capture? capture;
    public event Action<string>? Log;
    public event Action<string>? Status;
    public event Action<string>? Fatal;
    long bytes;
    public long Bytes => Interlocked.Read(ref bytes);
    public int Viewers => peers.Values.Count(p => p.connectionState == RTCPeerConnectionState.connected);
    public async Task Run(SendSettings settings)
    {
        var json = settings.Code.Trim().Replace('-', '+').Replace('_', '/'); json = json.PadRight((json.Length + 3) / 4 * 4, '=');
        JsonObject code;
        try { code = JsonNode.Parse(Convert.FromBase64String(json))!.AsObject(); }
        catch { throw new Exception("接続コードを確認してください。配信側の「接続」からコピーします。"); }
        var address = new Uri((string?)code["url"] ?? "");
        if (address.Scheme is not ("http" or "https")) throw new Exception("接続先URLが不正です");
        var endpoint = new UriBuilder(address) { Scheme = address.Scheme == "https" ? "wss" : "ws", Path = "/signal", Query = "", Fragment = "" }.Uri;
        capture = new Capture(); capture.Log += s => Log?.Invoke(s);
        capture.Failed += s => { Fatal?.Invoke(s); stop.Cancel(); socket?.Abort(); };
        capture.Packet += Forward;
        await capture.Start(settings, stop.Token);
        Log?.Invoke($"使用エンコーダー: {capture.Encoder}");
        int retries = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Status?.Invoke("接続中");
                    using var ws = new ClientWebSocket(); ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15); socket = ws;
                    await ws.ConnectAsync(endpoint, stop.Token).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
                    await Send(new() { ["role"] = "sender", ["slot"] = code["slot"]!.DeepClone(), ["token"] = code["token"]!.DeepClone() });
                    var ready = await Read(ws, stop.Token).WaitAsync(TimeSpan.FromSeconds(15), stop.Token);
                    if ((string?)ready["type"] == "error") throw new InvalidDataException((string?)ready["message"]);
                    if ((string?)ready["type"] != "ready") throw new Exception("接続応答が不正です");
                    var ice = (ready["iceServers"] as JsonArray ?? new()).Select(i => new RTCIceServer { urls = (string?)i?["urls"] ?? "", username = (string?)i?["username"], credential = (string?)i?["credential"] }).ToList();
                    retries = 0; Status?.Invoke("接続済み／受信待ち");
                    while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
                    {
                        var msg = await Read(ws, stop.Token); string type = (string?)msg["type"] ?? "";
                        if (type == "viewer") await AddPeer((string)msg["peer"]!, ice, settings.Audio != "none");
                        else if (type == "left") RemovePeer((string)msg["peer"]!, "受信ページが接続を閉じました");
                        else if (type == "diagnostic")
                        {
                            string fromId = (string?)msg["from"] ?? "unknown";
                            Log?.Invoke($"受信側 {fromId[..Math.Min(6, fromId.Length)]} / {(string?)msg["phase"]}: {(string?)msg["message"]}");
                        }
                        else if (msg["from"] is JsonValue from && peers.TryGetValue((string)from!, out var pc))
                        {
                            if (type == "answer")
                            {
                                var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = (string)msg["sdp"]! };
                                var result = pc.setRemoteDescription(answer);
                                if (result != SetDescriptionResultEnum.OK) throw new Exception($"WebRTC応答: {result}");
                                Log?.Invoke($"受信先 {(string)from!}: 映像形式の応答を受信（OK）");
                            }
                            else if (type == "ice" && msg["candidate"] is JsonObject candidate)
                            {
                                pc.addIceCandidate(new RTCIceCandidateInit { candidate = (string?)candidate["candidate"], sdpMid = (string?)candidate["sdpMid"], sdpMLineIndex = (ushort?)candidate["sdpMLineIndex"] ?? 0 });
                            }
                        }
                    }
                }
                catch (InvalidDataException e) { Fatal?.Invoke(e.Message); break; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) { Log?.Invoke("接続: " + e.GetBaseException().Message); }
                finally { foreach (var id in peers.Keys) RemovePeer(id); socket?.Abort(); }
                if (!stop.IsCancellationRequested)
                {
                    int delay = Math.Min(15, 2 + retries++ * 2); Status?.Invoke($"切断／{delay}秒後に再接続");
                    await Task.Delay(TimeSpan.FromSeconds(delay), stop.Token);
                }
            }
        }
        finally { await capture.DisposeAsync(); }
    }
    async Task AddPeer(string id, List<RTCIceServer> ice, bool audio)
    {
        RemovePeer(id);
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = ice }); peers[id] = pc;
        pc.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e033"), MediaStreamStatusEnum.SendOnly));
        if (audio) pc.addTrack(new MediaStreamTrack(new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1;stereo=1"), MediaStreamStatusEnum.SendOnly));
        var candidates = new List<JsonObject>(); bool offered = false;
        pc.onicecandidate += c =>
        {
            if (c == null) return;
            var obj = new JsonObject { ["type"] = "ice", ["to"] = id, ["candidate"] = new JsonObject { ["candidate"] = c.candidate, ["sdpMid"] = c.sdpMid, ["sdpMLineIndex"] = c.sdpMLineIndex } };
            lock (candidates) { if (!offered) { candidates.Add(obj); return; } }
            _ = SafeSend(obj);
        };
        pc.oniceconnectionstatechange += s => Log?.Invoke($"受信先 {id[..6]} / ICE: {s}");
        pc.onconnectionstatechange += s => { Status?.Invoke(s == RTCPeerConnectionState.connected ? "映像送信中" : $"WebRTC: {s}"); Log?.Invoke($"受信先 {id[..6]}: {s}"); };
        var offer = pc.createOffer(null); await pc.setLocalDescription(offer);
        Log?.Invoke($"受信先 {id[..6]}: 映像形式のオファーを送信");
        await Send(new() { ["type"] = "offer", ["to"] = id, ["sdp"] = offer.sdp });
        JsonObject[] queued;
        lock (candidates) { offered = true; queued = candidates.ToArray(); candidates.Clear(); }
        foreach (var c in queued) await SafeSend(c);
    }
    void Forward(RtpPayload p, bool audio)
    {
        foreach (var pc in peers.Values)
        {
            if (pc.connectionState != RTCPeerConnectionState.connected) continue;
            try
            {
                pc.SendRtpRaw(audio ? SDPMediaTypesEnum.audio : SDPMediaTypesEnum.video, p.Data, p.Timestamp, p.Marker, audio ? 111 : 96);
                Interlocked.Add(ref bytes, p.Data.Length);
            }
            catch (Exception e) { Log?.Invoke("送信: " + e.Message); }
        }
    }
    void RemovePeer(string id, string reason = "接続の終了・再作成") { if (peers.TryRemove(id, out var p)) { Log?.Invoke($"受信先 {id[..Math.Min(6, id.Length)]} を終了: {reason}"); p.Close(reason); } }
    async Task SafeSend(JsonObject obj) { try { await Send(obj); } catch (Exception e) { if (!stop.IsCancellationRequested) Log?.Invoke(e.Message); } }
    async Task Send(JsonObject obj)
    {
        await sending.WaitAsync(stop.Token);
        try { if (socket?.State == WebSocketState.Open) await socket.SendAsync(Encoding.UTF8.GetBytes(obj.ToJsonString()), WebSocketMessageType.Text, true, stop.Token); }
        finally { sending.Release(); }
    }
    static async Task<JsonObject> Read(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[16384]; using var ms = new MemoryStream(); WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buffer, ct); if (r.MessageType == WebSocketMessageType.Close) throw new Exception($"接続受付が通信を終了（{r.CloseStatus}）: {r.CloseStatusDescription}"); ms.Write(buffer, 0, r.Count); if (ms.Length > 131072) throw new Exception("応答サイズ上限"); } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray())!.AsObject();
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); socket?.Abort(); foreach (var id in peers.Keys) RemovePeer(id);
        if (capture != null) await capture.DisposeAsync();
    }
}
