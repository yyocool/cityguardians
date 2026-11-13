<?php
namespace CG\Service;

use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Db;

/** 요청 횟수 제한. 정해진 시간 창 안에서 같은 주소의 요청 수를 센다. 주소는 해시로만 저장한다. */
final class RateLimit
{
    /** @var Db */
    private $db;

    public function __construct(Db $db)
    {
        $this->db = $db;
    }

    /** 한도를 넘으면 RATE_LIMITED 오류. */
    public function hit($kind, $subject, $limit, $windowSeconds)
    {
        $bucket = $kind . ':' . substr(hash('sha256', (string)$subject), 0, 32);
        $window = Clock::sql(Clock::now() - Clock::now() % $windowSeconds);
        $this->db->exec('INSERT IGNORE INTO rate_limits (bucket, window_start, hits) VALUES (?, ?, 0)', [$bucket, $window]);
        $this->db->exec('UPDATE rate_limits SET hits = hits + 1 WHERE bucket = ? AND window_start = ?', [$bucket, $window]);
        $hits = (int)$this->db->val('SELECT hits FROM rate_limits WHERE bucket = ? AND window_start = ?', [$bucket, $window]);
        if ($hits > $limit) {
            throw new ApiError(ApiError::RATE_LIMITED, '요청이 너무 많아요. 잠시 뒤에 다시 시도해 주세요.');
        }
    }

    /** 지난 기록을 정리한다. 예약 작업이 부른다. */
    public function purge()
    {
        return $this->db->exec('DELETE FROM rate_limits WHERE window_start < ?', [Clock::sql(Clock::now() - 2 * 86400)]);
    }
}
