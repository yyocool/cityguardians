-- CITY GUARDIANS 0003: v8.0 전투 콘텐츠의 초기값.
--
-- bin/gen-seed.php 가 클라이언트의 초기 콘텐츠(tests/fixtures/defs.json)에서 만든 파일이다. 손으로 고치지 않는다.
-- INSERT IGNORE 만 쓴다: 이미 있는 행(관리자가 고친 값 포함)은 바꾸지 않는다.
-- 수치는 초기값이며 플레이테스트로 조정한다. 조정은 관리자 화면에서 한다.

INSERT IGNORE INTO combat_modes
  (code, name, summary, camera, controls, is_closed, sort_order, updated_at)
VALUES
  ('defense', '디펜스 모드', '타워를 세워 도시 외곽으로 몰려오는 로봇을 막아요.', 'third_person', 'E 타워 세우기·강화   V 전략 시점   좌클릭 공격', 0, 1, UTC_TIMESTAMP()),
  ('hunt', '헌트 모드', '도시 주변을 순찰하며 로봇을 직접 물리쳐요.', 'third_person', 'WASD 이동   좌클릭 공격   1·2 무기 바꾸기', 0, 2, UTC_TIMESTAMP()),
  ('fps', 'FPS 모드', '시내로 들어온 로봇을 1인칭으로 찾아 물리치고 시설과 시민을 지켜요.', 'first_person', 'WASD 이동   마우스 시점   좌클릭 발사   우클릭 조준   R 재장전   E 상호작용', 0, 3, UTC_TIMESTAMP());

INSERT IGNORE INTO combat_equipment
  (code, name, description, slot, kind, modes, price_gc, unlock_level, max_level, upgrade_base_gc, upkeep_gc, damage, attacks_per_second, range_m, projectile_speed, clip_size, reload_seconds, aim_zoom, cone_degrees, knockback, cooldown_seconds, effect_seconds, effect_radius, effect_value, is_coming_soon, sort_order, updated_at)
VALUES
  ('gear_energy_blaster', '에너지 블래스터', '빠르게 쏘는 기본 무기예요. 누구나 처음부터 가지고 있어요.', 'primary', 'blaster', 'fps', 0, 1, 3, 150, 0, 12, 5, 30, 60, 24, 1.4, 0.8, 0, 0, 0, 0, 0, 0, 0, 10, UTC_TIMESTAMP()),
  ('gear_precision_rifle', '정밀 에너지 라이플', '한 발이 세고 멀리 나가요. 대신 천천히 쏘고 탄창이 작아요.', 'primary', 'rifle', 'fps', 600, 4, 3, 300, 0, 55, 1.2, 60, 110, 6, 1.8, 0.45, 0, 0, 0, 0, 0, 0, 0, 20, UTC_TIMESTAMP()),
  ('gear_shockwave_launcher', '충격파 발사기', '가까운 로봇 여럿을 한꺼번에 밀어내요. 멀리는 닿지 않아요.', 'primary', 'shockwave', 'fps', 800, 5, 3, 350, 0, 30, 0.8, 7, 0, 4, 2, 0.9, 60, 2.5, 0, 0, 0, 0, 0, 30, UTC_TIMESTAMP()),
  ('gear_heal_device', '회복 장치', '체력을 채워요. 누구나 처음부터 가지고 있어요.', 'gadget', 'heal', 'fps', 0, 1, 3, 120, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 30, 0, 0, 40, 0, 40, UTC_TIMESTAMP()),
  ('gear_emp_device', 'EMP 장치', '가까운 로봇을 잠깐 멈춰요.', 'gadget', 'emp', 'fps', 500, 3, 3, 250, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 20, 3, 7, 0, 0, 50, UTC_TIMESTAMP()),
  ('gear_shield', '보호막', '잠깐 동안 받는 피해를 막아 줘요.', 'gadget', 'shield', 'fps', 700, 5, 3, 300, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 25, 6, 0, 60, 0, 60, UTC_TIMESTAMP()),
  ('gear_sensor', '탐지 센서', '벽 뒤의 로봇이 어디 있는지 잠깐 보여 줘요.', 'support', 'sensor', 'fps', 300, 2, 2, 200, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 15, 8, 45, 0, 0, 70, UTC_TIMESTAMP()),
  ('gear_support_drone', '지원 드론', '곁을 따라다니며 가까운 로봇을 쏴요.', 'support', 'drone', 'fps', 1200, 6, 3, 400, 0, 0, 2, 14, 0, 0, 0, 1, 0, 0, 40, 15, 0, 8, 0, 80, UTC_TIMESTAMP());

