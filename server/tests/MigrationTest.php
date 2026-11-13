<?php
use CG\Infra\Migrator;

/** 마이그레이션: 추가만 하고, 다시 돌려도 안전하고, 기존 데이터를 지키는가. */
final class MigrationTest extends DbTestCase
{
    private function migrator()
    {
        return new Migrator($this->db, __DIR__ . '/../migrations');
    }

    public function testAllMigrationsAreRecordedAndRunningAgainDoesNothing()
    {
        $applied = $this->migrator()->applied();
        T::eq(['0001', '0002', '0003', '0004', '0005', '0006', '0007', '0008', '0009', '0010', '0011'], $applied);
        T::eq([], $this->migrator()->pending());
        T::eq([], $this->migrator()->migrate());
    }

    public function testSeedCreatesTheDefinitionsTheSpecAsksFor()
    {
        T::eq(3, (int)$this->db->val('SELECT COUNT(*) FROM combat_modes'));
        T::eq(['defense', 'fps', 'hunt'], array_column($this->db->all('SELECT code FROM combat_modes ORDER BY code'), 'code'));
        T::eq(8, (int)$this->db->val('SELECT COUNT(*) FROM combat_equipment'));
        T::eq(15, (int)$this->db->val('SELECT COUNT(*) FROM defense_structures'));
        T::eq(['advanced' => 4, 'basic' => 3, 'mid' => 4, 'special' => 4], array_map('intval', array_column(
            $this->db->all('SELECT tier, COUNT(*) AS n FROM defense_structures GROUP BY tier ORDER BY tier'), 'n', 'tier')));
        T::eq(16, (int)$this->db->val('SELECT COUNT(*) FROM sidos'));
        T::eq(230, (int)$this->db->val('SELECT COUNT(*) FROM regions'));
        T::eq(1, (int)$this->db->val('SELECT COUNT(*) FROM city_risk_policy'));
        T::ok((int)$this->db->val('SELECT COUNT(*) FROM fps_missions') >= 1);
        T::ok((int)$this->db->val('SELECT COUNT(*) FROM defense_zones') >= 1);
    }

    public function testEveryTableTheSpecListsExists()
    {
        $required = ['combat_modes', 'combat_loadouts', 'defense_zones', 'defense_structures', 'player_defense_structures',
            'city_defense_structures', 'defense_battles', 'hunt_sessions', 'fps_missions', 'fps_mission_results',
            'city_risk_status', 'city_risk_history', 'city_invasion_events', 'city_facility_damage',
            'city_support_requests', 'city_support_contributions', 'combat_equipment', 'equipment_upgrades'];
        foreach ($required as $table) {
            T::ok($this->migrator()->tableExists($table), "테이블이 없음: $table");
        }
    }

