import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonical, compileSchema } from './profile.mjs';
import { deriveContractIdentities, reconcileContractIdentities } from './identity.mjs';
import { generateDeployment } from './deployment.mjs';
import { requireControlTransportProfile } from './control-profile.mjs';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const check=process.argv.includes('--check');
const productVersion=JSON.parse(fs.readFileSync(path.join(root,'package.json'),'utf8')).version;
if(typeof productVersion!=='string' || productVersion.length>64 || !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.test(productVersion))
  throw new Error('package.json version 必须使用受支持的 major.minor.patch[-prerelease] 发行格式');
if(productVersion.split('-')[0].split('.').some(component=>Number(component)>65534))
  throw new Error('package.json version 的数字分量必须在 0—65534 内，以匹配受支持的 managed 构建版本');
const config=JSON.parse(fs.readFileSync(path.join(root,'contracts/capabilities.json'),'utf8'));
const controlTransportProfile=requireControlTransportProfile(
  JSON.parse(fs.readFileSync(path.join(root,'contracts/control-transport-profile.json'),'utf8')),config.capabilities);
const documents=fs.readdirSync(path.join(root,'contracts/schemas')).filter(f=>f.endsWith('.json')).sort().map(f=>{
  const name=f.slice(0,-5), schema=JSON.parse(fs.readFileSync(path.join(root,'contracts/schemas',f),'utf8'));
  return {name,schema};
});
const currentIdentities=deriveContractIdentities(documents,config);
const entries=documents.map(({name,schema})=>({name,schema,model:compileSchema(schema,currentIdentities.metadataByName.get(name))}));
// The finite profile deliberately permits local refs only. These self-contained
// documents share wire values; reject drift before generating either consumer.
const schemaNamed=name=>documents.find(e=>e.name===name)?.schema;
const historyNames=new Set(['operation-query-v3','operation-lookup-v3','operation-lookup-v4','operation-history-checkpoint','operation-history-checkpoint-v2','native-history-request','native-history-capture','native-history-request-v2','native-history-capture-v2','native-history-request-v3','native-history-capture-v3']);
const checkpointSchema=schemaNamed('operation-history-checkpoint');
if (!checkpointSchema || [...historyNames].some(name=>!schemaNamed(name))) throw new Error('历史契约子集不完整');
const sameShape=(a,b,label)=>{if(canonical(a)!==canonical(b))throw new Error('共享契约形状漂移：'+label);};
const checkpoint2Schema=schemaNamed('operation-history-checkpoint-v2');
if(!checkpoint2Schema)throw new Error('完整Task历史契约缺失');
// One private-document envelope for TS, managed game IO and the BCL desktop
// consumer. The 3x expansion plus fixed allowance is the reviewed CP2 proof.
const historyLimits={LegacyMaximumFileBytes:262144,
  MaximumSourceBytes:checkpoint2Schema.$defs.HistorySource.properties.throughBytes.maximum,
  FixedProjectionAllowance:262144+4096};
historyLimits.MaximumFileBytes=3*historyLimits.MaximumSourceBytes+historyLimits.FixedProjectionAllowance;
historyLimits.MaximumTextBytes=historyLimits.MaximumFileBytes-44;
const nativeHistoryLimits={MaximumRequestBytes:4096,Version1MaximumResponseBytes:32768,Version2MaximumResponseBytes:32768,Version3MaximumResponseBytes:49152};
const historyLimitsCs='public static class PrivateHistoryLimits { '+Object.entries(historyLimits)
  .map(([name,value])=>'public const int '+name+' = '+value+';').join(' ')+' }\npublic static class NativeHistoryLimits { '+Object.entries(nativeHistoryLimits)
  .map(([name,value])=>'public const int '+name+' = '+value+';').join(' ')+' }';
for(const key of ['Identity','HistoryContext','HistoryLoad','HistoryRecord'])
  sameShape(checkpoint2Schema.$defs[key],checkpointSchema.$defs[key],'MixedHistory/'+key);
sameShape(checkpoint2Schema.properties.records,checkpointSchema.properties.records,'MixedHistory/records');
const expectedHistorySource2=structuredClone(checkpointSchema.$defs.HistorySource);
expectedHistorySource2.properties.formatVersion={type:'integer',minimum:2,maximum:2};
expectedHistorySource2.properties.journalSchemaRevision={type:'integer',minimum:3,maximum:3};
expectedHistorySource2.properties.selection={type:'string',minLength:42,maxLength:42,const:'confirmedTaskMetadataAndOperationTerminals'};
expectedHistorySource2.properties.throughBytes={type:'integer',minimum:45,maximum:2097152};
expectedHistorySource2.required.push('throughBytes');
sameShape(checkpoint2Schema.$defs.HistorySource,expectedHistorySource2,'MixedHistory/source');
const taskHistoryKinds=['taskCreated','taskGoalRevised','taskStopped'];
sameShape(schemaNamed('native-history-request-v2').$defs.NativeHistoryQuery,
  schemaNamed('native-history-capture-v2').$defs.NativeHistoryQuery,'MixedNative/query');
