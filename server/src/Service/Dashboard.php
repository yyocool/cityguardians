<?php
namespace CG\Service;

use CG\App;
use CG\Domain\Risk;
use CG\Infra\Clock;

/**
 * v10.0 실시간 대시보드의 요약 (docs/LIVE_DASHBOARD.md, docs/API_SPEC.md 23장).
 *
 * 게임 화면 오른쪽의 카드 세 장(내 계좌, 우리 도시, 전국 현황)이 한 번에 받아 가는 값이다.
 *
 * - 새로 계산하는 값은 없다. 지갑, 도시 위험도, 지역 랭킹, 전입자 집계, 도시 재정이 이미 내놓는 값을 모아 줄 뿐이다.
 *   상세 화면은 저마다의 기존 API 를 그대로 쓴다.
 * - 금융 값은 언제나 요청한 사람 자신의 것이다. 다른 사람을 고르는 인자는 없다.
 * - 서버에 아직 없는 금융 기능(은행, 가상 주식, 대출)은 "없다"고 답한다. 숫자를 지어내지 않는다.
 *   개발용 샘플을 보여 줄지는 관리자 설정이 정하고, 샘플 숫자는 클라이언트가 "개발용 샘플"이라고 적어서 따로 보여 준다.
 * - 읽기만 한다. 이 요청으로 잔액, 위험도, 재정이 바뀌는 일은 없다.
 */
final class Dashboard
{
    /** 자산 변동 그래프의 날수 (오늘 포함). */
    const TREND_DAYS = 7;
    /** 전국 카드에 보내는 상위 도시의 수. */
    const TOP = 5;
    /** 전국 카드에 보내는 긴급 도시의 수. */
    const URGENT = 5;

    const LEVEL_INFO = 'info';
    const LEVEL_WARN = 'warn';
    const LEVEL_URGENT = 'urgent';

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    /** GET /dashboard/summary */
    public function summary(array $player)
    {
        $settings = $this->app->settings()->dashboard();
        $board = $this->app->city()->board($player);
        $cities = $board['risks']['cities'];
        $names = $board['names'];
        $homeCode = $player['resident'] ? $player['regionCode'] : null;

        $home = null;
        foreach ($cities as $city) {
            if ($homeCode !== null && $city['regionCode'] === $homeCode) {
                $home = $city;
            }
        }
        $invasions = [];
        foreach ($board['activeInvasions'] as $invasion) {
            $invasions[$invasion['regionCode']] = $invasion;
        }

        $myCity = $this->myCity($player, $home, $settings);
        $national = $this->national($cities, $board['rankings'], $names);
        return [
            'finance' => $this->finance($player),
            'myCity' => $myCity,
            'national' => $national,
            'notifications' => $this->notifications($myCity, $home, $cities, $invasions, $names, $settings),
            'settings' => self::clientSettings($settings),
            'updatedAt' => Clock::iso(),
        ];
    }

    /** 클라이언트에 내려가는 설정. 관리자가 정한 기본값이고, 이용자가 자기 화면에서 바꾼 것은 기기에 저장된다. */
    public static function clientSettings(array $settings)
    {
        $cards = [];
        foreach (['finance', 'city', 'national'] as $id) {
            $cards[] = ['id' => $id, 'visible' => $settings[$id . 'Visible'] === 1, 'expanded' => $settings[$id . 'Expanded'] === 1];
        }
        return [
            'refreshSeconds' => $settings['refreshSeconds'],
            'cityAlertRisk' => $settings['cityAlertRisk'],
            'nationalNotice' => $settings['nationalNotice'],
            'sampleData' => $settings['sampleData'] === 1,
            'cards' => $cards,
        ];
    }

    // ── 내 계좌 ─────────────────────────────────────────────

