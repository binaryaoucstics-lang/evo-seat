using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using Anode.Core.Bridge;

namespace Anode.Core.Desktop;

/// <summary>Reads a fresh UIA snapshot of small forms through the Windows COM client.</summary>
internal static class NativeFormObservation
{
    private const int MaximumControls = 64;
    private static readonly Guid AutomationClass = new("e22ad333-b25f-460c-83d0-0581107395c9");
    [ThreadStatic] private static Automation? _automation;
    [ThreadStatic] private static CacheRequest? _cache;
    [ThreadStatic] private static CacheRequest? _identity;
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern IntPtr SendMessageTimeout(
        IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);

    internal static bool IsWinForms(IntPtr window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString().StartsWith("WindowsForms10.Window.", StringComparison.Ordinal);
    }

    private static bool HasSupportedChildren(IntPtr window)
    {
        var name = new StringBuilder(128);
        int count = 0;
        bool supported = true;
        EnumChildWindows(window, (child, _) =>
        {
            name.Clear();
            GetClassName(child, name, name.Capacity);
            var type = name.ToString().Split('.', 3);
            if (++count > MaximumControls || type.Length < 2 || type[0] != "WindowsForms10" ||
                type[1] is not ("BUTTON" or "STATIC" or "EDIT")) return supported = false;
            if (type[1] != "EDIT") return true;
            // Keep password and long document values out of the bulk cache.
            const uint getPasswordChar = 0x00D2, getTextLength = 0x000E, abortIfHung = 2;
            return supported = SendMessageTimeout(child, getPasswordChar, IntPtr.Zero, IntPtr.Zero, abortIfHung, 20, out var mask) != IntPtr.Zero &&
                mask == IntPtr.Zero &&
                SendMessageTimeout(child, getTextLength, IntPtr.Zero, IntPtr.Zero, abortIfHung, 20, out var length) != IntPtr.Zero &&
                length.ToInt64() <= 4096;
        }, IntPtr.Zero);
        return supported && count > 0;
    }

    public static JsonObject? TryRead(WindowTarget target, JsonObject request)
    {
        var window = WindowAccess.Verify(target);
        if (!IsWinForms(window) || !HasSupportedChildren(window)) return null;
        int maximum = request.Int("maxElements") ?? 200;
        int depthLimit = request.Int("maxDepth") ?? 8;
        int textBudget = request.Int("maxTextChars") ?? 6000;
        bool offscreen = request.Bool("includeOffscreen") ?? false;
        try
        {
            var watch = Stopwatch.StartNew();
            var automation = Client();
            var cache = _cache ??= CreateCache(automation);
            var root = automation.ElementFromHandleBuildCache(window, cache);
            var pending = new Queue<(Element Element, int[] Path, string? Parent)>();
            pending.Enqueue((root, [], null));
            var elements = new JsonArray();
            var seen = new HashSet<string>();
            bool truncated = false;
            int visited = 0;
            while (pending.Count > 0 && visited < maximum)
            {
                var (element, path, parent) = pending.Dequeue();
                visited++;
                object? Read(AutomationProperty property) => element.GetCachedPropertyValueEx(property.Id, 1);
                if (Read(AutomationElement.RuntimeIdProperty) is not int[] { Length: > 0 } id ||
                    !seen.Add(string.Join(".", id)) || Read(AutomationElement.ProcessIdProperty) is not int pid || pid != target.Pid)
                    return null;
                var node = AccessibilityReader.DescribeProperties(Read, id, path, parent, out var actions);
                if (node.Bool("password") == true || !AddState(Read, node, actions, ref textBudget)) return null;
                node["nativeReference"] = true;
                node["actions"] = new JsonArray(actions.Select(value => (JsonNode)value).ToArray());
                if (offscreen || node.Bool("offscreen") != true || path.Length == 0)
                {
                    parent = "e" + elements.Count;
                    node["id"] = parent;
                    elements.Add(node);
                }
                if (path.Length >= depthLimit) { truncated = true; continue; }
                var children = element.GetCachedChildren();
                int count = children?.Length ?? 0;
                if (count + visited + pending.Count > MaximumControls + 8) return null;
                for (int i = 0; i < count; i++)
                {
                    if (visited + pending.Count >= maximum) { truncated = true; break; }
                    pending.Enqueue((children!.GetElement(i), [.. path, i], parent));
                }
            }
            return new JsonObject
            {
                ["window"] = WindowAccess.Describe(target), ["elements"] = elements,
                ["truncated"] = truncated || pending.Count > 0, ["warnings"] = new JsonArray(),
                ["observedAt"] = DateTime.UtcNow.ToString("o"), ["elapsedMs"] = watch.ElapsedMilliseconds
            };
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ElementNotAvailableException)
        {
            // A provider without a matching COM view still uses the bounded managed reader.
            return null;
        }
    }

