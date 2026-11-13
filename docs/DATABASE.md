# DATABASE — 데이터베이스

> 기준 v10.0 · 2026-11-14 · 상태: v8.0의 전투·도시 위험도에 필요한 테이블 43개(`0001`~`0004`), v7.0 CITY PASS·장학의 테이블 27개(`0005`~`0007`, 2.12), v9.0 도시 성장·협동 방어전·지방세의 테이블 21개(`0008`~`0011`, 2.13)를 추가형 마이그레이션 11개(`server/migrations/`)로 작성하고 **로컬 테스트 DB(MariaDB 11.4)에서만** 적용·검증함. v9.0의 적용·롤백 계획은 9장. **v10.0(실시간 대시보드)은 테이블과 마이그레이션을 더하지 않음** (2.14). **운영 DB에는 적용하지 않음.** 가방·은행·주식·대출·커뮤니티 테이블은 v6.0의 목표 설계 그대로이고 마이그레이션이 없음

## 0. 먼저 읽을 것

### 실제로 만든 것 (v8.0, v7.0)

| 마이그레이션 | 내용 |
|---|---|
| `0001_core.sql` | 전투 기능이 기대는 공통 테이블 18개 (플레이어·인증·캐릭터·지갑·원장·중복 방지·일일 집계·요청 제한·지역·설정·관리자·로그) |
| `0002_combat_v8.sql` | v8.0 테이블 25개 (2.11) |
| `0003_seed_combat_v8.sql` | 초기 정의 데이터: 전투 모드 3, 장비 8, 적 4, 방어 구역 1, 방어시설 15, 사냥 구역 3, FPS 임무 1, 주요 시설 3, 설정 3 |
| `0004_seed_regions.sql` | 16개 시·도, 230개 시·군·구 (기준일 2026-10-01, 클라이언트의 지역 목록과 같은 원본). **지역 목록이 비어 있을 때만** 넣음. 운영 DB의 기존 목록(이 문서 2.3은 229개라고 적고 있음)과 코드가 같은지는 확인하지 못함 |
| `0005_city_pass_v7.sql` | v7.0 현실 혜택의 공통 장치와 CITY PASS 테이블 14개 (2.12) |
| `0006_scholarship_v7.sql` | v7.0 장학 테이블 13개 (2.12) |
| `0007_seed_benefits_v7.sql` | 현실 혜택 기능 4개의 목록. **모두 꺼진 채, 법률 검토 전 상태로** 넣음. 제휴 상점·혜택·쿠폰·장학 프로그램은 넣지 않음 |
| `0008_city_growth_v9.sql` | v9.0 도시 성장 테이블 8개: 전입자 현황, 확장 기준, 관리자 알림, 모델링 제작·결과물 (2.13) |
| `0009_coop_defense_v9.sql` | v9.0 협동 방어전 테이블 6개: 판, 팀원, 역할, 전술 핑, 공동 예산, 핑 차단 (2.13) |
| `0010_city_tax_v9.sql` | v9.0 지방세·도시 재정·참여예산 테이블 7개 (2.13) |
| `0011_seed_city_v9.sql` | 확장 기준 4개, 팀 역할 4개, 세금 정책 3개(**모두 꺼짐**), 설정 5개(`growth`, `coop`, `ping`, `treasury`, `budget` — 협동 방어전과 참여예산은 **꺼짐**). 모두 `INSERT IGNORE` |
| `requirements.php` | 사전 점검 목록: 이 서버가 쓰는 테이블의 열 (v7.0의 27개, v9.0의 21개 포함) |

**기존 데이터를 보존하는 방법**

- 모든 테이블은 `CREATE TABLE IF NOT EXISTS`, 모든 초기 데이터는 `INSERT IGNORE` 또는 "비어 있을 때만"입니다. 이미 있는 테이블과 행은 건드리지 않습니다. `ALTER`, `DROP`, `UPDATE`, `DELETE`가 없습니다.
- `CREATE TABLE IF NOT EXISTS`는 이름이 같은 기존 테이블을 그냥 지나칩니다. 운영 DB에 `players`, `wallets` 같은 테이블이 **다른 구조로** 이미 있으면 코드가 그 위에서 돌다가 오류가 납니다. 그래서 `bin/migrate.php`는 적용하기 전에 **사전 점검**을 합니다: 같은 이름의 테이블이 있으면 필요한 열이 있는지 확인하고, 하나라도 없으면 **아무것도 바꾸지 않고 멈춥니다.** 그 경우에는 기존 구조를 확인하고 열을 더하는 마이그레이션을 따로 작성해야 합니다.
- 마이그레이션 도구는 `DROP`, `TRUNCATE`, 열 삭제가 들어 있는 파일을 실행하지 않습니다.
- 운영 DB의 구조는 여전히 확인하지 못했습니다. 아래 "적용 순서"의 1~2단계가 그대로 남아 있습니다.

**v6.0 목표 스키마와 다른 점** (구현하면서 정한 것)

| 항목 | v6.0 설계 | v8.0 구현 | 이유 |
|---|---|---|---|
| 시각 | 미정 | **UTC** `DATETIME`. 날짜 경계(일일 한도, NPC 순찰)만 한국 날짜 | 서버 시간대 설정에 기대지 않기 위해 |
| JSON | `JSON` 열 | `TEXT` / `MEDIUMTEXT`에 JSON 문자열 | MySQL 5.7 / 8 / MariaDB에서 똑같이 동작 |
| `ENUM` | 사용 | `VARCHAR` + 애플리케이션 검사 | 값을 더할 때 `ALTER`가 필요 없게 |
| 외래 키, `CHECK` | 금융·소유에 제약 | **없음.** 색인과 애플리케이션 검사만 | 구조를 모르는 기존 테이블에 제약을 걸 수 없음. 운영 DB를 확인한 뒤에 더할 수 있음 |
| 이름 | `tower_defs`, `stage_defs`, `player_towers`, `battle_sessions` | `defense_structures`, `defense_zones`, `player_defense_structures`, `defense_battles` | v8.0 지시서의 테이블 이름 |
| `stage_clears`, `reward_rejections` | 별도 테이블 | 없음. `defense_battles` / `fps_mission_results`의 `result`, `accepted`, `reject_reason`에서 읽음 | 한 곳에만 저장 |
| `idempotency_keys.key` | `key` | `idem_key` (+ `endpoint`, `status_code`) | `key`는 예약어 |
| `rate_limits` | 없음 | 추가 | 게스트 생성·토큰 발급·관리자 로그인 횟수 제한 |

