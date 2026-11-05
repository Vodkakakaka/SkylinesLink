using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SkylinesLink.Deployment.Generated;
using SkylinesLink.Shared.Installation;

namespace SkylinesLink.Deployment;

/// <summary>只读取和核验既有核心制品；不下载、不安装、不写授权。</summary>
public sealed class CorePackage : IDisposable
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumCoreBytes = 1024L * 1024 * 1024;
    private readonly List<FileStream> pins = new();
    private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStream> filePins = new(StringComparer.OrdinalIgnoreCase);
    private FileStream manifestPin = null!;
    public string Root { get; }
    public CoreManifest Manifest { get; private set; } = null!;
    public string ManifestSha256 { get; private set; } = null!;

    private CorePackage(string root) => Root = Path.GetFullPath(root);

    public static CorePackage Open(string directory, string? expectedManifestHash = null) => OpenVerified(directory, expectedManifestHash, null);

    /// <summary>Setup 借出其固定准备清单的已核句柄。调用方持有至返回包 Dispose 以后；本方法不关闭借用句柄。</summary>
    public static CorePackage OpenPreparing(string directory, FileStream preparationManifest, string expectedManifestHash)
        => OpenVerified(directory, expectedManifestHash, preparationManifest);

    private static CorePackage OpenVerified(string directory, string? expectedManifestHash, FileStream? preparationManifest)
    {
        var package = new CorePackage(directory);
        try
        {
            RejectReparseAncestors(package.Root);
            var manifestPath = Path.Combine(package.Root, "core-manifest.json");
            var manifest = preparationManifest ?? package.Pin(manifestPath);
            package.manifestPin = manifest;
            if (manifest.Length > MaximumManifestBytes) throw new InvalidDataException("发行清单超出读取上限");
            manifest.Position = 0;
            var bytes = new byte[checked((int)manifest.Length)];
            manifest.ReadExactly(bytes);
            package.ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (expectedManifestHash is not null && !package.ManifestSha256.Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("核心清单校验失败");
            package.Manifest = DeploymentContract.ParseCoreManifest(new UTF8Encoding(false, true).GetString(bytes));
            ValidateIdentifier(package.Manifest.Version);
            if (IsReservedDeviceName(package.Manifest.Version)) throw new InvalidDataException("发行号包含保留名称");
            long total = 0;
            foreach (var item in package.Manifest.Files)
            {
                total = checked(total + item.Size);
                if (total > MaximumCoreBytes) throw new InvalidDataException("核心制品超出当前验证预算");
                var path = ResolveCorePath(package.Root, item.Path);
                var firstSegment = item.Path.Split('/')[0];
                if (firstSegment.Equals("core-manifest.json", StringComparison.OrdinalIgnoreCase)
                    || firstSegment.Equals("preparation-manifest.json", StringComparison.OrdinalIgnoreCase)
                    || !package.files.TryAdd(item.Path, path)) throw new InvalidDataException("清单存在重复或自引用文件");
                var file = package.Pin(path);
                package.filePins.Add(item.Path, file);
                if (file.Length != item.Size) throw new InvalidDataException("核心文件大小不符");
                VerifyHash(file, item.Sha256);
            }
            return package;
        }
        catch
        {
            package.Dispose();
            throw;
        }
    }

    public string RequireFile(string relativePath)
    {
        if (!files.TryGetValue(relativePath, out var path)) throw new InvalidDataException("发行缺少必要运行文件");
        RejectReparseAncestors(path);
        return path;
    }

    /// <summary>核当前成套载荷的必要文件集合；不代替来源、可执行格式或实际运行验收。</summary>
    public void RequireCompletePayload()
    {
        RequireSupportedContract();
        foreach (var path in CorePayloadFiles.Required)
        {
            RequireFile(path);
            if (filePins[path].Length == 0) throw new InvalidDataException("核心必要文件为空");
        }
    }

    /// <summary>稳定入口的基础ABI守卫；不代替准确发行组合的兼容性验收。</summary>
    public void RequireSupportedContract()
    {
        if (Manifest.ContractVersion != "2") throw new InvalidDataException("不支持此核心契约版本");
    }

    public ActivationRecord CreateActivationRecord() => new() {
        ProductId = Manifest.ProductId, Version = Manifest.Version, CoreManifestSha256 = ManifestSha256
    };

    public static ActivationRecord ReadActivationRecord(FileStream file)
    {
        if (file.Length is < 1 or > 4096) throw new InvalidDataException("激活记录超出读取范围");
        file.Position = 0;
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        var activation = DeploymentContract.ParseActivationRecord(new UTF8Encoding(false, true).GetString(bytes));
        ValidateIdentifier(activation.Version);
        if (IsReservedDeviceName(activation.Version)) throw new InvalidDataException("激活版本包含保留名称");
        foreach (var digit in activation.CoreManifestSha256)
            if (!(digit is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                throw new InvalidDataException("激活清单哈希不合法");
        return activation;
    }

    // Setup 复制的是已经核验并保持打开的源文件，不重新解析可变的源路径。
    public void CopyManifestTo(Stream destination) => CopyPinned(manifestPin, destination);

    public void CopyFileTo(string relativePath, Stream destination)
    {
        if (!filePins.TryGetValue(relativePath, out var stream)) throw new InvalidDataException("复制文件不在核心清单中");
        CopyPinned(stream, destination);
    }

    private void CopyPinned(FileStream stream, Stream destination)
    {
        ObjectDisposedException.ThrowIf(pins.Count == 0, this);
        stream.Position = 0;
        stream.CopyTo(destination, 64 * 1024);
    }

    public static CorePackage OpenActive(string environment)
    {
        ValidateIdentifier(environment);
        var root = EnvironmentRoot(environment);
        var activationPath = Path.Combine(root, "activation.json");
        RejectReparseAncestors(activationPath);
        using var file = new FileStream(activationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var activation = ReadActivationRecord(file);
        var package = Open(Path.Combine(root, "releases", activation.Version), activation.CoreManifestSha256);
        if (!package.Manifest.Version.Equals(activation.Version, StringComparison.Ordinal))
        {
            package.Dispose();
            throw new InvalidDataException("激活记录与核心发行不匹配");
        }
        return package;
    }

    public static string EnvironmentRoot(string environment) => CoreInstallationPaths.EnvironmentRoot(environment);

    public static string StableLauncherPath(string environment) => CoreInstallationPaths.StableLauncherPath(environment);

    public static string NormalizeEnvironment(string environment) => CoreInstallationPaths.NormalizeEnvironment(environment);

    public static void ValidateIdentifier(string value) => CoreInstallationPaths.ValidateIdentifier(value);

    public static string ResolveCorePath(string root, string path) => CoreInstallationPaths.ResolveCorePath(root, path);

    private static bool IsReservedDeviceName(string value) => CoreInstallationPaths.IsReservedDeviceName(value);

    public static void RejectReparseAncestors(string path) => CoreInstallationPaths.RejectReparseAncestors(path);

    private FileStream Pin(string path)
    {
        RejectReparseAncestors(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        pins.Add(stream);
        return stream;
    }

    private static void VerifyHash(FileStream stream, string expected)
    {
        if (expected.Length != 64) throw new InvalidDataException("文件校验值不合法");
        byte[] hash;
        try { hash = Convert.FromHexString(expected); }
        catch (FormatException) { throw new InvalidDataException("文件校验值不合法"); }
        stream.Position = 0;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), hash)) throw new InvalidDataException("核心文件校验失败");
    }

    public void Dispose()
    {
        foreach (var stream in pins) stream.Dispose();
        pins.Clear();
    }
}
