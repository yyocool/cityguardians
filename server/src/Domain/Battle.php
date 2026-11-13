<?php
namespace CG\Domain;

/**
 * 방어전(타워디펜스) 결과 검증과 보상 계산 (docs/DEFENSE_MODE.md). 클라이언트의 BattleRules 와 같다.
 * 클라이언트는 결과만 보내고, 금액은 구역 정의에서 이 규칙이 정한다. 걸린 시간은 서버가 잰다.
 *
 * 구역 정의 $stage 의 키는 클라이언트 StageDef 의 필드 이름과 같다.
 */
final class Battle
{
    const WIN = 'win';
    const LOSE = 'lose';

    const INVALID_RESULT = 'invalid_result';
    const INVALID_COUNT = 'invalid_count';
    const UNKNOWN_ENEMY = 'enemy_not_in_stage';
    const TOO_MANY_KILLS = 'kills_exceed_stage';
    const INVALID_WAVES = 'invalid_waves';
    const INVALID_BASE_HP = 'invalid_base_hp';
    const TOO_FAST = 'finished_too_fast';
    const INCONSISTENT = 'kills_and_base_hp_mismatch';

    /** 앞에서부터 $waveCount 개 웨이브에 나오는 적의 수. */
    public static function enemiesInFirstWaves(array $stage, $waveCount)
    {
        $total = 0;
        foreach ($stage['waves'] as $i => $wave) {
            if ($i >= $waveCount) {
                break;
            }
            foreach ($wave['groups'] as $group) {
                $total += $group['count'];
            }
        }
        return $total;
    }

    public static function totalEnemies(array $stage)
    {
        return self::enemiesInFirstWaves($stage, count($stage['waves']));
    }

    public static function countOf(array $stage, $enemyCode)
    {
        $total = 0;
        foreach ($stage['waves'] as $wave) {
            foreach ($wave['groups'] as $group) {
                if ($group['enemyCode'] === $enemyCode) {
                    $total += $group['count'];
                }
            }
        }
        return $total;
    }

    /**
     * @param array|null $report result, kills [[enemyCode, count]], baseHpLeft, wavesCleared
     * @param float $serverElapsedSeconds 서버가 잰, 전투 시작부터 지금까지의 시간.
     * @param bool $alreadyCleared 이 플레이어가 이 구역을 이긴 적이 있는가.
     */
    public static function evaluate(array $stage, $report, $serverElapsedSeconds, $alreadyCleared)
    {
        $verdict = [
            'accepted' => false,
            'result' => $report ? $report['result'] : null,
            'wavesCleared' => 0,
            'rewardGc' => 0,
            'rewardXp' => 0,
            'firstWin' => false,
            'rejectReason' => null,
        ];
        if (!$report) {
            return self::reject($verdict, self::INVALID_RESULT);
        }
        $win = $report['result'] === self::WIN;
        if (!$win && $report['result'] !== self::LOSE) {
            return self::reject($verdict, self::INVALID_RESULT);
        }

        $waveCount = count($stage['waves']);
        if ($report['wavesCleared'] < 0 || $report['wavesCleared'] > $waveCount) {
            return self::reject($verdict, self::INVALID_WAVES);
        }
        $verdict['wavesCleared'] = $report['wavesCleared'];

        // 처치 수: 구역에 나오는 적만, 나오는 수 이내로.
        $totalKills = 0;
        $seen = [];
        foreach ($report['kills'] as $kill) {
            if ($kill['count'] < 0) {
                return self::reject($verdict, self::INVALID_COUNT);
            }
            if ($kill['count'] === 0) {
                continue;
            }
            $inStage = self::countOf($stage, $kill['enemyCode']);
            if ($inStage === 0) {
                return self::reject($verdict, self::UNKNOWN_ENEMY);
            }
            // 같은 적을 여러 줄로 나눠 보내 한도를 피하지 못하게 한다.
            if (isset($seen[$kill['enemyCode']])) {
                return self::reject($verdict, self::INVALID_COUNT);
            }
            $seen[$kill['enemyCode']] = true;
            if ($kill['count'] > $inStage) {
                return self::reject($verdict, self::TOO_MANY_KILLS);
            }
            $totalKills += $kill['count'];
        }

        if ($serverElapsedSeconds < $report['wavesCleared'] * $stage['minSecondsPerWave']) {
            return self::reject($verdict, self::TOO_FAST);
        }

        if ($win) {
            if ($report['wavesCleared'] !== $waveCount) {
                return self::reject($verdict, self::INVALID_WAVES);
            }
            if ($report['baseHpLeft'] < 1 || $report['baseHpLeft'] > $stage['baseHp']) {
                return self::reject($verdict, self::INVALID_BASE_HP);
            }
            // 쓰러뜨리지 못한 적은 방어 거점에 닿았고, 닿을 때마다 방어 체력이 1 이상 깎인다.
            $leaked = self::totalEnemies($stage) - $totalKills;
            if ($leaked > $stage['baseHp'] - $report['baseHpLeft']) {
                return self::reject($verdict, self::INCONSISTENT);
            }
            $verdict['accepted'] = true;
            $verdict['firstWin'] = !$alreadyCleared;
            $verdict['rewardGc'] = $stage['winGc'] + ($verdict['firstWin'] ? $stage['firstWinBonusGc'] : 0);
            $verdict['rewardXp'] = $stage['winXp'];
            return $verdict;
        }

        // 패배(후퇴 포함): 모든 웨이브를 막았다면 패배일 수 없다.
        if ($report['wavesCleared'] >= $waveCount) {
            return self::reject($verdict, self::INVALID_WAVES);
        }
        if ($report['baseHpLeft'] < 0 || $report['baseHpLeft'] > $stage['baseHp']) {
            return self::reject($verdict, self::INVALID_BASE_HP);
        }
        // 막아낸 웨이브의 적은 쓰러졌거나 방어 거점에 닿았다. 닿은 수는 방어 체력을 넘을 수 없다.
        if ($totalKills + $stage['baseHp'] < self::enemiesInFirstWaves($stage, $report['wavesCleared'])) {
            return self::reject($verdict, self::INCONSISTENT);
        }
        $verdict['accepted'] = true;
        $verdict['rewardGc'] = $stage['loseGcPerWave'] * $report['wavesCleared'];
        $verdict['rewardXp'] = 0;
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
