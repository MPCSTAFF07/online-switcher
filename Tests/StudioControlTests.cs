using System.Text.Json.Nodes;

public static class StudioControlTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    public static async Task Run()
    {
        var delayed = new FakeObs { ApplyDelayReads = 2 };
        await new ObsStudioControl(delayed.Call).Execute("SetCurrentPreviewScene", new() { ["sceneName"] = "Scene B" });
        Check(delayed.EnableRequests == 1 && delayed.PreviewChanges == 1 && delayed.PreviewWhileDisabled == 0,
            "preview waits until asynchronous studio enable is observable");
        Check(delayed.ProgramChanges == 0, "preview selection never falls back to changing program output");

        var active = new FakeObs { Enabled = true };
        await new ObsStudioControl(active.Call).Execute("SetCurrentPreviewScene", new() { ["sceneName"] = "Scene B" });
        Check(active.EnableRequests == 0 && active.PreviewChanges == 1, "active studio mode is not toggled again");

        var race = new FakeObs { Enabled = true, RejectOnce = true };
        await new ObsStudioControl(race.Call).Execute("SetCurrentPreviewScene", new() { ["sceneName"] = "Scene B" });
        Check(race.PreviewAttempts == 2 && race.EnableRequests == 1 && race.PreviewChanges == 1,
            "506 caused by external disable is recovered once");

        var persistent = new FakeObs { Enabled = true, RejectAlways = true };
        try
        {
            await new ObsStudioControl(persistent.Call).Execute("SetCurrentPreviewScene");
            throw new Exception("Expected persistent 506 failure");
        }
        catch (ObsRequestException e) when (e.Code == 506)
        {
            Check(persistent.PreviewAttempts == 2 && e.Message.Contains("スタジオモード"),
                "persistent 506 stops after one retry and explains the required mode");
        }

        var missing = new FakeObs { Enabled = true, RejectCode = 600 };
        try
        {
            await new ObsStudioControl(missing.Call).Execute("SetCurrentPreviewScene");
            throw new Exception("Expected missing-scene failure");
        }
        catch (ObsRequestException e) when (e.Code == 600)
        {
            Check(missing.PreviewAttempts == 1 && missing.EnableRequests == 0, "non-506 failures are not retried");
        }

        var take = new FakeObs { Enabled = true, RejectOnce = true };
        await new ObsStudioControl(take.Call).Execute("TriggerStudioModeTransition");
        Check(take.TransitionAttempts == 2 && take.Transitions == 1, "TAKE recovers from 506 without executing the transition twice");

        var concurrent = new FakeObs { ApplyDelayReads = 2 };
        var serial = new ObsStudioControl(concurrent.Call);
        await Task.WhenAll(serial.Execute("SetCurrentPreviewScene"), serial.Execute("SetCurrentPreviewScene"));
        Check(concurrent.EnableRequests == 1 && concurrent.PreviewChanges == 2 && concurrent.PreviewWhileDisabled == 0,
            "concurrent selections share one confirmed studio activation");

        var blocked = new FakeObs { AlwaysDisabled = true };
        try
        {
            await new ObsStudioControl(blocked.Call).Execute("SetCurrentPreviewScene");
            throw new Exception("Expected studio activation timeout");
        }
        catch (InvalidOperationException e)
        {
            Check(blocked.PreviewAttempts == 0 && e.Message.Contains("ONにできません"),
                "failed studio activation stops before changing any scene");
        }
    }

    sealed class FakeObs
    {
        public bool Enabled, RejectOnce, RejectAlways, AlwaysDisabled;
        public int ApplyDelayReads, RejectCode, EnableRequests, PreviewAttempts, PreviewChanges, PreviewWhileDisabled;
        public int TransitionAttempts, Transitions, ProgramChanges;
        int pendingReads = -1;

        public async Task<JsonObject> Call(string type, JsonObject? data)
        {
            await Task.Yield();
            if (type == "GetStudioModeEnabled")
            {
                if (pendingReads == 0 && !AlwaysDisabled) { Enabled = true; pendingReads = -1; }
                else if (pendingReads > 0) pendingReads--;
                return new() { ["studioModeEnabled"] = Enabled };
            }
            if (type == "SetStudioModeEnabled")
            {
                if ((bool?)data?["studioModeEnabled"] != true) throw new Exception("Unexpected studio disable");
                EnableRequests++; pendingReads = ApplyDelayReads; return new();
            }
            if (type == "SetCurrentProgramScene") { ProgramChanges++; return new(); }
            if (type == "SetCurrentPreviewScene") PreviewAttempts++;
            if (type == "TriggerStudioModeTransition") TransitionAttempts++;
            if (RejectCode != 0) throw new ObsRequestException(type, RejectCode, "Scene not found");
            if (RejectOnce || RejectAlways)
            {
                RejectOnce = false; Enabled = false; pendingReads = -1;
                throw new ObsRequestException(type, 506, null);
            }
            if (!Enabled)
            {
                PreviewWhileDisabled++;
                throw new ObsRequestException(type, 506, null);
            }
            if (type == "SetCurrentPreviewScene") PreviewChanges++;
            if (type == "TriggerStudioModeTransition") Transitions++;
            return new();
        }
    }
}
