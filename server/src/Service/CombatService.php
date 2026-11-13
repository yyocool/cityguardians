<?php
namespace CG\Service;

use CG\App;
use CG\Domain\Gear;
use CG\Domain\Invasion;
use CG\Infra\ApiError;
use CG\Infra\Clock;

/**
 * 공통 전투 시스템 (docs/COMBAT_SYSTEM.md): 전투 모드, 전투 장비, 모드별 장착, 보상 현황.
 *
 * 장비는 한 사람이 한 점씩 가진다 (player_combat_equipment). 모드별로 따로 사지 않는다.
 * 어느 모드의 어느 칸에 꼈는지만 따로 적는다 (combat_loadouts).
 * 무료 기본 장비는 사지 않아도 가진 것으로 본다. 그래서 GC 가 없어도 모든 전투에 나설 수 있다.
 */
final class CombatService
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

    // ── 가진 장비와 장착 ────────────────────────────────────

    /** @return array [장비 코드 => 강화 단계] */
    public function owned($playerId)
    {
        $owned = [];
        foreach ($this->db->all(
            'SELECT e.code, p.level FROM player_combat_equipment p JOIN combat_equipment e ON e.id = p.equipment_id WHERE p.player_id = ?',
            [$playerId]
        ) as $row) {
            $owned[$row['code']] = (int)$row['level'];
        }
        return $owned;
    }

    /** @return array ["모드:칸" => 장비 코드] */
    public function loadouts($playerId)
    {
        $loadouts = [];
        foreach ($this->db->all(
            'SELECT l.mode_code, l.slot, e.code FROM combat_loadouts l JOIN combat_equipment e ON e.id = l.equipment_id WHERE l.player_id = ?',
            [$playerId]
        ) as $row) {
            $loadouts[$row['mode_code'] . ':' . $row['slot']] = $row['code'];
        }
        return $loadouts;
    }

    public function ownedList($playerId)
    {
        $list = [];
        foreach ($this->owned($playerId) as $code => $level) {
            $list[] = ['code' => $code, 'level' => $level];
        }
        return $list;
    }

    public function loadoutList($playerId)
    {
        $list = [];
        foreach ($this->loadouts($playerId) as $key => $code) {
            list($mode, $slot) = explode(':', $key, 2);
            $list[] = ['mode' => $mode, 'slot' => $slot, 'gearCode' => $code];
        }
        return $list;
    }

    private function gearResult($playerId, $gearCode, $costGc)
    {
        $owned = $this->owned($playerId);
        return [
            'gearCode' => $gearCode,
            'level' => $gearCode !== null ? Gear::levelOf($owned, $this->app->defs()->findGear($gearCode)) : 0,
            'costGc' => $costGc,
            'gc' => $this->app->wallets()->balance($playerId),
            'gear' => $this->ownedList($playerId),
            'loadouts' => $this->loadoutList($playerId),
            'duplicate' => false,
        ];
    }

    private static function gearError($reason, $def)
    {
        switch ($reason) {
            case Gear::UNKNOWN: $code = ApiError::NOT_FOUND; break;
            case Gear::INSUFFICIENT_GC: $code = ApiError::INSUFFICIENT_GC; break;
            case Gear::LEVEL_TOO_LOW:
            case Gear::COMING_SOON: $code = ApiError::LOCKED; break;
            case Gear::ALREADY_OWNED:
            case Gear::MAX_LEVEL:
            case Gear::NO_UPGRADE: $code = ApiError::CONFLICT; break;
            case Gear::NOT_OWNED:
            case Gear::WRONG_SLOT:
            case Gear::WRONG_MODE: $code = ApiError::VALIDATION_FAILED; break;
            default: $code = ApiError::INVALID_REQUEST;
        }
        return new ApiError($code, Gear::message($reason, $def), $reason);
    }

    // ── 조회 ────────────────────────────────────────────────

    /** GET /combat/modes: 전투 모드와, 그 모드에서 지금 끼고 있는 장비. */
    public function modes(array $player)
    {
        $defs = $this->app->defs();
        $owned = $this->owned($player['id']);
        $loadouts = $this->loadouts($player['id']);
        $modes = [];
        foreach ($defs->modes() as $mode) {
            $equipped = [];
            foreach (Gear::slots() as $slot) {
                $def = Gear::equipped($defs->gear(), $owned, $loadouts, $mode['code'], $slot);
                $equipped[] = ['slot' => $slot, 'gearCode' => $def !== null ? $def['code'] : null,
                    'level' => $def !== null ? Gear::levelOf($owned, $def) : 0];
            }
            $modes[] = $mode + ['equipped' => $equipped];
        }
        return ['modes' => $modes];
    }

    /** GET /combat/equipment: 장비 목록. 가격과 성능은 서버가 정한 값이다. */
    public function equipment(array $player)
    {
        $owned = $this->owned($player['id']);
        $balance = $this->app->wallets()->balance($player['id']);
        $list = [];
        foreach ($this->app->defs()->gear() as $def) {
            $level = Gear::levelOf($owned, $def);
            $item = $def;
            unset($item['id']);
            $item['free'] = Gear::isFree($def);
            $item['owned'] = $level > 0;
            $item['level'] = $level;
            $item['levelDamage'] = Gear::damage($def, max(1, $level));
            $item['levelEffectValue'] = Gear::effectValue($def, max(1, $level));
            $item['buyBlocked'] = $level > 0 ? null : Gear::checkBuy($def, $owned, $player['level'], $balance);
            $item['upgradeCostGc'] = $level > 0 ? Gear::upgradeCost($def, $level) : 0;
            $item['upgradeBlocked'] = $level > 0 ? Gear::checkUpgrade($def, $owned, $balance) : Gear::NOT_OWNED;
            $list[] = $item;
        }
        return ['equipment' => $list, 'gc' => $balance, 'loadouts' => $this->loadoutList($player['id'])];
    }

    /** GET /combat/overview: 전투 모드, 가진 장비, 모드별 장착, 오늘의 보상 현황. */
    public function overview(array $player)
    {
        $app = $this->app;
        $policy = $app->defs()->policy();
        return [
            'modes' => $app->defs()->modes(),
            'gear' => $this->ownedList($player['id']),
            'loadouts' => $this->loadoutList($player['id']),
            'clearedFpsMissions' => $app->fps()->clearedMissions($player['id']),
            'fpsRewardsToday' => $app->daily()->get($player['id'], Daily::FPS_REWARDS),
            'fpsRewardLimit' => $app->defs()->combatConfig()['dailyFpsRewardLimit'],
            'riskReducedToday' => intdiv($app->daily()->get($player['id'], Daily::RISK_REDUCED), 100),
            'riskReduceCap' => $policy['dailyPlayerReduceCap'],
        ];
    }

    /** GET /combat/rewards: 전투로 받은 보상. 세 모드의 보상이 모두 같은 지갑으로 들어온다. */
    public function rewards(array $player)
    {
        $app = $this->app;
        $reasons = [Wallets::HUNT_REWARD => Invasion::MODE_HUNT, Wallets::BATTLE_REWARD => Invasion::MODE_DEFENSE,
            Wallets::FPS_REWARD => Invasion::MODE_FPS];
        $totals = [];
        foreach ($reasons as $mode) {
            $totals[$mode] = ['mode' => $mode, 'totalGc' => 0, 'count' => 0];
        }
        foreach ($this->db->all(
            "SELECT reason, COALESCE(SUM(delta), 0) AS total, COUNT(*) AS n FROM ledger_entries"
            . " WHERE player_id = ? AND reason IN ('hunt_reward', 'battle_reward', 'fps_reward') GROUP BY reason",
            [$player['id']]
        ) as $row) {
            $totals[$reasons[$row['reason']]] = ['mode' => $reasons[$row['reason']], 'totalGc' => (int)$row['total'], 'count' => (int)$row['n']];
        }
        $recent = [];
        foreach ($this->db->all(
            "SELECT reason, delta, balance_after, created_at FROM ledger_entries"
            . " WHERE player_id = ? AND reason IN ('hunt_reward', 'battle_reward', 'fps_reward') ORDER BY id DESC LIMIT 30",
            [$player['id']]
        ) as $row) {
            $recent[] = ['mode' => $reasons[$row['reason']], 'reason' => $row['reason'], 'gc' => (int)$row['delta'],
                'balanceAfter' => (int)$row['balance_after'], 'at' => Clock::isoFromSql($row['created_at'])];
        }
        $policy = $app->defs()->policy();
        return [
            'gc' => $app->wallets()->balance($player['id']),
            'totals' => array_values($totals),
            'recent' => $recent,
            'dailyHuntGc' => $app->daily()->get($player['id'], Daily::HUNT_GC),
            'dailyHuntGcLimit' => $app->defs()->economy()['dailyHuntGcLimit'],
            'fpsRewardsToday' => $app->daily()->get($player['id'], Daily::FPS_REWARDS),
            'fpsRewardLimit' => $app->defs()->combatConfig()['dailyFpsRewardLimit'],
            'riskReducedToday' => intdiv($app->daily()->get($player['id'], Daily::RISK_REDUCED), 100),
            'riskReduceCap' => $policy['dailyPlayerReduceCap'],
            'cityContribution' => $player['resident'] ? $app->city()->contributionOf($player['id'], $player['regionId']) : 0,
        ];
    }

    // ── 구매, 강화, 장착 ────────────────────────────────────

    /** 장비를 산다. 값은 서버의 정의에서 읽는다. 요청이 보낸 가격은 받지 않는다. */
    public function buy(array $player, $gearCode, $idemKey)
    {
        return $this->app->idempotency()->run($player['id'], $idemKey, 'gear.buy', ['gear' => (string)$gearCode],
            function () use ($player, $gearCode) {
                $player = $this->app->players()->get($player['id']);
                $def = $this->app->defs()->findGear((string)$gearCode);
                $balance = $this->app->wallets()->lock($player['id']);
                $reason = Gear::checkBuy($def, $this->owned($player['id']), $player['level'], $balance);
                if ($reason !== null) {
                    throw self::gearError($reason, $def);
                }
                $this->app->wallets()->apply($player['id'], -$def['priceGc'], Wallets::GEAR_PURCHASE, 'gear', $def['id']);
                $now = Clock::sql();
                $this->db->exec(
                    'INSERT INTO player_combat_equipment (player_id, equipment_id, level, acquired_at, updated_at) VALUES (?, ?, 1, ?, ?)',
                    [$player['id'], $def['id'], $now, $now]
                );
                return $this->gearResult($player['id'], $def['code'], $def['priceGc']);
            });
    }

    /** 장비를 한 단계 강화한다. */
    public function upgrade(array $player, $gearCode, $idemKey)
    {
        return $this->app->idempotency()->run($player['id'], $idemKey, 'gear.upgrade', ['gear' => (string)$gearCode],
            function () use ($player, $gearCode) {
                $def = $this->app->defs()->findGear((string)$gearCode);
                $balance = $this->app->wallets()->lock($player['id']);
                $owned = $this->owned($player['id']);
                $reason = Gear::checkUpgrade($def, $owned, $balance);
                if ($reason !== null) {
                    throw self::gearError($reason, $def);
                }
                $level = Gear::levelOf($owned, $def);
                $cost = Gear::upgradeCost($def, $level);
                $this->app->wallets()->apply($player['id'], -$cost, Wallets::GEAR_UPGRADE, 'gear', $def['id']);
                $now = Clock::sql();
                // 무료 기본 장비는 강화할 때 처음으로 행이 생긴다.
                $this->db->exec(
                    'INSERT INTO player_combat_equipment (player_id, equipment_id, level, acquired_at, updated_at) VALUES (?, ?, ?, ?, ?)'
                    . ' ON DUPLICATE KEY UPDATE level = VALUES(level), updated_at = VALUES(updated_at)',
                    [$player['id'], $def['id'], $level + 1, $now, $now]
                );
                $this->db->exec(
                    'INSERT INTO equipment_upgrades (player_id, equipment_id, from_level, to_level, cost_gc, created_at) VALUES (?, ?, ?, ?, ?, ?)',
                    [$player['id'], $def['id'], $level, $level + 1, $cost, $now]
                );
                return $this->gearResult($player['id'], $def['code'], $cost);
            });
    }

    /** 그 모드의 그 칸에 장비를 낀다. gearCode 가 비어 있으면 칸을 비운다 (다음부터 무료 기본 장비를 쓴다). */
    public function equip(array $player, $mode, $slot, $gearCode)
    {
        if (!in_array($mode, Gear::modes(), true) || !in_array($slot, Gear::slots(), true)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '없는 전투 모드나 장비 칸이에요.');
        }
        return $this->db->tx(function () use ($player, $mode, $slot, $gearCode) {
            $this->app->wallets()->lock($player['id']);
            if ($gearCode === null || $gearCode === '') {
                $this->db->exec('DELETE FROM combat_loadouts WHERE player_id = ? AND mode_code = ? AND slot = ?',
                    [$player['id'], $mode, $slot]);
                return $this->gearResult($player['id'], null, 0);
            }
            $def = $this->app->defs()->findGear((string)$gearCode);
            $reason = Gear::checkEquip($def, $this->owned($player['id']), $mode, $slot);
            if ($reason !== null) {
                throw self::gearError($reason, $def);
            }
            $this->db->exec(
                'INSERT INTO combat_loadouts (player_id, mode_code, slot, equipment_id, updated_at) VALUES (?, ?, ?, ?, ?)'
                . ' ON DUPLICATE KEY UPDATE equipment_id = VALUES(equipment_id), updated_at = VALUES(updated_at)',
                [$player['id'], $mode, $slot, $def['id'], Clock::sql()]
            );
            return $this->gearResult($player['id'], $def['code'], 0);
        });
    }

    // ── 전투 시작과 결과 (모드별 서비스로 넘긴다) ───────────

    /** 그 모드로 출동할 수 있는지 확인한다. */
    public function requireOpen($mode)
    {
        if ($this->app->defs()->findMode($mode) === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 전투 모드예요.');
        }
        if (!$this->app->defs()->modeOpen($mode)) {
            throw new ApiError(ApiError::LOCKED, '지금은 출동할 수 없는 전투예요.', 'mode_closed');
        }
    }

    /** POST /combat/sessions: 전투를 시작한다. 모드에 따라 맡을 서비스가 달라진다. */
    public function start(array $player, $mode, $code)
    {
        switch ($mode) {
            case Invasion::MODE_DEFENSE: $session = $this->app->defense()->start($player, $code); break;
            case Invasion::MODE_HUNT: $session = $this->app->hunt()->start($player, $code); break;
            case Invasion::MODE_FPS: $session = $this->app->fps()->start($player, $code); break;
            default: throw new ApiError(ApiError::NOT_FOUND, '없는 전투 모드예요.');
        }
        return ['mode' => $mode] + $session;
    }

    /** POST /combat/sessions/{id}/result: 전투 결과를 보고한다. 기록 번호의 머리글자로 모드를 안다. */
    public function result(array $player, $sessionId, array $body)
    {
        $kills = isset($body['kills']) && is_array($body['kills']) ? $body['kills'] : [];
        $prefix = is_string($sessionId) ? substr($sessionId, 0, 2) : '';
        if ($prefix === 'b_') {
            return ['mode' => Invasion::MODE_DEFENSE] + $this->app->defense()->finish($player, $sessionId, [
                'result' => isset($body['result']) ? $body['result'] : null,
                'kills' => $kills,
                'baseHpLeft' => isset($body['baseHpLeft']) ? $body['baseHpLeft'] : 0,
                'wavesCleared' => isset($body['wavesCleared']) ? $body['wavesCleared'] : 0,
            ]);
        }
        if ($prefix === 'f_') {
            return ['mode' => Invasion::MODE_FPS] + $this->app->fps()->finish($player, $sessionId, [
                'result' => isset($body['result']) ? $body['result'] : null,
                'kills' => $kills,
                'facilitiesSaved' => isset($body['facilitiesSaved']) ? $body['facilitiesSaved'] : 0,
                'civiliansSaved' => isset($body['civiliansSaved']) ? $body['civiliansSaved'] : 0,
            ]);
        }
        if ($prefix === 'h_') {
            return ['mode' => Invasion::MODE_HUNT]
                + $this->app->hunt()->report($player, $sessionId, isset($body['seq']) ? $body['seq'] : 0, $kills);
        }
        throw new ApiError(ApiError::NOT_FOUND, '전투 기록을 찾을 수 없어요.');
    }
}
