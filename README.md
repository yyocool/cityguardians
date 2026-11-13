# CITY GUARDIANS — 시티가디언즈

**PROJECT ZERO: AI 도시 침공** · 개발 기준 v10.0 (v6.0 위에 v8.0의 3종 전투 모드 · 도시 위험도 · 지역 경쟁, v7.0의 CITY PASS · GUARDIANS SCHOLARSHIP, v9.0의 도시 성장 · 협동 타워디펜스 · 전술 미니맵 · 가상 지방세, v10.0의 게임 우측 실시간 3종 대시보드를 차례로 더함) · 2026-11-14

대한민국의 실제 시·군·구를 바탕으로 한 가상 도시에서 생활하고, 폭주한 AI 기계 군단으로부터 도시를 지키는 3D 도시 생활 RPG입니다.

핵심 플레이: **생활 → 탐험 → 전투 → 수익 → 소비·투자 → 성장 → 도시 방어**

가장 중요한 목표는 기능의 개수가 아니라 **재미**이고, 초등학생도 설명 없이 시작할 수 있어야 합니다.

## 현재 상태

| 항목 | 상태 |
|---|---|
| v6.0 설계 문서 | 작성 완료 (이 저장소 `docs/`) |
| Unity 클라이언트 | Phase 1 구현 (방 → 해운대구 → 로봇 사냥 → GC) + Phase 2 구현 (방어전: 출동 게이트 → 타워 건설 → 웨이브 5개 → 보상 → 폭발 타워 해금 / 대한민국 지도 화면 / 전입신고: 지도에서 도시 선택 → 가디언 등록). 자동 테스트 통과, **사람이 해 보는 플레이테스트는 아직** |
| Phase 2에서 미룬 것 | 보안 드론, 타워 영구 강화, 닉네임 직접 입력, 지도에서의 이동, 도시 랭킹, 이사 |
| Phase 3 (아이템과 경제) | 클라이언트 구현: 미소 상점 → 볼트 블래스터 구매·장착 → 원거리 사격, 수리 키트, 가방, 도시은행(맡기기·찾기·이자·거래 내역·정기예금, 은행원과 방 컴퓨터), 방어구(받는 피해 -30%까지), 옷과 입은 모습, 옷장, 알록달록 가게, 가구와 방 꾸미기, 되팔기. 자동 테스트 통과, **사람이 해 보는 플레이테스트는 아직**. 거울(얼굴·헤어), 개인 보관함, 액세서리, 강화는 미룸 |
| **v8.0** 3종 전투 모드 (디펜스 · 헌트 · FPS), 도시 위험도, 전국 지역 경쟁 | 규칙과 모의 서버: 구현, 테스트 통과 (.NET에서 272개). **Unity 게임 코드: 작성했지만 Unity가 없는 PC에서 작업해 컴파일 검사만 함 — Unity에서 실행해 보지 못했고, 기존 PlayMode 테스트도 다시 돌리지 못함.** 먼저 Unity에서 열어 확인이 필요 ([TEST_PLAN](docs/TEST_PLAN.md) 13.3, 13.4) |
| 게임 서버 (`server/`, PHP) | v8.0의 전투·도시 API 42개 경로 + v7.0의 혜택 API 18개 경로 + v9.0의 26개 경로(와 실시간 서버만 부르는 내부 경로 3개) + v10.0의 대시보드 요약 1개, 추가형 DB 마이그레이션 11개, 예약 작업, 웹 관리자(메뉴 13개 + 혜택 13개 + v9.0의 9개 + v10.0의 1개). 로컬 테스트 DB에서 테스트 310개 통과. **운영 서버에는 올리지 않음.** v6.0의 가방·상점·은행·방·임무는 서버에 없음 |
| **v7.0** 현실 혜택: CITY PASS (지역 제휴 멤버십·쿠폰), GUARDIANS SCHOLARSHIP (장학 프로그램) | 서버·홈페이지(`/city-pass`, `/scholarship`)·상점 확인 화면(`/merchant`)·관리자·모의 서버: 구현, 테스트 통과 (서버 215개, .NET 329개). **현실 혜택 기능은 모두 꺼져 있고, 켜려면 관리자에서 법률 검토 기록과 다른 관리자의 승인이 필요.** 제휴 상점과 장학 프로그램은 하나도 등록되어 있지 않음 (견본도 넣지 않음). **Unity 게임 코드(안내센터, NPC, 휴대전화 화면, 쿠폰·QR, 장학 안내)는 작성하고 컴파일 검사만 함. 브라우저로 열어 보지 못했고 QR 을 실제 카메라로 읽어 보지 못함.** 법률 검토·제휴 계약 전 ([TEST_PLAN](docs/TEST_PLAN.md) 14.4, [REAL_WORLD_REWARDS_POLICY](docs/REAL_WORLD_REWARDS_POLICY.md)) |
| **v9.0** 도시 성장(전입자 집계 · 상징거리 제작 관리), 협동 타워디펜스, 전술 미니맵, 가상 지방세 · 도시 재정 · 참여예산 | 서버·관리자·실시간 서버·규칙·모의 서버: 구현, 테스트 통과 (서버 302개, 실시간 서버 39개, .NET 규칙 385개). 게임 서버와 실시간 서버를 로컬에 띄워 두 접속으로 한 판을 돌려 봄. **지방세 3종, 참여예산, 온라인 협동 방어전은 모두 꺼져 있음** (관리자가 켬). 기본 집계 규칙은 연결된 계정만 세는데 계정 연결 기능이 아직 없어 **유효 전입자는 0명에서 움직이지 않음**. 실제 3D 상징거리 콘텐츠와 그것을 내려받는 코드는 없음 (제작·검수·배포 관리까지). **Unity 게임 코드(협동 방어전 장면, 미니맵, 도시 살림 화면)는 컴파일 검사만 함** — 게임에서 할 수 있는 것은 연습전뿐 ([TEST_PLAN](docs/TEST_PLAN.md) 15장) |
| **v10.0** 게임 우측 실시간 3종 대시보드 (내 계좌 및 투자현황 · 우리 도시 현황 · 전국 지도 현황) | 서버의 요약 API(`GET /dashboard/summary`)·관리자 설정·규칙·모의 서버: 구현, 테스트 통과 (서버 310개, .NET 규칙 415개). 서버를 로컬에 띄워 요약을 HTTP 로 확인함. 카드의 자리 잡기는 해상도 11가지를 계산으로 확인. **Unity 화면(카드 세 장, 축약 지도, 접기·펴기, 전투 모드별 자동 접기)은 컴파일 검사만 함 — 모양과 움직임을 눈으로 본 적이 없음.** 서버에 은행·가상 주식·대출이 없어 계좌 카드의 그 항목은 "준비 중"으로 보임 (관리자가 켜면 "개발용 샘플" 표시와 함께) ([TEST_PLAN](docs/TEST_PLAN.md) 16장) |
| 실시간 서버 (`realtime/`, .NET 8) | v9.0에서 처음 만듦. 협동 방어전의 전투 상태와 전술 핑을 WebSocket 으로 동기화 (서버 권한). 한 대만 도는 구조. **운영 서버에는 설치하지 않음** |
| 게임 ↔ 서버 | 연결되지 않음. 클라이언트는 여전히 `LocalMockBackend` 로 동작 (HTTP 구현 없음). v9.0과 v10.0의 응답 형식은 표본으로 양쪽에서 대조하고, 전투 중의 WebSocket 접속 코드만 실제 실시간 서버에 붙여 테스트함 |
| 운영 서버 | 이전 버전(좀비 테마) 홈페이지와 `/api/v1` v1.0.0 운영 중. v8.0, v7.0, v9.0, v10.0 작업은 운영 서버에 접속하지 않았고 아무것도 바꾸지 않음 |
| 운영 서버 내부 조사 | 미완료 — 접속 정보 필요 (`docs/SERVER_DEPLOYMENT.md` 2장) |

