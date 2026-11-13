<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;

/**
 * 현실 혜택 기능의 승인 절차 (docs/REAL_WORLD_REWARDS_POLICY.md 4장).
 *
 * 기능은 모두 꺼진 채로 설치된다. 켜려면
 *   1) 준법 담당(또는 최고 관리자)이 법률 검토 완료를 기록하고 (문서 번호, 검토한 곳, 날짜)
 *   2) 그 기록을 한 사람이 아닌 최고 관리자가 사유를 적고 켠다.
 * 끄는 것은 언제든 할 수 있고, 법률 검토 상태가 "완료"에서 벗어나면 저절로 꺼진다.
 *
 * 게임머니의 현실 교환, 전투 성적·랭킹에 따른 현금성 보상과 장학금, 결제에 따른 가산점,
 * 게임 내 금융 수익의 현금화는 여기에 스위치가 없다. 만들지 않았기 때문이다.
 */
final class Features
{
    const PASS_MEMBERSHIP = 'city_pass_membership';
    const PASS_COUPONS = 'city_pass_coupons';
    const SCH_APPLICATIONS = 'scholarship_applications';
    const SCH_AWARDS = 'scholarship_awards';

    const NOT_REVIEWED = 'not_reviewed';
    const IN_REVIEW = 'in_review';
    const APPROVED = 'approved';
    const REJECTED = 'rejected';

    /** @var App */
    private $app;
    /** @var array<string,array>|null */
    private $cache = null;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    public static function legalStatuses()
    {
        return [self::NOT_REVIEWED => '검토 전', self::IN_REVIEW => '검토 중', self::APPROVED => '검토 완료 (승인)', self::REJECTED => '반려'];
    }

    /** 이 기능이 동작하려면 함께 켜져 있어야 하는 기능. */
    private static function requires($code)
    {
        return $code === self::PASS_COUPONS ? [self::PASS_MEMBERSHIP] : [];
    }

    /** 만들지 않은 기능의 목록 (관리자 화면과 문서에 그대로 보여 준다). */
    public static function notBuilt()
    {
        return [
            '게임머니(GC)를 현실 상품으로 교환',
            '전투 성적에 따른 현금성 보상',
            '게임 랭킹에 따른 장학금 자동 지급',
            '유료 아이템 구매에 따른 장학금 가산점',
            '게임 내 대출·주식 수익의 현실 현금화',
        ];
    }

    /** @return array<string,array> code => 행 */
    public function all()
    {
        if ($this->cache === null) {
            $this->cache = [];
            foreach ($this->app->db->all('SELECT * FROM benefit_features ORDER BY sort_order, code') as $row) {
                $this->cache[$row['code']] = $row;
            }
        }
        return $this->cache;
    }

    public function forget()
    {
        $this->cache = null;
    }

    /** 서버 설정으로 모두 잠갔는가 (비상용). */
    public function locked()
    {
        return $this->app->env->bool('BENEFITS_LOCKED', false);
    }

    public function isEnabled($code)
    {
        if ($this->locked()) {
            return false;
        }
        $all = $this->all();
        // 켜져 있다고 적혀 있어도 법률 검토가 "완료"가 아니면 꺼진 것으로 본다 (DB 를 직접 고친 경우를 막는다).
        if (!isset($all[$code]) || (int)$all[$code]['is_enabled'] !== 1 || $all[$code]['legal_status'] !== self::APPROVED) {
            return false;
        }
        foreach (self::requires($code) as $other) {
            if (!$this->isEnabled($other)) {
                return false;
            }
        }
        return true;
    }

    /** 꺼져 있으면 FEATURE_DISABLED 오류. */
    public function mustBeEnabled($code)
    {
        if (!$this->isEnabled($code)) {
            throw new ApiError(ApiError::FEATURE_DISABLED, '아직 준비 중인 기능이에요.', $code);
        }
    }

    private function row($code)
    {
        $row = $this->app->db->one('SELECT * FROM benefit_features WHERE code = ? FOR UPDATE', [$code]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 기능입니다.');
        }
        return $row;
    }

