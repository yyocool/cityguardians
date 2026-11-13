<?php
namespace CG\Admin;

use CG\Infra\Request;
use CG\Service\Budget;
use CG\Service\Settings;
use CG\Service\Taxes;
use CG\Service\Treasury;

/**
 * 관리자 화면: 지방세 정책(/admin/tax), 도시 재정(/admin/treasury), 참여예산 관리(/admin/budget).
 * (docs/CITY_TAX_SYSTEM.md, docs/CITY_TREASURY_SYSTEM.md, docs/PARTICIPATORY_BUDGET.md) CityOpsPages 가 쓴다.
 *
 * 여기의 세금과 재정은 모두 게임 안의 GC 다. 현금, 현실의 세금, CITY PASS 예산, 장학금 예산과 이어지지 않는다.
 * 도시 재정은 플레이어에게 지급되지 않는다. 방어시설·시설 복구 같은 도시의 일에만 쓰인다.
 */
trait FinanceScreens
{
    private function routeFinance($get, $path, Request $request)
    {
        $form = $request->form;
        $ip = $request->ip;
        if ($path === '/admin/tax') {
            return $get ? $this->taxPage([], $request) : null;
        }
        if (preg_match('#^/admin/tax/(trade|business|property)$#', $path, $m) && !$get) {
            $before = $this->app->taxes()->adminPolicy($m[1]);
            $problems = $this->app->taxes()->savePolicy($this->admin, $m[1], $form, $ip);
            if ($problems) {
                return $this->taxPage($problems, $request);
            }
            return ['redirect' => '/admin/tax?done=' . ($before == $this->app->taxes()->adminPolicy($m[1]) ? 'none' : 'saved')];
        }
        if ($path === '/admin/treasury') {
            return $get ? $this->treasuryPage($request) : null;
        }
        if (preg_match('#^/admin/treasury/(\d{1,8})$#', $path, $m)) {
            return $get ? $this->treasuryCity($m[1], [], $request) : null;
        }
        if (preg_match('#^/admin/treasury/(\d{1,8})/spend$#', $path, $m) && !$get) {
            // 분야에 맞는 대상만 넘긴다.
            $effect = Treasury::effectOf(self::field($form, 'category'));
            $form['target_code'] = $effect === Treasury::EFFECT_FUND ? self::field($form, 'structure_code')
                : ($effect === Treasury::EFFECT_REPAIR ? self::field($form, 'facility_code') : '');
            list(, $problems) = $this->app->treasury()->spend($this->admin, $m[1], $form, null, $ip);
            return $problems ? $this->treasuryCity($m[1], $problems, $request) : ['redirect' => '/admin/treasury/' . $m[1] . '?done=spent'];
        }
        if ($path === '/admin/budget') {
            return $get ? $this->budgetPage($request) : null;
        }
        if (preg_match('#^/admin/budget/(\d{1,18})$#', $path, $m)) {
            return $get ? $this->budgetDetail((int)$m[1], [], $request) : null;
        }
        if (preg_match('#^/admin/budget/(\d{1,18})/(approve|reject|cancel|execute)$#', $path, $m) && !$get) {
            $id = (int)$m[1];
            $budget = $this->app->budget();
            $reason = self::field($form, 'reason');
            if ($m[2] === 'approve' || $m[2] === 'reject') {
                $problems = $budget->review($this->admin, $id, $m[2] === 'approve', $reason, $ip);
                $done = $m[2] === 'approve' ? 'approved' : 'rejected';
            } elseif ($m[2] === 'cancel') {
                $problems = $budget->cancel($this->admin, $id, $reason, $ip);
                $done = 'cancelled';
            } else {
                $problems = $budget->execute($this->admin, $id, $reason, $ip);
                $done = 'executed';
            }
            return $problems ? $this->budgetDetail($id, $problems, $request) : ['redirect' => '/admin/budget/' . $id . '?done=' . $done];
        }
        return null;
    }

    private static function percent($basisPoints)
    {
        return rtrim(rtrim(number_format((int)$basisPoints / 100, 2), '0'), '.') . '%';
    }

    // ── 지방세 정책 ─────────────────────────────────────────

