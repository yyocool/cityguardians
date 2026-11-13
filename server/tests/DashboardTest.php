<?php
use CG\Admin\Auth;
use CG\Admin\DashboardPages;
use CG\Api\Kernel as ApiKernel;
use CG\Admin\Kernel as AdminKernel;
use CG\Domain\Invasion;
use CG\Domain\Risk;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Infra\Totp;
use CG\Service\Settings;

/**
 * v10.0 실시간 대시보드 (docs/LIVE_DASHBOARD.md, docs/API_SPEC.md 23장, docs/ADMIN_SYSTEM.md 13장).
 *
 *   GET /dashboard/summary 는 접속한 사람 자신의 값만 준다. 서버에 없는 금융 기능은 "없다"고 답한다.
 *   우리 도시와 전국의 값은 기존 API 가 주는 값과 같다. 요약을 읽어도 아무것도 바뀌지 않는다.
 *   관리자 설정은 범위·사유·이력·되돌리기를 거치고, 관리자 화면에는 이용자의 금융 정보가 없다.
 */
final class DashboardTest extends CityTestCase
{
    private $sessions = [];

    // ── 요청 도우미 ─────────────────────────────────────────

    private function api($method, $path, $token = null, array $query = [], array $body = [])
    {
        $headers = $token !== null ? ['Authorization' => 'Bearer ' . $token] : [];
        return (new ApiKernel($this->app))->handle(Request::make($method, $path, $body, $headers, $query));
    }

    private function newGuest()
    {
        list($status, $response) = (new ApiKernel($this->app))->handle(Request::make('POST', '/auth/guest', [],
            ['X-Client-Platform' => 'windows', 'X-Client-Version' => '10.0.0']));
        T::same(200, $status);
        return $response['data'];
    }

    private function summary(array $player)
    {
        return $this->app->dashboard()->summary($this->fresh($player));
    }

    private function send($method, $path, $cookie = null, array $form = [], array $query = [])
    {
        $request = Request::make($method, $path, [], [], $query);
        $request->form = $form;
        if ($cookie !== null) {
            $request->cookies = [Auth::COOKIE => $cookie];
        }
        return (new AdminKernel($this->app, ''))->handle($request);
    }

    /** 로그인하고 [쿠키 토큰, CSRF 토큰] 을 돌려준다. */
    private function signIn(array $admin)
    {
        if (isset($this->sessions[$admin['login']])) {
            return $this->sessions[$admin['login']];
        }
        Clock::advance(31);
        list($status, $headers) = $this->send('POST', '/admin/login', null,
            ['login' => $admin['login'], 'password' => self::PASSWORD, 'code' => Totp::code($admin['totpSecret'], Clock::now())]);
        T::same(303, $status, '로그인 실패: ' . $admin['login']);
        $cookie = '';
        foreach ($headers as $header) {
            if (preg_match('/^Set-Cookie: ' . Auth::COOKIE . '=([0-9a-f]{64});/', $header, $m)) {
                $cookie = $m[1];
            }
        }
        list($status, , $html) = $this->send('GET', '/admin', $cookie);
        T::same(200, $status, $admin['login'] . ' 의 첫 화면');
        T::ok(preg_match('/name="_csrf" value="([0-9a-f]{64})"/', $html, $c) === 1);
        return $this->sessions[$admin['login']] = [$cookie, $c[1]];
    }

    private function get(array $admin, $path, array $query = [])
    {
        return $this->send('GET', $path, $this->signIn($admin)[0], [], $query);
    }

    private function post(array $admin, $path, array $form = [])
    {
        $session = $this->signIn($admin);
        return $this->send('POST', $path, $session[0], $form + ['_csrf' => $session[1]]);
    }

    private static function location(array $response)
    {
        foreach ($response[1] as $header) {
            if (stripos($header, 'Location:') === 0) {
                return trim(substr($header, 9));
            }
        }
        return null;
    }

    /** 설정의 폼: 지금 값에 바꿀 값만 덮어쓴다. */
    private function form(array $changes, $reason = '대시보드 설정 변경')
    {
        $form = ['reason' => $reason];
        foreach (array_merge($this->app->settings()->dashboard(), $changes) as $name => $value) {
            $form[$name] = (string)$value;
        }
        return $form;
    }

    private static function kinds(array $summary)
    {
        return array_column($summary['notifications'], 'kind');
    }