상세한 구현 기록과 설계 대비 변경은 [DEVELOPMENT_PLAN](docs/DEVELOPMENT_PLAN.md)의 "Phase 1 구현 기록", "Phase 2 구현 기록 ①, ②", "Phase 3 구현 기록", "Phase 3 구현 기록 ②", "v8.0 구현 기록", "v7.0 구현 기록", "v9.0 구현 기록", "v10.0 구현 기록"에 있습니다. 수치는 플레이테스트로 조정할 초기값입니다.

## 실행해 보기

Unity 6000.3.28f1이 필요합니다. 저장소 루트에서 PowerShell로 실행합니다.

```powershell
# 처음 한 번: 재질과 장면 파일을 만들고 빌드 목록에 등록
.\tools\unity\run-unity.ps1 -LogName setup -UnityArgs '-quit','-executeMethod','CityGuardians.EditorTools.ProjectSetup.Run'

# 테스트
.\tools\unity\run-unity.ps1 -LogName editmode -UnityArgs '-runTests','-testPlatform','EditMode','-testResults',"$PWD\client\Logs\editmode.xml"
.\tools\unity\run-unity.ps1 -LogName playmode -Graphics -UnityArgs '-runTests','-testPlatform','PlayMode','-testResults',"$PWD\client\Logs\playmode.xml"

# Windows 개발 빌드 → builds\windows\CityGuardians.exe
.\tools\unity\run-unity.ps1 -LogName build -UnityArgs '-quit','-executeMethod','CityGuardians.EditorTools.BuildScript.BuildWindowsDev'
```

