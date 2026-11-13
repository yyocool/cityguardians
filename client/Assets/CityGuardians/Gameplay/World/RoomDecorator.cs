using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>가구의 겉모습. 블록으로 조립한다 (docs/ROOM_SYSTEM.md 4장).</summary>
    public static class FurnitureModels
    {
        private static readonly Color Wood = new Color(0.72f, 0.55f, 0.38f);
        private static readonly Color DarkWood = new Color(0.48f, 0.34f, 0.24f);

        /// <summary>
        /// 가구 하나를 조립한다. 기준점은 차지하는 네모의 한가운데 바닥이고, 돌리지 않았을 때 앞면이 남쪽(-Z)을 본다.
        /// 충돌체는 붙이지 않는다.
        /// </summary>
        public static Transform Build(Transform parent, ItemDef item)
        {
            Transform root = Blocks.Pivot(parent, "Furniture_" + item.code, Vector3.zero);
            switch (item.look)
            {
                case "plant":
                    Blocks.Cylinder(root, "Pot", new Vector3(0f, 0.18f, 0f), new Vector3(0.34f, 0.36f, 0.34f), new Color(0.85f, 0.50f, 0.35f));
                    Blocks.Cylinder(root, "Soil", new Vector3(0f, 0.365f, 0f), new Vector3(0.28f, 0.02f, 0.28f), new Color(0.36f, 0.26f, 0.20f));
                    Blocks.Box(root, "Stem", new Vector3(0f, 0.55f, 0f), new Vector3(0.05f, 0.40f, 0.05f), new Color(0.24f, 0.52f, 0.30f));
                    Blocks.Box(root, "Leaves", new Vector3(0f, 0.80f, 0f), new Vector3(0.42f, 0.34f, 0.42f), new Color(0.30f, 0.70f, 0.40f));
                    Blocks.Box(root, "LeavesTop", new Vector3(0f, 1.03f, 0f), new Vector3(0.26f, 0.18f, 0.26f), new Color(0.42f, 0.80f, 0.46f));
                    break;

                case "lamp":
                {
                    Color metal = new Color(0.25f, 0.28f, 0.32f);
                    Color warm = new Color(1f, 0.93f, 0.70f);
                    Blocks.Cylinder(root, "Base", new Vector3(0f, 0.03f, 0f), new Vector3(0.32f, 0.06f, 0.32f), metal);
                    Blocks.Box(root, "Pole", new Vector3(0f, 0.70f, 0f), new Vector3(0.05f, 1.30f, 0.05f), metal);
                    Blocks.Cylinder(root, "Shade", new Vector3(0f, 1.42f, 0f), new Vector3(0.40f, 0.30f, 0.40f), warm, false, true);
                    var glow = new GameObject("Glow");
                    glow.transform.SetParent(root, false);
                    glow.transform.localPosition = new Vector3(0f, 1.35f, 0f);
                    var light = glow.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.color = new Color(1f, 0.85f, 0.55f);
                    light.range = 3.5f;
                    light.intensity = 1.6f;
                    light.shadows = LightShadows.None;
                    break;
                }

                case "cushion":
                    Blocks.Box(root, "Cushion", new Vector3(0f, 0.11f, 0f), new Vector3(0.92f, 0.22f, 0.92f), new Color(0.98f, 0.62f, 0.45f));
                    Blocks.Box(root, "Top", new Vector3(0f, 0.25f, 0f), new Vector3(0.76f, 0.08f, 0.76f), new Color(1f, 0.74f, 0.58f));
                    Blocks.Cylinder(root, "Button", new Vector3(0f, 0.30f, 0f), new Vector3(0.14f, 0.03f, 0.14f), Color.white);
                    break;

                case "bookshelf":
                {
                    Blocks.Box(root, "Back", new Vector3(0f, 0.75f, 0.17f), new Vector3(0.96f, 1.50f, 0.04f), DarkWood);
                    Blocks.Box(root, "SideL", new Vector3(-0.46f, 0.75f, 0f), new Vector3(0.04f, 1.50f, 0.38f), DarkWood);
                    Blocks.Box(root, "SideR", new Vector3(0.46f, 0.75f, 0f), new Vector3(0.04f, 1.50f, 0.38f), DarkWood);
                    float[] shelves = { 0.03f, 0.50f, 0.97f, 1.47f };
                    foreach (float y in shelves)
                        Blocks.Box(root, "Shelf", new Vector3(0f, y, 0f), new Vector3(0.96f, 0.05f, 0.38f), Wood);
                    Color[] books =
                    {
                        new Color(0.90f, 0.35f, 0.32f), new Color(1f, 0.80f, 0.25f), new Color(0.35f, 0.70f, 0.48f),
                        new Color(0.30f, 0.58f, 0.88f), new Color(0.62f, 0.42f, 0.86f), new Color(0.95f, 0.55f, 0.65f),
                    };
                    for (int row = 0; row < 3; row++)
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            // 책마다 키가 조금씩 다르다. 같은 가구는 언제나 같은 모습이다.
                            float height = 0.28f + ((row * 5 + i * 3) % 4) * 0.03f;
                            Blocks.Box(root, "Book", new Vector3(-0.385f + i * 0.11f, shelves[row] + 0.025f + height * 0.5f, -0.02f),
                                new Vector3(0.09f, height, 0.26f), books[(row * 2 + i) % books.Length]);
                        }
                    }
                    break;
                }

                case "sofa":
                {
                    Color seat = new Color(0.30f, 0.66f, 0.72f);
                    Color shade = new Color(0.22f, 0.52f, 0.60f);
                    Color pillow = new Color(0.55f, 0.82f, 0.86f);
                    Blocks.Box(root, "Base", new Vector3(0f, 0.20f, -0.02f), new Vector3(1.44f, 0.40f, 0.90f), seat);
                    Blocks.Box(root, "Back", new Vector3(0f, 0.62f, 0.36f), new Vector3(1.44f, 0.54f, 0.20f), shade);
                    Blocks.Box(root, "ArmL", new Vector3(-0.64f, 0.50f, -0.06f), new Vector3(0.16f, 0.30f, 0.82f), shade);
                    Blocks.Box(root, "ArmR", new Vector3(0.64f, 0.50f, -0.06f), new Vector3(0.16f, 0.30f, 0.82f), shade);
                    Blocks.Box(root, "PillowL", new Vector3(-0.29f, 0.44f, -0.12f), new Vector3(0.54f, 0.10f, 0.62f), pillow);
                    Blocks.Box(root, "PillowR", new Vector3(0.29f, 0.44f, -0.12f), new Vector3(0.54f, 0.10f, 0.62f), pillow);
                    break;
                }

                default:
                {
                    // 모르는 모습: 크기만 맞는 상자로 보여 준다.
                    Debug.LogWarning($"모르는 가구 모습: {item.look} ({item.code})");
                    float w = Mathf.Max(1, item.sizeX) * RoomLayout.CellSize;
                    float d = Mathf.Max(1, item.sizeZ) * RoomLayout.CellSize;
                    Blocks.Box(root, "Box", new Vector3(0f, 0.3f, 0f), new Vector3(w - 0.08f, 0.6f, d - 0.08f), Wood);
                    break;
                }
            }
            return root;
        }

        /// <summary>충돌체의 높이. 낮은 가구도 밟고 올라서지 못할 만큼은 된다.</summary>
        public static float BlockHeight(ItemDef item)
        {
            switch (item.look)
            {
                case "lamp": return 1.55f;
                case "bookshelf": return 1.5f;
                case "plant": return 1.1f;
                default: return 0.85f;
            }
        }
    }

    /// <summary>
    /// 방 꾸미기 (docs/ROOM_SYSTEM.md 4장). F로 시작하고 끝낸다.
    /// 가구를 꺼내 들면 캐릭터 앞의 칸에 미리 보이고, 놓을 수 있는 자리면 초록, 아니면 빨강이다.
    /// 든 가구는 앞면이 나를 보고 나와 함께 돈다 (벽을 보고 서면 앞면이 방 안쪽을 본다). E로 놓고 R로 더 돌린다.
    /// 놓을 수 있는지는 서버가 다시 확인하며, 여기서는 같은 규칙(RoomRules)으로 미리 보여 준다.
    /// </summary>
    public sealed class RoomDecorator : MonoBehaviour
    {
        public const int MinTutorialStep = 6;
        public const string BrowseHint = "E  가구 고르기      F  꾸미기 끝내기";
        public const string CarryHint = "E  놓기      R  돌리기      F  그만두기";
        public const string TakeOutPrompt = "[E] 가구 꺼내기";
        public const string TakeOutTitle = "가구 꺼내기";
        public const string MoveLabel = "옮기기";
        public const string StoreLabel = "넣어 두기";
        public const string TakeOutLabel = "다른 가구 꺼내기";
        public const string PlacePrompt = "[E] 여기에 놓기";
        public const string TooClose = "너무 가까워요. 한 걸음 물러나세요.";
        public const string WouldTrap = "여기에 놓으면 갇혀요.";

        private const float PickRadius = 1.1f;
        private const float CarryReach = 0.7f;
        private const float PlayerRadius = 0.3f;
        private const float FacingSlack = 55f;

        private static int Mod4(int value) => ((value % 4) + 4) % 4;
        private static readonly Color Good = new Color(0.35f, 0.95f, 0.50f);
        private static readonly Color Bad = new Color(1f, 0.35f, 0.30f);

        public static RoomDecorator Instance { get; private set; }

        /// <summary>가구를 들고 있는가.</summary>
        public bool IsCarrying => _carryId != null;
        public string CarryItemCode { get; private set; }
        public int CarryX { get; private set; }
        public int CarryZ { get; private set; }
        public int CarryRot { get; private set; }
        /// <summary>지금 자리에 놓을 수 없는 이유를 알리는 말. 놓을 수 있으면 null.</summary>
        public string CarryProblem { get; private set; }
        /// <summary>화면에 띄운 안내.</summary>
        public string Prompt { get; private set; }
        /// <summary>방에 세워 둔 가구의 수.</summary>
        public int BuiltCount => _built.Count;

        private sealed class Built
        {
            public GameObject go;
            public string signature;
        }

        private Transform _root;
        private PlayerController _player;
        private NpcAgent _guide;
        private Func<string> _defaultKeyHint;
        private GameSession _session;
        private readonly Dictionary<string, Built> _built = new Dictionary<string, Built>();
        private readonly List<string> _scratch = new List<string>();
        private readonly List<GameObject> _tiles = new List<GameObject>();

        private string _carryId;
        private bool _carryWasPlaced;
        /// <summary>내가 보는 쪽 (90도 단위: 0 북, 1 동, 2 남, 3 서).</summary>
        private int _facing;
        /// <summary>R로 더 돌린 횟수.</summary>
        private int _turns;
        private Transform _ghost;
        private bool _waiting;
        private FurniturePiece _near;

        /// <param name="guide">안내 로봇. 한마디 하게 하는 데 쓴다.</param>
        /// <param name="defaultKeyHint">꾸미기를 끝냈을 때 돌려놓을 키 안내.</param>
        public static RoomDecorator Create(Transform root, PlayerController player, NpcAgent guide, Func<string> defaultKeyHint)
        {
            var go = new GameObject("RoomDecorator");
            go.transform.SetParent(root, false);
            var decorator = go.AddComponent<RoomDecorator>();
            decorator._root = root;
            decorator._player = player;
            decorator._guide = guide;
            decorator._defaultKeyHint = defaultKeyHint;
            decorator._session = GameSession.Current;
            if (decorator._session != null) decorator._session.Changed += decorator.Sync;
            Instance = decorator;
            decorator.Sync();
            return decorator;
        }

        private void OnDestroy()
        {
            if (_session != null) _session.Changed -= Sync;
            if (Instance == this) Instance = null;
            if (GameState.Decorating)
            {
                GameState.Decorating = false;
                GameEvents.SetPrompt(null);
            }
        }

        // ── 방에 세우기 ─────────────────────────────────────────

        private static string Signature(FurniturePiece piece) => $"{piece.itemCode}:{piece.x}:{piece.z}:{piece.rot}";

        /// <summary>세션의 가구 목록과 방의 모습을 맞춘다. 달라진 것만 다시 세운다.</summary>
        private void Sync()
        {
            if (_session == null || GameSession.Current != _session || _root == null) return;
            List<FurniturePiece> pieces = _session.Me.furniture;

            _scratch.Clear();
            foreach (KeyValuePair<string, Built> pair in _built)
            {
                FurniturePiece piece = RoomRules.Find(pieces, pair.Key);
                if (piece == null || !piece.placed || Signature(piece) != pair.Value.signature) _scratch.Add(pair.Key);
            }
            for (int i = 0; i < _scratch.Count; i++)
            {
                Destroy(_built[_scratch[i]].go);
                _built.Remove(_scratch[i]);
            }

            if (pieces != null)
            {
                for (int i = 0; i < pieces.Count; i++)
                {
                    FurniturePiece piece = pieces[i];
                    if (!piece.placed || _built.ContainsKey(piece.id)) continue;
                    ItemDef item = _session.Defs.FindItem(piece.itemCode);
                    if (item == null || !item.IsFurniture) continue;

                    Transform model = FurnitureModels.Build(_root, item);
                    model.name = "Furniture_" + piece.id;
                    Pose(model, item, piece.x, piece.z, piece.rot, 0f);
                    // 벽과 같은 층(Ignore Raycast)이라 카메라 충돌 검사에는 걸리지 않는다.
                    model.gameObject.layer = 2;
                    float height = FurnitureModels.BlockHeight(item);
                    var box = model.gameObject.AddComponent<BoxCollider>();
                    box.size = new Vector3(Mathf.Max(1, item.sizeX) * RoomLayout.CellSize - 0.02f, height,
                        Mathf.Max(1, item.sizeZ) * RoomLayout.CellSize - 0.02f);
                    box.center = new Vector3(0f, height * 0.5f, 0f);
                    _built[piece.id] = new Built { go = model.gameObject, signature = Signature(piece) };
                }
            }

            // 옮기려고 든 가구는 제자리의 모습을 숨긴다.
            foreach (KeyValuePair<string, Built> pair in _built)
            {
                bool carried = pair.Key == _carryId;
                if (pair.Value.go.activeSelf == carried) pair.Value.go.SetActive(!carried);
            }
        }

        private static void Pose(Transform model, ItemDef item, int x, int z, int rot, float lift)
        {
            RoomLayout layout = RoomLayout.Default;
            RoomRules.Footprint(item, rot, out int width, out int depth);
            model.localPosition = new Vector3(
                layout.OriginX + (x + width * 0.5f) * RoomLayout.CellSize, lift,
                layout.OriginZ + (z + depth * 0.5f) * RoomLayout.CellSize);
            model.localRotation = Quaternion.Euler(0f, rot * 90f, 0f);
        }

        // ── 매 프레임 ───────────────────────────────────────────

        private void Update()
        {
            GameSession session = GameSession.Current;
            if (session == null || session != _session || _player == null) return;
            IPlayerInput input = PlayerInputHub.Current;

            // 메뉴나 대화가 떠 있는 동안, 그리고 그것을 닫은 바로 그 프레임에는 입력을 받지 않는다.
            bool blocked = GameState.IsControlLocked || GameRoot.Instance.IsBusy || Time.frameCount <= GameState.LastUnlockFrame;

            if (!GameState.Decorating)
            {
                if (!blocked && input.DecoratePressed) Enter(session);
                return;
            }
            if (blocked || _waiting)
            {
                Prompt = null;
                return;
            }

            if (IsCarrying) UpdateCarry(session, input);
            else UpdateBrowse(session, input);
        }

        private void LateUpdate()
        {
            // 꾸미는 동안의 안내는 여기서 띄운다 (평소의 상호작용 안내는 쉬고 있다).
            if (GameState.Decorating) GameEvents.SetPrompt(Prompt);
        }

        private void Enter(GameSession session)
        {
            if (session.TutorialStep < MinTutorialStep) return;
            List<FurniturePiece> pieces = session.Me.furniture;
            if (pieces == null || pieces.Count == 0)
            {
                GameEvents.ShowToast("가구가 아직 없어요. 알록달록 가게에서 살 수 있어요.");
                if (_guide != null) _guide.Say("가구는 알록달록 가게에서 팔아!", 4f);
                return;
            }

            GameState.Decorating = true;
            Sfx.Play(Sfx.Id.Interact);
            GameEvents.SetKeyHint(BrowseHint);
            GameEvents.ShowToast("방 꾸미기를 시작해요!");
        }

        private void Exit()
        {
            DropCarry();
            GameState.Decorating = false;
            Prompt = null;
            _near = null;
            GameEvents.SetPrompt(null);
            GameEvents.SetKeyHint(_defaultKeyHint != null ? _defaultKeyHint() : null);
            Sfx.Play(Sfx.Id.Interact);
        }

        // ── 고르기 ──────────────────────────────────────────────

        private void UpdateBrowse(GameSession session, IPlayerInput input)
        {
            if (input.DecoratePressed)
            {
                Exit();
                return;
            }

            _near = FindNearPlaced(session);
            bool anyStored = AnyStored(session);
            if (_near != null)
            {
                ItemDef item = session.Defs.FindItem(_near.itemCode);
                Prompt = $"[E] {item.name} 옮기기";
            }
            else
            {
                Prompt = anyStored ? TakeOutPrompt : "놓인 가구에 다가가면 옮길 수 있어요";
            }

            if (!input.InteractPressed) return;
            if (_near != null) OpenPieceMenu(session, _near);
            else if (anyStored) OpenStoredMenu(session);
        }

        private static bool AnyStored(GameSession session)
        {
            List<FurniturePiece> pieces = session.Me.furniture;
            if (pieces == null) return false;
            for (int i = 0; i < pieces.Count; i++)
                if (!pieces[i].placed) return true;
            return false;
        }

        /// <summary>플레이어 가까이에 놓인 가구 (차지한 네모까지의 거리로 잰다).</summary>
        private FurniturePiece FindNearPlaced(GameSession session)
        {
            List<FurniturePiece> pieces = session.Me.furniture;
            if (pieces == null) return null;
            RoomLayout layout = RoomLayout.Default;
            Vector3 position = _player.transform.position;
            FurniturePiece best = null;
            float bestDistance = PickRadius;
            for (int i = 0; i < pieces.Count; i++)
            {
                FurniturePiece piece = pieces[i];
                if (!piece.placed) continue;
                ItemDef item = session.Defs.FindItem(piece.itemCode);
                if (item == null) continue;
                RoomRules.Footprint(item, piece.rot, out int width, out int depth);
                float distance = DistanceToRect(layout, piece.x, piece.z, width, depth, position.x, position.z);
                if (distance >= bestDistance) continue;
                best = piece;
                bestDistance = distance;
            }
            return best;
        }

        private static float DistanceToRect(RoomLayout layout, int x, int z, int width, int depth, float px, float pz)
        {
            float minX = layout.OriginX + x * RoomLayout.CellSize;
            float minZ = layout.OriginZ + z * RoomLayout.CellSize;
            float maxX = minX + width * RoomLayout.CellSize;
            float maxZ = minZ + depth * RoomLayout.CellSize;
            float dx = Mathf.Max(0f, Mathf.Max(minX - px, px - maxX));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - pz, pz - maxZ));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private void OpenPieceMenu(GameSession session, FurniturePiece piece)
        {
            ItemDef item = session.Defs.FindItem(piece.itemCode);
            string id = piece.id;
            var options = new List<ChoiceOption>
            {
                new ChoiceOption
                {
                    label = MoveLabel,
                    detail = "들고 걸어가서 다른 자리에 놓아요.",
                    onChosen = () => StartCarry(session, id),
                },
                new ChoiceOption
                {
                    label = StoreLabel,
                    detail = "방에서 치워요. 언제든 다시 꺼낼 수 있어요.",
                    onChosen = () => Store(session, id),
                },
            };
            // 방이 가구로 차면 어디에 서도 가구 곁이다. 그래도 새 가구를 꺼낼 수 있어야 한다.
            if (AnyStored(session))
            {
                options.Add(new ChoiceOption
                {
                    label = TakeOutLabel,
                    detail = "넣어 둔 가구를 꺼내 놓아요.",
                    onChosen = () => OpenStoredMenu(session),
                });
            }
            ChoiceMenu.Instance.Open(item.name, "어떻게 할까요?", options.ToArray());
        }

        /// <summary>넣어 둔 가구를 종류별로 보여 준다.</summary>
        private void OpenStoredMenu(GameSession session)
        {
            var options = new List<ChoiceOption>();
            var seen = new List<string>();
            List<FurniturePiece> pieces = session.Me.furniture;
            for (int i = 0; i < pieces.Count && options.Count < ChoiceMenu.MaxOptions; i++)
            {
                FurniturePiece piece = pieces[i];
                if (piece.placed || seen.Contains(piece.itemCode)) continue;
                seen.Add(piece.itemCode);
                ItemDef item = session.Defs.FindItem(piece.itemCode);
                if (item == null) continue;
                string id = piece.id;
                options.Add(new ChoiceOption
                {
                    label = item.name,
                    cost = $"× {session.StoredFurniture(item.code)}",
                    detail = $"{item.description}  (바닥 {Mathf.Max(1, item.sizeX)} × {Mathf.Max(1, item.sizeZ)}칸)",
                    onChosen = () => StartCarry(session, id),
                });
            }
            if (options.Count == 0) return;
            ChoiceMenu.Instance.Open(TakeOutTitle, "어떤 가구를 놓을까요?", options.ToArray());
        }

        private void Store(GameSession session, string id)
        {
            _waiting = true;
            session.Backend.StoreFurniture(id, result =>
            {
                _waiting = false;
                if (this == null || GameSession.Current != session) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"가구 넣기 실패 ({id}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast("가구를 넣어 두지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    return;
                }
                session.ApplyRoom(result.data);
                Sfx.Play(Sfx.Id.Interact);
                GameEvents.ShowToast("가구를 넣어 두었어요.");
            });
        }

        // ── 들고 다니기 ─────────────────────────────────────────

        private void StartCarry(GameSession session, string id)
        {
            FurniturePiece piece = RoomRules.Find(session.Me.furniture, id);
            ItemDef item = piece != null ? session.Defs.FindItem(piece.itemCode) : null;
            if (item == null) return;

            _carryId = id;
            _carryWasPlaced = piece.placed;
            CarryItemCode = item.code;
            // 꺼낸 가구는 앞면이 나를 보게 든다. 옮기는 가구는 놓여 있던 방향 그대로 든다.
            _facing = Mod4(Mathf.RoundToInt(_player.transform.eulerAngles.y / 90f));
            _turns = piece.placed ? Mod4(piece.rot - _facing) : 0;
            CarryRot = Mod4(_facing + _turns);
            _ghost = FurnitureModels.Build(_root, item);
            _ghost.name = "FurnitureGhost";
            Sync();
            GameEvents.SetKeyHint(CarryHint);
            UpdateCarryTarget(session, item);
        }

        /// <summary>들고 있던 것을 내려놓는다 (놓지 않고). 옮기던 가구는 제자리에 그대로 있고, 꺼낸 가구는 넣어 둔 채다.</summary>
        private void DropCarry()
        {
            if (!IsCarrying) return;
            _carryId = null;
            CarryItemCode = null;
            CarryProblem = null;
            if (_ghost != null) Destroy(_ghost.gameObject);
            _ghost = null;
            for (int i = 0; i < _tiles.Count; i++) _tiles[i].SetActive(false);
            Sync();
            if (GameState.Decorating) GameEvents.SetKeyHint(BrowseHint);
        }

        private void UpdateCarry(GameSession session, IPlayerInput input)
        {
            ItemDef item = session.Defs.FindItem(CarryItemCode);
            if (item == null || RoomRules.Find(session.Me.furniture, _carryId) == null)
            {
                DropCarry();
                return;
            }

            if (input.DecoratePressed)
            {
                DropCarry();
                Sfx.Play(Sfx.Id.Interact);
                return;
            }
            // 든 가구는 나와 함께 돈다: 벽을 보고 서면 가구의 앞면이 방 안쪽을 본다.
            // 비스듬히 설 때 왔다 갔다 하지 않게, 지금 방향에서 55도 넘게 틀어져야 바꾼다.
            float yaw = _player.transform.eulerAngles.y;
            if (Mathf.Abs(Mathf.DeltaAngle(yaw, _facing * 90f)) > FacingSlack) _facing = Mod4(Mathf.RoundToInt(yaw / 90f));
            if (input.RotatePressed)
            {
                _turns = Mod4(_turns + 1);
                Sfx.Play(Sfx.Id.Interact);
            }
            CarryRot = Mod4(_facing + _turns);

            UpdateCarryTarget(session, item);
            Prompt = CarryProblem ?? PlacePrompt;

            if (input.InteractPressed)
            {
                if (CarryProblem != null)
                {
                    GameEvents.ShowToast(CarryProblem);
                    return;
                }
                Place(session);
            }
        }

        /// <summary>캐릭터 앞의 칸을 고르고, 거기에 놓을 수 있는지 살펴 미리 보기를 맞춘다.</summary>
        private void UpdateCarryTarget(GameSession session, ItemDef item)
        {
            RoomLayout layout = RoomLayout.Default;
            Vector3 position = _player.transform.position;
            Vector3 forward = _player.transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;

            // 가구의 가장자리가 캐릭터에서 조금 떨어지게, 바라보는 쪽으로 가구 크기만큼 더 내민다.
            RoomRules.Footprint(item, CarryRot, out int width, out int depth);
            float half = (Mathf.Abs(forward.x) * width + Mathf.Abs(forward.z) * depth) * RoomLayout.CellSize * 0.5f;
            Vector3 center = position + forward * (CarryReach + half);
            RoomRules.CellFor(layout, item, CarryRot, center.x, center.z, out int x, out int z);
            CarryX = x;
            CarryZ = z;
            CarryProblem = ProblemAt(session, layout, x, z, width, depth, position);

            Pose(_ghost, item, x, z, CarryRot, 0.06f + Mathf.Sin(Time.time * 4f) * 0.02f);
            ShowTiles(layout, x, z, width, depth, CarryProblem == null ? Good : Bad);
        }

        private string ProblemAt(GameSession session, RoomLayout layout, int x, int z, int width, int depth, Vector3 position)
        {
            List<FurniturePiece> pieces = session.Me.furniture;
            string reason = RoomRules.CheckPlace(session.Defs, layout, pieces, _carryId, x, z, CarryRot);
            if (reason != null) return RoomReasons.Message(reason);

            // 여기부터는 지금 서 있는 자리에 달린 검사라 클라이언트만 한다.
            if (DistanceToRect(layout, x, z, width, depth, position.x, position.z) < PlayerRadius - 0.02f) return TooClose;

            // 놓기 전에는 문까지 갈 수 있었는데 놓으면 못 가게 된다면, 스스로를 가두는 자리다.
            bool[] before = RoomRules.Occupied(session.Defs, layout, pieces, _carryId);
            if (RoomRules.CanWalkToDoor(layout, before, position.x, position.z))
            {
                for (int cz = z; cz < z + depth; cz++)
                    for (int cx = x; cx < x + width; cx++)
                        before[cz * layout.Columns + cx] = true;
                if (!RoomRules.CanWalkToDoor(layout, before, position.x, position.z)) return WouldTrap;
            }
            return null;
        }

        private void ShowTiles(RoomLayout layout, int x, int z, int width, int depth, Color color)
        {
            int needed = width * depth;
            while (_tiles.Count < needed)
            {
                GameObject tile = Blocks.Box(_root, "PlaceTile", Vector3.zero,
                    new Vector3(RoomLayout.CellSize - 0.04f, 0.012f, RoomLayout.CellSize - 0.04f), color, false, true);
                _tiles.Add(tile);
            }
            int index = 0;
            for (int cz = z; cz < z + depth; cz++)
            {
                for (int cx = x; cx < x + width; cx++)
                {
                    GameObject tile = _tiles[index++];
                    tile.transform.localPosition = new Vector3(
                        layout.OriginX + (cx + 0.5f) * RoomLayout.CellSize, 0.03f,
                        layout.OriginZ + (cz + 0.5f) * RoomLayout.CellSize);
                    Blocks.SetColor(tile, color, true);
                    if (!tile.activeSelf) tile.SetActive(true);
                }
            }
            for (int i = index; i < _tiles.Count; i++)
                if (_tiles[i].activeSelf) _tiles[i].SetActive(false);
        }

        private void Place(GameSession session)
        {
            string id = _carryId;
            bool moved = _carryWasPlaced;
            _waiting = true;
            session.Backend.PlaceFurniture(id, CarryX, CarryZ, CarryRot, result =>
            {
                _waiting = false;
                if (this == null || GameSession.Current != session) return;
                if (!result.ok)
                {
                    // 서버가 받아들이지 않았다. 서버의 말을 그대로 보여 주고 계속 들고 있는다.
                    Debug.LogWarning($"가구 놓기 실패 ({id}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast(string.IsNullOrEmpty(result.message) ? "여기에는 놓을 수 없어요." : result.message);
                    return;
                }

                // 먼저 손에서 내려놓아야, 새 자리에 세운 가구가 숨겨지지 않는다.
                DropCarry();
                session.ApplyRoom(result.data);
                Sfx.Play(Sfx.Id.Build);
                GameEvents.ShowToast(moved ? "가구를 옮겼어요!" : "가구를 놓았어요!");
            });
        }
    }
}