    private function taxPage(array $problems, Request $request)
    {
        $taxes = $this->app->taxes();
        $types = Taxes::types();
        $reasons = Taxes::taxableReasons();
        $periods = Taxes::periods();
        $policy = $this->can(Auth::CITY_POLICY);
        $content = self::done($request) . Html::problems($problems)
            . '<p>게임 안의 GC 로만 걷는 가상의 지방세입니다. 현금이나 현실의 세금과 이어지지 않습니다. '
            . '걷은 세금은 낸 사람이 사는 도시의 재정으로 들어가고, 걷을 때마다 거래 명세와 도시 재정 원장에 남습니다. '
            . '자발적 방어 기여금(시설 기금)은 세금이 아니며 따로 관리됩니다.</p>'
            . '<p>초보자 보호: 정해 둔 레벨에 못 미치거나, 처음 전입한 지 정해 둔 날수가 지나지 않은 계정은 내지 않습니다. '
            . '주거 도시가 없는 계정도 내지 않습니다. 세율은 20% 를 넘길 수 없습니다.</p>';

        $totals = $taxes->adminTotals();
        $rows = [];
        foreach ($types as $type => $name) {
            $rows[] = [$name, self::n($totals[$type]['count']), self::n($totals[$type]['total']) . ' GC', self::n($totals[$type]['week']) . ' GC'];
        }
        $content .= '<h2>징수 합계</h2>' . Html::table(['세금', '건수', '전체', '최근 7일'], $rows);

        $help = [
            Taxes::TRADE => ['면제 기준 (GC)', '이 금액 미만의 거래에는 붙지 않습니다.', '상한 (GC)', '쓰지 않습니다 (0).',
                '치른 값에 이미 들어 있는 세금입니다. 켜도 플레이어가 치르는 값은 그대로이고, 그 가운데 세율만큼이 도시 재정으로 갑니다.'],
            Taxes::BUSINESS => ['면제 기준 (GC)', '하루 소득 가운데 이만큼까지는 붙지 않습니다.', '하루 상한 (GC)', '0 이면 상한이 없습니다.',
                '전투 보상에서 떼는 세금입니다. 하루 소득이 면제 기준을 넘은 부분에만 붙습니다.'],
            Taxes::PROPERTY => ['면제 기준 (GC)', '보유한 GC 가운데 이만큼까지는 붙지 않습니다.', '한 기간의 상한 (GC)', '0 이면 상한이 없습니다.',
                '보유한 GC 에 한 기간에 한 번 붙습니다. 예약 작업이 걷고, 정해 둔 날수 동안 접속하지 않은 계정은 건너뜁니다. 세금 때문에 잔액이 면제 기준 아래로 내려가지 않습니다.'],
        ];
        foreach ($types as $type => $name) {
            $row = $taxes->adminPolicy($type);
            if ($row === null) {
                continue;
            }
            $applies = array_filter(explode(',', $row['applies_to']), 'strlen');
            $content .= '<h2 id="' . Html::e($type) . '">' . Html::e($name) . ' — ' . ((int)$row['is_active'] === 1 ? '● 걷는 중' : '○ 꺼짐') . '</h2>'
                . '<p>' . Html::e($help[$type][4]) . '</p>';
            if (!$policy) {
                $names = [];
                foreach ($applies as $reason) {
                    $names[] = isset($reasons[$type][$reason]) ? $reasons[$type][$reason] : $reason;
                }
                $content .= Html::table(['항목', '값'], [
                    ['세율', self::percent($row['rate_bp'])], ['적용 대상', $type === Taxes::PROPERTY ? '보유 GC' : implode(', ', $names)],
                    [$help[$type][0], self::n($row['exempt_amount'])], ['초보자 보호: 최소 레벨', (int)$row['min_level']],
                    ['초보자 보호: 전입 뒤 날수', (int)$row['grace_days']], [$help[$type][2], self::n($row['cap_gc'])],
                    ['징수 시점', $periods[$row['period']]],
                ]);
                continue;
            }
            // 고치다 틀린 폼이면 보낸 값을 그대로 다시 보여 준다.
            $sent = $problems && self::field($request->form, 'tax_type') === $type ? $request->form : null;
            $value = function ($key) use ($sent, $row) {
                return $sent !== null ? self::field($sent, $key) : (string)$row[$key];
            };
            $inner = '<input type="hidden" name="tax_type" value="' . Html::e($type) . '">'
                . Html::checkbox('is_active', '이 세금을 걷는다', $sent !== null ? self::field($sent, 'is_active') === '1' : (int)$row['is_active'] === 1)
                . Html::input('rate_bp', '세율 (1/100 %)', $value('rate_bp'), '0 ~ 2,000 (300 = 3%). 지금: ' . self::percent($row['rate_bp']));
            foreach ($reasons[$type] as $reason => $label) {
                $inner .= Html::checkbox('applies_' . $reason, '적용 대상: ' . $label,
                    $sent !== null ? self::field($sent, 'applies_' . $reason) === '1' : in_array($reason, $applies, true));
            }
            $inner .= Html::input('exempt_amount', $help[$type][0], $value('exempt_amount'), $help[$type][1])
                . Html::input('min_level', '초보자 보호: 최소 레벨', $value('min_level'), '1 ~ 50. 이 레벨에 못 미치면 내지 않습니다.')
                . Html::input('grace_days', '초보자 보호: 전입 뒤 날수', $value('grace_days'), '0 ~ 365. 처음 전입한 지 이 날수가 지나기 전에는 내지 않습니다.')
                . Html::input('cap_gc', $help[$type][2], $value('cap_gc'), $help[$type][3]);
            $inner .= $type === Taxes::PROPERTY
                ? Html::select('period', '징수 시점', ['weekly' => $periods['weekly'], 'monthly' => $periods['monthly']], $value('period'))
                : '<input type="hidden" name="period" value="transaction"><p class="sub">징수 시점: ' . Html::e($periods['transaction']) . '</p>';
            $content .= $this->form('/admin/tax/' . $type, $inner . Html::reason());
        }

        $rows = [];
        foreach ($taxes->adminTransactions(null, 100) as $line) {
            $rows[] = [self::kst($line['created_at']), $types[$line['tax_type']], $line['region_name'], $line['player_public_id'],
                isset($reasons[$line['tax_type']][$line['source_reason']]) ? $reasons[$line['tax_type']][$line['source_reason']] : $line['source_reason'],
                self::n($line['base_gc']), self::percent($line['rate_bp']), self::n($line['tax_gc']),
                (int)$line['included'] === 1 ? '치른 값에 포함' : '지갑에서 차감', (string)$line['period_key']];
        }
        $content .= '<h2>최근 거래 명세 (100건)</h2><p>낸 사람은 계정의 공개 ID 로만 보입니다.</p>'
            . Html::table(['때 (한국 시간)', '세금', '도시', '계정', '과세 대상', '과세 금액', '세율', '세금 (GC)', '걷는 방식', '기간'], $rows, '아직 걷은 세금이 없습니다.');

        $rows = [];
        foreach ($taxes->policyHistory() as $line) {
            $rows[] = [self::kst($line['created_at']), isset($types[$line['target_key']]) ? $types[$line['target_key']] : $line['target_key'],
                (string)$line['login'], $line['reason'], $line['before_value'], $line['after_value']];
        }
        $content .= '<h2>정책 변경 이력</h2>' . Html::table(['때 (한국 시간)', '세금', '관리자', '사유', '바꾸기 전', '바꾼 뒤'], $rows, '변경한 적이 없습니다.');
        return $this->page('지방세 정책', $content, $problems ? 422 : 200);
    }

