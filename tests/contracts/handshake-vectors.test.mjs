import test from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import fs from 'node:fs';

// Test oracle only. Production authentication stays in the two connection owners.
// The fixed expected bytes were independently calculated with Python hmac/struct.
const fixture = JSON.parse(fs.readFileSync(new URL('../../contracts/fixtures/handshake-v2.json', import.meta.url), 'utf8'));
const fieldOrder = ['requestId', 'environmentId', 'instanceId', 'productVersion', 'contractVersion', 'coreManifestSha256', 'authorityEpoch', 'profileId', 'grantEpoch', 'clientNonce', 'serverNonce'];
assert.deepEqual(fixture.fieldOrder, ['domain', 'role', ...fieldOrder]);

function transcript(role, fields) {
  const parts = [fixture.domain, role, ...fieldOrder.map(key => String(fields[key]))];
  return Buffer.concat(parts.flatMap(value => {
    const bytes = Buffer.from(value, 'utf8');
    const length = Buffer.alloc(4);
    length.writeUInt32LE(bytes.length);
    return [length, bytes];
  }));
}

for (const vector of fixture.vectors) test('双向认证共享向量：' + vector.name, () => {
  const key = Buffer.from(vector.internalToken, 'hex');
  assert.equal(key.length, 32);
  for (const role of ['server', 'client']) {
    const bytes = transcript(role, vector.fields);
    assert.equal(bytes.length, vector[role + 'TranscriptBytes']);
    assert.equal(createHmac('sha256', key).update(bytes).digest('hex'), vector[role + 'Proof']);
  }
  assert.notEqual(vector.serverProof, vector.clientProof);
});

test('证明绑定全部身份字段且分隔不造成串接歧义', () => {
  const vector = fixture.vectors[0];
  const key = Buffer.from(vector.internalToken, 'hex');
  for (const field of fieldOrder) {
    const changed = { ...vector.fields, [field]: typeof vector.fields[field] === 'number' ? vector.fields[field] + 1 : vector.fields[field] + 'x' };
    assert.notEqual(createHmac('sha256', key).update(transcript('server', changed)).digest('hex'), vector.serverProof, field);
  }
  assert.notDeepEqual(transcript('server', { ...vector.fields, requestId: 'a', environmentId: 'bc' }), transcript('server', { ...vector.fields, requestId: 'ab', environmentId: 'c' }));
});
