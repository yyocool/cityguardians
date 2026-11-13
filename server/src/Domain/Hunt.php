<?php
namespace CG\Domain;

/**
 * 사냥 보고 검증과 보상 계산 (docs/HUNT_MODE.md). 클라이언트의 HuntRules 와 같다.
 * 클라이언트는 "무엇을 쓰러뜨렸다"만 보내고 금액은 이 규칙이 정한다.
 */
final class Hunt
{
    const UNKNOWN_ENEMY = 'enemy_not_in_zone';
    const TOO_FAST = 'kill_rate_exceeded';
    const DAILY_LIMIT = 'daily_limit_reached';
    const INVALID_COUNT = 'invalid_count';

    /**
     * @param array $enemies [코드 => 적 정의(rewardGc, rewardXp)]
     * @param array $zone code, enemyCodes, maxKillsPerMinute, burstAllowance
     * @param array[] $reported [[enemyCode, count]]
     * @param float $elapsedSeconds 지난 보고(또는 세션 시작) 이후 흐른 시간.
     * @param float $carriedAllowance 이전 보고까지 쓰지 않고 남은 처치 허용량.
     * @param int $dailyHuntGcSoFar 오늘 이미 받은 사냥 보상.
     * @param int $dailyHuntGcLimit 하루 사냥 보상 한도.
     * @return array [판정, 이번 보고 뒤 남는 허용량]
     */
    public static function evaluate(array $enemies, array $zone, array $reported, $elapsedSeconds, $carriedAllowance,
        $dailyHuntGcSoFar, $dailyHuntGcLimit)
    {
        $verdict = [
            'accepted' => [],
            'rewardGc' => 0,
            'rewardXp' => 0,
            'rejectReason' => null,
            'rejectedKills' => 0,
        ];
        if ($elapsedSeconds < 0) {
            $elapsedSeconds = 0;
        }

        // 시간이 흐른 만큼 허용량이 쌓인다. 쌓일 수 있는 양에는 상한이 있다.
        $cap = max($zone['burstAllowance'], $zone['maxKillsPerMinute']);
        $allowance = min($cap, $carriedAllowance + $zone['maxKillsPerMinute'] * $elapsedSeconds / 60.0);
        $budget = (int)floor($allowance + 0.0001);
        $used = 0;
        $dailyRemaining = max(0, $dailyHuntGcLimit - $dailyHuntGcSoFar);

        foreach ($reported as $kill) {
            $count = (int)$kill['count'];
            if ($count <= 0) {
                if ($count < 0) {
                    self::reject($verdict, self::INVALID_COUNT, 0);
                }
                continue;
            }
            $code = $kill['enemyCode'];
            if (!isset($enemies[$code]) || !in_array($code, $zone['enemyCodes'], true)) {
                self::reject($verdict, self::UNKNOWN_ENEMY, $count);
                continue;
            }
            $allowed = min($count, $budget - $used);
            if ($allowed < $count) {
                self::reject($verdict, self::TOO_FAST, $count - $allowed);
            }
            if ($allowed <= 0) {
                continue;
            }
            $used += $allowed;
            $verdict['accepted'][] = ['enemyCode' => $code, 'count' => $allowed];
            $verdict['rewardXp'] += $enemies[$code]['rewardXp'] * $allowed;

            $gc = $enemies[$code]['rewardGc'] * $allowed;
            if ($gc > $dailyRemaining) {
                $gc = $dailyRemaining;
                if ($verdict['rejectReason'] === null) {
                    $verdict['rejectReason'] = self::DAILY_LIMIT;
                }
            }
            $dailyRemaining -= $gc;
            $verdict['rewardGc'] += $gc;
        }

        return [$verdict, $allowance - $used];
    }

    private static function reject(array &$verdict, $reason, $count)
    {
        $verdict['rejectedKills'] += $count;
        // 속도 초과와 없는 적은 한도 도달보다 먼저 알린다.
        if ($verdict['rejectReason'] === null || $verdict['rejectReason'] === self::DAILY_LIMIT) {
            $verdict['rejectReason'] = $reason;
        }
    }
}
