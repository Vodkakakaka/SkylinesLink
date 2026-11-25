import assert from 'node:assert/strict';
import { test } from 'node:test';
import { contractHelpers, requireDeclaredCatalog } from './readonly-client.mjs';

const { productCapabilities } = await contractHelpers();
const currentTools = productCapabilities.map(({ toolName, effect }) => ({
  name: toolName,
  annotations: { readOnlyHint: effect === 'read', destructiveHint: effect === 'write' },
}));

test('菜单只读验收接受当前完整目录并核查读写标记', () => {
  assert.ok(currentTools.some(tool => tool.annotations.destructiveHint));
  assert.doesNotThrow(() => requireDeclaredCatalog([...currentTools].reverse(), productCapabilities));
});

test('旧的两项名单和重复项不能冒充当前目录', () => {
  const originalReads = currentTools.filter(tool => ['skylines_context', 'skylines_operation_get'].includes(tool.name));
  assert.throws(() => requireDeclaredCatalog(originalReads, productCapabilities), { code: 'TOOL_CATALOG_MISMATCH' });
  assert.throws(() => requireDeclaredCatalog([...currentTools.slice(1), currentTools[1]], productCapabilities), { code: 'TOOL_CATALOG_MISMATCH' });
});

test('写工具被标成只读会拒绝目录', () => {
  const wrongEffect = currentTools.map(tool => tool.annotations.destructiveHint
    ? { ...tool, annotations: { readOnlyHint: true, destructiveHint: false } } : tool);
  assert.throws(() => requireDeclaredCatalog(wrongEffect, productCapabilities), { code: 'TOOL_EFFECT_MISMATCH' });
});
