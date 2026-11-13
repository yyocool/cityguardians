using System;
using System.Collections;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Gameplay;
using CityGuardians.Services;
using CityGuardians.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace CityGuardians.Tests
{
    /// <summary>
    /// "돈을 벌어 장비를 산다"를 처음부터 끝까지 해 본다 (docs/ITEM_SYSTEM.md):
    /// 미소 상점에서 볼트 블래스터를 사서 들고, 멀리서 로봇을 쏘고, 무기를 바꾸고, 수리 키트를 쓰고, 가방을 연다.
    /// 사람이 하듯 걸어가서 E·숫자 키·I 키·마우스를 쓴다. 상점에 갈 돈만 모의 서버에 사냥 보고를 보내 미리 마련한다
    /// (사냥으로 돈을 버는 과정은 FirstPlayTests가 확인한다).
    /// </summary>
    public sealed class ShopTests : PlayTestBase
    {
        private static readonly Vector3 MisoSpot = new Vector3(24.6f, 0f, 8f);
        private static readonly Vector3 ZoneCenter = new Vector3(37f, 0f, 33f);
        /// <summary>북동 전투 구역 남쪽 울타리의 틈 바로 안쪽.</summary>
        private static readonly Vector3 ZoneDoorstep = new Vector3(33f, 0f, 23f);
        /// <summary>북동 전투 구역의 남쪽 울타리에서 4.5 m 떨어진 길 위. 구역 밖이다.</summary>
        private static readonly Vector3 ShootingSpot = new Vector3(33f, 0f, 17f);
        private static readonly Vector3 Plaza = new Vector3(3f, 0f, -6f);

        /// <summary>게임 시간과 함께 흐르고, 테스트 준비를 위해 앞으로 감을 수도 있는 서버 시계.</summary>
        private sealed class TestClock : IClock
        {
            private readonly DateTime _start = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public double offset;
            public DateTime UtcNow => _start.AddSeconds(Time.timeAsDouble + offset);
        }

        private TestClock _clock;

        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Shop_BuyBlaster_ShootFromAfar_SwitchWeapons_UseRepairKit_OpenBag_AndKeepAfterRestart()
        {
            _clock = new TestClock();
            GameRoot.ClockOverride = _clock;
            // 드롭이 섞이면 수리 키트의 수를 정확히 셀 수 없다. 드롭은 아래의 다른 테스트가 확인한다.
            GameRoot.DefsOverride = DefsWithDropChance(0f);

            yield return BootToRoom();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            Assert.AreEqual(1, session.CountOf(ItemCodes.SparkBaton), "처음부터 배턴 하나를 가지고 있다");
            Assert.AreEqual(1, session.Me.inventory.Count);
            Assert.IsNull(session.EquippedRanged);

            // 배턴을 받기 전에는 가방이 열리지 않는다
            yield return PressInventory();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen, "배턴을 받기 전에는 가방이 없다");

            session.AdvanceTutorial(6);
            Earn(270);
            GameRoot.Instance.LoadScene(SceneNames.City, SpawnIds.Home);
            yield return UntilInCity();
            yield return null;
            Assert.AreEqual(270, session.Me.gc);

            // 돈이 모였으면 상점을 권한다. 빠른 칸은 아직 없다.
            StringAssert.Contains("미소 상점", Hud.Instance.ObjectiveText);
            Assert.IsFalse(Hud.Instance.Hotbar.Visible, "배턴 하나뿐일 때는 빠른 칸을 보여 주지 않는다");
            yield return Shot("60_city_shop_guide");

            // 원거리 무기 없이 2를 누르면 어디서 사는지 알려 준다
            yield return PressWeaponSlot(2);
            Assert.AreEqual(EquipSlots.Melee, session.ActiveSlot);
            StringAssert.Contains("미소 상점", Hud.Instance.LastToast);

            // 가방: 배턴 하나. I 키로 열고 닫는다.
            yield return PressInventory();
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual("가방", Hud.Instance.Choice.Title);
            Assert.AreEqual(1, Hud.Instance.Choice.OptionCount);
            Assert.IsFalse(ChoiceMenu.Instance.Options[0].enabled, "근접 무기 칸은 비울 수 없다");
            Assert.IsTrue(GameState.IsControlLocked);
            yield return PressInventory();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen, "I 키로 닫는다");
            Assert.IsFalse(GameState.IsControlLocked);

            // 미소 상점: 진열과 값은 서버에서 온다
            yield return TalkToMiso();
            Assert.AreEqual("미소 상점", Hud.Instance.Choice.Title);
            Assert.AreEqual(ItemActions.ShopTag, ChoiceMenu.Instance.Tag);
            Assert.AreEqual(5, Hud.Instance.Choice.OptionCount, "물건 넷과 '물건 팔기'");
            Assert.AreEqual(ItemActions.SellLabel, ChoiceMenu.Instance.Options[4].label);
            StringAssert.Contains("270 GC", ChoiceMenu.Instance.Body);
            Assert.AreEqual("볼트 블래스터", ChoiceMenu.Instance.Options[0].label);
            Assert.AreEqual("200 GC", ChoiceMenu.Instance.Options[0].cost);
            Assert.IsTrue(ChoiceMenu.Instance.Options[0].enabled);
            Assert.AreEqual("수리 키트", ChoiceMenu.Instance.Options[1].label);
            Assert.AreEqual("30 GC", ChoiceMenu.Instance.Options[1].cost);
            yield return Shot("61_shop_menu");

            // I 키는 상점을 닫지 않는다 (가방만 여닫는다)
            yield return PressInventory();
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen);
            Assert.AreEqual(ItemActions.ShopTag, ChoiceMenu.Instance.Tag);

            // 볼트 블래스터 구매 → 바로 쓸지 묻는다
            yield return PressChoice(1);
            Assert.AreEqual(70, session.Me.gc, "200 GC가 빠져야 한다");
            Assert.IsTrue(session.Owns(ItemCodes.BoltBlaster));
            Assert.AreEqual(LedgerReasons.ShopPurchase, backend.CurrentLedger[backend.CurrentLedger.Count - 1].reason);
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "산 장비를 바로 쓸지 물어야 한다");
            Assert.AreEqual("새 장비: 볼트 블래스터", Hud.Instance.Choice.Title);
            Assert.IsNull(session.EquippedRanged, "묻기 전에는 장착하지 않는다");
            StringAssert.Contains("볼트 블래스터를 샀어요", Hud.Instance.LastToast);
            yield return Shot("62_shop_offer_equip");

            yield return PressChoice(1);
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsNotNull(session.EquippedRanged);
            Assert.AreEqual(WeaponCodes.BoltBlaster, session.EquippedRanged.code);
            Assert.AreEqual(EquipSlots.Ranged, session.ActiveSlot, "메뉴에서 누른 숫자 1이 무기까지 근접으로 바꾸면 안 된다");
            yield return null;
            Assert.IsTrue(Hud.Instance.Hotbar.Visible, "원거리 무기가 생기면 빠른 칸이 보인다");
            Assert.IsTrue(Hud.Instance.Hotbar.RangedRowVisible);
            Assert.AreEqual("볼트 블래스터", Hud.Instance.Hotbar.ActiveName);
            Assert.IsNull(Hud.Instance.Hotbar.KitText, "수리 키트가 없으면 3번 칸이 없다");
            StringAssert.DoesNotContain("미소 상점", Hud.Instance.ObjectiveText, "산 뒤에는 상점을 더 권하지 않는다");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Shot("63_city_blaster_in_hand");

            // 다시 들러 수리 키트 두 개. 소모품은 사고 나면 상점이 다시 열려 이어서 살 수 있다.
            yield return TalkToMiso();
            Assert.IsFalse(ChoiceMenu.Instance.Options[0].enabled, "이미 가진 장비는 다시 살 수 없다");
            StringAssert.Contains("구매 완료", ChoiceMenu.Instance.Options[0].label);
            Assert.IsTrue(ChoiceMenu.Instance.Options[1].enabled);
            yield return PressChoice(1);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "고를 수 없는 줄은 눌러도 반응하지 않는다");
            Assert.AreEqual(70, session.Me.gc);
            yield return PressChoice(2);
            Assert.AreEqual(40, session.Me.gc);
            Assert.AreEqual(1, session.CountOf(ItemCodes.RepairKit));
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "소모품을 사면 상점이 다시 열린다");
            Assert.AreEqual(EquipSlots.Ranged, session.ActiveSlot, "상점에서 누른 숫자 2는 무기를 바꾸지 않는다");
            yield return PressChoice(2);
            Assert.AreEqual(10, session.Me.gc);
            Assert.AreEqual(2, session.CountOf(ItemCodes.RepairKit));
            Assert.IsFalse(ChoiceMenu.Instance.Options[1].enabled, "GC가 모자라면 고를 수 없다");
            StringAssert.Contains("모자라요", ChoiceMenu.Instance.Options[1].detail);
            yield return Shot("64_shop_not_enough_gc");
            yield return PressInteract(); // E로 닫기
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            yield return null;
            Assert.AreEqual("수리 키트  × 2", Hud.Instance.Hotbar.KitText);

            // 체력이 가득할 때 3을 눌러도 키트가 줄지 않는다
            Assert.AreEqual(Player.Health.Max, Player.Health.Current);
            yield return new WaitForSecondsRealtime(2.6f); // 같은 안내를 연달아 띄우지 않는 시간
            yield return PressQuickUse();
            Assert.AreEqual(2, session.CountOf(ItemCodes.RepairKit), "체력이 가득하면 쓰지 않는다");
            StringAssert.Contains("체력이 가득", Hud.Instance.LastToast);

            // 1 · 2 키로 무기를 바꾼다. 고른 칸은 서버에 남는다.
            yield return PressWeaponSlot(1);
            Assert.AreEqual(EquipSlots.Melee, session.ActiveSlot);
            yield return null;
            Assert.AreEqual("스파크 배턴", Hud.Instance.Hotbar.ActiveName);
            yield return Shot("65_city_baton_selected");
            yield return PressWeaponSlot(2);
            Assert.AreEqual(EquipSlots.Ranged, session.ActiveSlot);
            Assert.AreEqual(EquipSlots.Ranged, session.Me.GetFlag(FlagKeys.ActiveSlot));

            // 전투 구역 밖, 울타리 남쪽에 서서 한 걸음도 다가가지 않고 쏜다.
            // 허리 높이의 울타리와 낮은 잔해 너머로도 맞아야 한다.
            Player.Teleport(new Vector3(33f, 0f, 15.5f), 0f);
            OrbitCamera.Instance.SnapBehind(0f);
            yield return null;
            yield return WalkTo(ShootingSpot, 0.5f, 10f, false);
            HuntZone zone = FindZone(ZoneCodes.NorthEast);
            Assert.Greater(zone.AliveCount, 0, "전투 구역에 로봇이 있어야 한다");
            Assert.IsFalse(zone.Contains(Player.transform.position), "구역 밖에서 쏜다");
            PlayerCombat combat = Player.GetComponent<PlayerCombat>();
            Vector3 stood = Player.transform.position;

            long gcBefore = session.Me.gc;
            int hitsBefore = BoltProjectile.HitCount;
            var watch = new KillWatch();
            bool shot = false;
            bool sawTarget = false;
            bool sawMark = false;
            float nextLog = Time.time;
            float deadline = Time.time + 120f;
            _input.move = Vector2.zero;
            _input.attackHeld = true;
            while (watch.Kills < 3)
            {
                if (Time.time > deadline)
                    Assert.Fail($"시간 초과: 원거리로 로봇 3대 처치. 처치 {watch.Kills}, 명중 {BoltProjectile.HitCount - hitsBefore}, " +
                                $"체력 {Player.Health.Current}, 위치 {Player.transform.position}");

                // 사람이 하듯 마우스로 화면을 가장 가까운 로봇 쪽으로 돌린다. 겨누는 것은 게임이 돕는다.
                EnemyAgent nearest = Nearest(zone);
                TurnCameraTowards(nearest != null ? nearest.transform.position : ZoneCenter);

                Health target = combat.RangedTarget;
                if (target != null) sawTarget = true;
                if (target != null && combat.AimMarkVisible) sawMark = true;
                if (Time.time >= nextLog)
                {
                    nextLog = Time.time + 5f;
                    Debug.Log("[ShopTest] 사격 중: " + DescribeAim(zone, combat, nearest));
                }
                if (!shot && target != null && BoltProjectile.ActiveCount > 0
                    && PlanarDistance(target.transform.position, Player.transform.position) > 6f)
                {
                    shot = true;
                    yield return Shot("66_city_shooting");
                }
                watch.Step(zone, Player.transform.position);
                yield return null;
            }
            _input.attackHeld = false;
            _input.move = Vector2.zero;
            _input.lookDelta = Vector2.zero;
            Debug.Log($"[ShopTest] 원거리 처치 거리: {watch.Describe()}  명중 {BoltProjectile.HitCount - hitsBefore}  " +
                      $"걸린 시간 {Time.time - (deadline - 120f):0.0}초  체력 {Player.Health.Current}/{Player.Health.Max}");

            Assert.IsTrue(sawTarget, "화면 가운데 가까이의 적을 겨눠야 한다");
            Assert.IsTrue(sawMark, "겨눈 적의 발밑에 조준 표시가 보여야 한다");
            Assert.Greater(BoltProjectile.HitCount, hitsBefore, "탄환이 적에게 맞아야 한다");
            Assert.Greater(watch.FarthestKill, 7f, "멀리 있는 적을 쓰러뜨려야 원거리 무기다");
            Assert.Less(PlanarDistance(Player.transform.position, stood), 1.5f, "제자리에서 쏜 것이어야 한다 (넉백 정도만 허용)");
            yield return Until(() => session.PendingGc == 0, 8f, "사냥 보고 확정");
            Assert.GreaterOrEqual(session.Me.gc, gcBefore + 30, "원거리로 잡아도 보상은 같다");
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");

            // 일부러 맞은 다음 (공격하지 않고 다가간다), 둘레의 적을 정리하고 3으로 수리 키트를 쓴다
            int kits = session.CountOf(ItemCodes.RepairKit);
            int healthBefore = -1;
            deadline = Time.time + 120f;
            while (true)
            {
                if (Time.time > deadline)
                    Assert.Fail($"시간 초과: 다친 뒤 수리 키트 사용. 체력 {Player.Health.Current}/{Player.Health.Max}, 위치 {Player.transform.position}");
                Assert.IsFalse(Player.Health.IsDead, "이 테스트에서 쓰러지면 안 된다");

                EnemyAgent nearest = Nearest(zone);
                float distance = nearest != null ? PlanarDistance(nearest.transform.position, Player.transform.position) : float.MaxValue;
                bool hurt = Player.Health.Current <= Player.Health.Max - 16;
                if (!hurt)
                {
                    // 맞으러 간다
                    _input.attackHeld = false;
                    Vector3 goal = nearest != null ? nearest.transform.position : ZoneCenter;
                    Vector3 to = goal - Player.transform.position;
                    to.y = 0f;
                    if (to.magnitude > 1.2f) SteerTowards(to.normalized);
                    else _input.move = Vector2.zero;
                }
                else if (distance < 7f)
                {
                    // 바짝 붙은 적은 화면 어디에 있든 겨눈다
                    _input.move = Vector2.zero;
                    _input.attackHeld = true;
                }
                else
                {
                    _input.attackHeld = false;
                    _input.move = Vector2.zero;
                    healthBefore = Player.Health.Current;
                    break;
                }
                yield return null;
            }
            int max = Player.Health.Max;
            yield return PressQuickUse();
            Assert.AreEqual(kits - 1, session.CountOf(ItemCodes.RepairKit), "수리 키트가 하나 줄어야 한다");
            Assert.GreaterOrEqual(Player.Health.Current, Mathf.Min(max, healthBefore + 50), "체력이 50 회복되어야 한다");
            yield return null;
            Assert.AreEqual($"수리 키트  × {kits - 1}", Hud.Instance.Hotbar.KitText);
            yield return Shot("67_city_after_repair_kit");
            yield return Until(() => session.PendingGc == 0, 8f, "사냥 보고 확정");

            // 안전한 광장에서 가방을 연다: 장착을 풀었다가 다시 한다
            Player.Teleport(Plaza, 0f);
            OrbitCamera.Instance.SnapBehind(0f);
            yield return null;

            // 겨눌 적이 없으면 화면이 보는 쪽으로 쏜다. 쏘는 자세를 앞 오른쪽에서 한 장 남긴다 (눈으로 확인하는 용도).
            int flying = BoltProjectile.ActiveCount;
            _input.attackHeld = true;
            yield return Until(() => BoltProjectile.ActiveCount > flying, 2f, "적이 없어도 탄환이 나간다");
            Assert.IsNull(combat.RangedTarget);
            // 조준 표시는 그 프레임의 끝(LateUpdate)에 따라온다. 전투 구역에서 겨누던 적의 표시가
            // 순간 이동 직후 한 프레임 남아 있을 수 있으므로 한 프레임 뒤에 본다.
            yield return null;
            Assert.IsFalse(combat.AimMarkVisible);
            yield return new WaitForSeconds(0.3f);
            _input.attackHeld = false;
            Assert.Less(Mathf.Abs(Mathf.DeltaAngle(Player.transform.eulerAngles.y, OrbitCamera.Instance.Yaw)), 20f,
                "쏘는 동안에는 화면이 보는 쪽을 향한다");
            OrbitCamera.Instance.SnapBehind(Player.transform.eulerAngles.y - 115f);
            yield return Shot("67b_blaster_aim_pose");
            OrbitCamera.Instance.SnapBehind(0f);
            yield return new WaitForSeconds(0.6f);

            yield return PressInventory();
            Assert.AreEqual("가방", Hud.Instance.Choice.Title);
            Assert.IsFalse(combat.AimMarkVisible, "메뉴가 열려 있는 동안에는 조준 표시가 없다");
            Assert.AreEqual(3, Hud.Instance.Choice.OptionCount);
            StringAssert.Contains("3 / 30칸", ChoiceMenu.Instance.Body);
            Assert.AreEqual("장착 중", ChoiceMenu.Instance.Options[1].cost);
            Assert.AreEqual($"× {kits - 1}", ChoiceMenu.Instance.Options[2].cost);
            yield return Shot("68_bag");
            yield return PressChoice(2);
            Assert.IsNull(session.EquippedRanged, "가방에서 원거리 무기 장착을 풀 수 있다");
            Assert.AreEqual(EquipSlots.Melee, session.ActiveSlot);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "장착을 바꾼 뒤에도 가방이 열려 있다");
            Assert.AreEqual(ItemActions.BagTag, ChoiceMenu.Instance.Tag);
            Assert.AreEqual("장착하기", ChoiceMenu.Instance.Options[1].cost);
            yield return PressChoice(2);
            Assert.IsNotNull(session.EquippedRanged);
            Assert.AreEqual(EquipSlots.Ranged, session.ActiveSlot);
            yield return PressInventory();
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsFalse(GameState.IsControlLocked);

            // 껐다 켜도 가방·장착·손에 든 무기가 그대로다
            long gc = session.Me.gc;
            GameRoot.Instance.Shutdown();
            yield return BootToRoom();
            GameSession restored = GameSession.Current;
            Assert.AreNotSame(session, restored);
            Assert.AreEqual(gc, restored.Me.gc);
            Assert.IsTrue(restored.Owns(ItemCodes.BoltBlaster));
            Assert.AreEqual(kits - 1, restored.CountOf(ItemCodes.RepairKit));
            Assert.AreEqual(WeaponCodes.BoltBlaster, restored.EquippedRanged.code);
            Assert.AreEqual(EquipSlots.Ranged, restored.ActiveSlot);
            yield return null;
            yield return null;
            Assert.AreEqual("볼트 블래스터", Hud.Instance.Hotbar.ActiveName);
            yield return Shot("69_room_after_restart_with_blaster");
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator RobotDropsARepairKit_ItGoesIntoTheBag_AndTheQuickSlotAppears()
        {
            GameRoot.DefsOverride = DefsWithDropChance(1f);
            yield return BootToCityAfterTutorial();
            GameSession session = GameSession.Current;
            Assert.AreEqual(0, session.CountOf(ItemCodes.RepairKit));
            Assert.IsFalse(Hud.Instance.Hotbar.Visible);

            Player.Teleport(new Vector3(33f, 0f, 16f), 0f);
            OrbitCamera.Instance.SnapBehind(0f);
            yield return null;
            yield return WalkTo(ZoneDoorstep, 0.8f, 15f, true);
            HuntZone zone = FindZone(ZoneCodes.NorthEast);

            // 배턴으로 한 대를 잡는다. 드롭은 서버가 사냥 보고를 받을 때 판정한다.
            bool sawToast = false;
            float deadline = Time.time + 90f;
            _input.attackHeld = true;
            while (session.CountOf(ItemCodes.RepairKit) == 0)
            {
                if (Time.time > deadline)
                    Assert.Fail($"시간 초과: 로봇을 잡아 수리 키트 줍기. GC {session.Me.gc}, 체력 {Player.Health.Current}");
                EnemyAgent nearest = Nearest(zone);
                Vector3 goal = nearest != null ? nearest.transform.position : ZoneCenter;
                Vector3 to = goal - Player.transform.position;
                to.y = 0f;
                if (to.magnitude > 1.3f) SteerTowards(to.normalized);
                else _input.move = Vector2.zero;
                yield return null;
            }
            _input.attackHeld = false;
            _input.move = Vector2.zero;
            for (int i = 0; i < 5 && !sawToast; i++)
            {
                sawToast = Hud.Instance.LastToast != null && Hud.Instance.LastToast.Contains("수리 키트를 주웠어요");
                yield return null;
            }

            Assert.IsTrue(sawToast, "주운 것을 알려야 한다. 마지막 알림: " + Hud.Instance.LastToast);
            Assert.IsTrue(Hud.Instance.Hotbar.Visible, "수리 키트가 생기면 빠른 칸이 보인다");
            Assert.IsFalse(Hud.Instance.Hotbar.RangedRowVisible, "원거리 무기가 없으면 2번 칸은 없다");
            StringAssert.StartsWith("수리 키트  × ", Hud.Instance.Hotbar.KitText);
            yield return Shot("70_city_kit_dropped");
        }

        // ── 도우미 ──────────────────────────────────────────────

        private static GameDefs DefsWithDropChance(float chance)
        {
            GameDefs defs = DefaultDefs.Create();
            defs.FindEnemy(EnemyCodes.MalfunctionRobot).dropChance = chance;
            return defs;
        }

        /// <summary>
        /// 상점에 갈 돈을 마련한다: 모의 서버에 사냥 보고를 보낸다 (로봇 한 대 10 GC, 1분에 24대까지 인정).
        /// 서버 시계를 1분씩 앞으로 감아 "너무 빨리 잡았다"는 검증에 걸리지 않게 한다.
        /// </summary>
        private void Earn(int gc)
        {
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            while (gc > 0)
            {
                int chunk = Math.Min(gc, 240);
                ApiResult<HuntSessionDto> started = null;
                backend.StartHunt(ZoneCodes.NorthEast, r => started = r);
                Assert.IsTrue(started.ok, started.message);
                _clock.offset += 60;
                ApiResult<HuntReportDto> report = null;
                backend.ReportHunt(started.data.sessionId, 1,
                    new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, chunk / 10) }, r => report = r);
                Assert.IsTrue(report.ok, report.message);
                Assert.AreEqual(chunk, report.data.rewardGc, "테스트 준비: 사냥 보상");
                gc -= chunk;
            }
            ApiResult<MeDto> me = null;
            backend.GetMe(r => me = r);
            Assert.IsTrue(me.ok, me.message);
            session.ApplyMe(me.data);
        }

        /// <summary>미소 상점의 오미소에게 걸어가 말을 걸고, 대화를 넘겨 상점 메뉴까지 간다.</summary>
        private IEnumerator TalkToMiso()
        {
            if (PlanarDistance(Player.transform.position, MisoSpot) > 8f)
                Player.Teleport(MisoSpot + new Vector3(-6f, 0f, 0f), 90f);
            yield return null;
            yield return WalkTo(MisoSpot, 1.6f, 15f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("말하기"), 2f, "오미소에게 말하기 안내");
            yield return PressInteract();
            Assert.IsTrue(Hud.Instance.DialogueVisible, "대화가 시작되어야 한다");
            yield return AdvanceDialogue();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "상점 메뉴");
        }

        private static EnemyAgent Nearest(HuntZone zone)
        {
            EnemyAgent best = null;
            float bestDistance = float.MaxValue;
            foreach (EnemyAgent enemy in zone.Alive)
            {
                if (enemy == null || enemy.Health.IsDead) continue;
                float distance = PlanarDistance(enemy.transform.position, Player.transform.position);
                if (distance < bestDistance) { bestDistance = distance; best = enemy; }
            }
            return best;
        }

        /// <summary>사격이 잘 안 될 때 까닭을 알 수 있게 지금의 조준 상황을 적는다.</summary>
        private static string DescribeAim(HuntZone zone, PlayerCombat combat, EnemyAgent nearest)
        {
            Vector3 shoulder = Player.transform.position;
            shoulder.y = Player.Character.ShoulderR.position.y;
            var parts = new List<string>
            {
                $"어깨 높이 {shoulder.y - Player.transform.position.y:0.00}",
                $"겨눈 적 {(combat.RangedTarget != null ? "있음" : "없음")}",
                $"명중 {BoltProjectile.HitCount}",
                $"화면 방향 {OrbitCamera.Instance.Yaw:0}",
            };
            foreach (EnemyAgent enemy in zone.Alive)
            {
                if (enemy == null || enemy.Health.IsDead) continue;
                Vector3 aim = BoltProjectile.AimPointOf(enemy.Health);
                bool blocked = Physics.Linecast(shoulder, aim, out RaycastHit hit, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                Vector3 to = enemy.transform.position - Player.transform.position;
                float bearing = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
                parts.Add($"[{(enemy == nearest ? "*" : "")}{PlanarDistance(enemy.transform.position, Player.transform.position):0.0}m " +
                          $"방향 {bearing:0} 높이 {aim.y:0.00} {(blocked ? "막힘:" + hit.collider.name + "@" + hit.point.y.ToString("0.00") : "트임")} " +
                          $"체력 {enemy.Health.Current}]");
            }
            return string.Join("  ", parts);
        }

        private static HuntZone FindZone(string code)
        {
            foreach (HuntZone zone in HuntZone.All)
                if (zone.Def.code == code) return zone;
            Assert.Fail("사냥 구역을 찾을 수 없다: " + code);
            return null;
        }

        /// <summary>적이 쓰러질 때 플레이어와 얼마나 떨어져 있었는지 적어 둔다.</summary>
        private sealed class KillWatch
        {
            private readonly Dictionary<EnemyAgent, float> _lastDistance = new Dictionary<EnemyAgent, float>();
            private readonly List<EnemyAgent> _gone = new List<EnemyAgent>();
            private readonly List<float> _killDistances = new List<float>();

            public int Kills => _killDistances.Count;
            public float FarthestKill { get; private set; }

            public void Step(HuntZone zone, Vector3 playerPosition)
            {
                _gone.Clear();
                foreach (KeyValuePair<EnemyAgent, float> pair in _lastDistance)
                    if (pair.Key == null || pair.Key.Health.IsDead) _gone.Add(pair.Key);
                foreach (EnemyAgent enemy in _gone)
                {
                    float distance = _lastDistance[enemy];
                    _lastDistance.Remove(enemy);
                    _killDistances.Add(distance);
                    FarthestKill = Mathf.Max(FarthestKill, distance);
                }
                foreach (EnemyAgent enemy in zone.Alive)
                {
                    if (enemy == null || enemy.Health.IsDead) continue;
                    _lastDistance[enemy] = PlanarDistance(enemy.transform.position, playerPosition);
                }
            }

            public string Describe()
            {
                var parts = new List<string>();
                foreach (float distance in _killDistances) parts.Add(distance.ToString("0.0") + "m");
                return string.Join(", ", parts);
            }
        }
    }
}
