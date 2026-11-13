<?php
namespace CG\Admin;

use CG\Infra\Form;
use CG\Infra\Request;
use CG\Service\Features;
use CG\Service\ScholarshipOffice;
use CG\Service\Scholarships;

/**
 * 관리자 화면: 장학사업 관리 (docs/ADMIN_SYSTEM.md 11장, docs/SCHOLARSHIP_REVIEW_POLICY.md).
 *
 *   프로그램 · 신청자 · 자격 심사 · 심사위원 · 평가 결과 · 최종 승인 · 지급 상태 · 개인정보 접근 로그
 *
 * 신청자는 접수 번호로만 보인다. 이름과 연락처는 사유를 적고 따로 열어야 하고, 열 때마다 기록이 남는다.
 * 심사위원은 "내 심사" 화면만 쓴다: 배정받은 지원서를, 이름·연락처·학교 이름 없이 본다.
 * 게임 닉네임·레벨·GC·전투 기록은 어느 화면에도 나오지 않는다.
 */
trait ScholarshipScreens
{
    private function routeScholarship($get, $path, Request $request)
    {
        $form = $request->form;
        $ip = $request->ip;
        $office = $this->app->scholarshipOffice();
        $base = '/admin/scholarship';
        if ($path === "$base/programs") {
            if ($get) {
                return $this->programList($request);
            }
            list($id, $problems) = $office->saveProgram($this->admin, null, $form, $ip);
            return $problems ? $this->programNew($problems, $form) : ['redirect' => "$base/programs/$id?done=saved"];
        }
        if ($path === "$base/programs/new") {
            return $get ? $this->programNew([], null) : null;
        }
        if (preg_match("#^$base/programs/(\\d{1,18})$#", $path, $m)) {
            if ($get) {
                return $this->programDetail($m[1], [], null, $request);
            }
            list(, $problems) = $office->saveProgram($this->admin, self::id($m[1]), $form, $ip);
            return $problems ? $this->programDetail($m[1], $problems, $form, $request) : ['redirect' => "$base/programs/" . self::id($m[1]) . '?done=saved'];
        }
        if (preg_match("#^$base/programs/(\\d{1,18})/(transition|rules|criteria|reviewers)$#", $path, $m) && !$get) {
            switch ($m[2]) {
                case 'transition': $problems = $office->programTransition($this->admin, $m[1], self::field($form, 'action'), self::field($form, 'reason'), $ip); $done = 'changed'; break;
                case 'rules': $problems = $office->addRule($this->admin, $m[1], $form, $ip); $done = 'added'; break;
                case 'criteria': $problems = $office->addCriterion($this->admin, $m[1], $form, $ip); $done = 'added'; break;
                default:
                    $problems = $office->setReviewer($this->admin, $m[1], self::id(self::field($form, 'admin_id')), self::field($form, 'active') === '1', $ip);
                    $done = 'changed';
            }
            return $problems ? $this->programDetail($m[1], $problems, null, $request) : ['redirect' => "$base/programs/" . self::id($m[1]) . '?done=' . $done];
        }
        if (preg_match("#^$base/programs/(\\d{1,18})/(rules|criteria)/(\\d{1,18})/remove$#", $path, $m) && !$get) {
            $problems = $office->removeItem($this->admin, $m[1], $m[2] === 'rules' ? 'rule' : 'criterion', $m[3], $ip);
            return $problems ? $this->programDetail($m[1], $problems, null, $request) : ['redirect' => "$base/programs/" . self::id($m[1]) . '?done=removed'];
        }
        if (preg_match("#^$base/programs/(\\d{1,18})/applications$#", $path, $m)) {
            return $get ? $this->applicationList($m[1], $request) : null;
        }
        if (preg_match("#^$base/programs/(\\d{1,18})/results$#", $path, $m)) {
            return $get ? $this->resultsPage($m[1], [], $request) : null;
        }
        if (preg_match("#^$base/programs/(\\d{1,18})/results/(propose|approve|announce)$#", $path, $m) && !$get) {
            if ($m[2] === 'propose') {
                $decisions = [];
                foreach ($form as $key => $value) {
                    if (preg_match('/^decision_(\d{1,18})$/', $key, $k)) {
                        $decisions[(int)$k[1]] = $value;
                    }
                }
                $problems = $office->proposeDecisions($this->admin, $m[1], $decisions, self::field($form, 'note'), $ip);
                $done = 'proposed';
            } elseif ($m[2] === 'approve') {
                $problems = $office->approveDecisions($this->admin, $m[1], self::field($form, 'reason'), $ip);
                $done = 'approved';
            } else {
                $problems = $office->announce($this->admin, $m[1], self::field($form, 'reason'), $ip);
                $done = 'announced';
            }
            return $problems ? $this->resultsPage($m[1], $problems, $request) : ['redirect' => "$base/programs/" . self::id($m[1]) . '/results?done=' . $done];
        }
        if (preg_match("#^$base/applications/(\\d{1,18})$#", $path, $m)) {
            return $get ? $this->applicationPage($m[1], [], null, $request) : null;
        }
        if (preg_match("#^$base/applications/(\\d{1,18})/(pii|consent|eligibility)$#", $path, $m) && !$get) {
            if ($m[2] === 'pii') {
                list($pii, $problems) = $office->pii($this->admin, $m[1], self::field($form, 'reason'), $ip);
                return $this->applicationPage($m[1], $problems, $pii, $request);
            }
            $problems = $m[2] === 'consent'
                ? $office->verifyConsent($this->admin, $m[1], $form, $ip)
                : $office->decideEligibility($this->admin, $m[1], self::field($form, 'eligible') === '1', self::field($form, 'note'), $ip);
            return $problems ? $this->applicationPage($m[1], $problems, null, $request) : ['redirect' => "$base/applications/" . self::id($m[1]) . '?done=changed'];
        }
        if ($path === "$base/reviews") {
            return $get ? $this->reviewQueuePage($request) : null;
        }
        if (preg_match("#^$base/reviews/(\\d{1,18})$#", $path, $m)) {
            if ($get) {
                return $this->reviewPage($m[1], [], null, $request);
            }
            $problems = $office->submitReview($this->admin, $m[1], $form, $ip);
            return $problems ? $this->reviewPage($m[1], $problems, $form, $request) : ['redirect' => "$base/reviews?done=submitted"];
        }
        if (preg_match("#^$base/reviews/(\\d{1,18})/recuse$#", $path, $m) && !$get) {
            $problems = $office->recuse($this->admin, $m[1], self::field($form, 'reason'), $ip);
            return $problems ? $this->reviewPage($m[1], $problems, null, $request) : ['redirect' => "$base/reviews?done=recused"];
        }
        if ($path === "$base/appeals") {
            return $get ? $this->appealList($request) : null;
        }
        if (preg_match("#^$base/appeals/(\\d{1,18})$#", $path, $m)) {
            if ($get) {
                return $this->appealPage($m[1], [], $request);
            }
            $problems = $office->handleAppeal($this->admin, $m[1], self::field($form, 'outcome'), self::field($form, 'response'), $ip);
            return $problems ? $this->appealPage($m[1], $problems, $request) : ['redirect' => "$base/appeals?done=handled"];
        }
        if ($path === "$base/disbursements") {
            if ($get) {
                return $this->disbursementPage([], $request);
            }
            $problems = $office->createDisbursement($this->admin, self::id(self::field($form, 'application_id')), $form, $ip);
            return $problems ? $this->disbursementPage($problems, $request) : ['redirect' => "$base/disbursements?done=saved"];
        }
        if (preg_match("#^$base/disbursements/(\\d{1,18})/(approve|complete|cancel)$#", $path, $m) && !$get) {
            $problems = $office->disbursementTransition($this->admin, $m[1], $m[2], $form, $ip);
            return $problems ? $this->disbursementPage($problems, $request) : ['redirect' => "$base/disbursements?done=changed"];
        }
        if ($path === "$base/audit") {
            return $get ? $this->scholarshipAudit($request) : null;
        }
        return null;
    }

