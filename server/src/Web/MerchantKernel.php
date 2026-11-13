<?php
namespace CG\Web;

use CG\Admin\Html;
use CG\Infra\ApiError;
use CG\Infra\Request;
use CG\Service\CityPass;
use CG\Service\MerchantPortal;

/**
 * 상점 확인 화면 /merchant (docs/COUPON_SYSTEM.md 5장, docs/PARTNER_MERCHANT_SYSTEM.md 5장).
 *
 *   로그인 → 쿠폰 코드 확인 → 혜택·조건 확인 → 사용 처리 → 이용 내역 · 통계
 *
 * - 상점 계정은 자기 업체(지점 계정이면 자기 지점)의 쿠폰과 내역만 본다. 그 범위는 서비스(MerchantPortal)가 지킨다.
 * - 손님의 정보(회원번호, 닉네임)는 어느 화면에도 나오지 않는다.
 * - QR 을 찍어 들어온 주소(GET)는 코드를 칸에 채워 줄 뿐이다. 확인과 사용 처리는 직원이 누른 POST 로만 한다.
 * - 쿠키는 SameSite=Strict 다. QR 은 카메라 앱에서 열리므로 첫 요청에 쿠키가 오지 않을 수 있다:
 *   그때는 같은 사이트 안의 링크를 한 번 거치게 해서 다시 로그인하지 않아도 되게 한다.
 */
final class MerchantKernel extends Base
{
    const LOG_NAME = 'merchant';

    /** @var array|null */
    private $user = null;
    private $csrf = '';

    private function cookie($token, $maxAge)
    {
        return $this->cookieHeader(MerchantPortal::COOKIE, $token, $maxAge, '/merchant', 'Strict');
    }

    // ── 틀 ──────────────────────────────────────────────────

    private function layout($title, $content)
    {
        $e = function ($value) {
            return Html::e($value);
        };
        $out = '<!doctype html><html lang="ko"><head><meta charset="utf-8">'
            . '<meta name="robots" content="noindex, nofollow"><meta name="viewport" content="width=device-width, initial-scale=1">'
            . '<title>' . $e($title) . ' — CITY PASS 상점 확인</title>'
            . '<link rel="stylesheet" href="' . $e($this->url('/assets/site.css')) . '"></head><body class="portal">'
            . '<header class="top"><p class="brand"><a href="' . $e($this->url('/merchant')) . '">CITY PASS 상점 확인</a></p>';
        if ($this->user !== null) {
            $out .= '<p class="who">' . $e($this->user['merchantName'])
                . ($this->user['branchName'] !== null ? ' · ' . $e($this->user['branchName']) : ' · 모든 지점')
                . ' · ' . $e($this->user['displayName']) . '</p>';
            if (!$this->user['mustChangePassword']) {
                $out .= '<nav aria-label="상점 메뉴"><ul>'
                    . '<li><a href="' . $e($this->url('/merchant')) . '">쿠폰 확인</a></li>'
                    . '<li><a href="' . $e($this->url('/merchant/history')) . '">이용 내역</a></li>'
                    . '<li><a href="' . $e($this->url('/merchant/stats')) . '">통계</a></li>'
                    . '<li><a href="' . $e($this->url('/merchant/password')) . '">비밀번호</a></li></ul></nav>';
            }
            $out .= '<form method="post" action="' . $e($this->url('/merchant/logout')) . '"><input type="hidden" name="_csrf" value="'
                . $e($this->csrf) . '"><button type="submit" class="plain">로그아웃</button></form>';
        }
        return $out . '</header><main><h1>' . $e($title) . '</h1>' . $content . '</main></body></html>';
    }

    protected function messagePage($title, $text)
    {
        return $this->layout($title, '<p>' . Html::e($text) . '</p>'
            . ($this->user !== null ? '<p><a href="' . Html::e($this->url('/merchant')) . '">쿠폰 확인으로</a></p>' : ''));
    }

    private function page($title, $content, $status = 200)
    {
        return [$status, $this->headers(), $this->layout($title, $content)];
    }

    private function form($path, $inner, $submit)
    {
        return Html::form($this->url($path), $this->csrf, $inner, $submit);
    }

    /** 주소로 받은 코드: 모양이 맞을 때만 쓴다. */
    private static function codeFrom(array $values)
    {
        $code = self::field($values, 'code');
        return preg_match('/^[A-Za-z0-9]{5}-?[A-Za-z0-9]{5}$/', $code) ? strtoupper($code) : '';
    }

