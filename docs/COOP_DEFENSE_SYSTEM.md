# COOP_DEFENSE_SYSTEM — 협동 타워디펜스

> 기준 v9.0 · 2026-11-14 · 상태: 전투 규칙(`CG.Domain.CoopSim`), 실시간 서버(`realtime/`, .NET 8 WebSocket), 게임 서버(PHP `CG\Service\Coop`), 관리자 화면, 클라이언트의 연결 계층과 연습전, 전장 화면 구현. 실시간 서버와 게임 서버는 로컬에서 서로 붙여 테스트했고 **운영에는 설치하지 않음**. Unity 화면(전장, HUD)은 컴파일 검사만 했고 실행해 보지 못함. 운영 설정에서 **꺼져 있음** · 수치는 플레이테스트 전의 초기값

## 1. 방향

도시 외곽의 관문을 여러 대원이 함께 지킵니다. 혼자 하는 방어전([DEFENSE_MODE](DEFENSE_MODE.md))은 그대로 두고, 그 옆에 "함께 하는 판"을 더했습니다. 가장 먼저 본 것은 **실제로 협력해야 재미있는가**입니다.

- **공동 예산 하나를 함께 씁니다.** 내 에너지가 아니라 팀의 에너지입니다. 어디에 세울지 서로 알려야 합니다.
- **역할이 겹치지 않게 돕습니다.** 지휘관은 핑을 자주 쓰고, 설치 담당은 싸게 세우고, 지원 담당은 빨리 고치고, 전투 담당은 타워가 놓친 적을 잡습니다. 강제가 아니라 고르는 것입니다.
- **전술 핑과 미니맵으로 말없이 통합니다** ([TACTICAL_MINIMAP](TACTICAL_MINIMAP.md)). 채팅이 없어도 (초등학생도) 협력할 수 있게 했습니다.
- **사람이 많을수록 적이 강해지고 예산도 늘어납니다.** 혼자 다 할 수 없는 크기가 됩니다.
- **누가 나가도 판은 계속됩니다.** 다시 들어오면 이어서 합니다.

지키는 원칙:

1. **전투는 서버가 정합니다.** 적의 이동, 타워의 상태, 웨이브, 관문 내구도, 설치 결과, 공동 예산은 모두 실시간 서버의 시뮬레이션에서 정해집니다. 클라이언트는 "어디로 가겠다", "여기에 세우겠다"는 의사만 보내고 결과를 받아 그립니다.
2. **PHP REST API 로 실시간 동기화를 하지 않습니다.** 전투 중의 상태는 게임 서버를 거치지 않습니다.
3. **보상은 게임 서버가 정합니다.** 실시간 서버는 금액을 보내지 않고, 게임 서버는 받은 결과를 다시 검증합니다.
4. **공동 예산은 GC 가 아닙니다.** 그 판 안의 전투 에너지이고, 지갑과 이어지지 않습니다.

## 2. 한 판의 흐름

| 단계 | 내용 |
|---|---|
| 만들기·참여 | 주민이 자기 도시(또는 지원하러 간 도시)의 방어전을 만들거나 대기실 목록에서 골라 들어감. 한 계정은 동시에 한 판에만 |
| 대기실 | 역할을 고르고(안 골라도 됨) 방장이 시작. 대원이 `minPlayersToStart`(2)명 이상이어야 함. 15분 안에 시작하지 않으면 닫힘 |
| 전투 | 5초 준비 뒤 웨이브. 웨이브 사이에 쉬는 시간. 관문 내구도가 0이 되면 패배, 모든 웨이브를 막으면 승리 |
| 중간 참여 | `allowLateJoin`이 켜져 있으면 전투 중에도 들어올 수 있음 (자리가 남았을 때) |
| 이탈 | 연결이 끊겨도 전투는 계속됨. 같은 입장 절차로 다시 들어오면 이어서 함. 전투 중에 아무도 없는 채로 1분이 지나면 판을 닫음 (`all_left`, 패배로 기록) |
| 끝 | 실시간 서버가 결과와 기록을 게임 서버에 보고 → 게임 서버가 검증하고 보상·경험치·도시 위험도를 반영 → 클라이언트가 결과를 조회 |

