<?php
namespace CG\Service;

use CG\App;
use CG\Domain\Battle;
use CG\Domain\Invasion;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 디펜스 모드 (docs/DEFENSE_MODE.md): 방어 구역, 타워와 방어시설, 방어전 한 판의 시작과 결과.
 *
 * 방어시설의 소유:
 *   battle    전투 중 설치형 — 누구나 전투 에너지로 세운다. 기록할 소유가 없다.
 *   permanent 영구 보유형 — GC 로 한 번 해금한다 (player_defense_structures). 개인 자산이다.
 *   city      도시 공용 — 주민이 함께 기금을 모은다 (city_defense_structures). 개인 자산이 아니다.
 *
 * 결과 검증: 걸린 시간은 서버가 잰다. 보상 금액은 구역 정의에서 계산한다. 보고가 보낸 금액은 없다.
 */
final class DefenseService
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

    // ── 방어시설 ────────────────────────────────────────────

    /** @return array [방어시설 id => true] 이 플레이어가 영구 해금한 시설. */
    private function unlockedIds($playerId)
    {
        $ids = [];
        foreach ($this->db->all('SELECT structure_id FROM player_defense_structures WHERE player_id = ?', [$playerId]) as $row) {
            $ids[(int)$row['structure_id']] = true;
        }
        return $ids;
    }

    private static function buildable(array $def)
    {
        return !$def['comingSoon'] && $def['ownership'] !== 'city';
    }

    /** 전투에서 세울 수 있는 시설: 처음부터 쓸 수 있는 것과 GC 로 해금한 것. */
    public function usableStructures($playerId)
    {
        $unlocked = $this->unlockedIds($playerId);
        $usable = [];
        foreach ($this->app->defs()->structures() as $def) {
            // 준비 중인 시설과 도시 공용 시설은 개인이 전투에서 세우는 것이 아니다.
            if (self::buildable($def) && ($def['unlockGc'] <= 0 || isset($unlocked[$def['id']]))) {
                $usable[] = $def['code'];
            }
        }
        return $usable;
    }

    /** GET /defense/structures: 타워와 방어시설 목록. 개인 자산과 도시 공용 시설을 구별해 보여 준다. */
    public function structures(array $player)
    {
        $unlocked = $this->unlockedIds($player['id']);
        $funded = [];
        if ($player['resident']) {
            foreach ($this->db->all('SELECT structure_id, funded_gc FROM city_defense_structures WHERE region_id = ?',
                [$player['regionId']]) as $row) {
                $funded[(int)$row['structure_id']] = (int)$row['funded_gc'];
            }
        }
        $list = [];
        foreach ($this->app->defs()->structures() as $def) {
            $item = $def;
            unset($item['id']);
            $cityOwned = $def['ownership'] === 'city';
            $item['usable'] = self::buildable($def) && ($def['unlockGc'] <= 0 || isset($unlocked[$def['id']]));
            // 영구 해금한 개인 자산인가.
            $item['ownedPermanently'] = isset($unlocked[$def['id']]);
            $item['cityOwned'] = $cityOwned;
            if ($cityOwned) {
                $gc = isset($funded[$def['id']]) ? $funded[$def['id']] : 0;
                $level = Defs::cityStructureLevel($def, $gc);
                $item['cityLevel'] = $level;
                $item['cityFundedGc'] = $gc - $level * $def['cityGoalGc'];
            }
            $list[] = $item;
        }
        return ['structures' => $list, 'usable' => $this->usableStructures($player['id']),
            'gc' => $this->app->wallets()->balance($player['id'])];
    }

    /** 방어시설을 영구 해금한다 (개인 자산). */
    public function purchase(array $player, $structureCode, $idemKey)
    {
        return $this->app->idempotency()->run($player['id'], $idemKey, 'defense.unlock', ['structure' => (string)$structureCode],
            function () use ($player, $structureCode) {
                $player = $this->app->players()->get($player['id']);
                $def = $this->app->defs()->findStructure((string)$structureCode);
                if ($def === null) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 타워예요.');
                }
                if (!self::buildable($def)) {
                    throw new ApiError(ApiError::LOCKED,
                        $def['ownership'] === 'city' ? '도시가 함께 세우는 시설이에요.' : '아직 준비 중인 시설이에요.');
                }
                $this->app->wallets()->lock($player['id']);
                $unlocked = $this->unlockedIds($player['id']);
                if ($def['unlockGc'] <= 0 || isset($unlocked[$def['id']])) {
                    throw new ApiError(ApiError::CONFLICT, '이미 쓸 수 있는 타워예요.');
                }
                if ($player['level'] < $def['unlockLevel']) {
                    throw new ApiError(ApiError::LOCKED, '레벨 ' . $def['unlockLevel'] . '부터 해금할 수 있어요.', 'level_too_low');
                }
                $gc = $this->app->wallets()->apply($player['id'], -$def['unlockGc'], Wallets::TOWER_UNLOCK, 'structure', $def['id']);
                $this->db->exec(
                    'INSERT INTO player_defense_structures (player_id, structure_id, level, unlocked_at) VALUES (?, ?, 1, ?)',
                    [$player['id'], $def['id'], Clock::sql()]
                );
                return ['towerCode' => $def['code'], 'costGc' => $def['unlockGc'], 'gc' => $gc,
                    'unlockedTowers' => $this->usableStructures($player['id'])];
            });
    }

    /**
     * 방어시설 업그레이드.
     * 도시 공용 시설은 기금을 내서 단계를 올린다. 개인이 세우는 타워는 전투 안에서 전투 에너지로 올리며,
     * 전투가 끝나면 사라지므로 서버에 저장할 것이 없다.
     */
    public function upgrade(array $player, $structureCode, $amountGc, $idemKey)
    {
        $def = $this->app->defs()->findStructure((string)$structureCode);
        if ($def === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 타워예요.');
        }
        if ($def['ownership'] !== 'city') {
            throw new ApiError(ApiError::VALIDATION_FAILED,
                '이 타워는 전투 중에 전투 에너지로 강화해요. 미리 GC로 강화할 수 없어요.', 'in_battle_upgrade');
        }
        return $this->app->city()->fundStructure($player, $structureCode, $amountGc, $idemKey);
    }

    // ── 방어 구역 ───────────────────────────────────────────

    /** 한 번 이상 이긴 방어 구역. */
    public function clearedZones($playerId)
    {
        $codes = [];
        foreach ($this->db->all(
            "SELECT DISTINCT z.code FROM defense_battles b JOIN defense_zones z ON z.id = b.zone_id"
            . " WHERE b.player_id = ? AND b.result = 'win' AND b.accepted = 1 ORDER BY z.code",
            [$playerId]
        ) as $row) {
            $codes[] = $row['code'];
        }
        return $codes;
    }

    /** GET /defense/zones: 방어 구역과, 지금 그 도시의 외곽에 침공이 있는지. */
    public function zones(array $player)
    {
        $cleared = array_flip($this->clearedZones($player['id']));
        $zones = [];
        foreach ($this->app->defs()->zones() as $def) {
            $item = $def;
            unset($item['id']);
            $item['cleared'] = isset($cleared[$def['code']]);
            $item['locked'] = $player['level'] < $def['unlockLevel'];
            $item['waveCount'] = count($def['waves']);
            $item['enemyCount'] = Battle::totalEnemies($def);
            $zones[] = $item;
        }
        $regionCode = $this->app->city()->combatRegionCode($player);
        $invasion = $regionCode !== null ? $this->app->city()->countingInvasion($regionCode, Invasion::MODE_DEFENSE) : null;
        return [
            'zones' => $zones,
            'regionCode' => $regionCode,
            'invasionId' => $invasion !== null ? $invasion['id'] : null,
            'invasionKind' => $invasion !== null ? $invasion['kind'] : null,
            'invasionSeverity' => $invasion !== null ? $invasion['severity'] : 0,
            'usableStructures' => $this->usableStructures($player['id']),
        ];
    }

    // ── 방어전 ──────────────────────────────────────────────

    /** 방어전을 시작한다. 이때부터 서버가 시간을 잰다. */
    public function start(array $player, $zoneCode)
    {
        $this->app->combat()->requireOpen(Invasion::MODE_DEFENSE);
        $zone = $this->app->defs()->findZone((string)$zoneCode);
        if ($zone === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
        }
        if ($player['level'] < $zone['unlockLevel']) {
            throw new ApiError(ApiError::LOCKED, '레벨 ' . $zone['unlockLevel'] . '부터 나설 수 있어요.', 'level_too_low');
        }
        $regionCode = $this->app->city()->combatRegionCode($player);
        $invasion = $regionCode !== null ? $this->app->city()->countingInvasion($regionCode, Invasion::MODE_DEFENSE) : null;
        $publicId = Ids::publicId('b_');
        $this->db->exec(
            "INSERT INTO defense_battles (public_id, player_id, zone_id, invasion_id, status, started_at) VALUES (?, ?, ?, ?, 'active', ?)",
            [$publicId, $player['id'], $zone['id'], $invasion !== null ? $invasion['dbId'] : null, Clock::sql()]
        );
        return [
            'sessionId' => $publicId,
            'stageCode' => $zone['code'],
            'regionCode' => $regionCode,
            'invasionId' => $invasion !== null ? $invasion['id'] : null,
            'usableStructures' => $this->usableStructures($player['id']),
        ];
    }

    /**
     * 방어전 결과를 보고한다. 서버가 검증하고, 인정한 결과만 보상과 도시 위험도에 반영한다.
     * 이미 끝난 전투를 다시 보고하면 처음 응답을 그대로 돌려준다 (두 번 지급하지 않는다).
     */
    public function finish(array $player, $sessionId, array $report)
    {
        if (!Reports::sessionId($sessionId, 'b_')) {
            throw new ApiError(ApiError::NOT_FOUND, '방어전 기록을 찾을 수 없어요.');
        }
        $report = [
            'result' => Reports::result($report),
            'kills' => Reports::kills(isset($report['kills']) ? $report['kills'] : null),
            'baseHpLeft' => Reports::int($report, 'baseHpLeft'),
            'wavesCleared' => Reports::int($report, 'wavesCleared'),
        ];
        $app = $this->app;
        return $this->db->tx(function () use ($app, $player, $sessionId, $report) {
            $app->wallets()->lock($player['id']);
            $battle = $this->db->one('SELECT * FROM defense_battles WHERE public_id = ? AND player_id = ? FOR UPDATE',
                [$sessionId, $player['id']]);
            if ($battle === null) {
                throw new ApiError(ApiError::NOT_FOUND, '방어전 기록을 찾을 수 없어요.');
            }
            if ($battle['status'] !== 'active') {
                return json_decode($battle['response'], true);
            }
            $player = $app->players()->get($player['id']);
            $zone = null;
            foreach ($app->defs()->zones() as $def) {
                if ($def['id'] === (int)$battle['zone_id']) {
                    $zone = $def;
                }
            }
            if ($zone === null) {
                throw new ApiError(ApiError::CONFLICT, '지금은 열려 있지 않은 방어전이에요.');
            }

            $elapsed = Clock::now() - Clock::parse($battle['started_at']);
            $alreadyCleared = in_array($zone['code'], $this->clearedZones($player['id']), true);
            $verdict = Battle::evaluate($zone, $report, $elapsed, $alreadyCleared);
            $win = $verdict['accepted'] && $verdict['result'] === Battle::WIN;

            $gc = $app->wallets()->balance($player['id']);
            $level = ['levelsGained' => 0] + $app->players()->levelFields($player['level'], $player['xp']);
            $city = ['effect' => null, 'regionCode' => null, 'defensePoints' => 0];
            if ($verdict['accepted']) {
                if ($verdict['rewardGc'] > 0) {
                    $gc = $app->wallets()->apply($player['id'], $verdict['rewardGc'], Wallets::BATTLE_REWARD, 'defense_battle',
                        (int)$battle['id']);
                }
                if ($verdict['rewardXp'] > 0) {
                    $level = $app->players()->addXp($player['id'], $verdict['rewardXp']);
                }
                // 도시 위험도: 인정된 결과만 반영한다. 이기면 내려가고, 져도 올라가지 않는다.
                $city = $app->city()->applyCombat($player, Invasion::MODE_DEFENSE, $win, $sessionId,
                    $win ? $zone['winDefensePoints'] : 0);
            }

            $now = Clock::sql();
            $regionId = null;
            if ($city['regionCode'] !== null) {
                $regionId = $this->db->val('SELECT id FROM regions WHERE code = ?', [$city['regionCode']]);
            }
            // 먼저 결과를 적는다. 그래야 아래의 "이긴 구역" 목록에 이번 승리가 들어간다.
            $this->db->exec(
                "UPDATE defense_battles SET status = 'finished', ended_at = ?, result = ?, accepted = ?, reject_reason = ?,"
                . ' waves_cleared = ?, base_hp_left = ?, kills = ?, reward_gc = ?, reward_xp = ?, first_win = ?,'
                . ' defense_points = ?, region_id = ? WHERE id = ?',
                [$now, $verdict['accepted'] ? $verdict['result'] : null, $verdict['accepted'] ? 1 : 0, $verdict['rejectReason'],
                    $verdict['wavesCleared'], $verdict['accepted'] ? $report['baseHpLeft'] : 0, json_encode($report['kills']),
                    $verdict['rewardGc'], $verdict['rewardXp'], $verdict['firstWin'] ? 1 : 0,
                    $city['defensePoints'], $regionId, (int)$battle['id']]
            );

            $response = [
                'accepted' => $verdict['accepted'],
                'result' => $verdict['result'],
                'wavesCleared' => $verdict['wavesCleared'],
                'rewardGc' => $verdict['rewardGc'],
                'rewardXp' => $verdict['rewardXp'],
                'firstWin' => $verdict['firstWin'],
                'gc' => $gc,
                'level' => $level['level'],
                'xp' => $level['xp'],
                'xpNext' => $level['xpNext'],
                'hpMax' => $level['hpMax'],
                'levelsGained' => $level['levelsGained'],
                'rejectReason' => $verdict['rejectReason'],
                'clearedStages' => $this->clearedZones($player['id']),
                'defensePoints' => $city['defensePoints'],
                'regionCode' => $city['defensePoints'] > 0 ? $city['regionCode'] : null,
                'city' => $city['effect'],
            ];
            $this->db->exec('UPDATE defense_battles SET response = ? WHERE id = ?',
                [json_encode($response, JSON_UNESCAPED_UNICODE), (int)$battle['id']]);
            return $response;
        });
    }
}
