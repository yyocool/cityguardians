<?php
namespace CG\Admin;

use CG\Infra\Form;
use CG\Infra\Request;
use CG\Service\CityPass;
use CG\Service\Features;
use CG\Service\Partners;

/**
 * 관리자 화면: CITY PASS 관리 (docs/ADMIN_SYSTEM.md 11장, docs/PARTNER_MERCHANT_SYSTEM.md).
 *
 *   제휴 업체 · 지점 · 혜택 · 쿠폰 · 사용 내역 · 이용 통계 · 부정 사용 탐지 · 상점 계정 기록
 *
 * 회원은 회원번호로만 보인다. 닉네임과 계정 번호는 이 화면에 나오지 않는다.
 */
trait PassScreens
{
    private function routePass($get, $path, Request $request)
    {
        $form = $request->form;
        $ip = $request->ip;
        $partners = $this->app->partners();
        if ($path === '/admin/pass/merchants') {
            return $get ? $this->merchantList([], [], $request) : $this->merchantSave(null, $form, $request);
        }
        if (preg_match('#^/admin/pass/merchants/(\d{1,18})$#', $path, $m)) {
            return $get ? $this->merchantDetail($m[1], [], null, $request) : $this->merchantSave(self::id($m[1]), $form, $request);
        }
        if (preg_match('#^/admin/pass/merchants/(\d{1,18})/transition$#', $path, $m) && !$get) {
            $problems = $partners->merchantTransition($this->admin, $m[1], self::field($form, 'action'), self::field($form, 'reason'), $ip);
            return $problems ? $this->merchantDetail($m[1], $problems, null, $request) : ['redirect' => '/admin/pass/merchants/' . self::id($m[1]) . '?done=changed'];
        }
        if (preg_match('#^/admin/pass/merchants/(\d{1,18})/branches$#', $path, $m) && !$get) {
            $problems = $partners->saveBranch($this->admin, $m[1], null, $form, $ip);
            return $problems ? $this->merchantDetail($m[1], $problems, null, $request) : ['redirect' => '/admin/pass/merchants/' . self::id($m[1]) . '?done=saved'];
        }
        if (preg_match('#^/admin/pass/merchants/(\d{1,18})/branches/(\d{1,18})$#', $path, $m)) {
            if ($get) {
                return $this->branchEdit($m[1], $m[2], [], null);
            }
            $problems = $partners->saveBranch($this->admin, $m[1], $m[2], $form, $ip);
            return $problems ? $this->branchEdit($m[1], $m[2], $problems, $form) : ['redirect' => '/admin/pass/merchants/' . self::id($m[1]) . '?done=saved'];
        }
        if (preg_match('#^/admin/pass/merchants/(\d{1,18})/offers/new$#', $path, $m) && $get) {
            return $this->offerForm($m[1], null, [], null, $request);
        }
        if (preg_match('#^/admin/pass/merchants/(\d{1,18})/offers$#', $path, $m) && !$get) {
            list($id, $problems) = $partners->saveOffer($this->admin, $m[1], null, $form, $ip);
            return $problems ? $this->offerForm($m[1], null, $problems, $form, $request) : ['redirect' => '/admin/pass/offers/' . $id . '?done=saved'];
        }
        if (preg_match('#^/admin/pass/merchants/(\d{1,18})/users$#', $path, $m) && !$get) {
            list($problems, $password) = $partners->createMerchantUser($this->admin, $m[1], $form, $ip);
            return $this->merchantDetail($m[1], $problems, $password !== null ? [self::field($form, 'login'), $password] : null, $request);
        }
        if (preg_match('#^/admin/pass/users/(\d{1,18})/(deactivate|activate|reset)$#', $path, $m) && !$get) {
            $merchantId = $this->app->db->val('SELECT merchant_id FROM merchant_users WHERE id = ?', [self::id($m[1])]);
            if ($merchantId === null) {
                return null;
            }
            $login = $this->app->db->val('SELECT login FROM merchant_users WHERE id = ?', [self::id($m[1])]);
            list($problems, $password) = $partners->merchantUserAction($this->admin, $m[1], $m[2], $ip);
            if ($password !== null) {
                return $this->merchantDetail($merchantId, $problems, [$login, $password], $request);
            }
            return $problems ? $this->merchantDetail($merchantId, $problems, null, $request) : ['redirect' => '/admin/pass/merchants/' . (int)$merchantId . '?done=changed'];
        }
        if ($path === '/admin/pass/offers') {
            return $get ? $this->offerList($request) : null;
        }
        if (preg_match('#^/admin/pass/offers/(\d{1,18})$#', $path, $m)) {
            $offer = $partners->offer($this->admin, $m[1]);
            if ($get) {
                return $this->offerForm($offer['merchant_id'], $offer, [], null, $request);
            }
            list(, $problems) = $partners->saveOffer($this->admin, $offer['merchant_id'], $offer['id'], $form, $ip);
            return $problems ? $this->offerForm($offer['merchant_id'], $offer, $problems, $form, $request) : ['redirect' => '/admin/pass/offers/' . (int)$offer['id'] . '?done=saved'];
        }
        if (preg_match('#^/admin/pass/offers/(\d{1,18})/transition$#', $path, $m) && !$get) {
            $problems = $partners->offerTransition($this->admin, $m[1], self::field($form, 'action'), self::field($form, 'reason'), $ip);
            if ($problems) {
                $offer = $partners->offer($this->admin, $m[1]);
                return $this->offerForm($offer['merchant_id'], $offer, $problems, null, $request);
            }
            return ['redirect' => '/admin/pass/offers/' . self::id($m[1]) . '?done=changed'];
        }
        if ($path === '/admin/pass/coupons') {
            return $get ? $this->couponList([], $request) : null;
        }
        if (preg_match('#^/admin/pass/coupons/(\d{1,18})/cancel$#', $path, $m) && !$get) {
            $problems = $partners->cancelCoupon($this->admin, $m[1], self::field($form, 'reason'), $ip);
            return $problems ? $this->couponList($problems, $request) : ['redirect' => '/admin/pass/coupons?done=cancelled'];
        }
        if ($path === '/admin/pass/redemptions') {
            return $get ? $this->redemptionList($request) : null;
        }
        if ($path === '/admin/pass/stats') {
            return $get ? $this->passStats() : null;
        }
        if ($path === '/admin/pass/fraud') {
            return $get ? $this->passFraud() : null;
        }
        if ($path === '/admin/pass/audit') {
            return $get ? $this->merchantAudit($request) : null;
        }
        return null;
    }

