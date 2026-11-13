<?php
namespace CG\Admin;

use CG\App;
use CG\Domain\Invasion;
use CG\Domain\Ranking;
use CG\Domain\Risk;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Service\CityOpsHistory;

/**
 * 관리자 화면 (docs/ADMIN_SYSTEM.md 4장의 v8.0 메뉴).
 *
 *   1 전투 모드  2 적 유닛  3 디펜스 구역  4 타워 및 방어시설  5 FPS 임무  6 전투 장비  7 장비 가격 및 성능
 *   8 도시 위험도  9 위험도 계산 정책  10 침공 이벤트  11 도시 간 지원  12 지역 랭킹  13 전투 통계
 *   + 변경 이력, 감사 기록
 *
 * 화면에 나가는 값은 모두 Html::e() 를 거친다.
 */
final class Pages
{
    /** @var App */
    private $app;
    /** @var Auth */
    private $auth;
    private $base;
    /** @var array|null */
    private $admin = null;
    private $csrf = '';
    /** @var DefEditor */
    private $defs;
    /** @var PolicyEditor */
    private $policy;

    public function __construct(App $app, Auth $auth, $base)
    {
        $this->app = $app;
        $this->auth = $auth;
        $this->base = $base;
        $this->defs = new DefEditor($app, $auth);
        $this->policy = new PolicyEditor($app, $auth);
    }

    public function signedIn(array $admin, $csrf)
    {
        $this->admin = $admin;
        $this->csrf = $csrf;
    }

    // ── 틀 ──────────────────────────────────────────────────

    public static function menu()
    {
        return [
            ['/admin/defs/modes', '1. 전투 모드'],
            ['/admin/defs/enemies', '2. 적 유닛'],
            ['/admin/defs/zones', '3. 디펜스 구역'],
            ['/admin/defs/structures', '4. 타워 및 방어시설'],
            ['/admin/defs/fps', '5. FPS 임무'],
            ['/admin/defs/gear', '6. 전투 장비'],
            ['/admin/defs/gear_stats', '7. 장비 가격 및 성능'],
            ['/admin/risk', '8. 도시 위험도'],
            ['/admin/policy', '9. 위험도 계산 정책'],
            ['/admin/invasions', '10. 침공 이벤트'],
            ['/admin/support', '11. 도시 간 지원'],
            ['/admin/ranking', '12. 지역 랭킹'],
            ['/admin/stats', '13. 전투 통계'],
            ['/admin/history', '변경 이력'],
            ['/admin/audit', '감사 기록'],
        ];
    }

    /** 이 계정이 볼 수 있는 메뉴: 게임 운영(VIEW 가 있을 때) + 현실 혜택(권한마다). */
    public function visibleMenu()
    {
        if ($this->admin === null) {
            return [];
        }
        $items = [];
        foreach (self::menu() as $item) {
            $permission = $item[0] === '/admin/audit' ? Auth::AUDIT : Auth::VIEW;
            if (Auth::can($this->admin, $permission)) {
                $items[] = $item;
            }
        }
        foreach (array_merge(CityOpsPages::menu(), DashboardPages::menu(), BenefitPages::menu()) as $item) {
            if (Auth::can($this->admin, $item[2])) {
                $items[] = [$item[0], $item[1]];
            }
        }
        return $items;
    }

    /** 다른 화면 묶음(BenefitPages)이 같은 틀을 쓴다. $content 는 이미 이스케이프를 마친 HTML 이다. */
    public function frame($title, $content)
    {
        return $this->layout($title, $content);
    }

    /** 게임 운영 화면을 볼 수 없는 역할의 첫 화면: 자기 업무의 메뉴. */
    public function workMenu()
    {
        $links = '';
        foreach ($this->visibleMenu() as $item) {
            $links .= '<li>' . Html::e(Html::link($this->url($item[0]), $item[1])) . '</li>';
        }
        return $this->layout('업무 메뉴', $links !== '' ? '<ul>' . $links . '</ul>' : '<p>이 계정의 역할로 볼 수 있는 화면이 없습니다.</p>');
    }

    private function url($path)
    {
        return $this->base . $path;
    }

    private function layout($title, $content)
    {
        $e = function ($value) {
            return Html::e($value);
        };
        $out = '<!doctype html><html lang="ko"><head><meta charset="utf-8">'
            . '<meta name="robots" content="noindex, nofollow"><meta name="viewport" content="width=device-width, initial-scale=1">'
            . '<title>' . $e($title) . ' — CITY GUARDIANS 관리자</title>'
            . '<link rel="stylesheet" href="' . $e($this->url('/assets/admin.css')) . '"></head><body>';
        if ($this->admin !== null) {
            $out .= '<nav aria-label="관리자 메뉴"><p class="brand"><a href="' . $e($this->url('/admin')) . '">CITY GUARDIANS 관리자</a></p><ul>';
            // 메뉴에는 이 계정의 역할로 볼 수 있는 화면만 나온다.
            foreach ($this->visibleMenu() as $item) {
                $out .= '<li><a href="' . $e($this->url($item[0])) . '">' . $e($item[1]) . '</a></li>';
            }
            $roles = Auth::roles();
            $out .= '</ul><p class="who">' . $e($this->admin['login']) . ' (' . $e($roles[$this->admin['role']]) . ')</p>'
                . '<form method="post" action="' . $e($this->url('/admin/logout')) . '"><input type="hidden" name="_csrf" value="'
                . $e($this->csrf) . '"><button type="submit">로그아웃</button></form></nav>';
        }
        return $out . '<main><h1>' . $e($title) . '</h1>' . $content . '</main></body></html>';
    }

    public function message($title, $text)
    {
        return $this->layout($title, '<p>' . Html::e($text) . '</p>');
    }

    public function login($error)
    {
        $inner = Html::input('login', '아이디', '') . Html::input('password', '비밀번호', '', '', 'password')
            . Html::input('code', '일회용 코드 (6자리)', '', '일회용 코드 앱에 보이는 숫자');
        // 로그인 전에는 접속이 없으므로 CSRF 토큰 대신 출처(Origin) 확인과 SameSite 쿠키로 막는다.
        return $this->layout('로그인', Html::problems($error !== '' ? [$error] : [])
            . '<form method="post" action="' . Html::e($this->url('/admin/login')) . '">' . $inner
            . '<p><button type="submit">로그인</button></p></form>');
    }

    private function page($title, $content, $status = 200)
    {
        return ['html' => $this->layout($title, $content), 'status' => $status];
    }

