<?php
namespace CG\Service;

use CG\App;
use CG\Domain\Nickname;
use CG\Domain\Progression;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 계정, 접속, 레벨, 소속 도시. 세 전투 모드가 모두 이 하나의 기록을 쓴다.
 *
 * 플레이어를 나타내는 배열:
 *   id(내부 번호), publicId, status, resident, nickname, regionId, regionCode, level, xp
 */
final class Players
{
    /** 접속 토큰의 수명(시간). 만료되면 게스트 비밀키로 다시 받는다. */
    const SESSION_HOURS = 24;

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    // ── 계정과 접속 ─────────────────────────────────────────

    /**
     * 게스트 계정을 만든다. 비밀키는 이때 한 번만 돌려준다 (서버에는 해시만 남는다).
     * @return array playerId, guestSecret, accessToken, expiresIn
     */
    public function createGuest($platform = '', $clientVersion = '')
    {
        $db = $this->app->db;
        return $db->tx(function () use ($db, $platform, $clientVersion) {
            $now = Clock::sql();
            $publicId = Ids::publicId('p_');
            $secret = Ids::secret();
            $id = $db->insert(
                "INSERT INTO players (public_id, status, residency_status, created_at, last_seen_at) VALUES (?, 'active', 'stranger', ?, ?)",
                [$publicId, $now, $now]
            );
            $db->exec(
                "INSERT INTO player_credentials (player_id, type, identifier, secret_hash, created_at) VALUES (?, 'guest', ?, ?, ?)",
                [$id, $publicId, password_hash($secret, PASSWORD_DEFAULT), $now]
            );
            $db->exec('INSERT INTO characters (player_id, level, xp, updated_at) VALUES (?, 1, 0, ?)', [$id, $now]);
            $db->exec('INSERT INTO wallets (player_id, balance, updated_at) VALUES (?, 0, ?)', [$id, $now]);
            $db->exec('INSERT INTO player_combat_state (player_id, updated_at) VALUES (?, ?)', [$id, $now]);
            $session = $this->issueToken($id, $platform, $clientVersion);
            return ['playerId' => $publicId, 'guestSecret' => $secret] + $session;
        });
    }

    /** 게스트 ID와 비밀키로 새 접속 토큰을 받는다. */
    public function login($publicId, $secret, $platform = '', $clientVersion = '')
    {
        $db = $this->app->db;
        $row = $db->one(
            "SELECT c.player_id, c.secret_hash, p.status FROM player_credentials c JOIN players p ON p.id = c.player_id"
            . " WHERE c.type = 'guest' AND c.identifier = ?",
            [(string)$publicId]
        );
        // 없는 계정과 틀린 비밀키를 구별해 알려 주지 않는다.
        if ($row === null || !password_verify((string)$secret, $row['secret_hash'])) {
            throw new ApiError(ApiError::UNAUTHORIZED, '접속 정보가 맞지 않아요.');
        }
        if ($row['status'] !== 'active') {
            throw new ApiError(ApiError::SUSPENDED, '이용이 제한된 계정이에요.');
        }
        $session = $this->issueToken((int)$row['player_id'], $platform, $clientVersion);
        return ['playerId' => (string)$publicId] + $session;
    }

    private function issueToken($playerId, $platform, $clientVersion)
    {
        $token = Ids::secret();
        $seconds = max(1, $this->app->env->int('SESSION_HOURS', self::SESSION_HOURS)) * 3600;
        $expires = Clock::now() + $seconds;
        $this->app->db->exec(
            'INSERT INTO player_sessions (player_id, token_hash, platform, client_version, created_at, expires_at) VALUES (?, ?, ?, ?, ?, ?)',
            [$playerId, Ids::sha256($token), mb_substr((string)$platform, 0, 16), mb_substr((string)$clientVersion, 0, 16),
                Clock::sql(), Clock::sql($expires)]
        );
        return ['accessToken' => $token, 'expiresIn' => $seconds];
    }

    /** 접속 토큰으로 플레이어를 찾는다. 없거나 만료되었으면 UNAUTHORIZED. */
    public function authenticate($token)
    {
        if (!is_string($token) || !preg_match('/^[0-9a-f]{64}$/', $token)) {
            throw new ApiError(ApiError::UNAUTHORIZED, '다시 접속해 주세요.');
        }
        $db = $this->app->db;
        $session = $db->one(
            'SELECT player_id FROM player_sessions WHERE token_hash = ? AND revoked_at IS NULL AND expires_at > ?',
            [Ids::sha256($token), Clock::sql()]
        );
        if ($session === null) {
            throw new ApiError(ApiError::UNAUTHORIZED, '다시 접속해 주세요.');
        }
        $player = $this->get((int)$session['player_id']);
        if ($player === null) {
            throw new ApiError(ApiError::UNAUTHORIZED, '다시 접속해 주세요.');
        }
        if ($player['status'] !== 'active') {
            throw new ApiError(ApiError::SUSPENDED, '이용이 제한된 계정이에요.');
        }
        // 마지막 접속 시각은 5분에 한 번만 고친다.
        $db->exec('UPDATE players SET last_seen_at = ? WHERE id = ? AND (last_seen_at IS NULL OR last_seen_at < ?)',
            [Clock::sql(), $player['id'], Clock::sql(Clock::now() - 300)]);
        return $player;
    }