    private function passNotice()
    {
        $features = $this->app->features();
        $off = [];
        if (!$features->isEnabled(Features::PASS_MEMBERSHIP)) {
            $off[] = 'CITY PASS 멤버십';
        }
        if (!$features->isEnabled(Features::PASS_COUPONS)) {
            $off[] = '쿠폰 발급·사용';
        }
        return $off ? '<div class="notice" role="status">지금 꺼져 있는 기능: ' . Html::e(implode(', ', $off))
            . '. 꺼져 있는 동안에는 승인된 업체와 혜택도 밖에 보이지 않습니다. 등록과 승인 준비는 미리 해 둘 수 있습니다.</div>' : '';
    }

    // ── 제휴 업체 ───────────────────────────────────────────

    private function merchantInputs(array $values)
    {
        $v = function ($key) use ($values) {
            return isset($values[$key]) ? (string)$values[$key] : '';
        };
        return Html::input('name', '상점명', $v('name'))
            . Html::select('category', '업종', CityPass::categories(), $v('category'))
            . Html::input('contract_reference', '제휴 계약서 문서 번호', $v('contract_reference'), '승인하려면 필요합니다. 계약이 없는 상점은 제휴 상점으로 보일 수 없습니다')
            . Html::input('contract_starts_on', '계약 시작일', $v('contract_starts_on'), '예: 2026-12-01')
            . Html::input('contract_ends_on', '계약 종료일', $v('contract_ends_on'), '예: 2027-11-30')
            . Html::input('contact_name', '업체 담당자', $v('contact_name'))
            . Html::input('contact_phone', '담당자 연락처', $v('contact_phone'), '운영용. 밖에 보이지 않습니다')
            . Html::input('contact_email', '담당자 이메일', $v('contact_email'), '운영용. 밖에 보이지 않습니다')
            . Html::checkbox('youth_safe_confirmed', '청소년에게 맞지 않는 업종·상품(주류, 담배, 유흥, 사행성 등)이 아님을 확인했습니다',
                (string)$v('youth_safe_confirmed') === '1');
    }