for(const key of ['HistorySource','HistoryRecord','HistoryContext','HistoryLoad'])
  sameShape(schemaNamed('native-history-capture-v2').$defs[key],checkpoint2Schema.$defs[key],'MixedNative/'+key);
sameShape(schemaNamed('native-history-request-v3').$defs.NativeHistoryQuery,
  schemaNamed('native-history-capture-v3').$defs.NativeHistoryQuery,'DiscoveryNative/query');
const native2QuerySchema=schemaNamed('native-history-request-v2').$defs.NativeHistoryQuery;
const native3QuerySchema=schemaNamed('native-history-request-v3').$defs.NativeHistoryQuery;
sameShape(native3QuerySchema.oneOf.slice(0,native2QuerySchema.oneOf.length),native2QuerySchema.oneOf,'DiscoveryNative/priorQueries');
for(const [key,value] of Object.entries(schemaNamed('native-history-capture-v2').$defs)) {
  if(key==='NativeHistoryQuery'||key==='NativeHistoryResult')continue;
  sameShape(schemaNamed('native-history-capture-v3').$defs[key],value,'DiscoveryNative/'+key);
}
sameShape(schemaNamed('native-history-capture-v3').$defs.NativeHistoryResult.oneOf.slice(0,
  schemaNamed('native-history-capture-v2').$defs.NativeHistoryResult.oneOf.length),
  schemaNamed('native-history-capture-v2').$defs.NativeHistoryResult.oneOf,'DiscoveryNative/priorResults');
const stopNames=new Set(['task-stop-query','task-stop-result','task-stop-decision-query','task-stop-decision-lookup']);
if([...stopNames].some(name=>!schemaNamed(name)))throw new Error('停止契约子集不完整');
const stopQuerySchema=schemaNamed('task-stop-query');
const stopResultSchema=schemaNamed('task-stop-result');
const stopDecisionSchema=schemaNamed('task-stop-decision-lookup');
for(const field of ['taskId','expectedControlVersion'])
  sameShape(schemaNamed('task-stop-decision-query').properties[field],stopQuerySchema.properties[field],'StopQuery/'+field);
for(const doc of [stopResultSchema,stopDecisionSchema]) {
  sameShape(doc.$defs.Identity,stopQuerySchema.properties.taskId,'Stop/identity');
  sameShape(doc.$defs.TaskStopSelector,{type:'object',properties:{taskId:{$ref:'#/$defs/Identity'},
    expectedControlVersion:stopQuerySchema.properties.expectedControlVersion},required:stopQuerySchema.required,
    additionalProperties:false},'Stop/selector');
  sameShape(doc.$defs.SubmissionKey,schemaNamed('native-history-capture-v3').$defs.SubmissionKey,'Stop/key');
}
sameShape(stopDecisionSchema.$defs.JournalReceipt,stopResultSchema.$defs.JournalReceipt,'Stop/journal');
for(const field of ['Identity','PositiveSafeInteger','SubmissionKey','ContextSource','TaskDecisionActor','TaskDecision','TaskHistoryDecisionReceipt','HistorySource'])
  sameShape(stopDecisionSchema.$defs[field],schemaNamed('native-history-capture-v3').$defs[field],'StopHistory/'+field);
sameShape(checkpoint2Schema.$defs.TaskMetadataEntry.oneOf,
  schemaNamed('operation-journal-record-v3').properties.entry.oneOf.filter(branch=>taskHistoryKinds.includes(branch.properties.kind.const)),
  'MixedHistory/TaskMetadataEntry');
