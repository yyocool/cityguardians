<?php
namespace CG\Service;

use CG\Admin\Auth;
use CG\Admin\DefEditor;
use CG\App;
use CG\Infra\Clock;

/**
 * v9.0 의 운영 설정 (game_config 의 growth, coop, ping, treasury, budget).
 *
 * 값은 모두 정수이고 범위가 정해져 있다. 관리자가 고치면 변경 이력(config_history)과 감사 기록에 남는다.
 * 기본값은 "켜기 전에는 v8.0 과 똑같이 동작한다"가 되도록 골랐다: 협동 방어전과 참여예산은 꺼져 있다.
 */
final class Settings
{
    const GROWTH = 'growth';
    const COOP = 'coop';
    const PING = 'ping';
    const TREASURY = 'treasury';
    const BUDGET = 'budget';
    /** v10.0 실시간 대시보드 (docs/LIVE_DASHBOARD.md 7장). */
    const DASHBOARD = 'dashboard';

    /** 전국 긴급 알림 표시 정책. */
    const NOTICE_NONE = 0;
    const NOTICE_EMERGENCY = 1;
    const NOTICE_EMERGENCY_OR_INVASION = 2;

    /** 유효 전입 기준: 연결된 계정만. */
    const MODE_LINKED = 1;
    /** 유효 전입 기준: 연결된 계정, 또는 활동으로 확인된 게스트 계정. */
    const MODE_LINKED_OR_PROVEN = 2;

    /** @var App */
    private $app;
    private $cache = [];

    public function __construct(App $app)
    {
        $this->app = $app;
    }