### 인원

처음에는 4인(`defaultPlayers`), 설정으로 8인(`maxPlayers`)까지. 규칙 코드에는 인원 상한이 박혀 있지 않습니다 — 시작 자리와 배율이 인원에서 계산됩니다. 8인 넘게 늘리려면 설정의 허용 범위와 지도의 시작 자리만 넓히면 됩니다.

| 사람 수에 따른 배율 | 기본 | 뜻 |
|---|---|---|
| 적의 체력 | 한 명마다 +60% (`hpScalePercentPerPlayer`) | 2명이면 160%, 4명이면 280% |
| 공동 예산 | 한 명마다 +50% (`energyScalePercentPerPlayer`) | 시작 예산과 웨이브 지급에 적용 |

배율은 **전투를 시작할 때의 인원**으로 정합니다. 중간에 누가 나가도 적이 약해지지 않습니다.

### 역할 (`defense_team_roles`, 관리자가 고침)

| 코드 | 이름 | 보정 (기본) | 한 판의 인원 |
|---|---|---|---|
| `commander` | 지휘관 | 전술 핑이 다시 차는 시간 −50% | 1명 |
| `builder` | 타워 설치 담당 | 설치·강화 비용 −10% | 제한 없음 |
| `support` | 수리 및 지원 담당 | 수리량 +50% | 제한 없음 |
| `fighter` | 직접 전투 담당 | 직접 공격 피해 +20% | 제한 없음 |

역할은 고르지 않아도 되고(`role_required: false`), 전투 중에도 바꿀 수 있습니다. 역할이 없어도 모든 행동을 할 수 있습니다 — 보정만 없습니다.

## 3. 공동 예산

팀이 함께 쓰는 에너지입니다 (`SharedBudget`).

| 들어옴 | 나감 |
|---|---|
| 시작 지급 (`grant_start`), 웨이브를 막을 때 (`grant_wave`), 적을 쓰러뜨릴 때 (`grant_kill`) | 설치 (`build`), 강화 (`upgrade`), 수리 (`repair`) |

- **원자적 차감:** 방마다 일꾼이 하나이고 명령을 한 번에 하나씩 처리합니다. 두 사람이 같은 순간에 예산을 쓰려 해도 순서가 정해지고, 뒤의 요청은 남은 예산으로 다시 판정됩니다. 잔액이 음수가 될 수 없습니다.
- **같은 자리에는 타워가 하나만:** 두 사람이 같은 자리에 동시에 세우려 하면 먼저 처리된 쪽만 세워지고, 다른 쪽은 **예산이 빠지지 않은 채** `slot_taken`으로 거절됩니다.
- **같은 요청은 한 번만:** 예산을 쓰는 명령에는 요청 번호(`request_id`)가 붙습니다. 같은 사람이 같은 번호를 다시 보내면(다시 보내기, 두 번 누르기) 다시 실행하지 않고 처음 결과를 돌려줍니다.
- **장부:** 바뀔 때마다 한 줄(순번, 종류, 변화량, 바뀐 뒤 잔액, 쓴 사람, 자리). 판이 끝나면 게임 서버로 넘어가 `defense_shared_budget`에 보관됩니다. 게임 서버는 줄마다 잔액이 이어지는지, 음수가 없는지, 쓴 사람이 팀원인지, 같은 자리에 두 번 세운 줄이 없는지 확인하고, 맞지 않으면 그 판의 결과를 인정하지 않습니다.

## 4. 구조

```
 Unity 클라이언트 ──(REST, https)──▶ 게임 서버 (PHP)   판·팀원·입장권·결과·보상·기록
        │                                   ▲
        │                                   │ 서명한 내부 요청 (판을 열 때, 시작할 때, 끝날 때)
        └────(WebSocket, wss)────▶ 실시간 서버 (.NET 8)   전투 중의 모든 것 (메모리)
```

