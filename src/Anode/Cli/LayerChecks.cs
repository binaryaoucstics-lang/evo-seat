using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Desktop;
using Anode.Core.Layers;

namespace Anode.Cli;

/// <summary>Layer routing checks use synthetic windows; no Win32 discovery, capture or input.</summary>
internal static class LayerChecks
{
    private static void Require(bool test, string message)
    {
        if (!test) throw new InvalidOperationException(message);
    }

    private static async Task Rejected(Func<Task<JsonObject>> action)
    {
        try
        {
            // Validation failures use an explicit failure envelope; stale references and
            // runtime layer checks throw. Both are rejections, neither may reach a worker.
            if ((await action().ConfigureAwait(false)).Bool("ok") == false) return;
        }
        catch (InvalidOperationException) { return; }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("A layer accepted an out-of-scope reference or coordinate.");
    }

    public static async Task<string> RegionLayouts()
    {
        var dual = LayerHub.Regions(new Size(2880, 900), 2);
        var quad = LayerHub.Regions(new Size(2880, 1800), 4);
        var twelve = LayerHub.Regions(new Size(5760, 2700), 12);
        Require(dual.SequenceEqual(new[] { new Rectangle(0, 0, 1440, 900), new Rectangle(1440, 0, 1440, 900) }), "Default dual layout changed.");
        Require(quad.SequenceEqual(new[] { new Rectangle(0, 0, 1440, 900), new Rectangle(1440, 0, 1440, 900), new Rectangle(0, 900, 1440, 900), new Rectangle(1440, 900, 1440, 900) }), "Four-region placement is incorrect.");
        Require(twelve.Length == 12 && twelve[3].Location == new Point(4320, 0) && twelve[4].Location == new Point(0, 900)
            && twelve[11].Location == new Point(4320, 1800) && twelve.All(r => r.Size == new Size(1440, 900)), "Twelve-region placement is incorrect.");
        Require(quad.All(r => quad.Count(other => r.IntersectsWith(other)) == 1)
            && twelve.All(r => twelve.Count(other => r.IntersectsWith(other)) == 1), "Regions overlap.");
        foreach (int count in new[] { 0, 1, 3, 5, 11, 13 })
        {
            bool refused = false;
            try { LayerHub.Regions(new Size(2880, 1800), count); } catch (ArgumentException) { refused = true; }
            Require(refused, "An unsupported region count was accepted.");
        }
        foreach (var (size, count) in new[] { (new Size(2880, 900), 4), (new Size(5119, 2700), 12), (new Size(5760, 2159), 12) })
        {
            bool small = false;
            try { LayerHub.Regions(size, count); } catch (ArgumentException) { small = true; }
            Require(small, $"{count} regions accepted an undersized display.");
        }
        Require(LayerHub.StartupArguments(4).Last() == "1800" && LayerHub.StartupArguments(2).Last() == "900"
            && LayerHub.StartupArguments(0).SequenceEqual(new[] { "--hidden" })
            && LayerHub.StartupArguments(12).SequenceEqual(new[] { "--hidden", "--width", "5760", "--height", "2700" }), "Startup dimensions are incorrect.");
        foreach (int count in new[] { 2, 4, 12 })
            Require(Mcp.Tools.ValidateArguments("seat_lease", new() { ["action"] = "acquire", ["mode"] = "layered", ["layerCount"] = count }) is null,
                $"The schema rejected {count} regions.");
        foreach (var invalid in new[] {
            new JsonObject { ["action"] = "renew", ["layerCount"] = 4 },
            new JsonObject { ["action"] = "acquire", ["mode"] = "exclusive", ["layerCount"] = 4 },
            new JsonObject { ["action"] = "acquire", ["mode"] = "layered", ["layerCount"] = 3 } })
            Require(Mcp.Tools.ValidateArguments("seat_lease", invalid) is not null, "An invalid layout request passed schema validation.");
        var target = new WindowTarget(100, 10, 1000);
        Task<JsonObject> Worker(JsonObject request, CancellationToken _)
        {
            JsonObject window = new() { ["target"] = target.ToJson(), ["pid"] = 10, ["title"] = "bottom right", ["bounds"] = WindowAccess.RectangleJson(new Rectangle(1450, 920, 1000, 700)) };
            if (request.Str("op") == "windows") return Task.FromResult(new JsonObject { ["windows"] = new JsonArray(window) });
            if (request.Str("op") == "observe") return Task.FromResult(new JsonObject { ["window"] = window, ["elements"] = new JsonArray(new JsonObject {
                ["id"] = "e0", ["role"] = "Edit", ["actions"] = new JsonArray("focus"), ["bounds"] = WindowAccess.RectangleJson(new Rectangle(1470, 940, 200, 80)),
                ["path"] = new JsonArray(0), ["runtimeId"] = new JsonArray(1, 2), ["processId"] = 10,
                ["nativeWindow"] = 101, ["nativeReference"] = true }) });
            Require(request.Obj("element")?.Obj("bounds")?.Int("x") == 1470 && request.Obj("element")?.Obj("bounds")?.Int("y") == 940, "Bottom-row private geometry was not global.");
            Require(request.Obj("element")?.Int("nativeWindow") == 101, "Native control identity did not reach the action worker.");
            Require(DesktopWorker.UsesNativeReference(request), "The native snapshot did not select the native action worker.");
            return Task.FromResult(new JsonObject { ["note"] = "focused" });
        }
        var bottom = new DesktopTools(Worker, quad[3], t => t == target);
        var list = (await bottom.HandleAsync("desktop.windows", new()).ConfigureAwait(false)).Obj("result")!["windows"]!.AsArray();
        var observation = (await bottom.HandleAsync("desktop.observe", new() { ["windowId"] = list[0]!["windowId"]!.GetValue<string>(), ["includeScreenshot"] = false }).ConfigureAwait(false)).Obj("result")!;
        Require(observation["elements"]![0]!["bounds"]!["x"]!.GetValue<int>() == 30 && observation["elements"]![0]!["bounds"]!["y"]!.GetValue<int>() == 40, "Bottom row was not localized on both axes.");
        Require(observation["elements"]![0]!["nativeWindow"] is null && observation["elements"]![0]!["nativeReference"] is null,
            "Native control identity leaked into a public observation.");
        await bottom.HandleAsync("desktop.element", new() { ["snapshotId"] = observation.Str("snapshotId"), ["elementId"] = "e0", ["action"] = "focus" }).ConfigureAwait(false);
        return "2, 4, and 12 nonoverlapping regions, display/count/schema rejection, startup sizes and bottom-row public/private geometry; no desktop I/O";
    }

