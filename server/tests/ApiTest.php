<?php
use CG\Api\Kernel;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Env;
use CG\Infra\Request;

/**
 * /api/v1 을 요청 단위로 확인한다: 인증, 응답 형식, 오류, 그리고
 * "클라이언트가 보낸 숫자(위험도·보상·가격)를 서버가 믿지 않는다"는 것.
 */
final class ApiTest extends DbTestCase
{
    private function call($method, $path, array $body = [], $token = null, array $headers = [], array $query = [])
    {
        if ($token !== null) {
            $headers['Authorization'] = 'Bearer ' . $token;
        }
        return (new Kernel($this->app))->handle(Request::make($method, $path, $body, $headers, $query));
    }

    /** 성공해야 하는 요청. data 를 돌려준다. */
    private function ok($method, $path, array $body = [], $token = null, array $headers = [], array $query = [])
    {
        list($status, $response) = $this->call($method, $path, $body, $token, $headers, $query);
        T::same(200, $status, $method . ' ' . $path . ' → ' . json_encode($response, JSON_UNESCAPED_UNICODE));
        T::same(true, $response['ok']);
        return $response['data'];
    }

    /** 실패해야 하는 요청. error 를 돌려준다. */
    private function fail($expectedStatus, $expectedCode, $method, $path, array $body = [], $token = null, array $headers = [])
    {
        list($status, $response) = $this->call($method, $path, $body, $token, $headers);
        T::same(false, $response['ok'], $method . ' ' . $path . ' 가 성공했다');
        T::same($expectedCode, $response['error']['code'], $method . ' ' . $path);
        T::same($expectedStatus, $status);
        T::ok(!isset($response['data']));
        return $response['error'];
    }

    private function newGuest()
    {
        return $this->ok('POST', '/auth/guest', [], null, ['X-Client-Platform' => 'windows', 'X-Client-Version' => '8.0.0']);
    }

    private function newResident($regionCode = '11110', $nickname = '테스트가디언')
    {
        $guest = $this->newGuest();
        $this->ok('POST', '/residency', ['region_code' => $regionCode, 'nickname' => $nickname], $guest['access_token']);
        return $guest;
    }

    private function idem()
    {
        return ['Idempotency-Key' => $this->key()];
    }

    // ── 응답 형식 ───────────────────────────────────────────

    public function testHealthNeedsNoLoginAndShowsNoInternals()
    {
        list($status, $response) = $this->call('GET', '/health');
        T::same(200, $status);
        T::same(true, $response['ok']);
        T::same('ok', $response['data']['status']);
        T::same(\CG\Api\Kernel::VERSION, $response['data']['version']);
        T::ok(preg_match('/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/', $response['server_time']) === 1, '시각은 UTC ISO-8601');
        $text = json_encode($response);
        foreach (['DB_', 'password', 'city_guardians', '127.0.0.1', 'root'] as $secret) {
            T::ok(stripos($text, $secret) === false, "상태 응답에 내부 정보가 있다: $secret");
        }
    }

    public function testUnknownPathsAndWrongMethodsAreRefused()
    {
        $this->fail(404, ApiError::NOT_FOUND, 'GET', '/nope');
        $this->fail(400, ApiError::INVALID_REQUEST, 'DELETE', '/health');
        $this->fail(404, ApiError::NOT_FOUND, 'GET', '/cities/../../etc/passwd');
        $guest = $this->newGuest();
        $this->fail(404, ApiError::NOT_FOUND, 'GET', "/cities/11110' OR '1'='1", [], $guest['access_token']);
    }

    public function testOutputNamesAreSnakeCase()
    {
        $guest = $this->newGuest();
        $me = $this->ok('GET', '/me', [], $guest['access_token']);
        foreach (['player_id', 'display_name', 'region_code', 'xp_next', 'hp_max', 'daily_hunt_gc_limit', 'unlocked_towers',
                     'cleared_fps_missions', 'city_contribution', 'support_region_code'] as $key) {
            T::ok(array_key_exists($key, $me), "/me 에 $key 가 없다");
        }
        T::same('stranger', $me['residency']);
        T::same(0, $me['gc']);
        $walk = function ($value) use (&$walk) {
            if (!is_array($value)) {
                return;
            }
            foreach ($value as $key => $child) {
                if (is_string($key)) {
                    T::ok(preg_match('/[A-Z]/', $key) === 0, "camelCase 이름이 나갔다: $key");
                }
                $walk($child);
            }
        };
        $walk($me);
        $walk($this->ok('GET', '/combat/overview', [], $guest['access_token']));
        $walk($this->ok('GET', '/cities/risk', [], $guest['access_token']));
    }

