<?php
namespace CG\Admin;

use CG\App;
use CG\Domain\Gear;
use CG\Infra\Clock;

/**
 * 정의 데이터 고치기 (docs/ADMIN_SYSTEM.md 6장).
 *
 * 1) 값의 형식과 범위를 검사한다  2) 사유를 받는다  3) 저장하면서 변경 전후·관리자·사유를 config_history 에,
 * 누가 무엇을 했는지를 admin_audit_logs 에 같은 거래로 남긴다  4) 이력에서 이전 값으로 되돌릴 수 있다.
 * 테이블과 열 이름은 Catalog 에서만 나온다. 값은 모두 바인딩으로 넣는다.
 */
final class DefEditor
{
    /** @var App */
    private $app;
    /** @var Auth */
    private $auth;

    public function __construct(App $app, Auth $auth)
    {
        $this->app = $app;
        $this->auth = $auth;
    }

    public function rows(array $screen)
    {
        return $this->app->db->all('SELECT * FROM `' . $screen['table'] . '` ORDER BY sort_order, id');
    }

    public function row(array $screen, $code)
    {
        return is_string($code) && preg_match('/^[A-Za-z0-9_\-]{1,40}$/', $code)
            ? $this->app->db->one('SELECT * FROM `' . $screen['table'] . '` WHERE code = ?', [$code]) : null;
    }

    public static function reasonProblem($reason)
    {
        $length = mb_strlen(trim((string)$reason));
        if ($length < 2) {
            return '변경 사유를 적어 주세요.';
        }
        return $length > 255 ? '변경 사유가 너무 깁니다 (255자까지).' : null;
    }

    // ── 값 하나의 검사 ──────────────────────────────────────

    /**
     * 입력 하나를 그 열의 형식으로 바꾼다.
     * @return array [값, 문제(없으면 null)]
     */
    public function parse(array $field, $input)
    {
        $label = $field['label'];
        switch ($field['type']) {
            case 'bool':
                return [$input === '1' || $input === 'on' || $input === true || $input === 1 ? 1 : 0, null];
            case 'int':
                if (!is_string($input) || !preg_match('/^-?\d{1,12}$/', trim($input))) {
                    return [null, "$label: 정수를 넣어 주세요."];
                }
                $value = (int)trim($input);
                if ($value < $field['min'] || $value > $field['max']) {
                    return [null, "$label: {$field['min']} ~ {$field['max']} 사이여야 합니다."];
                }
                return [$value, null];
            case 'decimal':
                if (!is_string($input) || !preg_match('/^-?\d{1,6}(\.\d{1,3})?$/', trim($input))) {
                    return [null, "$label: 숫자를 넣어 주세요 (소수 셋째 자리까지)."];
                }
                $value = (float)trim($input);
                if ($value < $field['min'] || $value > $field['max']) {
                    return [null, "$label: {$field['min']} ~ {$field['max']} 사이여야 합니다."];
                }
                return [number_format($value, 3, '.', ''), null];
            case 'select':
                if (!is_string($input) || !isset($field['options'][$input])) {
                    return [null, "$label: 고를 수 없는 값입니다."];
                }
                return [$input, null];
            case 'text':
                if (!is_string($input)) {
                    return [null, "$label: 글자를 넣어 주세요."];
                }
                $value = trim($input);
                if (preg_match('/[\x00-\x1F\x7F]/', $value)) {
                    return [null, "$label: 쓸 수 없는 문자가 들어 있습니다."];
                }
                if ($value === '' && !empty($field['required'])) {
                    return [null, "$label: 비워 둘 수 없습니다."];
                }
                if (mb_strlen($value) > $field['max']) {
                    return [null, "$label: {$field['max']}자까지 쓸 수 있습니다."];
                }
                return [$value, null];
            case 'json':
                if (!is_string($input) || strlen($input) > 20000) {
                    return [null, "$label: 내용이 너무 깁니다."];
                }
                $decoded = json_decode($input, true);
                if (!is_array($decoded)) {
                    return [null, "$label: JSON 으로 읽을 수 없습니다."];
                }
                $problem = $field['validator'] === 'waves' ? $this->wavesProblem($decoded) : $this->groupsProblem($decoded);
                if ($problem !== null) {
                    return [null, "$label: $problem"];
                }
                return [json_encode($decoded, JSON_UNESCAPED_UNICODE), null];
        }
        return [null, "$label: 알 수 없는 형식입니다."];
    }