### 운영 DB에 적용하기 전에

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

### 2.11 v8.0 전투와 도시 위험도 (구현됨)

`server/migrations/0002_combat_v8.sql`. 지시서 11장의 테이블 18개가 모두 있고, 그것을 받치는 테이블 7개가 더 있습니다 (★ 표시).

**공통 전투**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `combat_modes` | 전투 모드 3종 | `code`, `name`, `camera`, `controls`, `is_closed` |
| `combat_equipment` | 전투 장비 정의 | `code`, `slot`, `kind`, `modes`, `price_gc`, `unlock_level`, `max_level`, `upgrade_base_gc`, `upkeep_gc`, 성능 열들, `is_coming_soon`, `is_active` |
| `player_combat_equipment` ★ | 가진 장비. 한 사람이 한 장비를 한 점. 무료 기본 장비는 행이 없어도 가진 것으로 봄 | `player_id`, `equipment_id`, `level` |
| `equipment_upgrades` | 강화 기록 (추가만) | `player_id`, `equipment_id`, `from_level`, `to_level`, `cost_gc` |
| `combat_loadouts` | 모드·칸별 장착 | `player_id`, `mode_code`, `slot`, `equipment_id` |
| `player_combat_state` ★ | 전투에 관한 플레이어 상태 | `last_active_at`(활성 가디언 판정), `hunt_kill_carry`(위험도에 아직 반영되지 않은 사냥 처치), `support_request_id`(지원 나간 요청) |
| `enemy_defs` ★ | 적 정의 | 능력치, `reward_gc`, `reward_xp`, `base_damage`, `stagger_immune` |

레벨·경험치(`characters`), GC(`wallets` + `ledger_entries`), 소속 도시(`players.region_id`)는 0001의 테이블 하나씩에만 있습니다. **모드별로 따로 저장하는 테이블이 없습니다.**

**디펜스**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `defense_zones` | 방어 구역 (스테이지) | `base_hp`, `start_energy`, `waves`(JSON), 보상, `min_seconds_per_wave`, `win_defense_points` |
| `defense_structures` | 타워와 방어시설 15종 | `tier`, `kind`, `ownership`(battle / permanent / city), `unlock_gc`(가격), `energy_cost`(설치 비용), `damage`, `range_m`, `shots_per_second`, `max_hp`(내구도), `upkeep_gc`, `upgrade_energy_cost`, `max_per_battle`(설치 제한), `unlock_level`(해금 조건), `city_goal_gc`, `city_power`, `city_max_level`, `is_coming_soon` |
| `player_defense_structures` | 개인이 영구 해금한 시설 (**개인 자산**) | `player_id`, `structure_id`, `level` |
| `city_defense_structures` | 도시 공용 방어시설 (**도시 자산**) | `region_id`, `structure_id`, `funded_gc` |
| `defense_battles` | 방어전 한 판 | `public_id`, `player_id`, `zone_id`, `region_id`, `invasion_id`, `status`, `started_at`, `ended_at`, `result`, `accepted`, `reject_reason`, `kills`, 보상, `response` |

**헌트**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `hunt_zone_defs` ★ | 사냥 구역 | `enemy_codes`, `max_alive`, `respawn_seconds`, `max_kills_per_minute`, `burst_allowance` |
| `hunt_sessions` | 사냥 세션 | `public_id`, `player_id`, `zone_id`, `last_report_at`, `allowance`, `next_seq`, 합계, `last_response` |

**FPS**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `fps_missions` | 임무 정의 | `enemy_groups`(JSON), `facility_count`, `facility_hp`, `civilian_count`, `time_limit_seconds`, `min_seconds`, 보상 |
| `fps_mission_results` | 임무 한 번의 기록과 결과 | `public_id`, `player_id`, `mission_id`, `region_id`, `invasion_id`, `status`, `result`, `accepted`, `reject_reason`, `facilities_saved`, `civilians_saved`, 보상, `reward_limited`, `response` |

**도시 위험도, 침공, 지원**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `city_risk_status` | 도시의 위험도와 집계 | `risk_centi`(0~10000), `stabilized_date`, `player_wins`, `player_attempts`, `invasion_severity_total`, `repelled_severity`, `npc_held_count`, `failed_count`, `contribution`, `support_given`, `support_received` |
| `city_risk_history` | 위험도 변경 이력 (추가만) | `region_id`, `at`, `reason`, `actor`(player / supporter / npc / system / admin), `delta_centi`, `after_centi`, `stage`, `reference`, `player_id`, `admin_id` |
| `city_risk_policy` ★ | 위험도 계산 정책과 랭킹 가중치. 고칠 때마다 새 행 (이전 정책이 이력으로 남음) | `policy`(JSON), `ranking`(JSON), `admin_id`, `reason` |
| `city_world_state` ★ | 침공 판정의 진행 상태. 한 행 | `last_tick`, `seed` |
| `city_invasion_events` | 침공 이벤트 | `region_id`, `tick`, `kind`, `severity`, `status`(active / repelled / npc_held / failed), `source`(system / admin), `started_at`, `ends_at`, `required`, `progress` |
| `city_facility_defs` ★ | 도시 주요 시설 정의 | `code`, `name`, `repair_gc`, `max_damage` |
| `city_facility_damage` | 시설의 피해 단계 | `region_id`, `facility_id`, `damage` |
| `city_support_requests` | 지원 요청 | `region_id`, `status`(open / resolved / expired), `opened_at`, `closes_at`, `wins`, `points` |
| `city_support_contributions` | 지원 기여도 | `request_id`, `player_id`, `home_region_id`, `wins`, `points` |

