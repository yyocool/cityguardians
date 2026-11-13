<?php
use CG\Admin\Auth;
use CG\Infra\ApiError;
use CG\Infra\Crypto;
use CG\Infra\Env;
use CG\Service\Features;

/**
 * 현실 혜택 기능의 승인 절차 (docs/REAL_WORLD_REWARDS_POLICY.md 3~4장), 업무별 권한, 개인정보 암호화.
 */
final class BenefitFeatureTest extends BenefitTestCase
{
    public function testEveryRealWorldFeatureStartsDisabledAndUnreviewed()
    {
        $all = $this->app->features()->all();
        T::eq(['city_pass_membership', 'city_pass_coupons', 'scholarship_applications', 'scholarship_awards'], array_keys($all));
        foreach ($all as $code => $row) {
            T::eq(0, (int)$row['is_enabled'], $code);
            T::eq('not_reviewed', $row['legal_status'], $code);
            T::ok(!$this->app->features()->isEnabled($code), $code);
            $error = T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($code) {
                $this->app->features()->mustBeEnabled($code);
            });
            T::eq(403, $error->httpStatus());
        }
    }

    public function testFeatureCannotBeEnabledBeforeLegalReviewIsRecordedAsApproved()
    {
        $features = $this->app->features();
        $code = Features::PASS_MEMBERSHIP;
        $problems = $features->enable($this->super(), $code, '켜 본다');
        T::ok(count($problems) > 0 && strpos(implode(' ', $problems), '법률 검토') !== false);
        T::ok(!$features->isEnabled($code));

        // 검토 중, 반려 상태로는 켤 수 없다.
        foreach ([Features::IN_REVIEW, Features::REJECTED] as $status) {
            T::eq([], $features->recordLegal($this->compliance(), $code, $this->legalInput($status)));
            T::ok(count($features->enable($this->super(), $code, '켜 본다')) > 0);
            T::ok(!$features->isEnabled($code));
        }
    }

    public function testApprovedLegalReviewNeedsDocumentReferenceReviewerAndDate()
    {
        $features = $this->app->features();
        foreach (['legalReference', 'legalReviewer', 'legalReviewedOn'] as $missing) {
            $input = $this->legalInput();
            $input[$missing] = '';
            T::ok(count($features->recordLegal($this->compliance(), Features::PASS_MEMBERSHIP, $input)) > 0, $missing);
        }
        // 앞날의 날짜, 틀린 날짜, 사유 없음
        T::ok(count($features->recordLegal($this->compliance(), Features::PASS_MEMBERSHIP, ['legalReviewedOn' => '2030-01-01'] + $this->legalInput())) > 0);
        T::ok(count($features->recordLegal($this->compliance(), Features::PASS_MEMBERSHIP, ['legalReviewedOn' => '2026-02-31'] + $this->legalInput())) > 0);
        T::ok(count($features->recordLegal($this->compliance(), Features::PASS_MEMBERSHIP, ['reason' => ''] + $this->legalInput())) > 0);
        T::ok(count($features->recordLegal($this->compliance(), Features::PASS_MEMBERSHIP, ['legalStatus' => 'yes'] + $this->legalInput())) > 0);
        T::eq('not_reviewed', $features->all()[Features::PASS_MEMBERSHIP]['legal_status']);
    }

    public function testThePersonWhoRecordedLegalApprovalCannotEnableTheFeature()
    {
        $features = $this->app->features();
        $code = Features::PASS_MEMBERSHIP;
        // 최고 관리자가 혼자 기록하고 혼자 켜려 한다.
        T::eq([], $features->recordLegal($this->super(), $code, $this->legalInput()));
        $problems = $features->enable($this->super(), $code, '혼자 켠다');
        T::ok(count($problems) === 1 && strpos($problems[0], '다른 최고 관리자') !== false);
        T::ok(!$features->isEnabled($code));
        // 다른 최고 관리자는 켤 수 있다.
        T::eq([], $features->enable($this->admin('root2', Auth::ROLE_SUPER), $code, '검토 의견서를 확인하고 켬'));
        T::ok($features->isEnabled($code));
    }

    public function testOnlyTheRightRolesCanRecordLegalReviewAndEnable()
    {
        $features = $this->app->features();
        $code = Features::PASS_MEMBERSHIP;
        foreach ([Auth::ROLE_PASS, Auth::ROLE_SCHOLARSHIP, Auth::ROLE_OPERATOR, Auth::ROLE_REVIEWER, Auth::ROLE_FINANCE, Auth::ROLE_VIEWER] as $role) {
            T::throwsApi(ApiError::FORBIDDEN, function () use ($features, $code, $role) {
                $features->recordLegal($this->admin('a_' . $role, $role), $code, $this->legalInput());
            }, $role);
        }
        T::eq([], $features->recordLegal($this->compliance(), $code, $this->legalInput()));
        // 준법 담당은 기록하고 끌 수는 있어도 켤 수는 없다.
        foreach ([Auth::ROLE_COMPLIANCE, Auth::ROLE_PASS, Auth::ROLE_OPERATOR] as $role) {
            T::throwsApi(ApiError::FORBIDDEN, function () use ($features, $code, $role) {
                $features->enable($this->admin('a_' . $role, $role), $code, '켜 본다');
            }, $role);
        }
        T::ok(!$features->isEnabled($code));
    }

    public function testEnableDisableAndHistoryAreRecorded()
    {
        $features = $this->app->features();
        $code = Features::PASS_MEMBERSHIP;
        $this->enableFeature($code);
        T::ok(count($features->enable($this->super(), $code, '또 켠다')) > 0, '이미 켜진 것을 또 켤 수 없다');
        T::ok(count($features->disable($this->compliance(), $code, '')) > 0, '사유가 있어야 한다');
        T::eq([], $features->disable($this->compliance(), $code, '운영 정책 재검토'));
        T::ok(!$features->isEnabled($code));

        $actions = array_column($this->db->all('SELECT action FROM benefit_feature_history WHERE feature_code = ? ORDER BY id', [$code]), 'action');
        T::eq(['legal_update', 'enable', 'disable'], $actions);
        $audit = array_column($this->db->all("SELECT action FROM admin_audit_logs WHERE target_type = 'benefit_feature' ORDER BY id"), 'action');
        T::eq(['feature.legal_update', 'feature.enable', 'feature.disable'], $audit);
        $last = $this->db->one('SELECT * FROM benefit_feature_history ORDER BY id DESC LIMIT 1');
        T::eq('운영 정책 재검토', $last['reason']);
        T::eq(1, json_decode($last['before_value'], true)['isEnabled']);
        T::eq(0, json_decode($last['after_value'], true)['isEnabled']);
    }

    public function testFeatureTurnsOffWhenLegalStatusStopsBeingApproved()
    {
        $features = $this->app->features();
        $code = Features::PASS_MEMBERSHIP;
        $this->enableFeature($code);
        T::eq([], $features->recordLegal($this->compliance(), $code, $this->legalInput(Features::IN_REVIEW)));
        T::ok(!$features->isEnabled($code));
        T::eq(0, (int)$this->db->val('SELECT is_enabled FROM benefit_features WHERE code = ?', [$code]));
        T::eq('auto_disable', $this->db->val('SELECT action FROM benefit_feature_history ORDER BY id DESC LIMIT 1'));
    }

    public function testFlagSetDirectlyInTheDatabaseDoesNotEnableAnUnreviewedFeature()
    {
        $this->db->exec('UPDATE benefit_features SET is_enabled = 1');
        $this->app->features()->forget();
        foreach (array_keys($this->app->features()->all()) as $code) {
            T::ok(!$this->app->features()->isEnabled($code), $code);
        }
    }

    public function testServerLockOverridesEverything()
    {
        $this->enableCityPass();
        $locked = new \CG\App(Env::fromArray(['BENEFITS_LOCKED' => '1']), $this->db);
        T::ok(!$locked->features()->isEnabled(Features::PASS_MEMBERSHIP));
        T::ok(!$locked->features()->isEnabled(Features::PASS_COUPONS));
        T::eq([], $locked->features()->recordLegal($this->compliance(), Features::SCH_AWARDS, $this->legalInput()));
        T::ok(count($locked->features()->enable($this->super(), Features::SCH_AWARDS, '잠긴 채로 켜 본다')) > 0);
    }

    public function testCouponsNeedMembershipFeature()
    {
        $features = $this->app->features();
        T::eq([], $features->recordLegal($this->compliance(), Features::PASS_COUPONS, $this->legalInput()));
        T::ok(count($features->enable($this->super(), Features::PASS_COUPONS, '쿠폰만 켠다')) > 0);
        $this->enableCityPass();
        T::eq([], $features->disable($this->super(), Features::PASS_MEMBERSHIP, '가입을 멈춘다'));
        T::ok(!$features->isEnabled(Features::PASS_COUPONS), '가입이 꺼지면 쿠폰도 동작하지 않는다');
    }

    public function testScholarshipApplicationsCannotBeEnabledWithoutAnEncryptionKey()
    {
        $noKey = new \CG\App(Env::fromArray([]), $this->db);
        T::ok(!$noKey->crypto()->ready());
        T::eq([], $noKey->features()->recordLegal($this->compliance(), Features::SCH_APPLICATIONS, $this->legalInput()));
        $problems = $noKey->features()->enable($this->super(), Features::SCH_APPLICATIONS, '키 없이 켜 본다');
        T::ok(count($problems) === 1 && strpos($problems[0], 'DATA_KEY') !== false);
        T::eq([], $this->app->features()->enable($this->super(), Features::SCH_APPLICATIONS, '키가 있는 서버에서 켠다'));
    }

    // ── 만들지 않은 기능 ────────────────────────────────────

    public function testThereIsNoSwitchOrRouteForTheForbiddenFeatures()
    {
        // 게임머니 교환, 성적·랭킹에 따른 보상, 결제 가산점, 금융 수익 현금화는 스위치가 없다.
        T::eq(4, (int)$this->db->val('SELECT COUNT(*) FROM benefit_features'));
        T::eq(5, count(Features::notBuilt()));
        foreach (\CG\Api\Routes::all($this->app) as $route) {
            T::ok(!preg_match('#(exchange|cash|withdraw-gc|payout|transfer|convert)#', $route['regex']), $route['regex']);
        }
    }

    public function testBenefitServicesNeverTouchWalletsOrGameRecords()
    {
        $forbidden = '/->wallets\(\)|->daily\(\)|->combat\(\)|->city\(\)|->defense\(\)|->hunt\(\)|->fps\(\)'
            . '|\b(wallets|ledger_entries|characters|defense_battles|hunt_sessions|fps_mission_results|region_contributions|player_combat_\w+|city_risk_\w+)\b/';
        foreach (['CityPass', 'Partners', 'MerchantPortal', 'Scholarships', 'ScholarshipOffice', 'Features', 'WebAuth'] as $service) {
            $code = file_get_contents(__DIR__ . '/../src/Service/' . $service . '.php');
            // 주석은 빼고 본다 (주석에는 "Wallets 를 부르지 않는다" 같은 말이 있다).
            $code = preg_replace('#/\*.*?\*/#s', '', $code);
            $code = preg_replace('#^\s*//.*$#m', '', $code);
            T::ok(preg_match($forbidden, $code, $m) === 0, $service . ' 가 게임 기록을 건드린다: ' . (isset($m[0]) ? $m[0] : ''));
        }
        // 현실 혜택 테이블에는 게임머니나 성적을 담는 열이 없다.
        $columns = $this->db->all(
            "SELECT table_name AS t, column_name AS c FROM information_schema.columns WHERE table_schema = DATABASE()"
            . " AND (table_name LIKE 'scholarship\\_%' OR table_name LIKE 'partner\\_%' OR table_name LIKE 'benefit\\_%' OR table_name LIKE 'coupon\\_%'"
            . " OR table_name LIKE 'city\\_pass\\_%' OR table_name LIKE 'merchant\\_%')");
        T::ok(count($columns) > 150);
        foreach ($columns as $column) {
            T::ok(!preg_match('/(^|_)(gc|xp|wallet|rank|ranking|level|kills|battle|playtime|payment|purchase|account|bank|resident|rrn)(_|$)/i', $column['c'])
                || $column['c'] === 'school_level', $column['t'] . '.' . $column['c']);
        }
    }

    // ── 권한 ────────────────────────────────────────────────

    public function testBenefitRolesAreSeparatedByDuty()
    {
        $can = function ($role, $permission) {
            return Auth::can(['role' => $role], $permission);
        };
        // 새 역할은 게임 운영 화면을 보지 못하고, 게임 운영 역할은 현실 혜택을 다루지 못한다.
        foreach ([Auth::ROLE_PASS, Auth::ROLE_SCHOLARSHIP, Auth::ROLE_REVIEWER, Auth::ROLE_COMPLIANCE, Auth::ROLE_FINANCE] as $role) {
            T::ok(!$can($role, Auth::VIEW), $role);
            T::ok(!$can($role, Auth::BALANCE) && !$can($role, Auth::OPERATE), $role);
        }
        foreach ([Auth::ROLE_OPERATOR, Auth::ROLE_BALANCE, Auth::ROLE_SUPPORT, Auth::ROLE_VIEWER] as $role) {
            foreach ([Auth::PASS_VIEW, Auth::PASS_MANAGE, Auth::SCH_VIEW, Auth::SCH_MANAGE, Auth::SCH_PII, Auth::SCH_REVIEW, Auth::SCH_DISBURSE,
                         Auth::FEATURE_LEGAL, Auth::FEATURE_ENABLE, Auth::BENEFIT_AUDIT] as $permission) {
                T::ok(!$can($role, $permission), "$role / $permission");
            }
        }
        // CITY PASS 담당은 장학을, 장학 담당은 CITY PASS 를 다루지 못한다.
        T::ok($can(Auth::ROLE_PASS, Auth::PASS_MANAGE) && !$can(Auth::ROLE_PASS, Auth::SCH_VIEW) && !$can(Auth::ROLE_PASS, Auth::SCH_PII));
        T::ok($can(Auth::ROLE_SCHOLARSHIP, Auth::SCH_MANAGE) && !$can(Auth::ROLE_SCHOLARSHIP, Auth::PASS_VIEW));
        // 심사위원은 심사만, 지급 담당은 지급 상태만.
        foreach ([Auth::SCH_VIEW, Auth::SCH_MANAGE, Auth::SCH_PII, Auth::SCH_APPROVE, Auth::SCH_DISBURSE, Auth::PASS_VIEW] as $permission) {
            T::ok(!$can(Auth::ROLE_REVIEWER, $permission), $permission);
        }
        T::ok($can(Auth::ROLE_REVIEWER, Auth::SCH_REVIEW));
        T::ok($can(Auth::ROLE_FINANCE, Auth::SCH_DISBURSE) && !$can(Auth::ROLE_FINANCE, Auth::SCH_PII) && !$can(Auth::ROLE_FINANCE, Auth::SCH_MANAGE));
        // 최고 관리자도 심사 점수는 넣지 못한다. 준법 담당은 개인정보를 열지 못하지만 열람 기록은 본다.
        T::ok(!$can(Auth::ROLE_SUPER, Auth::SCH_REVIEW));
        T::ok(!$can(Auth::ROLE_COMPLIANCE, Auth::SCH_PII) && $can(Auth::ROLE_COMPLIANCE, Auth::BENEFIT_AUDIT));
        T::ok(!Auth::can(['role' => 'nobody'], Auth::VIEW) && !Auth::can(['role' => Auth::ROLE_SUPER], 'no.such.permission'));
        // 역할 이름은 admins.role (16자) 에 들어가야 한다.
        foreach (array_keys(Auth::roles()) as $role) {
            T::ok(strlen($role) <= 16, $role);
        }
    }

    // ── 암호화 ──────────────────────────────────────────────

    public function testCryptoRoundTripsAndRejectsTampering()
    {
        $crypto = new Crypto(Crypto::newKey());
        T::ok($crypto->ready());
        $sealed = $crypto->seal('홍길동', 'profile.name');
        T::ok(strpos($sealed, 'v1:') === 0 && strpos($sealed, '홍길동') === false);
        T::eq('홍길동', $crypto->open($sealed, 'profile.name'));
        T::ok($sealed !== $crypto->seal('홍길동', 'profile.name'), '같은 글자도 매번 다른 암호문이 된다');
        T::eq(null, $crypto->seal('', 'x'));
        T::eq(null, $crypto->seal(null, 'x'));
        T::eq(null, $crypto->open(null, 'x'));

        $fails = function (callable $work) {
            try {
                $work();
            } catch (\RuntimeException $error) {
                return true;
            }
            return false;
        };
        // 다른 용도, 다른 키, 손댄 암호문은 풀리지 않는다.
        T::ok($fails(function () use ($crypto, $sealed) {
            $crypto->open($sealed, 'profile.email');
        }));
        T::ok($fails(function () use ($sealed) {
            (new Crypto(Crypto::newKey()))->open($sealed, 'profile.name');
        }));
        $raw = base64_decode(substr($sealed, 3));
        $raw[strlen($raw) - 1] = chr(ord($raw[strlen($raw) - 1]) ^ 1);
        T::ok($fails(function () use ($crypto, $raw) {
            $crypto->open('v1:' . base64_encode($raw), 'profile.name');
        }));
        // 키가 없거나 길이가 맞지 않으면 평문으로 넘어가지 않고 멈춘다.
        foreach (['', 'not-base64!', base64_encode('short')] as $bad) {
            $broken = new Crypto($bad);
            T::ok(!$broken->ready());
            T::ok($fails(function () use ($broken) {
                $broken->seal('값', 'x');
            }));
        }
    }
}
