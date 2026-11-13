using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Anode.Core.Bridge;

namespace Anode.Core.Desktop;

/// <summary>Runs only in a disposable MTA worker inside the verified child session.</summary>
internal static class AccessibilityReader
{
    private static TreeWalker Walker => TreeWalker.ControlViewWalker;
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
    private static readonly (AutomationProperty Available, string[] Actions)[] Patterns =
    {
        (AutomationElement.IsInvokePatternAvailableProperty, ["invoke"]),
        (AutomationElement.IsValuePatternAvailableProperty, ["set_value"]),
        (AutomationElement.IsTogglePatternAvailableProperty, ["toggle"]),
        (AutomationElement.IsSelectionItemPatternAvailableProperty, ["select"]),
        (AutomationElement.IsExpandCollapsePatternAvailableProperty, ["expand", "collapse"]),
        (AutomationElement.IsScrollItemPatternAvailableProperty, ["scroll_into_view"]),
        (AutomationElement.IsScrollPatternAvailableProperty, ["scroll"]),
        (AutomationElement.IsRangeValuePatternAvailableProperty, ["set_range"])
    };

    public static JsonObject Observe(WindowTarget target, JsonObject request)
    {
        var window = WindowAccess.Verify(target);
        int maximum = request.Int("maxElements") ?? 200;
        int depthLimit = request.Int("maxDepth") ?? 8;
        int textBudget = request.Int("maxTextChars") ?? 6000;
        bool offscreen = request.Bool("includeOffscreen") ?? false;
        var root = AutomationElement.FromHandle(window);
        var elements = new JsonArray();
        var warnings = new JsonArray();
        var cache = ObservationCache();
        var watch = Stopwatch.StartNew();
        // An app can list the same control twice in its tree, even below itself (Chrome appeared to while a <select> list
        // was open). Walking it again would repeat its whole subtree until the budget ran out, so a control with a runtime
        // ID already queued is skipped. Its index still counts, so the paths seat_element follows stay right.
        var seen = new HashSet<string>();
        int repeated = 0;
        var queue = new Queue<(AutomationElement Element, int[]? RuntimeId, int[] Path, string? Parent)>();
        int[]? rootId = RuntimeId(root);
        if (rootId is { Length: > 0 }) seen.Add(Key(rootId));
        queue.Enqueue((root, rootId, Array.Empty<int>(), null));
        bool truncated = false;
        int visited = 0;
        while (queue.Count > 0 && visited < maximum && watch.ElapsedMilliseconds < 4000)
        {
            var current = queue.Dequeue();
            visited++;
            JsonObject? node = null;
            string? key = current.RuntimeId is { Length: > 0 } queued ? Key(queued) : null;
            try
            {
                // A runtime ID that couldn't be read when queued is read again here, and its failure reported.
                int[] runtimeId = current.RuntimeId ?? current.Element.GetRuntimeId() ?? Array.Empty<int>();
                if (current.RuntimeId is null && runtimeId.Length > 0 && !seen.Add(Key(runtimeId)))
                {
                    visited--;
                    repeated++;
                    continue;
                }
                if (runtimeId.Length > 0) key = Key(runtimeId);
                // Child navigation already fetched this element's cache. Only the root needs a separate fetch.
                var element = current.Path.Length == 0 ? current.Element.GetUpdatedCache(cache) : current.Element;
                node = Describe(element, runtimeId, current.Path, current.Parent, ref textBudget);
                string? parent = current.Parent;
                if (offscreen || node.Bool("offscreen") != true || current.Path.Length == 0)
                {
                    node["id"] = "e" + elements.Count;
                    parent = node.Str("id");
                    elements.Add(node);
                }
                if (current.Path.Length >= depthLimit) { truncated = true; continue; }
                int index = 0;
                var siblings = new HashSet<string>();
                var child = Walker.GetFirstChild(element, cache);
                while (child is not null && queue.Count + visited < maximum && watch.ElapsedMilliseconds < 4000)
                {
                    int[]? id = RuntimeId(child, useCache: true);
                    string? childKey = id is { Length: > 0 } ? Key(id) : null;
                    // A list of children that comes back to one it already gave has no more to give; repeats take no
                    // budget, so only this stops it.
                    if (childKey is not null && !siblings.Add(childKey)) { child = null; break; }
                    if (childKey is not null && !seen.Add(childKey)) repeated++;
                    else queue.Enqueue((child, id, current.Path.Append(index).ToArray(), parent));
                    index++;
                    child = Walker.GetNextSibling(child, cache);
                }
                if (child is not null) truncated = true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // A control that couldn't be described lets a copy of it listed later be described instead.
                if (node is null && key is not null) seen.Remove(key);
                if (warnings.Count < 3) warnings.Add("A control changed or did not expose readable accessibility information.");
            }
        }
        truncated |= queue.Count > 0;
        var result = new JsonObject
        {
            ["window"] = WindowAccess.Describe(target), ["elements"] = elements,
            ["truncated"] = truncated, ["warnings"] = warnings,
            ["observedAt"] = DateTime.UtcNow.ToString("o"), ["elapsedMs"] = watch.ElapsedMilliseconds
        };
        // Not a warning: nothing was hidden, and seat_wait's "missing" needs a tree without warnings.
        if (repeated > 0) result["skippedRepeats"] = repeated;
        return result;

        // Null when the control can't say yet; it is then read again, and its failure reported, when it is described.
        static int[]? RuntimeId(AutomationElement element, bool useCache = false)
        {
            try
            {
                if (useCache && element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty, true) is int[] id) return id;
                return element.GetRuntimeId();
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }
        static string Key(int[] id) => string.Join(".", id);
    }

