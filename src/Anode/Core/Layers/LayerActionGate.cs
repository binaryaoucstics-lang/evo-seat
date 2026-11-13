namespace Anode.Core.Layers;

/// <summary>Allows owned-window actions in different layers to overlap and reserves the seat for foreground actions.</summary>
internal sealed class LayerActionGate
{
    private readonly SemaphoreSlim _arrival = new(1, 1);
    private readonly SemaphoreSlim _idle = new(1, 1);
    private readonly object _state = new();
    private int _sharedActions;

    public async Task EnterAsync(bool shared, CancellationToken cancel)
    {
        // A foreground action waiting for the active batch holds arrival so later actions cannot delay it.
        await _arrival.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (!shared)
            {
                await _idle.WaitAsync(cancel).ConfigureAwait(false);
                return;
            }
            lock (_state)
            {
                if (_sharedActions > 0)
                {
                    _sharedActions++;
                    return;
                }
            }
            await _idle.WaitAsync(cancel).ConfigureAwait(false);
            lock (_state) _sharedActions = 1;
        }
        finally { _arrival.Release(); }
    }

    public void Exit(bool shared)
    {
        if (!shared) { _idle.Release(); return; }
        lock (_state)
            if (--_sharedActions == 0) _idle.Release();
    }
}
