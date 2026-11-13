<?php
namespace CG\Admin;

use CG\App;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Service\Features;

/**
 * 관리자 화면: 현실 혜택 (docs/ADMIN_SYSTEM.md 11장).
 *
 *   현실 혜택 기능 승인   /admin/benefits
 *   CITY PASS 관리        /admin/pass/…        (PassScreens)
 *   장학사업 관리         /admin/scholarship/… (ScholarshipScreens)
 *
 * 권한은 업무별로 나뉜다. 화면은 할 수 없는 일의 폼을 보여 주지 않고, 서비스가 요청마다 권한을 다시 확인한다
 * (권한이 없으면 ApiError 가 나고 Kernel 이 403 으로 답한다). 화면에 나가는 값은 모두 Html::e() 를 거친다.
 */
final class BenefitPages
{
    use PassScreens;
    use ScholarshipScreens;

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
        return preg_match('#^/admin/(benefits|pass|scholarship)(/|$)#', $path) === 1;
    }

    /** [주소, 이름, 필요한 권한] */
    public static function menu()
    {
        return [
            ['/admin/benefits', '현실 혜택 기능 승인', Auth::FEATURE_VIEW],
            ['/admin/pass/merchants', 'CITY PASS: 제휴 업체·지점', Auth::PASS_VIEW],
            ['/admin/pass/offers', 'CITY PASS: 혜택', Auth::PASS_VIEW],
            ['/admin/pass/coupons', 'CITY PASS: 쿠폰', Auth::PASS_VIEW],
            ['/admin/pass/redemptions', 'CITY PASS: 사용 내역', Auth::PASS_VIEW],
            ['/admin/pass/stats', 'CITY PASS: 이용 통계', Auth::PASS_VIEW],
            ['/admin/pass/fraud', 'CITY PASS: 부정 사용 탐지', Auth::PASS_VIEW],
            ['/admin/pass/audit', 'CITY PASS: 상점 계정 기록', Auth::BENEFIT_AUDIT],
            ['/admin/scholarship/programs', '장학: 프로그램·신청자', Auth::SCH_VIEW],
            ['/admin/scholarship/reviews', '장학: 내 심사', Auth::SCH_REVIEW],
            ['/admin/scholarship/appeals', '장학: 이의신청', Auth::SCH_VIEW],
            ['/admin/scholarship/disbursements', '장학: 지급 상태', Auth::SCH_DISBURSE],
            ['/admin/scholarship/audit', '장학: 개인정보 접근 로그', Auth::BENEFIT_AUDIT],
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

    private static function pageNo(Request $request)
    {
        $page = self::field($request->query, 'page');
        return ctype_digit($page) && (int)$page >= 1 && (int)$page <= 100000 ? (int)$page : 1;
    }

    private function pager($path, array $query, $page, $count, $size = 100)
    {
        $out = '';
        if ($page > 1) {
            $out .= Html::e($this->link($path . '?' . http_build_query($query + ['page' => $page - 1]), '이전')) . ' ';
        }
        if ($count >= $size) {
            $out .= Html::e($this->link($path . '?' . http_build_query($query + ['page' => $page + 1]), '다음'));
        }
        return $out !== '' ? '<p>' . $out . '</p>' : '';
    }

    private static function done(Request $request)
    {
        $messages = [
            'saved' => '저장했습니다. 기록에 남았습니다.', 'changed' => '상태를 바꿨습니다. 기록에 남았습니다.',
            'recorded' => '법률 검토 상태를 기록했습니다.', 'enabled' => '기능을 켰습니다.', 'disabled' => '기능을 껐습니다.',
            'cancelled' => '쿠폰을 취소했습니다.', 'added' => '더했습니다.', 'removed' => '뺐습니다.',
            'submitted' => '평가를 냈습니다. 낸 평가는 고칠 수 없습니다.', 'recused' => '심사를 회피했습니다.',
            'proposed' => '선정을 제안했습니다. 다른 관리자의 최종 승인이 필요합니다.', 'approved' => '최종 승인했습니다. 결과를 알리기 전에는 신청자에게 보이지 않습니다.',
            'announced' => '결과를 알렸습니다. 신청자가 진행 상태 화면에서 볼 수 있습니다.', 'handled' => '이의신청을 처리했습니다.',
        ];
        $key = self::field($request->query, 'done');
        return Html::notice(isset($messages[$key]) ? $messages[$key] : '');
    }

    private static function id($value)
    {
        return (int)$value;
    }

    // ── 경로 ────────────────────────────────────────────────

    /** @return array|null html(+status) 또는 redirect. 없는 주소면 null. */
    public function route($method, $path, Request $request)
    {
        $get = $method === 'GET';
        $form = $request->form;
        if ($path === '/admin/benefits') {
            return $get ? $this->featuresPage([], $request) : null;
        }
        if (preg_match('#^/admin/benefits/([a-z_]{1,40})/(legal|enable|disable)$#', $path, $m) && !$get) {
            return $this->featureAction($m[1], $m[2], $form, $request);
        }
        if (strpos($path, '/admin/pass/') === 0) {
            return $this->routePass($get, $path, $request);
        }
        if (strpos($path, '/admin/scholarship/') === 0) {
            return $this->routeScholarship($get, $path, $request);
        }
        return null;
    }

    // ── 현실 혜택 기능 승인 ─────────────────────────────────

    private function featuresPage(array $problems, Request $request)
    {
        if (!$this->can(Auth::FEATURE_VIEW)) {
            return $this->denied();
        }
        $features = $this->app->features();
        $features->forget();
        $legal = Features::legalStatuses();
        $content = self::done($request) . Html::problems($problems)
            . '<p>현실 혜택 기능은 모두 꺼진 채로 설치됩니다. 켜려면 <strong>① 준법 담당이 법률 검토 완료를 기록</strong>하고 (검토 의견서의 문서 번호, 검토한 곳, 날짜), '
            . '<strong>② 그 기록을 한 사람이 아닌 최고 관리자가 사유를 적고 켭니다.</strong> 끄는 것은 언제든 할 수 있고, '
            . '검토 상태가 "완료"에서 벗어나면 저절로 꺼집니다. 모든 변경은 아래 이력에 남습니다.</p>';
        if ($features->locked()) {
            $content .= '<div class="problems" role="alert"><strong>서버 설정(BENEFITS_LOCKED)으로 현실 혜택 기능이 모두 잠겨 있습니다.</strong> '
                . '여기에서 켜도 동작하지 않습니다.</div>';
        }
        if (!$this->app->crypto()->ready()) {
            $content .= '<div class="problems" role="alert">개인정보 암호화 키(DATA_KEY)가 서버에 설정되어 있지 않습니다. 장학 신청 접수는 켤 수 없습니다.</div>';
        }
        $admins = [];
        foreach ($this->app->db->all('SELECT id, login FROM admins') as $row) {
            $admins[(int)$row['id']] = $row['login'];
        }
        $name = function ($id) use ($admins) {
            return $id === null ? '' : (isset($admins[(int)$id]) ? $admins[(int)$id] : '#' . $id);
        };
        foreach ($features->all() as $code => $row) {
            $on = $features->isEnabled($code);
            $content .= '<h2>' . Html::e($row['name']) . ' — ' . ($on ? '● 켜짐' : '○ 꺼짐') . '</h2><p>' . Html::e($row['description']) . '</p>'
                . Html::table(['항목', '값'], [
                    ['동작 여부', $on ? '켜짐 (동작 중)' : ((int)$row['is_enabled'] === 1 ? '켜져 있다고 적혀 있지만 조건이 맞지 않아 동작하지 않음' : '꺼짐')],
                    ['법률 검토', $legal[$row['legal_status']]],
                    ['검토 의견서 문서 번호', $row['legal_reference']], ['검토한 곳', $row['legal_reviewer']], ['검토한 날짜', $row['legal_reviewed_on']],
                    ['메모', $row['legal_note']],
                    ['검토 상태를 기록한 관리자', trim($name($row['legal_recorded_by']) . ' ' . self::kst($row['legal_recorded_at']))],
                    ['켠 관리자', (int)$row['is_enabled'] === 1 ? trim($name($row['enabled_by']) . ' ' . self::kst($row['enabled_at'])) : ''],
                ]);
            if ($this->can(Auth::FEATURE_LEGAL)) {
                $content .= '<h3>법률 검토 상태 기록</h3>' . $this->form('/admin/benefits/' . $code . '/legal',
                    Html::select('legal_status', '검토 상태', $legal, $row['legal_status'])
                    . Html::input('legal_reference', '검토 의견서 문서 번호', (string)$row['legal_reference'], '검토 완료로 기록하려면 필요합니다')
                    . Html::input('legal_reviewer', '검토한 곳 (법무법인·담당자)', (string)$row['legal_reviewer'])
                    . Html::input('legal_reviewed_on', '검토한 날짜', (string)$row['legal_reviewed_on'], '예: 2026-11-13')
                    . Html::input('legal_note', '메모', (string)$row['legal_note'])
                    . Html::reason(), '기록');
            }
            if ((int)$row['is_enabled'] === 1 && $this->can(Auth::FEATURE_LEGAL)) {
                $content .= '<h3>끄기</h3>' . $this->form('/admin/benefits/' . $code . '/disable', Html::reason(), '이 기능 끄기');
            } elseif ((int)$row['is_enabled'] !== 1 && $this->can(Auth::FEATURE_ENABLE)) {
                $content .= '<h3>켜기</h3><p>법률 검토 완료를 기록한 사람은 켤 수 없습니다.</p>'
                    . $this->form('/admin/benefits/' . $code . '/enable', Html::reason(), '이 기능 켜기');
            }
        }
        $content .= '<h2>만들지 않은 기능</h2><p>아래 기능은 스위치가 없습니다. 법률 검토 전에는 만들지 않기로 했기 때문입니다 '
            . '(docs/REAL_WORLD_REWARDS_POLICY.md 3장).</p><ul>';
        foreach (Features::notBuilt() as $item) {
            $content .= '<li>' . Html::e($item) . '</li>';
        }
        $content .= '</ul>';
        $actions = ['legal_update' => '법률 검토 기록', 'enable' => '켬', 'disable' => '끔', 'auto_disable' => '자동으로 끔'];
        $rows = [];
        foreach ($features->historyOf(100) as $row) {
            $rows[] = [self::kst($row['created_at']), $row['login'], $row['feature_code'],
                isset($actions[$row['action']]) ? $actions[$row['action']] : $row['action'],
                Html::raw('<code>' . Html::e($row['before_value']) . '</code>'), Html::raw('<code>' . Html::e($row['after_value']) . '</code>'), $row['reason']];
        }
        $content .= '<h2>이력 (최근 100건, 한국 시간)</h2>'
            . Html::table(['때', '관리자', '기능', '한 일', '바꾸기 전', '바꾼 뒤', '사유'], $rows, '아직 이력이 없습니다.');
        return $this->page('현실 혜택 기능 승인', $content, $problems ? 422 : 200);
    }

    private function featureAction($code, $action, array $form, Request $request)
    {
        $features = $this->app->features();
        if (!isset($features->all()[$code])) {
            return null;
        }
        if ($action === 'legal') {
            $problems = $features->recordLegal($this->admin, $code, [
                'legalStatus' => self::field($form, 'legal_status'), 'legalReference' => self::field($form, 'legal_reference'),
                'legalReviewer' => self::field($form, 'legal_reviewer'), 'legalReviewedOn' => self::field($form, 'legal_reviewed_on'),
                'legalNote' => self::field($form, 'legal_note'), 'reason' => self::field($form, 'reason'),
            ], $request->ip);
            $done = 'recorded';
        } elseif ($action === 'enable') {
            $problems = $features->enable($this->admin, $code, self::field($form, 'reason'), $request->ip);
            $done = 'enabled';
        } else {
            $problems = $features->disable($this->admin, $code, self::field($form, 'reason'), $request->ip);
            $done = 'disabled';
        }
        return $problems ? $this->featuresPage($problems, $request) : ['redirect' => '/admin/benefits?done=' . $done];
    }
}