    private static function isList(array $value)
    {
        return $value === [] || array_keys($value) === range(0, count($value) - 1);
    }

    /** 적 묶음 목록의 문제. 맞으면 null. 값을 정리해 되돌려 넣는다. */
    private function groupsProblem(array &$groups)
    {
        if (!self::isList($groups) || count($groups) < 1 || count($groups) > 20) {
            return '적 묶음은 1 ~ 20개의 목록이어야 합니다.';
        }
        $enemies = $this->app->defs()->enemies();
        $clean = [];
        foreach ($groups as $group) {
            if (!is_array($group) || !isset($group['enemyCode'], $group['count']) || !is_string($group['enemyCode'])) {
                return '적 묶음마다 enemyCode 와 count 가 있어야 합니다.';
            }
            if (!isset($enemies[$group['enemyCode']])) {
                return '없거나 꺼진 적입니다: ' . mb_substr($group['enemyCode'], 0, 40);
            }
            if (!is_int($group['count']) || $group['count'] < 1 || $group['count'] > 500) {
                return 'count 는 1 ~ 500 사이의 정수여야 합니다.';
            }
            $interval = isset($group['interval']) ? $group['interval'] : 0;
            $delay = isset($group['delay']) ? $group['delay'] : 0;
            if (!(is_int($interval) || is_float($interval)) || $interval < 0 || $interval > 60) {
                return 'interval 은 0 ~ 60 사이의 숫자여야 합니다.';
            }
            if (!(is_int($delay) || is_float($delay)) || $delay < 0 || $delay > 600) {
                return 'delay 는 0 ~ 600 사이의 숫자여야 합니다.';
            }
            $clean[] = ['enemyCode' => $group['enemyCode'], 'count' => $group['count'], 'interval' => $interval, 'delay' => $delay];
        }
        $groups = $clean;
        return null;
    }

    private function wavesProblem(array &$waves)
    {
        if (!self::isList($waves) || count($waves) < 1 || count($waves) > 30) {
            return '웨이브는 1 ~ 30개의 목록이어야 합니다.';
        }
        $clean = [];
        $total = 0;
        foreach ($waves as $i => $wave) {
            if (!is_array($wave) || !isset($wave['groups']) || !is_array($wave['groups'])) {
                return '웨이브마다 groups 가 있어야 합니다.';
            }
            $groups = $wave['groups'];
            $problem = $this->groupsProblem($groups);
            if ($problem !== null) {
                return '웨이브 ' . ($i + 1) . ': ' . $problem;
            }
            foreach ($groups as $group) {
                $total += $group['count'];
            }
            $clean[] = ['groups' => $groups];
        }
        if ($total > 5000) {
            return '적의 수가 너무 많습니다 (5000까지).';
        }
        $waves = $clean;
        return null;
    }

    // ── 값들 사이의 검사 ────────────────────────────────────