    private function merchantList(array $problems, array $entered, Request $request)
    {
        if (!$this->can(Auth::PASS_VIEW)) {
            return $this->denied();
        }
        $statuses = Partners::merchantStatuses();
        $categories = CityPass::categories();
        $rows = [];
        foreach ($this->app->partners()->merchants($this->admin) as $row) {
            $rows[] = [$this->link('/admin/pass/merchants/' . $row['id'], $row['name']), $categories[$row['category']], $statuses[$row['status']],
                $row['contract_reference'], trim($row['contract_starts_on'] . ' ~ ' . $row['contract_ends_on'], ' ~'), $row['branch_count'], $row['offer_count']];
        }
        $content = self::done($request) . Html::problems($problems) . $this->passNotice()
            . '<p>제휴 계약이 확인된 업체만 승인할 수 있고, 승인은 등록하거나 마지막으로 고친 사람이 아닌 다른 관리자가 합니다. '
            . '승인된 업체만 홈페이지와 게임에 제휴 상점으로 보입니다.</p>'
            . Html::table(['상점명', '업종', '상태', '계약서 문서 번호', '계약 기간', '운영 지점', '혜택'], $rows, '등록된 제휴 업체가 없습니다.');
        if ($this->can(Auth::PASS_MANAGE)) {
            $content .= '<h2>제휴 업체 등록</h2>' . $this->form('/admin/pass/merchants', $this->merchantInputs($entered), '초안으로 등록');
        }
        return $this->page('CITY PASS: 제휴 업체', $content, $problems ? 422 : 200);
    }

    private function merchantSave($id, array $form, Request $request)
    {
        list($savedId, $problems) = $this->app->partners()->saveMerchant($this->admin, $id, $form, $request->ip);
        if ($problems) {
            return $id === null ? $this->merchantList($problems, $form, $request) : $this->merchantDetail($id, $problems, null, $request, $form);
        }
        return ['redirect' => '/admin/pass/merchants/' . $savedId . '?done=saved'];
    }

    private function branchInputs(array $values)
    {
        $v = function ($key) use ($values) {
            return isset($values[$key]) ? (string)$values[$key] : '';
        };
        return Html::input('name', '지점 이름', $v('name'))
            . Html::input('region_code', '지역 (시·군·구 코드 5자리)', $v('region_code'), '예: 11110 = 서울 종로구')
            . Html::input('address', '주소', $v('address'), '실제 매장의 주소')
            . Html::input('phone', '연락처', $v('phone'), '매장 전화번호 (밖에 보입니다)')
            . Html::input('lat', '지도 위치: 위도', $v('lat'), '32.5 ~ 39.5, 비워 둘 수 있음')
            . Html::input('lng', '지도 위치: 경도', $v('lng'), '123.5 ~ 132.5')
            . Html::input('image_file', '대표 이미지 파일 이름', $v('image_file'), '서버의 public/media 에 넣어 둔 파일 (예: cafe-jongno.jpg). 올리는 기능은 없습니다')
            . Html::checkbox('is_active', '운영 중', !isset($values['is_active']) || (string)$values['is_active'] === '1');
    }

