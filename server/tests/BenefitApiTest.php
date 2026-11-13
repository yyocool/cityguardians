<?php
use CG\Api\Kernel;
use CG\Infra\Request;

/**
 * /api/v1 의 CITY PASS · SCHOLARSHIP 경로를 요청 단위로 확인한다 (docs/CITY_PASS_API.md, docs/SCHOLARSHIP_API.md).
 * 게임 화면과 홈페이지가 같은 서비스를 쓰므로, 여기서는 인증·권한·응답 모양과 "게임 경제와 섞이지 않는다"는 것을 본다.
 */
final class BenefitApiTest extends BenefitTestCase
{
    private function call($method, $path, array $body = [], $token = null, array $headers = [], array $query = [])
    {
        if ($token !== null) {
            $headers['Authorization'] = 'Bearer ' . $token;
        }
        return (new Kernel($this->app))->handle(Request::make($method, $path, $body, $headers, $query));
    }

    private function ok($method, $path, array $body = [], $token = null, array $headers = [], array $query = [])
    {
        list($status, $response) = $this->call($method, $path, $body, $token, $headers, $query);
        T::same(200, $status, $method . ' ' . $path . ' → ' . json_encode($response, JSON_UNESCAPED_UNICODE));
        T::same(true, $response['ok']);
        return $response['data'];
    }

    private function fail($expectedStatus, $expectedCode, $method, $path, array $body = [], $token = null, array $headers = [])
    {
        list($status, $response) = $this->call($method, $path, $body, $token, $headers);
        T::same(false, $response['ok'], $method . ' ' . $path . ' 가 성공했다');
        T::same($expectedCode, $response['error']['code'], $method . ' ' . $path);
        T::same($expectedStatus, $status);
        return $response['error'];
    }

    /** 전입신고까지 마친 계정의 토큰. */
    private function token($nickname = '혜택가디언')
    {
        $guest = $this->ok('POST', '/auth/guest', [], null, ['X-Client-Platform' => 'windows', 'X-Client-Version' => '8.1.0']);
        $this->ok('POST', '/residency', ['region_code' => '11110', 'nickname' => $nickname], $guest['access_token']);
        return $guest['access_token'];
    }

    private static function routes()
    {
        return [
            ['POST', '/auth/web-code'], ['GET', '/city-pass/profile'], ['PUT', '/city-pass/profile'], ['POST', '/city-pass/membership'],
            ['GET', '/city-pass/offers'], ['GET', '/city-pass/merchants'], ['GET', '/city-pass/coupons'],
            ['POST', '/city-pass/coupons/claim'], ['POST', '/city-pass/coupons/redeem'],
            ['GET', '/city-pass/history'], ['GET', '/scholarships'], ['GET', '/scholarships/my-applications'],
            ['GET', '/scholarships/p_aaaaaaaaaaaaaa'], ['POST', '/scholarships/p_aaaaaaaaaaaaaa/applications'],
            ['GET', '/scholarships/applications/a_aaaaaaaaaaaaaa'], ['POST', '/scholarships/applications/a_aaaaaaaaaaaaaa/withdraw'],
            ['POST', '/scholarships/applications/a_aaaaaaaaaaaaaa/appeal'], ['POST', '/scholarships/applications/a_aaaaaaaaaaaaaa/guardian-link'],
        ];
    }

    /** 응답 어디에도 게임 경제의 값이 섞여 있지 않다. */
    private static function noEconomy($data, $what)
    {
        $walk = function ($value, $path) use (&$walk, $what) {
            if (!is_array($value)) {
                return;
            }
            foreach ($value as $key => $inner) {
                if (is_string($key)) {
                    T::ok(preg_match('/(^|_)(gc|balance|wallet|credit_score|loan|stock|xp|level|rank|kills?|battle)(_|$)/', $key) === 0, "$what: $path.$key");
                    T::ok(preg_match('/[A-Z]/', $key) === 0, "$what: snake_case 가 아니다: $path.$key");
                }
                $walk($inner, $path . '.' . $key);
            }
        };
        $walk($data, '');
    }

    public function testEveryBenefitRouteNeedsAToken()
    {
        foreach (self::routes() as $route) {
            $this->fail(401, 'UNAUTHORIZED', $route[0], $route[1]);
            $this->fail(401, 'UNAUTHORIZED', $route[0], $route[1], [], str_repeat('0', 64));
        }
    }