    // ── 요청과 권한 ─────────────────────────────────────────

    public function testSummaryNeedsLoginAndAlwaysAnswersForTheCaller()
    {
        $this->quiet();
        list($status, $response) = $this->api('GET', '/dashboard/summary');
        T::same(401, $status);
        T::same(ApiError::UNAUTHORIZED, $response['error']['code']);
        T::same(401, $this->api('GET', '/dashboard/summary', str_repeat('a', 64))[0]);

        $a = $this->newGuest();
        $b = $this->newGuest();
        $playerA = $this->app->players()->authenticate($a['access_token']);
        $playerB = $this->app->players()->authenticate($b['access_token']);
        $this->give($playerA, 1200);
        $this->give($playerB, 987654321);

        list($status, $response) = $this->api('GET', '/dashboard/summary', $a['access_token']);
        T::same(200, $status);
        T::same(true, $response['ok']);
        $data = $response['data'];
        // 응답은 snake_case 이고, 지시서의 다섯 묶음에 관리자 설정이 붙는다.
        T::eq(['finance', 'my_city', 'national', 'notifications', 'settings', 'updated_at'], array_keys($data));
        T::eq($this->gc($playerA), $data['finance']['gc']);
        T::eq($this->gc($playerA), $data['finance']['net_worth_gc']);
        T::same(false, $data['my_city']['resident']);

        // 다른 사람을 고르는 방법이 없다. 무엇을 붙여 보내도 자기 값이 온다.
        foreach ([['player_id' => $b['player_id']], ['playerId' => $b['player_id']], ['id' => (string)$playerB['id']], ['public_id' => $b['player_id']]] as $query) {
            list($status, $again) = $this->api('GET', '/dashboard/summary', $a['access_token'], $query);
            T::same(200, $status);
            T::eq($data['finance'], $again['data']['finance'], json_encode($query));
            $text = json_encode($again);
            T::ok(strpos($text, '987654321') === false, '다른 사람의 잔액이 응답에 있다');
            T::ok(strpos($text, $b['player_id']) === false, '다른 사람의 계정이 응답에 있다');
        }
        // 응답에 내부 번호, 접속 정보가 없다.
        $text = json_encode($response);
        foreach (['"player_id"', '"id":' . $playerA['id'] . ',', 'guest_secret', 'access_token', '"email"', '"ip"'] as $never) {
            T::ok(strpos($text, $never) === false, $never);
        }
        // B 는 B 의 값을 받는다.
        T::eq(987654321 + ($this->gc($playerA) - 1200), $this->api('GET', '/dashboard/summary', $b['access_token'])[1]['data']['finance']['gc']);

        // 읽기만 하는 주소다. 다른 방식으로는 부를 수 없다.
        foreach (['POST', 'PUT', 'DELETE'] as $method) {
            list($status, $response) = $this->api($method, '/dashboard/summary', $a['access_token'], [], ['gc' => 999999]);
            T::same(false, $response['ok'], $method);
            T::ok($status >= 400 && $status < 500, $method . ' → ' . $status);
        }
        T::eq(1200 + ($this->gc($playerB) - 987654321), $this->gc($playerA));
    }