    /** @param array|null $issued [아이디, 임시 비밀번호]: 방금 만든 상점 계정. 이 화면에서 한 번만 보인다. */
    private function merchantDetail($id, array $problems, $issued, Request $request, array $entered = null)
    {
        $partners = $this->app->partners();
        $merchant = $partners->merchant($this->admin, $id);
        $statuses = Partners::merchantStatuses();
        $manage = $this->can(Auth::PASS_MANAGE);
        $path = '/admin/pass/merchants/' . (int)$merchant['id'];
        $content = self::done($request) . Html::problems($problems) . $this->back('/admin/pass/merchants') . $this->passNotice();
        if ($issued !== null) {
            $content .= '<div class="notice" role="status"><strong>상점 계정의 임시 비밀번호 (이 화면에서 한 번만 보입니다)</strong><br>아이디: <code>'
                . Html::e($issued[0]) . '</code> · 임시 비밀번호: <code>' . Html::e($issued[1]) . '</code><br>'
                . '점주에게 안전한 방법으로 전달해 주세요. 처음 로그인하면 비밀번호를 바꿔야 쓸 수 있습니다.</div>';
        }
        $content .= Html::table(['항목', '값'], [
            ['상태', $statuses[$merchant['status']]], ['상태를 바꾼 사유', $merchant['status_reason']],
            ['승인한 때', self::kst($merchant['approved_at'])], ['공개 번호', $merchant['public_id']],
        ]);

        // 상태 바꾸기
        $actions = [
            'draft' => ['submit' => '승인 요청', 'end' => '제휴 종료'],
            'pending' => ['approve' => '승인 (공개)', 'return' => '초안으로 돌려보내기', 'end' => '제휴 종료'],
            'approved' => ['suspend' => '일시 중지', 'reopen' => '고치기 위해 초안으로 (공개가 멈춥니다)', 'end' => '제휴 종료'],
            'suspended' => ['resume' => '다시 공개', 'reopen' => '고치기 위해 초안으로', 'end' => '제휴 종료'],
        ];
        if (isset($actions[$merchant['status']]) && ($manage || $this->can(Auth::PASS_APPROVE))) {
            $content .= '<h2>상태 바꾸기</h2>' . $this->form($path . '/transition',
                Html::select('action', '할 일', $actions[$merchant['status']], '') . Html::reason(), '바꾸기');
        }

        // 내용
        $editable = in_array($merchant['status'], ['draft', 'pending'], true);
        $inputs = $this->merchantInputs($entered !== null ? $entered : $merchant);
        $content .= '<h2>업체 정보</h2>' . ($manage && $editable ? $this->form($path, $inputs)
            : '<div class="readonly">' . $inputs . '</div>' . ($manage ? '<p>승인된 업체는 초안으로 되돌린 뒤 고칠 수 있습니다.</p>' : ''));

        // 지점
        $rows = [];
        foreach ($partners->branches($this->admin, $merchant['id']) as $branch) {
            $rows[] = [$branch['name'], $branch['sido_name'] . ' ' . $branch['region_name'], $branch['address'], $branch['phone'],
                $branch['lat'] !== null ? $branch['lat'] . ', ' . $branch['lng'] : '', $branch['image_file'], (int)$branch['is_active'] === 1 ? '운영 중' : '닫음',
                $manage ? $this->link($path . '/branches/' . $branch['id'], '고치기') : ''];
        }
        $content .= '<h2>지점</h2>' . Html::table(['지점', '지역', '주소', '연락처', '지도 위치', '대표 이미지', '상태', ''], $rows, '지점이 없습니다. 지점이 있어야 승인할 수 있습니다.');
        if ($manage && $merchant['status'] !== 'ended') {
            $content .= '<h3>지점 더하기</h3>' . $this->form($path . '/branches', $this->branchInputs([]), '지점 더하기');
        }

        // 혜택
        $offerStatuses = Partners::offerStatuses();
        $rows = [];
        foreach ($partners->offers($this->admin, $merchant['id']) as $offer) {
            $rows[] = [$this->link('/admin/pass/offers/' . $offer['id'], $offer['title']), CityPass::discountText($offer['discount_type'], (int)$offer['discount_value']),
                $offerStatuses[$offer['status']], self::kst($offer['starts_at']) . ' ~ ' . self::kst($offer['ends_at']), $offer['issued_count'], $offer['redeemed_count']];
        }
        $content .= '<h2>혜택</h2>' . Html::table(['혜택', '할인', '상태', '이용 기간 (한국 시간)', '발급', '사용'], $rows, '등록된 혜택이 없습니다.');
        if ($manage && $merchant['status'] !== 'ended') {
            $content .= '<p>' . Html::e($this->link($path . '/offers/new', '혜택 등록')) . '</p>';
        }

        // 상점 계정
        $rows = [];
        foreach ($partners->merchantUsers($this->admin, $merchant['id']) as $user) {
            $buttons = '';
            if ($manage) {
                $buttons = $this->inline('/admin/pass/users/' . $user['id'] . '/' . ((int)$user['is_active'] === 1 ? 'deactivate' : 'activate'),
                    (int)$user['is_active'] === 1 ? '멈추기' : '다시 열기', [], null)->html
                    . ' ' . $this->inline('/admin/pass/users/' . $user['id'] . '/reset', '임시 비밀번호 새로 내기', [], null)->html;
            }
            $rows[] = [$user['login'], $user['display_name'], $user['role'] === 'owner' ? '점주' : '직원', $user['branch_name'] !== null ? $user['branch_name'] : '모든 지점',
                (int)$user['is_active'] === 1 ? '사용 중' : '멈춤', (int)$user['must_change_password'] === 1 ? '비밀번호를 바꿔야 함' : '',
                self::kst($user['last_login_at']), Html::raw($buttons)];
        }
        $content .= '<h2>상점 계정 (쿠폰 확인 화면에 로그인하는 계정)</h2><p>상점 계정은 자기 업체(지점을 정했으면 그 지점)의 쿠폰만 확인하고 처리할 수 있습니다.</p>'
            . Html::table(['아이디', '이름', '구분', '지점', '상태', '', '마지막 로그인', ''], $rows, '상점 계정이 없습니다.');
        if ($manage && $merchant['status'] !== 'ended') {
            $branches = ['' => '모든 지점'];
            foreach ($partners->branches($this->admin, $merchant['id']) as $branch) {
                $branches[$branch['id']] = $branch['name'];
            }
            $content .= '<h3>상점 계정 만들기</h3>' . $this->form($path . '/users',
                Html::input('login', '아이디', '', '영문 소문자·숫자·._- 3~40자') . Html::input('display_name', '이름(직함)', '')
                . Html::select('role', '구분', ['owner' => '점주', 'staff' => '직원'], 'staff') . Html::select('branch_id', '지점', $branches, ''), '계정 만들기');
        }
        return $this->page('제휴 업체 — ' . $merchant['name'], $content, $problems ? 422 : 200);
    }

