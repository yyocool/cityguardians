<?php
namespace CG\Web;

use CG\Admin\Html;
use CG\Infra\ApiError;
use CG\Infra\Request;
use CG\Service\Features;
use CG\Service\Scholarships;

/**
 * 홈페이지: GUARDIANS SCHOLARSHIP (docs/SCHOLARSHIP_SYSTEM.md 5장).
 *
 * 장학 신청은 게임 안이 아니라 이 화면에서 한다. 공고는 운영자가 승인한 것만 나오고, 없으면 없다고 말한다.
 * 게임 성적·랭킹·게임머니·결제 내역을 묻는 칸은 어디에도 없다.
 */
trait SiteScholarshipPages
{
    private function routeScholarship($get, $path, Request $request)
    {
        $id = '([a-z0-9_]{16})';
        if ($get) {
            switch ($path) {
                case '/scholarship': return $this->schIntro();
                case '/scholarship/programs': return $this->schPrograms();
                case '/scholarship/how-to-apply': return $this->schHowTo();
                case '/scholarship/criteria': return $this->schCriteria();
                case '/scholarship/faq': return $this->schFaq();
                case '/scholarship/status': return $this->player === null ? $this->needLogin($path) : $this->schStatus();
            }
            if (preg_match("#^/scholarship/programs/$id$#", $path, $m)) {
                return $this->schProgram($m[1]);
            }
            if (preg_match("#^/scholarship/apply/$id$#", $path, $m)) {
                return $this->player === null ? $this->needLogin($path) : $this->schApplyForm($m[1], [], []);
            }
            if (preg_match("#^/scholarship/status/$id$#", $path, $m)) {
                return $this->player === null ? $this->needLogin('/scholarship/status') : $this->schApplication($m[1], [], '', $request);
            }
            return null;
        }
        // 여기부터는 로그인과 CSRF 확인을 마친 POST 다.
        $sch = $this->app->scholarships();
        if (preg_match("#^/scholarship/apply/$id$#", $path, $m)) {
            return $this->schApply($m[1], $request);
        }
        if (preg_match("#^/scholarship/status/$id/(withdraw|appeal|guardian-link)$#", $path, $m)) {
            if ($m[2] === 'withdraw') {
                if (self::field($request->form, 'confirm') !== '1') {
                    return $this->schApplication($m[1], ['철회하려면 확인에 표시해 주세요.'], '', $request);
                }
                $sch->withdraw($this->player, $m[1], $request->ip);
                return $this->redirect('/scholarship/status/' . $m[1] . '?done=withdrawn');
            }
            if ($m[2] === 'appeal') {
                try {
                    $sch->appeal($this->player, $m[1], self::field($request->form, 'reason'), $request->ip);
                } catch (ApiError $error) {
                    if ($error->errorCode !== ApiError::VALIDATION_FAILED) {
                        throw $error;
                    }
                    return $this->schApplication($m[1], [$error->getMessage()], '', $request);
                }
                return $this->redirect('/scholarship/status/' . $m[1] . '?done=appealed');
            }
            $link = $sch->reissueGuardianLink($this->player, $m[1], $request->ip);
            return $this->schApplication($m[1], [], $this->guardianLinkHtml($link['guardianConsent']), $request);
        }
        return null;
    }

    private function schEnabled()
    {
        return $this->app->features()->isEnabled(Features::SCH_APPLICATIONS);
    }

    private static function phaseText($phase)
    {
        $names = ['upcoming' => '모집 예정', 'open' => '모집 중', 'closed' => '모집 마감', 'announced' => '결과 발표'];
        return isset($names[$phase]) ? $names[$phase] : $phase;
    }

    /** 심사에 쓰지 않는 것. 여러 화면에 같은 글로 보인다. */
    private static function notUsed()
    {
        return '<div class="notice" role="note"><strong>심사에 쓰지 않는 것</strong><p>게임 플레이 기록, 전투 성적, 도시 랭킹, 레벨, 아이템, '
            . '게임머니(GC), 가상 주식·대출·신용점수, 유료 결제 내역은 지원 자격 확인과 심사에 쓰지 않습니다. 게임을 하지 않아도 불리하지 않습니다.</p></div>';
    }

    private static function privacyHtml(array $privacy)
    {
        $rows = [];
        foreach ($privacy['items'] as $item) {
            $rows[] = [$item['what'], $item['why'], $item['who']];
        }
        return '<div class="scroll">' . Html::table(['받는 정보', '쓰는 목적', '보는 사람'], $rows) . '</div>'
            . '<p>' . Html::e($privacy['notCollected']) . '</p><p>' . Html::e($privacy['retention']) . '</p>'
            . '<p><small>' . Html::e($privacy['draft']) . ' (판 ' . Html::e($privacy['version']) . ')</small></p>';
    }