지켜지는 것:

- `city_risk_status`에는 플레이어의 것(`player_*`, `contribution`)과 NPC의 것(`npc_held_count`)이 따로 있습니다.
- `(region_id, tick)`이 유일해 같은 침공 판정이 두 번 돌아도 침공이 두 번 생기지 않습니다.
- 이 테이블들은 은행·대출·신용점수 테이블을 참조하지 않습니다 (테스트 `testCityTablesDoNotReferToBankLoanOrCreditData`).
- `city_risk_history`와 `equipment_upgrades`, `ledger_entries`는 추가만 합니다. 서버 코드에 이 테이블의 `UPDATE`·`DELETE`가 없습니다.

### 2.12 v7.0 현실 혜택: CITY PASS 와 장학 (구현됨)

`server/migrations/0005_city_pass_v7.sql`, `0006_scholarship_v7.sql`. 지시서 4장의 테이블 18개가 모두 있고, 그것을 받치는 테이블 9개가 더 있습니다 (★ 표시). 설명은 [CITY_PASS_SYSTEM](CITY_PASS_SYSTEM.md), [COUPON_SYSTEM](COUPON_SYSTEM.md), [SCHOLARSHIP_SYSTEM](SCHOLARSHIP_SYSTEM.md), [PRIVACY_DATA_FLOW](PRIVACY_DATA_FLOW.md).

**기능 승인과 홈페이지 로그인**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `benefit_features` ★ | 현실 혜택 기능의 스위치와 법률 검토 기록. 4행 | `code`, `is_enabled`(기본 0), `legal_status`(not_reviewed / in_review / approved / rejected), `legal_reference`, `legal_reviewer`, `legal_reviewed_on`, `legal_recorded_by`, `enabled_by` |
| `benefit_feature_history` ★ | 기능 승인의 이력 (추가만) | `feature_code`, `action`(legal_update / enable / disable / auto_disable), `before_value`, `after_value`, `admin_id`, `reason` |
| `web_login_codes` ★ | 홈페이지 로그인용 일회용 코드 (해시만) | `player_id`, `code_hash`, `expires_at`, `used_at` |
| `web_sessions` ★ | 홈페이지 접속 (토큰은 해시만) | `player_id`, `token_hash`, `csrf_token`, `last_seen_at`, `expires_at`, `revoked_at` |

**CITY PASS**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `city_pass_memberships` | 멤버십. **이름·연락처·주소가 없다** | `player_id`(유일), `member_no`(CP-XXXX-XXXX, 유일), `interest_region_id`(관심 지역. 실제 거주지가 아님), `status`, `terms_version` |
| `partner_merchants` | 제휴 업체 | `public_id`, `name`, `category`, `status`(draft / pending / approved / suspended / ended), `contract_reference`, `contract_starts_on`, `contract_ends_on`, `contact_*`(밖에 보이지 않음), `youth_safe_confirmed`, `created_by`, `approved_by` |
| `partner_branches` | 지점 | `merchant_id`, `name`, `region_id`, `address`, `phone`, `lat`, `lng`, `image_file`, `is_active` |
| `partner_offers` | 혜택 | `merchant_id`, `branch_id`(NULL = 모든 지점), `title`, `discount_type`, `discount_value`, `conditions`, `issue_policy`(membership / promotion / partner_condition), `starts_at`, `ends_at`, `total_limit`, `per_member_limit`, `coupon_valid_days`, `issued_count`, `status`(draft / pending / approved / paused / ended) |
| `benefit_coupons` | 쿠폰 | `public_id`, `offer_id`, `merchant_id`, `membership_id`, `player_id`, `status`(issued / redeemed / expired / cancelled), `issue_reason`, `expires_at`, `present_code_hash`(유일), `present_expires_at`, `redeemed_at` |
| `coupon_redemptions` | 사용 기록. **`coupon_id`가 유일**: 쿠폰 하나에 한 줄 | `coupon_id`, `offer_id`, `merchant_id`, `branch_id`, `merchant_user_id`, `membership_id`, `redeemed_at` |
| `coupon_events` ★ | 쿠폰의 거래 로그 (추가만) | `coupon_id`, `event`(issued / presented / verify_failed / redeemed / expired / cancelled), `merchant_id`, `merchant_user_id`, `player_id`, `detail` |
| `merchant_users` | 상점 계정 | `merchant_id`, `branch_id`(NULL = 모든 지점), `login`(유일), `password_hash`, `role`(owner / staff), `is_active`, `must_change_password`, `failed_attempts`, `locked_until` |
| `merchant_sessions` ★ | 상점 접속 | `merchant_user_id`, `token_hash`, `csrf_token`, `ip`, `expires_at`, `revoked_at` |
| `merchant_audit_logs` | 상점 계정의 행동 기록 (추가만) | `merchant_user_id`, `merchant_id`, `action`, `target`, `detail`, `ip` |

