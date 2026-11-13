<?php
namespace CG\Admin;

/**
 * 관리자가 고칠 수 있는 정의 데이터의 목록 (docs/ADMIN_SYSTEM.md 4장의 v8.0 메뉴).
 *
 * 여기에 적힌 테이블의, 여기에 적힌 열만 고칠 수 있다. 테이블·열 이름은 요청에서 받지 않는다 (이 목록에서만 나온다).
 * 값마다 범위가 있고, 범위를 벗어난 값은 저장하지 않는다. 범위는 "게임이 망가지지 않는 한도"이고 알맞은 값은 플레이테스트로 정한다.
 *
 * 열의 형식: int, decimal(소수 셋째 자리까지), text, bool, select, json
 * 정의를 새로 더하거나 지우는 기능은 없다 (새 적·타워는 클라이언트의 모델이 함께 있어야 한다). 끄려면 is_active 를 끈다.
 */
final class Catalog
{
    private static function int($label, $min, $max, $help = '')
    {
        return ['label' => $label, 'type' => 'int', 'min' => $min, 'max' => $max, 'help' => $help];
    }

    private static function dec($label, $min, $max, $help = '')
    {
        return ['label' => $label, 'type' => 'decimal', 'min' => $min, 'max' => $max, 'help' => $help];
    }

    private static function text($label, $max, $required = true)
    {
        return ['label' => $label, 'type' => 'text', 'max' => $max, 'required' => $required, 'help' => ''];
    }

    private static function bool($label, $help = '')
    {
        return ['label' => $label, 'type' => 'bool', 'help' => $help];
    }

    private static function select($label, array $options)
    {
        return ['label' => $label, 'type' => 'select', 'options' => $options, 'help' => ''];
    }

    private static function json($label, $validator, $help)
    {
        return ['label' => $label, 'type' => 'json', 'validator' => $validator, 'help' => $help];
    }

    private static function common()
    {
        return [
            'is_active' => self::bool('사용', '끄면 게임에 나오지 않는다'),
            'sort_order' => self::int('정렬 순서', 0, 9999),
        ];
    }

