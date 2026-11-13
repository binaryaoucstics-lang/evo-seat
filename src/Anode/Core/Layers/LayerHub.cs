using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Anode.Core.Agents;
using Anode.Core.Bridge;
using Anode.Core.Capture;
using Anode.Core.Desktop;
using Anode.Core.Display;
using Anode.Core.Input;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Core.Layers;

/// <summary>Opt-in partitioned desktop. Region lifetimes are independent; only physical input is shared.</summary>
internal sealed class LayerHub
{
    private sealed class Context
    {
        public readonly string Id, Token;
        public readonly int Slot;
        public readonly LayerWindows Windows;
        public readonly DesktopWorker.Session Worker = new();
        private readonly DesktopWorker.Session _nativeObserver = new();
        private readonly SemaphoreSlim _formReads;
        private WindowTarget? _managedOnly;
        private bool _nativeSnapshot;
        public readonly DesktopTools Desktop;
        public Context(string id, string token, int slot, LayerWindows windows, SemaphoreSlim formReads)
        {
            Id = id; Token = token; Slot = slot; Windows = windows;
            _formReads = formReads;
            Desktop = new(CallWorkerAsync, windows.Bounds, windows.Owns, windows.DescribeWindows);
        }

        private async Task<JsonObject> CallWorkerAsync(JsonObject request, CancellationToken cancel)
        {
            if (request.Str("op") == "observe")
            {
                var target = WindowTarget.FromJson(request.Obj("target")!);
                if (target != _managedOnly && NativeFormObservation.IsWinForms(new IntPtr(target.Handle)))
                {
                    // Initializing the managed UIA proxies also affects native COM queries in that process.
                    var native = (JsonObject)request.DeepClone();
                    native["op"] = "observe_native";
                    await _formReads.WaitAsync(cancel).ConfigureAwait(false);
                    JsonObject view;
                    try { view = await _nativeObserver.CallAsync(native, cancel).ConfigureAwait(false); }
                    finally { _formReads.Release(); }
                    if (view.Bool("useManaged") != true)
                    {
                        _nativeSnapshot = true;
                        return view;
                    }
                    _managedOnly = target;
                }
            }
            var worker = DesktopWorker.UsesNativeReference(request) ? _nativeObserver : Worker;
            return await worker.CallAsync(request, cancel, request.Str("op") switch
            {
                // Some legacy UIA providers focus controls by injecting a hotkey.
                // Bound the turn even if the provider never returns.
                "element" => TimeSpan.FromMilliseconds(2500),
                "window" when request.Str("action") is "focus" or "raise" => TimeSpan.FromSeconds(1),
                _ => null
            }).ConfigureAwait(false);
        }

        internal void CloseWorkers()
        {
            try { _nativeObserver.Dispose(); }
            finally { Worker.Dispose(); }
        }

