<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 상징거리 모델링의 제작·검수·배포 관리 (docs/CITY_MODELING_PIPELINE.md).
 *
 * 작업(프로젝트)의 상태: NOT_STARTED → PLANNING → MODELING → REVIEW → READY → DEPLOYED
 *   - REVIEW 에서 READY 로 가려면 검수 통과가 있어야 한다. 담당자 본인은 검수할 수 없다.
 *   - DEPLOYED 는 손으로 고르지 못한다. 결과물(콘텐츠 묶음)의 한 판을 배포했을 때에만 된다.
 * 결과물의 상태: uploaded → approved → released → withdrawn (또는 rejected)
 *   - 올린 사람은 자기 결과물을 승인할 수 없다.
 *   - 배포는 받는 비율(0~100%)을 정해 조금씩 넓힐 수 있고, 문제가 있으면 바로 회수한다.
 *
 * 서버는 파일을 받지도 내보내지도 않는다. 콘텐츠 저장소 안의 경로와 해시만 적어 두고, 클라이언트에게는
 * "어느 도시의 어느 판을 어디에서 받는가"만 알려 준다. 받을 주소의 앞부분은 서버 설정(CONTENT_BASE_URL)으로 고정한다:
 * 관리자 화면에서 임의의 주소를 클라이언트에 내려보낼 수 없다.
 */
final class Modeling
{
    const NOT_STARTED = 'NOT_STARTED';
    const PLANNING = 'PLANNING';
    const MODELING = 'MODELING';
    const REVIEW = 'REVIEW';
    const READY = 'READY';
    const DEPLOYED = 'DEPLOYED';

