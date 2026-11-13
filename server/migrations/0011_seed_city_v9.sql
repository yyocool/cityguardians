-- CITY GUARDIANS 0011: v9.0 초기 데이터 — 도시 확장 기준, 팀 역할, 세금 정책, 설정.
--
-- INSERT IGNORE 만 쓴다: 이미 있는 값(관리자가 고친 값)은 건드리지 않는다.
-- 세금 정책은 모두 꺼진 채로 들어간다. 협동 방어전과 참여예산도 설정에서 꺼져 있다.
-- 켜기 전까지 v8.0 의 값과 동작은 하나도 달라지지 않는다 (docs/DEVELOPMENT_PLAN.md v9.0).

INSERT IGNORE INTO city_growth_milestones
  (code, name, threshold, action, description, opens_street, is_active, sort_order, updated_at)
VALUES
  ('landmark_review', '상징물 제작 검토', 100, 'landmark_review', '도시를 대표하는 상징물 하나를 만들지 검토합니다.', 0, 1, 10, UTC_TIMESTAMP()),
  ('street_candidate', '상징거리 제작 후보', 500, 'street_candidate', '상징거리를 만들 후보 도시가 됩니다.', 0, 1, 20, UTC_TIMESTAMP()),
  ('street_open', '상징거리 개방 목표', 1000, 'street_open', '상징거리를 제작·검수해 정식으로 개방하는 것을 목표로 합니다.', 1, 1, 30, UTC_TIMESTAMP()),
  ('special_zone_review', '추가 특화 구역 검토', 3000, 'special_zone_review', '상징거리 밖의 특화 구역을 더 만들지 검토합니다.', 0, 1, 40, UTC_TIMESTAMP());

INSERT IGNORE INTO defense_team_roles
  (code, name, description, build_discount_percent, repair_bonus_percent, attack_bonus_percent, ping_cooldown_percent, max_per_session, is_active, sort_order, updated_at)
VALUES
  ('commander', '지휘관', '전술 핑을 더 자주 쓸 수 있어요. 어디를 막을지 팀에 알려 주세요.', 0, 0, 0, 50, 1, 1, 10, UTC_TIMESTAMP()),
  ('builder', '타워 설치 담당', '타워를 세우고 강화할 때 공동 예산이 덜 들어요.', 10, 0, 0, 0, 0, 1, 20, UTC_TIMESTAMP()),
  ('support', '수리 및 지원 담당', '부서진 타워를 더 빨리 고쳐요.', 0, 50, 0, 0, 0, 1, 30, UTC_TIMESTAMP()),
  ('fighter', '직접 전투 담당', '직접 공격의 피해가 커요. 타워가 놓친 로봇을 맡아 주세요.', 0, 0, 20, 0, 0, 1, 40, UTC_TIMESTAMP());

INSERT IGNORE INTO city_tax_policies
  (tax_type, name, description, is_active, rate_bp, applies_to, exempt_amount, min_level, grace_days, cap_gc, period, updated_at)
VALUES
  ('trade', '거래세', '장비와 타워를 살 때 치르는 값에 들어 있는 세금이에요. 값이 더 비싸지지 않아요.', 0, 300, 'gear_purchase,gear_upgrade,tower_unlock', 100, 3, 7, 0, 'transaction', UTC_TIMESTAMP()),
  ('business', '사업세', '전투 보상으로 번 GC 가운데 하루 면제액을 넘는 부분에 붙는 세금이에요.', 0, 500, 'battle_reward,fps_reward,hunt_reward', 1000, 5, 14, 300, 'transaction', UTC_TIMESTAMP()),
  ('property', '재산세', '가진 GC 가운데 면제액을 넘는 부분에 한 주에 한 번 붙는 세금이에요.', 0, 100, '', 10000, 10, 30, 500, 'weekly', UTC_TIMESTAMP());

INSERT IGNORE INTO game_config
  (config_key, value, updated_at)
VALUES
  ('growth', '{"populationMode":1,"provenMinLevel":3,"provenMinAccountDays":3,"provenMinActiveDays":2,"moveEnabled":1,"moveFeeGc":1000,"moveCooldownDays":7}', UTC_TIMESTAMP()),
  ('coop', '{"enabled":0,"defaultPlayers":4,"maxPlayers":8,"lobbyMinutes":15,"sessionMaxMinutes":60,"dailyRewardLimit":5,"hpScalePercentPerPlayer":60,"energyScalePercentPerPlayer":50,"minParticipationPercent":30,"allowLateJoin":1,"ticketSeconds":60,"minPlayersToStart":2,"cityStructureGatePercent":5,"playerDamage":12,"playerDamagePerLevelPercent":5,"playerRangeM":14,"playerShotsPer10s":30,"repairHp":30,"repairEnergy":10}', UTC_TIMESTAMP()),
  ('ping', '{"enabled":1,"burst":3,"refillSeconds":4,"lifetimeSeconds":8,"maxActivePerPlayer":3,"autoMuteStrikes":5,"autoMuteSeconds":30,"routeOverlay":1,"rangeOverlay":1}', UTC_TIMESTAMP()),
  ('treasury', '{"reservePercent":10,"absenteeDays":7}', UTC_TIMESTAMP()),
  ('budget', '{"enabled":0,"voteDays":7,"quorumVotes":5,"voterMinLevel":2,"voterMinResidentDays":3,"proposerMinLevel":3,"maxOpenProposalsPerPlayer":1,"maxRequestGc":50000}', UTC_TIMESTAMP());
