using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Anode.Core.Bridge;
using Anode.Core.Desktop;
using Anode.Core.Util;

namespace Anode.Core.Layers;

/// <summary>In-memory clipboard belonging to one layer. The caller serializes turns with the physical input lock.</summary>
internal sealed class LayerClipboard
{
    internal sealed class Snapshot
    {
        internal readonly Dictionary<string, object> Formats;
        private readonly bool _transient;
        internal Snapshot(Dictionary<string, object> formats, bool transient = false) { Formats = formats; _transient = transient; }
        internal static Snapshot Empty() => new(new(StringComparer.Ordinal));
        internal static Snapshot Text(string text) => new(new(StringComparer.Ordinal) { [DataFormats.UnicodeText] = text });
        internal Snapshot AsTransient() => _transient ? this : new(Formats, transient: true);
        internal IEnumerable<KeyValuePair<string, object>> NativeFormats()
        {
            // Internal clipboard swaps are not new user copies. Advertise the opt-out
            // before content so the Windows history service need not render every swap.
            // Normal exclusive API writes retain their original policy.
            if (_transient && Formats.Count > 0)
                foreach (string name in HistoryFormats) yield return new(name, new MemoryStream(new byte[4]));
            foreach (var item in Formats)
                if (!_transient || !HistoryFormats.Contains(item.Key, StringComparer.OrdinalIgnoreCase)) yield return item;
        }
    }
    private static readonly string[] HistoryFormats = { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" };
    internal interface IBackend
    {
        Snapshot Read();
        void Write(Snapshot snapshot);
        uint Sequence();
    }
    private readonly object _sync = new();
    private Snapshot _contents = Snapshot.Empty();
    private readonly IBackend _backend;
    private readonly Recovery _recovery;
    private static readonly ConditionalWeakTable<IBackend, Recovery> Recoveries = new();
    private static readonly Lazy<IBackend> SystemBackend = new(() => new WorkerBackend());
    internal LayerClipboard() : this(SystemBackend.Value) { }
    internal LayerClipboard(IBackend backend) { _backend = backend; _recovery = Recoveries.GetValue(backend, b => new Recovery(b)); }
    internal static void RecoverSystem()
    {
        if (SystemBackend.IsValueCreated) Recoveries.GetValue(SystemBackend.Value, b => new Recovery(b)).EnsureRestored();
    }
    internal static string ReadSystemText() { RecoverSystem(); return ReadTextFrom(SystemBackend.Value.Read()); }
    internal static void WriteSystemText(string text) { RecoverSystem(); SystemBackend.Value.Write(Snapshot.Text(text)); }
    // A failed restoration survives a layer's retirement. Only another clipboard
    // operation needs to wait for it; ordinary input never touches this backend.
    private sealed class Recovery(IBackend backend)
    {
        private readonly object _gate = new();
        private Snapshot? _pending;
        internal void EnsureRestored()
        {
            lock (_gate)
            {
                if (_pending is null) return;
                try { backend.Write(_pending); _pending = null; }
                catch (Exception ex) { throw new InvalidOperationException("Shared clipboard recovery is pending; no new input was sent. " + Cause(ex), ex); }
            }
        }
        internal void Restore(Snapshot prior)
        {
            lock (_gate)
            {
                _pending = prior.AsTransient();
                backend.Write(_pending);
                _pending = null;
            }
        }
    }
    private static string Cause(Exception ex) => $"{ex.GetType().Name} (0x{ex.HResult:X8}): {ex.Message}";
    internal void WriteText(string text) { lock (_sync) _contents = Snapshot.Text(text); }
    internal string ReadText()
    {
        lock (_sync) return ReadTextFrom(_contents);
    }
    private static string ReadTextFrom(Snapshot snapshot)
    {
        if (snapshot.Formats.Count == 0) return "";
        if (snapshot.Formats.TryGetValue(DataFormats.UnicodeText, out var value) && value is string text) return text;
        if (snapshot.Formats.TryGetValue(DataFormats.Text, out value) && value is string ansi) return ansi;
        throw new InvalidOperationException("Clipboard does not contain text.");
    }

    // Call only inside the shared input lock, and always Complete even when input fails.
    internal Turn BeginTurn()
    {
        _recovery.EnsureRestored();
        var prior = _backend.Read();
        Snapshot current; lock (_sync) current = _contents;
        uint sequence;
        try { _backend.Write(current.AsTransient()); sequence = _backend.Sequence(); }
        catch (Exception activation)
        {
            try { _recovery.Restore(prior); }
            catch (Exception restore) { throw new AggregateException("Clipboard activation and restoration failed.", activation, restore); }
            throw;
        }
        return new Turn(this, prior, sequence);
    }
    internal sealed class Turn
    {
        private readonly LayerClipboard _owner;
        private readonly Snapshot _prior;
        private readonly uint _sequence;
        private bool _finished;
        internal Turn(LayerClipboard owner, Snapshot prior, uint sequence) { _owner = owner; _prior = prior; _sequence = sequence; }
        // copy=true waits for a clipboard change; paste=true gives delayed consumers time to read.
        // Input is never repeated. A copy with no observed change is an explicit failure.
        internal void Complete(bool copy = false, bool paste = false)
        {
            if (_finished) throw new InvalidOperationException("Clipboard turn already completed.");
            _finished = true;
            Exception? captureError = null;
            Snapshot? captured = null;
            try
            {
                if (copy)
                {
                    long end = Environment.TickCount64 + 700;
                    while (_owner._backend.Sequence() == _sequence && Environment.TickCount64 < end) Thread.Sleep(20);
                    if (_owner._backend.Sequence() == _sequence) throw new InvalidOperationException("Copy did not produce an observable clipboard update; input may have been delivered.");
                    // Allow asynchronously rendered formats to become available.
                    Thread.Sleep(50);
                }
                else if (paste) Thread.Sleep(150);
                // A non-copy operation may still have changed the clipboard.
                if (_owner._backend.Sequence() != _sequence) captured = _owner._backend.Read();
            }
            catch (Exception ex) { captureError = ex; }
            try { _owner._recovery.Restore(_prior); }
            catch (Exception restore)
            {
                if (captureError is not null) throw new AggregateException("Clipboard capture and restoration failed.", captureError, restore);
                throw new InvalidOperationException("Could not restore the shared clipboard after the input turn; further clipboard shortcuts require recovery. " + Cause(restore), restore);
            }
            if (captureError is not null) throw new InvalidOperationException("Could not capture the layer clipboard after input; shared clipboard was restored.", captureError);
            if (captured is not null) lock (_owner._sync) _owner._contents = captured;
        }
    }

    // Delayed rendering may wait in a foreign app's OLE handler. Use a process
    // so the deadline retires even a call already inside native code.
    private sealed class WorkerBackend : IBackend
    {
        private readonly DesktopWorker.Session _worker = new();
        private JsonObject Call(JsonObject request) =>
            _worker.CallAsync(request, timeout: TimeSpan.FromMilliseconds(1200)).GetAwaiter().GetResult();
        public uint Sequence() => GetClipboardSequenceNumber();
        public Snapshot Read() => Decode(Call(new JsonObject { ["op"] = "clipboard.read" }));
        public void Write(Snapshot snapshot) => Call(new JsonObject
            { ["op"] = "clipboard.write", ["formats"] = Encode(snapshot.NativeFormats()) });
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    }

    internal static JsonObject ExecuteNative(JsonObject request) => ClipboardNativeRetry.Run(() =>
    {
        if (request.Str("op") == "clipboard.read")
        {
            var data = Clipboard.GetDataObject();
            var formats = data is null ? new JsonObject() : Encode(data.GetFormats(false)
                .Select(format => new KeyValuePair<string, object>(format, data.GetData(format, false)!)));
            return new JsonObject { ["formats"] = formats };
        }
        var snapshot = Decode(request);
        try
        {
            if (snapshot.Formats.Count == 0) Clipboard.Clear();
            else
            {
                var data = new DataObject();
                foreach (var item in snapshot.Formats) data.SetData(item.Key, false, item.Value);
                Clipboard.SetDataObject(data, true, 3, 30);
            }
        }
        finally
        {
            foreach (var value in snapshot.Formats.Values.OfType<IDisposable>()) value.Dispose();
        }
        return new JsonObject();
    }, timeoutMs: 800);

    private static JsonObject Encode(IEnumerable<KeyValuePair<string, object>> formats)
    {
        var result = new JsonObject();
        foreach (var (format, value) in formats)
        {
            (string Type, JsonNode Data) item = value switch
            {
                string text => ("text", JsonValue.Create(text)!),
                byte[] bytes => ("bytes", JsonValue.Create(Convert.ToBase64String(bytes))!),
                MemoryStream stream => ("stream", JsonValue.Create(Convert.ToBase64String(stream.ToArray()))!),
                StringCollection list => ("strings", new JsonArray(list.Cast<string>().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())),
                Bitmap bitmap => ("bitmap", JsonValue.Create(EncodeBitmap(bitmap))!),
                _ => throw new NotSupportedException($"Clipboard format '{format}' cannot be safely preserved ({value?.GetType().Name ?? "null"}).")
            };
            result.Add(format, new JsonObject { ["type"] = item.Type, ["data"] = item.Data });
        }
        return result;
    }
    private static Snapshot Decode(JsonObject request)
    {
        var formats = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (format, node) in request.Obj("formats")!)
        {
            var item = node!.AsObject();
            formats.Add(format, item.Str("type") switch
            {
                "text" => item.Str("data")!,
                "bytes" => Convert.FromBase64String(item.Str("data")!),
                "stream" => new MemoryStream(Convert.FromBase64String(item.Str("data")!)),
                "strings" => DecodeStrings(item["data"]!.AsArray()),
                "bitmap" => DecodeBitmap(item.Str("data")!),
                _ => throw new NotSupportedException($"Unknown clipboard format type for '{format}'.")
            });
        }
        return new Snapshot(formats);
    }
    private static StringCollection DecodeStrings(JsonArray data)
    {
        var list = new StringCollection();
        foreach (var value in data) list.Add(value!.GetValue<string>());
        return list;
    }
    private static string EncodeBitmap(Bitmap bitmap)
    {
        using var bytes = new MemoryStream();
        bitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToBase64String(bytes.ToArray());
    }
    private static Bitmap DecodeBitmap(string data)
    {
        using var bytes = new MemoryStream(Convert.FromBase64String(data));
        using var bitmap = new Bitmap(bytes);
        return new Bitmap(bitmap);
    }
}

