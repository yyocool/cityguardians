<?php
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Service\MerchantPortal;
use CG\Service\WebAuth;
use CG\Web\MerchantKernel;
use CG\Web\SiteKernel;

/**
 * 홈페이지(/city-pass, /scholarship)와 상점 확인 화면(/merchant)을 요청 단위로 확인한다
 * (docs/WEBSITE_SPEC.md 11장, docs/COUPON_SYSTEM.md 5장).
 */
final class BenefitWebTest extends BenefitTestCase
{
    private function site($method, $path, $cookie = null, array $form = [], array $query = [], array $headers = [])
    {
        $request = Request::make($method, $path, [], $headers, $query);
        $request->form = $form;
        if ($cookie !== null) {
            $request->cookies = [WebAuth::COOKIE => $cookie];
        }
        return (new SiteKernel($this->app, ''))->handle($request);
    }

    private function shop($method, $path, $cookie = null, array $form = [], array $query = [], array $headers = [])
    {
        $request = Request::make($method, $path, [], $headers, $query);
        $request->form = $form;
        if ($cookie !== null) {
            $request->cookies = [MerchantPortal::COOKIE => $cookie];
        }
        return (new MerchantKernel($this->app, ''))->handle($request);
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

    private static function cookieOf(array $response, $name)
    {
        foreach ($response[1] as $header) {
            if (preg_match('/^Set-Cookie: ' . $name . '=([0-9a-f]{64});(.*)$/', $header, $m)) {
                return [$m[1], $m[2]];
            }
        }
        return null;
    }

    private static function csrfIn($html)
    {
        T::ok(preg_match('/name="_csrf" value="([0-9a-f]{64})"/', $html, $m) === 1, '화면에 CSRF 토큰이 없다');
        return $m[1];
    }

    /** 어느 화면에나 맞아야 하는 것: 스크립트가 없고, 밖의 자원을 부르지 않고, 보안 헤더가 있다. */
    private static function checkPage(array $response, $what)
    {
        list(, $headers, $html) = $response;
        T::ok(stripos($html, '<script') === false, $what . ': 스크립트가 있다');
        T::ok(preg_match('#(src|href|action)="(https?:)?//#i', $html) === 0, $what . ': 밖의 자원을 부른다');
        T::ok(preg_match('/\son[a-z]+\s*=/i', $html) === 0, $what . ': 인라인 이벤트 처리기가 있다');
        T::ok(in_array('X-Frame-Options: DENY', $headers, true) && in_array('X-Content-Type-Options: nosniff', $headers, true)
            && in_array('Referrer-Policy: no-referrer', $headers, true), $what . ': 보안 헤더');
        $csp = '';
        foreach ($headers as $header) {
            if (strpos($header, 'Content-Security-Policy:') === 0) {
                $csp = $header;
            }
        }
        T::ok(strpos($csp, "default-src 'none'") !== false && strpos($csp, 'script-src') === false && strpos($csp, "form-action 'self'") !== false, $what . ': CSP');
    }

    /** 게임에서 받은 코드로 홈페이지에 로그인한다. @return array [쿠키, CSRF 토큰] */
    private function signIn(array $player)
    {
        $code = $this->app->webAuth()->issueCode($player)['code'];
        $response = $this->site('POST', '/login', null, ['code' => $code]);
        T::same(303, $response[0]);
        $cookie = self::cookieOf($response, WebAuth::COOKIE);
        T::ok($cookie !== null, '로그인 쿠키가 없다');
        return [$cookie[0], self::csrfIn($this->site('GET', '/city-pass', $cookie[0])[2])];
    }

    private function sitePost(array $session, $path, array $form = [])
    {
        return $this->site('POST', $path, $session[0], $form + ['_csrf' => $session[1]]);
    }

    /** 임시 비밀번호를 바꾼 상점 계정으로 로그인한다. @return array [쿠키, CSRF 토큰] */
    private function shopSignIn($merchantId, $login, $branchId = null)
    {
        $this->merchantUser($merchantId, $login, $branchId);
        $response = $this->shop('POST', '/merchant/login', null, ['login' => $login, 'password' => self::PASSWORD]);
        T::same(303, $response[0]);
        T::same('/merchant', self::location($response));
        $cookie = self::cookieOf($response, MerchantPortal::COOKIE);
        return [$cookie[0], self::csrfIn($this->shop('GET', '/merchant', $cookie[0])[2])];
    }

    private function shopPost(array $session, $path, array $form = [])
    {
        return $this->shop('POST', $path, $session[0], $form + ['_csrf' => $session[1]]);
    }

    // ── 홈페이지: 준비되지 않은 것은 보이지 않는다 ─────────

    public function testWithEverythingOffTheSiteSaysPreparingAndShowsNothingElse()
    {
        // 승인된 업체·혜택·프로그램이 DB 에 있어도, 기능이 꺼져 있으면 보이지 않아야 한다.
        $this->enableCityPass();
        $this->enableScholarship();
        $this->offer($this->merchant(['name' => '보이면 안 되는 카페']), ['title' => '보이면 안 되는 혜택']);
        $this->program(['title' => '보이면 안 되는 장학']);
        T::ok(strpos($this->site('GET', '/city-pass/merchants')[2], '보이면 안 되는 카페') !== false);
        T::ok(strpos($this->site('GET', '/scholarship/programs')[2], '보이면 안 되는 장학') !== false);
        foreach (array_keys($this->app->features()->all()) as $code) {
            $this->app->features()->disable($this->compliance(), $code, '운영 정책이 확정되지 않음');
        }
        $public = ['/city-pass', '/city-pass/merchants', '/city-pass/offers', '/city-pass/partnership',
            '/scholarship', '/scholarship/programs', '/scholarship/how-to-apply', '/scholarship/criteria', '/scholarship/faq'];
        foreach (SiteKernel::menu() as $items) {
            foreach ($items as $item) {
                $response = $this->site('GET', $item[0]);
                if (!in_array($item[0], $public, true)) {
                    // 내 쿠폰, 지원 현황은 로그인해야 한다.
                    T::same(303, $response[0], $item[0]);
                    T::same('/login?next=' . rawurlencode($item[0]), self::location($response));
                    continue;
                }
                T::same(200, $response[0], $item[0]);
                self::checkPage($response, $item[0]);
                T::ok(strpos($response[2], '준비 중') !== false, $item[0] . ' 가 준비 중이라고 말하지 않는다');
                T::ok(strpos($response[2], '보이면 안 되는') === false, $item[0] . ' 가 꺼진 기능의 자료를 보여 준다');
                // 누구나 보는 안내 화면은 검색에 나와도 된다. 저장은 하지 않게 한다.
                T::ok(in_array('Cache-Control: no-cache', $response[1], true) && !in_array('X-Robots-Tag: noindex, nofollow', $response[1], true));
                // 메뉴가 모두 들어 있다.
                foreach (['서비스 소개', '제휴 상점', '지역별 혜택', '쿠폰', '제휴 문의', '장학사업 소개', '모집 공고', '지원 방법', '심사 기준', '지원 현황', 'FAQ'] as $label) {
                    T::ok(strpos($response[2], '>' . $label . '</a>') !== false, $item[0] . ' 의 메뉴에 ' . $label . ' 가 없다');
                }
                T::ok(strpos($response[2], 'name="viewport"') !== false);
            }
        }
        // 꺼져 있으면 프로그램 하나를 주소로 직접 열 수도 없다.
        $id = $this->db->val('SELECT public_id FROM scholarship_programs LIMIT 1');
        $response = $this->site('GET', '/scholarship/programs/' . $id);
        T::same(403, $response[0]);
        T::ok(strpos($response[2], '보이면 안 되는') === false);
        // 이 화면 묶음이 맡지 않는 주소.
        T::ok(!SiteKernel::owns('/') && !SiteKernel::owns('/city-passport') && !SiteKernel::owns('/admin') && SiteKernel::owns('/scholarship/faq'));
        T::same(404, $this->site('GET', '/city-pass/none')[0]);
        T::same(405, $this->site('DELETE', '/city-pass')[0]);
    }

    public function testPartnershipPageShowsAContactOnlyWhenOneIsConfigured()
    {
        $html = $this->site('GET', '/city-pass/partnership')[2];
        T::ok(strpos($html, '준비 중') !== false && strpos($html, '@') === false);
        T::ok(strpos($html, '<form method="post"') === false, '제휴 문의 화면은 개인정보를 받지 않는다');
    }

    // ── 홈페이지: 로그인 ───────────────────────────────────

    public function testLoginUsesAOneTimeCodeFromTheGame()
    {
        $player = $this->resident();
        T::same(200, $this->site('GET', '/login')[0]);
        T::same(401, $this->site('POST', '/login', null, ['code' => 'AAAA-BBBB'])[0]);

        $code = $this->app->webAuth()->issueCode($player)['code'];
        T::ok(preg_match('/^[A-Z0-9]{4}-[A-Z0-9]{4}$/', $code) === 1);
        // 다른 사이트로 보내는 데 쓰이지 않는다.
        foreach (['//evil.example/x', 'https://evil.example', '/admin', '/city-pass/../admin'] as $next) {
            T::ok(strpos($this->site('GET', '/login', null, [], ['next' => $next])[2], 'value="/city-pass/coupons"') !== false, $next);
        }
        $response = $this->site('POST', '/login', null, ['code' => strtolower($code), 'next' => '/scholarship/status']);
        T::same(303, $response[0]);
        T::same('/scholarship/status', self::location($response));
        list($token, $flags) = self::cookieOf($response, WebAuth::COOKIE);
        T::ok(strpos($flags, 'HttpOnly') !== false && strpos($flags, 'SameSite=Lax') !== false && strpos($flags, 'Secure') !== false && strpos($flags, 'Path=/;') !== false);
        // 토큰은 해시로만 저장된다.
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM web_sessions WHERE token_hash = ?', [$token]));
        // 같은 코드는 두 번 쓸 수 없다.
        T::same(401, $this->site('POST', '/login', null, ['code' => $code])[0]);

        $page = $this->site('GET', '/scholarship/status', $token);
        T::same(200, $page[0]);
        // 로그인한 사람의 화면은 저장하지 않고, 검색에도 맡기지 않는다.
        T::ok(in_array('Cache-Control: no-store', $page[1], true) && in_array('X-Robots-Tag: noindex, nofollow', $page[1], true));
        T::ok(strpos($page[2], htmlspecialchars($player['nickname'])) !== false);

        // 로그아웃은 CSRF 토큰이 있어야 한다.
        T::same(403, $this->site('POST', '/logout', $token)[0]);
        T::same(200, $this->site('GET', '/scholarship/status', $token)[0]);
        T::same(303, $this->site('POST', '/logout', $token, ['_csrf' => self::csrfIn($page[2])])[0]);
        T::same(303, $this->site('GET', '/scholarship/status', $token)[0]);

        // 30분 동안 쓰지 않으면 끝난다.
        $session = $this->signIn($player);
        Clock::advance(31 * 60);
        T::same(303, $this->site('GET', '/city-pass/coupons', $session[0])[0]);
    }

    public function testPostsNeedASessionACsrfTokenAndTheSameOrigin()
    {
        $this->enableCityPass();
        $player = $this->resident();
        $session = $this->signIn($player);
        $form = ['agree' => '1', 'region' => '11110'];
        // 로그인하지 않았으면 로그인으로.
        T::same(303, $this->site('POST', '/city-pass/join', null, $form)[0]);
        // 토큰이 없거나 틀리면 거절.
        T::same(403, $this->site('POST', '/city-pass/join', $session[0], $form)[0]);
        T::same(403, $this->site('POST', '/city-pass/join', $session[0], $form + ['_csrf' => str_repeat('0', 64)])[0]);
        // 다른 사이트에서 보낸 폼은 토큰이 맞아도 거절.
        T::same(403, $this->site('POST', '/city-pass/join', $session[0], $form + ['_csrf' => $session[1]], [],
            ['Origin' => 'https://evil.example', 'Host' => 'cg.test'])[0]);
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM city_pass_memberships'));
        // 같은 사이트면 받는다.
        T::same(303, $this->site('POST', '/city-pass/join', $session[0], $form + ['_csrf' => $session[1]], [],
            ['Origin' => 'https://cg.test', 'Host' => 'cg.test'])[0]);
        T::same(1, (int)$this->db->val('SELECT COUNT(*) FROM city_pass_memberships'));
        // 배열로 보낸 값은 버린다 (오류가 나지 않는다).
        T::ok($this->site('POST', '/city-pass/region', $session[0], ['_csrf' => $session[1], 'region' => ['x']])[0] < 500);
    }

    // ── 홈페이지: CITY PASS ────────────────────────────────

    public function testOnlyApprovedPartnersAndOffersAppearAndTextIsEscaped()
    {
        $this->enableCityPass();
        $partners = $this->app->partners();
        $shown = $this->merchant(['name' => '<b>굵은</b> 카페'], ['image_file' => 'cafe-1.jpg']);
        $this->offer($shown, ['title' => '아메리카노 <i>10%</i> 할인']);
        // 승인 대기 중인 업체와 혜택.
        list($pending) = $partners->saveMerchant($this->passManager(), null, $this->merchantInput(['name' => '승인 전 식당', 'category' => 'restaurant']));
        T::eq([], $partners->saveBranch($this->passManager(), $pending, null, $this->branchInput()));
        list($draftOffer, $problems) = $partners->saveOffer($this->passManager(), $shown, null, $this->offerInput(['title' => '승인 전 혜택']));
        T::eq([], $problems);

        $merchants = $this->site('GET', '/city-pass/merchants');
        self::checkPage($merchants, '제휴 상점');
        T::ok(strpos($merchants[2], '&lt;b&gt;굵은&lt;/b&gt; 카페') !== false && strpos($merchants[2], '<b>굵은') === false);
        T::ok(strpos($merchants[2], '승인 전 식당') === false, '승인하지 않은 업체가 제휴 상점으로 보인다');
        T::ok(strpos($merchants[2], '서울 종로구 테스트로 1') !== false && strpos($merchants[2], 'src="/media/cafe-1.jpg"') !== false);
        // 업체 담당자의 연락처와 계약 번호는 밖에 나가지 않는다.
        T::ok(strpos($merchants[2], 'owner@example.com') === false && strpos($merchants[2], 'CONTRACT-TEST') === false && strpos($merchants[2], '김담당') === false);

        $offers = $this->site('GET', '/city-pass/offers');
        self::checkPage($offers, '지역별 혜택');
        T::ok(strpos($offers[2], '아메리카노 &lt;i&gt;10%&lt;/i&gt; 할인') !== false && strpos($offers[2], '승인 전 혜택') === false);
        T::ok(strpos($offers[2], '1회 1잔') !== false && strpos($offers[2], '10% 할인') !== false);
        // 로그인하지 않은 사람에게는 받기 단추가 없다.
        T::ok(strpos($offers[2], '/city-pass/claim') === false);

        // 고르기: 업종, 지역. 모르는 값은 무시한다.
        T::ok(strpos($this->site('GET', '/city-pass/merchants', null, [], ['category' => 'fitness'])[2], '굵은') === false);
        T::ok(strpos($this->site('GET', '/city-pass/merchants', null, [], ['category' => 'cafe', 'region' => '11110'])[2], '굵은') !== false);
        T::ok(strpos($this->site('GET', '/city-pass/merchants', null, [], ['category' => '"><script>', 'region' => "1' OR 1"])[2], '굵은') !== false);

        // 제휴를 멈추면 바로 내려간다.
        T::eq([], $partners->merchantTransition($this->passManager(), $shown, 'suspend', '계약 문제'));
        T::ok(strpos($this->site('GET', '/city-pass/merchants')[2], '굵은') === false);
        T::ok(strpos($this->site('GET', '/city-pass/offers')[2], '아메리카노') === false);
    }

    public function testMemberJoinsClaimsAndShowsAOneTimeCodeWithoutTouchingTheGameEconomy()
    {
        $this->enableCityPass();
        $offer = $this->offer($this->merchant());
        $player = $this->resident('11110', '쿠폰가디언');
        $this->give($player, 5000);
        $stranger = $this->resident();
        $gc = $this->gc($player);
        $ledger = $this->ledgerRows();
        $session = $this->signIn($player);

        // 가입 전: 가입 폼이 보이고, 전입신고한 지역이 골라져 있다.
        $page = $this->site('GET', '/city-pass/coupons', $session[0]);
        T::same(200, $page[0]);
        T::ok(strpos($page[2], 'action="/city-pass/join"') !== false && strpos($page[2], '<option value="11110" selected>') !== false);
        // 실제 이름, 전화번호, 주소를 묻는 칸이 없다.
        preg_match_all('/name="([^"]+)"/', $page[2], $names);
        $asked = array_values(array_unique(array_diff($names[1], ['viewport'])));
        sort($asked);
        T::eq(['_csrf', 'agree', 'region'], $asked);
        // 약관에 동의하지 않으면 가입되지 않는다.
        T::same(422, $this->sitePost($session, '/city-pass/join', ['region' => '11110'])[0]);
        // 다른 지역을 관심 지역으로 골라도 된다 (거주지를 확인하지 않는다).
        T::same('/city-pass/coupons?done=joined', self::location($this->sitePost($session, '/city-pass/join', ['agree' => '1', 'region' => '26110'])));
        $memberNo = $this->db->val('SELECT member_no FROM city_pass_memberships WHERE player_id = ?', [$player['id']]);
        $page = $this->site('GET', '/city-pass/coupons', $session[0], [], ['done' => 'joined']);
        T::ok(strpos($page[2], $memberNo) !== false && strpos($page[2], '쿠폰가디언') !== false && strpos($page[2], '<option value="26110" selected>') !== false);

        // 쿠폰 받기: 폼에 요청 번호가 들어 있어서 두 번 눌러도 한 장이다.
        $offers = $this->site('GET', '/city-pass/offers', $session[0])[2];
        T::ok(preg_match('/name="key" value="(web-[A-Za-z0-9]{24})"/', $offers, $key) === 1);
        $form = ['offer' => $offer['public_id'], 'key' => $key[1]];
        T::same('/city-pass/coupons?done=claimed', self::location($this->sitePost($session, '/city-pass/claim', $form)));
        T::same(303, $this->sitePost($session, '/city-pass/claim', $form)[0]);
        T::same(1, (int)$this->db->val('SELECT COUNT(*) FROM benefit_coupons'));
        // 한 사람 한 장: 새 요청 번호로도 더 받을 수 없다.
        T::ok($this->sitePost($session, '/city-pass/claim', ['offer' => $offer['public_id'], 'key' => 'web-' . str_repeat('b', 24)])[0] >= 400);
        T::ok(strpos($this->site('GET', '/city-pass/offers', $session[0])[2], '이미 받은 쿠폰입니다') !== false);

        // 매장에 보여 줄 코드: 그 응답에서만 보이고, 쿠폰은 아직 쓰이지 않았다.
        $couponId = $this->db->val('SELECT public_id FROM benefit_coupons');
        $shown = $this->sitePost($session, '/city-pass/present', ['coupon' => $couponId]);
        T::same(200, $shown[0]);
        T::ok(preg_match('#<p class="code-big">([A-Z0-9]{5}-[A-Z0-9]{5})</p>#', $shown[2], $code) === 1);
        T::ok(in_array('Cache-Control: no-store', $shown[1], true));
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));
        T::ok(strpos($this->site('GET', '/city-pass/coupons', $session[0])[2], $code[1]) === false, '코드가 다시 보인다');
        // 남의 쿠폰으로는 코드를 만들 수 없다.
        $other = $this->signIn($stranger);
        T::same(404, $this->sitePost($other, '/city-pass/present', ['coupon' => $couponId])[0]);

