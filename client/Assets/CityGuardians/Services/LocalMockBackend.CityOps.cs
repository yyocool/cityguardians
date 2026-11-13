using System;
using System.Collections.Generic;
using System.Globalization;
using CityGuardians.Domain;

namespace CityGuardians.Services
{
    /// <summary>
    /// 모의 서버의 v9.0 설정. 실제 서버의 기본값과 같다: 가상 지방세, 참여예산, 온라인 협동 방어전은 운영팀이 켜기 전까지 닫혀 있다.
    /// </summary>
    public sealed class MockCityOpsOptions
    {
        /// <summary>게스트 계정도 유효 전입자로 셀까. 실제 서버의 기본은 "연결된 계정만"이고, 모의 서버의 계정은 모두 게스트다.</summary>
        public bool countGuests;
        public bool moveEnabled = true;
        public int moveFeeGc = 1000;
        public int moveCooldownDays = 7;
    }

    // v9.0: 도시 성장, 이사, 가상 지방세, 도시 재정, 참여예산, 협동 방어전 (docs/API_SPEC.md 19~22장).
    //
    // 모의 서버가 실제로 하는 것은 도시 성장 조회와 이사다.
    // 지방세·참여예산·온라인 협동 방어전은 실제 서버에서도 운영팀이 켜기 전까지 닫혀 있으므로, 여기서도 "준비 중"으로 답한다
    // (실제 규칙은 server/src/Service 의 Taxes, Treasury, Budget, Coop 과 그 테스트에 있다).
    // 협동 방어전의 연습전은 서버가 필요 없다 (LocalCoopLink).
    public sealed partial class LocalMockBackend
    {
        private const string RequestMove = "residency_move";

        public MockCityOpsOptions CityOps { get; } = new MockCityOpsOptions();

        /// <summary>다른 주민을 대신하는 숫자 (지역 코드 → 유효 전입자 수). 저장하지 않는다. 화면과 테스트에서 성장 단계를 보려고 쓴다.</summary>
        private readonly Dictionary<string, int> _seededResidents = new Dictionary<string, int>();

        private static readonly int[] MilestoneThresholds = { 100, 500, 1000, 3000 };
        private static readonly string[] MilestoneCodes = { "landmark_review", "street_candidate", "street_open", "special_zone_review" };
        private static readonly string[] MilestoneNames = { "상징물 제작 검토", "상징거리 제작 후보", "상징거리 개방 목표", "추가 특화 구역 검토" };
        private static readonly string[] MilestoneNotes =
        {
            "도시를 대표하는 상징물 하나를 만들지 검토합니다.", "상징거리를 만들 후보 도시가 됩니다.",
            "상징거리를 제작·검수해 정식으로 개방하는 것을 목표로 합니다.", "상징거리 밖의 특화 구역을 더 만들지 검토합니다.",
        };
        private const int StreetThreshold = 1000;

        /// <summary>테스트와 개발용: 그 도시에 다른 유효 전입자가 이만큼 있다고 친다.</summary>
        public void MockSetOtherResidents(string regionCode, int count)
        {
            if (Regions.FindRegion(regionCode) == null) throw new ArgumentException("없는 지역: " + regionCode);
            _seededResidents[regionCode] = Math.Max(0, count);
        }

        // ── 도시 성장 ───────────────────────────────────────────

        private int ValidResidents(string regionCode)
        {
            _seededResidents.TryGetValue(regionCode, out int count);
            if (CityOps.countGuests)
            {
                RegionRow row = _db.regions.Find(r => r.code == regionCode);
                if (row != null) count += row.residents;
            }
            return count;
        }

        private static List<GrowthMilestoneDto> Milestones(int valid, bool withReached)
        {
            var list = new List<GrowthMilestoneDto>();
            for (int i = 0; i < MilestoneThresholds.Length; i++)
                list.Add(new GrowthMilestoneDto
                {
                    code = MilestoneCodes[i], name = MilestoneNames[i], threshold = MilestoneThresholds[i], description = MilestoneNotes[i],
                    opensStreet = MilestoneThresholds[i] == StreetThreshold, reached = withReached && valid >= MilestoneThresholds[i],
                });
            return list;
        }