**장학**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `scholarship_programs` | 프로그램 | `public_id`, `category`, `title`, `support_type`, `support_detail`, `capacity`, `apply_starts_at`, `apply_ends_at`, `result_on`, `guardian_consent_age`, `min_reviews`, `appeal_days`, `retention_days_unselected`, `retention_days_selected`, `status`(draft / pending / published / closed / announced / archived), `created_by`, `approved_by` |
| `scholarship_eligibility_rules` | 지원 자격 | `program_id`, `rule_type`(school_level / birth_year / statement / document 만), `params`(JSON), `description` |
| `scholarship_criteria` ★ | 평가 기준 | `program_id`, `name`, `max_score`, `weight` |
| `scholarship_applications` | 신청. `(program_id, player_id)`가 유일 | `public_id`(접수 번호), `status`, `eligibility_status`, `consent_status`, `school_level`, `is_minor`, `form_enc`(암호화), `result`, `result_notified_at`, `disbursement_status`, `purge_after`, `purged_at` |
| `scholarship_applicant_profiles` ★ | 신청자의 본인·연락 정보. 신청서와 따로 둠 | `application_id`, `name_enc`, `birth_year`, `school_name_enc`, `email_enc`, `phone_enc`, `purged_at` |
| `scholarship_documents` | 제출 자료 (글 또는 주소. 파일 없음) | `application_id`, `kind`, `title`, `content_enc`, `url` |
| `scholarship_consents` | 개인정보 동의, 보호자 동의 | `application_id`, `consent_type`(privacy / guardian), `status`, `version`, `guardian_name_enc`, `guardian_relation`, `guardian_contact_enc`, `token_hash`, `token_expires_at`, `verified_by`, `verify_method` |
| `scholarship_reviewers` | 프로그램의 심사위원 | `program_id`, `admin_id`, `is_active`, `assigned_by` |
| `scholarship_reviews` | 심사. `(application_id, reviewer_id)`가 유일 | `admin_id`, `status`(assigned / submitted / recused), `scores`(JSON), `total_centi`, `comment`, `recuse_reason` |
| `scholarship_awards` | 선정 결과. `application_id`가 유일 | `decision`(selected / waitlisted / not_selected), `score_centi`, `review_count`, `status`(proposed / approved / reopened), `decided_by`, `approved_by` |
| `scholarship_appeals` ★ | 이의신청. `application_id`가 유일 | `reason_enc`, `status`(submitted / upheld / dismissed), `response`, `handled_by` |
| `scholarship_disbursement_records` | 지급 **상태** 기록. 계좌번호 열이 없다 | `award_id`, `application_id`, `support_type`, `description`, `amount_krw`(장부용), `status`(planned / approved / completed / cancelled), `external_reference`, `created_by`, `approved_by`, `completed_by` |
| `scholarship_audit_logs` | 감사 로그 (추가만). 개인정보 열람(`pii.view`) 포함 | `actor_type`(admin / applicant / guardian / system), `actor_id`, `action`, `program_id`, `application_id`, `detail`, `ip` |

지켜지는 것:

- **이 27개 테이블은 지갑·원장·전투·랭킹·은행 테이블을 참조하지 않습니다.** `player_id`로 계정을, `region_id`로 지역을, `admin_id`로 관리자를 가리킬 뿐입니다. 혜택 서비스 코드가 지갑과 게임 기록을 읽지 않는다는 것은 테스트(`BenefitFeatureTest::testBenefitServicesNeverTouchWalletsOrGameRecords`)가 소스를 훑어 확인합니다.
- 기존 테이블에 열을 더하지 않았습니다. `ALTER`가 없습니다.
- 초기 데이터는 기능 4행뿐이고 모두 꺼져 있습니다. 가짜 제휴 상점이나 장학 프로그램을 넣지 않았습니다.
- `*_enc` 열은 애플리케이션이 AES-256-GCM 으로 암호화해 넣습니다. DB 에는 평문이 없습니다. 주민등록번호·계좌번호·주소를 담는 열이 없습니다.
- 일회용 코드와 토큰(`code_hash`, `token_hash`, `present_code_hash`)은 해시만 저장합니다.
- 같은 쿠폰이 두 번 쓰이지 않는 것은 조건부 `UPDATE`(상태가 `issued`일 때만)와 `coupon_redemptions.coupon_id`의 유일 색인 두 겹으로 지킵니다.
- 개인정보 테이블에 대한 **DB 계정 수준의 권한 분리는 하지 않았습니다** (6장). 접근 제한은 애플리케이션의 역할과 열 암호화로 합니다.

### 2.13 v9.0 도시 성장, 협동 방어전, 지방세 (구현됨)

`server/migrations/0008_city_growth_v9.sql`, `0009_coop_defense_v9.sql`, `0010_city_tax_v9.sql`. 지시서 8장의 테이블 17개가 모두 있고, 그것을 받치는 테이블 4개가 더 있습니다 (★ 표시). 설명은 [CITY_GROWTH_SYSTEM](CITY_GROWTH_SYSTEM.md), [CITY_MODELING_PIPELINE](CITY_MODELING_PIPELINE.md), [COOP_DEFENSE_SYSTEM](COOP_DEFENSE_SYSTEM.md), [TACTICAL_MINIMAP](TACTICAL_MINIMAP.md), [CITY_TAX_SYSTEM](CITY_TAX_SYSTEM.md), [CITY_TREASURY_SYSTEM](CITY_TREASURY_SYSTEM.md), [PARTICIPATORY_BUDGET](PARTICIPATORY_BUDGET.md).

**도시 성장과 모델링**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `city_population_stats` | 도시별 전입자 현황. 도시마다 한 행. 서버가 다시 세어 채움 | `region_id`(PK), `valid_residents`(현재 유효 전입자), `registered_residents`(게스트 포함, 참고용), `cumulative_residents`(누적), `growth_7d`, `growth_30d`, `stage`(확장 단계), `stage_milestone_id`, `modeling_threshold`(이 도시만의 기준. NULL = 공통), `threshold_reached`, `threshold_reached_at`, `modeling_status`, `content_version`(적용 버전), `opened_at`(정식 개방), `counted_at` |
| `city_population_daily` ★ | 날마다의 전입자 수 (한국 날짜). 7일·30일 증가량과 추이 | `(region_id, date)`(PK), `valid_residents`, `registered_residents` |
| `city_resident_marks` ★ | 한 계정이 한 도시에서 처음 유효 전입으로 인정된 때. 누적 전입자 수의 근거 | `(region_id, player_id)`(PK), `first_counted_at` |
| `city_residency_events` ★ | 전입·이사 기록 (추가만) | `player_id`, `region_id`, `kind`(register / move_in / move_out), `other_region_id`, `fee_gc` |
| `city_growth_milestones` | 도시 확장 기준. 관리자가 고침 | `code`(유일), `name`, `threshold`, `action`(landmark_review / street_candidate / street_open / special_zone_review), `opens_street`(이 기준이 "상징거리 모델링 기준 인원"인가), `is_active` |
| `city_growth_notifications` | 관리자 알림. **`dedupe_key`가 유일**: 같은 도시의 같은 기준에 알림이 두 번 생기지 않음 | `kind`(milestone = 기준 도달 / modeling = 모델링 진행), `region_id`, `milestone_id`, `project_id`, `dedupe_key`, `title`, `body`, `status`(new / acknowledged / assigned / closed), `acknowledged_by`, `assigned_admin_id`, `assigned_by` |
| `city_modeling_projects` | 모델링 제작 작업 | `public_id`, `region_id`, `milestone_id`, `notification_id`, `kind`(landmark / street / special_zone), `title`, `status`(NOT_STARTED / PLANNING / MODELING / REVIEW / READY / DEPLOYED), `assignee_admin_id`(담당자), `memo`, `due_on`(예정일), `file_version`, `review_result`(none / pass / fail), `review_note`, `reviewed_by`, `applied_at`(적용 일시) |
| `city_modeling_assets` | 결과물(콘텐츠 묶음)의 판. **파일은 없고** 저장소 안의 경로와 해시만. `(project_id, version)`이 유일 | `public_id`, `project_id`, `region_id`, `version`, `address_key`, `catalog_path`(상대 경로), `content_hash`(SHA-256), `size_bytes`, `min_client_version`, `status`(uploaded / approved / rejected / released / withdrawn), `rollout_percent`, `uploaded_by`, `reviewed_by`, `released_by`, `withdrawn_by`, `withdraw_reason` |

