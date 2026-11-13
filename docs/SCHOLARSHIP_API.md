# SCHOLARSHIP_API — 장학 API

> 기준 v7.0 · 2026-11-13 · 상태: **서버(`server/`)에 구현, 로컬 테스트 DB에서 테스트 통과 (`BenefitApiTest`, `ScholarshipTest`). 운영 서버에는 배포하지 않음.** Unity 클라이언트는 아직 이 API를 부르지 않습니다 (모의 서버만 씀).

공통 규칙은 [`API_SPEC.md`](API_SPEC.md) 0장과 [`CITY_PASS_API.md`](CITY_PASS_API.md) 1장. 관련: [`SCHOLARSHIP_SYSTEM.md`](SCHOLARSHIP_SYSTEM.md), [`SCHOLARSHIP_REVIEW_POLICY.md`](SCHOLARSHIP_REVIEW_POLICY.md), [`PRIVACY_DATA_FLOW.md`](PRIVACY_DATA_FLOW.md).

---

## 1. 공통

| 항목 | 내용 |
|---|---|
| 인증 | **모든 경로**에 `Authorization: Bearer <접속 토큰>`. 공고 목록도 토큰이 필요합니다 (로그인 없이 보는 공고는 홈페이지 `/scholarship`) |
| 본인 것만 | 신청은 토큰의 주인 것만. 남의 신청 번호는 `404 NOT_FOUND` |
| 게임 데이터 | 이 경로들은 지갑·원장·전투·랭킹을 읽지 않는다. 요청에 `game_level`, `gc`, `rank`, `status`, `result`를 덧붙여 보내도 읽지 않는다 |
| 결과 | 발표된 뒤에만 응답에 나온다. 그 전에는 `result: null` |
| 개인정보 | 응답에 신청서의 내용, 이름, 연락처가 **없다.** 신청의 상태만 돌려준다 |

| 메서드 | 경로 | 내용 | 필요한 기능 |
|---|---|---|---|
| GET | `/scholarships` | 공고 목록 | — (꺼져 있으면 빈 목록) |
| GET | `/scholarships/{id}` | 공고 상세 | `scholarship_applications` |
| POST | `/scholarships/{id}/applications` | 신청 | `scholarship_applications` + 암호화 키 |
| GET | `/scholarships/my-applications` | 내 신청 목록 | — |
| GET | `/scholarships/applications/{id}` | 내 신청 하나 | — |
| POST | `/scholarships/applications/{id}/withdraw` | 철회 | — |
| POST | `/scholarships/applications/{id}/appeal` | 이의신청 | — |
| POST | `/scholarships/applications/{id}/guardian-link` | 보호자 확인 주소 다시 받기 | `scholarship_applications` |

지시서의 다섯 경로가 모두 있고, 철회·이의신청·보호자 주소 재발급을 더했습니다.

**없는 경로** (불러도 `404`/`400`): 결과를 정하거나 바꾸는 것(`…/result`, `…/award`, `…/review`), 지급(`…/disburse`), 낸 신청서를 고치는 것(`PUT …/applications/{id}`). 심사·선정·지급 상태는 관리자 화면에서만 다룹니다 (7장).

**게임은 신청 경로를 쓰지 않습니다.** 게임 안에서는 안내와 상태 확인만 하고(`GET` 셋), 신청은 홈페이지에서 합니다. `POST /scholarships/{id}/applications`는 홈페이지와 같은 서비스를 부르는 API 로 있을 뿐이고, `IGameBackend`에는 대응하는 요청이 없습니다.

## 2. 공고

### `GET /scholarships`

공고가 승인된 프로그램(접수 중·마감·결과 발표)입니다. 초안·승인 대기·보관된 것은 나오지 않습니다.

