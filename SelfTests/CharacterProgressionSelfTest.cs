using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DfoGmTool.Services;
using DfoGmTool.ServerCore.Infrastructure;
using DfoGmTool.ServerCore.GameWorld;
using DfoGmTool.ServerCore.Game.Inventory;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.SelfTests
{
    internal static class CharacterProgressionSelfTest
    {
        internal static int Run(string[] args)
        {
            var root = Path.Combine(Path.GetTempPath(), "gm-progression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var pvfIndexArg = Array.IndexOf(args, "--pvf-path");
                if (pvfIndexArg < 0) throw new ArgumentException("测试需要 --pvf-path");
                var pvf = args[pvfIndexArg + 1];
                var db = Path.Combine(root, "test.db");
                SqliteDatabaseBootstrap.CreateTestDatabase(db, Path.Combine(AppContext.BaseDirectory, "ServerCore", "Sqlite", "item_schema.sql"));
                using var c = new SqliteConnection($"Data Source={db};Pooling=False"); c.Open();
                void Sql(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                long Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
                void Check(bool condition, string text) { if (!condition) throw new Exception(text); Console.WriteLine("PASS " + text); }
                JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
                bool Ok(object result) { var j = Json(result); return j.TryGetProperty("success",out var s) && s.GetBoolean(); }
                Sql("PRAGMA user_version=999; INSERT INTO accounts(account_id,m_id,password_hash) VALUES(1,'progression',''); INSERT INTO characters(character_id,account_id,name,job,level) VALUES(1,1,X'54455354',0,60); INSERT INTO character_subtype1_fields(character_id) VALUES(1); INSERT INTO character_skills(character_id,page_index,slot,skill_id,level) VALUES(1,0,5,999,1),(1,1,5,999,1); INSERT INTO character_pvp_skills(character_id,page_index,slot,skill_id,level) VALUES(1,0,5,777,1);");
                Check(GmConfig.TryCreate(db,pvf,out var config,out var error), "create config " + error);
                PvfArchiveAccessor.Configure(pvf); PvfRuntimeCache.ResetForPvfChange(); GmService.ResetPvfStaticData();
                var index = new PvfIndexService(pvf); index.WarmInBackground();
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (!index.IsReady && string.IsNullOrWhiteSpace(index.BuildError) && DateTime.UtcNow < deadline) Thread.Sleep(100);
                Check(index.IsReady, "PVF index ready " + index.BuildError);
                var gm = new GmService(config,index);
                var duelCatalog = new CharacterDuelCatalog(pvf);
                Check(duelCatalog.Grades.Count == 21 && duelCatalog.Grades[0] == "入门"
                    && duelCatalog.Grades[1] == "青铜1星" && duelCatalog.Grades[20] == "泰拉石4星",
                    "PVF grade IDs map to current client grade names");
                var duelOptions = Json(gm.GetCharacterProgression(1)).GetProperty("duel");
                Check(duelOptions.GetProperty("gradeName").GetString() == "入门"
                    && duelOptions.GetProperty("gradeOptions").GetArrayLength() == 21, "API supplies named grade choices");
                var catalog = new CharacterProfessionCatalog(pvf);
                var allIds = catalog.Jobs.SelectMany(x=>x.ResetQuestIds).Distinct().ToArray();
                object Set(int type,int level,long? exp=null)
                {
                    var current = Json(gm.GetCharacterProgression(1)).GetProperty("profession");
                    return gm.SetCharacterProfession(1,new ProfessionRequest { Type=type,Level=level,Experience=exp,
                        ExpectedType=current.GetProperty("type").GetInt32(),ExpectedExperience=current.GetProperty("experience").GetInt64(),CharacterOffline=true });
                }
                foreach (var job in catalog.Jobs)
                {
                    var result = Set(job.Type, job.MaxLevel);
                    Check(Ok(result), "switch profession " + job.Name + " " + Json(result));
                    Check(Scalar("SELECT expert_job_exp FROM character_subtype0_fields WHERE character_id=1") == job.ExperienceForLevel(job.MaxLevel), "profession max level writes exact experience");
                    var ids=string.Join(",",job.QuestIds);
                    Check(Scalar($"SELECT count(*) FROM character_quest_completions WHERE character_id=1 AND quest_id IN ({ids})") == job.QuestIds.Length,"selected quest chain complete");
                    var other = allIds.Except(job.QuestIds).ToArray();
                    Check(Scalar($"SELECT count(*) FROM character_quest_completions WHERE character_id=1 AND quest_id IN ({string.Join(",",other)})") == 0,"alternative completions released");
                    Check(other.All(id=>(index.GetQuestMeta(id).CollisionQuest??Array.Empty<int>()).Intersect(job.QuestIds).Any()),"all alternate quests hidden by native collision rules");
                    Check(Scalar("SELECT count(*) FROM character_skills WHERE character_id=1 AND skill_id=999")==2,"unrelated skills preserved");
                    Check(Scalar("SELECT count(*) FROM character_pvp_skills WHERE character_id=1 AND skill_id=777")==1,"PvP skills untouched");
                    foreach(var skill in job.Skills.Keys) Check(Scalar($"SELECT count(*) FROM character_skills WHERE character_id=1 AND skill_id={skill}")==2,"profession skill on both pages");
                    Sql("INSERT OR IGNORE INTO character_expert_job_recipes(character_id,recipe_id) VALUES(1,999999)");
                    Check(Ok(Set(job.Type,1)),"downgrade profession to Lv1");
                    Check(Scalar("SELECT count(*) FROM character_expert_job_recipes WHERE character_id=1 AND recipe_id=999999")==1,"same profession preserves manual recipes");
                    Check(Ok(Set(job.Type,1,job.Thresholds[0])),"edit experience directly");
                    Check(Json(gm.GetCharacterProgression(1)).GetProperty("profession").GetProperty("level").GetInt32()==2,"exact experience boundary maps to Lv2");
                }
                var stale = gm.SetCharacterProfession(1,new ProfessionRequest { Type=1,Level=1,ExpectedType=0,CharacterOffline=true });
                Check(Json(gm.GetCharacterProgression(1)).GetProperty("abilities").ValueKind==JsonValueKind.Null,
                    "other professions have no manual machine upgrade");
                Check(Ok(Set(3,11)),"prepare disjointer machine editing");
                JsonElement Machine() => Json(gm.GetCharacterProgression(1)).GetProperty("abilities");
                ProfessionAbilityRequest MachineRequest(int level) => new ProfessionAbilityRequest {
                    Level=level,ExpectedType=3,ExpectedState=Machine().GetProperty("State").GetString(),CharacterOffline=true};
                Check(Machine().GetProperty("Levels").EnumerateArray().Max(x=>x.GetInt32())==10,
                    "machine choices respect character level 60");
                Check(!Ok(gm.SetProfessionAbility(1,MachineRequest(11))),"machine Lv11 rejects character below Lv70");
                Sql("UPDATE characters SET level=70 WHERE character_id=1");
                var machineRequest=MachineRequest(11);
                Check(Ok(gm.SetProfessionAbility(1,machineRequest)),"machine Lv11 accepted with profession and character requirements");
                Check(Scalar("SELECT disjoint_machine_grade FROM character_expert_job WHERE character_id=1")==11
                    && Scalar("SELECT disjoint_machine_endurance FROM character_expert_job WHERE character_id=1")==700,
                    "machine writes independent grade and PVF maximum endurance");
                Check(Scalar("SELECT count(*) FROM character_skills WHERE character_id=1 AND skill_id=194 AND level=1")==2,
                    "machine grade leaves skill-entry levels unchanged");
                Check(!Ok(gm.SetProfessionAbility(1,machineRequest)),"stale machine state rejected");
                Sql("UPDATE character_expert_job SET disjoint_machine_endurance=123 WHERE character_id=1");
                Check(Ok(gm.SetProfessionAbility(1,MachineRequest(11)))
                    && Scalar("SELECT disjoint_machine_endurance FROM character_expert_job WHERE character_id=1")==123,
                    "same-grade save preserves durability");
                var unconfirmed=MachineRequest(10);unconfirmed.CharacterOffline=false;
                Check(!Ok(gm.SetProfessionAbility(1,unconfirmed)),"machine requires offline confirmation");
                Check(!Ok(gm.SetProfessionAbility(1,MachineRequest(12))),"machine rejects grade outside PVF range");
                Sql("CREATE TRIGGER reject_machine_update BEFORE UPDATE ON character_expert_job BEGIN SELECT RAISE(ABORT,'test rollback'); END;");
                bool machineFailed=false;try {gm.SetProfessionAbility(1,MachineRequest(10));}catch(SqliteException){machineFailed=true;}
                Check(machineFailed && Scalar("SELECT disjoint_machine_grade FROM character_expert_job WHERE character_id=1")==11
                    && Scalar("SELECT disjoint_machine_endurance FROM character_expert_job WHERE character_id=1")==123,
                    "failed machine update rolls back grade and durability");
                Sql("DROP TRIGGER reject_machine_update");
                Check(Ok(gm.SetProfessionAbility(1,MachineRequest(1)))
                    && Scalar("SELECT disjoint_machine_endurance FROM character_expert_job WHERE character_id=1")==300,
                    "machine downgrade uses new grade durability");
                Check(Ok(Set(3,1)),"profession downgrade remains independent");
                Check(!Ok(gm.SetProfessionAbility(1,MachineRequest(2))),"machine cannot exceed profession level");
                var oldMachine=MachineRequest(1);
                Check(Ok(Set(4,1)) && !Ok(gm.SetProfessionAbility(1,oldMachine)),"profession switch invalidates machine edit");
                Check(!Ok(stale),"stale profession request rejected");
                Check(!Ok(gm.SetCharacterProfession(1,new ProfessionRequest { Type=1,Level=1 })),"offline acknowledgement required");
                var before=Json(gm.GetCharacterProgression(1)).GetProperty("profession").GetRawText();
                Sql("CREATE TRIGGER reject_profession_recipe BEFORE INSERT ON character_expert_job_recipes BEGIN SELECT RAISE(ABORT,'test rollback'); END");
                bool failed=false; try { Set(1,11); } catch(SqliteException) { failed=true; }
                Check(failed && Json(gm.GetCharacterProgression(1)).GetProperty("profession").GetRawText()==before,"profession transaction fully rolls back on write failure");
                Sql("DROP TRIGGER reject_profession_recipe");
                Check(Ok(Set(0,0)),"clear profession");
                Check(Scalar("SELECT count(*) FROM character_skills WHERE skill_id IN (191,192,193,194)")==0,"clear removes profession skills");
                Check(Scalar($"SELECT count(*) FROM character_quest_completions WHERE quest_id IN ({string.Join(",",allIds)})")==0,"clear profession releases all branches");
                Check(Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { Grade=20,CharacterOffline=true })),"set grade independently");
                Check(Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { RatingGrade=8,CharacterOffline=true })),"set rating independently");
                Check(Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { WinPoints=123456,CharacterOffline=true })),"set win points");
                var state=Json(gm.GetCharacterProgression(1)).GetProperty("duel");
                Check(state.GetProperty("grade").GetInt32()==20 && state.GetProperty("ratingGrade").GetInt32()==8 && state.GetProperty("winPoints").GetInt32()==123456,"duel changes do not overwrite one another");
                Check(Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { WinPoints=0,CharacterOffline=true })),"win points can be zero");
                Check(Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { WinPoints=999999,CharacterOffline=true })),"win points maximum accepted");
                Check(!Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { WinPoints=1000000,CharacterOffline=true }))
                    && Json(gm.GetCharacterProgression(1)).GetProperty("duel").GetProperty("winPoints").GetInt32()==999999,
                    "over-limit points rejected without writing");
                Check(!Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { Grade=21,CharacterOffline=true })),"reject grade absent from PVF grade point table");
                Check(Json(gm.GetCharacterProgression(1)).GetProperty("duel").GetProperty("gradeName").GetString()=="泰拉石4星",
                    "saved grade resolves to name");
                Check(!Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { Grade=256,CharacterOffline=true })),"reject invalid grade");
                Check(!Ok(gm.SetCharacterDuelProgress(1,new DuelProgressRequest { WinPoints=-1,CharacterOffline=true })),"reject negative points");
                Check(Ok(gm.SetWalletValue(1,"winPoints",999999)),"currency page saves victory points");
                Check(!Ok(gm.SetWalletValue(1,"winPoints",1000000)) && !Ok(gm.SetWalletValue(1,"sp",1000000)),
                    "currency page and legacy alias enforce victory point cap");
                var currencyRows=Json(gm.ListItems(1,index)).GetProperty("items").EnumerateArray()
                    .Where(x=>x.GetProperty("category").GetString()=="货币").ToArray();
                Check(currencyRows.Single(x=>x.GetProperty("slot").GetInt32()==0).GetProperty("name").GetString()=="金币"
                    && currencyRows.Single(x=>x.GetProperty("slot").GetInt32()==2).GetProperty("name").GetString()=="胜点",
                    "currency names include virtual gold and victory points");
                Sql("INSERT INTO characters(character_id,account_id,name,job,level) VALUES(2,1,X'4F54484552',0,60); INSERT INTO united_friend_relations(owner_name,friend_name) VALUES('TEST','Friend'),('Friend','TEST');");
                object Rename(string expected,string name,bool offline=true) => gm.RenameCharacter(1,
                    new CharacterRenameRequest {ExpectedName=expected,NewName=name,CharacterOffline=offline});
                Check(!Ok(Rename("TEST","中文角色名字",false)),"rename requires offline confirmation");
                Check(!Ok(Rename("TEST","中文角色名字多")),"rename rejects seven Chinese characters");
                Check(!Ok(Rename("TEST","Abc123Def4567")),"rename rejects thirteen ASCII characters");
                Check(!Ok(Rename("TEST","OTHER")),"rename rejects duplicate GBK name");
                Check(Ok(Rename("TEST","中文角色名字")),"rename accepts six Chinese characters");
                using(var cmd=c.CreateCommand())
                {
                    cmd.CommandText="SELECT name,name_bytes FROM characters WHERE character_id=1";
                    using var reader=cmd.ExecuteReader();reader.Read();
                    Check(((byte[])reader.GetValue(0)).SequenceEqual(ClientTextEncoding.GetBytes("中文角色名字"))
                        && ((byte[])reader.GetValue(1)).SequenceEqual(ClientTextEncoding.GetBytes("中文角色名字")),
                        "rename updates both GBK name fields");
                }
                Check(Scalar("SELECT count(*) FROM united_friend_relations WHERE owner_name='中文角色名字' OR friend_name='中文角色名字'")==2,
                    "rename preserves both friendship directions");
                Check(!Ok(Rename("TEST","Abc123Def456")),"stale sidebar rename rejected");
                Sql("CREATE TRIGGER reject_friend_rename BEFORE UPDATE ON united_friend_relations BEGIN SELECT RAISE(ABORT,'test rollback'); END;");
                Check(!Ok(Rename("中文角色名字","Abc123Def456"))
                    && Scalar("SELECT count(*) FROM characters WHERE character_id=1 AND name=name_bytes")==1,
                    "friend write failure rejects rename");
                Sql("DROP TRIGGER reject_friend_rename");
                Check(Ok(Rename("中文角色名字","Abc123Def456")),"rollback preserved previous name; twelve ASCII rename accepted");
                Check(Scalar("PRAGMA user_version")==999,"schema version remains diagnostic only");
                Console.WriteLine("CharacterProgressionSelfTest OK " + root); return 0;
            }
            catch(Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine(root); return 1; }
        }
    }
}
