using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using NUnit.Framework;

namespace CityGuardians.Tests
{
    public sealed class ProgressionTests
    {
        private static readonly ProgressionConfig Config = DefaultDefs.Create().progression;

        [Test]
        public void XpForNextLevel_FollowsDocumentedFormula()
        {
            // round(50 × level^1.5)
            Assert.AreEqual(50, Progression.XpForNextLevel(1, Config));
            Assert.AreEqual(141, Progression.XpForNextLevel(2, Config));
            Assert.AreEqual(260, Progression.XpForNextLevel(3, Config));
        }

        [Test]
        public void MaxHp_Grows10PerLevel()
        {
            Assert.AreEqual(100, Progression.MaxHp(1, Config));
            Assert.AreEqual(140, Progression.MaxHp(5, Config));
        }

        [Test]
        public void AddXp_CarriesRemainderAcrossSeveralLevels()
        {
            int level = 1, xp = 0;
            int gained = Progression.AddXp(ref level, ref xp, 50 + 141 + 7, Config);
            Assert.AreEqual(2, gained);
            Assert.AreEqual(3, level);
            Assert.AreEqual(7, xp);
        }

        [Test]
        public void AddXp_StopsAtMaxLevel()
        {
            int level = Config.maxLevel - 1, xp = 0;
            Progression.AddXp(ref level, ref xp, 10_000_000, Config);
            Assert.AreEqual(Config.maxLevel, level);
            Assert.AreEqual(0, xp);

            Assert.AreEqual(0, Progression.AddXp(ref level, ref xp, 500, Config));
            Assert.AreEqual(0, xp);
        }

        [Test]
        public void Damage_IsNeverBelowOne()
        {
            Assert.AreEqual(25, DamageFormula.Compute(25, 1f, 0));
            Assert.AreEqual(30, DamageFormula.Compute(25, 1.2f, 0));
            Assert.AreEqual(1, DamageFormula.Compute(25, 1f, 999));
        }
    }

    public sealed class EnemyBrainTests
    {
        private static readonly EnemyDef Robot = DefaultDefs.Create().FindEnemy(EnemyCodes.MalfunctionRobot);

        private static EnemyBrain Awake()
        {
            var brain = new EnemyBrain(Robot, new Random(1));
            brain.Tick(EnemyBrain.SpawnDuration + 0.01f, default);
            Assert.AreEqual(EnemyState.Idle, brain.State);
            return brain;
        }

        private static EnemySenses Target(float distance, bool canEngage = true, bool inLeash = true) =>
            new EnemySenses { hasTarget = true, targetDistance = distance, targetInsideLeash = inLeash, canEngage = canEngage };

        [Test]
        public void StartsInSpawn_AndIgnoresDamageUntilReady()
        {
            var brain = new EnemyBrain(Robot, new Random(1));
            Assert.AreEqual(EnemyState.Spawn, brain.State);
            brain.OnDamaged();
            Assert.AreEqual(EnemyState.Spawn, brain.State);
        }

        [Test]
        public void ChasesOnlyInsideDetectRange()
        {
            EnemyBrain brain = Awake();
            brain.Tick(0.1f, Target(Robot.detectRange + 1f));
            Assert.AreNotEqual(EnemyState.Chase, brain.State);

            EnemyIntent intent = brain.Tick(0.1f, Target(Robot.detectRange - 1f));
            Assert.AreEqual(EnemyState.Chase, brain.State);
            Assert.AreEqual(EnemyMove.ToTarget, intent.move);
        }

        [Test]
        public void Attack_TelegraphsThenStrikesExactlyOnce()
        {
            EnemyBrain brain = Awake();
            brain.Tick(0.1f, Target(5f));
            EnemyIntent start = brain.Tick(0.1f, Target(1f));
            Assert.AreEqual(EnemyState.Attack, brain.State);
            Assert.IsTrue(start.windupStarted);
            Assert.IsFalse(start.strike, "예고 없이 바로 때리면 안 된다");

            int strikes = 0;
            float elapsed = 0f;
            while (elapsed < Robot.attackInterval - 0.05f)
            {
                EnemyIntent intent = brain.Tick(0.05f, Target(1f));
                elapsed += 0.05f;
                if (intent.strike)
                {
                    strikes++;
                    Assert.GreaterOrEqual(elapsed, Robot.attackWindup - 0.001f, "예고 시간이 지나기 전에 때렸다");
                }
            }
            Assert.AreEqual(1, strikes);
        }

