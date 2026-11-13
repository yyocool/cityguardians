# API_SPEC — PHP API 명세

> 기준 v10.0 · 2026-11-14 · 상태: **1~3장의 인증·시작 데이터, 5장의 지역·전입신고, 14~17장(전투·디펜스·헌트·FPS·도시), 18장(CITY PASS·장학), 19~22장(v9.0 도시 성장·지방세·참여예산·협동 방어전), 23장(v10.0 실시간 대시보드 요약)은 PHP 서버(`server/`)에 구현**되어 로컬 테스트 DB에서 테스트를 통과함. 운영 서버에는 배포하지 않음. 4·6·8~12장(방, 가방·상점, 은행, 주식, 대출, 커뮤니티, 공지)은 v6.0의 설계 그대로이고 서버에 없음. 클라이언트의 모의 서버(`LocalMockBackend`)가 쓰는 형식은 4.1, 5.1, 6.1, 7장, 8.1에 적음

## 0. 먼저 읽을 것

### v8.0 서버가 실제로 쓰는 형식 (구현된 것)

아래는 구현된 서버의 동작입니다. 1~13장의 v6.0 설계와 다른 곳은 **이 절이 맞습니다.**

| 항목 | 구현 |
|---|---|
| 기본 경로 | `{설치 경로}/api/v1`. 프런트 컨트롤러는 `server/public/index.php` 하나 |
| `GET /health` | `{ status, version: "10.0.0", environment, maintenance, min_client_version }`. 인증 없음. DB에 닿지 않으면 `503` |
| 이름 | 요청과 응답 모두 `snake_case`. 서버가 받은 이름을 안에서 쓰는 이름으로 바꿉니다 |
| 응답 | `{ "ok": true, "data": {…}, "server_time": "…" }` / `{ "ok": false, "error": { "code", "message", "reason"? }, "server_time": "…" }` |
| `reason` | 오류의 자세한 이유 코드 (`level_too_low`, `not_owned`, `in_battle_upgrade` …). 없을 수도 있습니다. 화면에는 `message`를 보여 주고, 분기에는 `code`와 `reason`을 씁니다 |
| 시각 | ISO 8601 **UTC** (`2026-11-13T03:00:00Z`). 설계의 `+09:00` 표기가 아닙니다. 하루 한도의 날짜 경계만 한국 시간입니다 |
| 응답의 모양 | **평평합니다.** `gc`, `level`, `xp`가 `data` 바로 아래에 있습니다. 3장·7장 예시의 `wallet: { gc }`, `character: { … }` 같은 묶음이 아닙니다 (클라이언트 DTO와 같은 모양) |
| 처치 수 | `"kills": [ { "enemy_code": "malfunction_robot", "count": 3 } ]` — 목록. 7장 예시의 `{ "이름": 수 }` 묶음이 아닙니다. 최대 32줄 |
| 검증에 걸린 전투 보고 | 오류가 아니라 `200`에 `accepted: false`와 `reject_reason`. 형식이 틀린 보고(숫자가 문자열 등)만 `400` |
| 중복 방지 키 | `Idempotency-Key` 헤더, 8~64자의 영문·숫자·`_`·`-`. **GC가 바뀌는 구매·강화·복구·기금 요청에 필수.** 전투 보고는 세션 번호와 순번으로 재전송을 가리므로 키가 필요 없습니다. 다시 온 요청의 응답에는 `duplicate: true`가 붙습니다 |
| 오류 코드 | 1장의 표 그대로이고, 내부 오류 `500 INTERNAL_ERROR`와 v7.0의 `403 FEATURE_DISABLED`(운영 승인 전이라 꺼져 있는 현실 혜택 기능. `reason`에 기능 코드)가 더 있습니다. 경로는 맞는데 방식이 틀리면 `400 INVALID_REQUEST`, 없는 경로는 `404 NOT_FOUND` |
| 요청 제한 | `POST /auth/guest`는 IP당 시간에 20번, `POST /auth/token`은 IP당 10분에 30번 (`.env`로 조정). 넘으면 `429 RATE_LIMITED`. **전투·도시 경로에는 제한이 없고 `X-RateLimit-*` 헤더도 내보내지 않습니다.** v7.0 경로의 제한은 18장의 두 문서에 있습니다 |
| 점검, 버전 | `.env`의 `MAINTENANCE=1`이면 `/health`를 뺀 모든 경로가 `503 MAINTENANCE`. `MIN_CLIENT_VERSION`보다 낮은 `X-Client-Version`은 `426 UPDATE_REQUIRED` |
| 응답 헤더 | `Content-Type: application/json; charset=utf-8`, `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` |
| 구현되지 않은 것 | `PUT /me/flags`, `/auth/transfer*`, `/auth/link`, `/quests`, 4·6·8~12장 전부 |

**Unity 클라이언트는 아직 이 서버에 붙지 않습니다.** `IGameBackend`의 구현은 `LocalMockBackend`뿐이고 HTTP 구현이 없습니다. 서버의 응답은 클라이언트 DTO와 필드가 같도록 만들었지만(이름만 `snake_case`), 실제로 이어 붙여 확인한 적은 없습니다.

**v9.0 경로(19~21장)와 v10.0의 요약(23장)은 응답의 모양을 테스트로 묶어 두었습니다.** 서버 테스트(`server/tests/ClientWireTest.php`)가 실제 응답에서 "어떤 이름에 어떤 종류의 값이 오는가"를 뽑아 `server/tests/fixtures/client-wire.json`에 두고, 클라이언트 쪽 테스트(`tools/check/rules/WireContractTests.cs`)가 DTO의 모든 필드가 그 안에 같은 종류로 있는지 확인합니다. 서버의 응답을 바꾸면 한쪽 테스트가 실패합니다. v8.0·v7.0 경로에는 이 장치가 없습니다.

### 운영 서버의 기존 API

운영 서버에 `/api/v1`이 이미 있습니다 (`GET /api/v1/health` 응답: 버전 1.0.0, 환경 production). v1의 다른 경로와 요청·응답 형식은 서버 소스를 확인하지 못해 알 수 없습니다. 이 문서는 v6.0의 목표 명세이며, 기존 v1 경로를 확인한 뒤 겹치는 기능(게스트, 캐릭터, 전입신고, 전투 보상, 은행)은 기존 형식과 맞춰 조정합니다.

공개된 게임 빌드가 아직 없으므로(다운로드 페이지 "준비 중"), 첫 빌드 공개 전까지는 `/api/v1` 안에서 형식을 바꿀 수 있습니다. 첫 공개 뒤에는 호환을 깨는 변경을 `/api/v2`로 냅니다.

**v8.0 서버(`server/`)는 운영 서버의 기존 `/api/v1`을 대체하도록 배포하지 않았습니다.** 기존 v1의 소스와 DB 구조를 여전히 확인하지 못했기 때문입니다. 같은 `/api/v1/health` 경로를 쓰고 있어 그대로 올리면 기존 것을 덮어쓰게 됩니다. 배포 방법(다른 경로에 나란히 두기, 기존 것과 합치기)은 기존 서버를 확인하고 승인을 받은 뒤에 정합니다 ([SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md)).

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

> v8.0: 이 장은 v6.0의 초안이고 모의 서버의 형식을 설명합니다. **PHP 서버에 구현된 경로와 형식은 14~16장**입니다 (`/hunts` → `/hunt/sessions`, `/battles` → `/defense/battles`, `/towers/{code}/unlock` → `/defense/structures/{code}/purchase`). `/quests/{code}/claim`은 서버에 없습니다.

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

v8.0 서버가 여기에 더해 지키는 것:

- **위험도 숫자를 받는 경로가 없습니다.** 위험도는 서버가 검증한 전투 결과, 복구·기금, 시간의 흐름(침공 판정)으로만 바뀝니다.
- **가격·보상 금액을 받는 경로가 없습니다.** 도시 기금의 `amount_gc`만 플레이어가 고르고, 범위·한도·잔액은 서버가 검사합니다.
- 경로의 `{code}`, `{id}`는 영문·숫자·`_`·`-` 64자까지만 받습니다.
- 숫자는 JSON 숫자(정수)만 받습니다. 문자열로 온 숫자, 소수는 `400`입니다.
- 내부 오류는 `500 INTERNAL_ERROR`와 "잠시 뒤에 다시 시도해 주세요."만 돌려주고, 자세한 내용은 `system_logs`에 남깁니다.

---

아래 14~17장은 **구현된** v8.0 API입니다. 모든 경로는 인증(`Authorization: Bearer`)이 필요합니다. 예시는 로컬 테스트 서버의 실제 응답에서 `data`만 옮긴 것입니다.

## 14. 공통 전투 (v8.0)