    private function codeForm($code, array $problems = [])
    {
        return self::problems($problems) . $this->form('/merchant/verify',
            '<p class="field"><label>쿠폰 코드<br><input type="text" name="code" value="' . Html::e($code) . '" class="code" inputmode="text"'
            . ' autocomplete="off" autocapitalize="characters" maxlength="11" placeholder="XXXXX-XXXXX" required></label></p>'
            . '<p><small>손님의 화면에 보이는 10자리 코드를 넣거나, QR 을 찍으면 코드가 채워집니다. 코드는 5분 동안만 쓸 수 있습니다.</small></p>',
            '코드 확인');
    }

    private function loginPage($error, $code, $status = 200)
    {
        // 로그인 전에는 접속이 없으므로 CSRF 토큰 대신 출처(Origin) 확인과 SameSite 쿠키로 막는다.
        return $this->page('로그인', self::problems($error !== '' ? [$error] : [])
            . '<form method="post" action="' . Html::e($this->url('/merchant/login')) . '">'
            . Html::input('login', '아이디', '') . Html::input('password', '비밀번호', '', '', 'password')
            . '<input type="hidden" name="code" value="' . Html::e($code) . '">'
            . '<p><button type="submit">로그인</button></p></form>'
            . '<p><small>제휴 상점의 계정으로만 들어올 수 있습니다. 계정은 CITY GUARDIANS 운영팀이 만들어 드립니다.</small></p>', $status);
    }

    // ── 경로 ────────────────────────────────────────────────

    protected function dispatch(Request $request)
    {
        self::clean($request);
        $path = rtrim($request->path, '/');
        $method = $request->method;
        $portal = $this->app->merchantPortal();
        if ($method !== 'GET' && $method !== 'POST') {
            return [405, $this->headers(true, ['Allow: GET, POST']), $this->messagePage('쓸 수 없는 요청', '쓸 수 없는 요청 방식입니다.')];
        }
        if ($method === 'POST' && self::crossOrigin($request)) {
            return [403, $this->headers(), $this->messagePage('거절', '다른 곳에서 보낸 요청은 받지 않습니다.')];
        }
        $token = self::field($request->cookies, MerchantPortal::COOKIE);

        if ($path === '/merchant/login') {
            if ($method === 'GET') {
                return $this->loginPage('', self::codeFrom($request->query));
            }
            $code = self::codeFrom($request->form);
            $result = $portal->login(self::field($request->form, 'login'), self::field($request->form, 'password'), $request->ip);
            if ($result === null) {
                return $this->loginPage('아이디와 비밀번호를 다시 확인해 주세요.', $code, 401);
            }
            $next = $result[1]['mustChangePassword'] ? '/merchant/password' : ($code !== '' ? '/merchant/redeem?code=' . $code : '/merchant');
            return $this->redirect($next, [$this->cookie($result[0], MerchantPortal::MAX_HOURS * 3600)]);
        }

        $session = $portal->session($token);
        if ($session === null) {
            $code = self::codeFrom($request->query);
            // QR 로 들어온 첫 요청: 쿠키가 안 왔을 수 있다. 같은 사이트 안의 링크를 한 번 거친다.
            if ($path === '/merchant/redeem' && $method === 'GET' && $code !== '' && self::field($request->query, 'go') !== '1') {
                return $this->page('쿠폰 확인', '<p>쿠폰 코드를 받았습니다.</p><p><a class="button" href="'
                    . Html::e($this->url('/merchant/redeem?code=' . $code . '&go=1')) . '">쿠폰 확인 화면으로 계속</a></p>');
            }
            return $this->redirect('/merchant/login' . ($code !== '' ? '?code=' . $code : ''), $token !== '' ? [$this->cookie('', 0)] : []);
        }
        $this->user = $session['user'];
        $this->csrf = $session['csrf'];
        $user = $this->user;

        if ($method === 'POST') {
            if (!hash_equals($this->csrf, self::field($request->form, '_csrf'))) {
                $portal->audit($user, 'csrf.rejected', mb_substr($path, 0, 64), null, $request->ip);
                return [403, $this->headers(), $this->messagePage('거절', '화면을 새로 고친 뒤 다시 시도해 주세요.')];
            }
        }
        if ($path === '/merchant/logout' && $method === 'POST') {
            $portal->logout($token);
            $portal->audit($user, 'logout', $user['login'], null, $request->ip);
            return $this->redirect('/merchant/login', [$this->cookie('', 0)]);
        }
        if ($path === '/merchant/password') {
            if ($method === 'GET') {
                return $this->passwordPage([]);
            }
            $problems = $portal->changePassword($user, self::field($request->form, 'current'), self::field($request->form, 'new'),
                self::field($request->form, 'again'), $token, $request->ip);
            return $problems ? $this->passwordPage($problems, 422) : $this->redirect('/merchant?done=password');
        }
        // 임시 비밀번호를 바꾸기 전에는 다른 화면을 쓸 수 없다.
        if ($user['mustChangePassword']) {
            return $this->redirect('/merchant/password');
        }
        $get = $method === 'GET';
        if (($path === '/merchant' || $path === '') && $get) {
            return $this->home($request);
        }
        if ($path === '/merchant/redeem' && $get) {
            // 코드를 칸에 채워 줄 뿐이다. 확인도 사용 처리도 하지 않는다.
            return $this->page('쿠폰 확인', $this->codeForm(self::codeFrom($request->query)));
        }
        if ($path === '/merchant/verify' && !$get) {
            return $this->verify($request);
        }
        if ($path === '/merchant/redeem' && !$get) {
            return $this->redeem($request);
        }
        if ($path === '/merchant/history' && $get) {
            return $this->history($request);
        }
        if ($path === '/merchant/stats' && $get) {
            return $this->stats();
        }
        return [404, $this->headers(), $this->messagePage('없는 주소', '없는 주소입니다.')];
    }

