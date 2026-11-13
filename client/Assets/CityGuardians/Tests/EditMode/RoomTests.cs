using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>가구 배치 규칙 (docs/ROOM_SYSTEM.md 4장). 실제 서버가 같은 규칙을 지켜야 한다.</summary>
    public sealed class RoomRulesTests
    {
        private GameDefs _defs;
        private RoomLayout _room;

        [SetUp]
        public void SetUp()
        {
            _defs = DefaultDefs.Create();
            _room = RoomLayout.Default;
        }

        private static FurniturePiece Piece(string id, string itemCode) => new FurniturePiece { id = id, itemCode = itemCode };

        private static FurniturePiece Placed(string id, string itemCode, int x, int z, int rot = 0) =>
            new FurniturePiece { id = id, itemCode = itemCode, placed = true, x = x, z = z, rot = rot };

        /// <summary>가로 3 m × 세로 2 m의 작은 방. 서쪽 끝에 문, 동쪽 끝에 서야 하는 자리가 있다.</summary>
        private static RoomLayout SmallRoom()
        {
            var room = new RoomLayout(6, 4, 0f, 0f);
            room.AddStandPoint(0.5f, 0.5f);
            room.AddStandPoint(2.5f, 0.5f);
            return room;
        }

        [Test]
        public void DefaultRoom_Is12By10Cells_AndBaseFurnitureBlocksItsCells()
        {
            Assert.AreEqual(12, _room.Columns);
            Assert.AreEqual(10, _room.Rows);
            Assert.AreEqual(-3f, _room.OriginX);
            Assert.AreEqual(-2.5f, _room.OriginZ);

            Assert.IsTrue(_room.IsSolid(0, 5), "침대");
            Assert.IsTrue(_room.IsSolid(2, 9), "침대");
            Assert.IsFalse(_room.IsSolid(3, 5), "침대 옆");
            Assert.IsTrue(_room.IsSolid(6, 8), "책상");
            Assert.IsTrue(_room.IsSolid(9, 9), "책상");
            Assert.IsTrue(_room.IsSolid(9, 7), "의자");
            Assert.IsTrue(_room.IsSolid(10, 7), "옷장");
            Assert.IsTrue(_room.IsSolid(11, 9), "옷장");
            Assert.IsTrue(_room.IsSolid(0, 0), "보관함");
            Assert.IsTrue(_room.IsSolid(2, 1), "보관함");
            Assert.IsTrue(_room.IsSolid(11, 0), "화분");
            Assert.IsTrue(_room.IsSolid(6, 5), "보리");
            Assert.IsFalse(_room.IsSolid(7, 5), "보리는 한 칸만 차지한다");
            Assert.IsFalse(_room.IsSolid(5, 2), "방 한가운데");

            Assert.IsTrue(_room.IsKeepClear(10, 2), "문 앞");
            Assert.IsTrue(_room.IsKeepClear(11, 3), "문 앞");
            Assert.IsTrue(_room.IsKeepClear(3, 6), "침대 옆 (시작하는 자리)");
            Assert.IsTrue(_room.IsKeepClear(8, 7), "컴퓨터 앞");
            Assert.IsTrue(_room.IsKeepClear(9, 5), "옷장 앞");
            Assert.IsTrue(_room.IsKeepClear(0, 2), "거울 앞");
            Assert.IsTrue(_room.IsKeepClear(6, 4), "보리 앞");
            Assert.IsFalse(_room.IsKeepClear(4, 1), "남쪽 벽 앞은 가구 자리");

            Assert.IsFalse(_room.IsSolid(-1, 0));
            Assert.IsFalse(_room.IsSolid(12, 0));
        }

        [Test]
        public void DefaultRoom_EveryStandPointIsReachableFromTheDoor_AndThereIsRoomForFurniture()
        {
            bool[] occupied = RoomRules.Occupied(_defs, _room, null);
            Assert.AreEqual(6, _room.MustReach.Count);
            Assert.IsTrue(RoomRules.AllReachable(_room, occupied), "빈 방에서는 어디든 갈 수 있어야 한다");

            int placeable = 0;
            for (int cz = 0; cz < _room.Rows; cz++)
                for (int cx = 0; cx < _room.Columns; cx++)
                    if (!_room.IsSolid(cx, cz) && !_room.IsKeepClear(cx, cz)) placeable++;
            Assert.GreaterOrEqual(placeable, 40, "가구를 놓을 칸이 넉넉해야 한다");
        }

        [Test]
        public void Footprint_SwapsWidthAndDepthWhenTurned()
        {
            ItemDef sofa = _defs.FindItem(ItemCodes.Sofa);
            RoomRules.Footprint(sofa, 0, out int w0, out int d0);
            RoomRules.Footprint(sofa, 1, out int w1, out int d1);
            RoomRules.Footprint(sofa, 2, out int w2, out int d2);
            RoomRules.Footprint(sofa, 3, out int w3, out int d3);
            Assert.AreEqual((3, 2), (w0, d0));
            Assert.AreEqual((2, 3), (w1, d1));
            Assert.AreEqual((3, 2), (w2, d2));
            Assert.AreEqual((2, 3), (w3, d3));
        }

        [Test]
        public void CheckPlace_InTheDefaultRoom()
        {
            var pieces = new List<FurniturePiece>
            {
                Piece("f1", ItemCodes.Bookshelf), Piece("f2", ItemCodes.Plant), Piece("f3", ItemCodes.Sofa),
            };

            Assert.IsNull(RoomRules.CheckPlace(_defs, _room, pieces, "f1", 4, 0, 0), "남쪽 벽 앞의 책장");
            Assert.AreEqual(RoomReasons.UnknownFurniture, RoomRules.CheckPlace(_defs, _room, pieces, "f9", 4, 0, 0));
            Assert.AreEqual(RoomReasons.InvalidRotation, RoomRules.CheckPlace(_defs, _room, pieces, "f1", 4, 0, 4));
            Assert.AreEqual(RoomReasons.OutOfRoom, RoomRules.CheckPlace(_defs, _room, pieces, "f1", -1, 0, 0));
            Assert.AreEqual(RoomReasons.OutOfRoom, RoomRules.CheckPlace(_defs, _room, pieces, "f1", 11, 4, 0), "한 칸이 벽을 넘는다");
            Assert.AreEqual(RoomReasons.OutOfRoom, RoomRules.CheckPlace(_defs, _room, pieces, "f1", 4, 9, 1), "세워 놓으면 북쪽 벽을 넘는다");
            Assert.AreEqual(RoomReasons.Blocked, RoomRules.CheckPlace(_defs, _room, pieces, "f2", 1, 6, 0), "침대 위");
            Assert.AreEqual(RoomReasons.Blocked, RoomRules.CheckPlace(_defs, _room, pieces, "f1", 2, 5, 0), "침대에 한 칸 걸친다");
            Assert.AreEqual(RoomReasons.KeepClear, RoomRules.CheckPlace(_defs, _room, pieces, "f2", 10, 2, 0), "문 앞");
            Assert.AreEqual(RoomReasons.KeepClear, RoomRules.CheckPlace(_defs, _room, pieces, "f2", 3, 6, 0), "침대 옆");

            // 다른 가구와 겹치면 안 된다. 자기 자신이 있던 자리와는 겹쳐도 된다 (한 칸 옮기기).
            pieces[0] = Placed("f1", ItemCodes.Bookshelf, 4, 0);
            Assert.AreEqual(RoomReasons.Overlap, RoomRules.CheckPlace(_defs, _room, pieces, "f2", 5, 0, 0));
            Assert.IsNull(RoomRules.CheckPlace(_defs, _room, pieces, "f2", 6, 0, 0));
            Assert.IsNull(RoomRules.CheckPlace(_defs, _room, pieces, "f1", 5, 0, 0), "한 칸 옆으로 옮기기");
            Assert.IsNull(RoomRules.CheckPlace(_defs, _room, pieces, "f3", 3, 1, 0), "책장 앞의 소파");
            // 남쪽 벽 앞에 책장이 있을 때 소파를 한 칸 더 북쪽에 놓으면 거울 앞으로 가는 길이 모두 막힌다.
            Assert.AreEqual(RoomReasons.BlocksPath, RoomRules.CheckPlace(_defs, _room, pieces, "f3", 3, 2, 0));

            // 넣어 둔 가구는 자리를 차지하지 않는다.
            pieces[0] = Piece("f1", ItemCodes.Bookshelf);
            Assert.IsNull(RoomRules.CheckPlace(_defs, _room, pieces, "f2", 5, 0, 0));
        }

        [Test]
        public void CheckPlace_OnlyFurnitureGoesInTheRoom()
        {
            var pieces = new List<FurniturePiece> { Piece("f1", ItemCodes.GuardVest), Piece("f2", "fur_nothing") };
            Assert.AreEqual(RoomReasons.NotFurniture, RoomRules.CheckPlace(_defs, _room, pieces, "f1", 4, 0, 0));
            Assert.AreEqual(RoomReasons.NotFurniture, RoomRules.CheckPlace(_defs, _room, pieces, "f2", 4, 0, 0));
        }

        [Test]
        public void CheckPlace_RefusesToCutTheRoomInTwo_AndAOneCellGapIsNotAPath()
        {
            RoomLayout room = SmallRoom();
            var pieces = new List<FurniturePiece>
            {
                Piece("a", ItemCodes.Bookshelf), Piece("b", ItemCodes.Bookshelf), Piece("c", ItemCodes.Plant),
            };
            Assert.IsTrue(RoomRules.AllReachable(room, RoomRules.Occupied(_defs, room, pieces)));

            // 세운 책장 하나 (세로 두 칸): 북쪽으로 돌아갈 수 있다.
            Assert.IsNull(RoomRules.CheckPlace(_defs, room, pieces, "a", 2, 0, 1));
            pieces[0] = Placed("a", ItemCodes.Bookshelf, 2, 0, 1);

            // 그 위에 하나 더 세우면 방이 둘로 나뉜다.
            Assert.AreEqual(RoomReasons.BlocksPath, RoomRules.CheckPlace(_defs, room, pieces, "b", 2, 2, 1));
            // 한 칸(0.5 m)만 남겨도 지나갈 수 없다.
            Assert.AreEqual(RoomReasons.BlocksPath, RoomRules.CheckPlace(_defs, room, pieces, "c", 2, 3, 0));
            // 옆 줄에 놓으면 두 칸 너비의 길이 남는다.
            Assert.IsNull(RoomRules.CheckPlace(_defs, room, pieces, "c", 3, 0, 0));

            // 서야 하는 자리는 비워 둔다.
            Assert.AreEqual(RoomReasons.KeepClear, RoomRules.CheckPlace(_defs, room, pieces, "c", 0, 0, 0));
            Assert.AreEqual(RoomReasons.KeepClear, RoomRules.CheckPlace(_defs, room, pieces, "c", 5, 1, 0));
        }

        [Test]
        public void CanWalkToDoor_TellsWhetherSomeoneStandingThereWouldBeShutIn()
        {
            RoomLayout room = SmallRoom();
            var open = new List<FurniturePiece> { Placed("a", ItemCodes.Bookshelf, 2, 0, 1) };
            bool[] occupied = RoomRules.Occupied(_defs, room, open);
            Assert.IsTrue(RoomRules.CanWalkToDoor(room, occupied, 2.5f, 1.5f), "동쪽에 서 있어도 북쪽으로 돌아 나올 수 있다");

            var shut = new List<FurniturePiece>
            {
                Placed("a", ItemCodes.Bookshelf, 2, 0, 1), Placed("b", ItemCodes.Bookshelf, 2, 2, 1),
            };
            occupied = RoomRules.Occupied(_defs, room, shut);
            Assert.IsFalse(RoomRules.CanWalkToDoor(room, occupied, 2.5f, 1.5f), "동쪽에 갇힌다");
            Assert.IsTrue(RoomRules.CanWalkToDoor(room, occupied, 0.5f, 1.5f), "문 쪽은 괜찮다");
        }

        [Test]
        public void CellFor_SnapsToTheGrid_AndStaysInsideTheRoom()
        {
            ItemDef plant = _defs.FindItem(ItemCodes.Plant);
            ItemDef sofa = _defs.FindItem(ItemCodes.Sofa);

            RoomRules.CellFor(_room, plant, 0, 0.25f, -2.25f, out int x, out int z);
            Assert.AreEqual((6, 0), (x, z), "칸 한가운데");
            RoomRules.CellFor(_room, plant, 0, 0.4f, -2.1f, out x, out z);
            Assert.AreEqual((6, 0), (x, z), "칸 안의 어느 곳이든 그 칸");

            RoomRules.CellFor(_room, sofa, 0, 0f, 0f, out x, out z);
            Assert.AreEqual((5, 4), (x, z), "소파(3×2)의 가운데가 방 가운데 근처에 온다");
            RoomRules.CellFor(_room, sofa, 1, 0f, 0f, out x, out z);
            Assert.AreEqual((5, 4), (x, z));

            RoomRules.CellFor(_room, sofa, 0, 10f, 10f, out x, out z);
            Assert.AreEqual((9, 8), (x, z), "방 밖을 가리켜도 벽 안쪽에 맞춘다");
            RoomRules.CellFor(_room, sofa, 0, -10f, -10f, out x, out z);
            Assert.AreEqual((0, 0), (x, z));
        }

        [Test]
        public void CheckSell_OnlyStoredFurniture_AtTheShopThatSellsIt()
        {
            var pieces = new List<FurniturePiece>
            {
                Placed("f1", ItemCodes.Bookshelf, 4, 0), Piece("f2", ItemCodes.Plant), Placed("f3", ItemCodes.Plant, 7, 0),
            };
            Assert.IsNull(RoomRules.CheckSell(_defs, ShopCodes.Color, ItemCodes.Plant, pieces));
            Assert.AreEqual(RoomReasons.StillPlaced, RoomRules.CheckSell(_defs, ShopCodes.Color, ItemCodes.Bookshelf, pieces));
            Assert.AreEqual(ItemReasons.NotOwned, RoomRules.CheckSell(_defs, ShopCodes.Color, ItemCodes.Sofa, pieces));
            Assert.AreEqual(ItemReasons.NotSellable, RoomRules.CheckSell(_defs, ShopCodes.Miso, ItemCodes.Plant, pieces));
            Assert.AreEqual(ItemReasons.NotSellable, RoomRules.CheckSell(_defs, ShopCodes.Color, ItemCodes.WaveCap, pieces));
            Assert.AreEqual(1, RoomRules.CountStored(pieces, ItemCodes.Plant));
            Assert.AreEqual(2, RoomRules.CountOwned(pieces, ItemCodes.Plant));
            Assert.AreEqual("f2", RoomRules.FindStored(pieces, ItemCodes.Plant).id);
        }
    }

    /// <summary>모의 서버의 가구: 사기, 놓기, 옮기기, 넣어 두기, 되팔기.</summary>
    public sealed class LocalMockBackendRoomTests
    {
        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        [Test]
        public void BoughtFurniture_GoesToTheFurnitureList_NotTheBag()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MeDto me = MockKit.Boot(backend);
            Assert.AreEqual(0, me.furniture.Count);

            _kit.Earn(backend, 500);
            PurchaseResultDto first = MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b1"));
            PurchaseResultDto second = MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b2"));
            Assert.AreEqual(500 - 160, second.gc);
            Assert.AreEqual(1, first.furniture.Count);
            Assert.AreEqual(2, second.furniture.Count, "가구는 같은 것을 여러 개 가질 수 있다");
            Assert.AreEqual("f1", second.furniture[0].id);
            Assert.AreEqual("f2", second.furniture[1].id);
            Assert.IsFalse(second.furniture[1].placed, "산 가구는 넣어 둔 상태다");
            Assert.AreEqual(0, InventoryRules.CountOf(second.inventory, ItemCodes.Plant), "가방에는 들어가지 않는다");
            Assert.AreEqual(1, second.inventory.Count);

            // 같은 요청을 다시 보내도 하나만 산다.
            PurchaseResultDto resent = MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b2"));
            Assert.IsTrue(resent.duplicate);
            Assert.AreEqual(2, resent.furniture.Count);
            Assert.AreEqual(340, MockKit.Me(backend).gc);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void PlaceMoveAndStore_AreCheckedByTheServer_AndSurviveARestart()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 900);
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Bookshelf, "b1"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b2"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Sofa, "b3"));

            RoomDto room = MockKit.Ok(MockKit.Place(backend, "f1", 4, 0));
            FurniturePiece shelf = RoomRules.Find(room.furniture, "f1");
            Assert.IsTrue(shelf.placed);
            Assert.AreEqual((4, 0, 0), (shelf.x, shelf.z, shelf.rot));

            // 서버가 받아들이지 않는 자리: 아무것도 바뀌지 않는다.
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(MockKit.Place(backend, "f2", 5, 0)), "책장과 겹친다");
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(MockKit.Place(backend, "f2", 10, 2)), "문 앞");
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(MockKit.Place(backend, "f2", 1, 6)), "침대 위");
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(MockKit.Place(backend, "f3", 10, 4)), "벽 밖");
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(MockKit.Place(backend, "f2", 6, 0, 7)), "방향이 올바르지 않다");
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.Place(backend, "f99", 6, 0)));
            Assert.IsFalse(RoomRules.Find(MockKit.Me(backend).furniture, "f2").placed);

            MockKit.Ok(MockKit.Place(backend, "f2", 6, 0));
            Assert.AreEqual(ApiErrors.ValidationFailed, MockKit.Error(MockKit.Place(backend, "f3", 3, 2, 0)), "거울 앞으로 가는 길이 막힌다");
            MockKit.Ok(MockKit.Place(backend, "f3", 3, 1, 0));
            // 옮기기: 돌려서 다른 자리로.
            room = MockKit.Ok(MockKit.Place(backend, "f1", 8, 2, 1));
            shelf = RoomRules.Find(room.furniture, "f1");
            Assert.AreEqual((8, 2, 1), (shelf.x, shelf.z, shelf.rot));

            MeDto restored = MockKit.Boot(_kit.NewBackend());
            Assert.AreEqual(3, restored.furniture.Count);
            FurniturePiece sofa = RoomRules.Find(restored.furniture, "f3");
            Assert.IsTrue(sofa.placed);
            Assert.AreEqual((3, 1, 0), (sofa.x, sofa.z, sofa.rot));
            Assert.AreEqual((8, 2, 1), (RoomRules.Find(restored.furniture, "f1").x, RoomRules.Find(restored.furniture, "f1").z,
                RoomRules.Find(restored.furniture, "f1").rot));

            // 넣어 두기. 이미 넣어 둔 것을 또 넣어도 성공한다.
            LocalMockBackend again = _kit.NewBackend();
            MockKit.Boot(again);
            room = MockKit.Ok(MockKit.StoreAway(again, "f3"));
            Assert.IsFalse(RoomRules.Find(room.furniture, "f3").placed);
            Assert.IsFalse(RoomRules.Find(MockKit.Ok(MockKit.StoreAway(again, "f3")).furniture, "f3").placed);
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.StoreAway(again, "f99")));
            Assert.IsTrue(RoomRules.Find(MockKit.Me(again).furniture, "f2").placed, "다른 가구는 그대로다");
        }

        [Test]
        public void SellFurniture_OnlyStoredPieces_OneAtATime()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 160);
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b1"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b2"));
            MockKit.Ok(MockKit.Place(backend, "f1", 6, 0));
            Assert.AreEqual(0, MockKit.Me(backend).gc);

            // 넣어 둔 것(f2)이 팔린다. 놓은 것은 그대로다.
            SellResultDto sold = MockKit.Ok(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.Plant, "s1"));
            Assert.AreEqual(24, sold.priceGc);
            Assert.AreEqual(24, sold.gc);
            Assert.AreEqual(1, sold.furniture.Count);
            Assert.AreEqual("f1", sold.furniture[0].id);
            Assert.IsTrue(sold.furniture[0].placed);

            Assert.IsTrue(MockKit.Ok(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.Plant, "s1")).duplicate);
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.Plant, "s2")),
                "놓여 있는 가구는 먼저 넣어 두어야 판다");
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.Plant, "s3")));
            Assert.AreEqual(ApiErrors.NotOwned, MockKit.Error(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.Sofa, "s4")));
            Assert.AreEqual(24, MockKit.Me(backend).gc);

            MockKit.Ok(MockKit.StoreAway(backend, "f1"));
            Assert.AreEqual(48, MockKit.Ok(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.Plant, "s5")).gc);
            Assert.AreEqual(0, MockKit.Me(backend).furniture.Count);
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 새로 사면 새 번호를 받는다 (팔린 가구의 번호를 다시 쓰지 않는다).
            _kit.Earn(backend, 40);
            Assert.AreEqual("f3", MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b3")).furniture[0].id);
        }

        [Test]
        public void FurnitureLimit_StopsBuyingMore()
        {
            GameDefs defs = MockKit.Defs();
            defs.room.maxFurniture = 2;
            LocalMockBackend backend = _kit.NewBackend(defs);
            MockKit.Boot(backend);
            _kit.Earn(backend, 400);
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b1"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b2"));
            Assert.AreEqual(ApiErrors.InventoryFull, MockKit.Error(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Plant, "b3")));
            Assert.AreEqual(240, MockKit.Me(backend).gc);

            // 하나를 팔면 다시 살 수 있다.
            MockKit.Ok(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.Plant, "s1"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Lamp, "b4"));
            Assert.AreEqual(2, MockKit.Me(backend).furniture.Count);
        }

        [Test]
        public void Session_FollowsTheFurniture()
        {
            LocalMockBackend backend = _kit.NewBackend();
            GameSession session = GameSession.Start(backend, MockKit.Boot(backend));
            try
            {
                _kit.Earn(backend, 250);
                session.ApplyPurchase(MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.Bookshelf, "b1")));
                Assert.AreEqual(0, session.Me.gc);
                Assert.AreEqual(1, session.StoredFurniture(ItemCodes.Bookshelf));

                session.ApplyRoom(MockKit.Ok(MockKit.Place(backend, "f1", 4, 0)));
                Assert.AreEqual(0, session.StoredFurniture(ItemCodes.Bookshelf));
                Assert.IsTrue(session.Me.furniture[0].placed);

                session.ApplyRoom(MockKit.Ok(MockKit.StoreAway(backend, "f1")));
                session.ApplySale(MockKit.Ok(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.Bookshelf, "s1")));
                Assert.AreEqual(75, session.Me.gc);
                Assert.AreEqual(0, session.Me.furniture.Count);
            }
            finally
            {
                GameSession.End();
            }
        }
    }
}
