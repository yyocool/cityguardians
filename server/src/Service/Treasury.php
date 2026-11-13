<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;
use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 도시 재정 (docs/CITY_TREASURY_SYSTEM.md).
 *
 * 도시마다 두 계정이 있다: 일반 회계(general)와 예비비(reserve). 들어오는 돈은 가상 지방세뿐이고, 나가는 돈은 예산 집행뿐이다.
 * 계정의 잔액이 바뀌는 곳은 이 클래스의 post() 하나다: 계정 행을 잠그고, 잔액을 확인하고, 바꾸고, 원장에 한 줄을 남긴다.
 *
 * - 도시 재정에서 플레이어의 지갑으로 GC 가 나가는 길은 없다. 집행은 도시의 시설에만 쓰인다.
 * - v7.0 의 CITY PASS·장학과는 서로 부르지 않는다. 이 클래스는 그쪽 테이블을 읽지도 쓰지도 않는다.
 * - 주민이 스스로 낸 방어 기여금은 여기로 들어오지 않는다 (CityService::fundStructure 가 시설 기금에 바로 쌓는다).
 */
final class Treasury
{
    const GENERAL = 'general';
    const RESERVE = 'reserve';

    const EFFECT_FUND = 'structure_fund';
    const EFFECT_REPAIR = 'facility_repair';
    const EFFECT_RECORD = 'record';

