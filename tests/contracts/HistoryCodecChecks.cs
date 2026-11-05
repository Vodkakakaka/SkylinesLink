using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkylinesLink.Contracts;
using Desktop = SkylinesLink.Deployment.Generated;

internal static class HistoryCodecChecks
{
    internal static JObject Run(string root, List<string> failures)
    {
        var samples = JArray.Parse(File.ReadAllText(Path.Combine(root, "contracts", "fixtures", "history-cases.json")));
        foreach (var sample in JArray.Parse(File.ReadAllText(Path.Combine(root, "contracts", "fixtures", "mixed-history-cases.json")))) samples.Add(sample);
        foreach (var sample in samples)
        {
            string id = (string)sample["schemaId"]!, json = (string)sample["json"]!;
            if (id != SchemaIds.OperationHistoryCheckpoint && id != SchemaIds.NativeHistoryRequest && id != SchemaIds.NativeHistoryCapture &&
                id != SchemaIds.OperationHistoryCheckpointV2 && id != SchemaIds.NativeHistoryRequestV2 && id != SchemaIds.NativeHistoryCaptureV2) continue;
            bool valid;
            try
            {
                if (id == SchemaIds.OperationHistoryCheckpoint) { using var document = Desktop.DeploymentContract.ParseHistoryCheckpoint(json); }
                else if (id == SchemaIds.OperationHistoryCheckpointV2) { using var document = Desktop.DeploymentContract.ParseHistoryCheckpointV2(json); }
                else if (id == SchemaIds.NativeHistoryRequest) Desktop.DeploymentContract.ParseNativeHistoryRequest(json);
                else if (id == SchemaIds.NativeHistoryRequestV2) Desktop.DeploymentContract.ParseNativeHistoryRequestV2(json);
                else if (id == SchemaIds.NativeHistoryCaptureV2) Desktop.DeploymentContract.ParseNativeHistoryCaptureV2(json);
                else Desktop.DeploymentContract.ParseNativeHistoryCapture(json);
                valid = true;
            }
            catch (FormatException) { valid = false; }
            if (valid != (bool)sample["valid"]!) failures.Add("Desktop shared fixture: " + (string)sample["name"]!);
        }
        // This tests the private lexical envelope only. Real producer byte
        // provenance and complete Task replay have separate runtime/CLI tests.
        var cp2Sample = samples.First(sample => (string)sample["schemaId"]! == SchemaIds.OperationHistoryCheckpointV2 && (bool)sample["valid"]!);
        string largePrivateDocument = new string(' ', 1048576) + (string)cp2Sample["json"]!;
        try
        {
            ContractCatalog.ParseAndValidate(SchemaIds.OperationHistoryCheckpointV2, largePrivateDocument);
            using var document = Desktop.DeploymentContract.ParseHistoryCheckpointV2(largePrivateDocument);
            if (document.RootElement.GetProperty("source").GetProperty("formatVersion").GetInt32() != 2)
                failures.Add("Private CP2 profile changed its source format.");
        }
        catch (Exception) { failures.Add("Private CP2 lexical profile rejected its explicit large envelope."); }
        try { ContractJson.Parse(largePrivateDocument); failures.Add("Ordinary JSON inherited the private CP2 text limit."); }
        catch (ContractValidationException error) { if (error.Issues[0].Code != "BUDGET") failures.Add("Ordinary JSON lost its lexical budget error."); }
        try { using var document = Desktop.DeploymentContract.ParseHistoryCheckpoint(largePrivateDocument); failures.Add("Legacy Desktop history inherited the CP2 text limit."); }
        catch (FormatException) { }
        try { ContractCatalog.ParseAndValidate(SchemaIds.OperationHistoryCheckpointV2, new string(' ', PrivateHistoryLimits.MaximumTextBytes + 1)); failures.Add("Private CP2 text envelope was unbounded."); }
        catch (ContractValidationException error) { if (error.Issues[0].Code != "BUDGET") failures.Add("Private CP2 text envelope lost its budget error."); }
        var baseCapture = JObject.Parse((string)samples.First(s => (string)s["name"]! == "历史Native capture单条结果")["json"]!);
        var checkpoint = new JObject { ["source"] = baseCapture["source"]!.DeepClone(), ["records"] = new JArray(baseCapture["record"]!.DeepClone()) };
        ContractCatalog.Require(SchemaIds.OperationHistoryCheckpoint, checkpoint);
        var gameSource = ContractCodec.DecodeHistorySource(checkpoint["source"]!);
        var gameRecord = ContractCodec.DecodeHistoryRecord(checkpoint["records"]![0]!);
        Desktop.HistorySource desktopSource;
        Desktop.HistoryRecord desktopRecord;
        using (var document = Desktop.DeploymentContract.ParseHistoryCheckpoint(checkpoint.ToString(Formatting.None)))
        {
            desktopSource = Desktop.DeploymentContract.DecodeHistorySource(document.RootElement.GetProperty("source"));
            desktopRecord = Desktop.DeploymentContract.DecodeHistoryRecord(document.RootElement.GetProperty("records")[0]);
        }
        // The selected values still work after the document has been disposed;
        // they contain strings/scalars/DTOs, never a JsonElement parent view.
        if (desktopSource.StoreId != gameSource.StoreId || desktopRecord.OperationId != gameRecord.OperationId ||
            desktopRecord.Load.ObservationContext!.CityId != gameRecord.Load.ObservationContext!.CityId)
            failures.Add("Selected source/record changed across codecs or disposal.");
        var query = ContractCodec.DecodeOperationQueryV3(ContractJson.Parse("{\"selector\":{\"kind\":\"operationId\",\"operationId\":\"op-1\"}}"));
        if (query.Source.HasValue) failures.Add("Absent source was replaced by an injected default.");
        var bad = (JObject)checkpoint.DeepClone();
        ((JArray)bad["records"]!).Add(new JObject { ["invalidTail"] = true });
        try { using var document = Desktop.DeploymentContract.ParseHistoryCheckpoint(bad.ToString(Formatting.None)); failures.Add("Full checkpoint validation skipped invalid tail."); }
        catch (FormatException) { }
        try { ContractCodec.DecodeHistoryRecord(new JObject { ["operationId"] = "op-1" }); failures.Add("Game fragment decode skipped its schema."); }
        catch (ContractValidationException) { }
        using (var invalidFragment = JsonDocument.Parse("{\"operationId\":\"op-1\"}"))
        {
            try { Desktop.DeploymentContract.DecodeHistoryRecord(invalidFragment.RootElement); failures.Add("Desktop fragment decode skipped its schema."); }
            catch (FormatException) { }
        }
        var capture = Desktop.DeploymentContract.ParseNativeHistoryCapture(baseCapture.ToString(Formatting.None));
        var encoded = EncodeCapture(capture, 32768);
        if (!JToken.DeepEquals(JToken.Parse(Encoding.UTF8.GetString(encoded)), baseCapture)) failures.Add("Explicit desktop writer changed capture.");
        capture.Record = null;
        if (JObject.Parse(Encoding.UTF8.GetString(EncodeCapture(capture, 32768)))["record"]!.Type != JTokenType.Null)
            failures.Add("Desktop writer lost explicit record null.");
        capture.Record = desktopRecord;
        capture.Record.OperationId = "\ud800";
        using (var untouched = new MemoryStream())
        using (var writer = new Utf8JsonWriter(untouched))
        {
            try { Desktop.DeploymentContract.WriteNativeHistoryCapture(writer, capture); failures.Add("Writer replaced invalid Unicode instead of rejecting."); }
            catch (FormatException) { }
            writer.Flush();
            if (untouched.Length != 0) failures.Add("Invalid DTO emitted a partial prefix before validation.");
        }
        var unicode = MakeWorstCapture(baseCapture, string.Concat(Enumerable.Repeat("😀", 128)));
        var unicodeBytes = EncodeCapture(Desktop.DeploymentContract.ParseNativeHistoryCapture(unicode.ToString(Formatting.None)), 32768);
        ContractCatalog.Require(SchemaIds.NativeHistoryCapture, ContractJson.Parse(Encoding.UTF8.GetString(unicodeBytes)));
        if (unicodeBytes.Length <= 16384 || unicodeBytes.Length > 32768) failures.Add("Real default STJ Unicode encoding did not verify the revised response bound.");
        var controls = MakeWorstCapture(baseCapture, new string('\u0001', 128));
        var controlBytes = EncodeCapture(Desktop.DeploymentContract.ParseNativeHistoryCapture(controls.ToString(Formatting.None)), 32768);
        var worstCheckpoint = new JObject { ["source"] = controls["source"]!.DeepClone(), ["records"] = new JArray() };
        worstCheckpoint["source"]!["throughSequence"] = 129;
        for (int i = 0; i < 32; i++)
        {
            var record = (JObject)controls["record"]!.DeepClone();
            record["terminalSequence"] = 5 + i * 4;
            record["operationId"] = i.ToString("D2") + new string('\u0001', 126);
            record["submissionKey"]!["requestId"] = i.ToString("D2") + new string('\u0001', 126);
            ((JArray)worstCheckpoint["records"]!).Add(record);
        }
        ContractCatalog.Require(SchemaIds.OperationHistoryCheckpoint, worstCheckpoint);
        byte[] checkpointBytes;
        using (var output = new MemoryStream())
        {
            using (var text = new StreamWriter(output, new UTF8Encoding(false, true), 1024, true))
            using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None, StringEscapeHandling = StringEscapeHandling.Default })
                worstCheckpoint.WriteTo(writer);
            checkpointBytes = output.ToArray();
        }
        if (checkpointBytes.Length + 44 > 262144) failures.Add("32 unique control-character records exceeded the candidate SLHC limit.");
        using (var document = Desktop.DeploymentContract.ParseHistoryCheckpoint(new UTF8Encoding(false, true).GetString(checkpointBytes)))
            if (document.RootElement.GetProperty("records").GetArrayLength() != 32) failures.Add("Worst checkpoint lost records.");
        return new JObject { ["stjNonBmpCaptureBytes"] = unicodeBytes.Length, ["stjControlCaptureBytes"] = controlBytes.Length,
            ["newtonsoftDefaultControlCheckpointPayloadBytes"] = checkpointBytes.Length, ["slhcFileBytesIncluding44ByteFrame"] = checkpointBytes.Length + 44,
            ["scope"] = "有限契约编码夹具；不含文件授权、SLHC实现、业务选择、进程或NativeAOT验收" };
    }

    private static byte[] EncodeCapture(Desktop.NativeHistoryCapture value, int capacity)
    {
        using var output = new MemoryStream(new byte[capacity], 0, capacity, true, true);
        output.SetLength(0);
        using (var writer = new Utf8JsonWriter(output)) { Desktop.DeploymentContract.WriteNativeHistoryCapture(writer, value); writer.Flush(); }
        return output.ToArray();
    }

    private static JObject MakeWorstCapture(JObject basis, string identity)
    {
        var value = (JObject)basis.DeepClone();
        value["environmentId"] = new string('e', 64); value["profileId"] = new string('p', 64);
        foreach (var key in new[] { "authorizationRecordRevision", "authorityEpoch", "grantEpoch" }) value[key] = 9007199254740991L;
        var source = value["source"]!;
        source["environmentId"] = value["environmentId"]!.DeepClone(); source["storeId"] = identity; source["producerInstanceId"] = identity;
        source["producerVersion"] = identity.Substring(0, identity.Length / 2); source["throughSequence"] = 9007199254740991L; source["publishedAtUnixMs"] = 9007199254740991L;
        var record = value["record"]!;
        record["operationId"] = identity; record["originInstanceId"] = identity; record["terminalSequence"] = 9007199254740991L;
        record["submissionKey"]!["writeContextId"] = identity; record["submissionKey"]!["requestId"] = identity;
        foreach (var context in new[] { record["sourceContext"]!, record["load"]!["observationContext"]! })
        { context["instanceId"] = identity; context["cityId"] = identity; }
        record["sourceContext"]!["generation"] = "9223372036854775806";
        record["load"]!["observationContext"]!["generation"] = "9223372036854775807";
        record["load"]!["observationEnd"] = "ownerStopped";
        return value;
    }
}
