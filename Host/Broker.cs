using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

public sealed class SignalPeer(WebSocket socket, string role, int slot)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Role => role;
    public int Slot => slot;
    public WebSocket Socket => socket;
    readonly SemaphoreSlim write = new(1);
    public async Task Send(JsonObject message)
    {
        await write.WaitAsync();
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (WebSocketException) { socket.Abort(); }
        finally { write.Release(); }
    }
}
public sealed class Broker(AppConfig config)
{
    readonly ConcurrentDictionary<string, SignalPeer> peers = new();
    readonly SemaphoreSlim admission = new(1);
    public object[] Status() => config.Slots.Select(s => (object)new { s.Id, s.Name, online = peers.Values.Any(p => p.Slot == s.Id && p.Role == "sender"), viewers = peers.Values.Count(p => p.Slot == s.Id && p.Role == "viewer") }).ToArray();
    public static bool Secret(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public async Task Handle(WebSocket ws)
    {
        SignalPeer? peer = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var auth = await Read(ws, timeout.Token);
            int slot = (int?)auth["slot"] ?? 0; string role = (string?)auth["role"] ?? "";
            var entry = config.Slots.FirstOrDefault(s => s.Id == slot);
            if (entry == null || role is not ("sender" or "viewer") || !Secret((string?)auth["token"] ?? "", role == "sender" ? entry.SendToken : entry.ViewToken)) throw new Exception("接続コードが一致しません");
            await admission.WaitAsync();
            try
            {
                if (peers.Values.Count(p => p.Slot == slot && p.Role == role) >= (role == "sender" ? 1 : 2)) throw new Exception(role == "sender" ? "この送信枠は使用中です" : "受信数の上限です（2接続）");
                peer = new(ws, role, slot); peers[peer.Id] = peer;
            }
            finally { admission.Release(); }
            await peer.Send(new() { ["type"] = "ready", ["id"] = peer.Id, ["iceServers"] = System.Text.Json.JsonSerializer.SerializeToNode(config.IceServers) });
            if (role == "viewer")
            {
                foreach (var sender in peers.Values.Where(p => p.Slot == slot && p.Role == "sender")) await sender.Send(new() { ["type"] = "viewer", ["peer"] = peer.Id });
            }
            else foreach (var viewer in peers.Values.Where(p => p.Slot == slot && p.Role == "viewer")) await peer.Send(new() { ["type"] = "viewer", ["peer"] = viewer.Id });
            while (ws.State == WebSocketState.Open)
            {
                var msg = await Read(ws, CancellationToken.None);
                string type = (string?)msg["type"] ?? "";
                if (type == "ping") { await peer.Send(new() { ["type"] = "pong" }); continue; }
                if (type is not ("offer" or "answer" or "ice")) continue;
                if ((type == "offer" && role != "sender") || (type == "answer" && role != "viewer")) continue;
                string target = (string?)msg["to"] ?? "";
                if (peers.TryGetValue(target, out var recipient) && recipient.Slot == slot && recipient.Role != role)
                { msg.Remove("to"); msg["from"] = peer.Id; await recipient.Send(msg); }
            }
        }
        catch (Exception e)
        {
            if (ws.State == WebSocketState.Open)
            {
                try { var error = Encoding.UTF8.GetBytes(new JsonObject { ["type"] = "error", ["message"] = e.Message }.ToJsonString()); await ws.SendAsync(error, WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
            }
        }
        finally
        {
            if (peer != null)
            {
                peers.TryRemove(peer.Id, out _);
                foreach (var other in peers.Values.Where(p => p.Slot == peer.Slot && p.Role != peer.Role)) await other.Send(new() { ["type"] = "left", ["peer"] = peer.Id });
            }
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "connection ended", closing.Token);
                }
            }
            catch { ws.Abort(); }
            ws.Dispose();
        }
    }
    static async Task<JsonObject> Read(WebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream(); byte[] b = new byte[16384]; WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(b, ct);
            if (r.MessageType != WebSocketMessageType.Text) throw new Exception("接続終了");
            ms.Write(b, 0, r.Count);
            if (ms.Length > 131072) throw new Exception("メッセージ上限を超えました");
        } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray())!.AsObject();
    }
}
