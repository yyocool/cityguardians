<?php
namespace CG\Service;

use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * CITY PASS: 이용자 쪽 (docs/CITY_PASS_SYSTEM.md, docs/COUPON_SYSTEM.md).
 *
 * - 멤버십에는 실제 개인정보가 없다. 회원번호와 관심 지역뿐이다. 관심 지역은 실제 거주지가 아니고, 인증하지 않는다.
 * - 쿠폰은 가입, 무료 프로모션, 제휴사가 공개한 조건으로만 발급된다. GC·전투 보상·랭킹과 이어지지 않는다:
 *   이 서비스는 Wallets 를 부르지 않는다.
 * - 쿠폰을 쓰는 것은 상점 쪽 화면(MerchantPortal)이 서버에서 처리한다. 이용자는 5분짜리 일회용 코드를 받아 보여 줄 뿐이다.
 *   코드와 QR 에는 개인정보가 없다.
 */
final class CityPass
{
    /** 이용 약관의 판. 약관은 초안이다 (법률 검토 전). */
    const TERMS_VERSION = '2026-11-draft';
    /** 제시용 코드가 살아 있는 시간(초). */
    const PRESENT_SECONDS = 300;
    const PRESENT_CODE_LENGTH = 10;

    const ISSUED = 'issued';
    const REDEEMED = 'redeemed';
    const EXPIRED = 'expired';
    const CANCELLED = 'cancelled';

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    public static function categories()
    {
        return ['cafe' => '카페', 'restaurant' => '음식점', 'fitness' => '헬스장', 'culture' => '문화시설',
            'education' => '교육시설', 'retail' => '판매점', 'service' => '생활 서비스'];
    }

    public static function discountTypes()
    {
        return ['percent' => '할인율(%)', 'amount' => '할인 금액(원)', 'free_trial' => '무료 체험', 'gift' => '사은품', 'special' => '그 밖의 혜택'];
    }

    /** 쿠폰을 발급하는 근거. 게임 성적이나 게임머니는 여기에 없고, 넣을 수 없다. */
    public static function issuePolicies()
    {
        return ['membership' => 'CITY PASS 가입', 'promotion' => '무료 프로모션', 'partner_condition' => '제휴사가 공개한 조건'];
    }

    public static function discountText($type, $value)
    {
        switch ($type) {
            case 'percent': return (int)$value . '% 할인';
            case 'amount': return number_format((int)$value) . '원 할인';
            case 'free_trial': return '무료 체험';
            case 'gift': return '사은품';
            default: return '특별 혜택';
        }
    }

    /** 제시용 코드: 붙임표와 빈칸을 떼고 대문자로 맞춘다. */
    public static function normalizeCode($code)
    {
        return is_string($code) ? strtoupper(preg_replace('/[^A-Za-z0-9]/', '', $code)) : '';
    }

    public static function hashCode($normalized)
    {
        return Ids::sha256('cgpass:' . $normalized);
    }

    // ── 멤버십 ──────────────────────────────────────────────

    private function membership($playerId, $lock = false)
    {
        return $this->app->db->one('SELECT * FROM city_pass_memberships WHERE player_id = ?' . ($lock ? ' FOR UPDATE' : ''), [$playerId]);
    }

    private function region($id)
    {
        if ($id === null) {
            return null;
        }
        $row = $this->app->db->one('SELECT r.code, r.name, s.name AS sido_name FROM regions r JOIN sidos s ON s.id = r.sido_id WHERE r.id = ?', [$id]);
        return $row === null ? null : ['code' => $row['code'], 'name' => $row['name'], 'sidoName' => $row['sido_name']];
    }

