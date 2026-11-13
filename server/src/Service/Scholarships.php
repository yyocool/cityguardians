<?php
namespace CG\Service;

use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Form;
use CG\Infra\Ids;

/**
 * GUARDIANS SCHOLARSHIP: 신청자 쪽 (docs/SCHOLARSHIP_SYSTEM.md).
 *
 * - 게임 성적, GC, 랭킹, 결제 내역은 자격에도 심사에도 쓰이지 않는다. 이 서비스는 전투·지갑·랭킹 테이블을 읽지 않는다.
 *   계정은 "누가 신청했는가"를 가리키는 데에만 쓴다.
 * - 개인정보는 목적별로 나눠 암호화해 저장한다 (신청서 / 본인·연락 정보 / 보호자). 주민등록번호와 계좌번호는 받지 않는다.
 * - 미성년자는 보호자 동의가 있어야 심사로 넘어간다.
 * - 지급은 여기에 없다. 선정 결과와 지급 상태를 보여 줄 뿐이다.
 */
final class Scholarships
{
    /** 개인정보 수집·이용 동의서의 판. 문구는 초안이다 (법률 검토 전). */
    const PRIVACY_VERSION = '2026-11-draft';
    /** 보호자 동의 나이의 바닥. 프로그램이 이보다 낮게 정할 수 없다. */
    const MIN_CONSENT_AGE = 14;

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    public static function categories()
    {
        return ['finance' => '금융 역량 장학', 'creativity' => '창의력 장학', 'leadership' => '협력·리더십 장학', 'growth' => '성장 지원 장학'];
    }

    public static function supportTypes()
    {
        return ['education_fee' => '교육비 지원', 'books' => '도서·교재 지원', 'online_pass' => '온라인 교육 이용권',
            'project' => '학습 프로젝트 지원', 'grant' => '별도 장학금'];
    }

    public static function schoolLevels()
    {
        return ['elementary' => '초등학생', 'middle' => '중학생', 'high' => '고등학생', 'university' => '대학생', 'out_of_school' => '학교 밖 청소년'];
    }

    /**
     * 지원 자격의 종류. 이 넷뿐이다.
     * 게임 레벨, GC, 전투 성적, 랭킹, 플레이 시간, 결제 내역을 보는 종류는 없고, 만들 수 없다.
     */
    public static function ruleTypes()
    {
        return ['school_level' => '학교급', 'birth_year' => '태어난 해', 'statement' => '본인이 확인하는 문구', 'document' => '제출 자료'];
    }

    public static function documentKinds()
    {
        return ['essay' => '글', 'plan' => '계획서', 'project' => '프로젝트 소개', 'proof' => '확인 자료', 'link' => '링크'];
    }

    public static function guardianRelations()
    {
        return ['parent' => '부모', 'grandparent' => '조부모', 'legal_guardian' => '그 밖의 법정대리인'];
    }

    public static function statusText($status)
    {
        $names = ['submitted' => '접수됨', 'ineligible' => '지원 자격에 해당하지 않음', 'in_review' => '심사 중', 'decided' => '심사 완료 (발표 전)',
            'announced' => '결과 발표', 'withdrawn' => '신청 철회'];
        return isset($names[$status]) ? $names[$status] : $status;
    }

    public static function resultText($result)
    {
        $names = ['selected' => '선정', 'waitlisted' => '예비 선정', 'not_selected' => '선정되지 않음'];
        return isset($names[$result]) ? $names[$result] : '';
    }

    /** 수집하는 개인정보의 항목·목적·보관 기간. 동의 화면과 보호자 화면에 그대로 보여 준다. */
    public static function privacyNotice(array $program)
    {
        return [
            'version' => self::PRIVACY_VERSION,
            'items' => [
                ['what' => '이름, 태어난 해, 학교급, 학교 이름', 'why' => '지원 자격 확인', 'who' => '장학 담당자'],
                ['what' => '이메일, 전화번호(선택)', 'why' => '결과 안내와 연락', 'who' => '장학 담당자'],
                ['what' => '지원 동기, 계획, 제출 자료', 'why' => '심사', 'who' => '장학 담당자, 배정된 심사위원 (이름과 연락처는 보지 못함)'],
                ['what' => '보호자의 이름, 관계, 연락처 (미성년자)', 'why' => '보호자 동의 확인', 'who' => '장학 담당자'],
            ],
            'notCollected' => '주민등록번호, 계좌번호, 주소는 받지 않습니다. 게임 플레이 기록·결제 내역·게임머니는 심사에 쓰지 않습니다.',
            'retention' => '선정되지 않은 신청은 결과 발표 뒤 ' . (int)$program['retention_days_unselected'] . '일, 선정된 신청은 '
                . (int)$program['retention_days_selected'] . '일 동안 보관한 뒤 지웁니다. 철회하면 30일 뒤에 지웁니다.',
            'draft' => '이 동의서는 초안입니다. 법률 검토를 거쳐 확정됩니다.',
        ];
    }