    public function testAnUnexpectedErrorIsLoggedAndShowsNothingInternal()
    {
        $guest = $this->newGuest();
        // 서버 안에서 예상하지 못한 오류가 나게 한다: 테이블 하나의 이름을 잠시 바꾼다.
        $this->db->pdo()->exec('RENAME TABLE combat_modes TO combat_modes_gone');
        try {
            list($status, $response) = $this->call('GET', '/combat/modes', [], $guest['access_token']);
        } finally {
            $this->db->pdo()->exec('RENAME TABLE combat_modes_gone TO combat_modes');
        }
        T::same(500, $status);
        T::same(ApiError::INTERNAL, $response['error']['code']);
        $text = json_encode($response, JSON_UNESCAPED_UNICODE);
        foreach (['combat_modes', 'SQL', 'PDO', '.php', 'server\\\\src', 'Stack'] as $leak) {
            T::ok(stripos($text, $leak) === false, "오류 응답에 내부 정보가 있다: $leak");
        }
        $log = $this->db->one("SELECT * FROM system_logs WHERE source = 'api' ORDER BY id DESC LIMIT 1");
        T::ok($log !== null, '서버 로그에는 남아야 한다');
        T::ok(strpos($log['message'], 'combat_modes') !== false);
    }

    // ── 인증 ────────────────────────────────────────────────

    public function testGuestGetsATokenAndOnlyHashesAreStored()
    {
        $guest = $this->newGuest();
        T::ok(preg_match('/^p_[a-z0-9]{14}$/', $guest['player_id']) === 1);
        T::ok(preg_match('/^[0-9a-f]{64}$/', $guest['guest_secret']) === 1);
        T::ok(preg_match('/^[0-9a-f]{64}$/', $guest['access_token']) === 1);
        T::same(86400, $guest['expires_in']);

        // 비밀키와 토큰은 그대로 저장하지 않는다.
        $dump = json_encode([
            $this->db->all('SELECT * FROM player_credentials'),
            $this->db->all('SELECT * FROM player_sessions'),
            $this->db->all('SELECT * FROM players'),
        ]);
        T::ok(strpos($dump, $guest['guest_secret']) === false, '비밀키가 그대로 저장되었다');
        T::ok(strpos($dump, $guest['access_token']) === false, '토큰이 그대로 저장되었다');

        T::same($guest['player_id'], $this->ok('GET', '/me', [], $guest['access_token'])['player_id']);
    }

    public function testRequestsWithoutAValidTokenAreRefused()
    {
        $guest = $this->newGuest();
        $this->fail(401, ApiError::UNAUTHORIZED, 'GET', '/me');
        $this->fail(401, ApiError::UNAUTHORIZED, 'GET', '/me', [], str_repeat('a', 64));
        $this->fail(401, ApiError::UNAUTHORIZED, 'GET', '/me', [], 'not-a-token');
        $this->fail(401, ApiError::UNAUTHORIZED, 'GET', '/me', [], null, ['Authorization' => 'Basic ' . $guest['access_token']]);
        // 토큰의 해시를 알아도 접속할 수 없다.
        $hash = $this->db->val('SELECT token_hash FROM player_sessions LIMIT 1');
        $this->fail(401, ApiError::UNAUTHORIZED, 'GET', '/me', [], $hash);
        // 로그인이 필요한 경로는 모두 막혀 있다.
        foreach (['/defs', '/regions', '/combat/modes', '/combat/equipment', '/defense/zones', '/hunt/zones', '/fps/missions',
                     '/cities/risk', '/cities/rankings', '/cities/invasions', '/cities/support', '/cities/11110', '/cities/11110/history'] as $path) {
            $this->fail(401, ApiError::UNAUTHORIZED, 'GET', $path);
        }
        foreach (['/residency', '/combat/sessions', '/defense/battles', '/hunt/sessions', '/fps/missions/downtown_1/start',
                     '/cities/facilities/fac_power/repair', '/cities/structures/twr_command_center/fund', '/auth/logout'] as $path) {
            $this->fail(401, ApiError::UNAUTHORIZED, 'POST', $path);
        }
    }

