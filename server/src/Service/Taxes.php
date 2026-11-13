<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;
use CG\Infra\Clock;
use CG\Infra\Ids;

/**
 * 게임 안의 가상 지방세 (docs/CITY_TAX_SYSTEM.md).
 *
 * GC 로만 걷는다. 실제 돈이나 현실의 세금과 이어지는 곳이 없다. 걷은 세금은 낸 사람의 주거 도시 재정으로 들어간다.
 *
 *   거래세 (trade)    장비·타워를 살 때 "치른 값에 들어 있는" 세금. 값은 그대로이고, 그 가운데 일부가 도시 재정으로 간다.
 *   사업세 (business) 전투 보상으로 번 GC 가운데 하루 면제액을 넘는 부분에 붙는다. 보상을 받는 순간 지갑에서 빠진다.
 *   재산세 (property) 가진 GC 가운데 면제액을 넘는 부분에 기간마다 한 번 붙는다. 최근에 접속한 주민에게만 걷는다.
 *
 * 초보자 보호: 정책마다 최소 레벨과 "처음 전입한 뒤 지난 날수"가 있다. 그 전에는 내지 않는다. 이방인도 내지 않는다.
 * 중복 징수 방지: 명세(city_tax_transactions)의 (세금 종류, 근거) 가 유일하다. 같은 원장 줄이나 같은 기간에 두 번 걷으려 하면
 * 두 번째는 아무 일도 하지 않는다.
 * 주민이 스스로 내는 방어 기여금(city_fund)과 시설 복구비(city_repair)는 세금이 아니고, 과세 대상으로 고를 수도 없다.
 */
final class Taxes
{
    const TRADE = 'trade';
    const BUSINESS = 'business';
    const PROPERTY = 'property';

    /** 사업세: 오늘 과세 대상이 된 소득. */
    const DAILY_INCOME = 'tax_income';
    /** 사업세: 오늘 낸 세금. */
    const DAILY_PAID = 'tax_business';

    /** 재산세를 한 번의 예약 작업에서 걷는 최대 인원. */
    const PROPERTY_BATCH = 2000;

    /** @var App */
    private $app;
    /** @var \CG\Infra\Db */
    private $db;
    private $policies = null;
    private $payers = [];

    public function __construct(App $app)
    {
        $this->app = $app;
        $this->db = $app->db;
    }

    public static function types()
    {
        return [self::TRADE => '거래세', self::BUSINESS => '사업세', self::PROPERTY => '재산세'];
    }

    /** 세금 종류마다 과세 대상으로 고를 수 있는 원장 사유. 여기에 없는 사유(기여금, 복구비, 정착 지원금 등)에는 세금을 붙일 수 없다. */
    public static function taxableReasons()
    {
        return [
            self::TRADE => [Wallets::GEAR_PURCHASE => '장비 구매', Wallets::GEAR_UPGRADE => '장비 강화', Wallets::TOWER_UNLOCK => '타워 해금'],
            self::BUSINESS => [Wallets::BATTLE_REWARD => '방어전 보상', Wallets::FPS_REWARD => 'FPS 임무 보상', Wallets::HUNT_REWARD => '사냥 보상'],
            self::PROPERTY => [],
        ];
    }

    public static function periods()
    {
        return ['transaction' => '거래할 때', 'weekly' => '한 주에 한 번', 'monthly' => '한 달에 한 번'];
    }

    public function forget()
    {
        $this->policies = null;
        $this->payers = [];
    }

    /** @return array<string,array> [세금 종류 => 정책] */
    public function policies()
    {
        if ($this->policies === null) {
            $this->policies = [];
            foreach ($this->db->all('SELECT * FROM city_tax_policies ORDER BY id') as $row) {
                $this->policies[$row['tax_type']] = [
                    'id' => (int)$row['id'], 'taxType' => $row['tax_type'], 'name' => $row['name'], 'description' => $row['description'],
                    'active' => (int)$row['is_active'] === 1, 'rateBp' => (int)$row['rate_bp'],
                    'appliesTo' => array_values(array_filter(array_map('trim', explode(',', $row['applies_to'])), 'strlen')),
                    'exemptAmount' => (int)$row['exempt_amount'], 'minLevel' => (int)$row['min_level'], 'graceDays' => (int)$row['grace_days'],
                    'capGc' => (int)$row['cap_gc'], 'period' => $row['period'],
                ];
            }
        }
        return $this->policies;
    }

