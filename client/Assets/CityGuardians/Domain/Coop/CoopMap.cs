using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    public static class CoopLandmarkKinds
    {
        public const string Spawn = "spawn";
        public const string Gate = "gate";
        public const string Wall = "wall";
        public const string Facility = "facility";
    }

    /// <summary>지도 위의 고정된 것: 적이 나오는 곳, 관문, 방어벽, 주요 시설. 미니맵에 그린다.</summary>
    public sealed class CoopLandmark
    {
        public string code;
        public string name;
        public string kind;
        public CoopVec pos;
    }

    /// <summary>
    /// 협동 방어전의 지도: 적이 지나는 길, 타워를 세울 수 있는 자리, 관문, 주요 시설.
    /// 실시간 서버와 클라이언트가 같은 지도를 쓴다. 좌표는 미터.
    /// </summary>
    public sealed class CoopMap
    {
        public string zoneCode;
        public float minX, maxX, minZ, maxZ;
        /// <summary>적이 지나는 길. 처음이 나오는 곳, 마지막이 관문.</summary>
        public CoopVec[] path;
        /// <summary>타워를 세울 수 있는 자리. 번호가 자리의 이름이다.</summary>
        public CoopVec[] slots;
        public CoopLandmark[] landmarks;
        /// <summary>플레이어가 시작하는 자리 (들어온 순서대로).</summary>
        public CoopVec[] playerStarts;

        private float[] cumulative;

        public float PathLength
        {
            get
            {
                EnsureLengths();
                return cumulative[cumulative.Length - 1];
            }
        }

        public CoopVec Gate => path[path.Length - 1];
        public CoopVec Spawn => path[0];

        private void EnsureLengths()
        {
            if (cumulative != null) return;
            cumulative = new float[path.Length];
            for (int i = 1; i < path.Length; i++) cumulative[i] = cumulative[i - 1] + CoopVec.Distance(path[i - 1], path[i]);
        }

        private int SegmentAt(float distance)
        {
            EnsureLengths();
            for (int i = 1; i < path.Length; i++)
                if (distance <= cumulative[i]) return i;
            return path.Length - 1;
        }

        /// <summary>길을 따라 distance 미터 간 곳.</summary>
        public CoopVec PointAt(float distance)
        {
            EnsureLengths();
            if (distance <= 0f) return path[0];
            if (distance >= PathLength) return path[path.Length - 1];
            int i = SegmentAt(distance);
            float length = cumulative[i] - cumulative[i - 1];
            float t = length <= 0f ? 0f : (distance - cumulative[i - 1]) / length;
            return new CoopVec(path[i - 1].x + (path[i].x - path[i - 1].x) * t, path[i - 1].z + (path[i].z - path[i - 1].z) * t);
        }

        /// <summary>길을 따라 distance 미터 간 곳에서 적이 향하는 방향 (길이 1).</summary>
        public CoopVec DirectionAt(float distance)
        {
            int i = SegmentAt(Math.Max(0f, Math.Min(distance, PathLength)));
            float dx = path[i].x - path[i - 1].x, dz = path[i].z - path[i - 1].z;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            return length <= 0f ? new CoopVec(0f, -1f) : new CoopVec(dx / length, dz / length);
        }

        /// <summary>지도 밖으로 나가지 않게 한 점.</summary>
        public CoopVec Clamp(CoopVec p)
        {
            // NaN 이 들어오면 지도 한가운데로 본다.
            if (float.IsNaN(p.x) || float.IsInfinity(p.x)) p.x = (minX + maxX) / 2f;
            if (float.IsNaN(p.z) || float.IsInfinity(p.z)) p.z = (minZ + maxZ) / 2f;
            return new CoopVec(Math.Max(minX, Math.Min(maxX, p.x)), Math.Max(minZ, Math.Min(maxZ, p.z)));
        }

        public bool IsSlot(int slot) => slot >= 0 && slot < slots.Length;

        /// <summary>
        /// 방어 구역의 지도. 지금은 모든 구역이 같은 외곽 지도를 쓴다 (구역마다 지도를 따로 두는 것은 준비 중).
        /// 길은 북쪽에서 내려와 두 번 꺾여 남쪽의 관문에 닿는다. 길이 116 m.
        /// </summary>
        public static CoopMap ForZone(string zoneCode)
        {
            var map = new CoopMap
            {
                zoneCode = zoneCode,
                minX = -26f, maxX = 26f, minZ = -26f, maxZ = 44f,
                path = new[]
                {
                    new CoopVec(0f, 40f), new CoopVec(0f, 24f), new CoopVec(-14f, 24f), new CoopVec(-14f, 8f),
                    new CoopVec(14f, 8f), new CoopVec(14f, -8f), new CoopVec(0f, -8f), new CoopVec(0f, -20f),
                },
                playerStarts = new[]
                {
                    new CoopVec(-3f, -14f), new CoopVec(3f, -14f), new CoopVec(-6f, -16f), new CoopVec(6f, -16f),
                    new CoopVec(-3f, -18f), new CoopVec(3f, -18f), new CoopVec(-9f, -18f), new CoopVec(9f, -18f),
                    new CoopVec(-6f, -20f), new CoopVec(6f, -20f), new CoopVec(-12f, -20f), new CoopVec(12f, -20f),
                    new CoopVec(-9f, -22f), new CoopVec(9f, -22f), new CoopVec(-15f, -22f), new CoopVec(15f, -22f),
                },
                landmarks = new[]
                {
                    new CoopLandmark { code = "spawn", name = "침입 지점", kind = CoopLandmarkKinds.Spawn, pos = new CoopVec(0f, 40f) },
                    new CoopLandmark { code = "gate", name = "도시 관문", kind = CoopLandmarkKinds.Gate, pos = new CoopVec(0f, -20f) },
                    new CoopLandmark { code = "wall_west", name = "서쪽 방어벽", kind = CoopLandmarkKinds.Wall, pos = new CoopVec(-9f, -21f) },
                    new CoopLandmark { code = "wall_east", name = "동쪽 방어벽", kind = CoopLandmarkKinds.Wall, pos = new CoopVec(9f, -21f) },
                    new CoopLandmark { code = "power_plant", name = "발전소", kind = CoopLandmarkKinds.Facility, pos = new CoopVec(-20f, -12f) },
                    new CoopLandmark { code = "water_plant", name = "정수장", kind = CoopLandmarkKinds.Facility, pos = new CoopVec(20f, -12f) },
                    new CoopLandmark { code = "command_post", name = "방어 지휘소", kind = CoopLandmarkKinds.Facility, pos = new CoopVec(0f, -24f) },
                },
            };
            // 타워 자리: 길에서 4 m 쯤 떨어진 곳에 16개.
            map.slots = new[]
            {
                new CoopVec(4f, 32f), new CoopVec(-4f, 32f),            // 0, 1: 들머리
                new CoopVec(-7f, 28f), new CoopVec(-7f, 20f),           // 2, 3: 첫 굽이
                new CoopVec(-18f, 16f), new CoopVec(-10f, 16f),         // 4, 5: 서쪽 길
                new CoopVec(-7f, 12f), new CoopVec(-7f, 4f),            // 6, 7: 가운데 길 서쪽
                new CoopVec(0f, 12f), new CoopVec(0f, 4f),              // 8, 9: 가운데 길 한복판
                new CoopVec(7f, 12f), new CoopVec(7f, 4f),              // 10, 11: 가운데 길 동쪽
                new CoopVec(18f, 0f), new CoopVec(10f, 0f),             // 12, 13: 동쪽 길
                new CoopVec(7f, -12f), new CoopVec(-4f, -13f),          // 14, 15: 관문 앞
            };
            return map;
        }

        /// <summary>지도 정의가 앞뒤가 맞는지 검사한다. 맞으면 null.</summary>
        public string FindProblem()
        {
            if (path == null || path.Length < 2) return "길이 없음";
            if (slots == null || slots.Length == 0) return "타워 자리가 없음";
            if (playerStarts == null || playerStarts.Length == 0) return "시작 자리가 없음";
            var points = new List<CoopVec>(path);
            points.AddRange(slots);
            points.AddRange(playerStarts);
            foreach (var landmark in landmarks ?? new CoopLandmark[0]) points.Add(landmark.pos);
            foreach (var point in points)
                if (point.x < minX || point.x > maxX || point.z < minZ || point.z > maxZ) return "지도 밖의 점: " + point;
            for (int i = 0; i < slots.Length; i++)
            {
                for (int j = i + 1; j < slots.Length; j++)
                    if (CoopVec.Distance(slots[i], slots[j]) < 2f) return "타워 자리 " + i + ", " + j + " 가 겹침";
                if (DistanceToPath(slots[i]) < 2f) return "타워 자리 " + i + " 가 길 위에 있음";
            }
            return null;
        }

        /// <summary>점에서 길까지의 가장 가까운 거리.</summary>
        public float DistanceToPath(CoopVec p)
        {
            float best = float.MaxValue;
            for (int i = 1; i < path.Length; i++)
            {
                CoopVec a = path[i - 1], b = path[i];
                float dx = b.x - a.x, dz = b.z - a.z;
                float lengthSquared = dx * dx + dz * dz;
                float t = lengthSquared <= 0f ? 0f : Math.Max(0f, Math.Min(1f, ((p.x - a.x) * dx + (p.z - a.z) * dz) / lengthSquared));
                float distance = CoopVec.Distance(p, new CoopVec(a.x + dx * t, a.z + dz * t));
                if (distance < best) best = distance;
            }
            return best;
        }
    }
}
