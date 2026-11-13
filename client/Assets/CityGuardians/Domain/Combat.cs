using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    // 공통 전투 데이터 (docs/COMBAT_SYSTEM.md 12장, docs/EQUIPMENT_SYSTEM.md).
    // 세 전투 모드는 조작과 규칙이 다르지만 계정, 레벨, GC, 가방, 장비, 소속 도시는 하나를 함께 쓴다.
    // 여기에는 모드와 상관없이 같은 것(모드 목록, 장비, 장착)만 둔다.

    public static class CombatModeCodes
    {
        /// <summary>타워디펜스: 도시 외곽 방어.</summary>
        public const string Defense = "defense";
        /// <summary>3인칭 사냥: 도시 주변 순찰.</summary>
        public const string Hunt = "hunt";
        /// <summary>1인칭 시내 침입 격퇴.</summary>
        public const string Fps = "fps";

        public static readonly string[] All = { Defense, Hunt, Fps };

        public static bool IsKnown(string code)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i] == code) return true;
            return false;
        }
    }

    public static class CameraKinds
    {
        /// <summary>위에서 내려다보는 전략 시점 (쿼터뷰, 돌리고 당길 수 있다).</summary>
        public const string Strategy = "strategy";
        public const string ThirdPerson = "third_person";
        public const string FirstPerson = "first_person";
    }

    /// <summary>전투 모드 정의. 서버의 combat_modes 가 원본이다.</summary>
    [Serializable]
    public sealed class CombatModeDef
    {
        public string code;
        public string name;
        public string summary;
        /// <summary>이 모드가 처음 켜는 카메라 (CameraKinds).</summary>
        public string camera;
        /// <summary>화면에 보여 줄 조작 안내 한 줄.</summary>
        public string controls;
        /// <summary>관리자가 모드를 잠시 닫을 수 있다.</summary>
        public bool closed;
        public int sortOrder;
    }

    // ── 전투 장비 ───────────────────────────────────────────────

    public static class GearSlots
    {
        /// <summary>주 무기.</summary>
        public const string Primary = "primary";
        /// <summary>보조 장치 (Q).</summary>
        public const string Gadget = "gadget";
        /// <summary>지원 장비 (F).</summary>
        public const string Support = "support";

        public static readonly string[] All = { Primary, Gadget, Support };

        public static bool IsKnown(string slot)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i] == slot) return true;
            return false;
        }

        public static string DisplayName(string slot)
        {
            switch (slot)
            {
                case Primary: return "주 무기";
                case Gadget: return "보조 장치";
                case Support: return "지원 장비";
                default: return "장비";
            }
        }
    }

    public static class GearKinds
    {
        public const string Blaster = "blaster";
        public const string Rifle = "rifle";
        public const string Shockwave = "shockwave";
        public const string Emp = "emp";
        public const string Shield = "shield";
        public const string Sensor = "sensor";
        public const string Drone = "drone";
        public const string Heal = "heal";
    }

    public static class GearCodes
    {
        public const string EnergyBlaster = "gear_energy_blaster";
        public const string PrecisionRifle = "gear_precision_rifle";
        public const string ShockwaveLauncher = "gear_shockwave_launcher";
        public const string EmpDevice = "gear_emp_device";
        public const string Shield = "gear_shield";
        public const string Sensor = "gear_sensor";
        public const string SupportDrone = "gear_support_drone";
        public const string HealDevice = "gear_heal_device";
    }

    /// <summary>
    /// 전투 장비 정의. 서버의 combat_equipment 가 원본이다.
    /// 무기는 피해·연사·탄창을, 장치는 재사용 시간·효과 값을 쓴다. 쓰지 않는 값은 0이다.
    /// </summary>
    [Serializable]
    public sealed class GearDef
    {
        public string code;
        public string name;
        public string description;
        public string slot;
        public string kind;
        /// <summary>쓸 수 있는 전투 모드 (CombatModeCodes).</summary>
        public string[] modes;
        /// <summary>사는 값. 0이면 처음부터 가진 기본 장비다.</summary>
        public int priceGc;
        /// <summary>살 수 있는 레벨.</summary>
        public int unlockLevel = 1;
        /// <summary>강화 한도. 1이면 강화가 없다.</summary>
        public int maxLevel = 1;
        /// <summary>강화 한 번의 기준 값. 실제 값은 지금 단계를 곱한다 (GearRules.UpgradeCost).</summary>
        public int upgradeBaseGc;
        /// <summary>임무에 들고 나갈 때마다 드는 유지비. 지금은 모두 0이다.</summary>
        public int upkeepGc;

        // 무기
        public int damage;
        public float attacksPerSecond;
        public float range;
        public float projectileSpeed;
        /// <summary>탄창 크기. 다 쓰면 재장전한다. 탄약은 사지 않는다 (에너지 충전).</summary>
        public int clipSize;
        public float reloadSeconds;
        /// <summary>겨눌 때(우클릭) 시야가 좁아지는 비율. 1이면 그대로.</summary>
        public float aimZoom = 1f;
        /// <summary>한 번에 맞히는 정면 부채꼴 각도. 0이면 탄환 하나.</summary>
        public float coneDegrees;
        /// <summary>맞은 적을 미는 거리 (m).</summary>
        public float knockback;

        // 장치
        public float cooldownSeconds;
        public float effectSeconds;
        public float effectRadius;
        /// <summary>효과의 크기. 종류마다 뜻이 다르다 (회복량, 막아 주는 피해량, 드론의 피해 등).</summary>
        public int effectValue;

        /// <summary>정의만 있고 아직 게임에서 동작하지 않는다. 상점에 "준비 중"으로 보인다.</summary>
        public bool comingSoon;

        public bool IsFree => priceGc <= 0;
        public bool IsWeapon => slot == GearSlots.Primary;

        public bool UsableIn(string mode)
        {
            if (modes == null) return false;
            for (int i = 0; i < modes.Length; i++)
                if (modes[i] == mode) return true;
            return false;
        }
    }

    /// <summary>가진 장비 한 점. 서버의 player_combat_equipment 한 행.</summary>
    [Serializable]
    public sealed class OwnedGear
    {
        public string code;
        /// <summary>강화 단계. 1부터.</summary>
        public int level = 1;

        public OwnedGear() { }

        public OwnedGear(string code, int level)
        {
            this.code = code;
            this.level = level;
        }
    }

    /// <summary>모드별 장착 한 칸. 서버의 combat_loadouts 한 행.</summary>
    [Serializable]
    public sealed class LoadoutEntry
    {
        public string mode;
        public string slot;
        public string gearCode;
    }

    public static class GearReasons
    {
        public const string Unknown = "unknown_gear";
        public const string ComingSoon = "coming_soon";
        public const string AlreadyOwned = "already_owned";
        public const string LevelTooLow = "level_too_low";
        public const string InsufficientGc = "insufficient_gc";
        public const string NotOwned = "not_owned";
        public const string MaxLevel = "max_level";
        public const string NoUpgrade = "no_upgrade";
        public const string WrongSlot = "wrong_slot";
        public const string WrongMode = "wrong_mode";

        public static string Message(string reason)
        {
            switch (reason)
            {
                case Unknown: return "없는 장비예요.";
                case ComingSoon: return "아직 준비 중인 장비예요.";
                case AlreadyOwned: return "이미 가진 장비예요.";
                case LevelTooLow: return "레벨이 모자라요.";
                case InsufficientGc: return "GC가 모자라요.";
                case NotOwned: return "가지고 있지 않은 장비예요.";
                case MaxLevel: return "더 강화할 수 없어요.";
                case NoUpgrade: return "강화할 수 없는 장비예요.";
                case WrongSlot: return "그 칸에 낄 수 없는 장비예요.";
                case WrongMode: return "그 전투에서는 쓸 수 없는 장비예요.";
                default: return "처리할 수 없어요.";
            }
        }
    }

    /// <summary>
    /// 장비 구매·강화·장착 규칙. 실제 서버(PHP)의 검증이 이 규칙과 같아야 한다.
    /// 비싼 장비가 승리를 보장하지 않게 강화 효과는 단계마다 10%로 작게 두었다.
    /// </summary>
    public static class GearRules
    {
        /// <summary>강화 한 단계의 효과 (%). 무기는 피해가 늘고 장치는 재사용 시간이 준다.</summary>
        public const int UpgradeBonusPercent = 10;

        public static OwnedGear Find(List<OwnedGear> owned, string code)
        {
            if (owned == null) return null;
            for (int i = 0; i < owned.Count; i++)
                if (owned[i] != null && owned[i].code == code) return owned[i];
            return null;
        }

        /// <summary>가진 장비의 강화 단계. 기본 장비는 사지 않아도 1이다. 없으면 0.</summary>
        public static int LevelOf(List<OwnedGear> owned, GearDef def)
        {
            if (def == null) return 0;
            OwnedGear row = Find(owned, def.code);
            if (row != null) return Math.Max(1, Math.Min(row.level, Math.Max(1, def.maxLevel)));
            return def.IsFree && !def.comingSoon ? 1 : 0;
        }

        public static bool Owns(List<OwnedGear> owned, GearDef def) => LevelOf(owned, def) > 0;

        /// <summary>살 수 없는 이유. 살 수 있으면 null.</summary>
        public static string CheckBuy(GearDef def, List<OwnedGear> owned, int playerLevel, long balance)
        {
            if (def == null) return GearReasons.Unknown;
            if (def.comingSoon) return GearReasons.ComingSoon;
            if (Owns(owned, def)) return GearReasons.AlreadyOwned;
            if (playerLevel < def.unlockLevel) return GearReasons.LevelTooLow;
            if (balance < def.priceGc) return GearReasons.InsufficientGc;
            return null;
        }

        /// <summary>지금 단계에서 다음 단계로 강화하는 값.</summary>
        public static int UpgradeCost(GearDef def, int currentLevel) =>
            def == null ? 0 : Math.Max(0, def.upgradeBaseGc) * Math.Max(1, currentLevel);

        /// <summary>강화할 수 없는 이유. 강화할 수 있으면 null.</summary>
        public static string CheckUpgrade(GearDef def, List<OwnedGear> owned, long balance)
        {
            if (def == null) return GearReasons.Unknown;
            if (def.comingSoon) return GearReasons.ComingSoon;
            int level = LevelOf(owned, def);
            if (level <= 0) return GearReasons.NotOwned;
            if (def.maxLevel <= 1 || def.upgradeBaseGc <= 0) return GearReasons.NoUpgrade;
            if (level >= def.maxLevel) return GearReasons.MaxLevel;
            if (balance < UpgradeCost(def, level)) return GearReasons.InsufficientGc;
            return null;
        }

        /// <summary>낄 수 없는 이유. 낄 수 있으면 null.</summary>
        public static string CheckEquip(GearDef def, List<OwnedGear> owned, string mode, string slot)
        {
            if (def == null) return GearReasons.Unknown;
            if (def.comingSoon) return GearReasons.ComingSoon;
            if (!CombatModeCodes.IsKnown(mode) || !def.UsableIn(mode)) return GearReasons.WrongMode;
            if (!GearSlots.IsKnown(slot) || def.slot != slot) return GearReasons.WrongSlot;
            if (!Owns(owned, def)) return GearReasons.NotOwned;
            return null;
        }

        public static int Damage(GearDef def, int level) =>
            def == null ? 0 : Math.Max(def.damage > 0 ? 1 : 0, Scale(def.damage, 100 + UpgradeBonusPercent * (Math.Max(1, level) - 1)));

        public static int EffectValue(GearDef def, int level) =>
            def == null ? 0 : Scale(def.effectValue, 100 + UpgradeBonusPercent * (Math.Max(1, level) - 1));

        public static float Cooldown(GearDef def, int level) =>
            def == null ? 0f : def.cooldownSeconds * (100 - UpgradeBonusPercent * (Math.Max(1, level) - 1)) / 100f;

        private static int Scale(int value, int percent) => (int)Math.Round(value * (long)percent / 100.0);

        public static LoadoutEntry FindEntry(List<LoadoutEntry> loadouts, string mode, string slot)
        {
            if (loadouts == null) return null;
            for (int i = 0; i < loadouts.Count; i++)
                if (loadouts[i] != null && loadouts[i].mode == mode && loadouts[i].slot == slot) return loadouts[i];
            return null;
        }

        public static void SetEntry(List<LoadoutEntry> loadouts, string mode, string slot, string gearCode)
        {
            LoadoutEntry entry = FindEntry(loadouts, mode, slot);
            if (entry == null)
            {
                entry = new LoadoutEntry { mode = mode, slot = slot };
                loadouts.Add(entry);
            }
            entry.gearCode = gearCode;
        }

        /// <summary>
        /// 그 모드의 그 칸에 낀 장비. 고른 적이 없거나 고른 장비를 더는 쓸 수 없으면 무료 기본 장비를 돌려준다.
        /// 기본 장비가 없는 칸은 null (비어 있다).
        /// </summary>
        public static GearDef Equipped(CombatDefs combat, List<OwnedGear> owned, List<LoadoutEntry> loadouts, string mode, string slot)
        {
            if (combat == null) return null;
            LoadoutEntry entry = FindEntry(loadouts, mode, slot);
            if (entry != null && !string.IsNullOrEmpty(entry.gearCode))
            {
                GearDef chosen = combat.FindGear(entry.gearCode);
                if (CheckEquip(chosen, owned, mode, slot) == null) return chosen;
            }
            for (int i = 0; i < combat.gear.Count; i++)
            {
                GearDef def = combat.gear[i];
                if (def.IsFree && !def.comingSoon && def.slot == slot && def.UsableIn(mode)) return def;
            }
            return null;
        }
    }

    // ── 타워·방어시설의 분류 (TowerDef 의 tier / kind / ownership) ──

    public static class StructureTiers
    {
        public const string Basic = "basic";
        public const string Mid = "mid";
        public const string Advanced = "advanced";
        public const string Special = "special";

        public static string DisplayName(string tier)
        {
            switch (tier)
            {
                case Basic: return "기본";
                case Mid: return "중급";
                case Advanced: return "고급";
                case Special: return "특수";
                default: return "기본";
            }
        }
    }

    public static class StructureOwnership
    {
        /// <summary>전투 중 설치형: 누구나 전투 에너지로 세운다. 전투가 끝나면 사라진다.</summary>
        public const string Battle = "battle";
        /// <summary>영구 보유형: GC로 한 번 해금하면 그 뒤로 전투에서 세울 수 있다 (개인 자산).</summary>
        public const string Permanent = "permanent";
        /// <summary>도시 공용: 주민이 함께 기금을 모아 도시에 세운다. 개인 자산이 아니다.</summary>
        public const string City = "city";

        public static string DisplayName(string ownership)
        {
            switch (ownership)
            {
                case Permanent: return "영구 보유";
                case City: return "도시 공용";
                default: return "전투 중 설치";
            }
        }
    }

    public static class StructureKinds
    {
        public const string Gun = "gun";
        public const string Blast = "blast";
        public const string Wall = "wall";
        public const string Emp = "emp";
        public const string Laser = "laser";
        public const string Slow = "slow";
        public const string Repair = "repair";
        public const string AntiAir = "anti_air";
        public const string Shield = "shield";
        public const string Hangar = "hangar";
        public const string Command = "command";
        public const string Shock = "shock";
        public const string Jammer = "jammer";
        public const string Robot = "robot";
        public const string Support = "support";
    }

    // ── FPS 임무 ────────────────────────────────────────────────

    public static class FpsMissionCodes
    {
        public const string Downtown1 = "downtown_1";
    }

    /// <summary>FPS 시내 침입 임무 정의. 서버의 fps_missions 가 원본이다 (docs/FPS_MODE.md).</summary>
    [Serializable]
    public sealed class FpsMissionDef
    {
        public string code;
        public string name;
        public string description;
        /// <summary>나오는 로봇 묶음. delay 는 임무가 시작된 뒤의 시간이다.</summary>
        public List<WaveGroupDef> groups = new List<WaveGroupDef>();
        /// <summary>지켜야 하는 주요 시설의 수.</summary>
        public int facilityCount;
        /// <summary>시설 하나의 내구도. 로봇이 닿으면 그 로봇의 baseDamage 만큼 깎인다.</summary>
        public int facilityHp;
        /// <summary>대피시켜야 하는 시민 NPC의 수.</summary>
        public int civilianCount;
        /// <summary>제한 시간(초). 넘으면 실패다.</summary>
        public float timeLimitSeconds;
        /// <summary>이기는 데 걸리는 최소 시간(초). 서버 검증에 쓴다.</summary>
        public float minSeconds;
        public int winGc;
        public int firstWinBonusGc;
        public int winXp;
        /// <summary>대피시킨 시민 한 명당 더 주는 GC.</summary>
        public int civilianBonusGc;
        /// <summary>지켜낸 시설 하나당 더 주는 GC.</summary>
        public int facilityBonusGc;
        /// <summary>이겼을 때 주민의 소속 도시에 쌓이는 방어 포인트.</summary>
        public int winDefensePoints;
        public int unlockLevel = 1;

        public int TotalEnemies
        {
            get
            {
                int total = 0;
                for (int i = 0; i < groups.Count; i++) total += groups[i].count;
                return total;
            }
        }

        public int CountOf(string enemyCode)
        {
            int total = 0;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].enemyCode == enemyCode) total += groups[i].count;
            return total;
        }
    }

    /// <summary>도시의 주요 시설. 침입을 막지 못하면 피해를 입고, 주민이 GC를 내어 고친다.</summary>
    [Serializable]
    public sealed class FacilityDef
    {
        public string code;
        public string name;
        /// <summary>피해 한 단계를 고치는 값 (GC).</summary>
        public int repairGc;
        /// <summary>피해 단계의 한도.</summary>
        public int maxDamage = 3;
    }

    public static class FacilityCodes
    {
        public const string PowerPlant = "fac_power";
        public const string CommCenter = "fac_comm";
        public const string WaterPlant = "fac_water";
    }

    /// <summary>전투와 도시 위험도에 관한 정의 묶음. GameDefs.combat 에 들어 있다.</summary>
    [Serializable]
    public sealed class CombatDefs
    {
        public List<CombatModeDef> modes = new List<CombatModeDef>();
        public List<GearDef> gear = new List<GearDef>();
        public List<FpsMissionDef> fpsMissions = new List<FpsMissionDef>();
        public List<FacilityDef> facilities = new List<FacilityDef>();
        /// <summary>하루에 보상을 받는 FPS 임무의 수. 넘으면 GC 없이 도시 위험도와 경험치만 반영된다.</summary>
        public int dailyFpsRewardLimit = 5;
        public RiskPolicy risk = new RiskPolicy();
        public RankingWeights ranking = new RankingWeights();

        public CombatModeDef FindMode(string code)
        {
            for (int i = 0; i < modes.Count; i++)
                if (modes[i].code == code) return modes[i];
            return null;
        }

        public GearDef FindGear(string code)
        {
            for (int i = 0; i < gear.Count; i++)
                if (gear[i].code == code) return gear[i];
            return null;
        }

        public FpsMissionDef FindFpsMission(string code)
        {
            for (int i = 0; i < fpsMissions.Count; i++)
                if (fpsMissions[i].code == code) return fpsMissions[i];
            return null;
        }

        public FacilityDef FindFacility(string code)
        {
            for (int i = 0; i < facilities.Count; i++)
                if (facilities[i].code == code) return facilities[i];
            return null;
        }
    }
}
