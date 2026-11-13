using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    public static class MinimapBlipKinds
    {
        public const string Self = "self";
        public const string Teammate = "teammate";
        public const string Enemy = "enemy";
        /// <summary>중간 보스. 더 크게 그린다.</summary>
        public const string Boss = "boss";
        public const string Tower = "tower";
        public const string Gate = "gate";
        public const string Wall = "wall";
        public const string Facility = "facility";
        public const string Spawn = "spawn";
        public const string Danger = "danger";
        public const string Ping = "ping";
        /// <summary>긴급 지원 요청 (방어 요청, 수리 요청 핑). 지도 밖이어도 가장자리에 보인다.</summary>
        public const string SupportRequest = "support_request";
    }

    /// <summary>미니맵 위의 한 점. 좌표는 0~1 (u: 왼쪽→오른쪽, v: 위→아래). 북쪽이 위다.</summary>
    public sealed class MinimapBlip
    {
        public string kind;
        public float u;
        public float v;
        /// <summary>향하는 방향 (도). 0 이 위(북), 시계 방향. 방향이 없으면 -1.</summary>
        public float heading = -1f;
        public string label;
        public string code;
        public int id;
        public string pid;
        /// <summary>지도 너비에 대한 반지름 (위험 지역의 크기, 타워 사거리). 없으면 0.</summary>
        public float radius;
        /// <summary>남은 내구도의 비율 (타워, 관문). 없으면 -1.</summary>
        public float hpRatio = -1f;
        /// <summary>보이는 범위 밖이라 가장자리에 붙여 그린 것인가.</summary>
        public bool clamped;
    }

    public sealed class MinimapPoint
    {
        public float u;
        public float v;
    }

    /// <summary>미니맵에 그릴 것들. 실시간 서버가 보낸 상태에서 만든다 (연습전에서는 CoopSim 에서).</summary>
    public sealed class MinimapSource
    {
        public sealed class Actor
        {
            public string pid;
            public string name;
            public CoopVec pos;
            public bool connected = true;
        }

        public sealed class Foe
        {
            public int id;
            public CoopVec pos;
            public CoopVec dir;
            public bool boss;
        }

        public sealed class Structure
        {
            public int slot;
            public string code;
            public CoopVec pos;
            public float range;
            public float hpRatio = 1f;
        }

        public CoopMap map;
        public string viewerPid;
        public List<Actor> players = new List<Actor>();
        public List<Foe> enemies = new List<Foe>();
        public List<Structure> towers = new List<Structure>();
        /// <summary>이 사람에게 보이는 핑 (숨긴 사람의 핑은 빠져 있다).</summary>
        public List<TacticalPing> pings = new List<TacticalPing>();
        public List<CoopDangerZone> dangers = new List<CoopDangerZone>();
        public float gateHpRatio = 1f;

        public static MinimapSource FromSim(CoopSim sim, string viewerPid)
        {
            var source = new MinimapSource { map = sim.Map, viewerPid = viewerPid, gateHpRatio = sim.GateHp / (float)sim.GateMaxHp };
            foreach (var player in sim.Players)
                source.players.Add(new Actor { pid = player.pid, name = player.name, pos = player.pos, connected = player.connected });
            foreach (var enemy in sim.Enemies)
                source.enemies.Add(new Foe { id = enemy.id, pos = enemy.pos, dir = enemy.dir, boss = enemy.def.staggerImmune });
            foreach (var tower in sim.Towers)
                source.towers.Add(new Structure
                {
                    slot = tower.slot, code = tower.code, pos = tower.pos, range = TowerMath.Range(tower.def, tower.level),
                    hpRatio = tower.hp / (float)tower.maxHp,
                });
            source.pings = sim.Pings.VisibleTo(viewerPid);
            source.dangers = sim.DangerZones();
            return source;
        }
    }

    public sealed class MinimapOptions
    {
        /// <summary>전체 지도로 펼쳤는가. 접혀 있으면 화면 오른쪽 위에 내 둘레만 보인다.</summary>
        public bool expanded;
        /// <summary>접힌 미니맵이 보여 주는 반경 (m).</summary>
        public float radiusM = 28f;
        /// <summary>전술 지도에 적 이동 경로를 그릴까 (펼쳤을 때만, 서버 설정이 허용할 때만).</summary>
        public bool showRoute = true;
        /// <summary>전술 지도에 타워 사거리를 그릴까 (펼쳤을 때만, 서버 설정이 허용할 때만).</summary>
        public bool showRanges = true;
    }

    public sealed class MinimapView
    {
        public List<MinimapBlip> blips = new List<MinimapBlip>();
        /// <summary>적 이동 경로 (꺾이는 점들). 그리지 않으면 비어 있다.</summary>
        public List<MinimapPoint> route = new List<MinimapPoint>();
        /// <summary>지도 한 변이 나타내는 거리 (m).</summary>
        public float widthM;
        public float heightM;
        public bool expanded;
    }

    /// <summary>
    /// 전술 미니맵의 내용을 정한다 (docs/TACTICAL_MINIMAP.md). 그리는 일은 UI 가 한다.
    ///
    /// 접힌 미니맵은 나를 가운데 두고 둘레만 보여 준다. 관문, 핑, 긴급 지원 요청, 위험 지역은 범위 밖이어도 가장자리에 붙여 보여 준다.
    /// 펼친 전술 지도는 지도 전체를 보여 주고, 고르면 적 이동 경로와 타워 사거리를 겹쳐 그린다.
    /// </summary>
    public static class Minimap
    {
        public static float HeadingOf(CoopVec dir)
        {
            if (Math.Abs(dir.x) < 0.0001f && Math.Abs(dir.z) < 0.0001f) return -1f;
            double degrees = Math.Atan2(dir.x, dir.z) * 180.0 / Math.PI;
            return (float)((degrees + 360.0) % 360.0);
        }

        public static MinimapView Build(MinimapSource source, MinimapOptions options, PingRules rules = null)
        {
            options = options ?? new MinimapOptions();
            var map = source.map;
            var view = new MinimapView { expanded = options.expanded };
            CoopVec center = new CoopVec((map.minX + map.maxX) / 2f, (map.minZ + map.maxZ) / 2f);
            float halfWidth, halfHeight;
            if (options.expanded)
            {
                // 지도 전체가 들어가는 정사각형.
                halfWidth = halfHeight = Math.Max(map.maxX - map.minX, map.maxZ - map.minZ) / 2f;
            }
            else
            {
                halfWidth = halfHeight = Math.Max(5f, options.radiusM);
                foreach (var player in source.players)
                    if (player.pid == source.viewerPid) center = player.pos;
            }
            view.widthM = halfWidth * 2f;
            view.heightM = halfHeight * 2f;

            // 가장자리에서 조금 안쪽까지를 "보이는 범위"로 본다.
            const float margin = 0.04f;
            MinimapBlip Place(string kind, CoopVec pos, bool always)
            {
                float u = (pos.x - (center.x - halfWidth)) / (halfWidth * 2f);
                float v = ((center.z + halfHeight) - pos.z) / (halfHeight * 2f);
                bool inside = u >= 0f && u <= 1f && v >= 0f && v <= 1f;
                if (!inside && !always) return null;
                var blip = new MinimapBlip { kind = kind };
                if (!inside)
                {
                    blip.clamped = true;
                    u = Math.Max(margin, Math.Min(1f - margin, u));
                    v = Math.Max(margin, Math.Min(1f - margin, v));
                }
                blip.u = u;
                blip.v = v;
                view.blips.Add(blip);
                return blip;
            }

            // 그리는 순서: 바닥의 것부터.
            foreach (var landmark in map.landmarks)
            {
                string kind = landmark.kind == CoopLandmarkKinds.Gate ? MinimapBlipKinds.Gate
                    : landmark.kind == CoopLandmarkKinds.Wall ? MinimapBlipKinds.Wall
                    : landmark.kind == CoopLandmarkKinds.Spawn ? MinimapBlipKinds.Spawn : MinimapBlipKinds.Facility;
                var blip = Place(kind, landmark.pos, landmark.kind == CoopLandmarkKinds.Gate);
                if (blip == null) continue;
                blip.label = landmark.name;
                blip.code = landmark.code;
                if (kind == MinimapBlipKinds.Gate) blip.hpRatio = Math.Max(0f, Math.Min(1f, source.gateHpRatio));
            }
            bool ranges = options.expanded && options.showRanges && (rules == null || rules.rangeOverlay == 1);
            foreach (var tower in source.towers)
            {
                var blip = Place(MinimapBlipKinds.Tower, tower.pos, false);
                if (blip == null) continue;
                blip.id = tower.slot;
                blip.code = tower.code;
                blip.hpRatio = Math.Max(0f, Math.Min(1f, tower.hpRatio));
                if (ranges) blip.radius = tower.range / (halfWidth * 2f);
            }
            foreach (var danger in source.dangers)
            {
                var blip = Place(MinimapBlipKinds.Danger, danger.pos, true);
                blip.id = danger.enemies;
                blip.label = danger.nearGate ? "관문이 위험해요" : "적이 몰려 있어요";
                blip.radius = Math.Min(0.25f, (4f + danger.enemies * 0.4f) / (halfWidth * 2f));
            }
            foreach (var enemy in source.enemies)
            {
                var blip = Place(enemy.boss ? MinimapBlipKinds.Boss : MinimapBlipKinds.Enemy, enemy.pos, false);
                if (blip == null) continue;
                blip.id = enemy.id;
                blip.heading = HeadingOf(enemy.dir);
            }
            foreach (var player in source.players)
            {
                if (player.pid == source.viewerPid) continue;
                var blip = Place(MinimapBlipKinds.Teammate, player.pos, options.expanded);
                if (blip == null) continue;
                blip.pid = player.pid;
                blip.label = player.connected ? player.name : player.name + " (연결 끊김)";
            }
            foreach (var ping in source.pings)
            {
                bool support = PingKinds.IsSupportRequest(ping.kind);
                var blip = Place(support ? MinimapBlipKinds.SupportRequest : MinimapBlipKinds.Ping, ping.pos, true);
                blip.id = ping.id;
                blip.pid = ping.pid;
                blip.code = ping.kind;
                blip.label = PingKinds.DisplayName(ping.kind);
            }
            foreach (var player in source.players)
            {
                if (player.pid != source.viewerPid) continue;
                var blip = Place(MinimapBlipKinds.Self, player.pos, true);
                blip.pid = player.pid;
                blip.label = "나";
            }

            if (options.expanded && options.showRoute && (rules == null || rules.routeOverlay == 1))
            {
                foreach (var point in map.path)
                    view.route.Add(new MinimapPoint
                    {
                        u = (point.x - (center.x - halfWidth)) / (halfWidth * 2f),
                        v = ((center.z + halfHeight) - point.z) / (halfHeight * 2f),
                    });
            }
            return view;
        }

        /// <summary>미니맵 위의 점(0~1)이 가리키는 지도 위의 곳. 전술 지도를 눌러 핑을 찍을 때 쓴다.</summary>
        public static CoopVec ToWorld(MinimapSource source, MinimapOptions options, float u, float v)
        {
            var map = source.map;
            CoopVec center = new CoopVec((map.minX + map.maxX) / 2f, (map.minZ + map.maxZ) / 2f);
            float half;
            if (options != null && options.expanded)
            {
                half = Math.Max(map.maxX - map.minX, map.maxZ - map.minZ) / 2f;
            }
            else
            {
                half = Math.Max(5f, options != null ? options.radiusM : 28f);
                foreach (var player in source.players)
                    if (player.pid == source.viewerPid) center = player.pos;
            }
            return map.Clamp(new CoopVec(center.x - half + u * half * 2f, center.z + half - v * half * 2f));
        }
    }
}
