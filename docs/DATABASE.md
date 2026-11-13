# DATABASE — 데이터베이스

> 기준 v6.0 · 2026-11-13 · 상태: 목표 스키마 설계 (미적용)

## 0. 먼저 읽을 것

이 문서는 v6.0의 **목표 스키마**입니다. 운영 서버에는 이전 버전(API v1.0.0)이 쓰는 DB가 이미 있을 가능성이 높지만, 서버에 접속하지 못해 실제 테이블 구조를 확인하지 못했습니다.

따라서 적용 순서는 다음과 같습니다.

1. 운영 DB의 구조만 덤프해 실제 테이블을 확인한다 (데이터 변경 없음).
2. 이 문서의 목표 스키마와 대조해 "그대로 씀 / 열 추가 / 이름 변경 / 신규" 목록을 만든다.
3. 기존 테이블과 데이터를 보존하는 방향으로 마이그레이션을 작성한다.
4. 이 문서를 실제 구조에 맞게 갱신한다.

기존 테이블은 삭제하거나 덮어쓰지 않습니다.

## 1. 기본 규칙

| 항목 | 규칙 |
|---|---|
| DB 이름 | `city_guardians` (운영), `city_guardians_staging`, `city_guardians_dev` |
| 엔진 | InnoDB |
| 문자 집합 | `utf8mb4`. 정렬 규칙은 서버 MySQL 버전 확인 후 결정 |
| 기본 키 | `id BIGINT UNSIGNED AUTO_INCREMENT` |
| 외부 노출 ID | 플레이어는 추측하기 어려운 `public_id`를 따로 둔다. 내부 `id`는 API에 내보내지 않는다 |
| 금액 | `BIGINT` 정수 (GC). 소수 없음 |
| 이율 | 만분율 정수 (`rate_bp`: 10 = 0.1%) |
| 시각 | `DATETIME`. 저장 시간대는 기존 테이블의 방식을 확인해 통일. 날짜 경계(이자, 일일 한도)는 한국 시간 |
| 유연한 속성 | `JSON` 열 (아이템 수치, 외형, 웨이브 구성) |
| 이름 | 테이블은 복수형 소문자, 열은 소문자와 밑줄 |
| 외래 키 | 금융·소유 관계에는 제약을 건다 |
| 삭제 | 금융 기록과 원장은 지우지 않는다. 정의 데이터는 `is_active`로 끈다 |

## 2. 영역별 테이블

### 2.1 플레이어와 인증

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `players` | 플레이어 | `public_id`, `status`, `residency_status`, `nickname`, `region_id`, `registered_at`, `tutorial_step`, `created_at`, `last_seen_at` |
| `player_credentials` | 로그인 수단 (게스트 비밀키, 추후 계정 연결) | `player_id`, `type`, `identifier`, `secret_hash` |
| `player_sessions` | 접속 토큰 | `player_id`, `token_hash`, `expires_at`, `revoked_at`, `platform`, `client_version` |
| `player_flags` | 진행 표시 (튜토리얼, 대화, 해금) | `player_id`, `flag`, `value` |

### 2.2 캐릭터와 방

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `characters` | 캐릭터 (플레이어당 1개에서 시작) | `player_id`, `level`, `xp`, `appearance` (JSON) |
| `character_equipment` | 장착 | `player_id`, `slot`, `inventory_id` |
| `room_furniture` | 보유 가구와 배치 | `player_id`, `item_id`, `grid_x`, `grid_y`, `rotation`, `is_placed` |

### 2.3 지역

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `sidos` | 시·도 | `code`, `name` |
| `regions` | 시·군·구 | `sido_id`, `code`, `name`, `defense_points`, `level`, `resident_count`, `invasion_level`, `city_template`, `is_playable` |
| `residency_history` | 전입·이사 이력 | `player_id`, `from_region_id`, `to_region_id`, `fee_gc` |
| `region_contributions` | 플레이어별 도시 기여도 | `region_id`, `player_id`, `points` |

지역 목록(16개 시·도, 229개 시·군·구)은 운영 DB에 이미 있습니다. 기존 테이블에 필요한 열만 추가합니다.

### 2.4 콘텐츠 정의 (관리자에서 편집)

