using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>클라이언트가 FPS 임무가 끝났을 때 보내는 내용. 걸린 시간과 금액은 보내지 않는다.</summary>
    [Serializable]
    public sealed class FpsReport
    {
        public string result;
        public List<KillCount> kills = new List<KillCount>();
        /// <summary>끝까지 지켜낸 주요 시설의 수.</summary>
        public int facilitiesSaved;
        /// <summary>대피시킨 시민의 수.</summary>
        public int civiliansSaved;
    }

    public sealed class FpsVerdict
    {
        /// <summary>보고가 규칙에 맞는가. 맞지 않으면 보상도 없고 도시에도 반영되지 않는다.</summary>
        public bool accepted;
        public string result;
        public int rewardGc;
        public int rewardXp;
        public bool firstWin;
        public int facilitiesSaved;
        public int civiliansSaved;
        public string rejectReason;
    }

    public static class FpsRejectReasons
    {
        public const string InvalidResult = "invalid_result";
        public const string InvalidCount = "invalid_count";
        public const string UnknownEnemy = "enemy_not_in_mission";
        public const string TooManyKills = "kills_exceed_mission";
        public const string InvalidFacilities = "invalid_facilities";
        public const string InvalidCivilians = "invalid_civilians";
        public const string TooFast = "finished_too_fast";
        public const string TooLate = "time_limit_exceeded";
        /// <summary>이겼다고 했지만 남은 로봇을 설명할 수 없다.</summary>
        public const string Inconsistent = "kills_and_facilities_mismatch";
    }

    /// <summary>
    /// FPS 임무 결과 검증과 보상 계산 (docs/FPS_MODE.md 7장). 방어전의 BattleRules 와 같은 방식이다.
    /// 실제 서버(PHP)의 검증이 이 규칙과 같아야 한다.
    /// </summary>
    public static class FpsRules
    {
        /// <summary>제한 시간 뒤에 보고가 도착해도 받아 주는 여유(초). 통신이 느린 경우를 위한 것이다.</summary>
        public const float LateGraceSeconds = 30f;

        /// <param name="serverElapsedSeconds">서버가 잰, 임무 시작부터 지금까지의 시간.</param>
        /// <param name="alreadyCleared">이 플레이어가 이 임무를 이긴 적이 있는가.</param>
        /// <param name="rewardAllowed">오늘 보상을 받을 수 있는 횟수가 남았는가. 없으면 GC는 0이고 경험치만 준다.</param>
        public static FpsVerdict Evaluate(FpsMissionDef mission, FpsReport report, float serverElapsedSeconds,
            bool alreadyCleared, bool rewardAllowed)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            var verdict = new FpsVerdict { result = report != null ? report.result : null };
            if (report == null) return Reject(verdict, FpsRejectReasons.InvalidResult);

            bool win = report.result == BattleResults.Win;
            if (!win && report.result != BattleResults.Lose) return Reject(verdict, FpsRejectReasons.InvalidResult);

            // 처치 수: 임무에 나오는 적만, 나오는 수 이내로.
            int totalKills = 0;
            var seen = new HashSet<string>();
            if (report.kills != null)
            {
                for (int i = 0; i < report.kills.Count; i++)
                {
                    KillCount kill = report.kills[i];
                    if (kill.count < 0) return Reject(verdict, FpsRejectReasons.InvalidCount);
                    if (kill.count == 0) continue;
                    int inMission = mission.CountOf(kill.enemyCode);
                    if (inMission == 0) return Reject(verdict, FpsRejectReasons.UnknownEnemy);
                    if (!seen.Add(kill.enemyCode)) return Reject(verdict, FpsRejectReasons.InvalidCount);
                    if (kill.count > inMission) return Reject(verdict, FpsRejectReasons.TooManyKills);
                    totalKills += kill.count;
                }
            }

            if (report.facilitiesSaved < 0 || report.facilitiesSaved > mission.facilityCount)
                return Reject(verdict, FpsRejectReasons.InvalidFacilities);
            if (report.civiliansSaved < 0 || report.civiliansSaved > mission.civilianCount)
                return Reject(verdict, FpsRejectReasons.InvalidCivilians);
            if (serverElapsedSeconds > mission.timeLimitSeconds + LateGraceSeconds)
                return Reject(verdict, FpsRejectReasons.TooLate);

            verdict.facilitiesSaved = report.facilitiesSaved;
            verdict.civiliansSaved = report.civiliansSaved;

            if (!win)
            {
                // 실패(후퇴 포함)에는 보상이 없다. 기록만 남는다.
                verdict.accepted = true;
                return verdict;
            }

            if (serverElapsedSeconds < mission.minSeconds) return Reject(verdict, FpsRejectReasons.TooFast);
            if (report.facilitiesSaved < 1) return Reject(verdict, FpsRejectReasons.InvalidFacilities);
            // 쓰러뜨리지 못한 로봇은 시설에 닿아 멈췄다. 시설에 닿은 로봇은 시설의 내구도를 1 이상 깎으므로,
            // 그 수는 잃은 시설과 남은 시설이 견딜 수 있는 양을 넘을 수 없다.
            int leaked = mission.TotalEnemies - totalKills;
            if (leaked > mission.facilityCount * mission.facilityHp - report.facilitiesSaved)
                return Reject(verdict, FpsRejectReasons.Inconsistent);

            verdict.accepted = true;
            verdict.firstWin = !alreadyCleared;
            verdict.rewardXp = mission.winXp;
            if (rewardAllowed)
            {
                verdict.rewardGc = mission.winGc
                                   + (verdict.firstWin ? mission.firstWinBonusGc : 0)
                                   + mission.civilianBonusGc * report.civiliansSaved
                                   + mission.facilityBonusGc * report.facilitiesSaved;
            }
            return verdict;
        }

        private static FpsVerdict Reject(FpsVerdict verdict, string reason)
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
