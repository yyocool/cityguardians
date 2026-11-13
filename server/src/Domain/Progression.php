<?php
namespace CG\Domain;

/**
 * 레벨과 경험치 (docs/CHARACTER_SYSTEM.md 7장). 클라이언트의 Progression 과 같다.
 * 레벨과 경험치는 세 전투 모드가 함께 쓰는 하나의 값이다 (characters).
 */
final class Progression
{
    public static function defaultConfig()
    {
        return ['xpBase' => 50, 'xpExponent' => 1.5, 'baseMaxHp' => 100, 'maxHpPerLevel' => 10, 'maxLevel' => 50];
    }

    /** 현재 레벨에서 다음 레벨까지 필요한 경험치: round(xpBase × level^xpExponent). */
    public static function xpForNextLevel($level, array $config)
    {
        $raw = $config['xpBase'] * pow($level, $config['xpExponent']);
        return max(1, (int)round($raw));
    }

    public static function maxHp($level, array $config)
    {
        return $config['baseMaxHp'] + ($level - 1) * $config['maxHpPerLevel'];
    }

    /**
     * 경험치를 더한다. 최고 레벨에서는 경험치가 더 쌓이지 않는다.
     * @return array [레벨, 경험치, 오른 레벨 수]
     */
    public static function addXp($level, $xp, $gained, array $config)
    {
        $levelsGained = 0;
        if ($level >= $config['maxLevel']) {
            return [$level, 0, 0];
        }
        $xp += max(0, $gained);
        while ($level < $config['maxLevel']) {
            $need = self::xpForNextLevel($level, $config);
            if ($xp < $need) {
                break;
            }
            $xp -= $need;
            $level++;
            $levelsGained++;
        }
        if ($level >= $config['maxLevel']) {
            $xp = 0;
        }
        return [$level, $xp, $levelsGained];
    }
}