/// <summary>Only retry CLIPBRD_E_CANT_OPEN, never input or unsupported format errors.</summary>
internal static class ClipboardNativeRetry
{
    internal static T Run<T>(Func<T> operation, int timeoutMs = 5000, Func<long>? now = null, Action<int>? wait = null)
    {
        bool diagnostics = now is null && wait is null;
        now ??= () => Environment.TickCount64;
        wait ??= Thread.Sleep;
        long started = now();
        int attempts = 0;
        while (true)
        {
            try
            {
                attempts++;
                T result = operation();
                if (diagnostics && attempts > 1) Log.Info($"clipboard recovered attempts={attempts} elapsedMs={now() - started}");
                return result;
            }
            catch (ExternalException ex) when (ex.HResult == unchecked((int)0x800401D0))
            {
                if (diagnostics && attempts == 1) Log.Warn($"clipboard busy {Ownership()} timeoutMs={timeoutMs}");
                long remaining = timeoutMs - (now() - started);
                if (remaining <= 0)
                {
                    if (diagnostics) Log.Warn($"clipboard busy deadline attempts={attempts} elapsedMs={now() - started} {Ownership()}");
                    throw;
                }
                wait((int)Math.Min(50, remaining));
                if (now() - started >= timeoutMs)
                {
                    if (diagnostics) Log.Warn($"clipboard busy deadline attempts={attempts} elapsedMs={now() - started} {Ownership()}");
                    throw;
                }
            }
        }
    }
    private static string Ownership()
    {
        IntPtr open = GetOpenClipboardWindow(), owner = GetClipboardOwner();
        GetWindowThreadProcessId(open, out uint openPid); GetWindowThreadProcessId(owner, out uint ownerPid);
        // An app may OpenClipboard(NULL); zero is unknown, not proof of availability.
        return $"openHwnd={open.ToInt64()} openPid={openPid} ownerPid={ownerPid}";
    }
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