    private function scholarshipNotice()
    {
        $features = $this->app->features();
        $off = [];
        if (!$features->isEnabled(Features::SCH_APPLICATIONS)) {
            $off[] = '장학 신청 접수 (공고를 낼 수 없습니다)';
        }
        if (!$features->isEnabled(Features::SCH_AWARDS)) {
            $off[] = '선정 확정·통지·지급 상태 기록';
        }
        return $off ? '<div class="notice" role="status">지금 꺼져 있는 기능: ' . Html::e(implode(', ', $off))
            . '. 프로그램의 초안과 평가 기준은 미리 준비해 둘 수 있습니다.</div>' : '';
    }

    // ── 프로그램 ────────────────────────────────────────────

    private function programInputs(array $values)
    {
        $v = function ($key, $default = '') use ($values) {
            return isset($values[$key]) ? (string)$values[$key] : $default;
        };
        return Html::select('category', '장학 종류', Scholarships::categories(), $v('category'))
            . Html::input('title', '이름', $v('title'))
            . Html::input('summary', '한 줄 소개', $v('summary'))
            . Html::textarea('description', '소개', $v('description'))
            . Html::select('support_type', '지원 방식', Scholarships::supportTypes(), $v('support_type'))
            . Html::input('support_detail', '지원 내용', $v('support_detail'), '예: 금융 교재와 온라인 강의 6개월 이용권. 게임머니·아이템은 지원 내용이 될 수 없습니다')
            . Html::input('capacity', '선발 인원', $v('capacity'))
            . Html::input('apply_starts_at', '접수 시작 (한국 시간)', $v('apply_starts_at'), '예: 2026-12-01 09:00')
            . Html::input('apply_ends_at', '접수 마감 (한국 시간)', $v('apply_ends_at'))
            . Html::input('result_on', '결과 발표 예정일', $v('result_on'), '예: 2027-01-15')
            . Html::input('guardian_consent_age', '보호자 동의가 필요한 나이 (미만)', $v('guardian_consent_age', '19'), '14 ~ 19. 14 아래로 내릴 수 없습니다')
            . Html::input('min_reviews', '지원서마다 필요한 심사 수', $v('min_reviews', '2'), '1 ~ 10')
            . Html::input('appeal_days', '이의신청 기간(일)', $v('appeal_days', '7'), '3 ~ 30')
            . Html::input('retention_days_unselected', '선정되지 않은 신청의 개인정보 보관 기간(일)', $v('retention_days_unselected', '180'), '30 ~ 365')
            . Html::input('retention_days_selected', '선정된 신청의 개인정보 보관 기간(일)', $v('retention_days_selected', '1825'), '30 ~ 1825');
    }

    private function programList(Request $request)
    {
        $statuses = ScholarshipOffice::programStatuses();
        $categories = Scholarships::categories();
        $rows = [];
        foreach ($this->app->scholarshipOffice()->programs($this->admin) as $row) {
            $rows[] = [$this->link('/admin/scholarship/programs/' . $row['id'], $row['title']), $categories[$row['category']], $statuses[$row['status']],
                self::kst($row['apply_starts_at']) . ' ~ ' . self::kst($row['apply_ends_at']), $row['capacity'], $row['application_count']];
        }
        $content = self::done($request) . $this->scholarshipNotice()
            . '<p>장학 프로그램은 게임 경제와 이어져 있지 않습니다. 게임 성적·랭킹·GC·결제는 자격 조건도 평가 기준도 될 수 없습니다. '
            . '이 시스템은 돈을 보내지 않습니다: 지급은 상태만 기록합니다.</p>'
            . Html::table(['프로그램', '종류', '상태', '접수 기간 (한국 시간)', '선발 인원', '신청'], $rows, '등록된 프로그램이 없습니다.');
        if ($this->can(Auth::SCH_MANAGE)) {
            $content .= '<p>' . Html::e($this->link('/admin/scholarship/programs/new', '프로그램 만들기')) . '</p>';
        }
        return $this->page('장학: 프로그램', $content);
    }

