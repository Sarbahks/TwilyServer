using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.Json;

public sealed class ServerState
{
    private readonly ConcurrentDictionary<string, BigSalonInfo> _bigSalons =
        new(StringComparer.Ordinal);



    #region salon
    public bool TryAddBigSalon(BigSalonInfo salon, out string? error)
    {
        if (salon == null) { error = "null-payload"; return false; }
        if (string.IsNullOrWhiteSpace(salon.Id)) { error = "missing-id"; return false; }

        if (_bigSalons.TryAdd(salon.Id, salon))
        {
            salon.UserInBig = new List<UserInfo>();
            salon.Salons = new List<SalonInfo>();

            error = null;
            return true;
        }

        error = "already-exists";
        return false;
    }

    public IReadOnlyCollection<BigSalonInfo> GetBigSalons()
        => _bigSalons.Values.ToList();

    public bool TryGetBigSalon(string id, out BigSalonInfo salon)
        => _bigSalons.TryGetValue(id, out salon!);

    public bool TryToJoinBigSalon(JoinSalonRequest join)
    {
        if (join == null || string.IsNullOrWhiteSpace(join.SalonId) || join.UserInfo == null)
            return false;

        foreach (var salon in _bigSalons.Values)
        {
            if (salon.Id == join.SalonId)
            {
                salon.UserInBig ??= new List<UserInfo>();

                // check if same Id already exists
                if (salon.UserInBig.Any(u => u.Id == join.UserInfo.Id))
                    return false; // already joined

                salon.UserInBig.Add(join.UserInfo);
                return true;
            }
        }

        return false;
    }