| | 게임 서버 (`server/`, PHP) | 실시간 서버 (`realtime/`, .NET 8) |
|---|---|---|
| 맡는 일 | 누가 어느 판에 있는가, 입장권, 결과의 반영, 기록의 보관 | 적·타워·웨이브·관문·공동 예산·전술 핑 |
| 상태 | MySQL | 메모리 (방마다 `CoopSim` 하나) |
| 플레이어와의 통신 | REST | WebSocket |

### 왜 .NET 인가

전투 규칙(`client/Assets/CityGuardians/Domain/Coop/*.cs`)은 Unity 클라이언트와 **같은 C# 파일**입니다. 실시간 서버가 그 파일을 그대로 컴파일해 씁니다. 규칙을 두 언어로 따로 쓰면 어긋나기 쉽고, 연습전(클라이언트 안에서 도는 같은 시뮬레이션)과 온라인의 결과가 달라질 수 있습니다. 이전 계획의 Node.js 후보 대신 .NET 8 을 고른 이유입니다. 외부 패키지는 쓰지 않습니다 (ASP.NET Core 의 WebSocket 만).

### 서명 (`REALTIME_SECRET`)

두 서버는 같은 비밀값을 **환경변수로만** 나눠 갖습니다 (32자 이상. 저장소와 문서에 적지 않습니다). 비밀값이 없으면 협동 방어전은 열리지 않습니다.

| | 형식 | 수명 |
|---|---|---|
| 입장권 | `base64url(payload) . base64url(HMAC-SHA256)`. 판, 계정 공개 ID, 이름, 역할, 레벨, 방장 여부, 세울 수 있는 타워, 핑 차단 여부, 만료 시각, 한 번 쓰는 값(nonce) | 60초 (`ticketSeconds`). 한 번만 쓸 수 있음 |
| 내부 요청 | `X-CG-Signature: t=<시각>,v1=<HMAC-SHA256(시각 \n 방식 \n 경로 \n 본문의 SHA-256)>` | 시각이 60초 넘게 어긋나면 거절 (두 서버의 시계를 NTP 로 맞춤) |

입장권과 내부 요청은 비밀값에서 **용도별로 끌어낸 서로 다른 키**로 서명합니다. 한쪽 서명을 다른 쪽에 쓸 수 없습니다. 입장권은 주소(URL)에 싣지 않고 접속한 뒤 첫 메시지로 냅니다 (주소는 프록시 기록에 남기 때문입니다).

### 내부 API (실시간 서버 → 게임 서버)

| 요청 | 때 | 내용 |
|---|---|---|
| `GET /internal/coop/sessions/{id}/config` | 방을 열 때, 그리고 30초마다 | 구역(길·자리·웨이브), 타워·적 정의, 역할, 규칙, 팀원. 판이 닫혔는지도 이것으로 앎 |
| `POST /internal/coop/sessions/{id}/start` | 방장이 시작했을 때 | 판을 `running`으로 |
| `POST /internal/coop/sessions/{id}/result` | 판이 끝났을 때 | 결과, 팀원별 기록, 공동 예산 장부, 핑 기록. **보상 금액은 없음** |

결과를 보내지 못하면 1·2·4·8·15·30초 간격으로 다시 보내고, 60분 동안 계속합니다. 같은 결과가 다시 가도 게임 서버는 한 번만 반영하고 처음 응답을 돌려줍니다.

### 게임 서버의 결과 검증

서명을 확인한 뒤에도 내용을 다시 봅니다. 하나라도 맞지 않으면 **판 전체를 인정하지 않고** 아무에게도 보상을 주지 않습니다 (`reward_note = rejected`).

- 막은 웨이브 수가 구역의 웨이브 수를 넘지 않는가. 승리라면 모든 웨이브를 막았고 관문이 남았는가
- 남은 관문 내구도가 최대치를 넘지 않는가
- 걸린 시간: **게임 서버가 잰** 시작부터의 시간이 웨이브 수 × 최소 시간보다 짧지 않은가
- 공동 예산 장부의 앞뒤 (3장)

