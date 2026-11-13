using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using SkylinesLink.Application.Context;
using SkylinesLink.Contracts;

namespace SkylinesLink.Application.Buildings
{
    public sealed class BuildingInspectionQuery
    {
        private BuildingInspectionQuery(bool selected, int limit, string? instance = null, long generation = 0, int index = 0, int version = 0)
        { SelectedInfo = selected; RelationLimit = limit; InstanceId = instance; Generation = generation; Index = index; Version = version; }
        public bool SelectedInfo { get; }
        public int RelationLimit { get; }
        public string? InstanceId { get; }
        public long Generation { get; }
        public int Index { get; }
        public int Version { get; }
        public static BuildingInspectionQuery Parse(JToken arguments)
        {
            if (!ContractCatalog.Validate("urn:skylineslink:contract:building-inspection-query:1", arguments).Valid)
                throw new ArgumentException("Invalid building inspection query.");
            int limit = (int?)arguments["relationLimit"] ?? 8;
            var target = arguments["target"]!;
            if ((string?)target["kind"] == "selectedInfo") return new BuildingInspectionQuery(true, limit);
            var reference = target["ref"]!;
            string text = (string)reference["generation"]!;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) || generation <= 0 ||
                generation.ToString(CultureInfo.InvariantCulture) != text) throw new ArgumentException("Invalid loading generation.");
            return new BuildingInspectionQuery(false, limit, (string)reference["instanceId"]!, generation,
                (int)reference["index"]!, (int)reference["version"]!);
        }
        public bool SameAs(BuildingInspectionQuery other) => other != null && SelectedInfo == other.SelectedInfo &&
            RelationLimit == other.RelationLimit && InstanceId == other.InstanceId && Generation == other.Generation && Index == other.Index && Version == other.Version;
    }

    /// <summary>只在本次捕获Scope内有效；不含World/Entity/原生借用。</summary>
    public sealed class BuildingEntityReference
    {
        public BuildingEntityReference(int index, int version) { if (index < 0) throw new ArgumentOutOfRangeException(nameof(index)); Index = index; Version = version; }
        public int Index { get; }
        public int Version { get; }
    }
    public sealed class BuildingTextFact
    {
        private BuildingTextFact(string? value, string? reason) { Value = value; Reason = reason; }
        public string? Value { get; }
        public string? Reason { get; }
        public static BuildingTextFact Unavailable(string reason)
        {
            if (reason != "MISSING_COMPONENT" && reason != "READ_FAILED" && reason != "INVALID_VALUE" && reason != "TOO_LONG" && reason != "MISSING_VALUE")
                throw new ArgumentException("Unknown text coverage reason.");
            return new BuildingTextFact(null, reason);
        }
        public static BuildingTextFact Read(Func<string?> read, int maximumScalars)
        {
            string? value;
            try { value = read(); } catch { return new BuildingTextFact(null, "READ_FAILED"); }
            if (string.IsNullOrEmpty(value)) return new BuildingTextFact(null, "MISSING_VALUE");
            // Reject, never shorten, native names/IDs. Stop before scanning an oversized string.
            if (value!.Length > maximumScalars * 2) return new BuildingTextFact(null, "TOO_LONG");
            int scalars = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c)) { if (++i == value.Length || !char.IsLowSurrogate(value[i])) return new BuildingTextFact(null, "INVALID_VALUE"); }
                else if (char.IsLowSurrogate(c)) return new BuildingTextFact(null, "INVALID_VALUE");
                if (++scalars > maximumScalars) return new BuildingTextFact(null, "TOO_LONG");
            }
            return new BuildingTextFact(value, null);
        }
    }
    public sealed class BuildingRelationEntry
    {
        public BuildingRelationEntry(BuildingEntityReference? reference, bool exists, uint? optionMask = null)
        { Reference = reference; Exists = exists; OptionMask = optionMask; if (reference == null && exists) throw new ArgumentException("A null entity cannot exist."); }
        public BuildingEntityReference? Reference { get; }
        public bool Exists { get; }
        public uint? OptionMask { get; }
    }
    public sealed class BuildingDirectRelations
    {
        public BuildingDirectRelations(bool? present, int? length, IReadOnlyList<BuildingRelationEntry> entries, string? reason)
        {
            if (entries == null || entries.Count > 16) throw new ArgumentException("Relation prefix exceeds its bound.");
            if (present == true ? !length.HasValue || length < entries.Count ||
                (length == entries.Count ? reason != null : reason != "LIMIT") :
                length.HasValue || entries.Count != 0 || (present == false ? reason != "MISSING_BUFFER" : reason != "READ_FAILED"))
                throw new ArgumentException("Relation coverage is inconsistent.");
            Present = present; Length = length; Entries = Array.AsReadOnly(entries.ToArray()); Reason = reason;
        }
        public bool? Present { get; }
        public int? Length { get; }
        public IReadOnlyList<BuildingRelationEntry> Entries { get; }
        public string? Reason { get; }
        public bool Complete => Present == true && Length == Entries.Count;
        public static BuildingDirectRelations Missing() => new BuildingDirectRelations(false, null, Array.Empty<BuildingRelationEntry>(), "MISSING_BUFFER");
        public static BuildingDirectRelations Failed() => new BuildingDirectRelations(null, null, Array.Empty<BuildingRelationEntry>(), "READ_FAILED");
    }
    public sealed class BuildingBoundsFact
    {
        public BuildingBoundsFact(IReadOnlyList<double>? min, IReadOnlyList<double>? max, string? reason)
        {
            if (min == null || max == null)
            {
                if (min != null || max != null || reason != "MISSING_COMPONENT" && reason != "READ_FAILED" && reason != "INVALID_VALUE")
                    throw new ArgumentException("Bounds availability is inconsistent.");
            }
            else if (reason != null || min.Count != 3 || max.Count != 3 || min.Concat(max).Any(v => double.IsNaN(v) || double.IsInfinity(v)) ||
                Enumerable.Range(0, 3).Any(i => min[i] > max[i])) throw new ArgumentException("Invalid asset-local bounds.");
            Min = min == null ? null : Array.AsReadOnly(min.ToArray()); Max = max == null ? null : Array.AsReadOnly(max.ToArray()); Reason = reason;
        }
        public IReadOnlyList<double>? Min { get; }
        public IReadOnlyList<double>? Max { get; }
        public string? Reason { get; }
    }
    public sealed class BuildingInspectionData
    {
        public BuildingInspectionData(BuildingEntityReference prefab, BuildingTextFact name, BuildingTextFact nativeId,
            int? lotWidth, int? lotDepth, string? lotIssue, uint? baseCost, string? costIssue, bool? locked, string? lockedIssue,
            IReadOnlyList<double> position, IReadOnlyList<double> rotation, BuildingEntityReference? roadEdge, bool roadEdgeExists,
            double curvePosition, uint optionMask, byte flags, BuildingDirectRelations subObjects,
            BuildingDirectRelations subNets, BuildingDirectRelations installedUpgrades, BuildingBoundsFact? prefabLocalBounds = null)
        {
            if (position.Count != 3 || rotation.Count != 4 || position.Concat(rotation).Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                throw new ArgumentException("Invalid native transform.");
            if (lotWidth.HasValue != lotDepth.HasValue || (lotWidth.HasValue ? lotIssue != null : lotIssue == null) ||
                (baseCost.HasValue ? costIssue != null : costIssue == null) || (locked.HasValue ? lockedIssue != null : lockedIssue == null))
                throw new ArgumentException("Prefab field availability is inconsistent.");
            if (subObjects.Entries.Concat(subNets.Entries).Any(entry => entry.OptionMask.HasValue) ||
                installedUpgrades.Entries.Any(entry => !entry.OptionMask.HasValue)) throw new ArgumentException("Upgrade masks belong only to installed-upgrade entries.");
            Prefab = prefab; Name = name; NativeId = nativeId; LotWidth = lotWidth; LotDepth = lotDepth; LotIssue = lotIssue;
            BaseCost = baseCost; CostIssue = costIssue; Locked = locked; LockedIssue = lockedIssue;
            Position = Array.AsReadOnly(position.ToArray()); Rotation = Array.AsReadOnly(rotation.ToArray());
            RoadEdge = roadEdge; RoadEdgeExists = roadEdgeExists; CurvePosition = curvePosition; OptionMask = optionMask; Flags = flags;
            SubObjects = subObjects; SubNets = subNets; InstalledUpgrades = installedUpgrades;
            PrefabLocalBounds = prefabLocalBounds ?? new BuildingBoundsFact(null, null, "MISSING_COMPONENT");
        }
        public BuildingEntityReference Prefab { get; }
        public BuildingTextFact Name { get; }
        public BuildingTextFact NativeId { get; }
        public int? LotWidth { get; }
        public int? LotDepth { get; }
        public string? LotIssue { get; }
        public uint? BaseCost { get; }
        public string? CostIssue { get; }
        public bool? Locked { get; }
        public string? LockedIssue { get; }
        public IReadOnlyList<double> Position { get; }
        public IReadOnlyList<double> Rotation { get; }
        public BuildingEntityReference? RoadEdge { get; }
        public bool RoadEdgeExists { get; }
        public double CurvePosition { get; }
        public uint OptionMask { get; }
        public byte Flags { get; }
        public BuildingDirectRelations SubObjects { get; }
        public BuildingDirectRelations SubNets { get; }
        public BuildingDirectRelations InstalledUpgrades { get; }
        public BuildingBoundsFact PrefabLocalBounds { get; }
    }
    public sealed class BuildingInspectionCapture
    {
        public BuildingInspectionCapture(WorldContextSnapshot scope, BuildingInspectionQuery query, BuildingEntityReference reference,
            string gameVersion, long startedAtUnixMs, long endedAtUnixMs, uint frameStart, uint frameEnd, BuildingInspectionData? data,
            string? unsupportedReason = null)
        { Scope = scope; Query = query; Reference = reference; GameVersion = gameVersion; StartedAtUnixMs = startedAtUnixMs;
            EndedAtUnixMs = endedAtUnixMs; FrameStart = frameStart; FrameEnd = frameEnd; Data = data; UnsupportedReason = unsupportedReason;
            if ((data == null) != (unsupportedReason != null)) throw new ArgumentException("Capture must contain data or an unsupported reason."); }
        public WorldContextSnapshot Scope { get; }
        public BuildingInspectionQuery Query { get; }
        public BuildingEntityReference Reference { get; }
        public string GameVersion { get; }
        public long StartedAtUnixMs { get; }
        public long EndedAtUnixMs { get; }
        public uint FrameStart { get; }
        public uint FrameEnd { get; }
        public BuildingInspectionData? Data { get; }
        public string? UnsupportedReason { get; }
    }
}
