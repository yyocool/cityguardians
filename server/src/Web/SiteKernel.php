<?php
namespace CG\Web;

use CG\Admin\Html;
use CG\Infra\Request;
use CG\Service\WebAuth;

/**
 * 홈페이지의 CITY PASS 와 SCHOLARSHIP (docs/WEBSITE_SPEC.md 11장).
 *
 *   /city-pass …   서비스 소개 · 제휴 상점 · 지역별 혜택 · 쿠폰 · 제휴 문의        (SitePassPages)
 *   /scholarship … 장학사업 소개 · 모집 공고 · 지원 방법 · 심사 기준 · 지원 현황 · FAQ (SiteScholarshipPages)
 *   /login, /logout  게임에서 받은 일회용 코드로 로그인
 *
 * - 게임 화면과 같은 서비스(CityPass, Scholarships)를 쓴다: 웹과 게임이 같은 데이터를 본다.
 * - 준비되지 않은 혜택과 프로그램은 보여 주지 않는다. 기능이 꺼져 있으면 "준비 중"이라고만 말한다.
 * - 스크립트가 없다. 좁은 화면에서도 그대로 읽힌다 (public/assets/site.css).
 */
final class SiteKernel extends Base
{
    use SitePassPages;
    use SiteScholarshipPages;

    const LOG_NAME = 'site';

    /** @var array|null 로그인한 플레이어. */
    private $player = null;
    private $csrf = '';

    /** 이 화면 묶음이 맡는 주소인가. */
    public static function owns($path)
    {
        return preg_match('#^/(city-pass|scholarship|login|logout)(/|$)#', $path) === 1;
    }

    public static function menu()
    {
        return [
            'CITY PASS' => [
                ['/city-pass', '서비스 소개'], ['/city-pass/merchants', '제휴 상점'], ['/city-pass/offers', '지역별 혜택'],
                ['/city-pass/coupons', '쿠폰'], ['/city-pass/partnership', '제휴 문의'],
            ],
            'SCHOLARSHIP' => [
                ['/scholarship', '장학사업 소개'], ['/scholarship/programs', '모집 공고'], ['/scholarship/how-to-apply', '지원 방법'],
                ['/scholarship/criteria', '심사 기준'], ['/scholarship/status', '지원 현황'], ['/scholarship/faq', 'FAQ'],
            ],
        ];
    }

    // ── 틀 ──────────────────────────────────────────────────

    private function layout($title, $content, $description = '')
    {
        $e = function ($value) {
            return Html::e($value);
        };
        $out = '<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">'
            . '<title>' . $e($title) . ' — CITY GUARDIANS</title>'
            . ($description !== '' ? '<meta name="description" content="' . $e($description) . '">' : '')
            . '<link rel="stylesheet" href="' . $e($this->url('/assets/site.css')) . '"></head><body>'
            . '<a class="skip" href="#main">본문으로 건너뛰기</a>'
            . '<header class="top"><p class="brand"><a href="' . $e($this->url('/city-pass')) . '">CITY GUARDIANS</a></p>'
            . '<nav aria-label="주 메뉴">';
        foreach (self::menu() as $group => $items) {
            $out .= '<div class="group"><p>' . $e($group) . '</p><ul>';
            foreach ($items as $item) {
                $out .= '<li><a href="' . $e($this->url($item[0])) . '">' . $e($item[1]) . '</a></li>';
            }
            $out .= '</ul></div>';
        }
        $out .= '</nav><div class="who">';
        if ($this->player !== null) {
            $out .= '<span>' . $e($this->player['nickname'] !== null ? $this->player['nickname'] : '이방인') . '</span> '
                . '<form method="post" action="' . $e($this->url('/logout')) . '"><input type="hidden" name="_csrf" value="' . $e($this->csrf)
                . '"><button type="submit" class="plain">로그아웃</button></form>';
        } else {
            $out .= '<a href="' . $e($this->url('/login')) . '">로그인</a>';
        }
        return $out . '</div></header><main id="main"><h1>' . $e($title) . '</h1>' . $content . '</main>'
            . '<footer><p>CITY PASS 와 GUARDIANS SCHOLARSHIP 은 게임 보상이 아닙니다. 게임머니(GC), 전투 성적, 랭킹, 아이템, 결제 금액은 '
            . '현실의 현금·상품권·쿠폰·장학금으로 바뀌지 않습니다.</p></footer></body></html>';
    }