Unity 에디터에서 해 보려면 `client/` 를 열고 `Assets/CityGuardians/Scenes/Boot.unity` 에서 재생합니다.

조작: W A S D 이동 · 마우스 시점 · Shift 달리기 · Space 점프 · 마우스 왼쪽 공격 · E 상호작용 · 숫자 1~6 메뉴에서 고르기 · 걸어 다닐 때 숫자 1 근접 무기 / 2 원거리 무기 / 3 수리 키트 · I 가방 · M 지도 · 방에서 F 방 꾸미기 (꾸미는 중 E 고르기·놓기, R 돌리기, F 그만두기) · Esc 일시정지 (일시정지 중 Q 종료)

상점과 은행 해 보기: 광장 동쪽에 미소 상점(오미소)과 도시은행(은다온)이 있습니다. 로봇을 잡아 200 GC를 모으면 목표 줄이 상점을 가리킵니다. 은행은 레벨 3부터 열리고, 그 뒤로는 내 방 컴퓨터에서도 열 수 있습니다. 이자는 한국 시간 자정이 지나면 들어옵니다 (통장의 10,000 GC까지 하루 1%). 정기예금은 7일(이자 10%)과 30일(이자 50%) 두 가지이고 만기 전에 찾으면 원금만 돌아옵니다.

옷과 방 꾸미기 해 보기: 미소 상점에서 가디언 헬멧·조끼(방어구)를, 광장 남동쪽 알록달록 가게(한아름)에서 옷과 가구를 삽니다. 옷은 가방(I)이나 내 방 옷장에서 갈아입습니다. 가구를 산 뒤 방에서 F를 누르면 가구를 들고 걸어 다니며 놓을 수 있습니다 (바닥 칸이 초록이면 E). 산 물건은 그 가게에서 산 값의 30%에 되팔 수 있습니다.

방어전 해 보기: 방에서 나와 도담·방어대장(광장)과 대화를 마친 뒤, 도시 서쪽 방어기지 문 앞의 출동 게이트에서 E.

지도와 전입신고 해 보기: 방어대장과의 대화를 마친 뒤부터 M 키로 대한민국 지도가 열립니다 (마우스로 시·도 → 시·군·구, 휠 확대, 끌어서 이동, 오른쪽 버튼이나 Esc로 뒤로). 전입신고는 광장 서쪽 주민센터 앞의 정나래에게 E → 숫자 1 → 지도에서 도시를 고르고 E → 이름 고르기.

지도 경계 데이터의 출처와 이용 조건은 [MAP_SYSTEM](docs/MAP_SYSTEM.md) 2.1에 있습니다 (통계청 SGIS 행정동 경계, 공공누리 제1유형 / vuski/admdongkor 가공본, CC BY 4.0). 지도 화면에 출처가 표시됩니다.

게임 진행은 `%USERPROFILE%\AppData\LocalLow\CityGuardians\CITY GUARDIANS\dev_local` 에 저장됩니다. 처음부터 다시 하려면 이 폴더를 지웁니다.

### v8.0 전투 모드 (Unity에서 아직 실행해 보지 못함)

| 모드 | 들어가는 곳 | 조작 |
|---|---|---|
| 디펜스 | 방어기지의 출동 게이트 → "방어전 출동" | 기존 방어전 + V 시점 바꾸기 (3인칭 ↔ 전략) |
| 헌트 | 도시의 전투 구역 | 기존 사냥 그대로 |
| FPS | 방어기지의 작전 단말기 → "시내 긴급 출동" | W A S D · 마우스 시점 · 왼쪽 공격 · 오른쪽 조준 · R 재장전 · Space 점프 · Shift 달리기 · E 상호작용 · Q 보조 장치 · F 지원 장비 |

