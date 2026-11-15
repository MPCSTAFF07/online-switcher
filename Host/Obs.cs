using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

public sealed class Obs : IAsyncDisposable
{
    volatile Connection? current;
    readonly SemaphoreSlim connecting = new(1);
    readonly ConcurrentDictionary<string, (DateTime time, string data)> images = new();
    readonly SemaphoreSlim screenshots = new(2);
    readonly object logLock = new();
    string lastError = "未接続";
    public bool Connected => current?.Connected == true;
    public string Error => current?.Error is { Length: > 0 } e ? e : Connected ? "" : lastError;
    public string Endpoint { get; private set; } = "";
    public string Version { get; private set; } = "";
    public async Task Connect(string url, string password)
    {
        await connecting.WaitAsync();
        Connection? next = null;
        string stage = "接続先の確認";
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new Exception("OBS接続先は ws://127.0.0.1:4455 の形式で指定してください");
            current?.Dispose(); current = null; images.Clear(); Version = "";
            Endpoint = uri.GetLeftPart(UriPartial.Path); lastError = "OBS接続を確認中";
            next = new Connection(Log);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            stage = "WebSocket接続"; await next.Socket.ConnectAsync(uri, timeout.Token);
            Log($"OPEN {Endpoint}");
            stage = "OBS Hello受信";
            var hello = await Receive(next.Socket, timeout.Token);
            if ((int?)hello["op"] != 0) throw new Exception("OBS WebSocket v5のHelloが返りません。接続ポートを確認してください");
            var auth = hello["d"]?["authentication"];
            var data = new JsonObject { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
            if (auth != null) data["authentication"] = Hash(Hash(password + (string?)auth["salt"]) + (string?)auth["challenge"]);
            stage = "OBS認証";
            await next.Send(new JsonObject { ["op"] = 1, ["d"] = data }, timeout.Token);
            var response = await Receive(next.Socket, timeout.Token);
            if ((int?)response["op"] != 2) throw new Exception("OBS認証が完了しませんでした。WebSocketパスワードを確認してください");
            next.StartReader();
            stage = "OBSバージョン確認"; var version = await next.Call("GetVersion");
            stage = "OBSシーン一覧の取得"; var scenes = await next.Call("GetSceneList");
            if (scenes["scenes"] is not JsonArray) throw new Exception("OBSから正常なシーン一覧が返りませんでした");
            if (next.Socket.State != WebSocketState.Open || next.Error.Length > 0) throw new Exception(next.Error.Length > 0 ? next.Error : "確認中に接続が切れました");
            next.Verified = true; current = next; Version = (string?)version["obsVersion"] ?? "";
            lastError = ""; Log($"VERIFIED OBS {Version} / scenes={(scenes["scenes"] as JsonArray)!.Count}");
        }
        catch (Exception e)
        {
            next?.Dispose();
            string detail = e is OperationCanceledException ? "応答がありません（12秒）。OBS起動・ポート・ファイアウォールを確認してください" : e.GetBaseException().Message;
            lastError = $"{stage}: {detail}"; Log("CONNECT FAILED " + lastError);
            throw new Exception(lastError, e);
        }
        finally { connecting.Release(); }
    }
    static string Hash(string s) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    public Task<JsonObject> Call(string type, JsonObject? data = null)
    {
        var session = current;
        if (session?.Connected != true) throw new Exception(Error.Length > 0 ? Error : "OBSに接続してください");
        return session.Call(type, data);
    }
    public async Task<string> Screenshot(string scene, int width)
    {
        string key = scene + ":" + width;
        if (images.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.time < TimeSpan.FromSeconds(1)) return entry.data;
        await screenshots.WaitAsync();
        try
        {
            if (images.TryGetValue(key, out entry) && DateTime.UtcNow - entry.time < TimeSpan.FromSeconds(1)) return entry.data;
            var r = await Call("GetSourceScreenshot", new() { ["sourceName"] = scene, ["imageFormat"] = "jpg", ["imageWidth"] = width, ["imageCompressionQuality"] = 45 });
            var value = (string)r["imageData"]!; images[key] = (DateTime.UtcNow, value); return value;
        }
        finally { screenshots.Release(); }
    }
    void Log(string message)
    {
        lock (logLock)
        {
            var line = $"{DateTimeOffset.Now:O} {message}";
            Console.WriteLine(line);
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "obs-connection.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }
    }
    static async Task<JsonObject> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream(); byte[] b = new byte[65536]; WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(b, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                var code = r.CloseStatus.HasValue ? ((int)r.CloseStatus.Value).ToString() : "不明";
                throw new Exception($"OBSが接続を閉じました（コード {code}）: {r.CloseStatusDescription ?? "理由なし"}");
            }
            if (r.MessageType != WebSocketMessageType.Text) throw new Exception("OBSからJSON以外の応答を受信しました。接続ポートを確認してください");
            ms.Write(b, 0, r.Count);
            if (ms.Length > 8 * 1024 * 1024) throw new Exception("OBSメッセージが大きすぎます");
        } while (!r.EndOfMessage);
        return JsonNode.Parse(ms.ToArray())?.AsObject() ?? throw new Exception("OBSの応答が空です");
    }
    sealed class Connection(Action<string> log) : IDisposable
    {
        public ClientWebSocket Socket { get; } = new() { Options = { KeepAliveInterval = TimeSpan.FromSeconds(15) } };
        readonly CancellationTokenSource lifetime = new();
        readonly SemaphoreSlim sending = new(1);
        readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> pending = new();
        public volatile bool Verified;
        public string Error { get; private set; } = "";
        public bool Connected => Verified && !lifetime.IsCancellationRequested && Socket.State == WebSocketState.Open;
        public void StartReader() => _ = ReadLoop();
        async Task ReadLoop()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var message = await Receive(Socket, lifetime.Token);
                    if ((int?)message["op"] != 7 || message["d"] is not JsonObject data) continue;
                    var id = (string?)data["requestId"];
                    if (id != null && pending.TryRemove(id, out var completion)) completion.TrySetResult(data);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception e) { Error = e.GetBaseException().Message; log("DISCONNECTED " + Error); }
            finally
            {
                Verified = false;
                foreach (var waiter in pending.Values) waiter.TrySetException(new Exception(Error.Length > 0 ? Error : "OBSとの接続が終了しました"));
                pending.Clear();
            }
        }
        public async Task Send(JsonObject obj, CancellationToken ct)
        {
            await sending.WaitAsync(ct);
            try { await Socket.SendAsync(Encoding.UTF8.GetBytes(obj.ToJsonString()), WebSocketMessageType.Text, true, ct); }
            finally { sending.Release(); }
        }
        public async Task<JsonObject> Call(string type, JsonObject? data = null)
        {
            if (Socket.State != WebSocketState.Open || lifetime.IsCancellationRequested) throw new Exception(Error.Length > 0 ? Error : "OBSとの接続がありません");
            string id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await Send(new() { ["op"] = 6, ["d"] = new JsonObject { ["requestType"] = type, ["requestId"] = id, ["requestData"] = data?.DeepClone() } }, timeout.Token);
                var result = await completion.Task.WaitAsync(timeout.Token);
                if ((bool?)result["requestStatus"]?["result"] != true)
                {
                    string reason = $"OBS {type} 失敗（{(int?)result["requestStatus"]?["code"]}）: {(string?)result["requestStatus"]?["comment"] ?? "詳細なし"}";
                    log(reason); throw new Exception(reason);
                }
                return (result["responseData"] as JsonObject)?.DeepClone().AsObject() ?? new();
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                Error = $"OBS {type} の応答待ちが10秒を超えました"; Verified = false; log(Error); Socket.Abort();
                throw new Exception(Error);
            }
            finally { pending.TryRemove(id, out _); }
        }
        public void Dispose()
        {
            if (lifetime.IsCancellationRequested) return;
            Verified = false; lifetime.Cancel(); Socket.Abort(); Socket.Dispose();
            foreach (var waiter in pending.Values) waiter.TrySetException(new Exception("OBS接続を終了しました"));
            pending.Clear();
        }
    }
    public ValueTask DisposeAsync() { current?.Dispose(); current = null; return ValueTask.CompletedTask; }
}
