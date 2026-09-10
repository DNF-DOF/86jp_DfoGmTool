using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GmPvfLib;

namespace DfoGmTool.Services
{
    internal sealed class CharacterProfessionDefinition
    {
        public int Type { get; set; }
        public string Name { get; set; }
        public int[] Thresholds { get; set; }
        public int[] QuestIds { get; set; }
        public int[] ResetQuestIds { get; set; }
        public Dictionary<int, int> Skills { get; set; }
        public Dictionary<int, int> AutoRecipes { get; set; }
        public int InitialEndurance { get; set; }
        // Match the server: disjointer uses all thresholds; recipe jobs cap at row count.
        public int MaxLevel => Type == 3 ? Thresholds.Length + 1 : Thresholds.Length;
        public int GetLevel(long experience) => Math.Min(MaxLevel, 1 + Thresholds.TakeWhile(x => experience >= x).Count());
        public long ExperienceForLevel(int level)
        {
            if (level < 1 || level > MaxLevel) throw new ArgumentException($"{Name}等级范围为 1–{MaxLevel}");
            return level == 1 ? 0 : Thresholds[level - 2];
        }
    }

    internal sealed class CharacterProfessionCatalog
    {
        internal IReadOnlyList<CharacterProfessionDefinition> Jobs { get; }
        internal IReadOnlyList<ProfessionMachineLevel> MachineLevels { get; }
        internal CharacterProfessionCatalog(string pvfPath)
        {
            using var archive = PvfArchive.Open(pvfPath);
            Jobs = new[] { (1, "附魔师", "enchanter"), (2, "炼金术师", "alchemist"),
                (3, "分解师", "disjointer"), (4, "控偶师", "doll_controller") }
                .Select(x => Parse(x.Item1, x.Item2, archive.GetFileContent($"character/expertjob/{x.Item3}.exj"))).ToArray();
            MachineLevels = ParseMachineLevels(archive.GetFileContent("character/expertjob/disjointer.exj"),
                archive.GetFileContent("character/expertjob.etc"));
        }
        internal sealed class ProfessionMachineLevel
        {
            public int Level { get; set; }
            public int MaximumEndurance { get; set; }
            public int MinimumCharacterLevel { get; set; }
        }
        private static IReadOnlyList<ProfessionMachineLevel> ParseMachineLevels(string machine, string common)
        {
            int[] Tokens(string text, string tag)
            {
                var match = Regex.Match(text ?? "", @"\[" + Regex.Escape(tag) + @"\](.*?)\[/" + Regex.Escape(tag) + @"\]", RegexOptions.Singleline);
                var tokens = match.Groups[1].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (!match.Success || tokens.Length == 0 || tokens.Length % 2 != 0)
                    throw new InvalidOperationException("分解机 PVF 配置缺失或格式错误：" + tag);
                return tokens.Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            }
            var repairs = Tokens(machine, "endurance repair cost");
            var limits = Tokens(common, "expertjob level limit");
            var upgrades = Tokens(machine, "upgrade cost");
            var minimumLevels = Enumerable.Range(0, limits.Length / 2).ToDictionary(i => limits[2*i], i => limits[2*i+1]);
            var upgradeLevels = Enumerable.Range(0, upgrades.Length / 2).ToDictionary(i => upgrades[2*i], i => upgrades[2*i+1]);
            var result = new List<ProfessionMachineLevel>();
            for (int i = 0; i < repairs.Length / 2; i++)
            {
                var level = i + 1;
                if (level > byte.MaxValue || repairs[2*i+1] <= 0 || !minimumLevels.TryGetValue(level, out var minimum)
                    || minimum <= 0 || !upgradeLevels.TryGetValue(level, out var cost) || cost < 0)
                    throw new InvalidOperationException("分解机等级配置无效");
                result.Add(new ProfessionMachineLevel { Level = level, MaximumEndurance = repairs[2*i+1], MinimumCharacterLevel = minimum });
            }
            return result;
        }
        internal static CharacterProfessionDefinition Parse(int type, string name, string text)
        {
            string[] Tokens(string tag)
            {
                var match = Regex.Match(text ?? "", @"(?mi)^\[" + Regex.Escape(tag) + @"\][ \t]*\r?\n(?<data>[\s\S]*?)(?=^\[|\z)");
                return Regex.Matches(match.Groups["data"].Value, @"`[^`]*`|\S+").Select(m => m.Value).ToArray();
            }
            int Number(string s) => int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
            int[] Ids(string tag) => Tokens(tag).Select(Number).Distinct().ToArray();
            Dictionary<int, int> Pairs(string tag)
            {
                var tokens = Tokens(tag);
                if (tokens.Length % 2 != 0) throw new InvalidOperationException(name + " PVF " + tag + " 格式错误");
                var pairs = new Dictionary<int, int>();
                for (int i = 0; i < tokens.Length; i += 2) pairs.Add(Number(tokens[i]), Number(tokens[i + 1]));
                return pairs;
            }
            var exp = Tokens("expertness exp");
            if (exp.Length == 0 || exp.Length % 3 != 0) throw new InvalidOperationException(name + " PVF 经验表缺失或格式错误");
            var thresholds = Enumerable.Range(0, exp.Length / 3).Select(i => Number(exp[i * 3])).ToArray();
            if (thresholds[0] <= 0 || thresholds.Zip(thresholds.Skip(1)).Any(x => x.First >= x.Second))
                throw new InvalidOperationException(name + " PVF 经验阈值必须递增");
            var quests = Ids("connect quest list"); var reset = Ids("clear quests");
            if (quests.Length == 0 || quests.Concat(reset).Any(x => x < 1 || x > 29999) || quests.Except(reset).Any())
                throw new InvalidOperationException(name + " PVF 职业任务定义无效");
            var skills = Pairs("skill");
            if (skills.Count == 0 || skills.Any(x => x.Key <= 0 || x.Key > ushort.MaxValue || x.Value < 0 || x.Value > 255))
                throw new InvalidOperationException(name + " PVF 职业技能定义无效");
            var endurance = Tokens("endurance initial value");
            return new CharacterProfessionDefinition { Type = type, Name = name, Thresholds = thresholds,
                QuestIds = quests, ResetQuestIds = reset, Skills = skills, AutoRecipes = Pairs("auto learn recipe"),
                InitialEndurance = endurance.Length == 1 ? Number(endurance[0]) : 0 };
        }
    }
}