장비 구매·강화·장착, 시설 복구, 도시 기금, 지원 출동은 작전 단말기에서, 도시별 위험도와 랭킹은 지도(M)에서 봅니다. 처음 열 때는 위의 `ProjectSetup.Run`을 다시 실행해 `Fps` 장면을 빌드 목록에 넣어야 합니다.

### v7.0 현실 혜택 (Unity에서 아직 실행해 보지 못함)

도시 남쪽, 바다로 가는 길의 동쪽에 CITY PASS 안내센터(윤서윤 매니저, 이가람 안내원), 서쪽에 장학센터(문세빈 선생님, 한루리 안내원)가 있습니다. 방어대장과의 대화를 마친 뒤부터 E 로 말을 걸 수 있고, 같은 화면을 휴대전화(P 키)에서도 엽니다.

게임은 모의 서버로 동작하고 **모의 서버의 현실 혜택 기능은 처음에 모두 닫혀 있어 모든 메뉴가 "준비 중"으로 보입니다.** 제휴 상점·쿠폰·장학 공고의 견본은 들어 있지 않습니다. 게임 안에는 쿠폰을 GC 로 사거나 바꾸는 기능, 장학금을 신청하거나 받는 기능이 없습니다 (장학 신청은 홈페이지에서).

### v9.0 협동 방어전과 도시 살림 (Unity에서 아직 실행해 보지 못함)

방어기지의 작전 단말기(작전 본부)에 메뉴 두 개가 늘었습니다. 처음 열 때는 위의 `ProjectSetup.Run`을 다시 실행해 `Coop` 장면을 만들어야 합니다.

| 메뉴 | 내용 |
|---|---|
| 협동 방어전 | **연습전**: 서버 없이 `[NPC]` 대원 3명과 함께 도시 외곽의 관문을 지킴. 보상도 기록도 없음. **온라인**: 모의 서버에서는 "준비 중" (실제 서버와 실시간 서버가 있어야 함) |
| 도시 살림 | 내 도시의 전입자 수와 확장 단계, 이사, 지방세(꺼져 있음), 도시 재정, 참여예산 |

전장의 조작: W A S D 이동 · `E` 타워 메뉴(자리 앞에서 설치·강화·수리) · `Q` 전술 핑 · `M` 전술 지도 · `F` 팀 메뉴(역할, 적 이동 경로·사거리 표시, 핑 가리기, 나가기) · `V` 전략 시점 · 마우스 왼쪽 = 보고 있는 적을 먼저 쏘기. 화면 오른쪽 위에 미니맵이 있습니다.

지방세는 GC 로만 걷는 **게임 안의 가상 세금**이고 현실의 돈·세금과 이어지지 않습니다. 도시 재정은 CITY PASS · 장학의 예산과 섞이지 않습니다.

### v10.0 실시간 대시보드 (Unity에서 아직 실행해 보지 못함)

첫 보상을 받은 뒤(GC 표시가 나타날 때)부터 화면 오른쪽에 카드 세 장이 보입니다: **내 계좌**, **우리 도시**, **전국 현황**.

| 키 | 일 |
|---|---|
| `F6` | 대시보드 전체 보이기 / 숨기기 |
| `F7` / `F8` / `F9` | 내 계좌 / 우리 도시 / 전국 현황 카드 접기 ↔ 펴기 |
| `Shift` + `F7` / `F8` / `F9` | 상세 보기 (은행·주식·대출·자산 변동 / 도시 방어·재정·성장·순위·지원 / 확대·축소하는 전국 지도) |
| `Ctrl` + `F7` / `F8` / `F9` | 그 카드만 숨기기 / 다시 보이기 |
| `Shift` + `F6` | 대시보드 설정: 글자와 카드 크기(80~140%), 타워디펜스에서 카드 자리 |
| 왼쪽 `Alt` (누르고 있는 동안) | 커서가 풀려 카드를 마우스로 누름 (머리 줄 = 접기·펴기, 몸통 = 상세, 오른쪽 버튼 = 숨기기) |

FPS 에서는 세 장이 스스로 접히고, 타워디펜스에서는 접히거나 고른 자리에 놓이며, 전투가 끝나면 이전 상태로 돌아옵니다. 접어 두거나 숨겨 두어도 우리 도시의 침공·긴급 알림은 보입니다. 카드의 상태는 `dev_local` 폴더의 `dashboard_prefs.json`에 저장됩니다.

