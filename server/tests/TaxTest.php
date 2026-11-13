<?php
use CG\Admin\Auth;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Service\Settings;
use CG\Service\Taxes;
use CG\Service\Treasury;
use CG\Service\Wallets;

/**
 * v9.0 가상 지방세와 도시 재정.
 * (지시서 11장의 테스트 항목: 세금 중복 징수 방지, 도시 재정 원장 일치)
 */
final class TaxTest extends CityTestCase
{
    private function taxRows($type = null)
    {
        return $type === null
            ? $this->db->all('SELECT * FROM city_tax_transactions ORDER BY id')
            : $this->db->all('SELECT * FROM city_tax_transactions WHERE tax_type = ? ORDER BY id', [$type]);
    }

    private function consistent()
    {
        T::eq(null, $this->app->treasury()->findInconsistency());
        T::eq(null, $this->app->city()->findInconsistency());
    }

    // ── 기본 상태 ───────────────────────────────────────────

    public function testEveryTaxIsOffByDefaultAndNothingChangesFromV8()
    {
        $this->quiet();
        foreach ($this->app->taxes()->policies() as $policy) {
            T::eq(false, $policy['active'], $policy['taxType']);
        }
        $player = $this->setLevel($this->resident('11110'), 10);
        $this->give($player, 100000);
        $base = $this->gc($player);
        $this->app->combat()->buy($player, 'gear_precision_rifle', $this->key());
        $this->app->defense()->purchase($player, 'twr_blast', $this->key());
        $this->winDefense($player);
        $result = $this->app->taxes()->collectProperty();
        T::eq(0, $result['collected']);
        T::eq([], $this->taxRows());
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_treasury_ledger'));
        // v8.0 과 같은 금액: 라이플 600, 폭발 타워 500, 방어전 보상 300 + 첫 승리 200
        T::eq($base - 600 - 500 + 500, $this->gc($player));
        T::eq(null, $this->app->wallets()->findInconsistency($player['id']));
        $this->consistent();
    }

    // ── 거래세 ──────────────────────────────────────────────

    public function testTradeTaxIsPartOfThePriceAndGoesToThePayersCity()
    {
        $this->enableTax(Taxes::TRADE);
        $player = $this->setLevel($this->resident('11110'), 10);
        $this->give($player, 10000);
        $before = $this->gc($player);

        $key = $this->key();
        $this->app->combat()->buy($player, 'gear_precision_rifle', $key);
        // 값은 그대로 600 GC. 그 가운데 3% (18 GC) 가 도시 재정으로 간다.
        T::eq($before - 600, $this->gc($player));
        $rows = $this->taxRows();
        T::eq(1, count($rows));
        T::eq('trade', $rows[0]['tax_type']);
        T::eq(600, (int)$rows[0]['base_gc']);
        T::eq(300, (int)$rows[0]['rate_bp']);
        T::eq(18, (int)$rows[0]['tax_gc']);
        T::eq(1, (int)$rows[0]['included']);
        T::eq(Wallets::GEAR_PURCHASE, $rows[0]['source_reason']);
        T::eq($this->regionId('11110'), (int)$rows[0]['region_id']);
        T::eq(null, $rows[0]['wallet_ledger_id'], '치른 값에 들어 있는 세금은 지갑에서 따로 빠지지 않는다');
        // 10% 는 예비비로.
        T::eq(17, $this->treasuryGc('11110'));
        T::eq(1, $this->treasuryGc('11110', 'reserve'));
        T::eq(0, $this->treasuryGc('26110'));

        // 같은 요청이 다시 와도 세금은 한 번만 걷힌다.
        $again = $this->app->combat()->buy($player, 'gear_precision_rifle', $key);
        T::eq(true, $again['duplicate']);
        T::eq(1, count($this->taxRows()));
        T::eq(17, $this->treasuryGc('11110'));

        // 타워 해금 500 GC → 15 GC
        $this->app->defense()->purchase($player, 'twr_blast', $this->key());
        T::eq($before - 600 - 500, $this->gc($player));
        T::eq(15, (int)$this->taxRows()[1]['tax_gc']);
        T::eq(17 + 14, $this->treasuryGc('11110'));
        T::eq(2, $this->treasuryGc('11110', 'reserve'));
        $this->consistent();
        T::eq(null, $this->app->wallets()->findInconsistency($player['id']));

        // 주민이 스스로 내는 방어 기여금에는 세금이 붙지 않는다.
        $this->app->city()->fundStructure($player, 'twr_energy_shield', 1000, $this->key());
        T::eq(2, count($this->taxRows()));
    }

