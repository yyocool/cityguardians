<?php
namespace CG\Admin;

use CG\App;
use CG\Infra\Clock;
use CG\Infra\Ids;
use CG\Infra\Totp;

/**
 * 관리자 계정과 접속 (docs/ADMIN_SYSTEM.md 2~3장).
 *
 * - 플레이어 계정과 별개의 admins 테이블. 웹에서 가입하는 길은 없다 (bin/create-admin.php).
 * - 비밀번호는 password_hash 로, 접속 토큰은 SHA-256 으로만 저장한다.
 * - 비밀번호와 일회용 코드(TOTP)가 둘 다 맞아야 한다. 같은 일회용 코드는 두 번 받지 않는다.
 * - 연속으로 틀리면 계정을 잠시 잠근다. 틀린 이유(없는 계정, 틀린 비밀번호, 틀린 코드)는 구별해 알려 주지 않는다.
 * - 변경과 로그인은 admin_audit_logs 에 남는다.
 */
final class Auth
{
    const COOKIE = 'cg_admin';
    /** 조작이 없으면 접속이 끝나는 시간(분). */
    const IDLE_MINUTES = 30;
    /** 조작이 있어도 접속이 끝나는 시간(시간). */
    const MAX_HOURS = 12;
    const MAX_FAILURES = 5;
    const LOCK_MINUTES = 15;
    const MIN_PASSWORD = 12;

    const ROLE_SUPER = 'super';
    const ROLE_OPERATOR = 'operator';
    const ROLE_BALANCE = 'balance';
    const ROLE_SUPPORT = 'support';
    const ROLE_VIEWER = 'viewer';
    // v7.0 현실 혜택의 역할. 업무마다 나눴다: 이 역할들은 게임 운영 화면(전투·위험도)을 보지 못한다.
    const ROLE_PASS = 'pass_manager';
    const ROLE_SCHOLARSHIP = 'sch_manager';   // admins.role 은 16자까지다
    const ROLE_REVIEWER = 'reviewer';
    const ROLE_COMPLIANCE = 'compliance';
    const ROLE_FINANCE = 'finance';
    // v9.0 도시 상징거리의 제작 담당. 모델링 작업과 파일 등록만 한다. 전투·위험도·재정은 고치지 못한다.
    const ROLE_CONTENT = 'content';

    /** 게임 운영 화면을 본다. */
    const VIEW = 'view';
    /** 정의·정책·랭킹 가중치를 고친다 (밸런스). */
    const BALANCE = 'balance';
    /** 위험도 수동 조정, 침공 시작·종료, 전투 모드 여닫기 (운영). */
    const OPERATE = 'operate';
    /** 감사 기록을 본다. */
    const AUDIT = 'audit';

    // ── v7.0 현실 혜택 (docs/ADMIN_SYSTEM.md 11장) ──
    /** 현실 혜택 기능의 상태를 본다. */
    const FEATURE_VIEW = 'feature.view';
    /** 법률 검토 상태를 기록하고, 기능을 끈다. */
    const FEATURE_LEGAL = 'feature.legal';
    /** 기능을 켠다. */
    const FEATURE_ENABLE = 'feature.enable';
    /** CITY PASS: 제휴 업체·혜택·쿠폰·사용 내역을 본다. */
    const PASS_VIEW = 'pass.view';
    /** CITY PASS: 제휴 업체·지점·혜택·상점 계정을 등록하고 고친다. 쿠폰을 취소한다. */
    const PASS_MANAGE = 'pass.manage';
    /** CITY PASS: 제휴 업체와 혜택을 승인한다 (등록한 사람은 승인할 수 없다). */
    const PASS_APPROVE = 'pass.approve';
    /** 장학: 프로그램과 신청 목록을 본다 (개인정보 제외). */
    const SCH_VIEW = 'sch.view';
    /** 장학: 프로그램을 만들고, 자격을 확인하고, 심사위원을 배정하고, 선정을 제안한다. */
    const SCH_MANAGE = 'sch.manage';
    /** 장학: 신청자의 개인정보를 연다 (열 때마다 기록이 남는다). */
    const SCH_PII = 'sch.pii';
    /** 장학: 배정받은 지원서를 심사한다. */
    const SCH_REVIEW = 'sch.review';
    /** 장학: 공고와 선정 결과를 승인한다 (작성·제안한 사람은 승인할 수 없다). */
    const SCH_APPROVE = 'sch.approve';
    /** 장학: 지급 상태를 기록한다. */
    const SCH_DISBURSE = 'sch.disburse';
    /** 현실 혜택의 감사 로그(개인정보 열람 기록, 상점 계정 기록)를 본다. */
    const BENEFIT_AUDIT = 'benefit.audit';