| 메서드 | 경로 | 내용 | 키 |
|---|---|---|---|
| GET | `/combat/modes` | 전투 모드 목록과 모드별 장착 | |
| GET | `/combat/equipment` | 장비 목록 (정의 + 내 보유·단계·살 수 있는지) | |
| POST | `/combat/equipment/{code}/purchase` | 장비 구매 | 필수 |
| POST | `/combat/equipment/{code}/upgrade` | 장비 한 단계 강화 | 필수 |
| PUT | `/combat/loadouts` | 장착 `{ mode, slot, gear_code }`. `gear_code`를 비우면 기본으로 되돌림 | |
| POST | `/combat/sessions` | 전투 시작 `{ mode, code }` — 모드별 시작 경로와 같은 일 | |
| POST | `/combat/sessions/{id}/result` | 전투 결과 보고 — 기록 번호의 머리글자(`b_`, `h_`, `f_`)로 모드를 가림 | |
| GET | `/combat/overview` | 모드, 가진 장비, 장착, 오늘의 한도 사용량을 한 번에 | |
| GET | `/combat/rewards` | 보상 조회: 모드별 합계, 최근 내역, 오늘의 한도 | |

"키"는 `Idempotency-Key` 헤더입니다.

### 모드와 장비

`GET /combat/modes`:

```json
{
  "modes": [
    {
      "code": "fps", "name": "FPS 모드", "summary": "시내로 들어온 로봇을 1인칭으로 찾아 물리치고 시설과 시민을 지켜요.",
      "camera": "first_person", "controls": "WASD 이동   마우스 시점   좌클릭 발사   우클릭 조준   R 재장전   E 상호작용",
      "closed": false, "sort_order": 3,
      "equipped": [
        { "slot": "primary", "gear_code": "gear_energy_blaster", "level": 1 },
        { "slot": "gadget", "gear_code": "gear_heal_device", "level": 1 },
        { "slot": "support", "gear_code": null, "level": 0 }
      ]
    }
  ]
}
```

- `closed`가 `true`인 모드는 시작이 `422 LOCKED` (`mode_closed`)입니다.
- `equipped`는 고른 것이 없으면 그 칸의 무료 기본 장비입니다. 무료 기본 장비가 없는 칸은 `null`입니다.

`GET /combat/equipment`의 장비 한 줄:

```json
{
  "code": "gear_energy_blaster", "name": "에너지 블래스터", "description": "…",
  "slot": "primary", "kind": "blaster", "modes": ["fps"],
  "price_gc": 0, "unlock_level": 1, "max_level": 3, "upgrade_base_gc": 150, "upkeep_gc": 0,
  "damage": 12, "attacks_per_second": 5, "range": 30, "projectile_speed": 60, "clip_size": 24, "reload_seconds": 1.4,
  "aim_zoom": 0.8, "cone_degrees": 0, "knockback": 0,
  "cooldown_seconds": 0, "effect_seconds": 0, "effect_radius": 0, "effect_value": 0,
  "coming_soon": false,
  "free": true, "owned": true, "level": 1, "level_damage": 12, "level_effect_value": 0,
  "buy_blocked": null, "upgrade_cost_gc": 150, "upgrade_blocked": null
}
```

응답 전체는 `{ "equipment": [ … ], "gc": 500, "loadouts": [ … ] }`입니다. `buy_blocked` / `upgrade_blocked`는 지금 살 수 없는·강화할 수 없는 이유 코드이고, 할 수 있으면 `null`입니다.

구매·강화·장착의 응답은 같은 모양입니다.

```json
{ "gear_code": "gear_sensor", "level": 1, "cost_gc": 300, "gc": 200,
  "gear": [ { "code": "gear_sensor", "level": 1 } ],
  "loadouts": [ { "mode": "fps", "slot": "support", "gear_code": "gear_sensor" } ],
  "duplicate": false }
```

| 상황 | 응답 |
|---|---|
| 없는 장비 | `404 NOT_FOUND` |
| 준비 중, 레벨 부족 | `422 LOCKED` (`coming_soon`, `level_too_low`) |
| GC 부족 | `422 INSUFFICIENT_GC` |
| 이미 가짐, 최대 단계, 강화할 수 없는 장비 | `409 CONFLICT` |
| 가지지 않은 장비를 강화·장착, 칸이나 모드가 맞지 않음 | `422 VALIDATION_FAILED` (`not_owned`, `wrong_slot`, `wrong_mode`) |
| 없는 모드·칸 이름 | `400 INVALID_REQUEST` |
| 키가 없거나 형식이 틀림 | `400 INVALID_REQUEST` |
| 같은 키를 다른 장비에 씀 | `409 IDEMPOTENCY_MISMATCH` |

### 보상 조회

`GET /combat/rewards`:

```json
{
  "gc": 1100,
  "totals": [
    { "mode": "hunt", "total_gc": 100, "count": 1 },
    { "mode": "defense", "total_gc": 0, "count": 0 },
    { "mode": "fps", "total_gc": 500, "count": 1 }
  ],
  "recent": [
    { "mode": "fps", "reason": "fps_reward", "gc": 500, "balance_after": 1100, "at": "2026-11-13T03:03:10Z" },
    { "mode": "hunt", "reason": "hunt_reward", "gc": 100, "balance_after": 600, "at": "2026-11-13T03:02:00Z" }
  ],
  "daily_hunt_gc": 100, "daily_hunt_gc_limit": 3000,
  "fps_rewards_today": 1, "fps_reward_limit": 5,
  "risk_reduced_today": 5, "risk_reduce_cap": 20,
  "city_contribution": 13
}
```

세 모드의 보상이 한 원장에서 나옵니다. `GET /combat/overview`는 `{ modes, gear, loadouts, cleared_fps_missions, fps_rewards_today, fps_reward_limit, risk_reduced_today, risk_reduce_cap }`입니다.

### 전투 응답에 딸려 오는 `city`

방어전·사냥·FPS의 결과, 시설 복구, 도시 기금의 응답에는 도시에 반영된 내용이 들어 있습니다. 반영된 것이 없으면(이방인, 패배, 사냥 10마리 미만) `null`입니다.

```json
{
  "region_code": "11110", "actor": "player", "reason": "hunt_completed",
  "risk_before": 24, "risk_after": 23, "risk_delta_centi": -100,
  "stage_before": "safe", "stage": "safe",
  "contribution": 1,
  "invasion_id": null, "invasion_kind": null, "invasion_progress": 0, "invasion_required": 0, "invasion_repelled": false,
  "capped": false
}
```

| 필드 | 뜻 |
|---|---|
| `actor` | `player`(소속 가디언) / `supporter`(지원 나온 가디언) |
| `reason` | 위험도 사유 코드 ([CITY_RISK_SYSTEM](CITY_RISK_SYSTEM.md) 3장). 기여도만 쌓였으면 `null` |
| `risk_before`, `risk_after` | 0~100 |
| `risk_delta_centi` | 이번에 바뀐 양 (1/100 단위) |
| `stage_before`, `stage` | `safe` / `caution` / `danger` / `emergency` |
| `contribution` | 이번에 얻은 기여도 (지원 중이면 지원 기여도) |
| `invasion_*` | 이 승리가 침공을 막는 데 쓰였을 때. `invasion_repelled`가 `true`면 이 승리로 침공이 끝남 |
| `capped` | 오늘 내릴 수 있는 위험도 한도에 걸렸는가 |

## 15. 디펜스와 헌트 (v8.0)

### 디펜스

| 메서드 | 경로 | 내용 | 키 |
|---|---|---|---|
| GET | `/defense/zones` | 방어 구역 목록, 지금 막을 수 있는 침공, 쓸 수 있는 시설 | |
| GET | `/defense/structures` | 타워와 방어시설 15종 (정의 + 쓸 수 있는지·해금했는지·도시 것인지) | |
| POST | `/defense/structures/{code}/purchase` | 영구 해금 | 필수 |
| POST | `/defense/structures/{code}/upgrade` | 도시 공용 시설에 기금 `{ amount_gc }`. 개인 타워는 `422` (`in_battle_upgrade`) | 필수 |
| POST | `/defense/battles` | 방어전 시작 `{ zone_code }` | |
| POST | `/defense/battles/{id}/finish` | 결과 보고 | |

`GET /defense/zones`:

```json
{
  "zones": [ {
    "code": "outskirts_1", "name": "외곽 1구역", "base_hp": 20, "start_energy": 200, "wave_clear_energy": 50, "rest_seconds": 10,
    "waves": [ { "groups": [ { "enemy_code": "malfunction_robot", "count": 6, "interval": 2.5, "delay": 0 } ] } ],
    "win_gc": 300, "first_win_bonus_gc": 200, "win_xp": 150, "lose_gc_per_wave": 20, "min_seconds_per_wave": 10,
    "win_defense_points": 100, "unlock_level": 1,
    "cleared": false, "locked": false, "wave_count": 5, "enemy_count": 195
  } ],
  "region_code": "11110", "invasion_id": null, "invasion_kind": null, "invasion_severity": 0,
  "usable_structures": ["twr_machine_gun"]
}
```

