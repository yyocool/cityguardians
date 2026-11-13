using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    // 현실 혜택의 게임 화면 (docs/CITY_PASS_SYSTEM.md 4장, docs/SCHOLARSHIP_SYSTEM.md 4장).
    //
    // 안내센터의 NPC 와 휴대전화가 같은 화면을 연다. 보여 주는 값은 모두 서버가 준 것이고 (홈페이지와 같은 자료),
    // 여기서는 요청을 보내고 받은 것을 보여 주기만 한다.
    //
    // 이 파일에는 GC 를 쓰거나 받는 곳이 없다. 쿠폰은 값을 치르지 않고 받고, 쿠폰을 GC 로 바꾸는 메뉴도,
    // 장학금을 신청하거나 받는 메뉴도 없다. 운영 승인이 끝나지 않은 기능은 "준비 중"으로만 보인다.

    /// <summary>혜택 화면들이 함께 쓰는 것.</summary>
    internal static class BenefitUi
    {
        /// <summary>안내판 한 줄에 들어가는 글자 수 (가장 작은 글자 크기 기준).</summary>
        public const int LineMax = 38;
        /// <summary>안내판 한 장에 들어가는 줄 수.</summary>
        public const int BoardLines = 10;

        public static string NewRequestId() => Guid.NewGuid().ToString("N");

        /// <summary>줄이 한 화면보다 많으면 쪽으로 나눈다. 마지막 줄이 "다음 쪽"이 되고, 끝에서 처음으로 돌아간다.</summary>
        public static ChoiceOption[] Paged(List<ChoiceOption> rows, int page, Action<int> open)
        {
            int total = rows.Count;
            if (total <= ChoiceMenu.MaxOptions) return rows.ToArray();

            int perPage = ChoiceMenu.MaxOptions - 1;
            int pages = Mathf.CeilToInt(total / (float)perPage);
            page = ((page % pages) + pages) % pages;
            var shown = new List<ChoiceOption>();
            for (int i = page * perPage; i < total && shown.Count < perPage; i++) shown.Add(rows[i]);
            int next = page + 1;
            shown.Add(new ChoiceOption
            {
                label = "다음 쪽",
                cost = $"{page + 1} / {pages}",
                detail = "나머지를 봐요.",
                onChosen = () => open(next),
            });
            return shown.ToArray();
        }

        public static string Clip(string text, int max = LineMax)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }

        /// <summary>글만 있는 안내판. 줄이 많으면 넘치는 만큼은 "홈페이지에서 볼 수 있어요"로 줄인다.</summary>
        public static void Board(string title, List<string> lines, Action onDone)
        {
            if (lines.Count > BoardLines)
            {
                lines = lines.GetRange(0, BoardLines - 1);
                lines.Add("… 나머지는 홈페이지에서 볼 수 있어요.");
            }
            DialogueRunner.Instance.ShowPanel(title, string.Join("\n", lines), false, onDone);
        }

        /// <summary>요청이 실패했을 때의 알림. 준비 중인 기능은 그렇게 알린다.</summary>
        public static void Failed<T>(ApiResult<T> result, string what)
        {
            Debug.LogWarning($"{what} 실패: {result.errorCode} {result.message}");
            string text;
            switch (result.errorCode)
            {
                case ApiErrors.FeatureDisabled:
                    text = string.IsNullOrEmpty(result.message) ? "아직 준비 중이에요." : result.message;
                    break;
                case ApiErrors.RateLimited:
                case ApiErrors.LimitReached:
                case ApiErrors.Forbidden:
                case ApiErrors.ValidationFailed:
                case ApiErrors.Conflict:
                case ApiErrors.NotFound:
                    text = string.IsNullOrEmpty(result.message) ? "지금은 할 수 없어요." : result.message;
                    break;
                default:
                    text = "잠시 뒤에 다시 시도해 주세요.";
                    break;
            }
            GameEvents.ShowToast(text);
        }

        public static string RegionName(GameSession session, string regionCode) =>
            session.Backend.Regions.ShortName(regionCode) ?? "전국";

        /// <summary>지점들을 한 줄로: "해운대점 (부산 해운대구) 외 1곳".</summary>
        public static string BranchSummary(GameSession session, List<PassBranchDto> branches)
        {
            if (branches == null || branches.Count == 0) return "쓸 수 있는 지점이 없어요.";
            string first = $"{branches[0].name} ({RegionName(session, branches[0].regionCode)})";
            return branches.Count == 1 ? first : $"{first} 외 {branches.Count - 1}곳";
        }

        /// <summary>지점 안내판.</summary>
        public static void ShowBranches(GameSession session, string title, List<PassBranchDto> branches, Action onDone)
        {
            var lines = new List<string>();
            for (int i = 0; branches != null && i < branches.Count; i++)
            {
                PassBranchDto branch = branches[i];
                lines.Add(Clip($"{branch.name}  ·  {branch.regionName}"));
                if (!string.IsNullOrEmpty(branch.address)) lines.Add(Clip(branch.address));
                if (!string.IsNullOrEmpty(branch.phone)) lines.Add(Clip("전화 " + branch.phone));
            }
            if (lines.Count == 0) lines.Add("쓸 수 있는 지점이 없어요.");
            Board(title, lines, onDone);
        }

        /// <summary>
        /// 지역 고르기: 시·도를 고르고, 그 안의 시·군·구를 고른다. 실제로 사는 곳과 달라도 된다.
        /// </summary>
        public static void PickRegion(GameSession session, string title, Action<string> chosen, Action onBack, int page = 0)
        {
            if (GameSession.Current != session) return;
            RegionCatalog catalog = session.Backend.Regions;
            var rows = new List<ChoiceOption>();
            for (int i = 0; i < catalog.Sidos.Count; i++)
            {
                SidoDef sido = catalog.Sidos[i];
                rows.Add(new ChoiceOption
                {
                    label = sido.name,
                    cost = $"{catalog.CountIn(sido.code)}곳",
                    detail = "이 시·도의 지역을 봐요.",
                    onChosen = () => PickRegionIn(session, title, sido, chosen, () => PickRegion(session, title, chosen, onBack, page), 0),
                });
            }
            ChoiceMenu.Instance.Open(title, "실제로 사는 곳과 달라도 괜찮아요.",
                Paged(rows, page, next => PickRegion(session, title, chosen, onBack, next)));
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void PickRegionIn(GameSession session, string title, SidoDef sido, Action<string> chosen, Action onBack, int page)
        {
            if (GameSession.Current != session) return;
            RegionCatalog catalog = session.Backend.Regions;
            var rows = new List<ChoiceOption>();
            for (int i = 0; i < catalog.Regions.Count; i++)
            {
                RegionDef region = catalog.Regions[i];
                if (region.sidoCode != sido.code) continue;
                rows.Add(new ChoiceOption
                {
                    label = region.name,
                    detail = catalog.FullName(region.code),
                    onChosen = () => chosen(region.code),
                });
            }
            ChoiceMenu.Instance.Open(title, sido.name,
                Paged(rows, page, next => PickRegionIn(session, title, sido, chosen, onBack, next)));
            ChoiceMenu.Instance.OnCancelled = onBack;
        }
    }

    /// <summary>
    /// CITY PASS: 지역 제휴 멤버십. 내 CITY PASS, 주변 제휴 상점, 할인 혜택, 내 쿠폰, 이용 내역.
    /// </summary>
    public static class CityPassActions
    {
        public const string Title = "CITY PASS 안내센터";
        public const string MyPassTitle = "내 CITY PASS";
        public const string JoinTitle = "CITY PASS 가입";
        public const string MerchantsTitle = "주변 제휴 상점";
        public const string OffersTitle = "할인 혜택";
        public const string CouponsTitle = "내 쿠폰";
        public const string HistoryTitle = "이용 내역";
        public const string SoonText = "준비 중이에요.";

        /// <summary>안내센터를 연다.</summary>
        /// <param name="guide">안내원. 한마디 하게 하는 데 쓴다. 휴대전화에서 열면 없다.</param>
        /// <param name="onBack">E로 닫았을 때 돌아갈 곳 (휴대전화 첫 화면).</param>
        public static void Open(NpcAgent guide = null, Action onBack = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetCityPass(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "CITY PASS 열기");
                    return;
                }
                if (guide != null && !result.data.membershipEnabled) guide.Say("아직 준비 중이에요. 열리면 알려 드릴게요!", 3.5f);
                ShowMain(session, result.data, onBack);
            });
        }

        /// <summary>지역 혜택 안내원이 여는 화면: 혜택 목록부터 보여 주고, 뒤로 가면 안내센터의 첫 화면이다.</summary>
        public static void OpenOffers(NpcAgent guide = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetCityPass(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "CITY PASS 열기");
                    return;
                }
                CityPassProfileDto pass = result.data;
                if (!pass.couponsEnabled)
                {
                    // 준비되지 않은 혜택을 있는 것처럼 보여 주지 않는다.
                    if (guide != null) guide.Say("제휴 혜택은 아직 준비 중이에요.", 3.5f);
                    ShowMain(session, pass, null);
                    return;
                }
                ShowOffers(session, pass, DefaultRegion(session, pass), 0, () => Reopen(session, null));
            });
        }

        private static void Reopen(GameSession session, Action onBack)
        {
            if (GameSession.Current == session) Open(null, onBack);
        }

        private static void ShowMain(GameSession session, CityPassProfileDto pass, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action reopen = () => ShowMain(session, pass, onBack);
            Action refresh = () => Reopen(session, onBack);

            string body;
            if (!pass.membershipEnabled) body = string.IsNullOrEmpty(pass.notice) ? "CITY PASS 는 준비 중이에요." : pass.notice;
            else if (pass.isMember) body = $"{pass.nickname} 님  ·  회원번호 {pass.memberNo}";
            else body = "우리 동네 제휴 상점의 혜택을 모아 보는 멤버십이에요. 가입은 무료예요.";

            var mine = new ChoiceOption { label = MyPassTitle };
            if (pass.isMember)
            {
                mine.cost = pass.interestRegion != null ? BenefitUi.RegionName(session, pass.interestRegion.code) : "";
                mine.detail = "회원번호와 관심 지역을 봐요.";
                mine.onChosen = () => ShowMyPass(session, pass, onBack);
            }
            else if (pass.membershipEnabled)
            {
                mine.cost = "무료 가입";
                mine.detail = "가입하면 제휴 상점의 쿠폰을 받을 수 있어요.";
                mine.onChosen = () => ShowJoin(session, pass, onBack);
            }
            else
            {
                mine.cost = "준비 중";
                mine.detail = "운영 준비가 끝나면 가입할 수 있어요.";
                mine.enabled = false;
            }

            string closed = "제휴 혜택은 준비 중이에요.";
            string defaultRegion = DefaultRegion(session, pass);
            var merchants = new ChoiceOption
            {
                label = MerchantsTitle,
                cost = pass.couponsEnabled ? "" : "준비 중",
                detail = pass.couponsEnabled ? "운영팀과 제휴한 상점만 나와요." : closed,
                enabled = pass.couponsEnabled,
                onChosen = () => ShowMerchants(session, defaultRegion, 0, reopen),
            };
            var offers = new ChoiceOption
            {
                label = OffersTitle,
                cost = pass.couponsEnabled ? "" : "준비 중",
                detail = pass.couponsEnabled ? "지역별 혜택을 보고 쿠폰을 받아요. 쿠폰은 무료예요." : closed,
                enabled = pass.couponsEnabled,
                onChosen = () => ShowOffers(session, pass, defaultRegion, 0, refresh),
            };
            var coupons = new ChoiceOption
            {
                label = CouponsTitle,
                cost = pass.isMember ? $"사용 가능 {pass.coupons.available}장" : "",
                detail = pass.isMember ? "받은 쿠폰을 보고, 상점에 보여 줘요." : "가입한 뒤에 쓸 수 있어요.",
                enabled = pass.isMember,
                onChosen = () => ShowCoupons(session, pass, 0, refresh),
            };
            var history = new ChoiceOption
            {
                label = HistoryTitle,
                cost = pass.isMember && pass.coupons.used > 0 ? $"{pass.coupons.used}건" : "",
                detail = pass.isMember ? "상점에서 쓴 쿠폰의 기록이에요." : "가입한 뒤에 쓸 수 있어요.",
                enabled = pass.isMember,
                onChosen = () => ShowHistory(session, reopen),
            };

            ChoiceMenu.Instance.Open(Title, body, mine, merchants, offers, coupons, history);
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        /// <summary>처음 보여 줄 지역: 관심 지역, 없으면 전입신고한 지역, 그것도 없으면 전국.</summary>
        private static string DefaultRegion(GameSession session, CityPassProfileDto pass)
        {
            if (pass.interestRegion != null && !string.IsNullOrEmpty(pass.interestRegion.code)) return pass.interestRegion.code;
            return session.IsResident ? session.Me.regionCode : null;
        }

        // ── 내 CITY PASS ────────────────────────────────────────

        private static void ShowMyPass(GameSession session, CityPassProfileDto pass, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action back = () => Reopen(session, onBack);
            string region = pass.interestRegion != null ? BenefitUi.RegionName(session, pass.interestRegion.code) : "아직 고르지 않음";
            // 화면에 나오는 것은 게임 닉네임과 회원번호뿐이다. 실제 이름이나 연락처는 받지 않는다.
            string body = $"{pass.nickname} 님  ·  회원번호 {pass.memberNo}\n"
                + $"관심 지역 {region}  ·  가입한 날 {BenefitRules.DateText(pass.joinedAt)}";

            ChoiceMenu.Instance.Open(MyPassTitle, body,
                new ChoiceOption
                {
                    label = "관심 지역 바꾸기",
                    cost = region,
                    detail = "혜택을 먼저 보고 싶은 지역이에요. 사는 곳과 달라도 괜찮아요.",
                    onChosen = () => BenefitUi.PickRegion(session, "관심 지역 고르기",
                        code => SetRegion(session, code, back), () => ShowMyPass(session, pass, onBack)),
                },
                new ChoiceOption
                {
                    label = "쿠폰 현황",
                    cost = $"사용 가능 {pass.coupons.available}장",
                    detail = $"사용함 {pass.coupons.used}장  ·  기간 만료 {pass.coupons.expired}장",
                    onChosen = () => ShowCoupons(session, pass, 0, back),
                });
            ChoiceMenu.Instance.OnCancelled = back;
        }

        private static void SetRegion(GameSession session, string regionCode, Action onDone)
        {
            session.Backend.SetCityPassRegion(regionCode, result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok) GameEvents.ShowToast($"관심 지역을 {BenefitUi.RegionName(session, regionCode)}(으)로 바꿨어요.");
                else BenefitUi.Failed(result, "관심 지역 바꾸기");
                onDone?.Invoke();
            });
        }

        private static void ShowJoin(GameSession session, CityPassProfileDto pass, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action back = () => Reopen(session, onBack);
            Action again = () => ShowJoin(session, pass, onBack);
            string region = session.IsResident ? BenefitUi.RegionName(session, session.Me.regionCode) : "나중에 고르기";

            ChoiceMenu.Instance.Open(JoinTitle,
                "가입은 무료예요. GC 도 들지 않아요.\n게임 닉네임과 회원번호만 써요. 실제 이름과 주소는 받지 않아요.",
                new ChoiceOption
                {
                    label = "약관에 동의하고 가입하기",
                    cost = "무료",
                    detail = $"관심 지역: {region}  (가입한 뒤에 바꿀 수 있어요)",
                    onChosen = () => Join(session, back),
                },
                new ChoiceOption
                {
                    label = "약관 살펴보기",
                    cost = pass.termsVersion ?? "",
                    detail = "중요한 내용을 먼저 읽어 봐요.",
                    onChosen = () => BenefitUi.Board("CITY PASS 이용 약관 (요약)", TermsSummary(), again),
                },
                new ChoiceOption
                {
                    label = "나중에 할게요",
                    detail = "가입하지 않아도 게임을 하는 데에는 아무 차이가 없어요.",
                    onChosen = back,
                });
            ChoiceMenu.Instance.OnCancelled = back;
        }

        /// <summary>가입 화면에서 보여 주는 약관의 요약. 전체 약관은 홈페이지에 있다.</summary>
        public static List<string> TermsSummary() => new List<string>
        {
            "가입과 쿠폰은 모두 무료예요.",
            "쿠폰은 제휴 상점에서만 쓸 수 있어요.",
            "쿠폰은 현금이나 GC, 아이템으로 바꿀 수 없어요.",
            "GC 나 전투 성적으로 쿠폰을 받을 수도 없어요.",
            "받는 정보는 게임 닉네임과 관심 지역뿐이에요.",
            "관심 지역은 실제로 사는 곳과 달라도 돼요.",
            "",
            "전체 약관은 홈페이지에서 볼 수 있어요.",
        };

        private static void Join(GameSession session, Action onDone)
        {
            // 여기까지 온 것은 "약관에 동의하고 가입하기"를 골랐을 때뿐이다.
            session.Backend.JoinCityPass(true, null, result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    Sfx.Play(Sfx.Id.Interact);
                    GameEvents.ShowToast($"CITY PASS 에 가입했어요!  회원번호 {result.data.memberNo}");
                }
                else BenefitUi.Failed(result, "CITY PASS 가입");
                onDone?.Invoke();
            });
        }

        // ── 주변 제휴 상점 ──────────────────────────────────────

        private static void ShowMerchants(GameSession session, string regionCode, int page, Action onBack)
        {
            if (GameSession.Current != session) return;
            session.Backend.GetCityPassMerchants(regionCode, null, result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "제휴 상점 보기");
                    onBack?.Invoke();
                    return;
                }
                Action<int> open = next => ShowMerchants(session, regionCode, next, onBack);
                var rows = new List<ChoiceOption> { RegionRow(session, regionCode, code => ShowMerchants(session, code, 0, onBack), () => open(page)) };
                List<PassMerchantDto> list = result.data.merchants;
                for (int i = 0; i < list.Count; i++)
                {
                    PassMerchantDto merchant = list[i];
                    rows.Add(new ChoiceOption
                    {
                        label = BenefitUi.Clip(merchant.name, 18),
                        cost = merchant.categoryName,
                        detail = BenefitUi.Clip(BenefitUi.BranchSummary(session, merchant.branches)),
                        onChosen = () => BenefitUi.ShowBranches(session, merchant.name, merchant.branches, () => open(page)),
                    });
                }
                string body = !result.data.couponsEnabled ? "제휴 상점은 준비 중이에요."
                    : list.Count == 0 ? "이 지역에는 아직 제휴 상점이 없어요. 다른 지역도 볼 수 있어요."
                    : "운영팀과 제휴 계약을 맺은 상점이에요.";
                ChoiceMenu.Instance.Open(MerchantsTitle, body, BenefitUi.Paged(rows, page, open));
                ChoiceMenu.Instance.OnCancelled = onBack;
            });
        }

        /// <summary>목록의 첫 줄: 지금 보는 지역과 바꾸기.</summary>
        private static ChoiceOption RegionRow(GameSession session, string regionCode, Action<string> show, Action onBack)
        {
            bool all = string.IsNullOrEmpty(regionCode);
            return new ChoiceOption
            {
                label = all ? "지역: 전국" : "지역: " + BenefitUi.RegionName(session, regionCode),
                cost = "바꾸기",
                detail = "다른 지역의 것도 볼 수 있어요.",
                onChosen = () =>
                {
                    var options = new List<ChoiceOption>();
                    if (!all)
                    {
                        options.Add(new ChoiceOption { label = "전국", detail = "모든 지역을 한꺼번에 봐요.", onChosen = () => show(null) });
                    }
                    if (session.IsResident && session.Me.regionCode != regionCode)
                    {
                        options.Add(new ChoiceOption
                        {
                            label = "우리 도시",
                            cost = BenefitUi.RegionName(session, session.Me.regionCode),
                            detail = "전입신고한 도시예요.",
                            onChosen = () => show(session.Me.regionCode),
                        });
                    }
                    options.Add(new ChoiceOption
                    {
                        label = "다른 지역 고르기",
                        detail = "시·도와 시·군·구를 골라요.",
                        onChosen = () => BenefitUi.PickRegion(session, "지역 고르기", show, onBack),
                    });
                    ChoiceMenu.Instance.Open("지역 고르기", "어느 지역을 볼까요?", options.ToArray());
                    ChoiceMenu.Instance.OnCancelled = onBack;
                },
            };
        }

        // ── 할인 혜택 ───────────────────────────────────────────

        private static void ShowOffers(GameSession session, CityPassProfileDto pass, string regionCode, int page, Action onBack)
        {
            if (GameSession.Current != session) return;
            session.Backend.GetCityPassOffers(regionCode, null, result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "혜택 보기");
                    onBack?.Invoke();
                    return;
                }
                Action<int> open = next => ShowOffers(session, pass, regionCode, next, onBack);
                var rows = new List<ChoiceOption> { RegionRow(session, regionCode, code => ShowOffers(session, pass, code, 0, onBack), () => open(page)) };
                List<PassOfferDto> list = result.data.offers;
                for (int i = 0; i < list.Count; i++)
                {
                    PassOfferDto offer = list[i];
                    string state = offer.claimable ? "쿠폰을 받을 수 있어요." : BenefitRules.ClaimBlockText(offer.claimBlock);
                    rows.Add(new ChoiceOption
                    {
                        label = BenefitUi.Clip(offer.title, 18),
                        cost = offer.discountText,
                        detail = BenefitUi.Clip($"{offer.merchant.name}  ·  {state}"),
                        onChosen = () => ShowOffer(session, offer, () => open(page)),
                    });
                }
                string body = !result.data.couponsEnabled ? (string.IsNullOrEmpty(result.data.notice) ? "제휴 혜택은 준비 중이에요." : result.data.notice)
                    : list.Count == 0 ? "이 지역에는 지금 받을 수 있는 혜택이 없어요. 다른 지역도 볼 수 있어요."
                    : pass.isMember ? "쿠폰은 무료예요. GC 는 들지 않아요."
                    : "쿠폰을 받으려면 먼저 CITY PASS 에 가입해 주세요. 가입은 무료예요.";
                ChoiceMenu.Instance.Open(OffersTitle, body, BenefitUi.Paged(rows, page, open));
                ChoiceMenu.Instance.OnCancelled = onBack;
            });
        }

        private static void ShowOffer(GameSession session, PassOfferDto offer, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action again = () => ShowOffer(session, offer, onBack);
            string conditions = string.IsNullOrEmpty(offer.conditions) ? "이용 조건 없음" : offer.conditions;
            string body = BenefitUi.Clip($"{offer.merchant.name} ({offer.merchant.categoryName})  ·  {offer.discountText}", 44) + "\n"
                + BenefitUi.Clip($"{conditions}  ·  {BenefitRules.DateText(offer.endsAt)}까지", 44);

            string left = offer.remaining >= 0 ? $"남은 수 {offer.remaining}장" : "";
            ChoiceMenu.Instance.Open(BenefitUi.Clip(offer.title, 22), body,
                new ChoiceOption
                {
                    label = "쿠폰 받기",
                    cost = offer.claimable ? "무료" : "",
                    detail = offer.claimable
                        ? $"받은 날부터 {offer.couponValidDays}일 동안 쓸 수 있어요.  {left}".TrimEnd()
                        : BenefitRules.ClaimBlockText(offer.claimBlock),
                    enabled = offer.claimable,
                    onChosen = () => Claim(session, offer, onBack),
                },
                new ChoiceOption
                {
                    label = "쓸 수 있는 지점",
                    cost = $"{offer.branches.Count}곳",
                    detail = BenefitUi.Clip(BenefitUi.BranchSummary(session, offer.branches)),
                    onChosen = () => BenefitUi.ShowBranches(session, offer.merchant.name, offer.branches, again),
                });
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void Claim(GameSession session, PassOfferDto offer, Action onDone)
        {
            // 쿠폰은 값을 치르지 않고 받는다. 지갑은 그대로다.
            session.Backend.ClaimCityPassCoupon(offer.id, BenefitUi.NewRequestId(), result =>
            {
                if (GameSession.Current != session) return;
                if (result.ok)
                {
                    Sfx.Play(Sfx.Id.Interact);
                    GameEvents.ShowToast("쿠폰을 받았어요!  '내 쿠폰'에서 볼 수 있어요.");
                }
                else BenefitUi.Failed(result, "쿠폰 받기");
                onDone?.Invoke();
            });
        }

        // ── 내 쿠폰 ─────────────────────────────────────────────

        private static void ShowCoupons(GameSession session, CityPassProfileDto pass, int page, Action onBack)
        {
            if (GameSession.Current != session) return;
            session.Backend.GetCityPassCoupons(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "쿠폰 보기");
                    onBack?.Invoke();
                    return;
                }
                List<PassCouponDto> list = result.data.coupons;
                if (list.Count == 0)
                {
                    GameEvents.ShowToast("받은 쿠폰이 없어요. '할인 혜택'에서 받을 수 있어요.");
                    onBack?.Invoke();
                    return;
                }
                Action<int> open = next => ShowCoupons(session, pass, next, onBack);
                var rows = new List<ChoiceOption>();
                for (int i = 0; i < list.Count; i++)
                {
                    PassCouponDto coupon = list[i];
                    rows.Add(new ChoiceOption
                    {
                        label = BenefitUi.Clip(coupon.offer != null ? coupon.offer.title : "쿠폰", 18),
                        cost = CouponState(coupon),
                        detail = BenefitUi.Clip($"{MerchantName(coupon)}  ·  {(coupon.offer != null ? coupon.offer.discountText : "")}"),
                        onChosen = () => ShowCoupon(session, pass, coupon, () => open(page)),
                    });
                }
                string body = result.data.couponsEnabled ? "쿠폰을 고르면 상점에 보여 줄 수 있어요."
                    : string.IsNullOrEmpty(result.data.notice) ? "쿠폰은 준비 중이에요. 지금은 쓸 수 없어요." : result.data.notice;
                ChoiceMenu.Instance.Open(CouponsTitle, body, BenefitUi.Paged(rows, page, open));
                ChoiceMenu.Instance.OnCancelled = onBack;
            });
        }

        private static string MerchantName(PassCouponDto coupon) => coupon.merchant != null ? coupon.merchant.name : "";

        /// <summary>목록 오른쪽에 보이는 쿠폰의 상태: 쓸 수 있으면 남은 날, 아니면 상태.</summary>
        private static string CouponState(PassCouponDto coupon)
        {
            if (coupon.status != CouponStatus.Issued || !BenefitRules.TryParseUtc(coupon.expiresAt, out DateTime _))
                return BenefitRules.StatusText(coupon.status);
            return BenefitRules.DateText(coupon.expiresAt) + "까지";
        }

        private static void ShowCoupon(GameSession session, CityPassProfileDto pass, PassCouponDto coupon, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action again = () => ShowCoupon(session, pass, coupon, onBack);
            string title = coupon.offer != null ? coupon.offer.title : "쿠폰";
            string conditions = coupon.offer == null || string.IsNullOrEmpty(coupon.offer.conditions) ? "이용 조건 없음" : coupon.offer.conditions;
            string discount = coupon.offer != null ? coupon.offer.discountText : "";
            string body = BenefitUi.Clip($"{MerchantName(coupon)}  ·  {discount}  ·  {BenefitRules.StatusText(coupon.status)}", 44) + "\n"
                + BenefitUi.Clip($"{conditions}  ·  유효기간 {BenefitRules.DateText(coupon.expiresAt)}까지", 44);

            string why = coupon.status == CouponStatus.Redeemed ? $"{BenefitRules.DateTimeText(coupon.redeemedAt)}에 썼어요."
                : coupon.status == CouponStatus.Expired ? "유효기간이 지났어요."
                : "지금은 이 상점에서 쓸 수 없어요.";
            ChoiceMenu.Instance.Open(BenefitUi.Clip(title, 22), body,
                new ChoiceOption
                {
                    label = "상점에 보여 주기",
                    cost = coupon.usable ? "QR · 코드" : "",
                    detail = coupon.usable ? "5분 동안 쓸 수 있는 QR 과 코드를 띄워요. 직원에게 보여 주세요." : why,
                    enabled = coupon.usable,
                    onChosen = () => Present(session, pass, coupon, onBack),
                },
                new ChoiceOption
                {
                    label = "쓸 수 있는 지점",
                    cost = $"{coupon.branches.Count}곳",
                    detail = BenefitUi.Clip(BenefitUi.BranchSummary(session, coupon.branches)),
                    onChosen = () => BenefitUi.ShowBranches(session, MerchantName(coupon), coupon.branches, again),
                });
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        /// <summary>
        /// 상점에 보여 줄 일회용 코드를 받아 QR 과 함께 띄운다. 쿠폰이 쓰였는지는 상점이 처리한 뒤에 서버가 알려 준다:
        /// 화면을 닫으면 목록을 다시 받아 온다.
        /// </summary>
        private static void Present(GameSession session, CityPassProfileDto pass, PassCouponDto coupon, Action onDone)
        {
            session.Backend.PresentCityPassCoupon(coupon.id, result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "쿠폰 보여 주기");
                    onDone?.Invoke();
                    return;
                }
                PassPresentDto present = result.data;
                // QR 에는 일회용 코드만 넣는다. 서버가 보낸 글에 다른 것이 섞여 있으면 코드만 그린다.
                string qrText = BenefitRules.SafeQrText(present.qrPayload, present.code, pass.memberNo, pass.nickname);
                string title = coupon.offer != null ? coupon.offer.title : "쿠폰";
                string subtitle = $"{MerchantName(coupon)}  ·  {(coupon.offer != null ? coupon.offer.discountText : "")}";
                if (qrText == null || !CouponPresenter.Instance.Show(title, subtitle, present.code, qrText, present.expiresInSeconds, onDone))
                {
                    Debug.LogWarning("쿠폰 제시 코드를 화면에 낼 수 없다.");
                    GameEvents.ShowToast("코드를 띄우지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    onDone?.Invoke();
                }
            });
        }

        // ── 이용 내역 ───────────────────────────────────────────

        private static void ShowHistory(GameSession session, Action onDone)
        {
            session.Backend.GetCityPassHistory(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "이용 내역 보기");
                    onDone?.Invoke();
                    return;
                }
                BenefitUi.Board(HistoryTitle, HistoryLines(result.data), onDone);
            });
        }

        public static List<string> HistoryLines(PassHistoryDto history)
        {
            var lines = new List<string>();
            if (history.history.Count == 0)
            {
                lines.Add("아직 쓴 쿠폰이 없어요.");
                lines.Add("");
                lines.Add("쿠폰은 상점에서 확인한 뒤에 '사용함'으로 바뀌어요.");
                return lines;
            }
            for (int i = 0; i < history.history.Count; i++)
            {
                PassHistoryEntryDto entry = history.history[i];
                string place = string.IsNullOrEmpty(entry.branchName) ? entry.merchantName : $"{entry.merchantName} {entry.branchName}";
                lines.Add(BenefitUi.Clip($"{BenefitRules.DateText(entry.redeemedAt)}  {place}  ·  {entry.discountText}"));
            }
            return lines;
        }
    }

    /// <summary>
    /// GUARDIANS SCHOLARSHIP 안내: 프로그램 소개, 지원 자격, 모집 일정, 금융교육, 지원 방법, 심사 진행 상태.
    /// 게임 안에서는 안내만 한다. 신청은 홈페이지에서 하고, 심사와 지급은 운영팀이 한다.
    /// </summary>
    public static class ScholarshipActions
    {
        public const string Title = "장학센터";
        public const string IntroTitle = "장학 프로그램 소개";
        public const string EligibilityTitle = "지원 자격";
        public const string ScheduleTitle = "모집 일정";
        public const string EducationTitle = "금융교육";
        public const string HowToTitle = "지원 방법";
        public const string StatusTitle = "심사 진행 상태";
        /// <summary>심사에 쓰지 않는 것. 여러 화면에 같은 문장으로 나온다.</summary>
        public const string NotUsed = "게임 성적, 랭킹, GC, 결제 내역은 심사에 쓰이지 않아요.";

        public static void Open(NpcAgent guide = null, Action onBack = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetScholarships(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "장학센터 열기");
                    return;
                }
                if (guide != null && !result.data.applicationsEnabled) guide.Say("모집이 시작되면 여기에 공고를 붙일게요!", 3.5f);
                ShowMain(session, result.data, onBack);
            });
        }

        /// <summary>교육 프로그램 안내원이 여는 화면: 모집 일정부터 보여 주고, 닫으면 장학센터의 첫 화면이다.</summary>
        public static void OpenSchedule(NpcAgent guide = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetScholarships(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "모집 일정 보기");
                    return;
                }
                if (guide != null && !result.data.applicationsEnabled) guide.Say("모집이 시작되면 알려 드릴게요!", 3.5f);
                BenefitUi.Board(ScheduleTitle, ScheduleLines(result.data), () => ShowMain(session, result.data, null));
            });
        }

        private static void ShowMain(GameSession session, ScholarshipListDto list, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action reopen = () => ShowMain(session, list, onBack);
            bool on = list.applicationsEnabled;
            string body = on
                ? "GUARDIANS SCHOLARSHIP 을 안내해요. 신청은 홈페이지에서 해요."
                : string.IsNullOrEmpty(list.notice) ? "장학 프로그램은 준비 중이에요." : list.notice;
            int open = 0;
            for (int i = 0; i < list.programs.Count; i++)
                if (list.programs[i].phase == ScholarshipPhases.Open) open++;

            ChoiceMenu.Instance.Open(Title, body,
                new ChoiceOption
                {
                    label = IntroTitle,
                    detail = "어떤 장학 프로그램이 있는지 알려 줘요.",
                    onChosen = () => BenefitUi.Board(IntroTitle, IntroLines(), reopen),
                },
                new ChoiceOption
                {
                    label = EligibilityTitle,
                    cost = on ? "" : "준비 중",
                    detail = on && list.programs.Count > 0 ? "공고를 골라 지원 자격과 심사 기준을 봐요." : "공고가 나오면 볼 수 있어요.",
                    enabled = on && list.programs.Count > 0,
                    onChosen = () => ShowPrograms(session, list, 0, reopen),
                },
                new ChoiceOption
                {
                    label = ScheduleTitle,
                    cost = on ? (open > 0 ? $"모집 중 {open}건" : "") : "준비 중",
                    detail = "모집 기간과 결과를 알리는 날을 봐요.",
                    onChosen = () => BenefitUi.Board(ScheduleTitle, ScheduleLines(list), reopen),
                },
                new ChoiceOption
                {
                    label = EducationTitle,
                    detail = "돈을 모으고 불리는 법을 배우는 길을 알려 줘요.",
                    onChosen = () => BenefitUi.Board(EducationTitle, EducationLines(), reopen),
                },
                new ChoiceOption
                {
                    label = HowToTitle,
                    cost = "홈페이지",
                    detail = "신청은 게임 안이 아니라 홈페이지에서 해요.",
                    onChosen = () => ShowHowTo(session, list, reopen),
                },
                new ChoiceOption
                {
                    label = StatusTitle,
                    detail = "홈페이지에서 낸 신청이 어디까지 갔는지 봐요.",
                    onChosen = () => ShowStatus(session, reopen),
                });
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        public static List<string> IntroLines() => new List<string>
        {
            "배우고 싶은 학생을 돕는 장학 프로그램이에요.",
            "",
            "금융 역량 장학  ·  금융교육을 마치고 계획을 세운 학생",
            "창의력 장학  ·  창작과 탐구 활동을 하는 학생",
            "협력·리더십 장학  ·  함께 배우고 나누는 학생",
            "성장 지원 장학  ·  배움에 도움이 필요한 학생",
            "",
            NotUsed,
            "게임 안에서 장학금을 주거나 받지 않아요.",
        };

        public static List<string> EducationLines() => new List<string>
        {
            "도시은행에서 저축과 이자를 직접 해 볼 수 있어요.",
            "맡긴 돈에는 날마다 이자가 붙고,",
            "정기예금은 기다린 만큼 이자가 커져요.",
            "",
            "금융 역량 장학의 교육 과정은",
            "공고가 나오면 홈페이지에서 안내해요.",
            "",
            "게임 속 통장과 GC 는 연습용이에요.",
            "심사에도, 현실의 돈으로도 쓰이지 않아요.",
        };

        public static List<string> ScheduleLines(ScholarshipListDto list)
        {
            var lines = new List<string>();
            if (!list.applicationsEnabled)
            {
                lines.Add(string.IsNullOrEmpty(list.notice) ? "장학 프로그램은 준비 중이에요." : BenefitUi.Clip(list.notice, 44));
                lines.Add("");
                lines.Add("모집이 시작되면 여기와 홈페이지에 공고가 올라와요.");
                return lines;
            }
            if (list.programs.Count == 0)
            {
                lines.Add("지금은 올라온 공고가 없어요.");
                lines.Add("");
                lines.Add("모집이 시작되면 여기와 홈페이지에 공고가 올라와요.");
                return lines;
            }
            // 한 공고에 두 줄: 이름과 단계, 그리고 날짜.
            for (int i = 0; i < list.programs.Count && lines.Count + 2 <= BenefitUi.BoardLines - 1; i++)
            {
                ScholarshipProgramDto program = list.programs[i];
                lines.Add(BenefitUi.Clip($"[{BenefitRules.PhaseText(program.phase)}]  {program.title}"));
                lines.Add(BenefitUi.Clip($"{BenefitRules.DateText(program.applyStartsAt)} ~ {BenefitRules.DateText(program.applyEndsAt)}"
                    + (string.IsNullOrEmpty(program.resultOn) ? "" : $"  ·  발표 {program.resultOn}")));
            }
            return lines;
        }

        private static void ShowPrograms(GameSession session, ScholarshipListDto list, int page, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action<int> open = next => ShowPrograms(session, list, next, onBack);
            var rows = new List<ChoiceOption>();
            for (int i = 0; i < list.programs.Count; i++)
            {
                ScholarshipProgramDto program = list.programs[i];
                rows.Add(new ChoiceOption
                {
                    label = BenefitUi.Clip(program.title, 20),
                    cost = BenefitRules.PhaseText(program.phase),
                    detail = BenefitUi.Clip($"{program.categoryName}  ·  {program.supportTypeName}  ·  {BenefitRules.DateText(program.applyEndsAt)}까지"),
                    onChosen = () => ShowProgram(session, program.id, () => open(page)),
                });
            }
            ChoiceMenu.Instance.Open(EligibilityTitle, "공고를 골라 주세요.", BenefitUi.Paged(rows, page, open));
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        private static void ShowProgram(GameSession session, string programId, Action onDone)
        {
            session.Backend.GetScholarship(programId, result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "공고 보기");
                    onDone?.Invoke();
                    return;
                }
                BenefitUi.Board(BenefitUi.Clip(result.data.title, 20), ProgramLines(result.data), onDone);
            });
        }

        public static List<string> ProgramLines(ScholarshipProgramDto program)
        {
            var lines = new List<string>
            {
                BenefitUi.Clip($"{program.categoryName}  ·  {program.supportTypeName}  ·  {BenefitRules.PhaseText(program.phase)}"),
                BenefitUi.Clip($"모집 {BenefitRules.DateText(program.applyStartsAt)} ~ {BenefitRules.DateText(program.applyEndsAt)}"),
            };
            // 지원 자격과 심사 기준은 세 줄씩만 보여 준다. 나머지는 홈페이지에 있다.
            for (int i = 0; i < program.eligibility.Count && i < 3; i++)
                lines.Add(BenefitUi.Clip("지원 자격  ·  " + program.eligibility[i].description));
            for (int i = 0; i < program.criteria.Count && i < 3; i++)
                lines.Add(BenefitUi.Clip("심사 기준  ·  " + program.criteria[i].name));
            if (program.guardianConsentAge > 0) lines.Add($"만 {program.guardianConsentAge}세 미만은 보호자 동의가 필요해요.");
            lines.Add(NotUsed);
            return lines;
        }

        private static void ShowHowTo(GameSession session, ScholarshipListDto list, Action onBack)
        {
            if (GameSession.Current != session) return;
            Action again = () => ShowHowTo(session, list, onBack);
            ChoiceMenu.Instance.Open(HowToTitle,
                "신청은 홈페이지에서 해요. 게임 안에서는 신청도, 지급도 하지 않아요.",
                new ChoiceOption
                {
                    label = "신청 순서 보기",
                    detail = "로그인부터 결과 확인까지의 순서예요.",
                    onChosen = () => BenefitUi.Board(HowToTitle, HowToLines(list), again),
                },
                new ChoiceOption
                {
                    label = PhoneActions.WebCodeTitle + " 받기",
                    cost = "5분",
                    detail = "홈페이지에 로그인할 때 한 번 쓰는 코드예요.",
                    onChosen = () => PhoneActions.ShowWebCode(again),
                });
            ChoiceMenu.Instance.OnCancelled = onBack;
        }

        public static List<string> HowToLines(ScholarshipListDto list)
        {
            var lines = new List<string>
            {
                "1. 휴대전화(P)에서 홈페이지 로그인 코드를 받아요.",
                "2. 홈페이지의 SCHOLARSHIP 에서 공고를 골라요.",
                "3. 신청서와 학습 계획을 써서 내요.",
                "4. 미성년자는 보호자의 동의를 받아요.",
                "5. 심사가 끝나면 '심사 진행 상태'에서 결과를 봐요.",
                "",
                "주민등록번호는 받지 않아요.",
                NotUsed,
            };
            if (!string.IsNullOrEmpty(list.siteUrl)) lines.Add(BenefitUi.Clip(list.siteUrl, 44));
            return lines;
        }

        private static void ShowStatus(GameSession session, Action onDone)
        {
            session.Backend.GetMyScholarshipApplications(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "신청 상태 보기");
                    onDone?.Invoke();
                    return;
                }
                BenefitUi.Board(StatusTitle, StatusLines(result.data), onDone);
            });
        }

        public static List<string> StatusLines(ScholarshipApplicationsDto data)
        {
            var lines = new List<string>();
            if (data.applications.Count == 0)
            {
                lines.Add("홈페이지에서 낸 신청이 없어요.");
                lines.Add("");
                lines.Add("신청은 홈페이지의 SCHOLARSHIP 에서 할 수 있어요.");
                lines.Add(NotUsed);
                return lines;
            }
            // 한 신청에 두 줄: 공고의 이름, 그리고 상태. 신청서의 내용(개인정보)은 게임으로 오지 않는다.
            for (int i = 0; i < data.applications.Count && lines.Count + 2 <= BenefitUi.BoardLines - 1; i++)
            {
                ScholarshipApplicationDto row = data.applications[i];
                lines.Add(BenefitUi.Clip(row.programTitle));
                string state = row.statusText;
                if (!string.IsNullOrEmpty(row.resultText)) state += "  ·  " + row.resultText;
                else if (row.isMinor && row.consentStatus != "verified") state += "  ·  " + row.consentText;
                lines.Add(BenefitUi.Clip($"  {BenefitRules.DateText(row.submittedAt)} 접수  ·  {state}"));
            }
            lines.Add("자세한 내용과 이의신청은 홈페이지에서 볼 수 있어요.");
            return lines;
        }
    }

    /// <summary>
    /// 게임 속 휴대전화. 안내센터까지 가지 않고도 CITY PASS 와 장학 안내를 열 수 있고,
    /// 홈페이지 로그인 코드를 받을 수 있다.
    /// </summary>
    public static class PhoneActions
    {
        public const string PhoneTag = "phone";
        public const string Title = "휴대전화";
        public const string WebCodeTitle = "홈페이지 로그인 코드";

        public static void Open()
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            Action reopen = () =>
            {
                if (GameSession.Current == session && PhoneController.Available) Open();
            };
            ChoiceMenu.Instance.Open(Title, "혜택과 안내를 여기서도 볼 수 있어요.",
                new ChoiceOption
                {
                    label = "CITY PASS",
                    detail = "내 멤버십, 주변 제휴 상점, 할인 혜택, 내 쿠폰",
                    onChosen = () => CityPassActions.Open(null, reopen),
                },
                new ChoiceOption
                {
                    label = "장학 안내",
                    detail = "장학 프로그램, 모집 일정, 지원 방법, 심사 진행 상태",
                    onChosen = () => ScholarshipActions.Open(null, reopen),
                },
                new ChoiceOption
                {
                    label = WebCodeTitle,
                    cost = "5분",
                    detail = "홈페이지에 로그인할 때 한 번 쓰는 코드예요.",
                    onChosen = () => ShowWebCode(reopen),
                });
            ChoiceMenu.Instance.Tag = PhoneTag;
        }

        /// <summary>홈페이지 로그인 코드를 받아 보여 준다. 비밀번호 대신 쓰는 것이라 5분 뒤에는 못 쓴다.</summary>
        public static void ShowWebCode(Action onDone)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.IssueWebLoginCode(result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    BenefitUi.Failed(result, "로그인 코드 받기");
                    onDone?.Invoke();
                    return;
                }
                BenefitUi.Board(WebCodeTitle, WebCodeLines(result.data), onDone);
            });
        }

        public static List<string> WebCodeLines(WebLoginCodeDto code)
        {
            var lines = new List<string>
            {
                code.code,
                "",
                $"{BenefitRules.RemainingText(code.expiresInSeconds)} 안에 한 번만 쓸 수 있어요.",
                string.IsNullOrEmpty(code.loginUrl) ? "홈페이지의 로그인 화면에 넣어 주세요." : BenefitUi.Clip(code.loginUrl, 44),
                "",
                "이 코드는 다른 사람에게 알려 주지 마세요.",
                "새 코드를 받으면 앞의 코드는 쓸 수 없어요.",
            };
            return lines;
        }
    }
}