    /** 고친 뒤의 행이 다른 정의와 어긋나지 않는지 본다. 문제 목록을 돌려준다. */
    private function crossProblems(array $screen, array $before, array $after)
    {
        $db = $this->app->db;
        $problems = [];
        $table = $screen['table'];
        $turnedOff = isset($after['is_active']) && (int)$before['is_active'] === 1 && (int)$after['is_active'] === 0;

        if ($table === 'fps_missions' && (float)$after['min_seconds'] > (float)$after['time_limit_seconds']) {
            $problems[] = '최소 시간은 제한 시간보다 클 수 없습니다.';
        }

        if ($table === 'enemy_defs' && $turnedOff) {
            // 쓰이고 있는 적은 끌 수 없다.
            $needle = '%"enemyCode":"' . $before['code'] . '"%';
            $used = $db->val('SELECT COUNT(*) FROM defense_zones WHERE is_active = 1 AND waves LIKE ?', [$needle])
                + $db->val('SELECT COUNT(*) FROM fps_missions WHERE is_active = 1 AND enemy_groups LIKE ?', [$needle])
                + $db->val('SELECT COUNT(*) FROM hunt_zone_defs WHERE is_active = 1 AND FIND_IN_SET(?, enemy_codes) > 0', [$before['code']]);
            if ($used > 0) {
                $problems[] = '이 적이 나오는 방어 구역·FPS 임무·사냥 구역이 있어 끌 수 없습니다. 먼저 그쪽에서 빼 주세요.';
            }
        }

        if ($table === 'combat_equipment') {
            // 무료 기본 장비로도 모든 핵심 전투에 참여할 수 있어야 한다:
            // 지금 무료 기본 장비가 있는 (전투 모드, 칸)에는 고친 뒤에도 하나가 남아야 한다.
            $rows = $db->all('SELECT * FROM combat_equipment');
            $free = function (array $list) {
                $pairs = [];
                foreach ($list as $row) {
                    if ((int)$row['is_active'] !== 1 || (int)$row['is_coming_soon'] === 1 || (int)$row['price_gc'] > 0
                        || (int)$row['unlock_level'] > 1) {
                        continue;
                    }
                    foreach (array_filter(array_map('trim', explode(',', $row['modes'])), 'strlen') as $mode) {
                        $pairs[$mode . ':' . $row['slot']] = true;
                    }
                }
                return $pairs;
            };
            $had = $free($rows);
            foreach ($rows as $i => $row) {
                if ($row['code'] === $before['code']) {
                    $rows[$i] = $after;
                }
            }
            $lost = array_diff_key($had, $free($rows));
            if ($lost) {
                $names = [];
                foreach (array_keys($lost) as $pair) {
                    list($mode, $slot) = explode(':', $pair);
                    $names[] = $mode . ' ' . Gear::slotName($slot);
                }
                $problems[] = '무료 기본 장비가 없어지는 칸이 생깁니다 (' . implode(', ', $names)
                    . '). 무료 기본 장비로도 모든 전투에 참여할 수 있어야 합니다.';
            }
        }

        if ($table === 'defense_structures') {
            // 처음부터 무료로 세울 수 있는 타워가 적어도 하나는 남아야 한다.
            $rows = $db->all('SELECT * FROM defense_structures');
            $count = 0;
            foreach ($rows as $row) {
                if ($row['code'] === $before['code']) {
                    $row = $after;
                }
                if ((int)$row['is_active'] === 1 && (int)$row['is_coming_soon'] === 0 && $row['ownership'] !== 'city'
                    && (int)$row['unlock_gc'] <= 0 && (int)$row['unlock_level'] <= 1 && (int)$row['damage'] > 0) {
                    $count++;
                }
            }
            if ($count < 1) {
                $problems[] = '처음부터 무료로 세울 수 있는 공격 타워가 하나는 있어야 합니다.';
            }
            if ($after['ownership'] === 'city' && (int)$after['city_goal_gc'] > 0 && (int)$after['city_max_level'] < 1) {
                $problems[] = '도시 공용 시설은 최대 단계가 1 이상이어야 합니다.';
            }
        }
        return $problems;
    }

    // ── 저장 ────────────────────────────────────────────────

    /**
     * 폼 입력을 검사하고 저장한다.
     * @return array ok(bool), problems(string[]), changed(바뀐 열의 수), values(화면에 다시 보여 줄 값)
     */
    public function save(array $admin, array $screen, $code, array $form, $ip)
    {
        $before = $this->row($screen, $code);
        if ($before === null) {
            return ['ok' => false, 'problems' => ['없는 항목입니다.'], 'changed' => 0, 'values' => []];
        }
        $problems = [];
        $values = [];
        foreach ($screen['fields'] as $column => $field) {
            $input = isset($form[$column]) ? $form[$column] : ($field['type'] === 'bool' ? '0' : null);
            list($value, $problem) = $this->parse($field, $input);
            if ($problem !== null) {
                $problems[] = $problem;
                $values[$column] = is_string($input) ? $input : '';
            } else {
                $values[$column] = $value;
            }
        }
        $reason = isset($form['reason']) && is_string($form['reason']) ? trim($form['reason']) : '';
        $reasonProblem = self::reasonProblem($reason);
        if ($reasonProblem !== null) {
            $problems[] = $reasonProblem;
        }
        if ($problems) {
            return ['ok' => false, 'problems' => $problems, 'changed' => 0, 'values' => $values];
        }
        return $this->apply($admin, $screen, $before, $values, $reason, $ip);
    }

