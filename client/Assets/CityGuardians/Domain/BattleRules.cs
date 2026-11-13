using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    public static class BattleResults
    {
        public const string Win = "win";
        public const string Lose = "lose";
    }

    /// <summary>클라이언트가 전투가 끝났을 때 보내는 내용 (POST /battles/{id}/finish).</summary>
    [Serializable]
    public sealed class BattleReport
    {
        public string result;
        public List<KillCount> kills = new List<KillCount>();
        public int baseHpLeft;
        public int wavesCleared;
    }

    public sealed class BattleVerdict
    {
        /// <summary>보고가 규칙에 맞는가. 맞지 않으면 보상이 없다.</summary>
        public bool accepted;
        public string result;
        public int wavesCleared;
        public int rewardGc;
        public int rewardXp;
        public bool firstWin;
        public string rejectReason;
    }

    public static class BattleRejectReasons
    {
        public const string InvalidResult = "invalid_result";
        public const string InvalidCount = "invalid_count";
        public const string UnknownEnemy = "enemy_not_in_stage";
        public const string TooManyKills = "kills_exceed_stage";
        public const string InvalidWaves = "invalid_waves";
        public const string InvalidBaseHp = "invalid_base_hp";
        public const string TooFast = "finished_too_fast";
        /// <summary>처치 수와 남은 방어 체력이 서로 맞지 않는다.</summary>
        public const string Inconsistent = "kills_and_base_hp_mismatch";
    }

    /// <summary>
    /// 스테이지 전투(방어전) 결과 검증과 보상 계산 (docs/COMBAT_SYSTEM.md 10.2, docs/TOWER_DEFENSE.md 10장).
    /// 클라이언트는 결과만 보내고 금액은 스테이지 정의에서 이 규칙이 정한다.
    /// 실제 서버(PHP)의 검증이 이 규칙과 같아야 한다.
    /// </summary>
    public static class BattleRules
    {
        /// <param name="serverElapsedSeconds">서버가 잰, 전투 시작부터 지금까지의 시간. 클라이언트가 보낸 값을 쓰지 않는다.</param>
        /// <param name="alreadyCleared">이 플레이어가 이 스테이지를 이긴 적이 있는가.</param>
        public static BattleVerdict Evaluate(StageDef stage, BattleReport report, float serverElapsedSeconds, bool alreadyCleared)
        {
            if (stage == null) throw new ArgumentNullException(nameof(stage));
            var verdict = new BattleVerdict { result = report != null ? report.result : null };
            if (report == null) return Reject(verdict, BattleRejectReasons.InvalidResult);

            bool win = report.result == BattleResults.Win;
            if (!win && report.result != BattleResults.Lose) return Reject(verdict, BattleRejectReasons.InvalidResult);

            int waveCount = stage.waves.Count;
            if (report.wavesCleared < 0 || report.wavesCleared > waveCount) return Reject(verdict, BattleRejectReasons.InvalidWaves);
            verdict.wavesCleared = report.wavesCleared;

            // 처치 수: 스테이지에 나오는 적만, 나오는 수 이내로.
            int totalKills = 0;
            var seen = new HashSet<string>();
            if (report.kills != null)
            {
                for (int i = 0; i < report.kills.Count; i++)
                {
                    KillCount kill = report.kills[i];
                    if (kill.count < 0) return Reject(verdict, BattleRejectReasons.InvalidCount);
                    if (kill.count == 0) continue;
                    int inStage = stage.CountOf(kill.enemyCode);
                    if (inStage == 0) return Reject(verdict, BattleRejectReasons.UnknownEnemy);
                    // 같은 적을 여러 줄로 나눠 보내 한도를 피하지 못하게 한다.
                    if (!seen.Add(kill.enemyCode)) return Reject(verdict, BattleRejectReasons.InvalidCount);
                    if (kill.count > inStage) return Reject(verdict, BattleRejectReasons.TooManyKills);
                    totalKills += kill.count;
                }
            }

            if (serverElapsedSeconds < report.wavesCleared * stage.minSecondsPerWave)
                return Reject(verdict, BattleRejectReasons.TooFast);

            if (win)
            {
                if (report.wavesCleared != waveCount) return Reject(verdict, BattleRejectReasons.InvalidWaves);
                if (report.baseHpLeft < 1 || report.baseHpLeft > stage.baseHp)
                    return Reject(verdict, BattleRejectReasons.InvalidBaseHp);
                // 쓰러뜨리지 못한 적은 방어 거점에 닿았고, 닿을 때마다 방어 체력이 1 이상 깎인다.
                int leaked = stage.TotalEnemies - totalKills;
                if (leaked > stage.baseHp - report.baseHpLeft) return Reject(verdict, BattleRejectReasons.Inconsistent);

                verdict.accepted = true;
                verdict.firstWin = !alreadyCleared;
                verdict.rewardGc = stage.winGc + (verdict.firstWin ? stage.firstWinBonusGc : 0);
                verdict.rewardXp = stage.winXp;
                return verdict;
            }

            // 패배(후퇴 포함): 모든 웨이브를 막았다면 패배일 수 없다.
            if (report.wavesCleared >= waveCount) return Reject(verdict, BattleRejectReasons.InvalidWaves);
            if (report.baseHpLeft < 0 || report.baseHpLeft > stage.baseHp)
                return Reject(verdict, BattleRejectReasons.InvalidBaseHp);
            // 막아낸 웨이브의 적은 쓰러졌거나 방어 거점에 닿았다. 닿은 수는 방어 체력을 넘을 수 없다.
            if (totalKills + stage.baseHp < stage.EnemiesInFirstWaves(report.wavesCleared))
                return Reject(verdict, BattleRejectReasons.Inconsistent);

            verdict.accepted = true;
            verdict.rewardGc = stage.loseGcPerWave * report.wavesCleared;
            verdict.rewardXp = 0;
            return verdict;
        }

        private static BattleVerdict Reject(BattleVerdict verdict, string reason)
        {
            verdict.accepted = false;
            verdict.rewardGc = 0;
            verdict.rewardXp = 0;
            verdict.firstWin = false;
            verdict.rejectReason = reason;
            return verdict;
        }
    }
}
