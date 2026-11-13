-- CITY GUARDIANS 0008: v9.0 도시 성장 — 유효 전입자 집계, 확장 단계, 기준 도달 알림, 상징거리 모델링 제작·배포.
--
-- 추가만 한다. 기존 테이블(players, regions 등)은 건드리지 않는다. 기존 regions.resident_count 는 그대로 "전입신고를 한 계정 수"다.
-- 여기의 valid_residents 는 그와 다른 값이다: 중복으로 만든 게스트 계정을 뺀 "유효 전입자 수" (docs/CITY_GROWTH_SYSTEM.md 2장).
-- 되돌리는 방법은 docs/DATABASE.md 9장.

-- 도시별 전입자 현황. 도시마다 한 행. 서버가 다시 세어 채운다 (CG\Service\Growth).
CREATE TABLE IF NOT EXISTS city_population_stats (
  region_id            BIGINT UNSIGNED NOT NULL,
  valid_residents      INT UNSIGNED    NOT NULL DEFAULT 0,   -- 현재 유효 전입자 수
  registered_residents INT UNSIGNED    NOT NULL DEFAULT 0,   -- 전입신고를 한 계정 수 (게스트 포함, 참고용)
  cumulative_residents INT UNSIGNED    NOT NULL DEFAULT 0,   -- 누적 유효 전입자 수 (이 도시에서 유효 전입으로 인정된 적이 있는 계정 수)
  growth_7d            INT             NOT NULL DEFAULT 0,   -- 최근 7일 증가량
  growth_30d           INT             NOT NULL DEFAULT 0,   -- 최근 30일 증가량
  stage                INT UNSIGNED    NOT NULL DEFAULT 0,   -- 도시 확장 단계 (달성한 기준의 수)
  stage_milestone_id   BIGINT UNSIGNED NULL,                 -- 가장 높이 달성한 기준
  modeling_threshold   INT UNSIGNED    NULL,                 -- 이 도시만의 상징거리 기준 인원. NULL 이면 공통 기준
  threshold_reached    TINYINT(1)      NOT NULL DEFAULT 0,   -- 상징거리 기준 달성 여부
  threshold_reached_at DATETIME        NULL,
  modeling_status      VARCHAR(16)     NOT NULL DEFAULT 'NOT_STARTED',   -- NOT_STARTED, PLANNING, MODELING, REVIEW, READY, DEPLOYED
  content_version      VARCHAR(32)     NULL,                 -- 적용된 모델링 버전
  opened_at            DATETIME        NULL,                 -- 정식 개방 일시
  counted_at           DATETIME        NULL,                 -- 마지막으로 다시 센 때
  updated_at           DATETIME        NOT NULL,
  PRIMARY KEY (region_id),
  KEY ix_population_valid (valid_residents)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 날마다의 전입자 수 (한국 날짜). 7일·30일 증가량과 추이 그래프에 쓴다.
CREATE TABLE IF NOT EXISTS city_population_daily (
  region_id            BIGINT UNSIGNED NOT NULL,
  date                 DATE            NOT NULL,
  valid_residents      INT UNSIGNED    NOT NULL DEFAULT 0,
  registered_residents INT UNSIGNED    NOT NULL DEFAULT 0,
  PRIMARY KEY (region_id, date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 유효 전입 인정 기록. 한 계정이 한 도시에서 처음 유효 전입으로 인정된 때. 누적 전입자 수의 근거다.
CREATE TABLE IF NOT EXISTS city_resident_marks (
  region_id        BIGINT UNSIGNED NOT NULL,
  player_id        BIGINT UNSIGNED NOT NULL,
  first_counted_at DATETIME        NOT NULL,
  PRIMARY KEY (region_id, player_id),
  KEY ix_resident_marks_player (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 전입·이사 기록. 추가만 한다.
CREATE TABLE IF NOT EXISTS city_residency_events (
  id              BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id       BIGINT UNSIGNED NOT NULL,
  region_id       BIGINT UNSIGNED NOT NULL,
  kind            VARCHAR(16)     NOT NULL,          -- register, move_in, move_out
  other_region_id BIGINT UNSIGNED NULL,              -- 이사라면 상대 도시
  fee_gc          INT             NOT NULL DEFAULT 0,
  created_at      DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_residency_events_player (player_id, id),
  KEY ix_residency_events_region (region_id, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 도시 확장 기준. 관리자가 인원을 고친다.
CREATE TABLE IF NOT EXISTS city_growth_milestones (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  code         VARCHAR(40)     NOT NULL,
  name         VARCHAR(60)     NOT NULL,
  threshold    INT UNSIGNED    NOT NULL,             -- 유효 전입자 수
  action       VARCHAR(24)     NOT NULL DEFAULT '',  -- landmark_review, street_candidate, street_open, special_zone_review
  description  VARCHAR(255)    NOT NULL DEFAULT '',
  opens_street TINYINT(1)      NOT NULL DEFAULT 0,   -- 이 기준이 "상징거리 모델링 기준 인원"인가
  is_active    TINYINT(1)      NOT NULL DEFAULT 1,
  sort_order   INT             NOT NULL DEFAULT 0,
  updated_at   DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_growth_milestones_code (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 관리자 알림. kind 로 "기준 도달 알림"(milestone)과 "모델링 진행 알림"(modeling)을 나눈다.
-- dedupe_key 가 유일해서 같은 도시의 같은 기준에 알림이 두 번 생기지 않는다.
CREATE TABLE IF NOT EXISTS city_growth_notifications (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  kind              VARCHAR(16)     NOT NULL,        -- milestone, modeling
  region_id         BIGINT UNSIGNED NOT NULL,
  milestone_id      BIGINT UNSIGNED NULL,
  project_id        BIGINT UNSIGNED NULL,
  dedupe_key        VARCHAR(80)     NOT NULL,
  title             VARCHAR(120)    NOT NULL,
  body              VARCHAR(500)    NOT NULL DEFAULT '',
  status            VARCHAR(16)     NOT NULL DEFAULT 'new',   -- new, acknowledged, assigned, closed
  acknowledged_by   BIGINT UNSIGNED NULL,
  acknowledged_at   DATETIME        NULL,
  assigned_admin_id BIGINT UNSIGNED NULL,
  assigned_by       BIGINT UNSIGNED NULL,
  assigned_at       DATETIME        NULL,
  closed_at         DATETIME        NULL,
  created_at        DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_growth_notifications_dedupe (dedupe_key),
  KEY ix_growth_notifications_status (status, kind, id),
  KEY ix_growth_notifications_region (region_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 모델링 제작 작업. 도시의 상징물·상징거리·특화 구역 하나가 한 작업이다.
CREATE TABLE IF NOT EXISTS city_modeling_projects (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id         CHAR(16)        NOT NULL,
  region_id         BIGINT UNSIGNED NOT NULL,
  milestone_id      BIGINT UNSIGNED NULL,
  notification_id   BIGINT UNSIGNED NULL,            -- 이 작업을 만든 계기가 된 알림
  kind              VARCHAR(16)     NOT NULL DEFAULT 'street',   -- landmark, street, special_zone
  title             VARCHAR(80)     NOT NULL,
  status            VARCHAR(16)     NOT NULL DEFAULT 'NOT_STARTED',
  assignee_admin_id BIGINT UNSIGNED NULL,            -- 담당자
  memo              TEXT            NULL,            -- 작업 메모
  due_on            DATE            NULL,            -- 예정일
  file_version      VARCHAR(32)     NOT NULL DEFAULT '',
  review_result     VARCHAR(16)     NOT NULL DEFAULT 'none',     -- none, pass, fail
  review_note       VARCHAR(500)    NOT NULL DEFAULT '',
  reviewed_by       BIGINT UNSIGNED NULL,
  reviewed_at       DATETIME        NULL,
  applied_at        DATETIME        NULL,            -- 적용 일시
  created_by        BIGINT UNSIGNED NULL,
  created_at        DATETIME        NOT NULL,
  updated_at        DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_modeling_projects_public (public_id),
  KEY ix_modeling_projects_region (region_id, id),
  KEY ix_modeling_projects_status (status, id),
  KEY ix_modeling_projects_assignee (assignee_admin_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 모델링 결과물(콘텐츠 묶음)의 판. 클라이언트는 released 인 판만 받는다.
-- 파일 자체는 여기에 없다. 콘텐츠 저장소의 경로와 해시만 적는다 (docs/CITY_MODELING_PIPELINE.md).
CREATE TABLE IF NOT EXISTS city_modeling_assets (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id          CHAR(16)        NOT NULL,
  project_id         BIGINT UNSIGNED NOT NULL,
  region_id          BIGINT UNSIGNED NOT NULL,
  version            VARCHAR(32)     NOT NULL,       -- 파일 버전 (예: 1.0.0)
  address_key        VARCHAR(120)    NOT NULL,       -- 콘텐츠 주소 (Addressables 의 key)
  catalog_path       VARCHAR(255)    NOT NULL,       -- 콘텐츠 저장소 안의 카탈로그 경로 (상대 경로)
  content_hash       CHAR(64)        NOT NULL,       -- SHA-256
  size_bytes         BIGINT UNSIGNED NOT NULL DEFAULT 0,
  min_client_version VARCHAR(16)     NOT NULL DEFAULT '',
  status             VARCHAR(16)     NOT NULL DEFAULT 'uploaded',   -- uploaded, approved, rejected, released, withdrawn
  rollout_percent    INT UNSIGNED    NOT NULL DEFAULT 0,   -- 받는 클라이언트의 비율 (0~100)
  note               VARCHAR(500)    NOT NULL DEFAULT '',
  uploaded_by        BIGINT UNSIGNED NULL,
  reviewed_by        BIGINT UNSIGNED NULL,
  reviewed_at        DATETIME        NULL,
  review_note        VARCHAR(500)    NOT NULL DEFAULT '',
  released_by        BIGINT UNSIGNED NULL,
  released_at        DATETIME        NULL,
  withdrawn_by       BIGINT UNSIGNED NULL,
  withdrawn_at       DATETIME        NULL,
  withdraw_reason    VARCHAR(255)    NOT NULL DEFAULT '',
  created_at         DATETIME        NOT NULL,
  updated_at         DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_modeling_assets_public (public_id),
  UNIQUE KEY uq_modeling_assets_version (project_id, version),
  KEY ix_modeling_assets_region (region_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