    // ── 납세자 ──────────────────────────────────────────────

    /** 세금을 낼 사람의 정보. 요청 하나 안에서는 한 번만 읽는다. */
    private function payer($playerId)
    {
        if (!isset($this->payers[$playerId])) {
            $row = $this->db->one(
                'SELECT p.region_id, p.residency_status, p.status, COALESCE(c.level, 1) AS level FROM players p'
                . ' LEFT JOIN characters c ON c.player_id = p.id WHERE p.id = ?', [$playerId]);
            $resident = $row !== null && $row['residency_status'] === 'resident' && $row['region_id'] !== null && $row['status'] === 'active';
            $this->payers[$playerId] = [
                'resident' => $resident, 'regionId' => $resident ? (int)$row['region_id'] : null,
                'level' => $row !== null ? (int)$row['level'] : 1,
                'firstRegisteredAt' => $resident ? $this->app->growth()->firstRegisteredAt($playerId) : null,
            ];
        }
        return $this->payers[$playerId];
    }

    /** 이 사람이 이 세금을 내지 않는 이유. 내야 하면 null. */
    private function exemption(array $payer, array $policy)
    {
        if (!$payer['resident']) {
            return 'stranger';
        }
        if ($payer['level'] < $policy['minLevel']) {
            return 'beginner_level';
        }
        if ($policy['graceDays'] > 0 && ($payer['firstRegisteredAt'] === null
            || Clock::now() < $payer['firstRegisteredAt'] + $policy['graceDays'] * 86400)) {
            return 'new_resident';
        }
        return null;
    }

    public static function exemptionMessage($reason)
    {
        $messages = [
            'stranger' => '전입신고를 하기 전에는 세금을 내지 않아요.',
            'beginner_level' => '아직 레벨이 낮아 세금을 내지 않아요.',
            'new_resident' => '전입한 지 얼마 되지 않아 세금을 내지 않아요.',
        ];
        return isset($messages[$reason]) ? $messages[$reason] : '';
    }

    private static function taxOf($base, $rateBp)
    {
        return $base > 0 ? intdiv($base * $rateBp, 10000) : 0;
    }

    // ── 징수: 거래세와 사업세 ───────────────────────────────

    /**
     * 지갑의 원장에 한 줄이 생길 때마다 Wallets 가 부른다 (같은 거래 안에서).
     * 그 줄이 과세 대상이면 세금을 걷어 도시 재정에 넣고 명세를 남긴다.
     * @return int 세금까지 반영한 뒤의 잔액
     */
    public function onLedger($playerId, $delta, $reason, $ledgerId, $balanceAfter)
    {
        foreach ($this->policies() as $type => $policy) {
            if (!$policy['active'] || $policy['rateBp'] <= 0 || $type === self::PROPERTY || !in_array($reason, $policy['appliesTo'], true)) {
                continue;
            }
            // 과세 대상으로 허용된 사유만. DB 를 직접 고쳐 다른 사유를 넣어도 세금이 붙지 않는다.
            if (!isset(self::taxableReasons()[$type][$reason])) {
                continue;
            }
            $payer = $this->payer($playerId);
            if ($this->exemption($payer, $policy) !== null) {
                continue;
            }
            if ($type === self::TRADE && $delta < 0) {
                $this->collectTrade($playerId, $payer, $policy, -$delta, $reason, $ledgerId);
            } elseif ($type === self::BUSINESS && $delta > 0) {
                $balanceAfter = $this->collectBusiness($playerId, $payer, $policy, $delta, $reason, $ledgerId, $balanceAfter);
            }
        }
        return $balanceAfter;
    }

    /** 거래세: 치른 값에 들어 있다. 지갑은 더 줄지 않고, 값의 일부가 도시 재정으로 간다. */
    private function collectTrade($playerId, array $payer, array $policy, $spent, $reason, $ledgerId)
    {
        if ($spent < $policy['exemptAmount']) {
            return;
        }
        $tax = self::taxOf($spent, $policy['rateBp']);
        if ($tax <= 0) {
            return;
        }
        $this->record($policy, $playerId, $payer['regionId'], $spent, $tax, true, $reason, 'L' . $ledgerId, $ledgerId, null);
    }

