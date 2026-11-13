using System;
using System.Collections.Generic;
using System.Text.Json;
using CityGuardians.Domain;

namespace CityGuardians.Realtime
{
    /// <summary>클라이언트가 보낸 메시지 하나. 형식만 확인한 것이고, 되는지 안 되는지는 방(시뮬레이션)이 판정한다.</summary>
    public sealed class ClientMessage
    {
        public const string Join = "join";
        public const string Start = "start";
        public const string Move = "move";
        public const string Focus = "focus";
        public const string Build = "build";
        public const string Upgrade = "upgrade";
        public const string Repair = "repair";
        public const string Ping = "ping";
        public const string HidePings = "hide_pings";
        public const string SetRole = "set_role";
        public const string Alive = "alive";
        public const string Leave = "leave";

        public string Type;
        public string Ticket;
        public string RequestId;
        public int Slot = -1;
        public string Code;
        public float X;
        public float Z;
        public int EnemyId;
        public string Kind;
        public string Pid;
        public bool Hide;
        public string Role;

        private static string Text(JsonElement root, string name, int maxLength)
        {
            if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String) return null;
            string text = value.GetString();
            return text.Length <= maxLength ? text : null;
        }

        private static bool Number(JsonElement root, string name, out float number)
        {
            number = 0f;
            return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out number)
                && !float.IsNaN(number) && !float.IsInfinity(number) && Math.Abs(number) <= 100000f;
        }

        private static bool Whole(JsonElement root, string name, out int number)
        {
            number = 0;
            return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
        }

        /// <summary>메시지를 읽는다. 형식이 맞지 않으면 null.</summary>
        public static ClientMessage Parse(ArraySegment<byte> utf8)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 4 }))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    var message = new ClientMessage { Type = Text(root, "type", 20) };
                    switch (message.Type)
                    {
                        case Join:
                            message.Ticket = Text(root, "ticket", Signer.MaxTicketLength);
                            return message.Ticket != null ? message : null;
                        case Start:
                        case Alive:
                        case Leave:
                            return message;
                        case Move:
                            return Number(root, "x", out message.X) && Number(root, "z", out message.Z) ? message : null;
                        case Focus:
                            return Whole(root, "enemy_id", out message.EnemyId) ? message : null;
                        case Build:
                            message.RequestId = Text(root, "request_id", 40);
                            message.Code = Text(root, "code", 40);
                            return message.RequestId != null && message.Code != null && Whole(root, "slot", out message.Slot) ? message : null;
                        case Upgrade:
                        case Repair:
                            message.RequestId = Text(root, "request_id", 40);
                            return message.RequestId != null && Whole(root, "slot", out message.Slot) ? message : null;
                        case Ping:
                            message.Kind = Text(root, "kind", 24);
                            return message.Kind != null && Number(root, "x", out message.X) && Number(root, "z", out message.Z) ? message : null;
                        case HidePings:
                            message.Pid = Text(root, "pid", 32);
                            message.Hide = !root.TryGetProperty("hide", out JsonElement hide) || hide.ValueKind != JsonValueKind.False;
                            return message.Pid != null ? message : null;
                        case SetRole:
                            // role 이 없거나 null 이면 역할을 내려놓는다.
                            message.Role = Text(root, "role", 24);
                            return message;
                        default:
                            return null;
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>서버가 보내는 메시지를 만든다 (docs/COOP_DEFENSE_SYSTEM.md 5장). 이름은 보낼 때 snake_case 가 된다.</summary>
    public static class Messages
    {
        /// <summary>좌표는 cm 까지만 보낸다.</summary>
        private static float R(float value) => MathF.Round(value * 100f) / 100f;

        public static string PhaseName(CoopPhase phase)
        {
            switch (phase)
            {
                case CoopPhase.Lobby: return "lobby";
                case CoopPhase.Rest: return "rest";
                case CoopPhase.Wave: return "wave";
                case CoopPhase.Won: return "won";
                default: return "lost";
            }
        }

        public static object Error(string reason) => new { type = "error", reason };

        public static object Reject(string action, string reason) => new { type = "reject", action, reason };

        public static object Result(string action, string requestId, CoopOutcome outcome, int energy) =>
            new { type = "result", action, requestId, ok = outcome.Ok, outcome.reject, outcome.duplicate, energy };

        /// <summary>
        /// 전투 중의 알림. 해당하지 않는 값(자리가 없는 알림의 slot 등)은 null 로 보내지 않고 아예 넣지 않는다:
        /// 클라이언트(Unity 의 JsonUtility)는 숫자 자리에 온 null 을 다루지 못한다.
        /// </summary>
        public static object Event(CoopEvent e)
        {
            var message = new Dictionary<string, object> { ["type"] = "event", ["event"] = e.type, ["value"] = e.value };
            if (e.pid != null) message["pid"] = e.pid;
            if (e.slot >= 0) message["slot"] = e.slot;
            if (e.code != null) message["code"] = e.code;
            if (e.type == CoopEventTypes.Ping)
            {
                message["x"] = R(e.x);
                message["z"] = R(e.z);
            }
            if (e.id != 0) message["id"] = e.id;
            return message;
        }

        public static object PlayerEvent(string name, CoopPlayerState player) =>
            new { type = "event", @event = name, player.pid, player.name };

        public static List<object> Pings(IEnumerable<TacticalPing> pings, float clock)
        {
            var list = new List<object>();
            foreach (TacticalPing ping in pings)
                list.Add(new
                {
                    ping.id, ping.pid, ping.kind, x = R(ping.pos.x), z = R(ping.pos.z),
                    leftMs = Math.Max(0, (int)MathF.Round((ping.expiresAt - clock) * 1000f)),
                });
            return list;
        }

        /// <summary>지금의 전투 상태. pings 는 받는 사람마다 다를 수 있어서 따로 받는다.</summary>
        public static object State(CoopSim sim, List<object> pings)
        {
            var players = new List<object>();
            foreach (CoopPlayerState p in sim.Players)
                players.Add(new
                {
                    p.pid, p.name, p.role, p.level, p.host, p.connected, p.npc, x = R(p.pos.x), z = R(p.pos.z), p.kills, p.towersBuilt, p.towersUpgraded,
                    p.repairs,
                });
            var enemies = new List<object>();
            foreach (CoopEnemyState e in sim.Enemies)
                enemies.Add(new
                {
                    e.id, e.code, x = R(e.pos.x), z = R(e.pos.z), dx = R(e.dir.x), dz = R(e.dir.z), e.hp, e.maxHp, slowed = e.slowLeft > 0f,
                    stunned = e.stunLeft > 0f, boss = e.def.staggerImmune,
                });
            var towers = new List<object>();
            foreach (CoopTowerState t in sim.Towers)
                towers.Add(new { t.slot, t.code, t.level, t.hp, t.maxHp, owner = t.ownerPid, range = R(TowerMath.Range(t.def, t.level)) });
            var dangers = new List<object>();
            foreach (CoopDangerZone d in sim.DangerZones())
                dangers.Add(new { x = R(d.pos.x), z = R(d.pos.z), d.enemies, d.nearGate });
            return new
            {
                type = "state", tick = sim.Tick, phase = PhaseName(sim.Phase), timeMs = sim.NowMs, wave = sim.Wave, waveCount = sim.WaveCount,
                wavesCleared = sim.WavesCleared, restMs = (int)MathF.Round(sim.RestLeft * 1000f), gateHp = sim.GateHp, gateMaxHp = sim.GateMaxHp,
                energy = sim.Budget.Balance, players, enemies, towers, pings, dangers,
            };
        }

        public static object Welcome(Ticket ticket, RoomConfig config, CoopSim sim, RealtimeOptions options, object state)
        {
            CoopMap map = sim.Map;
            var path = new List<object>();
            foreach (CoopVec p in map.path) path.Add(new { p.x, p.z });
            var slots = new List<object>();
            foreach (CoopVec p in map.slots) slots.Add(new { p.x, p.z });
            var landmarks = new List<object>();
            foreach (CoopLandmark l in map.landmarks) landmarks.Add(new { l.code, l.name, l.kind, l.pos.x, l.pos.z });
            var structures = new List<object>();
            foreach (TowerDef t in sim.Config.structures)
            {
                if (!t.IsBuildable) continue;
                structures.Add(new
                {
                    t.code, t.name, t.description, t.kind, t.tier, t.energyCost, t.upgradeEnergyCost, t.maxUpgradeLevel, t.damage, t.shotsPerSecond,
                    t.range, t.splashRadius, t.maxHp, t.maxPerBattle, t.effectValue, t.effectSeconds, usable = ticket.Structs.Contains(t.code),
                });
            }
            CoopRules rules = sim.Config.rules;
            PingRules ping = sim.Config.ping;
            return new
            {
                type = "welcome", you = ticket.Pid,
                session = new { id = config.SessionId, config.RegionCode, config.RegionName, config.HostPid, sim.Config.maxPlayers },
                tickMs = (int)(CoopSim.TickSeconds * 1000f), snapshotEveryTicks = options.SnapshotEveryTicks,
                zone = new { sim.Config.zone.code, sim.Config.zone.name, waveCount = sim.WaveCount },
                map = new { map.minX, map.maxX, map.minZ, map.maxZ, path, slots, landmarks },
                structures, roles = sim.Config.roles,
                rules = new
                {
                    rules.minPlayersToStart, rules.repairEnergy, rules.repairHp, repairRangeM = CoopSim.RepairRangeM, rules.playerRangeM,
                    playerSpeed = CoopSim.PlayerSpeed,
                },
                ping = new
                {
                    ping.enabled, ping.burst, ping.refillSeconds, ping.lifetimeSeconds, ping.maxActivePerPlayer, ping.routeOverlay, ping.rangeOverlay,
                    kinds = PingKinds.All, blocked = ticket.PingBlocked,
                },
                state,
            };
        }
    }
}