    public function testWhileTheFeaturesAreOffTheApiIssuesNothing()
    {
        // 승인된 혜택과 공고가 DB 에 있어도, 기능을 끄면 API 는 아무것도 내주지 않는다.
        $this->enableCityPass();
        $this->enableScholarship();
        $offer = $this->offer($this->merchant());
        $program = $this->program();
        foreach (array_keys($this->app->features()->all()) as $code) {
            $this->app->features()->disable($this->compliance(), $code, '운영 정책이 확정되지 않음');
        }
        $token = $this->token();

        $profile = $this->ok('GET', '/city-pass/profile', [], $token);
        T::same(false, $profile['membership_enabled']);
        T::same(false, $profile['is_member']);
        T::ok($profile['notice'] !== '');
        $offers = $this->ok('GET', '/city-pass/offers', [], $token);
        T::same(false, $offers['coupons_enabled']);
        T::eq([], $offers['offers']);
        $merchants = $this->ok('GET', '/city-pass/merchants', [], $token);
        T::same(false, $merchants['coupons_enabled']);
        T::eq([], $merchants['merchants']);
        T::eq([], $this->ok('GET', '/city-pass/coupons', [], $token)['coupons']);
        $list = $this->ok('GET', '/scholarships', [], $token);
        T::same(false, $list['applications_enabled']);
        T::eq([], $list['programs']);

        $error = $this->fail(403, 'FEATURE_DISABLED', 'POST', '/city-pass/membership', ['agree_terms' => true], $token);
        T::ok($error['message'] !== '');
        $this->fail(403, 'FEATURE_DISABLED', 'POST', '/city-pass/coupons/claim', ['offer_id' => $offer['public_id']], $token, ['Idempotency-Key' => $this->key()]);
        $this->fail(403, 'FEATURE_DISABLED', 'GET', '/scholarships/' . $program['public_id'], [], $token);
        $this->fail(403, 'FEATURE_DISABLED', 'POST', '/scholarships/' . $program['public_id'] . '/applications', $this->applicationInput(), $token);
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM city_pass_memberships'));
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_applications'));
    }