const control2=schemaNamed('journal-control-v2');
if(!control2)throw new Error('混合物理控制契约缺失');
const expectedControl2=structuredClone(schemaNamed('journal-control'));
expectedControl2.properties.formatVersion.minimum=2;expectedControl2.properties.formatVersion.maximum=2;
const mixedRoot=expectedControl2.properties.entry.oneOf.find(branch=>branch.properties.kind.const==='root');
mixedRoot.properties.layoutVersion.minimum=2;mixedRoot.properties.layoutVersion.maximum=2;
mixedRoot.properties.journalSchemaRevision={type:'integer',minimum:3,maximum:3};mixedRoot.required.push('journalSchemaRevision');
expectedControl2.properties.entry.oneOf.find(branch=>branch.properties.kind.const==='generation').properties.seedBytes.maximum=2097152;
expectedControl2.$defs.SourceCut.properties.throughBytes.maximum=2097152;
for(const key of ['properties','$defs','required','additionalProperties'])sameShape(control2[key],expectedControl2[key],'MixedControl/'+key);
for(const name of ['operation-history-checkpoint','operation-lookup-v3','operation-lookup-v4','native-history-capture']) {
  for(const key of ['Identity','HistorySource','HistoryContext','HistoryLoad','HistoryRecord'])
    sameShape(schemaNamed(name).$defs[key],checkpointSchema.$defs[key],name+'/'+key);
}
for(const name of ['operation-query-v3','operation-lookup-v3','operation-lookup-v4','native-history-request']) {
  sameShape(schemaNamed(name).$defs.OperationSelector,schemaNamed('operation-query-v2').properties.selector,name+'/selector');
  sameShape(schemaNamed(name).$defs.Identity,schemaNamed('operation-query-v2').$defs.Identity,name+'/Identity');
}
sameShape(checkpointSchema.$defs.HistoryContext,schemaNamed('operation-journal-record-v2').$defs.ContextSource,'HistoryContext');
// Mixed records preserve the declared Load facts. Actor provenance is the only
// addition to accepted; metadata branches do not alter the old record identity.
const mixedJournal=schemaNamed('operation-journal-record-v3');
if(!mixedJournal)throw new Error('混合日志契约缺失');
for(const key of ['sequence','environmentId'])
  sameShape(mixedJournal.properties[key],schemaNamed('operation-journal-record-v2').properties[key],'MixedJournal/'+key);
sameShape(mixedJournal.required,schemaNamed('operation-journal-record-v2').required,'MixedJournal/required');
sameShape(mixedJournal.additionalProperties,schemaNamed('operation-journal-record-v2').additionalProperties,'MixedJournal/closed');
for(const [key,value] of Object.entries(schemaNamed('operation-journal-record-v2').$defs))
  sameShape(mixedJournal.$defs[key],value,'MixedJournal/'+key);
for(const previous of schemaNamed('operation-journal-record-v2').properties.entry.oneOf) {
  const branch=structuredClone(mixedJournal.properties.entry.oneOf.find(item=>item.properties.kind.const===previous.properties.kind.const));
  if(!branch)throw new Error('混合日志缺少原操作阶段');
  if(previous.properties.kind.const==='accepted') {
    delete branch.properties.actorIdentity;
    branch.required=branch.required.filter(key=>key!=='actorIdentity');
  }
  sameShape(branch,previous,'MixedJournal/'+previous.properties.kind.const);
}
sameShape(schemaNamed('operation-lookup-v3').$defs.LiveLookup,schemaNamed('operation-lookup-v2').properties.lookup,'LiveLookup');
sameShape(schemaNamed('operation-lookup-v3').$defs.LoadObservation,schemaNamed('operation-lookup-v2').$defs.LoadObservation,'LiveLoadObservation');
// Retention changes only the live missing-coverage reason. Historical values,
// selectors and existing live facts keep their previously declared shapes.
const retainedLive=structuredClone(schemaNamed('operation-lookup-v4').$defs.LiveLookup);
const retainedReasons=retainedLive.oneOf.find(branch=>branch.properties.kind.const==='unavailable').properties.reason;
const previousReasons=schemaNamed('operation-lookup-v3').$defs.LiveLookup.oneOf.find(branch=>branch.properties.kind.const==='unavailable').properties.reason;
sameShape(retainedReasons.enum,[...previousReasons.enum,'RETENTION_NOT_COVERED'],'RetentionReason');
retainedReasons.enum=previousReasons.enum;
sameShape(retainedLive,schemaNamed('operation-lookup-v3').$defs.LiveLookup,'RetainedLiveLookup');
sameShape(schemaNamed('operation-lookup-v4').$defs.LoadObservation,schemaNamed('operation-lookup-v3').$defs.LoadObservation,'RetainedLoadObservation');
const guidanceOutputs=new Set(['guidance-search-result','guidance-document']);
const guidanceSearch=schemaNamed('guidance-search-result');
const guidanceDocument=schemaNamed('guidance-document');
if (!guidanceSearch || !guidanceDocument) throw new Error('公开指南契约子集不完整');
for(const name of ['guidance-search-query','guidance-query','guidance-document'])
  sameShape(schemaNamed(name)?.$defs.Language,guidanceSearch.$defs.Language,name+'/Language');
sameShape(guidanceSearch.$defs.CapabilityReference,guidanceDocument.$defs.CapabilityReference,'GuidanceCapabilityReference');
for(const [key,value] of Object.entries(guidanceSearch.$defs.GuidanceSummary.properties))
  sameShape(value,guidanceDocument.properties[key],'GuidanceSummary/'+key);