모의 서버에는 가상 주식과 대출이 없어 그 줄에는 **"개발용 샘플"** 꼬리표가 붙은 지어낸 값이 보입니다 (순자산에 더해지지 않음). 실제 서버의 기본값은 샘플 없이 "준비 중"입니다. 설명은 [LIVE_DASHBOARD](docs/LIVE_DASHBOARD.md).

### Unity 없이 확인하기

.NET SDK 8만 있으면 됩니다.

```powershell
# 규칙 테스트 (Domain, 모의 서버)
cd tools\check\rules; dotnet test

# Unity 코드 형식 검사 (컴파일만. 처음 한 번 참조용 어셈블리를 내려받음)
powershell -File tools\check\get-refs.ps1
cd tools\check\unity; dotnet build

# 실시간 서버 테스트 (v9.0. 127.0.0.1 의 빈 포트에 서버를 띄워 실제 WebSocket 으로 확인, 약 30초)
cd realtime\tests; dotnet test
```

### 게임 서버 (`server/`)

PHP 7.3 이상(64비트, `pdo_mysql`, `mbstring`)과 MySQL 5.7 이상 또는 MariaDB가 필요합니다. 설정은 `server/.env.example`을 `server/.env`로 복사해 채웁니다 (`.env`는 저장소에 넣지 않습니다).

```powershell
cd server
php bin\migrate.php            # 상태와 사전 점검만. 아무것도 바꾸지 않음
php bin\migrate.php --apply    # 적용 (dev / test 환경)
php -S 127.0.0.1:8088 -t public    # 로컬에서 열어 보기: /api/v1/health, /admin, /city-pass, /scholarship, /merchant (로컬에서는 .env 에 ADMIN_COOKIE_SECURE=0, WEB_COOKIE_SECURE=0)
php bin\create-admin.php <아이디> super    # 관리자 계정 (일회용 코드 앱 필요)
php bin\tick.php               # 침공 판정, 쿠폰 만료, 보관 기간이 끝난 장학 신청의 개인정보 삭제, v9.0의 전입자 집계·재산세·투표 확정·기록 점검 (예약 작업)
php bin\new-data-key.php       # 장학 신청서 암호화 키 만들기 → .env 의 DATA_KEY (저장소·문서에 적지 않음)

# 테스트: 이름이 _test 로 끝나는 빈 DB 와 CG_TEST_DB_HOST / PORT / USER / PASS / NAME 환경변수
php tests\run.php
```

**운영 서버에 올리기 전에** [SERVER_DEPLOYMENT](docs/SERVER_DEPLOYMENT.md) 11.2, 12.2, 13.2를 먼저 확인합니다. 운영 서버에는 기존 `/api/v1`과 DB가 있고, 그대로 올리면 기존 API를 바꿔치기하게 됩니다. v7.0은 `/login`, `/assets` 같은 경로도 씁니다.

현실 혜택(멤버십, 쿠폰, 장학 신청, 장학 지급 기록)은 서버를 올려도 **꺼진 채**입니다. 켜는 것은 배포가 아니라 운영 결정이고, 법률 검토와 제휴 계약이 끝나기 전에는 켜지 않습니다. 실사용자에게 쿠폰을 내거나 장학 신청을 받는 것은 따로 사전 승인이 필요합니다 ([SERVER_DEPLOYMENT](docs/SERVER_DEPLOYMENT.md) 12.1, 12.5).

### 실시간 서버 (`realtime/`, v9.0)

협동 방어전을 온라인으로 열려면 게임 서버 옆에 실시간 서버가 떠 있어야 합니다. .NET 8 (ASP.NET Core 런타임)이 필요하고 설정은 환경변수로만 받습니다.

```powershell
# 두 서버가 같은 서명 키를 가져야 한다 (32자 이상의 무작위 값. 저장소·문서에 적지 않음)
#   게임 서버 .env:  REALTIME_SECRET=…   REALTIME_URL=ws://127.0.0.1:8090/ws   (운영에서는 wss:// 만)
#   실시간 서버 환경: REALTIME_SECRET=…   GAME_API_URL=http://127.0.0.1:8088/api/v1
cd realtime\src; dotnet run -c Release      # http://127.0.0.1:8090 (/health, /ws)
```

