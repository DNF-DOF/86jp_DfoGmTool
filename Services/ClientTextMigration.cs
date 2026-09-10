using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using DfoGmTool.ServerCore.Infrastructure;

namespace DfoGmTool.Services
{
    public sealed class ClientTextMigrationRequest
    {
        public string DatabasePath { get; set; }
        public string Fingerprint { get; set; }
        public bool ServerStopped { get; set; }
        public string ConfirmText { get; set; }
        public Dictionary<string, string> AmbiguousEncodings { get; set; } = new();
    }

    public sealed class ClientTextPreview
    {
        public bool Success { get; set; } = true;
        public string Fingerprint { get; set; }
        public List<ClientTextChange> Changes { get; } = new();
        public List<string> Errors { get; } = new();
        public int Unchanged { get; set; }
        public bool Required => Changes.Count != 0 || Errors.Count != 0;
        public bool CanExecute => Changes.Count != 0 && Errors.Count == 0;
    }

    public sealed class ClientTextChange
    {
        public string Table { get; set; }
        public string Column { get; set; }
        public long RowId { get; set; }
        public string Key => $"{Table}.{Column}[{RowId}]";
        public string Utf8Text { get; set; }
        public string GbkText { get; set; }
        public bool Ambiguous { get; set; }
        public string ConversionError { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public object Converted { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public object Original { get; set; }
    }

    // Inspects wire BLOBs only. SQLite TEXT (mail body, subject, friends) stays Unicode.
    public static class ClientTextMigration
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private static Encoding Gbk
        {
            get
            {
                ClientTextEncoding.EnsureInitialized();
                return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            }
        }
        private static SqliteConnection Open(string path, bool write = false)
        {
            if (!File.Exists(path)) throw new InvalidOperationException("数据库文件不存在。");
            var c = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.GetFullPath(path), Mode = write ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                Pooling = false, DefaultTimeout = 5 }.ToString());
            c.Open();
            return c;
        }
        private static string Hash(object value) => (value is byte[] ? "B:" : "T:") + Convert.ToHexString(SHA256.HashData(
            value is byte[] bytes ? bytes : Encoding.UTF8.GetBytes((string)value)));
        private static bool HasColumn(SqliteConnection c, SqliteTransaction tx, string table, string column)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (r.GetString(1) == column) return true;
            return false;
        }
        public static ClientTextPreview Preview(string path)
        {
            using var c = Open(path);
            using var tx = c.BeginTransaction(deferred: true);
            return Scan(c, tx);
        }
        private static ClientTextPreview Scan(SqliteConnection c, SqliteTransaction tx)
        {
            var result = new ClientTextPreview();
            var fingerprint = new StringBuilder();
            var marked = new HashSet<string>();
            if (HasColumn(c, tx, "gm_client_text_gbk", "key"))
            {
                using var m = c.CreateCommand(); m.Transaction = tx;
                m.CommandText = "SELECT key FROM gm_client_text_gbk ORDER BY key";
                using var mr = m.ExecuteReader();
                while (mr.Read()) { marked.Add(mr.GetString(0)); fingerprint.AppendLine(mr.GetString(0)); }
            }
            foreach (var (table, column) in new[] { ("characters", "name"), ("characters", "name_bytes"),
                ("character_creatures", "creature_text"), ("mailbox_attachments", "detail_json") })
            {
                if (!HasColumn(c, tx, table, column)) continue;
                fingerprint.AppendLine(table + "." + column);
                using var cmd = c.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = $"SELECT rowid, \"{column}\" FROM \"{table}\" WHERE \"{column}\" IS NOT NULL ORDER BY rowid";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetInt64(0); var value = r.GetValue(1);
                    var location = $"{table}.{column}[{id}]";
                    if (value is not byte[] && value is not string) { result.Errors.Add(location + ": 类型无效"); continue; }
                    var key = location + ":" + Hash(value);
                    fingerprint.AppendLine(key);
                    if (marked.Contains(key)) { result.Unchanged++; continue; }
                    try
                    {
                        JsonNode root = null; JsonObject creature = null; string jsonKey = null;
                        object raw = value;
                        if (column == "detail_json")
                        {
                            if (string.IsNullOrWhiteSpace((string)value)) { result.Unchanged++; continue; }
                            root = JsonNode.Parse((string)value);
                            creature = (root?["Creature"] ?? root?["creature"]) as JsonObject;
                            jsonKey = creature?.ContainsKey("NameBytes") == true ? "NameBytes" : "nameBytes";
                            if (creature?[jsonKey] == null) { result.Unchanged++; continue; }
                            raw = Convert.FromBase64String(creature[jsonKey].GetValue<string>());
                        }
                        string utf8Text = null, gbkText = null;
                        bool ambiguous = false;
                        if (raw is byte[] bytes)
                        {
                            if (bytes.All(b => b < 128)) { result.Unchanged++; continue; }
                            try { utf8Text = Utf8.GetString(bytes); } catch (DecoderFallbackException) { }
                            try { gbkText = Gbk.GetString(bytes); } catch (DecoderFallbackException) { }
                            if (utf8Text == null)
                            {
                                if (gbkText == null) throw new InvalidOperationException("既不是有效 UTF-8，也不是 GBK");
                                result.Unchanged++; continue;
                            }
                            ambiguous = gbkText != null;
                        }
                        else utf8Text = (string)raw;
                        byte[] converted = null;
                        string conversionError = null;
                        try { converted = Gbk.GetBytes(utf8Text); }
                        catch (EncoderFallbackException ex) when (ambiguous) { conversionError = ex.Message; }
                        object newValue = converted;
                        if (creature != null && converted != null) { creature[jsonKey] = Convert.ToBase64String(converted); newValue = root.ToJsonString(); }
                        result.Changes.Add(new ClientTextChange { Table = table, Column = column, RowId = id,
                            Utf8Text = utf8Text, GbkText = gbkText, Ambiguous = ambiguous, ConversionError = conversionError,
                            Original = value, Converted = newValue });
                    }
                    catch (Exception ex) { result.Errors.Add(location + ": " + ex.Message); }
                }
            }
            result.Fingerprint = Hash(fingerprint.ToString());
            return result;
        }

        public static object Execute(ClientTextMigrationRequest request)
        {
            if (request == null || !request.ServerStopped || request.ConfirmText != "GBK")
                throw new InvalidOperationException("请停止服务端，核对已选择显示正常的文字，并在弹窗中确认处理。");
            using var c = Open(request.DatabasePath, true);
            using var tx = c.BeginTransaction(deferred: false);
            var preview = Scan(c, tx);
            if (preview.Fingerprint != request.Fingerprint) throw new InvalidOperationException("数据库已变化，请重新预览。");
            if (preview.Errors.Count != 0) throw new InvalidOperationException(string.Join("\n", preview.Errors));
            bool KeepGbk(ClientTextChange change) => change.Ambiguous
                && request.AmbiguousEncodings != null && request.AmbiguousEncodings.TryGetValue(change.Key, out var encoding) && encoding == "gbk";
            foreach (var change in preview.Changes.Where(x => x.Ambiguous))
            {
                if (request.AmbiguousEncodings == null || !request.AmbiguousEncodings.TryGetValue(change.Key, out var encoding)
                    || (encoding != "gbk" && encoding != "utf8"))
                    throw new InvalidOperationException(change.Key + "：请逐项确认原始编码。");
                if (!KeepGbk(change) && change.ConversionError != null)
                    throw new InvalidOperationException(change.Key + "：UTF-8 内容无法表示为 GBK，请核对是否原本就是 GBK。");
            }
            if (!preview.Required) return new { success = true, converted = 0, backupPath = (string)null };
            // A separate reader sees the same committed snapshot while BEGIN IMMEDIATE blocks writers.
            var backupPath = Path.GetFullPath(request.DatabasePath) + ".before-gbk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".db";
            using (var source = Open(request.DatabasePath))
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
            { backup.Open(); source.BackupDatabase(backup); }
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS gm_client_text_gbk (key TEXT PRIMARY KEY)"; cmd.ExecuteNonQuery();
            int count = 0;
            foreach (var change in preview.Changes)
            {
                var value = KeepGbk(change) ? change.Original : change.Converted;
                if (!KeepGbk(change))
                {
                    cmd.CommandText = $"UPDATE \"{change.Table}\" SET \"{change.Column}\"=$value WHERE rowid=$id";
                    cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$value", value); cmd.Parameters.AddWithValue("$id", change.RowId);
                    if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("记录数量已变化，已回滚。");
                    count++;
                }
                cmd.CommandText = "INSERT OR IGNORE INTO gm_client_text_gbk(key) VALUES($key)";
                cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$key", $"{change.Table}.{change.Column}[{change.RowId}]:{Hash(value)}"); cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return new { success = true, converted = count, backupPath };
        }
    }
}