| 테이블 | 내용 |
|---|---|
| `item_defs` | 아이템 정의 |
| `shops`, `shop_items` | 상점과 진열 |
| `enemy_defs` | 적 정의 (능력치, 이동·공격 방식, 능력, 보상) |
| `tower_defs`, `tower_upgrade_defs` | 타워와 강화 단계 |
| `stage_defs` | 전투 스테이지 (방식, 웨이브, 방어 체력, 보상) |
| `hunt_zone_defs` | 사냥 구역 (출현 적, 최대 출현 속도) |
| `drop_tables`, `drop_table_entries` | 드롭 표 |
| `npc_defs` | NPC 정의 |
| `game_config` | 설정 값 (한도, 해금 레벨, 계산식 계수) |
| `config_history` | 정의·설정 변경 이력 (변경 전후, 관리자, 사유) |

### 2.5 소유와 전투

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `inventories` | 보유 아이템 | `player_id`, `item_id`, `quantity`, `enhance_level`, `location` |
| `player_towers` | 타워 해금과 영구 강화 | `player_id`, `tower_id`, `level` |
| `battle_sessions` | 스테이지 전투 | `player_id`, `stage_id`, `status`, `started_at`, `ended_at`, `result` (JSON), `reward_gc`, `reward_xp` |
| `hunt_sessions` | 사냥 세션 | `player_id`, `zone_id`, `started_at`, `last_report_at`, `kills` (JSON), `reward_gc` |
| `stage_clears` | 스테이지 기록 | `player_id`, `stage_id`, `clear_count`, `first_cleared_at` |
| `daily_counters` | 일일 한도 집계 | `player_id`, `date`, `counter`, `value` |
| `reward_rejections` | 검증에서 거절된 보상 보고 | `player_id`, `session`, `reason`, `payload` |

### 2.6 경제와 은행

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `wallets` | GC 잔액 | `player_id`, `balance` |
| `ledger_entries` | 원장 (추가만 가능) | `player_id`, `account`, `account_ref`, `delta`, `balance_after`, `reason`, `ref_type`, `ref_id`, `created_at` |
| `idempotency_keys` | 중복 방지 | `player_id`, `key`, `request_hash`, `response`, `created_at` |
| `bank_products` | 예금 상품 | `code`, `type`, `rate_bp`, `term_days`, `interest_cap_balance`, `is_active` |
| `bank_accounts` | 계좌 | `player_id`, `product_id`, `balance`, `day_min_balance`, `last_interest_date`, `matures_at`, `status` |

### 2.7 주식 (Phase 4)

| 테이블 | 내용 |
|---|---|
| `companies` | 가상 기업 |
| `stock_prices` | 시각별 가격 |
| `stock_daily` | 일별 시가·고가·저가·종가 |
| `stock_events` | 사건과 뉴스 |
| `stock_holdings` | 보유 수량, 취득원가 합계, 누적 실현손익 |
| `stock_orders` | 체결 기록 |

### 2.8 대출과 신용 (Phase 4)

| 테이블 | 내용 |
|---|---|
| `credit_scores` | 현재 신용점수 |
| `credit_score_history` | 변화 이력 |
| `credit_policy_rules` | 점수 변화 규칙 |
| `loan_products`, `loan_rate_tiers` | 대출 상품과 구간별 조건 |
| `loans` | 대출 |
| `loan_repayments` | 상환 기록 |

### 2.9 커뮤니티 (Phase 5)

| 테이블 | 내용 |
|---|---|
| `friendships` | 친구 |
| `blocks` | 차단 |
| `reports` | 신고와 증거 |
| `sanctions` | 제재 |
| `chat_policies` | 채팅 정책 |
| `chat_phrases` | 제한형 채팅 문구 |
| `banned_words` | 금칙어 |
| `chat_messages` | 메시지 (기간 한정 보관) |
| `parties`, `party_members` | 파티 |
| `city_notices` | 도시 공지 |

### 2.10 관리자와 홈페이지

| 테이블 | 내용 |
|---|---|
| `admins` | 관리자 계정, 역할 |
| `admin_sessions` | 관리자 세션 |
| `admin_audit_logs` | 관리자 행동 기록 |
| `notices` | 홈페이지 공지사항·소식 |
| `game_builds` | 다운로드용 빌드 (플랫폼, 버전, 파일, SHA-256, 공개 여부) |
| `system_logs` | 시스템 로그 (오류, 예약 작업 결과) |
| `schema_migrations` | 적용된 마이그레이션 |

