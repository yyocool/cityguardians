# API_SPEC — PHP API 명세

> 기준 v6.0 · 2026-11-13 · 상태: 설계. PHP 서버는 미구현. 클라이언트의 모의 서버(`LocalMockBackend`)가 지금 쓰는 형식은 5.1, 6.1, 7장, 8.1에 적음

## 0. 먼저 읽을 것

운영 서버에 `/api/v1`이 이미 있습니다 (`GET /api/v1/health` 응답: 버전 1.0.0, 환경 production). v1의 다른 경로와 요청·응답 형식은 서버 소스를 확인하지 못해 알 수 없습니다. 이 문서는 v6.0의 목표 명세이며, 기존 v1 경로를 확인한 뒤 겹치는 기능(게스트, 캐릭터, 전입신고, 전투 보상, 은행)은 기존 형식과 맞춰 조정합니다.

공개된 게임 빌드가 아직 없으므로(다운로드 페이지 "준비 중"), 첫 빌드 공개 전까지는 `/api/v1` 안에서 형식을 바꿀 수 있습니다. 첫 공개 뒤에는 호환을 깨는 변경을 `/api/v2`로 냅니다.

## 1. 공통

| 항목 | 내용 |
|---|---|
| 기본 주소 | `https://www.dalfin.net/api/v1` |
| 전송 | HTTPS만. JSON (`Content-Type: application/json; charset=utf-8`) |
| 인증 | `Authorization: Bearer <접속 토큰>` |
| 클라이언트 정보 | `X-Client-Version`, `X-Client-Platform` (`windows` / `android` / `ios`) |
| 중복 방지 | 상태를 바꾸는 요청에 `Idempotency-Key: <UUID>` |
| 시각 | ISO 8601, 시간대 포함 |
| 금액 | 정수 |

### 응답 형식

운영 중인 health 응답의 형식을 그대로 따릅니다.

성공:

```json
{ "ok": true, "data": { }, "server_time": "2026-11-13T10:14:43+09:00" }
```

실패 (형식은 v1 확인 후 확정):

```json
{ "ok": false, "error": { "code": "INSUFFICIENT_GC", "message": "GC가 모자라요." }, "server_time": "..." }
```

`message`는 화면에 그대로 보여 줄 수 있는 쉬운 말로 씁니다.

### 오류 코드

| HTTP | 코드 | 뜻 |
|---|---|---|
| 400 | `INVALID_REQUEST` | 형식 오류, 필수 값 누락 |
| 401 | `UNAUTHORIZED` | 토큰 없음·만료 |
| 403 | `FORBIDDEN` | 권한 없음 (예: 이방인이 주민 전용 기능) |
| 403 | `SUSPENDED` | 이용 정지 |
| 404 | `NOT_FOUND` | 대상 없음 |
| 409 | `CONFLICT` | 상태 충돌 (이미 처리됨, 닉네임 중복) |
| 409 | `IDEMPOTENCY_MISMATCH` | 같은 키로 다른 내용 요청 |
| 422 | `INSUFFICIENT_GC` | 잔액 부족 |
| 422 | `LIMIT_REACHED` | 일일 한도, 보유 한도 |
| 422 | `LOCKED` | 아직 해금되지 않은 기능 |
| 422 | `VALIDATION_FAILED` | 보상 검증 실패 |
| 426 | `UPDATE_REQUIRED` | 클라이언트 버전이 낮음 |
| 429 | `RATE_LIMITED` | 요청이 너무 잦음 |
| 503 | `MAINTENANCE` | 점검 중 |

### 중복 방지 키

- GC·아이템·금융 상태를 바꾸는 모든 `POST`·`PUT` 요청에 필수입니다.
- 같은 키와 같은 내용이 다시 오면 처음 응답을 그대로 돌려줍니다.
- 같은 키에 다른 내용이 오면 `IDEMPOTENCY_MISMATCH`.
- 클라이언트는 응답을 받지 못했을 때 **같은 키로** 다시 보냅니다.

