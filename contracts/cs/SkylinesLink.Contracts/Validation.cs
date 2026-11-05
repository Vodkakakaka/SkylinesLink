using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SkylinesLink.Contracts
{
    public sealed class ValidationIssue
    {
        public string Path { get; }
        public string Code { get; }
        public string Message => "输入不符合契约约束：" + Code;
        public ValidationIssue(string path, string code) { Path = path; Code = code; }
    }
    public sealed class ValidationResult
    {
        public bool Valid => Issues.Count == 0;
        public IReadOnlyList<ValidationIssue> Issues { get; }
        internal ValidationResult(IReadOnlyList<ValidationIssue> issues) { Issues = issues; }
    }
    public sealed class ContractValidationException : Exception
    {
        public IReadOnlyList<ValidationIssue> Issues { get; }
        public ContractValidationException(IReadOnlyList<ValidationIssue> issues) : base("契约校验失败") { Issues = issues; }
    }
    public sealed class RegisteredBinding
    {
        public string CapabilityId { get; }
        public string SchemaRevision { get; }
        public RegisteredBinding(string capabilityId, string schemaRevision) { CapabilityId = capabilityId; SchemaRevision = schemaRevision; }
    }
    public readonly struct Optional<T>
    {
        public bool HasValue { get; }
        private readonly T value;
        public T Value => HasValue ? value : throw new InvalidOperationException("字段缺失");
        public Optional(T value) { this.value = value; HasValue = true; }
        public static Optional<T> Missing => default;
    }
    internal sealed class SchemaNode
    {
        internal string Type = "";
        internal bool Nullable;
        internal string? Boundary;
        internal Dictionary<string, SchemaNode> Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal);
        internal string[] Required = Array.Empty<string>();
        internal int MaxProperties, MinItems, MaxItems, MinLength, MaxLength;
        internal SchemaNode? Items;
        internal SchemaNode[] Branches = Array.Empty<SchemaNode>();
        internal string[]? Enum;
        internal string? Const;
        internal string? StringRule = null;
        internal double Minimum, Maximum;
        internal DecimalRule? Decimal = null;
    }
    internal sealed class DecimalRule { internal bool Signed = false; internal int Scale = 0, MaxDigits = 0; }
    public sealed class CapabilityDeclaration
    {
        public string Id { get; }
        public string ToolName { get; }
        public string SchemaRevision { get; }
        public string Description { get; }
        public string Effect { get; }
        public string InputId { get; }
        public string OutputId { get; }
        public CapabilityDeclaration(string id, string toolName, string schemaRevision, string description, string effect, string inputId, string outputId)
        { Id = id; ToolName = toolName; SchemaRevision = schemaRevision; Description = description; Effect = effect; InputId = inputId; OutputId = outputId; }
    }
    internal static class ContractValidator
    {
        private static string Pointer(string path, string key) => path + "/" + key.Replace("~", "~0").Replace("/", "~1");
        private static bool Unicode(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
                else if (char.IsLowSurrogate(text[i])) return false;
            }
            return true;
        }
        private static int ScalarLength(string text)
        { var count = 0; for (var i = 0; i < text.Length; i++, count++) if (char.IsHighSurrogate(text[i])) i++; return count; }
        private static bool IsNumber(JToken t) => t.Type == JTokenType.Integer || t.Type == JTokenType.Float;
        private static bool SameJson(JToken a, JToken b)
        {
            if (IsNumber(a) && IsNumber(b)) return a.Value<double>() == b.Value<double>();
            if (a.Type != b.Type) return false;
            if (a is JObject ao && b is JObject bo) return ao.Count == bo.Count && ao.Properties().All(p => bo.TryGetValue(p.Name, StringComparison.Ordinal, out var other) && SameJson(p.Value, other));
            if (a is JArray aa && b is JArray ba) return aa.Count == ba.Count && aa.Zip(ba, SameJson).All(x => x);
            return JToken.DeepEquals(a, b);
        }
        internal static ValidationResult Validate(string id, JToken value, RegisteredBinding? binding,
            IReadOnlyDictionary<string, SchemaNode> models, IReadOnlyDictionary<string, JToken> schemas, IReadOnlyList<CapabilityDeclaration> caps, SchemaNode? selectedModel = null)
        {
            var issues = new List<ValidationIssue>();
            bool Error(string path, string code) { if (issues.Count == 0) issues.Add(new ValidationIssue(path, code)); return false; }
            var nodes = 0;
            bool Inspect(JToken v, string path, int depth)
            {
                if (++nodes > 16384 || depth > 32) return Error(path, "BUDGET");
                if (v.Type == JTokenType.Null || v.Type == JTokenType.Boolean) return true;
                if (IsNumber(v)) { var n = v.Value<double>(); return !double.IsNaN(n) && !double.IsInfinity(n) || Error(path, "NUMBER"); }
                if (v.Type == JTokenType.String)
                {
                    // Json.NET 可在内存构造 String/null；这不是受控 JSON parser 产生的普通字符串。
                    var s = v.Value<string>();
                    if (s == null) return Error(path, "TYPE");
                    return Unicode(s) ? ScalarLength(s) <= 65536 || Error(path, "BUDGET") : Error(path, "UNICODE");
                }
                if (v is JArray array)
                { if (array.Count > 4096) return Error(path, "BUDGET"); for (var i = 0; i < array.Count; i++) if (!Inspect(array[i], Pointer(path, i.ToString(CultureInfo.InvariantCulture)), depth + 1)) return false; return true; }
                if (!(v is JObject obj)) return Error(path, "TYPE");
                if (obj.Count > 256) return Error(path, "BUDGET");
                foreach (var p in obj.Properties())
                { if (!Unicode(p.Name)) return Error(Pointer(path, p.Name), "UNICODE"); if (ScalarLength(p.Name) > 65536 || !Inspect(p.Value, Pointer(path, p.Name), depth + 1)) return Error(Pointer(path, p.Name), "BUDGET"); }
                return true;
            }
            bool Check(SchemaNode s, JToken v, string path, JToken root)
            {
                if (v.Type == JTokenType.Null && (s.Nullable || s.Type == "null")) return true;
                switch (s.Type)
                {
                    case "object":
                        if (!(v is JObject obj)) return Error(path, "TYPE");
                        if (obj.Count > s.MaxProperties) return Error(path, "PROPERTIES");
                        if (s.Boundary == "schemaDocument")
                        { var sid = obj["$id"]?.Type == JTokenType.String ? obj["$id"]!.Value<string>() : null; return sid != null && schemas.TryGetValue(sid, out var expected) && SameJson(expected, obj) || Error(path, "SCHEMA_DOCUMENT"); }
                        if (s.Boundary != null)
                        {
                            var cid = binding?.CapabilityId; var revision = binding?.SchemaRevision;
                            if (s.Boundary == "registeredInput")
                            { cid = root["capabilityId"]?.Type == JTokenType.String ? root["capabilityId"]!.Value<string>() : null; revision = root["schemaRevision"]?.Type == JTokenType.String ? root["schemaRevision"]!.Value<string>() : null; }
                            if (cid == null || revision == null) return Error(path, "BINDING_REQUIRED");
                            var cap = caps.FirstOrDefault(c => c.Id == cid);
                            if (cap == null) return Error(path, "UNKNOWN_CAPABILITY");
                            if (cap.SchemaRevision != revision) return Error(path, "SCHEMA_REVISION");
                            return Check(models[s.Boundary == "registeredInput" ? cap.InputId : cap.OutputId], v, path, v);
                        }
                        foreach (var key in s.Required) if (!obj.TryGetValue(key, StringComparison.Ordinal, out _)) return Error(Pointer(path, key), "REQUIRED");
                        foreach (var prop in obj.Properties()) if (!s.Properties.ContainsKey(prop.Name)) return Error(Pointer(path, prop.Name), "EXTRA_PROPERTY");
                        foreach (var child in s.Properties) if (obj.TryGetValue(child.Key, StringComparison.Ordinal, out var val) && !Check(child.Value, val, Pointer(path, child.Key), root)) return false;
                        return true;
                    case "array":
                        if (!(v is JArray array)) return Error(path, "TYPE");
                        if (array.Count < s.MinItems || array.Count > s.MaxItems) return Error(path, "LENGTH");
                        for (var i = 0; i < array.Count; i++) if (!Check(s.Items!, array[i], Pointer(path, i.ToString(CultureInfo.InvariantCulture)), root)) return false;
                        return true;
                    case "string":
                        if (v.Type != JTokenType.String) return Error(path, "TYPE");
                        var text = v.Value<string>()!; var len = ScalarLength(text);
                        if (len < s.MinLength || len > s.MaxLength) return Error(path, "LENGTH");
                        if (s.Enum != null && !s.Enum.Contains(text, StringComparer.Ordinal) || s.Const != null && s.Const != text) return Error(path, "ENUM");
                        if (s.StringRule != null && !ValidStringRule(text, s.StringRule)) return Error(path, "PATTERN");
                        if (s.Decimal != null && !ValidDecimal(text, s.Decimal)) return Error(path, "DECIMAL");
                        return true;
                    case "integer": case "number":
                        if (!IsNumber(v)) return Error(path, "TYPE");
                        var n = v.Value<double>();
                        if (double.IsNaN(n) || double.IsInfinity(n)) return Error(path, "TYPE");
                        if (s.Type == "integer" && (Math.Truncate(n) != n || Math.Abs(n) > 9007199254740991d)) return Error(path, "INTEGER");
                        return n >= s.Minimum && n <= s.Maximum || Error(path, "RANGE");
                    case "boolean": return v.Type == JTokenType.Boolean || Error(path, "TYPE");
                    case "null": return Error(path, "TYPE");
                    case "union":
                        if (!(v is JObject u)) return Error(path, "TYPE");
                        var tag = u["kind"]?.Type == JTokenType.String ? u["kind"]!.Value<string>() : null;
                        var branch = s.Branches.FirstOrDefault(b => b.Properties["kind"].Const == tag);
                        return branch != null ? Check(branch, v, path, root) : Error(Pointer(path, "kind"), "DISCRIMINATOR");
                    default: return Error(path, "SCHEMA");
                }
            }
            if (!models.TryGetValue(id, out var model)) Error("", "UNKNOWN_SCHEMA");
            else if (Inspect(value, "", 0)) Check(selectedModel ?? model, value, "", value);
            return new ValidationResult(issues);
        }
        private static bool ValidStringRule(string text, string rule)
        {
            if (rule == "hex32Lower")
            {
                if (text.Length != 32) return false;
                foreach (var c in text) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
                return true;
            }
            if (rule != "assemblyVersion" || text.Length < 1 || text.Length > 32) return false;
            var parts = 1; var digit = false;
            foreach (var c in text)
            {
                if (c >= '0' && c <= '9') { digit = true; continue; }
                if (c != '.' || !digit || parts == 4) return false;
                parts++; digit = false;
            }
            return digit;
        }
        private static bool ValidDecimal(string text, DecimalRule d)
        {
            var negative = text.StartsWith("-", StringComparison.Ordinal);
            if (negative && !d.Signed) return false;
            var body = negative ? text.Substring(1) : text;
            var parts = body.Split('.');
            if (parts.Length != (d.Scale == 0 ? 1 : 2)) return false;
            var whole = parts[0];
            if (whole.Length == 0 || whole.Length > 1 && whole[0] == '0' || whole.Any(c => c < '0' || c > '9')) return false;
            if (d.Scale > 0 && (parts[1].Length != d.Scale || parts[1].Any(c => c < '0' || c > '9'))) return false;
            var digits = string.Concat(parts);
            return digits.Length <= d.MaxDigits && !(negative && digits.All(c => c == '0'));
        }
    }

    // Json.NET 同时接受 JavaScript 扩展。先只允许严格 JSON 词法，并保留重复键的后值语义。
    public static class ContractJson
    {
        public static JToken Parse(string json) => ParseCore(json, false);
        internal static JToken ParseHistoryCheckpointV2(string json) => ParseCore(json, true);
        private static JToken ParseCore(string json, bool checkpointV2)
        {
            try
            {
                if (checkpointV2) StrictJsonSyntax.ValidateHistoryCheckpointV2(json); else StrictJsonSyntax.Validate(json);
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 33 })
                    return JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace, CommentHandling = CommentHandling.Ignore, LineInfoHandling = LineInfoHandling.Ignore });
            }
            catch (ContractValidationException) { throw; }
            catch (JsonException) { throw Invalid("JSON"); }
            catch (FormatException e) { throw Invalid(e.Message == "UNICODE" || e.Message == "BUDGET" ? e.Message : "JSON"); }
            catch (OverflowException) { throw Invalid("NUMBER"); }
        }
        private static ContractValidationException Invalid(string code) => new ContractValidationException(new[] { new ValidationIssue("", code) });

    }
}
