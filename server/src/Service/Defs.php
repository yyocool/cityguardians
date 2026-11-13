<?php
namespace CG\Service;

use CG\Domain\Progression;
use CG\Domain\Ranking;
use CG\Domain\Risk;
use CG\Infra\Db;

/**
 * 정의 데이터를 DB 에서 읽는다. 값은 관리자가 고친다.
 * 돌려주는 배열의 키는 클라이언트의 정의 클래스(GearDef, TowerDef, StageDef …)의 필드 이름과 같다.
 * 요청 하나 안에서는 한 번만 읽는다.
 */
final class Defs
{
    /** @var Db */
    private $db;
    private $cache = [];

    public function __construct(Db $db)
    {
        $this->db = $db;
    }

    /** 관리자가 값을 바꾼 뒤 같은 요청 안에서 다시 읽게 한다. */
    public function forget()
    {
        $this->cache = [];
    }

    private function cached($key, callable $load)
    {
        if (!array_key_exists($key, $this->cache)) {
            $this->cache[$key] = $load();
        }
        return $this->cache[$key];
    }

    private static function find(array $list, $code)
    {
        foreach ($list as $def) {
            if ($def['code'] === $code) {
                return $def;
            }
        }
        return null;
    }

    // ── 전투 모드 ───────────────────────────────────────────

    public function modes()
    {
        return $this->cached('modes', function () {
            $modes = [];
            foreach ($this->db->all('SELECT * FROM combat_modes ORDER BY sort_order, id') as $row) {
                $modes[] = [
                    'code' => $row['code'], 'name' => $row['name'], 'summary' => $row['summary'],
                    'camera' => $row['camera'], 'controls' => $row['controls'],
                    'closed' => (bool)$row['is_closed'], 'sortOrder' => (int)$row['sort_order'],
                ];
            }
            return $modes;
        });
    }

    public function findMode($code)
    {
        return self::find($this->modes(), $code);
    }

    /** 그 모드로 출동할 수 있는가. 없는 모드이거나 관리자가 닫았으면 false. */
    public function modeOpen($code)
    {
        $mode = $this->findMode($code);
        return $mode !== null && !$mode['closed'];
    }

    // ── 전투 장비 ───────────────────────────────────────────

    public function gear()
    {
        return $this->cached('gear', function () {
            $gear = [];
            foreach ($this->db->all('SELECT * FROM combat_equipment WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $gear[] = [
                    'id' => (int)$row['id'],
                    'code' => $row['code'], 'name' => $row['name'], 'description' => $row['description'],
                    'slot' => $row['slot'], 'kind' => $row['kind'],
                    'modes' => array_values(array_filter(array_map('trim', explode(',', $row['modes'])), 'strlen')),
                    'priceGc' => (int)$row['price_gc'], 'unlockLevel' => (int)$row['unlock_level'],
                    'maxLevel' => (int)$row['max_level'], 'upgradeBaseGc' => (int)$row['upgrade_base_gc'],
                    'upkeepGc' => (int)$row['upkeep_gc'],
                    'damage' => (int)$row['damage'], 'attacksPerSecond' => (float)$row['attacks_per_second'],
                    'range' => (float)$row['range_m'], 'projectileSpeed' => (float)$row['projectile_speed'],
                    'clipSize' => (int)$row['clip_size'], 'reloadSeconds' => (float)$row['reload_seconds'],
                    'aimZoom' => (float)$row['aim_zoom'], 'coneDegrees' => (float)$row['cone_degrees'],
                    'knockback' => (float)$row['knockback'],
                    'cooldownSeconds' => (float)$row['cooldown_seconds'], 'effectSeconds' => (float)$row['effect_seconds'],
                    'effectRadius' => (float)$row['effect_radius'], 'effectValue' => (int)$row['effect_value'],
                    'comingSoon' => (bool)$row['is_coming_soon'],
                ];
            }
            return $gear;
        });
    }

    public function findGear($code)
    {
        return self::find($this->gear(), $code);
    }

    // ── 적 ──────────────────────────────────────────────────

    /** @return array [코드 => 적 정의] */
    public function enemies()
    {
        return $this->cached('enemies', function () {
            $enemies = [];
            foreach ($this->db->all('SELECT * FROM enemy_defs WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $enemies[$row['code']] = [
                    'code' => $row['code'], 'name' => $row['name'],
                    'maxHp' => (int)$row['max_hp'], 'damage' => (int)$row['damage'],
                    'moveSpeed' => (float)$row['move_speed'], 'detectRange' => (float)$row['detect_range'],
                    'attackRange' => (float)$row['attack_range'], 'attackWindup' => (float)$row['attack_windup'],
                    'attackInterval' => (float)$row['attack_interval'], 'loseRange' => (float)$row['lose_range'],
                    'rewardGc' => (int)$row['reward_gc'], 'rewardXp' => (int)$row['reward_xp'],
                    'baseDamage' => (int)$row['base_damage'], 'rewardEnergy' => (int)$row['reward_energy'],
                    'aggroSeconds' => (float)$row['aggro_seconds'], 'attacksStructures' => (bool)$row['attacks_structures'],
                    'strikeRadius' => (float)$row['strike_radius'], 'structureDamage' => (int)$row['structure_damage'],
                    'staggerImmune' => (bool)$row['stagger_immune'],
                ];
            }
            return $enemies;
        });
    }

