// Helpers.cs
using Microsoft.Extensions.Options;
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
    #region general helpers
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
    #endregion

    #region jsonPart

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // set at runtime; do not mark readonly
    private static string _dataPath = "";
    private static string? _contentRoot;

    /// <summary>
    /// Call this once at app startup. Example in ASP.NET Core:
    /// TwilyStorage.ConfigurePaths(null, builder.Environment.ContentRootPath);
    /// </summary>
    public static void ConfigurePaths(string? configuredAbsolutePath = null, string? contentRoot = null)
    {
        _contentRoot = contentRoot;
        _dataPath = ResolveWritableDataPath(configuredAbsolutePath, contentRoot);
    }

    private static string DataPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_dataPath))
                _dataPath = ResolveWritableDataPath(null, _contentRoot);
            return _dataPath;
        }
    }

    // ------------- resource helpers -------------

    private static string GetResourcePath(string fileName)
    {
        // Works in Debug and after publish on Ubuntu (Resources copied to output)
        return Path.Combine(AppContext.BaseDirectory, "Resources", fileName);
    }

    private static string SafeCombine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return "";
        return Path.Combine(new[] { root! }.Concat(parts).ToArray());
    }

    private static void EnsureDir(string pathToFile)
    {
        var dir = Path.GetDirectoryName(pathToFile);
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
    }

    private static bool IsPathWritable(string pathToFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pathToFile)) return false;
            var dir = Path.GetDirectoryName(pathToFile);
            if (string.IsNullOrWhiteSpace(dir)) return false;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var probe = Path.Combine(dir, ".write_probe.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Picks a writable path, copies Resources/datas.json as seed if present
    private static string ResolveWritableDataPath(string? configuredAbsolutePath, string? contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredAbsolutePath))
        {
            var abs = Path.GetFullPath(configuredAbsolutePath!);
            EnsureDir(abs);
            return abs;
        }

        // try to use a seed from Resources if it exists
        var seedCandidates = new[]
        {
            SafeCombine(contentRoot, "Resources", "datas.json"),
            SafeCombine(AppContext.BaseDirectory, "Resources", "datas.json")
        };
        var seedPath = seedCandidates.FirstOrDefault(File.Exists);

        // 1) <content-root>/Resources (great for Debug with Kestrel)
        var crResources = SafeCombine(contentRoot, "Resources", "datas.json");
        if (IsPathWritable(crResources))
        {
            if (!File.Exists(crResources) && seedPath != null)
            {
                EnsureDir(crResources);
                File.Copy(seedPath, crResources, overwrite: true);
            }
            return crResources;
        }

        // 2) <base-dir>/Resources (publish output)
        var baseResources = SafeCombine(AppContext.BaseDirectory, "Resources", "datas.json");
        if (IsPathWritable(baseResources))
        {
            if (!File.Exists(baseResources) && seedPath != null)
            {
                EnsureDir(baseResources);
                File.Copy(seedPath, baseResources, overwrite: true);
            }
            return baseResources;
        }

        // 3) fallback to user home (Ubuntu friendly, no server steps)
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appDataDir = Path.Combine(home, ".local", "share", "Twily");
        var appDataPath = Path.Combine(appDataDir, "datas.json");
        EnsureDir(appDataPath);
        if (!File.Exists(appDataPath))
        {
            if (seedPath != null) File.Copy(seedPath, appDataPath, overwrite: true);
            else
            {
                var emptyJson = JsonSerializer.Serialize(new TwilyData(), JsonOpts);
                File.WriteAllText(appDataPath, emptyJson, new UTF8Encoding(false));
            }
        }
        return appDataPath;
    }

    private static async Task EnsureDataFileAsync()
    {
        EnsureDir(DataPath);
        if (!File.Exists(DataPath))
        {
            var json = JsonSerializer.Serialize(new TwilyData(), JsonOpts);
            await File.WriteAllTextAsync(DataPath, json, new UTF8Encoding(false));
        }
    }

    // ------------- JSON loaders from Resources -------------

    public static List<CardData> GetCardsFromJson()
    {
        string cardsPath = GetResourcePath("cards.json");
        if (!File.Exists(cardsPath))
            throw new FileNotFoundException("cards.json not found.", cardsPath);

        string json = File.ReadAllText(cardsPath);

        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };
        opts.Converters.Add(new FlexibleBoolConverter());

        return JsonSerializer.Deserialize<List<CardData>>(json, opts) ?? new List<CardData>();
    }

    /// <summary>
    /// Converter that accepts true/false, "true"/"false", "1"/"0", "yes"/"no"
    /// </summary>
    public sealed class FlexibleBoolConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.False) return false;

            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString()?.Trim();
                if (bool.TryParse(s, out var b)) return b;

                if (long.TryParse(s, out var n)) return n != 0;

                var lower = s?.ToLowerInvariant();
                if (lower == "y" || lower == "yes") return true;
                if (lower == "n" || lower == "no") return false;

                throw new JsonException($"Cannot convert string '{s}' to bool.");
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out var n)) return n != 0;
            }

            throw new JsonException($"Token {reader.TokenType} is not valid for bool.");
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
            => writer.WriteBooleanValue(value);
    }


    public static GameRulesData GetRulesFromJson()
    {
        string rulesPath = GetResourcePath("rules.json");
        if (!File.Exists(rulesPath))
            throw new FileNotFoundException("rules.json not found.", rulesPath);

        string json = File.ReadAllText(rulesPath);
        return JsonSerializer.Deserialize<GameRulesData>(json, JsonOpts) ?? new GameRulesData();
    }

    // concurrency safe consumption of link.txt (pop first line)
    public static async Task<string?> GetLinkFromText()
    {
        string linkPath = GetResourcePath("link.txt");
        if (!File.Exists(linkPath))
            throw new FileNotFoundException("link.txt not found.", linkPath);

        await Gate.WaitAsync();
        try
        {
            var links = File.ReadAllLines(linkPath).ToList();
            if (links.Count == 0) return null;

            string link = links[0];
            links.RemoveAt(0);

            File.WriteAllLines(linkPath, links);
            return link;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ------------- data store core (datas.json) -------------

    public static async Task<TwilyData> LoadTwilyDataAsync()
    {
        await Gate.WaitAsync();
        try
        {
            await EnsureDataFileAsync();

            var json = await File.ReadAllTextAsync(DataPath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<TwilyData>(json, JsonOpts) ?? new TwilyData();

            data.GameDatas ??= new List<TwilyGameData>();
            data.PersonnalDatas ??= new List<TwilyPersonnalData>();
            return data;
        }
        catch (JsonException)
        {
            // backup corrupted file and start fresh
            try
            {
                var backup = DataPath + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                if (File.Exists(DataPath)) File.Move(DataPath, backup);
            }
            catch { }

            var fresh = new TwilyData();
            var json = JsonSerializer.Serialize(fresh, JsonOpts);
            await File.WriteAllTextAsync(DataPath, json, new UTF8Encoding(false));
            return fresh;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task SaveTwilyDataAsync(TwilyData data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));

        await Gate.WaitAsync();
        try
        {
            EnsureDir(DataPath);
            var tmp = DataPath + ".tmp";
            var json = JsonSerializer.Serialize(data, JsonOpts);

            await File.WriteAllTextAsync(tmp, json, new UTF8Encoding(false));

            if (File.Exists(DataPath)) File.Delete(DataPath);
            File.Move(tmp, DataPath);
        }
        finally
        {
            Gate.Release();
        }
    }

    // Load -> mutate -> save (only saves if mutator returns true)
    public static async Task<bool> UpdateTwilyDataAsync(Func<TwilyData, bool> mutator)
    {
        if (mutator == null) throw new ArgumentNullException(nameof(mutator));

        await Gate.WaitAsync();
        try
        {
            await EnsureDataFileAsync();

            var json = await File.ReadAllTextAsync(DataPath, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<TwilyData>(json, JsonOpts) ?? new TwilyData();

            data.GameDatas ??= new List<TwilyGameData>();
            data.PersonnalDatas ??= new List<TwilyPersonnalData>();

            var changed = mutator(data);
            if (!changed) return false;

            var tmp = DataPath + ".tmp";
            var outJson = JsonSerializer.Serialize(data, JsonOpts);
            await File.WriteAllTextAsync(tmp, outJson, new UTF8Encoding(false));

            if (File.Exists(DataPath)) File.Delete(DataPath);
            File.Move(tmp, DataPath);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ------------- game lifecycle -------------

    public static async Task<TwilyData> InitializeNewGameData(GameStateData gameInit, string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) throw new ArgumentException("teamId is required", nameof(teamId));

        var data = await LoadTwilyDataAsync();
        data.GameDatas ??= new List<TwilyGameData>();
        data.PersonnalDatas ??= new List<TwilyPersonnalData>();

        data.GameStarted++;

        var gameData = new TwilyGameData
        {
            TeamID = teamId,
            CardsPlayed = 0,
            TotalScore = 0,
            TotalCards = gameInit?.Board?.Count ?? 0,
            Completed = false,
            PlayerDatas = new List<TwilyPlayerData>()
        };

        if (gameInit?.Players != null)
        {
            foreach (var user in gameInit.Players)
            {
                if (user?.userInfo == null) continue;

                int uid = user.userInfo.Id;
                string name = user.userInfo.Name ?? "";
                string role = user.roleGame.ToString() ?? "";

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

                var existingPerson = data.PersonnalDatas.FirstOrDefault(p => p.IdPlayer == uid);
                if (existingPerson == null)
                {
                    data.PersonnalDatas.Add(new TwilyPersonnalData
                    {
                        IdPlayer = uid,
                        Name = name,
                        CardPlays = 0,
                        PointTotalEarned = 0,
                        GamePlayed = 1
                    });
                }
                else
                {
                    existingPerson.GamePlayed += 1;
                    if (!string.IsNullOrWhiteSpace(name) && !name.Equals(existingPerson.Name))
                        existingPerson.Name = name;
                }
            }
        }

        data.GameDatas.Add(gameData);

        await SaveTwilyDataAsync(data);
        return data;
    }

    public static async Task<TwilyData> PlayerRespondCard(GameStateData gameInit, string teamId, CardData card)
    {
        if (gameInit == null) throw new ArgumentNullException(nameof(gameInit));
        if (string.IsNullOrWhiteSpace(teamId)) throw new ArgumentException("teamId is required", nameof(teamId));

        int playerId = gameInit.CurrentPlayerId;
        int points = card?.Points ?? 0;
        bool nowCompleted = (gameInit.Board != null) && gameInit.Board.Count > 0 && gameInit.Board.All(x => x.Unlocked);

        await UpdateTwilyDataAsync(data =>
        {
            data.GameDatas ??= new List<TwilyGameData>();
            data.PersonnalDatas ??= new List<TwilyPersonnalData>();

            data.CardPlayed += 1;

            var game = data.GameDatas.FirstOrDefault(g => string.Equals(g.TeamID, teamId, StringComparison.Ordinal));
            if (game == null)
            {
                game = new TwilyGameData
                {
                    TeamID = teamId,
                    PlayerDatas = new List<TwilyPlayerData>(),
                    TotalCards = 0,
                    TotalScore = 0,
                    CardsPlayed = 0,
                    Completed = false
                };
                data.GameDatas.Add(game);
            }

            game.TotalScore += points;
            game.CardsPlayed += 1;
            game.TotalCards = Math.Max(game.TotalCards, game.CardsPlayed);

            if (nowCompleted && !game.Completed)
            {
                game.Completed = true;
                data.GameCompleted += 1;
            }
            else
            {
                game.Completed = nowCompleted;
            }

            var u = gameInit.Players?.FirstOrDefault(x => x.userInfo?.Id == playerId);

            var gp = game.PlayerDatas.FirstOrDefault(p => p.PlayerId == playerId);
            if (gp == null)
            {
                gp = new TwilyPlayerData
                {
                    PlayerId = playerId,
                    Name = u?.userInfo?.Name ?? "",
                    RoleInGame = u?.roleGame.ToString() ?? ""
                };
                game.PlayerDatas.Add(gp);
            }
            gp.CardsPlayed += 1;
            if (u != null)
            {
                gp.Score = u.score; // mirror live state
                gp.RoleInGame = u.roleGame.ToString() ?? gp.RoleInGame ?? "";
                if (!string.IsNullOrWhiteSpace(u.userInfo?.Name)) gp.Name = u.userInfo!.Name!;
            }

            var pd = data.PersonnalDatas.FirstOrDefault(p => p.IdPlayer == playerId);
            if (pd == null)
            {
                pd = new TwilyPersonnalData
                {
                    IdPlayer = playerId,
                    Name = u?.userInfo?.Name ?? "",
                    CardPlays = 0,
                    PointTotalEarned = 0,
                    GamePlayed = 0
                };
                data.PersonnalDatas.Add(pd);
            }
            pd.CardPlays += 1;
            pd.PointTotalEarned += points;

            return true;
        });

        return await LoadTwilyDataAsync();
    }

    // ------------- convenience helpers -------------

    public static Task<bool> AddOrUpdatePersonAsync(TwilyPersonnalData p)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));

        return UpdateTwilyDataAsync(data =>
        {
            data.PersonnalDatas ??= new List<TwilyPersonnalData>();

            var idx = data.PersonnalDatas.FindIndex(x => x.IdPlayer == p.IdPlayer);
            if (idx >= 0)
                data.PersonnalDatas[idx] = p;
            else
                data.PersonnalDatas.Add(p);
            return true;
        });
    }

    public static Task<bool> UpsertGameByTeamIdAsync(TwilyGameData g)
    {
        if (g == null) throw new ArgumentNullException(nameof(g));

        return UpdateTwilyDataAsync(data =>
        {
            data.GameDatas ??= new List<TwilyGameData>();

            var idx = data.GameDatas.FindIndex(x => string.Equals(x.TeamID, g.TeamID, StringComparison.Ordinal));
            if (idx >= 0)
                data.GameDatas[idx] = g;
            else
                data.GameDatas.Add(g);
            return true;
        });
    }

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

    public static Task<bool> RecordCardPlayedAsync(string teamId, int playerId, int addScore = 0)
    {
        if (string.IsNullOrWhiteSpace(teamId)) throw new ArgumentException("teamId is required", nameof(teamId));

        return UpdateTwilyDataAsync(data =>
        {
            data.GameDatas ??= new List<TwilyGameData>();

            var game = data.GameDatas.Find(g => string.Equals(g.TeamID, teamId, StringComparison.Ordinal));
            if (game == null)
            {
                game = new TwilyGameData
                {
                    TeamID = teamId,
                    PlayerDatas = new List<TwilyPlayerData>(),
                    TotalCards = 0,
                    TotalScore = 0,
                    CardsPlayed = 0,
                    Completed = false
                };
                data.GameDatas.Add(game);
            }

            game.CardsPlayed += 1;
            game.TotalCards = Math.Max(game.TotalCards, game.CardsPlayed);
            game.TotalScore += addScore;

            var player = game.PlayerDatas.FirstOrDefault(p => p.PlayerId == playerId);
            if (player == null)
            {
                player = new TwilyPlayerData { PlayerId = playerId, Name = "" };
                game.PlayerDatas.Add(player);
            }
            player.CardsPlayed += 1;
            player.Score += addScore;

            data.CardPlayed += 1;
            return true;
        });
    }

    public static Task<bool> CompleteGameAsync(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId)) throw new ArgumentException("teamId is required", nameof(teamId));

        return UpdateTwilyDataAsync(data =>
        {
            data.GameDatas ??= new List<TwilyGameData>();

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
    public int idCase { get; set; }
    public bool isVisited { get; set; }
    public int idCardOn { get; set; }

    public float xpos { get; set; }
    public float ypos { get; set; }
    public float zpos { get; set; }
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


[Serializable]
public class AskAdminDataRequest
{
    public string BigSalonId { get; set; }

}

[Serializable]
public class AskAdminDataResponse
{
    public string BigSalonId { get; set; }

    public List<NotificationTwily> Notifications { get; set; } = new List<NotificationTwily>();
    public List<ResumeGameAdmin> GamesInSalon { get; set; } = new List<ResumeGameAdmin>();

}

[Serializable]
public class ResumeGameAdmin
{
    public string IdSalon { get; set; }

    public string NameSalon { get; set; }

    public GameStateData GameState { get; set; }
}
