<?php
namespace CG\Admin;

use CG\App;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Service\Growth;
use CG\Service\Modeling;
use CG\Service\Settings;

/**
 * 관리자 화면: v9.0 도시 운영 (docs/ADMIN_SYSTEM.md 12장).
 *
 *   도시 성장 관리             /admin/growth
 *   도시별 전입자 통계         /admin/population
 *   모델링 제작 관리           /admin/modeling      (ModelingScreens)
 *   도시 콘텐츠 배포 관리      /admin/content       (ModelingScreens)
 *   협동 디펜스 모니터링       /admin/coop          (CoopScreens)
 *   미니맵 및 전술 핑 설정     /admin/ping          (CoopScreens)
 *   지방세 정책                /admin/tax           (FinanceScreens)
 *   도시 재정                  /admin/treasury      (FinanceScreens)
 *   참여예산 관리              /admin/budget        (FinanceScreens)
 *
 * 보는 것은 CITY_VIEW 권한이 있으면 된다. 바꾸는 일은 일마다 권한이 따로 있고, 서비스가 요청마다 다시 확인한다.
 * 화면은 할 수 없는 일의 폼을 보여 주지 않는다. 값을 바꾸는 요청은 모두 POST 이고 (CSRF 는 Kernel 이 확인한다),
 * 사유를 적어야 하며, 감사 기록에 남는다. 화면에 나가는 값은 모두 Html::e() 를 거친다.
 */
final class CityOpsPages
{
    use ModelingScreens;
    use CoopScreens;
    use FinanceScreens;

    /** @var App */
    private $app;
    /** @var Pages */
    private $pages;
    private $base;
    /** @var array */
    private $admin;
    private $csrf;

    public function __construct(App $app, Pages $pages, $base, array $admin, $csrf)
    {
        $this->app = $app;
        $this->pages = $pages;
        $this->base = $base;
        $this->admin = $admin;
        $this->csrf = $csrf;
    }

    /** 이 묶음이 맡는 주소인가. */
    public static function owns($path)
    {
        return preg_match('#^/admin/(growth|population|modeling|content|coop|ping|tax|treasury|budget)(/|$)#', $path) === 1;
    }

    /** [주소, 이름, 필요한 권한] */
    public static function menu()
    {
        return [
            ['/admin/growth', '도시 성장 관리', Auth::CITY_VIEW],
            ['/admin/population', '도시별 전입자 통계', Auth::CITY_VIEW],
            ['/admin/modeling', '모델링 제작 관리', Auth::CITY_VIEW],
            ['/admin/content', '도시 콘텐츠 배포 관리', Auth::CITY_VIEW],
            ['/admin/coop', '협동 디펜스 모니터링', Auth::CITY_VIEW],
            ['/admin/ping', '미니맵 및 전술 핑 설정', Auth::CITY_VIEW],
            ['/admin/tax', '지방세 정책', Auth::CITY_VIEW],
            ['/admin/treasury', '도시 재정', Auth::CITY_VIEW],
            ['/admin/budget', '참여예산 관리', Auth::CITY_VIEW],
        ];
    }

    // ── 조각 ────────────────────────────────────────────────

    private function url($path)
    {
        return $this->base . $path;
    }

    private function can($permission)
    {
        return Auth::can($this->admin, $permission);
    }

    private function page($title, $content, $status = 200)
    {
        return ['html' => $this->pages->frame($title, $content), 'status' => $status];
    }

    private function denied()
    {
        return $this->page('권한 없음', '<p>이 계정의 역할로는 할 수 없는 일입니다.</p>', 403);
    }

    private function link($path, $text)
    {
        return Html::link($this->url($path), $text);
    }

    private function back($path, $text = '목록으로')
    {
        return '<p class="sub">' . Html::e($this->link($path, $text)) . '</p>';
    }

    private function form($path, $inner, $submit = '저장')
    {
        return Html::form($this->url($path), $this->csrf, $inner, $submit);
    }

