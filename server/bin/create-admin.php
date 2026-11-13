<?php
// 관리자 계정을 만든다. 웹에서 가입하는 기능은 없고, 서버에서 이 도구로만 만든다 (docs/ADMIN_SYSTEM.md 2장).
//
//   php bin/create-admin.php <아이디> <역할>
//
//   역할: super(최고 관리자), operator(운영자), balance(밸런스 담당), support(고객지원), viewer(조회 전용)
//
// 비밀번호는 물어본다 (명령행 인자로 받지 않는다: 명령 기록에 남기 때문이다).
// 다른 프로그램에서 부를 때는 CG_ADMIN_PASSWORD 환경변수로 넘길 수 있다.
// 일회용 코드(TOTP)의 비밀값은 이때 한 번만 보여 준다. 일회용 코드 앱에 등록해 두어야 로그인할 수 있다.

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}
mb_internal_encoding('UTF-8');
require __DIR__ . '/../src/autoload.php';

use CG\Admin\Auth;
use CG\Infra\Db;
use CG\Infra\Env;

if ($argc !== 3) {
    fwrite(STDERR, "쓰는 법: php bin/create-admin.php <아이디> <역할>\n역할: " . implode(', ', array_keys(Auth::roles())) . "\n");
    exit(2);
}
$password = getenv('CG_ADMIN_PASSWORD');
if ($password === false || $password === '') {
    fwrite(STDOUT, '비밀번호 (' . Auth::MIN_PASSWORD . "자 이상, 대문자·소문자·숫자·기호 가운데 세 가지 이상): ");
    $password = rtrim((string)fgets(STDIN), "\r\n");
}

$env = Env::load(__DIR__ . '/..');
try {
    $db = Db::connect($env);
} catch (\Throwable $error) {
    fwrite(STDERR, "DB 에 연결하지 못했습니다.\n");
    exit(1);
}
try {
    $admin = (new Auth(new CG\App($env, $db)))->create($argv[1], $password, $argv[2]);
} catch (\InvalidArgumentException $error) {
    fwrite(STDERR, $error->getMessage() . "\n");
    exit(1);
}
echo "관리자 계정을 만들었습니다: {$admin['login']} ({$admin['role']})\n\n";
echo "일회용 코드 앱에 아래 값을 등록해 주세요. 다시 보여 주지 않습니다.\n";
echo "  비밀값: {$admin['totpSecret']}\n";
echo "  주소:   {$admin['totpUri']}\n";
