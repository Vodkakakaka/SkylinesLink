import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdtemp, mkdir, readdir, readFile, writeFile, copyFile, lstat, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const source = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const rawPath = 'contracts/schema-identities.generated.json';
const semanticPath = 'contracts/semantic-identities.generated.json';
const moneyId = 'urn:skylineslink:contract:identity-money:1';
const canonical = value => Array.isArray(value) ? '[' + value.map(canonical).join(',') + ']'
  : value !== null && typeof value === 'object' ? '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}'
    : JSON.stringify(value);
const hash = value => createHash('sha256').update(canonical(value)).digest('hex');
const compare = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;
const json = async (root, name) => JSON.parse(await readFile(path.join(root, name), 'utf8'));
const put = (root, name, value) => writeFile(path.join(root, name), JSON.stringify(value, null, 2) + '\n');

// The initial test oracle describes declared semantics, not the compiler model.
function semanticOracle(schemas, config) {
  const byName = new Map(schemas.map(entry => [entry.name, entry.schema.$id]));
  return {
    formatVersion: 1,
    schemaMetadata: Object.fromEntries(schemas.map(({ name, schema }) => [schema.$id, hash({
      formatVersion: 1,
      boundaries: config.boundaries.filter(item => item.schema === name).map(({ path, kind }) => ({ path, kind })).sort(compare),
      decimalFields: config.decimalFields.filter(item => item.schema === name).map(({ path, signed, scale, maxDigits }) => ({ path, signed, scale, maxDigits })).sort(compare),
    })])),
    capabilityBindings: Object.fromEntries(config.capabilities.map(capability => [capability.id, { [capability.schemaRevision]: hash({
      formatVersion: 1, effect: capability.effect, inputId: byName.get(capability.input), outputId: byName.get(capability.output), toolName: capability.toolName,
    }) }])),
  };
}

function run(root, ...args) {
  try { return { status: 0, output: execFileSync(process.execPath, ['tools/contracts/generate.mjs', ...args], { cwd: root, encoding: 'utf8', windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], timeout: 10000, maxBuffer: 256 * 1024 }) }; }
  catch (error) { return { status: error.status, output: String(error.stderr) + String(error.stdout) }; }
}

async function withFixture(work) {
  const temporaryParent = path.resolve(tmpdir());
  const prefix = 'skylineslink-identities-';
  const root = await mkdtemp(path.join(temporaryParent, prefix));
  const created = await lstat(root, { bigint: true });
  try {
    await copyFile(path.join(source, 'package.json'), path.join(root, 'package.json'));
    for (const directory of ['tools/contracts', 'contracts/schemas', 'contracts/fixtures']) await mkdir(path.join(root, directory), { recursive: true });
    for (const name of await readdir(path.join(source, 'tools/contracts')))
      if (name.endsWith('.mjs') || name.endsWith('.template')) await copyFile(path.join(source, 'tools/contracts', name), path.join(root, 'tools/contracts', name));
    for (const name of await readdir(path.join(source, 'contracts/schemas')))
      if (name.endsWith('.json')) await copyFile(path.join(source, 'contracts/schemas', name), path.join(root, 'contracts/schemas', name));
    for (const name of ['profile-schema.json', 'profile-metadata.json']) await copyFile(path.join(source, 'contracts/fixtures', name), path.join(root, 'contracts/fixtures', name));
    await copyFile(path.join(source,'contracts/control-transport-profile.json'),path.join(root,'contracts/control-transport-profile.json'));
    const money = { $schema: 'https://json-schema.org/draft/2020-12/schema', $id: moneyId, type: 'object', properties: {
      amount: { type: 'string', minLength: 1, maxLength: 12 }, other: { type: 'string', minLength: 1, maxLength: 12 },
    }, required: ['amount', 'other'], additionalProperties: false };
    await put(root, 'contracts/schemas/identity-money.json', money);
    const config = await json(source, 'contracts/capabilities.json');
    config.decimalFields.push(...['amount', 'other'].map(field => ({ schema: 'identity-money', path: '/properties/' + field, signed: true, scale: 2, maxDigits: 8 })));
    await put(root, 'contracts/capabilities.json', config);
    const schemas = await Promise.all((await readdir(path.join(root, 'contracts/schemas'))).filter(name => name.endsWith('.json')).sort().map(async name => ({ name: name.slice(0, -5), schema: await json(root, 'contracts/schemas/' + name) })));
    const documents = await json(source, rawPath); documents[moneyId] = hash(money);
    await put(root, rawPath, documents);
    await put(root, semanticPath, semanticOracle(schemas, config));
    const baseline = run(root); assert.equal(baseline.status, 0, baseline.output);
    await work(root);
  } finally {
    assert.ok(path.isAbsolute(root));
    assert.equal(path.dirname(root), temporaryParent);
    assert.ok(path.basename(root).startsWith(prefix));
    const current = await lstat(root, { bigint: true });
    assert.ok(current.isDirectory() && !current.isSymbolicLink());
    assert.equal(current.dev, created.dev);
    assert.equal(current.ino, created.ino);
    await rm(root, { recursive: true });
  }
}