    /** 한 줄 폼: 숨은 값 + 사유 + 단추. 표의 칸에 넣는다. */
    private function inline($path, $submit, array $hidden = [], $reasonField = 'reason', $placeholder = '사유')
    {
        $out = '<form method="post" class="inline" action="' . Html::e($this->url($path)) . '">'
            . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '">';
        foreach ($hidden as $name => $value) {
            $out .= '<input type="hidden" name="' . Html::e($name) . '" value="' . Html::e($value) . '">';
        }
        if ($reasonField !== null) {
            $out .= '<input type="text" name="' . Html::e($reasonField) . '" placeholder="' . Html::e($placeholder) . '" aria-label="' . Html::e($placeholder) . '"> ';
        }
        return Html::raw($out . '<button type="submit">' . Html::e($submit) . '</button></form>');
    }

    private static function kst($sqlUtc)
    {
        return $sqlUtc === null || $sqlUtc === '' ? '' : gmdate('Y-m-d H:i', Clock::parse($sqlUtc) + 9 * 3600);
    }

    private static function field(array $form, $key, $default = '')
    {
        return isset($form[$key]) && is_string($form[$key]) ? $form[$key] : $default;
    }

    private static function n($value)
    {
        return number_format((int)$value);
    }

    /** 늘었으면 +, 줄었으면 − 를 붙인다 (색만으로 구분하지 않는다). */
    private static function signed($value)
    {
        $value = (int)$value;
        return ($value > 0 ? '+' : ($value < 0 ? '−' : '')) . number_format(abs($value));
    }

    private static function done(Request $request)
    {
        $messages = [
            'saved' => '저장했습니다. 변경 이력과 감사 기록에 남았습니다.', 'none' => '바뀐 값이 없습니다.',
            'acknowledged' => '알림을 확인했습니다.', 'assigned' => '담당자를 배정했습니다.', 'closed' => '닫았습니다.',
            'created' => '제작 작업을 등록했습니다.', 'moved' => '상태를 바꿨습니다.', 'reviewed' => '검수 결과를 기록했습니다.',
            'added' => '결과물을 등록했습니다.', 'released' => '배포했습니다. 클라이언트는 다음에 목록을 받을 때부터 새 판을 받습니다.',
            'withdrawn' => '회수했습니다. 클라이언트는 다음에 목록을 받을 때부터 기본 도시를 씁니다.',
            'blocked' => '전술 핑을 막았습니다.', 'lifted' => '차단을 풀었습니다.',
            'spent' => '집행했습니다. 집행 내역이 공개되었습니다.', 'approved' => '투표를 열었습니다.', 'rejected' => '반려했습니다.',
            'cancelled' => '취소했습니다.', 'executed' => '집행했습니다. 집행 내역이 공개되었습니다.',
        ];
        $key = self::field($request->query, 'done');
        return Html::notice(isset($messages[$key]) ? $messages[$key] : '');
    }

    /** 고를 수 있는 관리자 목록 [id => 아이디 (역할)]. */
    private function adminOptions($withEmpty = true)
    {
        $roles = Auth::roles();
        $options = $withEmpty ? ['' => '(없음)'] : [];
        foreach ($this->app->db->all('SELECT id, login, role FROM admins WHERE is_active = 1 ORDER BY login') as $row) {
            $options[(int)$row['id']] = $row['login'] . ' (' . (isset($roles[$row['role']]) ? $roles[$row['role']] : $row['role']) . ')';
        }
        return $options;
    }

    /** 운영 설정(Settings)의 폼. CITY_POLICY 권한이 있을 때에만 고칠 수 있다. */
    private function settingsSection($key, $back)
    {
        $specs = Settings::specs()[$key];
        $values = $this->app->settings()->get($key);
        $title = Settings::titles()[$key];
        if (!$this->can(Auth::CITY_POLICY)) {
            $rows = [];
            foreach ($specs as $name => $spec) {
                $rows[] = [$spec[0], self::n($values[$name])];
            }
            return '<h2>설정: ' . Html::e($title) . '</h2>' . Html::table(['항목', '값'], $rows);
        }
        $inner = '<input type="hidden" name="back" value="' . Html::e($back) . '">';
        foreach ($specs as $name => $spec) {
            $inner .= Html::input($name, $spec[0], (string)$values[$name], $spec[1] . ' ~ ' . number_format($spec[2]) . ' (기본 ' . number_format($spec[3]) . ')');
        }
        return '<h2>설정: ' . Html::e($title) . '</h2>' . $this->form('/admin/growth/settings/' . $key, $inner . Html::reason());
    }