```json
{
  "applications_enabled": true,
  "notice": "",
  "site_url": "https://…/scholarship",
  "programs": [
    {
      "id": "s_8f3k2m9x7q2zab",
      "category": "finance", "category_name": "금융 역량 장학",
      "title": "…", "summary": "…",
      "support_type": "books", "support_type_name": "도서·교재 지원", "support_detail": "…",
      "capacity": 10,
      "apply_starts_at": "…", "apply_ends_at": "…",
      "result_on": "2027-01-15",
      "phase": "open"
    }
  ]
}
```

| 값 | 내용 |
|---|---|
| `category` | `finance`(금융 역량), `creativity`(창의력), `leadership`(협력·리더십), `growth`(성장 지원) |
| `support_type` | `education_fee`(교육비), `books`(도서·교재), `online_pass`(온라인 교육 이용권), `project`(학습 프로젝트), `grant`(별도 장학금) |
| `phase` | `upcoming`(모집 예정), `open`(모집 중), `closed`(마감), `announced`(결과 발표) |
| `site_url` | 신청하는 홈페이지. `PUBLIC_BASE_URL`이 없으면 `null` |

기능이 꺼져 있으면 `applications_enabled: false`, `programs: []`, `notice: "장학 프로그램은 준비 중이에요…"`.

### `GET /scholarships/{id}`

목록의 값에 더해:

```json
{
  "description": "…",
  "eligibility": [ { "id": 3, "type": "school_level", "description": "중학생 또는 고등학생" } ],
  "criteria": [ { "name": "계획의 구체성", "description": "…", "max_score": 10, "weight": 3 } ],
  "guardian_consent_age": 19,
  "appeal_days": 7,
  "privacy": {
    "version": "2026-11-draft",
    "items": [ { "what": "이름, 태어난 해, 학교급, 학교 이름", "why": "지원 자격 확인", "who": "장학 담당자" } ],
    "not_collected": "주민등록번호, 계좌번호, 주소는 받지 않습니다. …",
    "retention": "선정되지 않은 신청은 결과 발표 뒤 180일, …",
    "draft": "이 동의서는 초안입니다. 법률 검토를 거쳐 확정됩니다."
  },
  "apply_url": "https://…/scholarship/apply/s_8f3k2m9x7q2zab",
  "not_used_in_review": "게임 플레이 기록, 전투 성적, 도시 랭킹, 게임머니, 유료 결제 내역은 자격 확인과 심사에 쓰이지 않아요."
}
```

- `eligibility[].type`은 `school_level`, `birth_year`, `statement`, `document` 넷뿐입니다.
- `eligibility[].id`는 신청할 때 `form.statements`에 넣는 값입니다 (`statement` 종류).
- 오류: `403 FEATURE_DISABLED`, `400 INVALID_REQUEST`(번호 형식), `404 NOT_FOUND`.

## 3. 신청

### `POST /scholarships/{id}/applications`

```json
{
  "applicant": {
    "name": "홍길동", "birth_year": 2010, "school_level": "middle",
    "school_name": "○○중학교", "email": "student@example.com", "phone": "010-0000-0000"
  },
  "form": {
    "motivation": "… 50~2000자 …",
    "plan": "… 50~2000자 …",
    "statements": [3]
  },
  "documents": [
    { "kind": "plan", "title": "학습 계획서", "content": "… 5000자까지 …" },
    { "kind": "link", "title": "프로젝트", "url": "https://…" }
  ],
  "consents": { "privacy": true },
  "guardian": { "name": "홍부모", "relation": "parent", "contact": "010-0000-0000" }
}
```

| 값 | 규칙 |
|---|---|
| `applicant.name` | 2~40자 |
| `applicant.birth_year` | 올해 − 40 ~ 올해 − 6. **월·일은 받지 않는다** |
| `applicant.school_level` | `elementary`, `middle`, `high`, `university`, `out_of_school` |
| `applicant.school_name` | 선택, 80자까지 |
| `applicant.email` | 필수 |
| `applicant.phone` | 선택 |
| `form.motivation`, `form.plan` | 각 50~2000자 |
| `form.statements` | 확인한 자격 문구의 `id` 목록. 공고의 `statement` 자격을 모두 넣어야 한다 |
| `documents` | 5개까지. `kind`: `essay`, `plan`, `project`, `proof`, `link`. `title` 1~120자. `content`(5000자까지)나 `url`(`https://`만) 가운데 하나는 있어야 한다. **파일은 받지 않는다** |
| `consents.privacy` | `true`여야 한다 |
| `guardian` | 미성년자일 때 필수. `relation`: `parent`, `grandparent`, `legal_guardian`. `contact` 5~120자 |