**협동 방어전**

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `defense_sessions` | 공동 방어전 한 판. 전투 중의 상태는 여기에 없음 (실시간 서버의 메모리) | `public_id`, `region_id`, `zone_id`, `host_player_id`, `status`(lobby / running / finished / cancelled / expired / abandoned), `max_players`, `players_at_start`, `expires_at`, `started_at`, `ended_at`, `result`, `waves_cleared`, `gate_hp_left`, `duration_seconds`, `energy_granted`, `energy_spent`, `energy_left`, `close_reason`, `response`(결과를 처음 반영했을 때의 응답. 다시 보고받으면 이것을 돌려줌) |
| `defense_session_members` | 팀원. **`(session_id, player_id)`가 유일** | `role_code`(NULL 가능), `status`(joined / left), `connected_seconds`, `kills`, `towers_built`, `towers_upgraded`, `repairs`, `damage_dealt`, `pings_sent`, `pings_rejected`, `eligible`, `reward_gc`, `reward_xp`, `reward_note`, `rewarded_at` |
| `defense_team_roles` | 역할의 정의. 관리자가 고침 | `code`(유일), `name`, `build_discount_percent`, `repair_bonus_percent`, `attack_bonus_percent`, `ping_cooldown_percent`, `max_per_session`(0 = 제한 없음), `is_active` |
| `defense_tactical_pings` | 전술 핑 기록. 판이 끝날 때 실시간 서버가 넘김. 거절된 핑도 남음. `(session_id, seq)`가 유일 | `player_id`, `kind`, `x_centi`, `z_centi`, `at_ms`, `accepted` |
| `defense_shared_budget` | 공동 예산의 장부 (GC 가 아님). **`(session_id, seq)`가 유일**. 줄마다 "바뀐 뒤 잔액"이 이어져야 함 | `player_id`(지급이면 NULL), `kind`(grant_start / grant_wave / grant_kill / build / upgrade / repair), `delta`, `balance_after`, `slot_index`, `structure_code`, `at_ms` |
| `defense_ping_blocks` ★ | 관리자의 전술 핑 차단 | `player_id`, `reason`, `admin_id`, `expires_at`(NULL = 풀 때까지), `lifted_at`, `lifted_by` |

**지방세, 도시 재정, 참여예산** (금액은 모두 GC)

| 테이블 | 내용 | 주요 열 |
|---|---|---|
| `city_tax_policies` | 세금 정책. 세금 종류마다 한 행. 처음에는 모두 꺼짐 | `tax_type`(trade / business / property, 유일), `is_active`(기본 0), `rate_bp`(1/100 %), `applies_to`(원장 사유를 쉼표로), `exempt_amount`, `min_level`, `grace_days`, `cap_gc`, `period`(transaction / weekly / monthly) |
| `city_tax_transactions` | 세금 거래 명세. **`(tax_type, source_key)`가 유일**: 같은 거래·같은 기간에 두 번 걷지 못함 | `public_id`, `tax_type`, `policy_id`, `player_id`, `region_id`(세금이 들어간 도시), `base_gc`, `rate_bp`, `tax_gc`, `included`(1 = 치른 값에 들어 있음), `source_reason`, `source_key`(`L<원장 번호>` / `P<기간>:<계정>`), `source_ledger_id`, `wallet_ledger_id`, `period_key` |
| `city_treasury_accounts` | 도시 재정 계정. `(region_id, account)`가 유일 | `account`(general / reserve), `balance`, `total_in`, `total_out` |
| `city_treasury_ledger` | 도시 재정 원장 (추가만) | `region_id`, `account`, `delta`, `balance_after`, `reason`(tax_trade / tax_business / tax_property / expenditure), `ref_type`, `ref_id` |
| `city_budget_proposals` | 참여예산 제안 | `public_id`, `region_id`, `player_id`, `category`, `title`, `description`, `requested_gc`, `target_code`, `status`(submitted / rejected / voting / passed / failed / executed / withdrawn / cancelled), `review_note`, `reviewed_by`, `vote_starts_at`, `vote_ends_at`, `yes_count`, `no_count`, `quorum`, `decided_at`, `expenditure_id` |
| `city_budget_votes` | 투표. **`(proposal_id, player_id)`가 유일.** 표의 무게를 담는 열이 없음 | `choice`(yes / no) |
| `city_budget_expenditures` | 예산 집행 내역 (공개). **`proposal_id`가 유일**: 제안 하나는 한 번만 집행 | `public_id`, `region_id`, `proposal_id`(NULL = 직접 집행), `category`, `title`, `description`, `amount_gc`, `account`, `effect_type`(structure_fund / facility_repair / record), `target_code`, `effect_detail`, `executed_by`, `reason` |

**기존 테이블과의 관계 — 겹치는 구조를 만들지 않기 위해 본 것**