    // ── 장학사업 소개 ───────────────────────────────────────

    private function schIntro()
    {
        $content = '<p class="lead">GUARDIANS SCHOLARSHIP 은 금융교육, 창의력, 협력, 사회공헌 역량을 가진 학생을 지원하는 장학 프로그램입니다.</p>';
        if (!$this->schEnabled()) {
            $content .= self::soon('장학 프로그램은 준비 중입니다. 지금은 모집 중인 프로그램이 없고, 신청을 받지 않습니다. 모집이 시작되면 모집 공고에 올라옵니다.');
        }
        $content .= '<h2>장학의 종류</h2><dl>'
            . '<dt>금융 역량 장학</dt><dd>금융교육 과정 이수, 금융 이해도 평가, 금융 프로젝트 참여</dd>'
            . '<dt>창의력 장학</dt><dd>도시 문제 해결 아이디어, 게임·콘텐츠 기획, 창작 프로젝트</dd>'
            . '<dt>협력·리더십 장학</dt><dd>건전한 커뮤니티 활동, 협동 프로젝트 참여, 지역 사회 기여 활동</dd>'
            . '<dt>성장 지원 장학</dt><dd>학습 의지, 프로젝트 수행 과정, 교육 프로그램 참여</dd></dl>'
            . self::notUsed()
            . '<h2>지원하는 방식</h2><p>프로그램마다 다릅니다: 교육비 지원, 도서·교재 지원, 온라인 교육 이용권, 학습 프로젝트 지원, 별도 장학금. '
            . '어떤 방식인지는 각 모집 공고에 적혀 있습니다.</p>'
            . '<p>장학 지원은 게임 경제와 완전히 따로 운영합니다. 게임머니로 받거나 게임머니로 바꿀 수 없고, 게임 안에서 지급되지 않습니다.</p>'
            . '<h2>더 보기</h2><ul><li>' . $this->a('/scholarship/programs', '모집 공고') . '</li><li>' . $this->a('/scholarship/how-to-apply', '지원 방법') . '</li>'
            . '<li>' . $this->a('/scholarship/criteria', '심사 기준') . '</li><li>' . $this->a('/scholarship/faq', '자주 묻는 질문') . '</li></ul>';
        return $this->page('GUARDIANS SCHOLARSHIP', $content, 200, '시티가디언즈 장학 프로그램 GUARDIANS SCHOLARSHIP 소개');
    }

    // ── 모집 공고 ───────────────────────────────────────────

    private function programCard(array $program)
    {
        return '<article class="card"><h2>' . $this->a('/scholarship/programs/' . $program['id'], $program['title'])
            . ' <span class="tag strong">' . Html::e(self::phaseText($program['phase'])) . '</span> <span class="tag">' . Html::e($program['categoryName']) . '</span></h2>'
            . '<p>' . Html::e($program['summary']) . '</p><dl>'
            . '<dt>지원 방식</dt><dd>' . Html::e($program['supportTypeName']) . '</dd>'
            . '<dt>모집 인원</dt><dd>' . (int)$program['capacity'] . '명</dd>'
            . '<dt>신청 기간</dt><dd>' . Html::e(self::kst($program['applyStartsAt'])) . ' ~ ' . Html::e(self::kst($program['applyEndsAt'])) . ' (한국 시간)</dd>'
            . ($program['resultOn'] !== null ? '<dt>결과 발표 예정</dt><dd>' . Html::e($program['resultOn']) . '</dd>' : '') . '</dl></article>';
    }

    private function schPrograms()
    {
        $data = $this->app->scholarships()->programs();
        if (!$data['applicationsEnabled']) {
            return $this->page('모집 공고', self::soon('장학 프로그램은 준비 중입니다. 지금은 모집 공고가 없습니다.'));
        }
        $content = '';
        foreach ($data['programs'] as $program) {
            $content .= $this->programCard($program);
        }
        if (!$data['programs']) {
            $content = '<p class="empty">지금은 모집 공고가 없습니다. 모집이 시작되면 이 페이지에 올라옵니다.</p>';
        }
        return $this->page('모집 공고', $content);
    }

