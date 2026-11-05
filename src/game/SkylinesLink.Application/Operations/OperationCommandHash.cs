using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkylinesLink.Application.Context;
using SkylinesLink.Application.Simulation;

namespace SkylinesLink.Application.Operations
{
    public static class OperationCommandHash
    {
        public static string Compute(WorldContextSnapshot source, LoadTarget target)
        {
            // 此有限类型只有普通字符串/null；长度前缀避免分隔符碰撞，不推导通用 JSON 规范化。
            var bytes = new List<byte>(1024);
            foreach (var value in new string?[] { "SkylinesLink/single-load/1", source.InstanceId,
                source.Generation.ToString(CultureInfo.InvariantCulture), source.State, source.CityId,
                target.ProviderId, target.AssetId, target.ContentVersion })
            {
                if (value == null) { bytes.Add(0); continue; }
                bytes.Add(1);
                byte[] encoded = new UTF8Encoding(false, true).GetBytes(value);
                uint length = checked((uint)encoded.Length);
                for (int shift = 0; shift != 32; shift += 8) bytes.Add((byte)(length >> shift));
                bytes.AddRange(encoded);
            }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
        }
    }
}
