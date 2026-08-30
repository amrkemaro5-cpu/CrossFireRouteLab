using System.Diagnostics;

namespace StageBug;

public enum StageBugSessionState
{
    Idle,
    CrossFireDetected,
    Initialized,
    Boost1Applied,
    Boost2Applied
}

public sealed class SessionController
{
    public StageBugSessionState State { get; private set; } = StageBugSessionState.Idle;
    public int? CrossFireProcessId { get; private set; }
    public DateTimeOffset? InitializedAt { get; private set; }
    public DateTimeOffset? Boost1AppliedAt { get; private set; }
    public DateTimeOffset? Boost2AppliedAt { get; private set; }

    public bool RefreshCrossFire()
    {
        using var process = Process.GetProcessesByName("crossfire").FirstOrDefault();
        CrossFireProcessId = process?.Id;

        if (process is null)
        {
            Reset();
            return false;
        }

        if (State == StageBugSessionState.Idle)
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

        // Recoverable behavior: initialization is a local state transition.
        // The original's protected/network handshake is not fabricated here.
        State = StageBugSessionState.Initialized;
        InitializedAt = DateTimeOffset.Now;
        Boost1AppliedAt = null;
        Boost2AppliedAt = null;
        message = "Session Initialized!";
        return true;
    }

    public bool TryTriggerBoost(int boostNumber, out string message)
    {
        if (!RefreshCrossFire())
        {
            message = "CrossFire closed; session was reset.";
            return false;
        }

        switch (boostNumber)
        {
            case 1 when State != StageBugSessionState.Initialized:
                message = "Step 2: Locked (Requires active session)";
                return false;

            case 1:
                Boost1AppliedAt = DateTimeOffset.Now;
                State = StageBugSessionState.Boost1Applied;
                message = "BOOST 1 (PRIMARY) READY";
                return true;

            case 2 when State != StageBugSessionState.Boost1Applied:
                message = "Step 3: Locked (Requires Boost 1)";
                return false;

            case 2:
                Boost2AppliedAt = DateTimeOffset.Now;
                State = StageBugSessionState.Boost2Applied;
                message = "BOOST 2 (FINAL BUFF) READY";
                return true;

            default:
                message = "Unknown boost stage.";
                return false;
        }
    }

    private void Reset()
    {
        State = StageBugSessionState.Idle;
        CrossFireProcessId = null;
        InitializedAt = null;
        Boost1AppliedAt = null;
        Boost2AppliedAt = null;
    }
}