    public function testFinanceShowsOnlyWhatTheServerHasAndReadingChangesNothing()
    {
        $this->quiet();
        $player = $this->guest();
        $base = $this->gc($player);
        $this->give($player, 500);
        Clock::advance(2 * 86400);
        $this->give($player, 300);
        Clock::advance(86400);

        $ledger = (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries');
        $config = (int)$this->db->val('SELECT COUNT(*) FROM game_config');
        $finance = $this->summary($player)['finance'];
        $this->summary($player);

        T::eq($base + 800, $finance['gc']);
        // 은행, 가상 주식, 대출은 서버에 없다. 0 이라고 하지 않고 "없다"고 한다. 숫자는 지어내지 않는다.
        T::eq([false, 0, 0], [$finance['bankAvailable'], $finance['bankGc'], $finance['termGc']]);
        T::eq([false, 0, 0], [$finance['stocksAvailable'], $finance['stockValueGc'], $finance['stockProfitGc']]);
        T::eq([false, 0], [$finance['loansAvailable'], $finance['loanBalanceGc']]);
        // 순자산은 있는 것으로만 세고, 빠진 항목이 있다고 알린다.
        T::eq([$base + 800, true], [$finance['netWorthGc'], $finance['netWorthPartial']]);

        // 자산 변동: 최근 7일의 하루 끝 잔액 (한국 날짜). 원장의 잔액을 그대로 읽는다.
        T::eq(7, count($finance['trend']));
        $dates = array_column($finance['trend'], 'date');
        T::eq(Clock::koreaDate(), $dates[6]);
        T::eq(Clock::koreaDate(Clock::now() - 6 * 86400), $dates[0]);
        $sorted = $dates;
        sort($sorted);
        T::eq($sorted, $dates);
        T::eq([$base + 500, $base + 500, $base + 800, $base + 800], array_slice(array_column($finance['trend'], 'gc'), 3));
        foreach (array_slice($finance['trend'], 0, 3) as $day) {
            T::ok($day['gc'] <= $base, '계정을 만들기 전의 날: ' . $day['gc']);
        }

        // 요약을 읽어도 원장, 잔액, 설정이 바뀌지 않는다.
        T::eq($ledger, (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries'));
        T::eq($config, (int)$this->db->val('SELECT COUNT(*) FROM game_config'));
        T::eq($base + 800, $this->gc($player));
        T::same(null, $this->app->wallets()->findInconsistency($player['id']));
    }

    // ── 우리 도시 ───────────────────────────────────────────

    public function testMyCityCarriesTheSameValuesAsTheExistingApis()
    {
        $this->quiet();
        $stranger = $this->guest();
        $city = $this->summary($stranger)['myCity'];
        T::eq([false, null, null, 0, false, null], [$city['resident'], $city['regionCode'], $city['name'], $city['validResidents'], $city['alert'], $city['alertReason']]);
        T::eq(0, $this->summary($stranger)['national']['homeRank']);

        $me = $this->setLevel($this->linkedResident('11110'), 10);
        $this->linkedResident('11110');
        $this->give($me, 99500);
        $this->enableTax(\CG\Service\Taxes::TRADE);
        $this->app->combat()->buy($me, 'gear_precision_rifle', $this->key());
        $this->winDefense($me);
        $this->app->growth()->refreshAll();
        $this->setRisk('11110', 4200);

        $me = $this->fresh($me);
        $city = $this->summary($me)['myCity'];
        $status = null;
        foreach ($this->app->city()->risks($me)['cities'] as $row) {
            if ($row['regionCode'] === '11110') {
                $status = $row;
            }
        }
        $growth = $this->app->growth()->city('11110');
        $treasury = $this->app->treasury()->summary('11110');
        $detail = $this->app->city()->detail($me, '11110');

        T::eq([true, '11110', $growth['name']], [$city['resident'], $city['regionCode'], $city['name']]);
        T::eq([42, Risk::CAUTION], [$city['risk'], $city['stage']]);
        T::eq([$status['risk'], $status['stage'], $status['defensePower'], $status['rank']],
            [$city['risk'], $city['stage'], $city['defensePower'], $city['rank']]);
        T::ok($growth['validResidents'] >= 1, '연결된 계정의 주민이 유효 전입자로 세어졌다');
        T::eq([$growth['validResidents'], $growth['stage'], $growth['stageName'], $growth['modelingStatus']],
            [$city['validResidents'], $city['growthStage'], $city['growthStageName'], $city['modelingStatus']]);
        T::ok($city['treasuryGc'] > 0, '거래세가 도시 재정에 들어갔다');
        T::eq($treasury['generalGc'] + $treasury['reserveGc'], $city['treasuryGc']);
        T::ok($city['myContribution'] > 0);
        T::eq($detail['myContribution'], $city['myContribution']);
        T::eq([null, 0, false, false], [$city['invasionKind'], $city['invasionRequired'], $city['supportOpen'], $city['alert']]);

        // 전국 카드의 우리 도시 순위도 지역 랭킹의 값이다.
        T::eq($this->app->city()->rankings($me)['home']['rank'], $this->summary($me)['national']['homeRank']);
    }

    public function testCityAlertFollowsInvasionEmergencyAndTheAdminThreshold()
    {
        $this->quiet();
        $me = $this->resident('11110');
        $today = Clock::koreaDate();

        $summary = $this->summary($me);
        T::eq([false, null, []], [$summary['myCity']['alert'], $summary['myCity']['alertReason'], $summary['notifications']]);

        // 관리자가 정한 위험도 기준: 기본은 70. 기준을 낮추면 경고가 뜨고, 올리면 사라진다.
        $this->setRisk('11110', 3500);
        T::same(false, $this->summary($me)['myCity']['alert']);
        $this->setting(Settings::DASHBOARD, ['cityAlertRisk' => 35]);
        $summary = $this->summary($me);
        T::eq([true, 'risk', Risk::CAUTION], [$summary['myCity']['alert'], $summary['myCity']['alertReason'], $summary['myCity']['stage']]);
        T::eq([['id' => 'city_risk:11110:' . $today, 'kind' => 'city_risk', 'level' => 'warn', 'regionCode' => '11110']],
            array_map(function ($n) {
                return ['id' => $n['id'], 'kind' => $n['kind'], 'level' => $n['level'], 'regionCode' => $n['regionCode']];
            }, $summary['notifications']));
        $this->setting(Settings::DASHBOARD, ['cityAlertRisk' => 36]);
        T::same(false, $this->summary($me)['myCity']['alert']);
        $this->setting(Settings::DASHBOARD, ['cityAlertRisk' => 70]);

        // 침공이 시작되면 위험도와 상관없이 가장 급한 경고가 된다.
        $invasion = $this->app->city()->adminStartInvasion(null, '11110', Invasion::OUTSKIRTS, 2);
        $summary = $this->summary($me);
        $city = $summary['myCity'];
        T::eq([true, 'invasion', Invasion::OUTSKIRTS, 2], [$city['alert'], $city['alertReason'], $city['invasionKind'], $city['invasionSeverity']]);
        T::ok($city['invasionRequired'] > 0 && $city['invasionEndsAt'] !== null);
        T::eq(['city_invasion'], self::kinds($summary));
        T::eq('urgent', $summary['notifications'][0]['level']);
        T::ok(strpos($summary['notifications'][0]['id'], 'city_invasion:inv_') === 0, $summary['notifications'][0]['id']);
        T::ok(strpos($summary['notifications'][0]['title'], '외곽 침공') !== false);

        // 긴급 단계까지 오르면 단계 경고가 더해진다. 같은 일에는 같은 id 가 붙는다 (클라이언트가 한 번만 알린다).
        $this->app->city()->adminAdjustRisk(null, '11110', 60, '테스트: 긴급 단계로');
        $summary = $this->summary($me);
        T::eq(Risk::EMERGENCY, $summary['myCity']['stage']);
        T::eq(['city_invasion', 'city_emergency'], array_slice(self::kinds($summary), 0, 2));
        T::eq(array_column($summary['notifications'], 'id'), array_column($this->summary($me)['notifications'], 'id'));
        T::eq('city_emergency:11110:' . $today, $summary['notifications'][1]['id']);
        // 우리 도시의 일은 전국 알림으로 다시 나오지 않는다.
        T::ok(!in_array('national_emergency', self::kinds($summary), true));

        // 위험도가 높아져도 요약이 지갑을 건드리는 일은 없다.
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND delta < 0', [$me['id']]));

        // 다른 도시의 주민과 이방인에게는 이 도시의 경고가 "우리 도시" 경고로 가지 않는다.
        $other = $this->summary($this->resident('26350'));
        T::eq([false, []], [$other['myCity']['alert'], array_values(array_intersect(self::kinds($other), ['city_invasion', 'city_emergency', 'city_risk']))]);
        T::same(false, $this->summary($this->guest())['myCity']['alert']);
    }

    // ── 전국 ────────────────────────────────────────────────

    public function testNationalFiguresComeFromRiskAndRankingAndNoticesFollowThePolicy()
    {
        $this->quiet();
        $me = $this->resident('11110');
        $this->app->city()->adminAdjustRisk(null, '11140', 80, '테스트: 긴급 단계로');
        $this->app->city()->adminStartInvasion(null, '26350', Invasion::INTRUSION, 1);

        $summary = $this->summary($me);
        $national = $summary['national'];
        $risks = $this->app->city()->risks($me)['cities'];
        $rankings = $this->app->city()->rankings($me);

        $total = 0;
        $emergency = 0;
        $open = 0;
        foreach ($risks as $city) {
            $total += $city['risk'];
            $emergency += $city['stage'] === Risk::EMERGENCY ? 1 : 0;
            $open += $city['supportRequestId'] !== null ? 1 : 0;
        }
        T::eq(count($risks), $national['totalCities']);
        T::ok($national['totalCities'] > 200);
        T::eq((int)round(100 - $total / count($risks)), $national['averageSafety']);
        T::eq([1, $emergency, 1, $open], [$national['emergencyCities'], $national['emergencyCities'], $national['invasionCities'], $national['supportRequests']]);
        T::eq($rankings['home']['rank'], $national['homeRank']);

        // 상위 도시는 지역 랭킹의 앞 다섯 줄이다.
        T::eq(5, count($national['top']));
        foreach ($national['top'] as $index => $row) {
            $expected = $rankings['rows'][$index];
            T::eq([$expected['regionCode'], $expected['rank'], $expected['score'], $expected['risk'], $expected['stage']],
                [$row['regionCode'], $row['rank'], $row['score'], $row['risk'], $row['stage']]);
            T::ok($row['name'] !== '' && $row['name'] !== $row['regionCode']);
        }
        // 긴급 도시: 긴급 단계가 먼저, 그다음이 침공 중인 도시.
        T::eq(['11140', '26350'], array_column($national['urgent'], 'regionCode'));
        T::eq([Risk::EMERGENCY, null], [$national['urgent'][0]['stage'], $national['urgent'][0]['invasionKind']]);
        T::eq(Invasion::INTRUSION, $national['urgent'][1]['invasionKind']);

        // 전국 긴급 알림 정책. 기본(1): 긴급 단계인 도시만.
        $today = Clock::koreaDate();
        T::eq(['national_emergency:11140:' . $today], array_column($summary['notifications'], 'id'));
        T::eq('warn', $summary['notifications'][0]['level']);
        // 2: 침공 중인 도시도.
        $this->setting(Settings::DASHBOARD, ['nationalNotice' => Settings::NOTICE_EMERGENCY_OR_INVASION]);
        T::eq(['national_emergency', 'national_invasion'], self::kinds($this->summary($me)));
        // 한 번에 보여 주는 개수.
        $this->setting(Settings::DASHBOARD, ['nationalNoticeMax' => 1]);
        T::eq(['national_emergency'], self::kinds($this->summary($me)));
        // 0: 전국 알림을 보내지 않는다. 카드의 숫자는 그대로다.
        $this->setting(Settings::DASHBOARD, ['nationalNotice' => Settings::NOTICE_NONE, 'nationalNoticeMax' => 3]);
        $quiet = $this->summary($me);
        T::eq([], $quiet['notifications']);
        T::eq($national['emergencyCities'], $quiet['national']['emergencyCities']);
    }

    // ── 관리자 설정 ─────────────────────────────────────────

    public function testAdminDefaultsReachTheClientWithinTheirRanges()
    {
        $this->quiet();
        $me = $this->guest();
        $settings = $this->summary($me)['settings'];
        T::eq(['refreshSeconds' => 20, 'cityAlertRisk' => 70, 'nationalNotice' => 1, 'sampleData' => false, 'cards' => [
            ['id' => 'finance', 'visible' => true, 'expanded' => true],
            ['id' => 'city', 'visible' => true, 'expanded' => true],
            ['id' => 'national', 'visible' => true, 'expanded' => false],
        ]], $settings);

        $this->setting(Settings::DASHBOARD, ['financeVisible' => 0, 'nationalExpanded' => 1, 'refreshSeconds' => 45, 'sampleData' => 1]);
        $settings = $this->summary($me)['settings'];
        T::eq([45, true], [$settings['refreshSeconds'], $settings['sampleData']]);
        T::eq([false, true], [$settings['cards'][0]['visible'], $settings['cards'][2]['expanded']]);

        // 저장된 값이 범위를 벗어나 있어도 (손으로 고친 경우) 클라이언트에는 범위 안의 값이 간다.
        $this->db->exec("UPDATE game_config SET value = ? WHERE config_key = 'dashboard'", [json_encode(['refreshSeconds' => 0, 'cityAlertRisk' => 900])]);
        $this->app->settings()->forget();
        $settings = $this->summary($me)['settings'];
        T::eq([5, 100], [$settings['refreshSeconds'], $settings['cityAlertRisk']]);

        // 설정표: 모든 값이 정수이고 기본값이 범위 안에 있다.
        foreach (Settings::specs()[Settings::DASHBOARD] as $name => $spec) {
            T::ok(is_int($spec[1]) && is_int($spec[2]) && is_int($spec[3]) && $spec[1] <= $spec[3] && $spec[3] <= $spec[2], $name);
        }
    }

    public function testSettingsScreenChecksRoleReasonRangeAndKeepsHistory()
    {
        $this->quiet();
        $balancer = $this->balancer();
        $operator = $this->operator();
        $viewer = $this->admin('viewer1', Auth::ROLE_VIEWER);

        // 로그인하지 않으면 로그인 화면으로 간다.
        T::eq([['/admin/dashboard', '실시간 대시보드 설정', Auth::CITY_VIEW]], DashboardPages::menu());
        $response = $this->send('GET', '/admin/dashboard');
        T::eq([303, '/admin/login'], [$response[0], self::location($response)]);
        T::same(303, $this->send('POST', '/admin/dashboard/settings', null, $this->form(['refreshSeconds' => 30]))[0]);

        // 도시 운영 화면을 보는 역할에게만 보인다. 현실 혜택 역할에게는 보이지 않는다.
        foreach ([[$balancer, true], [$operator, true], [$viewer, true], [$this->passManager(), false], [$this->compliance(), false]] as $pair) {
            list($admin, $allowed) = $pair;
            list($status, $headers, $html) = $this->get($admin, '/admin/dashboard');
            T::same($allowed ? 200 : 403, $status, $admin['role']);
            T::same($allowed, strpos($this->get($admin, '/admin')[2], 'href="/admin/dashboard"') !== false, $admin['role'] . ' 의 메뉴');
            T::ok(stripos($html, '<script') === false);
            T::ok(preg_match('#(src|href)="(https?:)?//#i', $html) === 0, '외부 자원을 부른다');
            T::ok(in_array('X-Frame-Options: DENY', $headers, true) && in_array('Cache-Control: no-store', $headers, true)
                && in_array('X-Robots-Tag: noindex, nofollow', $headers, true));
            if (!$allowed) {
                T::same(403, $this->post($admin, '/admin/dashboard/settings', $this->form(['refreshSeconds' => 30]))[0], $admin['role']);
            }
        }
        // 정책을 바꿀 수 없는 역할에는 폼이 없다 (로그아웃 폼만 있다).
        T::same(1, preg_match_all('/<form /', $this->get($viewer, '/admin/dashboard')[2]));
        T::same(1, preg_match_all('/<form /', $this->get($operator, '/admin/dashboard')[2]));
        T::same(2, preg_match_all('/<form /', $this->get($balancer, '/admin/dashboard')[2]));

        // 권한, 사유, 범위.
        T::same(422, $this->post($operator, '/admin/dashboard/settings', $this->form(['refreshSeconds' => 30]))[0]);
        T::same(422, $this->post($balancer, '/admin/dashboard/settings', $this->form(['refreshSeconds' => 30], ''))[0]);
        foreach ([['refreshSeconds' => 4], ['refreshSeconds' => 301], ['cityAlertRisk' => 101], ['cityAlertRisk' => -1], ['nationalNotice' => 3],
                     ['nationalNoticeMax' => 0], ['financeVisible' => 2], ['sampleData' => 'yes'], ['refreshSeconds' => '30; DROP']] as $bad) {
            list($status, , $html) = $this->post($balancer, '/admin/dashboard/settings', $this->form($bad));
            T::same(422, $status, json_encode($bad));
            T::ok(strpos($html, '저장하지 않았습니다') !== false);
        }
        $this->app->settings()->forget();
        T::eq(20, $this->app->settings()->dashboard()['refreshSeconds']);
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM config_history'));

        // CSRF 토큰이 없으면 거절된다. 값을 바꾸는 주소는 GET 으로 부를 수 없다.
        $cookie = $this->signIn($balancer)[0];
        T::same(403, $this->send('POST', '/admin/dashboard/settings', $cookie, $this->form(['refreshSeconds' => 30]))[0]);
        T::same(403, $this->send('POST', '/admin/dashboard/settings', $cookie, $this->form(['refreshSeconds' => 30]) + ['_csrf' => str_repeat('0', 64)])[0]);
        T::same(404, $this->get($balancer, '/admin/dashboard/settings')[0]);
        T::same(404, $this->get($balancer, '/admin/dashboard/nothing')[0]);
        T::same(404, $this->post($balancer, '/admin/dashboard', ['reason' => '없는 동작'])[0]);

        // 저장: 이력과 감사 기록이 남고, 클라이언트가 받는 값이 바뀐다.
        $response = $this->post($balancer, '/admin/dashboard/settings', $this->form(
            ['refreshSeconds' => 30, 'nationalExpanded' => 1, 'cityAlertRisk' => 60, 'nationalNotice' => 2, 'sampleData' => 1], '전국 카드를 펼쳐 둔다'));
        T::same('/admin/dashboard?done=saved', self::location($response));
        $this->app->settings()->forget();
        $saved = $this->app->settings()->dashboard();
        T::eq([30, 1, 60, 2, 1], [$saved['refreshSeconds'], $saved['nationalExpanded'], $saved['cityAlertRisk'], $saved['nationalNotice'], $saved['sampleData']]);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM config_history WHERE target_type = 'game_config' AND target_key = 'dashboard'"));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'config.update' AND target_key = 'dashboard'"));
        $settings = $this->summary($this->guest())['settings'];
        T::eq([30, 60, 2, true, true], [$settings['refreshSeconds'], $settings['cityAlertRisk'], $settings['nationalNotice'], $settings['sampleData'],
            $settings['cards'][2]['expanded']]);
        // 같은 값을 다시 저장하면 이력이 늘지 않는다.
        T::same('/admin/dashboard?done=none', self::location($this->post($balancer, '/admin/dashboard/settings', $this->form([], '한 번 더'))));
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM config_history'));
        // 화면에 바뀐 값과 사유가 보인다.
        $html = $this->get($balancer, '/admin/dashboard', ['done' => 'saved'])[2];
        T::ok(strpos($html, '전국 카드를 펼쳐 둔다') !== false && strpos($html, '저장했습니다') !== false);
        T::ok(strpos($html, '20 → 30') !== false, '바뀐 값이 보인다');

        // 변경 이력 화면에서 이전 값으로 되돌린다 (같은 권한과 사유 검사를 거친다).
        $id = (int)$this->db->val("SELECT MAX(id) FROM config_history WHERE target_key = 'dashboard'");
        T::same(422, $this->post($operator, '/admin/history/' . $id . '/revert', ['reason' => '되돌려 봄'])[0]);
        T::same(422, $this->post($balancer, '/admin/history/' . $id . '/revert', ['reason' => ''])[0]);
        T::same('/admin/history?done=reverted', self::location($this->post($balancer, '/admin/history/' . $id . '/revert', ['reason' => '기본값으로'])));
        $this->app->settings()->forget();
        $back = $this->app->settings()->dashboard();
        T::eq([20, 0, 70, 1, 0], [$back['refreshSeconds'], $back['nationalExpanded'], $back['cityAlertRisk'], $back['nationalNotice'], $back['sampleData']]);
        T::eq(2, (int)$this->db->val("SELECT COUNT(*) FROM config_history WHERE target_key = 'dashboard'"));
    }

    public function testAdminScreenShowsNoPlayerFinance()
    {
        $this->quiet();
        $rich = $this->setLevel($this->linkedResident('11110', '비밀부자'), 10);
        $this->give($rich, 987654321);
        $balance = $this->gc($rich);
        T::eq([], $this->app->settings()->save($this->balancer(), Settings::DASHBOARD, $this->form(['refreshSeconds' => 25], '갱신 간격 조정')));

        foreach ([$this->super(), $this->balancer(), $this->operator()] as $admin) {
            $html = $this->get($admin, '/admin/dashboard')[2];
            foreach ([(string)$balance, number_format($balance), '987654321', '987,654,321', '비밀부자', $rich['publicId']] as $never) {
                T::ok(strpos($html, $never) === false, $admin['role'] . ' 의 화면에 이용자의 금융 정보가 있다: ' . $never);
            }
            T::ok(strpos($html, '이용자의 금융 정보를 읽지 않습니다') !== false);
        }
        // 화면을 그리는 동안 지갑과 원장을 읽지 않는다는 것을, 값이 바뀌지 않는 것으로도 확인한다.
        T::eq($balance, $this->gc($rich));
    }
}
