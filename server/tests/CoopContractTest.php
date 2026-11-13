<?php
use CG\Infra\Clock;
use CG\Infra\Signer;
use CG\Infra\Wire;
use CG\Service\Coop;

/**
 * v9.0 게임 서버(PHP)와 실시간 서버(.NET) 사이의 약속 (docs/COOP_DEFENSE_SYSTEM.md 4장).
 *
 * 두 서버는 다른 언어로 쓰여 있어서, 같은 표본을 양쪽 테스트가 함께 본다:
 *
 *   서명의 고정 벡터            여기와 realtime/tests/SignerTests.cs 에 같은 값이 적혀 있다.
 *   fixtures/coop-config.json   이 서버가 실시간 서버에 주는 판의 설정. 실시간 서버의 테스트가 이것을 읽어 방을 연다.
 *   fixtures/coop-report.json   실시간 서버가 전투를 돌린 뒤 보내는 결과. 이 서버가 그것을 받아들이는지 여기에서 본다.
 *
 * 표본을 새로 만들 때: CG_UPDATE_FIXTURES=1 로 이 테스트를 돌린 뒤 (config), realtime/tests 를 같은 환경변수로 돌린다 (report).
 */
final class CoopContractTest extends CityTestCase
{
    /** 테스트에만 쓰는 값이다. 실제 비밀값이 아니다. */
    const VECTOR_SECRET = 'unit-test-only-secret-000000000000000000';
    const VECTOR_TICKET = 'eyJ2IjoxLCJzaWQiOiJkX2FiY2RlZmdoMTIzNDU2IiwicGlkIjoicF8wMTIzNDU2Nzg5YWJjZCIsIm5hbWUiOiLsiJjtmLjrjIDsm5AgQS9CIiwicm9sZSI6ImJ1aWxkZXIiLCJsZXZlbCI6NywiaG9zdCI6dHJ1ZSwic3RydWN0cyI6WyJ0d3JfbWFjaGluZV9ndW4iLCJ0d3Jfc2xvdyJdLCJwaW5nX2Jsb2NrZWQiOmZhbHNlLCJleHAiOjIwMDAwMDAwMDAsIm5vbmNlIjoibjBuY2VfYWJjREVGIn0.ria6JTRyfYrqqCBEZiq_ZSo9SgRuYdtwxC8NY3m5XAE';

    const FIXTURE_A = 'p_fixture_a';
    const FIXTURE_B = 'p_fixture_b';

    public function setUp()
    {
        parent::setUp();
        $this->quiet();
        $this->enableCoop();
    }

    private static function fixture($name)
    {
        return __DIR__ . '/fixtures/' . $name;
    }

    private static function updating()
    {
        return getenv('CG_UPDATE_FIXTURES') === '1';
    }

    public function testSignerVectorsMatchTheRealtimeServer()
    {
        $signer = new Signer(self::VECTOR_SECRET);
        $payload = ['v' => 1, 'sid' => 'd_abcdefgh123456', 'pid' => 'p_0123456789abcd', 'name' => '수호대원 A/B', 'role' => 'builder', 'level' => 7,
            'host' => true, 'structs' => ['twr_machine_gun', 'twr_slow'], 'ping_blocked' => false, 'exp' => 2000000000, 'nonce' => 'n0nce_abcDEF'];
        T::eq(self::VECTOR_TICKET, $signer->ticket($payload));
        T::eq($payload, $signer->verifyTicket(self::VECTOR_TICKET));
        T::eq('t=1794538800,v1=5541373ba3cc5cf57f2a8f0e10257dfb7c905af59de1d9d39afe6e3e7c4575c8',
            $signer->signRequest('post', '/internal/coop/sessions/d_abcdefgh123456/result', '{"result":"win","waves_cleared":5}', 1794538800));
        T::eq('t=1794538800,v1=0cfc865951a7fe245092a576951a9f74e9ac2d5cf5e19b700403407fc9266619',
            $signer->signRequest('GET', '/internal/coop/sessions/d_abcdefgh123456/config', '', 1794538800));
        // 이름을 바꾸는 규칙도 양쪽이 같아야 한다 (realtime/tests 의 같은 표).
        foreach (['playerShotsPer10s' => 'player_shots_per10s', 'playerRangeM' => 'player_range_m', 'balanceAfter' => 'balance_after',
            'atMs' => 'at_ms', 'gateHpLeft' => 'gate_hp_left', 'hpScalePercentPerPlayer' => 'hp_scale_percent_per_player',
            'x' => 'x', 'winGc' => 'win_gc'] as $camel => $snake) {
            T::eq($snake, Wire::snakeKey($camel));
            T::eq($camel, Wire::camelKey($snake));
        }
    }