    private function finance(array $player)
    {
        $gc = $this->app->wallets()->balance($player['id']);
        return [
            'gc' => $gc,
            // 은행, 가상 주식, 대출은 이 서버에 아직 없다 (docs/FINANCE_DASHBOARD.md 3장). 없는 것을 0 이라고 하지 않고 "없다"고 한다.
            'bankAvailable' => false, 'bankGc' => 0, 'termGc' => 0,
            'stocksAvailable' => false, 'stockValueGc' => 0, 'stockProfitGc' => 0,
            'loansAvailable' => false, 'loanBalanceGc' => 0,
            // 순자산은 서버에 있는 자산과 부채로만 센다. 빠진 항목이 있으면 그렇다고 알린다.
            'netWorthGc' => $gc, 'netWorthPartial' => true,
            'trend' => $this->trend($player['id'], $gc),
        ];
    }

    /** 최근 며칠의 하루 끝 지갑 잔액 (한국 날짜). 원장에 적힌 잔액을 그대로 읽는다. */
    private function trend($playerId, $balance)
    {
        $now = Clock::now();
        $dates = [];
        for ($i = self::TREND_DAYS - 1; $i >= 0; $i--) {
            $dates[] = Clock::koreaDate($now - $i * 86400);
        }
        $since = Clock::sql(strtotime($dates[0] . 'T00:00:00+09:00'));
        $db = $this->app->db;
        $closing = [];
        foreach ($db->all(
            'SELECT DATE(DATE_ADD(e.created_at, INTERVAL 9 HOUR)) AS d, e.balance_after FROM ledger_entries e'
            . " JOIN (SELECT MAX(id) AS id FROM ledger_entries WHERE player_id = ? AND account = 'wallet' AND created_at >= ?"
            . ' GROUP BY DATE(DATE_ADD(created_at, INTERVAL 9 HOUR))) m ON m.id = e.id',
            [$playerId, $since]
        ) as $row) {
            $closing[$row['d']] = (int)$row['balance_after'];
        }
        $before = $db->val(
            "SELECT balance_after FROM ledger_entries WHERE player_id = ? AND account = 'wallet' AND created_at < ?"
            . ' ORDER BY created_at DESC, id DESC LIMIT 1',
            [$playerId, $since]
        );
        $carry = $before !== null ? (int)$before : 0;
        $out = [];
        foreach ($dates as $date) {
            if (isset($closing[$date])) {
                $carry = $closing[$date];
            }
            $out[] = ['date' => $date, 'gc' => $carry];
        }
        $out[count($out) - 1]['gc'] = (int)$balance;
        return $out;
    }

    // ── 우리 도시 ───────────────────────────────────────────

    private function myCity(array $player, $home, array $settings)
    {
        $out = [
            'resident' => false, 'regionCode' => null, 'name' => null, 'risk' => 0, 'stage' => Risk::SAFE, 'defensePower' => 0,
            'validResidents' => 0, 'treasuryGc' => 0,
            'invasionKind' => null, 'invasionSeverity' => 0, 'invasionProgress' => 0, 'invasionRequired' => 0, 'invasionEndsAt' => null,
            'myContribution' => 0, 'rank' => 0, 'growthStage' => 0, 'growthStageName' => null, 'modelingStatus' => Modeling::NOT_STARTED,
            'supportOpen' => false, 'alert' => false, 'alertReason' => null,
        ];
        if ($home === null) {
            return $out;
        }
        $growth = $this->app->growth()->city($home['regionCode']);
        $treasury = $this->app->treasury()->balances($player['regionId']);
        $out = [
            'resident' => true, 'regionCode' => $home['regionCode'], 'name' => $growth['name'],
            'risk' => $home['risk'], 'stage' => $home['stage'], 'defensePower' => $home['defensePower'],
            'validResidents' => $growth['validResidents'],
            // 게임 안의 도시 재정이다 (일반 + 예비). CITY PASS, 장학 사업의 예산과는 따로다.
            'treasuryGc' => array_sum($treasury),
            'invasionKind' => $home['invasionKind'], 'invasionSeverity' => $home['invasionSeverity'],
            'invasionProgress' => $home['invasionProgress'], 'invasionRequired' => $home['invasionRequired'],
            'invasionEndsAt' => $home['invasionEndsAt'],
            'myContribution' => $this->app->city()->contributionOf($player['id'], $player['regionId']),
            'rank' => $home['rank'],
            'growthStage' => $growth['stage'], 'growthStageName' => $growth['stageName'], 'modelingStatus' => $growth['modelingStatus'],
            'supportOpen' => $home['supportRequestId'] !== null,
            'alert' => false, 'alertReason' => null,
        ];
        // 경고의 까닭. 침공이 가장 급하고, 그다음이 긴급 단계, 그다음이 관리자가 정한 위험도 기준이다.
        if ($home['invasionKind'] !== null) {
            $out['alertReason'] = 'invasion';
        } elseif ($home['stage'] === Risk::EMERGENCY) {
            $out['alertReason'] = 'emergency';
        } elseif ($home['risk'] >= $settings['cityAlertRisk']) {
            $out['alertReason'] = 'risk';
        }
        $out['alert'] = $out['alertReason'] !== null;
        return $out;
    }