    private function settingsSave($key, array $form, Request $request)
    {
        // 돌아갈 화면은 이 묶음의 주소만 받는다.
        $back = self::field($form, 'back');
        if (!self::owns($back) || !preg_match('#^/admin/[a-z]{1,12}$#', $back)) {
            $back = '/admin/growth';
        }
        if (!isset(Settings::specs()[$key])) {
            return null;
        }
        $before = $this->app->settings()->get($key);
        $problems = $this->app->settings()->save($this->admin, $key, $form, $request->ip);
        if ($problems) {
            return $this->page('설정: ' . Settings::titles()[$key], Html::problems($problems) . $this->back($back, '돌아가기'), 422);
        }
        return ['redirect' => $back . '?done=' . ($before === $this->app->settings()->get($key) ? 'none' : 'saved')];
    }

    // ── 경로 ────────────────────────────────────────────────

    /** @return array|null html(+status) 또는 redirect. 없는 주소면 null. */
    public function route($method, $path, Request $request)
    {
        if (!$this->can(Auth::CITY_VIEW)) {
            return $this->denied();
        }
        $get = $method === 'GET';
        $form = $request->form;
        if ($path === '/admin/growth') {
            return $get ? $this->growthPage([], $request) : null;
        }
        if (preg_match('#^/admin/growth/settings/([a-z]{1,12})$#', $path, $m) && !$get) {
            return $this->settingsSave($m[1], $form, $request);
        }
        if (preg_match('#^/admin/growth/notifications/(\d{1,18})/(acknowledge|assign|close)$#', $path, $m) && !$get) {
            $problems = $this->app->growth()->handleNotification($this->admin, (int)$m[1], $m[2],
                $m[2] === 'assign' ? (int)self::field($form, 'assignee_admin_id') : null, $request->ip);
            $done = ['acknowledge' => 'acknowledged', 'assign' => 'assigned', 'close' => 'closed'];
            return $problems ? $this->growthPage($problems, $request) : ['redirect' => '/admin/growth?done=' . $done[$m[2]]];
        }
        if (preg_match('#^/admin/growth/milestones/([a-z_]{1,40})$#', $path, $m) && !$get) {
            $problems = $this->app->growth()->saveMilestone($this->admin, $m[1], $form, $request->ip);
            return $problems ? $this->growthPage($problems, $request) : ['redirect' => '/admin/growth?done=saved'];
        }
        if (preg_match('#^/admin/growth/city/(\d{1,8})$#', $path, $m)) {
            return $get ? $this->cityPage($m[1], [], $request) : null;
        }
        if (preg_match('#^/admin/growth/city/(\d{1,8})/threshold$#', $path, $m) && !$get) {
            $problems = $this->app->growth()->setCityThreshold($this->admin, $m[1], self::field($form, 'modeling_threshold'),
                self::field($form, 'reason'), $request->ip);
            return $problems ? $this->cityPage($m[1], $problems, $request) : ['redirect' => '/admin/growth/city/' . $m[1] . '?done=saved'];
        }
        if ($path === '/admin/population') {
            return $get ? $this->populationPage($request) : null;
        }
        if (strpos($path, '/admin/modeling') === 0 || strpos($path, '/admin/content') === 0) {
            return $this->routeModeling($get, $path, $request);
        }
        if (strpos($path, '/admin/coop') === 0 || strpos($path, '/admin/ping') === 0) {
            return $this->routeCoop($get, $path, $request);
        }
        return $this->routeFinance($get, $path, $request);
    }

