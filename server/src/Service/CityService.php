<?php
namespace CG\Service;

use CG\App;
use CG\Domain\Invasion;
use CG\Domain\Ranking;
use CG\Domain\Risk;
use CG\Domain\Support;
use CG\Infra\ApiError;
use CG\Infra\Clock;

/**
 * 도시 위험도, 침공, 지역 랭킹, 도시 간 지원 (docs/CITY_RISK_SYSTEM.md, docs/REGIONAL_COMPETITION.md).
 *
 * 위험도는 여기에서만 바뀐다:
 *   - 시간의 흐름 (침공 발생, 제한 시간이 끝난 침공의 판정, 하루 한 번의 NPC 순찰) — tick()
 *   - 서버가 검증한 전투 결과 — applyCombat(), applyHunt()
 *   - 서버가 검증한 시설 복구와 도시 기금 — repairFacility(), fundStructure()
 *   - 관리자의 수동 조정 (사유와 함께) — adminAdjustRisk()
 * 위험도 숫자를 인자로 받아 그대로 저장하는 공개 메서드는 없다. 바뀔 때마다 city_risk_history 에 한 줄이 남는다.
 *
 * 이 클래스는 은행·대출·신용점수를 읽지도 쓰지도 않는다. GC 는 플레이어가 스스로 고른 복구비와 기금으로만 나간다.
 *
 * 잠금 순서: 플레이어의 지갑 → 도시 기록(city_world_state 한 행). 도시를 바꾸는 처리는 모두 이 한 행을 잠그고 한다.
 */
final class CityService
{
    /** 밀린 판정을 한 번에 따라잡는 한도. 그보다 오래된 판정은 건너뛴다 (지난 일을 만들어 내지 않는다). */
    const MAX_CATCH_UP_TICKS = 72;
    const RECENT_INVASION_DAYS = 7;
    const DETAIL_HISTORY = 20;
    const DETAIL_INVASIONS = 8;
    const DEFAULT_SEED = 20261113;

