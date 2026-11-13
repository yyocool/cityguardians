<?php
use CG\Admin\Auth;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Request;
use CG\Service\Taxes;

/**
 * v9.0 참여예산: 제안 → 검토 → 주민 투표 → 결과 확정 → 집행 → 집행 내역 공개.
 * (지시서 11장의 테스트 항목: 투표 중복 방지)
 */
final class BudgetTest extends CityTestCase
{
    private function form(array $overrides = [])
    {
        return array_merge(['category' => 'education', 'title' => '어린이 경제 교실', 'description' => '세금이 어디에 쓰이는지 배우는 교실을 열어요.',
            'requestedGc' => 3000, 'targetCode' => ''], $overrides);
    }

    /** 제안할 수 있는 주민 (레벨 3). */
    private function proposer($regionCode = '11110')
    {
        return $this->setLevel($this->resident($regionCode), 3);
    }

    /** 투표할 수 있는 주민 (레벨 2). 전입한 지 3일이 지나야 하므로, 만든 뒤에 시계를 돌려야 한다. */
    private function voter($regionCode = '11110')
    {
        return $this->setLevel($this->resident($regionCode), 2);
    }

    private function rowOf($publicId)
    {
        return $this->db->one('SELECT * FROM city_budget_proposals WHERE public_id = ?', [$publicId]);
    }

    /** 제안 하나를 만들어 투표를 연다. 투표할 주민 $voters 명도 함께 만든다. [제안 공개 ID, 제안한 사람, 투표자들] */
    private function openVote($voters = 5, array $form = [])
    {
        $this->enableBudget();
        $proposer = $this->proposer();
        $people = [];
        for ($i = 0; $i < $voters; $i++) {
            $people[] = $this->voter();
        }
        Clock::advance(4 * 86400);
        $proposal = $this->app->budget()->propose($proposer, $this->form($form), $this->key())['proposal'];
        T::eq([], $this->app->budget()->review($this->operator(), $this->rowOf($proposal['id'])['id'], true, '규칙에 맞는 제안'));
        return [$proposal['id'], $proposer, $people];
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

    // ── 제안 ────────────────────────────────────────────────

    public function testBudgetIsOffByDefault()
    {
        $player = $this->proposer();
        T::eq(false, $this->app->budget()->options($player)['enabled']);
        $error = T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($player) {
            $this->app->budget()->propose($player, $this->form(), $this->key());
        });
        T::eq('budget_disabled', $error->reason);
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_proposals'));
    }

    public function testOnlyResidentsCanProposeAndValuesAreChecked()
    {
        $this->enableBudget();
        $budget = $this->app->budget();

        $stranger = $this->guest();
        T::eq('not_resident', T::throwsApi(ApiError::CONFLICT, function () use ($budget, $stranger) {
            $budget->propose($stranger, $this->form(), $this->key());
        })->reason);
        $beginner = $this->resident('11110');
        T::eq('level_too_low', T::throwsApi(ApiError::LOCKED, function () use ($budget, $beginner) {
            $budget->propose($beginner, $this->form(), $this->key());
        })->reason);

        $player = $this->proposer();
        foreach ([
            ['category' => 'reserve'],                     // 예비비는 제안할 수 없다
            ['category' => 'party'],
            ['title' => ''],
            ['title' => str_repeat('가', 61)],
            ['description' => str_repeat('나', 601)],
            ['requestedGc' => 0],
            ['requestedGc' => 50001],                      // 한 제안의 상한
            ['requestedGc' => '3000'],                     // 숫자가 아닌 값
            ['category' => 'defense_facility', 'targetCode' => 'twr_machine_gun'],   // 도시 시설이 아니다
            ['category' => 'defense_maintenance', 'targetCode' => 'fac_nothing'],
        ] as $bad) {
            T::throwsApi(ApiError::INVALID_REQUEST, function () use ($budget, $player, $bad) {
                $budget->propose($player, $this->form($bad), $this->key());
            });
        }
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_proposals'));

        $key = $this->key();
        $created = $budget->propose($player, $this->form(['category' => 'defense_facility', 'targetCode' => 'twr_energy_shield']), $key)['proposal'];
        T::ok(preg_match('/^b_[a-z0-9]{14}$/', $created['id']) === 1);
        T::eq('submitted', $created['status']);
        T::eq('에너지 방어막', $created['targetName']);
        T::eq(true, $created['mine']);
        T::eq(false, $created['canVote']);
        // 같은 요청이 다시 와도 제안은 하나.
        T::eq(true, $budget->propose($player, $this->form(['category' => 'defense_facility', 'targetCode' => 'twr_energy_shield']), $key)['duplicate']);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_proposals'));
        // 진행 중인 제안은 한 사람에 하나.
        T::eq('too_many_proposals', T::throwsApi(ApiError::LIMIT_REACHED, function () use ($budget, $player) {
            $budget->propose($player, $this->form(), $this->key());
        })->reason);
        // 제안해도 GC 는 들지 않는다.
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries WHERE player_id = ? AND delta < 0', [$player['id']]));
    }

