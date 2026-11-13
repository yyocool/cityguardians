# CITY GUARDIANS — 시티가디언즈

**PROJECT ZERO: AI 도시 침공** · 개발 기준 v6.0 · 2026-11-13

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
| v6.0 서버 코드 | 미착수. 클라이언트는 서버 대신 `LocalMockBackend` 로 동작 |
| 운영 서버 | 이전 버전(좀비 테마) 홈페이지와 `/api/v1` v1.0.0 운영 중 |
| 운영 서버 내부 조사 | 미완료 — 접속 정보 필요 (`docs/SERVER_DEPLOYMENT.md` 2장) |

상세한 구현 기록과 설계 대비 변경은 [DEVELOPMENT_PLAN](docs/DEVELOPMENT_PLAN.md)의 "Phase 1 구현 기록", "Phase 2 구현 기록 ①, ②", "Phase 3 구현 기록", "Phase 3 구현 기록 ②"에 있습니다. 수치는 플레이테스트로 조정할 초기값입니다.

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
| [ENEMY_AI](docs/ENEMY_AI.md) | AI 기계 군단 6종, FSM 구조, 좀비 기획 이행 |
| [NPC_SYSTEM](docs/NPC_SYSTEM.md) | NPC 역할, 행동, 대화 |
| [RESIDENCY_SYSTEM](docs/RESIDENCY_SYSTEM.md) | 이방인, 전입신고, 계정 연결 |
| [ITEM_SYSTEM](docs/ITEM_SYSTEM.md) | 아이템, 상점, 인벤토리, 장착 |
| [ECONOMY_SYSTEM](docs/ECONOMY_SYSTEM.md) | GC, 원장, 은행, 저축 |
| [STOCK_SYSTEM](docs/STOCK_SYSTEM.md) | 가상 주식 |
| [LOAN_CREDIT_SYSTEM](docs/LOAN_CREDIT_SYSTEM.md) | 대출, 신용점수 |
| [COMMUNITY_SYSTEM](docs/COMMUNITY_SYSTEM.md) | 채팅, 친구, 파티, 신고·차단 |
| [DATABASE](docs/DATABASE.md) | MySQL 스키마 |
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
  tools/       unity/ Unity 실행 스크립트 · map/ 지도 경계 데이터 가공 (Node.js) · (앞으로: 배포 스크립트)
  builds/      빌드 결과물 (git에 넣지 않음)

  (계획, 아직 없음)
  server/      PHP 애플리케이션 (API · 홈페이지 · 관리자), DB 마이그레이션
  realtime/    WebSocket 채팅 서비스 (Phase 5)
  data/        밸런스·콘텐츠 초기 데이터
```

## 지켜야 할 원칙

- 서버 비밀번호, DB 계정, API 키는 저장소와 문서에 기록하지 않습니다. 환경변수와 서버의 `.env`로만 관리합니다.
- 운영 서버의 기존 서비스와 데이터베이스를 삭제하거나 덮어쓰지 않습니다. 기존 서비스에 영향을 주는 변경은 사전 승인을 받습니다.
- GC, 아이템, 금융 거래는 모두 서버에서 검증하고 원장에 기록합니다.
- GC는 현실 화폐로 교환할 수 없고, 게임 내 신용점수는 현실 신용평가와 무관합니다.
- NPC는 실제 플레이어로 위장하지 않습니다.
- 다른 게임의 캐릭터와 에셋을 복제하지 않습니다.
