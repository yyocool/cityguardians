using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    [Serializable]
    public struct KillCount
    {
        public string enemyCode;
        public int count;

        public KillCount(string enemyCode, int count)
        {
            this.enemyCode = enemyCode;
            this.count = count;
        }
    }

    public sealed class HuntVerdict
    {
        public List<KillCount> accepted = new List<KillCount>();
        public int rewardGc;
        public int rewardXp;
        /// <summary>일부 또는 전부를 인정하지 않았다면 그 이유. 전부 인정했으면 null.</summary>
        public string rejectReason;
        public int rejectedKills;
    }

    public static class HuntRejectReasons
    {
        public const string UnknownEnemy = "enemy_not_in_zone";
        public const string TooFast = "kill_rate_exceeded";
        public const string DailyLimit = "daily_limit_reached";
        public const string InvalidCount = "invalid_count";
    }

    /// <summary>
    /// 사냥 보고 검증과 보상 계산 (docs/COMBAT_SYSTEM.md 10.1).
    /// 클라이언트는 "무엇을 쓰러뜨렸다"만 보내고 금액은 이 규칙이 정한다.
    /// 실제 서버(PHP)의 검증이 이 규칙과 같아야 한다.
    /// </summary>
    public static class HuntRules
    {
        /// <param name="elapsedSeconds">지난 보고(또는 세션 시작) 이후 흐른 시간.</param>
        /// <param name="carriedAllowance">이전 보고까지 쓰지 않고 남은 처치 허용량.</param>
        /// <param name="dailyHuntGcSoFar">오늘 이미 받은 사냥 보상.</param>
        /// <param name="newAllowance">이번 보고 뒤 남는 허용량.</param>
        public static HuntVerdict Evaluate(
            GameDefs defs,
            HuntZoneDef zone,
            IList<KillCount> reported,
            float elapsedSeconds,
            float carriedAllowance,
            int dailyHuntGcSoFar,
            out float newAllowance)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            if (zone == null) throw new ArgumentNullException(nameof(zone));

            var verdict = new HuntVerdict();
            if (elapsedSeconds < 0f) elapsedSeconds = 0f;

            // 시간이 흐른 만큼 허용량이 쌓인다. 쌓일 수 있는 양에는 상한이 있다.
            float cap = Math.Max(zone.burstAllowance, zone.maxKillsPerMinute);
            float allowance = Math.Min(cap, carriedAllowance + zone.maxKillsPerMinute * elapsedSeconds / 60f);
            int budget = (int)Math.Floor(allowance + 0.0001f);
            int used = 0;

            int dailyRemaining = Math.Max(0, defs.economy.dailyHuntGcLimit - dailyHuntGcSoFar);

            if (reported != null)
            {
                for (int i = 0; i < reported.Count; i++)
                {
                    KillCount kill = reported[i];
                    if (kill.count <= 0)
                    {
                        if (kill.count < 0) Reject(verdict, HuntRejectReasons.InvalidCount, 0);
                        continue;
                    }

                    EnemyDef enemy = defs.FindEnemy(kill.enemyCode);
                    if (enemy == null || !ZoneHasEnemy(zone, kill.enemyCode))
                    {
                        Reject(verdict, HuntRejectReasons.UnknownEnemy, kill.count);
                        continue;
                    }

                    int allowed = Math.Min(kill.count, budget - used);
                    if (allowed < kill.count) Reject(verdict, HuntRejectReasons.TooFast, kill.count - allowed);
                    if (allowed <= 0) continue;

                    used += allowed;
                    verdict.accepted.Add(new KillCount(kill.enemyCode, allowed));
                    verdict.rewardXp += enemy.rewardXp * allowed;

                    int gc = enemy.rewardGc * allowed;
                    if (gc > dailyRemaining)
                    {
                        gc = dailyRemaining;
                        if (verdict.rejectReason == null) verdict.rejectReason = HuntRejectReasons.DailyLimit;
                    }
                    dailyRemaining -= gc;
                    verdict.rewardGc += gc;
                }
            }

            newAllowance = allowance - used;
            return verdict;
        }

        private static bool ZoneHasEnemy(HuntZoneDef zone, string enemyCode)
        {
            if (zone.enemyCodes == null) return false;
            for (int i = 0; i < zone.enemyCodes.Length; i++)
                if (zone.enemyCodes[i] == enemyCode) return true;
            return false;
        }

        private static void Reject(HuntVerdict verdict, string reason, int count)
        {
            verdict.rejectedKills += count;
            // 속도 초과와 없는 적은 한도 도달보다 먼저 알린다.
            if (verdict.rejectReason == null || verdict.rejectReason == HuntRejectReasons.DailyLimit)
                verdict.rejectReason = reason;
        }
    }
}