    public function testTheSameLedgerLineIsNeverTaxedTwice()
    {
        $this->enableTax(Taxes::TRADE);
        $player = $this->setLevel($this->resident('11110'), 10);
        $this->give($player, 10000);
        $this->app->combat()->buy($player, 'gear_precision_rifle', $this->key());
        $row = $this->taxRows()[0];
        $general = $this->treasuryGc('11110');

        // 같은 원장 줄을 다시 과세하려 해도 (재시도, 중복 호출) 아무 일도 일어나지 않는다.
        $this->db->tx(function () use ($player, $row) {
            $this->app->taxes()->onLedger($player['id'], -600, Wallets::GEAR_PURCHASE, (int)$row['source_ledger_id'], 0);
            $this->app->taxes()->onLedger($player['id'], -600, Wallets::GEAR_PURCHASE, (int)$row['source_ledger_id'], 0);
        });
        T::eq(1, count($this->taxRows()));
        T::eq($general, $this->treasuryGc('11110'));
        $this->consistent();
    }

    public function testSmallPurchasesAndNonTaxableReasonsAreExempt()
    {
        // 면제 기준을 1,000 GC 로 올리면 600 GC 짜리 구매에는 붙지 않는다.
        $this->enableTax(Taxes::TRADE, ['exempt_amount' => 1000]);
        $player = $this->setLevel($this->resident('11110'), 10);
        $this->give($player, 10000);
        $this->app->combat()->buy($player, 'gear_precision_rifle', $this->key());
        T::eq([], $this->taxRows());

        // DB 를 직접 고쳐 과세 대상에 다른 사유를 넣어도 세금이 붙지 않는다.
        $this->enableTax(Taxes::TRADE, ['exempt_amount' => 0, 'applies_to' => 'city_fund,residency_move,admin_adjust']);
        $this->app->city()->fundStructure($player, 'twr_energy_shield', 1000, $this->key());
        $this->give($player, -100);
        T::eq([], $this->taxRows());
    }

    // ── 사업세 ──────────────────────────────────────────────

    public function testBusinessTaxAppliesAboveTheDailyExemptionAndUpToTheDailyCap()
    {
        $this->quiet();
        $this->enableTax(Taxes::BUSINESS);
        $player = $this->resident('11110');
        $base = $this->gc($player);

        // 하루 1,000 GC 까지는 면제: 500 (첫 승리) + 300 = 800
        $this->winDefense($player);
        $this->winDefense($player);
        T::eq($base + 800, $this->gc($player));
        T::eq([], $this->taxRows());

        // 세 번째 승리로 1,100 GC: 넘은 100 GC 의 5% = 5 GC
        $result = $this->winDefense($player);
        T::eq(300, $result['rewardGc'], '보상 금액 자체는 그대로 알려 준다');
        T::eq($base + 1100 - 5, $this->gc($player));
        $rows = $this->taxRows();
        T::eq(1, count($rows));
        T::eq('business', $rows[0]['tax_type']);
        T::eq(100, (int)$rows[0]['base_gc']);
        T::eq(5, (int)$rows[0]['tax_gc']);
        T::eq(0, (int)$rows[0]['included']);
        // 지갑 원장에 세금 줄이 남고, 명세가 그 줄을 가리킨다.
        $ledger = $this->db->one('SELECT * FROM ledger_entries WHERE id = ?', [$rows[0]['wallet_ledger_id']]);
        T::eq(Wallets::TAX_BUSINESS, $ledger['reason']);
        T::eq(-5, (int)$ledger['delta']);
        T::eq((int)$rows[0]['id'], (int)$ledger['ref_id']);
        T::eq($base + 1100 - 5, (int)$ledger['balance_after']);

        // 네 번째: 300 GC 전부가 과세 대상 → 15 GC
        $this->winDefense($player);
        T::eq($base + 1400 - 20, $this->gc($player));
        // 5 GC → 일반 회계 5 (10% 는 0 으로 내림), 15 GC → 일반 회계 14 + 예비비 1
        T::eq(19, $this->treasuryGc('11110'));
        T::eq(1, $this->treasuryGc('11110', 'reserve'));

        $mine = $this->app->taxes()->policiesFor($player);
        T::eq(1400, $mine['businessIncomeToday']);
        T::eq(20, $mine['businessTaxToday']);
        $list = $this->app->taxes()->transactionsOf($player);
        T::eq(2, count($list['transactions']));
        T::eq(15, $list['transactions'][0]['taxGc']);
        T::eq(20, $list['totals'][0]['totalGc']);

        // 날이 바뀌면 면제액을 다시 센다.
        Clock::advance(86400);
        $this->winDefense($player);
        T::eq(2, count($this->taxRows()));
        T::eq(null, $this->app->wallets()->findInconsistency($player['id']));
        $this->consistent();
    }