    /** @var App */
    private $app;
    /** @var \CG\Infra\Db */
    private $db;

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->db = $app->db;
    }

    public static function accounts()
    {
        return [self::GENERAL => '일반 회계', self::RESERVE => '예비비'];
    }

    /** 예산의 사용처. 참여예산 제안은 reserve 를 뺀 여섯 분야에서 고른다. */
    public static function categories()
    {
        return [
            'defense_facility' => '공동 방어시설', 'defense_maintenance' => '방어시설 유지보수', 'infrastructure' => '도시 인프라',
            'npc_welfare' => 'NPC 복지시설', 'education' => '게임 내 교육시설', 'event' => '도시 행사', 'reserve' => '예비비',
        ];
    }

    /** 분야마다 집행이 게임에 미치는 효과. record 는 지금은 집행 내역만 남는다 (콘텐츠가 붙으면 효과가 생긴다). */
    public static function effectOf($category)
    {
        if ($category === 'defense_facility') {
            return self::EFFECT_FUND;
        }
        if ($category === 'defense_maintenance') {
            return self::EFFECT_REPAIR;
        }
        return self::EFFECT_RECORD;
    }

    public static function reasonName($reason)
    {
        $names = ['tax_trade' => '거래세', 'tax_business' => '사업세', 'tax_property' => '재산세', 'expenditure' => '예산 집행'];
        return isset($names[$reason]) ? $names[$reason] : $reason;
    }

    // ── 계정과 원장 ─────────────────────────────────────────

    /** 계정의 잔액을 바꾸고 원장에 남긴다. 거래 안에서만 부른다. 모자라면 오류가 나고 아무것도 바뀌지 않는다. */
    private function post($regionId, $account, $delta, $reason, $refType, $refId)
    {
        if (!$this->db->inTransaction()) {
            throw new \LogicException('도시 재정은 거래 안에서만 바꿀 수 있습니다.');
        }
        $delta = (int)$delta;
        $now = Clock::sql();
        // 먼저 잠근다. 계정이 아직 없을 때에만 만든다 (있는 행에 INSERT IGNORE 를 하면 공유 잠금이 걸려, 동시에 온 요청끼리 서로를 기다린다).
        $select = 'SELECT balance FROM city_treasury_accounts WHERE region_id = ? AND account = ? FOR UPDATE';
        $balance = $this->db->val($select, [$regionId, $account]);
        if ($balance === null) {
            $this->db->exec('INSERT IGNORE INTO city_treasury_accounts (region_id, account, balance, updated_at) VALUES (?, ?, 0, ?)',
                [$regionId, $account, $now]);
            $balance = $this->db->val($select, [$regionId, $account]);
        }
        $balance = (int)$balance;
        if ($delta === 0) {
            return $balance;
        }
        $after = $balance + $delta;
        if ($after < 0) {
            throw new ApiError(ApiError::CONFLICT, '도시 재정의 잔액이 모자랍니다.', 'insufficient_treasury');
        }
        $this->db->exec(
            'UPDATE city_treasury_accounts SET balance = ?, total_in = total_in + ?, total_out = total_out + ?, updated_at = ?'
            . ' WHERE region_id = ? AND account = ?',
            [$after, max(0, $delta), max(0, -$delta), $now, $regionId, $account]
        );
        $this->db->exec(
            'INSERT INTO city_treasury_ledger (region_id, account, delta, balance_after, reason, ref_type, ref_id, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)',
            [$regionId, $account, $delta, $after, $reason, $refType, $refId, $now]
        );
        return $after;
    }

    /** 걷은 세금을 도시 재정에 넣는다. 정해진 비율만큼은 예비비로 떼어 둔다. Taxes 가 부른다 (같은 거래 안에서). */
    public function creditTax($regionId, $amount, $taxType, $taxTransactionId)
    {
        $amount = (int)$amount;
        if ($amount <= 0) {
            return;
        }
        $reserve = intdiv($amount * $this->app->settings()->treasury()['reservePercent'], 100);
        $reason = 'tax_' . $taxType;
        // 늘 같은 순서(예비비 → 일반 회계)로 잠근다.
        if ($reserve > 0) {
            $this->post($regionId, self::RESERVE, $reserve, $reason, 'tax_transaction', $taxTransactionId);
        }
        if ($amount - $reserve > 0) {
            $this->post($regionId, self::GENERAL, $amount - $reserve, $reason, 'tax_transaction', $taxTransactionId);
        }
    }

    public function balances($regionId)
    {
        $out = [self::GENERAL => 0, self::RESERVE => 0];
        foreach ($this->db->all('SELECT account, balance FROM city_treasury_accounts WHERE region_id = ?', [(int)$regionId]) as $row) {
            $out[$row['account']] = (int)$row['balance'];
        }
        return $out;
    }

    // ── 예산 집행 ───────────────────────────────────────────

    /**
     * 도시 재정을 집행한다. 관리자만 할 수 있다. 참여예산으로 가결된 제안을 집행할 때에는 Budget 이 그 제안을 넘긴다.
     *
     * 분야가 공동 방어시설이면 그 시설의 기금이 채워지고, 방어시설 유지보수면 피해를 입은 시설이 고쳐진다.
     * 그 밖의 분야는 지금은 집행 내역만 남는다. 실제로 쓰인 금액만 재정에서 빠진다 (시설이 다 지어졌으면 남은 만큼만).
     *
     * @param array $form category, title, description, amount_gc, target_code, reason
     * @return array [집행 id 또는 null, 문제 목록]
     */
    public function spend(array $admin, $regionCode, array $form, array $proposal = null, $ip = '')
    {
        if (!Auth::can($admin, Auth::TREASURY_EXECUTE)) {
            return [null, ['권한이 없습니다.']];
        }
        $text = function ($key) use ($form) {
            return isset($form[$key]) && is_string($form[$key]) ? trim($form[$key]) : '';
        };
        $problems = [];
        $region = $this->db->one('SELECT id, code, name FROM regions WHERE code = ?', [(string)$regionCode]);
        if ($region === null) {
            return [null, ['없는 지역입니다.']];
        }
        $category = $text('category');
        $categories = self::categories();
        if (!isset($categories[$category])) {
            $problems[] = '없는 분야입니다.';
        }
        if ($proposal !== null && $category === 'reserve') {
            $problems[] = '참여예산은 예비비로 집행할 수 없습니다.';
        }
        $title = $text('title');
        if ($title === '' || mb_strlen($title) > 60) {
            $problems[] = '집행 이름은 1 ~ 60자여야 합니다.';
        }
        $description = $text('description');
        if (mb_strlen($description) > 600) {
            $problems[] = '설명은 600자까지입니다.';
        }
        $amount = $text('amount_gc');
        if (!preg_match('/^\d{1,12}$/', $amount) || (int)$amount < 1 || (int)$amount > 100000000) {
            $problems[] = '금액은 1 ~ 100,000,000 GC 사이의 정수여야 합니다.';
        }
        $reason = $text('reason');
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            $problems[] = $reasonProblem;
        }
        $effect = self::effectOf($category);
        $target = $text('target_code');
        if ($effect === self::EFFECT_FUND) {
            $def = $this->app->defs()->findStructure($target);
            if ($def === null || $def['ownership'] !== 'city' || $def['comingSoon'] || $def['cityGoalGc'] <= 0) {
                $problems[] = '공동 방어시설을 골라 주세요.';
            }
        } elseif ($effect === self::EFFECT_REPAIR) {
            if ($this->app->defs()->findFacility($target) === null) {
                $problems[] = '고칠 주요 시설을 골라 주세요.';
            }
        } else {
            $target = '';
        }
        if ($problems) {
            return [null, $problems];
        }
        $amount = (int)$amount;
        // 예비비 분야만 예비비 계정에서 나간다.
        $account = $category === 'reserve' ? self::RESERVE : self::GENERAL;

        try {
            $id = $this->db->tx(function () use ($admin, $region, $category, $title, $description, $amount, $reason, $effect, $target, $account, $proposal, $ip) {
                $regionId = (int)$region['id'];
                // 먼저 계정을 잠가 잔액을 확인한다.
                $balance = $this->post($regionId, $account, 0, 'expenditure', null, null);
                if ($balance < $amount) {
                    throw new ApiError(ApiError::CONFLICT, '도시 재정의 잔액이 모자랍니다 (잔액 ' . number_format($balance) . ' GC).', 'insufficient_treasury');
                }
                $spent = $amount;
                $detail = '';
                $reference = 'treasury:' . $category;
                if ($effect === self::EFFECT_FUND) {
                    $result = $this->app->city()->treasuryFund($regionId, $target, $amount, $reference);
                    $spent = $result['accepted'];
                    if ($spent <= 0) {
                        throw new ApiError(ApiError::CONFLICT, '이미 다 지은 시설입니다.', 'nothing_to_do');
                    }
                    $detail = '시설 기금 +' . number_format($spent) . ' GC (단계 ' . $result['levelBefore'] . ' → ' . $result['levelAfter'] . ')';
                } elseif ($effect === self::EFFECT_REPAIR) {
                    $def = $this->app->defs()->findFacility($target);
                    $steps = $def['repairGc'] > 0 ? intdiv($amount, $def['repairGc']) : 0;
                    if ($steps < 1) {
                        throw new ApiError(ApiError::CONFLICT, '한 단계를 고치는 데 ' . number_format($def['repairGc']) . ' GC가 듭니다.', 'amount_too_small');
                    }
                    $result = $this->app->city()->treasuryRepair($regionId, $target, $steps, $reference);
                    $spent = $result['costGc'];
                    if ($result['repaired'] <= 0) {
                        throw new ApiError(ApiError::CONFLICT, '고칠 곳이 없습니다.', 'nothing_to_do');
                    }
                    $detail = '피해 ' . $result['repaired'] . '단계 복구 (남은 피해 ' . $result['damageAfter'] . '단계)';
                } else {
                    $detail = '집행 내역만 기록됩니다 (이 분야의 게임 내 효과는 준비 중).';
                }
                $id = $this->db->insert(
                    'INSERT INTO city_budget_expenditures (public_id, region_id, proposal_id, category, title, description, amount_gc, account,'
                    . ' effect_type, target_code, effect_detail, executed_by, reason, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
                    [Ids::publicId('e_'), $regionId, $proposal !== null ? $proposal['id'] : null, $category, $title, $description, $spent, $account,
                        $effect, $target !== '' ? $target : null, $detail, $admin['id'], $reason, Clock::sql()]
                );
                $this->post($regionId, $account, -$spent, 'expenditure', 'expenditure', $id);
                (new Auth($this->app))->audit($admin['id'], 'treasury.spend', 'region', $region['code'],
                    $category . ' ' . $spent . ' GC' . ($proposal !== null ? ' proposal=' . $proposal['public_id'] : '') . ': ' . $reason, $ip);
                return $id;
            });
        } catch (ApiError $error) {
            return [null, [$error->getMessage()]];
        }
        return [$id, []];
    }

    // ── 조회 (공개) ─────────────────────────────────────────

    private function region($regionCode)
    {
        $region = $this->db->one('SELECT id, code, name FROM regions WHERE code = ?', [(string)$regionCode]);
        if ($region === null) {
            throw new ApiError(ApiError::NOT_FOUND, '없는 지역이에요.');
        }
        return $region;
    }

    /** GET /cities/{code}/treasury: 도시 재정 현황. 누구나 볼 수 있다. */
    public function summary($regionCode)
    {
        $region = $this->region($regionCode);
        $regionId = (int)$region['id'];
        $balances = $this->balances($regionId);
        $income = [];
        $types = Taxes::types();
        foreach ($types as $type => $name) {
            $income[$type] = ['taxType' => $type, 'taxName' => $name, 'totalGc' => 0, 'last30dGc' => 0];
        }
        $since = Clock::sql(Clock::now() - 30 * 86400);
        foreach ($this->db->all(
            'SELECT tax_type, COALESCE(SUM(tax_gc), 0) AS total, COALESCE(SUM(CASE WHEN created_at >= ? THEN tax_gc ELSE 0 END), 0) AS recent'
            . ' FROM city_tax_transactions WHERE region_id = ? GROUP BY tax_type', [$since, $regionId]
        ) as $row) {
            if (isset($income[$row['tax_type']])) {
                $income[$row['tax_type']]['totalGc'] = (int)$row['total'];
                $income[$row['tax_type']]['last30dGc'] = (int)$row['recent'];
            }
        }
        $categories = self::categories();
        $spent = [];
        foreach ($this->db->all('SELECT category, COALESCE(SUM(amount_gc), 0) AS total, COUNT(*) AS n FROM city_budget_expenditures'
            . ' WHERE region_id = ? GROUP BY category', [$regionId]) as $row) {
            $spent[] = ['category' => $row['category'], 'categoryName' => isset($categories[$row['category']]) ? $categories[$row['category']] : $row['category'],
                'totalGc' => (int)$row['total'], 'count' => (int)$row['n']];
        }
        $ledger = [];
        foreach ($this->db->all('SELECT account, delta, balance_after, reason, created_at FROM city_treasury_ledger WHERE region_id = ?'
            . ' ORDER BY id DESC LIMIT 30', [$regionId]) as $row) {
            $ledger[] = ['account' => $row['account'], 'deltaGc' => (int)$row['delta'], 'balanceAfter' => (int)$row['balance_after'],
                'reason' => $row['reason'], 'reasonName' => self::reasonName($row['reason']), 'at' => Clock::isoFromSql($row['created_at'])];
        }
        return [
            'regionCode' => $region['code'], 'regionName' => $region['name'],
            'generalGc' => $balances[self::GENERAL], 'reserveGc' => $balances[self::RESERVE],
            'income' => array_values($income), 'spent' => $spent, 'recent' => $ledger,
            'reservePercent' => $this->app->settings()->treasury()['reservePercent'],
            'notice' => '게임 안의 도시 재정이에요. 실제 돈이 아니고, CITY PASS나 장학 사업의 예산과는 따로예요.',
        ];
    }

    /** GET /cities/{code}/treasury/expenditures: 예산 집행 내역. 누구나 볼 수 있다. */
    public function expenditures($regionCode, $limit = 50, $beforeId = '')
    {
        $region = $this->region($regionCode);
        $limit = max(1, min(100, (int)$limit));
        $where = 'e.region_id = ?';
        $params = [(int)$region['id']];
        if (is_string($beforeId) && preg_match('/^e_[a-z0-9]{14}$/', $beforeId)) {
            $before = (int)$this->db->val('SELECT id FROM city_budget_expenditures WHERE public_id = ?', [$beforeId]);
            if ($before > 0) {
                $where .= ' AND e.id < ?';
                $params[] = $before;
            }
        }
        $categories = self::categories();
        $items = [];
        foreach ($this->db->all(
            'SELECT e.*, b.public_id AS proposal_public_id FROM city_budget_expenditures e LEFT JOIN city_budget_proposals b ON b.id = e.proposal_id'
            . ' WHERE ' . $where . ' ORDER BY e.id DESC LIMIT ' . $limit, $params
        ) as $row) {
            $items[] = [
                'id' => $row['public_id'], 'category' => $row['category'], 'categoryName' => $categories[$row['category']],
                'title' => $row['title'], 'description' => $row['description'], 'amountGc' => (int)$row['amount_gc'],
                'account' => $row['account'], 'effectType' => $row['effect_type'], 'targetCode' => $row['target_code'],
                'effectDetail' => $row['effect_detail'], 'proposalId' => $row['proposal_public_id'],
                'byVote' => $row['proposal_id'] !== null, 'at' => Clock::isoFromSql($row['created_at']),
            ];
        }
        return ['regionCode' => $region['code'], 'expenditures' => $items];
    }

    // ── 관리자 ──────────────────────────────────────────────

    /** 재정이 있는 도시의 목록 (잔액이 많은 것부터). */
    public function adminCities()
    {
        return $this->db->all(
            'SELECT r.code, r.name, COALESCE(SUM(CASE WHEN a.account = ? THEN a.balance END), 0) AS general_gc,'
            . ' COALESCE(SUM(CASE WHEN a.account = ? THEN a.balance END), 0) AS reserve_gc,'
            . ' COALESCE(SUM(a.total_in), 0) AS total_in, COALESCE(SUM(a.total_out), 0) AS total_out'
            . ' FROM city_treasury_accounts a JOIN regions r ON r.id = a.region_id GROUP BY r.id, r.code, r.name'
            . ' ORDER BY COALESCE(SUM(a.balance), 0) DESC, r.id LIMIT 300', [self::GENERAL, self::RESERVE]);
    }

    public function adminLedger($regionId, $limit = 100)
    {
        return $this->db->all('SELECT * FROM city_treasury_ledger WHERE region_id = ? ORDER BY id DESC LIMIT ' . (int)$limit, [(int)$regionId]);
    }

    public function adminExpenditures($regionId = null, $limit = 200)
    {
        $where = '1 = 1';
        $params = [];
        if ($regionId !== null) {
            $where .= ' AND e.region_id = ?';
            $params[] = (int)$regionId;
        }
        return $this->db->all(
            'SELECT e.*, r.code AS region_code, r.name AS region_name, a.login FROM city_budget_expenditures e JOIN regions r ON r.id = e.region_id'
            . ' LEFT JOIN admins a ON a.id = e.executed_by WHERE ' . $where . ' ORDER BY e.id DESC LIMIT ' . (int)$limit, $params);
    }

    /**
     * 도시 재정의 앞뒤가 맞는지 검사한다. 맞으면 null.
     *   - 계정마다 원장의 변화량을 모두 더한 값이 잔액과 같고, 줄마다 적힌 "바뀐 뒤 잔액"이 이어진다. 음수가 된 적이 없다.
     *   - 걷은 세금의 합(명세)이 재정 원장에 들어온 세금의 합과 같다.
     *   - 집행 내역의 합이 재정 원장에서 나간 금액의 합과 같다.
     */
    public function findInconsistency()
    {
        $running = [];
        foreach ($this->db->all('SELECT region_id, account, delta, balance_after FROM city_treasury_ledger ORDER BY id') as $row) {
            $key = $row['region_id'] . ':' . $row['account'];
            $running[$key] = (isset($running[$key]) ? $running[$key] : 0) + (int)$row['delta'];
            if ($running[$key] !== (int)$row['balance_after']) {
                return "재정 원장의 잔액이 이어지지 않음 ($key)";
            }
            if ($running[$key] < 0) {
                return "재정 잔액이 음수가 된 적이 있음 ($key)";
            }
        }
        foreach ($this->db->all('SELECT region_id, account, balance FROM city_treasury_accounts') as $row) {
            $key = $row['region_id'] . ':' . $row['account'];
            $sum = isset($running[$key]) ? $running[$key] : 0;
            if ($sum !== (int)$row['balance']) {
                return "재정 원장의 합($sum)과 잔액({$row['balance']})이 다름 ($key)";
            }
            unset($running[$key]);
        }
        foreach ($running as $key => $sum) {
            if ($sum !== 0) {
                return "계정이 없는 원장이 있음 ($key)";
            }
        }
        $taxes = (int)$this->db->val('SELECT COALESCE(SUM(tax_gc), 0) FROM city_tax_transactions');
        $credited = (int)$this->db->val("SELECT COALESCE(SUM(delta), 0) FROM city_treasury_ledger WHERE reason LIKE 'tax\\_%'");
        if ($taxes !== $credited) {
            return "걷은 세금의 합($taxes)과 재정에 들어온 세금의 합($credited)이 다름";
        }
        $spent = (int)$this->db->val('SELECT COALESCE(SUM(amount_gc), 0) FROM city_budget_expenditures');
        $debited = -(int)$this->db->val("SELECT COALESCE(SUM(delta), 0) FROM city_treasury_ledger WHERE reason = 'expenditure'");
        if ($spent !== $debited) {
            return "집행 내역의 합($spent)과 재정에서 나간 금액의 합($debited)이 다름";
        }
        // 지갑에서 빠진 세금(치른 값에 들어 있는 거래세는 빼고)의 합도 명세와 같아야 한다.
        $withheld = (int)$this->db->val('SELECT COALESCE(SUM(tax_gc), 0) FROM city_tax_transactions WHERE included = 0');
        $fromWallets = -(int)$this->db->val("SELECT COALESCE(SUM(delta), 0) FROM ledger_entries WHERE reason IN ('tax_business', 'tax_property')");
        if ($withheld !== $fromWallets) {
            return "지갑에서 빠진 세금의 합($fromWallets)과 명세의 합($withheld)이 다름";
        }
        return null;
    }
}
