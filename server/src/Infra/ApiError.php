<?php
namespace CG\Infra;

/**
 * API 오류. message 는 화면에 그대로 보여 줄 수 있는 쉬운 말로 쓴다.
 * 내부 정보(SQL, 경로, 스택)는 넣지 않는다 (docs/API_SPEC.md 13장).
 */
final class ApiError extends \RuntimeException
{
    const INVALID_REQUEST = 'INVALID_REQUEST';
    const UNAUTHORIZED = 'UNAUTHORIZED';
    const FORBIDDEN = 'FORBIDDEN';
    const SUSPENDED = 'SUSPENDED';
    /** 현실 혜택 기능이 꺼져 있다 (법률 검토와 운영 승인 전). docs/REAL_WORLD_REWARDS_POLICY.md 4장. */
    const FEATURE_DISABLED = 'FEATURE_DISABLED';
    const NOT_FOUND = 'NOT_FOUND';
    const CONFLICT = 'CONFLICT';
    const IDEMPOTENCY_MISMATCH = 'IDEMPOTENCY_MISMATCH';
    const INSUFFICIENT_GC = 'INSUFFICIENT_GC';
    const LIMIT_REACHED = 'LIMIT_REACHED';
    const LOCKED = 'LOCKED';
    const VALIDATION_FAILED = 'VALIDATION_FAILED';
    const UPDATE_REQUIRED = 'UPDATE_REQUIRED';
    const RATE_LIMITED = 'RATE_LIMITED';
    const MAINTENANCE = 'MAINTENANCE';
    const INTERNAL = 'INTERNAL_ERROR';

    private static $status = [
        self::INVALID_REQUEST => 400,
        self::UNAUTHORIZED => 401,
        self::FORBIDDEN => 403,
        self::SUSPENDED => 403,
        self::FEATURE_DISABLED => 403,
        self::NOT_FOUND => 404,
        self::CONFLICT => 409,
        self::IDEMPOTENCY_MISMATCH => 409,
        self::INSUFFICIENT_GC => 422,
        self::LIMIT_REACHED => 422,
        self::LOCKED => 422,
        self::VALIDATION_FAILED => 422,
        self::UPDATE_REQUIRED => 426,
        self::RATE_LIMITED => 429,
        self::MAINTENANCE => 503,
        self::INTERNAL => 500,
    ];

    /** @var string */
    public $errorCode;
    /** @var string|null 더 자세한 이유 코드 (예: level_too_low). 없으면 null. */
    public $reason;
    /** @var string[] 폼에 하나씩 보여 줄 문제 목록 (있을 때만). message 는 이것을 이어 붙인 것이다. */
    public $problems = [];

    public function __construct($errorCode, $message, $reason = null)
    {
        parent::__construct($message);
        $this->errorCode = $errorCode;
        $this->reason = $reason;
    }

    public function httpStatus()
    {
        return isset(self::$status[$this->errorCode]) ? self::$status[$this->errorCode] : 400;
    }
}