const checkpointModel=entries.find(e=>e.name==='operation-history-checkpoint').model;
const checkpoint2Model=entries.find(e=>e.name==='operation-history-checkpoint-v2').model;
const native2QueryModel=entries.find(e=>e.name==='native-history-request-v2').model.properties.query;
const native2ResultModel=entries.find(e=>e.name==='native-history-capture-v2').model.properties.result;
const native3QueryModel=entries.find(e=>e.name==='native-history-request-v3').model.properties.query;
const selectorModel=entries.find(e=>e.name==='operation-query-v3').model.properties.selector;
const journalControlModel=entries.find(e=>e.name==='journal-control').model;
const guidanceSearchModel=entries.find(e=>e.name==='guidance-search-result').model;
const mixedJournalModel=entries.find(e=>e.name==='operation-journal-record-v3').model;
const mixedCreated=mixedJournalModel.properties.entry.branches.find(branch=>branch.properties.kind.const==='taskCreated');
const stopQueryModel=entries.find(e=>e.name==='task-stop-query').model;
const stopResultModel=entries.find(e=>e.name==='task-stop-result').model;
const stopDecisionModel=entries.find(e=>e.name==='task-stop-decision-lookup').model;
const stopLiveLookup=stopDecisionModel.properties.result.branches.find(branch=>branch.properties.kind.const==='live').properties.lookup;
const stopHistoryLookup=stopDecisionModel.properties.result.branches.find(branch=>branch.properties.kind.const==='history').properties.lookup;
const modulePageModel=entries.find(e=>e.name==='module-runtime-page').model;
const modulePage=modulePageModel.branches.find(branch=>branch.properties.kind.const==='page');
const mixedControlModel=entries.find(e=>e.name==='journal-control-v2').model;
const sharedTypes={HistorySource:checkpointModel.properties.source,HistoryRecord:checkpointModel.properties.records.items,
  HistorySourceV2:checkpoint2Model.properties.source,TaskMetadataRow:checkpoint2Model.properties.taskRecords.items,
  TaskMetadataEntry:checkpoint2Model.properties.taskRecords.items.properties.entry,
  NativeHistoryQuery:native2QueryModel,
  NativeHistoryQueryV3:native3QueryModel,
  TaskHistorySummary:native2ResultModel.branches.find(branch=>branch.properties.kind.const==='task').properties.summary,
  TaskHistoryDecisionReceipt:native2ResultModel.branches.find(branch=>branch.properties.kind.const==='taskDecision').properties.receipt,
  TaskHistoryGoalPage:native2ResultModel.branches.find(branch=>branch.properties.kind.const==='taskGoal').properties.page,
  HistoryContext:checkpointModel.properties.records.items.properties.sourceContext,
  HistoryLoad:checkpointModel.properties.records.items.properties.load,OperationSelector:selectorModel,
  OperationSubmissionKey:selectorModel.branches[1].properties.submissionKey,
  JournalSourceCut:journalControlModel.properties.entry.branches.find(branch=>branch.properties.kind.const==='preparing').properties.source,
  JournalSourceCutV2:mixedControlModel.properties.entry.branches.find(branch=>branch.properties.kind.const==='preparing').properties.source,
  OperationActorIdentity:mixedJournalModel.properties.entry.branches.find(branch=>branch.properties.kind.const==='accepted').properties.actorIdentity,
  TaskDecision:mixedCreated.properties.decision,TaskDecisionActor:mixedCreated.properties.decision.properties.actor,
  TaskStatedIntent:mixedCreated.properties.goal,
  GuidanceSummary:guidanceSearchModel.properties.results.items,
  GuidanceCapabilityReference:guidanceSearchModel.properties.results.items.properties.capabilityRefs.items,
  TaskStopSelector:stopQueryModel,TaskStopLookup:stopResultModel.properties.lookup,
  TaskStopReceipt:stopResultModel.properties.lookup.branches[0].properties.receipt,
  TaskJournalReceipt:stopResultModel.properties.lookup.branches[0].properties.receipt.properties.journal,
  TaskStopDecisionReceipt:stopLiveLookup.branches[0].properties.receipt,
  TaskStopLiveLookup:stopLiveLookup,TaskStopHistoryLookup:stopHistoryLookup,
  ModuleRuntimeSnapshot:modulePage.properties.snapshot,
  ModuleRuntimeCoverage:modulePage.properties.snapshot.properties.coverage,
  ModuleRuntimeRow:modulePage.properties.entries.items,
  ModuleRuntimeFieldIssue:modulePage.properties.entries.items.properties.fieldIssues.items};
