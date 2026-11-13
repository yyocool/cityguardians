namespace CityGuardians.Domain
{
    /// <summary>
    /// v8.0 전투 콘텐츠의 초기값: 방어시설 15종, 전투 모드 3종, FPS 장비 8종, FPS 임무, 도시 위험도 정책.
    /// 실제 서버가 연결되면 서버가 내려 주는 정의가 이 값을 대신한다. 수치는 플레이테스트로 조정한다.
    /// </summary>
    public static class CombatDefaults
    {
        /// <summary>
        /// 기관총 타워와 폭발 타워 뒤에 나머지 방어시설 13종을 더한다 (docs/DEFENSE_MODE.md 3장).
        /// 순서를 바꾸지 않는다: 전투 중 타워 고르기 메뉴의 번호가 이 순서다.
        /// </summary>
        public static void AddStructures(GameDefs defs)
        {
            // ── 전투에서 세울 수 있는 것 ──
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.Laser, name = "레이저 타워",
                description = "한 대상을 세게 지져요. 단단한 로봇에게 좋아요.",
                tier = StructureTiers.Mid, kind = StructureKinds.Laser, ownership = StructureOwnership.Permanent,
                energyCost = 200, damage = 70, shotsPerSecond = 1f, range = 11f, canHitAir = true, maxHp = 130,
                unlockGc = 900, unlockLevel = 4, upgradeEnergyCost = 120, maxUpgradeLevel = 3,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.Slow, name = "감속 장치",
                description = "가까운 로봇을 느리게 해요. 다른 타워가 더 오래 쏠 수 있어요.",
                tier = StructureTiers.Mid, kind = StructureKinds.Slow, ownership = StructureOwnership.Permanent,
                energyCost = 120, damage = 0, shotsPerSecond = 0f, range = 6f, maxHp = 110,
                unlockGc = 600, unlockLevel = 2, upgradeEnergyCost = 70, maxUpgradeLevel = 2,
                effectValue = 40, maxPerBattle = 2,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.Emp, name = "EMP 타워",
                description = "가끔 전자기 파동을 내보내 가까운 로봇을 잠깐 멈춰요.",
                tier = StructureTiers.Mid, kind = StructureKinds.Emp, ownership = StructureOwnership.Permanent,
                energyCost = 150, damage = 0, shotsPerSecond = 0.2f, range = 6f, maxHp = 130,
                unlockGc = 800, unlockLevel = 3, upgradeEnergyCost = 90, maxUpgradeLevel = 2,
                effectSeconds = 1.5f, maxPerBattle = 2,
            });

            // ── 도시 공용: 주민이 기금을 모아 세운다. 개인 자산이 아니고 도시의 방어력을 올린다 ──
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.EnergyShield, name = "에너지 방어막",
                description = "도시를 감싸는 방어막이에요. 주민이 함께 기금을 모아 세워요.",
                tier = StructureTiers.Advanced, kind = StructureKinds.Shield, ownership = StructureOwnership.City,
                cityGoalGc = 5000, cityPower = 40, cityMaxLevel = 3,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.CommandCenter, name = "지휘 센터",
                description = "NPC 수비대를 지휘해요. 침공을 더 잘 막아요.",
                tier = StructureTiers.Advanced, kind = StructureKinds.Command, ownership = StructureOwnership.City,
                cityGoalGc = 8000, cityPower = 60, cityMaxLevel = 3,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.EmergencySupport, name = "긴급 방어 지원 장치",
                description = "위급할 때 수비대를 바로 불러와요.",
                tier = StructureTiers.Special, kind = StructureKinds.Support, ownership = StructureOwnership.City,
                cityGoalGc = 6000, cityPower = 50, cityMaxLevel = 2,
            });

            // ── 준비 중: 정의와 값만 있고 아직 게임에서 동작하지 않는다 ──
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.Wall, name = "방어벽",
                description = "로봇이 지나가지 못하게 길을 막아요.",
                tier = StructureTiers.Basic, kind = StructureKinds.Wall, ownership = StructureOwnership.Battle,
                energyCost = 60, maxHp = 400, maxPerBattle = 4, comingSoon = true,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.RepairDrone, name = "자동 수리 드론",
                description = "부서진 타워를 스스로 고쳐요.",
                tier = StructureTiers.Mid, kind = StructureKinds.Repair, ownership = StructureOwnership.Permanent,
                energyCost = 140, range = 7f, maxHp = 90, unlockGc = 700, unlockLevel = 3, effectValue = 10,
                maxPerBattle = 1, comingSoon = true,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.AntiAir, name = "대공 타워",
                description = "하늘의 보안 드론을 맞혀요.",
                tier = StructureTiers.Advanced, kind = StructureKinds.AntiAir, ownership = StructureOwnership.Permanent,
                energyCost = 180, damage = 35, shotsPerSecond = 1.5f, range = 14f, canHitAir = true, maxHp = 120,
                unlockGc = 1200, unlockLevel = 6, upgradeEnergyCost = 110, maxUpgradeLevel = 3, comingSoon = true,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.DroneHangar, name = "드론 격납고",
                description = "아군 드론을 내보내 로봇을 쫓아요.",
                tier = StructureTiers.Advanced, kind = StructureKinds.Hangar, ownership = StructureOwnership.Permanent,
                energyCost = 260, damage = 10, shotsPerSecond = 2f, range = 16f, maxHp = 160,
                unlockGc = 2000, unlockLevel = 8, maxPerBattle = 1, comingSoon = true,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.WideShock, name = "광역 전자기 충격 장치",
                description = "전장 전체의 로봇을 한 번에 멈춰요.",
                tier = StructureTiers.Special, kind = StructureKinds.Shock, ownership = StructureOwnership.Permanent,
                energyCost = 320, range = 30f, maxHp = 140, unlockGc = 3000, unlockLevel = 10,
                effectSeconds = 3f, maxPerBattle = 1, comingSoon = true,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.AiJammer, name = "AI 교란 장치",
                description = "로봇이 길을 잃고 헤매게 해요.",
                tier = StructureTiers.Special, kind = StructureKinds.Jammer, ownership = StructureOwnership.Permanent,
                energyCost = 240, range = 9f, maxHp = 120, unlockGc = 2500, unlockLevel = 9,
                effectSeconds = 4f, maxPerBattle = 1, comingSoon = true,
            });
            defs.towers.Add(new TowerDef
            {
                code = TowerCodes.MobileRobot, name = "이동식 방어 로봇",
                description = "전장을 돌아다니며 함께 싸워요.",
                tier = StructureTiers.Special, kind = StructureKinds.Robot, ownership = StructureOwnership.Permanent,
                energyCost = 280, damage = 20, shotsPerSecond = 2f, range = 8f, maxHp = 300,
                unlockGc = 2800, unlockLevel = 10, maxPerBattle = 1, comingSoon = true,
            });
        }

        public static CombatDefs Create()
        {
            var combat = new CombatDefs();

            combat.modes.Add(new CombatModeDef
            {
                code = CombatModeCodes.Defense, name = "디펜스 모드", sortOrder = 1,
                summary = "타워를 세워 도시 외곽으로 몰려오는 로봇을 막아요.",
                camera = CameraKinds.ThirdPerson,
                controls = "E 타워 세우기·강화   V 전략 시점   좌클릭 공격",
            });
            combat.modes.Add(new CombatModeDef
            {
                code = CombatModeCodes.Hunt, name = "헌트 모드", sortOrder = 2,
                summary = "도시 주변을 순찰하며 로봇을 직접 물리쳐요.",
                camera = CameraKinds.ThirdPerson,
                controls = "WASD 이동   좌클릭 공격   1·2 무기 바꾸기",
            });
            combat.modes.Add(new CombatModeDef
            {
                code = CombatModeCodes.Fps, name = "FPS 모드", sortOrder = 3,
                summary = "시내로 들어온 로봇을 1인칭으로 찾아 물리치고 시설과 시민을 지켜요.",
                camera = CameraKinds.FirstPerson,
                controls = "WASD 이동   마우스 시점   좌클릭 발사   우클릭 조준   R 재장전   E 상호작용",
            });

            string[] fpsOnly = { CombatModeCodes.Fps };

            // ── 주 무기 ──
            combat.gear.Add(new GearDef
            {
                code = GearCodes.EnergyBlaster, name = "에너지 블래스터", slot = GearSlots.Primary, kind = GearKinds.Blaster,
                description = "빠르게 쏘는 기본 무기예요. 누구나 처음부터 가지고 있어요.",
                modes = fpsOnly, priceGc = 0, unlockLevel = 1, maxLevel = 3, upgradeBaseGc = 150,
                damage = 12, attacksPerSecond = 5f, range = 30f, projectileSpeed = 60f,
                clipSize = 24, reloadSeconds = 1.4f, aimZoom = 0.8f,
            });
            combat.gear.Add(new GearDef
            {
                code = GearCodes.PrecisionRifle, name = "정밀 에너지 라이플", slot = GearSlots.Primary, kind = GearKinds.Rifle,
                description = "한 발이 세고 멀리 나가요. 대신 천천히 쏘고 탄창이 작아요.",
                modes = fpsOnly, priceGc = 600, unlockLevel = 4, maxLevel = 3, upgradeBaseGc = 300,
                damage = 55, attacksPerSecond = 1.2f, range = 60f, projectileSpeed = 110f,
                clipSize = 6, reloadSeconds = 1.8f, aimZoom = 0.45f,
            });
            combat.gear.Add(new GearDef
            {
                code = GearCodes.ShockwaveLauncher, name = "충격파 발사기", slot = GearSlots.Primary, kind = GearKinds.Shockwave,
                description = "가까운 로봇 여럿을 한꺼번에 밀어내요. 멀리는 닿지 않아요.",
                modes = fpsOnly, priceGc = 800, unlockLevel = 5, maxLevel = 3, upgradeBaseGc = 350,
                damage = 30, attacksPerSecond = 0.8f, range = 7f, coneDegrees = 60f, knockback = 2.5f,
                clipSize = 4, reloadSeconds = 2f, aimZoom = 0.9f,
            });

            // ── 보조 장치 (Q) ──
            combat.gear.Add(new GearDef
            {
                code = GearCodes.HealDevice, name = "회복 장치", slot = GearSlots.Gadget, kind = GearKinds.Heal,
                description = "체력을 채워요. 누구나 처음부터 가지고 있어요.",
                modes = fpsOnly, priceGc = 0, unlockLevel = 1, maxLevel = 3, upgradeBaseGc = 120,
                cooldownSeconds = 30f, effectValue = 40,
            });
            combat.gear.Add(new GearDef
            {
                code = GearCodes.EmpDevice, name = "EMP 장치", slot = GearSlots.Gadget, kind = GearKinds.Emp,
                description = "가까운 로봇을 잠깐 멈춰요.",
                modes = fpsOnly, priceGc = 500, unlockLevel = 3, maxLevel = 3, upgradeBaseGc = 250,
                cooldownSeconds = 20f, effectSeconds = 3f, effectRadius = 7f,
            });
            combat.gear.Add(new GearDef
            {
                code = GearCodes.Shield, name = "보호막", slot = GearSlots.Gadget, kind = GearKinds.Shield,
                description = "잠깐 동안 받는 피해를 막아 줘요.",
                modes = fpsOnly, priceGc = 700, unlockLevel = 5, maxLevel = 3, upgradeBaseGc = 300,
                cooldownSeconds = 25f, effectSeconds = 6f, effectValue = 60,
            });

            // ── 지원 장비 (F) ──
            combat.gear.Add(new GearDef
            {
                code = GearCodes.Sensor, name = "탐지 센서", slot = GearSlots.Support, kind = GearKinds.Sensor,
                description = "벽 뒤의 로봇이 어디 있는지 잠깐 보여 줘요.",
                modes = fpsOnly, priceGc = 300, unlockLevel = 2, maxLevel = 2, upgradeBaseGc = 200,
                cooldownSeconds = 15f, effectSeconds = 8f, effectRadius = 45f,
            });
            combat.gear.Add(new GearDef
            {
                code = GearCodes.SupportDrone, name = "지원 드론", slot = GearSlots.Support, kind = GearKinds.Drone,
                description = "곁을 따라다니며 가까운 로봇을 쏴요.",
                modes = fpsOnly, priceGc = 1200, unlockLevel = 6, maxLevel = 3, upgradeBaseGc = 400,
                cooldownSeconds = 40f, effectSeconds = 15f, effectValue = 8, attacksPerSecond = 2f, range = 14f,
            });

            // 첫 FPS 임무. 로봇 18대가 세 방향에서 들어와 시설 세 곳으로 간다.
            // 마지막 묶음이 60초에 나오기 시작하므로 그보다 빨리 이길 수 없다.
            var mission = new FpsMissionDef
            {
                code = FpsMissionCodes.Downtown1, name = "도심 상가 구역",
                description = "시내로 들어온 로봇을 찾아 물리치고, 시설 세 곳과 시민을 지켜요.",
                facilityCount = 3, facilityHp = 6, civilianCount = 4,
                timeLimitSeconds = 300f, minSeconds = 60f,
                winGc = 250, firstWinBonusGc = 150, winXp = 120, civilianBonusGc = 10, facilityBonusGc = 20,
                winDefensePoints = 80, unlockLevel = 1,
            };
            mission.groups.Add(new WaveGroupDef { enemyCode = EnemyCodes.MalfunctionRobot, count = 4, interval = 3f, delay = 3f });
            mission.groups.Add(new WaveGroupDef { enemyCode = EnemyCodes.MechInsect, count = 6, interval = 1.2f, delay = 22f });
            mission.groups.Add(new WaveGroupDef { enemyCode = EnemyCodes.MalfunctionRobot, count = 4, interval = 2.5f, delay = 42f });
            mission.groups.Add(new WaveGroupDef { enemyCode = EnemyCodes.MechInsect, count = 4, interval = 1f, delay = 60f });
            combat.fpsMissions.Add(mission);

            combat.facilities.Add(new FacilityDef { code = FacilityCodes.PowerPlant, name = "발전소", repairGc = 150, maxDamage = 3 });
            combat.facilities.Add(new FacilityDef { code = FacilityCodes.CommCenter, name = "통신 센터", repairGc = 150, maxDamage = 3 });
            combat.facilities.Add(new FacilityDef { code = FacilityCodes.WaterPlant, name = "정수장", repairGc = 150, maxDamage = 3 });

            combat.dailyFpsRewardLimit = 5;
            combat.risk = RiskPolicy.CreateDefault();
            combat.ranking = new RankingWeights();
            return combat;
        }
    }
}
