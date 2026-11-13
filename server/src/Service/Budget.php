<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 참여예산 (docs/PARTICIPATORY_BUDGET.md).
 *
 *   제안(submitted) → 검토(관리자: voting 또는 rejected) → 주민 투표 → 결과 확정(passed 또는 failed) → 집행(executed) → 집행 내역 공개
 *
 * - 한 계정은 한 제안에 한 번만 투표한다 (city_budget_votes 의 (proposal_id, player_id) 가 유일).
 * - 한 표의 무게는 누구나 같다. 가진 GC, 낸 세금, 레벨, 기여도는 표의 무게에 들어가지 않는다 (무게를 담는 열 자체가 없다).
 * - 나이를 묻지 않는다. 미성년자도 그 도시의 주민이면 제안하고 투표할 수 있다. 게임 안의 교육용 활동이고 실제 돈과 이어지지 않는다.
 * - 제안은 관리자가 검토한 뒤에만 다른 주민에게 보인다 (부적절한 글이 그대로 공개되지 않게).
 * - 집행은 Treasury 가 한다. 제안 하나는 한 번만 집행된다.
 */
final class Budget
{
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
            'submitted' => '검토 대기', 'rejected' => '반려', 'voting' => '투표 중', 'passed' => '가결', 'failed' => '부결',
            'executed' => '집행 완료', 'withdrawn' => '철회', 'cancelled' => '취소',
        ];
    }

    /** 다른 주민에게도 보이는 상태. */
    private static function publicStatuses()
    {
        return ['voting', 'passed', 'failed', 'executed'];
    }

    /** 제안할 수 있는 분야 (예비비는 제안할 수 없다). */
    public static function categories()
    {
        $categories = Treasury::categories();
        unset($categories['reserve']);
        return $categories;
    }

    private function requireEnabled()
    {
        if ($this->app->settings()->budget()['enabled'] !== 1) {
            throw new ApiError(ApiError::FEATURE_DISABLED, '참여예산은 아직 열리지 않았어요.', 'budget_disabled');
        }
    }

    private static function clean($value, $max)
    {
        $value = is_string($value) ? $value : '';
        // 제어 문자를 지우고 공백을 정리한다.
        $value = trim(preg_replace('/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/u', '', $value));
        return mb_strlen($value) > $max ? null : $value;
    }

    /** 대상(방어시설·주요 시설)의 이름. 대상이 필요 없는 분야면 ''. 잘못된 대상이면 null. */
    private function targetName($category, $target)
    {
        $effect = Treasury::effectOf($category);
        if ($effect === Treasury::EFFECT_FUND) {
            $def = $this->app->defs()->findStructure($target);
            return $def !== null && $def['ownership'] === 'city' && !$def['comingSoon'] && $def['cityGoalGc'] > 0 ? $def['name'] : null;
        }
        if ($effect === Treasury::EFFECT_REPAIR) {
            $def = $this->app->defs()->findFacility($target);
            return $def !== null ? $def['name'] : null;
        }
        return '';
    }

    // ── 플레이어 ────────────────────────────────────────────

    /** 제안할 때 고를 수 있는 것들과 자격. */
    public function options(array $player)
    {
        $config = $this->app->settings()->budget();
        $categories = [];
        foreach (self::categories() as $code => $name) {
            $categories[] = ['category' => $code, 'name' => $name, 'effectType' => Treasury::effectOf($code)];
        }
        $structures = [];
        foreach ($this->app->defs()->structures() as $def) {
            if ($def['ownership'] === 'city' && !$def['comingSoon'] && $def['cityGoalGc'] > 0) {
                $structures[] = ['code' => $def['code'], 'name' => $def['name']];
            }
        }
        $facilities = [];
        foreach ($this->app->defs()->facilities() as $def) {
            $facilities[] = ['code' => $def['code'], 'name' => $def['name'], 'repairGc' => $def['repairGc']];
        }
        return [
            'enabled' => $config['enabled'] === 1, 'categories' => $categories, 'structures' => $structures, 'facilities' => $facilities,
            'maxRequestGc' => $config['maxRequestGc'], 'proposerMinLevel' => $config['proposerMinLevel'],
            'voterMinLevel' => $config['voterMinLevel'], 'voterMinResidentDays' => $config['voterMinResidentDays'],
            'voteDays' => $config['voteDays'], 'quorumVotes' => $config['quorumVotes'],
            'notice' => '한 사람이 한 표를 가져요. 가진 GC나 낸 세금이 많다고 표가 늘어나지 않아요.',
        ];
    }

    /** POST /cities/budget/proposals: 내 주거 도시에 예산을 제안한다. */
    public function propose(array $player, array $form, $idemKey)
    {
        $category = isset($form['category']) && is_string($form['category']) ? $form['category'] : '';
        $title = self::clean(isset($form['title']) ? $form['title'] : '', 60);
        $description = self::clean(isset($form['description']) ? $form['description'] : '', 600);
        $requested = isset($form['requestedGc']) && is_int($form['requestedGc']) ? $form['requestedGc'] : 0;
        $target = isset($form['targetCode']) && is_string($form['targetCode']) ? $form['targetCode'] : '';
        $request = ['category' => $category, 'title' => $title, 'description' => $description, 'gc' => $requested, 'target' => $target];

        return $this->app->idempotency()->run($player['id'], $idemKey, 'budget.propose', $request,
            function () use ($player, $category, $title, $description, $requested, $target) {
                $this->requireEnabled();
                $config = $this->app->settings()->budget();
                $player = $this->app->players()->get($player['id']);
                if (!$player['resident']) {
                    throw new ApiError(ApiError::CONFLICT, '전입신고를 한 주민만 제안할 수 있어요.', 'not_resident');
                }
                if ($player['level'] < $config['proposerMinLevel']) {
                    throw new ApiError(ApiError::LOCKED, '레벨 ' . $config['proposerMinLevel'] . '부터 제안할 수 있어요.', 'level_too_low');
                }
                if (!isset(self::categories()[$category])) {
                    throw new ApiError(ApiError::INVALID_REQUEST, '분야를 골라 주세요.');
                }
                if ($title === null || $title === '') {
                    throw new ApiError(ApiError::INVALID_REQUEST, '제목은 1 ~ 60자로 적어 주세요.');
                }
                if ($description === null) {
                    throw new ApiError(ApiError::INVALID_REQUEST, '설명은 600자까지 적을 수 있어요.');
                }
                if ($requested < 1 || $requested > $config['maxRequestGc']) {
                    throw new ApiError(ApiError::INVALID_REQUEST, '금액은 1 ~ ' . number_format($config['maxRequestGc']) . ' GC 사이로 적어 주세요.');
                }
                if ($this->targetName($category, $target) === null) {
                    throw new ApiError(ApiError::INVALID_REQUEST, '대상 시설을 골라 주세요.');
                }
                $open = (int)$this->db->val(
                    "SELECT COUNT(*) FROM city_budget_proposals WHERE player_id = ? AND status IN ('submitted', 'voting')", [$player['id']]);
                if ($open >= $config['maxOpenProposalsPerPlayer']) {
                    throw new ApiError(ApiError::LIMIT_REACHED, '진행 중인 제안이 끝난 뒤에 새로 제안할 수 있어요.', 'too_many_proposals');
                }
                $now = Clock::sql();
                $publicId = Ids::publicId('b_');
                $this->db->insert(
                    'INSERT INTO city_budget_proposals (public_id, region_id, player_id, category, title, description, requested_gc, target_code,'
                    . " status, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'submitted', ?, ?)",
                    [$publicId, $player['regionId'], $player['id'], $category, $title, $description, $requested,
                        Treasury::effectOf($category) === Treasury::EFFECT_RECORD ? null : $target, $now, $now]
                );
                return ['proposal' => $this->out($this->row($publicId), $player)];
            });
    }

    private function row($publicId)
    {
        if (!is_string($publicId) || !preg_match('/^b_[a-z0-9]{14}$/', $publicId)) {
            return null;
        }
        return $this->db->one(
            'SELECT b.*, r.code AS region_code, r.name AS region_name, p.nickname FROM city_budget_proposals b'
            . ' JOIN regions r ON r.id = b.region_id JOIN players p ON p.id = b.player_id WHERE b.public_id = ?', [$publicId]);
    }

    /** 이 사람이 이 제안에 투표할 수 없는 이유. 할 수 있으면 null. */
    private function voteBlock(array $row, array $player, $myVote)
    {
        $config = $this->app->settings()->budget();
        if ($config['enabled'] !== 1) {
            return 'budget_disabled';
        }
        if ($row['status'] !== 'voting' || Clock::now() >= Clock::parse($row['vote_ends_at'])) {
            return 'not_voting';
        }
        if ($myVote !== null) {
            return 'already_voted';
        }
        if (!$player['resident'] || (int)$player['regionId'] !== (int)$row['region_id']) {
            return 'not_resident_here';
        }
        if ($player['level'] < $config['voterMinLevel']) {
            return 'level_too_low';
        }
        $registeredAt = $this->db->val('SELECT registered_at FROM players WHERE id = ?', [$player['id']]);
        if ($registeredAt === null || Clock::now() < Clock::parse($registeredAt) + $config['voterMinResidentDays'] * 86400) {
            return 'resident_too_new';
        }
        return null;
    }

    private static function blockMessage($reason)
    {
        $messages = [
            'budget_disabled' => '참여예산은 아직 열리지 않았어요.', 'not_voting' => '지금은 투표할 수 없는 제안이에요.',
            'already_voted' => '이미 투표했어요.', 'not_resident_here' => '이 도시의 주민만 투표할 수 있어요.',
            'level_too_low' => '레벨이 조금 더 오르면 투표할 수 있어요.', 'resident_too_new' => '전입한 지 며칠이 지나야 투표할 수 있어요.',
        ];
        return isset($messages[$reason]) ? $messages[$reason] : '';
    }

    private function out(array $row, array $player)
    {
        $mine = (int)$row['player_id'] === (int)$player['id'];
        $myVote = $this->db->val('SELECT choice FROM city_budget_votes WHERE proposal_id = ? AND player_id = ?', [$row['id'], $player['id']]);
        $block = $this->voteBlock($row, $player, $myVote);
        $statuses = self::statuses();
        $categories = Treasury::categories();
        $targetName = $row['target_code'] !== null ? $this->targetName($row['category'], $row['target_code']) : '';
        return [
            'id' => $row['public_id'], 'regionCode' => $row['region_code'], 'regionName' => $row['region_name'],
            'category' => $row['category'], 'categoryName' => isset($categories[$row['category']]) ? $categories[$row['category']] : $row['category'],
            'title' => $row['title'], 'description' => $row['description'], 'requestedGc' => (int)$row['requested_gc'],
            'targetCode' => $row['target_code'], 'targetName' => $targetName === null ? '' : $targetName,
            'status' => $row['status'], 'statusName' => $statuses[$row['status']],
            'proposer' => $row['nickname'], 'mine' => $mine,
            // 반려 사유는 제안한 사람에게만 보인다.
            'reviewNote' => $mine ? $row['review_note'] : '',
            'voteStartsAt' => $row['vote_starts_at'] !== null ? Clock::isoFromSql($row['vote_starts_at']) : null,
            'voteEndsAt' => $row['vote_ends_at'] !== null ? Clock::isoFromSql($row['vote_ends_at']) : null,
            'yesCount' => (int)$row['yes_count'], 'noCount' => (int)$row['no_count'], 'quorum' => (int)$row['quorum'],
            'myVote' => $myVote, 'canVote' => $block === null, 'voteBlockReason' => $block, 'voteBlockMessage' => self::blockMessage($block),
            'createdAt' => Clock::isoFromSql($row['created_at']),
        ];
    }

    /** GET /cities/{code}/budget/proposals: 그 도시의 공개된 제안과, 내가 낸 제안. */
    public function listFor(array $player, $regionCode, $status = '')
    {
        $region = $this->db->one('SELECT id, code, name FROM regions WHERE code = ?', [(string)$regionCode]);
        if ($region === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
        }
        $this->closeDue();
        $public = self::publicStatuses();
        $where = "b.region_id = ? AND (b.status IN ('" . implode("', '", $public) . "') OR b.player_id = ?)";
        $params = [(int)$region['id'], $player['id']];
        if ($status !== '' && isset(self::statuses()[$status])) {
            $where .= ' AND b.status = ?';
            $params[] = $status;
        }
        $items = [];
        foreach ($this->db->all(
            'SELECT b.*, r.code AS region_code, r.name AS region_name, p.nickname FROM city_budget_proposals b'
            . ' JOIN regions r ON r.id = b.region_id JOIN players p ON p.id = b.player_id WHERE ' . $where
            . " ORDER BY (b.status = 'voting') DESC, b.id DESC LIMIT 100", $params
        ) as $row) {
            $items[] = $this->out($row, $player);
        }
        return ['regionCode' => $region['code'], 'regionName' => $region['name'], 'proposals' => $items, 'options' => $this->options($player)];
    }

    /** GET /cities/budget/proposals/{id} */
    public function detail(array $player, $publicId)
    {
        $this->closeDue();
        $row = $this->row($publicId);
        if ($row === null || (!in_array($row['status'], self::publicStatuses(), true) && (int)$row['player_id'] !== (int)$player['id'])) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 제안이에요.');
        }
        $out = $this->out($row, $player);
        $out['expenditure'] = null;
        if ($row['expenditure_id'] !== null) {
            $spent = $this->db->one('SELECT public_id, amount_gc, effect_detail, created_at FROM city_budget_expenditures WHERE id = ?',
                [$row['expenditure_id']]);
            if ($spent !== null) {
                $out['expenditure'] = ['id' => $spent['public_id'], 'amountGc' => (int)$spent['amount_gc'],
                    'effectDetail' => $spent['effect_detail'], 'at' => Clock::isoFromSql($spent['created_at'])];
            }
        }
        return ['proposal' => $out];
    }

    /** POST /cities/budget/proposals/{id}/vote: 한 계정이 한 번. 바꿀 수 없다. */
    public function vote(array $player, $publicId, $choice, $idemKey)
    {
        if ($choice !== 'yes' && $choice !== 'no') {
            throw new ApiError(ApiError::INVALID_REQUEST, '찬성 또는 반대를 골라 주세요.');
        }
        return $this->app->idempotency()->run($player['id'], $idemKey, 'budget.vote', ['proposal' => (string)$publicId, 'choice' => $choice],
            function () use ($player, $publicId, $choice) {
                $this->requireEnabled();
                $found = $this->row($publicId);
                if ($found === null || !in_array($found['status'], self::publicStatuses(), true)) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 제안이에요.');
                }
                // 제안 행을 잠근 뒤에 다시 읽는다: 마감 처리와 겹치지 않게.
                $this->db->val('SELECT id FROM city_budget_proposals WHERE id = ? FOR UPDATE', [$found['id']]);
                $row = $this->row($publicId);
                $player = $this->app->players()->get($player['id']);
                $myVote = $this->db->val('SELECT choice FROM city_budget_votes WHERE proposal_id = ? AND player_id = ?', [$row['id'], $player['id']]);
                $block = $this->voteBlock($row, $player, $myVote);
                if ($block !== null) {
                    throw new ApiError($block === 'level_too_low' || $block === 'resident_too_new' ? ApiError::LOCKED : ApiError::CONFLICT,
                        self::blockMessage($block), $block);
                }
                // 유일 키가 마지막 방어선이다.
                $inserted = $this->db->exec('INSERT IGNORE INTO city_budget_votes (proposal_id, player_id, choice, created_at) VALUES (?, ?, ?, ?)',
                    [$row['id'], $player['id'], $choice, Clock::sql()]);
                if ($inserted < 1) {
                    throw new ApiError(ApiError::CONFLICT, self::blockMessage('already_voted'), 'already_voted');
                }
                $column = $choice === 'yes' ? 'yes_count' : 'no_count';
                $this->db->exec("UPDATE city_budget_proposals SET $column = $column + 1, updated_at = ? WHERE id = ?", [Clock::sql(), $row['id']]);
                return ['proposal' => $this->out($this->row($publicId), $player)];
            });
    }

    /** POST /cities/budget/proposals/{id}/withdraw: 검토 전인 내 제안을 거둔다. */
    public function withdraw(array $player, $publicId)
    {
        return $this->db->tx(function () use ($player, $publicId) {
            $found = $this->row($publicId);
            if ($found === null || (int)$found['player_id'] !== (int)$player['id']) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 제안이에요.');
            }
            $status = $this->db->val('SELECT status FROM city_budget_proposals WHERE id = ? FOR UPDATE', [$found['id']]);
            if ($status !== 'submitted') {
                throw new ApiError(ApiError::CONFLICT, '검토가 시작된 제안은 거둘 수 없어요.', 'not_withdrawable');
            }
            $this->db->exec("UPDATE city_budget_proposals SET status = 'withdrawn', updated_at = ? WHERE id = ?", [Clock::sql(), $found['id']]);
            return ['proposal' => $this->out($this->row($publicId), $player)];
        });
    }

    // ── 결과 확정 ───────────────────────────────────────────

    /**
     * 투표 기간이 끝난 제안의 결과를 확정한다. 예약 작업과 목록 조회가 부른다. 여러 번 불러도 같다.
     * 찬성이 반대보다 많고, 전체 투표 수가 투표를 열 때 정해 둔 최소 투표 수 이상이면 가결이다.
     * @return int 확정한 제안의 수
     */
    public function closeDue()
    {
        $due = $this->db->all("SELECT id FROM city_budget_proposals WHERE status = 'voting' AND vote_ends_at <= ? ORDER BY id LIMIT 200",
            [Clock::sql()]);
        $closed = 0;
        foreach ($due as $item) {
            $closed += $this->db->tx(function () use ($item) {
                $row = $this->db->one('SELECT id, status, vote_ends_at, quorum FROM city_budget_proposals WHERE id = ? FOR UPDATE', [$item['id']]);
                if ($row === null || $row['status'] !== 'voting' || Clock::now() < Clock::parse($row['vote_ends_at'])) {
                    return 0;
                }
                // 집계 열이 아니라 표를 직접 센다.
                $yes = (int)$this->db->val("SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ? AND choice = 'yes'", [$row['id']]);
                $no = (int)$this->db->val("SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ? AND choice = 'no'", [$row['id']]);
                $passed = $yes > $no && $yes + $no >= (int)$row['quorum'];
                $this->db->exec('UPDATE city_budget_proposals SET status = ?, yes_count = ?, no_count = ?, decided_at = ?, updated_at = ? WHERE id = ?',
                    [$passed ? 'passed' : 'failed', $yes, $no, Clock::sql(), Clock::sql(), $row['id']]);
                return 1;
            });
        }
        return $closed;
    }

    // ── 관리자 ──────────────────────────────────────────────

    public function adminList($status = '', $regionId = null, $limit = 200)
    {
        $where = '1 = 1';
        $params = [];
        if ($status !== '' && isset(self::statuses()[$status])) {
            $where .= ' AND b.status = ?';
            $params[] = $status;
        }
        if ($regionId !== null) {
            $where .= ' AND b.region_id = ?';
            $params[] = (int)$regionId;
        }
        return $this->db->all(
            'SELECT b.*, r.code AS region_code, r.name AS region_name, p.public_id AS player_public_id, p.nickname FROM city_budget_proposals b'
            . ' JOIN regions r ON r.id = b.region_id JOIN players p ON p.id = b.player_id WHERE ' . $where
            . ' ORDER BY b.id DESC LIMIT ' . (int)$limit, $params);
    }

    public function adminGet($id)
    {
        return $this->db->one(
            'SELECT b.*, r.code AS region_code, r.name AS region_name, p.public_id AS player_public_id, p.nickname FROM city_budget_proposals b'
            . ' JOIN regions r ON r.id = b.region_id JOIN players p ON p.id = b.player_id WHERE b.id = ?', [(int)$id]);
    }

    public function adminCounts()
    {
        $counts = [];
        foreach (array_keys(self::statuses()) as $status) {
            $counts[$status] = 0;
        }
        foreach ($this->db->all('SELECT status, COUNT(*) AS n FROM city_budget_proposals GROUP BY status') as $row) {
            $counts[$row['status']] = (int)$row['n'];
        }
        return $counts;
    }

    /**
     * 제안을 검토한다. 승인하면 투표가 열리고, 반려하면 제안한 사람에게만 사유가 보인다.
     * @return string[] 문제 목록
     */
    public function review(array $admin, $id, $approve, $note, $ip = '')
    {
        if (!Auth::can($admin, Auth::BUDGET_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $note = is_string($note) ? trim($note) : '';
        $problem = DefEditor::reasonProblem($note);
        if ($problem !== null) {
            return [$problem];
        }
        $config = $this->app->settings()->budget();
        if ($approve && $config['enabled'] !== 1) {
            return ['참여예산이 꺼져 있습니다. 먼저 설정에서 켜 주세요.'];
        }
        return $this->db->tx(function () use ($admin, $id, $approve, $note, $config, $ip) {
            $row = $this->db->one('SELECT * FROM city_budget_proposals WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($row === null) {
                return ['없는 제안입니다.'];
            }
            if ($row['status'] !== 'submitted') {
                return ['검토 대기 중인 제안만 검토할 수 있습니다.'];
            }
            $now = Clock::now();
            if ($approve) {
                $this->db->exec(
                    "UPDATE city_budget_proposals SET status = 'voting', review_note = ?, reviewed_by = ?, reviewed_at = ?, vote_starts_at = ?,"
                    . ' vote_ends_at = ?, quorum = ?, updated_at = ? WHERE id = ?',
                    [$note, $admin['id'], Clock::sql($now), Clock::sql($now), Clock::sql($now + $config['voteDays'] * 86400),
                        $config['quorumVotes'], Clock::sql($now), $row['id']]
                );
            } else {
                $this->db->exec(
                    "UPDATE city_budget_proposals SET status = 'rejected', review_note = ?, reviewed_by = ?, reviewed_at = ?, updated_at = ? WHERE id = ?",
                    [$note, $admin['id'], Clock::sql($now), Clock::sql($now), $row['id']]
                );
            }
            (new Auth($this->app))->audit($admin['id'], $approve ? 'budget.approve' : 'budget.reject', 'city_budget_proposals',
                $row['public_id'], $note, $ip);
            return [];
        });
    }

    /** 투표 중이거나 가결된 제안을 취소한다 (규칙을 어긴 글이 뒤늦게 발견된 경우 등). 이미 집행된 제안은 취소할 수 없다. */
    public function cancel(array $admin, $id, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::BUDGET_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $reason = is_string($reason) ? trim($reason) : '';
        $problem = DefEditor::reasonProblem($reason);
        if ($problem !== null) {
            return [$problem];
        }
        return $this->db->tx(function () use ($admin, $id, $reason, $ip) {
            $row = $this->db->one('SELECT * FROM city_budget_proposals WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($row === null) {
                return ['없는 제안입니다.'];
            }
            if ($row['status'] !== 'voting' && $row['status'] !== 'passed') {
                return ['투표 중이거나 가결된 제안만 취소할 수 있습니다.'];
            }
            $this->db->exec("UPDATE city_budget_proposals SET status = 'cancelled', review_note = ?, decided_at = ?, updated_at = ? WHERE id = ?",
                [$reason, Clock::sql(), Clock::sql(), $row['id']]);
            (new Auth($this->app))->audit($admin['id'], 'budget.cancel', 'city_budget_proposals', $row['public_id'], $reason, $ip);
            return [];
        });
    }

    /**
     * 가결된 제안을 집행한다. 도시 재정에서 나가고, 집행 내역이 공개된다. 제안 하나는 한 번만 집행된다.
     * @return string[] 문제 목록
     */
    public function execute(array $admin, $id, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::TREASURY_EXECUTE)) {
            return ['권한이 없습니다.'];
        }
        $this->closeDue();
        $problems = [];
        try {
            $this->db->tx(function () use ($admin, $id, $reason, $ip, &$problems) {
                $row = $this->db->one(
                    'SELECT b.*, r.code AS region_code FROM city_budget_proposals b JOIN regions r ON r.id = b.region_id WHERE b.id = ? FOR UPDATE',
                    [(int)$id]);
                if ($row === null) {
                    $problems = ['없는 제안입니다.'];
                    return;
                }
                if ($row['status'] !== 'passed') {
                    $problems = ['가결된 제안만 집행할 수 있습니다.'];
                    return;
                }
                list($expenditureId, $found) = $this->app->treasury()->spend($admin, $row['region_code'], [
                    'category' => $row['category'], 'title' => $row['title'], 'description' => $row['description'],
                    'amount_gc' => (string)$row['requested_gc'], 'target_code' => (string)$row['target_code'], 'reason' => is_string($reason) ? $reason : '',
                ], $row, $ip);
                if ($found) {
                    $problems = $found;
                    // 집행하다 만 것이 남지 않게 거래 전체를 되돌린다.
                    throw new \RuntimeException('budget_execute_failed');
                }
                $this->db->exec("UPDATE city_budget_proposals SET status = 'executed', expenditure_id = ?, updated_at = ? WHERE id = ?",
                    [$expenditureId, Clock::sql(), $row['id']]);
                (new Auth($this->app))->audit($admin['id'], 'budget.execute', 'city_budget_proposals', $row['public_id'], (string)$reason, $ip);
            });
        } catch (\RuntimeException $error) {
            if ($error->getMessage() !== 'budget_execute_failed') {
                throw $error;
            }
        }
        return $problems;
    }
}
