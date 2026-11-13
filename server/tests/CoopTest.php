<?php
use CG\Admin\Auth;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Env;
use CG\Infra\Request;
use CG\Infra\Signer;
use CG\Service\Coop;
use CG\Service\Taxes;

/**
 * v9.0 협동 타워디펜스 — 게임 서버 쪽: 판에 들어가고 나가기, 역할, 입장권, 실시간 서버의 서명한 요청, 결과 반영.
 * 전투 중의 동기화(동시 타워 설치, 미니맵 위치, 공동 예산의 원자적 차감)는 실시간 서버의 테스트가 맡는다 (realtime/tests).
 * 여기서는 그 결과가 게임 서버에 "한 번만, 검증된 뒤에" 반영되는지를 본다.
 */
final class CoopTest extends CityTestCase
{
    public function setUp()
    {
        parent::setUp();
        $this->quiet();
    }

    private function pids(array $team)
    {
        return array_map(function ($player) {
            return $player['publicId'];
        }, $team);
    }

    /** 대기실을 만든다. [판의 공개 ID, 팀원들(첫 사람이 방장)] */
    private function lobby($size = 4, $regionCode = '11110')
    {
        $team = [];
        for ($i = 0; $i < $size; $i++) {
            $team[] = $this->resident($regionCode);
        }
        $coop = $this->app->coop();
        $id = $coop->create($team[0], 'outskirts_1', max(4, $size), null, $this->key())['session']['id'];
        for ($i = 1; $i < $size; $i++) {
            $coop->join($team[$i], $id, null, $this->key());
        }
        return [$id, $team];
    }

    /** 대기실을 만들고 전투를 시작한다. */
    private function running($size = 4, $regionCode = '11110')
    {
        list($id, $team) = $this->lobby($size, $regionCode);
        $started = $this->app->coop()->internalStart($id, ['by' => $team[0]['publicId'], 'players' => $this->pids($team)]);
        T::eq('running', $started['status']);
        return [$id, $team];
    }

    /** 이미 있는 팀으로 새 판을 열고 시작한다 (앞의 판이 끝난 뒤). */
    private function again(array $team)
    {
        $coop = $this->app->coop();
        $id = $coop->create($team[0], 'outskirts_1', max(2, count($team)), null, $this->key())['session']['id'];
        for ($i = 1; $i < count($team); $i++) {
            $coop->join($team[$i], $id, null, $this->key());
        }
        $coop->internalStart($id, ['by' => $team[0]['publicId'], 'players' => $this->pids($team)]);
        return $id;
    }

    /** 공동 예산 장부를 만든다. 줄: [종류, 변화량, 쓴 사람의 공개 ID, 자리, 타워 코드] */
    private static function ledger(array $steps)
    {
        $balance = 0;
        $lines = [];
        foreach ($steps as $index => $step) {
            $balance += $step[1];
            $lines[] = ['seq' => $index + 1, 'pid' => isset($step[2]) ? $step[2] : null, 'kind' => $step[0], 'delta' => $step[1],
                'balanceAfter' => $balance, 'slot' => isset($step[3]) ? $step[3] : null, 'code' => isset($step[4]) ? $step[4] : null,
                'atMs' => $index * 1000];
        }
        return $lines;
    }

    /** 실시간 서버가 보내는 결과 (서버 안의 이름으로). */
    private function report(array $team, array $overrides = [])
    {
        $members = [];
        foreach ($team as $index => $player) {
            $members[] = ['pid' => $player['publicId'], 'role' => null, 'connectedSeconds' => 120, 'kills' => 10 + $index, 'towersBuilt' => 1,
                'towersUpgraded' => 0, 'repairs' => 0, 'damageDealt' => 500, 'pingsSent' => 1, 'pingsRejected' => 0];
        }
        $a = $team[0]['publicId'];
        $b = $team[1]['publicId'];
        return array_merge([
            'result' => 'win', 'wavesCleared' => 5, 'gateHpLeft' => 14, 'durationSeconds' => 120, 'closeReason' => 'completed',
            'members' => $members,
            'budget' => self::ledger([
                ['grant_start', 400], ['build', -100, $a, 3, 'twr_machine_gun'], ['build', -100, $b, 7, 'twr_machine_gun'],
                ['grant_kill', 36], ['upgrade', -60, $a, 3, 'twr_machine_gun'], ['grant_wave', 100], ['repair', -10, $b, 7, 'twr_machine_gun'],
            ]),
            'pings' => [
                ['pid' => $a, 'kind' => 'focus_fire', 'x' => 1250, 'z' => -300, 'atMs' => 5000, 'accepted' => true],
                ['pid' => $b, 'kind' => 'repair_here', 'x' => 0, 'z' => 800, 'atMs' => 9000, 'accepted' => true],
                ['pid' => $b, 'kind' => 'repair_here', 'x' => 0, 'z' => 800, 'atMs' => 9100, 'accepted' => false],
            ],
        ], $overrides);
    }

    private function sessionRow($publicId)
    {
        return $this->db->one('SELECT * FROM defense_sessions WHERE public_id = ?', [$publicId]);
    }

    private function consistent()
    {
        T::eq(null, $this->app->coop()->findInconsistency());
        T::eq(null, $this->app->city()->findInconsistency());
        T::eq(null, $this->app->treasury()->findInconsistency());
    }

    // ── 열려 있는가 ─────────────────────────────────────────

    public function testCoopIsOffByDefaultAndNeedsTheRealtimeServerToBeConfigured()
    {
        $player = $this->resident('11110');
        $coop = $this->app->coop();
        $config = $coop->config($player);
        T::eq(false, $config['available']);
        T::eq('coop_disabled', $config['unavailableReason']);
        T::eq(4, $config['defaultPlayers']);
        T::eq(8, $config['maxPlayers']);
        T::eq(false, $config['roleRequired']);
        T::eq(4, count($config['roles']));
        T::eq(6, count($config['ping']['kinds']));
        T::eq('coop_disabled', T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($coop, $player) {
            $coop->create($player, 'outskirts_1', null, null, $this->key());
        })->reason);