    public static JsonObject Act(WindowTarget target, JsonObject reference, JsonObject request)
    {
        var window = WindowAccess.Verify(target);
        var automation = Client();
        int[] expected = reference["runtimeId"]!.AsArray().Select(value => value!.GetValue<int>()).ToArray();
        var child = new IntPtr(reference.Int("nativeWindow") ?? 0);
        Element? element = null;
        if (child != IntPtr.Zero && IsChild(window, child))
        {
            var candidate = automation.ElementFromHandle(child);
            if (candidate.GetRuntimeId().SequenceEqual(expected)) element = candidate;
        }
        bool rebound = element is not null;
        if (element is null)
        {
            element = automation.ElementFromHandle(window);
            var walker = automation.ControlViewWalker();
            foreach (int index in reference["path"]!.AsArray().Select(value => value!.GetValue<int>()))
            {
                element = walker.GetFirstChildElement(element);
                for (int i = 0; i < index && element is not null; i++) element = walker.GetNextSiblingElement(element);
                if (element is null) throw AccessibilityReader.Stale();
            }
            if (!element.GetRuntimeId().SequenceEqual(expected)) throw AccessibilityReader.Stale();
        }
        var identity = element.BuildUpdatedCache(_identity ??= CreateIdentityCache(automation));
        object Read(AutomationProperty property) => identity.GetCachedPropertyValueEx(property.Id, 1);
        if (Read(AutomationElement.ProcessIdProperty) is not int pid || pid != target.Pid ||
            AccessibilityReader.Clip(Read(AutomationElement.NameProperty) as string, 500) != reference.Str("name") ||
            AccessibilityReader.Clip(Read(AutomationElement.AutomationIdProperty) as string, 256) != reference.Str("automationId") ||
            Read(AutomationElement.ControlTypeProperty) is not int type ||
            AccessibilityReader.Role(ControlType.LookupById(type)) != reference.Str("role")) throw AccessibilityReader.Stale();
        if (Read(AutomationElement.IsPasswordProperty) is true)
            throw new InvalidOperationException("Password controls require the user's direct interaction.");
        if (Read(AutomationElement.IsEnabledProperty) is not true)
            throw new InvalidOperationException("The control is disabled. Inspect again.");
        WindowAccess.Verify(target);
        if (rebound && !IsChild(window, child)) throw AccessibilityReader.Stale();
        string action = request.Str("action") ?? "";
        if (reference["actions"] is not JsonArray actions || !actions.Any(item => item?.GetValue<string>() == action))
            throw new InvalidOperationException("That action was not offered by the observed control. Inspect again.");
        T Pattern<T>(AutomationPattern pattern) where T : class =>
            element.GetCurrentPattern(pattern.Id) as T ?? throw AccessibilityReader.Stale();
        switch (action)
        {
            case "focus": element.SetFocus(); break;
            case "invoke": Pattern<Invoke>(InvokePattern.Pattern).Run(); break;
            case "toggle": Pattern<Toggle>(TogglePattern.Pattern).Run(); break;
            case "expand": Pattern<ExpandCollapse>(ExpandCollapsePattern.Pattern).Expand(); break;
            case "collapse": Pattern<ExpandCollapse>(ExpandCollapsePattern.Pattern).Collapse(); break;
            case "scroll_into_view": Pattern<ScrollItem>(ScrollItemPattern.Pattern).Run(); break;
            case "set_value":
                var value = Pattern<Value>(ValuePattern.Pattern);
                return AccessibilityReader.SetValue(() => value.CurrentValue, value.SetValue, request.Str("value")!, false);
            default: throw new ArgumentException("Unknown element action.");
        }
        return AccessibilityReader.Performed(action);
    }

    private static Automation Client() =>
        _automation ??= (Automation)Activator.CreateInstance(Type.GetTypeFromCLSID(AutomationClass, true)!)!;