    public function logout($token)
    {
        $this->app->db->exec('UPDATE player_sessions SET revoked_at = ? WHERE token_hash = ? AND revoked_at IS NULL',
            [Clock::sql(), Ids::sha256((string)$token)]);
    }

    // ── 조회 ────────────────────────────────────────────────

    public function get($id)
    {
        $row = $this->app->db->one(
            'SELECT p.id, p.public_id, p.status, p.residency_status, p.nickname, p.region_id, r.code AS region_code,'
            . ' c.level, c.xp FROM players p'
            . ' LEFT JOIN characters c ON c.player_id = p.id'
            . ' LEFT JOIN regions r ON r.id = p.region_id'
            . ' WHERE p.id = ?',
            [$id]
        );
        if ($row === null) {
            return null;
        }
        $resident = $row['residency_status'] === 'resident' && $row['region_id'] !== null && $row['region_code'] !== null;
        return [
            'id' => (int)$row['id'],
            'publicId' => $row['public_id'],
            'status' => $row['status'],
            'resident' => $resident,
            'nickname' => $row['nickname'],
            'regionId' => $resident ? (int)$row['region_id'] : null,
            'regionCode' => $resident ? $row['region_code'] : null,
            'level' => $row['level'] !== null ? (int)$row['level'] : 1,
            'xp' => $row['xp'] !== null ? (int)$row['xp'] : 0,
        ];
    }

    /** 레벨·경험치를 응답에 넣는 형식으로. */
    public function levelFields($level, $xp)
    {
        $config = $this->app->defs()->progression();
        return [
            'level' => (int)$level,
            'xp' => (int)$xp,
            'xpNext' => Progression::xpForNextLevel($level, $config),
            'hpMax' => Progression::maxHp($level, $config),
        ];
    }

    /** GET /me: 세 전투 모드가 함께 쓰는 계정 정보. */
    public function me(array $player)
    {
        $player = $this->get($player['id']);
        $app = $this->app;
        $combat = $app->combat();
        $support = $app->city()->openSupportOf($player['id']);
        return [
            'playerId' => $player['publicId'],
            'displayName' => $player['nickname'] !== null ? $player['nickname'] : '이방인',
            'residency' => $player['resident'] ? 'resident' : 'stranger',
            'regionCode' => $player['regionCode'],
        ] + $this->levelFields($player['level'], $player['xp']) + [
            'gc' => $app->wallets()->balance($player['id']),
            'dailyHuntGc' => $app->daily()->get($player['id'], Daily::HUNT_GC),
            'dailyHuntGcLimit' => $app->defs()->economy()['dailyHuntGcLimit'],
            'unlockedTowers' => $app->defense()->usableStructures($player['id']),
            'clearedStages' => $app->defense()->clearedZones($player['id']),
            'gear' => $combat->ownedList($player['id']),
            'loadouts' => $combat->loadoutList($player['id']),
            'clearedFpsMissions' => $app->fps()->clearedMissions($player['id']),
            'cityContribution' => $player['resident'] ? $app->city()->contributionOf($player['id'], $player['regionId']) : 0,
            'supportRegionCode' => $support !== null ? $support['regionCode'] : null,
        ];
    }

    // ── 레벨 ────────────────────────────────────────────────

    /**
     * 경험치를 더한다. 어느 전투 모드에서 얻었든 같은 곳에 쌓인다.
     * @return array level, xp, xpNext, hpMax, levelsGained
     */
    public function addXp($playerId, $gained)
    {
        $db = $this->app->db;
        return $db->tx(function () use ($db, $playerId, $gained) {
            $row = $db->one('SELECT level, xp FROM characters WHERE player_id = ? FOR UPDATE', [$playerId]);
            $level = $row ? (int)$row['level'] : 1;
            $xp = $row ? (int)$row['xp'] : 0;
            $levelsGained = 0;
            if ($gained > 0) {
                list($level, $xp, $levelsGained) = Progression::addXp($level, $xp, (int)$gained, $this->app->defs()->progression());
                $db->exec('UPDATE characters SET level = ?, xp = ?, updated_at = ? WHERE player_id = ?',
                    [$level, $xp, Clock::sql(), $playerId]);
            }
            return $this->levelFields($level, $xp) + ['levelsGained' => $levelsGained];
        });
    }

    // ── 전투 상태 ───────────────────────────────────────────