    public function testApplyingEveryMigrationFileAgainKeepsExistingDataAndAdminChanges()
    {
        $player = $this->resident('11110', '지킴이');
        $this->give($player, 777);
        // 관리자가 값을 고친 상황
        $this->db->exec("UPDATE combat_equipment SET price_gc = 4321 WHERE code = 'gear_precision_rifle'");
        $this->db->exec("UPDATE regions SET defense_points = 55 WHERE code = '11110'");
        $before = [
            'players' => (int)$this->db->val('SELECT COUNT(*) FROM players'),
            'ledger' => (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries'),
            'policy' => (int)$this->db->val('SELECT COUNT(*) FROM city_risk_policy'),
            'regions' => (int)$this->db->val('SELECT COUNT(*) FROM regions'),
            'gear' => (int)$this->db->val('SELECT COUNT(*) FROM combat_equipment'),
        ];

        foreach ($this->migrator()->files() as $file) {
            foreach (Migrator::split(file_get_contents($file['file'])) as $statement) {
                $this->db->pdo()->exec($statement);
            }
        }

        T::eq($before['players'], (int)$this->db->val('SELECT COUNT(*) FROM players'));
        T::eq($before['ledger'], (int)$this->db->val('SELECT COUNT(*) FROM ledger_entries'));
        T::eq($before['policy'], (int)$this->db->val('SELECT COUNT(*) FROM city_risk_policy'));
        T::eq($before['regions'], (int)$this->db->val('SELECT COUNT(*) FROM regions'));
        T::eq($before['gear'], (int)$this->db->val('SELECT COUNT(*) FROM combat_equipment'));
        T::eq(4321, (int)$this->db->val("SELECT price_gc FROM combat_equipment WHERE code = 'gear_precision_rifle'"));
        T::eq(55, (int)$this->db->val("SELECT defense_points FROM regions WHERE code = '11110'"));
        T::eq(777 + 500, $this->gc($player));
        T::eq('지킴이', $this->fresh($player)['nickname']);
    }

    public function testNoMigrationFileContainsDestructiveStatements()
    {
        foreach ($this->migrator()->files() as $file) {
            T::eq([], Migrator::destructiveStatements(file_get_contents($file['file'])), basename($file['file']));
        }
    }

    public function testDestructiveStatementsAreDetected()
    {
        $bad = [
            'DROP TABLE players',
            'ALTER TABLE players DROP COLUMN nickname',
            'TRUNCATE wallets',
            'DELETE FROM ledger_entries',
            "UPDATE wallets SET balance = 0",
            'ALTER TABLE players MODIFY nickname VARCHAR(4)',
            'RENAME TABLE players TO old_players',
            "REPLACE INTO game_config (config_key, value, updated_at) VALUES ('a', 'b', NOW())",
            "INSERT INTO game_config (config_key, value, updated_at) VALUES ('a','b',NOW()) ON DUPLICATE KEY UPDATE value = 'b'",
        ];
        foreach ($bad as $sql) {
            T::ok(count(Migrator::destructiveStatements($sql)) > 0, "잡지 못함: $sql");
        }
        T::eq([], Migrator::destructiveStatements("-- DROP TABLE x 는 하지 않는다\nCREATE TABLE IF NOT EXISTS a (id INT)"));
    }

    public function testADestructiveMigrationIsRefusedBeforeAnythingIsApplied()
    {
        $dir = sys_get_temp_dir() . '/cg_mig_' . bin2hex(random_bytes(4));
        mkdir($dir);
        file_put_contents("$dir/9001_ok.sql", 'CREATE TABLE IF NOT EXISTS zz_probe_ok (id INT NOT NULL, PRIMARY KEY (id)) ENGINE=InnoDB;');
        file_put_contents("$dir/9002_bad.sql", 'DROP TABLE players;');
        try {
            $error = null;
            try {
                (new Migrator($this->db, $dir))->migrate();
            } catch (RuntimeException $e) {
                $error = $e->getMessage();
            }
            T::ok($error !== null && strpos($error, '9002_bad.sql') !== false, '거절하지 않음');
            // 앞의 멀쩡한 파일도 적용하지 않았다.
            T::ok(!$this->migrator()->tableExists('zz_probe_ok'), '일부가 적용됨');
            T::ok($this->migrator()->tableExists('players'));
        } finally {
            array_map('unlink', glob("$dir/*"));
            rmdir($dir);
        }
    }

    public function testPreflightStopsWhenAnExistingTableHasADifferentStructure()
    {
        // 운영 DB 에 같은 이름의 테이블이 다른 구조로 이미 있는 상황: players 에는 legacy_code 열이 없다.
        $dir = sys_get_temp_dir() . '/cg_mig_' . bin2hex(random_bytes(4));
        mkdir($dir);
        file_put_contents("$dir/9001_probe.sql",
            "CREATE TABLE IF NOT EXISTS players (\n  id BIGINT UNSIGNED NOT NULL,\n  legacy_code VARCHAR(8) NOT NULL,\n  PRIMARY KEY (id)\n) ENGINE=InnoDB;\n"
            . 'CREATE TABLE IF NOT EXISTS zz_probe_new (id INT NOT NULL, PRIMARY KEY (id)) ENGINE=InnoDB;');
        try {
            $migrator = new Migrator($this->db, $dir);
            $problems = $migrator->preflight();
            T::eq(1, count($problems));
            T::ok(strpos($problems[0], 'legacy_code') !== false);
            $thrown = false;
            try {
                $migrator->migrate();
            } catch (RuntimeException $e) {
                $thrown = true;
            }
            T::ok($thrown);
            T::ok(!$this->migrator()->tableExists('zz_probe_new'), '점검에 걸렸는데도 일부가 적용됨');
        } finally {
            array_map('unlink', glob("$dir/*"));
            rmdir($dir);
        }
    }
}
