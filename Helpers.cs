// Helpers.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.Serialization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Runtime.InteropServices.JavaScript.JSType;

public static class Helpers
{
    // Reuse everywhere so server and clients are consistent
    public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        IncludeFields = true, // IMPORTANT: you use public fields in DTOs
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public record Envelope<T>(string Type, T Data);

    public static async Task<string?> ReceiveTextAsync(WebSocket socket)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), default);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static Task SendTextAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, default);
    }

    public static Task SendJsonAsync<T>(WebSocket socket, Envelope<T> envelope, JsonSerializerOptions? opts = null)
    {
        var json = JsonSerializer.Serialize(envelope, opts ?? Json);
        return SendTextAsync(socket, json);
    }

    // Small helper to quickly read "type" without binding entire DTO
    public static string GetMessageType(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("type", out var t))
                return t.GetString() ?? "";
        }
        catch { /* ignore */ }
        return "";
    }

    #region
    private static readonly string TwilyDataPath =
      Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Resources", "datas.json"));

    // Single-process concurrency guard
    private static readonly SemaphoreSlim TwilyDataGate = new(1, 1);

    // System.Text.Json options
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Ensure the folder and file exist with a blank TwilyData
    public static async Task EnsureTwilyDataExistsAsync()
    {
        await TwilyDataGate.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(TwilyDataPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(TwilyDataPath))
            {
                var blank = new TwilyData();
                var json = JsonSerializer.Serialize(blank, JsonOpts);
                await File.WriteAllTextAsync(TwilyDataPath, json, new UTF8Encoding(false));
            }
        }
        finally
        {
            TwilyDataGate.Release();
        }
    }

    // Load the JSON file into memory
    public static async Task<TwilyData> LoadTwilyDataAsync()
    {
        await EnsureTwilyDataExistsAsync();

        await TwilyDataGate.WaitAsync();
        try
        {
            var json = await File.ReadAllTextAsync(TwilyDataPath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<TwilyData>(json, JsonOpts) ?? new TwilyData();
            return data;
        }
        finally
        {
            TwilyDataGate.Release();
        }
    }

    public static async Task<TwilyData> InitializeNewGameData(GameStateData gameInit, string teamId)
    {
        var data = await Helpers.LoadTwilyDataAsync() ?? new TwilyData();
        data.GameStarted++;

        var gameData = new TwilyGameData
        {
            TeamID = teamId,
            CardsPlayed = 0,
            TotalScore = 0,
            TotalCards = gameInit.Board.Count,
            Completed = false
        };

        // Guard: no players
        if (gameInit?.Players != null)
        {
            foreach (var user in gameInit.Players)
            {
                if (user?.userInfo == null) continue;

                int uid = user.userInfo.Id;
                string name = user.userInfo.Name ?? "";
                string role = user.roleGame.ToString() ?? "";

                // Add to the current game's PlayerDatas if not already present
                if (!gameData.PlayerDatas.Any(p => p.PlayerId == uid))
                {
                    gameData.PlayerDatas.Add(new TwilyPlayerData
                    {
                        PlayerId = uid,
                        Name = name,
                        RoleInGame = role,
                        Score = 0,
                        CardsPlayed = 0
                    });
                }

                // Ensure a personal profile exists in the global data
                var existingPerson = data.PersonnalDatas.FirstOrDefault(p => p.IdPlayer == uid);
                if (existingPerson == null)
                {
                    data.PersonnalDatas.Add(new TwilyPersonnalData
                    {
                        IdPlayer = uid,
                        Name = name,
                        CardPlays = 0,
                        PointTotalEarned = 0,
                        GamePlayed = 1 // first time we see this player
                    });
                }
                else
                {
                    // Optional: count that this player started another game
                    existingPerson.GamePlayed += 1;
                    // Optional: keep name up to date
                    if (!string.IsNullOrWhiteSpace(name) && !name.Equals(existingPerson.Name))
                        existingPerson.Name = name;
                }
            }
        }

        // Track this game in the global list
        data.GameDatas.Add(gameData);

        // Persist
        await Helpers.SaveTwilyDataAsync(data);

        return data;
    }

    public static async Task<TwilyData> PlayerRespondCard(GameStateData gameInit, string teamId, CardData card)
    {
        if (gameInit == null) throw new ArgumentNullException(nameof(gameInit));
        if (string.IsNullOrWhiteSpace(teamId)) throw new ArgumentException("teamId is required", nameof(teamId));

        int playerId = gameInit.CurrentPlayerId;
        int points = card?.Points ?? 0;
        bool nowCompleted = (gameInit.Board != null) && gameInit.Board.All(x => x.Unlocked);

        // Mutate atomically
        await Helpers.UpdateTwilyDataAsync(data =>
        {
            data.CardPlayed += 1;

            // Find or create the game row for this team
            var game = data.GameDatas.FirstOrDefault(g => string.Equals(g.TeamID, teamId, StringComparison.Ordinal));
            if (game == null)
            {
                game = new TwilyGameData { TeamID = teamId };
                data.GameDatas.Add(game);
            }

            // Update game totals
            game.TotalScore += points;
            game.CardsPlayed += 1;

             game.TotalCards = Math.Max(game.TotalCards, game.CardsPlayed);

            // Flip to completed once; only then bump global GameCompleted
            if (nowCompleted && !game.Completed)
            {
                game.Completed = true;
                data.GameCompleted += 1;
            }
            else
            {
                // Keep the field accurate even if not flipping
                game.Completed = nowCompleted;
            }

            // Find the player info from the live game state (if present)
            var u = gameInit.Players?.FirstOrDefault(x => x.userInfo?.Id == playerId);

            // Upsert player inside this game
            var gp = game.PlayerDatas.FirstOrDefault(p => p.PlayerId == playerId);
            if (gp == null)
            {
                gp = new TwilyPlayerData
                {
                    PlayerId = playerId,
                    Name = u?.userInfo?.Name ?? ""
                };
                game.PlayerDatas.Add(gp);
            }
            gp.CardsPlayed += 1;
            if (u != null)
            {
                // If you want cumulative, add to score instead of overwrite
                gp.Score = u.score;
                gp.RoleInGame = u.roleGame.ToString() ?? gp.RoleInGame ?? "";
            }

            // Upsert personal aggregate
            var pd = data.PersonnalDatas.FirstOrDefault(p => p.IdPlayer == playerId);
            if (pd == null)
            {
                pd = new TwilyPersonnalData
                {
                    IdPlayer = playerId,
                    Name = u?.userInfo?.Name ?? ""
                };
                data.PersonnalDatas.Add(pd);
            }
            pd.CardPlays += 1;
            pd.PointTotalEarned += points;

          
            if (nowCompleted && !game.Completed) 
            {
                // leave empty intentionally; logic handled in the flip branch above
            }

            return true; // indicates we changed something
        });

        // Return a fresh snapshot after mutation
        return await Helpers.LoadTwilyDataAsync();
    }

    // Save the full object back to file (atomic)
    public static async Task SaveTwilyDataAsync(TwilyData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        await TwilyDataGate.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(TwilyDataPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmp = TwilyDataPath + ".tmp";
            var json = JsonSerializer.Serialize(data, JsonOpts);

            await File.WriteAllTextAsync(tmp, json, new UTF8Encoding(false));

            if (File.Exists(TwilyDataPath))
                File.Delete(TwilyDataPath);
            File.Move(tmp, TwilyDataPath);
        }
        finally
        {
            TwilyDataGate.Release();
        }
    }

    // Load -> mutate -> save (only saves if mutator returns true)
    public static async Task<bool> UpdateTwilyDataAsync(Func<TwilyData, bool> mutator)
    {
        if (mutator == null) throw new ArgumentNullException(nameof(mutator));

        await TwilyDataGate.WaitAsync();
        try
        {
            await EnsureTwilyDataExistsAsync();

            var json = await File.ReadAllTextAsync(TwilyDataPath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<TwilyData>(json, JsonOpts) ?? new TwilyData();

            var changed = mutator(data);
            if (!changed) return false;

            var tmp = TwilyDataPath + ".tmp";
            var outJson = JsonSerializer.Serialize(data, JsonOpts);
            await File.WriteAllTextAsync(tmp, outJson, new UTF8Encoding(false));

            if (File.Exists(TwilyDataPath))
                File.Delete(TwilyDataPath);
            File.Move(tmp, TwilyDataPath);
            return true;
        }
        finally
        {
            TwilyDataGate.Release();
        }
    }

    // -------- Convenience helpers below --------

    // Add or update a personal record by IdPlayer
    public static Task<bool> AddOrUpdatePersonAsync(TwilyPersonnalData p)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));

        return UpdateTwilyDataAsync(data =>
        {
            var idx = data.PersonnalDatas.FindIndex(x => x.IdPlayer == p.IdPlayer);
            if (idx >= 0)
                data.PersonnalDatas[idx] = p;
            else
                data.PersonnalDatas.Add(p);
            return true;
        });
    }

    // Upsert a game record by TeamID
    public static Task<bool> UpsertGameByTeamIdAsync(TwilyGameData g)
    {
        if (g == null) throw new ArgumentNullException(nameof(g));

        return UpdateTwilyDataAsync(data =>
        {
            var idx = data.GameDatas.FindIndex(x => string.Equals(x.TeamID, g.TeamID, StringComparison.Ordinal));
            if (idx >= 0)
                data.GameDatas[idx] = g;
            else
                data.GameDatas.Add(g);
            return true;
        });
    }

    // Increment global counters (any args null are skipped)
    public static Task<bool> IncrementCountersAsync(int? gamesStarted = null, int? gamesCompleted = null, int? cardsPlayed = null)
    {
        return UpdateTwilyDataAsync(data =>
        {
            var changed = false;
            if (gamesStarted.HasValue) { data.GameStarted += gamesStarted.Value; changed = true; }
            if (gamesCompleted.HasValue) { data.GameCompleted += gamesCompleted.Value; changed = true; }
            if (cardsPlayed.HasValue) { data.CardPlayed += cardsPlayed.Value; changed = true; }
            return changed;
        });
    }

    // Record a card played by a player in a team
    public static Task<bool> RecordCardPlayedAsync(string teamId, int playerId, int addScore = 0)
    {
        return UpdateTwilyDataAsync(data =>
        {
            var game = data.GameDatas.Find(g => string.Equals(g.TeamID, teamId, StringComparison.Ordinal));
            if (game == null)
            {
                game = new TwilyGameData { TeamID = teamId };
                data.GameDatas.Add(game);
            }

            game.CardsPlayed += 1;
            game.TotalCards += 1;
            game.TotalScore += addScore;

            var player = game.PlayerDatas.Find(p => p.PlayerId == playerId);
            if (player == null)
            {
                player = new TwilyPlayerData { PlayerId = playerId };
                game.PlayerDatas.Add(player);
            }
            player.CardsPlayed += 1;
            player.Score += addScore;

            data.CardPlayed += 1;
            return true;
        });
    }

    // Mark a team game as completed
    public static Task<bool> CompleteGameAsync(string teamId)
    {
        return UpdateTwilyDataAsync(data =>
        {
            var game = data.GameDatas.Find(g => string.Equals(g.TeamID, teamId, StringComparison.Ordinal));
            if (game == null) return false;

            if (!game.Completed)
            {
                game.Completed = true;
                data.GameCompleted += 1;
                return true;
            }
            return false;
        });
    }
    #endregion

}
[Serializable]
public class ValidateCardIARequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public int CardId { get; set; }
    public EvaluationResult NewState { get; set; }
}

