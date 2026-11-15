using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("OCS_URLS") ?? "http://127.0.0.1:17640");
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
var config = AppConfig.Load(); var broker = new Broker(config); await using var obs = new Obs();
var sessions = new ConcurrentDictionary<string, DateTime>();
var attempts = new ConcurrentDictionary<string, (DateTime until, int count)>();
Console.WriteLine("\nOCS LINK / Control: http://localhost:17640\n初回パスワードは同じフォルダーの config.json → AdminPassword を確認してください。\n");
app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Path != "/api/login")
    {
        ctx.Response.Headers.CacheControl = "no-store";
        if (!ctx.Request.Cookies.TryGetValue("ocs_session", out var token) || !sessions.TryGetValue(token, out var expires) || expires < DateTime.UtcNow)
        { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "ログインしてください" }); return; }
        if (ctx.Request.Method != "GET" && ctx.Request.Headers["X-OCS-Control"] != "1") { ctx.Response.StatusCode = 403; return; }
    }
    try { await next(); }
    catch (Exception e) { if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); } }
});
app.UseDefaultFiles(); app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });
app.MapPost("/api/login", async (HttpContext c) =>
{
    string ip = c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var a = attempts.GetOrAdd(ip, _ => (DateTime.UtcNow.AddMinutes(1), 0));
    if (a.until < DateTime.UtcNow) a = (DateTime.UtcNow.AddMinutes(1), 0);
    if (a.count >= 8) return Results.Json(new { error = "1分後に再試行してください" }, statusCode: 429);
    attempts[ip] = (a.until, a.count + 1);
    var body = await c.Request.ReadFromJsonAsync<JsonObject>();
    if (!Broker.Secret((string?)body?["password"] ?? "", config.AdminPassword)) return Results.Json(new { error = "パスワードが一致しません" }, statusCode: 401);
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    sessions[token] = DateTime.UtcNow.AddHours(12);
    foreach (var old in sessions.Where(x => x.Value < DateTime.UtcNow).Select(x => x.Key).ToArray()) sessions.TryRemove(old, out _);
    c.Response.Cookies.Append("ocs_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = c.Request.IsHttps || config.PublicUrl.StartsWith("https://"), MaxAge = TimeSpan.FromHours(12) });
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/logout", (HttpContext c) => { if (c.Request.Cookies.TryGetValue("ocs_session", out var t)) sessions.TryRemove(t, out _); c.Response.Cookies.Delete("ocs_session"); return Results.Ok(); });
app.MapGet("/api/config", () => Results.Json(new { config.PublicUrl, config.ObsUrl, config.IceServers, slots = config.Slots.Select(s => new { s.Id, s.Name, code = EncodeCode(s), viewUrl = $"http://127.0.0.1:17640/view.html?slot={s.Id}#{s.ViewToken}" }) }));
string EncodeCode(Slot s) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { url = config.PublicUrl, slot = s.Id, token = s.SendToken })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
app.MapPost("/api/config", async (HttpContext c) =>
{
    var v = await c.Request.ReadFromJsonAsync<JsonObject>() ?? throw new Exception("設定がありません");
    var publicUrl = new Uri((string?)v["publicUrl"] ?? config.PublicUrl);
    if (publicUrl.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(publicUrl.UserInfo)) throw new Exception("接続URLは http:// または https:// で指定してください");
    var obsUrl = new Uri((string?)v["obsUrl"] ?? config.ObsUrl);
    if (obsUrl.Scheme is not ("ws" or "wss")) throw new Exception("OBS接続先は ws:// または wss:// です");
    var ice = v["iceServers"]?.Deserialize<IceServer[]>() ?? config.IceServers;
    if (ice.Length > 8 || ice.Any(i => !new[] { "stun:", "stuns:", "turn:", "turns:" }.Any(p => i.urls.StartsWith(p)))) throw new Exception("ICEサーバー設定を確認してください");
    config.PublicUrl = publicUrl.ToString().TrimEnd('/'); config.ObsUrl = obsUrl.ToString(); config.IceServers = ice;
    if (v["obsPassword"] is JsonValue p && !string.IsNullOrEmpty((string?)p)) config.ObsPassword = (string)p!;
    if (v["names"] is JsonArray names) for (int i = 0; i < Math.Min(6, names.Count); i++) config.Slots[i].Name = ((string?)names[i] ?? $"PC {i + 1}")[..Math.Min(((string?)names[i] ?? $"PC {i + 1}").Length, 48)];
    config.Save(); return Results.Ok(new { ok = true });
});
app.MapPost("/api/obs/connect", async (HttpContext context) =>
{
    var request = await context.Request.ReadFromJsonAsync<JsonObject>() ?? new();
    string url = (string?)request["url"] ?? config.ObsUrl;
    string password = (string?)request["password"] ?? "";
    if (string.IsNullOrEmpty(password)) password = config.ObsPassword;
    await obs.Connect(url, password);
    await obs.Call("SetStudioModeEnabled", new() { ["studioModeEnabled"] = true });
    var scenes = await obs.Call("GetSceneList");
    if (!obs.Connected) throw new Exception(obs.Error.Length > 0 ? obs.Error : "確認中にOBS接続が切れました");
    config.ObsUrl = url; config.ObsPassword = password; config.Save();
    return Results.Ok(new { ok = true, connected = true, version = obs.Version, endpoint = obs.Endpoint, scenes });
});
app.MapGet("/api/state", async () =>
{
    JsonObject? scenes = null, inputs = null; string? stateError = null;
    if (obs.Connected)
    {
        try { scenes = await obs.Call("GetSceneList"); inputs = await obs.Call("GetInputList"); } catch (Exception e) { stateError = e.Message; }
    }
    return Results.Json(new { connected = obs.Connected, error = stateError ?? obs.Error, endpoint = obs.Endpoint, version = obs.Version, slots = broker.Status(), scenes, inputs });
});
app.MapGet("/api/preview", async (string scene, int? width) => Results.Json(new { image = await obs.Screenshot(scene, width == 640 ? 640 : 320) }));
app.MapPost("/api/obs/action", async (HttpContext c) =>
{
    var b = await c.Request.ReadFromJsonAsync<JsonObject>() ?? throw new Exception("操作がありません");
    var data = b["data"] as JsonObject ?? new();
    string action = (string?)b["action"] ?? "";
    if (action is not ("SetCurrentPreviewScene" or "SetCurrentProgramScene" or "TriggerStudioModeTransition" or "SetStudioModeEnabled" or "SetInputMute" or "SetInputVolume" or "GetInputMute" or "GetInputVolume" or "GetStudioModeEnabled" or "GetSceneItemList" or "SetSceneItemEnabled" or "GetStreamStatus" or "GetRecordStatus" or "StartStream" or "StopStream" or "StartRecord" or "StopRecord" or "SetCurrentSceneTransition" or "SetCurrentSceneTransitionDuration" or "GetSceneTransitionList")) throw new Exception("未対応の操作です");
    if (action == "SetInputVolume") { double value = (double?)data["inputVolumeMul"] ?? 0; if (value is < 0 or > 2) throw new Exception("音量範囲外です"); }
    return Results.Json(await obs.Call(action, data));
});
app.MapPost("/api/obs/setup", async () =>
{
    var existing = await obs.Call("GetSceneList");
    var sceneNames = (existing["scenes"] as JsonArray ?? new()).Select(s => (string?)s?["sceneName"]).ToHashSet();
    var inputList = await obs.Call("GetInputList");
    var inputNames = (inputList["inputs"] as JsonArray ?? new()).Select(s => (string?)s?["inputName"]).ToHashSet();
    foreach (var slot in config.Slots)
    {
        string scene = $"OCS / PC {slot.Id:00}", input = $"OCS-Receiver-{slot.Id:00}";
        if (!sceneNames.Contains(scene)) await obs.Call("CreateScene", new() { ["sceneName"] = scene });
        if (!inputNames.Contains(input)) await obs.Call("CreateInput", new() { ["sceneName"] = scene, ["inputName"] = input, ["inputKind"] = "browser_source", ["inputSettings"] = new JsonObject { ["url"] = $"http://127.0.0.1:17640/view.html?slot={slot.Id}#{slot.ViewToken}", ["width"] = 1920, ["height"] = 1080, ["fps"] = 60, ["shutdown"] = false, ["restart_when_active"] = false, ["reroute_audio"] = true }, ["sceneItemEnabled"] = true });
        else
        {
            var items = await obs.Call("GetSceneItemList", new() { ["sceneName"] = scene });
            if (!(items["sceneItems"] as JsonArray ?? new()).Any(i => (string?)i?["sourceName"] == input)) await obs.Call("CreateSceneItem", new() { ["sceneName"] = scene, ["sourceName"] = input, ["sceneItemEnabled"] = true });
        }
    }
    return Results.Ok(new { ok = true });
});
app.Map("/signal", async c =>
{
    if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
    using var ws = await c.WebSockets.AcceptWebSocketAsync(); await broker.Handle(ws);
});
await app.RunAsync();