        private CityGrowthDto ToGrowth(string regionCode, bool detail)
        {
            int valid = ValidResidents(regionCode);
            int stage = 0;
            int next = 0;
            for (int i = 0; i < MilestoneThresholds.Length; i++)
            {
                if (valid >= MilestoneThresholds[i]) stage = i + 1;
                else if (next == 0) next = MilestoneThresholds[i];
            }
            var dto = new CityGrowthDto
            {
                regionCode = regionCode, name = Regions.ShortName(regionCode), sidoCode = regionCode.Length >= 2 ? regionCode.Substring(0, 2) : "",
                validResidents = valid, stage = stage, stageName = stage > 0 ? MilestoneNames[stage - 1] : null, nextThreshold = next,
                streetThreshold = StreetThreshold, thresholdReached = valid >= StreetThreshold,
                // 모의 서버에는 제작 작업이 없다. 기준에 닿아도 상징거리는 제작·검수·배포를 거쳐야 열린다.
                modelingStatus = ModelingStatuses.NotStarted, streetOpen = false,
            };
            if (detail)
            {
                dto.cumulativeResidents = valid;
                dto.milestones = Milestones(valid, true);
            }
            return dto;
        }

        public void GetCityGrowth(Action<ApiResult<CityGrowthListDto>> done)
        {
            if (!RequirePlayer(done)) return;
            var dto = new CityGrowthListDto { milestones = Milestones(0, false), countRule = CityOps.countGuests ? "linked_or_proven" : "linked" };
            var seen = new HashSet<string>();
            foreach (var pair in _seededResidents)
                if (seen.Add(pair.Key)) dto.cities.Add(ToGrowth(pair.Key, false));
            for (int i = 0; i < _db.regions.Count; i++)
                if (_db.regions[i].residents > 0 && seen.Add(_db.regions[i].code)) dto.cities.Add(ToGrowth(_db.regions[i].code, false));
            dto.cities.Sort((a, b) => a.validResidents != b.validResidents
                ? b.validResidents.CompareTo(a.validResidents) : string.CompareOrdinal(a.regionCode, b.regionCode));
            done(ApiResult<CityGrowthListDto>.Success(dto));
        }

        public void GetCityGrowthDetail(string regionCode, Action<ApiResult<CityGrowthDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<CityGrowthDto>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return;
            }
            done(ApiResult<CityGrowthDto>.Success(ToGrowth(regionCode, true)));
        }

        // ── 주거 도시와 이사 ────────────────────────────────────

