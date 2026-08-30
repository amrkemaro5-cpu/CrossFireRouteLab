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
        var process = Process.GetProcessesByName("crossfire").FirstOrDefault();
        CrossFireProcessId = process?.Id;
        if (process is null)
        {
            State = StageBugSessionState.Idle;
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

        State = StageBugSessionState.Initialized;
        InitializedAt = DateTimeOffset.Now;
        Boost1AppliedAt = null;
        Boost2AppliedAt = null;
        message = $"Session ready for CrossFire PID {CrossFireProcessId}.";
        return true;
    }

    public bool TryTriggerBoost(int boostNumber, out string message)
    {
        if (!RefreshCrossFire())
        {
            message = "CrossFire closed; session was reset.";
            return false;
        }

        if (boostNumber is not 1 and not 2)
        {
            message = "Unknown boost stage.";
            return false;
        }

        if (boostNumber == 1)
        {
            if (State is not StageBugSessionState.Initialized)
            {
                message = "Initialize the session first.";
                return false;
            }

            State = StageBugSessionState.Boost1Applied;
            Boost1AppliedAt = DateTimeOffset.Now;
            message = "Boost 1 state applied; ready for Boost 2.";
            return true;
        }

        if (State is not StageBugSessionState.Boost1Applied)
        {
            message = "Boost 2 is locked until Boost 1 is applied.";
            return false;
        }

        State = StageBugSessionState.Boost2Applied;
        Boost2AppliedAt = DateTimeOffset.Now;
        message = "Boost 2 state applied.";
        return true;
    }
}
