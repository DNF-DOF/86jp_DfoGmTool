using System;
using System.Linq;
using DfoGmTool.ServerCore.Game.Currency;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.ServerCore.Game.Inventory
{
    public sealed partial class NewInventoryStore
    {
        // Match the server's CountMainItem: main inventory only, with virtual
        // account assets sourced from accounts rather than duplicate item rows.
        internal long CountQuestItem(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int accountId, int itemId)
        {
            if (CurrencyService.IsCubeFragment(itemId))
                return CurrencyService.LoadCubeFragments(connection, transaction, accountId)
                    .First(x => x.ItemId == itemId).Count;
            if (CurrencyService.IsSoulWarehouseItem(itemId))
                return CurrencyService.LoadSoulWarehouseCounts(connection, transaction, accountId)
                    .First(x => x.ItemId == itemId).Count;
            if (itemId >= 0 && itemId <= 2)
                return TryLoadItem(connection, transaction, characterId, accountId, InventoryListType.Main,
                    (short)itemId, out var record) ? Math.Max(0, record.Core.Count) : 0;
            return LoadList(connection, transaction, characterId, InventoryListType.Main, 0, short.MaxValue)
                .Where(x => x.Core.ItemId == itemId)
                .Sum(x => IsStackableKind(x.Core.ItemKind) ? (long)Math.Max(0, x.Core.Count) : 1L);
        }

        internal bool TryGrantQuestVirtualItem(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int accountId, int itemId, int count)
        {
            if (itemId > 2 && !CurrencyService.IsAccountWarehouseItem(itemId)) return false;
            var beforeCount = checked((int)CountQuestItem(connection, transaction, characterId, accountId, itemId));
            var afterCount = checked(beforeCount + count);
            short slot;
            ItemCore before;
            ItemCore after;
            if (CurrencyService.IsAccountWarehouseItem(itemId))
            {
                slot = checked((short)(CurrencyService.IsCubeFragment(itemId)
                    ? CurrencyService.GetCubeFragmentSlot(itemId) : CurrencyService.GetSoulWarehouseSlot(itemId)));
                before = new ItemCore { ItemKind = ItemCore.KindSpecialMaterial, ItemId = itemId, Count = beforeCount };
                after = before.Copy(); after.Count = afterCount;
                if (CurrencyService.IsCubeFragment(itemId))
                    CurrencyService.AddCubeFragment(connection, transaction, accountId, itemId, count);
                else CurrencyService.AddSoulWarehouse(connection, transaction, accountId, itemId, count);
            }
            else
            {
                slot = checked((short)itemId);
                before = TryLoadItem(connection, transaction, characterId, accountId, InventoryListType.Main, slot, out var row)
                    ? row.Core.Copy() : null;
                after = before?.Copy() ?? new ItemCore { ItemKind = ItemCore.KindSpecialMaterial, ItemId = itemId };
                after.Count = afterCount;
                UpsertCharacterCore(connection, transaction, characterId, InventoryListType.Main, slot, after);
            }
            WriteAudit(connection, transaction, "gm_quest_item_supplement", characterId, accountId,
                InventoryListType.Main, slot, before, after, 0);
            return true;
        }
    }
}
