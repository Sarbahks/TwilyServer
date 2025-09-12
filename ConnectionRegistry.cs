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
    // userId -> active connection
    private readonly ConcurrentDictionary<int, ClientConnection> _activeByUser = new();
    // socket -> userId (reverse map)
    private readonly ConcurrentDictionary<WebSocket, int> _userIdBySocket = new();

    // Register or replace the active connection for a user
    public void Register(ClientConnection conn)
    {
        var userId = conn.UserId;

        // Update reverse map first so IsCurrent and TryGetUserId work during races
        _userIdBySocket[conn.Socket] = userId;

        if (_activeByUser.TryGetValue(userId, out var prev) && !ReferenceEquals(prev.Socket, conn.Socket))
        {
            // Replace with new connection
            _activeByUser[userId] = conn;

            // Remove reverse map for old socket and close it
            _userIdBySocket.TryRemove(prev.Socket, out _);
            SafeClose(prev.Socket, WebSocketCloseStatus.PolicyViolation, "replaced");
        }
        else
        {
            // First time or same instance
            _activeByUser[userId] = conn;
        }
    }

    // Snapshot of all active connections
    public IReadOnlyCollection<ClientConnection> All()
        => _activeByUser.Values.ToList();

    // Unregister by userId
    public void UnregisterByUserId(int userId)
    {
        if (_activeByUser.TryRemove(userId, out var conn))
        {
            _userIdBySocket.TryRemove(conn.Socket, out _);
        }
    }

    // Unregister by socket, but only if that socket is still current for the user
    public void UnregisterBySocket(WebSocket socket)
    {
        if (_userIdBySocket.TryGetValue(socket, out var userId))
        {
            if (_activeByUser.TryGetValue(userId, out var current) && ReferenceEquals(current.Socket, socket))
            {
                _activeByUser.TryRemove(userId, out _);
            }
            _userIdBySocket.TryRemove(socket, out _);
        }
    }

    // Resolve sender userId from socket
    public bool TryGetUserId(WebSocket socket, out int userId)
        => _userIdBySocket.TryGetValue(socket, out userId);

    // Check if a socket is still the active one for its user
    public bool IsCurrent(WebSocket socket)
    {
        if (!_userIdBySocket.TryGetValue(socket, out var userId)) return false;
        return _activeByUser.TryGetValue(userId, out var curr) && ReferenceEquals(curr.Socket, socket);
    }

    // Is this user currently connected
    public bool IsUserConnected(int userId) => _activeByUser.ContainsKey(userId);

    // Optional: get a snapshot of active userIds (useful for diagnostics)
    public IReadOnlyCollection<int> ActiveUserIds() => _activeByUser.Keys.ToList();

    // Send to a specific user if connected
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
            // Treat as dead, unregister this socket safely
            UnregisterBySocket(ws);
            return false;
        }
    }

    // Broadcast to a set of userIds
    public async Task BroadcastAsync<T>(IEnumerable<int> userIds, T payload, JsonSerializerOptions jsonOptions, CancellationToken ct = default)
    {
        foreach (var uid in userIds)
            await TrySendToUserAsync(uid, payload, jsonOptions, ct);
    }

    private static async void SafeClose(WebSocket ws, WebSocketCloseStatus code, string reason)
    {
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(code, reason, CancellationToken.None); } catch { }
        try { ws.Dispose(); } catch { }
    }
}