        /// <summary>다시 이사할 수 있는 때. 지금 할 수 있으면 null.</summary>
        private DateTime? NextMoveAt(PlayerRow row, DateTime now)
        {
            if (string.IsNullOrEmpty(row.registeredAt) || CityOps.moveCooldownDays <= 0) return null;
            DateTime at = DateTime.Parse(row.registeredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()
                .AddDays(CityOps.moveCooldownDays);
            return at > now ? at : (DateTime?)null;
        }

        public void GetResidency(Action<ApiResult<ResidencyInfoDto>> done)
        {
            if (!RequirePlayer(done)) return;
            DateTime now = _clock.UtcNow;
            var dto = new ResidencyInfoDto
            {
                residency = IsResident(_player) ? "resident" : "stranger", countRule = CityOps.countGuests ? "linked_or_proven" : "linked",
                moveEnabled = CityOps.moveEnabled, moveFeeGc = CityOps.moveFeeGc, moveCooldownDays = CityOps.moveCooldownDays, gc = _player.balance,
            };
            if (IsResident(_player))
            {
                dto.regionCode = _player.regionCode;
                dto.regionName = Regions.ShortName(_player.regionCode);
                dto.since = _player.registeredAt;
                // 모의 서버의 계정은 게스트다. 실제 서버는 계정을 연결해야 유효 전입자로 센다.
                dto.counted = CityOps.countGuests;
                dto.notCountedReason = dto.counted ? null : "guest_account";
                DateTime? next = NextMoveAt(_player, now);
                dto.canMoveAt = next.HasValue ? next.Value.ToString("o") : null;
            }
            done(ApiResult<ResidencyInfoDto>.Success(dto));
        }

        public void MoveResidency(string regionCode, string requestId, Action<ApiResult<MoveResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (!IsValidRequestId(requestId))
            {
                done(ApiResult<MoveResultDto>.Fail(ApiErrors.InvalidRequest, "요청 번호가 올바르지 않아요."));
                return;
            }
            DateTime now = _clock.UtcNow;
            RequestRow seen = FindRequest(_player, requestId);
            if (seen != null)
            {
                if (seen.kind != RequestMove || seen.itemCode != regionCode)
                {
                    done(ApiResult<MoveResultDto>.Fail(ApiErrors.Conflict, "이미 다른 일에 쓴 요청 번호예요."));
                    return;
                }
                // 같은 요청이 다시 왔다: 또 이사하지 않고, 수수료도 또 받지 않는다.
                DateTime? again = NextMoveAt(_player, now);
                done(ApiResult<MoveResultDto>.Success(new MoveResultDto
                {
                    regionCode = _player.regionCode, regionName = Regions.ShortName(_player.regionCode), feeGc = seen.amount, gc = _player.balance,
                    canMoveAt = again.HasValue ? again.Value.ToString("o") : null,
                }));
                return;
            }

            // 먼저 모두 확인하고, 그다음에 한꺼번에 바꾼다 (실제 서버에서는 하나의 DB 트랜잭션).
            if (!CityOps.moveEnabled)
            {
                done(ApiResult<MoveResultDto>.Fail(ApiErrors.Locked, "지금은 이사할 수 없어요."));
                return;
            }
            if (!IsResident(_player))
            {
                done(ApiResult<MoveResultDto>.Fail(ApiErrors.Conflict, "먼저 전입신고를 해 주세요."));
                return;
            }
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<MoveResultDto>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return;
            }
            if (regionCode == _player.regionCode)
            {
                done(ApiResult<MoveResultDto>.Fail(ApiErrors.Conflict, "이미 이 도시에 살고 있어요."));
                return;
            }
            if (NextMoveAt(_player, now).HasValue)
            {
                done(ApiResult<MoveResultDto>.Fail(ApiErrors.Locked, $"전입한 지 {CityOps.moveCooldownDays}일이 지나야 이사할 수 있어요."));
                return;
            }
            int fee = Math.Max(0, CityOps.moveFeeGc);
            if (_player.balance < fee)
            {
                done(ApiResult<MoveResultDto>.Fail(ApiErrors.InsufficientFunds, $"이사 수수료 {fee:N0} GC가 모자라요."));
                return;
            }

            string previous = _player.regionCode;
            if (fee > 0) ApplyLedger(_player, -fee, LedgerReasons.ResidencyMove, regionCode, now);
            // 한 계정은 한 도시에만 산다: 이전 도시에서 빠지고 새 도시에 들어간다.
            RegionRow from = RegionRowFor(previous);
            if (from.residents > 0) from.residents--;
            RegionRowFor(regionCode).residents++;
            _player.regionCode = regionCode;
            _player.registeredAt = now.ToString("o");
            _player.lastActiveAt = _player.registeredAt;
            // 다른 도시를 지원하러 나가 있었다면 돌아온다.
            _player.supportRequestId = null;
            RememberRequest(_player, requestId, RequestMove, regionCode, fee);
            SaveDb();

            DateTime? next = NextMoveAt(_player, now);
            done(ApiResult<MoveResultDto>.Success(new MoveResultDto
            {
                regionCode = regionCode, regionName = Regions.ShortName(regionCode), previousRegionCode = previous, feeGc = fee, gc = _player.balance,
                canMoveAt = next.HasValue ? next.Value.ToString("o") : null,
            }));
        }

        public void GetCityContent(Action<ApiResult<CityContentListDto>> done)
        {
            if (!RequirePlayer(done)) return;
            // 모의 서버에는 배포된 지역 콘텐츠가 없다.
            done(ApiResult<CityContentListDto>.Success(new CityContentListDto { available = false }));
        }

        // ── 가상 지방세, 도시 재정 ──────────────────────────────

