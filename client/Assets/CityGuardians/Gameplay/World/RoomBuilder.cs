using CityGuardians.Services;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 내 방 (docs/ROOM_SYSTEM.md). 게임이 시작되는 곳이며 가구는 모두 블록으로 조립한다.
    /// 벽은 안쪽에서만 보이는 판이라, 방 밖으로 나간 카메라가 인형의 집처럼 안을 들여다본다.
    /// </summary>
    public sealed class RoomBuilder : MonoBehaviour
    {
        private const float Width = 6f;
        private const float Depth = 5f;
        private const float Height = 2.8f;
        private const float CameraDistance = 4.5f;
        private const float LearnMoveDistance = 2.5f;

        private static readonly Color Wood = new Color(0.72f, 0.55f, 0.38f);
        private static readonly Color DarkWood = new Color(0.48f, 0.34f, 0.24f);
        private static readonly Color ScreenCalm = new Color(0.25f, 0.55f, 0.85f);
        private static readonly Color ScreenAlarm = new Color(1f, 0.2f, 0.15f);
        private static readonly Color ScreenDark = new Color(0.25f, 0.05f, 0.05f);

        private static readonly Vector3 ComputerPoint = new Vector3(0.9f, 0f, 1.6f);
        private static readonly Vector3 DoorPoint = new Vector3(2.5f, 0f, -1.3f);
        public static readonly Vector3 BoriPoint = new Vector3(0.25f, 0f, 0.25f);
        /// <summary>옷장 앞에 서는 자리 (RoomLayout.Default가 비워 둔다).</summary>
        public static readonly Vector3 WardrobePoint = new Vector3(2.0f, 0f, 1.2f);

        private PlayerController _player;
        private NpcAgent _bori;
        private BoriVisual _boriVisual;
        private GameObject _screen;
        private GameObject _doorGlow;
        private Light _alarmLight;
        private ObjectiveMarker _marker;
        private int _shownStep = -1;
        private float _nextAlarmSound;

        private void Start()
        {
            if (!SceneGuard.EnsureSession()) return;
            Build();
        }

        private void Build()
        {
            SetupLighting();
            Transform root = new GameObject("Room").transform;
            BuildShell(root);
            BuildFurniture(root);

            bool fromDoor = GameRoot.PendingSpawn == SpawnIds.RoomDoor;
            Vector3 spawn = fromDoor ? new Vector3(2.1f, 0f, -1.3f) : new Vector3(-1.35f, 0f, 0.9f);
            float yaw = fromDoor ? -90f : 0f;
            _player = PlayerFactory.Spawn(spawn, yaw, new Color(0.10f, 0.11f, 0.16f), CameraDistance);
            _player.respawnPoint = new Vector3(-1.35f, 0f, 0.9f);
            _player.respawnYaw = 0f;

            // 보리는 바닥 칸 하나의 한가운데에 선다 (RoomLayout.Default가 그 칸을 막아 둔다).
            _bori = NpcAgent.CreateCustom(root, "bori", BoriPoint, 200f, BoriVisual.Height, 0.28f);
            _boriVisual = BoriVisual.Create(_bori.transform);

            _marker = ObjectiveMarker.Create();
            _marker.transform.SetParent(root, true);

            // 가진 가구를 세우고, F 키의 방 꾸미기를 맡는다.
            RoomDecorator.Create(root, _player, _bori, () => KeyHintFor(Step));
        }

        /// <summary>튜토리얼을 마친 뒤 방에서 보이는 키 안내.</summary>
        public static string KeyHintFor(int step)
        {
            if (step < MapController.MinTutorialStep) return null;
            return step >= RoomDecorator.MinTutorialStep ? "M  지도      I  가방      F  방 꾸미기" : "M  지도      I  가방";
        }

        private static void SetupLighting()
        {
            RenderSettings.fog = false;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.68f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.05f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(58f, -30f, 0f);
        }

        private void BuildShell(Transform root)
        {
            float halfW = Width * 0.5f;
            float halfD = Depth * 0.5f;
            Color wall = new Color(0.93f, 0.89f, 0.80f);
            Color wallAccent = new Color(0.72f, 0.84f, 0.90f);

            Blocks.Box(root, "Floor", new Vector3(0f, -0.1f, 0f), new Vector3(Width, 0.2f, Depth),
                new Color(0.80f, 0.66f, 0.48f), true);
            // 마루 줄무늬
            for (int i = -2; i <= 2; i++)
                Blocks.Box(root, "Plank", new Vector3(i * 1.2f, 0.002f, 0f), new Vector3(0.03f, 0.004f, Depth),
                    new Color(0.70f, 0.56f, 0.40f));

            Blocks.Panel(root, "WallN", new Vector3(0f, Height * 0.5f, halfD), Vector3.back, new Vector2(Width, Height), wallAccent);
            Blocks.Panel(root, "WallS", new Vector3(0f, Height * 0.5f, -halfD), Vector3.forward, new Vector2(Width, Height), wall);
            Blocks.Panel(root, "WallE", new Vector3(halfW, Height * 0.5f, 0f), Vector3.left, new Vector2(Depth, Height), wall);
            Blocks.Panel(root, "WallW", new Vector3(-halfW, Height * 0.5f, 0f), Vector3.right, new Vector2(Depth, Height), wall);

            // 벽의 충돌체. 카메라 충돌 검사에는 걸리지 않게 한다 (Ignore Raycast).
            Blocker(root, "ColN", new Vector3(0f, Height * 0.5f, halfD + 0.25f), new Vector3(Width + 1f, Height, 0.5f));
            Blocker(root, "ColS", new Vector3(0f, Height * 0.5f, -halfD - 0.25f), new Vector3(Width + 1f, Height, 0.5f));
            Blocker(root, "ColE", new Vector3(halfW + 0.25f, Height * 0.5f, 0f), new Vector3(0.5f, Height, Depth + 1f));
            Blocker(root, "ColW", new Vector3(-halfW - 0.25f, Height * 0.5f, 0f), new Vector3(0.5f, Height, Depth + 1f));

            // 깔개
            Blocks.Box(root, "Rug", new Vector3(0f, 0.008f, -0.3f), new Vector3(2.8f, 0.016f, 1.9f), new Color(0.35f, 0.62f, 0.78f));
            Blocks.Box(root, "RugInner", new Vector3(0f, 0.012f, -0.3f), new Vector3(2.3f, 0.02f, 1.4f), new Color(0.93f, 0.90f, 0.78f));
        }

        private static void Blocker(Transform root, string name, Vector3 position, Vector3 size)
        {
            Blocks.InvisibleWall(root, name, position, size).layer = 2;
        }

        private void BuildFurniture(Transform root)
        {
            float halfW = Width * 0.5f;
            float halfD = Depth * 0.5f;

            // 침대 (북서쪽 구석): 시작 위치이자 체력을 채우는 곳
            Blocks.Box(root, "BedFrame", new Vector3(-2.3f, 0.2f, 1.4f), new Vector3(1.2f, 0.4f, 2.1f), Wood);
            Blocks.Box(root, "Mattress", new Vector3(-2.3f, 0.47f, 1.4f), new Vector3(1.1f, 0.14f, 2.0f), new Color(0.96f, 0.96f, 0.94f));
            Blocks.Box(root, "Blanket", new Vector3(-2.3f, 0.56f, 1.05f), new Vector3(1.14f, 0.08f, 1.3f), new Color(0.30f, 0.52f, 0.86f));
            Blocks.Box(root, "Pillow", new Vector3(-2.3f, 0.60f, 2.12f), new Vector3(0.8f, 0.14f, 0.4f), Color.white);
            Blocks.Box(root, "Headboard", new Vector3(-2.3f, 0.6f, 2.44f), new Vector3(1.2f, 0.9f, 0.08f), DarkWood);
            Blocker(root, "BedCol", new Vector3(-2.3f, 0.35f, 1.4f), new Vector3(1.2f, 0.7f, 2.1f));
            Spot(root, "Bed", new Vector3(-1.6f, 0f, 1.3f), "눕기", 1.3f, UseBed,
                () => Step >= 2 && _player != null && _player.Health.Current < _player.Health.Max);

            // 창문 (북쪽 벽): 벽처럼 안쪽에서만 보이는 판이라, 방 밖에서 남쪽을 보는 카메라를 가리지 않는다
            Blocks.Panel(root, "WindowFrame", new Vector3(-0.9f, 1.6f, halfD - 0.012f), Vector3.back, new Vector2(1.3f, 1.0f), Color.white);
            Blocks.Panel(root, "WindowGlass", new Vector3(-0.9f, 1.6f, halfD - 0.016f), Vector3.back, new Vector2(1.14f, 0.84f),
                new Color(0.62f, 0.86f, 1f), true);
            Blocks.Panel(root, "WindowBarV", new Vector3(-0.9f, 1.6f, halfD - 0.02f), Vector3.back, new Vector2(0.04f, 0.84f), Color.white);
            Blocks.Panel(root, "WindowBarH", new Vector3(-0.9f, 1.6f, halfD - 0.02f), Vector3.back, new Vector2(1.14f, 0.04f), Color.white);

            // 책상과 컴퓨터 (북쪽 벽)
            Blocks.Box(root, "DeskTop", new Vector3(0.9f, 0.74f, 2.1f), new Vector3(1.5f, 0.06f, 0.7f), Wood);
            foreach (float x in new[] { 0.22f, 1.58f })
                foreach (float z in new[] { 1.82f, 2.38f })
                    Blocks.Box(root, "DeskLeg", new Vector3(x, 0.36f, z), new Vector3(0.07f, 0.72f, 0.07f), DarkWood);
            Blocker(root, "DeskCol", new Vector3(0.9f, 0.4f, 2.1f), new Vector3(1.5f, 0.8f, 0.7f));
            Blocks.Box(root, "MonitorBase", new Vector3(0.9f, 0.79f, 2.25f), new Vector3(0.24f, 0.04f, 0.16f), new Color(0.2f, 0.2f, 0.24f));
            Blocks.Box(root, "MonitorNeck", new Vector3(0.9f, 0.87f, 2.28f), new Vector3(0.05f, 0.14f, 0.04f), new Color(0.2f, 0.2f, 0.24f));
            Blocks.Box(root, "Monitor", new Vector3(0.9f, 1.14f, 2.26f), new Vector3(0.74f, 0.46f, 0.05f), new Color(0.16f, 0.16f, 0.2f));
            _screen = Blocks.Box(root, "Screen", new Vector3(0.9f, 1.14f, 2.231f), new Vector3(0.66f, 0.38f, 0.012f), ScreenCalm, false, true);
            Blocks.Box(root, "Keyboard", new Vector3(0.9f, 0.785f, 1.96f), new Vector3(0.5f, 0.03f, 0.16f), new Color(0.85f, 0.85f, 0.88f));
            Blocks.Box(root, "Stool", new Vector3(1.75f, 0.24f, 1.45f), new Vector3(0.4f, 0.48f, 0.4f), new Color(0.90f, 0.50f, 0.35f));
            Blocker(root, "StoolCol", new Vector3(1.75f, 0.24f, 1.45f), new Vector3(0.4f, 0.48f, 0.4f));
            Spot(root, "Computer", ComputerPoint, "컴퓨터 보기", 1.5f, UseComputer, () => Step >= 1);

            var lightObject = new GameObject("AlarmLight");
            lightObject.transform.SetParent(root, false);
            lightObject.transform.localPosition = new Vector3(0.9f, 1.6f, 1.7f);
            _alarmLight = lightObject.AddComponent<Light>();
            _alarmLight.type = LightType.Point;
            _alarmLight.color = new Color(1f, 0.15f, 0.1f);
            _alarmLight.range = 6f;
            _alarmLight.intensity = 0f;

            // 옷장 (동쪽 벽, 북쪽)
            Blocks.Box(root, "Wardrobe", new Vector3(halfW - 0.33f, 0.95f, 1.6f), new Vector3(0.62f, 1.9f, 1.2f), DarkWood);
            Blocks.Box(root, "WardrobeGap", new Vector3(halfW - 0.645f, 0.95f, 1.6f), new Vector3(0.012f, 1.8f, 0.03f), new Color(0.25f, 0.17f, 0.12f));
            Blocks.Box(root, "WardrobeKnobA", new Vector3(halfW - 0.66f, 1.0f, 1.5f), new Vector3(0.03f, 0.12f, 0.03f), new Color(0.9f, 0.8f, 0.4f));
            Blocks.Box(root, "WardrobeKnobB", new Vector3(halfW - 0.66f, 1.0f, 1.7f), new Vector3(0.03f, 0.12f, 0.03f), new Color(0.9f, 0.8f, 0.4f));
            Blocker(root, "WardrobeCol", new Vector3(halfW - 0.33f, 0.95f, 1.6f), new Vector3(0.62f, 1.9f, 1.2f));
            Spot(root, "WardrobeSpot", WardrobePoint, "옷장 열기", 1.1f, UseWardrobe, () => Step >= 2);

            // 문 (동쪽 벽, 남쪽): 안쪽에서만 보이는 판이라 밖에서 보는 카메라를 가리지 않는다
            Vector3 doorCenter = new Vector3(halfW - 0.012f, 1.02f, -1.3f);
            _doorGlow = Blocks.Panel(root, "DoorGlow", doorCenter + new Vector3(0.004f, 0.03f, 0f), Vector3.left,
                new Vector2(1.24f, 2.22f), new Color(1f, 0.86f, 0.25f), true);
            Blocks.Panel(root, "DoorFrame", doorCenter, Vector3.left, new Vector2(1.08f, 2.1f), DarkWood);
            Blocks.Panel(root, "DoorPanel", doorCenter + new Vector3(-0.004f, -0.02f, 0f), Vector3.left,
                new Vector2(0.92f, 1.98f), new Color(0.40f, 0.62f, 0.52f));
            Blocks.Box(root, "DoorKnob", new Vector3(halfW - 0.06f, 1.0f, -0.95f), new Vector3(0.06f, 0.06f, 0.06f), new Color(0.95f, 0.85f, 0.4f));
            _doorGlow.SetActive(false);
            Spot(root, "Door", DoorPoint, "밖으로 나가기", 1.3f, UseDoor, () => Step >= 2);

            // 거울 (서쪽 벽, 남쪽): 창문처럼 안쪽에서만 보이는 판
            Blocks.Panel(root, "MirrorFrame", new Vector3(-halfW + 0.012f, 1.15f, -1.0f), Vector3.right, new Vector2(0.72f, 1.5f), Wood);
            Blocks.Panel(root, "MirrorGlass", new Vector3(-halfW + 0.016f, 1.15f, -1.0f), Vector3.right, new Vector2(0.58f, 1.36f),
                new Color(0.80f, 0.92f, 0.96f), true);
            Spot(root, "MirrorSpot", new Vector3(-halfW + 0.8f, 0f, -1.0f), "거울 보기", 1.1f, NotReadyYet, () => Step >= 2);

            // 개인 보관함 (남서쪽 구석)
            Blocks.Box(root, "Chest", new Vector3(-2.4f, 0.25f, -2.05f), new Vector3(0.95f, 0.5f, 0.6f), new Color(0.55f, 0.40f, 0.62f));
            Blocks.Box(root, "ChestLid", new Vector3(-2.4f, 0.53f, -2.05f), new Vector3(1.0f, 0.08f, 0.65f), new Color(0.42f, 0.30f, 0.50f));
            Blocks.Box(root, "ChestLock", new Vector3(-2.4f, 0.42f, -1.74f), new Vector3(0.12f, 0.14f, 0.03f), new Color(0.95f, 0.85f, 0.4f));
            Blocker(root, "ChestCol", new Vector3(-2.4f, 0.3f, -2.05f), new Vector3(0.95f, 0.6f, 0.6f));
            Spot(root, "ChestSpot", new Vector3(-2.4f, 0f, -1.3f), "보관함 열기", 1.1f, NotReadyYet, () => Step >= 2);

            // 화분 (남동쪽 구석)
            Blocks.Cylinder(root, "Pot", new Vector3(2.5f, 0.2f, -2.1f), new Vector3(0.4f, 0.4f, 0.4f), new Color(0.85f, 0.50f, 0.35f));
            Blocks.Box(root, "Plant", new Vector3(2.5f, 0.7f, -2.1f), new Vector3(0.5f, 0.6f, 0.5f), new Color(0.30f, 0.70f, 0.40f));
            Blocker(root, "PotCol", new Vector3(2.5f, 0.4f, -2.1f), new Vector3(0.45f, 0.8f, 0.45f));
        }

        private static void Spot(Transform root, string name, Vector3 position, string verb, float radius,
            System.Action onInteract, System.Func<bool> available)
        {
            var go = new GameObject(name + "Interact");
            go.transform.SetParent(root, false);
            go.transform.localPosition = position;
            Interactable.Add(go, verb, onInteract, radius, available);
        }

        private static int Step => GameSession.Current != null ? GameSession.Current.TutorialStep : 0;

        // ── 상호작용 ────────────────────────────────────────────

        private void UseComputer()
        {
            GameSession session = GameSession.Current;
            if (session.TutorialStep <= 1)
            {
                PanelInfo warning = DialogueDb.Panel("zero_warning");
                DialogueRunner.Instance.ShowPanel(warning.title, warning.body, warning.alert, AfterWarning);
            }
            else if (session.TutorialStep >= 6 && session.BankUnlocked)
            {
                // 은행을 쓸 수 있게 되면 컴퓨터에서도 은행을 연다 (은행원과 같은 화면).
                ChoiceMenu.Instance.Open("컴퓨터", "무엇을 볼까요?",
                    new ChoiceOption { label = "도시 소식", detail = "도시에서 일어나는 일을 읽어요.", onChosen = ShowCityNews },
                    new ChoiceOption
                    {
                        label = "도시은행",
                        cost = session.Me.bankGc > 0 ? $"통장 {session.Me.bankGc:N0} GC" : "",
                        detail = "GC를 맡기고 찾아요. 맡겨 둔 돈에는 매일 이자가 붙어요.",
                        onChosen = () => BankActions.Open(),
                    });
            }
            else
            {
                ShowCityNews();
            }
        }

        private static void ShowCityNews()
        {
            PanelInfo news = DialogueDb.Panel("city_news");
            DialogueRunner.Instance.ShowPanel(news.title, news.body, news.alert);
        }

        private void AfterWarning()
        {
            GameSession session = GameSession.Current;
            if (session == null || session.TutorialStep != 1) return;
            DialogueEntry entry = DialogueDb.Find("bori_after_warning", 1);
            if (entry == null) { session.AdvanceTutorial(2); return; }
            DialogueRunner.Instance.Show(_bori.DisplayName, entry.lines, () =>
            {
                if (GameSession.Current != null) GameSession.Current.AdvanceTutorial(entry.setStep);
            });
        }

        private void UseDoor()
        {
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
        }

        private void UseBed()
        {
            _player.Health.Heal(_player.Health.Max);
            GameEvents.ShowToast("푹 쉬었어요. 체력이 모두 회복됐어요.");
        }

        /// <summary>옷장: 가진 방어구와 옷을 갈아입는다.</summary>
        private void UseWardrobe()
        {
            if (ItemActions.OpenWardrobe()) return;
            _bori.Say("입을 옷이 아직 없어. 알록달록 가게에서 살 수 있어!", 4f);
        }

        private void NotReadyYet()
        {
            _bori.Say("그건 아직 준비 중이야. 조금만 기다려 줘!", 3.5f);
        }

        // ── 튜토리얼 진행 ───────────────────────────────────────

        private void Update()
        {
            GameSession session = GameSession.Current;
            if (session == null || _player == null) return;

            int step = session.TutorialStep;
            if (step != _shownStep)
            {
                _shownStep = step;
                ApplyStep(step);
            }

            if (step == 0 && _player.DistanceMoved >= LearnMoveDistance) session.AdvanceTutorial(1);
            if (step == 1) AnimateAlarm();
        }

        private void ApplyStep(int step)
        {
            _boriVisual.waving = step == 0;
            _doorGlow.SetActive(step == 2);
            if (step != 1)
            {
                Blocks.SetColor(_screen, ScreenCalm, true);
                _alarmLight.intensity = 0f;
            }
            if (step >= 2) PlayerFactory.Arm(_player);

            switch (step)
            {
                case 0:
                    GameEvents.SetObjective("W, A, S, D 키로 걸어 보세요");
                    GameEvents.SetKeyHint("W A S D  걷기      마우스  둘러보기      Shift  달리기      Space  점프");
                    _bori.Say("일어났구나! 이쪽으로 걸어와 봐!", 12f);
                    _marker.Hide();
                    break;
                case 1:
                    GameEvents.SetObjective("울리는 컴퓨터 앞에서 E 키를 누르세요");
                    GameEvents.SetKeyHint("E  살펴보기 · 말하기");
                    _bori.Say("어? 컴퓨터가 울리고 있어!", 6f);
                    _marker.PointAt(new Vector3(0.9f, 1.75f, 2.2f));
                    break;
                case 2:
                    GameEvents.SetObjective("빛나는 문 앞에서 E 키를 눌러 밖으로 나가세요");
                    GameEvents.SetKeyHint(null);
                    _bori.HideBubble(); // 컴퓨터 이야기는 끝났으므로 지난 말풍선을 남기지 않는다
                    _marker.PointAt(new Vector3(2.6f, 2.35f, -1.3f));
                    break;
                default:
                    GameEvents.SetObjective("문으로 나가면 도시예요");
                    GameEvents.SetKeyHint(KeyHintFor(step));
                    _marker.Hide();
                    break;
            }
        }

        private void AnimateAlarm()
        {
            bool on = Mathf.Repeat(Time.time, 0.5f) < 0.25f;
            Blocks.SetColor(_screen, on ? ScreenAlarm : ScreenDark, true);
            _alarmLight.intensity = on ? 2.2f : 0.4f;
            if (Time.time >= _nextAlarmSound && !GameState.IsControlLocked)
            {
                _nextAlarmSound = Time.time + 2.4f;
                Sfx.Play(Sfx.Id.Alarm, 0.6f);
            }
        }
    }
}