- **미성년자 판단**: 올해 − 태어난 해 − 1 (생일이 아직 지나지 않았다고 봄) 이 `guardian_consent_age`(최소 14) 미만이면 미성년자입니다.
- 주민등록번호, 계좌번호, 주소를 받는 값이 없습니다.
- 이름·학교·이메일·전화·신청서·제출 자료의 내용·보호자 이름과 연락처는 **암호화해** 저장합니다. 서버에 암호화 키가 없으면 `403 FEATURE_DISABLED` (`crypto_not_ready`) — 평문으로 저장하는 길이 없습니다.
- 한 계정이 한 프로그램에 한 번. 계정마다 시간에 20번까지 시도할 수 있습니다.

응답:

```json
{
  "application": { … 4장 … },
  "guardian_consent": {
    "required": true,
    "token": "… 64자 …",
    "url": "https://…/scholarship/consent/…",
    "message": "보호자가 이 주소를 열어 동의해야 심사로 넘어가요. …"
  }
}
```

`guardian_consent`는 미성년자가 아니면 `null`입니다. **주소는 이때 한 번만 나옵니다** (서버에는 해시만 남음). 서버는 문자나 메일을 보내지 않으므로 신청자가 보호자에게 직접 전합니다.

| 오류 | `reason` | 뜻 |
|---|---|---|
| `403 FEATURE_DISABLED` | `scholarship_applications` / `crypto_not_ready` | 기능이 꺼져 있음 / 암호화 키 없음 |
| `404 NOT_FOUND` | | 없는 프로그램 (공고되지 않은 것 포함) |
| `422 VALIDATION_FAILED` | `not_open` | 신청 기간이 아님 |
| `422 VALIDATION_FAILED` | `invalid_application` | 값이 규칙에 맞지 않음, 자격에 해당하지 않음, 동의 없음. `message`에 문제가 모두 적혀 있다 |
| `409 CONFLICT` | `already_applied` | 이미 신청함 (철회한 경우 포함) |
| `429 RATE_LIMITED` | | |

## 4. 내 신청

### `GET /scholarships/my-applications`

`{ "applications": [ … ] }` — 최근 50건. 신청 접수 기능이 꺼져 있어도 본인은 볼 수 있습니다.

### `GET /scholarships/applications/{id}`

`{ "application": { … } }`

```json
{
  "id": "a_8f3k2m9x7q2zab",
  "program": { "id": "s_…", "title": "…", "category_name": "금융 역량 장학" },
  "status": "in_review", "status_text": "심사 중",
  "submitted_at": "…",
  "eligibility_status": "confirmed",
  "eligibility_note": null,
  "is_minor": true,
  "consent_status": "verified", "consent_text": "보호자 동의 확인됨",
  "result": null, "result_text": "",
  "result_notified_at": null,
  "disbursement_status": null,
  "can_withdraw": true,
  "can_appeal": false,
  "appeal_deadline": null,
  "appeal": null,
  "can_reissue_guardian_link": false
}
```