`GET /defense/structures`의 한 줄은 [DEFENSE_MODE](DEFENSE_MODE.md) 3.2의 속성에 `coming_soon`, `usable`, `owned_permanently`, `city_owned`가 붙습니다. 응답 전체는 `{ structures, usable, gc }`.

`POST /defense/structures/{code}/purchase` → `{ "tower_code": "twr_blast", "cost_gc": 500, "gc": 0, "unlocked_towers": ["twr_machine_gun", "twr_blast"] }`. 없는 시설 `404`, 준비 중·도시 공용·레벨 부족 `422 LOCKED`, 이미 쓸 수 있음 `409 CONFLICT`, GC 부족 `422 INSUFFICIENT_GC`.

`POST /defense/battles` 요청 `{ "zone_code": "outskirts_1" }` → 응답:

```json
{ "session_id": "b_kyemepf3rrfn4b", "stage_code": "outskirts_1", "region_code": "11110", "invasion_id": "inv_1",
  "usable_structures": ["twr_machine_gun"] }
```

`POST /defense/battles/{id}/finish` 요청:

```json
{ "result": "win", "kills": [ { "enemy_code": "malfunction_robot", "count": 60 }, { "enemy_code": "mech_insect", "count": 134 },
  { "enemy_code": "construction_mecha", "count": 1 } ], "base_hp_left": 20, "waves_cleared": 5 }
```

응답 (검증에 걸린 보고의 예):

```json
{ "accepted": false, "result": "win", "waves_cleared": 5, "reward_gc": 0, "reward_xp": 0, "first_win": false,
  "gc": 500, "level": 1, "xp": 0, "xp_next": 50, "hp_max": 100, "levels_gained": 0,
  "reject_reason": "kills_exceed_stage", "cleared_stages": [], "defense_points": 0, "region_code": null, "city": null }
```

- 인정된 승리에는 `accepted: true`, 보상, `first_win`, `defense_points`, `region_code`, `city`(14장)가 채워집니다.
- `reject_reason` 값은 7장의 목록과 같습니다.
- 끝난 전투를 다시 보고하면 처음 응답 그대로. 없는 전투·남의 전투는 `404`.

### 헌트

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/hunt/zones` | 사냥 구역 목록과 소속 도시의 위험 단계 |
| POST | `/hunt/sessions` | 사냥 시작 `{ zone_code }` |
| POST | `/hunt/sessions/{id}/report` | 처치 보고 `{ seq, kills }` |

`GET /hunt/zones` → `{ "zones": [ { "code": "zone_ne", "name": "북동 전투 구역", "enemy_codes": ["malfunction_robot"], "max_alive": 5, "respawn_seconds": 8, "max_kills_per_minute": 24, "burst_allowance": 5 } ], "region_code": "11110", "stage": "safe", "special": false, "risk_reduce_percent": 100 }`

- `special`이 `true`면 위험 단계 이상의 특별 사냥 임무이고 `risk_reduce_percent`가 200(초기값)입니다 ([HUNT_MODE](HUNT_MODE.md) 4장).

`POST /hunt/sessions` → `{ "session_id": "h_3kjjefjjadscuy", "zone_code": "zone_ne", "region_code": "11110", "stage": "safe", "special": false, "risk_reduce_percent": 100 }`

`POST /hunt/sessions/{id}/report` 요청 `{ "seq": 1, "kills": [ { "enemy_code": "malfunction_robot", "count": 10 } ] }` → 응답:

```json
{
  "accepted": [ { "enemy_code": "malfunction_robot", "count": 10 } ],
  "reward_gc": 100, "reward_xp": 100,
  "gc": 600, "level": 2, "xp": 50, "xp_next": 141, "hp_max": 110, "levels_gained": 1,
  "daily_hunt_gc": 100, "daily_hunt_gc_limit": 3000,
  "reject_reason": null,
  "drops": [], "inventory": null,
  "city": { "region_code": "11110", "actor": "player", "reason": "hunt_completed", "risk_before": 24, "risk_after": 23, "…": "…" }
}
```

- `seq`는 1부터 하나씩 늘립니다. 바로 앞 순번을 다시 보내면 그때의 응답을 그대로 돌려주고, 그 밖의 순번은 `409 CONFLICT`입니다.
- `accepted`는 서버가 인정한 처치입니다. 보낸 것보다 적을 수 있습니다 (분당 한도, 구역에 나오지 않는 적).
- `drops`와 `inventory`는 자리만 있습니다. **서버에 아이템 드롭이 아직 없습니다.**

## 16. FPS (v8.0)

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/fps/invasions` | 지금 출동할 수 있는 시내 침입과 나설 수 있는 임무 |
| GET | `/fps/missions` | 임무 목록 (정의 + 이긴 적 있는지·잠겼는지)과 오늘의 보상 횟수 |
| POST | `/fps/missions/{code}/start` | 임무 시작 |
| POST | `/fps/sessions/{id}/finish` | 결과 보고 |

`GET /fps/invasions`:

```json
{ "region_code": "11110",
  "invasion": { "id": "inv_7", "region_code": "11110", "kind": "intrusion", "severity": 2, "progress": 0, "required": 2,
                "ends_at": "2026-11-13T06:00:00Z" },
  "available_missions": ["downtown_1"] }
```

`invasion`은 내가 지키는 도시(소속 또는 지원 나간 도시)에 시내 침입이나 ZERO 이벤트가 진행 중일 때만 채워집니다. 없어도 순찰 임무로 나설 수 있습니다.

`GET /fps/missions`:

```json
{
  "missions": [ {
    "code": "downtown_1", "name": "도심 상가 구역", "description": "…",
    "groups": [ { "enemy_code": "malfunction_robot", "count": 4, "interval": 3, "delay": 3 } ],
    "facility_count": 3, "facility_hp": 6, "civilian_count": 4, "time_limit_seconds": 300, "min_seconds": 60,
    "win_gc": 250, "first_win_bonus_gc": 150, "win_xp": 120, "civilian_bonus_gc": 10, "facility_bonus_gc": 20,
    "win_defense_points": 80, "unlock_level": 1,
    "cleared": false, "locked": false, "enemy_count": 18
  } ],
  "fps_rewards_today": 0, "fps_reward_limit": 5
}
```

`POST /fps/missions/downtown_1/start` → `{ "session_id": "f_cefxw5k7vdnvqr", "mission_code": "downtown_1", "invasion_id": null, "region_code": "11110" }`. 없는 임무 `404`, 레벨 부족·모드 닫힘 `422 LOCKED`.

`POST /fps/sessions/{id}/finish` 요청:

```json
{ "result": "win", "kills": [ { "enemy_code": "malfunction_robot", "count": 8 }, { "enemy_code": "mech_insect", "count": 10 } ],
  "facilities_saved": 3, "civilians_saved": 4 }
```

응답:

```json
{
  "accepted": true, "result": "win",
  "reward_gc": 500, "reward_xp": 120, "first_win": true, "reward_limited": false,
  "gc": 1100, "level": 3, "xp": 29, "xp_next": 260, "hp_max": 120, "levels_gained": 1,
  "reject_reason": null,
  "facilities_saved": 3, "civilians_saved": 4,
  "cleared_fps_missions": ["downtown_1"],
  "defense_points": 80,
  "fps_rewards_today": 1, "fps_reward_limit": 5,
  "city": { "region_code": "11110", "actor": "player", "reason": "fps_repelled", "risk_before": 23, "risk_after": 19,
            "risk_delta_centi": -400, "contribution": 12, "…": "…" }
}
```

- `reward_limited: true`면 오늘의 보상 횟수를 다 써서 GC가 0입니다. 경험치와 도시 반영은 그대로입니다.
- `reject_reason` 값과 검증 규칙은 [FPS_MODE](FPS_MODE.md) 7장.
- 끝난 임무를 다시 보고하면 처음 응답 그대로. 없는 임무·남의 임무는 `404`.

## 17. 도시: 위험도, 침공, 랭킹, 지원 (v8.0)

