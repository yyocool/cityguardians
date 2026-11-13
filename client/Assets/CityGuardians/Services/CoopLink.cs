using System;
using System.Collections.Generic;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    // 협동 방어전 화면과 전투를 잇는 창구 (docs/COOP_DEFENSE_SYSTEM.md 7장).
    //
    //  - 온라인: RemoteCoopLink 가 실시간 서버(WebSocket)와 이야기한다. 전투의 권한은 서버에 있다.
    //    화면은 서버가 보낸 상태를 그리고, "무엇을 하겠다"는 요청만 보낸다.
    //  - 연습전: LocalCoopLink 가 같은 규칙(CoopSim)을 기기 안에서 돌린다. NPC 대원과 함께 해 보는 것이고,
    //    보상도 기록도 없다. NPC 대원은 이름에 [NPC] 가 붙는다.
    //
    // 화면은 어느 쪽인지 몰라도 된다. 이 파일은 Unity 를 쓰지 않는다 (테스트가 실제 실시간 서버에 붙여 본다).

    public static class CoopLinkStates
    {
        public const string Connecting = "connecting";
        public const string Lobby = "lobby";
        public const string Battle = "battle";
        public const string Finished = "finished";
        /// <summary>판이 닫혔거나 연결이 끊겼다. 이유는 ClosedReason.</summary>
        public const string Closed = "closed";
    }

    public sealed class CoopStructureInfo
    {
        public TowerDef def;
        /// <summary>내가 해금해서 세울 수 있는가.</summary>
        public bool usable;
    }

    /// <summary>요청(설치·강화·수리·핑·역할·시작)에 대한 답.</summary>
    public sealed class CoopActionResult
    {
        public string action;
        public string requestId;
        public bool ok;
        /// <summary>CoopRejects. 받아들였으면 null.</summary>
        public string reject;
        public bool duplicate;
        /// <summary>처리한 뒤의 공동 예산. 모르면 -1.</summary>
        public int energy = -1;
    }

    public sealed class CoopFinish
    {
        /// <summary>"win" 또는 "lose".</summary>
        public string result;
        public int wavesCleared;
        public int gateHpLeft;
        public int durationSeconds;
        public string closeReason;
        /// <summary>연습전인가. 연습전에는 보상도 기록도 없다.</summary>
        public bool practice;
        /// <summary>게임 서버가 결과를 받았는가 (온라인). 보상은 게임 서버에서 확인한다.</summary>
        public bool recordKnown;
        public bool recorded;
        public bool accepted;
        /// <summary>지금은 보내지 못했고 나중에 다시 보낸다.</summary>
        public bool pending;

        public bool Won => result == "win";
    }

    public interface ICoopLink
    {
        bool Practice { get; }
        /// <summary>CoopLinkStates.</summary>
        string State { get; }
        string ClosedReason { get; }
        string MyPid { get; }
        string HostPid { get; }
        string ZoneName { get; }
        string RegionName { get; }
        int MaxPlayers { get; }
        int MinPlayersToStart { get; }
        int RepairEnergy { get; }
        float RepairRangeM { get; }
        /// <summary>대원의 사격이 닿는 거리 (m). 쏘는 것도 서버가 정한다. 화면은 이 거리 안의 적을 겨누는 모습만 그린다.</summary>
        float PlayerRangeM { get; }
        /// <summary>지도. 연결되기 전에는 null.</summary>
        CoopMap Map { get; }
        IReadOnlyList<CoopStructureInfo> Structures { get; }
        IReadOnlyList<CoopRoleDef> Roles { get; }
        PingRules PingRules { get; }
        /// <summary>관리자가 이 계정의 핑을 막았는가.</summary>
        bool PingBlocked { get; }
        /// <summary>지금의 상태. 연결되기 전에는 null.</summary>
        CoopSnapshot Snapshot { get; }
        /// <summary>끝났으면 그 결과.</summary>
        CoopFinish Finish { get; }

        /// <summary>매 프레임 부른다.</summary>
        void Update(float dt);

        void Start();
        /// <summary>가고 싶은 곳. 실제 위치는 서버가 정한 속도로만 움직인다.</summary>
        void Move(float x, float z);
        void Focus(int enemyId);
        /// <returns>요청 번호. 답(DrainResults)에서 같은 번호로 찾는다.</returns>
        string Build(int slot, string code);
        string Upgrade(int slot);
        string Repair(int slot);
        void Ping(string kind, float x, float z);
        void HidePings(string pid, bool hide);
        bool IsHidden(string pid);
        void SetRole(string role);
        void Leave();

        List<CoopEvent> DrainEvents();
        List<CoopActionResult> DrainResults();
    }

    /// <summary>화면에 보여 줄 말.</summary>
    public static class CoopTexts
    {
        public static string Reject(string reason)
        {
            switch (reason)
            {
                case CoopRejects.NoBudget: return "공동 예산이 모자라요.";
                case CoopRejects.SlotTaken: return "다른 대원이 먼저 세웠어요.";
                case CoopRejects.SlotEmpty: return "타워가 없는 자리예요.";
                case CoopRejects.NotUnlocked: return "아직 해금하지 않은 타워예요.";
                case CoopRejects.TooMany: return "이 타워는 더 세울 수 없어요.";
                case CoopRejects.MaxLevel: return "더 강화할 수 없어요.";
                case CoopRejects.NotDamaged: return "고칠 곳이 없어요.";
                case CoopRejects.TooFar: return "타워 가까이 가야 고칠 수 있어요.";
                case CoopRejects.NotHost: return "방장만 시작할 수 있어요.";
                case CoopRejects.NotEnoughPlayers: return "대원이 더 모여야 시작할 수 있어요.";
                case CoopRejects.NotLobby: return "이미 시작했어요.";
                case CoopRejects.NotRunning: return "지금은 할 수 없어요.";
                case CoopRejects.RoleTaken: return "다른 대원이 이미 맡은 역할이에요.";
                case CoopRejects.BadRole: return "없는 역할이에요.";
                case CoopRejects.PingDisabled: return "지금은 전술 핑을 쓸 수 없어요.";
                case CoopRejects.PingBlocked: return "전술 핑 사용이 제한된 계정이에요.";
                case CoopRejects.PingMuted: return "핑을 너무 자주 보내서 잠시 쉬어야 해요.";
                case CoopRejects.PingRate: return "핑은 조금 있다가 다시 보낼 수 있어요.";
                case "maintenance": return "서버 점검 중이에요. 잠시 뒤에 다시 해 주세요.";
                default: return "할 수 없는 일이에요.";
            }
        }

        public static string Closed(string reason)
        {
            switch (reason)
            {
                case "bad_ticket": return "입장권이 만료되었어요. 다시 들어와 주세요.";
                case "session_not_found":
                case "session_closed": return "이미 닫힌 방어전이에요.";
                case "session_full": return "자리가 없어요.";
                case "session_interrupted": return "서버가 다시 시작되어 이 방어전은 무효가 되었어요. 보상 횟수는 줄지 않아요.";
                case "lobby_timeout": return "대기 시간이 지나 방이 닫혔어요.";
                case "shutdown": return "서버 점검으로 방이 닫혔어요.";
                case "server_full": return "지금은 서버에 자리가 없어요.";
                case "replaced": return "다른 기기에서 접속했어요.";
                case "flood":
                case "slow": return "연결 상태가 좋지 않아 끊겼어요.";
                case "left": return "방어전에서 나왔어요.";
                default: return "연결이 끊겼어요.";
            }
        }

        public static string RoleName(IReadOnlyList<CoopRoleDef> roles, string code)
        {
            if (string.IsNullOrEmpty(code) || roles == null) return "역할 없음";
            for (int i = 0; i < roles.Count; i++)
                if (roles[i].code == code) return roles[i].name;
            return code;
        }
    }

    /// <summary>연습전의 설정을 게임 정의에서 만든다. 서버의 기본 설정과 같은 값이다.</summary>
    public static class CoopPractice
    {
        public const string MyPid = "p_practice_me";
        public const string NpcTag = "[NPC]";

        public static CoopSimConfig Config(GameDefs defs, int players)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            StageDef zone = defs.FindStage(StageCodes.Outskirts1);
            if (zone == null) throw new InvalidOperationException("방어 구역 정의가 없습니다.");
            var config = new CoopSimConfig
            {
                zone = zone, gateHp = zone.baseHp, roles = CoopDefaults.Roles(), rules = new CoopRules(), ping = new PingRules(),
                maxPlayers = Math.Max(1, Math.Min(8, players)),
            };
            // 연습전은 혼자서도 시작할 수 있다 (NPC 대원이 함께 들어온다).
            config.rules.minPlayersToStart = 1;
            config.enemies.AddRange(defs.enemies);
            for (int i = 0; i < defs.towers.Count; i++)
                if (defs.towers[i].IsBuildable) config.structures.Add(defs.towers[i]);
            return config;
        }
    }

    /// <summary>
    /// 연습전. 같은 규칙을 기기 안에서 돌린다. 서버와 주고받는 것이 없으므로 보상도, 도시 위험도 반영도, 기록도 없다.
    /// </summary>
    public sealed class LocalCoopLink : ICoopLink
    {
        private const int MaxStepsPerUpdate = 5;
        /// <summary>NPC 설치 담당이 세우는 타워의 한도. 나머지 자리와 예산은 플레이어의 몫이다.</summary>
        private const int HelperTowerLimit = 4;
        private static readonly int[] HelperSlots = { 9, 13, 6, 15 };
        private static readonly float[] HelperPosts = { 0.45f, 0.70f, 0.90f };

        private readonly CoopSim _sim;
        private readonly List<CoopStructureInfo> _structures = new List<CoopStructureInfo>();
        private readonly List<CoopEvent> _events = new List<CoopEvent>();
        private readonly List<CoopActionResult> _results = new List<CoopActionResult>();
        private readonly List<string> _helpers = new List<string>();
        private float _carry;
        private int _nextRequest = 1;
        private int _helperTowers;
        private bool _left;

        public CoopSim Sim => _sim;
        public bool Practice => true;
        public string State { get; private set; } = CoopLinkStates.Lobby;
        public string ClosedReason { get; private set; }
        public string MyPid => CoopPractice.MyPid;
        public string HostPid => CoopPractice.MyPid;
        public string ZoneName => _sim.Config.zone.name;
        public string RegionName => "연습 구역";
        public int MaxPlayers => _sim.Config.maxPlayers;
        public int MinPlayersToStart => _sim.Config.rules.minPlayersToStart;
        public int RepairEnergy => _sim.Config.rules.repairEnergy;
        public float RepairRangeM => CoopSim.RepairRangeM;
        public float PlayerRangeM => _sim.Config.rules.playerRangeM;
        public CoopMap Map => _sim.Map;
        public IReadOnlyList<CoopStructureInfo> Structures => _structures;
        public IReadOnlyList<CoopRoleDef> Roles => _sim.Config.roles;
        public PingRules PingRules => _sim.Config.ping;
        public bool PingBlocked => false;
        public CoopSnapshot Snapshot { get; private set; }
        public CoopFinish Finish { get; private set; }

        /// <param name="unlocked">내가 해금한 타워 (처음부터 쓸 수 있는 것 포함).</param>
        /// <param name="helpers">함께할 NPC 대원 수 (0~3).</param>
        public LocalCoopLink(CoopSimConfig config, string myName, int myLevel, IEnumerable<string> unlocked, int helpers = 3)
        {
            helpers = Math.Max(0, Math.Min(Math.Min(3, config.maxPlayers - 1), helpers));
            _sim = new CoopSim(config);
            var mine = new HashSet<string>(unlocked ?? new string[0]);
            var all = new List<string>();
            for (int i = 0; i < config.structures.Count; i++)
            {
                TowerDef def = config.structures[i];
                if (!def.IsBuildable) continue;
                all.Add(def.code);
                _structures.Add(new CoopStructureInfo { def = def, usable = mine.Contains(def.code) });
            }
            _sim.Join(MyPid, string.IsNullOrEmpty(myName) ? "나" : myName, null, myLevel, true, mine, false);
            string[] roles = { CoopRoleCodes.Builder, CoopRoleCodes.Support, CoopRoleCodes.Fighter };
            for (int i = 0; i < helpers; i++)
            {
                string pid = "p_practice_npc" + (i + 1);
                // NPC 는 실제 플레이어인 것처럼 보이지 않게 이름에 표시를 붙인다.
                _sim.Join(pid, CoopPractice.NpcTag + " 연습 대원 " + (i + 1), roles[i], 1, false, all, false, true);
                _helpers.Add(pid);
            }
            Snapshot = CoopSnapshot.FromSim(_sim, MyPid);
        }

        public void Update(float dt)
        {
            if (_left || Finish != null) return;
            _carry += Math.Max(0f, dt);
            int steps = 0;
            while (_carry >= CoopSim.TickSeconds && steps < MaxStepsPerUpdate)
            {
                _carry -= CoopSim.TickSeconds;
                steps++;
                if (_sim.IsRunning && _sim.Tick % 10 == 0) HelpersThink();
                _sim.Step();
                if (_sim.IsFinished) break;
            }
            // 화면이 오래 멈췄다 돌아와도 한꺼번에 따라잡지 않는다.
            if (_carry > CoopSim.TickSeconds * MaxStepsPerUpdate) _carry = 0f;
            Collect();
        }

        private void Collect()
        {
            _events.AddRange(_sim.DrainEvents());
            Snapshot = CoopSnapshot.FromSim(_sim, MyPid);
            if (_sim.IsRunning && State == CoopLinkStates.Lobby) State = CoopLinkStates.Battle;
            if (_sim.IsFinished && Finish == null)
            {
                CoopReport report = _sim.BuildReport();
                Finish = new CoopFinish
                {
                    result = report.result, wavesCleared = report.wavesCleared, gateHpLeft = report.gateHpLeft,
                    durationSeconds = report.durationSeconds, closeReason = report.closeReason, practice = true,
                };
                State = CoopLinkStates.Finished;
            }
        }

        /// <summary>NPC 대원: 길목에 서서 쏘고, 설치 담당은 타워 몇 개를 세우고, 지원 담당은 가까운 타워를 고친다.</summary>
        private void HelpersThink()
        {
            CoopMap map = _sim.Map;
            for (int i = 0; i < _helpers.Count; i++)
            {
                CoopPlayerState helper = _sim.FindPlayer(_helpers[i]);
                if (helper == null) continue;
                CoopVec post = map.PointAt(map.PathLength * HelperPosts[i % HelperPosts.Length]);
                // 길 한가운데가 아니라 옆에 선다.
                CoopVec stand = map.Clamp(new CoopVec(post.x + 3f, post.z - 3f));

                if (helper.role == CoopRoleCodes.Support)
                {
                    CoopTowerState damaged = null;
                    foreach (CoopTowerState tower in _sim.Towers)
                        if (tower.hp < tower.maxHp && (damaged == null || tower.hp * damaged.maxHp < damaged.hp * tower.maxHp)) damaged = tower;
                    if (damaged != null)
                    {
                        stand = damaged.pos;
                        if (CoopVec.Distance(helper.pos, damaged.pos) <= CoopSim.RepairRangeM && _sim.Budget.Balance >= _sim.Config.rules.repairEnergy + 50)
                            _sim.Repair(helper.pid, "npc" + _nextRequest++, damaged.slot);
                    }
                }
                else if (helper.role == CoopRoleCodes.Builder && _helperTowers < HelperTowerLimit)
                {
                    TowerDef gun = null;
                    for (int s = 0; s < _sim.Config.structures.Count; s++)
                        if (_sim.Config.structures[s].code == TowerCodes.MachineGun) gun = _sim.Config.structures[s];
                    // 플레이어가 쓸 예산을 남겨 둔다.
                    if (gun != null && _sim.Budget.Balance >= _sim.BuildCost(helper, gun) + 100)
                    {
                        for (int s = 0; s < HelperSlots.Length; s++)
                        {
                            if (!map.IsSlot(HelperSlots[s]) || _sim.TowerAt(HelperSlots[s]) != null) continue;
                            if (_sim.Build(helper.pid, "npc" + _nextRequest++, HelperSlots[s], gun.code).Ok) _helperTowers++;
                            break;
                        }
                    }
                }
                _sim.Move(helper.pid, stand.x, stand.z);
            }
        }

        private string Answer(string action, string requestId, CoopOutcome outcome)
        {
            _results.Add(new CoopActionResult
            {
                action = action, requestId = requestId, ok = outcome.Ok, reject = outcome.reject, duplicate = outcome.duplicate,
                energy = _sim.Budget.Balance,
            });
            Collect();
            return requestId;
        }

        public void Start() => Answer("start", null, _sim.Start(MyPid));

        public void Move(float x, float z) => _sim.Move(MyPid, x, z);

        public void Focus(int enemyId) => _sim.Focus(MyPid, enemyId);

        public string Build(int slot, string code)
        {
            string id = "r" + _nextRequest++;
            return Answer("build", id, _sim.Build(MyPid, id, slot, code));
        }

        public string Upgrade(int slot)
        {
            string id = "r" + _nextRequest++;
            return Answer("upgrade", id, _sim.Upgrade(MyPid, id, slot));
        }

        public string Repair(int slot)
        {
            string id = "r" + _nextRequest++;
            return Answer("repair", id, _sim.Repair(MyPid, id, slot));
        }

        public void Ping(string kind, float x, float z)
        {
            CoopOutcome outcome = _sim.Ping(MyPid, kind, x, z);
            if (!outcome.Ok) Answer("ping", null, outcome);
            else Collect();
        }

        public void HidePings(string pid, bool hide)
        {
            if (string.IsNullOrEmpty(pid) || pid == MyPid || _sim.FindPlayer(pid) == null) return;
            _sim.Pings.Hide(MyPid, pid, hide);
            Collect();
        }

        public bool IsHidden(string pid) => _sim.Pings.IsHidden(MyPid, pid);

        public void SetRole(string role)
        {
            CoopOutcome outcome = _sim.SetRole(MyPid, role);
            if (!outcome.Ok) Answer("set_role", null, outcome);
            else Collect();
        }

        public void Leave()
        {
            if (_left) return;
            _left = true;
            ClosedReason = "left";
            State = CoopLinkStates.Closed;
        }

        public List<CoopEvent> DrainEvents()
        {
            var drained = new List<CoopEvent>(_events);
            _events.Clear();
            return drained;
        }

        public List<CoopActionResult> DrainResults()
        {
            var drained = new List<CoopActionResult>(_results);
            _results.Clear();
            return drained;
        }
    }
}
