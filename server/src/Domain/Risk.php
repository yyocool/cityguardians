<?php
namespace CG\Domain;

/**
 * 도시 위험도 규칙 (docs/CITY_RISK_SYSTEM.md).
 * 클라이언트의 CityGuardians.Domain.CityRiskRules / RiskPolicy 와 같은 계산이다.
 * 같은 입력에 같은 결과가 나오는지는 tests/RulesParityTest.php 가 C# 에서 뽑은 표본으로 확인한다.
 *
 * - 위험도는 1/100 단위(0~10000)로 저장하고 화면에는 0~100으로 보여 준다.
 * - 위험도를 올리는 것은 시스템 사건뿐이다. 플레이어의 활동은 내리기만 한다.
 * - 이 파일은 지갑·은행·신용점수를 참조하지 않는다.
 *
 * 정책은 연관 배열이다. 키는 클라이언트 RiskPolicy 의 필드 이름과 같고, weights 는 [사유 => 변화량(1/100)] 이다.
 */
final class Risk
{
    const MAX_CENTI = 10000;

    // 단계
    const SAFE = 'safe';
    const CAUTION = 'caution';
    const DANGER = 'danger';
    const EMERGENCY = 'emergency';

    // 올리는 사건 (시스템)
    const INVASION_STARTED = 'invasion_started';
    const DEFENSE_FAILED = 'defense_failed';
    const INTRUSION_FAILED = 'intrusion_failed';
    const FACILITY_DAMAGED = 'facility_damaged';
    const EMERGENCY_UNRESOLVED = 'emergency_unresolved';
    const ZERO_EVENT = 'zero_event';
    // 내리는 활동
    const DEFENSE_WON = 'defense_won';
    const HUNT_COMPLETED = 'hunt_completed';
    const FPS_REPELLED = 'fps_repelled';
    const FACILITY_REPAIRED = 'facility_repaired';
    const JOINT_DEFENSE = 'joint_defense';
    const SPECIAL_MISSION = 'special_mission';
    const INVASION_REPELLED = 'invasion_repelled';
    const NPC_HELD = 'npc_held';
    const DAILY_STABILIZE = 'daily_stabilize';
    const ADMIN_ADJUST = 'admin_adjust';

    // 위험도를 바꾼 주체
    const ACTOR_PLAYER = 'player';
    const ACTOR_SUPPORTER = 'supporter';
    const ACTOR_NPC = 'npc';
    const ACTOR_SYSTEM = 'system';
    const ACTOR_ADMIN = 'admin';

    /** 관리자가 가중치를 조정할 수 있는 사유와 방향 (+1 올림, -1 내림). */
    public static function weightReasons()
    {
        return [
            self::INVASION_STARTED => 1, self::DEFENSE_FAILED => 1, self::INTRUSION_FAILED => 1,
            self::FACILITY_DAMAGED => 1, self::EMERGENCY_UNRESOLVED => 1, self::ZERO_EVENT => 1,
            self::DEFENSE_WON => -1, self::HUNT_COMPLETED => -1, self::FPS_REPELLED => -1,
            self::FACILITY_REPAIRED => -1, self::JOINT_DEFENSE => -1, self::SPECIAL_MISSION => -1,
            self::INVASION_REPELLED => -1, self::NPC_HELD => -1,
        ];
    }

    public static function stageName($stage)
    {
        switch ($stage) {
            case self::CAUTION: return '주의';
            case self::DANGER: return '위험';
            case self::EMERGENCY: return '긴급';
            default: return '안전';
        }
    }

    /** 색을 보지 않아도 구분되는 모양 표시. */
    public static function stageIcon($stage)
    {
        switch ($stage) {
            case self::CAUTION: return '▲';
            case self::DANGER: return '◆';
            case self::EMERGENCY: return '■';
            default: return '●';
        }
    }

    public static function stageOrder($stage)
    {
        switch ($stage) {
            case self::CAUTION: return 1;
            case self::DANGER: return 2;
            case self::EMERGENCY: return 3;
            default: return 0;
        }
    }

    public static function stageMood($stage)
    {
        switch ($stage) {
            case self::CAUTION: return '순찰이 늘었어요. 사냥 임무가 더 많이 나와요.';
            case self::DANGER: return '경보가 울려요. 방어전과 긴급 출동이 필요해요.';
            case self::EMERGENCY: return '도시가 위험해요! 주변 도시에 지원을 요청했어요.';
            default: return '도시가 평화로워요.';
        }
    }