| 필요한 것 | 기존에 있던 것 | 정한 것 |
|---|---|---|
| 누가 어느 도시의 주민인가 | `players.region_id`, `residency_status`, `registered_at` (v8.0) | **그대로 원본으로 씀.** `city_population_stats`는 그것을 센 결과(다시 만들 수 있는 값)일 뿐 |
| 도시의 주민 수 | `regions.resident_count` (전입신고를 한 계정 수) | 그대로 둠. 뜻이 다른 값(유효 전입자)이라 새 표에 따로 둠. 기존 화면·랭킹은 바뀌지 않음 |
| 방어 구역, 타워, 적, 타워 해금 | `defense_zones`, `defense_structures`, `enemy_defs`, `player_defense_structures` (v8.0) | 협동 방어전이 **같은 정의를 읽음.** 새 정의 테이블을 만들지 않음 |
| 방어전 기록 | `defense_battles` (혼자 하는 방어전, 한 사람 한 줄) | 여러 사람이 한 판에 있는 구조가 달라 `defense_sessions` + `defense_session_members`로 따로 둠. 기존 테이블은 그대로 |
| 도시 공용 방어시설, 시설 피해 | `city_defense_structures`, `city_facility_damage` (v8.0) | 도시 재정의 집행이 **같은 행을 채우고 고침.** 주민의 기여금과 같은 곳에 쌓임 |
| 플레이어의 GC | `wallets`, `ledger_entries` | 세금의 지갑 쪽은 **기존 원장**에 남김 (사유 `tax_business`, `tax_property`). 도시 재정은 플레이어의 지갑이 아니라 따로 원장을 둠 |
| 설정과 이력 | `game_config`, `config_history` | 그대로 씀 (키 5개 추가) |
| 감사 기록 | `admin_audit_logs` | 그대로 씀 |
| 하루 횟수 | `daily_counters` | 그대로 씀 (`coop_rewards`, `tax_income`, `tax_business`) |
| 중복 요청 방지 | `idempotency_keys` | 그대로 씀 |
| 관리자 알림 | 없음 | `city_growth_notifications` 신규 |
| 현실 혜택의 예산 (v7.0) | `scholarship_*`, `partner_*` 등 | **가리키지 않음.** 도시 재정과 서로 참조하는 열이 없음 ([CITY_TREASURY_SYSTEM](CITY_TREASURY_SYSTEM.md) 1장) |

지켜지는 것:

- **기존 테이블의 구조를 바꾸지 않았습니다.** `ALTER`가 없습니다. 기존 테이블에 더해지는 것은 행뿐입니다: `game_config`의 키 5개(마이그레이션), 그리고 v9.0 기능을 쓸 때 `ledger_entries`의 새 사유(`tax_business`, `tax_property`, `residency_move`), `daily_counters`의 새 종류, `admins.role`의 새 값 `content`.
- 외래 키와 `CHECK`가 없는 것은 이전과 같습니다. 유일 색인이 중복을 막는 마지막 방어선입니다 (위 표의 굵은 글씨).
- 초기 데이터에 가짜 도시 인구, 가짜 제안, 가짜 세금 기록을 넣지 않았습니다.
- `defense_tactical_pings`와 `defense_shared_budget`은 판마다 수천 줄까지 쌓일 수 있습니다 (판마다 핑 3,000줄, 예산 6,000줄이 상한). 보관 기간은 정하지 않았습니다 (7장).

### 2.14 v10.0 실시간 대시보드 (DB 변경 없음)

v10.0은 **마이그레이션이 없습니다.** 테이블, 열, 색인을 더하지 않았습니다.

- 대시보드의 요약(`GET /dashboard/summary`)은 기존 테이블을 **읽기만** 합니다: `wallets`, `ledger_entries`(자산 변동 7일), 도시 위험도와 침공·지원·랭킹의 테이블(2.11), `city_population_stats`와 모델링(2.13), `city_treasury_accounts`(2.13), `region_contributions`.
- 관리자 설정은 v8.0의 `game_config`에 `dashboard` 키로 저장됩니다. 행은 **관리자가 처음 저장할 때** 생기고 (마이그레이션이 넣지 않음), 그 전에는 코드의 기본값을 씁니다. 변경 이력은 `config_history`, 감사 기록은 `admin_audit_logs`에 남습니다.
- 이용자가 정한 카드 상태(접힘·숨김·크기)는 서버에 저장하지 않습니다. 기기의 파일입니다.
- 자산 변동 조회는 `ledger_entries`를 계정과 날짜로 읽고 (7일 범위, 계정 하나) 기존 색인 `ix_ledger_player_time (player_id, created_at)`에 맞습니다. 원장이 큰 DB 에서 실제로 재 보지는 않았습니다.

되돌릴 것이 DB 에 없습니다. 코드를 이전 판으로 돌리면 되고, `game_config`의 `dashboard` 행은 남아 있어도 쓰이지 않습니다.

## 3. 핵심 테이블 DDL

돈과 인증에 관련된 테이블의 초안입니다. 나머지는 구현 단계에서 마이그레이션 파일로 작성합니다.

> v8.0: 실제로 적용되는 DDL은 `server/migrations/0001_core.sql`과 `0002_combat_v8.sql`입니다. 아래 초안과 다른 곳(`ENUM` 대신 `VARCHAR`, 외래 키·`CHECK` 없음, `idem_key`)은 0장의 표에 적었습니다. 아래의 은행·대출 테이블은 아직 마이그레이션이 없습니다.

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
- v8.0 서버의 잠금 순서: 지갑 → 전투 기록(`defense_battles` / `hunt_sessions` / `fps_mission_results`) → 캐릭터 → `player_combat_state` → `daily_counters` → `city_world_state`(id = 1). 도시의 위험도·침공·지원을 바꾸는 일은 모두 `city_world_state`의 한 행을 잠근 채 합니다. 그래서 침공 판정과 전투 결과가 겹쳐도 순서대로 처리되지만, **도시를 바꾸는 요청은 한 번에 하나씩만 처리됩니다** (접속이 많아지면 도시별 잠금으로 나눠야 합니다).
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

### 도구 (v8.0)