    /** 사업세: 오늘 번 과세 소득 가운데 면제액을 넘는 부분에 붙는다. 보상을 받은 직후 지갑에서 뺀다. */
    private function collectBusiness($playerId, array $payer, array $policy, $income, $reason, $ledgerId, $balanceAfter)
    {
        $daily = $this->app->daily();
        $before = $daily->lock($playerId, self::DAILY_INCOME);
        $daily->set($playerId, self::DAILY_INCOME, $before + $income);
        $taxable = max(0, $before + $income - $policy['exemptAmount']) - max(0, $before - $policy['exemptAmount']);
        $tax = self::taxOf($taxable, $policy['rateBp']);
        if ($policy['capGc'] > 0) {
            $paid = $daily->lock($playerId, self::DAILY_PAID);
            $tax = min($tax, max(0, $policy['capGc'] - $paid));
        }
        // 방금 받은 보상보다 많이 걷지 않는다.
        $tax = min($tax, $income, $balanceAfter);
        if ($tax <= 0) {
            return $balanceAfter;
        }
        $id = $this->record($policy, $playerId, $payer['regionId'], $taxable, $tax, false, $reason, 'L' . $ledgerId, $ledgerId, null);
        if ($id === null) {
            return $balanceAfter;
        }
        $daily->add($playerId, self::DAILY_PAID, $tax);
        return $this->deduct($playerId, $tax, Wallets::TAX_BUSINESS, $id);
    }

    /** 세금을 지갑에서 빼고, 그 원장 줄을 명세에 적는다. */
    private function deduct($playerId, $tax, $walletReason, $taxTransactionId)
    {
        $wallets = $this->app->wallets();
        $after = $wallets->apply($playerId, -$tax, $walletReason, 'tax_transaction', $taxTransactionId);
        $this->db->exec('UPDATE city_tax_transactions SET wallet_ledger_id = ? WHERE id = ?', [$wallets->lastEntryId, $taxTransactionId]);
        return $after;
    }

    /**
     * 명세를 남기고 도시 재정에 넣는다. 같은 근거(source_key)로 이미 걷었으면 아무것도 하지 않고 null 을 돌려준다.
     * @return int|null 명세의 id
     */
    private function record(array $policy, $playerId, $regionId, $base, $tax, $included, $sourceReason, $sourceKey, $sourceLedgerId,
        $periodKey)
    {
        $inserted = $this->db->exec(
            'INSERT IGNORE INTO city_tax_transactions (public_id, tax_type, policy_id, player_id, region_id, base_gc, rate_bp, tax_gc, included,'
            . ' source_reason, source_key, source_ledger_id, period_key, created_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)',
            [Ids::publicId('x_'), $policy['taxType'], $policy['id'], $playerId, $regionId, $base, $policy['rateBp'], $tax, $included ? 1 : 0,
                (string)$sourceReason, $sourceKey, $sourceLedgerId, $periodKey, Clock::sql()]
        );
        if ($inserted < 1) {
            return null;
        }
        $id = (int)$this->db->val('SELECT id FROM city_tax_transactions WHERE tax_type = ? AND source_key = ?', [$policy['taxType'], $sourceKey]);
        $this->app->treasury()->creditTax($regionId, $tax, $policy['taxType'], $id);
        return $id;
    }

    // ── 징수: 재산세 ────────────────────────────────────────

    /** 지금이 속한 기간의 이름 (한국 시간). weekly: 2026-W46, monthly: 2026-11 */
    public static function periodKey($period, $unix = null)
    {
        $local = ($unix === null ? Clock::now() : $unix) + 9 * 3600;
        return $period === 'monthly' ? gmdate('Y-m', $local) : gmdate('o-\WW', $local);
    }

