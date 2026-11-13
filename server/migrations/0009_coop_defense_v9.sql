-- CITY GUARDIANS 0009: v9.0 협동 타워디펜스 — 공동 방어전, 팀원, 역할, 전술 핑, 공동 예산.
--
-- 추가만 한다. 혼자 하는 방어전(defense_battles)은 그대로다.
-- 전투 중의 상태(적 위치, 타워, 관문 내구도)는 여기에 없다. 그것은 실시간 서버가 메모리에서 권한을 갖고 처리하고,
-- 끝나면 결과와 기록(공동 예산 장부, 핑)을 이 테이블들에 넘긴다 (docs/COOP_DEFENSE_SYSTEM.md 3장).

-- 공동 방어전 한 판.
CREATE TABLE IF NOT EXISTS defense_sessions (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id        CHAR(16)        NOT NULL,
  region_id        BIGINT UNSIGNED NOT NULL,         -- 지키는 도시
  zone_id          BIGINT UNSIGNED NOT NULL,         -- defense_zones
  host_player_id   BIGINT UNSIGNED NOT NULL,
  invasion_id      BIGINT UNSIGNED NULL,
  status           VARCHAR(16)     NOT NULL DEFAULT 'lobby',   -- lobby, running, finished, cancelled, expired, abandoned
  max_players      INT UNSIGNED    NOT NULL DEFAULT 4,
  players_at_start INT UNSIGNED    NOT NULL DEFAULT 0,
  created_at       DATETIME        NOT NULL,
  expires_at       DATETIME        NOT NULL,         -- 이때까지 끝나지 않으면 정리한다
  started_at       DATETIME        NULL,
  ended_at         DATETIME        NULL,
  result           VARCHAR(8)      NULL,             -- win, lose
  waves_cleared    INT             NOT NULL DEFAULT 0,
  gate_hp_left     INT             NOT NULL DEFAULT 0,
  duration_seconds INT             NOT NULL DEFAULT 0,
  energy_granted   INT             NOT NULL DEFAULT 0,   -- 공동 예산: 받은 합
  energy_spent     INT             NOT NULL DEFAULT 0,   -- 공동 예산: 쓴 합
  energy_left      INT             NOT NULL DEFAULT 0,
  close_reason     VARCHAR(40)     NULL,
  response         MEDIUMTEXT      NULL,             -- 결과를 처음 반영했을 때의 응답. 다시 보고받으면 이것을 돌려준다
  updated_at       DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_sessions_public (public_id),
  KEY ix_defense_sessions_region (region_id, status, id),
  KEY ix_defense_sessions_status (status, expires_at),
  KEY ix_defense_sessions_host (host_player_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 팀원. 한 계정은 한 판에 한 번만 들어간다.
CREATE TABLE IF NOT EXISTS defense_session_members (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  session_id        BIGINT UNSIGNED NOT NULL,
  player_id         BIGINT UNSIGNED NOT NULL,
  role_code         VARCHAR(24)     NULL,            -- 고른 역할. 고르지 않아도 된다
  status            VARCHAR(16)     NOT NULL DEFAULT 'joined',   -- joined, left
  joined_at         DATETIME        NOT NULL,
  left_at           DATETIME        NULL,
  connected_seconds INT             NOT NULL DEFAULT 0,
  kills             INT             NOT NULL DEFAULT 0,
  towers_built      INT             NOT NULL DEFAULT 0,
  towers_upgraded   INT             NOT NULL DEFAULT 0,
  repairs           INT             NOT NULL DEFAULT 0,
  damage_dealt      BIGINT          NOT NULL DEFAULT 0,
  pings_sent        INT             NOT NULL DEFAULT 0,
  pings_rejected    INT             NOT NULL DEFAULT 0,
  eligible          TINYINT(1)      NULL,            -- 보상 대상이었는가
  reward_gc         INT             NOT NULL DEFAULT 0,
  reward_xp         INT             NOT NULL DEFAULT 0,
  reward_note       VARCHAR(40)     NULL,            -- 보상을 받지 못한 이유 등
  rewarded_at       DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_members (session_id, player_id),
  KEY ix_defense_members_player (player_id, status, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 팀 역할의 정의. 역할은 강제가 아니다. 고르면 작은 보정이 붙는다.
CREATE TABLE IF NOT EXISTS defense_team_roles (
  id                     BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code                   VARCHAR(24)     NOT NULL,
  name                   VARCHAR(40)     NOT NULL,
  description            VARCHAR(255)    NOT NULL DEFAULT '',
  build_discount_percent INT             NOT NULL DEFAULT 0,   -- 타워 설치·강화 비용 할인
  repair_bonus_percent   INT             NOT NULL DEFAULT 0,   -- 수리량 증가
  attack_bonus_percent   INT             NOT NULL DEFAULT 0,   -- 직접 공격 피해 증가
  ping_cooldown_percent  INT             NOT NULL DEFAULT 0,   -- 전술 핑 재사용 시간 단축
  max_per_session        INT             NOT NULL DEFAULT 0,   -- 한 판에 이 역할을 고를 수 있는 사람 수. 0 이면 제한 없음
  is_active              TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order             INT             NOT NULL DEFAULT 0,
  updated_at             DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_roles_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 전술 핑 기록. 실시간 서버가 판이 끝날 때 넘긴다. 남용을 살피는 데 쓴다.
CREATE TABLE IF NOT EXISTS defense_tactical_pings (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  session_id BIGINT UNSIGNED NOT NULL,
  seq        INT             NOT NULL,
  player_id  BIGINT UNSIGNED NOT NULL,
  kind       VARCHAR(24)     NOT NULL,   -- focus_fire, defend_here, build_here, repair_here, danger, rally
  x_centi    INT             NOT NULL DEFAULT 0,
  z_centi    INT             NOT NULL DEFAULT 0,
  at_ms      INT             NOT NULL DEFAULT 0,   -- 전투 시작부터의 시간
  accepted   TINYINT(1)      NOT NULL DEFAULT 1,   -- 0 이면 빈도 제한이나 차단으로 거절된 핑
  created_at DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_pings_seq (session_id, seq),
  KEY ix_defense_pings_player (player_id, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 공동 예산의 장부. 한 판 안에서 팀이 함께 쓰는 에너지다 (GC 가 아니다).
-- seq 가 유일해서 같은 줄이 두 번 들어가지 않는다. 줄마다 적힌 "바뀐 뒤 잔액"이 이어져야 한다.
CREATE TABLE IF NOT EXISTS defense_shared_budget (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  session_id     BIGINT UNSIGNED NOT NULL,
  seq            INT             NOT NULL,
  player_id      BIGINT UNSIGNED NULL,           -- 쓴 사람. 지급이면 NULL
  kind           VARCHAR(24)     NOT NULL,       -- grant_start, grant_wave, grant_kill, build, upgrade, repair
  delta          INT             NOT NULL,
  balance_after  INT             NOT NULL,
  slot_index     INT             NULL,
  structure_code VARCHAR(40)     NULL,
  at_ms          INT             NOT NULL DEFAULT 0,
  created_at     DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_defense_budget_seq (session_id, seq)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 전술 핑 차단. 관리자가 남용한 계정의 핑을 막는다. 팀원끼리의 "이 사람 핑 숨기기"는 실시간 서버가 그 판 안에서만 처리한다.
CREATE TABLE IF NOT EXISTS defense_ping_blocks (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id  BIGINT UNSIGNED NOT NULL,
  reason     VARCHAR(255)    NOT NULL DEFAULT '',
  admin_id   BIGINT UNSIGNED NULL,
  created_at DATETIME        NOT NULL,
  expires_at DATETIME        NULL,               -- NULL 이면 풀 때까지
  lifted_at  DATETIME        NULL,
  lifted_by  BIGINT UNSIGNED NULL,
  PRIMARY KEY (id),
  KEY ix_ping_blocks_player (player_id, lifted_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