    private function schProgram($id)
    {
        $program = $this->app->scholarships()->program($id);
        $content = '<p><span class="tag strong">' . Html::e(self::phaseText($program['phase'])) . '</span> <span class="tag">' . Html::e($program['categoryName']) . '</span></p>'
            . '<p class="lead">' . Html::e($program['summary']) . '</p>' . ($program['description'] !== '' ? self::prose($program['description']) : '')
            . '<h2>지원 내용</h2><dl><dt>지원 방식</dt><dd>' . Html::e($program['supportTypeName']) . '</dd>'
            . '<dt>내용</dt><dd>' . nl2br(Html::e($program['supportDetail'])) . '</dd>'
            . '<dt>모집 인원</dt><dd>' . (int)$program['capacity'] . '명</dd></dl>'
            . '<h2>지원 일정</h2><dl><dt>신청 기간</dt><dd>' . Html::e(self::kst($program['applyStartsAt'])) . ' ~ ' . Html::e(self::kst($program['applyEndsAt'])) . ' (한국 시간)</dd>'
            . ($program['resultOn'] !== null ? '<dt>결과 발표 예정</dt><dd>' . Html::e($program['resultOn']) . '</dd>' : '')
            . '<dt>이의신청</dt><dd>결과를 안내받은 날부터 ' . (int)$program['appealDays'] . '일 안에 한 번</dd></dl>'
            . '<h2>지원 자격</h2>';
        if ($program['eligibility']) {
            $content .= '<ul>';
            foreach ($program['eligibility'] as $rule) {
                $content .= '<li>' . Html::e($rule['description']) . '</li>';
            }
            $content .= '</ul>';
        } else {
            $content .= '<p>따로 정한 자격 조건이 없습니다.</p>';
        }
        $content .= '<p>만 ' . (int)$program['guardianConsentAge'] . '세 미만이면 보호자(법정대리인)의 동의가 필요합니다.</p><h2>선발 기준</h2>';
        $rows = [];
        foreach ($program['criteria'] as $criterion) {
            $rows[] = [$criterion['name'], $criterion['description'], $criterion['maxScore'] . '점', '×' . $criterion['weight']];
        }
        $content .= '<div class="scroll">' . Html::table(['평가 항목', '보는 것', '만점', '가중치'], $rows) . '</div>' . self::notUsed()
            . '<h2>개인정보</h2>' . self::privacyHtml($program['privacy']) . '<h2>신청</h2>';
        if ($program['phase'] === 'open') {
            $content .= '<p>' . $this->a('/scholarship/apply/' . $program['id'], '신청서 쓰기', 'button') . '</p>'
                . ($this->player === null ? '<p><small>신청하려면 게임에서 받은 코드로 로그인해야 합니다.</small></p>' : '');
        } else {
            $content .= '<p class="muted">' . ($program['phase'] === 'upcoming' ? '아직 신청 기간이 아닙니다.' : '신청 기간이 끝났습니다.') . '</p>';
        }
        return $this->page($program['title'], $content);
    }

    // ── 지원 방법 · 심사 기준 · FAQ ─────────────────────────

    private function schHowTo()
    {
        $content = ($this->schEnabled() ? '' : self::soon('장학 프로그램은 준비 중입니다. 아래는 모집이 시작되면 적용할 절차입니다.'))
            . '<h2>지원하는 순서</h2><ol>'
            . '<li>' . $this->a('/scholarship/programs', '모집 공고') . '에서 프로그램의 지원 자격, 일정, 선발 기준을 확인합니다.</li>'
            . '<li>게임의 휴대전화(P 키)에서 <strong>홈페이지 로그인 코드</strong>를 받아 ' . $this->a('/login?next=/scholarship/programs', '로그인') . '합니다.</li>'
            . '<li>공고의 <strong>신청서 쓰기</strong>에서 신청서를 내고, 개인정보 수집·이용에 동의합니다.</li>'
            . '<li>미성년자는 보호자 동의가 필요합니다. 신청을 내면 보호자에게 전할 주소가 나옵니다. 보호자가 그 주소를 열어 동의하고, 그 뒤 장학 담당자가 보호자에게 연락해 한 번 더 확인합니다.</li>'
            . '<li>담당자가 지원 자격을 확인하고, 심사위원이 심사합니다.</li>'
            . '<li>결과는 ' . $this->a('/scholarship/status', '지원 현황') . '에서 확인합니다.</li></ol>'
            . '<h2>신청서에 쓰는 것</h2><ul><li>이름, 태어난 해, 학교급, 학교 이름(선택), 이메일, 전화번호(선택)</li><li>지원 동기, 활동·학습 계획</li>'
            . '<li>제출 자료 (글이나 https 주소, 5개까지)</li><li>미성년자는 보호자의 이름, 관계, 연락처</li></ul>'
            . '<p><strong>받지 않는 것:</strong> 주민등록번호, 계좌번호, 주소. 이런 정보를 신청서나 제출 자료에 적지 마세요.</p>'
            . '<h2>게임 안에서는</h2><p>게임 안의 장학센터에서는 프로그램 소개와 모집 일정, 내 심사 진행 상태를 볼 수 있습니다. '
            . '신청과 결과 확인, 이의신청은 이 홈페이지에서 합니다. 게임 안에서 장학금이 지급되는 일은 없습니다.</p>'
            . '<h2>신청을 거두고 싶을 때</h2><p>심사가 끝나기 전에는 지원 현황에서 신청을 철회할 수 있습니다. 철회하면 30일 뒤에 신청서의 개인정보를 지웁니다. '
            . '철회한 프로그램에는 다시 신청할 수 없습니다.</p>';
        return $this->page('지원 방법', $content);
    }