    #endregion
    #region team
    public bool TryToCreateTeam(CreateTeamRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.IdSalon) || req.SalonInfo == null)
            return false;

        if (!_bigSalons.TryGetValue(req.IdSalon, out var bigSalon))
            return false;

        lock (bigSalon)
        {
            bigSalon.Salons ??= new List<SalonInfo>();

            // prevent duplicates by Id
            if (bigSalon.Salons.Any(s => s.Id == req.SalonInfo.Id))
                return false;

            bigSalon.Salons.Add(req.SalonInfo);
        }
        return true;
    }

    public bool TryJoinTeam(JoinTeamRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.SalonId) ||
            string.IsNullOrWhiteSpace(req.TeamId) || req.UserInfo == null)
            return false;

        if (!_bigSalons.TryGetValue(req.SalonId, out var big)) return false;

        lock (big)
        {
            if (big.Salons == null) return false;

            var team = big.Salons.FirstOrDefault(s => s.Id == req.TeamId);
            if (team == null) return false;

            team.UsersInSalon ??= new List<UserInfo>();
            team.WhiteList ??= new List<string>();

            bool allowed = false;

            if (req.UserInfo.Roles != null && req.UserInfo.Roles.Contains("administrator"))
            {
                allowed = true;
            }

            else
            {
                // allow if whitelist empty OR username present in whitelist (case-insensitive)
                allowed = team.WhiteList.Count == 0 ||
                              team.WhiteList.Any(w => string.Equals(w, req.UserInfo.Name, StringComparison.OrdinalIgnoreCase));
                if (!allowed) return false;
            }
  

            // no duplicates by Id
            if (team.UsersInSalon.Any(u => u.Id == req.UserInfo.Id))
                return false;

            team.UsersInSalon.Add(req.UserInfo);
            return true;
        }
    }

    public bool TryLeaveTeam(LeaveTeamRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.SalonId) ||
            string.IsNullOrWhiteSpace(req.TeamId) || req.UserInfo == null)
            return false;

        if (!_bigSalons.TryGetValue(req.SalonId, out var big)) return false;

        lock (big)
        {
            var team = big.Salons?.FirstOrDefault(s => s.Id == req.TeamId);
            if (team?.UsersInSalon == null) return false;

            var removed = team.UsersInSalon.RemoveAll(u => u.Id == req.UserInfo.Id) > 0;
            return removed;
        }
    }

    public bool TryToLeaveBigSalon(LeaveSalonRequest leave)
    {
        if (leave == null || string.IsNullOrWhiteSpace(leave.SalonId) || leave.UserInfo == null)
            return false;

        if (!_bigSalons.TryGetValue(leave.SalonId, out var salon)) return false;

        lock (salon)
        {
            if (salon.UserInBig == null) return false;
            // Remove by Id
            var removed = salon.UserInBig.RemoveAll(u => u.Id == leave.UserInfo.Id) > 0;
            return removed;
        }
    }
    #endregion
    #region Game
    // in ServerState

  
    private readonly ConcurrentDictionary<string, int> _initLeaderByTeam =
        new(StringComparer.Ordinal);

    private static string Norm(string s) => (s ?? string.Empty).Trim();
    private static string Key(string salonId, string teamId) => Norm(salonId) + "::" + Norm(teamId);

    // team readiness: key -> set of userIds (as keys)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _teamReady
        = new(StringComparer.Ordinal);

    // helper: get BigSalon and team, and ensure game not started
    public bool TryGetTeam(string salonId, string teamId, out BigSalonInfo big, out SalonInfo team, bool allowActive = true)
    {
        big = null!;
        team = null!;

        if (!_bigSalons.TryGetValue(salonId.Trim(), out big))
            return false;

        lock (big)
        {
            team = big.Salons?.FirstOrDefault(s => s.Id == teamId.Trim());
            if (team == null) return false;

            if (!allowActive && team.GameState != null && team.GameState.Active)
                return false; // used only when you want to block ready-up

            return true;
        }
    }




    // choose a leader deterministically (lowest userId)
    public static int ChooseLeaderUserId(IEnumerable<int> ids)
        => ids.OrderBy(x => x).First();

    // Build an initial GameStateData (you can enrich later)
    public GameStateData BuildInitialGameState(string salonId, string teamId)
    {
        if (!TryGetTeam(salonId, teamId, out var big, out var team))
            return new GameStateData();

        List<PlayerData> players;
        lock (big)
        {
            // create players from current team members
            players = (team.UsersInSalon ?? new List<UserInfo>())
                .Select(u => new PlayerData
                {
                    userInfo = u,
                    score = 0,
                    roleGame = RoleGameType.NOROLE,
                    cardsProfile = new List<CardData>()
                })
                .ToList();
        }

        return SetupGameStateFromJson(players);
    }

 


    public GameStateData SetupGameStateFromJson(List<PlayerData> players)
    {


        // deserialize
        GameRulesData rules = GetRulesFromJson();
        List<CardData> cards = GetCardsFromJson();

        // wrap in game state
        return new GameStateData
        {
            GameRules = rules,
            Board = cards,
            Active = false,
            Completed = false,
            CurrentPosition = -1,
            Players = players,
            CurrentPlayerId = players.FirstOrDefault()?.userInfo?.Id ?? 0,
            SharedMessage = "",
            AreaStates = new List<AreaStateData>(),
            TotalScore = 0,
            Notifications = new List<NotificationTwily>(),
            Step = StepGameType.NOTSTARTED
        };
    }

    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        Converters =
    {
        new JsonStringEnumConverter(),   // your existing enum converter
        new BoolFromStringConverter()    // new converter for string booleans
    }
    };

    public static List<CardData> GetCardsFromJson()
    {
        string cardsPath = Path.Combine("Resources", "cards.json");
        string json = File.ReadAllText(cardsPath);
        return JsonSerializer.Deserialize<List<CardData>>(json, Options);
    }

    public static GameRulesData GetRulesFromJson()
    {
        string rulesPath = Path.Combine("Resources", "rules.json");
        string json = File.ReadAllText(rulesPath);
        return JsonSerializer.Deserialize<GameRulesData>(json, Options);
    }



    public bool TryReadyPlayer(StartGameRequest req, out int readyCount, out List<int> readyIds)
    {
        readyCount = 0; readyIds = new List<int>();
        if (req == null || req.UserInfo == null ||
            string.IsNullOrWhiteSpace(req.SalonId) || string.IsNullOrWhiteSpace(req.TeamId))
            return false;

        if (!TryGetTeam(req.SalonId, req.TeamId, out var big, out var team))
            return false;

        // ensure user is in this team
        bool inTeam;
        lock (big)
        {
            team.UsersInSalon ??= new List<UserInfo>();
            inTeam = team.UsersInSalon.Any(u => u.Id == req.UserInfo.Id);
        }
        if (!inTeam) return false;

        var k = Key(req.SalonId, req.TeamId);
        var set = _teamReady.GetOrAdd(k, _ => new ConcurrentDictionary<int, byte>());
        set[req.UserInfo.Id] = 1;

        readyIds = set.Keys.ToList();
        readyCount = readyIds.Count;
        return true;
    }


    public bool TryUnreadyPlayer(LeaveGameRequest req, out int readyCount, out List<int> readyIds)
    {
        readyCount = 0; readyIds = new List<int>();
        if (req == null || req.UserInfo == null ||
            string.IsNullOrWhiteSpace(req.SalonId) || string.IsNullOrWhiteSpace(req.TeamId))
            return false;

        if (!TryGetTeam(req.SalonId, req.TeamId, out _, out _))
            return false;

        var k = Key(req.SalonId, req.TeamId);
        if (!_teamReady.TryGetValue(k, out var set)) return false;

        set.TryRemove(req.UserInfo.Id, out _);
        readyIds = set.Keys.ToList();
        readyCount = readyIds.Count;
        return true;
    }


    // record the chosen leader for a team
    public void SetInitLeader(string salonId, string teamId, int userId)
    {
        _initLeaderByTeam[Key(salonId, teamId)] = userId;
    }

    public bool TryGetInitLeader(string salonId, string teamId, out int userId)
    {
        return _initLeaderByTeam.TryGetValue(Key(salonId, teamId), out userId);
    }

    public bool ApplyInitializedGame(string salonId, string teamId, GameStateData game)
    {
        if (!TryGetTeam(salonId, teamId, out var big, out var team))
            return false;

        var key = Key(salonId, teamId);

        // Find the ready user ids for this team
        List<int> readyIds = new List<int>();
        if (_teamReady.TryGetValue(key, out var set))
            readyIds = set.Keys.ToList();

        // Build Players list from the ready users present in this team
        List<PlayerData> players;
        lock (big)
        {
            var teamUsers = team.UsersInSalon ?? new List<UserInfo>();

            // Select only users who are in the ready set (limit to 4 if more)
            var selectedUsers = teamUsers
                .Where(u => readyIds.Contains(u.Id))
                .OrderBy(u => u.Id)
                .Take(4)
                .ToList();

            // Fallback: if for some reason readyIds is empty, use first 4 team users
            if (selectedUsers.Count == 0)
                selectedUsers = teamUsers.OrderBy(u => u.Id).Take(4).ToList();

            players = selectedUsers.Select(u => new PlayerData
            {
                userInfo = u,
                score = 0,
                roleGame = RoleGameType.NOROLE,
                cardsProfile = new List<CardData>()
            }).ToList();
        }

        // Ensure we have a game object and non-null collections
        var gs = game ?? new GameStateData();
        gs.Players = players;

        gs.Notifications ??= new List<NotificationTwily>();
    
        // Set initial runtime state
        gs.Active = true;
        gs.Completed = false;
        gs.StartGame = DateTime.UtcNow;
        gs.TimeLastTurn = gs.StartGame;
        gs.Step = StepGameType.CHOSEROLE;

        // Pick a deterministic initial current player if not set
        if (gs.CurrentPlayerId == 0 && players.Count > 0)
            gs.CurrentPlayerId = players.First().userInfo.Id;

        // Apply to team
        lock (big)
        {
            team.GameState = gs;
        }

        // Clear readiness for this team now that the game is active
        _teamReady.TryRemove(key, out _);

        return true;
    }


    // get team user ids (to notify only that team)
    public HashSet<int> GetTeamUserIds(string salonId, string teamId)
    {
        var set = new HashSet<int>();
        if (!TryGetTeam(salonId, teamId, out var big, out var team)) return set;

        lock (big)
        {
            var users = team.UsersInSalon ?? new List<UserInfo>();
            foreach (var u in users) set.Add(u.Id);
            Console.WriteLine($"[GetTeamUserIds] {salonId}/{teamId}: {set.Count} users -> [{string.Join(",", set)}]");
        }
        return set;
    }



    #endregion

    #region role
    public bool TryAssignRoleAndMaybeAdvanceStep(
    string salonId,
    string teamId,
    UserInfo user,
    RoleGameType role,
    out GameStateData game,
    out string error)
    {
        game = null;
        error = null;

        if (!TryGetTeam(salonId, teamId, out var big, out var team))
        {
            error = "not-found";
            return false;
        }
        if (user == null || user.Id == 0)
        {
            error = "missing-user-id";
            return false;
        }
        if (role == RoleGameType.NOROLE)
        {
            error = "invalid-role";
            return false;
        }

        lock (big)
        {
            game = team.GameState;
            if (game == null)
            {
                error = "no-game";
                return false;
            }

            game.Players ??= new List<PlayerData>();

            var player = game.Players.FirstOrDefault(p => p?.userInfo?.Id == user.Id);
            if (player == null)
            {
                player = new PlayerData
                {
                    userInfo = user,
                    score = 0,
                    roleGame = RoleGameType.NOROLE,
                    cardsProfile = new List<CardData>()
                };
                game.Players.Add(player);
            }

            // uniqueness
            bool taken = game.Players.Any(p => p.roleGame == role && (p.userInfo?.Id ?? -1) != user.Id);
            if (taken)
            {
                error = "role-taken";
                return false;
            }

            // assign
            player.roleGame = role;

            // step advance rule
            var allowed = new HashSet<RoleGameType> {
            RoleGameType.RESPOQUAL, RoleGameType.RESPOCLI, RoleGameType.RESPODATA, RoleGameType.RESPOFORM
        };

            var assigned = game.Players
                .Where(p => p.roleGame != RoleGameType.NOROLE)
                .Select(p => p.roleGame)
                .ToList();

            bool allFromAllowed = assigned.Count >= 4 && assigned.All(r => allowed.Contains(r));
            int distinctCount = assigned.Distinct().Count();

            if (game.Players.Count >= 4 && distinctCount >= 4 && allFromAllowed)
                game.Step = StepGameType.ROLECHOSEN;

            return true;
        }
    }

    #endregion
}

public class BoolFromStringConverter : System.Text.Json.Serialization.JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            return bool.TryParse(s, out var b) && b;
        }
        else if (reader.TokenType == JsonTokenType.True)
        {
            return true;
        }
        else if (reader.TokenType == JsonTokenType.False)
        {
            return false;
        }
        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
    {
        writer.WriteBooleanValue(value);
    }
}
