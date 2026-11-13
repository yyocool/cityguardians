<?php
namespace CG\Domain;

/**
 * 침공 규칙. 클라이언트의 CityGuardians.Domain.InvasionRules 와 같은 계산이다.
 * 판정은 난수가 아니라 (씨앗, 도시, 판정 순번)으로 정해지는 값이라, 같은 판정을 두 번 돌려도 결과가 같다.
 */
final class Invasion
{
    const OUTSKIRTS = 'outskirts';
    const INTRUSION = 'intrusion';
    const ZERO = 'zero';

    const ACTIVE = 'active';
    const REPELLED = 'repelled';
    const NPC_HELD = 'npc_held';
    const FAILED = 'failed';

    const MODE_DEFENSE = 'defense';
    const MODE_HUNT = 'hunt';
    const MODE_FPS = 'fps';

    /** 판정 순번 0의 시각: 2026-01-01T00:00:00Z. */
    const EPOCH = 1767225600;

    public static function kindName($kind)
    {
        switch ($kind) {
            case self::INTRUSION: return '시내 침입';
            case self::ZERO: return 'ZERO 이벤트';
            default: return '외곽 침공';
        }
    }

    public static function statusName($status)
    {
        switch ($status) {
            case self::REPELLED: return '가디언이 격퇴';
            case self::NPC_HELD: return 'NPC 수비대가 방어';
            case self::FAILED: return '방어 실패';
            default: return '진행 중';
        }
    }

    /** 32비트 부호 없는 곱셈. PHP 의 정수(64비트)가 넘치지 않게 나눠서 곱한다. */
    private static function mul32($a, $b)
    {
        $low = ($a & 0xFFFF) * $b;
        $high = ((($a >> 16) & 0xFFFF) * $b) & 0xFFFF;
        return ($low + ($high << 16)) & 0xFFFFFFFF;
    }

    /**
     * 같은 입력에 늘 같은 값을 내는 판정 (0 이상 modulo 미만).
     */
    public static function roll($seed, $regionCode, $tick, $salt, $modulo)
    {
        if ($modulo <= 0) {
            return 0;
        }
        $h = 2166136261;
        $h = self::mul32($h ^ ($seed & 0xFFFFFFFF), 16777619);
        $code = (string)$regionCode;
        for ($i = 0, $n = strlen($code); $i < $n; $i++) {
            $h = self::mul32($h ^ ord($code[$i]), 16777619);
        }
        $h = self::mul32($h ^ ($tick & 0xFFFFFFFF), 16777619);
        $h = self::mul32($h ^ (($tick >> 32) & 0xFFFFFFFF), 16777619);
        $h = self::mul32($h ^ ($salt & 0xFFFFFFFF), 16777619);
        $h ^= $h >> 16;
        $h = self::mul32($h, 0x7feb352d);
        $h ^= $h >> 15;
        $h = self::mul32($h, 0x846ca68b);
        $h ^= $h >> 16;
        return $h % $modulo;
    }

    /** 시각(유닉스 초)을 판정 순번으로 바꾼다. */
    public static function tickOf($unix, array $policy)
    {
        $seconds = $unix - self::EPOCH;
        // C# 의 (long)TotalMinutes 는 0 쪽으로 버린다.
        $minutes = $seconds >= 0 ? intdiv($seconds, 60) : -intdiv(-$seconds, 60);
        return intdiv($minutes, max(1, $policy['tickMinutes']));
    }

    /** 판정 순번이 시작되는 시각(유닉스 초). */
    public static function tickStart($tick, array $policy)
    {
        return self::EPOCH + $tick * max(1, $policy['tickMinutes']) * 60;
    }

