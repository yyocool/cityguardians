<?php
use CG\Infra\Clock;
use CG\Infra\Db;
use CG\Infra\Env;
use CG\Infra\Migrator;

/**
 * DB 가 필요한 테스트의 바탕.
 *
 * CG_TEST_DB_* 환경변수가 가리키는 DB 에서만 돈다. 그 DB 의 이름은 _test 로 끝나야 한다:
 * 테스트마다 그 안의 테이블을 모두 지우고 마이그레이션을 처음부터 다시 적용하기 때문이다.
 * 이름이 _test 로 끝나지 않으면 아무것도 건드리지 않고 멈춘다.
 */
abstract class DbTestCase
{
    /** @var Db */
    protected $db;
    /** @var Env */
    protected $env;
    /** @var \CG\App */
    protected $app;

    private static $ready = false;

    public static function envForTests()
    {
        $name = getenv('CG_TEST_DB_NAME');
        if ($name === false || $name === '') {
            return null;
        }
        if (!preg_match('/_test$/', $name)) {
            throw new RuntimeException("테스트용 DB 의 이름은 _test 로 끝나야 합니다: $name");
        }
        return Env::fromArray([
            'APP_ENV' => 'test',
            'DB_HOST' => getenv('CG_TEST_DB_HOST') ?: '127.0.0.1',
            'DB_PORT' => getenv('CG_TEST_DB_PORT') ?: '3306',
            'DB_USER' => getenv('CG_TEST_DB_USER') ?: '',
            'DB_PASS' => getenv('CG_TEST_DB_PASS') ?: '',
            'DB_NAME' => $name,
            'WORLD_LAZY_TICK' => '1',
            'ADMIN_COOKIE_SECURE' => '0',
            // 테스트에서만 쓰는 값이다. 실제 키는 서버의 .env 에만 둔다.
            'DATA_KEY' => base64_encode(str_repeat('t', 32)),
            'PUBLIC_BASE_URL' => 'https://cg.test',
            // v9.0: 실시간 서버와 나눠 갖는 서명 키, 실시간 서버 주소, 지역 콘텐츠 주소. 모두 테스트에서만 쓰는 값이다.
            'REALTIME_SECRET' => str_repeat('test-realtime-secret-', 2),
            'REALTIME_URL' => 'wss://rt.cg.test/ws',
            'CONTENT_BASE_URL' => 'https://cdn.cg.test/content',
        ]);
    }

    public function setUp()
    {
        $this->env = self::envForTests();
        if ($this->env === null) {
            T::skip('CG_TEST_DB_NAME 이 없어 DB 테스트를 돌리지 않았습니다.');
        }
        Clock::set(null);
        $this->db = Db::connect($this->env);
        if (!self::$ready) {
            self::dropAll($this->db);
            (new Migrator($this->db, __DIR__ . '/../migrations'))->migrate();
            self::$ready = true;
        } else {
            self::truncateData($this->db);
        }
        // 2026-11-13 12:00:00 KST. 테스트마다 같은 시각에서 시작한다.
        Clock::set(1794538800);
        $this->app = new \CG\App($this->env, $this->db);
    }

    public function tearDown()
    {
        Clock::set(null);
    }

    // ── 테스트에서 자주 쓰는 것 ─────────────────────────────

    private $counter = 0;

    /** 새 게스트 (이방인). */
    protected function guest()
    {
        $guest = $this->app->players()->createGuest('windows', '8.0.0');
        return $this->app->players()->authenticate($guest['accessToken']);
    }

    /** 전입신고를 마친 주민. */
    protected function resident($regionCode = '11110', $nickname = null)
    {
        $player = $this->guest();
        $this->app->players()->registerResidency($player, $regionCode, $nickname !== null ? $nickname : '가디언' . (++$this->counter));
        return $this->app->players()->get($player['id']);
    }

    protected function fresh(array $player)
    {
        return $this->app->players()->get($player['id']);
    }

    /** 테스트 준비용: GC 를 넣어 준다 (원장에 남는다). */
    protected function give(array $player, $gc)
    {
        $this->app->wallets()->apply($player['id'], $gc, \CG\Service\Wallets::ADMIN_ADJUST);
    }