보상 (방어 구역 정의에서 계산):

| | GC | 경험치 |
|---|---|---|
| 승리 | 구역의 `win_gc` | 구역의 `win_xp` |
| 패배 | 막은 웨이브 × `lose_gc_per_wave` | 없음 |

- 팀원마다 같은 금액. 단, 전투 시간의 `minParticipationPercent`(30%) 넘게 연결되어 있던 사람만 (`low_participation`).
- 하루 `dailyRewardLimit`(5)번까지 (`daily_limit`). 넘어도 참여는 할 수 있습니다.
- 지갑 원장에는 혼자 하는 방어전과 같은 사유(`battle_reward`)로 남고, 근거가 `defense_session`입니다. 사업세가 켜져 있으면 같은 규칙으로 붙습니다.
- 이기면 도시의 위험도가 내려갑니다 (보상을 받은 사람마다 한 번의 승리로). **져도 올라가지 않습니다.**

## 5. 실시간 프로토콜

`GET <REALTIME_URL>` (WebSocket, 운영에서는 `wss://`만). 메시지는 JSON 텍스트이고 이름은 `snake_case`입니다. 2 KB 를 넘는 메시지, 1초에 40개를 넘는 메시지, 10초 안에 입장권을 내지 않는 연결, 45초 동안 아무것도 보내지 않는 연결은 끊습니다.

### 클라이언트 → 서버

| `type` | 값 | 뜻 |
|---|---|---|
| `join` | `ticket` | **첫 메시지.** 입장권 |
| `start` | | 방장이 전투를 시작 |
| `move` | `x`, `z` | 가고 싶은 곳. 실제 위치는 서버가 초속 6 m 로만 움직임 (순간 이동 불가) |
| `focus` | `enemy_id` | 먼저 쏠 적. 0 이면 가까운 적 |
| `build` | `request_id`, `slot`, `code` | 타워 설치 |
| `upgrade` / `repair` | `request_id`, `slot` | 강화 / 수리 (수리는 타워에서 8 m 안에 있어야 함) |
| `ping` | `kind`, `x`, `z` | 전술 핑 |
| `hide_pings` | `pid`, `hide` | 그 대원의 핑을 내 화면에서 가리기 / 다시 보기 |
| `set_role` | `role` (없으면 내려놓기) | 역할 |
| `alive` | | 15초마다. 연결 유지 |
| `leave` | | 나가기 |

### 서버 → 클라이언트

| `type` | 뜻 |
|---|---|
| `welcome` | 들어왔음. 내 ID, 판, 구역, **지도**(범위·길·자리·주요 시설), 세울 수 있는 타워, 역할, 규칙, 핑 설정, 첫 상태 |
| `state` | 초당 5번. 틱, 단계(`lobby`/`rest`/`wave`/`won`/`lost`), 웨이브, 관문 내구도, 공동 예산, 대원·적·타워·핑·위험 지역 |
| `event` | 일어난 일: `started`, `wave_started`, `wave_cleared`, `tower_built`, `tower_upgraded`, `tower_repaired`, `tower_lost`, `gate_hit`, `ping`, `role_changed`, `player_joined`, `player_left`. **해당하지 않는 값은 `null`로 보내지 않고 아예 넣지 않음** (Unity 의 `JsonUtility`가 숫자 자리의 `null`을 다루지 못함) |
| `result` | 내 `build`/`upgrade`/`repair`의 결과: `request_id`, `ok`, `reject`, `duplicate`, `energy` |
| `reject` | 그 밖의 거절: `action`, `reason` |
| `finished` | 전투 끝: 결과, 막은 웨이브, 남은 관문 내구도, 걸린 시간, 닫힌 이유 |
| `recorded` | 게임 서버에 결과가 반영되었는가: `ok`, `accepted`, `pending`(다시 보내는 중). **보상은 여기에 없음** — 클라이언트가 `GET /coop/sessions/{id}/result`로 확인 |
| `closed` | 판이 닫힘 (관리자가 닫음, 방장이 취소 등) |
| `error` | 들어오지 못함: 이유 |

