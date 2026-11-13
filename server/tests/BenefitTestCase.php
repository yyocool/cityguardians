<?php
use CG\Admin\Auth;
use CG\Infra\Clock;
use CG\Infra\Form;
use CG\Service\Features;

/**
 * v7.0 현실 혜택 테스트의 바탕: 관리자, 기능 승인, 제휴 업체·혜택, 상점 계정, 장학 프로그램을 만드는 도우미.
 *
 * 여기서 만드는 업체와 프로그램은 테스트 DB 안에만 있는 가짜다. 실제 제휴나 실제 장학 프로그램이 아니다.
 */
abstract class BenefitTestCase extends DbTestCase
{
    const PASSWORD = 'Correct-Horse-9';

    private $admins = [];
    private $serial = 0;

    /** 그 역할의 관리자 (없으면 만든다). 서비스에 넘기는 모양 그대로다. */
    protected function admin($login, $role)
    {
        if (!isset($this->admins[$login])) {
            $created = (new Auth($this->app))->create($login, self::PASSWORD, $role);
            $this->admins[$login] = ['id' => $created['id'], 'login' => $login, 'role' => $role, 'totpSecret' => $created['totpSecret']];
        }
        return $this->admins[$login];
    }

    protected function super()
    {
        return $this->admin('root', Auth::ROLE_SUPER);
    }

    protected function compliance()
    {
        return $this->admin('legal', Auth::ROLE_COMPLIANCE);
    }

    protected function passManager()
    {
        return $this->admin('passmgr', Auth::ROLE_PASS);
    }

    protected function schManager()
    {
        return $this->admin('schmgr', Auth::ROLE_SCHOLARSHIP);
    }

    protected function legalInput($status = Features::APPROVED)
    {
        return ['legalStatus' => $status, 'legalReference' => 'LEGAL-TEST-001', 'legalReviewer' => '테스트 법무법인',
            'legalReviewedOn' => '2026-11-10', 'legalNote' => '', 'reason' => '테스트용 검토 기록'];
    }

    /** 승인 절차를 그대로 밟아 기능을 켠다: 준법 담당이 기록하고, 최고 관리자가 켠다. */
    protected function enableFeature($code)
    {
        $features = $this->app->features();
        T::eq([], $features->recordLegal($this->compliance(), $code, $this->legalInput()));
        T::eq([], $features->enable($this->super(), $code, '테스트에서 켬'));
        T::ok($features->isEnabled($code));
    }

    protected function enableCityPass()
    {
        $this->enableFeature(Features::PASS_MEMBERSHIP);
        $this->enableFeature(Features::PASS_COUPONS);
    }

    protected function enableScholarship()
    {
        $this->enableFeature(Features::SCH_APPLICATIONS);
        $this->enableFeature(Features::SCH_AWARDS);
    }

    // ── CITY PASS ───────────────────────────────────────────

    protected function merchantInput(array $changes = [])
    {
        return $changes + ['name' => '테스트 카페 ' . (++$this->serial), 'category' => 'cafe', 'contract_reference' => 'CONTRACT-TEST-' . $this->serial,
            'contract_starts_on' => '2026-11-01', 'contract_ends_on' => '2027-10-31', 'contact_name' => '김담당', 'contact_phone' => '02-000-0000',
            'contact_email' => 'owner@example.com', 'youth_safe_confirmed' => '1'];
    }

    protected function branchInput(array $changes = [])
    {
        return $changes + ['name' => '종로점', 'region_code' => '11110', 'address' => '서울 종로구 테스트로 1', 'phone' => '02-000-0001',
            'lat' => '37.5729', 'lng' => '126.9794', 'image_file' => '', 'is_active' => '1'];
    }

    protected function offerInput(array $changes = [])
    {
        return $changes + ['title' => '음료 10% 할인', 'description' => '모든 음료', 'discount_type' => 'percent', 'discount_value' => '10',
            'conditions' => '1회 1잔, 다른 할인과 함께 쓸 수 없음', 'issue_policy' => 'membership',
            'starts_at' => Form::koreaText(Clock::now() - 3600), 'ends_at' => Form::koreaText(Clock::now() + 30 * 86400),
            'total_limit' => '', 'per_member_limit' => '1', 'coupon_valid_days' => '14', 'branch_id' => ''];
    }

    /** 승인까지 마친 제휴 업체 (지점 하나). @return int 업체 id */
    protected function merchant(array $changes = [], array $branch = [])
    {
        $partners = $this->app->partners();
        list($id, $problems) = $partners->saveMerchant($this->passManager(), null, $this->merchantInput($changes));
        T::eq([], $problems);
        T::eq([], $partners->saveBranch($this->passManager(), $id, null, $this->branchInput($branch)));
        T::eq([], $partners->merchantTransition($this->passManager(), $id, 'submit', '승인 요청'));
        T::eq([], $partners->merchantTransition($this->super(), $id, 'approve', '계약서 확인'));
        return $id;
    }

    protected function branchId($merchantId)
    {
        return (int)$this->db->val('SELECT id FROM partner_branches WHERE merchant_id = ? ORDER BY id LIMIT 1', [$merchantId]);
    }