[Serializable]
public class ValidateCardIAResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public GameStateData Game { get; set; }
}

[Serializable]
public class BigSalonInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public List<string> WhiteList { get; set; }
    public List<SalonInfo> Salons { get; set; }
    public List<UserInfo> UserInBig { get; set; }   // who’s currently connected to THIS big salon
    public List<NotificationTwily> Notifications { get; set; } = new List<NotificationTwily>();
}

[Serializable]
public class SalonInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public List<string> WhiteList { get; set; }
    public List<UserInfo> UsersInSalon { get; set; } // who’s currently connected to THIS sub-salon
    public GameStateData GameState { get; set; }     // null if not started
}

[Serializable]
public class JoinSalonRequest
{
    public UserInfo UserInfo { get; set; }

    public string SalonId { get; set; }

}


[Serializable]
public class LeaveSalonRequest
{
    public UserInfo UserInfo { get; set; }

    public string SalonId { get; set; }

}

[Serializable]
public class LeaveSalonResponse
{
    public bool Leaved { get; set; }
    public string SalonId { get; set; }
}

[Serializable]
public class JoinSalonResponse
{
    public bool Joined { get; set; }
    public string SalonId { get; set; }

}
[Serializable]
public class CreateTeamRequest
{
    public string IdSalon { get; set; }