거절 이유: `not_member`, `not_running`, `not_lobby`, `not_host`, `not_enough_players`, `session_full`, `late_join_closed`, `bad_slot`, `slot_taken`, `slot_empty`, `bad_structure`, `not_unlocked`, `too_many`, `max_level`, `not_damaged`, `too_far`, `insufficient_budget`, `bad_role`, `role_taken`, `bad_request`, `ping_disabled`, `ping_blocked`, `ping_muted`, `ping_rate`, `ping_kind`.

## 6. 실시간 서버의 운영

| 항목 | 내용 |
|---|---|
| 한 대만 | 방은 메모리에만 있습니다. 여러 대로 늘리려면 판마다 서버를 정해 주는 층이 필요합니다 (미구현) |
| 재시작 | 전투 중에 재시작되면 그 판은 이어 갈 수 없습니다. 다시 뜬 뒤 게임 서버에 "전투 중"으로 남은 판은 `abandoned`로 닫습니다 (`REALTIME_CLOSE_ORPHANS=1`, 보상 없음, 하루 횟수도 세지 않음). 정상 종료(SIGTERM) 때는 진행 중인 판을 `shutdown`으로 끝내고 결과를 보고합니다 |
| 한도 | 연결 2,000개, 한 주소에서 20개, 방 300개 (환경변수로 조정) |
| 접속 | TLS 는 앞단의 웹 서버(프록시)가 맡고 `wss://…/ws`를 `127.0.0.1:8090`으로 중계합니다. 새 포트를 밖으로 열지 않습니다 |
| 브라우저 | `REALTIME_ALLOWED_ORIGINS`에 있는 Origin 만. Origin 이 없는 연결(앱)은 그대로 받음 |
| 상태 확인 | `GET /health` (방과 연결의 수만) |

설치와 환경변수는 [SERVER_DEPLOYMENT](SERVER_DEPLOYMENT.md) 13장.

## 7. 클라이언트

| 것 | 파일 | 내용 |
|---|---|---|
| 연결 계층 | `Services/CoopLink.cs`, `CoopRemoteLink.cs`, `CoopWebSocket.cs` | `ICoopLink` 하나로 연습전(`LocalCoopLink`)과 온라인(`RemoteCoopLink`)을 같은 화면이 씀 |
| 연습전 | `LocalCoopLink` | 같은 `CoopSim`을 클라이언트 안에서 돌림. NPC 대원 3명 (이름 앞에 `[NPC]`). **보상도 기록도 없음** |
| 들어가는 메뉴 | `Gameplay/Coop/CoopActions.cs` | 작전 본부 → 협동 방어전 → 연습전 / 온라인 (만들기, 목록에서 참여, 진행 중인 판으로 돌아가기) |
| 전장 | `Gameplay/Coop/CoopBuilder.cs`, `CoopBattle.cs` | 서버가 준 지도로 길·자리·관문·시설을 만들고, 받은 상태대로 대원·적·타워·핑을 그림 |
| HUD | `UI/CoopHud.cs`, `MinimapPanel.cs` | 관문 내구도, 웨이브, 공동 예산, 대원 목록, 미니맵, 결과 |

조작 (전장): `E` 타워 메뉴(자리 앞에서) · `Q` 전술 핑 · `M` 전술 지도 · `F` 팀 메뉴(역할, 표시 켜고 끄기, 핑 가리기, 나가기) · `V` 전략 시점 · 마우스 왼쪽 = 보고 있는 적을 먼저 쏘기.