    /**
     * 재산세를 걷는다. 예약 작업이 부른다. 한 기간에 한 사람에게 한 번만 걷는다 (여러 번 돌아도 같다).
     * @return array collected(걷은 사람 수), totalGc
     */
    public function collectProperty()
    {
        $policies = $this->policies();
        $none = ['collected' => 0, 'totalGc' => 0];
        if (!isset($policies[self::PROPERTY])) {
            return $none;
        }
        $policy = $policies[self::PROPERTY];
        if (!$policy['active'] || $policy['rateBp'] <= 0 || ($policy['period'] !== 'weekly' && $policy['period'] !== 'monthly')) {
            return $none;
        }
        $period = self::periodKey($policy['period']);
        $absentee = $this->app->settings()->treasury()['absenteeDays'];
        // 걷을 수 있는 사람: 주민, 레벨이 되고, 최근에 접속했고, 면제액보다 많이 가졌고, 이번 기간에 아직 내지 않은 사람.
        $candidates = $this->db->all(
            'SELECT p.id FROM players p JOIN wallets w ON w.player_id = p.id LEFT JOIN characters c ON c.player_id = p.id'
            . " WHERE p.residency_status = 'resident' AND p.status = 'active' AND p.region_id IS NOT NULL"
            . ' AND COALESCE(c.level, 1) >= ? AND p.last_seen_at >= ? AND w.balance > ?'
            . ' AND NOT EXISTS (SELECT 1 FROM city_tax_transactions t WHERE t.tax_type = ? AND t.source_key = CONCAT(?, p.id))'
            . ' ORDER BY p.id LIMIT ' . self::PROPERTY_BATCH,
            [$policy['minLevel'], Clock::sql(Clock::now() - $absentee * 86400), $policy['exemptAmount'], self::PROPERTY, 'P' . $period . ':']
        );
        $collected = 0;
        $total = 0;
        foreach ($candidates as $candidate) {
            $tax = $this->collectPropertyFrom((int)$candidate['id'], $policy, $period);
            if ($tax > 0) {
                $collected++;
                $total += $tax;
            }
        }
        return ['collected' => $collected, 'totalGc' => $total];
    }

    private function collectPropertyFrom($playerId, array $policy, $period)
    {
        return $this->db->tx(function () use ($playerId, $policy, $period) {
            $balance = $this->app->wallets()->lock($playerId);
            unset($this->payers[$playerId]);
            $payer = $this->payer($playerId);
            if ($this->exemption($payer, $policy) !== null) {
                return 0;
            }
            $base = $balance - $policy['exemptAmount'];
            $tax = self::taxOf($base, $policy['rateBp']);
            if ($policy['capGc'] > 0) {
                $tax = min($tax, $policy['capGc']);
            }
            // 세금을 내고 나서도 면제액 아래로 내려가지 않는다.
            $tax = min($tax, max(0, $base));
            if ($tax <= 0) {
                return 0;
            }
            $id = $this->record($policy, $playerId, $payer['regionId'], $base, $tax, false, '', 'P' . $period . ':' . $playerId, null, $period);
            if ($id === null) {
                return 0;
            }
            $this->deduct($playerId, $tax, Wallets::TAX_PROPERTY, $id);
            return $tax;
        });
    }

    // ── 조회 ────────────────────────────────────────────────

    private static function policyOut(array $policy)
    {
        $reasons = self::taxableReasons()[$policy['taxType']];
        $applies = [];
        foreach ($policy['appliesTo'] as $reason) {
            if (isset($reasons[$reason])) {
                $applies[] = ['reason' => $reason, 'name' => $reasons[$reason]];
            }
        }
        return [
            'taxType' => $policy['taxType'], 'name' => $policy['name'], 'description' => $policy['description'], 'active' => $policy['active'],
            'rateBp' => $policy['rateBp'], 'appliesTo' => $applies, 'exemptAmount' => $policy['exemptAmount'],
            'minLevel' => $policy['minLevel'], 'graceDays' => $policy['graceDays'], 'capGc' => $policy['capGc'], 'period' => $policy['period'],
            'included' => $policy['taxType'] === self::TRADE,
        ];
    }

    /** GET /tax/policies: 세금 정책과, 내가 지금 세금을 내는지. */
    public function policiesFor(array $player)
    {
        $payer = $this->payer($player['id']);
        $out = [];
        foreach ($this->policies() as $policy) {
            $item = self::policyOut($policy);
            $reason = $this->exemption($payer, $policy);
            $item['exempt'] = $reason !== null;
            $item['exemptReason'] = $reason;
            $item['exemptMessage'] = self::exemptionMessage($reason);
            $out[] = $item;
        }
        return [
            'policies' => $out,
            'notice' => '게임 안에서만 쓰는 가상의 세금이에요. 실제 돈이나 현실의 세금과는 아무 관계가 없어요.',
            'businessIncomeToday' => $this->app->daily()->get($player['id'], self::DAILY_INCOME),
            'businessTaxToday' => $this->app->daily()->get($player['id'], self::DAILY_PAID),
        ];
    }

