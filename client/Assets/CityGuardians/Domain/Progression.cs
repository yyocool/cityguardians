using System;

namespace CityGuardians.Domain
{
    /// <summary>레벨과 경험치 계산 (docs/CHARACTER_SYSTEM.md 7장).</summary>
    public static class Progression
    {
        /// <summary>현재 레벨에서 다음 레벨까지 필요한 경험치: round(xpBase × level^xpExponent).</summary>
        public static int XpForNextLevel(int level, ProgressionConfig config)
        {
            if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
            double raw = config.xpBase * Math.Pow(level, config.xpExponent);
            return Math.Max(1, (int)Math.Round(raw, MidpointRounding.AwayFromZero));
        }

        public static int MaxHp(int level, ProgressionConfig config)
        {
            if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
            return config.baseMaxHp + (level - 1) * config.maxHpPerLevel;
        }

        /// <summary>
        /// 경험치를 더하고 오른 레벨 수를 돌려준다. 최고 레벨에서는 경험치가 더 쌓이지 않는다.
        /// </summary>
        public static int AddXp(ref int level, ref int xp, int gained, ProgressionConfig config)
        {
            if (gained < 0) throw new ArgumentOutOfRangeException(nameof(gained));
            int levelsGained = 0;
            if (level >= config.maxLevel)
            {
                xp = 0;
                return 0;
            }

            xp += gained;
            while (level < config.maxLevel)
            {
                int need = XpForNextLevel(level, config);
                if (xp < need) break;
                xp -= need;
                level++;
                levelsGained++;
            }

            if (level >= config.maxLevel) xp = 0;
            return levelsGained;
        }
    }

    /// <summary>피해 계산 (docs/COMBAT_SYSTEM.md 4장). 개인 전투와 방어전이 같은 식을 쓴다.</summary>
    public static class DamageFormula
    {
        public static int Compute(int weaponDamage, float enhanceMultiplier, int targetArmor)
        {
            if (weaponDamage < 0) throw new ArgumentOutOfRangeException(nameof(weaponDamage));
            if (enhanceMultiplier < 0f) throw new ArgumentOutOfRangeException(nameof(enhanceMultiplier));
            int scaled = (int)Math.Round(weaponDamage * (double)enhanceMultiplier, MidpointRounding.AwayFromZero);
            return Math.Max(1, scaled - Math.Max(0, targetArmor));
        }

        /// <summary>방어구로 줄일 수 있는 피해의 한도(%). 아무리 입어도 피해가 0이 되지는 않는다.</summary>
        public const int MaxDefensePercent = 80;

        /// <summary>
        /// 플레이어가 방어구를 입고 받는 피해: round(피해 × (100 − 방어)/100), 최소 1.
        /// 방어는 비율(%)이다. 빼는 방식이면 약한 적(피해 4)의 공격이 통째로 사라지기 때문이다.
        /// </summary>
        public static int Reduce(int damage, int defensePercent)
        {
            if (damage <= 0) return 0;
            int defense = Math.Min(MaxDefensePercent, Math.Max(0, defensePercent));
            return Math.Max(1, (damage * (100 - defense) + 50) / 100);
        }
    }
}