    public static function reasonName($reason)
    {
        switch ($reason) {
            case self::INVASION_STARTED: return 'AI 침공 발생';
            case self::DEFENSE_FAILED: return '외곽 방어 실패';
            case self::INTRUSION_FAILED: return '시내 침입';
            case self::FACILITY_DAMAGED: return '주요 시설 피해';
            case self::EMERGENCY_UNRESOLVED: return '미해결 긴급 임무';
            case self::ZERO_EVENT: return 'ZERO 이벤트';
            case self::DEFENSE_WON: return '타워디펜스 성공';
            case self::HUNT_COMPLETED: return '사냥 임무 완료';
            case self::FPS_REPELLED: return '시내 침입 격퇴';
            case self::FACILITY_REPAIRED: return '방어시설 복구';
            case self::JOINT_DEFENSE: return '도시 공동 방어';
            case self::SPECIAL_MISSION: return '특별 위험 제거';
            case self::INVASION_REPELLED: return '침공 격퇴 완료';
            case self::NPC_HELD: return 'NPC 수비대 방어';
            case self::DAILY_STABILIZE: return 'NPC 순찰';
            case self::ADMIN_ADJUST: return '관리자 조정';
            default: return '기타';
        }
    }

    public static function actorName($actor)
    {
        switch ($actor) {
            case self::ACTOR_PLAYER: return '가디언';
            case self::ACTOR_SUPPORTER: return '지원 가디언';
            case self::ACTOR_NPC: return 'NPC 수비대';
            case self::ACTOR_ADMIN: return '관리자';
            default: return '시스템';
        }
    }

    /** 초기 정책. 클라이언트의 RiskPolicy.CreateDefault() 와 같다. */
    public static function defaultPolicy()
    {
        return [
            'version' => 1,
            'weights' => [
                self::INVASION_STARTED => 800,
                self::DEFENSE_FAILED => 300,
                self::INTRUSION_FAILED => 300,
                self::FACILITY_DAMAGED => 200,
                self::EMERGENCY_UNRESOLVED => 400,
                self::ZERO_EVENT => 1000,
                self::DEFENSE_WON => -600,
                self::HUNT_COMPLETED => -100,
                self::FPS_REPELLED => -800,
                self::FACILITY_REPAIRED => -200,
                self::JOINT_DEFENSE => -300,
                self::SPECIAL_MISSION => -1000,
                self::INVASION_REPELLED => -300,
                self::NPC_HELD => -700,
            ],
            'cautionAt' => 25,
            'dangerAt' => 50,
            'emergencyAt' => 75,
            'initialRisk' => 20,
            'initialSpread' => 12,
            'activePlayerNorm' => 20,
            'activeDays' => 7,
            'huntKillsPerStep' => 10,
            'huntHighRiskPercent' => 200,
            'dailyPlayerReduceCap' => 20,
            'patrolPercent' => 50,
            'tickMinutes' => 60,
            'invasionChancePermille' => 40,
            'invasionMinutes' => 180,
            'intrusionPermille' => 350,
            'zeroEventPermille' => 20,
            'npcHoldBasePercent' => 80,
            'defensePowerPerPercent' => 20,
            'npcHoldMaxBonusPercent' => 20,
            'npcHoldSeverityPenaltyPercent' => 10,
            'dailyStabilizePercent' => 20,
            'baseGarrisonPower' => 100,
            'defensePointsPowerCap' => 300,
            'supportOpenAt' => 75,
            'supportCloseBelow' => 50,
            'supportMinutes' => 360,
            'contribDefenseWin' => 10,
            'contribFpsWin' => 12,
            'contribHuntStep' => 1,
            'contribRepair' => 5,
            'contribPerFund100Gc' => 2,
            'contribSeverityBonusPercent' => 50,
            'dailyRepairLimit' => 3,
            'dailyFundLimitGc' => 2000,
        ];
    }

