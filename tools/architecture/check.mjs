import { readdir, readFile } from 'node:fs/promises';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
const projectEdges = {
  'SkylinesLink.Contracts': [],
  'SkylinesLink.Application': ['SkylinesLink.Contracts'],
  'SkylinesLink.Infrastructure': ['SkylinesLink.Application', 'SkylinesLink.Contracts'],
  'SkylinesLink.Mod': ['SkylinesLink.Application', 'SkylinesLink.Infrastructure', 'SkylinesLink.Contracts'],
  'SkylinesLink.Deployment': [],
  'SkylinesLink.Launcher': ['SkylinesLink.Deployment'],
  'SkylinesLink.Setup': ['SkylinesLink.Deployment'],
};
const domains = new Set(['Networks', 'Zoning', 'Buildings', 'UtilitiesIndustry', 'Transport', 'Economy', 'Simulation', 'Terrain', 'View']);

// 有限规则约束实际引用和导入；编译器继续负责类型解析。
export function inspectSource(path, text) {
  const findings = [];
  const report = (rule) => findings.push({ path, rule });
  if (path.endsWith('.csproj')) {
    const project = path.split('/').at(-1).replace('.csproj', '');
    if (!(project in projectEdges)) return findings;
    for (const match of text.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
      const dependency = match[1].replaceAll('\\', '/').split('/').at(-1).replace('.csproj', '');
      if (!projectEdges[project].includes(dependency)) report(`禁止工程依赖 ${project} -> ${dependency}`);
    }
    if (project !== 'SkylinesLink.Mod' && /CSII_|Mod\.(?:props|targets)|<Reference\b[^>]*Include="(?:Game|Colossal|Unity)/.test(text)) {
      report('游戏依赖只能由 Mod 工程解析');
    }
    if (['SkylinesLink.Contracts', 'SkylinesLink.Application', 'SkylinesLink.Infrastructure'].includes(project)
      && !/<TargetFramework>netstandard2\.0<\/TargetFramework>/.test(text)) report('净库目标必须为 netstandard2.0');
  }
  if (path.endsWith('.cs')) {
    const code = text.replace(/\/\*[\s\S]*?\*\/|\/\/[^\r\n]*/g, '');
    if (path.startsWith('src/shared/Tasks/')) {
      if (/\b(?:Newtonsoft|UnityEngine|Unity\.Entities|Colossal|Game|SkylinesLink\.(?:Application|Infrastructure|Contracts|Mod|Deployment))\b/.test(code)
        || /\bSystem\.(?:IO|Net|Reflection|Runtime\.InteropServices|Diagnostics\.Process)\b/.test(code)
        || [...code.matchAll(/\busing\s+((?:static\s+)?[A-Za-z_][\w.]*(?:\s*=\s*[\w.]+)?)\s*;/g)]
          .some(match => !/^System(?:\.[\w.]+)?$/.test(match[1].trim())))
        report('共享Task规则只能依赖无IO的BCL普通值');
    }
    const neutral = /(?:contracts\/cs\/|SkylinesLink\.(?:Application|Infrastructure)\/)/.test(path);
    if (neutral && /\b(?:UnityEngine|Unity\.Entities|Colossal|Game)\./.test(code)) report('净库泄漏游戏类型');
    if (/SkylinesLink\.Application\//.test(path) && /\b(?:System\.(?:IO|Net|Diagnostics\.Process)|SkylinesLink\.(?:Infrastructure|Mod))\b/.test(code)) report('Application 引用了 IO 或外层实现');
    const currentDomain = path.match(/SkylinesLink\.Application\/([^/]+)\//)?.[1];
    if (domains.has(currentDomain)) {
      if (/\bSkylinesLink\.Application\.Workflows\b/.test(code)) report('领域反向依赖 Workflow');
      for (const match of code.matchAll(/\bSkylinesLink\.Application\.(\w+)(?:\.(\w+))?/g)) {
        if (domains.has(match[1]) && match[1] !== currentDomain && !['Ports', 'Facts', 'ReadModels'].includes(match[2])) report('跨域只能消费显式窄事实或端口');
      }
    }
  }
  if (path.endsWith('.ts')) {
    const imports = [...text.matchAll(/(?:from\s*|import\s*\(|require\s*\(|import\s*)['"]([^'"]+)['"]/g)].map(m => m[1]);
    for (const specifier of imports) {
      const target = specifier.startsWith('.') ? relative(root, resolve(root, dirname(path), specifier)).replaceAll('\\', '/') : specifier;
      if (path.startsWith('contracts/ts/') && (/^node:|@modelcontextprotocol|src\/(?:host|ui)\//.test(target))) report('中立契约引入平台依赖');
      if (path.startsWith('src/host/src/game-client/') && /(?:^|\/)mcp\//.test(target)) report('GameClient 反向依赖 MCP');
      if (path.startsWith('src/host/') && /src\/ui\//.test(target)) report('Host 依赖 UI');
      if (path.startsWith('src/ui/') && (/^node:|@modelcontextprotocol|src\/host\//.test(target))) report('UI 依赖 Host 或 Node');
    }
    if (path.startsWith('contracts/ts/') && /\b(?:process|Buffer|window|document)\./.test(text)) report('中立契约使用平台全局值');
  }
  return findings;
}

async function collect(directory) {
  const entries = await readdir(resolve(root, directory), { withFileTypes: true }).catch(error => {
    if (error.code === 'ENOENT') return [];
    throw error;
  });
  const paths = [];
  for (const entry of entries) {
    if (['node_modules', 'bin', 'obj'].includes(entry.name)) continue;
    const path = `${directory}/${entry.name}`;
    if (entry.isDirectory()) paths.push(...await collect(path));
    else if (/\.(?:csproj|cs|ts)$/.test(entry.name)) paths.push(path);
  }
  return paths;
}

export async function checkArchitecture() {
  const paths = [...await collect('contracts/cs'), ...await collect('contracts/ts'), ...await collect('src')];
  const findings = [];
  for (const path of paths) findings.push(...inspectSource(path, await readFile(resolve(root, path), 'utf8')));
  if (paths.length === 0) throw new Error('没有可核对的源码，不能报告架构通过');
  return { paths: paths.length, findings };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const result = await checkArchitecture();
  for (const finding of result.findings) console.error(`${finding.path}: ${finding.rule}`);
  console.log(`架构检查：${result.paths} 个文件，${result.findings.length} 个违规。`);
  process.exitCode = result.findings.length ? 1 : 0;
}