    private function denied()
    {
        return $this->page('권한 없음', '<p>이 계정의 역할로는 할 수 없는 일입니다.</p>', 403);
    }

    private function can($permission)
    {
        return Auth::can($this->admin, $permission);
    }

    private static function done($request)
    {
        $messages = [
            'saved' => '저장했습니다. 변경 이력에 남았습니다.', 'none' => '바뀐 값이 없습니다.',
            'adjusted' => '위험도를 조정했습니다. 위험도 이력에 남았습니다.', 'started' => '침공을 시작했습니다.',
            'ended' => '침공을 끝냈습니다.', 'reverted' => '이전 값으로 되돌렸습니다.',
        ];
        $key = isset($request->query['done']) && is_string($request->query['done']) ? $request->query['done'] : '';
        return Html::notice(isset($messages[$key]) ? $messages[$key] : '');
    }

    private static function stage($stage)
    {
        // 색만으로 구분하지 않는다: 모양과 글자를 함께 쓴다.
        return Html::raw('<span class="stage stage-' . Html::e($stage) . '">' . Html::e(Risk::stageIcon($stage)) . ' '
            . Html::e(Risk::stageName($stage)) . '</span>');
    }

    private static function kst($sqlUtc)
    {
        return $sqlUtc === null || $sqlUtc === '' ? '' : gmdate('Y-m-d H:i', Clock::parse($sqlUtc) + 9 * 3600);
    }

    private static function kstUnix($unix)
    {
        return gmdate('Y-m-d H:i', $unix + 9 * 3600);
    }

    // ── 경로 ────────────────────────────────────────────────

    /** @return array|null html(+status) 또는 redirect. 없는 주소면 null. */
    public function route($method, $path, Request $request)
    {
        $get = $method === 'GET';
        $form = $request->form;
        $ip = $request->ip;
        if ($path === '/admin' || $path === '') {
            return $get ? $this->dashboard() : null;
        }
        if (preg_match('#^/admin/defs/([a-z_]{1,20})$#', $path, $m)) {
            $screen = Catalog::find($m[1]);
            return $screen !== null && $get ? $this->defList($m[1], $screen, $request) : null;
        }
        if (preg_match('#^/admin/defs/([a-z_]{1,20})/([A-Za-z0-9_\-]{1,40})$#', $path, $m)) {
            $screen = Catalog::find($m[1]);
            if ($screen === null) {
                return null;
            }
            return $get ? $this->defEdit($m[1], $screen, $m[2], null, []) : $this->defSave($m[1], $screen, $m[2], $form, $ip);
        }
        if (preg_match('#^/admin/config/([a-z]{1,20})$#', $path, $m) && !$get) {
            return $this->configSave($m[1], $form, $ip);
        }
        if ($path === '/admin/risk') {
            return $get ? $this->riskList($request) : null;
        }
        if (preg_match('#^/admin/risk/(\d{1,8})$#', $path, $m)) {
            return $get ? $this->riskDetail($m[1], [], $request) : null;
        }
        if (preg_match('#^/admin/risk/(\d{1,8})/adjust$#', $path, $m) && !$get) {
            return $this->riskAdjust($m[1], $form, $request);
        }
        if ($path === '/admin/policy') {
            return $get ? $this->policyPage([], null, $request) : $this->policySave($form, $ip, '/admin/policy', $request);
        }
        if ($path === '/admin/policy/revert' && !$get) {
            return $this->policyRevert($form, $ip, $request);
        }
        if ($path === '/admin/invasions') {
            return $get ? $this->invasionsPage([], $request) : null;
        }
        if ($path === '/admin/invasions/policy' && !$get) {
            return $this->policySave($form, $ip, '/admin/invasions', $request);
        }
        if ($path === '/admin/invasions/start' && !$get) {
            return $this->invasionStart($form, $request);
        }
        if (preg_match('#^/admin/invasions/(\d{1,18})/end$#', $path, $m) && !$get) {
            return $this->invasionEnd((int)$m[1], $request);
        }
        if ($path === '/admin/support') {
            return $get ? $this->supportPage([], $request) : $this->policySave($form, $ip, '/admin/support', $request);
        }
        if ($path === '/admin/ranking') {
            return $get ? $this->rankingPage([], $request) : $this->policySave($form, $ip, '/admin/ranking', $request);
        }
        if ($path === '/admin/stats') {
            return $get ? $this->statsPage() : null;
        }
        if ($path === '/admin/history') {
            return $get ? $this->historyPage($request, []) : null;
        }
        if (preg_match('#^/admin/history/(\d{1,18})/revert$#', $path, $m) && !$get) {
            return $this->historyRevert((int)$m[1], $form, $ip, $request);
        }
        if ($path === '/admin/audit') {
            return $get ? $this->auditPage($request) : null;
        }
        return null;
    }

    // ── 대시보드 ────────────────────────────────────────────

    private function dashboard()
    {
        $db = $this->app->db;
        $cities = $this->app->city()->adminOverview();
        $stages = [Risk::SAFE => 0, Risk::CAUTION => 0, Risk::DANGER => 0, Risk::EMERGENCY => 0];
        $active = 0;
        foreach ($cities as $city) {
            $stages[$city['stage']]++;
            $active += $city['invasionKind'] !== null ? 1 : 0;
        }
        $rows = [];
        foreach ($stages as $stage => $count) {
            $rows[] = [self::stage($stage), $count];
        }
        $since = Clock::sql(Clock::now() - 86400);
        $facts = [
            ['진행 중인 침공', $active],
            ['열려 있는 지원 요청', (int)$db->val("SELECT COUNT(*) FROM city_support_requests WHERE status = 'open'")],
            ['최근 24시간 방어전', (int)$db->val('SELECT COUNT(*) FROM defense_battles WHERE started_at >= ?', [$since])],
            ['최근 24시간 FPS 임무', (int)$db->val('SELECT COUNT(*) FROM fps_mission_results WHERE started_at >= ?', [$since])],
            ['최근 24시간 사냥', (int)$db->val('SELECT COUNT(*) FROM hunt_sessions WHERE last_report_at >= ?', [$since])],
            ['최근 24시간 거절된 전투 보고', (int)$db->val('SELECT COUNT(*) FROM defense_battles WHERE accepted = 0 AND ended_at >= ?', [$since])
                + (int)$db->val('SELECT COUNT(*) FROM fps_mission_results WHERE accepted = 0 AND ended_at >= ?', [$since])],
            ['마지막 침공 판정', self::kst($db->val('SELECT updated_at FROM city_world_state WHERE id = 1')) . ' (한국 시간)'],
            ['최근 24시간 서버 오류', (int)$db->val("SELECT COUNT(*) FROM system_logs WHERE level = 'error' AND created_at >= ?", [$since])],
        ];
        return $this->page('운영 현황', '<h2>도시 위험 단계</h2>' . Html::table(['단계', '도시 수'], $rows)
            . '<h2>오늘</h2>' . Html::table(['항목', '값'], $facts));
    }

