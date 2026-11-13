using System.ComponentModel;
using Anode.Core.Capture;

namespace Anode.Cli;

internal static class CaptureReadinessChecks
{
    internal static string Run()
    {
        long clock = 0;
        int attempts = 0;
        var readiness = new CaptureReadiness();
        int value = readiness.Run(() => ++attempts < 3 ? throw new Win32Exception(6) : 42,
            () => false, 500, CancellationToken.None, () => clock, (ms, _) => clock += ms);
        Require(value == 42 && attempts == 3 && clock == 200, "initial transient retry");
        attempts = 0;
        value = readiness.Run(() => ++attempts == 1 ? throw new Win32Exception(5) : 43,
            () => false, 5000, CancellationToken.None, () => clock, (ms, _) => clock += ms);
        Require(value == 43 && attempts == 2, "warm capture retries a transient error");

        clock = 0; attempts = 0;
        try { new CaptureReadiness().Run<int>(() => { attempts++; throw new Win32Exception(5); }, () => false, 250,
            CancellationToken.None, () => clock, (ms, _) => clock += ms); }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception) { }
        Require(clock == 250 && attempts == 3, "deadline");
        attempts = 0;
        try { new CaptureReadiness().Run<int>(() => { attempts++; throw new Win32Exception(6); }, () => true, 500,
            CancellationToken.None, () => clock, (ms, _) => clock += ms); }
        catch (InvalidOperationException) { }
        Require(attempts == 1, "disconnected session");
        attempts = 0;
        try { new CaptureReadiness().Run<int>(() => { attempts++; throw new Win32Exception(87); }, () => false, 500,
            CancellationToken.None, () => clock, (ms, _) => clock += ms); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 87) { }
        Require(attempts == 1, "non-transient error");
        using var cts = new CancellationTokenSource();
        attempts = 0;
        try { new CaptureReadiness().Run<int>(() => { attempts++; throw new Win32Exception(6); }, () => false, 500,
            cts.Token, () => clock, (_, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); }); }
        catch (OperationCanceledException) { }
        Require(attempts == 1 && cts.IsCancellationRequested, "cancellation");
        return "initial retry, bounded failure, disconnect, cancellation and permanent errors";
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Capture readiness: " + message); }
}
