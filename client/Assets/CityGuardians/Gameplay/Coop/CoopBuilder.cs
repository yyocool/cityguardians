using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 협동 방어전 전장 (docs/COOP_DEFENSE_SYSTEM.md). 도시 외곽에서 여러 대원이 함께 관문을 지킨다.
    /// 지형은 서버가 알려 준 지도(CoopMap)대로 만든다: 적이 오는 길, 타워 자리, 관문, 방어벽, 주요 시설.
    /// 전투는 CoopBattle 이 맡는다. 연결이 정해지지 않은 채로 들어오면 (에디터에서 바로 실행) 연습전을 연다.
    /// </summary>
    public sealed class CoopBuilder : MonoBehaviour
    {
        private const float CameraDistance = 7f;
        private const float PathWidth = 5f;

        public static readonly Color Sky = new Color(0.62f, 0.74f, 0.90f);
        private static readonly Color Ground = new Color(0.56f, 0.60f, 0.52f);
        private static readonly Color PathColor = new Color(0.31f, 0.32f, 0.36f);
        private static readonly Color PathEdge = new Color(1f, 0.80f, 0.20f);
        private static readonly Color WallColor = new Color(0.46f, 0.54f, 0.50f);
        private static readonly Color Dark = new Color(0.22f, 0.25f, 0.28f);

        public CoopBattle Battle { get; private set; }
        public CoopModeController Mode { get; private set; }

        private void Start()
        {
            if (!SceneGuard.EnsureSession()) return;
            GameSession session = GameSession.Current;

            ICoopLink link = CoopLaunch.Take(out string sessionId);
            if (link == null)
            {
                link = CoopLaunch.NewPractice(session);
                sessionId = null;
            }
            if (link.Map == null || link.Snapshot == null)
            {
                // 지도를 받지 못한 연결로는 전장을 만들 수 없다.
                link.Leave();
                GameEvents.ShowToast(CoopTexts.Closed(link.ClosedReason));
                GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.DefenseGate);
                return;
            }
            Build(link, sessionId);
        }

        private void OnDestroy()
        {
            GameEvents.SetKeyHint(null);
        }

        private void Build(ICoopLink link, string sessionId)
        {
            CoopMap map = link.Map;
            SetupLighting();

            Transform world = new GameObject("CoopStatic").transform;
            BuildGround(world, map);
            BuildPath(world, map);
            BuildLandmarks(world, map, link.RegionName);
            BuildBounds(world, map);

            // 내 자리는 서버가 정해 준 곳이다.
            CoopPlayerView me = link.Snapshot.FindPlayer(link.MyPid);
            CoopVec gate = map.Gate;
            Vector3 spawn = me != null ? new Vector3(me.x, 0f, me.z) : new Vector3(gate.x, 0f, gate.z + 4f);

            Transform actors = new GameObject("CoopActors").transform;
            PlayerController player = PlayerFactory.Spawn(spawn, 0f, Sky, CameraDistance);
            // 협동 방어전에서는 사격을 서버가 처리한다. 손에는 블래스터를 들지만 직접 휘두르지는 않는다.
            player.Character.EquipBlaster();

            Vector3 signal = new Vector3(gate.x + 4.5f, 0f, gate.z + 1f);
            Vector3 exit = new Vector3(gate.x - 4.5f, 0f, gate.z + 1f);
            Battle = CoopBattle.Create(actors, link, sessionId, player, signal, exit);
            Mode = actors.gameObject.AddComponent<CoopModeController>();
        }

        private static void SetupLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.78f, 0.84f, 0.92f);
            RenderSettings.ambientEquatorColor = new Color(0.62f, 0.64f, 0.62f);
            RenderSettings.ambientGroundColor = new Color(0.36f, 0.38f, 0.36f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Sky;
            RenderSettings.fogStartDistance = 60f;
            RenderSettings.fogEndDistance = 230f;

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.95f, 0.86f);
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(48f, -150f, 0f);
        }

        private static Vector3 At(CoopVec p, float y = 0f) => new Vector3(p.x, y, p.z);

        private static void BuildGround(Transform world, CoopMap map)
        {
            float midX = (map.minX + map.maxX) * 0.5f;
            float midZ = (map.minZ + map.maxZ) * 0.5f;
            float spanX = map.maxX - map.minX + 8f;
            float spanZ = map.maxZ - map.minZ + 8f;
            // 걸을 수 있는 땅 (윗면 y = 0)
            Blocks.Box(world, "Ground", new Vector3(midX, -0.5f, midZ), new Vector3(spanX, 1f, spanZ), Ground, true);
            Blocks.Box(world, "FarLand", new Vector3(midX, -0.75f, midZ), new Vector3(500f, 1f, 500f), new Color(0.50f, 0.54f, 0.46f));
        }

        /// <summary>적이 지나가는 길. 꺾이는 곳이 비지 않게 구간마다 길 너비만큼 더 깐다.</summary>
        private static void BuildPath(Transform world, CoopMap map)
        {
            for (int i = 0; i + 1 < map.path.Length; i++)
            {
                Vector3 from = At(map.path[i]);
                Vector3 to = At(map.path[i + 1]);
                Vector3 delta = to - from;
                float length = delta.magnitude;
                if (length < 0.01f) continue;
                Vector3 direction = delta / length;

                Transform segment = Blocks.Pivot(world, "PathSegment", (from + to) * 0.5f);
                segment.localRotation = Quaternion.LookRotation(direction, Vector3.up);
                Blocks.Box(segment, "Road", new Vector3(0f, 0.01f, 0f), new Vector3(PathWidth, 0.02f, length + PathWidth), PathColor);

                // 가는 방향을 알려 주는 노란 화살표
                for (float d = 3f; d < length - 1.5f; d += 6f)
                {
                    Transform arrow = Blocks.Pivot(world, "PathArrow", from + direction * d);
                    arrow.localRotation = Quaternion.LookRotation(direction, Vector3.up);
                    Blocks.Box(arrow, "Stem", new Vector3(0f, 0.03f, -0.3f), new Vector3(0.3f, 0.02f, 1.0f), PathEdge, false, true);
                    Blocks.Box(arrow, "HeadL", new Vector3(-0.25f, 0.03f, 0.25f), new Vector3(0.3f, 0.02f, 0.8f), PathEdge, false, true)
                        .transform.localRotation = Quaternion.Euler(0f, 40f, 0f);
                    Blocks.Box(arrow, "HeadR", new Vector3(0.25f, 0.03f, 0.25f), new Vector3(0.3f, 0.02f, 0.8f), PathEdge, false, true)
                        .transform.localRotation = Quaternion.Euler(0f, -40f, 0f);
                }
            }
        }

        /// <summary>지도에 적힌 곳들: 침입 지점, 관문, 방어벽, 주요 시설. 미니맵에도 같은 것이 보인다.</summary>
        private static void BuildLandmarks(Transform world, CoopMap map, string regionName)
        {
            for (int i = 0; i < map.landmarks.Length; i++)
            {
                CoopLandmark landmark = map.landmarks[i];
                Vector3 at = At(landmark.pos);
                switch (landmark.kind)
                {
                    case CoopLandmarkKinds.Spawn:
                        BuildPortal(world, at, landmark.name);
                        break;
                    case CoopLandmarkKinds.Gate:
                        BuildGate(world, at, string.IsNullOrEmpty(regionName) ? landmark.name : regionName + " " + landmark.name);
                        break;
                    case CoopLandmarkKinds.Wall:
                        Blocks.Box(world, "Wall", at + new Vector3(0f, 1.6f, 0f), new Vector3(10f, 3.2f, 0.8f), WallColor, true);
                        Blocks.Box(world, "WallTop", at + new Vector3(0f, 3.4f, 0f), new Vector3(10.4f, 0.4f, 1.2f), Dark);
                        WorldLabel.Create(world, at + new Vector3(0f, 4.4f, 0f), landmark.name, 0.4f, Color.white);
                        break;
                    default:
                        BuildFacility(world, at, landmark.name, i);
                        break;
                }
            }
        }

        /// <summary>침입 지점: ZERO 가 로봇을 내보내는 문.</summary>
        private static void BuildPortal(Transform world, Vector3 at, string name)
        {
            Color frame = new Color(0.20f, 0.18f, 0.26f);
            Color glow = new Color(0.72f, 0.30f, 1f);
            Vector3 back = at + new Vector3(0f, 0f, 2.2f);
            Blocks.Box(world, "PortalPostL", back + new Vector3(-3.4f, 2.6f, 0f), new Vector3(1.0f, 5.2f, 1.2f), frame, true);
            Blocks.Box(world, "PortalPostR", back + new Vector3(3.4f, 2.6f, 0f), new Vector3(1.0f, 5.2f, 1.2f), frame, true);
            Blocks.Box(world, "PortalTop", back + new Vector3(0f, 5.6f, 0f), new Vector3(8.0f, 1.0f, 1.2f), frame);
            Blocks.Box(world, "PortalGlow", back + new Vector3(0f, 2.5f, 0.2f), new Vector3(5.6f, 4.8f, 0.06f), glow, false, true);
            WorldLabel.Create(world, back + new Vector3(0f, 6.9f, 0f), "ZERO " + name, 0.8f, new Color(0.86f, 0.6f, 1f));
        }

        /// <summary>관문: 로봇이 여기에 닿으면 관문 내구도가 깎인다.</summary>
        private static void BuildGate(Transform world, Vector3 at, string name)
        {
            // 문은 길의 끝보다 조금 뒤에 선다. 대원이 문 앞을 지나다닐 수 있다.
            Vector3 gate = at + new Vector3(0f, 0f, -1.6f);
            Blocks.Box(world, "Gate", gate + new Vector3(0f, 2.0f, 0f), new Vector3(6f, 4.0f, 1.0f), Dark, true);
            Blocks.Box(world, "GatePostL", gate + new Vector3(-3.4f, 2.8f, 0f), new Vector3(0.9f, 5.6f, 1.6f), WallColor, true);
            Blocks.Box(world, "GatePostR", gate + new Vector3(3.4f, 2.8f, 0f), new Vector3(0.9f, 5.6f, 1.6f), WallColor, true);
            Blocks.Box(world, "GateBeam", gate + new Vector3(0f, 5.4f, 0f), new Vector3(7.8f, 0.9f, 1.6f), WallColor);
            Blocks.Box(world, "GateStripe", gate + new Vector3(0f, 2.0f, 0.52f), new Vector3(5.6f, 0.4f, 0.04f), Appearance.HaeundaeColor, false, true);
            Blocks.Cylinder(world, "CoreBase", at + new Vector3(0f, 0.25f, -0.6f), new Vector3(2.2f, 0.5f, 2.2f), Dark, true);
            GameObject core = Blocks.Sphere(world, "Core", at + new Vector3(0f, 1.3f, -0.6f), new Vector3(1.2f, 1.2f, 1.2f),
                new Color(0.45f, 0.95f, 1f), false, true);
            core.AddComponent<Bobbing>();
            WorldLabel.Create(world, gate + new Vector3(0f, 6.6f, 0f), name, 0.8f, Color.white);
        }

        /// <summary>주요 시설: 지켜야 할 곳. 미니맵에 표시되는 자리에 건물이 선다.</summary>
        private static void BuildFacility(Transform world, Vector3 at, string name, int index)
        {
            Color[] walls = { new Color(0.86f, 0.80f, 0.66f), new Color(0.70f, 0.80f, 0.86f), new Color(0.80f, 0.72f, 0.80f) };
            Color[] roofs = { new Color(0.72f, 0.36f, 0.26f), new Color(0.24f, 0.44f, 0.62f), new Color(0.40f, 0.34f, 0.56f) };
            Color wall = walls[index % walls.Length];
            Color roof = roofs[index % roofs.Length];
            Blocks.Box(world, "Facility", at + new Vector3(0f, 1.6f, 0f), new Vector3(5f, 3.2f, 3f), wall, true);
            Blocks.Box(world, "FacilityRoof", at + new Vector3(0f, 3.4f, 0f), new Vector3(5.6f, 0.5f, 3.6f), roof);
            Blocks.Box(world, "FacilityDoor", at + new Vector3(0f, 0.9f, 1.52f), new Vector3(1.1f, 1.8f, 0.06f), Dark);
            Blocks.Box(world, "FacilityLight", at + new Vector3(0f, 2.4f, 1.52f), new Vector3(3.6f, 0.5f, 0.06f), new Color(1f, 0.92f, 0.65f), false, true);
            WorldLabel.Create(world, at + new Vector3(0f, 4.6f, 0f), name, 0.5f, Color.white);
        }

        private static void BuildBounds(Transform world, CoopMap map)
        {
            float midX = (map.minX + map.maxX) * 0.5f;
            float midZ = (map.minZ + map.maxZ) * 0.5f;
            float spanX = map.maxX - map.minX;
            float spanZ = map.maxZ - map.minZ;
            Blocks.InvisibleWall(world, "BoundN", new Vector3(midX, 4f, map.maxZ + 0.5f), new Vector3(spanX + 2f, 8f, 1f));
            Blocks.InvisibleWall(world, "BoundS", new Vector3(midX, 4f, map.minZ - 0.5f), new Vector3(spanX + 2f, 8f, 1f));
            Blocks.InvisibleWall(world, "BoundE", new Vector3(map.maxX + 0.5f, 4f, midZ), new Vector3(1f, 8f, spanZ + 2f));
            Blocks.InvisibleWall(world, "BoundW", new Vector3(map.minX - 0.5f, 4f, midZ), new Vector3(1f, 8f, spanZ + 2f));
        }
    }
}
