<?php
// 초기 데이터 마이그레이션(0003, 0004)을 만든다. 개발할 때만 쓰는 도구다.
//
// 값의 출처는 클라이언트의 초기 콘텐츠다: tools/check/vectors 가 클라이언트 코드(DefaultDefs, KrRegions)에서 뽑아
// tests/fixtures/defs.json, regions.json 에 써 둔 것을 읽는다. 손으로 옮겨 적지 않으므로 서버와 클라이언트의 초기값이 어긋나지 않는다.
//
//   php bin/gen-seed.php
//
// 이미 운영 DB 에 적용한 마이그레이션 파일은 다시 만들지 않는다. 값이 바뀌면 새 번호의 마이그레이션을 만든다.

if (PHP_SAPI !== 'cli') {
    exit(1);
}
$root = dirname(__DIR__);
$defs = json_decode(file_get_contents($root . '/tests/fixtures/defs.json'), true);
$regions = json_decode(file_get_contents($root . '/tests/fixtures/regions.json'), true);
if (!is_array($defs) || !is_array($regions)) {
    fwrite(STDERR, "tests/fixtures 의 defs.json, regions.json 을 읽을 수 없습니다. tools/check/vectors 를 먼저 실행하세요.\n");
    exit(1);
}

function q($value)
{
    if ($value === null) {
        return 'NULL';
    }
    if (is_bool($value)) {
        return $value ? '1' : '0';
    }
    if (is_int($value)) {
        return (string)$value;
    }
    if (is_float($value)) {
        return rtrim(rtrim(number_format($value, 3, '.', ''), '0'), '.');
    }
    $text = (string)$value;
    if (strpos($text, '\\') !== false) {
        throw new RuntimeException('초기 데이터에 역슬래시가 있습니다: ' . $text);
    }
    return "'" . str_replace("'", "''", $text) . "'";
}