    private function branchEdit($merchantId, $branchId, array $problems, $entered)
    {
        if (!$this->can(Auth::PASS_MANAGE)) {
            return $this->denied();
        }
        $merchant = $this->app->partners()->merchant($this->admin, $merchantId);
        $branch = null;
        foreach ($this->app->partners()->branches($this->admin, $merchant['id']) as $row) {
            if ((int)$row['id'] === (int)$branchId) {
                $branch = $row;
            }
        }
        if ($branch === null) {
            return null;
        }
        $path = '/admin/pass/merchants/' . (int)$merchant['id'];
        if ($entered !== null && !isset($entered['is_active'])) {
            $entered['is_active'] = '0';
        }
        return $this->page('지점 — ' . $merchant['name'] . ' ' . $branch['name'], Html::problems($problems) . $this->back($path, '업체로')
            . $this->form($path . '/branches/' . (int)$branch['id'], $this->branchInputs($entered !== null ? $entered : $branch)), $problems ? 422 : 200);
    }

    // ── 혜택 ────────────────────────────────────────────────

    private function offerList(Request $request)
    {
        $statuses = Partners::offerStatuses();
        $merchantStatuses = Partners::merchantStatuses();
        $rows = [];
        foreach ($this->app->partners()->offers($this->admin) as $offer) {
            $rows[] = [$this->link('/admin/pass/offers/' . $offer['id'], $offer['title']),
                $this->link('/admin/pass/merchants/' . $offer['merchant_id'], $offer['merchant_name']) , $merchantStatuses[$offer['merchant_status']],
                CityPass::discountText($offer['discount_type'], (int)$offer['discount_value']), $statuses[$offer['status']],
                self::kst($offer['starts_at']) . ' ~ ' . self::kst($offer['ends_at']),
                $offer['issued_count'] . ($offer['total_limit'] !== null ? ' / ' . $offer['total_limit'] : ''), $offer['redeemed_count']];
        }
        return $this->page('CITY PASS: 혜택', self::done($request) . $this->passNotice()
            . '<p>혜택은 업체 화면에서 등록합니다. 승인된 업체의 승인된 혜택만 밖에 보이고, 쿠폰은 그 혜택에서만 발급됩니다. '
            . '쿠폰 발급은 GC·전투 보상과 이어져 있지 않습니다.</p>'
            . Html::table(['혜택', '업체', '업체 상태', '할인', '상태', '이용 기간 (한국 시간)', '발급 / 한도', '사용'], $rows, '등록된 혜택이 없습니다.'));
    }

