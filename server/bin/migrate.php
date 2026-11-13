<?php
// DB 마이그레이션 (docs/DATABASE.md 5장).
//
//   php bin/migrate.php                 상태만 본다. 아무것도 바꾸지 않는다.
//   php bin/migrate.php --apply         밀린 마이그레이션을 적용한다 (dev, test 환경).
//   php bin/migrate.php --apply --i-have-approval-and-backup
//                                       staging, production 에서는 이 확인이 더 있어야 적용한다.
//
// - 추가만 한다. DROP·TRUNCATE·열 삭제가 들어 있는 파일은 실행하지 않고 멈춘다.
// - 적용하기 전에 사전 점검을 한다: 이름이 같은데 구조가 다른 기존 테이블이 있으면 아무것도 바꾸지 않고 멈춘다.
// - DB 자체를 만들거나 지우지 않는다. 접속 정보는 server/.env 또는 환경변수에서 읽는다.
// - 운영 DB 에 적용하기 전에는 백업과 승인이 있어야 한다 (docs/SERVER_DEPLOYMENT.md 7장).

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}
if (PHP_INT_SIZE < 8) {
    fwrite(STDERR, "64비트 PHP 가 필요합니다.\n");
    exit(2);
}
mb_internal_encoding('UTF-8');
require __DIR__ . '/../src/autoload.php';

use CG\Infra\Db;
use CG\Infra\Env;
use CG\Infra\Migrator;

$args = array_slice($argv, 1);
$apply = in_array('--apply', $args, true);
$confirmed = in_array('--i-have-approval-and-backup', $args, true);
foreach ($args as $arg) {
    if (!in_array($arg, ['--apply', '--i-have-approval-and-backup', '--status'], true)) {
        fwrite(STDERR, "알 수 없는 옵션: $arg\n");
        exit(2);
    }
}

$env = Env::load(__DIR__ . '/..');
$appEnv = $env->get('APP_ENV', 'production');
try {
    $db = Db::connect($env);
} catch (\Throwable $error) {
    fwrite(STDERR, "DB 에 연결하지 못했습니다. server/.env 의 DB_HOST, DB_PORT, DB_NAME, DB_USER, DB_PASS 를 확인해 주세요.\n");
    exit(1);
}

$migrator = new Migrator($db, __DIR__ . '/../migrations');
echo '환경: ' . $appEnv . ' / DB: ' . $db->val('SELECT DATABASE()') . ' @ ' . $env->get('DB_HOST', '127.0.0.1') . ':' . $env->int('DB_PORT', 3306) . "\n";
echo '적용된 마이그레이션: ' . (implode(', ', $migrator->applied()) ?: '(없음)') . "\n";
$pending = $migrator->pending();
echo '밀린 마이그레이션: ' . (implode(', ', array_map(function ($file) {
    return basename($file['file']);
}, $pending)) ?: '(없음)') . "\n";

$problems = $migrator->preflight();
foreach ($pending as $file) {
    $bad = Migrator::destructiveStatements(file_get_contents($file['file']));
    if ($bad) {
        $problems[] = basename($file['file']) . ' 에 기존 데이터를 지우거나 바꿀 수 있는 문장이 있습니다 (' . implode(', ', $bad) . ').';
    }
}
if ($problems) {
    echo "\n사전 점검에서 문제를 찾았습니다. 아무것도 바꾸지 않았습니다.\n";
    foreach ($problems as $problem) {
        echo "- $problem\n";
    }
    echo "\n기존 테이블의 구조를 확인하고, 필요한 열을 더하는 마이그레이션을 따로 작성해 주세요.\n";
    exit(1);
}
echo "사전 점검: 문제 없음\n";

if (!$apply) {
    echo "\n상태만 확인했습니다. 적용하려면 --apply 를 붙여 주세요.\n";
    exit(0);
}
if (!$pending) {
    echo "\n적용할 것이 없습니다.\n";
    exit(0);
}
if (!in_array($appEnv, ['dev', 'test'], true) && !$confirmed) {
    echo "\n이 환경($appEnv)에서는 백업과 승인을 확인한 뒤 --i-have-approval-and-backup 을 함께 붙여야 적용합니다.\n";
    echo "아무것도 바꾸지 않았습니다.\n";
    exit(1);
}

try {
    $done = $migrator->migrate(function ($line) {
        echo $line . "\n";
    });
} catch (\Throwable $error) {
    fwrite(STDERR, "\n마이그레이션이 멈췄습니다: " . $error->getMessage() . "\n");
    exit(1);
}
echo "\n적용했습니다: " . implode(', ', $done) . "\n";
