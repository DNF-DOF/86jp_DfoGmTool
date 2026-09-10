using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DfoGmTool.Services;
using DfoGmTool.ServerCore.Infrastructure;
using DfoGmTool.ServerCore.GameWorld;
using DfoGmTool.ServerCore.Game.Inventory;
using DfoGmTool.ServerCore.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.SelfTests
{
    internal static class QuestItemsSelfTest
    {
        internal static int Run(string[] args)
        {
            var root = Path.Combine(Path.GetTempPath(), "gm-quest-items-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
                JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
                bool Ok(object value) => Json(value).TryGetProperty("success", out var s) && s.GetBoolean();
                var pvf = args[Array.IndexOf(args, "--pvf-path") + 1];
                var db = Path.Combine(root, "test.db");
                SqliteDatabaseBootstrap.CreateTestDatabase(db, Path.Combine(AppContext.BaseDirectory, "ServerCore", "Sqlite", "item_schema.sql"));
                using var c = new SqliteConnection($"Data Source={db};Pooling=False"); c.Open();
                void Sql(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                long Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
                Sql("PRAGMA user_version=999; INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'questitems',''); INSERT INTO characters(character_id,account_id,name,job,level) VALUES(1,1,X'B2E2CAD4',0,60); INSERT INTO character_subtype1_fields(character_id) VALUES(1);");
                Check(GmConfig.TryCreate(db,pvf,out var config,out var error), "config " + error);
                PvfArchiveAccessor.Configure(pvf); PvfRuntimeCache.ResetForPvfChange(); GmService.ResetPvfStaticData();
                var index = new PvfIndexService(pvf); index.WarmInBackground();
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (!index.IsReady && string.IsNullOrWhiteSpace(index.BuildError) && DateTime.UtcNow < deadline) Thread.Sleep(100);
                Check(index.IsReady, "PVF ready " + index.BuildError);
                var gm = new GmService(config,index);
                var inventory = new NewInventoryStore(db, config.SchemaPath);
                var candidates = index.AllQuestMeta.Values.Where(x=>x.Type=="seeking" && x.Id>0 && x.Id<30000).ToArray();
                Check(candidates.Length>0,"actual PVF seeking metadata loaded");
                var itemId = candidates.SelectMany(x => GmService.TryParseQuestItems(x,out var req,out _) ? req.Keys.ToArray() : Array.Empty<int>())
                    .Distinct().First(id => { var x=ItemMetadataResolver.Resolve(id); return x!=null && x.ItemKind=="stackable"
                        && x.StackLimit>20 && x.StackLimit<1000000 && ItemMetadataResolver.ResolvePvfTypeTag(x)=="material"; });
                var item = ItemMetadataResolver.Resolve(itemId);
                var meta = candidates[0];
                meta.Job="[all]"; meta.TargetCharacter=""; meta.GrowType=-1; meta.MinLevel=1; meta.MaxLevel=99;
                meta.PreRequired=Array.Empty<int>(); meta.PreGroups=Array.Empty<int[]>(); meta.PreRequiredQuestAnswer=Array.Empty<int>();
                meta.CollisionQuest=Array.Empty<int>(); meta.Grade="daily"; meta.IntData=$"{itemId} 4 {itemId} 6";
                Check(GmService.TryParseQuestItems(meta,out var parsed,out _) && parsed[itemId]==10,"duplicate requirements summed");
                string Activate()
                {
                    Sql("DELETE FROM character_active_quests WHERE character_id=1");
                    using var tx=c.BeginTransaction();
                    var activation=QuestRepository.InsertActiveQuest(c,tx,1,0,(ushort)meta.Id,123).ToString(); tx.Commit(); return activation;
                }
                long Held() { using var tx=c.BeginTransaction(); var n=inventory.CountQuestItem(c,tx,1,1,itemId); tx.Commit(); return n; }
                var activation=Activate();
                Check(inventory.TryGrant(1,1,0,itemId,4,new ItemGrantOptions()).Success,"seed partial holdings");
                var ready=gm.MarkQuestReady(1,meta.Id,activation);
                Check(Ok(ready) && Held()==10 && Json(ready).GetProperty("itemDelivery").GetProperty("inventoryCount").GetInt64()==6,"ready supplements only shortage");
                Check(Ok(gm.MarkQuestReady(1,meta.Id,activation)) && Held()==10,"repeat ready no extra inventory");
                meta.IntData=$"{itemId} 3";
                var sufficientAudits = Scalar("SELECT count(*) FROM inventory_audit_log");
                Check(Ok(gm.MarkQuestReady(1,meta.Id,activation)) && Held()==10,
                    "ready never reduces holdings above requirements");
                Check(Ok(gm.MarkVisibleDailyQuestReady(1,meta.Id,index)) && Held()==10,
                    "daily ready never reduces holdings above requirements");
                Sql("DELETE FROM character_active_quests WHERE character_id=1");
                Check(Ok(gm.MarkVisibleDailyQuestReady(1,meta.Id,index)) && Held()==10
                    && Scalar("SELECT count(*) FROM inventory_audit_log")==sufficientAudits
                    && Scalar("SELECT count(*) FROM mailbox_messages")==0,
                    "accept-and-ready leaves surplus items untouched without grants or mail");
                meta.IntData=$"{itemId} 20"; var replacement=Activate();
                Check(!Ok(gm.MarkQuestReady(1,meta.Id,activation)) && Held()==10,"stale activation cannot grant");
                meta.IntData=$"{itemId} 20 123";
                Check(!Ok(gm.MarkQuestReady(1,meta.Id,replacement)) && Held()==10 && Scalar("SELECT trigger_value FROM character_active_quests")==123,"malformed list leaves state unchanged");
                meta.Type="hunt monster";
                Check(Ok(gm.MarkQuestReady(1,meta.Id,replacement)) && Held()==10,"non-item objective does not parse IntData as items");
                meta.Type="seeking"; meta.IntData="0 100 3037 12 10100115 8";
                Check(Ok(gm.MarkQuestReady(1,meta.Id,replacement)),"virtual gold and account materials supported");
                Check(Scalar("SELECT cube_clear FROM accounts WHERE account_id=1")==12 && Scalar("SELECT soul_10100115 FROM accounts WHERE account_id=1")==8,"account materials stored at authoritative owner");

                // Fill all material slots and leave one unit of stack room.
                Sql("DELETE FROM character_inventory_items WHERE character_id=1 AND slot_index>2");
                using(var tx=c.BeginTransaction())
                {
                    Check(NewInventoryStore.TryGetCharacterOpenRange(c,tx,1,ItemCore.KindMaterial,out var list,out var start,out var end,out error),"material open range");
                    for(short slot=start;slot<=end;slot++)
                    {
                        using var cmd=c.CreateCommand();cmd.Transaction=tx;
                        cmd.CommandText="INSERT INTO character_inventory_items(character_id,list_type,slot_index,item_core) VALUES(1,@list,@slot,@core)";
                        cmd.Parameters.AddWithValue("@list",(int)list);cmd.Parameters.AddWithValue("@slot",slot);
                        cmd.Parameters.AddWithValue("@core",new ItemCore { ItemKind=ItemCore.KindMaterial,ItemId=itemId,Count=slot==start?item.StackLimit-1:item.StackLimit }.ToBytes());cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                var fullHeld=Held(); meta.IntData=$"{itemId} {fullHeld+3}";
                replacement=Activate();
                var auditBefore=Scalar("SELECT count(*) FROM inventory_audit_log");
                ready=gm.MarkQuestReady(1,meta.Id,replacement);
                Check(Ok(ready),"full bag ready " + Json(ready));
                Check(Held()==fullHeld,"partial stack merge rolled back before mailing");
                Check(Scalar("SELECT sum(item_count) FROM mailbox_attachments")==3,"mail contains full shortage once");
                Check(Scalar("SELECT count(*) FROM inventory_audit_log")==auditBefore,"partial stack audit rolled back");
                using(var cmd=c.CreateCommand()) { cmd.CommandText="SELECT receiver_name FROM mailbox_messages LIMIT 1";Check((string)cmd.ExecuteScalar()=="测试","mail recipient GBK decoded correctly"); }
                Check(Ok(gm.MarkQuestReady(1,meta.Id,replacement)) && Scalar("SELECT count(*) FROM mailbox_messages")==1,"repeat ready recognizes pending mail");
                Check(Ok(gm.MarkVisibleDailyQuestReady(1,meta.Id,index)) && Scalar("SELECT count(*) FROM mailbox_messages")==1,"daily ready shares pending mail detection");
                Sql("DELETE FROM character_active_quests WHERE character_id=1");
                Check(Ok(gm.MarkVisibleDailyQuestReady(1,meta.Id,index)) && Scalar("SELECT count(*) FROM character_active_quests")==1 && Scalar("SELECT count(*) FROM mailbox_messages")==1,"accept-and-ready shares supplement path");
                Sql("UPDATE mailbox_attachments SET claimed_flag=1");
                Check(Ok(gm.MarkVisibleDailyQuestReady(1,meta.Id,index)) && Scalar("SELECT count(*) FROM mailbox_messages")==2,"claimed mail no longer covers missing inventory");
                Sql("UPDATE mailbox_attachments SET claimed_flag=1; CREATE TRIGGER reject_quest_mail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'test rollback'); END; UPDATE character_active_quests SET trigger_value=123;");
                Check(!Ok(gm.MarkVisibleDailyQuestReady(1,meta.Id,index)) && Held()==fullHeld && Scalar("SELECT trigger_value FROM character_active_quests")==123,"mail failure rolls back daily state and all inventory changes");
                Sql("DROP TRIGGER reject_quest_mail; DELETE FROM character_inventory_items WHERE slot_index>2; CREATE TRIGGER reject_quest_update BEFORE UPDATE ON character_active_quests BEGIN SELECT RAISE(ABORT,'test rollback'); END;");
                meta.IntData=$"{itemId} 2";
                using(var cmd=c.CreateCommand()) {cmd.CommandText="SELECT activation_id FROM character_active_quests"; replacement=(string)cmd.ExecuteScalar();}
                bool failed=false;try {gm.MarkQuestReady(1,meta.Id,replacement);}catch(SqliteException){failed=true;}
                Check(failed && Held()==0,"quest CAS failure rolls back successful grant");
                Check(Scalar("PRAGMA user_version")==999,"no schema version gate or migration");
                Console.WriteLine("QuestItemsSelfTest OK " + root);return 0;
            }
            catch(Exception ex) {Console.Error.WriteLine(ex);Console.Error.WriteLine(root);return 1;}
        }
    }
}
