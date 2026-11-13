<?php
// 장학 신청서의 개인정보를 암호화할 키를 새로 만든다 (docs/PRIVACY_DATA_FLOW.md 4장).
//
//   php bin/new-data-key.php
//
// 나온 값을 서버의 .env 에 DATA_KEY= 로 넣는다. 저장소·문서·채팅에 적지 않는다.
// 이미 신청서가 저장된 서버에서 키를 바꾸면 앞의 신청서를 읽을 수 없다: 그때는 키를 바꾸지 말고 운영 절차를 따른다.

if (PHP_SAPI !== 'cli') {
    http_response_code(404);
    exit;
}
require __DIR__ . '/../src/autoload.php';

echo CG\Infra\Crypto::newKey() . "\n";
