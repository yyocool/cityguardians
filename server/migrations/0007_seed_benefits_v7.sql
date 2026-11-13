-- CITY GUARDIANS 0007: v7.0 현실 혜택 기능의 목록.
--
-- 모두 꺼진 채로, 법률 검토를 하지 않은 상태로 넣는다. 켜는 것은 관리자 화면의 승인 절차로만 한다
-- (docs/REAL_WORLD_REWARDS_POLICY.md 4장). 이미 있는 행은 건드리지 않는다 (INSERT IGNORE):
-- 다시 적용해도 관리자가 기록한 법률 검토와 켜고 끈 상태가 그대로 남는다.
--
-- 제휴 상점, 혜택, 쿠폰, 장학 프로그램은 넣지 않는다. 실제 제휴와 실제 프로그램이 생겼을 때 관리자가 등록한다.

INSERT IGNORE INTO benefit_features (code, name, description, is_enabled, legal_status, sort_order, updated_at) VALUES
  ('city_pass_membership', 'CITY PASS 멤버십 가입', '이용자가 CITY PASS 에 가입해 회원번호를 받는다.', 0, 'not_reviewed', 10, UTC_TIMESTAMP()),
  ('city_pass_coupons', 'CITY PASS 쿠폰 발급과 사용', '제휴 혜택의 쿠폰을 발급하고, 제휴 상점이 사용 처리한다.', 0, 'not_reviewed', 20, UTC_TIMESTAMP()),
  ('scholarship_applications', '장학 프로그램 신청 접수', '학생의 신청서와 개인정보, 보호자 동의를 받는다.', 0, 'not_reviewed', 30, UTC_TIMESTAMP()),
  ('scholarship_awards', '장학 선정 결과와 지급 상태', '선정 결과를 확정해 알리고, 지급 상태를 기록한다. 이 시스템은 돈을 보내지 않는다.', 0, 'not_reviewed', 40, UTC_TIMESTAMP());