        internal Task WarmAsync(string op, CancellationToken cancel) =>
            (op == "desktop.element" && _nativeSnapshot ? _nativeObserver : Worker).WarmAsync(cancel);
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly CancellationTokenSource Stop = new();
        public readonly LayerClipboard Clipboard = new();
        public Task<JsonObject>? Closing;
        public long RetryCloseAt;
        public long Expires;
        public int TtlMs = 120000, Active;
        public int? ClientPid;
        public long ClientStarted;
        public Point Pointer = new(60, 60);
        public WindowTarget? SelectedWindow;
    }
    private readonly object _state = new();
    private readonly Dictionary<string, Context> _layers = new();
    private Size _layoutScreen;
    private Rectangle[] _regions = [];
    private bool _automaticLayout;
    private readonly LayerActionGate _layout = new();
    private readonly LayerActionGate _input = new();
    // Concurrent WinForms proxy queries contend in the same Windows desktop.
    // Two permits keep an independent read available if a single provider hangs.
    private readonly SemaphoreSlim _formReads = new(2, 2);
    public bool Any { get { lock (_state) return _layers.Count > 0; } }
    public bool Contains(string? id) { lock (_state) return id is not null && _layers.ContainsKey(id); }
    public JsonObject Snapshot()
    {
        lock (_state)
        {
            bool active = _layers.Count > 0;
            bool displayChanged = active && _layoutScreen != InputInjector.ScreenSize();
            var regions = new JsonArray();
            if (active && !displayChanged)
            {
                var occupied = _layers.Values.Select(layer => layer.Slot).ToHashSet();
                for (int slot = 0; slot < _regions.Length; slot++)
                {
                    var region = WindowAccess.RectangleJson(_regions[slot]);
                    region["number"] = slot + 1;
                    region["occupied"] = occupied.Contains(slot);
                    regions.Add(region);
                }
            }
            return new JsonObject
            {
                ["count"] = active ? _regions.Length : 0,
                ["owners"] = new JsonArray(_layers.Keys.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["closing"] = _layers.Values.Count(c => c.Closing is not null),
                ["displayChanged"] = displayChanged,
                ["regions"] = regions
            };
        }
    }

    internal const string InvalidLayerCount = "layerCount must be 2, 4, or 12.";
    internal const int DefaultKeyHoldMs = 20;
    internal const int DefaultTypeIntervalMs = 8;

    private static (int Columns, int Rows) Grid(int count) => count switch
    {
        2 => (2, 1),
        4 => (2, 2),
        12 => (4, 3),
        _ => default
    };

    internal static bool SupportsLayout(int count) => Grid(count).Columns != 0;

    internal static Size DefaultLayoutSize(int count)
    {
        var (columns, rows) = Grid(count);
        return new Size(Daemon.SeatOptions.DefaultWidth * columns, Daemon.SeatOptions.DefaultHeight * rows);
    }

    internal static bool FitsLayout(Size screen, int count)
    {
        var (columns, rows) = Grid(count);
        return columns > 0 && screen.Width >= 1280 * columns && screen.Height >= 720 * rows;
    }

    internal static Rectangle[] Regions(Size screen, int count)
    {
        var (columns, rows) = Grid(count);
        if (columns == 0) throw new ArgumentException(InvalidLayerCount);
        if (!FitsLayout(screen, count))
            throw new ArgumentException($"{count} layers need at least {1280 * columns}x{720 * rows}. "
                + $"Start at {1440 * columns}x{900 * rows} for {count} 1440x900 regions.");
        int width = screen.Width / columns, height = screen.Height / rows;
        return Enumerable.Range(0, count).Select(slot => new Rectangle(slot % columns * width, slot / columns * height, width, height)).ToArray();
    }

    internal static Rectangle[] ExpandedRegions(Rectangle[] current, Size size, int count) =>
        [.. current, .. Regions(size, count).Except(current)];

    internal static string[] StartupArguments(int count)
    {
        var size = DefaultLayoutSize(count);
        return size.IsEmpty ? ["--hidden"] : ["--hidden", "--width", $"{size.Width}", "--height", $"{size.Height}"];
    }

    public static JsonObject Failure(string code, string message)
    {
        var r = JsonLine.Fail(message); r["errorCode"] = code; return r;
    }
    private JsonObject State(Context c, bool token = false) => new()
    {
        ["mode"] = "layered", ["agentId"] = c.Id, ["ownerAgentId"] = c.Id,
        ["leaseToken"] = token ? c.Token : null, ["slot"] = c.Slot, ["layerCount"] = _regions.Length,
        ["expiresInMs"] = Math.Max(0, c.Expires - Environment.TickCount64),
        ["region"] = new JsonObject { ["x"] = c.Windows.Bounds.X, ["y"] = c.Windows.Bounds.Y,
            ["width"] = c.Windows.Bounds.Width, ["height"] = c.Windows.Bounds.Height },
        ["summary"] = "You own an independent layer. Coordinates are layer-local; physical input takes short turns. Release closes only your registered apps."
    };
    public async Task<JsonObject> ManageAsync(JsonObject request)
    {
        var (state, closing) = Manage(request);
        return state ?? await closing!.ConfigureAwait(false);
    }

    // The seat host serializes lease acquisition and mode changes. Hold the layout gate
    // across the live resize so existing actions finish at their old geometry first.
    internal async Task<JsonObject?> GrowForAcquireAsync(JsonObject request, SeatDisplay display,
        CancellationToken cancel, CancellationToken stopping)
    {
        int next;
        Size previous;
        Rectangle[] regions;
        lock (_state)
        {
            if (request.Int("layerCount") is not null || !_automaticLayout || _layers.Count < _regions.Length
                || _layers.ContainsKey(request.Str("agentId")!) || _layers.Values.Any(layer => layer.Closing is not null)) return null;
            next = _regions.Length switch { 2 => 4, 4 => 12, _ => 0 };
            if (next == 0 || InputInjector.ScreenSize() != _layoutScreen) return null;
            previous = _layoutScreen;
            regions = _regions;
        }

        await _layout.EnterAsync(shared: false, cancel).ConfigureAwait(false);
        try
        {
            cancel.ThrowIfCancellationRequested();
            lock (_state)
                if (_layers.Count < regions.Length || _layers.Values.Any(layer => layer.Closing is not null)) return null;
            var size = DefaultLayoutSize(next);
            // Complete or restore the display change even if the joining client disconnects.
            var result = await display.SetAsync(new JsonObject { ["width"] = size.Width, ["height"] = size.Height },
                stopping, liveOnly: true).ConfigureAwait(false);
            if (InputInjector.ScreenSize() != size)
            {
                await display.SetAsync(new JsonObject { ["width"] = previous.Width, ["height"] = previous.Height },
                    stopping, liveOnly: true).ConfigureAwait(false);
                return Failure("layer_display", result.Str("error") ?? "Windows could not expand the desktop live.");
            }

            lock (_state)
            {
                // Keep each existing slot and all its pixel coordinates. The new cells follow those slots.
                _regions = ExpandedRegions(regions, size, next);
                _layoutScreen = size;
            }
            Log.Info($"layer layout expanded from {regions.Length} to {next} at {size.Width}x{size.Height}");
            return null;
        }
        finally { _layout.Exit(shared: false); }
    }

    private (JsonObject? State, Task<JsonObject>? Closing) Manage(JsonObject request)
    {
        lock (_state)
        {
            string id = request.Str("agentId")!;
            string action = request.Str("action") ?? "status";
            _layers.TryGetValue(id, out var c);
            if (action == "acquire")
            {
                int? requestedCount = request.Int("layerCount");
                int count = requestedCount ?? (_layers.Count > 0 ? _regions.Length : 2);
                if (!SupportsLayout(count)) return (Failure("layer_layout", InvalidLayerCount), null);
                if (_layers.Count > 0 && _regions.Length != count)
                    return (Failure("layer_layout", "Release all regions before changing the layout."), null);
                var screen = InputInjector.ScreenSize();
                if (_layers.Count > 0 && screen != _layoutScreen)
                    return (Failure("layer_display", "The active layout display size changed. Restore it or release the layers."), null);
                if (c?.Closing is not null) return (Failure("seat_busy", "This layer is still closing."), null);
                bool leaseStarted = c is null;
                if (c is null)
                {
                    if (_layers.Count >= count) return (Failure("seat_busy", $"All {count} layers are occupied."), null);
                    if (_layers.Count == 0)
                    {
                        try { _regions = Regions(screen, count); }
                        catch (ArgumentException ex) { return (Failure("layer_display", ex.Message), null); }
                        _layoutScreen = screen;
                        _automaticLayout = requestedCount is null && screen == DefaultLayoutSize(count);
                    }
                    int slot = Enumerable.Range(0, count).First(n => _layers.Values.All(layer => layer.Slot != n));
                    c = new Context(id, "layer_" + Guid.NewGuid().ToString("N"), slot,
                        new LayerWindows(id, _regions[slot]), _formReads);
                    _layers.Add(id, c);
                    Log.Info($"layer acquired agent={id} slot={slot} layerCount={count}");
                }
                c.TtlMs = (request.Int("ttlSeconds") ?? 120) * 1000;
                c.Expires = Environment.TickCount64 + c.TtlMs;
                c.ClientPid = request.Int("clientPid"); c.ClientStarted = request["clientStarted"]?.GetValue<long>() ?? 0;
                var acquired = JsonLine.Ok(State(c, true));
                if (leaseStarted) acquired["leaseStarted"] = true;
                return (acquired, null);
            }
            if (action == "status")
            {
                var result = c is null
                    ? new JsonObject { ["mode"] = "layered", ["layers"] = _layers.Count, ["summary"] = "No layer held by this agent." }
                    : State(c);
                if (c?.Closing is not null) result["closing"] = true;
                return (JsonLine.Ok(result), null);
            }
            if (c is null) return (Failure("lease_expired", "Your layer has ended; acquire and observe again."), null);
            if (request.Str("leaseToken") != c.Token) return (Failure("stale_lease", "This token does not own the layer."), null);
            if (action == "renew")
            {
                if (c.Closing is not null) return (Failure("seat_busy", "This layer is still closing."), null);
                c.TtlMs = (request.Int("ttlSeconds") ?? 120) * 1000;
                c.Expires = Environment.TickCount64 + c.TtlMs;
                return (JsonLine.Ok(State(c, true)), null);
            }
            return (null, BeginClose(c));
        }
    }
    // Keep the slot reserved until its worker and apps have stopped. Cleanup never holds _state.
    private Task<JsonObject> BeginClose(Context c)
    {
        if (c.Closing is { IsCompleted: false } || c.Closing is not null && c.RetryCloseAt > Environment.TickCount64)
            return c.Closing;
        return c.Closing = Task.Run(async () =>
        {
            try
            {
                c.Stop.Cancel();
                await c.Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    c.CloseWorkers();
                    await c.Windows.CloseOwnedAsync().ConfigureAwait(false);
                    DeleteProfile(c.Token);
                    c.Windows.Dispose();
                    c.Desktop.InvalidateLease();
                }
                finally { c.Gate.Release(); }
                lock (_state) _layers.Remove(c.Id);
                Log.Info($"layer released agent={c.Id} slot={c.Slot}");
                return JsonLine.Ok(new JsonObject { ["mode"] = "layered", ["summary"] = "Your layer and its registered apps closed. Other layers remain available." });
            }
            catch (Exception ex)
            {
                lock (_state) c.RetryCloseAt = Environment.TickCount64 + 1000;
                Log.Warn($"layer cleanup agent={c.Id} slot={c.Slot}: {ex.Message}");
                return Failure("layer_cleanup_pending", "Your layer is still closing. Its slot stays reserved while cleanup retries.");
            }
        });
    }
    private static void DeleteProfile(string token)
    {
        var root = Path.Combine(Env.StateDirectory, "layers");
        if (!Directory.Exists(root)) return;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The layer profile directory is a reparse point.");
        var path = Path.Combine(root, token);
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path);
        else Directory.Delete(path, recursive: true);
    }

    public static void CleanProfiles()
    {
        var root = Path.Combine(Env.StateDirectory, "layers");
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var path in Directory.EnumerateDirectories(root, "layer_*"))
        {
            string name = Path.GetFileName(path);
            if (name.Length != 38 || !Guid.TryParseExact(name.Substring(6), "N", out _)) continue;
            try { DeleteProfile(name); }
            catch (IOException ex) { Log.Warn($"could not remove retired layer profile {name}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Log.Warn($"could not remove retired layer profile {name}: {ex.Message}"); }
        }
    }
    public void ExpireIdle()
    {
        Context[] contexts;
        lock (_state) contexts = _layers.Values.ToArray();
        foreach (var c in contexts)
        {
            bool clientEnded = c.ClientPid is int pid && !DesktopLease.ClientAlive(pid, c.ClientStarted);
            lock (_state)
                if (_layers.TryGetValue(c.Id, out var current) && current == c &&
                    (c.Closing is not null || clientEnded || c.Active == 0 && c.Expires <= Environment.TickCount64))
                    _ = BeginClose(c);
        }
    }

    public async Task<JsonObject> HandleAsync(JsonObject request, CancellationToken cancel)
    {
        string op = request.Str("op")!;
        bool valueWrite = IsValueWrite(op, request), foreground = UsesForeground(op, request);
        var shortcut = op == "input.key" ? ClipboardShortcut(request.Str("keys")!) : ClipboardAction.None;
        Context c;
        lock (_state)
        {
            if (!_layers.TryGetValue(request.Str("agentId")!, out c!)) return Failure("lease_expired", "Acquire a layer before acting.");
            if (request.Str("leaseToken") != c.Token) return Failure("stale_lease", "This request does not own the layer.");
            if (c.Closing is not null) return Failure("lease_expired", "This layer is closing; acquire a new layer before acting.");
            if (c.Active == 0 && c.Expires <= Environment.TickCount64) return Failure("lease_expired", "Layer expired; acquire and observe again.");
            c.Active++; c.Expires = Environment.TickCount64 + c.TtlMs;
        }
        long arrived = Environment.TickCount64;
        var watch = Stopwatch.StartNew();
        double waitMs = 0, activateMs = 0, dispatchMs = 0, restoreMs = 0, cleanupMs = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel, c.Stop.Token);
        deadline.CancelAfter(DesktopLease.StopAfterMs(request.Int("timeoutMs") ?? 60000));
        bool entered = false, layout = false, input = false, started = false;
        try
        {
            await c.Gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            await _layout.EnterAsync(shared: true, deadline.Token).ConfigureAwait(false); layout = true;
            if (InputInjector.ScreenSize() != _layoutScreen)
                return Failure("layer_display", "The display changed. Restore the active layout before using coordinates.");
            if (!SupportsOperation(op)) return Failure("layer_unsupported", $"'{op}' requires an exclusive desktop lease. Release your region, then acquire mode=exclusive after the other regions finish.");
            if (foreground || valueWrite)
            {
                await c.WarmAsync(op, deadline.Token).ConfigureAwait(false);
                // A provider can change focus as a side effect of a field update.
                // Field writes can overlap each other, but not a physical input turn.
                await _input.EnterAsync(valueWrite, deadline.Token).ConfigureAwait(false);
                input = true;
            }
            waitMs = watch.Elapsed.TotalMilliseconds;
            deadline.Token.ThrowIfCancellationRequested();
            AgentAccess.Spend(request, Environment.TickCount64 - arrived);
            Log.Info($"layer action start agent={c.Id} op={op} physical={foreground} valueWrite={valueWrite}");
            if (foreground) InputInjector.ReleaseHeld();
            var turn = shortcut != ClipboardAction.None ? c.Clipboard.BeginTurn() : null;
            activateMs = watch.Elapsed.TotalMilliseconds - waitMs;
            bool delivered = false;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                started = true;
                var result = await DispatchAsync(c, op, request, deadline.Token).ConfigureAwait(false);
                dispatchMs = watch.Elapsed.TotalMilliseconds - waitMs - activateMs;
                delivered = result.Bool("ok") == true;
                return result;
            }
            finally
            {
                turn?.Complete(copy: delivered && shortcut is ClipboardAction.Copy or ClipboardAction.Cut,
                    paste: delivered && shortcut == ClipboardAction.Paste);
                restoreMs = watch.Elapsed.TotalMilliseconds - waitMs - activateMs - dispatchMs;
            }
        }
        catch (OperationCanceledException)
        {
            var failed = Failure("timed_out", started ? "Layer action timed out; effects may already have happened. Observe before acting again." : "Layer request timed out in the queue; nothing started.");
            failed["started"] = started; return failed;
        }
        catch (Exception ex)
        {
            var failed = Failure(ex is TimeoutException ? "timed_out" : started ? "outcome_unknown" : "not_started", ex.Message);
            failed["started"] = started; return failed;
        }
        finally
        {
            double beforeCleanup = watch.Elapsed.TotalMilliseconds;
            try
            {
                if (input)
                {
                    try { if (foreground) { InputInjector.ReleaseHeld(); c.Windows.RememberFocus(); } }
                    catch (Exception ex) { Log.Warn($"layer {c.Id}: input cleanup failed: {ex.Message}"); }
                    finally { _input.Exit(valueWrite); }
                }
            }
            finally
            {
                if (layout) _layout.Exit(shared: true);
                if (entered) c.Gate.Release();
                lock (_state) { c.Active--; c.Expires = Environment.TickCount64 + c.TtlMs; }
            }
            cleanupMs = watch.Elapsed.TotalMilliseconds - beforeCleanup;
            if (started) Log.Info($"layer action end agent={c.Id} op={op}");
            if (started) Log.Info($"layer timing agent={c.Id} op={op} waitMs={waitMs:F2} activateMs={activateMs:F2} dispatchMs={dispatchMs:F2} restoreMs={restoreMs:F2} cleanupMs={cleanupMs:F2} totalMs={watch.Elapsed.TotalMilliseconds:F2} physical={foreground} valueWrite={valueWrite}");
        }
    }
    internal static bool IsValueWrite(string op, JsonObject request) =>
        op == "desktop.element" && IsValueAction(request);

    private static bool IsValueAction(JsonObject request) => request.Str("action") is "set_value" or "set_values";

    internal static bool UsesForeground(string op, JsonObject request) =>
        op.StartsWith("input.", StringComparison.Ordinal) && op != "input.position"
        || op == "desktop.window" && request.Str("action") is "focus" or "raise"
        || op == "desktop.element" && !IsValueAction(request);

    internal static bool SupportsOperation(string op) => op is "screenshot" or "run" or "desktop.windows" or "desktop.observe"
        or "desktop.wait" or "desktop.window" or "desktop.element" or "input.move" or "input.click"
        or "input.drag" or "input.scroll" or "input.key" or "input.text" or "input.position" or "clipboard.read" or "clipboard.write"
        or "desktop.launch.status";

    internal enum ClipboardAction { None, Copy, Cut, Paste }
    internal static ClipboardAction ClipboardShortcut(string chord)
    {
        var keys = KeyCodes.Chord(chord);
        bool Has(params ushort[] codes) => keys.Any(codes.Contains);
        bool ctrl = Has(0x11, 0xA2, 0xA3), alt = Has(0x12, 0xA4, 0xA5), shift = Has(0x10, 0xA0, 0xA1);
        if (alt || Has(0x5B, 0x5C)) return ClipboardAction.None;
        if (keys.Length == 2 && ctrl && Has(0x43, 0x2D)) return ClipboardAction.Copy;
        if (keys.Length == 2 && ctrl && Has(0x58) || keys.Length == 2 && shift && Has(0x2E)) return ClipboardAction.Cut;
        if (keys.Length == 2 && ctrl && Has(0x56) || keys.Length == 2 && shift && Has(0x2D)
            || keys.Length == 3 && ctrl && shift && Has(0x56)) return ClipboardAction.Paste;
        return ClipboardAction.None;
    }

    private static Point LocalPoint(Context c, int x, int y)
    {
        if (x < 0 || y < 0 || x >= c.Windows.Bounds.Width || y >= c.Windows.Bounds.Height)
            throw new ArgumentException("Coordinates must be inside your layer's screenshot.");
        return new Point(x + c.Windows.Bounds.X, y + c.Windows.Bounds.Y);
    }
    private static async Task<JsonObject> DispatchAsync(Context c, string op, JsonObject r, CancellationToken cancel)
    {
        if (op == "clipboard.read") return JsonLine.Ok(new JsonObject { ["text"] = c.Clipboard.ReadText() });
        if (op == "clipboard.write") { c.Clipboard.WriteText(r.Str("text")!); return JsonLine.Ok(); }
        if (op == "screenshot")
        {
            var shot = ScreenCapture.Capture(r.Int("maxWidth"), r.Str("format") ?? "png", r.Int("quality") ?? r.Int("jpegQuality") ?? 80, c.Windows.Bounds,
                cancel, Math.Min(5000, r.Int("timeoutMs") ?? 60000));
            return JsonLine.Ok(new JsonObject { ["data"] = Convert.ToBase64String(shot.Bytes), ["mimeType"] = shot.MimeType,
                ["width"] = shot.Width, ["height"] = shot.Height, ["sourceWidth"] = shot.SourceWidth, ["sourceHeight"] = shot.SourceHeight });
        }
        if (op == "run")
        {
            var launch = (JsonObject)r.DeepClone();
            string name = Path.GetFileNameWithoutExtension(r.Str("path") ?? "").ToLowerInvariant();
            if (name is "msedge" or "chrome" or "brave" or "chromium")
            {
                var args = launch["args"] as JsonArray ?? new JsonArray();
                if (args.Any(a => a?.ToString().StartsWith("--user-data-dir", StringComparison.OrdinalIgnoreCase) == true))
                    return Seat.SeatHost.LaunchRefused("Layered browser launches manage their own unique profile; omit --user-data-dir.", "layer_profile");
                args.Insert(0, "--no-first-run");
                args.Insert(0, "--user-data-dir=" + Path.Combine(Env.StateDirectory, "layers", c.Token, "browser"));
                launch["args"] = args;
            }
            string launchId = Guid.NewGuid().ToString("N");
            JsonObject result;
            try
            {
                result = Seat.SeatHost.RunProgram(launch, ChildSession.CurrentSessionId(),
                    launchOwned: info => c.Windows.Launch(info, launchId));
            }
            catch (Core.Launch.LaunchNotStartedException ex)
            {
                return Seat.SeatHost.LaunchRefused(ex.Message, "layer_launch_failed");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or ArgumentException)
            {
                var uncertain = Failure("outcome_unknown", ex.Message);
                uncertain["started"] = true; return uncertain;
            }
            if (result.Obj("result") is { } data) data["launchId"] = launchId;
            c.Desktop.InvalidateObservations(); return result;
        }
        if (op == "desktop.launch.status")
        {
            if (r.Str("launchId") is not { } launchId) return Failure("layer_launch_id", "Pass the launchId from seat_run.");
            return JsonLine.Ok(new JsonObject { ["alive"] = c.Windows.LaunchAlive(launchId) });
        }
        if (op.StartsWith("desktop.", StringComparison.Ordinal))
        {
            var result = await c.Desktop.HandleAsync(op, r, cancel).ConfigureAwait(false);
            if (result.Bool("ok") == true && r.Str("windowId") is { } windowId &&
                (op is "desktop.observe" or "desktop.wait" || op == "desktop.window" && r.Str("action") is "focus" or "raise"))
                c.SelectedWindow = c.Desktop.WindowForInput(windowId);
            if (op is "desktop.element" or "desktop.window") c.Windows.ConstrainAll();
            return result;
        }
        if (op == "input.position") return JsonLine.Ok(new JsonObject { ["x"] = c.Pointer.X, ["y"] = c.Pointer.Y });
        if (op == "input.key")
        {
            var keys = KeyCodes.Chord(r.Str("keys")!);
            bool Has(params ushort[] codes) => keys.Any(codes.Contains);
            bool ctrl = Has(0x11, 0xA2, 0xA3), alt = Has(0x12, 0xA4, 0xA5), shift = Has(0x10, 0xA0, 0xA1);
            bool clipboardKeys = ctrl && Has(0x43, 0x56, 0x58, 0x2D) || shift && Has(0x2D, 0x2E);
            if (Has(0x5B, 0x5C) || alt && Has(0x09, 0x1B) || ctrl && Has(0x1B)
                || ctrl && alt && Has(0x2E) || clipboardKeys && ClipboardShortcut(r.Str("keys")!) == ClipboardAction.None)
                return Failure("layer_shortcut", "Global desktop shortcuts and unrecognized clipboard combinations are disabled; use supported copy/cut/paste shortcuts.");
        }
        c.Windows.ConstrainAll();
        var selected = r.Str("windowId") is { } explicitWindow ? c.Desktop.WindowForInput(explicitWindow) : c.SelectedWindow;
        if (selected is null)
        {
            var candidates = c.Windows.Windows().Where(LayerWindows.IsSelectable).Take(2).ToArray();
            if (candidates.Length == 1) selected = candidates[0];
        }
        if (selected is null || !await c.Windows.FocusAsync(c.Worker, cancel, selected).ConfigureAwait(false))
            return Failure("layer_focus", "Select the exact owned window with seat_observe or windowId. No input was sent.");
        bool OnTarget(Point point) => c.Windows.HitTestOwned(point, selected);
        bool KeyboardReady() => c.Windows.OwnedKeyboardFocus(selected) != IntPtr.Zero;
        if (op is "input.key" or "input.text" && !await c.Windows.WaitForKeyboardFocusAsync(cancel, selected).ConfigureAwait(false))
            return Failure("layer_focus", "The selected control did not receive keyboard focus. No input was sent.");
        cancel.ThrowIfCancellationRequested();
        c.Desktop.InvalidateObservations();
        Point at;
        switch (op)
        {
            case "input.text":
            {
                var result = InputInjector.TypeText(r.Str("text")!, r.Int("perCharMs") ?? DefaultTypeIntervalMs, r.Int("timeoutMs") ?? 60000,
                    cancel, KeyboardReady);
                if (result.Bool("ok") == true) await SettleKeyboardAsync(KeyboardReady, cancel).ConfigureAwait(false);
                return result;
            }
            case "input.key":
                if (!KeyboardReady()) return Failure("layer_focus", "The selected control lost keyboard focus. No input was sent.");
                InputInjector.Press(r.Str("keys")!, r.Int("holdMs") ?? DefaultKeyHoldMs, r.Bool("scanCode") ?? true, cancel);
                await SettleKeyboardAsync(KeyboardReady, cancel).ConfigureAwait(false);
                break;
            case "input.click":
                at = LocalPoint(c, r.Int("x") ?? c.Pointer.X, r.Int("y") ?? c.Pointer.Y);
                if (!OnTarget(at)) return Failure("layer_target", "The actual window at that point is not owned by your layer. Observe before clicking.");
                InputInjector.Click(r.Str("button") ?? "left", at.X, at.Y, r.Int("count") ?? 1, r.Int("holdMs") ?? 20, cancel,
                    () => OnTarget(at));
                c.Pointer = new Point(at.X - c.Windows.Bounds.X, at.Y - c.Windows.Bounds.Y); break;
            case "input.move":
                int x = r.Int("x") ?? c.Pointer.X + (r.Int("dx") ?? 0), y = r.Int("y") ?? c.Pointer.Y + (r.Int("dy") ?? 0);
                at = LocalPoint(c, x, y);
                if (!OnTarget(at)) return Failure("layer_target", "The actual window at that point is not owned by your layer.");
                InputInjector.MoveTo(at.X, at.Y); c.Pointer = new Point(x, y); break;
            case "input.drag":
            {
                var points = DragPath.Read(r);
                var screenPoints = points.Select(point => LocalPoint(c, point.X, point.Y)).ToArray();
                if (screenPoints.Any(point => !OnTarget(point)))
                    return Failure("layer_target", "Each drag waypoint must hit your registered window. No mouse input was sent.");
                InputInjector.Drag(screenPoints, r.Str("button") ?? "left", cancel,
                    () => c.Windows.IsForegroundOwned(selected), OnTarget);
                c.Pointer = points[^1];
                break;
            }
            case "input.scroll":
                at = LocalPoint(c, c.Pointer.X, c.Pointer.Y);
                if (!OnTarget(at)) return Failure("layer_target", "Scroll target is not owned by your layer; move to your window first.");
                InputInjector.MoveTo(at.X, at.Y);
                InputInjector.Scroll(r.Int("amount") ?? -3, r.Bool("horizontal") ?? false); break;
        }
        return JsonLine.Ok();
    }

    private static async Task SettleKeyboardAsync(Func<bool> keyboardReady, CancellationToken cancel)
    {
        // SendInput queues events. Keep the physical turn while Windows dispatches them,
        // so a following turn does not activate its window immediately after key-up.
        await Task.Delay(45, cancel).ConfigureAwait(false);
        if (!keyboardReady())
            throw new InvalidOperationException("Keyboard focus changed after sending input. Inspect the actual result before continuing.");
    }
}