    private function programNew(array $problems, $entered)
    {
        if (!$this->can(Auth::SCH_MANAGE)) {
            return $this->denied();
        }
        return $this->page('장학 프로그램 만들기', Html::problems($problems) . $this->back('/admin/scholarship/programs')
            . $this->form('/admin/scholarship/programs', $this->programInputs($entered !== null ? $entered : []), '초안으로 만들기'), $problems ? 422 : 200);
    }

    private static function ruleText(array $rule)
    {
        $params = $rule['params'] !== null ? (array)json_decode($rule['params'], true) : [];
        switch ($rule['rule_type']) {
            case 'school_level':
                $levels = Scholarships::schoolLevels();
                return implode(', ', array_map(function ($level) use ($levels) {
                    return isset($levels[$level]) ? $levels[$level] : $level;
                }, isset($params['levels']) ? (array)$params['levels'] : []));
            case 'birth_year': return (isset($params['min']) ? $params['min'] : '') . ' ~ ' . (isset($params['max']) ? $params['max'] : '');
            case 'document':
                $kinds = Scholarships::documentKinds();
                return isset($params['kind'], $kinds[$params['kind']]) ? $kinds[$params['kind']] : '';
            default: return '신청할 때 본인이 표시';
        }
    }

    private function programDetail($id, array $problems, $entered, Request $request)
    {
        $office = $this->app->scholarshipOffice();
        $program = $office->program($this->admin, $id);
        $manage = $this->can(Auth::SCH_MANAGE);
        $statuses = ScholarshipOffice::programStatuses();
        $path = '/admin/scholarship/programs/' . (int)$program['id'];
        $editable = in_array($program['status'], ['draft', 'pending'], true);
        $content = self::done($request) . Html::problems($problems) . $this->back('/admin/scholarship/programs') . $this->scholarshipNotice()
            . Html::table(['항목', '값'], [['상태', $statuses[$program['status']]], ['공개 번호', $program['public_id']],
                ['공고를 승인한 때', self::kst($program['approved_at'])], ['결과를 알린 때', self::kst($program['announced_at'])]])
            . '<p class="sub">' . Html::e($this->link($path . '/applications', '신청자')) . ' ' . Html::e($this->link($path . '/results', '평가 결과·선정')) . '</p>';

        $actions = [
            'draft' => ['submit' => '공고 승인 요청', 'archive' => '보관'],
            'pending' => ['publish' => '공고 승인 (공개, 접수 시작)', 'return' => '초안으로 돌려보내기', 'archive' => '보관'],
            'published' => ['close' => '접수 마감 (마감 시각이 지난 뒤)'],
            'closed' => ['archive' => '보관'], 'announced' => ['archive' => '보관'],
        ];
        if (isset($actions[$program['status']]) && ($manage || $this->can(Auth::SCH_APPROVE))) {
            $content .= '<h2>상태 바꾸기</h2><p>공고의 승인은 작성하거나 마지막으로 고친 사람이 아닌 다른 관리자가 합니다. '
                . '공고가 나간 뒤에는 내용·자격·평가 기준을 바꿀 수 없습니다.</p>'
                . $this->form($path . '/transition', Html::select('action', '할 일', $actions[$program['status']], '') . Html::reason(), '바꾸기');
        }

        $values = $entered !== null ? $entered : $program;
        if ($entered === null) {
            $values['apply_starts_at'] = Form::koreaText($program['apply_starts_at']);
            $values['apply_ends_at'] = Form::koreaText($program['apply_ends_at']);
        }
        $inputs = $this->programInputs($values);
        $content .= '<h2>프로그램 내용</h2>' . ($manage && $editable ? $this->form($path, $inputs) : '<div class="readonly">' . $inputs . '</div>');

        // 지원 자격
        $types = Scholarships::ruleTypes();
        $rows = [];
        foreach ($this->app->scholarships()->rulesOf($program['id']) as $rule) {
            $rows[] = [$types[$rule['rule_type']], self::ruleText($rule), $rule['description'],
                $manage && $editable ? $this->inline($path . '/rules/' . $rule['id'] . '/remove', '빼기', [], null) : ''];
        }
        $content .= '<h2>지원 자격</h2><p>자격의 종류는 넷뿐입니다. 게임 레벨, GC, 전투 성적, 랭킹, 플레이 시간, 결제 내역을 조건으로 삼는 종류는 없습니다.</p>'
            . Html::table(['종류', '값', '설명 (지원자에게 보입니다)', ''], $rows, '자격 조건이 없습니다 (누구나 지원할 수 있습니다).');
        if ($manage && $editable) {
            $content .= '<h3>자격 더하기</h3>' . $this->form($path . '/rules',
                Html::select('rule_type', '종류', $types, 'school_level')
                . Html::input('description', '설명', '', '예: 중학생과 고등학생이 지원할 수 있습니다')
                . Html::input('levels', '학교급일 때: 허용하는 학교급', '', implode(', ', array_keys(Scholarships::schoolLevels())) . ' 가운데에서 쉼표로')
                . Html::input('min', '태어난 해일 때: 부터', '') . Html::input('max', '태어난 해일 때: 까지', '')
                . Html::select('kind', '제출 자료일 때: 자료의 종류', Scholarships::documentKinds(), 'plan'), '자격 더하기');
        }

        // 평가 기준
        $rows = [];
        foreach ($this->app->scholarships()->criteriaOf($program['id']) as $criterion) {
            $rows[] = [$criterion['name'], $criterion['description'], $criterion['max_score'], $criterion['weight'],
                $manage && $editable ? $this->inline($path . '/criteria/' . $criterion['id'] . '/remove', '빼기', [], null) : ''];
        }
        $content .= '<h2>평가 기준</h2><p>심사위원은 기준마다 0점부터 만점까지 점수를 줍니다. 총점은 (점수 ÷ 만점)에 가중치를 곱해 100점 만점으로 계산합니다. '
            . '기준은 공고와 함께 지원자에게 보입니다.</p>'
            . Html::table(['기준', '설명', '만점', '가중치', ''], $rows, '평가 기준이 없습니다. 기준이 있어야 공고할 수 있습니다.');
        if ($manage && $editable) {
            $content .= '<h3>평가 기준 더하기</h3>' . $this->form($path . '/criteria',
                Html::input('name', '평가 기준', '', '게임 성적·게임머니·랭킹·결제는 넣을 수 없습니다') . Html::input('description', '설명', '')
                . Html::input('max_score', '만점', '10', '1 ~ 100') . Html::input('weight', '가중치', '1', '1 ~ 100'), '기준 더하기');
        }

        // 심사위원
        $rows = [];
        foreach ($office->reviewers($this->admin, $program['id']) as $reviewer) {
            $assigned = $reviewer['reviewer_id'] !== null && (int)$reviewer['is_active'] === 1;
            $button = '';
            if ($manage && !in_array($program['status'], ['announced', 'archived'], true) && (int)$reviewer['account_active'] === 1) {
                $button = $this->inline($path . '/reviewers', $assigned ? '빼기' : '배정', ['admin_id' => $reviewer['admin_id'], 'active' => $assigned ? '0' : '1'], null);
            }
            $rows[] = [$reviewer['login'], (int)$reviewer['account_active'] === 1 ? '' : '멈춘 계정', $assigned ? '배정됨' : '', $button];
        }
        $content .= '<h2>심사위원</h2><p>역할이 "심사위원"인 관리자 계정만 배정할 수 있습니다. 자격 확인을 마친 지원서는 배정된 심사위원 모두에게 넘어갑니다. '
            . '심사위원은 자기에게 배정된 지원서만, 이름·연락처·학교 이름 없이 봅니다.</p>'
            . Html::table(['계정', '', '상태', ''], $rows, '심사위원 역할의 계정이 없습니다. bin/create-admin.php 로 reviewer 역할의 계정을 만들어 주세요.');
        return $this->page('장학 프로그램 — ' . $program['title'], $content, $problems ? 422 : 200);
    }