    // ── v9.0 도시 성장·협동 방어전·지방세 (docs/ADMIN_SYSTEM.md 12장) ──
    /** 도시 성장, 모델링, 협동 방어전, 지방세, 재정, 참여예산 화면을 본다. */
    const CITY_VIEW = 'city.view';
    /** 전입자 기준, 성장 단계, 세금 정책, 협동 방어전·핑·참여예산 설정을 고친다. */
    const CITY_POLICY = 'city.policy';
    /** 기준 도달 알림을 확인하고 담당자를 배정한다. */
    const GROWTH_MANAGE = 'growth.manage';
    /** 모델링 제작 작업을 등록하고 진행 상태를 바꾸고 파일을 등록한다. */
    const MODELING_MANAGE = 'modeling.manage';
    /** 모델링 작업과 파일을 검수한다 (만든 사람은 검수할 수 없다). */
    const MODELING_REVIEW = 'modeling.review';
    /** 검수된 지역 콘텐츠를 운영 중인 클라이언트에 배포한다. */
    const CONTENT_RELEASE = 'content.release';
    /** 배포한 지역 콘텐츠를 내린다 (문제가 생겼을 때 빨리 되돌릴 수 있게 배포보다 넓게 준다). */
    const CONTENT_WITHDRAW = 'content.withdraw';
    /** 협동 방어전을 강제로 닫고, 전술 핑을 막거나 푼다. */
    const COOP_MANAGE = 'coop.manage';
    /** 도시 재정을 집행한다. */
    const TREASURY_EXECUTE = 'treasury.execute';
    /** 참여예산 제안을 검토하고 취소한다. */
    const BUDGET_MANAGE = 'budget.manage';

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    public static function roles()
    {
        return [
            self::ROLE_SUPER => '최고 관리자', self::ROLE_OPERATOR => '운영자', self::ROLE_BALANCE => '밸런스 담당',
            self::ROLE_SUPPORT => '고객지원', self::ROLE_VIEWER => '조회 전용',
            self::ROLE_PASS => 'CITY PASS 담당', self::ROLE_SCHOLARSHIP => '장학 담당', self::ROLE_REVIEWER => '심사위원',
            self::ROLE_COMPLIANCE => '준법·개인정보 담당', self::ROLE_FINANCE => '지급 담당',
            self::ROLE_CONTENT => '콘텐츠 제작 담당',
        ];
    }

    /** 권한마다 그 일을 할 수 있는 역할. 여기에 없는 권한은 아무도 할 수 없다. */
    private static function grants()
    {
        $super = self::ROLE_SUPER;
        return [
            self::VIEW => [$super, self::ROLE_OPERATOR, self::ROLE_BALANCE, self::ROLE_SUPPORT, self::ROLE_VIEWER],
            self::BALANCE => [$super, self::ROLE_BALANCE],
            self::OPERATE => [$super, self::ROLE_OPERATOR],
            self::AUDIT => [$super, self::ROLE_OPERATOR, self::ROLE_COMPLIANCE],
            self::FEATURE_VIEW => [$super, self::ROLE_COMPLIANCE, self::ROLE_PASS, self::ROLE_SCHOLARSHIP, self::ROLE_OPERATOR],
            self::FEATURE_LEGAL => [$super, self::ROLE_COMPLIANCE],
            self::FEATURE_ENABLE => [$super],
            self::PASS_VIEW => [$super, self::ROLE_PASS, self::ROLE_COMPLIANCE],
            self::PASS_MANAGE => [$super, self::ROLE_PASS],
            self::PASS_APPROVE => [$super, self::ROLE_PASS, self::ROLE_COMPLIANCE],
            self::SCH_VIEW => [$super, self::ROLE_SCHOLARSHIP, self::ROLE_COMPLIANCE],
            self::SCH_MANAGE => [$super, self::ROLE_SCHOLARSHIP],
            self::SCH_PII => [$super, self::ROLE_SCHOLARSHIP],
            // 심사는 심사위원만 한다. 최고 관리자도 점수를 넣지 못한다.
            self::SCH_REVIEW => [self::ROLE_REVIEWER],
            self::SCH_APPROVE => [$super, self::ROLE_SCHOLARSHIP],
            self::SCH_DISBURSE => [$super, self::ROLE_FINANCE],
            self::BENEFIT_AUDIT => [$super, self::ROLE_COMPLIANCE],
            // v9.0
            self::CITY_VIEW => [$super, self::ROLE_OPERATOR, self::ROLE_BALANCE, self::ROLE_SUPPORT, self::ROLE_VIEWER, self::ROLE_CONTENT],
            self::CITY_POLICY => [$super, self::ROLE_BALANCE],
            self::GROWTH_MANAGE => [$super, self::ROLE_OPERATOR],
            self::MODELING_MANAGE => [$super, self::ROLE_OPERATOR, self::ROLE_CONTENT],
            self::MODELING_REVIEW => [$super, self::ROLE_OPERATOR],
            self::CONTENT_RELEASE => [$super],
            self::CONTENT_WITHDRAW => [$super, self::ROLE_OPERATOR],
            self::COOP_MANAGE => [$super, self::ROLE_OPERATOR, self::ROLE_SUPPORT],
            self::TREASURY_EXECUTE => [$super, self::ROLE_OPERATOR],
            self::BUDGET_MANAGE => [$super, self::ROLE_OPERATOR, self::ROLE_SUPPORT],
        ];
    }

