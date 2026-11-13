<?php
namespace CG\Infra;

/**
 * 마이그레이션 (docs/DATABASE.md 5장).
 *
 * - 앞으로만 진행한다. migrations/NNNN_설명.sql 을 번호순으로 한 번씩 적용하고 schema_migrations 에 기록한다.
 * - 추가만 한다. DROP, TRUNCATE, 열 삭제가 들어 있는 파일은 실행하지 않고 멈춘다 (별도 승인 뒤 사람이 처리).
 * - 적용하기 전에, DB에 이미 있는 테이블이 이 서버가 필요로 하는 열을 가지고 있는지 먼저 확인한다 (preflight).
 *   이름이 같은데 구조가 다른 기존 테이블이 있으면 아무것도 바꾸지 않고 멈춘다. 기존 데이터를 덮어쓰지 않기 위해서다.
 */
final class Migrator
{
    /** @var Db */
    private $db;
    private $dir;

    public function __construct(Db $db, $migrationsDir)
    {
        $this->db = $db;
        $this->dir = rtrim($migrationsDir, '/\\');
    }

    /** @return array<int,array{version:string,file:string}> */
    public function files()
    {
        $files = [];
        foreach (glob($this->dir . '/*.sql') as $path) {
            $name = basename($path);
            if (preg_match('/^(\d{4})_[A-Za-z0-9_]+\.sql$/', $name, $m)) {
                $files[] = ['version' => $m[1], 'file' => $path];
            }
        }
        usort($files, function ($a, $b) {
            return strcmp($a['version'], $b['version']);
        });
        return $files;
    }

    /** @return string[] 이미 적용한 번호. */
    public function applied()
    {
        if (!$this->tableExists('schema_migrations')) {
            return [];
        }
        $rows = $this->db->all('SELECT version FROM schema_migrations ORDER BY version');
        return array_map(function ($row) {
            return (string)$row['version'];
        }, $rows);
    }

    /** @return array<int,array{version:string,file:string}> */
    public function pending()
    {
        $applied = array_flip($this->applied());
        return array_values(array_filter($this->files(), function ($file) use ($applied) {
            return !isset($applied[$file['version']]);
        }));
    }

    public function tableExists($table)
    {
        return $this->db->val(
            'SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = ?',
            [$table]
        ) > 0;
    }

    /** @return array<string,string> [열 이름 => 형식]. 형식은 "bigint unsigned" 처럼 표시 폭을 뗀 소문자다. */
    public function columnTypesOf($table)
    {
        $rows = $this->db->all(
            'SELECT column_name AS name, column_type AS type FROM information_schema.columns'
            . ' WHERE table_schema = DATABASE() AND table_name = ?',
            [$table]
        );
        $types = [];
        foreach ($rows as $row) {
            // MySQL 5.7 과 MariaDB 는 "bigint(20) unsigned", MySQL 8 은 "bigint unsigned" 로 알려 준다.
            $types[strtolower($row['name'])] = preg_replace('/\(\d+\)/', '', strtolower($row['type']));
        }
        return $types;
    }

    /**
     * 이미 있는 테이블이 필요한 열을 가지고 있는지 확인한다.
     * @return string[] 문제 목록. 비어 있으면 진행해도 된다.
     */
    public function preflight()
    {
        $problems = [];
        $requirementsFile = $this->dir . '/requirements.php';
        $requirements = is_file($requirementsFile) ? require $requirementsFile : [];
        // 아직 적용하지 않은 마이그레이션이 만들려는 테이블: 이미 있다면 같은 열을 가지고 있어야 한다.
        foreach ($this->pending() as $file) {
            foreach (self::declaredTables(file_get_contents($file['file'])) as $table => $columns) {
                $requirements[$table] = array_merge(isset($requirements[$table]) ? $requirements[$table] : [], $columns);
            }
        }
        foreach ($requirements as $table => $columns) {
            if (!$this->tableExists($table)) {
                continue; // 없으면 마이그레이션이 새로 만든다.
            }
            $have = $this->columnTypesOf($table);
            $missing = [];
            $mismatched = [];
            foreach ($columns as $spec) {
                // "열" 또는 "열:형식". 형식은 다른 테이블이 그 열을 가리킬 때 서로 맞아야 해서 확인한다.
                $parts = explode(':', $spec, 2);
                $column = strtolower($parts[0]);
                if (!isset($have[$column])) {
                    $missing[] = $parts[0];
                } elseif (isset($parts[1]) && strpos($have[$column], strtolower($parts[1])) !== 0) {
                    $mismatched[] = "{$parts[0]} ({$have[$column]}, 필요: {$parts[1]})";
                }
            }
            if ($missing) {
                $problems[] = "기존 테이블 `$table` 에 필요한 열이 없습니다: " . implode(', ', $missing);
            }
            if ($mismatched) {
                $problems[] = "기존 테이블 `$table` 의 열 형식이 다릅니다: " . implode(', ', $mismatched);
            }
        }
        return $problems;
    }

