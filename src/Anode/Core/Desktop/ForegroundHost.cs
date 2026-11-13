using System.Text.Json.Nodes;
using Anode.Core.Agents;
using Anode.Core.Bridge;
using Anode.Core.Display;
using Anode.Core.Input;
using Anode.Core.Launch;
using Anode.Core.Session;
using Anode.Core.Util;
using Anode.Seat;
using Win = Anode.Core.Native.Native;

namespace Anode.Core.Desktop;

/// <summary>One explicit lease on the signed-in console, independent of the child session.</summary>
internal static class ForegroundHost
{
    internal const string Mode = "foreground";
    private static string Pipe => $"evo-seat-foreground-{ChildSession.CurrentSessionId()}";
    private const string MutexName = @"Local\EVO.Seat.Foreground";

    private static readonly DesktopTools Desktop = new(DesktopWorker.CallForegroundAsync,
        owns: target => !WindowAccess.IsSeatViewer(new IntPtr(target.Handle)));
    private static readonly DesktopLease Lease = new(EndLease, record: Log.Info);

    internal static int Run()
    {
        Log.SetRole("foreground");
        SeatDisplay.UsePhysicalPixels();
        try { VerifyDesktop(); }
        catch (InvalidOperationException ex) { Log.Warn(ex.Message); return 3; }
        InputInjector.DesktopGuard = () => SeatDesktop.IsInteractiveDefault() && IsConsoleSession()
            ? null : "Windows switched away from the unlocked foreground desktop.";
        using var mutex = new Mutex(true, MutexName, out bool acquired);
        if (!acquired) return 0; // Another client started it while we were opening the pipe.
        using var server = new JsonPipeServer(Pipe, HandleAsync);
        server.Start();
        using var expiry = new System.Threading.Timer(_ =>
        {
            try { Lease.ExpireIdle(); }
            catch (Exception ex) { Log.Warn($"Foreground lease cleanup will retry: {ex.Message}"); }
        }, null, 1000, 1000);
        using var untilSignOut = new ManualResetEventSlim();
        untilSignOut.Wait();
        return 0;
    }

    internal static async Task<JsonPipeClient?> ConnectAsync(bool start, CancellationToken cancel)
    {
        VerifyDesktop();
        var client = await JsonPipeClient.TryConnectAsync(Pipe, 200, cancel).ConfigureAwait(false);
        if (client is not null || !start) return client;
        cancel.ThrowIfCancellationRequested();
        DetachedProcess.Start(Env.ExecutablePath, ["__foreground-host"], AppContext.BaseDirectory);
        return await JsonPipeClient.TryConnectAsync(Pipe, 5000, cancel).ConfigureAwait(false);
    }

    internal static void VerifyDesktop()
    {
        if (!IsConsoleSession() || !SeatDesktop.IsInteractiveDefault())
            throw new InvalidOperationException("Foreground mode needs the unlocked Windows console desktop.");
    }

    private static bool IsConsoleSession()
    {
        uint session = ChildSession.CurrentSessionId();
        return session is > 0 and < uint.MaxValue && session == Win.WTSGetActiveConsoleSessionId();
    }

    internal static bool Supports(string op) => op is "lease" or "status" or "screenshot" or "run"
        or "desktop.launch.status" or "input.click" or "input.move" or "input.drag"
        or "input.key" or "input.text" or "input.scroll"
        || DesktopTools.ToolName(op) is not null;

    private static void EndLease()
    {
        // Retry cleanup after unlock if a desktop switch interrupted an input hold.
        if (InputInjector.HasHeldInput)
        {
            VerifyDesktop();
            InputInjector.ReleaseHeld();
        }
        Desktop.InvalidateLease();
    }

    private static async Task<JsonObject> HandleAsync(JsonObject request, CancellationToken cancel)
    {
        string op = request.Str("op") ?? "";
        if (!Supports(op)) return JsonLine.Fail($"'{op}' is unavailable on the foreground desktop.");
        if (AgentAccess.Validate(request) is { } invalid) return JsonLine.Fail(invalid);
        if (op == "lease")
        {
            if (request.Str("action") == "acquire")
            {
                if (request.Str("mode") != Mode) return JsonLine.Fail("Select mode=foreground to acquire the foreground desktop.");
                VerifyDesktop();
            }
            var reply = await Lease.HandleAsync(request, DispatchAsync, cancel).ConfigureAwait(false);
            if (reply.Obj("result") is { } state) state["mode"] = Mode;
            reply.Remove("leaseStarted");
            return reply;
        }
        if (op == "status")
        {
            var status = await Lease.HandleAsync(new JsonObject
            {
                ["op"] = "lease", ["action"] = "status", ["agentId"] = request.Str("agentId")
            }, DispatchAsync, cancel).ConfigureAwait(false);
            if (status.Bool("ok") != true) return status;
            return JsonLine.Ok(new JsonObject
            {
                ["mode"] = Mode, ["state"] = "ready", ["session"] = ChildSession.CurrentSessionId(),
                ["screen"] = SeatDisplay.Measure().ToJson(), ["lease"] = status.Obj("result")?.DeepClone()
            });
        }
        return await Lease.HandleAsync(request, DispatchAsync, cancel, requireLease: true).ConfigureAwait(false);
    }

    private static async Task<JsonObject> DispatchAsync(JsonObject request, CancellationToken cancel)
    {
        VerifyDesktop();
        string op = request.Str("op")!;
        if (op == "desktop.launch.status") return Desktop.LaunchStatus(request.Str("launchId")!);
        if (DesktopTools.ToolName(op) is not null)
            return await Desktop.HandleAsync(op, request, cancel).ConfigureAwait(false);
        if (op == "run")
        {
            Desktop.InvalidateObservations();
            return SeatHost.RunProgram(request, ChildSession.CurrentSessionId(), rememberLaunch: Desktop.RememberLaunch);
        }
        if (op != "screenshot") Desktop.InvalidateObservations();
        var response = DesktopInput.Dispatch(op, request, cancel, foreground: true);
        if (response.Obj("result") is { } result) result["mode"] = Mode;
        return response;
    }
}