    // ── 공고 ────────────────────────────────────────────────

    public static function phase(array $program)
    {
        $now = Clock::now();
        if ($program['status'] === 'announced') {
            return 'announced';
        }
        if ($program['status'] === 'closed' || Clock::parse($program['apply_ends_at']) <= $now) {
            return 'closed';
        }
        return Clock::parse($program['apply_starts_at']) > $now ? 'upcoming' : 'open';
    }

    private static function summary(array $row)
    {
        return [
            'id' => $row['public_id'], 'category' => $row['category'], 'categoryName' => self::categories()[$row['category']],
            'title' => $row['title'], 'summary' => $row['summary'],
            'supportType' => $row['support_type'], 'supportTypeName' => self::supportTypes()[$row['support_type']], 'supportDetail' => $row['support_detail'],
            'capacity' => (int)$row['capacity'],
            'applyStartsAt' => Clock::isoFromSql($row['apply_starts_at']), 'applyEndsAt' => Clock::isoFromSql($row['apply_ends_at']),
            'resultOn' => $row['result_on'], 'phase' => self::phase($row),
        ];
    }

    private function applyUrl($programPublicId)
    {
        $base = $this->app->publicBase();
        return $base !== '' ? $base . '/scholarship/apply/' . $programPublicId : null;
    }

    /** 밖에 보이는 프로그램: 공고가 승인된 것만. 신청 접수 기능이 꺼져 있으면 아무것도 보이지 않는다. */
    public function programs()
    {
        $enabled = $this->app->features()->isEnabled(Features::SCH_APPLICATIONS);
        $out = ['applicationsEnabled' => $enabled, 'programs' => [],
            'notice' => $enabled ? '' : '장학 프로그램은 준비 중이에요. 모집이 시작되면 여기에 공고가 올라와요.',
            // 신청은 게임 안이 아니라 홈페이지에서 한다.
            'siteUrl' => $this->app->publicBase() !== '' ? $this->app->publicBase() . '/scholarship' : null];
        if (!$enabled) {
            return $out;
        }
        foreach ($this->app->db->all(
            "SELECT * FROM scholarship_programs WHERE status IN ('published', 'closed', 'announced') ORDER BY apply_ends_at DESC, id DESC LIMIT 100") as $row) {
            $out['programs'][] = self::summary($row);
        }
        return $out;
    }

    private function visibleProgram($publicId, $lock = false)
    {
        if (!is_string($publicId) || !preg_match('/^[a-z0-9_]{16}$/', $publicId)) {
            throw new ApiError(ApiError::INVALID_REQUEST, '프로그램을 다시 골라 주세요.');
        }
        $this->app->features()->mustBeEnabled(Features::SCH_APPLICATIONS);
        $row = $this->app->db->one("SELECT * FROM scholarship_programs WHERE public_id = ? AND status IN ('published', 'closed', 'announced')"
            . ($lock ? ' FOR UPDATE' : ''), [$publicId]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 프로그램이에요.');
        }
        return $row;
    }

    public function rulesOf($programId)
    {
        return $this->app->db->all('SELECT * FROM scholarship_eligibility_rules WHERE program_id = ? ORDER BY sort_order, id', [$programId]);
    }

    public function criteriaOf($programId)
    {
        return $this->app->db->all('SELECT * FROM scholarship_criteria WHERE program_id = ? ORDER BY sort_order, id', [$programId]);
    }