| 메서드 | 경로 | 내용 | 키 |
|---|---|---|---|
| GET | `/cities/risk` | 전국 230개 도시의 위험도 현황 (지도용) | |
| GET | `/cities/{code}` | 도시 상세 | |
| GET | `/cities/{code}/history` | 위험도 변경 이력 `?limit=50&before=<seq>` | |
| GET | `/cities/invasions` | 침공 현황 (진행 중, 최근, 우리 도시) | |
| GET | `/cities/rankings` | 지역 랭킹 | |
| GET | `/cities/support` | 내가 볼 수 있는 지원 요청 | |
| POST | `/cities/support/{id}/join` | 지원에 나서기 | |
| POST | `/cities/support/leave` | 지원 그만두기 | |
| GET | `/cities/support/contributions` | 내 지원 기여도 | |
| POST | `/cities/facilities/{code}/repair` | 주요 시설 복구 | 필수 |
| POST | `/cities/structures/{code}/fund` | 도시 공용 방어시설 기금 `{ amount_gc }` (100 GC 단위) | 필수 |

지시서의 "전국 위험도 / 도시 상세 / 위험도 이력 / 침공 현황 / 지역 랭킹 / 지원 요청 / 지원 기여도 조회"가 위의 GET 일곱 개입니다. **위험도를 바꾸는 경로는 없습니다.**

### 전국 위험도

`GET /cities/risk`:

```json
{
  "server_time": "2026-11-13T03:03:10Z",
  "cities": [ {
    "region_code": "11110", "risk": 19, "stage": "safe",
    "residents": 1, "active_players": 1,
    "defense_points": 80, "defense_power": 108,
    "invasion_kind": "outskirts", "invasion_severity": 1, "invasion_progress": 0, "invasion_required": 1,
    "invasion_ends_at": "2026-11-13T06:00:00Z",
    "recent_invasions": 1,
    "contribution": 13, "rank": 1, "score": 5010, "player_defended": true,
    "support_request_id": null
  } ],
  "home_region_code": "11110", "support_region_code": null
}
```

| 필드 | 뜻 |
|---|---|
| `risk`, `stage` | 위험도 0~100과 단계. 화면은 단계를 색 + 모양 + 글자로 함께 표시합니다 |
| `defense_power` | 도시 방어력 (기본 수비대 + 방어 포인트 + 도시 공용 시설) |
| `invasion_*` | 진행 중인 침공. 없으면 `invasion_kind`가 `null` |
| `recent_invasions` | 최근 7일의 침공 수 |
| `rank`, `score` | 지역 랭킹 순위와 총점 (0~10000) |
| `player_defended` | 활성 가디언이 있는 도시인가. `false`면 NPC 수비대만 지키는 도시 |
| `support_request_id` | 지원 요청이 열려 있으면 그 번호 |

도시 이름은 `GET /regions`의 목록에서 `region_code`로 찾습니다.

### 도시 상세와 이력

`GET /cities/11110`:

```json
{
  "status": { "region_code": "11110", "risk": 19, "…": "전국 위험도의 한 줄과 같음" },
  "mood": "도시가 평화로워요.",
  "score": { "region_code": "11110", "rank": 1, "total": 5010, "safety": 81, "success_rate": 66, "contribution": 13,
             "response": 50, "cooperation": 0, "player_defended": true },
  "history": [ { "seq": 3, "region_code": "11110", "at": "2026-11-13T03:03:10Z", "reason": "fps_repelled", "actor": "player",
                 "delta_centi": -400, "after_centi": 1900, "stage": "safe", "reference": "f_cefxw5k7vdnvqr" } ],
  "invasions": [ { "id": "inv_1", "region_code": "11110", "kind": "outskirts", "severity": 1, "status": "active",
                   "started_at": "2026-11-13T03:00:00Z", "ends_at": "2026-11-13T06:00:00Z", "resolved_at": null,
                   "required": 1, "progress": 0 } ],
  "facilities": [ { "code": "fac_power", "name": "발전소", "damage": 0, "max_damage": 3, "repair_gc": 150 } ],
  "structures": [ { "code": "twr_command_center", "name": "지휘 센터", "level": 0, "max_level": 3, "funded_gc": 300,
                    "goal_gc": 8000, "power": 60 } ],
  "support": null,
  "my_contribution": 13,
  "is_home": true
}
```

`GET /cities/11110/history` → `{ "region_code", "risk", "stage", "entries": [ … ], "next_before": null }`. `entries`는 최근 것부터이고, `next_before`가 `null`이 아니면 그 값을 `before`에 넣어 다음 쪽을 받습니다. 이력의 `actor`는 `player` / `supporter` / `npc` / `system` / `admin`입니다. 이력에는 플레이어의 이름이나 ID가 나오지 않습니다 (`reference`는 전투 기록 번호).

없는 지역 코드는 `404`.

### 침공 현황과 랭킹

`GET /cities/invasions` → `{ "server_time", "active": [ 침공 … ], "recent": [ 끝난 침공 … ], "home": 우리 도시의 침공 또는 null, "home_region_code" }`. 침공의 `kind`는 `outskirts` / `intrusion` / `zero`, `status`는 `active` / `repelled`(가디언이 막음) / `npc_held`(NPC 수비대가 막음) / `failed`.

`GET /cities/rankings`:

```json
{
  "rows": [ { "region_code": "11110", "rank": 1, "score": 5010, "safety": 81, "success_rate": 66, "contribution": 13,
              "response": 50, "cooperation": 0, "player_defended": true, "risk": 19, "stage": "safe", "active_players": 1 } ],
  "total_cities": 230, "player_cities": 1,
  "home": { "region_code": "11110", "rank": 1, "…": "…" }
}
```

점수의 계산은 [REGIONAL_COMPETITION](REGIONAL_COMPETITION.md) 2장.

### 도시 간 지원

`GET /cities/support`:

```json
{
  "requests": [ { "request_id": "sup_1", "region_code": "11140", "risk": 100, "stage": "emergency",
                  "closes_at": "2026-11-13T09:03:10Z", "wins": 0, "points": 0, "can_join": true, "reason": null } ],
  "joined_request_id": null, "joined_region_code": null, "my_support_points": 0
}
```

- 같은 시·도의 요청만 보입니다. 이방인에게는 목록이 비어 있습니다.
- `can_join`이 `false`면 `reason`에 이유가 있습니다 (`own_city`).

`POST /cities/support/sup_1/join` → `{ "joined_request_id": "sup_1", "joined_region_code": "11140", "home_region_code": "11110" }`. **`home_region_code`는 바뀌지 않습니다.**

| 상황 | 응답 |
|---|---|
| 이방인 | `422 LOCKED` (`not_resident`) |
| 우리 도시의 요청, 다른 시·도 | `422 VALIDATION_FAILED` (`own_city`, `too_far`) |
| 이미 닫힌 요청 | `409 CONFLICT` (`not_open`) |
| 없는 요청 | `404 NOT_FOUND` |

`POST /cities/support/leave` → `{ "joined_request_id": null, "joined_region_code": null, "home_region_code": "11110" }`

`GET /cities/support/contributions` → `{ "contributions": [ { "request_id", "region_code", "status", "wins", "points", "joined_at" } ], "total_points": 0, "home_region_code": "11110" }`

### 복구와 기금

`POST /cities/facilities/fac_power/repair` (본문 없음), `POST /cities/structures/twr_command_center/fund` 요청 `{ "amount_gc": 300 }` → 응답:

```json
{ "cost_gc": 300, "gc": 800, "contribution": 6,
  "city": { "region_code": "11110", "actor": "player", "reason": null, "risk_before": 19, "risk_after": 19, "…": "…" },
  "detail": { "…": "도시 상세와 같음" } }
```

| 상황 | 응답 |
|---|---|
| 이방인 | `422 LOCKED` |
| 없는 시설, 도시 공용이 아닌 시설 | `404 NOT_FOUND` |
| 고칠 곳이 없음, 이미 다 지은 시설 | `409 CONFLICT` |
| 하루 한도 (복구 3번, 기금 2,000 GC) | `422 LIMIT_REACHED` |
| 100 GC 단위가 아닌 금액 | `400 INVALID_REQUEST` |
| GC 부족 | `422 INSUFFICIENT_GC` |

- 복구의 값은 시설 정의의 `repair_gc`입니다. 요청에 금액이 없습니다.
- 기금은 남은 목표보다 많이 보내면 남은 만큼만 받고, 실제로 빠져나간 금액이 `cost_gc`입니다.
- `city.reason`은 복구면 `facility_repaired`, 기금으로 한 단계가 완성되면 `joint_defense`, 기여도만 쌓였으면 `null`입니다.

### 시작 데이터에 더해진 것