    /** 승인까지 마친 혜택. @return array 행 */
    protected function offer($merchantId, array $changes = [])
    {
        $partners = $this->app->partners();
        list($id, $problems) = $partners->saveOffer($this->passManager(), $merchantId, null, $this->offerInput($changes));
        T::eq([], $problems);
        T::eq([], $partners->offerTransition($this->passManager(), $id, 'submit', '승인 요청'));
        T::eq([], $partners->offerTransition($this->super(), $id, 'approve', '조건 확인'));
        return $this->db->one('SELECT * FROM partner_offers WHERE id = ?', [$id]);
    }

    /** 비밀번호까지 바꾼 상점 계정. @return array 상점 쪽 서비스에 넘기는 계정 */
    protected function merchantUser($merchantId, $login, $branchId = null, $role = 'owner')
    {
        list($problems, $temporary) = $this->app->partners()->createMerchantUser($this->passManager(), $merchantId,
            ['login' => $login, 'display_name' => '직원 ' . $login, 'role' => $role, 'branch_id' => $branchId !== null ? (string)$branchId : '']);
        T::eq([], $problems);
        $portal = $this->app->merchantPortal();
        list($token, $user) = $portal->login($login, $temporary, '127.0.0.1');
        T::eq([], $portal->changePassword($user, $temporary, self::PASSWORD, self::PASSWORD, $token));
        return $portal->session($token)['user'];
    }

    protected function member(array $player)
    {
        return $this->app->cityPass()->join($player, true);
    }

    /** 쿠폰을 받고, 상점에 보여 줄 코드까지 만든다. @return array [쿠폰 공개 id, 코드] */
    protected function couponWithCode(array $player, array $offer)
    {
        $claimed = $this->app->cityPass()->claim($player, $offer['public_id'], $this->key());
        $shown = $this->app->cityPass()->present($player, $claimed['coupon']['id']);
        return [$claimed['coupon']['id'], $shown['code']];
    }

    // ── 장학 ────────────────────────────────────────────────

    protected function programInput(array $changes = [])
    {
        return $changes + ['category' => 'finance', 'title' => '금융 역량 장학 ' . (++$this->serial), 'summary' => '금융교육을 마친 학생을 지원합니다',
            'description' => '테스트용 프로그램', 'support_type' => 'books', 'support_detail' => '교재 3권', 'capacity' => '2',
            'apply_starts_at' => Form::koreaText(Clock::now() - 3600), 'apply_ends_at' => Form::koreaText(Clock::now() + 14 * 86400),
            'result_on' => '', 'guardian_consent_age' => '19', 'min_reviews' => '2', 'appeal_days' => '7',
            'retention_days_unselected' => '180', 'retention_days_selected' => '1825'];
    }

    protected function reviewer($n)
    {
        return $this->admin('reviewer' . $n, Auth::ROLE_REVIEWER);
    }

    /** 공고까지 마친 프로그램 (평가 기준 둘, 심사위원 둘). @return array 행 */
    protected function program(array $changes = [], array $rules = [])
    {
        $office = $this->app->scholarshipOffice();
        list($id, $problems) = $office->saveProgram($this->schManager(), null, $this->programInput($changes));
        T::eq([], $problems);
        T::eq([], $office->addCriterion($this->schManager(), $id, ['name' => '학습 계획의 구체성', 'description' => '', 'max_score' => '10', 'weight' => '2']));
        T::eq([], $office->addCriterion($this->schManager(), $id, ['name' => '지원 동기', 'description' => '', 'max_score' => '5', 'weight' => '1']));
        foreach ($rules as $rule) {
            T::eq([], $office->addRule($this->schManager(), $id, $rule));
        }
        T::eq([], $office->setReviewer($this->schManager(), $id, $this->reviewer(1)['id'], true));
        T::eq([], $office->setReviewer($this->schManager(), $id, $this->reviewer(2)['id'], true));
        T::eq([], $office->programTransition($this->schManager(), $id, 'submit', '공고 승인 요청'));
        T::eq([], $office->programTransition($this->super(), $id, 'publish', '내용 확인'));
        return $this->db->one('SELECT * FROM scholarship_programs WHERE id = ?', [$id]);
    }

    /** 성인 신청자의 신청서. */
    protected function applicationInput(array $applicant = [], array $more = [])
    {
        return $more + [
            'applicant' => $applicant + ['name' => '홍길동', 'birthYear' => 2004, 'schoolLevel' => 'university', 'schoolName' => '테스트대학교',
                'email' => 'student@example.com', 'phone' => '010-0000-0000'],
            'form' => ['motivation' => str_repeat('금융을 제대로 배우고 싶습니다. ', 5), 'plan' => str_repeat('매주 두 번 공부 모임을 엽니다. ', 5), 'statements' => []],
            'documents' => [],
            'consents' => ['privacy' => true],
        ];
    }

    protected function minorInput(array $more = [])
    {
        return $this->applicationInput(['birthYear' => 2011, 'schoolLevel' => 'middle', 'schoolName' => '테스트중학교'],
            $more + ['guardian' => ['name' => '홍판서', 'relation' => 'parent', 'contact' => '010-1111-2222']]);
    }

    protected function applicationId($publicId)
    {
        return (int)$this->db->val('SELECT id FROM scholarship_applications WHERE public_id = ?', [$publicId]);
    }

    protected function ledgerRows()
    {
        return (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries');
    }
}