    // ── 도시 재정 ───────────────────────────────────────────

    private function treasuryPage(Request $request)
    {
        $treasury = $this->app->treasury();
        $categories = Treasury::categories();
        $content = self::done($request)
            . '<p>도시 재정은 게임 안의 GC 입니다. CITY PASS 예산, 장학금 예산과는 테이블도 화면도 권한도 따로이며 서로 옮길 수 없습니다. '
            . '플레이어에게 지급하는 데에는 쓸 수 없습니다.</p>';
        $rows = [];
        foreach ($treasury->adminCities() as $city) {
            $rows[] = [$this->link('/admin/treasury/' . $city['code'], $city['name']), self::n($city['general_gc']), self::n($city['reserve_gc']),
                self::n($city['total_in']), self::n($city['total_out'])];
        }
        $content .= '<h2>도시별 재정</h2>' . Html::table(['도시', '일반 회계 (GC)', '예비비 (GC)', '들어온 합', '나간 합'], $rows, '아직 재정이 있는 도시가 없습니다.');

        $rows = [];
        foreach ($treasury->adminExpenditures(null, 100) as $line) {
            $rows[] = $this->expenditureRow($line, $categories, true);
        }
        $content .= '<h2>최근 예산 사용 내역 (100건)</h2><p>집행 내역은 그 도시의 주민 모두에게 공개됩니다.</p>'
            . Html::table(['때 (한국 시간)', '도시', '분야', '이름', '금액 (GC)', '효과', '근거', '집행한 관리자', '사유'], $rows, '아직 집행한 적이 없습니다.');

        $problem = $treasury->findInconsistency();
        $content .= '<h2>원장 점검</h2><p>' . ($problem === null
            ? '○ 계정마다 원장의 합이 잔액과 같고, 걷은 세금의 합이 재정에 들어온 세금의 합과 같으며, 집행 내역의 합이 나간 금액의 합과 같습니다.'
            : '● 맞지 않는 곳이 있습니다: ' . Html::e($problem)) . '</p>';
        $content .= $this->settingsSection(Settings::TREASURY, '/admin/treasury');
        return $this->page('도시 재정', $content);
    }