- `GET /me`: `gear`, `loadouts`, `cleared_fps_missions`, `city_contribution`, `support_region_code`가 더해졌습니다. 전체는 `{ player_id, display_name, residency, region_code, level, xp, xp_next, hp_max, gc, daily_hunt_gc, daily_hunt_gc_limit, unlocked_towers, cleared_stages, gear, loadouts, cleared_fps_missions, city_contribution, support_region_code }`.
- `GET /defs[?version=…]`: `{ version, unchanged, economy, progression, enemies, zones(사냥 구역), towers(방어시설 15종), stages(방어 구역), combat: { modes, gear, fps_missions, facilities, risk(위험도 정책), ranking(랭킹 가중치), daily_fps_reward_limit } }`. 가진 버전과 같으면 `{ version, unchanged: true }`만 옵니다. 관리자가 값을 고치면 버전이 바뀝니다.
- `GET /regions`: `{ sidos: [ { code, name, short_name } ], regions: [ { region_code, name, sido_code, residents, defense_points } ] }`.
- `POST /residency/check-nickname` `{ nickname }` → `{ nickname, available, reason, message }`, `POST /residency` `{ region_code, nickname }` → `{ region_code, nickname, bonus_gc, me }`.

## 18. 현실 혜택: CITY PASS 와 장학 (v7.0)

요청·응답의 자세한 내용은 두 문서에 있습니다.

- [CITY_PASS_API](CITY_PASS_API.md) — 홈페이지 로그인 코드, 멤버십, 혜택, 제휴 상점, 쿠폰
- [SCHOLARSHIP_API](SCHOLARSHIP_API.md) — 공고, 신청, 진행 상태, 철회, 이의신청

| 메서드 | 경로 | 내용 |
|---|---|---|
| POST | `/auth/web-code` | 홈페이지에 넣을 일회용 로그인 코드 (5분, 한 번) |
| GET | `/city-pass/profile` | 내 CITY PASS (닉네임, 회원번호, 관심 지역, 쿠폰 수) |
| PUT | `/city-pass/profile` | 관심 지역 바꾸기 |
| POST | `/city-pass/membership` | 가입 (무료, 약관 동의) |
| GET | `/city-pass/offers` | 받을 수 있는 혜택 (`region`, `category`) |
| GET | `/city-pass/merchants` | 제휴 상점 (`region`, `category`) |
| GET | `/city-pass/coupons` | 내 쿠폰 |
| POST | `/city-pass/coupons/claim` | 쿠폰 받기 (`Idempotency-Key` 필수) |
| POST | `/city-pass/coupons/redeem` | 상점에 보여 줄 일회용 코드 받기. **사용 처리가 아님** |
| GET | `/city-pass/history` | 이용 내역 |
| GET | `/scholarships` | 장학 공고 목록 |
| GET | `/scholarships/{id}` | 공고 상세 (자격, 심사 기준, 개인정보 안내) |
| POST | `/scholarships/{id}/applications` | 신청 (게임은 쓰지 않음. 신청은 홈페이지에서) |
| GET | `/scholarships/my-applications` | 내 신청 목록 |
| GET | `/scholarships/applications/{id}` | 내 신청 하나 |
| POST | `/scholarships/applications/{id}/withdraw` | 철회 |
| POST | `/scholarships/applications/{id}/appeal` | 이의신청 |
| POST | `/scholarships/applications/{id}/guardian-link` | 보호자 확인 주소 다시 받기 |

공통으로 지켜지는 것:

- **모든 경로에 접속 토큰이 필요합니다.** 토큰 없이 부르면 18개 경로 모두 `401` (`BenefitApiTest::testEveryBenefitRouteNeedsAToken`).
- 본인의 것만 돌려줍니다. 남의 쿠폰·신청은 `404`.
- 기능은 모두 꺼진 채로 설치됩니다. 꺼져 있는 동안 조회는 빈 목록과 안내 문구를, 발급·신청은 `403 FEATURE_DISABLED`를 돌려줍니다. 켜는 것은 관리자 화면의 승인 절차로만 합니다 ([REAL_WORLD_REWARDS_POLICY](REAL_WORLD_REWARDS_POLICY.md) 4장).
- 이 경로들은 지갑·원장·경험치·전투 기록을 읽지도 바꾸지도 않습니다. 응답에 게임 경제의 값이 없습니다.
- **없는 경로**: GC ↔ 쿠폰·상품·현금 교환, 쿠폰 구매, 이용자가 스스로 하는 쿠폰 사용 처리, 장학 결과 결정, 지급·송금. 주소를 짐작해 불러도 `404`/`400`입니다 (`testThereIsNoRouteThatPaysConvertsOrDecides`).
- 관리자 기능과 상점의 쿠폰 사용 처리는 JSON API 가 아니라 따로 로그인하는 웹 화면입니다 ([ADMIN_SYSTEM](ADMIN_SYSTEM.md) 11장, [COUPON_SYSTEM](COUPON_SYSTEM.md) 5장).

## 19. 도시 성장, 이사, 지역 콘텐츠 (v9.0)

`server/src/Api/RoutesV9.php`. 19~21장의 모든 경로에 접속 토큰이 필요합니다. 형식의 규칙은 0장과 같습니다 (`snake_case`, 평평한 응답, UTC 시각). **전입자 수, 확장 단계, 세율, 세액, 보상 금액을 받는 경로는 없습니다** — 모두 서버가 계산합니다. 관리자 기능은 JSON API 가 아니라 관리자 웹 화면입니다 ([ADMIN_SYSTEM](ADMIN_SYSTEM.md) 12장).

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/cities/growth` | 전국 도시의 전입자 현황과 성장 단계, 확장 기준 |
| GET | `/cities/{code}/growth` | 한 도시의 현황: 누적, 최근 7일·30일 증가, 기준별 달성 여부, 30일 추이, 모델링 상태 |
| GET | `/cities/content` | 내 클라이언트가 받을 수 있는 지역 콘텐츠 목록 |
| GET | `/residency` | 내 주거 도시, 유효 전입으로 세어지는지, 이사 조건 |
| POST | `/residency/move` | 이사 (`Idempotency-Key` 필수) |

설명은 [CITY_GROWTH_SYSTEM](CITY_GROWTH_SYSTEM.md), [CITY_MODELING_PIPELINE](CITY_MODELING_PIPELINE.md).

`GET /cities/growth`:

```json
{
  "count_rule": "linked",
  "milestones": [ { "code": "street_open", "name": "상징거리 개방 목표", "threshold": 1000, "opens_street": true, "description": "…" } ],
  "cities": [ { "region_code": "11110", "name": "종로구", "sido_code": "11", "valid_residents": 0, "stage": 0, "stage_name": null,
                "next_threshold": 100, "street_threshold": 1000, "threshold_reached": false, "street_open": false,
                "modeling_status": "NOT_STARTED", "content_version": null, "opened_at": null } ]
}
```

`GET /cities/11110/growth`는 한 도시의 같은 값에 `cumulative_residents`, `growth_7d`, `growth_30d`, `counted_at`, `milestones: [ { code, name, threshold, reached } ]`, `trend: [ { date, valid_residents } ]`가 더해집니다. 없는 지역은 `404`.

- 계정의 목록, 닉네임, 게스트 여부는 어디에도 나오지 않습니다. 숫자만 나옵니다.
- `count_rule`은 지금의 집계 규칙입니다 (`linked` 또는 `linked_or_proven`).

`GET /cities/content` (헤더 `X-Client-Version`으로 거름):

```json
{ "available": true,
  "items": [ { "content_id": "c_…", "region_code": "11110", "kind": "street", "version": "1.0.0", "address_key": "city/11110/street",
               "catalog_url": "https://…/catalog.json", "content_hash": "<sha-256>", "size_bytes": 1048576, "min_client_version": "9.0.0" } ] }
```

- 배포된(`released`) 것 가운데 내 계정이 단계적 배포의 비율 안에 들고, 내 클라이언트 버전이 `min_client_version` 이상인 것만 옵니다.
- 서버에 콘텐츠 저장소 주소(`CONTENT_BASE_URL`)가 없으면 `available: false`와 빈 목록.

`GET /residency`:

```json
{ "residency": "resident", "region_code": "11110", "region_name": "종로구", "since": "2026-11-01T02:00:00Z",
  "counted": false, "not_counted_reason": "guest_account", "count_rule": "linked",
  "move_enabled": true, "move_fee_gc": 1000, "move_cooldown_days": 7, "can_move_at": null, "gc": 1500 }
