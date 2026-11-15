using System.Security.Cryptography;
using System.Text.Json;

public sealed class AppConfig
{
    public string AdminPassword { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
    public string PublicUrl { get; set; } = "http://localhost:17640";
    public string ObsUrl { get; set; } = "ws://127.0.0.1:4455";
    public string ObsPassword { get; set; } = "";
    public IceServer[] IceServers { get; set; } = [new() { urls = "stun:stun.l.google.com:19302" }];
    public Slot[] Slots { get; set; } = Enumerable.Range(1, 6).Select(i => new Slot { Id = i, Name = $"PC {i:00}" }).ToArray();
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");
    public static AppConfig Load()
    {
        if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? throw new Exception("config.json が不正です");
        var c = new AppConfig(); c.Save(); return c;
    }
    public void Save()
    {
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
public sealed class IceServer
{
    public string urls { get; set; } = "";
    public string? username { get; set; }
    public string? credential { get; set; }
}
public sealed class Slot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string SendToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string ViewToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
}