async function rejectedBeforeWrite(root, expected) {
  const outputs = [rawPath, semanticPath, 'contracts/ts/index.ts', 'contracts/ts/private-history-limits.generated.ts', 'contracts/cs/SkylinesLink.Contracts/Contracts.generated.cs', 'contracts/cs/SkylinesLink.Contracts/ControlTransportProfile.generated.cs',
    'tests/contracts/FixtureContracts.generated.cs', 'contracts/cs/SkylinesLink.Contracts/StrictJsonSyntax.generated.cs',
    'src/desktop/SkylinesLink.Deployment/Generated/StrictJsonSyntax.generated.cs', 'src/desktop/SkylinesLink.Deployment/Generated/DeploymentContracts.generated.cs'];
  const before = await Promise.all(outputs.map(async name => [name, await readFile(path.join(root, name)).catch(error => { if (error.code === 'ENOENT') return null; throw error; })]));
  const result = run(root);
  assert.notEqual(result.status, 0, 'generator must reject the changed identity');
  assert.match(result.output, expected);
  for (const [name, bytes] of before) {
    const after = await readFile(path.join(root, name)).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
    assert.deepEqual(after, bytes, 'failed identity validation must not write ' + name);
  }
}

test('真实生成入口拒绝同$id改变原文，写出前保持全部身份及代码', () => withFixture(async root => {
  const document = await json(root, 'contracts/schemas/identity-money.json'); document.properties.amount.maxLength++;
  await put(root, 'contracts/schemas/identity-money.json', document);
  await rejectedBeforeWrite(root, /schema ID内容改变|SCHEMA_IDENTITY_CHANGED/);
}));

test('历史共享形状不能在新文档中漂移，真实入口写出前拒绝', () => withFixture(async root => {
  const name = 'contracts/schemas/native-history-capture.json';
  const original = await json(root, name);
  for (const change of [
    document => { document.$defs.HistoryRecord.properties.operationId = { type: 'string', minLength: 1, maxLength: 127 }; },
    document => { document.$defs.HistorySource.properties.producerVersion.maxLength = 63; },
    document => { document.$defs.HistoryContext.properties.generation = { type: 'integer', minimum: 0, maximum: 9007199254740991 }; },
  ]) {
    // Use a deliberately unregistered revision: :2 is now a real contract.
    const document = structuredClone(original); document.$id = 'urn:skylineslink:contract:native-history-capture:999';
    change(document); await put(root, name, document);
    await rejectedBeforeWrite(root, /共享契约形状漂移/);
  }
}));