    /** [설정 키 => [이름 => [표시 이름, 최소, 최대, 기본값]]] */
    public static function specs()
    {
        return [
            self::GROWTH => [
                'populationMode' => ['유효 전입 기준 (1: 연결된 계정만, 2: 연결된 계정 또는 활동으로 확인된 게스트)', 1, 2, self::MODE_LINKED],
                'provenMinLevel' => ['활동 확인: 최소 레벨', 1, 50, 3],
                'provenMinAccountDays' => ['활동 확인: 계정을 만든 뒤 지난 날수', 0, 365, 3],
                'provenMinActiveDays' => ['활동 확인: 플레이한 날수', 1, 365, 2],
                'moveEnabled' => ['이사 (0: 막음, 1: 허용)', 0, 1, 1],
                'moveFeeGc' => ['이사 수수료 GC', 0, 1000000, 1000],
                'moveCooldownDays' => ['전입·이사 뒤 다시 이사할 수 있을 때까지의 날수', 0, 365, 7],
            ],
            self::COOP => [
                'enabled' => ['협동 방어전 (0: 끔, 1: 켬)', 0, 1, 0],
                'defaultPlayers' => ['기본 정원', 2, 16, 4],
                'maxPlayers' => ['최대 정원', 2, 16, 8],
                'lobbyMinutes' => ['대기실이 닫히기까지의 시간(분)', 1, 120, 15],
                'sessionMaxMinutes' => ['한 판의 최대 시간(분)', 5, 240, 60],
                'dailyRewardLimit' => ['보상을 받을 수 있는 하루 횟수', 0, 1000, 5],
                'hpScalePercentPerPlayer' => ['사람이 한 명 늘 때마다 적 체력이 느는 비율(%)', 0, 300, 60],
                'energyScalePercentPerPlayer' => ['사람이 한 명 늘 때마다 공동 예산이 느는 비율(%)', 0, 300, 50],
                'minParticipationPercent' => ['보상을 받는 최소 참여 시간 비율(%)', 0, 100, 30],
                'allowLateJoin' => ['전투 중 참가 (0: 막음, 1: 허용)', 0, 1, 1],
                'ticketSeconds' => ['실시간 서버 입장권의 수명(초)', 10, 600, 60],
                'minPlayersToStart' => ['시작에 필요한 최소 인원', 1, 16, 2],
                'cityStructureGatePercent' => ['도시 공용 방어시설 한 단계마다 관문 내구도가 느는 비율(%)', 0, 50, 5],
                'playerDamage' => ['직접 공격: 한 발의 피해', 1, 1000, 12],
                'playerDamagePerLevelPercent' => ['직접 공격: 레벨마다 느는 피해(%)', 0, 100, 5],
                'playerRangeM' => ['직접 공격: 사거리(m)', 1, 60, 14],
                'playerShotsPer10s' => ['직접 공격: 10초 동안 쏠 수 있는 횟수', 1, 200, 30],
                'repairHp' => ['수리: 한 번에 고치는 내구도', 1, 1000, 30],
                'repairEnergy' => ['수리: 한 번에 드는 공동 예산', 0, 1000, 10],
            ],
            self::PING => [
                'enabled' => ['전술 핑 (0: 끔, 1: 켬)', 0, 1, 1],
                'burst' => ['연달아 보낼 수 있는 핑의 수', 1, 20, 3],
                'refillSeconds' => ['핑 하나가 다시 차는 시간(초)', 1, 120, 4],
                'lifetimeSeconds' => ['핑이 지도에 남는 시간(초)', 1, 120, 8],
                'maxActivePerPlayer' => ['한 사람이 동시에 띄울 수 있는 핑의 수', 1, 20, 3],
                'autoMuteStrikes' => ['제한을 이만큼 넘기면 잠시 핑을 막음', 1, 100, 5],
                'autoMuteSeconds' => ['잠시 막는 시간(초)', 1, 3600, 30],
                'routeOverlay' => ['전술 지도에 적 이동 경로 표시 허용 (0: 끔, 1: 켬)', 0, 1, 1],
                'rangeOverlay' => ['전술 지도에 타워 사거리 표시 허용 (0: 끔, 1: 켬)', 0, 1, 1],
            ],
            self::TREASURY => [
                'reservePercent' => ['세금 가운데 예비비로 떼어 두는 비율(%)', 0, 50, 10],
                'absenteeDays' => ['재산세: 이 날수 안에 접속한 주민에게만 걷음', 1, 365, 7],
            ],
            self::BUDGET => [
                'enabled' => ['참여예산 (0: 끔, 1: 켬)', 0, 1, 0],
                'voteDays' => ['투표 기간(일)', 1, 60, 7],
                'quorumVotes' => ['가결에 필요한 최소 투표 수', 1, 100000, 5],
                'voterMinLevel' => ['투표할 수 있는 최소 레벨', 1, 50, 2],
                'voterMinResidentDays' => ['투표하려면 전입한 지 지나야 하는 날수', 0, 365, 3],
                'proposerMinLevel' => ['제안할 수 있는 최소 레벨', 1, 50, 3],
                'maxOpenProposalsPerPlayer' => ['한 사람이 동시에 낼 수 있는 제안의 수', 1, 20, 1],
                'maxRequestGc' => ['제안 하나가 요청할 수 있는 최대 GC', 100, 100000000, 50000],
            ],
            self::DASHBOARD => [
                'financeVisible' => ['내 계좌 카드: 기본 표시 (0: 숨김, 1: 표시)', 0, 1, 1],
                'financeExpanded' => ['내 계좌 카드: 기본 펼침 (0: 접힘, 1: 펼침)', 0, 1, 1],
                'cityVisible' => ['우리 도시 카드: 기본 표시 (0: 숨김, 1: 표시)', 0, 1, 1],
                'cityExpanded' => ['우리 도시 카드: 기본 펼침 (0: 접힘, 1: 펼침)', 0, 1, 1],
                'nationalVisible' => ['전국 현황 카드: 기본 표시 (0: 숨김, 1: 표시)', 0, 1, 1],
                'nationalExpanded' => ['전국 현황 카드: 기본 펼침 (0: 접힘, 1: 펼침)', 0, 1, 0],
                'refreshSeconds' => ['데이터 갱신 간격(초)', 5, 300, 20],
                'cityAlertRisk' => ['도시 위험 경고 기준: 우리 도시의 위험도가 이 값 이상이면 카드에 경고', 0, 100, 70],
                'nationalNotice' => ['전국 긴급 알림 (0: 표시 안 함, 1: 긴급 단계인 도시, 2: 긴급 단계이거나 침공 중인 도시)', 0, 2, self::NOTICE_EMERGENCY],
                'nationalNoticeMax' => ['전국 긴급 알림을 한 번에 보여 주는 최대 개수', 1, 10, 3],
                'sampleData' => ['준비되지 않은 금융 항목 (0: "준비 중"으로 표시, 1: "개발용 샘플"로 표시)', 0, 1, 0],
            ],
        ];
    }