| 값 | 내용 |
|---|---|
| `status` | `submitted`(접수됨), `ineligible`(지원 자격에 해당하지 않음), `in_review`(심사 중), `decided`(심사 완료, 발표 전), `announced`(결과 발표), `withdrawn`(신청 철회) |
| `eligibility_status` | `pending`, `confirmed`, `rejected` |
| `eligibility_note` | 자격에 해당하지 않을 때만 사유. 그 밖에는 `null` |
| `consent_status` | `not_required`, `pending`(보호자 동의를 기다리는 중), `guardian_confirmed`(보호자가 동의함, 담당자 확인 전), `verified` |
| `result` | **`announced`일 때만**: `selected`(선정), `waitlisted`(예비 선정), `not_selected`(선정되지 않음). `decided` 상태에서도 `null` |
| `disbursement_status` | 발표 뒤에만: `planned`, `approved`, `completed`, `cancelled`. 지급 기록이 없으면 `null` |
| `appeal` | `{ status: submitted / upheld / dismissed, response, created_at }` 또는 `null` |

응답에 **없는 것**: 신청서의 글, 이름, 연락처, 심사 점수, 심사위원, 심사 의견.

발표된 결과를 처음 조회하면 그 때가 기록됩니다 (`result.view`).

## 5. 철회, 이의신청

### `POST /scholarships/applications/{id}/withdraw`

본문 없음. `submitted` / `in_review`일 때만. 그 밖에는 `409 CONFLICT` (`cannot_withdraw`). 철회하면 보호자 확인 주소가 죽고, 30일 뒤에 개인정보를 지웁니다. 같은 프로그램에 다시 신청할 수 없습니다. 응답: 신청.

### `POST /scholarships/applications/{id}/appeal`

```json
{ "reason": "… 20~2000자 …" }
```

| 규칙 | 내용 |
|---|---|
| 할 수 있는 때 | 자격에 해당하지 않는다는 결정을 받았거나, 예비 선정·선정되지 않음을 통지받은 뒤 `appeal_days`(기본 7일) 안 |
| 횟수 | 신청 하나에 한 번. 계정마다 시간에 10번까지 시도 |
| 저장 | 사유는 암호화 |

| 오류 | `reason` |
|---|---|
| `422 VALIDATION_FAILED` | `invalid_appeal`(사유의 길이), `appeal_closed`(기간이 아님) |
| `409 CONFLICT` | `already_appealed` |

응답: 신청 (`appeal.status: "submitted"`).

## 6. 보호자 확인 주소 다시 받기

### `POST /scholarships/applications/{id}/guardian-link`

본문 없음. 보호자 동의를 기다리는 접수 상태의 신청만 (`consent_status: pending`, `status: submitted`). 그 밖에는 `409 CONFLICT` (`consent_not_pending`). 계정마다 시간에 5번.

응답: `{ "guardian_consent": { required, token, url, message } }`. 앞의 주소는 못 쓰게 됩니다.

보호자가 주소를 열어 동의하는 화면(`/scholarship/consent/{값}`)은 API 가 아니라 홈페이지입니다. 접속 주소(IP)마다 10분에 30번으로 제한됩니다.

## 7. 관리자

관리자 기능(장학 프로그램 등록, 지원서 관리, 심사위원 관리, 장학 심사, 지급 상태 관리, 감사 로그)은 JSON API 가 아니라 **관리자 화면**(`/admin/scholarship/…`, 세션 쿠키 + 2단계 인증 + CSRF)입니다. 경로와 권한은 [`ADMIN_SYSTEM.md`](ADMIN_SYSTEM.md) 11장, 절차는 [`SCHOLARSHIP_REVIEW_POLICY.md`](SCHOLARSHIP_REVIEW_POLICY.md).

게임 API 의 접속 토큰으로는 들어갈 수 없습니다.

## 8. 클라이언트와의 대응

| 클라이언트 (`IGameBackend`) | 서버 |
|---|---|
| `GetScholarships` | `GET /scholarships` |
| `GetScholarship` | `GET /scholarships/{id}` |
| `GetMyScholarshipApplications` | `GET /scholarships/my-applications` |

클라이언트에는 이 셋뿐입니다: 신청, 철회, 이의신청, 지급에 해당하는 요청이 없습니다 (`LocalMockBackendBenefitTests`가 인터페이스를 훑어 확인).