    public function combatState($playerId, $forUpdate = false)
    {
        $db = $this->app->db;
        $sql = 'SELECT last_active_at, hunt_kill_carry, support_request_id FROM player_combat_state WHERE player_id = ?';
        $row = $db->one($sql . ($forUpdate ? ' FOR UPDATE' : ''), [$playerId]);
        if ($row === null) {
            $db->exec('INSERT IGNORE INTO player_combat_state (player_id, updated_at) VALUES (?, ?)', [$playerId, Clock::sql()]);
            return ['lastActiveAt' => null, 'huntKillCarry' => 0, 'supportRequestId' => null];
        }
        return [
            'lastActiveAt' => $row['last_active_at'],
            'huntKillCarry' => (int)$row['hunt_kill_carry'],
            'supportRequestId' => $row['support_request_id'] !== null ? (int)$row['support_request_id'] : null,
        ];
    }

    /** 방금 전투에 참여했다고 적는다 (활성 가디언 판정). */
    public function touchActive($playerId)
    {
        $this->combatState($playerId);
        $this->app->db->exec('UPDATE player_combat_state SET last_active_at = ?, updated_at = ? WHERE player_id = ?',
            [Clock::sql(), Clock::sql(), $playerId]);
    }

    public function setHuntCarry($playerId, $carry)
    {
        $this->app->db->exec('UPDATE player_combat_state SET hunt_kill_carry = ?, updated_at = ? WHERE player_id = ?',
            [(int)$carry, Clock::sql(), $playerId]);
    }

    public function setSupportRequest($playerId, $requestId)
    {
        $this->combatState($playerId);
        $this->app->db->exec('UPDATE player_combat_state SET support_request_id = ?, updated_at = ? WHERE player_id = ?',
            [$requestId, Clock::sql(), $playerId]);
    }

    // ── 전입신고 ────────────────────────────────────────────

    /** 닉네임을 쓸 수 없는 이유. 쓸 수 있으면 null. */
    public function nicknameProblem($playerId, $nickname)
    {
        $reason = Nickname::check($nickname);
        if ($reason !== null) {
            return $reason;
        }
        $key = Nickname::key($nickname);
        foreach ($this->app->db->all('SELECT id, nickname FROM players WHERE nickname = ?', [Nickname::normalize($nickname)]) as $row) {
            if ((int)$row['id'] !== (int)$playerId && Nickname::key($row['nickname']) === $key) {
                return Nickname::TAKEN;
            }
        }
        return null;
    }

    /**
     * 전입신고: 도시를 고르고 이름을 정한다. 한 거래로 처리한다.
     * @return array regionCode, nickname, bonusGc, me
     */
    public function registerResidency(array $player, $regionCode, $nickname)
    {
        $app = $this->app;
        $db = $app->db;
        return $db->tx(function () use ($app, $db, $player, $regionCode, $nickname) {
            $app->wallets()->lock($player['id']);
            $player = $this->get($player['id']);
            if ($player['resident']) {
                throw new ApiError(ApiError::CONFLICT, '이미 전입신고를 했어요.', 'already_resident');
            }
            $region = $db->one('SELECT id, code FROM regions WHERE code = ?', [(string)$regionCode]);
            if ($region === null) {
                throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
            }
            $reason = $this->nicknameProblem($player['id'], $nickname);
            if ($reason !== null) {
                throw new ApiError($reason === Nickname::TAKEN ? ApiError::CONFLICT : ApiError::VALIDATION_FAILED,
                    Nickname::message($reason), $reason);
            }

            $now = Clock::sql();
            $name = Nickname::normalize($nickname);
            try {
                $db->exec(
                    "UPDATE players SET residency_status = 'resident', nickname = ?, region_id = ?, registered_at = ? WHERE id = ?",
                    [$name, $region['id'], $now, $player['id']]
                );
            } catch (\PDOException $error) {
                // 같은 이름을 동시에 신청한 경우: 유일 색인이 막는다.
                if (isset($error->errorInfo[1]) && (int)$error->errorInfo[1] === 1062) {
                    throw new ApiError(ApiError::CONFLICT, Nickname::message(Nickname::TAKEN), Nickname::TAKEN);
                }
                throw $error;
            }
            $db->exec('UPDATE regions SET resident_count = resident_count + 1 WHERE id = ?', [$region['id']]);
            // v9.0: 전입 기록을 남기고, 거래가 끝난 뒤 그 도시의 유효 전입자 수를 다시 센다.
            $app->growth()->onRegistered($player['id'], (int)$region['id']);

            // 정착 지원금은 계정에 한 번만 준다.
            $bonus = 0;
            $paid = $db->val('SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND reason = ?',
                [$player['id'], Wallets::RESIDENCY_BONUS]);
            $amount = $app->defs()->economy()['residencyBonusGc'];
            if ((int)$paid === 0 && $amount > 0) {
                $bonus = $amount;
                $app->wallets()->apply($player['id'], $bonus, Wallets::RESIDENCY_BONUS, 'region', (int)$region['id']);
            }
            $this->touchActive($player['id']);

            return [
                'regionCode' => $region['code'],
                'nickname' => $name,
                'bonusGc' => $bonus,
                'me' => $this->me($player),
            ];
        });
    }
}
