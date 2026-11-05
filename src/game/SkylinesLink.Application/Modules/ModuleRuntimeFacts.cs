using System;
using System.Collections.Generic;

namespace SkylinesLink.Application.Modules
{
    /// <summary>固定字段的覆盖问题；不携带原生异常、路径或任意诊断文本。</summary>
    public sealed class ModuleFieldIssue
    {
        public ModuleFieldIssue(string field, string reason)
        {
            if (!ModuleFactValues.IsField(field)) throw new ArgumentException("Unknown module field.", nameof(field));
            if (!ModuleFactValues.IsIssueReason(reason)) throw new ArgumentException("Unknown module field reason.", nameof(reason));
            Field = field; Reason = reason;
        }
        public string Field { get; }
        public string Reason { get; }
    }

    /// <summary>本次注册序列的一行纯值观察；Ordinal 和 AssetGuid 不声明跨观察的包身份。</summary>
    public sealed class ModuleRuntimeFact
    {
        public ModuleRuntimeFact(int ordinal, string? assetGuid, bool? isMod, int? loaderStateCode,
            bool? assemblyPresent, string? assemblyName, string? assemblyVersion, int? instanceCount,
            bool? hasLoadError, string databaseKind, IReadOnlyList<ModuleFieldIssue> fieldIssues)
        {
            if (ordinal < 0 || ordinal >= ModuleRegistryCapture.MaxEntries) throw new ArgumentOutOfRangeException(nameof(ordinal));
            if (assetGuid != null && !ModuleFactValues.IsGuid(assetGuid)) throw new ArgumentException("Invalid asset GUID.", nameof(assetGuid));
            if (assemblyName != null && !ModuleFactValues.IsText(assemblyName, 128)) throw new ArgumentException("Invalid assembly name.", nameof(assemblyName));
            if (assemblyVersion != null && !ModuleFactValues.IsVersion(assemblyVersion)) throw new ArgumentException("Invalid assembly version.", nameof(assemblyVersion));
            if (assemblyPresent != true && (assemblyName != null || assemblyVersion != null))
                throw new ArgumentException("An absent assembly cannot supply loaded identity.", nameof(assemblyPresent));
            if (instanceCount < 0) throw new ArgumentOutOfRangeException(nameof(instanceCount));
            if (!ModuleFactValues.IsDatabaseKind(databaseKind)) throw new ArgumentException("Unknown database kind.", nameof(databaseKind));
            if (fieldIssues == null) throw new ArgumentNullException(nameof(fieldIssues));
            int count = fieldIssues.Count;
            if (count > 8) throw new ArgumentOutOfRangeException(nameof(fieldIssues));
            var copy = new ModuleFieldIssue[count]; var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                var issue = fieldIssues[i] ?? throw new ArgumentException("A field issue cannot be null.", nameof(fieldIssues));
                if (fields.ContainsKey(issue.Field)) throw new ArgumentException("A field can have only one issue.", nameof(fieldIssues));
                fields.Add(issue.Field, issue.Reason);
                copy[i] = issue;
            }
            bool Has(string field) => fields.ContainsKey(field);
            void InvalidCoverage() => throw new ArgumentException("Module field coverage is inconsistent.", nameof(fieldIssues));
            if (Has("entry"))
            {
                if (fields.Count != 1 || fields["entry"] != "missing" || assetGuid != null || isMod != null ||
                    loaderStateCode != null || assemblyPresent != null || assemblyName != null || assemblyVersion != null ||
                    instanceCount != null || hasLoadError != null || databaseKind != "unknown") InvalidCoverage();
            }
            else
            {
                if (loaderStateCode == null || hasLoadError == null || (instanceCount == null) != Has("instanceCount")) InvalidCoverage();
                if (Has("asset"))
                {
                    if (fields["asset"] != "missing" || assetGuid != null || isMod != null || assemblyPresent != null ||
                        assemblyName != null || assemblyVersion != null || databaseKind != "unknown" ||
                        fields.Count != (Has("instanceCount") ? 2 : 1)) InvalidCoverage();
                }
                else
                {
                    if (isMod == null || assemblyPresent == null || (assetGuid == null) != Has("assetGuid") ||
                        (databaseKind == "unknown") != Has("databaseKind")) InvalidCoverage();
                    if (assemblyPresent == true)
                    {
                        if ((assemblyName == null) != Has("assemblyName") || (assemblyVersion == null) != Has("assemblyVersion")) InvalidCoverage();
                    }
                    else if (assemblyName != null || assemblyVersion != null || Has("assemblyName") || Has("assemblyVersion")) InvalidCoverage();
                }
            }
            Ordinal = ordinal; AssetGuid = assetGuid; IsMod = isMod; LoaderStateCode = loaderStateCode;
            AssemblyPresent = assemblyPresent; AssemblyName = assemblyName; AssemblyVersion = assemblyVersion;
            InstanceCount = instanceCount; HasLoadError = hasLoadError; DatabaseKind = databaseKind;
            FieldIssues = Array.AsReadOnly(copy);
        }
        public int Ordinal { get; }
        public string? AssetGuid { get; }
        public bool? IsMod { get; }
        public int? LoaderStateCode { get; }
        public string LoaderState => LoaderStateCode switch
        {
            null => "unavailable", 0 => "unknown", 1 => "loaded", 2 => "disposed", 3 => "notModWarning",
            4 => "notUniqueWarning", 5 => "generalError", 6 => "missingDependenciesError",
            7 => "loadAssemblyError", 8 => "loadAssemblyReferenceError", _ => "unrecognized"
        };
        public bool? AssemblyPresent { get; }
        public string? AssemblyName { get; }
        public string? AssemblyVersion { get; }
        public int? InstanceCount { get; }
        public bool? HasLoadError { get; }
        public string DatabaseKind { get; }
        public IReadOnlyList<ModuleFieldIssue> FieldIssues { get; }
    }

    /// <summary>单次主线程捕获的有界前缀；分页和观察寿命由拥有它的应用服务负责。</summary>
    public sealed class ModuleRegistryCapture
    {
        public const int MaxEntries = 128;
        public const int MaxVisited = 129;
        public const int PageSize = 16;
        public const int SliceMilliseconds = 5;

        public ModuleRegistryCapture(long captureStartedAtUnixMs, long captureEndedAtUnixMs,
            string? gameVersion, string? gameVersionIssue, bool firstInitializationComplete, bool restartRequired,
            IReadOnlyList<ModuleRuntimeFact> entries, int visited, bool complete, string? reason)
        {
            if (captureStartedAtUnixMs < 0 || captureStartedAtUnixMs > ModuleFactValues.MaximumJsonInteger)
                throw new ArgumentOutOfRangeException(nameof(captureStartedAtUnixMs));
            if (captureEndedAtUnixMs < captureStartedAtUnixMs || captureEndedAtUnixMs > ModuleFactValues.MaximumJsonInteger)
                throw new ArgumentOutOfRangeException(nameof(captureEndedAtUnixMs));
            if (gameVersion != null && !ModuleFactValues.IsText(gameVersion, 64)) throw new ArgumentException("Invalid game version.", nameof(gameVersion));
            if (gameVersionIssue != null && !ModuleFactValues.IsIssueReason(gameVersionIssue)) throw new ArgumentException("Unknown game version issue.", nameof(gameVersionIssue));
            if ((gameVersion == null) != (gameVersionIssue != null)) throw new ArgumentException("Game version coverage must be explicit.", nameof(gameVersionIssue));
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            int count = entries.Count;
            // Check the advertised bound before allocating or reading any entry.
            if (count > MaxEntries) throw new ArgumentOutOfRangeException(nameof(entries));
            if (visited < count || visited > MaxVisited || visited > count + 1) throw new ArgumentOutOfRangeException(nameof(visited));
            if (complete ? reason != null || visited != count : reason != "ENTRY_LIMIT" && reason != "TIME_LIMIT")
                throw new ArgumentException("Invalid capture coverage.", nameof(reason));
            if (reason == "ENTRY_LIMIT" && (count != MaxEntries || visited != MaxVisited))
                throw new ArgumentException("Entry limit requires the bounded lookahead.", nameof(reason));
            var copy = new ModuleRuntimeFact[count];
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i] ?? throw new ArgumentException("A captured row cannot be null.", nameof(entries));
                if (entry.Ordinal != i) throw new ArgumentException("Captured rows must retain their sequence ordinals.", nameof(entries));
                copy[i] = entry;
            }
            CaptureStartedAtUnixMs = captureStartedAtUnixMs; CaptureEndedAtUnixMs = captureEndedAtUnixMs;
            GameVersion = gameVersion; GameVersionIssue = gameVersionIssue;
            FirstInitializationComplete = firstInitializationComplete; RestartRequired = restartRequired;
            Entries = Array.AsReadOnly(copy); Visited = visited; Complete = complete; Reason = reason;
        }
        public long CaptureStartedAtUnixMs { get; }
        public long CaptureEndedAtUnixMs { get; }
        public string? GameVersion { get; }
        public string? GameVersionIssue { get; }
        public bool FirstInitializationComplete { get; }
        public bool RestartRequired { get; }
        public IReadOnlyList<ModuleRuntimeFact> Entries { get; }
        public int Visited { get; }
        public bool Complete { get; }
        public string? Reason { get; }
    }

    public sealed class ModuleRegistryReadFailure : Exception
    {
        public ModuleRegistryReadFailure(string code) : base(ValidateCode(code)) { Code = code; }
        public string Code { get; }
        private static string ValidateCode(string code) => code == "SOURCE_UNAVAILABLE" || code == "REGISTRY_CHANGED" || code == "CAPTURE_FAILED"
            ? code : throw new ArgumentException("Unknown module capture failure.", nameof(code));
    }

    internal static class ModuleFactValues
    {
        internal const long MaximumJsonInteger = 9007199254740991L;
        internal static bool IsField(string value) => value == "entry" || value == "asset" || value == "assetGuid" ||
            value == "assemblyName" || value == "assemblyVersion" || value == "instanceCount" || value == "databaseKind";
        internal static bool IsIssueReason(string value) => value == "missing" || value == "invalid" || value == "tooLong" || value == "unavailable";
        internal static bool IsDatabaseKind(string value) => value == "user" || value == "paradoxMods" || value == "game" || value == "other" || value == "unknown";
        internal static bool IsGuid(string value)
        {
            if (value.Length != 32) return false;
            bool nonzero = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (!(value[i] >= '0' && value[i] <= '9') && !(value[i] >= 'a' && value[i] <= 'f')) return false;
                nonzero |= value[i] != '0';
            }
            return nonzero;
        }
        internal static bool IsVersion(string value)
        {
            if (value.Length == 0 || value.Length > 32) return false;
            int dots = 0, digits = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= '0' && c <= '9') { digits++; continue; }
                if (c != '.' || digits == 0 || ++dots > 3) return false;
                digits = 0;
            }
            return digits > 0;
        }
        internal static bool IsText(string value, int maximumScalars)
        {
            if (value.Length == 0 || value.Length > maximumScalars * 2) return false;
            int scalars = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (++i == value.Length || !char.IsLowSurrogate(value[i])) return false;
                }
                else if (char.IsLowSurrogate(c)) return false;
                if (++scalars > maximumScalars) return false;
            }
            return true;
        }
    }
}