    public function testProposalIsHiddenUntilReviewedAndRejectionNoteIsPrivate()
    {
        $this->enableBudget();
        $budget = $this->app->budget();
        $author = $this->proposer();
        $neighbor = $this->voter();
        $proposal = $budget->propose($author, $this->form(), $this->key())['proposal'];
        $id = (int)$this->rowOf($proposal['id'])['id'];

        // 검토 전: 제안한 사람에게만 보인다.
        T::eq(1, count($budget->listFor($author, '11110')['proposals']));
        T::eq(0, count($budget->listFor($neighbor, '11110')['proposals']));
        T::throwsApi(ApiError::NOT_FOUND, function () use ($budget, $neighbor, $proposal) {
            $budget->detail($neighbor, $proposal['id']);
        });
        T::throwsApi(ApiError::NOT_FOUND, function () use ($budget, $neighbor, $proposal) {
            $budget->vote($neighbor, $proposal['id'], 'yes', $this->key());
        });

        // 검토 권한과 사유.
        T::eq(['권한이 없습니다.'], $budget->review($this->balancer(), $id, false, '도시와 관계없는 내용'));
        T::ok(count($budget->review($this->operator(), $id, false, '')) > 0);
        T::eq([], $budget->review($this->operator(), $id, false, '도시와 관계없는 내용', '10.0.0.3'));
        T::eq('rejected', $this->rowOf($proposal['id'])['status']);
        T::eq(['검토 대기 중인 제안만 검토할 수 있습니다.'], $budget->review($this->operator(), $id, true, '다시 검토'));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'budget.reject' AND target_key = ?", [$proposal['id']]));

        // 반려 사유는 제안한 사람에게만 보인다. 다른 주민에게는 제안 자체가 보이지 않는다.
        T::eq('도시와 관계없는 내용', $budget->detail($author, $proposal['id'])['proposal']['reviewNote']);
        T::eq(0, count($budget->listFor($neighbor, '11110')['proposals']));

        // 반려된 뒤에는 새로 제안할 수 있고, 검토 전이면 스스로 거둘 수 있다.
        $second = $budget->propose($author, $this->form(['title' => '가로등 고치기', 'category' => 'infrastructure']), $this->key())['proposal'];
        T::throwsApi(ApiError::NOT_FOUND, function () use ($budget, $neighbor, $second) {
            $budget->withdraw($neighbor, $second['id']);
        });
        T::eq('withdrawn', $budget->withdraw($author, $second['id'])['proposal']['status']);
        T::eq('not_withdrawable', T::throwsApi(ApiError::CONFLICT, function () use ($budget, $author, $second) {
            $budget->withdraw($author, $second['id']);
        })->reason);
    }

    // ── 투표 ────────────────────────────────────────────────

