<?php
namespace CG\Service;

use CG\Infra\Clock;
use CG\Infra\Db;

/** 일일 한도 집계. 날짜 경계는 한국 시간 자정이다. 날이 바뀌면 새 행에서 0부터 센다. */
final class Daily
{
    const HUNT_GC = 'hunt_gc';
    const FPS_REWARDS = 'fps_rewards';
    /** 오늘 낮춘 도시 위험도 (1/100 단위). */
    const RISK_REDUCED = 'risk_reduced';
    const REPAIRS = 'repairs';
    const FUND_GC = 'fund_gc';

    /** @var Db */
    private $db;

    public function __construct(Db $db)
    {
        $this->db = $db;
    }

    public function get($playerId, $counter)
    {
        return (int)$this->db->val(
            'SELECT value FROM daily_counters WHERE player_id = ? AND date = ? AND counter = ?',
            [$playerId, Clock::koreaDate(), $counter]
        );
    }

    /** 값을 읽으면서 그 행을 잠근다. 한도 검사와 증가 사이에 다른 요청이 끼어들지 못하게 한다. */
    public function lock($playerId, $counter)
    {
        $date = Clock::koreaDate();
        $this->db->exec(
            'INSERT IGNORE INTO daily_counters (player_id, date, counter, value) VALUES (?, ?, ?, 0)',
            [$playerId, $date, $counter]
        );
        return (int)$this->db->val(
            'SELECT value FROM daily_counters WHERE player_id = ? AND date = ? AND counter = ? FOR UPDATE',
            [$playerId, $date, $counter]
        );
    }

    public function set($playerId, $counter, $value)
    {
        $date = Clock::koreaDate();
        $this->db->exec(
            'INSERT IGNORE INTO daily_counters (player_id, date, counter, value) VALUES (?, ?, ?, 0)',
            [$playerId, $date, $counter]
        );
        $this->db->exec(
            'UPDATE daily_counters SET value = ? WHERE player_id = ? AND date = ? AND counter = ?',
            [(int)$value, $playerId, $date, $counter]
        );
    }

    public function add($playerId, $counter, $amount)
    {
        $this->set($playerId, $counter, $this->lock($playerId, $counter) + (int)$amount);
    }
}