    public function program($publicId)
    {
        $row = $this->visibleProgram($publicId);
        return self::summary($row) + [
            'description' => (string)$row['description'],
            'eligibility' => array_map(function ($rule) {
                return ['id' => (int)$rule['id'], 'type' => $rule['rule_type'], 'description' => $rule['description']];
            }, $this->rulesOf($row['id'])),
            'criteria' => array_map(function ($criterion) {
                return ['name' => $criterion['name'], 'description' => $criterion['description'], 'maxScore' => (int)$criterion['max_score'],
                    'weight' => (int)$criterion['weight']];
            }, $this->criteriaOf($row['id'])),
            'guardianConsentAge' => (int)$row['guardian_consent_age'],
            'appealDays' => (int)$row['appeal_days'],
            'privacy' => self::privacyNotice($row),
            'applyUrl' => $this->applyUrl($row['public_id']),
            'notUsedInReview' => '게임 플레이 기록, 전투 성적, 도시 랭킹, 게임머니, 유료 결제 내역은 자격 확인과 심사에 쓰이지 않아요.',
        ];
    }

    // ── 신청 ────────────────────────────────────────────────

    public function audit($actorType, $actorId, $action, $programId, $applicationId, $detail = null, $ip = '')
    {
        $this->app->db->exec(
            'INSERT INTO scholarship_audit_logs (actor_type, actor_id, action, program_id, application_id, detail, ip, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
            [$actorType, $actorId, mb_substr($action, 0, 64), $programId, $applicationId, $detail !== null ? mb_substr((string)$detail, 0, 500) : null,
                mb_substr((string)$ip, 0, 45), Clock::sql()]
        );
    }

    /** 태어난 해만으로 보는 나이의 가장 어린 쪽: 올해 생일이 아직 안 지났다고 본다. */
    public static function youngestAge($birthYear)
    {
        return (int)substr(Clock::koreaDate(), 0, 4) - (int)$birthYear - 1;
    }

