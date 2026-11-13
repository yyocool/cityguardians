using System;
using System.Collections.Generic;

namespace CityGuardians.Domain
{
    /// <summary>인벤토리의 한 칸: 아이템 한 종류와 그 수.</summary>
    [Serializable]
    public sealed class ItemStack
    {
        public string itemCode;
        public int count;

        public ItemStack() { }

        public ItemStack(string itemCode, int count)
        {
            this.itemCode = itemCode;
            this.count = count;
        }
    }

    /// <summary>장착 칸 (docs/ITEM_SYSTEM.md 4장). 무기 두 칸과 입는 칸 두 개가 있다.</summary>
    public static class EquipSlots
    {
        public const string Melee = "melee";
        public const string Ranged = "ranged";
        /// <summary>머리: 방어구 또는 의상 하나.</summary>
        public const string Head = "head";
        /// <summary>몸: 방어구 또는 의상 하나.</summary>
        public const string Body = "body";

        public static bool IsWeaponSlot(string slot) => slot == Melee || slot == Ranged;
        public static bool IsWearSlot(string slot) => slot == Head || slot == Body;
    }

    /// <summary>아이템 요청을 받아들일 수 없는 이유.</summary>
    public static class ItemReasons
    {
        public const string UnknownItem = "unknown_item";
        public const string UnknownShop = "unknown_shop";
        public const string NotForSale = "not_for_sale";
        public const string AlreadyOwned = "already_owned";
        public const string StackFull = "stack_full";
        public const string BagFull = "bag_full";
        public const string InsufficientFunds = "insufficient_funds";
        public const string NotOwned = "not_owned";
        public const string WrongSlot = "wrong_slot";
        public const string CannotUnequip = "cannot_unequip";
        public const string NotUsable = "not_usable";
        /// <summary>상점이 되사지 않는 물건.</summary>
        public const string NotSellable = "not_sellable";
        /// <summary>가구를 더 가질 수 없음.</summary>
        public const string FurnitureFull = "furniture_full";
    }

    /// <summary>
    /// 인벤토리·상점·장착 규칙 (docs/ITEM_SYSTEM.md 3~6장). Unity에 의존하지 않는다.
    /// 실제 서버(PHP)의 검증이 이 규칙과 같아야 한다.
    /// </summary>
    public static class InventoryRules
    {
        public static int CountOf(IList<ItemStack> inventory, string itemCode)
        {
            if (inventory == null || string.IsNullOrEmpty(itemCode)) return 0;
            for (int i = 0; i < inventory.Count; i++)
                if (inventory[i].itemCode == itemCode) return Math.Max(0, inventory[i].count);
            return 0;
        }

        public static bool Owns(IList<ItemStack> inventory, string itemCode) => CountOf(inventory, itemCode) > 0;

        /// <summary>그 아이템을 count개 더 넣을 수 있는가. 넣을 수 있으면 null, 아니면 이유.</summary>
        public static string CheckAdd(GameDefs defs, IList<ItemStack> inventory, ItemDef item, int count)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            if (item == null) return ItemReasons.UnknownItem;
            if (count <= 0) return ItemReasons.UnknownItem;

            int have = CountOf(inventory, item.code);
            int maxStack = Math.Max(1, item.maxStack);
            if (item.IsFurniture) return ItemReasons.UnknownItem; // 가구는 가방에 넣지 않는다
            if (have > 0)
            {
                if (item.IsUnique) return ItemReasons.AlreadyOwned;
                return have + count > maxStack ? ItemReasons.StackFull : null;
            }
            if (count > maxStack) return ItemReasons.StackFull;
            int used = inventory != null ? inventory.Count : 0;
            return used >= defs.economy.inventorySlots ? ItemReasons.BagFull : null;
        }