    public static function titles()
    {
        return [
            self::GROWTH => '전입자 집계와 이사', self::COOP => '협동 방어전', self::PING => '미니맵 및 전술 핑',
            self::TREASURY => '도시 재정', self::BUDGET => '참여예산', self::DASHBOARD => '실시간 대시보드',
        ];
    }

    public function forget()
    {
        $this->cache = [];
    }

    /** @return array<string,int> */
    public function get($key)
    {
        if (!isset($this->cache[$key])) {
            $specs = self::specs();
            if (!isset($specs[$key])) {
                throw new \InvalidArgumentException('없는 설정: ' . $key);
            }
            $stored = json_decode((string)$this->app->db->val('SELECT value FROM game_config WHERE config_key = ?', [$key]), true);
            $values = [];
            foreach ($specs[$key] as $name => $spec) {
                $value = is_array($stored) && isset($stored[$name]) && is_numeric($stored[$name]) ? (int)$stored[$name] : $spec[3];
                // 저장된 값이 범위를 벗어나 있으면 (손으로 고친 경우) 범위 안으로 당긴다.
                $values[$name] = max($spec[1], min($spec[2], $value));
            }
            $this->cache[$key] = $values;
        }
        return $this->cache[$key];
    }

    public function growth()
    {
        return $this->get(self::GROWTH);
    }

    public function coop()
    {
        return $this->get(self::COOP);
    }

    public function ping()
    {
        return $this->get(self::PING);
    }

    public function treasury()
    {
        return $this->get(self::TREASURY);
    }

    public function budget()
    {
        return $this->get(self::BUDGET);
    }

    public function dashboard()
    {
        return $this->get(self::DASHBOARD);
    }

    /**
     * 관리자가 설정을 고친다. 범위를 벗어난 값이 하나라도 있으면 아무것도 저장하지 않는다.
     * @return string[] 문제 목록. 비어 있으면 저장했다 (바뀐 값이 없어도 비어 있다).
     */
    public function save(array $admin, $key, array $form, $ip = '')
    {
        $specs = self::specs();
        if (!is_string($key) || !isset($specs[$key])) {
            return ['없는 설정입니다.'];
        }
        if (!Auth::can($admin, Auth::CITY_POLICY)) {
            return ['권한이 없습니다.'];
        }
        $before = $this->get($key);
        $after = $before;
        $problems = [];
        foreach ($specs[$key] as $name => $spec) {
            $input = isset($form[$name]) && is_string($form[$name]) ? trim($form[$name]) : '';
            if (!preg_match('/^-?\d{1,12}$/', $input) || (int)$input < $spec[1] || (int)$input > $spec[2]) {
                $problems[] = $spec[0] . ': ' . $spec[1] . ' ~ ' . $spec[2] . ' 사이의 정수여야 합니다.';
            } else {
                $after[$name] = (int)$input;
            }
        }
        if ($key === self::COOP && !$problems && $after['defaultPlayers'] > $after['maxPlayers']) {
            $problems[] = '기본 정원은 최대 정원보다 클 수 없습니다.';
        }
        $reason = isset($form['reason']) && is_string($form['reason']) ? trim($form['reason']) : '';
        $reasonProblem = DefEditor::reasonProblem($reason);
        if ($reasonProblem !== null) {
            $problems[] = $reasonProblem;
        }
        if ($problems || $after === $before) {
            return $problems;
        }
        $db = $this->app->db;
        $db->tx(function () use ($db, $admin, $key, $before, $after, $reason, $ip) {
            $stored = json_decode((string)$db->val('SELECT value FROM game_config WHERE config_key = ? FOR UPDATE', [$key]), true);
            $merged = is_array($stored) ? $stored : [];
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
            (new Auth($this->app))->audit($admin['id'], 'config.update', 'game_config', $key, $reason, $ip);
        });
        $this->forget();
        $this->app->defs()->forget();
        return [];
    }
}
