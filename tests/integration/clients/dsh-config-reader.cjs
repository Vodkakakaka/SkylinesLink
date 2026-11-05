// Pure YAML parsing under the already installed Electron Node carrier. Never boots DSH.
'use strict';
const { join, isAbsolute } = require('node:path');
const assert = require('node:assert/strict');
const criticalIds = new Set(['tools', 'agent-default-model', 'llm-deepseek', 'approval', 'sandbox-policy', 'skylineslink-readonly']);
(async () => {
  assert.equal(process.argv.length, 3);
  assert.ok(isAbsolute(process.argv[2]));
  // Caller verifies the complete bundled js-yaml JS/package tree before executing this helper.
  const yaml = require(join(process.argv[2], 'dsh/node_modules/js-yaml/index.js'));
  const jsTag = new yaml.Type('tag:yaml.org,2002:js', {
    kind: 'scalar', construct: value => ({ __unresolvedDshExpression: value }),
  });
  const chunks = []; let bytes = 0;
  for await (const chunk of process.stdin) {
    bytes += chunk.length; assert.ok(bytes <= 1048576); chunks.push(chunk);
  }
  const text = new TextDecoder('utf-8', { fatal: true }).decode(Buffer.concat(chunks));
  const entries = yaml.load(text, { schema: yaml.DEFAULT_SCHEMA.extend([jsTag]), json: false });
  assert.ok(Array.isArray(entries) && entries.length > 0 && entries.length <= 256);
  const ids = new Set();
  const rows = entries.map(entry => {
    assert.ok(entry && typeof entry === 'object' && !Array.isArray(entry));
    assert.ok(typeof entry.id === 'string' && entry.id.length <= 128 && !ids.has(entry.id)); ids.add(entry.id);
    assert.ok(typeof entry.name === 'string' && entry.name.length <= 256);
    return { id: entry.id, name: entry.name, ...(entry.disabled === undefined ? {} : { disabled: entry.disabled }),
      ...(criticalIds.has(entry.id) ? { config: entry.config } : {}) };
  });
  const output = JSON.stringify({ rowCount: rows.length, rows });
  assert.ok(Buffer.byteLength(output) <= 65536);
  process.stdout.write(output + '\n');
})().catch(() => { process.stderr.write('Configuration summary rejected.\n'); process.exitCode = 1; });