INSERT IGNORE INTO enemy_defs
  (code, name, max_hp, damage, move_speed, detect_range, attack_range, attack_windup, attack_interval, lose_range, reward_gc, reward_xp, base_damage, reward_energy, aggro_seconds, attacks_structures, strike_radius, structure_damage, stagger_immune, sort_order, updated_at)
VALUES
  ('tutorial_robot', '오작동 로봇', 50, 4, 2.2, 9, 1.6, 0.9, 1.8, 16, 10, 10, 1, 0, 0, 0, 0, 0, 0, 10, UTC_TIMESTAMP()),
  ('malfunction_robot', '오작동 로봇', 60, 8, 2.8, 10, 1.6, 0.7, 1.2, 18, 10, 10, 1, 6, 4, 0, 0, 0, 0, 20, UTC_TIMESTAMP()),
  ('mech_insect', '기계 곤충', 20, 4, 5, 11, 1.2, 0.6, 1, 18, 3, 3, 1, 3, 0, 0, 0, 0, 0, 30, UTC_TIMESTAMP()),
  ('construction_mecha', '건설 메카', 1500, 30, 1.5, 7, 2.6, 1.2, 2.6, 12, 120, 100, 5, 60, 3, 1, 3, 60, 1, 40, UTC_TIMESTAMP());

INSERT IGNORE INTO defense_zones
  (code, name, base_hp, start_energy, wave_clear_energy, rest_seconds, waves, win_gc, first_win_bonus_gc, win_xp, lose_gc_per_wave, min_seconds_per_wave, win_defense_points, unlock_level, sort_order, updated_at)
VALUES
  ('outskirts_1', '외곽 1구역', 20, 200, 50, 10, '[{"groups":[{"enemyCode":"malfunction_robot","count":6,"interval":2.5,"delay":0}]},{"groups":[{"enemyCode":"malfunction_robot","count":8,"interval":2,"delay":0},{"enemyCode":"mech_insect","count":16,"interval":0.6,"delay":14}]},{"groups":[{"enemyCode":"malfunction_robot","count":10,"interval":1.5,"delay":0},{"enemyCode":"mech_insect","count":30,"interval":0.4,"delay":14}]},{"groups":[{"enemyCode":"malfunction_robot","count":16,"interval":1.1,"delay":0},{"enemyCode":"mech_insect","count":36,"interval":0.35,"delay":14}]},{"groups":[{"enemyCode":"construction_mecha","count":1,"interval":0,"delay":0},{"enemyCode":"malfunction_robot","count":14,"interval":1.2,"delay":2},{"enemyCode":"mech_insect","count":40,"interval":0.35,"delay":16},{"enemyCode":"malfunction_robot","count":6,"interval":1.5,"delay":30},{"enemyCode":"mech_insect","count":12,"interval":0.4,"delay":44}]}]', 300, 200, 150, 20, 10, 100, 1, 10, UTC_TIMESTAMP());

INSERT IGNORE INTO defense_structures
  (code, name, description, tier, kind, ownership, unlock_gc, energy_cost, damage, shots_per_second, range_m, splash_radius, can_hit_air, max_hp, upkeep_gc, upgrade_energy_cost, max_upgrade_level, max_per_battle, unlock_level, effect_value, effect_seconds, city_goal_gc, city_power, city_max_level, is_coming_soon, sort_order, updated_at)