    private function schCriteria()
    {
        $content = '<h2>심사의 원칙</h2><ul>'
            . '<li>심사 기준은 모집 공고에 미리 공개하고, 모집이 시작된 뒤에는 바꾸지 않습니다.</li>'
            . '<li>심사위원은 배정된 신청서만 봅니다. 신청자의 이름, 연락처, 학교 이름, 게임 계정은 심사위원에게 보이지 않습니다.</li>'
            . '<li>심사위원은 다른 심사위원의 점수를 볼 수 없습니다. 이해관계가 있는 신청은 심사를 맡지 않습니다(회피).</li>'
            . '<li>결과는 한 담당자가 제안하고 다른 담당자가 승인해야 확정됩니다.</li>'
            . '<li>결과에 이의가 있으면 정해진 기간 안에 한 번 이의신청을 할 수 있고, 처음 결정한 사람이 아닌 담당자가 다시 봅니다.</li>'
            . '<li>심사 과정은 기록으로 남습니다.</li></ul>' . self::notUsed();
        $data = $this->app->scholarships()->programs();
        $content .= '<h2>프로그램별 평가 항목</h2>';
        if (!$data['applicationsEnabled']) {
            $content .= self::soon('장학 프로그램은 준비 중입니다. 프로그램이 공고되면 평가 항목이 여기에 나옵니다.');
        } elseif (!$data['programs']) {
            $content .= '<p class="empty">지금은 공고된 프로그램이 없습니다.</p>';
        }
        foreach ($data['programs'] as $summary) {
            $program = $this->app->scholarships()->program($summary['id']);
            $rows = [];
            foreach ($program['criteria'] as $criterion) {
                $rows[] = [$criterion['name'], $criterion['description'], $criterion['maxScore'] . '점', '×' . $criterion['weight']];
            }
            $content .= '<h3>' . $this->a('/scholarship/programs/' . $program['id'], $program['title']) . '</h3><div class="scroll">'
                . Html::table(['평가 항목', '보는 것', '만점', '가중치'], $rows) . '</div>';
        }
        return $this->page('심사 기준', $content);
    }

    private function schFaq()
    {
        $faq = [
            ['게임을 잘하면 장학금을 받나요?', '아니요. 전투 성적, 도시 랭킹, 레벨, 게임머니, 아이템은 심사에 쓰지 않습니다. 게임을 하지 않아도 불리하지 않습니다.'],
            ['유료 아이템을 사면 유리한가요?', '아니요. 결제 내역은 심사에 쓰지 않고, 심사위원에게 보이지도 않습니다. 결제에 따른 가산점은 없습니다.'],
            ['게임머니(GC)로 장학금을 받거나, 장학금을 게임머니로 받을 수 있나요?', '아니요. 장학 지원은 게임 경제와 완전히 따로 운영합니다. 게임 안에서는 안내만 합니다.'],
            ['왜 게임에서 로그인 코드를 받아야 하나요?', '게임 계정에는 아이디와 비밀번호가 없습니다. 본인의 계정으로 신청하고 결과를 확인할 수 있도록, 게임 안에서 일회용 코드를 받아 로그인합니다.'],
            ['미성년자도 신청할 수 있나요?', '프로그램의 지원 자격에 맞으면 신청할 수 있습니다. 보호자(법정대리인)의 동의가 필요하고, 담당자가 보호자에게 연락해 확인합니다. 동의가 확인되기 전에는 심사로 넘어가지 않습니다.'],
            ['어떤 개인정보를 받나요?', '이름, 태어난 해, 학교급, 연락처처럼 자격 확인과 연락에 필요한 것만 받습니다. 주민등록번호, 계좌번호, 주소는 받지 않습니다. 받은 정보는 암호화해 보관하고, 정해진 기간이 지나면 지웁니다.'],
            ['심사위원이 제 이름을 보나요?', '아니요. 심사위원에게는 이름, 연락처, 학교 이름, 게임 계정을 가린 신청서만 보입니다.'],
            ['결과는 어떻게 알 수 있나요?', '지원 현황에서 확인합니다. 지금은 이메일이나 문자로 따로 알리지 않으니, 공고에 적힌 발표 예정일 뒤에 확인해 주세요.'],
            ['결과에 이의가 있으면요?', '결과를 안내받은 날부터 공고에 적힌 기간 안에, 지원 현황에서 한 번 이의신청을 할 수 있습니다.'],
            ['선정되면 지원은 어떻게 받나요?', '담당자가 따로 연락해 안내합니다. 실제 지급은 별도로 승인된 운영 절차로 진행하고, 이 홈페이지와 게임은 진행 상태만 보여 줍니다. 홈페이지에서 계좌번호를 묻지 않습니다.'],
            ['신청을 취소할 수 있나요?', '심사가 끝나기 전에는 지원 현황에서 철회할 수 있습니다. 철회하면 30일 뒤에 개인정보를 지웁니다.'],
        ];
        $content = $this->schEnabled() ? '' : self::soon('장학 프로그램은 준비 중입니다. 아래는 모집이 시작되면 적용할 내용입니다.');
        foreach ($faq as $item) {
            $content .= '<details><summary>' . Html::e($item[0]) . '</summary><p>' . Html::e($item[1]) . '</p></details>';
        }
        return $this->page('자주 묻는 질문', $content);
    }

