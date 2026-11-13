<?php
namespace CG\Admin;

use CG\App;
use CG\Domain\Ranking;
use CG\Domain\Risk;
use CG\Infra\Clock;

/**
 * 위험도 계산 정책과 지역 랭킹 가중치 (docs/CITY_RISK_SYSTEM.md 7장, docs/REGIONAL_COMPETITION.md).
 *
 * 저장할 때마다 city_risk_policy 에 새 행이 생긴다 (이전 정책은 그대로 남는다). 가장 최근 행이 지금의 정책이다.
 * 범위를 벗어난 값, 플레이어의 활동이 위험도를 올리게 되는 가중치는 저장하지 않는다 (Risk::validatePolicy).
 */
final class PolicyEditor
{
    /** @var App */
    private $app;
    /** @var Auth */
    private $auth;

    public function __construct(App $app, Auth $auth)
    {
        $this->app = $app;
        $this->auth = $auth;
    }

    /** 정책의 값에 붙이는 이름. 화면의 묶음 순서대로다. */
    public static function groups()
    {
        return [
            '위험 단계' => [
                'cautionAt' => '주의 단계가 시작되는 위험도', 'dangerAt' => '위험 단계가 시작되는 위험도',
                'emergencyAt' => '긴급 단계가 시작되는 위험도', 'initialRisk' => '도시의 처음 위험도',
                'initialSpread' => '처음 위험도의 도시별 차이 (±)',
            ],
            '플레이어의 활동' => [
                'activePlayerNorm' => '활성 가디언 기준 수 (이보다 많으면 한 사람의 몫이 나뉜다)',
                'activeDays' => '활성 가디언으로 보는 기간(일)', 'dailyPlayerReduceCap' => '한 사람이 하루에 낮출 수 있는 위험도',
                'patrolPercent' => '침공이 없을 때의 승리가 반영되는 비율(%)', 'huntKillsPerStep' => '사냥: 위험도가 한 번 내려가는 처치 수',
                'huntHighRiskPercent' => '위험 단계 이상에서의 사냥 효과(%)',
            ],
            '침공' => [
                'tickMinutes' => '침공 판정 간격(분)', 'invasionChancePermille' => '판정마다 도시에 침공이 생길 확률(‰)',
                'invasionMinutes' => '침공 제한 시간(분)', 'intrusionPermille' => '침공 가운데 시내 침입의 비율(‰)',
                'zeroEventPermille' => '침공 가운데 ZERO 이벤트의 비율(‰)',
            ],
            'NPC 수비대' => [
                'npcHoldBasePercent' => '기본 방어 확률(%)', 'defensePowerPerPercent' => '방어 확률 1%에 필요한 방어력',
                'npcHoldMaxBonusPercent' => '방어력으로 오르는 확률의 한도(%)', 'npcHoldSeverityPenaltyPercent' => '침공 규모 1마다 내려가는 확률(%)',
                'dailyStabilizePercent' => '하루 한 번의 순찰이 낮추는 비율(%)', 'baseGarrisonPower' => '기본 수비대 방어력',
                'defensePointsPowerCap' => '방어 포인트로 오르는 방어력의 한도',
            ],
            '도시 간 지원' => [
                'supportOpenAt' => '지원 요청이 열리는 위험도', 'supportCloseBelow' => '지원 요청이 해결로 닫히는 위험도 (미만)',
                'supportMinutes' => '지원 요청이 열려 있는 시간(분)',
            ],
            '기여도와 한도' => [
                'contribDefenseWin' => '방어전 승리 기여도', 'contribFpsWin' => 'FPS 승리 기여도', 'contribHuntStep' => '사냥 한 단계 기여도',
                'contribRepair' => '시설 복구 기여도', 'contribPerFund100Gc' => '기금 100 GC 당 기여도',
                'contribSeverityBonusPercent' => '침공 규모 1마다 더하는 기여도(%)', 'dailyRepairLimit' => '하루 시설 복구 횟수',
                'dailyFundLimitGc' => '하루 도시 기금 한도 GC',
            ],
        ];
    }

    public static function rankingLabels()
    {
        return [
            'safety' => '안전도 가중치', 'successRate' => '방어 성공률 가중치', 'contribution' => '기여도 가중치',
            'response' => '침공 대응 성과 가중치', 'cooperation' => '협동 활동 가중치',
            'contributionTarget' => '기여도 만점 기준 (활성 가디언 한 사람당)', 'cooperationTarget' => '협동 만점 기준 (활성 가디언 한 사람당)',
        ];
    }

    private static function parseInt($input)
    {
        return is_string($input) && preg_match('/^-?\d{1,9}$/', trim($input)) ? (int)trim($input) : null;
    }

    /**
     * 폼에서 온 값으로 정책을 만든다. 폼에 없는 값은 지금 값 그대로다.
     * @return array [정책, 랭킹 가중치, 문제 목록]
     */
    public function fromForm(array $form)
    {
        $policy = $this->app->defs()->policy();
        unset($policy['version']);
        $ranking = $this->app->defs()->rankingWeights();
        $problems = [];
        foreach (array_keys(Risk::policyRanges()) as $key) {
            if (!array_key_exists($key, $form)) {
                continue;
            }
            $value = self::parseInt($form[$key]);
            if ($value === null) {
                $problems[] = "$key: 정수를 넣어 주세요.";
            } else {
                $policy[$key] = $value;
            }
        }
        foreach (array_keys(Risk::weightReasons()) as $reason) {
            if (!array_key_exists('w_' . $reason, $form)) {
                continue;
            }
            $value = self::parseInt($form['w_' . $reason]);
            if ($value === null) {
                $problems[] = Risk::reasonName($reason) . ': 정수를 넣어 주세요.';
            } else {
                $policy['weights'][$reason] = $value;
            }
        }
        foreach (array_keys(Ranking::defaultWeights()) as $key) {
            if (!array_key_exists('r_' . $key, $form)) {
                continue;
            }
            $value = self::parseInt($form['r_' . $key]);
            if ($value === null) {
                $problems[] = "$key: 정수를 넣어 주세요.";
            } else {
                $ranking[$key] = $value;
            }
        }
        return [$policy, $ranking, $problems];
    }

