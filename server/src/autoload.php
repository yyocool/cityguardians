<?php
// CITY GUARDIANS 서버: 클래스 자동 불러오기. 외부 라이브러리를 쓰지 않는다 (PHP 7.3 ~ 8.3).
// CG\Infra\Db → src/Infra/Db.php

spl_autoload_register(function ($class) {
    if (strncmp($class, 'CG\\', 3) !== 0) {
        return;
    }
    $path = __DIR__ . '/' . str_replace('\\', '/', substr($class, 3)) . '.php';
    if (is_file($path)) {
        require $path;
    }
});