    // ── 신청자, 자격 심사 ───────────────────────────────────

    private static function consentText($status)
    {
        $names = ['not_required' => '필요 없음 (성인)', 'pending' => '보호자 동의를 기다리는 중', 'guardian_confirmed' => '보호자가 동의함 (담당자 확인 전)',
            'verified' => '보호자 동의 확인됨'];
        return isset($names[$status]) ? $names[$status] : $status;
    }

    private function applicationList($programId, Request $request)
    {
        $office = $this->app->scholarshipOffice();
        $program = $office->program($this->admin, $programId);
        $levels = Scholarships::schoolLevels();
        $manage = $this->can(Auth::SCH_MANAGE);
        $rows = [];
        foreach ($office->applications($this->admin, $program['id']) as $row) {
            $rows[] = [$manage ? $this->link('/admin/scholarship/applications/' . $row['id'], $row['public_id']) : $row['public_id'],
                Scholarships::statusText($row['status']), isset($levels[$row['school_level']]) ? $levels[$row['school_level']] : '',
                (int)$row['is_minor'] === 1 ? '미성년' : '', self::consentText($row['consent_status']), self::kst($row['submitted_at']),
                $row['reviews_done'] . ' 끝 / ' . $row['reviews_waiting'] . ' 남음', Scholarships::resultText($row['result']),
                $row['purged_at'] !== null ? '개인정보 지움' : ''];
        }
        return $this->page('신청자 — ' . $program['title'], self::done($request) . $this->back('/admin/scholarship/programs/' . (int)$program['id'], '프로그램으로')
            . '<p>이 목록에는 개인정보가 없습니다. 접수 번호를 누르면 신청서가 열리고, 열 때마다 기록이 남습니다.</p>'
            . Html::table(['접수 번호', '상태', '학교급', '', '보호자 동의', '접수한 때 (한국 시간)', '심사', '결과', ''], $rows, '신청이 없습니다.'));
    }

