using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using static Helpers;

public sealed class ClientConnection
{
    public int UserId { get; init; }
    public string Name { get; init; } = "";
    public WebSocket Socket { get; init; } = default!;
    public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;
}

public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<int, ClientConnection> _byUserId = new();

    public void Register(ClientConnection conn)
    {
        _byUserId.AddOrUpdate(conn.UserId, conn, (_, __) => conn);
    }

    public void UnregisterByUserId(int userId)
    {
        _byUserId.TryRemove(userId, out _);
    }

    public void UnregisterBySocket(WebSocket socket)
    {
        foreach (var kv in _byUserId)
        {
            if (ReferenceEquals(kv.Value.Socket, socket))
            {
                _byUserId.TryRemove(kv.Key, out _);
                break;
            }
        }
    }

    public bool TryGet(int userId, out ClientConnection conn) => _byUserId.TryGetValue(userId, out conn!);

    public IEnumerable<ClientConnection> All() => _byUserId.Values;

    // Convenience: send an envelope to a specific user if online
    public async Task<bool> TrySendToUserAsync<T>(int userId, Envelope<T> envelope, JsonSerializerOptions? opts = null)
    {
        if (!TryGet(userId, out var conn)) return false;
        if (conn.Socket.State != WebSocketState.Open) return false;

        await Helpers.SendJsonAsync(conn.Socket, envelope, opts ?? Helpers.Json);
        return true;
    }

    private static IEnumerable<ClientConnection> FilterByUserIds(ConnectionRegistry registry, IEnumerable<int> ids)
    {
        foreach (var id in ids)
        {
            if (registry.TryGet(id, out var conn))
                yield return conn;
        }
    }

}
