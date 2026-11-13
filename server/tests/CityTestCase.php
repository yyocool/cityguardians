<?php
use CG\Admin\Auth;
use CG\Infra\Clock;
use CG\Service\Settings;

/**
 * v9.0 테스트의 바탕: 설정을 바꾸고, "연결된 계정"의 주민을 만들고, 세금 정책을 켜는 도우미.
 */
abstract class CityTestCase extends BenefitTestCase
{
    private $linkSerial = 0;

    /** 운영 설정의 일부 값을 바꾼다 (관리자가 고친 것과 같다). */
    protected function setting($key, array $overrides)
    {
        $values = array_merge($this->app->settings()->get($key), $overrides);
        $this->db->exec('INSERT INTO game_config (config_key, value, updated_at) VALUES (?, ?, ?)'
            . ' ON DUPLICATE KEY UPDATE value = VALUES(value), updated_at = VALUES(updated_at)', [$key, json_encode($values), Clock::sql()]);
        $this->app->settings()->forget();
    }

    /**
     * 테스트 준비용: 게스트가 아닌 로그인 수단을 계정에 붙인다 ("연결된 계정").
     * 계정 연결 기능 자체는 아직 없다. 여기서는 연결이 끝난 뒤의 상태만 만든다.
     */
    protected function link(array $player)
    {
        $this->db->exec('INSERT INTO player_credentials (player_id, type, identifier, secret_hash, created_at) VALUES (?, ?, ?, ?, ?)',
            [$player['id'], 'test_link', 'link-' . (++$this->linkSerial) . '-' . $player['id'], str_repeat('0', 64), Clock::sql()]);
    }

    /** 연결된 계정의 주민 (유효 전입자로 세어진다). */
    protected function linkedResident($regionCode = '11110', $nickname = null)
    {
        $player = $this->guest();
        $this->link($player);
        $this->app->players()->registerResidency($player, $regionCode, $nickname !== null ? $nickname : '시민' . (++$this->linkSerial));
        return $this->app->players()->get($player['id']);
    }

    protected function regionId($regionCode)
    {
        return (int)$this->db->val('SELECT id FROM regions WHERE code = ?', [$regionCode]);
    }

    protected function stats($regionCode)
    {
        return $this->db->one('SELECT * FROM city_population_stats WHERE region_id = ?', [$this->regionId($regionCode)]);
    }

    /** 세금 정책을 켠다 (관리자가 켠 것과 같다). 초보자 보호는 테스트가 따로 정하지 않으면 끈다. */
    protected function enableTax($type, array $overrides = [])
    {
        $values = array_merge(['is_active' => 1, 'min_level' => 1, 'grace_days' => 0], $overrides);
        $sets = [];
        foreach (array_keys($values) as $column) {
            $sets[] = "$column = ?";
        }
        $this->db->exec('UPDATE city_tax_policies SET ' . implode(', ', $sets) . ' WHERE tax_type = ?', array_merge(array_values($values), [$type]));
        $this->app->taxes()->forget();
    }

    protected function treasuryGc($regionCode, $account = 'general')
    {
        return $this->app->treasury()->balances($this->regionId($regionCode))[$account];
    }

    protected function operator()
    {
        return $this->admin('ops', Auth::ROLE_OPERATOR);
    }

    protected function balancer()
    {
        return $this->admin('bal', Auth::ROLE_BALANCE);
    }

    protected function contentMaker()
    {
        return $this->admin('maker', Auth::ROLE_CONTENT);
    }

    protected function enableCoop(array $overrides = [])
    {
        $this->setting(Settings::COOP, array_merge(['enabled' => 1], $overrides));
    }

    protected function enableBudget(array $overrides = [])
    {
        $this->setting(Settings::BUDGET, array_merge(['enabled' => 1], $overrides));
    }

    /**
     * 여러 요청을 "같은 순간에" 보낸다: 요청마다 PHP 프로세스를 따로 띄우고(저마다 DB 연결이 다르다), 같은 시각에 시작하게 한다.
     * 잠금과 유일 키가 실제로 동시 요청을 막는지 확인하는 데 쓴다.
     *
     * @param array[] $jobs tests/worker.php 가 아는 작업들 (job, playerId, ...)
     * @return array[] 작업 순서대로의 결과: ['ok' => true, 'data' => ...] 또는 ['ok' => false, 'code' => ..., 'reason' => ...]
     */
    protected function parallel(array $jobs)
    {
        $startAt = microtime(true) + 1.5 + 0.1 * count($jobs);
        $running = [];
        foreach ($jobs as $index => $job) {
            $spec = base64_encode(json_encode($job + ['clock' => Clock::now(), 'startAt' => $startAt]));
            $command = '"' . PHP_BINARY . '" "' . __DIR__ . DIRECTORY_SEPARATOR . 'worker.php" ' . $spec;
            $pipes = [];
            $process = proc_open($command, [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes, __DIR__, null,
                ['bypass_shell' => true]);
            if (!is_resource($process)) {
                throw new RuntimeException('일꾼 프로세스를 띄우지 못했습니다.');
            }
            fclose($pipes[0]);
            $running[$index] = [$process, $pipes];
        }
        $results = [];
        foreach ($running as $index => $item) {
            list($process, $pipes) = $item;
            $out = stream_get_contents($pipes[1]);
            $err = stream_get_contents($pipes[2]);
            fclose($pipes[1]);
            fclose($pipes[2]);
            proc_close($process);
            $decoded = json_decode((string)$out, true);
            if (!is_array($decoded)) {
                throw new RuntimeException("일꾼 $index 의 결과를 읽지 못했습니다: " . trim($out . ' ' . $err));
            }
            $results[$index] = $decoded;
        }
        return $results;
    }

    /** parallel() 의 결과 가운데 성공한 것의 수. */
    protected static function succeeded(array $results)
    {
        return count(array_filter($results, function ($result) {
            return $result['ok'] === true && empty($result['data']['duplicate']);
        }));
    }
}
