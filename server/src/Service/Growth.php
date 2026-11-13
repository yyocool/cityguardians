<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;

/**
 * 도시 성장: 유효 전입자 집계, 이사, 확장 단계, 기준 도달 알림 (docs/CITY_GROWTH_SYSTEM.md).
 *
 * 유효 전입자 수는 "다시 세어서" 구한다. 더하고 빼는 식으로 관리하지 않는다:
 *   - 한 계정은 players.region_id 하나만 가지므로 동시에 두 도시에 집계될 수 없다.
 *   - 세는 조건(연결된 계정인가, 활동이 확인되었는가)이 나중에 바뀌어도 다시 세면 맞는다.
 *   - 게스트 계정을 여러 개 만들어 전입해도 조건을 채우지 못하면 수에 들어가지 않는다.
 * 기존 regions.resident_count 는 "전입신고를 한 계정 수"로 그대로 두고, 확장 단계와 알림은 유효 전입자 수만 본다.
 */
final class Growth
{
    const EVENT_REGISTER = 'register';
    const EVENT_MOVE_IN = 'move_in';
    const EVENT_MOVE_OUT = 'move_out';

    const NOTICE_MILESTONE = 'milestone';
    const NOTICE_MODELING = 'modeling';

    /** @var App */
    private $app;
    /** @var \CG\Infra\Db */
    private $db;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->db = $app->db;
    }

    // ── 유효 전입의 조건 ────────────────────────────────────

    /**
     * 유효 전입으로 세는 조건 (players p, characters ch 를 가리키는 SQL 조각).
     * @return array [SQL, 바인딩 값]
     */
    private function validWhere()
    {
        $config = $this->app->settings()->growth();
        $sql = "p.residency_status = 'resident' AND p.status = 'active' AND p.region_id IS NOT NULL AND ("
            . "EXISTS (SELECT 1 FROM player_credentials c WHERE c.player_id = p.id AND c.type <> 'guest')";
        $params = [];
        if ($config['populationMode'] === Settings::MODE_LINKED_OR_PROVEN) {
            // 활동으로 확인된 게스트: 레벨, 계정을 만든 뒤 지난 날수, 플레이한 날수를 모두 채워야 한다.
            // 중복 계정을 만드는 비용을 올릴 뿐 완전히 막지는 못한다. 그래서 기본값이 아니다.
            $sql .= ' OR (COALESCE(ch.level, 1) >= ? AND p.created_at <= ?'
                . ' AND (SELECT COUNT(DISTINCT d.date) FROM daily_counters d WHERE d.player_id = p.id) >= ?)';
            $params = [$config['provenMinLevel'], Clock::sql(Clock::now() - $config['provenMinAccountDays'] * 86400),
                $config['provenMinActiveDays']];
        }
        return [$sql . ')', $params];
    }

    private static function from()
    {
        return ' FROM players p LEFT JOIN characters ch ON ch.player_id = p.id WHERE ';
    }

    /** 이 계정의 전입이 지금 유효 전입으로 세어지는가. */
    public function isCounted($playerId)
    {
        list($where, $params) = $this->validWhere();
        $params[] = $playerId;
        return (int)$this->db->val('SELECT COUNT(*)' . self::from() . $where . ' AND p.id = ?', $params) > 0;
    }

    public function isLinked($playerId)
    {
        return (int)$this->db->val("SELECT COUNT(*) FROM player_credentials WHERE player_id = ? AND type <> 'guest'", [$playerId]) > 0;
    }

    // ── 다시 세기 ───────────────────────────────────────────

    /** 지금의 거래가 커밋된 뒤에 그 도시를 다시 센다. 집계가 실패해도 전입·이사는 되돌리지 않는다 (다음 예약 작업이 바로잡는다). */
    public function touch($regionId)
    {
        $regionId = (int)$regionId;
        $this->db->afterCommit(function () use ($regionId) {
            try {
                $this->refreshRegion($regionId);
            } catch (\Throwable $error) {
                $this->app->log('error', 'growth', '전입자 집계 실패: ' . get_class($error) . ': ' . $error->getMessage(), 'region ' . $regionId);
            }
        });
    }

    /**
     * 한 도시의 전입자 현황을 다시 센다.
     *
     * 현황 행을 먼저 잠근 다음에 센다. 같은 도시를 동시에 세는 요청은 줄을 서고, 뒤에 선 요청은 앞 요청이 커밋한 것까지
     * 보고 세므로 마지막에 적힌 수가 가장 새로운 수다.
     * @return array validResidents, registeredResidents, cumulativeResidents, stage, created(새로 생긴 알림 수)
     */
    public function refreshRegion($regionId)
    {
        $regionId = (int)$regionId;
        return $this->db->tx(function () use ($regionId) {
            $now = Clock::sql();
            // 먼저 잠근다. 현황 행이 아직 없을 때에만 만든다.
            $stats = $this->db->one('SELECT * FROM city_population_stats WHERE region_id = ? FOR UPDATE', [$regionId]);
            if ($stats === null) {
                $this->db->exec('INSERT IGNORE INTO city_population_stats (region_id, updated_at) VALUES (?, ?)', [$regionId, $now]);
                $stats = $this->db->one('SELECT * FROM city_population_stats WHERE region_id = ? FOR UPDATE', [$regionId]);
            }

            list($where, $params) = $this->validWhere();
            $this->db->exec('INSERT IGNORE INTO city_resident_marks (region_id, player_id, first_counted_at) SELECT p.region_id, p.id, ?'
                . self::from() . $where . ' AND p.region_id = ?', array_merge([$now], $params, [$regionId]));
            $valid = (int)$this->db->val('SELECT COUNT(*)' . self::from() . $where . ' AND p.region_id = ?', array_merge($params, [$regionId]));
            $registered = (int)$this->db->val(
                "SELECT COUNT(*) FROM players WHERE region_id = ? AND residency_status = 'resident' AND status = 'active'", [$regionId]);
            $cumulative = (int)$this->db->val('SELECT COUNT(*) FROM city_resident_marks WHERE region_id = ?', [$regionId]);

            $today = Clock::koreaDate();
            $this->db->exec(
                'INSERT INTO city_population_daily (region_id, date, valid_residents, registered_residents) VALUES (?, ?, ?, ?)'
                . ' ON DUPLICATE KEY UPDATE valid_residents = VALUES(valid_residents), registered_residents = VALUES(registered_residents)',
                [$regionId, $today, $valid, $registered]
            );
            $growth7 = $valid - $this->baseline($regionId, 7, $valid);
            $growth30 = $valid - $this->baseline($regionId, 30, $valid);

            // 확장 단계: 달성한 기준의 수. 한 번 오른 단계는 주민이 줄어도 내려가지 않는다 (이미 제작이 시작되었을 수 있다).
            $stage = (int)$stats['stage'];
            $stageMilestone = $stats['stage_milestone_id'];
            $created = 0;
            $reached = 0;
            $region = null;
            foreach ($this->milestones() as $milestone) {
                if (!$milestone['active'] || $valid < $milestone['threshold']) {
                    continue;
                }
                $reached++;
                if ($reached > $stage) {
                    $stageMilestone = $milestone['id'];
                }
                if ($region === null) {
                    $region = $this->db->one('SELECT code, name FROM regions WHERE id = ?', [$regionId]);
                }
                $created += $this->notify(self::NOTICE_MILESTONE, $regionId, 'milestone:' . $regionId . ':' . $milestone['id'],
                    ($region ? $region['name'] : '') . ': ' . $milestone['name'] . ' 기준 도달 (' . number_format($milestone['threshold']) . '명)',
                    '유효 전입자 ' . number_format($valid) . '명. ' . $milestone['description'], $milestone['id'], null);
            }
            $stage = max($stage, $reached);

            $threshold = $stats['modeling_threshold'] !== null ? (int)$stats['modeling_threshold'] : $this->streetThreshold();
            $reachedNow = $threshold !== null && $valid >= $threshold;
            $wasReached = (int)$stats['threshold_reached'] === 1;
            $this->db->exec(
                'UPDATE city_population_stats SET valid_residents = ?, registered_residents = ?, cumulative_residents = ?, growth_7d = ?,'
                . ' growth_30d = ?, stage = ?, stage_milestone_id = ?, threshold_reached = ?, threshold_reached_at = ?, counted_at = ?,'
                . ' updated_at = ? WHERE region_id = ?',
                [$valid, $registered, $cumulative, $growth7, $growth30, $stage, $stageMilestone, ($wasReached || $reachedNow) ? 1 : 0,
                    $wasReached ? $stats['threshold_reached_at'] : ($reachedNow ? $now : null), $now, $now, $regionId]
            );
            return ['validResidents' => $valid, 'registeredResidents' => $registered, 'cumulativeResidents' => $cumulative,
                'stage' => $stage, 'created' => $created];
        });
    }

    /** 그 날수 전의 유효 전입자 수. 그만큼 오래된 기록이 없으면 가장 오래된 기록, 그것도 없으면 지금 값. */
    private function baseline($regionId, $days, $current)
    {
        $date = Clock::koreaDate(Clock::now() - $days * 86400);
        $value = $this->db->val('SELECT valid_residents FROM city_population_daily WHERE region_id = ? AND date <= ? ORDER BY date DESC LIMIT 1',
            [$regionId, $date]);
        if ($value === null) {
            $value = $this->db->val('SELECT valid_residents FROM city_population_daily WHERE region_id = ? ORDER BY date ASC LIMIT 1', [$regionId]);
        }
        return $value !== null ? (int)$value : (int)$current;
    }

    /**
     * 모든 도시를 점검한다. 예약 작업이 부른다.
     * 수가 달라졌거나 오늘의 기록이 없는 도시만 다시 센다.
     * @return array refreshed(다시 센 도시 수), notifications(새로 생긴 알림 수)
     */
    public function refreshAll()
    {
        list($where, $params) = $this->validWhere();
        $valid = [];
        foreach ($this->db->all('SELECT p.region_id, COUNT(*) AS n' . self::from() . $where . ' GROUP BY p.region_id', $params) as $row) {
            $valid[(int)$row['region_id']] = (int)$row['n'];
        }
        $registered = [];
        foreach ($this->db->all("SELECT region_id, COUNT(*) AS n FROM players WHERE residency_status = 'resident' AND status = 'active'"
            . ' AND region_id IS NOT NULL GROUP BY region_id') as $row) {
            $registered[(int)$row['region_id']] = (int)$row['n'];
        }
        $today = Clock::koreaDate();
        $stats = [];
        foreach ($this->db->all(
            'SELECT s.region_id, s.valid_residents, s.registered_residents, d.date FROM city_population_stats s'
            . ' LEFT JOIN city_population_daily d ON d.region_id = s.region_id AND d.date = ?', [$today]
        ) as $row) {
            $stats[(int)$row['region_id']] = $row;
        }
        $refreshed = 0;
        $created = 0;
        foreach ($this->db->all('SELECT id FROM regions ORDER BY id') as $region) {
            $id = (int)$region['id'];
            $v = isset($valid[$id]) ? $valid[$id] : 0;
            $r = isset($registered[$id]) ? $registered[$id] : 0;
            if (isset($stats[$id]) && $stats[$id]['date'] !== null && (int)$stats[$id]['valid_residents'] === $v
                && (int)$stats[$id]['registered_residents'] === $r) {
                continue;
            }
            $result = $this->refreshRegion($id);
            $refreshed++;
            $created += $result['created'];
        }
        return ['refreshed' => $refreshed, 'notifications' => $created];
    }

    // ── 확장 기준 ───────────────────────────────────────────

    private $milestones = null;

    /** 확장 기준 (인원이 적은 것부터). */
    public function milestones()
    {
        if ($this->milestones === null) {
            $this->milestones = [];
            foreach ($this->db->all('SELECT * FROM city_growth_milestones ORDER BY threshold, id') as $row) {
                $this->milestones[] = ['id' => (int)$row['id'], 'code' => $row['code'], 'name' => $row['name'],
                    'threshold' => (int)$row['threshold'], 'action' => $row['action'], 'description' => $row['description'],
                    'opensStreet' => (int)$row['opens_street'] === 1, 'active' => (int)$row['is_active'] === 1];
            }
        }
        return $this->milestones;
    }

    /** 상징거리 모델링 기준 인원 (공통). 정해 둔 기준이 없으면 null. */
    public function streetThreshold()
    {
        foreach ($this->milestones() as $milestone) {
            if ($milestone['active'] && $milestone['opensStreet']) {
                return $milestone['threshold'];
            }
        }
        return null;
    }

    /**
     * 관리자가 확장 기준 하나를 고친다 (인원, 이름, 설명, 사용 여부).
     * @return string[] 문제 목록
     */
    public function saveMilestone(array $admin, $code, array $form, $ip = '')
    {
        if (!Auth::can($admin, Auth::CITY_POLICY)) {
            return ['권한이 없습니다.'];
        }
        $before = $this->db->one('SELECT * FROM city_growth_milestones WHERE code = ?', [(string)$code]);
        if ($before === null) {
            return ['없는 기준입니다.'];
        }
        $text = function ($key) use ($form) {
            return isset($form[$key]) && is_string($form[$key]) ? trim($form[$key]) : '';
        };
        $problems = [];
        $threshold = $text('threshold');
        if (!preg_match('/^\d{1,9}$/', $threshold) || (int)$threshold < 1 || (int)$threshold > 10000000) {
            $problems[] = '기준 인원은 1 ~ 10,000,000 사이의 정수여야 합니다.';
        }
        $name = $text('name');
        if ($name === '' || mb_strlen($name) > 60) {
            $problems[] = '이름은 1 ~ 60자여야 합니다.';
        }
        $description = $text('description');
        if (mb_strlen($description) > 255) {
            $problems[] = '설명은 255자까지입니다.';
        }
        $reason = $text('reason');
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            $problems[] = $reasonProblem;
        }
        if ($problems) {
            return $problems;
        }
        $after = ['threshold' => (int)$threshold, 'name' => $name, 'description' => $description,
            'is_active' => isset($form['is_active']) && $form['is_active'] === '1' ? 1 : 0];
        $old = ['threshold' => (int)$before['threshold'], 'name' => $before['name'], 'description' => $before['description'],
            'is_active' => (int)$before['is_active']];
        if ($after === $old) {
            return [];
        }
        $this->db->tx(function () use ($admin, $before, $old, $after, $reason, $ip) {
            $this->db->exec('UPDATE city_growth_milestones SET threshold = ?, name = ?, description = ?, is_active = ?, updated_at = ? WHERE id = ?',
                [$after['threshold'], $after['name'], $after['description'], $after['is_active'], Clock::sql(), $before['id']]);
            $this->db->exec(
                'INSERT INTO config_history (target_type, target_key, before_value, after_value, admin_id, reason, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                ['city_growth_milestones', $before['code'], json_encode($old, JSON_UNESCAPED_UNICODE), json_encode($after, JSON_UNESCAPED_UNICODE),
                    $admin['id'], $reason, Clock::sql()]
            );
            (new Auth($this->app))->audit($admin['id'], 'growth.milestone.update', 'city_growth_milestones', $before['code'], $reason, $ip);
        });
        $this->milestones = null;
        // 기준이 낮아졌다면 이미 그 인원을 넘은 도시에 알림이 생겨야 한다.
        $this->recheckAll();
        return [];
    }

    /** 모든 도시를 지금의 기준으로 다시 점검한다 (기준을 고친 뒤). */
    public function recheckAll()
    {
        $created = 0;
        foreach ($this->db->all('SELECT region_id FROM city_population_stats WHERE valid_residents > 0') as $row) {
            $created += $this->refreshRegion((int)$row['region_id'])['created'];
        }
        return $created;
    }

    /** 관리자가 한 도시의 상징거리 기준 인원을 따로 정한다. 비우면 공통 기준을 쓴다. */
    public function setCityThreshold(array $admin, $regionCode, $value, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::CITY_POLICY)) {
            return ['권한이 없습니다.'];
        }
        $region = $this->db->one('SELECT id, code FROM regions WHERE code = ?', [(string)$regionCode]);
        if ($region === null) {
            return ['없는 지역입니다.'];
        }
        $value = is_string($value) ? trim($value) : '';
        if ($value !== '' && (!preg_match('/^\d{1,9}$/', $value) || (int)$value < 1 || (int)$value > 10000000)) {
            return ['기준 인원은 1 ~ 10,000,000 사이의 정수이거나 비어 있어야 합니다.'];
        }
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            return [$reasonProblem];
        }
        $this->db->tx(function () use ($admin, $region, $value, $reason, $ip) {
            $this->db->exec('INSERT IGNORE INTO city_population_stats (region_id, updated_at) VALUES (?, ?)', [$region['id'], Clock::sql()]);
            $before = $this->db->val('SELECT modeling_threshold FROM city_population_stats WHERE region_id = ? FOR UPDATE', [$region['id']]);
            $this->db->exec('UPDATE city_population_stats SET modeling_threshold = ?, updated_at = ? WHERE region_id = ?',
                [$value === '' ? null : (int)$value, Clock::sql(), $region['id']]);
            $this->db->exec(
                'INSERT INTO config_history (target_type, target_key, before_value, after_value, admin_id, reason, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                ['city_population_stats', $region['code'], json_encode(['modeling_threshold' => $before !== null ? (int)$before : null]),
                    json_encode(['modeling_threshold' => $value === '' ? null : (int)$value]), $admin['id'], trim($reason), Clock::sql()]
            );
            (new Auth($this->app))->audit($admin['id'], 'growth.threshold.update', 'region', $region['code'], trim($reason), $ip);
        });
        $this->refreshRegion((int)$region['id']);
        return [];
    }

    // ── 알림 ────────────────────────────────────────────────

    /**
     * 관리자 알림을 만든다. 같은 dedupe_key 의 알림이 이미 있으면 만들지 않는다.
     * @return int 새로 만들었으면 1, 이미 있었으면 0
     */
    public function notify($kind, $regionId, $dedupeKey, $title, $body, $milestoneId = null, $projectId = null)
    {
        return $this->db->exec(
            'INSERT IGNORE INTO city_growth_notifications (kind, region_id, milestone_id, project_id, dedupe_key, title, body, status, created_at)'
            . " VALUES (?, ?, ?, ?, ?, ?, ?, 'new', ?)",
            [$kind, $regionId, $milestoneId, $projectId, mb_substr($dedupeKey, 0, 80), mb_substr($title, 0, 120), mb_substr($body, 0, 500), Clock::sql()]
        );
    }

    /** 관리자 화면의 알림 목록. */
    public function notifications($kind = null, $status = null, $limit = 200)
    {
        $where = '1 = 1';
        $params = [];
        if ($kind !== null) {
            $where .= ' AND n.kind = ?';
            $params[] = $kind;
        }
        if ($status === 'open') {
            $where .= " AND n.status <> 'closed'";
        } elseif ($status !== null) {
            $where .= ' AND n.status = ?';
            $params[] = $status;
        }
        return $this->db->all(
            'SELECT n.*, r.code AS region_code, r.name AS region_name, a1.login AS acknowledged_login, a2.login AS assigned_login'
            . ' FROM city_growth_notifications n JOIN regions r ON r.id = n.region_id'
            . ' LEFT JOIN admins a1 ON a1.id = n.acknowledged_by LEFT JOIN admins a2 ON a2.id = n.assigned_admin_id'
            . ' WHERE ' . $where . ' ORDER BY n.id DESC LIMIT ' . (int)$limit, $params);
    }

    public function notification($id)
    {
        return $this->db->one('SELECT n.*, r.code AS region_code, r.name AS region_name FROM city_growth_notifications n'
            . ' JOIN regions r ON r.id = n.region_id WHERE n.id = ?', [(int)$id]);
    }

    /**
     * 알림을 확인하거나, 담당자를 배정하거나, 닫는다.
     * @param string $action acknowledge, assign, close
     * @return string[] 문제 목록
     */
    public function handleNotification(array $admin, $id, $action, $assigneeId = null, $ip = '')
    {
        if (!Auth::can($admin, Auth::GROWTH_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        return $this->db->tx(function () use ($admin, $id, $action, $assigneeId, $ip) {
            $row = $this->db->one('SELECT * FROM city_growth_notifications WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($row === null) {
                return ['없는 알림입니다.'];
            }
            $now = Clock::sql();
            if ($action === 'acknowledge') {
                if ($row['status'] !== 'new') {
                    return ['이미 확인한 알림입니다.'];
                }
                $this->db->exec("UPDATE city_growth_notifications SET status = 'acknowledged', acknowledged_by = ?, acknowledged_at = ? WHERE id = ?",
                    [$admin['id'], $now, $row['id']]);
            } elseif ($action === 'assign') {
                if ($row['status'] === 'new') {
                    return ['먼저 알림을 확인해 주세요.'];
                }
                if ($row['status'] === 'closed') {
                    return ['닫힌 알림입니다.'];
                }
                $assignee = $this->db->one('SELECT id, login FROM admins WHERE id = ? AND is_active = 1', [(int)$assigneeId]);
                if ($assignee === null) {
                    return ['없는 관리자입니다.'];
                }
                $this->db->exec("UPDATE city_growth_notifications SET status = 'assigned', assigned_admin_id = ?, assigned_by = ?, assigned_at = ? WHERE id = ?",
                    [$assignee['id'], $admin['id'], $now, $row['id']]);
            } elseif ($action === 'close') {
                if ($row['status'] === 'closed') {
                    return ['이미 닫힌 알림입니다.'];
                }
                $this->db->exec("UPDATE city_growth_notifications SET status = 'closed', closed_at = ? WHERE id = ?", [$now, $row['id']]);
            } else {
                return ['할 수 없는 일입니다.'];
            }
            (new Auth($this->app))->audit($admin['id'], 'growth.notification.' . $action, 'notification', (string)$row['id'],
                $action === 'assign' ? 'assignee=' . (int)$assigneeId : null, $ip);
            return [];
        });
    }

    // ── 전입·이사 ───────────────────────────────────────────

    /** 전입신고가 끝났을 때 Players 가 부른다 (같은 거래 안에서). */
    public function onRegistered($playerId, $regionId)
    {
        $this->db->exec('INSERT INTO city_residency_events (player_id, region_id, kind, created_at) VALUES (?, ?, ?, ?)',
            [$playerId, $regionId, self::EVENT_REGISTER, Clock::sql()]);
        $this->touch($regionId);
    }

    /** 이 계정이 처음 전입한 때 (유닉스 시각). 초보자 보호 기간의 기준이다. 이사해도 달라지지 않는다. */
    public function firstRegisteredAt($playerId)
    {
        $first = $this->db->val('SELECT MIN(created_at) FROM city_residency_events WHERE player_id = ?', [$playerId]);
        if ($first === null) {
            $first = $this->db->val('SELECT registered_at FROM players WHERE id = ?', [$playerId]);
        }
        return $first !== null ? Clock::parse($first) : null;
    }

    /** 다시 이사할 수 있는 때 (유닉스 시각). 지금 바로 할 수 있으면 0. */
    private function nextMoveAt($playerId)
    {
        $days = $this->app->settings()->growth()['moveCooldownDays'];
        $since = $this->db->val('SELECT registered_at FROM players WHERE id = ?', [$playerId]);
        $at = $since !== null ? Clock::parse($since) + $days * 86400 : 0;
        return $at > Clock::now() ? $at : 0;
    }

    /** GET /residency: 내 전입 상태와 이사 조건. */
    public function residency(array $player)
    {
        $player = $this->app->players()->get($player['id']);
        $config = $this->app->settings()->growth();
        $out = [
            'residency' => $player['resident'] ? 'resident' : 'stranger',
            'regionCode' => $player['regionCode'], 'regionName' => null, 'since' => null,
            'counted' => false, 'countRule' => $config['populationMode'] === Settings::MODE_LINKED ? 'linked' : 'linked_or_proven',
            'notCountedReason' => null,
            'moveEnabled' => $config['moveEnabled'] === 1, 'moveFeeGc' => $config['moveFeeGc'],
            'moveCooldownDays' => $config['moveCooldownDays'], 'canMoveAt' => null,
            'gc' => $this->app->wallets()->balance($player['id']),
        ];
        if (!$player['resident']) {
            return $out;
        }
        $row = $this->db->one('SELECT p.registered_at, r.name FROM players p JOIN regions r ON r.id = p.region_id WHERE p.id = ?', [$player['id']]);
        $out['regionName'] = $row['name'];
        $out['since'] = Clock::isoFromSql($row['registered_at']);
        $out['counted'] = $this->isCounted($player['id']);
        if (!$out['counted']) {
            // 연결된 계정이 아니거나, 활동 확인 조건을 아직 채우지 못했다.
            $out['notCountedReason'] = $config['populationMode'] === Settings::MODE_LINKED ? 'guest_account' : 'not_proven';
        }
        $next = $this->nextMoveAt($player['id']);
        $out['canMoveAt'] = $next > 0 ? Clock::iso($next) : null;
        return $out;
    }

    /**
     * 이사: 주거 도시를 바꾼다. 한 거래로 처리한다.
     * 이전 도시와 새 도시의 전입자 수가 함께 바뀐다. 이전 도시에 쌓은 기여도는 그 도시에 남는다.
     */
    public function move(array $player, $regionCode, $idemKey)
    {
        return $this->app->idempotency()->run($player['id'], $idemKey, 'residency.move', ['region' => (string)$regionCode],
            function () use ($player, $regionCode) {
                $app = $this->app;
                $config = $app->settings()->growth();
                if ($config['moveEnabled'] !== 1) {
                    throw new ApiError(ApiError::LOCKED, '지금은 이사할 수 없어요.', 'move_disabled');
                }
                $player = $app->players()->get($player['id']);
                if (!$player['resident']) {
                    throw new ApiError(ApiError::CONFLICT, '먼저 전입신고를 해 주세요.', 'not_resident');
                }
                $to = $this->db->one('SELECT id, code, name FROM regions WHERE code = ?', [(string)$regionCode]);
                if ($to === null) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
                }
                $fromId = (int)$player['regionId'];
                if ((int)$to['id'] === $fromId) {
                    throw new ApiError(ApiError::CONFLICT, '이미 이 도시에 살고 있어요.', 'same_city');
                }
                $next = $this->nextMoveAt($player['id']);
                if ($next > 0) {
                    throw new ApiError(ApiError::LOCKED, '전입한 지 ' . $config['moveCooldownDays'] . '일이 지나야 이사할 수 있어요.', 'move_cooldown');
                }
                if ($app->coop()->activeSessionOf($player['id']) !== null) {
                    throw new ApiError(ApiError::CONFLICT, '공동 방어전에 참여하는 동안에는 이사할 수 없어요.', 'in_coop_session');
                }
                $fee = $config['moveFeeGc'];
                $gc = $fee > 0
                    ? $app->wallets()->apply($player['id'], -$fee, Wallets::RESIDENCY_MOVE, 'region', (int)$to['id'])
                    : $app->wallets()->balance($player['id']);

                $now = Clock::sql();
                // 이사 기록이 하나도 없는 계정(v9.0 이전에 전입)은 처음 전입한 때를 먼저 적어 둔다. 초보자 보호 기간의 기준이 된다.
                if ((int)$this->db->val('SELECT COUNT(*) FROM city_residency_events WHERE player_id = ?', [$player['id']]) === 0) {
                    $registeredAt = $this->db->val('SELECT registered_at FROM players WHERE id = ?', [$player['id']]);
                    $this->db->exec('INSERT INTO city_residency_events (player_id, region_id, kind, created_at) VALUES (?, ?, ?, ?)',
                        [$player['id'], $fromId, self::EVENT_REGISTER, $registeredAt !== null ? $registeredAt : $now]);
                }
                $this->db->exec('UPDATE players SET region_id = ?, registered_at = ? WHERE id = ?', [$to['id'], $now, $player['id']]);
                $this->db->exec('UPDATE regions SET resident_count = resident_count - 1 WHERE id = ? AND resident_count > 0', [$fromId]);
                $this->db->exec('UPDATE regions SET resident_count = resident_count + 1 WHERE id = ?', [$to['id']]);
                $this->db->exec('INSERT INTO city_residency_events (player_id, region_id, kind, other_region_id, fee_gc, created_at) VALUES (?, ?, ?, ?, 0, ?)',
                    [$player['id'], $fromId, self::EVENT_MOVE_OUT, $to['id'], $now]);
                $this->db->exec('INSERT INTO city_residency_events (player_id, region_id, kind, other_region_id, fee_gc, created_at) VALUES (?, ?, ?, ?, ?, ?)',
                    [$player['id'], $to['id'], self::EVENT_MOVE_IN, $fromId, $fee, $now]);
                // 다른 도시를 지원하러 나가 있었다면 돌아온다 (지원은 "소속 도시의 주변"을 기준으로 받기 때문이다).
                $app->players()->setSupportRequest($player['id'], null);
                $app->players()->touchActive($player['id']);
                $this->touch($fromId);
                $this->touch((int)$to['id']);

                return [
                    'regionCode' => $to['code'], 'regionName' => $to['name'], 'previousRegionCode' => $player['regionCode'],
                    'feeGc' => $fee, 'gc' => $gc,
                    'canMoveAt' => $config['moveCooldownDays'] > 0 ? Clock::iso(Clock::now() + $config['moveCooldownDays'] * 86400) : null,
                ];
            });
    }

    // ── 조회 ────────────────────────────────────────────────

    private function milestoneNames()
    {
        $names = [];
        foreach ($this->milestones() as $milestone) {
            $names[$milestone['id']] = $milestone['name'];
        }
        return $names;
    }

    private function nextThreshold($valid)
    {
        foreach ($this->milestones() as $milestone) {
            if ($milestone['active'] && $milestone['threshold'] > $valid) {
                return $milestone['threshold'];
            }
        }
        return null;
    }

    private function cityOut(array $row, $streetThreshold, array $names)
    {
        $valid = (int)$row['valid_residents'];
        $threshold = $row['modeling_threshold'] !== null ? (int)$row['modeling_threshold'] : $streetThreshold;
        return [
            'regionCode' => $row['code'], 'name' => $row['name'], 'sidoCode' => $row['sido_code'],
            'validResidents' => $valid,
            'stage' => (int)$row['stage'],
            'stageName' => $row['stage_milestone_id'] !== null && isset($names[(int)$row['stage_milestone_id']]) ? $names[(int)$row['stage_milestone_id']] : null,
            'nextThreshold' => $this->nextThreshold($valid),
            'streetThreshold' => $threshold,
            'thresholdReached' => (int)$row['threshold_reached'] === 1,
            'modelingStatus' => $row['modeling_status'] !== null ? $row['modeling_status'] : Modeling::NOT_STARTED,
            'contentVersion' => $row['content_version'],
            'streetOpen' => $row['opened_at'] !== null && $row['content_version'] !== null,
            'openedAt' => Clock::isoFromSql($row['opened_at']),
        ];
    }

    private static function citySql()
    {
        return 'SELECT r.id, r.code, r.name, s.code AS sido_code, COALESCE(ps.valid_residents, 0) AS valid_residents,'
            . ' COALESCE(ps.registered_residents, 0) AS registered_residents, COALESCE(ps.cumulative_residents, 0) AS cumulative_residents,'
            . ' COALESCE(ps.growth_7d, 0) AS growth_7d, COALESCE(ps.growth_30d, 0) AS growth_30d, COALESCE(ps.stage, 0) AS stage,'
            . ' ps.stage_milestone_id, ps.modeling_threshold, COALESCE(ps.threshold_reached, 0) AS threshold_reached, ps.threshold_reached_at,'
            . ' ps.modeling_status, ps.content_version, ps.opened_at, ps.counted_at'
            . ' FROM regions r JOIN sidos s ON s.id = r.sido_id LEFT JOIN city_population_stats ps ON ps.region_id = r.id';
    }

    /** GET /cities/growth: 전국 도시의 전입자 현황과 성장 단계. */
    public function cities()
    {
        $names = $this->milestoneNames();
        $street = $this->streetThreshold();
        $cities = [];
        foreach ($this->db->all(self::citySql() . ' ORDER BY r.id') as $row) {
            $cities[] = $this->cityOut($row, $street, $names);
        }
        $milestones = [];
        foreach ($this->milestones() as $milestone) {
            if ($milestone['active']) {
                $milestones[] = ['code' => $milestone['code'], 'name' => $milestone['name'], 'threshold' => $milestone['threshold'],
                    'description' => $milestone['description'], 'opensStreet' => $milestone['opensStreet']];
            }
        }
        return ['cities' => $cities, 'milestones' => $milestones,
            'countRule' => $this->app->settings()->growth()['populationMode'] === Settings::MODE_LINKED ? 'linked' : 'linked_or_proven'];
    }

    /** GET /cities/{code}/growth: 한 도시의 전입자 현황, 성장 단계, 모델링 상태. */
    public function city($regionCode)
    {
        $row = $this->db->one(self::citySql() . ' WHERE r.code = ?', [(string)$regionCode]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
        }
        $out = $this->cityOut($row, $this->streetThreshold(), $this->milestoneNames());
        $valid = (int)$row['valid_residents'];
        $out['cumulativeResidents'] = (int)$row['cumulative_residents'];
        $out['growth7d'] = (int)$row['growth_7d'];
        $out['growth30d'] = (int)$row['growth_30d'];
        $out['countedAt'] = Clock::isoFromSql($row['counted_at']);
        $out['milestones'] = [];
        foreach ($this->milestones() as $milestone) {
            if ($milestone['active']) {
                $out['milestones'][] = ['code' => $milestone['code'], 'name' => $milestone['name'], 'threshold' => $milestone['threshold'],
                    'reached' => $valid >= $milestone['threshold']];
            }
        }
        $out['trend'] = [];
        foreach ($this->db->all('SELECT date, valid_residents FROM city_population_daily WHERE region_id = ? AND date >= ? ORDER BY date',
            [$row['id'], Clock::koreaDate(Clock::now() - 30 * 86400)]) as $day) {
            $out['trend'][] = ['date' => $day['date'], 'validResidents' => (int)$day['valid_residents']];
        }
        return $out;
    }

    // ── 관리자 화면 ─────────────────────────────────────────

    /** 도시 성장 관리의 목록. @param string|null $filter reached, not_started, in_progress, deployed */
    public function adminCities($filter = null, $sidoCode = null)
    {
        $where = '1 = 1';
        $params = [];
        if ($filter === 'reached') {
            $where .= ' AND ps.threshold_reached = 1';
        } elseif ($filter === 'not_started') {
            $where .= " AND ps.stage > 0 AND ps.modeling_status = 'NOT_STARTED'";
        } elseif ($filter === 'in_progress') {
            $where .= " AND ps.modeling_status IN ('PLANNING', 'MODELING', 'REVIEW', 'READY')";
        } elseif ($filter === 'deployed') {
            $where .= " AND ps.modeling_status = 'DEPLOYED'";
        } elseif ($filter === 'populated') {
            $where .= ' AND ps.registered_residents > 0';
        }
        if ($sidoCode !== null && $sidoCode !== '') {
            $where .= ' AND s.code = ?';
            $params[] = $sidoCode;
        }
        return $this->db->all(self::citySql() . ' WHERE ' . $where . ' ORDER BY COALESCE(ps.valid_residents, 0) DESC, r.id', $params);
    }

    public function adminCity($regionCode)
    {
        return $this->db->one(self::citySql() . ' WHERE r.code = ?', [(string)$regionCode]);
    }

    /** 대시보드의 요약. */
    public function adminSummary()
    {
        $row = $this->db->one(
            'SELECT COALESCE(SUM(valid_residents), 0) AS valid, COALESCE(SUM(registered_residents), 0) AS registered,'
            . ' COALESCE(SUM(growth_7d), 0) AS g7, COALESCE(SUM(growth_30d), 0) AS g30,'
            . ' COALESCE(SUM(threshold_reached = 1), 0) AS reached,'
            . " COALESCE(SUM(stage > 0 AND modeling_status = 'NOT_STARTED'), 0) AS not_started,"
            . " COALESCE(SUM(modeling_status IN ('PLANNING', 'MODELING', 'REVIEW', 'READY')), 0) AS in_progress,"
            . " COALESCE(SUM(modeling_status = 'DEPLOYED'), 0) AS deployed, COALESCE(SUM(stage > 0), 0) AS staged"
            . ' FROM city_population_stats');
        return [
            'validResidents' => (int)$row['valid'], 'registeredResidents' => (int)$row['registered'],
            'growth7d' => (int)$row['g7'], 'growth30d' => (int)$row['g30'], 'reached' => (int)$row['reached'],
            'staged' => (int)$row['staged'], 'notStarted' => (int)$row['not_started'], 'inProgress' => (int)$row['in_progress'],
            'deployed' => (int)$row['deployed'],
            'openMilestoneNotices' => (int)$this->db->val("SELECT COUNT(*) FROM city_growth_notifications WHERE kind = 'milestone' AND status <> 'closed'"),
            'openModelingNotices' => (int)$this->db->val("SELECT COUNT(*) FROM city_growth_notifications WHERE kind = 'modeling' AND status <> 'closed'"),
        ];
    }

    /** 한 도시의 날짜별 전입자 수 (최근 것부터). */
    public function adminTrend($regionId, $days = 60)
    {
        return $this->db->all('SELECT date, valid_residents, registered_residents FROM city_population_daily WHERE region_id = ? AND date >= ?'
            . ' ORDER BY date DESC', [(int)$regionId, Clock::koreaDate(Clock::now() - $days * 86400)]);
    }

    /** 한 도시의 전입·이사 기록 (최근 것부터). 닉네임 대신 계정의 공개 ID 만 보여 준다. */
    public function adminEvents($regionId, $limit = 100)
    {
        return $this->db->all(
            'SELECT e.created_at, e.kind, e.fee_gc, p.public_id, o.name AS other_name FROM city_residency_events e'
            . ' JOIN players p ON p.id = e.player_id LEFT JOIN regions o ON o.id = e.other_region_id'
            . ' WHERE e.region_id = ? ORDER BY e.id DESC LIMIT ' . (int)$limit, [(int)$regionId]);
    }

    /**
     * 전입자 수가 실제 계정 기록과 맞는지 검사한다. 맞으면 null.
     * 한 계정이 두 도시에 집계될 수 없다는 것(유효 전입자의 합 = 조건에 맞는 계정의 수)도 함께 본다.
     */
    public function findInconsistency()
    {
        list($where, $params) = $this->validWhere();
        $actual = (int)$this->db->val('SELECT COUNT(*)' . self::from() . $where, $params);
        $stored = (int)$this->db->val('SELECT COALESCE(SUM(valid_residents), 0) FROM city_population_stats');
        if ($actual !== $stored) {
            return "유효 전입자의 합($stored)이 조건에 맞는 계정의 수($actual)와 다름";
        }
        $bad = $this->db->one(
            'SELECT s.region_id, s.valid_residents, s.cumulative_residents FROM city_population_stats s'
            . ' WHERE s.cumulative_residents < s.valid_residents LIMIT 1');
        return $bad !== null ? '도시 ' . $bad['region_id'] . ' 의 누적 전입자 수가 현재 수보다 적음' : null;
    }
}