```
php server/bin/migrate.php            # 상태만 보여 줌: 적용된 것, 밀린 것, 사전 점검 결과. 아무것도 바꾸지 않음
php server/bin/migrate.php --apply    # 밀린 마이그레이션을 적용
```

| 동작 | 내용 |
|---|---|
| 기본 | 상태 보기. `--apply`를 붙여야 적용 |
| 접속 정보 | `server/.env`의 `DB_*`. 비밀번호를 화면에 내보내지 않음 |
| 운영·스테이징 | `APP_ENV`가 `dev` / `test`가 아니면 `--apply`만으로는 거절. 백업과 승인을 확인했다는 뜻의 `--i-have-approval-and-backup`이 더 필요 |
| 사전 점검 | 0장. 문제가 있으면 목록을 보여 주고 아무것도 바꾸지 않음 |
| 위험한 문장 | `DROP`, `TRUNCATE`, 열 삭제가 있는 파일은 실행하지 않음 |
| 기록 | `schema_migrations`에 번호와 시각. 다시 돌리면 "적용할 것이 없습니다" |
| 웹에서 실행 | 불가. 명령줄 전용 |

MySQL의 DDL은 거래 안에서 되돌릴 수 없습니다. 파일 하나가 중간에 실패하면 그 파일의 앞부분은 적용된 채로 남습니다. 모든 문장이 `IF NOT EXISTS` / `INSERT IGNORE`라 다시 돌려도 안전합니다.

## 6. 권한

| 계정 | 권한 | 용도 |
|---|---|---|
| 애플리케이션 계정 | `city_guardians`에 대한 SELECT, INSERT, UPDATE, DELETE | 웹·API |
| 마이그레이션 계정 | 위 + CREATE, ALTER, INDEX | 배포 때만 |
| 읽기 전용 계정 | SELECT | 통계, 점검 |

- 애플리케이션은 DB 관리자(root) 계정으로 접속하지 않습니다.
- 각 계정은 `city_guardians` 계열 DB에만 권한을 가집니다. 같은 서버의 다른 DB에는 접근할 수 없어야 합니다.
- 계정과 비밀번호는 서버의 `.env`에만 둡니다.
- v7.0: 장학 신청의 개인정보(`scholarship_applicant_profiles`, `scholarship_consents`, `*_enc` 열)는 애플리케이션 계정 하나로 읽고 씁니다. 읽기 전용 계정에는 이 테이블들의 권한을 주지 않는 것이 좋습니다 (값은 암호화되어 있지만 태어난 해와 학교급은 평문입니다). 암호화 키(`DATA_KEY`)는 DB 가 아니라 서버의 `.env`에만 있고, DB 덤프와 같은 곳에 두지 않습니다 ([PRIVACY_DATA_FLOW](PRIVACY_DATA_FLOW.md) 4장).

## 7. 보관과 정리

| 데이터 | 정책 |
|---|---|
| 원장, 대출, 주식 체결 | 삭제하지 않음 |
| 만료된 세션 | 매일 정리 |
| 중복 방지 키 | 7일 뒤 정리 |
| 시각별 주가 | 30일 뒤 일별 요약만 남김 |
| 채팅 메시지 | [COMMUNITY_SYSTEM](COMMUNITY_SYSTEM.md) 5.8 |
| 시스템 로그 | 90일 |
| 홈페이지 로그인 코드·접속, 상점 접속 (v7.0) | 만료 7일 뒤 삭제 (`bin/tick.php`) |
| 장학 신청의 개인정보 (v7.0) | 보관 기한(`purge_after`)이 지나면 값을 비움: 선정되지 않음 180일, 선정 5년, 철회 30일 (기본값). 접수 번호·상태·결과·점수는 남음 ([PRIVACY_DATA_FLOW](PRIVACY_DATA_FLOW.md) 5장) |
| 쿠폰, 사용 기록, 쿠폰 거래 로그, 혜택 감사 로그 (v7.0) | 삭제하지 않음 (보관 기간 미정) |
| 세금 명세, 도시 재정 원장, 집행 내역, 제안과 투표 (v9.0) | 삭제하지 않음 |
| 전입·이사 기록, 날마다의 전입자 수 (v9.0) | 삭제하지 않음 (보관 기간 미정) |
| 협동 방어전의 판·팀원 기록 (v9.0) | 삭제하지 않음 |
| 전술 핑 기록, 공동 예산 장부 (v9.0) | 지금은 삭제하지 않음. 양이 많아질 수 있어 보관 기간을 정해야 함 (정리하는 코드 없음) |

## 8. 백업

- 매일 `city_guardians` 전체 덤프, 최근 14일분 보관.
- 배포 전 덤프는 따로 보관.
- 백업 파일은 웹에서 접근할 수 없는 경로에 둡니다.
- 정기적으로 백업을 다른 DB에 복원해 보아 실제로 쓸 수 있는지 확인합니다.

절차는 [SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md) 6장에 있습니다.

## 9. v9.0 마이그레이션 및 롤백 계획

**지금 상태: 운영 DB에는 아무것도 적용하지 않았습니다.** 아래는 승인을 받은 뒤에 따를 계획입니다.

### 9.1 무엇이 바뀌는가

| 파일 | 바뀌는 것 | 기존 데이터에 미치는 영향 |
|---|---|---|
| `0008` ~ `0010` | 새 테이블 21개 (`CREATE TABLE IF NOT EXISTS`) | 없음. 기존 테이블을 읽지도 고치지도 않음 |
| `0011` | 새 테이블 3개에 초기 행 11개, `game_config`에 키 5개 (`INSERT IGNORE`) | 같은 키가 이미 있으면 그대로 둠 |

`ALTER`, `DROP`, `UPDATE`, `DELETE`가 없습니다. 적용한 직후에는 세금·협동 방어전·참여예산이 모두 꺼져 있어, 게임의 동작은 v8.0과 같습니다 (`TaxTest::testEveryTaxIsOffByDefaultAndNothingChangesFromV8`).