    public function testTokenExpiresAndCanBeRenewedWithTheGuestSecret()
    {
        $guest = $this->newGuest();
        Clock::advance(86400 - 5);
        $this->ok('GET', '/me', [], $guest['access_token']);
        Clock::advance(10);
        $this->fail(401, ApiError::UNAUTHORIZED, 'GET', '/me', [], $guest['access_token']);

        $this->fail(401, ApiError::UNAUTHORIZED, 'POST', '/auth/token',
            ['player_id' => $guest['player_id'], 'guest_secret' => str_repeat('0', 64)]);
        $this->fail(401, ApiError::UNAUTHORIZED, 'POST', '/auth/token',
            ['player_id' => 'p_aaaaaaaaaaaaaa', 'guest_secret' => $guest['guest_secret']]);
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', '/auth/token', ['player_id' => $guest['player_id']]);

        $renewed = $this->ok('POST', '/auth/token', ['player_id' => $guest['player_id'], 'guest_secret' => $guest['guest_secret']]);
        T::ok($renewed['access_token'] !== $guest['access_token']);
        T::ok(!isset($renewed['guest_secret']), '비밀키는 처음 한 번만 돌려준다');
        T::same($guest['player_id'], $this->ok('GET', '/me', [], $renewed['access_token'])['player_id']);
    }

    public function testLogoutRevokesOnlyThatToken()
    {
        $guest = $this->newGuest();
        $second = $this->ok('POST', '/auth/token', ['player_id' => $guest['player_id'], 'guest_secret' => $guest['guest_secret']]);
        $this->ok('POST', '/auth/logout', [], $guest['access_token']);
        $this->fail(401, ApiError::UNAUTHORIZED, 'GET', '/me', [], $guest['access_token']);
        $this->ok('GET', '/me', [], $second['access_token']);
    }

    public function testASuspendedAccountIsShutOut()
    {
        $guest = $this->newGuest();
        $this->db->exec("UPDATE players SET status = 'suspended' WHERE public_id = ?", [$guest['player_id']]);
        $this->fail(403, ApiError::SUSPENDED, 'GET', '/me', [], $guest['access_token']);
        $this->fail(403, ApiError::SUSPENDED, 'POST', '/auth/token',
            ['player_id' => $guest['player_id'], 'guest_secret' => $guest['guest_secret']]);
    }

    public function testAccountCreationAndLoginAreRateLimitedPerAddress()
    {
        $app = new \CG\App(Env::fromArray(['GUEST_PER_HOUR' => '3', 'TOKEN_PER_10MIN' => '2']), $this->db);
        $kernel = new Kernel($app);
        for ($i = 0; $i < 3; $i++) {
            list($status) = $kernel->handle(Request::make('POST', '/auth/guest'));
            T::same(200, $status);
        }
        list($status, $response) = $kernel->handle(Request::make('POST', '/auth/guest'));
        T::same(429, $status);
        T::same(ApiError::RATE_LIMITED, $response['error']['code']);
        T::eq(3, $this->db->val('SELECT COUNT(*) FROM players'), '막힌 요청은 계정을 만들지 않는다');

        // 비밀키를 계속 찍어 보는 것도 막는다.
        for ($i = 0; $i < 2; $i++) {
            list($status) = $kernel->handle(Request::make('POST', '/auth/token', ['player_id' => 'p_aaaaaaaaaaaaaa', 'guest_secret' => 'x']));
            T::same(401, $status);
        }
        list($status) = $kernel->handle(Request::make('POST', '/auth/token', ['player_id' => 'p_aaaaaaaaaaaaaa', 'guest_secret' => 'x']));
        T::same(429, $status);

        // 주소는 해시로만 남는다.
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM rate_limits WHERE bucket LIKE '%127.0.0.1%'"));
        // 시간 창이 지나면 다시 쓸 수 있다.
        Clock::advance(3600);
        list($status) = $kernel->handle(Request::make('POST', '/auth/guest'));
        T::same(200, $status);
    }

