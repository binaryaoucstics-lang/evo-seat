using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Anode.Core.Bridge;
using Anode.Core.Layers;
using Anode.Core.Util;

namespace Anode.Core.Desktop;

internal static class DesktopWorker
{
    // The CLI entry point is STA. Keep each worker's UIA calls on the same MTA
    // thread so a busy seat does not reschedule every action through its pool.
    public static int Run(bool foreground = false) => Task.Run(() => RunWorker(foreground)).GetAwaiter().GetResult();

    private static int RunWorker(bool foreground)
    {
        // Window and control bounds in the seat's physical pixels, the space its screenshots and input use at every scale.
        Display.SeatDisplay.UsePhysicalPixels();
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            if (foreground) ForegroundHost.VerifyDesktop();
            else Seat.SeatHost.VerifyCurrentSession();
            using var input = new StreamReader(Console.OpenStandardInput());
            bool persistent;
            do
            {
                string? line = input.ReadLine();
                if (line is null) return 0;
                if (line.Length > 4_000_000) throw new ArgumentException("Worker request is too large.");
                var request = JsonLine.Parse(line) ?? throw new ArgumentException("Invalid worker request.");
                if (foreground) ForegroundHost.VerifyDesktop();
                bool clipboard = request.Str("op") is "clipboard.read" or "clipboard.write";
                if (!clipboard && line.Length > 131072) throw new ArgumentException("Worker request is too large.");
                persistent = request.Bool("persistentWorker") == true;
                var result = clipboard ? LayerClipboard.ExecuteNative(request) : Execute(request, foreground);
                output.WriteLine(JsonLine.Ok(result).ToJsonString());
            } while (persistent);
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(JsonLine.Fail(ex.Message).ToJsonString());
            return 1;
        }
    }

    internal static JsonObject Execute(JsonObject request, bool foreground = false)
    {
        string op = request.Str("op") ?? "";
        if (op == "ready") return new JsonObject();
        if (op == "windows") return new JsonObject { ["windows"] = WindowAccess.List() };
        if (op == "capabilities") return SeatCapabilities.Read(request.Bool("probeCapture") ?? true, foreground);
        var target = WindowTarget.FromJson(request.Obj("target") ?? throw new ArgumentException("Missing worker target."));
        return op switch
        {
            "observe" => AccessibilityReader.Observe(target, request),
            "observe_native" => NativeFormObservation.TryRead(target, request) ?? new JsonObject { ["useManaged"] = true },
            "element" when request.Str("action") == "set_values" => SetValues(target, request["edits"]!.AsArray()),
            "element" => Act(target, request.Obj("element") ?? throw new ArgumentException("Missing element."), request),
            "window" => WindowAccess.Act(target, request.Str("action") ?? "", request),
            _ => throw new ArgumentException("Unknown desktop worker operation.")
        };
    }

    internal static bool UsesNativeReference(JsonObject request) =>
        request.Obj("element")?.Bool("nativeReference") == true ||
        request["edits"] is JsonArray edits && edits.OfType<JsonObject>().Any(edit => edit.Obj("element")?.Bool("nativeReference") == true);

    private static JsonObject Act(WindowTarget target, JsonObject element, JsonObject request) =>
        element.Bool("nativeReference") == true ? NativeFormObservation.Act(target, element, request) : AccessibilityReader.Act(target, element, request);

    private static JsonObject SetValues(WindowTarget target, JsonArray edits)
    {
        var completed = new JsonArray();
        foreach (var edit in edits.Cast<JsonObject>())
        {
            var element = edit.Obj("element")!;
            string id = element.Str("id")!;
            try
            {
                var result = Act(target, element, new JsonObject { ["action"] = "set_value", ["value"] = edit.Str("value") });
                result["elementId"] = id;
                completed.Add(result);
            }
            catch (Exception ex)
            {
                return new JsonObject { ["completed"] = completed, ["failedElementId"] = id,
                    ["batchError"] = $"Batch stopped at {id} after {completed.Count} completed actions. Inspect before acting again. {ex.Message}" };
            }
        }
        return new JsonObject { ["completed"] = completed, ["note"] = "Observation consumed. Inspect before acting again." };
    }

    public static async Task<JsonObject> CallAsync(JsonObject request, CancellationToken cancel = default)
    {
        using var process = Start();
        return await ExchangeAsync(process, request, TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
    }

    public static async Task<JsonObject> CallForegroundAsync(JsonObject request, CancellationToken cancel)
    {
        using var process = Start(foreground: true);
        return await ExchangeAsync(process, request, TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
    }

    private static Process Start() => Start(foreground: false);

    private static Process Start(bool foreground)
    {
        var info = new ProcessStartInfo(Env.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(foreground ? "__foreground-worker" : "__desktop-worker");
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start the seat inspection worker.");
    }

    /// <summary>One bounded worker per layer. A failed/uncertain request is never retried.</summary>
    internal sealed class Session(Func<Process>? start = null) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Process? _process;
        private int _requests;
        private bool _disposed, _retiring;

        private bool NeedsStart => _process is null || _retiring || _process.HasExited || _requests >= 128;

        // The layer gate serializes calls here. Warm a cold worker before taking the seat's input turn.
        internal Task WarmAsync(CancellationToken cancel = default) =>
            NeedsStart ? CallAsync(new JsonObject { ["op"] = "ready" }, cancel) : Task.CompletedTask;

        internal async Task<JsonObject> CallAsync(JsonObject request, CancellationToken cancel = default, TimeSpan? timeout = null)
        {
            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancel.ThrowIfCancellationRequested();
                if (NeedsStart)
                {
                    Stop(); _process = (start ?? Start)(); _requests = 0;
                    _process.BeginErrorReadLine();
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
                try
                {
                    var message = (JsonObject)request.DeepClone(); message["persistentWorker"] = true;
                    var process = _process!;
                    await process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), deadline.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
                    string line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The accessibility worker exited without a reply; observe before acting again.");
                    if (line.Length > 4_000_000) throw new InvalidOperationException("The accessibility result exceeded its size limit.");
                    _requests++;
                    return ParseResult(line);
                }
                catch (OperationCanceledException)
                {
                    Stop();
                    if (cancel.IsCancellationRequested) throw;
                    throw new TimeoutException(request.Str("op") switch
                    {
                        "clipboard.read" or "clipboard.write" => "The system clipboard did not respond. Its worker was retired.",
                        "element" or "window" => "The app did not finish the action within its deadline. It may already have taken effect. Inspect again; do not replay it.",
                        _ => "The app's accessibility provider did not respond. The worker was retired; inspect again."
                    });
                }
                catch { Stop(); throw; }
            }
            finally { _gate.Release(); }
        }

        private void Stop()
        {
            if (_process is null) return;
            _retiring = true;
            // This worker has no owned children. Do not wait on inherited stderr handles.
            if (!_process.HasExited)
            {
                try { _process.Kill(); }
                catch (InvalidOperationException) when (_process.HasExited) { }
            }
            if (!_process.WaitForExit(500)) throw new TimeoutException("The accessibility worker is still exiting.");
            _process.Dispose();
            _process = null;
            _retiring = false;
        }
        public void Dispose()
        {
            _gate.Wait();
            try { _disposed = true; Stop(); }
            finally { _gate.Release(); }
        }
    }

    internal static async Task<JsonObject> ExchangeAsync(Process process, JsonObject request, TimeSpan timeout, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(timeout);
        try
        {
            // Never log requests: value actions can contain document or form contents.
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), deadline.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            string result = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (result.Length > 4_000_000) throw new InvalidOperationException("The accessibility result exceeded its size limit.");
            return ParseResult(result);
        }
        catch (OperationCanceledException)
        {
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException(request.Str("op") is "element" or "window"
                ? "The app did not finish the action within 10 seconds. It may already have taken effect. Inspect again; do not replay it."
                : "The app's accessibility provider did not respond within 10 seconds. The seat remains available; inspect another window or try a smaller tree.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }

    internal static JsonObject ParseResult(string json)
    {
        var envelope = JsonLine.Parse(json) ?? throw new InvalidOperationException("The seat worker returned no valid result.");
        if (envelope.Bool("ok") != true) throw new InvalidOperationException(envelope.Str("error") ?? "Seat inspection failed.");
        return envelope.Obj("result")?.DeepClone().AsObject() ?? new JsonObject();
    }
}