    protected function setLevel(array $player, $level)
    {
        $this->db->exec('UPDATE characters SET level = ?, xp = 0 WHERE player_id = ?', [$level, $player['id']]);
        return $this->fresh($player);
    }

    protected function gc(array $player)
    {
        return $this->app->wallets()->balance($player['id']);
    }

    protected function key()
    {
        return 'test-key-' . str_pad((string)(++$this->counter), 6, '0', STR_PAD_LEFT);
    }

    /** 정책의 일부 값을 바꾼 새 정책을 저장한다 (관리자가 고친 것과 같다). */
    protected function policy(array $overrides)
    {
        $policy = $this->app->defs()->policy();
        foreach ($overrides as $key => $value) {
            if ($key === 'weights') {
                foreach ($value as $reason => $delta) {
                    $policy['weights'][$reason] = $delta;
                }
            } else {
                $policy[$key] = $value;
            }
        }
        unset($policy['version']);
        $this->db->exec('INSERT INTO city_risk_policy (policy, ranking, admin_id, reason, created_at) VALUES (?, ?, NULL, ?, ?)',
            [json_encode($policy), json_encode($this->app->defs()->rankingWeights()), '테스트', Clock::sql()]);
        $this->app->defs()->forget();
    }

    /** 무작위 침공이 일어나지 않게 한다. 금액과 위험도 변화를 정확히 확인하는 테스트에서 쓴다. */
    protected function quiet()
    {
        $this->policy(['invasionChancePermille' => 0]);
    }

    protected function riskCenti($regionCode)
    {
        $this->app->city()->tick();
        return (int)$this->db->val(
            'SELECT s.risk_centi FROM city_risk_status s JOIN regions r ON r.id = s.region_id WHERE r.code = ?', [$regionCode]);
    }

    /** 테스트 준비용: 도시의 위험도를 정해 둔다. 이력의 마지막 값과 맞도록 이력에도 한 줄을 넣는다. */
    protected function setRisk($regionCode, $centi)
    {
        $this->app->city()->tick();
        $regionId = (int)$this->db->val('SELECT id FROM regions WHERE code = ?', [$regionCode]);
        $before = (int)$this->db->val('SELECT risk_centi FROM city_risk_status WHERE region_id = ?', [$regionId]);
        $this->db->exec('UPDATE city_risk_status SET risk_centi = ? WHERE region_id = ?', [$centi, $regionId]);
        $this->db->exec(
            'INSERT INTO city_risk_history (region_id, at, reason, actor, delta_centi, after_centi, stage, reference) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
            [$regionId, Clock::sql(), 'admin_adjust', 'admin', $centi - $before, $centi,
                \CG\Domain\Risk::stageOf($centi, $this->app->defs()->policy()), '테스트 준비']
        );
    }

    protected static function defenseWinReport()
    {
        return ['result' => 'win', 'baseHpLeft' => 20, 'wavesCleared' => 5, 'kills' => [
            ['enemyCode' => 'malfunction_robot', 'count' => 60],
            ['enemyCode' => 'mech_insect', 'count' => 134],
            ['enemyCode' => 'construction_mecha', 'count' => 1],
        ]];
    }

    /** 방어전을 정상적으로 이긴다 (서버가 재는 시간이 충분히 흐른 뒤 보고). */
    protected function winDefense(array $player, $seconds = 60)
    {
        $session = $this->app->defense()->start($player, 'outskirts_1');
        Clock::advance($seconds);
        return $this->app->defense()->finish($player, $session['sessionId'], self::defenseWinReport());
    }

    protected static function fpsWinReport()
    {
        return ['result' => 'win', 'facilitiesSaved' => 3, 'civiliansSaved' => 4, 'kills' => [
            ['enemyCode' => 'malfunction_robot', 'count' => 8],
            ['enemyCode' => 'mech_insect', 'count' => 10],
        ]];
    }

    protected function winFps(array $player, $seconds = 61)
    {
        $session = $this->app->fps()->start($player, 'downtown_1');
        Clock::advance($seconds);
        return $this->app->fps()->finish($player, $session['sessionId'], self::fpsWinReport());
    }