```

`POST /residency/move` 요청 `{ "region_code": "26350" }` → `{ "region_code": "26350", "region_name": "해운대구", "previous_region_code": "11110", "fee_gc": 1000, "gc": 500, "can_move_at": "2026-11-21T03:00:00Z" }`

| 상황 | 응답 |
|---|---|
| 이사가 닫혀 있음 | `422 LOCKED` (`move_disabled`) |
| 이방인 | `409 CONFLICT` (`not_resident`) |
| 없는 지역 | `404 NOT_FOUND` |
| 지금 사는 도시 | `409 CONFLICT` (`same_city`) |
| 전입·이사한 지 며칠 안 됨 | `422 LOCKED` (`move_cooldown`) |
| 협동 방어전에 들어가 있음 | `409 CONFLICT` (`in_coop_session`) |
| GC 부족 | `422 INSUFFICIENT_GC` |

## 20. 지방세, 도시 재정, 참여예산 (v9.0)

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/tax/policies` | 세금 정책, 내가 면제인지와 이유, 오늘의 사업세 소득·세액 |
| GET | `/tax/transactions` | 내가 낸 세금의 명세 (`limit` 1~100, `before` = 명세 id) |
| GET | `/cities/{code}/treasury` | 도시 재정 현황 (누구나) |
| GET | `/cities/{code}/treasury/expenditures` | 예산 집행 내역 (누구나. `limit`, `before`) |
| GET | `/cities/budget/options` | 참여예산의 분야, 대상 시설, 한도, 자격 |
| GET | `/cities/{code}/budget/proposals` | 그 도시의 공개된 제안과 내가 낸 제안 (`status`로 거름) |
| GET | `/cities/budget/proposals/{id}` | 제안 하나 (집행되었으면 `expenditure`) |
| POST | `/cities/budget/proposals` | 제안 (`Idempotency-Key` 필수) |
| POST | `/cities/budget/proposals/{id}/vote` | 투표 (`Idempotency-Key` 필수) |
| POST | `/cities/budget/proposals/{id}/withdraw` | 검토 전의 내 제안을 거둠 |

설명은 [CITY_TAX_SYSTEM](CITY_TAX_SYSTEM.md), [CITY_TREASURY_SYSTEM](CITY_TREASURY_SYSTEM.md), [PARTICIPATORY_BUDGET](PARTICIPATORY_BUDGET.md).

**세금과 재정의 경로는 읽기 전용입니다.** 세금을 내는 요청, 세율을 바꾸는 요청, 재정을 집행하는 요청은 없습니다 (`TaxTest::testTaxAndTreasuryApiIsReadOnly`). 세금은 다른 거래(구매, 보상)가 일어날 때 서버가 걷습니다.

`GET /tax/policies`:

```json
{ "notice": "게임 안에서만 쓰는 가상의 세금이에요. 실제 돈이나 현실의 세금과는 아무 관계가 없어요.",
  "business_income_today": 0, "business_tax_today": 0,
  "policies": [ { "tax_type": "trade", "name": "거래세", "description": "…", "active": false, "rate_bp": 300,
                  "applies_to": [ { "reason": "gear_purchase", "name": "장비 구매" } ],
                  "exempt_amount": 100, "min_level": 3, "grace_days": 7, "cap_gc": 0, "period": "transaction", "included": true,
                  "exempt": true, "exempt_reason": "beginner_level", "exempt_message": "아직 레벨이 낮아 세금을 내지 않아요." } ] }
```

`GET /tax/transactions` → `{ "transactions": [ { id, tax_type, tax_name, base_gc, rate_bp, tax_gc, included, source_reason, period_key, region_code, region_name, at } ], "totals": [ { tax_type, tax_name, total_gc, count } ], "gc" }`. 내 것만 옵니다.

`GET /cities/11110/treasury` → `{ region_code, region_name, general_gc, reserve_gc, reserve_percent, income: [ { tax_type, tax_name, total_gc, last_30d_gc } ], spent: [ { category, category_name, total_gc, count } ], recent: [ { account, delta_gc, balance_after, reason, reason_name, at } ], notice }`.

`GET /cities/11110/treasury/expenditures` → `{ region_code, expenditures: [ { id, category, category_name, title, description, amount_gc, account, effect_type, target_code, effect_detail, proposal_id, by_vote, at } ] }`. 집행한 관리자와 내부 사유는 나오지 않습니다.

`POST /cities/budget/proposals` 요청 `{ "category": "defense_facility", "target_code": "twr_command_center", "title": "…", "description": "…", "requested_gc": 5000 }`, `POST …/vote` 요청 `{ "choice": "yes" }` (`yes` / `no`) → 둘 다 `{ "proposal": { … } }`:

```json
{ "proposal": { "id": "b_…", "region_code": "11110", "region_name": "종로구", "category": "defense_facility", "category_name": "공동 방어시설",
    "title": "…", "description": "…", "requested_gc": 5000, "target_code": "twr_command_center", "target_name": "…",
    "status": "voting", "status_name": "투표 중", "proposer": "닉네임", "mine": false, "review_note": "",
    "vote_starts_at": "…", "vote_ends_at": "…", "yes_count": 3, "no_count": 1, "quorum": 5,
    "my_vote": "yes", "can_vote": false, "vote_block_reason": "already_voted", "vote_block_message": "이미 투표했어요.", "created_at": "…" } }
```

| 상황 | 응답 |
|---|---|
| 참여예산이 꺼져 있음 (제안·투표) | `403 FEATURE_DISABLED` (`budget_disabled`). 목록과 옵션은 `enabled: false`로 옴 |
| 이방인의 제안 | `409 CONFLICT` (`not_resident`) |
| 레벨이 낮음 (제안, 투표), 전입한 지 얼마 안 됨 (투표) | `422 LOCKED` (`level_too_low`, `resident_too_new`) |
| 분야·제목·설명·금액·대상이 맞지 않음 | `400 INVALID_REQUEST` |
| 진행 중인 내 제안이 이미 있음 | `422 LIMIT_REACHED` (`too_many_proposals`) |
| 이미 투표함, 다른 도시의 주민, 투표 중이 아님 | `409 CONFLICT` (`already_voted`, `not_resident_here`, `not_voting`) |
| 없는 제안, 아직 공개되지 않은 남의 제안 | `404 NOT_FOUND` |
| 검토가 시작된 제안을 거두려 함 | `409 CONFLICT` (`not_withdrawable`) |

- 응답에 표의 무게, 다른 사람의 투표 내용, 낸 세금의 양은 없습니다. 찬성·반대의 수와 내 투표만 있습니다.
- `review_note`(반려·취소 사유)는 제안한 사람에게만 채워져 옵니다.

## 21. 협동 방어전 (v9.0)

**이 경로들은 판에 들어가고 나가는 것, 입장권, 끝난 뒤의 결과만 다룹니다.** 전투 중의 상태(적의 위치, 타워 설치, 공동 예산, 전술 핑)를 주고받는 REST 경로는 없습니다. 그것은 실시간 서버가 WebSocket 으로 맡습니다 (22장).

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/coop/config` | 열려 있는지, 인원, 역할, 핑 설정, 오늘 남은 보상 횟수, 내가 들어가 있는 판 |
| GET | `/coop/sessions` | 지금 내가 지키는 도시(내 도시, 지원을 나가 있으면 그 도시)의 대기실 목록 |
| POST | `/coop/sessions` | 방어전 만들기 (`Idempotency-Key` 필수) |
| GET | `/coop/sessions/{id}` | 판 하나와 팀원 목록 |
| POST | `/coop/sessions/{id}/join` | 참여 (`Idempotency-Key` 필수) |
| POST | `/coop/sessions/{id}/leave` | 나가기. 방장이 나가면 다음 사람이 방장. 마지막 사람이 나가면 대기실은 닫힘 |
| PUT | `/coop/sessions/{id}/role` | 대기실에서 역할 바꾸기 (전투 중에는 실시간 서버에서) |
| POST | `/coop/sessions/{id}/ticket` | 실시간 서버에 낼 입장권 |
| GET | `/coop/sessions/{id}/result` | 방어전 결과와 내 보상, 팀원별 기록 |
| GET | `/coop/sessions/{id}/budget` | 공동 예산의 장부 (팀원만. 장부는 판이 끝난 뒤에 채워짐. 전투 중의 잔액은 실시간 서버가 보냄) |
| GET | `/coop/history` | 내가 참여한 최근 20판 |

설명은 [COOP_DEFENSE_SYSTEM](COOP_DEFENSE_SYSTEM.md).

`GET /coop/config`:

```json
{ "available": false, "unavailable_reason": "coop_disabled", "region_code": "11110",
  "default_players": 4, "max_players": 8, "min_players_to_start": 2, "allow_late_join": true,
  "daily_reward_limit": 5, "rewards_left_today": 5, "role_required": false,
  "roles": [ { "code": "commander", "name": "지휘관", "description": "…", "build_discount_percent": 0, "repair_bonus_percent": 0,
               "attack_bonus_percent": 0, "ping_cooldown_percent": 50, "max_per_session": 1 } ],
  "ping": { "enabled": true, "blocked": false, "burst": 3, "refill_seconds": 4, "lifetime_seconds": 8, "route_overlay": true, "range_overlay": true,
            "kinds": [ { "kind": "focus_fire", "name": "공격 집중" } ] },
  "active_session": null }
