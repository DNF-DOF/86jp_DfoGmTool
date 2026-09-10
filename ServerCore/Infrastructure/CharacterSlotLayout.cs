using Microsoft.Data.Sqlite;

namespace DfoGmTool.ServerCore.Infrastructure
{
    internal static class CharacterSlotLayout
    {
        // A21 schema v12 requires the active selection list to have no holes.
        internal static void Normalize(SqliteConnection connection, SqliteTransaction transaction, int? accountId = null)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
WITH slots AS (
    SELECT character_id,
           ROW_NUMBER() OVER (PARTITION BY account_id ORDER BY slot_index, character_id) - 1 AS slot
    FROM characters WHERE delete_flag=0 AND (@aid IS NULL OR account_id=@aid)
)
UPDATE characters SET slot_index=(SELECT slot FROM slots WHERE slots.character_id=characters.character_id)
WHERE character_id IN (SELECT character_id FROM slots);";
            command.Parameters.AddWithValue("@aid", (object)accountId ?? System.DBNull.Value);
            command.ExecuteNonQuery();
        }
    }
}