    // ── 디펜스 ──────────────────────────────────────────────

    public function zones()
    {
        return $this->cached('zones', function () {
            $zones = [];
            foreach ($this->db->all('SELECT * FROM defense_zones WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $waves = json_decode($row['waves'], true);
                $zones[] = [
                    'id' => (int)$row['id'],
                    'code' => $row['code'], 'name' => $row['name'],
                    'baseHp' => (int)$row['base_hp'], 'startEnergy' => (int)$row['start_energy'],
                    'waveClearEnergy' => (int)$row['wave_clear_energy'], 'restSeconds' => (float)$row['rest_seconds'],
                    'waves' => is_array($waves) ? $waves : [],
                    'winGc' => (int)$row['win_gc'], 'firstWinBonusGc' => (int)$row['first_win_bonus_gc'],
                    'winXp' => (int)$row['win_xp'], 'loseGcPerWave' => (int)$row['lose_gc_per_wave'],
                    'minSecondsPerWave' => (float)$row['min_seconds_per_wave'],
                    'winDefensePoints' => (int)$row['win_defense_points'], 'unlockLevel' => (int)$row['unlock_level'],
                ];
            }
            return $zones;
        });
    }

    public function findZone($code)
    {
        return self::find($this->zones(), $code);
    }

    public function structures()
    {
        return $this->cached('structures', function () {
            $structures = [];
            foreach ($this->db->all('SELECT * FROM defense_structures WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $structures[] = [
                    'id' => (int)$row['id'],
                    'code' => $row['code'], 'name' => $row['name'], 'description' => $row['description'],
                    'tier' => $row['tier'], 'kind' => $row['kind'], 'ownership' => $row['ownership'],
                    'unlockGc' => (int)$row['unlock_gc'], 'energyCost' => (int)$row['energy_cost'],
                    'damage' => (int)$row['damage'], 'shotsPerSecond' => (float)$row['shots_per_second'],
                    'range' => (float)$row['range_m'], 'splashRadius' => (float)$row['splash_radius'],
                    'canHitAir' => (bool)$row['can_hit_air'], 'maxHp' => (int)$row['max_hp'],
                    'upkeepGc' => (int)$row['upkeep_gc'], 'upgradeEnergyCost' => (int)$row['upgrade_energy_cost'],
                    'maxUpgradeLevel' => (int)$row['max_upgrade_level'], 'maxPerBattle' => (int)$row['max_per_battle'],
                    'unlockLevel' => (int)$row['unlock_level'], 'effectValue' => (int)$row['effect_value'],
                    'effectSeconds' => (float)$row['effect_seconds'],
                    'cityGoalGc' => (int)$row['city_goal_gc'], 'cityPower' => (int)$row['city_power'],
                    'cityMaxLevel' => (int)$row['city_max_level'], 'comingSoon' => (bool)$row['is_coming_soon'],
                ];
            }
            return $structures;
        });
    }

    public function findStructure($code)
    {
        return self::find($this->structures(), $code);
    }

    /** 도시 공용 시설의 단계: 모인 기금 ÷ 한 단계의 목표, 한도까지. */
    public static function cityStructureLevel(array $def, $fundedGc)
    {
        if ($def['cityGoalGc'] <= 0) {
            return 0;
        }
        return (int)min(max(1, $def['cityMaxLevel']), intdiv((int)$fundedGc, $def['cityGoalGc']));
    }

    // ── 사냥 ────────────────────────────────────────────────

    public function huntZones()
    {
        return $this->cached('huntZones', function () {
            $zones = [];
            foreach ($this->db->all('SELECT * FROM hunt_zone_defs WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $zones[] = [
                    'id' => (int)$row['id'],
                    'code' => $row['code'], 'name' => $row['name'],
                    'enemyCodes' => array_values(array_filter(array_map('trim', explode(',', $row['enemy_codes'])), 'strlen')),
                    'maxAlive' => (int)$row['max_alive'], 'respawnSeconds' => (float)$row['respawn_seconds'],
                    'maxKillsPerMinute' => (float)$row['max_kills_per_minute'], 'burstAllowance' => (int)$row['burst_allowance'],
                ];
            }
            return $zones;
        });
    }

    public function findHuntZone($code)
    {
        return self::find($this->huntZones(), $code);
    }

    // ── FPS ─────────────────────────────────────────────────

    public function fpsMissions()
    {
        return $this->cached('fpsMissions', function () {
            $missions = [];
            foreach ($this->db->all('SELECT * FROM fps_missions WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $groups = json_decode($row['enemy_groups'], true);
                $missions[] = [
                    'id' => (int)$row['id'],
                    'code' => $row['code'], 'name' => $row['name'], 'description' => $row['description'],
                    'groups' => is_array($groups) ? $groups : [],
                    'facilityCount' => (int)$row['facility_count'], 'facilityHp' => (int)$row['facility_hp'],
                    'civilianCount' => (int)$row['civilian_count'],
                    'timeLimitSeconds' => (float)$row['time_limit_seconds'], 'minSeconds' => (float)$row['min_seconds'],
                    'winGc' => (int)$row['win_gc'], 'firstWinBonusGc' => (int)$row['first_win_bonus_gc'],
                    'winXp' => (int)$row['win_xp'], 'civilianBonusGc' => (int)$row['civilian_bonus_gc'],
                    'facilityBonusGc' => (int)$row['facility_bonus_gc'],
                    'winDefensePoints' => (int)$row['win_defense_points'], 'unlockLevel' => (int)$row['unlock_level'],
                ];
            }
            return $missions;
        });
    }

    public function findFpsMission($code)
    {
        return self::find($this->fpsMissions(), $code);
    }

    public function facilities()
    {
        return $this->cached('facilities', function () {
            $facilities = [];
            foreach ($this->db->all('SELECT * FROM city_facility_defs WHERE is_active = 1 ORDER BY sort_order, id') as $row) {
                $facilities[] = [
                    'id' => (int)$row['id'], 'code' => $row['code'], 'name' => $row['name'],
                    'repairGc' => (int)$row['repair_gc'], 'maxDamage' => (int)$row['max_damage'],
                ];
            }
            return $facilities;
        });
    }

    public function findFacility($code)
    {
        return self::find($this->facilities(), $code);
    }

    // ── 정책과 설정 ─────────────────────────────────────────

    private function policyRow()
    {
        return $this->cached('policyRow', function () {
            return $this->db->one('SELECT * FROM city_risk_policy ORDER BY id DESC LIMIT 1');
        });
    }

    /** 지금의 위험도 계산 정책. 가장 최근에 저장된 것이다. */
    public function policy()
    {
        return $this->cached('policy', function () {
            $row = $this->policyRow();
            $policy = Risk::fillPolicy($row ? json_decode($row['policy'], true) : null);
            $policy['version'] = $row ? (int)$row['id'] : 0;
            return $policy;
        });
    }

    public function rankingWeights()
    {
        return $this->cached('ranking', function () {
            $row = $this->policyRow();
            return Ranking::fillWeights($row ? json_decode($row['ranking'], true) : null);
        });
    }

    private function config($key, array $defaults)
    {
        return $this->cached('config:' . $key, function () use ($key, $defaults) {
            $value = $this->db->val('SELECT value FROM game_config WHERE config_key = ?', [$key]);
            $stored = $value !== null ? json_decode($value, true) : null;
            if (is_array($stored)) {
                foreach ($defaults as $name => $default) {
                    if (isset($stored[$name]) && is_numeric($stored[$name])) {
                        $defaults[$name] = is_float($default) ? (float)$stored[$name] : (int)$stored[$name];
                    }
                }
            }
            return $defaults;
        });
    }

    public function economy()
    {
        return $this->config('economy', ['dailyHuntGcLimit' => 3000, 'residencyBonusGc' => 500]);
    }

    public function progression()
    {
        return $this->config('progression', Progression::defaultConfig());
    }

    public function combatConfig()
    {
        return $this->config('combat', ['dailyFpsRewardLimit' => 5]);
    }

    /** 정의 데이터의 판(version). 값이 하나라도 바뀌면 달라진다. 클라이언트는 이 값이 다를 때만 정의를 다시 받는다. */
    public function version()
    {
        return $this->cached('version', function () {
            $parts = [];
            foreach (['combat_modes', 'combat_equipment', 'enemy_defs', 'defense_zones', 'defense_structures',
                         'hunt_zone_defs', 'fps_missions', 'city_facility_defs', 'game_config'] as $table) {
                $parts[] = (string)$this->db->val("SELECT CONCAT(COUNT(*), ':', COALESCE(MAX(updated_at), '')) FROM $table");
            }
            $parts[] = (string)$this->db->val('SELECT COALESCE(MAX(id), 0) FROM city_risk_policy');
            // 같은 초에 두 번 고쳐도 판이 달라지도록 변경 이력의 번호도 넣는다.
            $parts[] = (string)$this->db->val('SELECT COALESCE(MAX(id), 0) FROM config_history');
            return substr(sha1(implode('|', $parts)), 0, 12);
        });
    }
}