    /**
     * 마이그레이션이 만드는 테이블과 그 열 이름을 읽어 낸다.
     * @return array<string,string[]> [테이블 => 열 이름들]
     */
    public static function declaredTables($sql)
    {
        $tables = [];
        foreach (self::split($sql) as $statement) {
            if (!preg_match('/^CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?`?(\w+)`?\s*\((.*)\)[^)]*$/is', $statement, $m)) {
                continue;
            }
            $columns = [];
            foreach (preg_split('/\r?\n/', $m[2]) as $line) {
                $line = trim($line);
                if ($line === '' || preg_match('/^(PRIMARY|UNIQUE|KEY|INDEX|CONSTRAINT|FOREIGN|CHECK)\b/i', $line)) {
                    continue;
                }
                if (preg_match('/^`?(\w+)`?\s+[A-Za-z]/', $line, $c)) {
                    $columns[] = $c[1];
                }
            }
            $tables[$m[1]] = $columns;
        }
        return $tables;
    }

    /**
     * 파일에 기존 데이터를 지우거나 덮어쓸 수 있는 문장이 있는지 본다.
     * @return string[] 발견한 문장의 종류.
     */
    public static function destructiveStatements($sql)
    {
        $found = [];
        $plain = self::stripComments($sql);
        $patterns = [
            'DROP' => '/\bDROP\s+(TABLE|DATABASE|SCHEMA|COLUMN|INDEX|VIEW)\b/i',
            'TRUNCATE' => '/\bTRUNCATE\b/i',
            'DELETE' => '/\bDELETE\s+FROM\b/i',
            'ALTER … DROP' => '/\bALTER\s+TABLE\b[^;]*\bDROP\b/i',
            'ALTER … MODIFY/CHANGE' => '/\bALTER\s+TABLE\b[^;]*\b(MODIFY|CHANGE)\b/i',
            'RENAME' => '/\bRENAME\s+TABLE\b/i',
            'REPLACE INTO' => '/\bREPLACE\s+INTO\b/i',
            'UPDATE' => '/^\s*UPDATE\b/im',
            'ON DUPLICATE KEY UPDATE' => '/\bON\s+DUPLICATE\s+KEY\s+UPDATE\b/i',
        ];
        foreach ($patterns as $name => $pattern) {
            if (preg_match($pattern, $plain)) {
                $found[] = $name;
            }
        }
        return $found;
    }

    private static function stripComments($sql)
    {
        $sql = preg_replace('/\/\*.*?\*\//s', '', $sql);
        return preg_replace('/^\s*--.*$/m', '', $sql);
    }

    /** 세미콜론으로 문장을 나눈다. 문자열 안의 세미콜론은 건드리지 않는다. */
    public static function split($sql)
    {
        $statements = [];
        $current = '';
        $quote = null;
        $length = strlen($sql);
        for ($i = 0; $i < $length; $i++) {
            $ch = $sql[$i];
            if ($quote !== null) {
                $current .= $ch;
                if ($ch === '\\' && $i + 1 < $length) {
                    $current .= $sql[++$i];
                } elseif ($ch === $quote) {
                    if ($i + 1 < $length && $sql[$i + 1] === $quote) {
                        $current .= $sql[++$i];
                    } else {
                        $quote = null;
                    }
                }
                continue;
            }
            if ($ch === '-' && $i + 1 < $length && $sql[$i + 1] === '-') {
                while ($i < $length && $sql[$i] !== "\n") {
                    $i++;
                }
                $current .= "\n";
                continue;
            }
            if ($ch === "'" || $ch === '"' || $ch === '`') {
                $quote = $ch;
                $current .= $ch;
                continue;
            }
            if ($ch === ';') {
                if (trim($current) !== '') {
                    $statements[] = trim($current);
                }
                $current = '';
                continue;
            }
            $current .= $ch;
        }
        if (trim($current) !== '') {
            $statements[] = trim($current);
        }
        return $statements;
    }

    /**
     * 밀린 마이그레이션을 적용한다.
     * @return string[] 적용한 번호.
     */
    public function migrate(callable $log = null)
    {
        $problems = $this->preflight();
        if ($problems) {
            throw new \RuntimeException("사전 점검 실패. 아무것도 바꾸지 않았습니다.\n- " . implode("\n- ", $problems));
        }
        $pending = $this->pending();
        // 실행하기 전에 모든 파일을 먼저 검사한다. 하나라도 걸리면 아무것도 적용하지 않는다.
        foreach ($pending as $file) {
            $bad = self::destructiveStatements(file_get_contents($file['file']));
            if ($bad) {
                throw new \RuntimeException(basename($file['file']) . ' 에 기존 데이터를 지우거나 바꿀 수 있는 문장이 있습니다 ('
                    . implode(', ', $bad) . '). 추가만 하는 마이그레이션이 아니므로 실행하지 않았습니다.');
            }
        }

        $this->db->exec('CREATE TABLE IF NOT EXISTS schema_migrations (
            version VARCHAR(16) NOT NULL,
            name VARCHAR(190) NOT NULL,
            applied_at DATETIME NOT NULL,
            PRIMARY KEY (version)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4');

        $done = [];
        foreach ($pending as $file) {
            $name = basename($file['file']);
            if ($log) {
                $log("적용: $name");
            }
            foreach (self::split(file_get_contents($file['file'])) as $statement) {
                $this->db->pdo()->exec($statement);
            }
            $this->db->exec('INSERT INTO schema_migrations (version, name, applied_at) VALUES (?, ?, ?)',
                [$file['version'], $name, Clock::sql()]);
            $done[] = $file['version'];
        }
        return $done;
    }
}