## 3. 핵심 테이블 DDL

돈과 인증에 관련된 테이블의 초안입니다. 나머지는 구현 단계에서 마이그레이션 파일로 작성합니다.

```sql
CREATE TABLE players (
  id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  public_id        CHAR(16)        NOT NULL,
  status           ENUM('active','suspended','deleted') NOT NULL DEFAULT 'active',
  residency_status ENUM('stranger','resident')          NOT NULL DEFAULT 'stranger',
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

CREATE TABLE player_credentials (
  id          BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id   BIGINT UNSIGNED NOT NULL,
  type        VARCHAR(20)     NOT NULL,          -- guest, transfer_code, email ...
  identifier  VARCHAR(190)    NULL,
  secret_hash VARCHAR(255)    NOT NULL,
  created_at  DATETIME        NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_cred_type_identifier (type, identifier),
  KEY ix_cred_player (player_id),
  CONSTRAINT fk_cred_player FOREIGN KEY (player_id) REFERENCES players (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE player_sessions (
  id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id      BIGINT UNSIGNED NOT NULL,
  token_hash     CHAR(64)        NOT NULL,       -- SHA-256
  platform       VARCHAR(16)     NOT NULL,
  client_version VARCHAR(16)     NOT NULL,
  created_at     DATETIME        NOT NULL,
  expires_at     DATETIME        NOT NULL,
  revoked_at     DATETIME        NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uq_sessions_token (token_hash),
  KEY ix_sessions_player (player_id),
  CONSTRAINT fk_sessions_player FOREIGN KEY (player_id) REFERENCES players (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE wallets (
  player_id  BIGINT UNSIGNED NOT NULL,
  balance    BIGINT          NOT NULL DEFAULT 0,
  updated_at DATETIME        NOT NULL,
  PRIMARY KEY (player_id),
  CONSTRAINT fk_wallets_player FOREIGN KEY (player_id) REFERENCES players (id),
  CONSTRAINT ck_wallets_balance CHECK (balance >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE ledger_entries (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id     BIGINT UNSIGNED NOT NULL,
  account       ENUM('wallet','bank','loan') NOT NULL,
  account_ref   BIGINT UNSIGNED NULL,            -- bank_accounts.id, loans.id
  delta         BIGINT          NOT NULL,
  balance_after BIGINT          NOT NULL,
  reason        VARCHAR(32)     NOT NULL,        -- hunt_reward, shop_purchase ...
  ref_type      VARCHAR(32)     NULL,
  ref_id        BIGINT UNSIGNED NULL,
  txn_id        CHAR(26)        NOT NULL,        -- 한 거래에 속한 행을 묶는 값
  created_at    DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_ledger_player_time (player_id, created_at),
  KEY ix_ledger_txn (txn_id),
  KEY ix_ledger_reason_time (reason, created_at),
  CONSTRAINT fk_ledger_player FOREIGN KEY (player_id) REFERENCES players (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE idempotency_keys (
  player_id    BIGINT UNSIGNED NOT NULL,
  idem_key     CHAR(36)        NOT NULL,
  endpoint     VARCHAR(64)     NOT NULL,
  request_hash CHAR(64)        NOT NULL,
  status_code  SMALLINT        NOT NULL,
  response     JSON            NOT NULL,
  created_at   DATETIME        NOT NULL,
  PRIMARY KEY (player_id, idem_key),
  KEY ix_idem_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE bank_accounts (
  id                 BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id          BIGINT UNSIGNED NOT NULL,
  product_id         BIGINT UNSIGNED NOT NULL,
  balance            BIGINT          NOT NULL DEFAULT 0,
  day_min_balance    BIGINT          NOT NULL DEFAULT 0,
  rate_bp            INT             NOT NULL,   -- 정기예금은 가입 시 이율을 고정
  last_interest_date DATE            NULL,
  opened_at          DATETIME        NOT NULL,
  matures_at         DATETIME        NULL,
  status             ENUM('open','matured','closed') NOT NULL DEFAULT 'open',
  PRIMARY KEY (id),
  KEY ix_bank_player (player_id),
  CONSTRAINT fk_bank_player FOREIGN KEY (player_id) REFERENCES players (id),
  CONSTRAINT ck_bank_balance CHECK (balance >= 0)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE inventories (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
  player_id     BIGINT UNSIGNED NOT NULL,
  item_id       BIGINT UNSIGNED NOT NULL,
  quantity      INT UNSIGNED    NOT NULL DEFAULT 1,
  enhance_level TINYINT UNSIGNED NOT NULL DEFAULT 0,
  location      ENUM('bag','storage') NOT NULL DEFAULT 'bag',
  acquired_at   DATETIME        NOT NULL,
  PRIMARY KEY (id),
  KEY ix_inv_player (player_id, location),
  CONSTRAINT fk_inv_player FOREIGN KEY (player_id) REFERENCES players (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
```

