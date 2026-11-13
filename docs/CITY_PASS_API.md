# CITY_PASS_API — CITY PASS API

> 기준 v7.0 · 2026-11-13 · 상태: **서버(`server/`)에 구현, 로컬 테스트 DB에서 테스트 통과 (`BenefitApiTest`, `CityPassTest`). 운영 서버에는 배포하지 않음.** Unity 클라이언트는 아직 이 API를 부르지 않습니다 (모의 서버만 씀). 홈페이지는 같은 서비스를 서버 안에서 직접 부릅니다.

공통 규칙(기본 경로 `{설치 경로}/api/v1`, 응답 형식, 오류 코드, 중복 방지 키)은 [`API_SPEC.md`](API_SPEC.md) 0장. 이 문서는 CITY PASS 경로만 다룹니다. 관련: [`CITY_PASS_SYSTEM.md`](CITY_PASS_SYSTEM.md), [`COUPON_SYSTEM.md`](COUPON_SYSTEM.md).

---

## 1. 공통

| 항목 | 내용 |
|---|---|
| 인증 | **모든 경로**에 `Authorization: Bearer <접속 토큰>`. 없거나 만료되면 `401 UNAUTHORIZED`. 이용 정지 계정은 `403 SUSPENDED` |
| 본인 것만 | 회원 정보, 쿠폰, 이용 내역은 토큰의 주인 것만 돌려준다. 남의 쿠폰 번호를 넣으면 `404 NOT_FOUND` (있는지조차 알려 주지 않는다) |
| 이름 | 요청·응답 모두 `snake_case` |
| 시각 | ISO 8601 UTC |
| 기능이 꺼져 있을 때 | 조회는 `200`에 빈 목록과 `notice`(안내 문구). 상태를 바꾸는 요청은 `403 FEATURE_DISABLED` (`reason` = 기능 코드) |
| 게임 경제 | 이 경로들은 지갑·원장·경험치를 읽지도 바꾸지도 않는다. 응답에 `gc`, `level`, `rank` 같은 값이 없다 (테스트가 응답의 모든 이름을 훑어 확인) |
| 클라이언트가 보낸 값 | 정해진 값(`offer_id`, `coupon_id`, `interest_region_code`, `agree_terms`)만 읽는다. `discount_value`, `status`, `gc` 등을 덧붙여 보내도 무시한다 |

| 메서드 | 경로 | 내용 | 필요한 기능 |
|---|---|---|---|
| POST | `/auth/web-code` | 홈페이지 로그인 코드 (2장) | — |
| GET | `/city-pass/profile` | 내 CITY PASS | — (꺼져 있으면 안내) |
| POST | `/city-pass/membership` | 가입 | `city_pass_membership` |
| PUT | `/city-pass/profile` | 관심 지역 바꾸기 | 회원 |
| GET | `/city-pass/offers` | 받을 수 있는 혜택 | — (꺼져 있으면 빈 목록) |
| GET | `/city-pass/merchants` | 제휴 상점 | — (꺼져 있으면 빈 목록) |
| GET | `/city-pass/coupons` | 내 쿠폰 | — |
| POST | `/city-pass/coupons/claim` | 쿠폰 받기 | `city_pass_coupons` |
| POST | `/city-pass/coupons/redeem` | **제시 코드 받기** (사용 처리가 아님 — 8장) | `city_pass_coupons` |
| GET | `/city-pass/history` | 이용 내역 | — |

지시서의 여섯 경로가 모두 있고, 가입(`POST /city-pass/membership`), 관심 지역 변경(`PUT /city-pass/profile`), 제휴 상점(`GET /city-pass/merchants`), 로그인 코드(`POST /auth/web-code`)를 더했습니다.

**없는 경로** (만들지 않았고, 불러도 `404`/`400`): 쿠폰 구매·GC 교환(`/city-pass/coupons/buy`, `/exchange`), 이용자가 스스로 하는 사용 처리(`/use`, `/confirm`), 지갑 현금화. 쿠폰의 사용 처리는 API 가 아니라 상점 확인 화면(`/merchant`, 상점 계정 로그인)에서만 합니다.

## 2. 홈페이지 로그인 코드

### `POST /auth/web-code`

게임 안에서 부릅니다. 홈페이지(`/login`)에 넣을 일회용 코드를 돌려줍니다. 본문 없음.

```json
{
  "code": "K7QM-2XHD",
  "expires_at": "2026-11-13T03:05:00Z",
  "expires_in_seconds": 300,
  "login_url": "https://…/login"
}
```