    private function expenditureRow(array $line, array $categories, $withCity)
    {
        $row = [self::kst($line['created_at'])];
        if ($withCity) {
            $row[] = $this->link('/admin/treasury/' . $line['region_code'], $line['region_name']);
        }
        return array_merge($row, [isset($categories[$line['category']]) ? $categories[$line['category']] : $line['category'], $line['title'],
            self::n($line['amount_gc']), $line['effect_detail'],
            $line['proposal_id'] !== null ? $this->link('/admin/budget/' . (int)$line['proposal_id'], '참여예산 (주민 투표)') : '관리자 집행',
            (string)$line['login'], $line['reason']]);
    }

    private function treasuryCity($regionCode, array $problems, Request $request)
    {
        $treasury = $this->app->treasury();
        $region = $this->app->db->one('SELECT id, code, name FROM regions WHERE code = ?', [(string)$regionCode]);
        if ($region === null) {
            return null;
        }
        $regionId = (int)$region['id'];
        $categories = Treasury::categories();
        $accounts = Treasury::accounts();
        $balances = $treasury->balances($regionId);
        $content = $this->back('/admin/treasury', '도시 재정으로') . self::done($request) . Html::problems($problems)
            . Html::table(['계정', '잔액 (GC)'], [
                [$accounts[Treasury::GENERAL], self::n($balances[Treasury::GENERAL])],
                [$accounts[Treasury::RESERVE], self::n($balances[Treasury::RESERVE])],
            ]);

        if ($this->can(Auth::TREASURY_EXECUTE)) {
            $structures = ['' => '(고르지 않음)'];
            foreach ($this->app->defs()->structures() as $def) {
                if ($def['ownership'] === 'city' && !$def['comingSoon'] && $def['cityGoalGc'] > 0) {
                    $structures[$def['code']] = $def['name'];
                }
            }
            $facilities = ['' => '(고르지 않음)'];
            foreach ($this->app->defs()->facilities() as $def) {
                $facilities[$def['code']] = $def['name'] . ' (한 단계 ' . number_format($def['repairGc']) . ' GC)';
            }
            $sent = $problems ? $request->form : [];
            $content .= '<h2 id="spend">예산 집행</h2>'
                . '<p>공동 방어시설은 고른 시설의 기금이 채워지고, 방어시설 유지보수는 피해를 입은 주요 시설이 고쳐집니다. '
                . '실제로 쓰인 금액만 재정에서 나갑니다. 그 밖의 분야는 지금은 집행 내역만 남습니다 (게임 내 효과는 준비 중). '
                . '예비비 분야만 예비비 계정에서 나갑니다. 집행 내역은 주민에게 공개됩니다.</p>'
                . $this->form('/admin/treasury/' . $region['code'] . '/spend',
                    Html::select('category', '분야', $categories, self::field($sent, 'category', 'defense_facility'))
                    . Html::select('structure_code', '공동 방어시설 (분야가 공동 방어시설일 때)', $structures, self::field($sent, 'structure_code'))
                    . Html::select('facility_code', '고칠 주요 시설 (분야가 방어시설 유지보수일 때)', $facilities, self::field($sent, 'facility_code'))
                    . Html::input('title', '집행 이름 (공개)', self::field($sent, 'title'), '60자까지')
                    . Html::textarea('description', '설명 (공개)', self::field($sent, 'description'))
                    . Html::input('amount_gc', '금액 (GC)', self::field($sent, 'amount_gc'), '1 ~ 100,000,000')
                    . Html::reason(), '집행');
        }

        $rows = [];
        foreach ($treasury->adminExpenditures($regionId, 100) as $line) {
            $rows[] = $this->expenditureRow($line, $categories, false);
        }
        $content .= '<h2>예산 사용 내역</h2>'
            . Html::table(['때 (한국 시간)', '분야', '이름', '금액 (GC)', '효과', '근거', '집행한 관리자', '사유'], $rows, '아직 집행한 적이 없습니다.');

        $rows = [];
        foreach ($treasury->adminLedger($regionId, 200) as $line) {
            $rows[] = [(int)$line['id'], self::kst($line['created_at']), $accounts[$line['account']], Treasury::reasonName($line['reason']),
                self::signed($line['delta']), self::n($line['balance_after']), trim((string)$line['ref_type'] . ' ' . (string)$line['ref_id'])];
        }
        $content .= '<h2>재정 원장 (최근 200줄)</h2>'
            . Html::table(['번호', '때 (한국 시간)', '계정', '사유', '변화 (GC)', '바뀐 뒤 잔액', '근거'], $rows, '아직 기록이 없습니다.');

        $types = Taxes::types();
        $rows = [];
        foreach ($this->app->taxes()->adminTransactions($regionId, 100) as $line) {
            $rows[] = [self::kst($line['created_at']), $types[$line['tax_type']], $line['player_public_id'], self::n($line['base_gc']),
                self::percent($line['rate_bp']), self::n($line['tax_gc'])];
        }
        $content .= '<h2>이 도시가 걷은 세금 (최근 100건)</h2>'
            . Html::table(['때 (한국 시간)', '세금', '계정', '과세 금액', '세율', '세금 (GC)'], $rows, '아직 걷은 세금이 없습니다.');
        return $this->page($region['name'] . ' — 도시 재정', $content, $problems ? 422 : 200);
    }