    public SalonInfo SalonInfo { get; set; }

}



[Serializable]
public class ServerUserData
{
    public UserInfo UserInfo { get; set; }
    public string Token { get; set; }
}


[Serializable]
public class UserInfo
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string[] Roles { get; set; }
}


[Serializable]
public class JoinTeamRequest
{
    public string SalonId { get; set; }

    public string TeamId { get; set; }
    public bool IsPlayer { get; set; }

    public UserInfo UserInfo { get; set; }

}

[Serializable]
public class LeaveTeamRequest
{
    public string SalonId { get; set; }


    public string TeamId { get; set; }
    public bool IsPlayer { get; set; }

    public UserInfo UserInfo { get; set; }
}


[Serializable]
public class StartGameRequest
{

    public string SalonId { get; set; }


    public string TeamId { get; set; }

    public UserInfo UserInfo { get; set; }
}



[Serializable]
public class LeaveGameRequest
{

    public string SalonId { get; set; }


    public string TeamId { get; set; }

    public UserInfo UserInfo { get; set; }
}


[Serializable]
public class ChoseRoleGameRequest
{

    public string SalonId { get; set; }


    public string TeamId { get; set; }

    public UserInfo UserInfo { get; set; }

    public RoleGameType RoleWanted { get; set; }
}