    // ── 1~7: 정의 데이터 ────────────────────────────────────

    private function subLinks($key)
    {
        $links = [];
        if ($key === 'enemies' || $key === 'hunt_zones') {
            $links = [['/admin/defs/enemies', '적 유닛'], ['/admin/defs/hunt_zones', '사냥 구역']];
        }
        if ($key === 'facilities') {
            $links = [['/admin/risk', '도시 위험도'], ['/admin/defs/facilities', '도시 주요 시설']];
        }
        $out = '';
        foreach ($links as $link) {
            $out .= Html::e(Html::link($this->url($link[0]), $link[1])) . ' ';
        }
        return $out !== '' ? '<p class="sub">' . $out . '</p>' : '';
    }

    private function defList($key, array $screen, Request $request)
    {
        $headers = [];
        foreach ($screen['list'] as $column) {
            $headers[] = Catalog::label($screen, $column);
        }
        $headers[] = '';
        $rows = [];
        foreach ($this->defs->rows($screen) as $row) {
            $cells = [];
            foreach ($screen['list'] as $column) {
                $value = $row[$column];
                if (isset($screen['fields'][$column]) && $screen['fields'][$column]['type'] === 'bool') {
                    $value = (int)$value === 1 ? '예' : '아니오';
                }
                $cells[] = $value;
            }
            $cells[] = Html::link($this->url('/admin/defs/' . $key . '/' . $row['code']),
                $this->can($screen['permission']) ? '고치기' : '보기');
            $rows[] = $cells;
        }
        $content = self::done($request) . $this->subLinks($key) . Html::table($headers, $rows);
        if ($key === 'modes') {
            $content .= $this->configForms([]);
        }
        return $this->page($screen['title'], $content);
    }

    private function configForms(array $problems)
    {
        $titles = ['economy' => '공통 보상 한도', 'combat' => '전투 보상 한도'];
        $out = Html::problems($problems);
        foreach (Catalog::configs() as $key => $specs) {
            $values = $this->defs->configValues($key);
            $inner = '';
            foreach ($specs as $name => $spec) {
                $inner .= Html::input($name, $spec[0], $values[$name], $spec[1] . ' ~ ' . $spec[2]);
            }
            $out .= '<h2>' . Html::e($titles[$key]) . '</h2>';
            $out .= $this->can(Auth::BALANCE)
                ? Html::form($this->url('/admin/config/' . $key), $this->csrf, $inner . Html::reason())
                : '<div class="readonly">' . $inner . '</div>';
        }
        return $out;
    }

    private function configSave($key, array $form, $ip)
    {
        if (!$this->can(Auth::BALANCE)) {
            return $this->denied();
        }
        $result = $this->defs->saveConfig($this->admin, $key, $form, $ip);
        if (!$result['ok']) {
            $screen = Catalog::find('modes');
            return $this->page($screen['title'], $this->configForms($result['problems']), 422);
        }
        return ['redirect' => '/admin/defs/modes?done=' . ($result['changed'] > 0 ? 'saved' : 'none')];
    }

    private function defEdit($key, array $screen, $code, $values, array $problems)
    {
        $row = $this->defs->row($screen, $code);
        if ($row === null) {
            return null;
        }
        $inner = '';
        foreach ($screen['readonly'] as $column => $label) {
            $inner .= '<p class="field">' . Html::e($label) . ': <strong>' . Html::e($row[$column]) . '</strong></p>';
        }
        foreach ($screen['fields'] as $column => $field) {
            $value = $values !== null && array_key_exists($column, $values) ? $values[$column] : $row[$column];
            $help = $field['help'];
            if ($field['type'] === 'int' || $field['type'] === 'decimal') {
                $help = trim($field['min'] . ' ~ ' . $field['max'] . '. ' . $help, ' .');
            }
            switch ($field['type']) {
                case 'bool': $inner .= Html::checkbox($column, $field['label'], (int)$value === 1, $help); break;
                case 'select': $inner .= Html::select($column, $field['label'], $field['options'], $value); break;
                case 'json':
                    $pretty = json_decode((string)$value, true);
                    $inner .= Html::textarea($column, $field['label'],
                        is_array($pretty) ? json_encode($pretty, JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT) : (string)$value, $help);
                    break;
                case 'decimal': $inner .= Html::input($column, $field['label'], rtrim(rtrim((string)$value, '0'), '.'), $help); break;
                default: $inner .= Html::input($column, $field['label'], $value, $help);
            }
        }
        $content = Html::problems($problems) . '<p class="sub">' . Html::e(Html::link($this->url('/admin/defs/' . $key), '목록으로')) . '</p>';
        $content .= $this->can($screen['permission'])
            ? Html::form($this->url('/admin/defs/' . $key . '/' . $row['code']), $this->csrf, $inner . Html::reason())
            : '<div class="readonly">' . $inner . '</div><p>이 계정의 역할로는 고칠 수 없습니다.</p>';
        return $this->page($screen['title'] . ' — ' . $row['name'], $content, $problems ? 422 : 200);
    }

    private function defSave($key, array $screen, $code, array $form, $ip)
    {
        if (!$this->can($screen['permission'])) {
            return $this->denied();
        }
        $result = $this->defs->save($this->admin, $screen, $code, $form, $ip);
        if (!$result['ok']) {
            return $this->defEdit($key, $screen, $code, $result['values'], $result['problems']);
        }
        return ['redirect' => '/admin/defs/' . $key . '?done=' . ($result['changed'] > 0 ? 'saved' : 'none')];
    }

    // ── 8: 도시 위험도 ──────────────────────────────────────

    private function sidos()
    {
        $names = [];
        foreach ($this->app->db->all('SELECT code, name FROM sidos ORDER BY id') as $row) {
            $names[$row['code']] = $row['name'];
        }
        return $names;
    }