test('混合日志沿用的Load事实不能随新文档漂移，真实生成写出前拒绝', () => withFixture(async root => {
  const name='contracts/schemas/operation-journal-record-v3.json';
  const original=await json(root,name);
  for(const change of [
    document=>{document.properties.environmentId.maxLength=63;},
    document=>{document.$defs.ContextSource.properties.generation={type:'integer',minimum:0,maximum:9007199254740991};},
    document=>{document.properties.entry.oneOf.find(branch=>branch.properties.kind.const==='accepted').properties.commandHash.maxLength=65;},
    document=>{document.properties.entry.oneOf.find(branch=>branch.properties.kind.const==='commitIntent').properties.nativeHost.properties.creationTime.maxLength=31;},
    document=>{document.properties.entry.oneOf.find(branch=>branch.properties.kind.const==='nativeTerminal').properties.physicalTaskState.enum.push('unknown');},
  ]) {
    const document=structuredClone(original);document.$id='urn:skylineslink:contract:operation-journal-record:4';
    change(document);await put(root,name,document);
    await rejectedBeforeWrite(root,/共享契约形状漂移/);
  }
}));
test('控制2只改变声明的版本及字节界，生成拒绝其他形状漂移', () => withFixture(async root => {
  const name='contracts/schemas/journal-control-v2.json';
  const original=await json(root,name);
  for(const change of [
    document=>{document.$defs.SourceCut.properties.throughBytes.maximum=2097153;},
    document=>{document.properties.entry.oneOf.find(branch=>branch.properties.kind.const==='generation').properties.seedFrameCount.maximum=130;},
    document=>{document.properties.entry.oneOf.find(branch=>branch.properties.kind.const==='root').properties.journalSchemaRevision.maximum=4;},
  ]) {
    const document=structuredClone(original);document.$id='urn:skylineslink:contract:journal-control:3';
    change(document);await put(root,name,document);
    await rejectedBeforeWrite(root,/共享契约形状漂移/);
  }
}));

test('保留lookup只扩展未覆盖原因，真实生成拒绝其他事实漂移', () => withFixture(async root => {
  const name = 'contracts/schemas/operation-lookup-v4.json';
  const original = await json(root, name);
  for (const change of [
    document => { document.$defs.HistorySource.properties.producerVersion.maxLength = 63; },
    document => { document.$defs.LiveLookup.oneOf.find(branch => branch.properties.kind.const === 'unavailable').properties.reason.enum.push('UNDECLARED_REASON'); },
    document => { document.$defs.LoadObservation.properties.isObservationCurrent.type = ['boolean', 'null']; },
  ]) {
    const document = structuredClone(original); document.$id = 'urn:skylineslink:contract:operation-lookup:5';
    change(document); await put(root, name, document);
    await rejectedBeforeWrite(root, /共享契约形状漂移/);
  }
}));

test('公开指南的语言、摘要与能力引用保持同形，真实生成写出前拒绝漂移', () => withFixture(async root => {
  const name = 'contracts/schemas/guidance-document.json';
  const original = await json(root, name);
  for (const change of [
    document => { document.$defs.Language.enum = ['en', 'de']; },
    document => { document.$defs.CapabilityReference.properties.capabilityId.maxLength = 127; },
    document => { document.properties.summary.maxLength = 511; },
  ]) {
    const document = structuredClone(original); document.$id = 'urn:skylineslink:contract:guidance-document:2';
    change(document); await put(root, name, document);
    await rejectedBeforeWrite(root, /共享契约形状漂移/);
  }
}));

for (const [field, value] of [['effect', 'write'], ['input', 'context'], ['output', 'empty'], ['toolName', 'identity_renamed']])
  test('真实生成入口拒绝同能力revision改变' + field, () => withFixture(async root => {
    const config = await json(root, 'contracts/capabilities.json'); config.capabilities[0][field] = value;
    await put(root, 'contracts/capabilities.json', config);
    await rejectedBeforeWrite(root, /CAPABILITY_BINDING_CHANGED/);
  }));