    // ── 도시 성장 관리 ──────────────────────────────────────

    private static function filters()
    {
        return ['' => '전체', 'populated' => '주민이 있는 도시', 'reached' => '기준 달성 도시', 'not_started' => '모델링 미착수 도시',
            'in_progress' => '제작 진행 중 도시', 'deployed' => '모델링 적용 완료 도시'];
    }

    private function cityRows(array $cities, $streetThreshold)
    {
        $statuses = Modeling::statuses();
        $names = [];
        foreach ($this->app->growth()->milestones() as $milestone) {
            $names[$milestone['id']] = $milestone['name'];
        }
        $rows = [];
        foreach ($cities as $city) {
            $threshold = $city['modeling_threshold'] !== null ? (int)$city['modeling_threshold'] : $streetThreshold;
            $status = $city['modeling_status'] !== null ? $city['modeling_status'] : Modeling::NOT_STARTED;
            $rows[] = [
                $this->link('/admin/growth/city/' . $city['code'], $city['name']), $city['code'],
                self::n($city['valid_residents']), self::n($city['cumulative_residents']),
                self::signed($city['growth_7d']), self::signed($city['growth_30d']),
                (int)$city['stage'] . ($city['stage_milestone_id'] !== null && isset($names[(int)$city['stage_milestone_id']])
                    ? ' · ' . $names[(int)$city['stage_milestone_id']] : ''),
                ($threshold !== null ? self::n($threshold) : '—') . ($city['modeling_threshold'] !== null ? ' (이 도시만)' : ''),
                (int)$city['threshold_reached'] === 1 ? '● 달성' : '○ 미달',
                isset($statuses[$status]) ? $statuses[$status] : $status,
                (string)$city['content_version'], self::kst($city['opened_at']),
            ];
        }
        return $rows;
    }

    private static function cityHeaders()
    {
        return ['도시', '도시 코드', '현재 유효 전입자', '누적 전입자', '최근 7일', '최근 30일', '확장 단계', '상징거리 기준 인원', '기준 달성',
            '모델링 상태', '적용 버전', '정식 개방 (한국 시간)'];
    }

    private function noticeRows(array $notices, $manage)
    {
        $statuses = ['new' => '● 새 알림', 'acknowledged' => '확인함', 'assigned' => '담당자 배정', 'closed' => '닫힘'];
        $admins = $manage ? $this->adminOptions(false) : [];
        $rows = [];
        foreach ($notices as $notice) {
            $actions = '';
            if ($manage) {
                $base = '/admin/growth/notifications/' . (int)$notice['id'];
                if ($notice['status'] === 'new') {
                    $actions .= Html::e($this->inline($base . '/acknowledge', '확인', [], null));
                } elseif ($notice['status'] !== 'closed') {
                    $select = '<form method="post" class="inline" action="' . Html::e($this->url($base . '/assign')) . '">'
                        . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '">'
                        . '<select name="assignee_admin_id" aria-label="담당자">';
                    foreach ($admins as $id => $label) {
                        $select .= '<option value="' . Html::e($id) . '"' . ((int)$notice['assigned_admin_id'] === (int)$id ? ' selected' : '') . '>'
                            . Html::e($label) . '</option>';
                    }
                    $actions .= $select . '</select> <button type="submit">담당자 배정</button></form> '
                        . Html::e($this->inline($base . '/close', '닫기', [], null));
                    if ($notice['kind'] === Growth::NOTICE_MILESTONE && $this->can(Auth::MODELING_MANAGE)) {
                        $actions .= ' ' . Html::e($this->link('/admin/modeling?region=' . $notice['region_code'] . '&notification=' . (int)$notice['id'] . '#create',
                            '제작 작업 등록'));
                    }
                }
            }
            $rows[] = [self::kst($notice['created_at']), $this->link('/admin/growth/city/' . $notice['region_code'], $notice['region_name']),
                $notice['title'], $notice['body'], $statuses[$notice['status']],
                trim((string)$notice['acknowledged_login'] . ' ' . self::kst($notice['acknowledged_at'])),
                trim((string)$notice['assigned_login'] . ' ' . self::kst($notice['assigned_at'])), Html::raw($actions)];
        }
        return $rows;
    }