    public function testCityPassThroughTheApiSharesDataWithTheSiteAndStaysOutOfTheGameEconomy()
    {
        $this->enableCityPass();
        $merchantId = $this->merchant();
        $offer = $this->offer($merchantId);
        $token = $this->token('혜택가디언');
        $gc = $this->ok('GET', '/me', [], $token)['gc'];
        $ledger = $this->ledgerRows();

        // 가입: 약관 동의가 있어야 한다. 관심 지역의 기본값은 전입신고한 지역이다.
        $this->fail(422, 'VALIDATION_FAILED', 'POST', '/city-pass/membership', [], $token);
        $profile = $this->ok('POST', '/city-pass/membership', ['agree_terms' => true], $token);
        T::same(true, $profile['is_member']);
        T::ok(preg_match('/^CP-[A-Z0-9]{4}-[A-Z0-9]{4}$/', $profile['member_no']) === 1);
        T::same('11110', $profile['interest_region']['code']);
        T::same('혜택가디언', $profile['nickname']);
        self::noEconomy($profile, 'profile');
        // 다른 지역으로 바꿀 수 있다 (거주지를 확인하지 않는다).
        T::same('26110', $this->ok('PUT', '/city-pass/profile', ['interest_region_code' => '26110'], $token)['interest_region']['code']);
        $this->fail(404, 'NOT_FOUND', 'PUT', '/city-pass/profile', ['interest_region_code' => '99999'], $token);

        // 혜택: 다른 지역의 것도 볼 수 있다.
        $offers = $this->ok('GET', '/city-pass/offers', [], $token);
        T::same(1, count($offers['offers']));
        T::same(true, $offers['offers'][0]['claimable']);
        self::noEconomy($offers, 'offers');
        T::same(1, count($this->ok('GET', '/city-pass/offers', [], $token, [], ['region' => '11110'])['offers']));
        T::same(0, count($this->ok('GET', '/city-pass/offers', [], $token, [], ['region' => '26110'])['offers']));
        // 모르는 지역·업종은 거절한다.
        T::same(400, $this->call('GET', '/city-pass/offers', [], $token, [], ['region' => "1' OR 1"])[0]);
        T::same(400, $this->call('GET', '/city-pass/offers', [], $token, [], ['category' => 'casino'])[0]);

        // 제휴 상점: 승인된 업체의 지점만, 고른 지역과 업종의 것만 나온다. 게임과 홈페이지가 같은 목록을 본다.
        $merchants = $this->ok('GET', '/city-pass/merchants', [], $token);
        T::same(1, count($merchants['merchants']));
        T::ok(count($merchants['merchants'][0]['branches']) >= 1);
        self::noEconomy($merchants, 'merchants');
        T::same($this->app->cityPass()->merchants()['merchants'][0]['id'], $merchants['merchants'][0]['id']);
        T::same(1, count($this->ok('GET', '/city-pass/merchants', [], $token, [], ['region' => '11110'])['merchants']));
        T::same(0, count($this->ok('GET', '/city-pass/merchants', [], $token, [], ['region' => '26110'])['merchants']));
        $category = $merchants['merchants'][0]['category'];
        T::same(1, count($this->ok('GET', '/city-pass/merchants', [], $token, [], ['category' => $category])['merchants']));
        $other = $category === 'cafe' ? 'fitness' : 'cafe';
        T::same(0, count($this->ok('GET', '/city-pass/merchants', [], $token, [], ['category' => $other])['merchants']));
        T::same(400, $this->call('GET', '/city-pass/merchants', [], $token, [], ['region' => "1' OR 1"])[0]);
        T::same(400, $this->call('GET', '/city-pass/merchants', [], $token, [], ['category' => 'casino'])[0]);
        // 승인되지 않은 업체는 나오지 않는다.
        $this->db->exec("UPDATE partner_merchants SET status = 'suspended' WHERE id = ?", [$merchantId]);
        T::eq([], $this->ok('GET', '/city-pass/merchants', [], $token)['merchants']);
        $this->db->exec("UPDATE partner_merchants SET status = 'approved' WHERE id = ?", [$merchantId]);

        // 가입과 조회는 지갑과 원장을 건드리지 않는다.
        T::same($gc, $this->ok('GET', '/me', [], $token)['gc']);
        T::same($ledger, $this->ledgerRows());
    }

