<?php
namespace CG\Service;

use CG\App;

/**
 * v9.0 의 예약 작업 (bin/tick.php 가 부른다). 여러 번 돌아도 같은 일을 두 번 하지 않는다.
 *
 *   - 도시별 전입자 현황을 다시 세고 오늘의 기록을 남긴다 (기준에 닿으면 알림이 생긴다. 같은 기준의 알림은 한 번만).
 *   - 재산세를 걷는다 (정책이 켜져 있을 때만. 한 기간에 한 사람에게 한 번).
 *   - 투표 기간이 끝난 참여예산 제안의 결과를 확정한다.
 *   - 시간이 지난 협동 방어전을 닫는다.
 *   - 기록의 앞뒤(전입자 수, 도시 재정 원장, 협동 방어전 보상과 공동 예산)를 점검한다.
 */
final class CityOpsTick
{
    /** @return array population, propertyTaxPayers, propertyTaxGc, budgetClosed, coopClosed, problems(문자열 목록) */
    public static function run(App $app)
    {
        $population = $app->growth()->refreshAll()['refreshed'];
        $property = $app->taxes()->collectProperty();
        $budget = $app->budget()->closeDue();
        $coop = $app->coop()->expireDue();
        $problems = [];
        foreach ([
            '전입자 집계' => $app->growth()->findInconsistency(),
            '도시 재정' => $app->treasury()->findInconsistency(),
            '협동 방어전' => $app->coop()->findInconsistency(),
        ] as $name => $problem) {
            if ($problem !== null) {
                $problems[] = $name . ': ' . $problem;
            }
        }
        return [
            'population' => $population, 'propertyTaxPayers' => $property['collected'], 'propertyTaxGc' => $property['totalGc'],
            'budgetClosed' => $budget, 'coopClosed' => $coop, 'problems' => $problems,
        ];
    }
}
