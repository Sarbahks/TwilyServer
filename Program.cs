using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Helpers;

var builder = WebApplication.CreateBuilder(args);




List<BigSalonInfo> actualBigSalons = new List<BigSalonInfo>();
builder.Services.AddSingleton<ServerState>();


// DI: track connections
builder.Services.AddSingleton<ConnectionRegistry>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// JSON opts aligned with your client payload (PascalCase allowed, case-insensitive)
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false
};

app.Map("/ws", async ctx =>
{
    var connections = ctx.RequestServices.GetRequiredService<ConnectionRegistry>();
    var serverState = ctx.RequestServices.GetRequiredService<ServerState>();
    var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WS");

    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("WebSocket endpoint");
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    logger.LogInformation("Client connected");

    int? registeredUserId = null;

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var text = await ReceiveTextAsync(socket);
            if (text == null)
            {
                logger.LogInformation("Client closed");
                break;
            }

            var type = GetMessageType(text);


            switch (type)
            {
                case "ping":
                    await SendTextAsync(socket, "pong");
                    break;

                case "connectedToServer":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<ServerUserData>>(text, jsonOptions);
                        if (env?.Data?.UserInfo == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var u = env.Data.UserInfo;
                        registeredUserId = u.Id;

                        // Register this connection
                        connections.Register(new ClientConnection
                        {
                            UserId = u.Id,
                            Name = u.Name ?? "",
                            Socket = socket
                        });

                        // Ack back to this client
                        await SendJsonAsync(socket, new Envelope<ServerUserData>("connectedAck", env.Data), jsonOptions);

                        logger.LogInformation("Registered user {Id} {Name}", u.Id, u.Name);
                        break;
                    }
                case "createBigSalon":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<BigSalonInfo>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        if (serverState.TryAddBigSalon(env.Data, out var err))
                        {
                            await BroadcastBigSalonsAsync(connections, serverState, jsonOptions);
                        }
                        else
                        {
                            await SendJsonAsync(socket, new Envelope<object>("bigSalonCreated", new { ok = false, error = err }), jsonOptions);
                        }
                        break;
                    }

                case "getBigSalons":
                    {
                        await BroadcastBigSalonsAsync(connections, serverState, jsonOptions);
                        break;
                    }
                case "joinSalonRequest":
                    {
                        var join = JsonSerializer.Deserialize<Envelope<JoinSalonRequest>>(text, jsonOptions);
                        if (join?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        bool ok = serverState.TryToJoinBigSalon(join.Data);

                        var resp = new JoinSalonResponse
                        {
                            Joined = ok,
                            SalonId = join.Data.SalonId
                        };

                        await SendJsonAsync(socket, new Envelope<JoinSalonResponse>("salonJoined", resp), jsonOptions);

                        break;
                    }
                case "leaveSalonRequest":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<LeaveSalonRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        bool ok = serverState.TryToLeaveBigSalon(env.Data);

                        var resp = new LeaveSalonResponse
                        {
                            Leaved = ok,
                            SalonId = env.Data.SalonId
                        };

                        await SendJsonAsync(socket, new Envelope<LeaveSalonResponse>("salonLeaved", resp), jsonOptions);

                        // optional: if you want all members of this salon to refresh lobby state
                        if (ok)
                            await BroadcastLobbyIdAsync(connections, serverState, jsonOptions, env.Data.SalonId);

                        break;
                    }


                case "getSalonInfo":
                    {
                        var sal = JsonSerializer.Deserialize<Envelope<string>>(text, jsonOptions);


                        await BroadcastBigSalonsAsync(connections, serverState, jsonOptions);
                        break;
                    }
                case "createTeam":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<CreateTeamRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        bool ok = serverState.TryToCreateTeam(env.Data);

                        if (ok)
                        {
                            // Notify all users in this BigSalon
                            await BroadcastLobbyIdAsync(connections, serverState, jsonOptions, env.Data.IdSalon);
                        }
                        else
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "team-create-failed"), jsonOptions);
                        }

                        break;
                    }

                case "getLobbyInfo":
                    {

                        var env = JsonSerializer.Deserialize<Envelope<string>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        await BroadcastLobbyIdAsync(connections, serverState, jsonOptions, env.Data);
                        break;
                    }
                case "joinTeam":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<JoinTeamRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        bool ok = serverState.TryJoinTeam(env.Data);

                        // Ack to requester
                        await SendJsonAsync(socket, new Envelope<object>("teamJoined", new
                        {
                            joined = ok,
                            salonId = env.Data.SalonId,
                            teamId = env.Data.TeamId,
                            userId = env.Data.UserInfo?.Id
                        }), jsonOptions);

                    
                        break;
                    }

                case "leaveTeam":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<LeaveTeamRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        bool ok = serverState.TryLeaveTeam(env.Data);

                        // Ack to requester
                        await SendJsonAsync(socket, new Envelope<object>("teamLeaved", new
                        {
                            leaved = ok,
                            salonId = env.Data.SalonId,
                            teamId = env.Data.TeamId,
                            userId = env.Data.UserInfo?.Id
                        }), jsonOptions);

                   
                        break;
                    }

                case "askStartGame":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<StartGameRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            logger.LogWarning("askStartGame received with missing data.");
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        const int REQUIRED_READY = 4;

                        if (serverState.TryReadyPlayer(env.Data, out var readyCount, out var readyIds))
                        {
                            logger.LogInformation(
                                "askStartGame -> User {UserId} ready in Salon={SalonId}, Team={TeamId}. ReadyCount={Count}, ReadyIds=[{Ids}]",
                                env.Data.UserInfo?.Id,
                                env.Data.SalonId,
                                env.Data.TeamId,
                                readyCount,
                                string.Join(",", readyIds)
                            );

                            // ack back current ready
                            await SendJsonAsync(socket, new Envelope<object>("readyStatus", new
                            {
                                salonId = env.Data.SalonId,
                                teamId = env.Data.TeamId,
                                readyCount
                            }), jsonOptions);

                            if (readyCount >= REQUIRED_READY)
                            {
                                var leaderId = ServerState.ChooseLeaderUserId(readyIds);
                                serverState.SetInitLeader(env.Data.SalonId, env.Data.TeamId, leaderId);

                                var initial = serverState.BuildInitialGameState(env.Data.SalonId, env.Data.TeamId);

                                var initPayload = new InitializeGamePayload
                                {
                                    SalonId = env.Data.SalonId,
                                    TeamId = env.Data.TeamId,
                                    Game = initial
                                };

                                logger.LogInformation(
                                    "askStartGame -> Threshold reached ({Count}). Leader chosen = {LeaderId}. Sending initializeGame.",
                                    readyCount,
                                    leaderId
                                );

                                await connections.TrySendToUserAsync(
                                    leaderId,
                                    new Envelope<InitializeGamePayload>("initializeGame", initPayload),
                                    jsonOptions
                                );
                            }
                        }
                        else
                        {
                            logger.LogWarning(
                                "askStartGame -> User {UserId} not eligible to ready. Salon={SalonId}, Team={TeamId}",
                                env.Data.UserInfo?.Id,
                                env.Data.SalonId,
                                env.Data.TeamId
                            );

                            await SendJsonAsync(socket, new Envelope<string>("error", "not-eligible"), jsonOptions);
                        }
                        break;
                    }

                case "gameBoardInitialized":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<GameBoardInitializedPayload>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            logger.LogWarning("gameBoardInitialized received with missing data.");
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        if (!serverState.TryGetInitLeader(env.Data.SalonId, env.Data.TeamId, out var leaderId))
                        {
                            logger.LogWarning("gameBoardInitialized -> no leader registered for Salon={SalonId}, Team={TeamId}.",
                                env.Data.SalonId,
                                env.Data.TeamId
                            );

                            await SendJsonAsync(socket, new Envelope<string>("error", "no-leader"), jsonOptions);
                            break;
                        }

                        logger.LogInformation(
                            "gameBoardInitialized -> Received from Leader {LeaderId}. Salon={SalonId}, Team={TeamId}",
                            leaderId,
                            env.Data.SalonId,
                            env.Data.TeamId
                        );

                        var ok = serverState.ApplyInitializedGame(env.Data.SalonId, env.Data.TeamId, env.Data.Game);
                        if (!ok)
                        {
                            logger.LogWarning("gameBoardInitialized -> Apply failed for Salon={SalonId}, Team={TeamId}",
                                env.Data.SalonId,
                                env.Data.TeamId
                            );

                            await SendJsonAsync(socket, new Envelope<string>("error", "apply-failed"), jsonOptions);
                            break;
                        }

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.SalonId, env.Data.TeamId);

                        logger.LogInformation(
                            "gameBoardInitialized -> Broadcasting gameInitialized to {Count} players: [{Ids}]",
                            teamUserIds.Count,
                            string.Join(",", teamUserIds)
                        );

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<GameBoardInitializedPayload>("gameInitialized", env.Data),
                            jsonOptions
                        );

                        break;
                    }

                case "leaveGame":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<LeaveGameRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        if (serverState.TryUnreadyPlayer(env.Data, out var readyCount, out var readyIds))
                        {
                            // ack to requester
                            await SendJsonAsync(socket, new Envelope<object>("readyStatus", new
                            {
                                salonId = env.Data.SalonId,
                                teamId = env.Data.TeamId,
                                readyCount
                            }), jsonOptions);

                            // optional: refresh lobby for all members of the big salon
                            // await BroadcastLobbyIdAsync(connections, serverState, jsonOptions, env.Data.SalonId);
                        }
                        else
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "not-eligible"), jsonOptions);
                        }
                        break;
                    }

                case "choseRole":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<ChoseRoleGameRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryAssignRoleAndMaybeAdvanceStep(
                            env.Data.SalonId,
                            env.Data.TeamId,
                            env.Data.UserInfo,
                            env.Data.RoleWanted,
                            out var game,
                            out var error
                        );

                        if (!ok)
                        {
                            logger.LogWarning("choseRole failed: {Err} (Salon={SalonId}, Team={TeamId})",
                                error, env.Data.SalonId, env.Data.TeamId);
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.SalonId, env.Data.TeamId);

                        logger.LogInformation(
                            "choseRole -> Broadcasting gameStateUpdated to {Count} players: [{Ids}] (Salon={SalonId}, Team={TeamId})",
                            teamUserIds.Count, string.Join(",", teamUserIds), env.Data.SalonId, env.Data.TeamId
                        );

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<GameStateData>("profileChosen", game),
                            jsonOptions
                        );
                        break;
                    }

                default:
                    await SendJsonAsync(socket, new Envelope<string>("error", "unknown-type"), jsonOptions);
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "WS loop error");
    }
    finally
    {
        // Remove from registry
        if (registeredUserId != null)
            connections.UnregisterByUserId(registeredUserId.Value);
        else
            connections.UnregisterBySocket(socket);

        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
        logger.LogInformation("Client disconnected");
    }
});

