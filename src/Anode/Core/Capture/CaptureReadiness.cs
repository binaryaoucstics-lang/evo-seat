using System.ComponentModel;


namespace Anode.Core.Capture;

/// <summary>Retries transient read-only capture failures with a shorter budget after the first frame.</summary>
internal sealed class CaptureReadiness
{
    private int _hasCaptured;
    internal T Run<T>(Func<T> probe, Func<bool> disconnected, int timeoutMs, CancellationToken cancel,
        Func<long>? now = null, Action<int, CancellationToken>? wait = null)
    {
        now ??= () => Environment.TickCount64;
        wait ??= (ms, token) => { if (token.WaitHandle.WaitOne(ms)) token.ThrowIfCancellationRequested(); };
        if (Volatile.Read(ref _hasCaptured) != 0) timeoutMs = Math.Min(timeoutMs, 500);
        long started = now();
        Win32Exception? lastError = null;
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            if (lastError is not null && now() - started >= Math.Max(0, timeoutMs))
                throw new InvalidOperationException(ScreenCapture.UnavailableMessage(disconnected()), lastError);
            try
            {
                T result = probe();
                Interlocked.Exchange(ref _hasCaptured, 1);
                return result;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 6)
            {
                bool detached = disconnected();
                long remaining = (long)Math.Max(0, timeoutMs) - (now() - started);
                if (detached || remaining <= 0)
                    throw new InvalidOperationException(ScreenCapture.UnavailableMessage(detached), ex);
                lastError = ex;
                wait((int)Math.Min(100, remaining), cancel);
            }
        }
    }
}
