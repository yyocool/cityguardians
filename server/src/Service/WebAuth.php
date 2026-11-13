<?php
namespace CG\Service;

use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 홈페이지 로그인 (docs/CITY_PASS_API.md 2장).
 *
 * 게임 계정에는 아이디와 비밀번호가 없다. 그래서 게임 안에서 일회용 코드를 받아 홈페이지에 넣는다.
 * 코드는 5분 동안, 한 번만 쓸 수 있다. 코드와 접속 토큰은 해시만 저장한다.
 * 홈페이지의 접속은 게임의 접속 토큰과 별개다: 홈페이지 쿠키로는 게임 API 를 부를 수 없다.
 */
final class WebAuth
{
    const COOKIE = 'cg_web';
    const CODE_SECONDS = 300;
    const CODE_LENGTH = 8;
    const IDLE_MINUTES = 30;
    const MAX_HOURS = 4;

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    /** 코드에서 붙임표와 빈칸을 떼고 소문자로 맞춘다. */
    public static function normalize($code)
    {
        return is_string($code) ? strtolower(preg_replace('/[^A-Za-z0-9]/', '', $code)) : '';
    }

    /** 게임 안에서 부른다: 홈페이지에 넣을 일회용 코드. */
    public function issueCode(array $player)
    {
        $this->app->rateLimit()->hit('web_code', 'p' . $player['id'], 6, 600);
        $code = Ids::token(self::CODE_LENGTH);
        $db = $this->app->db;
        // 앞서 받은 코드는 못 쓰게 한다: 살아 있는 코드는 하나뿐이다.
        $db->exec('UPDATE web_login_codes SET used_at = ? WHERE player_id = ? AND used_at IS NULL', [Clock::sql(), $player['id']]);
        $db->exec('INSERT INTO web_login_codes (player_id, code_hash, expires_at, created_at) VALUES (?, ?, ?, ?)',
            [$player['id'], Ids::sha256($code), Clock::sql(Clock::now() + self::CODE_SECONDS), Clock::sql()]);
        $base = $this->app->publicBase();
        return [
            'code' => strtoupper(substr($code, 0, 4) . '-' . substr($code, 4)),
            'expiresAt' => Clock::iso(Clock::now() + self::CODE_SECONDS),
            'expiresInSeconds' => self::CODE_SECONDS,
            'loginUrl' => $base !== '' ? $base . '/login' : null,
        ];
    }

    /**
     * 홈페이지에서 코드를 넣었다. 맞으면 [쿠키에 넣을 토큰, CSRF 토큰], 아니면 null.
     */
    public function login($code, $ip)
    {
        $this->app->rateLimit()->hit('web_login', $ip, $this->app->env->int('WEB_LOGIN_PER_10MIN', 10), 600);
        $code = self::normalize($code);
        if (strlen($code) !== self::CODE_LENGTH) {
            return null;
        }
        $db = $this->app->db;
        return $db->tx(function () use ($db, $code) {
            $row = $db->one('SELECT c.id, c.player_id, p.status FROM web_login_codes c JOIN players p ON p.id = c.player_id'
                . ' WHERE c.code_hash = ? AND c.used_at IS NULL AND c.expires_at > ? FOR UPDATE', [Ids::sha256($code), Clock::sql()]);
            if ($row === null || $row['status'] !== 'active') {
                return null;
            }
            // 같은 코드는 두 번 쓰이지 않는다.
            if ($db->exec('UPDATE web_login_codes SET used_at = ? WHERE id = ? AND used_at IS NULL', [Clock::sql(), $row['id']]) !== 1) {
                return null;
            }
            $token = Ids::secret();
            $csrf = Ids::secret();
            $db->exec('INSERT INTO web_sessions (player_id, token_hash, csrf_token, created_at, last_seen_at, expires_at) VALUES (?, ?, ?, ?, ?, ?)',
                [$row['player_id'], Ids::sha256($token), $csrf, Clock::sql(), Clock::sql(), Clock::sql(Clock::now() + self::MAX_HOURS * 3600)]);
            return [$token, $csrf];
        });
    }

    /** @return array|null player, csrf */
    public function session($token)
    {
        if (!is_string($token) || !preg_match('/^[0-9a-f]{64}$/', $token)) {
            return null;
        }
        $db = $this->app->db;
        $now = Clock::now();
        $row = $db->one(
            'SELECT s.id, s.player_id, s.csrf_token FROM web_sessions s JOIN players p ON p.id = s.player_id'
            . " WHERE s.token_hash = ? AND s.revoked_at IS NULL AND s.expires_at > ? AND s.last_seen_at > ? AND p.status = 'active'",
            [Ids::sha256($token), Clock::sql($now), Clock::sql($now - self::IDLE_MINUTES * 60)]
        );
        if ($row === null) {
            return null;
        }
        $db->exec('UPDATE web_sessions SET last_seen_at = ? WHERE id = ?', [Clock::sql($now), $row['id']]);
        return ['player' => $this->app->players()->get((int)$row['player_id']), 'csrf' => $row['csrf_token']];
    }

    public function logout($token)
    {
        if (is_string($token) && $token !== '') {
            $this->app->db->exec('UPDATE web_sessions SET revoked_at = ? WHERE token_hash = ? AND revoked_at IS NULL',
                [Clock::sql(), Ids::sha256($token)]);
        }
    }

    /** 지난 코드와 접속 기록을 정리한다. 예약 작업이 부른다. */
    public function purge()
    {
        $old = Clock::sql(Clock::now() - 7 * 86400);
        return $this->app->db->exec('DELETE FROM web_login_codes WHERE expires_at < ?', [$old])
            + $this->app->db->exec('DELETE FROM web_sessions WHERE expires_at < ?', [$old]);
    }
}