    /** @var App */
    private $app;
    /** @var \CG\Infra\Db */
    private $db;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->db = $app->db;
    }

    private function lazyTick()
    {
        return $this->app->env->bool('WORLD_LAZY_TICK', true);
    }

    // ── 읽기와 잠금 ─────────────────────────────────────────

    private function initialRisk($seed, array $policy, $regionCode)
    {
        $spread = max(0, $policy['initialSpread']);
        $points = $policy['initialRisk'] + ($spread > 0 ? Invasion::roll($seed, $regionCode, 0, 9, $spread * 2 + 1) - $spread : 0);
        return Risk::clamp($points * 100);
    }

    private function load($seed, $lastTick)
    {
        $w = new World();
        $w->seed = (int)$seed;
        $w->lastTick = (int)$lastTick;
        $w->policy = $this->app->defs()->policy();

        $rows = $this->db->all(
            'SELECT r.id, r.code, r.name, r.sido_id, sd.code AS sido_code, r.defense_points, r.resident_count,'
            . ' s.region_id AS has_row, s.risk_centi, s.stabilized_date, s.player_wins, s.player_attempts,'
            . ' s.invasion_severity_total, s.repelled_severity, s.npc_held_count, s.failed_count,'
            . ' s.contribution, s.support_given, s.support_received'
            . ' FROM regions r LEFT JOIN sidos sd ON sd.id = r.sido_id'
            . ' LEFT JOIN city_risk_status s ON s.region_id = r.id ORDER BY r.id'
        );
        foreach ($rows as $row) {
            $hasRow = $row['has_row'] !== null;
            $w->cities[(int)$row['id']] = [
                'regionId' => (int)$row['id'],
                'code' => $row['code'],
                'name' => $row['name'],
                'sidoCode' => $row['sido_code'],
                'residents' => (int)$row['resident_count'],
                'defensePoints' => (int)$row['defense_points'],
                'hasRow' => $hasRow,
                // 아직 기록이 없는 도시는 처음 값으로 본다 (도시마다 조금씩 다르다).
                'riskCenti' => $hasRow ? (int)$row['risk_centi'] : $this->initialRisk($w->seed, $w->policy, $row['code']),
                'stabilizedDate' => $row['stabilized_date'],
                'playerWins' => (int)$row['player_wins'],
                'playerAttempts' => (int)$row['player_attempts'],
                'invasionSeverityTotal' => (int)$row['invasion_severity_total'],
                'repelledSeverity' => (int)$row['repelled_severity'],
                'npcHeldCount' => (int)$row['npc_held_count'],
                'failedCount' => (int)$row['failed_count'],
                'contribution' => (int)$row['contribution'],
                'supportGiven' => (int)$row['support_given'],
                'supportReceived' => (int)$row['support_received'],
            ];
        }
        foreach ($this->db->all("SELECT * FROM city_invasion_events WHERE status = 'active' ORDER BY id") as $row) {
            if (isset($w->cities[(int)$row['region_id']])) {
                $w->active[(int)$row['region_id']] = $this->invasionFromRow($row, $w->cities[(int)$row['region_id']]['code']);
            }
        }
        foreach ($this->db->all("SELECT * FROM city_support_requests WHERE status = 'open' ORDER BY id") as $row) {
            if (isset($w->cities[(int)$row['region_id']])) {
                $w->open[(int)$row['region_id']] = $this->supportFromRow($row, $w->cities[(int)$row['region_id']]['code']);
            }
        }
        foreach ($this->db->all('SELECT region_id, facility_id, damage FROM city_facility_damage') as $row) {
            $w->damage[(int)$row['region_id']][(int)$row['facility_id']] = (int)$row['damage'];
        }
        foreach ($this->db->all('SELECT region_id, structure_id, funded_gc FROM city_defense_structures') as $row) {
            $w->funded[(int)$row['region_id']][(int)$row['structure_id']] = (int)$row['funded_gc'];
        }
        return $w;
    }

    private function invasionFromRow(array $row, $regionCode)
    {
        return [
            'dbId' => (int)$row['id'],
            'id' => 'inv_' . $row['id'],
            'regionId' => (int)$row['region_id'],
            'regionCode' => $regionCode,
            'kind' => $row['kind'],
            'severity' => (int)$row['severity'],
            'status' => $row['status'],
            'source' => $row['source'],
            'startUnix' => Clock::parse($row['started_at']),
            'endUnix' => Clock::parse($row['ends_at']),
            'resolvedAt' => $row['resolved_at'],
            'required' => (int)$row['required'],
            'progress' => (int)$row['progress'],
        ];
    }

    private function supportFromRow(array $row, $regionCode)
    {
        return [
            'dbId' => (int)$row['id'],
            'id' => 'sup_' . $row['id'],
            'regionId' => (int)$row['region_id'],
            'regionCode' => $regionCode,
            'status' => $row['status'],
            'openUnix' => Clock::parse($row['opened_at']),
            'closesUnix' => Clock::parse($row['closes_at']),
            'wins' => (int)$row['wins'],
            'points' => (int)$row['points'],
        ];
    }

    /**
     * 도시 기록을 잠그고 읽는다. 거래 안에서만 부른다.
     * 이 잠금을 쥔 동안에는 다른 요청이 위험도를 바꾸지 못한다.
     */
    private function open()
    {
        if (!$this->db->inTransaction()) {
            throw new \LogicException('도시 기록은 거래 안에서만 바꿀 수 있습니다.');
        }
        $state = $this->db->one('SELECT last_tick, seed FROM city_world_state WHERE id = 1 FOR UPDATE');
        if ($state === null) {
            $this->db->exec('INSERT IGNORE INTO city_world_state (id, last_tick, seed, updated_at) VALUES (1, -1, ?, ?)',
                [self::DEFAULT_SEED, Clock::sql()]);
            $state = $this->db->one('SELECT last_tick, seed FROM city_world_state WHERE id = 1 FOR UPDATE');
        }
        $w = $this->load($state['seed'], $state['last_tick']);
        $this->ensureCities($w);
        if ($this->lazyTick()) {
            $this->advance($w);
        }
        return $w;
    }

    /** 잠그지 않고 읽는다 (조회용). 판정이 밀려 있으면 먼저 따라잡는다. */
    private function read()
    {
        if ($this->lazyTick()) {
            $last = $this->db->val('SELECT last_tick FROM city_world_state WHERE id = 1');
            if ($last === null || (int)$last < Invasion::tickOf(Clock::now(), $this->app->defs()->policy())) {
                $this->db->tx(function () {
                    $this->flush($this->open());
                });
            }
        }
        $state = $this->db->one('SELECT last_tick, seed FROM city_world_state WHERE id = 1');
        return $this->load($state ? $state['seed'] : self::DEFAULT_SEED, $state ? $state['last_tick'] : -1);
    }

    /** 지역 목록의 모든 도시에 기록을 만든다. */
    private function ensureCities(World $w)
    {
        foreach ($w->cities as $regionId => $city) {
            if ($city['hasRow']) {
                continue;
            }
            $this->db->exec('INSERT IGNORE INTO city_risk_status (region_id, risk_centi, updated_at) VALUES (?, ?, ?)',
                [$regionId, $city['riskCenti'], Clock::sql()]);
            $w->cities[$regionId]['hasRow'] = true;
        }
    }

    /** 바뀐 도시를 저장한다. */
    private function flush(World $w)
    {
        foreach (array_keys($w->dirty) as $regionId) {
            $city = $w->cities[$regionId];
            $this->db->exec(
                'UPDATE city_risk_status SET risk_centi = ?, stabilized_date = ?, player_wins = ?, player_attempts = ?,'
                . ' invasion_severity_total = ?, repelled_severity = ?, npc_held_count = ?, failed_count = ?,'
                . ' contribution = ?, support_given = ?, support_received = ?, updated_at = ? WHERE region_id = ?',
                [$city['riskCenti'], $city['stabilizedDate'], $city['playerWins'], $city['playerAttempts'],
                    $city['invasionSeverityTotal'], $city['repelledSeverity'], $city['npcHeldCount'], $city['failedCount'],
                    $city['contribution'], $city['supportGiven'], $city['supportReceived'], Clock::sql(), $regionId]
            );
        }
        $w->dirty = [];
        $w->scores = null;
        $w->ordered = null;
    }

    // ── 계산 ────────────────────────────────────────────────

    /** 그 시각을 기준으로 한 도시별 활성 가디언 수: 최근에 전투에 참여했거나 전입한 주민. */
    private function activeMap(World $w, $atUnix)
    {
        if (!isset($w->activeCache[$atUnix])) {
            $since = Clock::sql($atUnix - max(1, $w->policy['activeDays']) * 86400);
            $map = [];
            foreach ($this->db->all(
                'SELECT p.region_id, COUNT(*) AS n FROM players p JOIN player_combat_state s ON s.player_id = p.id'
                . " WHERE p.residency_status = 'resident' AND p.status = 'active' AND p.region_id IS NOT NULL"
                . ' AND s.last_active_at >= ? GROUP BY p.region_id',
                [$since]
            ) as $row) {
                $map[(int)$row['region_id']] = (int)$row['n'];
            }
            $w->activeCache = [$atUnix => $map];   // 가장 최근 것 하나만 둔다
        }
        return $w->activeCache[$atUnix];
    }

    private function activePlayers(World $w, $regionId, $atUnix)
    {
        $map = $this->activeMap($w, $atUnix);
        return isset($map[$regionId]) ? $map[$regionId] : 0;
    }

    private function structurePower(World $w, $regionId)
    {
        if (empty($w->funded[$regionId])) {
            return 0;
        }
        $power = 0;
        foreach ($this->app->defs()->structures() as $def) {
            if ($def['ownership'] === 'city' && isset($w->funded[$regionId][$def['id']])) {
                $power += Defs::cityStructureLevel($def, $w->funded[$regionId][$def['id']]) * $def['cityPower'];
            }
        }
        return $power;
    }

    private function defensePower(World $w, $regionId)
    {
        return Risk::defensePower($w->policy, $w->cities[$regionId]['defensePoints'], $this->structurePower($w, $regionId));
    }

    /** 위험도를 바꾸고 이력에 남긴다. 실제로 바뀌지 않았으면 남기지 않는다. */
    private function changeRisk(World $w, $regionId, array $change, $actor, $reference, $atUnix, $playerId = null, $adminId = null)
    {
        $w->cities[$regionId]['riskCenti'] = $change['after'];
        $w->dirty[$regionId] = true;
        if (!$change['changed']) {
            return;
        }
        $this->db->exec(
            'INSERT INTO city_risk_history (region_id, at, reason, actor, delta_centi, after_centi, stage, reference, player_id, admin_id)'
            . ' VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
            [$regionId, Clock::sql($atUnix), $change['reason'], $actor, $change['delta'], $change['after'], $change['stageAfter'],
                $reference !== null ? mb_substr((string)$reference, 0, 100) : null, $playerId, $adminId]
        );
    }

    /**
     * 지원 요청을 열거나 닫는다. 긴급 단계가 되면 열리고, 위험도가 충분히 내려가면 해결로 닫힌다.
     * 아무 도움 없이 시간이 다 되면 "미해결 긴급 임무"로 위험도가 오른다.
     */
    private function updateSupport(World $w, $regionId, $nowUnix)
    {
        $policy = $w->policy;
        $open = isset($w->open[$regionId]) ? $w->open[$regionId] : null;
        if ($open !== null) {
            $status = null;
            if (Support::isResolved($policy, $w->cities[$regionId]['riskCenti'])) {
                $status = Support::RESOLVED;
            } elseif ($open['closesUnix'] <= $nowUnix) {
                $status = Support::EXPIRED;
            }
            if ($status !== null) {
                $this->db->exec('UPDATE city_support_requests SET status = ?, closed_at = ? WHERE id = ?',
                    [$status, Clock::sql($nowUnix), $open['dbId']]);
                unset($w->open[$regionId]);
                if ($status === Support::EXPIRED && $open['wins'] <= 0) {
                    $this->changeRisk($w, $regionId,
                        Risk::applySystem($w->cities[$regionId]['riskCenti'], Risk::EMERGENCY_UNRESOLVED, 1, $policy),
                        Risk::ACTOR_SYSTEM, $open['id'], $nowUnix);
                }
                $open = null;
            }
        }
        if (!Support::shouldOpen($policy, $w->cities[$regionId]['riskCenti'], $open !== null)) {
            return;
        }
        $closes = $nowUnix + max(1, $policy['supportMinutes']) * 60;
        $id = $this->db->insert(
            "INSERT INTO city_support_requests (region_id, status, opened_at, closes_at) VALUES (?, 'open', ?, ?)",
            [$regionId, Clock::sql($nowUnix), Clock::sql($closes)]
        );
        $w->open[$regionId] = [
            'dbId' => $id, 'id' => 'sup_' . $id, 'regionId' => $regionId, 'regionCode' => $w->cities[$regionId]['code'],
            'status' => Support::OPEN, 'openUnix' => $nowUnix, 'closesUnix' => $closes, 'wins' => 0, 'points' => 0,
        ];
    }

    // ── 시간의 흐름 ─────────────────────────────────────────

    /**
     * 지금 시각까지의 침공 판정을 처리한다. 예약 작업(bin/tick.php)이 정해진 간격으로 부른다.
     * WORLD_LAZY_TICK 이 켜져 있으면 도시 기록을 쓰는 요청이 올 때에도 밀린 만큼 따라잡는다.
     * @return int 처리한 판정의 수.
     */
    public function tick()
    {
        return $this->db->tx(function () {
            $state = $this->db->one('SELECT last_tick, seed FROM city_world_state WHERE id = 1 FOR UPDATE');
            if ($state === null) {
                $this->db->exec('INSERT IGNORE INTO city_world_state (id, last_tick, seed, updated_at) VALUES (1, -1, ?, ?)',
                    [self::DEFAULT_SEED, Clock::sql()]);
                $state = $this->db->one('SELECT last_tick, seed FROM city_world_state WHERE id = 1 FOR UPDATE');
            }
            $w = $this->load($state['seed'], $state['last_tick']);
            $this->ensureCities($w);
            $count = $this->advance($w);
            $this->flush($w);
            return $count;
        });
    }

    private function advance(World $w)
    {
        $nowTick = Invasion::tickOf(Clock::now(), $w->policy);
        if ($nowTick < 0 || ($w->lastTick >= 0 && $nowTick <= $w->lastTick)) {
            return 0;
        }
        // 처음에는 지금 순번만 처리한다. 지난 일은 만들어 내지 않는다.
        $from = $w->lastTick < 0 ? $nowTick : max($w->lastTick + 1, $nowTick - self::MAX_CATCH_UP_TICKS + 1);
        for ($tick = $from; $tick <= $nowTick; $tick++) {
            $this->processTick($w, $tick);
        }
        $w->lastTick = $nowTick;
        $this->db->exec('UPDATE city_world_state SET last_tick = ?, updated_at = ? WHERE id = 1', [$nowTick, Clock::sql()]);
        $this->flush($w);
        return $nowTick - $from + 1;
    }

    private function processTick(World $w, $tick)
    {
        $policy = $w->policy;
        $tickStart = Invasion::tickStart($tick, $policy);
        $date = Clock::koreaDate($tickStart);

        foreach (array_keys($w->cities) as $regionId) {
            $active = isset($w->active[$regionId]) ? $w->active[$regionId] : null;

            // 1) 제한 시간이 끝난 침공: 플레이어가 막지 못했으므로 NPC 수비대가 판정한다.
            if ($active !== null && $active['endUnix'] <= $tickStart) {
                $this->resolveExpired($w, $regionId, $active, $tick, $tickStart);
                $active = null;
            }

            // 2) 하루 한 번, NPC 순찰대가 위험도를 조금 낮춘다.
            if ($w->cities[$regionId]['stabilizedDate'] !== $date) {
                if (!empty($w->cities[$regionId]['stabilizedDate'])) {
                    $delta = Risk::dailyStabilizeDelta($w->cities[$regionId]['riskCenti'], $policy);
                    if ($delta !== 0) {
                        $this->changeRisk($w, $regionId,
                            Risk::applyExact($w->cities[$regionId]['riskCenti'], Risk::DAILY_STABILIZE, $delta, $policy),
                            Risk::ACTOR_NPC, null, $tickStart);
                    }
                }
                $w->cities[$regionId]['stabilizedDate'] = $date;
                $w->dirty[$regionId] = true;
            }

            // 3) 새 침공. 한 도시에 침공은 한 번에 하나다.
            if ($active === null) {
                $started = Invasion::tryStart($policy, $w->cities[$regionId]['code'], $tick, $w->seed);
                if ($started !== null) {
                    $required = Invasion::requiredWins($policy, $started['severity'], $this->activePlayers($w, $regionId, $tickStart));
                    $this->startInvasion($w, $regionId, $tick, $started['kind'], $started['severity'], $required,
                        $started['startUnix'], $started['endUnix'], 'system', Risk::ACTOR_SYSTEM, null);
                }
            }

            // 4) 지원 요청을 열거나 닫는다.
            $this->updateSupport($w, $regionId, $tickStart);
        }
    }

    /** 침공을 기록하고 위험도를 올린다. 같은 (도시, 순번)이 이미 있으면 아무것도 하지 않는다. */
    private function startInvasion(World $w, $regionId, $tick, $kind, $severity, $required, $startUnix, $endUnix, $source,
        $actor, $adminId)
    {
        $id = $this->db->insert(
            'INSERT IGNORE INTO city_invasion_events (region_id, tick, kind, severity, status, source, started_at, ends_at, required, progress)'
            . " VALUES (?, ?, ?, ?, 'active', ?, ?, ?, ?, 0)",
            [$regionId, $tick, $kind, $severity, $source, Clock::sql($startUnix), Clock::sql($endUnix), $required]
        );
        if ($id <= 0) {
            return null;
        }
        $invasion = [
            'dbId' => $id, 'id' => 'inv_' . $id, 'regionId' => $regionId, 'regionCode' => $w->cities[$regionId]['code'],
            'kind' => $kind, 'severity' => $severity, 'status' => Invasion::ACTIVE, 'source' => $source,
            'startUnix' => $startUnix, 'endUnix' => $endUnix, 'resolvedAt' => null, 'required' => $required, 'progress' => 0,
        ];
        $w->active[$regionId] = $invasion;
        $w->recent = null;
        $quantity = $kind === Invasion::ZERO ? 1 : $severity;
        $this->changeRisk($w, $regionId,
            Risk::applySystem($w->cities[$regionId]['riskCenti'], Invasion::startReason($kind), $quantity, $w->policy),
            $actor, $invasion['id'], $startUnix, null, $adminId);
        return $invasion;
    }

    private function closeInvasion(World $w, $regionId, array $invasion, $status, $atUnix)
    {
        $this->db->exec('UPDATE city_invasion_events SET status = ?, resolved_at = ?, progress = ? WHERE id = ?',
            [$status, Clock::sql($atUnix), $invasion['progress'], $invasion['dbId']]);
        unset($w->active[$regionId]);
        $w->cities[$regionId]['invasionSeverityTotal'] += $invasion['severity'];
        $w->dirty[$regionId] = true;
    }

    private function resolveExpired(World $w, $regionId, array $invasion, $tick, $atUnix)
    {
        $policy = $w->policy;
        $status = Invasion::resolveExpired($policy, $invasion, $this->defensePower($w, $regionId), $w->seed, $tick);
        $this->closeInvasion($w, $regionId, $invasion, $status, $atUnix);

        if ($status === Invasion::NPC_HELD) {
            // NPC가 막은 것은 플레이어의 기여로 세지 않는다.
            $w->cities[$regionId]['npcHeldCount']++;
            $this->changeRisk($w, $regionId,
                Risk::applySystem($w->cities[$regionId]['riskCenti'], Risk::NPC_HELD, $invasion['severity'], $policy),
                Risk::ACTOR_NPC, $invasion['id'], $atUnix);
            return;
        }

        $w->cities[$regionId]['failedCount']++;
        $this->changeRisk($w, $regionId,
            Risk::applySystem($w->cities[$regionId]['riskCenti'], Invasion::failReason($invasion['kind']), $invasion['severity'], $policy),
            Risk::ACTOR_SYSTEM, $invasion['id'], $atUnix);

        // 시내까지 들어온 침공을 막지 못하면 주요 시설 한 곳이 피해를 입는다.
        $facilities = $this->app->defs()->facilities();
        if ($invasion['kind'] === Invasion::OUTSKIRTS || !$facilities) {
            return;
        }
        $def = $facilities[Invasion::roll($w->seed, $w->cities[$regionId]['code'], $tick, 5, count($facilities))];
        $damage = isset($w->damage[$regionId][$def['id']]) ? $w->damage[$regionId][$def['id']] : 0;
        if ($damage >= $def['maxDamage']) {
            return;
        }
        $this->setDamage($w, $regionId, $def['id'], $damage + 1);
        $this->changeRisk($w, $regionId,
            Risk::applySystem($w->cities[$regionId]['riskCenti'], Risk::FACILITY_DAMAGED, 1, $policy),
            Risk::ACTOR_SYSTEM, $invasion['id'] . ':' . $def['code'], $atUnix);
    }

    private function setDamage(World $w, $regionId, $facilityId, $damage)
    {
        $this->db->exec(
            'INSERT INTO city_facility_damage (region_id, facility_id, damage, updated_at) VALUES (?, ?, ?, ?)'
            . ' ON DUPLICATE KEY UPDATE damage = VALUES(damage), updated_at = VALUES(updated_at)',
            [$regionId, $facilityId, $damage, Clock::sql()]
        );
        $w->damage[$regionId][$facilityId] = $damage;
    }

    // ── 전투 결과를 도시에 반영 ─────────────────────────────

    /** 지금 지원 나가 있는 요청. 요청이 닫혔으면 null. */
    public function openSupportOf($playerId)
    {
        $row = $this->db->one(
            'SELECT q.*, r.code AS region_code FROM player_combat_state s'
            . ' JOIN city_support_requests q ON q.id = s.support_request_id'
            . ' JOIN regions r ON r.id = q.region_id'
            . " WHERE s.player_id = ? AND q.status = 'open'",
            [$playerId]
        );
        return $row !== null ? $this->supportFromRow($row, $row['region_code']) : null;
    }

    /** 전투가 반영될 도시의 코드: 지원 나가 있으면 그 도시, 아니면 소속 도시. 이방인이면 null. */
    public function combatRegionCode(array $player)
    {
        if (!$player['resident']) {
            return null;
        }
        $support = $this->openSupportOf($player['id']);
        return $support !== null ? $support['regionCode'] : $player['regionCode'];
    }

    /** 그 도시에서 그 전투 모드의 승리가 막는 데 쓰이는 침공. 없으면 null. */
    public function countingInvasion($regionCode, $mode)
    {
        $w = $this->read();
        $city = $w->byCode($regionCode);
        if ($city === null || !isset($w->active[$city['regionId']])) {
            return null;
        }
        $invasion = $w->active[$city['regionId']];
        return Invasion::counts($invasion, $mode) ? $invasion : null;
    }

    private function effect(World $w, $regionId, $actor, $reason, $before, $stageBefore, $points, $reducedToday)
    {
        $after = $w->cities[$regionId]['riskCenti'];
        return [
            'regionCode' => $w->cities[$regionId]['code'],
            'actor' => $actor,
            'reason' => $reason,
            'riskBefore' => Risk::toPoints($before),
            'riskAfter' => Risk::toPoints($after),
            'riskDeltaCenti' => $after - $before,
            'stageBefore' => $stageBefore,
            'stage' => Risk::stageOf($after, $w->policy),
            'contribution' => $points,
            'invasionId' => null,
            'invasionKind' => null,
            'invasionProgress' => 0,
            'invasionRequired' => 0,
            'invasionRepelled' => false,
            'capped' => $reducedToday >= $w->policy['dailyPlayerReduceCap'] * 100,
        ];
    }

    private function addContribution($regionId, $playerId, $points)
    {
        if ($points <= 0) {
            return;
        }
        $this->db->exec(
            'INSERT INTO region_contributions (region_id, player_id, points, updated_at) VALUES (?, ?, ?, ?)'
            . ' ON DUPLICATE KEY UPDATE points = points + VALUES(points), updated_at = VALUES(updated_at)',
            [$regionId, $playerId, $points, Clock::sql()]
        );
    }

    public function contributionOf($playerId, $regionId)
    {
        return (int)$this->db->val('SELECT points FROM region_contributions WHERE region_id = ? AND player_id = ?',
            [$regionId, $playerId]);
    }

    /**
     * 방어전·FPS 결과를 도시에 반영한다. 서버가 검증한 결과만 넘어온다.
     * 이기면 위험도가 내려가고 기여도가 쌓인다. 져도 위험도는 올라가지 않는다 (시도 횟수만 센다).
     * 이방인은 반영할 도시가 없다.
     *
     * @return array effect(도시 반영 내용 또는 null), regionCode(반영된 도시 또는 null), defensePoints(쌓인 방어 포인트)
     */
    public function applyCombat(array $player, $mode, $win, $reference, $winDefensePoints)
    {
        $none = ['effect' => null, 'regionCode' => null, 'defensePoints' => 0];
        if (!$player['resident']) {
            return $none;
        }
        $players = $this->app->players();
        $players->touchActive($player['id']);
        $w = $this->open();
        $now = Clock::now();

        $support = $this->openSupportOf($player['id']);
        if ($support !== null && !isset($w->open[$support['regionId']])) {
            $support = null;
        }
        if ($support === null) {
            $players->setSupportRequest($player['id'], null);
        } else {
            $support = $w->open[$support['regionId']];
        }
        $target = $support !== null ? $support['regionId'] : $player['regionId'];
        if (!isset($w->cities[$target])) {
            $this->flush($w);
            return $none;
        }

        $w->cities[$target]['playerAttempts']++;
        $w->dirty[$target] = true;
        if (!$win) {
            $this->flush($w);
            return $none;
        }
        $w->cities[$target]['playerWins']++;

        // 주민이 이기면 그 도시에 방어 포인트가 쌓인다.
        $defensePoints = max(0, (int)$winDefensePoints);
        if ($defensePoints > 0) {
            $this->db->exec('UPDATE regions SET defense_points = defense_points + ? WHERE id = ?', [$defensePoints, $target]);
            $w->cities[$target]['defensePoints'] += $defensePoints;
        }

        $policy = $w->policy;
        $actor = $support !== null ? Risk::ACTOR_SUPPORTER : Risk::ACTOR_PLAYER;
        $invasion = isset($w->active[$target]) ? $w->active[$target] : null;
        $counts = Invasion::counts($invasion, $mode);
        $reason = Invasion::winReason($mode);

        $before = $w->cities[$target]['riskCenti'];
        $stageBefore = Risk::stageOf($before, $policy);
        $daily = $this->app->daily();
        $reduced = $daily->lock($player['id'], Daily::RISK_REDUCED);
        list($change, $reducedToday) = Risk::applyPlayer($before, $reason, $counts ? 100 : $policy['patrolPercent'], $policy,
            $this->activePlayers($w, $target, $now), $reduced);
        $daily->set($player['id'], Daily::RISK_REDUCED, $reducedToday);
        $this->changeRisk($w, $target, $change, $actor, $reference, $now, $player['id']);

        $points = $mode === Invasion::MODE_FPS ? $policy['contribFpsWin'] : $policy['contribDefenseWin'];
        if ($counts) {
            $points += intdiv($points * $policy['contribSeverityBonusPercent'] * $invasion['severity'], 100);
        }

        $extra = [];
        if ($counts) {
            $invasion['progress']++;
            $extra = [
                'invasionId' => $invasion['id'], 'invasionKind' => $invasion['kind'],
                'invasionRequired' => $invasion['required'], 'invasionProgress' => $invasion['progress'],
            ];
            if ($invasion['progress'] >= $invasion['required']) {
                // 플레이어들이 끝까지 막아 냈다. 이 몫은 침공 하나에 한 번이라 사람 수로 나누지 않는다.
                $this->closeInvasion($w, $target, $invasion, Invasion::REPELLED, $now);
                $w->cities[$target]['repelledSeverity'] += $invasion['severity'];
                $quantity = $invasion['kind'] === Invasion::ZERO ? 1 : $invasion['severity'];
                $this->changeRisk($w, $target,
                    Risk::applySystem($w->cities[$target]['riskCenti'], Invasion::repelReason($invasion['kind']), $quantity, $policy),
                    $actor, $invasion['id'], $now, $player['id']);
                $extra['invasionRepelled'] = true;
            } else {
                $this->db->exec('UPDATE city_invasion_events SET progress = ? WHERE id = ?', [$invasion['progress'], $invasion['dbId']]);
                $w->active[$target] = $invasion;
            }
        }

        if ($support !== null) {
            // 지원 나간 도시: 소속 도시는 그대로이고, 기여는 지원 기여도로 따로 쌓인다.
            $this->db->exec('UPDATE city_support_requests SET wins = wins + 1, points = points + ? WHERE id = ?',
                [$points, $support['dbId']]);
            $w->open[$target]['wins']++;
            $w->open[$target]['points'] += $points;
            $this->db->exec(
                'INSERT INTO city_support_contributions (request_id, player_id, home_region_id, wins, points, joined_at, updated_at)'
                . ' VALUES (?, ?, ?, 1, ?, ?, ?)'
                . ' ON DUPLICATE KEY UPDATE wins = wins + 1, points = points + VALUES(points), updated_at = VALUES(updated_at)',
                [$support['dbId'], $player['id'], $player['regionId'], $points, Clock::sql($now), Clock::sql($now)]
            );
            $w->cities[$target]['supportReceived'] += $points;
            if (isset($w->cities[$player['regionId']])) {
                $w->cities[$player['regionId']]['supportGiven'] += $points;
                $w->dirty[$player['regionId']] = true;
            }
        } else {
            $w->cities[$target]['contribution'] += $points;
            $this->addContribution($target, $player['id'], $points);
        }

        $this->updateSupport($w, $target, $now);
        $effect = $extra + $this->effect($w, $target, $actor, $reason, $before, $stageBefore, $points, $reducedToday);
        $this->flush($w);
        return ['effect' => $effect, 'regionCode' => $w->cities[$target]['code'], 'defensePoints' => $defensePoints];
    }

    /**
     * 사냥을 소속 도시에 반영한다. 서버가 인정한 처치가 일정 수 모일 때마다 위험도가 조금 내려간다.
     * 사냥은 우리 도시의 거리에서 하는 일이라, 지원 나가 있어도 소속 도시에 반영된다.
     * @return array|null 도시 반영 내용.
     */
    public function applyHunt(array $player, $acceptedKills, $reference)
    {
        if (!$player['resident'] || $acceptedKills <= 0) {
            return null;
        }
        $players = $this->app->players();
        $policy = $this->app->defs()->policy();
        $players->touchActive($player['id']);
        $state = $players->combatState($player['id'], true);
        $carry = $state['huntKillCarry'] + $acceptedKills;
        $perStep = max(1, $policy['huntKillsPerStep']);
        $steps = intdiv($carry, $perStep);
        $players->setHuntCarry($player['id'], $carry - $steps * $perStep);
        if ($steps <= 0) {
            return null;
        }

        $w = $this->open();
        $policy = $w->policy;
        $now = Clock::now();
        $regionId = $player['regionId'];
        if (!isset($w->cities[$regionId])) {
            $this->flush($w);
            return null;
        }

        $before = $w->cities[$regionId]['riskCenti'];
        $stageBefore = Risk::stageOf($before, $policy);
        // 위험 단계 이상의 도시에서는 사냥이 특별 사냥 임무로 쳐진다.
        $units = $steps * 100;
        if (Risk::stageOrder($stageBefore) >= Risk::stageOrder(Risk::DANGER)) {
            $units = intdiv($units * max(100, $policy['huntHighRiskPercent']), 100);
        }
        $daily = $this->app->daily();
        $reduced = $daily->lock($player['id'], Daily::RISK_REDUCED);
        list($change, $reducedToday) = Risk::applyPlayer($before, Risk::HUNT_COMPLETED, $units, $policy,
            $this->activePlayers($w, $regionId, $now), $reduced);
        $daily->set($player['id'], Daily::RISK_REDUCED, $reducedToday);
        $this->changeRisk($w, $regionId, $change, Risk::ACTOR_PLAYER, $reference, $now, $player['id']);

        $points = $policy['contribHuntStep'] * $steps;
        $w->cities[$regionId]['contribution'] += $points;
        $w->dirty[$regionId] = true;
        $this->addContribution($regionId, $player['id'], $points);
        $this->updateSupport($w, $regionId, $now);
        $effect = $this->effect($w, $regionId, Risk::ACTOR_PLAYER, Risk::HUNT_COMPLETED, $before, $stageBefore, $points, $reducedToday);
        $this->flush($w);
        return $effect;
    }

    // ── 도시를 위해 GC를 쓰는 일 (플레이어가 스스로 고른 것만) ──

    /** 시설 복구나 도시 기금을 소속 도시에 반영한다. reason 이 없으면 기여도만 쌓인다. */
    private function applyCityAction(World $w, array $player, $reason, $points, $reference)
    {
        $regionId = $player['regionId'];
        $now = Clock::now();
        $before = $w->cities[$regionId]['riskCenti'];
        $stageBefore = Risk::stageOf($before, $w->policy);
        $daily = $this->app->daily();
        $reducedToday = $daily->lock($player['id'], Daily::RISK_REDUCED);
        if ($reason !== null) {
            list($change, $reducedToday) = Risk::applyPlayer($before, $reason, 100, $w->policy,
                $this->activePlayers($w, $regionId, $now), $reducedToday);
            $daily->set($player['id'], Daily::RISK_REDUCED, $reducedToday);
            $this->changeRisk($w, $regionId, $change, Risk::ACTOR_PLAYER, $reference, $now, $player['id']);
        }
        $w->cities[$regionId]['contribution'] += $points;
        $w->dirty[$regionId] = true;
        $this->addContribution($regionId, $player['id'], $points);
        $this->updateSupport($w, $regionId, $now);
        return $this->effect($w, $regionId, Risk::ACTOR_PLAYER, $reason, $before, $stageBefore, $points, $reducedToday);
    }

    /** 피해를 입은 주요 시설을 한 단계 고친다. 값은 서버가 정한다. */
    public function repairFacility(array $player, $facilityCode, $idemKey)
    {
        return $this->app->idempotency()->run($player['id'], $idemKey, 'city.repair', ['facility' => (string)$facilityCode],
            function () use ($player, $facilityCode) {
                $player = $this->app->players()->get($player['id']);
                if (!$player['resident']) {
                    throw new ApiError(ApiError::LOCKED, '전입신고를 한 가디언만 도시의 시설을 고칠 수 있어요.');
                }
                $def = $this->app->defs()->findFacility((string)$facilityCode);
                if ($def === null) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 시설이에요.');
                }
                $w = $this->open();
                $regionId = $player['regionId'];
                if (!isset($w->cities[$regionId])) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
                }
                $damage = isset($w->damage[$regionId][$def['id']]) ? $w->damage[$regionId][$def['id']] : 0;
                if ($damage <= 0) {
                    throw new ApiError(ApiError::CONFLICT, '고칠 곳이 없어요.');
                }
                $daily = $this->app->daily();
                if ($daily->lock($player['id'], Daily::REPAIRS) >= $w->policy['dailyRepairLimit']) {
                    throw new ApiError(ApiError::LIMIT_REACHED, '오늘은 더 고칠 수 없어요. 내일 다시 도와주세요.');
                }
                $gc = $this->app->wallets()->apply($player['id'], -$def['repairGc'], Wallets::CITY_REPAIR, 'facility', $def['id']);
                $this->setDamage($w, $regionId, $def['id'], $damage - 1);
                $daily->add($player['id'], Daily::REPAIRS, 1);
                $this->app->players()->touchActive($player['id']);
                $effect = $this->applyCityAction($w, $player, Risk::FACILITY_REPAIRED, $w->policy['contribRepair'],
                    'repair:' . $def['code']);
                $this->flush($w);
                return [
                    'costGc' => $def['repairGc'], 'gc' => $gc, 'contribution' => $effect['contribution'], 'city' => $effect,
                    'detail' => $this->detailFrom($w, $player, $regionId),
                ];
            });
    }

    /** 도시 공용 방어시설의 기금에 낸다. 개인 자산이 되지 않는다. */
    public function fundStructure(array $player, $structureCode, $amountGc, $idemKey)
    {
        return $this->app->idempotency()->run($player['id'], $idemKey, 'city.fund',
            ['structure' => (string)$structureCode, 'amount' => (int)$amountGc],
            function () use ($player, $structureCode, $amountGc) {
                $player = $this->app->players()->get($player['id']);
                if (!$player['resident']) {
                    throw new ApiError(ApiError::LOCKED, '전입신고를 한 가디언만 도시 기금에 낼 수 있어요.');
                }
                $def = $this->app->defs()->findStructure((string)$structureCode);
                if ($def === null || $def['ownership'] !== 'city' || $def['comingSoon'] || $def['cityGoalGc'] <= 0) {
                    throw new ApiError(ApiError::NOT_FOUND, '도시가 함께 세우는 시설이 아니에요.');
                }
                // 100 GC 단위로만 낸다. 금액은 플레이어가 고르지만 범위는 서버가 검사한다.
                if (!is_int($amountGc) || $amountGc < 100 || $amountGc % 100 !== 0) {
                    throw new ApiError(ApiError::INVALID_REQUEST, '100 GC 단위로 낼 수 있어요.');
                }
                $w = $this->open();
                $regionId = $player['regionId'];
                if (!isset($w->cities[$regionId])) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
                }
                $funded = isset($w->funded[$regionId][$def['id']]) ? $w->funded[$regionId][$def['id']] : 0;
                $full = $def['cityGoalGc'] * max(1, $def['cityMaxLevel']);
                if ($funded >= $full) {
                    throw new ApiError(ApiError::CONFLICT, '이미 다 지은 시설이에요.');
                }
                $daily = $this->app->daily();
                $limit = $w->policy['dailyFundLimitGc'];
                if ($daily->lock($player['id'], Daily::FUND_GC) + $amountGc > $limit) {
                    throw new ApiError(ApiError::LIMIT_REACHED, '하루에 ' . number_format($limit) . ' GC까지 낼 수 있어요.');
                }
                // 남은 목표보다 많이 내려고 하면 남은 만큼만 받는다 (100 GC 단위로 올림).
                $accepted = (int)min($amountGc, intdiv($full - $funded + 99, 100) * 100);
                $gc = $this->app->wallets()->apply($player['id'], -$accepted, Wallets::CITY_FUND, 'structure', $def['id']);

                $levelBefore = Defs::cityStructureLevel($def, $funded);
                $newFunded = min($full, $funded + $accepted);
                $this->db->exec(
                    'INSERT INTO city_defense_structures (region_id, structure_id, funded_gc, updated_at) VALUES (?, ?, ?, ?)'
                    . ' ON DUPLICATE KEY UPDATE funded_gc = VALUES(funded_gc), updated_at = VALUES(updated_at)',
                    [$regionId, $def['id'], $newFunded, Clock::sql()]
                );
                $w->funded[$regionId][$def['id']] = $newFunded;
                $daily->add($player['id'], Daily::FUND_GC, $accepted);
                $this->app->players()->touchActive($player['id']);

                $points = $w->policy['contribPerFund100Gc'] * intdiv($accepted, 100);
                // 기금은 도시의 방어력을 올린다. 위험도는 시설이 한 단계 완성될 때만 내려간다.
                $leveled = Defs::cityStructureLevel($def, $newFunded) > $levelBefore;
                $effect = $this->applyCityAction($w, $player, $leveled ? Risk::JOINT_DEFENSE : null, $points, 'fund:' . $def['code']);
                $this->flush($w);
                return [
                    'costGc' => $accepted, 'gc' => $gc, 'contribution' => $points, 'city' => $effect,
                    'detail' => $this->detailFrom($w, $player, $regionId),
                ];
            });
    }

    // ── 도시 재정으로 하는 일 (v9.0) ────────────────────────
    // 세금으로 모인 도시 재정을 집행할 때 Treasury 가 부른다. 누구의 지갑도, 누구의 기여도도 바뀌지 않는다.

    /**
     * 도시 재정으로 공용 방어시설의 기금을 채운다. 주민이 낸 기여금과 같은 곳(city_defense_structures)에 쌓인다.
     * @return array accepted(실제로 들어간 금액), levelBefore, levelAfter
     */
    public function treasuryFund($regionId, $structureCode, $amountGc, $reference)
    {
        $def = $this->app->defs()->findStructure((string)$structureCode);
        if ($def === null || $def['ownership'] !== 'city' || $def['comingSoon'] || $def['cityGoalGc'] <= 0) {
            throw new ApiError(ApiError::NOT_FOUND, '도시가 함께 세우는 시설이 아닙니다.');
        }
        $w = $this->open();
        $regionId = (int)$regionId;
        if (!isset($w->cities[$regionId])) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역입니다.');
        }
        $funded = isset($w->funded[$regionId][$def['id']]) ? $w->funded[$regionId][$def['id']] : 0;
        $full = $def['cityGoalGc'] * max(1, $def['cityMaxLevel']);
        $accepted = (int)max(0, min((int)$amountGc, $full - $funded));
        $levelBefore = Defs::cityStructureLevel($def, $funded);
        if ($accepted <= 0) {
            return ['accepted' => 0, 'levelBefore' => $levelBefore, 'levelAfter' => $levelBefore];
        }
        $newFunded = $funded + $accepted;
        $this->db->exec(
            'INSERT INTO city_defense_structures (region_id, structure_id, funded_gc, updated_at) VALUES (?, ?, ?, ?)'
            . ' ON DUPLICATE KEY UPDATE funded_gc = VALUES(funded_gc), updated_at = VALUES(updated_at)',
            [$regionId, $def['id'], $newFunded, Clock::sql()]
        );
        $w->funded[$regionId][$def['id']] = $newFunded;
        $levelAfter = Defs::cityStructureLevel($def, $newFunded);
        if ($levelAfter > $levelBefore) {
            // 시설이 한 단계 완성되면 위험도가 내려간다 (주민 기금으로 완성했을 때와 같은 사유).
            $this->changeRisk($w, $regionId,
                Risk::applySystem($w->cities[$regionId]['riskCenti'], Risk::JOINT_DEFENSE, $levelAfter - $levelBefore, $w->policy),
                Risk::ACTOR_SYSTEM, $reference, Clock::now());
            $this->updateSupport($w, $regionId, Clock::now());
        }
        $this->flush($w);
        return ['accepted' => $accepted, 'levelBefore' => $levelBefore, 'levelAfter' => $levelAfter];
    }

    /**
     * 도시 재정으로 피해를 입은 주요 시설을 고친다.
     * @param int $steps 고칠 단계 수
     * @return array repaired(고친 단계 수), costGc(한 단계의 값 × 고친 단계 수), damageAfter
     */
    public function treasuryRepair($regionId, $facilityCode, $steps, $reference)
    {
        $def = $this->app->defs()->findFacility((string)$facilityCode);
        if ($def === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 시설입니다.');
        }
        $w = $this->open();
        $regionId = (int)$regionId;
        if (!isset($w->cities[$regionId])) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역입니다.');
        }
        $damage = isset($w->damage[$regionId][$def['id']]) ? $w->damage[$regionId][$def['id']] : 0;
        $repaired = (int)max(0, min((int)$steps, $damage));
        if ($repaired > 0) {
            $this->setDamage($w, $regionId, $def['id'], $damage - $repaired);
            $this->changeRisk($w, $regionId,
                Risk::applySystem($w->cities[$regionId]['riskCenti'], Risk::FACILITY_REPAIRED, $repaired, $w->policy),
                Risk::ACTOR_SYSTEM, $reference, Clock::now());
            $this->updateSupport($w, $regionId, Clock::now());
        }
        $this->flush($w);
        return ['repaired' => $repaired, 'costGc' => $def['repairGc'] * $repaired, 'damageAfter' => $damage - $repaired];
    }

    /** 그 도시의 주요 시설 피해 단계 [시설 코드 => 단계]. 집행 화면에서 쓴다. */
    public function facilityDamage($regionId)
    {
        $out = [];
        foreach ($this->db->all(
            'SELECT f.code, d.damage FROM city_facility_damage d JOIN city_facility_defs f ON f.id = d.facility_id WHERE d.region_id = ? AND d.damage > 0',
            [(int)$regionId]
        ) as $row) {
            $out[$row['code']] = (int)$row['damage'];
        }
        return $out;
    }

    // ── 도시 간 지원 ────────────────────────────────────────

    private static function parseId($publicId, $prefix)
    {
        return is_string($publicId) && preg_match('/^' . $prefix . '(\d{1,18})$/', $publicId, $m) ? (int)$m[1] : 0;
    }

    private function sidoOf(World $w, $regionId)
    {
        return $regionId !== null && isset($w->cities[$regionId]) ? $w->cities[$regionId]['sidoCode'] : null;
    }

    /** GET /cities/support: 열려 있는 지원 요청. 주변 도시의 가디언에게만 보인다. */
    public function supportList(array $player)
    {
        $w = $this->read();
        $joined = $this->openSupportOf($player['id']);
        $list = [
            'requests' => [],
            'joinedRequestId' => $joined !== null ? $joined['id'] : null,
            'joinedRegionCode' => $joined !== null ? $joined['regionCode'] : null,
            'mySupportPoints' => (int)$this->db->val('SELECT COALESCE(SUM(points), 0) FROM city_support_contributions WHERE player_id = ?',
                [$player['id']]),
        ];
        // 이방인은 지원에 나설 수 없다. 목록도 비어 있다.
        if (!$player['resident']) {
            return $list;
        }
        $homeSido = $this->sidoOf($w, $player['regionId']);
        foreach ($w->open as $regionId => $request) {
            $reason = Support::checkJoin($request, $player['regionCode'], $homeSido, $this->sidoOf($w, $regionId));
            if ($reason === Support::TOO_FAR) {
                continue;
            }
            $risk = $w->cities[$regionId]['riskCenti'];
            $list['requests'][] = [
                'requestId' => $request['id'], 'regionCode' => $request['regionCode'],
                'risk' => Risk::toPoints($risk), 'stage' => Risk::stageOf($risk, $w->policy),
                'closesAt' => Clock::iso($request['closesUnix']), 'wins' => $request['wins'], 'points' => $request['points'],
                'canJoin' => $reason === null, 'reason' => $reason,
            ];
        }
        // 가장 위험한 도시가 앞에 온다.
        usort($list['requests'], function ($a, $b) {
            return $a['risk'] !== $b['risk'] ? $b['risk'] - $a['risk'] : strcmp($a['regionCode'], $b['regionCode']);
        });
        return $list;
    }

    /** 지원에 나선다. 소속 도시는 바뀌지 않는다: 어디를 돕고 있는지만 적는다. */
    public function joinSupport(array $player, $requestId)
    {
        return $this->db->tx(function () use ($player, $requestId) {
            $this->app->wallets()->lock($player['id']);
            $player = $this->app->players()->get($player['id']);
            $w = $this->open();
            $this->flush($w);
            $id = self::parseId($requestId, 'sup_');
            $request = null;
            foreach ($w->open as $open) {
                if ($open['dbId'] === $id) {
                    $request = $open;
                }
            }
            $reason = Support::checkJoin($request, $player['regionCode'], $this->sidoOf($w, $player['regionId']),
                $request !== null ? $this->sidoOf($w, $request['regionId']) : null);
            if ($reason !== null) {
                if ($reason === Support::NOT_OPEN) {
                    $exists = $id > 0 && $this->db->val('SELECT COUNT(*) FROM city_support_requests WHERE id = ?', [$id]) > 0;
                    throw new ApiError($exists ? ApiError::CONFLICT : ApiError::NOT_FOUND, Support::message($reason), $reason);
                }
                throw new ApiError($reason === Support::NOT_RESIDENT ? ApiError::LOCKED : ApiError::VALIDATION_FAILED,
                    Support::message($reason), $reason);
            }
            $this->app->players()->setSupportRequest($player['id'], $request['dbId']);
            return ['joinedRequestId' => $request['id'], 'joinedRegionCode' => $request['regionCode'],
                'homeRegionCode' => $player['regionCode']];
        });
    }

    public function leaveSupport(array $player)
    {
        $this->app->players()->setSupportRequest($player['id'], null);
        return ['joinedRequestId' => null, 'joinedRegionCode' => null, 'homeRegionCode' => $player['regionCode']];
    }

    /** GET /cities/support/contributions: 내가 다른 도시를 도운 기록. */
    public function supportContributions(array $player)
    {
        $rows = $this->db->all(
            'SELECT c.request_id, c.wins, c.points, c.joined_at, q.status, r.code AS region_code'
            . ' FROM city_support_contributions c JOIN city_support_requests q ON q.id = c.request_id'
            . ' JOIN regions r ON r.id = q.region_id WHERE c.player_id = ? ORDER BY c.updated_at DESC LIMIT 50',
            [$player['id']]
        );
        $list = [];
        $total = 0;
        foreach ($rows as $row) {
            $total += (int)$row['points'];
            $list[] = [
                'requestId' => 'sup_' . $row['request_id'], 'regionCode' => $row['region_code'], 'status' => $row['status'],
                'wins' => (int)$row['wins'], 'points' => (int)$row['points'], 'joinedAt' => Clock::isoFromSql($row['joined_at']),
            ];
        }
        return [
            'contributions' => $list,
            'totalPoints' => (int)$this->db->val('SELECT COALESCE(SUM(points), 0) FROM city_support_contributions WHERE player_id = ?',
                [$player['id']]),
            'homeRegionCode' => $player['regionCode'],
        ];
    }

    // ── 조회 ────────────────────────────────────────────────

    private function rank(World $w)
    {
        if ($w->scores !== null) {
            return;
        }
        $now = Clock::now();
        $stats = [];
        foreach ($w->cities as $regionId => $city) {
            $stats[] = [
                'regionCode' => $city['code'],
                'activePlayers' => $this->activePlayers($w, $regionId, $now),
                'riskCenti' => $city['riskCenti'],
                'playerWins' => $city['playerWins'],
                'playerAttempts' => $city['playerAttempts'],
                'invasionSeverityTotal' => $city['invasionSeverityTotal'],
                'repelledSeverity' => $city['repelledSeverity'],
                'contribution' => $city['contribution'],
                'supportGiven' => $city['supportGiven'],
            ];
        }
        $w->ordered = Ranking::rank($stats, $this->app->defs()->rankingWeights());
        $w->scores = [];
        foreach ($w->ordered as $score) {
            $w->scores[$score['regionCode']] = $score;
        }
    }

    private function recentInvasions(World $w, $regionId)
    {
        if ($w->recent === null) {
            $w->recent = [];
            foreach ($this->db->all(
                'SELECT region_id, COUNT(*) AS n FROM city_invasion_events WHERE started_at >= ? GROUP BY region_id',
                [Clock::sql(Clock::now() - self::RECENT_INVASION_DAYS * 86400)]
            ) as $row) {
                $w->recent[(int)$row['region_id']] = (int)$row['n'];
            }
        }
        return isset($w->recent[$regionId]) ? $w->recent[$regionId] : 0;
    }

    private function statusOf(World $w, $regionId)
    {
        $this->rank($w);
        $city = $w->cities[$regionId];
        $score = isset($w->scores[$city['code']]) ? $w->scores[$city['code']] : null;
        $invasion = isset($w->active[$regionId]) ? $w->active[$regionId] : null;
        return [
            'regionCode' => $city['code'],
            'risk' => Risk::toPoints($city['riskCenti']),
            'stage' => Risk::stageOf($city['riskCenti'], $w->policy),
            'residents' => $city['residents'],
            'activePlayers' => $this->activePlayers($w, $regionId, Clock::now()),
            'defensePoints' => $city['defensePoints'],
            'defensePower' => $this->defensePower($w, $regionId),
            'invasionKind' => $invasion !== null ? $invasion['kind'] : null,
            'invasionSeverity' => $invasion !== null ? $invasion['severity'] : 0,
            'invasionProgress' => $invasion !== null ? $invasion['progress'] : 0,
            'invasionRequired' => $invasion !== null ? $invasion['required'] : 0,
            'invasionEndsAt' => $invasion !== null ? Clock::iso($invasion['endUnix']) : null,
            'recentInvasions' => $this->recentInvasions($w, $regionId),
            'contribution' => $city['contribution'],
            'rank' => $score !== null ? $score['rank'] : 0,
            'score' => $score !== null ? $score['total'] : 0,
            'playerDefended' => $score !== null ? $score['playerDefended'] : false,
            'supportRequestId' => isset($w->open[$regionId]) ? $w->open[$regionId]['id'] : null,
        ];
    }

    /** GET /cities/risk: 전국 위험도. */
    public function risks(array $player)
    {
        return $this->risksFrom($this->read(), $player);
    }

    private function risksFrom(World $w, array $player)
    {
        $support = $this->openSupportOf($player['id']);
        $cities = [];
        foreach (array_keys($w->cities) as $regionId) {
            $cities[] = $this->statusOf($w, $regionId);
        }
        return [
            'serverTime' => Clock::iso(),
            'cities' => $cities,
            'homeRegionCode' => $player['regionCode'],
            'supportRegionCode' => $support !== null ? $support['regionCode'] : null,
        ];
    }

    private function invasionOut(array $invasion)
    {
        return [
            'id' => $invasion['id'], 'regionCode' => $invasion['regionCode'], 'kind' => $invasion['kind'],
            'severity' => $invasion['severity'], 'status' => $invasion['status'],
            'startedAt' => Clock::iso($invasion['startUnix']), 'endsAt' => Clock::iso($invasion['endUnix']),
            'resolvedAt' => Clock::isoFromSql($invasion['resolvedAt']),
            'required' => $invasion['required'], 'progress' => $invasion['progress'],
        ];
    }

    private function historyOut(array $row, $regionCode)
    {
        return [
            'seq' => (int)$row['id'], 'regionCode' => $regionCode, 'at' => Clock::isoFromSql($row['at']),
            'reason' => $row['reason'], 'actor' => $row['actor'], 'deltaCenti' => (int)$row['delta_centi'],
            'afterCenti' => (int)$row['after_centi'], 'stage' => $row['stage'], 'reference' => $row['reference'],
        ];
    }

    private function detailFrom(World $w, array $player, $regionId)
    {
        $city = $w->cities[$regionId];
        $status = $this->statusOf($w, $regionId);
        $isHome = $player['resident'] && $player['regionId'] === $regionId;
        $detail = [
            'status' => $status,
            'mood' => Risk::stageMood($status['stage']),
            'score' => isset($w->scores[$city['code']]) ? $w->scores[$city['code']] : null,
            'history' => $this->historyList($city, self::DETAIL_HISTORY, 0),
            'invasions' => [],
            'facilities' => [],
            'structures' => [],
            'support' => null,
            'myContribution' => $isHome ? $this->contributionOf($player['id'], $regionId) : 0,
            'isHome' => $isHome,
        ];
        foreach ($this->db->all('SELECT * FROM city_invasion_events WHERE region_id = ? ORDER BY id DESC LIMIT ' . self::DETAIL_INVASIONS,
            [$regionId]) as $row) {
            $detail['invasions'][] = $this->invasionOut($this->invasionFromRow($row, $city['code']));
        }
        foreach ($this->app->defs()->facilities() as $def) {
            $detail['facilities'][] = [
                'code' => $def['code'], 'name' => $def['name'],
                'damage' => isset($w->damage[$regionId][$def['id']]) ? $w->damage[$regionId][$def['id']] : 0,
                'maxDamage' => $def['maxDamage'], 'repairGc' => $def['repairGc'],
            ];
        }
        foreach ($this->app->defs()->structures() as $def) {
            if ($def['ownership'] !== 'city' || $def['comingSoon']) {
                continue;
            }
            $funded = isset($w->funded[$regionId][$def['id']]) ? $w->funded[$regionId][$def['id']] : 0;
            $level = Defs::cityStructureLevel($def, $funded);
            $detail['structures'][] = [
                'code' => $def['code'], 'name' => $def['name'], 'level' => $level, 'maxLevel' => max(1, $def['cityMaxLevel']),
                'fundedGc' => $funded - $level * $def['cityGoalGc'], 'goalGc' => $def['cityGoalGc'], 'power' => $level * $def['cityPower'],
            ];
        }
        if (isset($w->open[$regionId])) {
            $open = $w->open[$regionId];
            $detail['support'] = [
                'id' => $open['id'], 'regionCode' => $open['regionCode'], 'status' => $open['status'],
                'openedAt' => Clock::iso($open['openUnix']), 'closesAt' => Clock::iso($open['closesUnix']),
                'wins' => $open['wins'], 'points' => $open['points'],
            ];
        }
        return $detail;
    }

    /** GET /cities/{code}: 도시 상세. */
    public function detail(array $player, $regionCode)
    {
        $w = $this->read();
        $city = $w->byCode((string)$regionCode);
        if ($city === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
        }
        return $this->detailFrom($w, $player, $city['regionId']);
    }

    private function historyList(array $city, $limit, $beforeSeq)
    {
        $limit = max(1, min(100, (int)$limit));
        $sql = 'SELECT * FROM city_risk_history WHERE region_id = ?' . ($beforeSeq > 0 ? ' AND id < ?' : '')
            . ' ORDER BY id DESC LIMIT ' . $limit;
        $rows = $this->db->all($sql, $beforeSeq > 0 ? [$city['regionId'], (int)$beforeSeq] : [$city['regionId']]);
        $list = [];
        foreach ($rows as $row) {
            $list[] = $this->historyOut($row, $city['code']);
        }
        return $list;
    }

    /** GET /cities/{code}/history: 위험도 변경 이력. 새 것이 앞이다. */
    public function history($regionCode, $limit = 50, $beforeSeq = 0)
    {
        $w = $this->read();
        $city = $w->byCode((string)$regionCode);
        if ($city === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
        }
        $entries = $this->historyList($city, $limit, $beforeSeq);
        return [
            'regionCode' => $city['code'],
            'risk' => Risk::toPoints($city['riskCenti']),
            'stage' => Risk::stageOf($city['riskCenti'], $w->policy),
            'entries' => $entries,
            'nextBefore' => count($entries) >= max(1, min(100, (int)$limit)) ? $entries[count($entries) - 1]['seq'] : null,
        ];
    }

    /** GET /cities/invasions: 침공 현황. 진행 중인 침공과 최근에 끝난 침공. */
    public function invasions(array $player)
    {
        $w = $this->read();
        $active = [];
        foreach ($w->active as $invasion) {
            $active[] = $this->invasionOut($invasion);
        }
        usort($active, function ($a, $b) {
            return $a['severity'] !== $b['severity'] ? $b['severity'] - $a['severity'] : strcmp($a['regionCode'], $b['regionCode']);
        });
        $recent = [];
        foreach ($this->db->all(
            "SELECT e.*, r.code AS region_code FROM city_invasion_events e JOIN regions r ON r.id = e.region_id"
            . " WHERE e.status <> 'active' ORDER BY e.resolved_at DESC, e.id DESC LIMIT 30"
        ) as $row) {
            $recent[] = $this->invasionOut($this->invasionFromRow($row, $row['region_code']));
        }
        $home = null;
        if ($player['resident'] && isset($w->active[$player['regionId']])) {
            $home = $this->invasionOut($w->active[$player['regionId']]);
        }
        return ['serverTime' => Clock::iso(), 'active' => $active, 'recent' => $recent, 'home' => $home,
            'homeRegionCode' => $player['regionCode']];
    }

    /** GET /cities/rankings: 지역 랭킹. 활성 가디언이 있는 도시가 먼저 온다. */
    public function rankings(array $player)
    {
        return $this->rankingsFrom($this->read(), $player);
    }

    /**
     * 실시간 대시보드(v10.0, docs/LIVE_DASHBOARD.md)가 한 번에 읽는 것: 전국 위험도, 지역 랭킹, 진행 중인 침공, 도시 이름.
     * 값은 GET /cities/risk, /cities/rankings, /cities/invasions 가 주는 것과 같다. 세계를 한 번만 읽으려고 묶었을 뿐이다.
     */
    public function board(array $player)
    {
        $w = $this->read();
        $active = [];
        foreach ($w->active as $invasion) {
            $active[] = $this->invasionOut($invasion);
        }
        $names = [];
        foreach ($w->cities as $city) {
            $names[$city['code']] = $city['name'];
        }
        return ['risks' => $this->risksFrom($w, $player), 'rankings' => $this->rankingsFrom($w, $player),
            'activeInvasions' => $active, 'names' => $names];
    }

    private function rankingsFrom(World $w, array $player)
    {
        $this->rank($w);
        $byCode = [];
        foreach ($w->cities as $regionId => $city) {
            $byCode[$city['code']] = $regionId;
        }
        $result = ['rows' => [], 'totalCities' => count($w->ordered), 'playerCities' => 0, 'home' => null];
        foreach ($w->ordered as $score) {
            $regionId = $byCode[$score['regionCode']];
            $risk = $w->cities[$regionId]['riskCenti'];
            $row = [
                'regionCode' => $score['regionCode'], 'rank' => $score['rank'], 'score' => $score['total'],
                'safety' => $score['safety'], 'successRate' => $score['successRate'], 'contribution' => $score['contribution'],
                'response' => $score['response'], 'cooperation' => $score['cooperation'], 'playerDefended' => $score['playerDefended'],
                'risk' => Risk::toPoints($risk), 'stage' => Risk::stageOf($risk, $w->policy),
                'activePlayers' => $this->activePlayers($w, $regionId, Clock::now()),
            ];
            if ($score['playerDefended']) {
                $result['playerCities']++;
            }
            if ($player['resident'] && $score['regionCode'] === $player['regionCode']) {
                $result['home'] = $row;
            }
            $result['rows'][] = $row;
        }
        return $result;
    }

    // ── 관리자 ──────────────────────────────────────────────

    /** 관리자 화면용: 모든 도시의 현황. */
    public function adminOverview()
    {
        $w = $this->read();
        $rows = [];
        foreach ($w->cities as $regionId => $city) {
            $rows[] = $this->statusOf($w, $regionId) + [
                'name' => $city['name'], 'npcHeldCount' => $city['npcHeldCount'], 'failedCount' => $city['failedCount'],
                'playerWins' => $city['playerWins'], 'playerAttempts' => $city['playerAttempts'],
                'supportGiven' => $city['supportGiven'], 'supportReceived' => $city['supportReceived'],
            ];
        }
        return $rows;
    }

    /** 관리자 화면용: 랭킹 점수의 세부 항목. */
    public function adminRanking()
    {
        $w = $this->read();
        $this->rank($w);
        return $w->ordered;
    }

    /**
     * 관리자의 수동 조정. 사유가 있어야 하고, 이력에 관리자와 사유가 남는다.
     * @param int $deltaPoints 바꿀 양 (-100 ~ 100, 화면에 보이는 단위).
     */
    public function adminAdjustRisk($adminId, $regionCode, $deltaPoints, $reason)
    {
        $reason = trim((string)$reason);
        if (!is_int($deltaPoints) || $deltaPoints === 0 || $deltaPoints < -100 || $deltaPoints > 100) {
            throw new ApiError(ApiError::VALIDATION_FAILED, '조정 값은 -100 ~ 100 사이의 0이 아닌 정수여야 합니다.');
        }
        if (mb_strlen($reason) < 2) {
            throw new ApiError(ApiError::VALIDATION_FAILED, '사유를 적어 주세요.');
        }
        return $this->db->tx(function () use ($adminId, $regionCode, $deltaPoints, $reason) {
            $w = $this->open();
            $city = $w->byCode((string)$regionCode);
            if ($city === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 지역입니다.');
            }
            $regionId = $city['regionId'];
            $change = Risk::applyExact($city['riskCenti'], Risk::ADMIN_ADJUST, $deltaPoints * 100, $w->policy);
            $this->changeRisk($w, $regionId, $change, Risk::ACTOR_ADMIN, $reason, Clock::now(), null, $adminId);
            $this->updateSupport($w, $regionId, Clock::now());
            $this->flush($w);
            return $change;
        });
    }

    /** 관리자가 침공을 시작한다. 그 도시에 진행 중인 침공이 있으면 시작할 수 없다. */
    public function adminStartInvasion($adminId, $regionCode, $kind, $severity)
    {
        if (!in_array($kind, [Invasion::OUTSKIRTS, Invasion::INTRUSION, Invasion::ZERO], true)) {
            throw new ApiError(ApiError::VALIDATION_FAILED, '없는 침공 종류입니다.');
        }
        if (!is_int($severity) || $severity < 1 || $severity > 3) {
            throw new ApiError(ApiError::VALIDATION_FAILED, '규모는 1 ~ 3 이어야 합니다.');
        }
        return $this->db->tx(function () use ($adminId, $regionCode, $kind, $severity) {
            $w = $this->open();
            $city = $w->byCode((string)$regionCode);
            if ($city === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 지역입니다.');
            }
            $regionId = $city['regionId'];
            if (isset($w->active[$regionId])) {
                throw new ApiError(ApiError::CONFLICT, '그 도시에는 이미 진행 중인 침공이 있습니다.');
            }
            if ($kind === Invasion::ZERO) {
                $severity = 3;
            }
            $now = Clock::now();
            // 관리자가 시작한 침공은 음수 순번을 쓴다. 정해진 판정의 순번과 겹치지 않는다.
            $tick = (int)$this->db->val('SELECT COALESCE(MIN(tick), 0) FROM city_invasion_events WHERE tick < 0') - 1;
            $required = Invasion::requiredWins($w->policy, $severity, $this->activePlayers($w, $regionId, $now));
            $invasion = $this->startInvasion($w, $regionId, $tick, $kind, $severity, $required, $now,
                $now + max(1, $w->policy['invasionMinutes']) * 60, 'admin', Risk::ACTOR_ADMIN, $adminId);
            $this->updateSupport($w, $regionId, $now);
            $this->flush($w);
            return $invasion !== null ? $this->invasionOut($invasion) : null;
        });
    }

    /** 관리자가 진행 중인 침공을 끝낸다. NPC 수비대가 막은 것으로 처리한다 (플레이어의 기여로 세지 않는다). */
    public function adminEndInvasion($adminId, $invasionId)
    {
        return $this->db->tx(function () use ($adminId, $invasionId) {
            $w = $this->open();
            $found = null;
            foreach ($w->active as $invasion) {
                if ($invasion['dbId'] === (int)$invasionId) {
                    $found = $invasion;
                }
            }
            if ($found === null) {
                throw new ApiError(ApiError::NOT_FOUND, '진행 중인 침공이 아닙니다.');
            }
            $regionId = $found['regionId'];
            $now = Clock::now();
            $this->closeInvasion($w, $regionId, $found, Invasion::NPC_HELD, $now);
            $w->cities[$regionId]['npcHeldCount']++;
            $this->changeRisk($w, $regionId,
                Risk::applySystem($w->cities[$regionId]['riskCenti'], Risk::NPC_HELD, $found['severity'], $w->policy),
                Risk::ACTOR_ADMIN, $found['id'], $now, null, $adminId);
            $this->updateSupport($w, $regionId, $now);
            $this->flush($w);
            return $found['id'];
        });
    }

    /** 판정 간격이 바뀌면 순번의 기준이 달라진다. 지금 순번에서 다시 시작하게 맞춘다. */
    public function resyncTick()
    {
        $this->db->tx(function () {
            $this->db->one('SELECT last_tick FROM city_world_state WHERE id = 1 FOR UPDATE');
            $this->app->defs()->forget();
            $tick = Invasion::tickOf(Clock::now(), $this->app->defs()->policy());
            $this->db->exec('UPDATE city_world_state SET last_tick = ?, updated_at = ? WHERE id = 1', [$tick, Clock::sql()]);
        });
    }

    /**
     * 도시 기록이 앞뒤가 맞는지 검사한다 (테스트와 점검용). 맞으면 null.
     * 이력의 마지막 값이 지금 위험도와 같아야 하고, 한 도시에 진행 중인 침공과 열린 지원 요청은 하나까지다.
     */
    public function findInconsistency()
    {
        $bad = $this->db->one(
            'SELECT s.region_id FROM city_risk_status s JOIN city_risk_history h ON h.id ='
            . ' (SELECT MAX(id) FROM city_risk_history WHERE region_id = s.region_id)'
            . ' WHERE h.after_centi <> s.risk_centi LIMIT 1'
        );
        if ($bad !== null) {
            return '이력의 마지막 값과 위험도가 다름: 지역 ' . $bad['region_id'];
        }
        if ($this->db->val('SELECT COUNT(*) FROM city_risk_status WHERE risk_centi < 0 OR risk_centi > ?', [Risk::MAX_CENTI]) > 0) {
            return '위험도가 범위를 벗어남';
        }
        if ($this->db->val("SELECT COUNT(*) FROM (SELECT region_id FROM city_invasion_events WHERE status = 'active'"
            . ' GROUP BY region_id HAVING COUNT(*) > 1) t') > 0) {
            return '한 도시에 진행 중인 침공이 둘 이상';
        }
        if ($this->db->val("SELECT COUNT(*) FROM (SELECT region_id FROM city_support_requests WHERE status = 'open'"
            . ' GROUP BY region_id HAVING COUNT(*) > 1) t') > 0) {
            return '한 도시에 열린 지원 요청이 둘 이상';
        }
        return null;
    }
}
