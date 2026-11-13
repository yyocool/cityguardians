<?php
namespace CG\Service;

/**
 * 한 번의 처리 동안 메모리에 올려 둔 도시 기록. CityService 가 만들고 쓴다.
 * 거래가 다시 시도되면 처음부터 다시 읽어야 하므로 요청 사이에 보관하지 않는다.
 */
final class World
{
    /** @var int 침공 판정에 쓰는 씨앗. */
    public $seed = 0;
    /** @var int 마지막으로 처리한 판정 순번. 처음에는 -1. */
    public $lastTick = -1;
    /** @var array 위험도 계산 정책. */
    public $policy = [];
    /** @var array[] [지역 id => 도시] */
    public $cities = [];
    /** @var array[] [지역 id => 진행 중인 침공] */
    public $active = [];
    /** @var array[] [지역 id => 열려 있는 지원 요청] */
    public $open = [];
    /** @var array [지역 id => [시설 id => 피해 단계]] */
    public $damage = [];
    /** @var array [지역 id => [방어시설 id => 모인 기금]] */
    public $funded = [];
    /** @var array [지역 id => true] 저장해야 하는 도시. */
    public $dirty = [];
    /** @var array [시각 => [지역 id => 활성 가디언 수]] */
    public $activeCache = [];
    /** @var array|null [지역 코드 => 랭킹 점수] */
    public $scores = null;
    /** @var array[]|null 순위 순서의 랭킹 점수. */
    public $ordered = null;
    /** @var array|null [지역 id => 최근 침공 수] */
    public $recent = null;

    /** 지역 코드로 도시를 찾는다. */
    public function byCode($code)
    {
        foreach ($this->cities as $city) {
            if ($city['code'] === $code) {
                return $city;
            }
        }
        return null;
    }
}
