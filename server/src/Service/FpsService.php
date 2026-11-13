<?php
namespace CG\Service;

use CG\App;
use CG\Domain\Battle;
use CG\Domain\Fps;
use CG\Domain\Invasion;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * FPS 모드 (docs/FPS_MODE.md): 시내에 들어온 로봇을 1인칭으로 막는 임무.
 *
 * PvE 뿐이다. 다른 플레이어를 상대하는 기록이나 보상은 없다.
 * 걸린 시간은 서버가 재고, 보상은 임무 정의에서 계산한다. 하루에 GC 보상을 받는 횟수에 한도가 있고,
 * 한도를 넘어도 임무에는 나설 수 있다 (경험치와 도시 기여는 그대로다).
 */
final class FpsService
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

    /** 한 번 이상 성공한 임무. */
    public function clearedMissions($playerId)
    {
        $codes = [];
        foreach ($this->db->all(
            "SELECT DISTINCT m.code FROM fps_mission_results r JOIN fps_missions m ON m.id = r.mission_id"
            . " WHERE r.player_id = ? AND r.result = 'win' AND r.accepted = 1 ORDER BY m.code",
            [$playerId]
        ) as $row) {
            $codes[] = $row['code'];
        }
        return $codes;
    }

    /** GET /fps/missions */
    public function missions(array $player)
    {
        $cleared = array_flip($this->clearedMissions($player['id']));
        $missions = [];
        foreach ($this->app->defs()->fpsMissions() as $def) {
            $item = $def;
            unset($item['id']);
            $item['cleared'] = isset($cleared[$def['code']]);
            $item['locked'] = $player['level'] < $def['unlockLevel'];
            $item['enemyCount'] = Fps::totalEnemies($def);
            $missions[] = $item;
        }
        return [
            'missions' => $missions,
            'fpsRewardsToday' => $this->app->daily()->get($player['id'], Daily::FPS_REWARDS),
            'fpsRewardLimit' => $this->app->defs()->combatConfig()['dailyFpsRewardLimit'],
        ];
    }

    /** GET /fps/invasions: 지금 출동할 수 있는 시내 침입. 내가 지키는 도시(소속 또는 지원 나간 도시)의 것이다. */
    public function invasions(array $player)
    {
        $city = $this->app->city();
        $regionCode = $city->combatRegionCode($player);
        $invasion = $regionCode !== null ? $city->countingInvasion($regionCode, Invasion::MODE_FPS) : null;
        $missions = [];
        foreach ($this->app->defs()->fpsMissions() as $def) {
            if ($player['level'] >= $def['unlockLevel']) {
                $missions[] = $def['code'];
            }
        }
        return [
            'regionCode' => $regionCode,
            'invasion' => $invasion === null ? null : [
                'id' => $invasion['id'], 'regionCode' => $invasion['regionCode'], 'kind' => $invasion['kind'],
                'severity' => $invasion['severity'], 'progress' => $invasion['progress'], 'required' => $invasion['required'],
                'endsAt' => Clock::iso($invasion['endUnix']),
            ],
            // 침입이 없어도 순찰 임무로 나설 수 있다.
            'availableMissions' => $missions,
        ];
    }

    /** 임무를 시작한다. 이때부터 서버가 시간을 잰다. */
    public function start(array $player, $missionCode)
    {
        $this->app->combat()->requireOpen(Invasion::MODE_FPS);
        $mission = $this->app->defs()->findFpsMission((string)$missionCode);
        if ($mission === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 임무예요.');
        }
        if ($player['level'] < $mission['unlockLevel']) {
            throw new ApiError(ApiError::LOCKED, '레벨 ' . $mission['unlockLevel'] . '부터 나설 수 있어요.', 'level_too_low');
        }
        $regionCode = $this->app->city()->combatRegionCode($player);
        $invasion = $regionCode !== null ? $this->app->city()->countingInvasion($regionCode, Invasion::MODE_FPS) : null;
        $publicId = Ids::publicId('f_');
        $this->db->exec(
            "INSERT INTO fps_mission_results (public_id, player_id, mission_id, invasion_id, status, started_at) VALUES (?, ?, ?, ?, 'active', ?)",
            [$publicId, $player['id'], $mission['id'], $invasion !== null ? $invasion['dbId'] : null, Clock::sql()]
        );
        return [
            'sessionId' => $publicId,
            'missionCode' => $mission['code'],
            'invasionId' => $invasion !== null ? $invasion['id'] : null,
            'regionCode' => $regionCode,
        ];
    }

    /**
     * 임무 결과를 보고한다. 이미 끝난 임무를 다시 보고하면 처음 응답을 그대로 돌려준다.
     */
    public function finish(array $player, $sessionId, array $report)
    {
        if (!Reports::sessionId($sessionId, 'f_')) {
            throw new ApiError(ApiError::NOT_FOUND, '임무 기록을 찾을 수 없어요.');
        }
        $report = [
            'result' => Reports::result($report),
            'kills' => Reports::kills(isset($report['kills']) ? $report['kills'] : null),
            'facilitiesSaved' => Reports::int($report, 'facilitiesSaved'),
            'civiliansSaved' => Reports::int($report, 'civiliansSaved'),
        ];
        $app = $this->app;
        return $this->db->tx(function () use ($app, $player, $sessionId, $report) {
            $app->wallets()->lock($player['id']);
            $row = $this->db->one('SELECT * FROM fps_mission_results WHERE public_id = ? AND player_id = ? FOR UPDATE',
                [$sessionId, $player['id']]);
            if ($row === null) {
                throw new ApiError(ApiError::NOT_FOUND, '임무 기록을 찾을 수 없어요.');
            }
            if ($row['status'] !== 'active') {
                return json_decode($row['response'], true);
            }
            $player = $app->players()->get($player['id']);
            $mission = null;
            foreach ($app->defs()->fpsMissions() as $def) {
                if ($def['id'] === (int)$row['mission_id']) {
                    $mission = $def;
                }
            }
            if ($mission === null) {
                throw new ApiError(ApiError::CONFLICT, '지금은 열려 있지 않은 임무예요.');
            }

            $limit = $app->defs()->combatConfig()['dailyFpsRewardLimit'];
            $rewardsToday = $app->daily()->lock($player['id'], Daily::FPS_REWARDS);
            $rewardAllowed = $rewardsToday < $limit;
            $elapsed = Clock::now() - Clock::parse($row['started_at']);
            $alreadyCleared = in_array($mission['code'], $this->clearedMissions($player['id']), true);
            $verdict = Fps::evaluate($mission, $report, $elapsed, $alreadyCleared, $rewardAllowed);
            $win = $verdict['accepted'] && $verdict['result'] === Battle::WIN;

            $gc = $app->wallets()->balance($player['id']);
            $level = ['levelsGained' => 0] + $app->players()->levelFields($player['level'], $player['xp']);
            $city = ['effect' => null, 'regionCode' => null, 'defensePoints' => 0];
            if ($win) {
                if ($verdict['rewardGc'] > 0) {
                    $gc = $app->wallets()->apply($player['id'], $verdict['rewardGc'], Wallets::FPS_REWARD, 'fps_mission',
                        (int)$row['id']);
                    $rewardsToday++;
                    $app->daily()->set($player['id'], Daily::FPS_REWARDS, $rewardsToday);
                }
                if ($verdict['rewardXp'] > 0) {
                    $level = $app->players()->addXp($player['id'], $verdict['rewardXp']);
                }
            }
            if ($verdict['accepted']) {
                $city = $app->city()->applyCombat($player, Invasion::MODE_FPS, $win, $sessionId,
                    $win ? $mission['winDefensePoints'] : 0);
            }

            $regionId = null;
            if ($city['regionCode'] !== null) {
                $regionId = $this->db->val('SELECT id FROM regions WHERE code = ?', [$city['regionCode']]);
            }
            $limited = $win && !$rewardAllowed;
            $this->db->exec(
                "UPDATE fps_mission_results SET status = 'finished', ended_at = ?, result = ?, accepted = ?, reject_reason = ?,"
                . ' kills = ?, facilities_saved = ?, civilians_saved = ?, reward_gc = ?, reward_xp = ?, first_win = ?,'
                . ' reward_limited = ?, defense_points = ?, region_id = ? WHERE id = ?',
                [Clock::sql(), $verdict['accepted'] ? $verdict['result'] : null, $verdict['accepted'] ? 1 : 0, $verdict['rejectReason'],
                    json_encode($report['kills']), $verdict['facilitiesSaved'], $verdict['civiliansSaved'],
                    $verdict['rewardGc'], $verdict['rewardXp'], $verdict['firstWin'] ? 1 : 0,
                    $limited ? 1 : 0, $city['defensePoints'], $regionId, (int)$row['id']]
            );

            $response = [
                'accepted' => $verdict['accepted'],
                'result' => $verdict['result'],
                'rewardGc' => $verdict['rewardGc'],
                'rewardXp' => $verdict['rewardXp'],
                'firstWin' => $verdict['firstWin'],
                'rewardLimited' => $limited,
                'gc' => $gc,
                'level' => $level['level'],
                'xp' => $level['xp'],
                'xpNext' => $level['xpNext'],
                'hpMax' => $level['hpMax'],
                'levelsGained' => $level['levelsGained'],
                'rejectReason' => $verdict['rejectReason'],
                'facilitiesSaved' => $verdict['facilitiesSaved'],
                'civiliansSaved' => $verdict['civiliansSaved'],
                'clearedFpsMissions' => $this->clearedMissions($player['id']),
                'defensePoints' => $city['defensePoints'],
                'fpsRewardsToday' => $rewardsToday,
                'fpsRewardLimit' => $limit,
                'city' => $city['effect'],
            ];
            $this->db->exec('UPDATE fps_mission_results SET response = ? WHERE id = ?',
                [json_encode($response, JSON_UNESCAPED_UNICODE), (int)$row['id']]);
            return $response;
        });
    }
}