for (const [field, value] of [['scale', 3], ['signed', false], ['maxDigits', 9]])
  test('真实生成入口拒绝同$id改变decimal ' + field, () => withFixture(async root => {
    const config = await json(root, 'contracts/capabilities.json'); config.decimalFields[0][field] = value;
    await put(root, 'contracts/capabilities.json', config);
    await rejectedBeforeWrite(root, /SEMANTIC_IDENTITY_CHANGED/);
  }));

test('真实生成入口拒绝重复path及未知配置字段，不能归一化后忽略', () => withFixture(async root => {
  const baseline = await json(root, 'contracts/capabilities.json');
  for (const change of [
    config => config.decimalFields.push({ ...config.decimalFields[0] }),
    config => config.boundaries.push({ ...config.boundaries[0] }),
    config => { config.decimalFields[0].rounding = 'nearest'; },
    config => { config.boundaries[0].optional = true; },
    config => { config.decimalFields[0].schema = 'missing-schema'; },
    config => { config.capabilities[0].optional = true; },
    config => config.capabilities.push({ ...config.capabilities[0], toolName: 'duplicate_identity' }),
    config => config.capabilities.push({ ...config.capabilities[0], id: 'duplicate.tool' }),
    config => { delete config.capabilities[0].schemaRevision; },
    config => { config.optional = true; },
  ]) {
    const config = structuredClone(baseline); change(config); await put(root, 'contracts/capabilities.json', config);
    await rejectedBeforeWrite(root, /INVALID_CONTRACT_DECLARATION/);
  }
}));

test('metadata重排与能力描述修字不改变身份，允许新revision且保旧绑定', () => withFixture(async root => {
  const initial = await json(root, semanticPath);
  const config = await json(root, 'contracts/capabilities.json'); config.boundaries.reverse(); config.decimalFields.reverse(); config.capabilities[0].description += ' 说明修订。';
  await put(root, 'contracts/capabilities.json', config);
  assert.equal(run(root).status, 0);
  assert.deepEqual(await json(root, semanticPath), initial);
  const old = config.capabilities[0].schemaRevision; config.capabilities[0].schemaRevision = 'identity-new';
  await put(root, 'contracts/capabilities.json', config);
  assert.equal(run(root).status, 0);
  const next = await json(root, semanticPath);
  assert.equal(next.capabilityBindings[config.capabilities[0].id][old], initial.capabilityBindings[config.capabilities[0].id][old]);
  assert.equal(typeof next.capabilityBindings[config.capabilities[0].id]['identity-new'], 'string');
  assert.equal(run(root, '--check').status, 0);
}));

test('移除后保留文档与metadata身份，重新使用旧$id改义仍拒绝', () => withFixture(async root => {
  const document = await json(root, 'contracts/schemas/identity-money.json');
  const config = await json(root, 'contracts/capabilities.json'); const metadata = config.decimalFields;
  const initial = await json(root, rawPath);
  config.decimalFields = []; await put(root, 'contracts/capabilities.json', config);
  await rm(path.join(root, 'contracts/schemas/identity-money.json'));
  assert.equal(run(root).status, 0);
  assert.equal((await json(root, rawPath))[moneyId], initial[moneyId]);
  await put(root, 'contracts/schemas/identity-money.json', document); config.decimalFields = metadata; config.decimalFields[0].scale = 3;
  await put(root, 'contracts/capabilities.json', config);
  await rejectedBeforeWrite(root, /SEMANTIC_IDENTITY_CHANGED/);
}));