        [Test]
        public void GivesUp_WhenTargetLeavesTheZone()
        {
            EnemyBrain brain = Awake();
            brain.Tick(0.1f, Target(5f));
            EnemyIntent intent = brain.Tick(0.1f, Target(5f, true, false));
            Assert.AreEqual(EnemyState.Return, brain.State);
            Assert.AreEqual(EnemyMove.ToHome, intent.move);

            EnemySenses home = default;
            home.atHome = true;
            EnemyIntent arrived = brain.Tick(0.1f, home);
            Assert.AreEqual(EnemyState.Idle, brain.State);
            Assert.IsTrue(arrived.healToFull);
        }

        [Test]
        public void WaitsNearby_WhenAllAttackSlotsAreTaken()
        {
            EnemyBrain brain = Awake();
            brain.Tick(0.1f, Target(5f, false));
            EnemyIntent intent = brain.Tick(0.1f, Target(1f, false));
            Assert.AreEqual(EnemyState.Chase, brain.State);
            Assert.AreEqual(EnemyMove.None, intent.move);
        }

        [Test]
        public void Damage_StaggersAndThenChasesAttackerBeyondDetectRange()
        {
            EnemyBrain brain = Awake();
            brain.OnDamaged();
            Assert.AreEqual(EnemyState.Stagger, brain.State);
            // 감지 거리 밖이지만, 맞았으므로 쫓아간다.
            brain.Tick(EnemyBrain.StaggerDuration + 0.01f, Target(Robot.detectRange + 3f));
            Assert.AreEqual(EnemyState.Chase, brain.State);
        }

        [Test]
        public void Dead_StaysDead()
        {
            EnemyBrain brain = Awake();
            brain.OnDied();
            brain.Tick(5f, Target(1f));
            Assert.AreEqual(EnemyState.Dead, brain.State);
        }
    }

    public sealed class HuntRulesTests
    {
        private static readonly GameDefs Defs = DefaultDefs.Create();
        private static readonly HuntZoneDef NorthEast = Defs.FindZone(ZoneCodes.NorthEast);

        private static List<KillCount> Kills(string code, int count) => new List<KillCount> { new KillCount(code, count) };

        [Test]
        public void RewardIsComputedFromDefinitions_NotFromTheClient()
        {
            HuntVerdict verdict = HuntRules.Evaluate(Defs, NorthEast, Kills(EnemyCodes.MalfunctionRobot, 3),
                10f, NorthEast.burstAllowance, 0, out _);
            Assert.AreEqual(30, verdict.rewardGc);
            Assert.AreEqual(30, verdict.rewardXp);
            Assert.IsNull(verdict.rejectReason);
        }

        [Test]
        public void EnemyNotInZone_IsRejected()
        {
            HuntVerdict verdict = HuntRules.Evaluate(Defs, NorthEast, Kills(EnemyCodes.MechInsect, 2),
                10f, NorthEast.burstAllowance, 0, out _);
            Assert.AreEqual(0, verdict.rewardGc);
            Assert.AreEqual(HuntRejectReasons.UnknownEnemy, verdict.rejectReason);
            Assert.AreEqual(2, verdict.rejectedKills);
        }

        [Test]
        public void ImpossiblyFastKills_AreCutToTheAllowance()
        {
            // 방금 시작한 사냥에서 1초 만에 100마리: 여유분(5)과 1초치만 인정된다.
            HuntVerdict verdict = HuntRules.Evaluate(Defs, NorthEast, Kills(EnemyCodes.MalfunctionRobot, 100),
                1f, NorthEast.burstAllowance, 0, out float left);
            Assert.AreEqual(5, verdict.accepted[0].count);
            Assert.AreEqual(HuntRejectReasons.TooFast, verdict.rejectReason);
            Assert.AreEqual(95, verdict.rejectedKills);
            Assert.Less(left, 1f);
        }

