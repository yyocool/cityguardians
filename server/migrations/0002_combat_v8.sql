-- CITY GUARDIANS 0002: v8.0 — 3종 전투 모드, 전투 장비, 도시 위험도, 침공, 지역 경쟁, 도시 간 지원.
--
-- 추가만 한다. 기존 테이블과 데이터는 건드리지 않는다.
-- 세 전투 모드는 계정·레벨·GC·소속 도시를 함께 쓴다. 여기에는 그 값을 모드별로 따로 저장하는 테이블이 없다:
--   레벨·경험치 = characters, GC = wallets + ledger_entries, 소속 = players.region_id.
-- 위험도는 서버만 바꾼다. 위험도 숫자를 받는 API 는 없고, 바뀔 때마다 city_risk_history 에 한 줄이 남는다.
-- 이 파일의 어느 테이블도 은행·대출·신용점수 테이블을 참조하지 않는다 (도시 위험도와 개인 금융은 독립).

-- ── 공통 전투 ────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS combat_modes (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code       VARCHAR(16)     NOT NULL,             -- defense, hunt, fps
  name       VARCHAR(40)     NOT NULL,
  summary    VARCHAR(255)    NOT NULL DEFAULT '',
  camera     VARCHAR(16)     NOT NULL,             -- strategy, third_person, first_person
  controls   VARCHAR(255)    NOT NULL DEFAULT '',
  is_closed  TINYINT(1)      NOT NULL DEFAULT 0,   -- 관리자가 모드를 잠시 닫을 수 있다
  sort_order INT             NOT NULL DEFAULT 0,
  updated_at DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_combat_modes_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 전투 장비 정의. 값(가격·성능)은 관리자에서 조정한다.
CREATE TABLE IF NOT EXISTS combat_equipment (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code               VARCHAR(40)     NOT NULL,
  name               VARCHAR(40)     NOT NULL,
  description        VARCHAR(255)    NOT NULL DEFAULT '',
  slot               VARCHAR(16)     NOT NULL,     -- primary, gadget, support
  kind               VARCHAR(16)     NOT NULL,
  modes              VARCHAR(64)     NOT NULL,     -- 쓸 수 있는 전투 모드 (쉼표로 구분)
  price_gc           INT             NOT NULL DEFAULT 0,   -- 0이면 처음부터 가진 무료 기본 장비
  unlock_level       INT             NOT NULL DEFAULT 1,
  max_level          INT             NOT NULL DEFAULT 1,
  upgrade_base_gc    INT             NOT NULL DEFAULT 0,
  upkeep_gc          INT             NOT NULL DEFAULT 0,
  damage             INT             NOT NULL DEFAULT 0,
  attacks_per_second DECIMAL(8,3)    NOT NULL DEFAULT 0,
  range_m            DECIMAL(8,3)    NOT NULL DEFAULT 0,
  projectile_speed   DECIMAL(8,3)    NOT NULL DEFAULT 0,
  clip_size          INT             NOT NULL DEFAULT 0,
  reload_seconds     DECIMAL(8,3)    NOT NULL DEFAULT 0,
  aim_zoom           DECIMAL(8,3)    NOT NULL DEFAULT 1,
  cone_degrees       DECIMAL(8,3)    NOT NULL DEFAULT 0,
  knockback          DECIMAL(8,3)    NOT NULL DEFAULT 0,
  cooldown_seconds   DECIMAL(8,3)    NOT NULL DEFAULT 0,
  effect_seconds     DECIMAL(8,3)    NOT NULL DEFAULT 0,
  effect_radius      DECIMAL(8,3)    NOT NULL DEFAULT 0,
  effect_value       INT             NOT NULL DEFAULT 0,
  is_coming_soon     TINYINT(1)      NOT NULL DEFAULT 0,
  is_active          TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order         INT             NOT NULL DEFAULT 0,
  updated_at         DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_combat_equipment_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 가진 장비. 한 사람이 한 장비를 한 점만 가진다. 무료 기본 장비는 행이 없어도 가진 것으로 본다.
CREATE TABLE IF NOT EXISTS player_combat_equipment (
  player_id    BIGINT UNSIGNED NOT NULL,
  equipment_id BIGINT UNSIGNED NOT NULL,
  level        INT             NOT NULL DEFAULT 1,
  acquired_at  DATETIME        NOT NULL,
  updated_at   DATETIME        NOT NULL,
  PRIMARY KEY (player_id, equipment_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 장비 강화 기록. 추가만 한다.
CREATE TABLE IF NOT EXISTS equipment_upgrades (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id    BIGINT UNSIGNED NOT NULL,
  equipment_id BIGINT UNSIGNED NOT NULL,
  from_level   INT             NOT NULL,
  to_level     INT             NOT NULL,
  cost_gc      INT             NOT NULL,
  created_at   DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_equipment_upgrades_player (player_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 모드별 장착. 가진 장비는 하나이고, 어느 모드의 어느 칸에 꼈는지만 여기에 적는다.
CREATE TABLE IF NOT EXISTS combat_loadouts (
  player_id    BIGINT UNSIGNED NOT NULL,
  mode_code    VARCHAR(16)     NOT NULL,
  slot         VARCHAR(16)     NOT NULL,
  equipment_id BIGINT UNSIGNED NOT NULL,
  updated_at   DATETIME        NOT NULL,
  PRIMARY KEY (player_id, mode_code, slot)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 전투와 지원에 관한 플레이어의 상태. 소속 도시는 여기에 없다 (players.region_id 하나뿐이다).
CREATE TABLE IF NOT EXISTS player_combat_state (
  player_id          BIGINT UNSIGNED NOT NULL,
  last_active_at     DATETIME        NULL,         -- 마지막으로 전투에 참여했거나 전입한 시각 (활성 가디언 판정)
  hunt_kill_carry    INT             NOT NULL DEFAULT 0,   -- 위험도에 아직 반영되지 않은 사냥 처치 수
  support_request_id BIGINT UNSIGNED NULL,         -- 지금 지원 나가 있는 요청
  updated_at         DATETIME        NOT NULL,
  PRIMARY KEY (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 적 정의.
CREATE TABLE IF NOT EXISTS enemy_defs (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code               VARCHAR(40)     NOT NULL,
  name               VARCHAR(40)     NOT NULL,
  max_hp             INT             NOT NULL,
  damage             INT             NOT NULL DEFAULT 0,
  move_speed         DECIMAL(8,3)    NOT NULL DEFAULT 0,
  detect_range       DECIMAL(8,3)    NOT NULL DEFAULT 0,
  attack_range       DECIMAL(8,3)    NOT NULL DEFAULT 0,
  attack_windup      DECIMAL(8,3)    NOT NULL DEFAULT 0,
  attack_interval    DECIMAL(8,3)    NOT NULL DEFAULT 0,
  lose_range         DECIMAL(8,3)    NOT NULL DEFAULT 0,
  reward_gc          INT             NOT NULL DEFAULT 0,
  reward_xp          INT             NOT NULL DEFAULT 0,
  base_damage        INT             NOT NULL DEFAULT 1,
  reward_energy      INT             NOT NULL DEFAULT 0,
  aggro_seconds      DECIMAL(8,3)    NOT NULL DEFAULT 0,
  attacks_structures TINYINT(1)      NOT NULL DEFAULT 0,
  strike_radius      DECIMAL(8,3)    NOT NULL DEFAULT 0,
  structure_damage   INT             NOT NULL DEFAULT 0,
  stagger_immune     TINYINT(1)      NOT NULL DEFAULT 0,
  is_active          TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order         INT             NOT NULL DEFAULT 0,
  updated_at         DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_enemy_defs_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── 디펜스 모드 ──────────────────────────────────────────────

-- 방어 구역 (스테이지). waves 는 JSON: [{"groups":[{"enemyCode","count","interval","delay"}]}]
CREATE TABLE IF NOT EXISTS defense_zones (
  id                   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code                 VARCHAR(40)     NOT NULL,
  name                 VARCHAR(40)     NOT NULL,
  base_hp              INT             NOT NULL,
  start_energy         INT             NOT NULL DEFAULT 0,
  wave_clear_energy    INT             NOT NULL DEFAULT 0,
  rest_seconds         DECIMAL(8,3)    NOT NULL DEFAULT 0,
  waves                MEDIUMTEXT      NOT NULL,
  win_gc               INT             NOT NULL DEFAULT 0,
  first_win_bonus_gc   INT             NOT NULL DEFAULT 0,
  win_xp               INT             NOT NULL DEFAULT 0,
  lose_gc_per_wave     INT             NOT NULL DEFAULT 0,
  min_seconds_per_wave DECIMAL(8,3)    NOT NULL DEFAULT 0,
  win_defense_points   INT             NOT NULL DEFAULT 0,
  unlock_level         INT             NOT NULL DEFAULT 1,
  is_active            TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order           INT             NOT NULL DEFAULT 0,
  updated_at           DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_zones_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 타워와 방어시설 정의.
--   ownership = battle    전투 중 설치형: 누구나 전투 에너지로 세운다
--               permanent 영구 보유형: GC 로 한 번 해금하면 그 뒤로 전투에서 세울 수 있다 (개인 자산)
--               city      도시 공용: 주민이 함께 기금을 모아 세운다 (개인 자산이 아니다)
CREATE TABLE IF NOT EXISTS defense_structures (
  id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code                VARCHAR(40)     NOT NULL,
  name                VARCHAR(40)     NOT NULL,
  description         VARCHAR(255)    NOT NULL DEFAULT '',
  tier                VARCHAR(16)     NOT NULL DEFAULT 'basic',   -- basic, mid, advanced, special
  kind                VARCHAR(16)     NOT NULL DEFAULT 'gun',
  ownership           VARCHAR(16)     NOT NULL DEFAULT 'battle',
  unlock_gc           INT             NOT NULL DEFAULT 0,   -- 가격 (영구 해금)
  energy_cost         INT             NOT NULL DEFAULT 0,   -- 설치 비용 (전투 에너지)
  damage              INT             NOT NULL DEFAULT 0,   -- 공격력
  shots_per_second    DECIMAL(8,3)    NOT NULL DEFAULT 0,   -- 속도
  range_m             DECIMAL(8,3)    NOT NULL DEFAULT 0,   -- 범위
  splash_radius       DECIMAL(8,3)    NOT NULL DEFAULT 0,
  can_hit_air         TINYINT(1)      NOT NULL DEFAULT 0,
  max_hp              INT             NOT NULL DEFAULT 0,   -- 내구도
  upkeep_gc           INT             NOT NULL DEFAULT 0,   -- 유지 비용
  upgrade_energy_cost INT             NOT NULL DEFAULT 0,   -- 업그레이드 비용 (전투 중, 에너지)
  max_upgrade_level   INT             NOT NULL DEFAULT 0,
  max_per_battle      INT             NOT NULL DEFAULT 0,   -- 설치 제한 (0이면 제한 없음)
  unlock_level        INT             NOT NULL DEFAULT 1,   -- 해금 조건 (레벨)
  effect_value        INT             NOT NULL DEFAULT 0,
  effect_seconds      DECIMAL(8,3)    NOT NULL DEFAULT 0,
  city_goal_gc        INT             NOT NULL DEFAULT 0,   -- 도시 공용: 한 단계에 필요한 기금
  city_power          INT             NOT NULL DEFAULT 0,   -- 도시 공용: 한 단계마다 오르는 도시 방어력
  city_max_level      INT             NOT NULL DEFAULT 0,
  is_coming_soon      TINYINT(1)      NOT NULL DEFAULT 0,
  is_active           TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order          INT             NOT NULL DEFAULT 0,
  updated_at          DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_structures_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 개인이 영구 해금한 방어시설 (개인 자산).
CREATE TABLE IF NOT EXISTS player_defense_structures (
  player_id    BIGINT UNSIGNED NOT NULL,
  structure_id BIGINT UNSIGNED NOT NULL,
  level        INT             NOT NULL DEFAULT 1,
  unlocked_at  DATETIME        NOT NULL,
  PRIMARY KEY (player_id, structure_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 도시 공용 방어시설. 개인 자산과 따로 관리한다: 도시에 속하고 주민 누구의 것도 아니다.
CREATE TABLE IF NOT EXISTS city_defense_structures (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  region_id    BIGINT UNSIGNED NOT NULL,
  structure_id BIGINT UNSIGNED NOT NULL,
  funded_gc    BIGINT          NOT NULL DEFAULT 0,   -- 지금까지 모인 기금의 합
  updated_at   DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_city_structures (region_id, structure_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 방어전 한 판. 시작할 때 만들고, 끝나면 결과를 채운다. 걸린 시간은 서버가 잰다.
CREATE TABLE IF NOT EXISTS defense_battles (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id      CHAR(16)        NOT NULL,
  player_id      BIGINT UNSIGNED NOT NULL,
  zone_id        BIGINT UNSIGNED NOT NULL,
  region_id      BIGINT UNSIGNED NULL,             -- 결과가 반영된 도시. 이방인이면 NULL
  invasion_id    BIGINT UNSIGNED NULL,
  status         VARCHAR(16)     NOT NULL DEFAULT 'active',   -- active, finished
  started_at     DATETIME        NOT NULL,
  ended_at       DATETIME        NULL,
  result         VARCHAR(8)      NULL,             -- win, lose
  accepted       TINYINT(1)      NULL,             -- 서버 검증을 통과했는가
  reject_reason  VARCHAR(40)     NULL,
  waves_cleared  INT             NOT NULL DEFAULT 0,
  base_hp_left   INT             NOT NULL DEFAULT 0,
  kills          TEXT            NULL,             -- JSON
  reward_gc      INT             NOT NULL DEFAULT 0,
  reward_xp      INT             NOT NULL DEFAULT 0,
  first_win      TINYINT(1)      NOT NULL DEFAULT 0,
  defense_points INT             NOT NULL DEFAULT 0,
  response       MEDIUMTEXT      NULL,             -- 처음 돌려준 응답. 다시 보고하면 이것을 그대로 돌려준다
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_battles_public (public_id),
  KEY ix_defense_battles_player (player_id, zone_id, result),
  KEY ix_defense_battles_time (ended_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── 헌트 모드 ────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS hunt_zone_defs (
  id                   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code                 VARCHAR(40)     NOT NULL,
  name                 VARCHAR(40)     NOT NULL,
  enemy_codes          VARCHAR(255)    NOT NULL,   -- 쉼표로 구분
  max_alive            INT             NOT NULL DEFAULT 1,
  respawn_seconds      DECIMAL(8,3)    NOT NULL DEFAULT 0,
  max_kills_per_minute DECIMAL(8,3)    NOT NULL DEFAULT 0,
  burst_allowance      INT             NOT NULL DEFAULT 0,
  is_active            TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order           INT             NOT NULL DEFAULT 0,
  updated_at           DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_hunt_zone_defs_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS hunt_sessions (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id       CHAR(16)        NOT NULL,
  player_id       BIGINT UNSIGNED NOT NULL,
  zone_id         BIGINT UNSIGNED NOT NULL,
  started_at      DATETIME        NOT NULL,
  last_report_at  DATETIME        NOT NULL,
  allowance       DECIMAL(12,4)   NOT NULL DEFAULT 0,   -- 쓰지 않고 남은 처치 허용량
  next_seq        INT             NOT NULL DEFAULT 1,
  kills_total     INT             NOT NULL DEFAULT 0,
  reward_gc_total INT             NOT NULL DEFAULT 0,
  reward_xp_total INT             NOT NULL DEFAULT 0,
  rejected_total  INT             NOT NULL DEFAULT 0,
  last_response   MEDIUMTEXT      NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_hunt_sessions_public (public_id),
  KEY ix_hunt_sessions_player (player_id, id),
  KEY ix_hunt_sessions_time (last_report_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── FPS 모드 ─────────────────────────────────────────────────

-- FPS 임무 정의. enemy_groups 는 JSON: [{"enemyCode","count","interval","delay"}]
CREATE TABLE IF NOT EXISTS fps_missions (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code               VARCHAR(40)     NOT NULL,
  name               VARCHAR(40)     NOT NULL,
  description        VARCHAR(255)    NOT NULL DEFAULT '',
  enemy_groups       MEDIUMTEXT      NOT NULL,
  facility_count     INT             NOT NULL,
  facility_hp        INT             NOT NULL,
  civilian_count     INT             NOT NULL DEFAULT 0,
  time_limit_seconds DECIMAL(8,3)    NOT NULL,
  min_seconds        DECIMAL(8,3)    NOT NULL DEFAULT 0,
  win_gc             INT             NOT NULL DEFAULT 0,
  first_win_bonus_gc INT             NOT NULL DEFAULT 0,
  win_xp             INT             NOT NULL DEFAULT 0,
  civilian_bonus_gc  INT             NOT NULL DEFAULT 0,
  facility_bonus_gc  INT             NOT NULL DEFAULT 0,
  win_defense_points INT             NOT NULL DEFAULT 0,
  unlock_level       INT             NOT NULL DEFAULT 1,
  is_active          TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order         INT             NOT NULL DEFAULT 0,
  updated_at         DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_fps_missions_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- FPS 임무 한 번의 기록과 결과.
CREATE TABLE IF NOT EXISTS fps_mission_results (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id        CHAR(16)        NOT NULL,
  player_id        BIGINT UNSIGNED NOT NULL,
  mission_id       BIGINT UNSIGNED NOT NULL,
  region_id        BIGINT UNSIGNED NULL,
  invasion_id      BIGINT UNSIGNED NULL,
  status           VARCHAR(16)     NOT NULL DEFAULT 'active',
  started_at       DATETIME        NOT NULL,
  ended_at         DATETIME        NULL,
  result           VARCHAR(8)      NULL,
  accepted         TINYINT(1)      NULL,
  reject_reason    VARCHAR(40)     NULL,
  kills            TEXT            NULL,
  facilities_saved INT             NOT NULL DEFAULT 0,
  civilians_saved  INT             NOT NULL DEFAULT 0,
  reward_gc        INT             NOT NULL DEFAULT 0,
  reward_xp        INT             NOT NULL DEFAULT 0,
  first_win        TINYINT(1)      NOT NULL DEFAULT 0,
  reward_limited   TINYINT(1)      NOT NULL DEFAULT 0,
  defense_points   INT             NOT NULL DEFAULT 0,
  response         MEDIUMTEXT      NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_fps_results_public (public_id),
  KEY ix_fps_results_player (player_id, mission_id, result),
  KEY ix_fps_results_time (ended_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── 도시 위험도 ──────────────────────────────────────────────

-- 도시 하나의 위험도와 집계. risk_centi 는 1/100 단위 (0~10000), 화면에는 0~100.
-- 플레이어의 것(player_*, contribution)과 NPC 의 것(npc_held_count)을 따로 센다.
CREATE TABLE IF NOT EXISTS city_risk_status (
  region_id               BIGINT UNSIGNED NOT NULL,
  risk_centi              INT             NOT NULL DEFAULT 0,
  stabilized_date         DATE            NULL,     -- 하루 한 번의 NPC 순찰을 마지막으로 반영한 날 (한국 날짜)
  player_wins             INT             NOT NULL DEFAULT 0,
  player_attempts         INT             NOT NULL DEFAULT 0,
  invasion_severity_total INT             NOT NULL DEFAULT 0,
  repelled_severity       INT             NOT NULL DEFAULT 0,
  npc_held_count          INT             NOT NULL DEFAULT 0,
  failed_count            INT             NOT NULL DEFAULT 0,
  contribution            BIGINT          NOT NULL DEFAULT 0,
  support_given           BIGINT          NOT NULL DEFAULT 0,
  support_received        BIGINT          NOT NULL DEFAULT 0,
  updated_at              DATETIME        NOT NULL,
  PRIMARY KEY (region_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 위험도 변경 이력. 추가만 한다. 누가(actor) 왜(reason) 얼마나 바꿨는지 남는다.
CREATE TABLE IF NOT EXISTS city_risk_history (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  region_id   BIGINT UNSIGNED NOT NULL,
  at          DATETIME        NOT NULL,
  reason      VARCHAR(32)     NOT NULL,
  actor       VARCHAR(16)     NOT NULL,            -- player, supporter, npc, system, admin
  delta_centi INT             NOT NULL,
  after_centi INT             NOT NULL,
  stage       VARCHAR(16)     NOT NULL,
  reference   VARCHAR(100)    NULL,
  player_id   BIGINT UNSIGNED NULL,
  admin_id    BIGINT UNSIGNED NULL,
  PRIMARY KEY (id),
  KEY ix_risk_history_region (region_id, id),
  KEY ix_risk_history_time (at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 위험도 계산 정책. 관리자가 고칠 때마다 새 행이 생기고, 가장 최근 행이 지금의 정책이다 (이전 정책이 이력으로 남는다).
CREATE TABLE IF NOT EXISTS city_risk_policy (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  policy     MEDIUMTEXT      NOT NULL,             -- JSON: 가중치, 단계 경계, 침공 빈도, 보정 값
  ranking    TEXT            NOT NULL,             -- JSON: 지역 랭킹 가중치
  admin_id   BIGINT UNSIGNED NULL,
  reason     VARCHAR(255)    NOT NULL DEFAULT '',
  created_at DATETIME        NOT NULL,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 침공 판정의 진행 상태. 한 행뿐이다. 판정은 이 행을 잠그고 처리해 겹치지 않는다.
CREATE TABLE IF NOT EXISTS city_world_state (
  id         TINYINT UNSIGNED NOT NULL,
  last_tick  BIGINT           NOT NULL DEFAULT -1,  -- 마지막으로 처리한 판정 순번. 처음에는 -1
  seed       INT              NOT NULL,
  updated_at DATETIME         NOT NULL,
  PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 침공 이벤트. 한 도시에 진행 중인 침공은 하나다. (region_id, tick) 이 유일해 같은 판정이 두 번 돌아도 두 번 생기지 않는다.
CREATE TABLE IF NOT EXISTS city_invasion_events (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  region_id   BIGINT UNSIGNED NOT NULL,
  tick        BIGINT          NOT NULL,
  kind        VARCHAR(16)     NOT NULL,            -- outskirts, intrusion, zero
  severity    TINYINT         NOT NULL,            -- 1~3
  status      VARCHAR(16)     NOT NULL DEFAULT 'active',   -- active, repelled, npc_held, failed
  source      VARCHAR(16)     NOT NULL DEFAULT 'system',   -- system, admin
  started_at  DATETIME        NOT NULL,
  ends_at     DATETIME        NOT NULL,
  resolved_at DATETIME        NULL,
  required    INT             NOT NULL DEFAULT 1,  -- 막는 데 필요한 플레이어의 승리 수
  progress    INT             NOT NULL DEFAULT 0,
  PRIMARY KEY (id),
  UNIQUE KEY uq_invasion_region_tick (region_id, tick),
  KEY ix_invasion_status (status, region_id),
  KEY ix_invasion_started (started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 도시의 주요 시설 정의.
CREATE TABLE IF NOT EXISTS city_facility_defs (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code       VARCHAR(40)     NOT NULL,
  name       VARCHAR(40)     NOT NULL,
  repair_gc  INT             NOT NULL DEFAULT 0,
  max_damage INT             NOT NULL DEFAULT 3,
  is_active  TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order INT             NOT NULL DEFAULT 0,
  updated_at DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_city_facility_defs_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 도시 주요 시설의 피해 단계.
CREATE TABLE IF NOT EXISTS city_facility_damage (
  region_id   BIGINT UNSIGNED NOT NULL,
  facility_id BIGINT UNSIGNED NOT NULL,
  damage      INT             NOT NULL DEFAULT 0,
  updated_at  DATETIME        NOT NULL,
  PRIMARY KEY (region_id, facility_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── 도시 간 지원 ─────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS city_support_requests (
  id        BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  region_id BIGINT UNSIGNED NOT NULL,
  status    VARCHAR(16)     NOT NULL DEFAULT 'open',   -- open, resolved, expired
  opened_at DATETIME        NOT NULL,
  closes_at DATETIME        NOT NULL,
  closed_at DATETIME        NULL,
  wins      INT             NOT NULL DEFAULT 0,    -- 지원 온 가디언의 승리 수
  points    BIGINT          NOT NULL DEFAULT 0,
  PRIMARY KEY (id),
  KEY ix_support_region (region_id, status),
  KEY ix_support_status (status, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 지원 기여도. 지원에 나선 가디언마다 한 행. 소속 도시(home_region_id)는 참고용으로 적어 둘 뿐 바뀌지 않는다.
CREATE TABLE IF NOT EXISTS city_support_contributions (
  request_id     BIGINT UNSIGNED NOT NULL,
  player_id      BIGINT UNSIGNED NOT NULL,
  home_region_id BIGINT UNSIGNED NOT NULL,
  wins           INT             NOT NULL DEFAULT 0,
  points         BIGINT          NOT NULL DEFAULT 0,
  joined_at      DATETIME        NOT NULL,
  updated_at     DATETIME        NOT NULL,
  PRIMARY KEY (request_id, player_id),
  KEY ix_support_contrib_player (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