    // ── 신청 ────────────────────────────────────────────────

    /** @param array $v 다시 보여 줄 값 (받지 못했을 때 적은 것을 잃지 않게) */
    private function schApplyForm($id, array $v, array $problems, $status = 200)
    {
        $program = $this->app->scholarships()->program($id);
        if ($program['phase'] !== 'open') {
            return $this->page($program['title'], '<p>지금은 신청 기간이 아닙니다.</p><p>' . $this->a('/scholarship/programs/' . $id, '공고로 돌아가기') . '</p>', 200, '', true);
        }
        $val = function ($key) use ($v) {
            return isset($v[$key]) ? $v[$key] : '';
        };
        $text = function ($name, $label, $help, $rows = 8) use ($val) {
            return '<p class="field"><label>' . Html::e($label) . '<br><textarea name="' . Html::e($name) . '" rows="' . (int)$rows . '">' . Html::e($val($name))
                . '</textarea></label>' . ($help !== '' ? '<br><small>' . Html::e($help) . '</small>' : '') . '</p>';
        };
        $inner = '<fieldset><legend>신청자</legend>'
            . Html::input('name', '이름', $val('name'))
            . Html::input('birth_year', '태어난 해', $val('birth_year'), '예: 2010. 생일은 받지 않습니다')
            . Html::select('school_level', '학교급', ['' => '고르기'] + Scholarships::schoolLevels(), $val('school_level'))
            . Html::input('school_name', '학교 이름 (선택)', $val('school_name'))
            . Html::input('email', '이메일', $val('email'), '결과 안내와 연락에만 씁니다', 'email')
            . Html::input('phone', '전화번호 (선택)', $val('phone'), '', 'tel') . '</fieldset>'
            . '<fieldset><legend>신청서</legend>' . $text('motivation', '지원 동기', '50자 이상 2000자 이하')
            . $text('plan', '활동·학습 계획', '50자 이상 2000자 이하') . '</fieldset>';

        $statements = '';
        foreach ($program['eligibility'] as $rule) {
            if ($rule['type'] === 'statement') {
                $statements .= Html::checkbox('statement_' . $rule['id'], $rule['description'], $val('statement_' . $rule['id']) === '1');
            }
        }
        if ($statements !== '') {
            $inner .= '<fieldset><legend>확인</legend><p>아래 내용이 맞으면 표시해 주세요.</p>' . $statements . '</fieldset>';
        }

        $inner .= '<fieldset><legend>제출 자료 (5개까지)</legend><p><small>파일은 올릴 수 없습니다. 글을 직접 적거나, https:// 로 시작하는 주소를 적어 주세요. '
            . '주민등록번호, 계좌번호, 주소가 들어간 자료는 내지 마세요.</small></p>';
        for ($n = 1; $n <= 5; $n++) {
            $inner .= '<details' . ($n === 1 || $val("doc{$n}_title") !== '' ? ' open' : '') . '><summary>자료 ' . $n . '</summary>'
                . Html::select("doc{$n}_kind", '종류', ['' => '고르기'] + Scholarships::documentKinds(), $val("doc{$n}_kind"))
                . Html::input("doc{$n}_title", '제목', $val("doc{$n}_title"))
                . $text("doc{$n}_content", '내용', '5000자까지', 5)
                . Html::input("doc{$n}_url", '주소', $val("doc{$n}_url"), 'https:// 로 시작', 'url') . '</details>';
        }
        $inner .= '</fieldset><fieldset><legend>보호자 (만 ' . (int)$program['guardianConsentAge'] . '세 미만이면 꼭 적어 주세요)</legend>'
            . '<p><small>보호자 동의를 확인하는 데에만 씁니다. 만 ' . (int)$program['guardianConsentAge'] . '세 이상이면 비워 두세요.</small></p>'
            . Html::input('guardian_name', '보호자 이름', $val('guardian_name'))
            . Html::select('guardian_relation', '관계', ['' => '고르기'] + Scholarships::guardianRelations(), $val('guardian_relation'))
            . Html::input('guardian_contact', '보호자 연락처', $val('guardian_contact'), '전화번호나 이메일') . '</fieldset>'
            . '<fieldset><legend>개인정보 수집·이용 동의</legend>' . self::privacyHtml($program['privacy'])
            . Html::checkbox('privacy', '위 내용을 읽었고, 개인정보 수집·이용에 동의합니다', false, '동의하지 않으면 신청할 수 없습니다') . '</fieldset>';

        return $this->page('신청서: ' . $program['title'], self::problems($problems, '신청을 받지 못했습니다.') . self::notUsed()
            . $this->form('/scholarship/apply/' . $id, $inner, '신청서 내기'), $status, '', true);
    }