    public function testCouponClaimNeedsAnIdempotencyKeyAndTheGameOnlyGetsAOneTimeCode()
    {
        $this->enableCityPass();
        $merchantId = $this->merchant();
        $offer = $this->offer($merchantId);
        $token = $this->token('혜택가디언');
        $this->ok('POST', '/city-pass/membership', ['agree_terms' => true], $token);
        $stranger = $this->token('다른가디언');
        $gc = $this->ok('GET', '/me', [], $token)['gc'];
        $ledger = $this->ledgerRows();

        // 중복 방지 키가 있어야 하고, 같은 키로 다시 보내면 같은 쿠폰이다.
        $this->fail(400, 'INVALID_REQUEST', 'POST', '/city-pass/coupons/claim', ['offer_id' => $offer['public_id']], $token);
        $key = ['Idempotency-Key' => $this->key()];
        $first = $this->ok('POST', '/city-pass/coupons/claim', ['offer_id' => $offer['public_id']], $token, $key);
        $again = $this->ok('POST', '/city-pass/coupons/claim', ['offer_id' => $offer['public_id']], $token, $key);
        T::same($first['coupon']['id'], $again['coupon']['id']);
        T::same(1, (int)$this->db->val('SELECT COUNT(*) FROM benefit_coupons'));
        self::noEconomy($first, 'claim');
        // 클라이언트가 값을 덧붙여도 쿠폰의 내용은 서버가 정한다.
        $this->fail(422, 'LIMIT_REACHED', 'POST', '/city-pass/coupons/claim',
            ['offer_id' => $offer['public_id'], 'discount_value' => 100, 'per_member_limit' => 99, 'gc' => 1000], $token, ['Idempotency-Key' => $this->key()]);

        $coupons = $this->ok('GET', '/city-pass/coupons', [], $token);
        T::same(1, count($coupons['coupons']));
        T::same('issued', $coupons['coupons'][0]['status']);
        self::noEconomy($coupons, 'coupons');

        // 이름은 redeem 이지만, 게임이 부르는 이 경로는 쿠폰을 쓰지 않는다: 매장에 보여 줄 일회용 코드만 만든다.
        $shown = $this->ok('POST', '/city-pass/coupons/redeem', ['coupon_id' => $first['coupon']['id']], $token);
        T::ok(preg_match('/^[A-Z0-9]{5}-[A-Z0-9]{5}$/', $shown['code']) === 1);
        T::same('https://cg.test/merchant/redeem?code=' . $shown['code'], $shown['qr_payload']);
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));
        // 클라이언트가 "썼다"고 알릴 방법은 없다.
        list($status) = $this->call('POST', '/city-pass/coupons/redeem', ['coupon_id' => $first['coupon']['id'], 'status' => 'redeemed', 'redeemed' => true], $token);
        T::same(200, $status);
        T::same('issued', $this->db->val('SELECT status FROM benefit_coupons'));
        // 남의 쿠폰은 없는 것으로 답한다.
        $this->fail(404, 'NOT_FOUND', 'POST', '/city-pass/coupons/redeem', ['coupon_id' => $first['coupon']['id']], $stranger);

        // 상점이 사용 처리하면 이용 내역에 나온다.
        $staff = $this->merchantUser($merchantId, 'cafe.owner');
        $code = $this->ok('POST', '/city-pass/coupons/redeem', ['coupon_id' => $first['coupon']['id']], $token)['code'];
        $this->app->merchantPortal()->redeem($staff, $code);
        $history = $this->ok('GET', '/city-pass/history', [], $token);
        T::same(1, count($history['history']));
        self::noEconomy($history, 'history');
        $this->fail(409, 'CONFLICT', 'POST', '/city-pass/coupons/redeem', ['coupon_id' => $first['coupon']['id']], $token);

        // 게임 경제는 그대로다: 지갑도 원장도 바뀌지 않았다.
        T::same($gc, $this->ok('GET', '/me', [], $token)['gc']);
        T::same($ledger, $this->ledgerRows());
    }

    public function testWebLoginCodeIsIssuedOnlyToTheSignedInPlayer()
    {
        $token = $this->token();
        $issued = $this->ok('POST', '/auth/web-code', [], $token);
        T::ok(preg_match('/^[A-Z0-9]{4}-[A-Z0-9]{4}$/', $issued['code']) === 1);
        T::same(300, $issued['expires_in_seconds']);
        T::same('https://cg.test/login', $issued['login_url']);
        // 코드는 해시로만 저장된다.
        T::same(0, (int)$this->db->val('SELECT COUNT(*) FROM web_login_codes WHERE code_hash = ?', [strtolower(str_replace('-', '', $issued['code']))]));
        // 새 코드를 받으면 앞의 코드는 쓸 수 없다.
        $second = $this->ok('POST', '/auth/web-code', [], $token);
        T::same(null, $this->app->webAuth()->login($issued['code'], '127.0.0.1'));
        T::ok($this->app->webAuth()->login($second['code'], '127.0.0.1') !== null);
        // 너무 자주 받을 수 없다.
        for ($i = 0; $i < 4; $i++) {
            $this->ok('POST', '/auth/web-code', [], $token);
        }
        $this->fail(429, 'RATE_LIMITED', 'POST', '/auth/web-code', [], $token);
    }

    public function testScholarshipApiShowsProgramsAndStatusWithoutGameData()
    {
        $this->enableScholarship();
        $program = $this->program();
        $token = $this->token();
        $other = $this->token('다른가디언');
        $gc = $this->ok('GET', '/me', [], $token)['gc'];
        $ledger = $this->ledgerRows();

        $list = $this->ok('GET', '/scholarships', [], $token);
        T::same(1, count($list['programs']));
        T::same('https://cg.test/scholarship', $list['site_url']);
        self::noEconomy($list, 'scholarships');
        $detail = $this->ok('GET', '/scholarships/' . $program['public_id'], [], $token);
        T::same(2, count($detail['criteria']));
        T::same('https://cg.test/scholarship/apply/' . $program['public_id'], $detail['apply_url']);
        T::ok(strpos($detail['not_used_in_review'], '게임') !== false);
        self::noEconomy($detail, 'program');
        $this->fail(404, 'NOT_FOUND', 'GET', '/scholarships/p_aaaaaaaaaaaaaa', [], $token);
        $this->fail(400, 'INVALID_REQUEST', 'GET', '/scholarships/NOT-AN-ID', [], $token);

        // 신청 API: 값의 이름은 snake_case 로 받는다. 클라이언트가 덧붙인 게임 값은 읽지 않는다.
        $input = $this->applicationInput();
        $body = ['applicant' => ['name' => '홍길동', 'birth_year' => 2004, 'school_level' => 'university', 'school_name' => '테스트대학교',
            'email' => 'student@example.com', 'phone' => '010-0000-0000'], 'form' => $input['form'], 'documents' => [], 'consents' => ['privacy' => true],
            'game_level' => 99, 'gc' => 999999, 'rank' => 1, 'status' => 'announced', 'result' => 'selected'];
        $this->fail(422, 'VALIDATION_FAILED', 'POST', '/scholarships/' . $program['public_id'] . '/applications', ['consents' => ['privacy' => true]], $token);
        $applied = $this->ok('POST', '/scholarships/' . $program['public_id'] . '/applications', $body, $token);
        T::same('submitted', $applied['application']['status']);
        T::same(null, $applied['application']['result']);
        T::same(null, $applied['guardian_consent']);
        self::noEconomy($applied, 'apply');
        $row = $this->db->one('SELECT * FROM scholarship_applications');
        T::same('submitted', $row['status']);
        T::same(null, $row['result']);
        $this->fail(409, 'CONFLICT', 'POST', '/scholarships/' . $program['public_id'] . '/applications', $body, $token);

        $mine = $this->ok('GET', '/scholarships/my-applications', [], $token);
        T::same(1, count($mine['applications']));
        $id = $mine['applications'][0]['id'];
        T::same($id, $this->ok('GET', '/scholarships/applications/' . $id, [], $token)['application']['id']);
        // 남의 신청은 없는 것으로 답한다.
        $this->fail(404, 'NOT_FOUND', 'GET', '/scholarships/applications/' . $id, [], $other);
        $this->fail(404, 'NOT_FOUND', 'POST', '/scholarships/applications/' . $id . '/withdraw', [], $other);
        $this->fail(404, 'NOT_FOUND', 'POST', '/scholarships/applications/' . $id . '/appeal', ['reason' => str_repeat('이의가 있습니다. ', 5)], $other);
        T::eq([], $this->ok('GET', '/scholarships/my-applications', [], $other)['applications']);

        T::same('withdrawn', $this->ok('POST', '/scholarships/applications/' . $id . '/withdraw', [], $token)['application']['status']);
        T::same($gc, $this->ok('GET', '/me', [], $token)['gc']);
        T::same($ledger, $this->ledgerRows());
    }

    public function testThereIsNoRouteThatPaysConvertsOrDecides()
    {
        // 만들지 않은 것: GC 교환, 쿠폰 구매, 사용 처리, 장학 결과 결정, 지급. 주소를 짐작해 불러도 없다.
        $token = $this->token();
        foreach ([
            ['POST', '/city-pass/coupons/buy'], ['POST', '/city-pass/coupons/exchange'], ['POST', '/city-pass/coupons/use'], ['POST', '/city-pass/coupons/confirm'],
            ['POST', '/city-pass/exchange'], ['POST', '/wallet/exchange'], ['POST', '/wallet/cashout'],
            ['POST', '/scholarships/applications/a_aaaaaaaaaaaaaa/result'], ['POST', '/scholarships/applications/a_aaaaaaaaaaaaaa/award'],
            ['POST', '/scholarships/applications/a_aaaaaaaaaaaaaa/disburse'], ['POST', '/scholarships/applications/a_aaaaaaaaaaaaaa/review'],
            ['PUT', '/scholarships/applications/a_aaaaaaaaaaaaaa'], ['DELETE', '/city-pass/coupons'],
        ] as $route) {
            list($status, $response) = $this->call($route[0], $route[1], [], $token, ['Idempotency-Key' => $this->key()]);
            // 없는 주소는 404, 있는 주소에 없는 방식은 400 으로 답한다.
            T::ok(in_array($status, [400, 404], true), $route[0] . ' ' . $route[1] . ' → ' . $status);
            T::ok(in_array($response['error']['code'], ['NOT_FOUND', 'INVALID_REQUEST'], true));
            T::same(false, $response['ok']);
        }
    }
}
