<?php
namespace CG\Domain;

/**
 * FPS 시내 침입 임무의 결과 검증과 보상 계산 (docs/FPS_MODE.md 7장). 클라이언트의 FpsRules 와 같다.
 * 임무 정의 $mission 의 키는 클라이언트 FpsMissionDef 의 필드 이름과 같다.
 */
final class Fps
{
    /** 제한 시간 뒤에 보고가 도착해도 받아 주는 여유(초). */
    const LATE_GRACE_SECONDS = 30.0;

    const INVALID_RESULT = 'invalid_result';
    const INVALID_COUNT = 'invalid_count';
    const UNKNOWN_ENEMY = 'enemy_not_in_mission';
    const TOO_MANY_KILLS = 'kills_exceed_mission';
    const INVALID_FACILITIES = 'invalid_facilities';
    const INVALID_CIVILIANS = 'invalid_civilians';
    const TOO_FAST = 'finished_too_fast';
    const TOO_LATE = 'time_limit_exceeded';
    const INCONSISTENT = 'kills_and_facilities_mismatch';

    public static function totalEnemies(array $mission)
    {
        $total = 0;
        foreach ($mission['groups'] as $group) {
            $total += $group['count'];
        }
        return $total;
    }

    public static function countOf(array $mission, $enemyCode)
    {
        $total = 0;
        foreach ($mission['groups'] as $group) {
            if ($group['enemyCode'] === $enemyCode) {
                $total += $group['count'];
            }
        }
        return $total;
    }

    /**
     * @param array|null $report result, kills, facilitiesSaved, civiliansSaved
     * @param float $serverElapsedSeconds 서버가 잰, 임무 시작부터 지금까지의 시간.
     * @param bool $alreadyCleared 이 플레이어가 이 임무를 이긴 적이 있는가.
     * @param bool $rewardAllowed 오늘 보상을 받을 수 있는 횟수가 남았는가. 없으면 GC는 0이고 경험치만 준다.
     */
    public static function evaluate(array $mission, $report, $serverElapsedSeconds, $alreadyCleared, $rewardAllowed)
    {
        $verdict = [
            'accepted' => false,
            'result' => $report ? $report['result'] : null,
            'rewardGc' => 0,
            'rewardXp' => 0,
            'firstWin' => false,
            'facilitiesSaved' => 0,
            'civiliansSaved' => 0,
            'rejectReason' => null,
        ];
        if (!$report) {
            return self::reject($verdict, self::INVALID_RESULT);
        }
        $win = $report['result'] === Battle::WIN;
        if (!$win && $report['result'] !== Battle::LOSE) {
            return self::reject($verdict, self::INVALID_RESULT);
        }

        // 처치 수: 임무에 나오는 적만, 나오는 수 이내로.
        $totalKills = 0;
        $seen = [];
        foreach ($report['kills'] as $kill) {
            if ($kill['count'] < 0) {
                return self::reject($verdict, self::INVALID_COUNT);
            }
            if ($kill['count'] === 0) {
                continue;
            }
            $inMission = self::countOf($mission, $kill['enemyCode']);
            if ($inMission === 0) {
                return self::reject($verdict, self::UNKNOWN_ENEMY);
            }
            if (isset($seen[$kill['enemyCode']])) {
                return self::reject($verdict, self::INVALID_COUNT);
            }
            $seen[$kill['enemyCode']] = true;
            if ($kill['count'] > $inMission) {
                return self::reject($verdict, self::TOO_MANY_KILLS);
            }
            $totalKills += $kill['count'];
        }

        if ($report['facilitiesSaved'] < 0 || $report['facilitiesSaved'] > $mission['facilityCount']) {
            return self::reject($verdict, self::INVALID_FACILITIES);
        }
        if ($report['civiliansSaved'] < 0 || $report['civiliansSaved'] > $mission['civilianCount']) {
            return self::reject($verdict, self::INVALID_CIVILIANS);
        }
        if ($serverElapsedSeconds > $mission['timeLimitSeconds'] + self::LATE_GRACE_SECONDS) {
            return self::reject($verdict, self::TOO_LATE);
        }

        $verdict['facilitiesSaved'] = $report['facilitiesSaved'];
        $verdict['civiliansSaved'] = $report['civiliansSaved'];

        if (!$win) {
            // 실패(후퇴 포함)에는 보상이 없다. 기록만 남는다.
            $verdict['accepted'] = true;
            return $verdict;
        }

        if ($serverElapsedSeconds < $mission['minSeconds']) {
            return self::reject($verdict, self::TOO_FAST);
        }
        if ($report['facilitiesSaved'] < 1) {
            return self::reject($verdict, self::INVALID_FACILITIES);
        }
        // 쓰러뜨리지 못한 로봇은 시설에 닿아 멈췄다. 그 수는 시설이 견딜 수 있는 양을 넘을 수 없다.
        $leaked = self::totalEnemies($mission) - $totalKills;
        if ($leaked > $mission['facilityCount'] * $mission['facilityHp'] - $report['facilitiesSaved']) {
            return self::reject($verdict, self::INCONSISTENT);
        }

        $verdict['accepted'] = true;
        $verdict['firstWin'] = !$alreadyCleared;
        $verdict['rewardXp'] = $mission['winXp'];
        if ($rewardAllowed) {
            $verdict['rewardGc'] = $mission['winGc']
                + ($verdict['firstWin'] ? $mission['firstWinBonusGc'] : 0)
                + $mission['civilianBonusGc'] * $report['civiliansSaved']
                + $mission['facilityBonusGc'] * $report['facilitiesSaved'];
        }
        return $verdict;
    }

    private static function reject(array $verdict, $reason)
    {
        $verdict['accepted'] = false;
        $verdict['rewardGc'] = 0;
        $verdict['rewardXp'] = 0;
        $verdict['firstWin'] = false;
        $verdict['rejectReason'] = $reason;
        return $verdict;
    }
}