    public function testOneAccountVotesOnceAndCannotChangeTheVote()
    {
        list($id, $proposer, $voters) = $this->openVote(2);
        $budget = $this->app->budget();
        $voter = $voters[0];

        $before = $budget->detail($voter, $id)['proposal'];
        T::eq('voting', $before['status']);
        T::eq(true, $before['canVote']);
        T::eq(5, $before['quorum']);

        $key = $this->key();
        $voted = $budget->vote($voter, $id, 'yes', $key)['proposal'];
        T::eq(1, $voted['yesCount']);
        T::eq('yes', $voted['myVote']);
        T::eq(false, $voted['canVote']);
        T::eq('already_voted', $voted['voteBlockReason']);

        // 같은 요청의 재전송: 표는 그대로 하나.
        T::eq(true, $budget->vote($voter, $id, 'yes', $key)['duplicate']);
        // 새 요청으로 다시, 또는 반대로 바꾸려 해도 안 된다.
        foreach (['yes', 'no'] as $choice) {
            T::eq('already_voted', T::throwsApi(ApiError::CONFLICT, function () use ($budget, $voter, $id, $choice) {
                $budget->vote($voter, $id, $choice, $this->key());
            })->reason);
        }
        T::throwsApi(ApiError::INVALID_REQUEST, function () use ($budget, $voters, $id) {
            $budget->vote($voters[1], $id, 'maybe', $this->key());
        });
        $row = $this->rowOf($id);
        T::eq(1, (int)$row['yes_count']);
        T::eq(0, (int)$row['no_count']);
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_votes WHERE proposal_id = ?', [$row['id']]));

        // 마지막 방어선: 같은 계정의 두 번째 표는 DB 가 받지 않는다.
        T::eq(0, $this->db->exec("INSERT IGNORE INTO city_budget_votes (proposal_id, player_id, choice, created_at) VALUES (?, ?, 'no', ?)",
            [$row['id'], $voter['id'], Clock::sql()]));

        // 제안한 사람도 한 표.
        T::eq(2, $budget->vote($proposer, $id, 'yes', $this->key())['proposal']['yesCount']);
    }

    public function testOnlyEligibleResidentsOfThatCityCanVote()
    {
        list($id, , $voters) = $this->openVote(1);
        $budget = $this->app->budget();
        $reason = function (array $player, $code) use ($budget, $id) {
            return T::throwsApi($code, function () use ($budget, $player, $id) {
                $budget->vote($player, $id, 'yes', $this->key());
            })->reason;
        };

        T::eq('not_resident_here', $reason($this->guest(), ApiError::CONFLICT));                 // 이방인
        $other = $this->setLevel($this->resident('26110'), 5);
        $newcomer = $this->setLevel($this->resident('11110'), 5);
        $lowLevel = $this->resident('11110');
        Clock::advance(86400);
        T::eq('resident_too_new', $reason($newcomer, ApiError::LOCKED));                         // 전입한 지 3일이 안 됨
        Clock::advance(3 * 86400);
        T::eq('not_resident_here', $reason($other, ApiError::CONFLICT));                         // 다른 도시의 주민
        T::eq('level_too_low', $reason($lowLevel, ApiError::LOCKED));                            // 레벨 1
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_votes'));

        // 조건을 채우면 투표할 수 있다.
        T::eq(1, $budget->vote($newcomer, $id, 'no', $this->key())['proposal']['noCount']);

        // 투표 기간이 끝나면 받지 않는다 (투표는 승인한 때부터 7일).
        Clock::advance(4 * 86400);
        T::eq('not_voting', $reason($voters[0], ApiError::CONFLICT));

        // 참여예산을 끄면 투표도 멈춘다.
        $this->enableBudget(['enabled' => 0]);
        T::eq('budget_disabled', T::throwsApi(ApiError::FEATURE_DISABLED, function () use ($budget, $voters, $id) {
            $budget->vote($voters[0], $id, 'yes', $this->key());
        })->reason);
    }

