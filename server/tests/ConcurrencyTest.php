<?php
use CG\Infra\Clock;
use CG\Service\Taxes;

/**
 * 같은 순간에 오는 요청들 (v9.0).
 *
 * 요청마다 PHP 프로세스를 따로 띄워 (DB 연결이 서로 다르다) 같은 시각에 일을 시킨다. 한 프로세스 안에서 차례로 부르는
 * 다른 테스트로는 잡히지 않는 것 — 잠금을 기다리는 동안 다른 요청이 끝낸 일을 못 보고 덮어쓰는 것 — 을 여기서 확인한다.
 * (지시서 11장: 공동 예산·세금의 중복 차감/징수 방지, 도시 재정 원장 일치, 투표 중복 방지, 전입자 중복 집계 방지)
 */
final class ConcurrencyTest extends CityTestCase
{
    public function setUp()
    {
        parent::setUp();
        $this->quiet();
    }

    private function consistent()
    {
        T::eq(null, $this->app->city()->findInconsistency());
        T::eq(null, $this->app->treasury()->findInconsistency());
        T::eq(null, $this->app->growth()->findInconsistency());
        T::eq(null, $this->app->coop()->findInconsistency());
    }

    private static function allOk(array $results)
    {
        foreach ($results as $index => $result) {
            T::ok($result['ok'] === true, "요청 $index: " . json_encode($result, JSON_UNESCAPED_UNICODE));
        }
    }

    /** 재산세로 종로구의 재정을 채운다 (일반 회계 18,000 GC, 예비비 2,000 GC). */
    private function fillTreasury()
    {
        $this->enableTax(Taxes::PROPERTY, ['rate_bp' => 2000, 'cap_gc' => 0, 'exempt_amount' => 0]);
        $payer = $this->resident('11110');
        $this->give($payer, 99500);
        T::eq(20000, $this->app->taxes()->collectProperty()['totalGc']);
        $this->enableTax(Taxes::PROPERTY, ['is_active' => 0]);
    }

    // ── 도시 기록 (v8.0 의 것도 함께 지킨다) ────────────────

    public function testSimultaneousFundingByManyResidentsLosesNothing()
    {
        $this->app->city()->tick();
        $jobs = [];
        $players = [];
        for ($i = 0; $i < 8; $i++) {
            $player = $this->resident('11110');
            $this->give($player, 1000);
            $players[] = $player;
            $jobs[] = ['job' => 'city.fund', 'playerId' => $player['id'], 'structure' => 'twr_energy_shield', 'amount' => 500,
                'key' => 'parallel-fund-' . $i . '-0001'];
        }
        $before = array_map([$this, 'gc'], $players);
        $results = $this->parallel($jobs);
        self::allOk($results);
        // 여덟 명이 낸 4,000 GC 가 모두 시설 기금에 쌓여야 한다. 낸 사람의 지갑에서만 빠지고 기금에서 사라지면 안 된다.
        $def = $this->app->defs()->findStructure('twr_energy_shield');
        T::eq(4000, (int)$this->db->val('SELECT funded_gc FROM city_defense_structures WHERE region_id = ? AND structure_id = ?',
            [$this->regionId('11110'), $def['id']]));
        foreach ($players as $index => $player) {
            T::eq($before[$index] - 500, $this->gc($player));
        }
        T::eq(4000, -(int)$this->db->val("SELECT COALESCE(SUM(delta), 0) FROM ledger_entries WHERE reason = 'city_fund'"));
        $this->consistent();
    }

