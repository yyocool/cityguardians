using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    /// <summary>
    /// 방어전 결과 검증 규칙 (docs/TOWER_DEFENSE.md). 클라이언트가 보낸 결과가 스테이지 정의와 맞을 때만 보상을 준다.
    /// </summary>
    public sealed class BattleRulesTests
    {
        private StageDef _stage;

        [SetUp]
        public void SetUp()
        {
            _stage = DefaultDefs.Create().FindStage(StageCodes.Outskirts1);
            Assert.IsNotNull(_stage);
        }

        /// <summary>스테이지에 나오는 적을 모두 쓰러뜨린 처치 목록.</summary>
        internal static List<KillCount> AllKills(StageDef stage)
        {
            var kills = new List<KillCount>();
            var seen = new HashSet<string>();
            foreach (WaveDef wave in stage.waves)
                foreach (WaveGroupDef group in wave.groups)
                    if (seen.Add(group.enemyCode)) kills.Add(new KillCount(group.enemyCode, stage.CountOf(group.enemyCode)));
            return kills;
        }

        internal static BattleReport WinReport(StageDef stage)
        {
            return new BattleReport
            {
                result = BattleResults.Win, kills = AllKills(stage),
                baseHpLeft = stage.baseHp, wavesCleared = stage.waves.Count,
            };
        }

        private float EnoughTime => _stage.waves.Count * _stage.minSecondsPerWave + 1f;

        [Test]
        public void StageDefinition_IsWhatTheDocsSay()
        {
            Assert.AreEqual(5, _stage.waves.Count);
            Assert.AreEqual(6, _stage.waves[0].Total, "첫 웨이브는 로봇 6대");
            Assert.AreEqual(1, _stage.CountOf(EnemyCodes.ConstructionMecha), "마지막 웨이브에 건설 메카 한 대");
            Assert.AreEqual(_stage.TotalEnemies, _stage.EnemiesInFirstWaves(_stage.waves.Count));
            Assert.AreEqual(0, _stage.EnemiesInFirstWaves(0));
            Assert.AreEqual(0, _stage.CountOf(EnemyCodes.TutorialRobot));
        }

        [Test]
        public void FirstWin_PaysTheBonus_LaterWinsDoNot()
        {
            BattleVerdict first = BattleRules.Evaluate(_stage, WinReport(_stage), EnoughTime, false);
            Assert.IsTrue(first.accepted, first.rejectReason);
            Assert.IsTrue(first.firstWin);
            Assert.AreEqual(_stage.winGc + _stage.firstWinBonusGc, first.rewardGc);
            Assert.AreEqual(_stage.winXp, first.rewardXp);

            BattleVerdict again = BattleRules.Evaluate(_stage, WinReport(_stage), EnoughTime, true);
            Assert.IsTrue(again.accepted);
            Assert.IsFalse(again.firstWin);
            Assert.AreEqual(_stage.winGc, again.rewardGc);
        }

        [Test]
        public void RewardNeverComesFromTheClient()
        {
            // 보고서에는 금액을 적을 칸이 없다. 같은 보고는 언제나 같은 보상이다.
            BattleVerdict a = BattleRules.Evaluate(_stage, WinReport(_stage), EnoughTime, false);
            BattleVerdict b = BattleRules.Evaluate(_stage, WinReport(_stage), EnoughTime * 100f, false);
            Assert.AreEqual(a.rewardGc, b.rewardGc);
        }

        [Test]
        public void Win_WithLeaks_NeedsMatchingBaseDamage()
        {
            BattleReport report = WinReport(_stage);
            report.kills[0] = new KillCount(report.kills[0].enemyCode, report.kills[0].count - 3);

            // 세 마리를 놓쳤는데 방어 체력이 그대로일 수는 없다
            BattleVerdict untouched = BattleRules.Evaluate(_stage, report, EnoughTime, false);
            Assert.IsFalse(untouched.accepted);
            Assert.AreEqual(BattleRejectReasons.Inconsistent, untouched.rejectReason);
            Assert.AreEqual(0, untouched.rewardGc);

            report.baseHpLeft = _stage.baseHp - 3;
            Assert.IsTrue(BattleRules.Evaluate(_stage, report, EnoughTime, false).accepted);
        }

        [Test]
        public void Win_FinishedTooFast_IsRefused()
        {
            BattleVerdict verdict = BattleRules.Evaluate(_stage, WinReport(_stage), 5f, false);
            Assert.IsFalse(verdict.accepted);
            Assert.AreEqual(BattleRejectReasons.TooFast, verdict.rejectReason);
            Assert.AreEqual(0, verdict.rewardGc);
            Assert.AreEqual(0, verdict.rewardXp);
        }

        [Test]
        public void Win_WithoutAllWaves_IsRefused()
        {
            BattleReport report = WinReport(_stage);
            report.wavesCleared = _stage.waves.Count - 1;
            Assert.AreEqual(BattleRejectReasons.InvalidWaves, BattleRules.Evaluate(_stage, report, EnoughTime, false).rejectReason);
        }

        [Test]
        public void Win_WithNoBaseHpLeft_IsRefused()
        {
            BattleReport report = WinReport(_stage);
            report.baseHpLeft = 0;
            Assert.AreEqual(BattleRejectReasons.InvalidBaseHp, BattleRules.Evaluate(_stage, report, EnoughTime, false).rejectReason);
            report.baseHpLeft = _stage.baseHp + 1;
            Assert.AreEqual(BattleRejectReasons.InvalidBaseHp, BattleRules.Evaluate(_stage, report, EnoughTime, false).rejectReason);
        }

        [Test]
        public void Kills_MustBeEnemiesOfTheStage_AndWithinTheirNumbers()
        {
            BattleReport stranger = WinReport(_stage);
            stranger.kills.Add(new KillCount(EnemyCodes.TutorialRobot, 1));
            Assert.AreEqual(BattleRejectReasons.UnknownEnemy, BattleRules.Evaluate(_stage, stranger, EnoughTime, false).rejectReason);

            BattleReport tooMany = WinReport(_stage);
            tooMany.kills[0] = new KillCount(tooMany.kills[0].enemyCode, tooMany.kills[0].count + 1);
            Assert.AreEqual(BattleRejectReasons.TooManyKills, BattleRules.Evaluate(_stage, tooMany, EnoughTime, false).rejectReason);

            BattleReport negative = WinReport(_stage);
            negative.kills[0] = new KillCount(negative.kills[0].enemyCode, -1);
            Assert.AreEqual(BattleRejectReasons.InvalidCount, BattleRules.Evaluate(_stage, negative, EnoughTime, false).rejectReason);

            // 같은 적을 두 줄로 나눠 보내 한도를 피하려는 경우
            BattleReport split = WinReport(_stage);
            split.kills.Add(new KillCount(split.kills[0].enemyCode, 1));
            Assert.AreEqual(BattleRejectReasons.InvalidCount, BattleRules.Evaluate(_stage, split, EnoughTime, false).rejectReason);
        }

        [Test]
        public void UnknownResult_OrMissingReport_IsRefused()
        {
            BattleReport report = WinReport(_stage);
            report.result = "draw";
            Assert.AreEqual(BattleRejectReasons.InvalidResult, BattleRules.Evaluate(_stage, report, EnoughTime, false).rejectReason);
            Assert.AreEqual(BattleRejectReasons.InvalidResult, BattleRules.Evaluate(_stage, null, EnoughTime, false).rejectReason);

            report = WinReport(_stage);
            report.wavesCleared = _stage.waves.Count + 1;
            Assert.AreEqual(BattleRejectReasons.InvalidWaves, BattleRules.Evaluate(_stage, report, EnoughTime, false).rejectReason);
        }

        [Test]
        public void Defeat_PaysASmallAmountPerWaveHeld_AndNoXp()
        {
            int held = 2;
            var report = new BattleReport
            {
                result = BattleResults.Lose, wavesCleared = held, baseHpLeft = 0,
                kills = new List<KillCount> { new KillCount(EnemyCodes.MalfunctionRobot, 14), new KillCount(EnemyCodes.MechInsect, 10) },
            };
            BattleVerdict verdict = BattleRules.Evaluate(_stage, report, held * _stage.minSecondsPerWave + 1f, true);
            Assert.IsTrue(verdict.accepted, verdict.rejectReason);
            Assert.AreEqual(_stage.loseGcPerWave * held, verdict.rewardGc);
            Assert.AreEqual(0, verdict.rewardXp);
            Assert.IsFalse(verdict.firstWin);
            Assert.Less(verdict.rewardGc, _stage.winGc, "지는 쪽이 이기는 쪽보다 이득이면 안 된다");
        }

        [Test]
        public void Retreat_BeforeAnyWave_IsAcceptedWithNoReward()
        {
            var report = new BattleReport { result = BattleResults.Lose, wavesCleared = 0, baseHpLeft = _stage.baseHp };
            BattleVerdict verdict = BattleRules.Evaluate(_stage, report, 0f, false);
            Assert.IsTrue(verdict.accepted, verdict.rejectReason);
            Assert.AreEqual(0, verdict.rewardGc);
        }

        [Test]
        public void Defeat_ClaimingWavesWithoutTheKills_IsRefused()
        {
            // 네 웨이브를 막았다면서 아무도 쓰러뜨리지 않았다: 그 적들이 모두 방어선에 닿았다면 방어 체력으로 감당할 수 없다
            var report = new BattleReport { result = BattleResults.Lose, wavesCleared = 4, baseHpLeft = 0 };
            BattleVerdict verdict = BattleRules.Evaluate(_stage, report, 1000f, false);
            Assert.IsFalse(verdict.accepted);
            Assert.AreEqual(BattleRejectReasons.Inconsistent, verdict.rejectReason);
            Assert.AreEqual(0, verdict.rewardGc);

            // 모든 웨이브를 막았다면 패배일 수 없다
            report = new BattleReport { result = BattleResults.Lose, wavesCleared = _stage.waves.Count, baseHpLeft = 0, kills = AllKills(_stage) };
            Assert.AreEqual(BattleRejectReasons.InvalidWaves, BattleRules.Evaluate(_stage, report, 1000f, false).rejectReason);
        }

        [Test]
        public void TowerUpgrade_MakesItStrongerAndReachFarther()
        {
            TowerDef tower = DefaultDefs.Create().FindTower(TowerCodes.MachineGun);
            // 단계 0 = 강화하지 않은 타워
            Assert.AreEqual(tower.damage, TowerMath.Damage(tower, 0));
            Assert.Greater(TowerMath.Damage(tower, 1), TowerMath.Damage(tower, 0));
            Assert.Greater(TowerMath.Damage(tower, 3), TowerMath.Damage(tower, 2));
            Assert.AreEqual(tower.range, TowerMath.Range(tower, 0), 0.001f);
            Assert.Greater(TowerMath.Range(tower, tower.maxUpgradeLevel), TowerMath.Range(tower, 0));
        }
    }

    /// <summary>모의 서버의 방어전·타워 해금 처리. 지급은 한 번만, 모든 증감은 원장에.</summary>
    public sealed class LocalMockBackendBattleTests
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

        private LocalMockBackend NewBackend() => new LocalMockBackend(_store, _clock, null, 7);

        private static T Ok<T>(ApiResult<T> result)
        {
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            Assert.IsTrue(result.ok, $"실패 응답: {result.errorCode} {result.message}");
            return result.data;
        }

        private static MeDto Boot(LocalMockBackend backend)
        {
            ApiResult<MeDto> result = null;
            backend.Bootstrap(r => result = r);
            return Ok(result);
        }

        private static string StartBattle(LocalMockBackend backend)
        {
            ApiResult<BattleSessionDto> result = null;
            backend.StartBattle(StageCodes.Outskirts1, r => result = r);
            return Ok(result).sessionId;
        }

        private static ApiResult<BattleResultDto> Finish(LocalMockBackend backend, string session, BattleReport report)
        {
            ApiResult<BattleResultDto> result = null;
            backend.FinishBattle(session, report, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        private static ApiResult<TowerUnlockDto> Unlock(LocalMockBackend backend, string tower)
        {
            ApiResult<TowerUnlockDto> result = null;
            backend.UnlockTower(tower, r => result = r);
            Assert.IsNotNull(result, "응답 콜백이 불리지 않았다");
            return result;
        }

        private BattleResultDto Win(LocalMockBackend backend)
        {
            string session = StartBattle(backend);
            _clock.Advance(300);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);
            return Ok(Finish(backend, session, BattleRulesTests.WinReport(stage)));
        }

        [Test]
        public void NewPlayer_HasTheMachineGunTower_ButNotTheBlastTower()
        {
            MeDto me = Boot(NewBackend());
            CollectionAssert.Contains(me.unlockedTowers, TowerCodes.MachineGun);
            CollectionAssert.DoesNotContain(me.unlockedTowers, TowerCodes.Blast);
            Assert.AreEqual(0, me.clearedStages.Count);
        }

        [Test]
        public void Win_PaysThroughTheLedger_AndMarksTheStageCleared()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);
            string session = StartBattle(backend);
            _clock.Advance(300);

            BattleResultDto result = Ok(Finish(backend, session, BattleRulesTests.WinReport(stage)));
            Assert.IsTrue(result.accepted, result.rejectReason);
            Assert.IsTrue(result.firstWin);
            Assert.AreEqual(stage.winGc + stage.firstWinBonusGc, result.rewardGc);
            Assert.AreEqual(result.rewardGc, result.gc);
            Assert.AreEqual(stage.winXp, result.rewardXp);
            Assert.GreaterOrEqual(result.levelsGained, 1, "첫 승리로 레벨이 오른다");
            Assert.Greater(result.level, 1);
            CollectionAssert.Contains(result.clearedStages, stage.code);

            Assert.AreEqual(1, backend.CurrentLedger.Count);
            LedgerEntry entry = backend.CurrentLedger[0];
            Assert.AreEqual(result.rewardGc, entry.delta);
            Assert.AreEqual(LedgerReasons.BattleReward, entry.reason);
            Assert.AreEqual(session, entry.reference);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void ResendingTheResult_ReturnsTheSameAnswer_AndPaysOnce()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);
            string session = StartBattle(backend);
            _clock.Advance(300);

            BattleResultDto first = Ok(Finish(backend, session, BattleRulesTests.WinReport(stage)));
            BattleResultDto again = Ok(Finish(backend, session, BattleRulesTests.WinReport(stage)));
            Assert.AreEqual(first.rewardGc, again.rewardGc);
            Assert.AreEqual(first.gc, again.gc);
            Assert.IsTrue(again.firstWin, "처음 응답을 그대로 돌려준다");
            Assert.AreEqual(1, backend.CurrentLedger.Count);

            // 진 전투를 이겼다고 다시 보내도 처음 결과가 유지된다
            string lost = StartBattle(backend);
            _clock.Advance(5);
            BattleResultDto retreat = Ok(Finish(backend, lost,
                new BattleReport { result = BattleResults.Lose, wavesCleared = 0, baseHpLeft = stage.baseHp }));
            Assert.AreEqual(0, retreat.rewardGc);
            _clock.Advance(300);
            BattleResultDto rewritten = Ok(Finish(backend, lost, BattleRulesTests.WinReport(stage)));
            Assert.AreEqual(BattleResults.Lose, rewritten.result);
            Assert.AreEqual(0, rewritten.rewardGc);
            Assert.AreEqual(1, backend.CurrentLedger.Count);
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void SecondWin_HasNoFirstWinBonus()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);

            BattleResultDto first = Win(backend);
            BattleResultDto second = Win(backend);
            Assert.IsFalse(second.firstWin);
            Assert.AreEqual(stage.winGc, second.rewardGc);
            Assert.AreEqual(first.gc + stage.winGc, second.gc);
            Assert.AreEqual(1, second.clearedStages.Count, "같은 스테이지가 두 번 적히지 않는다");
            Assert.IsNull(backend.FindLedgerInconsistency());
        }

        [Test]
        public void ImpossiblyFastWin_IsRecordedButNotPaid()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            StageDef stage = backend.Defs.FindStage(StageCodes.Outskirts1);
            string session = StartBattle(backend);
            _clock.Advance(3);

            BattleResultDto result = Ok(Finish(backend, session, BattleRulesTests.WinReport(stage)));
            Assert.IsFalse(result.accepted);
            Assert.AreEqual(BattleRejectReasons.TooFast, result.rejectReason);
            Assert.AreEqual(0, result.rewardGc);
            Assert.AreEqual(0, result.gc);
            Assert.AreEqual(1, result.level);
            Assert.AreEqual(0, result.clearedStages.Count);
            Assert.AreEqual(0, backend.CurrentLedger.Count);

            // 기다렸다 다시 보내도 이미 끝난 전투다
            _clock.Advance(600);
            BattleResultDto later = Ok(Finish(backend, session, BattleRulesTests.WinReport(stage)));
            Assert.IsFalse(later.accepted);
            Assert.AreEqual(0, backend.CurrentLedger.Count);
        }

        [Test]
        public void UnknownBattleOrStage_IsRefused()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);

            ApiResult<BattleResultDto> finish = Finish(backend, "b_does_not_exist", new BattleReport { result = BattleResults.Win });
            Assert.IsFalse(finish.ok);
            Assert.AreEqual(ApiErrors.NotFound, finish.errorCode);

            ApiResult<BattleSessionDto> start = null;
            backend.StartBattle("no_such_stage", r => start = r);
            Assert.IsFalse(start.ok);
            Assert.AreEqual(ApiErrors.NotFound, start.errorCode);
        }

        [Test]
        public void BattleCalls_BeforeBoot_AreUnauthorized()
        {
            ApiResult<BattleSessionDto> start = null;
            NewBackend().StartBattle(StageCodes.Outskirts1, r => start = r);
            Assert.IsFalse(start.ok);
            Assert.AreEqual(ApiErrors.Unauthorized, start.errorCode);

            ApiResult<TowerUnlockDto> unlock = Unlock(NewBackend(), TowerCodes.Blast);
            Assert.IsFalse(unlock.ok);
            Assert.AreEqual(ApiErrors.Unauthorized, unlock.errorCode);
        }

        [Test]
        public void UnlockTower_NeedsEnoughGc_ChargesOnce_AndSurvivesRestart()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);
            TowerDef blast = backend.Defs.FindTower(TowerCodes.Blast);

            ApiResult<TowerUnlockDto> poor = Unlock(backend, TowerCodes.Blast);
            Assert.IsFalse(poor.ok);
            Assert.AreEqual(ApiErrors.InsufficientFunds, poor.errorCode);
            Assert.AreEqual(0, backend.CurrentLedger.Count);

            long before = Win(backend).gc;
            Assert.GreaterOrEqual(before, blast.unlockGc, "첫 승리 보상으로 폭발 타워를 살 수 있어야 한다");

            TowerUnlockDto unlocked = Ok(Unlock(backend, TowerCodes.Blast));
            Assert.AreEqual(blast.unlockGc, unlocked.costGc);
            Assert.AreEqual(before - blast.unlockGc, unlocked.gc);
            CollectionAssert.Contains(unlocked.unlockedTowers, TowerCodes.Blast);
            CollectionAssert.Contains(unlocked.unlockedTowers, TowerCodes.MachineGun);

            LedgerEntry entry = backend.CurrentLedger[backend.CurrentLedger.Count - 1];
            Assert.AreEqual(-blast.unlockGc, entry.delta);
            Assert.AreEqual(LedgerReasons.TowerUnlock, entry.reason);

            ApiResult<TowerUnlockDto> twice = Unlock(backend, TowerCodes.Blast);
            Assert.IsFalse(twice.ok);
            Assert.AreEqual(ApiErrors.Conflict, twice.errorCode);
            Assert.AreEqual(2, backend.CurrentLedger.Count);
            Assert.IsNull(backend.FindLedgerInconsistency());

            // 껐다 켜도 해금과 클리어 기록이 남는다
            MeDto restored = Boot(NewBackend());
            CollectionAssert.Contains(restored.unlockedTowers, TowerCodes.Blast);
            CollectionAssert.Contains(restored.clearedStages, StageCodes.Outskirts1);
            Assert.AreEqual(unlocked.gc, restored.gc);
        }

        [Test]
        public void UnlockTower_FreeOrUnknownTower_IsRefused()
        {
            LocalMockBackend backend = NewBackend();
            Boot(backend);

            ApiResult<TowerUnlockDto> free = Unlock(backend, TowerCodes.MachineGun);
            Assert.IsFalse(free.ok);
            Assert.AreEqual(ApiErrors.Conflict, free.errorCode);

            ApiResult<TowerUnlockDto> unknown = Unlock(backend, "twr_nothing");
            Assert.IsFalse(unknown.ok);
            Assert.AreEqual(ApiErrors.NotFound, unknown.errorCode);
        }
    }
}
