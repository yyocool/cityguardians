<?php
use CG\Admin\Auth;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Env;
use CG\Infra\Form;
use CG\Service\Features;
use CG\Service\ScholarshipOffice;
use CG\Service\Scholarships;

/**
 * GUARDIANS SCHOLARSHIP: 공고, 신청, 보호자 동의, 자격 확인, 심사, 선정, 통지, 이의신청, 지급 상태, 개인정보
 * (docs/SCHOLARSHIP_SYSTEM.md, docs/SCHOLARSHIP_REVIEW_POLICY.md, docs/PRIVACY_DATA_FLOW.md).
 */
final class ScholarshipTest extends BenefitTestCase
{
    private function apply(array $player, array $program, array $input = null)
    {
        return $this->app->scholarships()->apply($player, $program['public_id'], $input !== null ? $input : $this->applicationInput());
    }

    /** 신청 → 자격 확인까지. @return int 신청 id */
    private function inReview(array $player, array $program, array $input = null)
    {
        $id = $this->applicationId($this->apply($player, $program, $input)['application']['id']);
        T::eq([], $this->app->scholarshipOffice()->decideEligibility($this->schManager(), $id, true, '서류 확인'));
        return $id;
    }

    /** 배정된 심사위원이 모두 같은 점수로 평가한다. */
    private function review($applicationId, $planScore, $motivationScore)
    {
        $office = $this->app->scholarshipOffice();
        $criteria = $this->db->all('SELECT c.id FROM scholarship_criteria c JOIN scholarship_applications a ON a.program_id = c.program_id WHERE a.id = ? ORDER BY c.id',
            [$applicationId]);
        foreach ($this->db->all('SELECT r.id, a.login FROM scholarship_reviews r JOIN admins a ON a.id = r.admin_id WHERE r.application_id = ?', [$applicationId]) as $row) {
            T::eq([], $office->submitReview($this->admin($row['login'], Auth::ROLE_REVIEWER), $row['id'],
                ['score_' . $criteria[0]['id'] => (string)$planScore, 'score_' . $criteria[1]['id'] => (string)$motivationScore, 'comment' => '계획이 구체적이고 실행할 수 있어 보입니다.']));
        }
    }

    private function close(array $program)
    {
        Clock::advance(15 * 86400);
        T::eq([], $this->app->scholarshipOffice()->programTransition($this->schManager(), $program['id'], 'close', '접수 마감'));
    }

    // ── 꺼져 있을 때 ────────────────────────────────────────

    public function testNoProgramsAreSeededAndNothingIsShownWhileTheFeatureIsOff()
    {
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_programs'));
        $list = $this->app->scholarships()->programs();
        T::ok(!$list['applicationsEnabled'] && $list['programs'] === [] && $list['notice'] !== '');
        T::throwsApi(ApiError::FEATURE_DISABLED, function () {
            $this->app->scholarships()->program('s_aaaaaaaaaaaaaa');
        });
        T::throwsApi(ApiError::FEATURE_DISABLED, function () {
            $this->app->scholarships()->apply($this->resident(), 's_aaaaaaaaaaaaaa', $this->applicationInput());
        });
        // 꺼져 있으면 공고를 낼 수 없다.
        $office = $this->app->scholarshipOffice();
        list($id, ) = $office->saveProgram($this->schManager(), null, $this->programInput());
        $office->addCriterion($this->schManager(), $id, ['name' => '계획', 'description' => '', 'max_score' => '10', 'weight' => '1']);
        $office->setReviewer($this->schManager(), $id, $this->reviewer(1)['id'], true);
        $office->setReviewer($this->schManager(), $id, $this->reviewer(2)['id'], true);
        $office->programTransition($this->schManager(), $id, 'submit', '승인 요청');
        $problems = $office->programTransition($this->super(), $id, 'publish', '공고');
        T::ok(count($problems) === 1 && strpos($problems[0], '꺼져') !== false);
    }

    // ── 공고 ────────────────────────────────────────────────

    public function testProgramNeedsCriteriaReviewersAndASecondAdminToBePublished()
    {
        $this->enableScholarship();
        $office = $this->app->scholarshipOffice();
        list($id, $problems) = $office->saveProgram($this->schManager(), null, $this->programInput());
        T::eq([], $problems);
        T::eq([], $office->programTransition($this->schManager(), $id, 'submit', '승인 요청'));
        // 평가 기준 없음, 심사위원 없음
        T::eq(2, count($office->programTransition($this->super(), $id, 'publish', '공고')));
        T::eq([], $office->addCriterion($this->schManager(), $id, ['name' => '계획', 'description' => '', 'max_score' => '10', 'weight' => '1']));
        T::eq([], $office->setReviewer($this->schManager(), $id, $this->reviewer(1)['id'], true));
        T::eq([], $office->setReviewer($this->schManager(), $id, $this->reviewer(2)['id'], true));
        // 심사위원 역할이 아닌 계정은 심사위원이 될 수 없다.
        T::ok(count($office->setReviewer($this->schManager(), $id, $this->super()['id'], true)) > 0);
        // 작성한 사람은 승인할 수 없다 (기준을 더한 사람이 마지막 작성자다).
        $problems = $office->programTransition($this->schManager(), $id, 'publish', '내가 공고');
        T::ok(count($problems) === 1 && strpos($problems[0], '다른 관리자') !== false);
        T::eq([], $office->programTransition($this->super(), $id, 'publish', '내용 확인'));

        $list = $this->app->scholarships()->programs()['programs'];
        T::eq(1, count($list));
        T::eq('open', $list[0]['phase']);
        T::eq('금융 역량 장학', $list[0]['categoryName']);
        $detail = $this->app->scholarships()->program($list[0]['id']);
        T::eq('계획', $detail['criteria'][0]['name']);
        T::eq('https://cg.test/scholarship/apply/' . $list[0]['id'], $detail['applyUrl']);
        T::ok(strpos($detail['notUsedInReview'], '게임') !== false);
        // 공고가 나간 뒤에는 내용·자격·기준을 바꿀 수 없다.
        list(, $problems) = $office->saveProgram($this->schManager(), $id, $this->programInput(['capacity' => '100']));
        T::ok(count($problems) > 0);
        T::ok(count($office->addCriterion($this->schManager(), $id, ['name' => '더하기', 'description' => '', 'max_score' => '10', 'weight' => '1'])) > 0);
        T::ok(count($office->addRule($this->schManager(), $id, ['rule_type' => 'statement', 'description' => '새 조건'])) > 0);
    }