    public function testSimultaneousBattleWinsKeepTheCityRecordStraight()
    {
        $this->app->city()->tick();
        $jobs = [];
        for ($i = 0; $i < 6; $i++) {
            $player = $this->resident('11110');
            $jobs[] = ['job' => 'defense.win', 'playerId' => $player['id']];
        }
        $results = $this->parallel($jobs);
        self::allOk($results);
        // 여섯 번의 승리가 모두 도시 기록에 남는다 (방어 포인트 100 × 6).
        T::eq(600, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        T::eq(6, (int)$this->db->val('SELECT player_wins FROM city_risk_status WHERE region_id = ?', [$this->regionId('11110')]));
        $this->consistent();
    }

    // ── 전입자 ──────────────────────────────────────────────

    public function testSimultaneousRegistrationsAreCountedExactly()
    {
        $jobs = [];
        for ($i = 0; $i < 8; $i++) {
            $player = $this->guest();
            $this->link($player);
            $jobs[] = ['job' => 'residency.register', 'playerId' => $player['id'], 'region' => '11110', 'nickname' => '동시전입' . $i];
        }
        // 그 가운데 한 계정은 같은 순간에 두 도시에 전입신고를 낸다.
        $jobs[] = ['job' => 'residency.register', 'playerId' => $jobs[0]['playerId'], 'region' => '26110', 'nickname' => '두도시'];
        $results = $this->parallel($jobs);
        T::eq(8, self::succeeded($results), json_encode($results, JSON_UNESCAPED_UNICODE));

        // 한 계정은 한 도시에만 산다.
        $both = $this->db->one('SELECT r.code FROM players p JOIN regions r ON r.id = p.region_id WHERE p.id = ?', [$jobs[0]['playerId']]);
        $inBusan = $both['code'] === '26110' ? 1 : 0;
        T::eq(8 - $inBusan, (int)$this->stats('11110')['valid_residents']);
        T::eq($inBusan, (int)$this->db->val('SELECT COALESCE(SUM(valid_residents), 0) FROM city_population_stats WHERE region_id = ?',
            [$this->regionId('26110')]));
        T::eq(8, (int)$this->db->val('SELECT SUM(resident_count) FROM regions'));
        $this->consistent();
    }

    public function testSimultaneousMovesByOneAccountHappenOnce()
    {
        $player = $this->linkedResident('11110');
        $this->give($player, 5000);
        $before = $this->gc($player);
        Clock::advance(8 * 86400);
        $jobs = [];
        foreach (['26110', '27110', '26110', '28110'] as $index => $region) {
            $jobs[] = ['job' => 'residency.move', 'playerId' => $player['id'], 'region' => $region, 'key' => 'parallel-move-' . $index . '-0001'];
        }
        $results = $this->parallel($jobs);
        T::eq(1, self::succeeded($results), json_encode($results, JSON_UNESCAPED_UNICODE));
        // 이사 비용은 한 번만 나가고, 전입자는 어느 도시에서든 한 명으로만 세어진다.
        T::eq($before - 1000, $this->gc($player));
        T::eq(0, (int)$this->stats('11110')['valid_residents']);
        T::eq(1, (int)$this->db->val('SELECT SUM(valid_residents) FROM city_population_stats'));
        T::eq(1, (int)$this->db->val('SELECT SUM(resident_count) FROM regions'));
        $this->consistent();
    }

    // ── 세금과 도시 재정 ────────────────────────────────────

    public function testSimultaneousPropertyTaxRunsCollectOncePerResident()
    {
        $this->enableTax(Taxes::PROPERTY, ['exempt_amount' => 0, 'cap_gc' => 0, 'rate_bp' => 1000]);
        $players = [];
        for ($i = 0; $i < 5; $i++) {
            $player = $this->resident($i < 3 ? '11110' : '26110');
            $this->give($player, 9500);    // 10,000 GC → 1,000 GC
            $players[] = $player;
        }
        // 예약 작업이 겹쳐서 네 번 돈다.
        $results = $this->parallel(array_fill(0, 4, ['job' => 'tax.property']));
        self::allOk($results);
        $collected = 0;
        foreach ($results as $result) {
            $collected += $result['data']['collected'];
        }
        T::eq(5, $collected);
        T::eq(5, (int)$this->db->val("SELECT COUNT(*) FROM city_tax_transactions WHERE tax_type = 'property'"));
        foreach ($players as $player) {
            T::eq(9000, $this->gc($player));
            T::eq(null, $this->app->wallets()->findInconsistency($player['id']));
        }
        T::eq(3000, $this->treasuryGc('11110') + $this->treasuryGc('11110', 'reserve'));
        T::eq(2000, $this->treasuryGc('26110') + $this->treasuryGc('26110', 'reserve'));
        $this->consistent();
    }

    public function testSimultaneousTradeTaxesFromManyBuyersAllReachTheTreasury()
    {
        $this->enableTax(Taxes::TRADE);
        $jobs = [];
        for ($i = 0; $i < 8; $i++) {
            $player = $this->setLevel($this->resident('11110'), 10);
            $this->give($player, 1000);
            $jobs[] = ['job' => 'gear.buy', 'playerId' => $player['id'], 'gear' => 'gear_precision_rifle', 'key' => 'parallel-buy-' . $i . '-0001'];
        }
        $results = $this->parallel($jobs);
        self::allOk($results);
        // 600 GC 의 3% = 18 GC 가 여덟 번: 144 GC (일반 회계 17 × 8, 예비비 1 × 8).
        T::eq(8, (int)$this->db->val('SELECT COUNT(*) FROM city_tax_transactions'));
        T::eq(136, $this->treasuryGc('11110'));
        T::eq(8, $this->treasuryGc('11110', 'reserve'));
        $this->consistent();
    }

    public function testSimultaneousSpendingNeverOverdrawsTheTreasury()
    {
        $this->fillTreasury();
        $operator = $this->operator();
        $jobs = [];
        for ($i = 0; $i < 5; $i++) {
            $jobs[] = ['job' => 'treasury.spend', 'admin' => $operator, 'region' => '11110', 'form' => [
                'category' => 'event', 'title' => '도시 행사 ' . $i, 'amount_gc' => '5000', 'reason' => '동시에 들어온 집행 요청']];
        }
        $results = $this->parallel($jobs);
        self::allOk($results);
        $done = count(array_filter($results, function ($result) {
            return $result['data']['id'] !== null;
        }));
        // 18,000 GC 로는 5,000 GC 짜리를 세 번만 집행할 수 있다.
        T::eq(3, $done);
        T::eq(3000, $this->treasuryGc('11110'));
        T::eq(3, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_expenditures'));
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_treasury_ledger WHERE balance_after < 0'));
        $this->consistent();
    }

    // ── 참여예산 ────────────────────────────────────────────

    /** 투표 중인 제안 하나와 투표할 수 있는 주민들. [제안 공개 ID, 제안 번호, 주민들] */
    private function openVote($voters, array $form = [])
    {
        $this->enableBudget();
        $proposer = $this->setLevel($this->resident('11110'), 3);
        $people = [];
        for ($i = 0; $i < $voters; $i++) {
            $people[] = $this->setLevel($this->resident('11110'), 2);
        }
        Clock::advance(4 * 86400);
        $proposal = $this->app->budget()->propose($proposer, array_merge(['category' => 'event', 'title' => '가을 축제', 'description' => '',
            'requestedGc' => 5000, 'targetCode' => ''], $form), $this->key())['proposal'];
        $rowId = (int)$this->db->val('SELECT id FROM city_budget_proposals WHERE public_id = ?', [$proposal['id']]);
        T::eq([], $this->app->budget()->review($this->operator(), $rowId, true, '규칙에 맞는 제안'));
        return [$proposal['id'], $rowId, $people];
    }

    public function testSimultaneousVotesCountOncePerAccount()
    {
        list($id, $rowId, $voters) = $this->openVote(6);
        $jobs = [];
        // 한 계정이 같은 순간에 네 번 (찬성 둘, 반대 둘), 나머지 다섯 계정이 한 번씩.
        foreach (['yes', 'no', 'yes', 'no'] as $index => $choice) {
            $jobs[] = ['job' => 'budget.vote', 'playerId' => $voters[0]['id'], 'proposal' => $id, 'choice' => $choice,
                'key' => 'parallel-vote-a' . $index . '-0001'];
        }
        for ($i = 1; $i < 6; $i++) {
            $jobs[] = ['job' => 'budget.vote', 'playerId' => $voters[$i]['id'], 'proposal' => $id, 'choice' => 'yes',
                'key' => 'parallel-vote-b' . $i . '-0001'];
        }
        $results = $this->parallel($jobs);
        T::eq(6, self::succeeded($results), json_encode($results, JSON_UNESCAPED_UNICODE));
        T::eq(1, self::succeeded(array_slice($results, 0, 4)));
        foreach (array_slice($results, 0, 4) as $result) {
            T::ok($result['ok'] || $result['reason'] === 'already_voted');
        }
        T::eq(6, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ?', [$rowId]));
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ? AND player_id = ?', [$rowId, $voters[0]['id']]));
        // 집계 열이 실제 표의 수와 같다.
        $row = $this->db->one('SELECT yes_count, no_count FROM city_budget_proposals WHERE id = ?', [$rowId]);
        T::eq((int)$this->db->val("SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ? AND choice = 'yes'", [$rowId]), (int)$row['yes_count']);
        T::eq((int)$this->db->val("SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ? AND choice = 'no'", [$rowId]), (int)$row['no_count']);
    }

    public function testSimultaneousExecutionsOfOneProposalSpendOnce()
    {
        $this->fillTreasury();
        list($id, $rowId, $voters) = $this->openVote(5);
        foreach ($voters as $voter) {
            $this->app->budget()->vote($voter, $id, 'yes', $this->key());
        }
        Clock::advance(7 * 86400);
        $operator = $this->operator();
        $results = $this->parallel(array_fill(0, 4, ['job' => 'budget.execute', 'admin' => $operator, 'id' => $rowId, 'reason' => '주민 투표 결과 집행']));
        self::allOk($results);
        $done = count(array_filter($results, function ($result) {
            return $result['data']['problems'] === [];
        }));
        T::eq(1, $done, json_encode($results, JSON_UNESCAPED_UNICODE));
        T::eq(13000, $this->treasuryGc('11110'));
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_expenditures'));
        T::eq('executed', $this->db->val('SELECT status FROM city_budget_proposals WHERE id = ?', [$rowId]));
        $this->consistent();
    }
}