그 뒤 관리자 화면의 "협동 디펜스 모니터링"에서 협동 방어전을 켭니다. 서명 키가 없거나 꺼져 있으면 `GET /api/v1/coop/config`가 `available: false`와 이유를 돌려줍니다. 설치·중계·재시작의 영향은 [SERVER_DEPLOYMENT](docs/SERVER_DEPLOYMENT.md) 13.4, 구조와 프로토콜은 [COOP_DEFENSE_SYSTEM](docs/COOP_DEFENSE_SYSTEM.md).

v9.0의 지방세, 참여예산, 협동 방어전도 서버를 올려도 **꺼진 채**입니다. 켜면 플레이어의 GC 가 움직이므로 따로 승인이 필요합니다 ([SERVER_DEPLOYMENT](docs/SERVER_DEPLOYMENT.md) 13.8).

## 플랫폼

1. Windows PC (Unity 6 LTS, C#, URP) — 현재 목표
2. Android
3. iOS

## 문서

| 문서 | 내용 |
|---|---|
| [GAME_DESIGN](docs/GAME_DESIGN.md) | 게임 목표, 핵심 루프, 시스템 구성, 1차 범위 |
| [WORLD_STORY](docs/WORLD_STORY.md) | PROJECT ZERO 세계관, 적 세력, 인물, 표현 수위 |
| [PLATFORM_STRATEGY](docs/PLATFORM_STRATEGY.md) | Windows 우선 전략, 모바일 확장 대비 |
| [USER_FLOW](docs/USER_FLOW.md) | 첫 플레이와 주요 화면 흐름 |
| [CHARACTER_SYSTEM](docs/CHARACTER_SYSTEM.md) | 블록형 캐릭터, 동작, 커스터마이징 |
| [ROOM_SYSTEM](docs/ROOM_SYSTEM.md) | 개인 방, 가구, 컴퓨터 |
| [MAP_SYSTEM](docs/MAP_SYSTEM.md) | 대한민국 지도, 3D 도시 연결 |
| [COMBAT_SYSTEM](docs/COMBAT_SYSTEM.md) | 3D 액션 전투, 무기, 보상 검증 |
| [TOWER_DEFENSE](docs/TOWER_DEFENSE.md) | 도시 방어전, 타워, 웨이브 |
| [DEFENSE_MODE](docs/DEFENSE_MODE.md) | v8.0 디펜스 모드: 전략 시점, 방어시설 15종, 개인·도시 자산 |
| [HUNT_MODE](docs/HUNT_MODE.md) | v8.0 헌트 모드: 사냥과 도시 위험도 |
| [FPS_MODE](docs/FPS_MODE.md) | v8.0 FPS 모드: 시내 침입 대응 임무 |
| [CITY_RISK_SYSTEM](docs/CITY_RISK_SYSTEM.md) | 도시 위험도 계산, 단계, 침공, 이력, 정책 |
| [REGIONAL_COMPETITION](docs/REGIONAL_COMPETITION.md) | 지역 랭킹, 도시 간 지원 |
| [EQUIPMENT_SYSTEM](docs/EQUIPMENT_SYSTEM.md) | 전투 장비 8종, 구매·강화·장착 |
| [COMBAT_ECONOMY](docs/COMBAT_ECONOMY.md) | 전투의 수입과 지출, 개인 금융과의 분리, 한도 |
| [ENEMY_AI](docs/ENEMY_AI.md) | AI 기계 군단 6종, FSM 구조, 좀비 기획 이행 |
| [NPC_SYSTEM](docs/NPC_SYSTEM.md) | NPC 역할, 행동, 대화 |
| [RESIDENCY_SYSTEM](docs/RESIDENCY_SYSTEM.md) | 이방인, 전입신고, 계정 연결 |
| [ITEM_SYSTEM](docs/ITEM_SYSTEM.md) | 아이템, 상점, 인벤토리, 장착 |
| [ECONOMY_SYSTEM](docs/ECONOMY_SYSTEM.md) | GC, 원장, 은행, 저축 |
| [STOCK_SYSTEM](docs/STOCK_SYSTEM.md) | 가상 주식 |
| [LOAN_CREDIT_SYSTEM](docs/LOAN_CREDIT_SYSTEM.md) | 대출, 신용점수 |
| [COMMUNITY_SYSTEM](docs/COMMUNITY_SYSTEM.md) | 채팅, 친구, 파티, 신고·차단 |
| [REAL_WORLD_REWARDS_POLICY](docs/REAL_WORLD_REWARDS_POLICY.md) | v7.0 현실 혜택 운영 정책 (초안): 게임 보상과의 분리, 하지 않는 것, 기능 승인 절차, 법률 검토 항목 |
| [CITY_PASS_SYSTEM](docs/CITY_PASS_SYSTEM.md) | v7.0 CITY PASS: 멤버십, 관심 지역, 게임·홈페이지 화면 |
| [PARTNER_MERCHANT_SYSTEM](docs/PARTNER_MERCHANT_SYSTEM.md) | v7.0 제휴 상점·지점·혜택의 등록과 승인, 상점 계정 |
| [COUPON_SYSTEM](docs/COUPON_SYSTEM.md) | v7.0 쿠폰 발급, 일회용 코드와 QR, 상점의 사용 처리, 중복 사용 방지 |
| [SCHOLARSHIP_SYSTEM](docs/SCHOLARSHIP_SYSTEM.md) | v7.0 장학 프로그램, 신청, 보호자 동의, 지급 상태 |
| [SCHOLARSHIP_REVIEW_POLICY](docs/SCHOLARSHIP_REVIEW_POLICY.md) | v7.0 장학 심사 정책 (초안): 쓰는 것과 쓰지 않는 것, 역할 분리, 절차 |
| [PRIVACY_DATA_FLOW](docs/PRIVACY_DATA_FLOW.md) | v7.0 개인정보의 흐름, 분리, 암호화, 보관 기간, 접근 권한 |
| [CITY_PASS_API](docs/CITY_PASS_API.md) | v7.0 CITY PASS API |
| [SCHOLARSHIP_API](docs/SCHOLARSHIP_API.md) | v7.0 장학 API |
| [CITY_GROWTH_SYSTEM](docs/CITY_GROWTH_SYSTEM.md) | v9.0 도시별 유효 전입자 집계, 이사, 확장 단계, 기준 도달 알림 |
| [CITY_MODELING_PIPELINE](docs/CITY_MODELING_PIPELINE.md) | v9.0 상징거리 3D 콘텐츠의 제작 → 검수 → 배포 절차, 지역 콘텐츠 배포 구조 |
| [COOP_DEFENSE_SYSTEM](docs/COOP_DEFENSE_SYSTEM.md) | v9.0 협동 타워디펜스: 역할, 공동 예산, 실시간 서버, 프로토콜 |
| [TACTICAL_MINIMAP](docs/TACTICAL_MINIMAP.md) | v9.0 전술 미니맵과 전술 핑, 남용 방지 |
| [LIVE_DASHBOARD](docs/LIVE_DASHBOARD.md) | v10.0 게임 우측 실시간 3종 대시보드: 카드 상태, 전투 모드 연동, 데이터 갱신, 알림, 관리자 설정 |
| [FINANCE_DASHBOARD](docs/FINANCE_DASHBOARD.md) | v10.0 카드 1 내 계좌 및 투자현황: 서버에 없는 항목과 개발용 샘플, 개인 금융 정보 보호 |
| [CITY_STATUS_DASHBOARD](docs/CITY_STATUS_DASHBOARD.md) | v10.0 카드 2 우리 도시 현황: 위험 단계 표시와 경고, 상세 보기 |
| [NATIONAL_MAP_DASHBOARD](docs/NATIONAL_MAP_DASHBOARD.md) | v10.0 카드 3 전국 지도 현황: 축약 지도, 경계 데이터의 출처와 라이선스 |
| [HUD_LAYOUT_SYSTEM](docs/HUD_LAYOUT_SYSTEM.md) | v10.0 대시보드의 디자인 시스템, 자리 잡기, 움직임, 키, 설정 저장, 해상도별 확인 |
| [CITY_TAX_SYSTEM](docs/CITY_TAX_SYSTEM.md) | v9.0 가상 지방세 (거래세, 사업세, 재산세), 초보자 보호, 중복 징수 방지 |
| [CITY_TREASURY_SYSTEM](docs/CITY_TREASURY_SYSTEM.md) | v9.0 도시 재정 원장, 사용처와 집행, 공개 |
| [PARTICIPATORY_BUDGET](docs/PARTICIPATORY_BUDGET.md) | v9.0 참여예산: 제안 → 검토 → 주민 투표 → 집행 → 공개 |
| [DATABASE](docs/DATABASE.md) | MySQL 스키마, 마이그레이션과 롤백 계획 |
| [API_SPEC](docs/API_SPEC.md) | PHP API 명세 |
| [ADMIN_SYSTEM](docs/ADMIN_SYSTEM.md) | 웹 관리자 |
| [WEBSITE_SPEC](docs/WEBSITE_SPEC.md) | 공식 홈페이지 |
| [SERVER_DEPLOYMENT](docs/SERVER_DEPLOYMENT.md) | 서버 현황, 환경 분리, 배포, 승인 항목 |
| [SECURITY_POLICY](docs/SECURITY_POLICY.md) | 보안, 미성년자 보호, 개인정보 |
| [DEVELOPMENT_PLAN](docs/DEVELOPMENT_PLAN.md) | 기술 선택, 저장소 구조, 단계별 계획 |
| [TEST_PLAN](docs/TEST_PLAN.md) | 테스트 계획, 1차 완료 기준 점검표 |

처음 읽는다면 GAME_DESIGN → DEVELOPMENT_PLAN → SERVER_DEPLOYMENT 순서를 권합니다.

## 저장소 구조

```
cityguardians/
  docs/        설계 문서
  client/      Unity 프로젝트
  server/      PHP 게임 서버: API · 관리자 · DB 마이그레이션 · 예약 작업 · 테스트 (v8.0) + CITY PASS · 장학의 홈페이지 페이지와 상점 확인 화면 (v7.0. 공식 홈페이지 전체는 아직 없음)
  realtime/    협동 방어전의 실시간 서버 (v9.0, .NET 8 WebSocket): src/ · tests/. 전투 규칙은 client 의 Domain 소스를 그대로 컴파일
  tools/       unity/ Unity 실행 스크립트 · map/ 지도 경계 데이터 가공 (Node.js) · check/ Unity 없이 검증 (.NET) · (앞으로: 배포 스크립트)
  builds/      빌드 결과물 (git에 넣지 않음)

  (계획, 아직 없음)
  data/        밸런스·콘텐츠 초기 데이터
```

## 지켜야 할 원칙

- 서버 비밀번호, DB 계정, API 키는 저장소와 문서에 기록하지 않습니다. 환경변수와 서버의 `.env`로만 관리합니다.
- 운영 서버의 기존 서비스와 데이터베이스를 삭제하거나 덮어쓰지 않습니다. 기존 서비스에 영향을 주는 변경은 사전 승인을 받습니다.
- GC, 아이템, 금융 거래는 모두 서버에서 검증하고 원장에 기록합니다.
- GC는 현실 화폐로 교환할 수 없고, 게임 내 신용점수는 현실 신용평가와 무관합니다.
- 현실 혜택(CITY PASS, 장학)은 게임 보상이 아닙니다. GC, 전투 성적, 랭킹, 아이템, 플레이 시간, 결제액으로 쿠폰이나 장학금을 받을 수 없고, 쿠폰과 장학금을 GC 로 바꿀 수 없습니다. 법률 검토와 운영 승인이 끝나지 않은 현실 혜택 기능은 꺼 둡니다.
- 제휴하지 않은 상점이나 준비되지 않은 혜택·장학 프로그램을 제공 중인 것처럼 표시하지 않습니다.
- 지방세는 GC 로만 걷는 게임 안의 가상 세금입니다. 실제 현금이나 현실의 세금과 연결하지 않고, 게임 안의 도시 재정을 CITY PASS · 장학의 예산과 섞지 않습니다. 참여예산의 표는 GC 나 낸 세금과 상관없이 계정마다 한 표입니다.
- 실시간 전투의 상태(적, 타워, 웨이브, 관문, 공동 예산)는 서버가 정합니다. 클라이언트는 요청만 보냅니다.
- 대시보드는 보여 주기만 합니다. 자산과 부채는 서버에서 받고, 클라이언트가 금융 거래의 결과를 스스로 정하지 않습니다. 개인의 금융 정보는 본인에게만 내려가고 관리자 화면에 불필요하게 내보이지 않습니다. 아직 없는 기능의 값은 "준비 중"이나 "개발용 샘플"로 구분해 보입니다.
- NPC는 실제 플레이어로 위장하지 않습니다.
- 다른 게임의 캐릭터와 에셋을 복제하지 않습니다.