    private function schApply($id, Request $request)
    {
        $f = $request->form;
        $statements = [];
        $documents = [];
        foreach ($f as $key => $value) {
            if (preg_match('/^statement_([0-9]{1,12})$/', $key, $m) && $value === '1') {
                $statements[] = (int)$m[1];
            }
        }
        for ($n = 1; $n <= 5; $n++) {
            $document = ['kind' => self::field($f, "doc{$n}_kind"), 'title' => self::field($f, "doc{$n}_title"),
                'content' => self::field($f, "doc{$n}_content"), 'url' => self::field($f, "doc{$n}_url")];
            if (trim($document['title'] . $document['content'] . $document['url']) !== '') {
                $documents[] = $document;
            }
        }
        $input = [
            'applicant' => ['name' => self::field($f, 'name'), 'birthYear' => self::field($f, 'birth_year'), 'schoolLevel' => self::field($f, 'school_level'),
                'schoolName' => self::field($f, 'school_name'), 'email' => self::field($f, 'email'), 'phone' => self::field($f, 'phone')],
            'form' => ['motivation' => self::field($f, 'motivation'), 'plan' => self::field($f, 'plan'), 'statements' => $statements],
            'documents' => $documents,
            'consents' => ['privacy' => self::field($f, 'privacy') === '1'],
            'guardian' => ['name' => self::field($f, 'guardian_name'), 'relation' => self::field($f, 'guardian_relation'),
                'contact' => self::field($f, 'guardian_contact')],
        ];
        try {
            $result = $this->app->scholarships()->apply($this->player, $id, $input, $request->ip);
        } catch (ApiError $error) {
            if ($error->errorCode !== ApiError::VALIDATION_FAILED || $error->reason !== 'invalid_application') {
                throw $error;
            }
            return $this->schApplyForm($id, $f, $error->problems ? $error->problems : [$error->getMessage()], 422);
        }
        $application = $result['application'];
        $content = '<div class="notice" role="status"><strong>신청서를 받았습니다.</strong> 접수 번호: ' . Html::e($application['id']) . '</div>';
        if ($result['guardianConsent'] !== null) {
            $content .= $this->guardianLinkHtml($result['guardianConsent']);
        }
        $content .= '<p>진행 상태는 ' . $this->a('/scholarship/status/' . $application['id'], '지원 현황') . '에서 볼 수 있습니다.</p>';
        return $this->page('신청 완료', $content, 200, '', true);
    }

    /** 보호자에게 전할 주소. 이 응답에서만 보인다. */
    private function guardianLinkHtml(array $consent)
    {
        $url = $consent['url'] !== null ? $consent['url'] : $this->url('/scholarship/consent/' . $consent['token']);
        return '<section class="present" role="status"><h2>보호자 동의가 필요합니다</h2>'
            . '<p>아래 주소를 보호자에게 전해 주세요. 보호자가 이 주소를 열어 동의해야 심사로 넘어갑니다. 그 뒤 장학 담당자가 보호자에게 연락해 한 번 더 확인합니다.</p>'
            . '<p class="link">' . Html::e($url) . '</p>'
            . '<p><small><strong>이 주소는 지금 한 번만 보입니다.</strong> 적어 두거나 복사해 두세요. 잃어버리면 지원 현황에서 새 주소를 만들 수 있고, 그러면 앞의 주소는 쓸 수 없습니다. '
            . '이 서비스는 보호자에게 문자나 메일을 보내지 않습니다.</small></p></section>';
    }

    // ── 지원 현황 ───────────────────────────────────────────