    private static bool AddState(Func<AutomationProperty, object?> read, JsonObject node, List<string> actions, ref int budget)
    {
        if (read(AutomationElement.IsValuePatternAvailableProperty) is true)
        {
            if (read(ValuePattern.IsReadOnlyProperty) is not bool readOnly) return false;
            node["readOnly"] = readOnly;
            if (readOnly) actions.Remove("set_value");
            if (budget > 0)
            {
                if (read(ValuePattern.ValueProperty) is not string text) return false;
                int length = Math.Min(text.Length, Math.Min(budget, 3000));
                if (length > 0) node["text"] = text[..length];
                budget -= length;
            }
        }
        else if (budget > 0 && read(AutomationElement.IsTextPatternAvailableProperty) is true) return false;
        if (read(AutomationElement.IsTogglePatternAvailableProperty) is true)
        {
            if (read(TogglePattern.ToggleStateProperty) is not int toggle) return false;
            node["toggleState"] = ((ToggleState)toggle).ToString();
        }
        if (read(AutomationElement.IsExpandCollapsePatternAvailableProperty) is true)
        {
            if (read(ExpandCollapsePattern.ExpandCollapseStateProperty) is not int expand) return false;
            node["expandState"] = ((ExpandCollapseState)expand).ToString();
        }
        // The managed reader handles complex patterns, including document text and scrolling.
        return read(AutomationElement.IsSelectionItemPatternAvailableProperty) is not true
            && read(AutomationElement.IsRangeValuePatternAvailableProperty) is not true
            && read(AutomationElement.IsScrollPatternAvailableProperty) is not true;
    }

    private static CacheRequest CreateCache(Automation automation)
    {
        var cache = automation.CreateCacheRequest();
        cache.SetTreeScope(7); // TreeScope_Subtree
        cache.SetAutomationElementMode(0); // AutomationElementMode_None
        foreach (var property in AccessibilityReader.ObservationProperties())
            cache.AddProperty(property.Id);
        cache.AddProperty(ValuePattern.ValueProperty.Id);
        cache.AddProperty(ValuePattern.IsReadOnlyProperty.Id);
        cache.AddProperty(TogglePattern.ToggleStateProperty.Id);
        cache.AddProperty(ExpandCollapsePattern.ExpandCollapseStateProperty.Id);
        return cache;
    }

    private static CacheRequest CreateIdentityCache(Automation automation)
    {
        var cache = automation.CreateCacheRequest();
        cache.SetTreeScope(1); // TreeScope_Element
        cache.SetAutomationElementMode(0);
        foreach (var property in new[]
        {
            AutomationElement.ProcessIdProperty, AutomationElement.NameProperty, AutomationElement.AutomationIdProperty,
            AutomationElement.ControlTypeProperty, AutomationElement.IsPasswordProperty, AutomationElement.IsEnabledProperty
        }) cache.AddProperty(property.Id);
        return cache;
    }

    // UIAutomationCore type library order. Placeholder slots are never called.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Automation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        Element ElementFromHandle(IntPtr window);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache();
        Element ElementFromHandleBuildCache(IntPtr window, CacheRequest cache);
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        Walker ControlViewWalker();
        void ContentViewWalker(); void RawViewWalker();
        void RawViewCondition(); void ControlViewCondition(); void ContentViewCondition();
        CacheRequest CreateCacheRequest();
    }

    [ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface CacheRequest
    {
        void AddProperty(int propertyId); void AddPattern(); void Clone();
        void GetTreeScope(); void SetTreeScope(int scope); void GetTreeFilter(); void SetTreeFilter();
        void GetAutomationElementMode(); void SetAutomationElementMode(int mode);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Element
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] GetRuntimeId();
        void FindFirst(); void FindAll(); void FindFirstBuildCache();
        void FindAllBuildCache();
        Element BuildUpdatedCache(CacheRequest cache);
        void GetCurrentPropertyValue();
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCachedPropertyValueEx(int propertyId, int ignoreDefault);
        void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
        void GetCachedPattern();
        void GetCachedParent();
        ElementArray? GetCachedChildren();
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ElementArray
    {
        int Length { get; }
        Element GetElement(int index);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Walker
    {
        void GetParentElement();
        Element? GetFirstChildElement(Element element);
        void GetLastChildElement();
        Element? GetNextSiblingElement(Element element);
    }

    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Invoke { void Run(); }

    [ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Toggle { void Run(); }

    [ComImport, Guid("619be086-1f4e-4ee4-bafa-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ExpandCollapse { void Expand(); void Collapse(); }

    [ComImport, Guid("b488300f-d015-4f19-9c29-bb595e3645ef"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ScrollItem { void Run(); }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Value
    {
        void SetValue([MarshalAs(UnmanagedType.BStr)] string value);
        string CurrentValue { [return: MarshalAs(UnmanagedType.BStr)] get; }
    }
}