    private function riskList(Request $request)
    {
        $sidos = $this->sidos();
        $sido = isset($request->query['sido']) && is_string($request->query['sido']) && isset($sidos[$request->query['sido']])
            ? $request->query['sido'] : '';
        $stage = isset($request->query['stage']) && is_string($request->query['stage']) ? $request->query['stage'] : '';
        $regionSido = [];
        foreach ($this->app->db->all('SELECT r.code, s.code AS sido FROM regions r JOIN sidos s ON s.id = r.sido_id') as $row) {
            $regionSido[$row['code']] = $row['sido'];
        }
        $cities = $this->app->city()->adminOverview();
        usort($cities, function ($a, $b) {
            return $a['risk'] !== $b['risk'] ? $b['risk'] - $a['risk'] : strcmp($a['regionCode'], $b['regionCode']);
        });
        $rows = [];
        foreach ($cities as $city) {
            $citySido = isset($regionSido[$city['regionCode']]) ? $regionSido[$city['regionCode']] : '';
            if (($sido !== '' && $citySido !== $sido) || ($stage !== '' && $city['stage'] !== $stage)) {
                continue;
            }
            $rows[] = [
                isset($sidos[$citySido]) ? $sidos[$citySido] : '',
                Html::link($this->url('/admin/risk/' . $city['regionCode']), $city['name']),
                $city['risk'], self::stage($city['stage']), $city['defensePower'],
                $city['invasionKind'] !== null ? Invasion::kindName($city['invasionKind']) . ' (규모 ' . $city['invasionSeverity'] . ')' : '',
                $city['recentInvasions'], $city['activePlayers'], $city['contribution'],
                $city['npcHeldCount'], $city['failedCount'], $city['rank'],
            ];
        }
        $filter = '<form method="get" action="' . Html::e($this->url('/admin/risk')) . '" class="filter">'
            . Html::select('sido', '시·도', ['' => '전체'] + $sidos, $sido)
            . Html::select('stage', '단계', ['' => '전체', Risk::SAFE => '안전', Risk::CAUTION => '주의', Risk::DANGER => '위험',
                Risk::EMERGENCY => '긴급'], $stage)
            . '<p><button type="submit">보기</button></p></form>';
        return $this->page('도시 위험도', self::done($request)
            . '<p class="sub">' . Html::e(Html::link($this->url('/admin/defs/facilities'), '도시 주요 시설')) . '</p>'
            . '<p>위험도는 서버가 계산합니다. 여기에서 바꾸려면 도시를 골라 사유와 함께 조정합니다 (이력에 남습니다).</p>'
            . $filter . Html::table(['시·도', '도시', '위험도', '단계', '방어력', '진행 중인 침공', '최근 7일 침공', '활성 가디언',
                '주민 기여도', 'NPC 방어', '방어 실패', '순위'], $rows));
    }

    private function riskDetail($code, array $problems, Request $request)
    {
        $city = null;
        foreach ($this->app->city()->adminOverview() as $row) {
            if ($row['regionCode'] === $code) {
                $city = $row;
            }
        }
        if ($city === null) {
            return null;
        }
        $history = $this->app->city()->history($code, 100);
        $adminNames = [];
        foreach ($this->app->db->all('SELECT id, login FROM admins') as $row) {
            $adminNames[(int)$row['id']] = $row['login'];
        }
        $adminOf = [];
        foreach ($this->app->db->all(
            'SELECT h.id, h.admin_id FROM city_risk_history h JOIN regions r ON r.id = h.region_id WHERE r.code = ? AND h.admin_id IS NOT NULL'
            . ' ORDER BY h.id DESC LIMIT 100', [$code]) as $row) {
            $adminOf[(int)$row['id']] = isset($adminNames[(int)$row['admin_id']]) ? $adminNames[(int)$row['admin_id']] : '#' . $row['admin_id'];
        }
        $rows = [];
        foreach ($history['entries'] as $entry) {
            $rows[] = [
                self::kstUnix(strtotime($entry['at'])), Risk::reasonName($entry['reason']),
                Risk::actorName($entry['actor']) . (isset($adminOf[$entry['seq']]) ? ' (' . $adminOf[$entry['seq']] . ')' : ''),
                ($entry['deltaCenti'] > 0 ? '+' : '') . number_format($entry['deltaCenti'] / 100, 2),
                number_format($entry['afterCenti'] / 100, 2), self::stage($entry['stage']), $entry['reference'],
            ];
        }
        $facts = [
            ['위험도', $city['risk']], ['단계', self::stage($city['stage'])], ['방어력', $city['defensePower']],
            ['주민', $city['residents']], ['활성 가디언', $city['activePlayers']],
            ['가디언의 승리 / 시도', $city['playerWins'] . ' / ' . $city['playerAttempts']],
            ['NPC 수비대가 막은 침공', $city['npcHeldCount']], ['막지 못한 침공', $city['failedCount']],
            ['주민 기여도', $city['contribution']], ['받은 지원 / 보낸 지원', $city['supportReceived'] . ' / ' . $city['supportGiven']],
            ['순위', $city['rank']],
        ];
        $content = self::done($request) . Html::problems($problems)
            . '<p class="sub">' . Html::e(Html::link($this->url('/admin/risk'), '목록으로')) . '</p>'
            . Html::table(['항목', '값'], $facts);
        if ($this->can(Auth::OPERATE)) {
            $content .= '<h2>수동 조정</h2><p>이벤트 보정이나 오류 수습에만 씁니다. 사유와 관리자 이름이 위험도 이력에 남습니다.</p>'
                . Html::form($this->url('/admin/risk/' . $code . '/adjust'), $this->csrf,
                    Html::input('delta', '바꿀 양 (-100 ~ 100)', '', '음수는 내림, 양수는 올림') . Html::reason(), '조정');
        }
        $content .= '<h2>위험도 변경 이력 (최근 100건, 한국 시간)</h2>'
            . Html::table(['시각', '사유', '주체', '변화', '바뀐 뒤', '단계', '참고'], $rows);
        return $this->page('도시 위험도 — ' . $city['name'], $content, $problems ? 422 : 200);
    }

