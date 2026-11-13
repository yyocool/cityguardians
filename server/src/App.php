<?php
namespace CG;

use CG\Infra\Db;
use CG\Infra\Env;
use CG\Service\CityService;
use CG\Service\CombatService;
use CG\Service\Daily;
use CG\Service\DefenseService;
use CG\Service\Defs;
use CG\Service\FpsService;
use CG\Service\HuntService;
use CG\Service\Idempotency;
use CG\Service\Players;
use CG\Service\RateLimit;
use CG\Service\Wallets;

/**
 * 서비스 묶음. 요청 하나(또는 명령행 작업 하나)마다 하나를 만든다.
 *
 * 공통 전투 시스템과 모드별 전투 시스템을 나눠 두었다:
 *   공통  — Players(계정·레벨·소속), Wallets(GC·원장), CombatService(모드·장비·장착), CityService(위험도·침공·랭킹·지원)
 *   모드별 — DefenseService(타워디펜스), HuntService(3인칭 사냥), FpsService(1인칭 시내 침입)
 * 모드별 서비스는 결과를 검증한 뒤 보상·경험치·도시 반영을 모두 공통 서비스에 맡긴다. 모드마다 따로 가진 지갑이나 레벨은 없다.
 */
final class App
{
    /** @var Env */
    public $env;
    /** @var Db */
    public $db;

    private $services = [];

    public function __construct(Env $env, Db $db)
    {
        $this->env = $env;
        $this->db = $db;
    }

    private function service($name, callable $make)
    {
        if (!isset($this->services[$name])) {
            $this->services[$name] = $make();
        }
        return $this->services[$name];
    }

    /** @return Defs */
    public function defs()
    {
        return $this->service('defs', function () {
            return new Defs($this->db);
        });
    }

    /** @return Players */
    public function players()
    {
        return $this->service('players', function () {
            return new Players($this);
        });
    }

    /** @return Wallets */
    public function wallets()
    {
        return $this->service('wallets', function () {
            $wallets = new Wallets($this->db);
            // v9.0 가상 지방세: 원장에 한 줄이 생길 때마다 과세 대상인지 본다. 켜진 세금 정책이 없으면 아무 일도 하지 않는다.
            $wallets->onEntry(function ($playerId, $delta, $reason, $ledgerId, $balanceAfter) {
                return $this->taxes()->onLedger($playerId, $delta, $reason, $ledgerId, $balanceAfter);
            });
            return $wallets;
        });
    }

    /** @return Daily */
    public function daily()
    {
        return $this->service('daily', function () {
            return new Daily($this->db);
        });
    }

    /** @return Idempotency */
    public function idempotency()
    {
        return $this->service('idempotency', function () {
            return new Idempotency($this->db);
        });
    }

    /** @return RateLimit */
    public function rateLimit()
    {
        return $this->service('rateLimit', function () {
            return new RateLimit($this->db);
        });
    }

    /** @return CityService */
    public function city()
    {
        return $this->service('city', function () {
            return new CityService($this);
        });
    }

    /** @return CombatService */
    public function combat()
    {
        return $this->service('combat', function () {
            return new CombatService($this);
        });
    }

    /** @return DefenseService */
    public function defense()
    {
        return $this->service('defense', function () {
            return new DefenseService($this);
        });
    }

    /** @return HuntService */
    public function hunt()
    {
        return $this->service('hunt', function () {
            return new HuntService($this);
        });
    }

    /** @return FpsService */
    public function fps()
    {
        return $this->service('fps', function () {
            return new FpsService($this);
        });
    }

    // ── v7.0 현실 혜택 ──────────────────────────────────────
    // 아래 서비스들은 Wallets 를 부르지 않는다: 게임머니와 현실 혜택은 이어지지 않는다 (docs/REAL_WORLD_REWARDS_POLICY.md).

    /** @return \CG\Service\Features 현실 혜택 기능의 승인 절차 */
    public function features()
    {
        return $this->service('features', function () {
            return new \CG\Service\Features($this);
        });
    }

    /** @return \CG\Infra\Crypto 개인정보 열의 암호화 */
    public function crypto()
    {
        return $this->service('crypto', function () {
            return \CG\Infra\Crypto::fromEnv($this->env);
        });
    }

