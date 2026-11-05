using System;
using System.Reflection;

namespace SkylinesLink.Infrastructure.Installation
{
    /// <summary>已载模块的普通身份事实；由原生装配按真实Type取得，不接受客户端输入。</summary>
    public sealed class LoadedManagedComponent
    {
        public LoadedManagedComponent(string name, Guid moduleId, Version assemblyVersion, string informationalVersion)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ModuleId = moduleId;
            AssemblyVersion = assemblyVersion ?? throw new ArgumentNullException(nameof(assemblyVersion));
            InformationalVersion = informationalVersion ?? throw new ArgumentNullException(nameof(informationalVersion));
        }
        public string Name { get; }
        public Guid ModuleId { get; }
        public Version AssemblyVersion { get; }
        public string InformationalVersion { get; }
        public static LoadedManagedComponent Capture(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic) throw new ArgumentException("A loaded product assembly is required.");
            var name = assembly.GetName();
            var information = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return new LoadedManagedComponent(name.Name!, assembly.ManifestModule.ModuleVersionId, name.Version!, information?.InformationalVersion ?? "");
        }
    }
}