    /** GET /tax/transactions: 내가 낸 세금의 명세 (최근 것부터). */
    public function transactionsOf(array $player, $limit = 50, $beforeId = 0)
    {
        $limit = max(1, min(100, (int)$limit));
        $params = [$player['id']];
        $where = 't.player_id = ?';
        $before = is_string($beforeId) && preg_match('/^x_[a-z0-9]{14}$/', $beforeId)
            ? (int)$this->db->val('SELECT id FROM city_tax_transactions WHERE public_id = ? AND player_id = ?', [$beforeId, $player['id']]) : 0;
        if ($before > 0) {
            $where .= ' AND t.id < ?';
            $params[] = $before;
        }
        $types = self::types();
        $items = [];
        foreach ($this->db->all(
            'SELECT t.*, r.code AS region_code, r.name AS region_name FROM city_tax_transactions t JOIN regions r ON r.id = t.region_id'
            . ' WHERE ' . $where . ' ORDER BY t.id DESC LIMIT ' . $limit, $params
        ) as $row) {
            $items[] = [
                'id' => $row['public_id'], 'taxType' => $row['tax_type'], 'taxName' => $types[$row['tax_type']],
                'baseGc' => (int)$row['base_gc'], 'rateBp' => (int)$row['rate_bp'], 'taxGc' => (int)$row['tax_gc'],
                'included' => (int)$row['included'] === 1, 'sourceReason' => $row['source_reason'], 'periodKey' => $row['period_key'],
                'regionCode' => $row['region_code'], 'regionName' => $row['region_name'], 'at' => Clock::isoFromSql($row['created_at']),
            ];
        }
        $totals = [];
        foreach ($this->db->all('SELECT tax_type, COALESCE(SUM(tax_gc), 0) AS total, COUNT(*) AS n FROM city_tax_transactions WHERE player_id = ? GROUP BY tax_type',
            [$player['id']]) as $row) {
            $totals[] = ['taxType' => $row['tax_type'], 'taxName' => $types[$row['tax_type']], 'totalGc' => (int)$row['total'], 'count' => (int)$row['n']];
        }
        return ['transactions' => $items, 'totals' => $totals, 'gc' => $this->app->wallets()->balance($player['id'])];
    }

    // ── 관리자 ──────────────────────────────────────────────

    public function adminPolicy($type)
    {
        return $this->db->one('SELECT * FROM city_tax_policies WHERE tax_type = ?', [(string)$type]);
    }

