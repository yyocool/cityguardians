<?php
namespace CG\Admin;

use CG\App;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Service\Settings;

/**
 * 관리자 화면: v10.0 실시간 대시보드 설정 (docs/ADMIN_SYSTEM.md 13장, docs/LIVE_DASHBOARD.md 7장).
 *
 *   실시간 대시보드 설정       /admin/dashboard
 *
 * 게임 화면 오른쪽의 카드 세 장에 대해 정하는 것: 카드별 기본 표시 여부, 기본 펼침 상태, 데이터 갱신 간격,
 * 도시 위험 경고 기준, 전국 긴급 알림 표시 정책, 준비되지 않은 금융 항목을 보여 주는 방법.
 *
 * - 이 화면에는 이용자의 금융 정보가 없다. 잔액, 거래 내역, 순자산 어느 것도 읽지 않는다.
 * - 보는 것은 CITY_VIEW, 바꾸는 것은 CITY_POLICY 권한이다. 서비스(Settings::save)가 요청마다 다시 확인한다.
 * - 값을 바꾸는 요청은 POST 이고 (CSRF 는 Kernel 이 확인한다), 사유를 적어야 하며, 변경 이력과 감사 기록에 남는다.
 *   이전 값으로 되돌리는 일은 변경 이력 화면(/admin/history)에서 같은 검사를 거쳐 한다.
 * - 화면에 나가는 값은 모두 Html::e() 를 거친다.
 */
