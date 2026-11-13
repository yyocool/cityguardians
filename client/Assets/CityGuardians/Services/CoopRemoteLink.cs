using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Services
{
    /// <summary>실시간 서버와의 연결. 글(JSON) 한 줄씩 주고받는다. 구현은 WebSocketCoopSocket.</summary>
    public interface ICoopSocket
    {
        bool IsOpen { get; }
        /// <summary>닫혔다 (연결하지 못한 것도 포함).</summary>
        bool IsClosed { get; }
        /// <summary>서버가 알려 준 닫힌 이유. 모르면 null.</summary>
        string CloseReason { get; }
        void Send(string json);
        bool TryReceive(out string json);
        void Close();
    }

    // 실시간 서버가 보내는 메시지의 모양 (docs/COOP_DEFENSE_SYSTEM.md 5장). 이름은 서버가 보내는 그대로다 (snake_case).
    // JsonUtility 가 읽을 수 있게 공개 필드만 쓴다. 모르는 필드는 버린다.
    namespace CoopWire
    {
        [Serializable]
        public sealed class Head
        {
            public string type;
            public string @event;
        }

        [Serializable]
        public sealed class Player
        {
            public string pid;
            public string name;
            public string role;
            public int level;
            public bool host;
            public bool connected;
            public bool npc;
            public float x;
            public float z;
            public int kills;
            public int towers_built;
            public int towers_upgraded;
            public int repairs;
        }

        [Serializable]
        public sealed class Enemy
        {
            public int id;
            public string code;
            public float x;
            public float z;
            public float dx;
            public float dz;
            public int hp;
            public int max_hp;
            public bool slowed;
            public bool stunned;
            public bool boss;
        }

        [Serializable]
        public sealed class Tower
        {
            public int slot;
            public string code;
            public int level;
            public int hp;
            public int max_hp;
            public string owner;
            public float range;
        }

        [Serializable]
        public sealed class PingMark
        {
            public int id;
            public string pid;
            public string kind;
            public float x;
            public float z;
            public int left_ms;
        }

        [Serializable]
        public sealed class Danger
        {
            public float x;
            public float z;
            public int enemies;
            public bool near_gate;
        }

        [Serializable]
        public sealed class State
        {
            public long tick;
            public string phase;
            public int time_ms;
            public int wave;
            public int wave_count;
            public int waves_cleared;
            public int rest_ms;
            public int gate_hp;
            public int gate_max_hp;
            public int energy;
            public List<Player> players = new List<Player>();
            public List<Enemy> enemies = new List<Enemy>();
            public List<Tower> towers = new List<Tower>();
            public List<PingMark> pings = new List<PingMark>();
            public List<Danger> dangers = new List<Danger>();
        }

        [Serializable]
        public sealed class Point
        {
            public float x;
            public float z;
        }

        [Serializable]
        public sealed class Landmark
        {
            public string code;
            public string name;
            public string kind;
            public float x;
            public float z;
        }

        [Serializable]
        public sealed class Map
        {
            public float min_x;
            public float max_x;
            public float min_z;
            public float max_z;
            public List<Point> path = new List<Point>();
            public List<Point> slots = new List<Point>();
            public List<Landmark> landmarks = new List<Landmark>();
        }

        [Serializable]
        public sealed class Structure
        {
            public string code;
            public string name;
            public string description;
            public string kind;
            public string tier;
            public int energy_cost;
            public int upgrade_energy_cost;
            public int max_upgrade_level;
            public int damage;
            public float shots_per_second;
            public float range;
            public float splash_radius;
            public int max_hp;
            public int max_per_battle;
            public int effect_value;
            public float effect_seconds;
            public bool usable;
        }

        [Serializable]
        public sealed class Role
        {
            public string code;
            public string name;
            public string description;
            public int build_discount_percent;
            public int repair_bonus_percent;
            public int attack_bonus_percent;
            public int ping_cooldown_percent;
            public int max_per_session;
        }

        [Serializable]
        public sealed class Session
        {
            public string id;
            public string region_code;
            public string region_name;
            public string host_pid;
            public int max_players;
        }

        [Serializable]
        public sealed class Zone
        {
            public string code;
            public string name;
            public int wave_count;
        }

        [Serializable]
        public sealed class Rules
        {
            public int min_players_to_start;
            public int repair_energy;
            public int repair_hp;
            public float repair_range_m;
            public int player_range_m;
            public float player_speed;
        }

        [Serializable]
        public sealed class PingInfo
        {
            public int enabled;
            public int burst;
            public int refill_seconds;
            public int lifetime_seconds;
            public int max_active_per_player;
            public int route_overlay;
            public int range_overlay;
            public List<string> kinds = new List<string>();
            public bool blocked;
        }

        [Serializable]
        public sealed class Welcome
        {
            public string you;
            public Session session = new Session();
            public int tick_ms;
            public int snapshot_every_ticks;
            public Zone zone = new Zone();
            public Map map = new Map();
            public List<Structure> structures = new List<Structure>();
            public List<Role> roles = new List<Role>();
            public Rules rules = new Rules();
            public PingInfo ping = new PingInfo();
            public State state = new State();
        }

        /// <summary>event, result, reject, error, closed, hidden, finished, recorded 를 한 모양으로 읽는다 (없는 값은 기본값).</summary>
        [Serializable]
        public sealed class Note
        {
            public string type;
            public string @event;
            public string pid;
            public string name;
            public string code;
            public string action;
            public string reason;
            public string request_id;
            public string reject;
            public string result;
            public string close_reason;
            public int slot = -1;
            public int value;
            public int id;
            public int energy = -1;
            public int waves_cleared;
            public int gate_hp_left;
            public int duration_seconds;
            public float x;
            public float z;
            public bool ok;
            public bool duplicate;
            public bool hide;
            public bool accepted;
            public bool pending;
        }
    }

    /// <summary>
    /// 온라인 협동 방어전. 실시간 서버가 보낸 상태를 그대로 받아 두고, 요청만 보낸다.
    /// 여기에는 전투 규칙이 없다: 설치가 되었는지, 예산이 얼마인지, 누가 어디에 있는지는 모두 서버가 정한다.
    /// </summary>
    public sealed class RemoteCoopLink : ICoopLink
    {
        private const float AliveEverySeconds = 10f;
        private const float MoveEverySeconds = 0.1f;
        private const float MoveMinDistance = 0.05f;
        private const int MaxMessagesPerUpdate = 200;

        private readonly ICoopSocket _socket;
        private readonly string _ticket;
        private readonly List<CoopStructureInfo> _structures = new List<CoopStructureInfo>();
        private readonly List<CoopRoleDef> _roles = new List<CoopRoleDef>();
        private readonly List<CoopEvent> _events = new List<CoopEvent>();
        private readonly List<CoopActionResult> _results = new List<CoopActionResult>();
        private readonly HashSet<string> _hidden = new HashSet<string>();
        private readonly string _requestPrefix;
        private bool _joinSent;
        private bool _leaving;
        private int _nextRequest = 1;
        private float _sinceSend;
        private float _sinceMove = MoveEverySeconds;
        private bool _moveDirty;
        private float _moveX, _moveZ, _sentX = float.NaN, _sentZ = float.NaN;

        public bool Practice => false;
        public string State { get; private set; } = CoopLinkStates.Connecting;
        public string ClosedReason { get; private set; }
        public string MyPid { get; private set; }
        public string HostPid { get; private set; }
        public string ZoneName { get; private set; }
        public string RegionName { get; private set; }
        public int MaxPlayers { get; private set; }
        public int MinPlayersToStart { get; private set; } = 2;
        public int RepairEnergy { get; private set; }
        public float RepairRangeM { get; private set; } = CoopSim.RepairRangeM;
        public float PlayerRangeM { get; private set; } = 14f;
        public CoopMap Map { get; private set; }
        public IReadOnlyList<CoopStructureInfo> Structures => _structures;
        public IReadOnlyList<CoopRoleDef> Roles => _roles;
        public PingRules PingRules { get; private set; } = new PingRules();
        public bool PingBlocked { get; private set; }
        public CoopSnapshot Snapshot { get; private set; }
        public CoopFinish Finish { get; private set; }
        /// <summary>서버가 알려 준 틱 간격 (ms).</summary>
        public int TickMs { get; private set; } = 100;

        /// <param name="ticket">게임 서버(POST /coop/sessions/{id}/ticket)에서 받은 입장권. 주소에 넣지 않고 첫 메시지로 보낸다.</param>
        public RemoteCoopLink(ICoopSocket socket, string ticket)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _ticket = ticket ?? "";
            // 요청 번호는 다시 접속해도 겹치지 않게 한다.
            _requestPrefix = "c" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-";
        }

        public void Update(float dt)
        {
            if (State == CoopLinkStates.Closed) return;
            if (!_joinSent && _socket.IsOpen)
            {
                _joinSent = true;
                _socket.Send("{\"type\":\"join\",\"ticket\":" + Quote(_ticket) + "}");
            }
            for (int i = 0; i < MaxMessagesPerUpdate && _socket.TryReceive(out string json); i++) Handle(json);

            if (_socket.IsClosed && State != CoopLinkStates.Closed)
            {
                // 끝난 뒤에 서버가 연결을 닫는 것은 정상이다.
                if (State != CoopLinkStates.Finished) Close(_leaving ? "left" : _socket.CloseReason ?? "disconnected");
                return;
            }
            if (!_joinSent || MyPid == null) return;

            _sinceMove += dt;
            if (_moveDirty && _sinceMove >= MoveEverySeconds)
            {
                _moveDirty = false;
                _sinceMove = 0f;
                _sentX = _moveX;
                _sentZ = _moveZ;
                SendNow("{\"type\":\"move\",\"x\":" + Num(_moveX) + ",\"z\":" + Num(_moveZ) + "}");
            }
            // 아무것도 보내지 않으면 서버가 끊는다.
            _sinceSend += dt;
            if (_sinceSend >= AliveEverySeconds) SendNow("{\"type\":\"alive\"}");
        }

        private void Close(string reason)
        {
            ClosedReason = reason;
            State = CoopLinkStates.Closed;
        }

        private bool CanSend => MyPid != null && State != CoopLinkStates.Closed && State != CoopLinkStates.Finished && _socket.IsOpen;

        private void SendNow(string json)
        {
            if (!_socket.IsOpen) return;
            _sinceSend = 0f;
            _socket.Send(json);
        }

        // ── 받은 메시지 ─────────────────────────────────────

        private void Handle(string json)
        {
            CoopWire.Head head;
            try
            {
                head = JsonUtility.FromJson<CoopWire.Head>(json);
                if (head == null || string.IsNullOrEmpty(head.type)) return;
                switch (head.type)
                {
                    case "welcome":
                        ApplyWelcome(JsonUtility.FromJson<CoopWire.Welcome>(json));
                        break;
                    case "state":
                        if (MyPid != null) ApplyState(JsonUtility.FromJson<CoopWire.State>(json));
                        break;
                    default:
                        ApplyNote(JsonUtility.FromJson<CoopWire.Note>(json));
                        break;
                }
            }
            catch (ArgumentException)
            {
                // 읽을 수 없는 메시지는 버린다.
            }
        }

        private static string Text(string value) => string.IsNullOrEmpty(value) ? null : value;

        private void ApplyWelcome(CoopWire.Welcome welcome)
        {
            if (welcome == null || string.IsNullOrEmpty(welcome.you) || welcome.map == null || welcome.map.path.Count < 2) return;
            MyPid = welcome.you;
            HostPid = welcome.session.host_pid;
            RegionName = welcome.session.region_name;
            MaxPlayers = welcome.session.max_players;
            ZoneName = welcome.zone.name;
            TickMs = welcome.tick_ms > 0 ? welcome.tick_ms : 100;
            MinPlayersToStart = welcome.rules.min_players_to_start;
            RepairEnergy = welcome.rules.repair_energy;
            if (welcome.rules.repair_range_m > 0f) RepairRangeM = welcome.rules.repair_range_m;
            if (welcome.rules.player_range_m > 0) PlayerRangeM = welcome.rules.player_range_m;

            var map = new CoopMap
            {
                zoneCode = welcome.zone.code, minX = welcome.map.min_x, maxX = welcome.map.max_x, minZ = welcome.map.min_z, maxZ = welcome.map.max_z,
                path = new CoopVec[welcome.map.path.Count], slots = new CoopVec[welcome.map.slots.Count],
                landmarks = new CoopLandmark[welcome.map.landmarks.Count], playerStarts = new CoopVec[0],
            };
            for (int i = 0; i < map.path.Length; i++) map.path[i] = new CoopVec(welcome.map.path[i].x, welcome.map.path[i].z);
            for (int i = 0; i < map.slots.Length; i++) map.slots[i] = new CoopVec(welcome.map.slots[i].x, welcome.map.slots[i].z);
            for (int i = 0; i < map.landmarks.Length; i++)
            {
                CoopWire.Landmark l = welcome.map.landmarks[i];
                map.landmarks[i] = new CoopLandmark { code = l.code, name = l.name, kind = l.kind, pos = new CoopVec(l.x, l.z) };
            }
            Map = map;

            _structures.Clear();
            foreach (CoopWire.Structure s in welcome.structures)
            {
                _structures.Add(new CoopStructureInfo
                {
                    usable = s.usable,
                    def = new TowerDef
                    {
                        code = s.code, name = s.name, description = s.description, kind = s.kind, tier = s.tier, energyCost = s.energy_cost,
                        upgradeEnergyCost = s.upgrade_energy_cost, maxUpgradeLevel = s.max_upgrade_level, damage = s.damage,
                        shotsPerSecond = s.shots_per_second, range = s.range, splashRadius = s.splash_radius, maxHp = s.max_hp,
                        maxPerBattle = s.max_per_battle, effectValue = s.effect_value, effectSeconds = s.effect_seconds,
                    },
                });
            }
            _roles.Clear();
            foreach (CoopWire.Role r in welcome.roles)
            {
                _roles.Add(new CoopRoleDef
                {
                    code = r.code, name = r.name, description = r.description, buildDiscountPercent = r.build_discount_percent,
                    repairBonusPercent = r.repair_bonus_percent, attackBonusPercent = r.attack_bonus_percent,
                    pingCooldownPercent = r.ping_cooldown_percent, maxPerSession = r.max_per_session,
                });
            }
            PingRules = new PingRules
            {
                enabled = welcome.ping.enabled, burst = welcome.ping.burst, refillSeconds = welcome.ping.refill_seconds,
                lifetimeSeconds = welcome.ping.lifetime_seconds, maxActivePerPlayer = welcome.ping.max_active_per_player,
                routeOverlay = welcome.ping.route_overlay, rangeOverlay = welcome.ping.range_overlay,
            };
            PingBlocked = welcome.ping.blocked;
            ApplyState(welcome.state);
        }

        private void ApplyState(CoopWire.State state)
        {
            if (state == null || string.IsNullOrEmpty(state.phase)) return;
            // 늦게 온 옛 상태는 버린다.
            if (Snapshot != null && state.tick < Snapshot.tick) return;
            var snapshot = new CoopSnapshot
            {
                tick = state.tick, phase = state.phase, timeMs = state.time_ms, wave = state.wave, waveCount = state.wave_count,
                wavesCleared = state.waves_cleared, restMs = state.rest_ms, gateHp = state.gate_hp, gateMaxHp = Math.Max(1, state.gate_max_hp),
                energy = state.energy,
            };
            foreach (CoopWire.Player p in state.players)
                snapshot.players.Add(new CoopPlayerView
                {
                    pid = p.pid, name = p.name, role = Text(p.role), level = p.level, host = p.host, connected = p.connected, npc = p.npc,
                    x = p.x, z = p.z, kills = p.kills, towersBuilt = p.towers_built, towersUpgraded = p.towers_upgraded, repairs = p.repairs,
                });
            foreach (CoopWire.Enemy e in state.enemies)
                snapshot.enemies.Add(new CoopEnemyView
                {
                    id = e.id, code = e.code, x = e.x, z = e.z, dx = e.dx, dz = e.dz, hp = e.hp, maxHp = e.max_hp, slowed = e.slowed,
                    stunned = e.stunned, boss = e.boss,
                });
            foreach (CoopWire.Tower t in state.towers)
                snapshot.towers.Add(new CoopTowerView
                {
                    slot = t.slot, code = t.code, level = t.level, hp = t.hp, maxHp = t.max_hp, owner = t.owner, range = t.range,
                });
            foreach (CoopWire.PingMark ping in state.pings)
                snapshot.pings.Add(new CoopPingView { id = ping.id, pid = ping.pid, kind = ping.kind, x = ping.x, z = ping.z, leftMs = ping.left_ms });
            foreach (CoopWire.Danger d in state.dangers)
                snapshot.dangers.Add(new CoopDangerView { x = d.x, z = d.z, enemies = d.enemies, nearGate = d.near_gate });
            Snapshot = snapshot;
            if (State == CoopLinkStates.Connecting || State == CoopLinkStates.Lobby || State == CoopLinkStates.Battle)
                State = snapshot.IsLobby ? CoopLinkStates.Lobby : CoopLinkStates.Battle;
        }

        private void ApplyNote(CoopWire.Note note)
        {
            if (note == null) return;
            switch (note.type)
            {
                case "event":
                    _events.Add(new CoopEvent
                    {
                        type = note.@event, pid = Text(note.pid), slot = note.slot, code = Text(note.code) ?? Text(note.name), value = note.value,
                        x = note.x, z = note.z, id = note.id,
                    });
                    break;
                case "result":
                    _results.Add(new CoopActionResult
                    {
                        action = note.action, requestId = Text(note.request_id), ok = note.ok, reject = Text(note.reject),
                        duplicate = note.duplicate, energy = note.energy,
                    });
                    break;
                case "reject":
                    _results.Add(new CoopActionResult { action = note.action, ok = false, reject = Text(note.reason) ?? CoopRejects.BadRequest });
                    break;
                case "hidden":
                    if (note.hide) _hidden.Add(note.pid ?? "");
                    else _hidden.Remove(note.pid ?? "");
                    break;
                case "finished":
                    Finish = new CoopFinish
                    {
                        result = note.result, wavesCleared = note.waves_cleared, gateHpLeft = note.gate_hp_left,
                        durationSeconds = note.duration_seconds, closeReason = Text(note.close_reason),
                    };
                    State = CoopLinkStates.Finished;
                    break;
                case "recorded":
                    if (Finish == null) break;
                    Finish.recordKnown = true;
                    Finish.recorded = note.ok;
                    Finish.accepted = note.accepted;
                    Finish.pending = note.pending;
                    break;
                case "closed":
                case "error":
                    Close(Text(note.reason) ?? "disconnected");
                    break;
            }
        }

        // ── 보내는 요청 ─────────────────────────────────────

        private static string Num(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            var text = new StringBuilder((value ?? "").Length + 2);
            text.Append('"');
            foreach (char c in value ?? "")
            {
                if (c == '"' || c == '\\') text.Append('\\').Append(c);
                else if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }

        private string NextRequest() => _requestPrefix + _nextRequest++;

        public void Start()
        {
            if (CanSend) SendNow("{\"type\":\"start\"}");
        }

        public void Move(float x, float z)
        {
            if (!CanSend) return;
            if (!float.IsNaN(_sentX) && Math.Abs(x - _sentX) < MoveMinDistance && Math.Abs(z - _sentZ) < MoveMinDistance) return;
            _moveX = x;
            _moveZ = z;
            _moveDirty = true;
        }

        public void Focus(int enemyId)
        {
            if (CanSend) SendNow("{\"type\":\"focus\",\"enemy_id\":" + Math.Max(0, enemyId).ToString(CultureInfo.InvariantCulture) + "}");
        }

        public string Build(int slot, string code)
        {
            if (!CanSend) return null;
            string id = NextRequest();
            SendNow("{\"type\":\"build\",\"request_id\":" + Quote(id) + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture) + ",\"code\":" + Quote(code) + "}");
            return id;
        }

        public string Upgrade(int slot)
        {
            if (!CanSend) return null;
            string id = NextRequest();
            SendNow("{\"type\":\"upgrade\",\"request_id\":" + Quote(id) + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture) + "}");
            return id;
        }

        public string Repair(int slot)
        {
            if (!CanSend) return null;
            string id = NextRequest();
            SendNow("{\"type\":\"repair\",\"request_id\":" + Quote(id) + ",\"slot\":" + slot.ToString(CultureInfo.InvariantCulture) + "}");
            return id;
        }

        public void Ping(string kind, float x, float z)
        {
            if (CanSend) SendNow("{\"type\":\"ping\",\"kind\":" + Quote(kind) + ",\"x\":" + Num(x) + ",\"z\":" + Num(z) + "}");
        }

        public void HidePings(string pid, bool hide)
        {
            if (CanSend && !string.IsNullOrEmpty(pid))
                SendNow("{\"type\":\"hide_pings\",\"pid\":" + Quote(pid) + ",\"hide\":" + (hide ? "true" : "false") + "}");
        }

        public bool IsHidden(string pid) => pid != null && _hidden.Contains(pid);

        public void SetRole(string role)
        {
            if (CanSend) SendNow("{\"type\":\"set_role\",\"role\":" + (string.IsNullOrEmpty(role) ? "null" : Quote(role)) + "}");
        }

        public void Leave()
        {
            if (State == CoopLinkStates.Closed) return;
            _leaving = true;
            if (_socket.IsOpen && MyPid != null) _socket.Send("{\"type\":\"leave\"}");
            _socket.Close();
            if (State != CoopLinkStates.Finished) Close("left");
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