    public function testBusinessTaxStopsAtTheDailyCap()
    {
        $this->quiet();
        $this->enableTax(Taxes::BUSINESS, ['exempt_amount' => 0, 'cap_gc' => 30]);
        $player = $this->resident('11110');
        $base = $this->gc($player);
        $this->winDefense($player);   // 500 → 25
        $this->winDefense($player);   // 300 → 15 인데 상한까지 5 만
        $this->winDefense($player);   // 상한에 닿아 0
        T::eq($base + 1100 - 30, $this->gc($player));
        T::eq([25, 5], array_map('intval', array_column($this->taxRows(), 'tax_gc')));
        $this->consistent();
    }

    // ── 초보자 보호 ─────────────────────────────────────────

    public function testBeginnersNewResidentsAndStrangersAreProtected()
    {
        $this->quiet();
        // 기본 초보자 보호: 사업세는 레벨 5 부터, 전입한 지 14일 뒤부터.
        $this->db->exec("UPDATE city_tax_policies SET is_active = 1, exempt_amount = 0 WHERE tax_type = 'business'");
        $this->app->taxes()->forget();
        $player = $this->resident('11110');
        $base = $this->gc($player);

        $this->winDefense($player);
        T::eq([], $this->taxRows(), '레벨이 낮으면 내지 않는다');
        $view = $this->app->taxes()->policiesFor($player);
        $business = null;
        foreach ($view['policies'] as $policy) {
            if ($policy['taxType'] === 'business') {
                $business = $policy;
            }
        }
        T::eq(true, $business['exempt']);
        T::eq('beginner_level', $business['exemptReason']);

        $this->setLevel($player, 5);
        $this->app->taxes()->forget();
        $this->winDefense($player);
        T::eq([], $this->taxRows(), '전입한 지 14일이 되지 않으면 내지 않는다');

        Clock::advance(15 * 86400);
        $this->app->taxes()->forget();
        $this->winDefense($player);
        T::eq(1, count($this->taxRows()));
        T::eq(15, (int)$this->taxRows()[0]['tax_gc']);
        T::eq($base + 500 + 300 + 300 - 15, $this->gc($player));

        // 이사해도 보호 기간이 다시 시작되지 않는다 (처음 전입한 때가 기준이다). 세금은 새 도시로 간다.
        $this->give($player, 2000);
        $this->app->growth()->move($player, '26110', $this->key());
        $this->app->taxes()->forget();
        $this->winDefense($player);
        T::eq(2, count($this->taxRows()));
        T::eq($this->regionId('26110'), (int)$this->taxRows()[1]['region_id']);

        // 이방인은 세금을 내지 않는다.
        $this->enableTax(Taxes::BUSINESS, ['exempt_amount' => 0]);
        $stranger = $this->guest();
        $this->winDefense($stranger);
        T::eq(2, count($this->taxRows()));
        $this->consistent();
    }

