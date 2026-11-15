using System.Text.Json.Nodes;

// OBS can acknowledge SetStudioModeEnabled before the frontend has applied it.
// Verify the observed state before any preview/transition request.
public sealed class ObsStudioControl(Func<string, JsonObject?, Task<JsonObject>> request)
{
    readonly SemaphoreSlim switching = new(1);

    public async Task EnsureEnabled()
    {
        await switching.WaitAsync();
        try { await EnsureEnabledCore(false); }
        finally { switching.Release(); }
    }

    public async Task<JsonObject> Execute(string type, JsonObject? data = null)
    {
        if (type is not ("SetCurrentPreviewScene" or "TriggerStudioModeTransition"))
            throw new ArgumentException("Unsupported studio operation", nameof(type));

        await switching.WaitAsync();
        try
        {
            await EnsureEnabledCore(false);
            try { return await request(type, data); }
            catch (ObsRequestException error) when (error.Code == 506)
            {
                // The user or another client may have disabled it after our check.
                // A rejected 506 has not performed the operation; retry it only once.
                await EnsureEnabledCore(true);
                return await request(type, data);
            }
        }
        finally { switching.Release(); }
    }

    async Task EnsureEnabledCore(bool forceEnable)
    {
        var state = await request("GetStudioModeEnabled", null);
        if (!forceEnable && (bool?)state["studioModeEnabled"] == true) return;

        await request("SetStudioModeEnabled", new() { ["studioModeEnabled"] = true });
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            state = await request("GetStudioModeEnabled", null);
            if ((bool?)state["studioModeEnabled"] == true) return;
            await Task.Delay(75);
        } while (deadline.Elapsed < TimeSpan.FromSeconds(3));

        throw new InvalidOperationException("OBSのスタジオモードをONにできませんでした。OBS右下の「スタジオモード」をONにして再試行してください。");
    }
}