    private function offerForm($merchantId, $offer, array $problems, $entered, Request $request)
    {
        $partners = $this->app->partners();
        $merchant = $partners->merchant($this->admin, $merchantId);
        $manage = $this->can(Auth::PASS_MANAGE);
        if ($offer === null && !$manage) {
            return $this->denied();
        }
        $values = $entered !== null ? $entered : ($offer !== null ? $offer : ['per_member_limit' => '1', 'coupon_valid_days' => '30', 'issue_policy' => 'membership']);
        if ($entered === null && $offer !== null) {
            $values['starts_at'] = Form::koreaText($offer['starts_at']);
            $values['ends_at'] = Form::koreaText($offer['ends_at']);
        }
        $v = function ($key) use ($values) {
            return isset($values[$key]) ? (string)$values[$key] : '';
        };
        $branches = ['' => '모든 지점'];
        foreach ($partners->branches($this->admin, $merchant['id']) as $branch) {
            $branches[$branch['id']] = $branch['name'];
        }
        $inputs = Html::input('title', '혜택 이름', $v('title'))
            . Html::textarea('description', '설명', $v('description'))
            . Html::select('discount_type', '할인 유형', CityPass::discountTypes(), $v('discount_type'))
            . Html::input('discount_value', '할인율(%) 또는 할인 금액(원)', $v('discount_value'), '증정·기타 혜택이면 비워 둡니다')
            . Html::textarea('conditions', '이용 조건', $v('conditions'), '예: 1인 1회, 다른 할인과 함께 쓸 수 없음')
            . Html::select('issue_policy', '쿠폰 발급 조건', CityPass::issuePolicies(), $v('issue_policy'))
            . '<p><small>발급 조건에는 게임 성적·GC·결제가 없습니다. 고를 수 있는 것은 위의 조건뿐입니다.</small></p>'
            . Html::input('starts_at', '이용 시작 (한국 시간)', $v('starts_at'), '예: 2026-12-01 09:00')
            . Html::input('ends_at', '이용 끝 (한국 시간)', $v('ends_at'), '계약 종료일을 넘을 수 없습니다')
            . Html::input('total_limit', '전체 발급 수', $v('total_limit'), '비워 두면 한도 없음')
            . Html::input('per_member_limit', '한 사람이 받을 수 있는 수', $v('per_member_limit'), '1 ~ 100')
            . Html::input('coupon_valid_days', '쿠폰 유효기간(일)', $v('coupon_valid_days'), '1 ~ 365. 혜택의 이용 기간을 넘지 않습니다')
            . Html::select('branch_id', '쓸 수 있는 지점', $branches, $v('branch_id'));
        $back = $this->back('/admin/pass/merchants/' . (int)$merchant['id'], '업체로');
        if ($offer === null) {
            return $this->page('혜택 등록 — ' . $merchant['name'], Html::problems($problems) . $back
                . $this->form('/admin/pass/merchants/' . (int)$merchant['id'] . '/offers', $inputs, '초안으로 등록'), $problems ? 422 : 200);
        }
        $statuses = Partners::offerStatuses();
        $path = '/admin/pass/offers/' . (int)$offer['id'];
        $content = self::done($request) . Html::problems($problems) . $back . $this->passNotice()
            . Html::table(['항목', '값'], [['상태', $statuses[$offer['status']]], ['상태를 바꾼 사유', $offer['status_reason']],
                ['발급한 쿠폰', $offer['issued_count']], ['승인한 때', self::kst($offer['approved_at'])], ['업체 상태', Partners::merchantStatuses()[$merchant['status']]]]);
        $actions = [
            'draft' => ['submit' => '승인 요청', 'end' => '종료'],
            'pending' => ['approve' => '승인 (공개)', 'return' => '초안으로 돌려보내기', 'end' => '종료'],
            'approved' => ['pause' => '발급 중지', 'reopen' => '고치기 위해 초안으로 (쿠폰이 한 장도 나가지 않았을 때만)', 'end' => '종료'],
            'paused' => ['resume' => '다시 발급', 'reopen' => '고치기 위해 초안으로', 'end' => '종료'],
        ];
        if (isset($actions[$offer['status']]) && ($manage || $this->can(Auth::PASS_APPROVE))) {
            $content .= '<h2>상태 바꾸기</h2><p>승인은 등록하거나 마지막으로 고친 사람이 아닌 다른 관리자가 합니다. 종료해도 이미 나간 쿠폰은 유효기간까지 쓸 수 있습니다.</p>'
                . $this->form($path . '/transition', Html::select('action', '할 일', $actions[$offer['status']], '') . Html::reason(), '바꾸기');
        }
        $editable = in_array($offer['status'], ['draft', 'pending'], true);
        $content .= '<h2>혜택 내용</h2>' . ($manage && $editable ? $this->form($path, $inputs) : '<div class="readonly">' . $inputs . '</div>');
        return $this->page('혜택 — ' . $offer['title'], $content, $problems ? 422 : 200);
    }

    // ── 쿠폰, 사용 내역 ─────────────────────────────────────