    // ── 참여예산 관리 ───────────────────────────────────────

    private function budgetPage(Request $request)
    {
        $budget = $this->app->budget();
        $statuses = Budget::statuses();
        $categories = Treasury::categories();
        $enabled = $this->app->settings()->budget()['enabled'] === 1;
        $content = self::done($request)
            . '<p>' . ($enabled ? '○ 참여예산이 열려 있습니다.' : '● 참여예산이 꺼져 있습니다 (아래 설정의 "참여예산 열기"). 꺼져 있는 동안에는 제안과 투표를 받지 않습니다.') . '</p>'
            . '<p>예산 제안 → 제안 검토 → 주민 투표 → 결과 확정 → 예산 집행 → 집행 내역 공개 의 순서로 진행됩니다. '
            . '투표는 계정마다 한 표이고, 보유한 GC 나 낸 세금과 상관없이 같은 한 표입니다. 나이로 투표를 막지 않습니다.</p>';

        $counts = $budget->adminCounts();
        $rows = [];
        foreach ($statuses as $status => $name) {
            $rows[] = [$this->link('/admin/budget?status=' . $status, $name), self::n($counts[$status])];
        }
        $content .= '<h2>제안 수</h2>' . Html::table(['상태', '제안'], $rows);

        $status = self::field($request->query, 'status');
        if (!isset($statuses[$status])) {
            $status = '';
        }
        $rows = [];
        foreach ($budget->adminList($status) as $proposal) {
            $rows[] = [$this->link('/admin/budget/' . (int)$proposal['id'], $proposal['title']), $proposal['region_name'],
                isset($categories[$proposal['category']]) ? $categories[$proposal['category']] : $proposal['category'],
                self::n($proposal['requested_gc']), $statuses[$proposal['status']], $proposal['nickname'],
                (int)$proposal['yes_count'] . ' / ' . (int)$proposal['no_count'] . ' (최소 ' . (int)$proposal['quorum'] . '표)',
                self::kst($proposal['vote_ends_at']), self::kst($proposal['created_at'])];
        }
        $content .= '<h2>' . Html::e($status !== '' ? $statuses[$status] : '전체') . ' 제안</h2>'
            . ($status !== '' ? '<p>' . Html::e($this->link('/admin/budget', '전체 보기')) . '</p>' : '')
            . Html::table(['제안', '도시', '분야', '요청 금액 (GC)', '상태', '제안한 사람', '찬성 / 반대', '투표 마감', '제안한 때'], $rows, '제안이 없습니다.');
        $content .= $this->settingsSection(Settings::BUDGET, '/admin/budget');
        return $this->page('참여예산 관리', $content);
    }