function j($value)
{
    return json_encode($value, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
}

/** 여러 행을 넣는 INSERT IGNORE 문장. 이미 같은 코드의 행이 있으면 그 행은 건드리지 않는다. */
function insertIgnore($table, array $columns, array $rows)
{
    $lines = [];
    foreach ($rows as $row) {
        $lines[] = '  (' . implode(', ', array_map('q', $row)) . ', UTC_TIMESTAMP())';
    }
    return "INSERT IGNORE INTO $table\n  (" . implode(', ', $columns) . ", updated_at)\nVALUES\n" . implode(",\n", $lines) . ";\n";
}

$out = [];
$out[] = "-- CITY GUARDIANS 0003: v8.0 전투 콘텐츠의 초기값.";
$out[] = "--";
$out[] = "-- bin/gen-seed.php 가 클라이언트의 초기 콘텐츠(tests/fixtures/defs.json)에서 만든 파일이다. 손으로 고치지 않는다.";
$out[] = "-- INSERT IGNORE 만 쓴다: 이미 있는 행(관리자가 고친 값 포함)은 바꾸지 않는다.";
$out[] = "-- 수치는 초기값이며 플레이테스트로 조정한다. 조정은 관리자 화면에서 한다.";
$out[] = "";

// 전투 모드
$rows = [];
foreach ($defs['combat']['modes'] as $m) {
    $rows[] = [$m['code'], $m['name'], $m['summary'], $m['camera'], $m['controls'], $m['closed'], $m['sortOrder']];
}
$out[] = insertIgnore('combat_modes', ['code', 'name', 'summary', 'camera', 'controls', 'is_closed', 'sort_order'], $rows);

// 전투 장비
$rows = [];
foreach ($defs['combat']['gear'] as $i => $g) {
    $rows[] = [
        $g['code'], $g['name'], $g['description'], $g['slot'], $g['kind'], implode(',', $g['modes']),
        $g['priceGc'], $g['unlockLevel'], $g['maxLevel'], $g['upgradeBaseGc'], $g['upkeepGc'],
        $g['damage'], (float)$g['attacksPerSecond'], (float)$g['range'], (float)$g['projectileSpeed'],
        $g['clipSize'], (float)$g['reloadSeconds'], (float)$g['aimZoom'], (float)$g['coneDegrees'], (float)$g['knockback'],
        (float)$g['cooldownSeconds'], (float)$g['effectSeconds'], (float)$g['effectRadius'], $g['effectValue'],
        $g['comingSoon'], ($i + 1) * 10,
    ];
}
$out[] = insertIgnore('combat_equipment', [
    'code', 'name', 'description', 'slot', 'kind', 'modes',
    'price_gc', 'unlock_level', 'max_level', 'upgrade_base_gc', 'upkeep_gc',
    'damage', 'attacks_per_second', 'range_m', 'projectile_speed',
    'clip_size', 'reload_seconds', 'aim_zoom', 'cone_degrees', 'knockback',
    'cooldown_seconds', 'effect_seconds', 'effect_radius', 'effect_value',
    'is_coming_soon', 'sort_order',
], $rows);

// 적
$rows = [];
foreach ($defs['enemies'] as $i => $e) {
    $rows[] = [
        $e['code'], $e['name'], $e['maxHp'], $e['damage'], (float)$e['moveSpeed'], (float)$e['detectRange'],
        (float)$e['attackRange'], (float)$e['attackWindup'], (float)$e['attackInterval'], (float)$e['loseRange'],
        $e['rewardGc'], $e['rewardXp'], $e['baseDamage'], $e['rewardEnergy'], (float)$e['aggroSeconds'],
        $e['attacksStructures'], (float)$e['strikeRadius'], $e['structureDamage'], $e['staggerImmune'], ($i + 1) * 10,
    ];
}
$out[] = insertIgnore('enemy_defs', [
    'code', 'name', 'max_hp', 'damage', 'move_speed', 'detect_range',
    'attack_range', 'attack_windup', 'attack_interval', 'lose_range',
    'reward_gc', 'reward_xp', 'base_damage', 'reward_energy', 'aggro_seconds',
    'attacks_structures', 'strike_radius', 'structure_damage', 'stagger_immune', 'sort_order',
], $rows);

// 방어 구역
$rows = [];
foreach ($defs['stages'] as $i => $s) {
    $rows[] = [
        $s['code'], $s['name'], $s['baseHp'], $s['startEnergy'], $s['waveClearEnergy'], (float)$s['restSeconds'],
        j($s['waves']), $s['winGc'], $s['firstWinBonusGc'], $s['winXp'], $s['loseGcPerWave'],
        (float)$s['minSecondsPerWave'], $s['winDefensePoints'], 1, ($i + 1) * 10,
    ];
}
$out[] = insertIgnore('defense_zones', [
    'code', 'name', 'base_hp', 'start_energy', 'wave_clear_energy', 'rest_seconds',
    'waves', 'win_gc', 'first_win_bonus_gc', 'win_xp', 'lose_gc_per_wave',
    'min_seconds_per_wave', 'win_defense_points', 'unlock_level', 'sort_order',
], $rows);

// 타워와 방어시설 15종
$rows = [];
foreach ($defs['towers'] as $i => $t) {
    $rows[] = [
        $t['code'], $t['name'], $t['description'], $t['tier'], $t['kind'], $t['ownership'],
        $t['unlockGc'], $t['energyCost'], $t['damage'], (float)$t['shotsPerSecond'], (float)$t['range'],
        (float)$t['splashRadius'], $t['canHitAir'], $t['maxHp'], $t['upkeepGc'], $t['upgradeEnergyCost'],
        $t['maxUpgradeLevel'], $t['maxPerBattle'], $t['unlockLevel'], $t['effectValue'], (float)$t['effectSeconds'],
        $t['cityGoalGc'], $t['cityPower'], $t['cityMaxLevel'], $t['comingSoon'], ($i + 1) * 10,
    ];
}
$out[] = insertIgnore('defense_structures', [
    'code', 'name', 'description', 'tier', 'kind', 'ownership',
    'unlock_gc', 'energy_cost', 'damage', 'shots_per_second', 'range_m',
    'splash_radius', 'can_hit_air', 'max_hp', 'upkeep_gc', 'upgrade_energy_cost',
    'max_upgrade_level', 'max_per_battle', 'unlock_level', 'effect_value', 'effect_seconds',
    'city_goal_gc', 'city_power', 'city_max_level', 'is_coming_soon', 'sort_order',
], $rows);

// 사냥 구역
$rows = [];
foreach ($defs['zones'] as $i => $z) {
    $rows[] = [
        $z['code'], $z['name'], implode(',', $z['enemyCodes']), $z['maxAlive'], (float)$z['respawnSeconds'],
        (float)$z['maxKillsPerMinute'], $z['burstAllowance'], ($i + 1) * 10,
    ];
}
$out[] = insertIgnore('hunt_zone_defs', [
    'code', 'name', 'enemy_codes', 'max_alive', 'respawn_seconds', 'max_kills_per_minute', 'burst_allowance', 'sort_order',
], $rows);

// FPS 임무
$rows = [];
foreach ($defs['combat']['fpsMissions'] as $i => $m) {
    $rows[] = [
        $m['code'], $m['name'], $m['description'], j($m['groups']), $m['facilityCount'], $m['facilityHp'],
        $m['civilianCount'], (float)$m['timeLimitSeconds'], (float)$m['minSeconds'], $m['winGc'], $m['firstWinBonusGc'],
        $m['winXp'], $m['civilianBonusGc'], $m['facilityBonusGc'], $m['winDefensePoints'], $m['unlockLevel'], ($i + 1) * 10,
    ];
}
$out[] = insertIgnore('fps_missions', [
    'code', 'name', 'description', 'enemy_groups', 'facility_count', 'facility_hp',
    'civilian_count', 'time_limit_seconds', 'min_seconds', 'win_gc', 'first_win_bonus_gc',
    'win_xp', 'civilian_bonus_gc', 'facility_bonus_gc', 'win_defense_points', 'unlock_level', 'sort_order',
], $rows);

// 도시의 주요 시설
$rows = [];
foreach ($defs['combat']['facilities'] as $i => $f) {
    $rows[] = [$f['code'], $f['name'], $f['repairGc'], $f['maxDamage'], ($i + 1) * 10];
}
$out[] = insertIgnore('city_facility_defs', ['code', 'name', 'repair_gc', 'max_damage', 'sort_order'], $rows);

// 설정 값
$config = [
    'economy' => [
        'dailyHuntGcLimit' => $defs['economy']['dailyHuntGcLimit'],
        'residencyBonusGc' => $defs['economy']['residencyBonusGc'],
    ],
    'progression' => $defs['progression'],
    'combat' => ['dailyFpsRewardLimit' => $defs['combat']['dailyFpsRewardLimit']],
];
$rows = [];
foreach ($config as $key => $value) {
    $rows[] = [$key, j($value)];
}
$out[] = insertIgnore('game_config', ['config_key', 'value'], $rows);

// 위험도 계산 정책: 아직 하나도 없을 때만 초기 정책을 넣는다.
$policy = $defs['combat']['risk'];
$weights = [];
foreach ($policy['weights'] as $w) {
    $weights[$w['reason']] = $w['deltaCenti'];
}
$policy['weights'] = $weights;
$out[] = "INSERT INTO city_risk_policy (policy, ranking, admin_id, reason, created_at)\n"
    . 'SELECT ' . q(j($policy)) . ",\n       " . q(j($defs['combat']['ranking'])) . ", NULL, '초기 정책', UTC_TIMESTAMP()\n"
    . "FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM city_risk_policy);\n";

// 침공 판정의 진행 상태. 씨앗은 클라이언트의 모의 서버와 같은 값이다.
$out[] = "INSERT IGNORE INTO city_world_state (id, last_tick, seed, updated_at) VALUES (1, -1, 20261113, UTC_TIMESTAMP());\n";

file_put_contents($root . '/migrations/0003_seed_combat_v8.sql', implode("\n", $out));

// ── 지역 ──
$out = [];
$out[] = "-- CITY GUARDIANS 0004: 시·도와 시·군·구 목록 (기준일 {$regions['baseDate']}).";
$out[] = "--";
$out[] = "-- bin/gen-seed.php 가 클라이언트의 지역 목록(tests/fixtures/regions.json)에서 만든 파일이다. 손으로 고치지 않는다.";
$out[] = "-- 목록이 비어 있을 때만 넣는다. 지역 목록이 이미 있는 DB(운영)에서는 아무것도 넣지 않는다:";
$out[] = "-- 기존 목록의 코드가 이 목록과 같은지 확인하기 전에는 섞지 않기 위해서다 (docs/DATABASE.md 0장).";
$out[] = "";
$lines = [];
foreach ($regions['sidos'] as $i => $s) {
    $lines[] = '  SELECT ' . q($s['code']) . ($i === 0 ? ' AS code' : '') . ', ' . q($s['name']) . ($i === 0 ? ' AS name' : '')
        . ', ' . q($s['shortName']) . ($i === 0 ? ' AS short_name' : '');
}
$out[] = "INSERT INTO sidos (code, name, short_name)\nSELECT t.code, t.name, t.short_name FROM (\n"
    . implode(" UNION ALL\n", $lines) . "\n) t\nWHERE NOT EXISTS (SELECT 1 FROM sidos);\n";

$lines = [];
foreach ($regions['regions'] as $i => $r) {
    $lines[] = '  SELECT ' . q($r['sidoCode']) . ($i === 0 ? ' AS sido_code' : '') . ', ' . q($r['code']) . ($i === 0 ? ' AS code' : '')
        . ', ' . q($r['name']) . ($i === 0 ? ' AS name' : '');
}
$out[] = "INSERT INTO regions (sido_id, code, name)\nSELECT s.id, t.code, t.name FROM (\n"
    . implode(" UNION ALL\n", $lines) . "\n) t\nJOIN sidos s ON s.code = t.sido_code\nWHERE NOT EXISTS (SELECT 1 FROM regions)\nORDER BY t.code;\n";

file_put_contents($root . '/migrations/0004_seed_regions.sql', implode("\n", $out));

echo "0003_seed_combat_v8.sql, 0004_seed_regions.sql 을 만들었습니다.\n";
