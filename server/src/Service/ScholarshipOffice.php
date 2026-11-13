<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Form;
use CG\Infra\Ids;

/**
 * GUARDIANS SCHOLARSHIP: 운영 쪽 (docs/SCHOLARSHIP_SYSTEM.md, docs/SCHOLARSHIP_REVIEW_POLICY.md).
 *
 * 접수 → 자격 확인 → 심사위원 배정 → 평가 입력 → 결과 집계 → 선정 제안 → 최종 승인 → 결과 통지 → 이의신청 → 지급 상태.
 *
 * - 업무마다 권한이 다르다. 심사위원은 자기에게 배정된 지원서만, 이름·연락처·학교 이름 없이 본다.
 *   장학 담당자가 개인정보를 열 때에는 사유를 적어야 하고, 열 때마다 기록이 남는다.
 * - 공고의 승인, 선정의 최종 승인, 지급 기록의 승인은 작성·제안·기록한 사람이 아닌 다른 관리자가 한다.
 * - 이 서비스는 전투·지갑·랭킹 테이블을 읽지 않는다. 심사 점수는 심사위원이 넣은 것뿐이다.
 * - 돈을 보내지 않는다. 지급은 "예정 → 승인 → 완료" 라는 상태 기록일 뿐이고, 계좌 정보를 받는 곳이 없다.
 */
final class ScholarshipOffice
{
    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    public static function programStatuses()
    {
        return ['draft' => '초안', 'pending' => '승인 대기', 'published' => '공고 중', 'closed' => '접수 마감 (심사)', 'announced' => '결과 발표',
            'archived' => '보관'];
    }

    public static function decisions()
    {
        return ['selected' => '선정', 'waitlisted' => '예비 선정', 'not_selected' => '선정되지 않음'];
    }

    public static function disbursementStatuses()
    {
        return ['planned' => '예정', 'approved' => '승인됨', 'completed' => '완료', 'cancelled' => '취소'];
    }

    private static function need(array $admin, $permission)
    {
        if (!Auth::can($admin, $permission)) {
            throw new ApiError(ApiError::FORBIDDEN, '이 계정의 역할로는 할 수 없는 일입니다.');
        }
    }

    private function log(array $admin, $action, $programId, $applicationId, $detail = null, $ip = '')
    {
        $this->app->scholarships()->audit('admin', $admin['id'], $action, $programId, $applicationId, $detail, $ip);
    }

    // ── 프로그램 ────────────────────────────────────────────

    public function programs(array $admin)
    {
        self::need($admin, Auth::SCH_VIEW);
        return $this->app->db->all(
            "SELECT p.*, (SELECT COUNT(*) FROM scholarship_applications a WHERE a.program_id = p.id AND a.status <> 'withdrawn') AS application_count"
            . ' FROM scholarship_programs p ORDER BY p.id DESC LIMIT 300');
    }

    /** 권한 확인 없이 읽는다. 부르는 쪽이 권한을 확인한다. */
    private function programRow($id, $lock = false)
    {
        $row = $this->app->db->one('SELECT * FROM scholarship_programs WHERE id = ?' . ($lock ? ' FOR UPDATE' : ''), [(int)$id]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 프로그램입니다.');
        }
        return $row;
    }

    public function program(array $admin, $id)
    {
        self::need($admin, Auth::SCH_VIEW);
        return $this->programRow($id);
    }