    /** @param array|null $pii 방금 사유를 적고 연 개인정보. 이 응답에서만 보인다. */
    private function applicationPage($id, array $problems, $pii, Request $request)
    {
        $office = $this->app->scholarshipOffice();
        $detail = $office->applicationDetail($this->admin, $id, $request->ip);
        $application = $detail['application'];
        $program = $detail['program'];
        $levels = Scholarships::schoolLevels();
        $path = '/admin/scholarship/applications/' . (int)$application['id'];
        $content = self::done($request) . Html::problems($problems)
            . $this->back('/admin/scholarship/programs/' . (int)$program['id'] . '/applications', '신청자 목록으로')
            . Html::table(['항목', '값'], [
                ['프로그램', $program['title']], ['접수 번호', $application['public_id']], ['상태', Scholarships::statusText($application['status'])],
                ['학교급', isset($levels[$application['school_level']]) ? $levels[$application['school_level']] : ''],
                ['미성년자', (int)$application['is_minor'] === 1], ['보호자 동의', self::consentText($application['consent_status'])],
                ['자격 확인', $application['eligibility_status'] . ($application['eligibility_note'] !== null ? ' — ' . $application['eligibility_note'] : '')],
                ['접수한 때', self::kst($application['submitted_at'])], ['결과', Scholarships::resultText($application['result'])],
                ['개인정보를 지우는 날', $application['purge_after']], ['개인정보를 지운 때', self::kst($application['purged_at'])],
            ]);

        // 개인정보
        $content .= '<h2>개인정보 (이름·연락처)</h2>';
        if ($pii !== null) {
            if (!empty($pii['purged'])) {
                $content .= '<p>보관 기한이 지나 지워졌습니다.</p>';
            } else {
                $relations = Scholarships::guardianRelations();
                $content .= '<div class="notice" role="status">열람이 기록되었습니다. 이 화면을 벗어나면 다시 사유를 적어야 볼 수 있습니다.</div>'
                    . Html::table(['항목', '값'], [
                        ['이름', $pii['name']], ['태어난 해', $pii['birthYear']], ['학교', $pii['schoolName']], ['이메일', $pii['email']], ['전화번호', $pii['phone']],
                        ['보호자 이름', $pii['guardianName']],
                        ['보호자와의 관계', isset($relations[$pii['guardianRelation']]) ? $relations[$pii['guardianRelation']] : ''],
                        ['보호자 연락처', $pii['guardianContact']],
                    ]);
            }
        } elseif ($this->can(Auth::SCH_PII)) {
            $content .= '<p>연락하거나 자격을 확인해야 할 때에만 엽니다. 누가 언제 왜 열었는지 기록되고, 준법·개인정보 담당이 그 기록을 봅니다.</p>'
                . $this->form($path . '/pii', Html::input('reason', '열람 사유 (필수)', '', '예: 보호자에게 연락해 동의를 확인하려고'), '개인정보 열기');
        }

        // 동의
        $rows = [];
        foreach ($detail['consents'] as $consent) {
            $rows[] = [$consent['consent_type'] === 'privacy' ? '개인정보 수집·이용 (본인)' : '보호자 동의', $consent['status'], $consent['version'],
                self::kst($consent['confirmed_at']), self::kst($consent['verified_at']), trim($consent['verify_method'] . ' ' . $consent['verify_note'])];
        }
        $content .= '<h2>동의</h2>' . Html::table(['종류', '상태', '동의서 판', '동의한 때', '담당자가 확인한 때', '확인 방법'], $rows);
        if ($application['consent_status'] === 'guardian_confirmed') {
            $content .= '<h3>보호자 동의 확인</h3><p>보호자가 동의 화면에서 동의했습니다. 이 시스템은 그 사람이 정말 보호자인지 확인하지 못합니다. '
                . '보호자에게 직접 연락하거나 서면을 받아 확인한 뒤에 기록해 주세요. 확인 전에는 심사로 넘어가지 않습니다.</p>'
                . $this->form($path . '/consent', Html::select('verify_method', '확인한 방법', ['call' => '보호자와 통화', 'document' => '서면 동의서', 'in_person' => '대면 확인'], 'call')
                    . Html::input('verify_note', '확인 내용', ''), '확인했다고 기록');
        }

        // 신청서
        if ($detail['form'] === null) {
            $content .= '<h2>신청서</h2><p>보관 기한이 지나 지워졌습니다.</p>';
        } else {
            $content .= '<h2>신청서</h2>' . $this->applicationFormHtml($detail['form'], $detail['documents']);
        }

        // 자격 확인
        if ($application['status'] === 'submitted') {
            $content .= '<h2>자격 확인</h2><p>해당하면 심사로 넘어가고, 배정된 심사위원 모두에게 지원서가 전달됩니다. '
                . '자격은 신청서와 제출 자료로만 확인합니다 (게임 기록은 보지 않습니다).</p>'
                . $this->form($path . '/eligibility', '<input type="hidden" name="eligible" value="1">' . Html::input('note', '확인 내용', ''), '자격 확인, 심사로 넘기기')
                . $this->form($path . '/eligibility', '<input type="hidden" name="eligible" value="0">'
                    . Html::input('note', '해당하지 않는 사유', '', '신청자에게 그대로 보입니다'), '자격에 해당하지 않음');
        }

        // 심사, 결정
        $rows = [];
        $reviewStatus = ['assigned' => '심사 중', 'submitted' => '평가 냄', 'recused' => '회피'];
        foreach ($detail['reviews'] as $review) {
            $rows[] = [$review['login'], $reviewStatus[$review['status']], $review['total_centi'] !== null ? number_format($review['total_centi'] / 100, 2) : '',
                self::kst($review['submitted_at'])];
        }
        $content .= '<h2>심사</h2>' . Html::table(['심사위원', '상태', '총점 (100점 만점)', '낸 때'], $rows, '아직 배정되지 않았습니다.');
        if ($detail['award'] !== null) {
            $decisions = ScholarshipOffice::decisions();
            $content .= '<h2>결정</h2>' . Html::table(['항목', '값'], [
                ['결정', $decisions[$detail['award']['decision']]], ['상태', $detail['award']['status']],
                ['평균 점수', $detail['award']['score_centi'] !== null ? number_format($detail['award']['score_centi'] / 100, 2) : ''],
                ['사유', $detail['award']['decision_note']],
            ]);
        }
        return $this->page('신청 — ' . $application['public_id'], $content, $problems ? 422 : 200);
    }

