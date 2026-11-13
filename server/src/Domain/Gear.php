<?php
namespace CG\Domain;

/**
 * 전투 장비의 구매·강화·장착 규칙 (docs/EQUIPMENT_SYSTEM.md). 클라이언트의 GearRules 와 같다.
 *
 * - 무료 기본 장비(priceGc 0)는 사지 않아도 가진 것으로 본다. 모든 핵심 전투에 기본 장비로 참여할 수 있다.
 * - 비싼 장비가 승리를 보장하지 않게 강화 효과는 단계마다 10%로 작게 두었다.
 * - 장비는 GC(게임 안에서 번 돈)로만 산다. 현금으로 사는 길은 없다.
 *
 * 장비 정의는 연관 배열이고 키는 클라이언트 GearDef 의 필드 이름과 같다.
 * 가진 장비 $owned 는 [코드 => 강화 단계] 이다.
 */
final class Gear
{
    const UPGRADE_BONUS_PERCENT = 10;

    const SLOT_PRIMARY = 'primary';
    const SLOT_GADGET = 'gadget';
    const SLOT_SUPPORT = 'support';

    const UNKNOWN = 'unknown_gear';
    const COMING_SOON = 'coming_soon';
    const ALREADY_OWNED = 'already_owned';
    const LEVEL_TOO_LOW = 'level_too_low';
    const INSUFFICIENT_GC = 'insufficient_gc';
    const NOT_OWNED = 'not_owned';
    const MAX_LEVEL = 'max_level';
    const NO_UPGRADE = 'no_upgrade';
    const WRONG_SLOT = 'wrong_slot';
    const WRONG_MODE = 'wrong_mode';

    public static function slots()
    {
        return [self::SLOT_PRIMARY, self::SLOT_GADGET, self::SLOT_SUPPORT];
    }

    public static function modes()
    {
        return [Invasion::MODE_DEFENSE, Invasion::MODE_HUNT, Invasion::MODE_FPS];
    }

    public static function slotName($slot)
    {
        switch ($slot) {
            case self::SLOT_PRIMARY: return '주 무기';
            case self::SLOT_GADGET: return '보조 장치';
            case self::SLOT_SUPPORT: return '지원 장비';
            default: return '장비';
        }
    }

    public static function message($reason, $def = null)
    {
        switch ($reason) {
            case self::UNKNOWN: return '없는 장비예요.';
            case self::COMING_SOON: return '아직 준비 중인 장비예요.';
            case self::ALREADY_OWNED: return '이미 가진 장비예요.';
            case self::LEVEL_TOO_LOW:
                return $def ? '레벨 ' . $def['unlockLevel'] . '부터 살 수 있어요.' : '레벨이 모자라요.';
            case self::INSUFFICIENT_GC: return 'GC가 모자라요.';
            case self::NOT_OWNED: return '가지고 있지 않은 장비예요.';
            case self::MAX_LEVEL: return '더 강화할 수 없어요.';
            case self::NO_UPGRADE: return '강화할 수 없는 장비예요.';
            case self::WRONG_SLOT: return '그 칸에 낄 수 없는 장비예요.';
            case self::WRONG_MODE: return '그 전투에서는 쓸 수 없는 장비예요.';
            default: return '처리할 수 없어요.';
        }
    }

    public static function isFree(array $def)
    {
        return $def['priceGc'] <= 0;
    }

    public static function usableIn(array $def, $mode)
    {
        return !empty($def['modes']) && in_array($mode, $def['modes'], true);
    }

    /** 가진 장비의 강화 단계. 기본 장비는 사지 않아도 1이다. 없으면 0. */
    public static function levelOf(array $owned, $def)
    {
        if (!$def) {
            return 0;
        }
        if (isset($owned[$def['code']])) {
            return max(1, min($owned[$def['code']], max(1, $def['maxLevel'])));
        }
        return self::isFree($def) && empty($def['comingSoon']) ? 1 : 0;
    }

    public static function owns(array $owned, $def)
    {
        return self::levelOf($owned, $def) > 0;
    }

