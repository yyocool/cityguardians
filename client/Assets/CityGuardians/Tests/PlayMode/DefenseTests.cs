using System;
using System.Collections;
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
    /// 방어전을 처음부터 끝까지 해 본다 (docs/TOWER_DEFENSE.md): 출동 → 타워 건설 → 웨이브 → 승리 보상 → 타워 해금.
    /// 사람이 하듯 걸어가서 E와 숫자 키를 누른다. 전투 시간이 길어 게임 시간을 빠르게 돌리며,
    /// 모의 서버의 시계도 게임 시간을 따라가게 해서 "너무 빨리 끝났다"는 검증에 걸리지 않게 한다.
    /// </summary>
    public sealed class DefenseTests : PlayTestBase
    {
        private const float FastForward = 3f;
        private static readonly Vector3 HomePoint = new Vector3(0f, 0f, -14f);

        /// <summary>게임 시간과 함께 흐르는 서버 시계.</summary>
        private sealed class GameTimeClock : IClock
        {
            private readonly DateTime _start = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);
            public DateTime UtcNow => _start.AddSeconds(Time.timeAsDouble);
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator Defense_BuildTowers_HoldFiveWaves_WinReward_ThenUnlockBlastTower()
        {
            GameRoot.ClockOverride = new GameTimeClock();
            yield return BootToCityAfterTutorial();
            GameSession session = GameSession.Current;
            var backend = (LocalMockBackend)session.Backend;
            Assert.AreEqual(0, session.Me.gc);
            Assert.IsTrue(session.IsTowerUnlocked(TowerCodes.MachineGun), "기관총 타워는 처음부터 쓸 수 있다");
            Assert.IsFalse(session.IsTowerUnlocked(TowerCodes.Blast), "폭발 타워는 해금해야 쓸 수 있다");

            // 출동 게이트: 메뉴에서 출동을 고른다. GC가 없으니 해금은 고를 수 없다.
            yield return OpenGateMenu();
            Assert.AreEqual(2, Hud.Instance.Choice.OptionCount);
            Assert.IsTrue(ChoiceMenu.Instance.Options[0].enabled);
            Assert.IsFalse(ChoiceMenu.Instance.Options[1].enabled, "GC가 모자라면 해금을 고를 수 없다");
            yield return Shot("30_city_defense_menu");
            yield return PressChoice(2);
            Assert.IsTrue(ChoiceMenu.Instance.IsOpen, "고를 수 없는 줄은 눌러도 반응하지 않는다");
            yield return PressChoice(1);
            yield return UntilInDefense();

            DefenseBattle battle = DefenseBattle.Current;
            DefenseBuilder field = UnityEngine.Object.FindFirstObjectByType<DefenseBuilder>();
            StageDef stage = battle.Stage;
            Assert.AreEqual(stage.startEnergy, battle.Energy);
            Assert.AreEqual(stage.baseHp, battle.BaseHp);
            Assert.AreEqual(0, battle.WaveNumber, "첫 웨이브는 플레이어가 시작한다");
            yield return null;
            Assert.IsTrue(Hud.Instance.Defense.StatusVisible);
            StringAssert.Contains("타워", Hud.Instance.ObjectiveText);
            yield return Shot("31_defense_prep");

            // 준비: 타워 두 개. 잠긴 폭발 타워는 고를 수 없다.
            yield return WalkToSlot(field.Slots[DefenseBuilder.FirstSlotIndex]);
            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "타워 선택 메뉴");
            Assert.IsFalse(ChoiceMenu.Instance.Options[1].enabled, "잠긴 타워는 고를 수 없다");
            yield return Shot("32_defense_build_menu");
            yield return PressChoice(1);
            Assert.IsNotNull(field.Slots[DefenseBuilder.FirstSlotIndex].Tower, "타워가 세워져야 한다");
            Assert.AreEqual(stage.startEnergy - 100, battle.Energy);
            StringAssert.Contains("신호기", Hud.Instance.ObjectiveText);

            yield return BuildAt(field.Slots[4], 1);
            Assert.AreEqual(2, battle.TowersBuilt);
            Assert.AreEqual(0, battle.Energy);

            // 에너지가 없으면 더 세울 수 없다
            yield return WalkToSlot(field.Slots[3]);
            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "타워 선택 메뉴");
            Assert.IsFalse(ChoiceMenu.Instance.Options[0].enabled, "에너지가 모자라면 고를 수 없다");
            yield return PressInteract(); // E로 닫기
            Assert.IsFalse(ChoiceMenu.Instance.IsOpen);
            Assert.IsNull(field.Slots[3].Tower);

            // 신호기에서 첫 웨이브 시작
            yield return WalkTo(DefenseBuilder.SignalPoint, 1.9f, 20f, true);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("웨이브 시작"), 2f, "신호기 안내");
            yield return PressInteract();
            Assert.AreEqual(DefensePhase.Wave, battle.Phase);
            Assert.AreEqual(1, battle.WaveNumber);

            // 웨이브: 방어선 앞에서 싸우고, 쉬는 시간마다 번 에너지로 타워를 더 세운다
            Time.timeScale = FastForward;
            int[] buildOrder = { 3, 1, 0, 2 };
            int nextBuild = 0;
            bool waveShot = false;
            bool mechaShot = false;
            float started = Time.time;
            while (!battle.IsOver)
            {
                if (Time.time - started > 900f) Assert.Fail($"방어전이 끝나지 않는다. {Describe(battle)}");
                Assert.AreNotEqual(DefensePhase.ReportFailed, battle.Phase, "결과 보고가 실패하면 안 된다");

                if (battle.Phase == DefensePhase.Rest && nextBuild < buildOrder.Length && battle.Energy >= 100)
                {
                    Debug.Log($"[DefenseTest] 쉬는 시간: {Describe(battle)}");
                    yield return BuildAt(field.Slots[buildOrder[nextBuild]], 1);
                    nextBuild++;
                    continue;
                }
                if (battle.Phase == DefensePhase.Rest && battle.Energy >= 60 && field.Slots[DefenseBuilder.FirstSlotIndex].Tower != null
                    && field.Slots[DefenseBuilder.FirstSlotIndex].Tower.CanUpgrade && nextBuild >= buildOrder.Length)
                {
                    yield return UpgradeAt(field.Slots[DefenseBuilder.FirstSlotIndex]);
                    continue;
                }

                EnemyAgent nearest = DefendStep(battle);
                if (!waveShot && battle.WaveNumber == 2 && nearest != null && PlanarDistance(nearest.transform.position, Player.transform.position) < 4f)
                {
                    waveShot = true;
                    yield return Shot("33_defense_wave");
                }
                if (!mechaShot && MechaWithin(battle, 8f))
                {
                    mechaShot = true;
                    yield return Shot("35_defense_mecha");
                }
                yield return null;
            }
            Time.timeScale = 1f;
            _input.attackHeld = false;
            _input.move = Vector2.zero;
            Debug.Log($"[DefenseTest] 끝: {Describe(battle)} 걸린 게임 시간 {Time.time - started:0}초");

            // 승리: 보상은 서버가 계산한 값
            Assert.AreEqual(DefensePhase.Won, battle.Phase, $"이 정도로 준비했으면 이겨야 한다. {Describe(battle)}");
            BattleResultDto result = battle.Result;
            Assert.IsTrue(result.accepted, "서버가 결과를 인정해야 한다: " + result.rejectReason);
            Assert.IsTrue(result.firstWin);
            Assert.AreEqual(stage.winGc + stage.firstWinBonusGc, result.rewardGc);
            Assert.AreEqual(stage.winXp, result.rewardXp);
            Assert.AreEqual(stage.winGc + stage.firstWinBonusGc, session.Me.gc);
            Assert.IsTrue(session.HasCleared(stage.code));
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            yield return null;
            Assert.IsTrue(Hud.Instance.Defense.ResultVisible);
            Assert.AreEqual("방어 성공!", Hud.Instance.Defense.ResultTitle);
            Assert.IsTrue(GameState.IsControlLocked, "결과 화면에서는 조작이 잠긴다");
            yield return Shot("36_defense_victory");

            // E로 도시에 돌아오면 출동 게이트 앞이다
            yield return new WaitForSecondsRealtime(1.2f);
            yield return PressInteract();
            yield return UntilInCity();
            Assert.Less(PlanarDistance(Player.transform.position, CityBuilder.DefenseGatePoint), 8f, "출동 게이트 앞에서 돌아와야 한다");
            Assert.IsFalse(GameState.IsControlLocked);
            yield return null;
            // 첫 승리 뒤에는 전입신고를 권한다 (이방인이고, 아직 "나중에"라고 답하지 않았을 때).
            StringAssert.Contains("전입신고", Hud.Instance.ObjectiveText);
            Assert.AreEqual(0, result.defensePoints, "이방인에게는 방어 포인트가 쌓일 도시가 없다");
            Assert.Greater(OrbitCamera.Instance.CurrentDistance, 5f, "돌아온 자리에서 카메라가 벽이나 게이트에 막히면 안 된다");
            yield return new WaitForSecondsRealtime(0.5f); // 화면이 밝아진 뒤에 찍는다
            yield return Shot("37_city_after_defense");

            // 받은 보상으로 폭발 타워를 해금한다
            yield return OpenGateMenu();
            Assert.IsTrue(ChoiceMenu.Instance.Options[1].enabled, "GC가 충분하면 해금을 고를 수 있다");
            yield return PressChoice(2);
            Assert.IsTrue(session.IsTowerUnlocked(TowerCodes.Blast));
            Assert.AreEqual(0, session.Me.gc, "해금 값 500 GC가 빠져야 한다");
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");

            // 다시 출동해서 폭발 타워를 세우고 쏘는 것을 본 뒤 후퇴한다
            yield return OpenGateMenu();
            Assert.IsFalse(ChoiceMenu.Instance.Options[1].enabled, "이미 해금한 타워는 다시 살 수 없다");
            yield return Shot("38_city_defense_menu_unlocked");
            yield return PressChoice(1);
            yield return UntilInDefense();
            battle = DefenseBattle.Current;
            field = UnityEngine.Object.FindFirstObjectByType<DefenseBuilder>();
            yield return BuildAt(field.Slots[DefenseBuilder.FirstSlotIndex], 2);
            DefenseTower blast = field.Slots[DefenseBuilder.FirstSlotIndex].Tower;
            Assert.AreEqual(TowerCodes.Blast, blast.Def.code);
            Assert.AreEqual(stage.startEnergy - 175, battle.Energy);

            yield return WalkTo(DefenseBuilder.SignalPoint, 1.9f, 20f, true);
            yield return PressInteract();
            Assert.AreEqual(DefensePhase.Wave, battle.Phase);
            Time.timeScale = FastForward;
            yield return Until(() => blast.ShotsFired >= 2 && battle.TotalKills >= 1, 90f, "폭발 타워가 쏘고 적이 쓰러진다");
            Time.timeScale = 1f;
            yield return Shot("39_defense_blast_tower");

            long gcBefore = session.Me.gc;
            yield return WalkTo(DefenseBuilder.RetreatPoint, 1.6f, 20f, true);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("돌아가기"), 2f, "후퇴 안내");
            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "후퇴 확인");
            yield return PressChoice(1);
            yield return Until(() => battle.IsOver, 3f, "후퇴 결과");
            Assert.AreEqual(DefensePhase.Lost, battle.Phase);
            Assert.IsTrue(battle.Result.accepted);
            // 물러나면 막아낸 웨이브만큼의 참가 보상만 받는다 (하나도 못 막았으면 0).
            int consolation = stage.loseGcPerWave * battle.WavesCleared;
            Assert.AreEqual(consolation, battle.Result.rewardGc);
            Assert.AreEqual(gcBefore + consolation, session.Me.gc);
            yield return null;
            Assert.AreEqual("후퇴했어요", Hud.Instance.Defense.ResultTitle);
            yield return Shot("40_defense_retreat");
            yield return new WaitForSecondsRealtime(1.2f);
            yield return PressInteract();
            yield return UntilInCity();
            Assert.IsNull(backend.FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");

            // 아직 이방인이므로 여전히 전입신고를 권한다. 전입신고를 하면 축하금 500 GC가 생기고,
            // 볼트 블래스터를 살 돈이 되므로 이번에는 상점을 권한다.
            // (지도에서 도시를 고르는 화면 흐름은 MapTests가 확인한다. 여기서는 등록한 뒤의 도시만 본다.)
            yield return null;
            StringAssert.Contains("전입신고", Hud.Instance.ObjectiveText);
            ApiResult<ResidencyResultDto> registered = null;
            backend.RegisterResidency(RegionCodes.Haeundae, "푸른고래", r => registered = r);
            Assert.IsTrue(registered.ok, registered.message);
            session.ApplyResidency(registered.data);
            yield return null;
            yield return null;
            Assert.GreaterOrEqual(session.Me.gc, session.Defs.FindItem(ItemCodes.BoltBlaster).priceGc);
            Assert.AreEqual("미소 상점에서 볼트 블래스터를 살 수 있어요", Hud.Instance.ObjectiveText);
            Assert.AreEqual("부산 해운대구 가디언", Hud.Instance.CityText);
        }

        [UnityTest]
        [Timeout(180000)]
        public IEnumerator Defense_LeavingBeforeTheFirstWave_IsNotADefeat()
        {
            GameRoot.ClockOverride = new GameTimeClock();
            yield return BootToCityAfterTutorial();
            GameSession session = GameSession.Current;

            yield return OpenGateMenu();
            yield return PressChoice(1);
            yield return UntilInDefense();
            DefenseBattle battle = DefenseBattle.Current;

            yield return WalkTo(DefenseBuilder.RetreatPoint, 1.6f, 20f, true);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("돌아가기"), 2f, "돌아가기 안내");
            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "돌아가기 확인");

            // "계속 싸우기"를 고르면 그대로 남는다
            yield return PressChoice(2);
            Assert.IsTrue(battle.CanBuild);
            Assert.AreEqual(SceneNames.Defense, SceneManager.GetActiveScene().name);

            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "돌아가기 확인");
            yield return PressChoice(1);
            Assert.IsFalse(Hud.Instance.Defense.ResultVisible, "웨이브를 시작하기 전에 돌아가면 결과 화면이 없다");
            yield return UntilInCity();
            Assert.AreEqual(0, session.Me.gc);
            Assert.IsFalse(session.HasCleared(StageCodes.Outskirts1));
            Assert.IsFalse(GameState.IsControlLocked);
        }

        /// <summary>
        /// 플레이어의 행동이 승패를 가르는지 확인한다: 타워 두 개만 세우고 가만히 있으면 방어선이 뚫린다.
        /// 실제로 져서 패배 화면과 참가 보상까지 가는 길도 함께 지난다.
        /// </summary>
        [UnityTest]
        [Timeout(600000)]
        public IEnumerator Defense_TwoTowersAndStandingStill_LosesTheLine()
        {
            GameRoot.ClockOverride = new GameTimeClock();
            yield return BootToCityAfterTutorial();
            GameSession session = GameSession.Current;

            yield return OpenGateMenu();
            yield return PressChoice(1);
            yield return UntilInDefense();
            DefenseBattle battle = DefenseBattle.Current;
            DefenseBuilder field = UnityEngine.Object.FindFirstObjectByType<DefenseBuilder>();
            StageDef stage = battle.Stage;

            yield return BuildAt(field.Slots[DefenseBuilder.FirstSlotIndex], 1);
            yield return BuildAt(field.Slots[4], 1);
            yield return WalkTo(DefenseBuilder.SignalPoint, 1.9f, 20f, true);
            yield return PressInteract();
            Assert.AreEqual(DefensePhase.Wave, battle.Phase);

            // 그 뒤로는 아무것도 하지 않는다. 쉬는 시간이 지나면 다음 웨이브가 저절로 온다.
            Time.timeScale = 4f;
            float started = Time.time;
            int loggedWave = 0;
            bool hitShot = false;
            while (!battle.IsOver)
            {
                if (Time.time - started > 900f) Assert.Fail($"방어전이 끝나지 않는다. {Describe(battle)}");
                if (battle.WavesCleared > loggedWave)
                {
                    loggedWave = battle.WavesCleared;
                    Debug.Log($"[DefenseTest] 가만히 있기: {Describe(battle)}");
                }
                if (!hitShot && battle.BaseHp <= stage.baseHp / 2)
                {
                    hitShot = true;
                    yield return Shot("41_defense_line_breaking");
                }
                yield return null;
            }
            Time.timeScale = 1f;
            Debug.Log($"[DefenseTest] 가만히 있기 끝: {Describe(battle)} 걸린 게임 시간 {Time.time - started:0}초");

            Assert.AreEqual(DefensePhase.Lost, battle.Phase, $"아무것도 하지 않아도 이기면 플레이어가 할 일이 없다. {Describe(battle)}");
            Assert.AreEqual(0, battle.BaseHp);
            Assert.GreaterOrEqual(battle.WavesCleared, 1, "타워 두 개와 방어대원만으로도 첫 웨이브는 막아야 한다 (처음부터 무너지면 배울 틈이 없다)");
            Assert.IsTrue(battle.Result.accepted, "서버가 결과를 인정해야 한다: " + battle.Result.rejectReason);
            Assert.AreEqual(stage.loseGcPerWave * battle.WavesCleared, battle.Result.rewardGc, "막아낸 웨이브만큼 참가 보상을 받는다");
            Assert.AreEqual(battle.Result.rewardGc, session.Me.gc);
            Assert.IsFalse(session.HasCleared(stage.code));
            Assert.IsNull(((LocalMockBackend)session.Backend).FindLedgerInconsistency(), "잔액과 원장이 맞아야 한다");
            yield return null;
            Assert.AreEqual("방어선이 뚫렸어요", Hud.Instance.Defense.ResultTitle);
            yield return Shot("42_defense_defeat");

            yield return new WaitForSecondsRealtime(1.2f);
            yield return PressInteract();
            yield return UntilInCity();
            Assert.IsFalse(GameState.IsControlLocked);
        }

        // ── 도우미 ──────────────────────────────────────────────

        private IEnumerator UntilInDefense()
        {
            yield return Until(() => SceneManager.GetActiveScene().name == SceneNames.Defense && Player != null
                                     && !GameRoot.Instance.IsBusy && DefenseBattle.Current != null
                                     && DefenseBattle.Current.Phase == DefensePhase.Prep,
                40f, "방어전 장면으로 이동");
            yield return null;
        }

        /// <summary>출동 게이트로 걸어가 메뉴를 연다. 먼 길은 건너뛰고 게이트 앞 광장 쪽에서부터 걷는다.</summary>
        private IEnumerator OpenGateMenu()
        {
            if (PlanarDistance(Player.transform.position, CityBuilder.DefenseGatePoint) > 8f)
                Player.Teleport(CityBuilder.DefenseGatePoint + new Vector3(6f, 0f, 0f), -90f);
            yield return null;
            yield return WalkTo(CityBuilder.DefenseGatePoint, 1.2f, 15f, false);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("방어기지"), 2f, "출동 게이트 안내");
            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 2f, "방어기지 메뉴");
            Assert.AreEqual("방어기지", Hud.Instance.Choice.Title);
            Assert.IsTrue(GameState.IsControlLocked, "메뉴가 열려 있는 동안에는 조작이 잠긴다");
        }

        private IEnumerator WalkToSlot(BuildSlot slot)
        {
            yield return WalkTo(slot.transform.position, 2.1f, 40f, true);
            yield return Until(() => Hud.Instance.PromptText != null && Hud.Instance.PromptText.Contains("타워"), 3f,
                $"타워 자리 {slot.Index} 안내");
        }

        private IEnumerator BuildAt(BuildSlot slot, int choice)
        {
            _input.attackHeld = false;
            yield return WalkToSlot(slot);
            yield return PressInteract();
            yield return Until(() => Hud.Instance.Choice.Visible, 3f, "타워 선택 메뉴");
            yield return PressChoice(choice);
            Assert.IsNotNull(slot.Tower, $"자리 {slot.Index} 에 타워가 세워져야 한다");
        }

        private IEnumerator UpgradeAt(BuildSlot slot)
        {
            _input.attackHeld = false;
            int level = slot.Tower.Level;
            yield return WalkToSlot(slot);
            StringAssert.Contains("강화", Hud.Instance.PromptText);
            yield return PressInteract();
            Assert.AreEqual(level + 1, slot.Tower.Level, "타워가 강화되어야 한다");
        }

        /// <summary>방어선 앞을 지키는 한 프레임: 가까운 적에게 다가가 때리고, 없으면 제자리로 돌아온다.</summary>
        private EnemyAgent DefendStep(DefenseBattle battle)
        {
            _input.attackHeld = true;
            _input.run = true;
            EnemyAgent nearest = null;
            float best = float.MaxValue;
            foreach (EnemyAgent enemy in battle.Alive)
            {
                if (enemy == null || enemy.IsGone) continue;
                if (PlanarDistance(enemy.transform.position, HomePoint) > 15f) continue;
                float distance = PlanarDistance(enemy.transform.position, Player.transform.position);
                if (distance < best) { best = distance; nearest = enemy; }
            }

            Vector3 goal = nearest != null ? nearest.transform.position : HomePoint;
            float stop = nearest != null ? 1.2f + nearest.Radius : 1.5f;
            Vector3 to = goal - Player.transform.position;
            to.y = 0f;
            if (to.magnitude > stop) SteerTowards(to.normalized);
            else _input.move = Vector2.zero;
            return nearest;
        }

        private static bool MechaWithin(DefenseBattle battle, float distance)
        {
            foreach (EnemyAgent enemy in battle.Alive)
            {
                if (enemy == null || enemy.IsGone || enemy.Def.code != EnemyCodes.ConstructionMecha) continue;
                if (PlanarDistance(enemy.transform.position, Player.transform.position) < distance) return true;
            }
            return false;
        }

        private static string Describe(DefenseBattle battle)
        {
            return $"단계 {battle.Phase}, 웨이브 {battle.WaveNumber}, 막아낸 웨이브 {battle.WavesCleared}, " +
                   $"방어 체력 {battle.BaseHp}, 에너지 {battle.Energy}, 처치 {battle.TotalKills}, 타워 {battle.TowersBuilt}, " +
                   $"살아 있는 적 {battle.AliveCount}, 플레이어 체력 {(Player != null ? Player.Health.Current : -1)}";
        }
    }
}