    /**
     * 관리자가 고칠 수 있는 값의 범위 [최소, 최대]. 범위를 벗어난 값은 저장하지 않는다.
     * 범위는 "게임이 망가지지 않는 한도"이고, 알맞은 값은 플레이테스트로 정한다.
     */
    public static function policyRanges()
    {
        return [
            'cautionAt' => [1, 98], 'dangerAt' => [2, 99], 'emergencyAt' => [3, 100],
            'initialRisk' => [0, 100], 'initialSpread' => [0, 50],
            'activePlayerNorm' => [1, 10000], 'activeDays' => [1, 90],
            'huntKillsPerStep' => [1, 1000], 'huntHighRiskPercent' => [100, 1000],
            'dailyPlayerReduceCap' => [0, 100], 'patrolPercent' => [0, 100],
            'tickMinutes' => [1, 1440], 'invasionChancePermille' => [0, 1000], 'invasionMinutes' => [1, 10080],
            'intrusionPermille' => [0, 1000], 'zeroEventPermille' => [0, 1000],
            'npcHoldBasePercent' => [0, 100], 'defensePowerPerPercent' => [1, 100000],
            'npcHoldMaxBonusPercent' => [0, 100], 'npcHoldSeverityPenaltyPercent' => [0, 100],
            'dailyStabilizePercent' => [0, 100], 'baseGarrisonPower' => [0, 100000], 'defensePointsPowerCap' => [0, 100000],
            'supportOpenAt' => [1, 100], 'supportCloseBelow' => [0, 100], 'supportMinutes' => [1, 10080],
            'contribDefenseWin' => [0, 1000], 'contribFpsWin' => [0, 1000], 'contribHuntStep' => [0, 1000],
            'contribRepair' => [0, 1000], 'contribPerFund100Gc' => [0, 1000], 'contribSeverityBonusPercent' => [0, 500],
            'dailyRepairLimit' => [0, 100], 'dailyFundLimitGc' => [0, 1000000],
        ];
    }

    /**
     * 저장된 정책에 빠진 값을 기본값으로 채운다. 새 값이 생겨도 옛 정책이 그대로 동작한다.
     */
    public static function fillPolicy($stored)
    {
        $policy = self::defaultPolicy();
        if (!is_array($stored)) {
            return $policy;
        }
        foreach ($stored as $key => $value) {
            if ($key === 'weights') {
                if (is_array($value)) {
                    foreach ($value as $reason => $delta) {
                        $policy['weights'][(string)$reason] = (int)$delta;
                    }
                }
            } elseif (array_key_exists($key, $policy)) {
                $policy[$key] = (int)$value;
            }
        }
        return $policy;
    }

    /**
     * 정책이 범위에 맞는지 검사한다.
     * @return string[] 문제 목록. 비어 있으면 저장해도 된다.
     */
    public static function validatePolicy(array $policy)
    {
        $problems = [];
        foreach (self::policyRanges() as $key => $range) {
            if (!isset($policy[$key]) || !is_int($policy[$key])) {
                $problems[] = "$key: 정수가 아닙니다.";
                continue;
            }
            if ($policy[$key] < $range[0] || $policy[$key] > $range[1]) {
                $problems[] = "$key: {$range[0]} ~ {$range[1]} 사이여야 합니다.";
            }
        }
        if (!$problems) {
            if (!($policy['cautionAt'] < $policy['dangerAt'] && $policy['dangerAt'] < $policy['emergencyAt'])) {
                $problems[] = '단계 경계는 주의 < 위험 < 긴급 순서여야 합니다.';
            }
            if ($policy['supportCloseBelow'] > $policy['supportOpenAt']) {
                $problems[] = '지원 요청이 닫히는 값은 열리는 값보다 클 수 없습니다.';
            }
            if ($policy['intrusionPermille'] + $policy['zeroEventPermille'] > 1000) {
                $problems[] = '시내 침입과 ZERO 이벤트의 비율을 더한 값이 1000을 넘을 수 없습니다.';
            }
        }
        foreach (self::weightReasons() as $reason => $direction) {
            $delta = isset($policy['weights'][$reason]) ? $policy['weights'][$reason] : null;
            if (!is_int($delta)) {
                $problems[] = "가중치 $reason: 정수가 아닙니다.";
                continue;
            }
            // 올리는 사건은 0 이상, 내리는 활동은 0 이하. 플레이어의 활동이 위험도를 올리게 만들 수 없다.
            if ($direction > 0 && ($delta < 0 || $delta > self::MAX_CENTI)) {
                $problems[] = "가중치 $reason: 0 ~ " . self::MAX_CENTI . ' 사이여야 합니다.';
            }
            if ($direction < 0 && ($delta > 0 || $delta < -self::MAX_CENTI)) {
                $problems[] = "가중치 $reason: -" . self::MAX_CENTI . ' ~ 0 사이여야 합니다.';
            }
        }
        return $problems;
    }

    public static function delta(array $policy, $reason)
    {
        return isset($policy['weights'][$reason]) ? (int)$policy['weights'][$reason] : 0;
    }

