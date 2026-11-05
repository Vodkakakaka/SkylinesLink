using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;
using SkylinesLink.Contracts.Fixtures;

var root = Directory.GetCurrentDirectory();
var file = args.Length == 0 ? Path.Combine(root, "contracts", "fixtures", "cases.json") : args[0];
var cases = (JArray)JToken.Parse(File.ReadAllText(file));
if (args.Length == 0)
    foreach (var name in new[] { "operation-v2-cases.json", "operation-journal-cases.json", "operation-v3-cases.json", "operation-journal-v2-cases.json", "history-cases.json", "mixed-history-cases.json", "journal-control-cases.json", "guidance-cases.json", "authorization-v2-cases.json", "mixed-journal-cases.json", "journal-control-v2-cases.json", "task-stop-cases.json", "module-runtime-cases.json" })
        foreach (var entry in (JArray)JToken.Parse(File.ReadAllText(Path.Combine(root, "contracts", "fixtures", name)))) cases.Add(entry);
var failures = new List<string>();
var results = new JArray();
foreach (var entry in cases)
{
    var name = entry["name"]!.Value<string>()!;
    var id = entry["schemaId"]!.Value<string>()!;
    var expected = entry["valid"]!.Value<bool>();
    var expectedCode = entry["code"]?.Value<string>();
    ValidationResult? outcome = null;
    IReadOnlyList<ValidationIssue> issues = Array.Empty<ValidationIssue>();
    JToken? value = null;
    try
    {
        value = ContractJson.Parse(entry["json"]!.Value<string>()!);
        var binding = entry["binding"] is JObject b ? new RegisteredBinding(b["capabilityId"]!.Value<string>()!, b["schemaRevision"]!.Value<string>()!) : null;
        outcome = id.Contains("profile-fixture", StringComparison.Ordinal) ? FixtureCatalog.Validate(id, value) : ContractCatalog.Validate(id, value, binding);
        issues = outcome.Issues;
    }
    catch (ContractValidationException e) { issues = e.Issues; }
    var valid = outcome?.Valid == true;
    var code = issues.FirstOrDefault()?.Code;
    if (valid != expected || expectedCode != null && code != expectedCode) failures.Add(name + ": expected=" + expected + "/" + expectedCode + ", actual=" + valid + "/" + code);
    if (valid && id.Contains("profile-fixture", StringComparison.Ordinal))
    {
        var dto = FixtureCodec.Decode(value!);
        var encoded = FixtureCodec.Encode(dto);
        if (!SemanticEqual(value!, encoded)) failures.Add(name + ": typed round-trip changed content");
        if (value!["label"] == null && dto.Label.HasValue) failures.Add(name + ": absent field became present");
        if (value!["label"]?.Type == JTokenType.Null && (!dto.Label.HasValue || dto.Label.Value != null)) failures.Add(name + ": explicit null lost");
        if (value!["defaulted"] == null && dto.Defaulted.HasValue) failures.Add(name + ": annotation default injected");
    }
    if (valid)
    {
        // These records cross the persistence and pipe boundaries. Check the actual
        // generated DTO/codec, including an explicit null external HTTP credential.
        var encoded = id switch
        {
            SchemaIds.AuthorizationSnapshot => ContractCodec.EncodeAuthorizationSnapshot(ContractCodec.DecodeAuthorizationSnapshot(value!)),
            SchemaIds.AuthorizationSnapshotV2 => ContractCodec.EncodeAuthorizationSnapshotV2(ContractCodec.DecodeAuthorizationSnapshotV2(value!)),
            SchemaIds.EnvironmentDiscovery => ContractCodec.EncodeEnvironmentDiscovery(ContractCodec.DecodeEnvironmentDiscovery(value!)),
            SchemaIds.WireAuthenticateV2 => ContractCodec.EncodeWireAuthenticateV2(ContractCodec.DecodeWireAuthenticateV2(value!)),
            SchemaIds.WireChallengeV2 => ContractCodec.EncodeWireChallengeV2(ContractCodec.DecodeWireChallengeV2(value!)),
            SchemaIds.WireProofV2 => ContractCodec.EncodeWireProofV2(ContractCodec.DecodeWireProofV2(value!)),
            SchemaIds.WireAuthenticatedV2 => ContractCodec.EncodeWireAuthenticatedV2(ContractCodec.DecodeWireAuthenticatedV2(value!)),
            SchemaIds.OperationQueryV2 => ContractCodec.EncodeOperationQueryV2(ContractCodec.DecodeOperationQueryV2(value!)),
            SchemaIds.OperationQueryV3 => ContractCodec.EncodeOperationQueryV3(ContractCodec.DecodeOperationQueryV3(value!)),
            SchemaIds.TaskStopQuery => ContractCodec.EncodeTaskStopQuery(ContractCodec.DecodeTaskStopQuery(value!)),
            SchemaIds.ModuleRuntimeQuery => ContractCodec.EncodeModuleRuntimeQuery(ContractCodec.DecodeModuleRuntimeQuery(value!)),
            SchemaIds.ModuleRuntimePage => ContractCodec.EncodeModuleRuntimePage(ContractCodec.DecodeModuleRuntimePage(value!)),
            SchemaIds.TaskStopResult => ContractCodec.EncodeTaskStopResult(ContractCodec.DecodeTaskStopResult(value!)),
            SchemaIds.TaskStopDecisionQuery => ContractCodec.EncodeTaskStopDecisionQuery(ContractCodec.DecodeTaskStopDecisionQuery(value!)),
            SchemaIds.TaskStopDecisionLookup => ContractCodec.EncodeTaskStopDecisionLookup(ContractCodec.DecodeTaskStopDecisionLookup(value!)),
            SchemaIds.OperationLookupV3 => ContractCodec.EncodeOperationLookupV3(ContractCodec.DecodeOperationLookupV3(value!)),
            SchemaIds.OperationLookupV4 => ContractCodec.EncodeOperationLookupV4(ContractCodec.DecodeOperationLookupV4(value!)),
            SchemaIds.JournalControl => ContractCodec.EncodeJournalControl(ContractCodec.DecodeJournalControl(value!)),
            SchemaIds.JournalControlV2 => ContractCodec.EncodeJournalControlV2(ContractCodec.DecodeJournalControlV2(value!)),
            SchemaIds.GuidanceSearchQuery => ContractCodec.EncodeGuidanceSearchQuery(ContractCodec.DecodeGuidanceSearchQuery(value!)),
            SchemaIds.GuidanceSearchResult => ContractCodec.EncodeGuidanceSearchResult(ContractCodec.DecodeGuidanceSearchResult(value!)),
            SchemaIds.GuidanceQuery => ContractCodec.EncodeGuidanceQuery(ContractCodec.DecodeGuidanceQuery(value!)),
            SchemaIds.GuidanceDocument => ContractCodec.EncodeGuidanceDocument(ContractCodec.DecodeGuidanceDocument(value!)),
            SchemaIds.CapabilityQuery => ContractCodec.EncodeCapabilityQuery(ContractCodec.DecodeCapabilityQuery(value!)),
            SchemaIds.CapabilityDescription => ContractCodec.EncodeCapabilityDescription(ContractCodec.DecodeCapabilityDescription(value!)),
            SchemaIds.OperationHistoryCheckpoint => ContractCodec.EncodeOperationHistoryCheckpoint(ContractCodec.DecodeOperationHistoryCheckpoint(value!)),
            SchemaIds.OperationHistoryCheckpointV2 => ContractCodec.EncodeOperationHistoryCheckpointV2(ContractCodec.DecodeOperationHistoryCheckpointV2(value!)),
            SchemaIds.NativeHistoryRequest => ContractCodec.EncodeNativeHistoryRequest(ContractCodec.DecodeNativeHistoryRequest(value!)),
            SchemaIds.NativeHistoryRequestV2 => ContractCodec.EncodeNativeHistoryRequestV2(ContractCodec.DecodeNativeHistoryRequestV2(value!)),
            SchemaIds.NativeHistoryCapture => ContractCodec.EncodeNativeHistoryCapture(ContractCodec.DecodeNativeHistoryCapture(value!)),
            SchemaIds.NativeHistoryCaptureV2 => ContractCodec.EncodeNativeHistoryCaptureV2(ContractCodec.DecodeNativeHistoryCaptureV2(value!)),
            SchemaIds.OperationLookupV2 => ContractCodec.EncodeOperationLookupV2(ContractCodec.DecodeOperationLookupV2(value!)),
            SchemaIds.OperationJournalRecordV2 => ContractCodec.EncodeOperationJournalRecordV2(ContractCodec.DecodeOperationJournalRecordV2(value!)),
            SchemaIds.OperationJournalRecordV3 => ContractCodec.EncodeOperationJournalRecordV3(ContractCodec.DecodeOperationJournalRecordV3(value!)),
            SchemaIds.OperationLookup => ContractCodec.EncodeOperationLookup(ContractCodec.DecodeOperationLookup(value!)),
            SchemaIds.OperationJournalRecord => ContractCodec.EncodeOperationJournalRecord(ContractCodec.DecodeOperationJournalRecord(value!)),
            _ => null
        };
        if (encoded != null && !SemanticEqual(value!, encoded)) failures.Add(name + ": connection typed round-trip changed content");
        if (id == SchemaIds.OperationJournalRecordV3 && name == "mixed3/maximum-escaped-goal" && encoded != null)
        {
            int bytes = System.Text.Encoding.UTF8.GetByteCount(encoded.ToString(Formatting.None));
            if (bytes <= 4096 || bytes > 96 * 1024) failures.Add(name + ": actual mixed metadata encoding did not verify its separate payload bound");
        }
        if (id == SchemaIds.JournalControlV2 && name.StartsWith("control2/maximum-escaped-", StringComparison.Ordinal) && encoded != null &&
            System.Text.Encoding.UTF8.GetByteCount(encoded.ToString(Formatting.None)) > 4052)
            failures.Add(name + ": generated control2 encoding exceeds one fixed control payload");
    }
    results.Add(new JObject { ["name"] = name, ["valid"] = valid, ["code"] = code });
}
// JSON 文本不能表达 Json.NET 的 String/null 内存形态；直接构造检查 validator 安全拒绝。
var nonCanonicalNull = new JObject { ["selector"] = new JObject { ["kind"] = "operationId", ["operationId"] = new JValue((string?)null) } };
var nullOutcome = ContractCatalog.Validate(SchemaIds.OperationQueryV2, nonCanonicalNull);
if (nullOutcome.Valid || nullOutcome.Issues.FirstOrDefault()?.Code != "TYPE") failures.Add("String/null in-memory token was not rejected as TYPE");
var historyEncoding = HistoryCodecChecks.Run(root, failures);
// Desktop consumes the same explicit authorization2 schema, including rejected
// shapes; profile hex encoding and identity uniqueness belong to its consumer.
foreach (var sample in JArray.Parse(File.ReadAllText(Path.Combine(root, "contracts", "fixtures", "authorization-v2-cases.json"))))
{
    bool valid;
    try
    {
        var parsed = SkylinesLink.Deployment.Generated.DeploymentContract.ParseAuthorizationSnapshotV2((string)sample["json"]!);
        var expected = JObject.Parse((string)sample["json"]!);
        if (parsed.FormatVersion != 2 || !parsed.Profiles.Select(p => p.ProfileInstanceId).SequenceEqual(expected["profiles"]!.Select(p => (string)p["profileInstanceId"]!)))
            failures.Add("Desktop authorization2 identity changed: " + (string)sample["name"]!);
        valid = true;
    }
    catch (FormatException) { valid = false; }
    if (valid != (bool)sample["valid"]!) failures.Add("Desktop authorization2 shared fixture: " + (string)sample["name"]!);
}
Console.WriteLine(new JObject { ["cases"] = cases.Count, ["failures"] = new JArray(failures), ["historyEncoding"] = historyEncoding, ["results"] = results }.ToString(Formatting.None));
return failures.Count == 0 ? 0 : 1;

static bool SemanticEqual(JToken a, JToken b)
{
    var numeric = new[] { JTokenType.Integer, JTokenType.Float };
    if (numeric.Contains(a.Type) && numeric.Contains(b.Type)) return a.Value<double>() == b.Value<double>();
    if (a.Type != b.Type) return false;
    if (a is JObject ao && b is JObject bo) return ao.Count == bo.Count && ao.Properties().All(p => bo.TryGetValue(p.Name, out var other) && SemanticEqual(p.Value, other));
    if (a is JArray aa && b is JArray ba) return aa.Count == ba.Count && aa.Zip(ba, SemanticEqual).All(x => x);
    return JToken.DeepEquals(a, b);
}