코드 쪽에서 함께 바뀌는 것이 하나 있습니다: **DB 접속의 격리 수준을 `READ COMMITTED`로 씁니다** (`CG\Infra\Db`). 서버의 설정을 바꾸는 것이 아니라 이 애플리케이션의 접속에만 적용됩니다. `binlog_format`이 `STATEMENT`인 서버에서는 쓸 수 없으므로 적용 전에 확인합니다 ([SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md) 13장).

### 9.2 적용 순서 (승인 뒤)

1. 운영 DB를 덤프하고, 덤프가 복원되는지 다른 DB에서 확인한다 (8장).
2. `php bin/migrate.php`로 **상태만** 본다: 밀린 마이그레이션 4개, 사전 점검 결과. 이름이 같은 테이블이 다른 구조로 있으면 여기에서 멈춘다.
3. 스테이징(운영 덤프를 복원한 DB)에 먼저 적용하고 서버 테스트와 점검표([TEST_PLAN](TEST_PLAN.md) 15장)를 돌린다.
4. 운영에 적용한다: `php bin/migrate.php --apply --i-have-approval-and-backup`.
5. `php bin/tick.php`를 한 번 돌려 전입자 현황을 채우고, 기록 점검이 통과하는지 본다.
6. 기능은 꺼진 채로 둔다. 켜는 것은 기능마다 따로 결정한다.

MySQL의 DDL은 거래 안에서 되돌릴 수 없습니다. 파일이 중간에 실패하면 앞부분의 테이블은 만들어진 채로 남지만, 모든 문장이 `IF NOT EXISTS` / `INSERT IGNORE`라 원인을 고친 뒤 다시 돌리면 됩니다.

### 9.3 되돌리기 — 가벼운 것부터

되돌릴 일이 생기면 **데이터를 지우지 않는 방법부터** 씁니다.

| 단계 | 방법 | DB 변경 | 승인 |
|---|---|---|---|
| 1. 기능 끄기 | 관리자 화면에서: 세금 정책 끄기, `coop.enabled = 0`, `budget.enabled = 0`, `growth.moveEnabled = 0`, 콘텐츠 배포 철회 | 설정 행만 | 운영 담당 (이력·감사 기록이 남음) |
| 2. 코드 되돌리기 | 이전 릴리스의 코드로 되돌림 ([SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md) 6.4). v9.0 테이블은 **그대로 둠** — 쓰는 코드가 없을 뿐 | 없음 | 배포 승인 |
| 3. 테이블 지우기 | 아래 9.4 | **파괴적** | 별도 승인 + 직전 백업 |

대부분의 문제는 1단계로 끝납니다: 기능을 끄면 새 세금이 걷히지 않고, 새 판이 열리지 않고, 새 투표가 열리지 않습니다. 이미 쌓인 기록은 남습니다.

**2단계에서 알아 둘 것** — v9.0 기능을 켜서 쓴 뒤에 코드를 되돌리면, 기존 테이블에 v9.0 이 남긴 행이 있습니다.

| 남는 것 | 이전 코드에서 |
|---|---|
| `ledger_entries`의 사유 `tax_business`, `tax_property`, `residency_move` | 원장은 그대로 맞음 (잔액은 줄마다 이어짐). 사유의 이름을 모르는 화면에서는 코드가 그대로 보일 수 있음 |
| 이사로 바뀐 `players.region_id`, `registered_at` | 그대로 유효함 (이사한 도시의 주민) |
| 도시 재정이 채운 `city_defense_structures.funded_gc`, 고친 `city_facility_damage` | 그대로 유효함 (시설이 지어진 채로 남음) |
| `game_config`의 키 5개, `daily_counters`의 새 종류 | 읽는 코드가 없어 무시됨 |
| `admins.role = 'content'` | 이전 코드는 이 역할을 모름. **되돌리기 전에 그 계정의 역할을 바꾸거나 비활성화할 것** |
| 걷은 세금 | 플레이어에게 돌려주지 않음. 돌려주려면 보상 지급 절차를 따로 설계해야 함 (자동으로 하는 코드 없음) |

### 9.4 테이블을 지워야 할 때 (최후의 수단)

마이그레이션 도구는 `DROP`이 들어 있는 파일을 실행하지 않습니다. 그래서 되돌리는 SQL은 **저장소에 넣지 않았고**, 필요하면 아래 순서로 사람이 직접 합니다.

1. 왜 1·2단계로 안 되는지 적고 승인을 받는다 ([SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md) 7장).
2. 서버를 점검 상태로 두고 DB를 덤프한다.
3. 지우려는 테이블에 **운영 기록이 있는지** 센다. 세금 명세·도시 재정 원장·투표·협동 방어전 보상 기록이 있으면 지우지 않는 것이 원칙이다 (지갑 원장의 `tax_*` 줄과 `battle_reward` 줄의 근거가 사라진다). 그 경우에는 테이블 이름을 바꿔 보관하는 쪽을 검토한다.
4. 지운다면 v9.0 테이블 21개만, 이 문서 2.13의 목록대로. v8.0·v7.0 테이블과 이름이 겹치는 것은 없다 (`defense_sessions` ≠ `defense_battles`, `defense_team_roles` ≠ `defense_structures`). **`defense_zones`, `defense_structures`, `city_defense_structures`는 v8.0 의 것이므로 지우지 않는다.**
5. `game_config`에서 키 `growth`, `coop`, `ping`, `treasury`, `budget`의 행을, `schema_migrations`에서 `0008` ~ `0011`의 행을 지운다 (다시 적용할 수 있게).
6. 덤프를 다른 DB에 복원해 지우기 전의 상태를 확인할 수 있게 보관한다.

### 9.5 백업에서 복원해야 하는 경우

v9.0 마이그레이션은 기존 행을 고치지 않으므로, 마이그레이션 때문에 백업 복원이 필요해질 일은 없어야 합니다. 그래도 복원한다면 덤프 시점 이후의 **모든** 변경(v8.0의 전투 보상, 지갑 거래 포함)이 사라지므로, 복원은 9.3의 어떤 단계로도 해결되지 않을 때에만, 별도 승인 뒤에 합니다.