    public static function clamp($centi)
    {
        return $centi < 0 ? 0 : ($centi > self::MAX_CENTI ? self::MAX_CENTI : (int)$centi);
    }

    /** 화면에 보이는 값 (0~100). */
    public static function toPoints($centi)
    {
        return intdiv(self::clamp($centi), 100);
    }

    public static function stageOf($centi, array $policy)
    {
        $points = self::toPoints($centi);
        if ($points >= $policy['emergencyAt']) {
            return self::EMERGENCY;
        }
        if ($points >= $policy['dangerAt']) {
            return self::DANGER;
        }
        if ($points >= $policy['cautionAt']) {
            return self::CAUTION;
        }
        return self::SAFE;
    }

    /**
     * 한 사람의 활동이 반영되는 비율(%). 활성 가디언이 기준 수 이하면 100, 넘으면 그만큼 나뉜다.
     */
    public static function scalePercent(array $policy, $activePlayers)
    {
        $norm = max(1, $policy['activePlayerNorm']);
        if ($activePlayers <= $norm) {
            return 100;
        }
        return max(1, intdiv(100 * $norm, $activePlayers));
    }

    /** 시스템 사건을 반영한다. quantity 는 침공 규모 같은 배수다. */
    public static function applySystem($current, $reason, $quantity, array $policy)
    {
        $delta = self::delta($policy, $reason) * max(0, $quantity);
        return self::finish($current, $current + $delta, $reason, $policy);
    }

    /** 정해진 양만큼 바꾼다 (관리자 조정, 하루 한 번의 순찰). */
    public static function applyExact($current, $reason, $deltaCenti, array $policy)
    {
        return self::finish($current, $current + $deltaCenti, $reason, $policy);
    }

    /**
     * 플레이어의 활동을 반영한다. 내리는 방향만 반영되고, 활성 가디언 수로 나뉘며,
     * 한 사람의 하루 한도를 넘지 않는다.
     *
     * @param int $unitsPercent 활동의 크기 (%). 100이 한 번이다.
     * @param int $reducedToday 이 플레이어가 오늘 이미 낮춘 양 (1/100 단위).
     * @return array [변화, 오늘 낮춘 양(갱신된 값)]
     */
    public static function applyPlayer($current, $reason, $unitsPercent, array $policy, $activePlayers, $reducedToday)
    {
        $newReduced = max(0, $reducedToday);
        $perUnit = self::delta($policy, $reason);
        // 정책을 잘못 고쳐 양수가 되더라도 플레이어의 활동이 위험도를 올리지는 않는다.
        if ($perUnit >= 0 || $unitsPercent <= 0) {
            return [self::finish($current, $current, $reason, $policy), $newReduced];
        }
        $wanted = intdiv(-$perUnit * $unitsPercent, 100);
        $wanted = intdiv($wanted * self::scalePercent($policy, $activePlayers), 100);
        $room = max(0, $policy['dailyPlayerReduceCap'] * 100 - $newReduced);
        $applied = min($wanted, $room, self::clamp($current));
        if ($applied < 0) {
            $applied = 0;
        }
        $newReduced += $applied;
        return [self::finish($current, $current - $applied, $reason, $policy), $newReduced];
    }

    /** 하루 한 번의 NPC 순찰이 낮추는 양 (0 이하). */
    public static function dailyStabilizeDelta($current, array $policy)
    {
        return -intdiv(self::clamp($current) * max(0, $policy['dailyStabilizePercent']), 100);
    }

    /** 도시의 방어력: NPC 수비대 + 주민이 쌓은 방어 포인트 + 도시 공용 방어시설. */
    public static function defensePower(array $policy, $defensePoints, $structurePower)
    {
        $fromPoints = min(max(0, $policy['defensePointsPowerCap']), intdiv(max(0, $defensePoints), 10));
        return $policy['baseGarrisonPower'] + $fromPoints + max(0, $structurePower);
    }

    private static function finish($before, $after, $reason, array $policy)
    {
        $b = self::clamp($before);
        $a = self::clamp($after);
        $stageBefore = self::stageOf($b, $policy);
        $stageAfter = self::stageOf($a, $policy);
        return [
            'reason' => $reason,
            'before' => $b,
            'after' => $a,
            'stageBefore' => $stageBefore,
            'stageAfter' => $stageAfter,
            'delta' => $a - $b,
            'changed' => $a !== $b,
            'stageChanged' => $stageBefore !== $stageAfter,
        ];
    }
}
