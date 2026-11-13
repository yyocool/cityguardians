<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * CITY PASS: 상점 쪽 (docs/COUPON_SYSTEM.md 4~5장).
 *
 * 상점 직원이 로그인해 손님이 보여 준 일회용 코드를 넣으면, 서버가 쿠폰을 확인하고 "사용됨"으로 바꾼다.
 *
 * - 상점 계정은 운영 관리자가 만든다. 자기 업체(지점 계정이면 자기 지점)의 쿠폰과 내역만 본다.
 *   다른 업체의 쿠폰 코드를 넣으면 "없는 코드"와 똑같이 답한다 (그 쿠폰이 있다는 것도 알려 주지 않는다).
 * - 같은 쿠폰은 한 번만 쓰인다: 상태를 issued → redeemed 로 바꾸는 UPDATE 가 한 행을 바꿨을 때만 사용 기록을 넣고,
 *   사용 기록의 coupon_id 는 유일하다. 두 직원이 동시에 눌러도 한쪽만 성공한다.
 * - 손님의 닉네임, 회원번호, 계정은 상점 화면에 나오지 않는다.
 * - 확인에 실패한 시도는 거래 로그에 남고, 많이 틀리면 잠시 막힌다 (코드를 추측하지 못하게).
 */
final class MerchantPortal
{
    const COOKIE = 'cg_merchant';
    const IDLE_MINUTES = 30;
    const MAX_HOURS = 12;
    const MAX_FAILURES = 5;
    const LOCK_MINUTES = 15;
    /** 10분 동안 틀릴 수 있는 쿠폰 코드의 수. */
    const MAX_BAD_CODES = 10;

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    public function audit($user, $action, $target = '', $detail = null, $ip = '')
    {
        $this->app->db->exec(
            'INSERT INTO merchant_audit_logs (merchant_user_id, merchant_id, action, target, detail, ip, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$user !== null ? $user['id'] : null, $user !== null ? $user['merchantId'] : null, mb_substr($action, 0, 64), mb_substr((string)$target, 0, 64),
                $detail !== null ? mb_substr((string)$detail, 0, 500) : null, mb_substr((string)$ip, 0, 45), Clock::sql()]
        );
    }

    private static function userOf(array $row)
    {
        return [
            'id' => (int)$row['id'], 'merchantId' => (int)$row['merchant_id'],
            'branchId' => $row['branch_id'] !== null ? (int)$row['branch_id'] : null,
            'login' => $row['login'], 'displayName' => $row['display_name'], 'role' => $row['role'],
            'mustChangePassword' => (int)$row['must_change_password'] === 1,
        ];
    }

    // ── 로그인 ──────────────────────────────────────────────

    /** 성공하면 [쿠키에 넣을 토큰, 계정]. 실패하면 null (이유는 알려 주지 않는다). */
    public function login($login, $password, $ip)
    {
        $this->app->rateLimit()->hit('merchant_login', $ip, $this->app->env->int('MERCHANT_LOGIN_PER_10MIN', 10), 600);
        $db = $this->app->db;
        $login = is_string($login) ? strtolower(trim($login)) : '';
        $row = $login !== '' && strlen($login) <= 40 ? $db->one(
            'SELECT u.*, m.status AS merchant_status FROM merchant_users u JOIN partner_merchants m ON m.id = u.merchant_id WHERE u.login = ?', [$login]) : null;
        if ($row === null) {
            // 없는 계정이어도 같은 만큼 시간을 쓴다.
            password_verify((string)$password, '$2y$10$usesomesillystringfore7hnbRJHxXVLeakoG8K30oukPsA.ztMG');
            $this->audit(null, 'login.failed', mb_substr($login, 0, 40), 'unknown', $ip);
            return null;
        }
        $user = self::userOf($row);
        $now = Clock::now();
        if ($row['locked_until'] !== null && Clock::parse($row['locked_until']) > $now) {
            $this->audit($user, 'login.locked', $row['login'], null, $ip);
            return null;
        }
        // 제휴가 끝난 업체의 계정은 들어올 수 없다.
        if (!is_string($password) || !password_verify($password, $row['password_hash']) || !(int)$row['is_active'] || $row['merchant_status'] === 'ended') {
            $failures = (int)$row['failed_attempts'] + 1;
            $lock = $failures >= self::MAX_FAILURES ? Clock::sql($now + self::LOCK_MINUTES * 60) : null;
            $db->exec('UPDATE merchant_users SET failed_attempts = ?, locked_until = ? WHERE id = ?', [$lock !== null ? 0 : $failures, $lock, $row['id']]);
            $this->audit($user, 'login.failed', $row['login'], $lock !== null ? 'locked' : null, $ip);
            return null;
        }
        $token = Ids::secret();
        $db->exec('UPDATE merchant_users SET failed_attempts = 0, locked_until = NULL, last_login_at = ? WHERE id = ?', [Clock::sql(), $row['id']]);
        $db->exec('INSERT INTO merchant_sessions (merchant_user_id, token_hash, csrf_token, ip, created_at, last_seen_at, expires_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$row['id'], Ids::sha256($token), Ids::secret(), mb_substr((string)$ip, 0, 45), Clock::sql(), Clock::sql(), Clock::sql($now + self::MAX_HOURS * 3600)]);
        $this->audit($user, 'login', $row['login'], null, $ip);
        return [$token, $user];
    }

    /** @return array|null user (+ merchantName, merchantStatus), csrf */
    public function session($token)
    {
        if (!is_string($token) || !preg_match('/^[0-9a-f]{64}$/', $token)) {
            return null;
        }
        $db = $this->app->db;
        $now = Clock::now();
        $row = $db->one(
            'SELECT s.id AS session_id, s.csrf_token, u.*, m.name AS merchant_name, m.status AS merchant_status, b.name AS branch_name'
            . ' FROM merchant_sessions s JOIN merchant_users u ON u.id = s.merchant_user_id JOIN partner_merchants m ON m.id = u.merchant_id'
            . ' LEFT JOIN partner_branches b ON b.id = u.branch_id'
            . " WHERE s.token_hash = ? AND s.revoked_at IS NULL AND s.expires_at > ? AND s.last_seen_at > ? AND u.is_active = 1 AND m.status <> 'ended'",
            [Ids::sha256($token), Clock::sql($now), Clock::sql($now - self::IDLE_MINUTES * 60)]
        );
        if ($row === null) {
            return null;
        }
        $db->exec('UPDATE merchant_sessions SET last_seen_at = ? WHERE id = ?', [Clock::sql($now), $row['session_id']]);
        return ['user' => self::userOf($row) + ['merchantName' => $row['merchant_name'], 'merchantStatus' => $row['merchant_status'],
            'branchName' => $row['branch_name']], 'csrf' => $row['csrf_token']];
    }

    public function logout($token)
    {
        if (is_string($token) && $token !== '') {
            $this->app->db->exec('UPDATE merchant_sessions SET revoked_at = ? WHERE token_hash = ? AND revoked_at IS NULL', [Clock::sql(), Ids::sha256($token)]);
        }
    }

    /**
     * 비밀번호를 바꾼다. 다른 접속은 모두 끊긴다.
     * @return string[] 문제 목록
     */
    public function changePassword(array $user, $current, $new, $again, $keepToken, $ip = '')
    {
        $db = $this->app->db;
        $hash = $db->val('SELECT password_hash FROM merchant_users WHERE id = ?', [$user['id']]);
        $problems = [];
        if (!is_string($current) || !password_verify($current, (string)$hash)) {
            $problems[] = '지금 비밀번호가 맞지 않습니다.';
        }
        $problem = Auth::passwordProblem($new);
        if ($problem !== null) {
            $problems[] = $problem;
        } elseif ($new !== $again) {
            $problems[] = '새 비밀번호를 두 번 똑같이 적어 주세요.';
        } elseif ($new === $current) {
            $problems[] = '지금 비밀번호와 다른 것으로 정해 주세요.';
        }
        if ($problems) {
            $this->audit($user, 'password.change_failed', $user['login'], null, $ip);
            return $problems;
        }
        $db->tx(function () use ($db, $user, $new, $keepToken, $ip) {
            $db->exec('UPDATE merchant_users SET password_hash = ?, must_change_password = 0 WHERE id = ?', [password_hash($new, PASSWORD_DEFAULT), $user['id']]);
            $db->exec('UPDATE merchant_sessions SET revoked_at = ? WHERE merchant_user_id = ? AND revoked_at IS NULL AND token_hash <> ?',
                [Clock::sql(), $user['id'], Ids::sha256((string)$keepToken)]);
            $this->audit($user, 'password.change', $user['login'], null, $ip);
        });
        return [];
    }

    // ── 쿠폰 확인과 사용 처리 ───────────────────────────────

    private function failed(array $user, $reason, $couponId, $ip)
    {
        $this->app->db->exec(
            'INSERT INTO coupon_events (coupon_id, event, merchant_id, merchant_user_id, detail, created_at) VALUES (?, ?, ?, ?, ?, ?)',
            [$couponId, 'verify_failed', $user['merchantId'], $user['id'], $reason, Clock::sql()]);
        $this->audit($user, 'coupon.verify_failed', '', $reason, $ip);
    }

    /** 쿠폰을 다룰 수 있는 상태인지 확인한다. */
    private function guard(array $user)
    {
        $this->app->features()->mustBeEnabled(Features::PASS_COUPONS);
        if ($user['mustChangePassword']) {
            throw new ApiError(ApiError::FORBIDDEN, '먼저 비밀번호를 바꿔 주세요.', 'password_change_required');
        }
        if ($this->app->db->val('SELECT status FROM partner_merchants WHERE id = ?', [$user['merchantId']]) !== 'approved') {
            throw new ApiError(ApiError::FORBIDDEN, '지금은 쿠폰을 처리할 수 없는 상태입니다. 운영팀에 문의해 주세요.', 'merchant_unavailable');
        }
        $bad = (int)$this->app->db->val("SELECT COUNT(*) FROM coupon_events WHERE merchant_user_id = ? AND event = 'verify_failed' AND created_at > ?",
            [$user['id'], Clock::sql(Clock::now() - 600)]);
        if ($bad >= self::MAX_BAD_CODES) {
            throw new ApiError(ApiError::RATE_LIMITED, '코드를 여러 번 틀렸습니다. 10분 뒤에 다시 시도해 주세요.', 'too_many_bad_codes');
        }
    }

    /**
     * 코드에 해당하는 쿠폰을 찾는다. 이 계정이 처리할 수 없는 쿠폰이면 없는 것으로 답한다.
     * @return array [쿠폰 행 또는 null, 실패 이유 또는 null]
     */
    private function find(array $user, $code, $lock)
    {
        $code = CityPass::normalizeCode($code);
        if (strlen($code) !== CityPass::PRESENT_CODE_LENGTH) {
            return [null, 'bad_format'];
        }
        $coupon = $this->app->db->one(
            'SELECT c.*, o.title, o.description, o.discount_type, o.discount_value, o.conditions, o.branch_id AS offer_branch_id'
            . ' FROM benefit_coupons c JOIN partner_offers o ON o.id = c.offer_id WHERE c.present_code_hash = ?' . ($lock ? ' FOR UPDATE' : ''),
            [CityPass::hashCode($code)]);
        if ($coupon === null) {
            return [null, 'unknown_code'];
        }
        if ((int)$coupon['merchant_id'] !== $user['merchantId']) {
            return [null, 'other_merchant'];
        }
        if ($user['branchId'] !== null && $coupon['offer_branch_id'] !== null && (int)$coupon['offer_branch_id'] !== $user['branchId']) {
            return [null, 'other_branch'];
        }
        if ($coupon['status'] !== CityPass::ISSUED) {
            return [null, 'not_issued'];
        }
        if (Clock::parse($coupon['present_expires_at']) <= Clock::now()) {
            return [null, 'code_expired'];
        }
        if (Clock::parse($coupon['expires_at']) <= Clock::now()) {
            return [null, 'coupon_expired'];
        }
        return [$coupon, null];
    }

    private static function failureMessage($reason)
    {
        switch ($reason) {
            case 'code_expired': return '코드의 시간이 지났습니다. 손님에게 코드를 새로 받아 주세요.';
            case 'coupon_expired': return '유효기간이 지난 쿠폰입니다.';
            case 'other_branch': return '이 지점에서는 쓸 수 없는 쿠폰입니다.';
            // 모르는 코드, 다른 업체의 쿠폰, 이미 쓴 쿠폰은 구별하지 않는다.
            default: return '쓸 수 있는 쿠폰을 찾지 못했습니다. 코드를 다시 확인해 주세요.';
        }
    }

    private function branchChoices(array $user, array $coupon)
    {
        $db = $this->app->db;
        if ($user['branchId'] !== null) {
            return $db->all('SELECT id, name FROM partner_branches WHERE id = ?', [$user['branchId']]);
        }
        if ($coupon['offer_branch_id'] !== null) {
            return $db->all('SELECT id, name FROM partner_branches WHERE id = ?', [$coupon['offer_branch_id']]);
        }
        return $db->all('SELECT id, name FROM partner_branches WHERE merchant_id = ? AND is_active = 1 ORDER BY id', [$user['merchantId']]);
    }

    /**
     * 코드를 확인만 한다 (쓰지 않는다). 직원이 혜택과 조건을 확인하고 "사용 처리"를 누르게 한다.
     * 손님의 정보는 돌려주지 않는다.
     */
    public function verify(array $user, $code, $ip = '')
    {
        $this->guard($user);
        list($coupon, $reason) = $this->find($user, $code, false);
        if ($coupon === null) {
            $this->failed($user, $reason, null, $ip);
            throw new ApiError(ApiError::NOT_FOUND, self::failureMessage($reason), 'coupon_not_usable');
        }
        $this->audit($user, 'coupon.verify', $coupon['public_id'], null, $ip);
        return [
            'title' => $coupon['title'], 'description' => $coupon['description'],
            'discountText' => CityPass::discountText($coupon['discount_type'], $coupon['discount_value']),
            'conditions' => $coupon['conditions'], 'expiresAt' => $coupon['expires_at'],
            'codeExpiresAt' => $coupon['present_expires_at'],
            'branches' => $this->branchChoices($user, $coupon),
        ];
    }

    /**
     * 쿠폰을 사용 처리한다. 한 번만 성공한다.
     * @param string|int|null $branchId 본사 계정이 고른 지점. 지점 계정이면 무시한다
     */
    public function redeem(array $user, $code, $branchId = null, $ip = '')
    {
        $this->guard($user);
        $db = $this->app->db;
        $failure = null;
        try {
            return $db->tx(function () use ($db, $user, $code, $branchId, $ip, &$failure) {
                list($coupon, $reason) = $this->find($user, $code, true);
                if ($coupon === null) {
                    $failure = [$reason, null];
                    throw new ApiError(ApiError::NOT_FOUND, self::failureMessage($reason), 'coupon_not_usable');
                }
                $choices = $this->branchChoices($user, $coupon);
                $branch = null;
                if (count($choices) === 1) {
                    $branch = (int)$choices[0]['id'];
                } else {
                    foreach ($choices as $choice) {
                        if ((string)$choice['id'] === (string)$branchId) {
                            $branch = (int)$choice['id'];
                        }
                    }
                    if ($branch === null && $choices) {
                        throw new ApiError(ApiError::VALIDATION_FAILED, '쿠폰을 쓰는 지점을 골라 주세요.', 'branch_required');
                    }
                }
                $now = Clock::sql();
                // 원자적 상태 변경: 아직 쓰지 않았고, 이 코드가 살아 있을 때만 한 행이 바뀐다.
                $changed = $db->exec(
                    "UPDATE benefit_coupons SET status = 'redeemed', redeemed_at = ?, present_code_hash = NULL, present_expires_at = NULL"
                    . " WHERE id = ? AND status = 'issued' AND present_code_hash = ? AND present_expires_at > ? AND expires_at > ?",
                    [$now, $coupon['id'], $coupon['present_code_hash'], $now, $now]);
                if ($changed !== 1) {
                    $failure = ['not_issued', (int)$coupon['id']];
                    throw new ApiError(ApiError::NOT_FOUND, self::failureMessage('not_issued'), 'coupon_not_usable');
                }
                // coupon_id 가 유일해서, 어떤 이유로든 두 번째 기록은 들어가지 못한다.
                $db->exec('INSERT INTO coupon_redemptions (coupon_id, offer_id, merchant_id, branch_id, merchant_user_id, membership_id, redeemed_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                    [$coupon['id'], $coupon['offer_id'], $coupon['merchant_id'], $branch, $user['id'], $coupon['membership_id'], $now]);
                $db->exec('INSERT INTO coupon_events (coupon_id, event, merchant_id, merchant_user_id, player_id, detail, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                    [$coupon['id'], 'redeemed', $coupon['merchant_id'], $user['id'], $coupon['player_id'], $branch !== null ? 'branch=' . $branch : null, $now]);
                $this->audit($user, 'coupon.redeem', $coupon['public_id'], $branch !== null ? 'branch=' . $branch : null, $ip);
                return ['redeemed' => true, 'title' => $coupon['title'],
                    'discountText' => CityPass::discountText($coupon['discount_type'], $coupon['discount_value']), 'redeemedAt' => $now];
            });
        } catch (ApiError $error) {
            // 거래가 되돌려진 뒤에 실패를 적는다 (되돌려져도 실패한 시도는 남아야 한다).
            if ($failure !== null) {
                $this->failed($user, $failure[0], $failure[1], $ip);
            }
            throw $error;
        }
    }

    // ── 이용 내역, 통계 ─────────────────────────────────────

    /** 이 계정이 볼 수 있는 범위: 자기 업체, 지점 계정이면 자기 지점. */
    private static function scope(array $user, $alias = 'r')
    {
        return $user['branchId'] !== null
            ? ["$alias.merchant_id = ? AND $alias.branch_id = ?", [$user['merchantId'], $user['branchId']]]
            : ["$alias.merchant_id = ?", [$user['merchantId']]];
    }

    /** 사용 처리한 쿠폰. 손님은 보이지 않는다. */
    public function history(array $user, $page = 1)
    {
        list($where, $params) = self::scope($user);
        return $this->app->db->all(
            'SELECT r.redeemed_at, o.title, o.discount_type, o.discount_value, b.name AS branch_name, u.display_name AS staff_name'
            . ' FROM coupon_redemptions r JOIN partner_offers o ON o.id = r.offer_id LEFT JOIN partner_branches b ON b.id = r.branch_id'
            . " JOIN merchant_users u ON u.id = r.merchant_user_id WHERE $where ORDER BY r.id DESC LIMIT 50 OFFSET " . (max(1, (int)$page) - 1) * 50, $params);
    }

    public function stats(array $user)
    {
        $db = $this->app->db;
        list($where, $params) = self::scope($user);
        $kstToday = Clock::koreaDate();
        $todayStart = Clock::sql((int)strtotime($kstToday . ' 00:00:00 UTC') - 9 * 3600);
        $out = [
            'today' => (int)$db->val("SELECT COUNT(*) FROM coupon_redemptions r WHERE $where AND r.redeemed_at >= ?", array_merge($params, [$todayStart])),
            'last7Days' => (int)$db->val("SELECT COUNT(*) FROM coupon_redemptions r WHERE $where AND r.redeemed_at >= ?",
                array_merge($params, [Clock::sql(Clock::now() - 7 * 86400)])),
            'total' => (int)$db->val("SELECT COUNT(*) FROM coupon_redemptions r WHERE $where", $params),
            'byOffer' => $db->all("SELECT o.title, COUNT(*) AS n FROM coupon_redemptions r JOIN partner_offers o ON o.id = r.offer_id WHERE $where"
                . ' GROUP BY o.id, o.title ORDER BY n DESC LIMIT 50', $params),
            'daily' => $db->all("SELECT DATE(DATE_ADD(r.redeemed_at, INTERVAL 9 HOUR)) AS day, COUNT(*) AS n FROM coupon_redemptions r WHERE $where"
                . ' AND r.redeemed_at >= ? GROUP BY day ORDER BY day DESC', array_merge($params, [Clock::sql(Clock::now() - 14 * 86400)])),
            // 발급 수는 업체 전체의 것만 보여 준다 (지점 계정에는 보이지 않는다).
            'offers' => $user['branchId'] === null ? $db->all(
                'SELECT title, status, issued_count, total_limit, ends_at FROM partner_offers WHERE merchant_id = ? ORDER BY id DESC LIMIT 50', [$user['merchantId']]) : [],
        ];
        return $out;
    }

    /** 지난 접속 기록을 정리한다. 예약 작업이 부른다. */
    public function purge()
    {
        return $this->app->db->exec('DELETE FROM merchant_sessions WHERE expires_at < ?', [Clock::sql(Clock::now() - 7 * 86400)]);
    }
}
