import { TextDecoder } from 'node:util';
import { TransportBudgetError, type TransportLimits } from './limits.ts';

const decoder = new TextDecoder('utf-8', { fatal: true });

/** 只扫描结构成本；语法与协议校验仍交给官方 SDK，不实现第二套 JSON parser。 */
export function checkRawJson(bytes: Uint8Array, limits: TransportLimits): string {
  if (bytes.byteLength > limits.messageBytes) throw new TransportBudgetError('消息超过字节上限。');
  const text = decoder.decode(bytes);
  let depth = 0;
  let tokens = 0;
  let inString = false;
  let escaped = false;
  let primitive = false;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    if (inString) {
      if (escaped) escaped = false;
      else if (c === 92) escaped = true;
      else if (c === 34) inString = false;
      continue;
    }
    if (c === 34) { inString = true; tokens++; primitive = false; }
    else if (c === 123 || c === 91) { depth++; tokens++; primitive = false; }
    else if (c === 125 || c === 93) { depth--; primitive = false; }
    else if (c === 44 || c === 58 || c <= 32) primitive = false;
    else if (!primitive) { primitive = true; tokens++; }
    if (depth > limits.jsonDepth || tokens > limits.jsonTokens) throw new TransportBudgetError('消息结构超过上限。');
  }
  return text;
}

/** 在 SDK 整条 stringify 前核普通值的成本；不调用属性 getter。 */
export function measureJson(value: unknown, limits: TransportLimits): number {
  const stack: Array<{ value: unknown; depth: number; leave?: boolean }> = [{ value, depth: 0 }];
  const seen = new Set<object>();
  let bytes = 0;
  // Reserve values before pushing them, including siblings still pending on the stack.
  // Leave markers do not represent JSON tokens and are separately bounded by nesting depth.
  let tokens = 1;
  while (stack.length > 0) {
    const item = stack.pop()!;
    if (item.leave) { seen.delete(item.value as object); continue; }
    if (tokens > limits.jsonTokens || item.depth > limits.jsonDepth) throw new TransportBudgetError('输出结构超过上限。');
    const v = item.value;
    if (v === null || v === undefined) bytes += 4;
    else if (typeof v === 'boolean') bytes += 5;
    else if (typeof v === 'number') {
      if (!Number.isFinite(v)) throw new TransportBudgetError('输出包含非有限数值。');
      bytes += String(v).length;
    } else if (typeof v === 'string') {
      if (v.length > limits.messageBytes) throw new TransportBudgetError('输出字符串超过上限。');
      bytes += Buffer.byteLength(JSON.stringify(v), 'utf8');
    } else if (typeof v === 'object') {
      const array = Array.isArray(v);
      const prototype = Object.getPrototypeOf(v) as unknown;
      if (array ? prototype !== Array.prototype : prototype !== Object.prototype && prototype !== null) throw new TransportBudgetError('输出必须是普通 JSON 对象或数组。');
      const toJson = Object.getOwnPropertyDescriptor(v, 'toJSON')
        ?? (prototype === null ? undefined : Object.getOwnPropertyDescriptor(prototype as object, 'toJSON'))
        ?? (array ? Object.getOwnPropertyDescriptor(Object.prototype, 'toJSON') : undefined);
      if (toJson && (!('value' in toJson) || typeof toJson.value === 'function')) throw new TransportBudgetError('输出不能包含自定义序列化。');
      if (array) {
        if (v.length > limits.jsonTokens - tokens) throw new TransportBudgetError('输出数组长度超过上限。');
        tokens += v.length;
      }
      if (seen.has(v)) throw new TransportBudgetError('输出不能包含循环。');
      seen.add(v);
      stack.push({ value: v, depth: item.depth, leave: true });
      bytes += 2;
      if (array) {
        // Count holes too; do not first copy every descriptor or build an entries array.
        bytes += v.length;
        if (bytes > limits.messageBytes) throw new TransportBudgetError('输出超过字节上限。');
        for (let index = 0; index < v.length; index++) {
          const key = String(index);
          const descriptor = Object.getOwnPropertyDescriptor(v, key)
            ?? Object.getOwnPropertyDescriptor(Array.prototype, key) ?? Object.getOwnPropertyDescriptor(Object.prototype, key);
          if (descriptor && !('value' in descriptor)) throw new TransportBudgetError('输出不能包含访问器。');
          stack.push({ value: descriptor?.value, depth: item.depth + 1 });
        }
      } else {
        // Only enumerable own keys participate in JSON.stringify. Per-property checks stop
        // before a descriptor-object copy of an oversized value; V8's key enumeration is not zero-allocation.
        for (const key in v) {
          if (!Object.hasOwn(v, key)) continue;
          tokens += 2; // The key and its value are both part of the global reservation.
          if (tokens > limits.jsonTokens) throw new TransportBudgetError('输出条目超过上限。');
          if (key.length > limits.messageBytes) throw new TransportBudgetError('输出键超过上限。');
          bytes += Buffer.byteLength(JSON.stringify(key), 'utf8') + 2;
          if (bytes > limits.messageBytes) throw new TransportBudgetError('输出超过字节上限。');
          const descriptor = Object.getOwnPropertyDescriptor(v, key);
          if (!descriptor || !('value' in descriptor)) throw new TransportBudgetError('输出不能包含访问器。');
          stack.push({ value: descriptor.value, depth: item.depth + 1 });
        }
      }
    } else throw new TransportBudgetError('输出不是 JSON 普通值。');
    if (bytes > limits.messageBytes) throw new TransportBudgetError('输出超过字节上限。');
  }
  return bytes;
}