    public function testEveryVoteWeighsTheSameWhateverTheGcTaxOrLevel()
    {
        list($id, , $voters) = $this->openVote(5);
        $budget = $this->app->budget();
        // 부자이고, 세금을 많이 냈고, 레벨이 높은 한 사람이 반대한다.
        $rich = $voters[0];
        $this->give($rich, 5000000);
        $this->setLevel($rich, 50);
        $this->enableTax(Taxes::PROPERTY, ['rate_bp' => 2000, 'cap_gc' => 0, 'exempt_amount' => 0]);
        T::ok($this->app->taxes()->collectProperty()['totalGc'] >= 1000000);
        $this->enableTax(Taxes::PROPERTY, ['is_active' => 0]);

        T::eq(1, $budget->vote($rich, $id, 'no', $this->key())['proposal']['noCount']);
        // 가진 것이 거의 없는 네 사람이 찬성한다.
        for ($i = 1; $i <= 4; $i++) {
            $budget->vote($voters[$i], $id, 'yes', $this->key());
        }
        $row = $this->rowOf($id);
        T::eq(4, (int)$row['yes_count']);
        T::eq(1, (int)$row['no_count']);
        // 표에는 무게를 담는 열이 없다.
        $columns = array_column($this->db->all(
            "SELECT column_name AS name FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'city_budget_votes'"), 'name');
        sort($columns);
        T::eq(['choice', 'created_at', 'id', 'player_id', 'proposal_id'], $columns);

        Clock::advance(7 * 86400);
        T::eq(1, $budget->closeDue());
        T::eq('passed', $this->rowOf($id)['status']);
    }

    // ── 결과 확정 ───────────────────────────────────────────

    public function testResultNeedsMoreYesThanNoAndTheQuorum()
    {
        // 찬성이 많아도 최소 투표 수(5)에 못 미치면 부결.
        list($id, , $voters) = $this->openVote(4);
        $budget = $this->app->budget();
        foreach ($voters as $voter) {
            $budget->vote($voter, $id, 'yes', $this->key());
        }
        // 기간이 끝나기 전에는 확정하지 않는다.
        T::eq(0, $budget->closeDue());
        T::eq('voting', $this->rowOf($id)['status']);

        // 집계 열을 손으로 고쳐도 결과는 실제 표를 세어 정한다.
        $this->db->exec('UPDATE city_budget_proposals SET yes_count = 99 WHERE public_id = ?', [$id]);
        Clock::advance(7 * 86400);
        T::eq(1, $budget->closeDue());
        $row = $this->rowOf($id);
        T::eq('failed', $row['status']);
        T::eq(4, (int)$row['yes_count']);
        T::ok($row['decided_at'] !== null);
        // 여러 번 불러도 같다.
        T::eq(0, $budget->closeDue());
        // 끝난 제안은 공개된 채로 남는다.
        T::eq('failed', $budget->detail($voters[0], $id)['proposal']['status']);
        // 부결된 제안은 집행할 수 없다.
        T::eq(['가결된 제안만 집행할 수 있습니다.'], $budget->execute($this->operator(), $row['id'], '집행 시도'));
    }

    public function testATieFailsAndTheScheduledJobClosesVotes()
    {
        list($id, $proposer, $voters) = $this->openVote(5);
        $budget = $this->app->budget();
        foreach ([0, 1, 2] as $i) {
            $budget->vote($voters[$i], $id, 'yes', $this->key());
        }
        foreach ([3, 4] as $i) {
            $budget->vote($voters[$i], $id, 'no', $this->key());
        }
        $budget->vote($proposer, $id, 'no', $this->key());   // 3 : 3
        Clock::advance(7 * 86400);
        T::eq(1, \CG\Service\CityOpsTick::run($this->app)['budgetClosed']);
        T::eq('failed', $this->rowOf($id)['status']);
    }

    // ── 집행과 공개 ─────────────────────────────────────────