    private function riskAdjust($code, array $form, Request $request)
    {
        if (!$this->can(Auth::OPERATE)) {
            return $this->denied();
        }
        $delta = isset($form['delta']) && is_string($form['delta']) && preg_match('/^-?\d{1,3}$/', trim($form['delta']))
            ? (int)trim($form['delta']) : 0;
        $reason = isset($form['reason']) && is_string($form['reason']) ? trim($form['reason']) : '';
        try {
            $this->app->db->tx(function () use ($code, $delta, $reason, $request) {
                $this->app->city()->adminAdjustRisk($this->admin['id'], $code, $delta, $reason);
                $this->auth->audit($this->admin['id'], 'risk.adjust', 'region', $code, $delta . ' — ' . $reason, $request->ip);
            });
        } catch (\CG\Infra\ApiError $error) {
            return $this->riskDetail($code, [$error->getMessage()], $request);
        }
        return ['redirect' => '/admin/risk/' . $code . '?done=adjusted'];
    }

    // ── 9: 위험도 계산 정책 ─────────────────────────────────

    private function policyInputs(array $policy, array $keys)
    {
        $ranges = Risk::policyRanges();
        $inner = '';
        foreach ($keys as $key => $label) {
            $inner .= Html::input($key, $label, $policy[$key], $ranges[$key][0] . ' ~ ' . $ranges[$key][1]);
        }
        return $inner;
    }

    private function policyPage(array $problems, $entered, Request $request)
    {
        $policy = $entered !== null ? $entered : $this->app->defs()->policy();
        $inner = '<h2>위험도를 바꾸는 사건의 크기 (1/100 단위: 100 = 위험도 1)</h2>'
            . '<p>올리는 사건은 0 이상, 내리는 활동은 0 이하만 넣을 수 있습니다. 플레이어의 활동이 위험도를 올리게 만들 수 없습니다.</p>';
        foreach (Risk::weightReasons() as $reason => $direction) {
            $inner .= Html::input('w_' . $reason, Risk::reasonName($reason) . ($direction > 0 ? ' (올림)' : ' (내림)'),
                $policy['weights'][$reason], $direction > 0 ? '0 ~ 10000' : '-10000 ~ 0');
        }
        foreach (PolicyEditor::groups() as $title => $keys) {
            $inner .= '<h2>' . Html::e($title) . '</h2>' . $this->policyInputs($policy, $keys);
        }
        $content = self::done($request) . Html::problems($problems)
            . '<p>저장할 때마다 새 정책으로 기록되고, 이전 정책은 그대로 남습니다. 지금 정책: #' . Html::e($this->app->defs()->policy()['version']) . '</p>';
        $content .= $this->can(Auth::BALANCE)
            ? Html::form($this->url('/admin/policy'), $this->csrf, $inner . Html::reason())
            : '<div class="readonly">' . $inner . '</div>';

        $rows = [];
        foreach ($this->policy->versions() as $version) {
            $revert = '';
            if ($this->can(Auth::BALANCE) && (int)$version['id'] !== $this->app->defs()->policy()['version']) {
                $revert = Html::raw('<form method="post" class="inline" action="' . Html::e($this->url('/admin/policy/revert')) . '">'
                    . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '">'
                    . '<input type="hidden" name="id" value="' . Html::e($version['id']) . '">'
                    . '<input type="text" name="reason" placeholder="사유" aria-label="되돌리는 사유"> <button type="submit">이 정책으로 되돌리기</button></form>');
            }
            $rows[] = ['#' . $version['id'], self::kst($version['created_at']), $version['login'] !== null ? $version['login'] : '(초기값)',
                $version['reason'], $revert];
        }
        $content .= '<h2>지난 정책 (한국 시간)</h2>' . Html::table(['번호', '저장한 때', '관리자', '사유', ''], $rows);
        return $this->page('위험도 계산 정책', $content, $problems ? 422 : 200);
    }

    /** 정책의 일부(또는 전부)를 저장한다. 침공·지원·랭킹 화면의 작은 폼도 이 길로 온다. */
    private function policySave(array $form, $ip, $back, Request $request)
    {
        if (!$this->can(Auth::BALANCE)) {
            return $this->denied();
        }
        list($policy, $ranking, $problems) = $this->policy->fromForm($form);
        $result = ['ok' => false, 'problems' => $problems, 'changed' => 0];
        if (!$problems) {
            $result = $this->policy->save($this->admin, $policy, $ranking, isset($form['reason']) ? $form['reason'] : '', $ip);
        }
        if (!$result['ok']) {
            switch ($back) {
                case '/admin/invasions': return $this->invasionsPage($result['problems'], $request);
                case '/admin/support': return $this->supportPage($result['problems'], $request);
                case '/admin/ranking': return $this->rankingPage($result['problems'], $request);
                default: return $this->policyPage($result['problems'], $policy, $request);
            }
        }
        return ['redirect' => $back . '?done=' . ($result['changed'] > 0 ? 'saved' : 'none')];
    }

    private function policyRevert(array $form, $ip, Request $request)
    {
        if (!$this->can(Auth::BALANCE)) {
            return $this->denied();
        }
        $id = isset($form['id']) && is_string($form['id']) && ctype_digit($form['id']) ? (int)$form['id'] : 0;
        $result = $this->policy->revert($this->admin, $id, isset($form['reason']) ? $form['reason'] : '', $ip);
        if (!$result['ok']) {
            return $this->policyPage($result['problems'], null, $request);
        }
        return ['redirect' => '/admin/policy?done=' . ($result['changed'] > 0 ? 'reverted' : 'none')];
    }

    // ── 10: 침공 이벤트 ─────────────────────────────────────

    private function regionNames()
    {
        $names = [];
        foreach ($this->app->db->all('SELECT r.id, r.code, r.name, s.short_name FROM regions r JOIN sidos s ON s.id = r.sido_id') as $row) {
            $names[(int)$row['id']] = ['code' => $row['code'], 'name' => trim($row['short_name'] . ' ' . $row['name'])];
        }
        return $names;
    }

