using System.Runtime.InteropServices;
using System.Threading;

namespace NotIdle.Core.Native;

/// <summary>
/// Holds the Windows "keep this session awake" request via
/// <c>SetThreadExecutionState</c> — the supported, non-synthetic mechanism for
/// preventing sleep, and the same one media players and installers use.
///
/// <para><b>Why a dedicated thread.</b> The execution-state flags are a property of
/// the <em>calling thread</em> and are dropped when that thread exits. The
/// controller ticks on thread-pool threads, which retire freely, so asserting the
/// state from there would silently lose it. This class owns one long-lived
/// background thread whose only job is to hold the request for as long as it is
/// wanted.</para>
/// </summary>
public sealed class Win32ExecutionState : IExecutionStateController, IDisposable
{
    private const uint ES_CONTINUOUS = 0x8000_0000;
    private const uint ES_SYSTEM_REQUIRED = 0x0000_0001;
    private const uint ES_DISPLAY_REQUIRED = 0x0000_0002;

    private readonly Thread _holder;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();

    private uint _desiredFlags = ES_CONTINUOUS; // "no special requirements"
    private uint _appliedFlags = ES_CONTINUOUS;
    private volatile bool _stopping;

    public Win32ExecutionState()
    {
        _holder = new Thread(HolderLoop)
        {
            IsBackground = true,
            Name = "NotIdle.ExecutionState",
        };
        _holder.Start();
    }

    public void Acquire(bool keepDisplayOn)
    {
        uint flags = ES_CONTINUOUS | ES_SYSTEM_REQUIRED;
        if (keepDisplayOn) flags |= ES_DISPLAY_REQUIRED;
        Request(flags);
    }

    public void Release() => Request(ES_CONTINUOUS);

    private void Request(uint flags)
    {
        lock (_gate)
        {
            if (_stopping || _desiredFlags == flags) return;
            _desiredFlags = flags;
        }
        _wake.Release();
    }

    private void HolderLoop()
    {
        while (true)
        {
            _wake.Wait();

            uint flags;
            lock (_gate)
            {
                if (_stopping) break;
                if (_desiredFlags == _appliedFlags) continue;
                flags = _desiredFlags;
                _appliedFlags = flags;
            }

            // A zero return means the call failed; there is nothing useful to do
            // about it beyond not crashing a background utility over power policy.
            _ = SetThreadExecutionState(flags);
        }

        // Leave the session exactly as we found it.
        _ = SetThreadExecutionState(ES_CONTINUOUS);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
        }

        _wake.Release();
        _holder.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);
}