[Serializable]
public class AuthResponse
{
    public string token;
    public string user_email;
    public string user_nicename;
}

[Serializable]
public class GameRulesData
{
    public int NumberAreas { get; set; }
    public List<AreaData> AreaDatas { get; set; }
}

[Serializable]
public class AreaData
{
    [JsonPropertyName("areaId")]
    public int AreaId { get; set; }

    [JsonPropertyName("maxCaseQuestion")]
    public int MaxCaseQuestion { get; set; }

    [JsonPropertyName("maxCaseBonus")]
    public int MaxCaseBonus { get; set; }

    [JsonPropertyName("maxCaseProfile")]
    public int MaxCaseProfile { get; set; }

    [JsonPropertyName("maxCaseDefi")]
    public int MaxCaseDefi { get; set; }

    [JsonPropertyName("maxCaseKpi")]
    public int MaxCaseKpi { get; set; }

    [JsonPropertyName("maxCaseProfileManagement")]
    public int MaxCaseProfileManagement { get; set; }
}


[Serializable]
public class CardData
{
    public bool ChooseCarrd { get; set; } = false;
    public int IdCardChossen { get; set; } = 0;
    public int Id { get; set; }
    public int IdArea { get; set; }
    public TypeCard TypeCard { get; set; }

    public string Title { get; set; }
    public string Instruction { get; set; }

 
    public string Question { get; set; }         // for QuestionCard
    public string Response { get; set; }         // for QuestionCard
    public bool Unlocked { get; set; }
    public bool NeedProEvaluation { get; set; }
    public EvaluationResult AutoEvaluationResult { get; set; }
    public EvaluationResult ProEvaluationResult { get; set; }