- 쏘는 연출과 타워의 탄은 **보이기 위한 것**입니다. 맞았는지는 서버가 정합니다.
- 내 캐릭터는 내 화면에서 바로 움직이고, 서버에는 가고 싶은 곳을 초당 몇 번 보냅니다. 서버의 내 위치는 초속 6 m 로 따라옵니다 (수리 거리 판정은 서버의 위치 기준).
- 설치 메뉴의 값은 역할 할인을 같은 식으로 계산해 보여 주는 것이고, 실제로 빠지는 값은 서버가 정합니다.
- 온라인은 실시간 서버가 맞아 준 뒤에(지도와 첫 상태를 받은 뒤에) 전장으로 넘어갑니다. 15초 안에 연결되지 않으면 그만둡니다.
- 결과 화면의 보상은 게임 서버에서 조회한 값만 보여 줍니다. 기록이 아직 반영되지 않았으면 "기록 중"으로 보입니다.

**한계:** 클라이언트의 `IGameBackend`는 모의 서버뿐이라, 온라인의 "만들기·참여·입장권"은 실제 서버에 붙여 보지 못했습니다 (모의 서버는 "준비 중"으로 답합니다). 전투 중의 연결(`RemoteCoopLink` ↔ 실시간 서버)은 실제로 붙여 테스트했습니다 (`realtime/tests/ClientLinkTests.cs`). WebGL 빌드에서는 이 소켓을 쓸 수 없습니다. `Coop` 장면은 저장소에 없고 Unity 에서 `CityGuardians/Project Setup`을 다시 돌려야 만들어집니다.

## 8. 운영 설정 (`game_config`)

`coop`: `enabled`(0), `defaultPlayers`(4), `maxPlayers`(8), `minPlayersToStart`(2), `lobbyMinutes`(15), `sessionMaxMinutes`(60), `dailyRewardLimit`(5), `hpScalePercentPerPlayer`(60), `energyScalePercentPerPlayer`(50), `minParticipationPercent`(30), `allowLateJoin`(1), `ticketSeconds`(60), `cityStructureGatePercent`(5), `playerDamage`(12), `playerDamagePerLevelPercent`(5), `playerRangeM`(14), `playerShotsPer10s`(30), `repairHp`(30), `repairEnergy`(10).

온라인 협동 방어전이 열리려면 세 가지가 모두 필요합니다: 설정 `coop.enabled = 1`, 서버의 `REALTIME_SECRET`과 `REALTIME_URL`, 방어 모드가 열려 있음. 하나라도 없으면 `GET /coop/config`가 `available: false`와 이유(`coop_disabled`, `realtime_not_configured`, `mode_closed`)를 돌려줍니다.

## 9. 테스트

- 규칙: `client/.../Tests/EditMode/CoopTests.cs` (동시 설치, 예산, 요청 번호, 이탈, 역할, 핑, 배율), `CityOpsTests.cs` (연습전, 온라인 연결 계층의 메시지 읽기)
- 실시간 서버: `realtime/tests/` — `RoomTests`(다중 접속, 같은 자리 동시 설치, 예산 중복 차감, 이탈·재접속, 핑 제한), `ClientLinkTests`(클라이언트의 `RemoteCoopLink`를 실제 소켓으로), `SignerTests`, `ContractTests`
- 게임 서버: `server/tests/CoopTest.php`, `CoopContractTest.php`(두 서버가 같은 표본을 봄), `ConcurrencyTest.php`
- 응답 형식: `server/tests/ClientWireTest.php` + `tools/check/rules/WireContractTests.cs`

[TEST_PLAN](TEST_PLAN.md) 15장.

## 10. 남은 일

- 운영 설치와 실제 사람들과의 플레이테스트 (수치는 모두 초기값).
- Unity 에서 전장과 HUD 를 실행해 확인. 화면 배치는 눈으로 보지 못했음.
- 클라이언트의 HTTP 구현 (온라인 대기실).
- 적이 플레이어를 공격하지 않음 (v1: 관문과 타워만 공격). 플레이어의 체력·쓰러짐·부활 없음.
- 도시 침공과의 연동(침공 중인 도시의 협동 방어전에 보너스), 친구 초대, 빠른 참여, 방 코드.
- 실시간 서버를 여러 대로 늘리는 구조.
- 모바일 조작 (전술 지도 터치는 됨, 전장 조작은 키보드·마우스 기준).
