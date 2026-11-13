using System;
using System.Collections.Generic;
using CityGuardians.Domain;
using CityGuardians.Inputs;
using CityGuardians.Services;
using UnityEngine;

namespace CityGuardians.Gameplay
{
    /// <summary>
    /// 아이템으로 하는 일들: 상점에서 사고팔기, 가방 보기, 장착과 입기, 무기 바꾸기, 수리 키트 쓰기 (docs/ITEM_SYSTEM.md).
    /// 값과 성공 여부는 언제나 서버가 정하고, 여기서는 요청을 보내고 결과를 화면에 알린다.
    /// 화면은 숫자 키로 고르는 선택 메뉴를 그대로 쓴다.
    /// </summary>
    public static class ItemActions
    {
        public const string BagTag = "bag";
        public const string ShopTag = "shop";
        public const string WardrobeTag = "wardrobe";
        public const string SellLabel = "물건 팔기";
        private const float KitCooldown = 0.5f;
        private const float HintCooldown = 2.5f;

        private static float _nextKitTime;
        private static float _nextHintTime;

        /// <summary>게임을 새로 시작할 때 부른다 (에디터에서 도메인을 다시 불러오지 않는 경우에도 깨끗하게).</summary>
        public static void ResetState()
        {
            _nextKitTime = 0f;
            _nextHintTime = 0f;
        }

        private static string NewRequestId() => Guid.NewGuid().ToString("N");

        // ── 무기 바꾸기 ─────────────────────────────────────────

        /// <summary>손에 든 무기를 바꾼다 (숫자 1·2).</summary>
        public static void SelectWeapon(string slot)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;

            if (slot == EquipSlots.Ranged && session.EquippedRanged == null)
            {
                // 아직 원거리 무기가 없다. 같은 안내를 연달아 띄우지 않는다.
                if (Time.unscaledTime < _nextHintTime) return;
                _nextHintTime = Time.unscaledTime + HintCooldown;
                bool owned = session.Owns(ItemCodes.BoltBlaster);
                GameEvents.ShowToast(owned
                    ? "원거리 무기를 장착하지 않았어요. I 키로 가방을 열어 장착해 보세요."
                    : "원거리 무기가 없어요. 미소 상점에서 살 수 있어요.");
                return;
            }

            if (session.ActiveSlot == slot) return;
            if (session.SelectSlot(slot)) Sfx.Play(Sfx.Id.Interact);
        }

        // ── 수리 키트 ───────────────────────────────────────────

        /// <summary>
        /// 수리 키트를 하나 쓴다 (숫자 3, 또는 가방에서). 회복을 먼저 보여 주고,
        /// 서버가 거절하면 되돌린다 (docs/ITEM_SYSTEM.md 5장).
        /// </summary>
        /// <param name="onDone">서버 응답까지 끝난 뒤. 쓰지 못했을 때도 부른다.</param>
        public static void UseRepairKit(Action onDone = null)
        {
            GameSession session = GameSession.Current;
            PlayerController player = PlayerController.Instance;
            if (session == null || player == null || player.Health.IsDead) { onDone?.Invoke(); return; }

            ItemDef kit = session.Defs.FindItem(ItemCodes.RepairKit);
            if (kit == null) { onDone?.Invoke(); return; }

            if (session.CountOf(kit.code) <= 0)
            {
                Hint($"{Korean.WithGa(kit.name)} 없어요. 미소 상점에서 살 수 있어요.");
                onDone?.Invoke();
                return;
            }
            if (player.Health.Current >= player.Health.Max)
            {
                Hint("체력이 가득해요.");
                onDone?.Invoke();
                return;
            }
            if (Time.unscaledTime < _nextKitTime) { onDone?.Invoke(); return; }
            _nextKitTime = Time.unscaledTime + KitCooldown;

            // 먼저 보여 준다.
            int before = player.Health.Current;
            player.Health.Heal(kit.healHp);
            int healed = player.Health.Current - before;
            Sfx.Play(Sfx.Id.Heal);
            Fx.FloatingText(player.transform.position + Vector3.up * 1.9f, $"+{healed}", new Color(0.5f, 1f, 0.6f), 0.4f);

            session.Backend.UseItem(kit.code, NewRequestId(), result =>
            {
                if (GameSession.Current == session)
                {
                    if (result.ok)
                    {
                        session.ApplyItemUse(result.data);
                    }
                    else
                    {
                        // 서버가 인정하지 않았다: 회복을 되돌리고 서버의 인벤토리를 다시 받는다.
                        Debug.LogWarning($"아이템 사용 실패: {result.errorCode} {result.message}");
                        if (player != null) player.Health.UndoHeal(healed);
                        GameEvents.ShowToast($"{Korean.WithEul(kit.name)} 쓰지 못했어요.");
                        session.Backend.GetMe(me => { if (me.ok && GameSession.Current == session) session.ApplyMe(me.data); });
                    }
                }
                onDone?.Invoke();
            });
        }