| 규칙 | 내용 |
|---|---|
| 수명 | 5분, 한 번만 쓸 수 있다 |
| 살아 있는 코드 | 계정마다 하나. 새로 받으면 앞의 코드는 못 쓴다 |
| 횟수 | 계정마다 10분에 6번. 넘으면 `429 RATE_LIMITED` |
| 저장 | 서버에는 코드의 해시만 남는다 |
| `login_url` | 서버 설정 `PUBLIC_BASE_URL`이 없으면 `null` |
| 글자 | 헷갈리는 글자(0·O, 1·I·L)가 없는 31자에서 고른 8자. 넣을 때 대소문자와 붙임표는 가리지 않는다 |

코드를 넣는 쪽(홈페이지 `POST /login`)은 접속 주소(IP)마다 10분에 10번(`WEB_LOGIN_PER_10MIN`)으로 제한됩니다. 홈페이지의 접속은 쿠키 `cg_web`(HttpOnly, SameSite=Lax, 운영에서는 Secure)로 유지되고, 30분 동안 쓰지 않거나 4시간이 지나면 끝납니다. 자세한 것은 [`WEBSITE_SPEC.md`](WEBSITE_SPEC.md) 11장.

이 코드로는 홈페이지의 CITY PASS·장학 화면에만 들어갑니다. 게임 API 의 접속 토큰을 얻을 수 없습니다.

## 3. 내 CITY PASS

### `GET /city-pass/profile`

```json
{
  "membership_enabled": true,
  "coupons_enabled": true,
  "is_member": true,
  "member_no": "CP-K7QM-2XHD",
  "status": "active",
  "nickname": "혜택가디언",
  "interest_region": { "code": "11110", "name": "종로구", "sido_name": "서울특별시" },
  "joined_at": "2026-11-13T02:00:00Z",
  "terms_version": "2026-11-draft",
  "coupons": { "available": 1, "used": 0, "expired": 0 },
  "notice": ""
}
```

- 가입하지 않았으면 `is_member: false`이고 `member_no`, `status`, `interest_region`, `joined_at`이 `null`입니다.
- 멤버십 기능이 꺼져 있으면 `membership_enabled: false`와 `notice`("CITY PASS 는 준비 중이에요…").
- `nickname`은 게임 닉네임입니다 (전입신고 전이면 "이방인"). 실제 이름·연락처는 받지도 저장하지도 않습니다.

### `POST /city-pass/membership`

```json
{ "agree_terms": true, "interest_region_code": "26110" }
```

- `agree_terms`가 `true`가 아니면 `422 VALIDATION_FAILED` (`terms_required`).
- `interest_region_code`를 빼면 **전입신고한 지역**이 기본 관심 지역이 됩니다. 전입신고를 하지 않은 이방인은 관심 지역 없이 가입됩니다. 다른 지역을 골라도 됩니다 — 실제 거주지를 확인하지 않습니다.
- 값을 치르지 않습니다. GC·레벨·전입신고 여부를 조건으로 보지 않습니다.
- 이미 가입했으면 그대로 돌려줍니다 (두 번 불러도 회원번호가 바뀌지 않음).
- 응답: `GET /city-pass/profile`과 같습니다.
- 오류: `403 FEATURE_DISABLED`, `400 INVALID_REQUEST`(지역 코드 형식), `404 NOT_FOUND`(없는 지역).

### `PUT /city-pass/profile`

```json
{ "interest_region_code": "26110" }
```

관심 지역을 바꿉니다. 회원이 아니면 `403 FORBIDDEN` (`not_member`). 응답은 프로필.

## 4. 혜택

### `GET /city-pass/offers?region=11110&category=cafe`

지금 받을 수 있는 혜택입니다. **승인된 업체(계약 기간 안)의 승인된 혜택이고, 혜택 기간 안이며, 쓸 수 있는 지점이 있는 것만** 나옵니다.

| 쿼리 | 내용 |
|---|---|
| `region` | 시·군·구 코드. 그 지역에 지점이 있는 혜택만. 빼면 전국 (다른 지역의 혜택도 볼 수 있다) |
| `category` | `cafe`, `restaurant`, `fitness`, `culture`, `education`, `retail`, `service` |

```json
{
  "coupons_enabled": true,
  "region_code": "11110",
  "notice": "",
  "offers": [
    {
      "id": "o_8f3k2m9x7q2zab",
      "title": "아메리카노 10% 할인",
      "description": "…",
      "discount_type": "percent",
      "discount_value": 10,
      "discount_text": "10% 할인",
      "conditions": "1인 1회, 다른 할인과 함께 쓸 수 없음",
      "issue_policy": "membership",
      "starts_at": "…", "ends_at": "…",
      "coupon_valid_days": 30,
      "per_member_limit": 1,
      "remaining": 95,
      "merchant": { "id": "m_…", "name": "…", "category": "cafe", "category_name": "카페" },
      "branches": [
        { "id": "b_…", "name": "…", "region_code": "11110", "region_name": "서울특별시 종로구",
          "address": "…", "phone": "…", "lat": 37.57, "lng": 126.98, "image_path": "/media/….jpg" }
      ],
      "my_coupons": 0,
      "claimable": true,
      "claim_block": null
    }
  ]
}
```