    private function budgetDetail($id, array $problems, Request $request)
    {
        $budget = $this->app->budget();
        // 마감이 지난 투표는 먼저 결과를 확정한다.
        $budget->closeDue();
        $proposal = $budget->adminGet($id);
        if ($proposal === null) {
            return null;
        }
        $statuses = Budget::statuses();
        $categories = Treasury::categories();
        $regionId = (int)$proposal['region_id'];
        $balances = $this->app->treasury()->balances($regionId);
        $target = '';
        if ($proposal['target_code'] !== null && $proposal['target_code'] !== '') {
            $def = $this->app->defs()->findStructure($proposal['target_code']);
            $def = $def !== null ? $def : $this->app->defs()->findFacility($proposal['target_code']);
            $target = $def !== null ? $def['name'] : $proposal['target_code'];
        }
        $votes = (int)$this->app->db->val('SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ?', [$proposal['id']]);
        $content = $this->back('/admin/budget') . self::done($request) . Html::problems($problems)
            . Html::table(['항목', '값'], [
                ['도시', $this->link('/admin/treasury/' . $proposal['region_code'], $proposal['region_name'])],
                ['분야', isset($categories[$proposal['category']]) ? $categories[$proposal['category']] : $proposal['category']],
                ['대상', $target], ['요청 금액', self::n($proposal['requested_gc']) . ' GC'],
                ['도시 재정 (일반 회계)', self::n($balances[Treasury::GENERAL]) . ' GC'],
                ['상태', $statuses[$proposal['status']]],
                ['제안한 사람', $proposal['nickname'] . ' (' . $proposal['player_public_id'] . ')'],
                ['제안한 때 (한국 시간)', self::kst($proposal['created_at'])],
                ['검토 의견', $proposal['review_note']], ['검토한 때', self::kst($proposal['reviewed_at'])],
                ['투표 기간', $proposal['vote_starts_at'] !== null ? self::kst($proposal['vote_starts_at']) . ' ~ ' . self::kst($proposal['vote_ends_at']) : ''],
                ['찬성 / 반대', (int)$proposal['yes_count'] . ' / ' . (int)$proposal['no_count'] . ' (기록된 표 ' . $votes . ', 최소 ' . (int)$proposal['quorum'] . '표)'],
                ['결과 확정', self::kst($proposal['decided_at'])],
                ['집행 내역', $proposal['expenditure_id'] !== null ? '집행됨 (도시 재정의 예산 사용 내역에 공개)' : ''],
            ])
            . '<h2>제안 내용</h2><p>' . nl2br(Html::e($proposal['description'])) . '</p>'
            . '<p class="sub">누가 어느 쪽에 투표했는지는 관리자 화면에서도 보여 주지 않습니다.</p>';

        if ($proposal['status'] === 'submitted' && $this->can(Auth::BUDGET_MANAGE)) {
            $content .= '<h2>제안 검토</h2><p>승인하면 바로 주민 투표가 열립니다. 반려 사유는 제안한 사람에게만 보입니다. '
                . '검토는 내용이 규칙에 맞는지만 봅니다 (욕설, 개인정보, 현실의 금전 요구, 게임과 무관한 내용).</p>'
                . $this->form('/admin/budget/' . $id . '/approve', Html::input('reason', '검토 의견 (필수)', ''), '승인하고 투표 열기')
                . $this->form('/admin/budget/' . $id . '/reject', Html::input('reason', '반려 사유 (필수)', ''), '반려');
        }
        if (($proposal['status'] === 'voting' || $proposal['status'] === 'passed') && $this->can(Auth::BUDGET_MANAGE)) {
            $content .= '<h2>취소</h2><p>규칙을 어긴 제안이 뒤늦게 발견된 경우에만 씁니다. 이미 집행된 제안은 취소할 수 없습니다.</p>'
                . $this->form('/admin/budget/' . $id . '/cancel', Html::reason(), '이 제안 취소');
        }
        if ($proposal['status'] === 'passed' && $this->can(Auth::TREASURY_EXECUTE)) {
            $content .= '<h2>예산 집행</h2><p>도시 재정(일반 회계)에서 요청 금액이 나가고, 집행 내역이 주민에게 공개됩니다. 제안 하나는 한 번만 집행됩니다.</p>'
                . $this->form('/admin/budget/' . $id . '/execute', Html::reason(), '집행');
        }
        return $this->page($proposal['title'] . ' — 참여예산 제안', $content, $problems ? 422 : 200);
    }
}