    public int Points { get; set; }             // for BonusCard

    public string Description { get; set; }      // for ProfileCard
    public string Degree { get; set; }
    public string StrongPoints { get; set; }
    public string WeakPoints { get; set; }

    public string Role { get; set; }
    public string Experience { get; set; }

    public string Seniority { get; set; }

    public string OldService { get; set; }

    public RoleGameType ProfileType { get; set; }

    public int SpecialCardEffect { get; set; } = 0;

    public int AttachedDocupentId { get; set; } = 0;

}



[Serializable]
public class GameStateData
{
    public bool Active { get; set; }
    public bool Completed { get; set; }

    public int CurrentPosition { get; set; } // Position commune sur le plateau


    public List<PlayerData> Players { get; set; }
    public List<CardData> Board { get; set; }

    public GameRulesData GameRules { get; set; }
    public int CurrentPlayerId { get; set; } // explicitly indicates whose turn it is

    public string SharedMessage { get; set; }

    public int CurrentArea { get; set; } = 0;
    public List<AreaStateData> AreaStates { get; set; } = new List<AreaStateData>();

    //new things to add
    public int TotalScore { get; set; }
    public List<NotificationTwily> Notifications { get; set; }
    public DateTime StartGame { get; set; }
    public DateTime TimeLastTurn { get; set; }
    public DateTime EndedTime { get; set; }

    public StepGameType Step { get; set; }
    public SpecialCardBudgetResponse SpecialCardBudgetResponse { get; set; } = new SpecialCardBudgetResponse();
    public SpecialCardCrisisResponse SpecialCardCrisisResponse { get; set; } = new SpecialCardCrisisResponse();

    public bool FirstCardsProfileChosen { get; set; } = false;
    public bool SecondCardsProfileChosen { get; set; } = false;
}

public class InitializeGamePayload
{
    public string SalonId { get; set; } = "";
    public string TeamId { get; set; } = "";
    public GameStateData Game { get; set; } = new GameStateData();
}

public class GameBoardInitializedPayload
{
    public string SalonId { get; set; } = "";
    public string TeamId { get; set; } = "";
    public GameStateData Game { get; set; } = new GameStateData();
}


[Serializable]
public class NotificationTwily
{
    public string idNotification { get; set; }
    public string notificationInfo { get; set; }
    public DateTime notificationTime { get; set; }
    public TypeNotification typeNotification { get; set; }
    public string idSalonNotif { get; set; }
    public string idTeamNotif { get; set; }

    public int idUserNotif { get; set; }
}

[Serializable]
public class SendNotificationRequest
{
    public string IdSalon { get; set; }
    public NotificationTwily Notification { get; set; }
}

[Serializable]
public class DeleteNotificationRequest
{
    public string IdSalon { get; set; }
    public string IdNotification { get; set; }
}