        private const string TaxNotice = "게임 안에서만 쓰는 가상의 세금이에요. 실제 돈이나 현실의 세금과는 아무 관계가 없어요.";
        private const string TreasuryNotice = "게임 안의 도시 재정이에요. 실제 돈이 아니고, CITY PASS나 장학 사업의 예산과는 따로예요.";

        private static TaxPolicyDto MockPolicy(string type, string name, string description, int rateBp, int exempt, int minLevel, int graceDays,
            int cap, string period, params string[] applies)
        {
            var dto = new TaxPolicyDto
            {
                taxType = type, name = name, description = description, active = false, rateBp = rateBp, exemptAmount = exempt, minLevel = minLevel,
                graceDays = graceDays, capGc = cap, period = period, included = type == TaxTypes.Trade,
                // 실제 서버의 기본값과 같다: 운영팀이 켜기 전에는 걷지 않는다.
                exempt = true, exemptReason = "policy_off", exemptMessage = "지금은 걷지 않는 세금이에요.",
            };
            for (int i = 0; i + 1 < applies.Length; i += 2) dto.appliesTo.Add(new TaxAppliesDto { reason = applies[i], name = applies[i + 1] });
            return dto;
        }

        public void GetTaxPolicies(Action<ApiResult<TaxPoliciesDto>> done)
        {
            if (!RequirePlayer(done)) return;
            var dto = new TaxPoliciesDto { notice = TaxNotice };
            dto.policies.Add(MockPolicy(TaxTypes.Trade, "거래세", "장비와 타워를 살 때 치르는 값에 들어 있는 세금이에요. 값이 더 비싸지지 않아요.",
                300, 100, 3, 7, 0, "transaction", LedgerReasons.GearPurchase, "전투 장비 구매", LedgerReasons.GearUpgrade, "전투 장비 강화",
                LedgerReasons.TowerUnlock, "타워 해금"));
            dto.policies.Add(MockPolicy(TaxTypes.Business, "사업세", "전투 보상으로 번 GC 가운데 하루 면제액을 넘는 부분에 붙는 세금이에요.",
                500, 1000, 5, 14, 300, "transaction", LedgerReasons.BattleReward, "방어전 보상", LedgerReasons.FpsReward, "긴급 출동 보상",
                LedgerReasons.HuntReward, "사냥 보상"));
            dto.policies.Add(MockPolicy(TaxTypes.Property, "재산세", "가진 GC 가운데 면제액을 넘는 부분에 한 주에 한 번 붙는 세금이에요.",
                100, 10000, 10, 30, 500, "weekly"));
            done(ApiResult<TaxPoliciesDto>.Success(dto));
        }