    public function testMaintenanceAndMinimumClientVersion()
    {
        $guest = $this->newGuest();
        $app = new \CG\App(Env::fromArray(['MAINTENANCE' => '1']), $this->db);
        list($status, $response) = (new Kernel($app))->handle(Request::make('GET', '/me', [], ['Authorization' => 'Bearer ' . $guest['access_token']]));
        T::same(503, $status);
        T::same(ApiError::MAINTENANCE, $response['error']['code']);
        list($status, $response) = (new Kernel($app))->handle(Request::make('GET', '/health'));
        T::same(200, $status);
        T::same(true, $response['data']['maintenance']);

        $app = new \CG\App(Env::fromArray(['MIN_CLIENT_VERSION' => '8.0.0']), $this->db);
        $headers = ['Authorization' => 'Bearer ' . $guest['access_token']];
        list($status, $response) = (new Kernel($app))->handle(Request::make('GET', '/me', [], $headers + ['X-Client-Version' => '7.9.9']));
        T::same(426, $status);
        T::same(ApiError::UPDATE_REQUIRED, $response['error']['code']);
        list($status) = (new Kernel($app))->handle(Request::make('GET', '/me', [], $headers + ['X-Client-Version' => '8.0.1']));
        T::same(200, $status);
    }

    // ── 한 플레이어의 흐름 ──────────────────────────────────

    public function testANewGuardianCanPlayAllThreeModesAndSeeTheCityChange()
    {
        $this->quiet();
        $guest = $this->newGuest();
        $token = $guest['access_token'];

        // 시작 데이터
        $defs = $this->ok('GET', '/defs', [], $token);
        T::eq(3, count($defs['combat']['modes']));
        T::eq(15, count($defs['towers']));
        T::same(true, $this->ok('GET', '/defs', [], $token, [], ['version' => $defs['version']])['unchanged']);
        $regions = $this->ok('GET', '/regions', [], $token);
        T::eq(16, count($regions['sidos']));
        T::eq(230, count($regions['regions']));

        // 전입신고
        $check = $this->ok('POST', '/residency/check-nickname', ['nickname' => '새가디언'], $token);
        T::same(true, $check['available']);
        $moved = $this->ok('POST', '/residency', ['region_code' => '11110', 'nickname' => '새가디언'], $token);
        T::same(500, $moved['bonus_gc']);
        T::same('resident', $moved['me']['residency']);
        T::same('11110', $moved['me']['region_code']);
        T::same('새가디언', $moved['me']['display_name']);
        T::same(500, $moved['me']['gc']);
        $this->fail(409, ApiError::CONFLICT, 'POST', '/residency', ['region_code' => '11140', 'nickname' => '또신고'], $token);
        $riskBefore = $this->ok('GET', '/cities/11110', [], $token)['status']['risk'];

        // 디펜스
        $zones = $this->ok('GET', '/defense/zones', [], $token);
        T::ok(count($zones['zones']) >= 1);
        $battle = $this->ok('POST', '/defense/battles', ['zone_code' => 'outskirts_1'], $token);
        T::ok(preg_match('/^b_/', $battle['session_id']) === 1);
        Clock::advance(60);
        $won = $this->ok('POST', '/defense/battles/' . $battle['session_id'] . '/finish', [
            'result' => 'win', 'base_hp_left' => 20, 'waves_cleared' => 5,
            'kills' => [['enemy_code' => 'malfunction_robot', 'count' => 60], ['enemy_code' => 'mech_insect', 'count' => 134],
                ['enemy_code' => 'construction_mecha', 'count' => 1]],
        ], $token);
        T::same(500, $won['reward_gc']);
        T::same(1000, $won['gc']);
        T::same(-300, $won['city']['risk_delta_centi']);

        // 헌트 (공통 진입점으로 시작)
        $hunt = $this->ok('POST', '/combat/sessions', ['mode' => 'hunt', 'code' => 'zone_ne'], $token);
        Clock::advance(60);
        $report = $this->ok('POST', '/hunt/sessions/' . $hunt['session_id'] . '/report',
            ['seq' => 1, 'kills' => [['enemy_code' => 'malfunction_robot', 'count' => 10]]], $token);
        T::same(100, $report['reward_gc']);
        T::same(-100, $report['city']['risk_delta_centi']);

        // FPS
        $missions = $this->ok('GET', '/fps/missions', [], $token);
        T::ok(count($missions['missions']) >= 1);
        $fps = $this->ok('POST', '/fps/missions/downtown_1/start', [], $token);
        Clock::advance(70);
        $cleared = $this->ok('POST', '/fps/sessions/' . $fps['session_id'] . '/finish', [
            'result' => 'win', 'facilities_saved' => 3, 'civilians_saved' => 4,
            'kills' => [['enemy_code' => 'malfunction_robot', 'count' => 8], ['enemy_code' => 'mech_insect', 'count' => 10]],
        ], $token);
        T::same('win', $cleared['result']);
        T::same(-400, $cleared['city']['risk_delta_centi']);

        // 세 모드의 결과가 하나의 계정과 하나의 도시에 모인다.
        $me = $this->ok('GET', '/me', [], $token);
        T::same(1000 + 100 + $cleared['reward_gc'], $me['gc']);
        T::ok($me['level'] >= 2);
        T::same(10 + 1 + 12, $me['city_contribution']);
        $detail = $this->ok('GET', '/cities/11110', [], $token);
        T::same($riskBefore - 8, $detail['status']['risk']);
        T::same(true, $detail['is_home']);
        T::eq(3, count($detail['history']));
        $rewards = $this->ok('GET', '/combat/rewards', [], $token);
        T::ok(count($rewards['recent']) >= 3);
        $history = $this->ok('GET', '/cities/11110/history', [], $token, [], ['limit' => '2']);
        T::eq(2, count($history['entries']));
        $rankings = $this->ok('GET', '/cities/rankings', [], $token);
        T::same('11110', $rankings['rows'][0]['region_code']);
        $this->ok('GET', '/cities/invasions', [], $token);
        $this->ok('GET', '/fps/invasions', [], $token);
        $this->ok('GET', '/cities/support', [], $token);
        $this->ok('GET', '/cities/support/contributions', [], $token);
        T::same(null, $this->app->city()->findInconsistency());
    }