    private function growthPage(array $problems, Request $request)
    {
        $growth = $this->app->growth();
        $summary = $growth->adminSummary();
        $filter = self::field($request->query, 'filter');
        $filters = self::filters();
        if (!isset($filters[$filter])) {
            $filter = '';
        }
        $rule = $this->app->settings()->growth()['populationMode'] === Settings::MODE_LINKED
            ? '연결된 계정(게스트가 아닌 로그인 수단이 있는 계정)의 전입신고만 셉니다. 게스트 계정을 여러 개 만들어도 전입자 수는 늘지 않습니다.'
            : '연결된 계정, 그리고 활동으로 확인된 게스트 계정(레벨·가입한 지 지난 날수·플레이한 날수)의 전입신고를 셉니다.';
        $content = self::done($request) . Html::problems($problems)
            . '<p>' . Html::e($rule) . ' 한 계정은 한 도시에만 삽니다.</p>'
            . Html::table(['항목', '값'], [
                ['전국 유효 전입자', self::n($summary['validResidents'])],
                ['전입신고를 한 계정 (게스트 포함)', self::n($summary['registeredResidents'])],
                ['최근 7일 / 30일 증가', self::signed($summary['growth7d']) . ' / ' . self::signed($summary['growth30d'])],
                ['확장 기준을 하나 이상 달성한 도시', self::n($summary['staged'])],
                ['상징거리 기준 달성 도시', self::n($summary['reached'])],
                ['모델링 미착수 도시 (기준은 달성)', self::n($summary['notStarted'])],
                ['제작 진행 중 도시', self::n($summary['inProgress'])],
                ['모델링 적용 완료 도시', self::n($summary['deployed'])],
                ['열려 있는 기준 도달 알림', self::n($summary['openMilestoneNotices'])],
                ['열려 있는 모델링 진행 알림', self::n($summary['openModelingNotices'])],
            ]);

        $manage = $this->can(Auth::GROWTH_MANAGE);
        $content .= '<h2 id="milestone-notices">기준 도달 알림</h2><p>도시의 유효 전입자가 확장 기준에 닿으면 한 번만 생깁니다. '
            . '확인한 뒤에 담당자를 배정하고 제작 작업을 등록할 수 있습니다.</p>'
            . Html::table(['때 (한국 시간)', '도시', '알림', '내용', '상태', '확인', '담당자', '할 일'],
                $this->noticeRows($growth->notifications(Growth::NOTICE_MILESTONE, 'open'), $manage), '열려 있는 기준 도달 알림이 없습니다.');
        $content .= '<h2 id="modeling-notices">모델링 진행 알림</h2><p>제작 작업의 등록, 상태 변경, 검수, 배포, 회수 때 생깁니다.</p>'
            . Html::table(['때 (한국 시간)', '도시', '알림', '내용', '상태', '확인', '담당자', '할 일'],
                $this->noticeRows($growth->notifications(Growth::NOTICE_MODELING, 'open', 100), $manage), '열려 있는 모델링 진행 알림이 없습니다.');

        $tabs = '';
        foreach ($filters as $key => $name) {
            $tabs .= ($key === $filter ? '<strong>' . Html::e($name) . '</strong>' : Html::e($this->link('/admin/growth' . ($key !== '' ? '?filter=' . $key : ''), $name))) . ' · ';
        }
        $content .= '<h2 id="cities">전국 도시</h2><p>' . rtrim($tabs, ' ·') . '</p>'
            . Html::table(self::cityHeaders(), $this->cityRows($growth->adminCities($filter !== '' ? $filter : null), $growth->streetThreshold()),
                '해당하는 도시가 없습니다.');

        $content .= '<h2 id="milestones">확장 기준</h2><p>기준 인원을 고치면 모든 도시를 새 기준으로 다시 점검합니다. '
            . '이미 오른 확장 단계는 내려가지 않습니다.</p>';
        foreach ($growth->milestones() as $milestone) {
            $line = Html::e($milestone['name']) . ' — ' . Html::e(self::n($milestone['threshold'])) . '명'
                . ($milestone['opensStreet'] ? ' (상징거리 모델링 기준)' : '') . ($milestone['active'] ? '' : ' [사용 안 함]');
            if ($this->can(Auth::CITY_POLICY)) {
                $content .= '<h3>' . $line . '</h3>' . $this->form('/admin/growth/milestones/' . $milestone['code'],
                    Html::input('threshold', '기준 인원', (string)$milestone['threshold'], '1 ~ 10,000,000')
                    . Html::input('name', '이름', $milestone['name']) . Html::input('description', '설명', $milestone['description'])
                    . Html::checkbox('is_active', '사용', $milestone['active']) . Html::reason());
            } else {
                $content .= '<p>' . $line . ' — ' . Html::e($milestone['description']) . '</p>';
            }
        }
        $content .= $this->settingsSection(Settings::GROWTH, '/admin/growth');
        return $this->page('도시 성장 관리', $content, $problems ? 422 : 200);
    }