test('新schema修订可登记新语义，旧原文与metadata指纹仍保留', () => withFixture(async root => {
  const raw = await json(root, rawPath), semantics = await json(root, semanticPath);
  const document = await json(root, 'contracts/schemas/identity-money.json'); document.$id = 'urn:skylineslink:contract:identity-money:2';
  await put(root, 'contracts/schemas/identity-money.json', document);
  const config = await json(root, 'contracts/capabilities.json'); config.decimalFields[0].scale = 3;
  await put(root, 'contracts/capabilities.json', config); assert.equal(run(root).status, 0);
  const nextRaw = await json(root, rawPath), nextSemantics = await json(root, semanticPath);
  assert.equal(nextRaw[moneyId], raw[moneyId]);
  assert.equal(nextSemantics.schemaMetadata[moneyId], semantics.schemaMetadata[moneyId]);
  assert.equal(nextRaw[document.$id], hash(document));
  assert.notEqual(nextSemantics.schemaMetadata[document.$id], semantics.schemaMetadata[moneyId]);
}));

test('停用能力保留历史绑定而不恢复active catalog，旧revision改义仍拒绝', () => withFixture(async root => {
  const config = await json(root, 'contracts/capabilities.json'); const removed = config.capabilities.shift();
  const initial = await json(root, semanticPath);
  await put(root, 'contracts/capabilities.json', config); assert.equal(run(root).status, 0);
  assert.deepEqual((await json(root, semanticPath)).capabilityBindings[removed.id], initial.capabilityBindings[removed.id]);
  assert.equal((await readFile(path.join(root, 'contracts/ts/index.ts'), 'utf8')).includes(JSON.stringify(removed.toolName)), false);
  config.capabilities.push({ ...removed, effect: 'write' }); await put(root, 'contracts/capabilities.json', config);
  await rejectedBeforeWrite(root, /CAPABILITY_BINDING_CHANGED/);
}));

test('能力map保留原型名和含分隔符的独立id/revision，不合并或丢项', () => withFixture(async root => {
  const config = await json(root, 'contracts/capabilities.json');
  for (const [id, revision, toolName] of [['__proto__', 'constructor', 'identity_proto'], ['identity:cap', 'v1', 'identity_first'], ['identity', 'cap:v1', 'identity_second']])
    config.capabilities.push({ id, schemaRevision: revision, toolName, description: 'identity fixture', effect: 'read', input: 'empty', output: 'context' });
  await put(root, 'contracts/capabilities.json', config); assert.equal(run(root).status, 0);
  const bindings = (await json(root, semanticPath)).capabilityBindings;
  assert.equal(typeof bindings['__proto__']['constructor'], 'string');
  assert.equal(typeof bindings['identity:cap']['v1'], 'string');
  assert.equal(typeof bindings.identity['cap:v1'], 'string');
}));

for (const name of [rawPath, semanticPath]) test('缺失身份锁不能自动重建：' + name, () => withFixture(async root => {
  await rm(path.join(root, name)); await rejectedBeforeWrite(root, /ENOENT|IDENTITY_LOCK/);
}));

test('损坏及未知语义锁版本拒绝，不按当前源重置', () => withFixture(async root => {
  const baseline = await json(root, semanticPath);
  for (const value of [{ ...baseline, formatVersion: 2 }, { ...baseline, schemaMetadata: { [moneyId]: 'not-a-hash' } },
    { ...baseline, capabilityBindings: {} }, { ...baseline, capabilityBindings: { 'system.context': {} } }]) {
    await put(root, semanticPath, value); await rejectedBeforeWrite(root, /INVALID_IDENTITY_LOCK/);
  }
}));

test('清空两锁或只删一侧schema历史不能变成首次登记', () => withFixture(async root => {
  const raw = await json(root, rawPath), semantics = await json(root, semanticPath);
  const reduced = { ...semantics, schemaMetadata: { ...semantics.schemaMetadata } }; delete reduced.schemaMetadata[moneyId];
  await put(root, semanticPath, reduced); await rejectedBeforeWrite(root, /INVALID_IDENTITY_LOCK/);
  await put(root, rawPath, {}); await put(root, semanticPath, { ...semantics, schemaMetadata: {} });
  await rejectedBeforeWrite(root, /INVALID_IDENTITY_LOCK/);
  await put(root, rawPath, raw);
}));