    private function regionIdOf($code)
    {
        if (!is_string($code) || !preg_match('/^[0-9]{1,8}$/', $code)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '지역을 다시 골라 주세요.');
        }
        $id = $this->app->db->val('SELECT id FROM regions WHERE code = ?', [$code]);
        if ($id === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
        }
        return (int)$id;
    }

    public function profile(array $player)
    {
        $features = $this->app->features();
        $membership = $this->membership($player['id']);
        $out = [
            'membershipEnabled' => $features->isEnabled(Features::PASS_MEMBERSHIP),
            'couponsEnabled' => $features->isEnabled(Features::PASS_COUPONS),
            'isMember' => $membership !== null && $membership['status'] !== 'withdrawn',
            'memberNo' => null,
            'status' => null,
            // 화면에는 게임 닉네임만 보인다. 실제 이름은 받지도, 저장하지도 않는다.
            'nickname' => $player['nickname'] !== null ? $player['nickname'] : '이방인',
            'interestRegion' => null,
            'joinedAt' => null,
            'termsVersion' => self::TERMS_VERSION,
            'coupons' => ['available' => 0, 'used' => 0, 'expired' => 0],
            'notice' => $features->isEnabled(Features::PASS_MEMBERSHIP) ? '' : 'CITY PASS 는 준비 중이에요. 제휴와 운영 준비가 끝나면 열려요.',
        ];
        if ($out['isMember']) {
            $out['memberNo'] = $membership['member_no'];
            $out['status'] = $membership['status'];
            $out['interestRegion'] = $this->region($membership['interest_region_id']);
            $out['joinedAt'] = Clock::isoFromSql($membership['joined_at']);
            foreach ($this->app->db->all(
                'SELECT status, expires_at FROM benefit_coupons WHERE membership_id = ?', [$membership['id']]) as $coupon) {
                $status = self::effectiveStatus($coupon);
                if ($status === self::ISSUED) {
                    $out['coupons']['available']++;
                } elseif ($status === self::REDEEMED) {
                    $out['coupons']['used']++;
                } elseif ($status === self::EXPIRED) {
                    $out['coupons']['expired']++;
                }
            }
        }
        return $out;
    }

    /** 가입한다. 이미 가입했으면 그대로 돌려준다. */
    public function join(array $player, $agreeTerms, $interestRegionCode = null)
    {
        $this->app->features()->mustBeEnabled(Features::PASS_MEMBERSHIP);
        if ($agreeTerms !== true) {
            throw new ApiError(ApiError::VALIDATION_FAILED, '이용 약관에 동의해야 가입할 수 있어요.', 'terms_required');
        }
        // 관심 지역의 기본값은 전입신고한 지역이다. 다른 지역을 골라도 된다 (실제 거주지를 확인하지 않는다).
        $regionId = $interestRegionCode !== null && $interestRegionCode !== ''
            ? $this->regionIdOf($interestRegionCode) : $player['regionId'];
        $db = $this->app->db;
        $db->tx(function () use ($db, $player, $regionId) {
            $existing = $this->membership($player['id'], true);
            if ($existing !== null && $existing['status'] !== 'withdrawn') {
                return;
            }
            if ($existing !== null) {
                $db->exec("UPDATE city_pass_memberships SET status = 'active', interest_region_id = ?, terms_version = ?, updated_at = ? WHERE id = ?",
                    [$regionId, self::TERMS_VERSION, Clock::sql(), $existing['id']]);
                return;
            }
            for ($attempt = 0; ; $attempt++) {
                $token = strtoupper(Ids::token(8));
                $memberNo = 'CP-' . substr($token, 0, 4) . '-' . substr($token, 4);
                if ($db->val('SELECT COUNT(*) FROM city_pass_memberships WHERE member_no = ?', [$memberNo]) == 0 || $attempt >= 5) {
                    break;
                }
            }
            $db->exec('INSERT INTO city_pass_memberships (player_id, member_no, interest_region_id, status, terms_version, joined_at, updated_at)'
                . " VALUES (?, ?, ?, 'active', ?, ?, ?)", [$player['id'], $memberNo, $regionId, self::TERMS_VERSION, Clock::sql(), Clock::sql()]);
        });
        return $this->profile($player);
    }

    public function setInterestRegion(array $player, $regionCode)
    {
        $regionId = $this->regionIdOf($regionCode);
        $membership = $this->membership($player['id']);
        if ($membership === null || $membership['status'] !== 'active') {
            throw new ApiError(ApiError::FORBIDDEN, '먼저 CITY PASS 에 가입해 주세요.', 'not_member');
        }
        $this->app->db->exec('UPDATE city_pass_memberships SET interest_region_id = ?, updated_at = ? WHERE id = ?',
            [$regionId, Clock::sql(), $membership['id']]);
        return $this->profile($player);
    }

    // ── 혜택 ────────────────────────────────────────────────

    /** 밖에 보여도 되는 혜택의 조건: 승인된 업체(계약 기간 안)의 승인된 혜택이고, 기간 안이다. */
    private static function visibleSql()
    {
        return "o.status = 'approved' AND o.starts_at <= ? AND o.ends_at > ? AND m.status = 'approved'"
            . ' AND m.contract_starts_on <= ? AND m.contract_ends_on >= ?';
    }

    /** visibleSql() 의 물음표에 들어갈 값. */
    private static function visibleParams()
    {
        return [Clock::sql(), Clock::sql(), Clock::koreaDate(), Clock::koreaDate()];
    }

    private function branchesOf(array $merchantIds)
    {
        if (!$merchantIds) {
            return [];
        }
        $marks = implode(',', array_fill(0, count($merchantIds), '?'));
        $out = [];
        foreach ($this->app->db->all(
            'SELECT b.id, b.public_id, b.merchant_id, b.name, b.address, b.phone, b.lat, b.lng, b.image_file, r.code AS region_code, r.name AS region_name,'
            . ' s.name AS sido_name FROM partner_branches b JOIN regions r ON r.id = b.region_id JOIN sidos s ON s.id = r.sido_id'
            . " WHERE b.is_active = 1 AND b.merchant_id IN ($marks) ORDER BY b.id", array_values($merchantIds)) as $row) {
            $out[(int)$row['merchant_id']][] = [
                'internalId' => (int)$row['id'],
                'id' => $row['public_id'], 'name' => $row['name'], 'regionCode' => $row['region_code'],
                'regionName' => $row['sido_name'] . ' ' . $row['region_name'], 'address' => $row['address'], 'phone' => $row['phone'],
                'lat' => $row['lat'] !== null ? (float)$row['lat'] : null, 'lng' => $row['lng'] !== null ? (float)$row['lng'] : null,
                'imagePath' => $row['image_file'] !== null && $row['image_file'] !== '' ? '/media/' . $row['image_file'] : null,
            ];
        }
        return $out;
    }

    private static function publicBranches(array $branches)
    {
        return array_map(function ($branch) {
            unset($branch['internalId']);
            return $branch;
        }, array_values($branches));
    }

    /**
     * 지금 받을 수 있는 혜택. 쿠폰 기능이 꺼져 있으면 아무것도 보이지 않는다
     * (준비되지 않은 혜택을 제공 중인 것처럼 보이지 않는다).
     *
     * @param array|null $player 로그인하지 않은 홈페이지 방문자면 null
     * @param string|null $regionCode 이 지역에 지점이 있는 혜택만. null 이면 전국
     */
    public function offers($player = null, $regionCode = null, $category = null)
    {
        $features = $this->app->features();
        $enabled = $features->isEnabled(Features::PASS_COUPONS);
        $out = ['couponsEnabled' => $enabled, 'regionCode' => $regionCode, 'offers' => [],
            'notice' => $enabled ? '' : '제휴 혜택은 준비 중이에요.'];
        if (!$enabled) {
            return $out;
        }
        if ($regionCode !== null) {
            $this->regionIdOf($regionCode);
        }
        if ($category !== null && !isset(self::categories()[$category])) {
            throw new ApiError(ApiError::INVALID_REQUEST, '업종을 다시 골라 주세요.');
        }
        $db = $this->app->db;
        $rows = $db->all(
            'SELECT o.*, m.public_id AS merchant_public_id, m.name AS merchant_name, m.category FROM partner_offers o'
            . ' JOIN partner_merchants m ON m.id = o.merchant_id WHERE ' . self::visibleSql()
            . ($category !== null ? ' AND m.category = ?' : '') . ' ORDER BY o.ends_at, o.id LIMIT 300',
            array_merge(self::visibleParams(), $category !== null ? [$category] : [])
        );
        $branches = $this->branchesOf(array_unique(array_map(function ($row) {
            return (int)$row['merchant_id'];
        }, $rows)));
        $membership = $player !== null ? $this->membership($player['id']) : null;
        $member = $membership !== null && $membership['status'] === 'active';
        $mine = [];
        if ($member) {
            foreach ($db->all("SELECT offer_id, COUNT(*) AS n FROM benefit_coupons WHERE membership_id = ? AND status <> 'cancelled' GROUP BY offer_id",
                [$membership['id']]) as $row) {
                $mine[(int)$row['offer_id']] = (int)$row['n'];
            }
        }
        foreach ($rows as $row) {
            $where = isset($branches[(int)$row['merchant_id']]) ? $branches[(int)$row['merchant_id']] : [];
            if ($row['branch_id'] !== null) {
                $where = array_filter($where, function ($branch) use ($row) {
                    return $branch['internalId'] === (int)$row['branch_id'];
                });
            }
            if ($regionCode !== null) {
                $where = array_filter($where, function ($branch) use ($regionCode) {
                    return $branch['regionCode'] === $regionCode;
                });
            }
            if (!$where) {
                continue;   // 쓸 수 있는 지점이 없는 혜택은 보이지 않는다
            }
            $claimed = isset($mine[(int)$row['id']]) ? $mine[(int)$row['id']] : 0;
            $remaining = $row['total_limit'] !== null ? max(0, (int)$row['total_limit'] - (int)$row['issued_count']) : null;
            $block = null;
            if (!$member) {
                $block = 'not_member';
            } elseif ($claimed >= (int)$row['per_member_limit']) {
                $block = 'limit_reached';
            } elseif ($remaining === 0) {
                $block = 'sold_out';
            }
            $out['offers'][] = [
                'id' => $row['public_id'], 'title' => $row['title'], 'description' => $row['description'],
                'discountType' => $row['discount_type'], 'discountValue' => (int)$row['discount_value'],
                'discountText' => self::discountText($row['discount_type'], $row['discount_value']),
                'conditions' => $row['conditions'], 'issuePolicy' => $row['issue_policy'],
                'startsAt' => Clock::isoFromSql($row['starts_at']), 'endsAt' => Clock::isoFromSql($row['ends_at']),
                'couponValidDays' => (int)$row['coupon_valid_days'], 'perMemberLimit' => (int)$row['per_member_limit'],
                'remaining' => $remaining,
                'merchant' => ['id' => $row['merchant_public_id'], 'name' => $row['merchant_name'], 'category' => $row['category'],
                    'categoryName' => self::categories()[$row['category']]],
                'branches' => self::publicBranches($where),
                'myCoupons' => $claimed, 'claimable' => $block === null, 'claimBlock' => $block,
            ];
        }
        return $out;
    }

    /** 제휴 상점 목록 (홈페이지). 승인되고 계약 기간 안인 업체의 지점만 보인다. */
    public function merchants($regionCode = null, $category = null)
    {
        $enabled = $this->app->features()->isEnabled(Features::PASS_COUPONS);
        $out = ['couponsEnabled' => $enabled, 'merchants' => []];
        if (!$enabled) {
            return $out;
        }
        if ($regionCode !== null) {
            $this->regionIdOf($regionCode);
        }
        if ($category !== null && !isset(self::categories()[$category])) {
            throw new ApiError(ApiError::INVALID_REQUEST, '업종을 다시 골라 주세요.');
        }
        $rows = $this->app->db->all(
            "SELECT m.id, m.public_id, m.name, m.category FROM partner_merchants m WHERE m.status = 'approved'"
            . ' AND m.contract_starts_on <= ? AND m.contract_ends_on >= ? ORDER BY m.name LIMIT 500', [Clock::koreaDate(), Clock::koreaDate()]);
        $branches = $this->branchesOf(array_map(function ($row) {
            return (int)$row['id'];
        }, $rows));
        foreach ($rows as $row) {
            if ($category !== null && $row['category'] !== $category) {
                continue;
            }
            $where = isset($branches[(int)$row['id']]) ? $branches[(int)$row['id']] : [];
            if ($regionCode !== null) {
                $where = array_filter($where, function ($branch) use ($regionCode) {
                    return $branch['regionCode'] === $regionCode;
                });
            }
            if ($where) {
                $out['merchants'][] = ['id' => $row['public_id'], 'name' => $row['name'], 'category' => $row['category'],
                    'categoryName' => self::categories()[$row['category']], 'branches' => self::publicBranches($where)];
            }
        }
        return $out;
    }

    // ── 쿠폰 ────────────────────────────────────────────────

    /** 저장된 상태에 유효기간을 반영한 상태. */
    public static function effectiveStatus(array $coupon)
    {
        if ($coupon['status'] === self::ISSUED && Clock::parse($coupon['expires_at']) <= Clock::now()) {
            return self::EXPIRED;
        }
        return $coupon['status'];
    }

    private function couponView(array $row, array $branches)
    {
        $where = isset($branches[(int)$row['merchant_id']]) ? $branches[(int)$row['merchant_id']] : [];
        if ($row['offer_branch_id'] !== null) {
            $where = array_filter($where, function ($branch) use ($row) {
                return $branch['internalId'] === (int)$row['offer_branch_id'];
            });
        }
        $status = self::effectiveStatus($row);
        return [
            'id' => $row['public_id'], 'status' => $status,
            'issuedAt' => Clock::isoFromSql($row['issued_at']), 'expiresAt' => Clock::isoFromSql($row['expires_at']),
            'redeemedAt' => Clock::isoFromSql($row['redeemed_at']),
            'offer' => ['id' => $row['offer_public_id'], 'title' => $row['title'], 'description' => $row['description'],
                'discountText' => self::discountText($row['discount_type'], $row['discount_value']), 'conditions' => $row['conditions']],
            'merchant' => ['name' => $row['merchant_name'], 'categoryName' => self::categories()[$row['category']]],
            'branches' => self::publicBranches($where),
            // 지금 상점에 보여 줄 수 있는가
            'usable' => $status === self::ISSUED && $row['merchant_status'] === 'approved'
                && $this->app->features()->isEnabled(Features::PASS_COUPONS),
        ];
    }

    private static function couponSql()
    {
        return 'SELECT c.*, o.public_id AS offer_public_id, o.title, o.description, o.discount_type, o.discount_value, o.conditions,'
            . ' o.branch_id AS offer_branch_id, m.name AS merchant_name, m.category, m.status AS merchant_status'
            . ' FROM benefit_coupons c JOIN partner_offers o ON o.id = c.offer_id JOIN partner_merchants m ON m.id = c.merchant_id';
    }

    /** 내 쿠폰. 기능이 꺼져 있어도 이미 받은 쿠폰의 목록은 본인이 볼 수 있다 (쓸 수는 없다). */
    public function coupons(array $player)
    {
        $rows = $this->app->db->all(self::couponSql() . " WHERE c.player_id = ? AND c.status <> 'cancelled' ORDER BY c.id DESC LIMIT 200", [$player['id']]);
        $branches = $this->branchesOf(array_unique(array_map(function ($row) {
            return (int)$row['merchant_id'];
        }, $rows)));
        $enabled = $this->app->features()->isEnabled(Features::PASS_COUPONS);
        return [
            'couponsEnabled' => $enabled,
            'coupons' => array_map(function ($row) use ($branches) {
                return $this->couponView($row, $branches);
            }, $rows),
            'notice' => $enabled ? '' : '쿠폰은 준비 중이에요. 지금은 쓸 수 없어요.',
        ];
    }

    private function event($couponId, $event, array $more = [])
    {
        $this->app->db->exec(
            'INSERT INTO coupon_events (coupon_id, event, merchant_id, merchant_user_id, player_id, detail, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$couponId, $event, isset($more['merchantId']) ? $more['merchantId'] : null, isset($more['merchantUserId']) ? $more['merchantUserId'] : null,
                isset($more['playerId']) ? $more['playerId'] : null, isset($more['detail']) ? mb_substr($more['detail'], 0, 190) : null, Clock::sql()]
        );
    }

    /**
     * 혜택의 쿠폰을 받는다. 값은 치르지 않는다: 가입한 회원이면 받을 수 있다.
     * 같은 요청 번호로 다시 오면 처음 응답을 돌려준다 (두 장이 생기지 않는다).
     */
    public function claim(array $player, $offerPublicId, $key)
    {
        $this->app->features()->mustBeEnabled(Features::PASS_COUPONS);
        if (!is_string($offerPublicId) || !preg_match('/^[a-z0-9_]{16}$/', $offerPublicId)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '혜택을 다시 골라 주세요.');
        }
        $db = $this->app->db;
        return $this->app->idempotency()->run($player['id'], $key, 'city_pass.claim', ['offerId' => $offerPublicId],
            function () use ($db, $player, $offerPublicId) {
                $membership = $this->membership($player['id'], true);
                if ($membership === null || $membership['status'] !== 'active') {
                    throw new ApiError(ApiError::FORBIDDEN, '먼저 CITY PASS 에 가입해 주세요.', 'not_member');
                }
                $offer = $db->one('SELECT o.* FROM partner_offers o JOIN partner_merchants m ON m.id = o.merchant_id WHERE o.public_id = ? AND '
                    . self::visibleSql() . ' FOR UPDATE', array_merge([$offerPublicId], self::visibleParams()));
                if ($offer === null) {
                    throw new ApiError(ApiError::NOT_FOUND, '지금은 받을 수 없는 혜택이에요.');
                }
                $mine = (int)$db->val("SELECT COUNT(*) FROM benefit_coupons WHERE offer_id = ? AND membership_id = ? AND status <> 'cancelled'",
                    [$offer['id'], $membership['id']]);
                if ($mine >= (int)$offer['per_member_limit']) {
                    throw new ApiError(ApiError::LIMIT_REACHED, '이 혜택의 쿠폰은 이미 받았어요.', 'limit_reached');
                }
                if ($offer['total_limit'] !== null && (int)$offer['issued_count'] >= (int)$offer['total_limit']) {
                    throw new ApiError(ApiError::LIMIT_REACHED, '준비한 쿠폰이 모두 나갔어요.', 'sold_out');
                }
                $now = Clock::now();
                $expires = min($now + (int)$offer['coupon_valid_days'] * 86400, Clock::parse($offer['ends_at']));
                $publicId = Ids::publicId('c_');
                $id = $db->insert('INSERT INTO benefit_coupons (public_id, offer_id, merchant_id, membership_id, player_id, status, issue_reason, issued_at, expires_at)'
                    . " VALUES (?, ?, ?, ?, ?, 'issued', ?, ?, ?)",
                    [$publicId, $offer['id'], $offer['merchant_id'], $membership['id'], $player['id'], $offer['issue_policy'], Clock::sql($now), Clock::sql($expires)]);
                $db->exec('UPDATE partner_offers SET issued_count = issued_count + 1 WHERE id = ?', [$offer['id']]);
                $this->event($id, 'issued', ['merchantId' => (int)$offer['merchant_id'], 'playerId' => $player['id'], 'detail' => $offer['issue_policy']]);
                return ['coupon' => $this->couponOf($player, $publicId)];
            },
            // 줄은 멤버십 행에서 선다. 지갑 행은 잠그지도 읽지도 않는다.
            function () use ($player) {
                $this->membership($player['id'], true);
            });
    }

    private function couponOf(array $player, $publicId)
    {
        $row = $this->app->db->one(self::couponSql() . ' WHERE c.public_id = ? AND c.player_id = ?', [$publicId, $player['id']]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 쿠폰이에요.');
        }
        return $this->couponView($row, $this->branchesOf([(int)$row['merchant_id']]));
    }

    /**
     * 쿠폰을 상점에 보여 줄 일회용 코드를 만든다. 쿠폰이 "사용됨"으로 바뀌는 것은 상점이 확인했을 때다.
     *
     * 코드는 5분 동안만 살아 있고, 다시 만들면 앞의 코드는 못 쓴다. 서버에는 해시만 남는다.
     * QR 에 들어가는 글자에는 회원번호·닉네임·쿠폰 번호가 없다: 그 일회용 코드뿐이다.
     */
    public function present(array $player, $couponPublicId)
    {
        $this->app->features()->mustBeEnabled(Features::PASS_COUPONS);
        if (!is_string($couponPublicId) || !preg_match('/^[a-z0-9_]{16}$/', $couponPublicId)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '쿠폰을 다시 골라 주세요.');
        }
        $this->app->rateLimit()->hit('pass_present', 'p' . $player['id'], 30, 600);
        $db = $this->app->db;
        $code = strtoupper(Ids::token(self::PRESENT_CODE_LENGTH));
        $expires = $db->tx(function () use ($db, $player, $couponPublicId, $code) {
            $coupon = $db->one('SELECT c.*, m.status AS merchant_status FROM benefit_coupons c JOIN partner_merchants m ON m.id = c.merchant_id'
                . ' WHERE c.public_id = ? AND c.player_id = ? FOR UPDATE', [$couponPublicId, $player['id']]);
            if ($coupon === null || $coupon['status'] === self::CANCELLED) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 쿠폰이에요.');
            }
            $status = self::effectiveStatus($coupon);
            if ($status === self::REDEEMED) {
                throw new ApiError(ApiError::CONFLICT, '이미 쓴 쿠폰이에요.', 'already_redeemed');
            }
            if ($status === self::EXPIRED) {
                throw new ApiError(ApiError::VALIDATION_FAILED, '유효기간이 지난 쿠폰이에요.', 'expired');
            }
            if ($coupon['merchant_status'] !== 'approved') {
                throw new ApiError(ApiError::VALIDATION_FAILED, '지금은 이 상점에서 쓸 수 없어요.', 'merchant_unavailable');
            }
            // 쿠폰의 유효기간보다 오래 살아 있지 않는다.
            $expires = min(Clock::now() + self::PRESENT_SECONDS, Clock::parse($coupon['expires_at']));
            $db->exec('UPDATE benefit_coupons SET present_code_hash = ?, present_expires_at = ? WHERE id = ?',
                [self::hashCode($code), Clock::sql($expires), $coupon['id']]);
            $this->event((int)$coupon['id'], 'presented', ['merchantId' => (int)$coupon['merchant_id'], 'playerId' => $player['id']]);
            return $expires;
        });
        $shown = substr($code, 0, 5) . '-' . substr($code, 5);
        $base = $this->app->publicBase();
        return [
            'couponId' => $couponPublicId,
            'code' => $shown,
            // 상점 직원이 휴대전화로 찍으면 상점 확인 화면이 열린다 (로그인해야 하고, 열기만 해서는 쓰이지 않는다).
            'qrPayload' => $base !== '' ? $base . '/merchant/redeem?code=' . $shown : 'CGPASS:' . $shown,
            'expiresAt' => Clock::iso($expires),
            'expiresInSeconds' => $expires - Clock::now(),
        ];
    }

    /** 이용 내역: 쓴 쿠폰. */
    public function history(array $player)
    {
        $rows = $this->app->db->all(
            'SELECT c.public_id, r.redeemed_at, o.title, o.discount_type, o.discount_value, m.name AS merchant_name, b.name AS branch_name'
            . ' FROM coupon_redemptions r JOIN benefit_coupons c ON c.id = r.coupon_id JOIN partner_offers o ON o.id = r.offer_id'
            . ' JOIN partner_merchants m ON m.id = r.merchant_id LEFT JOIN partner_branches b ON b.id = r.branch_id'
            . ' WHERE c.player_id = ? ORDER BY r.id DESC LIMIT 200', [$player['id']]);
        return ['history' => array_map(function ($row) {
            return ['couponId' => $row['public_id'], 'offerTitle' => $row['title'],
                'discountText' => self::discountText($row['discount_type'], $row['discount_value']),
                'merchantName' => $row['merchant_name'], 'branchName' => $row['branch_name'], 'redeemedAt' => Clock::isoFromSql($row['redeemed_at'])];
        }, $rows)];
    }

    /** 유효기간이 지난 쿠폰을 만료로 적는다. 예약 작업이 부른다. */
    public function expireDue($limit = 500)
    {
        $db = $this->app->db;
        $count = 0;
        foreach ($db->all("SELECT id, merchant_id, player_id FROM benefit_coupons WHERE status = 'issued' AND expires_at <= ? ORDER BY id LIMIT " . (int)$limit,
            [Clock::sql()]) as $row) {
            $count += $db->tx(function () use ($db, $row) {
                $changed = $db->exec("UPDATE benefit_coupons SET status = 'expired', present_code_hash = NULL, present_expires_at = NULL"
                    . " WHERE id = ? AND status = 'issued' AND expires_at <= ?", [$row['id'], Clock::sql()]);
                if ($changed === 1) {
                    $this->event((int)$row['id'], 'expired', ['merchantId' => (int)$row['merchant_id'], 'playerId' => (int)$row['player_id']]);
                }
                return $changed;
            });
        }
        return $count;
    }
}
