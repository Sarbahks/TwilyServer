using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using static Helpers;

var builder = WebApplication.CreateBuilder(args);




List<BigSalonInfo> actualBigSalons = new List<BigSalonInfo>();
builder.Services.AddSingleton<ServerState>();


// DI: track connections
builder.Services.AddSingleton<ConnectionRegistry>();

builder.Services.AddSingleton<WsMessageHandler>();
var app = builder.Build();

var handler = app.Services.GetRequiredService<WsMessageHandler>();
var connectionRegistry = app.Services.GetRequiredService<ConnectionRegistry>();
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

                        connections.Register(new ClientConnection
                        {
                            UserId = u.Id,
                            Name = u.Name ?? "",
                            Socket = socket
                        });

                        // Optionally ACK
                        await SendJsonAsync(socket, new Envelope<string>("connectedAck", "ok"), jsonOptions);
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
                case "deleteBigSalon":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<string>>(text, jsonOptions);
                        var bigSalonId = env?.Data;

                        if (string.IsNullOrWhiteSpace(bigSalonId))
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-id"), jsonOptions);
                            break;
                        }

                        if (serverState.TryRemoveBigSalon(bigSalonId, out var affectedUserIds, out var err))
                        {
                            // 1) Notify only members of that big salon that it was closed
                            var memberIdSet = new HashSet<int>(affectedUserIds);
                            await SendToUserIdsAsync(
                                connections, // ConnectionRegistry
                                memberIdSet,
                                new Envelope<object>("bigSalonClosed", new { id = bigSalonId }),
                                jsonOptions
                            );

                            // 2) Broadcast the refreshed big salon list to everyone
                            await BroadcastBigSalonsAsync(connections, serverState, jsonOptions);

                        }
                        else
                        {
                            await SendJsonAsync(
                                socket,
                                new Envelope<object>("bigSalonDeleted", new { ok = false, error = err }),
                                jsonOptions
                            );
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
                case "deleteTeam":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<DeleteTeamRequest>>(text, jsonOptions);
                        var req = env?.Data;

                        if (req == null || string.IsNullOrWhiteSpace(req.IdSalon) || string.IsNullOrWhiteSpace(req.IdTeam))
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        if (serverState.TryDeleteTeam(req.IdSalon, req.IdTeam, out var affectedUserIds, out var err))
                        {
                            // 1) Notify only members of that team so clients can exit that room
                            var memberIdSet = new HashSet<int>(affectedUserIds);
                            await SendToUserIdsAsync(
                                connections,
                                memberIdSet,
                                new Envelope<object>("teamClosed", new { idSalon = req.IdSalon, idTeam = req.IdTeam }),
                                jsonOptions
                            );

                            // 2) Broadcast updated lobby (teams list of this BigSalon) to everyone in that BigSalon
                            await BroadcastLobbyIdAsync(connections, serverState, jsonOptions, req.IdSalon);
                        }
                        else
                        {
                            await SendJsonAsync(socket,
                                new Envelope<object>("teamDeleted", new { ok = false, error = err }),
                                jsonOptions);
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

                        var gameInit = serverState.ApplyInitializedGame(env.Data.SalonId, env.Data.TeamId, env.Data.Game);

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.SalonId, env.Data.TeamId);

                        logger.LogInformation(
                            "gameBoardInitialized -> Broadcasting gameInitialized to {Count} players: [{Ids}]",
                            teamUserIds.Count,
                            string.Join(",", teamUserIds)
                        );

                        var datatosend = new GameBoardInitializedPayload
                        {
                            Game = gameInit,
                            SalonId = env.Data.SalonId,
                            TeamId = env.Data.SalonId
                        };

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<GameBoardInitializedPayload>("gameInitialized", datatosend),
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
                case "choseCard":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<ChoseCardRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryUnlockCard(env.Data.IdSalon, env.Data.IdTeam, env.Data.IdCard, out var error);
                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var resp = new ChoseCardResponse
                        {
                            IdSalon = env.Data.IdSalon,
                            IdTeam = env.Data.IdTeam,
                            IdCard = env.Data.IdCard,
                            Game = serverState.GetGameState(env.Data.IdSalon, env.Data.IdTeam)
                        };

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.IdSalon, env.Data.IdTeam);

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<ChoseCardResponse>("cardChosen", resp),
                            jsonOptions
                        );

                        break;
                    }
                case "answerCard":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<AnswerCardRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryApplyAnswerAndAdvanceTurn(
                            env.Data.IdSalon,
                            env.Data.IdTeam,
                            env.Data.CardAnsewerd,
                            env.Data.IdPlayerAnswered,
                            out var game,
                            out var error
                        );

                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var resp = new AnswerCardResponse
                        {
                            IdSalon = env.Data.IdSalon,
                            IdTeam = env.Data.IdTeam,
                            Game = game
                        };

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.IdSalon, env.Data.IdTeam);

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<AnswerCardResponse>("cardAnswered", resp),
                            jsonOptions
                        );

                        break;
                    }
                case "choseProfile":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<ChoseProfileRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryApplyProfileToPlayer(
                            env.Data.IdSalon,
                            env.Data.IdTeam,
                            env.Data.UserInfo,
                            env.Data.CardsChosen,
                            out var game,
                            out var error
                        );

                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var resp = new ChoseProfileResponse
                        {
                            IdSalon = env.Data.IdSalon,
                            IdTeam = env.Data.IdTeam,
                            Game = game
                        };

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.IdSalon, env.Data.IdTeam);

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<ChoseProfileResponse>("playerProfileCardsChosen", resp),
                            jsonOptions
                        );

                        break;
                    }
                case "sendNotif":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<SendNotificationRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryAddNotificationToBigSalon(env.Data.IdSalon, env.Data.Notification, out var big, out var error);
                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var recipients = serverState.GetBigSalonAdminObserverUserIds(env.Data.IdSalon);

                        await SendToUserIdsAsync(
                            connections,
                            recipients,
                            new Envelope<BigSalonInfo>("notificationSent", big),
                            jsonOptions
                        );
                        break;
                    }

                case "deleteNotif":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<DeleteNotificationRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryRemoveNotificationFromBigSalon(env.Data.IdSalon, env.Data.IdNotification, out var big, out var error);
                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var recipients = serverState.GetBigSalonAdminObserverUserIds(env.Data.IdSalon);

                        await SendToUserIdsAsync(
                            connections,
                            recipients,
                            new Envelope<BigSalonInfo>("notificationDeleted", big),
                            jsonOptions
                        );
                        break;
                    }
                case "validateCardAdmin":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<ValidateCardAdminRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryValidateCardAdmin(
                            env.Data.IdSalon,
                            env.Data.IdTeam,
                            env.Data.CardId,
                            env.Data.NewState,
                            out var game,
                            out var error
                        );

                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var resp = new ValidateCardAdminResponse
                        {
                            IdSalon = env.Data.IdSalon,
                            IdTeam = env.Data.IdTeam,
                            Game = game
                        };

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.IdSalon, env.Data.IdTeam);

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<ValidateCardAdminResponse>("cardValidatedAdmin", resp),
                            jsonOptions
                        );
                        break;
                    }
                case "submitBudget":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<SubmitBudgetRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryApplyBudgetToGame(
                            env.Data.IdSalon,
                            env.Data.IdTeam,
                            env.Data.Budget,
                            out var game,
                            out var error
                        );

                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.IdSalon, env.Data.IdTeam);

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<BudgetSubmittedResponse>("budgetSubmitted", new BudgetSubmittedResponse
                            {
                                IdSalon = env.Data.IdSalon,
                                IdTeam = env.Data.IdTeam,
                                Game = game
                            }),
                            jsonOptions
                        );
                        break;
                    }

                case "submitCrisis":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<SubmitCrisisRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryApplyCrisisToGame(
                            env.Data.IdSalon,
                            env.Data.IdTeam,
                            env.Data.Crisis,
                            out var game,
                            out var error
                        );

                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.IdSalon, env.Data.IdTeam);

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<CrisisSubmittedResponse>("crisisSubmitted", new CrisisSubmittedResponse
                            {
                                IdSalon = env.Data.IdSalon,
                                IdTeam = env.Data.IdTeam,
                                Game = game
                            }),
                            jsonOptions
                        );
                        break;
                    }
                case "chatMessage":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<ChatDTO>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        // Normalize timestamp
                        if (env.Data.Ts == 0)
                            env.Data.Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                        // Resolve recipients
                        if (!serverState.TryGetChatRecipients(env.Data, out var recipientIds, out var error))
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "no-recipients"), jsonOptions);
                            break;
                        }

                        // Broadcast using the registry
                        await connectionRegistry.SendToUsersAsync(recipientIds, new Envelope<ChatDTO>("chatMessage", env.Data), jsonOptions);
                        break;
                    }
                case "shareMessage":
                    {
                        var env = JsonSerializer.Deserialize<Envelope<SharedMessageRequest>>(text, jsonOptions);
                        if (env?.Data == null)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", "missing-data"), jsonOptions);
                            break;
                        }

                        var ok = serverState.TryApplySharedMessage(
                            env.Data.IdSalon,
                            env.Data.IdTeam,
                            env.Data.TextShared,
                            out var error
                        );

                        if (!ok)
                        {
                            await SendJsonAsync(socket, new Envelope<string>("error", error ?? "unknown"), jsonOptions);
                            break;
                        }

                        var teamUserIds = serverState.GetTeamUserIds(env.Data.IdSalon, env.Data.IdTeam);

                        await SendToUserIdsAsync(
                            connections,
                            teamUserIds,
                            new Envelope<SharedMessageResponse>("sharedText", new SharedMessageResponse
                            {
                                IdSalon = env.Data.IdSalon,
                                IdTeam = env.Data.IdTeam,
                                TextShared = text
                            }),
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


public static class ConnectionRegistryExtensions
{
    public static async Task SendToUsersAsync<T>(
        this ConnectionRegistry registry,
        IEnumerable<int> userIds,
        Envelope<T> envelope,
        JsonSerializerOptions? opts = null)
    {
        var tasks = new List<Task>(8);
        foreach (var uid in userIds)
            tasks.Add(registry.TrySendToUserAsync(uid, envelope, opts));
        await Task.WhenAll(tasks);
    }
}
public sealed class WsMessageHandler
{
    private readonly ServerState _serverState;
    private readonly ConnectionRegistry _conn;

    public WsMessageHandler(ServerState serverState, ConnectionRegistry conn)
    {
        _serverState = serverState;
        _conn = conn;
    }

    public async Task HandleAsync(WebSocket socket, string text, JsonSerializerOptions jsonOptions)
    {
        var env = JsonSerializer.Deserialize<Envelope<object>>(text, jsonOptions);
        switch (env?.Type)
        {
            case "chatMessage":
                var chat = JsonSerializer.Deserialize<Envelope<ChatDTO>>(text, jsonOptions);
                if (chat?.Data == null) break;

                if (!_serverState.TryGetChatRecipients(chat.Data, out var ids, out var err)) break;

                await _conn.SendToUsersAsync(ids, new Envelope<ChatDTO>("chatMessage", chat.Data), jsonOptions);
                break;


        }
    }

    // In your ASP.NET Core handler or custom server loop:

    

    private static async Task CloseReplaced(ClientConnection prev)
    {
        try { await prev.Socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "replaced", CancellationToken.None); }
        catch { }
        try { prev.Socket.Dispose(); } catch { }
    }

}
