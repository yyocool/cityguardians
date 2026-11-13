<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;
use CG\Domain\Invasion;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;
use CG\Infra\Signer;

/**
 * 협동 타워디펜스 — 게임 서버 쪽 (docs/COOP_DEFENSE_SYSTEM.md).
 *
 * 역할을 둘로 나눈다:
 *   게임 서버 (이 클래스)  누가 어느 판에 들어가 있는가, 입장권, 결과의 반영(보상·경험치·도시 위험도), 기록의 보관.
 *   실시간 서버 (realtime/) 전투 중의 모든 것: 적의 이동, 타워, 웨이브, 관문 내구도, 공동 예산, 전술 핑.
 *
 * 전투 중의 상태는 이 서버를 거치지 않는다 (REST 로 실시간 동기화를 하지 않는다).
 * 실시간 서버는 판이 시작될 때와 끝날 때에만 서명한 요청으로 이 서버를 부른다 (/internal/coop/...).
 *
 * - 한 계정은 동시에 한 판에만 들어간다.
 * - 보상 금액은 방어 구역 정의에서 계산한다. 실시간 서버가 금액을 보내지 않는다.
 * - 결과는 한 번만 반영된다. 같은 결과가 다시 오면 처음 응답을 그대로 돌려준다.
 * - 공동 예산은 그 판 안의 전투 에너지다. GC 가 아니고, 지갑과 이어지지 않는다.
 */
final class Coop
{
    /** 오늘 받은 협동 방어전 보상의 횟수. */
    const DAILY_REWARDS = 'coop_rewards';

    const MAX_BUDGET_LINES = 6000;
    const MAX_PING_LINES = 3000;

