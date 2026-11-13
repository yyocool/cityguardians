<?php
namespace CG\Service;

use CG\App;
use CG\Domain\Hunt;
use CG\Domain\Invasion;
use CG\Domain\Risk;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 헌트 모드 (docs/HUNT_MODE.md): 3인칭 사냥.
 *
 * 클라이언트는 "무엇을 몇 마리 쓰러뜨렸다"만 보고한다. 보상 금액, 처치 속도 한도, 하루 한도는 서버가 정한다.
 * 서버가 인정한 처치가 쌓이면 소속 도시의 위험도가 조금 내려간다.
 * 아이템 드롭은 아직 서버에 옮기지 않았다 (docs/DEVELOPMENT_PLAN.md).
 */
final class HuntService
{
    /** @var App */
    private $app;
    /** @var \CG\Infra\Db */
    private $db;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->db = $app->db;
    }

    /** GET /hunt/zones: 사냥 구역과, 지금 우리 도시의 위험 단계. */
    public function zones(array $player)
    {
        $zones = [];
        foreach ($this->app->defs()->huntZones() as $def) {
            unset($def['id']);
            $zones[] = $def;
        }
        return ['zones' => $zones] + $this->cityHint($player);
    }

    /**
     * 위험 단계 이상의 도시에서는 사냥이 특별 사냥 임무가 된다: 위험도를 더 많이 낮추고, 적이 더 강하게 나온다.
     * 적을 얼마나 강하게 낼지는 클라이언트가 이 값을 보고 정한다. 보상과 위험도 반영은 서버가 정한다.
     */
    private function cityHint(array $player)
    {
        $hint = ['regionCode' => $player['regionCode'], 'stage' => null, 'special' => false, 'riskReducePercent' => 100];
        if (!$player['resident']) {
            return $hint;
        }
        $risk = $this->db->val('SELECT risk_centi FROM city_risk_status WHERE region_id = ?', [$player['regionId']]);
        if ($risk === null) {
            return $hint;
        }
        $policy = $this->app->defs()->policy();
        $hint['stage'] = Risk::stageOf((int)$risk, $policy);
        $hint['special'] = Risk::stageOrder($hint['stage']) >= Risk::stageOrder(Risk::DANGER);
        if ($hint['special']) {
            $hint['riskReducePercent'] = max(100, $policy['huntHighRiskPercent']);
        }
        return $hint;
    }

    public function start(array $player, $zoneCode)
    {
        $this->app->combat()->requireOpen(Invasion::MODE_HUNT);
        $zone = $this->app->defs()->findHuntZone((string)$zoneCode);
        if ($zone === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 사냥 구역이에요.');
        }
        $publicId = Ids::publicId('h_');
        $now = Clock::sql();
        $this->db->exec(
            'INSERT INTO hunt_sessions (public_id, player_id, zone_id, started_at, last_report_at, allowance, next_seq) VALUES (?, ?, ?, ?, ?, ?, 1)',
            [$publicId, $player['id'], $zone['id'], $now, $now, $zone['burstAllowance']]
        );
        return ['sessionId' => $publicId, 'zoneCode' => $zone['code']] + $this->cityHint($player);
    }

    /**
     * 처치를 보고한다. 순번(seq)이 있어 같은 보고가 다시 와도 두 번 지급하지 않는다.
     */
    public function report(array $player, $sessionId, $seq, $kills)
    {
        if (!Reports::sessionId($sessionId, 'h_')) {
            throw new ApiError(ApiError::NOT_FOUND, '사냥 기록을 찾을 수 없어요.');
        }
        if (!is_int($seq)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '보고의 형식이 올바르지 않아요.');
        }
        $kills = Reports::kills($kills);
        $app = $this->app;
        return $this->db->tx(function () use ($app, $player, $sessionId, $seq, $kills) {
            $app->wallets()->lock($player['id']);
            $session = $this->db->one('SELECT * FROM hunt_sessions WHERE public_id = ? AND player_id = ? FOR UPDATE',
                [$sessionId, $player['id']]);
            if ($session === null) {
                throw new ApiError(ApiError::NOT_FOUND, '사냥 기록을 찾을 수 없어요.');
            }
            $nextSeq = (int)$session['next_seq'];
            // 같은 순번의 재전송: 처음 결과를 그대로 돌려준다.
            if ($seq === $nextSeq - 1 && $session['last_response'] !== null) {
                return json_decode($session['last_response'], true);
            }
            if ($seq !== $nextSeq) {
                throw new ApiError(ApiError::CONFLICT, '보고 순서가 맞지 않아요.');
            }
            $zone = null;
            foreach ($app->defs()->huntZones() as $def) {
                if ($def['id'] === (int)$session['zone_id']) {
                    $zone = $def;
                }
            }
            if ($zone === null) {
                throw new ApiError(ApiError::CONFLICT, '지금은 열려 있지 않은 사냥 구역이에요.');
            }
            $app->combat()->requireOpen(Invasion::MODE_HUNT);
            $player = $app->players()->get($player['id']);

            $now = Clock::now();
            $elapsed = $now - Clock::parse($session['last_report_at']);
            $limit = $app->defs()->economy()['dailyHuntGcLimit'];
            $dailySoFar = $app->daily()->lock($player['id'], Daily::HUNT_GC);
            list($verdict, $allowance) = Hunt::evaluate($app->defs()->enemies(), $zone, $kills, $elapsed,
                (float)$session['allowance'], $dailySoFar, $limit);

            $acceptedCount = 0;
            foreach ($verdict['accepted'] as $kill) {
                $acceptedCount += $kill['count'];
            }

            $gc = $app->wallets()->balance($player['id']);
            $level = ['levelsGained' => 0] + $app->players()->levelFields($player['level'], $player['xp']);
            $reference = $sessionId . '#' . $seq;
            $cityEffect = null;
            if ($acceptedCount > 0) {
                if ($verdict['rewardGc'] > 0) {
                    $gc = $app->wallets()->apply($player['id'], $verdict['rewardGc'], Wallets::HUNT_REWARD, 'hunt_session',
                        (int)$session['id']);
                    $dailySoFar += $verdict['rewardGc'];
                    $app->daily()->set($player['id'], Daily::HUNT_GC, $dailySoFar);
                }
                $level = $app->players()->addXp($player['id'], $verdict['rewardXp']);
                // 사냥은 소속 도시의 위험도를 조금 낮춘다. 서버가 인정한 처치만 센다.
                $cityEffect = $app->city()->applyHunt($player, $acceptedCount, $reference);
            }

            $response = [
                'accepted' => $verdict['accepted'],
                'rewardGc' => $verdict['rewardGc'],
                'rewardXp' => $verdict['rewardXp'],
                'gc' => $gc,
                'level' => $level['level'],
                'xp' => $level['xp'],
                'xpNext' => $level['xpNext'],
                'hpMax' => $level['hpMax'],
                'levelsGained' => $level['levelsGained'],
                'dailyHuntGc' => $dailySoFar,
                'dailyHuntGcLimit' => $limit,
                'rejectReason' => $verdict['rejectReason'],
                'drops' => [],
                'inventory' => null,
                'city' => $cityEffect,
            ];
            $this->db->exec(
                'UPDATE hunt_sessions SET last_report_at = ?, allowance = ?, next_seq = next_seq + 1, kills_total = kills_total + ?,'
                . ' reward_gc_total = reward_gc_total + ?, reward_xp_total = reward_xp_total + ?, rejected_total = rejected_total + ?,'
                . ' last_response = ? WHERE id = ?',
                [Clock::sql($now), round($allowance, 4), $acceptedCount, $verdict['rewardGc'], $verdict['rewardXp'],
                    $verdict['rejectedKills'], json_encode($response, JSON_UNESCAPED_UNICODE), (int)$session['id']]
            );
            return $response;
        });
    }
}
