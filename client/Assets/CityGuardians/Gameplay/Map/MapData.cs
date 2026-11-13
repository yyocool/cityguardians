using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    // tools/map/build-regions.mjs 가 만든 Resources/Data/kr_regions.json 의 모양.
    // 좌표는 정수 칸이고 왼쪽 아래가 (0,0), 북쪽이 +y 이다.

    [Serializable]
    public sealed class MapSido
    {
        public string code;
        public string name;
        public string shortName;
        public int labelX;
        public int labelY;
        /// <summary>윤곽선. [점의 수, x, y, x, y, …] 가 고리마다 이어진다.</summary>
        public int[] lines;
        public int minX, maxX, minY, maxY;
        /// <summary>이 시·도를 눌렀을 때 화면에 맞출 범위. 먼 섬 지역은 빠져 있다.</summary>
        public int focusMinX, focusMaxX, focusMinY, focusMaxY;
    }

    [Serializable]
    public sealed class MapRegion
    {
        public string code;
        public string sido;
        public string name;
        /// <summary>이름표 자리. 항상 도형 안쪽이다.</summary>
        public int labelX;
        public int labelY;
        public int areaKm2;
        /// <summary>꼭짓점 [x, y, x, y, …].</summary>
        public int[] v;
        /// <summary>삼각형. 꼭짓점 번호 세 개씩.</summary>
        public int[] t;
        /// <summary>테두리 고리 [시작 꼭짓점, 점의 수, …].</summary>
        public int[] rings;
        public int minX, minY, maxX, maxY;
    }

    [Serializable]
    public sealed class MapMark
    {
        public string name;
        public int x;
        public int y;
    }

    [Serializable]
    public sealed class MapFile
    {
        /// <summary>지도 화면에 반드시 보여야 하는 출처 문구 (docs/MAP_SYSTEM.md 2장).</summary>
        public string attribution;
        public string baseDate;
        public int width;
        public int height;
        public float unitKm;
        public MapSido[] sidos = new MapSido[0];
        public MapRegion[] regions = new MapRegion[0];
        public MapMark[] marks = new MapMark[0];
    }

    /// <summary>
    /// 지도 도형 데이터. 실행 중에는 가공하지 않고 읽기만 한다.
    /// 어느 지역을 가리키는지의 판정도 여기서 한다 (화면 좌표가 아니라 지도 좌표로).
    /// </summary>
    public sealed class MapData
    {
        public const string ResourcePath = "Data/kr_regions";

        private static MapData _instance;

        /// <summary>처음 쓸 때 읽는다. 데이터가 없으면 빈 지도를 돌려주고 오류를 남긴다.</summary>
        public static MapData Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var asset = Resources.Load<TextAsset>(ResourcePath);
                if (asset == null)
                {
                    Debug.LogError($"지도 데이터 Resources/{ResourcePath}.json 을 찾을 수 없습니다. tools/map 에서 npm run build 를 실행하세요.");
                    _instance = new MapData(new MapFile());
                }
                else
                {
                    _instance = new MapData(JsonUtility.FromJson<MapFile>(asset.text));
                }
                return _instance;
            }
        }

        public MapFile File { get; }
        public IReadOnlyList<MapSido> Sidos => File.sidos;
        public IReadOnlyList<MapRegion> Regions => File.regions;
        public bool IsEmpty => File.regions.Length == 0;

        private readonly Dictionary<string, MapRegion> _regionByCode = new Dictionary<string, MapRegion>();
        private readonly Dictionary<string, MapSido> _sidoByCode = new Dictionary<string, MapSido>();
        private readonly Dictionary<string, int> _sidoIndex = new Dictionary<string, int>();

        public MapData(MapFile file)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
            for (int i = 0; i < file.sidos.Length; i++)
            {
                _sidoByCode[file.sidos[i].code] = file.sidos[i];
                _sidoIndex[file.sidos[i].code] = i;
            }
            for (int i = 0; i < file.regions.Length; i++) _regionByCode[file.regions[i].code] = file.regions[i];
        }

        /// <summary>없으면 null.</summary>
        public MapRegion FindRegion(string code) =>
            code != null && _regionByCode.TryGetValue(code, out MapRegion region) ? region : null;

        /// <summary>없으면 null.</summary>
        public MapSido FindSido(string code) =>
            code != null && _sidoByCode.TryGetValue(code, out MapSido sido) ? sido : null;

        /// <summary>목록에서의 순서. 색을 고르는 데 쓴다. 없으면 -1.</summary>
        public int IndexOfSido(string code) =>
            code != null && _sidoIndex.TryGetValue(code, out int index) ? index : -1;

        /// <summary>
        /// 그 자리의 지역. 없으면 null. slop이 0보다 크면, 정확히 닿은 곳이 없을 때
        /// 그 거리 안의 가까운 지역을 찾는다 (작은 섬이나 좁은 지역을 누르기 쉽게).
        /// </summary>
        /// <param name="onlySido">이 시·도의 지역만 찾는다. null이면 전체.</param>
        public MapRegion RegionAt(Vector2 point, float slop = 0f, string onlySido = null)
        {
            MapRegion exact = RegionAtExact(point, onlySido);
            if (exact != null || slop <= 0f) return exact;

            // 가까운 쪽부터 두 겹으로 둘러본다.
            for (int ring = 1; ring <= 2; ring++)
            {
                float radius = slop * ring * 0.5f;
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * Mathf.PI / 4f;
                    var probe = new Vector2(point.x + Mathf.Cos(angle) * radius, point.y + Mathf.Sin(angle) * radius);
                    MapRegion near = RegionAtExact(probe, onlySido);
                    if (near != null) return near;
                }
            }
            return null;
        }

        private MapRegion RegionAtExact(Vector2 point, string onlySido)
        {
            MapRegion[] regions = File.regions;
            for (int i = 0; i < regions.Length; i++)
            {
                MapRegion region = regions[i];
                if (point.x < region.minX || point.x > region.maxX || point.y < region.minY || point.y > region.maxY) continue;
                if (onlySido != null && region.sido != onlySido) continue;
                if (Contains(region, point)) return region;
            }
            return null;
        }

        public static bool Contains(MapRegion region, Vector2 point)
        {
            int[] v = region.v;
            int[] t = region.t;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int a = t[i] * 2, b = t[i + 1] * 2, c = t[i + 2] * 2;
                if (InTriangle(point.x, point.y, v[a], v[a + 1], v[b], v[b + 1], v[c], v[c + 1])) return true;
            }
            return false;
        }

        private static bool InTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            float d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            float d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }

        /// <summary>지도 데이터의 지역이 지역 목록과 어긋나는 곳. 없으면 null. 점검과 테스트에 쓴다.</summary>
        public string FindMismatch(RegionCatalog catalog)
        {
            if (File.regions.Length != catalog.Regions.Count)
                return $"지역 수가 다릅니다: 지도 {File.regions.Length}, 목록 {catalog.Regions.Count}";
            if (File.sidos.Length != catalog.Sidos.Count)
                return $"시·도 수가 다릅니다: 지도 {File.sidos.Length}, 목록 {catalog.Sidos.Count}";
            for (int i = 0; i < File.regions.Length; i++)
            {
                MapRegion region = File.regions[i];
                RegionDef def = catalog.FindRegion(region.code);
                if (def == null) return $"목록에 없는 지역: {region.code} {region.name}";
                if (def.name != region.name || def.sidoCode != region.sido)
                    return $"이름이나 시·도가 다른 지역: {region.code} {region.name}";
            }
            return null;
        }
    }

    /// <summary>지역을 나타내는 색. 지도와 완장이 같은 색을 쓴다.</summary>
    public static class RegionColors
    {
        // 시·도 코드 순서 (11 서울 … 52 전북). 이웃한 시·도끼리 구별되게 골랐다.
        private static readonly Dictionary<string, Color> SidoColors = new Dictionary<string, Color>
        {
            { "11", new Color(0.93f, 0.42f, 0.40f) }, // 서울
            { "12", new Color(0.95f, 0.62f, 0.30f) }, // 전남광주
            { "26", new Color(0.10f, 0.62f, 0.82f) }, // 부산 (해운대 방어대의 바다색)
            { "27", new Color(0.94f, 0.80f, 0.32f) }, // 대구
            { "28", new Color(0.58f, 0.80f, 0.36f) }, // 인천
            { "30", new Color(0.36f, 0.76f, 0.52f) }, // 대전
            { "31", new Color(0.40f, 0.78f, 0.70f) }, // 울산
            { "36", new Color(0.96f, 0.72f, 0.50f) }, // 세종
            { "41", new Color(0.36f, 0.72f, 0.74f) }, // 경기
            { "43", new Color(0.50f, 0.68f, 0.90f) }, // 충북
            { "44", new Color(0.42f, 0.54f, 0.84f) }, // 충남
            { "47", new Color(0.56f, 0.50f, 0.84f) }, // 경북
            { "48", new Color(0.72f, 0.48f, 0.80f) }, // 경남
            { "50", new Color(0.92f, 0.56f, 0.72f) }, // 제주
            { "51", new Color(0.80f, 0.46f, 0.66f) }, // 강원
            { "52", new Color(0.86f, 0.50f, 0.52f) }, // 전북
        };

        private static readonly Color Unknown = new Color(0.62f, 0.64f, 0.68f);

        public static Color OfSido(string sidoCode) =>
            sidoCode != null && SidoColors.TryGetValue(sidoCode, out Color color) ? color : Unknown;

        /// <summary>지역의 색. 지금은 시·도의 색을 쓴다. 지역마다의 문양은 추후.</summary>
        public static Color OfRegion(string regionCode)
        {
            RegionDef region = RegionCatalog.Default.FindRegion(regionCode);
            return region != null ? OfSido(region.sidoCode) : Unknown;
        }
    }
}
