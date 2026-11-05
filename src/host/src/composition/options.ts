import { parseArgs } from 'node:util';
import { isAbsolute } from 'node:path';
import { canonicalEnvironment, isDeploymentIdentifier } from '../shared/identifiers.ts';
export { canonicalEnvironment, isDeploymentIdentifier } from '../shared/identifiers.ts';

export function parseHostOptions(args: string[]) {
  const { values, tokens } = parseArgs({ args, tokens: true, options: { transport: { type: 'string', default: 'stdio' }, profile: { type: 'string' }, environment: { type: 'string' }, 'core-manifest-sha256': { type: 'string' }, 'stdio-relay': { type: 'string' }, 'history-reader': { type: 'string' } }, strict: true });
  const names = new Set<string>();
  for (const token of tokens) if (token.kind === 'option') {
    if (names.has(token.name)) throw new Error('连接参数重复。');
    names.add(token.name);
  }
  if (values.transport !== 'stdio' || !values.profile || !values.environment || !isDeploymentIdentifier(values.profile)
    || !values['core-manifest-sha256'] || !/^[0-9a-f]{64}$/.test(values['core-manifest-sha256'])
    || !values['stdio-relay'] || !/^[0-9a-f]{32}$/.test(values['stdio-relay'])) throw new Error('连接参数无效。');
  const historyReader = values['history-reader'];
  if (historyReader !== undefined && (!isAbsolute(historyReader) || historyReader.length > 1024 || historyReader.includes('\0'))) throw new Error('历史读取依赖无效。');
  return { transport: 'stdio' as const, profile: values.profile, environment: canonicalEnvironment(values.environment), coreManifestSha256: values['core-manifest-sha256'], stdioRelay: values['stdio-relay'],
    ...(historyReader !== undefined ? { historyReader } : {}) };
}