app.Run();

static async Task BroadcastBigSalonsAsync(
    ConnectionRegistry connections,
    ServerState serverState,
    JsonSerializerOptions jsonOptions)
{
    var list = serverState.GetBigSalons();
    var payload = new Helpers.Envelope<object>("bigSalonsList", new { salons = list });

    foreach (var c in connections.All())
    {
        if (c.Socket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            await Helpers.SendJsonAsync(c.Socket, payload, jsonOptions);
        }
    }
}


static async Task BroadcastLobbyIdAsync(
    ConnectionRegistry connections,
    ServerState serverState,
    JsonSerializerOptions jsonOptions,
    string salonId)
{
    if (!serverState.TryGetBigSalon(salonId, out var salon))
        return;

    // Snapshot member ids (avoid race conditions while iterating)
    HashSet<int> memberIds;
    BigSalonInfo salonSnapshot;
    lock (salon)
    {
        memberIds = salon.UserInBig?.Select(u => u.Id).ToHashSet() ?? new HashSet<int>();
        // shallow snapshot is enough if clients only read
        salonSnapshot = salon;
    }

    var payload = new Envelope<object>("actualizeLobby", new { salon = salonSnapshot });

    foreach (var c in connections.All())
    {
        if (c.Socket.State != WebSocketState.Open) continue;
        if (!memberIds.Contains(c.UserId)) continue;

        await SendJsonAsync(c.Socket, payload, jsonOptions);
    }
}

static async Task SendToUserIdsAsync<T>(
ConnectionRegistry connections,
IEnumerable<int> userIds,
Helpers.Envelope<T> envelope,
JsonSerializerOptions jsonOptions)
{
    foreach (var uid in userIds)
        await connections.TrySendToUserAsync(uid, envelope, jsonOptions);
}