    private function schStatus()
    {
        $list = $this->app->scholarships()->myApplications($this->player)['applications'];
        $content = '';
        foreach ($list as $application) {
            $content .= '<article class="card"><h2>' . $this->a('/scholarship/status/' . $application['id'], $application['program']['title'])
                . ' <span class="tag strong">' . Html::e($application['statusText']) . '</span></h2>'
                . '<p>' . Html::e($application['program']['categoryName']) . ' · 신청한 때 ' . Html::e(self::kst($application['submittedAt'])) . ' (한국 시간)</p>'
                . ($application['isMinor'] ? '<p>보호자 동의: ' . Html::e($application['consentText']) . '</p>' : '')
                . ($application['status'] === 'announced' ? '<p><strong>결과가 발표되었습니다.</strong> 눌러서 확인해 주세요.</p>' : '') . '</article>';
        }
        if (!$list) {
            $content = '<p class="empty">낸 신청이 없습니다.</p>'
                . ($this->schEnabled() ? '<p>' . $this->a('/scholarship/programs', '모집 공고 보기') . '</p>' : self::soon('장학 프로그램은 준비 중입니다.'));
        }
        return $this->page('지원 현황', $content);
    }

    private function schApplication($id, array $problems, $extra, Request $request)
    {
        // 결과가 발표된 신청을 처음 열면 "결과를 확인함"이 기록된다.
        $a = $this->app->scholarships()->application($this->player, $id, $request->ip)['application'];
        $done = ['withdrawn' => '신청을 철회했습니다.', 'appealed' => '이의신청을 받았습니다.'];
        $key = self::field($request->query, 'done');
        $steps = ['submitted' => 1, 'in_review' => 3, 'decided' => 4, 'announced' => 5];
        $content = Html::notice(isset($done[$key]) ? $done[$key] : '') . self::problems($problems) . $extra
            . '<dl><dt>프로그램</dt><dd>' . $this->a('/scholarship/programs/' . $a['program']['id'], $a['program']['title']) . '</dd>'
            . '<dt>접수 번호</dt><dd>' . Html::e($a['id']) . '</dd>'
            . '<dt>신청한 때</dt><dd>' . Html::e(self::kst($a['submittedAt'])) . ' (한국 시간)</dd>'
            . '<dt>진행 상태</dt><dd><strong>' . Html::e($a['statusText']) . '</strong></dd>'
            . ($a['isMinor'] ? '<dt>보호자 동의</dt><dd>' . Html::e($a['consentText']) . '</dd>' : '')
            . ($a['eligibilityNote'] !== null ? '<dt>자격 확인 결과</dt><dd>' . Html::e($a['eligibilityNote']) . '</dd>' : '') . '</dl>';
        if (isset($steps[$a['status']])) {
            $names = [1 => '접수', 2 => '자격 확인', 3 => '심사', 4 => '최종 승인', 5 => '결과 발표'];
            $at = $steps[$a['status']] === 1 && $a['eligibilityStatus'] === 'pending' ? 2 : $steps[$a['status']];
            $content .= '<ol class="steps">';
            foreach ($names as $n => $name) {
                $content .= '<li' . ($n < $at ? ' class="done"' : ($n === $at ? ' class="now" aria-current="step"' : '')) . '>' . Html::e($name) . '</li>';
            }
            $content .= '</ol>';
        }
        if ($a['result'] !== null) {
            $pay = ['planned' => '지원 준비 중', 'approved' => '지원 승인됨', 'completed' => '지원 완료', 'cancelled' => '지원 취소'];
            $content .= '<section class="present"><h2>결과: ' . Html::e($a['resultText']) . '</h2><p>발표한 때 ' . Html::e(self::kst($a['resultNotifiedAt'])) . ' (한국 시간)</p>'
                . ($a['result'] === 'selected' ? '<p>지원 진행 상태: ' . Html::e($a['disbursementStatus'] !== null && isset($pay[$a['disbursementStatus']])
                    ? $pay[$a['disbursementStatus']] : '담당자가 연락드릴 예정입니다') . '</p><p><small>지원은 담당자가 따로 연락해 안내합니다. '
                    . '이 홈페이지에서는 계좌번호를 묻지 않습니다.</small></p>' : '') . '</section>';
        }
        if ($a['appeal'] !== null) {
            $names = ['submitted' => '검토 중', 'upheld' => '받아들임', 'dismissed' => '받아들이지 않음'];
            $content .= '<h2>이의신청</h2><dl><dt>낸 때</dt><dd>' . Html::e(self::kst($a['appeal']['createdAt'])) . '</dd><dt>상태</dt><dd>'
                . Html::e(isset($names[$a['appeal']['status']]) ? $names[$a['appeal']['status']] : $a['appeal']['status']) . '</dd>'
                . ($a['appeal']['response'] !== null ? '<dt>답변</dt><dd>' . nl2br(Html::e($a['appeal']['response'])) . '</dd>' : '') . '</dl>';
        }
        if ($a['canReissueGuardianLink'] && $extra === '' && $this->schEnabled()) {
            $content .= '<h2>보호자 동의 주소</h2><p>보호자에게 전할 주소를 잃어버렸으면 새로 만들 수 있습니다. 앞의 주소는 쓸 수 없게 됩니다.</p>'
                . $this->form('/scholarship/status/' . $a['id'] . '/guardian-link', '', '새 주소 만들기');
        }
        if ($a['canAppeal']) {
            $content .= '<h2>이의신청</h2><p>' . Html::e(self::kst($a['appealDeadline'])) . ' (한국 시간) 까지 한 번 할 수 있습니다.</p>'
                . $this->form('/scholarship/status/' . $a['id'] . '/appeal',
                    '<p class="field"><label>이의신청 사유<br><textarea name="reason" rows="6">' . Html::e(self::field($request->form, 'reason')) . '</textarea></label>'
                    . '<br><small>20자 이상 2000자 이하</small></p>', '이의신청 내기');
        }
        if ($a['canWithdraw']) {
            $content .= '<h2>신청 철회</h2><p>철회하면 되돌릴 수 없고, 이 프로그램에 다시 신청할 수 없습니다. 30일 뒤에 신청서의 개인정보를 지웁니다.</p>'
                . $this->form('/scholarship/status/' . $a['id'] . '/withdraw', Html::checkbox('confirm', '신청을 철회합니다', false), '철회하기');
        }
        return $this->page('지원 현황: ' . $a['program']['title'], $content . '<p>' . $this->a('/scholarship/status', '목록으로') . '</p>', $problems ? 422 : 200);
    }

