using System.Diagnostics;

namespace StageBug;

public enum StageBugSessionState
{
    Idle,
    CrossFireDetected,
    Initialized
}

public sealed class SessionController
{
    public StageBugSessionState State { get; private set; } = StageBugSessionState.Idle;
    public int? CrossFireProcessId { get; private set; }
    public DateTimeOffset? InitializedAt { get; private set; }

    public bool RefreshCrossFire()
    {
        var process = Process.GetProcessesByName("crossfire").FirstOrDefault();
        CrossFireProcessId = process?.Id;
        if (process is null)
        {
            State = StageBugSessionState.Idle;
            return false;
        }

        if (State != StageBugSessionState.Initialized)
            State = StageBugSessionState.CrossFireDetected;
        return true;
    }

    public bool InitializeSession(out string message)
    {
        if (!RefreshCrossFire())
        {
            message = "CrossFire is not running.";
            return false;
        }

        State = StageBugSessionState.Initialized;
        InitializedAt = DateTimeOffset.Now;
        message = $"Session ready for CrossFire PID {CrossFireProcessId}.";
        return true;
    }

    public bool TryTriggerBoost(int boostNumber, out string message)
    {
        if (State != StageBugSessionState.Initialized)
        {
            message = "Initialize the session first.";
            return false;
        }

        if (!RefreshCrossFire())
        {
            message = "CrossFire closed; session was reset.";
            return false;
        }

        // The original protected boost operation is intentionally not fabricated here.
        // This controller only validates and tracks the local session state.
        message = $"Boost {boostNumber} is ready to dispatch through an authorized integration.";
        return true;
    }
}