// Names are a code-generation convenience, never an immutable schema identity.
const modelShape=s=>canonical(JSON.parse(JSON.stringify({...s,nullable:undefined},(key,value)=>['csName','deployName'].includes(key)?undefined:value)));
const sharedTypeNames=new Map(Object.entries(sharedTypes).map(([name,model])=>[modelShape(model),name]));
const allowedBoundaries = new Set(['wire-request:/properties/arguments:registeredInput','wire-result:/properties/result:registeredOutput','capability-description:/properties/inputSchema:schemaDocument','capability-description:/properties/outputSchema:schemaDocument']);
for (const b of config.boundaries) if (!allowedBoundaries.has(b.schema+':'+b.path+':'+b.kind)) throw new Error('边界专用值不允许用于普通DTO');
const declarations=currentIdentities.declarations;
// Missing locks are errors. The reviewed initial semantic baseline is tracked;
// normal generation never bootstraps or resets an existing identity history.
const identities=reconcileContractIdentities(currentIdentities,
  JSON.parse(fs.readFileSync(path.join(root,'contracts/schema-identities.generated.json'),'utf8')),
  JSON.parse(fs.readFileSync(path.join(root,'contracts/semantic-identities.generated.json'),'utf8')));
const pascal=s=>s.split(/[^a-zA-Z0-9]+/).filter(Boolean).map(v=>v[0].toUpperCase()+v.slice(1)).join('');
const q=JSON.stringify;
const jsLiteral = value => Array.isArray(value) ? '['+value.map(jsLiteral).join(',')+']' : value && typeof value === 'object' ? '{'+Object.entries(value).map(([k,v])=>'['+q(k)+']:'+jsLiteral(v)).join(',')+'}' : q(value);
function tsType(s) {
  let t;
  switch(s.type){
    case 'object': t=s.boundary?'Record<string, JsonValue>':'{ '+Object.entries(s.properties).map(([k,v])=>'readonly '+q(k)+(s.required.includes(k)?'':'?')+': '+tsType(v)).join('; ')+' }';break;
    case 'array':t='ReadonlyArray<'+tsType(s.items)+'>';break;
    case 'string':t=s.const!==undefined?q(s.const):s.enum?s.enum.map(q).join(' | '):'string';break;
    case 'integer':case 'number':t='number';break;
    case 'boolean':t='boolean';break;
    case 'null':t='null';break;
    case 'union':t=s.branches.map(tsType).join(' | ');break;
    default:throw new Error('未知类型');
  }
  return s.nullable?'('+t+') | null':t;
}
const ts = [
  '// 此文件由 tools/contracts/generate.mjs 生成；修改源 schema / 声明后重新生成。',
  "import { createValidator, deepFreeze, ContractValidationError } from './runtime.js';",
  "import type { JsonValue, SchemaNode, CapabilityDeclaration, RegisteredBinding } from './runtime.js';",
  "export * from './runtime.js';",
  'export const schemaIds = '+q(Object.fromEntries(entries.map(e=>[pascal(e.name),e.schema.$id])))+' as const;',
  'export const schemaById: Readonly<Record<string, Readonly<Record<string, unknown>>>> = deepFreeze('+jsLiteral(Object.fromEntries(entries.map(e=>[e.schema.$id,e.schema])))+');',
  'const models: Readonly<Record<string, SchemaNode>> = deepFreeze('+jsLiteral(Object.fromEntries(entries.map(e=>[e.schema.$id,e.model])))+');',
  'const declarations: readonly CapabilityDeclaration[] = deepFreeze('+q(declarations)+');',
  'export const validateContract = createValidator(models, schemaById, declarations);',
  'export function requireContract(id: string, value: unknown, binding?: RegisteredBinding): void { const result=validateContract(id,value,binding); if(!result.valid)throw new ContractValidationError(result.issues); }',
  'export const capabilities = Object.freeze(declarations.map(c => Object.freeze({ ...c, inputSchema: schemaById[c.inputId]!, outputSchema: schemaById[c.outputId]!, validateInput: (value: unknown) => validateContract(c.inputId, value), validateOutput: (value: unknown) => validateContract(c.outputId, value) })));',
  ...entries.map(e=>'export type '+pascal(e.name)+' = '+tsType(e.model)+';'),
  ...Object.entries(sharedTypes).map(([name,model])=>'export type '+name+' = '+tsType(model)+';'),
  'export const nativeHistoryLimits = Object.freeze('+JSON.stringify(nativeHistoryLimits)+' as const);',
  'export const controlTransportProfile = Object.freeze('+JSON.stringify(controlTransportProfile)+' as const);',
  ''
].join('\n');

const csString=s=>'@"'+s.replaceAll('"','""')+'"';
const controlProfileCs=['// <auto-generated/>','namespace SkylinesLink.Contracts {',
  'public static class ControlTransportProfile { '+Object.entries(controlTransportProfile).map(([name,value])=>
    'public const '+(typeof value==='string'?'string ':'int ')+pascal(name)+' = '+(typeof value==='string'?csString(value):value)+';').join(' ')+' }','}',''].join('\n');