    // ── 클라이언트가 보낸 숫자를 믿지 않는다 ────────────────

    public function testAClientCannotSetRiskRewardOrPriceByAddingFields()
    {
        $this->quiet();
        $guest = $this->newResident();
        $token = $guest['access_token'];
        $honest = $this->newResident('11140', '정직한가디언');

        $forged = ['risk' => 0, 'risk_centi' => 0, 'risk_delta_centi' => -9999, 'reward_gc' => 999999, 'gc' => 999999,
            'xp' => 999999, 'level' => 99, 'contribution' => 99999, 'defense_points' => 99999, 'region_code' => '11140',
            'player_id' => $honest['player_id'], 'price_gc' => 0, 'cost_gc' => 0, 'duplicate' => true];

        $battle = $this->ok('POST', '/defense/battles', ['zone_code' => 'outskirts_1'] + $forged, $token);
        Clock::advance(60);
        $won = $this->ok('POST', '/defense/battles/' . $battle['session_id'] . '/finish', $forged + [
            'result' => 'win', 'base_hp_left' => 20, 'waves_cleared' => 5,
            'kills' => [['enemy_code' => 'malfunction_robot', 'count' => 60], ['enemy_code' => 'mech_insect', 'count' => 134],
                ['enemy_code' => 'construction_mecha', 'count' => 1]],
        ], $token);

        // 보상·위험도·기여도는 서버의 정의대로다.
        T::same(500, $won['reward_gc']);
        T::same(-300, $won['city']['risk_delta_centi']);
        T::same('11110', $won['city']['region_code']);
        $me = $this->ok('GET', '/me', [], $token);
        T::same(1000, $me['gc']);
        T::ok($me['level'] < 10);
        T::same(10, $me['city_contribution']);
        // 다른 사람의 계정과 도시에는 아무 일도 없다.
        $other = $this->ok('GET', '/me', [], $honest['access_token']);
        T::same(500, $other['gc']);
        T::same(0, $other['city_contribution']);
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM city_risk_history h JOIN regions r ON r.id = h.region_id WHERE r.code = ?', ['11140']));

        // 가격도 서버의 정의대로 빠진다.
        $bought = $this->ok('POST', '/defense/structures/twr_blast/purchase', $forged, $token, $this->idem());
        T::same(500, $bought['cost_gc']);
        T::same(500, $bought['gc']);
    }

