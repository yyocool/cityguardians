<?php
namespace CG\Service;

use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Db;

/**
 * 중복 방지 키 (docs/API_SPEC.md 1장).
 *
 * GC 나 소유가 바뀌는 요청에 쓴다. 같은 키와 같은 내용이 다시 오면 처음 응답을 그대로 돌려주고 (두 번 처리하지 않는다),
 * 같은 키에 다른 내용이 오면 IDEMPOTENCY_MISMATCH 로 거절한다.
 * 실패한 요청은 기록하지 않는다. 같은 키로 다시 시도할 수 있다.
 */
final class Idempotency
{
    /** @var Db */
    private $db;

    public function __construct(Db $db)
    {
        $this->db = $db;
    }

    public static function valid($key)
    {
        return is_string($key) && preg_match('/^[A-Za-z0-9_-]{8,64}$/', $key) === 1;
    }

    /**
     * @param callable $work 실제 처리. 응답(배열)을 돌려준다.
     * @param callable|null $lock 줄을 세울 행을 잠그는 일. 주지 않으면 지갑 행을 잠근다.
     *        GC 와 상관없는 요청(CITY PASS 쿠폰)은 지갑을 건드리지 않도록 자기 행을 잠그는 일을 넘긴다.
     * @return array 응답. 다시 온 요청이면 'duplicate' => true 가 붙는다.
     */
    public function run($playerId, $key, $endpoint, array $request, callable $work, callable $lock = null)
    {
        if (!self::valid($key)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '요청 번호가 올바르지 않아요.');
        }
        $hash = hash('sha256', $endpoint . "\n" . json_encode($request));
        return $this->db->tx(function () use ($playerId, $key, $endpoint, $hash, $work, $lock) {
            // 같은 플레이어의 요청이 동시에 오면 한 행에서 줄을 선다. 그 뒤에 키를 확인하므로 두 번 처리되지 않는다.
            if ($lock !== null) {
                $lock();
            } else {
                $this->db->val('SELECT balance FROM wallets WHERE player_id = ? FOR UPDATE', [$playerId]);
            }
            $seen = $this->db->one(
                'SELECT endpoint, request_hash, response FROM idempotency_keys WHERE player_id = ? AND idem_key = ?',
                [$playerId, $key]
            );
            if ($seen !== null) {
                if ($seen['request_hash'] !== $hash) {
                    throw new ApiError(ApiError::IDEMPOTENCY_MISMATCH, '이미 다른 일에 쓴 요청 번호예요.');
                }
                $response = json_decode($seen['response'], true);
                $response['duplicate'] = true;
                return $response;
            }
            $response = $work();
            $this->db->exec(
                'INSERT INTO idempotency_keys (player_id, idem_key, endpoint, request_hash, status_code, response, created_at)'
                . ' VALUES (?, ?, ?, ?, 200, ?, ?)',
                [$playerId, $key, $endpoint, $hash, json_encode($response, JSON_UNESCAPED_UNICODE), Clock::sql()]
            );
            return $response;
        });
    }

    /** 오래된 키를 정리한다 (7일). 예약 작업이 부른다. */
    public function purge($days = 7)
    {
        return $this->db->exec('DELETE FROM idempotency_keys WHERE created_at < ?', [Clock::sql(Clock::now() - $days * 86400)]);
    }
}