    /**
     * @return array<string,array> [화면 키 => 정의]
     *   menu(메뉴 키), title, table, permission, list(목록에 보이는 열), fields(고칠 수 있는 열), readonly(보여 주기만 하는 열)
     */
    public static function all()
    {
        $groupHelp = 'JSON. 예: [{"enemyCode":"mech_insect","count":12,"interval":0.8,"delay":0}]';
        return [
            'modes' => [
                'menu' => 'modes', 'title' => '전투 모드', 'table' => 'combat_modes', 'permission' => Auth::OPERATE,
                'list' => ['code', 'name', 'camera', 'is_closed', 'sort_order'],
                'readonly' => ['code' => '코드', 'camera' => '카메라'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'summary' => self::text('설명', 255, false),
                    'controls' => self::text('조작 안내', 255, false),
                    'is_closed' => self::bool('닫음', '닫으면 그 모드로 출동할 수 없다 (점검용). 진행 중인 전투의 결과 보고는 받는다'),
                    'sort_order' => self::int('정렬 순서', 0, 9999),
                ],
            ],
            'enemies' => [
                'menu' => 'enemies', 'title' => '적 유닛', 'table' => 'enemy_defs', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'max_hp', 'damage', 'move_speed', 'reward_gc', 'reward_xp', 'is_active'],
                'readonly' => ['code' => '코드'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'max_hp' => self::int('체력', 1, 1000000),
                    'damage' => self::int('공격력', 0, 10000),
                    'move_speed' => self::dec('이동 속도', 0, 50),
                    'detect_range' => self::dec('발견 거리', 0, 500),
                    'attack_range' => self::dec('공격 거리', 0, 500),
                    'attack_windup' => self::dec('공격 준비 시간(초)', 0, 60),
                    'attack_interval' => self::dec('공격 간격(초)', 0.05, 60),
                    'lose_range' => self::dec('추적을 포기하는 거리', 0, 1000),
                    'reward_gc' => self::int('처치 보상 GC', 0, 10000),
                    'reward_xp' => self::int('처치 보상 경험치', 0, 10000),
                    'base_damage' => self::int('방어 기지 피해', 0, 1000),
                    'reward_energy' => self::int('처치 전투 에너지', 0, 10000),
                    'aggro_seconds' => self::dec('추적 시간(초)', 0, 600),
                    'attacks_structures' => self::bool('방어시설을 공격함'),
                    'strike_radius' => self::dec('타격 반경', 0, 100),
                    'structure_damage' => self::int('방어시설 피해', 0, 100000),
                    'stagger_immune' => self::bool('경직 면역'),
                ] + self::common(),
            ],
            'hunt_zones' => [
                'menu' => 'enemies', 'title' => '사냥 구역', 'table' => 'hunt_zone_defs', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'enemy_codes', 'max_alive', 'max_kills_per_minute', 'burst_allowance', 'is_active'],
                'readonly' => ['code' => '코드', 'enemy_codes' => '나오는 적'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'max_alive' => self::int('동시에 나오는 수', 1, 200),
                    'respawn_seconds' => self::dec('다시 나오는 시간(초)', 0, 3600, '적이 없는 구역은 0'),
                    'max_kills_per_minute' => self::dec('분당 처치 한도 (검증)', 0.1, 1000, '서버가 인정하는 처치 속도의 한도'),
                    'burst_allowance' => self::int('한꺼번에 인정하는 처치 수 (검증)', 0, 1000),
                ] + self::common(),
            ],
            'zones' => [
                'menu' => 'zones', 'title' => '디펜스 구역', 'table' => 'defense_zones', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'base_hp', 'win_gc', 'first_win_bonus_gc', 'win_xp', 'unlock_level', 'is_active'],
                'readonly' => ['code' => '코드'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'base_hp' => self::int('방어 기지 체력', 1, 10000),
                    'start_energy' => self::int('시작 전투 에너지', 0, 100000),
                    'wave_clear_energy' => self::int('웨이브를 막을 때마다 받는 에너지', 0, 100000),
                    'rest_seconds' => self::dec('웨이브 사이 쉬는 시간(초)', 0, 600),
                    'waves' => self::json('웨이브 구성', 'waves', 'JSON. 예: [{"groups":[{"enemyCode":"mech_insect","count":12,"interval":0.8,"delay":0}]}]'),
                    'win_gc' => self::int('승리 보상 GC', 0, 100000),
                    'first_win_bonus_gc' => self::int('첫 승리 보너스 GC', 0, 100000),
                    'win_xp' => self::int('승리 경험치', 0, 100000),
                    'lose_gc_per_wave' => self::int('패배 시 막은 웨이브당 GC', 0, 10000),
                    'min_seconds_per_wave' => self::dec('웨이브당 최소 시간(초, 검증)', 0, 600, '이보다 빨리 끝났다는 보고는 인정하지 않는다'),
                    'win_defense_points' => self::int('승리 시 도시 방어 포인트', 0, 100000),
                    'unlock_level' => self::int('해금 레벨', 1, 100),
                ] + self::common(),
            ],
            'structures' => [
                'menu' => 'structures', 'title' => '타워 및 방어시설', 'table' => 'defense_structures', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'tier', 'ownership', 'unlock_gc', 'energy_cost', 'damage', 'max_hp', 'unlock_level',
                    'is_coming_soon', 'is_active'],
                'readonly' => ['code' => '코드', 'kind' => '종류', 'ownership' => '소유 (battle 전투 중 설치 / permanent 영구 보유 / city 도시 공용)'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'description' => self::text('설명', 255, false),
                    'tier' => self::select('등급', ['basic' => '기본', 'mid' => '중급', 'advanced' => '고급', 'special' => '특수']),
                    'unlock_gc' => self::int('가격 (영구 해금 GC)', 0, 1000000),
                    'energy_cost' => self::int('설치 비용 (전투 에너지)', 0, 100000),
                    'damage' => self::int('공격력', 0, 100000),
                    'range_m' => self::dec('범위', 0, 500),
                    'shots_per_second' => self::dec('속도 (초당 공격 횟수)', 0, 50),
                    'max_hp' => self::int('내구도', 0, 1000000),
                    'upkeep_gc' => self::int('유지 비용 GC', 0, 100000, '지금은 값만 저장한다. 자동으로 걷지 않는다'),
                    'upgrade_energy_cost' => self::int('업그레이드 비용 (전투 에너지)', 0, 100000),
                    'max_upgrade_level' => self::int('최대 업그레이드 단계', 0, 10),
                    'max_per_battle' => self::int('설치 제한 (한 전투에 세울 수 있는 수, 0은 제한 없음)', 0, 100),
                    'unlock_level' => self::int('해금 조건 (레벨)', 1, 100),
                    'splash_radius' => self::dec('폭발 반경', 0, 100),
                    'can_hit_air' => self::bool('공중의 적을 공격함'),
                    'effect_value' => self::int('특수 효과 값', 0, 100000),
                    'effect_seconds' => self::dec('특수 효과 시간(초)', 0, 600),
                    'city_goal_gc' => self::int('도시 공용: 한 단계에 필요한 기금 GC', 0, 100000000),
                    'city_power' => self::int('도시 공용: 한 단계마다 오르는 방어력', 0, 100000),
                    'city_max_level' => self::int('도시 공용: 최대 단계', 0, 20),
                    'is_coming_soon' => self::bool('준비 중', '켜면 목록에는 보이지만 쓸 수 없다'),
                ] + self::common(),
            ],
            'fps' => [
                'menu' => 'fps', 'title' => 'FPS 임무', 'table' => 'fps_missions', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'facility_count', 'civilian_count', 'time_limit_seconds', 'win_gc', 'win_xp',
                    'unlock_level', 'is_active'],
                'readonly' => ['code' => '코드'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'description' => self::text('설명', 255, false),
                    'enemy_groups' => self::json('침입한 적', 'groups', $groupHelp),
                    'facility_count' => self::int('주요 시설 수', 1, 10),
                    'facility_hp' => self::int('주요 시설 체력', 1, 1000),
                    'civilian_count' => self::int('시민 수', 0, 50),
                    'time_limit_seconds' => self::dec('제한 시간(초)', 30, 3600),
                    'min_seconds' => self::dec('최소 시간(초, 검증)', 0, 3600, '이보다 빨리 끝났다는 보고는 인정하지 않는다'),
                    'win_gc' => self::int('승리 보상 GC', 0, 100000),
                    'first_win_bonus_gc' => self::int('첫 승리 보너스 GC', 0, 100000),
                    'win_xp' => self::int('승리 경험치', 0, 100000),
                    'civilian_bonus_gc' => self::int('구한 시민 한 명당 GC', 0, 10000),
                    'facility_bonus_gc' => self::int('지킨 시설 하나당 GC', 0, 10000),
                    'win_defense_points' => self::int('승리 시 도시 방어 포인트', 0, 100000),
                    'unlock_level' => self::int('해금 레벨', 1, 100),
                ] + self::common(),
            ],
            'gear' => [
                'menu' => 'gear', 'title' => '전투 장비', 'table' => 'combat_equipment', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'slot', 'kind', 'modes', 'unlock_level', 'is_coming_soon', 'is_active'],
                'readonly' => ['code' => '코드', 'slot' => '칸', 'kind' => '종류', 'modes' => '쓸 수 있는 전투 모드'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'description' => self::text('설명', 255, false),
                    'unlock_level' => self::int('해금 레벨', 1, 100),
                    'is_coming_soon' => self::bool('준비 중', '켜면 목록에는 보이지만 살 수 없다'),
                ] + self::common(),
            ],
            'gear_stats' => [
                'menu' => 'gear_stats', 'title' => '장비 가격 및 성능', 'table' => 'combat_equipment', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'price_gc', 'max_level', 'upgrade_base_gc', 'damage', 'attacks_per_second', 'range_m',
                    'clip_size', 'cooldown_seconds'],
                'readonly' => ['code' => '코드', 'name' => '이름', 'slot' => '칸', 'modes' => '쓸 수 있는 전투 모드'],
                'fields' => [
                    'price_gc' => self::int('가격 GC (0이면 무료 기본 장비)', 0, 1000000),
                    'max_level' => self::int('최대 강화 단계', 1, 10, '한 단계마다 효과가 10% 오른다'),
                    'upgrade_base_gc' => self::int('강화 기본 비용 GC (× 지금 단계)', 0, 1000000),
                    'upkeep_gc' => self::int('유지 비용 GC', 0, 100000, '지금은 값만 저장한다. 자동으로 걷지 않는다'),
                    'damage' => self::int('공격력', 0, 100000),
                    'attacks_per_second' => self::dec('초당 공격 횟수', 0, 50),
                    'range_m' => self::dec('사거리', 0, 500),
                    'projectile_speed' => self::dec('탄 속도', 0, 1000),
                    'clip_size' => self::int('탄창', 0, 1000),
                    'reload_seconds' => self::dec('재장전 시간(초)', 0, 60),
                    'aim_zoom' => self::dec('조준할 때 시야 배율', 0, 1, '작을수록 크게 보인다. 0.3 ~ 1 사이로 쓰인다. 조준이 없는 장비는 0'),
                    'cone_degrees' => self::dec('퍼지는 각도', 0, 180),
                    'knockback' => self::dec('밀어내는 힘', 0, 100),
                    'cooldown_seconds' => self::dec('재사용 대기(초)', 0, 3600),
                    'effect_seconds' => self::dec('효과 시간(초)', 0, 600),
                    'effect_radius' => self::dec('효과 반경', 0, 500),
                    'effect_value' => self::int('효과 값', 0, 100000),
                ],
            ],
            'facilities' => [
                'menu' => 'risk', 'title' => '도시 주요 시설', 'table' => 'city_facility_defs', 'permission' => Auth::BALANCE,
                'list' => ['code', 'name', 'repair_gc', 'max_damage', 'is_active'],
                'readonly' => ['code' => '코드'],
                'fields' => [
                    'name' => self::text('이름', 40),
                    'repair_gc' => self::int('한 단계 복구 비용 GC', 0, 100000),
                    'max_damage' => self::int('최대 피해 단계', 1, 10),
                ] + self::common(),
            ],
        ];
    }

    public static function find($key)
    {
        $all = self::all();
        return is_string($key) && isset($all[$key]) ? $all[$key] : null;
    }

    /** 열 이름을 화면에 보이는 이름으로. */
    public static function label(array $screen, $column)
    {
        if (isset($screen['fields'][$column])) {
            return $screen['fields'][$column]['label'];
        }
        if (isset($screen['readonly'][$column])) {
            return $screen['readonly'][$column];
        }
        return $column;
    }

    /**
     * game_config 의 값 가운데 관리자가 고칠 수 있는 것: [설정 키 => [이름 => [표시 이름, 최소, 최대, 기본값]]]
     */
    public static function configs()
    {
        return [
            'economy' => [
                'dailyHuntGcLimit' => ['사냥으로 하루에 벌 수 있는 GC', 0, 1000000, 3000],
                'residencyBonusGc' => ['정착 지원금 GC (계정에 한 번)', 0, 100000, 500],
            ],
            'combat' => [
                'dailyFpsRewardLimit' => ['FPS 임무 보상을 받을 수 있는 하루 횟수', 0, 1000, 5],
            ],
        ];
    }
}