`CHECK` 제약은 MySQL 8.0.16 이상에서 동작합니다. 서버의 MySQL 버전이 그보다 낮으면 애플리케이션 검사만으로 대체하고 그 사실을 이 문서에 적습니다.

## 4. 거래 처리 규칙

잔액이 바뀌는 모든 작업은 다음 순서를 따릅니다.

```sql
START TRANSACTION;
SELECT balance FROM wallets WHERE player_id = ? FOR UPDATE;   -- 행 잠금
-- 검증: 잔액, 조건
UPDATE wallets SET balance = balance + ?, updated_at = ? WHERE player_id = ?;
INSERT INTO ledger_entries (...) VALUES (...);                -- 변경마다 한 행
-- 대응하는 변화: 인벤토리 추가, 계좌 잔액 등
INSERT INTO idempotency_keys (...) VALUES (...);
COMMIT;
```

- 여러 행을 잠글 때는 항상 같은 순서(지갑 → 은행 계좌 → 대출 → 인벤토리)로 잠가 교착을 피합니다.
- 교착이 감지되면 정해진 횟수만큼 다시 시도합니다.
- 중복 방지 키가 이미 있으면 저장된 응답을 그대로 돌려줍니다.

## 5. 마이그레이션

| 항목 | 규칙 |
|---|---|
| 형식 | `server/migrations/NNNN_설명.sql`, 번호순 적용 |
| 기록 | `schema_migrations`에 적용 번호와 시각 |
| 방향 | 앞으로만 진행. 되돌릴 일이 생기면 새 마이그레이션으로 |
| 호환 | 열·테이블 **추가**를 먼저 배포하고, 코드가 옮겨 간 뒤에 옛 열을 정리 |
| 삭제 | `DROP`, 열 삭제, 데이터 대량 변경은 별도 승인 후 |
| 백업 | 운영 적용 전 반드시 DB 덤프 |
| 순서 | 로컬 → 스테이징 → 운영 |

## 6. 권한

| 계정 | 권한 | 용도 |
|---|---|---|
| 애플리케이션 계정 | `city_guardians`에 대한 SELECT, INSERT, UPDATE, DELETE | 웹·API |
| 마이그레이션 계정 | 위 + CREATE, ALTER, INDEX | 배포 때만 |
| 읽기 전용 계정 | SELECT | 통계, 점검 |

- 애플리케이션은 DB 관리자(root) 계정으로 접속하지 않습니다.
- 각 계정은 `city_guardians` 계열 DB에만 권한을 가집니다. 같은 서버의 다른 DB에는 접근할 수 없어야 합니다.
- 계정과 비밀번호는 서버의 `.env`에만 둡니다.

## 7. 보관과 정리

| 데이터 | 정책 |
|---|---|
| 원장, 대출, 주식 체결 | 삭제하지 않음 |
| 만료된 세션 | 매일 정리 |
| 중복 방지 키 | 7일 뒤 정리 |
| 시각별 주가 | 30일 뒤 일별 요약만 남김 |
| 채팅 메시지 | [COMMUNITY_SYSTEM](COMMUNITY_SYSTEM.md) 5.8 |
| 시스템 로그 | 90일 |

## 8. 백업

- 매일 `city_guardians` 전체 덤프, 최근 14일분 보관.
- 배포 전 덤프는 따로 보관.
- 백업 파일은 웹에서 접근할 수 없는 경로에 둡니다.
- 정기적으로 백업을 다른 DB에 복원해 보아 실제로 쓸 수 있는지 확인합니다.

절차는 [SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md) 6장에 있습니다.
