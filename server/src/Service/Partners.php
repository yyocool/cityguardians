<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Form;
use CG\Infra\Ids;

/**
 * CITY PASS: 운영 쪽 (docs/PARTNER_MERCHANT_SYSTEM.md).
 *
 * 제휴 업체와 혜택은 "초안 → 승인 대기 → 승인" 을 거쳐야 밖에 보인다.
 *   - 승인은 내용을 등록하거나 마지막으로 고친 사람이 아닌 다른 관리자가 한다.
 *   - 업체는 제휴 계약서의 문서 번호·계약 기간·청소년 적합 확인·지점이 있어야 승인된다.
 *     계약이 없는 상점을 제휴 상점으로 보여 주는 길을 막기 위해서다.
 *   - 승인된 것은 그대로 고칠 수 없다. 초안으로 되돌린 뒤 고치고 다시 승인받는다.
 * 모든 변경은 관리자 감사 기록에 남는다.
 */
final class Partners
{
    /** @var App */
    private $app;
    /** @var Auth */
    private $auth;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->auth = new Auth($app);
    }

    public static function merchantStatuses()
    {
        return ['draft' => '초안', 'pending' => '승인 대기', 'approved' => '승인됨 (공개)', 'suspended' => '일시 중지', 'ended' => '제휴 종료'];
    }

    public static function offerStatuses()
    {
        return ['draft' => '초안', 'pending' => '승인 대기', 'approved' => '승인됨 (공개)', 'paused' => '발급 중지', 'ended' => '종료'];
    }

    private static function need(array $admin, $permission)
    {
        if (!Auth::can($admin, $permission)) {
            throw new ApiError(ApiError::FORBIDDEN, '이 계정의 역할로는 할 수 없는 일입니다.');
        }
    }

    private static function needAny(array $admin, array $permissions)
    {
        foreach ($permissions as $permission) {
            if (Auth::can($admin, $permission)) {
                return;
            }
        }
        throw new ApiError(ApiError::FORBIDDEN, '이 계정의 역할로는 할 수 없는 일입니다.');
    }

    // ── 제휴 업체 ───────────────────────────────────────────

    public function merchants(array $admin)
    {
        self::need($admin, Auth::PASS_VIEW);
        return $this->app->db->all(
            'SELECT m.*, (SELECT COUNT(*) FROM partner_branches b WHERE b.merchant_id = m.id AND b.is_active = 1) AS branch_count,'
            . ' (SELECT COUNT(*) FROM partner_offers o WHERE o.merchant_id = m.id) AS offer_count'
            . ' FROM partner_merchants m ORDER BY m.id DESC LIMIT 500');
    }

    public function merchant(array $admin, $id, $lock = false)
    {
        self::need($admin, Auth::PASS_VIEW);
        $row = $this->app->db->one('SELECT * FROM partner_merchants WHERE id = ?' . ($lock ? ' FOR UPDATE' : ''), [(int)$id]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 제휴 업체입니다.');
        }
        return $row;
    }

    /**
     * 제휴 업체를 등록하거나 고친다. 초안과 승인 대기 상태에서만 고칠 수 있다.
     * @return array [id, 문제 목록]
     */
    public function saveMerchant(array $admin, $id, array $input, $ip = '')
    {
        self::need($admin, Auth::PASS_MANAGE);
        $form = new Form($input);
        $values = [
            'name' => $form->text('name', '상점명', 1, 80),
            'category' => $form->choice('category', '업종', CityPass::categories()),
            'contract_reference' => $form->optional('contract_reference', '제휴 계약서 문서 번호', 120),
            'contract_starts_on' => $form->date('contract_starts_on', '계약 시작일'),
            'contract_ends_on' => $form->date('contract_ends_on', '계약 종료일'),
            'contact_name' => $form->optional('contact_name', '업체 담당자', 40),
            'contact_phone' => $form->phone('contact_phone', '담당자 연락처'),
            'contact_email' => $form->email('contact_email', '담당자 이메일'),
            'youth_safe_confirmed' => $form->checked('youth_safe_confirmed') ? 1 : 0,
        ];
        if ($values['contract_starts_on'] !== null && $values['contract_ends_on'] !== null
            && $values['contract_ends_on'] < $values['contract_starts_on']) {
            $form->fail('계약 종료일이 시작일보다 앞입니다.');
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $form, $values, $ip) {
            $existing = $id !== null ? $this->merchant($admin, $id, true) : null;
            if ($existing !== null && !in_array($existing['status'], ['draft', 'pending'], true)) {
                $form->fail('승인된 업체는 그대로 고칠 수 없습니다. 초안으로 되돌린 뒤 고쳐 주세요.');
            }
            if (!$form->ok()) {
                return [$id, $form->problems];
            }
            if ($existing === null) {
                $id = $db->insert(
                    'INSERT INTO partner_merchants (public_id, name, category, status, contract_reference, contract_starts_on, contract_ends_on,'
                    . " contact_name, contact_phone, contact_email, youth_safe_confirmed, created_by, created_at, updated_at) VALUES (?, ?, ?, 'draft', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    [Ids::publicId('m_'), $values['name'], $values['category'], $values['contract_reference'], $values['contract_starts_on'],
                        $values['contract_ends_on'], $values['contact_name'], $values['contact_phone'], $values['contact_email'],
                        $values['youth_safe_confirmed'], $admin['id'], Clock::sql(), Clock::sql()]);
                $this->auth->audit($admin['id'], 'pass.merchant.create', 'partner_merchant', (string)$id, $values['name'], $ip);
            } else {
                // 고친 사람이 created_by 가 된다: 내용을 마지막으로 정한 사람은 그 내용을 승인할 수 없다.
                $db->exec(
                    'UPDATE partner_merchants SET name = ?, category = ?, contract_reference = ?, contract_starts_on = ?, contract_ends_on = ?,'
                    . ' contact_name = ?, contact_phone = ?, contact_email = ?, youth_safe_confirmed = ?, created_by = ?, updated_at = ? WHERE id = ?',
                    [$values['name'], $values['category'], $values['contract_reference'], $values['contract_starts_on'], $values['contract_ends_on'],
                        $values['contact_name'], $values['contact_phone'], $values['contact_email'], $values['youth_safe_confirmed'],
                        $admin['id'], Clock::sql(), $existing['id']]);
                $this->auth->audit($admin['id'], 'pass.merchant.update', 'partner_merchant', (string)$existing['id'], $values['name'], $ip);
            }
            return [(int)$id, []];
        });
    }

    /** 업체를 승인해도 되는가. 문제 목록을 돌려준다. */
    private function merchantApprovalProblems(array $merchant)
    {
        $problems = [];
        if ($merchant['contract_reference'] === null || $merchant['contract_reference'] === '') {
            $problems[] = '제휴 계약서의 문서 번호가 없습니다. 계약이 확인되지 않은 상점은 제휴 상점으로 보일 수 없습니다.';
        }
        if ($merchant['contract_starts_on'] === null || $merchant['contract_ends_on'] === null) {
            $problems[] = '계약 기간이 없습니다.';
        } elseif ($merchant['contract_ends_on'] < Clock::koreaDate()) {
            $problems[] = '계약 기간이 끝났습니다.';
        }
        if ((int)$merchant['youth_safe_confirmed'] !== 1) {
            $problems[] = '청소년에게 맞지 않는 업종·상품이 아님을 확인하지 않았습니다.';
        }
        if ((int)$this->app->db->val('SELECT COUNT(*) FROM partner_branches WHERE merchant_id = ? AND is_active = 1', [$merchant['id']]) === 0) {
            $problems[] = '운영 중인 지점이 하나도 없습니다.';
        }
        return $problems;
    }

    /**
     * 업체의 상태를 바꾼다.
     *   submit  초안 → 승인 대기        return  승인 대기 → 초안       approve  승인 대기 → 승인
     *   suspend 승인 → 일시 중지        resume  일시 중지 → 승인       reopen   승인·중지 → 초안 (고치려고)
     *   end     → 제휴 종료 (되돌릴 수 없다)
     * @return string[] 문제 목록
     */
    public function merchantTransition(array $admin, $id, $action, $reason, $ip = '')
    {
        $rules = [
            'submit' => [['draft'], 'pending', [Auth::PASS_MANAGE]],
            'return' => [['pending'], 'draft', [Auth::PASS_MANAGE, Auth::PASS_APPROVE]],
            'approve' => [['pending'], 'approved', [Auth::PASS_APPROVE]],
            'suspend' => [['approved'], 'suspended', [Auth::PASS_MANAGE, Auth::PASS_APPROVE]],
            'resume' => [['suspended'], 'approved', [Auth::PASS_APPROVE]],
            'reopen' => [['approved', 'suspended'], 'draft', [Auth::PASS_MANAGE]],
            'end' => [['draft', 'pending', 'approved', 'suspended'], 'ended', [Auth::PASS_MANAGE]],
        ];
        if (!isset($rules[$action])) {
            throw new ApiError(ApiError::INVALID_REQUEST, '할 수 없는 일입니다.');
        }
        list($from, $to, $permissions) = $rules[$action];
        self::needAny($admin, $permissions);
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $action, $from, $to, $form, $reason, $ip) {
            $merchant = $this->merchant($admin, $id, true);
            if (!in_array($merchant['status'], $from, true)) {
                $form->fail('지금 상태(' . self::merchantStatuses()[$merchant['status']] . ')에서는 할 수 없습니다.');
            }
            if ($action === 'approve' || $action === 'resume') {
                foreach ($this->merchantApprovalProblems($merchant) as $problem) {
                    $form->fail($problem);
                }
            }
            if ($action === 'approve' && ($merchant['created_by'] === null || (int)$merchant['created_by'] === (int)$admin['id'])) {
                $form->fail('등록하거나 마지막으로 고친 사람은 승인할 수 없습니다. 다른 관리자가 승인해야 합니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $approved = $to === 'approved';
            $db->exec('UPDATE partner_merchants SET status = ?, status_reason = ?, approved_by = ?, approved_at = ?, updated_at = ? WHERE id = ?',
                [$to, $reason, $approved ? $admin['id'] : null, $approved ? Clock::sql() : null, Clock::sql(), $merchant['id']]);
            $this->auth->audit($admin['id'], 'pass.merchant.' . $action, 'partner_merchant', (string)$merchant['id'],
                $merchant['status'] . ' → ' . $to . ': ' . $reason, $ip);
            return [];
        });
    }

    // ── 지점 ────────────────────────────────────────────────

    public function branches(array $admin, $merchantId)
    {
        self::need($admin, Auth::PASS_VIEW);
        return $this->app->db->all(
            'SELECT b.*, r.code AS region_code, r.name AS region_name, s.name AS sido_name FROM partner_branches b'
            . ' JOIN regions r ON r.id = b.region_id JOIN sidos s ON s.id = r.sido_id WHERE b.merchant_id = ? ORDER BY b.id', [(int)$merchantId]);
    }

    /** 지점을 등록하거나 고친다. 주소와 지도 위치는 실제 매장의 것을 적는다. @return string[] 문제 목록 */
    public function saveBranch(array $admin, $merchantId, $id, array $input, $ip = '')
    {
        self::need($admin, Auth::PASS_MANAGE);
        $form = new Form($input);
        $values = [
            'name' => $form->text('name', '지점 이름', 1, 80),
            'address' => $form->text('address', '주소', 2, 190),
            'phone' => $form->phone('phone', '연락처'),
            'image_file' => $form->optional('image_file', '대표 이미지 파일', 80),
            'is_active' => $form->checked('is_active') ? 1 : 0,
        ];
        $regionCode = $form->raw('region_code');
        $regionId = preg_match('/^[0-9]{1,8}$/', $regionCode) ? $this->app->db->val('SELECT id FROM regions WHERE code = ?', [$regionCode]) : null;
        if ($regionId === null) {
            $form->fail('지역: 시·군·구 코드를 다시 확인해 주세요.');
        }
        // 지도 위치: 둘 다 적거나 둘 다 비운다. 우리나라 범위 안이어야 한다.
        $lat = $form->raw('lat');
        $lng = $form->raw('lng');
        if (($lat === '') !== ($lng === '')) {
            $form->fail('지도 위치: 위도와 경도를 함께 적어 주세요.');
        } elseif ($lat !== '' && (!preg_match('/^\d{2}(\.\d{1,6})?$/', $lat) || !preg_match('/^\d{3}(\.\d{1,6})?$/', $lng)
                || (float)$lat < 32.5 || (float)$lat > 39.5 || (float)$lng < 123.5 || (float)$lng > 132.5)) {
            $form->fail('지도 위치: 위도는 32.5~39.5, 경도는 123.5~132.5 사이로 적어 주세요 (소수 6자리까지).');
        }
        // 이미지는 올리는 기능이 없다. 서버의 public/media 에 넣어 둔 파일의 이름만 받는다.
        if ($values['image_file'] !== null && !preg_match('/^[a-z0-9][a-z0-9_\-]{0,60}\.(jpg|jpeg|png|webp)$/', $values['image_file'])) {
            $form->fail('대표 이미지 파일: 영문 소문자·숫자·밑줄·붙임표로 된 jpg, png, webp 파일 이름만 받을 수 있습니다.');
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $merchantId, $id, $form, $values, $regionId, $lat, $lng, $ip) {
            $merchant = $this->merchant($admin, $merchantId, true);
            if ($merchant['status'] === 'ended') {
                $form->fail('제휴가 끝난 업체입니다.');
            }
            $existing = $id !== null ? $db->one('SELECT * FROM partner_branches WHERE id = ? AND merchant_id = ?', [(int)$id, $merchant['id']]) : null;
            if ($id !== null && $existing === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 지점입니다.');
            }
            // 공개 중인 업체의 마지막 지점을 닫을 수는 없다 (지점 없는 제휴 상점이 생긴다).
            if ($existing !== null && (int)$existing['is_active'] === 1 && $values['is_active'] === 0 && $merchant['status'] === 'approved'
                && (int)$db->val('SELECT COUNT(*) FROM partner_branches WHERE merchant_id = ? AND is_active = 1', [$merchant['id']]) <= 1) {
                $form->fail('공개 중인 업체의 마지막 지점은 닫을 수 없습니다. 먼저 업체를 일시 중지해 주세요.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $params = [$values['name'], $regionId, $values['address'], $values['phone'], $lat !== '' ? $lat : null, $lng !== '' ? $lng : null,
                $values['image_file'], $values['is_active'], Clock::sql()];
            if ($existing === null) {
                $newId = $db->insert('INSERT INTO partner_branches (name, region_id, address, phone, lat, lng, image_file, is_active, updated_at, public_id, merchant_id, created_at)'
                    . ' VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)', array_merge($params, [Ids::publicId('b_'), $merchant['id'], Clock::sql()]));
                $this->auth->audit($admin['id'], 'pass.branch.create', 'partner_branch', (string)$newId, $values['name'], $ip);
            } else {
                $db->exec('UPDATE partner_branches SET name = ?, region_id = ?, address = ?, phone = ?, lat = ?, lng = ?, image_file = ?, is_active = ?, updated_at = ? WHERE id = ?',
                    array_merge($params, [$existing['id']]));
                $this->auth->audit($admin['id'], 'pass.branch.update', 'partner_branch', (string)$existing['id'], $values['name'], $ip);
            }
            return [];
        });
    }

    // ── 혜택 ────────────────────────────────────────────────

    public function offers(array $admin, $merchantId = null)
    {
        self::need($admin, Auth::PASS_VIEW);
        return $this->app->db->all(
            'SELECT o.*, m.name AS merchant_name, m.status AS merchant_status,'
            . " (SELECT COUNT(*) FROM benefit_coupons c WHERE c.offer_id = o.id AND c.status = 'redeemed') AS redeemed_count"
            . ' FROM partner_offers o JOIN partner_merchants m ON m.id = o.merchant_id'
            . ($merchantId !== null ? ' WHERE o.merchant_id = ?' : '') . ' ORDER BY o.id DESC LIMIT 500', $merchantId !== null ? [(int)$merchantId] : []);
    }

    public function offer(array $admin, $id, $lock = false)
    {
        self::need($admin, Auth::PASS_VIEW);
        $row = $this->app->db->one('SELECT * FROM partner_offers WHERE id = ?' . ($lock ? ' FOR UPDATE' : ''), [(int)$id]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 혜택입니다.');
        }
        return $row;
    }

    /**
     * 혜택을 등록하거나 고친다. 초안과 승인 대기 상태에서만 고칠 수 있다.
     * 발급 조건에는 게임 성적·게임머니가 없다: 고를 수 있는 값이 CityPass::issuePolicies() 뿐이다.
     * @return array [id, 문제 목록]
     */
    public function saveOffer(array $admin, $merchantId, $id, array $input, $ip = '')
    {
        self::need($admin, Auth::PASS_MANAGE);
        $form = new Form($input);
        $values = [
            'title' => $form->text('title', '혜택 이름', 2, 80),
            'description' => $form->text('description', '설명', 0, 500, true),
            'discount_type' => $form->choice('discount_type', '할인 유형', CityPass::discountTypes()),
            'conditions' => $form->text('conditions', '이용 조건', 2, 500, true),
            'issue_policy' => $form->choice('issue_policy', '쿠폰 발급 조건', CityPass::issuePolicies()),
            'total_limit' => $form->optionalInt('total_limit', '전체 발급 수', 1, 1000000),
            'per_member_limit' => $form->int('per_member_limit', '한 사람이 받을 수 있는 수', 1, 100),
            'coupon_valid_days' => $form->int('coupon_valid_days', '쿠폰 유효기간(일)', 1, 365),
        ];
        $starts = $form->koreaTime('starts_at', '시작');
        $ends = $form->koreaTime('ends_at', '끝');
        if ($starts !== null && $ends !== null && $ends <= $starts) {
            $form->fail('끝나는 시각이 시작보다 뒤여야 합니다.');
        }
        $value = 0;
        if ($values['discount_type'] === 'percent') {
            $value = $form->int('discount_value', '할인율', 1, 100);
        } elseif ($values['discount_type'] === 'amount') {
            $value = $form->int('discount_value', '할인 금액', 100, 10000000);
        }
        $branchId = $form->raw('branch_id');
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $merchantId, $id, $form, $values, $starts, $ends, $value, $branchId, $ip) {
            $merchant = $this->merchant($admin, $merchantId, true);
            if ($merchant['status'] === 'ended') {
                $form->fail('제휴가 끝난 업체입니다.');
            }
            $existing = $id !== null ? $this->offer($admin, $id, true) : null;
            if ($existing !== null && (int)$existing['merchant_id'] !== (int)$merchant['id']) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 혜택입니다.');
            }
            if ($existing !== null && !in_array($existing['status'], ['draft', 'pending'], true)) {
                $form->fail('승인된 혜택은 그대로 고칠 수 없습니다. 초안으로 되돌린 뒤 고쳐 주세요.');
            }
            $branch = null;
            if ($branchId !== '') {
                $branch = ctype_digit($branchId) ? $db->val('SELECT id FROM partner_branches WHERE id = ? AND merchant_id = ?', [(int)$branchId, $merchant['id']]) : null;
                if ($branch === null) {
                    $form->fail('지점: 이 업체의 지점을 골라 주세요.');
                }
            }
            if (!$form->ok()) {
                return [$id, $form->problems];
            }
            $params = [$branch, $values['title'], $values['description'], $values['discount_type'], $value, $values['conditions'],
                $values['issue_policy'], Clock::sql($starts), Clock::sql($ends), $values['total_limit'], $values['per_member_limit'],
                $values['coupon_valid_days'], $admin['id'], Clock::sql()];
            if ($existing === null) {
                $id = $db->insert(
                    'INSERT INTO partner_offers (branch_id, title, description, discount_type, discount_value, conditions, issue_policy, starts_at, ends_at,'
                    . " total_limit, per_member_limit, coupon_valid_days, created_by, updated_at, public_id, merchant_id, status, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'draft', ?)",
                    array_merge($params, [Ids::publicId('o_'), $merchant['id'], Clock::sql()]));
                $this->auth->audit($admin['id'], 'pass.offer.create', 'partner_offer', (string)$id, $values['title'], $ip);
            } else {
                $db->exec(
                    'UPDATE partner_offers SET branch_id = ?, title = ?, description = ?, discount_type = ?, discount_value = ?, conditions = ?, issue_policy = ?,'
                    . ' starts_at = ?, ends_at = ?, total_limit = ?, per_member_limit = ?, coupon_valid_days = ?, created_by = ?, updated_at = ? WHERE id = ?',
                    array_merge($params, [$existing['id']]));
                $this->auth->audit($admin['id'], 'pass.offer.update', 'partner_offer', (string)$existing['id'], $values['title'], $ip);
            }
            return [(int)$id, []];
        });
    }

    /**
     * 혜택의 상태를 바꾼다.
     *   submit 초안 → 승인 대기     return 승인 대기 → 초안     approve 승인 대기 → 승인
     *   pause  승인 → 발급 중지     resume 발급 중지 → 승인     reopen  승인·중지 → 초안 (쿠폰이 한 장도 나가지 않았을 때만)
     *   end    → 종료 (되돌릴 수 없다. 이미 나간 쿠폰은 유효기간까지 쓸 수 있다)
     * @return string[] 문제 목록
     */
    public function offerTransition(array $admin, $id, $action, $reason, $ip = '')
    {
        $rules = [
            'submit' => [['draft'], 'pending', [Auth::PASS_MANAGE]],
            'return' => [['pending'], 'draft', [Auth::PASS_MANAGE, Auth::PASS_APPROVE]],
            'approve' => [['pending'], 'approved', [Auth::PASS_APPROVE]],
            'pause' => [['approved'], 'paused', [Auth::PASS_MANAGE, Auth::PASS_APPROVE]],
            'resume' => [['paused'], 'approved', [Auth::PASS_MANAGE]],
            'reopen' => [['approved', 'paused'], 'draft', [Auth::PASS_MANAGE]],
            'end' => [['draft', 'pending', 'approved', 'paused'], 'ended', [Auth::PASS_MANAGE]],
        ];
        if (!isset($rules[$action])) {
            throw new ApiError(ApiError::INVALID_REQUEST, '할 수 없는 일입니다.');
        }
        list($from, $to, $permissions) = $rules[$action];
        self::needAny($admin, $permissions);
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $action, $from, $to, $form, $reason, $ip) {
            $offer = $this->offer($admin, $id, true);
            $merchant = $this->merchant($admin, $offer['merchant_id']);
            if (!in_array($offer['status'], $from, true)) {
                $form->fail('지금 상태(' . self::offerStatuses()[$offer['status']] . ')에서는 할 수 없습니다.');
            }
            if ($action === 'approve') {
                if ($merchant['status'] !== 'approved') {
                    $form->fail('업체가 승인된 상태가 아닙니다. 업체를 먼저 승인해야 합니다.');
                }
                if (Clock::parse($offer['ends_at']) <= Clock::now()) {
                    $form->fail('이용 기간이 이미 끝났습니다.');
                }
                // 계약이 끝난 뒤까지 혜택을 약속할 수 없다.
                if ($merchant['contract_ends_on'] !== null && Clock::koreaDate(Clock::parse($offer['ends_at']) - 1) > $merchant['contract_ends_on']) {
                    $form->fail('혜택의 이용 기간이 제휴 계약 기간(' . $merchant['contract_ends_on'] . ')을 넘습니다.');
                }
                if ($offer['created_by'] === null || (int)$offer['created_by'] === (int)$admin['id']) {
                    $form->fail('등록하거나 마지막으로 고친 사람은 승인할 수 없습니다. 다른 관리자가 승인해야 합니다.');
                }
            }
            if ($action === 'reopen' && (int)$offer['issued_count'] > 0) {
                $form->fail('이미 쿠폰이 나간 혜택은 내용을 바꿀 수 없습니다. 이 혜택을 종료하고 새 혜택을 등록해 주세요.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $approve = $action === 'approve';
            $db->exec('UPDATE partner_offers SET status = ?, status_reason = ?, approved_by = ' . ($approve ? '?' : 'approved_by')
                . ', approved_at = ' . ($approve ? '?' : 'approved_at') . ', updated_at = ? WHERE id = ?',
                array_merge([$to, $reason], $approve ? [$admin['id'], Clock::sql()] : [], [Clock::sql(), $offer['id']]));
            $this->auth->audit($admin['id'], 'pass.offer.' . $action, 'partner_offer', (string)$offer['id'],
                $offer['status'] . ' → ' . $to . ': ' . $reason, $ip);
            return [];
        });
    }

    // ── 상점 계정 ───────────────────────────────────────────

    public function merchantUsers(array $admin, $merchantId)
    {
        self::need($admin, Auth::PASS_VIEW);
        return $this->app->db->all(
            'SELECT u.id, u.login, u.display_name, u.role, u.is_active, u.must_change_password, u.locked_until, u.last_login_at, u.branch_id, b.name AS branch_name'
            . ' FROM merchant_users u LEFT JOIN partner_branches b ON b.id = u.branch_id WHERE u.merchant_id = ? ORDER BY u.id', [(int)$merchantId]);
    }

    private static function temporaryPassword()
    {
        // 소문자, 대문자, 숫자, 기호가 모두 들어간다. 처음 로그인하면 바꿔야 한다.
        return Ids::token(6) . '-' . strtoupper(Ids::token(6)) . '-' . random_int(100, 999);
    }

    /**
     * 상점 계정을 만든다. 임시 비밀번호는 이때 한 번만 돌려준다 (해시만 저장한다).
     * @return array [문제 목록, 임시 비밀번호 또는 null]
     */
    public function createMerchantUser(array $admin, $merchantId, array $input, $ip = '')
    {
        self::need($admin, Auth::PASS_MANAGE);
        $form = new Form($input);
        $login = strtolower($form->raw('login'));
        if (!preg_match('/^[a-z0-9_.\-]{3,40}$/', $login)) {
            $form->fail('아이디: 영문 소문자·숫자·._- 로 3~40자여야 합니다.');
        }
        $name = $form->text('display_name', '이름(직함)', 1, 40);
        $role = $form->choice('role', '구분', ['owner' => '점주', 'staff' => '직원']);
        $branchId = $form->raw('branch_id');
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $merchantId, $form, $login, $name, $role, $branchId, $ip) {
            $merchant = $this->merchant($admin, $merchantId, true);
            if ($merchant['status'] === 'ended') {
                $form->fail('제휴가 끝난 업체입니다.');
            }
            $branch = null;
            if ($branchId !== '') {
                $branch = ctype_digit($branchId) ? $db->val('SELECT id FROM partner_branches WHERE id = ? AND merchant_id = ?', [(int)$branchId, $merchant['id']]) : null;
                if ($branch === null) {
                    $form->fail('지점: 이 업체의 지점을 골라 주세요.');
                }
            }
            if ($form->ok() && (int)$db->val('SELECT COUNT(*) FROM merchant_users WHERE login = ?', [$login]) > 0) {
                $form->fail('이미 있는 아이디입니다.');
            }
            if (!$form->ok()) {
                return [$form->problems, null];
            }
            $password = self::temporaryPassword();
            $id = $db->insert('INSERT INTO merchant_users (merchant_id, branch_id, login, password_hash, display_name, role, is_active, must_change_password, created_by, created_at)'
                . ' VALUES (?, ?, ?, ?, ?, ?, 1, 1, ?, ?)',
                [$merchant['id'], $branch, $login, password_hash($password, PASSWORD_DEFAULT), $name, $role, $admin['id'], Clock::sql()]);
            $this->auth->audit($admin['id'], 'pass.merchant_user.create', 'merchant_user', (string)$id, $login . ' (' . $role . ')', $ip);
            return [[], $password];
        });
    }

    /**
     * 상점 계정을 멈추거나(deactivate), 다시 열거나(activate), 임시 비밀번호를 새로 낸다(reset).
     * 멈추거나 비밀번호를 새로 내면 그 계정의 접속은 모두 끊긴다.
     * @return array [문제 목록, 임시 비밀번호 또는 null]
     */
    public function merchantUserAction(array $admin, $userId, $action, $ip = '')
    {
        self::need($admin, Auth::PASS_MANAGE);
        if (!in_array($action, ['deactivate', 'activate', 'reset'], true)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '할 수 없는 일입니다.');
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $userId, $action, $ip) {
            $user = $db->one('SELECT * FROM merchant_users WHERE id = ? FOR UPDATE', [(int)$userId]);
            if ($user === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 상점 계정입니다.');
            }
            $password = null;
            if ($action === 'reset') {
                $password = self::temporaryPassword();
                $db->exec('UPDATE merchant_users SET password_hash = ?, must_change_password = 1, failed_attempts = 0, locked_until = NULL WHERE id = ?',
                    [password_hash($password, PASSWORD_DEFAULT), $user['id']]);
            } else {
                $db->exec('UPDATE merchant_users SET is_active = ? WHERE id = ?', [$action === 'activate' ? 1 : 0, $user['id']]);
            }
            if ($action !== 'activate') {
                $db->exec('UPDATE merchant_sessions SET revoked_at = ? WHERE merchant_user_id = ? AND revoked_at IS NULL', [Clock::sql(), $user['id']]);
            }
            $this->auth->audit($admin['id'], 'pass.merchant_user.' . $action, 'merchant_user', (string)$user['id'], $user['login'], $ip);
            return [[], $password];
        });
    }

    // ── 쿠폰, 사용 내역 ─────────────────────────────────────

    /** 쿠폰 목록. 회원은 회원번호로만 보인다 (닉네임과 계정 번호를 붙여 보여 주지 않는다). */
    public function coupons(array $admin, $status = null, $offerId = null, $page = 1)
    {
        self::need($admin, Auth::PASS_VIEW);
        $where = [];
        $params = [];
        if ($status !== null && $status !== '') {
            $where[] = 'c.status = ?';
            $params[] = $status;
        }
        if ($offerId !== null && $offerId !== '') {
            $where[] = 'c.offer_id = ?';
            $params[] = (int)$offerId;
        }
        return $this->app->db->all(
            'SELECT c.id, c.public_id, c.status, c.issue_reason, c.issued_at, c.expires_at, c.redeemed_at, c.cancelled_reason, o.title, m.name AS merchant_name, s.member_no'
            . ' FROM benefit_coupons c JOIN partner_offers o ON o.id = c.offer_id JOIN partner_merchants m ON m.id = c.merchant_id'
            . ' JOIN city_pass_memberships s ON s.id = c.membership_id' . ($where ? ' WHERE ' . implode(' AND ', $where) : '')
            . ' ORDER BY c.id DESC LIMIT 100 OFFSET ' . (max(1, (int)$page) - 1) * 100, $params);
    }

    /** 아직 쓰지 않은 쿠폰을 취소한다 (잘못 발급했거나 부정 사용이 의심될 때). @return string[] 문제 목록 */
    public function cancelCoupon(array $admin, $couponId, $reason, $ip = '')
    {
        self::need($admin, Auth::PASS_MANAGE);
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        if (!$form->ok()) {
            return $form->problems;
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $couponId, $reason, $ip) {
            $coupon = $db->one('SELECT id, merchant_id, player_id FROM benefit_coupons WHERE id = ?', [(int)$couponId]);
            if ($coupon === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 쿠폰입니다.');
            }
            // 쓴 쿠폰은 취소할 수 없다: 상태가 issued 일 때만 바뀐다.
            $changed = $db->exec("UPDATE benefit_coupons SET status = 'cancelled', cancelled_reason = ?, present_code_hash = NULL, present_expires_at = NULL"
                . " WHERE id = ? AND status = 'issued'", [$reason, $coupon['id']]);
            if ($changed !== 1) {
                return ['아직 쓰지 않은 쿠폰만 취소할 수 있습니다.'];
            }
            $db->exec('INSERT INTO coupon_events (coupon_id, event, merchant_id, player_id, detail, created_at) VALUES (?, ?, ?, ?, ?, ?)',
                [$coupon['id'], 'cancelled', $coupon['merchant_id'], $coupon['player_id'], mb_substr($reason, 0, 190), Clock::sql()]);
            $this->auth->audit($admin['id'], 'pass.coupon.cancel', 'benefit_coupon', (string)$coupon['id'], $reason, $ip);
            return [];
        });
    }

    public function redemptions(array $admin, $page = 1)
    {
        self::need($admin, Auth::PASS_VIEW);
        return $this->app->db->all(
            'SELECT r.id, r.redeemed_at, c.public_id, o.title, m.name AS merchant_name, b.name AS branch_name, u.login AS staff_login, s.member_no'
            . ' FROM coupon_redemptions r JOIN benefit_coupons c ON c.id = r.coupon_id JOIN partner_offers o ON o.id = r.offer_id'
            . ' JOIN partner_merchants m ON m.id = r.merchant_id LEFT JOIN partner_branches b ON b.id = r.branch_id'
            . ' JOIN merchant_users u ON u.id = r.merchant_user_id JOIN city_pass_memberships s ON s.id = r.membership_id'
            . ' ORDER BY r.id DESC LIMIT 100 OFFSET ' . (max(1, (int)$page) - 1) * 100);
    }

    // ── 통계, 부정 사용 탐지 ────────────────────────────────

    public function stats(array $admin)
    {
        self::need($admin, Auth::PASS_VIEW);
        $db = $this->app->db;
        $byStatus = ['issued' => 0, 'redeemed' => 0, 'expired' => 0, 'cancelled' => 0];
        foreach ($db->all('SELECT status, COUNT(*) AS n FROM benefit_coupons GROUP BY status') as $row) {
            $byStatus[$row['status']] = (int)$row['n'];
        }
        return [
            'members' => (int)$db->val("SELECT COUNT(*) FROM city_pass_memberships WHERE status = 'active'"),
            'merchants' => (int)$db->val("SELECT COUNT(*) FROM partner_merchants WHERE status = 'approved'"),
            'offers' => (int)$db->val("SELECT COUNT(*) FROM partner_offers WHERE status = 'approved'"),
            'coupons' => $byStatus,
            'byOffer' => $db->all(
                'SELECT o.id, o.title, m.name AS merchant_name, o.status, o.issued_count,'
                . " SUM(c.status = 'redeemed') AS redeemed, SUM(c.status = 'expired') AS expired, SUM(c.status = 'cancelled') AS cancelled"
                . ' FROM partner_offers o JOIN partner_merchants m ON m.id = o.merchant_id LEFT JOIN benefit_coupons c ON c.offer_id = o.id'
                . ' GROUP BY o.id, o.title, m.name, o.status, o.issued_count ORDER BY o.id DESC LIMIT 200'),
            'byRegion' => $db->all(
                'SELECT s.name AS sido_name, g.name AS region_name, COUNT(*) AS n FROM coupon_redemptions r'
                . ' JOIN partner_branches b ON b.id = r.branch_id JOIN regions g ON g.id = b.region_id JOIN sidos s ON s.id = g.sido_id'
                . ' GROUP BY s.name, g.name ORDER BY n DESC LIMIT 50'),
            'daily' => $db->all(
                'SELECT DATE(DATE_ADD(redeemed_at, INTERVAL 9 HOUR)) AS day, COUNT(*) AS n FROM coupon_redemptions WHERE redeemed_at >= ?'
                . ' GROUP BY day ORDER BY day DESC', [Clock::sql(Clock::now() - 14 * 86400)]),
        ];
    }

    /**
     * 부정 사용이 의심되는 신호 (최근 24시간). 판정이 아니라 사람이 들여다볼 목록이다.
     *   - 쿠폰 코드를 여러 번 틀린 상점 계정 (코드를 추측하고 있을 수 있다)
     *   - 짧은 시간에 쿠폰을 몰아서 처리한 상점 계정
     *   - 코드를 여러 번 만들고 쓰지 않은 회원
     */
    public function fraudSignals(array $admin)
    {
        self::need($admin, Auth::PASS_VIEW);
        $db = $this->app->db;
        $since = Clock::sql(Clock::now() - 86400);
        return [
            'failedVerify' => $db->all(
                "SELECT u.login, m.name AS merchant_name, COUNT(*) AS n, MAX(e.created_at) AS last_at FROM coupon_events e"
                . ' JOIN merchant_users u ON u.id = e.merchant_user_id JOIN partner_merchants m ON m.id = u.merchant_id'
                . " WHERE e.event = 'verify_failed' AND e.created_at >= ? GROUP BY u.login, m.name HAVING COUNT(*) >= 5 ORDER BY n DESC LIMIT 50", [$since]),
            'burst' => $db->all(
                'SELECT u.login, m.name AS merchant_name, COUNT(*) AS n, MIN(r.redeemed_at) AS first_at, MAX(r.redeemed_at) AS last_at'
                . ' FROM coupon_redemptions r JOIN merchant_users u ON u.id = r.merchant_user_id JOIN partner_merchants m ON m.id = r.merchant_id'
                . ' WHERE r.redeemed_at >= ? GROUP BY u.login, m.name, FLOOR(UNIX_TIMESTAMP(r.redeemed_at) / 600) HAVING COUNT(*) >= 20 ORDER BY n DESC LIMIT 50', [$since]),
            'presentOnly' => $db->all(
                "SELECT s.member_no, COUNT(*) AS n FROM coupon_events e JOIN city_pass_memberships s ON s.player_id = e.player_id"
                . " WHERE e.event = 'presented' AND e.created_at >= ? GROUP BY s.member_no HAVING COUNT(*) >= 15 ORDER BY n DESC LIMIT 50", [$since]),
        ];
    }
}
