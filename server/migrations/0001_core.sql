-- CITY GUARDIANS 0001: v8.0 전투·도시 위험도 기능이 기대는 최소한의 공통 테이블.
--
-- 추가만 한다. 이미 있는 테이블은 건드리지 않는다 (CREATE TABLE IF NOT EXISTS).
-- 같은 이름의 테이블이 다른 구조로 이미 있으면 bin/migrate.php 의 사전 점검이 먼저 멈춘다
-- (migrations/requirements.php). 구조는 docs/DATABASE.md 2~3장의 목표 스키마를 따른다.
-- 시각은 모두 UTC 로 저장한다. JSON 은 MySQL 5.7 / 8 / MariaDB 에서 똑같이 동작하도록 TEXT 에 담는다.

CREATE TABLE IF NOT EXISTS players (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id        CHAR(16)        NOT NULL,
  status           VARCHAR(16)     NOT NULL DEFAULT 'active',     -- active, suspended, deleted
  residency_status VARCHAR(16)     NOT NULL DEFAULT 'stranger',   -- stranger, resident
  nickname         VARCHAR(16)     NULL,
  region_id        BIGINT UNSIGNED NULL,
  registered_at    DATETIME        NULL,
  tutorial_step    SMALLINT UNSIGNED NOT NULL DEFAULT 0,
  created_at       DATETIME        NOT NULL,
  last_seen_at     DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_players_public_id (public_id),
  UNIQUE KEY uq_players_nickname (nickname),
  KEY ix_players_region (region_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS player_credentials (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id   BIGINT UNSIGNED NOT NULL,
  type        VARCHAR(20)     NOT NULL,            -- guest
  identifier  VARCHAR(190)    NULL,
  secret_hash VARCHAR(255)    NOT NULL,            -- password_hash()
  created_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_cred_type_identifier (type, identifier),
  KEY ix_cred_player (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS player_sessions (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id      BIGINT UNSIGNED NOT NULL,
  token_hash     CHAR(64)        NOT NULL,         -- SHA-256. 토큰 자체는 저장하지 않는다
  platform       VARCHAR(16)     NOT NULL DEFAULT '',
  client_version VARCHAR(16)     NOT NULL DEFAULT '',
  created_at     DATETIME        NOT NULL,
  expires_at     DATETIME        NOT NULL,
  revoked_at     DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_sessions_token (token_hash),
  KEY ix_sessions_player (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS characters (
  player_id  BIGINT UNSIGNED NOT NULL,
  level      INT UNSIGNED    NOT NULL DEFAULT 1,
  xp         INT UNSIGNED    NOT NULL DEFAULT 0,
  appearance TEXT            NULL,
  updated_at DATETIME        NOT NULL,
  PRIMARY KEY (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS wallets (
  player_id  BIGINT UNSIGNED NOT NULL,
  balance    BIGINT          NOT NULL DEFAULT 0,
  updated_at DATETIME        NOT NULL,
  PRIMARY KEY (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 원장. 추가만 한다. 잔액이 바뀔 때마다 한 줄.
CREATE TABLE IF NOT EXISTS ledger_entries (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id     BIGINT UNSIGNED NOT NULL,
  account       VARCHAR(16)     NOT NULL DEFAULT 'wallet',
  account_ref   BIGINT UNSIGNED NULL,
  delta         BIGINT          NOT NULL,
  balance_after BIGINT          NOT NULL,
  reason        VARCHAR(32)     NOT NULL,
  ref_type      VARCHAR(32)     NULL,
  ref_id        BIGINT UNSIGNED NULL,
  txn_id        CHAR(26)        NOT NULL,
  created_at    DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_ledger_player_time (player_id, created_at),
  KEY ix_ledger_txn (txn_id),
  KEY ix_ledger_reason_time (reason, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS idempotency_keys (
  player_id    BIGINT UNSIGNED NOT NULL,
  idem_key     VARCHAR(64)     NOT NULL,
  endpoint     VARCHAR(64)     NOT NULL,
  request_hash CHAR(64)        NOT NULL,
  status_code  SMALLINT        NOT NULL,
  response     MEDIUMTEXT      NOT NULL,
  created_at   DATETIME        NOT NULL,
  PRIMARY KEY (player_id, idem_key),
  KEY ix_idem_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS daily_counters (
  player_id BIGINT UNSIGNED NOT NULL,
  date      DATE            NOT NULL,              -- 한국 날짜
  counter   VARCHAR(32)     NOT NULL,
  value     BIGINT          NOT NULL DEFAULT 0,
  PRIMARY KEY (player_id, date, counter)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 요청 횟수 제한 (게스트 생성, 토큰 발급, 관리자 로그인). bucket 은 "종류:주소의 해시" 이다. 주소 자체는 저장하지 않는다.
CREATE TABLE IF NOT EXISTS rate_limits (
  bucket       VARCHAR(80) NOT NULL,
  window_start DATETIME    NOT NULL,
  hits         INT         NOT NULL DEFAULT 0,
  PRIMARY KEY (bucket, window_start),
  KEY ix_rate_limits_window (window_start)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS sidos (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code       VARCHAR(8)      NOT NULL,
  name       VARCHAR(40)     NOT NULL,
  short_name VARCHAR(16)     NOT NULL DEFAULT '',
  PRIMARY KEY (id),
  UNIQUE KEY uq_sidos_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS regions (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  sido_id        BIGINT UNSIGNED NOT NULL,
  code           VARCHAR(8)      NOT NULL,
  name           VARCHAR(40)     NOT NULL,
  defense_points BIGINT          NOT NULL DEFAULT 0,
  level          INT UNSIGNED    NOT NULL DEFAULT 1,
  resident_count INT UNSIGNED    NOT NULL DEFAULT 0,
  invasion_level INT UNSIGNED    NOT NULL DEFAULT 0,
  city_template  VARCHAR(32)     NULL,
  is_playable    TINYINT(1)      NOT NULL DEFAULT 0,
  PRIMARY KEY (id),
  UNIQUE KEY uq_regions_code (code),
  KEY ix_regions_sido (sido_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 플레이어별 도시 기여도. 플레이어가 쌓은 것만 들어간다 (NPC의 방어는 들어가지 않는다).
CREATE TABLE IF NOT EXISTS region_contributions (
  region_id  BIGINT UNSIGNED NOT NULL,
  player_id  BIGINT UNSIGNED NOT NULL,
  points     BIGINT          NOT NULL DEFAULT 0,
  updated_at DATETIME        NOT NULL,
  PRIMARY KEY (region_id, player_id),
  KEY ix_contrib_player (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS game_config (
  config_key VARCHAR(64) NOT NULL,
  value      TEXT        NOT NULL,                 -- JSON
  updated_at DATETIME    NOT NULL,
  PRIMARY KEY (config_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 정의·설정 변경 이력: 변경 전후, 관리자, 사유.
CREATE TABLE IF NOT EXISTS config_history (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  target_type  VARCHAR(40)     NOT NULL,
  target_key   VARCHAR(64)     NOT NULL,
  before_value MEDIUMTEXT      NULL,
  after_value  MEDIUMTEXT      NULL,
  admin_id     BIGINT UNSIGNED NULL,
  reason       VARCHAR(255)    NOT NULL DEFAULT '',
  created_at   DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_config_history_target (target_type, target_key, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS admins (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  login           VARCHAR(40)     NOT NULL,
  password_hash   VARCHAR(255)    NOT NULL,
  role            VARCHAR(16)     NOT NULL DEFAULT 'viewer',  -- super, operator, balance, support, viewer
  totp_secret     VARCHAR(64)     NULL,
  totp_last_step  BIGINT          NOT NULL DEFAULT 0,          -- 마지막으로 쓴 일회용 코드의 순번 (같은 코드를 두 번 받지 않는다)
  is_active       TINYINT(1)      NOT NULL DEFAULT 1,
  failed_attempts INT UNSIGNED    NOT NULL DEFAULT 0,
  locked_until    DATETIME        NULL,
  created_at      DATETIME        NOT NULL,
  last_login_at   DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_admins_login (login)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS admin_sessions (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  admin_id     BIGINT UNSIGNED NOT NULL,
  token_hash   CHAR(64)        NOT NULL,
  csrf_token   CHAR(64)        NOT NULL,
  ip           VARCHAR(45)     NOT NULL DEFAULT '',
  created_at   DATETIME        NOT NULL,
  last_seen_at DATETIME        NOT NULL,
  expires_at   DATETIME        NOT NULL,
  revoked_at   DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_admin_sessions_token (token_hash),
  KEY ix_admin_sessions_admin (admin_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS admin_audit_logs (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  admin_id    BIGINT UNSIGNED NULL,
  action      VARCHAR(64)     NOT NULL,
  target_type VARCHAR(40)     NOT NULL DEFAULT '',
  target_key  VARCHAR(64)     NOT NULL DEFAULT '',
  detail      TEXT            NULL,
  ip          VARCHAR(45)     NOT NULL DEFAULT '',
  created_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_audit_admin (admin_id, id),
  KEY ix_audit_time (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS system_logs (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  level      VARCHAR(16)     NOT NULL,             -- info, warn, error
  source     VARCHAR(40)     NOT NULL,
  message    VARCHAR(255)    NOT NULL,
  detail     TEXT            NULL,
  created_at DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_system_logs_time (created_at),
  KEY ix_system_logs_source (source, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
