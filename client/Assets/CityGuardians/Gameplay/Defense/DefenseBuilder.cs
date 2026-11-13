using CityGuardians.Domain;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 방어전 전장 "외곽 1구역" (docs/TOWER_DEFENSE.md). 북쪽 끝의 침입 지점에서 나온 로봇들이
    /// 구불구불한 길을 따라 남쪽의 방어선으로 온다. 길 옆의 빛나는 자리 6곳에 타워를 세울 수 있다.
    /// 해가 지는 하늘과 방어선 뒤의 도시 윤곽으로 "도시를 지킨다"는 느낌을 준다.
    /// </summary>
    public sealed class DefenseBuilder : MonoBehaviour
    {
        private const float CameraDistance = 7f;
        private const float PathWidth = 5f;

        // 걸어 다닐 수 있는 범위
        private const float MinX = -27f;
        private const float MaxX = 27f;
        private const float MinZ = -24f;
        private const float MaxZ = 42f;

        public static readonly Color Sky = new Color(0.97f, 0.72f, 0.56f);
        private static readonly Color Ground = new Color(0.63f, 0.58f, 0.52f);
        private static readonly Color PathColor = new Color(0.31f, 0.32f, 0.36f);
        private static readonly Color PathEdge = new Color(1f, 0.80f, 0.20f);
        private static readonly Color WallColor = new Color(0.46f, 0.54f, 0.50f);
        private static readonly Color Dark = new Color(0.22f, 0.25f, 0.28f);

        /// <summary>적이 나오는 곳.</summary>
        public static readonly Vector3 SpawnPoint = new Vector3(-16f, 0f, 36f);

        /// <summary>적이 지나가는 길. 마지막이 방어 거점이다.</summary>
        public static readonly Vector3[] Route =
        {
            new Vector3(-16f, 0f, 20f),
            new Vector3(12f, 0f, 20f),
            new Vector3(12f, 0f, 4f),
            new Vector3(-12f, 0f, 4f),
            new Vector3(-12f, 0f, -12f),
            new Vector3(0f, 0f, -12f),
            new Vector3(0f, 0f, -21.5f),
        };

        /// <summary>타워를 세울 수 있는 자리. 길모퉁이 안쪽일수록 길을 오래 겨눌 수 있다.</summary>
        public static readonly Vector3[] SlotPoints =
        {
            new Vector3(-10f, 0f, 26f),
            new Vector3(6f, 0f, 14f),
            new Vector3(18f, 0f, 10f),
            new Vector3(4f, 0f, -2f),
            new Vector3(-6f, 0f, -4f),
            new Vector3(5.5f, 0f, -15.5f),
        };

        /// <summary>처음 타워를 세워 보라고 가리키는 자리 (방어선에서 가장 가깝다).</summary>
        public const int FirstSlotIndex = 5;

        public static readonly Vector3 PlayerSpawn = new Vector3(0f, 0f, -17.5f);
        public static readonly Vector3 SignalPoint = new Vector3(-5.5f, 0f, -20f);
        public static readonly Vector3 RetreatPoint = new Vector3(8f, 0f, -23.2f);

        public DefenseBattle Battle { get; private set; }
        public BuildSlot[] Slots { get; private set; }
        public DefenseModeController Mode { get; private set; }

        private PlayerController _player;
        private ObjectiveMarker _marker;

        private void Start()
        {
            if (!SceneGuard.EnsureSession()) return;
            Build();
        }

        private void Build()
        {
            SetupLighting();

            // 움직이지 않는 지형. 이 아래의 충돌체로 길 찾기 지도를 만든다.
            Transform world = new GameObject("DefenseStatic").transform;
            BuildGround(world);
            BuildPath(world);
            BuildPortal(world);
            BuildDefenseLine(world);
            BuildScenery(world);
            BuildBounds(world);

            var surface = world.gameObject.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();

            Transform actors = new GameObject("DefenseActors").transform;
            _player = PlayerFactory.Spawn(PlayerSpawn, 0f, Sky, CameraDistance);
            PlayerFactory.Arm(_player);

            Battle = DefenseBattle.Create(actors, StageCodes.Outskirts1, SpawnPoint, Route);

            Slots = new BuildSlot[SlotPoints.Length];
            for (int i = 0; i < SlotPoints.Length; i++)
                Slots[i] = BuildSlot.Create(actors, i, SlotPoints[i], Battle);

            BuildGuardians(actors);
            BuildSignal(actors);
            BuildRetreatGate(actors);

            _marker = ObjectiveMarker.Create();
            _marker.transform.SetParent(actors, true);
            _marker.transform.localScale = Vector3.one * 1.8f;

            // 디펜스 모드: 시점 전환(V)과 조작 안내를 맡는다. 전투 규칙은 DefenseBattle 그대로다.
            Mode = DefenseModeController.Create(actors.gameObject, Battle);
        }

        private void OnDestroy()
        {
            GameEvents.SetKeyHint(null);
        }

        private void Update()
        {
            if (Battle == null || _marker == null) return;

            // 처음에는 어디서 무엇을 할지 화살표로 알려 준다.
            if (Battle.Phase == DefensePhase.Prep && Battle.TowersBuilt == 0)
                _marker.PointAt(SlotPoints[FirstSlotIndex] + Vector3.up * 2.4f);
            else if (Battle.Phase == DefensePhase.Prep)
                _marker.PointAt(SignalPoint + Vector3.up * 3.6f);
            else
                _marker.Hide();
        }

        private static void SetupLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.84f, 0.70f, 0.66f);
            RenderSettings.ambientEquatorColor = new Color(0.64f, 0.58f, 0.58f);
            RenderSettings.ambientGroundColor = new Color(0.38f, 0.34f, 0.36f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Sky;
            RenderSettings.fogStartDistance = 55f;
            RenderSettings.fogEndDistance = 220f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.84f, 0.66f);
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(32f, -140f, 0f);
        }

        // ── 지형 ────────────────────────────────────────────────

        private static void BuildGround(Transform world)
        {
            // 걸을 수 있는 땅 (윗면 y = 0)
            Blocks.Box(world, "Ground", new Vector3(0f, -0.5f, 9f), new Vector3(60f, 1f, 72f), Ground, true);
            // 바깥 풍경
            Blocks.Box(world, "FarLand", new Vector3(0f, -0.75f, 60f), new Vector3(500f, 1f, 420f), new Color(0.55f, 0.52f, 0.46f));
        }

        /// <summary>바닥에 깔리는 얇은 판. 충돌체가 없어 걷는 데 영향을 주지 않는다.</summary>
        private static GameObject Flat(Transform parent, string name, Vector3 center, Vector2 size, Color color,
            int layerIndex, bool unlit = false)
        {
            float y = 0.02f * layerIndex;
            return Blocks.Box(parent, name, new Vector3(center.x, y * 0.5f, center.z), new Vector3(size.x, y, size.y), color, false, unlit);
        }

        private static void BuildPath(Transform world)
        {
            // 침입 지점에서 방어선까지 이어지는 길. 모퉁이가 비지 않게 구간마다 길 너비의 절반씩 더 깐다.
            Vector3 from = SpawnPoint + new Vector3(0f, 0f, 4f);
            for (int i = 0; i < Route.Length; i++)
            {
                Vector3 to = Route[i];
                Vector3 mid = (from + to) * 0.5f;
                bool alongX = Mathf.Abs(to.x - from.x) > Mathf.Abs(to.z - from.z);
                float length = (alongX ? Mathf.Abs(to.x - from.x) : Mathf.Abs(to.z - from.z)) + PathWidth;
                Vector2 size = alongX ? new Vector2(length, PathWidth) : new Vector2(PathWidth, length);
                Flat(world, "Path", mid, size, PathColor, 1);

                // 가는 방향을 알려 주는 노란 화살표
                Vector3 direction = (to - from).normalized;
                float span = Vector3.Distance(from, to);
                for (float d = 4f; d < span - 2f; d += 6f)
                    Arrow(world, from + direction * d, direction);
                from = to;
            }
        }

        private static void Arrow(Transform world, Vector3 position, Vector3 direction)
        {
            Transform arrow = Blocks.Pivot(world, "PathArrow", new Vector3(position.x, 0f, position.z));
            arrow.localRotation = Quaternion.LookRotation(direction, Vector3.up);
            Blocks.Box(arrow, "Stem", new Vector3(0f, 0.03f, -0.3f), new Vector3(0.3f, 0.02f, 1.0f), PathEdge, false, true);
            Blocks.Box(arrow, "HeadL", new Vector3(-0.25f, 0.03f, 0.25f), new Vector3(0.3f, 0.02f, 0.8f), PathEdge, false, true)
                .transform.localRotation = Quaternion.Euler(0f, 40f, 0f);
            Blocks.Box(arrow, "HeadR", new Vector3(0.25f, 0.03f, 0.25f), new Vector3(0.3f, 0.02f, 0.8f), PathEdge, false, true)
                .transform.localRotation = Quaternion.Euler(0f, -40f, 0f);
        }

        /// <summary>침입 지점: ZERO가 로봇을 내보내는 문.</summary>
        private static void BuildPortal(Transform world)
        {
            Vector3 at = SpawnPoint + new Vector3(0f, 0f, 4.5f);
            Color frame = new Color(0.20f, 0.18f, 0.26f);
            Color glow = new Color(0.72f, 0.30f, 1f);
            Blocks.Box(world, "PortalPostL", at + new Vector3(-3.4f, 2.6f, 0f), new Vector3(1.0f, 5.2f, 1.2f), frame, true);
            Blocks.Box(world, "PortalPostR", at + new Vector3(3.4f, 2.6f, 0f), new Vector3(1.0f, 5.2f, 1.2f), frame, true);
            Blocks.Box(world, "PortalTop", at + new Vector3(0f, 5.6f, 0f), new Vector3(8.0f, 1.0f, 1.2f), frame);
            Blocks.Box(world, "PortalBack", at + new Vector3(0f, 2.6f, 0.9f), new Vector3(6.0f, 5.2f, 0.4f), frame, true);
            Blocks.Box(world, "PortalGlow", at + new Vector3(0f, 2.5f, 0.66f), new Vector3(5.6f, 4.8f, 0.06f), glow, false, true);
            // 깨진 화면 같은 조각들
            var rng = new System.Random(5);
            for (int i = 0; i < 9; i++)
            {
                float x = ((float)rng.NextDouble() * 2f - 1f) * 2.5f;
                float y = 0.6f + (float)rng.NextDouble() * 3.8f;
                float w = 0.3f + (float)rng.NextDouble() * 0.9f;
                Blocks.Box(world, "Glitch", at + new Vector3(x, y, 0.6f), new Vector3(w, 0.16f, 0.06f),
                    i % 2 == 0 ? Color.white : new Color(0.3f, 1f, 0.9f), false, true);
            }
            WorldLabel.Create(world, at + new Vector3(0f, 6.9f, 0f), "ZERO 침입 지점", 0.8f, new Color(0.86f, 0.6f, 1f));
        }

        /// <summary>방어선: 도시로 들어가는 문과 그 양옆의 벽. 로봇이 여기에 닿으면 도시 방어 체력이 깎인다.</summary>
        private static void BuildDefenseLine(Transform world)
        {
            const float wallZ = -25.5f;
            Blocks.Box(world, "WallL", new Vector3(-16.5f, 2.2f, wallZ), new Vector3(27f, 4.4f, 1.6f), WallColor, true);
            Blocks.Box(world, "WallR", new Vector3(16.5f, 2.2f, wallZ), new Vector3(27f, 4.4f, 1.6f), WallColor, true);
            Blocks.Box(world, "WallTopL", new Vector3(-16.5f, 4.6f, wallZ), new Vector3(27.4f, 0.4f, 2.0f), Dark);
            Blocks.Box(world, "WallTopR", new Vector3(16.5f, 4.6f, wallZ), new Vector3(27.4f, 0.4f, 2.0f), Dark);
            // 문: 닫힌 철문과 그 앞의 방어 코어
            Blocks.Box(world, "Gate", new Vector3(0f, 2.0f, wallZ + 0.2f), new Vector3(6f, 4.0f, 1.0f), Dark, true);
            Blocks.Box(world, "GatePostL", new Vector3(-3.4f, 2.8f, wallZ), new Vector3(0.9f, 5.6f, 2.0f), WallColor, true);
            Blocks.Box(world, "GatePostR", new Vector3(3.4f, 2.8f, wallZ), new Vector3(0.9f, 5.6f, 2.0f), WallColor, true);
            Blocks.Box(world, "GateBeam", new Vector3(0f, 5.4f, wallZ), new Vector3(7.8f, 0.9f, 2.0f), WallColor);
            Blocks.Box(world, "GateStripe", new Vector3(0f, 2.0f, wallZ + 0.72f), new Vector3(5.6f, 0.4f, 0.04f),
                Appearance.HaeundaeColor, false, true);
            Blocks.Cylinder(world, "CoreBase", new Vector3(0f, 0.25f, -23.4f), new Vector3(2.2f, 0.5f, 2.2f), Dark, true);
            GameObject core = Blocks.Sphere(world, "Core", new Vector3(0f, 1.3f, -23.4f), new Vector3(1.2f, 1.2f, 1.2f),
                new Color(0.45f, 0.95f, 1f), false, true);
            core.AddComponent<Bobbing>();
            WorldLabel.Create(world, new Vector3(0f, 6.6f, wallZ), "해운대구 방어선", 0.8f, Color.white);

            // 깃발
            foreach (float x in new[] { -3.4f, 3.4f })
            {
                Blocks.Box(world, "FlagPole", new Vector3(x, 6.6f, wallZ), new Vector3(0.1f, 2.0f, 0.1f), Dark);
                Blocks.Box(world, "Flag", new Vector3(x + 0.55f, 7.2f, wallZ), new Vector3(1.0f, 0.6f, 0.05f),
                    Appearance.HaeundaeColor, false, true);
            }

            // 방어선 뒤의 도시: 지켜야 할 곳
            float[] xs = { -44f, -30f, -17f, -6f, 7f, 19f, 31f, 45f };
            float[] heights = { 30f, 44f, 26f, 52f, 36f, 48f, 28f, 40f };
            Color[] tones = { new Color(0.62f, 0.60f, 0.70f), new Color(0.54f, 0.56f, 0.68f), new Color(0.70f, 0.64f, 0.70f) };
            for (int i = 0; i < xs.Length; i++)
            {
                Transform t = Blocks.Pivot(world, "CityTower", new Vector3(xs[i], -0.25f, -44f - (i % 3) * 7f));
                Blocks.Box(t, "Body", new Vector3(0f, heights[i] * 0.5f, 0f), new Vector3(9f, heights[i], 8f), tones[i % tones.Length]);
                // 저녁이라 불이 켜진 창
                for (float y = 4f; y < heights[i] - 2f; y += 4.4f)
                    Blocks.Box(t, "Windows", new Vector3(0f, y, 4.02f), new Vector3(7.6f, 1.6f, 0.05f),
                        new Color(1f, 0.90f, 0.60f), false, true);
            }
        }

        /// <summary>길에서 떨어진 곳의 잔해와 방벽. 지나다니는 데 방해되지 않는 자리에만 놓는다.</summary>
        private static void BuildScenery(Transform world)
        {
            Color barrier = new Color(0.92f, 0.52f, 0.18f);
            Color[] crate = { new Color(0.62f, 0.50f, 0.36f), new Color(0.44f, 0.48f, 0.54f), new Color(0.56f, 0.34f, 0.30f) };
            Vector3[] crates =
            {
                new Vector3(-24f, 0f, 30f), new Vector3(-23f, 0f, 12f), new Vector3(-2f, 0f, 30f), new Vector3(22f, 0f, 28f),
                new Vector3(23f, 0f, -4f), new Vector3(20f, 0f, -14f), new Vector3(-22f, 0f, -8f), new Vector3(-20f, 0f, -18f),
                new Vector3(2f, 0f, 12f), new Vector3(-3f, 0f, 11f),
            };
            for (int i = 0; i < crates.Length; i++)
            {
                float w = 1.2f + (i % 3) * 0.5f;
                float h = 0.9f + (i % 2) * 0.6f;
                Blocks.Box(world, "Crate", crates[i] + new Vector3(0f, h * 0.5f, 0f), new Vector3(w, h, 1.3f), crate[i % crate.Length], true);
            }
            // 길모퉁이 바깥의 방벽
            Blocks.Box(world, "Barrier", new Vector3(-20.5f, 0.45f, 18.5f), new Vector3(0.5f, 0.9f, 4f), barrier, true);
            Blocks.Box(world, "Barrier", new Vector3(16.5f, 0.45f, 21.5f), new Vector3(0.5f, 0.9f, 4f), barrier, true);
            Blocks.Box(world, "Barrier", new Vector3(-16.5f, 0.45f, 2.5f), new Vector3(0.5f, 0.9f, 4f), barrier, true);
            Blocks.Box(world, "Barrier", new Vector3(-13f, 0.45f, -16.5f), new Vector3(4f, 0.9f, 0.5f), barrier, true);

            // 가로등
            foreach (Vector3 spot in new[]
                     {
                         new Vector3(-4f, 0f, -12f + 3.6f), new Vector3(4f, 0f, -22f), new Vector3(-8f, 0f, 0f),
                         new Vector3(16f, 0f, 16f), new Vector3(-5f, 0f, 24f), new Vector3(6f, 0f, 24f),
                     })
            {
                Blocks.Box(world, "LampPole", spot + new Vector3(0f, 1.6f, 0f), new Vector3(0.14f, 3.2f, 0.14f), Dark, true);
                Blocks.Box(world, "LampHead", spot + new Vector3(0f, 3.3f, 0f), new Vector3(0.5f, 0.16f, 0.5f), Dark);
                Blocks.Box(world, "LampBulb", spot + new Vector3(0f, 3.18f, 0f), new Vector3(0.34f, 0.1f, 0.34f),
                    new Color(1f, 0.92f, 0.65f), false, true);
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

        // ── 움직이는 것들 ───────────────────────────────────────

        private void BuildGuardians(Transform actors)
        {
            var first = new Appearance
            {
                skin = new Color(0.96f, 0.78f, 0.62f), hair = new Color(0.16f, 0.12f, 0.10f),
                top = new Color(0.86f, 0.36f, 0.20f), bottom = new Color(0.22f, 0.26f, 0.32f),
                shoes = new Color(0.15f, 0.15f, 0.16f), armband = Appearance.HaeundaeColor, hairStyle = 1,
            };
            var second = new Appearance
            {
                skin = new Color(0.85f, 0.65f, 0.48f), hair = new Color(0.35f, 0.22f, 0.12f),
                top = new Color(0.86f, 0.36f, 0.20f), bottom = new Color(0.22f, 0.26f, 0.32f),
                shoes = new Color(0.15f, 0.15f, 0.16f), armband = Appearance.HaeundaeColor, hairStyle = 2,
            };
            GuardianAgent.Create(actors, "방어대원 가람", new Vector3(-3.6f, 0f, -16.5f), 0f, first, Battle);
            GuardianAgent.Create(actors, "방어대원 다솜", new Vector3(3.6f, 0f, -19.5f), 0f, second, Battle);
        }

        /// <summary>신호기: 첫 웨이브를 시작하거나, 쉬는 시간을 건너뛴다.</summary>
        private void BuildSignal(Transform actors)
        {
            Transform signal = Blocks.Pivot(actors, "Signal", SignalPoint);
            Blocks.Box(signal, "Pole", new Vector3(0f, 1.2f, 0f), new Vector3(0.22f, 2.4f, 0.22f), Dark, true);
            Blocks.Box(signal, "Box", new Vector3(0f, 1.3f, 0.2f), new Vector3(0.6f, 0.5f, 0.2f), new Color(0.95f, 0.75f, 0.2f));
            Blocks.Box(signal, "Button", new Vector3(0f, 1.3f, 0.32f), new Vector3(0.26f, 0.26f, 0.06f), new Color(1f, 0.3f, 0.25f), false, true);
            Blocks.Sphere(signal, "Lamp", new Vector3(0f, 2.6f, 0f), new Vector3(0.5f, 0.5f, 0.5f), new Color(1f, 0.4f, 0.3f), false, true);
            WorldLabel.Create(signal, new Vector3(0f, 3.2f, 0f), "신호기", 0.3f, new Color(1f, 0.86f, 0.3f));

            Interactable interact = Interactable.Add(signal.gameObject, "웨이브 시작", () => Battle.StartNextWave(), 2.4f,
                () => Battle != null && (Battle.Phase == DefensePhase.Prep || Battle.Phase == DefensePhase.Rest));
            signal.gameObject.AddComponent<SignalVerb>().Bind(interact, Battle);
        }

        /// <summary>도시로 돌아가는 문. 전투 중에 쓰면 후퇴가 된다.</summary>
        private void BuildRetreatGate(Transform actors)
        {
            Transform gate = Blocks.Pivot(actors, "RetreatGate", RetreatPoint);
            Blocks.Box(gate, "Door", new Vector3(0f, 1.1f, -1.4f), new Vector3(1.6f, 2.2f, 0.12f), new Color(0.30f, 0.44f, 0.40f));
            Blocks.Box(gate, "Sign", new Vector3(0f, 2.6f, -1.4f), new Vector3(2.2f, 0.5f, 0.12f), new Color(0.16f, 0.2f, 0.3f));
            WorldLabel.Create(gate, new Vector3(0f, 2.6f, -1.3f), "도시로", 0.32f, Color.white, null, 2.2f, false)
                .transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            Interactable.Add(gate.gameObject, "도시로 돌아가기", AskRetreat, 2.2f, () => Battle != null && Battle.CanBuild);
        }

        private void AskRetreat()
        {
            if (Battle == null || !Battle.CanBuild) return;
            bool started = Battle.WaveNumber > 0;
            int reward = Battle.Stage.loseGcPerWave * Battle.WavesCleared;
            string body = !started
                ? "아직 웨이브를 시작하지 않았어요.\n지금 돌아가도 괜찮아요."
                : reward > 0
                    ? $"지금 돌아가면 방어전이 끝나요.\n막아낸 웨이브만큼 참가 보상 {reward} GC를 받아요."
                    : "지금 돌아가면 방어전이 끝나요.\n웨이브를 하나라도 막아내면 참가 보상을 받을 수 있어요.";
            ChoiceMenu.Instance.Open("도시로 돌아갈까요?", body,
                new ChoiceOption { label = "돌아가기", detail = started ? "방어전을 그만두고 도시로 가요." : "도시로 가요.", onChosen = () => Battle.Retreat() },
                new ChoiceOption { label = "계속 싸우기", detail = "여기에 남아요.", onChosen = () => { } });
        }
    }

    /// <summary>신호기의 안내 글을 전투 단계에 맞춰 바꾼다.</summary>
    public sealed class SignalVerb : MonoBehaviour
    {
        private Interactable _interact;
        private DefenseBattle _battle;

        public void Bind(Interactable interact, DefenseBattle battle)
        {
            _interact = interact;
            _battle = battle;
        }

        private void Update()
        {
            if (_interact == null || _battle == null) return;
            _interact.verb = _battle.Phase == DefensePhase.Rest ? "다음 웨이브 바로 시작" : "웨이브 시작";
        }
    }

    /// <summary>제자리에서 천천히 오르내린다 (방어 코어).</summary>
    public sealed class Bobbing : MonoBehaviour
    {
        private Vector3 _base;

        private void Start() => _base = transform.localPosition;

        private void Update()
        {
            transform.localPosition = _base + new Vector3(0f, Mathf.Sin(Time.time * 1.6f) * 0.15f, 0f);
        }
    }
}