| 값 | 뜻 |
|---|---|
| `discount_type` | `percent`(할인율), `amount`(할인 금액, 원), `free_trial`(무료 체험), `gift`(사은품), `special`(그 밖의 혜택) |
| `issue_policy` | `membership`(CITY PASS 가입), `promotion`(무료 프로모션), `partner_condition`(제휴사가 공개한 조건). **게임 성적·GC 를 조건으로 하는 값은 없습니다** |
| `remaining` | 남은 발급 수. 제한이 없으면 `null` |
| `claim_block` | 받을 수 없는 이유: `not_member`, `limit_reached`(이미 받음), `sold_out`. 받을 수 있으면 `null` |
| `lat`, `lng`, `image_path`, `phone` | 등록되지 않았으면 `null` |

쿠폰 기능이 꺼져 있으면 `coupons_enabled: false`, `offers: []`, `notice: "제휴 혜택은 준비 중이에요."` — 준비되지 않은 혜택은 보이지 않습니다.

오류: `400 INVALID_REQUEST`(지역 코드·업종이 형식에 맞지 않음), `404 NOT_FOUND`(없는 지역).

## 5. 제휴 상점

### `GET /city-pass/merchants?region=11110&category=cafe`

승인되고 계약 기간 안인 업체와 그 지점입니다. 쿼리는 4장과 같습니다.

```json
{
  "coupons_enabled": true,
  "merchants": [
    { "id": "m_…", "name": "…", "category": "cafe", "category_name": "카페",
      "branches": [ { "id": "b_…", "name": "…", "region_code": "11110", "region_name": "서울특별시 종로구",
                      "address": "…", "phone": "…", "lat": 37.57, "lng": 126.98, "image_path": null } ] }
  ]
}
```

업체 담당자의 이름·연락처, 계약 번호는 응답에 없습니다. 초안·승인 대기·일시 중지·종료된 업체는 나오지 않습니다.

## 6. 내 쿠폰

### `GET /city-pass/coupons`

```json
{
  "coupons_enabled": true,
  "notice": "",
  "coupons": [
    {
      "id": "c_8f3k2m9x7q2zab",
      "status": "issued",
      "issued_at": "…", "expires_at": "…", "redeemed_at": null,
      "offer": { "id": "o_…", "title": "…", "description": "…", "discount_text": "10% 할인", "conditions": "…" },
      "merchant": { "name": "…", "category_name": "카페" },
      "branches": [ … ],
      "usable": true
    }
  ]
}
```

- `status`: `issued`(쓸 수 있음), `redeemed`(씀), `expired`(유효기간 지남). 취소된 쿠폰은 목록에 없습니다. 유효기간이 지난 쿠폰은 예약 작업이 돌기 전이라도 `expired`로 보입니다.
- `usable`: 지금 상점에 보여 줄 수 있는가 (`issued`이고, 업체가 승인 상태이고, 쿠폰 기능이 켜져 있음).
- 기능이 꺼져도 이미 받은 쿠폰의 목록은 볼 수 있습니다 (`usable: false`).
- 최근 200장.

## 7. 쿠폰 받기

### `POST /city-pass/coupons/claim`

헤더 `Idempotency-Key` **필수** (없으면 `400 INVALID_REQUEST`).

```json
{ "offer_id": "o_8f3k2m9x7q2zab" }
```

응답: `{ "coupon": { … 6장의 쿠폰 … } }`. 같은 키로 다시 보내면 처음 응답에 `duplicate: true`가 붙어 돌아오고 쿠폰은 한 장뿐입니다.

- 값을 치르지 않습니다. 지갑 행을 잠그지도 읽지도 않습니다.
- 유효기간 = 받은 때 + `coupon_valid_days`. 혜택의 끝(`ends_at`)을 넘지 않습니다.

| 오류 | `reason` | 뜻 |
|---|---|---|
| `403 FEATURE_DISABLED` | `city_pass_coupons` | 쿠폰 기능이 꺼져 있음 |
| `403 FORBIDDEN` | `not_member` | 가입하지 않음 |
| `404 NOT_FOUND` | | 없는 혜택이거나, 승인·기간 조건에 맞지 않아 지금 받을 수 없음 |
| `422 LIMIT_REACHED` | `limit_reached` | 한 사람이 받을 수 있는 수를 다 받음 |
| `422 LIMIT_REACHED` | `sold_out` | 준비한 쿠폰이 모두 나감 |
| `409 IDEMPOTENCY_MISMATCH` | | 같은 키로 다른 혜택을 요청 |