    /** @var App */
    private $app;
    /** @var \CG\Infra\Db */
    private $db;
    private $roles = null;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->db = $app->db;
    }

    public static function statuses()
    {
        return ['lobby' => '대기 중', 'running' => '전투 중', 'finished' => '끝남', 'cancelled' => '취소', 'expired' => '시간 초과', 'abandoned' => '중단'];
    }

    public static function pingKinds()
    {
        return ['focus_fire' => '공격 집중', 'defend_here' => '방어 요청', 'build_here' => '타워 설치 제안', 'repair_here' => '수리 요청',
            'danger' => '위험 경고', 'rally' => '집결 위치'];
    }

    /** 공동 예산 장부의 줄 종류와 부호 (+1: 들어옴, -1: 나감, 0: 변화 없음). */
    public static function budgetKinds()
    {
        return ['grant_start' => 1, 'grant_wave' => 1, 'grant_kill' => 1, 'build' => -1, 'upgrade' => -1, 'repair' => -1, 'lost' => 0];
    }

    // ── 준비 상태 ───────────────────────────────────────────

    /** @return Signer */
    public function signer()
    {
        return Signer::fromEnv($this->app->env);
    }

    /** 플레이어에게 알려 줄 실시간 서버의 주소. 설정하지 않았거나 형식이 맞지 않으면 ''. */
    public function realtimeUrl()
    {
        $url = rtrim($this->app->env->get('REALTIME_URL', ''), '/');
        if (preg_match('#^wss://[A-Za-z0-9.\-]+(:\d{1,5})?(/[A-Za-z0-9._~\-/]*)?$#', $url)) {
            return $url;
        }
        // 암호화하지 않은 연결은 내 PC 에서 시험할 때만.
        if (preg_match('#^ws://(127\.0\.0\.1|localhost)(:\d{1,5})?(/[A-Za-z0-9._~\-/]*)?$#', $url)) {
            return $url;
        }
        return '';
    }

    /** 협동 방어전을 열 수 없는 이유. 열 수 있으면 null. */
    public function unavailableReason()
    {
        if ($this->app->settings()->coop()['enabled'] !== 1) {
            return 'coop_disabled';
        }
        if (!$this->signer()->ready() || $this->realtimeUrl() === '') {
            return 'realtime_not_configured';
        }
        if (!$this->app->defs()->modeOpen(Invasion::MODE_DEFENSE)) {
            return 'mode_closed';
        }
        return null;
    }

    private function requireAvailable()
    {
        if ($this->unavailableReason() !== null) {
            throw new ApiError(ApiError::FEATURE_DISABLED, '협동 방어전은 아직 열리지 않았어요.', $this->unavailableReason());
        }
    }

    // ── 역할 ────────────────────────────────────────────────

    /** @return array[] 고를 수 있는 역할 */
    public function roles()
    {
        if ($this->roles === null) {
            $this->roles = [];
            foreach ($this->db->all('SELECT * FROM defense_team_roles WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $this->roles[$row['code']] = [
                    'code' => $row['code'], 'name' => $row['name'], 'description' => $row['description'],
                    'buildDiscountPercent' => (int)$row['build_discount_percent'], 'repairBonusPercent' => (int)$row['repair_bonus_percent'],
                    'attackBonusPercent' => (int)$row['attack_bonus_percent'], 'pingCooldownPercent' => (int)$row['ping_cooldown_percent'],
                    'maxPerSession' => (int)$row['max_per_session'],
                ];
            }
        }
        return $this->roles;
    }

    /** 역할을 고를 수 있는지 확인한다. 역할은 고르지 않아도 된다 (null). */
    private function checkRole($sessionId, $playerId, $roleCode)
    {
        if ($roleCode === null || $roleCode === '') {
            return null;
        }
        $roles = $this->roles();
        if (!isset($roles[$roleCode])) {
            throw new ApiError(ApiError::INVALID_REQUEST, '없는 역할이에요.');
        }
        $limit = $roles[$roleCode]['maxPerSession'];
        if ($limit > 0 && $sessionId !== null) {
            $taken = (int)$this->db->val(
                "SELECT COUNT(*) FROM defense_session_members WHERE session_id = ? AND role_code = ? AND status = 'joined' AND player_id <> ?",
                [$sessionId, $roleCode, $playerId]);
            if ($taken >= $limit) {
                throw new ApiError(ApiError::CONFLICT, '그 역할은 이미 다른 팀원이 맡았어요.', 'role_taken');
            }
        }
        return $roleCode;
    }

    // ── 조회 ────────────────────────────────────────────────

    /** 이 계정이 지금 들어가 있는 판 (대기 중이거나 전투 중). 없으면 null. */
    public function activeSessionOf($playerId)
    {
        return $this->db->one(
            'SELECT s.* FROM defense_session_members m JOIN defense_sessions s ON s.id = m.session_id'
            . " WHERE m.player_id = ? AND m.status = 'joined' AND s.status IN ('lobby', 'running') ORDER BY s.id DESC LIMIT 1", [$playerId]);
    }

    private function sessionRow($publicId, $lock = false)
    {
        if (!is_string($publicId) || !preg_match('/^d_[a-z0-9]{14}$/', $publicId)) {
            return null;
        }
        return $this->db->one('SELECT * FROM defense_sessions WHERE public_id = ?' . ($lock ? ' FOR UPDATE' : ''), [$publicId]);
    }

    private function zoneById($zoneId)
    {
        foreach ($this->app->defs()->zones() as $def) {
            if ($def['id'] === (int)$zoneId) {
                return $def;
            }
        }
        return null;
    }

    private function members($sessionId)
    {
        return $this->db->all(
            'SELECT m.*, p.public_id AS player_public_id, p.nickname, COALESCE(c.level, 1) AS level FROM defense_session_members m'
            . ' JOIN players p ON p.id = m.player_id LEFT JOIN characters c ON c.player_id = m.player_id WHERE m.session_id = ? ORDER BY m.id',
            [$sessionId]);
    }

    private function view(array $session, array $player)
    {
        $zone = $this->zoneById($session['zone_id']);
        $region = $this->db->one('SELECT code, name FROM regions WHERE id = ?', [$session['region_id']]);
        $members = [];
        $mine = false;
        $joined = 0;
        foreach ($this->members($session['id']) as $row) {
            $isMe = (int)$row['player_id'] === (int)$player['id'];
            if ($row['status'] === 'joined') {
                $joined++;
                $mine = $mine || $isMe;
            }
            $members[] = [
                'playerId' => $row['player_public_id'], 'nickname' => $row['nickname'], 'level' => (int)$row['level'],
                'roleCode' => $row['role_code'], 'status' => $row['status'],
                'isHost' => (int)$row['player_id'] === (int)$session['host_player_id'], 'isMe' => $isMe,
            ];
        }
        $statuses = self::statuses();
        return [
            'id' => $session['public_id'], 'status' => $session['status'], 'statusName' => $statuses[$session['status']],
            'regionCode' => $region['code'], 'regionName' => $region['name'],
            'zoneCode' => $zone !== null ? $zone['code'] : null, 'zoneName' => $zone !== null ? $zone['name'] : '',
            'maxPlayers' => (int)$session['max_players'], 'players' => $joined, 'members' => $members,
            'mine' => $mine, 'isHost' => (int)$session['host_player_id'] === (int)$player['id'],
            'createdAt' => Clock::isoFromSql($session['created_at']), 'expiresAt' => Clock::isoFromSql($session['expires_at']),
            'startedAt' => $session['started_at'] !== null ? Clock::isoFromSql($session['started_at']) : null,
        ];
    }

    /** GET /coop/config: 협동 방어전을 할 수 있는지, 역할, 핑, 오늘 남은 보상 횟수. */
    public function config(array $player)
    {
        $config = $this->app->settings()->coop();
        $ping = $this->app->settings()->ping();
        $reason = $this->unavailableReason();
        $kinds = [];
        foreach (self::pingKinds() as $code => $name) {
            $kinds[] = ['kind' => $code, 'name' => $name];
        }
        $active = $this->activeSessionOf($player['id']);
        $used = $this->app->daily()->get($player['id'], self::DAILY_REWARDS);
        return [
            'available' => $reason === null, 'unavailableReason' => $reason,
            'defaultPlayers' => $config['defaultPlayers'], 'maxPlayers' => $config['maxPlayers'], 'minPlayersToStart' => $config['minPlayersToStart'],
            'allowLateJoin' => $config['allowLateJoin'] === 1,
            'dailyRewardLimit' => $config['dailyRewardLimit'], 'rewardsLeftToday' => max(0, $config['dailyRewardLimit'] - $used),
            'roles' => array_values($this->roles()), 'roleRequired' => false,
            'ping' => ['enabled' => $ping['enabled'] === 1, 'kinds' => $kinds, 'burst' => $ping['burst'], 'refillSeconds' => $ping['refillSeconds'],
                'lifetimeSeconds' => $ping['lifetimeSeconds'], 'routeOverlay' => $ping['routeOverlay'] === 1, 'rangeOverlay' => $ping['rangeOverlay'] === 1,
                'blocked' => $this->isPingBlocked($player['id'])],
            'activeSession' => $active !== null ? $this->view($active, $player) : null,
            'regionCode' => $this->app->city()->combatRegionCode($player),
        ];
    }

    /** GET /coop/sessions: 지금 들어갈 수 있는 판 (내가 지키는 도시의 것). */
    public function sessions(array $player)
    {
        $this->expireDue();
        $regionCode = $this->app->city()->combatRegionCode($player);
        $items = [];
        if ($regionCode !== null) {
            $statuses = $this->app->settings()->coop()['allowLateJoin'] === 1 ? "'lobby', 'running'" : "'lobby'";
            foreach ($this->db->all(
                'SELECT s.* FROM defense_sessions s JOIN regions r ON r.id = s.region_id'
                . " WHERE r.code = ? AND s.status IN ($statuses) ORDER BY (s.status = 'lobby') DESC, s.id DESC LIMIT 50", [$regionCode]
            ) as $row) {
                $items[] = $this->view($row, $player);
            }
        }
        return ['regionCode' => $regionCode, 'sessions' => $items];
    }

    /** GET /coop/sessions/{id}: 팀원 목록과 상태. 그 도시를 지키는 사람과 팀원만 볼 수 있다. */
    public function session(array $player, $publicId)
    {
        $session = $this->sessionRow($publicId);
        if ($session === null || !$this->canSee($session, $player)) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
        }
        return ['session' => $this->view($session, $player)];
    }

    private function memberRow($sessionId, $playerId)
    {
        return $this->db->one('SELECT * FROM defense_session_members WHERE session_id = ? AND player_id = ?', [$sessionId, $playerId]);
    }

    private function canSee(array $session, array $player)
    {
        if ($this->memberRow($session['id'], $player['id']) !== null) {
            return true;
        }
        $regionCode = $this->app->city()->combatRegionCode($player);
        return $regionCode !== null && $regionCode === $this->db->val('SELECT code FROM regions WHERE id = ?', [$session['region_id']]);
    }

    // ── 방어전 생성과 참여 ──────────────────────────────────

    /** POST /coop/sessions: 공동 방어전을 연다. 연 사람이 방장이 된다. */
    public function create(array $player, $zoneCode, $maxPlayers, $roleCode, $idemKey)
    {
        $request = ['zone' => (string)$zoneCode, 'max' => $maxPlayers, 'role' => (string)$roleCode];
        return $this->app->idempotency()->run($player['id'], $idemKey, 'coop.create', $request,
            function () use ($player, $zoneCode, $maxPlayers, $roleCode) {
                $this->requireAvailable();
                $config = $this->app->settings()->coop();
                $player = $this->app->players()->get($player['id']);
                if (!$player['resident']) {
                    throw new ApiError(ApiError::CONFLICT, '전입신고를 한 주민만 공동 방어전에 나설 수 있어요.', 'not_resident');
                }
                $zone = $this->app->defs()->findZone((string)$zoneCode);
                if ($zone === null) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
                }
                if ($player['level'] < $zone['unlockLevel']) {
                    throw new ApiError(ApiError::LOCKED, '레벨 ' . $zone['unlockLevel'] . '부터 나설 수 있어요.', 'level_too_low');
                }
                if ($this->activeSessionOf($player['id']) !== null) {
                    throw new ApiError(ApiError::CONFLICT, '이미 다른 공동 방어전에 들어가 있어요.', 'in_coop_session');
                }
                $max = $maxPlayers === null ? $config['defaultPlayers'] : (int)$maxPlayers;
                if ($max < 2 || $max > $config['maxPlayers']) {
                    throw new ApiError(ApiError::INVALID_REQUEST, '정원은 2 ~ ' . $config['maxPlayers'] . '명 사이로 정해 주세요.');
                }
                $role = $this->checkRole(null, $player['id'], $roleCode);
                $regionCode = $this->app->city()->combatRegionCode($player);
                $regionId = (int)$this->db->val('SELECT id FROM regions WHERE code = ?', [$regionCode]);
                $invasion = $this->app->city()->countingInvasion($regionCode, Invasion::MODE_DEFENSE);
                $now = Clock::now();
                $publicId = Ids::publicId('d_');
                $id = $this->db->insert(
                    'INSERT INTO defense_sessions (public_id, region_id, zone_id, host_player_id, invasion_id, status, max_players, created_at,'
                    . " expires_at, updated_at) VALUES (?, ?, ?, ?, ?, 'lobby', ?, ?, ?, ?)",
                    [$publicId, $regionId, $zone['id'], $player['id'], $invasion !== null ? $invasion['dbId'] : null, $max, Clock::sql($now),
                        Clock::sql($now + $config['lobbyMinutes'] * 60), Clock::sql($now)]
                );
                $this->db->exec(
                    "INSERT INTO defense_session_members (session_id, player_id, role_code, status, joined_at) VALUES (?, ?, ?, 'joined', ?)",
                    [$id, $player['id'], $role, Clock::sql($now)]);
                return ['session' => $this->view($this->sessionRow($publicId), $player)];
            });
    }

    /** POST /coop/sessions/{id}/join: 판에 들어간다. 나갔던 사람은 다시 들어올 수 있다. */
    public function join(array $player, $publicId, $roleCode, $idemKey)
    {
        return $this->app->idempotency()->run($player['id'], $idemKey, 'coop.join', ['session' => (string)$publicId, 'role' => (string)$roleCode],
            function () use ($player, $publicId, $roleCode) {
                $this->requireAvailable();
                $config = $this->app->settings()->coop();
                $player = $this->app->players()->get($player['id']);
                if (!$player['resident']) {
                    throw new ApiError(ApiError::CONFLICT, '전입신고를 한 주민만 공동 방어전에 나설 수 있어요.', 'not_resident');
                }
                // 판의 행을 잠근다: 동시에 여럿이 들어와도 정원을 넘지 않는다.
                $session = $this->sessionRow($publicId, true);
                if ($session === null) {
                    throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
                }
                $joinable = $session['status'] === 'lobby' || ($session['status'] === 'running' && $config['allowLateJoin'] === 1);
                if (!$joinable || Clock::now() >= Clock::parse($session['expires_at'])) {
                    throw new ApiError(ApiError::CONFLICT, '지금은 들어갈 수 없는 방어전이에요.', 'not_joinable');
                }
                $regionCode = $this->app->city()->combatRegionCode($player);
                if ($regionCode === null || $regionCode !== $this->db->val('SELECT code FROM regions WHERE id = ?', [$session['region_id']])) {
                    throw new ApiError(ApiError::FORBIDDEN, '다른 도시의 방어전이에요. 그 도시에 지원을 나가면 함께할 수 있어요.', 'other_city');
                }
                $zone = $this->zoneById($session['zone_id']);
                if ($zone === null || $player['level'] < $zone['unlockLevel']) {
                    throw new ApiError(ApiError::LOCKED, '아직 나설 수 없는 방어전이에요.', 'level_too_low');
                }
                $member = $this->memberRow($session['id'], $player['id']);
                if ($member === null || $member['status'] !== 'joined') {
                    $active = $this->activeSessionOf($player['id']);
                    if ($active !== null) {
                        throw new ApiError(ApiError::CONFLICT, '이미 다른 공동 방어전에 들어가 있어요.', 'in_coop_session');
                    }
                    $joined = (int)$this->db->val("SELECT COUNT(*) FROM defense_session_members WHERE session_id = ? AND status = 'joined'",
                        [$session['id']]);
                    if ($joined >= (int)$session['max_players']) {
                        throw new ApiError(ApiError::CONFLICT, '정원이 다 찼어요.', 'session_full');
                    }
                    $role = $this->checkRole($session['id'], $player['id'], $roleCode);
                    if ($member === null) {
                        $this->db->exec(
                            "INSERT INTO defense_session_members (session_id, player_id, role_code, status, joined_at) VALUES (?, ?, ?, 'joined', ?)",
                            [$session['id'], $player['id'], $role, Clock::sql()]);
                    } else {
                        $this->db->exec("UPDATE defense_session_members SET status = 'joined', left_at = NULL, role_code = ? WHERE id = ?",
                            [$role, $member['id']]);
                    }
                    $this->db->exec('UPDATE defense_sessions SET updated_at = ? WHERE id = ?', [Clock::sql(), $session['id']]);
                }
                return ['session' => $this->view($this->sessionRow($publicId), $player)];
            });
    }

    /** POST /coop/sessions/{id}/leave: 판에서 나간다. 전투 중이어도 나갈 수 있고, 전투는 남은 사람들로 이어진다. */
    public function leave(array $player, $publicId)
    {
        return $this->db->tx(function () use ($player, $publicId) {
            $this->app->wallets()->lock($player['id']);
            $session = $this->sessionRow($publicId, true);
            if ($session === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
            }
            $member = $this->memberRow($session['id'], $player['id']);
            if ($member === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
            }
            if ($member['status'] === 'joined' && ($session['status'] === 'lobby' || $session['status'] === 'running')) {
                $now = Clock::sql();
                $this->db->exec("UPDATE defense_session_members SET status = 'left', left_at = ? WHERE id = ?", [$now, $member['id']]);
                if ($session['status'] === 'lobby') {
                    $next = $this->db->val(
                        "SELECT player_id FROM defense_session_members WHERE session_id = ? AND status = 'joined' ORDER BY id LIMIT 1", [$session['id']]);
                    if ($next === null) {
                        // 아무도 남지 않았다.
                        $this->db->exec("UPDATE defense_sessions SET status = 'cancelled', close_reason = 'empty', ended_at = ?, updated_at = ? WHERE id = ?",
                            [$now, $now, $session['id']]);
                    } elseif ((int)$session['host_player_id'] === (int)$player['id']) {
                        // 방장이 나가면 가장 먼저 들어온 사람이 방장이 된다.
                        $this->db->exec('UPDATE defense_sessions SET host_player_id = ?, updated_at = ? WHERE id = ?', [$next, $now, $session['id']]);
                    }
                }
            }
            return ['session' => $this->view($this->sessionRow($publicId), $player)];
        });
    }

    /**
     * PUT /coop/sessions/{id}/role: 역할을 고르거나 내려놓는다 (roleCode 를 비우면 역할 없음). 대기 중에만 여기서 바꾼다.
     * 전투 중의 역할 변경은 실시간 서버가 처리하고, 끝날 때의 역할이 기록으로 넘어온다.
     */
    public function setRole(array $player, $publicId, $roleCode)
    {
        return $this->db->tx(function () use ($player, $publicId, $roleCode) {
            $this->app->wallets()->lock($player['id']);
            $session = $this->sessionRow($publicId, true);
            $member = $session !== null ? $this->memberRow($session['id'], $player['id']) : null;
            if ($member === null || $member['status'] !== 'joined') {
                throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
            }
            if ($session['status'] !== 'lobby') {
                throw new ApiError(ApiError::CONFLICT, '전투가 시작된 뒤에는 전투 화면에서 역할을 바꿔요.', 'role_locked');
            }
            $role = $this->checkRole($session['id'], $player['id'], $roleCode);
            $this->db->exec('UPDATE defense_session_members SET role_code = ? WHERE id = ?', [$role, $member['id']]);
            return ['session' => $this->view($this->sessionRow($publicId), $player)];
        });
    }

    // ── 입장권 ──────────────────────────────────────────────

    /** POST /coop/sessions/{id}/ticket: 실시간 서버에 낼 입장권. 팀원에게만 준다. 수명이 짧다. */
    public function ticket(array $player, $publicId)
    {
        $this->requireAvailable();
        $session = $this->sessionRow($publicId);
        $member = $session !== null ? $this->memberRow($session['id'], $player['id']) : null;
        if ($member === null || $member['status'] !== 'joined') {
            throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
        }
        if (($session['status'] !== 'lobby' && $session['status'] !== 'running') || Clock::now() >= Clock::parse($session['expires_at'])) {
            throw new ApiError(ApiError::CONFLICT, '이미 끝난 방어전이에요.', 'session_closed');
        }
        $player = $this->app->players()->get($player['id']);
        $expires = Clock::now() + $this->app->settings()->coop()['ticketSeconds'];
        // 입장권에는 공개 ID 와 닉네임만 넣는다. 내부 번호나 개인정보는 넣지 않는다.
        $ticket = $this->signer()->ticket([
            'v' => 1, 'sid' => $session['public_id'], 'pid' => $player['publicId'], 'name' => (string)$player['nickname'],
            'role' => $member['role_code'], 'level' => (int)$player['level'],
            'host' => (int)$session['host_player_id'] === (int)$player['id'],
            'structs' => $this->app->defense()->usableStructures($player['id']),
            'ping_blocked' => $this->isPingBlocked($player['id']),
            'exp' => $expires, 'nonce' => Ids::token(12),
        ]);
        return ['ticket' => $ticket, 'url' => $this->realtimeUrl(), 'sessionId' => $session['public_id'], 'expiresAt' => Clock::iso($expires)];
    }

    // ── 실시간 서버가 부르는 것 (/internal/coop/...) ────────

    private function gateHp(array $zone, $regionId)
    {
        $levels = 0;
        $funded = [];
        foreach ($this->db->all('SELECT structure_id, funded_gc FROM city_defense_structures WHERE region_id = ?', [$regionId]) as $row) {
            $funded[(int)$row['structure_id']] = (int)$row['funded_gc'];
        }
        foreach ($this->app->defs()->structures() as $def) {
            if ($def['ownership'] === 'city' && isset($funded[$def['id']])) {
                $levels += Defs::cityStructureLevel($def, $funded[$def['id']]);
            }
        }
        $percent = $this->app->settings()->coop()['cityStructureGatePercent'];
        // 도시가 함께 세운 방어시설이 관문을 튼튼하게 한다. 올림.
        return [intdiv($zone['baseHp'] * (100 + $levels * $percent) + 99, 100), $levels];
    }

    /** 판을 여는 데 필요한 모든 것: 구역, 적, 타워, 역할, 규칙. 실시간 서버가 방을 만들 때 한 번 읽는다. */
    public function internalConfig($publicId)
    {
        $session = $this->sessionRow($publicId);
        if ($session === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 방어전입니다.');
        }
        $zone = $this->zoneById($session['zone_id']);
        if ($zone === null) {
            throw new ApiError(ApiError::CONFLICT, '닫힌 방어 구역입니다.', 'zone_closed');
        }
        $config = $this->app->settings()->coop();
        $region = $this->db->one('SELECT code, name FROM regions WHERE id = ?', [$session['region_id']]);
        list($gateHp, $cityLevels) = $this->gateHp($zone, (int)$session['region_id']);
        $structures = [];
        foreach ($this->app->defs()->structures() as $def) {
            if (!$def['comingSoon'] && $def['ownership'] !== 'city') {
                unset($def['id']);
                $structures[] = $def;
            }
        }
        $members = [];
        foreach ($this->members($session['id']) as $row) {
            if ($row['status'] === 'joined') {
                $members[] = ['pid' => $row['player_public_id'], 'name' => $row['nickname'], 'role' => $row['role_code'], 'level' => (int)$row['level']];
            }
        }
        unset($zone['id']);
        $host = $this->db->val('SELECT public_id FROM players WHERE id = ?', [$session['host_player_id']]);
        return [
            'session' => ['id' => $session['public_id'], 'status' => $session['status'], 'regionCode' => $region['code'], 'regionName' => $region['name'],
                'maxPlayers' => (int)$session['max_players'], 'hostPid' => $host,
                'expiresAt' => Clock::isoFromSql($session['expires_at']), 'expiresInSeconds' => max(0, Clock::parse($session['expires_at']) - Clock::now())],
            'zone' => $zone, 'gateHp' => $gateHp, 'cityStructureLevels' => $cityLevels,
            'enemies' => array_values($this->app->defs()->enemies()), 'structures' => $structures,
            'roles' => array_values($this->roles()),
            'rules' => $config, 'ping' => $this->app->settings()->ping(), 'pingKinds' => array_keys(self::pingKinds()),
            'members' => $members,
        ];
    }

    /**
     * 전투를 시작한다. 방장이 실시간 서버에 "시작"을 보내면 실시간 서버가 이것을 부른다. 여러 번 불러도 한 번만 시작된다.
     * @param array $body by(시작을 누른 사람의 공개 ID), players(지금 연결된 사람들의 공개 ID)
     */
    public function internalStart($publicId, array $body)
    {
        $by = isset($body['by']) && is_string($body['by']) ? $body['by'] : '';
        $connected = isset($body['players']) && is_array($body['players']) ? array_filter($body['players'], 'is_string') : [];
        return $this->db->tx(function () use ($publicId, $by, $connected) {
            $session = $this->sessionRow($publicId, true);
            if ($session === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 방어전입니다.');
            }
            if ($session['status'] === 'running') {
                return ['status' => 'running', 'playersAtStart' => (int)$session['players_at_start'],
                    'startedAt' => Clock::isoFromSql($session['started_at']), 'duplicate' => true];
            }
            if ($session['status'] !== 'lobby' || Clock::now() >= Clock::parse($session['expires_at'])) {
                throw new ApiError(ApiError::CONFLICT, '시작할 수 없는 방어전입니다.', 'session_closed');
            }
            if ($by !== $this->db->val('SELECT public_id FROM players WHERE id = ?', [$session['host_player_id']])) {
                throw new ApiError(ApiError::FORBIDDEN, '방장만 시작할 수 있습니다.', 'not_host');
            }
            $config = $this->app->settings()->coop();
            $present = 0;
            foreach ($this->members($session['id']) as $row) {
                if ($row['status'] === 'joined' && in_array($row['player_public_id'], $connected, true)) {
                    $present++;
                }
            }
            if ($present < $config['minPlayersToStart']) {
                throw new ApiError(ApiError::CONFLICT, '시작하려면 ' . $config['minPlayersToStart'] . '명이 모여야 합니다.', 'not_enough_players');
            }
            $now = Clock::now();
            $this->db->exec(
                "UPDATE defense_sessions SET status = 'running', started_at = ?, players_at_start = ?, expires_at = ?, updated_at = ? WHERE id = ?",
                [Clock::sql($now), $present, Clock::sql($now + $config['sessionMaxMinutes'] * 60), Clock::sql($now), $session['id']]);
            return ['status' => 'running', 'playersAtStart' => $present, 'startedAt' => Clock::iso($now), 'duplicate' => false];
        });
    }

    private static function count($row, $key, $max = 100000000)
    {
        return isset($row[$key]) && is_int($row[$key]) ? max(0, min($max, $row[$key])) : 0;
    }

    /**
     * 공동 예산 장부를 검사한다. 앞뒤가 맞으면 null, 아니면 이유.
     * 줄 번호가 1부터 이어지고, 잔액이 이어지고, 음수가 된 적이 없고, 이미 타워가 있는 자리에 다시 세운 적이 없어야 한다.
     * @param array $players [공개 ID => 내부 번호]
     */
    private static function checkBudget(array $lines, array $players)
    {
        $kinds = self::budgetKinds();
        $balance = 0;
        $occupied = [];
        foreach (array_values($lines) as $index => $line) {
            if (!is_array($line) || !isset($line['seq'], $line['kind'], $line['delta'], $line['balanceAfter'])
                || !is_int($line['seq']) || !is_int($line['delta']) || !is_int($line['balanceAfter']) || !is_string($line['kind'])) {
                return 'budget_format';
            }
            if ($line['seq'] !== $index + 1) {
                return 'budget_sequence';
            }
            if (!isset($kinds[$line['kind']])) {
                return 'budget_kind';
            }
            $sign = $kinds[$line['kind']];
            if (($sign > 0 && $line['delta'] < 0) || ($sign < 0 && $line['delta'] > 0) || ($sign === 0 && $line['delta'] !== 0)) {
                return 'budget_sign';
            }
            $balance += $line['delta'];
            if ($balance < 0) {
                return 'budget_negative';
            }
            if ($balance !== $line['balanceAfter']) {
                return 'budget_balance';
            }
            $spender = isset($line['pid']) && is_string($line['pid']) ? $line['pid'] : null;
            if ($sign < 0 && ($spender === null || !isset($players[$spender]))) {
                return 'budget_spender';
            }
            $slot = isset($line['slot']) && is_int($line['slot']) ? $line['slot'] : null;
            if ($line['kind'] === 'build') {
                if ($slot === null || isset($occupied[$slot])) {
                    return 'budget_duplicate_slot';
                }
                $occupied[$slot] = true;
            } elseif ($line['kind'] === 'lost' && $slot !== null) {
                unset($occupied[$slot]);
            } elseif ($line['kind'] === 'upgrade' && ($slot === null || !isset($occupied[$slot]))) {
                return 'budget_upgrade_empty_slot';
            }
        }
        return null;
    }

    /**
     * 전투 결과를 반영한다. 실시간 서버가 판이 끝났을 때 부른다.
     *
     * 서명으로 실시간 서버임을 확인한 뒤에도 내용을 다시 검사한다: 웨이브 수, 관문 내구도, 걸린 시간(이 서버가 잰 것),
     * 공동 예산 장부의 앞뒤. 맞지 않으면 판 전체를 인정하지 않고 아무에게도 보상을 주지 않는다.
     * 보상 금액은 방어 구역 정의에서 계산한다. 같은 결과가 다시 오면 처음 응답을 그대로 돌려준다.
     */
    public function internalResult($publicId, array $body)
    {
        $found = $this->sessionRow($publicId);
        if ($found === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 방어전입니다.');
        }
        // 잠그는 순서는 늘 지갑(계정 번호 순) → 판. 플레이어 쪽 요청과 같은 순서다.
        $memberIds = [];
        foreach ($this->db->all('SELECT player_id FROM defense_session_members WHERE session_id = ? ORDER BY player_id', [$found['id']]) as $row) {
            $memberIds[] = (int)$row['player_id'];
        }
        return $this->db->tx(function () use ($publicId, $body, $memberIds) {
            $app = $this->app;
            foreach ($memberIds as $playerId) {
                $app->wallets()->lock($playerId);
            }
            $session = $this->sessionRow($publicId, true);
            if ($session['status'] === 'finished') {
                $response = json_decode((string)$session['response'], true);
                $response['duplicate'] = true;
                return $response;
            }
            if ($session['status'] !== 'running') {
                throw new ApiError(ApiError::CONFLICT, '이미 닫힌 방어전입니다.', 'session_closed');
            }
            $zone = $this->zoneById($session['zone_id']);
            $config = $app->settings()->coop();
            $members = [];
            $byPublic = [];
            foreach ($this->members($session['id']) as $row) {
                $members[(int)$row['player_id']] = $row;
                $byPublic[$row['player_public_id']] = (int)$row['player_id'];
            }

            // ── 검증 ──
            $result = isset($body['result']) && ($body['result'] === 'win' || $body['result'] === 'lose') ? $body['result'] : null;
            $waves = self::count($body, 'wavesCleared', 1000);
            $gateLeft = self::count($body, 'gateHpLeft');
            $duration = self::count($body, 'durationSeconds', 86400);
            $closeReason = isset($body['closeReason']) && is_string($body['closeReason']) && preg_match('/^[a-z_]{1,32}$/', $body['closeReason'])
                ? $body['closeReason'] : 'completed';
            $budget = isset($body['budget']) && is_array($body['budget']) ? array_values($body['budget']) : [];
            $pings = isset($body['pings']) && is_array($body['pings']) ? array_values($body['pings']) : [];
            $elapsed = Clock::now() - Clock::parse($session['started_at']);
            $reject = null;
            if ($zone === null) {
                $reject = 'zone_closed';
            } elseif ($result === null) {
                $reject = 'bad_result';
            } else {
                $waveCount = count($zone['waves']);
                list($gateMax) = $this->gateHp($zone, (int)$session['region_id']);
                if ($waves > $waveCount || ($result === 'win' && ($waves !== $waveCount || $gateLeft < 1))) {
                    $reject = 'bad_waves';
                } elseif ($gateLeft > $gateMax || ($result === 'lose' && $closeReason === 'gate_destroyed' && $gateLeft !== 0)) {
                    $reject = 'bad_gate_hp';
                } elseif ($elapsed < $waves * $zone['minSecondsPerWave'] || $duration > $elapsed + 5) {
                    // 걸린 시간은 이 서버가 잰 것을 기준으로 본다.
                    $reject = 'too_fast';
                } elseif (count($budget) > self::MAX_BUDGET_LINES || count($pings) > self::MAX_PING_LINES) {
                    $reject = 'too_many_lines';
                } else {
                    $reject = self::checkBudget($budget, $byPublic);
                }
            }
            $accepted = $reject === null;
            $win = $accepted && $result === 'win';

            // ── 팀원 기록 ──
            $stats = [];
            foreach (isset($body['members']) && is_array($body['members']) ? $body['members'] : [] as $row) {
                if (is_array($row) && isset($row['pid']) && is_string($row['pid']) && isset($byPublic[$row['pid']])) {
                    $stats[$byPublic[$row['pid']]] = $row;
                }
            }
            $roles = $this->roles();
            $now = Clock::sql();
            $rewardGc = $accepted ? ($win ? $zone['winGc'] : $zone['loseGcPerWave'] * $waves) : 0;
            $rewardXp = $win ? $zone['winXp'] : 0;
            $rewarded = [];
            foreach ($members as $playerId => $member) {
                $stat = isset($stats[$playerId]) ? $stats[$playerId] : [];
                $connected = min($duration, self::count($stat, 'connectedSeconds', 86400));
                $role = isset($stat['role']) && is_string($stat['role']) && isset($roles[$stat['role']]) ? $stat['role'] : $member['role_code'];
                $note = null;
                $eligible = false;
                if (!$accepted) {
                    $note = 'rejected';
                } elseif ($duration <= 0 || $connected * 100 < $duration * $config['minParticipationPercent']) {
                    $note = 'low_participation';
                } elseif ($app->daily()->lock($playerId, self::DAILY_REWARDS) >= $config['dailyRewardLimit']) {
                    $note = 'daily_limit';
                } else {
                    $eligible = true;
                }
                $gc = 0;
                $xp = 0;
                if ($eligible) {
                    $app->daily()->add($playerId, self::DAILY_REWARDS, 1);
                    $gc = $rewardGc;
                    $xp = $rewardXp;
                    if ($gc > 0) {
                        // 지갑 원장에는 혼자 하는 방어전과 같은 사유로 남는다. 근거만 다르다 (defense_session).
                        $app->wallets()->apply($playerId, $gc, Wallets::BATTLE_REWARD, 'defense_session', (int)$session['id']);
                    }
                    if ($xp > 0) {
                        $app->players()->addXp($playerId, $xp);
                    }
                    $rewarded[] = $playerId;
                }
                $this->db->exec(
                    'UPDATE defense_session_members SET role_code = ?, connected_seconds = ?, kills = ?, towers_built = ?, towers_upgraded = ?, repairs = ?,'
                    . ' damage_dealt = ?, pings_sent = ?, pings_rejected = ?, eligible = ?, reward_gc = ?, reward_xp = ?, reward_note = ?, rewarded_at = ?'
                    . ' WHERE id = ?',
                    [$role, $connected, self::count($stat, 'kills'), self::count($stat, 'towersBuilt'), self::count($stat, 'towersUpgraded'),
                        self::count($stat, 'repairs'), self::count($stat, 'damageDealt', 2000000000), self::count($stat, 'pingsSent'),
                        self::count($stat, 'pingsRejected'), $eligible ? 1 : 0, $gc, $xp, $note, $eligible ? $now : null, $member['id']]
                );
            }
            // 도시 반영은 보상을 모두 준 뒤에 한다 (잠그는 순서: 지갑 → 도시 재정 → 도시 상태).
            // 이기면 위험도가 내려간다. 져도 올라가지 않는다. 함께 이긴 사람마다 한 번의 승리로 센다.
            foreach ($rewarded as $playerId) {
                $app->city()->applyCombat($app->players()->get($playerId), Invasion::MODE_DEFENSE, $win, $session['public_id'],
                    $win ? $zone['winDefensePoints'] : 0);
            }

            // ── 기록 보관: 공동 예산 장부와 전술 핑 ──
            $granted = 0;
            $spent = 0;
            $left = 0;
            if ($accepted) {
                $rows = [];
                foreach ($budget as $line) {
                    $granted += max(0, $line['delta']);
                    $spent += max(0, -$line['delta']);
                    $left = $line['balanceAfter'];
                    $spender = isset($line['pid']) && is_string($line['pid']) && isset($byPublic[$line['pid']]) ? $byPublic[$line['pid']] : null;
                    $code = isset($line['code']) && is_string($line['code']) && preg_match('/^[a-z0-9_]{1,40}$/', $line['code']) ? $line['code'] : null;
                    $rows[] = [$session['id'], $line['seq'], $spender, $line['kind'], $line['delta'], $line['balanceAfter'],
                        isset($line['slot']) && is_int($line['slot']) ? $line['slot'] : null, $code, self::count($line, 'atMs', 2000000000), $now];
                }
                $this->insertMany('defense_shared_budget',
                    'session_id, seq, player_id, kind, delta, balance_after, slot_index, structure_code, at_ms, created_at', $rows);
            }
            $kinds = self::pingKinds();
            $rows = [];
            foreach (array_slice($pings, 0, self::MAX_PING_LINES) as $index => $line) {
                if (!is_array($line) || !isset($line['pid'], $line['kind']) || !is_string($line['pid']) || !isset($byPublic[$line['pid']])
                    || !is_string($line['kind']) || !isset($kinds[$line['kind']])) {
                    continue;
                }
                $coordinate = function ($key) use ($line) {
                    return isset($line[$key]) && is_int($line[$key]) ? max(-1000000, min(1000000, $line[$key])) : 0;
                };
                $rows[] = [$session['id'], $index + 1, $byPublic[$line['pid']], $line['kind'], $coordinate('x'), $coordinate('z'),
                    self::count($line, 'atMs', 2000000000), !isset($line['accepted']) || $line['accepted'] === true ? 1 : 0, $now];
            }
            $this->insertMany('defense_tactical_pings', 'session_id, seq, player_id, kind, x_centi, z_centi, at_ms, accepted, created_at', $rows);

            $response = [
                'accepted' => $accepted, 'rejectReason' => $reject, 'result' => $accepted ? $result : null, 'wavesCleared' => $accepted ? $waves : 0,
                'rewardGc' => $rewardGc, 'rewardXp' => $rewardXp, 'rewardedPlayers' => count($rewarded), 'duplicate' => false,
            ];
            $this->db->exec(
                "UPDATE defense_sessions SET status = 'finished', ended_at = ?, result = ?, waves_cleared = ?, gate_hp_left = ?, duration_seconds = ?,"
                . ' energy_granted = ?, energy_spent = ?, energy_left = ?, close_reason = ?, response = ?, updated_at = ? WHERE id = ?',
                [$now, $accepted ? $result : null, $accepted ? $waves : 0, $accepted ? $gateLeft : 0, $duration, $granted, $spent, $left,
                    $accepted ? $closeReason : 'rejected:' . $reject, json_encode($response, JSON_UNESCAPED_UNICODE), $now, $session['id']]
            );
            if (!$accepted) {
                $app->log('warning', 'coop', '협동 방어전 결과를 인정하지 않음: ' . $reject, $session['public_id']);
            }
            return $response;
        });
    }

    private function insertMany($table, $columns, array $rows)
    {
        foreach (array_chunk($rows, 200) as $chunk) {
            $marks = '(' . implode(', ', array_fill(0, count($chunk[0]), '?')) . ')';
            $params = [];
            foreach ($chunk as $row) {
                foreach ($row as $value) {
                    $params[] = $value;
                }
            }
            $this->db->exec("INSERT IGNORE INTO $table ($columns) VALUES " . implode(', ', array_fill(0, count($chunk), $marks)), $params);
        }
    }

    // ── 결과와 기록 조회 ────────────────────────────────────

    private function resultOut(array $session, array $member, array $player)
    {
        $zone = $this->zoneById($session['zone_id']);
        $notes = ['low_participation' => '참여한 시간이 짧아 보상을 받지 못했어요.', 'daily_limit' => '오늘 받을 수 있는 보상을 모두 받았어요.',
            'rejected' => '결과가 인정되지 않았어요.'];
        $level = $this->app->players()->levelFields($player['level'], $player['xp']);
        return [
            'sessionId' => $session['public_id'], 'status' => $session['status'], 'result' => $session['result'],
            'zoneCode' => $zone !== null ? $zone['code'] : null, 'wavesCleared' => (int)$session['waves_cleared'],
            'gateHpLeft' => (int)$session['gate_hp_left'], 'durationSeconds' => (int)$session['duration_seconds'],
            'playersAtStart' => (int)$session['players_at_start'],
            'rewardGc' => (int)$member['reward_gc'], 'rewardXp' => (int)$member['reward_xp'],
            'rewardNote' => $member['reward_note'], 'rewardMessage' => isset($notes[$member['reward_note']]) ? $notes[$member['reward_note']] : '',
            'kills' => (int)$member['kills'], 'towersBuilt' => (int)$member['towers_built'], 'towersUpgraded' => (int)$member['towers_upgraded'],
            'repairs' => (int)$member['repairs'], 'damageDealt' => (int)$member['damage_dealt'], 'pingsSent' => (int)$member['pings_sent'],
            'energyGranted' => (int)$session['energy_granted'], 'energySpent' => (int)$session['energy_spent'],
            'gc' => $this->app->wallets()->balance($player['id']), 'level' => $level['level'], 'xp' => $level['xp'], 'xpNext' => $level['xpNext'],
            'endedAt' => $session['ended_at'] !== null ? Clock::isoFromSql($session['ended_at']) : null,
        ];
    }

    /** GET /coop/sessions/{id}/result: 방어전 결과 (팀원에게만). 아직 끝나지 않았으면 status 만 온다. */
    public function result(array $player, $publicId)
    {
        $session = $this->sessionRow($publicId);
        $member = $session !== null ? $this->memberRow($session['id'], $player['id']) : null;
        if ($member === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
        }
        $player = $this->app->players()->get($player['id']);
        $team = [];
        foreach ($this->members($session['id']) as $row) {
            $team[] = ['playerId' => $row['player_public_id'], 'nickname' => $row['nickname'], 'roleCode' => $row['role_code'],
                'kills' => (int)$row['kills'], 'towersBuilt' => (int)$row['towers_built'], 'towersUpgraded' => (int)$row['towers_upgraded'],
                'repairs' => (int)$row['repairs'], 'damageDealt' => (int)$row['damage_dealt'], 'isMe' => (int)$row['player_id'] === (int)$player['id']];
        }
        return ['finished' => $session['status'] === 'finished', 'result' => $this->resultOut($session, $member, $player), 'team' => $team];
    }

    /** GET /coop/sessions/{id}/budget: 끝난 판의 공동 예산 장부 (팀원에게만). 전투 중의 잔액은 실시간 서버가 보낸다. */
    public function budget(array $player, $publicId)
    {
        $session = $this->sessionRow($publicId);
        if ($session === null || $this->memberRow($session['id'], $player['id']) === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 방어전이에요.');
        }
        $lines = [];
        foreach ($this->db->all(
            'SELECT b.*, p.public_id AS player_public_id, p.nickname FROM defense_shared_budget b LEFT JOIN players p ON p.id = b.player_id'
            . ' WHERE b.session_id = ? ORDER BY b.seq LIMIT ' . self::MAX_BUDGET_LINES, [$session['id']]
        ) as $row) {
            $lines[] = ['seq' => (int)$row['seq'], 'kind' => $row['kind'], 'delta' => (int)$row['delta'], 'balanceAfter' => (int)$row['balance_after'],
                'playerId' => $row['player_public_id'], 'nickname' => $row['nickname'], 'slotIndex' => $row['slot_index'] !== null ? (int)$row['slot_index'] : null,
                'structureCode' => $row['structure_code'], 'atMs' => (int)$row['at_ms']];
        }
        return [
            'sessionId' => $session['public_id'], 'status' => $session['status'], 'live' => $session['status'] === 'running',
            'energyGranted' => (int)$session['energy_granted'], 'energySpent' => (int)$session['energy_spent'], 'energyLeft' => (int)$session['energy_left'],
            'lines' => $lines,
        ];
    }

    /** GET /coop/history: 내가 함께한 최근 방어전. */
    public function history(array $player)
    {
        $player = $this->app->players()->get($player['id']);
        $items = [];
        foreach ($this->db->all(
            'SELECT s.*, m.id AS member_id FROM defense_session_members m JOIN defense_sessions s ON s.id = m.session_id'
            . " WHERE m.player_id = ? AND s.status = 'finished' ORDER BY s.id DESC LIMIT 20", [$player['id']]
        ) as $row) {
            $items[] = $this->resultOut($row, $this->db->one('SELECT * FROM defense_session_members WHERE id = ?', [$row['member_id']]), $player);
        }
        return ['results' => $items];
    }

    // ── 정리 ────────────────────────────────────────────────

    /**
     * 시간이 지난 판을 닫는다. 예약 작업과 목록 조회가 부른다.
     * 대기실은 expired, 끝나지 않은 전투는 abandoned 가 된다. 닫힌 판에는 보상이 없다.
     * @return int 닫은 판의 수
     */
    public function expireDue()
    {
        $now = Clock::sql();
        $closed = $this->db->exec(
            "UPDATE defense_sessions SET status = 'expired', close_reason = 'lobby_timeout', ended_at = ?, updated_at = ? WHERE status = 'lobby' AND expires_at <= ?",
            [$now, $now, $now]);
        $closed += $this->db->exec(
            "UPDATE defense_sessions SET status = 'abandoned', close_reason = 'timeout', ended_at = ?, updated_at = ? WHERE status = 'running' AND expires_at <= ?",
            [$now, $now, $now]);
        return $closed;
    }

    // ── 전술 핑 차단 ────────────────────────────────────────

    public function isPingBlocked($playerId)
    {
        return $this->db->val(
            'SELECT 1 FROM defense_ping_blocks WHERE player_id = ? AND lifted_at IS NULL AND (expires_at IS NULL OR expires_at > ?) LIMIT 1',
            [$playerId, Clock::sql()]) !== null;
    }

    /**
     * 전술 핑을 막는다 (남용한 계정). 기간(시간)을 0 으로 주면 풀 때까지 막는다.
     * @return string[] 문제 목록
     */
    public function blockPing(array $admin, $playerPublicId, $hours, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::COOP_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $problems = [];
        $playerId = is_string($playerPublicId) ? $this->db->val('SELECT id FROM players WHERE public_id = ?', [trim($playerPublicId)]) : null;
        if ($playerId === null) {
            $problems[] = '없는 플레이어입니다.';
        }
        if (!is_string($hours) || !preg_match('/^\d{1,5}$/', $hours) || (int)$hours > 8760) {
            $problems[] = '기간은 0 ~ 8,760시간 사이여야 합니다 (0: 풀 때까지).';
        }
        $reason = is_string($reason) ? trim($reason) : '';
        $problem = DefEditor::reasonProblem($reason);
        if ($problem !== null) {
            $problems[] = $problem;
        }
        if ($problems) {
            return $problems;
        }
        if ($this->isPingBlocked($playerId)) {
            return ['이미 핑이 막힌 플레이어입니다.'];
        }
        $this->db->tx(function () use ($admin, $playerId, $playerPublicId, $hours, $reason, $ip) {
            $this->db->exec('INSERT INTO defense_ping_blocks (player_id, reason, admin_id, created_at, expires_at) VALUES (?, ?, ?, ?, ?)',
                [$playerId, $reason, $admin['id'], Clock::sql(), (int)$hours > 0 ? Clock::sql(Clock::now() + (int)$hours * 3600) : null]);
            (new Auth($this->app))->audit($admin['id'], 'ping.block', 'player', trim($playerPublicId), $hours . 'h: ' . $reason, $ip);
        });
        return [];
    }

    public function liftPingBlock(array $admin, $blockId, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::COOP_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $reason = is_string($reason) ? trim($reason) : '';
        $problem = DefEditor::reasonProblem($reason);
        if ($problem !== null) {
            return [$problem];
        }
        return $this->db->tx(function () use ($admin, $blockId, $reason, $ip) {
            $row = $this->db->one('SELECT b.*, p.public_id AS player_public_id FROM defense_ping_blocks b JOIN players p ON p.id = b.player_id'
                . ' WHERE b.id = ? FOR UPDATE', [(int)$blockId]);
            if ($row === null || $row['lifted_at'] !== null) {
                return ['이미 풀렸거나 없는 차단입니다.'];
            }
            $this->db->exec('UPDATE defense_ping_blocks SET lifted_at = ?, lifted_by = ? WHERE id = ?', [Clock::sql(), $admin['id'], $row['id']]);
            (new Auth($this->app))->audit($admin['id'], 'ping.unblock', 'player', $row['player_public_id'], $reason, $ip);
            return [];
        });
    }

    public function pingBlocks($limit = 100)
    {
        return $this->db->all(
            'SELECT b.*, p.public_id AS player_public_id, p.nickname, a.login FROM defense_ping_blocks b JOIN players p ON p.id = b.player_id'
            . ' LEFT JOIN admins a ON a.id = b.admin_id ORDER BY (b.lifted_at IS NULL) DESC, b.id DESC LIMIT ' . (int)$limit);
    }

    /** 최근 7일 동안 핑이 많이 거절된 계정 (남용을 살피는 데 쓴다). */
    public function pingAbuse($limit = 50)
    {
        return $this->db->all(
            'SELECT p.public_id AS player_public_id, p.nickname, COUNT(*) AS total, SUM(CASE WHEN g.accepted = 0 THEN 1 ELSE 0 END) AS rejected,'
            . ' COUNT(DISTINCT g.session_id) AS sessions FROM defense_tactical_pings g JOIN players p ON p.id = g.player_id'
            . ' WHERE g.created_at >= ? GROUP BY p.id, p.public_id, p.nickname HAVING SUM(CASE WHEN g.accepted = 0 THEN 1 ELSE 0 END) > 0'
            . ' ORDER BY rejected DESC, total DESC LIMIT ' . (int)$limit, [Clock::sql(Clock::now() - 7 * 86400)]);
    }

    public function pingKindTotals()
    {
        return $this->db->all('SELECT kind, COUNT(*) AS total, SUM(CASE WHEN accepted = 0 THEN 1 ELSE 0 END) AS rejected FROM defense_tactical_pings'
            . ' WHERE created_at >= ? GROUP BY kind ORDER BY total DESC', [Clock::sql(Clock::now() - 7 * 86400)]);
    }

    // ── 관리자: 모니터링 ────────────────────────────────────

    public function adminCounts()
    {
        $counts = [];
        foreach (array_keys(self::statuses()) as $status) {
            $counts[$status] = 0;
        }
        foreach ($this->db->all('SELECT status, COUNT(*) AS n FROM defense_sessions GROUP BY status') as $row) {
            $counts[$row['status']] = (int)$row['n'];
        }
        return $counts;
    }

    public function adminSessions($status = '', $limit = 100)
    {
        $where = '1 = 1';
        $params = [];
        if ($status !== '' && isset(self::statuses()[$status])) {
            $where .= ' AND s.status = ?';
            $params[] = $status;
        }
        return $this->db->all(
            'SELECT s.*, r.code AS region_code, r.name AS region_name, z.name AS zone_name, h.nickname AS host_nickname,'
            . " (SELECT COUNT(*) FROM defense_session_members m WHERE m.session_id = s.id AND m.status = 'joined') AS joined,"
            . ' (SELECT COUNT(*) FROM defense_session_members m WHERE m.session_id = s.id) AS members'
            . ' FROM defense_sessions s JOIN regions r ON r.id = s.region_id JOIN defense_zones z ON z.id = s.zone_id'
            . ' JOIN players h ON h.id = s.host_player_id WHERE ' . $where . ' ORDER BY s.id DESC LIMIT ' . (int)$limit, $params);
    }

    public function adminSession($id)
    {
        $session = $this->db->one(
            'SELECT s.*, r.code AS region_code, r.name AS region_name, z.name AS zone_name FROM defense_sessions s'
            . ' JOIN regions r ON r.id = s.region_id JOIN defense_zones z ON z.id = s.zone_id WHERE s.id = ?', [(int)$id]);
        if ($session === null) {
            return null;
        }
        return [
            'session' => $session, 'members' => $this->members($session['id']),
            'budget' => $this->db->all('SELECT b.*, p.nickname FROM defense_shared_budget b LEFT JOIN players p ON p.id = b.player_id'
                . ' WHERE b.session_id = ? ORDER BY b.seq LIMIT 300', [$session['id']]),
            'pings' => $this->db->all('SELECT g.*, p.nickname FROM defense_tactical_pings g JOIN players p ON p.id = g.player_id'
                . ' WHERE g.session_id = ? ORDER BY g.seq LIMIT 300', [$session['id']]),
        ];
    }

    /** 대기 중이거나 전투 중인 판을 강제로 닫는다. 보상은 없다. 실시간 서버는 결과를 보고할 때 닫힌 것을 알게 된다. */
    public function adminClose(array $admin, $id, $reason, $ip = '')
    {
        if (!Auth::can($admin, Auth::COOP_MANAGE)) {
            return ['권한이 없습니다.'];
        }
        $reason = is_string($reason) ? trim($reason) : '';
        $problem = DefEditor::reasonProblem($reason);
        if ($problem !== null) {
            return [$problem];
        }
        return $this->db->tx(function () use ($admin, $id, $reason, $ip) {
            $session = $this->db->one('SELECT * FROM defense_sessions WHERE id = ? FOR UPDATE', [(int)$id]);
            if ($session === null) {
                return ['없는 방어전입니다.'];
            }
            if ($session['status'] !== 'lobby' && $session['status'] !== 'running') {
                return ['이미 닫힌 방어전입니다.'];
            }
            $now = Clock::sql();
            $this->db->exec("UPDATE defense_sessions SET status = ?, close_reason = 'admin', ended_at = ?, updated_at = ? WHERE id = ?",
                [$session['status'] === 'lobby' ? 'cancelled' : 'abandoned', $now, $now, $session['id']]);
            (new Auth($this->app))->audit($admin['id'], 'coop.close', 'defense_sessions', $session['public_id'], $reason, $ip);
            return [];
        });
    }

    /**
     * 역할의 보정값을 고친다.
     * @return string[] 문제 목록
     */
    public function saveRole(array $admin, $code, array $form, $ip = '')
    {
        if (!Auth::can($admin, Auth::CITY_POLICY)) {
            return ['권한이 없습니다.'];
        }
        $before = $this->db->one('SELECT * FROM defense_team_roles WHERE code = ?', [(string)$code]);
        if ($before === null) {
            return ['없는 역할입니다.'];
        }
        $problems = [];
        $after = [];
        $fields = [
            'build_discount_percent' => ['타워 비용 할인(%)', 0, 50], 'repair_bonus_percent' => ['수리량 증가(%)', 0, 200],
            'attack_bonus_percent' => ['직접 공격 피해 증가(%)', 0, 200], 'ping_cooldown_percent' => ['핑 재사용 시간 단축(%)', 0, 90],
            'max_per_session' => ['한 판에 고를 수 있는 사람 수 (0: 제한 없음)', 0, 16],
        ];
        foreach ($fields as $column => $spec) {
            $value = isset($form[$column]) && is_string($form[$column]) ? trim($form[$column]) : '';
            if (!preg_match('/^\d{1,4}$/', $value) || (int)$value < $spec[1] || (int)$value > $spec[2]) {
                $problems[] = $spec[0] . ': ' . $spec[1] . ' ~ ' . $spec[2] . ' 사이의 정수여야 합니다.';
                continue;
            }
            $after[$column] = (int)$value;
        }
        $after['is_active'] = isset($form['is_active']) && $form['is_active'] === '1' ? 1 : 0;
        $reason = isset($form['reason']) && is_string($form['reason']) ? trim($form['reason']) : '';
        $problem = DefEditor::reasonProblem($reason);
        if ($problem !== null) {
            $problems[] = $problem;
        }
        if ($problems) {
            return $problems;
        }
        $old = [];
        foreach ($after as $column => $value) {
            $old[$column] = (int)$before[$column];
        }
        if ($old === $after) {
            return [];
        }
        $this->db->tx(function () use ($admin, $before, $old, $after, $reason, $ip) {
            $this->db->exec(
                'UPDATE defense_team_roles SET build_discount_percent = ?, repair_bonus_percent = ?, attack_bonus_percent = ?, ping_cooldown_percent = ?,'
                . ' max_per_session = ?, is_active = ?, updated_at = ? WHERE id = ?',
                [$after['build_discount_percent'], $after['repair_bonus_percent'], $after['attack_bonus_percent'], $after['ping_cooldown_percent'],
                    $after['max_per_session'], $after['is_active'], Clock::sql(), $before['id']]);
            $this->db->exec(
                'INSERT INTO config_history (target_type, target_key, before_value, after_value, admin_id, reason, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                ['defense_team_roles', $before['code'], json_encode($old), json_encode($after), $admin['id'], $reason, Clock::sql()]);
            (new Auth($this->app))->audit($admin['id'], 'coop.role.update', 'defense_team_roles', $before['code'], $reason, $ip);
        });
        $this->roles = null;
        return [];
    }

    public function adminRoles()
    {
        return $this->db->all('SELECT * FROM defense_team_roles ORDER BY sort_order, id');
    }

    /**
     * 기록의 앞뒤가 맞는지 검사한다. 맞으면 null.
     *   - 한 계정이 동시에 두 판에 들어가 있지 않다.
     *   - 끝난 판마다 팀원에게 준 GC 의 합이 지갑 원장(근거 defense_session)의 합과 같다.
     *   - 끝난 판마다 공동 예산 장부의 잔액이 이어지고, 마지막 잔액이 남은 예산과 같다.
     */
    public function findInconsistency()
    {
        $double = $this->db->one(
            'SELECT m.player_id, COUNT(*) AS n FROM defense_session_members m JOIN defense_sessions s ON s.id = m.session_id'
            . " WHERE m.status = 'joined' AND s.status IN ('lobby', 'running') GROUP BY m.player_id HAVING COUNT(*) > 1 LIMIT 1");
        if ($double !== null) {
            return "한 계정이 동시에 여러 판에 들어가 있음 (player {$double['player_id']})";
        }
        $paid = [];
        foreach ($this->db->all("SELECT ref_id, COALESCE(SUM(delta), 0) AS total FROM ledger_entries WHERE ref_type = 'defense_session' GROUP BY ref_id") as $row) {
            $paid[(int)$row['ref_id']] = (int)$row['total'];
        }
        foreach ($this->db->all('SELECT session_id, COALESCE(SUM(reward_gc), 0) AS total FROM defense_session_members GROUP BY session_id') as $row) {
            $sessionId = (int)$row['session_id'];
            $ledger = isset($paid[$sessionId]) ? $paid[$sessionId] : 0;
            if ($ledger !== (int)$row['total']) {
                return "협동 방어전 보상의 합({$row['total']})과 지갑 원장의 합($ledger)이 다름 (session $sessionId)";
            }
            unset($paid[$sessionId]);
        }
        if ($paid) {
            return '팀원 기록이 없는 협동 방어전 보상이 원장에 있음';
        }
        $running = [];
        foreach ($this->db->all('SELECT session_id, delta, balance_after FROM defense_shared_budget ORDER BY session_id, seq') as $row) {
            $key = (int)$row['session_id'];
            $running[$key] = (isset($running[$key]) ? $running[$key] : 0) + (int)$row['delta'];
            if ($running[$key] !== (int)$row['balance_after'] || $running[$key] < 0) {
                return "공동 예산 장부의 잔액이 이어지지 않음 (session $key)";
            }
        }
        foreach ($this->db->all("SELECT id, energy_left, energy_granted, energy_spent FROM defense_sessions WHERE status = 'finished'") as $row) {
            $left = isset($running[(int)$row['id']]) ? $running[(int)$row['id']] : 0;
            if ($left !== (int)$row['energy_left'] || (int)$row['energy_granted'] - (int)$row['energy_spent'] !== $left) {
                return "공동 예산의 남은 값이 장부와 다름 (session {$row['id']})";
            }
        }
        return null;
    }
}
