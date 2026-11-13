using System;
using CityGuardians.Domain;
using CityGuardians.Services;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 대표 도시: 부산광역시 해운대구 (docs/MAP_SYSTEM.md 5장).
    /// 실제 지형을 옮긴 것이 아니라 바다·모래사장·고층 건물 윤곽으로 지역의 느낌을 살린 놀이용 도시다.
    /// 북쪽(+Z)에 주거지와 전투 구역, 가운데 광장, 남쪽(-Z)에 바다가 있다.
    /// </summary>
    public sealed class CityBuilder : MonoBehaviour
    {
        private const float CameraDistance = 6f;

        // 걸어 다닐 수 있는 범위
        private const float MinX = -53f;
        private const float MaxX = 53f;
        private const float MinZ = -50f;
        private const float MaxZ = 53f;

        public static readonly Color Sky = new Color(0.56f, 0.79f, 0.96f);
        private static readonly Color SeaColor = Appearance.HaeundaeColor;
        private static readonly Color Pavement = new Color(0.74f, 0.75f, 0.77f);
        private static readonly Color Road = new Color(0.33f, 0.35f, 0.39f);
        private static readonly Color Sand = new Color(0.95f, 0.87f, 0.66f);
        private static readonly Color Grass = new Color(0.47f, 0.74f, 0.42f);

        private static readonly Vector3 HomeSpawn = new Vector3(0f, 0f, 30f);
        private static readonly Vector3 PlazaSpawn = new Vector3(3f, 0f, -6f);
        /// <summary>방어기지 출동 게이트 (문 앞).</summary>
        public static readonly Vector3 DefenseGatePoint = new Vector3(-25.6f, 0f, -9f);
        /// <summary>방어전에서 돌아왔을 때 서는 자리.</summary>
        // 게이트에서 충분히 떨어져야 한다. 가까우면 뒤따르는 카메라가 게이트 가로대 뒤로 들어가 화면이 가려진다.
        private static readonly Vector3 DefenseGateSpawn = new Vector3(-18.5f, 0f, -9f);
        private static readonly Vector3 TutorialRobotSpot = new Vector3(-2f, 0f, 23f);
        /// <summary>알록달록 가게. 앞면(북쪽)은 z = -18.5.</summary>
        private static readonly Vector3 ColorShopCenter = new Vector3(20f, 0f, -21.5f);
        /// <summary>CITY PASS 안내센터. 바다로 가는 길의 동쪽에서 길(서쪽)을 본다. 앞면은 x = 7.</summary>
        public static readonly Vector3 CityPassCenter = new Vector3(10.5f, 0f, -30f);
        /// <summary>장학센터. 바다로 가는 길의 서쪽에서 길(동쪽)을 본다. 앞면은 x = -7.</summary>
        public static readonly Vector3 ScholarshipCenter = new Vector3(-10.5f, 0f, -30f);
        private static readonly Vector3 NorthEastCenter = new Vector3(37f, 0f, 33f);
        private const float BarrierHeight = 0.6f;
        private static readonly Vector3 NorthWestCenter = new Vector3(-37f, 0f, 33f);
        private static readonly Vector2 BattleHalf = new Vector2(13f, 13f);

        private PlayerController _player;
        private HuntZone _homeZone;
        private NpcAgent _dodam;
        private NpcAgent _hanul;
        private NpcAgent _narae;
        private NpcAgent _miso;
        private NpcAgent _daon;
        private NpcAgent _areum;
        private NpcAgent _seoyun;
        private NpcAgent _garam;
        private NpcAgent _sebin;
        private NpcAgent _ruri;
        private ObjectiveMarker _marker;
        private EnemyAgent _tutorialRobot;
        private int _shownStep = -1;
        private int _shownGuide = -1;
        private float _nextCry;
        private float _respawnRobotAt = -1f;

        private void Start()
        {
            if (!SceneGuard.EnsureSession()) return;
            Build();
        }

        private void OnDestroy()
        {
            HuntZone.ReportConfirmed -= OnReportConfirmed;
        }

        private void Build()
        {
            SetupLighting();

            // 움직이지 않는 지형과 건물. 이 아래의 충돌체로 길 찾기 지도를 만든다.
            Transform world = new GameObject("CityStatic").transform;
            BuildGround(world);
            BuildPlaza(world);
            BuildResidential(world);
            BuildEastSide(world);
            BuildWestSide(world);
            BuildBenefitCenters(world);
            BuildBattleZoneScenery(world, NorthEastCenter, "북동 전투 구역", 11);
            BuildBattleZoneScenery(world, NorthWestCenter, "북서 전투 구역", 23);
            BuildSeaside(world);
            BuildBounds(world);

            var surface = world.gameObject.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();

            // 움직이는 것들
            Transform actors = new GameObject("CityActors").transform;
            // 방어전에서 돌아왔으면 출동 게이트 앞에, 그 밖에는 집 앞에 선다.
            bool fromDefense = GameRoot.PendingSpawn == SpawnIds.DefenseGate;
            _player = PlayerFactory.Spawn(fromDefense ? DefenseGateSpawn : HomeSpawn, fromDefense ? 90f : 180f, Sky, CameraDistance);
            _player.respawnPoint = PlazaSpawn;
            _player.respawnYaw = 0f;
            PlayerFactory.Arm(_player);

            BuildNpcs(actors);

            _homeZone = HuntZone.Create(actors, ZoneCodes.HomeStreet, new Vector3(0f, 0f, 24f), new Vector2(9f, 5f), false);
            _homeZone.reportInterval = 0.2f;
            HuntZone.Create(actors, ZoneCodes.NorthEast, NorthEastCenter, BattleHalf - Vector2.one * 2f, true);
            HuntZone.Create(actors, ZoneCodes.NorthWest, NorthWestCenter, BattleHalf - Vector2.one * 2f, true);
            HuntZone.ReportConfirmed += OnReportConfirmed;

            // 헌트 모드: 사냥 결과가 도시 위험도에 반영된 것을 알리고, 위험한 도시에서는 로봇이 더 나온다.
            actors.gameObject.AddComponent<HuntModeController>();

            _marker = ObjectiveMarker.Create();
            _marker.transform.SetParent(actors, true);
            _marker.transform.localScale = Vector3.one * 1.6f;

            // 방에서 처음 나왔다면 첫 전투가 시작된다.
            GameSession session = GameSession.Current;
            if (session.TutorialStep == 2) session.AdvanceTutorial(3);
        }

        private static void SetupLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.68f, 0.80f, 0.94f);
            RenderSettings.ambientEquatorColor = new Color(0.62f, 0.64f, 0.66f);
            RenderSettings.ambientGroundColor = new Color(0.40f, 0.38f, 0.36f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Sky;
            RenderSettings.fogStartDistance = 70f;
            RenderSettings.fogEndDistance = 300f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.97f, 0.90f);
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(50f, -32f, 0f);
        }

        // ── 지형 ────────────────────────────────────────────────

        private static void BuildGround(Transform world)
        {
            // 걸을 수 있는 땅 (윗면 y = 0)
            Blocks.Box(world, "Ground", new Vector3(0f, -0.5f, 1f), new Vector3(110f, 1f, 108f), Pavement, true);
            // 바깥 풍경: 걸을 수 없는 먼 땅과 바다
            Blocks.Box(world, "FarLand", new Vector3(0f, -0.75f, 150f), new Vector3(600f, 1f, 406f), new Color(0.52f, 0.66f, 0.50f));
            Blocks.Box(world, "Sea", new Vector3(0f, -0.65f, -253f), new Vector3(900f, 1f, 400f), SeaColor);

            // 길: 남북 큰길과 동서 큰길, 집 앞 거리
            Flat(world, "RoadNS", new Vector3(0f, 0f, -2f), new Vector2(7f, 62f), Road, 1);
            Flat(world, "RoadEW", new Vector3(0f, 0f, 0f), new Vector2(104f, 7f), Road, 1);
            Flat(world, "HomeStreet", new Vector3(0f, 0f, 26f), new Vector2(60f, 6f), Road, 1);
            for (int i = -6; i <= 6; i++)
            {
                if (Mathf.Abs(i) < 2) continue;
                Flat(world, "DashEW", new Vector3(i * 7.5f, 0f, 0f), new Vector2(2.2f, 0.25f), new Color(1f, 0.95f, 0.7f), 2, true);
            }
            for (int i = -3; i <= 2; i++)
            {
                if (i == 0 || i == -1) continue;
                Flat(world, "DashNS", new Vector3(0f, 0f, i * 8f + 4f), new Vector2(0.25f, 2.2f), new Color(1f, 0.95f, 0.7f), 2, true);
            }
            for (int i = -3; i <= 3; i++)
                Flat(world, "DashHome", new Vector3(i * 8f, 0f, 26f), new Vector2(2f, 0.2f), new Color(1f, 0.95f, 0.7f), 2, true);

            // 풀밭과 나무
            Flat(world, "GrassNE", new Vector3(20f, 0f, 14f), new Vector2(26f, 10f), Grass, 1);
            Flat(world, "GrassNW", new Vector3(-20f, 0f, 14f), new Vector2(26f, 10f), Grass, 1);
            Flat(world, "GrassSE", new Vector3(22f, 0f, -22f), new Vector2(30f, 8f), Grass, 1);
            Flat(world, "GrassSW", new Vector3(-22f, 0f, -22f), new Vector2(30f, 8f), Grass, 1);
            Vector3[] trees =
            {
                new Vector3(10f, 0f, 12f), new Vector3(18f, 0f, 16f), new Vector3(23f, 0f, 11f),
                new Vector3(-10f, 0f, 12f), new Vector3(-18f, 0f, 16f), new Vector3(-23f, 0f, 11f),
                new Vector3(12f, 0f, -21f), new Vector3(30f, 0f, -23f), new Vector3(-12f, 0f, -21f),
                new Vector3(-30f, 0f, -23f), new Vector3(20f, 0f, 30f), new Vector3(-20f, 0f, 30f),
            };
            for (int i = 0; i < trees.Length; i++) Tree(world, trees[i], 1f + (i % 3) * 0.15f);

            Vector3[] lamps =
            {
                new Vector3(5f, 0f, 12f), new Vector3(-5f, 0f, 12f), new Vector3(5f, 0f, 22f), new Vector3(-5f, 0f, 22f),
                new Vector3(14f, 0f, 5f), new Vector3(-14f, 0f, 5f), new Vector3(14f, 0f, -5f), new Vector3(-14f, 0f, -5f),
                new Vector3(5f, 0f, -18f), new Vector3(-5f, 0f, -18f),
            };
            foreach (Vector3 lamp in lamps) Lamp(world, lamp);
        }

        /// <summary>바닥에 깔리는 얇은 판. 충돌체가 없어 걷는 데 영향을 주지 않는다.</summary>
        private static GameObject Flat(Transform parent, string name, Vector3 center, Vector2 size, Color color,
            int layerIndex, bool unlit = false)
        {
            // 층마다 2cm씩 띄워, 멀리서 봐도 겹친 면이 깜빡이지 않게 한다.
            float y = 0.02f * layerIndex;
            return Blocks.Box(parent, name, new Vector3(center.x, y * 0.5f, center.z), new Vector3(size.x, y, size.y), color, false, unlit);
        }

        private static void Tree(Transform parent, Vector3 position, float scale)
        {
            Transform tree = Blocks.Pivot(parent, "Tree", position);
            Blocks.Box(tree, "Trunk", new Vector3(0f, 0.9f * scale, 0f), new Vector3(0.45f, 1.8f * scale, 0.45f),
                new Color(0.50f, 0.36f, 0.24f), true);
            Blocks.Box(tree, "Leaves", new Vector3(0f, 2.5f * scale, 0f), new Vector3(2.2f, 1.6f, 2.2f) * scale, new Color(0.32f, 0.66f, 0.36f));
            Blocks.Box(tree, "LeavesTop", new Vector3(0f, 3.5f * scale, 0f), new Vector3(1.4f, 0.9f, 1.4f) * scale, new Color(0.40f, 0.74f, 0.42f));
        }

        private static void Lamp(Transform parent, Vector3 position)
        {
            Transform lamp = Blocks.Pivot(parent, "Lamp", position);
            Blocks.Box(lamp, "Pole", new Vector3(0f, 1.6f, 0f), new Vector3(0.14f, 3.2f, 0.14f), new Color(0.25f, 0.28f, 0.32f), true);
            Blocks.Box(lamp, "Head", new Vector3(0f, 3.3f, 0f), new Vector3(0.5f, 0.16f, 0.5f), new Color(0.25f, 0.28f, 0.32f));
            Blocks.Box(lamp, "Bulb", new Vector3(0f, 3.18f, 0f), new Vector3(0.34f, 0.1f, 0.34f), new Color(1f, 0.96f, 0.75f), false, true);
        }

        // ── 건물 ────────────────────────────────────────────────

        /// <summary>
        /// 상자 모양 건물. yaw가 0이면 앞면이 남쪽(-Z)을 본다.
        /// 돌려준 Transform은 건물 바닥 중심이며, 앞면은 그 기준에서 z = -depth/2 이다.
        /// </summary>
        private static Transform Building(Transform parent, string name, Vector3 center, float yaw,
            float width, float height, float depth, Color wall, Color roof, Color door, string sign, int windowRows)
        {
            Transform b = Blocks.Pivot(parent, name, center);
            b.localRotation = Quaternion.Euler(0f, yaw, 0f);
            float front = -depth * 0.5f;

            Blocks.Box(b, "Walls", new Vector3(0f, height * 0.5f, 0f), new Vector3(width, height, depth), wall, true);
            Blocks.Box(b, "Roof", new Vector3(0f, height + 0.2f, 0f), new Vector3(width + 0.6f, 0.4f, depth + 0.6f), roof);
            Blocks.Box(b, "Door", new Vector3(0f, 1.1f, front - 0.03f), new Vector3(1.5f, 2.2f, 0.08f), door);
            Blocks.Box(b, "Step", new Vector3(0f, 0.06f, front - 0.5f), new Vector3(2.2f, 0.12f, 1.0f), new Color(0.6f, 0.6f, 0.62f));

            Color glass = new Color(0.70f, 0.90f, 1f);
            float rowHeight = (height - 3.2f) / Mathf.Max(1, windowRows);
            for (int row = 0; row < windowRows; row++)
            {
                float y = 3.4f + row * rowHeight + Mathf.Min(0.6f, rowHeight * 0.3f);
                for (float x = -width * 0.5f + 1.3f; x <= width * 0.5f - 1.2f; x += 2.2f)
                    Blocks.Box(b, "Window", new Vector3(x, y, front - 0.02f), new Vector3(1.1f, Mathf.Min(1.1f, rowHeight * 0.55f), 0.05f), glass, false, true);
            }
            // 1층 창문 (문 양옆)
            foreach (float x in new[] { -width * 0.5f + 1.6f, width * 0.5f - 1.6f })
                if (Mathf.Abs(x) > 1.8f)
                    Blocks.Box(b, "WindowLow", new Vector3(x, 1.5f, front - 0.02f), new Vector3(1.4f, 1.2f, 0.05f), glass, false, true);

            if (!string.IsNullOrEmpty(sign))
            {
                Blocks.Box(b, "SignBoard", new Vector3(0f, 2.95f, front - 0.12f), new Vector3(Mathf.Min(width - 1f, 6f), 0.9f, 0.16f),
                    new Color(0.16f, 0.2f, 0.3f));
                WorldLabel.Create(b, new Vector3(0f, 2.95f, front - 0.21f), sign, 0.55f, Color.white, null, 6f, false);
            }
            return b;
        }

        private void BuildPlaza(Transform world)
        {
            // 광장 바닥: 파도 무늬의 고리
            Blocks.Cylinder(world, "PlazaTile", new Vector3(0f, 0.03f, 0f), new Vector3(24f, 0.06f, 24f), new Color(0.90f, 0.86f, 0.76f));
            Blocks.Cylinder(world, "PlazaRing", new Vector3(0f, 0.04f, 0f), new Vector3(16f, 0.08f, 16f), SeaColor);
            Blocks.Cylinder(world, "PlazaInner", new Vector3(0f, 0.05f, 0f), new Vector3(14f, 0.10f, 14f), new Color(0.94f, 0.91f, 0.82f));

            // 가운데 조형물: 층층이 밀려오는 파도
            Blocks.Cylinder(world, "Pedestal", new Vector3(0f, 0.3f, 0f), new Vector3(4.4f, 0.6f, 4.4f), new Color(0.82f, 0.82f, 0.84f), true);
            Color[] waves = { new Color(0.08f, 0.50f, 0.74f), SeaColor, new Color(0.45f, 0.82f, 0.93f) };
            for (int i = 0; i < 3; i++)
            {
                Blocks.Box(world, "Wave", new Vector3(-0.9f + i * 0.9f, 1.0f + i * 0.55f, 0f), new Vector3(1.0f, 0.9f + i * 1.1f, 1.4f), waves[i]);
                Blocks.Box(world, "WaveFoam", new Vector3(-0.9f + i * 0.9f, 1.5f + i * 1.1f, 0f), new Vector3(1.05f, 0.14f, 1.45f), Color.white, false, true);
            }

            // 지역 이름 표지판 (앞뒤 양면)
            Blocks.Box(world, "NamePoleL", new Vector3(-2.4f, 2.3f, -3.2f), new Vector3(0.18f, 4.6f, 0.18f), new Color(0.25f, 0.28f, 0.32f), true);
            Blocks.Box(world, "NamePoleR", new Vector3(2.4f, 2.3f, -3.2f), new Vector3(0.18f, 4.6f, 0.18f), new Color(0.25f, 0.28f, 0.32f), true);
            Blocks.Box(world, "NameBoard", new Vector3(0f, 4.0f, -3.2f), new Vector3(5.4f, 1.3f, 0.2f), new Color(0.06f, 0.36f, 0.56f));
            Blocks.Box(world, "NameStripe", new Vector3(0f, 3.42f, -3.2f), new Vector3(5.4f, 0.12f, 0.22f), Color.white, false, true);
            WorldLabel.Create(world, new Vector3(0f, 4.05f, -3.32f), "해운대구", 0.8f, Color.white, null, 5.4f, false);
            WorldLabel back = WorldLabel.Create(world, new Vector3(0f, 4.05f, -3.08f), "해운대구", 0.8f, Color.white, null, 5.4f, false);
            back.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            // 긴 의자
            foreach (Vector3 spot in new[] { new Vector3(7.5f, 0f, 6f), new Vector3(-7.5f, 0f, 6f), new Vector3(7.5f, 0f, -6f), new Vector3(-7.5f, 0f, -6f) })
            {
                Blocks.Box(world, "BenchSeat", spot + new Vector3(0f, 0.42f, 0f), new Vector3(2.0f, 0.12f, 0.6f), new Color(0.66f, 0.48f, 0.32f), true);
                Blocks.Box(world, "BenchLegs", spot + new Vector3(0f, 0.2f, 0f), new Vector3(1.7f, 0.4f, 0.4f), new Color(0.3f, 0.32f, 0.36f));
            }
        }

        private void BuildResidential(Transform world)
        {
            Color[] walls = { new Color(0.98f, 0.90f, 0.72f), new Color(0.99f, 0.96f, 0.88f), new Color(0.86f, 0.92f, 0.82f) };
            Color[] roofs = { new Color(0.86f, 0.42f, 0.34f), new Color(0.24f, 0.56f, 0.78f), new Color(0.48f, 0.66f, 0.40f) };
            float[] xs = { -12f, 0f, 12f };
            for (int i = 0; i < xs.Length; i++)
            {
                bool mine = i == 1;
                Transform house = Building(world, mine ? "MyHome" : "House", new Vector3(xs[i], 0f, 40f), 0f,
                    8f, 4.6f, 7f, walls[i], roofs[i], mine ? new Color(0.40f, 0.62f, 0.52f) : new Color(0.55f, 0.40f, 0.30f),
                    mine ? "내 집" : null, 0);
                // 박공지붕 느낌의 윗단
                Blocks.Box(house, "RoofTop", new Vector3(0f, 5.3f, 0f), new Vector3(6.2f, 0.9f, 5.2f), roofs[i]);
                // 집 앞 길
                Flat(world, "Path", new Vector3(xs[i], 0f, 32.6f), new Vector2(2.2f, 7.2f), new Color(0.86f, 0.84f, 0.80f), 1);
            }

            // 내 집 문: 방으로 돌아간다
            var door = new GameObject("HomeDoorInteract");
            door.transform.SetParent(world, false);
            door.transform.localPosition = new Vector3(0f, 0f, 35.9f);
            Interactable.Add(door, "집으로 들어가기", () => GameRoot.Instance.LoadScene(SceneNames.Room, SpawnIds.RoomDoor), 1.8f);

            // 울타리
            foreach (float x in new[] { -6f, 6f })
                Blocks.Box(world, "Fence", new Vector3(x, 0.45f, 36.2f), new Vector3(3.2f, 0.9f, 0.2f), Color.white, true);
        }

        private void BuildEastSide(Transform world)
        {
            Transform shop = Building(world, "Shop", new Vector3(31f, 0f, 8f), 90f, 10f, 5f, 9f,
                new Color(1f, 0.86f, 0.62f), new Color(0.92f, 0.45f, 0.30f), new Color(0.50f, 0.32f, 0.22f), "미소 상점", 0);
            // 차양
            Blocks.Box(shop, "Awning", new Vector3(0f, 2.35f, -5.3f), new Vector3(8.6f, 0.16f, 1.8f), new Color(0.95f, 0.36f, 0.32f));
            Blocks.Box(shop, "AwningStripe", new Vector3(0f, 2.36f, -6.0f), new Vector3(8.6f, 0.17f, 0.4f), Color.white);
            // 닫힌 셔터: 아직 영업 전
            Blocks.Box(shop, "Shutter", new Vector3(0f, 1.15f, -4.56f), new Vector3(1.6f, 2.3f, 0.06f), new Color(0.62f, 0.64f, 0.68f));

            Transform bank = Building(world, "Bank", new Vector3(31f, 0f, -9f), 90f, 11f, 7.5f, 9f,
                new Color(0.90f, 0.92f, 0.95f), new Color(0.20f, 0.36f, 0.56f), new Color(0.20f, 0.30f, 0.44f), "도시은행", 1);
            // 기둥
            foreach (float x in new[] { -3.6f, -1.5f, 1.5f, 3.6f })
                Blocks.Box(bank, "Column", new Vector3(x, 1.4f, -4.9f), new Vector3(0.5f, 2.8f, 0.5f), Color.white, true);

            // 알록달록 가게 (남동쪽 풀밭, 북쪽을 본다): 옷과 가구를 판다
            Transform color = Building(world, "ColorShop", ColorShopCenter, 180f, 9f, 4.6f, 6f,
                new Color(1f, 0.95f, 0.86f), new Color(0.56f, 0.40f, 0.78f), new Color(0.95f, 0.55f, 0.65f), "알록달록 가게", 0);
            Color[] stripes =
            {
                new Color(0.95f, 0.36f, 0.32f), new Color(1f, 0.80f, 0.22f), new Color(0.40f, 0.78f, 0.52f),
                new Color(0.30f, 0.62f, 0.90f), new Color(0.62f, 0.42f, 0.86f),
            };
            for (int i = 0; i < stripes.Length; i++)
                Blocks.Box(color, "Awning", new Vector3(-3.4f + i * 1.7f, 2.35f, -3.8f), new Vector3(1.7f, 0.16f, 1.6f), stripes[i]);
            // 가게 앞 진열: 작은 소파와 화분
            Blocks.Box(color, "ShowSofaSeat", new Vector3(-3.1f, 0.25f, -3.7f), new Vector3(1.4f, 0.5f, 0.7f), new Color(0.95f, 0.55f, 0.65f), true);
            Blocks.Box(color, "ShowSofaBack", new Vector3(-3.1f, 0.6f, -3.42f), new Vector3(1.4f, 0.5f, 0.16f), new Color(0.86f, 0.42f, 0.55f));
            Blocks.Cylinder(color, "ShowPot", new Vector3(3.2f, 0.25f, -3.7f), new Vector3(0.5f, 0.5f, 0.5f), new Color(0.85f, 0.50f, 0.35f), true);
            Blocks.Box(color, "ShowPlant", new Vector3(3.2f, 0.85f, -3.7f), new Vector3(0.6f, 0.7f, 0.6f), new Color(0.30f, 0.70f, 0.40f));
        }

        private void BuildWestSide(Transform world)
        {
            Building(world, "CommunityCenter", new Vector3(-31f, 0f, 8f), -90f, 10f, 6f, 9f,
                new Color(0.84f, 0.90f, 0.96f), new Color(0.30f, 0.62f, 0.56f), new Color(0.24f, 0.44f, 0.42f), "주민센터", 1);

            Transform fort = Building(world, "DefenseBase", new Vector3(-31f, 0f, -9f), -90f, 11f, 5f, 9f,
                new Color(0.46f, 0.54f, 0.48f), new Color(0.30f, 0.36f, 0.32f), new Color(0.22f, 0.26f, 0.24f), "방어기지", 0);
            // 출동 게이트: 문 앞의 빛나는 틀. 여기서 방어전에 나선다.
            Color gateGlow = new Color(0.45f, 0.95f, 1f);
            Blocks.Box(fort, "GatePostL", new Vector3(-1.3f, 1.2f, -5.2f), new Vector3(0.22f, 2.4f, 0.22f), new Color(0.22f, 0.26f, 0.24f));
            Blocks.Box(fort, "GatePostR", new Vector3(1.3f, 1.2f, -5.2f), new Vector3(0.22f, 2.4f, 0.22f), new Color(0.22f, 0.26f, 0.24f));
            Blocks.Box(fort, "GateBeam", new Vector3(0f, 2.3f, -5.2f), new Vector3(2.9f, 0.5f, 0.26f), new Color(0.16f, 0.2f, 0.3f));
            Blocks.Box(fort, "GateGlow", new Vector3(0f, 2.02f, -5.2f), new Vector3(2.9f, 0.06f, 0.28f), gateGlow, false, true);
            Blocks.Cylinder(fort, "GatePad", new Vector3(0f, 0.14f, -5.5f), new Vector3(1.8f, 0.04f, 1.8f), gateGlow, false, true);
            WorldLabel.Create(fort, new Vector3(0f, 2.3f, -5.35f), "출동 게이트", 0.3f, Color.white, null, 2.9f, false);
            Transform gate = Blocks.Pivot(fort, "DefenseGateInteract", new Vector3(0f, 0f, -5.4f));
            Interactable.Add(gate.gameObject, "방어기지", OpenDefenseMenu, 2.0f,
                () => GameSession.Current != null && GameSession.Current.TutorialStep >= 6 && !GameRoot.Instance.IsBusy);

            // 작전 단말기 (v8.0): 게이트 왼쪽 벽에 붙은 화면. 긴급 출동, 타워 연구, 전투 장비, 도시 방어 현황.
            // 충돌체가 없어 길 찾기와 걷는 길에 영향을 주지 않는다. 게이트에서 3 m쯤 떨어져 있어 안내가 겹치지 않는다.
            Transform terminal = Blocks.Pivot(fort, "CommandTerminal", new Vector3(-3.0f, 0f, -4.75f));
            Blocks.Box(terminal, "Stand", new Vector3(0f, 0.55f, 0f), new Vector3(0.9f, 1.1f, 0.4f), new Color(0.22f, 0.26f, 0.24f));
            Blocks.Box(terminal, "Screen", new Vector3(0f, 1.45f, -0.05f), new Vector3(1.3f, 0.8f, 0.12f), new Color(0.16f, 0.2f, 0.3f));
            Blocks.Box(terminal, "ScreenGlow", new Vector3(0f, 1.45f, -0.12f), new Vector3(1.14f, 0.64f, 0.02f), new Color(1f, 0.55f, 0.35f), false, true);
            WorldLabel.Create(terminal, new Vector3(0f, 2.15f, -0.1f), "작전 단말기", 0.3f, Color.white, null, 2.4f, false);
            Transform terminalSpot = Blocks.Pivot(terminal, "CommandTerminalInteract", new Vector3(0f, 0f, -0.9f));
            Interactable.Add(terminalSpot.gameObject, "작전 본부", OpenCommandMenu, 1.7f,
                () => GameSession.Current != null && GameSession.Current.TutorialStep >= 6 && !GameRoot.Instance.IsBusy);

            // 망루
            Blocks.Box(fort, "Tower", new Vector3(3.6f, 6.4f, 2.4f), new Vector3(2.6f, 2.8f, 2.6f), new Color(0.40f, 0.48f, 0.42f));
            Blocks.Box(fort, "TowerTop", new Vector3(3.6f, 8.0f, 2.4f), new Vector3(3.2f, 0.4f, 3.2f), new Color(0.30f, 0.36f, 0.32f));
            Blocks.Box(fort, "Flag", new Vector3(3.6f, 9.4f, 2.4f), new Vector3(0.1f, 2.4f, 0.1f), new Color(0.2f, 0.2f, 0.22f));
            Blocks.Box(fort, "FlagCloth", new Vector3(4.2f, 10.1f, 2.4f), new Vector3(1.2f, 0.7f, 0.06f), SeaColor, false, true);
            foreach (float x in new[] { -4.2f, 4.2f })
            {
                Blocks.Box(fort, "Barricade", new Vector3(x, 0.5f, -6.6f), new Vector3(2.6f, 1.0f, 0.6f), new Color(0.95f, 0.75f, 0.2f), true);
                Blocks.Box(fort, "BarricadeStripe", new Vector3(x, 0.5f, -6.91f), new Vector3(2.6f, 0.3f, 0.02f), new Color(0.15f, 0.15f, 0.18f), false, true);
            }
        }

        /// <summary>
        /// 현실 혜택 안내센터 두 곳 (v7.0). 바다로 가는 길의 양쪽에 마주 보고 선다.
        /// 동쪽은 CITY PASS 안내센터, 서쪽은 장학센터. 안에는 들어가지 않고, 문 앞의 안내원에게 말을 건다.
        /// 여기서는 안내만 한다: GC 를 쓰거나 받는 창구가 아니다.
        /// </summary>
        private void BuildBenefitCenters(Transform world)
        {
            // CITY PASS 안내센터 (서쪽을 본다): 민트색 벽과 청록 지붕
            Color teal = new Color(0.16f, 0.62f, 0.62f);
            Transform pass = Building(world, "CityPassCenter", CityPassCenter, 90f, 8f, 5f, 7f,
                new Color(0.90f, 0.97f, 0.95f), teal, new Color(0.12f, 0.42f, 0.44f), "CITY PASS 안내센터", 0);
            Blocks.Box(pass, "Awning", new Vector3(0f, 2.35f, -4.2f), new Vector3(6.6f, 0.16f, 1.4f), teal);
            Blocks.Box(pass, "AwningStripe", new Vector3(0f, 2.36f, -4.75f), new Vector3(6.6f, 0.17f, 0.3f), Color.white);
            // 문 옆의 혜택 안내판: 화면 모양. 충돌체가 없어 걷는 길을 막지 않는다.
            Transform passBoard = Blocks.Pivot(pass, "OfferBoard", new Vector3(2.9f, 0f, -3.75f));
            Blocks.Box(passBoard, "Stand", new Vector3(0f, 0.55f, 0f), new Vector3(0.9f, 1.1f, 0.3f), new Color(0.12f, 0.42f, 0.44f));
            Blocks.Box(passBoard, "Screen", new Vector3(0f, 1.5f, -0.04f), new Vector3(1.5f, 0.9f, 0.12f), new Color(0.16f, 0.2f, 0.3f));
            Blocks.Box(passBoard, "ScreenGlow", new Vector3(0f, 1.5f, -0.11f), new Vector3(1.34f, 0.74f, 0.02f), new Color(0.55f, 0.95f, 0.88f), false, true);
            WorldLabel.Create(passBoard, new Vector3(0f, 2.2f, -0.1f), "제휴 혜택 안내", 0.3f, Color.white, null, 2.4f, false);

            // 장학센터 (동쪽을 본다): 크림색 벽과 남색 지붕, 위에는 작은 시계탑
            Color navy = new Color(0.20f, 0.28f, 0.52f);
            Transform school = Building(world, "ScholarshipCenter", ScholarshipCenter, -90f, 8f, 5f, 7f,
                new Color(0.99f, 0.95f, 0.84f), navy, new Color(0.36f, 0.26f, 0.20f), "장학센터", 0);
            Blocks.Box(school, "ClockTower", new Vector3(0f, 6.1f, -1.6f), new Vector3(2.0f, 1.8f, 2.0f), new Color(0.99f, 0.95f, 0.84f));
            Blocks.Box(school, "ClockTowerRoof", new Vector3(0f, 7.2f, -1.6f), new Vector3(2.5f, 0.4f, 2.5f), navy);
            Blocks.Cylinder(school, "ClockFace", new Vector3(0f, 6.1f, -2.62f), new Vector3(1.1f, 0.03f, 1.1f), Color.white, false, true)
                .transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            // 문 옆의 공고 게시판
            Transform notice = Blocks.Pivot(school, "NoticeBoard", new Vector3(-2.9f, 0f, -3.75f));
            Blocks.Box(notice, "PostL", new Vector3(-0.8f, 0.9f, 0f), new Vector3(0.12f, 1.8f, 0.12f), new Color(0.36f, 0.26f, 0.20f));
            Blocks.Box(notice, "PostR", new Vector3(0.8f, 0.9f, 0f), new Vector3(0.12f, 1.8f, 0.12f), new Color(0.36f, 0.26f, 0.20f));
            Blocks.Box(notice, "Board", new Vector3(0f, 1.4f, 0f), new Vector3(1.8f, 1.0f, 0.1f), new Color(0.30f, 0.48f, 0.36f));
            Blocks.Box(notice, "Paper1", new Vector3(-0.4f, 1.45f, -0.06f), new Vector3(0.5f, 0.6f, 0.02f), Color.white, false, true);
            Blocks.Box(notice, "Paper2", new Vector3(0.35f, 1.4f, -0.06f), new Vector3(0.55f, 0.5f, 0.02f), new Color(1f, 0.95f, 0.75f), false, true);
            WorldLabel.Create(notice, new Vector3(0f, 2.2f, -0.1f), "모집 공고", 0.3f, Color.white, null, 2.4f, false);
        }

        /// <summary>전투 구역의 겉모습: 경고색 바닥, 울타리, 숨을 곳이 되는 잔해.</summary>
        private static void BuildBattleZoneScenery(Transform world, Vector3 center, string label, int seed)
        {
            Vector2 half = BattleHalf;
            Flat(world, "ZoneGround", center, half * 2f, new Color(0.50f, 0.44f, 0.44f), 1);
            Color yellow = new Color(1f, 0.80f, 0.15f);
            Flat(world, "ZoneEdgeN", center + new Vector3(0f, 0f, half.y), new Vector2(half.x * 2f, 0.5f), yellow, 2, true);
            Flat(world, "ZoneEdgeS", center + new Vector3(0f, 0f, -half.y), new Vector2(half.x * 2f, 0.5f), yellow, 2, true);
            Flat(world, "ZoneEdgeE", center + new Vector3(half.x, 0f, 0f), new Vector2(0.5f, half.y * 2f), yellow, 2, true);
            Flat(world, "ZoneEdgeW", center + new Vector3(-half.x, 0f, 0f), new Vector2(0.5f, half.y * 2f), yellow, 2, true);

            // 울타리: 군데군데 틈이 있어 어디로든 드나들 수 있다.
            // 캐릭터의 어깨(약 0.8 m)보다 낮아야 한다. 그래야 밖에서 보이는 로봇을 울타리 너머로 쏠 수 있다.
            Color barrier = new Color(0.92f, 0.52f, 0.18f);
            for (float t = -half.x + 3f; t <= half.x - 3f; t += 8.5f)
            {
                Blocks.Box(world, "Barrier", center + new Vector3(t, BarrierHeight * 0.5f, half.y + 0.5f), new Vector3(3.4f, BarrierHeight, 0.5f), barrier, true);
                Blocks.Box(world, "Barrier", center + new Vector3(t, BarrierHeight * 0.5f, -half.y - 0.5f), new Vector3(3.4f, BarrierHeight, 0.5f), barrier, true);
            }
            for (float t = -half.y + 3f; t <= half.y - 3f; t += 8.5f)
            {
                Blocks.Box(world, "Barrier", center + new Vector3(half.x + 0.5f, BarrierHeight * 0.5f, t), new Vector3(0.5f, BarrierHeight, 3.4f), barrier, true);
                Blocks.Box(world, "Barrier", center + new Vector3(-half.x - 0.5f, BarrierHeight * 0.5f, t), new Vector3(0.5f, BarrierHeight, 3.4f), barrier, true);
            }

            // 잔해: 같은 씨앗이면 늘 같은 자리에 놓인다
            var rng = new System.Random(seed);
            Color[] crate = { new Color(0.62f, 0.50f, 0.36f), new Color(0.44f, 0.48f, 0.54f), new Color(0.56f, 0.34f, 0.30f) };
            for (int i = 0; i < 7; i++)
            {
                float x = ((float)rng.NextDouble() * 2f - 1f) * (half.x - 3f);
                float z = ((float)rng.NextDouble() * 2f - 1f) * (half.y - 3f);
                float w = 1.0f + (float)rng.NextDouble() * 1.4f;
                float h = 0.8f + (float)rng.NextDouble() * 1.0f;
                Blocks.Box(world, "Rubble", center + new Vector3(x, h * 0.5f, z), new Vector3(w, h, 1.0f + (float)rng.NextDouble()),
                    crate[i % crate.Length], true);
            }

            // 멀리서도 보이는 이름표
            Vector3 toPlaza = (Vector3.zero - center).normalized;
            Vector3 signSpot = center + new Vector3(Mathf.Sign(toPlaza.x) * (half.x + 1.5f), 0f, -half.y - 1.5f);
            Blocks.Box(world, "ZonePole", signSpot + new Vector3(0f, 2f, 0f), new Vector3(0.2f, 4f, 0.2f), new Color(0.25f, 0.28f, 0.32f), true);
            Blocks.Box(world, "ZoneLight", signSpot + new Vector3(0f, 4.15f, 0f), new Vector3(0.45f, 0.3f, 0.45f), new Color(1f, 0.3f, 0.2f), false, true);
            WorldLabel.Create(world, signSpot + new Vector3(0f, 5.0f, 0f), label, 0.7f, new Color(1f, 0.86f, 0.3f));
        }

        private static void BuildSeaside(Transform world)
        {
            // 바다 쪽으로 늘어선 고층 건물 (북쪽을 본다). 가운데는 바다로 가는 길이라 비워 둔다.
            float[] xs = { -46f, -34f, -22f, 22f, 34f, 46f };
            float[] heights = { 34f, 52f, 40f, 46f, 30f, 56f };
            Color[] tones = { new Color(0.78f, 0.86f, 0.92f), new Color(0.66f, 0.80f, 0.90f), new Color(0.84f, 0.88f, 0.90f) };
            for (int i = 0; i < xs.Length; i++)
                Skyscraper(world, new Vector3(xs[i], 0f, -30f), 9f, heights[i], 8f, tones[i % tones.Length], true);
            // 걸어갈 수 없는 먼 곳의 건물들: 도시가 이어져 보이게 한다
            float[] farXs = { -86f, -70f, 70f, 86f };
            float[] farHeights = { 60f, 44f, 50f, 66f };
            for (int i = 0; i < farXs.Length; i++)
                Skyscraper(world, new Vector3(farXs[i], -0.25f, -32f), 11f, farHeights[i], 10f, tones[(i + 1) % tones.Length], false);
            for (int i = 0; i < 9; i++)
                Skyscraper(world, new Vector3(-88f + i * 22f, -0.25f, 74f + (i % 2) * 8f), 12f, 14f + (i * 7) % 16, 10f, tones[i % tones.Length], false);
            for (int i = 0; i < 4; i++)
            {
                Skyscraper(world, new Vector3(70f + (i % 2) * 9f, -0.25f, -6f + i * 20f), 10f, 16f + (i * 5) % 14, 10f, tones[i % tones.Length], false);
                Skyscraper(world, new Vector3(-70f - (i % 2) * 9f, -0.25f, -6f + i * 20f), 10f, 18f + (i * 9) % 14, 10f, tones[(i + 2) % tones.Length], false);
            }

            // 산책로와 모래사장
            Flat(world, "Promenade", new Vector3(0f, 0f, -36.5f), new Vector2(108f, 4f), new Color(0.74f, 0.60f, 0.44f), 1);
            for (float x = -50f; x <= 50f; x += 10f)
            {
                if (Mathf.Abs(x) < 4f) continue;
                Blocks.Box(world, "RailPost", new Vector3(x, 0.5f, -38.6f), new Vector3(0.16f, 1.0f, 0.16f), Color.white, true);
            }
            Flat(world, "Sand", new Vector3(0f, 0f, -45.5f), new Vector2(108f, 14f), Sand, 1);
            Flat(world, "WetSand", new Vector3(0f, 0f, -51.5f), new Vector2(108f, 2f), new Color(0.84f, 0.76f, 0.58f), 2);

            // 파라솔
            Color[] parasol = { new Color(0.95f, 0.36f, 0.32f), new Color(1f, 0.82f, 0.25f), new Color(0.30f, 0.62f, 0.90f), new Color(0.40f, 0.78f, 0.52f) };
            for (int i = 0; i < 8; i++)
            {
                var spot = new Vector3(-42f + i * 12f, 0f, -43f - (i % 2) * 3f);
                Blocks.Box(world, "ParasolPole", spot + new Vector3(0f, 1.1f, 0f), new Vector3(0.1f, 2.2f, 0.1f), Color.white, true);
                Blocks.Cylinder(world, "ParasolTop", spot + new Vector3(0f, 2.25f, 0f), new Vector3(2.8f, 0.18f, 2.8f), parasol[i % parasol.Length]);
                Blocks.Box(world, "Towel", spot + new Vector3(1.4f, 0.03f, 0.3f), new Vector3(0.9f, 0.03f, 1.8f), parasol[(i + 1) % parasol.Length]);
            }

            // 밀려오는 파도
            for (int i = 0; i < 4; i++)
            {
                GameObject foam = Blocks.Box(world, "Foam", new Vector3(0f, -0.12f, -54.5f - i * 5f), new Vector3(400f, 0.06f, 0.7f),
                    new Color(0.92f, 0.98f, 1f), false, true);
                var wave = foam.AddComponent<WaveStrip>();
                wave.phase = i * 1.3f;
            }
        }

        private static void Skyscraper(Transform parent, Vector3 center, float width, float height, float depth, Color tone, bool collider)
        {
            Transform t = Blocks.Pivot(parent, "Tower", center);
            Blocks.Box(t, "Body", new Vector3(0f, height * 0.5f, 0f), new Vector3(width, height, depth), tone, collider);
            Blocks.Box(t, "Crown", new Vector3(0f, height + 0.8f, 0f), new Vector3(width * 0.6f, 1.6f, depth * 0.6f), tone * 0.9f);
            // 유리창 띠: 북쪽 면과 남쪽 면
            Color glass = new Color(0.36f, 0.62f, 0.82f);
            for (float y = 4f; y < height - 2f; y += 4.2f)
            {
                Blocks.Box(t, "Glass", new Vector3(0f, y, depth * 0.5f + 0.02f), new Vector3(width - 1f, 2.2f, 0.05f), glass, false, true);
                Blocks.Box(t, "Glass", new Vector3(0f, y, -depth * 0.5f - 0.02f), new Vector3(width - 1f, 2.2f, 0.05f), glass, false, true);
            }
        }

        private static void BuildBounds(Transform world)
        {
            float midX = (MinX + MaxX) * 0.5f;
            float midZ = (MinZ + MaxZ) * 0.5f;
            float spanX = MaxX - MinX;
            float spanZ = MaxZ - MinZ;
            Blocks.InvisibleWall(world, "BoundN", new Vector3(midX, 4f, MaxZ + 0.5f), new Vector3(spanX + 2f, 8f, 1f));
            Blocks.InvisibleWall(world, "BoundS", new Vector3(midX, 4f, MinZ - 0.5f), new Vector3(spanX + 2f, 8f, 1f));
            Blocks.InvisibleWall(world, "BoundE", new Vector3(MaxX + 0.5f, 4f, midZ), new Vector3(1f, 8f, spanZ + 2f));
            Blocks.InvisibleWall(world, "BoundW", new Vector3(MinX - 0.5f, 4f, midZ), new Vector3(1f, 8f, spanZ + 2f));
        }

        // ── NPC ─────────────────────────────────────────────────

        private void BuildNpcs(Transform actors)
        {
            var kid = new Appearance
            {
                skin = new Color(1f, 0.86f, 0.72f), hair = new Color(0.20f, 0.14f, 0.10f),
                top = new Color(1f, 0.80f, 0.25f), bottom = new Color(0.30f, 0.45f, 0.70f),
                shoes = new Color(0.9f, 0.3f, 0.3f), armband = new Color(1f, 0.80f, 0.25f), hairStyle = 2,
            };
            _dodam = NpcAgent.CreateHuman(actors, "dodam", new Vector3(5.5f, 0f, 24.5f), 200f, kid, 0.78f);
            _dodam.actionHandler = HandleNpcAction;

            var captain = new Appearance
            {
                skin = new Color(0.90f, 0.72f, 0.56f), hair = new Color(0.14f, 0.14f, 0.16f),
                top = new Color(0.86f, 0.36f, 0.20f), bottom = new Color(0.22f, 0.26f, 0.32f),
                shoes = new Color(0.15f, 0.15f, 0.16f), armband = Appearance.HaeundaeColor, hairStyle = 0,
            };
            _hanul = NpcAgent.CreateHuman(actors, "hanul", new Vector3(-9.5f, 0f, 2.5f), 90f, captain, 1.12f);

            var merchant = new Appearance
            {
                skin = new Color(0.98f, 0.80f, 0.64f), hair = new Color(0.36f, 0.22f, 0.14f),
                top = new Color(0.94f, 0.50f, 0.56f), bottom = new Color(0.36f, 0.30f, 0.26f),
                shoes = new Color(0.3f, 0.2f, 0.16f), armband = Appearance.HaeundaeColor, hairStyle = 1,
            };
            _miso = NpcAgent.CreateHuman(actors, "miso", new Vector3(24.6f, 0f, 8f), -90f, merchant);
            _miso.actionHandler = HandleNpcAction;

            var teller = new Appearance
            {
                skin = new Color(1f, 0.86f, 0.72f), hair = new Color(0.10f, 0.10f, 0.14f),
                top = new Color(0.22f, 0.36f, 0.60f), bottom = new Color(0.16f, 0.18f, 0.26f),
                shoes = new Color(0.1f, 0.1f, 0.12f), armband = Appearance.HaeundaeColor, hairStyle = 1,
            };
            _daon = NpcAgent.CreateHuman(actors, "daon", new Vector3(24.6f, 0f, -9f), -90f, teller);
            _daon.actionHandler = HandleNpcAction;

            // 알록달록 가게 주인: 자기 가게의 옷을 입고 있다.
            var stylist = new Appearance
            {
                skin = new Color(0.98f, 0.82f, 0.68f), hair = new Color(0.45f, 0.26f, 0.14f),
                top = new Color(0.62f, 0.42f, 0.86f), bottom = new Color(0.30f, 0.34f, 0.50f),
                shoes = new Color(0.95f, 0.55f, 0.65f), armband = Appearance.HaeundaeColor, hairStyle = 1,
            };
            _areum = NpcAgent.CreateHuman(actors, "areum", ColorShopCenter + new Vector3(0f, 0f, 5.6f), 0f, stylist);
            _areum.actionHandler = HandleNpcAction;
            _areum.GetComponentInChildren<BlockCharacter>().Wear("bunny_band", "sunny_hoodie");

            var officer = new Appearance
            {
                skin = new Color(0.96f, 0.78f, 0.62f), hair = new Color(0.30f, 0.20f, 0.12f),
                top = new Color(0.30f, 0.66f, 0.60f), bottom = new Color(0.24f, 0.26f, 0.32f),
                shoes = new Color(0.14f, 0.14f, 0.16f), armband = Appearance.HaeundaeColor, hairStyle = 0,
            };
            _narae = NpcAgent.CreateHuman(actors, "narae", new Vector3(-24.6f, 0f, 8f), 90f, officer);
            _narae.actionHandler = HandleNpcAction;

            // CITY PASS 안내센터: 매니저는 문 앞에, 지역 혜택 안내원은 그 옆에 선다. 둘 다 길(서쪽)을 본다.
            var passManager = new Appearance
            {
                skin = new Color(0.98f, 0.82f, 0.68f), hair = new Color(0.16f, 0.12f, 0.10f),
                top = new Color(0.16f, 0.62f, 0.62f), bottom = new Color(0.20f, 0.24f, 0.30f),
                shoes = new Color(0.12f, 0.12f, 0.14f), armband = Appearance.HaeundaeColor, hairStyle = 1,
            };
            _seoyun = NpcAgent.CreateHuman(actors, "seoyun", new Vector3(CityPassCenter.x - 5.3f, 0f, CityPassCenter.z), -90f, passManager);
            _seoyun.actionHandler = HandleNpcAction;
            var passGuide = new Appearance
            {
                skin = new Color(0.92f, 0.74f, 0.58f), hair = new Color(0.34f, 0.22f, 0.14f),
                top = new Color(0.55f, 0.90f, 0.84f), bottom = new Color(0.26f, 0.30f, 0.40f),
                shoes = new Color(0.9f, 0.9f, 0.92f), armband = Appearance.HaeundaeColor, hairStyle = 0,
            };
            _garam = NpcAgent.CreateHuman(actors, "garam", new Vector3(CityPassCenter.x - 5.1f, 0f, CityPassCenter.z + 2.6f), -90f, passGuide);
            _garam.actionHandler = HandleNpcAction;

            // 장학센터: 선생님은 문 앞에, 교육 프로그램 안내원은 그 옆에 선다. 둘 다 길(동쪽)을 본다.
            var teacher = new Appearance
            {
                skin = new Color(0.96f, 0.80f, 0.66f), hair = new Color(0.22f, 0.18f, 0.16f),
                top = new Color(0.20f, 0.28f, 0.52f), bottom = new Color(0.34f, 0.30f, 0.28f),
                shoes = new Color(0.2f, 0.16f, 0.14f), armband = Appearance.HaeundaeColor, hairStyle = 1,
            };
            _sebin = NpcAgent.CreateHuman(actors, "sebin", new Vector3(ScholarshipCenter.x + 5.3f, 0f, ScholarshipCenter.z), 90f, teacher);
            _sebin.actionHandler = HandleNpcAction;
            var programGuide = new Appearance
            {
                skin = new Color(1f, 0.86f, 0.72f), hair = new Color(0.40f, 0.26f, 0.16f),
                top = new Color(0.98f, 0.86f, 0.50f), bottom = new Color(0.22f, 0.28f, 0.46f),
                shoes = new Color(0.9f, 0.9f, 0.92f), armband = Appearance.HaeundaeColor, hairStyle = 2,
            };
            _ruri = NpcAgent.CreateHuman(actors, "ruri", new Vector3(ScholarshipCenter.x + 5.1f, 0f, ScholarshipCenter.z + 2.6f), 90f, programGuide);
            _ruri.actionHandler = HandleNpcAction;
        }

        private void HandleNpcAction(string action, Action onSuccess)
        {
            if (action == "offer_residency")
            {
                OfferResidency();
                onSuccess();
                return;
            }
            if (action == "open_shop")
            {
                ItemActions.OpenShop(ShopCodes.Miso, _miso);
                onSuccess();
                return;
            }
            if (action == "open_colorshop")
            {
                ItemActions.OpenShop(ShopCodes.Color, _areum);
                onSuccess();
                return;
            }
            if (action == "open_bank")
            {
                BankActions.Open(_daon);
                onSuccess();
                return;
            }
            // 현실 혜택 안내 (v7.0). 안내만 한다: GC 를 쓰거나 받는 화면이 아니다.
            if (action == "open_citypass")
            {
                CityPassActions.Open(_seoyun);
                onSuccess();
                return;
            }
            if (action == "open_citypass_offers")
            {
                CityPassActions.OpenOffers(_garam);
                onSuccess();
                return;
            }
            if (action == "open_scholarship")
            {
                ScholarshipActions.Open(_sebin);
                onSuccess();
                return;
            }
            if (action == "open_scholarship_schedule")
            {
                ScholarshipActions.OpenSchedule(_ruri);
                onSuccess();
                return;
            }
            if (action != "claim_first_rescue")
            {
                Debug.LogWarning("알 수 없는 대화 action: " + action);
                return;
            }

            GameSession session = GameSession.Current;
            session.Backend.ClaimQuest(QuestCodes.FirstRescue, result =>
            {
                if (result.ok)
                {
                    session.ApplyQuestReward(result.data);
                    Sfx.Play(Sfx.Id.Coin);
                    Fx.Coins(_dodam.transform.position + Vector3.up * 0.6f, 6, _player.transform);
                    GameEvents.ShowToast($"도담의 감사 선물: {result.data.rewardGc} GC를 받았어요!");
                    onSuccess();
                }
                else if (result.errorCode == ApiErrors.Conflict)
                {
                    // 이미 받은 보상: 진행만 이어 간다.
                    onSuccess();
                }
                else
                {
                    Debug.LogWarning($"임무 보상 실패: {result.errorCode} {result.message}");
                    GameEvents.ShowToast("선물을 받지 못했어요. 도담에게 다시 말을 걸어 보세요.");
                }
            });
        }

        // ── 주민센터 ────────────────────────────────────────────

        /// <summary>전입신고를 권한다. 고르면 지도가 열리고, "나중에"를 고르면 다시 권하지 않는다.</summary>
        private void OfferResidency()
        {
            GameSession session = GameSession.Current;
            if (session == null || session.IsResident) return;
            int bonus = session.Defs.economy.residencyBonusGc;
            ChoiceMenu.Instance.Open("전입신고",
                "지도에서 좋아하는 도시를 고르면 그 도시의 가디언이 돼요.\n실제로 사는 곳과 달라도 괜찮아요.",
                new ChoiceOption
                {
                    label = "전입신고 하기",
                    cost = bonus > 0 ? $"축하금 {bonus:N0} GC" : "",
                    detail = "대한민국 지도에서 도시를 골라요.",
                    onChosen = () => MapController.Instance.OpenForResidency(),
                },
                new ChoiceOption
                {
                    label = "나중에 할게요",
                    detail = "이방인으로 지내도 괜찮아요. 언제든 다시 올 수 있어요.",
                    onChosen = () =>
                    {
                        if (GameSession.Current == session) session.SetFlag(FlagKeys.ResidencyDeclined, "1");
                        _narae.Say("언제든 다시 오세요!", 4f);
                    },
                });
        }

        // ── 방어기지 ────────────────────────────────────────────

        /// <summary>출동 게이트의 메뉴: 방어전 출동과 타워 해금.</summary>
        private void OpenDefenseMenu()
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            StageDef stage = session.Defs.FindStage(StageCodes.Outskirts1);
            TowerDef blast = session.Defs.FindTower(TowerCodes.Blast);
            if (stage == null || blast == null)
            {
                Debug.LogError("방어전 정의가 없습니다.");
                return;
            }

            bool cleared = session.HasCleared(stage.code);
            string reward = cleared
                ? $"보상 {stage.winGc} GC"
                : $"보상 {stage.winGc} GC + 첫 승리 {stage.firstWinBonusGc} GC";
            var deploy = new ChoiceOption
            {
                label = "방어전 출동",
                cost = stage.name,
                detail = $"웨이브 {stage.waves.Count}번을 막아내면 승리!  {reward}",
                onChosen = () => GameRoot.Instance.LoadScene(SceneNames.Defense, SpawnIds.DefenseBase),
            };

            bool unlocked = session.IsTowerUnlocked(blast.code);
            bool affordable = session.Me.gc >= blast.unlockGc;
            var unlock = new ChoiceOption
            {
                label = unlocked ? $"{blast.name} (해금 완료)" : $"{blast.name} 해금",
                cost = unlocked ? "" : $"{blast.unlockGc:N0} GC",
                detail = unlocked ? "방어전에서 세울 수 있어요."
                    : affordable ? blast.description
                    : $"GC가 모자라요. (지금 {session.Me.gc:N0} GC)",
                enabled = !unlocked && affordable,
                onChosen = () => UnlockTower(blast),
            };

            // 관리자가 디펜스 모드를 잠시 닫았으면 출동할 수 없다.
            if (!ModeOpen(CombatModeCodes.Defense))
            {
                deploy.enabled = false;
                deploy.detail = "지금은 방어전이 잠시 닫혀 있어요.";
            }

            ChoiceMenu.Instance.Open("방어기지",
                "로봇 무리가 도시 외곽으로 몰려오고 있어요.\n타워를 세워 도시를 지켜 주세요.", deploy, unlock);
        }

        // ── 작전 단말기 (v8.0) ──────────────────────────────────

        /// <summary>
        /// 출동 게이트 옆 작전 단말기의 메뉴: 시내 긴급 출동(FPS), 타워 연구, 전투 장비, 도시 방어 현황.
        /// 출동 게이트의 메뉴(방어전 출동, 폭발 타워 해금)는 그대로 두고, 새 기능은 여기에 모았다.
        /// </summary>
        private void OpenCommandMenu()
        {
            GameSession session = GameSession.Current;
            if (session == null) return;

            CityStatusDto city = CityRiskService.CombatCity;
            string body = "시내로 들어온 로봇을 막는 긴급 출동과\n전투 준비를 여기서 해요.";
            if (city != null)
            {
                string cityName = RegionService.ShortName(city.regionCode) ?? "도시";
                body = city.HasInvasion
                    ? $"{InvasionKinds.DisplayName(city.invasionKind)} 발생!  격퇴 {city.invasionProgress} / {city.invasionRequired}\n" +
                      $"{cityName} 위험도 {city.risk} · {CityRiskService.StageBadge(city.stage)}"
                    : $"{cityName} 위험도 {city.risk} · {CityRiskService.StageBadge(city.stage)}\n" +
                      "방어전과 긴급 출동에서 이기면 위험도가 내려가요.";
            }

            ChoiceMenu.Instance.Open("작전 본부", body,
                FpsOption(session, city), ResearchOption(session), GearOption(), CityOption(session), CoopOption(), CityOpsOption());
        }

        /// <summary>모드가 열려 있는가. 정의가 없으면(옛 정의) 열린 것으로 본다.</summary>
        private static bool ModeOpen(string modeCode)
        {
            CombatModeDef mode = CombatService.ModeDef(modeCode);
            return mode == null || !mode.closed;
        }

        private ChoiceOption FpsOption(GameSession session, CityStatusDto city)
        {
            FpsMissionDef mission = session.Defs.combat.FindFpsMission(FpsMissionCodes.Downtown1);
            bool open = mission != null && ModeOpen(CombatModeCodes.Fps);
            bool levelOk = mission != null && session.Me.level >= mission.unlockLevel;
            bool intrusion = city != null && city.HasInvasion
                             && (city.invasionKind == InvasionKinds.Intrusion || city.invasionKind == InvasionKinds.Zero);
            string reward = mission == null ? ""
                : session.HasClearedFps(mission.code) ? $"보상 {mission.winGc} GC"
                : $"보상 {mission.winGc} GC + 첫 승리 {mission.firstWinBonusGc} GC";
            return new ChoiceOption
            {
                label = "시내 긴급 출동",
                cost = mission != null ? mission.name : "준비 중",
                detail = !open ? "지금은 긴급 출동이 잠시 닫혀 있어요."
                    : !levelOk ? $"레벨 {mission.unlockLevel}부터 나설 수 있어요."
                    : intrusion ? $"시내 침입 진행 중! 1인칭으로 로봇을 막고 시설과 시민을 지켜요.  {reward}"
                    : $"1인칭 임무예요. 로봇을 막고 시설과 시민을 지켜요.  {reward}",
                enabled = open && levelOk,
                onChosen = () => GameRoot.Instance.LoadScene(SceneNames.Fps, SpawnIds.FpsPlaza),
            };
        }

        private ChoiceOption ResearchOption(GameSession session)
        {
            int total = 0;
            int unlocked = 0;
            foreach (TowerDef def in session.Defs.towers)
            {
                if (!IsResearchable(def)) continue;
                total++;
                if (session.IsTowerUnlocked(def.code)) unlocked++;
            }
            return new ChoiceOption
            {
                label = "타워 연구",
                cost = $"{unlocked} / {total}",
                detail = "레이저 타워, 감속 장치, EMP 타워를 해금해요. 한 번 해금하면 계속 쓸 수 있어요.",
                enabled = total > 0,
                onChosen = OpenResearchMenu,
            };
        }

        /// <summary>연구 메뉴에 나오는 타워: 전투에서 세울 수 있고 GC로 해금하는 것 (폭발 타워는 앞의 칸에 따로 있다).</summary>
        private static bool IsResearchable(TowerDef def) =>
            def.IsBuildable && def.unlockGc > 0 && def.code != TowerCodes.Blast;

        private void OpenResearchMenu()
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            var options = new System.Collections.Generic.List<ChoiceOption>();
            foreach (TowerDef def in session.Defs.towers)
            {
                if (!IsResearchable(def) || options.Count >= ChoiceMenu.MaxOptions) continue;
                TowerDef tower = def;
                bool unlocked = session.IsTowerUnlocked(tower.code);
                bool levelOk = session.Me.level >= tower.unlockLevel;
                bool affordable = session.Me.gc >= tower.unlockGc;
                options.Add(new ChoiceOption
                {
                    label = unlocked ? $"{tower.name} (해금 완료)" : $"{tower.name} 해금",
                    cost = unlocked ? "" : !levelOk ? $"레벨 {tower.unlockLevel}부터" : $"{tower.unlockGc:N0} GC",
                    detail = unlocked ? "방어전에서 세울 수 있어요."
                        : !levelOk ? tower.description
                        : affordable ? tower.description
                        : $"GC가 모자라요. (지금 {session.Me.gc:N0} GC)",
                    enabled = !unlocked && levelOk && affordable,
                    onChosen = () => UnlockTower(tower),
                });
            }
            if (options.Count == 0) return;
            ChoiceMenu.Instance.Open("타워 연구",
                "해금한 타워는 내 것이 되어 모든 방어전에서 세울 수 있어요.\n기관총 타워만으로도 방어전을 이길 수 있어요.", options.ToArray());
            ChoiceMenu.Instance.OnCancelled = OpenCommandMenu;
        }

        private ChoiceOption GearOption()
        {
            GearDef primary = EquipmentSystem.Equipped(CombatModeCodes.Fps, GearSlots.Primary);
            return new ChoiceOption
            {
                label = "전투 장비",
                cost = primary != null ? primary.name : "",
                detail = "긴급 출동에 들고 갈 무기와 장치를 사고, 강화하고, 장착해요.",
                onChosen = () => EquipmentSystem.OpenShop(OpenCommandMenu),
            };
        }

        private ChoiceOption CityOption(GameSession session)
        {
            return new ChoiceOption
            {
                label = "도시 방어 현황",
                cost = session.IsResident ? "" : "주민 전용",
                detail = session.IsResident
                    ? "위험도, 시설 복구, 도시 공용 방어시설, 다른 도시 지원, 지역 랭킹을 봐요."
                    : "전입신고를 하면 도시를 함께 지킬 수 있어요.",
                enabled = session.IsResident,
                onChosen = () => CityActions.Open(OpenCommandMenu),
            };
        }

        /// <summary>v9.0 협동 타워디펜스: 연습전은 누구나, 온라인은 주민만 (운영팀이 열기 전에는 준비 중으로 보인다).</summary>
        private ChoiceOption CoopOption()
        {
            return new ChoiceOption
            {
                label = "협동 방어전",
                cost = "여럿이 함께",
                detail = "도시 외곽의 관문을 여러 대원이 함께 지켜요. 연습전은 지금도 할 수 있어요.",
                onChosen = () => CoopActions.Open(OpenCommandMenu),
            };
        }

        /// <summary>v9.0 도시 성장, 이사, 가상 지방세, 도시 재정, 참여예산.</summary>
        private ChoiceOption CityOpsOption()
        {
            return new ChoiceOption
            {
                label = CityOpsActions.Title,
                detail = "도시 성장, 이사, 지방세, 도시 재정, 참여예산을 봐요.",
                onChosen = () => CityOpsActions.Open(OpenCommandMenu),
            };
        }

        private void UnlockTower(TowerDef tower)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.UnlockTower(tower.code, result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    session.ApplyTowerUnlock(result.data);
                    Sfx.Play(Sfx.Id.Build);
                    GameEvents.ShowToast($"{tower.name}를 쓸 수 있게 됐어요! 방어전에서 세워 보세요.");
                }
                else if (result.errorCode == ApiErrors.InsufficientFunds)
                {
                    GameEvents.ShowToast("GC가 모자라요.");
                }
                else if (result.errorCode == ApiErrors.Conflict)
                {
                    GameEvents.ShowToast("이미 쓸 수 있는 타워예요.");
                }
                else
                {
                    Debug.LogWarning($"타워 해금 실패: {result.errorCode} {result.message}");
                    GameEvents.ShowToast("타워를 해금하지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                }
            });
        }

        // ── 튜토리얼 진행 ───────────────────────────────────────

        /// <summary>튜토리얼을 마친 뒤 무엇을 권할지: 1 사냥, 2 방어전, 3 자유, 4 전입신고, 5 상점.</summary>
        private static int GuideFor(GameSession session)
        {
            if (session.TutorialStep < 6) return 0;
            bool cleared = session.HasCleared(StageCodes.Outskirts1);
            if (cleared)
            {
                // 첫 방어전을 이긴 뒤 한 번 전입신고를 권한다. "나중에"라고 답했으면 더 권하지 않는다.
                bool declined = session.Me.GetFlag(FlagKeys.ResidencyDeclined) == "1";
                if (!session.IsResident && !declined) return 4;
            }
            // 첫 원거리 무기를 살 돈이 모였으면 상점을 권한다. "돈을 벌어 장비를 산다"가 이 게임의 기본 흐름이다.
            if (CanBuyFirstBlaster(session)) return 5;
            // 은행을 쓸 수 있는 레벨이 되고 맡길 돈이 있으면 한 번 소개한다. 은행 화면을 열어 보면 안내가 사라진다.
            if (session.BankUnlocked && session.Me.gc >= BankActions.SmallestAmount
                && session.Me.GetFlag(FlagKeys.BankVisited) != "1") return 6;
            if (cleared) return 3;
            // 사냥으로 전투에 익숙해진 다음에 방어전을 권한다. 권할 뿐이고 언제든 출동할 수 있다.
            return session.Me.level >= 2 ? 2 : 1;
        }

        private static bool CanBuyFirstBlaster(GameSession session)
        {
            ItemDef blaster = session.Defs.FindItem(ItemCodes.BoltBlaster);
            return blaster != null && blaster.priceGc > 0 && !session.Owns(blaster.code)
                   && session.Me.gc >= blaster.priceGc;
        }

        private void Update()
        {
            GameSession session = GameSession.Current;
            if (session == null || _player == null) return;

            int step = session.TutorialStep;
            int guide = GuideFor(session);
            if (step != _shownStep || guide != _shownGuide)
            {
                _shownStep = step;
                _shownGuide = guide;
                ApplyStep(step);
            }

            if (step == 3)
            {
                if (_tutorialRobot == null && _respawnRobotAt >= 0f && Time.time >= _respawnRobotAt)
                {
                    _respawnRobotAt = -1f;
                    SpawnTutorialRobot();
                }
                if (Time.time >= _nextCry && !GameState.IsControlLocked)
                {
                    _nextCry = Time.time + 7f;
                    _dodam.Say("도와주세요! 로봇이에요!", 4f);
                }
            }
        }

        private void ApplyStep(int step)
        {
            switch (step)
            {
                case 3:
                    GameEvents.SetObjective("마우스 왼쪽 버튼으로 로봇을 물리치세요");
                    GameEvents.SetKeyHint("마우스 왼쪽 버튼  공격      Shift  달리기");
                    SpawnTutorialRobot();
                    break;
                case 4:
                    GameEvents.SetObjective("도담에게 다가가 E 키로 말을 거세요");
                    GameEvents.SetKeyHint("E  말하기");
                    _dodam.Say("고마워요!", 5f);
                    _marker.Point(_dodam.transform, 1.9f);
                    break;
                case 5:
                    GameEvents.SetObjective("광장의 강한울 대장을 찾아가세요");
                    GameEvents.SetKeyHint(null);
                    _marker.Point(_hanul.transform, 2.5f);
                    break;
                default:
                    GameEvents.SetKeyHint(step >= MapController.MinTutorialStep ? "M  지도      I  가방      P  휴대전화" : null);
                    switch (_shownGuide)
                    {
                        case 5:
                            GameEvents.SetObjective("미소 상점에서 볼트 블래스터를 살 수 있어요");
                            _marker.Point(_miso.transform, 2.3f);
                            break;
                        case 6:
                            GameEvents.SetObjective("도시은행의 은다온에게 GC를 맡기면 이자가 붙어요");
                            _marker.Point(_daon.transform, 2.3f);
                            break;
                        case 2:
                            GameEvents.SetObjective("방어기지의 출동 게이트에서 방어전에 나서 보세요");
                            _marker.PointAt(DefenseGatePoint + Vector3.up * 3.6f);
                            break;
                        case 4:
                            GameEvents.SetObjective("주민센터의 정나래에게 전입신고를 하면 정식 가디언이 돼요");
                            _marker.Point(_narae.transform, 2.3f);
                            break;
                        case 3:
                            GameEvents.SetObjective("전투 구역과 방어전에서 GC를 모으세요");
                            _marker.Hide();
                            break;
                        default:
                            GameEvents.SetObjective("전투 구역에서 로봇을 물리치고 GC를 모으세요");
                            if (step == 6 && GameSession.Current.Me.level <= 1 && GameSession.Current.Me.xp <= 10)
                                _marker.PointAt(NorthEastCenter + new Vector3(-BattleHalf.x - 1f, 3f, -BattleHalf.y - 1f));
                            else
                                _marker.Hide();
                            break;
                    }
                    break;
            }
        }

        private void SpawnTutorialRobot()
        {
            if (_tutorialRobot != null) return;
            _tutorialRobot = _homeZone.SpawnAt(EnemyCodes.TutorialRobot, TutorialRobotSpot);
            if (_tutorialRobot == null)
            {
                Debug.LogError("첫 전투용 로봇을 출현시키지 못했습니다 (길 찾기 지도를 확인하세요).");
                _respawnRobotAt = Time.time + 2f;
                return;
            }
            _marker.Point(_tutorialRobot.transform, 2.2f);
        }

        private void OnReportConfirmed(HuntZone zone, HuntReportDto report)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;

            if (zone == _homeZone && session.TutorialStep == 3)
            {
                // 서버가 처치를 인정한 뒤에 다음 단계로 간다.
                if (report.accepted != null && report.accepted.Count > 0) session.AdvanceTutorial(4);
                else _respawnRobotAt = Time.time + 2f;
                return;
            }

            // 전투 구역에서 첫 보상을 받으면 길 안내 표시를 치운다.
            if (zone != _homeZone && _shownGuide == 1 && _marker != null) _marker.Hide();
        }
    }

    /// <summary>바닷가의 흰 물결. 앞뒤로 천천히 밀려왔다 나간다.</summary>
    public sealed class WaveStrip : MonoBehaviour
    {
        public float phase;
        private Vector3 _base;

        private void Start() => _base = transform.localPosition;

        private void Update()
        {
            transform.localPosition = _base + new Vector3(0f, 0f, Mathf.Sin(Time.time * 0.7f + phase) * 1.4f);
        }
    }
}