    private function invasionsPage(array $problems, Request $request)
    {
        $db = $this->app->db;
        $this->app->city()->adminOverview();   // 밀린 판정이 있으면 먼저 따라잡는다
        $names = $this->regionNames();
        $name = function ($regionId) use ($names) {
            return isset($names[(int)$regionId]) ? $names[(int)$regionId]['name'] : '#' . $regionId;
        };
        $active = [];
        foreach ($db->all("SELECT * FROM city_invasion_events WHERE status = 'active' ORDER BY ends_at") as $row) {
            $end = '';
            if ($this->can(Auth::OPERATE)) {
                $end = Html::raw('<form method="post" class="inline" action="' . Html::e($this->url('/admin/invasions/' . $row['id'] . '/end')) . '">'
                    . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '"><button type="submit">끝내기</button></form>');
            }
            $active[] = [$name($row['region_id']), Invasion::kindName($row['kind']), $row['severity'],
                $row['progress'] . ' / ' . $row['required'], self::kst($row['started_at']), self::kst($row['ends_at']),
                $row['source'] === 'admin' ? '관리자' : '시스템', $end];
        }
        $recent = [];
        foreach ($db->all("SELECT * FROM city_invasion_events WHERE status <> 'active' ORDER BY resolved_at DESC, id DESC LIMIT 50") as $row) {
            $recent[] = [$name($row['region_id']), Invasion::kindName($row['kind']), $row['severity'], Invasion::statusName($row['status']),
                $row['progress'] . ' / ' . $row['required'], self::kst($row['started_at']), self::kst($row['resolved_at']),
                $row['source'] === 'admin' ? '관리자' : '시스템'];
        }
        $content = self::done($request) . Html::problems($problems)
            . '<h2>진행 중인 침공 (한국 시간)</h2>'
            . Html::table(['도시', '종류', '규모', '가디언의 승리 / 필요', '시작', '끝', '시작한 곳', ''], $active, '진행 중인 침공이 없습니다.');
        if ($this->can(Auth::OPERATE)) {
            $content .= '<h2>침공 시작 (이벤트용)</h2><p>끝내면 NPC 수비대가 막은 것으로 처리됩니다 (가디언의 기여로 세지 않습니다).</p>'
                . Html::form($this->url('/admin/invasions/start'), $this->csrf,
                    Html::input('region', '지역 코드 (5자리)', '', '도시 위험도 화면의 주소에 보이는 숫자')
                    . Html::select('kind', '종류', [Invasion::OUTSKIRTS => Invasion::kindName(Invasion::OUTSKIRTS),
                        Invasion::INTRUSION => Invasion::kindName(Invasion::INTRUSION), Invasion::ZERO => Invasion::kindName(Invasion::ZERO)],
                        Invasion::OUTSKIRTS)
                    . Html::select('severity', '규모', ['1' => '1', '2' => '2', '3' => '3'], '1'), '시작');
        }
        $groups = PolicyEditor::groups();
        $inner = $this->policyInputs($this->app->defs()->policy(), $groups['침공']);
        $content .= '<h2>침공 빈도</h2>';
        $content .= $this->can(Auth::BALANCE)
            ? Html::form($this->url('/admin/invasions/policy'), $this->csrf, $inner . Html::reason())
            : '<div class="readonly">' . $inner . '</div>';
        $content .= '<h2>최근에 끝난 침공 50건</h2>'
            . Html::table(['도시', '종류', '규모', '결과', '가디언의 승리 / 필요', '시작', '끝', '시작한 곳'], $recent);
        return $this->page('침공 이벤트', $content, $problems ? 422 : 200);
    }

    private function invasionStart(array $form, Request $request)
    {
        if (!$this->can(Auth::OPERATE)) {
            return $this->denied();
        }
        $region = isset($form['region']) && is_string($form['region']) ? trim($form['region']) : '';
        $kind = isset($form['kind']) && is_string($form['kind']) ? $form['kind'] : '';
        $severity = isset($form['severity']) && is_string($form['severity']) && ctype_digit($form['severity']) ? (int)$form['severity'] : 0;
        try {
            $this->app->db->tx(function () use ($region, $kind, $severity, $request) {
                $invasion = $this->app->city()->adminStartInvasion($this->admin['id'], $region, $kind, $severity);
                $this->auth->audit($this->admin['id'], 'invasion.start', 'region', $region,
                    $kind . ' ' . $severity . ($invasion !== null ? ' ' . $invasion['id'] : ''), $request->ip);
            });
        } catch (\CG\Infra\ApiError $error) {
            return $this->invasionsPage([$error->getMessage()], $request);
        }
        return ['redirect' => '/admin/invasions?done=started'];
    }

    private function invasionEnd($id, Request $request)
    {
        if (!$this->can(Auth::OPERATE)) {
            return $this->denied();
        }
        try {
            $this->app->db->tx(function () use ($id, $request) {
                $this->app->city()->adminEndInvasion($this->admin['id'], $id);
                $this->auth->audit($this->admin['id'], 'invasion.end', 'invasion', 'inv_' . $id, null, $request->ip);
            });
        } catch (\CG\Infra\ApiError $error) {
            return $this->invasionsPage([$error->getMessage()], $request);
        }
        return ['redirect' => '/admin/invasions?done=ended'];
    }

    // ── 11: 도시 간 지원 ────────────────────────────────────

    private function supportPage(array $problems, Request $request)
    {
        $db = $this->app->db;
        $names = $this->regionNames();
        $name = function ($regionId) use ($names) {
            return isset($names[(int)$regionId]) ? $names[(int)$regionId]['name'] : '#' . $regionId;
        };
        $status = ['open' => '열림', 'resolved' => '해결', 'expired' => '만료'];
        $requests = [];
        foreach ($db->all(
            'SELECT q.*, (SELECT COUNT(*) FROM city_support_contributions c WHERE c.request_id = q.id) AS helpers'
            . " FROM city_support_requests q ORDER BY (q.status = 'open') DESC, q.id DESC LIMIT 100") as $row) {
            $requests[] = [$name($row['region_id']), isset($status[$row['status']]) ? $status[$row['status']] : $row['status'],
                self::kst($row['opened_at']), self::kst($row['status'] === 'open' ? $row['closes_at'] : $row['closed_at']),
                $row['helpers'], $row['wins'], $row['points']];
        }
        $givers = [];
        foreach ($db->all(
            'SELECT c.home_region_id, COUNT(DISTINCT c.player_id) AS players, SUM(c.wins) AS wins, SUM(c.points) AS points'
            . ' FROM city_support_contributions c GROUP BY c.home_region_id ORDER BY points DESC LIMIT 30') as $row) {
            $givers[] = [$name($row['home_region_id']), (int)$row['players'], (int)$row['wins'], (int)$row['points']];
        }
        $groups = PolicyEditor::groups();
        $inner = $this->policyInputs($this->app->defs()->policy(), $groups['도시 간 지원']);
        $content = self::done($request) . Html::problems($problems)
            . '<p>긴급 단계가 된 도시는 같은 시·도의 가디언에게 지원을 요청합니다. 지원에 나서도 소속 도시는 바뀌지 않습니다.</p>'
            . '<h2>지원 요청 (최근 100건, 한국 시간)</h2>'
            . Html::table(['도시', '상태', '열린 때', '닫히는/닫힌 때', '지원한 가디언', '지원 승리', '지원 기여도'], $requests)
            . '<h2>다른 도시를 많이 도운 도시</h2>' . Html::table(['도시', '가디언', '승리', '지원 기여도'], $givers)
            . '<h2>지원 요청 정책</h2>';
        $content .= $this->can(Auth::BALANCE)
            ? Html::form($this->url('/admin/support'), $this->csrf, $inner . Html::reason())
            : '<div class="readonly">' . $inner . '</div>';
        return $this->page('도시 간 지원', $content, $problems ? 422 : 200);
    }