    private function cityPage($regionCode, array $problems, Request $request)
    {
        $growth = $this->app->growth();
        $city = $growth->adminCity($regionCode);
        if ($city === null) {
            return null;
        }
        $regionId = (int)$city['id'];
        $content = $this->back('/admin/growth', '도시 성장 관리로') . self::done($request) . Html::problems($problems)
            . Html::table(self::cityHeaders(), $this->cityRows([$city], $growth->streetThreshold()))
            . '<p>전입신고를 한 계정(게스트 포함): ' . Html::e(self::n($city['registered_residents'])) . ' · 마지막 집계: '
            . Html::e(self::kst($city['counted_at'])) . ' · 기준 달성: ' . Html::e(self::kst($city['threshold_reached_at'])) . '</p>';

        if ($this->can(Auth::CITY_POLICY)) {
            $content .= '<h2>이 도시의 상징거리 기준 인원</h2><p>비워 두면 공통 기준을 씁니다.</p>'
                . $this->form('/admin/growth/city/' . $city['code'] . '/threshold',
                    Html::input('modeling_threshold', '기준 인원', $city['modeling_threshold'] !== null ? (string)$city['modeling_threshold'] : '',
                        '1 ~ 10,000,000 또는 비움') . Html::reason());
        }

        $statuses = Modeling::statuses();
        $kinds = Modeling::kinds();
        $rows = [];
        foreach ($this->app->modeling()->projects(null, $regionId) as $project) {
            $rows[] = [$this->link('/admin/modeling/' . (int)$project['id'], $project['title']), $kinds[$project['kind']],
                $statuses[$project['status']], (string)$project['assignee_login'], (string)$project['due_on'], $project['file_version'],
                self::kst($project['applied_at'])];
        }
        $content .= '<h2>제작 작업</h2>' . Html::table(['작업', '종류', '상태', '담당자', '예정일', '파일 버전', '적용 일시'], $rows, '등록된 제작 작업이 없습니다.');
        if ($this->can(Auth::MODELING_MANAGE)) {
            $content .= '<p>' . Html::e($this->link('/admin/modeling?region=' . $city['code'] . '#create', '이 도시의 제작 작업 등록')) . '</p>';
        }

        $rows = [];
        foreach ($growth->adminTrend($regionId) as $day) {
            $rows[] = [$day['date'], self::n($day['valid_residents']), self::n($day['registered_residents'])];
        }
        $content .= '<h2>증가 추이 (최근 60일)</h2>' . Html::table(['날짜', '유효 전입자', '전입신고를 한 계정'], $rows, '아직 기록이 없습니다.');

        $kindsOfEvent = [Growth::EVENT_REGISTER => '전입신고', Growth::EVENT_MOVE_IN => '이사 옴', Growth::EVENT_MOVE_OUT => '이사 감'];
        $rows = [];
        foreach ($growth->adminEvents($regionId) as $event) {
            $rows[] = [self::kst($event['created_at']), isset($kindsOfEvent[$event['kind']]) ? $kindsOfEvent[$event['kind']] : $event['kind'],
                $event['public_id'], (string)$event['other_name'], (int)$event['fee_gc'] > 0 ? self::n($event['fee_gc']) . ' GC' : ''];
        }
        $content .= '<h2>전입·이사 기록 (최근 100건)</h2><p>계정은 공개 ID 로만 보입니다.</p>'
            . Html::table(['때 (한국 시간)', '종류', '계정', '상대 도시', '수수료'], $rows, '아직 기록이 없습니다.');
        return $this->page($city['name'] . ' — 도시 성장', $content, $problems ? 422 : 200);
    }