    /**
     * 세금 정책을 고친다: 켜고 끄기, 세율, 적용 대상, 면제 기준, 초보자 보호, 상한, 징수 시점.
     * @return string[] 문제 목록
     */
    public function savePolicy(array $admin, $type, array $form, $ip = '')
    {
        if (!Auth::can($admin, Auth::CITY_POLICY)) {
            return ['권한이 없습니다.'];
        }
        $before = $this->adminPolicy($type);
        if ($before === null) {
            return ['없는 세금입니다.'];
        }
        $text = function ($key) use ($form) {
            return isset($form[$key]) && is_string($form[$key]) ? trim($form[$key]) : '';
        };
        $problems = [];
        $int = function ($key, $label, $min, $max) use ($text, &$problems) {
            $value = $text($key);
            if (!preg_match('/^\d{1,12}$/', $value) || (int)$value < $min || (int)$value > $max) {
                $problems[] = $label . ': ' . number_format($min) . ' ~ ' . number_format($max) . ' 사이의 정수여야 합니다.';
                return 0;
            }
            return (int)$value;
        };
        $after = [
            'is_active' => isset($form['is_active']) && $form['is_active'] === '1' ? 1 : 0,
            // 세율은 20% 를 넘길 수 없다.
            'rate_bp' => $int('rate_bp', '세율 (1/100 %)', 0, 2000),
            'exempt_amount' => $int('exempt_amount', '면제 기준', 0, 100000000),
            'min_level' => $int('min_level', '초보자 보호: 최소 레벨', 1, 50),
            'grace_days' => $int('grace_days', '초보자 보호: 전입 뒤 날수', 0, 365),
            'cap_gc' => $int('cap_gc', '상한', 0, 100000000),
        ];
        $allowed = self::taxableReasons()[$before['tax_type']];
        $applies = [];
        foreach (array_keys($allowed) as $reason) {
            if (isset($form['applies_' . $reason]) && $form['applies_' . $reason] === '1') {
                $applies[] = $reason;
            }
        }
        $after['applies_to'] = implode(',', $applies);
        $period = $text('period');
        if ($before['tax_type'] === self::PROPERTY) {
            if ($period !== 'weekly' && $period !== 'monthly') {
                $problems[] = '재산세의 징수 시점은 한 주 또는 한 달에 한 번이어야 합니다.';
            }
        } else {
            $period = 'transaction';
            if ($after['is_active'] === 1 && !$applies) {
                $problems[] = '켜려면 적용 대상을 하나 이상 골라 주세요.';
            }
        }
        $after['period'] = $period;
        if ($after['is_active'] === 1 && $after['rate_bp'] < 1) {
            $problems[] = '켜려면 세율이 0보다 커야 합니다.';
        }
        $reason = $text('reason');
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            $problems[] = $reasonProblem;
        }
        if ($problems) {
            return $problems;
        }
        $old = [];
        foreach ($after as $column => $value) {
            $old[$column] = is_int($value) ? (int)$before[$column] : $before[$column];
        }
        if ($old === $after) {
            return [];
        }
        $this->db->tx(function () use ($admin, $before, $old, $after, $reason, $ip) {
            $this->db->exec(
                'UPDATE city_tax_policies SET is_active = ?, rate_bp = ?, exempt_amount = ?, min_level = ?, grace_days = ?, cap_gc = ?, applies_to = ?,'
                . ' period = ?, updated_at = ? WHERE id = ?',
                [$after['is_active'], $after['rate_bp'], $after['exempt_amount'], $after['min_level'], $after['grace_days'], $after['cap_gc'],
                    $after['applies_to'], $after['period'], Clock::sql(), $before['id']]
            );
            $this->db->exec(
                'INSERT INTO config_history (target_type, target_key, before_value, after_value, admin_id, reason, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)',
                ['city_tax_policies', $before['tax_type'], json_encode($old), json_encode($after), $admin['id'], $reason, Clock::sql()]
            );
            (new Auth($this->app))->audit($admin['id'], 'tax.policy.update', 'city_tax_policies', $before['tax_type'],
                ($old['is_active'] !== $after['is_active'] ? ($after['is_active'] ? '[켬] ' : '[끔] ') : '') . $reason, $ip);
        });
        $this->forget();
        return [];
    }

    /** 세금 정책의 변경 이력. */
    public function policyHistory($limit = 50)
    {
        return $this->db->all(
            "SELECT h.*, a.login FROM config_history h LEFT JOIN admins a ON a.id = h.admin_id WHERE h.target_type = 'city_tax_policies'"
            . ' ORDER BY h.id DESC LIMIT ' . (int)$limit);
    }

    /** 세금 종류별 징수 합계 (전체, 최근 7일). */
    public function adminTotals()
    {
        $since = Clock::sql(Clock::now() - 7 * 86400);
        $rows = [];
        foreach (array_keys(self::types()) as $type) {
            $rows[$type] = ['total' => 0, 'count' => 0, 'week' => 0];
        }
        foreach ($this->db->all('SELECT tax_type, COALESCE(SUM(tax_gc), 0) AS total, COUNT(*) AS n,'
            . ' COALESCE(SUM(CASE WHEN created_at >= ? THEN tax_gc ELSE 0 END), 0) AS week FROM city_tax_transactions GROUP BY tax_type', [$since]) as $row) {
            $rows[$row['tax_type']] = ['total' => (int)$row['total'], 'count' => (int)$row['n'], 'week' => (int)$row['week']];
        }
        return $rows;
    }

    /** 최근 명세. 낸 사람은 공개 ID 로만 보인다. */
    public function adminTransactions($regionId = null, $limit = 200)
    {
        $where = '1 = 1';
        $params = [];
        if ($regionId !== null) {
            $where .= ' AND t.region_id = ?';
            $params[] = (int)$regionId;
        }
        return $this->db->all(
            'SELECT t.*, r.name AS region_name, p.public_id AS player_public_id FROM city_tax_transactions t JOIN regions r ON r.id = t.region_id'
            . ' JOIN players p ON p.id = t.player_id WHERE ' . $where . ' ORDER BY t.id DESC LIMIT ' . (int)$limit, $params);
    }
}