    /**
     * 프로그램을 만들거나 고친다. 초안과 승인 대기 상태에서만 고칠 수 있다.
     * @return array [id, 문제 목록]
     */
    public function saveProgram(array $admin, $id, array $input, $ip = '')
    {
        self::need($admin, Auth::SCH_MANAGE);
        $form = new Form($input);
        $values = [
            'category' => $form->choice('category', '장학 종류', Scholarships::categories()),
            'title' => $form->text('title', '이름', 2, 120),
            'summary' => $form->text('summary', '한 줄 소개', 2, 300),
            'description' => $form->text('description', '소개', 0, 10000, true),
            'support_type' => $form->choice('support_type', '지원 방식', Scholarships::supportTypes()),
            'support_detail' => $form->text('support_detail', '지원 내용', 2, 300),
            'capacity' => $form->int('capacity', '선발 인원', 1, 10000),
            'result_on' => $form->date('result_on', '결과 발표 예정일'),
            // 보호자 동의 나이는 14 아래로 내릴 수 없다.
            'guardian_consent_age' => $form->int('guardian_consent_age', '보호자 동의가 필요한 나이 (미만)', Scholarships::MIN_CONSENT_AGE, 19),
            'min_reviews' => $form->int('min_reviews', '지원서마다 필요한 심사 수', 1, 10),
            'appeal_days' => $form->int('appeal_days', '이의신청 기간(일)', 3, 30),
            'retention_days_unselected' => $form->int('retention_days_unselected', '선정되지 않은 신청의 보관 기간(일)', 30, 365),
            'retention_days_selected' => $form->int('retention_days_selected', '선정된 신청의 보관 기간(일)', 30, 1825),
        ];
        $starts = $form->koreaTime('apply_starts_at', '접수 시작');
        $ends = $form->koreaTime('apply_ends_at', '접수 마감');
        if ($starts !== null && $ends !== null && $ends <= $starts) {
            $form->fail('접수 마감이 시작보다 뒤여야 합니다.');
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $form, $values, $starts, $ends, $ip) {
            $existing = $id !== null ? $this->programRow($id, true) : null;
            if ($existing !== null && !in_array($existing['status'], ['draft', 'pending'], true)) {
                $form->fail('공고가 나간 프로그램은 고칠 수 없습니다.');
            }
            if (!$form->ok()) {
                return [$id, $form->problems];
            }
            $columns = array_keys($values);
            $params = array_values($values);
            $columns[] = 'apply_starts_at';
            $params[] = Clock::sql($starts);
            $columns[] = 'apply_ends_at';
            $params[] = Clock::sql($ends);
            // 고친 사람이 created_by 가 된다: 내용을 마지막으로 정한 사람은 그 공고를 승인할 수 없다.
            $columns[] = 'created_by';
            $params[] = $admin['id'];
            $columns[] = 'updated_at';
            $params[] = Clock::sql();
            if ($existing === null) {
                $columns = array_merge($columns, ['public_id', 'status', 'created_at']);
                $params = array_merge($params, [Ids::publicId('s_'), 'draft', Clock::sql()]);
                $id = $db->insert('INSERT INTO scholarship_programs (' . implode(', ', $columns) . ') VALUES (' . implode(', ', array_fill(0, count($columns), '?')) . ')', $params);
                $this->log($admin, 'program.create', $id, null, $values['title'], $ip);
            } else {
                $params[] = $existing['id'];
                $db->exec('UPDATE scholarship_programs SET ' . implode(' = ?, ', $columns) . ' = ? WHERE id = ?', $params);
                $this->log($admin, 'program.update', $existing['id'], null, $values['title'], $ip);
            }
            return [(int)$id, []];
        });
    }

    private function editableProgram(array $admin, $programId, Form $form)
    {
        self::need($admin, Auth::SCH_MANAGE);
        $program = $this->programRow($programId, true);
        if (!in_array($program['status'], ['draft', 'pending'], true)) {
            $form->fail('공고가 나간 프로그램의 자격과 평가 기준은 바꿀 수 없습니다.');
        }
        return $program;
    }

    /**
     * 지원 자격을 더한다. 종류는 Scholarships::ruleTypes() 의 넷뿐이다:
     * 게임 데이터를 조건으로 삼는 종류를 보내면 거절한다.
     * @return string[] 문제 목록
     */
    public function addRule(array $admin, $programId, array $input, $ip = '')
    {
        $form = new Form($input);
        $type = $form->raw('rule_type');
        if (!isset(Scholarships::ruleTypes()[$type])) {
            $form->fail('자격의 종류: 학교급, 태어난 해, 본인 확인 문구, 제출 자료 가운데에서만 고를 수 있습니다. 게임 기록은 자격 조건이 될 수 없습니다.');
        }
        $description = $form->text('description', '설명', 2, 300);
        $params = null;
        if ($type === 'school_level') {
            $levels = array_values(array_filter(array_map('trim', explode(',', $form->raw('levels'))), 'strlen'));
            if (!$levels || array_diff($levels, array_keys(Scholarships::schoolLevels()))) {
                $form->fail('학교급: ' . implode(', ', array_keys(Scholarships::schoolLevels())) . ' 가운데에서 쉼표로 이어 적어 주세요.');
            }
            $params = ['levels' => $levels];
        } elseif ($type === 'birth_year') {
            $params = ['min' => $form->int('min', '태어난 해 (부터)', 1980, 2100), 'max' => $form->int('max', '태어난 해 (까지)', 1980, 2100)];
            if ($params['max'] < $params['min']) {
                $form->fail('태어난 해의 범위가 거꾸로입니다.');
            }
        } elseif ($type === 'document') {
            $params = ['kind' => $form->choice('kind', '자료의 종류', Scholarships::documentKinds())];
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $programId, $form, $type, $description, $params, $ip) {
            $program = $this->editableProgram($admin, $programId, $form);
            if (!$form->ok()) {
                return $form->problems;
            }
            $order = (int)$db->val('SELECT COALESCE(MAX(sort_order), 0) + 1 FROM scholarship_eligibility_rules WHERE program_id = ?', [$program['id']]);
            $db->exec('INSERT INTO scholarship_eligibility_rules (program_id, rule_type, params, description, sort_order, created_at) VALUES (?, ?, ?, ?, ?, ?)',
                [$program['id'], $type, $params !== null ? json_encode($params) : null, $description, $order, Clock::sql()]);
            $db->exec('UPDATE scholarship_programs SET created_by = ?, updated_at = ? WHERE id = ?', [$admin['id'], Clock::sql(), $program['id']]);
            $this->log($admin, 'program.rule.add', $program['id'], null, $type . ': ' . $description, $ip);
            return [];
        });
    }

    /** @return string[] 문제 목록 */
    public function addCriterion(array $admin, $programId, array $input, $ip = '')
    {
        $form = new Form($input);
        $name = $form->text('name', '평가 기준', 2, 80);
        $description = $form->text('description', '설명', 0, 300);
        $max = $form->int('max_score', '만점', 1, 100);
        $weight = $form->int('weight', '가중치', 1, 100);
        // 게임 성적을 평가 기준으로 넣지 못하게 이름에서 걸러 낸다. 완전한 방법은 아니다: 승인하는 사람이 다시 본다.
        if (preg_match('/(GC|게임머니|랭킹|전투|레벨|결제|플레이\s*시간|아이템)/iu', $name . ' ' . $description)) {
            $form->fail('평가 기준에 게임 성적·게임머니·랭킹·결제를 넣을 수 없습니다.');
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $programId, $form, $name, $description, $max, $weight, $ip) {
            $program = $this->editableProgram($admin, $programId, $form);
            if (!$form->ok()) {
                return $form->problems;
            }
            $order = (int)$db->val('SELECT COALESCE(MAX(sort_order), 0) + 1 FROM scholarship_criteria WHERE program_id = ?', [$program['id']]);
            $db->exec('INSERT INTO scholarship_criteria (program_id, name, description, max_score, weight, sort_order, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                [$program['id'], $name, $description, $max, $weight, $order, Clock::sql()]);
            $db->exec('UPDATE scholarship_programs SET created_by = ?, updated_at = ? WHERE id = ?', [$admin['id'], Clock::sql(), $program['id']]);
            $this->log($admin, 'program.criterion.add', $program['id'], null, $name, $ip);
            return [];
        });
    }

    /** 초안의 자격($kind = rule)이나 평가 기준(criterion)을 뺀다. @return string[] 문제 목록 */
    public function removeItem(array $admin, $programId, $kind, $itemId, $ip = '')
    {
        $table = $kind === 'rule' ? 'scholarship_eligibility_rules' : ($kind === 'criterion' ? 'scholarship_criteria' : null);
        if ($table === null) {
            throw new ApiError(ApiError::INVALID_REQUEST, '할 수 없는 일입니다.');
        }
        $form = new Form([]);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $programId, $form, $table, $kind, $itemId, $ip) {
            $program = $this->editableProgram($admin, $programId, $form);
            if (!$form->ok()) {
                return $form->problems;
            }
            if ($db->exec("DELETE FROM $table WHERE id = ? AND program_id = ?", [(int)$itemId, $program['id']]) !== 1) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 항목입니다.');
            }
            $db->exec('UPDATE scholarship_programs SET created_by = ?, updated_at = ? WHERE id = ?', [$admin['id'], Clock::sql(), $program['id']]);
            $this->log($admin, 'program.' . $kind . '.remove', $program['id'], null, '#' . (int)$itemId, $ip);
            return [];
        });
    }

    /**
     * 프로그램의 상태를 바꾼다.
     *   submit  초안 → 승인 대기     return 승인 대기 → 초안     publish 승인 대기 → 공고 (다른 관리자가 승인)
     *   close   공고 → 접수 마감     archive → 보관 (남은 신청의 개인정보 보관 기한을 정한다)
     * @return string[] 문제 목록
     */
    public function programTransition(array $admin, $id, $action, $reason, $ip = '')
    {
        $rules = [
            'submit' => [['draft'], 'pending', Auth::SCH_MANAGE],
            'return' => [['pending'], 'draft', Auth::SCH_MANAGE],
            'publish' => [['pending'], 'published', Auth::SCH_APPROVE],
            'close' => [['published'], 'closed', Auth::SCH_MANAGE],
            'archive' => [['draft', 'pending', 'closed', 'announced'], 'archived', Auth::SCH_MANAGE],
        ];
        if (!isset($rules[$action])) {
            throw new ApiError(ApiError::INVALID_REQUEST, '할 수 없는 일입니다.');
        }
        list($from, $to, $permission) = $rules[$action];
        self::need($admin, $permission);
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $action, $from, $to, $form, $reason, $ip) {
            $program = $this->programRow($id, true);
            if (!in_array($program['status'], $from, true)) {
                $form->fail('지금 상태(' . self::programStatuses()[$program['status']] . ')에서는 할 수 없습니다.');
            }
            if ($action === 'publish') {
                if (!$this->app->features()->isEnabled(Features::SCH_APPLICATIONS)) {
                    $form->fail('장학 신청 접수 기능이 꺼져 있습니다. 법률 검토와 운영 승인이 끝난 뒤에 공고할 수 있습니다.');
                }
                if ($program['created_by'] === null || (int)$program['created_by'] === (int)$admin['id']) {
                    $form->fail('작성하거나 마지막으로 고친 사람은 공고를 승인할 수 없습니다. 다른 관리자가 승인해야 합니다.');
                }
                if (!$this->app->scholarships()->criteriaOf($program['id'])) {
                    $form->fail('평가 기준이 하나도 없습니다. 심사 기준을 먼저 정해야 합니다.');
                }
                if (Clock::parse($program['apply_ends_at']) <= Clock::now()) {
                    $form->fail('접수 마감이 이미 지났습니다.');
                }
                $reviewers = (int)$db->val('SELECT COUNT(*) FROM scholarship_reviewers WHERE program_id = ? AND is_active = 1', [$program['id']]);
                if ($reviewers < (int)$program['min_reviews']) {
                    $form->fail('심사위원이 ' . (int)$program['min_reviews'] . '명보다 적습니다. 심사위원을 먼저 배정해 주세요.');
                }
            }
            if ($action === 'close' && Clock::parse($program['apply_ends_at']) > Clock::now()) {
                $form->fail('접수 마감 시각이 아직 지나지 않았습니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $publish = $action === 'publish';
            $db->exec('UPDATE scholarship_programs SET status = ?, approved_by = ' . ($publish ? '?' : 'approved_by') . ', approved_at = '
                . ($publish ? '?' : 'approved_at') . ', updated_at = ? WHERE id = ?',
                array_merge([$to], $publish ? [$admin['id'], Clock::sql()] : [], [Clock::sql(), $program['id']]));
            if ($action === 'archive') {
                // 결과 없이 남은 신청도 언젠가는 지워져야 한다.
                $db->exec("UPDATE scholarship_applications SET purge_after = ? WHERE program_id = ? AND purge_after IS NULL AND purged_at IS NULL",
                    [Clock::koreaDate(Clock::now() + (int)$program['retention_days_unselected'] * 86400), $program['id']]);
            }
            $this->log($admin, 'program.' . $action, $program['id'], null, $program['status'] . ' → ' . $to . ': ' . $reason, $ip);
            return [];
        });
    }

    // ── 심사위원 ────────────────────────────────────────────

    /** 심사위원으로 배정할 수 있는 관리자 (역할이 reviewer 인 계정) 와 지금 배정된 상태. */
    public function reviewers(array $admin, $programId)
    {
        self::need($admin, Auth::SCH_VIEW);
        return $this->app->db->all(
            'SELECT a.id AS admin_id, a.login, a.is_active AS account_active, r.id AS reviewer_id, r.is_active FROM admins a'
            . ' LEFT JOIN scholarship_reviewers r ON r.admin_id = a.id AND r.program_id = ? WHERE a.role = ? ORDER BY a.login',
            [(int)$programId, Auth::ROLE_REVIEWER]);
    }

    /** 심사위원을 배정하거나 뺀다. @return string[] 문제 목록 */
    public function setReviewer(array $admin, $programId, $reviewerAdminId, $active, $ip = '')
    {
        self::need($admin, Auth::SCH_MANAGE);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $programId, $reviewerAdminId, $active, $ip) {
            $program = $this->programRow($programId, true);
            $target = $db->one('SELECT id, login, role, is_active FROM admins WHERE id = ?', [(int)$reviewerAdminId]);
            if ($target === null || $target['role'] !== Auth::ROLE_REVIEWER || !(int)$target['is_active']) {
                return ['심사위원 역할의 계정만 배정할 수 있습니다.'];
            }
            if (in_array($program['status'], ['announced', 'archived'], true)) {
                return ['끝난 프로그램입니다.'];
            }
            $db->exec('INSERT IGNORE INTO scholarship_reviewers (program_id, admin_id, is_active, assigned_by, created_at) VALUES (?, ?, 0, ?, ?)',
                [$program['id'], $target['id'], $admin['id'], Clock::sql()]);
            $db->exec('UPDATE scholarship_reviewers SET is_active = ?, assigned_by = ? WHERE program_id = ? AND admin_id = ?',
                [$active ? 1 : 0, $admin['id'], $program['id'], $target['id']]);
            $this->log($admin, $active ? 'reviewer.assign' : 'reviewer.unassign', $program['id'], null, $target['login'], $ip);
            return [];
        });
    }

    // ── 접수, 자격 확인 ─────────────────────────────────────

    /** 신청 목록. 개인정보는 없다: 접수 번호, 학교급, 상태뿐이다. */
    public function applications(array $admin, $programId)
    {
        self::need($admin, Auth::SCH_VIEW);
        return $this->app->db->all(
            'SELECT a.id, a.public_id, a.status, a.eligibility_status, a.consent_status, a.school_level, a.is_minor, a.submitted_at, a.result, a.disbursement_status,'
            . ' a.purged_at, w.decision, w.status AS award_status, w.score_centi, w.review_count,'
            . " (SELECT COUNT(*) FROM scholarship_reviews r WHERE r.application_id = a.id AND r.status = 'submitted') AS reviews_done,"
            . " (SELECT COUNT(*) FROM scholarship_reviews r WHERE r.application_id = a.id AND r.status = 'assigned') AS reviews_waiting"
            . ' FROM scholarship_applications a LEFT JOIN scholarship_awards w ON w.application_id = a.id WHERE a.program_id = ? ORDER BY a.id', [(int)$programId]);
    }

    private function applicationRow($id, $lock = false)
    {
        $row = $this->app->db->one('SELECT * FROM scholarship_applications WHERE id = ?' . ($lock ? ' FOR UPDATE' : ''), [(int)$id]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 신청입니다.');
        }
        return $row;
    }

    private function openForm(array $application)
    {
        if ($application['form_enc'] === null) {
            return null;
        }
        return (array)json_decode($this->app->crypto()->open($application['form_enc'], 'application.form'), true);
    }

    private function openDocuments($applicationId)
    {
        $crypto = $this->app->crypto();
        return array_map(function ($row) use ($crypto) {
            return ['kind' => $row['kind'], 'kindName' => Scholarships::documentKinds()[$row['kind']], 'title' => $row['title'],
                'content' => $crypto->open($row['content_enc'], 'document.content'), 'url' => $row['url']];
        }, $this->app->db->all('SELECT * FROM scholarship_documents WHERE application_id = ? ORDER BY id', [$applicationId]));
    }

    /**
     * 신청 하나 (장학 담당자). 신청서와 제출 자료가 보인다. 이름·연락처는 여기에 없다: pii() 로 따로 연다.
     * 열 때마다 기록이 남는다.
     */
    public function applicationDetail(array $admin, $id, $ip = '')
    {
        self::need($admin, Auth::SCH_MANAGE);
        $application = $this->applicationRow($id);
        $db = $this->app->db;
        $this->log($admin, 'application.view', $application['program_id'], $application['id'], null, $ip);
        return [
            'application' => $application,
            'program' => $this->programRow($application['program_id']),
            'form' => $this->openForm($application),
            'documents' => $this->openDocuments($application['id']),
            'consents' => $db->all('SELECT consent_type, status, version, guardian_relation, confirmed_at, verified_at, verify_method, verify_note'
                . ' FROM scholarship_consents WHERE application_id = ? ORDER BY id', [$application['id']]),
            'reviews' => $db->all('SELECT r.id, r.status, r.total_centi, r.submitted_at, a.login FROM scholarship_reviews r JOIN admins a ON a.id = r.admin_id'
                . ' WHERE r.application_id = ? ORDER BY r.id', [$application['id']]),
            'award' => $db->one('SELECT * FROM scholarship_awards WHERE application_id = ?', [$application['id']]),
            'appeal' => $db->one('SELECT * FROM scholarship_appeals WHERE application_id = ?', [$application['id']]),
        ];
    }

    /**
     * 신청자의 개인정보를 연다. 사유를 적어야 하고, 누가 언제 왜 열었는지 기록된다.
     * @return array [개인정보 또는 null, 문제 목록]
     */
    public function pii(array $admin, $id, $reason, $ip = '')
    {
        self::need($admin, Auth::SCH_PII);
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        if (!$form->ok()) {
            return [null, $form->problems];
        }
        $application = $this->applicationRow($id);
        $db = $this->app->db;
        // 기록을 먼저 남긴다. 기록하지 못하면 열지 않는다.
        $this->log($admin, 'pii.view', $application['program_id'], $application['id'], $reason, $ip);
        $profile = $db->one('SELECT * FROM scholarship_applicant_profiles WHERE application_id = ?', [$application['id']]);
        $guardian = $db->one("SELECT * FROM scholarship_consents WHERE application_id = ? AND consent_type = 'guardian'", [$application['id']]);
        $crypto = $this->app->crypto();
        if ($profile === null || $profile['purged_at'] !== null) {
            return [['purged' => true], []];
        }
        return [[
            'purged' => false,
            'name' => $crypto->open($profile['name_enc'], 'profile.name'), 'birthYear' => $profile['birth_year'],
            'schoolName' => $crypto->open($profile['school_name_enc'], 'profile.school'),
            'email' => $crypto->open($profile['email_enc'], 'profile.email'), 'phone' => $crypto->open($profile['phone_enc'], 'profile.phone'),
            'guardianName' => $guardian !== null ? $crypto->open($guardian['guardian_name_enc'], 'guardian.name') : null,
            'guardianRelation' => $guardian !== null ? $guardian['guardian_relation'] : null,
            'guardianContact' => $guardian !== null ? $crypto->open($guardian['guardian_contact_enc'], 'guardian.contact') : null,
        ], []];
    }

    /**
     * 보호자 동의를 확인했다고 기록한다 (보호자에게 연락하거나 서면을 받아서).
     * 보호자가 주소에서 동의한 뒤에만 할 수 있다.
     * @return string[] 문제 목록
     */
    public function verifyConsent(array $admin, $id, array $input, $ip = '')
    {
        self::need($admin, Auth::SCH_MANAGE);
        $form = new Form($input);
        $method = $form->choice('verify_method', '확인한 방법', ['call' => '보호자와 통화', 'document' => '서면 동의서', 'in_person' => '대면 확인']);
        $note = $form->text('verify_note', '확인 내용', 2, 300);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $form, $method, $note, $ip) {
            $application = $this->applicationRow($id, true);
            if ($application['consent_status'] !== 'guardian_confirmed') {
                $form->fail($application['consent_status'] === 'pending' ? '보호자가 아직 동의하지 않았습니다.' : '확인할 보호자 동의가 없습니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $db->exec("UPDATE scholarship_consents SET status = 'verified', verified_by = ?, verified_at = ?, verify_method = ?, verify_note = ?, updated_at = ?"
                . " WHERE application_id = ? AND consent_type = 'guardian'", [$admin['id'], Clock::sql(), $method, $note, Clock::sql(), $application['id']]);
            $db->exec("UPDATE scholarship_applications SET consent_status = 'verified', updated_at = ? WHERE id = ?", [Clock::sql(), $application['id']]);
            $this->log($admin, 'consent.verify', $application['program_id'], $application['id'], $method, $ip);
            return [];
        });
    }

    /**
     * 자격을 확인한다. 해당하면 심사로 넘기고 심사위원에게 배정한다. 해당하지 않으면 사유를 남긴다.
     * 미성년자는 보호자 동의가 확인된 뒤에만 심사로 넘어간다.
     * @return string[] 문제 목록
     */
    public function decideEligibility(array $admin, $id, $eligible, $note, $ip = '')
    {
        self::need($admin, Auth::SCH_MANAGE);
        $form = new Form(['note' => (string)$note]);
        $note = $form->text('note', $eligible ? '확인 내용' : '해당하지 않는 사유', 2, 300);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $eligible, $form, $note, $ip) {
            $application = $this->applicationRow($id, true);
            $program = $this->programRow($application['program_id']);
            if ($application['status'] !== 'submitted') {
                $form->fail('접수 상태의 신청만 자격을 확인할 수 있습니다.');
            }
            if ($eligible && !in_array($application['consent_status'], ['not_required', 'verified'], true)) {
                $form->fail('보호자 동의가 확인되지 않았습니다. 보호자 동의를 먼저 확인해 주세요.');
            }
            $reviewers = $db->all('SELECT id, admin_id FROM scholarship_reviewers WHERE program_id = ? AND is_active = 1 ORDER BY id', [$program['id']]);
            if ($eligible && count($reviewers) < (int)$program['min_reviews']) {
                $form->fail('심사위원이 ' . (int)$program['min_reviews'] . '명보다 적습니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            if ($eligible) {
                $db->exec("UPDATE scholarship_applications SET status = 'in_review', eligibility_status = 'confirmed', eligibility_note = ?, eligibility_by = ?,"
                    . ' eligibility_at = ?, updated_at = ? WHERE id = ?', [$note, $admin['id'], Clock::sql(), Clock::sql(), $application['id']]);
                foreach ($reviewers as $reviewer) {
                    $db->exec("INSERT IGNORE INTO scholarship_reviews (application_id, reviewer_id, admin_id, status, assigned_at) VALUES (?, ?, ?, 'assigned', ?)",
                        [$application['id'], $reviewer['id'], $reviewer['admin_id'], Clock::sql()]);
                }
                $this->log($admin, 'eligibility.confirm', $program['id'], $application['id'], count($reviewers) . '명에게 배정', $ip);
            } else {
                $db->exec("UPDATE scholarship_applications SET status = 'ineligible', eligibility_status = 'rejected', eligibility_note = ?, eligibility_by = ?,"
                    . ' eligibility_at = ?, purge_after = ?, updated_at = ? WHERE id = ?',
                    [$note, $admin['id'], Clock::sql(), Clock::koreaDate(Clock::now() + (int)$program['retention_days_unselected'] * 86400),
                        Clock::sql(), $application['id']]);
                $this->log($admin, 'eligibility.reject', $program['id'], $application['id'], $note, $ip);
            }
            return [];
        });
    }

    // ── 심사 ────────────────────────────────────────────────

    /** 이 심사위원에게 배정된 심사. 다른 사람의 것은 보이지 않는다. */
    public function reviewQueue(array $admin)
    {
        self::need($admin, Auth::SCH_REVIEW);
        return $this->app->db->all(
            'SELECT r.id, r.status, r.total_centi, r.assigned_at, r.submitted_at, a.public_id, a.status AS application_status, p.title, p.id AS program_id'
            . ' FROM scholarship_reviews r JOIN scholarship_applications a ON a.id = r.application_id JOIN scholarship_programs p ON p.id = a.program_id'
            . " WHERE r.admin_id = ? AND a.status <> 'withdrawn' ORDER BY r.status = 'assigned' DESC, r.id DESC LIMIT 300", [$admin['id']]);
    }

    private function ownReview(array $admin, $reviewId, $lock = false)
    {
        self::need($admin, Auth::SCH_REVIEW);
        // 남에게 배정된 심사는 없는 것으로 답한다.
        $review = $this->app->db->one('SELECT * FROM scholarship_reviews WHERE id = ? AND admin_id = ?' . ($lock ? ' FOR UPDATE' : ''),
            [(int)$reviewId, $admin['id']]);
        if ($review === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 심사입니다.');
        }
        return $review;
    }

    /**
     * 심사위원이 보는 지원서. 이름, 연락처, 학교 이름, 태어난 해, 게임 계정은 들어 있지 않다.
     * 열 때마다 기록이 남는다.
     */
    public function reviewView(array $admin, $reviewId, $ip = '')
    {
        $review = $this->ownReview($admin, $reviewId);
        $application = $this->applicationRow($review['application_id']);
        if ($application['status'] === 'withdrawn' || $application['purged_at'] !== null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 심사입니다.');
        }
        $program = $this->programRow($application['program_id']);
        $this->log($admin, 'review.view', $program['id'], $application['id'], null, $ip);
        return [
            'review' => ['id' => (int)$review['id'], 'status' => $review['status'], 'scores' => $review['scores'] !== null ? (array)json_decode($review['scores'], true) : [],
                'comment' => $review['comment'], 'totalCenti' => $review['total_centi'], 'submittedAt' => $review['submitted_at'],
                'open' => $review['status'] === 'assigned' && $application['status'] === 'in_review'],
            'application' => ['number' => $application['public_id'], 'schoolLevel' => Scholarships::schoolLevels()[$application['school_level']]],
            'program' => ['title' => $program['title'], 'categoryName' => Scholarships::categories()[$program['category']],
                'supportDetail' => $program['support_detail']],
            'criteria' => $this->app->scholarships()->criteriaOf($program['id']),
            'form' => $this->openForm($application),
            'documents' => $this->openDocuments($application['id']),
        ];
    }

    /** 점수의 합을 100점 만점의 1/100 단위로. 기준마다 (점수 ÷ 만점) 에 가중치를 곱해 더한다. */
    public static function totalCenti(array $criteria, array $scores)
    {
        $sum = 0.0;
        $weights = 0;
        foreach ($criteria as $criterion) {
            $sum += $scores[(int)$criterion['id']] / (int)$criterion['max_score'] * (int)$criterion['weight'];
            $weights += (int)$criterion['weight'];
        }
        return $weights > 0 ? (int)round($sum / $weights * 10000) : 0;
    }

    /**
     * 평가를 낸다. 낸 뒤에는 고칠 수 없다.
     * @param array $input score_{기준 id}, comment
     * @return string[] 문제 목록
     */
    public function submitReview(array $admin, $reviewId, array $input, $ip = '')
    {
        $form = new Form($input);
        $comment = $form->text('comment', '심사 의견', 10, 2000, true);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $reviewId, $form, $comment, $ip) {
            $review = $this->ownReview($admin, $reviewId, true);
            $application = $this->applicationRow($review['application_id'], true);
            if ($review['status'] !== 'assigned') {
                $form->fail('이미 낸 평가는 고칠 수 없습니다.');
            }
            if ($application['status'] !== 'in_review') {
                $form->fail('심사 중인 지원서가 아닙니다.');
            }
            $criteria = $this->app->scholarships()->criteriaOf($application['program_id']);
            $scores = [];
            foreach ($criteria as $criterion) {
                $scores[(int)$criterion['id']] = $form->int('score_' . $criterion['id'], $criterion['name'], 0, (int)$criterion['max_score']);
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $total = self::totalCenti($criteria, $scores);
            $db->exec("UPDATE scholarship_reviews SET status = 'submitted', scores = ?, total_centi = ?, comment = ?, submitted_at = ? WHERE id = ?",
                [json_encode($scores), $total, $comment, Clock::sql(), $review['id']]);
            $this->log($admin, 'review.submit', $application['program_id'], $application['id'], 'total=' . $total, $ip);
            return [];
        });
    }

    /** 이해관계가 있어 심사하지 않는다. @return string[] 문제 목록 */
    public function recuse(array $admin, $reviewId, $reason, $ip = '')
    {
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $reviewId, $form, $reason, $ip) {
            $review = $this->ownReview($admin, $reviewId, true);
            if ($review['status'] !== 'assigned') {
                $form->fail('이미 낸 평가는 회피할 수 없습니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $application = $this->applicationRow($review['application_id']);
            $db->exec("UPDATE scholarship_reviews SET status = 'recused', recuse_reason = ? WHERE id = ?", [$reason, $review['id']]);
            $this->log($admin, 'review.recuse', $application['program_id'], $application['id'], $reason, $ip);
            return [];
        });
    }

    // ── 결과 집계, 선정, 최종 승인, 통지 ────────────────────

    /** 지원서마다 심사 점수의 평균. 점수가 높은 순서. 개인정보는 없다. */
    public function results(array $admin, $programId)
    {
        self::need($admin, Auth::SCH_VIEW);
        $rows = $this->app->db->all(
            'SELECT a.id, a.public_id, a.status, a.school_level, w.decision, w.status AS award_status, w.decided_by, w.decision_note,'
            . " (SELECT COUNT(*) FROM scholarship_reviews r WHERE r.application_id = a.id AND r.status = 'submitted') AS review_count,"
            . " (SELECT ROUND(AVG(r.total_centi)) FROM scholarship_reviews r WHERE r.application_id = a.id AND r.status = 'submitted') AS score_centi"
            . ' FROM scholarship_applications a LEFT JOIN scholarship_awards w ON w.application_id = a.id'
            . " WHERE a.program_id = ? AND a.status IN ('in_review', 'decided', 'announced')", [(int)$programId]);
        usort($rows, function ($a, $b) {
            return ((int)$b['score_centi'] <=> (int)$a['score_centi']) ?: ((int)$a['id'] <=> (int)$b['id']);
        });
        return $rows;
    }

    /**
     * 선정을 제안한다. 심사 중인 지원서마다 결정을 고른다.
     * 필요한 수만큼 심사가 끝나지 않은 지원서가 있으면 제안할 수 없다. 선정 인원은 정원을 넘을 수 없다.
     * @param array<int|string,string> $decisions 신청 id => selected | waitlisted | not_selected
     * @return string[] 문제 목록
     */
    public function proposeDecisions(array $admin, $programId, array $decisions, $note, $ip = '')
    {
        self::need($admin, Auth::SCH_MANAGE);
        $form = new Form(['note' => (string)$note]);
        $note = $form->text('note', '선정 사유', 2, 300);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $programId, $decisions, $form, $note, $ip) {
            $program = $this->programRow($programId, true);
            if (!in_array($program['status'], ['closed', 'announced'], true)) {
                $form->fail('접수를 마감한 뒤에 선정할 수 있습니다.');
            }
            $pending = [];
            $selected = (int)$db->val("SELECT COUNT(*) FROM scholarship_awards w JOIN scholarship_applications a ON a.id = w.application_id"
                . " WHERE w.program_id = ? AND w.decision = 'selected' AND w.status = 'approved' AND a.status IN ('decided', 'announced')", [$program['id']]);
            foreach ($this->results($admin, $program['id']) as $row) {
                if ($row['status'] !== 'in_review') {
                    continue;
                }
                $decision = isset($decisions[$row['id']]) ? $decisions[$row['id']] : '';
                if (!isset(self::decisions()[$decision])) {
                    $form->fail('지원서 ' . $row['public_id'] . ': 결정을 골라 주세요.');
                    continue;
                }
                if ((int)$row['review_count'] < (int)$program['min_reviews']) {
                    $form->fail('지원서 ' . $row['public_id'] . ': 심사가 ' . (int)$program['min_reviews'] . '건보다 적습니다. 심사가 끝난 뒤에 결정할 수 있습니다.');
                }
                $selected += $decision === 'selected' ? 1 : 0;
                $pending[] = [$row, $decision];
            }
            if (!$pending && $form->ok()) {
                $form->fail('결정할 지원서가 없습니다.');
            }
            if ($selected > (int)$program['capacity']) {
                $form->fail('선정 인원(' . $selected . ')이 정원(' . (int)$program['capacity'] . ')을 넘습니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            foreach ($pending as $item) {
                list($row, $decision) = $item;
                $now = Clock::sql();
                if ($db->val('SELECT COUNT(*) FROM scholarship_awards WHERE application_id = ?', [$row['id']]) > 0) {
                    $db->exec("UPDATE scholarship_awards SET decision = ?, score_centi = ?, review_count = ?, decision_note = ?, status = 'proposed', decided_by = ?,"
                        . ' decided_at = ?, approved_by = NULL, approved_at = NULL, updated_at = ? WHERE application_id = ?',
                        [$decision, $row['score_centi'], $row['review_count'], $note, $admin['id'], $now, $now, $row['id']]);
                } else {
                    $db->exec('INSERT INTO scholarship_awards (application_id, program_id, decision, score_centi, review_count, decision_note, status, decided_by, decided_at, created_at, updated_at)'
                        . " VALUES (?, ?, ?, ?, ?, ?, 'proposed', ?, ?, ?, ?)",
                        [$row['id'], $program['id'], $decision, $row['score_centi'], $row['review_count'], $note, $admin['id'], $now, $now, $now]);
                }
                $this->log($admin, 'award.propose', $program['id'], $row['id'], $decision, $ip);
            }
            return [];
        });
    }

    /**
     * 제안된 선정을 최종 승인한다. 제안한 사람은 승인할 수 없다.
     * @return string[] 문제 목록
     */
    public function approveDecisions(array $admin, $programId, $reason, $ip = '')
    {
        self::need($admin, Auth::SCH_APPROVE);
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $programId, $form, $reason, $ip) {
            $program = $this->programRow($programId, true);
            if (!$this->app->features()->isEnabled(Features::SCH_AWARDS)) {
                $form->fail('선정 결과를 확정하는 기능이 꺼져 있습니다. 법률 검토와 운영 승인이 끝난 뒤에 할 수 있습니다.');
            }
            $awards = $db->all("SELECT w.* FROM scholarship_awards w JOIN scholarship_applications a ON a.id = w.application_id"
                . " WHERE w.program_id = ? AND w.status = 'proposed' AND a.status = 'in_review' FOR UPDATE", [$program['id']]);
            if (!$awards) {
                $form->fail('승인할 제안이 없습니다.');
            }
            foreach ($awards as $award) {
                if ((int)$award['decided_by'] === (int)$admin['id']) {
                    $form->fail('선정을 제안한 사람은 최종 승인할 수 없습니다. 다른 관리자가 승인해야 합니다.');
                    break;
                }
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            foreach ($awards as $award) {
                $db->exec("UPDATE scholarship_awards SET status = 'approved', approved_by = ?, approved_at = ?, updated_at = ? WHERE id = ?",
                    [$admin['id'], Clock::sql(), Clock::sql(), $award['id']]);
                $db->exec("UPDATE scholarship_applications SET status = 'decided', updated_at = ? WHERE id = ?", [Clock::sql(), $award['application_id']]);
                $this->log($admin, 'award.approve', $program['id'], $award['application_id'], $award['decision'] . ': ' . $reason, $ip);
            }
            return [];
        });
    }

    /**
     * 결과를 알린다: 신청자가 진행 상태 화면에서 결과를 볼 수 있게 된다 (서버는 문자나 메일을 보내지 않는다).
     * 개인정보 보관 기한이 이때 정해진다.
     * @return string[] 문제 목록
     */
    public function announce(array $admin, $programId, $reason, $ip = '')
    {
        self::need($admin, Auth::SCH_APPROVE);
        $form = new Form(['reason' => (string)$reason]);
        $reason = $form->reason();
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $programId, $form, $reason, $ip) {
            $program = $this->programRow($programId, true);
            if (!$this->app->features()->isEnabled(Features::SCH_AWARDS)) {
                $form->fail('선정 결과를 알리는 기능이 꺼져 있습니다.');
            }
            $rows = $db->all("SELECT a.id, w.decision FROM scholarship_applications a JOIN scholarship_awards w ON w.application_id = a.id"
                . " WHERE a.program_id = ? AND a.status = 'decided' AND w.status = 'approved' FOR UPDATE", [$program['id']]);
            if (!$rows) {
                $form->fail('알릴 결과가 없습니다. 최종 승인이 먼저 있어야 합니다.');
            }
            if ((int)$db->val("SELECT COUNT(*) FROM scholarship_applications WHERE program_id = ? AND status IN ('submitted', 'in_review')", [$program['id']]) > 0) {
                $form->fail('아직 자격 확인이나 심사가 끝나지 않은 신청이 있습니다. 모두 결정한 뒤에 한꺼번에 알립니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            foreach ($rows as $row) {
                $days = $row['decision'] === 'selected' ? (int)$program['retention_days_selected'] : (int)$program['retention_days_unselected'];
                $db->exec("UPDATE scholarship_applications SET status = 'announced', result = ?, result_notified_at = ?, result_viewed_at = NULL, purge_after = ?, updated_at = ? WHERE id = ?",
                    [$row['decision'], Clock::sql(), Clock::koreaDate(Clock::now() + $days * 86400), Clock::sql(), $row['id']]);
                $this->log($admin, 'result.announce', $program['id'], $row['id'], $row['decision'], $ip);
            }
            $db->exec("UPDATE scholarship_programs SET status = 'announced', announced_at = COALESCE(announced_at, ?), updated_at = ? WHERE id = ?",
                [Clock::sql(), Clock::sql(), $program['id']]);
            $this->log($admin, 'program.announce', $program['id'], null, count($rows) . '건: ' . $reason, $ip);
            return [];
        });
    }

    // ── 이의신청 ────────────────────────────────────────────

    public function appeals(array $admin)
    {
        self::need($admin, Auth::SCH_VIEW);
        return $this->app->db->all(
            'SELECT x.id, x.status, x.created_at, x.handled_at, x.response, a.id AS application_id, a.public_id, a.status AS application_status, p.title'
            . ' FROM scholarship_appeals x JOIN scholarship_applications a ON a.id = x.application_id JOIN scholarship_programs p ON p.id = a.program_id'
            . " ORDER BY x.status = 'submitted' DESC, x.id DESC LIMIT 200");
    }

    /** 이의신청의 사유를 읽는다 (기록이 남는다). */
    public function appealReason(array $admin, $appealId, $ip = '')
    {
        self::need($admin, Auth::SCH_MANAGE);
        $appeal = $this->app->db->one('SELECT x.*, a.program_id FROM scholarship_appeals x JOIN scholarship_applications a ON a.id = x.application_id WHERE x.id = ?',
            [(int)$appealId]);
        if ($appeal === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 이의신청입니다.');
        }
        $this->log($admin, 'appeal.view', $appeal['program_id'], $appeal['application_id'], null, $ip);
        return $appeal + ['reason' => $this->app->crypto()->open($appeal['reason_enc'], 'appeal.reason')];
    }

    /**
     * 이의신청을 처리한다. 받아들이면(upheld) 그 신청을 앞 단계로 되돌려 다시 본다:
     * 자격 미해당이었으면 접수 상태로, 결과 발표 뒤였으면 심사 중으로. 원래 결정을 내린 사람은 처리할 수 없다.
     * @return string[] 문제 목록
     */
    public function handleAppeal(array $admin, $appealId, $outcome, $response, $ip = '')
    {
        self::need($admin, Auth::SCH_APPROVE);
        $form = new Form(['outcome' => (string)$outcome, 'response' => (string)$response]);
        $outcome = $form->choice('outcome', '처리 결과', ['upheld' => '받아들임', 'dismissed' => '받아들이지 않음']);
        $response = $form->text('response', '답변', 10, 500);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $appealId, $form, $outcome, $response, $ip) {
            $appeal = $db->one('SELECT * FROM scholarship_appeals WHERE id = ? FOR UPDATE', [(int)$appealId]);
            if ($appeal === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 이의신청입니다.');
            }
            $application = $this->applicationRow($appeal['application_id'], true);
            $award = $db->one('SELECT * FROM scholarship_awards WHERE application_id = ?', [$application['id']]);
            if ($appeal['status'] !== 'submitted') {
                $form->fail('이미 처리한 이의신청입니다.');
            }
            $original = $application['status'] === 'ineligible' ? $application['eligibility_by'] : ($award !== null ? $award['decided_by'] : null);
            if ($original !== null && (int)$original === (int)$admin['id']) {
                $form->fail('원래 결정을 내린 사람은 그 이의신청을 처리할 수 없습니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $db->exec('UPDATE scholarship_appeals SET status = ?, response = ?, handled_by = ?, handled_at = ? WHERE id = ?',
                [$outcome, $response, $admin['id'], Clock::sql(), $appeal['id']]);
            if ($outcome === 'upheld') {
                if ($application['status'] === 'ineligible') {
                    $db->exec("UPDATE scholarship_applications SET status = 'submitted', eligibility_status = 'pending', purge_after = NULL, updated_at = ? WHERE id = ?",
                        [Clock::sql(), $application['id']]);
                } elseif ($application['status'] === 'announced') {
                    $db->exec("UPDATE scholarship_applications SET status = 'in_review', result = NULL, result_notified_at = NULL, purge_after = NULL, updated_at = ? WHERE id = ?",
                        [Clock::sql(), $application['id']]);
                    $db->exec("UPDATE scholarship_awards SET status = 'reopened', updated_at = ? WHERE application_id = ?", [Clock::sql(), $application['id']]);
                }
            }
            $this->log($admin, 'appeal.' . $outcome, $application['program_id'], $application['id'], null, $ip);
            return [];
        });
    }

    // ── 지급 상태 ───────────────────────────────────────────

    /** 지급 기록과, 지급 기록을 만들 수 있는 선정자. 이름과 연락처는 없다 (접수 번호로만 보인다). */
    public function disbursements(array $admin)
    {
        self::need($admin, Auth::SCH_DISBURSE);
        $db = $this->app->db;
        return [
            'records' => $db->all(
                'SELECT d.*, a.public_id, p.title FROM scholarship_disbursement_records d JOIN scholarship_applications a ON a.id = d.application_id'
                . ' JOIN scholarship_programs p ON p.id = a.program_id ORDER BY d.id DESC LIMIT 300'),
            'awardees' => $db->all(
                'SELECT a.id, a.public_id, a.disbursement_status, p.title, p.support_type, p.support_detail FROM scholarship_applications a'
                . ' JOIN scholarship_programs p ON p.id = a.program_id JOIN scholarship_awards w ON w.application_id = a.id'
                . " WHERE a.status = 'announced' AND a.result = 'selected' AND w.status = 'approved' ORDER BY a.id DESC LIMIT 300"),
        ];
    }

    /**
     * 지급 기록을 만든다 (예정). 실제 지급은 이 시스템 밖의 승인된 절차로 한다.
     * @return string[] 문제 목록
     */
    public function createDisbursement(array $admin, $applicationId, array $input, $ip = '')
    {
        self::need($admin, Auth::SCH_DISBURSE);
        $form = new Form($input);
        $type = $form->choice('support_type', '지원 방식', Scholarships::supportTypes());
        $description = $form->text('description', '지원 내용', 2, 300);
        $amount = $form->optionalInt('amount_krw', '금액(원)', 0, 100000000);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $applicationId, $form, $type, $description, $amount, $ip) {
            if (!$this->app->features()->isEnabled(Features::SCH_AWARDS)) {
                $form->fail('지급 상태를 기록하는 기능이 꺼져 있습니다.');
            }
            $application = $this->applicationRow($applicationId, true);
            $award = $db->one("SELECT * FROM scholarship_awards WHERE application_id = ? AND status = 'approved' AND decision = 'selected'", [$application['id']]);
            if ($application['status'] !== 'announced' || $application['result'] !== 'selected' || $award === null) {
                $form->fail('결과가 발표된 선정자에게만 지급 기록을 만들 수 있습니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $id = $db->insert('INSERT INTO scholarship_disbursement_records (award_id, application_id, support_type, description, amount_krw, status, created_by, created_at, updated_at)'
                . " VALUES (?, ?, ?, ?, ?, 'planned', ?, ?, ?)", [$award['id'], $application['id'], $type, $description, $amount, $admin['id'], Clock::sql(), Clock::sql()]);
            $db->exec("UPDATE scholarship_applications SET disbursement_status = 'planned', updated_at = ? WHERE id = ?", [Clock::sql(), $application['id']]);
            $this->log($admin, 'disbursement.plan', $application['program_id'], $application['id'], '#' . $id . ' ' . $type, $ip);
            return [];
        });
    }

    /**
     * 지급 기록의 상태를 바꾼다.
     *   approve  예정 → 승인 (기록을 만든 사람은 승인할 수 없다)
     *   complete 승인 → 완료 (실제 지급을 처리한 절차의 문서 번호가 있어야 한다)
     *   cancel   예정·승인 → 취소
     * @return string[] 문제 목록
     */
    public function disbursementTransition(array $admin, $id, $action, array $input, $ip = '')
    {
        self::need($admin, Auth::SCH_DISBURSE);
        $rules = ['approve' => [['planned'], 'approved'], 'complete' => [['approved'], 'completed'], 'cancel' => [['planned', 'approved'], 'cancelled']];
        if (!isset($rules[$action])) {
            throw new ApiError(ApiError::INVALID_REQUEST, '할 수 없는 일입니다.');
        }
        list($from, $to) = $rules[$action];
        $form = new Form($input);
        $note = $form->reason('note');
        $reference = $action === 'complete' ? $form->text('external_reference', '지급 처리 문서 번호', 2, 120) : null;
        $db = $this->app->db;
        return $db->tx(function () use ($db, $admin, $id, $action, $from, $to, $form, $note, $reference, $ip) {
            $record = $db->one('SELECT * FROM scholarship_disbursement_records WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($record === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 지급 기록입니다.');
            }
            if ($action !== 'cancel' && !$this->app->features()->isEnabled(Features::SCH_AWARDS)) {
                $form->fail('지급 상태를 기록하는 기능이 꺼져 있습니다.');
            }
            if (!in_array($record['status'], $from, true)) {
                $form->fail('지금 상태(' . self::disbursementStatuses()[$record['status']] . ')에서는 할 수 없습니다.');
            }
            if ($action === 'approve' && (int)$record['created_by'] === (int)$admin['id']) {
                $form->fail('지급 기록을 만든 사람은 승인할 수 없습니다. 다른 관리자가 승인해야 합니다.');
            }
            if (!$form->ok()) {
                return $form->problems;
            }
            $application = $this->applicationRow($record['application_id'], true);
            $sets = ['status = ?', 'status_note = ?', 'updated_at = ?'];
            $params = [$to, $note, Clock::sql()];
            if ($action === 'approve') {
                $sets[] = 'approved_by = ?';
                $sets[] = 'approved_at = ?';
                array_push($params, $admin['id'], Clock::sql());
            } elseif ($action === 'complete') {
                $sets[] = 'completed_by = ?';
                $sets[] = 'completed_at = ?';
                $sets[] = 'external_reference = ?';
                array_push($params, $admin['id'], Clock::sql(), $reference);
            }
            $params[] = $record['id'];
            $db->exec('UPDATE scholarship_disbursement_records SET ' . implode(', ', $sets) . ' WHERE id = ?', $params);
            $db->exec('UPDATE scholarship_applications SET disbursement_status = ?, updated_at = ? WHERE id = ?', [$to, Clock::sql(), $application['id']]);
            $this->log($admin, 'disbursement.' . $action, $application['program_id'], $application['id'], '#' . $record['id'] . ': ' . $note, $ip);
            return [];
        });
    }

    // ── 감사 로그, 보관 기한 ────────────────────────────────

    /** 감사 로그. $action 을 주면 그 종류만 (예: pii.view = 개인정보 열람 기록). */
    public function auditLog(array $admin, $action = null, $page = 1)
    {
        self::need($admin, Auth::BENEFIT_AUDIT);
        return $this->app->db->all(
            'SELECT l.*, a.login FROM scholarship_audit_logs l LEFT JOIN admins a ON a.id = l.actor_id AND l.actor_type = \'admin\''
            . ($action !== null && $action !== '' ? ' WHERE l.action = ?' : '') . ' ORDER BY l.id DESC LIMIT 100 OFFSET ' . (max(1, (int)$page) - 1) * 100,
            $action !== null && $action !== '' ? [$action] : []);
    }

    /**
     * 보관 기한이 지난 신청의 개인정보를 지운다. 예약 작업이 부른다.
     * 접수 번호, 상태, 결과, 심사 점수는 남는다 (누구의 것인지 알 수 없는 통계로).
     */
    public function purgeDue($limit = 200)
    {
        $db = $this->app->db;
        $count = 0;
        foreach ($db->all('SELECT id, program_id FROM scholarship_applications WHERE purge_after IS NOT NULL AND purge_after < ? AND purged_at IS NULL ORDER BY id LIMIT '
            . (int)$limit, [Clock::koreaDate()]) as $row) {
            $db->tx(function () use ($db, $row) {
                $now = Clock::sql();
                $db->exec('UPDATE scholarship_applications SET form_enc = NULL, eligibility_note = NULL, purged_at = ?, updated_at = ? WHERE id = ?', [$now, $now, $row['id']]);
                $db->exec('UPDATE scholarship_applicant_profiles SET name_enc = NULL, birth_year = NULL, school_name_enc = NULL, email_enc = NULL, phone_enc = NULL, purged_at = ?'
                    . ' WHERE application_id = ?', [$now, $row['id']]);
                $db->exec("UPDATE scholarship_documents SET title = '(삭제됨)', content_enc = NULL, url = NULL, purged_at = ? WHERE application_id = ?", [$now, $row['id']]);
                $db->exec('UPDATE scholarship_consents SET guardian_name_enc = NULL, guardian_contact_enc = NULL, token_hash = NULL, verify_note = NULL, updated_at = ? WHERE application_id = ?',
                    [$now, $row['id']]);
                $db->exec('UPDATE scholarship_appeals SET reason_enc = NULL WHERE application_id = ?', [$row['id']]);
                $db->exec('UPDATE scholarship_reviews SET comment = NULL WHERE application_id = ?', [$row['id']]);
                $this->app->scholarships()->audit('system', null, 'application.purge', $row['program_id'], $row['id']);
            });
            $count++;
        }
        return $count;
    }
}