    protected function messagePage($title, $text)
    {
        return $this->layout($title, '<p>' . Html::e($text) . '</p>');
    }

    /** @param bool|null $private null 이면 로그인했을 때만 사적인 화면으로 다룬다 */
    private function page($title, $content, $status = 200, $description = '', $private = null)
    {
        $private = $private !== null ? $private : $this->player !== null;
        return [$status, $this->headers($private || $status !== 200), $this->layout($title, $content, $description)];
    }

    private function form($path, $inner, $submit)
    {
        return Html::form($this->url($path), $this->csrf, $inner, $submit);
    }

    private function a($path, $text, $class = '')
    {
        return '<a' . ($class !== '' ? ' class="' . Html::e($class) . '"' : '') . ' href="' . Html::e($this->url($path)) . '">' . Html::e($text) . '</a>';
    }

    private static function soon($text)
    {
        return '<div class="soon" role="status"><strong>준비 중</strong><p>' . Html::e($text) . '</p></div>';
    }

    /** 여러 줄 글을 문단으로. */
    private static function prose($text)
    {
        return '<p class="prose">' . nl2br(Html::e($text)) . '</p>';
    }

    /** 로그인해야 하는 화면. 로그인하지 않았으면 로그인으로 보낸다 (돌아올 주소를 들고). */
    private function needLogin($path)
    {
        return $this->redirect('/login?next=' . rawurlencode($path));
    }

    /** 로그인한 뒤 돌아갈 주소: 이 사이트 안의 정해진 모양만 받는다 (다른 사이트로 보내는 데 쓰이지 않게). */
    private static function nextPath($value)
    {
        return is_string($value) && preg_match('#^/(city-pass|scholarship)(/[a-z0-9_\-]{1,40}){0,3}$#', $value) ? $value : '/city-pass/coupons';
    }

    // ── 경로 ────────────────────────────────────────────────

    protected function dispatch(Request $request)
    {
        self::clean($request);
        $path = rtrim($request->path, '/');
        $method = $request->method;
        if ($method !== 'GET' && $method !== 'POST') {
            return [405, $this->headers(true, ['Allow: GET, POST']), $this->messagePage('쓸 수 없는 요청', '쓸 수 없는 요청 방식입니다.')];
        }
        $get = $method === 'GET';
        if (!$get && self::crossOrigin($request)) {
            return [403, $this->headers(), $this->messagePage('거절', '다른 곳에서 보낸 요청은 받지 않습니다.')];
        }
        $auth = $this->app->webAuth();
        $token = self::field($request->cookies, WebAuth::COOKIE);
        $session = $token !== '' ? $auth->session($token) : null;
        if ($session !== null) {
            $this->player = $session['player'];
            $this->csrf = $session['csrf'];
        }

        // ── 로그인 ──
        if ($path === '/login') {
            $next = self::nextPath(self::field($get ? $request->query : $request->form, 'next'));
            if ($get) {
                return $this->loginPage('', $next);
            }
            // 로그인 전에는 접속이 없으므로 CSRF 토큰 대신 출처(Origin) 확인과 SameSite 쿠키로 막는다.
            $result = $auth->login(self::field($request->form, 'code'), $request->ip);
            if ($result === null) {
                return $this->loginPage('코드를 다시 확인해 주세요. 코드는 5분 동안, 한 번만 쓸 수 있습니다.', $next, 401);
            }
            // 게임이나 다른 곳의 링크로 들어와도 접속이 이어지도록 Lax 로 둔다. POST 는 출처와 CSRF 토큰으로 막는다.
            return $this->redirect($next, [$this->cookieHeader(WebAuth::COOKIE, $result[0], WebAuth::MAX_HOURS * 3600, '/', 'Lax')]);
        }
        // 보호자 동의 화면은 로그인 없이 쓴다 (주소의 값이 열쇠다). CSRF 토큰 대신 출처 확인으로 막는다.
        if (preg_match('#^/scholarship/consent/([0-9a-f]{64})$#', $path, $m)) {
            return $this->guardianConsent($m[1], $get, $request);
        }

        if (!$get) {
            if ($this->player === null) {
                return $this->needLogin('/city-pass/coupons');
            }
            if (!hash_equals($this->csrf, self::field($request->form, '_csrf'))) {
                return [403, $this->headers(), $this->messagePage('거절', '화면을 새로 고친 뒤 다시 시도해 주세요.')];
            }
            if ($path === '/logout') {
                $auth->logout($token);
                $this->player = null;
                return $this->redirect('/city-pass', [$this->cookieHeader(WebAuth::COOKIE, '', 0, '/', 'Lax')]);
            }
        }
        $result = strpos($path, '/city-pass') === 0 ? $this->routePass($get, $path, $request)
            : (strpos($path, '/scholarship') === 0 ? $this->routeScholarship($get, $path, $request) : null);
        if ($result === null) {
            return [404, $this->headers(), $this->messagePage('없는 주소', '없는 주소입니다.')];
        }
        return $result;
    }

