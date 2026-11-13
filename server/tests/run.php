<?php
// 서버 테스트 실행기.
//
//   php tests/run.php            모든 테스트
//   php tests/run.php Rules      이름에 "Rules" 가 들어간 테스트만
//
// DB 가 필요한 테스트는 CG_TEST_DB_HOST / PORT / USER / PASS / NAME 환경변수가 있을 때만 돈다.
// 없으면 "건너뜀"으로 표시하고 실패로 치지 않는다 — 대신 건너뛴 수를 끝에 분명히 적는다.
// 테스트용 DB 는 이름이 _test 로 끝나야 한다. 테스트가 그 안의 테이블을 지우고 다시 만들기 때문이다.

if (PHP_INT_SIZE < 8) {
    fwrite(STDERR, "64비트 PHP 가 필요합니다.\n");
    exit(2);
}
error_reporting(E_ALL);
ini_set('display_errors', '1');
mb_internal_encoding('UTF-8');

require __DIR__ . '/../src/autoload.php';
require __DIR__ . '/lib.php';
require __DIR__ . '/DbTestCase.php';
require __DIR__ . '/BenefitTestCase.php';
require __DIR__ . '/CityTestCase.php';

$filter = isset($argv[1]) ? $argv[1] : '';
$files = glob(__DIR__ . '/*Test.php');
sort($files);

$passed = 0;
$failed = 0;
$skipped = 0;
$failures = [];
$started = microtime(true);

foreach ($files as $file) {
    $class = basename($file, '.php');
    if ($filter !== '' && stripos($class, $filter) === false) {
        continue;
    }
    require $file;
    $methods = array_filter(get_class_methods($class), function ($name) {
        return strncmp($name, 'test', 4) === 0;
    });
    foreach ($methods as $method) {
        $case = new $class();
        try {
            if (method_exists($case, 'setUp')) {
                $case->setUp();
            }
            $case->$method();
            $passed++;
            echo "  통과  $class::$method\n";
        } catch (TestSkipped $skip) {
            $skipped++;
            echo "  건너뜀 $class::$method — {$skip->getMessage()}\n";
        } catch (\Throwable $error) {
            $failed++;
            $where = basename($error->getFile()) . ':' . $error->getLine();
            $failures[] = "$class::$method\n      " . get_class($error) . ': ' . $error->getMessage() . " ($where)";
            echo "  실패  $class::$method\n";
        }
        try {
            if (method_exists($case, 'tearDown')) {
                $case->tearDown();
            }
        } catch (\Throwable $error) {
            echo "  (정리 중 오류: {$error->getMessage()})\n";
        }
        // 테스트 객체는 App 과 서비스가 서로를 가리키고 있어 저절로 치워지지 않는다. 그대로 두면 테스트마다 연
        // DB 연결이 끝까지 남아 연결 한도를 넘는다. 여기서 놓고, 서로 가리키는 것들을 수거해 연결을 닫는다.
        $case = null;
        $error = null;
        gc_collect_cycles();
    }
}

echo "\n";
foreach ($failures as $failure) {
    echo "실패: $failure\n";
}
$seconds = number_format(microtime(true) - $started, 1);
echo "통과 $passed, 실패 $failed, 건너뜀 $skipped (확인 " . T::$checks . "개, {$seconds}초, PHP " . PHP_VERSION . ")\n";
exit($failed > 0 ? 1 : 0);
