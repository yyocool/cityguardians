using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>인벤토리·상점·장착 규칙 (docs/ITEM_SYSTEM.md). 실제 서버가 같은 규칙을 지켜야 한다.</summary>
    public sealed class InventoryRulesTests
    {
        private GameDefs _defs;

        [SetUp]
        public void SetUp() => _defs = DefaultDefs.Create();

        private static List<ItemStack> Bag(params (string code, int count)[] stacks)
        {
            var bag = new List<ItemStack>();
            foreach ((string code, int count) in stacks) bag.Add(new ItemStack(code, count));
            return bag;
        }

        [Test]
        public void Defs_HaveTheFirstItemsAndTheShop()
        {
            ItemDef blaster = _defs.FindItem(ItemCodes.BoltBlaster);
            Assert.AreEqual(200, blaster.priceGc);
            Assert.AreEqual(ItemCategory.WeaponRanged, blaster.category);
            WeaponDef weapon = _defs.FindWeapon(WeaponCodes.BoltBlaster);
            Assert.AreEqual(WeaponKind.Ranged, weapon.kind);
            Assert.AreEqual(14, weapon.damage);
            Assert.AreEqual(4f, weapon.attacksPerSecond);
            Assert.AreEqual(25f, weapon.range);
            Assert.Greater(weapon.projectileSpeed, 0f);

            ItemDef kit = _defs.FindItem(ItemCodes.RepairKit);
            Assert.AreEqual(30, kit.priceGc);
            Assert.AreEqual(50, kit.healHp);

            ShopDef shop = _defs.FindShop(ShopCodes.Miso);
            Assert.IsTrue(shop.Sells(ItemCodes.BoltBlaster));
            Assert.IsTrue(shop.Sells(ItemCodes.RepairKit));
            Assert.IsFalse(shop.Sells(ItemCodes.SparkBaton), "기본 무기는 팔지 않는다");

            // 무기 아이템은 모두 무기 정의가 있어야 장착할 수 있다.
            foreach (ItemDef item in _defs.items)
                if (item.IsWeapon) Assert.IsNotNull(_defs.FindWeapon(item.code), item.code);
        }

        [Test]
        public void CheckAdd_WeaponOnlyOnce_ConsumablesStackUpToTheLimit()
        {
            ItemDef blaster = _defs.FindItem(ItemCodes.BoltBlaster);
            ItemDef kit = _defs.FindItem(ItemCodes.RepairKit);

            Assert.IsNull(InventoryRules.CheckAdd(_defs, Bag(), blaster, 1));
            Assert.AreEqual(ItemReasons.AlreadyOwned,
                InventoryRules.CheckAdd(_defs, Bag((ItemCodes.BoltBlaster, 1)), blaster, 1));

            Assert.IsNull(InventoryRules.CheckAdd(_defs, Bag((ItemCodes.RepairKit, 98)), kit, 1));
            Assert.AreEqual(ItemReasons.StackFull,
                InventoryRules.CheckAdd(_defs, Bag((ItemCodes.RepairKit, 99)), kit, 1));
            Assert.AreEqual(ItemReasons.UnknownItem, InventoryRules.CheckAdd(_defs, Bag(), null, 1));
        }

        [Test]
        public void CheckAdd_ANewKindNeedsAFreeSlot_ButStackingDoesNot()
        {
            ItemDef kit = _defs.FindItem(ItemCodes.RepairKit);
            var full = new List<ItemStack>();
            for (int i = 0; i < _defs.economy.inventorySlots; i++) full.Add(new ItemStack("filler_" + i, 1));
            Assert.AreEqual(ItemReasons.BagFull, InventoryRules.CheckAdd(_defs, full, kit, 1));

            full[0] = new ItemStack(ItemCodes.RepairKit, 3);
            Assert.IsNull(InventoryRules.CheckAdd(_defs, full, kit, 1), "이미 있는 칸에 겹치는 것은 칸이 없어도 된다");
        }

        [Test]
        public void AddAndRemove_KeepOneStackPerKind_AndDropEmptyStacks()
        {
            List<ItemStack> bag = Bag();
            InventoryRules.Add(bag, ItemCodes.RepairKit, 2);
            InventoryRules.Add(bag, ItemCodes.RepairKit, 1);
            Assert.AreEqual(1, bag.Count);
            Assert.AreEqual(3, InventoryRules.CountOf(bag, ItemCodes.RepairKit));

            Assert.IsFalse(InventoryRules.Remove(bag, ItemCodes.RepairKit, 4), "모자라면 꺼내지 않는다");
            Assert.AreEqual(3, InventoryRules.CountOf(bag, ItemCodes.RepairKit));
            Assert.IsTrue(InventoryRules.Remove(bag, ItemCodes.RepairKit, 3));
            Assert.AreEqual(0, bag.Count, "0개가 된 칸은 비운다");
            Assert.IsFalse(InventoryRules.Remove(bag, ItemCodes.RepairKit, 1));
        }

        [Test]
        public void CheckPurchase_ReadsThePriceFromTheDefs()
        {
            List<ItemStack> bag = Bag((ItemCodes.SparkBaton, 1));
            Assert.AreEqual(ItemReasons.UnknownShop,
                InventoryRules.CheckPurchase(_defs, "nowhere", ItemCodes.RepairKit, bag, 1000));
            Assert.AreEqual(ItemReasons.UnknownItem,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Miso, "wpn_nothing", bag, 1000));
            Assert.AreEqual(ItemReasons.NotForSale,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Miso, ItemCodes.SparkBaton, Bag(), 1000));
            Assert.AreEqual(ItemReasons.InsufficientFunds,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Miso, ItemCodes.BoltBlaster, bag, 199));
            Assert.IsNull(InventoryRules.CheckPurchase(_defs, ShopCodes.Miso, ItemCodes.BoltBlaster, bag, 200));
            Assert.AreEqual(ItemReasons.AlreadyOwned,
                InventoryRules.CheckPurchase(_defs, ShopCodes.Miso, ItemCodes.BoltBlaster,
                    Bag((ItemCodes.BoltBlaster, 1)), 1000));
        }

        [Test]
        public void CheckEquip_NeedsTheRightSlotAndOwnership_MeleeCannotBeEmpty()
        {
            List<ItemStack> bag = Bag((ItemCodes.SparkBaton, 1), (ItemCodes.RepairKit, 2));
            Assert.IsNull(InventoryRules.CheckEquip(_defs, bag, EquipSlots.Melee, ItemCodes.SparkBaton));
            Assert.AreEqual(ItemReasons.NotOwned,
                InventoryRules.CheckEquip(_defs, bag, EquipSlots.Ranged, ItemCodes.BoltBlaster));
            Assert.AreEqual(ItemReasons.WrongSlot,
                InventoryRules.CheckEquip(_defs, bag, EquipSlots.Ranged, ItemCodes.SparkBaton));
            Assert.AreEqual(ItemReasons.WrongSlot,
                InventoryRules.CheckEquip(_defs, bag, EquipSlots.Melee, ItemCodes.RepairKit));
            Assert.AreEqual(ItemReasons.WrongSlot,
                InventoryRules.CheckEquip(_defs, bag, "head", ItemCodes.SparkBaton));
            Assert.AreEqual(ItemReasons.CannotUnequip, InventoryRules.CheckEquip(_defs, bag, EquipSlots.Melee, null));
            Assert.IsNull(InventoryRules.CheckEquip(_defs, bag, EquipSlots.Ranged, ""), "원거리 칸은 비울 수 있다");
        }

        [Test]
        public void CheckUse_OnlyOwnedConsumables()
        {
            Assert.IsNull(InventoryRules.CheckUse(_defs, Bag((ItemCodes.RepairKit, 1)), ItemCodes.RepairKit));
            Assert.AreEqual(ItemReasons.NotOwned, InventoryRules.CheckUse(_defs, Bag(), ItemCodes.RepairKit));
            Assert.AreEqual(ItemReasons.NotUsable,
                InventoryRules.CheckUse(_defs, Bag((ItemCodes.SparkBaton, 1)), ItemCodes.SparkBaton));
            Assert.AreEqual(ItemReasons.UnknownItem, InventoryRules.CheckUse(_defs, Bag(), "con_nothing"));
        }
    }

    /// <summary>맞은 적은 평소보다 멀리까지 쫓는다 (원거리 무기가 생기면서 필요해진 규칙).</summary>
    public sealed class EnemyBrainRangedTests
    {
        private static EnemyBrain AwakeBrain(EnemyDef def)
        {
            var brain = new EnemyBrain(def, new Random(1));
            brain.Tick(EnemyBrain.SpawnDuration + 0.01f, default);
            Assert.AreEqual(EnemyState.Idle, brain.State);
            return brain;
        }

        private static EnemySenses TargetAt(float distance) => new EnemySenses
        {
            hasTarget = true, targetDistance = distance, targetInsideLeash = true, atHome = true, canEngage = true,
        };

        [Test]
        public void ShotFromBlasterRange_TheEnemyComesAfterTheShooter()
        {
            EnemyDef def = DefaultDefs.Create().FindEnemy(EnemyCodes.MalfunctionRobot);
            float blasterRange = DefaultDefs.Create().FindWeapon(WeaponCodes.BoltBlaster).range;
            Assert.Greater(blasterRange, def.loseRange, "이 테스트는 사거리가 평소 추적 거리보다 길 때 의미가 있다");
            Assert.LessOrEqual(blasterRange, def.loseRange * EnemyBrain.AggravatedRangeFactor,
                "사거리 끝에서 쏴도 적이 쫓아올 수 있어야 한다");

            EnemyBrain calm = AwakeBrain(def);
            calm.Tick(0.1f, TargetAt(blasterRange));
            Assert.AreNotEqual(EnemyState.Chase, calm.State, "맞지 않은 적은 멀리 있는 플레이어를 쫓지 않는다");

            EnemyBrain shot = AwakeBrain(def);
            shot.OnDamaged(false);
            Assert.AreEqual(EnemyState.Idle, shot.State, "탄환은 적을 멈칫하게 하지 않는다");
            EnemyIntent intent = shot.Tick(0.1f, TargetAt(blasterRange));
            Assert.AreEqual(EnemyState.Chase, shot.State);
            Assert.AreEqual(EnemyMove.ToTarget, intent.move);

            // 계속 쫓아오다가, 아주 멀어지면 포기한다.
            shot.Tick(0.1f, TargetAt(blasterRange));
            Assert.AreEqual(EnemyState.Chase, shot.State);
            shot.Tick(0.1f, TargetAt(def.loseRange * EnemyBrain.AggravatedRangeFactor + 1f));
            Assert.AreEqual(EnemyState.Return, shot.State);
        }
    }

    /// <summary>모의 서버의 상점·인벤토리·장착·사용·드롭. 값은 서버가 정하고, 같은 요청은 한 번만 처리한다.</summary>
    public sealed class LocalMockBackendItemTests
    {
        private sealed class FakeClock : IClock
        {
            public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
        }

        private MemoryTextStore _store;
        private FakeClock _clock;

        [SetUp]
        public void SetUp()
        {
            _store = new MemoryTextStore();
            _clock = new FakeClock();
        }

        /// <summary>드롭이 섞이면 가방의 수를 정확히 셀 수 없으므로, 드롭을 확인하는 테스트 말고는 꺼 둔다.</summary>
        private static GameDefs DefsWithDropChance(float chance)
        {
            GameDefs defs = DefaultDefs.Create();
            defs.FindEnemy(EnemyCodes.MalfunctionRobot).dropChance = chance;
            return defs;
        }

        private LocalMockBackend NewBackend(float dropChance = 0f) =>
            new LocalMockBackend(_store, _clock, DefsWithDropChance(dropChance), 7);

        private static T Ok<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsTrue(result.ok, $"실패 응답: {result.errorCode} {result.message}");
            return result.data;
        }

        private static string Error<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsFalse(result.ok, "실패해야 하는 요청이 성공했다");
            return result.errorCode;
        }

        private static MeDto Boot(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.Bootstrap(r => result = r);
            return Ok(result);
        }

        private static MeDto Me(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.GetMe(r => result = r);
            return Ok(result);
        }

        /// <summary>사냥으로 GC를 번다 (로봇 한 대 10 GC). 한 번에 240 GC까지.</summary>
        private HuntReportDto Earn(LocalMockBackend backend, int gc)
        {
            ApiResult<HuntSessionDto> started = null;
            backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
            string session = Ok(started).sessionId;
            _clock.Advance(60);
            ApiResult<HuntReportDto> report = null;
            backend.ReportHunt(session, 1, new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, gc / 10) },
                r => report = r);
            HuntReportDto data = Ok(report);
            Assert.AreEqual(gc, data.rewardGc, "테스트 준비: 사냥 보상");
            return data;
        }

        private static ApiResult<PurchaseResultDto> Buy(LocalMockBackend backend, string itemCode, string requestId,
            string shop = ShopCodes.Miso)
        {
            ApiResult<PurchaseResultDto> result = null;
            backend.BuyItem(shop, itemCode, requestId, r => result = r);
            return result;
        }

        private static ApiResult<EquipResultDto> Equip(LocalMockBackend backend, string slot, string itemCode)
        {
            ApiResult<EquipResultDto> result = null;
            backend.EquipItem(slot, itemCode, r => result = r);
            return result;
        }

        private static ApiResult<ItemUseResultDto> Use(LocalMockBackend backend, string itemCode, string requestId)
        {
            ApiResult<ItemUseResultDto> result = null;
            backend.UseItem(itemCode, requestId, r => result = r);
            return result;
        }

        [Test]
        public void NewGuest_StartsWithTheBatonInTheBag_AndNoRangedWeapon()
        {
            MeDto me = Boot(NewBackend());
            Assert.AreEqual(1, me.inventory.Count);
            Assert.AreEqual(ItemCodes.SparkBaton, me.inventory[0].itemCode);
            Assert.AreEqual(1, me.inventory[0].count);
            Assert.AreEqual(WeaponCodes.SparkBaton, me.equippedMelee);
            Assert.IsTrue(string.IsNullOrEmpty(me.equippedRanged));
        }

        [Test]
        public void Shop_ListsItemsWithServerPrices()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            ApiResult<ShopDto> result = null;
            backend.GetShop(ShopCodes.Miso, r => result = r);
            ShopDto shop = Ok(result);
            Assert.AreEqual("미소 상점", shop.name);
            Assert.AreEqual(4, shop.items.Count);
            Assert.AreEqual(ItemCodes.BoltBlaster, shop.items[0].itemCode);
            Assert.AreEqual(200, shop.items[0].priceGc);
            Assert.AreEqual(60, shop.items[0].sellPriceGc, "되사는 값도 서버가 정한다");
            Assert.AreEqual(ItemCodes.RepairKit, shop.items[1].itemCode);
            Assert.AreEqual(30, shop.items[1].priceGc);
            Assert.AreEqual(0, shop.items[1].sellPriceGc, "수리 키트는 되사지 않는다");
            Assert.AreEqual(ItemCodes.GuardHelmet, shop.items[2].itemCode);
            Assert.AreEqual(ItemCodes.GuardVest, shop.items[3].itemCode);

            ApiResult<ShopDto> missing = null;
            backend.GetShop("nowhere", r => missing = r);
            Assert.AreEqual(ApiErrors.NotFound, Error(missing));
        }

        [Test]
        public void Buy_WithoutEnoughGc_ChangesNothing()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 190);

            Assert.AreEqual(ApiErrors.InsufficientFunds, Error(Buy(backend, ItemCodes.BoltBlaster, "r1")));
            MeDto me = Me(backend);
            Assert.AreEqual(190, me.gc);
            Assert.AreEqual(1, me.inventory.Count);
            Assert.AreEqual(1, backend.CurrentLedger.Count, "실패한 구매는 원장에 남지 않는다");
        }

        [Test]
        public void Buy_TakesTheServerPrice_AddsTheItem_AndWritesTheLedger()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 240);

            PurchaseResultDto bought = Ok(Buy(backend, ItemCodes.BoltBlaster, "req-blaster"));
            Assert.AreEqual(200, bought.priceGc);
            Assert.AreEqual(40, bought.gc);
            Assert.IsFalse(bought.duplicate);
            Assert.AreEqual(1, InventoryRules.CountOf(bought.inventory, ItemCodes.BoltBlaster));
            Assert.AreEqual(1, InventoryRules.CountOf(bought.inventory, ItemCodes.SparkBaton), "가지고 있던 것은 그대로다");

            Assert.AreEqual(2, backend.CurrentLedger.Count);
            LedgerEntry entry = backend.CurrentLedger[1];
            Assert.AreEqual(-200, entry.delta);
            Assert.AreEqual(40, entry.balanceAfter);
            Assert.AreEqual(LedgerReasons.ShopPurchase, entry.reason);
            StringAssert.Contains(ItemCodes.BoltBlaster, entry.reference);
            StringAssert.Contains("req-blaster", entry.reference);
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 산다고 저절로 장착되지는 않는다.
            Assert.IsTrue(string.IsNullOrEmpty(Me(backend).equippedRanged));
        }

        [Test]
        public void ResendingTheSamePurchase_ChargesOnlyOnce()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 240);

            PurchaseResultDto first = Ok(Buy(backend, ItemCodes.RepairKit, "req-kit"));
            // 응답을 못 받은 줄 알고 같은 요청 번호로 다시 보낸다.
            PurchaseResultDto again = Ok(Buy(backend, ItemCodes.RepairKit, "req-kit"));

            Assert.AreEqual(210, first.gc);
            Assert.AreEqual(210, again.gc);
            Assert.IsTrue(again.duplicate);
            Assert.AreEqual(30, again.priceGc);
            Assert.AreEqual(1, InventoryRules.CountOf(again.inventory, ItemCodes.RepairKit));
            Assert.AreEqual(2, backend.CurrentLedger.Count);

            // 게임을 껐다 켠 뒤에 다시 보내도 마찬가지다.
            LocalMockBackend restarted = NewBackend();
            Boot(restarted);
            PurchaseResultDto later = Ok(Buy(restarted, ItemCodes.RepairKit, "req-kit"));
            Assert.IsTrue(later.duplicate);
            Assert.AreEqual(210, later.gc);

            // 같은 번호를 다른 물건에 쓰면 거절한다.
            Assert.AreEqual(ApiErrors.Conflict, Error(Buy(restarted, ItemCodes.BoltBlaster, "req-kit")));
            // 새 번호면 새 구매다.
            Assert.AreEqual(180, Ok(Buy(restarted, ItemCodes.RepairKit, "req-kit-2")).gc);
            Assert.AreEqual(2, InventoryRules.CountOf(Me(restarted).inventory, ItemCodes.RepairKit));
            Assert.IsNull(restarted.FindLedgerInconsistency());
        }

        [Test]
        public void Buy_RejectsWhatCannotBeBought()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Earn(backend, 240);
            Earn(backend, 240);

            Ok(Buy(backend, ItemCodes.BoltBlaster, "a"));
            Assert.AreEqual(ApiErrors.AlreadyOwned, Error(Buy(backend, ItemCodes.BoltBlaster, "b")), "장비는 하나만 가진다");
            Assert.AreEqual(ApiErrors.NotFound, Error(Buy(backend, ItemCodes.SparkBaton, "c")), "팔지 않는 물건");
            Assert.AreEqual(ApiErrors.NotFound, Error(Buy(backend, "wpn_nothing", "d")));
            Assert.AreEqual(ApiErrors.NotFound, Error(Buy(backend, ItemCodes.RepairKit, "e", "nowhere")));
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Buy(backend, ItemCodes.RepairKit, "")), "요청 번호가 없으면 받지 않는다");
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Buy(backend, ItemCodes.RepairKit, new string('x', 65))));

            Assert.AreEqual(280, Me(backend).gc, "거절된 요청은 GC를 건드리지 않는다");
            Assert.AreEqual(3, backend.CurrentLedger.Count);
        }

        [Test]
        public void Equip_OnlyOwnedWeaponsInTheRightSlot_AndItSurvivesARestart()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Assert.AreEqual(ApiErrors.NotOwned, Error(Equip(backend, EquipSlots.Ranged, ItemCodes.BoltBlaster)));

            Earn(backend, 240);
            Ok(Buy(backend, ItemCodes.BoltBlaster, "a"));
            EquipResultDto equipped = Ok(Equip(backend, EquipSlots.Ranged, ItemCodes.BoltBlaster));
            Assert.AreEqual(ItemCodes.BoltBlaster, equipped.equippedRanged);
            Assert.AreEqual(ItemCodes.SparkBaton, equipped.equippedMelee);
            Assert.AreEqual(1, InventoryRules.CountOf(Me(backend).inventory, ItemCodes.BoltBlaster), "장착해도 가방에 남는다");

            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Equip(backend, EquipSlots.Ranged, ItemCodes.SparkBaton)));
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Equip(backend, EquipSlots.Melee, ItemCodes.BoltBlaster)));
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Equip(backend, EquipSlots.Melee, null)), "근접 무기는 비울 수 없다");
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Equip(backend, "head", ItemCodes.BoltBlaster)));

            MeDto restored = Boot(NewBackend());
            Assert.AreEqual(ItemCodes.BoltBlaster, restored.equippedRanged);
            Assert.AreEqual(ItemCodes.SparkBaton, restored.equippedMelee);

            LocalMockBackend again = NewBackend();
            Boot(again);
            Assert.IsTrue(string.IsNullOrEmpty(Ok(Equip(again, EquipSlots.Ranged, null)).equippedRanged), "원거리 칸은 비울 수 있다");
            Assert.IsTrue(string.IsNullOrEmpty(Me(again).equippedRanged));
        }

        [Test]
        public void UseItem_ConsumesOne_AndTheSameRequestOnlyOnce()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            Assert.AreEqual(ApiErrors.NotOwned, Error(Use(backend, ItemCodes.RepairKit, "u0")));

            Earn(backend, 240);
            Ok(Buy(backend, ItemCodes.RepairKit, "k1"));
            Ok(Buy(backend, ItemCodes.RepairKit, "k2"));

            ItemUseResultDto used = Ok(Use(backend, ItemCodes.RepairKit, "u1"));
            Assert.AreEqual(50, used.healHp, "회복량은 서버의 정의에서 온다");
            Assert.AreEqual(1, used.remaining);
            Assert.IsFalse(used.duplicate);

            ItemUseResultDto resent = Ok(Use(backend, ItemCodes.RepairKit, "u1"));
            Assert.IsTrue(resent.duplicate);
            Assert.AreEqual(50, resent.healHp);
            Assert.AreEqual(1, resent.remaining, "같은 요청은 한 번만 쓴다");

            Assert.AreEqual(0, Ok(Use(backend, ItemCodes.RepairKit, "u2")).remaining);
            Assert.AreEqual(0, InventoryRules.CountOf(Me(backend).inventory, ItemCodes.RepairKit));
            Assert.AreEqual(ApiErrors.NotOwned, Error(Use(backend, ItemCodes.RepairKit, "u3")));

            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Use(backend, ItemCodes.SparkBaton, "u4")), "무기는 쓰는 물건이 아니다");
            Assert.AreEqual(ApiErrors.InvalidRequest, Error(Use(backend, ItemCodes.RepairKit, null)));
            Assert.AreEqual(180, Me(backend).gc, "쓰는 데는 GC가 들지 않는다");
        }

        [Test]
        public void Drops_AreDecidedByTheServer_AndNotGivenTwiceOnResend()
        {
            LocalMockBackend backend = NewBackend(1f);
            Boot(backend);
            ApiResult<HuntSessionDto> started = null;
            backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
            string session = Ok(started).sessionId;
            _clock.Advance(30);

            var kills = new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, 3) };
            ApiResult<HuntReportDto> report = null;
            backend.ReportHunt(session, 1, kills, r => report = r);
            HuntReportDto first = Ok(report);
            Assert.AreEqual(1, first.drops.Count);
            Assert.AreEqual(ItemCodes.RepairKit, first.drops[0].itemCode);
            Assert.AreEqual(3, first.drops[0].count, "확률 100%면 인정한 처치마다 하나");
            Assert.AreEqual(3, InventoryRules.CountOf(first.inventory, ItemCodes.RepairKit));

            backend.ReportHunt(session, 1, kills, r => report = r);
            Ok(report);
            Assert.AreEqual(3, InventoryRules.CountOf(Me(backend).inventory, ItemCodes.RepairKit), "재전송으로 더 받지 않는다");

            // 인정하지 않은 처치는 드롭도 없다 (속도 초과).
            backend.ReportHunt(session, 2, new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, 500) }, r => report = r);
            HuntReportDto second = Ok(report);
            int accepted = second.accepted.Count > 0 ? second.accepted[0].count : 0;
            Assert.Less(accepted, 500);
            Assert.AreEqual(3 + accepted, InventoryRules.CountOf(Me(backend).inventory, ItemCodes.RepairKit));
        }

        [Test]
        public void Drops_NeverHappenAtZeroChance()
        {
            LocalMockBackend backend = NewBackend(0f);
            Boot(backend);
            HuntReportDto report = Earn(backend, 240);
            Assert.AreEqual(0, report.drops.Count);
            Assert.IsNull(report.inventory);
            Assert.AreEqual(0, InventoryRules.CountOf(Me(backend).inventory, ItemCodes.RepairKit));
        }

        [Test]
        public void SaveFromBeforeItems_GetsTheBatonInTheBag_AndKeepsEverythingElse()
        {
            LocalMockBackend old = NewBackend();
            MeDto created = Boot(old);
            Earn(old, 120);

            // 아이템 기능이 생기기 전의 저장 데이터: 가방과 요청 기록이 없다.
            string json = _store.Read(LocalMockBackend.ServerFile);
            string stripped = Regex.Replace(json, "\"inventory\":\\[[^\\]]*\\],?", "");
            stripped = Regex.Replace(stripped, "\"recentRequests\":\\[[^\\]]*\\],?", "");
            stripped = Regex.Replace(stripped, "\"equippedRanged\":\"[^\"]*\",?", "");
            StringAssert.DoesNotContain("inventory", stripped);
            _store.Write(LocalMockBackend.ServerFile, stripped);

            MeDto upgraded = Boot(NewBackend());
            Assert.AreEqual(created.playerId, upgraded.playerId);
            Assert.AreEqual(120, upgraded.gc);
            Assert.AreEqual(1, InventoryRules.CountOf(upgraded.inventory, ItemCodes.SparkBaton));
            Assert.AreEqual(WeaponCodes.SparkBaton, upgraded.equippedMelee);
            Assert.IsTrue(string.IsNullOrEmpty(upgraded.equippedRanged));
        }

        [Test]
        public void Session_HoldsTheWeaponInHand_AndRemembersItAfterARestart()
        {
            LocalMockBackend backend = NewBackend();
            GameSession session = GameSession.Start(backend, Boot(backend));
            try
            {
                Assert.AreEqual(EquipSlots.Melee, session.ActiveSlot);
                Assert.AreEqual(WeaponCodes.SparkBaton, session.ActiveWeapon.code);
                Assert.IsFalse(session.SelectSlot(EquipSlots.Ranged), "원거리 무기가 없으면 바꿀 수 없다");
                Assert.AreEqual(EquipSlots.Melee, session.ActiveSlot);

                Earn(backend, 240);
                session.ApplyPurchase(Ok(Buy(backend, ItemCodes.BoltBlaster, "a")));
                Assert.AreEqual(40, session.Me.gc);
                Assert.IsTrue(session.Owns(ItemCodes.BoltBlaster));
                Assert.IsNull(session.EquippedRanged, "사기만 하고 장착하지 않았다");

                session.ApplyEquip(Ok(Equip(backend, EquipSlots.Ranged, ItemCodes.BoltBlaster)));
                Assert.AreEqual(WeaponCodes.BoltBlaster, session.EquippedRanged.code);
                Assert.AreEqual(EquipSlots.Melee, session.ActiveSlot, "장착만으로 손에 든 무기가 바뀌지는 않는다");
                Assert.IsTrue(session.SelectSlot(EquipSlots.Ranged));
                Assert.AreEqual(WeaponCodes.BoltBlaster, session.ActiveWeapon.code);

                // 껐다 켜도 마지막에 들고 있던 무기를 든다.
                LocalMockBackend restarted = NewBackend();
                GameSession restored = GameSession.Start(restarted, Boot(restarted));
                Assert.AreEqual(EquipSlots.Ranged, restored.ActiveSlot);
                Assert.AreEqual(WeaponCodes.BoltBlaster, restored.ActiveWeapon.code);

                // 장착을 풀면 근접 무기로 돌아간다.
                restored.ApplyEquip(Ok(Equip(restarted, EquipSlots.Ranged, null)));
                Assert.AreEqual(EquipSlots.Melee, restored.ActiveSlot);
                Assert.AreEqual(WeaponCodes.SparkBaton, restored.ActiveWeapon.code);
            }
            finally
            {
                GameSession.End();
            }
        }
    }
}