## 8. 제시 코드 받기

### `POST /city-pass/coupons/redeem`

경로의 이름은 지시서의 것이지만, **이 요청은 쿠폰을 "사용됨"으로 바꾸지 않습니다.** 상점에 보여 줄 일회용 코드를 만들 뿐입니다. 쿠폰이 사용됨이 되는 것은 상점 직원이 상점 확인 화면에서 그 코드를 확인하고 "사용 처리"를 눌렀을 때입니다 ([`COUPON_SYSTEM.md`](COUPON_SYSTEM.md) 5장). 이용자가 스스로 사용 처리를 하는 길은 없습니다.

```json
{ "coupon_id": "c_8f3k2m9x7q2zab" }
```

```json
{
  "coupon_id": "c_8f3k2m9x7q2zab",
  "code": "K7QM2-XHD4P",
  "qr_payload": "https://…/merchant/redeem?code=K7QM2-XHD4P",
  "expires_at": "2026-11-13T03:05:00Z",
  "expires_in_seconds": 300
}
```

| 규칙 | 내용 |
|---|---|
| 수명 | 5분. 쿠폰의 유효기간보다 오래 살지 않는다 |
| 다시 부르면 | 새 코드가 나오고 앞의 코드는 못 쓴다 |
| `qr_payload` | QR 에 넣을 글자. `PUBLIC_BASE_URL`이 있으면 상점 확인 화면의 주소, 없으면 `CGPASS:<코드>`. **회원번호·닉네임·쿠폰 번호·계정 번호가 없다** |
| 저장 | 서버에는 코드의 해시만 남는다 |
| 횟수 | 계정마다 10분에 30번. 넘으면 `429 RATE_LIMITED` |
| 기록 | `coupon_events`에 `presented` |

| 오류 | `reason` | 뜻 |
|---|---|---|
| `403 FEATURE_DISABLED` | `city_pass_coupons` | |
| `404 NOT_FOUND` | | 없는 쿠폰, 남의 쿠폰, 취소된 쿠폰 |
| `409 CONFLICT` | `already_redeemed` | 이미 쓴 쿠폰 |
| `422 VALIDATION_FAILED` | `expired` | 유효기간이 지남 |
| `422 VALIDATION_FAILED` | `merchant_unavailable` | 업체의 제휴가 중지·종료됨 |

## 9. 이용 내역

### `GET /city-pass/history`

상점이 사용 처리한 쿠폰입니다. 최근 200건.

```json
{
  "history": [
    { "coupon_id": "c_…", "offer_title": "…", "discount_text": "10% 할인",
      "merchant_name": "…", "branch_name": "…", "redeemed_at": "…" }
  ]
}
```

## 10. 관리자와 상점

관리자 기능(제휴 상점 등록·승인, 혜택 등록·승인, 쿠폰 발급 정책, 쿠폰 사용 통계, 감사 로그)은 JSON API 가 아니라 **관리자 화면**(`/admin/pass/…`, 세션 쿠키 + 2단계 인증 + CSRF)입니다. 경로와 권한은 [`ADMIN_SYSTEM.md`](ADMIN_SYSTEM.md) 11장.

쿠폰의 확인과 사용 처리는 **상점 확인 화면**(`/merchant/…`, 상점 계정 로그인 + CSRF)입니다. [`COUPON_SYSTEM.md`](COUPON_SYSTEM.md) 5장.

둘 다 게임 API 의 접속 토큰으로는 들어갈 수 없습니다.

## 11. 클라이언트와의 대응

`IGameBackend` (`client/Assets/CityGuardians/Services/IGameBackend.cs`)의 CITY PASS 요청과 서버 경로:

| 클라이언트 | 서버 |
|---|---|
| `GetCityPass` | `GET /city-pass/profile` |
| `JoinCityPass` | `POST /city-pass/membership` |
| `SetCityPassRegion` | `PUT /city-pass/profile` |
| `GetCityPassOffers` | `GET /city-pass/offers` |
| `GetCityPassMerchants` | `GET /city-pass/merchants` |
| `GetCityPassCoupons` | `GET /city-pass/coupons` |
| `ClaimCityPassCoupon` | `POST /city-pass/coupons/claim` |
| `PresentCityPassCoupon` | `POST /city-pass/coupons/redeem` |
| `GetCityPassHistory` | `GET /city-pass/history` |
| `IssueWebLoginCode` | `POST /auth/web-code` |

클라이언트의 구현은 `LocalMockBackend`뿐이고 HTTP 구현이 없습니다. 응답의 필드가 같도록 맞췄지만 실제로 이어 붙여 확인한 적은 없습니다.
