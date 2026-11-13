using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>적 정의. 서버의 enemy_defs가 원본이 될 데이터 (docs/ENEMY_AI.md 1장).</summary>
    [Serializable]
    public sealed class EnemyDef
    {
        public string code;
        public string name;
        public int maxHp;
        public int damage;
        public float moveSpeed;
        public float detectRange;
        public float attackRange;
        /// <summary>공격 예고 동작 시간(초). 초급 적은 0.6초 이상.</summary>
        public float attackWindup;
        /// <summary>공격과 공격 사이의 간격(초). 예고 시간을 포함한다.</summary>
        public float attackInterval;
        /// <summary>이 거리보다 멀어지면 추적을 포기한다.</summary>
        public float loseRange;
        public int rewardGc;
        public int rewardXp;

        // ── 방어전 (docs/TOWER_DEFENSE.md) ──
        /// <summary>방어 거점에 닿았을 때 깎는 도시 방어 체력.</summary>
        public int baseDamage = 1;
        /// <summary>방어전에서 쓰러뜨렸을 때 얻는 에너지.</summary>
        public int rewardEnergy;
        /// <summary>방어전에서 플레이어에게 맞은 뒤 플레이어를 쫓는 시간(초). 0이면 길에서 벗어나지 않는다.</summary>
        public float aggroSeconds;
        /// <summary>가는 길 가까이의 타워를 먼저 공격하는가 (건설 메카).</summary>
        public bool attacksStructures;
        /// <summary>타격이 미치는 반경. 0이면 정면의 한 대상만 맞힌다.</summary>
        public float strikeRadius;
        /// <summary>타워 같은 구조물에 주는 피해. 0이면 damage와 같다.</summary>
        public int structureDamage;
        /// <summary>맞아도 멈칫하거나 밀리지 않는가 (중간 보스).</summary>
        public bool staggerImmune;

        // ── 드롭 (docs/ITEM_SYSTEM.md 8장) ──
        /// <summary>사냥 구역에서 쓰러뜨렸을 때 떨어뜨릴 수 있는 아이템. 없으면 비어 있다.</summary>
        public string dropItemCode;
        /// <summary>떨어뜨릴 확률 (0~1). 서버가 판정한다.</summary>
        public float dropChance;
    }

    /// <summary>타워 정의 (docs/TOWER_DEFENSE.md 5장).</summary>
    [Serializable]
    public sealed class TowerDef
    {
        public string code;
        public string name;
        public string description;
        /// <summary>설치에 드는 에너지.</summary>
        public int energyCost;
        public int damage;
        public float shotsPerSecond;
        public float range;
        /// <summary>범위 피해 반경. 0이면 한 대상만 맞힌다.</summary>
        public float splashRadius;
        public bool canHitAir;
        public int maxHp;
        /// <summary>해금에 드는 GC. 0이면 처음부터 쓸 수 있다.</summary>
        public int unlockGc;
        /// <summary>전투 중 강화 한 번에 드는 에너지.</summary>
        public int upgradeEnergyCost;
        public int maxUpgradeLevel;
    }

    /// <summary>타워 능력치 계산. 전투 중 강화 단계마다 피해 +40%, 사거리 +10%.</summary>
    public static class TowerMath
    {
        public const float DamagePerLevel = 0.4f;
        public const float RangePerLevel = 0.1f;

        public static int Damage(TowerDef def, int level) =>
            Math.Max(1, (int)Math.Round(def.damage * (1f + DamagePerLevel * Math.Max(0, level))));

        public static float Range(TowerDef def, int level) => def.range * (1f + RangePerLevel * Math.Max(0, level));
    }

    [Serializable]
    public sealed class WaveGroupDef
    {
        public string enemyCode;
        public int count;
        /// <summary>한 마리씩 나오는 간격(초).</summary>
        public float interval;
        /// <summary>웨이브가 시작되고 이 묶음이 나오기 시작할 때까지의 시간(초).</summary>
        public float delay;
    }

    [Serializable]
    public sealed class WaveDef
    {
        public List<WaveGroupDef> groups = new List<WaveGroupDef>();

        public int Total
        {
            get
            {
                int total = 0;
                for (int i = 0; i < groups.Count; i++) total += groups[i].count;
                return total;
            }
        }
    }

    /// <summary>방어전 스테이지 정의. 보상과 검증의 기준이다 (docs/TOWER_DEFENSE.md 6, 10장).</summary>
    [Serializable]
    public sealed class StageDef
    {
        public string code;
        public string name;
        public int baseHp;
        public int startEnergy;
        public int waveClearEnergy;
        /// <summary>웨이브 사이의 쉬는 시간(초).</summary>
        public float restSeconds;
        public List<WaveDef> waves = new List<WaveDef>();

        public int winGc;
        public int firstWinBonusGc;
        public int winXp;
        /// <summary>졌을 때 막아낸 웨이브 하나당 주는 참가 보상.</summary>
        public int loseGcPerWave;
        /// <summary>웨이브 하나를 끝내는 데 걸리는 최소 시간(초). 서버 검증에 쓴다.</summary>
        public float minSecondsPerWave;
        /// <summary>이겼을 때 주민의 소속 도시에 쌓이는 방어 포인트. 이방인에게는 쌓이지 않는다.</summary>
        public int winDefensePoints;

        public int TotalEnemies => EnemiesInFirstWaves(waves.Count);

        /// <summary>앞에서부터 waveCount개 웨이브에 나오는 적의 수.</summary>
        public int EnemiesInFirstWaves(int waveCount)
        {
            int total = 0;
            for (int i = 0; i < waves.Count && i < waveCount; i++) total += waves[i].Total;
            return total;
        }

        /// <summary>스테이지 전체에서 그 적이 나오는 수. 나오지 않으면 0.</summary>
        public int CountOf(string enemyCode)
        {
            int total = 0;
            for (int i = 0; i < waves.Count; i++)
            for (int g = 0; g < waves[i].groups.Count; g++)
                if (waves[i].groups[g].enemyCode == enemyCode) total += waves[i].groups[g].count;
            return total;
        }
    }

    public static class WeaponKind
    {
        public const string Melee = "melee";
        public const string Ranged = "ranged";
    }

    /// <summary>무기 정의 (docs/COMBAT_SYSTEM.md 3장).</summary>
    [Serializable]
    public sealed class WeaponDef
    {
        public string code;
        public string name;
        public string kind;
        public int damage;
        public float attacksPerSecond;
        public float range;
        /// <summary>근접 무기의 정면 부채꼴 각도.</summary>
        public float arcDegrees;
        /// <summary>원거리 무기의 탄환 속도 (m/s).</summary>
        public float projectileSpeed;
    }

    public static class ItemCategory
    {
        public const string WeaponMelee = "weapon_melee";
        public const string WeaponRanged = "weapon_ranged";
        public const string Consumable = "consumable";
        /// <summary>방어구: 머리나 몸에 입고, 받는 피해를 줄인다.</summary>
        public const string Armor = "armor";
        /// <summary>의상: 머리나 몸에 입는다. 능력치가 없다.</summary>
        public const string Costume = "costume";
        /// <summary>가구: 가방이 아니라 가구 목록에 들어가고 내 방에 놓는다.</summary>
        public const string Furniture = "furniture";

        /// <summary>상점과 가방에서 물건을 묶어 보여 줄 때의 이름.</summary>
        public static string DisplayName(string category)
        {
            switch (category)
            {
                case WeaponMelee:
                case WeaponRanged:
                    return "무기";
                case Consumable: return "회복";
                case Armor: return "방어구";
                case Costume: return "옷";
                case Furniture: return "가구";
                default: return "기타";
            }
        }
    }

    /// <summary>
    /// 아이템 정의 (docs/ITEM_SYSTEM.md 2장). 무기는 아이템 코드와 무기 코드가 같고,
    /// 전투 능력치는 WeaponDef에 있다.
    /// </summary>
    [Serializable]
    public sealed class ItemDef
    {
        public string code;
        public string category;
        public string name;
        public string description;
        /// <summary>상점 가격. 0이면 팔지 않는다.</summary>
        public int priceGc;
        /// <summary>상점이 되사는 값. 0이면 되팔 수 없다.</summary>
        public int sellPriceGc;
        /// <summary>한 칸에 겹쳐 가질 수 있는 수. 장비는 1.</summary>
        public int maxStack = 1;
        /// <summary>썼을 때 회복하는 체력. 회복 아이템이 아니면 0.</summary>
        public int healHp;
        /// <summary>입는 물건(방어구·의상)이 들어가는 장착 칸 (EquipSlots.Head 또는 Body).</summary>
        public string slot;
        /// <summary>입고 있을 때 받는 피해가 줄어드는 비율(%). 방어구가 아니면 0.</summary>
        public int defense;
        /// <summary>겉모습 코드. 입는 물건과 가구의 모양을 클라이언트가 이 이름으로 고른다.</summary>
        public string look;
        /// <summary>가구가 차지하는 바닥 칸 수 (돌리지 않았을 때의 가로). 가구가 아니면 0.</summary>
        public int sizeX;
        /// <summary>가구가 차지하는 바닥 칸 수 (돌리지 않았을 때의 세로).</summary>
        public int sizeZ;

        public bool IsWeapon => category == ItemCategory.WeaponMelee || category == ItemCategory.WeaponRanged;
        /// <summary>머리나 몸에 입는 물건인가 (방어구, 의상).</summary>
        public bool IsWearable => category == ItemCategory.Armor || category == ItemCategory.Costume;
        public bool IsFurniture => category == ItemCategory.Furniture;
        /// <summary>하나만 가질 수 있는 물건인가 (무기, 방어구, 의상).</summary>
        public bool IsUnique => IsWeapon || IsWearable;
    }

    /// <summary>상점 정의. 무엇을 파는지만 정하고 가격은 아이템 정의에서 읽는다.</summary>
    [Serializable]
    public sealed class ShopDef
    {
        public string code;
        public string name;
        public string[] itemCodes;

        public bool Sells(string itemCode)
        {
            if (itemCodes == null) return false;
            for (int i = 0; i < itemCodes.Length; i++)
                if (itemCodes[i] == itemCode) return true;
            return false;
        }
    }

    /// <summary>사냥 구역 정의. 보상 검증의 기준이 된다 (docs/COMBAT_SYSTEM.md 10.1).</summary>
    [Serializable]
    public sealed class HuntZoneDef
    {
        public string code;
        public string name;
        public string[] enemyCodes;
        public int maxAlive;
        public float respawnSeconds;
        /// <summary>이 구역에서 1분 동안 쓰러뜨릴 수 있는 최대 수. 서버 검증에 쓴다.</summary>
        public float maxKillsPerMinute;
        /// <summary>첫 보고에서 한 번에 인정하는 여유분.</summary>
        public int burstAllowance;
    }

    [Serializable]
    public sealed class EconomyConfig
    {
        public int dailyHuntGcLimit;
        public int firstRescueRewardGc;
        /// <summary>전입 축하금. 계정당 한 번 (docs/RESIDENCY_SYSTEM.md 5장).</summary>
        public int residencyBonusGc;
        /// <summary>인벤토리 칸 수. 아이템 한 종류가 한 칸을 차지한다.</summary>
        public int inventorySlots = 30;
        /// <summary>
        /// 되팔 때 돌려받는 비율(%). 아이템 정의의 sellPriceGc를 정할 때 쓴 값이다.
        /// 100보다 작아야 사고팔기를 되풀이해 이득을 볼 수 없다.
        /// </summary>
        public int sellBackPercent = 30;
    }

    [Serializable]
    public sealed class ProgressionConfig
    {
        public float xpBase;
        public float xpExponent;
        public int baseMaxHp;
        public int maxHpPerLevel;
        public int maxLevel;
    }

    /// <summary>클라이언트가 접속 때 받는 정의 묶음 (GET /defs).</summary>
    [Serializable]
    public sealed class GameDefs
    {
        public string version;
        public List<EnemyDef> enemies = new List<EnemyDef>();
        public List<WeaponDef> weapons = new List<WeaponDef>();
        public List<HuntZoneDef> zones = new List<HuntZoneDef>();
        public List<TowerDef> towers = new List<TowerDef>();
        public List<StageDef> stages = new List<StageDef>();
        public List<ItemDef> items = new List<ItemDef>();
        public List<ShopDef> shops = new List<ShopDef>();
        public EconomyConfig economy = new EconomyConfig();
        public BankConfig bank = new BankConfig();
        public RoomConfig room = new RoomConfig();
        public ProgressionConfig progression = new ProgressionConfig();

        public ItemDef FindItem(string code)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].code == code) return items[i];
            return null;
        }

        public ShopDef FindShop(string code)
        {
            for (int i = 0; i < shops.Count; i++)
                if (shops[i].code == code) return shops[i];
            return null;
        }

        public EnemyDef FindEnemy(string code)
        {
            for (int i = 0; i < enemies.Count; i++)
                if (enemies[i].code == code) return enemies[i];
            return null;
        }

        public WeaponDef FindWeapon(string code)
        {
            for (int i = 0; i < weapons.Count; i++)
                if (weapons[i].code == code) return weapons[i];
            return null;
        }

        public HuntZoneDef FindZone(string code)
        {
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].code == code) return zones[i];
            return null;
        }

        public TowerDef FindTower(string code)
        {
            for (int i = 0; i < towers.Count; i++)
                if (towers[i].code == code) return towers[i];
            return null;
        }

        public StageDef FindStage(string code)
        {
            for (int i = 0; i < stages.Count; i++)
                if (stages[i].code == code) return stages[i];
            return null;
        }
    }

    public static class EnemyCodes
    {
        public const string TutorialRobot = "tutorial_robot";
        public const string MalfunctionRobot = "malfunction_robot";
        public const string MechInsect = "mech_insect";
        public const string ConstructionMecha = "construction_mecha";
    }

    public static class TowerCodes
    {
        public const string MachineGun = "twr_machine_gun";
        public const string Blast = "twr_blast";
    }

    public static class StageCodes
    {
        public const string Outskirts1 = "outskirts_1";
    }

    public static class WeaponCodes
    {
        public const string SparkBaton = "wpn_spark_baton";
        public const string BoltBlaster = "wpn_bolt_blaster";
    }

    public static class ItemCodes
    {
        public const string SparkBaton = WeaponCodes.SparkBaton;
        public const string BoltBlaster = WeaponCodes.BoltBlaster;
        public const string RepairKit = "con_repair_kit";

        // 방어구 (미소 상점)
        public const string GuardHelmet = "arm_guard_helmet";
        public const string GuardVest = "arm_guard_vest";

        // 의상 (알록달록 가게)
        public const string WaveCap = "cos_wave_cap";
        public const string BunnyBand = "cos_bunny_band";
        public const string SunnyHoodie = "cos_sunny_hoodie";
        public const string StarTee = "cos_star_tee";

        // 가구 (알록달록 가게)
        public const string Plant = "fur_plant";
        public const string Lamp = "fur_lamp";
        public const string Cushion = "fur_cushion";
        public const string Bookshelf = "fur_bookshelf";
        public const string Sofa = "fur_sofa";
    }

    public static class ShopCodes
    {
        /// <summary>광장 동쪽의 미소 상점: 무기, 방어구, 수리 키트.</summary>
        public const string Miso = "misoshop";
        /// <summary>광장 남동쪽의 알록달록 가게: 옷과 가구.</summary>
        public const string Color = "colorshop";
    }

    public static class ZoneCodes
    {
        public const string HomeStreet = "home_street";
        public const string NorthEast = "zone_ne";
        public const string NorthWest = "zone_nw";
    }

    /// <summary>
    /// 초기 콘텐츠 값. 설계 문서의 초기값과 같으며 플레이테스트로 조정한다.
    /// 실제 서버가 연결되면 서버가 내려 주는 정의가 이 값을 대신한다.
    /// </summary>
    public static class DefaultDefs
    {
        public static GameDefs Create()
        {
            var defs = new GameDefs { version = "local-2026111306" };

            defs.enemies.Add(new EnemyDef
            {
                code = EnemyCodes.TutorialRobot, name = "오작동 로봇",
                maxHp = 50, damage = 4, moveSpeed = 2.2f,
                detectRange = 9f, attackRange = 1.6f, attackWindup = 0.9f, attackInterval = 1.8f,
                loseRange = 16f, rewardGc = 10, rewardXp = 10,
            });
            defs.enemies.Add(new EnemyDef
            {
                code = EnemyCodes.MalfunctionRobot, name = "오작동 로봇",
                maxHp = 60, damage = 8, moveSpeed = 2.8f,
                detectRange = 10f, attackRange = 1.6f, attackWindup = 0.7f, attackInterval = 1.2f,
                loseRange = 18f, rewardGc = 10, rewardXp = 10,
                baseDamage = 1, rewardEnergy = 6, aggroSeconds = 4f,
                dropItemCode = ItemCodes.RepairKit, dropChance = 0.05f,
            });
            defs.enemies.Add(new EnemyDef
            {
                code = EnemyCodes.MechInsect, name = "기계 곤충",
                maxHp = 20, damage = 4, moveSpeed = 5.0f,
                detectRange = 11f, attackRange = 1.2f, attackWindup = 0.6f, attackInterval = 1.0f,
                loseRange = 18f, rewardGc = 3, rewardXp = 3,
                baseDamage = 1, rewardEnergy = 3, aggroSeconds = 0f,
            });
            defs.enemies.Add(new EnemyDef
            {
                // 중간 보스. 느리지만 단단하고, 길 가까이의 타워를 부순다. 공격 예고가 길어 피할 수 있다.
                code = EnemyCodes.ConstructionMecha, name = "건설 메카",
                maxHp = 1500, damage = 30, moveSpeed = 1.5f,
                detectRange = 7f, attackRange = 2.6f, attackWindup = 1.2f, attackInterval = 2.6f,
                loseRange = 12f, rewardGc = 120, rewardXp = 100,
                baseDamage = 5, rewardEnergy = 60, aggroSeconds = 3f,
                attacksStructures = true, strikeRadius = 3f, structureDamage = 60, staggerImmune = true,
            });

            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.MachineGun, name = "기관총 타워",
                description = "빠르게 쏴요. 기계 곤충처럼 작고 빠른 적에게 좋아요.",
                energyCost = 100, damage = 6, shotsPerSecond = 5f, range = 8f, splashRadius = 0f,
                canHitAir = true, maxHp = 120, unlockGc = 0, upgradeEnergyCost = 60, maxUpgradeLevel = 3,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.Blast, name = "폭발 타워",
                description = "느리지만 한꺼번에 여럿을 맞혀요. 로봇 무리에게 좋아요.",
                energyCost = 175, damage = 40, shotsPerSecond = 0.4f, range = 10f, splashRadius = 2.5f,
                canHitAir = false, maxHp = 150, unlockGc = 500, upgradeEnergyCost = 105, maxUpgradeLevel = 3,
            });

            defs.stages.Add(new StageDef
            {
                code = StageCodes.Outskirts1, name = "외곽 1구역",
                baseHp = 20, startEnergy = 200, waveClearEnergy = 50, restSeconds = 10f,
                winGc = 300, firstWinBonusGc = 200, winXp = 150, loseGcPerWave = 20, minSecondsPerWave = 10f,
                winDefensePoints = 100,
                // 웨이브마다 밀려오는 양이 늘어난다. 첫 웨이브는 타워 두 개만으로 막히지만(배우는 구간),
                // 그 뒤로는 타워를 더 세우고 직접 싸워야 막을 수 있다. 가만히 있으면 진다 (자동 플레이 테스트가 확인한다).
                //
                // 기계 곤충은 로봇보다 훨씬 빨라서, 같이 내보내면 곤충이 먼저 지나가 버려 타워가 차례로 여유 있게 잡는다.
                // 곤충을 약 14초 늦게 내보내야 방어선 앞에서 로봇 무리와 겹쳐 한꺼번에 몰려온다.
                waves =
                {
                    Wave(Group(EnemyCodes.MalfunctionRobot, 6, 2.5f, 0f)),
                    Wave(Group(EnemyCodes.MalfunctionRobot, 8, 2.0f, 0f), Group(EnemyCodes.MechInsect, 16, 0.6f, 14f)),
                    Wave(Group(EnemyCodes.MalfunctionRobot, 10, 1.5f, 0f), Group(EnemyCodes.MechInsect, 30, 0.4f, 14f)),
                    Wave(Group(EnemyCodes.MalfunctionRobot, 16, 1.1f, 0f), Group(EnemyCodes.MechInsect, 36, 0.35f, 14f)),
                    // 마지막: 건설 메카가 맨 앞에서 천천히 걸어오고, 본대가 앞질러 몰려온 뒤, 호위대가 메카와 함께 도착한다.
                    Wave(Group(EnemyCodes.ConstructionMecha, 1, 0f, 0f),
                        Group(EnemyCodes.MalfunctionRobot, 14, 1.2f, 2f), Group(EnemyCodes.MechInsect, 40, 0.35f, 16f),
                        Group(EnemyCodes.MalfunctionRobot, 6, 1.5f, 30f), Group(EnemyCodes.MechInsect, 12, 0.4f, 44f)),
                },
            });

            defs.weapons.Add(new WeaponDef
            {
                code = WeaponCodes.SparkBaton, name = "스파크 배턴", kind = WeaponKind.Melee,
                damage = 25, attacksPerSecond = 2f, range = 1.8f, arcDegrees = 100f,
            });
            defs.weapons.Add(new WeaponDef
            {
                code = WeaponCodes.BoltBlaster, name = "볼트 블래스터", kind = WeaponKind.Ranged,
                damage = 14, attacksPerSecond = 4f, range = 25f, projectileSpeed = 45f,
            });

            defs.items.Add(new ItemDef
            {
                code = ItemCodes.SparkBaton, category = ItemCategory.WeaponMelee, name = "스파크 배턴",
                description = "가까운 로봇을 한꺼번에 때려요.", priceGc = 0, maxStack = 1,
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.BoltBlaster, category = ItemCategory.WeaponRanged, name = "볼트 블래스터",
                description = "멀리 있는 로봇을 빠르게 맞혀요.", priceGc = 200, maxStack = 1,
            });
            // 수리 키트는 되팔 수 없다. 로봇이 떨어뜨리기도 해서, 되팔 수 있으면 일일 사냥 보상 한도 밖의 수입이 된다.
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.RepairKit, category = ItemCategory.Consumable, name = "수리 키트",
                description = "체력을 50 회복해요.", priceGc = 30, maxStack = 99, healHp = 50,
            });

            // 방어구: 받는 피해를 줄인다. 머리와 몸에 하나씩 입는다.
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.GuardHelmet, category = ItemCategory.Armor, name = "가디언 헬멧",
                description = "머리에 써요.", priceGc = 150, slot = EquipSlots.Head, defense = 10, look = "guard_helmet",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.GuardVest, category = ItemCategory.Armor, name = "가디언 조끼",
                description = "몸에 입어요.", priceGc = 250, slot = EquipSlots.Body, defense = 20, look = "guard_vest",
            });

            // 의상: 능력치가 없고 모습만 바뀐다. 방어구와 같은 칸에 입는다.
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.WaveCap, category = ItemCategory.Costume, name = "파도 모자",
                description = "해운대 바다색 모자예요.", priceGc = 80, slot = EquipSlots.Head, look = "wave_cap",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.BunnyBand, category = ItemCategory.Costume, name = "토끼 머리띠",
                description = "쫑긋한 토끼 귀가 달렸어요.", priceGc = 100, slot = EquipSlots.Head, look = "bunny_band",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.SunnyHoodie, category = ItemCategory.Costume, name = "해님 후드티",
                description = "햇살처럼 노란 후드티예요.", priceGc = 120, slot = EquipSlots.Body, look = "sunny_hoodie",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.StarTee, category = ItemCategory.Costume, name = "별 티셔츠",
                description = "가슴에 큰 별이 있어요.", priceGc = 120, slot = EquipSlots.Body, look = "star_tee",
            });

            // 가구: 내 방 바닥의 칸(0.5 m)을 차지한다 (docs/ROOM_SYSTEM.md 4장).
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.Plant, category = ItemCategory.Furniture, name = "초록 화분",
                description = "방이 싱그러워져요.", priceGc = 80, sizeX = 1, sizeZ = 1, look = "plant",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.Cushion, category = ItemCategory.Furniture, name = "폭신 쿠션",
                description = "커다랗고 폭신한 쿠션이에요.", priceGc = 120, sizeX = 2, sizeZ = 2, look = "cushion",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.Lamp, category = ItemCategory.Furniture, name = "스탠드 조명",
                description = "따뜻한 불빛이 켜져요.", priceGc = 150, sizeX = 1, sizeZ = 1, look = "lamp",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.Bookshelf, category = ItemCategory.Furniture, name = "책장",
                description = "알록달록한 책이 꽂혀 있어요.", priceGc = 250, sizeX = 2, sizeZ = 1, look = "bookshelf",
            });
            defs.items.Add(new ItemDef
            {
                code = ItemCodes.Sofa, category = ItemCategory.Furniture, name = "소파",
                description = "푹신하고 긴 소파예요.", priceGc = 400, sizeX = 3, sizeZ = 2, look = "sofa",
            });

            defs.shops.Add(new ShopDef
            {
                code = ShopCodes.Miso, name = "미소 상점",
                itemCodes = new[] { ItemCodes.BoltBlaster, ItemCodes.RepairKit, ItemCodes.GuardHelmet, ItemCodes.GuardVest },
            });
            defs.shops.Add(new ShopDef
            {
                code = ShopCodes.Color, name = "알록달록 가게",
                itemCodes = new[]
                {
                    ItemCodes.WaveCap, ItemCodes.BunnyBand, ItemCodes.SunnyHoodie, ItemCodes.StarTee,
                    ItemCodes.Plant, ItemCodes.Cushion, ItemCodes.Lamp, ItemCodes.Bookshelf, ItemCodes.Sofa,
                },
            });

            defs.zones.Add(new HuntZoneDef
            {
                code = ZoneCodes.HomeStreet, name = "집 앞 거리",
                enemyCodes = new[] { EnemyCodes.TutorialRobot },
                maxAlive = 1, respawnSeconds = 0f, maxKillsPerMinute = 2f, burstAllowance = 1,
            });
            defs.zones.Add(new HuntZoneDef
            {
                code = ZoneCodes.NorthEast, name = "북동 전투 구역",
                enemyCodes = new[] { EnemyCodes.MalfunctionRobot },
                maxAlive = 5, respawnSeconds = 8f, maxKillsPerMinute = 24f, burstAllowance = 5,
            });
            defs.zones.Add(new HuntZoneDef
            {
                code = ZoneCodes.NorthWest, name = "북서 전투 구역",
                enemyCodes = new[] { EnemyCodes.MechInsect, EnemyCodes.MalfunctionRobot },
                maxAlive = 9, respawnSeconds = 6f, maxKillsPerMinute = 50f, burstAllowance = 9,
            });

            // 보통예금: 하루 1% (100 GC마다 하루 1 GC), 이자 대상 한도 10,000 GC, 레벨 3부터 (docs/ECONOMY_SYSTEM.md 4장).
            // 한도까지 채웠을 때의 하루 이자는 100 GC다.
            defs.bank = new BankConfig { dailyInterestBp = 100, interestCap = 10000, unlockLevel = 3 };
            // 정기예금: 만기까지 묶어 두는 대신, 같은 기간 보통예금에 둔 것(7일 약 7%, 30일 약 35%)보다 많이 받는다.
            // 오래 묶을수록 하루로 친 이자도 많다 (10,000 GC 기준 7일 상품 약 143 GC, 30일 상품 약 167 GC).
            // 상품마다 하나씩, 10,000 GC까지 넣을 수 있다.
            defs.bank.termProducts.Add(new TermProductDef
            {
                code = TermProductCodes.Days7, name = "7일 정기예금", days = 7, rateBp = 1000, minAmount = 100, maxAmount = 10000,
            });
            defs.bank.termProducts.Add(new TermProductDef
            {
                code = TermProductCodes.Days30, name = "30일 정기예금", days = 30, rateBp = 5000, minAmount = 100, maxAmount = 10000,
            });

            defs.room = new RoomConfig { maxFurniture = 20 };

            defs.economy = new EconomyConfig
            {
                dailyHuntGcLimit = 3000, firstRescueRewardGc = 100, residencyBonusGc = 500,
                inventorySlots = 30, sellBackPercent = 30,
            };
            // 되파는 값: 산 값의 30% (소수점 버림). 팔지 않는 물건과 수리 키트는 되팔 수 없다.
            for (int i = 0; i < defs.items.Count; i++)
            {
                ItemDef item = defs.items[i];
                item.sellPriceGc = item.category == ItemCategory.Consumable
                    ? 0
                    : item.priceGc * defs.economy.sellBackPercent / 100;
            }
            defs.progression = new ProgressionConfig
            {
                xpBase = 50f, xpExponent = 1.5f, baseMaxHp = 100, maxHpPerLevel = 10, maxLevel = 50,
            };
            return defs;
        }

        private static WaveDef Wave(params WaveGroupDef[] groups)
        {
            var wave = new WaveDef();
            wave.groups.AddRange(groups);
            return wave;
        }

        private static WaveGroupDef Group(string enemyCode, int count, float interval, float delay) =>
            new WaveGroupDef { enemyCode = enemyCode, count = count, interval = interval, delay = delay };
    }
}
