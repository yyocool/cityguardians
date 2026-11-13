<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;

/**
 * v9.0 의 정책·기준·설정을 변경 이력에서 이전 값으로 되돌린다 (docs/ADMIN_SYSTEM.md 12.5).
 *
 * 되돌리기는 "이전 값을 다시 저장하는 것"이다. 값을 직접 쓰지 않고, 관리자가 화면에서 고칠 때와 같은 저장 함수를 부른다.
 * 그래서 권한, 범위 검증, 사유, 새 이력 한 줄, 감사 기록이 평소의 변경과 똑같이 적용된다.
 * 지금의 허용 범위를 벗어나는 옛 값으로는 되돌아가지 않는다.
 */
final class CityOpsHistory
{
    const TAX = 'city_tax_policies';
    const MILESTONE = 'city_growth_milestones';
    const CITY_THRESHOLD = 'city_population_stats';
    const ROLE = 'defense_team_roles';
    const CONFIG = 'game_config';

    /** @var App */
    private $app;

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    /** 이 이력을 여기에서 되돌릴 수 있는가 (대상의 종류만 본다). */
    public static function handles($targetType, $targetKey)
    {
        if ($targetType === self::CONFIG) {
            return is_string($targetKey) && isset(Settings::specs()[$targetKey]);
        }
        return in_array($targetType, [self::TAX, self::MILESTONE, self::CITY_THRESHOLD, self::ROLE], true);
    }

    private static function text($value)
    {
        if (is_bool($value)) {
            return $value ? '1' : '0';
        }
        return $value === null ? '' : (string)$value;
    }

    /**
     * @return array ok, problems(문자열 목록), changed(바뀐 것이 있으면 1)
     */
    public function revert(array $admin, $historyId, $reason, $ip = '')
    {
        $fail = function ($problem) {
            return ['ok' => false, 'problems' => (array)$problem, 'changed' => 0];
        };
        if (!Auth::can($admin, Auth::CITY_POLICY)) {
            return $fail('권한이 없습니다.');
        }
        $db = $this->app->db;
        $history = $db->one('SELECT * FROM config_history WHERE id = ?', [(int)$historyId]);
        if ($history === null) {
            return $fail('없는 이력입니다.');
        }
        $type = $history['target_type'];
        $key = $history['target_key'];
        if (!self::handles($type, $key)) {
            return $fail('이 이력은 여기에서 되돌릴 수 없습니다.');
        }
        $old = json_decode((string)$history['before_value'], true);
        if (!is_array($old)) {
            return $fail('되돌릴 값이 없는 이력입니다.');
        }
        $reason = trim((string)$reason);
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            return $fail($reasonProblem);
        }
        $reason = mb_substr('되돌리기 #' . (int)$historyId . ': ' . $reason, 0, 255);

        $form = [];
        foreach ($old as $name => $value) {
            if (is_string($name) && !is_array($value)) {
                $form[$name] = self::text($value);
            }
        }
        $form['reason'] = $reason;
        $count = function () use ($db, $type, $key) {
            return (int)$db->val('SELECT COUNT(*) FROM config_history WHERE target_type = ? AND target_key = ?', [$type, $key]);
        };
        $lines = $count();

        if ($type === self::TAX) {
            // 적용 대상은 화면의 체크 상자와 같은 모양으로 넘긴다.
            foreach (explode(',', isset($old['applies_to']) ? (string)$old['applies_to'] : '') as $applies) {
                if ($applies !== '') {
                    $form['applies_' . $applies] = '1';
                }
            }
            $problems = $this->app->taxes()->savePolicy($admin, $key, $form, $ip);
        } elseif ($type === self::MILESTONE) {
            $problems = $this->app->growth()->saveMilestone($admin, $key, $form, $ip);
        } elseif ($type === self::ROLE) {
            $problems = $this->app->coop()->saveRole($admin, $key, $form, $ip);
        } elseif ($type === self::CITY_THRESHOLD) {
            $problems = $this->app->growth()->setCityThreshold($admin, $key,
                isset($old['modeling_threshold']) ? self::text($old['modeling_threshold']) : '', $reason, $ip);
        } else {
            // 그때에는 없던 항목(나중에 더해진 설정)은 지금 값을 그대로 둔다.
            foreach ($this->app->settings()->get($key) as $name => $value) {
                if (!isset($form[$name])) {
                    $form[$name] = self::text($value);
                }
            }
            $problems = $this->app->settings()->save($admin, $key, $form, $ip);
        }
        if ($problems) {
            return $fail($problems);
        }
        // 저장 함수는 바뀐 것이 있을 때에만 이력을 남긴다.
        return ['ok' => true, 'problems' => [], 'changed' => $count() > $lines ? 1 : 0];
    }
}