    /** 신청서와 제출 자료. 이름·연락처는 들어 있지 않다. */
    private function applicationFormHtml(array $form, array $documents)
    {
        $out = '<h3>지원 동기</h3><p class="prose">' . nl2br(Html::e(isset($form['motivation']) ? $form['motivation'] : '')) . '</p>'
            . '<h3>계획</h3><p class="prose">' . nl2br(Html::e(isset($form['plan']) ? $form['plan'] : '')) . '</p>';
        if (!empty($form['statements'])) {
            $out .= '<h3>본인이 확인한 문구</h3><ul>';
            foreach ((array)$form['statements'] as $statement) {
                $out .= '<li>' . Html::e(is_array($statement) && isset($statement['description']) ? $statement['description'] : $statement) . '</li>';
            }
            $out .= '</ul>';
        }
        $rows = [];
        foreach ($documents as $document) {
            // 주소는 누를 수 있는 링크로 만들지 않는다 (관리자 화면에서 밖으로 나가는 링크를 두지 않는다).
            $rows[] = [$document['kindName'], $document['title'], Html::raw(nl2br(Html::e($document['content']))), $document['url']];
        }
        return $out . '<h3>제출 자료</h3>' . Html::table(['종류', '제목', '내용', '주소 (복사해서 엽니다)'], $rows, '제출한 자료가 없습니다.');
    }

    // ── 심사위원: 내 심사 ───────────────────────────────────

    private function reviewQueuePage(Request $request)
    {
        $status = ['assigned' => '심사할 것', 'submitted' => '평가 냄', 'recused' => '회피'];
        $rows = [];
        foreach ($this->app->scholarshipOffice()->reviewQueue($this->admin) as $row) {
            $rows[] = [$this->link('/admin/scholarship/reviews/' . $row['id'], $row['public_id']), $row['title'], $status[$row['status']],
                $row['total_centi'] !== null ? number_format($row['total_centi'] / 100, 2) : '', self::kst($row['assigned_at']), self::kst($row['submitted_at'])];
        }
        return $this->page('장학: 내 심사', self::done($request)
            . '<p>나에게 배정된 지원서만 보입니다. 지원서에는 이름·연락처·학교 이름이 없습니다. 아는 사람의 지원서로 보이면 평가하지 말고 회피해 주세요. '
            . '게임 기록은 심사에 쓰지 않으며, 이 화면 어디에도 나오지 않습니다.</p>'
            . Html::table(['접수 번호', '프로그램', '상태', '내가 준 총점', '배정된 때 (한국 시간)', '낸 때'], $rows, '배정된 심사가 없습니다.'));
    }

    private function reviewPage($id, array $problems, $entered, Request $request)
    {
        $view = $this->app->scholarshipOffice()->reviewView($this->admin, $id, $request->ip);
        $path = '/admin/scholarship/reviews/' . (int)$view['review']['id'];
        $content = Html::problems($problems) . $this->back('/admin/scholarship/reviews')
            . Html::table(['항목', '값'], [['프로그램', $view['program']['title'] . ' (' . $view['program']['categoryName'] . ')'],
                ['지원 내용', $view['program']['supportDetail']], ['접수 번호', $view['application']['number']], ['학교급', $view['application']['schoolLevel']]])
            . '<h2>지원서</h2>' . $this->applicationFormHtml((array)$view['form'], $view['documents']);
        if ($view['review']['open']) {
            $inner = '';
            foreach ($view['criteria'] as $criterion) {
                $name = 'score_' . $criterion['id'];
                $inner .= Html::input($name, $criterion['name'] . ' (가중치 ' . $criterion['weight'] . ')', $entered !== null ? self::field($entered, $name) : '',
                    '0 ~ ' . $criterion['max_score'] . ($criterion['description'] !== '' ? '. ' . $criterion['description'] : ''));
            }
            $inner .= Html::textarea('comment', '심사 의견', $entered !== null ? self::field($entered, 'comment') : '', '10자 이상. 점수를 준 까닭을 적어 주세요');
            $content .= '<h2>평가</h2><p>낸 평가는 고칠 수 없습니다. 평가 기준 밖의 것(게임 실력, 아는 사이 등)으로 점수를 주지 마세요.</p>'
                . $this->form($path, $inner, '평가 내기')
                . '<h2>회피</h2><p>지원자를 알거나 이해관계가 있으면 평가하지 않습니다.</p>'
                . $this->form($path . '/recuse', Html::input('reason', '회피 사유 (필수)', ''), '이 심사를 회피');
        } else {
            $rows = [];
            foreach ($view['criteria'] as $criterion) {
                $rows[] = [$criterion['name'], isset($view['review']['scores'][$criterion['id']]) ? $view['review']['scores'][$criterion['id']] : '', $criterion['max_score']];
            }
            $content .= '<h2>내 평가</h2>' . Html::table(['기준', '점수', '만점'], $rows)
                . '<p>총점: ' . Html::e($view['review']['totalCenti'] !== null ? number_format($view['review']['totalCenti'] / 100, 2) : '-') . '</p>'
                . '<p class="prose">' . nl2br(Html::e($view['review']['comment'])) . '</p>';
        }
        return $this->page('심사 — ' . $view['application']['number'], $content, $problems ? 422 : 200);
    }

    // ── 평가 결과, 선정, 최종 승인, 통지 ────────────────────

