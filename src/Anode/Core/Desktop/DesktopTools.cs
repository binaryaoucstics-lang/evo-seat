using System.Diagnostics;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Capture;

namespace Anode.Core.Desktop;

internal sealed class DesktopTools
{
    private readonly DesktopReferences _references = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<JsonObject, CancellationToken, Task<JsonObject>> _worker;
    private readonly Rectangle? _region;
    private readonly Func<WindowTarget, bool>? _owns;
    private readonly Func<JsonObject>? _windows;
    // Exclusive launches may hand off to an existing app. Track only the actual
    // process returned by Windows; a retained handle distinguishes reused PIDs.
    private readonly Dictionary<string, Process> _launches = new();
    private string _launchEpoch = Guid.NewGuid().ToString("N");

    // Each layer has its own instance, reference table and observation gate. The default
    // remains the original whole-seat desktop, including its existing test-worker hook.
    public DesktopTools(Func<JsonObject, CancellationToken, Task<JsonObject>>? worker = null,
        Rectangle? region = null, Func<WindowTarget, bool>? owns = null, Func<JsonObject>? windows = null)
    {
        if (region is { } bounds && (bounds.Width <= 0 || bounds.Height <= 0))
            throw new ArgumentException("A layer requires a non-empty capture region.", nameof(region));
        if (region is not null && owns is null)
            throw new ArgumentException("A layer requires a window ownership check.", nameof(owns));
        _worker = worker ?? DesktopWorker.CallAsync;
        _region = region;
        _owns = owns;
        _windows = windows;
    }
    public void InvalidateObservations() => _references.InvalidateObservations();
    public void InvalidateLease()
    {
        _references.Clear();
        foreach (var process in _launches.Values) process.Dispose();
        _launches.Clear();
        _launchEpoch = Guid.NewGuid().ToString("N");
    }

    // These methods run under the exclusive desktop lease's interaction gate.
    // Disposing a handle does not terminate its process.
    public string RememberLaunch(Process process)
    {
        foreach (var (key, old) in _launches.ToArray())
            if (old.HasExited) { _launches.Remove(key); old.Dispose(); }
        var id = _launchEpoch + "." + Guid.NewGuid().ToString("N");
        _launches.Add(id, process);
        return id;
    }
    public JsonObject LaunchStatus(string id)
    {
        if (id.Length != 65 || !id.StartsWith(_launchEpoch + ".", StringComparison.Ordinal)
            || !Guid.TryParseExact(id[33..], "N", out _))
            return JsonLine.Fail("Unknown launchId for this lease. List windows and observe again.");
        return JsonLine.Ok(new JsonObject { ["alive"] = _launches.TryGetValue(id, out var p) && !p.HasExited });
    }
    internal WindowTarget WindowForInput(string id)
    {
        var target = _references.Window(id);
        VerifyOwned(target);
        return target;
    }

    public static string? ToolName(string op) => op switch
    {
        "desktop.windows" => "seat_windows", "desktop.observe" => "seat_observe",
        "desktop.window" => "seat_window", "desktop.element" => "seat_element",
        "desktop.capabilities" => "seat_capabilities", "desktop.wait" => "seat_wait", _ => null
    };