    private static function couponStatuses()
    {
        return ['issued' => '발급됨', 'redeemed' => '사용함', 'expired' => '기간 만료', 'cancelled' => '취소됨'];
    }

    private function couponList(array $problems, Request $request)
    {
        $statuses = self::couponStatuses();
        $status = self::field($request->query, 'status');
        $status = isset($statuses[$status]) ? $status : '';
        $offerId = self::field($request->query, 'offer');
        $offerId = ctype_digit($offerId) ? $offerId : '';
        $page = self::pageNo($request);
        $reasons = CityPass::issuePolicies();
        $rows = [];
        $coupons = $this->app->partners()->coupons($this->admin, $status, $offerId, $page);
        foreach ($coupons as $coupon) {
            $rows[] = [$coupon['public_id'], $coupon['member_no'], $coupon['merchant_name'], $coupon['title'], $statuses[$coupon['status']],
                isset($reasons[$coupon['issue_reason']]) ? $reasons[$coupon['issue_reason']] : $coupon['issue_reason'],
                self::kst($coupon['issued_at']), self::kst($coupon['expires_at']), self::kst($coupon['redeemed_at']), $coupon['cancelled_reason'],
                $coupon['status'] === 'issued' && $this->can(Auth::PASS_MANAGE)
                    ? $this->inline('/admin/pass/coupons/' . $coupon['id'] . '/cancel', '취소', [], 'reason', '취소 사유') : ''];
        }
        $filter = '<form method="get" action="' . Html::e($this->url('/admin/pass/coupons')) . '" class="filter">'
            . Html::select('status', '상태', ['' => '전체'] + $statuses, $status) . Html::input('offer', '혜택 번호', $offerId)
            . '<p><button type="submit">보기</button></p></form>';
        return $this->page('CITY PASS: 쿠폰', self::done($request) . Html::problems($problems)
            . '<p>회원은 회원번호로만 보입니다. 쿠폰의 사용 처리는 상점 확인 화면에서만 합니다: 여기에서는 아직 쓰지 않은 쿠폰을 취소할 수만 있습니다.</p>'
            . $filter . Html::table(['쿠폰', '회원번호', '업체', '혜택', '상태', '발급 조건', '발급 (한국 시간)', '유효기간', '사용한 때', '취소 사유', ''], $rows, '쿠폰이 없습니다.')
            . $this->pager('/admin/pass/coupons', array_filter(['status' => $status, 'offer' => $offerId], 'strlen'), $page, count($coupons)),
            $problems ? 422 : 200);
    }

    private function redemptionList(Request $request)
    {
        $page = self::pageNo($request);
        $rows = [];
        $list = $this->app->partners()->redemptions($this->admin, $page);
        foreach ($list as $row) {
            $rows[] = [self::kst($row['redeemed_at']), $row['merchant_name'], $row['branch_name'], $row['title'], $row['public_id'], $row['member_no'], $row['staff_login']];
        }
        return $this->page('CITY PASS: 사용 내역', '<p>쿠폰 한 장은 한 번만 쓸 수 있습니다 (사용 기록은 쿠폰마다 한 줄뿐입니다).</p>'
            . Html::table(['사용한 때 (한국 시간)', '업체', '지점', '혜택', '쿠폰', '회원번호', '처리한 상점 계정'], $rows, '사용 내역이 없습니다.')
            . $this->pager('/admin/pass/redemptions', [], $page, count($list)));
    }

    // ── 통계, 부정 사용 탐지, 상점 계정 기록 ────────────────

