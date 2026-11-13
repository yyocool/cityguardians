<?php
use CG\Admin\Auth;
use CG\Admin\Kernel;
use CG\Admin\Pages;
use CG\Infra\Clock;
use CG\Infra\Env;
use CG\Infra\Request;
use CG\Infra\Totp;

/**
 * 웹 관리자: 로그인(비밀번호 + 일회용 코드), 접속과 CSRF, 역할, 정의·정책 수정과 이력, 위험도 수동 조정, 침공 이벤트.
 */
final class AdminTest extends DbTestCase
{
    const PASSWORD = 'Correct-Horse-9';

    private $secrets = [];

    private function createAdmin($login, $role)
    {
        $admin = (new Auth($this->app))->create($login, self::PASSWORD, $role);
        $this->secrets[$login] = $admin['totpSecret'];
        return $admin;
    }

    private function request($method, $path, $cookie = null, array $form = [], array $query = [], array $headers = [])
    {
        $request = Request::make($method, $path, [], $headers, $query);
        $request->form = $form;
        if ($cookie !== null) {
            $request->cookies = [Auth::COOKIE => $cookie];
        }
        return $request;
    }

    private function send(Request $request, $app = null)
    {
        return (new Kernel($app !== null ? $app : $this->app, ''))->handle($request);
    }

    private function loginForm($login, $password = self::PASSWORD, $code = null)
    {
        return ['login' => $login, 'password' => $password,
            'code' => $code !== null ? $code : Totp::code($this->secrets[$login], Clock::now())];
    }

    private static function header(array $headers, $name)
    {
        foreach ($headers as $header) {
            if (stripos($header, $name . ':') === 0) {
                return trim(substr($header, strlen($name) + 1));
            }
        }
        return null;
    }

    /** 로그인하고 [쿠키 토큰, CSRF 토큰] 을 돌려준다. */
    private function signIn($login, $role = Auth::ROLE_SUPER)
    {
        if (!isset($this->secrets[$login])) {
            $this->createAdmin($login, $role);
        }
        Clock::advance(31);   // 같은 일회용 코드를 두 번 쓰지 않게 다음 칸으로
        list($status, $headers) = $this->send($this->request('POST', '/admin/login', null, $this->loginForm($login)));
        T::same(303, $status, '로그인 실패');
        T::ok(preg_match('/^' . Auth::COOKIE . '=([0-9a-f]{64});/', (string)self::header($headers, 'Set-Cookie'), $m) === 1);
        list($status, , $html) = $this->send($this->request('GET', '/admin', $m[1]));
        T::same(200, $status);
        T::ok(preg_match('/name="_csrf" value="([0-9a-f]{64})"/', $html, $c) === 1);
        return [$m[1], $c[1]];
    }

    private function get($path, array $session, array $query = [])
    {
        return $this->send($this->request('GET', $path, $session[0], [], $query));
    }

    private function post($path, array $session, array $form)
    {
        return $this->send($this->request('POST', $path, $session[0], $form + ['_csrf' => $session[1]]));
    }

    private function enemy($code)
    {
        return $this->db->one('SELECT * FROM enemy_defs WHERE code = ?', [$code]);
    }

    /** 적 수정 폼에 지금 값을 채운다. */
    private function enemyForm($code, array $changes, $reason = '밸런스 조정')
    {
        $row = $this->enemy($code);
        $form = ['reason' => $reason];
        foreach (CG\Admin\Catalog::find('enemies')['fields'] as $column => $field) {
            if ($field['type'] === 'bool') {
                if ((int)$row[$column] === 1) {
                    $form[$column] = '1';
                }
            } else {
                $form[$column] = (string)$row[$column];
            }
        }
        foreach ($changes as $column => $value) {
            if ($value === null) {
                unset($form[$column]);
            } else {
                $form[$column] = $value;
            }
        }
        return $form;
    }

    // ── 로그인 ──────────────────────────────────────────────