        public void GetTaxTransactions(Action<ApiResult<TaxTransactionsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<TaxTransactionsDto>.Success(new TaxTransactionsDto { gc = _player.balance }));
        }

        public void GetTreasury(string regionCode, Action<ApiResult<TreasuryDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<TreasuryDto>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return;
            }
            var dto = new TreasuryDto { regionCode = regionCode, regionName = Regions.ShortName(regionCode), reservePercent = 10, notice = TreasuryNotice };
            dto.income.Add(new TreasuryIncomeDto { taxType = TaxTypes.Trade, taxName = "거래세" });
            dto.income.Add(new TreasuryIncomeDto { taxType = TaxTypes.Business, taxName = "사업세" });
            dto.income.Add(new TreasuryIncomeDto { taxType = TaxTypes.Property, taxName = "재산세" });
            done(ApiResult<TreasuryDto>.Success(dto));
        }

        public void GetExpenditures(string regionCode, Action<ApiResult<ExpendituresDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<ExpendituresDto>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return;
            }
            done(ApiResult<ExpendituresDto>.Success(new ExpendituresDto { regionCode = regionCode }));
        }

        // ── 참여예산 ────────────────────────────────────────────

        private const string BudgetClosed = "참여예산은 아직 열리지 않았어요.";

        public void GetBudgetOptions(Action<ApiResult<BudgetOptionsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            var dto = new BudgetOptionsDto
            {
                enabled = false, maxRequestGc = 50000, proposerMinLevel = 3, voterMinLevel = 2, voterMinResidentDays = 3, voteDays = 7, quorumVotes = 5,
                notice = "한 사람이 한 표를 가져요. 가진 GC나 낸 세금이 많다고 표가 늘어나지 않아요.",
            };
            string[] categories =
            {
                "defense_facility", "공동 방어시설", "defense_maintenance", "방어시설 유지보수", "infrastructure", "도시 인프라",
                "npc_welfare", "NPC 복지시설", "education", "게임 내 교육시설", "event", "도시 행사",
            };
            for (int i = 0; i < categories.Length; i += 2)
                dto.categories.Add(new BudgetCategoryDto
                {
                    category = categories[i], name = categories[i + 1],
                    effectType = i == 0 ? BudgetEffects.StructureFund : i == 2 ? BudgetEffects.FacilityRepair : BudgetEffects.Record,
                });
            done(ApiResult<BudgetOptionsDto>.Success(dto));
        }

        public void GetBudgetProposals(string regionCode, Action<ApiResult<BudgetListDto>> done)
        {
            if (!RequirePlayer(done)) return;
            if (Regions.FindRegion(regionCode) == null)
            {
                done(ApiResult<BudgetListDto>.Fail(ApiErrors.NotFound, "없는 지역이에요."));
                return;
            }
            done(ApiResult<BudgetListDto>.Success(new BudgetListDto { regionCode = regionCode, regionName = Regions.ShortName(regionCode) }));
        }

        public void ProposeBudget(string category, string targetCode, string title, string description, int requestedGc, string requestId,
            Action<ApiResult<BudgetProposalResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<BudgetProposalResultDto>.Fail(ApiErrors.FeatureDisabled, BudgetClosed));
        }

        public void VoteBudget(string proposalId, string choice, string requestId, Action<ApiResult<BudgetProposalResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<BudgetProposalResultDto>.Fail(ApiErrors.FeatureDisabled, BudgetClosed));
        }

        // ── 협동 타워디펜스 ─────────────────────────────────────

        private const string CoopClosed = "온라인 협동 방어전은 아직 열리지 않았어요. 연습전은 지금도 할 수 있어요.";

        public void GetCoopConfig(Action<ApiResult<CoopConfigDto>> done)
        {
            if (!RequirePlayer(done)) return;
            // 모의 서버에는 실시간 서버가 없다. 실제 서버도 운영팀이 켜고 실시간 서버가 준비되기 전에는 이렇게 답한다.
            done(ApiResult<CoopConfigDto>.Success(new CoopConfigDto
            {
                available = false, unavailableReason = "realtime_not_configured", dailyRewardLimit = 5, rewardsLeftToday = 5,
                roles = CoopDefaults.Roles(), roleRequired = false, regionCode = IsResident(_player) ? _player.regionCode : null,
            }));
        }

        public void GetCoopSessions(Action<ApiResult<CoopSessionsDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<CoopSessionsDto>.Success(new CoopSessionsDto { regionCode = IsResident(_player) ? _player.regionCode : null }));
        }

        public void CreateCoopSession(string zoneCode, int maxPlayers, string roleCode, string requestId, Action<ApiResult<CoopSessionResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<CoopSessionResultDto>.Fail(ApiErrors.FeatureDisabled, CoopClosed));
        }

        public void JoinCoopSession(string sessionId, string roleCode, string requestId, Action<ApiResult<CoopSessionResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<CoopSessionResultDto>.Fail(ApiErrors.FeatureDisabled, CoopClosed));
        }

        public void LeaveCoopSession(string sessionId, Action<ApiResult<CoopSessionResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<CoopSessionResultDto>.Fail(ApiErrors.FeatureDisabled, CoopClosed));
        }

        public void GetCoopTicket(string sessionId, Action<ApiResult<CoopTicketDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<CoopTicketDto>.Fail(ApiErrors.FeatureDisabled, CoopClosed));
        }

        public void GetCoopResult(string sessionId, Action<ApiResult<CoopResultDto>> done)
        {
            if (!RequirePlayer(done)) return;
            done(ApiResult<CoopResultDto>.Fail(ApiErrors.FeatureDisabled, CoopClosed));
        }
    }
}