[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum TypeNotification
{
    VALIDATION,
    PM,
    ASKJOIN,
    STUCK
}

[Serializable]
public class AreaStateData
{
    public int idArea { get; set; }
    public List<CaseStateData> casesOnBoard { get; set; }
}

[Serializable]
public class CaseStateData
{
    public bool isVisited { get; set; }
    public int idCardOn { get; set; }
}

[Serializable]
public class PlayerData
{
    public UserInfo userInfo { get; set; }
    public int score { get; set; }
    public RoleGameType roleGame { get; set; }

    public TypeManagementResponsableQualiteEtProcessus TypeManagementQual { get; set; } = TypeManagementResponsableQualiteEtProcessus.AUCUN;
    public TypeManagementResponsableFormationEtSupportInterne TypeManagementFroma { get; set; } = TypeManagementResponsableFormationEtSupportInterne.AUCUN;
    public TypeManagementResponsableRelationClientele TypeManagementClient { get; set; } = TypeManagementResponsableRelationClientele.AUCUN;
    public TypeManagementResponsableAnalystesDeDonnees TypeManagementData { get; set; } = TypeManagementResponsableAnalystesDeDonnees.AUCUN;

    public List<CardData> cardsProfile { get; set; } = new List<CardData>();
}

[Serializable]
public class SpecialCardCrisisResponse
{
    public string FirstCause { get; set; }
    public string SecondCause { get; set; }
    public string ThirdCause { get; set; }
    public string FourthCause { get; set; }
    public string FifthCause { get; set; }
}

[Serializable]
public class SpecialCardBudgetResponse
{
    public List<SpecialCardBudgetData> SpecialCardBudgetDatas { get; set; } = new List<SpecialCardBudgetData>();
}

[Serializable]
public class SpecialCardBudgetData
{
    public RoleGameType Role { get; set; }
    public BudgetType Budget { get; set; }
    public int BudgetValue { get; set; }



}

[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum TypeManagementResponsableQualiteEtProcessus
{
    AUCUN,
    DIRECTIF,
    PARTICIPATIF,
    PAROBJECTIFS,
    DELEGATIF,
    TRANSFORMATIONNEL,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum TypeManagementResponsableFormationEtSupportInterne
{
    AUCUN,
    COACH,
    INSPIRANT,
    COLLABORATIF,
    AXESURLACOMMUNICATION,
    PAROBJECTIFS
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum TypeManagementResponsableRelationClientele
{
    AUCUN,
    COLLABORATIF,
    EMOTIONNEL,
    COACH,
    TRANSACTIONNEL,
    DEPROXIMITE
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum TypeManagementResponsableAnalystesDeDonnees
{
    AUCUN,
    AXESURLESRESULTATS,
    STRUCTURE,
    PARTICIPATIF,
    TRANSFORMATEUR,
    PARPROJET
}

[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum BudgetType
{
    SALARYRESPO,
    SALARYMEMBERS,
    FORMATION,
    OUTILTECH,
    FRAISOP,
    TOTAL
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoleGameType
{
    NOROLE,
    RESPOQUAL,
    RESPOCLI,
    RESPODATA,
    RESPOFORM

}
[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum StepGameType
{
    NOTSTARTED,
    STARTED,
    CHOSEROLE,
    ROLECHOSEN,
    PLAYCARD,
    SELECTTEAM,
    NEXTPARTCARD,
    NEXTSELECTTEAM,
    FINALCARD

}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypeCard
{
    QUESTION,
    BONUS,
    PROFILE,
    DEFI,
    KPI,
    PROFILMANAGEMENT,
    BLOCAGE
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvaluationResult
{
    NONE,
    WAITING,
    BAD,
    MID,
    GOOD
}
// Simple DTO that matches server messages
[Serializable]
public class ChatDTO
{
    public int FromId { get; set; }
    public string FromName { get; set; }
    public string Text { get; set; }
    public long Ts { get; set; }
    public ChatTarget Target { get; set; }


}

[Serializable]
public class ChatTarget
{
    public TypeChatTarget TypeChatTarget { get; set; }
    public int IdTarget { get; set; }
    public string StringIdTeam { get; set; }
    public string StringIdSalon { get; set; }
}
[JsonConverter(typeof(JsonStringEnumConverter))]
[Serializable]
public enum TypeChatTarget
{
    SALON,
    LOBBY,
    ADMIN,
    OBSERVER,
    PLAYER
}

[Serializable]
public class ChoseCardRequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public int IdCard { get; set; }
}

[Serializable]
public class ChoseCardResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public int IdCard { get; set; }
    public GameStateData Game { get; set; }
}

[Serializable]
public class AnswerCardRequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public CardData CardAnsewerd { get; set; }
    public int IdPlayerAnswered { get; set; }


}



[Serializable]
public class AnswerCardResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public GameStateData Game { get; set; }

}


[Serializable]
public class ChoseProfileRequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }

    public List<CardData> CardsChosen { get; set; } = new List<CardData>();

    public UserInfo UserInfo { get; set; }
}

[Serializable]
public class ChoseProfileResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public GameStateData Game { get; set; }
}

public class ValidateCardAdminRequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public int CardId { get; set; }
    public EvaluationResult NewState { get; set; }
}

[Serializable]
public class ValidateCardAdminResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public GameStateData Game { get; set; }
}