    // ── 도시별 전입자 통계 ──────────────────────────────────

    private function populationPage(Request $request)
    {
        $growth = $this->app->growth();
        $sidos = ['' => '전국'];
        foreach ($this->app->db->all('SELECT code, name FROM sidos ORDER BY id') as $row) {
            $sidos[$row['code']] = $row['name'];
        }
        $sido = self::field($request->query, 'sido');
        if (!isset($sidos[$sido])) {
            $sido = '';
        }
        $tabs = '';
        foreach ($sidos as $code => $name) {
            $tabs .= ((string)$code === (string)$sido ? '<strong>' . Html::e($name) . '</strong>' : Html::e($this->link('/admin/population' . ($code !== '' ? '?sido=' . $code : ''), $name))) . ' · ';
        }
        // 시·도별 합계
        $totals = [];
        foreach ($growth->adminCities() as $city) {
            $key = $city['sido_code'];
            if (!isset($totals[$key])) {
                $totals[$key] = ['cities' => 0, 'valid' => 0, 'registered' => 0, 'cumulative' => 0, 'g7' => 0, 'g30' => 0, 'reached' => 0];
            }
            $totals[$key]['cities']++;
            $totals[$key]['valid'] += (int)$city['valid_residents'];
            $totals[$key]['registered'] += (int)$city['registered_residents'];
            $totals[$key]['cumulative'] += (int)$city['cumulative_residents'];
            $totals[$key]['g7'] += (int)$city['growth_7d'];
            $totals[$key]['g30'] += (int)$city['growth_30d'];
            $totals[$key]['reached'] += (int)$city['threshold_reached'];
        }
        $rows = [];
        foreach ($totals as $code => $total) {
            $rows[] = [$this->link('/admin/population?sido=' . $code, isset($sidos[$code]) ? $sidos[$code] : $code), self::n($total['cities']),
                self::n($total['valid']), self::n($total['registered']), self::n($total['cumulative']), self::signed($total['g7']),
                self::signed($total['g30']), self::n($total['reached'])];
        }
        $content = '<p>시·군·구별 전입자 수입니다. "유효 전입자"만 도시 확장 기준에 쓰입니다. 수는 전입·이사·계정 상태가 바뀔 때마다 다시 세고, '
            . '예약 작업이 하루의 기록을 남깁니다.</p>'
            . '<h2>시·도별 합계</h2>'
            . Html::table(['시·도', '도시 수', '유효 전입자', '전입신고를 한 계정', '누적 전입자', '최근 7일', '최근 30일', '상징거리 기준 달성 도시'], $rows)
            . '<h2>도시별</h2><p>' . rtrim($tabs, ' ·') . '</p>'
            . Html::table(self::cityHeaders(), $this->cityRows($growth->adminCities(null, $sido !== '' ? $sido : null), $growth->streetThreshold()));
        $problem = $growth->findInconsistency();
        $content .= '<h2>집계 점검</h2><p>' . ($problem === null
            ? '○ 유효 전입자의 합이 조건에 맞는 계정의 수와 같습니다 (한 계정이 두 도시에 세어지지 않았습니다).'
            : '● 맞지 않는 곳이 있습니다: ' . Html::e($problem)) . '</p>';
        return $this->page('도시별 전입자 통계', $content);
    }
}