    private function passStats()
    {
        $stats = $this->app->partners()->stats($this->admin);
        $statuses = Partners::offerStatuses();
        $content = $this->passNotice() . Html::table(['항목', '값'], [
            ['멤버', $stats['members']], ['공개 중인 제휴 업체', $stats['merchants']], ['공개 중인 혜택', $stats['offers']],
            ['쿠폰: 발급됨 (아직 쓰지 않음)', $stats['coupons']['issued']], ['쿠폰: 사용함', $stats['coupons']['redeemed']],
            ['쿠폰: 기간 만료', $stats['coupons']['expired']], ['쿠폰: 취소됨', $stats['coupons']['cancelled']],
        ]);
        $rows = [];
        foreach ($stats['byOffer'] as $row) {
            $rows[] = [$this->link('/admin/pass/offers/' . $row['id'], $row['title']), $row['merchant_name'], $statuses[$row['status']], (int)$row['issued_count'],
                (int)$row['redeemed'], (int)$row['issued_count'] > 0 ? number_format((int)$row['redeemed'] * 100 / (int)$row['issued_count'], 1) . '%' : '-',
                (int)$row['expired'], (int)$row['cancelled']];
        }
        $content .= '<h2>혜택별</h2>' . Html::table(['혜택', '업체', '상태', '발급', '사용', '사용률', '만료', '취소'], $rows, '혜택이 없습니다.');
        $rows = [];
        foreach ($stats['byRegion'] as $row) {
            $rows[] = [$row['sido_name'] . ' ' . $row['region_name'], (int)$row['n']];
        }
        $content .= '<h2>지역별 사용</h2>' . Html::table(['지역', '사용'], $rows, '사용 내역이 없습니다.');
        $rows = [];
        foreach ($stats['daily'] as $row) {
            $rows[] = [$row['day'], (int)$row['n']];
        }
        $content .= '<h2>최근 14일 (한국 날짜)</h2>' . Html::table(['날짜', '사용'], $rows, '사용 내역이 없습니다.')
            . '<p>이 통계에는 게임 기록이 섞이지 않습니다. 개인을 알아볼 수 있는 값도 없습니다.</p>';
        return $this->page('CITY PASS: 이용 통계', $content);
    }

    private function passFraud()
    {
        $signals = $this->app->partners()->fraudSignals($this->admin);
        $rows = [];
        foreach ($signals['failedVerify'] as $row) {
            $rows[] = [$row['login'], $row['merchant_name'], (int)$row['n'], self::kst($row['last_at'])];
        }
        $content = '<p>최근 24시간의 신호입니다. 판정이 아니라 사람이 들여다볼 목록입니다. 필요하면 상점 계정을 멈추거나 쿠폰을 취소합니다.</p>'
            . '<h2>쿠폰 코드를 여러 번 틀린 상점 계정 (5번 이상)</h2><p>코드를 추측하고 있을 수 있습니다. 10분에 10번 틀리면 그 계정의 확인이 잠시 막힙니다.</p>'
            . Html::table(['상점 계정', '업체', '틀린 횟수', '마지막'], $rows, '해당하는 계정이 없습니다.');
        $rows = [];
        foreach ($signals['burst'] as $row) {
            $rows[] = [$row['login'], $row['merchant_name'], (int)$row['n'], self::kst($row['first_at']) . ' ~ ' . self::kst($row['last_at'])];
        }
        $content .= '<h2>짧은 시간에 쿠폰을 몰아서 처리한 상점 계정 (10분에 20장 이상)</h2>'
            . Html::table(['상점 계정', '업체', '처리한 수', '때 (한국 시간)'], $rows, '해당하는 계정이 없습니다.');
        $rows = [];
        foreach ($signals['presentOnly'] as $row) {
            $rows[] = [$row['member_no'], (int)$row['n']];
        }
        $content .= '<h2>코드를 여러 번 만든 회원 (15번 이상)</h2>' . Html::table(['회원번호', '코드를 만든 횟수'], $rows, '해당하는 회원이 없습니다.');
        return $this->page('CITY PASS: 부정 사용 탐지', $content);
    }

    private function merchantAudit(Request $request)
    {
        if (!$this->can(Auth::BENEFIT_AUDIT)) {
            return $this->denied();
        }
        $page = self::pageNo($request);
        $list = $this->app->db->all(
            'SELECT l.*, u.login, m.name AS merchant_name FROM merchant_audit_logs l LEFT JOIN merchant_users u ON u.id = l.merchant_user_id'
            . ' LEFT JOIN partner_merchants m ON m.id = l.merchant_id ORDER BY l.id DESC LIMIT 100 OFFSET ' . ($page - 1) * 100);
        $rows = [];
        foreach ($list as $row) {
            $rows[] = [self::kst($row['created_at']), $row['merchant_name'], $row['login'], $row['action'], $row['target'], $row['detail'], $row['ip']];
        }
        return $this->page('CITY PASS: 상점 계정 기록', '<p>상점 계정의 로그인, 쿠폰 확인·사용 처리, 비밀번호 변경이 남습니다. 쿠폰 코드는 남지 않습니다.</p>'
            . Html::table(['때 (한국 시간)', '업체', '상점 계정', '한 일', '대상', '내용', '주소'], $rows, '기록이 없습니다.')
            . $this->pager('/admin/pass/audit', [], $page, count($list)));
    }
}
