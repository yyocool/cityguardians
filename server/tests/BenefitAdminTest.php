<?php
use CG\Admin\Auth;
use CG\Admin\BenefitPages;
use CG\Admin\Kernel;
use CG\Admin\Pages;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Infra\Totp;
use CG\Service\Features;

/**
 * 웹 관리자의 현실 혜택 화면: 업무별 권한, 기능 승인, CITY PASS 관리, 장학사업 관리 (docs/ADMIN_SYSTEM.md 11장).
 */
final class BenefitAdminTest extends BenefitTestCase
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

    private function finance()
    {
        return $this->admin('finance1', Auth::ROLE_FINANCE);
    }

    // ── 업무별 권한 ─────────────────────────────────────────

    public function testEveryBenefitScreenNeedsASession()
    {
        foreach (BenefitPages::menu() as $item) {
            $response = $this->send('GET', $item[0]);
            T::same(303, $response[0], $item[0]);
            T::same('/admin/login', self::location($response));
        }
    }

    public function testEachRoleSeesOnlyTheScreensOfItsDuty()
    {
        $admins = [
            $this->super(), $this->compliance(), $this->passManager(), $this->schManager(), $this->reviewer(1), $this->finance(),
            $this->admin('operator1', Auth::ROLE_OPERATOR), $this->admin('viewer1', Auth::ROLE_VIEWER),
        ];
        foreach ($admins as $admin) {
            list(, , $home) = $this->get($admin, '/admin');
            foreach (BenefitPages::menu() as $item) {
                $allowed = Auth::can($admin, $item[2]);
                list($status, $headers, $html) = $this->get($admin, $item[0]);
                T::same($allowed ? 200 : 403, $status, $admin['role'] . ' ' . $item[0]);
                // 메뉴에는 볼 수 있는 화면만 나온다.
                T::same($allowed, strpos($home, 'href="' . $item[0] . '"') !== false, $admin['role'] . ' 의 메뉴: ' . $item[0]);
                T::ok(stripos($html, '<script') === false);
                T::ok(preg_match('#(src|href)="(https?:)?//#i', $html) === 0, $item[0] . ' 가 외부 자원을 부른다');
                T::ok(in_array('X-Frame-Options: DENY', $headers, true) && in_array('Cache-Control: no-store', $headers, true)
                    && in_array('X-Robots-Tag: noindex, nofollow', $headers, true));
            }
            // 게임 운영 화면은 게임 운영 역할만 본다.
            $game = Auth::can($admin, Auth::VIEW);
            T::same($game ? 200 : 403, $this->get($admin, '/admin/risk')[0], $admin['role'] . ' /admin/risk');
            T::same($game ? 200 : 403, $this->get($admin, '/admin/defs/enemies')[0], $admin['role']);
            T::same($game, strpos($home, 'href="/admin/risk"') !== false, $admin['role'] . ' 의 메뉴에 게임 운영');
            T::same(Auth::can($admin, Auth::AUDIT) ? 200 : 403, $this->get($admin, '/admin/audit')[0], $admin['role'] . ' /admin/audit');
        }
        // 현실 혜택 역할은 게임 값을 바꾸지 못한다.
        foreach ([$this->passManager(), $this->schManager(), $this->compliance(), $this->reviewer(1), $this->finance()] as $admin) {
            T::same(403, $this->post($admin, '/admin/risk/11110/adjust', ['delta' => '10', 'reason' => '권한 확인'])[0], $admin['role']);
            T::same(403, $this->post($admin, '/admin/policy', ['reason' => '권한 확인'])[0], $admin['role']);
        }
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'risk.adjust'"));
    }

    // ── 기능 승인 ───────────────────────────────────────────

    public function testFeatureIsEnabledOnlyThroughLegalRecordAndASecondAdmin()
    {
        $code = Features::PASS_MEMBERSHIP;
        $legal = ['legal_status' => 'approved', 'legal_reference' => 'LEGAL-2026-001', 'legal_reviewer' => '테스트 법무법인',
            'legal_reviewed_on' => '2026-11-10', 'legal_note' => '', 'reason' => '검토 의견서 접수'];
        list($status, , $html) = $this->get($this->super(), '/admin/benefits');
        T::same(200, $status);
        T::ok(strpos($html, '게임머니(GC)를 현실 상품으로 교환') !== false, '만들지 않은 기능이 화면에 보인다');
        T::eq(4, substr_count($html, '○ 꺼짐'));

        // 검토 기록 없이 켤 수 없다.
        list($status, , $html) = $this->post($this->super(), "/admin/benefits/$code/enable", ['reason' => '켜 본다']);
        T::same(422, $status);
        T::ok(strpos($html, '법률 검토가 완료로 기록되어 있지 않습니다') !== false);
        // 담당이 아닌 역할은 기록하지도 켜지도 못한다.
        foreach ([$this->passManager(), $this->schManager(), $this->admin('operator1', Auth::ROLE_OPERATOR)] as $admin) {
            T::same(403, $this->post($admin, "/admin/benefits/$code/legal", $legal)[0], $admin['role']);
            T::same(403, $this->post($admin, "/admin/benefits/$code/enable", ['reason' => '켜 본다'])[0], $admin['role']);
        }
        // 문서 번호 없이 "완료"로 기록할 수 없다.
        T::same(422, $this->post($this->compliance(), "/admin/benefits/$code/legal", ['legal_reference' => ''] + $legal)[0]);
        // CSRF 토큰이 없으면 받지 않는다.
        T::same(403, $this->send('POST', "/admin/benefits/$code/legal", $this->signIn($this->compliance())[0], $legal)[0]);
        T::eq('not_reviewed', $this->db->val('SELECT legal_status FROM benefit_features WHERE code = ?', [$code]));

        $response = $this->post($this->compliance(), "/admin/benefits/$code/legal", $legal);
        T::same(303, $response[0]);
        T::same('/admin/benefits?done=recorded', self::location($response));
        // 준법 담당은 켤 수 없다.
        T::same(403, $this->post($this->compliance(), "/admin/benefits/$code/enable", ['reason' => '켠다'])[0]);
        T::ok(!$this->app->features()->isEnabled($code));
        // 사유 없이는 켤 수 없다.
        T::same(422, $this->post($this->super(), "/admin/benefits/$code/enable", ['reason' => ''])[0]);
        T::same(303, $this->post($this->super(), "/admin/benefits/$code/enable", ['reason' => '운영 준비 완료'])[0]);
        $this->app->features()->forget();
        T::ok($this->app->features()->isEnabled($code));
        list(, , $html) = $this->get($this->compliance(), '/admin/benefits');
        T::eq(1, substr_count($html, '● 켜짐'));
        T::ok(strpos($html, 'LEGAL-2026-001') !== false && strpos($html, '운영 준비 완료') !== false, '이력이 보인다');
        // 준법 담당은 언제든 끌 수 있다.
        T::same(303, $this->post($this->compliance(), "/admin/benefits/$code/disable", ['reason' => '제휴 계약 재검토'])[0]);
        $this->app->features()->forget();
        T::ok(!$this->app->features()->isEnabled($code));
        // 없는 기능 (만들지 않은 기능) 은 켤 주소가 없다.
        T::same(404, $this->post($this->super(), '/admin/benefits/gc_exchange/enable', ['reason' => '켜 본다'])[0]);
        T::eq(4, (int)$this->db->val('SELECT COUNT(*) FROM benefit_features'));

        // 한 사람이 기록하고 켤 수는 없다.
        T::same(303, $this->post($this->super(), '/admin/benefits/' . Features::PASS_COUPONS . '/legal', $legal)[0]);
        list($status, , $html) = $this->post($this->super(), '/admin/benefits/' . Features::PASS_COUPONS . '/enable', ['reason' => '혼자 켠다']);
        T::same(422, $status);
        T::ok(strpos($html, '기록한 사람은 그 기능을 켤 수 없습니다') !== false);
    }

    // ── CITY PASS ───────────────────────────────────────────

    public function testPartnerIsRegisteredApprovedAndShownOnlyByMemberNumber()
    {
        $this->enableCityPass();
        $manager = $this->passManager();
        // 이름에 넣은 꺾쇠는 글자로 보인다.
        $response = $this->post($manager, '/admin/pass/merchants', $this->merchantInput(['name' => '<b>카페</b> "하나"']));
        T::same(303, $response[0]);
        T::ok(preg_match('#^/admin/pass/merchants/(\d+)\?done=saved$#', self::location($response), $m) === 1);
        $path = '/admin/pass/merchants/' . $m[1];
        list($status, , $html) = $this->get($manager, $path);
        T::same(200, $status);
        T::ok(strpos($html, '&lt;b&gt;카페&lt;/b&gt; &quot;하나&quot;') !== false && strpos($html, '<b>카페</b>') === false, '이스케이프되지 않았다');

        // 지점 없이, 그리고 등록한 사람이 승인할 수 없다.
        T::same(303, $this->post($manager, $path . '/transition', ['action' => 'submit', 'reason' => '승인 요청'])[0]);
        list($status, , $html) = $this->post($manager, $path . '/transition', ['action' => 'approve', 'reason' => '내가 승인']);
        T::same(422, $status);
        T::ok(strpos($html, '운영 중인 지점이 하나도 없습니다') !== false && strpos($html, '다른 관리자가 승인해야 합니다') !== false);
        T::same(422, $this->post($manager, $path . '/branches', $this->branchInput(['region_code' => '99999']))[0]);
        T::same(303, $this->post($manager, $path . '/branches', $this->branchInput())[0]);
        // 준법 담당은 보고 승인할 수 있지만 등록·수정은 못 한다.
        T::same(403, $this->post($this->compliance(), $path, $this->merchantInput())[0]);
        T::same(403, $this->post($this->compliance(), $path . '/branches', $this->branchInput())[0]);
        T::same(303, $this->post($this->compliance(), $path . '/transition', ['action' => 'approve', 'reason' => '계약서 확인'])[0]);
        T::eq('approved', $this->db->val('SELECT status FROM partner_merchants WHERE id = ?', [$m[1]]));
        // 승인된 업체는 그대로 고칠 수 없다.
        T::same(422, $this->post($manager, $path, $this->merchantInput(['contract_reference' => '']))[0]);
        // 장학 담당·심사위원은 CITY PASS 를 볼 수 없다.
        foreach ([$this->schManager(), $this->reviewer(1), $this->finance()] as $admin) {
            T::same(403, $this->get($admin, $path)[0], $admin['role']);
        }

        // 혜택: 등록 → 승인 요청 → 다른 관리자의 승인
        list($status, , $html) = $this->get($manager, $path . '/offers/new');
        T::same(200, $status);
        T::ok(strpos($html, 'battle') === false && strpos($html, '전투 성적') === false, '발급 조건에 게임 성적이 없다');
        T::same(422, $this->post($manager, $path . '/offers', $this->offerInput(['issue_policy' => 'battle_reward']))[0]);
        $response = $this->post($manager, $path . '/offers', $this->offerInput());
        T::same(303, $response[0]);
        T::ok(preg_match('#^/admin/pass/offers/(\d+)\?done=saved$#', self::location($response), $o) === 1);
        $offerPath = '/admin/pass/offers/' . $o[1];
        T::same(303, $this->post($manager, $offerPath . '/transition', ['action' => 'submit', 'reason' => '승인 요청'])[0]);
        T::same(422, $this->post($manager, $offerPath . '/transition', ['action' => 'approve', 'reason' => '내가 승인'])[0]);
        T::same(303, $this->post($this->super(), $offerPath . '/transition', ['action' => 'approve', 'reason' => '조건 확인'])[0]);
        T::same(200, $this->get($this->compliance(), $offerPath)[0]);

        // 상점 계정: 임시 비밀번호는 만든 화면에서 한 번만 보인다.
        list($status, , $html) = $this->post($manager, $path . '/users', ['login' => 'cafe.one', 'display_name' => '점주', 'role' => 'owner', 'branch_id' => '']);
        T::same(200, $status);
        T::ok(preg_match('#임시 비밀번호: <code>([^<]+)</code>#', $html, $p) === 1);
        list(, , $html) = $this->get($manager, $path);
        T::ok(strpos($html, $p[1]) === false, '임시 비밀번호가 다시 보인다');
        T::ok(strpos(json_encode($this->db->all('SELECT * FROM merchant_users')), $p[1]) === false, '임시 비밀번호가 그대로 저장되었다');
        T::ok($this->app->merchantPortal()->login('cafe.one', $p[1], '127.0.0.1') !== null);
        $userId = (int)$this->db->val("SELECT id FROM merchant_users WHERE login = 'cafe.one'");
        T::same(403, $this->post($this->compliance(), "/admin/pass/users/$userId/reset")[0]);
        T::same(303, $this->post($manager, "/admin/pass/users/$userId/deactivate")[0]);
        T::eq(null, $this->app->merchantPortal()->login('cafe.one', $p[1], '127.0.0.1'));

        // 쿠폰 목록: 회원번호만 보이고 닉네임은 보이지 않는다.
        $player = $this->resident('11110', '비밀닉네임');
        $profile = $this->member($player);
        $offer = $this->db->one('SELECT * FROM partner_offers WHERE id = ?', [$o[1]]);
        $this->app->cityPass()->claim($player, $offer['public_id'], $this->key());
        list($status, , $html) = $this->get($manager, '/admin/pass/coupons');
        T::same(200, $status);
        T::ok(strpos($html, $profile['memberNo']) !== false);
        T::ok(strpos($html, '비밀닉네임') === false, '쿠폰 목록에 닉네임이 보인다');
        // 취소: 사유가 있어야 하고, 준법 담당은 못 한다.
        $couponId = (int)$this->db->val('SELECT id FROM benefit_coupons');
        T::same(422, $this->post($manager, "/admin/pass/coupons/$couponId/cancel", ['reason' => ''])[0]);
        T::same(403, $this->post($this->compliance(), "/admin/pass/coupons/$couponId/cancel", ['reason' => '잘못 발급'])[0]);
        T::same(303, $this->post($manager, "/admin/pass/coupons/$couponId/cancel", ['reason' => '잘못 발급'])[0]);
        T::eq('cancelled', $this->db->val('SELECT status FROM benefit_coupons'));
        foreach (['/admin/pass/stats', '/admin/pass/fraud', '/admin/pass/redemptions', '/admin/pass/offers', '/admin/pass/merchants'] as $screen) {
            list($status, , $html) = $this->get($manager, $screen);
            T::same(200, $status, $screen);
            T::ok(strpos($html, '비밀닉네임') === false, $screen);
        }
        // 관리자 화면에는 쿠폰을 "사용 처리"하는 길이 없다.
        T::same(404, $this->post($manager, "/admin/pass/coupons/$couponId/redeem", ['reason' => '대신 처리'])[0]);
        T::ok((int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action LIKE 'pass.%'") >= 8, '변경이 감사 기록에 남는다');
    }

    // ── 장학 ────────────────────────────────────────────────

    public function testScholarshipRunsThroughTheScreensWithSeparatedDuties()
    {
        $this->enableScholarship();
        $manager = $this->schManager();
        $base = '/admin/scholarship';
        $reviewers = [$this->reviewer(1), $this->reviewer(2)];

        // 프로그램: 만들기 → 기준·심사위원 → 승인 요청 → 다른 관리자의 공고 승인
        $response = $this->post($manager, "$base/programs", $this->programInput(['capacity' => '1']));
        T::same(303, $response[0]);
        T::ok(preg_match('#/programs/(\d+)\?done=saved$#', self::location($response), $m) === 1);
        $program = "$base/programs/{$m[1]}";
        T::same(422, $this->post($manager, "$program/criteria", ['name' => '도시 랭킹 순위', 'description' => '', 'max_score' => '10', 'weight' => '1'])[0]);
        T::same(422, $this->post($manager, "$program/rules", ['rule_type' => 'gc_balance', 'description' => 'GC 가 많은 사람', 'min' => '1000'])[0]);
        T::same(303, $this->post($manager, "$program/criteria", ['name' => '학습 계획의 구체성', 'description' => '', 'max_score' => '10', 'weight' => '2'])[0]);
        T::same(303, $this->post($manager, "$program/criteria", ['name' => '지원 동기', 'description' => '', 'max_score' => '5', 'weight' => '1'])[0]);
        foreach ($reviewers as $reviewer) {
            T::same(303, $this->post($manager, "$program/reviewers", ['admin_id' => (string)$reviewer['id'], 'active' => '1'])[0]);
        }
        T::same(303, $this->post($manager, "$program/transition", ['action' => 'submit', 'reason' => '공고 승인 요청'])[0]);
        T::same(422, $this->post($manager, "$program/transition", ['action' => 'publish', 'reason' => '내가 공고'])[0]);
        T::same(403, $this->post($this->compliance(), "$program/transition", ['action' => 'publish', 'reason' => '공고'])[0]);
        T::same(303, $this->post($this->super(), "$program/transition", ['action' => 'publish', 'reason' => '내용 확인'])[0]);
        list($status, , $html) = $this->get($this->compliance(), $program);
        T::same(200, $status);
        T::ok(strpos($html, '<form method="post"') === strrpos($html, '<form method="post"'), '준법 담당의 화면에는 로그아웃 말고 바꾸는 폼이 없다');

        // 신청 (홈페이지에서 들어오는 길과 같은 서비스)
        $first = $this->resident('11110', '게임닉네임');
        $second = $this->resident('11110', '둘째닉네임');
        $publicId = $this->db->val('SELECT public_id FROM scholarship_programs WHERE id = ?', [$m[1]]);
        $a = $this->applicationId($this->app->scholarships()->apply($first, $publicId, $this->applicationInput())['application']['id']);
        $b = $this->applicationId($this->app->scholarships()->apply($second, $publicId, $this->applicationInput(['name' => '김둘째', 'email' => 'second@example.com']))['application']['id']);

        // 신청 목록과 신청서: 이름·연락처·닉네임이 없다.
        list($status, , $html) = $this->get($manager, "$program/applications");
        T::same(200, $status);
        foreach (['홍길동', '김둘째', 'student@example.com', '게임닉네임'] as $hidden) {
            T::ok(strpos($html, $hidden) === false, '신청 목록에 보임: ' . $hidden);
        }
        list($status, , $html) = $this->get($manager, "$base/applications/$a");
        T::same(200, $status);
        T::ok(strpos($html, '금융을 제대로 배우고 싶습니다') !== false);
        foreach (['홍길동', 'student@example.com', '010-0000-0000', '테스트대학교', '게임닉네임'] as $hidden) {
            T::ok(strpos($html, $hidden) === false, '신청서 화면에 보임: ' . $hidden);
        }
        // 개인정보: 사유를 적어야 열리고, 열면 기록된다.
        list($status, , $html) = $this->post($manager, "$base/applications/$a/pii", ['reason' => '']);
        T::same(422, $status);
        T::ok(strpos($html, '홍길동') === false);
        list($status, , $html) = $this->post($manager, "$base/applications/$a/pii", ['reason' => '결과 안내 메일 주소 확인']);
        T::same(200, $status);
        T::ok(strpos($html, '홍길동') !== false && strpos($html, 'student@example.com') !== false);
        T::ok(strpos($html, '게임닉네임') === false, '개인정보 화면에도 게임 닉네임은 없다');
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM scholarship_audit_logs WHERE action = 'pii.view' AND detail = '결과 안내 메일 주소 확인'"));
        // 다른 역할은 신청서도 개인정보도 열 수 없다.
        foreach ([$this->compliance(), $this->finance(), $reviewers[0], $this->passManager()] as $admin) {
            T::same(403, $this->get($admin, "$base/applications/$a")[0], $admin['role']);
            list($status, , $html) = $this->post($admin, "$base/applications/$a/pii", ['reason' => '열어 본다']);
            T::same(403, $status, $admin['role']);
            T::ok(strpos($html, '홍길동') === false);
        }
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM scholarship_audit_logs WHERE action = 'pii.view'"));

        // 자격 확인 → 심사위원에게 넘어간다.
        T::same(422, $this->post($manager, "$base/applications/$a/eligibility", ['eligible' => '1', 'note' => ''])[0]);
        T::same(303, $this->post($manager, "$base/applications/$a/eligibility", ['eligible' => '1', 'note' => '서류 확인'])[0]);
        T::same(303, $this->post($manager, "$base/applications/$b/eligibility", ['eligible' => '1', 'note' => '서류 확인'])[0]);

        // 심사위원: 내 심사만, 이름 없이.
        $scores = [$a => ['9', '5'], $b => ['6', '3']];
        $criteria = array_column($this->db->all('SELECT id FROM scholarship_criteria WHERE program_id = ? ORDER BY id', [$m[1]]), 'id');
        foreach ($reviewers as $index => $reviewer) {
            list($status, , $html) = $this->get($reviewer, "$base/reviews");
            T::same(200, $status);
            T::eq(2, preg_match_all("#href=\"$base/reviews/(\\d+)\"#", $html, $links));
            foreach ($this->db->all('SELECT id, application_id FROM scholarship_reviews WHERE admin_id = ? ORDER BY id', [$reviewer['id']]) as $review) {
                list($status, , $html) = $this->get($reviewer, "$base/reviews/{$review['id']}");
                T::same(200, $status);
                foreach (['홍길동', '김둘째', 'example.com', '010-0000-0000', '테스트대학교', '닉네임'] as $hidden) {
                    T::ok(strpos($html, $hidden) === false, '심사 화면에 보임: ' . $hidden);
                }
                // 남의 심사는 없는 주소다.
                $other = $reviewers[1 - $index];
                T::same(404, $this->get($other, "$base/reviews/{$review['id']}")[0]);
                T::same(404, $this->post($other, "$base/reviews/{$review['id']}", ['comment' => '남의 심사에 점수를 넣어 본다'])[0]);
                $form = ['score_' . $criteria[0] => $scores[$review['application_id']][0], 'score_' . $criteria[1] => $scores[$review['application_id']][1],
                    'comment' => '계획이 구체적이고 실행할 수 있어 보입니다.'];
                T::same(422, $this->post($reviewer, "$base/reviews/{$review['id']}", ['score_' . $criteria[0] => '99'] + $form)[0]);
                T::same(303, $this->post($reviewer, "$base/reviews/{$review['id']}", $form)[0]);
                T::same(422, $this->post($reviewer, "$base/reviews/{$review['id']}", $form)[0], '낸 평가는 고칠 수 없다');
            }
            // 심사위원은 프로그램·신청 목록·결과·지급·로그를 볼 수 없다.
            foreach (["$base/programs", $program, "$program/applications", "$program/results", "$base/disbursements", "$base/audit", "$base/appeals"] as $screen) {
                T::same(403, $this->get($reviewer, $screen)[0], $screen);
            }
        }
        // 최고 관리자도 심사 화면은 쓸 수 없다 (점수를 넣을 수 없다).
        T::same(403, $this->get($this->super(), "$base/reviews")[0]);

        // 마감 → 선정 제안 → 다른 관리자의 최종 승인 → 알림
        Clock::advance(15 * 86400);
        $this->sessions = [];
        T::same(303, $this->post($manager, "$program/transition", ['action' => 'close', 'reason' => '접수 마감'])[0]);
        list($status, , $html) = $this->get($manager, "$program/results");
        T::same(200, $status);
        T::ok(strpos($html, '93.33') !== false && strpos($html, '60.00') !== false, '평균 점수');
        T::ok(strpos($html, '홍길동') === false && strpos($html, '닉네임') === false);
        T::same(422, $this->post($manager, "$program/results/propose", ["decision_$a" => 'selected', "decision_$b" => 'selected', 'note' => '둘 다'])[0], '정원을 넘는다');
        T::same(403, $this->post($this->compliance(), "$program/results/propose", ["decision_$a" => 'selected', "decision_$b" => 'not_selected', 'note' => '점수 순'])[0]);
        T::same(303, $this->post($manager, "$program/results/propose", ["decision_$a" => 'selected', "decision_$b" => 'not_selected', 'note' => '심사 점수 순'])[0]);
        T::same(422, $this->post($manager, "$program/results/approve", ['reason' => '내가 승인'])[0], '제안한 사람은 승인할 수 없다');
        T::same(403, $this->post($this->finance(), "$program/results/approve", ['reason' => '승인'])[0]);
        T::same(303, $this->post($this->super(), "$program/results/approve", ['reason' => '심사 결과와 정원 확인'])[0]);
        T::eq(null, $this->app->scholarships()->myApplications($first)['applications'][0]['result'], '알리기 전에는 보이지 않는다');
        T::same(303, $this->post($this->super(), "$program/results/announce", ['reason' => '결과 발표'])[0]);
        T::eq('selected', $this->app->scholarships()->myApplications($first)['applications'][0]['result']);

        // 지급 상태: 지급 담당만, 이름 없이, 두 사람이.
        $finance = $this->finance();
        list($status, , $html) = $this->get($finance, "$base/disbursements");
        T::same(200, $status);
        T::ok(strpos($html, '이 화면은 돈을 보내지 않습니다') !== false);
        foreach (['홍길동', 'example.com', '닉네임'] as $hidden) {
            T::ok(strpos($html, $hidden) === false, '지급 화면에 보임: ' . $hidden);
        }
        T::ok(preg_match('/name="(account|bank|account_no|holder)[^"]*"/', $html) === 0, '계좌를 적는 칸이 있다');
        $record = ['application_id' => (string)$a, 'support_type' => 'books', 'description' => '금융 교재 3권', 'amount_krw' => '45000'];
        T::same(403, $this->post($manager, "$base/disbursements", $record)[0]);
        T::same(422, $this->post($finance, "$base/disbursements", ['application_id' => (string)$b] + $record)[0], '선정되지 않은 신청');
        T::same(303, $this->post($finance, "$base/disbursements", $record)[0]);
        $recordId = (int)$this->db->val('SELECT id FROM scholarship_disbursement_records');
        T::same(422, $this->post($finance, "$base/disbursements/$recordId/approve", ['note' => '내가 승인'])[0]);
        T::same(303, $this->post($this->super(), "$base/disbursements/$recordId/approve", ['note' => '금액 확인'])[0]);
        T::same(422, $this->post($finance, "$base/disbursements/$recordId/complete", ['note' => '교재 발송', 'external_reference' => ''])[0]);
        T::same(303, $this->post($finance, "$base/disbursements/$recordId/complete", ['note' => '교재 발송', 'external_reference' => 'PAY-2026-0001'])[0]);
        T::eq('completed', $this->db->val('SELECT status FROM scholarship_disbursement_records'));
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE reason LIKE '%scholar%' OR reason LIKE '%award%'"));

        // 개인정보 접근 로그: 준법 담당만 본다.
        list($status, , $html) = $this->get($this->compliance(), "$base/audit", ['action' => 'pii.view']);
        T::same(200, $status);
        T::ok(strpos($html, '결과 안내 메일 주소 확인') !== false && strpos($html, 'schmgr') !== false);
        T::ok(strpos($html, '홍길동') === false);
        T::same(403, $this->get($manager, "$base/audit")[0]);
        T::same(403, $this->get($finance, "$base/audit")[0]);
    }

    public function testGuardianConsentAndAppealScreens()
    {
        $this->enableScholarship();
        $manager = $this->schManager();
        $base = '/admin/scholarship';
        $program = $this->program(['capacity' => '1']);
        $player = $this->resident();
        $result = $this->app->scholarships()->apply($player, $program['public_id'], $this->minorInput());
        $id = $this->applicationId($result['application']['id']);

        // 보호자 동의가 확인되기 전에는 심사로 넘길 수 없다.
        list($status, , $html) = $this->post($manager, "$base/applications/$id/eligibility", ['eligible' => '1', 'note' => '서류 확인']);
        T::same(422, $status);
        T::ok(strpos($html, '보호자 동의가 확인되지 않았습니다') !== false);
        T::same(422, $this->post($manager, "$base/applications/$id/consent", ['verify_method' => 'call', 'verify_note' => '통화함'])[0], '보호자가 동의하기 전');
        T::eq([], $this->app->scholarships()->confirmGuardianConsent($result['guardianConsent']['token'], '홍판서', true, '198.51.100.7'));
        list(, , $html) = $this->get($manager, "$base/applications/$id");
        T::ok(strpos($html, '정말 보호자인지 확인하지 못합니다') !== false);
        T::ok(strpos($html, '홍판서') === false, '보호자의 이름도 사유를 적어야 보인다');
        T::same(303, $this->post($manager, "$base/applications/$id/consent", ['verify_method' => 'call', 'verify_note' => '보호자와 통화해 확인'])[0]);
        // 자격에 해당하지 않음 → 이의신청 → 다른 관리자가 처리
        T::same(303, $this->post($manager, "$base/applications/$id/eligibility", ['eligible' => '0', 'note' => '학교급이 맞지 않습니다'])[0]);
        $this->app->scholarships()->appeal($player, $result['application']['id'], '학교급을 잘못 적었습니다. 중학교 재학 증명을 낼 수 있습니다.');
        $appealId = (int)$this->db->val('SELECT id FROM scholarship_appeals');
        list($status, , $html) = $this->get($manager, "$base/appeals/$appealId");
        T::same(200, $status);
        T::ok(strpos($html, '재학 증명') !== false);
        T::same(403, $this->get($this->compliance(), "$base/appeals/$appealId")[0], '사유는 장학 담당만 읽는다');
        T::same(200, $this->get($this->compliance(), "$base/appeals")[0]);
        $handle = ['outcome' => 'upheld', 'response' => '자료를 다시 확인하겠습니다. 접수 상태로 되돌립니다.'];
        T::same(422, $this->post($manager, "$base/appeals/$appealId", $handle)[0], '원래 결정을 내린 사람');
        T::same(303, $this->post($this->super(), "$base/appeals/$appealId", $handle)[0]);
        T::eq('submitted', $this->db->val('SELECT status FROM scholarship_applications'));
        // 사유를 화면에 띄울 때마다 기록된다 (처음 열었을 때, 처리하지 못해 다시 보였을 때).
        T::eq(2, (int)$this->db->val("SELECT COUNT(*) FROM scholarship_audit_logs WHERE action = 'appeal.view' AND actor_id = ?", [$manager['id']]));
    }
}
