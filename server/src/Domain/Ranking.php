<?php
namespace CG\Domain;

/**
 * 지역 랭킹 (docs/REGIONAL_COMPETITION.md). 클라이언트의 RankingRules 와 같은 계산이다.
 * 단순한 인구 수가 아니라 활성 가디언 한 사람당 값으로 보고, 침공의 규모로 난이도를 보정한다.
 * 현금 결제와 관계된 항목은 없다.
 */
final class Ranking
{
    public static function defaultWeights()
    {
        return [
            'safety' => 30,
            'successRate' => 20,
            'contribution' => 20,
            'response' => 20,
            'cooperation' => 10,
            'contributionTarget' => 100,
            'cooperationTarget' => 30,
        ];
    }

    public static function fillWeights($stored)
    {
        $weights = self::defaultWeights();
        if (is_array($stored)) {
            foreach ($weights as $key => $value) {
                if (isset($stored[$key])) {
                    $weights[$key] = (int)$stored[$key];
                }
            }
        }
        return $weights;
    }

    /** @return string[] 문제 목록. */
    public static function validateWeights(array $weights)
    {
        $problems = [];
        foreach (['safety', 'successRate', 'contribution', 'response', 'cooperation'] as $key) {
            if (!isset($weights[$key]) || !is_int($weights[$key]) || $weights[$key] < 0 || $weights[$key] > 100) {
                $problems[] = "$key: 0 ~ 100 사이의 정수여야 합니다.";
            }
        }
        foreach (['contributionTarget', 'cooperationTarget'] as $key) {
            if (!isset($weights[$key]) || !is_int($weights[$key]) || $weights[$key] < 1 || $weights[$key] > 1000000) {
                $problems[] = "$key: 1 이상의 정수여야 합니다.";
            }
        }
        if (!$problems && $weights['safety'] + $weights['successRate'] + $weights['contribution']
            + $weights['response'] + $weights['cooperation'] <= 0) {
            $problems[] = '가중치의 합이 0보다 커야 합니다.';
        }
        return $problems;
    }

    private static function percent($value, $full)
    {
        if ($full <= 0 || $value <= 0) {
            return 0;
        }
        $percent = intdiv($value * 100, $full);
        return $percent > 100 ? 100 : $percent;
    }

    /**
     * @param array $stats regionCode, activePlayers, riskCenti, playerWins, playerAttempts,
     *                     invasionSeverityTotal, repelledSeverity, contribution, supportGiven
     */
    public static function score(array $stats, array $weights)
    {
        $active = max(1, $stats['activePlayers']);
        $score = [
            'regionCode' => $stats['regionCode'],
            'rank' => 0,
            'total' => 0,
            'safety' => 100 - Risk::toPoints($stats['riskCenti']),
            // 시도가 적은 도시가 한두 번의 결과로 0점이나 100점이 되지 않게 한다.
            'successRate' => intdiv((max(0, $stats['playerWins']) + 1) * 100, max(0, $stats['playerAttempts']) + 2),
            // 도시의 총합이 아니라 활성 가디언 한 사람당 기여로 본다 (인구 보정).
            'contribution' => self::percent($stats['contribution'], $active * max(1, $weights['contributionTarget'])),
            // 규모가 큰 침공을 막을수록 많이 반영된다 (난이도 보정). 침공이 없었다면 중간값.
            'response' => $stats['invasionSeverityTotal'] <= 0
                ? 50
                : self::percent($stats['repelledSeverity'], $stats['invasionSeverityTotal']),
            'cooperation' => self::percent($stats['supportGiven'], $active * max(1, $weights['cooperationTarget'])),
            // 활성 가디언이 있는 도시인가. 없으면 NPC 수비대만 지키는 도시다.
            'playerDefended' => $stats['activePlayers'] > 0,
        ];
        $weightSum = $weights['safety'] + $weights['successRate'] + $weights['contribution']
            + $weights['response'] + $weights['cooperation'];
        if ($weightSum <= 0) {
            return $score;
        }
        $sum = $score['safety'] * $weights['safety'] + $score['successRate'] * $weights['successRate']
            + $score['contribution'] * $weights['contribution'] + $score['response'] * $weights['response']
            + $score['cooperation'] * $weights['cooperation'];
        $score['total'] = intdiv($sum * 100, $weightSum);
        return $score;
    }

    /**
     * 순위를 매긴다. 활성 가디언이 있는 도시끼리 먼저 겨루고, NPC 수비대만 있는 도시는 그 뒤에 온다.
     * NPC가 지킨 결과로 실제 플레이어의 도시를 앞지르지 않게 하기 위해서다.
     *
     * @param array[] $all
     * @return array[] 순위 순서의 점수.
     */
    public static function rank(array $all, array $weights)
    {
        $scores = [];
        foreach ($all as $stats) {
            $scores[] = self::score($stats, $weights);
        }
        usort($scores, function ($a, $b) {
            if ($a['playerDefended'] !== $b['playerDefended']) {
                return $a['playerDefended'] ? -1 : 1;
            }
            if ($a['total'] !== $b['total']) {
                return $b['total'] < $a['total'] ? -1 : 1;
            }
            return strcmp($a['regionCode'], $b['regionCode']);
        });
        foreach ($scores as $i => $score) {
            $scores[$i]['rank'] = $i + 1;
        }
        return $scores;
    }
}