    /** @return \CG\Service\WebAuth 홈페이지 로그인 (게임에서 받은 일회용 코드) */
    public function webAuth()
    {
        return $this->service('webAuth', function () {
            return new \CG\Service\WebAuth($this);
        });
    }

    /** @return \CG\Service\CityPass CITY PASS: 이용자 쪽 (멤버십, 혜택, 쿠폰) */
    public function cityPass()
    {
        return $this->service('cityPass', function () {
            return new \CG\Service\CityPass($this);
        });
    }

    /** @return \CG\Service\Partners CITY PASS: 운영 쪽 (제휴 업체, 지점, 혜택, 상점 계정, 통계) */
    public function partners()
    {
        return $this->service('partners', function () {
            return new \CG\Service\Partners($this);
        });
    }

    /** @return \CG\Service\MerchantPortal CITY PASS: 상점 쪽 (쿠폰 확인과 사용 처리) */
    public function merchantPortal()
    {
        return $this->service('merchantPortal', function () {
            return new \CG\Service\MerchantPortal($this);
        });
    }

    /** @return \CG\Service\Scholarships 장학: 신청자 쪽 (공고, 신청, 보호자 동의, 진행 상태) */
    public function scholarships()
    {
        return $this->service('scholarships', function () {
            return new \CG\Service\Scholarships($this);
        });
    }

    /** @return \CG\Service\ScholarshipOffice 장학: 운영 쪽 (프로그램, 자격 확인, 심사, 선정, 지급 상태) */
    public function scholarshipOffice()
    {
        return $this->service('scholarshipOffice', function () {
            return new \CG\Service\ScholarshipOffice($this);
        });
    }

    // ── v9.0 도시 성장 · 협동 방어전 · 지방세 ───────────────
    // 도시 재정(Treasury)은 v7.0 의 현실 혜택 서비스를 부르지 않고, 그쪽도 이쪽을 부르지 않는다.

    /** @return \CG\Service\Settings v9.0 운영 설정 */
    public function settings()
    {
        return $this->service('settings', function () {
            return new \CG\Service\Settings($this);
        });
    }

    /** @return \CG\Service\Growth 도시별 전입자 집계, 이사, 확장 단계, 기준 도달 알림 */
    public function growth()
    {
        return $this->service('growth', function () {
            return new \CG\Service\Growth($this);
        });
    }

    /** @return \CG\Service\Modeling 상징거리 모델링의 제작·검수·배포 */
    public function modeling()
    {
        return $this->service('modeling', function () {
            return new \CG\Service\Modeling($this);
        });
    }

    /** @return \CG\Service\Taxes 가상 지방세 */
    public function taxes()
    {
        return $this->service('taxes', function () {
            return new \CG\Service\Taxes($this);
        });
    }

    /** @return \CG\Service\Treasury 도시 재정 */
    public function treasury()
    {
        return $this->service('treasury', function () {
            return new \CG\Service\Treasury($this);
        });
    }

    /** @return \CG\Service\Budget 참여예산 */
    public function budget()
    {
        return $this->service('budget', function () {
            return new \CG\Service\Budget($this);
        });
    }

    /** @return \CG\Service\Dashboard 실시간 대시보드의 요약 (v10.0) */
    public function dashboard()
    {
        return $this->service('dashboard', function () {
            return new \CG\Service\Dashboard($this);
        });
    }

    /** @return \CG\Service\Coop 협동 타워디펜스 (게임 서버 쪽) */
    public function coop()
    {
        return $this->service('coop', function () {
            return new \CG\Service\Coop($this);
        });
    }

    /** 공개 주소의 앞부분 (예: https://www.example.com/cg). 설정하지 않았으면 빈 글자. */
    public function publicBase()
    {
        $base = rtrim($this->env->get('PUBLIC_BASE_URL', ''), '/');
        return preg_match('#^https?://[A-Za-z0-9.\-]+(:\d+)?(/[A-Za-z0-9._~\-/]*)?$#', $base) ? $base : '';
    }

    /** 시스템 로그에 남긴다. 실패해도 요청을 막지 않는다. */
    public function log($level, $source, $message, $detail = null)
    {
        try {
            $this->db->exec(
                'INSERT INTO system_logs (level, source, message, detail, created_at) VALUES (?, ?, ?, ?, ?)',
                [$level, $source, mb_substr($message, 0, 250), $detail, Infra\Clock::sql()]
            );
        } catch (\Throwable $ignored) {
        }
    }
}