final class DashboardPages
{
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
        return preg_match('#^/admin/dashboard(/|$)#', $path) === 1;
    }

    /** [주소, 이름, 필요한 권한] */
    public static function menu()
    {
        return [['/admin/dashboard', '실시간 대시보드 설정', Auth::CITY_VIEW]];
    }

    private function page($title, $content, $status = 200)
    {
        return ['html' => $this->pages->frame($title, $content), 'status' => $status];
    }

    /** @return array|null html(+status) 또는 redirect. 없는 주소면 null. */
    public function route($method, $path, Request $request)
    {
        if (!Auth::can($this->admin, Auth::CITY_VIEW)) {
            return $this->page('권한 없음', '<p>이 계정의 역할로는 할 수 없는 일입니다.</p>', 403);
        }
        if ($path === '/admin/dashboard') {
            return $method === 'GET' ? $this->settingsPage([], $request) : null;
        }
        if ($path === '/admin/dashboard/settings' && $method === 'POST') {
            $settings = $this->app->settings();
            $before = $settings->dashboard();
            $problems = $settings->save($this->admin, Settings::DASHBOARD, $request->form, $request->ip);
            if ($problems) {
                return $this->settingsPage($problems, $request, 422);
            }
            return ['redirect' => '/admin/dashboard?done=' . ($before === $settings->dashboard() ? 'none' : 'saved')];
        }
        return null;
    }

    private static function field(array $form, $key, $default)
    {
        return isset($form[$key]) && is_string($form[$key]) ? $form[$key] : (string)$default;
    }

    /** 0/1 이나 몇 가지 가운데 고르는 값의 선택지. 없으면 숫자를 적는 칸이다. */
    private static function choices()
    {
        $show = ['1' => '표시', '0' => '숨김'];
        $open = ['1' => '펼침', '0' => '접힘'];
        return [
            'financeVisible' => $show, 'cityVisible' => $show, 'nationalVisible' => $show,
            'financeExpanded' => $open, 'cityExpanded' => $open, 'nationalExpanded' => $open,
            'nationalNotice' => ['0' => '표시 안 함', '1' => '긴급 단계인 도시', '2' => '긴급 단계이거나 침공 중인 도시'],
            'sampleData' => ['0' => '"준비 중"으로 표시', '1' => '"개발용 샘플"로 표시'],
        ];
    }

    private static function groups()
    {
        return [
            '카드별 기본 표시와 펼침' => ['financeVisible', 'financeExpanded', 'cityVisible', 'cityExpanded', 'nationalVisible', 'nationalExpanded'],
            '데이터 갱신' => ['refreshSeconds'],
            '도시 위험 경고' => ['cityAlertRisk'],
            '전국 긴급 알림' => ['nationalNotice', 'nationalNoticeMax'],
            '준비되지 않은 금융 항목' => ['sampleData'],
        ];
    }

    private static function shown($name, $value)
    {
        $choices = self::choices();
        return isset($choices[$name][(string)$value]) ? $choices[$name][(string)$value] : number_format((int)$value);
    }

    private function settingsPage(array $problems, Request $request, $status = 200)
    {
        $specs = Settings::specs()[Settings::DASHBOARD];
        $values = $this->app->settings()->dashboard();
        $done = isset($request->query['done']) && is_string($request->query['done']) ? $request->query['done'] : '';
        $messages = ['saved' => '저장했습니다. 변경 이력과 감사 기록에 남았습니다. 클라이언트는 다음에 요약을 받을 때부터 새 값을 씁니다.',
            'none' => '바뀐 값이 없습니다.'];

        $content = Html::notice(isset($messages[$done]) ? $messages[$done] : '') . Html::problems($problems)
            . '<p>게임 화면 오른쪽의 카드 세 장(내 계좌 및 투자현황, 우리 도시 현황, 전국 지도 현황)의 기본값을 정합니다. '
            . '여기의 표시·펼침은 <strong>처음 보는 이용자의 기본값</strong>입니다. 이용자가 자기 화면에서 접거나 숨긴 것은 그 기기에 저장되고, 이 값으로 덮어쓰지 않습니다.</p>'
            . '<p>이 화면은 이용자의 금융 정보를 읽지 않습니다. 잔액, 거래 내역, 순자산은 본인의 게임 화면에만 내려갑니다.</p>';

        $choices = self::choices();
        if (Auth::can($this->admin, Auth::CITY_POLICY)) {
            $inner = '';
            foreach (self::groups() as $title => $names) {
                $inner .= '<h3>' . Html::e($title) . '</h3>';
                foreach ($names as $name) {
                    $spec = $specs[$name];
                    $value = $problems ? self::field($request->form, $name, $values[$name]) : (string)$values[$name];
                    $inner .= isset($choices[$name])
                        ? Html::select($name, $spec[0], $choices[$name], $value)
                        : Html::input($name, $spec[0], $value, $spec[1] . ' ~ ' . number_format($spec[2]) . ' (기본 ' . number_format($spec[3]) . ')');
                }
            }
            $content .= '<h2>설정</h2>' . Html::form($this->base . '/admin/dashboard/settings', $this->csrf, $inner . Html::reason());
        } else {
            $rows = [];
            foreach (self::groups() as $names) {
                foreach ($names as $name) {
                    $rows[] = [$specs[$name][0], self::shown($name, $values[$name])];
                }
            }
            $content .= '<h2>설정</h2>' . Html::table(['항목', '값'], $rows)
                . '<p class="sub">값을 바꾸려면 정책을 바꿀 수 있는 역할이 필요합니다.</p>';
        }

        $content .= '<h2>카드가 보여 주는 값과 그 출처</h2>' . Html::table(['카드', '값', '출처'], [
            ['내 계좌 및 투자현황', '보유 GC, 자산 변동', '지갑과 원장 (본인의 것만)'],
            ['내 계좌 및 투자현황', '은행 예금, 가상 주식, 대출', '이 서버에 아직 없음. 위의 설정에 따라 "준비 중" 또는 "개발용 샘플"로 표시'],
            ['우리 도시 현황', '위험도, 방어력, 침공, 순위, 내 기여도', '도시 위험도 (서버가 계산)'],
            ['우리 도시 현황', '유효 전입자 수, 성장 단계, 모델링 상태', '도시 성장 관리'],
            ['우리 도시 현황', '도시 재정', '도시 재정 (게임 안의 GC. CITY PASS·장학 예산과 따로)'],
            ['전국 지도 현황', '도시별 위험도, 평균 안전도, 긴급 도시 수, 순위, 지원 요청', '도시 위험도, 지역 랭킹, 도시 간 지원'],
        ]);

        $rows = [];
        foreach ($this->app->db->all(
            'SELECT h.id, h.before_value, h.after_value, h.reason, h.created_at, a.login FROM config_history h'
            . ' LEFT JOIN admins a ON a.id = h.admin_id WHERE h.target_type = ? AND h.target_key = ? ORDER BY h.id DESC LIMIT 10',
            ['game_config', Settings::DASHBOARD]
        ) as $row) {
            $before = json_decode((string)$row['before_value'], true);
            $after = json_decode((string)$row['after_value'], true);
            $changes = [];
            foreach ($specs as $name => $spec) {
                if (is_array($before) && is_array($after) && isset($before[$name], $after[$name]) && $before[$name] !== $after[$name]) {
                    $changes[] = $spec[0] . ': ' . self::shown($name, $before[$name]) . ' → ' . self::shown($name, $after[$name]);
                }
            }
            $rows[] = ['#' . (int)$row['id'], gmdate('Y-m-d H:i', Clock::parse($row['created_at']) + 9 * 3600), (string)$row['login'],
                implode(' / ', $changes), (string)$row['reason']];
        }
        $content .= '<h2>최근 변경</h2>' . Html::table(['번호', '시각 (한국)', '관리자', '바뀐 값', '사유'], $rows, '아직 바꾼 적이 없습니다. 기본값을 쓰고 있습니다.')
            . '<p class="sub">이전 값으로 되돌리려면 ' . Html::e(Html::link($this->base . '/admin/history', '변경 이력'))
            . ' 화면에서 해당 번호를 고릅니다.</p>';

        return $this->page('실시간 대시보드 설정', $content, $status);
    }
}
