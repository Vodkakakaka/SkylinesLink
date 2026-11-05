import { schemaHash, validUnicode } from './profile.mjs';

const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const schemaId = value => typeof value === 'string' && /^urn:skylineslink:contract:[a-z][a-z0-9-]*:[1-9][0-9]*$/.test(value);
const hash = value => typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
const text = (value, maximum = Infinity, minimum = 1) => typeof value === 'string' && validUnicode(value) && [...value].length >= minimum && [...value].length <= maximum;
const fail = (code, detail) => { throw new Error(code + ': ' + detail); };
const declarationError = detail => fail('INVALID_CONTRACT_DECLARATION', detail);
const lockError = detail => fail('INVALID_IDENTITY_LOCK', detail);
const comparePath = (a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0;

function shape(value, fields, invalid, label) {
  if (!record(value) || Object.keys(value).length !== fields.length || fields.some(key => !Object.hasOwn(value, key))) invalid(label);
}

function pointer(value) {
  return text(value, Infinity, 0) && (value === '' || value.startsWith('/')) && !/~(?:[^01]|$)/.test(value);
}

/** Source declarations only. Compiler models and generator versions are not identities. */
export function deriveContractIdentities(documents, config) {
  shape(config, ['capabilities', 'boundaries', 'decimalFields'], declarationError, 'config fields');
  if (![config.capabilities, config.boundaries, config.decimalFields].every(Array.isArray)) declarationError('config arrays');
  const sources = new Map(), ids = new Set(), metadataByName = new Map();
  for (const entry of documents) {
    if (!text(entry.name) || !schemaId(entry.schema?.$id) || sources.has(entry.name) || ids.has(entry.schema.$id)) declarationError('schema identity');
    sources.set(entry.name, entry.schema); ids.add(entry.schema.$id);
    metadataByName.set(entry.name, { boundaries: [], decimalFields: [] });
  }
  for (const kind of ['boundaries', 'decimalFields']) {
    const seen = new Map();
    for (const item of config[kind]) {
      shape(item, kind === 'boundaries' ? ['schema', 'path', 'kind'] : ['schema', 'path', 'signed', 'scale', 'maxDigits'], declarationError, kind + ' fields');
      if (!text(item.schema) || !sources.has(item.schema) || !pointer(item.path)) declarationError(kind + ' target');
      const paths = seen.get(item.schema) ?? new Set();
      if (paths.has(item.path)) declarationError(kind + ' duplicate path');
      paths.add(item.path); seen.set(item.schema, paths);
      if (kind === 'boundaries') {
        if (!['registeredInput', 'registeredOutput', 'schemaDocument'].includes(item.kind)) declarationError('boundary kind');
        metadataByName.get(item.schema).boundaries.push({ path: item.path, kind: item.kind });
      } else {
        if (typeof item.signed !== 'boolean' || !Number.isSafeInteger(item.scale) || item.scale < 0 || item.scale > 2147483647
          || !Number.isSafeInteger(item.maxDigits) || item.maxDigits < 1 || item.maxDigits > 2147483647 || item.scale > item.maxDigits) declarationError('decimal rule');
        metadataByName.get(item.schema).decimalFields.push({ path: item.path, signed: item.signed, scale: item.scale, maxDigits: item.maxDigits });
      }
    }
  }
  for (const metadata of metadataByName.values()) { metadata.boundaries.sort(comparePath); metadata.decimalFields.sort(comparePath); }
  const names = new Set(), tools = new Set();
  const declarations = config.capabilities.map(capability => {
    shape(capability, ['id', 'toolName', 'schemaRevision', 'description', 'effect', 'input', 'output'], declarationError, 'capability fields');
    if (!text(capability.id, 128) || !text(capability.schemaRevision, 64) || !text(capability.toolName)
      || !text(capability.description, Infinity, 0) || !['read', 'write'].includes(capability.effect)
      || !text(capability.input) || !sources.has(capability.input) || !text(capability.output) || !sources.has(capability.output)
      || names.has(capability.id) || tools.has(capability.toolName)) declarationError('capability binding');
    names.add(capability.id); tools.add(capability.toolName);
    return { id: capability.id, toolName: capability.toolName, schemaRevision: capability.schemaRevision,
      description: capability.description, effect: capability.effect, inputId: sources.get(capability.input).$id, outputId: sources.get(capability.output).$id };
  });
  return {
    metadataByName,
    declarations,
    documents: Object.fromEntries(documents.map(entry => [entry.schema.$id, schemaHash(entry.schema)])),
    semantics: {
      formatVersion: 1,
      schemaMetadata: Object.fromEntries(documents.map(entry => [entry.schema.$id, schemaHash({ formatVersion: 1, ...metadataByName.get(entry.name) })])),
      capabilityBindings: Object.fromEntries(declarations.map(({ id, schemaRevision, effect, inputId, outputId, toolName }) =>
        [id, Object.fromEntries([[schemaRevision, schemaHash({ formatVersion: 1, effect, inputId, outputId, toolName })]])])),
    },
  };
}

function hashRecord(value, validKey, label) {
  if (!record(value) || Object.keys(value).length === 0 || Object.entries(value).some(([key, value]) => !validKey(key) || !hash(value))) lockError(label);
}

function mergeHashes(previous, current, code) {
  const merged = new Map(Object.entries(previous));
  for (const [key, value] of Object.entries(current)) {
    if (merged.has(key) && merged.get(key) !== value) fail(code, key);
    merged.set(key, value);
  }
  return Object.fromEntries(merged);
}

/** Preserve inactive fingerprints, but never feed them into the active catalog. */
export function reconcileContractIdentities(current, previousDocuments, previousSemantics) {
  hashRecord(previousDocuments, schemaId, 'schema document hashes');
  shape(previousSemantics, ['formatVersion', 'schemaMetadata', 'capabilityBindings'], lockError, 'semantic fields');
  if (previousSemantics.formatVersion !== 1) lockError('semantic format version');
  hashRecord(previousSemantics.schemaMetadata, schemaId, 'schema metadata hashes');
  if (!record(previousSemantics.capabilityBindings) || Object.keys(previousSemantics.capabilityBindings).length === 0) lockError('capability bindings');
  for (const [id, revisions] of Object.entries(previousSemantics.capabilityBindings)) {
    if (!text(id, 128)) lockError('capability id');
    hashRecord(revisions, revision => text(revision, 64), 'capability revisions');
  }
  // Both locks describe the same schema identity history. A partially removed
  // side cannot silently turn existing semantics into a first registration.
  if (Object.keys(previousDocuments).length !== Object.keys(previousSemantics.schemaMetadata).length
    || Object.keys(previousDocuments).some(id => !Object.hasOwn(previousSemantics.schemaMetadata, id))) lockError('schema histories differ');
  const documents = mergeHashes(previousDocuments, current.documents, 'SCHEMA_IDENTITY_CHANGED');
  const schemaMetadata = mergeHashes(previousSemantics.schemaMetadata, current.semantics.schemaMetadata, 'SEMANTIC_IDENTITY_CHANGED');
  const capabilities = new Map(Object.entries(previousSemantics.capabilityBindings));
  for (const [id, revisions] of Object.entries(current.semantics.capabilityBindings))
    capabilities.set(id, mergeHashes(capabilities.get(id) ?? {}, revisions, 'CAPABILITY_BINDING_CHANGED: ' + id));
  return { documents, semantics: { formatVersion: 1, schemaMetadata, capabilityBindings: Object.fromEntries(capabilities) } };
}
