<?php
namespace CG\Infra;

use PDO;
use PDOException;

/**
 * DB 접근. 모든 SQL은 준비된 문장(바인딩)으로만 실행한다.
 * 시각은 UTC로 저장한다. 날짜 경계(일일 한도)는 한국 시간으로 계산한다 (Clock::koreaDate).
 */
final class Db
{
    /** @var PDO */
    private $pdo;
    private $depth = 0;
    /** @var callable[] */
    private $afterCommit = [];

    public function __construct(PDO $pdo)
    {
        $this->pdo = $pdo;
    }

    public static function connect(Env $env, $withDatabase = true)
    {
        $dsn = 'mysql:host=' . $env->get('DB_HOST', '127.0.0.1') . ';port=' . $env->int('DB_PORT', 3306) . ';charset=utf8mb4';
        if ($withDatabase) {
            $dsn .= ';dbname=' . $env->get('DB_NAME', 'city_guardians_dev');
        }
        $pdo = new PDO($dsn, $env->get('DB_USER', ''), $env->get('DB_PASS', ''), [
            PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
            PDO::ATTR_EMULATE_PREPARES => false,
        ]);
        $pdo->exec("SET time_zone = '+00:00'");
        $pdo->exec("SET NAMES utf8mb4");
        // 격리 수준은 READ COMMITTED 로 쓴다.
        // 이 서버는 "행을 잠근 뒤에 읽고, 계산하고, 쓴다"로 동시 요청을 막는다 (지갑, 도시 기록, 도시 재정, 방어전의 판).
        // MySQL 의 기본값(REPEATABLE READ)에서는 거래 안의 첫 조회 때 본 모습이 끝까지 유지되어서, 잠금을 기다리는 동안
        // 다른 요청이 커밋한 값을 잠금을 얻은 뒤에도 보지 못한다. 그러면 낡은 값으로 계산해 덮어쓰게 된다
        // (예: 여러 주민이 같은 순간에 낸 기금이 한 사람 몫만 남는다). READ COMMITTED 에서는 조회할 때마다 커밋된 최신 값을 본다.
        // tests/ConcurrencyTest.php 가 이것을 확인한다. (binlog_format 이 STATEMENT 인 서버에서는 쓸 수 없다: docs/SERVER_DEPLOYMENT.md)
        $pdo->exec('SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED');
        return new self($pdo);
    }

    public function pdo()
    {
        return $this->pdo;
    }

    private function run($sql, array $params)
    {
        $statement = $this->pdo->prepare($sql);
        $statement->execute($params);
        return $statement;
    }

    /** @return array<int,array<string,mixed>> */
    public function all($sql, array $params = [])
    {
        return $this->run($sql, $params)->fetchAll();
    }

    /** @return array<string,mixed>|null */
    public function one($sql, array $params = [])
    {
        $row = $this->run($sql, $params)->fetch();
        return $row === false ? null : $row;
    }

    public function val($sql, array $params = [])
    {
        $value = $this->run($sql, $params)->fetchColumn();
        return $value === false ? null : $value;
    }

    /** 바뀐 행의 수. */
    public function exec($sql, array $params = [])
    {
        return $this->run($sql, $params)->rowCount();
    }

    /** 새로 들어간 행의 id. */
    public function insert($sql, array $params = [])
    {
        $this->run($sql, $params);
        return (int)$this->pdo->lastInsertId();
    }

    public function inTransaction()
    {
        return $this->depth > 0;
    }

    /**
     * 지금의 거래가 커밋된 뒤에 할 일을 맡긴다. 거래가 취소되면 하지 않는다. 거래 밖이면 바로 한다.
     *
     * 다른 요청이 커밋한 내용까지 보고 다시 세어야 하는 집계(도시 전입자 수)에 쓴다: 거래 안에서 세면
     * 동시에 진행 중인 다른 거래의 변경이 보이지 않아 수가 어긋날 수 있다.
     * 맡긴 일은 이미 커밋된 결과를 되돌릴 수 없으므로, 스스로 오류를 처리해야 한다.
     */
    public function afterCommit(callable $work)
    {
        if ($this->depth === 0) {
            $work();
            return;
        }
        $this->afterCommit[] = $work;
    }

    /**
     * 한 거래로 묶어 실행한다. 교착(deadlock)이면 처음부터 다시 시도한다.
     * 이미 거래 안이면 그 거래에 합류한다.
     */
    public function tx(callable $work)
    {
        if ($this->depth > 0) {
            return $work($this);
        }
        $attempt = 0;
        while (true) {
            $attempt++;
            $this->pdo->beginTransaction();
            $this->depth = 1;
            $this->afterCommit = [];
            try {
                $result = $work($this);
                $this->pdo->commit();
                $this->depth = 0;
                $queued = $this->afterCommit;
                $this->afterCommit = [];
                foreach ($queued as $later) {
                    $later();
                }
                return $result;
            } catch (\Throwable $error) {
                $this->depth = 0;
                $this->afterCommit = [];
                if ($this->pdo->inTransaction()) {
                    $this->pdo->rollBack();
                }
                $deadlock = $error instanceof PDOException
                    && (isset($error->errorInfo[1]) && in_array((int)$error->errorInfo[1], [1213, 1205], true));
                if ($deadlock && $attempt < 3) {
                    usleep(20000 * $attempt);
                    continue;
                }
                throw $error;
            }
        }
    }
}
