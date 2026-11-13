using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    public enum CoopPhase
    {
        /// <summary>대기실. 방장이 시작하기를 기다린다.</summary>
        Lobby,
        /// <summary>웨이브 사이의 쉬는 시간 (첫 웨이브 앞의 준비 시간 포함).</summary>
        Rest,
        Wave,
        Won,
        Lost,
    }

    /// <summary>한 판을 여는 데 필요한 것. 서버의 /internal/coop/sessions/{id}/config 응답에서 만든다.</summary>
    public sealed class CoopSimConfig
    {
        public StageDef zone;
        /// <summary>관문 내구도 (도시 공용 방어시설의 단계가 반영된 값).</summary>
        public int gateHp;
        public List<EnemyDef> enemies = new List<EnemyDef>();
        public List<TowerDef> structures = new List<TowerDef>();
        public List<CoopRoleDef> roles = new List<CoopRoleDef>();
        public CoopRules rules = new CoopRules();
        public PingRules ping = new PingRules();
        public CoopMap map;
        public int maxPlayers = 4;
    }

    public sealed class CoopPlayerState
    {
        public string pid;
        public string name;
        public string role;
        public int level = 1;
        public bool host;
        /// <summary>들어온 순서 (시작 자리를 정한다).</summary>
        public int order;
        public CoopVec pos;
        public CoopVec target;
        public bool connected;
        /// <summary>전투 중에 연결되어 있던 시간 (틱).</summary>
        public int connectedTicks;
        public int kills;
        public int towersBuilt;
        public int towersUpgraded;
        public int repairs;
        public long damageDealt;
        public int pingsSent;
        public int pingsRejected;
        /// <summary>이 사람이 세울 수 있는 타워 (입장권에 실려 온다).</summary>
        public HashSet<string> structs = new HashSet<string>();
        public int focusEnemyId;
        public float shotCooldown;
        /// <summary>NPC 도우미인가 (연습전). 실제 플레이어인 것처럼 보이지 않게 이름 앞에 [NPC] 가 붙는다.</summary>
        public bool npc;
    }

    public sealed class CoopEnemyState
    {
        public int id;
        public string code;
        public EnemyDef def;
        public int hp;
        public int maxHp;
        /// <summary>길을 따라 온 거리 (m).</summary>
        public float distance;
        public CoopVec pos;
        /// <summary>향하는 방향 (길이 1).</summary>
        public CoopVec dir;
        public int slowPercent;
        public float slowLeft;
        public float stunLeft;
        public float attackCooldown;
        /// <summary>공격하고 있는 타워의 자리. 없으면 -1.</summary>
        public int targetSlot = -1;
    }

    public sealed class CoopTowerState
    {
        public int slot;
        public string code;
        public TowerDef def;
        public int level;
        public int hp;
        public int maxHp;
        public float cooldown;
        public string ownerPid;
        public CoopVec pos;
    }

    public static class CoopEventTypes
    {
        public const string Started = "started";
        public const string WaveStarted = "wave_started";
        public const string WaveCleared = "wave_cleared";
        public const string TowerBuilt = "tower_built";
        public const string TowerUpgraded = "tower_upgraded";
        public const string TowerRepaired = "tower_repaired";
        public const string TowerLost = "tower_lost";
        public const string GateHit = "gate_hit";
        public const string Ping = "ping";
        public const string RoleChanged = "role_changed";
        public const string Finished = "finished";
    }

    /// <summary>모두에게 알릴 일. 실시간 서버가 틱마다 꺼내 보낸다.</summary>
    public sealed class CoopEvent
    {
        public string type;
        public string pid;
        public int slot = -1;
        public string code;
        public int value;
        public float x;
        public float z;
        public int id;
    }

    /// <summary>명령의 결과.</summary>
    public struct CoopOutcome
    {
        /// <summary>받아들이지 않은 이유. 받아들였으면 null.</summary>
        public string reject;
        /// <summary>같은 요청 번호가 다시 온 것이라 처음 결과를 돌려준 것인가.</summary>
        public bool duplicate;

        public bool Ok => reject == null;

        public static CoopOutcome Accept() => new CoopOutcome();
        public static CoopOutcome Reject(string reason) => new CoopOutcome { reject = reason };
    }

    [Serializable]
    public sealed class CoopMemberReport
    {
        public string pid;
        public string role;
        public int connectedSeconds;
        public int kills;
        public int towersBuilt;
        public int towersUpgraded;
        public int repairs;
        public long damageDealt;
        public int pingsSent;
        public int pingsRejected;
    }

    /// <summary>판이 끝났을 때 서버에 넘기는 결과와 기록.</summary>
    [Serializable]
    public sealed class CoopReport
    {
        public string result;
        public int wavesCleared;
        public int gateHpLeft;
        public int durationSeconds;
        public string closeReason;
        public List<CoopMemberReport> members = new List<CoopMemberReport>();
        public List<BudgetLine> budget = new List<BudgetLine>();
        public List<PingRecord> pings = new List<PingRecord>();
    }

    public sealed class CoopDangerZone
    {
        public CoopVec pos;
        /// <summary>그 구간에 있는 적의 수.</summary>
        public int enemies;
        /// <summary>관문 가까이까지 온 적 때문인가.</summary>
        public bool nearGate;
    }

    /// <summary>
    /// 협동 방어전 한 판의 시뮬레이션 (docs/COOP_DEFENSE_SYSTEM.md 4장).
    ///
    /// 적의 이동, 타워의 상태, 웨이브, 관문 내구도, 설치 결과, 공동 예산이 모두 여기에서 정해진다. 실시간 서버가 이 객체를
    /// 방마다 하나씩 갖고 한 줄로(한 번에 한 명령씩) 돌리며, 클라이언트는 결과만 받아 그린다. 클라이언트가 보낸 것은
    /// "어디로 가겠다", "여기에 세우겠다" 같은 의사뿐이고, 되는지 안 되는지는 여기에서 판정한다.
    ///
    /// 여러 스레드에서 동시에 부르면 안 된다. 같은 명령을 같은 순서로 넣으면 늘 같은 결과가 나온다 (난수를 쓰지 않는다).
    /// </summary>
    public sealed class CoopSim
    {
        public const float TickSeconds = 0.1f;
        /// <summary>1초의 틱 수. 시간은 틱을 세어 잰다 (소수를 더해 가면 어긋난다).</summary>
        public const int TicksPerSecond = 10;
        public const float PlayerSpeed = 6f;
        public const float RepairRangeM = 8f;
        public const float StartCountdownSeconds = 5f;
        /// <summary>전투 중에 아무도 연결되어 있지 않은 채로 이 시간이 지나면 판을 닫는다.</summary>
        public const float AbandonSeconds = 60f;
        private const float SlowLingerSeconds = 0.5f;
        private const float StructureReachM = 3f;
        private const int RememberedRequests = 256;

        private readonly CoopSimConfig config;
        private readonly CoopMap map;
        private readonly Dictionary<string, CoopPlayerState> players = new Dictionary<string, CoopPlayerState>();
        private readonly List<CoopPlayerState> playerList = new List<CoopPlayerState>();
        private readonly List<CoopEnemyState> enemies = new List<CoopEnemyState>();
        private readonly SortedDictionary<int, CoopTowerState> towers = new SortedDictionary<int, CoopTowerState>();
        private readonly List<CoopEvent> events = new List<CoopEvent>();
        private readonly Dictionary<string, Dictionary<string, string>> requests = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, Queue<string>> requestOrder = new Dictionary<string, Queue<string>>();
        private readonly Dictionary<string, EnemyDef> enemyDefs = new Dictionary<string, EnemyDef>();
        private readonly Dictionary<string, TowerDef> towerDefs = new Dictionary<string, TowerDef>();
        private int[] spawned = new int[0];
        private int nextEnemyId = 1;
        private float waveTime;
        private int restTicks;
        private int emptyTicks;
        private int waveTicks;
        private int pendingKillEnergy;
        /// <summary>방의 시계 (초). 대기실에서도 흐른다. 핑의 빈도 제한과 수명에 쓴다.</summary>
        private float clock;
        private int battleTicks;

        public SharedBudget Budget { get; } = new SharedBudget();
        public PingBoard Pings { get; }
        public CoopPhase Phase { get; private set; } = CoopPhase.Lobby;
        /// <summary>전투 시계 (초). 시작한 뒤부터 흐른다.</summary>
        public float Time { get; private set; }
        public long Tick { get; private set; }
        /// <summary>지금 웨이브 (1부터). 대기실에서는 0.</summary>
        public int Wave { get; private set; }
        public int WavesCleared { get; private set; }
        public int GateHp { get; private set; }
        public int GateMaxHp { get; }
        public int PlayersAtStart { get; private set; }
        public string CloseReason { get; private set; }
        public int TotalKills { get; private set; }

        public CoopMap Map => map;
        public CoopSimConfig Config => config;
        public IReadOnlyList<CoopPlayerState> Players => playerList;
        public IReadOnlyList<CoopEnemyState> Enemies => enemies;
        public IEnumerable<CoopTowerState> Towers => towers.Values;
        public int WaveCount => config.zone.waves.Count;
        public float RestLeft => Phase == CoopPhase.Rest ? restTicks * TickSeconds : 0f;
        public bool IsRunning => Phase == CoopPhase.Rest || Phase == CoopPhase.Wave;
        public bool IsFinished => Phase == CoopPhase.Won || Phase == CoopPhase.Lost;
        public int NowMs => battleTicks * (1000 / TicksPerSecond);
        /// <summary>방의 시계 (초). 핑이 사라지는 때를 이것과 견준다.</summary>
        public float Clock => clock;

        public CoopSim(CoopSimConfig config)
        {
            if (config == null || config.zone == null || config.zone.waves.Count == 0) throw new ArgumentException("방어 구역이 없습니다.");
            this.config = config;
            map = config.map ?? CoopMap.ForZone(config.zone.code);
            foreach (var def in config.enemies) enemyDefs[def.code] = def;
            foreach (var def in config.structures)
                if (def.IsBuildable) towerDefs[def.code] = def;
            GateMaxHp = Math.Max(1, config.gateHp > 0 ? config.gateHp : config.zone.baseHp);
            GateHp = GateMaxHp;
            Pings = new PingBoard(config.ping, map);
        }

        // ── 조회 ────────────────────────────────────────────

        public CoopPlayerState FindPlayer(string pid) => pid != null && players.TryGetValue(pid, out var player) ? player : null;

        public CoopTowerState TowerAt(int slot) => towers.TryGetValue(slot, out var tower) ? tower : null;

        public CoopRoleDef RoleOf(CoopPlayerState player) => CoopDefaults.FindRole(config.roles, player.role);

        public int ConnectedCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < playerList.Count; i++)
                    if (playerList[i].connected) count++;
                return count;
            }
        }

        /// <summary>사람 수에 따른 배율 (%): 100 + 한 명마다 perPlayer.</summary>
        private int Scale(int perPlayer) => 100 + Math.Max(0, perPlayer) * Math.Max(0, PlayersAtStart - 1);

        /// <summary>그 타워를 세우는 데 드는 공동 예산 (역할의 할인을 반영).</summary>
        public int BuildCost(CoopPlayerState player, TowerDef def) => Discounted(player, def.energyCost);

        public int UpgradeCost(CoopPlayerState player, TowerDef def) => Discounted(player, def.upgradeEnergyCost);

        private int Discounted(CoopPlayerState player, int cost)
        {
            var role = RoleOf(player);
            int discount = role != null ? Math.Max(0, Math.Min(90, role.buildDiscountPercent)) : 0;
            // 올림: 할인 때문에 0 이 되지는 않는다.
            return cost <= 0 ? 0 : (cost * (100 - discount) + 99) / 100;
        }

        public int RepairAmount(CoopPlayerState player)
        {
            var role = RoleOf(player);
            return Math.Max(1, config.rules.repairHp * (100 + (role != null ? role.repairBonusPercent : 0)) / 100);
        }

        public int PlayerDamage(CoopPlayerState player)
        {
            var role = RoleOf(player);
            long damage = (long)config.rules.playerDamage * (100 + config.rules.playerDamagePerLevelPercent * Math.Max(0, player.level - 1))
                * (100 + (role != null ? role.attackBonusPercent : 0));
            return (int)Math.Max(1, (damage + 5000) / 10000);
        }

        public int CountTowers(string code)
        {
            int count = 0;
            foreach (var tower in towers.Values)
                if (tower.code == code) count++;
            return count;
        }

        /// <summary>틱 사이에 쌓인 알림을 꺼낸다.</summary>
        public List<CoopEvent> DrainEvents()
        {
            var drained = new List<CoopEvent>(events);
            events.Clear();
            return drained;
        }

        private void Emit(string type, string pid = null, int slot = -1, string code = null, int value = 0)
        {
            events.Add(new CoopEvent { type = type, pid = pid, slot = slot, code = code, value = value });
        }

        // ── 들어오고 나가기 ─────────────────────────────────

        /// <summary>
        /// 팀원이 연결되었다 (입장권을 확인한 뒤에 부른다). 이미 있던 사람이면 다시 연결된 것으로 본다.
        /// 전투 중에 새로 들어오는 것은 설정이 허용할 때만 된다.
        /// </summary>
        public CoopOutcome Join(string pid, string name, string role, int level, bool host, IEnumerable<string> structs, bool pingBlocked,
            bool npc = false)
        {
            if (string.IsNullOrEmpty(pid)) return CoopOutcome.Reject(CoopRejects.BadRequest);
            var player = FindPlayer(pid);
            if (player == null)
            {
                if (IsFinished) return CoopOutcome.Reject(CoopRejects.NotRunning);
                if (IsRunning && config.rules.allowLateJoin != 1) return CoopOutcome.Reject(CoopRejects.LateJoinClosed);
                if (playerList.Count >= Math.Max(1, config.maxPlayers)) return CoopOutcome.Reject(CoopRejects.SessionFull);
                player = new CoopPlayerState { pid = pid, order = playerList.Count, npc = npc };
                player.pos = map.playerStarts[player.order % map.playerStarts.Length];
                player.target = player.pos;
                players[pid] = player;
                playerList.Add(player);
                // 역할은 처음 들어올 때에만 입장권의 것을 쓴다. 그 뒤로는 판 안에서 바꾼 것이 남는다.
                player.role = CoopDefaults.FindRole(config.roles, role) != null && RoleProblem(player, role) == null ? role : null;
            }
            player.name = string.IsNullOrEmpty(name) ? "수호대원" : name;
            player.level = Math.Max(1, level);
            player.host = host;
            player.connected = true;
            player.structs = new HashSet<string>(structs ?? new string[0]);
            Pings.SetBlocked(pid, pingBlocked, clock);
            ApplyRoleToPings(player);
            return CoopOutcome.Accept();
        }

        /// <summary>연결이 끊겼다. 전투는 그대로 진행된다. 다시 들어오면 이어서 한다.</summary>
        public void Disconnect(string pid)
        {
            var player = FindPlayer(pid);
            if (player != null) player.connected = false;
        }

        private string RoleProblem(CoopPlayerState player, string role)
        {
            if (string.IsNullOrEmpty(role)) return null;
            var def = CoopDefaults.FindRole(config.roles, role);
            if (def == null) return CoopRejects.BadRole;
            if (def.maxPerSession > 0)
            {
                int taken = 0;
                for (int i = 0; i < playerList.Count; i++)
                    if (playerList[i] != player && playerList[i].role == role) taken++;
                if (taken >= def.maxPerSession) return CoopRejects.RoleTaken;
            }
            return null;
        }

        private void ApplyRoleToPings(CoopPlayerState player)
        {
            var role = RoleOf(player);
            Pings.SetCooldownPercent(player.pid, role != null ? role.pingCooldownPercent : 0, clock);
        }

        /// <summary>역할을 고르거나 내려놓는다 (null). 강제가 아니고, 전투 중에도 바꿀 수 있다.</summary>
        public CoopOutcome SetRole(string pid, string role)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            if (IsFinished) return CoopOutcome.Reject(CoopRejects.NotRunning);
            if (string.IsNullOrEmpty(role)) role = null;
            string problem = RoleProblem(player, role);
            if (problem != null) return CoopOutcome.Reject(problem);
            if (player.role != role)
            {
                player.role = role;
                ApplyRoleToPings(player);
                Emit(CoopEventTypes.RoleChanged, pid, -1, role);
            }
            return CoopOutcome.Accept();
        }

        /// <summary>방장이 전투를 시작한다. 준비 시간이 지나면 첫 웨이브가 나온다.</summary>
        public CoopOutcome Start(string pid)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            if (Phase != CoopPhase.Lobby) return CoopOutcome.Reject(CoopRejects.NotLobby);
            if (!player.host) return CoopOutcome.Reject(CoopRejects.NotHost);
            if (ConnectedCount < Math.Max(1, config.rules.minPlayersToStart)) return CoopOutcome.Reject(CoopRejects.NotEnoughPlayers);
            PlayersAtStart = ConnectedCount;
            Phase = CoopPhase.Rest;
            restTicks = (int)Math.Round(StartCountdownSeconds * TicksPerSecond);
            Budget.Grant(CoopBudgetKinds.GrantStart, config.zone.startEnergy * Scale(config.rules.energyScalePercentPerPlayer) / 100, NowMs);
            Emit(CoopEventTypes.Started, pid, -1, null, PlayersAtStart);
            return CoopOutcome.Accept();
        }

        // ── 플레이어의 명령 ─────────────────────────────────

        /// <summary>가고 싶은 곳을 정한다. 실제 위치는 틱마다 정해진 속도로만 움직인다 (순간 이동이 되지 않는다).</summary>
        public CoopOutcome Move(string pid, float x, float z)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            player.target = map.Clamp(new CoopVec(x, z));
            return CoopOutcome.Accept();
        }

        /// <summary>먼저 쏠 적을 고른다. 0 이면 가까운 적을 쏜다.</summary>
        public CoopOutcome Focus(string pid, int enemyId)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            player.focusEnemyId = Math.Max(0, enemyId);
            return CoopOutcome.Accept();
        }

        private static bool ValidRequestId(string requestId)
        {
            if (string.IsNullOrEmpty(requestId) || requestId.Length > 40) return false;
            for (int i = 0; i < requestId.Length; i++)
            {
                char c = requestId[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            }
            return true;
        }

        /// <summary>
        /// 예산을 쓰는 명령을 요청 번호와 함께 처리한다. 같은 사람이 같은 요청 번호를 다시 보내면 (다시 보내기, 두 번 누르기)
        /// 다시 실행하지 않고 처음 결과를 돌려준다.
        /// </summary>
        private CoopOutcome Once(CoopPlayerState player, string requestId, Func<string> work)
        {
            if (!ValidRequestId(requestId)) return CoopOutcome.Reject(CoopRejects.BadRequest);
            if (!requests.TryGetValue(player.pid, out var seen))
            {
                seen = new Dictionary<string, string>();
                requests[player.pid] = seen;
                requestOrder[player.pid] = new Queue<string>();
            }
            if (seen.TryGetValue(requestId, out var previous)) return new CoopOutcome { reject = previous.Length == 0 ? null : previous, duplicate = true };
            string reject = work();
            seen[requestId] = reject ?? "";
            var order = requestOrder[player.pid];
            order.Enqueue(requestId);
            while (order.Count > RememberedRequests) seen.Remove(order.Dequeue());
            return new CoopOutcome { reject = reject };
        }

        /// <summary>
        /// 타워를 세운다. 한 자리에는 하나만 세워진다: 두 사람이 같은 자리에 동시에 세우려 하면 먼저 처리된 쪽만 되고,
        /// 다른 쪽은 예산이 빠지지 않은 채 slot_taken 으로 거절된다.
        /// </summary>
        public CoopOutcome Build(string pid, string requestId, int slot, string code)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            return Once(player, requestId, () =>
            {
                if (!IsRunning) return CoopRejects.NotRunning;
                if (!map.IsSlot(slot)) return CoopRejects.BadSlot;
                if (code == null || !towerDefs.TryGetValue(code, out var def)) return CoopRejects.BadStructure;
                if (!player.structs.Contains(code)) return CoopRejects.NotUnlocked;
                if (towers.ContainsKey(slot)) return CoopRejects.SlotTaken;
                if (def.maxPerBattle > 0 && CountTowers(code) >= def.maxPerBattle) return CoopRejects.TooMany;
                if (!Budget.TrySpend(pid, CoopBudgetKinds.Build, BuildCost(player, def), slot, code, NowMs, out _)) return CoopRejects.NoBudget;
                towers[slot] = new CoopTowerState
                {
                    slot = slot, code = code, def = def, level = 0, hp = Math.Max(1, def.maxHp), maxHp = Math.Max(1, def.maxHp),
                    ownerPid = pid, pos = map.slots[slot],
                };
                player.towersBuilt++;
                Emit(CoopEventTypes.TowerBuilt, pid, slot, code);
                return null;
            });
        }

        public CoopOutcome Upgrade(string pid, string requestId, int slot)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            return Once(player, requestId, () =>
            {
                if (!IsRunning) return CoopRejects.NotRunning;
                if (!map.IsSlot(slot)) return CoopRejects.BadSlot;
                var tower = TowerAt(slot);
                if (tower == null) return CoopRejects.SlotEmpty;
                if (tower.level >= tower.def.maxUpgradeLevel) return CoopRejects.MaxLevel;
                if (!Budget.TrySpend(pid, CoopBudgetKinds.Upgrade, UpgradeCost(player, tower.def), slot, tower.code, NowMs, out _))
                    return CoopRejects.NoBudget;
                tower.level++;
                player.towersUpgraded++;
                Emit(CoopEventTypes.TowerUpgraded, pid, slot, tower.code, tower.level);
                return null;
            });
        }

        /// <summary>타워를 고친다. 그 타워 가까이에 있어야 한다.</summary>
        public CoopOutcome Repair(string pid, string requestId, int slot)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            return Once(player, requestId, () =>
            {
                if (!IsRunning) return CoopRejects.NotRunning;
                if (!map.IsSlot(slot)) return CoopRejects.BadSlot;
                var tower = TowerAt(slot);
                if (tower == null) return CoopRejects.SlotEmpty;
                if (tower.hp >= tower.maxHp) return CoopRejects.NotDamaged;
                if (CoopVec.Distance(player.pos, tower.pos) > RepairRangeM) return CoopRejects.TooFar;
                if (!Budget.TrySpend(pid, CoopBudgetKinds.Repair, Math.Max(0, config.rules.repairEnergy), slot, tower.code, NowMs, out _))
                    return CoopRejects.NoBudget;
                int before = tower.hp;
                tower.hp = Math.Min(tower.maxHp, tower.hp + RepairAmount(player));
                player.repairs++;
                Emit(CoopEventTypes.TowerRepaired, pid, slot, tower.code, tower.hp - before);
                return null;
            });
        }

        /// <summary>전술 핑을 보낸다. 빈도 제한과 차단은 PingBoard 가 판정한다.</summary>
        public CoopOutcome Ping(string pid, string kind, float x, float z)
        {
            var player = FindPlayer(pid);
            if (player == null) return CoopOutcome.Reject(CoopRejects.NotMember);
            if (IsFinished) return CoopOutcome.Reject(CoopRejects.NotRunning);
            string reject = Pings.Send(pid, kind, new CoopVec(x, z), clock, out var ping, NowMs);
            if (reject == CoopRejects.PingKind) return CoopOutcome.Reject(reject);
            player.pingsSent++;
            if (reject != null)
            {
                player.pingsRejected++;
                return CoopOutcome.Reject(reject);
            }
            events.Add(new CoopEvent { type = CoopEventTypes.Ping, pid = pid, code = kind, x = ping.pos.x, z = ping.pos.z, id = ping.id });
            return CoopOutcome.Accept();
        }

        // ── 틱 ──────────────────────────────────────────────

        /// <summary>정해진 간격(TickSeconds)만큼 시간을 보낸다.</summary>
        public void Step()
        {
            Tick++;
            float dt = TickSeconds;
            clock = Tick * TickSeconds;
            MovePlayers(dt);
            if (!IsRunning)
            {
                // 대기실에서도 핑은 시간이 지나면 사라진다.
                Pings.Expire(clock);
                return;
            }
            battleTicks++;
            Time = battleTicks * TickSeconds;
            for (int i = 0; i < playerList.Count; i++)
                if (playerList[i].connected) playerList[i].connectedTicks++;

            if (Phase == CoopPhase.Rest)
            {
                restTicks--;
                if (restTicks <= 0) StartWave();
            }
            else
            {
                waveTicks++;
                waveTime = waveTicks * TickSeconds;
                Spawn();
            }
            StepEnemies(dt);
            if (Phase == CoopPhase.Lost) return;
            StepTowers(dt);
            StepPlayerAttacks(dt);
            if (pendingKillEnergy > 0)
            {
                Budget.Grant(CoopBudgetKinds.GrantKill, pendingKillEnergy, NowMs);
                pendingKillEnergy = 0;
            }
            if (Phase == CoopPhase.Wave && AllSpawned() && enemies.Count == 0) ClearWave();
            Pings.Expire(clock);

            // 모두 나간 채로 시간이 지나면 닫는다.
            emptyTicks = ConnectedCount == 0 ? emptyTicks + 1 : 0;
            if (IsRunning && emptyTicks >= (int)Math.Round(AbandonSeconds * TicksPerSecond)) Abort(CoopCloseReasons.AllLeft);
        }

        /// <summary>여러 틱을 한꺼번에 보낸다 (테스트와 연습전).</summary>
        public void Run(float seconds)
        {
            int steps = (int)Math.Round(seconds / TickSeconds);
            for (int i = 0; i < steps && !IsFinished; i++) Step();
        }

        private void MovePlayers(float dt)
        {
            for (int i = 0; i < playerList.Count; i++)
            {
                var player = playerList[i];
                player.pos = CoopVec.MoveTowards(player.pos, player.target, PlayerSpeed * dt);
            }
        }

        private void StartWave()
        {
            Wave++;
            Phase = CoopPhase.Wave;
            waveTicks = 0;
            waveTime = 0f;
            spawned = new int[config.zone.waves[Wave - 1].groups.Count];
            Emit(CoopEventTypes.WaveStarted, null, -1, null, Wave);
            Spawn();
        }

        private bool AllSpawned()
        {
            var groups = config.zone.waves[Wave - 1].groups;
            for (int g = 0; g < groups.Count; g++)
                if (spawned[g] < groups[g].count) return false;
            return true;
        }

        private void Spawn()
        {
            var groups = config.zone.waves[Wave - 1].groups;
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                while (spawned[g] < group.count && waveTime + 0.0001f >= group.delay + spawned[g] * Math.Max(0f, group.interval))
                {
                    spawned[g]++;
                    if (!enemyDefs.TryGetValue(group.enemyCode ?? "", out var def)) continue;   // 모르는 적은 건너뛴다
                    int hp = Math.Max(1, def.maxHp * Scale(config.rules.hpScalePercentPerPlayer) / 100);
                    enemies.Add(new CoopEnemyState
                    {
                        id = nextEnemyId++, code = def.code, def = def, hp = hp, maxHp = hp, distance = 0f,
                        pos = map.PointAt(0f), dir = map.DirectionAt(0f),
                    });
                }
            }
        }

        private void ClearWave()
        {
            WavesCleared = Wave;
            Emit(CoopEventTypes.WaveCleared, null, -1, null, Wave);
            if (Wave >= WaveCount)
            {
                Finish(CoopPhase.Won, CoopCloseReasons.Completed);
                return;
            }
            Budget.Grant(CoopBudgetKinds.GrantWave, config.zone.waveClearEnergy * Scale(config.rules.energyScalePercentPerPlayer) / 100, NowMs);
            Phase = CoopPhase.Rest;
            restTicks = (int)Math.Round(Math.Max(1f, config.zone.restSeconds) * TicksPerSecond);
        }

        private void Finish(CoopPhase phase, string reason)
        {
            if (IsFinished) return;
            Phase = phase;
            CloseReason = reason;
            Emit(CoopEventTypes.Finished, null, -1, reason, phase == CoopPhase.Won ? 1 : 0);
        }

        /// <summary>판을 중간에 닫는다 (모두 나감, 시간 초과, 서버 종료). 진 것으로 기록된다.</summary>
        public void Abort(string reason)
        {
            if (!IsRunning) return;
            Finish(CoopPhase.Lost, reason);
        }

        private void StepEnemies(float dt)
        {
            for (int i = enemies.Count - 1; i >= 0; i--)
            {
                var enemy = enemies[i];
                if (enemy.slowLeft > 0f)
                {
                    enemy.slowLeft -= dt;
                    if (enemy.slowLeft <= 0f) enemy.slowPercent = 0;
                }
                if (enemy.stunLeft > 0f)
                {
                    enemy.stunLeft -= dt;
                    continue;
                }
                // 건설 메카 같은 적은 길 가까이의 타워를 먼저 부순다.
                if (enemy.def.attacksStructures)
                {
                    var target = enemy.targetSlot >= 0 ? TowerAt(enemy.targetSlot) : null;
                    if (target == null)
                    {
                        enemy.targetSlot = -1;
                        float best = enemy.def.attackRange + StructureReachM;
                        foreach (var tower in towers.Values)
                        {
                            float distance = CoopVec.Distance(enemy.pos, tower.pos);
                            if (distance <= best)
                            {
                                best = distance;
                                target = tower;
                            }
                        }
                        if (target != null)
                        {
                            enemy.targetSlot = target.slot;
                            enemy.attackCooldown = Math.Max(0.1f, enemy.def.attackWindup);
                        }
                    }
                    if (target != null)
                    {
                        enemy.attackCooldown -= dt;
                        if (enemy.attackCooldown <= 0f)
                        {
                            enemy.attackCooldown = Math.Max(0.2f, enemy.def.attackInterval);
                            target.hp -= enemy.def.structureDamage > 0 ? enemy.def.structureDamage : enemy.def.damage;
                            if (target.hp <= 0)
                            {
                                towers.Remove(target.slot);
                                Budget.Lost(target.slot, target.code, NowMs);
                                Emit(CoopEventTypes.TowerLost, null, target.slot, target.code);
                                enemy.targetSlot = -1;
                            }
                        }
                        continue;
                    }
                }
                enemy.distance += enemy.def.moveSpeed * (100 - Math.Min(90, enemy.slowPercent)) / 100f * dt;
                if (enemy.distance >= map.PathLength)
                {
                    enemies.RemoveAt(i);
                    GateHp = Math.Max(0, GateHp - Math.Max(1, enemy.def.baseDamage));
                    Emit(CoopEventTypes.GateHit, null, -1, enemy.code, GateHp);
                    if (GateHp <= 0)
                    {
                        Finish(CoopPhase.Lost, CoopCloseReasons.GateDestroyed);
                        return;
                    }
                    continue;
                }
                enemy.pos = map.PointAt(enemy.distance);
                enemy.dir = map.DirectionAt(enemy.distance);
            }
        }

        /// <summary>적에게 피해를 준다. 쓰러뜨리면 그 사람의 처치로 세고, 공동 예산에 들어갈 에너지를 모아 둔다.</summary>
        private void Damage(CoopEnemyState enemy, int amount, string sourcePid, bool byPlayer)
        {
            if (enemy.hp <= 0) return;
            int dealt = Math.Min(enemy.hp, Math.Max(0, amount));
            enemy.hp -= dealt;
            var source = FindPlayer(sourcePid);
            if (source != null && byPlayer) source.damageDealt += dealt;
            if (enemy.hp > 0) return;
            enemies.Remove(enemy);
            TotalKills++;
            if (source != null) source.kills++;
            pendingKillEnergy += Math.Max(0, enemy.def.rewardEnergy);
        }

        /// <summary>사거리 안에서 관문에 가장 가까이 간 적.</summary>
        private CoopEnemyState FrontEnemyInRange(CoopVec from, float range)
        {
            CoopEnemyState best = null;
            float rangeSquared = range * range;
            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                if (CoopVec.DistanceSquared(from, enemy.pos) > rangeSquared) continue;
                if (best == null || enemy.distance > best.distance || (enemy.distance == best.distance && enemy.id < best.id)) best = enemy;
            }
            return best;
        }

        private void StepTowers(float dt)
        {
            foreach (var tower in towers.Values)
            {
                var def = tower.def;
                float range = TowerMath.Range(def, tower.level);
                if (def.kind == StructureKinds.Slow)
                {
                    // 감속 장치: 사거리 안의 적이 느려진다. 강화하면 사거리가 는다.
                    float rangeSquared = range * range;
                    for (int i = 0; i < enemies.Count; i++)
                    {
                        var enemy = enemies[i];
                        if (CoopVec.DistanceSquared(tower.pos, enemy.pos) > rangeSquared) continue;
                        enemy.slowPercent = Math.Max(enemy.slowPercent, def.effectValue);
                        enemy.slowLeft = SlowLingerSeconds;
                    }
                    continue;
                }
                if (tower.cooldown > 0f) tower.cooldown -= dt;
                if (tower.cooldown > 0f || def.shotsPerSecond <= 0f) continue;
                if (def.kind == StructureKinds.Emp)
                {
                    // EMP 타워: 사거리 안의 적이 잠깐 멈춘다. 중간 보스는 멈추지 않는다.
                    bool fired = false;
                    float rangeSquared = range * range;
                    for (int i = 0; i < enemies.Count; i++)
                    {
                        var enemy = enemies[i];
                        if (enemy.def.staggerImmune || CoopVec.DistanceSquared(tower.pos, enemy.pos) > rangeSquared) continue;
                        enemy.stunLeft = Math.Max(enemy.stunLeft, def.effectSeconds * (1f + 0.25f * tower.level));
                        fired = true;
                    }
                    if (fired) tower.cooldown = 1f / def.shotsPerSecond;
                    continue;
                }
                if (def.damage <= 0) continue;
                var target = FrontEnemyInRange(tower.pos, range);
                if (target == null) continue;
                tower.cooldown = 1f / def.shotsPerSecond;
                int damage = TowerMath.Damage(def, tower.level);
                if (def.splashRadius > 0f)
                {
                    var center = target.pos;
                    float radiusSquared = def.splashRadius * def.splashRadius;
                    for (int i = enemies.Count - 1; i >= 0; i--)
                        if (CoopVec.DistanceSquared(center, enemies[i].pos) <= radiusSquared) Damage(enemies[i], damage, tower.ownerPid, false);
                }
                else
                {
                    Damage(target, damage, tower.ownerPid, false);
                }
            }
        }

        private void StepPlayerAttacks(float dt)
        {
            float range = config.rules.playerRangeM;
            float interval = 10f / Math.Max(1, config.rules.playerShotsPer10s);
            for (int p = 0; p < playerList.Count; p++)
            {
                var player = playerList[p];
                if (player.shotCooldown > 0f) player.shotCooldown -= dt;
                // 연결이 끊긴 사람은 쏘지 않는다.
                if (!player.connected || player.shotCooldown > 0f) continue;
                CoopEnemyState target = null;
                if (player.focusEnemyId > 0)
                {
                    for (int i = 0; i < enemies.Count; i++)
                        if (enemies[i].id == player.focusEnemyId && CoopVec.Distance(player.pos, enemies[i].pos) <= range) target = enemies[i];
                }
                if (target == null)
                {
                    float best = range * range;
                    for (int i = 0; i < enemies.Count; i++)
                    {
                        float distance = CoopVec.DistanceSquared(player.pos, enemies[i].pos);
                        if (distance <= best && (target == null || distance < best || enemies[i].id < target.id))
                        {
                            best = distance;
                            target = enemies[i];
                        }
                    }
                }
                if (target == null) continue;
                player.shotCooldown = interval;
                Damage(target, PlayerDamage(player), player.pid, true);
            }
        }

        // ── 미니맵에 쓸 것 ──────────────────────────────────

        /// <summary>
        /// 위험 지역: 관문 가까이(길의 마지막 30%)까지 온 적의 무리, 그리고 길의 한 구간에 적이 여섯 넘게 몰린 곳.
        /// </summary>
        public List<CoopDangerZone> DangerZones()
        {
            var zones = new List<CoopDangerZone>();
            if (enemies.Count == 0) return zones;
            const int sections = 6;
            float length = map.PathLength;
            var count = new int[sections];
            var sumX = new float[sections];
            var sumZ = new float[sections];
            int nearCount = 0;
            float nearX = 0f, nearZ = 0f;
            for (int i = 0; i < enemies.Count; i++)
            {
                var enemy = enemies[i];
                int weight = enemy.def.staggerImmune ? 6 : 1;      // 중간 보스는 혼자서도 위험 지역이다
                int section = Math.Min(sections - 1, (int)(enemy.distance / length * sections));
                count[section] += weight;
                sumX[section] += enemy.pos.x * weight;
                sumZ[section] += enemy.pos.z * weight;
                if (enemy.distance >= length * 0.7f)
                {
                    nearCount++;
                    nearX += enemy.pos.x;
                    nearZ += enemy.pos.z;
                }
            }
            if (nearCount > 0)
                zones.Add(new CoopDangerZone { pos = new CoopVec(nearX / nearCount, nearZ / nearCount), enemies = nearCount, nearGate = true });
            for (int s = sections - 1; s >= 0; s--)
            {
                if (count[s] < 6) continue;
                // 관문 앞의 무리는 이미 넣었다.
                if (nearCount > 0 && (s + 1f) / sections > 0.7f) continue;
                zones.Add(new CoopDangerZone { pos = new CoopVec(sumX[s] / count[s], sumZ[s] / count[s]), enemies = count[s] });
            }
            return zones;
        }

        // ── 결과 ────────────────────────────────────────────

        /// <summary>판이 끝난 뒤 서버에 넘길 결과와 기록. 보상 금액은 여기에 없다 (서버가 방어 구역 정의에서 계산한다).</summary>
        public CoopReport BuildReport()
        {
            var report = new CoopReport
            {
                result = Phase == CoopPhase.Won ? "win" : "lose",
                wavesCleared = WavesCleared,
                gateHpLeft = GateHp,
                durationSeconds = battleTicks / TicksPerSecond,
                closeReason = CloseReason ?? CoopCloseReasons.Shutdown,
            };
            for (int i = 0; i < playerList.Count; i++)
            {
                var player = playerList[i];
                if (player.npc) continue;       // NPC 도우미는 보상 대상이 아니고 기록에도 넣지 않는다
                report.members.Add(new CoopMemberReport
                {
                    pid = player.pid, role = player.role, connectedSeconds = player.connectedTicks / TicksPerSecond,
                    kills = player.kills, towersBuilt = player.towersBuilt, towersUpgraded = player.towersUpgraded, repairs = player.repairs,
                    damageDealt = player.damageDealt, pingsSent = player.pingsSent, pingsRejected = player.pingsRejected,
                });
            }
            report.budget.AddRange(Budget.Lines);
            report.pings.AddRange(Pings.Records);
            return report;
        }
    }
}
