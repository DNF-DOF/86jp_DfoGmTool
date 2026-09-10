using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using DfoGmTool.Services;
using DfoGmTool.ServerCore.Infrastructure;

namespace DfoGmTool.SelfTests
{
    internal static class ClientTextMigrationSelfTest
    {
        public static int Run(string[] args)
        {
            var root = Path.Combine(Path.GetTempPath(), "gm-gbk-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "legacy.db");
                using var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open();
                void Sql(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                void Name(int id, byte[] name) { using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO characters VALUES($id,$name,$name)"; cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$name", name); cmd.ExecuteNonQuery(); }
                void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
                void ConvertPreview(ClientTextPreview p, bool keep = false) => ClientTextMigration.Execute(new ClientTextMigrationRequest { DatabasePath = path, Fingerprint = p.Fingerprint, ServerStopped = true, ConfirmText = "GBK", AmbiguousEncodings = p.Changes.Where(x => x.Ambiguous).ToDictionary(x => x.Key, x => keep ? "gbk" : "utf8") });
                Sql("PRAGMA journal_mode=WAL; PRAGMA user_version=999; CREATE TABLE characters(character_id INTEGER PRIMARY KEY,name BLOB UNIQUE,name_bytes BLOB); CREATE TABLE character_creatures(creature_text BLOB); CREATE TABLE mailbox_attachments(detail_json TEXT); CREATE TABLE mailbox_messages(title TEXT,body TEXT); INSERT INTO mailbox_messages VALUES('中文标题','中文正文');");
                Name(1, Encoding.UTF8.GetBytes("白神")); Name(2, Encoding.ASCII.GetBytes("ASCII"));
                Sql("INSERT INTO character_creatures VALUES(X'E5AEA0E789A9')");
                using (var cmd = c.CreateCommand()) { cmd.CommandText = "INSERT INTO mailbox_attachments VALUES($json)"; cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new { Creature = new { NameBytes = Convert.ToBase64String(Encoding.UTF8.GetBytes("宠物")) } })); cmd.ExecuteNonQuery(); }
                var preview = ClientTextMigration.Preview(path);
                Check(preview.CanExecute && preview.Changes.Count == 4, "结构检测忽略 schema 999，覆盖角色双字段、宠物和附件，ASCII 跳过");
                ConvertPreview(preview);
                Check(!ClientTextMigration.Preview(path).Required, "转换后重复检查无需转换");
                using (var cmd = c.CreateCommand()) { cmd.CommandText = "SELECT hex(name) FROM characters WHERE character_id=1"; Check((string)cmd.ExecuteScalar() == "B0D7C9F1", "UTF8 转换为正确 GBK"); cmd.CommandText = "SELECT title||body FROM mailbox_messages"; Check((string)cmd.ExecuteScalar() == "中文标题中文正文", "邮件 Unicode TEXT 保持不变"); cmd.CommandText = "PRAGMA user_version"; Check((long)cmd.ExecuteScalar() == 999, "不改 schema 版本"); }
                var backups = Directory.GetFiles(root, "*.before-gbk-*.db");
                Check(backups.Length == 1 && ClientTextMigration.Preview(backups[0]).Changes.Count == 4, "自动备份包含 WAL 中的原始数据");
                Name(3, Encoding.UTF8.GetBytes("流心"));
                var stale = ClientTextMigration.Preview(path); Name(4, Encoding.ASCII.GetBytes("new"));
                bool refused = false; try { ConvertPreview(stale); } catch (InvalidOperationException) { refused = true; }
                Check(refused, "拒绝使用过期预览");
                ConvertPreview(ClientTextMigration.Preview(path));
                // Existing GBK and legacy UTF8 spelling collide after conversion; all changes must roll back.
                Name(5, Encoding.UTF8.GetBytes("白神"));
                var collision = ClientTextMigration.Preview(path);
                refused = false; try { ConvertPreview(collision); } catch (SqliteException) { refused = true; }
                Check(refused && ClientTextMigration.Preview(path).Fingerprint == collision.Fingerprint, "唯一键冲突完整回滚");
                Sql("DELETE FROM characters WHERE character_id=5");
                Name(6, new byte[] { 0xC2, 0xA9 });
                var ambiguous = ClientTextMigration.Preview(path);
                Check(ambiguous.Changes.All(x => x.Ambiguous), "双编码有效字节要求确认");
                ConvertPreview(ambiguous, true);
                Check(!ClientTextMigration.Preview(path).Required, "确认保留 GBK 后不重复提示");
                Name(7, new byte[] { 0x81 });
                Check(ClientTextMigration.Preview(path).Errors.Count == 2, "非法编码阻止加载与转换");
                Sql("DELETE FROM characters WHERE character_id=7");
                Name(8, Encoding.UTF8.GetBytes("😀"));
                Check(ClientTextMigration.Preview(path).Errors.Count > 0 || ClientTextMigration.Preview(path).Changes.Any(x => x.ConversionError != null), "不可表示字符禁止有损转换");
                var realIndex = Array.IndexOf(args, "--database-path");
                if (realIndex >= 0)
                {
                    var original = args[realIndex + 1];
                    var copy = Path.Combine(root, "user-copy.db");
                    using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = original, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                    using (var dest = new SqliteConnection($"Data Source={copy};Pooling=False")) { source.Open(); dest.Open(); source.BackupDatabase(dest); }
                    var real = ClientTextMigration.Preview(copy);
                    Console.WriteLine(JsonSerializer.Serialize(real));
                    Check(real.Errors.Count == 0, "用户数据库副本编码可处理");
                    var dummyPvf = Path.Combine(root, "dummy.pvf"); File.WriteAllText(dummyPvf, "not a PVF");
                    Check(GmConfig.TryCreate(copy, dummyPvf, out var config, out _), "创建加载前检查配置");
                    var runtime = new GmRuntimeEnvironment(config);
                    Check(runtime.GetStatus().EncodingRequired && !runtime.GetStatus().Configured
                        && !runtime.GetStatus().Ready, "加载前拦截编码问题，不进入 PVF 或 GM 初始化");
                    ClientTextMigration.Execute(new ClientTextMigrationRequest { DatabasePath = copy, Fingerprint = real.Fingerprint, ServerStopped = true, ConfirmText = "GBK", AmbiguousEncodings = real.Changes.Where(x => x.Ambiguous).ToDictionary(x => x.Key, x => x.Column == "name" ? "gbk" : "utf8") });
                    Check(!ClientTextMigration.Preview(copy).Required, "用户数据库副本转换后重复检查通过");
                    runtime.Configure(copy, dummyPvf);
                    Check(!runtime.GetStatus().EncodingRequired, "转换后重新加载已通过编码检查");
                }
                Console.WriteLine("Artifacts: " + root);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Artifacts: " + root); return 1; }
        }
    }
}