    public function testGameResultsCanNeverBeAnEligibilityRuleOrACriterion()
    {
        $office = $this->app->scholarshipOffice();
        list($id, ) = $office->saveProgram($this->schManager(), null, $this->programInput());
        foreach (['game_level', 'gc_balance', 'city_ranking', 'battle_score', 'play_time', 'purchase_amount', 'paid_items', ''] as $type) {
            $problems = $office->addRule($this->schManager(), $id, ['rule_type' => $type, 'description' => '게임 조건', 'min' => '10']);
            T::ok(count($problems) > 0, $type);
        }
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_eligibility_rules'));
        foreach (['GC 보유량', '도시 랭킹 순위', '전투 성적', '게임 레벨', '유료 결제 금액', '플레이 시간'] as $name) {
            T::ok(count($office->addCriterion($this->schManager(), $id, ['name' => $name, 'description' => '', 'max_score' => '10', 'weight' => '1'])) > 0, $name);
        }
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_criteria'));
        // 허용된 종류는 들어간다.
        T::eq([], $office->addRule($this->schManager(), $id, ['rule_type' => 'school_level', 'description' => '중·고등학생', 'levels' => 'middle, high']));
        T::eq([], $office->addRule($this->schManager(), $id, ['rule_type' => 'birth_year', 'description' => '2008~2013년생', 'min' => '2008', 'max' => '2013']));
        T::ok(count($office->addRule($this->schManager(), $id, ['rule_type' => 'school_level', 'description' => '없는 학교급', 'levels' => 'kindergarten'])) > 0);
        // 보호자 동의 나이는 14 아래로 내릴 수 없다.
        list(, $problems) = $office->saveProgram($this->schManager(), $id, $this->programInput(['guardian_consent_age' => '12']));
        T::ok(count($problems) > 0);
    }

    // ── 신청 ────────────────────────────────────────────────

