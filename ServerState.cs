using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Text.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static Helpers;

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

    public bool TryRemoveBigSalon(
    string bigSalonId,
    out List<int> affectedUserIds,
    out string? error)
    {
        affectedUserIds = new List<int>();

        if (string.IsNullOrWhiteSpace(bigSalonId))
        {
            error = "missing-id";
            return false;
        }

        if (!_bigSalons.TryRemove(bigSalonId, out var big))
        {
            error = "not-found";
            return false;
        }

        // Collect users that were in the big salon
        if (big.UserInBig != null)
            affectedUserIds.AddRange(big.UserInBig.Select(u => u.Id));

        // Optional: if you track reverse indexes, clear them here
        // foreach (var uid in affectedUserIds) RemoveUserFromBigSalon(uid, bigSalonId);

        // Clear residual data on this BigSalonInfo instance (defensive)
        lock (big)
        {
            big.UserInBig?.Clear();
            if (big.Salons != null)
            {
                foreach (var s in big.Salons)
                    s.UsersInSalon?.Clear();
                big.Salons.Clear();
            }
        }

        error = null;
        return true;
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

    public bool TryDeleteTeam(string bigSalonId, string teamId,
    out List<int> affectedUserIds, out string? error)
    {
        affectedUserIds = new List<int>();
        error = null;

        if (string.IsNullOrWhiteSpace(bigSalonId) || string.IsNullOrWhiteSpace(teamId))
        {
            error = "missing-id";
            return false;
        }

        if (!_bigSalons.TryGetValue(bigSalonId, out var big))
        {
            error = "big-salon-not-found";
            return false;
        }

        lock (big)
        {
            if (big.Salons == null || big.Salons.Count == 0)
            {
                error = "no-teams";
                return false;
            }

            var idx = big.Salons.FindIndex(s => s.Id == teamId);
            if (idx < 0)
            {
                error = "team-not-found";
                return false;
            }

            // collect users before removing
            var team = big.Salons[idx];
            if (team?.UsersInSalon != null)
                affectedUserIds.AddRange(team.UsersInSalon.Select(u => u.Id));

            // remove the team
            big.Salons.RemoveAt(idx);
        }

        return true;
    }

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

        return SetupGameStateFromJson(players, team);
    }

 


    public GameStateData SetupGameStateFromJson(List<PlayerData> players, SalonInfo team)
    {


        // deserialize
        GameRulesData rules = GetRulesFromJson();
        List<CardData> cards = GetCardsFromJson();

        // wrap in game state
        var gameState =  new GameStateData
        {
            GameRules = rules,
            Board = cards,
            Active = false,
            Completed = false,
            CurrentPosition = 1,
            Players = players,
            CurrentPlayerId = players.FirstOrDefault()?.userInfo?.Id ?? 0,
            SharedMessage = "Lien meet : " + GetLinkFromText(),
            AreaStates = new List<AreaStateData>(),
            TotalScore = 0,
            Notifications = new List<NotificationTwily>(),
            Step = StepGameType.NOTSTARTED
        };

        team.GameState = gameState;
        return gameState;
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

    public static string GetLinkFromText()
    {
        string linkPath = Path.Combine("Resources", "link.txt");
        if (!File.Exists(linkPath))
            throw new FileNotFoundException("Link file not found.", linkPath);

        // Read all lines
        var links = File.ReadAllLines(linkPath).ToList();

        if (links.Count == 0)
            return null; // no more links available

        // Take first link
        string link = links[0];

        // Remove it from list
        links.RemoveAt(0);

        // Write remaining back to file
        File.WriteAllLines(linkPath, links);

        return link;
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

    public GameStateData ApplyInitializedGame(string salonId, string teamId, GameStateData game)
    {
        if (!TryGetTeam(salonId, teamId, out var big, out var team))
            return game;

        var key = Key(salonId, teamId);

        // Find the ready user ids for this team
        List<int> readyIds = new List<int>();
        if (_teamReady.TryGetValue(key, out var set))
            readyIds = set.Keys.ToList();

        // Build Players list from the ready users present in this team
        
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

        }

        // Ensure we have a game object and non-null collections
        var gs = game ?? new GameStateData();


        gs.Notifications ??= new List<NotificationTwily>();
    
        // Set initial runtime state
        gs.Active = true;
        gs.Completed = false;
        gs.StartGame = DateTime.UtcNow;
        gs.TimeLastTurn = gs.StartGame;
        gs.Step = StepGameType.CHOSEROLE;



        // Apply to team
        lock (big)
        {
            team.GameState = gs;
        }

        // Clear readiness for this team now that the game is active
        _teamReady.TryRemove(key, out _);

        return game;
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

    #region cardManagement
    public bool TryUnlockCard(string salonId, string teamId, int cardId, out string error)
    {
        error = null;

        if (!TryGetTeam(salonId, teamId, out var big, out var team))
        {
            error = "not-found";
            return false;
        }

        lock (big)
        {
            var game = team.GameState;
            if (game == null)
            {
                error = "no-game";
                return false;
            }

            var board = game.Board;
            if (board == null || board.Count == 0)
            {
                error = "no-board";
                return false;
            }

            var card = board.FirstOrDefault(c => c.Id == cardId);
            if (card == null)
            {
                error = "card-not-found";
                return false;
            }
            game.CurrentPosition = card.Id;

            foreach(var area in game.AreaStates)
            {
                foreach(var bc in area.casesOnBoard)
                {
                    if(bc.idCardOn == cardId)
                    {
                        bc.isVisited = true;
                    }
                }
            }

                if(game.Step == StepGameType.SELECTTEAM) 
                {
                    game.Step = StepGameType.NEXTPARTCARD;
                }
                else if (game.Step == StepGameType.NEXTPARTCARD)
                {
                    //second selection
                }
                else
                {
                    game.Step = StepGameType.PLAYCARD;
                }
 
            // idempotent: always set to true
            card.Unlocked = true;
            return true;
        }
    }

    public GameStateData GetGameState(string salonId, string teamId)
    {
        if (!TryGetTeam(salonId, teamId, out var big, out var team))
        {
           
            return null;
        }
        lock (big)
        {
            var game = team.GameState;
            if (game == null)
            {

                return null;
            }
            return game;
        }
     
    }
    public bool TryValidateCardAdmin(
    string salonId,
    string teamId,
    int cardId,
    EvaluationResult newState,
    out GameStateData game,
    out string error)
    {
        game = null;
        error = null;

        if (string.IsNullOrWhiteSpace(salonId) || string.IsNullOrWhiteSpace(teamId))
        {
            error = "invalid-ids";
            return false;
        }
        if (cardId <= 0)
        {
            error = "invalid-card-id";
            return false;
        }

        if (!TryGetTeam(salonId, teamId, out var big, out var team))
        {
            error = "not-found";
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
            if (game.Board == null || game.Board.Count == 0)
            {
                error = "no-board";
                return false;
            }

            var card = game.Board.FirstOrDefault(c => c != null && c.Id == cardId);
            if (card == null)
            {
                error = "card-not-found";
                return false;
            }

            // Apply admin validation
            card.ProEvaluationResult = newState;

            // Optional misc bookkeeping
            game.TimeLastTurn = DateTime.UtcNow;

            return true;
        }
    }

    public bool TryApplyAnswerAndAdvanceTurn(
        string salonId,
        string teamId,
        CardData answeredCard,
        int playerId,
        out GameStateData game,
        out string error)
    {
        game = null;
        error = null;

        if (answeredCard == null) { error = "missing-card"; return false; }
        if (!TryGetTeam(salonId, teamId, out var big, out var team)) { error = "not-found"; return false; }

        lock (big)
        {
            game = team.GameState;
            if (game == null) { error = "no-game"; return false; }
            if (game.Board == null || game.Board.Count == 0) { error = "no-board"; return false; }

            var boardCard = game.Board.FirstOrDefault(c => c.Id == answeredCard.Id);
            if (boardCard == null) { error = "card-not-found"; return false; }

            // update response
            boardCard.Response = answeredCard.Response;

            // points
            var points = boardCard.Points;
            if (points != 0 && playerId != 0)
            {
                game.Players ??= new List<PlayerData>();
                var player = game.Players.FirstOrDefault(p => p?.userInfo?.Id == playerId);
                if (player != null) player.score += points;
                game.TotalScore += points;
            }

            // advance turn WITHOUT capturing 'game' in a lambda
            var players = game.Players ?? new List<PlayerData>();
            int currentId = game.CurrentPlayerId;

            int idx = players.FindIndex(p => p?.userInfo?.Id == currentId);
            if (idx < 0) idx = 0;

            int nextIdx = players.Count > 0 ? (idx + 1) % players.Count : 0;
            int nextId = (players.Count > 0) ? (players[nextIdx]?.userInfo?.Id ?? 0) : 0;
            if (nextId != 0) game.CurrentPlayerId = nextId;

            game.TimeLastTurn = DateTime.UtcNow;

            var currentArea = game.CurrentArea; // keep previous as fallback
            foreach (var area in game.AreaStates)
            {
                if (area.casesOnBoard?.Any(bc => bc.isVisited) == true)
                {
                    currentArea = area.idArea;
                    break; // only one area can be active/visited, so stop here
                }
            }
            game.CurrentArea = currentArea;




            return true;
        }
    }

    internal bool TryApplyProfileToPlayer(
        string idSalon,
        string idTeam,
        UserInfo userInfo,
        List<CardData> cardsChosen,
        out GameStateData game,
        out string error)
    {
        game = null;
        error = null;

        if (string.IsNullOrWhiteSpace(idSalon) || string.IsNullOrWhiteSpace(idTeam))
        {
            error = "invalid-ids";
            return false;
        }
        if (userInfo == null || userInfo.Id == 0)
        {
            error = "missing-user";
            return false;
        }

        if (!TryGetTeam(idSalon, idTeam, out var big, out var team))
        {
            error = "not-found";
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
            game.Board ??= new List<CardData>();

            // Ensure player exists
            var player = game.Players.FirstOrDefault(p => p?.userInfo?.Id == userInfo.Id);
            if (player == null)
            {
               return false;
            }

            player.cardsProfile = cardsChosen;

            bool everyoneHasProfile = game.Players != null &&
                                game.Players.Count > 0 &&
                                game.Players.All(p => p.cardsProfile != null && p.cardsProfile.Count > 0);

            if (everyoneHasProfile)
            {
                // Advance the step of the game
                game.Step = StepGameType.SELECTTEAM; // or whatever step enum you want to use
            }


            return true;
        }
    }




    #endregion

    #region notification
    public bool TryAddNotificationToBigSalon(
    string salonId,
    NotificationTwily notif,
    out BigSalonInfo bigOut,
    out string error)
    {
        bigOut = null;
        error = null;

        if (string.IsNullOrWhiteSpace(salonId) || notif == null)
        {
            error = "invalid-payload";
            return false;
        }

        if (!TryGetBigSalon(salonId, out var big))
        {
            error = "not-found";
            return false;
        }

        // Normalize
        notif.idNotification = string.IsNullOrWhiteSpace(notif.idNotification)
            ? Guid.NewGuid().ToString("N")
            : notif.idNotification;
        notif.notificationTime = (notif.notificationTime == default)
            ? DateTime.UtcNow
            : notif.notificationTime;
        notif.idSalonNotif = string.IsNullOrWhiteSpace(notif.idSalonNotif)
            ? salonId
            : notif.idSalonNotif;

        lock (big)
        {
            big.Notifications ??= new List<NotificationTwily>();
            if (!big.Notifications.Any(n => n.idNotification == notif.idNotification))
                big.Notifications.Add(notif);

            bigOut = big; // see below
            return true;
        }
    }

    public bool TryRemoveNotificationFromBigSalon(
        string salonId,
        string notifId,
        out BigSalonInfo bigOut,
        out string error)
    {
        bigOut = null;
        error = null;

        if (string.IsNullOrWhiteSpace(salonId) || string.IsNullOrWhiteSpace(notifId))
        {
            error = "invalid-payload";
            return false;
        }

        if (!TryGetBigSalon(salonId, out var big))
        {
            error = "not-found";
            return false;
        }

        lock (big)
        {
            var list = big.Notifications ??= new List<NotificationTwily>();
            int removed = list.RemoveAll(n => n.idNotification == notifId);
            if (removed == 0)
            {
                error = "notif-not-found";
                return false;
            }

            bigOut =big; // see below
            return true;
        }
    }
    public HashSet<int> GetBigSalonAdminObserverUserIds(string salonId)
    {
        var result = new HashSet<int>();
        if (!TryGetBigSalon(salonId, out var big)) return result;

        lock (big)
        {
            var wanted = new HashSet<string>(new[] { "administrator", "observer" }
                .Select(r => r.ToLowerInvariant()));

            foreach (var u in big.UserInBig ?? new List<UserInfo>())
            {
                var roles = u.Roles ?? new string[0];
                bool ok = roles.Any(r => !string.IsNullOrWhiteSpace(r) && wanted.Contains(r.ToLowerInvariant()));
                if (ok && u.Id != 0) result.Add(u.Id);
            }
        }
        return result;
    }


    public bool TryApplyBudgetToGame(
    string salonId,
    string teamId,
    SpecialCardBudgetResponse budget,
    out GameStateData game,
    out string error)
    {
        game = null;
        error = null;

        if (string.IsNullOrWhiteSpace(salonId) || string.IsNullOrWhiteSpace(teamId))
        {
            error = "invalid-ids";
            return false;
        }
        if (!TryGetTeam(salonId, teamId, out var big, out var team))
        {
            error = "not-found";
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

            // normalize: never store null
            budget ??= new SpecialCardBudgetResponse { SpecialCardBudgetDatas = new List<SpecialCardBudgetData>() };
            budget.SpecialCardBudgetDatas ??= new List<SpecialCardBudgetData>();

            // assign to game
            game.SpecialCardBudgetResponse = budget;

            // optional bookkeeping
            game.TimeLastTurn = DateTime.UtcNow;
            return true;
        }
    }

    public bool TryApplyCrisisToGame(
        string salonId,
        string teamId,
        SpecialCardCrisisResponse crisis,
        out GameStateData game,
        out string error)
    {
        game = null;
        error = null;

        if (string.IsNullOrWhiteSpace(salonId) || string.IsNullOrWhiteSpace(teamId))
        {
            error = "invalid-ids";
            return false;
        }
        if (!TryGetTeam(salonId, teamId, out var big, out var team))
        {
            error = "not-found";
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

            crisis ??= new SpecialCardCrisisResponse();
            // if you want to trim whitespace:
            crisis.FirstCause = crisis.FirstCause?.Trim();
            crisis.SecondCause = crisis.SecondCause?.Trim();
            crisis.ThirdCause = crisis.ThirdCause?.Trim();
            crisis.FourthCause = crisis.FourthCause?.Trim();
            crisis.FifthCause = crisis.FifthCause?.Trim();

            game.SpecialCardCrisisResponse = crisis;

            game.TimeLastTurn = DateTime.UtcNow;
            return true;
        }
    }


    #endregion

    #region chat

    public bool TryGetChatRecipients(ChatDTO dto, out HashSet<int> recipients, out string error)
    {
        recipients = new HashSet<int>();
        error = null;

        if (dto == null || dto.Target == null)
        {
            error = "invalid-payload";
            return false;
        }


        switch (dto.Target.TypeChatTarget)
        {
            case TypeChatTarget.SALON:
                {
                    if (string.IsNullOrWhiteSpace(dto.Target.StringIdSalon))
                    {
                        error = "missing-salon";
                        return false;
                    }
                    recipients = GetSalonUserIds(dto.Target.StringIdSalon);
                    break;
                }

            case TypeChatTarget.LOBBY: // team chat
                {
                    if (string.IsNullOrWhiteSpace(dto.Target.StringIdSalon) ||
                        string.IsNullOrWhiteSpace(dto.Target.StringIdTeam))
                    {
                        error = "missing-salon-or-team";
                        return false;
                    }
                    recipients = GetTeamUserIds(dto.Target.StringIdSalon, dto.Target.StringIdTeam);
                    break;
                }

            case TypeChatTarget.ADMIN:
            case TypeChatTarget.OBSERVER:
            case TypeChatTarget.PLAYER:
                {
                    if (dto.Target.IdTarget == 0)
                    {
                        error = "missing-user-target";
                        return false;
                    }
                    recipients.Add(dto.Target.IdTarget);
                    break;
                }

            default:
                error = "unknown-target";
                return false;
        }

        // Optional: include sender echo so they see their own message
        if (dto.FromId != 0) recipients.Add(dto.FromId);

        return true;
    }

    public HashSet<int> GetSalonUserIds(string salonId)
    {
        var set = new HashSet<int>();
        if (!TryGetBigSalon(salonId, out var big))
            return set;

        lock (big)
        {
            foreach (var kv in big.Salons)
            {
         
                foreach (var u in kv.UsersInSalon ?? new List<UserInfo>())
                {
                    if (u.Id != 0)
                        set.Add(u.Id);
                }
            }
        }
        return set;
    }

    internal bool TryApplySharedMessage(
        string salonId,
        string teamId,
        string text,
        out string error)
    {
        GameStateData game = null;
        error = null;
      

        if (string.IsNullOrWhiteSpace(salonId) || string.IsNullOrWhiteSpace(teamId))
        {
            error = "invalid-ids";
            return false;
        }
        if (!TryGetTeam(salonId, teamId, out var big, out var team))
        {
            error = "not-found";
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

            game.SharedMessage = text;
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
