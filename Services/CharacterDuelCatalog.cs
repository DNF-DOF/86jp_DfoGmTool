using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GmPvfLib;

namespace DfoGmTool.Services
{
    internal sealed class CharacterDuelCatalog
    {
        // S4A21_CN/dstr.dat FILE/102, CNUser.cpp strings 1207–1227.
        // PVF pvp grade point supplies the actual editable grade IDs.
        // Ranking titles (strings 1228+) belong to a separate rating system;
        // do not infer their wire encoding from string-table order.
        private static readonly string[] Names = {
            "入门", "青铜1星", "青铜2星", "青铜3星", "青铜4星",
            "白银1星", "白银2星", "白银3星", "白银4星",
            "黄金1星", "黄金2星", "黄金3星", "黄金4星",
            "钻石1星", "钻石2星", "钻石3星", "钻石4星",
            "泰拉石1星", "泰拉石2星", "泰拉石3星", "泰拉石4星"
        };
        internal IReadOnlyDictionary<int, string> Grades { get; }
        internal CharacterDuelCatalog(string pvfPath)
        {
            using var archive = PvfArchive.Open(pvfPath);
            Grades = Parse(archive.GetFileContent("etc/pvp_ref.etc"));
        }
        internal static IReadOnlyDictionary<int, string> Parse(string text)
        {
            var block = Regex.Match(text ?? "", @"\[pvp grade point\](.*?)\[/pvp grade point\]", RegexOptions.Singleline);
            var tokens = block.Groups[1].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (!block.Success || tokens.Length == 0 || tokens.Length % 3 != 0)
                throw new InvalidOperationException("PVF 决斗等级表缺失或格式错误");
            var grades = new SortedDictionary<int, string>();
            for (var i = 0; i < tokens.Length; i += 3)
            {
                var id = int.Parse(tokens[i], CultureInfo.InvariantCulture);
                if (id < 0 || id >= Names.Length || grades.ContainsKey(id))
                    throw new InvalidOperationException("PVF 决斗等级表包含尚未确认名称的等级");
                grades.Add(id, Names[id]);
            }
            return grades;
        }
    }
}