    // ── 재산세 ──────────────────────────────────────────────

    public function testPropertyTaxIsCollectedOncePerPeriodFromActiveResidentsOnly()
    {
        $this->enableTax(Taxes::PROPERTY);
        $rich = $this->resident('11110');
        $this->give($rich, 60000);            // 60,500 GC
        $poor = $this->resident('11110');     // 500 GC: 면제 기준(10,000 GC) 아래
        $stranger = $this->guest();
        $this->give($stranger, 100000);       // 이방인

        $result = $this->app->taxes()->collectProperty();
        T::eq(1, $result['collected']);
        // (60,500 − 10,000) × 1% = 505 → 상한 500
        T::eq(500, $result['totalGc']);
        T::eq(60000, $this->gc($rich));
        T::eq(500, $this->gc($poor));
        T::eq(100000, $this->gc($stranger));
        $rows = $this->taxRows(Taxes::PROPERTY);
        T::eq(1, count($rows));
        T::eq('2026-W46', $rows[0]['period_key']);
        T::eq(50500, (int)$rows[0]['base_gc']);
        T::eq(450, $this->treasuryGc('11110'));
        T::eq(50, $this->treasuryGc('11110', 'reserve'));

        // 같은 주에 예약 작업이 몇 번을 돌아도 한 번만 걷는다.
        T::eq(0, $this->app->taxes()->collectProperty()['collected']);
        T::eq(0, \CG\Service\CityOpsTick::run($this->app)['propertyTaxPayers']);
        T::eq(60000, $this->gc($rich));
        T::eq(1, count($this->taxRows(Taxes::PROPERTY)));

        // 오래 접속하지 않은 주민에게는 걷지 않는다 (쉬는 동안 잔액이 깎이지 않는다).
        Clock::advance(8 * 86400);
        T::eq(0, $this->app->taxes()->collectProperty()['collected']);
        T::eq(60000, $this->gc($rich));
        // 다시 접속한 뒤, 새 기간에 한 번.
        $this->db->exec('UPDATE players SET last_seen_at = ? WHERE id = ?', [Clock::sql(), $rich['id']]);
        $result = $this->app->taxes()->collectProperty();
        T::eq(1, $result['collected']);
        T::eq(500, $result['totalGc']);
        T::eq(59500, $this->gc($rich));
        T::eq('2026-W47', $this->taxRows(Taxes::PROPERTY)[1]['period_key']);
        T::eq(null, $this->app->wallets()->findInconsistency($rich['id']));
        $this->consistent();
    }

    public function testPropertyTaxNeverTakesTheBalanceBelowTheExemption()
    {
        // 세율 20%, 상한 없음, 면제 10,000 GC
        $this->enableTax(Taxes::PROPERTY, ['rate_bp' => 2000, 'cap_gc' => 0]);
        $player = $this->resident('11110');
        $this->give($player, 9600);   // 10,100 GC → 과세 대상 100 GC → 20 GC
        T::eq(20, $this->app->taxes()->collectProperty()['totalGc']);
        T::eq(10080, $this->gc($player));
        T::eq('2026-11', Taxes::periodKey('monthly'));
    }

    // ── 정책 변경 ───────────────────────────────────────────