    public function testAdultApplicationIsStoredEncryptedAndDoesNotTouchTheGameEconomy()
    {
        $this->enableScholarship();
        $program = $this->program();
        $player = $this->resident('11110', '게임닉네임');
        $this->give($player, 1000);
        $gc = $this->gc($player);
        $ledger = $this->ledgerRows();

        $result = $this->apply($player, $program);
        T::eq('submitted', $result['application']['status']);
        T::eq('not_required', $result['application']['consentStatus']);
        T::eq(null, $result['guardianConsent']);
        T::eq(null, $result['application']['result']);

        // DB 에는 이름·연락처·신청서가 평문으로 남지 않는다.
        $dump = json_encode([
            $this->db->all('SELECT * FROM scholarship_applications'), $this->db->all('SELECT * FROM scholarship_applicant_profiles'),
            $this->db->all('SELECT * FROM scholarship_consents'), $this->db->all('SELECT * FROM scholarship_audit_logs'),
        ], JSON_UNESCAPED_UNICODE);
        foreach (['홍길동', '테스트대학교', 'student@example.com', '010-0000-0000', '금융을 제대로'] as $secret) {
            T::ok(strpos($dump, $secret) === false, '평문으로 저장됨: ' . $secret);
        }
        $profile = $this->db->one('SELECT * FROM scholarship_applicant_profiles');
        T::ok(strpos($profile['name_enc'], 'v1:') === 0);
        T::eq(2004, (int)$profile['birth_year']);
        T::eq(['privacy'], array_column($this->db->all('SELECT consent_type FROM scholarship_consents'), 'consent_type'));
        // 게임 닉네임은 신청 데이터 어디에도 붙어 있지 않다.
        T::ok(strpos($dump, '게임닉네임') === false);

        // 게임 경제와 이어지지 않는다.
        T::eq($gc, $this->gc($player));
        T::eq($ledger, $this->ledgerRows());

        // 한 프로그램에 한 번만.
        $error = T::throwsApi(ApiError::CONFLICT, function () use ($player, $program) {
            $this->apply($player, $program);
        });
        T::eq('already_applied', $error->reason);
        T::eq(1, count($this->app->scholarships()->myApplications($player)['applications']));
        // 남의 신청은 볼 수 없다.
        $other = $this->resident('11110', '다른사람');
        T::eq([], $this->app->scholarships()->myApplications($other)['applications']);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($other, $result) {
            $this->app->scholarships()->application($other, $result['application']['id']);
        });
        T::throwsApi(ApiError::NOT_FOUND, function () use ($other, $result) {
            $this->app->scholarships()->withdraw($other, $result['application']['id']);
        });
    }

    public function testApplicationIsValidatedAndRejectsMissingConsentOrBadValues()
    {
        $this->enableScholarship();
        $program = $this->program();
        $player = $this->resident();
        $cases = [
            '동의 없음' => $this->applicationInput([], ['consents' => ['privacy' => false]]),
            '동의가 글자' => $this->applicationInput([], ['consents' => ['privacy' => 'true']]),
            '이름 없음' => $this->applicationInput(['name' => '']),
            '이메일 틀림' => $this->applicationInput(['email' => 'not-an-email']),
            '학교급 틀림' => $this->applicationInput(['schoolLevel' => 'kindergarten']),
            '태어난 해 틀림' => $this->applicationInput(['birthYear' => 1900]),
            '짧은 동기' => $this->applicationInput([], ['form' => ['motivation' => '짧다', 'plan' => str_repeat('계획입니다. ', 10)]]),
            '자료가 너무 많음' => $this->applicationInput([], ['documents' => array_fill(0, 6, ['kind' => 'essay', 'title' => '글', 'content' => '내용'])]),
            'http 주소' => $this->applicationInput([], ['documents' => [['kind' => 'link', 'title' => '링크', 'url' => 'http://example.com/a']]]),
            '스크립트 주소' => $this->applicationInput([], ['documents' => [['kind' => 'link', 'title' => '링크', 'url' => 'javascript:alert(1)']]]),
            '빈 자료' => $this->applicationInput([], ['documents' => [['kind' => 'essay', 'title' => '글']]]),
        ];
        foreach ($cases as $name => $input) {
            $error = T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player, $program, $input) {
                $this->apply($player, $program, $input);
            }, $name);
            T::ok(count($error->problems) > 0, $name);
        }
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_applications'));
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_applicant_profiles'));
        // 제대로 낸 자료는 저장된다 (내용은 암호화).
        $ok = $this->applicationInput([], ['documents' => [['kind' => 'essay', 'title' => '나의 글', 'content' => '비밀스러운 글 내용'],
            ['kind' => 'link', 'title' => '발표 자료', 'url' => 'https://example.com/slides']]]);
        $this->apply($player, $program, $ok);
        $documents = $this->db->all('SELECT * FROM scholarship_documents ORDER BY id');
        T::eq(2, count($documents));
        T::ok(strpos(json_encode($documents, JSON_UNESCAPED_UNICODE), '비밀스러운') === false);
        T::eq('https://example.com/slides', $documents[1]['url']);
    }

    public function testEligibilityRulesReadOnlyTheApplicationForm()
    {
        $this->enableScholarship();
        $program = $this->program([], [
            ['rule_type' => 'school_level', 'description' => '중·고등학생만 지원할 수 있습니다', 'levels' => 'middle,high'],
            ['rule_type' => 'statement', 'description' => '금융교육 과정을 모두 마쳤습니다'],
            ['rule_type' => 'document', 'description' => '활동 계획서를 내야 합니다', 'kind' => 'plan'],
        ]);
        // 레벨이 높고 GC 가 많아도 자격이 생기지 않는다.
        $rich = $this->setLevel($this->resident('11110', '고레벨'), 50);
        $this->give($rich, 1000000);
        $error = T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($rich, $program) {
            $this->apply($rich, $program);   // 대학생, 확인 문구 없음, 계획서 없음
        });
        T::eq(3, count($error->problems), implode(' / ', $error->problems));

        $statement = (int)$this->db->val("SELECT id FROM scholarship_eligibility_rules WHERE rule_type = 'statement'");
        $input = $this->minorInput(['documents' => [['kind' => 'plan', 'title' => '계획서', 'content' => '매주 한 번 모입니다.']]]);
        $input['applicant']['schoolLevel'] = 'high';
        $input['applicant']['birthYear'] = 2009;
        $input['form']['statements'] = [$statement];
        // 레벨 1, GC 없는 계정도 조건이 맞으면 신청된다.
        $result = $this->apply($this->guest(), $program, $input);
        T::eq('submitted', $result['application']['status']);
    }

    public function testApplicationsAreOnlyAcceptedDuringTheApplyPeriod()
    {
        $this->enableScholarship();
        $upcoming = $this->program(['apply_starts_at' => Form::koreaText(Clock::now() + 86400), 'apply_ends_at' => Form::koreaText(Clock::now() + 5 * 86400)]);
        $player = $this->resident();
        $error = T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player, $upcoming) {
            $this->apply($player, $upcoming);
        });
        T::eq('not_open', $error->reason);
        T::eq('upcoming', $this->app->scholarships()->program($upcoming['public_id'])['phase']);
        Clock::advance(2 * 86400);
        T::eq('submitted', $this->apply($player, $upcoming)['application']['status']);
        Clock::advance(10 * 86400);
        T::eq('closed', $this->app->scholarships()->program($upcoming['public_id'])['phase']);
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($upcoming) {
            $this->apply($this->resident('11110', '늦은사람'), $upcoming);
        });
    }

    public function testApplyFailsClosedWithoutAnEncryptionKey()
    {
        $this->enableScholarship();
        $program = $this->program();
        $noKey = new \CG\App(Env::fromArray([]), $this->db);
        $player = $this->resident();
        // 키가 없는 서버에서는 기능이 켜져 있어도 개인정보를 받지 않는다.
        $noKey->features()->forget();
        $error = T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($noKey, $player, $program) {
            $noKey->scholarships()->apply($player, $program['public_id'], $this->applicationInput());
        });
        T::eq('crypto_not_ready', $error->reason);
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_applications'));
    }

    // ── 보호자 동의 ─────────────────────────────────────────

    public function testMinorNeedsGuardianConsentConfirmedAndVerifiedBeforeReview()
    {
        $this->enableScholarship();
        $program = $this->program();
        $player = $this->resident();
        $scholarships = $this->app->scholarships();
        $office = $this->app->scholarshipOffice();
        // 보호자 정보 없이는 낼 수 없다.
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($player, $program) {
            $this->apply($player, $program, $this->applicationInput(['birthYear' => 2011, 'schoolLevel' => 'middle']));
        });
        $result = $this->apply($player, $program, $this->minorInput());
        T::ok($result['application']['isMinor']);
        T::eq('pending', $result['application']['consentStatus']);
        $token = $result['guardianConsent']['token'];
        T::eq('https://cg.test/scholarship/consent/' . $token, $result['guardianConsent']['url']);
        $id = $this->applicationId($result['application']['id']);
        // 주소의 값은 해시로만 저장된다.
        T::ok(strpos(json_encode($this->db->all('SELECT * FROM scholarship_consents')), $token) === false);

        // 보호자 동의 전에는 심사로 넘길 수 없다.
        $problems = $office->decideEligibility($this->schManager(), $id, true, '서류 확인');
        T::ok(count($problems) === 1 && strpos($problems[0], '보호자') !== false);
        T::ok(count($office->verifyConsent($this->schManager(), $id, ['verify_method' => 'call', 'verify_note' => '통화함'])) > 0, '보호자가 동의하기 전에는 확인할 수 없다');

        // 보호자 화면
        $info = $scholarships->guardianConsentInfo($token, '198.51.100.7');
        T::eq('홍길동', $info['applicantName']);
        T::eq($program['title'], $info['programTitle']);
        T::eq(null, $scholarships->guardianConsentInfo(str_repeat('0', 64), '198.51.100.7'));
        T::eq(null, $scholarships->guardianConsentInfo('not-a-token', '198.51.100.7'));
        // 이름이 다르거나 동의에 표시하지 않으면 기록되지 않는다.
        T::ok(count($scholarships->confirmGuardianConsent($token, '다른사람', true, '198.51.100.7')) > 0);
        T::ok(count($scholarships->confirmGuardianConsent($token, '홍판서', false, '198.51.100.7')) > 0);
        T::eq('pending', $this->db->val('SELECT consent_status FROM scholarship_applications'));
        T::eq([], $scholarships->confirmGuardianConsent($token, ' 홍 판서 ', true, '198.51.100.7'));
        T::eq('guardian_confirmed', $this->db->val('SELECT consent_status FROM scholarship_applications'));
        // 주소는 한 번만 쓸 수 있다.
        T::eq(null, $scholarships->guardianConsentInfo($token, '198.51.100.7'));
        T::ok(count($scholarships->confirmGuardianConsent($token, '홍판서', true, '198.51.100.7')) > 0);

        // 보호자가 눌렀어도, 담당자가 확인하기 전에는 심사로 넘어가지 않는다.
        T::ok(count($office->decideEligibility($this->schManager(), $id, true, '서류 확인')) > 0);
        T::ok(count($office->verifyConsent($this->schManager(), $id, ['verify_method' => 'sms', 'verify_note' => '문자'])) > 0, '정해진 확인 방법만');
        T::eq([], $office->verifyConsent($this->schManager(), $id, ['verify_method' => 'call', 'verify_note' => '보호자와 통화해 동의를 확인함']));
        T::eq('verified', $this->db->val('SELECT consent_status FROM scholarship_applications'));
        T::eq([], $office->decideEligibility($this->schManager(), $id, true, '서류 확인'));
        T::eq('in_review', $this->db->val('SELECT status FROM scholarship_applications'));
        T::eq(2, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_reviews'));
        // 동의의 흐름이 기록된다.
        $actions = array_column($this->db->all('SELECT action FROM scholarship_audit_logs WHERE application_id = ? ORDER BY id', [$id]), 'action');
        foreach (['application.submit', 'consent.view', 'consent.confirm', 'consent.verify', 'eligibility.confirm'] as $action) {
            T::ok(in_array($action, $actions, true), $action);
        }
    }

    public function testGuardianLinkCanBeReissuedAndAgeBoundaryIsConservative()
    {
        $this->enableScholarship();
        $program = $this->program();
        $player = $this->resident();
        $result = $this->apply($player, $program, $this->minorInput());
        $old = $result['guardianConsent']['token'];
        $new = $this->app->scholarships()->reissueGuardianLink($player, $result['application']['id'])['guardianConsent']['token'];
        T::ok($old !== $new);
        T::eq(null, $this->app->scholarships()->guardianConsentInfo($old, '198.51.100.7'));
        T::ok($this->app->scholarships()->guardianConsentInfo($new, '198.51.100.7') !== null);

        // 2026년 기준: 2007년생은 생일 전이면 만 18세다 → 보호자 동의가 필요하다고 본다. 2006년생은 필요 없다.
        T::eq(18, Scholarships::youngestAge(2007));
        $born2007 = $this->applicationInput(['birthYear' => 2007]);
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($program, $born2007) {
            $this->apply($this->resident('11110', '2007년생'), $program, $born2007);
        });
        $adult = $this->apply($this->resident('11110', '2006년생'), $program, $this->applicationInput(['birthYear' => 2006]));
        T::ok(!$adult['application']['isMinor']);
    }

    // ── 심사 ────────────────────────────────────────────────

    public function testReviewerSeesOnlyAssignedApplicationsWithoutPersonalData()
    {
        $this->enableScholarship();
        $program = $this->program();
        $office = $this->app->scholarshipOffice();
        $id = $this->inReview($this->resident('11110', '게임닉네임'), $program,
            $this->applicationInput([], ['documents' => [['kind' => 'essay', 'title' => '나의 글', 'content' => '저축 습관에 대해 썼습니다.']]]));
        $one = $this->reviewer(1);
        $two = $this->reviewer(2);
        $three = $this->reviewer(3);   // 이 프로그램에 배정되지 않은 심사위원

        $queue = $office->reviewQueue($one);
        T::eq(1, count($queue));
        T::eq([], $office->reviewQueue($three));
        $view = $office->reviewView($one, $queue[0]['id']);
        $text = json_encode($view, JSON_UNESCAPED_UNICODE);
        // 이름, 학교 이름, 연락처, 태어난 해, 게임 닉네임·계정은 심사위원에게 가지 않는다.
        foreach (['홍길동', '테스트대학교', 'student@example.com', '010-0000-0000', '2004', '게임닉네임', 'player'] as $hidden) {
            T::ok(strpos($text, $hidden) === false, '심사위원에게 보임: ' . $hidden);
        }
        T::eq('대학생', $view['application']['schoolLevel']);
        T::ok(strpos($view['form']['motivation'], '금융을 제대로') === 0);
        T::eq('저축 습관에 대해 썼습니다.', $view['documents'][0]['content']);

        // 남에게 배정된 심사는 없는 것으로 답한다.
        $otherReview = (int)$this->db->val('SELECT id FROM scholarship_reviews WHERE admin_id = ?', [$two['id']]);
        T::throwsApi(ApiError::NOT_FOUND, function () use ($office, $one, $otherReview) {
            $office->reviewView($one, $otherReview);
        });
        T::throwsApi(ApiError::NOT_FOUND, function () use ($office, $three, $otherReview) {
            $office->submitReview($three, $otherReview, ['comment' => '남의 심사에 점수를 넣어 본다']);
        });
        // 심사위원이 아닌 역할은 심사 화면을 쓸 수 없다. 최고 관리자도.
        foreach ([$this->super(), $this->schManager(), $this->compliance(), $this->admin('fin', Auth::ROLE_FINANCE)] as $admin) {
            T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $admin, $otherReview) {
                $office->reviewView($admin, $otherReview);
            }, $admin['role']);
        }
        // 심사위원은 신청 목록, 개인정보, 결과 집계를 볼 수 없다.
        foreach (['applications', 'results'] as $method) {
            T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $one, $program, $method) {
                $office->$method($one, $program['id']);
            }, $method);
        }
        T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $one, $id) {
            $office->pii($one, $id, '궁금해서');
        });
        T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $one, $id) {
            $office->applicationDetail($one, $id);
        });
        // 열어 본 것은 기록된다.
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM scholarship_audit_logs WHERE action = 'review.view' AND actor_id = ?", [$one['id']]));
    }

    public function testReviewScoresAreValidatedSubmittedOnceAndWeighted()
    {
        $this->enableScholarship();
        $program = $this->program();
        $office = $this->app->scholarshipOffice();
        $id = $this->inReview($this->resident(), $program);
        $one = $this->reviewer(1);
        $review = (int)$this->db->val('SELECT id FROM scholarship_reviews WHERE admin_id = ?', [$one['id']]);
        $criteria = $this->db->all('SELECT id FROM scholarship_criteria WHERE program_id = ? ORDER BY id', [$program['id']]);
        $scores = function ($plan, $motivation, $comment = '계획이 구체적이고 실행할 수 있어 보입니다.') use ($criteria) {
            return ['score_' . $criteria[0]['id'] => $plan, 'score_' . $criteria[1]['id'] => $motivation, 'comment' => $comment];
        };
        // 범위 밖의 점수, 빠진 점수, 의견 없음
        T::ok(count($office->submitReview($one, $review, $scores('11', '5'))) > 0);
        T::ok(count($office->submitReview($one, $review, $scores('-1', '5'))) > 0);
        T::ok(count($office->submitReview($one, $review, $scores('8', ''))) > 0);
        T::ok(count($office->submitReview($one, $review, $scores('8', '4', ''))) > 0);
        T::eq('assigned', $this->db->val('SELECT status FROM scholarship_reviews WHERE id = ?', [$review]));

        // 계획 8/10 (가중치 2), 동기 4/5 (가중치 1) → (0.8×2 + 0.8×1) ÷ 3 = 80.00점
        T::eq([], $office->submitReview($one, $review, $scores('8', '4')));
        T::eq(8000, (int)$this->db->val('SELECT total_centi FROM scholarship_reviews WHERE id = ?', [$review]));
        T::eq(10000, ScholarshipOffice::totalCenti([['id' => 1, 'max_score' => 10, 'weight' => 2], ['id' => 2, 'max_score' => 5, 'weight' => 1]], [1 => 10, 2 => 5]));
        T::eq(6667, ScholarshipOffice::totalCenti([['id' => 1, 'max_score' => 10, 'weight' => 2], ['id' => 2, 'max_score' => 5, 'weight' => 1]], [1 => 10, 2 => 0]));
        // 낸 평가는 고칠 수 없다.
        T::ok(count($office->submitReview($one, $review, $scores('10', '5'))) > 0);
        T::ok(count($office->recuse($one, $review, '아는 사람입니다')) > 0);
        T::eq(8000, (int)$this->db->val('SELECT total_centi FROM scholarship_reviews WHERE id = ?', [$review]));

        // 다른 심사위원은 회피한다 → 심사 수가 모자라 선정을 제안할 수 없다.
        $two = $this->reviewer(2);
        $second = (int)$this->db->val('SELECT id FROM scholarship_reviews WHERE admin_id = ?', [$two['id']]);
        T::ok(count($office->recuse($two, $second, '')) > 0);
        T::eq([], $office->recuse($two, $second, '지원자와 아는 사이입니다'));
        $this->close($program);
        $problems = $office->proposeDecisions($this->schManager(), $program['id'], [$id => 'selected'], '점수 순');
        T::ok(count($problems) === 1 && strpos($problems[0], '심사가') !== false);
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM scholarship_awards'));
    }

    // ── 선정, 승인, 통지 ────────────────────────────────────

    public function testSelectionNeedsProposalSecondApprovalAndAnnouncement()
    {
        $this->enableScholarship();
        $program = $this->program(['capacity' => '1']);
        $office = $this->app->scholarshipOffice();
        $scholarships = $this->app->scholarships();
        $first = $this->resident('11110', '지원자하나');
        $second = $this->resident('11110', '지원자둘');
        $this->give($first, 300);
        $gcBefore = $this->gc($first);
        $a = $this->inReview($first, $program);
        $b = $this->inReview($second, $program);
        $this->review($a, 9, 5);
        $this->review($b, 6, 3);
        $publicA = $this->db->val('SELECT public_id FROM scholarship_applications WHERE id = ?', [$a]);

        // 접수를 마감하기 전에는 선정할 수 없다.
        T::ok(count($office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected', $b => 'not_selected'], '점수 순')) > 0);
        $this->close($program);
        $results = $office->results($this->schManager(), $program['id']);
        T::eq([$a, $b], array_map('intval', array_column($results, 'id')), '점수가 높은 순서');
        T::eq(9333, (int)$results[0]['score_centi']);
        T::eq(2, (int)$results[0]['review_count']);

        // 정원을 넘길 수 없고, 모든 지원서에 결정이 있어야 한다.
        T::ok(count($office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected', $b => 'selected'], '둘 다')) > 0);
        T::ok(count($office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected'], '하나만')) > 0);
        T::ok(count($office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected', $b => 'maybe'], '모름')) > 0);
        T::eq([], $office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected', $b => 'not_selected'], '심사 점수 순'));

        // 제안만으로는 아무것도 확정되지 않는다. 신청자에게도 보이지 않는다.
        T::eq('in_review', $this->db->val('SELECT status FROM scholarship_applications WHERE id = ?', [$a]));
        T::eq(null, $scholarships->application($first, $publicA)['application']['result']);
        // 제안한 사람은 최종 승인할 수 없다.
        $problems = $office->approveDecisions($this->schManager(), $program['id'], '내가 승인');
        T::ok(count($problems) === 1 && strpos($problems[0], '다른 관리자') !== false);
        // 심사위원, 준법 담당, 지급 담당은 승인할 수 없다.
        foreach ([$this->reviewer(1), $this->compliance(), $this->admin('fin', Auth::ROLE_FINANCE)] as $admin) {
            T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $admin, $program) {
                $office->approveDecisions($admin, $program['id'], '승인해 본다');
            }, $admin['role']);
        }
        // 알릴 수도 없다 (최종 승인이 먼저다).
        T::ok(count($office->announce($this->super(), $program['id'], '발표')) > 0);
        T::eq([], $office->approveDecisions($this->super(), $program['id'], '심사 결과와 정원을 확인함'));
        T::eq('decided', $this->db->val('SELECT status FROM scholarship_applications WHERE id = ?', [$a]));
        // 승인했어도 알리기 전에는 신청자에게 결과가 보이지 않는다.
        $view = $scholarships->application($first, $publicA)['application'];
        T::eq(null, $view['result']);
        T::eq('심사 완료 (발표 전)', $view['statusText']);

        T::eq([], $office->announce($this->super(), $program['id'], '결과 발표'));
        $view = $scholarships->application($first, $publicA)['application'];
        T::eq('selected', $view['result']);
        T::eq('선정', $view['resultText']);
        T::ok(!$view['canAppeal'], '선정된 사람은 이의신청 대상이 아니다');
        $loser = $scholarships->myApplications($second)['applications'][0];
        T::eq('not_selected', $loser['result']);
        T::ok($loser['canAppeal']);
        T::eq('announced', $this->db->val('SELECT status FROM scholarship_programs WHERE id = ?', [$program['id']]));
        // 보관 기한이 정해진다: 선정 5년, 그 밖 180일.
        T::eq(Clock::koreaDate(Clock::now() + 1825 * 86400), $this->db->val('SELECT purge_after FROM scholarship_applications WHERE id = ?', [$a]));
        T::eq(Clock::koreaDate(Clock::now() + 180 * 86400), $this->db->val('SELECT purge_after FROM scholarship_applications WHERE id = ?', [$b]));
        // 선정되어도 게임 보상은 없다.
        T::eq($gcBefore, $this->gc($first));
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE created_at >= ?", [Clock::sql(Clock::now() - 60)]));
    }

    public function testAwardsCannotBeFinalisedWhileTheAwardsFeatureIsOff()
    {
        $this->enableFeature(Features::SCH_APPLICATIONS);
        $program = $this->program();
        $office = $this->app->scholarshipOffice();
        $a = $this->inReview($this->resident(), $program);
        $this->review($a, 9, 5);
        $this->close($program);
        T::eq([], $office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected'], '점수 순'));
        $problems = $office->approveDecisions($this->super(), $program['id'], '승인');
        T::ok(count($problems) === 1 && strpos($problems[0], '꺼져') !== false);
        T::ok(count($office->announce($this->super(), $program['id'], '발표')) > 0);
        T::eq('in_review', $this->db->val('SELECT status FROM scholarship_applications'));
    }

    // ── 이의신청 ────────────────────────────────────────────

    public function testAppealIsAllowedOnceWithinThePeriodAndReopensTheReview()
    {
        $this->enableScholarship();
        $program = $this->program(['capacity' => '1']);
        $office = $this->app->scholarshipOffice();
        $scholarships = $this->app->scholarships();
        $winner = $this->resident('11110', '지원자하나');
        $loser = $this->resident('11110', '지원자둘');
        $a = $this->inReview($winner, $program);
        $b = $this->inReview($loser, $program);
        $this->review($a, 9, 5);
        $this->review($b, 6, 3);
        $this->close($program);
        $office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected', $b => 'not_selected'], '점수 순');
        $office->approveDecisions($this->super(), $program['id'], '확인');
        $office->announce($this->super(), $program['id'], '발표');
        $publicB = $this->db->val('SELECT public_id FROM scholarship_applications WHERE id = ?', [$b]);
        $publicA = $this->db->val('SELECT public_id FROM scholarship_applications WHERE id = ?', [$a]);

        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($scholarships, $loser, $publicB) {
            $scholarships->appeal($loser, $publicB, '짧음');
        });
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($scholarships, $winner, $publicA) {
            $scholarships->appeal($winner, $publicA, str_repeat('선정되었지만 이의가 있습니다. ', 3));
        });
        $reason = '제출한 계획서의 두 번째 자료가 심사에 반영되지 않은 것 같습니다.';
        $view = $scholarships->appeal($loser, $publicB, $reason)['application'];
        T::eq('submitted', $view['appeal']['status']);
        T::throwsApi(ApiError::CONFLICT, function () use ($scholarships, $loser, $publicB, $reason) {
            $scholarships->appeal($loser, $publicB, $reason);
        });
        // 사유는 암호화되어 저장된다.
        T::ok(strpos(json_encode($this->db->all('SELECT * FROM scholarship_appeals'), JSON_UNESCAPED_UNICODE), '계획서') === false);
        $appealId = (int)$this->db->val('SELECT id FROM scholarship_appeals');
        T::eq($reason, $office->appealReason($this->schManager(), $appealId)['reason']);

        // 원래 결정을 제안한 사람은 처리할 수 없다.
        $problems = $office->handleAppeal($this->schManager(), $appealId, 'upheld', '자료를 다시 확인하겠습니다.');
        T::ok(count($problems) === 1 && strpos($problems[0], '원래 결정') !== false);
        T::eq([], $office->handleAppeal($this->super(), $appealId, 'upheld', '빠진 자료를 확인했습니다. 다시 심사합니다.'));
        T::ok(count($office->handleAppeal($this->super(), $appealId, 'dismissed', '이미 처리한 것을 또 처리한다')) > 0);
        // 그 신청은 심사 중으로 돌아가고 결과가 가려진다.
        $view = $scholarships->application($loser, $publicB)['application'];
        T::eq('in_review', $view['status']);
        T::eq(null, $view['result']);
        T::eq('upheld', $view['appeal']['status']);
        T::eq('reopened', $this->db->val('SELECT status FROM scholarship_awards WHERE application_id = ?', [$b]));
        // 다시 결정한다: 정원은 여전히 지켜진다 (이미 한 명이 선정되어 있다).
        T::ok(count($office->proposeDecisions($this->schManager(), $program['id'], [$b => 'selected'], '재심사')) > 0);
        T::eq([], $office->proposeDecisions($this->schManager(), $program['id'], [$b => 'waitlisted'], '재심사 결과 예비 선정'));
        T::eq([], $office->approveDecisions($this->super(), $program['id'], '재심사 확인'));
        T::eq([], $office->announce($this->super(), $program['id'], '재심사 결과 발표'));
        T::eq('waitlisted', $scholarships->application($loser, $publicB)['application']['result']);

        // 기간이 지나면 이의신청을 할 수 없다.
        $late = $this->program();
        $c = $this->inReview($this->resident('11110', '지원자셋'), $late);
        T::eq([], $office->decideEligibility($this->schManager(), $this->applicationId($this->apply($loser, $late)['application']['id']), false, '학교급이 맞지 않습니다'));
        $ineligible = $scholarships->myApplications($loser)['applications'][0];
        T::eq('ineligible', $ineligible['status']);
        T::eq('학교급이 맞지 않습니다', $ineligible['eligibilityNote']);
        T::ok($ineligible['canAppeal']);
        Clock::advance(8 * 86400);
        T::throwsApi(ApiError::VALIDATION_FAILED, function () use ($scholarships, $loser, $ineligible, $reason) {
            $scholarships->appeal($loser, $ineligible['id'], $reason);
        });
    }

    // ── 지급 상태 ───────────────────────────────────────────

    public function testDisbursementIsOnlyAStatusRecordWithSeparateApproval()
    {
        $this->enableScholarship();
        $program = $this->program(['capacity' => '1']);
        $office = $this->app->scholarshipOffice();
        $winner = $this->resident('11110', '지원자하나');
        $this->give($winner, 100);
        $gcBefore = $this->gc($winner);
        $a = $this->inReview($winner, $program);
        $b = $this->inReview($this->resident('11110', '지원자둘'), $program);
        $this->review($a, 9, 5);
        $this->review($b, 6, 3);
        $this->close($program);
        $finance = $this->admin('fin', Auth::ROLE_FINANCE);
        $input = ['support_type' => 'books', 'description' => '금융 교재 3권', 'amount_krw' => '45000'];

        // 발표 전에는 지급 기록을 만들 수 없다.
        $office->proposeDecisions($this->schManager(), $program['id'], [$a => 'selected', $b => 'not_selected'], '점수 순');
        $office->approveDecisions($this->super(), $program['id'], '확인');
        T::ok(count($office->createDisbursement($finance, $a, $input)) > 0);
        $office->announce($this->super(), $program['id'], '발표');
        // 선정되지 않은 사람에게는 만들 수 없다.
        T::ok(count($office->createDisbursement($finance, $b, $input)) > 0);
        // 장학 담당·심사위원은 지급 기록을 다룰 수 없다.
        foreach ([$this->schManager(), $this->reviewer(1), $this->compliance()] as $admin) {
            T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $admin, $a, $input) {
                $office->createDisbursement($admin, $a, $input);
            }, $admin['role']);
        }
        T::eq([], $office->createDisbursement($finance, $a, $input));
        $record = (int)$this->db->val('SELECT id FROM scholarship_disbursement_records');
        // 지급 담당이 보는 목록에는 이름이 없다 (접수 번호뿐).
        $list = $office->disbursements($finance);
        T::eq(1, count($list['awardees']));
        T::ok(strpos(json_encode($list, JSON_UNESCAPED_UNICODE), '홍길동') === false);
        T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $finance, $a) {
            $office->pii($finance, $a, '지급 대상 확인');
        });

        // 만든 사람은 승인할 수 없다. 승인 전에는 완료로 바꿀 수 없다.
        T::ok(count($office->disbursementTransition($finance, $record, 'approve', ['note' => '승인'])) > 0);
        T::ok(count($office->disbursementTransition($this->super(), $record, 'complete', ['note' => '완료', 'external_reference' => 'PAY-1'])) > 0);
        T::eq([], $office->disbursementTransition($this->super(), $record, 'approve', ['note' => '선정 결과와 금액 확인']));
        // 완료하려면 실제 지급을 처리한 절차의 문서 번호가 있어야 한다.
        T::ok(count($office->disbursementTransition($finance, $record, 'complete', ['note' => '교재 발송함', 'external_reference' => ''])) > 0);
        T::eq([], $office->disbursementTransition($finance, $record, 'complete', ['note' => '교재 발송함', 'external_reference' => 'PAY-2026-0001']));
        T::ok(count($office->disbursementTransition($finance, $record, 'cancel', ['note' => '완료한 것을 취소'])) > 0);

        $row = $this->db->one('SELECT * FROM scholarship_disbursement_records');
        T::eq('completed', $row['status']);
        T::eq('PAY-2026-0001', $row['external_reference']);
        $view = $this->app->scholarships()->myApplications($winner)['applications'][0];
        T::eq('completed', $view['disbursementStatus']);
        // 이 시스템은 돈을 보내지 않는다: 계좌를 담는 열이 없고, 게임머니도 그대로다.
        T::ok(!array_intersect(['account', 'account_no', 'bank', 'bank_code', 'holder'], array_keys($row)));
        T::eq($gcBefore, $this->gc($winner));
        foreach (glob(__DIR__ . '/../src/Service/Scholarship*.php') as $file) {
            T::ok(preg_match('/curl_|fsockopen|file_get_contents\(\s*[\'"]http|\bmail\(/', file_get_contents($file)) === 0, basename($file) . ' 가 밖으로 요청을 보낸다');
        }
    }

    // ── 개인정보 ────────────────────────────────────────────

    public function testPersonalDataAccessNeedsAReasonAndIsLogged()
    {
        $this->enableScholarship();
        $program = $this->program();
        $office = $this->app->scholarshipOffice();
        $id = $this->applicationId($this->apply($this->resident(), $program, $this->minorInput())['application']['id']);

        // 신청 목록에는 개인정보가 없다.
        $list = json_encode($office->applications($this->compliance(), $program['id']), JSON_UNESCAPED_UNICODE);
        foreach (['홍길동', '홍판서', 'student@example.com', '테스트중학교'] as $hidden) {
            T::ok(strpos($list, $hidden) === false, $hidden);
        }
        // 사유 없이는 열 수 없다.
        list($pii, $problems) = $office->pii($this->schManager(), $id, '');
        T::ok($pii === null && count($problems) > 0);
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM scholarship_audit_logs WHERE action = 'pii.view'"));
        list($pii, $problems) = $office->pii($this->schManager(), $id, '보호자에게 연락해 동의를 확인하려고', '203.0.113.9');
        T::eq([], $problems);
        T::eq('홍길동', $pii['name']);
        T::eq('student@example.com', $pii['email']);
        T::eq('홍판서', $pii['guardianName']);
        T::eq('010-1111-2222', $pii['guardianContact']);
        $log = $this->db->one("SELECT * FROM scholarship_audit_logs WHERE action = 'pii.view'");
        T::eq($this->schManager()['id'], (int)$log['actor_id']);
        T::eq('보호자에게 연락해 동의를 확인하려고', $log['detail']);
        T::eq('203.0.113.9', $log['ip']);
        T::eq($id, (int)$log['application_id']);

        // 준법 담당은 열람 기록을 보지만 개인정보는 열 수 없다. 장학 담당은 열람 기록을 볼 수 없다.
        T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $id) {
            $office->pii($this->compliance(), $id, '감사');
        });
        T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $id) {
            $office->applicationDetail($this->compliance(), $id);
        });
        $logs = $office->auditLog($this->compliance(), 'pii.view');
        T::eq(1, count($logs));
        T::eq('schmgr', $logs[0]['login']);
        T::throwsApi(ApiError::FORBIDDEN, function () use ($office) {
            $office->auditLog($this->schManager());
        });
        // CITY PASS 담당과 게임 운영자는 장학 데이터에 접근할 수 없다.
        foreach ([$this->passManager(), $this->admin('oper1', Auth::ROLE_OPERATOR)] as $admin) {
            T::throwsApi(ApiError::FORBIDDEN, function () use ($office, $admin, $program) {
                $office->applications($admin, $program['id']);
            }, $admin['role']);
        }
        // 신청서를 여는 것도 기록된다.
        $detail = $office->applicationDetail($this->schManager(), $id);
        T::ok(strpos($detail['form']['motivation'], '금융을 제대로') === 0);
        T::ok(strpos(json_encode($detail, JSON_UNESCAPED_UNICODE), '홍길동') === false, '신청서 화면에는 이름이 없다');
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM scholarship_audit_logs WHERE action = 'application.view'"));
    }

    public function testWithdrawnAndExpiredApplicationsArePurged()
    {
        $this->enableScholarship();
        $program = $this->program();
        $office = $this->app->scholarshipOffice();
        $scholarships = $this->app->scholarships();
        $player = $this->resident();
        $result = $this->apply($player, $program, $this->minorInput(['documents' => [['kind' => 'essay', 'title' => '나의 글', 'content' => '글 내용']]]));
        $public = $result['application']['id'];
        $id = $this->applicationId($public);

        $view = $scholarships->withdraw($player, $public)['application'];
        T::eq('withdrawn', $view['status']);
        T::ok(!$view['canWithdraw']);
        T::throwsApi(ApiError::CONFLICT, function () use ($scholarships, $player, $public) {
            $scholarships->withdraw($player, $public);
        });
        // 철회하면 보호자 동의 주소도 못 쓴다. 담당자가 심사로 넘길 수도 없다.
        T::eq(null, $scholarships->guardianConsentInfo($result['guardianConsent']['token'], '198.51.100.7'));
        T::ok(count($office->decideEligibility($this->schManager(), $id, true, '서류 확인')) > 0);
        T::eq(Clock::koreaDate(Clock::now() + 30 * 86400), $this->db->val('SELECT purge_after FROM scholarship_applications'));

        // 기한 전에는 지워지지 않는다.
        Clock::advance(29 * 86400);
        T::eq(0, $office->purgeDue());
        T::ok($this->db->val('SELECT name_enc FROM scholarship_applicant_profiles') !== null);
        Clock::advance(3 * 86400);
        T::eq(1, $office->purgeDue());
        T::eq(0, $office->purgeDue());

        $profile = $this->db->one('SELECT * FROM scholarship_applicant_profiles');
        foreach (['name_enc', 'birth_year', 'school_name_enc', 'email_enc', 'phone_enc'] as $column) {
            T::eq(null, $profile[$column], $column);
        }
        T::eq(null, $this->db->val('SELECT form_enc FROM scholarship_applications'));
        T::eq(null, $this->db->val('SELECT content_enc FROM scholarship_documents'));
        T::eq(null, $this->db->val("SELECT guardian_name_enc FROM scholarship_consents WHERE consent_type = 'guardian'"));
        T::ok($this->db->val('SELECT purged_at FROM scholarship_applications') !== null);
        // 지운 뒤에는 열어도 아무것도 나오지 않는다. 지웠다는 기록은 남는다.
        list($pii, ) = $office->pii($this->schManager(), $id, '지워졌는지 확인');
        T::eq(['purged' => true], $pii);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM scholarship_audit_logs WHERE action = 'application.purge'"));
        // 신청 자체(접수 번호, 상태)는 남아 본인이 볼 수 있다.
        T::eq('withdrawn', $scholarships->application($player, $public)['application']['status']);
    }

    public function testPrivacyNoticeListsWhatIsCollectedAndWhatIsNot()
    {
        $this->enableScholarship();
        $program = $this->program();
        $privacy = $this->app->scholarships()->program($program['public_id'])['privacy'];
        T::eq(Scholarships::PRIVACY_VERSION, $privacy['version']);
        T::eq(4, count($privacy['items']));
        T::ok(strpos($privacy['notCollected'], '주민등록번호') !== false && strpos($privacy['notCollected'], '계좌번호') !== false);
        T::ok(strpos($privacy['retention'], '180일') !== false);
        // 주민등록번호·계좌·주소를 담는 열이 없다.
        $columns = array_column($this->db->all("SELECT column_name AS c FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name LIKE 'scholarship\\_%'"), 'c');
        foreach ($columns as $column) {
            T::ok(!preg_match('/rrn|resident|ssn|account|bank|address|birth_date|birthday/i', $column), $column);
        }
    }
}
