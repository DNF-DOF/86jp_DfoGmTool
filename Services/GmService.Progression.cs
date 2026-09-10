using System;
using System.Collections.Generic;
using System.Linq;
using DfoGmTool.ServerCore.Game.Quests;
using DfoGmTool.ServerCore.Game.CharacterData;
using DfoGmTool.ServerCore.Game.Skills;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed class ProfessionRequest
    {
        public int Type { get; set; }
        public int Level { get; set; }
        public long? Experience { get; set; }
        public int ExpectedType { get; set; }
        public long ExpectedExperience { get; set; }
        public bool CharacterOffline { get; set; }
    }
    public sealed class DuelProgressRequest
    {
        public int? Grade { get; set; }
        public int? RatingGrade { get; set; }
        public int? WinPoints { get; set; }
        public bool CharacterOffline { get; set; }
    }

    public sealed partial class GmService
    {
        private Lazy<CharacterProfessionCatalog> _professionCatalog;
        private Lazy<CharacterDuelCatalog> _duelCatalog;
        private CharacterProfessionCatalog Professions => _professionCatalog.Value;

        public object GetCharacterProgression(int characterId)
        {
            using var c = new SqliteConnection(_config.ConnectionString); c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT c.pvp_grade,c.pvp_rating_grade,COALESCE(f.expert_job_type,0),COALESCE(f.expert_job_exp,0)
FROM characters c LEFT JOIN character_subtype0_fields f ON f.character_id=c.character_id
WHERE c.character_id=$id AND c.delete_flag=0";
            cmd.Parameters.AddWithValue("$id", characterId);
            int grade, rating, type; long exp;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return Error("角色不存在");
                grade = r.GetInt32(0); rating = r.GetInt32(1); type = r.GetInt32(2); exp = r.GetInt64(3);
            }
            object profession = null; string professionError = null;
            try
            {
                var job = Professions.Jobs.FirstOrDefault(x => x.Type == type);
                profession = new { type, experience = exp, level = job?.GetLevel(exp) ?? 0,
                    options = Professions.Jobs.Select(x => new { x.Type, x.Name, x.MaxLevel,
                        levels = Enumerable.Range(1, x.MaxLevel).Select(l => new { level = l, experience = x.ExperienceForLevel(l) }),
                        quests = x.QuestIds.Select(id => new { id, name = _pvfIndex.ResolveQuestName(id) }) }) };
            }
            catch (Exception ex) { professionError = ex.Message; }
            object[] gradeOptions = Array.Empty<object>(); string gradeName = null; string duelError = null;
            try
            {
                var grades = _duelCatalog.Value.Grades;
                gradeOptions = grades.Select(x => (object)new { grade = x.Key, name = x.Value }).ToArray();
                grades.TryGetValue(grade, out gradeName);
            }
            catch (Exception ex) { duelError = ex.Message; }
            object abilities = null; string abilityError = null;
            try { abilities = LoadProfessionAbilities(c, null, characterId); }
            catch (Exception ex) { abilityError = ex.Message; }
            return new { success = true, characterId, profession, professionError, abilities, abilityError, duel = new { grade, ratingGrade = rating,
                gradeName, gradeOptions, error = duelError,
                winPoints = _inventory.LoadWallet(characterId).Sp, experienceSupported = false } };
        }

        public object SetCharacterProfession(int characterId, ProfessionRequest request)
        {
            if (request == null || !request.CharacterOffline) return Error("请先让角色退出游戏或回到选角界面");
            if (request.Type < 0 || request.Type > 4) return Error("副职业类型无效");
            var jobs = Professions.Jobs;
            var selected = jobs.FirstOrDefault(x => x.Type == request.Type);
            long experience = selected == null ? 0 : request.Experience ?? selected.ExperienceForLevel(request.Level);
            if (experience < 0 || experience > uint.MaxValue) return Error("副职业经验必须在 0–4294967295 之间");
            var level = selected?.GetLevel(experience) ?? 0;
            var selectedQuests = new HashSet<int>(selected?.QuestIds ?? Array.Empty<int>());
            var allQuests = jobs.SelectMany(x => x.ResetQuestIds).Distinct().ToArray();
            // A completion on the selected branch must hide every alternative using the native collision rules.
            foreach (var id in allQuests)
            {
                var meta = _pvfIndex.GetQuestMeta(id);
                if (meta == null) return Error("PVF 缺少副职业关联任务 " + id);
                if (selected != null && !selectedQuests.Contains(id)
                    && !(meta.CollisionQuest ?? Array.Empty<int>()).Any(selectedQuests.Contains))
                    return Error($"PVF 任务 {id} 缺少与所选副职业互斥的条件，无法保证其他副职业任务不可见");
            }
            using var c = new SqliteConnection(_config.ConnectionString); c.Open();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            void Run(string sql, params (string, object)[] args)
            {
                cmd.CommandText = sql; cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id", characterId);
                foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
                cmd.ExecuteNonQuery();
            }
            cmd.CommandText = @"SELECT c.job,c.level,c.grow_type,c.bonus_sp,c.bonus_tp,COALESCE(f.expert_job_type,0),COALESCE(f.expert_job_exp,0)
FROM characters c LEFT JOIN character_subtype0_fields f ON c.character_id=f.character_id WHERE c.character_id=$id AND c.delete_flag=0";
            cmd.Parameters.AddWithValue("$id", characterId);
            int oldType, characterJob, characterLevel, grow, bonusSp, bonusTp; long oldExperience;
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read()) return Error("角色不存在");
                characterJob = r.GetInt32(0); characterLevel = r.GetInt32(1); grow = r.GetInt32(2);
                bonusSp = r.GetInt32(3); bonusTp = r.GetInt32(4); oldType = r.GetInt32(5); oldExperience = r.GetInt64(6);
            }
            if (oldType != request.ExpectedType || oldExperience != request.ExpectedExperience)
                return Error("副职业状态已变化，请刷新后重试");
            Run(@"INSERT INTO character_subtype0_fields(character_id,expert_job_type,expert_job_exp) VALUES($id,$type,$exp)
ON CONFLICT(character_id) DO UPDATE SET expert_job_type=excluded.expert_job_type,expert_job_exp=excluded.expert_job_exp",
                ("$type", request.Type), ("$exp", experience));
            Run("INSERT OR IGNORE INTO character_init_flags(character_id) VALUES($id)");
            QuestRepository.ResetQuestProgress(c, tx, characterId, allQuests.Select(x => (ushort)x).ToArray());
            foreach (var id in selectedQuests) QuestRepository.MarkQuestCleared(c, tx, characterId, (ushort)id, 1);

            var switching = oldType != request.Type;
            Run(@"INSERT OR IGNORE INTO character_expert_job(character_id,disjoint_machine_grade,disjoint_machine_endurance,enchanter_endurance)
VALUES($id,$grade,$de,$ee)", ("$grade", request.Type == 3 ? 1 : 0),
                ("$de", request.Type == 3 ? selected.InitialEndurance : 0), ("$ee", request.Type == 1 ? selected.InitialEndurance : 0));
            if (switching)
            {
                Run(@"UPDATE character_expert_job SET disjoint_machine_grade=$grade,disjoint_machine_endurance=$de,
enchanter_endurance=$ee,updated_at=CURRENT_TIMESTAMP WHERE character_id=$id", ("$grade", request.Type == 3 ? 1 : 0),
                    ("$de", request.Type == 3 ? selected.InitialEndurance : 0), ("$ee", request.Type == 1 ? selected.InitialEndurance : 0));
                Run("DELETE FROM character_expert_job_recipes WHERE character_id=$id");
            }
            else if (selected != null)
            {
                // Reconcile only automatic recipes; keep manually learned designs on level edits.
                foreach (var recipe in selected.AutoRecipes.Values.Distinct())
                    Run("DELETE FROM character_expert_job_recipes WHERE character_id=$id AND recipe_id=$recipe", ("$recipe", recipe));
            }
            foreach (var recipe in selected?.AutoRecipes.Where(x => x.Key <= level).Select(x => x.Value).Distinct() ?? Enumerable.Empty<int>())
                Run("INSERT OR IGNORE INTO character_expert_job_recipes(character_id,recipe_id) VALUES($id,$recipe)", ("$recipe", recipe));

            var repository = SqliteCharacterProgressRepository.FromConnectionString(c.ConnectionString);
            var snapshot = repository.LoadSkills(c, tx, characterId);
            var professionSkills = jobs.SelectMany(x => x.Skills.Keys).ToHashSet();
            foreach (var page in snapshot.Pages) page.Entries.RemoveAll(x => professionSkills.Contains(x.SkillId));
            if (selected != null)
                CharacterSkillProfile.MergeGrants(snapshot, selected.Skills.Select(x => new CharacterSkillProfile.SkillGrant {
                    SkillIndex = (ushort)x.Key, Level = (byte)Math.Max(1,x.Value) }).ToArray(), (byte)characterJob, (byte)characterLevel);
            if (selected != null && snapshot.Pages.Any(page => selected.Skills.Keys.Any(id => !page.Entries.Any(x => x.SkillId == id))))
                return Error("技能栏没有足够空位，副职业修改已回滚");
            DfoGmTool.ServerCore.Game.Characters.CharacterStatComputer.DecodeGrowType((byte)grow, out int first, out int second);
            var points = SkillStateService.ResolvePointState(snapshot, (byte)characterJob, (byte)characterLevel, bonusSp, bonusTp, first, second);
            SkillStateService.ApplyProtocolMirrors(snapshot, points);
            repository.SaveSkillProgress(c, tx, characterId, snapshot, points);
            tx.Commit();
            return new { success = true, characterId, type = request.Type, level, experience,
                completedQuestIds = selectedQuests.ToArray(), releasedQuestIds = allQuests.Except(selectedQuests).ToArray() };
        }

        public object SetCharacterDuelProgress(int characterId, DuelProgressRequest request)
        {
            if (request == null || !request.CharacterOffline) return Error("请先让角色退出游戏或回到选角界面");
            if (request.WinPoints is < 0 or > 999999)
                return Error("胜点必须为 0–999999 之间的整数");
            if (request.Grade is < 0 or > 255 || request.RatingGrade is < 0 or > 255)
                return Error("决斗等级或段位无效");
            if (request.Grade.HasValue)
            {
                try
                {
                    if (!_duelCatalog.Value.Grades.ContainsKey(request.Grade.Value))
                        return Error("请选择当前 PVF 支持的决斗等级");
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
            if (request.WinPoints.HasValue && (request.Grade.HasValue || request.RatingGrade.HasValue))
                return Error("胜点与决斗等级请分别保存");
            if (!TryGetAccountId(characterId, out var accountId)) return Error("角色不存在");
            if (request.WinPoints.HasValue)
                return _inventory.TrySetVirtualCount(characterId, accountId, 2, request.WinPoints.Value)
                    ? new { success = true } : Error("胜点写入失败");
            if (!request.Grade.HasValue && !request.RatingGrade.HasValue) return Error("未指定修改项");
            using var c = new SqliteConnection(_config.ConnectionString); c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE characters SET pvp_grade=COALESCE($grade,pvp_grade),pvp_rating_grade=COALESCE($rating,pvp_rating_grade) WHERE character_id=$id AND delete_flag=0";
            cmd.Parameters.AddWithValue("$id", characterId); cmd.Parameters.AddWithValue("$grade", (object)request.Grade ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rating", (object)request.RatingGrade ?? DBNull.Value);
            return cmd.ExecuteNonQuery() == 1 ? new { success = true } : Error("角色不存在");
        }
    }
}