    public function testAdminChangesPoliciesWithinRangesWithReasonHistoryAndAudit()
    {
        $taxes = $this->app->taxes();
        $form = ['is_active' => '1', 'rate_bp' => '500', 'exempt_amount' => '200', 'min_level' => '2', 'grace_days' => '3', 'cap_gc' => '0',
            'applies_gear_purchase' => '1', 'applies_gear_upgrade' => '1', 'applies_city_fund' => '1', 'period' => 'weekly',
            'reason' => '시범 운영: 장비 거래에만 5%'];

        // 운영자·고객지원은 세금 정책을 고칠 수 없다.
        T::eq(['권한이 없습니다.'], $taxes->savePolicy($this->operator(), 'trade', $form));
        T::eq(['권한이 없습니다.'], $taxes->savePolicy($this->admin('helpdesk', Auth::ROLE_SUPPORT), 'trade', $form));
        // 세율은 20% 를 넘길 수 없다. 사유가 있어야 한다. 켜려면 적용 대상이 있어야 한다.
        T::ok(count($taxes->savePolicy($this->balancer(), 'trade', ['rate_bp' => '2001'] + $form)) > 0);
        T::ok(count($taxes->savePolicy($this->balancer(), 'trade', ['rate_bp' => '-1'] + $form)) > 0);
        T::ok(count($taxes->savePolicy($this->balancer(), 'trade', ['reason' => ' '] + $form)) > 0);
        $none = $form;
        unset($none['applies_gear_purchase'], $none['applies_gear_upgrade']);
        T::ok(count($taxes->savePolicy($this->balancer(), 'trade', $none)) > 0);
        T::eq(0, (int)$this->db->val("SELECT is_active FROM city_tax_policies WHERE tax_type = 'trade'"));

        T::eq([], $taxes->savePolicy($this->balancer(), 'trade', $form, '10.0.0.9'));
        $policy = $taxes->policies()['trade'];
        T::eq(true, $policy['active']);
        T::eq(500, $policy['rateBp']);
        // 기여금(city_fund)은 고를 수 없는 대상이라 들어가지 않는다. 거래세의 징수 시점은 늘 "거래할 때"다.
        T::eq(['gear_purchase', 'gear_upgrade'], $policy['appliesTo']);
        T::eq('transaction', $policy['period']);
        $history = $taxes->policyHistory();
        T::eq(1, count($history));
        T::eq('trade', $history[0]['target_key']);
        T::eq(300, json_decode($history[0]['before_value'], true)['rate_bp']);
        T::eq(500, json_decode($history[0]['after_value'], true)['rate_bp']);
        $audit = $this->db->one("SELECT * FROM admin_audit_logs WHERE action = 'tax.policy.update'");
        T::eq('trade', $audit['target_key']);
        T::eq('10.0.0.9', $audit['ip']);
        T::ok(strpos($audit['detail'], '[켬]') === 0);
        // 바뀐 것이 없으면 이력을 남기지 않는다.
        T::eq([], $taxes->savePolicy($this->balancer(), 'trade', $form));
        T::eq(1, count($taxes->policyHistory()));

        // 재산세는 한 주 또는 한 달에 한 번만.
        T::ok(count($taxes->savePolicy($this->balancer(), 'property', ['period' => 'daily'] + $form)) > 0);
        T::eq([], $taxes->savePolicy($this->balancer(), 'property', ['period' => 'monthly', 'rate_bp' => '100'] + $form));
        T::eq('monthly', $taxes->policies()['property']['period']);

        // 고친 정책이 바로 쓰인다: 레벨 2 부터, 전입 3일 뒤부터, 200 GC 이상의 거래에 5%.
        $player = $this->setLevel($this->resident('11110'), 10);
        $this->give($player, 5000);
        Clock::advance(4 * 86400);
        $this->app->combat()->buy($player, 'gear_precision_rifle', $this->key());
        T::eq(30, (int)$this->taxRows(Taxes::TRADE)[0]['tax_gc']);
    }

    // ── 도시 재정과 예산 집행 ───────────────────────────────

    /** 재산세로 종로구의 재정을 채운다: 일반 회계 18,000 GC, 예비비 2,000 GC. */
    private function fillTreasury()
    {
        $this->enableTax(Taxes::PROPERTY, ['rate_bp' => 2000, 'cap_gc' => 0, 'exempt_amount' => 0]);
        $player = $this->resident('11110');
        $this->give($player, 99500);   // 100,000 GC → 20,000 GC
        T::eq(20000, $this->app->taxes()->collectProperty()['totalGc']);
        T::eq(18000, $this->treasuryGc('11110'));
        T::eq(2000, $this->treasuryGc('11110', 'reserve'));
        $this->enableTax(Taxes::PROPERTY, ['is_active' => 0]);
        return $player;
    }