### 요청 제한

운영 서버는 이미 `X-RateLimit-Remaining` 헤더를 내보내고 있습니다. 기존 제한 방식을 확인해 유지하고, 인증·금융 경로에는 더 엄격한 제한을 둡니다.

## 2. 인증

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| POST | `/auth/guest` | 게스트 생성. 플레이어 ID, 게스트 비밀키(이때 한 번만), 접속 토큰 발급 | 1차 |
| POST | `/auth/token` | 플레이어 ID + 게스트 비밀키로 접속 토큰 발급 | 1차 |
| POST | `/auth/logout` | 접속 토큰 폐기 | 1차 |
| POST | `/auth/transfer-code` | 이어하기 코드 발급 | 추후 |
| POST | `/auth/transfer` | 이어하기 코드로 다른 기기에서 접속 | 추후 |
| POST | `/auth/link` | 계정 연결 | 추후 |

- 접속 토큰은 무작위 값이고 서버에는 해시만 저장합니다. 수명은 24시간(초기값)이며 만료되면 `/auth/token`으로 다시 받습니다.
- 게스트 비밀키는 서버에 비밀번호 해시 방식으로 저장합니다.
- `/auth/guest`는 IP당 생성 횟수를 제한합니다.

`POST /auth/guest` 응답 예:

```json
{
  "ok": true,
  "data": {
    "player_id": "p_8f3k2m9x7q1z",
    "guest_secret": "한 번만 전달되는 값",
    "access_token": "…",
    "expires_in": 86400
  },
  "server_time": "…"
}
```

## 3. 시작 데이터

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| GET | `/health` | 상태, 점검 여부, 최소 클라이언트 버전 (운영 중) | 있음 |
| GET | `/me` | 플레이어, 캐릭터, 지갑, 장착, 소속, 해금, 진행 표시를 한 번에 | 1차 |
| GET | `/defs` | 정의 데이터 묶음 (아이템, 적, 타워, 스테이지, 사냥 구역, NPC, 설정)과 버전 | 1차 |
| PUT | `/me/flags` | 진행 표시 갱신 (튜토리얼 단계 등) | 1차 |

`GET /defs`는 버전 값을 함께 주고, 클라이언트가 가진 버전과 같으면 본문 없이 응답합니다.

`GET /me` 응답 예:

```json
{
  "ok": true,
  "data": {
    "player": { "id": "p_8f3k2m9x7q1z", "display_name": "이방인-4821", "residency": "stranger", "region": null },
    "character": { "level": 2, "xp": 35, "xp_next": 141, "hp_max": 110, "appearance": { } },
    "wallet": { "gc": 240 },
    "equipment": { "weapon_melee": 1001, "weapon_ranged": null },
    "unlocks": { "bank": false, "stocks": false, "loans": false, "map": true },
    "flags": { "tutorial_step": 8 },
    "defs_version": "2026111301"
  },
  "server_time": "…"
}
```

## 4. 캐릭터와 방

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| GET | `/character` | 캐릭터 상세 | 1차 |
| PUT | `/character/appearance` | 외형 변경 | 1차 (전입신고의 "다른 모습 보기") |
| PUT | `/character/equipment` | 장착·해제 | 1차 |
| GET | `/room` | 방 배치 | Phase 3 (지금은 `/me` 의 `furniture[]`) |
| PUT | `/room/furniture/{id}` | 가구 하나를 놓거나 옮김 | Phase 3 |
| DELETE | `/room/furniture/{id}/placement` | 가구 하나를 넣어 둠 | Phase 3 |
| POST | `/room/storage/move` | 인벤토리 ↔ 보관함 | 미룸 |

### 4.1 클라이언트가 지금 쓰는 형식 (2026-11-13, 모의 서버 기준)