    // ── 보호자 동의 ─────────────────────────────────────────

    private function guardianConsent($token, $get, Request $request)
    {
        $sch = $this->app->scholarships();
        // 보호자는 게임 계정이 없다: 이 화면은 주소로만 열린다. 로그인한 사람의 메뉴를 보이지 않는다.
        $this->player = null;
        $problems = [];
        if (!$get) {
            $problems = $sch->confirmGuardianConsent($token, self::field($request->form, 'guardian_name'), self::field($request->form, 'agree') === '1', $request->ip);
            if (!$problems) {
                return $this->page('보호자 동의', '<div class="notice" role="status"><strong>동의가 기록되었습니다.</strong></div>'
                    . '<p>장학 담당자가 신청서에 적힌 보호자 연락처로 연락해 한 번 더 확인합니다. 그 뒤에 심사가 진행됩니다.</p>'
                    . '<p>동의를 거두고 싶으면 신청자가 지원 현황에서 신청을 철회하면 됩니다.</p>', 200, '', true);
            }
        }
        $info = $sch->guardianConsentInfo($token, $request->ip);
        if ($info === null) {
            return $this->page('보호자 동의', '<p>이 주소는 쓸 수 없습니다. 이미 동의했거나, 기간이 지났거나, 새 주소가 만들어졌습니다.</p>'
                . '<p>신청자에게 새 주소를 받아 주세요.</p>', 404, '', true);
        }
        $content = self::problems($problems, '동의를 기록하지 못했습니다.')
            . '<p class="lead"><strong>' . Html::e($info['applicantName']) . '</strong> 님이 GUARDIANS SCHOLARSHIP 의 아래 프로그램에 신청했습니다. '
            . '미성년자의 신청에는 보호자(법정대리인)의 동의가 필요합니다.</p>'
            . '<dl><dt>프로그램</dt><dd>' . Html::e($info['programTitle']) . '</dd><dt>지원 방식</dt><dd>' . Html::e($info['supportTypeName']) . '</dd>'
            . '<dt>지원 내용</dt><dd>' . nl2br(Html::e($info['supportDetail'])) . '</dd></dl>'
            . '<h2>개인정보 수집·이용</h2>' . self::privacyHtml($info['privacy']) . self::notUsed()
            . '<h2>동의</h2><form method="post" action="' . Html::e($this->url('/scholarship/consent/' . $token)) . '">'
            . Html::input('guardian_name', '보호자 이름', '', '신청서에 적힌 보호자 이름과 같아야 합니다')
            . Html::checkbox('agree', '보호자(법정대리인)로서 위 신청과 개인정보 수집·이용에 동의합니다', false)
            . '<p><button type="submit">동의하기</button></p></form>'
            . '<p><small>동의하지 않으려면 이 화면을 닫으면 됩니다. 동의하지 않은 신청은 심사로 넘어가지 않습니다. '
            . '이 화면에서는 주민등록번호나 계좌번호를 묻지 않습니다.</small></p>';
        return $this->page('보호자 동의', $content, $problems ? 422 : 200, '', true);
    }
}
