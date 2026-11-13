using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Desktop;
using Anode.Core.Input;
using Anode.Core.Launch;
using Anode.Core.Processes;
using Anode.Core.Session;
using Anode.Core.Util;

namespace Anode.Core.Layers;

/// <summary>
/// Owns only processes explicitly started for one agent and their verified descendants.
/// Windows are kept on a non-overlapping rectangle of the existing seat, not a new desktop.
/// Containment/frame correction is adapted from BgDesk (MIT, copyright 2026 enwong93-sketch).
/// Unlike BgDesk's heuristic fallback, an unknown window is never claimed by location or recency.
/// </summary>
internal sealed class LayerWindows : IDisposable
{
    private sealed record Identity(int Pid, long Started, string LaunchId);
    private readonly object _gate = new();
    private readonly Dictionary<int, Identity> _processes = new();
    private readonly int _session = checked((int)ChildSession.CurrentSessionId());
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, ExecutionJobObject> _jobs = new();
    private WindowTarget? _lastFocused;
    private long _lastDiscovery;
    private bool _disposed;

    public string AgentId { get; }
    public Rectangle Bounds { get; }

    public LayerWindows(string agentId, Rectangle tile)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("A layer needs an agent ID.", nameof(agentId));
        if (tile.Width < 320 || tile.Height < 240) throw new ArgumentException("A layer is too small.", nameof(tile));
        AgentId = agentId;
        Bounds = tile;
        _timer = new System.Threading.Timer(_ => Tick(), null, 200, 200);
    }

    public int Launch(ProcessStartInfo info, string launchId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string file = info.FileName;
            if (!Path.IsPathFullyQualified(file))
            {
                var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
                file = directories.Prepend(info.WorkingDirectory).Where(Path.IsPathFullyQualified)
                    .Select(dir => Path.GetFullPath(file, dir)).FirstOrDefault(File.Exists)
                    ?? throw new LaunchNotStartedException("Use the full path of an installed executable.");
            }
            if (!Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new LaunchNotStartedException("A partition launches executable files. Pass URLs or documents as app arguments.");
            var job = new ExecutionJobObject();
            try
            {
                int pid = DetachedProcess.Start(file, info.ArgumentList, info.WorkingDirectory, (id, handle) =>
                {
                    job.Assign(handle);
                    using var process = Process.GetProcessById(id);
                    if (!ChildSession.ProcessInSession(id, (uint)_session))
                        throw new InvalidOperationException("Refusing a process outside the verified seat.");
                    _processes[id] = new Identity(id, process.StartTime.ToUniversalTime().Ticks, launchId);
                });
                _jobs.Add(launchId, job);
                return pid;
            }
            catch { job.Dispose(); throw; }
        }
    }

    public bool LaunchAlive(string launchId)
    {
        lock (_gate) return _jobs.TryGetValue(launchId, out var job) && job.ActiveProcesses > 0;
    }

    private void DiscoverDescendants()
    {
        long now = Environment.TickCount64;
        if (now - _lastDiscovery < 200) return;
        _lastDiscovery = now;
        foreach (var (id, job) in _jobs.ToArray())
        {
            if (job.ActiveProcesses > 0) continue;
            job.Dispose();
            _jobs.Remove(id);
        }
        foreach (var (pid, identity) in _processes.ToArray())
            if (!_jobs.ContainsKey(identity.LaunchId)) _processes.Remove(pid);
    }

    public bool Owns(WindowTarget target)
    {
        try
        {
            var handle = WindowAccess.Verify(target);
            return Owns(handle);
        }
        catch { return false; }
    }

    public bool Owns(IntPtr handle)
    {
        lock (_gate)
        {
            if (_disposed || handle == IntPtr.Zero) return false;
            DiscoverDescendants();
            return OwnsCore(handle);
        }
    }

    private bool OwnsCore(IntPtr handle)
    {
        var visited = new HashSet<IntPtr>();
        for (int i = 0; i < 16 && handle != IntPtr.Zero && visited.Add(handle); i++, handle = GetWindow(handle, 4))
        {
            try
            {
                GetWindowThreadProcessId(handle, out uint pid);
                if (pid > int.MaxValue) return false;
                // Reject unrelated windows before opening processes and reading their
                // creation time. Never cache HWND identity across actions/PID reuse.
                if (_processes.TryGetValue((int)pid, out var identity))
                {
                    var target = WindowAccess.Identify(handle);
                    if (target.Pid != (int)pid) return false; // HWND changed during validation.
                    if (MatchesIdentity(target, identity.Pid, identity.Started)) return true;
                }
                IntPtr processHandle = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
                if (processHandle == IntPtr.Zero) continue;
                try
                {
                    var member = _jobs.FirstOrDefault(pair => pair.Value.Owns(processHandle));
                    if (member.Value is null) continue;
                    var target = WindowAccess.Identify(handle);
                    if (target.Pid != (int)pid) return false;
                    using var process = Process.GetProcessById((int)pid);
                    if (ChildSession.ProcessInSession(process.Id, (uint)_session) &&
                        member.Value.Owns(process.Handle) &&
                        MatchesIdentity(target, process.Id, process.StartTime.ToUniversalTime().Ticks))
                    {
                        _processes[(int)pid] = new Identity((int)pid, target.Started, member.Key);
                        return true;
                    }
                }
                finally { CloseHandle(processHandle); }
            }
            catch { return false; }
        }
        return false;
    }

    internal static bool MatchesIdentity(WindowTarget target, int pid, long started) => target.Pid == pid && target.Started == started;

    /// <summary>Visible top-level windows in native z-order, not all windows of this account.</summary>
    public IReadOnlyList<WindowTarget> Windows()
    {
        lock (_gate)
        {
            if (_disposed) return Array.Empty<WindowTarget>();
            DiscoverDescendants();
            return WindowsCore();
        }
    }

    // Window metadata uses bounded Win32 calls, not UIA. Filter ownership before
    // describing windows; no inspection process or foreign title is needed.
    public JsonObject DescribeWindows()
    {
        var windows = new JsonArray();
        foreach (var target in Windows())
        {
            try
            {
                var item = WindowAccess.Describe(target); item["target"] = target.ToJson();
                lock (_gate)
                    if (_processes.TryGetValue(target.Pid, out var process) && process.Started == target.Started)
                        item["launchId"] = process.LaunchId;
                windows.Add(item);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return new JsonObject { ["windows"] = windows };
    }

    private List<WindowTarget> WindowsCore()
    {
        var windows = new List<WindowTarget>();
        if (_jobs.Count == 0 && _processes.Count == 0) return windows;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || !OwnsCore(window)) return true;
            try { windows.Add(WindowAccess.Identify(window)); } catch { }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public void ConstrainAll()
    {
        lock (_gate)
        {
            if (_disposed) return;
            DiscoverDescendants();
            foreach (var target in WindowsCore())
            {
                try { Contain(WindowAccess.Verify(target)); } catch { }
            }
        }
    }

    private void Tick()
    {
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            if (_disposed) return;
            DiscoverDescendants();
            if (_jobs.Count == 0 && _processes.Count == 0) return;
            var foreground = GetForegroundWindow();
            if (OwnsCore(foreground))
            {
                try { _lastFocused = WindowAccess.Identify(foreground); } catch { }
            }
            foreach (var target in WindowsCore())
            {
                try { Contain(WindowAccess.Verify(target)); } catch { }
            }
        }
        catch (Exception ex) { Log.Warn($"layer {AgentId}: containment tick failed: {ex.Message}"); }
        finally { Monitor.Exit(_gate); }
    }

    private void Contain(IntPtr window)
    {
        string className = ClassName(window);
        // Popup menu placement is the owning app's responsibility; moving it independently
        // would break menu selection. It is never used as a layer's normal focus target.
        if (className is "#32768" or "tooltips_class32" or "Shell_TrayWnd" or "Progman" or "WorkerW") return;
        long exStyle = GetWindowLongPtr(window, -20).ToInt64();
        if ((exStyle & 0x08000000) != 0) return; // WS_EX_NOACTIVATE overlays.
        if (IsIconic(window)) return; // A minimized window has no visible frame to constrain.
        if (!GetWindowRect(window, out var outer)) return;
        var frame = Rectangle.FromLTRB(outer.Left, outer.Top, outer.Right, outer.Bottom);
        if (DwmGetWindowAttribute(window, 9, out var visible, Marshal.SizeOf<Rect>()) == 0 && visible.Right > visible.Left && visible.Bottom > visible.Top)
            frame = Rectangle.FromLTRB(visible.Left, visible.Top, visible.Right, visible.Bottom);
        bool maximized = IsZoomed(window);
        if (!maximized && Rectangle.Inflate(Bounds, 1, 1).Contains(frame)) return;
        int left = frame.Left - outer.Left, top = frame.Top - outer.Top;
        int right = outer.Right - frame.Right, bottom = outer.Bottom - frame.Bottom;
        if (maximized)
        {
            var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(window, ref placement)) return;
            placement.Flags = 4; // WPF_ASYNCWINDOWPLACEMENT
            placement.ShowCommand = 4;
            placement.Normal = new Rect { Left = Bounds.Left - left, Top = Bounds.Top - top, Right = Bounds.Right + right, Bottom = Bounds.Bottom + bottom };
            SetWindowPlacement(window, ref placement);
            return;
        }
        int width = Math.Min(frame.Width, Bounds.Width), height = Math.Min(frame.Height, Bounds.Height);
        if (width <= 0 || height <= 0) return;
        int x = Math.Clamp(frame.X, Bounds.Left, Bounds.Right - width), y = Math.Clamp(frame.Y, Bounds.Top, Bounds.Bottom - height);
        SetWindowPos(window, IntPtr.Zero, x - left, y - top, width + left + right, height + top + bottom,
            0x4014); // SWP_ASYNCWINDOWPOS | SWP_NOACTIVATE | SWP_NOZORDER
    }

    public static bool IsSelectable(WindowTarget window)
    {
        var handle = new IntPtr(window.Handle);
        return IsWindowEnabled(handle) && !IsHungAppWindow(handle) && ClassName(handle) is not ("#32768" or "tooltips_class32");
    }

    /// <summary>Caller must hold the seat's real-input lock for focus plus the complete input action.</summary>
    public async Task<bool> FocusAsync(DesktopWorker.Session worker, CancellationToken cancel, WindowTarget? selected = null)
    {
        WindowTarget? target;
        lock (_gate)
        {
            if (_disposed) return false;
            DiscoverDescendants();
            var current = GetForegroundWindow();
            if ((selected is null || selected.Handle == current.ToInt64()) && OwnsCore(current) && !IsHungAppWindow(current))
            {
                try { _lastFocused = WindowAccess.Identify(current); } catch { }
                return true;
            }
            var candidates = WindowsCore().Where(IsSelectable).ToList();
            // A visible modal dialog precedes its owner in z-order. Prefer it over a saved
            // main window that might now be disabled; otherwise retain the last app focus.
            var modal = candidates.FirstOrDefault(w => GetWindow(new IntPtr(w.Handle), 4) != IntPtr.Zero);
            target = selected is null ? modal ?? candidates.FirstOrDefault(w => w == _lastFocused) ?? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(w => w == selected);
        }
        if (target is null) return false;
        try
        {
            await worker.CallAsync(new JsonObject { ["op"] = "window", ["action"] = "focus", ["target"] = target.ToJson() },
                cancel, TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
            return GetForegroundWindow() == new IntPtr(target.Handle) && IsForegroundOwned();
        }
        catch (Exception ex) when (!cancel.IsCancellationRequested)
        {
            Log.Warn($"layer {AgentId}: focus refused: {ex.Message}");
            return false;
        }
    }

    public void RememberFocus()
    {
        lock (_gate)
        {
            var foreground = GetForegroundWindow();
            if (_disposed || !OwnsCore(foreground)) return;
            try { _lastFocused = WindowAccess.Identify(foreground); } catch { }
        }
    }

    /// <summary>Cheap focus check for per-character input guards; no discovery or window movement.</summary>
    public bool IsForegroundOwned(WindowTarget? selected = null)
    {
        lock (_gate)
        {
            var foreground = GetForegroundWindow();
            return !_disposed && (selected is null || MatchesWindow(selected, foreground)) && OwnsCore(foreground);
        }
    }

    internal IntPtr OwnedKeyboardFocus(WindowTarget? selected = null)
    {
        IntPtr focus = TextTyping.KeyboardFocus();
        if (focus == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = GetAncestor(focus, 2);
        lock (_gate)
            return !_disposed && root != IntPtr.Zero && root == GetForegroundWindow()
                && (selected is null || MatchesWindow(selected, root)) && OwnsCore(root) ? focus : IntPtr.Zero;
    }

    internal async Task<bool> WaitForKeyboardFocusAsync(CancellationToken cancel, WindowTarget? selected = null)
    {
        long end = Environment.TickCount64 + 350;
        do
        {
            cancel.ThrowIfCancellationRequested();
            var focus = OwnedKeyboardFocus(selected);
            if (focus != IntPtr.Zero && TextTyping.Responsive(focus, 50) == true)
            {
                // The foreground HWND may change before the new control receives focus.
                await Task.Delay(20, cancel).ConfigureAwait(false);
                if (OwnedKeyboardFocus(selected) == focus) return true;
            }
            await Task.Delay(10, cancel).ConfigureAwait(false);
        } while (Environment.TickCount64 < end);
        return false;
    }

    /// <summary>Rejects clicks intercepted by the taskbar, an unknown app, or another layer.</summary>
    public bool HitTestOwned(Point globalPoint, WindowTarget? selected = null)
    {
        if (!Bounds.Contains(globalPoint)) return false;
        var hit = WindowFromPoint(globalPoint);
        if (hit == IntPtr.Zero) return false;
        var root = GetAncestor(hit, 2); // GA_ROOT: ownership is attached to top-level app windows.
        return root != IntPtr.Zero && (selected is null || MatchesWindow(selected, root)) && Owns(root);
    }

    private static bool MatchesWindow(WindowTarget target, IntPtr handle)
    {
        if (handle.ToInt64() != target.Handle) return false;
        try { return WindowAccess.Verify(target) == handle; }
        catch { return false; }
    }

    /// <summary>Confirm all processes in this layer's jobs exited before the slot becomes available.</summary>
    public async Task CloseOwnedAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var job in _jobs.Values)
                if (job.ActiveProcesses > 0) job.Terminate();
        }
        long deadline = Environment.TickCount64 + 1000;
        while (true)
        {
            lock (_gate)
                if (_jobs.Values.All(job => job.ActiveProcesses == 0)) return;
            if (Environment.TickCount64 >= deadline) throw new TimeoutException("An owned application is still exiting.");
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            foreach (var job in _jobs.Values) job.Dispose();
            _jobs.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LayerWindows));
    }

    private static string ClassName(IntPtr handle)
    {
        var text = new StringBuilder(256);
        GetClassName(handle, text, text.Capacity);
        return text.ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowPlacement { public int Length, Flags, ShowCommand; public Point Minimum, Maximum; public Rect Normal; }
    private delegate bool EnumProc(IntPtr window, IntPtr parameter);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
}
