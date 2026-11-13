using System.Collections.Generic;
using System.Text.RegularExpressions;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>방어구·의상·되팔기의 규칙 (docs/ITEM_SYSTEM.md). 실제 서버가 같은 규칙을 지켜야 한다.</summary>
    public sealed class WearRulesTests
    {
        private GameDefs _defs;

        [SetUp]
        public void SetUp() => _defs = DefaultDefs.Create();

        private static List<ItemStack> Bag(params string[] codes)
        {
            var bag = new List<ItemStack>();
            foreach (string code in codes) bag.Add(new ItemStack(code, 1));
            return bag;
        }

        [Test]
        public void Defs_WearablesHaveASlotAndALook_FurnitureHasASize()
        {
            int wearables = 0;
            int furniture = 0;
            foreach (ItemDef item in _defs.items)
            {
                if (item.IsWearable)
                {
                    wearables++;
                    Assert.IsTrue(EquipSlots.IsWearSlot(item.slot), item.code + ": 입는 칸");
                    Assert.IsFalse(string.IsNullOrEmpty(item.look), item.code + ": 모습");
                    Assert.AreEqual(1, item.maxStack, item.code);
                    if (item.category == ItemCategory.Costume) Assert.AreEqual(0, item.defense, item.code + ": 의상에는 능력치가 없다");
                    else Assert.Greater(item.defense, 0, item.code + ": 방어구에는 방어가 있다");
                }
                if (item.IsFurniture)
                {
                    furniture++;
                    Assert.GreaterOrEqual(item.sizeX, 1, item.code);
                    Assert.GreaterOrEqual(item.sizeZ, 1, item.code);
                    Assert.IsFalse(string.IsNullOrEmpty(item.look), item.code + ": 모습");
                }
            }
            Assert.AreEqual(6, wearables);
            Assert.AreEqual(5, furniture);

            Assert.AreEqual(4, _defs.FindShop(ShopCodes.Miso).itemCodes.Length);
            Assert.AreEqual(9, _defs.FindShop(ShopCodes.Color).itemCodes.Length);
            // 모든 진열품은 정의가 있고 값이 있다.
            foreach (ShopDef shop in _defs.shops)
                foreach (string code in shop.itemCodes)
                    Assert.Greater(_defs.FindItem(code).priceGc, 0, code);
        }

        [Test]
        public void Defs_SellPriceIs30PercentOfThePrice_AndNothingSellsForMoreThanItCosts()
        {
            Assert.AreEqual(30, _defs.economy.sellBackPercent);
            Assert.Less(_defs.economy.sellBackPercent, 100);
            Assert.AreEqual(60, _defs.FindItem(ItemCodes.BoltBlaster).sellPriceGc);
            Assert.AreEqual(45, _defs.FindItem(ItemCodes.GuardHelmet).sellPriceGc);
            Assert.AreEqual(75, _defs.FindItem(ItemCodes.GuardVest).sellPriceGc);
            Assert.AreEqual(24, _defs.FindItem(ItemCodes.WaveCap).sellPriceGc);
            Assert.AreEqual(120, _defs.FindItem(ItemCodes.Sofa).sellPriceGc);

            // 로봇이 떨어뜨리는 물건을 되팔 수 있으면 사냥 보상 한도 밖의 수입이 된다.
            Assert.AreEqual(0, _defs.FindItem(ItemCodes.RepairKit).sellPriceGc);
            foreach (EnemyDef enemy in _defs.enemies)
            {
                if (string.IsNullOrEmpty(enemy.dropItemCode) || enemy.dropChance <= 0f) continue;
                Assert.AreEqual(0, _defs.FindItem(enemy.dropItemCode).sellPriceGc, enemy.code + "의 드롭은 되팔 수 없어야 한다");
            }
            Assert.AreEqual(0, _defs.FindItem(ItemCodes.SparkBaton).sellPriceGc, "기본 무기는 팔 수 없다");

            foreach (ItemDef item in _defs.items)
                if (item.sellPriceGc > 0) Assert.Less(item.sellPriceGc, item.priceGc, item.code);
        }

        [Test]
        public void Reduce_TakesAPercentageOff_ButNeverBelowOne()
        {
            Assert.AreEqual(4, DamageFormula.Reduce(4, 0));
            Assert.AreEqual(3, DamageFormula.Reduce(4, 30), "약한 적의 공격도 통째로 사라지지 않는다");
            Assert.AreEqual(6, DamageFormula.Reduce(8, 30));
            Assert.AreEqual(21, DamageFormula.Reduce(30, 30));
            Assert.AreEqual(7, DamageFormula.Reduce(8, 10));
            Assert.AreEqual(1, DamageFormula.Reduce(1, 80));
            Assert.AreEqual(20, DamageFormula.Reduce(100, 200), "아무리 입어도 80%까지만 줄어든다");
            Assert.AreEqual(8, DamageFormula.Reduce(8, -50), "음수 방어는 0으로 본다");
            Assert.AreEqual(0, DamageFormula.Reduce(0, 30));
        }

        [Test]
        public void TotalDefense_AddsHeadAndBody_CostumesAddNothing()
        {
            Assert.AreEqual(0, InventoryRules.TotalDefense(_defs, null, null));
            Assert.AreEqual(10, InventoryRules.TotalDefense(_defs, ItemCodes.GuardHelmet, null));
            Assert.AreEqual(30, InventoryRules.TotalDefense(_defs, ItemCodes.GuardHelmet, ItemCodes.GuardVest));
            Assert.AreEqual(20, InventoryRules.TotalDefense(_defs, ItemCodes.WaveCap, ItemCodes.GuardVest));
            Assert.AreEqual(0, InventoryRules.TotalDefense(_defs, ItemCodes.BunnyBand, ItemCodes.StarTee));
            Assert.AreEqual(0, InventoryRules.TotalDefense(_defs, ItemCodes.GuardVest, ItemCodes.GuardHelmet), "칸이 맞지 않으면 세지 않는다");
            Assert.AreEqual(0, InventoryRules.TotalDefense(_defs, "arm_nothing", ""));
            Assert.LessOrEqual(InventoryRules.TotalDefense(_defs, ItemCodes.GuardHelmet, ItemCodes.GuardVest),
                DamageFormula.MaxDefensePercent);
        }

        [Test]
        public void CheckEquip_HeadAndBodyTakeTheirOwnArmorOrCostume()
        {
            List<ItemStack> bag = Bag(ItemCodes.SparkBaton, ItemCodes.GuardHelmet, ItemCodes.StarTee);
            Assert.IsNull(InventoryRules.CheckEquip(_defs, bag, EquipSlots.Head, ItemCodes.GuardHelmet));
            Assert.IsNull(InventoryRules.CheckEquip(_defs, bag, EquipSlots.Body, ItemCodes.StarTee));
            Assert.AreEqual(ItemReasons.WrongSlot, InventoryRules.CheckEquip(_defs, bag, EquipSlots.Body, ItemCodes.GuardHelmet));
            Assert.AreEqual(ItemReasons.WrongSlot, InventoryRules.CheckEquip(_defs, bag, EquipSlots.Head, ItemCodes.StarTee));
            Assert.AreEqual(ItemReasons.WrongSlot, InventoryRules.CheckEquip(_defs, bag, EquipSlots.Head, ItemCodes.SparkBaton));
            Assert.AreEqual(ItemReasons.WrongSlot, InventoryRules.CheckEquip(_defs, bag, EquipSlots.Melee, ItemCodes.GuardHelmet));
            Assert.AreEqual(ItemReasons.NotOwned, InventoryRules.CheckEquip(_defs, bag, EquipSlots.Body, ItemCodes.GuardVest));
            Assert.AreEqual(ItemReasons.UnknownItem, InventoryRules.CheckEquip(_defs, bag, EquipSlots.Head, "cos_nothing"));
            Assert.IsNull(InventoryRules.CheckEquip(_defs, bag, EquipSlots.Head, null), "머리는 벗을 수 있다");
            Assert.IsNull(InventoryRules.CheckEquip(_defs, bag, EquipSlots.Body, ""), "몸도 벗을 수 있다");
            Assert.AreEqual(ItemReasons.WrongSlot, InventoryRules.CheckEquip(_defs, bag, "feet", ItemCodes.GuardHelmet));
        }

        [Test]
        public void CheckAdd_WearablesOnlyOnce_FurnitureNeverGoesInTheBag()
        {
            ItemDef helmet = _defs.FindItem(ItemCodes.GuardHelmet);
            Assert.IsNull(InventoryRules.CheckAdd(_defs, Bag(), helmet, 1));
            Assert.AreEqual(ItemReasons.AlreadyOwned, InventoryRules.CheckAdd(_defs, Bag(ItemCodes.GuardHelmet), helmet, 1));
            Assert.IsNotNull(InventoryRules.CheckAdd(_defs, Bag(), _defs.FindItem(ItemCodes.Sofa), 1));
        }

        [Test]
        public void CheckPurchase_FurnitureCountsAgainstTheFurnitureLimit_NotTheBag()
        {
            var fullBag = new List<ItemStack>();
            for (int i = 0; i < _defs.economy.inventorySlots; i++) fullBag.Add(new ItemStack("filler_" + i, 1));

            Assert.IsNull(InventoryRules.CheckPurchase(_defs, ShopCodes.Color, ItemCodes.Plant, fullBag, 80, 0),
                "가방이 가득해도 가구는 살 수 있다");
            Assert.IsNull(InventoryRules.CheckPurchase(_defs, ShopCodes.Color, ItemCodes.Plant, fullBag, 80, _defs.room.maxFurniture - 1));
            Assert.AreEqual(ItemReasons.FurnitureFull,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Color, ItemCodes.Plant, fullBag, 80, _defs.room.maxFurniture));
            Assert.AreEqual(ItemReasons.InsufficientFunds,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Color, ItemCodes.Plant, fullBag, 79, 0));
            Assert.AreEqual(ItemReasons.NotForSale,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Miso, ItemCodes.Plant, fullBag, 80, 0), "미소 상점은 가구를 팔지 않는다");
            Assert.AreEqual(ItemReasons.BagFull,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Color, ItemCodes.WaveCap, fullBag, 80, 0), "옷은 가방에 들어간다");
        }

        [Test]
        public void CheckSell_OnlyAtTheShopThatSellsIt_AndOnlyWhatTheShopBuysBack()
        {
            List<ItemStack> bag = Bag(ItemCodes.SparkBaton, ItemCodes.BoltBlaster, ItemCodes.GuardVest, ItemCodes.RepairKit);
            Assert.IsNull(InventoryRules.CheckSell(_defs, ShopCodes.Miso, ItemCodes.BoltBlaster, bag, ItemCodes.SparkBaton));
            Assert.IsNull(InventoryRules.CheckSell(_defs, ShopCodes.Miso, ItemCodes.GuardVest, bag));
            Assert.AreEqual(ItemReasons.NotSellable, InventoryRules.CheckSell(_defs, ShopCodes.Color, ItemCodes.GuardVest, bag),
                "그 물건을 파는 상점만 되산다");
            Assert.AreEqual(ItemReasons.NotSellable, InventoryRules.CheckSell(_defs, ShopCodes.Miso, ItemCodes.RepairKit, bag));
            Assert.AreEqual(ItemReasons.NotSellable, InventoryRules.CheckSell(_defs, ShopCodes.Miso, ItemCodes.SparkBaton, bag));
            Assert.AreEqual(ItemReasons.NotOwned, InventoryRules.CheckSell(_defs, ShopCodes.Miso, ItemCodes.GuardHelmet, bag));
            Assert.AreEqual(ItemReasons.UnknownShop, InventoryRules.CheckSell(_defs, "nowhere", ItemCodes.GuardVest, bag));
            Assert.AreEqual(ItemReasons.UnknownItem, InventoryRules.CheckSell(_defs, ShopCodes.Miso, "arm_nothing", bag));
            Assert.AreEqual(ItemReasons.NotSellable, InventoryRules.CheckSell(_defs, ShopCodes.Color, ItemCodes.Sofa, bag),
                "가구는 가구 규칙(RoomRules.CheckSell)으로 판다");

            // 근접 무기를 팔 수 있게 되더라도, 손에 든 것은 팔 수 없다.
            _defs.FindItem(ItemCodes.SparkBaton).sellPriceGc = 10;
            _defs.FindShop(ShopCodes.Miso).itemCodes = new[] { ItemCodes.SparkBaton };
            Assert.AreEqual(ItemReasons.CannotUnequip,
                InventoryRules.CheckSell(_defs, ShopCodes.Miso, ItemCodes.SparkBaton, bag, ItemCodes.SparkBaton));
        }
    }

    /// <summary>모의 서버의 방어구·의상·되팔기. 값은 서버가 정하고, 같은 요청은 한 번만 처리한다.</summary>
    public sealed class LocalMockBackendWearSellTests
    {
        private MockKit _kit;

        [SetUp]
        public void SetUp() => _kit = new MockKit();

        [Test]
        public void BuyAndWearArmor_ItStaysOnAfterARestart_AndCanBeSwappedOrTakenOff()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MeDto me = MockKit.Boot(backend);
            Assert.IsTrue(string.IsNullOrEmpty(me.equippedHead));
            Assert.IsTrue(string.IsNullOrEmpty(me.equippedBody));
            Assert.AreEqual(ApiErrors.NotOwned, MockKit.Error(MockKit.Equip(backend, EquipSlots.Head, ItemCodes.GuardHelmet)));

            _kit.Earn(backend, 600);
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "b1"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardVest, "b2"));
            PurchaseResultDto cap = MockKit.Ok(MockKit.Buy(backend, ShopCodes.Color, ItemCodes.WaveCap, "b3"));
            Assert.AreEqual(600 - 150 - 250 - 80, cap.gc);
            Assert.AreEqual(ApiErrors.AlreadyOwned, MockKit.Error(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardVest, "b4")));
            Assert.IsTrue(string.IsNullOrEmpty(MockKit.Me(backend).equippedHead), "산다고 저절로 입지는 않는다");

            MockKit.Ok(MockKit.Equip(backend, EquipSlots.Head, ItemCodes.GuardHelmet));
            EquipResultDto worn = MockKit.Ok(MockKit.Equip(backend, EquipSlots.Body, ItemCodes.GuardVest));
            Assert.AreEqual(ItemCodes.GuardHelmet, worn.equippedHead);
            Assert.AreEqual(ItemCodes.GuardVest, worn.equippedBody);
            Assert.AreEqual(ItemCodes.SparkBaton, worn.equippedMelee, "무기 칸은 그대로다");
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.Equip(backend, EquipSlots.Body, ItemCodes.GuardHelmet)));

            LocalMockBackend restarted = _kit.NewBackend();
            MeDto restored = MockKit.Boot(restarted);
            Assert.AreEqual(ItemCodes.GuardHelmet, restored.equippedHead);
            Assert.AreEqual(ItemCodes.GuardVest, restored.equippedBody);
            Assert.AreEqual(1, InventoryRules.CountOf(restored.inventory, ItemCodes.GuardVest), "입어도 가방에 남는다");

            // 같은 칸에 의상을 입으면 방어구가 벗겨진다 (한 칸에 하나).
            Assert.AreEqual(ItemCodes.WaveCap, MockKit.Ok(MockKit.Equip(restarted, EquipSlots.Head, ItemCodes.WaveCap)).equippedHead);
            Assert.AreEqual(1, InventoryRules.CountOf(MockKit.Me(restarted).inventory, ItemCodes.GuardHelmet), "벗은 것은 가방에 있다");
            Assert.IsTrue(string.IsNullOrEmpty(MockKit.Ok(MockKit.Equip(restarted, EquipSlots.Head, null)).equippedHead));
            Assert.AreEqual(ItemCodes.GuardVest, MockKit.Me(restarted).equippedBody);
        }

        [Test]
        public void Sell_GivesTheServerPrice_TakesTheItemOff_AndWritesTheLedger()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 250);
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardVest, "b1"));
            MockKit.Ok(MockKit.Equip(backend, EquipSlots.Body, ItemCodes.GuardVest));
            Assert.AreEqual(0, MockKit.Me(backend).gc);

            SellResultDto sold = MockKit.Ok(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardVest, "s1"));
            Assert.AreEqual(75, sold.priceGc);
            Assert.AreEqual(75, sold.gc);
            Assert.IsFalse(sold.duplicate);
            Assert.AreEqual(0, InventoryRules.CountOf(sold.inventory, ItemCodes.GuardVest));
            Assert.IsTrue(string.IsNullOrEmpty(sold.equippedBody), "입고 있던 것을 팔면 벗겨진다");
            Assert.AreEqual(ItemCodes.SparkBaton, sold.equippedMelee);

            LedgerEntry entry = backend.CurrentLedger[backend.CurrentLedger.Count - 1];
            Assert.AreEqual(75, entry.delta);
            Assert.AreEqual(LedgerReasons.ShopSale, entry.reason);
            StringAssert.Contains(ItemCodes.GuardVest, entry.reference);
            StringAssert.Contains("s1", entry.reference);
            Assert.IsNull(backend.FindLedgerInconsistency());

            MeDto me = MockKit.Me(backend);
            Assert.AreEqual(75, me.gc);
            Assert.IsTrue(string.IsNullOrEmpty(me.equippedBody));
            Assert.Less(me.gc, 250, "샀다가 되팔면 손해다");
        }

        [Test]
        public void ResendingTheSameSale_PaysOnlyOnce()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 400);
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "b1"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardVest, "b2"));

            SellResultDto first = MockKit.Ok(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "s1"));
            SellResultDto again = MockKit.Ok(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "s1"));
            Assert.AreEqual(45, first.gc);
            Assert.AreEqual(45, again.gc);
            Assert.IsTrue(again.duplicate);
            Assert.AreEqual(45, again.priceGc);

            // 껐다 켠 뒤에 다시 보내도 한 번만.
            LocalMockBackend restarted = _kit.NewBackend();
            MockKit.Boot(restarted);
            Assert.IsTrue(MockKit.Ok(MockKit.Sell(restarted, ShopCodes.Miso, ItemCodes.GuardHelmet, "s1")).duplicate);
            Assert.AreEqual(45, MockKit.Me(restarted).gc);
            // 같은 번호를 다른 물건에 쓰면 거절한다. 구매에 쓴 번호도 판매에 쓸 수 없다.
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(MockKit.Sell(restarted, ShopCodes.Miso, ItemCodes.GuardVest, "s1")));
            Assert.AreEqual(ApiErrors.Conflict, MockKit.Error(MockKit.Sell(restarted, ShopCodes.Miso, ItemCodes.GuardVest, "b2")));
            Assert.AreEqual(1, InventoryRules.CountOf(MockKit.Me(restarted).inventory, ItemCodes.GuardVest));
            Assert.IsNull(restarted.FindLedgerInconsistency());
        }

        [Test]
        public void Sell_RejectsWhatCannotBeSold_AndChangesNothing()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 400);
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "b1"));
            MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.RepairKit, "b2"));
            long gc = MockKit.Me(backend).gc;
            int ledger = backend.CurrentLedger.Count;

            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.Sell(backend, ShopCodes.Color, ItemCodes.GuardHelmet, "s1")),
                "다른 상점은 사 주지 않는다");
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.RepairKit, "s2")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.SparkBaton, "s3")));
            Assert.AreEqual(ApiErrors.NotOwned, MockKit.Error(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardVest, "s4")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.Sell(backend, "nowhere", ItemCodes.GuardHelmet, "s5")));
            Assert.AreEqual(ApiErrors.NotFound, MockKit.Error(MockKit.Sell(backend, ShopCodes.Miso, "arm_nothing", "s6")));
            Assert.AreEqual(ApiErrors.InvalidRequest, MockKit.Error(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "")));

            MeDto me = MockKit.Me(backend);
            Assert.AreEqual(gc, me.gc);
            Assert.AreEqual(ledger, backend.CurrentLedger.Count);
            Assert.AreEqual(1, InventoryRules.CountOf(me.inventory, ItemCodes.GuardHelmet));
            Assert.AreEqual(1, InventoryRules.CountOf(me.inventory, ItemCodes.RepairKit));

            // 팔고 나면 다시 팔 것이 없다.
            MockKit.Ok(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "s7"));
            Assert.AreEqual(ApiErrors.NotOwned, MockKit.Error(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "s8")));
        }

        [Test]
        public void BuyingAndSellingOverAndOver_OnlyLosesMoney()
        {
            LocalMockBackend backend = _kit.NewBackend();
            MockKit.Boot(backend);
            _kit.Earn(backend, 1000);
            long before = MockKit.Me(backend).gc;
            for (int i = 0; i < 5; i++)
            {
                MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "b" + i));
                MockKit.Ok(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "s" + i));
            }
            Assert.AreEqual(before - 5 * (150 - 45), MockKit.Me(backend).gc);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void SellingTheBlasterInHand_PutsTheBatonBackInHand()
        {
            LocalMockBackend backend = _kit.NewBackend();
            GameSession session = GameSession.Start(backend, MockKit.Boot(backend));
            try
            {
                _kit.Earn(backend, 200);
                session.ApplyPurchase(MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.BoltBlaster, "b1")));
                session.ApplyEquip(MockKit.Ok(MockKit.Equip(backend, EquipSlots.Ranged, ItemCodes.BoltBlaster)));
                Assert.IsTrue(session.SelectSlot(EquipSlots.Ranged));
                Assert.AreEqual(WeaponCodes.BoltBlaster, session.ActiveWeapon.code);

                session.ApplySale(MockKit.Ok(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.BoltBlaster, "s1")));
                Assert.AreEqual(60, session.Me.gc);
                Assert.IsNull(session.EquippedRanged);
                Assert.AreEqual(EquipSlots.Melee, session.ActiveSlot);
                Assert.AreEqual(WeaponCodes.SparkBaton, session.ActiveWeapon.code);
                Assert.IsFalse(session.Owns(ItemCodes.BoltBlaster));
            }
            finally
            {
                GameSession.End();
            }
        }

        [Test]
        public void Session_KnowsWhatIsWornAndTheDefense()
        {
            LocalMockBackend backend = _kit.NewBackend();
            GameSession session = GameSession.Start(backend, MockKit.Boot(backend));
            try
            {
                Assert.AreEqual(0, session.Defense);
                Assert.IsNull(session.Worn(EquipSlots.Head));

                _kit.Earn(backend, 500);
                session.ApplyPurchase(MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardHelmet, "b1")));
                session.ApplyPurchase(MockKit.Ok(MockKit.Buy(backend, ShopCodes.Miso, ItemCodes.GuardVest, "b2")));
                Assert.AreEqual(0, session.Defense, "가방에만 있으면 방어가 없다");

                session.ApplyEquip(MockKit.Ok(MockKit.Equip(backend, EquipSlots.Head, ItemCodes.GuardHelmet)));
                Assert.AreEqual(10, session.Defense);
                session.ApplyEquip(MockKit.Ok(MockKit.Equip(backend, EquipSlots.Body, ItemCodes.GuardVest)));
                Assert.AreEqual(30, session.Defense);
                Assert.AreEqual(ItemCodes.GuardVest, session.Worn(EquipSlots.Body).code);

                session.ApplySale(MockKit.Ok(MockKit.Sell(backend, ShopCodes.Miso, ItemCodes.GuardVest, "s1")));
                Assert.AreEqual(10, session.Defense);
                Assert.IsNull(session.Worn(EquipSlots.Body));
            }
            finally
            {
                GameSession.End();
            }
        }

        [Test]
        public void SaveFromBeforeThisUpdate_HasNoFurnitureOrTermDeposits_AndKeepsEverythingElse()
        {
            LocalMockBackend old = _kit.NewBackend();
            MeDto created = MockKit.Boot(old);
            _kit.Earn(old, 300);
            MockKit.Ok(MockKit.Buy(old, ShopCodes.Miso, ItemCodes.BoltBlaster, "b1"));
            MockKit.Ok(MockKit.Deposit(old, 50, "d1"));

            // 이번 기능이 생기기 전의 저장 데이터: 입은 것, 가구, 정기예금 항목이 없다.
            string json = _kit.Store.Read(LocalMockBackend.ServerFile);
            string stripped = Regex.Replace(json, "\"furniture\":\\[[^\\]]*\\],?", "");
            stripped = Regex.Replace(stripped, "\"termDeposits\":\\[[^\\]]*\\],?", "");
            stripped = Regex.Replace(stripped, "\"furnitureNextId\":\\d+,?", "");
            stripped = Regex.Replace(stripped, "\"equippedHead\":\"[^\"]*\",?", "");
            stripped = Regex.Replace(stripped, "\"equippedBody\":\"[^\"]*\",?", "");
            StringAssert.DoesNotContain("furniture", stripped);
            StringAssert.DoesNotContain("termDeposits", stripped);
            StringAssert.DoesNotContain("equippedHead", stripped);
            _kit.Store.Write(LocalMockBackend.ServerFile, stripped);

            LocalMockBackend upgradedBackend = _kit.NewBackend();
            MeDto upgraded = MockKit.Boot(upgradedBackend);
            Assert.AreEqual(created.playerId, upgraded.playerId);
            Assert.AreEqual(50, upgraded.gc);
            Assert.AreEqual(50, upgraded.bankGc);
            Assert.AreEqual(1, InventoryRules.CountOf(upgraded.inventory, ItemCodes.BoltBlaster));
            Assert.AreEqual(0, upgraded.furniture.Count);
            Assert.AreEqual(0, upgraded.termGc);
            Assert.IsTrue(string.IsNullOrEmpty(upgraded.equippedHead));

            // 새 기능도 바로 쓸 수 있다.
            _kit.Earn(upgradedBackend, 80);
            PurchaseResultDto bought = MockKit.Ok(MockKit.Buy(upgradedBackend, ShopCodes.Color, ItemCodes.Plant, "b2"));
            Assert.AreEqual(1, bought.furniture.Count);
            Assert.AreEqual("f1", bought.furniture[0].id);
        }
    }
}
