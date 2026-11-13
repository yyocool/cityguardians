<?php
use CG\Domain\Invasion;
use CG\Domain\Risk;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Service\Daily;

/**
 * 도시 위험도: 서버만 바꾸고, 바뀔 때마다 이력이 남고, 플레이어의 활동으로는 오르지 않고,
 * 개인의 GC·금융과는 독립이다. 침공, 지원, 랭킹도 여기에서 확인한다.
 */
final class CityRiskTest extends DbTestCase
{
    private function regionId($code)
    {
        return (int)$this->db->val('SELECT id FROM regions WHERE code = ?', [$code]);
    }

    private function status($code)
    {
        return $this->db->one('SELECT s.* FROM city_risk_status s JOIN regions r ON r.id = s.region_id WHERE r.code = ?', [$code]);
    }

    private function activeInvasion($code)
    {
        return $this->db->one("SELECT * FROM city_invasion_events WHERE region_id = ? AND status = 'active'", [$this->regionId($code)]);
    }

    // ── 시간의 흐름 ─────────────────────────────────────────

    public function testEveryCityStartsWithARiskInsideTheInitialRangeAndNoHistory()
    {
        T::same(1, $this->app->city()->tick());
        T::eq(230, $this->db->val('SELECT COUNT(*) FROM city_risk_status'));
        // 처음 값: 20 ± 12 (여기에 첫 판정에서 시작된 침공이 더해질 수 있다)
        $quietCities = $this->db->all(
            "SELECT s.risk_centi FROM city_risk_status s WHERE s.region_id NOT IN (SELECT region_id FROM city_invasion_events)");
        T::ok(count($quietCities) > 150);
        foreach ($quietCities as $row) {
            T::ok($row['risk_centi'] >= 800 && $row['risk_centi'] <= 3200, '처음 값이 범위 밖: ' . $row['risk_centi']);
        }
        T::ok(count(array_unique(array_column($quietCities, 'risk_centi'))) > 5, '도시마다 처음 값이 조금씩 달라야 한다');
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testTickStartsExactlyTheInvasionsTheRulesDecide()
    {
        $this->app->city()->tick();
        $policy = $this->app->defs()->policy();
        $seed = (int)$this->db->val('SELECT seed FROM city_world_state WHERE id = 1');
        $tick = Invasion::tickOf(Clock::now(), $policy);
        T::eq($tick, $this->db->val('SELECT last_tick FROM city_world_state WHERE id = 1'));

        $expected = [];
        foreach ($this->db->all('SELECT id, code FROM regions') as $region) {
            $started = Invasion::tryStart($policy, $region['code'], $tick, $seed);
            if ($started !== null) {
                $expected[(int)$region['id']] = $started['kind'] . ':' . $started['severity'];
            }
        }
        $actual = [];
        foreach ($this->db->all('SELECT region_id, kind, severity, status, source FROM city_invasion_events') as $row) {
            $actual[(int)$row['region_id']] = $row['kind'] . ':' . $row['severity'];
            T::same('active', $row['status']);
            T::same('system', $row['source']);
        }
        ksort($expected);
        ksort($actual);
        T::eq($expected, $actual);
        T::ok(count($expected) > 0, '이 순번에는 침공이 하나도 없다. 다른 시각으로 테스트해야 한다');
        // 침공이 시작되면 위험도가 오르고, 이력에 시스템의 일로 남는다.
        T::eq(count($expected), $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE actor = 'system' AND reason IN ('invasion_started', 'zero_event')"));
    }

    public function testRunningTheSameTickAgainChangesNothing()
    {
        $this->app->city()->tick();
        $events = (int)$this->db->val('SELECT COUNT(*) FROM city_invasion_events');
        $history = (int)$this->db->val('SELECT COUNT(*) FROM city_risk_history');
        $sum = (int)$this->db->val('SELECT SUM(risk_centi) FROM city_risk_status');

        T::same(0, $this->app->city()->tick());
        Clock::advance(1800);   // 같은 판정 순번 안
        T::same(0, $this->app->city()->tick());

        T::same($events, (int)$this->db->val('SELECT COUNT(*) FROM city_invasion_events'));
        T::same($history, (int)$this->db->val('SELECT COUNT(*) FROM city_risk_history'));
        T::same($sum, (int)$this->db->val('SELECT SUM(risk_centi) FROM city_risk_status'));
    }

    public function testCatchingUpProcessesEachMissedTickOnceAndKeepsOneInvasionPerCity()
    {
        $this->app->city()->tick();
        Clock::advance(5 * 3600);
        T::same(5, $this->app->city()->tick());
        Clock::advance(40 * 3600);
        T::same(40, $this->app->city()->tick());
        T::same(null, $this->app->city()->findInconsistency());
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM (SELECT region_id FROM city_invasion_events WHERE status = 'active' GROUP BY region_id HAVING COUNT(*) > 1) t"));
        // 제한 시간(3시간)이 지난 침공은 모두 판정이 끝났다.
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM city_invasion_events WHERE status = 'active' AND ends_at <= ?", [Clock::sql()]));
        T::ok($this->db->val("SELECT COUNT(*) FROM city_invasion_events WHERE status IN ('npc_held', 'failed')") > 0);
    }

    public function testALongOutageIsCaughtUpOnlyToTheLimitWithoutInventingThePast()
    {
        $this->app->city()->tick();
        Clock::advance(30 * 86400);
        T::same(72, $this->app->city()->tick());
        T::same(0, $this->app->city()->tick());
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testNpcPatrolLowersRiskOncePerKoreanDayPerCity()
    {
        $this->quiet();
        $this->app->city()->tick();
        $this->setRisk('11110', 5000);
        Clock::advance(3 * 86400);
        $this->app->city()->tick();

        $rows = $this->db->all("SELECT at, delta_centi, actor FROM city_risk_history WHERE region_id = ? AND reason = 'daily_stabilize' ORDER BY id",
            [$this->regionId('11110')]);
        T::eq(3, count($rows));
        // 하루에 20%씩: 5000 → 4000 → 3200 → 2560
        T::eq([-1000, -800, -640], array_map('intval', array_column($rows, 'delta_centi')));
        T::eq(['npc', 'npc', 'npc'], array_column($rows, 'actor'));
        T::eq(2560, $this->status('11110')['risk_centi']);
        // 같은 한국 날짜에 두 번 반영되지 않는다.
        $dates = array_map(function ($row) {
            return Clock::koreaDate(Clock::parse($row['at']));
        }, $rows);
        T::eq(3, count(array_unique($dates)));
    }

    public function testLazyTickRunsWhenARequestArrivesAndCanBeTurnedOff()
    {
        $player = $this->resident();
        $this->app->city()->risks($player);
        T::ok($this->db->val('SELECT last_tick FROM city_world_state WHERE id = 1') >= 0, '요청이 오면 밀린 판정을 따라잡는다');

        // 예약 작업만 쓰는 구성: 요청은 판정을 돌리지 않는다.
        $env = \CG\Infra\Env::fromArray(['WORLD_LAZY_TICK' => '0']);
        $app = new \CG\App($env, $this->db);
        $before = (int)$this->db->val('SELECT last_tick FROM city_world_state WHERE id = 1');
        Clock::advance(4 * 3600);
        $app->city()->risks($player);
        T::same($before, (int)$this->db->val('SELECT last_tick FROM city_world_state WHERE id = 1'));
        T::same(4, $app->city()->tick());
    }

    // ── 침공 ────────────────────────────────────────────────

    public function testPlayersRepelAnOutskirtsInvasionWithDefenseWins()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->app->city()->tick();
        $invasion = $this->app->city()->adminStartInvasion(null, '11110', Invasion::OUTSKIRTS, 2);
        T::same('active', $invasion['status']);
        T::same(2, $invasion['required'], '규모 2 × 활성 가디언 한 묶음');
        $risk = (int)$this->status('11110')['risk_centi'];

        $first = $this->winDefense($player);
        T::same($invasion['id'], $first['city']['invasionId']);
        T::same(1, $first['city']['invasionProgress']);
        T::same(false, $first['city']['invasionRepelled']);
        // 침공을 막는 승리는 전부 반영된다 (순찰의 절반이 아니다): -600
        T::same(-600, $first['city']['riskDeltaCenti']);
        // 기여도: 10 + 10 × 50% × 규모 2 = 20
        T::same(20, $first['city']['contribution']);

        $second = $this->winDefense($player);
        T::same(true, $second['city']['invasionRepelled']);
        // -600 (승리) + -300 × 규모 2 (격퇴)
        T::same(-1200, $second['city']['riskDeltaCenti']);
        T::same($risk - 1800, (int)$this->status('11110')['risk_centi']);

        $row = $this->db->one('SELECT * FROM city_invasion_events ORDER BY id DESC LIMIT 1');
        T::same('repelled', $row['status']);
        T::eq(2, $row['progress']);
        T::eq(2, $this->status('11110')['repelled_severity']);
        T::eq(2, $this->status('11110')['invasion_severity_total']);
        T::same(null, $this->activeInvasion('11110'));
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testEachInvasionKindIsRepelledByItsOwnMode()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->app->city()->tick();
        $this->app->city()->adminStartInvasion(null, '11110', Invasion::INTRUSION, 1);

        // 시내 침입은 FPS 로 막는다. 방어전에서 이겨도 침입의 진행은 그대로다.
        $defense = $this->winDefense($player);
        T::same(null, $defense['city']['invasionId']);
        T::eq(0, $this->activeInvasion('11110')['progress']);

        $session = $this->app->fps()->start($player, 'downtown_1');
        T::ok($session['invasionId'] !== null, '출동할 때 막으려는 침입을 알려 준다');
        Clock::advance(70);
        $fps = $this->app->fps()->finish($player, $session['sessionId'], self::fpsWinReport());
        T::same(true, $fps['city']['invasionRepelled']);
        T::same(null, $this->activeInvasion('11110'));
    }

    public function testAnInvasionNobodyStoppedIsDecidedByTheNpcGarrisonRoll()
    {
        $this->quiet();
        $this->app->city()->tick();
        $city = $this->app->city();
        $codes = array_column($this->db->all('SELECT code FROM regions ORDER BY id LIMIT 40'), 'code');
        foreach ($codes as $code) {
            $city->adminStartInvasion(null, $code, Invasion::INTRUSION, 3);
        }
        $policy = $this->app->defs()->policy();
        $seed = (int)$this->db->val('SELECT seed FROM city_world_state WHERE id = 1');
        Clock::advance(4 * 3600);   // 제한 시간 3시간이 지났다
        $city->tick();

        $resolveTick = Invasion::tickOf(Clock::now() - 3600, $policy);   // 끝난 뒤 처음 오는 판정
        $held = 0;
        $failed = 0;
        foreach ($this->db->all('SELECT e.*, r.code FROM city_invasion_events e JOIN regions r ON r.id = e.region_id') as $row) {
            $expected = Invasion::resolveExpired($policy,
                ['regionCode' => $row['code'], 'severity' => 3, 'required' => (int)$row['required'], 'progress' => 0],
                $policy['baseGarrisonPower'], $seed, $resolveTick);
            T::same($expected, $row['status'], $row['code']);
            $row['status'] === 'npc_held' ? $held++ : $failed++;
        }
        T::same(40, $held + $failed);
        T::ok($held > 0 && $failed > 0, "둘 다 나와야 한다 (막음 $held, 실패 $failed)");

        // NPC 가 막은 것은 플레이어의 기여가 아니다.
        T::same($held, (int)$this->db->val('SELECT SUM(npc_held_count) FROM city_risk_status'));
        T::same($failed, (int)$this->db->val('SELECT SUM(failed_count) FROM city_risk_status'));
        T::same(0, (int)$this->db->val('SELECT SUM(repelled_severity) FROM city_risk_status'));
        T::same(0, (int)$this->db->val('SELECT SUM(contribution) FROM city_risk_status'));
        // 시내 침입을 막지 못하면 주요 시설 한 곳이 피해를 입는다.
        T::same($failed, (int)$this->db->val('SELECT COALESCE(SUM(damage), 0) FROM city_facility_damage'));
        T::eq($failed, $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE reason = 'facility_damaged'"));
        T::eq($failed, $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE reason = 'intrusion_failed' AND actor = 'system'"));
        T::eq($held, $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE reason = 'npc_held' AND actor = 'npc'"));
        T::same(null, $city->findInconsistency());
    }

    public function testOnlyOneInvasionPerCityAtATime()
    {
        $this->quiet();
        $this->app->city()->tick();
        $this->app->city()->adminStartInvasion(null, '11110', Invasion::OUTSKIRTS, 1);
        T::throwsApi(ApiError::CONFLICT, function () {
            $this->app->city()->adminStartInvasion(null, '11110', Invasion::INTRUSION, 1);
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () {
            $this->app->city()->adminStartInvasion(null, '11140', 'meteor', 1);
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () {
            $this->app->city()->adminStartInvasion(null, '11140', Invasion::OUTSKIRTS, 9);
        });
    }

    // ── 플레이어의 활동과 위험도 ────────────────────────────

    public function testNothingAPlayerDoesCanRaiseRisk()
    {
        $player = $this->resident('11110');   // 무작위 침공이 켜진 기본 정책 그대로
        $this->give($player, 5000);
        $this->policy(['dailyPlayerReduceCap' => 100]);   // 하루 한도에 막혀 기록이 줄지 않게
        $this->setRisk('11110', 9000);
        for ($i = 0; $i < 3; $i++) {
            $this->winDefense($player);
            $this->winFps($player, 70);
            $session = $this->app->defense()->start($player, 'outskirts_1');
            Clock::advance(20);
            $this->app->defense()->finish($player, $session['sessionId'], ['result' => 'lose', 'kills' => [], 'baseHpLeft' => 0, 'wavesCleared' => 0]);
            $session = $this->app->fps()->start($player, 'downtown_1');
            Clock::advance(20);
            $this->app->fps()->finish($player, $session['sessionId'], ['result' => 'lose', 'kills' => [], 'facilitiesSaved' => 0, 'civiliansSaved' => 0]);
            $session = $this->app->hunt()->start($player, 'zone_nw');
            Clock::advance(120);
            $this->app->hunt()->report($player, $session['sessionId'], 1, [['enemyCode' => 'mech_insect', 'count' => 30]]);
            Clock::advance(3600);
        }
        T::ok($this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE actor = 'player'") > 5);
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE actor IN ('player', 'supporter') AND delta_centi > 0"),
            '플레이어의 활동으로 위험도가 오른 기록이 있다');
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testAMisconfiguredPositiveWeightStillCannotMakePlayerActivityRaiseRisk()
    {
        // 정책 저장은 관리자 화면의 검증을 거치지만, 그 검증이 빠지더라도 규칙이 마지막으로 막는다.
        $this->policy(['invasionChancePermille' => 0, 'weights' => ['defense_won' => 500]]);
        $player = $this->resident('11110');
        $risk = $this->riskCenti('11110');
        $result = $this->winDefense($player);
        T::same(0, $result['city']['riskDeltaCenti']);
        T::same($risk, $this->riskCenti('11110'));
    }

    public function testOnePlayerCanOnlyLowerRiskUpToTheDailyCap()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->setRisk('11110', 9000);
        $total = 0;
        $capped = false;
        for ($i = 0; $i < 12; $i++) {
            $result = $this->winDefense($player);
            $total += $result['city']['riskDeltaCenti'];
            $capped = $result['city']['capped'];
        }
        // 한 사람이 하루에 낮출 수 있는 양: 20 (= 2000)
        T::same(-2000, $total);
        T::same(true, $capped);
        T::same(7000, (int)$this->status('11110')['risk_centi']);
        T::same(2000, $this->app->daily()->get($player['id'], Daily::RISK_REDUCED));
        // 한도에 닿아도 보상과 기여도는 그대로 쌓인다.
        T::eq(120, $this->status('11110')['contribution']);

        // 다른 가디언은 자기 몫만큼 더 낮출 수 있다.
        $other = $this->resident('11110');
        T::same(-300, $this->winDefense($other)['city']['riskDeltaCenti']);
    }

    public function testInABigCityEachWinCountsForLessSoPopulationAloneDoesNotDecideSafety()
    {
        $this->quiet();
        $this->policy(['activePlayerNorm' => 2]);
        $solo = $this->resident('11110');
        $crowd = [];
        for ($i = 0; $i < 4; $i++) {
            $crowd[] = $this->resident('11140');
        }
        $small = $this->winDefense($solo);
        $big = $this->winDefense($crowd[0]);
        T::same(-300, $small['city']['riskDeltaCenti']);
        // 활성 가디언 4명, 기준 2명 → 한 사람의 몫은 50%
        T::same(-150, $big['city']['riskDeltaCenti']);
    }

    // ── 위험도와 개인 금융은 독립 ───────────────────────────

    public function testRisingRiskAndLostBattlesNeverTouchAPlayersGc()
    {
        $player = $this->resident('11110');
        $this->give($player, 3000);
        $gc = $this->gc($player);
        $ledger = (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries WHERE player_id = ?', [$player['id']]);
        $this->app->city()->tick();

        // 도시에 나쁜 일이 잇달아 일어난다: 침공, 방어 실패, 시설 피해, 긴급 단계, 지원 요청 만료.
        $this->app->city()->adminStartInvasion(null, '11110', Invasion::ZERO, 3);
        $this->app->city()->adminAdjustRisk(null, '11110', 60, '테스트: 긴급 단계로');
        Clock::advance(10 * 86400);
        for ($i = 0; $i < 6; $i++) {
            $this->app->city()->tick();
            Clock::advance(2 * 86400);
        }
        T::ok($this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE region_id = ? AND delta_centi > 0", [$this->regionId('11110')]) >= 2);

        T::same($gc, $this->gc($player), '도시 위험도 때문에 GC 가 줄었다');
        T::same($ledger, (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries WHERE player_id = ?', [$player['id']]));
        T::same(null, $this->app->wallets()->findInconsistency($player['id']));
    }

    public function testCityTablesDoNotReferToBankLoanOrCreditData()
    {
        // 도시 위험도와 개인 금융은 서로 다른 시스템이다. v8.0 이 만든 어느 테이블에도 금융 쪽을 가리키는 열이 없다.
        $rows = $this->db->all(
            "SELECT table_name AS t, column_name AS c FROM information_schema.columns WHERE table_schema = DATABASE()"
            . " AND (table_name LIKE 'city\\_%' OR table_name LIKE 'defense\\_%' OR table_name LIKE 'fps\\_%' OR table_name LIKE 'combat\\_%')"
            . " AND (column_name LIKE '%credit%' OR column_name LIKE '%loan%' OR column_name LIKE '%bank%' OR column_name LIKE '%debt%')");
        T::eq([], $rows);
        // 원장에서 GC 가 빠져나가는 이유는 플레이어가 스스로 고른 것뿐이다.
        $player = $this->resident('11110');
        $this->give($player, 5000);
        $this->winDefense($player);
        $this->app->combat()->upgrade($player, 'gear_energy_blaster', $this->key());
        $reasons = array_column($this->db->all('SELECT DISTINCT reason FROM ledger_entries WHERE delta < 0'), 'reason');
        T::eq(['gear_upgrade'], $reasons);
    }

    // ── 시설 복구와 도시 기금 ───────────────────────────────

    public function testRepairingAFacilityCostsTheDefinedGcAndLowersRisk()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->give($player, 1000);
        $this->app->city()->tick();
        $facility = $this->db->one("SELECT id FROM city_facility_defs WHERE code = 'fac_power'");
        $this->db->exec('INSERT INTO city_facility_damage (region_id, facility_id, damage, updated_at) VALUES (?, ?, 2, ?)',
            [$this->regionId('11110'), $facility['id'], Clock::sql()]);
        $gc = $this->gc($player);
        $risk = $this->riskCenti('11110');
        $key = $this->key();

        $result = $this->app->city()->repairFacility($player, 'fac_power', $key);

        T::same(150, $result['costGc']);
        T::same($gc - 150, $result['gc']);
        T::same(-200, $result['city']['riskDeltaCenti']);
        T::same('facility_repaired', $result['city']['reason']);
        T::same(5, $result['contribution']);
        T::same($risk - 200, $this->riskCenti('11110'));
        T::eq(1, $this->db->val('SELECT damage FROM city_facility_damage WHERE region_id = ? AND facility_id = ?',
            [$this->regionId('11110'), $facility['id']]));

        // 같은 요청은 한 번만 처리한다.
        $again = $this->app->city()->repairFacility($player, 'fac_power', $key);
        T::same(true, $again['duplicate']);
        T::same($gc - 150, $this->gc($player));

        $this->app->city()->repairFacility($player, 'fac_power', $this->key());
        T::throwsApi(ApiError::CONFLICT, function () use ($player) {
            $this->app->city()->repairFacility($player, 'fac_power', $this->key());   // 더 고칠 곳이 없다
        });
        T::same($gc - 300, $this->gc($player));
        T::same(null, $this->app->wallets()->findInconsistency($player['id']));
    }

    public function testRepairIsLimitedPerDayAndRequiresResidencyAndGc()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->app->city()->tick();
        foreach ($this->db->all('SELECT id FROM city_facility_defs') as $facility) {
            $this->db->exec('INSERT INTO city_facility_damage (region_id, facility_id, damage, updated_at) VALUES (?, ?, 3, ?)',
                [$this->regionId('11110'), $facility['id'], Clock::sql()]);
        }
        $this->give($player, 1000);
        $gc = $this->gc($player);
        for ($i = 0; $i < 3; $i++) {
            $this->app->city()->repairFacility($player, 'fac_comm', $this->key());
        }
        T::same($gc - 450, $this->gc($player));
        T::throwsApi(ApiError::LIMIT_REACHED, function () use ($player) {
            $this->app->city()->repairFacility($player, 'fac_water', $this->key());   // 하루 3번까지
        });
        T::same($gc - 450, $this->gc($player));
        // 다음 날(한국 시간)에는 다시 고칠 수 있다.
        Clock::advance(86400);
        $this->app->city()->repairFacility($player, 'fac_water', $this->key());

        // GC 가 모자라면 고칠 수 없고, 빚을 지지도 않는다.
        $poor = $this->resident('11110');
        $this->app->defense()->purchase($poor, 'twr_blast', $this->key());   // 전입 축하금 500 GC 를 다 쓴다
        T::same(0, $this->gc($poor));
        T::throwsApi(ApiError::INSUFFICIENT_GC, function () use ($poor) {
            $this->app->city()->repairFacility($poor, 'fac_water', $this->key());
        });
        T::same(0, $this->gc($poor));
        $stranger = $this->guest();
        T::throwsApi(ApiError::LOCKED, function () use ($stranger) {
            $this->app->city()->repairFacility($stranger, 'fac_water', $this->key());
        });
    }

    public function testCityFundBuildsASharedStructureThatNoPlayerOwns()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->give($player, 20000);
        $def = $this->db->one("SELECT * FROM defense_structures WHERE ownership = 'city' AND is_coming_soon = 0 AND city_goal_gc > 0 ORDER BY city_goal_gc LIMIT 1");
        T::ok($def !== null);
        $this->policy(['dailyFundLimitGc' => 1000000]);
        $risk = $this->riskCenti('11110');
        $powerBefore = $this->app->city()->detail($player, '11110')['status']['defensePower'];

        T::throwsApi(ApiError::INVALID_REQUEST, function () use ($player, $def) {
            $this->app->city()->fundStructure($player, $def['code'], 150, $this->key());   // 100 GC 단위
        });
        T::throwsApi(ApiError::INVALID_REQUEST, function () use ($player, $def) {
            $this->app->city()->fundStructure($player, $def['code'], -500, $this->key());
        });

        $gc = $this->gc($player);
        $first = $this->app->city()->fundStructure($player, $def['code'], 100, $this->key());
        T::same(100, $first['costGc']);
        T::same(2, $first['contribution']);
        T::same(0, $first['city']['riskDeltaCenti'], '단계가 완성되기 전에는 위험도가 그대로다');

        // 한 단계의 목표를 채우면 단계가 오르고, 그때 공동 방어로 위험도가 내려간다.
        $rest = (int)$def['city_goal_gc'] - 100;
        $second = $this->app->city()->fundStructure($player, $def['code'], $rest, $this->key());
        T::same($rest, $second['costGc']);
        T::same('joint_defense', $second['city']['reason']);
        T::same(-300, $second['city']['riskDeltaCenti']);
        T::same($risk - 300, $this->riskCenti('11110'));
        T::same($gc - (int)$def['city_goal_gc'], $this->gc($player));

        $detail = $this->app->city()->detail($player, '11110');
        $structure = null;
        foreach ($detail['structures'] as $row) {
            if ($row['code'] === $def['code']) {
                $structure = $row;
            }
        }
        T::same(1, $structure['level']);
        T::same((int)$def['city_power'], $structure['power']);
        T::same($powerBefore + (int)$def['city_power'], $detail['status']['defensePower']);

        // 도시의 것이지 낸 사람의 것이 아니다.
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM player_defense_structures WHERE player_id = ?', [$player['id']]));
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM city_defense_structures WHERE region_id = ?', [$this->regionId('11110')]));
        T::ok(!in_array($def['code'], $this->app->defense()->usableStructures($player['id']), true));
    }

    public function testCityFundHasADailyLimitAndNeverTakesMoreThanTheGoal()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->give($player, 500000);
        $def = $this->db->one("SELECT * FROM defense_structures WHERE ownership = 'city' AND is_coming_soon = 0 AND city_goal_gc > 0 ORDER BY city_goal_gc LIMIT 1");
        $this->app->city()->fundStructure($player, $def['code'], 2000, $this->key());
        T::throwsApi(ApiError::LIMIT_REACHED, function () use ($player, $def) {
            $this->app->city()->fundStructure($player, $def['code'], 100, $this->key());   // 하루 2,000 GC 까지
        });

        $this->policy(['dailyFundLimitGc' => 1000000]);
        $full = (int)$def['city_goal_gc'] * max(1, (int)$def['city_max_level']);
        $gc = $this->gc($player);
        $result = $this->app->city()->fundStructure($player, $def['code'], 400000, $this->key());
        T::same($full - 2000, $result['costGc'], '남은 목표만큼만 받는다');
        T::same($gc - ($full - 2000), $this->gc($player));
        T::throwsApi(ApiError::CONFLICT, function () use ($player, $def) {
            $this->app->city()->fundStructure($player, $def['code'], 100, $this->key());   // 이미 다 지었다
        });
    }

    // ── 도시 간 지원 ────────────────────────────────────────

    public function testAnEmergencyCityAsksNearbyGuardiansForHelpAndSupportersKeepTheirHomeCity()
    {
        $this->quiet();
        $helper = $this->resident('11110', '도우미');
        $far = $this->resident((string)$this->db->val("SELECT r.code FROM regions r JOIN sidos s ON s.id = r.sido_id WHERE s.code <> '11' ORDER BY r.id LIMIT 1"), '먼도시');
        $local = $this->resident('11140', '주민');
        $this->app->city()->tick();

        // 11140 이 긴급 단계가 된다 → 지원 요청이 열린다.
        $this->app->city()->adminAdjustRisk(null, '11140', 70, '테스트: 긴급 단계로');
        $risk = (int)$this->status('11140')['risk_centi'];
        T::ok($risk >= 7500);
        $request = $this->db->one("SELECT * FROM city_support_requests WHERE status = 'open'");
        T::eq($this->regionId('11140'), $request['region_id']);

        // 같은 시·도의 가디언에게만 보인다.
        $list = $this->app->city()->supportList($helper);
        T::eq(1, count($list['requests']));
        T::same('11140', $list['requests'][0]['regionCode']);
        T::same(true, $list['requests'][0]['canJoin']);
        T::eq([], $this->app->city()->supportList($far)['requests']);
        T::eq([], $this->app->city()->supportList($this->guest())['requests']);
        // 자기 도시의 요청은 보이지만 "지원"으로 나설 수는 없다 (바로 지키면 된다).
        $own = $this->app->city()->supportList($local)['requests'][0];
        T::same(false, $own['canJoin']);
        T::same('own_city', $own['reason']);
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($local, $list) {
            $this->app->city()->joinSupport($local, $list['requests'][0]['requestId']);
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($far, $list) {
            $this->app->city()->joinSupport($far, $list['requests'][0]['requestId']);
        });
        T::throwsApi(ApiError::NOT_FOUND, function () use ($helper) {
            $this->app->city()->joinSupport($helper, 'sup_999999');
        });

        $joined = $this->app->city()->joinSupport($helper, $list['requests'][0]['requestId']);
        T::same('11140', $joined['joinedRegionCode']);
        T::same('11110', $joined['homeRegionCode']);
        // 소속 도시는 바뀌지 않는다.
        T::same('11110', $this->fresh($helper)['regionCode']);
        T::same('11140', $this->app->players()->me($helper)['supportRegionCode']);

        $homeRisk = (int)$this->status('11110')['risk_centi'];
        $result = $this->winDefense($helper);

        // 승리는 지원 나간 도시에 반영된다.
        T::same('11140', $result['city']['regionCode']);
        T::same('supporter', $result['city']['actor']);
        T::same($risk - 300, (int)$this->status('11140')['risk_centi']);
        T::same($homeRisk, (int)$this->status('11110')['risk_centi']);
        T::same('11140', $result['regionCode'], '방어 포인트도 도운 도시에 쌓인다');
        // 기여는 "지원 기여도"로 따로 쌓인다: 도운 도시의 주민 기여도에 섞이지 않는다.
        T::eq(0, $this->status('11140')['contribution']);
        T::eq(10, $this->status('11140')['support_received']);
        T::eq(10, $this->status('11110')['support_given']);
        T::eq(0, $this->app->players()->me($helper)['cityContribution']);
        $mine = $this->app->city()->supportContributions($helper);
        T::same(10, $mine['totalPoints']);
        T::same('11140', $mine['contributions'][0]['regionCode']);
        T::same(1, $mine['contributions'][0]['wins']);
        T::same('11110', $mine['homeRegionCode']);

        // 사냥은 우리 도시의 거리에서 하는 일이라 지원 중에도 소속 도시에 반영된다.
        $session = $this->app->hunt()->start($helper, 'zone_nw');
        Clock::advance(60);
        $hunt = $this->app->hunt()->report($helper, $session['sessionId'], 1, [['enemyCode' => 'mech_insect', 'count' => 10]]);
        T::same('11110', $hunt['city']['regionCode']);

        // 그만두면 다시 우리 도시를 지킨다.
        $this->app->city()->leaveSupport($helper);
        T::same('11110', $this->winDefense($helper)['city']['regionCode']);
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testASupportRequestClosesWhenTheCityIsSafeAgain()
    {
        $this->quiet();
        $helper = $this->resident('11110');
        $this->app->city()->tick();
        $this->app->city()->adminAdjustRisk(null, '11140', 70, '테스트: 긴급 단계로');
        $requestId = 'sup_' . $this->db->val("SELECT id FROM city_support_requests WHERE status = 'open'");
        $this->app->city()->joinSupport($helper, $requestId);

        $this->app->city()->adminAdjustRisk(null, '11140', -60, '테스트: 다시 안전하게');

        T::eq('resolved', $this->db->val('SELECT status FROM city_support_requests ORDER BY id DESC LIMIT 1'));
        // 요청이 닫히면 지원 나간 가디언은 다시 자기 도시를 지킨다.
        T::same(null, $this->app->players()->me($helper)['supportRegionCode']);
        T::same('11110', $this->winDefense($helper)['city']['regionCode']);
        T::throwsApi(ApiError::CONFLICT, function () use ($helper, $requestId) {
            $this->app->city()->joinSupport($helper, $requestId);
        });
    }

    public function testAnEmergencyThatNobodyAnsweredRaisesRiskWhenItExpires()
    {
        $this->quiet();
        $this->policy(['dailyStabilizePercent' => 0]);
        $this->app->city()->tick();
        $this->app->city()->adminAdjustRisk(null, '11140', 60, '테스트: 긴급 단계로');
        $risk = (int)$this->status('11140')['risk_centi'];
        Clock::advance(7 * 3600);   // 지원 요청은 6시간 열려 있다
        $this->app->city()->tick();

        T::eq(1, $this->db->val("SELECT COUNT(*) FROM city_support_requests WHERE status = 'expired'"));
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE reason = 'emergency_unresolved' AND actor = 'system'"));
        T::same(min(10000, $risk + 400), (int)$this->status('11140')['risk_centi']);
        // 여전히 긴급 단계이므로 새 요청이 열린다.
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM city_support_requests WHERE status = 'open'"));
        T::same(null, $this->app->city()->findInconsistency());
    }

    // ── 전국 지도와 랭킹 ────────────────────────────────────

    public function testNationalRiskListCoversEveryCityWithStageAndInvasionInfo()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->app->city()->tick();
        $this->app->city()->adminStartInvasion(null, '11140', Invasion::INTRUSION, 2);
        $this->setRisk('11170', 8000);

        $risks = $this->app->city()->risks($player);

        T::eq(230, count($risks['cities']));
        T::same('11110', $risks['homeRegionCode']);
        $byCode = array_column($risks['cities'], null, 'regionCode');
        T::same('emergency', $byCode['11170']['stage']);
        T::same(80, $byCode['11170']['risk']);
        T::same('intrusion', $byCode['11140']['invasionKind']);
        T::same(2, $byCode['11140']['invasionSeverity']);
        T::same(1, $byCode['11140']['recentInvasions']);
        T::ok($byCode['11140']['invasionEndsAt'] !== null);
        T::same(1, $byCode['11110']['residents']);
        T::same(1, $byCode['11110']['activePlayers']);
        T::same(true, $byCode['11110']['playerDefended']);
        T::same(false, $byCode['11170']['playerDefended']);
        T::same(100, $byCode['11170']['defensePower'], 'NPC 수비대만 있는 도시');
        foreach ($risks['cities'] as $city) {
            T::ok(in_array($city['stage'], ['safe', 'caution', 'danger', 'emergency'], true));
            T::same(Risk::stageOf($city['risk'] * 100, $this->app->defs()->policy()), $city['stage']);
            T::ok($city['rank'] >= 1 && $city['rank'] <= 230);
        }
    }

    public function testCityDetailShowsHistoryNewestFirstAndHistoryCanBePaged()
    {
        $this->quiet();
        $player = $this->resident('11110');
        for ($i = 0; $i < 4; $i++) {
            $this->winDefense($player);
        }
        $detail = $this->app->city()->detail($player, '11110');
        T::same(true, $detail['isHome']);
        T::same(40, $detail['myContribution']);
        T::eq(4, count($detail['history']));
        T::ok($detail['history'][0]['seq'] > $detail['history'][1]['seq']);
        T::same('defense_won', $detail['history'][0]['reason']);
        T::same('player', $detail['history'][0]['actor']);
        T::eq(3, count($detail['facilities']));
        T::ok(strlen($detail['mood']) > 0);

        $page1 = $this->app->city()->history('11110', 3);
        T::eq(3, count($page1['entries']));
        T::ok($page1['nextBefore'] !== null);
        $page2 = $this->app->city()->history('11110', 3, $page1['nextBefore']);
        T::eq(1, count($page2['entries']));
        T::ok($page2['entries'][0]['seq'] < $page1['entries'][2]['seq']);

        T::throwsApi(ApiError::NOT_FOUND, function () use ($player) {
            $this->app->city()->detail($player, '99999');
        });
        T::same(false, $this->app->city()->detail($player, '11140')['isHome']);
    }

    public function testCitiesDefendedOnlyByNpcsRankBelowCitiesWithRealGuardians()
    {
        $this->quiet();
        $player = $this->resident('11110');
        $this->app->city()->tick();
        // 플레이어의 도시는 위험하고, NPC 만 있는 도시는 아주 안전한 상황.
        $this->setRisk('11110', 9000);
        $this->setRisk('11140', 0);

        $rankings = $this->app->city()->rankings($player);

        T::eq(230, count($rankings['rows']));
        T::same(230, $rankings['totalCities']);
        T::same(1, $rankings['playerCities']);
        T::same('11110', $rankings['rows'][0]['regionCode'], 'NPC 가 지킨 결과로 실제 플레이어의 도시를 앞지르지 않는다');
        T::same(1, $rankings['home']['rank']);
        T::same(true, $rankings['rows'][0]['playerDefended']);
        T::same(false, $rankings['rows'][1]['playerDefended']);
        // NPC 도시끼리는 점수 순서다.
        T::same('11140', $rankings['rows'][1]['regionCode']);
        for ($i = 2; $i < 230; $i++) {
            T::ok($rankings['rows'][$i - 1]['score'] >= $rankings['rows'][$i]['score']);
        }
    }

    public function testRankingUsesPerGuardianContributionNotRawPopulation()
    {
        $this->quiet();
        $solo = $this->resident('11110');
        $crowd = [];
        for ($i = 0; $i < 5; $i++) {
            $crowd[] = $this->resident('11140');
        }
        // 혼자인 도시의 가디언은 세 번 이기고, 다섯 명인 도시에서는 한 명만 세 번 이긴다.
        for ($i = 0; $i < 3; $i++) {
            $this->winDefense($solo);
            $this->winDefense($crowd[0]);
        }
        $this->setRisk('11110', 2000);
        $this->setRisk('11140', 2000);
        $rows = array_column($this->app->city()->rankings($solo)['rows'], null, 'regionCode');
        T::eq($this->status('11110')['contribution'], $this->status('11140')['contribution']);
        T::ok($rows['11110']['contribution'] > $rows['11140']['contribution'], '같은 총합이라도 한 사람당 기여가 큰 도시가 높다');
        T::ok($rows['11110']['rank'] < $rows['11140']['rank']);
    }

    // ── 관리자 조정 ─────────────────────────────────────────

    public function testAdminAdjustmentNeedsAReasonAndIsRecorded()
    {
        $this->app->city()->tick();
        T::throwsApi(ApiError::VALIDATION_FAILED, function () {
            $this->app->city()->adminAdjustRisk(7, '11110', 10, '');
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () {
            $this->app->city()->adminAdjustRisk(7, '11110', 0, '사유');
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () {
            $this->app->city()->adminAdjustRisk(7, '11110', 500, '사유');
        });
        $before = (int)$this->status('11110')['risk_centi'];
        $this->app->city()->adminAdjustRisk(7, '11110', 10, '이벤트 보정');
        T::same($before + 1000, (int)$this->status('11110')['risk_centi']);
        $row = $this->db->one('SELECT * FROM city_risk_history ORDER BY id DESC LIMIT 1');
        T::same('admin_adjust', $row['reason']);
        T::same('admin', $row['actor']);
        T::eq(7, $row['admin_id']);
        T::same('이벤트 보정', $row['reference']);
        // 범위를 넘지 않는다.
        $this->app->city()->adminAdjustRisk(7, '11110', 100, '상한 확인');
        T::same(10000, (int)$this->status('11110')['risk_centi']);
        $this->app->city()->adminAdjustRisk(7, '11110', -100, '하한 확인');
        T::same(0, (int)$this->status('11110')['risk_centi']);
    }
}