    /** @var App */
    private $app;
    /** @var \CG\Infra\Db */
    private $db;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->db = $app->db;
    }

    public static function statuses()
    {
        return [
            self::NOT_STARTED => '미착수', self::PLANNING => '기획', self::MODELING => '제작', self::REVIEW => '검수',
            self::READY => '배포 준비', self::DEPLOYED => '적용 완료',
        ];
    }

    public static function kinds()
    {
        return ['landmark' => '상징물', 'street' => '상징거리', 'special_zone' => '특화 구역'];
    }

    public static function assetStatuses()
    {
        return ['uploaded' => '등록됨', 'approved' => '승인됨', 'rejected' => '반려됨', 'released' => '배포 중', 'withdrawn' => '회수됨'];
    }

    /** 손으로 바꿀 수 있는 상태 이동: [지금 => [다음, …]]. READY 로 가는 길은 검수(review)뿐이고, DEPLOYED 는 배포(release)뿐이다. */
    public static function manualTransitions()
    {
        return [
            self::NOT_STARTED => [self::PLANNING],
            self::PLANNING => [self::MODELING, self::NOT_STARTED],
            self::MODELING => [self::REVIEW, self::PLANNING],
            self::REVIEW => [self::MODELING],
            self::READY => [self::REVIEW],
            self::DEPLOYED => [],
        ];
    }

    private static function rank($status)
    {
        return (int)array_search($status, array_keys(self::statuses()), true);
    }

    private static function text(array $form, $key)
    {
        return isset($form[$key]) && is_string($form[$key]) ? trim($form[$key]) : '';
    }

    private function audit(array $admin, $action, $key, $detail, $ip)
    {
        (new Auth($this->app))->audit($admin['id'], $action, 'modeling', (string)$key, $detail, $ip);
    }

    /** 모델링 진행 알림. 기준 도달 알림과 종류(kind)가 다르다. */
    private function notice(array $project, $title, $body)
    {
        $region = $this->db->val('SELECT name FROM regions WHERE id = ?', [$project['region_id']]);
        $this->app->growth()->notify(Growth::NOTICE_MODELING, (int)$project['region_id'],
            'modeling:' . $project['id'] . ':' . Ids::token(12), $region . ' · ' . $project['title'] . ': ' . $title, $body, null, (int)$project['id']);
    }

    // ── 조회 ────────────────────────────────────────────────

    public function projects($status = null, $regionId = null, $limit = 300)
    {
        $where = '1 = 1';
        $params = [];
        if ($status === 'active') {
            $where .= " AND m.status <> 'DEPLOYED'";
        } elseif ($status !== null && isset(self::statuses()[$status])) {
            $where .= ' AND m.status = ?';
            $params[] = $status;
        }
        if ($regionId !== null) {
            $where .= ' AND m.region_id = ?';
            $params[] = (int)$regionId;
        }
        return $this->db->all(
            'SELECT m.*, r.code AS region_code, r.name AS region_name, a.login AS assignee_login FROM city_modeling_projects m'
            . ' JOIN regions r ON r.id = m.region_id LEFT JOIN admins a ON a.id = m.assignee_admin_id'
            . ' WHERE ' . $where . ' ORDER BY m.id DESC LIMIT ' . (int)$limit, $params);
    }

    public function project($id)
    {
        return $this->db->one(
            'SELECT m.*, r.code AS region_code, r.name AS region_name, a.login AS assignee_login, v.login AS reviewer_login'
            . ' FROM city_modeling_projects m JOIN regions r ON r.id = m.region_id'
            . ' LEFT JOIN admins a ON a.id = m.assignee_admin_id LEFT JOIN admins v ON v.id = m.reviewed_by WHERE m.id = ?', [(int)$id]);
    }

    public function assets($projectId = null, $status = null, $limit = 300)
    {
        $where = '1 = 1';
        $params = [];
        if ($projectId !== null) {
            $where .= ' AND s.project_id = ?';
            $params[] = (int)$projectId;
        }
        if ($status !== null) {
            $where .= ' AND s.status = ?';
            $params[] = $status;
        }
        return $this->db->all(
            'SELECT s.*, r.code AS region_code, r.name AS region_name, m.title AS project_title, m.kind AS project_kind, m.status AS project_status,'
            . ' u.login AS uploader_login, v.login AS reviewer_login, l.login AS releaser_login'
            . ' FROM city_modeling_assets s JOIN city_modeling_projects m ON m.id = s.project_id JOIN regions r ON r.id = s.region_id'
            . ' LEFT JOIN admins u ON u.id = s.uploaded_by LEFT JOIN admins v ON v.id = s.reviewed_by LEFT JOIN admins l ON l.id = s.released_by'
            . ' WHERE ' . $where . ' ORDER BY s.id DESC LIMIT ' . (int)$limit, $params);
    }

    // ── 작업 ────────────────────────────────────────────────

    private function projectProblems(array $form, array &$values)
    {
        $problems = [];
        $title = self::text($form, 'title');
        if ($title === '' || mb_strlen($title) > 80) {
            $problems[] = '작업 이름은 1 ~ 80자여야 합니다.';
        }
        $due = self::text($form, 'due_on');
        if ($due !== '') {
            $date = \DateTime::createFromFormat('!Y-m-d', $due);
            if ($date === false || $date->format('Y-m-d') !== $due) {
                $problems[] = '예정일은 2026-12-31 같은 형식이어야 합니다.';
            }
        }
        $memo = self::text($form, 'memo');
        if (mb_strlen($memo) > 4000) {
            $problems[] = '작업 메모는 4000자까지입니다.';
        }
        $version = self::text($form, 'file_version');
        if ($version !== '' && !preg_match('/^[A-Za-z0-9][A-Za-z0-9._\-]{0,31}$/', $version)) {
            $problems[] = '파일 버전은 영문·숫자·._- 로 32자까지입니다.';
        }
        $assignee = self::text($form, 'assignee_admin_id');
        $assigneeId = null;
        if ($assignee !== '') {
            $assigneeId = ctype_digit($assignee)
                ? $this->db->val('SELECT id FROM admins WHERE id = ? AND is_active = 1', [(int)$assignee]) : null;
            if ($assigneeId === null) {
                $problems[] = '없는 담당자입니다.';
            }
        }
        $values = ['title' => $title, 'due_on' => $due !== '' ? $due : null, 'memo' => $memo, 'file_version' => $version,
            'assignee_admin_id' => $assigneeId !== null ? (int)$assigneeId : null];
        return $problems;
    }

    /**
     * 제작 작업을 등록한다. 알림에서 만들 때에는 그 알림을 확인한 뒤여야 한다.
     * @return array [작업 id 또는 null, 문제 목록]
     */
    public function createProject(array $admin, array $form, $ip = '')
    {
        if (!Auth::can($admin, Auth::MODELING_MANAGE)) {
            return [null, ['권한이 없습니다.']];
        }
        $values = [];
        $problems = $this->projectProblems($form, $values);
        $kind = self::text($form, 'kind');
        if (!isset(self::kinds()[$kind])) {
            $problems[] = '없는 작업 종류입니다.';
        }
        $region = $this->db->one('SELECT id, code FROM regions WHERE code = ?', [self::text($form, 'region_code')]);
        if ($region === null) {
            $problems[] = '없는 지역입니다.';
        }
        $notice = null;
        $noticeId = self::text($form, 'notification_id');
        if ($noticeId !== '') {
            $notice = ctype_digit($noticeId) ? $this->db->one('SELECT * FROM city_growth_notifications WHERE id = ?', [(int)$noticeId]) : null;
            if ($notice === null || $notice['kind'] !== Growth::NOTICE_MILESTONE || ($region !== null && (int)$notice['region_id'] !== (int)$region['id'])) {
                $problems[] = '이 도시의 기준 도달 알림이 아닙니다.';
            } elseif ($notice['status'] === 'new') {
                $problems[] = '먼저 알림을 확인해 주세요.';
            }
        }
        if ($problems) {
            return [null, $problems];
        }
        $id = $this->db->tx(function () use ($admin, $values, $kind, $region, $notice, $ip) {
            $now = Clock::sql();
            $id = $this->db->insert(
                'INSERT INTO city_modeling_projects (public_id, region_id, milestone_id, notification_id, kind, title, status, assignee_admin_id,'
                . ' memo, due_on, file_version, created_by, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
                [Ids::publicId('m_'), $region['id'], $notice !== null ? $notice['milestone_id'] : null, $notice !== null ? $notice['id'] : null,
                    $kind, $values['title'], self::NOT_STARTED, $values['assignee_admin_id'], $values['memo'], $values['due_on'],
                    $values['file_version'], $admin['id'], $now, $now]
            );
            $project = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ?', [$id]);
            $this->notice($project, '제작 작업 등록', '종류: ' . self::kinds()[$kind]);
            $this->audit($admin, 'modeling.project.create', $id, 'region=' . $region['code'] . ' kind=' . $kind, $ip);
            $this->syncCity((int)$region['id']);
            return $id;
        });
        return [$id, []];
    }

    /** 담당자, 메모, 예정일, 파일 버전, 이름을 고친다. @return string[] 문제 목록 */
    public function updateProject(array $admin, $id, array $form, $ip = '')
    {
        if (!Auth::can($admin, Auth::MODELING_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $values = [];
        $problems = $this->projectProblems($form, $values);
        if ($problems) {
            return $problems;
        }
        return $this->db->tx(function () use ($admin, $id, $values, $ip) {
            $before = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($before === null) {
                return ['없는 작업입니다.'];
            }
            $changed = [];
            foreach ($values as $column => $value) {
                $old = $before[$column];
                if ($column === 'assignee_admin_id') {
                    $old = $old !== null ? (int)$old : null;
                }
                if ($old !== $value && !($old === null && $value === '')) {
                    $changed[] = $column;
                }
            }
            if (!$changed) {
                return [];
            }
            $this->db->exec('UPDATE city_modeling_projects SET title = ?, due_on = ?, memo = ?, file_version = ?, assignee_admin_id = ?, updated_at = ? WHERE id = ?',
                [$values['title'], $values['due_on'], $values['memo'], $values['file_version'], $values['assignee_admin_id'], Clock::sql(), $before['id']]);
            if (in_array('assignee_admin_id', $changed, true)) {
                $login = $values['assignee_admin_id'] !== null ? $this->db->val('SELECT login FROM admins WHERE id = ?', [$values['assignee_admin_id']]) : null;
                $this->notice($before, '담당자 변경', $login !== null ? '담당자: ' . $login : '담당자 없음');
            }
            $this->audit($admin, 'modeling.project.update', $before['id'], implode(',', $changed), $ip);
            return [];
        });
    }

    /** 상태를 손으로 옮긴다. @return string[] 문제 목록 */
    public function transition(array $admin, $id, $to, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::MODELING_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            return [$reasonProblem];
        }
        return $this->db->tx(function () use ($admin, $id, $to, $reason, $ip) {
            $project = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($project === null) {
                return ['없는 작업입니다.'];
            }
            $allowed = self::manualTransitions();
            if (!is_string($to) || !in_array($to, $allowed[$project['status']], true)) {
                $names = self::statuses();
                if ($to === self::READY) {
                    return ['배포 준비 상태는 검수를 통과해야 됩니다.'];
                }
                if ($to === self::DEPLOYED) {
                    return ['적용 완료 상태는 결과물을 배포해야 됩니다.'];
                }
                return [$names[$project['status']] . ' 상태에서는 그렇게 옮길 수 없습니다.'];
            }
            if ($to === self::REVIEW && $project['status'] === self::MODELING && $project['file_version'] === '') {
                return ['검수에 넘기려면 파일 버전을 먼저 적어 주세요.'];
            }
            // 검수로 다시 넘기면 이전의 검수 결과는 지운다.
            $clearReview = $to === self::REVIEW || $to === self::MODELING;
            $this->db->exec(
                'UPDATE city_modeling_projects SET status = ?, updated_at = ?'
                . ($clearReview ? ", review_result = 'none', review_note = '', reviewed_by = NULL, reviewed_at = NULL" : '') . ' WHERE id = ?',
                [$to, Clock::sql(), $project['id']]);
            $names = self::statuses();
            $this->notice($project, $names[$project['status']] . ' → ' . $names[$to], trim($reason));
            $this->audit($admin, 'modeling.project.status', $project['id'], $project['status'] . '>' . $to . ': ' . trim($reason), $ip);
            $this->syncCity((int)$project['region_id']);
            return [];
        });
    }

    /** 검수 결과를 적는다. 통과면 READY, 반려면 MODELING 으로 돌아간다. 담당자 본인은 검수할 수 없다. */
    public function review(array $admin, $id, $result, $note, $ip = '')
    {
        if (!Auth::can($admin, Auth::MODELING_REVIEW)) {
            return ['권한이 없습니다.'];
        }
        if ($result !== 'pass' && $result !== 'fail') {
            return ['검수 결과를 골라 주세요.'];
        }
        $note = is_string($note) ? trim($note) : '';
        if (mb_strlen($note) < 2 || mb_strlen($note) > 500) {
            return ['검수 의견을 적어 주세요 (500자까지).'];
        }
        return $this->db->tx(function () use ($admin, $id, $result, $note, $ip) {
            $project = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($project === null) {
                return ['없는 작업입니다.'];
            }
            if ($project['status'] !== self::REVIEW) {
                return ['검수 상태인 작업만 검수할 수 있습니다.'];
            }
            if ($project['assignee_admin_id'] !== null && (int)$project['assignee_admin_id'] === (int)$admin['id']) {
                return ['담당자 본인은 검수할 수 없습니다.'];
            }
            $to = $result === 'pass' ? self::READY : self::MODELING;
            $this->db->exec('UPDATE city_modeling_projects SET status = ?, review_result = ?, review_note = ?, reviewed_by = ?, reviewed_at = ?, updated_at = ? WHERE id = ?',
                [$to, $result, $note, $admin['id'], Clock::sql(), Clock::sql(), $project['id']]);
            $this->notice($project, $result === 'pass' ? '검수 통과' : '검수 반려', $note);
            $this->audit($admin, 'modeling.project.review', $project['id'], $result . ': ' . $note, $ip);
            $this->syncCity((int)$project['region_id']);
            return [];
        });
    }

    // ── 결과물 (콘텐츠 묶음) ────────────────────────────────

    /** 결과물의 한 판을 등록한다. 파일은 콘텐츠 저장소에 따로 올리고, 여기에는 경로와 해시만 적는다. */
    public function addAsset(array $admin, $projectId, array $form, $ip = '')
    {
        if (!Auth::can($admin, Auth::MODELING_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $problems = [];
        $version = self::text($form, 'version');
        if (!preg_match('/^\d{1,4}\.\d{1,4}\.\d{1,6}$/', $version)) {
            $problems[] = '버전은 1.0.0 같은 형식이어야 합니다.';
        }
        $address = self::text($form, 'address_key');
        if (!preg_match('#^[A-Za-z0-9][A-Za-z0-9._\-/]{0,119}$#', $address)) {
            $problems[] = '콘텐츠 주소는 영문·숫자·._-/ 로 120자까지입니다.';
        }
        $path = self::text($form, 'catalog_path');
        if (!preg_match('#^[A-Za-z0-9][A-Za-z0-9._\-/]{0,254}$#', $path) || strpos($path, '..') !== false || strpos($path, '//') !== false) {
            $problems[] = '카탈로그 경로는 콘텐츠 저장소 안의 상대 경로여야 합니다 (영문·숫자·._-/ , ".." 없이).';
        }
        $hash = strtolower(self::text($form, 'content_hash'));
        if (!preg_match('/^[0-9a-f]{64}$/', $hash)) {
            $problems[] = '해시는 SHA-256 (16진수 64자) 이어야 합니다.';
        }
        $size = self::text($form, 'size_bytes');
        if (!preg_match('/^\d{1,12}$/', $size) || (int)$size < 1) {
            $problems[] = '크기(바이트)는 1 이상의 정수여야 합니다.';
        }
        $minClient = self::text($form, 'min_client_version');
        if ($minClient !== '' && !preg_match('/^\d{1,4}\.\d{1,4}\.\d{1,6}$/', $minClient)) {
            $problems[] = '최소 클라이언트 버전은 9.0.0 같은 형식이어야 합니다.';
        }
        $note = self::text($form, 'note');
        if (mb_strlen($note) > 500) {
            $problems[] = '메모는 500자까지입니다.';
        }
        if ($problems) {
            return $problems;
        }
        return $this->db->tx(function () use ($admin, $projectId, $version, $address, $path, $hash, $size, $minClient, $note, $ip) {
            $project = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ? FOR UPDATE', [(int)$projectId]);
            if ($project === null) {
                return ['없는 작업입니다.'];
            }
            if (self::rank($project['status']) < self::rank(self::MODELING)) {
                return ['제작 단계부터 결과물을 등록할 수 있습니다.'];
            }
            if ((int)$this->db->val('SELECT COUNT(*) FROM city_modeling_assets WHERE project_id = ? AND version = ?', [$project['id'], $version]) > 0) {
                return ['이미 있는 버전입니다. 내용이 바뀌었으면 버전을 올려 주세요.'];
            }
            $now = Clock::sql();
            $id = $this->db->insert(
                'INSERT INTO city_modeling_assets (public_id, project_id, region_id, version, address_key, catalog_path, content_hash, size_bytes,'
                . " min_client_version, status, rollout_percent, note, uploaded_by, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 'uploaded', 0, ?, ?, ?, ?)",
                [Ids::publicId('c_'), $project['id'], $project['region_id'], $version, $address, $path, $hash, (int)$size, $minClient, $note,
                    $admin['id'], $now, $now]
            );
            $this->db->exec('UPDATE city_modeling_projects SET file_version = ?, updated_at = ? WHERE id = ?', [$version, $now, $project['id']]);
            $this->notice($project, '결과물 ' . $version . ' 등록', $note);
            $this->audit($admin, 'modeling.asset.add', $id, 'project=' . $project['id'] . ' version=' . $version . ' hash=' . $hash, $ip);
            return [];
        });
    }

    /** 결과물을 승인하거나 반려한다. 올린 사람은 자기 결과물을 승인할 수 없다. */
    public function reviewAsset(array $admin, $assetId, $approve, $note, $ip = '')
    {
        if (!Auth::can($admin, Auth::MODELING_REVIEW)) {
            return ['권한이 없습니다.'];
        }
        $note = is_string($note) ? trim($note) : '';
        if (mb_strlen($note) < 2 || mb_strlen($note) > 500) {
            return ['검수 의견을 적어 주세요 (500자까지).'];
        }
        return $this->db->tx(function () use ($admin, $assetId, $approve, $note, $ip) {
            $asset = $this->db->one('SELECT * FROM city_modeling_assets WHERE id = ? FOR UPDATE', [(int)$assetId]);
            if ($asset === null) {
                return ['없는 결과물입니다.'];
            }
            if ($asset['status'] !== 'uploaded') {
                return ['등록된 상태의 결과물만 검수할 수 있습니다.'];
            }
            if ($asset['uploaded_by'] !== null && (int)$asset['uploaded_by'] === (int)$admin['id']) {
                return ['올린 사람은 자기 결과물을 검수할 수 없습니다.'];
            }
            $this->db->exec('UPDATE city_modeling_assets SET status = ?, reviewed_by = ?, reviewed_at = ?, review_note = ?, updated_at = ? WHERE id = ?',
                [$approve ? 'approved' : 'rejected', $admin['id'], Clock::sql(), $note, Clock::sql(), $asset['id']]);
            $project = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ?', [$asset['project_id']]);
            $this->notice($project, '결과물 ' . $asset['version'] . ($approve ? ' 승인' : ' 반려'), $note);
            $this->audit($admin, 'modeling.asset.review', $asset['id'], ($approve ? 'approved' : 'rejected') . ': ' . $note, $ip);
            return [];
        });
    }

    /**
     * 결과물을 클라이언트에 배포한다 (또는 이미 배포 중인 판의 받는 비율을 바꾼다).
     * 작업이 READY 이상이고 결과물이 승인되어 있어야 한다. 같은 작업의 이전 배포판은 회수 처리된다.
     */
    public function release(array $admin, $assetId, $rolloutPercent, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::CONTENT_RELEASE)) {
            return ['권한이 없습니다.'];
        }
        $rollout = is_string($rolloutPercent) ? trim($rolloutPercent) : (string)$rolloutPercent;
        if (!preg_match('/^\d{1,3}$/', $rollout) || (int)$rollout < 1 || (int)$rollout > 100) {
            return ['받는 비율은 1 ~ 100 사이의 정수여야 합니다.'];
        }
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            return [$reasonProblem];
        }
        if ($this->contentBase() === '') {
            return ['콘텐츠 저장소 주소(CONTENT_BASE_URL)가 서버에 설정되어 있지 않아 배포할 수 없습니다.'];
        }
        return $this->db->tx(function () use ($admin, $assetId, $rollout, $reason, $ip) {
            $asset = $this->db->one('SELECT * FROM city_modeling_assets WHERE id = ? FOR UPDATE', [(int)$assetId]);
            if ($asset === null) {
                return ['없는 결과물입니다.'];
            }
            $project = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ? FOR UPDATE', [$asset['project_id']]);
            if ($asset['status'] !== 'approved' && $asset['status'] !== 'released') {
                return ['승인된 결과물만 배포할 수 있습니다.'];
            }
            if ($project['status'] !== self::READY && $project['status'] !== self::DEPLOYED) {
                return ['작업이 배포 준비 상태(검수 통과)여야 배포할 수 있습니다.'];
            }
            $now = Clock::sql();
            $first = $asset['status'] !== 'released';
            if ($first) {
                $this->db->exec("UPDATE city_modeling_assets SET status = 'withdrawn', withdrawn_by = ?, withdrawn_at = ?, withdraw_reason = ?, rollout_percent = 0,"
                    . " updated_at = ? WHERE project_id = ? AND status = 'released'",
                    [$admin['id'], $now, '새 판 ' . $asset['version'] . ' 배포', $now, $project['id']]);
            }
            $this->db->exec("UPDATE city_modeling_assets SET status = 'released', rollout_percent = ?, released_by = ?, released_at = COALESCE(released_at, ?),"
                . ' updated_at = ? WHERE id = ?', [(int)$rollout, $admin['id'], $now, $now, $asset['id']]);
            $this->db->exec('UPDATE city_modeling_projects SET status = ?, applied_at = COALESCE(applied_at, ?), file_version = ?, updated_at = ? WHERE id = ?',
                [self::DEPLOYED, $now, $asset['version'], $now, $project['id']]);
            $this->notice($project, '결과물 ' . $asset['version'] . ' 배포 (' . (int)$rollout . '%)', trim($reason));
            $this->audit($admin, 'modeling.asset.release', $asset['id'], 'rollout=' . (int)$rollout . ' ' . trim($reason), $ip);
            $this->syncCity((int)$project['region_id']);
            return [];
        });
    }

    /** 배포한 결과물을 회수한다. 클라이언트는 다음에 목록을 받을 때부터 이 판을 받지 않고 기본 도시를 쓴다. */
    public function withdraw(array $admin, $assetId, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::CONTENT_WITHDRAW)) {
            return ['권한이 없습니다.'];
        }
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            return [$reasonProblem];
        }
        return $this->db->tx(function () use ($admin, $assetId, $reason, $ip) {
            $asset = $this->db->one('SELECT * FROM city_modeling_assets WHERE id = ? FOR UPDATE', [(int)$assetId]);
            if ($asset === null) {
                return ['없는 결과물입니다.'];
            }
            if ($asset['status'] !== 'released') {
                return ['배포 중인 결과물만 회수할 수 있습니다.'];
            }
            $project = $this->db->one('SELECT * FROM city_modeling_projects WHERE id = ? FOR UPDATE', [$asset['project_id']]);
            $now = Clock::sql();
            $this->db->exec("UPDATE city_modeling_assets SET status = 'withdrawn', rollout_percent = 0, withdrawn_by = ?, withdrawn_at = ?, withdraw_reason = ?,"
                . ' updated_at = ? WHERE id = ?', [$admin['id'], $now, mb_substr(trim($reason), 0, 255), $now, $asset['id']]);
            // 배포 중인 판이 없어졌으므로 작업은 "배포 준비"로 돌아간다.
            $this->db->exec('UPDATE city_modeling_projects SET status = ?, updated_at = ? WHERE id = ?', [self::READY, $now, $project['id']]);
            $this->notice($project, '결과물 ' . $asset['version'] . ' 회수', trim($reason));
            $this->audit($admin, 'modeling.asset.withdraw', $asset['id'], trim($reason), $ip);
            $this->syncCity((int)$project['region_id']);
            return [];
        });
    }

    /**
     * 도시 현황(city_population_stats)의 모델링 상태·적용 버전·정식 개방 일시를 작업과 결과물에 맞춘다.
     * 상징거리 작업이 있으면 그것을, 없으면 가장 많이 진행된 작업을 따른다.
     * 정식 개방 일시는 상징거리의 결과물이 처음으로 모든 클라이언트(100%)에 배포된 때다.
     */
    public function syncCity($regionId)
    {
        $status = self::NOT_STARTED;
        $street = null;
        foreach ($this->db->all('SELECT id, kind, status FROM city_modeling_projects WHERE region_id = ?', [(int)$regionId]) as $project) {
            if ($project['kind'] === 'street' && ($street === null || self::rank($project['status']) > self::rank($street['status']))) {
                $street = $project;
            }
            if (self::rank($project['status']) > self::rank($status)) {
                $status = $project['status'];
            }
        }
        if ($street !== null) {
            $status = $street['status'];
        }
        $released = $street !== null ? $this->db->one(
            "SELECT version, rollout_percent FROM city_modeling_assets WHERE project_id = ? AND status = 'released' ORDER BY id DESC LIMIT 1",
            [$street['id']]) : null;
        $now = Clock::sql();
        $this->db->exec('INSERT IGNORE INTO city_population_stats (region_id, updated_at) VALUES (?, ?)', [(int)$regionId, $now]);
        $this->db->exec('UPDATE city_population_stats SET modeling_status = ?, content_version = ?, updated_at = ? WHERE region_id = ?',
            [$status, $released !== null ? $released['version'] : null, $now, (int)$regionId]);
        if ($released !== null && (int)$released['rollout_percent'] >= 100) {
            $this->db->exec('UPDATE city_population_stats SET opened_at = ? WHERE region_id = ? AND opened_at IS NULL', [$now, (int)$regionId]);
        }
    }

    // ── 클라이언트에 알려 주는 콘텐츠 목록 ──────────────────

    /** 콘텐츠 저장소 주소의 앞부분. https 만 받는다. 설정하지 않았으면 빈 글자. */
    public function contentBase()
    {
        $base = rtrim($this->app->env->get('CONTENT_BASE_URL', ''), '/');
        return preg_match('#^https://[A-Za-z0-9.\-]+(:\d+)?(/[A-Za-z0-9._~\-/]*)?$#', $base) ? $base : '';
    }

    /** 이 플레이어가 이 결과물을 받는 무리에 드는가. 계정마다 늘 같은 답이 나온다 (비율을 올리면 받는 사람이 늘 뿐 바뀌지 않는다). */
    public static function inRollout($playerPublicId, $assetPublicId, $percent)
    {
        if ($percent >= 100) {
            return true;
        }
        if ($percent <= 0) {
            return false;
        }
        return hexdec(substr(hash('sha256', $playerPublicId . ':' . $assetPublicId), 0, 8)) % 100 < $percent;
    }

    /**
     * GET /cities/content: 이 클라이언트가 받아도 되는 도시 콘텐츠.
     * 배포 중(released)이고, 클라이언트 버전이 충분하고, 받는 무리에 든 것만 준다.
     */
    public function catalog(array $player, $clientVersion)
    {
        $base = $this->contentBase();
        $items = [];
        if ($base !== '') {
            foreach ($this->db->all(
                "SELECT s.*, r.code AS region_code, m.kind FROM city_modeling_assets s JOIN city_modeling_projects m ON m.id = s.project_id"
                . " JOIN regions r ON r.id = s.region_id WHERE s.status = 'released' ORDER BY s.id"
            ) as $row) {
                $minClient = $row['min_client_version'];
                if ($minClient !== '' && (!is_string($clientVersion) || !preg_match('/^\d+(\.\d+){0,3}$/', $clientVersion)
                    || version_compare($clientVersion, $minClient, '<'))) {
                    continue;
                }
                if (!self::inRollout($player['publicId'], $row['public_id'], (int)$row['rollout_percent'])) {
                    continue;
                }
                $items[] = [
                    'contentId' => $row['public_id'], 'regionCode' => $row['region_code'], 'kind' => $row['kind'],
                    'version' => $row['version'], 'addressKey' => $row['address_key'],
                    'catalogUrl' => $base . '/' . $row['catalog_path'], 'contentHash' => $row['content_hash'],
                    'sizeBytes' => (int)$row['size_bytes'], 'minClientVersion' => $minClient,
                ];
            }
        }
        return ['available' => $base !== '', 'items' => $items];
    }
}
