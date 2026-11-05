import {
  McpServer,
  fromJsonSchema,
  type JsonSchemaType,
  type jsonSchemaValidator,
  type CallToolResult,
} from '@modelcontextprotocol/server';
import type { OperationLookupV2, OperationQueryV2, OperationQueryV3 } from '@skylineslink/contracts';
import { requireOperationLookup, requireOperationLookupV4 } from '../game-client/history-result.ts';
import { productCapabilities, type Capability } from '../catalog/catalog.ts';
import { GameClientError, type GameClient } from '../game-client/game-client.ts';
import { measureJson } from '../shared/json-budget.ts';
import { probeLimits, validateLimits, type TransportLimits } from '../shared/limits.ts';
import { LocalReads } from '../guidance/local-reads.ts';
import { productVersion as defaultProductVersion } from '../shared/identity.ts';
import { ControlClassifier, LaneBudget, type RequestLifecycle } from '../shared/control-admission.ts';
import { copyStopSelector, requireStopResult, requireStopDecisionLookup } from '../game-client/task-stop-result.ts';
import { copyModuleRuntimeQuery, requireModuleRuntimePage } from '../game-client/module-runtime-result.ts';
import { requireCityOverview } from '../game-client/city-overview-result.ts';
import type { ModuleRuntimeQuery } from '@skylineslink/contracts';

export interface HostOptions {
  game: GameClient;
  capabilities?: readonly Capability[];
  limits?: TransportLimits;
  productVersion?: string;
  workBudget?: WorkBudget;
  lifecycle?: RequestLifecycle;
}