        /// <summary>검사 없이 넣는다. 먼저 CheckAdd로 확인해야 한다.</summary>
        public static void Add(List<ItemStack> inventory, string itemCode, int count)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < inventory.Count; i++)
            {
                if (inventory[i].itemCode != itemCode) continue;
                inventory[i].count += count;
                return;
            }
            inventory.Add(new ItemStack(itemCode, count));
        }

        /// <summary>count개를 꺼낸다. 모자라면 아무것도 바꾸지 않고 false. 0개가 된 칸은 비운다.</summary>
        public static bool Remove(List<ItemStack> inventory, string itemCode, int count)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < inventory.Count; i++)
            {
                if (inventory[i].itemCode != itemCode) continue;
                if (inventory[i].count < count) return false;
                inventory[i].count -= count;
                if (inventory[i].count == 0) inventory.RemoveAt(i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 상점에서 그 아이템 하나를 살 수 있는가. 살 수 있으면 null, 아니면 이유. 가격은 정의에서 읽는다.
        /// 가구는 가방이 아니라 가구 목록에 들어가므로 가진 가구의 수(furnitureOwned)로 한도를 본다.
        /// </summary>
        public static string CheckPurchase(GameDefs defs, string shopCode, string itemCode,
            IList<ItemStack> inventory, long balance, int furnitureOwned = 0)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            ShopDef shop = defs.FindShop(shopCode);
            if (shop == null) return ItemReasons.UnknownShop;
            ItemDef item = defs.FindItem(itemCode);
            if (item == null) return ItemReasons.UnknownItem;
            if (!shop.Sells(itemCode) || item.priceGc <= 0) return ItemReasons.NotForSale;
            if (item.IsFurniture)
            {
                if (furnitureOwned >= defs.room.maxFurniture) return ItemReasons.FurnitureFull;
            }
            else
            {
                string reason = CheckAdd(defs, inventory, item, 1);
                if (reason != null) return reason;
            }
            return balance < item.priceGc ? ItemReasons.InsufficientFunds : null;
        }

        /// <summary>
        /// 가방의 그 아이템 하나를 그 상점에 되팔 수 있는가 (가구는 RoomRules.CheckSell).
        /// 그 물건을 파는 상점만 되사고, 값은 정의의 sellPriceGc다.
        /// </summary>
        /// <param name="equippedMelee">지금 든 근접 무기. 그것은 팔 수 없다 (맨손이 되지 않게).</param>
        public static string CheckSell(GameDefs defs, string shopCode, string itemCode, IList<ItemStack> inventory,
            string equippedMelee = null)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            ShopDef shop = defs.FindShop(shopCode);
            if (shop == null) return ItemReasons.UnknownShop;
            ItemDef item = defs.FindItem(itemCode);
            if (item == null) return ItemReasons.UnknownItem;
            if (!shop.Sells(itemCode) || item.sellPriceGc <= 0) return ItemReasons.NotSellable;
            if (item.IsFurniture) return ItemReasons.NotSellable;
            if (!Owns(inventory, itemCode)) return ItemReasons.NotOwned;
            return itemCode == equippedMelee ? ItemReasons.CannotUnequip : null;
        }

        /// <summary>
        /// 그 칸에 그 아이템을 장착할 수 있는가. itemCode가 비어 있으면 "칸 비우기"다.
        /// 근접 무기 칸은 비울 수 없다 (맨손이 되지 않게). 머리와 몸에는 그 칸의 방어구나 의상을 입는다.
        /// </summary>
        public static string CheckEquip(GameDefs defs, IList<ItemStack> inventory, string slot, string itemCode)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            if (!EquipSlots.IsWeaponSlot(slot) && !EquipSlots.IsWearSlot(slot)) return ItemReasons.WrongSlot;
            if (string.IsNullOrEmpty(itemCode))
                return slot == EquipSlots.Melee ? ItemReasons.CannotUnequip : null;

            ItemDef item = defs.FindItem(itemCode);
            if (item == null) return ItemReasons.UnknownItem;
            if (EquipSlots.IsWearSlot(slot))
            {
                if (!item.IsWearable || item.slot != slot) return ItemReasons.WrongSlot;
            }
            else
            {
                string wanted = slot == EquipSlots.Melee ? ItemCategory.WeaponMelee : ItemCategory.WeaponRanged;
                if (item.category != wanted || defs.FindWeapon(itemCode) == null) return ItemReasons.WrongSlot;
            }
            return Owns(inventory, itemCode) ? null : ItemReasons.NotOwned;
        }

        /// <summary>입은 것들의 방어(%)를 더한 값. 피해 계산은 DamageFormula.Reduce가 한다.</summary>
        public static int TotalDefense(GameDefs defs, string headCode, string bodyCode)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            int total = 0;
            ItemDef head = string.IsNullOrEmpty(headCode) ? null : defs.FindItem(headCode);
            ItemDef body = string.IsNullOrEmpty(bodyCode) ? null : defs.FindItem(bodyCode);
            if (head != null && head.slot == EquipSlots.Head) total += Math.Max(0, head.defense);
            if (body != null && body.slot == EquipSlots.Body) total += Math.Max(0, body.defense);
            return total;
        }

        /// <summary>그 아이템을 하나 쓸 수 있는가.</summary>
        public static string CheckUse(GameDefs defs, IList<ItemStack> inventory, string itemCode)
        {
            if (defs == null) throw new ArgumentNullException(nameof(defs));
            ItemDef item = defs.FindItem(itemCode);
            if (item == null) return ItemReasons.UnknownItem;
            if (item.category != ItemCategory.Consumable) return ItemReasons.NotUsable;
            return Owns(inventory, itemCode) ? null : ItemReasons.NotOwned;
        }
    }
}