    /**
     * 정책을 저장한다. 바뀐 것이 없으면 아무것도 하지 않는다.
     * @return array ok, problems, changed
     */
    public function save(array $admin, array $policy, array $ranking, $reason, $ip)
    {
        $reason = trim((string)$reason);
        unset($policy['version']);
        $problems = array_merge(Risk::validatePolicy($policy), Ranking::validateWeights($ranking));
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            $problems[] = $reasonProblem;
        }
        if ($problems) {
            return ['ok' => false, 'problems' => $problems, 'changed' => 0];
        }
        $before = $this->app->defs()->policy();
        unset($before['version']);
        $beforeRanking = $this->app->defs()->rankingWeights();
        $diff = function (array $old, array $new) {
            $a = [];
            $b = [];
            foreach ($new as $key => $value) {
                if ($key === 'weights') {
                    foreach ($value as $reason => $delta) {
                        $was = isset($old['weights'][$reason]) ? $old['weights'][$reason] : null;
                        if ($was !== $delta) {
                            $a['weights.' . $reason] = $was;
                            $b['weights.' . $reason] = $delta;
                        }
                    }
                } elseif (!array_key_exists($key, $old) || $old[$key] !== $value) {
                    $a[$key] = isset($old[$key]) ? $old[$key] : null;
                    $b[$key] = $value;
                }
            }
            return [$a, $b];
        };
        list($policyBefore, $policyAfter) = $diff($before, $policy);
        list($rankingBefore, $rankingAfter) = $diff($beforeRanking, $ranking);
        if (!$policyAfter && !$rankingAfter) {
            return ['ok' => true, 'problems' => [], 'changed' => 0];
        }

        $db = $this->app->db;
        $db->tx(function () use ($db, $admin, $policy, $ranking, $reason, $ip, $policyBefore, $policyAfter, $rankingBefore, $rankingAfter) {
            $id = $db->insert('INSERT INTO city_risk_policy (policy, ranking, admin_id, reason, created_at) VALUES (?, ?, ?, ?, ?)',
                [json_encode($policy), json_encode($ranking), $admin['id'], $reason, Clock::sql()]);
            foreach ([['policy', $policyBefore, $policyAfter], ['ranking', $rankingBefore, $rankingAfter]] as $part) {
                if ($part[2]) {
                    $db->exec(
                        'INSERT INTO config_history (target_type, target_key, before_value, after_value, admin_id, reason, created_at)'
                        . ' VALUES (?, ?, ?, ?, ?, ?, ?)',
                        ['city_risk_policy', $part[0], json_encode($part[1]), json_encode($part[2]), $admin['id'], $reason, Clock::sql()]
                    );
                }
            }
            $this->auth->audit($admin['id'], 'policy.update', 'city_risk_policy', (string)$id,
                implode(', ', array_merge(array_keys($policyAfter), array_keys($rankingAfter))) . ' — ' . $reason, $ip);
        });
        $this->app->defs()->forget();
        // 판정 간격이 바뀌면 순번의 기준이 달라진다. 지난 판정을 한꺼번에 다시 돌리지 않도록 지금 순번에 맞춘다.
        if (isset($policyAfter['tickMinutes'])) {
            $this->app->city()->resyncTick();
        }
        return ['ok' => true, 'problems' => [], 'changed' => count($policyAfter) + count($rankingAfter)];
    }

    /** 지난 정책들 (새 것이 앞). */
    public function versions($limit = 20)
    {
        return $this->app->db->all(
            'SELECT p.id, p.reason, p.created_at, a.login FROM city_risk_policy p LEFT JOIN admins a ON a.id = p.admin_id'
            . ' ORDER BY p.id DESC LIMIT ' . (int)$limit
        );
    }

    /** 지난 정책 하나를 다시 지금의 정책으로 만든다 (새 행으로 저장한다). */
    public function revert(array $admin, $policyId, $reason, $ip)
    {
        $row = $this->app->db->one('SELECT * FROM city_risk_policy WHERE id = ?', [(int)$policyId]);
        if ($row === null) {
            return ['ok' => false, 'problems' => ['없는 정책입니다.'], 'changed' => 0];
        }
        // 사유는 관리자가 쓴 글만 본다 ("되돌리기 #n" 머리말이 붙어 빈 사유가 통과하면 안 된다).
        $reason = is_string($reason) ? trim($reason) : '';
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            return ['ok' => false, 'problems' => [$reasonProblem], 'changed' => 0];
        }
        $policy = Risk::fillPolicy(json_decode($row['policy'], true));
        return $this->save($admin, $policy, Ranking::fillWeights(json_decode($row['ranking'], true)),
            '되돌리기 #' . (int)$policyId . ': ' . trim((string)$reason), $ip);
    }
}