    private static function same($column, array $field, $old, $new)
    {
        if ($field['type'] === 'decimal') {
            return abs((float)$old - (float)$new) < 0.0005;
        }
        if ($field['type'] === 'json') {
            return json_decode((string)$old, true) == json_decode((string)$new, true);
        }
        return (string)$old === (string)$new;
    }

    private function apply(array $admin, array $screen, array $before, array $values, $reason, $ip)
    {
        $changedBefore = [];
        $changedAfter = [];
        foreach ($values as $column => $value) {
            if (!self::same($column, $screen['fields'][$column], $before[$column], $value)) {
                $changedBefore[$column] = $before[$column];
                $changedAfter[$column] = $value;
            }
        }
        if (!$changedAfter) {
            return ['ok' => true, 'problems' => [], 'changed' => 0, 'values' => $values];
        }
        $problems = $this->crossProblems($screen, $before, $changedAfter + $before);
        if ($problems) {
            return ['ok' => false, 'problems' => $problems, 'changed' => 0, 'values' => $values];
        }

        $db = $this->app->db;
        $db->tx(function () use ($db, $admin, $screen, $before, $changedBefore, $changedAfter, $reason, $ip) {
            $sets = [];
            $params = [];
            foreach ($changedAfter as $column => $value) {
                $sets[] = '`' . $column . '` = ?';   // 열 이름은 Catalog 에서 온 것이다
                $params[] = $value;
            }
            $sets[] = 'updated_at = ?';
            $params[] = Clock::sql();
            $params[] = $before['id'];
            $db->exec('UPDATE `' . $screen['table'] . '` SET ' . implode(', ', $sets) . ' WHERE id = ?', $params);
            $db->exec(
                'INSERT INTO config_history (target_type, target_key, before_value, after_value, admin_id, reason, created_at)'
                . ' VALUES (?, ?, ?, ?, ?, ?, ?)',
                [$screen['table'], $before['code'], json_encode($changedBefore, JSON_UNESCAPED_UNICODE),
                    json_encode($changedAfter, JSON_UNESCAPED_UNICODE), $admin['id'], $reason, Clock::sql()]
            );
            $this->auth->audit($admin['id'], 'def.update', $screen['table'], $before['code'],
                implode(', ', array_keys($changedAfter)) . ' — ' . $reason, $ip);
        });
        $this->app->defs()->forget();
        return ['ok' => true, 'problems' => [], 'changed' => count($changedAfter), 'values' => $values];
    }

    /**
     * 이력의 한 줄을 이전 값으로 되돌린다. 되돌리는 것도 하나의 변경이라 같은 검사를 거치고 이력에 남는다.
     * @return array ok, problems, changed
     */
    public function revert(array $admin, $historyId, $reason, $ip)
    {
        $history = $this->app->db->one('SELECT * FROM config_history WHERE id = ?', [(int)$historyId]);
        if ($history === null) {
            return ['ok' => false, 'problems' => ['없는 이력입니다.'], 'changed' => 0];
        }
        $screen = null;
        foreach (Catalog::all() as $candidate) {
            if ($candidate['table'] === $history['target_type']) {
                // 같은 테이블을 쓰는 화면이 둘일 수 있다 (전투 장비 / 장비 가격 및 성능): 고칠 수 있는 열을 합친다.
                $screen = $screen === null ? $candidate : ['fields' => $screen['fields'] + $candidate['fields']] + $screen;
            }
        }
        if ($screen === null) {
            return ['ok' => false, 'problems' => ['이 이력은 여기에서 되돌릴 수 없습니다.'], 'changed' => 0];
        }
        if (!Auth::can($admin, $screen['permission'])) {
            return ['ok' => false, 'problems' => ['권한이 없습니다.'], 'changed' => 0];
        }
        $before = $this->row($screen, $history['target_key']);
        $old = json_decode((string)$history['before_value'], true);
        if ($before === null || !is_array($old)) {
            return ['ok' => false, 'problems' => ['되돌릴 대상을 찾을 수 없습니다.'], 'changed' => 0];
        }
        $reason = trim((string)$reason);
        $reasonProblem = self::reasonProblem($reason);
        if ($reasonProblem !== null) {
            return ['ok' => false, 'problems' => [$reasonProblem], 'changed' => 0];
        }
        $values = [];
        $problems = [];
        foreach ($old as $column => $value) {
            if (!isset($screen['fields'][$column])) {
                continue;   // 지금은 고칠 수 없는 열
            }
            list($parsed, $problem) = $this->parse($screen['fields'][$column], is_bool($value) ? ($value ? '1' : '0') : (string)$value);
            if ($problem !== null) {
                $problems[] = $problem;
            } else {
                $values[$column] = $parsed;
            }
        }
        if ($problems) {
            return ['ok' => false, 'problems' => $problems, 'changed' => 0];
        }
        $result = $this->apply($admin, $screen, $before, $values, '되돌리기 #' . (int)$historyId . ': ' . $reason, $ip);
        unset($result['values']);
        return $result;
    }

