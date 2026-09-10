using System;
using DfoGmTool.ServerCore.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed class CharacterRenameRequest
    {
        public string NewName { get; set; }
        public string ExpectedName { get; set; }
        public bool CharacterOffline { get; set; }
    }

    public sealed partial class GmService
    {
        public object RenameCharacter(int characterId, CharacterRenameRequest request)
        {
            if (request == null || !request.CharacterOffline) return Error("请确认当前角色不在线");
            var name = (request.NewName ?? "").Trim();
            var invalid = ValidateCharacterName(name);
            if (invalid != null) return Error(invalid);
            using var connection = new SqliteConnection(_config.ConnectionString); connection.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            string previous;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT name FROM characters WHERE character_id=@id AND delete_flag=0";
                command.Parameters.AddWithValue("@id", characterId);
                var raw = command.ExecuteScalar();
                if (raw == null || raw == DBNull.Value) return Error("角色不存在或已删除");
                previous = raw is byte[] bytes ? ClientTextEncoding.GetString(bytes) : (string)raw;
            }
            if (!string.Equals(previous, request.ExpectedName, StringComparison.Ordinal))
                return Error("角色名字已变化，请刷新角色列表后重试");
            if (previous == name) return new { success = true, characterId, name };
            if (CharacterNameExists(connection, transaction, name)) return Error("角色名已存在：" + name);
            try
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "UPDATE characters SET name=@bytes,name_bytes=@bytes WHERE character_id=@id AND delete_flag=0";
                command.Parameters.AddWithValue("@bytes", ClientTextEncoding.GetBytes(name));
                command.Parameters.AddWithValue("@id", characterId);
                if (command.ExecuteNonQuery() != 1) return Error("角色不存在或已删除");
                // The friend graph uses names as keys. Preserve both directions
                // in the same transaction; historical mail/audit names are snapshots.
                if (TableExists(connection, transaction, "united_friend_relations"))
                {
                    command.Parameters.Clear();
                    command.CommandText = @"UPDATE united_friend_relations SET
owner_name=CASE WHEN owner_name=@old THEN @new ELSE owner_name END,
friend_name=CASE WHEN friend_name=@old THEN @new ELSE friend_name END
WHERE owner_name=@old OR friend_name=@old";
                    command.Parameters.AddWithValue("@old", previous);
                    command.Parameters.AddWithValue("@new", name);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                return new { success = true, characterId, name };
            }
            catch (SqliteException ex)
            {
                return Error("改名失败，修改已回滚；请检查重名或好友关系冲突：" + ex.Message);
            }
        }
    }
}
