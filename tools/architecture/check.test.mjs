import test from 'node:test';
import assert from 'node:assert/strict';
import { inspectSource } from './check.mjs';

test('净应用禁止反向工程引用与运行时 IO', () => {
  assert.equal(inspectSource('src/game/SkylinesLink.Application/SkylinesLink.Application.csproj', '<Project><TargetFramework>netstandard2.0</TargetFramework><ProjectReference Include="../SkylinesLink.Infrastructure/SkylinesLink.Infrastructure.csproj" /></Project>').length, 1);
  assert.equal(inspectSource('src/game/SkylinesLink.Application/Networks/Query.cs', 'using System.IO;').length, 1);
});

test('领域可消费另一域窄事实，不能调用其具体用例或上层组合', () => {
  const path = 'src/game/SkylinesLink.Application/Networks/Query.cs';
  assert.deepEqual(inspectSource(path, 'using SkylinesLink.Application.Buildings.Facts;'), []);
  assert.equal(inspectSource(path, 'using SkylinesLink.Application.Buildings.UseCases;').length, 1);
  assert.equal(inspectSource(path, 'using SkylinesLink.Application.Workflows.SpatialPlanning;').length, 1);
});

test('GameClient 与中立 TS 不能越过平台边界', () => {
  assert.equal(inspectSource('src/host/src/game-client/client.ts', "import { tool } from '../mcp/tools/tool.ts';").length, 1);
  assert.equal(inspectSource('contracts/ts/index.ts', "import { readFile } from 'node:fs';").length, 1);
  assert.deepEqual(inspectSource('src/host/src/mcp/tools/context.ts', "import { client } from '../../game-client/client.ts';"), []);
});

test('共享Task规则不反向依赖应用、JSON或平台IO', () => {
  const path = 'src/shared/Tasks/TaskJournalRules.cs';
  assert.deepEqual(inspectSource(path, 'using System; using System.Collections.Generic; using System.Security.Cryptography;'), []);
  assert.deepEqual(inspectSource(path, 'using (var hash = SHA256.Create()) { hash.ComputeHash(bytes); }'), []);
  assert.equal(inspectSource(path, 'using SkylinesLink.Application.Operations;').length, 1);
  assert.equal(inspectSource(path, 'using Newtonsoft.Json;').length, 1);
  assert.equal(inspectSource(path, 'System.IO.File.ReadAllText("x");').length, 1);
});