    // ── game_config ─────────────────────────────────────────

    public function configValues($key)
    {
        $configs = Catalog::configs();
        $stored = json_decode((string)$this->app->db->val('SELECT value FROM game_config WHERE config_key = ?', [$key]), true);
        $values = [];
        foreach ($configs[$key] as $name => $spec) {
            $values[$name] = is_array($stored) && isset($stored[$name]) && is_numeric($stored[$name]) ? (int)$stored[$name] : $spec[3];
        }
        return $values;
    }

    /** 공통 설정(하루 한도 등)을 저장한다. */
    public function saveConfig(array $admin, $key, array $form, $ip)
    {
        $configs = Catalog::configs();
        if (!is_string($key) || !isset($configs[$key])) {
            return ['ok' => false, 'problems' => ['없는 설정입니다.'], 'changed' => 0];
        }
        $before = $this->configValues($key);
        $after = $before;
        $problems = [];
        foreach ($configs[$key] as $name => $spec) {
            list($value, $problem) = $this->parse(['label' => $spec[0], 'type' => 'int', 'min' => $spec[1], 'max' => $spec[2]],
                isset($form[$name]) ? $form[$name] : null);
            if ($problem !== null) {
                $problems[] = $problem;
            } else {
                $after[$name] = $value;
            }
        }
        $reason = isset($form['reason']) && is_string($form['reason']) ? trim($form['reason']) : '';
        $reasonProblem = self::reasonProblem($reason);
        if ($reasonProblem !== null) {
            $problems[] = $reasonProblem;
        }
        if ($problems) {
            return ['ok' => false, 'problems' => $problems, 'changed' => 0];
        }
        if ($after == $before) {
            return ['ok' => true, 'problems' => [], 'changed' => 0];
        }
        $db = $this->app->db;
        $db->tx(function () use ($db, $admin, $key, $before, $after, $reason, $ip) {
            // 설정에 이 화면이 모르는 값이 있으면 그대로 둔다.
            $stored = json_decode((string)$db->val('SELECT value FROM game_config WHERE config_key = ? FOR UPDATE', [$key]), true);
            $merged = (is_array($stored) ? $stored : []);
            foreach ($after as $name => $value) {
                $merged[$name] = $value;
            }
            $db->exec(
                'INSERT INTO game_config (config_key, value, updated_at) VALUES (?, ?, ?)'
                . ' ON DUPLICATE KEY UPDATE value = VALUES(value), updated_at = VALUES(updated_at)',
                [$key, json_encode($merged), Clock::sql()]
            );
            $db->exec(
                'INSERT INTO config_history (target_type, target_key, before_value, after_value, admin_id, reason, created_at)'
                . ' VALUES (?, ?, ?, ?, ?, ?, ?)',
                ['game_config', $key, json_encode($before), json_encode($after), $admin['id'], $reason, Clock::sql()]
            );
            $this->auth->audit($admin['id'], 'config.update', 'game_config', $key, $reason, $ip);
        });
        $this->app->defs()->forget();
        return ['ok' => true, 'problems' => [], 'changed' => 1];
    }
}
