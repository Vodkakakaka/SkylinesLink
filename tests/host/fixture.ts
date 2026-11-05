import { capabilities, schemaById, schemaIds, validateContract, type OperationLookupV2, type OperationLookupV3, type OperationQueryV3, type OperationSelector, type OperationStatus } from '../../contracts/ts/index.ts';
import { GameClientError, type GameClient } from '../../src/host/src/game-client/game-client.ts';
import type { Capability } from '../../src/host/src/catalog/catalog.ts';

export const syntheticContext = Object.freeze({ productVersion: '0.1.0-dev.0', contractVersion: '1',
  instanceId: 'offline-fixture', world: null, gameState: 'menu', catalogRevision: '1' });
export const syntheticSubmissionKey = Object.freeze({ writeContextId: 'offline-write-context', requestId: 'offline-request' });

/** 普通事实夹具：Host 只传输，真实 Task<bool> 与 Ready 的组合由 Runtime 测试证明。 */
export function liveLookup(selector: OperationSelector, value: OperationLookupV2): OperationLookupV3 {
  return { selector: structuredClone(selector), result: { kind: 'live', instanceId: syntheticContext.instanceId, ...value } };
}

export const syntheticLoadQueries: Readonly<Record<string, OperationLookupV3>> = Object.fromEntries([
  ['offline-load-true', 'ranToCompletion', true],
  ['offline-load-false', 'ranToCompletion', false],
  ['offline-load-unknown', 'unknown', null],
  ['offline-load-cancelled', 'cancelled', null],
].map(([id, physicalTaskState, loadReturnValue]) => [id, liveLookup({ kind: 'operationId', operationId: id as string }, { lookup: {
  kind: 'found', operationId: id, originInstanceId: 'offline-previous-instance',
  state: loadReturnValue === true ? 'succeeded' : loadReturnValue === false ? 'failed' : 'unknown',
  mayHaveApplied: true, durability: 'volatile', load: {
    physicalTaskState, loadReturnValue, targetMatch: 'exact', isObservationCurrent: false,
    correlatedTargetReady: true, historicalCompletionProven: loadReturnValue === true, observationEnd: 'ownerStopped',
  },
} } as OperationLookupV2)])) as Record<string, OperationLookupV3>;

/** 只存在于离线测试，不属于生产能力目录，也不代表真实游戏写入。 */
export const fixtureCapabilities: readonly Capability[] = [...capabilities, {
  id: 'probe.submit', toolName: 'probe_submit', schemaRevision: '1', description: '仅离线夹具：建立一个合成操作。', effect: 'write',
  inputId: schemaIds.Empty, outputId: schemaIds.OperationStatus,
  inputSchema: schemaById[schemaIds.Empty]!, outputSchema: schemaById[schemaIds.OperationStatus]!,
  validateInput: value => validateContract(schemaIds.Empty, value),
  validateOutput: value => validateContract(schemaIds.OperationStatus, value),
}];

export class MemoryGame implements GameClient {
  calls: string[] = [];
  operation: OperationStatus | undefined;
  async call(id: string, _revision: string, args: unknown, signal: AbortSignal): Promise<unknown> {
    signal.throwIfAborted();
    this.calls.push(id);
    if (id === 'system.context') return { ...syntheticContext };
    if (id === 'probe.submit') {
      this.operation = { operationId: 'offline-operation', state: 'succeeded', mayHaveApplied: false };
      return { ...this.operation };
    }
    if (id === 'operations.get') {
      const selector = (args as OperationQueryV3).selector;
      const live = (value: OperationLookupV2) => liveLookup(selector, value);
      if (selector.kind === 'operationId') {
        if (Object.hasOwn(syntheticLoadQueries, selector.operationId)) return structuredClone(syntheticLoadQueries[selector.operationId]);
        if (selector.operationId === 'offline-old-load-shape') return { selector, result: { kind: 'live', instanceId: syntheticContext.instanceId, lookup: {
          kind: 'found', operationId: selector.operationId, originInstanceId: 'offline-old-instance',
          state: 'succeeded', mayHaveApplied: true, durability: 'durable', load: {
            nativeState: 'succeeded', targetMatch: 'exact', isObservationCurrent: false,
            correlatedTargetReady: true, historicalCompletionProven: true, observationEnd: 'ownerStopped',
          },
        } } };
        if (selector.operationId === 'offline-old-envelope') return { lookup: { kind: 'absent' } };
        if (selector.operationId === 'offline-old-journal') return live({ lookup: { kind: 'unavailable', reason: 'JOURNAL_INCOMPATIBLE' } });
      }
      const matches = selector.kind === 'operationId' ? selector.operationId === this.operation?.operationId
        : selector.submissionKey.writeContextId === syntheticSubmissionKey.writeContextId && selector.submissionKey.requestId === syntheticSubmissionKey.requestId;
      if (matches && this.operation) return live({ lookup: { kind: 'found', ...this.operation, originInstanceId: syntheticContext.instanceId, durability: 'volatile' } });
      if (selector.kind === 'submissionKey' && selector.submissionKey.writeContextId !== syntheticSubmissionKey.writeContextId)
        return live({ lookup: { kind: 'unavailable', reason: 'CONTEXT_UNKNOWN' } });
      return live({ lookup: { kind: 'absent' } });
    }
    throw new GameClientError('NOT_FOUND', '未找到该操作。');
  }
  async close(): Promise<void> {}
}