    public function testLoginNeedsPasswordAndOneTimeCode()
    {
        $this->createAdmin('root', Auth::ROLE_SUPER);
        $try = function (array $form) {
            return $this->send($this->request('POST', '/admin/login', null, $form));
        };
        list($status, , $wrongPassword) = $try($this->loginForm('root', 'Wrong-Password-1'));
        T::same(401, $status);
        list($status, , $wrongCode) = $try($this->loginForm('root', self::PASSWORD, '000000'));
        T::same(401, $status);
        list($status, , $noCode) = $try(['login' => 'root', 'password' => self::PASSWORD]);
        T::same(401, $status);
        list($status, , $unknown) = $try(['login' => 'nobody', 'password' => self::PASSWORD, 'code' => '123456']);
        T::same(401, $status);
        // 틀린 이유를 구별해 알려 주지 않는다.
        T::same($wrongPassword, $wrongCode);
        T::same($wrongPassword, $unknown);
        T::same($wrongPassword, $noCode);
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM admin_sessions'));

        Clock::advance(31);
        list($status, $headers, $body) = $try($this->loginForm('root'));
        T::same(303, $status);
        T::same('/admin', self::header($headers, 'Location'));
        $cookie = self::header($headers, 'Set-Cookie');
        T::ok(strpos($cookie, 'HttpOnly') !== false && strpos($cookie, 'SameSite=Strict') !== false && strpos($cookie, 'Path=/admin') !== false);
        T::same('', $body);
        T::eq(4, $this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'login.failed'"));
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'login'"));
    }

    public function testCookieIsSecureUnlessExplicitlyTurnedOffForLocalUse()
    {
        $this->createAdmin('root', Auth::ROLE_SUPER);
        $app = new \CG\App(Env::fromArray([]), $this->db);   // ADMIN_COOKIE_SECURE 를 정하지 않은 운영 구성
        list($status, $headers) = $this->send($this->request('POST', '/admin/login', null, $this->loginForm('root')), $app);
        T::same(303, $status);
        T::ok(strpos(self::header($headers, 'Set-Cookie'), '; Secure') !== false, '기본값은 Secure 쿠키다');
    }

    public function testAOneTimeCodeCannotBeUsedTwice()
    {
        $this->createAdmin('root', Auth::ROLE_SUPER);
        $form = $this->loginForm('root');
        list($status) = $this->send($this->request('POST', '/admin/login', null, $form));
        T::same(303, $status);
        list($status) = $this->send($this->request('POST', '/admin/login', null, $form));
        T::same(401, $status, '같은 코드를 다시 받았다');
        Clock::advance(30);
        list($status) = $this->send($this->request('POST', '/admin/login', null, $this->loginForm('root')));
        T::same(303, $status);
    }

    public function testRepeatedFailuresLockTheAccountForAWhile()
    {
        $this->createAdmin('root', Auth::ROLE_SUPER);
        for ($i = 0; $i < Auth::MAX_FAILURES; $i++) {
            list($status) = $this->send($this->request('POST', '/admin/login', null, $this->loginForm('root', 'Wrong-Password-1')));
            T::same(401, $status);
        }
        // 잠긴 동안에는 맞는 비밀번호도 받지 않는다.
        Clock::advance(31);
        list($status) = $this->send($this->request('POST', '/admin/login', null, $this->loginForm('root')));
        T::same(401, $status);
        Clock::advance(Auth::LOCK_MINUTES * 60);
        list($status) = $this->send($this->request('POST', '/admin/login', null, $this->loginForm('root')));
        T::same(303, $status);
    }

    public function testLoginAttemptsAreRateLimitedPerAddress()
    {
        $app = new \CG\App(Env::fromArray(['ADMIN_LOGIN_PER_10MIN' => '3']), $this->db);
        for ($i = 0; $i < 3; $i++) {
            list($status) = $this->send($this->request('POST', '/admin/login', null, ['login' => 'x' . $i, 'password' => 'p', 'code' => '1']), $app);
            T::same(401, $status);
        }
        list($status) = $this->send($this->request('POST', '/admin/login', null, ['login' => 'x', 'password' => 'p', 'code' => '1']), $app);
        T::same(429, $status);
    }

    public function testPasswordRulesAndStoredSecrets()
    {
        $auth = new Auth($this->app);
        foreach (['short1A!', 'alllowercaseletters', '12345678901234'] as $weak) {
            try {
                $auth->create('weak', $weak, Auth::ROLE_VIEWER);
                T::ok(false, "약한 비밀번호를 받았다: $weak");
            } catch (\InvalidArgumentException $expected) {
                T::ok(true);
            }
        }
        $session = $this->signIn('root');
        $dump = json_encode([$this->db->all('SELECT * FROM admins'), $this->db->all('SELECT * FROM admin_sessions')]);
        T::ok(strpos($dump, self::PASSWORD) === false, '비밀번호가 그대로 저장되었다');
        T::ok(strpos($dump, $session[0]) === false, '접속 토큰이 그대로 저장되었다');
    }

    // ── 접속 ────────────────────────────────────────────────

    public function testEveryPageNeedsASessionAndCarriesSecurityHeaders()
    {
        foreach (Pages::menu() as $item) {
            list($status, $headers) = $this->send($this->request('GET', $item[0]));
            T::same(303, $status, $item[0]);
            T::same('/admin/login', self::header($headers, 'Location'));
        }
        list($status) = $this->send($this->request('GET', '/admin', str_repeat('a', 64)));
        T::same(303, $status);

        $session = $this->signIn('root');
        $paths = array_merge(['/admin', '/admin/defs/hunt_zones', '/admin/defs/facilities', '/admin/risk/11110',
            '/admin/defs/enemies/malfunction_robot', '/admin/defs/zones/outskirts_1', '/admin/defs/gear_stats/gear_energy_blaster'],
            array_column(Pages::menu(), 0));
        foreach ($paths as $path) {
            list($status, $headers, $html) = $this->get($path, $session);
            T::same(200, $status, $path);
            T::ok(strpos(self::header($headers, 'Content-Security-Policy'), "default-src 'none'") === 0, $path);
            T::same('DENY', self::header($headers, 'X-Frame-Options'));
            T::same('no-store', self::header($headers, 'Cache-Control'));
            T::same('noindex, nofollow', self::header($headers, 'X-Robots-Tag'));
            // 스크립트와 외부 자원이 없다.
            T::ok(stripos($html, '<script') === false, "$path 에 스크립트가 있다");
            T::ok(preg_match('#(src|href)="(https?:)?//#i', $html) === 0, "$path 가 외부 자원을 부른다");
        }
        list($status) = $this->get('/admin/nope', $session);
        T::same(404, $status);
        list($status) = $this->get('/admin/defs/players', $session);
        T::same(404, $status, '목록에 없는 테이블은 고칠 수 없다');
    }

    public function testSessionEndsAfterIdleTimeAndOnLogout()
    {
        $session = $this->signIn('root');
        Clock::advance(Auth::IDLE_MINUTES * 60 - 30);
        T::same(200, $this->get('/admin', $session)[0]);
        Clock::advance(Auth::IDLE_MINUTES * 60 + 1);
        T::same(303, $this->get('/admin', $session)[0], '조작이 없으면 접속이 끝난다');

        $session = $this->signIn('root');
        list($status, $headers) = $this->post('/admin/logout', $session, []);
        T::same(303, $status);
        T::ok(strpos(self::header($headers, 'Set-Cookie'), 'Max-Age=0') !== false);
        T::same(303, $this->get('/admin', $session)[0]);
    }

    public function testChangesNeedTheCsrfTokenAndTheSameOrigin()
    {
        $session = $this->signIn('root');
        $before = (int)$this->enemy('malfunction_robot')['reward_gc'];
        $form = $this->enemyForm('malfunction_robot', ['reward_gc' => '77']);

        list($status) = $this->send($this->request('POST', '/admin/defs/enemies/malfunction_robot', $session[0], $form));
        T::same(403, $status, 'CSRF 토큰 없이 받았다');
        list($status) = $this->send($this->request('POST', '/admin/defs/enemies/malfunction_robot', $session[0], $form + ['_csrf' => str_repeat('0', 64)]));
        T::same(403, $status);
        list($status) = $this->send($this->request('POST', '/admin/defs/enemies/malfunction_robot', $session[0],
            $form + ['_csrf' => $session[1]], [], ['Origin' => 'https://evil.example', 'Host' => 'game.example']));
        T::same(403, $status, '다른 곳에서 보낸 폼을 받았다');
        // 값을 바꾸는 일은 GET 으로 할 수 없다.
        $this->get('/admin/defs/enemies/malfunction_robot', $session, $form + ['_csrf' => $session[1]]);
        T::same($before, (int)$this->enemy('malfunction_robot')['reward_gc']);
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM config_history'));

        list($status) = $this->send($this->request('POST', '/admin/defs/enemies/malfunction_robot', $session[0],
            $form + ['_csrf' => $session[1]], [], ['Origin' => 'https://game.example', 'Host' => 'game.example']));
        T::same(303, $status);
        T::same(77, (int)$this->enemy('malfunction_robot')['reward_gc']);
    }

    public function testAllowListHidesTheAdminAreaFromOtherAddresses()
    {
        $session = $this->signIn('root');
        $app = new \CG\App(Env::fromArray(['ADMIN_ALLOW_IPS' => '10.0.0.5, 10.0.0.6']), $this->db);
        list($status) = $this->send($this->request('GET', '/admin/login'), $app);
        T::same(404, $status);
        $request = $this->request('GET', '/admin', $session[0]);
        $request->ip = '10.0.0.6';
        T::same(200, $this->send($request, $app)[0]);
    }

    // ── 역할 ────────────────────────────────────────────────

    public function testRolesLimitWhatAnAdminCanChange()
    {
        $viewer = $this->signIn('viewer1', Auth::ROLE_VIEWER);
        $balance = $this->signIn('balance1', Auth::ROLE_BALANCE);
        $operator = $this->signIn('operator1', Auth::ROLE_OPERATOR);
        $this->app->city()->tick();
        $risk = $this->riskCenti('11110');
        $enemyForm = $this->enemyForm('malfunction_robot', ['reward_gc' => '55']);
        $adjust = ['delta' => '10', 'reason' => '역할 확인'];

        // 조회 전용: 볼 수는 있지만 아무것도 바꾸지 못한다.
        T::same(200, $this->get('/admin/defs/enemies/malfunction_robot', $viewer)[0]);
        T::same(403, $this->post('/admin/defs/enemies/malfunction_robot', $viewer, $enemyForm)[0]);
        T::same(403, $this->post('/admin/risk/11110/adjust', $viewer, $adjust)[0]);
        T::same(403, $this->post('/admin/policy', $viewer, ['invasionChancePermille' => '0', 'reason' => '역할 확인'])[0]);
        T::same(403, $this->post('/admin/invasions/start', $viewer, ['region' => '11110', 'kind' => 'outskirts', 'severity' => '1'])[0]);
        T::same(403, $this->post('/admin/config/economy', $viewer, ['dailyHuntGcLimit' => '1', 'residencyBonusGc' => '1', 'reason' => '역할 확인'])[0]);
        T::same(403, $this->get('/admin/audit', $viewer)[0]);
        T::eq(10, $this->enemy('malfunction_robot')['reward_gc']);
        T::same($risk, $this->riskCenti('11110'));

        // 밸런스 담당: 정의와 정책은 고치지만 위험도를 직접 조정하거나 침공을 시작하지는 못한다.
        T::same(303, $this->post('/admin/defs/enemies/malfunction_robot', $balance, $enemyForm)[0]);
        T::eq(55, $this->enemy('malfunction_robot')['reward_gc']);
        T::same(403, $this->post('/admin/risk/11110/adjust', $balance, $adjust)[0]);
        T::same(403, $this->post('/admin/invasions/start', $balance, ['region' => '11110', 'kind' => 'outskirts', 'severity' => '1'])[0]);
        T::same($risk, $this->riskCenti('11110'));

        // 운영자: 위험도 조정과 침공은 하지만 밸런스 값은 고치지 못한다.
        T::same(303, $this->post('/admin/risk/11110/adjust', $operator, $adjust)[0]);
        T::same($risk + 1000, $this->riskCenti('11110'));
        T::same(403, $this->post('/admin/defs/enemies/malfunction_robot', $operator, $this->enemyForm('malfunction_robot', ['reward_gc' => '99']))[0]);
        T::same(403, $this->post('/admin/ranking', $operator, ['r_safety' => '50', 'reason' => '역할 확인'])[0]);
        T::eq(55, $this->enemy('malfunction_robot')['reward_gc']);
        T::same(200, $this->get('/admin/audit', $operator)[0]);

        // 고칠 권한이 없으면 이력에서 되돌릴 수도 없다.
        $historyId = (int)$this->db->val("SELECT MAX(id) FROM config_history WHERE target_type = 'enemy_defs'");
        foreach ([$viewer, $operator] as $session) {
            T::ok($this->post('/admin/history/' . $historyId . '/revert', $session, ['reason' => '되돌리기 시도'])[0] >= 400);
            T::eq(55, $this->enemy('malfunction_robot')['reward_gc']);
        }
        T::same(303, $this->post('/admin/history/' . $historyId . '/revert', $balance, ['reason' => '원래대로'])[0]);
        T::eq(10, $this->enemy('malfunction_robot')['reward_gc']);
    }

    public function testFormValuesSentAsArraysAreIgnored()
    {
        $session = $this->signIn('root');
        $form = $this->enemyForm('malfunction_robot', ['reward_gc' => '30']);
        // reason[]=… 처럼 배열로 보낸 사유는 사유가 아니다.
        T::same(422, $this->post('/admin/defs/enemies/malfunction_robot', $session, ['reason' => ['밸런스 조정']] + $form)[0]);
        T::same(422, $this->post('/admin/defs/enemies/malfunction_robot', $session, ['reward_gc' => ['30']] + $form)[0]);
        T::same(422, $this->post('/admin/policy', $session, ['w_defense_won' => '-700', 'reason' => ['정책']])[0]);
        T::same(422, $this->post('/admin/risk/11110/adjust', $session, ['delta' => ['5'], 'reason' => '배열'])[0]);
        T::same(403, $this->send($this->request('POST', '/admin/risk/11110/adjust', $session[0],
            ['_csrf' => [$session[1]], 'delta' => '5', 'reason' => '배열']))[0]);
        T::same(401, $this->send($this->request('POST', '/admin/login', null,
            ['login' => ['root'], 'password' => [self::PASSWORD], 'code' => ['123456']]))[0]);
        $request = $this->request('GET', '/admin');
        $request->cookies = [Auth::COOKIE => [$session[0]]];
        T::same(303, $this->send($request)[0]);
        T::eq(10, $this->enemy('malfunction_robot')['reward_gc']);
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM city_risk_policy'));
    }

    // ── 정의 데이터 ─────────────────────────────────────────

    public function testEditingADefinitionIsValidatedRecordedAndUsedByTheGame()
    {
        $this->quiet();
        $session = $this->signIn('root');
        $versionBefore = $this->app->defs()->version();

        list($status, $headers) = $this->post('/admin/defs/enemies/malfunction_robot', $session,
            $this->enemyForm('malfunction_robot', ['reward_gc' => '25', 'max_hp' => '90'], '사냥 보상 상향'));
        T::same(303, $status);
        T::same('/admin/defs/enemies?done=saved', self::header($headers, 'Location'));

        $row = $this->enemy('malfunction_robot');
        T::eq(25, $row['reward_gc']);
        T::eq(90, $row['max_hp']);
        $history = $this->db->one('SELECT * FROM config_history ORDER BY id DESC LIMIT 1');
        T::same('enemy_defs', $history['target_type']);
        T::same('malfunction_robot', $history['target_key']);
        T::same('사냥 보상 상향', $history['reason']);
        T::ok((int)$history['admin_id'] > 0);
        T::same(['reward_gc' => 10], array_intersect_key(json_decode($history['before_value'], true), ['reward_gc' => 1]));
        T::same(['max_hp' => 90, 'reward_gc' => 25], json_decode($history['after_value'], true));
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'def.update' AND target_key = 'malfunction_robot'"));

        // 정의의 판이 바뀌어 클라이언트가 새 정의를 받는다. 서버의 보상 계산도 새 값을 쓴다.
        $app = new \CG\App($this->env, $this->db);
        T::ok($app->defs()->version() !== $versionBefore);
        $player = $this->resident();
        $hunt = $app->hunt()->start($player, 'zone_ne');
        Clock::advance(60);
        T::same(250, $app->hunt()->report($player, $hunt['sessionId'], 1, [['enemyCode' => 'malfunction_robot', 'count' => 10]])['rewardGc']);

        // 바뀐 값이 없으면 이력을 만들지 않는다.
        list($status, $headers) = $this->post('/admin/defs/enemies/malfunction_robot', $session, $this->enemyForm('malfunction_robot', []));
        T::same('/admin/defs/enemies?done=none', self::header($headers, 'Location'));
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM config_history'));
    }

    /** 화면의 폼에 지금 값을 채운다. */
    private function rowForm($screenKey, array $row, $reason = '밸런스 조정')
    {
        $form = ['reason' => $reason];
        foreach (CG\Admin\Catalog::find($screenKey)['fields'] as $column => $field) {
            if ($field['type'] === 'bool') {
                if ((int)$row[$column] === 1) {
                    $form[$column] = '1';
                }
            } else {
                $form[$column] = (string)$row[$column];
            }
        }
        return $form;
    }

    public function testEverySeededDefinitionPassesItsOwnChecks()
    {
        // 기본 데이터가 관리자 화면의 범위 검사에 걸리면 그 항목은 어떤 값도 고칠 수 없게 된다.
        $session = $this->signIn('root');
        $rows = 0;
        foreach (CG\Admin\Catalog::all() as $key => $screen) {
            foreach ($this->db->all('SELECT * FROM ' . $screen['table']) as $row) {
                list($status, $headers, $html) = $this->post("/admin/defs/$key/{$row['code']}", $session, $this->rowForm($key, $row));
                $problem = '';
                if ($status !== 303 && preg_match('#<div class="problems"[^>]*>(.*?)</div>#s', $html, $m)) {
                    $problem = trim(strip_tags($m[1]));
                }
                T::same(303, $status, "$key/{$row['code']} $problem");
                T::same("/admin/defs/$key?done=none", self::header($headers, 'Location'), "$key/{$row['code']}: 고치지 않았는데 바뀐 값이 있다");
                $rows++;
            }
        }
        T::ok($rows > 40);
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM config_history'));
    }

    public function testOutOfRangeValuesAndMissingReasonsAreRefused()
    {
        $session = $this->signIn('root');
        $bad = [
            ['reward_gc' => '-5'], ['reward_gc' => '999999999'], ['max_hp' => '0'], ['max_hp' => 'abc'], ['max_hp' => '10; DROP TABLE players'],
            ['move_speed' => '1e9'], ['attack_interval' => '0'], ['name' => ''], ['name' => str_repeat('가', 41)], ['max_hp' => null],
        ];
        foreach ($bad as $changes) {
            list($status, , $html) = $this->post('/admin/defs/enemies/malfunction_robot', $session, $this->enemyForm('malfunction_robot', $changes));
            T::same(422, $status, json_encode($changes));
            T::ok(strpos($html, '저장하지 않았습니다') !== false);
        }
        list($status) = $this->post('/admin/defs/enemies/malfunction_robot', $session, $this->enemyForm('malfunction_robot', ['reward_gc' => '20'], ''));
        T::same(422, $status, '사유 없이 저장했다');
        list($status) = $this->post('/admin/defs/enemies/nope', $session, $this->enemyForm('malfunction_robot', ['reward_gc' => '20']));
        T::same(404, $status);

        T::eq(10, $this->enemy('malfunction_robot')['reward_gc']);
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM config_history'));
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'players'"));
    }

    public function testFreeBasicGearAndFreeBasicTowerCannotBeRemoved()
    {
        $session = $this->signIn('root');
        $gear = $this->db->one("SELECT * FROM combat_equipment WHERE code = 'gear_energy_blaster'");
        $form = ['reason' => '가격 조정'];
        foreach (CG\Admin\Catalog::find('gear_stats')['fields'] as $column => $field) {
            $form[$column] = (string)$gear[$column];
        }
        // 무료 기본 무기에 값을 매기면 무료로 FPS 에 참여할 길이 없어진다.
        list($status, , $html) = $this->post('/admin/defs/gear_stats/gear_energy_blaster', $session, ['price_gc' => '100'] + $form);
        T::same(422, $status);
        T::ok(strpos($html, '무료 기본 장비') !== false);
        T::eq(0, $this->db->val("SELECT price_gc FROM combat_equipment WHERE code = 'gear_energy_blaster'"));
        // 성능을 고치는 것은 된다.
        list($status) = $this->post('/admin/defs/gear_stats/gear_energy_blaster', $session, ['damage' => '14'] + $form);
        T::same(303, $status);
        T::eq(14, $this->db->val("SELECT damage FROM combat_equipment WHERE code = 'gear_energy_blaster'"));
        // 유료 장비의 가격은 고칠 수 있다.
        $rifle = $this->db->one("SELECT * FROM combat_equipment WHERE code = 'gear_precision_rifle'");
        $rifleForm = ['reason' => '가격 조정'];
        foreach (CG\Admin\Catalog::find('gear_stats')['fields'] as $column => $field) {
            $rifleForm[$column] = (string)$rifle[$column];
        }
        T::same(303, $this->post('/admin/defs/gear_stats/gear_precision_rifle', $session, ['price_gc' => '450'] + $rifleForm)[0]);
        T::eq(450, $this->db->val("SELECT price_gc FROM combat_equipment WHERE code = 'gear_precision_rifle'"));

        // 처음부터 무료로 세울 수 있는 타워도 하나는 남아야 한다.
        $tower = $this->db->one("SELECT * FROM defense_structures WHERE code = 'twr_machine_gun'");
        $towerForm = ['reason' => '가격 조정'];
        foreach (CG\Admin\Catalog::find('structures')['fields'] as $column => $field) {
            if ($field['type'] === 'bool') {
                if ((int)$tower[$column] === 1) {
                    $towerForm[$column] = '1';
                }
            } else {
                $towerForm[$column] = (string)$tower[$column];
            }
        }
        T::same(422, $this->post('/admin/defs/structures/twr_machine_gun', $session, ['unlock_gc' => '300'] + $towerForm)[0]);
        $off = $towerForm;
        unset($off['is_active']);
        T::same(422, $this->post('/admin/defs/structures/twr_machine_gun', $session, $off)[0]);
        T::same(303, $this->post('/admin/defs/structures/twr_machine_gun', $session, ['damage' => '7'] + $towerForm)[0]);
        T::eq(7, $this->db->val("SELECT damage FROM defense_structures WHERE code = 'twr_machine_gun'"));
    }

    public function testWaveCompositionIsCheckedAgainstEnemyDefinitions()
    {
        $session = $this->signIn('root');
        $zone = $this->db->one("SELECT * FROM defense_zones WHERE code = 'outskirts_1'");
        $form = ['reason' => '웨이브 조정', 'is_active' => '1'];
        foreach (CG\Admin\Catalog::find('zones')['fields'] as $column => $field) {
            if ($field['type'] !== 'bool') {
                $form[$column] = (string)$zone[$column];
            }
        }
        $path = '/admin/defs/zones/outskirts_1';
        $bad = [
            'not json', '{}', '[]', '[{"groups":[]}]',
            '[{"groups":[{"enemyCode":"dragon","count":3}]}]',
            '[{"groups":[{"enemyCode":"mech_insect","count":0}]}]',
            '[{"groups":[{"enemyCode":"mech_insect","count":"12"}]}]',
            '[{"groups":[{"enemyCode":"mech_insect","count":501}]}]',
            '[{"groups":[{"enemyCode":"mech_insect","count":5,"interval":-1}]}]',
        ];
        foreach ($bad as $waves) {
            T::same(422, $this->post($path, $session, ['waves' => $waves] + $form)[0], $waves);
        }
        T::same($zone['waves'], $this->db->val("SELECT waves FROM defense_zones WHERE code = 'outskirts_1'"));

        $waves = '[{"groups":[{"enemyCode":"mech_insect","count":12,"interval":0.8,"delay":0,"extra":"<b>x</b>"}]},'
            . '{"groups":[{"enemyCode":"malfunction_robot","count":4}]}]';
        T::same(303, $this->post($path, $session, ['waves' => $waves] + $form)[0]);
        $saved = json_decode($this->db->val("SELECT waves FROM defense_zones WHERE code = 'outskirts_1'"), true);
        T::eq(2, count($saved));
        T::same(['enemyCode' => 'mech_insect', 'count' => 12, 'interval' => 0.8, 'delay' => 0], $saved[0]['groups'][0], '모르는 값은 버린다');
        // 서버의 검증도 새 구성을 쓴다: 적은 이제 16마리다.
        $app = new \CG\App($this->env, $this->db);
        T::same(16, \CG\Domain\Battle::totalEnemies($app->defs()->findZone('outskirts_1')));

        // 어딘가에서 쓰이는 적은 끌 수 없다.
        $enemyForm = $this->enemyForm('mech_insect', ['is_active' => null]);
        list($status, , $html) = $this->post('/admin/defs/enemies/mech_insect', $session, $enemyForm);
        T::same(422, $status);
        T::eq(1, $this->enemy('mech_insect')['is_active']);
    }

    public function testAChangeCanBeRevertedFromHistory()
    {
        $session = $this->signIn('root');
        $this->post('/admin/defs/enemies/malfunction_robot', $session, $this->enemyForm('malfunction_robot', ['reward_gc' => '40']));
        $id = (int)$this->db->val('SELECT MAX(id) FROM config_history');

        T::same(422, $this->post('/admin/history/' . $id . '/revert', $session, ['reason' => ''])[0], '사유 없이 되돌렸다');
        T::eq(40, $this->enemy('malfunction_robot')['reward_gc']);

        list($status, $headers) = $this->post('/admin/history/' . $id . '/revert', $session, ['reason' => '너무 높았다']);
        T::same(303, $status);
        T::same('/admin/history?done=reverted', self::header($headers, 'Location'));
        T::eq(10, $this->enemy('malfunction_robot')['reward_gc']);
        // 되돌린 것도 하나의 변경으로 이력에 남는다.
        $last = $this->db->one('SELECT * FROM config_history ORDER BY id DESC LIMIT 1');
        T::ok((int)$last['id'] > $id);
        T::ok(strpos($last['reason'], '되돌리기 #' . $id) === 0);
        T::same(422, $this->post('/admin/history/999999/revert', $session, ['reason' => '없는 이력'])[0]);
    }

    public function testCommonLimitsAreEditedWithRangeChecks()
    {
        $session = $this->signIn('root');
        T::same(422, $this->post('/admin/config/economy', $session, ['dailyHuntGcLimit' => '-1', 'residencyBonusGc' => '500', 'reason' => '한도'])[0]);
        T::same(422, $this->post('/admin/config/economy', $session, ['dailyHuntGcLimit' => '5000', 'residencyBonusGc' => '500', 'reason' => ''])[0]);
        T::same(422, $this->post('/admin/config/secrets', $session, ['reason' => '없는 설정'])[0], '목록에 없는 설정은 고칠 수 없다');
        T::eq(0, $this->db->val("SELECT COUNT(*) FROM game_config WHERE config_key = 'secrets'"));
        T::same(303, $this->post('/admin/config/economy', $session, ['dailyHuntGcLimit' => '5000', 'residencyBonusGc' => '500', 'reason' => '사냥 한도 상향'])[0]);
        $app = new \CG\App($this->env, $this->db);
        T::same(5000, $app->defs()->economy()['dailyHuntGcLimit']);
        T::same(500, $app->defs()->economy()['residencyBonusGc']);
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM config_history WHERE target_type = 'game_config' AND target_key = 'economy'"));
    }

    public function testAnOperatorCanCloseACombatMode()
    {
        $operator = $this->signIn('operator1', Auth::ROLE_OPERATOR);
        $mode = $this->db->one("SELECT * FROM combat_modes WHERE code = 'fps'");
        $form = ['name' => $mode['name'], 'summary' => $mode['summary'], 'controls' => $mode['controls'],
            'sort_order' => (string)$mode['sort_order'], 'is_closed' => '1', 'reason' => 'FPS 임무 점검'];
        T::same(303, $this->post('/admin/defs/modes/fps', $operator, $form)[0]);
        $app = new \CG\App($this->env, $this->db);
        $player = $this->resident();
        T::throwsApi(\CG\Infra\ApiError::LOCKED, function () use ($app, $player) {
            $app->fps()->start($player, 'downtown_1');
        });
        // 다른 모드는 그대로다.
        $app->defense()->start($player, 'outskirts_1');
    }

    // ── 도시 위험도, 정책, 침공 ─────────────────────────────

    public function testManualRiskAdjustmentNeedsAReasonAndLeavesTheAdminInTheHistory()
    {
        $this->quiet();
        $session = $this->signIn('root');
        $risk = $this->riskCenti('11110');

        foreach ([['delta' => '10', 'reason' => ''], ['delta' => '0', 'reason' => '사유'], ['delta' => '500', 'reason' => '사유'],
                     ['delta' => 'abc', 'reason' => '사유'], ['delta' => '1.5', 'reason' => '사유']] as $form) {
            T::same(422, $this->post('/admin/risk/11110/adjust', $session, $form)[0], json_encode($form));
        }
        T::same($risk, $this->riskCenti('11110'));
        T::same(404, $this->post('/admin/risk/99999/adjust', $session, ['delta' => '10', 'reason' => '없는 도시'])[0]);

        list($status, $headers) = $this->post('/admin/risk/11110/adjust', $session, ['delta' => '-5', 'reason' => '이벤트 보상']);
        T::same(303, $status);
        T::same($risk - 500, $this->riskCenti('11110'));
        $row = $this->db->one('SELECT * FROM city_risk_history ORDER BY id DESC LIMIT 1');
        T::same('admin', $row['actor']);
        T::same('admin_adjust', $row['reason']);
        T::same('이벤트 보상', $row['reference']);
        T::eq($this->db->val("SELECT id FROM admins WHERE login = 'root'"), $row['admin_id']);
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'risk.adjust' AND target_key = '11110'"));

        // 도시 화면에 누가 왜 바꿨는지 보인다.
        list(, , $html) = $this->get('/admin/risk/11110', $session);
        T::ok(strpos($html, '이벤트 보상') !== false);
        T::ok(strpos($html, '관리자 (root)') !== false);
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testTextEnteredByPeopleIsEscapedOnAdminPages()
    {
        $this->quiet();
        $session = $this->signIn('root');
        $attack = '<script>alert(1)</script><img src=x onerror=alert(2)>"\'';
        $this->post('/admin/risk/11110/adjust', $session, ['delta' => '5', 'reason' => $attack]);
        $this->post('/admin/defs/enemies/malfunction_robot', $session, $this->enemyForm('malfunction_robot', ['name' => '<b>로봇</b>'], $attack));

        foreach (['/admin/risk/11110', '/admin/history', '/admin/audit', '/admin/defs/enemies', '/admin/defs/enemies/malfunction_robot'] as $path) {
            list($status, , $html) = $this->get($path, $session);
            T::same(200, $status);
            T::ok(stripos($html, '<script') === false, "$path: 스크립트가 그대로 나갔다");
            T::ok(stripos($html, '<img') === false, "$path: 태그가 그대로 나갔다");
            T::ok(stripos($html, '<b>로봇') === false, "$path: 태그가 그대로 나갔다");
        }
        list(, , $html) = $this->get('/admin/risk/11110', $session);
        T::ok(strpos($html, '&lt;script&gt;alert(1)&lt;/script&gt;') !== false);
        // 주소의 값도 화면에 그대로 나가지 않는다.
        list(, , $html) = $this->get('/admin/risk', $session, ['sido' => '"><script>x</script>', 'stage' => '"><script>y</script>', 'done' => '<script>z</script>']);
        T::ok(stripos($html, '<script') === false);
    }

    public function testPolicyChangesAreValidatedVersionedAndTakeEffect()
    {
        $session = $this->signIn('root');
        $first = $this->app->defs()->policy()['version'];

        // 플레이어의 활동이 위험도를 올리게 되는 가중치, 순서가 뒤집힌 단계 경계, 범위를 벗어난 값은 저장하지 않는다.
        foreach ([['w_defense_won' => '500'], ['w_invasion_started' => '-100'], ['cautionAt' => '80'], ['invasionChancePermille' => '2000'],
                     ['tickMinutes' => '0'], ['dailyPlayerReduceCap' => 'many'], ['supportCloseBelow' => '90']] as $change) {
            list($status, , $html) = $this->post('/admin/policy', $session, $change + ['reason' => '정책 조정']);
            T::same(422, $status, json_encode($change));
            T::ok(strpos($html, '저장하지 않았습니다') !== false);
        }
        T::same(422, $this->post('/admin/policy', $session, ['w_defense_won' => '-700', 'reason' => ''])[0], '사유 없이 저장했다');
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM city_risk_policy'));

        list($status, $headers) = $this->post('/admin/policy', $session,
            ['w_defense_won' => '-1000', 'patrolPercent' => '100', 'invasionChancePermille' => '0', 'reason' => '방어전 효과 상향']);
        T::same(303, $status);
        T::same('/admin/policy?done=saved', self::header($headers, 'Location'));
        T::eq(2, $this->db->val('SELECT COUNT(*) FROM city_risk_policy'), '이전 정책은 그대로 남고 새 행이 생긴다');
        $history = $this->db->one("SELECT * FROM config_history WHERE target_type = 'city_risk_policy' ORDER BY id DESC LIMIT 1");
        T::same(['weights.defense_won' => -600, 'patrolPercent' => 50, 'invasionChancePermille' => 40], json_decode($history['before_value'], true));
        T::same('방어전 효과 상향', $history['reason']);

        // 게임이 새 정책을 쓴다.
        $app = new \CG\App($this->env, $this->db);
        T::same(-1000, $app->defs()->policy()['weights']['defense_won']);
        $player = $this->resident();
        $this->setRisk('11110', 5000);
        $battle = $app->defense()->start($player, 'outskirts_1');
        Clock::advance(60);
        T::same(-1000, $app->defense()->finish($player, $battle['sessionId'], self::defenseWinReport())['city']['riskDeltaCenti']);

        // 지난 정책으로 되돌린다 (새 행으로 저장된다).
        T::same(422, $this->post('/admin/policy/revert', $session, ['id' => (string)$first, 'reason' => ''])[0]);
        T::same(303, $this->post('/admin/policy/revert', $session, ['id' => (string)$first, 'reason' => '시험 종료'])[0]);
        T::eq(3, $this->db->val('SELECT COUNT(*) FROM city_risk_policy'));
        $app = new \CG\App($this->env, $this->db);
        T::same(-600, $app->defs()->policy()['weights']['defense_won']);
        T::same(40, $app->defs()->policy()['invasionChancePermille']);
    }

    public function testInvasionFrequencyCanBeTunedAndChangingTheIntervalDoesNotReplayThePast()
    {
        $session = $this->signIn('root');
        $this->app->city()->tick();
        $events = (int)$this->db->val('SELECT COUNT(*) FROM city_invasion_events');

        // 판정 간격을 60분에서 10분으로: 순번의 기준이 달라져도 지난 판정을 한꺼번에 다시 돌리지 않는다.
        T::same(303, $this->post('/admin/invasions/policy', $session,
            ['tickMinutes' => '10', 'invasionChancePermille' => '1000', 'invasionMinutes' => '30', 'intrusionPermille' => '0',
                'zeroEventPermille' => '0', 'reason' => '이벤트 주간'])[0]);
        $app = new \CG\App($this->env, $this->db);
        T::same(0, $app->city()->tick(), '간격을 바꾼 직후에는 새 판정이 없다');
        T::same($events, (int)$this->db->val('SELECT COUNT(*) FROM city_invasion_events'));

        // 다음 판정부터 새 빈도가 적용된다: 침공이 없는 도시마다 침공이 생긴다.
        Clock::advance(600);
        T::same(1, $app->city()->tick());
        T::eq(230, $this->db->val("SELECT COUNT(*) FROM city_invasion_events WHERE status = 'active'"));
        T::same(null, $app->city()->findInconsistency());
    }

    public function testAnAdminCanStartAndEndAnInvasionEvent()
    {
        $this->quiet();
        $session = $this->signIn('root');
        $this->app->city()->tick();
        $risk = $this->riskCenti('11110');

        foreach ([['region' => '99999', 'kind' => 'outskirts', 'severity' => '1'], ['region' => '11110', 'kind' => 'meteor', 'severity' => '1'],
                     ['region' => '11110', 'kind' => 'outskirts', 'severity' => '7']] as $form) {
            T::same(422, $this->post('/admin/invasions/start', $session, $form)[0], json_encode($form));
        }
        T::eq(0, $this->db->val('SELECT COUNT(*) FROM city_invasion_events'));

        T::same(303, $this->post('/admin/invasions/start', $session, ['region' => '11110', 'kind' => 'intrusion', 'severity' => '2'])[0]);
        $event = $this->db->one('SELECT * FROM city_invasion_events');
        T::same('admin', $event['source']);
        T::same('active', $event['status']);
        T::same($risk + 1600, $this->riskCenti('11110'));
        $history = $this->db->one('SELECT * FROM city_risk_history ORDER BY id DESC LIMIT 1');
        T::same('admin', $history['actor']);
        T::ok((int)$history['admin_id'] > 0);
        // 같은 도시에 또 시작할 수는 없다.
        T::same(422, $this->post('/admin/invasions/start', $session, ['region' => '11110', 'kind' => 'outskirts', 'severity' => '1'])[0]);
        T::eq(1, $this->db->val('SELECT COUNT(*) FROM city_invasion_events'));

        list(, , $html) = $this->get('/admin/invasions', $session);
        T::ok(strpos($html, '/admin/invasions/' . $event['id'] . '/end') !== false);
        T::same(303, $this->post('/admin/invasions/' . $event['id'] . '/end', $session, [])[0]);
        $event = $this->db->one('SELECT * FROM city_invasion_events');
        T::same('npc_held', $event['status'], '관리자가 끝낸 침공은 가디언의 기여로 세지 않는다');
        $status = $this->db->one('SELECT s.* FROM city_risk_status s JOIN regions r ON r.id = s.region_id WHERE r.code = ?', ['11110']);
        T::eq(0, $status['repelled_severity']);
        T::eq(0, $status['contribution']);
        T::eq(2, $this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action LIKE 'invasion.%'"));
        T::same(null, $this->app->city()->findInconsistency());
    }

    public function testRankingWeightsAreEditedWithChecks()
    {
        $session = $this->signIn('root');
        T::same(422, $this->post('/admin/ranking', $session, ['r_safety' => '150', 'reason' => '가중치'])[0]);
        T::same(422, $this->post('/admin/ranking', $session, ['r_contributionTarget' => '0', 'reason' => '가중치'])[0]);
        T::same(422, $this->post('/admin/ranking', $session,
            ['r_safety' => '0', 'r_successRate' => '0', 'r_contribution' => '0', 'r_response' => '0', 'r_cooperation' => '0', 'reason' => '가중치'])[0]);
        T::same(303, $this->post('/admin/ranking', $session, ['r_safety' => '10', 'r_cooperation' => '30', 'reason' => '협동을 더 높게'])[0]);
        $app = new \CG\App($this->env, $this->db);
        T::same(10, $app->defs()->rankingWeights()['safety']);
        T::same(30, $app->defs()->rankingWeights()['cooperation']);
        T::same(20, $app->defs()->rankingWeights()['successRate'], '보내지 않은 값은 그대로다');
        // 위험도 정책은 건드리지 않았다.
        T::same(-600, $app->defs()->policy()['weights']['defense_won']);
        list($status, , $html) = $this->get('/admin/ranking', $session);
        T::same(200, $status);
    }

    public function testStatsAndSupportPagesShowWhatHappenedInTheGame()
    {
        $this->quiet();
        $session = $this->signIn('root');
        $player = $this->resident('11110', '통계가디언');
        $this->winDefense($player);
        $this->winFps($player, 70);
        $battle = $this->app->defense()->start($player, 'outskirts_1');
        Clock::advance(3);
        $this->app->defense()->finish($player, $battle['sessionId'], self::defenseWinReport());   // 너무 빨라 거절된다
        $this->app->city()->adminAdjustRisk(null, '11140', 70, '테스트: 긴급 단계로');

        list($status, , $html) = $this->get('/admin/stats', $session);
        T::same(200, $status);
        T::ok(strpos($html, '방어전 보상') !== false);
        T::ok(strpos($html, 'too_fast') !== false, '거절된 보고의 이유가 보여야 한다');
        list($status, , $html) = $this->get('/admin/support', $session);
        T::same(200, $status);
        T::ok(strpos($html, '열림') !== false);
        list($status, , $html) = $this->get('/admin/risk', $session, ['sido' => '11', 'stage' => 'emergency']);
        T::same(200, $status);
        T::ok(strpos($html, '■ 긴급') !== false, '단계는 색만이 아니라 모양과 글자로도 보인다');
        T::ok(strpos($html, '중구') !== false);
        T::ok(strpos($html, '종로구') === false, '걸러진 도시는 보이지 않는다');
        list($status, , $html) = $this->get('/admin', $session);
        T::same(200, $status);
    }

    public function testAnUnexpectedErrorShowsNothingInternal()
    {
        $session = $this->signIn('root');
        $this->db->pdo()->exec('RENAME TABLE enemy_defs TO enemy_defs_gone');
        try {
            list($status, , $html) = $this->get('/admin/defs/enemies', $session);
        } finally {
            $this->db->pdo()->exec('RENAME TABLE enemy_defs_gone TO enemy_defs');
        }
        T::same(500, $status);
        foreach (['enemy_defs', 'SQLSTATE', 'PDO', '.php'] as $leak) {
            T::ok(stripos($html, $leak) === false, "오류 화면에 내부 정보가 있다: $leak");
        }
        T::eq(1, $this->db->val("SELECT COUNT(*) FROM system_logs WHERE source = 'admin' AND level = 'error'"));
    }
}
