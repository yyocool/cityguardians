<?php
namespace CG\Service;

use CG\Infra\ApiError;
use CG\Infra\Clock;
use CG\Infra\Db;
use CG\Infra\Ids;

/**
 * 지갑과 원장 (docs/DATABASE.md 4장). GC 가 바뀌는 곳은 여기 하나뿐이다.
 *
 * - 지갑 행을 잠그고(FOR UPDATE), 잔액을 확인하고, 바꾸고, 원장에 한 줄을 남긴다. 모두 한 거래 안에서.
 * - 잔액은 음수가 될 수 없다.
 * - 세 전투 모드의 보상이 모두 같은 지갑으로 들어온다. 모드마다 따로 가진 잔액은 없다.
 *
 * 전투·도시 위험도 쪽 코드는 GC 를 "주는 것"과 "플레이어가 스스로 고른 구매·기부"에만 이 클래스를 부른다.
 * 전투에서 지거나 도시 위험도가 올랐다고 GC 를 빼는 호출은 없다.
 *
 * v9.0 의 가상 지방세(CG\Service\Taxes)는 관리자가 켠 정책이 있을 때에만, 정해진 세율과 면제 기준대로 걷는다.
 * 도시 위험도와는 상관이 없다. 걷을 때마다 원장(사유 tax_*)과 세금 명세, 도시 재정 원장에 함께 남는다.
 */
final class Wallets
{
    const HUNT_REWARD = 'hunt_reward';
    const BATTLE_REWARD = 'battle_reward';
    const FPS_REWARD = 'fps_reward';
    const TOWER_UNLOCK = 'tower_unlock';
    const GEAR_PURCHASE = 'gear_purchase';
    const GEAR_UPGRADE = 'gear_upgrade';
    const CITY_REPAIR = 'city_repair';
    const CITY_FUND = 'city_fund';
    const RESIDENCY_BONUS = 'residency_bonus';
    const ADMIN_ADJUST = 'admin_adjust';
    // v9.0
    /** 이사 수수료. */
    const RESIDENCY_MOVE = 'residency_move';
    /** 가상 지방세: 사업세 (보상을 받을 때 빠진다). */
    const TAX_BUSINESS = 'tax_business';
    /** 가상 지방세: 재산세 (기간마다 한 번 빠진다). */
    const TAX_PROPERTY = 'tax_property';

    /** @var Db */
    private $db;
    /** @var callable|null 원장에 한 줄이 생길 때마다 부르는 일 (가상 지방세). 세금까지 반영한 잔액을 돌려준다. */
    private $onEntry = null;
    /** 가장 최근에 쓴 원장 줄의 번호. */
    public $lastEntryId = 0;

    public function __construct(Db $db)
    {
        $this->db = $db;
    }

    /**
     * 원장에 한 줄이 생길 때마다 부를 일을 건다: function ($playerId, $delta, $reason, $ledgerId, $balanceAfter): int
     * 세금 자체의 줄(tax_*)에는 부르지 않는다 (세금에 세금이 붙지 않는다).
     */
    public function onEntry(callable $work = null)
    {
        $this->onEntry = $work;
    }

    public function balance($playerId)
    {
        return (int)$this->db->val('SELECT balance FROM wallets WHERE player_id = ?', [$playerId]);
    }

    /** 지갑 행을 잠그고 잔액을 돌려준다. 거래 안에서만 부른다. */
    public function lock($playerId)
    {
        if (!$this->db->inTransaction()) {
            throw new \LogicException('지갑은 거래 안에서만 잠글 수 있습니다.');
        }
        $balance = $this->db->val('SELECT balance FROM wallets WHERE player_id = ? FOR UPDATE', [$playerId]);
        if ($balance === null) {
            throw new ApiError(ApiError::NOT_FOUND, '지갑을 찾을 수 없어요.');
        }
        return (int)$balance;
    }

    /**
     * 잔액을 바꾸고 원장에 남긴다. 바뀐 뒤의 잔액을 돌려준다.
     * 모자라면 INSUFFICIENT_GC 오류가 나고 아무것도 바뀌지 않는다.
     */
    public function apply($playerId, $delta, $reason, $refType = null, $refId = null, $txnId = null)
    {
        $delta = (int)$delta;
        if ($delta === 0) {
            return $this->balance($playerId);
        }
        return $this->db->tx(function () use ($playerId, $delta, $reason, $refType, $refId, $txnId) {
            $balance = $this->lock($playerId);
            $after = $balance + $delta;
            if ($after < 0) {
                throw new ApiError(ApiError::INSUFFICIENT_GC, 'GC가 모자라요.');
            }
            $now = Clock::sql();
            $this->db->exec('UPDATE wallets SET balance = ?, updated_at = ? WHERE player_id = ?', [$after, $now, $playerId]);
            $entryId = $this->db->insert(
                'INSERT INTO ledger_entries (player_id, account, delta, balance_after, reason, ref_type, ref_id, txn_id, created_at)'
                . " VALUES (?, 'wallet', ?, ?, ?, ?, ?, ?, ?)",
                [$playerId, $delta, $after, $reason, $refType, $refId, $txnId !== null ? $txnId : Ids::txn(), $now]
            );
            $this->lastEntryId = $entryId;
            if ($this->onEntry !== null && $reason !== self::TAX_BUSINESS && $reason !== self::TAX_PROPERTY) {
                // 같은 거래 안에서 처리한다: 세금을 걷지 못하면 이 줄도 남지 않는다.
                $after = (int)call_user_func($this->onEntry, $playerId, $delta, $reason, $entryId, $after);
            }
            return $after;
        });
    }

    /**
     * 원장과 잔액이 맞는지 검사한다. 맞으면 null.
     * 원장의 변화량을 모두 더한 값이 잔액과 같아야 하고, 줄마다 적힌 "바뀐 뒤 잔액"이 이어져야 한다.
     */
    public function findInconsistency($playerId)
    {
        $balance = $this->balance($playerId);
        $running = 0;
        foreach ($this->db->all("SELECT delta, balance_after FROM ledger_entries WHERE player_id = ? AND account = 'wallet' ORDER BY id",
            [$playerId]) as $row) {
            $running += (int)$row['delta'];
            if ($running !== (int)$row['balance_after']) {
                return '원장의 잔액이 이어지지 않음';
            }
            if ($running < 0) {
                return '잔액이 음수가 된 적이 있음';
            }
        }
        return $running === $balance ? null : "원장의 합($running)과 잔액($balance)이 다름";
    }
}