    public function testPassedProposalIsExecutedExactlyOnceAndPublished()
    {
        $this->quiet();
        $this->fillTreasury();
        list($id, $proposer, $voters) = $this->openVote(5, ['category' => 'defense_facility', 'targetCode' => 'twr_energy_shield', 'requestedGc' => 5000]);
        $budget = $this->app->budget();
        foreach ($voters as $voter) {
            $budget->vote($voter, $id, 'yes', $this->key());
        }
        $rowId = (int)$this->rowOf($id)['id'];
        // 투표가 끝나기 전에는 집행할 수 없다.
        T::eq(['가결된 제안만 집행할 수 있습니다.'], $budget->execute($this->operator(), $rowId, '주민 투표 결과 집행'));
        Clock::advance(7 * 86400);

        // 권한과 사유.
        T::eq(['권한이 없습니다.'], $budget->execute($this->admin('helpdesk', Auth::ROLE_SUPPORT), $rowId, '주민 투표 결과 집행'));
        T::ok(count($budget->execute($this->operator(), $rowId, '')) > 0);
        T::eq('passed', $this->rowOf($id)['status'], '집행에 실패하면 가결 상태로 남는다');
        T::eq(18000, $this->treasuryGc('11110'));

        $walletBefore = $this->gc($proposer);
        T::eq([], $budget->execute($this->operator(), $rowId, '주민 투표 결과 집행', '10.0.0.5'));   // 기간이 끝났으므로 여기서 가결로 확정된다
        $row = $this->rowOf($id);
        T::eq('executed', $row['status']);
        T::ok($row['expenditure_id'] !== null);
        T::eq(13000, $this->treasuryGc('11110'));
        // 한 번만 집행된다.
        T::eq(['가결된 제안만 집행할 수 있습니다.'], $budget->execute($this->operator(), $rowId, '주민 투표 결과 집행'));
        T::eq(13000, $this->treasuryGc('11110'));
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_expenditures'));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'budget.execute'"));
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'treasury.spend'"));
        // 제안한 사람에게 GC 가 가지 않는다.
        T::eq($walletBefore, $this->gc($proposer));

        // 집행 내역은 누구나 본다: 주민 투표로 집행된 것임이 표시된다.
        $public = $this->app->treasury()->expenditures('11110')['expenditures'];
        T::eq(1, count($public));
        T::eq(true, $public[0]['byVote']);
        T::eq($id, $public[0]['proposalId']);
        T::eq(5000, $public[0]['amountGc']);
        $detail = $budget->detail($this->guest(), $id)['proposal'];
        T::eq('executed', $detail['status']);
        T::eq(5000, $detail['expenditure']['amountGc']);
        T::eq(null, $this->app->treasury()->findInconsistency());
        // 집행된 제안은 취소할 수 없다.
        T::ok(count($budget->cancel($this->operator(), $rowId, '뒤늦은 취소')) > 0);
    }

