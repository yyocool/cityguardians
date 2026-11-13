<?php
namespace CG\Domain;

/** 도시 간 지원 규칙. 클라이언트의 SupportRules 와 같다. 소속 도시는 바뀌지 않는다. */
final class Support
{
    const OPEN = 'open';
    const RESOLVED = 'resolved';
    const EXPIRED = 'expired';

    const NOT_RESIDENT = 'not_resident';
    const OWN_CITY = 'own_city';
    const NOT_OPEN = 'not_open';
    const TOO_FAR = 'too_far';

    public static function message($reason)
    {
        switch ($reason) {
            case self::NOT_RESIDENT: return '전입신고를 한 가디언만 다른 도시를 지원할 수 있어요.';
            case self::OWN_CITY: return '우리 도시는 출동 게이트에서 바로 지킬 수 있어요.';
            case self::NOT_OPEN: return '이미 끝난 지원 요청이에요.';
            case self::TOO_FAR: return '같은 시·도의 도시만 지원할 수 있어요.';
            default: return '지원할 수 없어요.';
        }
    }

    /** 지원 요청을 새로 열어야 하는가. */
    public static function shouldOpen(array $policy, $riskCenti, $hasOpen)
    {
        return !$hasOpen && Risk::toPoints($riskCenti) >= $policy['supportOpenAt'];
    }

    /** 위험도가 충분히 내려가 요청이 해결되었는가. */
    public static function isResolved(array $policy, $riskCenti)
    {
        return Risk::toPoints($riskCenti) < $policy['supportCloseBelow'];
    }

    /**
     * 지원에 나설 수 없는 이유. 나설 수 있으면 null.
     * "주변 도시"는 지금은 같은 시·도다.
     *
     * @param array|null $request ['regionCode', 'status']
     * @param string|null $homeSido 소속 도시의 시·도 코드. 목록에 없는 도시면 null.
     * @param string|null $targetSido 요청한 도시의 시·도 코드.
     */
    public static function checkJoin($request, $homeRegionCode, $homeSido, $targetSido)
    {
        if ($homeRegionCode === null || $homeRegionCode === '') {
            return self::NOT_RESIDENT;
        }
        if (!$request || $request['status'] !== self::OPEN) {
            return self::NOT_OPEN;
        }
        if ($request['regionCode'] === $homeRegionCode) {
            return self::OWN_CITY;
        }
        if ($homeSido === null || $targetSido === null || $homeSido !== $targetSido) {
            return self::TOO_FAR;
        }
        return null;
    }
}