    public function testThereIsNoRouteThatWritesRiskDirectly()
    {
        $this->quiet();
        $guest = $this->newResident();
        $token = $guest['access_token'];
        $risk = $this->riskCenti('11110');
        foreach (['POST', 'PUT', 'PATCH', 'DELETE'] as $method) {
            foreach (['/cities/11110', '/cities/risk', '/cities/11110/risk', '/cities/11110/history', '/cities/rankings', '/cities/invasions'] as $path) {
                list($status, $response) = $this->call($method, $path, ['risk' => 0, 'risk_centi' => 0, 'delta' => -100], $token, $this->idem());
                T::same(false, $response['ok'], "$method $path");
                T::ok($status === 400 || $status === 404, "$method $path → $status");
            }
        }
        T::same($risk, $this->riskCenti('11110'));
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE actor IN ('player', 'supporter')"));
    }

    public function testMalformedBodiesAreRefusedBeforeAnythingChanges()
    {
        $this->quiet();
        $guest = $this->newResident();
        $token = $guest['access_token'];
        $battle = $this->ok('POST', '/defense/battles', ['zone_code' => 'outskirts_1'], $token);
        Clock::advance(60);
        $path = '/defense/battles/' . $battle['session_id'] . '/finish';
        $kills = [['enemy_code' => 'malfunction_robot', 'count' => 60]];

        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path, ['result' => 'win', 'kills' => 'many'], $token);
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path,
            ['result' => 'win', 'base_hp_left' => 20, 'waves_cleared' => 5, 'kills' => [['enemy_code' => 'malfunction_robot', 'count' => '60']]], $token);
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path,
            ['result' => 'win', 'base_hp_left' => 20, 'waves_cleared' => 5, 'kills' => [['enemy_code' => 'malfunction_robot', 'count' => 1.5]]], $token);
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path,
            ['result' => 'win', 'base_hp_left' => '20', 'waves_cleared' => 5, 'kills' => $kills], $token);
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path,
            ['result' => ['win'], 'base_hp_left' => 20, 'waves_cleared' => 5, 'kills' => $kills], $token);
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', '/defense/battles', ['zone_code' => ['outskirts_1']], $token);
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', '/defense/battles', [], $token);
        $this->fail(404, ApiError::NOT_FOUND, 'POST', '/defense/battles/b_zzzzzzzzzzzzzz/finish',
            ['result' => 'win', 'base_hp_left' => 20, 'waves_cleared' => 5, 'kills' => $kills], $token);

        // 읽을 수 없는 본문
        $request = Request::make('POST', '/defense/battles', [], ['Authorization' => 'Bearer ' . $token]);
        $request->badJson = true;
        list($status, $response) = (new Kernel($this->app))->handle($request);
        T::same(400, $status);
        T::same(ApiError::INVALID_REQUEST, $response['error']['code']);

        // 형식은 맞지만 뜻이 통하지 않는 결과: 보고는 받되 인정하지 않는다 (보상도, 도시 반영도 없다).
        $odd = $this->ok('POST', $path, ['result' => 'draw', 'base_hp_left' => 20, 'waves_cleared' => 5, 'kills' => $kills], $token);
        T::same(false, $odd['accepted']);
        T::same(0, $odd['reward_gc']);
        T::same(null, $odd['city']);

        T::same(500, $this->ok('GET', '/me', [], $token)['gc'], '거절된 보고로는 아무것도 바뀌지 않는다');
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM city_risk_history WHERE actor = 'player'"));
    }

    public function testGcChangingRequestsNeedAnIdempotencyKey()
    {
        $this->quiet();
        $guest = $this->newResident();
        $token = $guest['access_token'];

        foreach (['/defense/structures/twr_blast/purchase', '/combat/equipment/gear_energy_blaster/upgrade',
                     '/combat/equipment/gear_precision_rifle/purchase', '/cities/facilities/fac_power/repair'] as $path) {
            $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path, [], $token);
            $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path, [], $token, ['Idempotency-Key' => 'short']);
            $this->fail(400, ApiError::INVALID_REQUEST, 'POST', $path, [], $token, ['Idempotency-Key' => "bad key'; --"]);
        }
        $this->fail(400, ApiError::INVALID_REQUEST, 'POST', '/cities/structures/twr_command_center/fund', ['amount_gc' => 100], $token);
        T::same(500, $this->ok('GET', '/me', [], $token)['gc']);

        // 같은 키로 다시 보내면 한 번만 처리한다.
        $key = $this->idem();
        $first = $this->ok('POST', '/defense/structures/twr_blast/purchase', [], $token, $key);
        $again = $this->ok('POST', '/defense/structures/twr_blast/purchase', [], $token, $key);
        T::same(0, $first['gc']);
        T::same(true, $again['duplicate']);
        T::same(0, $this->ok('GET', '/me', [], $token)['gc']);
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE reason = 'tower_unlock'"));
        // 같은 키를 다른 일에 쓰면 거절한다.
        $this->fail(409, ApiError::IDEMPOTENCY_MISMATCH, 'POST', '/combat/equipment/gear_energy_blaster/upgrade', [], $token, $key);
    }

    public function testErrorsCarryAReasonCodeTheClientCanAct()
    {
        $this->quiet();
        $guest = $this->newResident();
        $token = $guest['access_token'];
        $error = $this->fail(422, ApiError::LOCKED, 'POST', '/combat/equipment/gear_precision_rifle/purchase', [], $token, $this->idem());
        T::ok(isset($error['reason']) && $error['reason'] !== '', '이유 코드가 있어야 한다');
        T::ok(mb_strlen($error['message']) > 0);

        $stranger = $this->newGuest();
        $this->fail(422, ApiError::LOCKED, 'POST', '/cities/facilities/fac_power/repair', [], $stranger['access_token'], $this->idem());
        $this->fail(404, ApiError::NOT_FOUND, 'GET', '/cities/99999', [], $token);
        $this->fail(404, ApiError::NOT_FOUND, 'POST', '/fps/missions/nope/start', [], $token);
        $this->fail(404, ApiError::NOT_FOUND, 'POST', '/cities/support/sup_12345/join', [], $token);
    }

    public function testOnePlayerCannotFinishAnotherPlayersSession()
    {
        $this->quiet();
        $owner = $this->newResident('11110', '주인');
        $thief = $this->newResident('11140', '남의것');
        $battle = $this->ok('POST', '/defense/battles', ['zone_code' => 'outskirts_1'], $owner['access_token']);
        Clock::advance(60);
        $report = ['result' => 'win', 'base_hp_left' => 20, 'waves_cleared' => 5,
            'kills' => [['enemy_code' => 'malfunction_robot', 'count' => 60], ['enemy_code' => 'mech_insect', 'count' => 134],
                ['enemy_code' => 'construction_mecha', 'count' => 1]]];
        $this->fail(404, ApiError::NOT_FOUND, 'POST', '/defense/battles/' . $battle['session_id'] . '/finish', $report, $thief['access_token']);
        $this->fail(404, ApiError::NOT_FOUND, 'POST', '/combat/sessions/' . $battle['session_id'] . '/result', $report, $thief['access_token']);
        T::same(500, $this->ok('GET', '/me', [], $thief['access_token'])['gc']);
        // 주인은 그대로 끝낼 수 있다.
        T::same(500, $this->ok('POST', '/combat/sessions/' . $battle['session_id'] . '/result', $report, $owner['access_token'])['reward_gc']);
    }

    public function testLoadoutIsChangedThroughTheApiAndSharedByTheAccount()
    {
        $this->quiet();
        $guest = $this->newResident();
        $token = $guest['access_token'];
        $equipment = $this->ok('GET', '/combat/equipment', [], $token);
        T::eq(8, count($equipment['equipment']));
        $this->fail(422, ApiError::VALIDATION_FAILED, 'PUT', '/combat/loadouts',
            ['mode' => 'fps', 'slot' => 'primary', 'gear_code' => 'gear_precision_rifle'], $token);   // 갖고 있지 않다
        $this->ok('PUT', '/combat/loadouts', ['mode' => 'fps', 'slot' => 'gadget', 'gear_code' => 'gear_heal_device'], $token);
        $modes = $this->ok('GET', '/combat/modes', [], $token);
        T::eq(3, count($modes['modes']));
    }
}
