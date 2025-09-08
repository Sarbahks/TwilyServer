using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public sealed class ClientConnection
{
    public int UserId { get; init; }
    public string Name { get; init; } = "";
    public WebSocket Socket { get; init; } = default!;
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
}

public sealed class ConnectionRegistry
{
    // Single active connection per user
    private readonly ConcurrentDictionary<int, ClientConnection> _activeByUser = new();
    // Reverse map to guard cleanup on late closes
    private readonly ConcurrentDictionary<WebSocket, int> _userIdBySocket = new();

    /// <summary>Register/replace the active connection for a user. Closes any previous socket safely.</summary>
    public void Register(ClientConnection conn)
    {
        var userId = conn.UserId;

        // Put reverse map first (so IsCurrentSocket works during races)
        _userIdBySocket[conn.Socket] = userId;

        // Replace old connection if present
        if (_activeByUser.TryGetValue(userId, out var prev) && !ReferenceEquals(prev.Socket, conn.Socket))
        {
            _activeByUser[userId] = conn; // replace with new
            SafeClose(prev.Socket, WebSocketCloseStatus.PolicyViolation, "replaced");
            _userIdBySocket.TryRemove(prev.Socket, out _);
        }
        else
        {
            _activeByUser[userId] = conn;
        }
    }

    /// <summary>Snapshot of all active connections.</summary>
    public IReadOnlyCollection<ClientConnection> All()
        => _activeByUser.Values.ToList();

    /// <summary>Unregister by user id, but only if this user still maps to the same socket (race-safe).</summary>
    public void UnregisterByUserId(int userId)
    {
        if (_activeByUser.TryRemove(userId, out var conn))
        {
            _userIdBySocket.TryRemove(conn.Socket, out _);
        }
    }

    /// <summary>Unregister by socket, but only if that socket is still the current one for the mapped user.</summary>
    public void UnregisterBySocket(WebSocket socket)
    {
        if (_userIdBySocket.TryGetValue(socket, out var userId))
        {
            // Only remove if the active mapping still points to this socket
            if (_activeByUser.TryGetValue(userId, out var current) && ReferenceEquals(current.Socket, socket))
            {
                _activeByUser.TryRemove(userId, out _);
            }
            _userIdBySocket.TryRemove(socket, out _);
        }
    }

    /// <summary>Try to send a JSON envelope to a specific user if connected.</summary>
    public async Task<bool> TrySendToUserAsync<T>(int userId, T payload, JsonSerializerOptions jsonOptions, CancellationToken ct = default)
    {
        if (!_activeByUser.TryGetValue(userId, out var conn))
            return false;

        var ws = conn.Socket;
        if (ws.State != WebSocketState.Open) return false;

        try
        {
            var json = JsonSerializer.Serialize(payload, jsonOptions);
            var buffer = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, ct);
            return true;
        }
        catch
        {
            // If send fails, treat as dead and unregister-by-socket (race-safe)
            UnregisterBySocket(ws);
            return false;
        }
    }

    /// <summary>Broadcast helper: sends to a set of user ids.</summary>
    public async Task BroadcastAsync<T>(IEnumerable<int> userIds, T payload, JsonSerializerOptions jsonOptions, CancellationToken ct = default)
    {
        // Iterate ids (not All()) so you don’t N-scan the whole registry
        foreach (var uid in userIds)
            await TrySendToUserAsync(uid, payload, jsonOptions, ct);
    }

    private static async void SafeClose(WebSocket ws, WebSocketCloseStatus code, string reason)
    {
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(code, reason, CancellationToken.None); } catch { }
        try { ws.Dispose(); } catch { }
    }
}
