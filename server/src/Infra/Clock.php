<?php
namespace CG\Infra;

/** 서버 시계. 테스트에서는 시각을 고정하거나 앞으로 감을 수 있다. */
final class Clock
{
    /** @var int|null */
    private static $fixed = null;

    public static function now()
    {
        return self::$fixed !== null ? self::$fixed : time();
    }

    /** 테스트용. null 이면 실제 시각으로 돌아간다. */
    public static function set($unix)
    {
        self::$fixed = $unix;
    }

    public static function advance($seconds)
    {
        self::$fixed = self::now() + (int)$seconds;
    }

    /** DB에 넣는 형식 (UTC). */
    public static function sql($unix = null)
    {
        return gmdate('Y-m-d H:i:s', $unix === null ? self::now() : $unix);
    }

    /** 응답에 넣는 형식 (ISO 8601, UTC). */
    public static function iso($unix = null)
    {
        return gmdate('Y-m-d\TH:i:s\Z', $unix === null ? self::now() : $unix);
    }

    /** DB의 DATETIME(UTC) 문자열을 유닉스 시각으로. */
    public static function parse($sqlUtc)
    {
        if ($sqlUtc === null || $sqlUtc === '') {
            return 0;
        }
        return (int)strtotime($sqlUtc . ' UTC');
    }

    public static function isoFromSql($sqlUtc)
    {
        return $sqlUtc === null || $sqlUtc === '' ? null : self::iso(self::parse($sqlUtc));
    }

    /** 한국 날짜 (YYYY-MM-DD). 일일 한도와 하루 한 번의 처리는 한국 자정을 경계로 한다. */
    public static function koreaDate($unix = null)
    {
        return gmdate('Y-m-d', ($unix === null ? self::now() : $unix) + 9 * 3600);
    }
}