    private static function snapshot(array $row)
    {
        return json_encode([
            'isEnabled' => (int)$row['is_enabled'], 'legalStatus' => $row['legal_status'], 'legalReference' => $row['legal_reference'],
            'legalReviewer' => $row['legal_reviewer'], 'legalReviewedOn' => $row['legal_reviewed_on'],
        ], JSON_UNESCAPED_UNICODE);
    }

    private function history($code, $action, array $before, array $after, $adminId, $reason, $ip)
    {
        $this->app->db->exec(
            'INSERT INTO benefit_feature_history (feature_code, action, before_value, after_value, admin_id, reason, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$code, $action, self::snapshot($before), self::snapshot($after), $adminId, mb_substr($reason, 0, 255), Clock::sql()]
        );
        (new Auth($this->app))->audit($adminId, 'feature.' . $action, 'benefit_feature', $code, $reason, $ip);
    }

    private static function reasonProblems($reason)
    {
        $length = mb_strlen(trim((string)$reason));
        return $length < 2 || $length > 255 ? ['사유를 2~255자로 적어 주세요.'] : [];
    }

    /**
     * 법률 검토 상태를 기록한다.
     * @return string[] 문제 목록. 비어 있으면 기록했다.
     */
    public function recordLegal(array $admin, $code, array $input, $ip = '')
    {
        if (!Auth::can($admin, Auth::FEATURE_LEGAL)) {
            throw new ApiError(ApiError::FORBIDDEN, '이 계정의 역할로는 할 수 없는 일입니다.');
        }
        $status = isset($input['legalStatus']) ? (string)$input['legalStatus'] : '';
        $reference = trim(isset($input['legalReference']) ? (string)$input['legalReference'] : '');
        $reviewer = trim(isset($input['legalReviewer']) ? (string)$input['legalReviewer'] : '');
        $reviewedOn = trim(isset($input['legalReviewedOn']) ? (string)$input['legalReviewedOn'] : '');
        $note = trim(isset($input['legalNote']) ? (string)$input['legalNote'] : '');
        $reason = trim(isset($input['reason']) ? (string)$input['reason'] : '');

        $problems = self::reasonProblems($reason);
        if (!isset(self::legalStatuses()[$status])) {
            $problems[] = '검토 상태를 골라 주세요.';
        }
        if (mb_strlen($reference) > 190 || mb_strlen($reviewer) > 80 || mb_strlen($note) > 500) {
            $problems[] = '적은 내용이 너무 깁니다.';
        }
        if ($reviewedOn !== '') {
            $date = \DateTime::createFromFormat('!Y-m-d', $reviewedOn, new \DateTimeZone('UTC'));
            if ($date === false || $date->format('Y-m-d') !== $reviewedOn) {
                $problems[] = '검토한 날짜는 2026-11-13 처럼 적어 주세요.';
            } elseif ($reviewedOn > Clock::koreaDate()) {
                $problems[] = '검토한 날짜가 오늘보다 뒤입니다.';
            }
        }
        if ($status === self::APPROVED && ($reference === '' || $reviewer === '' || $reviewedOn === '')) {
            $problems[] = '검토 완료로 기록하려면 검토 의견서의 문서 번호, 검토한 곳, 검토한 날짜가 모두 있어야 합니다.';
        }
        if ($problems) {
            return $problems;
        }

        $this->app->db->tx(function () use ($admin, $code, $status, $reference, $reviewer, $reviewedOn, $note, $reason, $ip) {
            $before = $this->row($code);
            $after = $before;
            $after['legal_status'] = $status;
            $after['legal_reference'] = $reference !== '' ? $reference : null;
            $after['legal_reviewer'] = $reviewer !== '' ? $reviewer : null;
            $after['legal_reviewed_on'] = $reviewedOn !== '' ? $reviewedOn : null;
            $this->app->db->exec(
                'UPDATE benefit_features SET legal_status = ?, legal_reference = ?, legal_reviewer = ?, legal_reviewed_on = ?, legal_note = ?,'
                . ' legal_recorded_by = ?, legal_recorded_at = ?, updated_at = ? WHERE code = ?',
                [$status, $after['legal_reference'], $after['legal_reviewer'], $after['legal_reviewed_on'], $note !== '' ? $note : null,
                    $admin['id'], Clock::sql(), Clock::sql(), $code]
            );
            $this->history($code, 'legal_update', $before, $after, $admin['id'], $reason, $ip);
            // 검토 완료가 아니게 되면 켜 둘 수 없다.
            if ($status !== self::APPROVED && (int)$before['is_enabled'] === 1) {
                $off = $after;
                $off['is_enabled'] = 0;
                $this->app->db->exec('UPDATE benefit_features SET is_enabled = 0, updated_at = ? WHERE code = ?', [Clock::sql(), $code]);
                $this->history($code, 'auto_disable', $after, $off, $admin['id'], '법률 검토 상태가 완료가 아니게 되어 자동으로 껐습니다.', $ip);
            }
        });
        $this->forget();
        return [];
    }

