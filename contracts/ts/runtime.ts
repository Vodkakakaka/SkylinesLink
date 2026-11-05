import { privateHistoryLimits } from './private-history-limits.generated.js';

export interface ValidationIssue { readonly path: string; readonly code: string; readonly message: string }
export interface ValidationResult { readonly valid: boolean; readonly issues: readonly ValidationIssue[] }
export interface RegisteredBinding { readonly capabilityId: string; readonly schemaRevision: string }
export type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue };
export interface SchemaNode {
  readonly type: string; readonly nullable?: boolean; readonly properties?: Readonly<Record<string, SchemaNode>>;
  readonly required?: readonly string[]; readonly maxProperties?: number; readonly items?: SchemaNode;
  readonly minItems?: number; readonly maxItems?: number; readonly minLength?: number; readonly maxLength?: number;
  readonly enum?: readonly string[]; readonly const?: string; readonly minimum?: number; readonly maximum?: number;
  readonly branches?: readonly SchemaNode[]; readonly boundary?: string;
  readonly stringRule?: 'hex32Lower' | 'assemblyVersion';
  readonly decimal?: { readonly signed: boolean; readonly scale: number; readonly maxDigits: number };
}
export interface CapabilityDeclaration { readonly id: string; readonly toolName: string; readonly schemaRevision: string; readonly description: string; readonly effect: 'read' | 'write'; readonly inputId: string; readonly outputId: string }
const own = (obj: object, key: string): boolean => Object.prototype.hasOwnProperty.call(obj, key);
const pointer = (path: string, key: string | number): string => path + '/' + String(key).replaceAll('~', '~0').replaceAll('/', '~1');
const object = (v: unknown): v is Record<string, unknown> => v !== null && typeof v === 'object' && !Array.isArray(v) && (Object.getPrototypeOf(v) === Object.prototype || Object.getPrototypeOf(v) === null);
export function validUnicode(s: string): boolean {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) { const next = s.charCodeAt(++i); if (!(next >= 0xdc00 && next <= 0xdfff)) return false; }
    else if (c >= 0xdc00 && c <= 0xdfff) return false;
  }
  return true;
}
function scalarLength(s: string): number { let n = 0; for (const _ of s) n++; return n; }
function validStringRule(text: string, rule: NonNullable<SchemaNode['stringRule']>): boolean {
  if (rule === 'hex32Lower') return text.length === 32 && [...text].every(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
  if (rule !== 'assemblyVersion' || text.length < 1 || text.length > 32) return false;
  let parts = 1, digit = false;
  for (const c of text) {
    if (c >= '0' && c <= '9') { digit = true; continue; }
    if (c !== '.' || !digit || parts === 4) return false;
    parts++; digit = false;
  }
  return digit;
}
function canonical(v: unknown): string {
  if (Array.isArray(v)) return '[' + v.map(canonical).join(',') + ']';
  if (object(v)) return '{' + Object.keys(v).sort().map(k => JSON.stringify(k) + ':' + canonical(v[k])).join(',') + '}';
  return JSON.stringify(v);
}
export function deepFreeze<T>(value: T): T {
  if (value && typeof value === 'object' && !Object.isFrozen(value)) {
    for (const child of Object.values(value)) deepFreeze(child);
    Object.freeze(value);
  }
  return value;
}
export class ContractValidationError extends Error {
  constructor(readonly issues: readonly ValidationIssue[]) { super('契约校验失败'); this.name = 'ContractValidationError'; }
}

export function createValidator(models: Readonly<Record<string, SchemaNode>>, schemas: Readonly<Record<string, Readonly<Record<string, unknown>>>>, declarations: readonly CapabilityDeclaration[]) {
  const caps = new Map(declarations.map(c => [c.id, c]));
  const knownDocuments = new Map(Object.entries(schemas).map(([id, s]) => [id, canonical(s)]));
  return function validateContract(schemaId: string, value: unknown, binding?: RegisteredBinding): ValidationResult {
    const issues: ValidationIssue[] = [];
    const error = (path: string, code: string): false => { if (issues.length === 0) issues.push({ path, code, message: '输入不符合契约约束：' + code }); return false; };
    let nodes = 0;
    const inspect = (v: unknown, path: string, depth: number): boolean => {
      if (++nodes > 16384 || depth > 32) return error(path, 'BUDGET');
      if (v === null || typeof v === 'boolean') return true;
      if (typeof v === 'number') return Number.isFinite(v) || error(path, 'NUMBER');
      if (typeof v === 'string') return validUnicode(v) ? (scalarLength(v) <= 65536 || error(path, 'BUDGET')) : error(path, 'UNICODE');
      if (Array.isArray(v)) {
        if (v.length > 4096) return error(path, 'BUDGET');
        if (Object.getPrototypeOf(v) !== Array.prototype) return error(path, 'TYPE');
        const keys = Reflect.ownKeys(v);
        if (keys.length > v.length + 1) return error(path, 'TYPE');
        for (let i = 0; i < v.length; i++) {
          const item = Object.getOwnPropertyDescriptor(v, String(i));
          // Holes become null during JSON encoding; accessors are not data.
          if (!item || !own(item, 'value')) return error(pointer(path, i), 'TYPE');
          if (!inspect(item.value, pointer(path, i), depth + 1)) return false;
        }
        return true;
      }
      if (!object(v)) return error(path, 'TYPE');
      const keys = Reflect.ownKeys(v);
      if (keys.length > 256) return error(path, 'BUDGET');
      for (const k of keys) {
        if (typeof k !== 'string') return error(path, 'TYPE');
        if (!validUnicode(k)) return error(pointer(path, k), 'UNICODE');
        if (scalarLength(k) > 65536) return error(pointer(path, k), 'BUDGET');
        const property = Object.getOwnPropertyDescriptor(v, k);
        if (!property || !property.enumerable || !own(property, 'value')) return error(pointer(path, k), 'TYPE');
        if (!inspect(property.value, pointer(path, k), depth + 1)) return false;
      }
      return true;
    };
    const check = (s: SchemaNode, v: unknown, path: string, root: unknown): boolean => {
      if (v === null && (s.nullable || s.type === 'null')) return true;
      switch (s.type) {
        case 'object': {
          if (!object(v)) return error(path, 'TYPE');
          if (Object.keys(v).length > (s.maxProperties ?? 0)) return error(path, 'PROPERTIES');
          if (s.boundary === 'schemaDocument') {
            const id = v['$id'];
            return typeof id === 'string' && knownDocuments.get(id) === canonical(v) || error(path, 'SCHEMA_DOCUMENT');
          }
          if (s.boundary) {
            const b = s.boundary === 'registeredInput' && object(root) ? { capabilityId: root['capabilityId'], schemaRevision: root['schemaRevision'] } : binding;
            if (!b || typeof b.capabilityId !== 'string' || typeof b.schemaRevision !== 'string') return error(path, 'BINDING_REQUIRED');
            const cap = caps.get(b.capabilityId);
            if (!cap) return error(path, 'UNKNOWN_CAPABILITY');
            if (cap.schemaRevision !== b.schemaRevision) return error(path, 'SCHEMA_REVISION');
            const innerId = s.boundary === 'registeredInput' ? cap.inputId : cap.outputId;
            return check(models[innerId]!, v, path, v);
          }
          for (const key of s.required ?? []) if (!own(v, key)) return error(pointer(path, key), 'REQUIRED');
          for (const key of Object.keys(v)) if (!own(s.properties!, key)) return error(pointer(path, key), 'EXTRA_PROPERTY');
          for (const [key, child] of Object.entries(s.properties!)) if (own(v, key) && !check(child, v[key], pointer(path, key), root)) return false;
          return true;
        }
        case 'array':
          if (!Array.isArray(v)) return error(path, 'TYPE');
          if (v.length < s.minItems! || v.length > s.maxItems!) return error(path, 'LENGTH');
          for (let i = 0; i < v.length; i++) if (!check(s.items!, v[i], pointer(path, i), root)) return false;
          return true;
        case 'string': {
          if (typeof v !== 'string') return error(path, 'TYPE');
          const len = scalarLength(v);
          if (len < s.minLength! || len > s.maxLength!) return error(path, 'LENGTH');
          if (s.enum && !s.enum.includes(v) || s.const !== undefined && s.const !== v) return error(path, 'ENUM');
          if (s.stringRule && !validStringRule(v, s.stringRule)) return error(path, 'PATTERN');
          if (s.decimal) {
            const d = s.decimal; const body = v.startsWith('-') ? v.slice(1) : v;
            if (v.startsWith('-') && !d.signed) return error(path, 'DECIMAL');
            const parts = body.split('.');
            if (parts.length !== (d.scale === 0 ? 1 : 2) || !/^(0|[1-9][0-9]*)$/.test(parts[0]!) || (d.scale > 0 && (parts[1]!.length !== d.scale || !/^[0-9]+$/.test(parts[1]!)))) return error(path, 'DECIMAL');
            const digits = parts.join('');
            if (digits.length > d.maxDigits || v.startsWith('-') && /^0+$/.test(digits)) return error(path, 'DECIMAL');
          }
          return true;
        }
        case 'integer': case 'number':
          if (typeof v !== 'number' || !Number.isFinite(v)) return error(path, 'TYPE');
          if (s.type === 'integer' && !Number.isSafeInteger(v)) return error(path, 'INTEGER');
          return v >= s.minimum! && v <= s.maximum! || error(path, 'RANGE');
        case 'boolean': return typeof v === 'boolean' || error(path, 'TYPE');
        case 'null': return error(path, 'TYPE');
        case 'union': {
          if (!object(v)) return error(path, 'TYPE');
          const branch = s.branches!.find(b => b.properties!['kind']!.const === v['kind']);
          return branch ? check(branch, v, path, root) : error(pointer(path, 'kind'), 'DISCRIMINATOR');
        }
        default: return error(path, 'SCHEMA');
      }
    };
    const model = own(models, schemaId) ? models[schemaId] : undefined;
    if (!model) error('', 'UNKNOWN_SCHEMA');
    else if (inspect(value, '', 0)) check(model, value, '', value);
    return { valid: issues.length === 0, issues };
  };
}

// 与C#边界相同：在JSON.parse之前守住词法预算及Unicode，重复键仍取后值。
export function parseContractJson(text: string): unknown {
  return parseContractJsonWithin(text, 1048576);
}
function parseContractJsonWithin(text: string, maximumTextLength: number): unknown {
  let position = 0, nodes = 0;
  const invalid = (code = 'JSON'): never => { throw new ContractValidationError([{ path: '', code, message: '输入不符合契约约束：' + code }]); };
  if (text.length > maximumTextLength) invalid('BUDGET');
  const current = (): string => text[position] ?? '';
  const white = (): void => { while ([' ', '\t', '\r', '\n'].includes(current())) position++; };
  const take = (char: string): void => { if (current() !== char) invalid(); position++; };
  const string = (): void => {
    take('"'); let high = false, length = 0;
    while (current() !== '"') {
      if (position >= text.length) invalid();
      let code = text.charCodeAt(position++);
      if (code < 0x20) invalid();
      if (code === 0x5c) {
        const escape = text[position++];
        const simple: Record<string, number> = { '"': 34, '\\': 92, '/': 47, b: 8, f: 12, n: 10, r: 13, t: 9 };
        if (escape === 'u') {
          const digits = text.slice(position, position + 4);
          if (!/^[0-9a-fA-F]{4}$/.test(digits)) invalid();
          code = parseInt(digits, 16); position += 4;
        } else { if (escape === undefined || !own(simple, escape)) return invalid(); code = simple[escape]!; }
      }
      if (high) { if (code < 0xdc00 || code > 0xdfff) invalid('UNICODE'); high = false; }
      else if (code >= 0xd800 && code <= 0xdbff) high = true;
      else if (code >= 0xdc00 && code <= 0xdfff) invalid('UNICODE');
      if (!high && ++length > 65536) invalid('BUDGET');
    }
    if (high) invalid('UNICODE'); take('"');
  };
  const value = (depth: number): void => {
    if (++nodes > 16384 || depth > 32) invalid('BUDGET');
    white();
    if (current() === '{') {
      position++; white(); if (current() === '}') { position++; return; }
      let count = 0;
      for (;;) { if (++count > 256) invalid('BUDGET'); string(); white(); take(':'); value(depth + 1); white(); if (current() === '}') { position++; return; } take(','); white(); }
    }
    if (current() === '[') {
      position++; white(); if (current() === ']') { position++; return; }
      let count = 0;
      for (;;) { if (++count > 4096) invalid('BUDGET'); value(depth + 1); white(); if (current() === ']') { position++; return; } take(','); white(); }
    }
    if (current() === '"') { string(); return; }
    for (const literal of ['true', 'false', 'null']) if (current() === literal[0]) { for (const c of literal) take(c); return; }
    const digit = (): boolean => current() >= '0' && current() <= '9';
    if (current() === '-') position++;
    if (current() === '0') position++;
    else { if (current() < '1' || current() > '9' || current() === '') invalid(); while (digit()) position++; }
    if (current() === '.') { position++; if (!digit()) invalid(); while (digit()) position++; }
    if (current() === 'e' || current() === 'E') { position++; if (current() === '+' || current() === '-') position++; if (!digit()) invalid(); while (digit()) position++; }
  };
  white(); value(0); white(); if (position !== text.length) invalid();
  try { return JSON.parse(text) as unknown; } catch { return invalid(); }
}
export function parseAndValidateContract(schemaId: string, text: string, validate: (id: string, value: unknown, binding?: RegisteredBinding) => ValidationResult, binding?: RegisteredBinding): unknown {
  const value = schemaId === 'urn:skylineslink:contract:operation-history-checkpoint:2'
    ? parseContractJsonWithin(text, privateHistoryLimits.MaximumTextBytes) : parseContractJson(text);
  const result = validate(schemaId, value, binding);
  if (!result.valid) throw new ContractValidationError(result.issues);
  return value;
}