        [Test]
        public void AllowanceRefillsOverTime_ButIsCapped()
        {
            HuntRules.Evaluate(Defs, NorthEast, null, 3600f, 0f, 0, out float afterHour);
            Assert.AreEqual(NorthEast.maxKillsPerMinute, afterHour, 0.001f, "오래 기다려도 1분치까지만 쌓인다");

            HuntRules.Evaluate(Defs, NorthEast, null, 30f, 0f, 0, out float afterHalfMinute);
            Assert.AreEqual(NorthEast.maxKillsPerMinute * 0.5f, afterHalfMinute, 0.001f);
        }

        [Test]
        public void DailyLimit_CapsGcButKeepsXp()
        {
            int almost = Defs.economy.dailyHuntGcLimit - 15;
            HuntVerdict verdict = HuntRules.Evaluate(Defs, NorthEast, Kills(EnemyCodes.MalfunctionRobot, 3),
                60f, NorthEast.burstAllowance, almost, out _);
            Assert.AreEqual(15, verdict.rewardGc);
            Assert.AreEqual(30, verdict.rewardXp);
            Assert.AreEqual(HuntRejectReasons.DailyLimit, verdict.rejectReason);
        }

        [Test]
        public void NegativeCounts_GiveNothing()
        {
            HuntVerdict verdict = HuntRules.Evaluate(Defs, NorthEast, Kills(EnemyCodes.MalfunctionRobot, -5),
                60f, NorthEast.burstAllowance, 0, out _);
            Assert.AreEqual(0, verdict.rewardGc);
            Assert.AreEqual(0, verdict.accepted.Count);
            Assert.AreEqual(HuntRejectReasons.InvalidCount, verdict.rejectReason);
        }
    }

    public sealed class WalletTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 13, 3, 0, 0, DateTimeKind.Utc);

        [Test]
        public void EveryChangeIsRecorded_AndBalanceMatchesLedger()
        {
            var wallet = new Wallet();
            wallet.Apply(100, LedgerReasons.QuestReward, "first_rescue", Now);
            wallet.Apply(10, LedgerReasons.HuntReward, "h#1", Now);
            wallet.Apply(-30, "shop_purchase", "item", Now);

            Assert.AreEqual(80, wallet.Balance);
            Assert.AreEqual(3, wallet.Ledger.Count);
            Assert.AreEqual(80, wallet.Ledger[2].balanceAfter);
            Assert.AreEqual(3, wallet.Ledger[2].seq);
            Assert.IsNull(wallet.FindInconsistency(0));
        }

        [Test]
        public void CannotGoBelowZero_AndNothingIsRecordedWhenRefused()
        {
            var wallet = new Wallet();
            wallet.Apply(50, LedgerReasons.HuntReward, "h#1", Now);
            Assert.Throws<InsufficientFundsException>(() => wallet.Apply(-51, "shop_purchase", "item", Now));
            Assert.AreEqual(50, wallet.Balance);
            Assert.AreEqual(1, wallet.Ledger.Count);
        }

        [Test]
        public void ZeroAmountAndMissingReason_AreRefused()
        {
            var wallet = new Wallet();
            Assert.Throws<ArgumentException>(() => wallet.Apply(0, LedgerReasons.HuntReward, "x", Now));
            Assert.Throws<ArgumentException>(() => wallet.Apply(10, "", "x", Now));
        }

        [Test]
        public void TamperedBalance_IsDetected()
        {
            var ledger = new List<LedgerEntry>();
            var honest = new Wallet(0, ledger, 1);
            honest.Apply(100, LedgerReasons.QuestReward, "first_rescue", Now);

            var tampered = new Wallet(999_999, ledger, honest.NextSeq);
            Assert.IsNotNull(tampered.FindInconsistency(0));
        }
    }
}