        private static void Hint(string text)
        {
            if (Time.unscaledTime < _nextHintTime) return;
            _nextHintTime = Time.unscaledTime + HintCooldown;
            GameEvents.ShowToast(text);
        }

        // ── 줄이 많은 메뉴 ──────────────────────────────────────

        /// <summary>
        /// 줄이 한 화면보다 많으면 쪽으로 나눈다. 여러 쪽이면 마지막 줄이 "다음 쪽"이 되고, 끝에서 처음으로 돌아간다.
        /// </summary>
        /// <param name="open">그 쪽을 다시 여는 방법.</param>
        private static ChoiceOption[] Paged(List<ChoiceOption> rows, int page, Action<int> open)
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
                detail = "다른 물건을 봐요.",
                onChosen = () => open(next),
            });
            return shown.ToArray();
        }

        // ── 상점 ────────────────────────────────────────────────

        /// <summary>열려 있는 상점 하나. 메뉴 사이를 오가는 동안 들고 다닌다.</summary>
        private sealed class ShopVisit
        {
            public GameSession session;
            public ShopDto shop;
            public NpcAgent keeper;
            /// <summary>진열된 물건 가운데 이 클라이언트가 아는 것.</summary>
            public readonly List<ShopItemDto> entries = new List<ShopItemDto>();
            /// <summary>진열을 묶은 분류 이름 (나온 순서대로).</summary>
            public readonly List<string> groups = new List<string>();
            public bool buysBack;
            /// <summary>한 화면에 다 들어가지 않아 분류로 나눠 보여 주는가.</summary>
            public bool grouped;

            public bool Alive => GameSession.Current == session;
            public ItemDef Item(ShopItemDto entry) => session.Defs.FindItem(entry.itemCode);
        }

        /// <summary>상점을 연다. 진열과 가격은 서버에서 받는다.</summary>
        /// <param name="keeper">상점 주인. 인사말을 하게 하는 데 쓴다.</param>
        public static void OpenShop(string shopCode, NpcAgent keeper = null)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;
            session.Backend.GetShop(shopCode, result =>
            {
                if (GameSession.Current != session) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"상점 열기 실패 ({shopCode}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast("상점을 열지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    return;
                }

                var visit = new ShopVisit { session = session, shop = result.data, keeper = keeper };
                for (int i = 0; i < visit.shop.items.Count; i++)
                {
                    ShopItemDto entry = visit.shop.items[i];
                    ItemDef item = visit.Item(entry);
                    if (item == null) continue;
                    visit.entries.Add(entry);
                    if (entry.sellPriceGc > 0) visit.buysBack = true;
                    string group = GroupOf(item);
                    if (!visit.groups.Contains(group)) visit.groups.Add(group);
                }
                if (visit.entries.Count == 0)
                {
                    GameEvents.ShowToast("지금은 파는 물건이 없어요.");
                    return;
                }
                visit.grouped = visit.entries.Count + (visit.buysBack ? 1 : 0) > ChoiceMenu.MaxOptions;
                ShowShop(visit);
            });
        }

        /// <summary>상점에서 물건을 묶어 보여 주는 이름.</summary>
        private static string GroupOf(ItemDef item)
        {
            if (item.IsWeapon) return "무기";
            if (item.category == ItemCategory.Armor) return "방어구";
            if (item.category == ItemCategory.Costume) return "옷";
            if (item.IsFurniture) return "가구";
            return "도구";
        }

        private static string Money(GameSession session) => $"가진 돈  {session.Me.gc:N0} GC";

        /// <summary>상점의 첫 화면. 물건이 적으면 바로 진열을, 많으면 분류를 보여 준다.</summary>
        private static void ShowShop(ShopVisit visit)
        {
            if (!visit.Alive) return;
            if (!visit.grouped)
            {
                ShowShopItems(visit, null, 0);
                return;
            }

            var options = new List<ChoiceOption>();
            for (int g = 0; g < visit.groups.Count && options.Count < ChoiceMenu.MaxOptions - 1; g++)
            {
                string group = visit.groups[g];
                var names = new List<string>();
                for (int i = 0; i < visit.entries.Count; i++)
                {
                    ItemDef item = visit.Item(visit.entries[i]);
                    if (GroupOf(item) == group) names.Add(item.name);
                }
                options.Add(new ChoiceOption
                {
                    label = $"{group} 사기",
                    cost = $"{names.Count}가지",
                    detail = string.Join(" · ", names),
                    onChosen = () => ShowShopItems(visit, group, 0),
                });
            }
            if (visit.buysBack) options.Add(SellOption(visit));

            ChoiceMenu.Instance.Open(visit.shop.name, $"무엇을 할까요?\n{Money(visit.session)}", options.ToArray());
            ChoiceMenu.Instance.Tag = ShopTag;
        }

        private static ChoiceOption SellOption(ShopVisit visit) => new ChoiceOption
        {
            label = SellLabel,
            detail = "이 가게에서 파는 물건을 다시 팔아요.",
            onChosen = () => ShowSell(visit, 0),
        };

        /// <summary>진열. group이 비어 있으면 상점의 모든 물건(그리고 팔기)이고, 아니면 그 분류의 물건이다.</summary>
        private static void ShowShopItems(ShopVisit visit, string group, int page)
        {
            if (!visit.Alive) return;
            GameSession session = visit.session;

            var rows = new List<ChoiceOption>();
            for (int i = 0; i < visit.entries.Count; i++)
            {
                ShopItemDto entry = visit.entries[i];
                ItemDef item = visit.Item(entry);
                if (group != null && GroupOf(item) != group) continue;
                rows.Add(ShopRow(visit, entry, item, group, page));
            }
            if (group == null && visit.buysBack) rows.Add(SellOption(visit));

            string title = group == null ? visit.shop.name : $"{visit.shop.name} · {group}";
            ChoiceMenu.Instance.Open(title, $"무엇을 살까요?\n{Money(session)}",
                Paged(rows, page, next => ShowShopItems(visit, group, next)));
            ChoiceMenu.Instance.Tag = ShopTag;
            // 분류 안에서는 E가 상점을 닫지 않고 분류 화면으로 돌아간다.
            if (group != null) ChoiceMenu.Instance.OnCancelled = () => ShowShop(visit);
        }

        private static ChoiceOption ShopRow(ShopVisit visit, ShopItemDto entry, ItemDef item, string group, int page)
        {
            GameSession session = visit.session;
            int owned = item.IsFurniture
                ? RoomRules.CountOwned(session.Me.furniture, item.code)
                : session.CountOf(item.code);
            var option = new ChoiceOption { label = item.name, cost = $"{entry.priceGc:N0} GC" };

            if (item.IsUnique && owned > 0)
            {
                option.label = $"{item.name} (구매 완료)";
                option.cost = "";
                option.detail = "가방에 있어요. I 키로 가방을 열 수 있어요.";
                option.enabled = false;
            }
            else if (item.IsFurniture && FurnitureCount(session) >= session.Defs.room.maxFurniture)
            {
                option.detail = $"가구를 더 가질 수 없어요. (가진 가구 {FurnitureCount(session)}개)";
                option.enabled = false;
            }
            else if (!item.IsUnique && !item.IsFurniture && owned >= Mathf.Max(1, item.maxStack))
            {
                option.detail = $"더 가질 수 없어요. (가진 수 {owned}개)";
                option.enabled = false;
            }
            else if (session.Me.gc < entry.priceGc)
            {
                option.detail = $"GC가 모자라요. (지금 {session.Me.gc:N0} GC)";
                option.enabled = false;
            }
            else
            {
                option.detail = DescribeForSale(session, item, owned);
                option.onChosen = () => Buy(visit, entry, group, page);
            }
            return option;
        }

        private static int FurnitureCount(GameSession session) => session.Me.furniture != null ? session.Me.furniture.Count : 0;

        private static string DescribeForSale(GameSession session, ItemDef item, int owned)
        {
            if (item.IsWeapon) return DescribeWeapon(session, item);
            if (item.IsWearable) return DescribeWear(item);
            if (item.IsFurniture) return $"{item.description}  방에서 F 키로 놓아요.  (가진 수 {owned}개)";
            return $"{item.description}  숫자 3 키로 바로 써요.  (가진 수 {owned}개)";
        }

        private static string DescribeWeapon(GameSession session, ItemDef item)
        {
            WeaponDef weapon = session.Defs.FindWeapon(item.code);
            if (weapon == null) return item.description;
            string rate = weapon.kind == WeaponKind.Ranged ? $"초당 {weapon.attacksPerSecond:0.#}발" : $"초당 {weapon.attacksPerSecond:0.#}회";
            return $"{item.description}  피해 {weapon.damage} · {rate}";
        }

        /// <summary>입는 물건의 설명. 방어구는 줄여 주는 피해를 함께 적는다.</summary>
        private static string DescribeWear(ItemDef item)
        {
            return item.defense > 0 ? $"{item.description}  받는 피해 -{item.defense}%" : $"{item.description}  모습만 바뀌어요.";
        }

        private static void Buy(ShopVisit visit, ShopItemDto entry, string group, int page)
        {
            GameSession session = visit.session;
            ItemDef item = visit.Item(entry);
            session.Backend.BuyItem(visit.shop.code, entry.itemCode, NewRequestId(), result =>
            {
                if (!visit.Alive) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"구매 실패 ({entry.itemCode}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast(PurchaseFailText(result.errorCode));
                    return;
                }

                session.ApplyPurchase(result.data);
                Sfx.Play(Sfx.Id.Coin);
                GameEvents.ShowToast(item.IsFurniture
                    ? $"{Korean.WithEul(item.name)} 샀어요!  -{result.data.priceGc:N0} GC  내 방에서 F 키로 놓아 보세요."
                    : $"{Korean.WithEul(item.name)} 샀어요!  -{result.data.priceGc:N0} GC");
                if (visit.keeper != null) visit.keeper.Say("고마워요! 잘 써 줘요!", 3.5f);

                // 장비와 옷은 바로 쓸지 묻고, 여러 개 살 수 있는 것은 진열을 다시 연다.
                if (item.IsWeapon) OfferEquip(session, item);
                else if (item.IsWearable) OfferWear(session, item);
                else ShowShopItems(visit, group, page);
            });
        }

        private static string PurchaseFailText(string errorCode)
        {
            switch (errorCode)
            {
                case ApiErrors.InsufficientFunds: return "GC가 모자라요.";
                case ApiErrors.AlreadyOwned: return "이미 가지고 있어요.";
                case ApiErrors.InventoryFull: return "더 가질 수 없어요.";
                default: return "사지 못했어요. 잠시 뒤에 다시 시도해 주세요.";
            }
        }

        /// <summary>새로 산 장비를 바로 쓸지 묻는다 (docs/USER_FLOW.md 5.1).</summary>
        private static void OfferEquip(GameSession session, ItemDef item)
        {
            string slot = item.category == ItemCategory.WeaponMelee ? EquipSlots.Melee : EquipSlots.Ranged;
            ChoiceMenu.Instance.Open($"새 장비: {item.name}", "바로 쓸까요?",
                new ChoiceOption
                {
                    label = "바로 장착하기",
                    detail = "숫자 1 · 2 키로 언제든 무기를 바꿀 수 있어요.",
                    onChosen = () => Equip(session, slot, item.code, null),
                },
                new ChoiceOption
                {
                    label = "나중에",
                    detail = "I 키로 가방을 열어 장착할 수 있어요.",
                });
            ChoiceMenu.Instance.Tag = ShopTag;
        }

        /// <summary>새로 산 방어구나 옷을 바로 입을지 묻는다.</summary>
        private static void OfferWear(GameSession session, ItemDef item)
        {
            bool head = item.slot == EquipSlots.Head;
            ChoiceMenu.Instance.Open($"새 {(item.defense > 0 ? "방어구" : "옷")}: {item.name}", head ? "바로 쓸까요?" : "바로 입을까요?",
                new ChoiceOption
                {
                    label = head ? "바로 쓰기" : "바로 입기",
                    detail = DescribeWear(item),
                    onChosen = () => Equip(session, item.slot, item.code, null),
                },
                new ChoiceOption
                {
                    label = "나중에",
                    detail = "I 키로 가방을 열거나 내 방의 옷장에서 갈아입을 수 있어요.",
                });
            ChoiceMenu.Instance.Tag = ShopTag;
        }

        // ── 되팔기 ──────────────────────────────────────────────

        /// <summary>이 상점에 되팔 수 있는, 지금 가진 물건들. 가구는 넣어 둔 것만 팔 수 있다.</summary>
        private static void ShowSell(ShopVisit visit, int page)
        {
            if (!visit.Alive) return;
            GameSession session = visit.session;

            var rows = new List<ChoiceOption>();
            for (int i = 0; i < visit.entries.Count; i++)
            {
                ShopItemDto entry = visit.entries[i];
                if (entry.sellPriceGc <= 0) continue;
                ItemDef item = visit.Item(entry);

                if (item.IsFurniture)
                {
                    int stored = session.StoredFurniture(item.code);
                    int owned = RoomRules.CountOwned(session.Me.furniture, item.code);
                    if (owned <= 0) continue;
                    rows.Add(new ChoiceOption
                    {
                        label = item.name,
                        cost = $"+{entry.sellPriceGc:N0} GC",
                        detail = stored > 0 ? $"넣어 둔 것 {stored}개" : "방에 놓여 있어요. 넣어 둔 가구만 팔 수 있어요.",
                        enabled = stored > 0,
                        onChosen = () => ConfirmSell(visit, entry, item, page),
                    });
                    continue;
                }

                if (!session.Owns(item.code)) continue;
                bool inUse = IsInUse(session, item);
                rows.Add(new ChoiceOption
                {
                    label = item.name,
                    cost = $"+{entry.sellPriceGc:N0} GC",
                    detail = inUse ? InUseText(item) : "가방에 있어요.",
                    onChosen = () => ConfirmSell(visit, entry, item, page),
                });
            }
            if (rows.Count == 0)
            {
                rows.Add(new ChoiceOption
                {
                    label = "팔 수 있는 물건이 없어요",
                    detail = "이 가게에서 파는 물건만 다시 팔 수 있어요.",
                    enabled = false,
                });
            }

            ChoiceMenu.Instance.Open($"{visit.shop.name} · 팔기", $"무엇을 팔까요?  산 값보다 싸게 팔려요.\n{Money(session)}",
                Paged(rows, page, next => ShowSell(visit, next)));
            ChoiceMenu.Instance.Tag = ShopTag;
            ChoiceMenu.Instance.OnCancelled = () => ShowShop(visit);
        }

        private static bool IsInUse(GameSession session, ItemDef item)
        {
            return session.Me.equippedRanged == item.code || session.Me.equippedMelee == item.code
                   || session.Me.equippedHead == item.code || session.Me.equippedBody == item.code;
        }

        private static string InUseText(ItemDef item)
        {
            if (item.IsWeapon) return "지금 장착하고 있어요. 팔면 장착이 풀려요.";
            return item.slot == EquipSlots.Head ? "지금 쓰고 있어요. 팔면 벗겨져요." : "지금 입고 있어요. 팔면 벗겨져요.";
        }

        /// <summary>실수로 팔지 않게 한 번 더 묻는다.</summary>
        private static void ConfirmSell(ShopVisit visit, ShopItemDto entry, ItemDef item, int page)
        {
            if (!visit.Alive) return;
            GameSession session = visit.session;
            string body = $"{Korean.WithEul(item.name)} 팔까요?  {entry.sellPriceGc:N0} GC를 받아요.";
            if (!item.IsFurniture && IsInUse(session, item)) body += "\n" + InUseText(item);
            else body += "\n판 물건은 돌아오지 않아요.";

            ChoiceMenu.Instance.Open($"{item.name} 팔기", body,
                new ChoiceOption
                {
                    label = "팔기",
                    cost = $"+{entry.sellPriceGc:N0} GC",
                    detail = $"팔면  가진 돈 {session.Me.gc + entry.sellPriceGc:N0} GC",
                    onChosen = () => Sell(visit, entry, item, page),
                },
                new ChoiceOption
                {
                    label = "그만두기",
                    detail = "팔지 않고 돌아가요.",
                    onChosen = () => ShowSell(visit, page),
                });
            ChoiceMenu.Instance.Tag = ShopTag;
            ChoiceMenu.Instance.OnCancelled = () => ShowSell(visit, page);
        }

        private static void Sell(ShopVisit visit, ShopItemDto entry, ItemDef item, int page)
        {
            GameSession session = visit.session;
            session.Backend.SellItem(visit.shop.code, entry.itemCode, NewRequestId(), result =>
            {
                if (!visit.Alive) return;
                if (!result.ok)
                {
                    Debug.LogWarning($"판매 실패 ({entry.itemCode}): {result.errorCode} {result.message}");
                    GameEvents.ShowToast(SellFailText(result.errorCode));
                    ShowSell(visit, page);
                    return;
                }

                session.ApplySale(result.data);
                Sfx.Play(Sfx.Id.Coin);
                GameEvents.ShowToast($"{Korean.WithEul(item.name)} 팔았어요!  +{result.data.priceGc:N0} GC");
                if (visit.keeper != null) visit.keeper.Say("잘 받았어요!", 3.5f);
                ShowSell(visit, page);
            });
        }

        private static string SellFailText(string errorCode)
        {
            switch (errorCode)
            {
                case ApiErrors.NotOwned: return "팔 물건이 없어요.";
                case ApiErrors.Conflict: return "지금은 팔 수 없어요. 방에 놓은 가구는 먼저 넣어 두세요.";
                default: return "팔지 못했어요. 잠시 뒤에 다시 시도해 주세요.";
            }
        }

        // ── 장착과 입기 ─────────────────────────────────────────

        /// <summary>
        /// 장착 칸을 바꾼다. itemCode가 비어 있으면 그 칸을 비운다. 장착한 무기는 바로 손에 들고,
        /// 입은 것은 바로 모습에 보인다 (PlayerWear가 맞춘다).
        /// </summary>
        private static void Equip(GameSession session, string slot, string itemCode, Action onDone)
        {
            session.Backend.EquipItem(slot, itemCode, result =>
            {
                if (GameSession.Current == session)
                {
                    if (result.ok)
                    {
                        session.ApplyEquip(result.data);
                        Sfx.Play(Sfx.Id.Build);
                        GameEvents.ShowToast(EquipText(session, slot, itemCode));
                    }
                    else
                    {
                        Debug.LogWarning($"장착 실패 ({slot}, {itemCode}): {result.errorCode} {result.message}");
                        GameEvents.ShowToast("장착하지 못했어요. 잠시 뒤에 다시 시도해 주세요.");
                    }
                }
                onDone?.Invoke();
            });
        }

        private static string EquipText(GameSession session, string slot, string itemCode)
        {
            ItemDef item = string.IsNullOrEmpty(itemCode) ? null : session.Defs.FindItem(itemCode);
            if (EquipSlots.IsWearSlot(slot))
            {
                if (item == null) return "벗었어요.";
                string text = $"{Korean.WithEul(item.name)} {(slot == EquipSlots.Head ? "썼어요" : "입었어요")}!";
                return item.defense > 0 ? $"{text}  받는 피해 -{session.Defense}%" : text;
            }

            if (item == null) return "장착을 풀었어요.";
            session.SelectSlot(slot);
            return $"{Korean.WithEul(item.name)} 들었어요!  1 · 2 키로 무기를 바꿔요.";
        }

        // ── 가방 ────────────────────────────────────────────────

        /// <summary>
        /// 가방을 연다 (I 키). 가진 아이템이 줄로 보이고, 숫자 키로 장착하거나 쓴다.
        /// 한 쪽에 다 들어가지 않으면 마지막 줄이 "다음 쪽"이 된다.
        /// </summary>
        public static void OpenBag(int page = 0)
        {
            GameSession session = GameSession.Current;
            if (session == null) return;

            List<ItemStack> inventory = session.Me.inventory ?? new List<ItemStack>();
            Action reopen = () => { if (GameSession.Current == session) OpenBag(page); };
            var rows = new List<ChoiceOption>();
            for (int i = 0; i < inventory.Count; i++)
            {
                ChoiceOption option = BagRow(session, inventory[i], reopen);
                if (option != null) rows.Add(option);
            }
            if (rows.Count == 0)
            {
                rows.Add(new ChoiceOption { label = "가방이 비어 있어요", detail = "상점에서 물건을 살 수 있어요.", enabled = false });
            }

            string head = $"{inventory.Count} / {session.Defs.economy.inventorySlots}칸";
            if (session.Defense > 0) head += $"          받는 피해 -{session.Defense}%";
            ChoiceMenu.Instance.Open("가방", $"{head}\n숫자 키로 장착하거나 사용해요.", Paged(rows, page, OpenBag));
            ChoiceMenu.Instance.Tag = BagTag;
        }

        /// <summary>
        /// 옷장을 연다 (내 방). 가방 가운데 입는 것만 보여 준다. 입을 것이 하나도 없으면 열지 않고 false.
        /// </summary>
        public static bool OpenWardrobe(int page = 0)
        {
            GameSession session = GameSession.Current;
            if (session == null) return false;

            List<ItemStack> inventory = session.Me.inventory ?? new List<ItemStack>();
            Action reopen = () => { if (GameSession.Current == session) OpenWardrobe(page); };
            var rows = new List<ChoiceOption>();
            for (int i = 0; i < inventory.Count; i++)
            {
                ItemDef item = session.Defs.FindItem(inventory[i].itemCode);
                if (item == null || !item.IsWearable) continue;
                rows.Add(BagRow(session, inventory[i], reopen));
            }
            if (rows.Count == 0) return false;

            string head = session.Defense > 0 ? $"받는 피해 -{session.Defense}%" : "무엇을 입을까요?";
            ChoiceMenu.Instance.Open("옷장", $"{head}\n숫자 키로 입거나 벗어요.", Paged(rows, page, next => OpenWardrobe(next)));
            ChoiceMenu.Instance.Tag = WardrobeTag;
            return true;
        }

        /// <param name="reopen">장착이나 사용이 끝난 뒤 같은 화면을 다시 여는 방법.</param>
        private static ChoiceOption BagRow(GameSession session, ItemStack stack, Action reopen)
        {
            ItemDef item = session.Defs.FindItem(stack.itemCode);
            if (item == null) return null;

            if (item.category == ItemCategory.WeaponMelee)
            {
                bool equipped = session.Me.equippedMelee == item.code;
                return new ChoiceOption
                {
                    label = item.name,
                    cost = equipped ? "장착 중" : "장착하기",
                    detail = DescribeWeapon(session, item),
                    // 근접 무기 칸은 비울 수 없다.
                    enabled = !equipped,
                    onChosen = () => Equip(session, EquipSlots.Melee, item.code, reopen),
                };
            }

            if (item.category == ItemCategory.WeaponRanged)
            {
                bool equipped = session.Me.equippedRanged == item.code;
                return new ChoiceOption
                {
                    label = item.name,
                    cost = equipped ? "장착 중" : "장착하기",
                    detail = equipped ? "누르면 장착을 풀어요." : DescribeWeapon(session, item),
                    onChosen = () => Equip(session, EquipSlots.Ranged, equipped ? null : item.code, reopen),
                };
            }

            if (item.IsWearable)
            {
                bool head = item.slot == EquipSlots.Head;
                bool worn = (head ? session.Me.equippedHead : session.Me.equippedBody) == item.code;
                return new ChoiceOption
                {
                    label = item.name,
                    cost = worn ? (head ? "쓰는 중" : "입는 중") : (head ? "쓰기" : "입기"),
                    detail = worn ? "누르면 벗어요." : DescribeWear(item),
                    onChosen = () => Equip(session, item.slot, worn ? null : item.code, reopen),
                };
            }

            PlayerController player = PlayerController.Instance;
            bool hurt = player != null && !player.Health.IsDead && player.Health.Current < player.Health.Max;
            return new ChoiceOption
            {
                label = item.name,
                cost = $"× {stack.count}",
                detail = hurt ? $"{item.description}  누르면 하나 써요." : $"{item.description}  지금은 체력이 가득해요.",
                enabled = hurt,
                onChosen = () => UseRepairKit(reopen),
            };
        }
    }

    /// <summary>I 키로 가방을 열고 닫는다. 장면이 바뀌어도 남아 있는 GameRoot에 붙는다.</summary>
    public sealed class BagController : MonoBehaviour
    {
        /// <summary>배턴을 받기 전에는 가방이 없다.</summary>
        public const int MinTutorialStep = 2;

        private void Update()
        {
            GameSession session = GameSession.Current;
            if (session == null || GameRoot.Instance.IsBusy) return;
            if (!PlayerInputHub.Current.InventoryPressed) return;

            ChoiceMenu menu = ChoiceMenu.Instance;
            if (menu.IsOpen && menu.Tag == ItemActions.BagTag)
            {
                if (!GameState.IsPaused) menu.Close();
                return;
            }
            if (GameState.IsControlLocked || GameState.Decorating || session.TutorialStep < MinTutorialStep) return;
            Sfx.Play(Sfx.Id.Interact);
            ItemActions.OpenBag();
        }
    }

    /// <summary>
    /// 플레이어가 입은 것을 모습과 방어에 맞춘다. 가방, 옷장, 상점 어디에서 바꿔도 세션이 바뀌면 따라간다.
    /// </summary>
    public sealed class PlayerWear : MonoBehaviour
    {
        private PlayerController _player;
        private GameSession _session;

        private void Start()
        {
            _player = GetComponent<PlayerController>();
            _session = GameSession.Current;
            if (_session != null) _session.Changed += Apply;
            Apply();
        }

        private void OnDestroy()
        {
            if (_session != null) _session.Changed -= Apply;
        }

        private void Apply()
        {
            if (_session == null || _player == null || _player.Character == null) return;
            _player.Health.defensePercent = _session.Defense;

            ItemDef head = _session.Worn(EquipSlots.Head);
            ItemDef body = _session.Worn(EquipSlots.Body);
            // 깜빡이는 중에 색을 바꾸면 되돌아올 때 덮이므로, 먼저 원래 재질로 돌려놓는다.
            var flash = _player.Character.GetComponent<HitFlash>();
            bool changes = (head != null ? head.look : null) != _player.Character.HeadLook
                           || (body != null ? body.look : null) != _player.Character.BodyLook;
            if (!changes) return;
            if (flash != null) flash.Capture();
            _player.Character.Wear(head != null ? head.look : null, body != null ? body.look : null);
            // 부품이 바뀌었으니 피격 깜빡임과, 캐릭터를 숨겨 둔 카메라가 새 부품도 알게 한다.
            if (flash != null) flash.Capture();
            if (OrbitCamera.Instance != null) OrbitCamera.Instance.RefreshTargetVisibility();
        }
    }

    /// <summary>한국어 조사 붙이기. 아이템 이름처럼 데이터에서 오는 낱말에 쓴다.</summary>
    public static class Korean
    {
        /// <summary>"이름을" 또는 "이름를": 마지막 글자에 받침이 있으면 "을".</summary>
        public static string WithEul(string word) => word + (HasFinalConsonant(word) ? "을" : "를");

        /// <summary>"이름이" 또는 "이름가": 마지막 글자에 받침이 있으면 "이".</summary>
        public static string WithGa(string word) => word + (HasFinalConsonant(word) ? "이" : "가");

        public static bool HasFinalConsonant(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            char last = word[word.Length - 1];
            if (last < 0xAC00 || last > 0xD7A3) return false;
            return (last - 0xAC00) % 28 != 0;
        }
    }
}
