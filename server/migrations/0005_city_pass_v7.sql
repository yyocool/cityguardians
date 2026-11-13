-- CITY GUARDIANS 0005: v7.0 현실 혜택의 공통 장치와 CITY PASS (docs/CITY_PASS_SYSTEM.md, docs/REAL_WORLD_REWARDS_POLICY.md).
--
-- 추가만 한다: CREATE TABLE IF NOT EXISTS. 기존 테이블과 데이터는 건드리지 않는다.
-- 이 테이블들은 지갑·원장·전투·은행 테이블을 참조하지 않는다. 게임머니와 현실 혜택은 서로 이어지지 않는다.
-- 시각은 UTC, JSON 은 TEXT 에 넣는다 (0001 과 같은 규칙).

-- ── 기능 승인 ──────────────────────────────────────────────
-- 현실 혜택을 주는 기능은 모두 꺼진 채로 시작한다. 법률 검토 완료를 기록한 뒤에만 켤 수 있다.
CREATE TABLE IF NOT EXISTS benefit_features (
  code              VARCHAR(40)     NOT NULL,
  name              VARCHAR(80)     NOT NULL,
  description       VARCHAR(300)    NOT NULL DEFAULT '',
  is_enabled        TINYINT(1)      NOT NULL DEFAULT 0,
  legal_status      VARCHAR(16)     NOT NULL DEFAULT 'not_reviewed',   -- not_reviewed, in_review, approved, rejected
  legal_reference   VARCHAR(190)    NULL,                              -- 검토 의견서의 문서 번호나 제목
  legal_reviewer    VARCHAR(80)     NULL,                              -- 검토한 곳 (법무법인, 담당자)
  legal_reviewed_on DATE            NULL,
  legal_note        VARCHAR(500)    NULL,
  legal_recorded_by BIGINT UNSIGNED NULL,
  legal_recorded_at DATETIME        NULL,
  enabled_by        BIGINT UNSIGNED NULL,
  enabled_at        DATETIME        NULL,
  sort_order        INT             NOT NULL DEFAULT 0,
  updated_at        DATETIME        NOT NULL,
  PRIMARY KEY (code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 기능 승인의 이력. 추가만 한다.
CREATE TABLE IF NOT EXISTS benefit_feature_history (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  feature_code VARCHAR(40)     NOT NULL,
  action       VARCHAR(20)     NOT NULL,                               -- legal_update, enable, disable, auto_disable
  before_value TEXT            NULL,
  after_value  TEXT            NULL,
  admin_id     BIGINT UNSIGNED NULL,
  reason       VARCHAR(255)    NOT NULL DEFAULT '',
  created_at   DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_feature_history (feature_code, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── 홈페이지 로그인 ────────────────────────────────────────
-- 게임 안에서 받은 일회용 코드를 홈페이지에 넣어 로그인한다. 코드와 토큰은 해시만 저장한다.
CREATE TABLE IF NOT EXISTS web_login_codes (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id  BIGINT UNSIGNED NOT NULL,
  code_hash  CHAR(64)        NOT NULL,
  expires_at DATETIME        NOT NULL,
  used_at    DATETIME        NULL,
  created_at DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_web_codes_hash (code_hash),
  KEY ix_web_codes_player (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS web_sessions (
  id           BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id    BIGINT UNSIGNED NOT NULL,
  token_hash   CHAR(64)        NOT NULL,
  csrf_token   CHAR(64)        NOT NULL,
  created_at   DATETIME        NOT NULL,
  last_seen_at DATETIME        NOT NULL,
  expires_at   DATETIME        NOT NULL,
  revoked_at   DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_web_sessions_token (token_hash),
  KEY ix_web_sessions_player (player_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- ── CITY PASS ──────────────────────────────────────────────
-- 멤버십. 이름·전화번호 같은 실제 개인정보는 없다: 회원번호, 계정, 관심 지역뿐이다.
CREATE TABLE IF NOT EXISTS city_pass_memberships (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id          BIGINT UNSIGNED NOT NULL,
  member_no          CHAR(12)        NOT NULL,                         -- CP-XXXX-XXXX
  interest_region_id BIGINT UNSIGNED NULL,                             -- 관심 지역. 실제 거주지가 아니다
  status             VARCHAR(16)     NOT NULL DEFAULT 'active',        -- active, suspended, withdrawn
  terms_version      VARCHAR(20)     NOT NULL,
  joined_at          DATETIME        NOT NULL,
  updated_at         DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_memberships_player (player_id),
  UNIQUE KEY uq_memberships_no (member_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 제휴 업체. 계약이 확인되고 승인된 업체만 밖에 보인다.
CREATE TABLE IF NOT EXISTS partner_merchants (
  id                   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id            CHAR(16)        NOT NULL,
  name                 VARCHAR(80)     NOT NULL,
  category             VARCHAR(20)     NOT NULL,                       -- cafe, restaurant, fitness, culture, education, retail, service
  status               VARCHAR(16)     NOT NULL DEFAULT 'draft',       -- draft, pending, approved, suspended, ended
  contract_reference   VARCHAR(120)    NULL,                           -- 제휴 계약서의 문서 번호
  contract_starts_on   DATE            NULL,
  contract_ends_on     DATE            NULL,
  contact_name         VARCHAR(40)     NULL,                           -- 업체 담당자 (밖에 보이지 않는다)
  contact_phone        VARCHAR(30)     NULL,
  contact_email        VARCHAR(120)    NULL,
  youth_safe_confirmed TINYINT(1)      NOT NULL DEFAULT 0,             -- 청소년에게 맞지 않는 업종·상품이 아님을 확인했는가
  status_reason        VARCHAR(255)    NULL,
  created_by           BIGINT UNSIGNED NULL,
  approved_by          BIGINT UNSIGNED NULL,
  approved_at          DATETIME        NULL,
  created_at           DATETIME        NOT NULL,
  updated_at           DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_merchants_public (public_id),
  KEY ix_merchants_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 제휴 지점. 주소와 지도 위치는 실제 매장의 것이다.
CREATE TABLE IF NOT EXISTS partner_branches (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id   CHAR(16)        NOT NULL,
  merchant_id BIGINT UNSIGNED NOT NULL,
  name        VARCHAR(80)     NOT NULL,
  region_id   BIGINT UNSIGNED NOT NULL,
  address     VARCHAR(190)    NOT NULL,
  phone       VARCHAR(30)     NULL,
  lat         DECIMAL(9,6)    NULL,
  lng         DECIMAL(9,6)    NULL,
  image_file  VARCHAR(80)     NULL,                                    -- public/media 안의 파일 이름
  is_active   TINYINT(1)      NOT NULL DEFAULT 1,
  created_at  DATETIME        NOT NULL,
  updated_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_branches_public (public_id),
  KEY ix_branches_merchant (merchant_id),
  KEY ix_branches_region (region_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 혜택. 승인된 것만 밖에 보이고 쿠폰으로 발급된다.
CREATE TABLE IF NOT EXISTS partner_offers (
  id                BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id         CHAR(16)        NOT NULL,
  merchant_id       BIGINT UNSIGNED NOT NULL,
  branch_id         BIGINT UNSIGNED NULL,                              -- NULL 이면 그 업체의 모든 지점
  title             VARCHAR(80)     NOT NULL,
  description       VARCHAR(500)    NOT NULL DEFAULT '',
  discount_type     VARCHAR(16)     NOT NULL,                          -- percent, amount, free_trial, gift, special
  discount_value    INT             NOT NULL DEFAULT 0,                -- percent 면 %, amount 면 원
  conditions        VARCHAR(500)    NOT NULL DEFAULT '',               -- 이용 조건
  issue_policy      VARCHAR(20)     NOT NULL DEFAULT 'membership',     -- membership, promotion, partner_condition
  starts_at         DATETIME        NOT NULL,
  ends_at           DATETIME        NOT NULL,
  total_limit       INT             NULL,                              -- 발급할 수 있는 전체 수. NULL 이면 제한 없음
  per_member_limit  INT             NOT NULL DEFAULT 1,
  coupon_valid_days INT             NOT NULL DEFAULT 30,
  issued_count      INT             NOT NULL DEFAULT 0,
  status            VARCHAR(16)     NOT NULL DEFAULT 'draft',          -- draft, pending, approved, paused, ended
  status_reason     VARCHAR(255)    NULL,
  created_by        BIGINT UNSIGNED NULL,
  approved_by       BIGINT UNSIGNED NULL,
  approved_at       DATETIME        NULL,
  created_at        DATETIME        NOT NULL,
  updated_at        DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_offers_public (public_id),
  KEY ix_offers_merchant (merchant_id),
  KEY ix_offers_status (status, ends_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 쿠폰. GC 나 전투 보상으로 발급되지 않는다 (issue_reason 은 멤버십·프로모션·제휴사 조건뿐).
-- 제시용 일회용 코드는 해시만 저장한다.
CREATE TABLE IF NOT EXISTS benefit_coupons (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id          CHAR(16)        NOT NULL,
  offer_id           BIGINT UNSIGNED NOT NULL,
  merchant_id        BIGINT UNSIGNED NOT NULL,
  membership_id      BIGINT UNSIGNED NOT NULL,
  player_id          BIGINT UNSIGNED NOT NULL,
  status             VARCHAR(16)     NOT NULL DEFAULT 'issued',        -- issued, redeemed, expired, cancelled
  issue_reason       VARCHAR(20)     NOT NULL,
  issued_at          DATETIME        NOT NULL,
  expires_at         DATETIME        NOT NULL,
  present_code_hash  CHAR(64)        NULL,
  present_expires_at DATETIME        NULL,
  redeemed_at        DATETIME        NULL,
  cancelled_reason   VARCHAR(255)    NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_coupons_public (public_id),
  UNIQUE KEY uq_coupons_present (present_code_hash),
  KEY ix_coupons_player (player_id, status),
  KEY ix_coupons_offer (offer_id, membership_id),
  KEY ix_coupons_expiry (status, expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 쿠폰 사용 기록. 쿠폰 하나에 한 줄뿐이다 (coupon_id 가 유일하다): 같은 쿠폰은 두 번 쓰이지 않는다.
CREATE TABLE IF NOT EXISTS coupon_redemptions (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  coupon_id        BIGINT UNSIGNED NOT NULL,
  offer_id         BIGINT UNSIGNED NOT NULL,
  merchant_id      BIGINT UNSIGNED NOT NULL,
  branch_id        BIGINT UNSIGNED NULL,
  merchant_user_id BIGINT UNSIGNED NOT NULL,
  membership_id    BIGINT UNSIGNED NOT NULL,
  redeemed_at      DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_redemptions_coupon (coupon_id),
  KEY ix_redemptions_merchant (merchant_id, redeemed_at),
  KEY ix_redemptions_membership (membership_id, redeemed_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 쿠폰의 거래 로그. 추가만 한다: 발급, 제시, 확인 실패, 사용, 만료, 취소.
CREATE TABLE IF NOT EXISTS coupon_events (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  coupon_id        BIGINT UNSIGNED NULL,
  event            VARCHAR(20)     NOT NULL,                           -- issued, presented, verify_failed, redeemed, expired, cancelled
  merchant_id      BIGINT UNSIGNED NULL,
  merchant_user_id BIGINT UNSIGNED NULL,
  player_id        BIGINT UNSIGNED NULL,
  detail           VARCHAR(190)    NULL,
  created_at       DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_coupon_events_coupon (coupon_id, id),
  KEY ix_coupon_events_kind (event, created_at),
  KEY ix_coupon_events_user (merchant_user_id, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 상점 관리자 계정. 운영 관리자가 만든다 (웹에서 가입하지 않는다). 자기 업체(와 지점)의 쿠폰만 본다.
CREATE TABLE IF NOT EXISTS merchant_users (
  id                   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  merchant_id          BIGINT UNSIGNED NOT NULL,
  branch_id            BIGINT UNSIGNED NULL,                           -- NULL 이면 그 업체의 모든 지점
  login                VARCHAR(40)     NOT NULL,
  password_hash        VARCHAR(255)    NOT NULL,
  display_name         VARCHAR(40)     NOT NULL DEFAULT '',
  role                 VARCHAR(16)     NOT NULL DEFAULT 'staff',       -- owner, staff
  is_active            TINYINT(1)      NOT NULL DEFAULT 1,
  must_change_password TINYINT(1)      NOT NULL DEFAULT 1,
  failed_attempts      INT UNSIGNED    NOT NULL DEFAULT 0,
  locked_until         DATETIME        NULL,
  created_by           BIGINT UNSIGNED NULL,
  created_at           DATETIME        NOT NULL,
  last_login_at        DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_merchant_users_login (login),
  KEY ix_merchant_users_merchant (merchant_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS merchant_sessions (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  merchant_user_id BIGINT UNSIGNED NOT NULL,
  token_hash       CHAR(64)        NOT NULL,
  csrf_token       CHAR(64)        NOT NULL,
  ip               VARCHAR(45)     NOT NULL DEFAULT '',
  created_at       DATETIME        NOT NULL,
  last_seen_at     DATETIME        NOT NULL,
  expires_at       DATETIME        NOT NULL,
  revoked_at       DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_merchant_sessions_token (token_hash),
  KEY ix_merchant_sessions_user (merchant_user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS merchant_audit_logs (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  merchant_user_id BIGINT UNSIGNED NULL,
  merchant_id      BIGINT UNSIGNED NULL,
  action           VARCHAR(64)     NOT NULL,
  target           VARCHAR(64)     NOT NULL DEFAULT '',
  detail           VARCHAR(500)    NULL,
  ip               VARCHAR(45)     NOT NULL DEFAULT '',
  created_at       DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_merchant_audit_merchant (merchant_id, id),
  KEY ix_merchant_audit_time (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
