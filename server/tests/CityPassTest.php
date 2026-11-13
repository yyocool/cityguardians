<?php
use CG\Admin\Auth;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Service\CityPass;
use CG\Service\Features;
use CG\Service\MerchantPortal;

/**
 * CITY PASS: 멤버십, 제휴 업체와 혜택의 승인, 쿠폰 발급·제시·사용 처리, 상점 계정의 범위 (docs/COUPON_SYSTEM.md).
 */
final class CityPassTest extends BenefitTestCase
{
    // ── 꺼져 있을 때 ────────────────────────────────────────

    public function testNothingWorksAndNothingIsShownWhileTheFeaturesAreOff()
    {
        $player = $this->resident();
        $pass = $this->app->cityPass();
        $profile = $pass->profile($player);
        T::ok(!$profile['membershipEnabled'] && !$profile['couponsEnabled'] && !$profile['isMember']);
        T::ok($profile['notice'] !== '');
        T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($pass, $player) {
            $pass->join($player, true);
        });
        T::eq([], $pass->offers($player)['offers']);
        T::eq([], $pass->merchants()['merchants']);
        T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($pass, $player) {
            $pass->claim($player, 'o_aaaaaaaaaaaaaa', $this->key());
        });
        T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($pass, $player) {
            $pass->present($player, 'c_aaaaaaaaaaaaaa');
        });
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_pass_memberships'));
    }

    public function testThereAreNoSeededMerchantsOffersOrCoupons()
    {
        // 실제 제휴가 없는데 제휴 상점이 있는 것처럼 보이면 안 된다.
        foreach (['partner_merchants', 'partner_branches', 'partner_offers', 'benefit_coupons', 'merchant_users'] as $table) {
            T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM $table"), $table);
        }
        $this->enableCityPass();
        T::eq([], $this->app->cityPass()->offers($this->resident())['offers']);
    }

    // ── 멤버십 ──────────────────────────────────────────────

    public function testJoiningGivesAMemberNumberAndTheResidencyRegionAsDefaultInterest()
    {
        $this->enableCityPass();
        $player = $this->resident('11110', '종로지킴이');
        $pass = $this->app->cityPass();
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($pass, $player) {
            $pass->join($player, false);
        });
        $profile = $pass->join($player, true);
        T::ok($profile['isMember']);
        T::ok(preg_match('/^CP-[A-Z2-9]{4}-[A-Z2-9]{4}$/', $profile['memberNo']) === 1, $profile['memberNo']);
        T::eq('11110', $profile['interestRegion']['code']);
        T::eq('종로지킴이', $profile['nickname']);
        // 다시 가입해도 회원번호가 바뀌지 않는다.
        T::eq($profile['memberNo'], $pass->join($player, true)['memberNo']);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_pass_memberships'));
        // 관심 지역은 다른 곳으로 바꿀 수 있다 (실제 거주지를 확인하지 않는다).
        T::eq('26110', $pass->setInterestRegion($player, '26110')['interestRegion']['code']);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($pass, $player) {
            $pass->setInterestRegion($player, '99999');
        });
    }

    public function testStrangerWithoutResidencyCanJoinAndPickAnyRegion()
    {
        $this->enableCityPass();
        $guest = $this->guest();
        $pass = $this->app->cityPass();
        $profile = $pass->join($guest, true);
        T::ok($profile['isMember']);
        T::eq(null, $profile['interestRegion']);
        T::eq('이방인', $profile['nickname']);
        $other = $pass->join($this->guest(), true, '26110');
        T::eq('26110', $other['interestRegion']['code']);
    }

    public function testMembershipStoresNoRealPersonalData()
    {
        $columns = array_column($this->db->all(
            "SELECT column_name AS c FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'city_pass_memberships'"), 'c');
        sort($columns);
        T::eq(['id', 'interest_region_id', 'joined_at', 'member_no', 'player_id', 'status', 'terms_version', 'updated_at'], $columns);
    }

    // ── 제휴 업체와 혜택의 승인 ─────────────────────────────

    public function testMerchantNeedsContractYouthCheckBranchAndASecondAdminToBeApproved()
    {
        $partners = $this->app->partners();
        list($id, $problems) = $partners->saveMerchant($this->passManager(), null,
            $this->merchantInput(['contract_reference' => '', 'contract_starts_on' => '', 'contract_ends_on' => '', 'youth_safe_confirmed' => '']));
        T::eq([], $problems);
        T::eq([], $partners->merchantTransition($this->passManager(), $id, 'submit', '승인 요청'));
        $problems = $partners->merchantTransition($this->super(), $id, 'approve', '승인해 본다');
        T::eq(4, count($problems), implode(' / ', $problems));   // 계약서 번호, 계약 기간, 청소년 확인, 지점
        T::eq('pending', $this->db->val('SELECT status FROM partner_merchants WHERE id = ?', [$id]));

        // 내용을 채운다 (고친 사람이 마지막 작성자가 된다).
        list(, $problems) = $partners->saveMerchant($this->passManager(), $id, $this->merchantInput());
        T::eq([], $problems);
        T::eq([], $partners->saveBranch($this->passManager(), $id, null, $this->branchInput()));
        // 등록한 사람은 승인할 수 없다.
        $problems = $partners->merchantTransition($this->passManager(), $id, 'approve', '내가 승인');
        T::ok(count($problems) === 1 && strpos($problems[0], '다른 관리자') !== false);
        T::eq([], $partners->merchantTransition($this->compliance(), $id, 'approve', '계약서 확인'));
        T::eq('approved', $this->db->val('SELECT status FROM partner_merchants WHERE id = ?', [$id]));

        // 승인된 업체는 그대로 고칠 수 없다.
        list(, $problems) = $partners->saveMerchant($this->passManager(), $id, $this->merchantInput(['name' => '이름 바꾸기']));
        T::ok(count($problems) > 0);
        // 계약이 끝난 업체는 승인되지 않는다.
        list($old, ) = $partners->saveMerchant($this->passManager(), null, $this->merchantInput(['contract_starts_on' => '2025-01-01', 'contract_ends_on' => '2025-12-31']));
        $partners->saveBranch($this->passManager(), $old, null, $this->branchInput());
        $partners->merchantTransition($this->passManager(), $old, 'submit', '승인 요청');
        T::ok(count($partners->merchantTransition($this->super(), $old, 'approve', '승인')) > 0);
    }

    public function testOnlyApprovedOffersOfApprovedMerchantsAreVisible()
    {
        $this->enableCityPass();
        $player = $this->resident();
        $this->member($player);
        $pass = $this->app->cityPass();
        $partners = $this->app->partners();
        $merchantId = $this->merchant();
        // 초안과 승인 대기 혜택은 보이지 않는다.
        list($draft, ) = $partners->saveOffer($this->passManager(), $merchantId, null, $this->offerInput());
        T::eq([], $pass->offers($player)['offers']);
        $partners->offerTransition($this->passManager(), $draft, 'submit', '승인 요청');
        T::eq([], $pass->offers($player)['offers']);
        // 등록한 사람은 승인할 수 없다.
        T::ok(count($partners->offerTransition($this->passManager(), $draft, 'approve', '내가 승인')) > 0);
        T::eq([], $partners->offerTransition($this->super(), $draft, 'approve', '조건 확인'));
        $offers = $pass->offers($player)['offers'];
        T::eq(1, count($offers));
        T::eq('10% 할인', $offers[0]['discountText']);
        T::eq('종로점', $offers[0]['branches'][0]['name']);
        T::eq('서울 종로구 테스트로 1', $offers[0]['branches'][0]['address']);
        T::near(37.5729, $offers[0]['branches'][0]['lat'], 0.00001);
        T::ok(!array_key_exists('internalId', $offers[0]['branches'][0]));
        T::ok($offers[0]['claimable']);
        T::eq(1, count($pass->merchants()['merchants']));

        // 지역으로 고른다: 지점이 없는 지역에는 보이지 않는다. 다른 지역의 혜택도 둘러볼 수 있다.
        T::eq(1, count($pass->offers($player, '11110')['offers']));
        T::eq(0, count($pass->offers($player, '26110')['offers']));
        T::eq(0, count($pass->offers($player, null, 'fitness')['offers']));

        // 업체를 멈추면 혜택도 사라진다.
        T::eq([], $partners->merchantTransition($this->passManager(), $merchantId, 'suspend', '민원 확인 중'));
        T::eq([], $pass->offers($player)['offers']);
        T::eq([], $pass->merchants()['merchants']);
        T::eq([], $partners->merchantTransition($this->super(), $merchantId, 'resume', '확인 끝'));
        // 발급을 멈춘 혜택, 기간이 지난 혜택도 보이지 않는다.
        T::eq([], $partners->offerTransition($this->passManager(), $draft, 'pause', '재고 확인'));
        T::eq([], $pass->offers($player)['offers']);
        T::eq([], $partners->offerTransition($this->passManager(), $draft, 'resume', '다시 시작'));
        T::eq(1, count($pass->offers($player)['offers']));
        Clock::advance(31 * 86400);
        T::eq([], $pass->offers($player)['offers']);
    }

    public function testOfferCannotPromiseBeyondTheContractOrUseGameResultsAsIssueCondition()
    {
        $partners = $this->app->partners();
        $merchantId = $this->merchant(['contract_ends_on' => '2026-12-31']);
        list($id, $problems) = $partners->saveOffer($this->passManager(), $merchantId, null,
            $this->offerInput(['ends_at' => '2027-03-01 00:00']));
        T::eq([], $problems);
        $partners->offerTransition($this->passManager(), $id, 'submit', '승인 요청');
        $problems = $partners->offerTransition($this->super(), $id, 'approve', '승인');
        T::ok(count($problems) === 1 && strpos($problems[0], '계약 기간') !== false);

        // 발급 조건은 가입·프로모션·제휴사 조건뿐이다. 전투 보상이나 GC 를 조건으로 넣을 수 없다.
        foreach (['battle_reward', 'gc_purchase', 'ranking', ''] as $policy) {
            list(, $problems) = $partners->saveOffer($this->passManager(), $merchantId, null, $this->offerInput(['issue_policy' => $policy]));
            T::ok(count($problems) > 0, $policy);
        }
        // 값의 범위
        foreach ([['discount_value' => '0'], ['discount_value' => '101'], ['per_member_limit' => '0'], ['coupon_valid_days' => '400'],
                     ['title' => ''], ['conditions' => ''], ['ends_at' => '어제'], ['total_limit' => '0']] as $bad) {
            list(, $problems) = $partners->saveOffer($this->passManager(), $merchantId, null, $this->offerInput($bad));
            T::ok(count($problems) > 0, json_encode($bad));
        }
    }

    // ── 쿠폰 발급 ───────────────────────────────────────────

    public function testClaimingACouponIsFreeIdempotentAndLimited()
    {
        $this->enableCityPass();
        $player = $this->resident();
        $offer = $this->offer($this->merchant(), ['total_limit' => '2']);
        $pass = $this->app->cityPass();
        // 가입하지 않으면 받을 수 없다.
        $error = T::throwsApi(ApiError::FORBIDDEN, function () use ($pass, $player, $offer) {
            $pass->claim($player, $offer['public_id'], $this->key());
        });
        T::eq('not_member', $error->reason);
        $this->member($player);
        $this->give($player, 500);
        $gc = $this->gc($player);
        $ledger = $this->ledgerRows();

        $key = $this->key();
        $first = $pass->claim($player, $offer['public_id'], $key);
        T::eq('issued', $first['coupon']['status']);
        T::eq('음료 10% 할인', $first['coupon']['offer']['title']);
        // 같은 요청 번호로 다시 오면 같은 쿠폰이 돌아오고, 두 장이 되지 않는다.
        $again = $pass->claim($player, $offer['public_id'], $key);
        T::eq($first['coupon']['id'], $again['coupon']['id']);
        T::ok($again['duplicate']);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM benefit_coupons'));
        T::eq(1, (int)$this->db->val('SELECT issued_count FROM partner_offers WHERE id = ?', [$offer['id']]));
        // 한 사람이 받을 수 있는 수
        $error = T::throwsApi(ApiError::LIMIT_REACHED, function () use ($pass, $player, $offer) {
            $pass->claim($player, $offer['public_id'], $this->key());
        });
        T::eq('limit_reached', $error->reason);

        // 쿠폰은 GC 와 이어지지 않는다: 잔액도 원장도 그대로다.
        T::eq($gc, $this->gc($player));
        T::eq($ledger, $this->ledgerRows());
        T::eq('membership', $this->db->val('SELECT issue_reason FROM benefit_coupons LIMIT 1'));

        // 전체 발급 수
        $second = $this->resident('11110', '둘째');
        $this->member($second);
        $pass->claim($second, $offer['public_id'], $this->key());
        $third = $this->resident('11110', '셋째');
        $this->member($third);
        $error = T::throwsApi(ApiError::LIMIT_REACHED, function () use ($pass, $third, $offer) {
            $pass->claim($third, $offer['public_id'], $this->key());
        });
        T::eq('sold_out', $error->reason);
        T::eq('sold_out', $pass->offers($third)['offers'][0]['claimBlock']);
        T::eq(2, (int)$this->db->val('SELECT COUNT(*) FROM benefit_coupons'));
        T::eq(2, (int)$this->db->val("SELECT COUNT(*) FROM coupon_events WHERE event = 'issued'"));
    }

    public function testCouponExpiryNeverPassesTheOfferPeriod()
    {
        $this->enableCityPass();
        $player = $this->resident();
        $this->member($player);
        $offer = $this->offer($this->merchant(), ['ends_at' => \CG\Infra\Form::koreaText(Clock::now() + 3 * 86400), 'coupon_valid_days' => '14']);
        $coupon = $this->app->cityPass()->claim($player, $offer['public_id'], $this->key())['coupon'];
        T::eq(Clock::isoFromSql($offer['ends_at']), $coupon['expiresAt']);
    }

    // ── 제시와 사용 처리 ────────────────────────────────────

    public function testPresentCodeAndQrCarryNoPersonalDataAndOnlyAHashIsStored()
    {
        $this->enableCityPass();
        $player = $this->resident('11110', '비밀닉네임');
        $profile = $this->member($player);
        $offer = $this->offer($this->merchant());
        $pass = $this->app->cityPass();
        $coupon = $pass->claim($player, $offer['public_id'], $this->key())['coupon'];
        $shown = $pass->present($player, $coupon['id']);

        T::ok(preg_match('/^[A-Z2-9]{5}-[A-Z2-9]{5}$/', $shown['code']) === 1, $shown['code']);
        T::eq('https://cg.test/merchant/redeem?code=' . $shown['code'], $shown['qrPayload']);
        T::eq(CityPass::PRESENT_SECONDS, $shown['expiresInSeconds']);
        foreach ([$profile['memberNo'], '비밀닉네임', $player['publicId'], $coupon['id'], (string)$player['id']] as $private) {
            T::ok(strpos($shown['qrPayload'], $private) === false, 'QR 에 개인정보가 들어 있다: ' . $private);
        }
        // DB 에는 코드가 그대로 남지 않는다.
        $row = $this->db->one('SELECT * FROM benefit_coupons LIMIT 1');
        $plain = str_replace('-', '', $shown['code']);
        T::eq(CityPass::hashCode($plain), $row['present_code_hash']);
        T::ok(strpos(json_encode($row), $plain) === false);
        T::ok(strpos(json_encode($this->db->all('SELECT * FROM coupon_events')), $plain) === false);

        // 다시 만들면 앞의 코드는 못 쓴다.
        $user = $this->merchantUser((int)$offer['merchant_id'], 'cafe.owner');
        $newer = $pass->present($player, $coupon['id']);
        T::ok($newer['code'] !== $shown['code']);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($user, $shown) {
            $this->app->merchantPortal()->verify($user, $shown['code']);
        });
        T::eq('음료 10% 할인', $this->app->merchantPortal()->verify($user, $newer['code'])['title']);

        // 남의 쿠폰으로는 코드를 만들 수 없다.
        $other = $this->resident('11110', '다른사람');
        T::throwsApi(ApiError::NOT_FOUND, function () use ($pass, $other, $coupon) {
            $pass->present($other, $coupon['id']);
        });
        // 공개 주소가 설정되지 않은 서버에서는 주소 대신 코드만 담는다.
        $bare = new \CG\App(\CG\Infra\Env::fromArray([]), $this->db);
        $bare->features()->forget();
        T::ok(strpos($bare->cityPass()->present($player, $coupon['id'])['qrPayload'], 'CGPASS:') === 0);
    }

    public function testMerchantRedeemsACouponExactlyOnce()
    {
        $this->enableCityPass();
        $player = $this->resident('11110', '손님');
        $profile = $this->member($player);
        $offer = $this->offer($this->merchant());
        $user = $this->merchantUser((int)$offer['merchant_id'], 'cafe.owner');
        list($couponId, $code) = $this->couponWithCode($player, $offer);
        $portal = $this->app->merchantPortal();
        $gc = $this->gc($player);
        $ledger = $this->ledgerRows();

        // 확인만 해서는 쓰이지 않는다. 손님의 정보는 상점에 나오지 않는다.
        $seen = $portal->verify($user, strtolower(str_replace('-', ' ', $code)));
        T::eq('10% 할인', $seen['discountText']);
        T::ok(strpos(json_encode($seen, JSON_UNESCAPED_UNICODE), '손님') === false);
        T::ok(strpos(json_encode($seen), $profile['memberNo']) === false);
        T::eq('issued', $this->db->val('SELECT status FROM benefit_coupons'));

        $done = $portal->redeem($user, $code);
        T::ok($done['redeemed']);
        $row = $this->db->one('SELECT * FROM benefit_coupons');
        T::eq('redeemed', $row['status']);
        T::eq(null, $row['present_code_hash']);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM coupon_redemptions'));
        T::eq($this->branchId((int)$offer['merchant_id']), (int)$this->db->val('SELECT branch_id FROM coupon_redemptions'));

        // 같은 코드로 다시 처리할 수 없다.
        T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $user, $code) {
            $portal->redeem($user, $code);
        });
        // 쓴 쿠폰으로는 새 코드도 만들 수 없다.
        $error = T::throwsApi(ApiError::CONFLICT, function () use ($player, $couponId) {
            $this->app->cityPass()->present($player, $couponId);
        });
        T::eq('already_redeemed', $error->reason);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM coupon_redemptions'));

        // 사용 기록은 쿠폰 하나에 한 줄뿐이다: 코드를 거치지 않고 넣으려 해도 DB 가 막는다.
        $duplicate = false;
        try {
            $this->db->exec('INSERT INTO coupon_redemptions (coupon_id, offer_id, merchant_id, merchant_user_id, membership_id, redeemed_at) VALUES (?, ?, ?, ?, ?, ?)',
                [$row['id'], $row['offer_id'], $row['merchant_id'], $user['id'], $row['membership_id'], Clock::sql()]);
        } catch (\PDOException $error) {
            $duplicate = true;
        }
        T::ok($duplicate);

        // 거래 로그: 발급 → 제시 → 사용, 그리고 실패한 시도.
        T::eq(['issued', 'presented', 'redeemed', 'verify_failed'],
            array_column($this->db->all('SELECT event FROM coupon_events ORDER BY id'), 'event'));
        // 이용자의 내역에 남는다. GC 는 그대로다 (쿠폰을 써도 게임 보상이 생기지 않는다).
        $history = $this->app->cityPass()->history($player)['history'];
        T::eq(1, count($history));
        T::eq('종로점', $history[0]['branchName']);
        T::eq($gc, $this->gc($player));
        T::eq($ledger, $this->ledgerRows());
        $profile = $this->app->cityPass()->profile($player);
        T::eq(['available' => 0, 'used' => 1, 'expired' => 0], $profile['coupons']);
    }

    public function testConcurrentRedeemLosesTheRace()
    {
        // 두 직원이 같은 코드를 동시에 처리하는 상황: 한쪽이 먼저 상태를 바꾼 뒤라면 다른 쪽의 UPDATE 는 0행을 바꾼다.
        $this->enableCityPass();
        $player = $this->resident();
        $this->member($player);
        $offer = $this->offer($this->merchant());
        $user = $this->merchantUser((int)$offer['merchant_id'], 'cafe.owner');
        list(, $code) = $this->couponWithCode($player, $offer);
        $hash = CityPass::hashCode(CityPass::normalizeCode($code));
        $sql = "UPDATE benefit_coupons SET status = 'redeemed', redeemed_at = ?, present_code_hash = NULL, present_expires_at = NULL"
            . " WHERE status = 'issued' AND present_code_hash = ? AND present_expires_at > ? AND expires_at > ?";
        T::eq(1, $this->db->exec($sql, [Clock::sql(), $hash, Clock::sql(), Clock::sql()]));
        T::eq(0, $this->db->exec($sql, [Clock::sql(), $hash, Clock::sql(), Clock::sql()]));
        T::throwsApi(ApiError::NOT_FOUND, function () use ($user, $code) {
            $this->app->merchantPortal()->redeem($user, $code);
        });
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM coupon_redemptions'));
    }

    public function testExpiredCodeExpiredCouponAndCancelledCouponCannotBeRedeemed()
    {
        $this->enableCityPass();
        $player = $this->resident();
        $this->member($player);
        $merchantId = $this->merchant();
        $offer = $this->offer($merchantId, ['per_member_limit' => '3', 'coupon_valid_days' => '1']);
        $user = $this->merchantUser($merchantId, 'cafe.owner');
        $portal = $this->app->merchantPortal();
        $pass = $this->app->cityPass();

        // 코드의 시간이 지났다.
        list($first, $code) = $this->couponWithCode($player, $offer);
        Clock::advance(CityPass::PRESENT_SECONDS + 1);
        $error = T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $user, $code) {
            $portal->redeem($user, $code);
        });
        T::ok(strpos($error->getMessage(), '새로') !== false);
        // 새 코드로는 된다.
        T::ok($portal->redeem($user, $pass->present($player, $first)['code'])['redeemed']);

        // 관리자가 취소한 쿠폰
        list($second, $code) = $this->couponWithCode($player, $offer);
        $secondRow = (int)$this->db->val('SELECT id FROM benefit_coupons WHERE public_id = ?', [$second]);
        T::ok(count($this->app->partners()->cancelCoupon($this->passManager(), $secondRow, '')) > 0);
        T::eq([], $this->app->partners()->cancelCoupon($this->passManager(), $secondRow, '잘못 발급'));
        T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $user, $code) {
            $portal->redeem($user, $code);
        });
        T::throwsApi(ApiError::NOT_FOUND, function () use ($pass, $player, $second) {
            $pass->present($player, $second);
        });
        // 쓴 쿠폰은 취소할 수 없다.
        T::ok(count($this->app->partners()->cancelCoupon($this->passManager(), (int)$this->db->val('SELECT id FROM benefit_coupons WHERE public_id = ?', [$first]), '취소해 본다')) > 0);

        // 유효기간이 지난 쿠폰
        list($third, ) = $this->couponWithCode($player, $offer);
        Clock::advance(86400 + 1);
        $error = T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($pass, $player, $third) {
            $pass->present($player, $third);
        });
        T::eq('expired', $error->reason);
        $statuses = array_column($pass->coupons($player)['coupons'], 'status', 'id');
        T::eq('expired', $statuses[$third]);
        T::eq('redeemed', $statuses[$first]);
        T::ok(!isset($statuses[$second]), '취소된 쿠폰은 목록에 없다');
        // 예약 작업이 만료로 적는다. 두 번 돌아도 한 번만 적는다.
        T::eq(1, $pass->expireDue());
        T::eq(0, $pass->expireDue());
        T::eq('expired', $this->db->val('SELECT status FROM benefit_coupons WHERE public_id = ?', [$third]));
    }

    public function testMerchantStaffCanOnlyHandleTheirOwnMerchantAndBranch()
    {
        $this->enableCityPass();
        $player = $this->resident();
        $this->member($player);
        $cafe = $this->merchant();
        $gym = $this->merchant(['name' => '테스트 헬스장', 'category' => 'fitness']);
        $cafeOffer = $this->offer($cafe);
        $gymOffer = $this->offer($gym, ['title' => '1일 무료 체험', 'discount_type' => 'free_trial', 'discount_value' => '']);
        $cafeUser = $this->merchantUser($cafe, 'cafe.owner');
        $gymUser = $this->merchantUser($gym, 'gym.owner');
        $portal = $this->app->merchantPortal();

        // 헬스장 직원이 카페 쿠폰의 코드를 넣으면 "없는 코드"와 똑같이 답한다.
        list(, $cafeCode) = $this->couponWithCode($player, $cafeOffer);
        $wrong = T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $gymUser, $cafeCode) {
            $portal->verify($gymUser, $cafeCode);
        });
        $unknown = T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $gymUser) {
            $portal->verify($gymUser, 'ABCDE-FGHJK');
        });
        T::eq($unknown->getMessage(), $wrong->getMessage());
        T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $gymUser, $cafeCode) {
            $portal->redeem($gymUser, $cafeCode);
        });
        T::eq('issued', $this->db->val('SELECT status FROM benefit_coupons WHERE merchant_id = ?', [$cafe]));
        T::ok($portal->redeem($cafeUser, $cafeCode)['redeemed']);

        // 내역과 통계는 자기 업체의 것만 보인다.
        list(, $gymCode) = $this->couponWithCode($player, $gymOffer);
        $portal->redeem($gymUser, $gymCode);
        T::eq(['음료 10% 할인'], array_column($portal->history($cafeUser), 'title'));
        T::eq(['1일 무료 체험'], array_column($portal->history($gymUser), 'title'));
        T::eq(1, $portal->stats($cafeUser)['total']);
        T::eq(1, $portal->stats($cafeUser)['today']);
        T::eq(['음료 10% 할인'], array_column($portal->stats($cafeUser)['offers'], 'title'));
        // 내역에 손님이 누구인지는 없다.
        T::ok(!array_intersect(['member_no', 'player_id', 'nickname', 'membership_id'], array_keys($portal->history($cafeUser)[0])));

        // 지점 계정: 다른 지점 전용 혜택은 처리할 수 없고, 자기 지점의 내역만 본다.
        T::eq([], $this->app->partners()->saveBranch($this->passManager(), $cafe, null, $this->branchInput(['name' => '부산점', 'region_code' => '26110',
            'address' => '부산 중구 테스트로 2', 'lat' => '35.1', 'lng' => '129.03'])));
        $busan = (int)$this->db->val("SELECT id FROM partner_branches WHERE merchant_id = ? AND name = '부산점'", [$cafe]);
        $jongno = $this->branchId($cafe);
        $busanOnly = $this->offer($cafe, ['title' => '부산점 한정', 'branch_id' => (string)$busan]);
        $jongnoStaff = $this->merchantUser($cafe, 'cafe.jongno', $jongno, 'staff');
        $busanStaff = $this->merchantUser($cafe, 'cafe.busan', $busan, 'staff');
        list(, $code) = $this->couponWithCode($player, $busanOnly);
        $error = T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $jongnoStaff, $code) {
            $portal->redeem($jongnoStaff, $code);
        });
        T::ok(strpos($error->getMessage(), '이 지점') !== false);
        T::ok($portal->redeem($busanStaff, $code)['redeemed']);
        T::eq(['부산점 한정'], array_column($portal->history($busanStaff), 'title'));
        T::eq(['음료 10% 할인'], array_column($portal->history($jongnoStaff), 'title'), '종로점 계정에는 종로점에서 쓴 것만 보인다');
        T::eq([], $portal->stats($busanStaff)['offers'], '지점 계정에는 업체 전체의 발급 수가 보이지 않는다');
        T::eq(2, count($portal->history($cafeUser)), '본사 계정은 모든 지점의 내역을 본다');
        // 지점이 여럿인 업체의 본사 계정은 지점을 골라야 한다.
        $anyBranch = $this->offer($cafe, ['title' => '전 지점']);
        list(, $code) = $this->couponWithCode($player, $anyBranch);
        $error = T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($portal, $cafeUser, $code) {
            $portal->redeem($cafeUser, $code);
        });
        T::eq('branch_required', $error->reason);
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($portal, $cafeUser, $code, $gym) {
            $portal->redeem($cafeUser, $code, $this->branchId($gym));   // 남의 업체의 지점
        });
        T::ok($portal->redeem($cafeUser, $code, $busan)['redeemed']);
    }

    public function testRedeemIsBlockedWhenFeatureOffMerchantSuspendedOrPasswordNotChanged()
    {
        $this->enableCityPass();
        $player = $this->resident();
        $this->member($player);
        $merchantId = $this->merchant();
        $offer = $this->offer($merchantId, ['per_member_limit' => '5']);
        $user = $this->merchantUser($merchantId, 'cafe.owner');
        $portal = $this->app->merchantPortal();
        list($couponId, $code) = $this->couponWithCode($player, $offer);

        // 임시 비밀번호를 바꾸지 않은 계정
        list(, $temporary) = $this->app->partners()->createMerchantUser($this->passManager(), $merchantId,
            ['login' => 'cafe.new', 'display_name' => '새 직원', 'role' => 'staff', 'branch_id' => '']);
        list(, $fresh) = $portal->login('cafe.new', $temporary, '127.0.0.1');
        T::ok($fresh['mustChangePassword']);
        $error = T::throwsApi(ApiError::FORBIDDEN, function () use ($portal, $fresh, $code) {
            $portal->redeem($fresh, $code);
        });
        T::eq('password_change_required', $error->reason);

        // 업체가 일시 중지됨
        $this->app->partners()->merchantTransition($this->passManager(), $merchantId, 'suspend', '확인 중');
        T::throwsApi(ApiError::FORBIDDEN, function () use ($portal, $user, $code) {
            $portal->redeem($user, $code);
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player, $couponId) {
            $this->app->cityPass()->present($player, $couponId);
        });
        T::ok(!$this->app->cityPass()->coupons($player)['coupons'][0]['usable']);
        $this->app->partners()->merchantTransition($this->super(), $merchantId, 'resume', '확인 끝');

        // 기능을 끔 (비상 정지)
        T::eq([], $this->app->features()->disable($this->compliance(), Features::PASS_COUPONS, '비상 정지'));
        T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($portal, $user, $code) {
            $portal->redeem($user, $code);
        });
        // 받은 쿠폰의 목록은 볼 수 있지만 쓸 수는 없다.
        $mine = $this->app->cityPass()->coupons($player);
        T::eq(1, count($mine['coupons']));
        T::ok(!$mine['couponsEnabled'] && !$mine['coupons'][0]['usable']);
        T::eq('issued', $this->db->val('SELECT status FROM benefit_coupons'));
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM coupon_redemptions'));
    }

    public function testGuessingCodesIsLoggedAndLocked()
    {
        $this->enableCityPass();
        $merchantId = $this->merchant();
        $user = $this->merchantUser($merchantId, 'cafe.owner');
        $portal = $this->app->merchantPortal();
        for ($i = 0; $i < MerchantPortal::MAX_BAD_CODES; $i++) {
            T::throwsApi(ApiError::NOT_FOUND, function () use ($portal, $user, $i) {
                $portal->verify($user, 'AAAAA-AAA' . chr(65 + $i) . 'B');
            });
        }
        T::eq(MerchantPortal::MAX_BAD_CODES, (int)$this->db->val("SELECT COUNT(*) FROM coupon_events WHERE event = 'verify_failed'"));
        // 더 틀릴 수 없다. 맞는 코드도 잠긴 동안에는 받지 않는다.
        $player = $this->resident();
        $this->member($player);
        list(, $code) = $this->couponWithCode($player, $this->offer($merchantId));
        T::throwsApi(ApiError::RATE_LIMITED, function () use ($portal, $user, $code) {
            $portal->redeem($user, $code);
        });
        // 부정 사용 신호에 잡힌다.
        $signals = $this->app->partners()->fraudSignals($this->passManager());
        T::eq('cafe.owner', $signals['failedVerify'][0]['login']);
        T::eq(MerchantPortal::MAX_BAD_CODES, (int)$signals['failedVerify'][0]['n']);
        Clock::advance(601);
        T::ok($portal->redeem($user, $this->app->cityPass()->present($player, $this->db->val('SELECT public_id FROM benefit_coupons'))['code'])['redeemed']);
    }

    // ── 상점 계정 ───────────────────────────────────────────

    public function testMerchantLoginLockoutPasswordChangeAndDeactivation()
    {
        $merchantId = $this->merchant();
        $portal = $this->app->merchantPortal();
        list($problems, $temporary) = $this->app->partners()->createMerchantUser($this->passManager(), $merchantId,
            ['login' => 'Cafe.Owner', 'display_name' => '점주', 'role' => 'owner', 'branch_id' => '']);
        T::eq([], $problems);
        // 임시 비밀번호는 해시로만 저장된다.
        $row = $this->db->one('SELECT * FROM merchant_users');
        T::eq('cafe.owner', $row['login']);
        T::ok($row['password_hash'] !== $temporary && password_verify($temporary, $row['password_hash']));
        T::ok(Auth::passwordProblem($temporary) === null);
        // 같은 아이디는 또 만들 수 없다.
        list($problems, ) = $this->app->partners()->createMerchantUser($this->passManager(), $merchantId,
            ['login' => 'cafe.owner', 'display_name' => '점주', 'role' => 'owner', 'branch_id' => '']);
        T::ok(count($problems) > 0);

        T::eq(null, $portal->login('cafe.owner', 'wrong-password', '127.0.0.1'));
        T::eq(null, $portal->login('nobody', $temporary, '127.0.0.1'));
        list($token, $user) = $portal->login('cafe.owner', $temporary, '127.0.0.1');
        T::ok($user['mustChangePassword']);
        T::ok(count($portal->changePassword($user, 'wrong', self::PASSWORD, self::PASSWORD, $token)) > 0);
        T::ok(count($portal->changePassword($user, $temporary, 'short', 'short', $token)) > 0);
        T::ok(count($portal->changePassword($user, $temporary, self::PASSWORD, self::PASSWORD . 'x', $token)) > 0);
        T::eq([], $portal->changePassword($user, $temporary, self::PASSWORD, self::PASSWORD, $token));
        T::ok(!$portal->session($token)['user']['mustChangePassword']);
        T::eq(null, $portal->login('cafe.owner', $temporary, '127.0.0.1'), '임시 비밀번호는 더 쓸 수 없다');

        // 다섯 번 틀리면 잠긴다. 잠긴 동안에는 맞는 비밀번호도 받지 않는다.
        $other = new \CG\App(\CG\Infra\Env::fromArray(['MERCHANT_LOGIN_PER_10MIN' => '100']), $this->db);
        for ($i = 0; $i < MerchantPortal::MAX_FAILURES; $i++) {
            T::eq(null, $other->merchantPortal()->login('cafe.owner', 'wrong-' . $i, '10.0.0.' . $i));
        }
        T::eq(null, $other->merchantPortal()->login('cafe.owner', self::PASSWORD, '10.0.0.9'));
        Clock::advance(MerchantPortal::LOCK_MINUTES * 60 + 1);
        list($token2, ) = $other->merchantPortal()->login('cafe.owner', self::PASSWORD, '10.0.0.9');
        T::ok($portal->session($token2) !== null);

        // 계정을 멈추면 접속이 바로 끊긴다.
        $this->app->partners()->merchantUserAction($this->passManager(), $row['id'], 'deactivate');
        T::eq(null, $portal->session($token2));
        T::eq(null, $other->merchantPortal()->login('cafe.owner', self::PASSWORD, '10.0.0.10'));
        // 접속은 쓰지 않으면 끝난다.
        $this->app->partners()->merchantUserAction($this->passManager(), $row['id'], 'activate');
        list($token3, ) = $other->merchantPortal()->login('cafe.owner', self::PASSWORD, '10.0.0.11');
        Clock::advance(MerchantPortal::IDLE_MINUTES * 60 + 1);
        T::eq(null, $portal->session($token3));
        // 로그인 기록이 남는다.
        T::ok((int)$this->db->val("SELECT COUNT(*) FROM merchant_audit_logs WHERE action = 'login.failed'") >= 6);
        T::ok((int)$this->db->val("SELECT COUNT(*) FROM merchant_audit_logs WHERE action = 'password.change'") === 1);
    }

    public function testLoginAttemptsFromOneAddressAreRateLimited()
    {
        $portal = $this->app->merchantPortal();
        for ($i = 0; $i < 10; $i++) {
            T::eq(null, $portal->login('nobody' . $i, 'x', '203.0.113.5'));
        }
        T::throwsApi(ApiError::RATE_LIMITED, function () use ($portal) {
            $portal->login('nobody', 'x', '203.0.113.5');
        });
    }

    public function testOnlyThePassRolesCanManagePartners()
    {
        $partners = $this->app->partners();
        foreach ([Auth::ROLE_OPERATOR, Auth::ROLE_SCHOLARSHIP, Auth::ROLE_REVIEWER, Auth::ROLE_FINANCE, Auth::ROLE_VIEWER] as $role) {
            $admin = $this->admin('x_' . $role, $role);
            T::throwsApi(ApiError::FORBIDDEN, function () use ($partners, $admin) {
                $partners->merchants($admin);
            }, $role);
            T::throwsApi(ApiError::FORBIDDEN, function () use ($partners, $admin) {
                $partners->saveMerchant($admin, null, $this->merchantInput());
            }, $role);
        }
        // 준법 담당은 보고 승인할 수 있지만 등록하거나 고칠 수는 없다.
        T::eq([], $partners->merchants($this->compliance()));
        T::throwsApi(ApiError::FORBIDDEN, function () use ($partners) {
            $partners->saveMerchant($this->compliance(), null, $this->merchantInput());
        });
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM partner_merchants'));
    }

    public function testStatsCountIssuedRedeemedAndRegions()
    {
        $this->enableCityPass();
        $merchantId = $this->merchant();
        $offer = $this->offer($merchantId);
        $user = $this->merchantUser($merchantId, 'cafe.owner');
        foreach (['손님하나', '손님둘', '손님셋'] as $i => $name) {
            $player = $this->resident('11110', $name);
            $this->member($player);
            list(, $code) = $this->couponWithCode($player, $offer);
            if ($i < 2) {
                $this->app->merchantPortal()->redeem($user, $code);
            }
        }
        $stats = $this->app->partners()->stats($this->passManager());
        T::eq(3, $stats['members']);
        T::eq(1, $stats['merchants']);
        T::eq(['issued' => 1, 'redeemed' => 2, 'expired' => 0, 'cancelled' => 0], $stats['coupons']);
        T::eq(3, (int)$stats['byOffer'][0]['issued_count']);
        T::eq(2, (int)$stats['byOffer'][0]['redeemed']);
        T::eq(2, (int)$stats['byRegion'][0]['n']);
        // 관리자 목록에서 회원은 회원번호로만 보인다.
        $coupons = $this->app->partners()->coupons($this->passManager());
        T::eq(3, count($coupons));
        T::ok(preg_match('/^CP-/', $coupons[0]['member_no']) === 1);
        T::ok(!isset($coupons[0]['nickname']) && !isset($coupons[0]['player_id']));
        T::eq(2, count($this->app->partners()->redemptions($this->passManager())));
    }
}
