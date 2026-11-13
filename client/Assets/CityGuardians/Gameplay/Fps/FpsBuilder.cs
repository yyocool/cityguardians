using CityGuardians.Domain;
using CityGuardians.Services;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// FPS 임무 전장 "도심 상가 구역" (docs/FPS_MODE.md). 북쪽 벽의 침입 지점 세 곳에서 나온 로봇들이
    /// 상가 사이의 길과 골목을 지나 주요 시설 세 곳으로 간다. 골목에는 시민이 숨어 있다.
    /// 플레이어는 가운데 광장에서 1인칭으로 시작한다. 이동은 다른 장면과 같은 PlayerController 이고,
    /// 카메라와 무기만 FPS 모드의 것으로 바뀐다 (FPSModeController).
    /// </summary>
    public sealed class FpsBuilder : MonoBehaviour
    {
        // 걸어 다닐 수 있는 범위
        private const float Half = 30f;
        /// <summary>가운데 십자로의 절반 너비.</summary>
        private const float StreetHalf = 6f;
        /// <summary>상가 블록의 바깥쪽 끝. 그 밖은 둘레길이다.</summary>
        private const float BlockEdge = 22f;

        public static readonly Color Sky = new Color(0.42f, 0.44f, 0.62f);
        private static readonly Color Ground = new Color(0.36f, 0.37f, 0.42f);
        private static readonly Color Plaza = new Color(0.58f, 0.56f, 0.54f);
        private static readonly Color Line = new Color(0.95f, 0.92f, 0.80f);
        private static readonly Color WallColor = new Color(0.40f, 0.44f, 0.50f);
        private static readonly Color Dark = new Color(0.20f, 0.22f, 0.27f);
        private static readonly Color Alarm = new Color(1f, 0.32f, 0.26f);
        private static readonly Color Glow = new Color(0.72f, 0.30f, 1f);

        public static readonly Vector3 PlayerSpawn = new Vector3(0f, 0f, -4f);

        /// <summary>침입 지점. 북쪽 벽 세 곳이 뚫렸다.</summary>
        public static readonly Vector3[] BreachPoints =
        {
            new Vector3(0f, 0f, 26.5f),
            new Vector3(26f, 0f, 26.5f),
            new Vector3(-26f, 0f, 26.5f),
        };

        /// <summary>시설의 자리와 바라보는 쪽. 차례는 FacilityCodes 의 발전소, 통신 센터, 정수장이다.</summary>
        public static readonly Vector3[] FacilityPoints =
        {
            new Vector3(0f, 0f, -27.5f),
            new Vector3(-27.5f, 0f, 0f),
            new Vector3(27.5f, 0f, 0f),
        };
        private static readonly Vector3[] FacilityFacing = { Vector3.forward, Vector3.right, Vector3.left };
        private static readonly string[] FacilityOrder =
            { FacilityCodes.PowerPlant, FacilityCodes.CommCenter, FacilityCodes.WaterPlant };
        private static readonly Color[] FacilityColors =
        {
            new Color(0.92f, 0.70f, 0.22f), new Color(0.36f, 0.62f, 0.92f), new Color(0.30f, 0.78f, 0.72f),
        };

        /// <summary>시민이 숨어 있는 골목 안쪽과, 대피할 때 달려가는 쪽 (광장 쪽).</summary>
        public static readonly Vector3[] CivilianPoints =
        {
            new Vector3(-14f, 0f, 14f),
            new Vector3(14f, 0f, 14f),
            new Vector3(-14f, 0f, -14f),
            new Vector3(14f, 0f, -14f),
        };
        private static readonly Vector3[] CivilianExits = { Vector3.right, Vector3.back, Vector3.forward, Vector3.left };

        public static readonly Vector3 ExitPoint = new Vector3(4.6f, 0f, -9f);

        public FpsMission Mission { get; private set; }
        public FPSModeController Mode { get; private set; }
        public FpsFacility[] Facilities { get; private set; }
        public FpsCivilian[] Civilians { get; private set; }

        private PlayerController _player;

        private void Start()
        {
            if (!SceneGuard.EnsureSession()) return;
            Build();
        }

        private void Build()
        {
            SetupLighting();
            GameSession session = GameSession.Current;

            // 움직이지 않는 지형. 이 아래의 충돌체로 길 찾기 지도를 만든다.
            Transform world = new GameObject("FpsStatic").transform;
            BuildGround(world);
            BuildShops(world);
            BuildPerimeter(world);
            BuildProps(world);

            Facilities = new FpsFacility[FacilityPoints.Length];
            for (int i = 0; i < FacilityPoints.Length; i++)
            {
                FacilityDef def = session.Defs.combat.FindFacility(FacilityOrder[i]);
                Facilities[i] = FpsFacility.Create(world, FacilityOrder[i], def != null ? def.name : "주요 시설",
                    FacilityPoints[i], FacilityFacing[i], FacilityColors[i]);
            }

            var surface = world.gameObject.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();

            Transform actors = new GameObject("FpsActors").transform;
            // 이동과 체력은 다른 모드와 같은 캐릭터다. 근접·원거리 무기(PlayerCombat)는 들지 않고 FPS 장비를 쓴다.
            _player = PlayerFactory.Spawn(PlayerSpawn, 0f, Sky, 6f);

            Mission = FpsMission.Create(actors, FpsMissionCodes.Downtown1, BreachPoints, Facilities);

            Civilians = new FpsCivilian[Mathf.Min(CivilianPoints.Length, Mission.Def.civilianCount)];
            for (int i = 0; i < Civilians.Length; i++)
            {
                // 광장 쪽을 바라보고 웅크려 있다.
                float yaw = Quaternion.LookRotation(CivilianExits[i], Vector3.up).eulerAngles.y;
                Civilians[i] = FpsCivilian.Create(actors, Mission, i, CivilianPoints[i], yaw, CivilianExits[i]);
            }

            BuildExit(actors);

            // FPS 모드: 1인칭 카메라와 FPS 장비, 조작 안내.
            Mode = FPSModeController.Create(actors.gameObject, Mission, _player);
        }

        private static void SetupLighting()
        {
            // 해가 진 직후의 도심. 경보가 울리는 저녁이다.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.62f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.52f, 0.50f, 0.60f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.30f, 0.36f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Sky;
            RenderSettings.fogStartDistance = 40f;
            RenderSettings.fogEndDistance = 160f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(0.86f, 0.80f, 0.96f);
            sun.intensity = 0.9f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(38f, 150f, 0f);
        }

        // ── 지형 ────────────────────────────────────────────────

        /// <summary>바닥에 깔리는 얇은 판. 충돌체가 없어 걷는 데 영향을 주지 않는다.</summary>
        private static GameObject Flat(Transform parent, string name, Vector3 center, Vector2 size, Color color,
            int layerIndex, bool unlit = false)
        {
            float y = 0.02f * layerIndex;
            return Blocks.Box(parent, name, new Vector3(center.x, y * 0.5f, center.z), new Vector3(size.x, y, size.y), color, false, unlit);
        }

        private static void BuildGround(Transform world)
        {
            // 걸을 수 있는 땅 (윗면 y = 0)
            Blocks.Box(world, "Ground", new Vector3(0f, -0.5f, 0f), new Vector3(Half * 2f + 4f, 1f, Half * 2f + 4f), Ground, true);
            Blocks.Box(world, "FarLand", new Vector3(0f, -0.75f, 0f), new Vector3(400f, 1f, 400f), new Color(0.28f, 0.29f, 0.34f));

            // 가운데 광장
            Flat(world, "Plaza", Vector3.zero, new Vector2(StreetHalf * 2f, StreetHalf * 2f), Plaza, 1);
            // 길 가운데의 점선
            for (float d = 9f; d < Half - 2f; d += 4f)
            {
                Flat(world, "LineN", new Vector3(0f, 0f, d), new Vector2(0.2f, 1.6f), Line, 2, true);
                Flat(world, "LineS", new Vector3(0f, 0f, -d), new Vector2(0.2f, 1.6f), Line, 2, true);
                Flat(world, "LineE", new Vector3(d, 0f, 0f), new Vector2(1.6f, 0.2f), Line, 2, true);
                Flat(world, "LineW", new Vector3(-d, 0f, 0f), new Vector2(1.6f, 0.2f), Line, 2, true);
            }

            // 광장 가운데의 시계탑. 길을 막지 않을 만큼 작다.
            Blocks.Box(world, "ClockBase", new Vector3(0f, 0.3f, 0f), new Vector3(1.8f, 0.6f, 1.8f), Dark, true);
            Blocks.Box(world, "ClockPole", new Vector3(0f, 2.2f, 0f), new Vector3(0.5f, 3.2f, 0.5f), WallColor, true);
            Blocks.Box(world, "ClockHead", new Vector3(0f, 4.2f, 0f), new Vector3(1.2f, 1.2f, 1.2f), Dark);
            foreach (Vector3 face in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                Blocks.Cylinder(world, "ClockFace", new Vector3(0f, 4.2f, 0f) + face * 0.61f, new Vector3(0.9f, 0.02f, 0.9f),
                        new Color(1f, 0.96f, 0.82f), false, true)
                    .transform.localRotation = Quaternion.FromToRotation(Vector3.up, face);
        }

        /// <summary>상가 건물. 네 구역에 두 채씩 있고, 그 사이가 골목이다.</summary>
        private static void BuildShops(Transform world)
        {
            const float a = StreetHalf;      // 6
            const float b = BlockEdge;       // 22
            const float alleyNear = 12.5f;
            const float alleyFar = 15.5f;
            Color[] tones =
            {
                new Color(0.80f, 0.62f, 0.52f), new Color(0.60f, 0.70f, 0.78f), new Color(0.76f, 0.72f, 0.58f),
                new Color(0.66f, 0.60f, 0.74f), new Color(0.58f, 0.72f, 0.62f), new Color(0.82f, 0.68f, 0.44f),
                new Color(0.70f, 0.56f, 0.56f), new Color(0.56f, 0.64f, 0.70f),
            };

            // 북서: 골목이 동서로 났다
            Shop(world, "편의점", -b, -a, alleyFar, b, 7f, tones[0], Vector3.right);
            Shop(world, "분식집", -b, -a, a, alleyNear, 5f, tones[1], Vector3.back);
            // 북동: 골목이 남북으로 났다
            Shop(world, "문구점", a, alleyNear, a, b, 6f, tones[2], Vector3.back);
            Shop(world, "약국", alleyFar, b, a, b, 8f, tones[3], Vector3.back);
            // 남서: 골목이 남북으로 났다
            Shop(world, "빵집", -b, -alleyFar, -b, -a, 6f, tones[4], Vector3.forward);
            Shop(world, "서점", -alleyNear, -a, -b, -a, 8f, tones[5], Vector3.forward);
            // 남동: 골목이 동서로 났다
            Shop(world, "꽃집", a, b, -alleyNear, -a, 5f, tones[6], Vector3.forward);
            Shop(world, "세탁소", a, b, -b, -alleyFar, 7f, tones[7], Vector3.left);
        }

        /// <param name="signSide">간판이 붙는 쪽 (건물 바깥을 향하는 방향).</param>
        private static void Shop(Transform world, string sign, float minX, float maxX, float minZ, float maxZ, float height,
            Color tone, Vector3 signSide)
        {
            var center = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
            var size = new Vector3(maxX - minX, height, maxZ - minZ);
            Transform shop = Blocks.Pivot(world, "Shop_" + sign, center);
            Blocks.Box(shop, "Body", new Vector3(0f, height * 0.5f, 0f), size, tone, true);
            Blocks.Box(shop, "Roof", new Vector3(0f, height + 0.15f, 0f), new Vector3(size.x + 0.4f, 0.3f, size.z + 0.4f), Dark);

            // 간판과 진열창: 간판이 붙는 면에만 단다.
            bool alongX = Mathf.Abs(signSide.z) > 0.5f; // 면이 x축을 따라 펼쳐지는가
            float faceWidth = alongX ? size.x : size.z;
            float out_ = (alongX ? size.z : size.x) * 0.5f;
            Vector3 face = signSide * (out_ + 0.03f);
            Vector3 thin = alongX ? new Vector3(1f, 1f, 0.05f) : new Vector3(0.05f, 1f, 1f);

            float windowWidth = Mathf.Min(faceWidth - 2f, 9f);
            Blocks.Box(shop, "Window", face + Vector3.up * 1.5f,
                Vector3.Scale(thin, alongX ? new Vector3(windowWidth, 1.8f, 1f) : new Vector3(1f, 1.8f, windowWidth)),
                new Color(1f, 0.92f, 0.66f), false, true);
            Blocks.Box(shop, "SignBoard", face + Vector3.up * 3.2f,
                Vector3.Scale(thin, alongX ? new Vector3(Mathf.Min(faceWidth - 1f, 6f), 0.9f, 1f) : new Vector3(1f, 0.9f, Mathf.Min(faceWidth - 1f, 6f))),
                Dark, false);
            WorldLabel label = WorldLabel.Create(shop, face + signSide * 0.05f + Vector3.up * 3.2f, sign, 0.6f, Color.white, null, 5f, false);
            label.transform.localRotation = Quaternion.LookRotation(-signSide, Vector3.up);

            // 차양
            Blocks.Box(shop, "Awning", face + signSide * 0.5f + Vector3.up * 2.6f,
                alongX ? new Vector3(windowWidth + 0.6f, 0.12f, 1.0f) : new Vector3(1.0f, 0.12f, windowWidth + 0.6f),
                Color.Lerp(tone, Color.white, 0.35f));
        }

        /// <summary>구역을 두른 벽과 그 너머의 도시, 그리고 북쪽 벽의 침입 지점.</summary>
        private static void BuildPerimeter(Transform world)
        {
            float edge = Half + 0.8f;
            float span = Half * 2f + 3.2f;
            Blocks.Box(world, "WallN", new Vector3(0f, 2.5f, edge), new Vector3(span, 5f, 1.6f), WallColor, true);
            Blocks.Box(world, "WallS", new Vector3(0f, 2.5f, -edge), new Vector3(span, 5f, 1.6f), WallColor, true);
            Blocks.Box(world, "WallE", new Vector3(edge, 2.5f, 0f), new Vector3(1.6f, 5f, span), WallColor, true);
            Blocks.Box(world, "WallW", new Vector3(-edge, 2.5f, 0f), new Vector3(1.6f, 5f, span), WallColor, true);
            Blocks.InvisibleWall(world, "BoundN", new Vector3(0f, 8f, Half + 0.5f), new Vector3(span, 8f, 1f));
            Blocks.InvisibleWall(world, "BoundS", new Vector3(0f, 8f, -Half - 0.5f), new Vector3(span, 8f, 1f));
            Blocks.InvisibleWall(world, "BoundE", new Vector3(Half + 0.5f, 8f, 0f), new Vector3(1f, 8f, span));
            Blocks.InvisibleWall(world, "BoundW", new Vector3(-Half - 0.5f, 8f, 0f), new Vector3(1f, 8f, span));

            for (int i = 0; i < BreachPoints.Length; i++) Breach(world, BreachPoints[i]);
            WorldLabel.Create(world, new Vector3(0f, 7.2f, Half), "ZERO 침입 지점", 0.8f, new Color(0.86f, 0.6f, 1f));

            // 벽 너머의 도시: 지켜야 할 곳
            var rng = new System.Random(8);
            Color[] tones = { new Color(0.40f, 0.42f, 0.56f), new Color(0.34f, 0.38f, 0.52f), new Color(0.46f, 0.42f, 0.56f) };
            for (int i = 0; i < 20; i++)
            {
                float angle = i / 20f * Mathf.PI * 2f;
                float distance = 52f + (float)rng.NextDouble() * 18f;
                float height = 18f + (float)rng.NextDouble() * 30f;
                Transform tower = Blocks.Pivot(world, "CityTower", new Vector3(Mathf.Cos(angle) * distance, -0.25f, Mathf.Sin(angle) * distance));
                tower.localRotation = Quaternion.LookRotation(new Vector3(-Mathf.Cos(angle), 0f, -Mathf.Sin(angle)), Vector3.up);
                Blocks.Box(tower, "Body", new Vector3(0f, height * 0.5f, 0f), new Vector3(9f, height, 8f), tones[i % tones.Length]);
                for (float y = 7f; y < height - 2f; y += 4.6f)
                    Blocks.Box(tower, "Windows", new Vector3(0f, y, 4.02f), new Vector3(7.4f, 1.5f, 0.05f),
                        new Color(1f, 0.90f, 0.60f), false, true);
            }
        }

        /// <summary>침입 지점: 벽이 뚫린 자리의 문. 로봇이 여기서 나온다.</summary>
        private static void Breach(Transform world, Vector3 point)
        {
            Vector3 at = new Vector3(point.x, 0f, Half);
            Color frame = new Color(0.20f, 0.18f, 0.26f);
            Blocks.Box(world, "BreachPostL", at + new Vector3(-2.3f, 2.2f, -0.2f), new Vector3(0.7f, 4.4f, 0.8f), frame);
            Blocks.Box(world, "BreachPostR", at + new Vector3(2.3f, 2.2f, -0.2f), new Vector3(0.7f, 4.4f, 0.8f), frame);
            Blocks.Box(world, "BreachTop", at + new Vector3(0f, 4.6f, -0.2f), new Vector3(5.3f, 0.7f, 0.8f), frame);
            Blocks.Box(world, "BreachGlow", at + new Vector3(0f, 2.1f, -0.05f), new Vector3(3.9f, 4.0f, 0.06f), Glow, false, true);
            var rng = new System.Random((int)(point.x * 7f) + 31);
            for (int i = 0; i < 6; i++)
            {
                float x = ((float)rng.NextDouble() * 2f - 1f) * 1.7f;
                float y = 0.5f + (float)rng.NextDouble() * 3.2f;
                float w = 0.3f + (float)rng.NextDouble() * 0.8f;
                Blocks.Box(world, "Glitch", at + new Vector3(x, y, -0.12f), new Vector3(w, 0.14f, 0.06f),
                    i % 2 == 0 ? Color.white : new Color(0.3f, 1f, 0.9f), false, true);
            }
            Blocks.Sphere(world, "BreachLamp", at + new Vector3(0f, 5.4f, -0.2f), new Vector3(0.5f, 0.5f, 0.5f), Alarm, false, true);
        }

        /// <summary>길가의 물건들. 몸을 숨길 수 있지만 길을 막지는 않는다.</summary>
        private static void BuildProps(Transform world)
        {
            Color barrier = new Color(0.92f, 0.52f, 0.18f);
            Color planter = new Color(0.44f, 0.36f, 0.30f);
            Color leaf = new Color(0.34f, 0.62f, 0.38f);

            // 가운데 길가의 화단 (길 양옆)
            foreach (float d in new[] { 10f, 18f })
            {
                foreach (float side in new[] { -4.8f, 4.8f })
                {
                    Planter(world, new Vector3(side, 0f, d), planter, leaf);
                    Planter(world, new Vector3(side, 0f, -d), planter, leaf);
                    Planter(world, new Vector3(d, 0f, side), planter, leaf);
                    Planter(world, new Vector3(-d, 0f, side), planter, leaf);
                }
            }

            // 둘레길의 방벽과 상자
            Vector3[] barriers =
            {
                new Vector3(-12f, 0f, 25f), new Vector3(12f, 0f, 25f), new Vector3(25f, 0f, 12f), new Vector3(25f, 0f, -12f),
                new Vector3(-25f, 0f, 12f), new Vector3(-25f, 0f, -12f), new Vector3(-12f, 0f, -25f), new Vector3(12f, 0f, -25f),
            };
            for (int i = 0; i < barriers.Length; i++)
            {
                bool alongX = Mathf.Abs(barriers[i].z) > Mathf.Abs(barriers[i].x);
                Blocks.Box(world, "Barrier", barriers[i] + new Vector3(0f, 0.5f, 0f),
                    alongX ? new Vector3(0.5f, 1.0f, 2.6f) : new Vector3(2.6f, 1.0f, 0.5f), barrier, true);
            }

            // 세워 둔 배달 수레 (낮은 상자 모양)
            Color[] carts = { new Color(0.80f, 0.30f, 0.28f), new Color(0.28f, 0.52f, 0.80f), new Color(0.90f, 0.78f, 0.30f) };
            Vector3[] cartSpots = { new Vector3(-27.6f, 0f, 20f), new Vector3(27.6f, 0f, -20f), new Vector3(20f, 0f, 27.8f), new Vector3(-20f, 0f, -27.8f) };
            for (int i = 0; i < cartSpots.Length; i++)
            {
                bool alongX = Mathf.Abs(cartSpots[i].z) > Mathf.Abs(cartSpots[i].x);
                Transform cart = Blocks.Pivot(world, "Cart", cartSpots[i]);
                Blocks.Box(cart, "Body", new Vector3(0f, 0.7f, 0f), alongX ? new Vector3(3.0f, 1.0f, 1.5f) : new Vector3(1.5f, 1.0f, 3.0f),
                    carts[i % carts.Length], true);
                Blocks.Box(cart, "Top", new Vector3(0f, 1.4f, 0f), alongX ? new Vector3(1.6f, 0.5f, 1.4f) : new Vector3(1.4f, 0.5f, 1.6f),
                    Color.Lerp(carts[i % carts.Length], Color.white, 0.4f));
            }

            // 가로등과 경보등
            foreach (Vector3 spot in new[]
                     {
                         new Vector3(-5.4f, 0f, 5.4f), new Vector3(5.4f, 0f, 5.4f), new Vector3(-5.4f, 0f, -5.4f), new Vector3(5.4f, 0f, -5.4f),
                         new Vector3(-22.6f, 0f, 22.6f), new Vector3(22.6f, 0f, 22.6f), new Vector3(-22.6f, 0f, -22.6f), new Vector3(22.6f, 0f, -22.6f),
                     })
            {
                Blocks.Box(world, "LampPole", spot + new Vector3(0f, 1.8f, 0f), new Vector3(0.14f, 3.6f, 0.14f), Dark, true);
                Blocks.Box(world, "LampHead", spot + new Vector3(0f, 3.7f, 0f), new Vector3(0.5f, 0.16f, 0.5f), Dark);
                Blocks.Box(world, "LampBulb", spot + new Vector3(0f, 3.58f, 0f), new Vector3(0.34f, 0.1f, 0.34f),
                    new Color(1f, 0.92f, 0.65f), false, true);
                Blocks.Sphere(world, "AlarmLamp", spot + new Vector3(0f, 4.0f, 0f), new Vector3(0.28f, 0.28f, 0.28f), Alarm, false, true);
            }
        }

        private static void Planter(Transform world, Vector3 at, Color box, Color leaf)
        {
            Blocks.Box(world, "Planter", at + new Vector3(0f, 0.4f, 0f), new Vector3(1.4f, 0.8f, 1.4f), box, true);
            Blocks.Sphere(world, "Bush", at + new Vector3(0f, 1.1f, 0f), new Vector3(1.3f, 0.9f, 1.3f), leaf);
        }

        // ── 움직이는 것들 ───────────────────────────────────────

        /// <summary>도시로 돌아가는 문. 임무 중에 쓰면 후퇴가 된다.</summary>
        private void BuildExit(Transform actors)
        {
            Transform gate = Blocks.Pivot(actors, "ExitGate", ExitPoint);
            Blocks.Box(gate, "Door", new Vector3(1.32f, 1.1f, 0f), new Vector3(0.12f, 2.2f, 1.6f), new Color(0.30f, 0.44f, 0.40f));
            Blocks.Box(gate, "Sign", new Vector3(1.32f, 2.6f, 0f), new Vector3(0.12f, 0.5f, 2.2f), new Color(0.16f, 0.2f, 0.3f));
            WorldLabel.Create(gate, new Vector3(1.22f, 2.6f, 0f), "도시로", 0.32f, Color.white, null, 2.2f, false)
                .transform.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);

            Interactable.Add(gate.gameObject, "도시로 돌아가기", AskRetreat, 2.2f,
                () => Mission != null && (Mission.IsActive || Mission.Phase == FpsPhase.Starting));
        }

        private void AskRetreat()
        {
            if (Mission == null || !(Mission.IsActive || Mission.Phase == FpsPhase.Starting)) return;
            ChoiceMenu.Instance.Open("도시로 돌아갈까요?",
                "지금 돌아가면 임무가 실패로 끝나요.\n보상은 없지만, 은행 잔액이나 신용에는 아무 영향이 없어요.",
                new ChoiceOption { label = "돌아가기", detail = "임무를 그만두고 도시로 가요.", onChosen = () => Mission.Retreat() },
                new ChoiceOption { label = "계속 싸우기", detail = "여기에 남아요.", onChosen = () => { } });
        }
    }
}
