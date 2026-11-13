<?php
use CG\Admin\Auth;
use CG\Admin\CityOpsPages;
use CG\Admin\Kernel;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Infra\Totp;
use CG\Service\Settings;
use CG\Service\Taxes;

/**
 * 웹 관리자의 v9.0 도시 운영 화면 9개: 권한, 알림 확인·담당자 배정, 모델링 제작·검수·배포, 협동 디펜스 모니터링,
 * 전술 핑 차단, 지방세 정책, 도시 재정 집행, 참여예산 검토·집행 (docs/ADMIN_SYSTEM.md 12장).
 */
final class CityOpsAdminTest extends CityTestCase
{
    private $sessions = [];

    private function send($method, $path, $cookie = null, array $form = [], array $query = [])
    {
        $request = Request::make($method, $path, [], [], $query);
        $request->form = $form;
        if ($cookie !== null) {
            $request->cookies = [Auth::COOKIE => $cookie];
        }
        return (new Kernel($this->app, ''))->handle($request);
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

    private function viewer()
    {
        return $this->admin('viewer1', Auth::ROLE_VIEWER);
    }

    private function support()
    {
        return $this->admin('helpdesk', Auth::ROLE_SUPPORT);
    }

    private function audits($action)
    {
        return (int)$this->db->val('SELECT COUNT(*) FROM admin_audit_logs WHERE action = ?', [$action]);
    }

    /** 운영 설정의 폼: 지금 값에 바꿀 값만 덮어쓴다. */
    private function settingsForm($key, array $changes, $reason = '운영 설정 변경')
    {
        $form = ['reason' => $reason];
        foreach (array_merge($this->app->settings()->get($key), $changes) as $name => $value) {
            $form[$name] = (string)$value;
        }
        return $form;
    }

    private function milestone($code, $threshold)
    {
        $this->db->exec('UPDATE city_growth_milestones SET threshold = ? WHERE code = ?', [$threshold, $code]);
    }

    // ── 권한과 화면의 기본 ──────────────────────────────────

    public function testEveryCityScreenNeedsASession()
    {
        T::eq(9, count(CityOpsPages::menu()));
        foreach (CityOpsPages::menu() as $item) {
            $response = $this->send('GET', $item[0]);
            T::same(303, $response[0], $item[0]);
            T::same('/admin/login', self::location($response));
        }
        T::same(303, $this->send('POST', '/admin/tax/trade', null, ['is_active' => '1'])[0]);
    }

    public function testCityScreensAreShownToGameOperationRolesOnly()
    {
        $names = ['도시 성장 관리', '도시별 전입자 통계', '모델링 제작 관리', '도시 콘텐츠 배포 관리', '협동 디펜스 모니터링', '미니맵 및 전술 핑 설정',
            '지방세 정책', '도시 재정', '참여예산 관리'];
        $menu = CityOpsPages::menu();
        foreach ($menu as $index => $item) {
            T::eq($names[$index], $item[1]);
        }
        $admins = [
            [$this->super(), true], [$this->operator(), true], [$this->balancer(), true], [$this->support(), true], [$this->viewer(), true],
            [$this->contentMaker(), true],
            // 현실 혜택 역할 (로그인 횟수 제한 때문에 네 역할만 본다. 나머지는 권한표로 확인한다).
            [$this->passManager(), false], [$this->schManager(), false], [$this->compliance(), false],
            [$this->admin('finance1', Auth::ROLE_FINANCE), false],
        ];
        T::same(false, Auth::can($this->reviewer(1), Auth::CITY_VIEW));
        foreach ($admins as $pair) {
            list($admin, $allowed) = $pair;
            T::same($allowed, Auth::can($admin, Auth::CITY_VIEW), $admin['role']);
            list(, , $home) = $this->get($admin, '/admin');
            foreach ($menu as $item) {
                list($status, $headers, $html) = $this->get($admin, $item[0]);
                T::same($allowed ? 200 : 403, $status, $admin['role'] . ' ' . $item[0]);
                T::same($allowed, strpos($home, 'href="' . $item[0] . '"') !== false, $admin['role'] . ' 의 메뉴: ' . $item[0]);
                T::ok(stripos($html, '<script') === false);
                T::ok(preg_match('#(src|href)="(https?:)?//#i', $html) === 0, $item[0] . ' 가 외부 자원을 부른다');
                T::ok(in_array('X-Frame-Options: DENY', $headers, true) && in_array('Cache-Control: no-store', $headers, true)
                    && in_array('X-Robots-Tag: noindex, nofollow', $headers, true));
                if ($allowed) {
                    T::ok(strpos($html, '<h1>' . $item[1]) !== false || strpos($html, $item[1]) !== false, $item[0] . ' 의 제목');
                }
            }
            // 현실 혜택 역할은 도시 운영 값을 바꾸지 못한다.
            if (!$allowed) {
                T::same(403, $this->post($admin, '/admin/tax/trade', ['is_active' => '1', 'reason' => '권한 확인'])[0], $admin['role']);
                T::same(403, $this->post($admin, '/admin/treasury/11110/spend', ['category' => 'event', 'reason' => '권한 확인'])[0], $admin['role']);
            }
        }
        // 조회 전용 계정에는 값을 바꾸는 폼이 보이지 않는다.
        foreach ($menu as $item) {
            $html = $this->get($this->viewer(), $item[0])[2];
            T::same(1, preg_match_all('/<form /', $html), '조회 전용의 ' . $item[0] . ' 에는 로그아웃 폼만 있어야 한다');
        }
        // 없는 주소와 없는 대상.
        T::same(404, $this->get($this->super(), '/admin/growth/nothing')[0]);
        T::same(404, $this->get($this->super(), '/admin/growth/city/99999')[0]);
        T::same(404, $this->get($this->super(), '/admin/modeling/999')[0]);
        T::same(404, $this->get($this->super(), '/admin/coop/999')[0]);
        T::same(404, $this->get($this->super(), '/admin/budget/999')[0]);
        T::same(404, $this->get($this->super(), '/admin/treasury/99999')[0]);
        T::same(404, $this->post($this->super(), '/admin/growth', ['reason' => '없는 동작'])[0]);
    }

    public function testEveryChangeNeedsACsrfToken()
    {
        $balancer = $this->balancer();
        $cookie = $this->signIn($balancer)[0];
        $paths = ['/admin/tax/trade', '/admin/growth/settings/coop', '/admin/growth/milestones/street_open', '/admin/treasury/11110/spend',
            '/admin/ping/block', '/admin/modeling/create', '/admin/content/1/release', '/admin/budget/1/execute', '/admin/coop/1/close'];
        foreach ($paths as $path) {
            T::same(403, $this->send('POST', $path, $cookie, ['reason' => '토큰 없이 보낸 요청', '_csrf' => str_repeat('0', 64)])[0], $path);
            T::same(403, $this->send('POST', $path, $cookie, ['reason' => '토큰 없이 보낸 요청'])[0], $path);
        }
        T::eq(count($paths) * 2, $this->audits('csrf.rejected'));
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM config_history"));
        // 값을 바꾸는 주소는 GET 으로 부를 수 없다.
        foreach ($paths as $path) {
            T::same(404, $this->get($balancer, $path)[0], 'GET ' . $path);
        }
    }

    // ── 도시 성장 관리, 전입자 통계 ─────────────────────────

    public function testGrowthDashboardShowsCitiesAndSeparatesTheTwoKindsOfNotices()
    {
        $this->milestone('landmark_review', 2);
        $this->linkedResident('11110');
        $this->linkedResident('11110');
        $this->resident('11110');            // 게스트: 유효 전입자로 세지 않는다
        $this->linkedResident('26110');
        $operator = $this->operator();
        $maker = $this->contentMaker();

        list($status, , $html) = $this->get($operator, '/admin/growth');
        T::same(200, $status);
        foreach (['기준 도달 알림', '모델링 진행 알림', '전국 도시', '확장 기준', '모델링 미착수 도시', '제작 진행 중 도시', '모델링 적용 완료 도시',
            '현재 유효 전입자', '누적 전입자', '최근 7일', '최근 30일', '확장 단계', '상징거리 기준 인원', '기준 달성', '모델링 상태', '적용 버전', '정식 개방',
            '도시 코드'] as $text) {
            T::ok(strpos($html, $text) !== false, $text);
        }
        $notice = $this->app->growth()->notifications('milestone', 'new')[0];
        // 새 알림에는 "확인"만 있고, 담당자 배정은 확인한 뒤에 나온다.
        T::ok(strpos($html, '/admin/growth/notifications/' . $notice['id'] . '/acknowledge') !== false);
        T::ok(strpos($html, '/admin/growth/notifications/' . $notice['id'] . '/assign') === false);

        // 제작 담당과 조회 전용은 알림을 처리하지 못한다.
        T::same(422, $this->post($maker, '/admin/growth/notifications/' . $notice['id'] . '/acknowledge')[0]);
        T::same(422, $this->post($this->viewer(), '/admin/growth/notifications/' . $notice['id'] . '/acknowledge')[0]);
        T::eq('new', $this->app->growth()->notification($notice['id'])['status']);

        // 확인 → 담당자 배정.
        $response = $this->post($operator, '/admin/growth/notifications/' . $notice['id'] . '/acknowledge');
        T::same(303, $response[0]);
        T::same('/admin/growth?done=acknowledged', self::location($response));
        $html = $this->get($operator, '/admin/growth')[2];
        T::ok(strpos($html, '/admin/growth/notifications/' . $notice['id'] . '/assign') !== false);
        T::ok(strpos($html, 'notification=' . $notice['id'] . '#create') !== false, '확인한 알림에서 제작 작업을 등록할 수 있다');
        T::same(422, $this->post($operator, '/admin/growth/notifications/' . $notice['id'] . '/assign', ['assignee_admin_id' => '999999'])[0]);
        T::same(303, $this->post($operator, '/admin/growth/notifications/' . $notice['id'] . '/assign', ['assignee_admin_id' => (string)$maker['id']])[0]);
        $row = $this->app->growth()->notification($notice['id']);
        T::eq('assigned', $row['status']);
        T::eq((int)$maker['id'], (int)$row['assigned_admin_id']);
        T::eq(1, $this->audits('growth.notification.acknowledge'));
        T::eq(1, $this->audits('growth.notification.assign'));

        // 알림에서 제작 작업을 등록하면 모델링 진행 알림이 따로 생긴다.
        $response = $this->post($maker, '/admin/modeling/create', ['region_code' => '11110', 'kind' => 'landmark', 'title' => '종로 상징물',
            'assignee_admin_id' => (string)$maker['id'], 'due_on' => '2026-12-31', 'memo' => '보신각을 본뜬 상징물', 'file_version' => '',
            'notification_id' => (string)$notice['id']]);
        T::same(303, $response[0]);
        T::ok(preg_match('#^/admin/modeling/(\d+)\?done=created$#', self::location($response), $m) === 1);
        $html = $this->get($operator, '/admin/growth')[2];
        $milestoneAt = strpos($html, 'id="milestone-notices"');
        $modelingAt = strpos($html, 'id="modeling-notices"');
        $citiesAt = strpos($html, 'id="cities"');
        $created = strpos($html, '제작 작업 등록</td>');
        T::ok($milestoneAt !== false && $modelingAt > $milestoneAt && $citiesAt > $modelingAt);
        T::ok($created > $modelingAt && $created < $citiesAt, '모델링 진행 알림은 기준 도달 알림과 다른 표에 있다');

        // 걸러 보기와 도시 상세.
        $html = $this->get($operator, '/admin/growth', ['filter' => 'in_progress'])[2];
        T::ok(strpos($html, '/admin/growth/city/11110') !== false);
        T::ok(strpos($html, '/admin/growth/city/26110"') === false);
        list($status, , $html) = $this->get($operator, '/admin/growth/city/11110');
        T::same(200, $status);
        T::ok(strpos($html, '종로 상징물') !== false && strpos($html, '전입신고') !== false);
        T::ok(strpos($html, 'modeling_threshold') === false, '운영자에게는 기준을 고치는 폼이 없다');

        // 전입자 통계: 시·도별 합계, 집계 점검.
        list($status, , $html) = $this->get($this->viewer(), '/admin/population');
        T::same(200, $status);
        T::ok(strpos($html, '시·도별 합계') !== false && strpos($html, '○ 유효 전입자의 합이') !== false);
        list($status, , $html) = $this->get($this->viewer(), '/admin/population', ['sido' => '26']);
        T::same(200, $status);
        T::ok(strpos($html, '/admin/growth/city/26110') !== false && strpos($html, '/admin/growth/city/11110"') === false);
    }

    public function testThresholdsAndSettingsAreChangedOnlyByPolicyRolesWithReasonAndHistory()
    {
        $balancer = $this->balancer();
        $operator = $this->operator();
        $this->linkedResident('11110');
        $this->linkedResident('11110');
        $form = ['threshold' => '2', 'name' => '상징물 제작 검토', 'description' => '작은 도시도 살핀다', 'is_active' => '1'];

        // 운영자는 기준을 바꾸지 못한다. 사유가 없으면 저장되지 않는다. 범위를 벗어난 값도.
        T::same(422, $this->post($operator, '/admin/growth/milestones/landmark_review', $form + ['reason' => '기준을 낮춘다'])[0]);
        T::same(422, $this->post($balancer, '/admin/growth/milestones/landmark_review', $form)[0]);
        T::same(422, $this->post($balancer, '/admin/growth/milestones/landmark_review', ['threshold' => '0', 'reason' => '기준을 낮춘다'] + $form)[0]);
        T::same(422, $this->post($balancer, '/admin/growth/milestones/nothing', $form + ['reason' => '기준을 낮춘다'])[0]);
        T::eq(100, (int)$this->db->val("SELECT threshold FROM city_growth_milestones WHERE code = 'landmark_review'"));
        T::eq(0, count($this->app->growth()->notifications('milestone')));

        $response = $this->post($balancer, '/admin/growth/milestones/landmark_review', $form + ['reason' => '기준을 낮춘다']);
        T::same('/admin/growth?done=saved', self::location($response));
        T::eq(2, (int)$this->db->val("SELECT threshold FROM city_growth_milestones WHERE code = 'landmark_review'"));
        // 기준을 낮추면 이미 그 인원을 넘은 도시에 알림이 한 번 생긴다. 다시 저장해도 또 생기지 않는다.
        T::eq(1, count($this->app->growth()->notifications('milestone')));
        $this->post($balancer, '/admin/growth/milestones/landmark_review', ['description' => '설명만 고침'] + $form + ['reason' => '설명을 고친다']);
        T::eq(1, count($this->app->growth()->notifications('milestone')));
        T::eq(2, $this->audits('growth.milestone.update'));

        // 한 도시만의 기준.
        T::same(422, $this->post($operator, '/admin/growth/city/11110/threshold', ['modeling_threshold' => '2', 'reason' => '작은 도시'])[0]);
        T::same(422, $this->post($balancer, '/admin/growth/city/11110/threshold', ['modeling_threshold' => '2'])[0]);
        T::same('/admin/growth/city/11110?done=saved',
            self::location($this->post($balancer, '/admin/growth/city/11110/threshold', ['modeling_threshold' => '2', 'reason' => '작은 도시'])));
        T::eq(1, (int)$this->stats('11110')['threshold_reached']);
        T::ok(strpos($this->get($balancer, '/admin/growth/city/11110')[2], '(이 도시만)') !== false);

        // 운영 설정: 이사 수수료.
        T::same(422, $this->post($operator, '/admin/growth/settings/growth', $this->settingsForm(Settings::GROWTH, ['moveFeeGc' => 500]))[0]);
        T::same(422, $this->post($balancer, '/admin/growth/settings/growth', $this->settingsForm(Settings::GROWTH, ['moveFeeGc' => 500], ''))[0]);
        T::same(422, $this->post($balancer, '/admin/growth/settings/growth', $this->settingsForm(Settings::GROWTH, ['moveFeeGc' => -1]))[0]);
        T::eq(1000, $this->app->settings()->growth()['moveFeeGc']);
        $response = $this->post($balancer, '/admin/growth/settings/growth', $this->settingsForm(Settings::GROWTH, ['moveFeeGc' => 500]) + ['back' => '/admin/growth']);
        T::same('/admin/growth?done=saved', self::location($response));
        $this->app->settings()->forget();
        T::eq(500, $this->app->settings()->growth()['moveFeeGc']);
        T::eq(1, $this->audits('config.update'));
        // 같은 값을 다시 저장하면 이력이 늘지 않는다. 돌아갈 주소는 이 묶음의 화면만 받는다.
        $response = $this->post($balancer, '/admin/growth/settings/growth', $this->settingsForm(Settings::GROWTH, []) + ['back' => 'https://evil.example/']);
        T::same('/admin/growth?done=none', self::location($response));
        $response = $this->post($balancer, '/admin/growth/settings/ping', $this->settingsForm(Settings::PING, ['burst' => 5]) + ['back' => '/admin/ping']);
        T::same('/admin/ping?done=saved', self::location($response));
        $response = $this->post($balancer, '/admin/growth/settings/ping', $this->settingsForm(Settings::PING, ['burst' => 4]) + ['back' => '/admin/risk']);
        T::same('/admin/growth?done=saved', self::location($response));
        T::same(404, $this->post($balancer, '/admin/growth/settings/nothing', ['reason' => '없는 설정'])[0]);
        T::eq(3, $this->audits('config.update'));
    }

    // ── 모델링 제작, 콘텐츠 배포 ────────────────────────────

    public function testModelingRunsFromCreationToReleaseThroughTheScreensWithSeparatedDuties()
    {
        $super = $this->super();
        $operator = $this->operator();
        $maker = $this->contentMaker();
        $this->linkedResident('11110');

        // 등록: 제작 담당이 한다. 조회 전용은 못 한다. 틀린 값은 그대로 다시 보여 준다.
        $form = ['region_code' => '11110', 'kind' => 'street', 'title' => '종로 <b>상징거리</b>', 'assignee_admin_id' => (string)$maker['id'],
            'due_on' => '2026-12-31', 'file_version' => '', 'memo' => "첫 줄\n<script>alert(1)</script>", 'notification_id' => ''];
        T::same(422, $this->post($this->viewer(), '/admin/modeling/create', $form)[0]);
        list($status, , $html) = $this->post($maker, '/admin/modeling/create', ['due_on' => '내일'] + $form);
        T::same(422, $status);
        T::ok(strpos($html, 'value="종로 &lt;b&gt;상징거리&lt;/b&gt;"') !== false, '보낸 값을 다시 보여 준다');
        T::ok(preg_match('#^/admin/modeling/(\d+)\?done=created$#', self::location($this->post($maker, '/admin/modeling/create', $form)), $m) === 1);
        $id = (int)$m[1];
        $base = '/admin/modeling/' . $id;

        // 제목과 메모는 이스케이프되어 나온다.
        foreach ([$base, '/admin/modeling', '/admin/growth', '/admin/growth/city/11110'] as $path) {
            $html = $this->get($operator, $path)[2];
            T::ok(stripos($html, '<script') === false && strpos($html, '<b>상징거리') === false, $path);
            T::ok(strpos($html, '종로 &lt;b&gt;상징거리&lt;/b&gt;') !== false, $path);
        }
        T::ok(strpos($this->get($operator, $base)[2], '&lt;script&gt;alert(1)&lt;/script&gt;') !== false);

        // 상태: 사유가 필요하고, 건너뛸 수 없고, 배포 준비·적용 완료로는 손으로 옮길 수 없다.
        T::same(422, $this->post($maker, $base . '/transition', ['to' => 'PLANNING'])[0]);
        T::same(422, $this->post($maker, $base . '/transition', ['to' => 'MODELING', 'reason' => '건너뛰기'])[0]);
        T::same(422, $this->post($maker, $base . '/transition', ['to' => 'READY', 'reason' => '검수 없이'])[0]);
        T::same(422, $this->post($maker, $base . '/transition', ['to' => 'DEPLOYED', 'reason' => '배포 없이'])[0]);
        T::same(422, $this->post($this->viewer(), $base . '/transition', ['to' => 'PLANNING', 'reason' => '기획 시작'])[0]);
        T::same($base . '?done=moved', self::location($this->post($maker, $base . '/transition', ['to' => 'PLANNING', 'reason' => '기획 시작'])));
        T::same(303, $this->post($maker, $base . '/transition', ['to' => 'MODELING', 'reason' => '제작 시작'])[0]);
        T::eq('MODELING', $this->stats('11110')['modeling_status']);

        // 결과물 등록: 경로와 해시만 적는다.
        $asset = ['version' => '1.0.0', 'address_key' => 'city/11110/street', 'catalog_path' => 'city-11110/1.0.0/catalog.json',
            'content_hash' => str_repeat('ab', 32), 'size_bytes' => '1048576', 'min_client_version' => '9.0.0', 'note' => '첫 판'];
        T::same(422, $this->post($maker, $base . '/assets', ['catalog_path' => 'https://evil.example/catalog.json'] + $asset)[0]);
        T::same(422, $this->post($maker, $base . '/assets', ['catalog_path' => '../catalog.json'] + $asset)[0]);
        T::same($base . '?done=added', self::location($this->post($maker, $base . '/assets', $asset)));
        $assetId = (int)$this->db->val('SELECT id FROM city_modeling_assets WHERE project_id = ?', [$id]);

        // 검수: 담당자 본인은 못 하고, 제작 담당 역할도 못 한다. 화면에도 검수 폼이 나오지 않는다.
        T::same(303, $this->post($maker, $base . '/transition', ['to' => 'REVIEW', 'reason' => '검수 요청'])[0]);
        T::ok(strpos($this->get($maker, $base)[2], $base . '/review') === false);
        T::ok(strpos($this->get($operator, $base)[2], $base . '/review') !== false);
        T::same(422, $this->post($maker, $base . '/review', ['result' => 'pass', 'note' => '내가 만든 것'])[0]);
        T::same(422, $this->post($operator, $base . '/review', ['result' => 'pass', 'note' => ''])[0]);
        T::same(422, $this->post($maker, '/admin/modeling/assets/' . $assetId . '/approve', ['note' => '내가 올린 것'])[0]);
        T::same($base . '?done=reviewed', self::location($this->post($operator, $base . '/review', ['result' => 'pass', 'note' => '기준에 맞음'])));
        T::same(303, $this->post($operator, '/admin/modeling/assets/' . $assetId . '/approve', ['note' => '해시 확인'])[0]);
        T::same(404, $this->post($operator, '/admin/modeling/assets/999999/approve', ['note' => '없는 결과물'])[0]);
        T::eq('READY', $this->app->modeling()->project($id)['status']);

        // 배포: 최고 관리자만. 운영자에게는 배포 폼이 보이지 않는다. 받는 비율과 사유가 있어야 한다.
        $content = '/admin/content/' . $assetId;
        T::ok(strpos($this->get($operator, '/admin/content')[2], $content . '/release') === false);
        $html = $this->get($super, '/admin/content')[2];
        T::ok(strpos($html, $content . '/release') !== false && strpos($html, 'https://cdn.cg.test/content') !== false);
        T::same(422, $this->post($operator, $content . '/release', ['rollout_percent' => '10', 'reason' => '시험 배포'])[0]);
        T::same(422, $this->post($super, $content . '/release', ['rollout_percent' => '0', 'reason' => '시험 배포'])[0]);
        T::same(422, $this->post($super, $content . '/release', ['rollout_percent' => '10'])[0]);
        T::eq('READY', $this->app->modeling()->project($id)['status']);
        T::same('/admin/content?done=released', self::location($this->post($super, $content . '/release', ['rollout_percent' => '10', 'reason' => '시험 배포'])));
        T::eq('DEPLOYED', $this->app->modeling()->project($id)['status']);
        $html = $this->get($operator, '/admin/content')[2];
        T::ok(strpos($html, '일부 클라이언트에 배포 중') !== false && strpos($html, '10%') !== false);
        T::eq(null, $this->stats('11110')['opened_at']);
        // 모든 클라이언트에 배포하면 정식 개방 일시가 적힌다.
        T::same(303, $this->post($super, $content . '/release', ['rollout_percent' => '100', 'reason' => '정식 개방'])[0]);
        T::ok($this->stats('11110')['opened_at'] !== null);
        T::ok(strpos($this->get($operator, '/admin/content')[2], '● 정식 개방') !== false);
        T::ok(strpos($this->get($operator, '/admin/growth', ['filter' => 'deployed'])[2], '/admin/growth/city/11110') !== false);

        // 회수: 운영자도 할 수 있다 (문제가 생겼을 때 빨리 내려야 한다). 제작 담당은 못 한다.
        T::same(422, $this->post($maker, $content . '/withdraw', ['reason' => '문제 발견'])[0]);
        T::same(422, $this->post($operator, $content . '/withdraw', [])[0]);
        T::same('/admin/content?done=withdrawn', self::location($this->post($operator, $content . '/withdraw', ['reason' => '충돌 발견'])));
        T::eq('withdrawn', $this->db->val('SELECT status FROM city_modeling_assets WHERE id = ?', [$assetId]));
        T::eq('READY', $this->app->modeling()->project($id)['status']);

        foreach (['modeling.project.create', 'modeling.project.status', 'modeling.asset.add', 'modeling.project.review', 'modeling.asset.review',
            'modeling.asset.release', 'modeling.asset.withdraw'] as $action) {
            T::ok($this->audits($action) >= 1, $action);
        }
        T::eq(2, $this->audits('modeling.asset.release'));
        // 모델링 진행 알림은 단계마다 쌓였고, 기준 도달 알림은 없다.
        T::ok(count($this->app->growth()->notifications('modeling')) >= 8);
        T::eq(0, count($this->app->growth()->notifications('milestone')));
    }

    // ── 협동 디펜스 모니터링, 전술 핑 ───────────────────────

    public function testCoopMonitoringShowsSessionsRecordsAndLetsOperatorsCloseASession()
    {
        $balancer = $this->balancer();
        $support = $this->support();
        // 기본값은 꺼져 있고, 화면이 그 이유를 알려 준다.
        $html = $this->get($support, '/admin/coop')[2];
        T::ok(strpos($html, '● 플레이어에게 열려 있지 않습니다') !== false && strpos($html, '협동 방어전이 꺼져 있습니다') !== false);
        T::ok(strpos($html, 'test-realtime-secret') === false, '비밀 값은 화면에 나오지 않는다');

        // 켜는 것은 정책 권한(밸런스 담당)만.
        T::same(422, $this->post($support, '/admin/growth/settings/coop', $this->settingsForm(Settings::COOP, ['enabled' => 1]))[0]);
        $response = $this->post($balancer, '/admin/growth/settings/coop', $this->settingsForm(Settings::COOP, ['enabled' => 1], '협동 방어전 시험 개방') + ['back' => '/admin/coop']);
        T::same('/admin/coop?done=saved', self::location($response));
        $this->app->settings()->forget();
        $html = $this->get($support, '/admin/coop')[2];
        T::ok(strpos($html, '○ 협동 방어전이 열려 있습니다') !== false && strpos($html, 'wss://rt.cg.test/ws') !== false);
        T::same(422, $this->post($balancer, '/admin/growth/settings/coop',
            $this->settingsForm(Settings::COOP, ['defaultPlayers' => 8, 'maxPlayers' => 4]))[0]);

        // 한 판을 끝까지 진행한다 (실시간 서버가 결과를 보고한 것과 같다).
        $team = [];
        for ($i = 0; $i < 3; $i++) {
            $team[] = $this->resident('11110');
        }
        $this->db->exec('UPDATE players SET nickname = ? WHERE id = ?', ['<i>a</i>', $team[1]['id']]);
        $coop = $this->app->coop();
        $publicId = $coop->create($team[0], 'outskirts_1', 4, 'commander', $this->key())['session']['id'];
        $coop->join($team[1], $publicId, 'builder', $this->key());
        $coop->join($team[2], $publicId, null, $this->key());
        $pids = [$team[0]['publicId'], $team[1]['publicId'], $team[2]['publicId']];
        $coop->internalStart($publicId, ['by' => $pids[0], 'players' => $pids]);
        $id = (int)$this->db->val('SELECT id FROM defense_sessions WHERE public_id = ?', [$publicId]);

        list($status, , $html) = $this->get($support, '/admin/coop/' . $id);
        T::same(200, $status);
        T::ok(strpos($html, '전투 중') !== false && strpos($html, '/admin/coop/' . $id . '/close') !== false);
        T::ok(strpos($html, '<i>a</i>') === false && strpos($html, '&lt;i&gt;a&lt;/i&gt;') !== false, '닉네임은 이스케이프된다');
        T::ok(strpos($this->get($this->viewer(), '/admin/coop/' . $id)[2], '/close') === false);

        $members = [];
        foreach ($pids as $index => $pid) {
            $members[] = ['pid' => $pid, 'role' => null, 'connectedSeconds' => 120, 'kills' => 10 + $index, 'towersBuilt' => 1, 'towersUpgraded' => 0,
                'repairs' => 0, 'damageDealt' => 500, 'pingsSent' => 1, 'pingsRejected' => $index === 1 ? 1 : 0];
        }
        Clock::advance(120);
        $response = $coop->internalResult($publicId, [
            'result' => 'win', 'wavesCleared' => 5, 'gateHpLeft' => 14, 'durationSeconds' => 120, 'closeReason' => 'completed', 'members' => $members,
            'budget' => [
                ['seq' => 1, 'pid' => null, 'kind' => 'grant_start', 'delta' => 400, 'balanceAfter' => 400, 'slot' => null, 'code' => null, 'atMs' => 0],
                ['seq' => 2, 'pid' => $pids[0], 'kind' => 'build', 'delta' => -100, 'balanceAfter' => 300, 'slot' => 3, 'code' => 'twr_machine_gun', 'atMs' => 61000],
            ],
            'pings' => [
                ['pid' => $pids[0], 'kind' => 'focus_fire', 'x' => 1250, 'z' => -300, 'atMs' => 5000, 'accepted' => true],
                ['pid' => $pids[1], 'kind' => 'repair_here', 'x' => 0, 'z' => 800, 'atMs' => 9100, 'accepted' => false],
            ],
        ]);
        T::eq(true, $response['accepted']);
        $html = $this->get($support, '/admin/coop/' . $id)[2];
        foreach (['○ 방어 성공', '공동 예산 장부', '시작 지급', '타워 설치', 'twr_machine_gun', '1:01', '공격 집중', '● 거절됨', '12.5, -3.0', '지휘관',
            '300 GC / 150 XP'] as $text) {
            T::ok(strpos($html, $text) !== false, $text);
        }
        T::ok(strpos($html, '/close') === false, '끝난 판에는 닫기 폼이 없다');
        $html = $this->get($support, '/admin/coop', ['status' => 'finished'])[2];
        T::ok(strpos($html, $publicId) !== false && strpos($html, '○ 보상의 합이 지갑 원장과 같고') !== false);
        T::ok(strpos($this->get($support, '/admin/coop', ['status' => 'lobby'])[2], $publicId) === false);

        // 대기 중인 판을 닫는다: 사유가 필요하고, 조회 전용은 못 한다.
        $lobby = $coop->create($team[0], 'outskirts_1', 4, null, $this->key())['session']['id'];
        $lobbyId = (int)$this->db->val('SELECT id FROM defense_sessions WHERE public_id = ?', [$lobby]);
        T::same(422, $this->post($this->viewer(), '/admin/coop/' . $lobbyId . '/close', ['reason' => '신고 접수'])[0]);
        T::same(422, $this->post($support, '/admin/coop/' . $lobbyId . '/close', [])[0]);
        T::same('/admin/coop/' . $lobbyId . '?done=closed', self::location($this->post($support, '/admin/coop/' . $lobbyId . '/close', ['reason' => '신고 접수'])));
        T::eq('cancelled', $this->db->val('SELECT status FROM defense_sessions WHERE id = ?', [$lobbyId]));
        T::same(422, $this->post($support, '/admin/coop/' . $lobbyId . '/close', ['reason' => '또 닫기'])[0]);
        T::eq(1, $this->audits('coop.close'));

        // 역할의 보정값: 정책 권한만, 범위 안에서.
        $role = ['build_discount_percent' => '15', 'repair_bonus_percent' => '0', 'attack_bonus_percent' => '0', 'ping_cooldown_percent' => '0',
            'max_per_session' => '0', 'is_active' => '1', 'reason' => '설치 담당 보정 조정'];
        T::same(422, $this->post($support, '/admin/coop/roles/builder', $role)[0]);
        T::same(422, $this->post($balancer, '/admin/coop/roles/builder', ['build_discount_percent' => '51'] + $role)[0]);
        T::same('/admin/coop?done=saved', self::location($this->post($balancer, '/admin/coop/roles/builder', $role)));
        T::eq(15, (int)$this->db->val("SELECT build_discount_percent FROM defense_team_roles WHERE code = 'builder'"));
        T::eq(1, $this->audits('coop.role.update'));
    }

    public function testPingAbuseIsShownAndAccountsAreBlockedAndUnblockedFromTheScreen()
    {
        $support = $this->support();
        $player = $this->resident('11110');
        $html = $this->get($support, '/admin/ping')[2];
        foreach (['공격 집중', '방어 요청', '타워 설치 제안', '수리 요청', '위험 경고', '집결 위치', '전술 핑 차단', '계정의 핑 막기'] as $text) {
            T::ok(strpos($html, $text) !== false, $text);
        }
        // 설정 값은 보이지만 고객지원에게는 고치는 폼이 없다.
        T::ok(strpos($html, '/admin/growth/settings/ping') === false);
        T::ok(strpos($this->get($this->balancer(), '/admin/ping')[2], '/admin/growth/settings/ping') !== false);

        T::same(422, $this->post($this->viewer(), '/admin/ping/block', ['player_id' => $player['publicId'], 'hours' => '24', 'reason' => '핑 도배'])[0]);
        T::same(422, $this->post($support, '/admin/ping/block', ['player_id' => 'p_none', 'hours' => '24', 'reason' => '핑 도배'])[0]);
        T::same(422, $this->post($support, '/admin/ping/block', ['player_id' => $player['publicId'], 'hours' => '99999', 'reason' => '핑 도배'])[0]);
        T::same(422, $this->post($support, '/admin/ping/block', ['player_id' => $player['publicId'], 'hours' => '24'])[0]);
        T::eq(false, $this->app->coop()->isPingBlocked($player['id']));
        T::same('/admin/ping?done=blocked',
            self::location($this->post($support, '/admin/ping/block', ['player_id' => $player['publicId'], 'hours' => '24', 'reason' => '핑 도배'])));
        T::eq(true, $this->app->coop()->isPingBlocked($player['id']));
        T::same(422, $this->post($support, '/admin/ping/block', ['player_id' => $player['publicId'], 'hours' => '24', 'reason' => '또 막기'])[0]);

        $blockId = (int)$this->db->val('SELECT id FROM defense_ping_blocks WHERE player_id = ?', [$player['id']]);
        $html = $this->get($support, '/admin/ping')[2];
        T::ok(strpos($html, '● 막힘') !== false && strpos($html, '/admin/ping/blocks/' . $blockId . '/lift') !== false);
        T::ok(strpos($this->get($this->viewer(), '/admin/ping')[2], '/lift') === false);
        T::same(422, $this->post($support, '/admin/ping/blocks/' . $blockId . '/lift', [])[0]);
        T::same('/admin/ping?done=lifted', self::location($this->post($support, '/admin/ping/blocks/' . $blockId . '/lift', ['reason' => '소명 확인'])));
        T::eq(false, $this->app->coop()->isPingBlocked($player['id']));
        T::eq(1, $this->audits('ping.block'));
        T::eq(1, $this->audits('ping.unblock'));
    }

    // ── 지방세 정책 ─────────────────────────────────────────

    public function testTaxPolicyIsChangedOnlyByPolicyRolesWithinLimits()
    {
        $balancer = $this->balancer();
        $html = $this->get($balancer, '/admin/tax')[2];
        foreach (['거래세', '사업세', '재산세', '○ 꺼짐', '초보자 보호', '현금이나 현실의 세금과 이어지지 않습니다', '징수 합계', '정책 변경 이력'] as $text) {
            T::ok(strpos($html, $text) !== false, $text);
        }
        T::ok(strpos($html, '● 걷는 중') === false, '세금은 기본으로 모두 꺼져 있다');
        $form = ['tax_type' => 'trade', 'is_active' => '1', 'rate_bp' => '500', 'applies_gear_purchase' => '1', 'exempt_amount' => '100',
            'min_level' => '2', 'grace_days' => '0', 'cap_gc' => '0', 'period' => 'transaction', 'reason' => '거래세 시험 적용'];

        // 운영자와 고객지원은 세금 정책을 바꾸지 못한다 (폼도 보이지 않는다).
        foreach ([$this->operator(), $this->support(), $this->viewer()] as $admin) {
            T::same(422, $this->post($admin, '/admin/tax/trade', $form)[0], $admin['role']);
            T::ok(strpos($this->get($admin, '/admin/tax')[2], 'name="rate_bp"') === false, $admin['role']);
        }
        // 세율은 20% 를 넘길 수 없다. 사유가 필요하다. 켜려면 적용 대상이 있어야 한다. 과세할 수 없는 사유는 고를 수 없다.
        list($status, , $html) = $this->post($balancer, '/admin/tax/trade', ['rate_bp' => '2001'] + $form);
        T::same(422, $status);
        T::ok(strpos($html, 'value="2001"') !== false, '틀린 폼은 보낸 값을 다시 보여 준다');
        T::same(422, $this->post($balancer, '/admin/tax/trade', ['reason' => ''] + $form)[0]);
        $none = $form;
        unset($none['applies_gear_purchase']);
        T::same(422, $this->post($balancer, '/admin/tax/trade', $none + ['applies_city_contribution' => '1'])[0]);
        T::same(404, $this->post($balancer, '/admin/tax/income', $form)[0]);
        T::eq(0, (int)$this->db->val("SELECT is_active FROM city_tax_policies WHERE tax_type = 'trade'"));

        T::same('/admin/tax?done=saved', self::location($this->post($balancer, '/admin/tax/trade', $form)));
        $row = $this->app->taxes()->adminPolicy('trade');
        T::eq([1, 500, 'gear_purchase'], [(int)$row['is_active'], (int)$row['rate_bp'], $row['applies_to']]);
        T::same('/admin/tax?done=none', self::location($this->post($balancer, '/admin/tax/trade', $form)));
        T::eq(1, $this->audits('tax.policy.update'));
        $html = $this->get($this->viewer(), '/admin/tax')[2];
        T::ok(strpos($html, '● 걷는 중') !== false && strpos($html, '거래세 시험 적용') !== false && strpos($html, '5%') !== false);

        // 재산세는 한 주 또는 한 달에 한 번만.
        $property = ['tax_type' => 'property', 'is_active' => '1', 'rate_bp' => '100', 'exempt_amount' => '10000', 'min_level' => '10',
            'grace_days' => '30', 'cap_gc' => '500', 'period' => 'transaction', 'reason' => '재산세 시험 적용'];
        T::same(422, $this->post($balancer, '/admin/tax/property', $property)[0]);
        T::same(303, $this->post($balancer, '/admin/tax/property', ['period' => 'monthly'] + $property)[0]);
        T::eq('monthly', $this->app->taxes()->adminPolicy('property')['period']);

        // 걷은 세금은 명세로 보인다. 낸 사람은 공개 ID 로만.
        $this->app->taxes()->forget();
        $buyer = $this->setLevel($this->resident('11110', '세금내는시민'), 4);
        $this->give($buyer, 5000);
        $this->app->combat()->buy($this->fresh($buyer), 'gear_precision_rifle', $this->key());
        $html = $this->get($this->viewer(), '/admin/tax')[2];
        T::ok(strpos($html, $buyer['publicId']) !== false && strpos($html, '치른 값에 포함') !== false && strpos($html, '장비 구매') !== false);
        T::ok(strpos($html, '세금내는시민') === false, '명세에는 닉네임이 나오지 않는다');
        T::eq(27, $this->treasuryGc('11110'));
    }

    // ── 도시 재정, 참여예산 ─────────────────────────────────

    /** 재산세로 종로구의 재정을 채운다 (일반 회계 18,000 GC, 예비비 2,000 GC). */
    private function fillTreasury()
    {
        $this->enableTax(Taxes::PROPERTY, ['rate_bp' => 2000, 'cap_gc' => 0, 'exempt_amount' => 0]);
        $payer = $this->resident('11110');
        $this->give($payer, 99500);
        T::eq(20000, $this->app->taxes()->collectProperty()['totalGc']);
        $this->enableTax(Taxes::PROPERTY, ['is_active' => 0]);
    }

    public function testPolicyChangesAreRevertedFromTheHistoryThroughTheSameChecks()
    {
        $balancer = $this->balancer();
        $history = function ($type, $key) {
            return (int)$this->db->val('SELECT MAX(id) FROM config_history WHERE target_type = ? AND target_key = ?', [$type, $key]);
        };
        $lines = function ($type, $key) {
            return (int)$this->db->val('SELECT COUNT(*) FROM config_history WHERE target_type = ? AND target_key = ?', [$type, $key]);
        };

        // 거래세를 켠다. 변경 이력에 "이전 값으로" 가 생긴다.
        T::same(303, $this->post($balancer, '/admin/tax/trade', ['is_active' => '1', 'rate_bp' => '500', 'applies_gear_purchase' => '1',
            'exempt_amount' => '100', 'min_level' => '2', 'grace_days' => '0', 'cap_gc' => '0', 'period' => 'transaction',
            'reason' => '거래세 시험 적용'])[0]);
        $id = $history('city_tax_policies', 'trade');
        $action = '/admin/history/' . $id . '/revert';
        T::ok(strpos($this->get($balancer, '/admin/history')[2], $action) !== false, '정책 담당에게는 되돌리기 단추가 보인다');

        // 정책 권한이 없는 역할에는 단추가 없고, 요청을 직접 보내도 거절된다. 사유도 필요하다.
        T::ok(strpos($this->get($this->operator(), '/admin/history')[2], $action) === false);
        T::same(422, $this->post($this->operator(), $action, ['reason' => '되돌려 봄'])[0]);
        T::same(422, $this->post($balancer, $action, ['reason' => ''])[0]);
        T::same(422, $this->post($balancer, '/admin/history/999999/revert', ['reason' => '없는 이력'])[0]);
        T::eq(1, (int)$this->app->taxes()->adminPolicy('trade')['is_active']);

        // 되돌리면 처음 값(꺼짐, 3%, 세 가지 대상)으로 돌아가고, 새 이력과 감사 기록이 남는다.
        T::same('/admin/history?done=reverted', self::location($this->post($balancer, $action, ['reason' => '시험이 끝남'])));
        $row = $this->app->taxes()->adminPolicy('trade');
        T::eq([0, 300, 'gear_purchase,gear_upgrade,tower_unlock', 3, 7],
            [(int)$row['is_active'], (int)$row['rate_bp'], $row['applies_to'], (int)$row['min_level'], (int)$row['grace_days']]);
        T::eq(2, $lines('city_tax_policies', 'trade'));
        T::eq(2, $this->audits('tax.policy.update'));
        T::eq('되돌리기 #' . $id . ': 시험이 끝남',
            $this->db->val('SELECT reason FROM config_history WHERE id = ?', [$history('city_tax_policies', 'trade')]));
        // 이미 그 값이면 바뀌는 것이 없다.
        T::same('/admin/history?done=none', self::location($this->post($balancer, $action, ['reason' => '한 번 더'])));
        T::eq(2, $lines('city_tax_policies', 'trade'));

        // 설정: 참여예산을 켰다가 되돌린다.
        $form = ['reason' => '참여예산 시험'];
        foreach ($this->app->settings()->get(Settings::BUDGET) as $name => $value) {
            $form[$name] = (string)$value;
        }
        T::eq([], $this->app->settings()->save($balancer, Settings::BUDGET, ['enabled' => '1', 'quorumVotes' => '9'] + $form));
        T::eq([1, 9], [$this->app->settings()->budget()['enabled'], $this->app->settings()->budget()['quorumVotes']]);
        T::same(303, $this->post($balancer, '/admin/history/' . $history('game_config', Settings::BUDGET) . '/revert', ['reason' => '다시 끔'])[0]);
        T::eq([0, 5], [$this->app->settings()->budget()['enabled'], $this->app->settings()->budget()['quorumVotes']]);
        T::eq(2, $lines('game_config', Settings::BUDGET));

        // 확장 기준, 한 도시만의 기준, 팀 역할.
        T::eq([], $this->app->growth()->saveMilestone($balancer, 'landmark_review',
            ['threshold' => '150', 'name' => '상징물 제작 검토', 'description' => '', 'is_active' => '1', 'reason' => '기준 올림']));
        T::same(303, $this->post($balancer, '/admin/history/' . $history('city_growth_milestones', 'landmark_review') . '/revert',
            ['reason' => '원래대로'])[0]);
        T::eq(100, (int)$this->db->val("SELECT threshold FROM city_growth_milestones WHERE code = 'landmark_review'"));
        T::ok($this->db->val("SELECT description FROM city_growth_milestones WHERE code = 'landmark_review'") !== '', '설명도 돌아온다');

        T::eq([], $this->app->growth()->setCityThreshold($balancer, '11110', '700', '종로구만 낮춤'));
        T::same(303, $this->post($balancer, '/admin/history/' . $history('city_population_stats', '11110') . '/revert', ['reason' => '공통 기준으로'])[0]);
        T::same(null, $this->db->val("SELECT s.modeling_threshold FROM city_population_stats s JOIN regions r ON r.id = s.region_id WHERE r.code = '11110'"));

        T::eq([], $this->app->coop()->saveRole($balancer, 'builder', ['build_discount_percent' => '20', 'repair_bonus_percent' => '0',
            'attack_bonus_percent' => '0', 'ping_cooldown_percent' => '0', 'max_per_session' => '0', 'is_active' => '1', 'reason' => '할인 올림']));
        T::same(303, $this->post($balancer, '/admin/history/' . $history('defense_team_roles', 'builder') . '/revert', ['reason' => '원래대로'])[0]);
        T::eq(10, (int)$this->db->val("SELECT build_discount_percent FROM defense_team_roles WHERE code = 'builder'"));
        T::eq(2, $this->audits('coop.role.update'));
    }

    public function testTreasuryIsSpentFromTheScreenByOperatorsAndEveryLineIsShown()
    {
        $this->fillTreasury();
        $operator = $this->operator();
        $html = $this->get($this->viewer(), '/admin/treasury')[2];
        T::ok(strpos($html, '/admin/treasury/11110') !== false && strpos($html, '18,000') !== false && strpos($html, '2,000') !== false);
        T::ok(strpos($html, '○ 계정마다 원장의 합이 잔액과 같고') !== false && strpos($html, 'CITY PASS 예산, 장학금 예산과는') !== false);

        $form = ['category' => 'defense_facility', 'structure_code' => 'twr_energy_shield', 'facility_code' => '', 'title' => '에너지 방어막 <1단계>',
            'description' => '도시 재정으로 방어막을 짓는다', 'amount_gc' => '5000', 'reason' => '주민 요청'];
        // 조회 전용·밸런스 담당·고객지원은 집행하지 못한다. 폼도 보이지 않는다.
        foreach ([$this->viewer(), $this->balancer(), $this->support()] as $admin) {
            T::same(422, $this->post($admin, '/admin/treasury/11110/spend', $form)[0], $admin['role']);
            T::ok(strpos($this->get($admin, '/admin/treasury/11110')[2], '/spend') === false, $admin['role']);
        }
        T::same(422, $this->post($operator, '/admin/treasury/11110/spend', ['reason' => ''] + $form)[0]);
        T::same(422, $this->post($operator, '/admin/treasury/11110/spend', ['structure_code' => ''] + $form)[0]);
        T::same(422, $this->post($operator, '/admin/treasury/11110/spend', ['amount_gc' => '18001'] + $form)[0]);
        T::same(422, $this->post($operator, '/admin/treasury/11110/spend', ['category' => 'cash_out'] + $form)[0]);
        T::same(404, $this->post($operator, '/admin/treasury/99999/spend', $form)[0]);
        T::eq(18000, $this->treasuryGc('11110'));

        T::same('/admin/treasury/11110?done=spent', self::location($this->post($operator, '/admin/treasury/11110/spend', $form)));
        T::eq(13000, $this->treasuryGc('11110'));
        // 분야가 행사면 대상은 무시되고 집행 내역만 남는다. 예비비 분야는 예비비 계정에서 나간다.
        T::same(303, $this->post($operator, '/admin/treasury/11110/spend', ['category' => 'event', 'title' => '가을 축제', 'amount_gc' => '1000'] + $form)[0]);
        T::same(303, $this->post($operator, '/admin/treasury/11110/spend', ['category' => 'reserve', 'title' => '긴급 복구 준비', 'amount_gc' => '500'] + $form)[0]);
        T::eq(12000, $this->treasuryGc('11110'));
        T::eq(1500, $this->treasuryGc('11110', 'reserve'));
        T::eq(null, $this->db->val("SELECT target_code FROM city_budget_expenditures WHERE category = 'event'"));
        T::eq(3, $this->audits('treasury.spend'));

        list($status, , $html) = $this->get($this->viewer(), '/admin/treasury/11110');
        T::same(200, $status);
        foreach (['에너지 방어막 &lt;1단계&gt;', '가을 축제', '긴급 복구 준비', '시설 기금 +5,000 GC', '−5,000', '+18,000', '재산세', '예산 집행', '관리자 집행',
            '이 도시가 걷은 세금'] as $text) {
            T::ok(strpos($html, $text) !== false, $text);
        }
        T::ok(strpos($html, '<1단계>') === false);
        T::eq(null, $this->app->treasury()->findInconsistency());
        // 플레이어의 지갑은 재정 집행으로 늘지 않는다.
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE reason LIKE 'treasury%' OR ref_type = 'expenditure'"));

        // 재정 설정 (예비비 비율).
        T::same(422, $this->post($operator, '/admin/growth/settings/treasury', $this->settingsForm(Settings::TREASURY, ['reservePercent' => 20]))[0]);
        $response = $this->post($this->balancer(), '/admin/growth/settings/treasury',
            $this->settingsForm(Settings::TREASURY, ['reservePercent' => 20]) + ['back' => '/admin/treasury']);
        T::same('/admin/treasury?done=saved', self::location($response));
    }

    public function testBudgetProposalIsReviewedVotedAndExecutedThroughTheScreens()
    {
        $this->fillTreasury();
        $support = $this->support();
        $operator = $this->operator();
        $html = $this->get($support, '/admin/budget')[2];
        T::ok(strpos($html, '● 참여예산이 꺼져 있습니다') !== false);
        foreach (['예산 제안', '제안 검토', '주민 투표', '결과 확정', '예산 집행', '집행 내역 공개', '계정마다 한 표'] as $text) {
            T::ok(strpos($html, $text) !== false, $text);
        }
        $this->enableBudget(['voterMinResidentDays' => 0, 'quorumVotes' => 3]);

        $proposer = $this->setLevel($this->resident('11110'), 3);
        $voters = [];
        for ($i = 0; $i < 3; $i++) {
            $voters[] = $this->setLevel($this->resident('11110'), 2);
        }
        $proposal = $this->app->budget()->propose($proposer, ['category' => 'education', 'title' => '어린이 <경제> 교실',
            'description' => '세금이 어디에 쓰이는지 배우는 교실을 열어요.', 'requestedGc' => 3000, 'targetCode' => ''], $this->key())['proposal'];
        $id = (int)$this->db->val('SELECT id FROM city_budget_proposals WHERE public_id = ?', [$proposal['id']]);
        $base = '/admin/budget/' . $id;

        // 검토: 고객지원·운영자가 한다. 조회 전용과 밸런스 담당은 못 한다. 의견이 필요하다.
        $html = $this->get($support, $base)[2];
        T::ok(strpos($html, '어린이 &lt;경제&gt; 교실') !== false && strpos($html, '<경제>') === false);
        T::ok(strpos($html, $base . '/approve') !== false && strpos($html, $base . '/execute') === false);
        T::ok(strpos($this->get($this->viewer(), $base)[2], $base . '/approve') === false);
        T::same(422, $this->post($this->viewer(), $base . '/approve', ['reason' => '규칙에 맞음'])[0]);
        T::same(422, $this->post($this->balancer(), $base . '/approve', ['reason' => '규칙에 맞음'])[0]);
        T::same(422, $this->post($support, $base . '/approve', [])[0]);
        T::same(422, $this->post($operator, $base . '/execute', ['reason' => '투표 없이 집행'])[0]);
        T::same($base . '?done=approved', self::location($this->post($support, $base . '/approve', ['reason' => '규칙에 맞음'])));
        T::same(422, $this->post($support, $base . '/reject', ['reason' => '뒤늦은 반려'])[0]);

        // 투표: 찬성 2, 반대 1. 관리자 화면에는 수만 나온다.
        $budget = $this->app->budget();
        $budget->vote($this->fresh($voters[0]), $proposal['id'], 'yes', $this->key());
        $budget->vote($this->fresh($voters[1]), $proposal['id'], 'yes', $this->key());
        $budget->vote($this->fresh($voters[2]), $proposal['id'], 'no', $this->key());
        $html = $this->get($support, $base)[2];
        T::ok(strpos($html, '2 / 1 (기록된 표 3, 최소 3표)') !== false && strpos($html, '투표 중') !== false);
        foreach ($voters as $voter) {
            T::ok(strpos($html, $voter['publicId']) === false && strpos($html, $voter['nickname']) === false, '누가 투표했는지는 보이지 않는다');
        }
        T::same(422, $this->post($operator, $base . '/execute', ['reason' => '투표가 끝나기 전'])[0]);

        // 마감이 지나면 화면을 열 때 결과가 확정된다 (가결). 시계가 넘어갔으므로 다시 로그인한다.
        Clock::advance(8 * 86400);
        $this->sessions = [];
        $html = $this->get($support, $base)[2];
        T::ok(strpos($html, '가결') !== false && strpos($html, $base . '/execute') === false, '고객지원에게는 집행 폼이 없다');
        T::same(422, $this->post($support, $base . '/execute', ['reason' => '가결된 제안 집행'])[0]);
        T::same(422, $this->post($operator, $base . '/execute', [])[0]);
        T::eq(18000, $this->treasuryGc('11110'));
        T::ok(strpos($this->get($operator, $base)[2], $base . '/execute') !== false);
        T::same($base . '?done=executed', self::location($this->post($operator, $base . '/execute', ['reason' => '가결된 제안 집행'])));
        T::eq(15000, $this->treasuryGc('11110'));
        // 한 번만 집행된다.
        T::same(422, $this->post($operator, $base . '/execute', ['reason' => '또 집행'])[0]);
        T::eq(15000, $this->treasuryGc('11110'));
        T::eq(1, $this->audits('budget.execute'));
        T::eq(1, $this->audits('budget.approve'));

        $html = $this->get($this->viewer(), '/admin/treasury/11110')[2];
        T::ok(strpos($html, '참여예산 (주민 투표)') !== false && strpos($html, $base . '"') !== false);
        $html = $this->get($this->viewer(), '/admin/budget', ['status' => 'executed'])[2];
        T::ok(strpos($html, '어린이 &lt;경제&gt; 교실') !== false);

        // 투표 중인 제안의 취소.
        $other = $this->setLevel($this->resident('11110'), 3);
        $second = $budget->propose($other, ['category' => 'event', 'title' => '가을 축제', 'description' => '도시 축제를 열어요.', 'requestedGc' => 1000,
            'targetCode' => ''], $this->key())['proposal'];
        $secondId = (int)$this->db->val('SELECT id FROM city_budget_proposals WHERE public_id = ?', [$second['id']]);
        T::same(303, $this->post($operator, '/admin/budget/' . $secondId . '/approve', ['reason' => '규칙에 맞음'])[0]);
        T::same(422, $this->post($this->viewer(), '/admin/budget/' . $secondId . '/cancel', ['reason' => '규칙 위반 발견'])[0]);
        T::same('/admin/budget/' . $secondId . '?done=cancelled',
            self::location($this->post($support, '/admin/budget/' . $secondId . '/cancel', ['reason' => '규칙 위반 발견'])));
        T::eq('cancelled', $this->db->val('SELECT status FROM city_budget_proposals WHERE id = ?', [$secondId]));
        T::eq(1, $this->audits('budget.cancel'));

        // 참여예산을 켜고 끄는 것은 정책 권한만.
        T::same(422, $this->post($support, '/admin/growth/settings/budget', $this->settingsForm(Settings::BUDGET, ['enabled' => 0]))[0]);
        T::same('/admin/budget?done=saved', self::location($this->post($this->balancer(), '/admin/growth/settings/budget',
            $this->settingsForm(Settings::BUDGET, ['enabled' => 0]) + ['back' => '/admin/budget'])));
    }
}
