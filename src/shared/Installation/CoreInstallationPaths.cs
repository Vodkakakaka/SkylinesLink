using System;
using System.IO;

namespace SkylinesLink.Shared.Installation
{
    /// <summary>安装入口共享的环境标识与路径规则，不读取安装记录或拥有文件句柄。</summary>
    internal static class CoreInstallationPaths
    {
        internal static string EnvironmentRoot(string environment)
        {
            environment = NormalizeEnvironment(environment);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skylineslink", "environments", environment);
        }

        internal static string StableLauncherPath(string environment) =>
            Path.Combine(EnvironmentRoot(environment), "launcher", "SkylinesLink.Launcher.exe");

        internal static string NormalizeEnvironment(string environment)
        {
            ValidateIdentifier(environment);
            if (IsReservedDeviceName(environment)) throw new InvalidDataException("环境标识包含保留名称");
            return environment.ToLowerInvariant();
        }

        internal static void ValidateIdentifier(string value)
        {
            if (value.Length is < 1 or > 64 || value is "." or "..") throw new InvalidDataException("标识不合法");
            foreach (var c in value)
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c is '.' or '-' or '_'))
                    throw new InvalidDataException("标识不合法");
            if (value.EndsWith(".", StringComparison.Ordinal)) throw new InvalidDataException("标识不合法");
        }

        internal static string ResolveCorePath(string root, string path)
        {
            if (path.Length == 0 || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || Path.IsPathRooted(path))
                throw new InvalidDataException("清单路径必须是相对路径");
            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)
                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("清单路径不合法");
                if (IsReservedDeviceName(segment)) throw new InvalidDataException("清单路径包含保留名称");
            }
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var result = Path.GetFullPath(Path.Combine(fullRoot, path));
            if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("清单路径越界");
            RejectReparseAncestors(result);
            return result;
        }

        internal static bool IsReservedDeviceName(string value)
        {
            var stem = value.Split('.')[0].ToUpperInvariant();
            return stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
        }

        internal static void RejectReparseAncestors(string path)
        {
            string? current = Path.GetFullPath(path);
            bool leaf = true;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    // Inspect the link itself even when its target is missing;
                    // Exists can hide a dangling link as an absent path.
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("运行路径包含重解析点");
                    if (!leaf && (attributes & FileAttributes.Directory) == 0)
                        throw new InvalidDataException("运行路径的祖先不是目录");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                leaf = false;
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