    public static function can(array $admin, $permission)
    {
        $grants = self::grants();
        return isset($grants[$permission]) && in_array($admin['role'], $grants[$permission], true);
    }

    /** 비밀번호 규칙에 맞지 않는 이유. 맞으면 null. */
    public static function passwordProblem($password)
    {
        if (!is_string($password) || strlen($password) < self::MIN_PASSWORD) {
            return '비밀번호는 ' . self::MIN_PASSWORD . '자 이상이어야 합니다.';
        }
        if (strlen($password) > 200) {
            return '비밀번호가 너무 깁니다.';
        }
        $kinds = (int)(preg_match('/[a-z]/', $password) === 1) + (int)(preg_match('/[A-Z]/', $password) === 1)
            + (int)(preg_match('/\d/', $password) === 1) + (int)(preg_match('/[^A-Za-z0-9]/', $password) === 1);
        if ($kinds < 3) {
            return '영문 대문자, 소문자, 숫자, 기호 가운데 세 가지 이상을 섞어야 합니다.';
        }
        return null;
    }

    /**
     * 관리자 계정을 만든다 (명령행 도구가 부른다).
     * @return array id, login, role, totpSecret, totpUri — 일회용 코드의 비밀값은 이때 한 번만 돌려준다.
     */
    public function create($login, $password, $role)
    {
        if (!is_string($login) || !preg_match('/^[a-z0-9_.-]{3,40}$/', $login)) {
            throw new \InvalidArgumentException('아이디는 영문 소문자·숫자·._- 로 3~40자여야 합니다.');
        }
        if (!isset(self::roles()[$role])) {
            throw new \InvalidArgumentException('없는 역할입니다: ' . $role);
        }
        $problem = self::passwordProblem($password);
        if ($problem !== null) {
            throw new \InvalidArgumentException($problem);
        }
        if ($this->app->db->val('SELECT COUNT(*) FROM admins WHERE login = ?', [$login]) > 0) {
            throw new \InvalidArgumentException('이미 있는 아이디입니다.');
        }
        $secret = Totp::newSecret();
        $id = $this->app->db->insert(
            'INSERT INTO admins (login, password_hash, role, totp_secret, is_active, created_at) VALUES (?, ?, ?, ?, 1, ?)',
            [$login, password_hash($password, PASSWORD_DEFAULT), $role, $secret, Clock::sql()]
        );
        $this->audit($id, 'admin.create', 'admin', $login, 'role=' . $role, 'cli');
        return ['id' => $id, 'login' => $login, 'role' => $role, 'totpSecret' => $secret, 'totpUri' => Totp::uri($secret, $login)];
    }