VALUES
  ('twr_machine_gun', '기관총 타워', '빠르게 쏴요. 기계 곤충처럼 작고 빠른 적에게 좋아요.', 'basic', 'gun', 'battle', 0, 100, 6, 5, 8, 0, 1, 120, 0, 60, 3, 0, 1, 0, 0, 0, 0, 0, 0, 10, UTC_TIMESTAMP()),
  ('twr_blast', '폭발 타워', '느리지만 한꺼번에 여럿을 맞혀요. 로봇 무리에게 좋아요.', 'basic', 'blast', 'permanent', 500, 175, 40, 0.4, 10, 2.5, 0, 150, 0, 105, 3, 0, 1, 0, 0, 0, 0, 0, 0, 20, UTC_TIMESTAMP()),
  ('twr_laser', '레이저 타워', '한 대상을 세게 지져요. 단단한 로봇에게 좋아요.', 'mid', 'laser', 'permanent', 900, 200, 70, 1, 11, 0, 1, 130, 0, 120, 3, 0, 4, 0, 0, 0, 0, 0, 0, 30, UTC_TIMESTAMP()),
  ('twr_slow', '감속 장치', '가까운 로봇을 느리게 해요. 다른 타워가 더 오래 쏠 수 있어요.', 'mid', 'slow', 'permanent', 600, 120, 0, 0, 6, 0, 0, 110, 0, 70, 2, 2, 2, 40, 0, 0, 0, 0, 0, 40, UTC_TIMESTAMP()),
  ('twr_emp', 'EMP 타워', '가끔 전자기 파동을 내보내 가까운 로봇을 잠깐 멈춰요.', 'mid', 'emp', 'permanent', 800, 150, 0, 0.2, 6, 0, 0, 130, 0, 90, 2, 2, 3, 0, 1.5, 0, 0, 0, 0, 50, UTC_TIMESTAMP()),
  ('twr_energy_shield', '에너지 방어막', '도시를 감싸는 방어막이에요. 주민이 함께 기금을 모아 세워요.', 'advanced', 'shield', 'city', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 5000, 40, 3, 0, 60, UTC_TIMESTAMP()),
  ('twr_command_center', '지휘 센터', 'NPC 수비대를 지휘해요. 침공을 더 잘 막아요.', 'advanced', 'command', 'city', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 8000, 60, 3, 0, 70, UTC_TIMESTAMP()),
  ('twr_emergency_support', '긴급 방어 지원 장치', '위급할 때 수비대를 바로 불러와요.', 'special', 'support', 'city', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 6000, 50, 2, 0, 80, UTC_TIMESTAMP()),
  ('twr_wall', '방어벽', '로봇이 지나가지 못하게 길을 막아요.', 'basic', 'wall', 'battle', 0, 60, 0, 0, 0, 0, 0, 400, 0, 0, 0, 4, 1, 0, 0, 0, 0, 0, 1, 90, UTC_TIMESTAMP()),
  ('twr_repair_drone', '자동 수리 드론', '부서진 타워를 스스로 고쳐요.', 'mid', 'repair', 'permanent', 700, 140, 0, 0, 7, 0, 0, 90, 0, 0, 0, 1, 3, 10, 0, 0, 0, 0, 1, 100, UTC_TIMESTAMP()),
  ('twr_anti_air', '대공 타워', '하늘의 보안 드론을 맞혀요.', 'advanced', 'anti_air', 'permanent', 1200, 180, 35, 1.5, 14, 0, 1, 120, 0, 110, 3, 0, 6, 0, 0, 0, 0, 0, 1, 110, UTC_TIMESTAMP()),
  ('twr_drone_hangar', '드론 격납고', '아군 드론을 내보내 로봇을 쫓아요.', 'advanced', 'hangar', 'permanent', 2000, 260, 10, 2, 16, 0, 0, 160, 0, 0, 0, 1, 8, 0, 0, 0, 0, 0, 1, 120, UTC_TIMESTAMP()),
  ('twr_wide_shock', '광역 전자기 충격 장치', '전장 전체의 로봇을 한 번에 멈춰요.', 'special', 'shock', 'permanent', 3000, 320, 0, 0, 30, 0, 0, 140, 0, 0, 0, 1, 10, 0, 3, 0, 0, 0, 1, 130, UTC_TIMESTAMP()),
  ('twr_ai_jammer', 'AI 교란 장치', '로봇이 길을 잃고 헤매게 해요.', 'special', 'jammer', 'permanent', 2500, 240, 0, 0, 9, 0, 0, 120, 0, 0, 0, 1, 9, 0, 4, 0, 0, 0, 1, 140, UTC_TIMESTAMP()),
  ('twr_mobile_robot', '이동식 방어 로봇', '전장을 돌아다니며 함께 싸워요.', 'special', 'robot', 'permanent', 2800, 280, 20, 2, 8, 0, 0, 300, 0, 0, 0, 1, 10, 0, 0, 0, 0, 0, 1, 150, UTC_TIMESTAMP());

INSERT IGNORE INTO hunt_zone_defs
  (code, name, enemy_codes, max_alive, respawn_seconds, max_kills_per_minute, burst_allowance, sort_order, updated_at)