    /** 테스트 DB 의 모든 테이블을 지운다. 이름이 _test 로 끝나는 DB 에서만 쓴다. */
    public static function dropAll(Db $db)
    {
        self::assertTestDb($db);
        $db->pdo()->exec('SET FOREIGN_KEY_CHECKS = 0');
        foreach ($db->all('SELECT table_name AS name FROM information_schema.tables WHERE table_schema = DATABASE()') as $row) {
            $db->pdo()->exec('DROP TABLE `' . str_replace('`', '', $row['name']) . '`');
        }
        $db->pdo()->exec('SET FOREIGN_KEY_CHECKS = 1');
    }

    /** 정의 데이터(초기값)는 남기고, 플레이어와 도시의 기록만 비운다. */
    public static function truncateData(Db $db)
    {
        self::assertTestDb($db);
        $tables = [
            'players', 'player_credentials', 'player_sessions', 'characters', 'wallets', 'ledger_entries',
            'idempotency_keys', 'daily_counters', 'rate_limits', 'region_contributions', 'config_history',
            'admins', 'admin_sessions', 'admin_audit_logs', 'system_logs',
            'player_combat_equipment', 'equipment_upgrades', 'combat_loadouts', 'player_combat_state',
            'player_defense_structures', 'city_defense_structures', 'defense_battles', 'hunt_sessions',
            'fps_mission_results', 'city_risk_status', 'city_risk_history', 'city_invasion_events',
            'city_facility_damage', 'city_support_requests', 'city_support_contributions',
            // v7.0 현실 혜택
            'benefit_features', 'benefit_feature_history', 'web_login_codes', 'web_sessions',
            'city_pass_memberships', 'partner_merchants', 'partner_branches', 'partner_offers', 'benefit_coupons', 'coupon_redemptions',
            'coupon_events', 'merchant_users', 'merchant_sessions', 'merchant_audit_logs',
            'scholarship_programs', 'scholarship_eligibility_rules', 'scholarship_criteria', 'scholarship_applications',
            'scholarship_applicant_profiles', 'scholarship_documents', 'scholarship_consents', 'scholarship_reviewers', 'scholarship_reviews',
            'scholarship_awards', 'scholarship_appeals', 'scholarship_disbursement_records', 'scholarship_audit_logs',
            // v9.0 도시 성장 · 협동 방어전 · 지방세
            'city_population_stats', 'city_population_daily', 'city_resident_marks', 'city_residency_events', 'city_growth_notifications',
            'city_modeling_projects', 'city_modeling_assets',
            'defense_sessions', 'defense_session_members', 'defense_tactical_pings', 'defense_shared_budget', 'defense_ping_blocks',
            'city_tax_transactions', 'city_treasury_accounts', 'city_treasury_ledger', 'city_budget_proposals', 'city_budget_votes',
            'city_budget_expenditures',
        ];
        foreach ($tables as $table) {
            $db->pdo()->exec("TRUNCATE TABLE `$table`");
        }
        // 정의와 정책을 초기값으로 되돌린다: 초기 데이터 마이그레이션을 다시 적용한다.
        foreach (['combat_modes', 'combat_equipment', 'enemy_defs', 'defense_zones', 'defense_structures',
                     'hunt_zone_defs', 'fps_missions', 'city_facility_defs', 'city_risk_policy', 'city_world_state',
                     'game_config', 'city_growth_milestones', 'defense_team_roles', 'city_tax_policies'] as $table) {
            $db->pdo()->exec("TRUNCATE TABLE `$table`");
        }
        $db->pdo()->exec('UPDATE regions SET defense_points = 0, resident_count = 0');
        foreach (array_merge(glob(__DIR__ . '/../migrations/*_seed_combat*.sql'), glob(__DIR__ . '/../migrations/*_seed_benefits*.sql'),
            glob(__DIR__ . '/../migrations/*_seed_city*.sql')) as $file) {
            foreach (Migrator::split(file_get_contents($file)) as $statement) {
                $db->pdo()->exec($statement);
            }
        }
    }

    private static function assertTestDb(Db $db)
    {
        $name = (string)$db->val('SELECT DATABASE()');
        if (!preg_match('/_test$/', $name)) {
            throw new RuntimeException("테스트용 DB 가 아닙니다: $name");
        }
    }
}