    public function testTreasuryFundsASharedDefenseStructureAndRecordsThePublicExpenditure()
    {
        $this->quiet();
        $resident = $this->fillTreasury();
        $treasury = $this->app->treasury();
        $form = ['category' => 'defense_facility', 'title' => '에너지 방어막 1단계', 'description' => '세금으로 방어막을 세웁니다.',
            'amount_gc' => '5000', 'target_code' => 'twr_energy_shield', 'reason' => '주민 요청에 따른 집행'];
        $riskBefore = $this->riskCenti('11110');
        $walletBefore = $this->gc($resident);

        // 권한: 밸런스 담당·조회 전용은 집행할 수 없다.
        list($id, $problems) = $treasury->spend($this->balancer(), '11110', $form);
        T::eq(null, $id);
        T::eq(['권한이 없습니다.'], $problems);
        // 값 검사: 없는 분야, 금액, 사유, 대상.
        T::ok(count($treasury->spend($this->operator(), '11110', ['category' => 'party'] + $form)[1]) > 0);
        T::ok(count($treasury->spend($this->operator(), '11110', ['amount_gc' => '0'] + $form)[1]) > 0);
        T::ok(count($treasury->spend($this->operator(), '11110', ['reason' => ''] + $form)[1]) > 0);
        T::ok(count($treasury->spend($this->operator(), '11110', ['target_code' => 'twr_machine_gun'] + $form)[1]) > 0);
        // 잔액보다 많이 쓸 수 없다.
        list($id, $problems) = $treasury->spend($this->operator(), '11110', ['amount_gc' => '18001'] + $form);
        T::eq(null, $id);
        T::ok(strpos($problems[0], '잔액이 모자랍니다') !== false);
        T::eq(18000, $this->treasuryGc('11110'));
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_expenditures'));

        list($id, $problems) = $treasury->spend($this->operator(), '11110', $form, null, '10.0.0.7');
        T::eq([], $problems);
        T::ok($id > 0);
        T::eq(13000, $this->treasuryGc('11110'));
        T::eq(2000, $this->treasuryGc('11110', 'reserve'), '일반 분야는 예비비에서 나가지 않는다');
        // 시설이 한 단계 완성되어 위험도가 내려간다. 누구의 지갑도 기여도도 바뀌지 않는다.
        $def = $this->app->defs()->findStructure('twr_energy_shield');
        T::eq(5000, (int)$this->db->val('SELECT funded_gc FROM city_defense_structures WHERE region_id = ? AND structure_id = ?',
            [$this->regionId('11110'), $def['id']]));
        T::ok($this->riskCenti('11110') < $riskBefore, '공용 방어시설이 완성되면 위험도가 내려간다');
        T::eq($walletBefore, $this->gc($resident));
        T::eq(0, $this->app->city()->contributionOf($resident['id'], $this->regionId('11110')));

        // 집행 내역은 누구나 볼 수 있다. 집행한 관리자의 이름은 나가지 않는다.
        $public = $treasury->expenditures('11110');
        T::eq(1, count($public['expenditures']));
        $item = $public['expenditures'][0];
        T::eq('defense_facility', $item['category']);
        T::eq(5000, $item['amountGc']);
        T::eq(false, $item['byVote']);
        T::ok(strpos($item['effectDetail'], '단계 0 → 1') !== false);
        T::ok(strpos(json_encode($public, JSON_UNESCAPED_UNICODE), 'ops') === false);
        $summary = $treasury->summary('11110');
        T::eq(13000, $summary['generalGc']);
        T::eq(2000, $summary['reserveGc']);
        T::eq(5000, $summary['spent'][0]['totalGc']);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'treasury.spend'"));

        // 남은 목표보다 많이 집행하면 실제로 들어간 만큼만 빠진다 (방어막은 3단계 15,000 GC 까지).
        list($id, $problems) = $treasury->spend($this->operator(), '11110', ['amount_gc' => '12000'] + $form);
        T::eq([], $problems);
        T::eq(3000, $this->treasuryGc('11110'));
        T::eq(10000, (int)$this->db->val('SELECT amount_gc FROM city_budget_expenditures WHERE id = ?', [$id]));
        // 다 지은 시설에는 더 쓸 수 없다.
        list($id, $problems) = $treasury->spend($this->operator(), '11110', ['amount_gc' => '100'] + $form);
        T::eq(null, $id);
        T::eq(['이미 다 지은 시설입니다.'], $problems);
        T::eq(3000, $this->treasuryGc('11110'));
        $this->consistent();
    }