    public function testConfigGivenToTheRealtimeServerMatchesTheSharedFixture()
    {
        $a = $this->resident('11110', '표본대원A');
        $b = $this->resident('11110', '표본대원B');
        $coop = $this->app->coop();
        $id = $coop->create($a, 'outskirts_1', 4, null, $this->key())['session']['id'];
        $coop->join($b, $id, 'builder', $this->key());
        $config = Wire::snake($coop->internalConfig($id));

        // 판마다 달라지는 값은 정해 둔 값으로 바꾼다.
        T::ok(preg_match('/^d_[a-z0-9]{14}$/', $config['session']['id']) === 1);
        T::eq($a['publicId'], $config['session']['host_pid']);
        T::eq(900, $config['session']['expires_in_seconds'], '대기실은 15분');
        $config['session']['id'] = 'd_fixture0000000';
        $config['session']['host_pid'] = self::FIXTURE_A;
        $config['session']['expires_at'] = '2026-11-14T00:00:00Z';
        T::eq([$a['publicId'], $b['publicId']], array_column($config['members'], 'pid'));
        $config['members'][0]['pid'] = self::FIXTURE_A;
        $config['members'][1]['pid'] = self::FIXTURE_B;
        // 내부 번호나 개인정보는 넘기지 않는다.
        $text = json_encode(['ok' => true, 'data' => $config, 'server_time' => '2026-11-14T00:00:00Z'],
            JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT) . "\n";
        foreach (['"id":', 'player_id', 'email', 'token', 'secret', 'guest_key'] as $never) {
            T::ok(strpos(str_replace('"id": "d_fixture0000000"', '', $text), $never) === false, $never);
        }

        $path = self::fixture('coop-config.json');
        if (self::updating()) {
            file_put_contents($path, $text);
        }
        T::ok(is_file($path), 'fixtures/coop-config.json 이 없다. CG_UPDATE_FIXTURES=1 로 만든다.');
        T::eq(json_decode(file_get_contents($path), true), json_decode($text, true),
            '실시간 서버에 주는 설정이 바뀌었다. 실시간 서버가 읽는 쪽(realtime/src/GameApi.cs)을 확인하고 표본을 새로 만든다.');
    }