    /**
     * 이번 판정에서 그 도시에 침공이 시작되는가. 시작되면 이벤트를, 아니면 null 을 돌려준다.
     * required 는 호출한 쪽이 채운다.
     */
    public static function tryStart(array $policy, $regionCode, $tick, $seed)
    {
        if (self::roll($seed, $regionCode, $tick, 1, 1000) >= $policy['invasionChancePermille']) {
            return null;
        }
        $kindRoll = self::roll($seed, $regionCode, $tick, 2, 1000);
        if ($kindRoll < $policy['zeroEventPermille']) {
            $kind = self::ZERO;
        } elseif ($kindRoll < $policy['zeroEventPermille'] + $policy['intrusionPermille']) {
            $kind = self::INTRUSION;
        } else {
            $kind = self::OUTSKIRTS;
        }
        $severityRoll = self::roll($seed, $regionCode, $tick, 3, 100);
        $severity = $kind === self::ZERO ? 3 : ($severityRoll < 60 ? 1 : ($severityRoll < 90 ? 2 : 3));
        $start = self::tickStart($tick, $policy);
        return [
            'id' => 'inv_' . $regionCode . '_' . $tick,
            'regionCode' => $regionCode,
            'kind' => $kind,
            'severity' => $severity,
            'status' => self::ACTIVE,
            'startUnix' => $start,
            'endUnix' => $start + max(1, $policy['invasionMinutes']) * 60,
        ];
    }

    /** 막아 내는 데 필요한 승리 수. 활성 가디언이 많은 도시는 그만큼 더 필요하다 (도시 크기 보정). */
    public static function requiredWins(array $policy, $severity, $activePlayers)
    {
        $norm = max(1, $policy['activePlayerNorm']);
        $groups = max(1, intdiv($activePlayers + $norm - 1, $norm));
        return max(1, $severity) * $groups;
    }

    public static function startReason($kind)
    {
        return $kind === self::ZERO ? Risk::ZERO_EVENT : Risk::INVASION_STARTED;
    }

    /** 그 전투 모드의 승리가 이 침공을 막는 데 쓰이는가. */
    public static function counts($invasion, $modeCode)
    {
        if (!$invasion || $invasion['status'] !== self::ACTIVE) {
            return false;
        }
        switch ($invasion['kind']) {
            case self::OUTSKIRTS: return $modeCode === self::MODE_DEFENSE;
            case self::INTRUSION: return $modeCode === self::MODE_FPS;
            case self::ZERO: return $modeCode === self::MODE_DEFENSE || $modeCode === self::MODE_FPS;
            default: return false;
        }
    }

    public static function winReason($modeCode)
    {
        return $modeCode === self::MODE_FPS ? Risk::FPS_REPELLED : Risk::DEFENSE_WON;
    }

    public static function repelReason($kind)
    {
        return $kind === self::ZERO ? Risk::SPECIAL_MISSION : Risk::INVASION_REPELLED;
    }

    public static function failReason($kind)
    {
        return $kind === self::OUTSKIRTS ? Risk::DEFENSE_FAILED : Risk::INTRUSION_FAILED;
    }

    /** 제한 시간이 끝난 침공을 NPC 수비대가 막을 확률 (%). */
    public static function npcHoldPercent(array $policy, $defensePower, array $invasion)
    {
        $bonus = min(
            max(0, $policy['npcHoldMaxBonusPercent']),
            intdiv(max(0, $defensePower - $policy['baseGarrisonPower']), max(1, $policy['defensePowerPerPercent']))
        );
        $penalty = $policy['npcHoldSeverityPenaltyPercent'] * (max(1, $invasion['severity']) - 1);
        // 플레이어가 조금이라도 싸웠다면 그만큼 NPC가 막기 쉬워진다.
        $helped = $invasion['required'] > 0
            ? intdiv(20 * min($invasion['progress'], $invasion['required']), $invasion['required'])
            : 0;
        $percent = $policy['npcHoldBasePercent'] + $bonus - $penalty + $helped;
        return $percent < 5 ? 5 : ($percent > 95 ? 95 : $percent);
    }

    /** 제한 시간이 끝난 침공의 결과: NPC 수비대가 막았거나(npc_held) 실패했다(failed). */
    public static function resolveExpired(array $policy, array $invasion, $defensePower, $seed, $tick)
    {
        $roll = self::roll($seed, $invasion['regionCode'], $tick, 4, 100);
        return $roll < self::npcHoldPercent($policy, $defensePower, $invasion) ? self::NPC_HELD : self::FAILED;
    }
}
