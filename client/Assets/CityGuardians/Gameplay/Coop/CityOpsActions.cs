using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 도시 살림 화면 (작전 본부 → 도시 살림): 도시 성장, 이사, 가상 지방세, 도시 재정, 참여예산.
    /// (docs/CITY_GROWTH_SYSTEM.md, CITY_TAX_SYSTEM.md, CITY_TREASURY_SYSTEM.md, PARTICIPATORY_BUDGET.md)
    ///
    /// 여기서 보여 주는 숫자는 모두 서버가 준 값이다. 전입자 수, 세율, 세액, 재정 잔액, 표의 수를 클라이언트가 정하거나 보내는 곳은 없다.
    /// 여기의 GC 는 게임 안에서만 쓰는 가상의 돈이다. 실제 돈, 현실의 세금, CITY PASS, 장학 사업과는 관계가 없다.
    /// </summary>
    public static class CityOpsActions
    {
        public const string MenuTag = "city_ops";
        public const string Title = "도시 살림";

        private static readonly int[] RequestSteps = { 1000, 3000, 5000, 10000, 30000 };

        public static void Open(Action onBack = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            Action reopen = () => Open(onBack);
            bool resident = session.IsResident;
            string home = resident ? RegionService.ShortName(session.Me.regionCode) : null;
            ChoiceMenu.Instance.Open(Title,
                resident ? $"{home}의 주민이에요.\n여기의 세금과 예산은 게임 안의 가상 화폐(GC)로만 움직여요."
                    : "아직 전입신고를 하지 않았어요.\n전국 도시의 성장은 누구나 볼 수 있어요.",
                new ChoiceOption
                {
                    label = "도시 성장",
                    detail = "전입자 수, 성장 단계, 상징거리 제작 현황을 봐요.",
                    onChosen = () => OpenGrowth(session, reopen),
                },
                new ChoiceOption
                {
                    label = "이사",
                    cost = resident ? "" : "주민 전용",
                    detail = resident ? "다른 도시로 주거지를 옮겨요. 한 계정은 한 도시에만 살 수 있어요." : "먼저 전입신고를 해 주세요.",
                    enabled = resident,
                    onChosen = () => OpenMove(session, reopen),
                },
                new ChoiceOption
                {
                    label = "지방세",
                    detail = "거래세·사업세·재산세의 정책과 내가 낸 세금을 봐요.",
                    onChosen = () => OpenTax(session, reopen),
                },
                new ChoiceOption
                {
                    label = "도시 재정",
                    cost = resident ? "" : "주민 전용",
                    detail = resident ? "걷힌 세금이 얼마이고 어디에 쓰였는지 봐요." : "전입신고를 하면 우리 도시의 재정을 볼 수 있어요.",
                    enabled = resident,
                    onChosen = () => OpenTreasury(session, reopen),
                },
                new ChoiceOption
                {
                    label = "참여예산",
                    cost = resident ? "한 사람 한 표" : "주민 전용",
                    detail = resident ? "도시 예산을 어디에 쓸지 제안하고 투표해요." : "전입신고를 하면 참여할 수 있어요.",
                    enabled = resident,
                    onChosen = () => OpenBudget(session, reopen),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static bool Alive(GameSession session) => GameSession.Current == session;

        /// <summary>"2026-11-13T09:30:00Z" → "11-13 09:30".</summary>
        public static string ShortTime(string iso)
        {
            if (string.IsNullOrEmpty(iso) || iso.Length < 16 || iso[10] != 'T') return iso ?? "";
            return iso.Substring(5, 5) + " " + iso.Substring(11, 5);
        }

        // ── 도시 성장 ───────────────────────────────────────────

        private static void OpenGrowth(GameSession session, Action onBack)
        {
            Action reopen = () => OpenGrowth(session, onBack);
            var options = new List<ChoiceOption>();
            if (session.IsResident)
                options.Add(new ChoiceOption
                {
                    label = "우리 도시",
                    cost = RegionService.ShortName(session.Me.regionCode),
                    detail = "우리 도시의 전입자 수와 성장 단계를 봐요.",
                    onChosen = () => ShowCity(session, session.Me.regionCode, reopen),
                });
            options.Add(new ChoiceOption
            {
                label = "전국 도시",
                detail = "전입자가 많은 도시와 상징거리가 열린 도시를 봐요.",
                onChosen = () => ShowNation(session, reopen),
            });
            options.Add(new ChoiceOption
            {
                label = "다른 도시 찾아보기",
                detail = "지역을 골라 그 도시의 성장을 봐요.",
                onChosen = () => BenefitUi.PickRegion(session, "도시 고르기", code => ShowCity(session, code, reopen), reopen),
            });
            ChoiceMenu.Instance.Open("도시 성장", "주민이 늘어나면 도시가 단계를 밟아 자라고,\n기준에 닿은 도시부터 상징거리를 만들어 열어요.", options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void ShowCity(GameSession session, string regionCode, Action onDone)
        {
            session.Backend.GetCityGrowthDetail(regionCode, result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "도시 성장");
                    onDone?.Invoke();
                    return;
                }
                BenefitUi.Board(RegionService.ShortName(regionCode) + "의 성장", GrowthLines(result.data), onDone);
            });
        }

        /// <summary>한 도시의 성장 안내.</summary>
        public static List<string> GrowthLines(CityGrowthDto city)
        {
            var lines = new List<string>
            {
                $"유효 전입자  {city.validResidents:N0}명     누적 {city.cumulativeResidents:N0}명",
                $"최근 7일 {Signed(city.growth7d)}명     최근 30일 {Signed(city.growth30d)}명",
                string.IsNullOrEmpty(city.stageName) ? "성장 단계  아직 첫 단계 전이에요" : $"성장 단계  {city.stage}단계 · {city.stageName}",
                city.nextThreshold > 0
                    ? $"다음 단계까지  {Math.Max(0, city.nextThreshold - city.validResidents):N0}명 (기준 {city.nextThreshold:N0}명)"
                    : "모든 성장 단계에 닿았어요.",
                city.streetOpen ? $"상징거리  열렸어요 ({ShortTime(city.openedAt)})"
                    : $"상징거리  {ModelingStatuses.DisplayName(city.modelingStatus)} · 기준 {city.streetThreshold:N0}명" +
                      (city.thresholdReached ? " 달성" : ""),
            };
            for (int i = 0; i < city.milestones.Count && lines.Count < BenefitUi.BoardLines - 1; i++)
            {
                GrowthMilestoneDto step = city.milestones[i];
                lines.Add($"{(step.reached ? "●" : "○")} {step.threshold:N0}명  {step.name}");
            }
            lines.Add("계정을 연결한 주민만 세요. 게스트 계정으로는 늘지 않아요.");
            return lines;
        }

        private static string Signed(int value) => value > 0 ? "+" + value.ToString("N0") : value.ToString("N0");

        private static void ShowNation(GameSession session, Action onDone)
        {
            session.Backend.GetCityGrowth(result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "전국 도시 성장");
                    onDone?.Invoke();
                    return;
                }
                var cities = new List<CityGrowthDto>(result.data.cities);
                cities.Sort((a, b) => b.validResidents != a.validResidents ? b.validResidents.CompareTo(a.validResidents)
                    : string.CompareOrdinal(a.regionCode, b.regionCode));
                int open = 0, reached = 0;
                for (int i = 0; i < cities.Count; i++)
                {
                    if (cities[i].streetOpen) open++;
                    if (cities[i].thresholdReached) reached++;
                }
                var lines = new List<string> { $"상징거리가 열린 도시 {open}곳     기준에 닿은 도시 {reached}곳" };
                for (int i = 0; i < cities.Count && lines.Count < BenefitUi.BoardLines - 1; i++)
                {
                    CityGrowthDto city = cities[i];
                    if (city.validResidents <= 0) break;
                    string street = city.streetOpen ? "상징거리 열림" : ModelingStatuses.DisplayName(city.modelingStatus);
                    lines.Add(BenefitUi.Clip($"{i + 1}. {RegionService.ShortName(city.regionCode)}  {city.validResidents:N0}명 · {street}"));
                }
                if (lines.Count == 1) lines.Add("아직 유효 전입자가 있는 도시가 없어요.");
                var steps = new List<string>();
                for (int i = 0; i < result.data.milestones.Count; i++) steps.Add(result.data.milestones[i].threshold.ToString("N0"));
                if (steps.Count > 0) lines.Add("성장 기준  " + string.Join(" · ", steps) + "명");
                BenefitUi.Board("전국 도시 성장", lines, onDone);
            });
        }

        // ── 이사 ────────────────────────────────────────────────

        private static void OpenMove(GameSession session, Action onBack)
        {
            session.Backend.GetResidency(result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "전입 상태");
                    onBack?.Invoke();
                    return;
                }
                ResidencyInfoDto info = result.data;
                Action reopen = () => OpenMove(session, onBack);
                bool waiting = !string.IsNullOrEmpty(info.canMoveAt);
                bool poor = info.gc < info.moveFeeGc;
                string counted = info.counted ? "유효 전입자로 세어지고 있어요."
                    : info.notCountedReason == "guest_account" ? "게스트 계정이라 도시의 유효 전입자 수에는 들어가지 않아요."
                    : "활동이 확인되면 유효 전입자로 세어져요.";
                ChoiceMenu.Instance.Open("이사",
                    $"지금 사는 곳  {info.regionName} ({ShortTime(info.since)}부터)\n{counted}\n" +
                    $"이사한 뒤 {info.moveCooldownDays}일 동안은 다시 이사할 수 없어요.",
                    new ChoiceOption
                    {
                        label = "이사할 도시 고르기",
                        cost = info.moveFeeGc > 0 ? $"수수료 {info.moveFeeGc:N0} GC" : "수수료 없음",
                        detail = !info.moveEnabled ? "지금은 이사할 수 없어요."
                            : waiting ? $"{ShortTime(info.canMoveAt)}부터 이사할 수 있어요."
                            : poor ? "이사 수수료가 모자라요."
                            : "주거 도시를 옮기면 이전 도시에서 빠지고 새 도시의 주민이 돼요.",
                        enabled = info.moveEnabled && !waiting && !poor,
                        onChosen = () => BenefitUi.PickRegion(session, "이사할 도시", code => ConfirmMove(session, info, code, reopen), reopen),
                    });
                ChoiceMenu.Instance.Tag = MenuTag;
                ChoiceMenu.Instance.OnCancelled = onBack;
            });
        }

        private static void ConfirmMove(GameSession session, ResidencyInfoDto info, string regionCode, Action onBack)
        {
            if (!Alive(session)) return;
            if (regionCode == info.regionCode)
            {
                GameEvents.ShowToast("이미 이 도시에 살고 있어요.");
                onBack?.Invoke();
                return;
            }
            string name = session.Backend.Regions.FullName(regionCode);
            ChoiceMenu.Instance.Open("이사 확인",
                $"{info.regionName} → {name}\n수수료 {info.moveFeeGc:N0} GC를 내고, {info.moveCooldownDays}일 동안 다시 이사할 수 없어요.",
                new ChoiceOption
                {
                    label = "이사하기",
                    cost = info.moveFeeGc > 0 ? $"{info.moveFeeGc:N0} GC" : "",
                    detail = "도시 랭킹과 방어전은 새 도시의 것으로 바뀌어요.",
                    onChosen = () => Move(session, regionCode, onBack),
                },
                new ChoiceOption { label = "그만두기", detail = "지금 도시에 그대로 살아요.", onChosen = onBack });
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void Move(GameSession session, string regionCode, Action onDone)
        {
            session.Backend.MoveResidency(regionCode, BenefitUi.NewRequestId(), result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    if (result.errorCode == ApiErrors.InsufficientFunds) GameEvents.ShowToast("이사 수수료가 모자라요.");
                    else if (result.errorCode == ApiErrors.Locked) GameEvents.ShowToast(result.message ?? "지금은 이사할 수 없어요.");
                    else BenefitUi.Failed(result, "이사");
                    onDone?.Invoke();
                    return;
                }
                MoveResultDto moved = result.data;
                // 주거 도시, GC, 완장 색, 도시 현황을 서버의 값으로 다시 맞춘다.
                session.Backend.GetMe(me =>
                {
                    if (!Alive(session)) return;
                    if (me.ok) session.ApplyMe(me.data);
                    PlayerController player = PlayerController.Instance;
                    if (player != null) PlayerFactory.ApplyArmband(player);
                    CityRiskService.Refresh();
                    RegionService.RefreshRankings();
                    RegionService.RefreshSupport();
                    Sfx.Play(Sfx.Id.Victory);
                    string fee = moved.feeGc > 0 ? $"\n\n이사 수수료  −{moved.feeGc:N0} GC" : "";
                    DialogueRunner.Instance.ShowPanel("이사 완료",
                        $"{session.Backend.Regions.FullName(moved.regionCode)}의 주민이 되었어요.{fee}\n\n" +
                        $"다음 이사는 {ShortTime(moved.canMoveAt)}부터 할 수 있어요.", false, onDone);
                });
            });
        }

        // ── 지방세 ──────────────────────────────────────────────

        private static void OpenTax(GameSession session, Action onBack)
        {
            Action reopen = () => OpenTax(session, onBack);
            ChoiceMenu.Instance.Open("지방세",
                "게임 안의 가상 화폐(GC)로만 걷는 세금이에요.\n실제 돈이나 현실의 세금과는 관계가 없어요.",
                new ChoiceOption
                {
                    label = "세금 정책",
                    detail = "세금의 종류, 세율, 면제 기준을 봐요.",
                    onChosen = () => ShowPolicies(session, reopen),
                },
                new ChoiceOption
                {
                    label = "내가 낸 세금",
                    detail = "세금을 낸 명세를 봐요. 낸 세금은 도시 재정으로 가요.",
                    onChosen = () => ShowMyTaxes(session, reopen),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void ShowPolicies(GameSession session, Action onDone)
        {
            session.Backend.GetTaxPolicies(result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "세금 정책");
                    onDone?.Invoke();
                    return;
                }
                BenefitUi.Board("세금 정책", TaxLines(result.data), onDone);
            });
        }

        /// <summary>세금 정책 안내. 꺼져 있는 세금은 걷지 않는다고 분명히 적는다.</summary>
        public static List<string> TaxLines(TaxPoliciesDto data)
        {
            var lines = new List<string>();
            for (int i = 0; i < data.policies.Count; i++)
            {
                TaxPolicyDto policy = data.policies[i];
                string state = !policy.active ? "걷지 않아요" : policy.exempt ? "나는 면제" : "걷는 중";
                lines.Add($"{policy.name}  {policy.RateText}  ·  {state}");
                string detail = policy.active && policy.exempt && !string.IsNullOrEmpty(policy.exemptMessage) ? policy.exemptMessage
                    : policy.included ? "값에 들어 있는 세금이라 값이 더 비싸지지 않아요."
                    : policy.description;
                if (!string.IsNullOrEmpty(detail)) lines.Add("   " + BenefitUi.Clip(detail, BenefitUi.LineMax - 3));
            }
            if (lines.Count == 0) lines.Add("정해진 세금이 없어요.");
            if (data.businessTaxToday > 0) lines.Add($"오늘 낸 사업세  {data.businessTaxToday:N0} GC");
            if (!string.IsNullOrEmpty(data.notice)) lines.Add(BenefitUi.Clip(data.notice));
            return lines;
        }

        private static void ShowMyTaxes(GameSession session, Action onDone)
        {
            session.Backend.GetTaxTransactions(result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "세금 명세");
                    onDone?.Invoke();
                    return;
                }
                var lines = new List<string>();
                for (int i = 0; i < result.data.totals.Count; i++)
                {
                    TaxTotalDto total = result.data.totals[i];
                    lines.Add($"{total.taxName}  모두 {total.totalGc:N0} GC ({total.count}번)");
                }
                for (int i = 0; i < result.data.transactions.Count && lines.Count < BenefitUi.BoardLines - 1; i++)
                {
                    TaxTransactionDto row = result.data.transactions[i];
                    string kind = row.included ? "값에 포함" : "따로 냄";
                    lines.Add(BenefitUi.Clip($"{ShortTime(row.at)}  {row.taxName} {row.taxGc:N0} GC · {row.regionName} ({kind})"));
                }
                if (lines.Count == 0) lines.Add("아직 낸 세금이 없어요.");
                BenefitUi.Board("내가 낸 세금", lines, onDone);
            });
        }

        // ── 도시 재정 ───────────────────────────────────────────

        private static void OpenTreasury(GameSession session, Action onBack)
        {
            Action reopen = () => OpenTreasury(session, onBack);
            string region = session.Me.regionCode;
            ChoiceMenu.Instance.Open("도시 재정",
                $"{RegionService.ShortName(region)}의 재정이에요. 누구나 볼 수 있어요.\nCITY PASS나 장학 사업의 예산과는 따로예요.",
                new ChoiceOption
                {
                    label = "재정 현황",
                    detail = "남은 예산, 걷힌 세금, 분야별로 쓴 금액을 봐요.",
                    onChosen = () => ShowTreasury(session, region, reopen),
                },
                new ChoiceOption
                {
                    label = "예산 집행 내역",
                    detail = "도시 예산이 어디에 쓰였는지 봐요.",
                    onChosen = () => ShowExpenditures(session, region, reopen),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void ShowTreasury(GameSession session, string regionCode, Action onDone)
        {
            session.Backend.GetTreasury(regionCode, result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "도시 재정");
                    onDone?.Invoke();
                    return;
                }
                TreasuryDto data = result.data;
                var lines = new List<string>
                {
                    $"일반 회계  {data.generalGc:N0} GC",
                    $"예비비  {data.reserveGc:N0} GC (걷힌 세금의 {data.reservePercent}%)",
                };
                for (int i = 0; i < data.income.Count; i++)
                    lines.Add($"걷힌 {data.income[i].taxName}  {data.income[i].totalGc:N0} GC (최근 30일 {data.income[i].last30dGc:N0})");
                for (int i = 0; i < data.spent.Count && lines.Count < BenefitUi.BoardLines - 1; i++)
                    lines.Add($"쓴 곳  {data.spent[i].categoryName}  {data.spent[i].totalGc:N0} GC ({data.spent[i].count}건)");
                if (data.income.Count == 0 && data.spent.Count == 0) lines.Add("아직 걷힌 세금도, 쓴 예산도 없어요.");
                if (!string.IsNullOrEmpty(data.notice)) lines.Add(BenefitUi.Clip(data.notice));
                BenefitUi.Board(RegionService.ShortName(regionCode) + "의 재정", lines, onDone);
            });
        }

        private static void ShowExpenditures(GameSession session, string regionCode, Action onDone)
        {
            session.Backend.GetExpenditures(regionCode, result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "예산 집행 내역");
                    onDone?.Invoke();
                    return;
                }
                var lines = new List<string>();
                for (int i = 0; i < result.data.expenditures.Count; i++)
                {
                    ExpenditureDto row = result.data.expenditures[i];
                    string how = row.byVote ? " · 주민 투표" : "";
                    lines.Add(BenefitUi.Clip($"{ShortTime(row.at)}  {row.categoryName}  −{row.amountGc:N0} GC{how}"));
                    lines.Add("   " + BenefitUi.Clip(string.IsNullOrEmpty(row.effectDetail) ? row.title : $"{row.title} · {row.effectDetail}",
                        BenefitUi.LineMax - 3));
                }
                if (lines.Count == 0) lines.Add("아직 쓴 예산이 없어요.");
                BenefitUi.Board("예산 집행 내역", lines, onDone);
            });
        }

        // ── 참여예산 ────────────────────────────────────────────

        private static void OpenBudget(GameSession session, Action onBack)
        {
            Action reopen = () => OpenBudget(session, onBack);
            session.Backend.GetBudgetProposals(session.Me.regionCode, result =>
            {
                if (!Alive(session)) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "참여예산");
                    onBack?.Invoke();
                    return;
                }
                BudgetListDto data = result.data;
                BudgetOptionsDto rules = data.options ?? new BudgetOptionsDto();
                if (!rules.enabled)
                {
                    // 운영팀이 열기 전에는 안내만 한다.
                    GameEvents.ShowToast("참여예산은 아직 열리지 않았어요.");
                    onBack?.Invoke();
                    return;
                }

                var options = new List<ChoiceOption>
                {
                    new ChoiceOption
                    {
                        label = "새 제안 하기",
                        cost = $"레벨 {rules.proposerMinLevel}부터",
                        detail = session.Me.level >= rules.proposerMinLevel ? "분야와 금액을 골라 제안해요. 운영팀이 검토한 뒤에 투표가 열려요."
                            : "레벨이 조금 더 오르면 제안할 수 있어요.",
                        enabled = session.Me.level >= rules.proposerMinLevel,
                        onChosen = () => PickCategory(session, rules, reopen, reopen),
                    },
                };
                for (int i = 0; i < data.proposals.Count; i++)
                {
                    BudgetProposalDto proposal = data.proposals[i];
                    string name = string.IsNullOrEmpty(proposal.statusName) ? BudgetStatuses.DisplayName(proposal.status) : proposal.statusName;
                    options.Add(new ChoiceOption
                    {
                        label = BenefitUi.Clip(proposal.title, 22),
                        cost = name,
                        detail = $"{proposal.categoryName} · {proposal.requestedGc:N0} GC · 찬성 {proposal.yesCount} 반대 {proposal.noCount}",
                        onChosen = () => ShowProposal(session, proposal, reopen),
                    });
                }
                string body = $"제안 → 검토 → 주민 투표({rules.voteDays}일) → 결과 확정 → 집행 → 공개\n" +
                              "한 사람이 한 표예요. 가진 GC나 낸 세금이 많아도 표는 늘지 않아요.";
                PagedMenu.Open("참여예산", body, options, onBack, MenuTag);
            });
        }

        private static void ShowProposal(GameSession session, BudgetProposalDto proposal, Action onBack)
        {
            if (!Alive(session)) return;
            string state = string.IsNullOrEmpty(proposal.statusName) ? BudgetStatuses.DisplayName(proposal.status) : proposal.statusName;
            string target = string.IsNullOrEmpty(proposal.targetName) ? "" : $" · {proposal.targetName}";
            string until = proposal.status == BudgetStatuses.Voting ? $"   {ShortTime(proposal.voteEndsAt)}까지" : "";
            string mine = proposal.myVote == "yes" ? "\n나는 찬성했어요." : proposal.myVote == "no" ? "\n나는 반대했어요." : "";
            string note = proposal.mine && !string.IsNullOrEmpty(proposal.reviewNote) ? "\n검토 의견: " + BenefitUi.Clip(proposal.reviewNote) : "";
            string body = $"{proposal.categoryName}{target} · {proposal.requestedGc:N0} GC · {proposal.proposer}\n" +
                          $"{BenefitUi.Clip(proposal.description, 60)}\n" +
                          $"{state}{until}   찬성 {proposal.yesCount} · 반대 {proposal.noCount} (최소 {proposal.quorum}표){mine}{note}";
            string blocked = proposal.canVote ? null
                : string.IsNullOrEmpty(proposal.voteBlockMessage) ? "지금은 투표할 수 없어요." : proposal.voteBlockMessage;
            ChoiceMenu.Instance.Open(BenefitUi.Clip(proposal.title, 26), body,
                new ChoiceOption
                {
                    label = "찬성",
                    detail = blocked ?? "한 번 던진 표는 바꿀 수 없어요.",
                    enabled = proposal.canVote,
                    onChosen = () => Vote(session, proposal.id, "yes", onBack),
                },
                new ChoiceOption
                {
                    label = "반대",
                    detail = blocked ?? "한 번 던진 표는 바꿀 수 없어요.",
                    enabled = proposal.canVote,
                    onChosen = () => Vote(session, proposal.id, "no", onBack),
                });
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void Vote(GameSession session, string proposalId, string choice, Action onDone)
        {
            session.Backend.VoteBudget(proposalId, choice, BenefitUi.NewRequestId(), result =>
            {
                if (!Alive(session)) return;
                if (result.ok) GameEvents.ShowToast(choice == "yes" ? "찬성에 투표했어요." : "반대에 투표했어요.");
                else BenefitUi.Failed(result, "투표");
                onDone?.Invoke();
            });
        }

        private static void PickCategory(GameSession session, BudgetOptionsDto rules, Action onBack, Action home)
        {
            if (!Alive(session)) return;
            Action reopen = () => PickCategory(session, rules, onBack, home);
            var options = new List<ChoiceOption>();
            for (int i = 0; i < rules.categories.Count; i++)
            {
                BudgetCategoryDto category = rules.categories[i];
                List<BudgetTargetDto> targets = category.effectType == BudgetEffects.StructureFund ? rules.structures
                    : category.effectType == BudgetEffects.FacilityRepair ? rules.facilities : null;
                bool empty = targets != null && targets.Count == 0;
                options.Add(new ChoiceOption
                {
                    label = category.name,
                    detail = empty ? "지금 고를 수 있는 대상이 없어요."
                        : category.effectType == BudgetEffects.StructureFund ? "도시 공용 방어시설의 건설 기금에 보태요."
                        : category.effectType == BudgetEffects.FacilityRepair ? "부서진 도시 시설을 고쳐요."
                        : "집행되면 도시의 집행 내역에 공개돼요.",
                    enabled = !empty,
                    onChosen = () =>
                    {
                        if (targets == null) PickAmount(session, rules, category, null, reopen, home);
                        else PickTarget(session, rules, category, targets, reopen, home);
                    },
                });
            }
            PagedMenu.Open("제안할 분야", "어디에 예산을 쓰자고 제안할까요?", options, onBack, MenuTag);
        }

        private static void PickTarget(GameSession session, BudgetOptionsDto rules, BudgetCategoryDto category, List<BudgetTargetDto> targets,
            Action onBack, Action home)
        {
            if (!Alive(session)) return;
            Action reopen = () => PickTarget(session, rules, category, targets, onBack, home);
            var options = new List<ChoiceOption>();
            for (int i = 0; i < targets.Count; i++)
            {
                BudgetTargetDto target = targets[i];
                options.Add(new ChoiceOption
                {
                    label = target.name,
                    detail = category.name + "의 대상으로 골라요.",
                    onChosen = () => PickAmount(session, rules, category, target, reopen, home),
                });
            }
            PagedMenu.Open("대상 시설", category.name, options, onBack, MenuTag);
        }

        private static void PickAmount(GameSession session, BudgetOptionsDto rules, BudgetCategoryDto category, BudgetTargetDto target, Action onBack,
            Action home)
        {
            if (!Alive(session)) return;
            var options = new List<ChoiceOption>();
            for (int i = 0; i < RequestSteps.Length && options.Count < ChoiceMenu.MaxOptions; i++)
            {
                int amount = RequestSteps[i];
                if (amount > rules.maxRequestGc) break;
                options.Add(new ChoiceOption
                {
                    label = $"{amount:N0} GC",
                    detail = "도시 재정에서 이만큼 쓰자고 제안해요. 내 GC는 들지 않아요.",
                    onChosen = () => Propose(session, category, target, amount, home),
                });
            }
            string what = target != null ? $"{category.name} · {target.name}" : category.name;
            ChoiceMenu.Instance.Open("제안할 금액", $"{what}\n한 번에 {rules.maxRequestGc:N0} GC까지 제안할 수 있어요.", options.ToArray());
            ChoiceMenu.Instance.Tag = MenuTag;
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void Propose(GameSession session, BudgetCategoryDto category, BudgetTargetDto target, int amount, Action onDone)
        {
            // 게임 화면에는 글을 적는 칸이 없다. 제목과 설명은 고른 분야·대상·금액으로 만든다.
            string title = target != null ? $"{target.name}에 예산을 써요" : $"{category.name}에 예산을 써요";
            string description = target != null
                ? $"{category.name} 분야의 {target.name}에 도시 예산 {amount:N0} GC를 쓰자는 제안이에요."
                : $"{category.name} 분야에 도시 예산 {amount:N0} GC를 쓰자는 제안이에요.";
            session.Backend.ProposeBudget(category.category, target != null ? target.code : "", title, description, amount,
                BenefitUi.NewRequestId(), result =>
                {
                    if (!Alive(session)) return;
                    if (result.ok) GameEvents.ShowToast("제안을 냈어요. 운영팀이 검토한 뒤에 주민 투표가 열려요.");
                    else if (result.errorCode == ApiErrors.Locked) GameEvents.ShowToast(result.message ?? "아직 제안할 수 없어요.");
                    else BenefitUi.Failed(result, "예산 제안");
                    // 낸 제안이 보이는 목록으로 돌아간다.
                    onDone?.Invoke();
                });
        }
    }
}