    // ── 화면 ────────────────────────────────────────────────

    private function passwordPage(array $problems, $status = 200)
    {
        $intro = $this->user['mustChangePassword']
            ? '<div class="notice" role="status">임시 비밀번호로 들어오셨습니다. 새 비밀번호를 정해야 쿠폰을 확인할 수 있습니다.</div>' : '';
        return $this->page('비밀번호 바꾸기', $intro . self::problems($problems) . $this->form('/merchant/password',
            Html::input('current', '지금 비밀번호', '', '', 'password') . Html::input('new', '새 비밀번호', '', '12자 이상, 영문 대문자·소문자·숫자를 섞어서', 'password')
            . Html::input('again', '새 비밀번호 한 번 더', '', '', 'password'), '바꾸기'), $status);
    }

    private function home(Request $request)
    {
        $stats = $this->app->merchantPortal()->stats($this->user);
        $notice = self::field($request->query, 'done') === 'password' ? Html::notice('비밀번호를 바꿨습니다.') : '';
        if ($this->user['merchantStatus'] !== 'approved') {
            $notice .= '<div class="problems" role="alert">지금은 쿠폰을 처리할 수 없는 상태입니다 (제휴가 일시 중지되었거나 준비 중입니다). 운영팀에 문의해 주세요.</div>';
        }
        return $this->page('쿠폰 확인', $notice . $this->codeForm('')
            . '<h2>오늘</h2><p class="big">' . (int)$stats['today'] . '<small> 장 사용 처리</small></p>'
            . '<p><small>최근 7일 ' . (int)$stats['last7Days'] . '장 · 전체 ' . (int)$stats['total'] . '장</small></p>');
    }

    private function verify(Request $request)
    {
        $code = self::field($request->form, 'code');
        try {
            $info = $this->app->merchantPortal()->verify($this->user, $code, $request->ip);
        } catch (ApiError $error) {
            if ($error->errorCode !== ApiError::NOT_FOUND) {
                throw $error;
            }
            return $this->page('쿠폰 확인', $this->codeForm('', [$error->getMessage()]), 404);
        }
        $inner = '<input type="hidden" name="code" value="' . Html::e(CityPass::normalizeCode($code)) . '">';
        if (count($info['branches']) > 1) {
            $options = ['' => '고르기'];
            foreach ($info['branches'] as $branch) {
                $options[$branch['id']] = $branch['name'];
            }
            $inner .= Html::select('branch_id', '쿠폰을 쓰는 지점', $options, '');
        }
        return $this->page('쿠폰 확인', '<div class="notice" role="status"><strong>쓸 수 있는 쿠폰입니다.</strong> 아직 사용 처리되지 않았습니다.</div>'
            . Html::table(['항목', '내용'], [
                ['혜택', $info['title']], ['할인', $info['discountText']], ['설명', $info['description']], ['이용 조건', $info['conditions']],
                ['쿠폰 유효기간', self::kst($info['expiresAt']) . ' 까지 (한국 시간)'], ['이 코드를 쓸 수 있는 시간', self::kst($info['codeExpiresAt']) . ' 까지'],
            ])
            . '<p>이용 조건을 확인한 뒤 눌러 주세요. <strong>사용 처리는 되돌릴 수 없습니다.</strong></p>'
            . $this->form('/merchant/redeem', $inner, '사용 처리')
            . '<p><a href="' . Html::e($this->url('/merchant')) . '">쓰지 않고 돌아가기</a></p>');
    }