        // 설정에서 켜도, 실시간 서버와 나눠 가질 비밀값이나 주소가 없으면 열리지 않는다.
        $this->enableCoop();
        T::eq(null, $coop->unavailableReason());
        $base = ['APP_ENV' => 'test', 'REALTIME_SECRET' => str_repeat('k', 40), 'REALTIME_URL' => 'wss://rt.cg.test/ws'];
        foreach ([
            ['REALTIME_SECRET' => ''], ['REALTIME_SECRET' => 'too-short'], ['REALTIME_URL' => ''],
            ['REALTIME_URL' => 'ws://rt.cg.test/ws'],                 // 암호화하지 않은 연결은 내 PC 에서만
            ['REALTIME_URL' => 'https://rt.cg.test/ws'], ['REALTIME_URL' => 'wss://rt.cg.test/ws?x=<script>'],
        ] as $broken) {
            $app = new \CG\App(Env::fromArray(array_merge($base, $broken)), $this->db);
            T::eq('realtime_not_configured', $app->coop()->unavailableReason(), json_encode($broken));
        }
        foreach (['wss://rt.cg.test', 'wss://rt.cg.test:8443/ws', 'ws://127.0.0.1:5199/ws', 'ws://localhost:5199'] as $url) {
            $app = new \CG\App(Env::fromArray(array_merge($base, ['REALTIME_URL' => $url])), $this->db);
            T::eq(null, $app->coop()->unavailableReason(), $url);
        }
    }

    // ── 방어전 생성과 참여 ──────────────────────────────────

    public function testCreateJoinLeaveAndHostHandover()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        $host = $this->resident('11110');
        $stranger = $this->guest();
        T::eq('not_resident', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $stranger) {
            $coop->create($stranger, 'outskirts_1', null, null, $this->key());
        })->reason);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($coop, $host) {
            $coop->create($host, 'nowhere', null, null, $this->key());
        });
        // 정원: 2 ~ 8명. 처음에는 4명, 8명까지 넓힐 수 있다.
        foreach ([1, 9, 100] as $bad) {
            T::throwsApi(ApiError::INVALID_REQUEST, function () use ($coop, $host, $bad) {
                $coop->create($host, 'outskirts_1', $bad, null, $this->key());
            });
        }

        $key = $this->key();
        $session = $coop->create($host, 'outskirts_1', null, null, $key)['session'];
        T::ok(preg_match('/^d_[a-z0-9]{14}$/', $session['id']) === 1);
        T::eq('lobby', $session['status']);
        T::eq(4, $session['maxPlayers']);
        T::eq(1, $session['players']);
        T::eq(true, $session['isHost']);
        T::eq('11110', $session['regionCode']);
        T::eq(null, $session['members'][0]['roleCode'], '역할은 고르지 않아도 된다');
        T::eq(true, $coop->create($host, 'outskirts_1', null, null, $key)['duplicate']);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM defense_sessions'));
        // 판을 여는 데 GC 는 들지 않는다.
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND delta < 0', [$host['id']]));

        $second = $this->resident('11110');
        $third = $this->resident('11110');
        $fourth = $this->resident('11110');
        $fifth = $this->resident('11110');
        $list = $coop->sessions($second);
        T::eq(1, count($list['sessions']));
        T::eq(false, $list['sessions'][0]['mine']);
        $joinKey = $this->key();
        T::eq(2, $coop->join($second, $session['id'], null, $joinKey)['session']['players']);
        T::eq(true, $coop->join($second, $session['id'], null, $joinKey)['duplicate']);
        // 이미 들어와 있는 사람이 다시 들어와도 한 자리.
        T::eq(2, $coop->join($second, $session['id'], null, $this->key())['session']['players']);
        $coop->join($third, $session['id'], null, $this->key());
        T::eq(4, $coop->join($fourth, $session['id'], null, $this->key())['session']['players']);
        T::eq('session_full', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $fifth, $session) {
            $coop->join($fifth, $session['id'], null, $this->key());
        })->reason);

        // 다른 도시의 주민과 이방인은 들어올 수 없고, 볼 수도 없다.
        $other = $this->resident('26110');
        T::eq('other_city', T::throwsApi(ApiError::FORBIDDEN, function () use ($coop, $other, $session) {
            $coop->join($other, $session['id'], null, $this->key());
        })->reason);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($coop, $other, $session) {
            $coop->session($other, $session['id']);
        });
        T::eq([], $coop->sessions($other)['sessions']);
        T::eq('not_resident', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $stranger, $session) {
            $coop->join($stranger, $session['id'], null, $this->key());
        })->reason);

        // 한 사람이 나가면 자리가 나고, 나갔던 사람은 다시 들어올 수 있다.
        T::eq(3, $coop->leave($third, $session['id'])['session']['players']);
        T::eq(4, $coop->join($fifth, $session['id'], null, $this->key())['session']['players']);
        T::eq('session_full', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $third, $session) {
            $coop->join($third, $session['id'], null, $this->key());
        })->reason);
        $coop->leave($fifth, $session['id']);
        T::eq(4, $coop->join($third, $session['id'], null, $this->key())['session']['players']);
        T::eq(5, (int)$this->db->val('SELECT COUNT(*) FROM defense_session_members'), '나갔다 들어와도 한 사람에 한 줄');

        // 방장이 나가면 가장 먼저 들어온 사람이 방장이 된다.
        $coop->leave($host, $session['id']);
        $view = $coop->session($second, $session['id'])['session'];
        T::eq(true, $view['isHost']);
        T::eq(3, $view['players']);
        // 모두 나가면 판이 닫힌다.
        foreach ([$second, $third, $fourth] as $player) {
            $coop->leave($player, $session['id']);
        }
        $row = $this->sessionRow($session['id']);
        T::eq('cancelled', $row['status']);
        T::eq('empty', $row['close_reason']);
        T::eq('not_joinable', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $host, $session) {
            $coop->join($host, $session['id'], null, $this->key());
        })->reason);
        $this->consistent();
    }

    public function testOneAccountIsInOneSessionAtATime()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        $a = $this->resident('11110');
        $b = $this->resident('11110');
        $first = $coop->create($a, 'outskirts_1', null, null, $this->key())['session']['id'];
        $second = $coop->create($b, 'outskirts_1', null, null, $this->key())['session']['id'];
        T::eq('in_coop_session', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $a) {
            $coop->create($a, 'outskirts_1', null, null, $this->key());
        })->reason);
        T::eq('in_coop_session', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $a, $second) {
            $coop->join($a, $second, null, $this->key());
        })->reason);
        T::eq($first, $coop->config($a)['activeSession']['id']);
        // 나간 뒤에는 다른 판에 들어갈 수 있다.
        $coop->leave($a, $first);
        T::eq(2, $coop->join($a, $second, null, $this->key())['session']['players']);
        T::eq($second, $coop->config($a)['activeSession']['id']);
        $this->consistent();
    }

    public function testRolesAreOptionalAndTheCommanderSeatIsSingle()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->lobby(3);

        T::throwsApi(ApiError::INVALID_REQUEST, function () use ($coop, $team, $id) {
            $coop->setRole($team[0], $id, 'wizard');
        });
        $view = $coop->setRole($team[0], $id, 'commander')['session'];
        T::eq('commander', $view['members'][0]['roleCode']);
        // 지휘관은 한 판에 한 명.
        T::eq('role_taken', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $team, $id) {
            $coop->setRole($team[1], $id, 'commander');
        })->reason);
        // 다른 역할은 여럿이 고를 수 있다.
        $coop->setRole($team[1], $id, 'builder');
        $coop->setRole($team[2], $id, 'builder');
        // 역할을 내려놓을 수 있다 (강제가 아니다). 그러면 다른 사람이 지휘관을 고를 수 있다.
        T::eq(null, $coop->setRole($team[0], $id, '')['session']['members'][0]['roleCode']);
        T::eq('commander', $coop->setRole($team[1], $id, 'commander')['session']['members'][1]['roleCode']);
        // 들어올 때 고를 수도 있다.
        $late = $this->resident('11110');
        T::eq('role_taken', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $late, $id) {
            $coop->join($late, $id, 'commander', $this->key());
        })->reason);
        // 팀원이 아닌 사람은 역할을 바꿀 수 없다.
        T::throwsApi(ApiError::NOT_FOUND, function () use ($coop, $late, $id) {
            $coop->setRole($late, $id, 'fighter');
        });

        // 관리자가 역할의 보정값을 고친다: 범위, 사유, 이력.
        $form = ['build_discount_percent' => '15', 'repair_bonus_percent' => '0', 'attack_bonus_percent' => '0', 'ping_cooldown_percent' => '0',
            'max_per_session' => '2', 'is_active' => '1', 'reason' => '설치 담당의 할인을 조금 올림'];
        T::eq(['권한이 없습니다.'], $coop->saveRole($this->operator(), 'builder', $form));
        T::ok(count($coop->saveRole($this->balancer(), 'builder', ['build_discount_percent' => '51'] + $form)) > 0);
        T::ok(count($coop->saveRole($this->balancer(), 'builder', ['reason' => ''] + $form)) > 0);
        T::eq([], $coop->saveRole($this->balancer(), 'builder', $form));
        T::eq(15, $coop->roles()['builder']['buildDiscountPercent']);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM config_history WHERE target_type = 'defense_team_roles' AND target_key = 'builder'"));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'coop.role.update'"));
    }

    // ── 동시 요청 ───────────────────────────────────────────

    public function testSimultaneousJoinsNeverExceedTheCapacity()
    {
        $this->enableCoop();
        $host = $this->resident('11110');
        $id = $this->app->coop()->create($host, 'outskirts_1', 4, null, $this->key())['session']['id'];
        $jobs = [];
        for ($i = 0; $i < 8; $i++) {
            $player = $this->resident('11110');
            $jobs[] = ['job' => 'coop.join', 'playerId' => $player['id'], 'session' => $id, 'key' => 'parallel-join-' . $i . '-0001'];
        }
        $results = $this->parallel($jobs);
        // 여덟 명이 같은 순간에 들어와도 남은 세 자리만 찬다.
        T::eq(3, self::succeeded($results));
        foreach ($results as $result) {
            T::ok($result['ok'] || $result['reason'] === 'session_full', json_encode($result, JSON_UNESCAPED_UNICODE));
        }
        T::eq(4, (int)$this->db->val("SELECT COUNT(*) FROM defense_session_members WHERE status = 'joined'"));
        $this->consistent();
    }

    public function testSimultaneousCreatesByOneAccountOpenOneSession()
    {
        $this->enableCoop();
        $player = $this->resident('11110');
        $jobs = [];
        for ($i = 0; $i < 5; $i++) {
            $jobs[] = ['job' => 'coop.create', 'playerId' => $player['id'], 'zone' => 'outskirts_1', 'key' => 'parallel-create-' . $i . '-0001'];
        }
        $results = $this->parallel($jobs);
        T::eq(1, self::succeeded($results));
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM defense_sessions'));
        $this->consistent();
    }

    // ── 입장권 ──────────────────────────────────────────────

    public function testTicketIsSignedShortLivedAndOnlyForMembers()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->lobby(2);
        $coop->setRole($team[1], $id, 'support');
        $signer = Signer::fromEnv($this->env);

        $issued = $coop->ticket($team[1], $id);
        T::eq('wss://rt.cg.test/ws', $issued['url']);
        T::eq($id, $issued['sessionId']);
        $payload = $signer->verifyTicket($issued['ticket']);
        T::ok(is_array($payload));
        T::eq($id, $payload['sid']);
        T::eq($team[1]['publicId'], $payload['pid']);
        T::eq('support', $payload['role']);
        T::eq(false, $payload['host']);
        T::eq(false, $payload['ping_blocked']);
        T::eq(Clock::now() + 60, $payload['exp']);
        T::ok(in_array('twr_machine_gun', $payload['structs'], true));
        T::ok(!in_array('twr_blast', $payload['structs'], true), '해금하지 않은 타워는 들어 있지 않다');
        // 입장권에는 내부 번호도, 접속 토큰도, 지갑도 들어 있지 않다.
        $keys = array_keys($payload);
        sort($keys);
        T::eq(['exp', 'host', 'level', 'name', 'nonce', 'pid', 'ping_blocked', 'role', 'sid', 'structs', 'v'], $keys);
        T::eq(true, $signer->verifyTicket($coop->ticket($team[0], $id)['ticket'])['host']);
        // 입장권마다 다르다.
        T::ok($coop->ticket($team[1], $id)['ticket'] !== $issued['ticket']);

        // 고친 입장권, 다른 키로 서명한 입장권은 통하지 않는다.
        list($body, $signature) = explode('.', $issued['ticket']);
        $forged = Signer::base64url(json_encode(array_merge($payload, ['pid' => $team[0]['publicId'], 'host' => true])));
        T::eq(null, $signer->verifyTicket($forged . '.' . $signature));
        T::eq(null, $signer->verifyTicket($body . '.' . strrev($signature)));
        T::eq(null, $signer->verifyTicket($body));
        T::eq(null, $signer->verifyTicket(''));
        T::eq(null, (new Signer(str_repeat('another-secret-', 3)))->verifyTicket($issued['ticket']));
        T::eq(null, (new Signer('short'))->verifyTicket($issued['ticket']));
        // 입장권의 서명을 내부 요청의 서명으로 쓸 수 없다 (키가 다르다).
        T::eq(false, $signer->verifyRequest('GET', "/internal/coop/sessions/$id/config", '', 't=' . Clock::now() . ',v1=' . bin2hex(
            (string)base64_decode(strtr($signature, '-_', '+/') . '='))));

        // 팀원이 아닌 사람, 나간 사람에게는 주지 않는다.
        $outsider = $this->resident('11110');
        T::throwsApi(ApiError::NOT_FOUND, function () use ($coop, $outsider, $id) {
            $coop->ticket($outsider, $id);
        });
        $coop->leave($team[1], $id);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($coop, $team, $id) {
            $coop->ticket($team[1], $id);
        });

        // 60초가 지나면 쓸 수 없다.
        Clock::advance(60);
        T::eq(null, $signer->verifyTicket($issued['ticket']));
    }

    // ── 실시간 서버가 부르는 경로 ───────────────────────────

    public function testInternalRoutesNeedAValidSignature()
    {
        $this->enableCoop();
        list($id, $team) = $this->lobby(2);
        $kernel = new \CG\Api\Kernel($this->app);
        $signer = $this->app->coop()->signer();
        $path = "/internal/coop/sessions/$id/config";
        $get = function ($path, $signature, array $extra = []) use ($kernel) {
            return $kernel->handle(Request::make('GET', $path, [], ($signature !== null ? ['X-CG-Signature' => $signature] : []) + $extra));
        };

        // 서명이 없거나 틀리면 없는 주소처럼 답한다.
        $guest = $this->app->players()->createGuest('windows', '9.0.0');
        $bad = [
            [null, []],
            [null, ['Authorization' => 'Bearer ' . $guest['accessToken']]],                    // 플레이어의 접속 토큰으로는 안 된다
            ['', []],
            ['t=' . Clock::now() . ',v1=' . str_repeat('0', 64), []],
            [$signer->signRequest('POST', $path, ''), []],                                      // 다른 방식에 대한 서명
            [$signer->signRequest('GET', "/internal/coop/sessions/$id/start", ''), []],         // 다른 경로에 대한 서명
            [$signer->signRequest('GET', $path, '{"x":1}'), []],                                // 다른 본문에 대한 서명
            [$signer->signRequest('GET', $path, '', Clock::now() - 61), []],                    // 오래된 서명 (다시 보내기)
            [$signer->signRequest('GET', $path, '', Clock::now() + 61), []],
            [(new Signer(str_repeat('another-secret-', 3)))->signRequest('GET', $path, ''), []],
            [$this->app->coop()->ticket($team[0], $id)['ticket'], []],                          // 입장권을 서명 자리에
        ];
        foreach ($bad as $index => $case) {
            list($status, $body) = $get($path, $case[0], $case[1]);
            T::eq(404, $status, "잘못된 서명 $index");
            T::eq('NOT_FOUND', $body['error']['code']);
        }

        list($status, $body) = $get($path, $signer->signRequest('GET', $path, ''));
        T::eq(200, $status);
        $data = $body['data'];
        T::eq($id, $data['session']['id']);
        T::eq('lobby', $data['session']['status']);
        T::eq($team[0]['publicId'], $data['session']['host_pid']);
        T::eq('outskirts_1', $data['zone']['code']);
        T::eq(5, count($data['zone']['waves']));
        T::eq(20, $data['gate_hp']);
        T::eq(2, $data['rules']['min_players_to_start']);
        T::eq(3, $data['ping']['burst']);
        T::eq(6, count($data['ping_kinds']));
        T::eq($this->pids($team), array_column($data['members'], 'pid'));
        $codes = array_column($data['structures'], 'code');
        T::ok(in_array('twr_machine_gun', $codes, true) && in_array('twr_laser', $codes, true));
        T::ok(!in_array('twr_wall', $codes, true), '준비 중인 시설은 넘기지 않는다');
        T::ok(!in_array('twr_energy_shield', $codes, true), '도시 공용 시설은 전투에서 세우는 것이 아니다');
        T::ok(count($data['enemies']) >= 3);
        // 내부 번호는 넘기지 않는다.
        T::ok(!isset($data['zone']['id']) && !isset($data['structures'][0]['id']) && !isset($data['members'][0]['player_id']));

        // 없는 판.
        $missing = '/internal/coop/sessions/d_aaaaaaaaaaaaaa/config';
        list($status) = $get($missing, $signer->signRequest('GET', $missing, ''));
        T::eq(404, $status);

        // 시작: 서명한 POST. 본문이 바뀌면 서명이 맞지 않는다.
        $startPath = "/internal/coop/sessions/$id/start";
        $startBody = ['by' => $team[0]['publicId'], 'players' => $this->pids($team)];
        $raw = json_encode($startBody, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        $signature = $signer->signRequest('POST', $startPath, $raw);
        list($status) = $kernel->handle(Request::make('POST', $startPath, ['by' => $team[1]['publicId']] + $startBody, ['X-CG-Signature' => $signature]));
        T::eq(404, $status);
        T::eq('lobby', $this->sessionRow($id)['status']);
        list($status, $body) = $kernel->handle(Request::make('POST', $startPath, $startBody, ['X-CG-Signature' => $signature]));
        T::eq(200, $status);
        T::eq('running', $body['data']['status']);
        T::eq(2, $body['data']['players_at_start']);

        // 결과: 실시간 서버는 snake_case 로 보낸다.
        Clock::advance(120);
        $resultPath = "/internal/coop/sessions/$id/result";
        $report = \CG\Infra\Wire::snake($this->report($team));
        $raw = json_encode($report, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        list($status) = $kernel->handle(Request::make('POST', $resultPath, $report));
        T::eq(404, $status);
        T::eq('running', $this->sessionRow($id)['status']);
        list($status, $body) = $kernel->handle(Request::make('POST', $resultPath, $report,
            ['X-CG-Signature' => $signer->signRequest('POST', $resultPath, $raw)]));
        T::eq(200, $status);
        T::eq(true, $body['data']['accepted']);
        T::eq(2, $body['data']['rewarded_players']);
        T::eq(7, (int)$this->db->val('SELECT COUNT(*) FROM defense_shared_budget'));
        $this->consistent();
    }

    public function testOnlyTheHostStartsAndItNeedsEnoughPlayers()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->lobby(3);
        $pids = $this->pids($team);

        T::eq('not_host', T::throwsApi(ApiError::FORBIDDEN, function () use ($coop, $id, $pids) {
            $coop->internalStart($id, ['by' => $pids[1], 'players' => $pids]);
        })->reason);
        // 연결된 사람이 두 명은 되어야 한다. 팀원이 아닌 사람은 세지 않는다.
        T::eq('not_enough_players', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $id, $pids) {
            $coop->internalStart($id, ['by' => $pids[0], 'players' => [$pids[0], 'p_someoneelse0000']]);
        })->reason);
        T::eq('lobby', $this->sessionRow($id)['status']);

        $started = $coop->internalStart($id, ['by' => $pids[0], 'players' => [$pids[0], $pids[2]]]);
        T::eq(false, $started['duplicate']);
        T::eq(2, $started['playersAtStart']);
        $row = $this->sessionRow($id);
        T::eq('running', $row['status']);
        // 전투가 시작되면 판의 수명이 전투 시간(60분)으로 다시 잡힌다.
        T::eq(Clock::now() + 3600, Clock::parse($row['expires_at']));
        // 다시 불러도 한 번만 시작된다.
        T::eq(true, $coop->internalStart($id, ['by' => $pids[0], 'players' => $pids])['duplicate']);
        T::eq(2, (int)$this->sessionRow($id)['players_at_start']);

        // 전투 중에는 REST 로 역할을 바꾸지 않는다 (전투 화면에서, 실시간 서버가 처리한다).
        T::eq('role_locked', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $team, $id) {
            $coop->setRole($team[1], $id, 'fighter');
        })->reason);

        // 전투 중에도 들어올 수 있다 (늦게 합류). 설정에서 끄면 막힌다.
        $late = $this->resident('11110');
        T::eq(4, $coop->join($late, $id, 'fighter', $this->key())['session']['players']);
        $this->enableCoop(['allowLateJoin' => 0]);
        $later = $this->resident('11110');
        T::eq('not_joinable', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $later, $id) {
            $coop->join($later, $id, null, $this->key());
        })->reason);
        T::eq([], $coop->sessions($later)['sessions']);
    }

    // ── 결과 반영 ───────────────────────────────────────────

    public function testWinRewardsEveryMemberOnceAndStoresTheRecords()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->running(4);
        $before = array_map([$this, 'gc'], $team);
        $riskBefore = $this->riskCenti('11110');
        Clock::advance(120);

        $report = $this->report($team);
        // 실시간 서버가 금액을 함께 보내도 쓰이지 않는다.
        $report['rewardGc'] = 999999;
        $report['members'][0]['rewardGc'] = 999999;
        $report['members'][2]['role'] = 'fighter';   // 전투 중에 고른 역할
        $response = $coop->internalResult($id, $report);
        T::eq(true, $response['accepted']);
        T::eq('win', $response['result']);
        T::eq(300, $response['rewardGc']);
        T::eq(150, $response['rewardXp']);
        T::eq(4, $response['rewardedPlayers']);
        T::eq(false, $response['duplicate']);

        // 팀원마다 300 GC (첫 승리 보너스는 혼자 하는 방어전에만 있다), 경험치 150.
        foreach ($team as $index => $player) {
            T::eq($before[$index] + 300, $this->gc($player), "팀원 $index");
            T::eq(null, $this->app->wallets()->findInconsistency($player['id']));
        }
        $session = $this->sessionRow($id);
        $entry = $this->db->one("SELECT * FROM ledger_entries WHERE player_id = ? AND ref_type = 'defense_session'", [$team[0]['id']]);
        T::eq('battle_reward', $entry['reason']);
        T::eq(300, (int)$entry['delta']);
        T::eq((int)$session['id'], (int)$entry['ref_id']);
        // 도시: 함께 이긴 네 사람의 승리가 방어 포인트로 쌓이고, 위험도는 올라가지 않는다.
        T::eq(400, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        T::ok($this->riskCenti('11110') <= $riskBefore);

        // 판의 기록.
        T::eq('finished', $session['status']);
        T::eq('win', $session['result']);
        T::eq(5, (int)$session['waves_cleared']);
        T::eq(14, (int)$session['gate_hp_left']);
        T::eq(120, (int)$session['duration_seconds']);
        T::eq(536, (int)$session['energy_granted']);
        T::eq(270, (int)$session['energy_spent']);
        T::eq(266, (int)$session['energy_left']);
        T::eq('completed', $session['close_reason']);
        $member = $this->db->one('SELECT * FROM defense_session_members WHERE session_id = ? AND player_id = ?', [$session['id'], $team[2]['id']]);
        T::eq('fighter', $member['role_code']);
        T::eq(12, (int)$member['kills']);
        T::eq(120, (int)$member['connected_seconds']);
        T::eq(1, (int)$member['eligible']);
        T::eq(300, (int)$member['reward_gc']);
        T::eq(150, (int)$member['reward_xp']);
        // 공동 예산 장부와 전술 핑.
        T::eq(7, (int)$this->db->val('SELECT COUNT(*) FROM defense_shared_budget WHERE session_id = ?', [$session['id']]));
        T::eq(3, (int)$this->db->val('SELECT COUNT(*) FROM defense_tactical_pings WHERE session_id = ?', [$session['id']]));
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM defense_tactical_pings WHERE session_id = ? AND accepted = 0', [$session['id']]));

        // 같은 결과가 다시 와도 (실시간 서버의 재시도) 한 번만 반영된다.
        $again = $coop->internalResult($id, $report);
        T::eq(true, $again['duplicate']);
        T::eq(true, $again['accepted']);
        T::eq(4, $again['rewardedPlayers']);
        // 다른 내용으로 다시 보내도 처음 것이 남는다.
        $changed = $coop->internalResult($id, $this->report($team, ['result' => 'lose', 'wavesCleared' => 1, 'gateHpLeft' => 0]));
        T::eq('win', $changed['result']);
        foreach ($team as $index => $player) {
            T::eq($before[$index] + 300, $this->gc($player));
        }
        T::eq(4, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE ref_type = 'defense_session'"));
        T::eq(400, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        T::eq(7, (int)$this->db->val('SELECT COUNT(*) FROM defense_shared_budget'));

        // 팀원이 보는 결과, 장부, 기록.
        $mine = $coop->result($team[1], $id);
        T::eq(true, $mine['finished']);
        T::eq('win', $mine['result']['result']);
        T::eq(300, $mine['result']['rewardGc']);
        T::eq($before[1] + 300, $mine['result']['gc']);
        T::eq(4, count($mine['team']));
        $budget = $coop->budget($team[1], $id);
        T::eq(false, $budget['live']);
        T::eq(266, $budget['energyLeft']);
        T::eq(7, count($budget['lines']));
        T::eq('build', $budget['lines'][1]['kind']);
        T::eq(3, $budget['lines'][1]['slotIndex']);
        T::eq($team[0]['publicId'], $budget['lines'][1]['playerId']);
        T::eq(1, count($coop->history($team[1])['results']));
        // 팀원이 아닌 사람은 볼 수 없다.
        $outsider = $this->resident('11110');
        T::throwsApi(ApiError::NOT_FOUND, function () use ($coop, $outsider, $id) {
            $coop->result($outsider, $id);
        });
        T::throwsApi(ApiError::NOT_FOUND, function () use ($coop, $outsider, $id) {
            $coop->budget($outsider, $id);
        });
        // 끝난 판의 입장권은 나오지 않는다. 끝난 뒤에는 새 판에 들어갈 수 있다.
        T::eq('session_closed', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $team, $id) {
            $coop->ticket($team[0], $id);
        })->reason);
        T::eq(null, $coop->config($team[0])['activeSession']);
        T::eq(4, $coop->config($team[0])['rewardsLeftToday']);
        $this->consistent();
    }

    public function testSimultaneousResultReportsPayOnce()
    {
        $this->enableCoop();
        list($id, $team) = $this->running(3);
        $before = array_map([$this, 'gc'], $team);
        Clock::advance(120);
        $report = $this->report($team);
        $results = $this->parallel(array_fill(0, 4, ['job' => 'coop.result', 'session' => $id, 'body' => $report]));
        foreach ($results as $result) {
            T::ok($result['ok'], json_encode($result, JSON_UNESCAPED_UNICODE));
            T::eq(true, $result['data']['accepted']);
        }
        T::eq(1, self::succeeded($results), '처음 것 하나만 반영하고 나머지는 그 응답을 그대로 돌려준다');
        foreach ($team as $index => $player) {
            T::eq($before[$index] + 300, $this->gc($player));
        }
        T::eq(3, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE ref_type = 'defense_session'"));
        T::eq(300, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        $this->consistent();
    }

    public function testBattleGoesOnWhenAMemberLeavesAndShortParticipationEarnsNothing()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->running(4);
        $before = array_map([$this, 'gc'], $team);
        Clock::advance(20);
        // 한 사람이 중간에 나간다. 판은 그대로 이어진다.
        $view = $coop->leave($team[3], $id)['session'];
        T::eq('running', $view['status']);
        T::eq(3, $view['players']);
        // 방장이 나가도 전투는 이어진다.
        $coop->leave($team[0], $id);
        T::eq('running', $this->sessionRow($id)['status']);
        Clock::advance(100);

        $report = $this->report($team);
        $report['members'][3]['connectedSeconds'] = 20;     // 120초 가운데 20초: 30% 에 못 미친다
        $report['members'][0]['connectedSeconds'] = 40;     // 33%: 보상을 받는다
        unset($report['members'][2]);                       // 기록이 없는 사람: 연결한 적이 없다고 본다
        $response = $coop->internalResult($id, $report);
        T::eq(true, $response['accepted']);
        T::eq(2, $response['rewardedPlayers']);
        T::eq($before[0] + 300, $this->gc($team[0]));
        T::eq($before[1] + 300, $this->gc($team[1]));
        T::eq($before[2], $this->gc($team[2]));
        T::eq($before[3], $this->gc($team[3]));
        $mine = $coop->result($team[3], $id)['result'];
        T::eq('low_participation', $mine['rewardNote']);
        T::eq(0, $mine['rewardGc']);
        // 보상을 받지 못한 판은 하루 횟수에서 빠지지 않는다.
        T::eq(5, $coop->config($team[3])['rewardsLeftToday']);
        T::eq(4, $coop->config($team[1])['rewardsLeftToday']);
        T::eq(200, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        $this->consistent();
    }

    public function testLosingPaysTheConsolationRewardAndDoesNotRaiseTheRisk()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->running(2);
        $before = array_map([$this, 'gc'], $team);
        $riskBefore = $this->riskCenti('11110');
        Clock::advance(90);
        $response = $coop->internalResult($id, $this->report($team,
            ['result' => 'lose', 'wavesCleared' => 2, 'gateHpLeft' => 0, 'durationSeconds' => 90, 'closeReason' => 'gate_destroyed']));
        T::eq(true, $response['accepted']);
        T::eq('lose', $response['result']);
        T::eq(40, $response['rewardGc']);
        T::eq(0, $response['rewardXp']);
        foreach ($team as $index => $player) {
            T::eq($before[$index] + 40, $this->gc($player));
        }
        T::eq(0, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        T::ok($this->riskCenti('11110') <= $riskBefore, '져도 위험도는 올라가지 않는다');
        T::eq('gate_destroyed', $this->sessionRow($id)['close_reason']);
        $this->consistent();
    }

    public function testDailyRewardLimit()
    {
        $this->enableCoop(['dailyRewardLimit' => 2]);
        $coop = $this->app->coop();
        list($id, $team) = $this->running(2);
        $before = $this->gc($team[0]);
        for ($round = 1; $round <= 3; $round++) {
            if ($round > 1) {
                $id = $this->again($team);
            }
            Clock::advance(120);
            $response = $coop->internalResult($id, $this->report($team));
            T::eq(true, $response['accepted']);
            T::eq($round <= 2 ? 2 : 0, $response['rewardedPlayers'], "판 $round");
        }
        T::eq($before + 600, $this->gc($team[0]));
        T::eq('daily_limit', $coop->result($team[0], $id)['result']['rewardNote']);
        T::eq(0, $coop->config($team[0])['rewardsLeftToday']);
        // 보상이 없던 판은 도시 방어 포인트에도 들어가지 않는다.
        T::eq(400, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        // 날이 바뀌면 다시 받는다.
        Clock::advance(86400);
        $id = $this->again($team);
        Clock::advance(120);
        T::eq(2, $coop->internalResult($id, $this->report($team))['rewardedPlayers']);
        $this->consistent();
    }

    public function testResultsThatDoNotAddUpAreRejectedAndPayNobody()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->running(2);
        $a = $team[0]['publicId'];
        $b = $team[1]['publicId'];
        $before = array_map([$this, 'gc'], $team);
        $ledger = function (array $steps) {
            return ['budget' => self::ledger($steps)];
        };
        $tampered = function (array $change) use ($team) {
            $lines = $this->report($team)['budget'];
            $lines[$change[0]] = array_merge($lines[$change[0]], $change[1]);
            return ['budget' => $lines];
        };
        // [걸린 시간(초), 바꾼 내용, 기대하는 거절 사유]
        $cases = [
            [120, ['result' => 'draw'], 'bad_result'],
            [120, ['wavesCleared' => 4], 'bad_waves'],                                   // 이겼다면서 웨이브가 남았다
            [120, ['wavesCleared' => 6], 'bad_waves'],                                   // 없는 웨이브
            [120, ['gateHpLeft' => 0], 'bad_waves'],                                     // 이겼다면서 관문이 부서졌다
            [120, ['gateHpLeft' => 21], 'bad_gate_hp'],                                  // 관문의 최대 내구도(20)보다 많이 남았다
            [120, ['result' => 'lose', 'wavesCleared' => 1, 'gateHpLeft' => 3, 'closeReason' => 'gate_destroyed'], 'bad_gate_hp'],
            [30, [], 'too_fast'],                                                        // 5 웨이브 × 10초보다 빨리 끝났다
            [60, ['durationSeconds' => 120], 'too_fast'],                                // 이 서버가 잰 시간보다 길게 싸웠다고 한다
            // 공동 예산 장부
            [120, $ledger([['grant_start', 400], ['build', -100, $a, 3], ['build', -100, $b, 3]]), 'budget_duplicate_slot'],   // 같은 자리에 타워 둘
            [120, $ledger([['grant_start', 100], ['build', -100, $a, 1], ['build', -100, $b, 2]]), 'budget_negative'],         // 예산보다 많이 썼다
            [120, $tampered([4, ['balanceAfter' => 999]]), 'budget_balance'],                                                  // 잔액이 이어지지 않는다
            [120, $tampered([2, ['seq' => 9]]), 'budget_sequence'],
            [120, $tampered([1, ['pid' => 'p_someoneelse0000']]), 'budget_spender'],                                           // 팀원이 아닌 사람이 썼다
            [120, $tampered([1, ['pid' => null]]), 'budget_spender'],
            [120, $ledger([['grant_start', 400], ['mint', 5000]]), 'budget_kind'],
            [120, $ledger([['grant_start', 400], ['build', 100, $a, 1]]), 'budget_sign'],                                      // 세웠는데 예산이 늘었다
            [120, $ledger([['grant_start', 400], ['grant_kill', -50]]), 'budget_sign'],
            [120, $ledger([['grant_start', 400], ['upgrade', -60, $a, 5]]), 'budget_upgrade_empty_slot'],                      // 빈 자리를 강화했다
            [120, $ledger([['grant_start', 400], ['build', -100, $a]]), 'budget_duplicate_slot'],                              // 자리가 없는 설치
            [120, $tampered([0, ['delta' => '400']]), 'budget_format'],
            [120, ['budget' => [['seq' => 1]]], 'budget_format'],
        ];
        foreach ($cases as $index => $case) {
            if ($index > 0) {
                $id = $this->again($team);
            }
            Clock::advance($case[0]);
            $response = $coop->internalResult($id, $this->report($team, $case[1]));
            T::eq(false, $response['accepted'], "경우 $index ({$case[2]})");
            T::eq($case[2], $response['rejectReason'], "경우 $index");
            T::eq(0, $response['rewardedPlayers']);
            T::eq(null, $response['result']);
            $row = $this->sessionRow($id);
            T::eq('finished', $row['status']);
            T::eq(null, $row['result']);
            T::eq('rejected:' . $case[2], $row['close_reason']);
        }
        // 아무도 보상을 받지 않았고, 도시에도 반영되지 않았고, 장부도 남지 않았다.
        T::eq($before, array_map([$this, 'gc'], $team));
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE ref_type = 'defense_session'"));
        T::eq(0, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM defense_shared_budget'));
        T::eq(count($cases), (int)$this->db->val("SELECT COUNT(*) FROM system_logs WHERE source = 'coop' AND level = 'warning'"));
        T::eq('rejected', $coop->result($team[0], $id)['result']['rewardNote']);
        T::eq(5, $coop->config($team[0])['rewardsLeftToday']);
        // 다시 보내도 거절된 채로 남는다.
        $again = $coop->internalResult($id, $this->report($team));
        T::eq(true, $again['duplicate']);
        T::eq(false, $again['accepted']);

        // 타워가 부서진 자리에는 다시 세울 수 있다 (lost 가 자리를 비운다).
        $id = $this->again($team);
        Clock::advance(120);
        $response = $coop->internalResult($id, $this->report($team, $ledger([
            ['grant_start', 400], ['build', -100, $a, 3, 'twr_machine_gun'], ['lost', 0, null, 3, 'twr_machine_gun'],
            ['build', -100, $b, 3, 'twr_machine_gun'], ['upgrade', -60, $b, 3, 'twr_machine_gun'],
        ])));
        T::eq(true, $response['accepted']);
        T::eq(5, (int)$this->db->val('SELECT COUNT(*) FROM defense_shared_budget'));
        $this->consistent();
    }

    public function testCityStructuresStrengthenTheGate()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        // 에너지 방어막을 2단계까지 세운다 (도시 재정이든 주민 기금이든 같은 곳에 쌓인다).
        $this->app->city()->tick();
        $this->db->tx(function () {
            $this->app->city()->treasuryFund($this->regionId('11110'), 'twr_energy_shield', 10000, 'test');
        });
        list($id, $team) = $this->running(2);
        $config = $coop->internalConfig($id);
        T::eq(2, $config['cityStructureLevels']);
        T::eq(22, $config['gateHp'], '20 × (100 + 2단계 × 5)% = 22');
        Clock::advance(120);
        // 튼튼해진 관문의 내구도까지는 인정된다.
        T::eq(true, $coop->internalResult($id, $this->report($team, ['gateHpLeft' => 22]))['accepted']);
        $id = $this->again($team);
        Clock::advance(120);
        T::eq('bad_gate_hp', $coop->internalResult($id, $this->report($team, ['gateHpLeft' => 23]))['rejectReason']);
    }

    public function testCoopRewardIsTaxedLikeAnyBattleRewardWhenTheBusinessTaxIsOn()
    {
        $this->enableCoop();
        $this->enableTax(Taxes::BUSINESS, ['exempt_amount' => 0]);
        list($id, $team) = $this->running(3);
        $before = array_map([$this, 'gc'], $team);
        Clock::advance(120);
        T::eq(3, $this->app->coop()->internalResult($id, $this->report($team))['rewardedPlayers']);
        foreach ($team as $index => $player) {
            T::eq($before[$index] + 300 - 15, $this->gc($player));
            T::eq(null, $this->app->wallets()->findInconsistency($player['id']));
        }
        T::eq(45, $this->treasuryGc('11110') + $this->treasuryGc('11110', 'reserve'));
        // 결과 화면에는 받은 보상(300)이 그대로 보이고, 세금은 세금 명세에서 본다.
        T::eq(300, $this->app->coop()->result($team[0], $id)['result']['rewardGc']);
        T::eq(15, $this->app->taxes()->transactionsOf($team[0])['transactions'][0]['taxGc']);
        $this->consistent();
    }

    // ── 정리 ────────────────────────────────────────────────

    public function testStaleSessionsAreClosedWithoutRewards()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($lobby, $waiting) = $this->lobby(2);
        list($battle, $team) = $this->running(2);
        $before = $this->gc($team[0]);

        Clock::advance(14 * 60);
        T::eq(0, $coop->expireDue());
        Clock::advance(60);
        // 대기실은 15분 뒤에 닫힌다.
        T::eq(1, $coop->expireDue());
        T::eq('expired', $this->sessionRow($lobby)['status']);
        T::eq(null, $coop->config($waiting[0])['activeSession']);
        T::eq('running', $this->sessionRow($battle)['status']);

        // 끝나지 않는 전투는 60분 뒤에 중단된다. 예약 작업이 한다.
        Clock::advance(3600);
        T::eq(1, \CG\Service\CityOpsTick::run($this->app)['coopClosed']);
        $row = $this->sessionRow($battle);
        T::eq('abandoned', $row['status']);
        T::eq('timeout', $row['close_reason']);
        // 닫힌 판의 결과는 받지 않는다.
        T::eq('session_closed', T::throwsApi(ApiError::CONFLICT, function () use ($coop, $battle, $team) {
            $coop->internalResult($battle, $this->report($team));
        })->reason);
        T::eq($before, $this->gc($team[0]));

        // 관리자가 강제로 닫을 수 있다: 권한, 사유, 감사 로그.
        list($open, $others) = $this->running(2);
        $id = (int)$this->sessionRow($open)['id'];
        T::eq(['권한이 없습니다.'], $coop->adminClose($this->balancer(), $id, '신고가 들어온 판'));
        T::ok(count($coop->adminClose($this->operator(), $id, '')) > 0);
        T::eq([], $coop->adminClose($this->operator(), $id, '신고가 들어온 판', '10.0.0.8'));
        T::eq('abandoned', $this->sessionRow($open)['status']);
        T::eq(['이미 닫힌 방어전입니다.'], $coop->adminClose($this->operator(), $id, '신고가 들어온 판'));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'coop.close' AND target_key = ?", [$open]));
        $counts = $coop->adminCounts();
        T::eq(1, $counts['expired']);
        T::eq(2, $counts['abandoned']);
        T::eq(3, count($coop->adminSessions()));
        T::eq(2, count($coop->adminSession($id)['members']));
        $this->consistent();
    }

    // ── 전술 핑 차단 ────────────────────────────────────────

    public function testPingBlockReachesTheTicketAndCanBeLifted()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->lobby(2);
        $signer = Signer::fromEnv($this->env);
        $noisy = $team[1];
        $support = $this->admin('helpdesk', Auth::ROLE_SUPPORT);

        T::eq(['권한이 없습니다.'], $coop->blockPing($this->balancer(), $noisy['publicId'], '24', '핑을 계속 눌러 팀을 방해함'));
        T::ok(count($coop->blockPing($support, 'p_nobody0000000000', '24', '핑을 계속 눌러 팀을 방해함')) > 0);
        T::ok(count($coop->blockPing($support, $noisy['publicId'], '9000', '핑을 계속 눌러 팀을 방해함')) > 0);
        T::ok(count($coop->blockPing($support, $noisy['publicId'], '24', '')) > 0);
        T::eq(false, $coop->isPingBlocked($noisy['id']));

        T::eq([], $coop->blockPing($support, $noisy['publicId'], '24', '핑을 계속 눌러 팀을 방해함', '10.0.0.4'));
        T::eq(true, $coop->isPingBlocked($noisy['id']));
        T::eq(['이미 핑이 막힌 플레이어입니다.'], $coop->blockPing($support, $noisy['publicId'], '24', '핑을 계속 눌러 팀을 방해함'));
        // 실시간 서버는 입장권으로 차단을 안다. 다른 팀원에게는 영향이 없다.
        T::eq(true, $signer->verifyTicket($coop->ticket($noisy, $id)['ticket'])['ping_blocked']);
        T::eq(false, $signer->verifyTicket($coop->ticket($team[0], $id)['ticket'])['ping_blocked']);
        T::eq(true, $coop->config($noisy)['ping']['blocked']);
        // 핑이 막혀도 방어전에는 들어갈 수 있다.
        T::eq(true, $coop->session($noisy, $id)['session']['mine']);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'ping.block' AND target_key = ?", [$noisy['publicId']]));

        // 기간이 지나면 풀린다.
        Clock::advance(24 * 3600);
        T::eq(false, $coop->isPingBlocked($noisy['id']));
        // 기간 없이 막으면 풀 때까지.
        T::eq([], $coop->blockPing($support, $noisy['publicId'], '0', '다시 같은 행동을 함'));
        Clock::advance(400 * 86400);
        T::eq(true, $coop->isPingBlocked($noisy['id']));
        $blocks = $coop->pingBlocks();
        T::eq(2, count($blocks));
        T::ok(count($coop->liftPingBlock($support, $blocks[0]['id'], '')) > 0);
        T::eq([], $coop->liftPingBlock($support, $blocks[0]['id'], '사과하고 다시 하지 않기로 함'));
        T::eq(false, $coop->isPingBlocked($noisy['id']));
        T::eq(['이미 풀렸거나 없는 차단입니다.'], $coop->liftPingBlock($support, $blocks[0]['id'], '사과하고 다시 하지 않기로 함'));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'ping.unblock'"));
    }

    public function testRejectedPingsShowUpInTheAbuseReport()
    {
        $this->enableCoop();
        $coop = $this->app->coop();
        list($id, $team) = $this->running(2);
        Clock::advance(120);
        $pings = [];
        for ($i = 0; $i < 12; $i++) {
            // 처음 셋만 받아들여지고 나머지는 빈도 제한에 걸렸다.
            $pings[] = ['pid' => $team[1]['publicId'], 'kind' => 'danger', 'x' => $i, 'z' => 0, 'atMs' => 1000 + $i * 100, 'accepted' => $i < 3];
        }
        $pings[] = ['pid' => 'p_someoneelse0000', 'kind' => 'danger', 'x' => 0, 'z' => 0, 'atMs' => 1, 'accepted' => true];   // 팀원이 아닌 사람: 버린다
        $pings[] = ['pid' => $team[0]['publicId'], 'kind' => 'dance', 'x' => 0, 'z' => 0, 'atMs' => 1, 'accepted' => true];    // 없는 종류: 버린다
        $pings[] = ['pid' => $team[0]['publicId'], 'kind' => 'rally', 'x' => 99999999, 'z' => -99999999, 'atMs' => 2, 'accepted' => true];
        T::eq(true, $coop->internalResult($id, $this->report($team, ['pings' => $pings]))['accepted']);
        T::eq(13, (int)$this->db->val('SELECT COUNT(*) FROM defense_tactical_pings'));
        // 좌표는 범위 안으로 잘라 둔다.
        $far = $this->db->one("SELECT x_centi, z_centi FROM defense_tactical_pings WHERE kind = 'rally'");
        T::eq(1000000, (int)$far['x_centi']);
        T::eq(-1000000, (int)$far['z_centi']);

        $abuse = $coop->pingAbuse();
        T::eq(1, count($abuse));
        T::eq($team[1]['publicId'], $abuse[0]['player_public_id']);
        T::eq(12, (int)$abuse[0]['total']);
        T::eq(9, (int)$abuse[0]['rejected']);
        $totals = $coop->pingKindTotals();
        T::eq('danger', $totals[0]['kind']);
        T::eq(12, (int)$totals[0]['total']);
    }

    // ── API ─────────────────────────────────────────────────

    public function testCoopApiFlowForPlayers()
    {
        $this->enableCoop();
        $kernel = new \CG\Api\Kernel($this->app);
        $session = function ($nickname) {
            $guest = $this->app->players()->createGuest('windows', '9.0.0');
            $player = $this->app->players()->authenticate($guest['accessToken']);
            $this->app->players()->registerResidency($player, '11110', $nickname);
            return [$this->app->players()->get($player['id']), ['Authorization' => 'Bearer ' . $guest['accessToken']]];
        };
        list($host, $hostHeaders) = $session('방장');
        list($mate, $mateHeaders) = $session('팀원');
        $call = function ($method, $path, array $body, array $headers, $key = null) use ($kernel) {
            return $kernel->handle(Request::make($method, $path, $body, $headers + ($key !== null ? ['Idempotency-Key' => $key] : [])));
        };

        list($status, $body) = $call('GET', '/coop/config', [], $hostHeaders);
        T::eq(200, $status);
        T::eq(true, $body['data']['available']);
        T::eq('11110', $body['data']['region_code']);

        list($status, $body) = $call('POST', '/coop/sessions', ['zone_code' => 'outskirts_1', 'max_players' => 4, 'role_code' => 'commander'],
            $hostHeaders, 'coop-api-create-0001');
        T::eq(200, $status);
        $id = $body['data']['session']['id'];
        T::eq('commander', $body['data']['session']['members'][0]['role_code']);

        list($status, $body) = $call('GET', '/coop/sessions', [], $mateHeaders);
        T::eq(200, $status);
        T::eq($id, $body['data']['sessions'][0]['id']);
        list($status, $body) = $call('POST', "/coop/sessions/$id/join", ['role_code' => 'commander'], $mateHeaders, 'coop-api-join-0001');
        T::eq(409, $status);
        T::eq('role_taken', $body['error']['reason']);
        list($status, $body) = $call('POST', "/coop/sessions/$id/join", [], $mateHeaders, 'coop-api-join-0002');
        T::eq(200, $status);
        T::eq(2, $body['data']['session']['players']);
        list($status, $body) = $call('PUT', "/coop/sessions/$id/role", ['role_code' => 'support'], $mateHeaders);
        T::eq(200, $status);
        T::eq('support', $body['data']['session']['members'][1]['role_code']);
        list($status, $body) = $call('GET', "/coop/sessions/$id", [], $mateHeaders);
        T::eq(200, $status);
        T::eq(true, $body['data']['session']['mine']);

        list($status, $body) = $call('POST', "/coop/sessions/$id/ticket", [], $mateHeaders);
        T::eq(200, $status);
        T::ok(is_array(Signer::fromEnv($this->env)->verifyTicket($body['data']['ticket'])));
        T::eq('wss://rt.cg.test/ws', $body['data']['url']);

        // 플레이어가 부를 수 있는 경로에는 전투를 시작하거나, 결과를 보고하거나, 타워를 세우거나, 예산을 바꾸는 것이 없다.
        foreach ([
            ['POST', "/coop/sessions/$id/start"], ['POST', "/coop/sessions/$id/result"], ['POST', "/coop/sessions/$id/finish"],
            ['POST', "/coop/sessions/$id/towers"], ['POST', "/coop/sessions/$id/budget"], ['POST', "/coop/sessions/$id/pings"],
            ['POST', "/internal/coop/sessions/$id/start"], ['POST', "/internal/coop/sessions/$id/result"],
        ] as $route) {
            list($status) = $call($route[0], $route[1], ['result' => 'win', 'by' => $host['publicId'], 'players' => [$host['publicId']]],
                $hostHeaders, 'coop-api-x-' . substr(md5($route[1]), 0, 8));
            T::ok($status === 404 || $status === 400, $route[0] . ' ' . $route[1] . " → $status");
        }
        T::eq('lobby', $this->sessionRow($id)['status']);

        list($status, $body) = $call('GET', "/coop/sessions/$id/result", [], $mateHeaders);
        T::eq(200, $status);
        T::eq(false, $body['data']['finished']);
        list($status, $body) = $call('GET', "/coop/sessions/$id/budget", [], $mateHeaders);
        T::eq(200, $status);
        T::eq([], $body['data']['lines']);
        list($status, $body) = $call('POST', "/coop/sessions/$id/leave", [], $mateHeaders);
        T::eq(200, $status);
        T::eq(1, $body['data']['session']['players']);
        list($status, $body) = $call('GET', '/coop/history', [], $mateHeaders);
        T::eq(200, $status);
        T::eq([], $body['data']['results']);
        list($status) = $call('GET', '/coop/config', [], []);
        T::eq(401, $status);
    }
}