    public function testExecutionFailsCleanlyWhenTheTreasuryIsShort()
    {
        // 재정이 비어 있다.
        list($id, , $voters) = $this->openVote(5);
        $budget = $this->app->budget();
        foreach ($voters as $voter) {
            $budget->vote($voter, $id, 'yes', $this->key());
        }
        Clock::advance(7 * 86400);
        $rowId = (int)$this->rowOf($id)['id'];
        $problems = $budget->execute($this->operator(), $rowId, '주민 투표 결과 집행');
        T::ok(count($problems) === 1 && strpos($problems[0], '잔액이 모자랍니다') !== false);
        T::eq('passed', $this->rowOf($id)['status']);
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_budget_expenditures'));
        T::eq(0, (int)$this->db->val('SELECT COUNT(*) FROM city_treasury_ledger'));
        T::eq(0, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action IN ('budget.execute', 'treasury.spend')"));

        // 재정이 채워지면 집행된다.
        $this->fillTreasury();
        T::eq([], $budget->execute($this->operator(), $rowId, '주민 투표 결과 집행'));
        T::eq(15000, $this->treasuryGc('11110'));
        T::eq('record', $this->db->val('SELECT effect_type FROM city_budget_expenditures'));
    }

    public function testAdminCanCancelAVoteInProgress()
    {
        list($id, , $voters) = $this->openVote(1);
        $budget = $this->app->budget();
        $rowId = (int)$this->rowOf($id)['id'];
        T::eq(['권한이 없습니다.'], $budget->cancel($this->balancer(), $rowId, '규칙을 어긴 내용이 뒤늦게 확인됨'));
        T::eq([], $budget->cancel($this->operator(), $rowId, '규칙을 어긴 내용이 뒤늦게 확인됨'));
        T::eq('cancelled', $this->rowOf($id)['status']);
        // 취소된 제안은 다른 주민에게 보이지 않고, 투표도 받지 않는다.
        T::throwsApi(ApiError::NOT_FOUND, function () use ($budget, $voters, $id) {
            $budget->vote($voters[0], $id, 'yes', $this->key());
        });
        T::eq(1, $budget->adminCounts()['cancelled']);
        T::eq(1, (int)$this->db->val("SELECT COUNT(*) FROM admin_audit_logs WHERE action = 'budget.cancel'"));
    }

    // ── API ─────────────────────────────────────────────────

    public function testBudgetApiFlow()
    {
        // 접속 토큰이 살아 있는 동안 끝나도록, 전입 뒤 기다리는 날수를 없앤다.
        $this->enableBudget(['voterMinResidentDays' => 0]);
        $kernel = new \CG\Api\Kernel($this->app);
        $session = function () {
            $guest = $this->app->players()->createGuest('windows', '9.0.0');
            $player = $this->app->players()->authenticate($guest['accessToken']);
            return [$player, ['Authorization' => 'Bearer ' . $guest['accessToken']]];
        };
        list($author, $authorHeaders) = $session();
        $this->app->players()->registerResidency($author, '11110', '제안자');
        $this->setLevel($author, 3);
        list($voter, $voterHeaders) = $session();
        $this->app->players()->registerResidency($voter, '11110', '투표자');
        $this->setLevel($voter, 2);

        list($status, $body) = $kernel->handle(Request::make('GET', '/cities/budget/options', [], $authorHeaders));
        T::eq(200, $status);
        T::eq(true, $body['data']['enabled']);
        T::eq(6, count($body['data']['categories']));

        // Idempotency-Key 가 없으면 받지 않는다.
        list($status) = $kernel->handle(Request::make('POST', '/cities/budget/proposals',
            ['category' => 'event', 'title' => '가을 축제', 'requested_gc' => 2000], $authorHeaders));
        T::eq(400, $status);
        list($status, $body) = $kernel->handle(Request::make('POST', '/cities/budget/proposals',
            ['category' => 'event', 'title' => '가을 축제', 'description' => '', 'requested_gc' => 2000],
            $authorHeaders + ['Idempotency-Key' => 'budget-api-0001']));
        T::eq(200, $status);
        $id = $body['data']['proposal']['id'];
        T::eq(2000, $body['data']['proposal']['requested_gc']);

        T::eq([], $this->app->budget()->review($this->operator(), $this->rowOf($id)['id'], true, '규칙에 맞는 제안'));
        list($status, $body) = $kernel->handle(Request::make('GET', '/cities/11110/budget/proposals', [], $voterHeaders));
        T::eq(200, $status);
        T::eq(1, count($body['data']['proposals']));
        T::eq(true, $body['data']['proposals'][0]['can_vote']);

        // 표의 수나 무게를 함께 보내도 쓰이지 않는다.
        list($status, $body) = $kernel->handle(Request::make('POST', "/cities/budget/proposals/$id/vote",
            ['choice' => 'yes', 'weight' => 100, 'count' => 100, 'yes_count' => 100], $voterHeaders + ['Idempotency-Key' => 'budget-api-0002']));
        T::eq(200, $status);
        T::eq(1, $body['data']['proposal']['yes_count']);
        list($status, $body) = $kernel->handle(Request::make('POST', "/cities/budget/proposals/$id/vote",
            ['choice' => 'yes'], $voterHeaders + ['Idempotency-Key' => 'budget-api-0003']));
        T::eq(409, $status);
        T::eq('already_voted', $body['error']['reason']);
        list($status, $body) = $kernel->handle(Request::make('GET', "/cities/budget/proposals/$id", [], $voterHeaders));
        T::eq(200, $status);
        T::eq('yes', $body['data']['proposal']['my_vote']);
        // 로그인하지 않으면 볼 수 없다.
        list($status) = $kernel->handle(Request::make('GET', "/cities/budget/proposals/$id"));
        T::eq(401, $status);
    }
}