    private function redeem(Request $request)
    {
        $code = self::field($request->form, 'code');
        try {
            $result = $this->app->merchantPortal()->redeem($this->user, $code, self::field($request->form, 'branch_id'), $request->ip);
        } catch (ApiError $error) {
            if ($error->errorCode !== ApiError::NOT_FOUND && $error->errorCode !== ApiError::VALIDATION_FAILED) {
                throw $error;
            }
            return $this->page('쿠폰 확인', $this->codeForm($error->errorCode === ApiError::VALIDATION_FAILED ? CityPass::normalizeCode($code) : '',
                [$error->getMessage()]), $error->httpStatus());
        }
        return $this->page('사용 처리 완료', '<div class="notice" role="status"><strong>사용 처리했습니다.</strong></div>'
            . Html::table(['항목', '내용'], [['혜택', $result['title']], ['할인', $result['discountText']],
                ['처리한 때', self::kst($result['redeemedAt']) . ' (한국 시간)']])
            . '<p>이 쿠폰은 다시 쓸 수 없습니다.</p><p><a class="button" href="' . Html::e($this->url('/merchant')) . '">다음 쿠폰 확인</a></p>');
    }

    private function history(Request $request)
    {
        $page = self::field($request->query, 'page');
        $page = ctype_digit($page) && (int)$page >= 1 && (int)$page <= 100000 ? (int)$page : 1;
        $list = $this->app->merchantPortal()->history($this->user, $page);
        $rows = [];
        foreach ($list as $row) {
            $rows[] = [self::kst($row['redeemed_at']), $row['title'], CityPass::discountText($row['discount_type'], (int)$row['discount_value']),
                $row['branch_name'], $row['staff_name']];
        }
        $pager = '';
        if ($page > 1) {
            $pager .= '<a href="' . Html::e($this->url('/merchant/history?page=' . ($page - 1))) . '">이전</a> ';
        }
        if (count($list) >= 50) {
            $pager .= '<a href="' . Html::e($this->url('/merchant/history?page=' . ($page + 1))) . '">다음</a>';
        }
        return $this->page('이용 내역', '<p>우리 매장에서 사용 처리한 쿠폰입니다. 손님의 정보는 보이지 않습니다.</p>'
            . '<div class="scroll">' . Html::table(['처리한 때 (한국 시간)', '혜택', '할인', '지점', '처리한 사람'], $rows, '아직 사용 처리한 쿠폰이 없습니다.') . '</div>'
            . ($pager !== '' ? '<p>' . $pager . '</p>' : ''));
    }

    private function stats()
    {
        $stats = $this->app->merchantPortal()->stats($this->user);
        $content = Html::table(['기간', '사용 처리'], [['오늘', (int)$stats['today']], ['최근 7일', (int)$stats['last7Days']], ['전체', (int)$stats['total']]]);
        $rows = [];
        foreach ($stats['byOffer'] as $row) {
            $rows[] = [$row['title'], (int)$row['n']];
        }
        $content .= '<h2>혜택별</h2>' . Html::table(['혜택', '사용 처리'], $rows, '아직 없습니다.');
        $rows = [];
        foreach ($stats['daily'] as $row) {
            $rows[] = [$row['day'], (int)$row['n']];
        }
        $content .= '<h2>최근 14일</h2>' . Html::table(['날짜', '사용 처리'], $rows, '아직 없습니다.');
        if ($stats['offers']) {
            $names = ['draft' => '준비 중', 'pending' => '승인 대기', 'approved' => '진행 중', 'paused' => '발급 중지', 'ended' => '종료'];
            $rows = [];
            foreach ($stats['offers'] as $row) {
                $rows[] = [$row['title'], isset($names[$row['status']]) ? $names[$row['status']] : $row['status'],
                    (int)$row['issued_count'] . ($row['total_limit'] !== null ? ' / ' . (int)$row['total_limit'] : ''), self::kst($row['ends_at'])];
            }
            $content .= '<h2>우리 업체의 혜택</h2>' . Html::table(['혜택', '상태', '발급한 쿠폰', '이용 끝 (한국 시간)'], $rows);
        }
        return $this->page('통계', '<div class="scroll">' . $content . '</div>');
    }
}