    /** 살 수 없는 이유. 살 수 있으면 null. */
    public static function checkBuy($def, array $owned, $playerLevel, $balance)
    {
        if (!$def) {
            return self::UNKNOWN;
        }
        if (!empty($def['comingSoon'])) {
            return self::COMING_SOON;
        }
        if (self::owns($owned, $def)) {
            return self::ALREADY_OWNED;
        }
        if ($playerLevel < $def['unlockLevel']) {
            return self::LEVEL_TOO_LOW;
        }
        if ($balance < $def['priceGc']) {
            return self::INSUFFICIENT_GC;
        }
        return null;
    }

    /** 지금 단계에서 다음 단계로 강화하는 값. */
    public static function upgradeCost($def, $currentLevel)
    {
        return $def ? max(0, $def['upgradeBaseGc']) * max(1, $currentLevel) : 0;
    }

    /** 강화할 수 없는 이유. 강화할 수 있으면 null. */
    public static function checkUpgrade($def, array $owned, $balance)
    {
        if (!$def) {
            return self::UNKNOWN;
        }
        if (!empty($def['comingSoon'])) {
            return self::COMING_SOON;
        }
        $level = self::levelOf($owned, $def);
        if ($level <= 0) {
            return self::NOT_OWNED;
        }
        if ($def['maxLevel'] <= 1 || $def['upgradeBaseGc'] <= 0) {
            return self::NO_UPGRADE;
        }
        if ($level >= $def['maxLevel']) {
            return self::MAX_LEVEL;
        }
        if ($balance < self::upgradeCost($def, $level)) {
            return self::INSUFFICIENT_GC;
        }
        return null;
    }

    /** 낄 수 없는 이유. 낄 수 있으면 null. */
    public static function checkEquip($def, array $owned, $mode, $slot)
    {
        if (!$def) {
            return self::UNKNOWN;
        }
        if (!empty($def['comingSoon'])) {
            return self::COMING_SOON;
        }
        if (!in_array($mode, self::modes(), true) || !self::usableIn($def, $mode)) {
            return self::WRONG_MODE;
        }
        if (!in_array($slot, self::slots(), true) || $def['slot'] !== $slot) {
            return self::WRONG_SLOT;
        }
        if (!self::owns($owned, $def)) {
            return self::NOT_OWNED;
        }
        return null;
    }

    /** C# 의 Math.Round 는 정확히 절반일 때 짝수 쪽으로 간다. 같은 결과가 나오게 맞춘다. */
    private static function scale($value, $percent)
    {
        return (int)round($value * $percent / 100.0, 0, PHP_ROUND_HALF_EVEN);
    }

    public static function damage($def, $level)
    {
        if (!$def) {
            return 0;
        }
        $scaled = self::scale($def['damage'], 100 + self::UPGRADE_BONUS_PERCENT * (max(1, $level) - 1));
        return max($def['damage'] > 0 ? 1 : 0, $scaled);
    }

    public static function effectValue($def, $level)
    {
        return $def ? self::scale($def['effectValue'], 100 + self::UPGRADE_BONUS_PERCENT * (max(1, $level) - 1)) : 0;
    }

    /**
     * 그 모드의 그 칸에 낀 장비. 고른 적이 없거나 고른 장비를 더는 쓸 수 없으면 무료 기본 장비를 돌려준다.
     * 기본 장비가 없는 칸은 null (비어 있다).
     *
     * @param array[] $gearList 장비 정의 (정렬된 순서).
     * @param array $loadouts ["모드:칸" => 장비 코드]
     */
    public static function equipped(array $gearList, array $owned, array $loadouts, $mode, $slot)
    {
        $key = $mode . ':' . $slot;
        if (!empty($loadouts[$key])) {
            foreach ($gearList as $def) {
                if ($def['code'] === $loadouts[$key]) {
                    if (self::checkEquip($def, $owned, $mode, $slot) === null) {
                        return $def;
                    }
                    break;
                }
            }
        }
        foreach ($gearList as $def) {
            if (self::isFree($def) && empty($def['comingSoon']) && $def['slot'] === $slot && self::usableIn($def, $mode)) {
                return $def;
            }
        }
        return null;
    }
}