    /**
     * 기능을 켠다.
     * @return string[] 문제 목록. 비어 있으면 켰다.
     */
    public function enable(array $admin, $code, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::FEATURE_ENABLE)) {
            throw new ApiError(ApiError::FORBIDDEN, '이 계정의 역할로는 할 수 없는 일입니다.');
        }
        $reason = trim((string)$reason);
        $problems = $this->app->db->tx(function () use ($admin, $code, $reason, $ip) {
            $before = $this->row($code);
            $problems = self::reasonProblems($reason);
            if ($this->locked()) {
                $problems[] = '서버 설정(BENEFITS_LOCKED)으로 현실 혜택 기능이 모두 잠겨 있습니다.';
            }
            if ($before['legal_status'] !== self::APPROVED) {
                $problems[] = '법률 검토가 완료로 기록되어 있지 않습니다. 먼저 검토 결과를 기록해 주세요.';
            } elseif ($before['legal_recorded_by'] === null || (int)$before['legal_recorded_by'] === (int)$admin['id']) {
                $problems[] = '법률 검토 완료를 기록한 사람은 그 기능을 켤 수 없습니다. 다른 최고 관리자가 켜야 합니다.';
            }
            foreach (self::requires($code) as $other) {
                if (!$this->isEnabled($other)) {
                    $problems[] = '먼저 켜져 있어야 하는 기능이 꺼져 있습니다: ' . $other;
                }
            }
            if ($code === self::SCH_APPLICATIONS && !$this->app->crypto()->ready()) {
                $problems[] = '개인정보 암호화 키(DATA_KEY)가 서버에 설정되어 있지 않습니다.';
            }
            if ((int)$before['is_enabled'] === 1) {
                $problems[] = '이미 켜져 있습니다.';
            }
            if ($problems) {
                return $problems;
            }
            $after = $before;
            $after['is_enabled'] = 1;
            $this->app->db->exec('UPDATE benefit_features SET is_enabled = 1, enabled_by = ?, enabled_at = ?, updated_at = ? WHERE code = ?',
                [$admin['id'], Clock::sql(), Clock::sql(), $code]);
            $this->history($code, 'enable', $before, $after, $admin['id'], $reason, $ip);
            return [];
        });
        $this->forget();
        return $problems;
    }

    /**
     * 기능을 끈다. 언제든 할 수 있다.
     * @return string[] 문제 목록.
     */
    public function disable(array $admin, $code, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::FEATURE_LEGAL)) {
            throw new ApiError(ApiError::FORBIDDEN, '이 계정의 역할로는 할 수 없는 일입니다.');
        }
        $reason = trim((string)$reason);
        $problems = $this->app->db->tx(function () use ($admin, $code, $reason, $ip) {
            $before = $this->row($code);
            $problems = self::reasonProblems($reason);
            if ((int)$before['is_enabled'] !== 1) {
                $problems[] = '이미 꺼져 있습니다.';
            }
            if ($problems) {
                return $problems;
            }
            $after = $before;
            $after['is_enabled'] = 0;
            $this->app->db->exec('UPDATE benefit_features SET is_enabled = 0, updated_at = ? WHERE code = ?', [Clock::sql(), $code]);
            $this->history($code, 'disable', $before, $after, $admin['id'], $reason, $ip);
            return [];
        });
        $this->forget();
        return $problems;
    }

    public function historyOf($limit = 100)
    {
        return $this->app->db->all(
            'SELECT h.*, a.login FROM benefit_feature_history h LEFT JOIN admins a ON a.id = h.admin_id ORDER BY h.id DESC LIMIT ' . (int)$limit
        );
    }
}
