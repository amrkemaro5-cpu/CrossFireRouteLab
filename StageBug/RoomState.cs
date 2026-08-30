using System.Text.Json;

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
    private sealed record PersistedRoom(string? RoomCode, DateTimeOffset? UpdatedAt);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string stateFile;

    public RoomState State { get; private set; } = RoomState.None;
    public string? RoomCode { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public RoomStateModel()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StageBug");
        Directory.CreateDirectory(root);
        stateFile = Path.Combine(root, "active-room.json");
        LoadPersistedState();
    }

    public bool Restore(out string message)
    {
        if (string.IsNullOrWhiteSpace(RoomCode))
        {
            message = "No saved active room was found.";
            return false;
        }

        State = RoomState.Restored;
        UpdatedAt = DateTimeOffset.Now;
        SavePersistedState();
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
        SavePersistedState();
        message = $"Room {RoomCode} is active locally.";
        return true;
    }

    public bool Leave(out string message)
    {
        if (State is RoomState.None or RoomState.Closed)
        {
            message = "No active room.";
            return false;
        }

        State = RoomState.Closed;
        UpdatedAt = DateTimeOffset.Now;
        RoomCode = null;
        DeletePersistedState();
        message = "Room closed locally.";
        return true;
    }

    private void LoadPersistedState()
    {
        try
        {
            if (!File.Exists(stateFile))
                return;

            var json = File.ReadAllText(stateFile);
            var persisted = JsonSerializer.Deserialize<PersistedRoom>(json, JsonOptions);
            if (persisted is null || string.IsNullOrWhiteSpace(persisted.RoomCode))
                return;

            RoomCode = persisted.RoomCode;
            UpdatedAt = persisted.UpdatedAt;
            State = RoomState.Restored;
        }
        catch
        {
            RoomCode = null;
            UpdatedAt = null;
            State = RoomState.None;
        }
    }

    private void SavePersistedState()
    {
        if (string.IsNullOrWhiteSpace(RoomCode))
            return;

        try
        {
            var payload = JsonSerializer.Serialize(new PersistedRoom(RoomCode, UpdatedAt), JsonOptions);
            var tempFile = stateFile + ".tmp";
            File.WriteAllText(tempFile, payload);
            File.Move(tempFile, stateFile, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; in-memory state remains authoritative.
        }
    }

    private void DeletePersistedState()
    {
        try
        {
            if (File.Exists(stateFile))
                File.Delete(stateFile);
        }
        catch
        {
            // Cleanup failure must not block normal operation.
        }
    }
}