        // 상점이 사용 처리하면 이용 내역에 나온다.
        $staff = $this->merchantUser((int)$offer['merchant_id'], 'cafe.owner');
        $this->app->merchantPortal()->redeem($staff, $code[1]);
        $page = $this->site('GET', '/city-pass/coupons', $session[0])[2];
        T::ok(strpos($page, '사용함') !== false && strpos($page, '매장에 보여 줄 코드 만들기') === false);

        // 게임 경제는 그대로다.
        T::same($gc, $this->gc($player));
        T::same($ledger, $this->ledgerRows());
    }

    // ── 홈페이지: SCHOLARSHIP ──────────────────────────────

    public function testOnlyPublishedProgramsAppearWithTheirCriteria()
    {
        $this->enableScholarship();
        $office = $this->app->scholarshipOffice();
        $program = $this->program(['title' => '금융 <u>역량</u> 장학'], [['rule_type' => 'statement', 'description' => '금융교육 과정을 마쳤습니다', 'params' => '']]);
        list($draft, $problems) = $office->saveProgram($this->schManager(), null, $this->programInput(['title' => '공고 전 프로그램']));
        T::eq([], $problems);

        $list = $this->site('GET', '/scholarship/programs');
        self::checkPage($list, '모집 공고');
        T::ok(strpos($list[2], '금융 &lt;u&gt;역량&lt;/u&gt; 장학') !== false && strpos($list[2], '공고 전 프로그램') === false && strpos($list[2], '모집 중') !== false);
        $draftId = $this->db->val('SELECT public_id FROM scholarship_programs WHERE id = ?', [$draft]);
        T::same(404, $this->site('GET', '/scholarship/programs/' . $draftId)[0]);

        $detail = $this->site('GET', '/scholarship/programs/' . $program['public_id']);
        T::same(200, $detail[0]);
        self::checkPage($detail, '공고');
        foreach (['학습 계획의 구체성', '지원 동기', '금융교육 과정을 마쳤습니다', '교재 3권', '심사에 쓰지 않는 것', '주민등록번호, 계좌번호, 주소는 받지 않습니다',
            '만 19세 미만이면 보호자', '/scholarship/apply/' . $program['public_id']] as $text) {
            T::ok(strpos($detail[2], $text) !== false, '공고에 없다: ' . $text);
        }
        T::ok(strpos($this->site('GET', '/scholarship/criteria')[2], '학습 계획의 구체성') !== false);
        // 신청서는 로그인해야 쓸 수 있다.
        $response = $this->site('GET', '/scholarship/apply/' . $program['public_id']);
        T::same('/login?next=' . rawurlencode('/scholarship/apply/' . $program['public_id']), self::location($response));
        T::ok(strpos($this->site('GET', '/login', null, [], ['next' => '/scholarship/apply/' . $program['public_id']])[2],
            'value="/scholarship/apply/' . $program['public_id'] . '"') !== false);
    }

    private function applyForm(array $changes = [])
    {
        return $changes + ['name' => '홍길동', 'birth_year' => '2004', 'school_level' => 'university', 'school_name' => '테스트대학교',
            'email' => 'student@example.com', 'phone' => '010-0000-0000', 'motivation' => str_repeat('금융을 제대로 배우고 싶습니다. ', 5),
            'plan' => str_repeat('매주 두 번 공부 모임을 엽니다. ', 5), 'privacy' => '1'];
    }

    public function testApplicationIsMadeOnTheSiteAndNeverAsksForGameResults()
    {
        $this->enableScholarship();
        $program = $this->program([], [['rule_type' => 'statement', 'description' => '금융교육 과정을 마쳤습니다', 'params' => '']]);
        $ruleId = (int)$this->db->val('SELECT id FROM scholarship_eligibility_rules WHERE program_id = ?', [$program['id']]);
        $player = $this->resident();
        $stranger = $this->resident();
        $this->setLevel($player, 40);
        $gc = $this->gc($player);
        $ledger = $this->ledgerRows();
        $session = $this->signIn($player);
        $path = '/scholarship/apply/' . $program['public_id'];

        $form = $this->site('GET', $path, $session[0]);
        T::same(200, $form[0]);
        self::checkPage($form, '신청서');
        T::ok(in_array('Cache-Control: no-store', $form[1], true));
        // 신청서의 칸: 게임 성적·랭킹·게임머니·결제, 주민등록번호·계좌·주소를 묻는 칸은 없다.
        preg_match_all('/name="([^"]+)"/', $form[2], $names);
        foreach (array_unique($names[1]) as $name) {
            T::ok(preg_match('/^(viewport|_csrf|name|birth_year|school_level|school_name|email|phone|motivation|plan|privacy|statement_\d+'
                . '|doc[1-5]_(kind|title|content|url)|guardian_(name|relation|contact))$/', $name) === 1, '신청서에 있으면 안 되는 칸: ' . $name);
        }
        T::ok(strpos($form[2], 'type="file"') === false);

        // 빠진 것이 있으면 받지 않고, 적은 것은 그대로 다시 보여 준다.
        $bad = $this->sitePost($session, $path, $this->applyForm(['email' => 'not-an-email', 'privacy' => '', 'name' => '<b>홍</b>길동']));
        T::same(422, $bad[0]);
        T::ok(strpos($bad[2], '이메일') !== false && strpos($bad[2], '개인정보 수집·이용에 동의') !== false && strpos($bad[2], '확인이 필요합니다') !== false);
        T::ok(strpos($bad[2], 'value="&lt;b&gt;홍&lt;/b&gt;길동"') !== false && strpos($bad[2], '<b>홍') === false);
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_applications'));

        $done = $this->sitePost($session, $path, $this->applyForm(['statement_' . $ruleId => '1', 'doc1_kind' => 'essay', 'doc1_title' => '나의 금융 공부',
            'doc1_content' => '용돈 기입장을 3년 동안 썼습니다.']));
        T::same(200, $done[0]);
        T::ok(strpos($done[2], '신청서를 받았습니다') !== false && strpos($done[2], '보호자 동의가 필요합니다') === false);
        $application = $this->db->one('SELECT * FROM scholarship_applications');
        T::same((int)$player['id'], (int)$application['player_id']);
        T::same(1, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_documents'));
        // 저장된 것은 암호문이다.
        T::ok(strpos($this->db->val('SELECT name_enc FROM scholarship_applicant_profiles'), '홍길동') === false);
        // 같은 프로그램에 두 번 낼 수 없다.
        T::same(409, $this->sitePost($session, $path, $this->applyForm(['statement_' . $ruleId => '1']))[0]);

        // 지원 현황: 내 신청만 보인다.
        $status = $this->site('GET', '/scholarship/status', $session[0]);
        T::ok(strpos($status[2], '/scholarship/status/' . $application['public_id']) !== false && strpos($status[2], '접수됨') !== false);
        $detail = $this->site('GET', '/scholarship/status/' . $application['public_id'], $session[0]);
        T::same(200, $detail[0]);
        T::ok(strpos($detail[2], '자격 확인') !== false && strpos($detail[2], 'aria-current="step"') !== false);
        // 신청서의 개인정보는 현황 화면에 다시 나오지 않는다.
        T::ok(strpos($detail[2], 'student@example.com') === false && strpos($detail[2], '010-0000-0000') === false);
        $other = $this->signIn($stranger);
        T::same(404, $this->site('GET', '/scholarship/status/' . $application['public_id'], $other[0])[0]);
        T::same(404, $this->sitePost($other, '/scholarship/status/' . $application['public_id'] . '/withdraw', ['confirm' => '1'])[0]);
        T::ok(strpos($this->site('GET', '/scholarship/status', $other[0])[2], '낸 신청이 없습니다') !== false);

        // 철회: 확인에 표시해야 한다.
        T::same(422, $this->sitePost($session, '/scholarship/status/' . $application['public_id'] . '/withdraw')[0]);
        T::same(303, $this->sitePost($session, '/scholarship/status/' . $application['public_id'] . '/withdraw', ['confirm' => '1'])[0]);
        T::same('withdrawn', $this->db->val('SELECT status FROM scholarship_applications'));

        // 게임 경제와 게임 기록은 읽지도 바꾸지도 않았다.
        T::same($gc, $this->gc($player));
        T::same($ledger, $this->ledgerRows());
    }

    public function testMinorApplicationWaitsForGuardianConsentThroughAOneTimeLink()
    {
        $this->enableScholarship();
        $program = $this->program();
        $session = $this->signIn($this->resident());
        $path = '/scholarship/apply/' . $program['public_id'];
        $minor = $this->applyForm(['birth_year' => '2011', 'school_level' => 'middle']);

        // 미성년자인데 보호자를 적지 않으면 받지 않는다.
        $bad = $this->sitePost($session, $path, $minor);
        T::same(422, $bad[0]);
        T::ok(strpos($bad[2], '보호자 이름') !== false);

        $done = $this->sitePost($session, $path, $minor + ['guardian_name' => '홍판서', 'guardian_relation' => 'parent', 'guardian_contact' => '010-1111-2222']);
        T::same(200, $done[0]);
        T::ok(preg_match('#https://cg\.test/scholarship/consent/([0-9a-f]{64})#', $done[2], $m) === 1, '보호자에게 전할 주소가 없다');
        $token = $m[1];
        $appId = $this->db->val('SELECT public_id FROM scholarship_applications');
        // 주소는 한 번만 보인다. 서버에는 해시만 있다.
        T::ok(strpos($this->site('GET', '/scholarship/status/' . $appId, $session[0])[2], $token) === false);
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_consents WHERE token_hash = ?', [$token]));

        // 보호자 화면: 로그인 없이 열리고, 저장·검색되지 않는다.
        $page = $this->site('GET', '/scholarship/consent/' . $token);
        T::same(200, $page[0]);
        self::checkPage($page, '보호자 동의');
        T::ok(in_array('Cache-Control: no-store', $page[1], true) && in_array('X-Robots-Tag: noindex, nofollow', $page[1], true));
        T::ok(strpos($page[2], '홍길동') !== false && strpos($page[2], $program['title']) !== false && strpos($page[2], '받는 정보') !== false);
        // 신청자의 연락처나 보호자 연락처는 이 화면에 나오지 않는다.
        T::ok(strpos($page[2], 'student@example.com') === false && strpos($page[2], '010-1111-2222') === false);

        // 이름이 다르거나 동의에 표시하지 않으면 기록되지 않는다.
        T::same(422, $this->site('POST', '/scholarship/consent/' . $token, null, ['guardian_name' => '다른사람', 'agree' => '1'])[0]);
        T::same(422, $this->site('POST', '/scholarship/consent/' . $token, null, ['guardian_name' => '홍판서'])[0]);
        T::same('pending', $this->db->val('SELECT consent_status FROM scholarship_applications'));

        // 새 주소를 만들면 앞의 주소는 쓸 수 없다.
        $again = $this->sitePost($session, '/scholarship/status/' . $appId . '/guardian-link');
        T::same(200, $again[0]);
        T::ok(preg_match('#/scholarship/consent/([0-9a-f]{64})#', $again[2], $m) === 1 && $m[1] !== $token);
        T::same(404, $this->site('GET', '/scholarship/consent/' . $token)[0]);
        $token = $m[1];

        $ok = $this->site('POST', '/scholarship/consent/' . $token, null, ['guardian_name' => ' 홍 판서 ', 'agree' => '1']);
        T::same(200, $ok[0]);
        T::ok(strpos($ok[2], '동의가 기록되었습니다') !== false);
        T::same('guardian_confirmed', $this->db->val('SELECT consent_status FROM scholarship_applications'));
        // 쓴 주소는 다시 열리지 않는다.
        T::same(404, $this->site('GET', '/scholarship/consent/' . $token)[0]);
        T::same(404, $this->site('GET', '/scholarship/consent/' . str_repeat('a', 64))[0]);
        // 담당자가 확인하기 전에는 심사로 넘어가지 않았다.
        T::ok(strpos($this->site('GET', '/scholarship/status/' . $appId, $session[0])[2], '담당자 확인 전') !== false);
        T::same('submitted', $this->db->val('SELECT status FROM scholarship_applications'));
    }

    public function testAppealIsFiledFromTheStatusPage()
    {
        $this->enableScholarship();
        $program = $this->program();
        $session = $this->signIn($this->resident());
        $this->sitePost($session, '/scholarship/apply/' . $program['public_id'], $this->applyForm());
        $row = $this->db->one('SELECT id, public_id FROM scholarship_applications');
        $path = '/scholarship/status/' . $row['public_id'];
        T::ok(strpos($this->site('GET', $path, $session[0])[2], $path . '/appeal') === false, '결정 전에 이의신청 폼이 보인다');

        T::eq([], $this->app->scholarshipOffice()->decideEligibility($this->schManager(), (int)$row['id'], false, '학교급이 맞지 않습니다'));
        $page = $this->site('GET', $path, $session[0])[2];
        T::ok(strpos($page, '학교급이 맞지 않습니다') !== false && strpos($page, $path . '/appeal') !== false);
        $short = $this->sitePost($session, $path . '/appeal', ['reason' => '짧다']);
        T::same(422, $short[0]);
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_appeals'));
        T::same($path . '?done=appealed', self::location($this->sitePost($session, $path . '/appeal', ['reason' => str_repeat('학교급을 잘못 골랐습니다. ', 3)])));
        $page = $this->site('GET', $path, $session[0])[2];
        T::ok(strpos($page, '검토 중') !== false && strpos($page, $path . '/appeal') === false);
    }

    // ── 상점 확인 화면 ─────────────────────────────────────

    public function testMerchantPortalNeedsALoginAndForcesANewPassword()
    {
        $this->enableCityPass();
        $merchantId = $this->merchant();
        list($problems, $temporary) = $this->app->partners()->createMerchantUser($this->passManager(), $merchantId,
            ['login' => 'cafe.staff', 'display_name' => '카운터', 'role' => 'staff', 'branch_id' => '']);
        T::eq([], $problems);

        foreach (['/merchant', '/merchant/history', '/merchant/stats', '/merchant/password'] as $path) {
            T::same('/merchant/login', self::location($this->shop('GET', $path)), $path);
        }
        $login = $this->shop('GET', '/merchant/login');
        T::same(200, $login[0]);
        self::checkPage($login, '상점 로그인');
        T::ok(in_array('X-Robots-Tag: noindex, nofollow', $login[1], true) && in_array('Cache-Control: no-store', $login[1], true));

        T::same(401, $this->shop('POST', '/merchant/login', null, ['login' => 'cafe.staff', 'password' => 'wrong'])[0]);
        T::same(403, $this->shop('POST', '/merchant/login', null, ['login' => 'cafe.staff', 'password' => $temporary], [],
            ['Origin' => 'https://evil.example', 'Host' => 'cg.test'])[0]);
        $response = $this->shop('POST', '/merchant/login', null, ['login' => 'cafe.staff', 'password' => $temporary]);
        T::same('/merchant/password', self::location($response));
        list($token, $flags) = self::cookieOf($response, MerchantPortal::COOKIE);
        T::ok(strpos($flags, 'HttpOnly') !== false && strpos($flags, 'SameSite=Strict') !== false && strpos($flags, 'Secure') !== false
            && strpos($flags, 'Path=/merchant;') !== false);

        // 임시 비밀번호를 바꾸기 전에는 다른 화면을 쓸 수 없다.
        foreach (['/merchant', '/merchant/history', '/merchant/stats', '/merchant/redeem'] as $path) {
            T::same('/merchant/password', self::location($this->shop('GET', $path, $token)), $path);
        }
        $page = $this->shop('GET', '/merchant/password', $token);
        $csrf = self::csrfIn($page[2]);
        T::same('/merchant/password', self::location($this->shop('POST', '/merchant/verify', $token, ['_csrf' => $csrf, 'code' => 'AAAAA-BBBBB'])));
        T::same(403, $this->shop('POST', '/merchant/password', $token, ['current' => $temporary, 'new' => self::PASSWORD, 'again' => self::PASSWORD])[0]);
        T::same(422, $this->shop('POST', '/merchant/password', $token, ['_csrf' => $csrf, 'current' => $temporary, 'new' => 'short', 'again' => 'short'])[0]);
        $changed = $this->shop('POST', '/merchant/password', $token, ['_csrf' => $csrf, 'current' => $temporary, 'new' => self::PASSWORD, 'again' => self::PASSWORD]);
        T::same('/merchant?done=password', self::location($changed));
        $home = $this->shop('GET', '/merchant', $token, [], ['done' => 'password']);
        T::same(200, $home[0]);
        self::checkPage($home, '상점 첫 화면');
        T::ok(strpos($home[2], '비밀번호를 바꿨습니다') !== false && strpos($home[2], 'action="/merchant/verify"') !== false);

        // 로그아웃하면 그 쿠키로는 들어올 수 없다.
        T::same(303, $this->shop('POST', '/merchant/logout', $token, ['_csrf' => $csrf])[0]);
        T::same('/merchant/login', self::location($this->shop('GET', '/merchant', $token)));
        T::same(404, $this->shop('GET', '/merchant/none', $this->shopSignIn($merchantId, 'cafe.second')[0])[0]);
    }

    public function testAQrLinkOnlyFillsInTheCodeAndRedeemingNeedsTheStaffToConfirm()
    {
        $this->enableCityPass();
        $merchantId = $this->merchant();
        $offer = $this->offer($merchantId);
        $player = $this->resident('11110', '비밀닉네임');
        $profile = $this->member($player);
        list($couponId, $code) = $this->couponWithCode($player, $offer);
        $session = $this->shopSignIn($merchantId, 'cafe.owner');

        // QR 에 들어 있는 주소: 개인정보가 없다.
        $shown = $this->app->cityPass()->present($player, $couponId);
        $code = $shown['code'];
        T::same('https://cg.test/merchant/redeem?code=' . $code, $shown['qrPayload']);
        T::ok(strpos($shown['qrPayload'], $profile['memberNo']) === false && strpos($shown['qrPayload'], (string)$player['id']) === false);

        // 카메라 앱에서 열면 쿠키가 오지 않는다: 같은 사이트 안의 링크를 한 번 거친다.
        $first = $this->shop('GET', '/merchant/redeem', null, [], ['code' => $code]);
        T::same(200, $first[0]);
        T::ok(strpos($first[2], 'href="/merchant/redeem?code=' . $code . '&amp;go=1"') !== false);
        // 그래도 접속이 없으면 코드를 들고 로그인으로 간다. 로그인하면 그 코드가 채워진 화면으로 온다.
        T::same('/merchant/login?code=' . $code, self::location($this->shop('GET', '/merchant/redeem', null, [], ['code' => $code, 'go' => '1'])));
        T::ok(strpos($this->shop('GET', '/merchant/login', null, [], ['code' => $code])[2], 'name="code" value="' . $code . '"') !== false);
        T::same('/merchant/redeem?code=' . $code, self::location(
            $this->shop('POST', '/merchant/login', null, ['login' => 'cafe.owner', 'password' => self::PASSWORD, 'code' => $code])));
        // 모양이 맞지 않는 코드는 버린다.
        T::same('/merchant/login', self::location($this->shop('GET', '/merchant/redeem', null, [], ['code' => '"><script>alert(1)</script>'])));

        // 주소를 여는 것만으로는 아무 일도 일어나지 않는다.
        $events = (int)$this->db->val('SELECT COUNT(*) FROM coupon_events');
        $form = $this->shop('GET', '/merchant/redeem', $session[0], [], ['code' => $code]);
        T::same(200, $form[0]);
        T::ok(strpos($form[2], 'name="code" value="' . $code . '"') !== false);
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));
        T::same($events, (int)$this->db->val('SELECT COUNT(*) FROM coupon_events'));

        // 확인: 혜택과 조건이 보이고, 쿠폰은 아직 쓰이지 않았다.
        $verify = $this->shopPost($session, '/merchant/verify', ['code' => $code]);
        T::same(200, $verify[0]);
        self::checkPage($verify, '쿠폰 확인');
        T::ok(strpos($verify[2], '음료 10% 할인') !== false && strpos($verify[2], '1회 1잔') !== false && strpos($verify[2], 'action="/merchant/redeem"') !== false);
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));
        // 손님이 누구인지는 어느 화면에도 나오지 않는다.
        foreach ([$verify[2], $form[2]] as $html) {
            T::ok(strpos($html, '비밀닉네임') === false && strpos($html, $profile['memberNo']) === false);
        }

        // 사용 처리는 CSRF 토큰이 있는 POST 로만 된다.
        T::same(403, $this->shop('POST', '/merchant/redeem', $session[0], ['code' => $code])[0]);
        T::same(403, $this->shop('POST', '/merchant/redeem', $session[0], ['code' => $code, '_csrf' => $session[1]], [],
            ['Origin' => 'https://evil.example', 'Host' => 'cg.test'])[0]);
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));

        $redeemed = $this->shopPost($session, '/merchant/redeem', ['code' => $code]);
        T::same(200, $redeemed[0]);
        T::ok(strpos($redeemed[2], '사용 처리했습니다') !== false && strpos($redeemed[2], '비밀닉네임') === false);
        T::same('redeemed', $this->db->val('SELECT status FROM benefit_coupons'));
        // 같은 쿠폰은 다시 쓸 수 없다.
        T::same(404, $this->shopPost($session, '/merchant/redeem', ['code' => $code])[0]);
        T::same(404, $this->shopPost($session, '/merchant/verify', ['code' => $code])[0]);
        T::same(1, (int)$this->db->val('SELECT COUNT(*) FROM coupon_redemptions'));

        $history = $this->shop('GET', '/merchant/history', $session[0]);
        T::ok(strpos($history[2], '음료 10% 할인') !== false && strpos($history[2], '비밀닉네임') === false && strpos($history[2], $profile['memberNo']) === false);
        $stats = $this->shop('GET', '/merchant/stats', $session[0]);
        T::same(200, $stats[0]);
        T::ok(strpos($stats[2], '음료 10% 할인') !== false);
    }

    public function testAMerchantCannotSeeOrRedeemAnotherMerchantsCoupons()
    {
        $this->enableCityPass();
        $mine = $this->merchant(['name' => '우리 카페']);
        $theirs = $this->merchant(['name' => '남의 서점', 'category' => 'retail']);
        $offer = $this->offer($theirs, ['title' => '도서 5% 할인', 'discount_value' => '5']);
        $player = $this->resident();
        $this->member($player);
        list(, $code) = $this->couponWithCode($player, $offer);
        $session = $this->shopSignIn($mine, 'our.owner');

        // 남의 쿠폰은 모르는 코드와 똑같이 답한다 (있는지 없는지도 알려 주지 않는다).
        $unknown = $this->shopPost($session, '/merchant/verify', ['code' => 'ZZZZZ-ZZZZZ']);
        $foreign = $this->shopPost($session, '/merchant/verify', ['code' => $code]);
        T::same(404, $foreign[0]);
        T::same(404, $unknown[0]);
        T::ok(strpos($foreign[2], '쓸 수 있는 쿠폰을 찾지 못했습니다') !== false && strpos($unknown[2], '쓸 수 있는 쿠폰을 찾지 못했습니다') !== false);
        T::ok(strpos($foreign[2], '도서 5% 할인') === false && strpos($foreign[2], '남의 서점') === false);
        T::same(404, $this->shopPost($session, '/merchant/redeem', ['code' => $code])[0]);
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));

        // 그 쿠폰의 주인 업체가 쓰면, 우리 화면의 내역과 통계에는 나오지 않는다.
        $owner = $this->shopSignIn($theirs, 'their.owner');
        T::same(200, $this->shopPost($owner, '/merchant/redeem', ['code' => $code])[0]);
        foreach (['/merchant/history', '/merchant/stats', '/merchant'] as $path) {
            $html = $this->shop('GET', $path, $session[0])[2];
            T::ok(strpos($html, '도서 5% 할인') === false && strpos($html, '남의 서점') === false, $path);
        }
        T::ok(strpos($this->shop('GET', '/merchant/history', $owner[0])[2], '도서 5% 할인') !== false);

        // 제휴가 멈춘 업체는 쿠폰을 처리할 수 없다.
        T::eq([], $this->app->partners()->merchantTransition($this->passManager(), $mine, 'suspend', '계약 확인 중'));
        T::ok(strpos($this->shop('GET', '/merchant', $session[0])[2], '지금은 쿠폰을 처리할 수 없는 상태입니다') !== false);
        T::same(403, $this->shopPost($session, '/merchant/verify', ['code' => $code])[0]);
    }

    public function testMerchantPortalStopsWorkingWhenTheCouponFeatureIsTurnedOff()
    {
        $this->enableCityPass();
        $merchantId = $this->merchant();
        $offer = $this->offer($merchantId);
        $player = $this->resident();
        $this->member($player);
        list(, $code) = $this->couponWithCode($player, $offer);
        $session = $this->shopSignIn($merchantId, 'cafe.owner');

        T::eq([], $this->app->features()->disable($this->compliance(), \CG\Service\Features::PASS_COUPONS, '법률 검토 다시 필요'));
        $response = $this->shopPost($session, '/merchant/redeem', ['code' => $code]);
        T::same(403, $response[0]);
        T::ok(strpos($response[2], '준비 중') !== false);
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));
        // 홈페이지에서도 바로 내려간다.
        T::ok(strpos($this->site('GET', '/city-pass/offers')[2], '음료 10% 할인') === false);
    }
}