| 호출 | 대응 경로 | 요청 | 응답 |
|---|---|---|---|
| `PlaceFurniture` | `PUT /room/furniture/{id}` | `x`, `z`(차지할 칸들 가운데 서쪽·남쪽 끝 칸), `rot`(90도씩 돌린 횟수 0~3) | `furniture[]`(가진 가구 전체) |
| `StoreFurniture` | `DELETE /room/furniture/{id}/placement` | — | 위와 같음. 이미 넣어 둔 가구여도 성공 |

가구 한 점: `id`, `item_code`, `placed`, `x`, `z`, `rot`. 넣어 둔 가구의 `x`, `z`, `rot` 은 뜻이 없습니다.

- 처음 설계는 배치 전체를 한 번에 보내는 `PUT /room/furniture` 였습니다. 가구를 하나씩 놓는 조작에 맞춰 하나씩 보내도록 바꿨습니다.
- 놓을 수 있는지는 서버가 검사합니다 ([ROOM_SYSTEM](ROOM_SYSTEM.md) 8.2). 규칙은 `Domain/Room.cs` 의 `RoomRules.CheckPlace` 이고, PHP 서버가 같은 결과를 내야 합니다.
- 놓기와 넣어 두기는 같은 요청을 두 번 보내도 결과가 같으므로 요청 번호를 쓰지 않습니다.
- `/me` 에 추가된 것: `furniture[]`, `equipped_head`, `equipped_body`.

오류 코드: `NOT_FOUND`(없는 가구), `VALIDATION_FAILED`(놓을 수 없는 자리. `message` 에 이유: 방 밖, 다른 가구와 겹침, 비워 둘 자리, 길이 막힘).

## 5. 지역과 전입신고

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| GET | `/regions` | 시·도, 시·군·구 목록과 상태 | 1차 |
| GET | `/regions/{id}` | 지역 상세 | 1차 |
| GET | `/regions/rankings` | 순위 | 1차 |
| GET | `/nicknames/check` | 닉네임 사용 가능 여부 | 1차 |
| GET | `/residency` | 내 소속, 기여도, 이사 가능 시점 | 1차 |
| POST | `/residency` | 전입신고 | 1차 |
| POST | `/residency/move` | 이사 | Phase 2 |

`POST /residency` 요청:

```json
{ "region_id": 118, "nickname": "별빛가디언" }
```

### 5.1 클라이언트가 지금 쓰는 형식 (2026-11-13, 모의 서버 기준)