function nodeCs(s) {
  const p=['Type = '+csString(s.type)];
  if(s.nullable)p.push('Nullable = true');
  if(s.boundary)p.push('Boundary = '+csString(s.boundary));
  for(const key of ['maxProperties','minItems','maxItems','minLength','maxLength'])if(s[key]!==undefined)p.push(pascal(key)+' = '+s[key]);
  for(const key of ['minimum','maximum'])if(s[key]!==undefined)p.push(pascal(key)+' = '+s[key]+'d');
  if(s.const!==undefined)p.push('Const = '+csString(s.const));
  if(s.stringRule)p.push('StringRule = '+csString(s.stringRule));
  if(s.enum)p.push('Enum = new string[] { '+s.enum.map(csString).join(', ')+' }');
  if(s.required)p.push('Required = new string[] { '+s.required.map(csString).join(', ')+' }');
  if(s.properties)p.push('Properties = new Dictionary<string, SchemaNode>(StringComparer.Ordinal) { '+Object.entries(s.properties).map(([k,v])=>'{ '+csString(k)+', '+nodeCs(v)+' }').join(', ')+' }');
  if(s.items)p.push('Items = '+nodeCs(s.items));
  if(s.branches)p.push('Branches = new SchemaNode[] { '+s.branches.map(nodeCs).join(', ')+' }');
  if(s.decimal)p.push('Decimal = new DecimalRule { Signed = '+s.decimal.signed+', Scale = '+s.decimal.scale+', MaxDigits = '+s.decimal.maxDigits+' }');
  return 'new SchemaNode { '+p.join(', ')+' }';
}
const cs=[
  '// <auto-generated/>', '#nullable enable',
  'using System;', 'using System.Collections.Generic;', 'using System.Linq;', 'using Newtonsoft.Json.Linq;',
  'namespace SkylinesLink.Contracts {',
  historyLimitsCs,
  'public static class SchemaIds {',...entries.map(e=>'public const string '+pascal(e.name)+' = '+csString(e.schema.$id)+';'),'}',
  'public static class ContractCatalog {',
  'private static readonly IReadOnlyDictionary<string, SchemaNode> Models = new Dictionary<string, SchemaNode>(StringComparer.Ordinal) { '+entries.map(e=>'{ '+csString(e.schema.$id)+', '+nodeCs(e.model)+' }').join(', ')+' };',
  'private static readonly IReadOnlyDictionary<string, JToken> Schemas = new Dictionary<string, JToken>(StringComparer.Ordinal) { '+entries.map(e=>'{ '+csString(e.schema.$id)+', JToken.Parse('+csString(q(e.schema))+') }').join(', ')+' };',
  'public static IReadOnlyList<CapabilityDeclaration> Capabilities { get; } = Array.AsReadOnly(new CapabilityDeclaration[] { '+declarations.map(d=>'new CapabilityDeclaration('+[d.id,d.toolName,d.schemaRevision,d.description,d.effect,d.inputId,d.outputId].map(csString).join(', ')+')').join(', ')+' });',
  'public static JToken GetSchema(string id) => Schemas.TryGetValue(id, out var schema) ? schema.DeepClone() : throw new ArgumentException("未知schema", nameof(id));',
  'public static ValidationResult Validate(string id, JToken value, RegisteredBinding? binding = null) => ContractValidator.Validate(id, value, binding, Models, Schemas, Capabilities);',
  'public static JToken ParseAndValidate(string id, string json, RegisteredBinding? binding = null) { var value = id == SchemaIds.OperationHistoryCheckpointV2 ? ContractJson.ParseHistoryCheckpointV2(json) : ContractJson.Parse(json); Require(id, value, binding); return value; }',
  'public static void Require(string id, JToken value, RegisteredBinding? binding = null) { var result = Validate(id, value, binding); if (!result.Valid) throw new ContractValidationException(result.Issues); }',
  ...['Source','Record'].map(part=>'internal static void RequireHistory'+part+'(JToken value) { var root=Models[SchemaIds.OperationHistoryCheckpoint]; var model='+ (part==='Source'?'root.Properties["source"]':'root.Properties["records"].Items!')+'; var result=ContractValidator.Validate(SchemaIds.OperationHistoryCheckpoint,value,null,Models,Schemas,Capabilities,model); if(!result.Valid) throw new ContractValidationException(result.Issues); }'),
  '}',
];
const classes=[], methods=[], registeredNames=new Set();
function register(s,name,shared=false) {
  if(shared && sharedTypeNames.has(modelShape(s))) { name=sharedTypeNames.get(modelShape(s)); s.csName=name; if(registeredNames.has(name))return; }
  if(s.type==='object'&&!s.boundary) {
    if(registeredNames.has(name))throw new Error('派生类型重名：'+name);registeredNames.add(name);s.csName=name;
    const propNames=new Set();
    for(const [k,v] of Object.entries(s.properties)){const n=pascal(k);if(!n||propNames.has(n))throw new Error('C#属性名冲突');propNames.add(n);register(v,name+n,shared);}
    classes.push('public sealed class '+name+' { '+Object.entries(s.properties).map(([k,v])=>'public '+(s.required.includes(k)?csType(v):'Optional<'+csType(v)+'>')+' '+pascal(k)+' { get; set; }'+(s.required.includes(k)&&!['boolean','number','integer'].includes(v.type)?' = default!;':'')).join(' ')+' }');
    methods.push('private static '+name+' Read'+name+'(JToken t) => new '+name+' { '+Object.entries(s.properties).map(([k,v])=>pascal(k)+' = '+(s.required.includes(k)?decode(v,'t['+csString(k)+']!'):'((JObject)t).TryGetValue('+csString(k)+', StringComparison.Ordinal, out var p'+pascal(k)+') ? new Optional<'+csType(v)+'>( '+decode(v,'p'+pascal(k))+') : Optional<'+csType(v)+'>.Missing')).join(', ')+' };');
    const writes=Object.entries(s.properties).map(([k,v])=>s.required.includes(k)?'o.Add('+csString(k)+', '+encode(v,'v.'+pascal(k))+');':'if(v.'+pascal(k)+'.HasValue) o.Add('+csString(k)+', '+encode(v,'v.'+pascal(k)+'.Value')+');').join(' ');
    methods.push('private static JToken Write'+name+'('+name+' v) { var o=new JObject(); '+writes+' return o; }');
  } else if(s.type==='array')register(s.items,name+'Item',shared);
  else if(s.type==='union') {
    if(registeredNames.has(name))throw new Error('派生类型重名');registeredNames.add(name);s.csName=name;
    classes.push('public abstract class '+name+' { internal '+name+'() {} }');
    for(const b of s.branches){register(b,name+pascal(b.properties.kind.const),shared);const pos=classes.findIndex(c=>c.startsWith('public sealed class '+b.csName+' {'));classes[pos]=classes[pos].replace('class '+b.csName+' {','class '+b.csName+' : '+name+' {');}
    methods.push('private static '+name+' Read'+name+'(JToken t) { switch(t["kind"]!.Value<string>()) { '+s.branches.map(b=>'case '+csString(b.properties.kind.const)+': return Read'+b.csName+'(t);').join(' ')+' default: throw new InvalidOperationException("未知联合分支"); } }');
    methods.push('private static JToken Write'+name+'('+name+' v) { '+s.branches.map(b=>'if(v is '+b.csName+' x'+b.csName+') return Write'+b.csName+'(x'+b.csName+');').join(' ')+' throw new InvalidOperationException("未知联合分支"); }');
  }
}
function csType(s) {
  let t=s.type==='string'?'string':s.type==='integer'?'long':s.type==='number'?'double':s.type==='boolean'?'bool':s.type==='null'?'object':s.type==='array'?'List<'+csType(s.items)+'>':s.boundary?'JObject':s.csName;
  return t+(s.nullable||s.type==='null'?'?':'');
}
function decode(s,t,depth=0) {
  const inner=s.type==='string'?t+'.Value<string>()!':s.type==='integer'?'checked((long)'+t+'.Value<double>())':s.type==='number'?t+'.Value<double>()':s.type==='boolean'?t+'.Value<bool>()':s.type==='null'?'null':s.type==='array'?'((JArray)'+t+').Select(x'+depth+' => '+decode(s.items,'x'+depth,depth+1)+').ToList()':s.boundary?'(JObject)'+t+'.DeepClone()':'Read'+s.csName+'('+t+')';
  return s.nullable?'('+t+'.Type == JTokenType.Null ? ('+csType(s)+')null : '+inner+')':inner;
}
function encode(s,v,depth=0) {
  const inner=s.type==='null'?'JValue.CreateNull()':['string','integer','number','boolean'].includes(s.type)?'new JValue('+v+')':s.type==='array'?'new JArray('+v+'.Select(x'+depth+' => '+encode(s.items,'x'+depth,depth+1)+'))':s.boundary?v+'.DeepClone()':'Write'+s.csName+'('+v+')';
  return s.nullable?'('+v+' == null ? JValue.CreateNull() : '+inner+')':inner;
}
for(const e of entries)register(e.model,pascal(e.name),historyNames.has(e.name)||e.name==='journal-control'||e.name==='journal-control-v2'||e.name==='operation-journal-record-v3'||guidanceOutputs.has(e.name)||stopNames.has(e.name)||e.name==='module-runtime-page');
cs.push(...classes,'public static class ContractCodec {',...methods);
for(const e of entries) {
  const name=pascal(e.name),type=csType(e.model);
  cs.push('public static '+type+' Decode'+name+'(JToken value, RegisteredBinding? binding = null) { ContractCatalog.Require(SchemaIds.'+name+', value, binding); return '+decode(e.model,'value')+'; }');
  cs.push('public static JToken Encode'+name+'('+type+' value, RegisteredBinding? binding = null) { var token='+encode(e.model,'value')+'; ContractCatalog.Require(SchemaIds.'+name+', token, binding); return token; }');
}
for(const part of ['Source','Record'])cs.push('public static History'+part+' DecodeHistory'+part+'(JToken value) { ContractCatalog.RequireHistory'+part+'(value); return ReadHistory'+part+'(value); }');
cs.push('}','}','');
const fixtureSchema=JSON.parse(fs.readFileSync(path.join(root,'contracts/fixtures/profile-schema.json'),'utf8'));
const fixtureMetadata=JSON.parse(fs.readFileSync(path.join(root,'contracts/fixtures/profile-metadata.json'),'utf8'));
const fixtureModel=compileSchema(fixtureSchema,fixtureMetadata);
const fixtureNodeCs=nodeCs(fixtureModel);
classes.length=0;methods.length=0;registeredNames.clear();register(fixtureModel,'ProfileFixture');
const fixtureCs=['// <auto-generated/>','#nullable enable','using System;','using System.Collections.Generic;','using System.Linq;','using Newtonsoft.Json.Linq;','using SkylinesLink.Contracts;','namespace SkylinesLink.Contracts.Fixtures {',
'public static class FixtureCatalog {',
'private static readonly IReadOnlyDictionary<string,SchemaNode> Models = new Dictionary<string,SchemaNode> { { '+csString(fixtureSchema.$id)+', '+fixtureNodeCs+' } };',
'private static readonly IReadOnlyDictionary<string,JToken> Schemas = new Dictionary<string,JToken> { { '+csString(fixtureSchema.$id)+', JToken.Parse('+csString(q(fixtureSchema))+') } };',
'public static ValidationResult Validate(string id,JToken value) => ContractValidator.Validate(id,value,null,Models,Schemas,Array.Empty<CapabilityDeclaration>());',
'public static void Require(JToken value) { var r=Validate('+csString(fixtureSchema.$id)+',value);if(!r.Valid)throw new ContractValidationException(r.Issues); }',
'}',...classes,'public static class FixtureCodec {',...methods,
'public static ProfileFixture Decode(JToken value) { FixtureCatalog.Require(value); return ReadProfileFixture(value); }',
'public static JToken Encode(ProfileFixture value) { var token=WriteProfileFixture(value);FixtureCatalog.Require(token);return token; }','}','}',''].join('\n');