export class WorkBudget {
  #lanes: LaneBudget;
  #idleWaiters = new Set<() => void>();
  readonly maximum: number;
  constructor(maximum: number) {
    if (!Number.isSafeInteger(maximum) || maximum < 1) throw new Error('工作额度必须为有限正整数。');
    this.maximum = maximum;
    this.#lanes = new LaneBudget(maximum);
  }
  get active(): number { return this.#lanes.active; }
  acquire(controlKey?: string): () => void {
    const release = this.#lanes.acquire(controlKey);
    let released = false;
    return () => { if (!released) { released = true; release(); if (this.active === 0) { for (const wake of this.#idleWaiters) wake(); this.#idleWaiters.clear(); } } };
  }
  waitIdle(): Promise<void> { return this.active === 0 ? Promise.resolve() : new Promise((resolve) => this.#idleWaiters.add(resolve)); }
}

function schemaAdapter(schema: Capability['inputSchema'], validate: Capability['validateInput']) {
  const validator: jsonSchemaValidator = {
    getValidator<T>() {
      return (data: unknown) => {
        const checked = validate(data);
        return checked.valid
          ? { valid: true as const, data: data as T, errorMessage: undefined }
          : { valid: false as const, data: undefined, errorMessage: checked.issues.map((i) => `${i.path}: ${i.code}`).join('; ') };
      };
    },
  };
  return fromJsonSchema<Record<string, unknown>>(schema as JsonSchemaType, validator);
}

function failure(code: string, message: string, mayHaveApplied: boolean | undefined, operationId?: string, selector?: OperationQueryV2['selector'], source?: 'live' | 'history', taskSelector?: { taskId: string; expectedControlVersion: number }): CallToolResult {
  const error = { code, message, ...(mayHaveApplied === undefined ? {} : { mayHaveApplied }), ...(operationId ? { operationId } : {}), ...(selector ? { selector } : {}), ...(taskSelector ? { taskSelector } : {}), ...(source ? { source } : {}) };
  return { isError: true, structuredContent: { error }, content: [{ type: 'text', text: JSON.stringify({ error }) }] };
}

export function createMcpServer(options: HostOptions): McpServer {
  const limits = options.limits ?? probeLimits;
  validateLimits(limits);
  const work = options.workBudget ?? new WorkBudget(limits.activeRequests);
  const productVersion = options.productVersion ?? defaultProductVersion;
  const catalog = options.capabilities ?? productCapabilities;
  const classifier = new ControlClassifier(catalog);
  const localReads = new LocalReads(catalog, productVersion);
  const server = new McpServer({ name: 'SkylinesLink', version: productVersion });
  const names = new Set<string>();
  for (const capability of catalog) {
    if (names.has(capability.toolName)) throw new Error('能力目录中存在重复工具名。');
    names.add(capability.toolName);
    const localRead = localReads.resolve(capability);
    server.registerTool(capability.toolName, {
      description: capability.description,
      inputSchema: schemaAdapter(capability.inputSchema, capability.validateInput),
      outputSchema: schemaAdapter(capability.outputSchema, capability.validateOutput),
      annotations: {
        readOnlyHint: capability.effect === 'read',
        destructiveHint: capability.effect !== 'read',
        idempotentHint: capability.effect === 'read',
        openWorldHint: false,
      },
    }, async (args, context) => {
      options.lifecycle?.handlerStarted(context.mcpReq.id, context.mcpReq.signal);
      let release: (() => void) | undefined;
      let operationId: string | undefined;
      let selector: OperationQueryV2['selector'] | undefined;
      let source: 'live' | 'history' | undefined;
      const isStop = capability.id === 'tasks.stop' && capability.schemaRevision === '1';
      const isStopRead = capability.id === 'tasks.stop.get' && capability.schemaRevision === '1';
      const moduleQuery = capability.id === 'system.modules.get' && capability.schemaRevision === '1'
        ? copyModuleRuntimeQuery(args as ModuleRuntimeQuery) : undefined;
      if (moduleQuery) args = copyModuleRuntimeQuery(moduleQuery) as Record<string, unknown>;
      const taskSelector = isStop || isStopRead ? copyStopSelector(args as { taskId: string; expectedControlVersion: number }) : undefined;
      if (taskSelector) {
        source = isStopRead && args.source === 'history' ? 'history' : 'live';
        args = { ...taskSelector, ...(isStopRead && args.source !== undefined ? { source: args.source } : {}) };
      }
      // SDK 已用同源 validator 验证输入；保存声明字段，后续连接失败也不丢失原查询身份。
      if (capability.id === 'operations.get' && (capability.schemaRevision === '3' || capability.schemaRevision === '4' || capability.schemaRevision === '5')) {
        const original = (args as OperationQueryV2).selector;
        selector = original.kind === 'operationId'
          ? { kind: 'operationId', operationId: original.operationId }
          : { kind: 'submissionKey', submissionKey: { writeContextId: original.submissionKey.writeContextId, requestId: original.submissionKey.requestId } };
        if (selector.kind === 'operationId') operationId = selector.operationId;
        if (capability.schemaRevision === '4' || capability.schemaRevision === '5') source = (args as OperationQueryV3).source ?? 'live';
      }
      const writeUncertainty = capability.effect === 'write' ? true : undefined;
      try {
        if (isStop) context.mcpReq.signal.throwIfAborted();
        release = work.acquire(classifier.capability(capability.id, capability.schemaRevision, args));
        // 撤权与实际游戏资格仍由执行边界判断，tool annotations 不是权限。
        if (localRead) context.mcpReq.signal.throwIfAborted();
        const result = localRead ? localRead(args)
          : await options.game.call(capability.id, capability.schemaRevision, args, context.mcpReq.signal);
        if (localRead) context.mcpReq.signal.throwIfAborted();
        if (capability.effect === 'write') operationId = typeof result === 'object' && result !== null && 'operationId' in result
          && typeof result.operationId === 'string' && result.operationId.length <= 128 ? result.operationId : undefined;
        const checked = capability.validateOutput(result);
        if (!checked.valid) {
          return localRead ? failure('INVALID_LOCAL_RESULT', 'Bundled product data did not satisfy its declared contract.', undefined)
            : failure('INVALID_GAME_RESULT', '游戏返回结果未满足契约，请按原身份查询操作状态。', writeUncertainty, operationId, selector, source, taskSelector);
        }
        if (isStop) requireStopResult(taskSelector!, result);
        if (isStopRead) requireStopDecisionLookup(args as { taskId: string; expectedControlVersion: number; source?: 'live' | 'history' }, result);
        if (moduleQuery) requireModuleRuntimePage(moduleQuery, result);
        if (capability.id === 'city.overview' && capability.schemaRevision === '2') requireCityOverview(result);
        if (selector && (capability.schemaRevision === '4' || capability.schemaRevision === '5')) {
          const verify = capability.schemaRevision === '5' ? requireOperationLookupV4 : requireOperationLookup;
          const checked = verify(args as OperationQueryV3, result);
          const body = checked.result;
          if (body.kind === 'history') { if (body.lookup.kind === 'found') operationId = body.lookup.record.operationId; }
          else if (body.lookup.kind === 'found' || body.lookup.kind === 'pending') operationId = body.lookup.operationId;
        } else if (selector) {
          const lookup = (result as OperationLookupV2).lookup;
          if (selector.kind === 'operationId' && (lookup.kind === 'expired'
            || lookup.kind === 'unavailable' && lookup.reason === 'CONTEXT_UNKNOWN'
            || (lookup.kind === 'found' || lookup.kind === 'pending') && lookup.operationId !== selector.operationId))
            return failure('INVALID_GAME_RESULT', '查询结果与原操作身份不一致，请继续按原身份查询。', undefined, operationId, selector, source);
          // 只消费已通过该能力输出契约的明确分支，不从任意嵌套对象猜测操作 ID。
          if (lookup.kind === 'found' || lookup.kind === 'pending') operationId = lookup.operationId;
        }
        measureJson(result, limits);
        const structuredContent = result as Record<string, unknown>;
        const response: CallToolResult = { structuredContent, content: [{ type: 'text', text: JSON.stringify(structuredContent) }] };
        measureJson(response, limits);
        return response;
      } catch (error) {
        if (error instanceof GameClientError) return failure(error.code, selector ? `${error.message} 本次读取未确认原操作结果。` : error.message,
          capability.effect === 'write' ? error.mayHaveApplied : undefined, operationId, selector, source, taskSelector);
        if (context.mcpReq.signal.aborted) return localRead ? failure('CANCELLED', 'The public product-data read was cancelled.', undefined)
          : failure('CANCELLED', '本次等待已取消，已受理操作仍须按原身份查询。', writeUncertainty, operationId, selector, source, taskSelector);
        if (localRead) return failure('LOCAL_READ_FAILURE', 'Bundled product data could not be read within the current limits.', undefined);
        return failure('HOST_FAILURE', '连接处理未完成，请检查连接状态并查询原操作。', writeUncertainty, operationId, selector, source, taskSelector);
      } finally {
        release?.();
        options.lifecycle?.handlerDone(context.mcpReq.id, context.mcpReq.signal);
      }
    });
  }
  return server;
}