    /**
     * 로그인. 성공하면 [쿠키에 넣을 토큰, 관리자]. 실패하면 null (이유는 알려 주지 않는다).
     */
    public function login($login, $password, $code, $ip)
    {
        $db = $this->app->db;
        $login = is_string($login) ? strtolower(trim($login)) : '';
        $admin = $login !== '' && strlen($login) <= 40 ? $db->one('SELECT * FROM admins WHERE login = ?', [$login]) : null;
        if ($admin === null) {
            // 없는 계정이어도 같은 만큼 시간을 쓴다 (계정이 있는지 응답 시간으로 알아내지 못하게).
            password_verify((string)$password, '$2y$10$usesomesillystringfore7hnbRJHxXVLeakoG8K30oukPsA.ztMG');
            $this->audit(null, 'login.failed', 'admin', mb_substr($login, 0, 40), 'unknown', $ip);
            return null;
        }
        $now = Clock::now();
        if ($admin['locked_until'] !== null && Clock::parse($admin['locked_until']) > $now) {
            $this->audit((int)$admin['id'], 'login.locked', 'admin', $admin['login'], null, $ip);
            return null;
        }
        $passwordOk = password_verify((string)$password, $admin['password_hash']);
        $step = $admin['totp_secret'] !== null && $admin['totp_secret'] !== ''
            ? Totp::matchStep($admin['totp_secret'], $code, $now) : 0;
        // 이미 쓴 코드는 다시 받지 않는다.
        $codeOk = $step > 0 && $step > (int)$admin['totp_last_step'];
        if (!$passwordOk || !$codeOk || !(int)$admin['is_active']) {
            $failures = (int)$admin['failed_attempts'] + 1;
            $lock = $failures >= self::MAX_FAILURES ? Clock::sql($now + self::LOCK_MINUTES * 60) : null;
            $db->exec('UPDATE admins SET failed_attempts = ?, locked_until = ? WHERE id = ?',
                [$lock !== null ? 0 : $failures, $lock, $admin['id']]);
            $this->audit((int)$admin['id'], 'login.failed', 'admin', $admin['login'], $lock !== null ? 'locked' : null, $ip);
            return null;
        }

        $token = Ids::secret();
        $db->exec('UPDATE admins SET failed_attempts = 0, locked_until = NULL, last_login_at = ?, totp_last_step = ? WHERE id = ?',
            [Clock::sql(), $step, $admin['id']]);
        $db->exec(
            'INSERT INTO admin_sessions (admin_id, token_hash, csrf_token, ip, created_at, last_seen_at, expires_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$admin['id'], Ids::sha256($token), Ids::secret(), mb_substr((string)$ip, 0, 45), Clock::sql(), Clock::sql(),
                Clock::sql($now + self::MAX_HOURS * 3600)]
        );
        $this->audit((int)$admin['id'], 'login', 'admin', $admin['login'], null, $ip);
        return [$token, $this->publicAdmin($admin)];
    }

    private function publicAdmin(array $row)
    {
        return ['id' => (int)$row['id'], 'login' => $row['login'], 'role' => $row['role']];
    }

    /**
     * 쿠키의 토큰으로 접속을 확인한다.
     * @return array|null admin, csrf, sessionId
     */
    public function session($token)
    {
        if (!is_string($token) || !preg_match('/^[0-9a-f]{64}$/', $token)) {
            return null;
        }
        $db = $this->app->db;
        $now = Clock::now();
        $row = $db->one(
            'SELECT s.id AS session_id, s.csrf_token, a.* FROM admin_sessions s JOIN admins a ON a.id = s.admin_id'
            . ' WHERE s.token_hash = ? AND s.revoked_at IS NULL AND s.expires_at > ? AND s.last_seen_at > ? AND a.is_active = 1',
            [Ids::sha256($token), Clock::sql($now), Clock::sql($now - self::IDLE_MINUTES * 60)]
        );
        if ($row === null) {
            return null;
        }
        $db->exec('UPDATE admin_sessions SET last_seen_at = ? WHERE id = ?', [Clock::sql($now), $row['session_id']]);
        return ['admin' => $this->publicAdmin($row), 'csrf' => $row['csrf_token'], 'sessionId' => (int)$row['session_id']];
    }

    public function logout($token)
    {
        if (is_string($token) && $token !== '') {
            $this->app->db->exec('UPDATE admin_sessions SET revoked_at = ? WHERE token_hash = ? AND revoked_at IS NULL',
                [Clock::sql(), Ids::sha256($token)]);
        }
    }

    /** 감사 기록. 실패해도 조용히 넘어가지 않는다: 기록하지 못한 변경은 하지 않는다 (같은 거래 안에서 부른다). */
    public function audit($adminId, $action, $targetType = '', $targetKey = '', $detail = null, $ip = '')
    {
        $this->app->db->exec(
            'INSERT INTO admin_audit_logs (admin_id, action, target_type, target_key, detail, ip, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
            [$adminId, mb_substr($action, 0, 64), mb_substr((string)$targetType, 0, 40), mb_substr((string)$targetKey, 0, 64),
                $detail !== null ? mb_substr((string)$detail, 0, 4000) : null, mb_substr((string)$ip, 0, 45), Clock::sql()]
        );
    }

    /** 지난 접속 기록을 정리한다. 예약 작업이 부른다. */
    public function purge()
    {
        return $this->app->db->exec('DELETE FROM admin_sessions WHERE expires_at < ?', [Clock::sql(Clock::now() - 7 * 86400)]);
    }
}