아래는 `IGameBackend` 와 `LocalMockBackend` 에 구현된 내용입니다 (필드 이름은 이 문서의 표기로 옮겨 적었고, C# 쪽은 `regionCode` 처럼 씁니다). 실제 서버를 만들 때 이 형식에 맞추거나, 운영 서버의 기존 형식에 맞춰 클라이언트를 고칩니다. 지역을 가리키는 값은 지금 **시·군·구 코드 문자열**(`"26350"` = 부산광역시 해운대구)이며, 운영 DB의 `region_id` 와 같은 체계인지는 확인하지 못했습니다 ([MAP_SYSTEM](MAP_SYSTEM.md) 2.3).

| 호출 | 대응 경로 | 응답 |
|---|---|---|
| `GetRegions` | `GET /regions` | `regions[]`: `region_code`, `residents`, `defense_points`. **주민도 방어 포인트도 없는 지역은 넣지 않음** (클라이언트가 0으로 채움). 지역 이름과 도형은 클라이언트에 들어 있음 |
| `CheckNickname` | `GET /nicknames/check` | `nickname`(앞뒤 공백을 뗀 값), `available`, `reason`, `message`. 쓸 수 없는 이름이어도 요청은 성공 |
| `RegisterResidency` | `POST /residency` | `region_code`, `nickname`, `bonus_gc`, `region`(등록 뒤의 지역 상태), `me`(갱신된 `/me`) |

`/me` 에 추가된 것: `residency`(`stranger` / `resident`), `region_code`. 전투 결과(`POST /battles/{id}/finish`)에 추가된 것: `defense_points`, `region_code` (주민이 이겼을 때만 값이 있음).

오류 코드:

| 코드 | 뜻 |
|---|---|
| `ALREADY_RESIDENT` | 이미 주민. 도시를 바꾸려면 이사 |
| `NOT_FOUND` | 없는 지역 |
| `NICKNAME_INVALID` | 닉네임 규칙 위반. `message` 에 이유 |
| `NICKNAME_TAKEN` | 다른 플레이어가 쓰는 이름 |

`GET /regions/{id}`, `GET /regions/rankings`, `GET /residency`, `POST /residency/move` 는 클라이언트에서 아직 쓰지 않습니다.

## 6. 인벤토리와 상점

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| GET | `/inventory` | 인벤토리와 장착 상태 | 1차 |
| POST | `/inventory/use` | 아이템 사용 | 1차 |
| GET | `/shops/{code}` | 상점 진열 | 1차 |
| POST | `/shops/{code}/buy` | 구매 | 1차 |
| POST | `/shops/{code}/sell` | 되팔기 | Phase 3 |

`POST /shops/misoshop/buy` 요청과 응답:

```json
{ "item_code": "wpn_bolt_blaster", "quantity": 1 }
```

```json
{
  "ok": true,
  "data": {
    "wallet": { "gc": 40 },
    "added": [ { "inventory_id": 1002, "item_code": "wpn_bolt_blaster", "quantity": 1 } ]
  },
  "server_time": "…"
}
```

가격은 요청에 넣지 않습니다. 서버가 자기 값으로 계산합니다.

### 6.1 클라이언트가 지금 쓰는 형식 (2026-11-13, 모의 서버 기준)

`IGameBackend` 와 `LocalMockBackend` 에 구현된 내용입니다. 필드 이름은 이 문서의 표기로 옮겨 적었습니다.

| 호출 | 대응 경로 | 요청 | 응답 |
|---|---|---|---|
| `GetShop` | `GET /shops/{code}` | — | `code`, `name`, `items[]`: `item_code`, `price_gc` |
| `BuyItem` | `POST /shops/{code}/buy` | `item_code`, 요청 번호 | `item_code`, `price_gc`, `gc`(새 잔액), `inventory[]`(가방 전체), `furniture[]`(가진 가구 전체), `duplicate` |
| `SellItem` | `POST /shops/{code}/sell` | `item_code`, 요청 번호 | `item_code`, `price_gc`(받은 값), `gc`, `inventory[]`, `furniture[]`, `equipped_melee`, `equipped_ranged`, `equipped_head`, `equipped_body`, `duplicate` |
| `EquipItem` | `PUT /character/equipment` | `slot`(`melee` / `ranged` / `head` / `body`), `item_code`(비우면 해제. 근접 무기 칸은 비울 수 없음) | `equipped_melee`, `equipped_ranged`, `equipped_head`, `equipped_body` |
| `UseItem` | `POST /inventory/use` | `item_code`, 요청 번호 | `item_code`, `heal_hp`, `remaining`, `inventory[]`, `duplicate` |

- 한 번에 하나씩 삽니다 (`quantity` 없음). 응답은 `added` 가 아니라 **가방 전체**를 돌려줍니다.
- 가구는 가방이 아니라 가구 목록(`furniture[]`)에 넣어 둔 상태로 들어갑니다.
- 되팔기: 값은 요청에 넣지 않습니다. 서버가 `구매가 × sell_back_percent / 100`(소수점 버림)으로 계산합니다. 그 물건을 파는 상점에서만, 한 번에 하나씩 팝니다. 입고 있던 물건을 팔면 그 칸이 비어서 옵니다. 가구는 넣어 둔 것만 팔 수 있습니다. 규칙은 [ITEM_SYSTEM](ITEM_SYSTEM.md) 7.3.
- 가방과 장착 상태는 `/me` 에 들어 있습니다 (`inventory[]`, `equipped_melee`, `equipped_ranged`). `GET /inventory` 는 따로 쓰지 않습니다.
- 손에 든 무기 칸은 진행 표시(`active_slot`)로 서버에 남깁니다.
- 사냥 보고 응답에 `drops[]` 와, 드롭이 있었을 때만 `inventory[]` 가 들어 있습니다.
- 요청 번호(중복 방지 키)는 1~64자입니다. 같은 번호를 다시 보내면 처음 한 번만 처리하고 `duplicate: true` 로 답합니다. 같은 번호를 다른 일에 쓰면 `CONFLICT` 입니다. 모의 서버는 최근 50건을 기억합니다.

오류 코드: `NOT_FOUND`(없는 상점·팔지 않는 물건), `INSUFFICIENT_FUNDS`, `ALREADY_OWNED`(장비는 하나만), `INVENTORY_FULL`(가방이 가득 참, 가구를 더 가질 수 없음), `NOT_OWNED`, `CONFLICT`(방에 놓여 있는 가구를 팔려 함), `INVALID_REQUEST`(칸과 맞지 않는 장착, 쓸 수 없는 아이템, 팔 수 없는 물건, 요청 번호 없음).

모의 서버와 1장의 오류 코드 표가 다른 곳 (서버를 만들 때 하나로 맞춥니다): 잔액 부족은 `INSUFFICIENT_FUNDS` (표는 `INSUFFICIENT_GC`), 같은 요청 번호를 다른 일에 쓰면 `CONFLICT` (표는 `IDEMPOTENCY_MISMATCH`), 재전송에는 처음 응답 그대로가 아니라 **지금 상태**에 `duplicate: true` 를 붙여 답합니다.

## 7. 전투

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| POST | `/hunts` | 사냥 세션 시작 (구역) | 1차 |
| POST | `/hunts/{id}/report` | 처치 보고 → 보상 | 1차 |
| POST | `/battles` | 스테이지 전투 시작 | 1차 |
| POST | `/battles/{id}/finish` | 결과 보고 → 보상 | 1차 |
| GET | `/towers` | 내 타워 해금·강화 상태 | 1차 |
| POST | `/towers/{code}/unlock` | 타워 해금 | 1차 |
| POST | `/towers/{code}/upgrade` | 타워 영구 강화 | 미정 (아직 만들지 않음) |

`POST /hunts/{id}/report` 요청과 응답:

```json
{ "kills": { "malfunction_robot": 3, "mech_insect": 5 }, "seq": 4 }
```

```json
{
  "ok": true,
  "data": {
    "accepted": { "malfunction_robot": 3, "mech_insect": 5 },
    "reward": { "gc": 45, "xp": 45, "drops": [ { "item_code": "con_repair_kit", "quantity": 1 } ] },
    "wallet": { "gc": 285 },
    "character": { "level": 2, "xp": 80, "xp_next": 141 },
    "daily": { "hunt_gc": 285, "hunt_gc_limit": 3000 }
  },
  "server_time": "…"
}
```

`seq`는 보고 순번입니다. 서버는 순번이 이어지는지 확인해 같은 보고의 재전송과 빠진 보고를 구분합니다.

### 임무 보상

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| POST | `/quests/{code}/claim` | 임무 보상 받기 | 1차 |

Phase 1 클라이언트를 만들면서 추가한 항목입니다. 첫 구조 임무(`first_rescue`, 100 GC)에 씁니다. 서버는 임무 조건을 스스로 확인하고(첫 로봇 처치가 확정되었는가), 임무마다 한 번만 지급하며, 원장에 기록합니다. 이미 받은 임무를 다시 요청하면 `CONFLICT` 입니다. 현재는 클라이언트의 `LocalMockBackend` 에만 구현되어 있고, 요청·응답 형식은 서버를 만들 때 확정합니다.

### 스테이지 전투 (방어전)

`POST /battles` 요청 `{ "stage_code": "outskirts_1" }` → 응답 `{ "session_id": "b_…", "stage_code": "outskirts_1" }`. 없는 스테이지는 `NOT_FOUND`.

`POST /battles/{id}/finish` 요청:

```json
{ "result": "win", "kills": { "malfunction_robot": 54, "mech_insect": 134, "construction_mecha": 1 }, "base_hp_left": 14, "waves_cleared": 5 }
```

- 걸린 시간은 보내지 않습니다. **서버가 `POST /battles` 시각부터 직접 잽니다.** (처음 계획에는 `duration_sec`가 있었으나, 클라이언트가 보낸 시간을 믿을 이유가 없어 뺐습니다.)
- 금액도 보내지 않습니다. 서버가 스테이지 정의에서 계산합니다.
- 후퇴는 `"result": "lose"`로 보냅니다.

응답:

```json
{
  "ok": true,
  "data": {
    "accepted": true, "result": "win", "waves_cleared": 5, "first_win": true, "reject_reason": null,
    "reward": { "gc": 500, "xp": 150 },
    "wallet": { "gc": 500 },
    "character": { "level": 2, "xp": 50, "xp_next": 141, "hp_max": 110, "levels_gained": 1 },
    "cleared_stages": [ "outskirts_1" ]
  },
  "server_time": "…"
}
```

- 규칙에 맞지 않는 보고는 오류가 아니라 `accepted: false`와 `reject_reason`으로 답하고 보상을 주지 않습니다. 그 전투는 끝난 것으로 기록합니다.
- 끝난 전투를 다시 보고하면 **처음 응답을 그대로** 돌려줍니다 (재전송해도 두 번 지급되지 않고, 결과를 고쳐 보낼 수 없습니다).
- 없는 세션이나 남의 세션은 `NOT_FOUND`.
- `reject_reason` 값: `invalid_result`, `invalid_count`, `enemy_not_in_stage`, `kills_exceed_stage`, `invalid_waves`, `invalid_base_hp`, `finished_too_fast`, `kills_and_base_hp_mismatch`.

검증 규칙은 [COMBAT_SYSTEM](COMBAT_SYSTEM.md) 10.2와 [TOWER_DEFENSE](TOWER_DEFENSE.md) 10장에 있습니다.

### 타워 해금

`POST /towers/{code}/unlock` → 응답 `{ "tower_code": "twr_blast", "cost_gc": 500, "wallet": { "gc": 0 }, "unlocked_towers": [ "twr_machine_gun", "twr_blast" ] }`.

- 값은 서버가 타워 정의에서 정합니다. 원장 사유는 `tower_unlock`.
- GC가 모자라면 `INSUFFICIENT_GC`, 이미 쓸 수 있는 타워면 `CONFLICT`, 없는 타워면 `NOT_FOUND`.
- 쓸 수 있는 타워 목록과 이긴 스테이지 목록은 시작 데이터(`/bootstrap`의 내 정보)에도 들어갑니다 (`unlocked_towers`, `cleared_stages`).

위 전투·해금 API는 현재 클라이언트의 `LocalMockBackend`에만 구현되어 있습니다 (`StartBattle`, `FinishBattle`, `UnlockTower`). JSON 필드 이름은 서버를 만들 때 확정합니다. 모의 서버의 잔액 부족 오류 코드는 `INSUFFICIENT_FUNDS`로 되어 있어, 서버를 만들 때 이 문서의 `INSUFFICIENT_GC`와 하나로 맞춰야 합니다.

## 8. 지갑과 은행

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| GET | `/wallet` | 잔액 | 1차 |
| GET | `/wallet/ledger` | 거래 내역 (쪽 나눔) | 1차 |
| GET | `/bank` | 계좌, 내일 받을 이자, 상품 | 1차 |
| POST | `/bank/deposit` | 입금 | 1차 |
| POST | `/bank/withdraw` | 출금 | 1차 |
| POST | `/bank/accounts` | 정기예금 가입 | Phase 3 |
| POST | `/bank/accounts/{product}/cancel` | 정기예금 중도 해지 | Phase 3 |

`POST /bank/deposit` 요청과 응답:

```json
{ "amount": 500 }
```

```json
{
  "ok": true,
  "data": {
    "wallet": { "gc": 120 },
    "account": { "id": 31, "product": "demand", "balance": 500, "interest_tomorrow": 0 }
  },
  "server_time": "…"
}
```

입금한 날의 최저 잔액은 0이므로 첫날의 "내일 받을 이자"는 0입니다 ([ECONOMY_SYSTEM](ECONOMY_SYSTEM.md) 4.4). 화면에서는 "모레부터 이자가 붙어요"로 안내합니다.

### 8.1 클라이언트가 지금 쓰는 형식 (2026-11-13, 모의 서버 기준)

| 호출 | 대응 경로 | 요청 | 응답 |
|---|---|---|---|
| `GetBank` | `GET /bank` | — | 아래 "은행 상태" |
| `BankDeposit` | `POST /bank/deposit` | `amount`, 요청 번호 | `amount`, `bank`(은행 상태), `duplicate` |
| `BankWithdraw` | `POST /bank/withdraw` | `amount`, 요청 번호 | 위와 같음 |
| `OpenTermDeposit` | `POST /bank/accounts` | `product_code`, `amount`, 요청 번호 | `product_code`, `amount`(원금), `interest`(만기에 받을 이자), `bank`, `duplicate` |
| `CancelTermDeposit` | `POST /bank/accounts/{product}/cancel` | 요청 번호 | `product_code`, `amount`(돌려받은 원금), `interest`(받지 못한 이자), `bank`, `duplicate` |

은행 상태:

| 필드 | 뜻 |
|---|---|
| `unlocked`, `unlock_level` | 쓸 수 있는가, 필요한 레벨. 잠겨 있어도 `GET /bank` 는 성공 |
| `gc`, `bank_gc` | 지갑, 통장 |
| `interest_tomorrow` | 지금대로 두면 다음 자정(한국 시간)에 받을 이자 |
| `interest_when_settled` | 오늘 맡긴 돈까지 이자가 붙기 시작한 뒤의 하루 이자 |
| `daily_interest_bp` | 하루 이율, 1만분의 1 단위 (100 = 1%). 돈 계산에 소수를 쓰지 않기 위해 정수 |
| `interest_cap` | 이자 대상 잔액 한도 |
| `balance_for_one_gc` | 하루 이자가 1 GC가 되는 잔액 (화면 안내용) |
| `interest_total` | 지금까지 받은 이자의 합 |
| `recent[]` | 최근 거래 5건, 새것이 앞: `reason`, `delta`, `balance_after`, `date`(한국 날짜. 이자는 "어느 날의 이자인지") |
| `terms[]` | 정기예금 상품과 내 가입 상태: `code`, `name`, `days`, `rate_bp`(만기 이자의 비율. 1000 = 원금의 10%), `min_amount`, `max_amount`, `open`, `principal`, `interest`(가입할 때 정해진 만기 이자), `matures_date`(한국 날짜), `days_left` |
| `term_gc` | 정기예금에 넣어 둔 원금의 합 |
| `term_matured_count` | 지금까지 만기가 된 정기예금의 수 |

- 입출금은 **지갑 원장과 통장 원장에 한 줄씩**, 같은 요청 번호를 `reference` 로 남깁니다.
- 밀린 이자는 `GET /me`, `GET /bank`, 입출금, 접속 때 서버가 먼저 정산합니다 (예약 작업이 하는 일을 모의 서버는 이렇게 대신합니다). 실제 서버에서 예약 작업을 쓰더라도, 자정 뒤의 첫 입출금 전에 정산이 끝나 있어야 전날 이자가 깎이지 않습니다.
- `/me` 에 추가된 것: `bank_gc`, `bank_interest_total`. 클라이언트는 `bank_interest_total` 과 진행 표시 `bank_interest_seen` 의 차이로 "새로 들어온 이자"를 한 번만 알립니다.
- 정기예금은 **지갑에서** 가입합니다 (지갑 원장 `term_open`). 만기일(한국 날짜)이 되면 정산 때 원금(`term_matured`)과 이자(`term_interest`)가 통장으로 들어옵니다. 중도 해지는 원금만 통장으로 돌아옵니다(`term_cancel`). 이자는 가입할 때 서버가 정해 저장하므로, 그 뒤에 이율을 바꿔도 이미 가입한 예금의 이자는 바뀌지 않습니다. 규칙은 [ECONOMY_SYSTEM](ECONOMY_SYSTEM.md) 10장.
- `/me` 에 추가된 것: `term_gc`, `term_matured_count`. 클라이언트는 `term_matured_count` 와 진행 표시 `term_matured_seen` 의 차이로 만기를 한 번만 알립니다.

오류 코드: `LOCKED`(레벨이 모자람), `INSUFFICIENT_FUNDS`(지갑 또는 통장에 그만큼 없음), `NOT_FOUND`(없는 상품, 가입하지 않은 상품을 해지하려 함), `INVALID_REQUEST`(0 이하의 금액, 한도 초과, 상품의 최소·최대 금액을 벗어남, 요청 번호 없음), `CONFLICT`(같은 요청 번호를 다른 거래에 씀, 이미 가입한 상품에 또 가입하려 함).

## 9. 주식 (Phase 4)

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/stocks` | 기업 목록과 현재가 |
| GET | `/stocks/{code}` | 기업 상세, 뉴스 |
| GET | `/stocks/{code}/chart` | 차트 (`range=day\|week\|month`) |
| POST | `/stocks/{code}/buy` | 매수 (`quantity`, `seen_price`) |
| POST | `/stocks/{code}/sell` | 매도 (`quantity`, `seen_price`) |
| GET | `/stocks/portfolio` | 보유, 평가손익, 수익률 |
| GET | `/stocks/orders` | 거래 기록 |

`seen_price`는 플레이어가 화면에서 본 가격입니다. 서버 현재가와 다르면 체결하지 않고 `CONFLICT`와 새 가격을 돌려줍니다.

## 10. 대출과 신용 (Phase 4)

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/credit` | 신용점수, 구간, 이력 |
| GET | `/loans/offer` | 가능한 한도, 이율, 기한 |
| POST | `/loans` | 대출 신청 (`amount`) |
| GET | `/loans/current` | 내 대출 |
| POST | `/loans/{id}/repay` | 상환 (`amount` 또는 `all`) |

## 11. 커뮤니티 (Phase 5)

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET / POST | `/friends` | 목록 / 요청 |
| PUT / DELETE | `/friends/{id}` | 수락·거절 / 삭제 |
| GET / POST | `/blocks` | 목록 / 차단 |
| DELETE | `/blocks/{id}` | 차단 해제 |
| POST | `/reports` | 신고 |
| GET | `/chat/policy` | 적용되는 채팅 단계, 문구 목록 |
| POST | `/chat/ticket` | 실시간 서비스 입장권 |
| GET | `/notices/city` | 도시 공지 |

## 12. 공지

| 메서드 | 경로 | 내용 | 시점 |
|---|---|---|---|
| GET | `/notices` | 게임 안에 보여 줄 공지 (홈페이지 공지사항과 같은 원본) | 1차 |

## 13. 서버 구현 규칙

- 모든 입력을 검증합니다. 숫자의 범위, 문자열 길이, 허용 값.
- SQL은 준비된 문장(바인딩)만 씁니다.
- 플레이어는 자기 데이터에만 접근합니다. 경로의 ID가 자기 것인지 항상 확인합니다.
- 오류 응답에 내부 정보(SQL, 파일 경로, 스택)를 넣지 않습니다.
- 금액, 보상, 가격, 확률은 클라이언트 값을 믿지 않습니다.
- 점검 중(`maintenance`)에는 `/health` 외 모든 경로가 `MAINTENANCE`를 돌려줍니다.
- 관리자 기능은 이 API에 넣지 않고 별도 경로와 별도 인증을 씁니다 ([ADMIN_SYSTEM](ADMIN_SYSTEM.md)).