    // ── 전국 ────────────────────────────────────────────────

    private static function isUrgent(array $city)
    {
        return $city['stage'] === Risk::EMERGENCY || $city['invasionKind'] !== null;
    }

    private function national(array $cities, array $rankings, array $names)
    {
        $out = ['totalCities' => count($cities), 'averageSafety' => 100, 'emergencyCities' => 0, 'dangerCities' => 0,
            'invasionCities' => 0, 'supportRequests' => 0, 'homeRank' => $rankings['home'] !== null ? $rankings['home']['rank'] : 0,
            'top' => [], 'urgent' => []];
        $riskTotal = 0;
        $urgent = [];
        foreach ($cities as $city) {
            $riskTotal += $city['risk'];
            if ($city['stage'] === Risk::EMERGENCY) {
                $out['emergencyCities']++;
            } elseif ($city['stage'] === Risk::DANGER) {
                $out['dangerCities']++;
            }
            if ($city['invasionKind'] !== null) {
                $out['invasionCities']++;
            }
            if ($city['supportRequestId'] !== null) {
                $out['supportRequests']++;
            }
            if (self::isUrgent($city)) {
                $urgent[] = $city;
            }
        }
        if ($cities) {
            $out['averageSafety'] = (int)round(100 - $riskTotal / count($cities));
        }
        // 긴급 단계가 먼저, 그 안에서는 위험도가 높은 도시가 먼저.
        usort($urgent, function ($a, $b) {
            $ea = $a['stage'] === Risk::EMERGENCY ? 1 : 0;
            $eb = $b['stage'] === Risk::EMERGENCY ? 1 : 0;
            if ($ea !== $eb) {
                return $eb - $ea;
            }
            return $a['risk'] !== $b['risk'] ? $b['risk'] - $a['risk'] : strcmp($a['regionCode'], $b['regionCode']);
        });
        foreach (array_slice($urgent, 0, self::URGENT) as $city) {
            $out['urgent'][] = ['regionCode' => $city['regionCode'], 'name' => self::nameOf($names, $city['regionCode']),
                'risk' => $city['risk'], 'stage' => $city['stage'], 'invasionKind' => $city['invasionKind']];
        }
        foreach (array_slice($rankings['rows'], 0, self::TOP) as $row) {
            $out['top'][] = ['regionCode' => $row['regionCode'], 'name' => self::nameOf($names, $row['regionCode']),
                'rank' => $row['rank'], 'score' => $row['score'], 'risk' => $row['risk'], 'stage' => $row['stage']];
        }
        return $out;
    }

    private static function nameOf(array $names, $regionCode)
    {
        return isset($names[$regionCode]) ? $names[$regionCode] : $regionCode;
    }

    // ── 알림 ────────────────────────────────────────────────

    private static function invasionName($kind)
    {
        $names = ['outskirts' => '외곽 침공', 'intrusion' => '시내 침입', 'zero' => 'ZERO 이벤트'];
        return isset($names[$kind]) ? $names[$kind] : '침공';
    }