    public function testReportProducedByTheRealtimeServerIsAcceptedAndPaidOnce()
    {
        $path = self::fixture('coop-report.json');
        T::ok(is_file($path), 'fixtures/coop-report.json 이 없다. realtime/tests 를 CG_UPDATE_FIXTURES=1 로 돌려 만든다.');
        $a = $this->resident('11110', '표본대원A');
        $b = $this->resident('11110', '표본대원B');
        $coop = $this->app->coop();
        $id = $coop->create($a, 'outskirts_1', 4, null, $this->key())['session']['id'];
        $coop->join($b, $id, 'builder', $this->key());
        $coop->internalStart($id, ['by' => $a['publicId'], 'players' => [$a['publicId'], $b['publicId']]]);

        // 표본의 계정 이름을 이 판의 팀원으로 바꾼다. 나머지는 실시간 서버가 보낸 그대로다 (API 로 들어올 때와 같이 이름만 바꾼다).
        $raw = str_replace([self::FIXTURE_A, self::FIXTURE_B], [$a['publicId'], $b['publicId']], file_get_contents($path));
        $body = Wire::camel(json_decode($raw, true));
        T::ok(is_array($body) && isset($body['result'], $body['budget'], $body['members'], $body['pings']));
        T::ok(!isset($body['rewardGc']) && !isset($body['reward_gc']), '실시간 서버는 보상 금액을 보내지 않는다');
        T::ok(count($body['budget']) > 10, '장부가 비어 있지 않다');

        // 전투에 걸린 만큼 시간이 흐른 뒤에 결과가 온다.
        Clock::advance($body['durationSeconds'] + 1);
        $before = [$this->gc($a), $this->gc($b)];
        $response = $coop->internalResult($id, $body);
        T::eq(null, $response['rejectReason']);
        T::eq(true, $response['accepted']);
        T::eq($body['result'], $response['result']);
        T::eq($body['wavesCleared'], $response['wavesCleared']);
        T::eq(2, $response['rewardedPlayers']);
        $reward = $body['result'] === 'win' ? 300 : 20 * $body['wavesCleared'];
        T::eq($reward, $response['rewardGc']);
        T::eq([$before[0] + $reward, $before[1] + $reward], [$this->gc($a), $this->gc($b)]);

        // 기록이 그대로 보관된다: 공동 예산 장부, 전술 핑, 팀원별 기여.
        $session = $this->db->one('SELECT * FROM defense_sessions WHERE public_id = ?', [$id]);
        T::eq('finished', $session['status']);
        T::eq(count($body['budget']), (int)$this->db->val('SELECT COUNT(*) FROM defense_shared_budget WHERE session_id = ?', [$session['id']]));
        $last = end($body['budget']);
        T::eq($last['balanceAfter'], (int)$session['energy_left']);
        T::eq((int)$session['energy_granted'] - (int)$session['energy_spent'], (int)$session['energy_left']);
        $known = 0;
        foreach ($body['pings'] as $ping) {
            $known += isset(Coop::pingKinds()[$ping['kind']]) ? 1 : 0;
        }
        T::eq($known, (int)$this->db->val('SELECT COUNT(*) FROM defense_tactical_pings WHERE session_id = ?', [$session['id']]));
        foreach ($body['members'] as $member) {
            $row = $this->db->one(
                'SELECT m.* FROM defense_session_members m JOIN players p ON p.id = m.player_id WHERE m.session_id = ? AND p.public_id = ?',
                [$session['id'], $member['pid']]);
            T::eq($member['kills'], (int)$row['kills']);
            T::eq($member['towersBuilt'], (int)$row['towers_built']);
            T::eq($member['pingsRejected'], (int)$row['pings_rejected']);
            T::eq(1, (int)$row['eligible']);
        }

        // 같은 결과가 다시 와도 (실시간 서버의 다시 보내기) 한 번만 반영된다.
        $again = $coop->internalResult($id, $body);
        T::eq(true, $again['duplicate']);
        T::eq([$before[0] + $reward, $before[1] + $reward], [$this->gc($a), $this->gc($b)]);
        T::eq(null, $coop->findInconsistency());
        T::eq(null, $this->app->wallets()->findInconsistency($a['id']));
    }

    public function testInterruptedSessionReportIsClosedWithoutRewardOrDailyCount()
    {
        // 실시간 서버가 전투 중에 다시 떠서 이어 갈 수 없게 된 판: 걸린 시간 0, 기록 없음으로 닫는다.
        $a = $this->resident('11110');
        $b = $this->resident('11110');
        $coop = $this->app->coop();
        $id = $coop->create($a, 'outskirts_1', 4, null, $this->key())['session']['id'];
        $coop->join($b, $id, null, $this->key());
        $coop->internalStart($id, ['by' => $a['publicId'], 'players' => [$a['publicId'], $b['publicId']]]);
        Clock::advance(30);
        $before = $this->gc($a);
        $response = $coop->internalResult($id, Wire::camel(json_decode(
            '{"result":"lose","waves_cleared":0,"gate_hp_left":20,"duration_seconds":0,"close_reason":"shutdown","members":[],"budget":[],"pings":[]}',
            true)));
        T::eq(true, $response['accepted']);
        T::eq(0, $response['rewardedPlayers']);
        T::eq($before, $this->gc($a));
        $session = $this->db->one('SELECT * FROM defense_sessions WHERE public_id = ?', [$id]);
        T::eq('finished', $session['status']);
        T::eq('shutdown', $session['close_reason']);
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM defense_session_members WHERE session_id = ? AND eligible = 1", [$session['id']]));
        // 두 사람은 곧바로 새 판을 열 수 있다.
        T::ok(isset($coop->create($a, 'outskirts_1', 4, null, $this->key())['session']['id']));
        T::eq(null, $coop->findInconsistency());
    }
}
