-- CITY GUARDIANS 0010: v9.0 가상 지방세, 도시 재정, 참여예산.
--
-- 추가만 한다. 여기의 금액은 모두 게임머니(GC)다. 실제 돈이나 현실의 세금과 이어지지 않는다.
-- v7.0 의 CITY PASS·장학 테이블과는 서로 가리키는 열이 하나도 없다: 게임 안의 도시 재정과 현실 혜택의 예산은 따로다
-- (docs/CITY_TREASURY_SYSTEM.md 1장).
-- 주민이 스스로 내는 방어 기여금(city_defense_structures.funded_gc, 원장 사유 city_fund)은 세금이 아니며 이 테이블들에 들어오지 않는다.

-- 세금 정책. 세금 종류마다 한 행. 처음에는 모두 꺼져 있다 (is_active = 0).
CREATE TABLE IF NOT EXISTS city_tax_policies (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  tax_type      VARCHAR(16)     NOT NULL,            -- trade(거래세), business(사업세), property(재산세)
  name          VARCHAR(40)     NOT NULL,
  description   VARCHAR(255)    NOT NULL DEFAULT '',
  is_active     TINYINT(1)      NOT NULL DEFAULT 0,
  rate_bp       INT             NOT NULL DEFAULT 0,  -- 세율. 1/100 퍼센트 (300 = 3%)
  applies_to    VARCHAR(255)    NOT NULL DEFAULT '', -- 적용 대상: 원장 사유를 쉼표로 (재산세는 쓰지 않는다)
  exempt_amount BIGINT          NOT NULL DEFAULT 0,  -- 면제 기준 (거래세: 이 금액 미만의 거래, 사업세: 하루 소득 중 이만큼, 재산세: 보유 GC 중 이만큼)
  min_level     INT             NOT NULL DEFAULT 1,  -- 초보자 보호: 이 레벨 미만은 내지 않는다
  grace_days    INT             NOT NULL DEFAULT 0,  -- 초보자 보호: 전입한 지 이 날수가 지나기 전에는 내지 않는다
  cap_gc        BIGINT          NOT NULL DEFAULT 0,  -- 상한 (사업세: 하루, 재산세: 한 기간). 0 이면 상한 없음
  period        VARCHAR(16)     NOT NULL DEFAULT 'transaction',   -- 징수 시점: transaction(거래할 때), weekly, monthly
  updated_at    DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_tax_policies_type (tax_type)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 세금 거래 명세. 걷을 때마다 한 줄. (tax_type, source_key) 가 유일해서 같은 거래·같은 기간에 두 번 걷지 못한다.
CREATE TABLE IF NOT EXISTS city_tax_transactions (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id        CHAR(16)        NOT NULL,
  tax_type         VARCHAR(16)     NOT NULL,
  policy_id        BIGINT UNSIGNED NOT NULL,
  player_id        BIGINT UNSIGNED NOT NULL,
  region_id        BIGINT UNSIGNED NOT NULL,         -- 세금이 들어간 도시 (낸 사람의 주거 도시)
  base_gc          BIGINT          NOT NULL,         -- 과세 대상 금액
  rate_bp          INT             NOT NULL,
  tax_gc           BIGINT          NOT NULL,
  included         TINYINT(1)      NOT NULL DEFAULT 0,   -- 1 이면 치른 값에 이미 들어 있는 세금 (지갑에서 더 빠지지 않았다)
  source_reason    VARCHAR(32)     NOT NULL DEFAULT '',
  source_key       VARCHAR(64)     NOT NULL,         -- L<원장 번호> 또는 P<기간>:<플레이어>
  source_ledger_id BIGINT UNSIGNED NULL,             -- 과세 대상이 된 원장 줄
  wallet_ledger_id BIGINT UNSIGNED NULL,             -- 세금으로 지갑에서 빠진 원장 줄 (included = 0 일 때)
  period_key       VARCHAR(16)     NULL,
  created_at       DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_tax_transactions_public (public_id),
  UNIQUE KEY uq_tax_transactions_source (tax_type, source_key),
  KEY ix_tax_transactions_player (player_id, id),
  KEY ix_tax_transactions_region (region_id, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 도시 재정 계정. 도시마다 일반 회계(general)와 예비비(reserve).
CREATE TABLE IF NOT EXISTS city_treasury_accounts (
  id         BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  region_id  BIGINT UNSIGNED NOT NULL,
  account    VARCHAR(16)     NOT NULL,               -- general, reserve
  balance    BIGINT          NOT NULL DEFAULT 0,
  total_in   BIGINT          NOT NULL DEFAULT 0,
  total_out  BIGINT          NOT NULL DEFAULT 0,
  updated_at DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_treasury_accounts (region_id, account)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 도시 재정 원장. 추가만 한다. 계정의 잔액이 바뀔 때마다 한 줄.
CREATE TABLE IF NOT EXISTS city_treasury_ledger (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  region_id     BIGINT UNSIGNED NOT NULL,
  account       VARCHAR(16)     NOT NULL,
  delta         BIGINT          NOT NULL,
  balance_after BIGINT          NOT NULL,
  reason        VARCHAR(32)     NOT NULL,            -- tax_trade, tax_business, tax_property, expenditure
  ref_type      VARCHAR(32)     NULL,                -- tax_transaction, expenditure
  ref_id        BIGINT UNSIGNED NULL,
  created_at    DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_treasury_ledger_account (region_id, account, id),
  KEY ix_treasury_ledger_reason (reason, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 참여예산 제안. 주민이 낸다. 관리자가 검토한 뒤에만 다른 주민에게 보이고 투표에 부쳐진다.
CREATE TABLE IF NOT EXISTS city_budget_proposals (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id      CHAR(16)        NOT NULL,
  region_id      BIGINT UNSIGNED NOT NULL,
  player_id      BIGINT UNSIGNED NOT NULL,
  category       VARCHAR(24)     NOT NULL,           -- defense_facility, defense_maintenance, infrastructure, npc_welfare, education, event
  title          VARCHAR(60)     NOT NULL,
  description    VARCHAR(600)    NOT NULL DEFAULT '',
  requested_gc   BIGINT          NOT NULL,
  target_code    VARCHAR(40)     NULL,               -- 방어시설·주요 시설의 코드 (해당하는 분야만)
  status         VARCHAR(16)     NOT NULL DEFAULT 'submitted',   -- submitted, rejected, voting, passed, failed, executed, withdrawn, cancelled
  review_note    VARCHAR(255)    NOT NULL DEFAULT '',
  reviewed_by    BIGINT UNSIGNED NULL,
  reviewed_at    DATETIME        NULL,
  vote_starts_at DATETIME        NULL,
  vote_ends_at   DATETIME        NULL,
  yes_count      INT             NOT NULL DEFAULT 0,
  no_count       INT             NOT NULL DEFAULT 0,
  quorum         INT             NOT NULL DEFAULT 0,  -- 투표를 열 때 정해 둔 최소 투표 수
  decided_at     DATETIME        NULL,
  expenditure_id BIGINT UNSIGNED NULL,
  created_at     DATETIME        NOT NULL,
  updated_at     DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_budget_proposals_public (public_id),
  KEY ix_budget_proposals_region (region_id, status, id),
  KEY ix_budget_proposals_player (player_id, id),
  KEY ix_budget_proposals_voting (status, vote_ends_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 투표. (proposal_id, player_id) 가 유일해서 한 계정이 두 번 투표하지 못한다. 한 표의 무게는 누구나 같다 (무게를 담는 열이 없다).
CREATE TABLE IF NOT EXISTS city_budget_votes (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  proposal_id BIGINT UNSIGNED NOT NULL,
  player_id   BIGINT UNSIGNED NOT NULL,
  choice      VARCHAR(8)      NOT NULL,              -- yes, no
  created_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_budget_votes (proposal_id, player_id),
  KEY ix_budget_votes_player (player_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 예산 집행 내역. 공개된다. 제안 하나는 한 번만 집행된다 (proposal_id 가 유일).
CREATE TABLE IF NOT EXISTS city_budget_expenditures (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id     CHAR(16)        NOT NULL,
  region_id     BIGINT UNSIGNED NOT NULL,
  proposal_id   BIGINT UNSIGNED NULL,                -- 참여예산으로 정해진 집행이면 그 제안
  category      VARCHAR(24)     NOT NULL,            -- 제안의 분야 + reserve(예비비)
  title         VARCHAR(60)     NOT NULL,
  description   VARCHAR(600)    NOT NULL DEFAULT '',
  amount_gc     BIGINT          NOT NULL,
  account       VARCHAR(16)     NOT NULL DEFAULT 'general',
  effect_type   VARCHAR(24)     NOT NULL DEFAULT 'record',   -- structure_fund, facility_repair, record
  target_code   VARCHAR(40)     NULL,
  effect_detail VARCHAR(500)    NOT NULL DEFAULT '',
  executed_by   BIGINT UNSIGNED NULL,                -- 관리자
  reason        VARCHAR(255)    NOT NULL DEFAULT '',
  created_at    DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_budget_expenditures_public (public_id),
  UNIQUE KEY uq_budget_expenditures_proposal (proposal_id),
  KEY ix_budget_expenditures_region (region_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