    /**
     * 카드와 전투 화면에 띄울 알림. 같은 일에는 같은 id 가 붙어서, 클라이언트가 한 번만 알린다.
     * 침공은 침공마다, 단계와 위험도 경고는 하루에 한 번이다.
     */
    private function notifications(array $myCity, $home, array $cities, array $invasions, array $names, array $settings)
    {
        $today = Clock::koreaDate();
        $out = [];
        if ($home !== null) {
            $code = $home['regionCode'];
            $name = $myCity['name'];
            if (isset($invasions[$code])) {
                $invasion = $invasions[$code];
                $out[] = ['id' => 'city_invasion:' . $invasion['id'], 'kind' => 'city_invasion', 'level' => self::LEVEL_URGENT, 'regionCode' => $code,
                    'title' => '우리 도시에 ' . self::invasionName($invasion['kind']) . '!',
                    'body' => $name . '을(를) 지켜 주세요. 막은 횟수 ' . $invasion['progress'] . ' / ' . $invasion['required']];
            }
            if ($home['stage'] === Risk::EMERGENCY) {
                $out[] = ['id' => 'city_emergency:' . $code . ':' . $today, 'kind' => 'city_emergency', 'level' => self::LEVEL_URGENT, 'regionCode' => $code,
                    'title' => '우리 도시가 긴급 단계예요', 'body' => $name . '의 위험도가 ' . $home['risk'] . '까지 올랐어요.'];
            } elseif ($myCity['alertReason'] === 'risk') {
                $out[] = ['id' => 'city_risk:' . $code . ':' . $today, 'kind' => 'city_risk', 'level' => self::LEVEL_WARN, 'regionCode' => $code,
                    'title' => '우리 도시의 위험도가 높아요', 'body' => $name . '의 위험도가 ' . $home['risk'] . '이에요.'];
            }
            if ($home['supportRequestId'] !== null) {
                $out[] = ['id' => 'city_support:' . $home['supportRequestId'], 'kind' => 'city_support', 'level' => self::LEVEL_INFO, 'regionCode' => $code,
                    'title' => '우리 도시가 지원을 요청했어요', 'body' => '이웃 도시의 가디언이 도우러 올 수 있어요.'];
            }
        }
        if ($settings['nationalNotice'] === Settings::NOTICE_NONE) {
            return $out;
        }
        $picked = [];
        foreach ($cities as $city) {
            if ($home !== null && $city['regionCode'] === $home['regionCode']) {
                continue;
            }
            $emergency = $city['stage'] === Risk::EMERGENCY;
            $invaded = $settings['nationalNotice'] === Settings::NOTICE_EMERGENCY_OR_INVASION && $city['invasionKind'] !== null;
            if ($emergency || $invaded) {
                $picked[] = $city;
            }
        }
        usort($picked, function ($a, $b) {
            return $a['risk'] !== $b['risk'] ? $b['risk'] - $a['risk'] : strcmp($a['regionCode'], $b['regionCode']);
        });
        foreach (array_slice($picked, 0, $settings['nationalNoticeMax']) as $city) {
            $code = $city['regionCode'];
            $name = self::nameOf($names, $code);
            if ($city['stage'] === Risk::EMERGENCY) {
                $out[] = ['id' => 'national_emergency:' . $code . ':' . $today, 'kind' => 'national_emergency', 'level' => self::LEVEL_WARN, 'regionCode' => $code,
                    'title' => $name . '이(가) 긴급 단계예요', 'body' => '위험도 ' . $city['risk'] . '. 전국 지도에서 볼 수 있어요.'];
            } else {
                $id = isset($invasions[$code]) ? $invasions[$code]['id'] : $code . ':' . $today;
                $out[] = ['id' => 'national_invasion:' . $id, 'kind' => 'national_invasion', 'level' => self::LEVEL_INFO, 'regionCode' => $code,
                    'title' => $name . '에 ' . self::invasionName($city['invasionKind']), 'body' => '위험도 ' . $city['risk'] . '. 전국 지도에서 볼 수 있어요.'];
            }
        }
        return $out;
    }
}