    private function resultsPage($programId, array $problems, Request $request)
    {
        $office = $this->app->scholarshipOffice();
        $program = $office->program($this->admin, $programId);
        $path = '/admin/scholarship/programs/' . (int)$program['id'];
        $decisions = ScholarshipOffice::decisions();
        $levels = Scholarships::schoolLevels();
        $awardStatus = ['proposed' => '제안됨 (최종 승인 전)', 'approved' => '최종 승인됨', 'reopened' => '이의신청으로 다시 심사'];
        $rows = [];
        $open = 0;
        $proposed = 0;
        $decided = 0;
        $inputs = '';
        foreach ($office->results($this->admin, $program['id']) as $row) {
            $choice = '';
            if ($row['status'] === 'in_review') {
                $open++;
                $proposed += $row['award_status'] === 'proposed' ? 1 : 0;
                $choice = Html::raw('<select name="decision_' . (int)$row['id'] . '" aria-label="결정"><option value="">고르기</option>'
                    . implode('', array_map(function ($value) use ($decisions, $row) {
                        return '<option value="' . Html::e($value) . '"' . ($row['decision'] === $value && $row['award_status'] === 'proposed' ? ' selected' : '') . '>'
                            . Html::e($decisions[$value]) . '</option>';
                    }, array_keys($decisions))) . '</select>');
            }
            $decided += $row['status'] === 'decided' ? 1 : 0;
            $rows[] = [$row['public_id'], isset($levels[$row['school_level']]) ? $levels[$row['school_level']] : '',
                $row['score_centi'] !== null ? number_format($row['score_centi'] / 100, 2) : '-', $row['review_count'] . ' / ' . $program['min_reviews'],
                Scholarships::statusText($row['status']), $row['decision'] !== null ? $decisions[$row['decision']] : '',
                $row['award_status'] !== null ? $awardStatus[$row['award_status']] : '', $choice];
        }
        $table = Html::table(['접수 번호', '학교급', '평균 점수 (100점 만점)', '끝난 심사 / 필요', '상태', '결정', '결정의 상태', '선정 제안'], $rows, '심사로 넘어간 지원서가 없습니다.');
        $content = self::done($request) . Html::problems($problems) . $this->back($path, '프로그램으로') . $this->scholarshipNotice()
            . '<p>점수는 심사위원이 넣은 것의 평균뿐입니다. 게임 기록·결제·GC 는 들어가지 않습니다. 정원: ' . Html::e($program['capacity']) . '명. '
            . '순서: ① 접수 마감 → ② 선정 제안 (장학 담당) → ③ 최종 승인 (제안한 사람이 아닌 다른 관리자) → ④ 결과 알림.</p>';
        if ($open > 0 && $this->can(Auth::SCH_MANAGE)) {
            $content .= '<form method="post" action="' . Html::e($this->url($path . '/results/propose')) . '">'
                . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '">' . $table
                . Html::input('note', '선정 사유 (필수)', '', '예: 심사 점수 순으로 정원만큼 선정') . '<p><button type="submit">선정 제안</button></p></form>';
        } else {
            $content .= $table;
        }
        if ($proposed > 0 && $this->can(Auth::SCH_APPROVE)) {
            $content .= '<h2>최종 승인</h2><p>제안된 ' . $proposed . '건을 확정합니다. 선정을 제안한 사람은 승인할 수 없습니다. 승인해도 결과를 알리기 전에는 신청자에게 보이지 않습니다.</p>'
                . $this->form($path . '/results/approve', Html::reason(), '최종 승인');
        }
        if ($decided > 0 && $this->can(Auth::SCH_APPROVE)) {
            $content .= '<h2>결과 알림</h2><p>확정된 ' . $decided . '건의 결과를 신청자의 진행 상태 화면에 보이게 합니다. 서버는 문자나 메일을 보내지 않습니다. '
                . '알린 날부터 이의신청 기간(' . Html::e($program['appeal_days']) . '일)과 개인정보 보관 기한이 시작됩니다.</p>'
                . $this->form($path . '/results/announce', Html::reason(), '결과 알리기');
        }
        return $this->page('평가 결과 — ' . $program['title'], $content, $problems ? 422 : 200);
    }

    // ── 이의신청 ────────────────────────────────────────────

    private function appealList(Request $request)
    {
        $status = ['submitted' => '처리 전', 'upheld' => '받아들임', 'dismissed' => '받아들이지 않음'];
        $rows = [];
        foreach ($this->app->scholarshipOffice()->appeals($this->admin) as $row) {
            $rows[] = [$this->can(Auth::SCH_MANAGE) ? $this->link('/admin/scholarship/appeals/' . $row['id'], $row['public_id']) : $row['public_id'],
                $row['title'], $status[$row['status']], self::kst($row['created_at']), self::kst($row['handled_at']), $row['response']];
        }
        return $this->page('장학: 이의신청', self::done($request)
            . '<p>자격에 해당하지 않는다는 결정이나 선정 결과에 대해 한 번 이의를 신청할 수 있습니다. 원래 결정을 내린 사람은 처리할 수 없습니다. '
            . '받아들이면 그 신청을 앞 단계로 되돌려 다시 봅니다.</p>'
            . Html::table(['접수 번호', '프로그램', '상태', '신청한 때 (한국 시간)', '처리한 때', '답변'], $rows, '이의신청이 없습니다.'));
    }

    private function appealPage($id, array $problems, Request $request)
    {
        $appeal = $this->app->scholarshipOffice()->appealReason($this->admin, $id, $request->ip);
        $content = Html::problems($problems) . $this->back('/admin/scholarship/appeals')
            . '<p>' . Html::e($this->link('/admin/scholarship/applications/' . (int)$appeal['application_id'], '신청서 보기')) . '</p>'
            . '<h2>이의신청 사유</h2><p class="prose">' . nl2br(Html::e($appeal['reason'] !== null ? $appeal['reason'] : '(보관 기한이 지나 지워졌습니다)')) . '</p>';
        if ($appeal['status'] === 'submitted' && $this->can(Auth::SCH_APPROVE)) {
            $content .= '<h2>처리</h2>' . $this->form('/admin/scholarship/appeals/' . (int)$appeal['id'],
                Html::select('outcome', '처리 결과', ['' => '고르기', 'upheld' => '받아들임 (다시 봅니다)', 'dismissed' => '받아들이지 않음'], '')
                . Html::input('response', '답변 (신청자에게 보입니다)', '', '10자 이상'), '처리');
        } elseif ($appeal['status'] !== 'submitted') {
            $content .= '<h2>답변</h2><p>' . Html::e($appeal['response']) . '</p>';
        }
        return $this->page('이의신청', $content, $problems ? 422 : 200);
    }