    // ── 12: 지역 랭킹 ───────────────────────────────────────

    private function rankingPage(array $problems, Request $request)
    {
        $names = [];
        foreach ($this->regionNames() as $region) {
            $names[$region['code']] = $region['name'];
        }
        $rows = [];
        foreach (array_slice($this->app->city()->adminRanking(), 0, 100) as $score) {
            $rows[] = [$score['rank'], isset($names[$score['regionCode']]) ? $names[$score['regionCode']] : $score['regionCode'],
                $score['playerDefended'] ? '가디언' : 'NPC 수비대만', number_format($score['total'] / 100, 2),
                $score['safety'], $score['successRate'], $score['contribution'], $score['response'], $score['cooperation']];
        }
        $weights = $this->app->defs()->rankingWeights();
        $inner = '';
        foreach (PolicyEditor::rankingLabels() as $key => $label) {
            $inner .= Html::input('r_' . $key, $label, $weights[$key], in_array($key, ['contributionTarget', 'cooperationTarget'], true)
                ? '1 이상' : '0 ~ 100');
        }
        $content = self::done($request) . Html::problems($problems)
            . '<p>점수는 인구가 아니라 활성 가디언 한 사람당 값으로 계산하고, 침공의 규모로 난이도를 보정합니다. '
            . '가디언이 있는 도시끼리 먼저 겨루고, NPC 수비대만 있는 도시는 그 뒤에 옵니다. 현금 결제와 관계된 항목은 없습니다.</p>'
            . '<h2>가중치</h2>';
        $content .= $this->can(Auth::BALANCE)
            ? Html::form($this->url('/admin/ranking'), $this->csrf, $inner . Html::reason())
            : '<div class="readonly">' . $inner . '</div>';
        $content .= '<h2>순위 (위에서 100곳)</h2>'
            . Html::table(['순위', '도시', '지키는 이', '점수', '안전도', '방어 성공률', '기여도', '침공 대응', '협동'], $rows);
        return $this->page('지역 랭킹', $content, $problems ? 422 : 200);
    }

    // ── 13: 전투 통계 ───────────────────────────────────────

    private function statsPage()
    {
        $db = $this->app->db;
        $content = '';
        foreach ([1 => '최근 24시간', 7 => '최근 7일'] as $days => $title) {
            $since = Clock::sql(Clock::now() - $days * 86400);
            $defense = $db->one("SELECT COUNT(*) AS n, COALESCE(SUM(result = 'win' AND accepted = 1), 0) AS wins,"
                . ' COALESCE(SUM(accepted = 0), 0) AS rejected, COALESCE(SUM(reward_gc), 0) AS gc, COUNT(DISTINCT player_id) AS players'
                . ' FROM defense_battles WHERE started_at >= ?', [$since]);
            $fps = $db->one("SELECT COUNT(*) AS n, COALESCE(SUM(result = 'win' AND accepted = 1), 0) AS wins,"
                . ' COALESCE(SUM(accepted = 0), 0) AS rejected, COALESCE(SUM(reward_gc), 0) AS gc, COUNT(DISTINCT player_id) AS players'
                . ' FROM fps_mission_results WHERE started_at >= ?', [$since]);
            $hunt = $db->one('SELECT COUNT(*) AS n, COALESCE(SUM(kills_total), 0) AS kills, COALESCE(SUM(rejected_total), 0) AS rejected,'
                . ' COALESCE(SUM(reward_gc_total), 0) AS gc, COUNT(DISTINCT player_id) AS players FROM hunt_sessions WHERE last_report_at >= ?',
                [$since]);
            $rate = function ($wins, $total) {
                return $total > 0 ? number_format($wins * 100 / $total, 1) . '%' : '-';
            };
            $content .= '<h2>' . Html::e($title) . '</h2>' . Html::table(
                ['전투 모드', '판 수', '참여한 가디언', '승리 (처치)', '승률', '거절된 보고', '지급한 GC'],
                [
                    ['디펜스', (int)$defense['n'], (int)$defense['players'], (int)$defense['wins'], $rate((int)$defense['wins'], (int)$defense['n']),
                        (int)$defense['rejected'], number_format((int)$defense['gc'])],
                    ['헌트', (int)$hunt['n'], (int)$hunt['players'], '처치 ' . number_format((int)$hunt['kills']), '-',
                        '처치 ' . number_format((int)$hunt['rejected']), number_format((int)$hunt['gc'])],
                    ['FPS', (int)$fps['n'], (int)$fps['players'], (int)$fps['wins'], $rate((int)$fps['wins'], (int)$fps['n']),
                        (int)$fps['rejected'], number_format((int)$fps['gc'])],
                ]
            );
        }
        $week = Clock::sql(Clock::now() - 7 * 86400);
        $reasons = [];
        foreach ($db->all("SELECT '디펜스' AS mode, reject_reason, COUNT(*) AS n FROM defense_battles WHERE accepted = 0 AND ended_at >= ?"
            . " GROUP BY reject_reason UNION ALL SELECT 'FPS', reject_reason, COUNT(*) FROM fps_mission_results WHERE accepted = 0 AND ended_at >= ?"
            . ' GROUP BY reject_reason', [$week, $week]) as $row) {
            $reasons[] = [$row['mode'], $row['reject_reason'], (int)$row['n']];
        }
        $ledgerNames = [
            'hunt_reward' => '사냥 보상', 'battle_reward' => '방어전 보상', 'fps_reward' => 'FPS 임무 보상', 'tower_unlock' => '타워 해금',
            'gear_purchase' => '장비 구매', 'gear_upgrade' => '장비 강화', 'city_repair' => '시설 복구', 'city_fund' => '도시 기금',
            'residency_bonus' => '정착 지원금', 'admin_adjust' => '관리자 조정',
        ];
        $ledger = [];
        foreach ($db->all('SELECT reason, COUNT(*) AS n, COALESCE(SUM(CASE WHEN delta > 0 THEN delta ELSE 0 END), 0) AS issued,'
            . ' COALESCE(SUM(CASE WHEN delta < 0 THEN -delta ELSE 0 END), 0) AS spent FROM ledger_entries WHERE created_at >= ?'
            . ' GROUP BY reason ORDER BY reason', [$week]) as $row) {
            $ledger[] = [isset($ledgerNames[$row['reason']]) ? $ledgerNames[$row['reason']] : $row['reason'], (int)$row['n'],
                number_format((int)$row['issued']), number_format((int)$row['spent'])];
        }
        $invasions = [];
        foreach ($db->all('SELECT kind, status, COUNT(*) AS n FROM city_invasion_events WHERE started_at >= ? GROUP BY kind, status'
            . ' ORDER BY kind, status', [$week]) as $row) {
            $invasions[] = [Invasion::kindName($row['kind']), Invasion::statusName($row['status']), (int)$row['n']];
        }
        $gear = [];
        foreach ($db->all('SELECT e.name, COUNT(*) AS owners, AVG(p.level) AS level FROM player_combat_equipment p'
            . ' JOIN combat_equipment e ON e.id = p.equipment_id GROUP BY e.id, e.name ORDER BY owners DESC') as $row) {
            $gear[] = [$row['name'], (int)$row['owners'], number_format((float)$row['level'], 1)];
        }
        $content .= '<h2>거절된 보고의 이유 (최근 7일)</h2>' . Html::table(['전투 모드', '이유', '건수'], $reasons, '거절된 보고가 없습니다.')
            . '<h2>GC 흐름 (최근 7일)</h2><p>전투 보상은 서버가 정의에서 계산한 금액만 지급됩니다.</p>'
            . Html::table(['사유', '건수', '지급', '회수'], $ledger)
            . '<h2>침공 (최근 7일)</h2>' . Html::table(['종류', '결과', '건수'], $invasions)
            . '<h2>구매한 장비</h2>' . Html::table(['장비', '가진 가디언', '평균 강화 단계'], $gear, '구매한 장비가 없습니다.');
        return $this->page('전투 통계', $content);
    }