    public function testTreasuryRepairsFacilitiesRecordsOtherCategoriesAndUsesTheReserveSeparately()
    {
        $this->quiet();
        $this->fillTreasury();
        $treasury = $this->app->treasury();
        $facility = $this->app->defs()->findFacility('fac_power');
        $this->app->city()->tick();
        $this->db->exec('INSERT INTO city_facility_damage (region_id, facility_id, damage, updated_at) VALUES (?, ?, 2, ?)',
            [$this->regionId('11110'), $facility['id'], Clock::sql()]);

        // 방어시설 유지보수: 낸 금액으로 고칠 수 있는 단계만큼 고치고, 실제로 든 값만 빠진다.
        $form = ['category' => 'defense_maintenance', 'title' => '발전소 복구', 'amount_gc' => (string)($facility['repairGc'] * 5),
            'target_code' => 'fac_power', 'reason' => '침공 피해 복구'];
        list($id, $problems) = $treasury->spend($this->operator(), '11110', $form);
        T::eq([], $problems);
        T::eq(18000 - $facility['repairGc'] * 2, $this->treasuryGc('11110'));
        T::eq([], $this->app->city()->facilityDamage($this->regionId('11110')));
        T::eq(['고칠 곳이 없습니다.'], $treasury->spend($this->operator(), '11110', $form)[1]);

        // 그 밖의 분야는 집행 내역만 남는다.
        $general = $this->treasuryGc('11110');
        foreach (['infrastructure' => '가로등 정비', 'npc_welfare' => 'NPC 쉼터', 'education' => '경제 교실', 'event' => '가을 축제'] as $category => $title) {
            list($id, $problems) = $treasury->spend($this->operator(), '11110',
                ['category' => $category, 'title' => $title, 'amount_gc' => '1000', 'reason' => '연간 계획에 따른 집행']);
            T::eq([], $problems);
            T::eq(Treasury::EFFECT_RECORD, $this->db->val('SELECT effect_type FROM city_budget_expenditures WHERE id = ?', [$id]));
        }
        T::eq($general - 4000, $this->treasuryGc('11110'));

        // 예비비는 예비비 계정에서만 나간다.
        list($id, $problems) = $treasury->spend($this->operator(), '11110',
            ['category' => 'reserve', 'title' => '긴급 복구 지원', 'amount_gc' => '1500', 'reason' => '대규모 침공 대응']);
        T::eq([], $problems);
        T::eq(500, $this->treasuryGc('11110', 'reserve'));
        T::eq($general - 4000, $this->treasuryGc('11110'));
        list($id, $problems) = $treasury->spend($this->operator(), '11110',
            ['category' => 'reserve', 'title' => '긴급 복구 지원', 'amount_gc' => '501', 'reason' => '대규모 침공 대응']);
        T::eq(null, $id);
        $this->consistent();

        // 도시 재정에서 플레이어의 지갑으로 나간 GC 는 없다.
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM ledger_entries WHERE delta > 0 AND ref_type IN ('expenditure', 'treasury')"));
    }

