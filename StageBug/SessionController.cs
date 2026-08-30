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
        var newPid = process?.Id;

        if (newPid is null)
        {
            CrossFireProcessId = null;
            ResetState("CrossFire process disappeared");
            return false;
        }

        if (CrossFireProcessId.HasValue && CrossFireProcessId.Value != newPid.Value)
        {
            ResetState($"CrossFire instance changed: {CrossFireProcessId.Value} -> {newPid.Value}");
        }

        CrossFireProcessId = newPid;
        if (State == StageBugSessionState.Idle)
        {
            State = StageBugSessionState.CrossFireDetected;
            StageBugDiagnostics.Info($"State -> {State}; PID={CrossFireProcessId}");
        }

        return true;
    }

    public bool InitializeSession(out string message)
    {
        if (!RefreshCrossFire())
        {
            message = "CrossFire is not running.";
            StageBugDiagnostics.Warning(message);
            return false;
        }

        State = StageBugSessionState.Initialized;
        InitializedAt = DateTimeOffset.Now;
        Boost1AppliedAt = null;
        Boost2AppliedAt = null;
        message = $"Session ready for CrossFire PID {CrossFireProcessId}.";
        StageBugDiagnostics.Info(message);
        return true;
    }

    public bool TryTriggerBoost(int boostNumber, out string message)
    {
        if (!RefreshCrossFire())
        {
            message = "CrossFire closed; session was reset.";
            StageBugDiagnostics.Warning(message);
            return false;
        }

        if (boostNumber is not 1 and not 2)
        {
            message = "Unknown boost stage.";
            StageBugDiagnostics.Warning(message);
            return false;
        }

        if (boostNumber == 1)
        {
            if (State is not StageBugSessionState.Initialized)
            {
                message = "Initialize the session first.";
                StageBugDiagnostics.Warning(message);
                return false;
            }

            State = StageBugSessionState.Boost1Applied;
            Boost1AppliedAt = DateTimeOffset.Now;
            Boost2AppliedAt = null;
            message = "Boost 1 state applied; ready for Boost 2.";
            StageBugDiagnostics.Info(message);
            return true;
        }

        if (State is not StageBugSessionState.Boost1Applied)
        {
            message = "Boost 2 is locked until Boost 1 is applied.";
            StageBugDiagnostics.Warning(message);
            return false;
        }

        State = StageBugSessionState.Boost2Applied;
        Boost2AppliedAt = DateTimeOffset.Now;
        message = "Boost 2 state applied.";
        StageBugDiagnostics.Info(message);
        return true;
    }

    private void ResetState(string reason)
    {
        if (State != StageBugSessionState.Idle)
            StageBugDiagnostics.Warning($"State reset: {reason}");

        State = StageBugSessionState.Idle;
        InitializedAt = null;
        Boost1AppliedAt = null;
        Boost2AppliedAt = null;
    }
}