    // ── 지급 상태 ───────────────────────────────────────────

    private function disbursementPage(array $problems, Request $request)
    {
        $data = $this->app->scholarshipOffice()->disbursements($this->admin);
        $types = Scholarships::supportTypes();
        $statuses = ScholarshipOffice::disbursementStatuses();
        $base = '/admin/scholarship/disbursements';
        $rows = [];
        foreach ($data['records'] as $row) {
            $buttons = '';
            if ($row['status'] === 'planned') {
                $buttons = $this->inline("$base/{$row['id']}/approve", '승인', [], 'note', '승인 사유')->html
                    . ' ' . $this->inline("$base/{$row['id']}/cancel", '취소', [], 'note', '취소 사유')->html;
            } elseif ($row['status'] === 'approved') {
                $buttons = '<form method="post" class="inline" action="' . Html::e($this->url("$base/{$row['id']}/complete")) . '">'
                    . '<input type="hidden" name="_csrf" value="' . Html::e($this->csrf) . '">'
                    . '<input type="text" name="external_reference" placeholder="지급 처리 문서 번호" aria-label="지급 처리 문서 번호"> '
                    . '<input type="text" name="note" placeholder="처리 내용" aria-label="처리 내용"> <button type="submit">완료로 기록</button></form> '
                    . $this->inline("$base/{$row['id']}/cancel", '취소', [], 'note', '취소 사유')->html;
            }
            $rows[] = [$row['public_id'], $row['title'], $types[$row['support_type']], $row['description'],
                $row['amount_krw'] !== null ? number_format((int)$row['amount_krw']) . '원' : '', $statuses[$row['status']], $row['external_reference'],
                $row['status_note'], Html::raw($buttons)];
        }
        $content = self::done($request) . Html::problems($problems) . $this->scholarshipNotice()
            . '<div class="notice" role="status"><strong>이 화면은 돈을 보내지 않습니다.</strong> 실제 지급은 이 시스템 밖의 승인된 운영 절차로 하고, 여기에는 그 상태만 적습니다. '
            . '계좌번호를 받거나 적는 곳이 없습니다. 게임머니·아이템으로 지급하는 길도 없습니다.</div>'
            . '<p>순서: 예정 → 승인 (기록을 만든 사람이 아닌 다른 관리자) → 완료 (실제 지급을 처리한 절차의 문서 번호를 적습니다). 대상자는 접수 번호로만 보입니다.</p>'
            . '<h2>지급 기록</h2>'
            . Html::table(['접수 번호', '프로그램', '지원 방식', '지원 내용', '금액', '상태', '지급 처리 문서 번호', '메모', ''], $rows, '지급 기록이 없습니다.');
        $awardees = ['' => '고르기'];
        foreach ($data['awardees'] as $row) {
            $awardees[$row['id']] = $row['public_id'] . ' — ' . $row['title'] . ' (' . $types[$row['support_type']] . ')';
        }
        $content .= '<h2>지급 기록 만들기 (예정)</h2>';
        $content .= count($awardees) > 1 ? $this->form($base, Html::select('application_id', '선정자 (접수 번호)', $awardees, '')
            . Html::select('support_type', '지원 방식', $types, 'books') . Html::input('description', '지원 내용', '')
            . Html::input('amount_krw', '금액(원)', '', '금액이 없는 지원이면 비워 둡니다'), '예정으로 기록') : '<p>결과가 발표된 선정자가 없습니다.</p>';
        return $this->page('장학: 지급 상태', $content, $problems ? 422 : 200);
    }

    // ── 개인정보 접근 로그 ──────────────────────────────────

    private function scholarshipAudit(Request $request)
    {
        $action = self::field($request->query, 'action');
        $action = preg_match('/^[a-z_.]{1,64}$/', $action) ? $action : '';
        $page = self::pageNo($request);
        $list = $this->app->scholarshipOffice()->auditLog($this->admin, $action !== '' ? $action : null, $page);
        $actors = ['admin' => '관리자', 'player' => '신청자', 'guardian' => '보호자', 'system' => '시스템'];
        $rows = [];
        foreach ($list as $row) {
            $rows[] = [self::kst($row['created_at']), isset($actors[$row['actor_type']]) ? $actors[$row['actor_type']] : $row['actor_type'],
                $row['actor_type'] === 'admin' ? $row['login'] : '', $row['action'], $row['program_id'], $row['application_id'], $row['detail'], $row['ip']];
        }
        $filter = '<form method="get" action="' . Html::e($this->url('/admin/scholarship/audit')) . '" class="filter">'
            . Html::select('action', '종류', ['' => '전체', 'pii.view' => '개인정보 열람 (pii.view)', 'application.view' => '신청서 열람', 'review.view' => '심사위원의 지원서 열람',
                'appeal.view' => '이의신청 사유 열람', 'consent.verify' => '보호자 동의 확인', 'award.approve' => '선정 최종 승인', 'result.announce' => '결과 알림',
                'disbursement.complete' => '지급 완료 기록', 'application.purge' => '개인정보 삭제'], $action)
            . '<p><button type="submit">보기</button></p></form>';
        return $this->page('장학: 개인정보 접근 로그', '<p>누가 언제 어떤 신청의 개인정보·신청서를 열었는지, 왜 열었는지가 남습니다. 관리자 화면에는 이 기록을 지우거나 고치는 기능이 없습니다. '
            . '신청자의 이름은 기록에 없습니다 (신청 번호로만 남습니다).</p>' . $filter
            . Html::table(['때 (한국 시간)', '주체', '관리자', '한 일', '프로그램', '신청', '내용', '주소'], $rows, '기록이 없습니다.')
            . $this->pager('/admin/scholarship/audit', array_filter(['action' => $action], 'strlen'), $page, count($list)));
    }
}