    internal static IEnumerable<AutomationProperty> ObservationProperties()
    {
        foreach (var property in new[]
        {
            AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.AutomationIdProperty,
            AutomationElement.ClassNameProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.IsKeyboardFocusableProperty, AutomationElement.HasKeyboardFocusProperty,
            AutomationElement.IsPasswordProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.ProcessIdProperty
        }) yield return property;
        foreach (var pattern in Patterns) yield return pattern.Available;
        yield return AutomationElement.IsTextPatternAvailableProperty;
        yield return AutomationElement.RuntimeIdProperty;
        yield return AutomationElement.NativeWindowHandleProperty;
    }

    private static CacheRequest ObservationCache()
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element };
        foreach (var property in ObservationProperties()) cache.Add(property);
        return cache;
    }

    private static JsonObject Describe(AutomationElement element, int[] runtimeId, int[] path, string? parent, ref int textBudget)
    {
        // Pattern availability is part of the same UIA fetch as names and bounds.
        // Discovering every supported pattern individually multiplies provider calls across layers.
        bool Available(AutomationProperty property) => element.GetCachedPropertyValue(property, true) is true;
        var node = DescribeProperties(property => element.GetCachedPropertyValue(property), runtimeId, path, parent, out var actions);
        bool password = node.Bool("password") == true;
        if (!password)
        {
            string? text = null;
            if (Available(AutomationElement.IsValuePatternAvailableProperty) && element.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            {
                var pattern = (ValuePattern)value;
                bool readOnly = pattern.Current.IsReadOnly;
                node["readOnly"] = readOnly;
                if (textBudget > 0) text = pattern.Current.Value;
                if (readOnly) actions.Remove("set_value");
            }
            else if (textBudget > 0 && Available(AutomationElement.IsTextPatternAvailableProperty)
                && element.TryGetCurrentPattern(TextPattern.Pattern, out var document))
            {
                var pattern = (TextPattern)document;
                // The client hands back null for a range or a selection the app didn't provide.
                text = pattern.DocumentRange?.GetText(Math.Min(textBudget, 3000));
                var selected = pattern.GetSelection()?.FirstOrDefault()?.GetText(Math.Min(textBudget, 1000));
                if (!string.IsNullOrEmpty(selected))
                {
                    node["selectedText"] = Clip(selected, textBudget);
                    textBudget -= node.Str("selectedText")!.Length;
                }
            }
            if (!string.IsNullOrEmpty(text))
            {
                string limited = Clip(text, Math.Min(textBudget, 3000));
                node["text"] = limited;
                textBudget -= limited.Length;
            }
        }
        if (!password && Available(AutomationElement.IsTogglePatternAvailableProperty) && element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            node["toggleState"] = ((TogglePattern)toggle).Current.ToggleState.ToString();
        if (!password && Available(AutomationElement.IsSelectionItemPatternAvailableProperty) && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            node["selected"] = ((SelectionItemPattern)selection).Current.IsSelected;
        if (!password && Available(AutomationElement.IsExpandCollapsePatternAvailableProperty) && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            node["expandState"] = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString();
        if (!password && Available(AutomationElement.IsRangeValuePatternAvailableProperty) && element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range))
        {
            var state = ((RangeValuePattern)range).Current;
            node["range"] = new JsonObject
            {
                ["value"] = Finite(state.Value), ["minimum"] = Finite(state.Minimum), ["maximum"] = Finite(state.Maximum),
                ["smallChange"] = Finite(state.SmallChange), ["largeChange"] = Finite(state.LargeChange), ["readOnly"] = state.IsReadOnly
            };
            if (state.IsReadOnly) actions.Remove("set_range");
        }
        if (!password && Available(AutomationElement.IsScrollPatternAvailableProperty) && element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrolling))
        {
            var state = ((ScrollPattern)scrolling).Current;
            node["scroll"] = new JsonObject
            {
                ["horizontal"] = state.HorizontallyScrollable, ["vertical"] = state.VerticallyScrollable,
                ["horizontalPercent"] = Finite(state.HorizontalScrollPercent), ["verticalPercent"] = Finite(state.VerticalScrollPercent)
            };
        }
        node["actions"] = new JsonArray(actions.Select(value => (JsonNode)value).ToArray());
        return node;
    }

    internal static JsonObject DescribeProperties(Func<AutomationProperty, object?> read, int[] runtimeId, int[] path,
        string? parent, out List<string> actions)
    {
        var pid = read(AutomationElement.ProcessIdProperty) is int id ? id : 0;
        VerifyElementSession(pid);
        bool password = read(AutomationElement.IsPasswordProperty) is true;
        actions = new();
        if (!password)
        {
            foreach (var pattern in Patterns)
                if (read(pattern.Available) is true) actions.AddRange(pattern.Actions);
            if (read(AutomationElement.IsKeyboardFocusableProperty) is true) actions.Add("focus");
        }
        var type = read(AutomationElement.ControlTypeProperty);
        var bounds = read(AutomationElement.BoundingRectangleProperty) switch
        {
            System.Windows.Rect rectangle => rectangle,
            double[] { Length: 4 } r when r.All(double.IsFinite) && r[2] >= 0 && r[3] >= 0
                => new System.Windows.Rect(r[0], r[1], r[2], r[3]),
            _ => System.Windows.Rect.Empty
        };
        return new JsonObject
        {
            ["parent"] = parent, ["depth"] = path.Length,
            ["role"] = Role(type is int number ? ControlType.LookupById(number) : type as ControlType),
            ["name"] = Clip(read(AutomationElement.NameProperty) as string, 500),
            ["automationId"] = Clip(read(AutomationElement.AutomationIdProperty) as string, 256),
            ["className"] = Clip(read(AutomationElement.ClassNameProperty) as string, 128),
            ["enabled"] = read(AutomationElement.IsEnabledProperty) is true,
            ["offscreen"] = read(AutomationElement.IsOffscreenProperty) is true,
            ["focused"] = read(AutomationElement.HasKeyboardFocusProperty) is true, ["password"] = password,
            ["bounds"] = bounds.IsEmpty ? null : WindowAccess.RectangleJson(new Rectangle(
                (int)bounds.X, (int)bounds.Y, Math.Max(0, (int)bounds.Width), Math.Max(0, (int)bounds.Height))),
            ["path"] = new JsonArray(path.Select(value => (JsonNode)value).ToArray()),
            ["runtimeId"] = new JsonArray(runtimeId.Select(value => (JsonNode)value).ToArray()),
            ["processId"] = pid,
            ["nativeWindow"] = read(AutomationElement.NativeWindowHandleProperty) is int handle ? handle : 0
        };
    }

    public static JsonObject Act(WindowTarget target, JsonObject reference, JsonObject request)
    {
        var window = WindowAccess.Verify(target);
        int[] runtimeId = reference["runtimeId"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        int nativeWindow = reference.Int("nativeWindow") ?? 0;
        var element = FindNativeChild(window, nativeWindow, runtimeId);
        bool rebound = element is not null;
        if (element is null)
        {
            element = AutomationElement.FromHandle(window);
            foreach (int index in reference["path"]!.AsArray().Select(value => value!.GetValue<int>()))
            {
                element = Walker.GetFirstChild(element);
                for (int i = 0; i < index && element is not null; i++) element = Walker.GetNextSibling(element);
                if (element is null) throw Stale();
            }
            if (!(element.GetRuntimeId() ?? Array.Empty<int>()).SequenceEqual(runtimeId)) throw Stale();
        }
        // Fetch a fresh identity for this action, rather than reusing the model's observation.
        // One provider call gets the properties that used to be read independently while holding a turn.
        var identity = new CacheRequest { TreeScope = TreeScope.Element };
        identity.Add(AutomationElement.ProcessIdProperty);
        identity.Add(AutomationElement.NameProperty);
        identity.Add(AutomationElement.ControlTypeProperty);
        identity.Add(AutomationElement.AutomationIdProperty);
        identity.Add(AutomationElement.IsPasswordProperty);
        identity.Add(AutomationElement.IsEnabledProperty);
        var state = element.GetUpdatedCache(identity).Cached;
        VerifyElementSession(state.ProcessId);
        if (state.ProcessId != reference.Int("processId") || Clip(state.Name, 500) != reference.Str("name")
            || Role(state.ControlType) != reference.Str("role")
            || Clip(state.AutomationId, 256) != reference.Str("automationId")) throw Stale();
        if (state.IsPassword) throw new InvalidOperationException("Password controls require the user's direct interaction.");
        if (!state.IsEnabled) throw new InvalidOperationException("The control is disabled. Inspect again.");
        WindowAccess.Verify(target);
        if (rebound && !IsChild(window, new IntPtr(nativeWindow))) throw Stale();
        string action = request.Str("action") ?? throw new ArgumentException("An element action is required.");
        if (reference["actions"] is not JsonArray actions || !actions.Any(value => value?.GetValue<string>() == action))
            throw new InvalidOperationException("That action was not offered by the observed control. Inspect again.");
        T Pattern<T>(AutomationPattern id) where T : class => element.TryGetCurrentPattern(id, out var value)
            ? (T)value : throw Stale();
        switch (action)
        {
            case "invoke": Pattern<InvokePattern>(InvokePattern.Pattern).Invoke(); break;
            case "focus": element.SetFocus(); break;
            case "set_value":
                var value = Pattern<ValuePattern>(ValuePattern.Pattern);
                return SetValue(() => value.Current.Value, value.SetValue, request.Str("value")!, state.ControlType == ControlType.ComboBox);
            case "toggle": Pattern<TogglePattern>(TogglePattern.Pattern).Toggle(); break;
            case "select": Pattern<SelectionItemPattern>(SelectionItemPattern.Pattern).Select(); break;
            case "expand": Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern).Expand(); break;
            case "collapse": Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern).Collapse(); break;
            case "scroll_into_view": Pattern<ScrollItemPattern>(ScrollItemPattern.Pattern).ScrollIntoView(); break;
            case "scroll":
                var amount = request.Str("amount") == "large" ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement;
                if (request.Str("direction") is "up" or "left") amount = request.Str("amount") == "large" ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement;
                Pattern<ScrollPattern>(ScrollPattern.Pattern).Scroll(
                    request.Str("direction") is "left" or "right" ? amount : ScrollAmount.NoAmount,
                    request.Str("direction") is "up" or "down" ? amount : ScrollAmount.NoAmount);
                break;
            case "set_range": Pattern<RangeValuePattern>(RangeValuePattern.Pattern).SetValue(request["number"]!.GetValue<double>()); break;
            default: throw new ArgumentException("Unknown element action.");
        }
        return Performed(action);
    }

    // Native controls expose an HWND. Rebind it inside the verified window and
    // compare its UIA identity before skipping the sibling walk. Virtual
    // controls and providers returning a different identity keep the tree path.
    private static AutomationElement? FindNativeChild(IntPtr window, int handle, int[] runtimeId)
    {
        var child = new IntPtr(handle);
        if (handle == 0 || runtimeId.Length == 0 || !IsChild(window, child)) return null;
        try
        {
            var element = AutomationElement.FromHandle(child);
            return element.GetRuntimeId()?.SequenceEqual(runtimeId) == true ? element : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            return null;
        }
    }

    internal static JsonObject Performed(string action) =>
        new() { ["performed"] = action, ["note"] = "Observation consumed. Inspect again before the next action." };

    /// <summary>How long set_value watches for the control to take its new value. Chrome shows a change a moment later.</summary>
    internal const int SetValueSettleMs = 1500;

    /// <summary>
    /// Sets a value and reads it back. Some controls accept the call and ignore it, as Chrome's drop-down lists
    /// (&lt;select&gt;) do, and "performed" would then let an agent submit a form with the old choice. The failure never
    /// quotes the control's text: the pipe server logs errors, and a field can hold anything.
    /// </summary>
    internal static JsonObject SetValue(Func<string?> read, Action<string> write, string value, bool list)
    {
        string before = read() ?? "";
        write(value);
        string now;
        var watch = Stopwatch.StartNew();
        try
        {
            now = read() ?? "";
            while (now == before && now != value && watch.ElapsedMilliseconds < SetValueSettleMs)
            {
                Thread.Sleep(50);
                now = read() ?? "";
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or TimeoutException
            or System.Runtime.InteropServices.COMException)
        {
            // The value was already sent, so this must not read as "nothing happened".
            var unread = Performed("set_value");
            unread["note"] = "The value was sent, but the control could not be read back (it may have gone away). Inspect again before the next action.";
            return unread;
        }
        if (now == value) return Performed("set_value");
        if (now != before)
        {
            // An app may reformat what it was given, for example a number field that adds its separators.
            var changed = Performed("set_value");
            changed["value"] = Clip(now, 500);
            changed["note"] = "The control changed the value it was given. Observation consumed. Inspect again before the next action.";
            return changed;
        }
        throw new InvalidOperationException(
            $"The control accepted set_value but still showed its old value {SetValueSettleMs} ms later. It ignored the change, or "
            + "took it and kept its old text (a tag field that adds a tag, a value it rewrites, a change still pending). "
            + (list ? "Drop-down lists such as Chrome's ignore set_value: expand the list and select the option, or click it. " : "")
            + "Inspect again before you retry.");
    }

    /// <summary>The role of a control whose type .NET's UI Automation client can't name.</summary>
    internal const string UnknownRole = "Unknown";

    /// <summary>
    /// The control type's programmatic name without its prefix, such as Button. .NET's client names only the types up to
    /// Separator and returns no type at all for later ones, SemanticZoom and AppBar, which File Explorer's command bar is.
    /// </summary>
    internal static string Role(ControlType? type) => type?.ProgrammaticName.Replace("ControlType.", "") ?? UnknownRole;

    internal static InvalidOperationException Stale() => new("The control changed since observation. Inspect again; the action was not replayed.");
    internal static void VerifyElementSession(int pid)
    {
        if (!Core.Session.ChildSession.ProcessInSession(pid, Core.Session.ChildSession.CurrentSessionId()))
            throw new InvalidOperationException("Refusing an accessibility element outside the seat session.");
    }
    internal static string Clip(string? value, int maximum) => string.IsNullOrEmpty(value) || maximum <= 0 ? "" : value[..Math.Min(value.Length, maximum)];
    private static JsonNode? Finite(double value) => double.IsFinite(value) ? JsonValue.Create(value) : null;
}
