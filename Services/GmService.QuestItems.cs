using System;
using System.Collections.Generic;
using System.Globalization;
using DfoGmTool.ServerCore.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed partial class GmService
    {
        internal sealed class QuestItemDelivery
        {
            public long inventoryCount { get; set; }
            public long mailedCount { get; set; }
            public long pendingMailCount { get; set; }
            public int mailCount { get; set; }
            public bool reselectCharacter => true;
        }

        // Only seeking IntData is an item/count list; other types contain
        // monster IDs, dungeon objectives, etc. Reject malformed lists fully.
        internal static bool TryParseQuestItems(PvfIndexService.QuestMeta meta,
            out Dictionary<int, int> items, out string error)
        {
            items = new Dictionary<int, int>(); error = null;
            if (meta == null) { error = "任务资料尚未加载"; return false; }
            if (!string.Equals(meta.Type, "seeking", StringComparison.Ordinal)) return true;
            var tokens = (meta.IntData ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length % 2 != 0)
            { error = "任务提交道具配置不是完整的物品/数量列表"; return false; }
            for (var i = 0; i < tokens.Length; i += 2)
            {
                if (!int.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0
                    || !int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count <= 0)
                { error = "任务提交道具配置含无效编号或数量"; return false; }
                items.TryGetValue(id, out var previous);
                if ((long)previous + count > int.MaxValue)
                { error = "任务提交道具数量超出范围"; return false; }
                items[id] = previous + count;
            }
            return true;
        }

        private bool TrySupplyQuestItems(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, PvfIndexService.QuestMeta meta, out QuestItemDelivery delivery, out string error)
        {
            delivery = new QuestItemDelivery();
            if (!TryParseQuestItems(meta, out var requirements, out error)) return false;
            if (requirements.Count == 0) return true;
            int accountId, job;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT account_id, job FROM characters WHERE character_id=@cid AND delete_flag=0";
                command.Parameters.AddWithValue("@cid", characterId);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) { error = "角色不存在或已删除"; return false; }
                accountId = reader.GetInt32(0); job = reader.GetInt32(1);
            }
            foreach (var requirement in requirements)
            {
                var held = _inventory.CountQuestItem(connection, transaction, characterId, accountId, requirement.Key);
                var missing = Math.Max(0L, requirement.Value - held);
                if (missing == 0) continue;
                var pending = CountPendingQuestItems(connection, transaction, characterId, requirement.Key);
                delivery.pendingMailCount += Math.Min(missing, pending);
                missing = Math.Max(0L, missing - pending);
                if (missing == 0) continue;
                var count = checked((int)missing);
                if (_inventory.TryGrantQuestVirtualItem(connection, transaction, characterId, accountId, requirement.Key, count))
                { delivery.inventoryCount += count; continue; }

                // Failed grants may have merged some stacks. Roll those changes
                // and their audits back before mailing the full shortage.
                transaction.Save("quest_item_grant");
                var grant = _inventory.TryGrant(connection, transaction, characterId, accountId, job,
                    requirement.Key, count, new ItemGrantOptions());
                if (grant.Success && grant.ListType == InventoryListType.Main)
                {
                    transaction.Release("quest_item_grant");
                    delivery.inventoryCount += count;
                    continue;
                }
                transaction.Rollback("quest_item_grant");
                transaction.Release("quest_item_grant");
                if (grant.Error != "背包空间不足" || grant.ListType != InventoryListType.Main)
                { error = "无法补发任务道具 " + requirement.Key + "：" + (grant.Error ?? "该物品不属于主背包"); return false; }
                var mail = _systemMail.SendItemGrant(characterId, accountId, requirement.Key, count,
                    new ItemGrantOptions(), "quest-ready:" + characterId + ":" + Guid.NewGuid().ToString("N"),
                    "任务「" + (meta.Name ?? meta.Id.ToString()) + "」所需道具 " + requirement.Key,
                    connection, transaction);
                if (!mail.Success) { error = mail.Error; return false; }
                delivery.mailedCount += count; delivery.mailCount += mail.MessageCount;
            }
            return true;
        }

        private static long CountPendingQuestItems(SqliteConnection connection, SqliteTransaction transaction,
            int characterId, int itemId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Include supplements from other quests too: after claiming, these
            // are fungible inventory items. Deleted/claimed mail is excluded.
            command.CommandText = @"
SELECT COALESCE(SUM(a.item_count),0)
FROM mailbox_attachments a JOIN mailbox_messages m ON m.message_id=a.message_id
WHERE m.receiver_character_id=@cid AND a.item_template_id=@item AND a.claimed_flag=0
  AND substr(m.idempotency_key,1,length(@prefix))=@prefix
  AND EXISTS(SELECT 1 FROM mailbox_recipients r WHERE r.message_id=m.message_id
      AND r.character_id=@cid AND r.folder=0 AND r.deleted_flag=0);";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@prefix", "gm:quest-ready:" + characterId + ":");
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }
}