    // ── 변경 이력, 감사 기록 ────────────────────────────────

    private static function pageNumber(Request $request)
    {
        return isset($request->query['before']) && is_string($request->query['before']) && ctype_digit($request->query['before'])
            ? (int)$request->query['before'] : 0;
    }

    private function historyPage(Request $request, array $problems)
    {
        $before = self::pageNumber($request);
        $rows = $this->app->db->all(
            'SELECT h.*, a.login FROM config_history h LEFT JOIN admins a ON a.id = h.admin_id'
            . ($before > 0 ? ' WHERE h.id < ?' : '') . ' ORDER BY h.id DESC LIMIT 50', $before > 0 ? [$before] : []);
        $tables = [];
        foreach (Catalog::all() as $screen) {
            $tables[$screen['table']] = $screen;
        }
        $out = [];
        $last = 0;
        foreach ($rows as $row) {
            $last = (int)$row['id'];
            $revert = '';
            // v8.0 의 정의는 DefEditor 가, v9.0 의 정책·기준·설정은 CityOpsHistory 가 되돌린다.
            $revertible = isset($tables[$row['target_type']])
                ? $this->can($tables[$row['target_type']]['permission'])
                : (CityOpsHistory::handles($row['target_type'], $row['target_key']) && $this->can(Auth::CITY_POLICY));
            if ($revertible) {
                $revert = Html::raw('<form method="post" class="inline" action="' . Html::e($this->url('/admin/history/' . $row['id'] . '/revert')) . '">'
                    . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '">'
                    . '<input type="text" name="reason" placeholder="사유" aria-label="되돌리는 사유"> <button type="submit">이전 값으로</button></form>');
            }
            $out[] = ['#' . $row['id'], self::kst($row['created_at']), $row['login'], $row['target_type'], $row['target_key'],
                Html::raw('<code>' . Html::e($row['before_value']) . '</code>'), Html::raw('<code>' . Html::e($row['after_value']) . '</code>'),
                $row['reason'], $revert];
        }
        $content = self::done($request) . Html::problems($problems)
            . Html::table(['번호', '때 (한국 시간)', '관리자', '대상', '항목', '바꾸기 전', '바꾼 뒤', '사유', ''], $out);
        if (count($rows) >= 50) {
            $content .= '<p>' . Html::e(Html::link($this->url('/admin/history?before=' . $last), '더 이전')) . '</p>';
        }
        return $this->page('변경 이력', $content, $problems ? 422 : 200);
    }

    private function historyRevert($id, array $form, $ip, Request $request)
    {
        $reason = isset($form['reason']) ? $form['reason'] : '';
        $history = $this->app->db->one('SELECT target_type, target_key FROM config_history WHERE id = ?', [(int)$id]);
        $result = $history !== null && CityOpsHistory::handles($history['target_type'], $history['target_key'])
            ? (new CityOpsHistory($this->app))->revert($this->admin, $id, $reason, $ip)
            : $this->defs->revert($this->admin, $id, $reason, $ip);
        if (!$result['ok']) {
            return $this->historyPage($request, $result['problems']);
        }
        return ['redirect' => '/admin/history?done=' . ($result['changed'] > 0 ? 'reverted' : 'none')];
    }

    private function auditPage(Request $request)
    {
        if (!$this->can(Auth::AUDIT)) {
            return $this->denied();
        }
        $before = self::pageNumber($request);
        $rows = $this->app->db->all(
            'SELECT l.*, a.login FROM admin_audit_logs l LEFT JOIN admins a ON a.id = l.admin_id'
            . ($before > 0 ? ' WHERE l.id < ?' : '') . ' ORDER BY l.id DESC LIMIT 100', $before > 0 ? [$before] : []);
        $out = [];
        $last = 0;
        foreach ($rows as $row) {
            $last = (int)$row['id'];
            $out[] = [self::kst($row['created_at']), $row['login'], $row['action'], $row['target_type'], $row['target_key'],
                $row['detail'], $row['ip']];
        }
        $content = Html::table(['때 (한국 시간)', '관리자', '한 일', '대상', '항목', '내용', '주소'], $out);
        if (count($rows) >= 100) {
            $content .= '<p>' . Html::e(Html::link($this->url('/admin/audit?before=' . $last), '더 이전')) . '</p>';
        }
        return $this->page('감사 기록', $content);
    }
}