const syntaxTemplate=fs.readFileSync(path.join(root,'tools/contracts/StrictJsonSyntax.cs.template'),'utf8');
const outputs=new Map([
  ['tools/build/ProductVersion.generated.props','<!-- <auto-generated/> package.json is the product version authority. -->\n<Project><PropertyGroup><Version>'+productVersion+'</Version></PropertyGroup></Project>\n'],
  ['src/host/src/shared/product-version.generated.ts','// <auto-generated/> Derived from the root package.json.\nexport const productVersion = '+JSON.stringify(productVersion)+';\n'],
  ['tests/contracts/FixtureContracts.generated.cs',fixtureCs],
  ['contracts/cs/SkylinesLink.Contracts/StrictJsonSyntax.generated.cs',syntaxTemplate.replace('__NAMESPACE__','SkylinesLink.Contracts')],
  ['src/desktop/SkylinesLink.Deployment/Generated/StrictJsonSyntax.generated.cs',syntaxTemplate.replace('__NAMESPACE__','SkylinesLink.Deployment.Generated')],
  ['src/desktop/SkylinesLink.Deployment/Generated/DeploymentContracts.generated.cs',generateDeployment(entries,sharedTypes,historyLimitsCs)],
  ['contracts/ts/private-history-limits.generated.ts','// <auto-generated/>\nexport const privateHistoryLimits = Object.freeze('+JSON.stringify(historyLimits)+' as const);\n'],
  ['contracts/ts/index.ts',ts],
  ['contracts/cs/SkylinesLink.Contracts/ControlTransportProfile.generated.cs',controlProfileCs],
  ['contracts/cs/SkylinesLink.Contracts/Contracts.generated.cs',cs.join('\n')],
  ['contracts/schema-identities.generated.json',JSON.stringify(identities.documents,null,2)+'\n'],
  ['contracts/semantic-identities.generated.json',JSON.stringify(identities.semantics,null,2)+'\n']
]);
let stale=false;
for(const [relative,content] of outputs) {
  const target=path.join(root,relative);
  if(check){if(!fs.existsSync(target)||fs.readFileSync(target,'utf8')!==content){console.error('派生产物不一致：'+relative);stale=true;}}
  else { fs.mkdirSync(path.dirname(target),{recursive:true}); fs.writeFileSync(target,content,'utf8'); }
}
if(stale)process.exitCode=1;
else console.log((check?'契约派生检查通过':'契约生成完成')+'：'+entries.length+' schemas / '+declarations.length+' capabilities');
