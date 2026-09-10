using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DfoGmTool.Services
{
    public sealed class ProfessionAbilityRequest
    {
        public int Level { get; set; }
        public int ExpectedType { get; set; }
        public string ExpectedState { get; set; }
        public bool CharacterOffline { get; set; }
    }
    internal sealed class ProfessionAbilityState
    {
        public int Type { get; set; }
        public string State { get; set; }
        public int Level { get; set; }
        public int Endurance { get; set; }
        public int[] Levels { get; set; } = Array.Empty<int>();
    }
    public sealed partial class GmService
    {
        private ProfessionAbilityState LoadProfessionAbilities(SqliteConnection connection, SqliteTransaction transaction, int characterId)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = @"SELECT c.level,COALESCE(f.expert_job_type,0),COALESCE(f.expert_job_exp,0),
COALESCE(e.disjoint_machine_grade,0),COALESCE(e.disjoint_machine_endurance,0)
FROM characters c LEFT JOIN character_subtype0_fields f ON f.character_id=c.character_id
LEFT JOIN character_expert_job e ON e.character_id=c.character_id
WHERE c.character_id=@id AND c.delete_flag=0";
            command.Parameters.AddWithValue("@id", characterId);
            var state = new ProfessionAbilityState(); long experience; int characterLevel;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new InvalidOperationException("角色不存在或已删除");
                characterLevel=reader.GetInt32(0);state.Type=reader.GetInt32(1);experience=reader.GetInt64(2);
                state.Level=reader.GetInt32(3);state.Endurance=reader.GetInt32(4);
            }
            if(state.Type!=3) return null;
            var profession=Professions.Jobs.Single(x=>x.Type==3);
            var signature=$"{characterId}|{state.Type}|{experience}|{characterLevel}|{state.Level}|{state.Endurance}";
            state.State=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
            state.Levels=Professions.MachineLevels.Where(x=>x.Level<=profession.GetLevel(experience)
                && x.MinimumCharacterLevel<=characterLevel).Select(x=>x.Level).ToArray();
            return state;
        }

        public object SetProfessionAbility(int characterId, ProfessionAbilityRequest request)
        {
            if(request==null || !request.CharacterOffline) return Error("请先确认当前角色不在线");
            using var connection=new SqliteConnection(_config.ConnectionString);connection.Open();
            using var transaction=connection.BeginTransaction(deferred:false);
            var state=LoadProfessionAbilities(connection,transaction,characterId);
            if(state==null || state.Type!=request.ExpectedType || state.State!=request.ExpectedState)
                return Error("当前角色不是分解师，或副职业、分解机状态已变化，请刷新后重试");
            if(!state.Levels.Contains(request.Level))
                return Error("请选择可用的分解机等级；需满足角色等级和副职业等级要求");
            if(state.Level!=request.Level)
            {
                var maximum=Professions.MachineLevels.Single(x=>x.Level==request.Level).MaximumEndurance;
                using var command=connection.CreateCommand();command.Transaction=transaction;
                command.CommandText=@"INSERT INTO character_expert_job(character_id,disjoint_machine_grade,disjoint_machine_endurance)
VALUES(@id,@level,@endurance) ON CONFLICT(character_id) DO UPDATE SET
disjoint_machine_grade=@level,disjoint_machine_endurance=@endurance,updated_at=CURRENT_TIMESTAMP";
                command.Parameters.AddWithValue("@id",characterId);command.Parameters.AddWithValue("@level",request.Level);
                command.Parameters.AddWithValue("@endurance",maximum);command.ExecuteNonQuery();
            }
            transaction.Commit();
            return new {success=true,characterId,level=request.Level};
        }
    }
}