    public async Task<JsonObject> HandleAsync(string op, JsonObject arguments, CancellationToken cancel = default)
    {
        var args = Core.Agents.AgentAccess.Arguments(arguments);
        string tool = ToolName(op) ?? throw new ArgumentException("Unknown desktop operation.");
        if (Mcp.Tools.ValidateArguments(tool, args) is { } error) return JsonLine.Fail(error);
        if (op == "desktop.wait") return await DesktopWait.RunAsync(args, (r, token) => HandleAsync("desktop.observe", r, token), cancel).ConfigureAwait(false);
        await _gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            JsonObject result;
            if (op == "desktop.capabilities")
            {
                args["op"] = "capabilities";
                return JsonLine.Ok(await _worker(args, cancel).ConfigureAwait(false));
            }
            if (op == "desktop.windows")
            {
                result = _windows?.Invoke() ?? await _worker(new JsonObject { ["op"] = "windows" }, cancel).ConfigureAwait(false);
                var windows = new JsonArray();
                var exclusive = _launches.Count == 0 ? null : _launches.ToLookup(pair => pair.Value.Id);
                foreach (var window in result["windows"]!.AsArray().OfType<JsonObject>())
                {
                    var target = WindowTarget.FromJson(window.Obj("target")!);
                    if (!Owns(target)) continue;
                    if (args.Int("pid") is int pid && target.Pid != pid) continue;
                    if (exclusive is not null)
                        foreach (var (id, p) in exclusive[target.Pid])
                            if (!p.HasExited && p.StartTime.ToUniversalTime().Ticks == target.Started) { window["launchId"] = id; break; }
                    if (args.Str("launchId") is { } launchId && window.Str("launchId") != launchId) continue;
                    if (args.Str("query") is { } query && !(window.Str("title") ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                        && !(window.Str("process") ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    var item = (JsonObject)window.DeepClone();
                    item.Remove("target");
                    item["windowId"] = _references.RememberWindow(target);
                    windows.Add(item);
                }
                result["windows"] = windows;
            }
            else if (op == "desktop.element" && args.Str("action") == "set_values")
            {
                var values = args["values"]!.AsArray().Cast<JsonObject>().ToArray();
                var (window, elements) = _references.TakeElements(args.Str("snapshotId")!, values.Select(field => field.Str("elementId")!));
                VerifyOwned(window);
                _references.InvalidateObservations();
                if (elements.Any(element => element.Bool("password") == true || element["actions"] is not JsonArray actions
                    || !actions.Any(action => action?.GetValue<string>() == "set_value")))
                    return JsonLine.Fail("Every control in the batch must offer set_value and allow direct editing. Inspect again before acting.");
                var edits = new JsonArray();
                for (int i = 0; i < values.Length; i++)
                    edits.Add(new JsonObject { ["element"] = elements[i], ["value"] = values[i].Str("value") });
                result = await _worker(new JsonObject { ["op"] = "element", ["action"] = "set_values",
                    ["target"] = window.ToJson(), ["edits"] = edits }, cancel).ConfigureAwait(false);
                if (result.Str("batchError") is { } batchError)
                {
                    var failed = JsonLine.Fail(batchError);
                    failed["errorCode"] = "outcome_unknown";
                    failed["started"] = true;
                    failed["completed"] = result["completed"]!.DeepClone();
                    failed["failedElementId"] = result.Str("failedElementId");
                    return failed;
                }
            }
            else if (op == "desktop.element")
            {
                var (window, element) = _references.TakeElement(args.Str("snapshotId")!, args.Str("elementId")!);
                VerifyOwned(window);
                // All observations are invalid after an attempted action, including a timeout.
                _references.InvalidateObservations();
                args["target"] = window.ToJson();
                args["element"] = element;
                args["op"] = "element";
                result = await _worker(args, cancel).ConfigureAwait(false);
            }
            else
            {
                string windowId = args.Str("windowId")!;
                var window = _references.Window(windowId);
                VerifyOwned(window);
                args["target"] = window.ToJson();
                args["op"] = op == "desktop.observe" ? "observe" : "window";
                if (op == "desktop.window") _references.InvalidateObservations();
                string? originalAction = args.Str("action");
                if (op == "desktop.window" && _region is { } region)
                {
                    if (originalAction == "maximize")
                    {
                        // Fill just this tile. LayerHub waits for these moves before admitting real input.
                        args["action"] = "fit_layer";
                        args["x"] = region.X; args["y"] = region.Y;
                        args["width"] = region.Width; args["height"] = region.Height;
                    }
                    else if (originalAction == "move") TranslateMove(args, region);
                }
                result = await _worker(args, cancel).ConfigureAwait(false);
                if (op == "desktop.window" && _region is not null && originalAction == "maximize")
                {
                    result["requested"] = "maximize";
                    result["note"] = "Requested a window filling this layer. Inspect again to confirm the resulting window state.";
                }
                foreach (string key in new[] { "coveredBy", "topmostAbove" })
                    if (op == "desktop.window" && result.Obj(key) is { } covering)
                    {
                        if (covering.Obj("target") is { } coveringTarget && Owns(WindowTarget.FromJson(coveringTarget)))
                            covering["windowId"] = _references.RememberWindow(WindowTarget.FromJson(coveringTarget));
                        else if (_region is not null)
                        {
                            // Coverage can be caused by an unowned system window. Do not
                            // return another layer's title, process or reusable reference.
                            result.Remove(key);
                            result["note"] = "A window outside this layer overlaps the target. Observe this layer again before input.";
                            continue;
                        }
                        covering.Remove("target");
                    }
                if (op == "desktop.observe")
                {
                    result["windowId"] = windowId;
                    result["snapshotId"] = _references.RememberObservation(window, result["elements"]!.AsArray());
                    result["expiresInSeconds"] = 90;
                    foreach (var element in result["elements"]!.AsArray().OfType<JsonObject>())
                    {
                        element.Remove("path"); element.Remove("runtimeId"); element.Remove("processId");
                        element.Remove("nativeWindow"); element.Remove("nativeReference");
                    }
                    if (args.Bool("includeScreenshot") == true)
                    {
                        try
                        {
                            // UIA remains useful if RDP suppresses the display. Never open the parent viewer here.
                            var shot = ScreenCapture.Capture(args.Int("maxWidth") ?? 1280, args.Str("format") ?? "jpeg",
                                args.Int("quality") ?? 80, region: _region, cancel: cancel);
                            result["screenshot"] = new JsonObject
                            {
                                ["data"] = Convert.ToBase64String(shot.Bytes), ["mimeType"] = shot.MimeType,
                                ["width"] = shot.Width, ["height"] = shot.Height,
                                ["sourceWidth"] = shot.SourceWidth, ["sourceHeight"] = shot.SourceHeight
                            };
                        }
                        catch (Exception ex)
                        {
                            result["screenshotError"] = "Seat capture unavailable: " + ex.Message
                                + " No foreground fallback was used. Use control actions from this tree; do not guess pixel coordinates.";
                        }
                    }
                }
            }
            // RememberObservation above copied the original global UIA data for its
            // worker. Only the public result is shifted/clipped to the layer's pixels.
            if (_region is { } tile)
            {
                Localize(result, tile);
                result["coordinateSpace"] = "layer";
                result["screen"] = new JsonObject { ["width"] = tile.Width, ["height"] = tile.Height };
            }
            result["summary"] = DesktopPresentation.Summary(result);
            return JsonLine.Ok(result);
        }
        finally { _gate.Release(); }
    }

    private bool Owns(WindowTarget target)
    {
        try { return _owns?.Invoke(target) ?? true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private void VerifyOwned(WindowTarget target)
    {
        if (!Owns(target)) throw new InvalidOperationException("This window no longer belongs to this layer. List this layer's windows again.");
    }

    private static void TranslateMove(JsonObject args, Rectangle region)
    {
        int x = args.Int("x")!.Value, y = args.Int("y")!.Value;
        int width = args.Int("width")!.Value, height = args.Int("height")!.Value;
        if (x < 0 || y < 0 || width <= 0 || height <= 0
            || (long)x + width > region.Width || (long)y + height > region.Height)
            throw new ArgumentException("Window bounds must fit entirely inside this layer, using layer-local coordinates.");
        args["x"] = checked(region.X + x); args["y"] = checked(region.Y + y);
    }

    private static void Localize(JsonNode node, Rectangle region)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) if (child is not null) Localize(child, region);
            return;
        }
        if (node is not JsonObject item) return;
        if (item.Obj("bounds") is { } bounds && bounds.Int("x") is int x && bounds.Int("y") is int y
            && bounds.Int("width") is int width && bounds.Int("height") is int height)
        {
            var source = new Rectangle(x, y, Math.Max(0, width), Math.Max(0, height));
            var clipped = Rectangle.Intersect(source, region);
            bool visible = clipped.Width > 0 && clipped.Height > 0;
            item["bounds"] = visible
                ? WindowAccess.RectangleJson(new Rectangle(clipped.X - region.X, clipped.Y - region.Y, clipped.Width, clipped.Height))
                : WindowAccess.RectangleJson(Rectangle.Empty);
            if (!visible) item["offscreen"] = true;
            if (clipped != source) item["clippedToLayer"] = true;
        }
        foreach (var (key, child) in item.ToArray())
            if (key != "bounds" && child is not null) Localize(child, region);
    }
}