```

`POST /coop/sessions` 요청 `{ "zone_code": "outskirts_1", "max_players": 4, "role_code": "builder" }` (`max_players`, `role_code`는 생략 가능), `POST …/join` 요청 `{ "role_code": null }`, `PUT …/role` 요청 `{ "role_code": "support" }` → `{ "session": { … } }`:

```json
{ "session": { "id": "d_…", "status": "lobby", "status_name": "대기 중", "region_code": "11110", "region_name": "종로구",
    "zone_code": "outskirts_1", "zone_name": "…", "max_players": 4, "players": 2, "mine": true, "is_host": true,
    "created_at": "…", "expires_at": "…", "started_at": null,
    "members": [ { "player_id": "p_…", "nickname": "…", "level": 5, "role_code": "builder", "status": "joined", "is_host": true, "is_me": true } ] } }
```

`POST /coop/sessions/{id}/ticket` → `{ "session_id": "d_…", "ticket": "<서명된 입장권>", "url": "wss://…/ws", "expires_at": "…" }`. 입장권은 60초 동안, 한 번만 쓸 수 있습니다. 팀원만 받을 수 있습니다.

`GET /coop/sessions/{id}/result`:

```json
{ "finished": true,
  "result": { "session_id": "d_…", "status": "finished", "result": "win", "zone_code": "outskirts_1", "waves_cleared": 5, "gate_hp_left": 310,
              "duration_seconds": 412, "players_at_start": 4, "reward_gc": 300, "reward_xp": 120, "reward_note": null, "reward_message": "",
              "kills": 31, "towers_built": 3, "towers_upgraded": 1, "repairs": 2, "damage_dealt": 5120, "pings_sent": 6,
              "energy_granted": 900, "energy_spent": 820, "gc": 2100, "level": 6, "xp": 40, "xp_next": 300, "ended_at": "…" },
  "team": [ { "player_id": "p_…", "nickname": "…", "role_code": "builder", "kills": 4, "towers_built": 3, "towers_upgraded": 1,
              "repairs": 0, "damage_dealt": 640, "is_me": false } ] }
```

- 아직 끝나지 않았으면 `finished: false`. 실시간 서버의 보고가 반영된 뒤에 `true`가 됩니다.
- `reward_note`: `null`(받음), `low_participation`, `daily_limit`, `rejected`(결과가 검증에 걸림). `reward_message`에 화면에 보일 설명이 옵니다.
- 팀원의 `player_id`는 계정의 **공개 ID**입니다 (실시간 서버의 대원 ID와 같음). 다른 사람의 보상 금액은 나오지 않습니다.

| 상황 | 응답 |
|---|---|
| 협동 방어전이 열리지 않음 | `403 FEATURE_DISABLED` (`coop_disabled`, `realtime_not_configured`, `mode_closed`). `GET /coop/config`는 오류가 아니라 `available: false` |
| 이방인 | `409 CONFLICT` (`not_resident`) |
| 레벨이 낮음 | `422 LOCKED` (`level_too_low`) |
| 이미 다른 판에 들어가 있음 | `409 CONFLICT` (`in_coop_session`) |
| 다른 도시의 판 (지원을 나가지 않음) | `403 FORBIDDEN` (`other_city`) |
| 정원이 참, 들어갈 수 없는 상태 | `409 CONFLICT` (`session_full`, `not_joinable`) |
| 없는 역할 / 이미 다른 팀원이 맡은 역할 | `400 INVALID_REQUEST` / `409 CONFLICT` (`role_taken`) |
| 전투가 시작된 뒤의 역할 변경 | `409 CONFLICT` (`role_locked`) |
| 이미 끝난 판의 입장권 | `409 CONFLICT` (`session_closed`) |
| 없는 판, 내가 팀원이 아닌 판의 결과·장부 | `404 NOT_FOUND` |

## 22. 실시간 서버와 내부 경로 (v9.0)

### 실시간 서버 (WebSocket)

PHP 서버가 아니라 **별도의 실시간 서버**(`realtime/`, .NET 8)입니다. 주소는 입장권 응답의 `url`로 받습니다 (`wss://`만. 로컬 개발에서만 `ws://localhost`). 접속한 뒤 첫 메시지로 입장권을 냅니다.

```json
→ { "type": "join", "ticket": "<입장권>" }
← { "type": "welcome", "you": "p_…", "session": "d_…", "tick_ms": 100, "zone": { … }, "map": { "min_x": -26, "max_x": 26, "min_z": -26, "max_z": 44, "path": [ … ], "slots": [ … ], "landmarks": [ … ] },
    "structures": [ … ], "roles": [ … ], "rules": { … }, "ping": { … }, "state": { … } }
← { "type": "state", "tick": 1840, "phase": "wave", "wave": 3, "wave_count": 5, "gate_hp": 420, "gate_max_hp": 500, "energy": 135,
    "players": [ … ], "enemies": [ … ], "towers": [ … ], "pings": [ … ], "dangers": [ … ] }
→ { "type": "build", "request_id": "r1", "slot": 2, "code": "twr_machine_gun" }
← { "type": "result", "action": "build", "request_id": "r1", "ok": true, "reject": null, "duplicate": false, "energy": 85 }
```

메시지의 전체 목록, 거절 이유, 연결 제한은 [COOP_DEFENSE_SYSTEM](COOP_DEFENSE_SYSTEM.md) 5장, 전술 핑은 [TACTICAL_MINIMAP](TACTICAL_MINIMAP.md) 4장.

### 내부 경로 (실시간 서버 → 게임 서버)

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/internal/coop/sessions/{id}/config` | 판을 여는 데 필요한 것: 구역, 적, 타워, 역할, 규칙, 팀원 |
| POST | `/internal/coop/sessions/{id}/start` | 방장이 시작함 → 판을 `running`으로 |
| POST | `/internal/coop/sessions/{id}/result` | 결과와 기록 (팀원별 기록, 공동 예산 장부, 핑 기록). **보상 금액은 받지 않음** |

- 접속 토큰이 아니라 **서명**(`X-CG-Signature: t=<시각>,v1=<HMAC-SHA256>`)으로 확인합니다. 서명이 없거나 틀리거나 시각이 60초 넘게 어긋나면 **없는 주소처럼 `404`**로 답합니다. `REALTIME_SECRET`이 설정되지 않은 서버에서는 늘 `404`입니다.
- 결과는 한 번만 반영됩니다. 같은 결과가 다시 오면 처음 응답을 그대로 돌려줍니다.
- 응답: `{ "accepted": true | false, "reject_reason": null | "…", … }`. 검증에 걸린 결과도 `200`이고 `accepted: false`입니다 (실시간 서버가 다시 보내지 않도록). 다시 온 결과에는 `duplicate: true`가 붙습니다. 이미 닫힌(취소·시간 초과) 판의 결과는 `409` (`session_closed`).
- 운영에서는 앞단의 웹 서버가 `/api/v1/internal/`을 실시간 서버의 주소에서만 받도록 막는 것을 권합니다 ([SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md) 13장). 서명이 주된 방어이고 이것은 한 겹을 더하는 것입니다.

## 23. 실시간 대시보드 요약 (v10.0)

게임 화면 오른쪽의 카드 세 장([LIVE_DASHBOARD](LIVE_DASHBOARD.md))이 한 번에 받아 가는 값입니다. **새 경로는 이것 하나**이고, 상세 화면은 저마다의 기존 경로를 씁니다 (아래 표).

| 메서드 | 경로 | 내용 |
|---|---|---|
| GET | `/dashboard/summary` | 내 계좌, 우리 도시, 전국 현황, 알림, 관리자가 정한 대시보드 설정 |

- **인증이 필요합니다.** 토큰이 없거나 틀리면 `401`.
- **언제나 요청한 사람 자신의 것입니다.** 요청에 인자가 없습니다. 쿼리(`?player_id=…`), 헤더, 본문으로 다른 계정을 가리켜도 무시합니다. 응답에 계정 ID, 닉네임 같은 식별 값이 없습니다.
- **읽기만 합니다.** 이 요청으로 잔액, 위험도, 재정이 바뀌지 않습니다. `Idempotency-Key`가 필요 없습니다. `POST` / `PUT` / `DELETE`는 `400 INVALID_REQUEST`.
- 새로 계산하는 값이 없습니다. 지갑, 도시 위험도(17장), 지역 랭킹(17장), 도시 성장(19장), 도시 재정(20장)이 내놓는 값을 모은 것이고, 같은 순간의 그 경로들과 값이 같습니다 (테스트가 대조).
- 공통 응답 헤더대로 `Cache-Control: no-store`.
- 요청 제한은 없습니다 (0장의 표와 같음). 클라이언트가 기본 20초 간격, 가장 잦아도 2초에 한 번으로 스스로 줄입니다 ([LIVE_DASHBOARD](LIVE_DASHBOARD.md) 5장).

```json
{ "finance": {
    "gc": 12340,
    "bank_available": false, "bank_gc": 0, "term_gc": 0,
    "stocks_available": false, "stock_value_gc": 0, "stock_profit_gc": 0,
    "loans_available": false, "loan_balance_gc": 0,
    "net_worth_gc": 12340, "net_worth_partial": true,
    "trend": [ { "date": "2026-11-08", "gc": 9000 }, { "date": "2026-11-14", "gc": 12340 } ] },
  "my_city": {
    "resident": true, "region_code": "26350", "name": "해운대구",
    "risk": 42, "stage": "caution", "defense_power": 1240, "valid_residents": 0, "treasury_gc": 3500,
    "invasion_kind": "outskirts", "invasion_severity": 2, "invasion_progress": 3, "invasion_required": 10, "invasion_ends_at": "2026-11-14T06:00:00Z",
    "my_contribution": 85, "rank": 12,
    "growth_stage": 0, "growth_stage_name": null, "modeling_status": "NOT_STARTED",
    "support_open": false, "alert": true, "alert_reason": "invasion" },
  "national": {
    "total_cities": 230, "average_safety": 81, "emergency_cities": 2, "danger_cities": 9, "invasion_cities": 4, "support_requests": 3, "home_rank": 12,
    "top": [ { "region_code": "11110", "name": "종로구", "rank": 1, "score": 940, "risk": 12, "stage": "safe" } ],
    "urgent": [ { "region_code": "11680", "name": "강남구", "risk": 91, "stage": "emergency", "invasion_kind": null } ] },
  "notifications": [
    { "id": "city_invasion:481", "kind": "city_invasion", "level": "urgent", "region_code": "26350",
      "title": "우리 도시에 외곽 침공!", "body": "해운대구을(를) 지켜 주세요. 막은 횟수 3 / 10" } ],
  "settings": {
    "refresh_seconds": 20, "city_alert_risk": 70, "national_notice": 1, "sample_data": false,
    "cards": [ { "id": "finance", "visible": true, "expanded": true }, { "id": "city", "visible": true, "expanded": true },
               { "id": "national", "visible": true, "expanded": false } ] },
  "updated_at": "2026-11-14T05:03:21Z" }