VALUES
  ('home_street', '집 앞 거리', 'tutorial_robot', 1, 0, 2, 1, 10, UTC_TIMESTAMP()),
  ('zone_ne', '북동 전투 구역', 'malfunction_robot', 5, 8, 24, 5, 20, UTC_TIMESTAMP()),
  ('zone_nw', '북서 전투 구역', 'mech_insect,malfunction_robot', 9, 6, 50, 9, 30, UTC_TIMESTAMP());

INSERT IGNORE INTO fps_missions
  (code, name, description, enemy_groups, facility_count, facility_hp, civilian_count, time_limit_seconds, min_seconds, win_gc, first_win_bonus_gc, win_xp, civilian_bonus_gc, facility_bonus_gc, win_defense_points, unlock_level, sort_order, updated_at)
VALUES
  ('downtown_1', '도심 상가 구역', '시내로 들어온 로봇을 찾아 물리치고, 시설 세 곳과 시민을 지켜요.', '[{"enemyCode":"malfunction_robot","count":4,"interval":3,"delay":3},{"enemyCode":"mech_insect","count":6,"interval":1.2,"delay":22},{"enemyCode":"malfunction_robot","count":4,"interval":2.5,"delay":42},{"enemyCode":"mech_insect","count":4,"interval":1,"delay":60}]', 3, 6, 4, 300, 60, 250, 150, 120, 10, 20, 80, 1, 10, UTC_TIMESTAMP());

INSERT IGNORE INTO city_facility_defs
  (code, name, repair_gc, max_damage, sort_order, updated_at)
VALUES
  ('fac_power', '발전소', 150, 3, 10, UTC_TIMESTAMP()),
  ('fac_comm', '통신 센터', 150, 3, 20, UTC_TIMESTAMP()),
  ('fac_water', '정수장', 150, 3, 30, UTC_TIMESTAMP());

INSERT IGNORE INTO game_config
  (config_key, value, updated_at)
VALUES
  ('economy', '{"dailyHuntGcLimit":3000,"residencyBonusGc":500}', UTC_TIMESTAMP()),
  ('progression', '{"xpBase":50,"xpExponent":1.5,"baseMaxHp":100,"maxHpPerLevel":10,"maxLevel":50}', UTC_TIMESTAMP()),
  ('combat', '{"dailyFpsRewardLimit":5}', UTC_TIMESTAMP());

INSERT INTO city_risk_policy (policy, ranking, admin_id, reason, created_at)
SELECT '{"version":1,"weights":{"invasion_started":800,"defense_failed":300,"intrusion_failed":300,"facility_damaged":200,"emergency_unresolved":400,"zero_event":1000,"defense_won":-600,"hunt_completed":-100,"fps_repelled":-800,"facility_repaired":-200,"joint_defense":-300,"special_mission":-1000,"invasion_repelled":-300,"npc_held":-700},"cautionAt":25,"dangerAt":50,"emergencyAt":75,"initialRisk":20,"initialSpread":12,"activePlayerNorm":20,"activeDays":7,"huntKillsPerStep":10,"huntHighRiskPercent":200,"dailyPlayerReduceCap":20,"patrolPercent":50,"tickMinutes":60,"invasionChancePermille":40,"invasionMinutes":180,"intrusionPermille":350,"zeroEventPermille":20,"npcHoldBasePercent":80,"defensePowerPerPercent":20,"npcHoldMaxBonusPercent":20,"npcHoldSeverityPenaltyPercent":10,"dailyStabilizePercent":20,"baseGarrisonPower":100,"defensePointsPowerCap":300,"supportOpenAt":75,"supportCloseBelow":50,"supportMinutes":360,"contribDefenseWin":10,"contribFpsWin":12,"contribHuntStep":1,"contribRepair":5,"contribPerFund100Gc":2,"contribSeverityBonusPercent":50,"dailyRepairLimit":3,"dailyFundLimitGc":2000}',
       '{"safety":30,"successRate":20,"contribution":20,"response":20,"cooperation":10,"contributionTarget":100,"cooperationTarget":30}', NULL, '초기 정책', UTC_TIMESTAMP()
FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM city_risk_policy);

INSERT IGNORE INTO city_world_state (id, last_tick, seed, updated_at) VALUES (1, -1, 20261113, UTC_TIMESTAMP());
