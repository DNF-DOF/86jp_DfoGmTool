using System;
using System.Text;
using System.Text.Json.Nodes;

namespace DfoGmTool.ServerCore.Infrastructure
{
    // Used only at explicitly legacy import boundaries, never to decode a live A21 row.
    internal static class LegacyClientText
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static object ConvertName(object value)
        {
            if (value == null || value == DBNull.Value)
                return value;
            if (value is string text)
                return EncodeStrict(text);
            if (value is not byte[] bytes)
                throw new InvalidOperationException("旧名字节必须为 BLOB 或 TEXT");
            try
            {
                return EncodeStrict(Utf8.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                // Match schema v11: already-GBK blobs are retained.
                if (!ClientTextEncoding.TryGetStringStrict(bytes, out _))
                    throw new InvalidOperationException("旧名字节既不是有效 UTF-8，也不是 GBK");
                return bytes;
            }
        }

        private static byte[] EncodeStrict(string text)
        {
            ClientTextEncoding.EnsureInitialized();
            return Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback).GetBytes(text);
        }

        internal static string ConvertMailboxDetail(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;
            var root = JsonNode.Parse(json);
            var creature = root?["Creature"] ?? root?["creature"];
            if (creature is not JsonObject obj)
                return json;
            var key = obj.ContainsKey("NameBytes") ? "NameBytes" : "nameBytes";
            if (obj[key] == null)
                return json;
            var bytes = Convert.FromBase64String(obj[key].GetValue<string>());
            obj[key] = Convert.ToBase64String((byte[])ConvertName(bytes));
            return root.ToJsonString();
        }
    }
}