    /**
     * 신청서를 낸다.
     *
     * @param array $input applicant{name,birthYear,schoolLevel,schoolName,email,phone}, form{motivation,plan,statements[]},
     *                     documents[{kind,title,content,url}], consents{privacy}, guardian{name,relation,contact}
     * @return array application, guardianConsent (미성년자면 보호자에게 전할 주소)
     */
    public function apply(array $player, $programPublicId, array $input, $ip = '')
    {
        $this->app->features()->mustBeEnabled(Features::SCH_APPLICATIONS);
        if (!$this->app->crypto()->ready()) {
            // 개인정보를 평문으로 저장하는 길은 없다.
            throw new ApiError(ApiError::FEATURE_DISABLED, '지금은 신청을 받을 수 없어요.', 'crypto_not_ready');
        }
        $this->app->rateLimit()->hit('sch_apply', 'p' . $player['id'], 20, 3600);

        $section = function ($key) use ($input) {
            return isset($input[$key]) && is_array($input[$key]) ? $input[$key] : [];
        };
        $stringify = function (array $values) {
            // 숫자로 온 값(태어난 해)도 글자로 바꿔 한 가지 방법으로 확인한다. 배열은 버린다.
            $out = [];
            foreach ($values as $key => $value) {
                if (is_string($value) || is_int($value)) {
                    $out[$key] = (string)$value;
                }
            }
            return $out;
        };
        $applicant = new Form($stringify($section('applicant')));
        $thisYear = (int)substr(Clock::koreaDate(), 0, 4);
        $name = $applicant->text('name', '이름', 2, 40);
        $birthYear = $applicant->int('birthYear', '태어난 해', $thisYear - 40, $thisYear - 6);
        $schoolLevel = $applicant->choice('schoolLevel', '학교급', self::schoolLevels());
        $schoolName = $applicant->optional('schoolName', '학교 이름', 80);
        $email = $applicant->email('email', '이메일', true);
        $phone = $applicant->phone('phone', '전화번호');

        $formInput = $section('form');
        $form = new Form($stringify($formInput));
        $motivation = $form->text('motivation', '지원 동기', 50, 2000, true);
        $plan = $form->text('plan', '활동·학습 계획', 50, 2000, true);
        $confirmed = isset($formInput['statements']) && is_array($formInput['statements'])
            ? array_map('intval', array_filter($formInput['statements'], 'is_scalar')) : [];

        $problems = array_merge($applicant->problems, $form->problems);
        $consents = $section('consents');
        if (!isset($consents['privacy']) || $consents['privacy'] !== true) {
            $problems[] = '개인정보 수집·이용에 동의해야 신청할 수 있습니다.';
        }

        $documents = [];
        $rawDocuments = isset($input['documents']) && is_array($input['documents']) ? array_values($input['documents']) : [];
        if (count($rawDocuments) > 5) {
            $problems[] = '제출 자료는 5개까지 낼 수 있습니다.';
            $rawDocuments = [];
        }
        foreach ($rawDocuments as $index => $raw) {
            $doc = new Form($stringify(is_array($raw) ? $raw : []));
            $label = '제출 자료 ' . ($index + 1);
            $document = [
                'kind' => $doc->choice('kind', $label . ' 종류', self::documentKinds()),
                'title' => $doc->text('title', $label . ' 제목', 1, 120),
                'content' => $doc->optional('content', $label . ' 내용', 5000, true),
                'url' => $doc->url('url', $label . ' 주소'),
            ];
            if ($doc->ok() && $document['content'] === null && $document['url'] === null) {
                $doc->fail($label . ': 내용이나 주소 가운데 하나는 있어야 합니다.');
            }
            $problems = array_merge($problems, $doc->problems);
            $documents[] = $document;
        }

        $db = $this->app->db;
        $guardianToken = null;
        $applicationId = $db->tx(function () use ($db, $player, $programPublicId, $input, $section, $stringify, $name, $birthYear, $schoolLevel, $schoolName,
            $email, $phone, $motivation, $plan, $confirmed, $documents, $ip, &$problems, &$guardianToken) {
            $program = $this->visibleProgram($programPublicId, true);
            if (self::phase($program) !== 'open') {
                throw new ApiError(ApiError::VALIDATION_FAILED, '지금은 신청 기간이 아니에요.', 'not_open');
            }
            $existing = $db->one('SELECT status FROM scholarship_applications WHERE program_id = ? AND player_id = ?', [$program['id'], $player['id']]);
            if ($existing !== null) {
                throw new ApiError(ApiError::CONFLICT, $existing['status'] === 'withdrawn'
                    ? '철회한 신청은 같은 프로그램에 다시 낼 수 없어요.' : '이 프로그램에는 이미 신청했어요.', 'already_applied');
            }

            // 자격 규칙. 여기서 읽는 것은 신청서에 적은 값뿐이다 (게임 데이터를 보지 않는다).
            $statements = [];
            foreach ($this->rulesOf($program['id']) as $rule) {
                $params = $rule['params'] !== null ? (array)json_decode($rule['params'], true) : [];
                switch ($rule['rule_type']) {
                    case 'school_level':
                        if (!in_array($schoolLevel, isset($params['levels']) ? (array)$params['levels'] : [], true)) {
                            $problems[] = '지원 자격: ' . $rule['description'];
                        }
                        break;
                    case 'birth_year':
                        if ($birthYear < (isset($params['min']) ? (int)$params['min'] : 0) || $birthYear > (isset($params['max']) ? (int)$params['max'] : 9999)) {
                            $problems[] = '지원 자격: ' . $rule['description'];
                        }
                        break;
                    case 'statement':
                        if (!in_array((int)$rule['id'], $confirmed, true)) {
                            $problems[] = '확인이 필요합니다: ' . $rule['description'];
                        } else {
                            $statements[] = $rule['description'];
                        }
                        break;
                    case 'document':
                        $kind = isset($params['kind']) ? $params['kind'] : '';
                        if (!array_filter($documents, function ($document) use ($kind) {
                            return $document['kind'] === $kind;
                        })) {
                            $problems[] = '제출 자료가 필요합니다: ' . $rule['description'];
                        }
                        break;
                }
            }

            // 보호자 동의가 필요한가. 태어난 해만 받으므로, 생일이 아직 지나지 않았다고 보고 판단한다.
            $consentAge = max(self::MIN_CONSENT_AGE, (int)$program['guardian_consent_age']);
            $minor = self::youngestAge($birthYear) < $consentAge;
            $guardian = null;
            if ($minor) {
                $g = new Form($stringify($section('guardian')));
                $guardian = [
                    'name' => $g->text('name', '보호자 이름', 2, 40),
                    'relation' => $g->choice('relation', '보호자와의 관계', self::guardianRelations()),
                    'contact' => $g->text('contact', '보호자 연락처', 5, 120),
                ];
                $problems = array_merge($problems, $g->problems);
            }
            if ($problems) {
                return null;
            }

            $crypto = $this->app->crypto();
            $publicId = Ids::publicId('a_');
            $now = Clock::sql();
            $id = $db->insert(
                'INSERT INTO scholarship_applications (public_id, program_id, player_id, status, eligibility_status, consent_status, school_level, is_minor, form_enc, submitted_at, updated_at)'
                . " VALUES (?, ?, ?, 'submitted', 'pending', ?, ?, ?, ?, ?, ?)",
                [$publicId, $program['id'], $player['id'], $minor ? 'pending' : 'not_required', $schoolLevel, $minor ? 1 : 0,
                    $crypto->seal(json_encode(['motivation' => $motivation, 'plan' => $plan, 'statements' => $statements], JSON_UNESCAPED_UNICODE), 'application.form'),
                    $now, $now]);
            $db->exec('INSERT INTO scholarship_applicant_profiles (application_id, name_enc, birth_year, school_name_enc, email_enc, phone_enc, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                [$id, $crypto->seal($name, 'profile.name'), $birthYear, $crypto->seal($schoolName, 'profile.school'), $crypto->seal($email, 'profile.email'),
                    $crypto->seal($phone, 'profile.phone'), $now]);
            foreach ($documents as $document) {
                $db->exec('INSERT INTO scholarship_documents (application_id, kind, title, content_enc, url, created_at) VALUES (?, ?, ?, ?, ?, ?)',
                    [$id, $document['kind'], $document['title'], $crypto->seal($document['content'], 'document.content'), $document['url'], $now]);
            }
            $db->exec("INSERT INTO scholarship_consents (application_id, consent_type, status, version, confirmed_at, created_at, updated_at) VALUES (?, 'privacy', 'given', ?, ?, ?, ?)",
                [$id, self::PRIVACY_VERSION, $now, $now, $now]);
            if ($minor) {
                $guardianToken = Ids::secret();
                $db->exec('INSERT INTO scholarship_consents (application_id, consent_type, status, version, guardian_name_enc, guardian_relation, guardian_contact_enc,'
                    . " token_hash, token_expires_at, created_at, updated_at) VALUES (?, 'guardian', 'pending', ?, ?, ?, ?, ?, ?, ?, ?)",
                    [$id, self::PRIVACY_VERSION, $crypto->seal($guardian['name'], 'guardian.name'), $guardian['relation'],
                        $crypto->seal($guardian['contact'], 'guardian.contact'), Ids::sha256($guardianToken),
                        Clock::sql(Clock::parse($program['apply_ends_at']) + 14 * 86400), $now, $now]);
            }
            $this->audit('applicant', $player['id'], 'application.submit', $program['id'], $id, $minor ? 'minor' : null, $ip);
            return $id;
        });
        if ($applicationId === null) {
            $error = new ApiError(ApiError::VALIDATION_FAILED, implode(' ', $problems), 'invalid_application');
            $error->problems = $problems;
            throw $error;
        }
        $row = $this->own($player, null, $applicationId);
        return ['application' => $this->view($row), 'guardianConsent' => $guardianToken !== null ? $this->guardianLink($guardianToken) : null];
    }

    private function guardianLink($token)
    {
        $base = $this->app->publicBase();
        return [
            'required' => true,
            // 이 주소는 지금 한 번만 보여 준다. 보호자에게 직접 전해야 한다 (서버는 문자나 메일을 보내지 않는다).
            'token' => $token,
            'url' => $base !== '' ? $base . '/scholarship/consent/' . $token : null,
            'message' => '보호자가 이 주소를 열어 동의해야 심사로 넘어가요. 그 뒤 장학 담당자가 보호자에게 연락해 한 번 더 확인해요.',
        ];
    }

    private function own(array $player, $publicId, $id = null)
    {
        if ($id === null && (!is_string($publicId) || !preg_match('/^[a-z0-9_]{16}$/', $publicId))) {
            throw new ApiError(ApiError::INVALID_REQUEST, '신청을 다시 골라 주세요.');
        }
        // 남의 신청은 없는 것으로 답한다.
        $row = $this->app->db->one(
            'SELECT a.*, p.public_id AS program_public_id, p.title, p.category, p.appeal_days, p.status AS program_status FROM scholarship_applications a'
            . ' JOIN scholarship_programs p ON p.id = a.program_id WHERE a.player_id = ? AND ' . ($id !== null ? 'a.id = ?' : 'a.public_id = ?'),
            [$player['id'], $id !== null ? $id : $publicId]);
        if ($row === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 신청이에요.');
        }
        return $row;
    }

    private function appealOf($applicationId)
    {
        return $this->app->db->one('SELECT status, response, created_at, handled_at FROM scholarship_appeals WHERE application_id = ?', [$applicationId]);
    }

    private static function appealDeadline(array $row)
    {
        $from = null;
        if ($row['status'] === 'announced' && in_array($row['result'], ['not_selected', 'waitlisted'], true)) {
            $from = $row['result_notified_at'];
        } elseif ($row['status'] === 'ineligible') {
            $from = $row['eligibility_at'];
        }
        return $from !== null ? Clock::parse($from) + (int)$row['appeal_days'] * 86400 : null;
    }

    private function view(array $row)
    {
        $appeal = $this->appealOf($row['id']);
        $deadline = self::appealDeadline($row);
        $announced = $row['status'] === 'announced';
        $consentText = ['not_required' => '필요 없음', 'pending' => '보호자 동의를 기다리는 중', 'guardian_confirmed' => '보호자가 동의함 (담당자 확인 전)',
            'verified' => '보호자 동의 확인됨'];
        return [
            'id' => $row['public_id'],
            'program' => ['id' => $row['program_public_id'], 'title' => $row['title'], 'categoryName' => self::categories()[$row['category']]],
            'status' => $row['status'], 'statusText' => self::statusText($row['status']),
            'submittedAt' => Clock::isoFromSql($row['submitted_at']),
            'eligibilityStatus' => $row['eligibility_status'],
            'eligibilityNote' => $row['status'] === 'ineligible' ? $row['eligibility_note'] : null,
            'isMinor' => (int)$row['is_minor'] === 1,
            'consentStatus' => $row['consent_status'], 'consentText' => $consentText[$row['consent_status']],
            // 결과는 발표한 뒤에만 보인다.
            'result' => $announced ? $row['result'] : null, 'resultText' => $announced ? self::resultText($row['result']) : '',
            'resultNotifiedAt' => $announced ? Clock::isoFromSql($row['result_notified_at']) : null,
            'disbursementStatus' => $announced ? $row['disbursement_status'] : null,
            'canWithdraw' => in_array($row['status'], ['submitted', 'in_review'], true),
            'canAppeal' => $appeal === null && $deadline !== null && $deadline > Clock::now(),
            'appealDeadline' => $deadline !== null ? Clock::iso($deadline) : null,
            'appeal' => $appeal !== null ? ['status' => $appeal['status'], 'response' => $appeal['response'],
                'createdAt' => Clock::isoFromSql($appeal['created_at'])] : null,
            'canReissueGuardianLink' => $row['consent_status'] === 'pending' && in_array($row['status'], ['submitted'], true),
        ];
    }

    /** 내 신청 목록. 신청 접수 기능이 꺼져 있어도 본인은 볼 수 있다. */
    public function myApplications(array $player)
    {
        $rows = $this->app->db->all(
            'SELECT a.*, p.public_id AS program_public_id, p.title, p.category, p.appeal_days, p.status AS program_status FROM scholarship_applications a'
            . ' JOIN scholarship_programs p ON p.id = a.program_id WHERE a.player_id = ? ORDER BY a.id DESC LIMIT 50', [$player['id']]);
        return ['applications' => array_map(function ($row) {
            return $this->view($row);
        }, $rows)];
    }

    public function application(array $player, $publicId, $ip = '')
    {
        $row = $this->own($player, $publicId);
        if ($row['status'] === 'announced' && $row['result_viewed_at'] === null) {
            $this->app->db->exec('UPDATE scholarship_applications SET result_viewed_at = ? WHERE id = ?', [Clock::sql(), $row['id']]);
            $this->audit('applicant', $player['id'], 'result.view', $row['program_id'], $row['id'], null, $ip);
        }
        return ['application' => $this->view($row)];
    }

    /** 신청을 철회한다. 30일 뒤에 개인정보를 지운다. */
    public function withdraw(array $player, $publicId, $ip = '')
    {
        $db = $this->app->db;
        $db->tx(function () use ($db, $player, $publicId, $ip) {
            $row = $this->own($player, $publicId);
            $changed = $db->exec("UPDATE scholarship_applications SET status = 'withdrawn', withdrawn_at = ?, purge_after = ?, updated_at = ?"
                . " WHERE id = ? AND status IN ('submitted', 'in_review')",
                [Clock::sql(), Clock::koreaDate(Clock::now() + 30 * 86400), Clock::sql(), $row['id']]);
            if ($changed !== 1) {
                throw new ApiError(ApiError::CONFLICT, '지금은 철회할 수 없어요.', 'cannot_withdraw');
            }
            $db->exec("UPDATE scholarship_consents SET status = 'withdrawn', token_hash = NULL, updated_at = ? WHERE application_id = ?", [Clock::sql(), $row['id']]);
            $this->audit('applicant', $player['id'], 'application.withdraw', $row['program_id'], $row['id'], null, $ip);
        });
        return $this->application($player, $publicId, $ip);
    }

    /** 이의신청. 신청 하나에 한 번, 정해진 기간 안에. */
    public function appeal(array $player, $publicId, $reason, $ip = '')
    {
        $this->app->rateLimit()->hit('sch_appeal', 'p' . $player['id'], 10, 3600);
        $form = new Form(['reason' => is_string($reason) ? $reason : '']);
        $text = $form->text('reason', '이의신청 사유', 20, 2000, true);
        if (!$form->ok()) {
            throw new ApiError(ApiError::VALIDATION_FAILED, implode(' ', $form->problems), 'invalid_appeal');
        }
        $db = $this->app->db;
        $db->tx(function () use ($db, $player, $publicId, $text, $ip) {
            $row = $this->own($player, $publicId);
            $db->val('SELECT id FROM scholarship_applications WHERE id = ? FOR UPDATE', [$row['id']]);
            $deadline = self::appealDeadline($row);
            if ($deadline === null || $deadline <= Clock::now()) {
                throw new ApiError(ApiError::VALIDATION_FAILED, '이의신청을 할 수 있는 기간이 아니에요.', 'appeal_closed');
            }
            if ($this->appealOf($row['id']) !== null) {
                throw new ApiError(ApiError::CONFLICT, '이의신청은 한 번만 할 수 있어요.', 'already_appealed');
            }
            $db->exec("INSERT INTO scholarship_appeals (application_id, reason_enc, status, created_at) VALUES (?, ?, 'submitted', ?)",
                [$row['id'], $this->app->crypto()->seal($text, 'appeal.reason'), Clock::sql()]);
            $this->audit('applicant', $player['id'], 'appeal.submit', $row['program_id'], $row['id'], null, $ip);
        });
        return $this->application($player, $publicId, $ip);
    }

    // ── 보호자 동의 ─────────────────────────────────────────

    /** 보호자에게 전할 주소를 새로 만든다 (앞의 주소는 못 쓴다). */
    public function reissueGuardianLink(array $player, $publicId, $ip = '')
    {
        $this->app->features()->mustBeEnabled(Features::SCH_APPLICATIONS);
        $this->app->rateLimit()->hit('sch_guardian_link', 'p' . $player['id'], 5, 3600);
        $row = $this->own($player, $publicId);
        if ($row['consent_status'] !== 'pending' || $row['status'] !== 'submitted') {
            throw new ApiError(ApiError::CONFLICT, '지금은 보호자 동의 주소를 새로 만들 수 없어요.', 'consent_not_pending');
        }
        $token = Ids::secret();
        $this->app->db->exec("UPDATE scholarship_consents SET token_hash = ?, updated_at = ? WHERE application_id = ? AND consent_type = 'guardian' AND status = 'pending'",
            [Ids::sha256($token), Clock::sql(), $row['id']]);
        $this->audit('applicant', $player['id'], 'consent.link_reissue', $row['program_id'], $row['id'], null, $ip);
        return ['guardianConsent' => $this->guardianLink($token)];
    }

    private function pendingConsent($token, $lock = false)
    {
        if (!is_string($token) || !preg_match('/^[0-9a-f]{64}$/', $token)) {
            return null;
        }
        return $this->app->db->one(
            'SELECT c.*, a.program_id, a.status AS application_status FROM scholarship_consents c JOIN scholarship_applications a ON a.id = c.application_id'
            . " WHERE c.token_hash = ? AND c.consent_type = 'guardian' AND c.status = 'pending' AND c.token_expires_at > ? AND a.status = 'submitted'"
            . ($lock ? ' FOR UPDATE' : ''), [Ids::sha256($token), Clock::sql()]);
    }

    /** 보호자 화면에 보여 줄 내용. 주소가 맞지 않거나 이미 썼으면 null. */
    public function guardianConsentInfo($token, $ip = '')
    {
        $this->app->rateLimit()->hit('sch_consent', $ip, 30, 600);
        if (!$this->app->features()->isEnabled(Features::SCH_APPLICATIONS) || !$this->app->crypto()->ready()) {
            return null;
        }
        $consent = $this->pendingConsent($token);
        if ($consent === null) {
            return null;
        }
        $program = $this->app->db->one('SELECT * FROM scholarship_programs WHERE id = ?', [$consent['program_id']]);
        $name = $this->app->crypto()->open(
            $this->app->db->val('SELECT name_enc FROM scholarship_applicant_profiles WHERE application_id = ?', [$consent['application_id']]), 'profile.name');
        $this->audit('guardian', null, 'consent.view', $consent['program_id'], $consent['application_id'], null, $ip);
        return [
            'applicantName' => $name, 'programTitle' => $program['title'],
            'supportTypeName' => self::supportTypes()[$program['support_type']], 'supportDetail' => $program['support_detail'],
            'privacy' => self::privacyNotice($program),
        ];
    }

    /**
     * 보호자가 동의한다. 적은 이름이 신청서의 보호자 이름과 같아야 한다.
     * 이것만으로 보호자 본인임이 증명되지는 않는다: 그래서 담당자가 연락해 확인하는 단계가 따로 있다.
     * @return string[] 문제 목록. 비어 있으면 동의가 기록되었다.
     */
    public function confirmGuardianConsent($token, $guardianName, $agree, $ip = '')
    {
        $this->app->rateLimit()->hit('sch_consent', $ip, 30, 600);
        $this->app->features()->mustBeEnabled(Features::SCH_APPLICATIONS);
        $db = $this->app->db;
        return $db->tx(function () use ($db, $token, $guardianName, $agree, $ip) {
            $consent = $this->pendingConsent($token, true);
            if ($consent === null) {
                return ['이 주소는 쓸 수 없습니다. 신청자에게 새 주소를 받아 주세요.'];
            }
            $problems = [];
            if ($agree !== true) {
                $problems[] = '동의에 표시해 주세요.';
            }
            $expected = $this->app->crypto()->open($consent['guardian_name_enc'], 'guardian.name');
            $normalize = function ($value) {
                return mb_strtolower(preg_replace('/\s+/u', '', (string)$value));
            };
            if (!is_string($guardianName) || $normalize($guardianName) === '' || $normalize($guardianName) !== $normalize($expected)) {
                $problems[] = '보호자 이름이 신청서에 적힌 것과 다릅니다.';
            }
            if ($problems) {
                return $problems;
            }
            // 주소는 한 번만 쓸 수 있다.
            $db->exec("UPDATE scholarship_consents SET status = 'guardian_confirmed', confirmed_at = ?, token_hash = NULL, updated_at = ? WHERE id = ?",
                [Clock::sql(), Clock::sql(), $consent['id']]);
            $db->exec("UPDATE scholarship_applications SET consent_status = 'guardian_confirmed', updated_at = ? WHERE id = ?", [Clock::sql(), $consent['application_id']]);
            $this->audit('guardian', null, 'consent.confirm', $consent['program_id'], $consent['application_id'], null, $ip);
            return [];
        });
    }
}