    public static async Task<string> InputScheduling()
    {
        var write = new JsonObject { ["action"] = "set_value" };
        Require(LayerHub.IsValueWrite("desktop.element", write) && !LayerHub.UsesForeground("desktop.element", write),
            "A value write took an exclusive foreground turn.");
        Require(LayerHub.IsValueWrite("desktop.element", new JsonObject { ["action"] = "set_values" }), "A value batch took a foreground turn.");
        foreach (string action in new[] { "focus", "invoke", "expand", "toggle" })
            Require(LayerHub.UsesForeground("desktop.element", new JsonObject { ["action"] = action }), $"{action} could change focus during another layer's input.");
        Require(LayerHub.UsesForeground("input.key", new()) && !LayerHub.UsesForeground("run", new())
            && !LayerHub.UsesForeground("input.position", new()), "An input, launch or position took the wrong turn.");
        Require(!LayerHub.UsesForeground("desktop.window", new JsonObject { ["action"] = "maximize" })
            && LayerHub.UsesForeground("desktop.window", new JsonObject { ["action"] = "focus" }),
            "Fitting an owned window should stay in its layer, while changing focus needs the seat.");

        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var gate = new LayerActionGate();
        await gate.EnterAsync(true, limit.Token);
        await gate.EnterAsync(true, limit.Token);
        var foreground = gate.EnterAsync(false, limit.Token);
        var laterWrite = gate.EnterAsync(true, limit.Token);
        gate.Exit(true);
        Require(!foreground.IsCompleted && !laterWrite.IsCompleted, "A foreground turn overlapped a write, or a later write jumped the queue.");
        gate.Exit(true);
        await foreground;
        Require(!laterWrite.IsCompleted, "A value write overlapped the foreground turn.");
        gate.Exit(false);
        await laterWrite;

        using var abandon = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(false, abandon.Token);
        abandon.Cancel();
        try { await cancelled; }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested) { }
        Require(cancelled.IsCanceled, "A cancelled foreground request acquired a turn.");
        await gate.EnterAsync(true, limit.Token);
        gate.Exit(true);
        gate.Exit(true);
        await gate.EnterAsync(false, limit.Token);
        gate.Exit(false);
        return "value writes overlap; foreground stays exclusive, gets its turn, and can cancel without blocking the queue";
    }

    public static async Task<string> BatchWrites()
    {
        var target = new WindowTarget(100, 10, 1000);
        int writes = 0;
        Task<JsonObject> Worker(JsonObject request, CancellationToken _)
        {
            if (request.Str("op") == "windows") return Task.FromResult(new JsonObject { ["windows"] = new JsonArray(new JsonObject { ["target"] = target.ToJson() }) });
            if (request.Str("op") == "observe") return Task.FromResult(new JsonObject { ["elements"] = new JsonArray(
                new JsonObject { ["id"] = "e0", ["actions"] = new JsonArray("set_value") },
                new JsonObject { ["id"] = "e1", ["actions"] = new JsonArray("set_value") },
                new JsonObject { ["id"] = "e2", ["actions"] = new JsonArray("invoke") }) });
            writes++;
            Require(request.Str("action") == "set_values" && WindowTarget.FromJson(request.Obj("target")!) == target,
                "The batch lost its action or window identity.");
            var edits = request["edits"]!.AsArray();
            Require(edits.Count == 2 && edits[0]!.AsObject().Obj("element")?.Str("id") == "e0"
                && edits[1]!.AsObject().Obj("element")?.Str("id") == "e1", "The worker did not receive both resolved controls in order.");
            return Task.FromResult(new JsonObject { ["completed"] = new JsonArray(new JsonObject { ["elementId"] = "e0" }),
                ["failedElementId"] = "e1", ["batchError"] = "Synthetic provider failed on the second write." });
        }
        var left = new DesktopTools(Worker, new Rectangle(0, 0, 1440, 900), _ => true);
        var right = new DesktopTools(Worker, new Rectangle(1440, 0, 1440, 900), _ => true);
        var windows = (await left.HandleAsync("desktop.windows", new())).Obj("result")!["windows"]!.AsArray();
        async Task<string> Snapshot() => (await left.HandleAsync("desktop.observe", new JsonObject {
            ["windowId"] = windows[0]!["windowId"]!.GetValue<string>(), ["includeScreenshot"] = false })).Obj("result")!.Str("snapshotId")!;
        JsonObject Batch(string snapshot, string second) => new() { ["snapshotId"] = snapshot, ["action"] = "set_values", ["values"] = new JsonArray(
            new JsonObject { ["elementId"] = "e0", ["value"] = "first" }, new JsonObject { ["elementId"] = second, ["value"] = "second" }) };
        var snapshot = await Snapshot();
        await Rejected(() => right.HandleAsync("desktop.element", Batch(snapshot, "e1")));
        await Rejected(() => left.HandleAsync("desktop.element", Batch(snapshot, "e0")));
        await Rejected(() => left.HandleAsync("desktop.element", Batch(snapshot, "e2")));
        string missing = await Snapshot();
        await Rejected(() => left.HandleAsync("desktop.element", Batch(missing, "foreign")));
        Require(writes == 0, "An invalid batch reached the worker.");
        snapshot = await Snapshot();
        var result = await left.HandleAsync("desktop.element", Batch(snapshot, "e1"));
        Require(result.Bool("ok") == false && result.Str("failedElementId") == "e1" && result["completed"]?.AsArray().Count == 1,
            "The caller lost the partial result after a failed field.");
        await Rejected(() => left.HandleAsync("desktop.element", Batch(snapshot, "e1")));
        Require(writes == 1, "A consumed batch snapshot could be replayed.");
        return "one-window batches validate all controls up front, refuse foreign/replayed references and report partial failure";
    }

    public static string SessionLookup()
    {
        uint session = Core.Session.ChildSession.CurrentSessionId();
        Require(Core.Session.ChildSession.ProcessInSession(Environment.ProcessId, session), "Current process lost its real session.");
        Require(!Core.Session.ChildSession.ProcessInSession(Environment.ProcessId, session == 0 ? 1u : 0u), "Another session accepted the current process.");
        foreach (int pid in new[] { 0, -1, int.MaxValue })
            Require(!Core.Session.ChildSession.ProcessInSession(pid, session), "Invalid/absent PID passed the session guard.");
        Require(!Core.Session.ChildSession.ProcessInSession(Environment.ProcessId, uint.MaxValue), "Unknown expected session passed the guard.");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Require((uint)process.SessionId == session, "Native session lookup disagreed with the original process API.");
        return "fresh native session matches original API; mismatched/unknown session and invalid/absent PIDs fail closed; no desktop input";
    }

    public static async Task<string> DesktopIsolation()
    {
        Require(!LayerWindows.MatchesIdentity(new WindowTarget(100, 11, 1000), 10, 1000), "Equal creation times allowed another PID.");
        Require(!LayerWindows.MatchesIdentity(new WindowTarget(100, 10, 1001), 10, 1000), "PID reuse passed its original creation time.");
        var a = new WindowTarget(100, 10, 1000);
        var b = new WindowTarget(200, 20, 2000);
        bool ownsB = true;
        var calls = new List<JsonObject>();
        var actionTargets = new List<int>();

        JsonObject Window(WindowTarget target)
        {
            int x = target == a ? 10 : 1450;
            return new JsonObject { ["target"] = target.ToJson(), ["pid"] = target.Pid,
                ["process"] = "fixture", ["title"] = target == a ? "A only" : "B only",
                ["bounds"] = WindowAccess.RectangleJson(new Rectangle(x, 20, 1000, 700)) };
        }

        Task<JsonObject> Worker(JsonObject request, CancellationToken _)
        {
            calls.Add((JsonObject)request.DeepClone());
            string op = request.Str("op")!;
            if (op == "windows") return Task.FromResult(new JsonObject { ["windows"] = new JsonArray(Window(a), Window(b)) });
            var target = WindowTarget.FromJson(request.Obj("target")!);
            if (op == "observe")
            {
                int x = target == a ? 30 : 1470;
                return Task.FromResult(new JsonObject { ["window"] = Window(target), ["elements"] = new JsonArray(
                    new JsonObject { ["id"] = "e0", ["name"] = "Text", ["role"] = "Edit", ["automationId"] = "text",
                        ["bounds"] = WindowAccess.RectangleJson(new Rectangle(x, 40, 200, 80)),
                        ["actions"] = new JsonArray("invoke"), ["path"] = new JsonArray(0),
                        ["runtimeId"] = new JsonArray(1, 2), ["processId"] = target.Pid }) });
            }
            if (op == "element")
            {
                actionTargets.Add(target.Pid);
                // The public observation is local, but private worker references must retain
                // their original global geometry and UIA identity after that projection.
                Require(request.Obj("element")?.Obj("bounds")?.Int("x") == (target == a ? 30 : 1470),
                    "Layer projection changed private UIA snapshot geometry.");
                Require(request.Obj("element")?["runtimeId"] is JsonArray { Count: 2 }, "Layer projection lost private UIA references.");
                return Task.FromResult(new JsonObject { ["note"] = "invoked" });
            }
            if (op == "window" && request.Str("action") == "raise")
                return Task.FromResult(new JsonObject { ["coveredBy"] = Window(a), ["note"] = "covered by A only" });
            if (op == "window") return Task.FromResult(new JsonObject { ["requested"] = request.Str("action") });
            throw new InvalidOperationException("Unexpected synthetic worker operation.");
        }

        int directLists = 0;
        var left = new DesktopTools(Worker, new Rectangle(0, 0, 1440, 900), t => t == a,
            () => { directLists++; return new JsonObject { ["windows"] = new JsonArray(Window(a), Window(b)) }; });
        var right = new DesktopTools(Worker, new Rectangle(1440, 0, 1440, 900), t => ownsB && t == b);
        var aw = (await left.HandleAsync("desktop.windows", new()).ConfigureAwait(false)).Obj("result")!["windows"]!.AsArray();
        var bw = (await right.HandleAsync("desktop.windows", new()).ConfigureAwait(false)).Obj("result")!["windows"]!.AsArray();
        Require(directLists == 1 && calls.Count == 1, "Direct layer listing started an inspection worker.");
        Require(aw.Count == 1 && aw[0]!["pid"]!.GetValue<int>() == a.Pid
            && bw.Count == 1 && bw[0]!["pid"]!.GetValue<int>() == b.Pid, "Layer window enumeration exposed another layer's window.");
        Require(bw[0]!["bounds"]!["x"]!.GetValue<int>() == 10, "Right-layer window bounds were not localized.");
        string aWindow = aw[0]!["windowId"]!.GetValue<string>(), bWindow = bw[0]!["windowId"]!.GetValue<string>();
        JsonObject Observe(string id) => new() { ["windowId"] = id, ["includeScreenshot"] = false };
        var ao = (await left.HandleAsync("desktop.observe", Observe(aWindow)).ConfigureAwait(false)).Obj("result")!;
        var staleA = (await left.HandleAsync("desktop.observe", Observe(aWindow)).ConfigureAwait(false)).Obj("result")!;
        var bo = (await right.HandleAsync("desktop.observe", new() { ["windowId"] = bWindow }).ConfigureAwait(false)).Obj("result")!;
        Require(bo["screenshot"] is null && bo["screenshotError"] is null, "An omitted layer screenshot attempted capture.");
        Require(bo["elements"]![0]!["bounds"]!["x"]!.GetValue<int>() == 30
            && bo["elements"]![0]!["runtimeId"] is null, "Public layer controls leaked global coordinates or UIA references.");
        JsonObject Invoke(JsonObject observation) => new() { ["snapshotId"] = observation.Str("snapshotId"), ["elementId"] = "e0", ["action"] = "invoke" };

        int beforeCross = calls.Count;
        await Rejected(() => right.HandleAsync("desktop.observe", Observe(aWindow))).ConfigureAwait(false);
        await Rejected(() => right.HandleAsync("desktop.element", Invoke(ao))).ConfigureAwait(false);
        Require(calls.Count == beforeCross, "A foreign reference reached the layer's worker.");
        await left.HandleAsync("desktop.element", Invoke(ao)).ConfigureAwait(false);
        await Rejected(() => left.HandleAsync("desktop.element", Invoke(staleA))).ConfigureAwait(false);
        // A's successful action invalidates all A observations, but B's earlier snapshot survives.
        await right.HandleAsync("desktop.element", Invoke(bo)).ConfigureAwait(false);
        Require(actionTargets.SequenceEqual(new[] { a.Pid, b.Pid }), "One layer's action invalidated or redirected the other layer's snapshot.");

        await right.HandleAsync("desktop.window", new() { ["windowId"] = bWindow, ["action"] = "move", ["x"] = 10,
            ["y"] = 20, ["width"] = 200, ["height"] = 100 }).ConfigureAwait(false);
        Require(calls[^1].Int("x") == 1450 && calls[^1].Int("y") == 20, "Layer-local window movement was not translated globally.");
        int beforeOutside = calls.Count;
        await Rejected(() => right.HandleAsync("desktop.window", new() { ["windowId"] = bWindow, ["action"] = "move", ["x"] = 1439,
            ["y"] = 0, ["width"] = 2, ["height"] = 100 })).ConfigureAwait(false);
        // Valid schema dimensions, but the right edge exceeds the 1440-pixel tile:
        // this must exercise the layer check rather than only schema validation.
        await Rejected(() => right.HandleAsync("desktop.window", new() { ["windowId"] = bWindow, ["action"] = "move", ["x"] = 1300,
            ["y"] = 0, ["width"] = 160, ["height"] = 100 })).ConfigureAwait(false);
        Require(calls.Count == beforeOutside, "An out-of-layer window move reached the worker.");
        var raised = (await right.HandleAsync("desktop.window", new() { ["windowId"] = bWindow, ["action"] = "raise" }).ConfigureAwait(false)).Obj("result")!;
        Require(raised["coveredBy"] is null && !raised.ToJsonString().Contains("A only"), "A cross-layer covering window leaked its title or reference.");
        int beforeMaximize = calls.Count;
        await right.HandleAsync("desktop.window", new() { ["windowId"] = bWindow, ["action"] = "maximize" }).ConfigureAwait(false);
        Require(calls.Count == beforeMaximize + 1 && calls[^1].Str("action") == "fit_layer"
            && calls[^1].Int("x") == 1440 && calls[^1].Int("width") == 1440 && calls[^1].Int("height") == 900,
            "Layer maximize requested whole-seat maximization.");
        ownsB = false;
        int beforeRevoked = calls.Count;
        await Rejected(() => right.HandleAsync("desktop.window", new() { ["windowId"] = bWindow, ["action"] = "close" })).ConfigureAwait(false);
        Require(calls.Count == beforeRevoked, "An ownership change did not revoke the stored layer window reference.");
        return "separate snapshots, foreign reference rejection, ownership revocation, local/global geometry and bounded maximize; no live desktop accessed";
    }
}
