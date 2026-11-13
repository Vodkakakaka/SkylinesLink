import { test } from 'node:test';
import assert from 'node:assert/strict';
import { capabilities } from '../../contracts/ts/index.ts';
import { guideTranslations } from '../../src/host/src/guidance/content.ts';

const scalarLength = (value: string) => [...value].length;
const bounded = (value: string, maximum: number) => {
  assert.equal(typeof value, 'string');
  assert.ok(scalarLength(value) >= 1 && scalarLength(value) <= maximum);
};

test('公共指引五主题成对中英文，稳定ID/修订/能力引用一致', () => {
  assert.equal(guideTranslations.length, 10);
  const groups = new Map<string, (typeof guideTranslations)[number][]>();
  for (const guide of guideTranslations) {
    const group = groups.get(guide.guideId) ?? [];
    assert.equal(group.some(item => item.language === guide.language), false);
    group.push(guide); groups.set(guide.guideId, group);
  }
  assert.deepEqual([...groups.keys()].sort(), ['building-inspection', 'getting-started', 'module-runtime', 'operation-status', 'planning']);
  for (const group of groups.values()) {
    assert.deepEqual(group.map(item => item.language).sort(), ['en', 'zh-CN']);
    const english = group.find(item => item.language === 'en')!;
    const chinese = group.find(item => item.language === 'zh-CN')!;
    assert.equal(english.revision, chinese.revision);
    assert.deepEqual(english.capabilityRefs, chinese.capabilityRefs);
    assert.notEqual(english.title, chinese.title);
    assert.notEqual(english.bodyMarkdown, chinese.bodyMarkdown);
    assert.match(chinese.bodyMarkdown, /\p{Script=Han}/u);
  }
});

test('随包公共内容字段与标量长度有限，所有引用绑定当前生成能力修订', () => {
  const approved = new Map([
    ['system.context', '2'], ['operations.get', '5'], ['guidance.search', '1'],
    ['guidance.get', '1'], ['capabilities.describe', '1'], ['tasks.stop', '1'], ['tasks.stop.get', '1'],
    ['system.modules.get', '1'],
    ['city.overview', '2'],
    ['buildings.inspect', '1'],
  ]);
  for (const guide of guideTranslations) {
    assert.deepEqual(Object.keys(guide).sort(), ['guideId', 'revision', 'language', 'title', 'summary', 'bodyMarkdown', 'capabilityRefs'].sort());
    bounded(guide.guideId, 64); bounded(guide.revision, 32); bounded(guide.title, 128);
    bounded(guide.summary, 512); bounded(guide.bodyMarkdown, 12000);
    assert.ok(['en', 'zh-CN'].includes(guide.language));
    assert.ok(guide.capabilityRefs.length <= 16);
    assert.equal(new Set(guide.capabilityRefs.map(item => item.capabilityId)).size, guide.capabilityRefs.length);
    for (const reference of guide.capabilityRefs) {
      assert.deepEqual(Object.keys(reference).sort(), ['capabilityId', 'schemaRevision']);
      assert.equal(approved.get(reference.capabilityId), reference.schemaRevision);
      const current = capabilities.find(item => item.id === reference.capabilityId);
      assert.ok(current, `Missing active capability: ${reference.capabilityId}`);
      assert.equal(current.schemaRevision, reference.schemaRevision);
    }
  }
});

test('公共内容和所有嵌套引用不可由一次读取修改后影响其他客户端', () => {
  assert.ok(Object.isFrozen(guideTranslations));
  for (const guide of guideTranslations) {
    assert.ok(Object.isFrozen(guide));
    assert.ok(Object.isFrozen(guide.capabilityRefs));
    assert.ok(guide.capabilityRefs.every(reference => Object.isFrozen(reference)));
  }
  const first = guideTranslations[0];
  const original = first.title;
  assert.equal(Reflect.set(first, 'title', 'changed by one caller'), false);
  assert.equal(first.title, original);
});