    public function testTreasuryLedgerChecksDetectTampering()
    {
        $this->fillTreasury();
        $treasury = $this->app->treasury();
        T::eq(null, $treasury->findInconsistency());
        // 원장의 줄 수와 합
        T::eq(2, (int)$this->db->val('SELECT COUNT(*) FROM city_treasury_ledger'));
        T::eq(20000, (int)$this->db->val('SELECT SUM(delta) FROM city_treasury_ledger'));

        // 잔액을 손으로 고치면 드러난다.
        $this->db->exec("UPDATE city_treasury_accounts SET balance = balance + 1 WHERE account = 'general'");
        T::ok(strpos($treasury->findInconsistency(), '잔액') !== false);
        T::ok(count(\CG\Service\CityOpsTick::run($this->app)['problems']) > 0);
        $this->db->exec("UPDATE city_treasury_accounts SET balance = balance - 1 WHERE account = 'general'");
        T::eq(null, $treasury->findInconsistency());

        // 명세 없이 재정에 세금이 들어오면 드러난다.
        $this->db->exec("INSERT INTO city_treasury_ledger (region_id, account, delta, balance_after, reason, created_at) VALUES (?, 'general', 5, 18005, 'tax_trade', ?)",
            [$this->regionId('11110'), Clock::sql()]);
        $this->db->exec("UPDATE city_treasury_accounts SET balance = 18005 WHERE account = 'general'");
        T::ok(strpos($treasury->findInconsistency(), '걷은 세금의 합') !== false);
    }

    public function testCityTreasuryIsSeparateFromRealWorldBenefitTables()
    {
        // 도시 재정의 코드는 CITY PASS·장학 테이블을 읽거나 쓰지 않는다.
        foreach (['Treasury', 'Taxes', 'Budget'] as $class) {
            $source = file_get_contents(__DIR__ . '/../src/Service/' . $class . '.php');
            foreach (['scholarship_', 'benefit_coupons', 'partner_', 'city_pass_', 'coupon_', 'cityPass()', 'scholarships()', 'scholarshipOffice()',
                         'amount_krw'] as $word) {
                T::ok(strpos($source, $word) === false, "$class 가 현실 혜택($word)을 가리킴");
            }
        }
        // 현실 혜택의 코드도 도시 재정을 가리키지 않는다.
        foreach (['CityPass', 'Partners', 'MerchantPortal', 'Scholarships', 'ScholarshipOffice'] as $class) {
            $source = file_get_contents(__DIR__ . '/../src/Service/' . $class . '.php');
            foreach (['city_treasury', 'city_tax', 'city_budget', 'treasury()', 'taxes()'] as $word) {
                T::ok(strpos($source, $word) === false, "$class 가 도시 재정($word)을 가리킴");
            }
        }
    }

    // ── API ─────────────────────────────────────────────────

    public function testTaxAndTreasuryApiIsReadOnly()
    {
        $this->fillTreasury();
        $kernel = new \CG\Api\Kernel($this->app);
        $guest = $this->app->players()->createGuest('windows', '9.0.0');
        $headers = ['Authorization' => 'Bearer ' . $guest['accessToken']];

        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/tax/policies', [], $headers));
        T::eq(200, $status);
        T::eq(3, count($body['data']['policies']));
        T::eq(true, $body['data']['policies'][0]['exempt']);
        T::eq('stranger', $body['data']['policies'][0]['exempt_reason']);
        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/tax/transactions', [], $headers));
        T::eq(200, $status);
        T::eq([], $body['data']['transactions']);
        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/cities/11110/treasury', [], $headers));
        T::eq(200, $status);
        T::eq(18000, $body['data']['general_gc']);
        T::eq(2000, $body['data']['reserve_gc']);
        list($status, $body) = $kernel->handle(\CG\Infra\Request::make('GET', '/cities/11110/treasury/expenditures', [], $headers));
        T::eq(200, $status);
        T::eq([], $body['data']['expenditures']);
        // 세율이나 세액, 재정 잔액을 바꾸는 경로는 없다.
        foreach ([['POST', '/tax/policies'], ['PUT', '/tax/policies'], ['POST', '/tax/transactions'], ['POST', '/cities/11110/treasury'],
                     ['POST', '/cities/11110/treasury/expenditures']] as $route) {
            list($status) = $kernel->handle(\CG\Infra\Request::make($route[0], $route[1], ['rateBp' => 0, 'amountGc' => 1], $headers));
            T::ok($status === 400 || $status === 404, $route[0] . ' ' . $route[1] . " → $status");
        }
    }
}
