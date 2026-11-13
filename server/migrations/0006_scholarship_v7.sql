-- CITY GUARDIANS 0006: v7.0 GUARDIANS SCHOLARSHIP (docs/SCHOLARSHIP_SYSTEM.md, docs/PRIVACY_DATA_FLOW.md).
--
-- 추가만 한다. 이 테이블들은 지갑·원장·전투·랭킹·은행 테이블을 참조하지 않는다:
-- 게임 플레이 기록, 게임머니, 결제 내역은 장학 심사에 쓰이지 않는다.
--
-- 개인정보는 목적에 따라 나눠 둔다.
--   scholarship_applications        심사에 쓰는 것 (신청서). 이름·연락처가 없다
--   scholarship_applicant_profiles  본인 확인과 연락에 쓰는 것. 심사위원은 보지 못한다
--   scholarship_consents            보호자 동의
-- *_enc 열은 애플리케이션이 암호화해 넣는다 (AES-256-GCM, 키는 서버의 환경 설정에만 있다).
-- 주민등록번호, 계좌번호는 받지 않는다 (열이 없다).

CREATE TABLE IF NOT EXISTS scholarship_programs (
  id                        BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id                 CHAR(16)        NOT NULL,
  category                  VARCHAR(16)     NOT NULL,                  -- finance, creativity, leadership, growth
  title                     VARCHAR(120)    NOT NULL,
  summary                   VARCHAR(300)    NOT NULL DEFAULT '',
  description               TEXT            NULL,
  support_type              VARCHAR(20)     NOT NULL,                  -- education_fee, books, online_pass, project, grant
  support_detail            VARCHAR(300)    NOT NULL DEFAULT '',
  capacity                  INT             NOT NULL DEFAULT 1,        -- 선발 인원
  apply_starts_at           DATETIME        NOT NULL,
  apply_ends_at             DATETIME        NOT NULL,
  result_on                 DATE            NULL,                      -- 결과를 알리기로 한 날
  guardian_consent_age      INT             NOT NULL DEFAULT 19,       -- 이 나이 미만이면 보호자 동의가 필요하다 (14 아래로 내릴 수 없다)
  min_reviews               INT             NOT NULL DEFAULT 2,        -- 지원서 하나에 필요한 심사 수
  appeal_days               INT             NOT NULL DEFAULT 7,
  retention_days_unselected INT             NOT NULL DEFAULT 180,      -- 선정되지 않은 신청의 개인정보 보관 기간
  retention_days_selected   INT             NOT NULL DEFAULT 1825,
  status                    VARCHAR(16)     NOT NULL DEFAULT 'draft',  -- draft, pending, published, closed, announced, archived
  created_by                BIGINT UNSIGNED NULL,
  approved_by               BIGINT UNSIGNED NULL,
  approved_at               DATETIME        NULL,
  announced_at              DATETIME        NULL,
  created_at                DATETIME        NOT NULL,
  updated_at                DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_programs_public (public_id),
  KEY ix_programs_status (status, apply_ends_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 지원 자격. 종류는 정해진 것만 쓸 수 있다: school_level, birth_year, statement, document.
-- 게임 레벨·GC·랭킹·결제 같은 종류는 없고, 넣으려 하면 서버가 거절한다.
CREATE TABLE IF NOT EXISTS scholarship_eligibility_rules (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  program_id  BIGINT UNSIGNED NOT NULL,
  rule_type   VARCHAR(20)     NOT NULL,
  params      TEXT            NULL,                                    -- JSON
  description VARCHAR(300)    NOT NULL,
  sort_order  INT             NOT NULL DEFAULT 0,
  created_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_rules_program (program_id, sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 평가 기준. 관리자가 프로그램마다 정한다.
CREATE TABLE IF NOT EXISTS scholarship_criteria (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  program_id  BIGINT UNSIGNED NOT NULL,
  name        VARCHAR(80)     NOT NULL,
  description VARCHAR(300)    NOT NULL DEFAULT '',
  max_score   INT             NOT NULL DEFAULT 10,
  weight      INT             NOT NULL DEFAULT 1,
  sort_order  INT             NOT NULL DEFAULT 0,
  created_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_criteria_program (program_id, sort_order)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 신청. 한 계정이 한 프로그램에 한 번.
CREATE TABLE IF NOT EXISTS scholarship_applications (
  id                  BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id           CHAR(16)        NOT NULL,
  program_id          BIGINT UNSIGNED NOT NULL,
  player_id           BIGINT UNSIGNED NOT NULL,                        -- 신청자 계정
  status              VARCHAR(20)     NOT NULL DEFAULT 'submitted',    -- submitted, ineligible, in_review, decided, announced, withdrawn
  eligibility_status  VARCHAR(16)     NOT NULL DEFAULT 'pending',      -- pending, confirmed, rejected
  eligibility_note    VARCHAR(300)    NULL,
  eligibility_by      BIGINT UNSIGNED NULL,
  eligibility_at      DATETIME        NULL,
  consent_status      VARCHAR(20)     NOT NULL DEFAULT 'not_required', -- not_required, pending, guardian_confirmed, verified
  school_level        VARCHAR(20)     NOT NULL,                        -- elementary, middle, high, university, out_of_school
  is_minor            TINYINT(1)      NOT NULL DEFAULT 0,
  form_enc            MEDIUMTEXT      NULL,                            -- 신청서 (지원 동기, 계획, 확인한 자격 문구)
  result              VARCHAR(16)     NULL,                            -- selected, waitlisted, not_selected (알린 뒤에만 채운다)
  result_notified_at  DATETIME        NULL,
  result_viewed_at    DATETIME        NULL,
  disbursement_status VARCHAR(16)     NULL,                            -- planned, approved, completed, cancelled
  submitted_at        DATETIME        NOT NULL,
  withdrawn_at        DATETIME        NULL,
  purge_after         DATE            NULL,                            -- 이 날이 지나면 개인정보를 지운다
  purged_at           DATETIME        NULL,
  updated_at          DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_applications_public (public_id),
  UNIQUE KEY uq_applications_once (program_id, player_id),
  KEY ix_applications_player (player_id),
  KEY ix_applications_purge (purge_after, purged_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 신청자의 본인·연락 정보. 신청서와 따로 둔다. 심사위원 화면은 이 테이블을 읽지 않는다.
CREATE TABLE IF NOT EXISTS scholarship_applicant_profiles (
  application_id  BIGINT UNSIGNED   NOT NULL,
  name_enc        TEXT              NULL,
  birth_year      SMALLINT UNSIGNED NULL,                              -- 태어난 해만 받는다 (날짜는 받지 않는다)
  school_name_enc TEXT              NULL,
  email_enc       TEXT              NULL,
  phone_enc       TEXT              NULL,
  created_at      DATETIME          NOT NULL,
  purged_at       DATETIME          NULL,
  PRIMARY KEY (application_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 제출 자료. 1차에서는 글과 주소(URL)만 받는다 (파일 올리기 없음).
CREATE TABLE IF NOT EXISTS scholarship_documents (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  application_id BIGINT UNSIGNED NOT NULL,
  kind           VARCHAR(20)     NOT NULL,                             -- essay, plan, project, proof, link
  title          VARCHAR(120)    NOT NULL,
  content_enc    MEDIUMTEXT      NULL,
  url            VARCHAR(300)    NULL,
  created_at     DATETIME        NOT NULL,
  purged_at      DATETIME        NULL,
  PRIMARY KEY (id),
  KEY ix_documents_application (application_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 동의. privacy: 개인정보 수집·이용 동의 (신청자). guardian: 보호자 동의 (미성년자).
CREATE TABLE IF NOT EXISTS scholarship_consents (
  id                   BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  application_id       BIGINT UNSIGNED NOT NULL,
  consent_type         VARCHAR(20)     NOT NULL,                       -- privacy, guardian
  status               VARCHAR(20)     NOT NULL,                       -- given, pending, guardian_confirmed, verified, withdrawn, expired
  version              VARCHAR(20)     NOT NULL,
  guardian_name_enc    TEXT            NULL,
  guardian_relation    VARCHAR(20)     NULL,
  guardian_contact_enc TEXT            NULL,
  token_hash           CHAR(64)        NULL,                           -- 보호자 확인 주소의 일회용 값 (해시)
  token_expires_at     DATETIME        NULL,
  confirmed_at         DATETIME        NULL,
  verified_by          BIGINT UNSIGNED NULL,                           -- 보호자 동의를 확인한 관리자
  verified_at          DATETIME        NULL,
  verify_method        VARCHAR(40)     NULL,                           -- 확인한 방법 (서면, 통화 등)
  verify_note          VARCHAR(300)    NULL,
  created_at           DATETIME        NOT NULL,
  updated_at           DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_consents_token (token_hash),
  UNIQUE KEY uq_consents_once (application_id, consent_type)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 심사위원 (프로그램마다 배정한다). 심사위원은 관리자 계정이고 역할이 reviewer 다.
CREATE TABLE IF NOT EXISTS scholarship_reviewers (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  program_id  BIGINT UNSIGNED NOT NULL,
  admin_id    BIGINT UNSIGNED NOT NULL,
  is_active   TINYINT(1)      NOT NULL DEFAULT 1,
  assigned_by BIGINT UNSIGNED NULL,
  created_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_reviewers_once (program_id, admin_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 심사. 지원서와 심사위원의 짝마다 한 줄. 심사위원은 자기에게 배정된 줄만 본다.
CREATE TABLE IF NOT EXISTS scholarship_reviews (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  application_id BIGINT UNSIGNED NOT NULL,
  reviewer_id    BIGINT UNSIGNED NOT NULL,
  admin_id       BIGINT UNSIGNED NOT NULL,
  status         VARCHAR(16)     NOT NULL DEFAULT 'assigned',          -- assigned, submitted, recused
  scores         TEXT            NULL,                                 -- JSON: 평가 기준 id => 점수
  total_centi    INT             NULL,                                 -- 0 ~ 10000 (100점 만점의 1/100 단위)
  comment        TEXT            NULL,
  recuse_reason  VARCHAR(300)    NULL,
  assigned_at    DATETIME        NOT NULL,
  submitted_at   DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_reviews_once (application_id, reviewer_id),
  KEY ix_reviews_admin (admin_id, status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 선정 결과. 제안한 사람과 최종 승인한 사람이 따로 남는다.
CREATE TABLE IF NOT EXISTS scholarship_awards (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  application_id BIGINT UNSIGNED NOT NULL,
  program_id     BIGINT UNSIGNED NOT NULL,
  decision       VARCHAR(16)     NOT NULL,                             -- selected, waitlisted, not_selected
  score_centi    INT             NULL,
  review_count   INT             NOT NULL DEFAULT 0,
  decision_note  VARCHAR(300)    NULL,
  status         VARCHAR(16)     NOT NULL DEFAULT 'proposed',          -- proposed, approved, reopened
  decided_by     BIGINT UNSIGNED NULL,
  decided_at     DATETIME        NULL,
  approved_by    BIGINT UNSIGNED NULL,
  approved_at    DATETIME        NULL,
  created_at     DATETIME        NOT NULL,
  updated_at     DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_awards_application (application_id),
  KEY ix_awards_program (program_id, decision)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 이의신청. 신청 하나에 한 번.
CREATE TABLE IF NOT EXISTS scholarship_appeals (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  application_id BIGINT UNSIGNED NOT NULL,
  reason_enc     TEXT            NULL,
  status         VARCHAR(16)     NOT NULL DEFAULT 'submitted',         -- submitted, upheld, dismissed
  response       VARCHAR(500)    NULL,
  handled_by     BIGINT UNSIGNED NULL,
  created_at     DATETIME        NOT NULL,
  handled_at     DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_appeals_application (application_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 지급 기록. 상태만 관리한다: 이 시스템은 돈을 보내지 않는다. 계좌번호를 담는 열이 없다.
CREATE TABLE IF NOT EXISTS scholarship_disbursement_records (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  award_id           BIGINT UNSIGNED NOT NULL,
  application_id     BIGINT UNSIGNED NOT NULL,
  support_type       VARCHAR(20)     NOT NULL,
  description        VARCHAR(300)    NOT NULL,                         -- 무엇을 지원했는가 (예: 교재 3권)
  amount_krw         BIGINT          NULL,                             -- 장부에 적는 금액 (원). 송금에 쓰이지 않는다
  status             VARCHAR(16)     NOT NULL DEFAULT 'planned',       -- planned, approved, completed, cancelled
  external_reference VARCHAR(120)    NULL,                             -- 실제 지급을 처리한 절차의 문서 번호
  status_note        VARCHAR(300)    NULL,
  created_by         BIGINT UNSIGNED NULL,
  approved_by        BIGINT UNSIGNED NULL,
  approved_at        DATETIME        NULL,
  completed_by       BIGINT UNSIGNED NULL,
  completed_at       DATETIME        NULL,
  created_at         DATETIME        NOT NULL,
  updated_at         DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_disbursements_award (award_id),
  KEY ix_disbursements_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- 감사 로그. 추가만 한다. 개인정보를 열어 본 기록(pii.view)도 여기에 남는다.
CREATE TABLE IF NOT EXISTS scholarship_audit_logs (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  actor_type     VARCHAR(16)     NOT NULL,                             -- admin, applicant, guardian, system
  actor_id       BIGINT UNSIGNED NULL,
  action         VARCHAR(64)     NOT NULL,
  program_id     BIGINT UNSIGNED NULL,
  application_id BIGINT UNSIGNED NULL,
  detail         VARCHAR(500)    NULL,
  ip             VARCHAR(45)     NOT NULL DEFAULT '',
  created_at     DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_sch_audit_application (application_id, id),
  KEY ix_sch_audit_action (action, created_at),
  KEY ix_sch_audit_actor (actor_type, actor_id, id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