    private function loginPage($error, $next, $status = 200)
    {
        if ($this->player !== null && $error === '') {
            return $this->redirect($next);
        }
        return $this->page('로그인', self::problems($error !== '' ? [$error] : [])
            . '<p>게임 계정에는 아이디와 비밀번호가 없습니다. 게임 안에서 일회용 코드를 받아 여기에 넣습니다.</p>'
            . '<ol><li>게임에서 휴대전화(P 키)를 열고 <strong>홈페이지 로그인 코드</strong>를 누릅니다.</li>'
            . '<li>화면에 보이는 8자리 코드를 5분 안에 아래에 넣습니다.</li></ol>'
            . '<form method="post" action="' . Html::e($this->url('/login')) . '">'
            . '<input type="hidden" name="next" value="' . Html::e($next) . '">'
            . '<p class="field"><label>로그인 코드<br><input type="text" name="code" class="code" maxlength="9" autocomplete="off" autocapitalize="characters"'
            . ' placeholder="XXXX-XXXX" required></label></p><p><button type="submit">로그인</button></p></form>'
            . '<p><small>코드는 한 번만 쓸 수 있습니다. 이 홈페이지의 접속은 게임의 접속과 별개이고, 4시간이 지나면 끝납니다.</small></p>', $status, '', true);
    }

    /** 지역 고르기: 시·도로 묶은 시·군·구. */
    private function regionSelect($name, $label, $selected, $emptyText = null)
    {
        $out = '<p class="field"><label>' . Html::e($label) . ' <select name="' . Html::e($name) . '">';
        if ($emptyText !== null) {
            $out .= '<option value="">' . Html::e($emptyText) . '</option>';
        }
        $sido = null;
        foreach ($this->app->db->all('SELECT r.code, r.name, s.name AS sido_name FROM regions r JOIN sidos s ON s.id = r.sido_id ORDER BY s.id, r.name') as $row) {
            if ($row['sido_name'] !== $sido) {
                $out .= ($sido !== null ? '</optgroup>' : '') . '<optgroup label="' . Html::e($row['sido_name']) . '">';
                $sido = $row['sido_name'];
            }
            $out .= '<option value="' . Html::e($row['code']) . '"' . ((string)$row['code'] === (string)$selected ? ' selected' : '') . '>' . Html::e($row['name']) . '</option>';
        }
        return $out . ($sido !== null ? '</optgroup>' : '') . '</select></label></p>';
    }
}
