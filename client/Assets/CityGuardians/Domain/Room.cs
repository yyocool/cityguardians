using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>내 방 설정 (docs/ROOM_SYSTEM.md 4장).</summary>
    [Serializable]
    public sealed class RoomConfig
    {
        /// <summary>가질 수 있는 가구의 수 (놓은 것과 넣어 둔 것을 합쳐서).</summary>
        public int maxFurniture = 20;
    }

    /// <summary>가진 가구 한 점. 가방이 아니라 가구 목록에 들어간다.</summary>
    [Serializable]
    public sealed class FurniturePiece
    {
        public string id;
        public string itemCode;
        /// <summary>방에 놓았는가. 아니면 넣어 둔 것이고 x, z, rot은 뜻이 없다.</summary>
        public bool placed;
        /// <summary>차지한 칸들 가운데 서쪽 끝 칸의 번호 (0이 서쪽 벽).</summary>
        public int x;
        /// <summary>차지한 칸들 가운데 남쪽 끝 칸의 번호 (0이 남쪽 벽).</summary>
        public int z;
        /// <summary>90도씩 돌린 횟수 (0~3).</summary>
        public int rot;

        public FurniturePiece Clone() => new FurniturePiece
        {
            id = id, itemCode = itemCode, placed = placed, x = x, z = z, rot = rot,
        };
    }

    /// <summary>가구를 그렇게 둘 수 없는 이유.</summary>
    public static class RoomReasons
    {
        public const string UnknownFurniture = "unknown_furniture";
        public const string NotFurniture = "not_furniture";
        public const string InvalidRotation = "invalid_rotation";
        /// <summary>방 밖으로 나감.</summary>
        public const string OutOfRoom = "out_of_room";
        /// <summary>침대나 책상 같은 기본 가구와 겹침.</summary>
        public const string Blocked = "blocked";
        /// <summary>문 앞처럼 비워 두어야 하는 자리.</summary>
        public const string KeepClear = "keep_clear";
        /// <summary>이미 놓은 다른 가구와 겹침.</summary>
        public const string Overlap = "overlap";
        /// <summary>놓으면 문에서 침대·책상·옷장 등으로 걸어갈 수 없게 됨.</summary>
        public const string BlocksPath = "blocks_path";
        /// <summary>그 가구는 모두 방에 놓여 있어 팔 수 없음 (먼저 넣어 두어야 함).</summary>
        public const string StillPlaced = "still_placed";

        /// <summary>화면에 보여 줄 말. 서버와 클라이언트가 같은 말을 쓴다.</summary>
        public static string Message(string reason)
        {
            switch (reason)
            {
                case null: return "";
                case UnknownFurniture: return "없는 가구예요.";
                case NotFurniture: return "방에 놓을 수 없는 물건이에요.";
                case OutOfRoom: return "방 밖으로 나가요.";
                case Blocked: return "다른 가구가 있는 자리예요.";
                case KeepClear: return "여기는 비워 둬야 해요.";
                case Overlap: return "다른 가구와 겹쳐요.";
                case BlocksPath: return "여기에 놓으면 길이 막혀요.";
                case StillPlaced: return "방에 놓여 있어요. 먼저 넣어 두세요.";
                default: return "여기에는 놓을 수 없어요.";
            }
        }
    }

    /// <summary>
    /// 방 바닥의 칸 배치 (docs/ROOM_SYSTEM.md 4장): 한 칸 0.5 m, 가로 12칸 × 세로 10칸.
    /// 어느 칸이 기본 가구로 막혀 있고, 어느 칸을 비워 두어야 하며, 어디까지 걸어갈 수 있어야 하는지를 담는다.
    /// 클라이언트의 방(RoomBuilder)과 서버의 배치 검사가 같은 배치를 쓴다.
    /// </summary>
    public sealed class RoomLayout
    {
        public const float CellSize = 0.5f;
        /// <summary>기본 가구가 칸에 이만큼(m) 넘게 걸쳐야 그 칸을 막힌 것으로 본다.</summary>
        private const float Tolerance = 0.04f;

        public int Columns { get; }
        public int Rows { get; }
        /// <summary>남서쪽 구석의 좌표 (m).</summary>
        public float OriginX { get; }
        public float OriginZ { get; }

        private readonly bool[] _solid;
        private readonly bool[] _keepClear;
        private readonly List<int> _mustReach = new List<int>();

        public RoomLayout(int columns, int rows, float originX, float originZ)
        {
            if (columns <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
            Columns = columns;
            Rows = rows;
            OriginX = originX;
            OriginZ = originZ;
            _solid = new bool[columns * rows];
            _keepClear = new bool[columns * rows];
        }

        public bool InRoom(int cx, int cz) => cx >= 0 && cz >= 0 && cx < Columns && cz < Rows;
        public bool IsSolid(int cx, int cz) => InRoom(cx, cz) && _solid[cz * Columns + cx];
        public bool IsKeepClear(int cx, int cz) => InRoom(cx, cz) && _keepClear[cz * Columns + cx];

        /// <summary>걸어갈 수 있어야 하는 자리들 (칸 모서리 번호). 첫 번째가 문 앞이다.</summary>
        public IReadOnlyList<int> MustReach => _mustReach;

        // 칸 모서리: 가로 0~Columns, 세로 0~Rows. 플레이어(지름 0.6 m)는 모서리 둘레의 네 칸이 모두 비어 있으면 그 자리에 설 수 있다.
        public int VertexIndex(int i, int j) => j * (Columns + 1) + i;
        public int VertexCount => (Columns + 1) * (Rows + 1);
        public float VertexX(int i) => OriginX + i * CellSize;
        public float VertexZ(int j) => OriginZ + j * CellSize;

        /// <summary>그 좌표에 가장 가까운 칸 모서리.</summary>
        public void NearestVertex(float x, float z, out int i, out int j)
        {
            i = Clamp((int)Math.Round((x - OriginX) / CellSize), 0, Columns);
            j = Clamp((int)Math.Round((z - OriginZ) / CellSize), 0, Rows);
        }

        /// <summary>기본 가구가 차지한 네모 (m). 걸친 칸을 모두 막는다.</summary>
        public void AddSolid(float minX, float minZ, float maxX, float maxZ) => Mark(_solid, minX, minZ, maxX, maxZ);

        /// <summary>비워 둘 네모 (m). 지나다닐 수는 있지만 가구를 놓을 수 없다.</summary>
        public void AddKeepClear(float minX, float minZ, float maxX, float maxZ) => Mark(_keepClear, minX, minZ, maxX, maxZ);

        /// <summary>
        /// 서서 무언가를 하는 자리 (m). 가장 가까운 칸 모서리 둘레의 네 칸을 비워 두고,
        /// 문에서 그 자리까지 언제나 걸어갈 수 있어야 한다.
        /// </summary>
        public void AddStandPoint(float x, float z)
        {
            NearestVertex(x, z, out int i, out int j);
            if (i < 1 || j < 1 || i > Columns - 1 || j > Rows - 1)
                throw new ArgumentException("서는 자리가 벽에 붙어 있습니다.");
            for (int cz = j - 1; cz <= j; cz++)
                for (int cx = i - 1; cx <= i; cx++)
                    _keepClear[cz * Columns + cx] = true;
            int vertex = VertexIndex(i, j);
            if (!_mustReach.Contains(vertex)) _mustReach.Add(vertex);
        }

        private void Mark(bool[] cells, float minX, float minZ, float maxX, float maxZ)
        {
            int x0 = Clamp((int)Math.Floor((minX + Tolerance - OriginX) / CellSize), 0, Columns - 1);
            int x1 = Clamp((int)Math.Ceiling((maxX - Tolerance - OriginX) / CellSize) - 1, 0, Columns - 1);
            int z0 = Clamp((int)Math.Floor((minZ + Tolerance - OriginZ) / CellSize), 0, Rows - 1);
            int z1 = Clamp((int)Math.Ceiling((maxZ - Tolerance - OriginZ) / CellSize) - 1, 0, Rows - 1);
            for (int cz = z0; cz <= z1; cz++)
                for (int cx = x0; cx <= x1; cx++)
                    cells[cz * Columns + cx] = true;
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

        private static RoomLayout _default;

        /// <summary>
        /// 지금의 내 방. 방은 6 × 5 m이고 한가운데가 (0, 0)이다. 기본 가구의 자리는 RoomBuilder의 충돌체와 같다.
        /// 기본 가구(침대, 책상, 옷장 등)는 옮길 수 없다.
        /// </summary>
        public static RoomLayout Default => _default ?? (_default = BuildDefault());

        private static RoomLayout BuildDefault()
        {
            var layout = new RoomLayout(12, 10, -3f, -2.5f);

            // 기본 가구 (RoomBuilder의 충돌체)
            layout.AddSolid(-2.9f, 0.35f, -1.7f, 2.45f);      // 침대
            layout.AddSolid(0.15f, 1.75f, 1.65f, 2.45f);      // 책상
            layout.AddSolid(1.55f, 1.25f, 1.95f, 1.65f);      // 의자
            layout.AddSolid(2.36f, 1.0f, 2.98f, 2.2f);        // 옷장
            layout.AddSolid(-2.9f, -2.375f, -1.9f, -1.725f);  // 보관함
            layout.AddSolid(2.25f, -2.35f, 2.75f, -1.85f);    // 화분
            layout.AddSolid(-0.03f, -0.03f, 0.53f, 0.53f);    // 보리 (0.25, 0.25)

            // 서서 무언가를 하는 자리. 첫 번째가 문 앞이다.
            layout.AddStandPoint(2.0f, -1.0f);   // 문
            layout.AddStandPoint(2.0f, 0.5f);    // 옷장
            layout.AddStandPoint(1.0f, 1.0f);    // 컴퓨터
            layout.AddStandPoint(-1.0f, 1.0f);   // 침대 옆 (게임을 시작하는 자리)
            layout.AddStandPoint(-2.0f, -1.0f);  // 거울과 보관함
            layout.AddStandPoint(0.5f, -0.5f);   // 보리 앞

            // 문 앞 전체와 거울 앞
            layout.AddKeepClear(1.5f, -2.0f, 3.0f, -0.5f);
            layout.AddKeepClear(-3.0f, -1.5f, -1.5f, -0.5f);
            return layout;
        }
    }

    /// <summary>
    /// 가구 배치 규칙 (docs/ROOM_SYSTEM.md 4장). Unity에 의존하지 않는다.
    /// 실제 서버(PHP)의 검증이 이 규칙과 같아야 한다.
    /// </summary>
    public static class RoomRules
    {
        /// <summary>돌린 뒤에 차지하는 칸 수 (가로, 세로).</summary>
        public static void Footprint(ItemDef item, int rot, out int width, out int depth)
        {
            int sizeX = Math.Max(1, item.sizeX);
            int sizeZ = Math.Max(1, item.sizeZ);
            bool turned = (rot & 1) == 1;
            width = turned ? sizeZ : sizeX;
            depth = turned ? sizeX : sizeZ;
        }

        public static FurniturePiece Find(IList<FurniturePiece> pieces, string id)
        {
            if (pieces == null || string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < pieces.Count; i++)
                if (pieces[i].id == id) return pieces[i];
            return null;
        }

        /// <summary>넣어 둔 그 가구의 수.</summary>
        public static int CountStored(IList<FurniturePiece> pieces, string itemCode)
        {
            int count = 0;
            if (pieces == null) return 0;
            for (int i = 0; i < pieces.Count; i++)
                if (!pieces[i].placed && pieces[i].itemCode == itemCode) count++;
            return count;
        }

        public static int CountOwned(IList<FurniturePiece> pieces, string itemCode)
        {
            int count = 0;
            if (pieces == null) return 0;
            for (int i = 0; i < pieces.Count; i++)
                if (pieces[i].itemCode == itemCode) count++;
            return count;
        }

        public static FurniturePiece FindStored(IList<FurniturePiece> pieces, string itemCode)
        {
            if (pieces == null) return null;
            for (int i = 0; i < pieces.Count; i++)
                if (!pieces[i].placed && pieces[i].itemCode == itemCode) return pieces[i];
            return null;
        }

        /// <summary>
        /// 가구로 찬 칸들 (기본 가구 포함). excludeId의 가구는 뺀다 (옮기는 중인 가구).
        /// 배열의 번호는 세로 × Columns + 가로.
        /// </summary>
        public static bool[] Occupied(GameDefs defs, RoomLayout layout, IList<FurniturePiece> pieces, string excludeId = null)
        {
            var occupied = new bool[layout.Columns * layout.Rows];
            for (int cz = 0; cz < layout.Rows; cz++)
                for (int cx = 0; cx < layout.Columns; cx++)
                    occupied[cz * layout.Columns + cx] = layout.IsSolid(cx, cz);

            if (pieces == null) return occupied;
            for (int i = 0; i < pieces.Count; i++)
            {
                FurniturePiece piece = pieces[i];
                if (!piece.placed || piece.id == excludeId) continue;
                ItemDef item = defs.FindItem(piece.itemCode);
                if (item == null) continue;
                Fill(layout, occupied, item, piece.x, piece.z, piece.rot);
            }
            return occupied;
        }

        private static void Fill(RoomLayout layout, bool[] occupied, ItemDef item, int x, int z, int rot)
        {
            Footprint(item, rot, out int width, out int depth);
            for (int cz = z; cz < z + depth; cz++)
                for (int cx = x; cx < x + width; cx++)
                    if (layout.InRoom(cx, cz)) occupied[cz * layout.Columns + cx] = true;
        }

        /// <summary>그 가구를 그 자리에 그 방향으로 놓을 수 있는가. 놓을 수 있으면 null, 아니면 이유.</summary>
        public static string CheckPlace(GameDefs defs, RoomLayout layout, IList<FurniturePiece> pieces,
            string id, int x, int z, int rot)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            FurniturePiece piece = Find(pieces, id);
            if (piece == null) return RoomReasons.UnknownFurniture;
            ItemDef item = defs.FindItem(piece.itemCode);
            if (item == null || !item.IsFurniture) return RoomReasons.NotFurniture;
            if (rot < 0 || rot > 3) return RoomReasons.InvalidRotation;

            Footprint(item, rot, out int width, out int depth);
            if (x < 0 || z < 0 || x + width > layout.Columns || z + depth > layout.Rows) return RoomReasons.OutOfRoom;

            bool[] occupied = Occupied(defs, layout, pieces, id);
            bool keepClear = false;
            bool overlap = false;
            for (int cz = z; cz < z + depth; cz++)
            {
                for (int cx = x; cx < x + width; cx++)
                {
                    if (layout.IsSolid(cx, cz)) return RoomReasons.Blocked;
                    if (layout.IsKeepClear(cx, cz)) keepClear = true;
                    if (occupied[cz * layout.Columns + cx]) overlap = true;
                }
            }
            if (keepClear) return RoomReasons.KeepClear;
            if (overlap) return RoomReasons.Overlap;

            Fill(layout, occupied, item, x, z, rot);
            return AllReachable(layout, occupied) ? null : RoomReasons.BlocksPath;
        }

        /// <summary>그 칸 모서리에 설 수 있는가: 벽에 붙지 않았고 둘레의 네 칸이 모두 비어 있다.</summary>
        public static bool CanStand(RoomLayout layout, bool[] occupied, int i, int j)
        {
            if (i < 1 || j < 1 || i > layout.Columns - 1 || j > layout.Rows - 1) return false;
            int columns = layout.Columns;
            return !occupied[(j - 1) * columns + (i - 1)] && !occupied[(j - 1) * columns + i]
                && !occupied[j * columns + (i - 1)] && !occupied[j * columns + i];
        }

        /// <summary>문 앞에서 걸어서 닿을 수 있는 칸 모서리들. 배열의 번호는 RoomLayout.VertexIndex.</summary>
        public static bool[] ReachableVertices(RoomLayout layout, bool[] occupied)
        {
            var reached = new bool[layout.VertexCount];
            if (layout.MustReach.Count == 0) return reached;

            int stride = layout.Columns + 1;
            int start = layout.MustReach[0];
            if (!CanStand(layout, occupied, start % stride, start / stride)) return reached;

            var queue = new Queue<int>();
            reached[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int vertex = queue.Dequeue();
                int i = vertex % stride;
                int j = vertex / stride;
                Visit(layout, occupied, reached, queue, i + 1, j);
                Visit(layout, occupied, reached, queue, i - 1, j);
                Visit(layout, occupied, reached, queue, i, j + 1);
                Visit(layout, occupied, reached, queue, i, j - 1);
            }
            return reached;
        }

        private static void Visit(RoomLayout layout, bool[] occupied, bool[] reached, Queue<int> queue, int i, int j)
        {
            if (!CanStand(layout, occupied, i, j)) return;
            int vertex = layout.VertexIndex(i, j);
            if (reached[vertex]) return;
            reached[vertex] = true;
            queue.Enqueue(vertex);
        }

        /// <summary>문 앞에서 서야 하는 자리 모두로 걸어갈 수 있는가.</summary>
        public static bool AllReachable(RoomLayout layout, bool[] occupied)
        {
            bool[] reached = ReachableVertices(layout, occupied);
            for (int i = 0; i < layout.MustReach.Count; i++)
                if (!reached[layout.MustReach[i]]) return false;
            return true;
        }

        /// <summary>
        /// 그 좌표(m)에 서 있는 사람이 문까지 걸어갈 수 있는가. 가구를 놓아 스스로를 가두는 일을 막는 데 쓴다.
        /// 둘레의 네 모서리 가운데 설 수 있는 곳이 하나라도 문과 이어져 있으면 된다.
        /// </summary>
        public static bool CanWalkToDoor(RoomLayout layout, bool[] occupied, float x, float z)
        {
            bool[] reached = ReachableVertices(layout, occupied);
            int i0 = (int)Math.Floor((x - layout.OriginX) / RoomLayout.CellSize);
            int j0 = (int)Math.Floor((z - layout.OriginZ) / RoomLayout.CellSize);
            for (int j = j0; j <= j0 + 1; j++)
                for (int i = i0; i <= i0 + 1; i++)
                    if (i >= 0 && j >= 0 && i <= layout.Columns && j <= layout.Rows && reached[layout.VertexIndex(i, j)]) return true;
            return false;
        }

        /// <summary>
        /// 그 좌표(m)를 가운데로 하는 자리의 칸 번호. 방 밖으로 나가지 않게 맞춘다.
        /// 가구를 들고 다닐 때 "지금 놓으면 어디에 놓이는가"를 정한다.
        /// </summary>
        public static void CellFor(RoomLayout layout, ItemDef item, int rot, float centerX, float centerZ, out int x, out int z)
        {
            Footprint(item, rot, out int width, out int depth);
            // 반올림은 언제나 같은 쪽으로 한다 (가운데에 걸쳤을 때 왔다 갔다 하지 않게).
            x = (int)Math.Floor((centerX - layout.OriginX) / RoomLayout.CellSize - width * 0.5f + 0.5f);
            z = (int)Math.Floor((centerZ - layout.OriginZ) / RoomLayout.CellSize - depth * 0.5f + 0.5f);
            x = Math.Max(0, Math.Min(layout.Columns - width, x));
            z = Math.Max(0, Math.Min(layout.Rows - depth, z));
        }

        /// <summary>
        /// 넣어 둔 그 가구 하나를 그 상점에 되팔 수 있는가. 놓여 있는 가구는 먼저 넣어 두어야 한다.
        /// </summary>
        public static string CheckSell(GameDefs defs, string shopCode, string itemCode, IList<FurniturePiece> pieces)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            ShopDef shop = defs.FindShop(shopCode);
            if (shop == null) return ItemReasons.UnknownShop;
            ItemDef item = defs.FindItem(itemCode);
            if (item == null) return ItemReasons.UnknownItem;
            if (!item.IsFurniture || !shop.Sells(itemCode) || item.sellPriceGc <= 0) return ItemReasons.NotSellable;
            if (CountOwned(pieces, itemCode) == 0) return ItemReasons.NotOwned;
            return FindStored(pieces, itemCode) == null ? RoomReasons.StillPlaced : null;
        }
    }
}
