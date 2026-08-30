namespace StageBug;

public enum RoomState
{
    None,
    Creating,
    Active,
    Restored,
    Closed
}

public sealed class RoomStateModel
{
    public RoomState State { get; private set; } = RoomState.None;
    public string? RoomCode { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public bool Restore(out string message)
    {
        // The original runtime has CheckAndRestoreActiveRoom()/storeActiveRoom.
        // Without the protected backend, this reconstruction only models the
        // local state transition and never fabricates a server response.
        if (string.IsNullOrWhiteSpace(RoomCode))
        {
            message = "No saved active room was found.";
            return false;
        }

        State = RoomState.Restored;
        UpdatedAt = DateTimeOffset.Now;
        message = $"Room {RoomCode} restored locally.";
        return true;
    }

    public bool Create(out string message)
    {
        if (State == RoomState.Creating)
        {
            message = "Room creation is already in progress.";
            return false;
        }

        State = RoomState.Creating;
        UpdatedAt = DateTimeOffset.Now;
        message = "Room creation is ready for an authorized network integration.";
        return true;
    }

    public bool MarkActive(string roomCode, out string message)
    {
        roomCode = roomCode.Trim();
        if (roomCode.Length == 0)
        {
            message = "Room code is empty.";
            return false;
        }

        RoomCode = roomCode;
        State = RoomState.Active;
        UpdatedAt = DateTimeOffset.Now;
        message = $"Room {RoomCode} is active locally.";
        return true;
    }

    public bool Leave(out string message)
    {
        if (State == RoomState.None || State == RoomState.Closed)
        {
            message = "No active room.";
            return false;
        }

        State = RoomState.Closed;
        UpdatedAt = DateTimeOffset.Now;
        message = "Room closed locally.";
        return true;
    }
}