```

### finance

| 항목 | 내용 |
|---|---|
| `gc` | 지갑 잔액 (`GET /me`의 `gc`와 같음) |
| `bank_available`, `stocks_available`, `loans_available` | 그 기능이 이 서버에 있는가. **지금은 셋 다 `false`** (8~10장이 서버에 없음). `false`이면 딸린 금액은 0 으로 오지만 "0 GC"가 아니라 "없음"으로 읽어야 합니다 |
| `bank_gc`, `term_gc` | 통장, 정기예금 |
| `stock_value_gc`, `stock_profit_gc` | 가상 주식 평가액, 평가손익 |
| `loan_balance_gc` | 대출 잔액 |
| `net_worth_gc` | 순자산. **서버에 있는 자산과 부채로만** 셉니다 (지금은 `gc`와 같음) |
| `net_worth_partial` | 빠진 항목이 있는가 (지금은 늘 `true`) |
| `trend` | 최근 7일(한국 날짜, 오늘 포함)의 하루 끝 지갑 잔액. 원장의 `balance_after`를 읽은 것. 거래가 없던 날은 앞날의 값. 마지막 날은 지금 잔액 |

서버는 개발용 샘플 숫자를 만들지 않습니다. `settings.sample_data`가 `true`일 때 클라이언트가 "개발용 샘플"이라고 적어 따로 보여 줍니다 ([FINANCE_DASHBOARD](FINANCE_DASHBOARD.md) 3장).

### my_city

이방인이면 `resident: false`이고 나머지는 빈 값(`null`, 0, `"safe"`, `"NOT_STARTED"`)입니다.

| 항목 | 내용 | 같은 값을 주는 기존 경로 |
|---|---|---|
| `risk`, `stage`, `defense_power`, `rank`, `invasion_*`, `support_open` | 위험도(0~100), 단계(`safe` / `caution` / `danger` / `emergency`), 방어력, 순위, 진행 중인 침공, 지원 요청이 열려 있는가 | `GET /cities/risk` |
| `valid_residents`, `growth_stage`, `growth_stage_name`, `modeling_status` | 유효 전입자, 성장 단계, 상징거리 모델링 상태 | `GET /cities/{code}/growth` |
| `treasury_gc` | 도시 재정 (일반 회계 + 예비비). 게임 안의 GC | `GET /cities/{code}/treasury` |
| `my_contribution` | 이 도시에 쌓은 내 기여 포인트 | `GET /cities/{code}` |
| `alert`, `alert_reason` | 카드에 경고를 띄울지와 그 까닭: `invasion`(침공 중) → `emergency`(긴급 단계) → `risk`(위험도가 `city_alert_risk` 이상). 급한 것 하나. **서버가 판정** | — |

### national

| 항목 | 내용 |
|---|---|
| `total_cities` | 도시(시·군·구)의 수 |
| `average_safety` | 100 − 모든 도시 위험도의 평균 (반올림) |
| `emergency_cities`, `danger_cities`, `invasion_cities` | 긴급 단계 / 위험 단계 / 침공 중인 도시의 수 |
| `support_requests` | 지원을 요청한 도시의 수 |
| `home_rank` | 우리 도시의 순위. 이방인은 0 |
| `top` | 랭킹 상위 5곳 (`GET /cities/rankings`의 앞 5줄과 같음) |
| `urgent` | 긴급 단계이거나 침공 중인 도시 5곳까지. 긴급 단계가 먼저, 그 안에서는 위험도가 높은 순 |

도시별 위험도 전체(지도의 색)는 여기에 싣지 않습니다. 기존 `GET /cities/risk`를 씁니다.

### notifications

| 항목 | 내용 |
|---|---|
| `id` | 같은 일에는 같은 값. 클라이언트가 한 번만 알리는 데 씁니다. 침공은 침공 번호, 단계·위험도 경고는 도시 코드와 한국 날짜 |
| `kind` | `city_invasion`, `city_emergency`, `city_risk`, `city_support`, `national_emergency`, `national_invasion` |
| `level` | `urgent` / `warn` / `info` |
| `region_code`, `title`, `body` | 도시, 화면에 보일 글 |

- 우리 도시의 알림이 앞에 옵니다. 이방인에게는 도시 알림이 없습니다.
- 다른 도시의 알림은 `settings.national_notice`를 따릅니다: `0` 보내지 않음, `1` 긴급 단계인 도시, `2` 긴급 단계이거나 침공 중인 도시. 위험도가 높은 순으로 관리자가 정한 수(기본 3)까지.
- 서버는 누가 무엇을 봤는지 기억하지 않습니다. 조건이 이어지는 동안 같은 `id`로 계속 옵니다.

### settings

관리자가 정한 값입니다 ([ADMIN_SYSTEM](ADMIN_SYSTEM.md) 13장). `cards`는 처음 보는 이용자의 기본값이고, 이용자가 자기 화면에서 바꾼 것은 서버에 오지 않습니다 (기기에 저장).

### 상세 보기가 쓰는 기존 경로

| 카드 | 상세 | 경로 |
|---|---|---|
| 내 계좌 | 은행 계좌, 예금 거래 내역 | `GET /bank` (8장. **서버에 없음** — 모의 서버만) |
| 내 계좌 | 주식, 대출 | 9장, 10장 (서버에 없음) |
| 우리 도시 | 도시 방어 현황 | `GET /cities/{code}`, `GET /cities/invasions` (17장) |
| 우리 도시 | 도시 재정, 지방세 | `GET /cities/{code}/treasury`, `…/expenditures`, `GET /tax/policies`, `GET /tax/transactions` (20장) |
| 우리 도시 | 성장 단계, 상징거리 | `GET /cities/{code}/growth` (19장) |
| 우리 도시, 전국 | 지역 순위 | `GET /cities/rankings` (17장) |
| 우리 도시, 전국 | 긴급 지원 요청 | `GET /cities/support` (17장) |
| 전국 | 전체 지도의 도시별 위험도 | `GET /cities/risk`, `GET /cities/{code}/history` (17장) |

### 클라이언트

`IGameBackend.GetDashboardSummary` → `DashboardSummaryDto`. 구현은 모의 서버뿐입니다 (`LocalMockBackend.Dashboard.cs`: 모의 서버의 기존 조회들을 모아 같은 모양으로 답함). 응답의 모양은 0장의 표본 대조(`ClientWireTest` ↔ `WireContractTests`)에 들어 있습니다.
