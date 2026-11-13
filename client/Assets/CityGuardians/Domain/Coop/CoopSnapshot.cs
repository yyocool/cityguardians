using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>전투 단계의 이름. 실시간 서버가 보내는 이름과 같다.</summary>
    public static class CoopPhaseNames
    {
        public const string Lobby = "lobby";
        public const string Rest = "rest";
        public const string Wave = "wave";
        public const string Won = "won";
        public const string Lost = "lost";

        public static string Of(CoopPhase phase)
        {
            switch (phase)
            {
                case CoopPhase.Lobby: return Lobby;
                case CoopPhase.Rest: return Rest;
                case CoopPhase.Wave: return Wave;
                case CoopPhase.Won: return Won;
                default: return Lost;
            }
        }
    }

    public sealed class CoopPlayerView
    {
        public string pid;
        public string name;
        /// <summary>고른 역할. 고르지 않았으면 null.</summary>
        public string role;
        public int level = 1;
        public bool host;
        public bool connected = true;
        /// <summary>연습전의 NPC 대원. 이름에 [NPC] 가 붙어 있고, 실제 플레이어인 것처럼 보이지 않는다.</summary>
        public bool npc;
        public float x;
        public float z;
        public int kills;
        public int towersBuilt;
        public int towersUpgraded;
        public int repairs;
    }

    public sealed class CoopEnemyView
    {
        public int id;
        public string code;
        public float x;
        public float z;
        /// <summary>향하는 방향 (길이 1).</summary>
        public float dx;
        public float dz;
        public int hp;
        public int maxHp;
        public bool slowed;
        public bool stunned;
        public bool boss;
    }

    public sealed class CoopTowerView
    {
        public int slot;
        public string code;
        public int level;
        public int hp;
        public int maxHp;
        /// <summary>세운 사람.</summary>
        public string owner;
        public float range;
    }

    public sealed class CoopPingView
    {
        public int id;
        public string pid;
        public string kind;
        public float x;
        public float z;
        /// <summary>지도에 남아 있을 시간 (ms).</summary>
        public int leftMs;
    }

    public sealed class CoopDangerView
    {
        public float x;
        public float z;
        public int enemies;
        public bool nearGate;
    }

    /// <summary>
    /// 한 순간의 전투 상태. 화면은 이것만 보고 그린다.
    /// 온라인에서는 실시간 서버가 보낸 상태에서 만들고, 연습전에서는 기기 안의 CoopSim 에서 만든다.
    /// 클라이언트는 이 값을 바꿔 서버에 보내지 않는다 (바꿀 수 있는 것은 "무엇을 하겠다"는 요청뿐이다).
    /// </summary>
    public sealed class CoopSnapshot
    {
        public long tick;
        /// <summary>CoopPhaseNames.</summary>
        public string phase = CoopPhaseNames.Lobby;
        public int timeMs;
        public int wave;
        public int waveCount;
        public int wavesCleared;
        /// <summary>다음 웨이브까지 남은 시간 (ms). 쉬는 시간이 아니면 0.</summary>
        public int restMs;
        public int gateHp;
        public int gateMaxHp = 1;
        /// <summary>공동 예산.</summary>
        public int energy;
        public List<CoopPlayerView> players = new List<CoopPlayerView>();
        public List<CoopEnemyView> enemies = new List<CoopEnemyView>();
        public List<CoopTowerView> towers = new List<CoopTowerView>();
        /// <summary>이 사람에게 보이는 핑.</summary>
        public List<CoopPingView> pings = new List<CoopPingView>();
        public List<CoopDangerView> dangers = new List<CoopDangerView>();

        public bool IsLobby => phase == CoopPhaseNames.Lobby;
        public bool IsRunning => phase == CoopPhaseNames.Rest || phase == CoopPhaseNames.Wave;
        public bool IsFinished => phase == CoopPhaseNames.Won || phase == CoopPhaseNames.Lost;

        public CoopPlayerView FindPlayer(string pid)
        {
            for (int i = 0; i < players.Count; i++)
                if (players[i].pid == pid) return players[i];
            return null;
        }

        public CoopTowerView FindTower(int slot)
        {
            for (int i = 0; i < towers.Count; i++)
                if (towers[i].slot == slot) return towers[i];
            return null;
        }

        public CoopEnemyView FindEnemy(int id)
        {
            for (int i = 0; i < enemies.Count; i++)
                if (enemies[i].id == id) return enemies[i];
            return null;
        }

        public int ConnectedCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < players.Count; i++)
                    if (players[i].connected) count++;
                return count;
            }
        }

        /// <summary>연습전: 기기 안에서 도는 시뮬레이션의 지금 상태.</summary>
        public static CoopSnapshot FromSim(CoopSim sim, string viewerPid)
        {
            var snapshot = new CoopSnapshot
            {
                tick = sim.Tick, phase = CoopPhaseNames.Of(sim.Phase), timeMs = sim.NowMs, wave = sim.Wave, waveCount = sim.WaveCount,
                wavesCleared = sim.WavesCleared, restMs = (int)Math.Round(sim.RestLeft * 1000f), gateHp = sim.GateHp, gateMaxHp = sim.GateMaxHp,
                energy = sim.Budget.Balance,
            };
            foreach (CoopPlayerState p in sim.Players)
                snapshot.players.Add(new CoopPlayerView
                {
                    pid = p.pid, name = p.name, role = p.role, level = p.level, host = p.host, connected = p.connected, npc = p.npc,
                    x = p.pos.x, z = p.pos.z, kills = p.kills, towersBuilt = p.towersBuilt, towersUpgraded = p.towersUpgraded, repairs = p.repairs,
                });
            foreach (CoopEnemyState e in sim.Enemies)
                snapshot.enemies.Add(new CoopEnemyView
                {
                    id = e.id, code = e.code, x = e.pos.x, z = e.pos.z, dx = e.dir.x, dz = e.dir.z, hp = e.hp, maxHp = e.maxHp,
                    slowed = e.slowLeft > 0f, stunned = e.stunLeft > 0f, boss = e.def.staggerImmune,
                });
            foreach (CoopTowerState t in sim.Towers)
                snapshot.towers.Add(new CoopTowerView
                {
                    slot = t.slot, code = t.code, level = t.level, hp = t.hp, maxHp = t.maxHp, owner = t.ownerPid,
                    range = TowerMath.Range(t.def, t.level),
                });
            foreach (TacticalPing ping in sim.Pings.VisibleTo(viewerPid))
                snapshot.pings.Add(new CoopPingView
                {
                    id = ping.id, pid = ping.pid, kind = ping.kind, x = ping.pos.x, z = ping.pos.z,
                    leftMs = Math.Max(0, (int)Math.Round((ping.expiresAt - sim.Clock) * 1000f)),
                });
            foreach (CoopDangerZone d in sim.DangerZones())
                snapshot.dangers.Add(new CoopDangerView { x = d.pos.x, z = d.pos.z, enemies = d.enemies, nearGate = d.nearGate });
            return snapshot;
        }

        /// <summary>미니맵에 그릴 것들로 바꾼다.</summary>
        public MinimapSource ToMinimap(CoopMap map, string viewerPid)
        {
            var source = new MinimapSource { map = map, viewerPid = viewerPid, gateHpRatio = gateMaxHp > 0 ? gateHp / (float)gateMaxHp : 0f };
            for (int i = 0; i < players.Count; i++)
            {
                CoopPlayerView p = players[i];
                source.players.Add(new MinimapSource.Actor { pid = p.pid, name = p.name, pos = new CoopVec(p.x, p.z), connected = p.connected });
            }
            for (int i = 0; i < enemies.Count; i++)
            {
                CoopEnemyView e = enemies[i];
                source.enemies.Add(new MinimapSource.Foe { id = e.id, pos = new CoopVec(e.x, e.z), dir = new CoopVec(e.dx, e.dz), boss = e.boss });
            }
            for (int i = 0; i < towers.Count; i++)
            {
                CoopTowerView t = towers[i];
                source.towers.Add(new MinimapSource.Structure
                {
                    slot = t.slot, code = t.code, pos = map.IsSlot(t.slot) ? map.slots[t.slot] : new CoopVec(0f, 0f), range = t.range,
                    hpRatio = t.maxHp > 0 ? t.hp / (float)t.maxHp : 0f,
                });
            }
            for (int i = 0; i < pings.Count; i++)
            {
                CoopPingView ping = pings[i];
                source.pings.Add(new TacticalPing { id = ping.id, pid = ping.pid, kind = ping.kind, pos = new CoopVec(ping.x, ping.z) });
            }
            for (int i = 0; i < dangers.Count; i++)
            {
                CoopDangerView d = dangers[i];
                source.dangers.Add(new CoopDangerZone { pos = new CoopVec(d.x, d.z), enemies = d.enemies, nearGate = d.nearGate });
            }
            return source;
        }
    }
}