// DTOs shared with server
public class SubmitBudgetRequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public SpecialCardBudgetResponse Budget { get; set; }
    public int UserId { get; set; } // optional
}

public class SubmitCrisisRequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public SpecialCardCrisisResponse Crisis { get; set; }
    public int UserId { get; set; } // optional
}

[Serializable]
public class BudgetSubmittedResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public GameStateData Game { get; set; }
}

[Serializable]
public class CrisisSubmittedResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }
    public GameStateData Game { get; set; }
}

[Serializable]
public class SharedMessageRequest
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }

    public string TextShared { get; set; }

}

[Serializable]
public class SharedMessageResponse
{
    public string IdSalon { get; set; }
    public string IdTeam { get; set; }

    public string TextShared { get; set; }
}

public sealed class DeleteTeamRequest
{
    public string IdSalon  { get; set; } = ""; // big salon id
    public string IdTeam { get; set; } = ""; // team/sub-salon id
}

#region datas stat

[Serializable]
public class TwilyData
{
    public List<TwilyPersonnalData> PersonnalDatas { get; set; } = new List<TwilyPersonnalData>();
    public List<TwilyGameData> GameDatas { get; set; } = new List<TwilyGameData>();

    public int GameStarted { get; set; } = 0;
    public int GameCompleted { get; set; } = 0;
    public int CardPlayed { get; set; } = 0;
}

[Serializable]
public class TwilyPersonnalData
{
    public int IdPlayer { get; set; } = 0;
    public string Name { get; set; } = "";
    public int CardPlays { get; set; } = 0;
    public int PointTotalEarned { get; set; } = 0;
    public int GamePlayed { get; set; } = 0;
}
[Serializable]
public class TwilyGameData
{
    public string TeamID { get; set; } = "";

    public int CardsPlayed { get; set; } = 0;
    public List<TwilyPlayerData> PlayerDatas { get; set; }  = new List<TwilyPlayerData> ();

    public int TotalScore { get; set; } = 0;
    
    public int TotalCards { get; set; } = 0;

    public bool Completed { get; set; } = false;
}

[Serializable]
public class TwilyPlayerData
{
    public string Name { get; set; } = "";
    public int PlayerId { get; set; } = -1;

    public int CardsPlayed { get; set;} = 0;
    public int Score { get; set;} = 0;
    public string RoleInGame { get; set; } = "";

}

[Serializable]
public class AskIfGameStartedRequest
{
    public string BigSalonId { get; set; }
    public string TeamId { get; set; }
    public bool IsPlayer { get; set; }
    public bool IsPlayerOnTheteam { get; set; }

    public int IdPlayer { get; set; }
}

[Serializable]
public class AskIfGameStartedResponse
{
    public string TeamId { get; set; }
    public bool IsPlayer { get; set; }
    public bool IsPlayerOnTheteam { get; set; }
    public bool HaveGameStarted { get; set; }
    public GameStateData GameData { get; set; }
}



#endregion