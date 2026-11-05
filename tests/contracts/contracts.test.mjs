import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';
import { compileSchema } from '../../tools/contracts/profile.mjs';
import { requireControlTransportProfile } from '../../tools/contracts/control-profile.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const bundle=await build({entryPoints:[path.join(root,'contracts/ts/index.ts')],bundle:true,write:false,platform:'neutral',format:'esm',target:'es2023'});
const contracts=await import('data:text/javascript;base64,'+Buffer.from(bundle.outputFiles[0].text).toString('base64'));
const fixtureSchema=JSON.parse(fs.readFileSync(path.join(root,'contracts/fixtures/profile-schema.json'),'utf8'));
const metadata=JSON.parse(fs.readFileSync(path.join(root,'contracts/fixtures/profile-metadata.json'),'utf8'));
const fixtureValidate=contracts.createValidator({[fixtureSchema.$id]:compileSchema(fixtureSchema,metadata)},{[fixtureSchema.$id]:fixtureSchema},[]);
const cases=['cases.json','operation-v2-cases.json','operation-journal-cases.json','operation-v3-cases.json','operation-journal-v2-cases.json','history-cases.json','mixed-history-cases.json','journal-control-cases.json','guidance-cases.json','authorization-v2-cases.json','mixed-journal-cases.json','journal-control-v2-cases.json','task-stop-cases.json','module-runtime-cases.json'].flatMap(name=>JSON.parse(fs.readFileSync(path.join(root,'contracts/fixtures',name),'utf8')));
for (const sample of cases) test('共享契约：'+sample.name,()=>{
  let outcome;
  try {
    const value=contracts.parseContractJson(sample.json);
    outcome=(sample.schemaId===fixtureSchema.$id?fixtureValidate:contracts.validateContract)(sample.schemaId,value,sample.binding);
  } catch(e) {
    if(!(e instanceof contracts.ContractValidationError))throw e;
    outcome={valid:false,issues:e.issues};
  }
  assert.equal(outcome.valid,sample.valid);
  if(sample.code)assert.equal(outcome.issues[0]?.code,sample.code);
});
test('授权2只增加明确格式与profile incarnation，原授权1结构保留',()=>{
  const previous=contracts.schemaById[contracts.schemaIds.AuthorizationSnapshot];
  const next=contracts.schemaById[contracts.schemaIds.AuthorizationSnapshotV2];
  const restored=structuredClone(next);
  restored.$id=previous.$id;
  delete restored.properties.formatVersion;
  restored.required=restored.required.filter(name=>name!=='formatVersion');
  delete restored.properties.profiles.items.properties.profileInstanceId;
  restored.properties.profiles.items.required=restored.properties.profiles.items.required.filter(name=>name!=='profileInstanceId');
  assert.deepEqual(restored,previous);
  assert.deepEqual(next.properties.formatVersion,{type:'integer',minimum:2,maximum:2});
  assert.deepEqual(next.properties.profiles.items.properties.profileInstanceId,{type:'string',minLength:32,maxLength:32});
  assert.equal(contracts.validateContract(contracts.schemaIds.AuthorizationSnapshot,JSON.parse(cases.find(sample=>sample.name==='authorization2/null-http-credential').json)).valid,false);
  assert.equal(contracts.capabilities.some(cap=>cap.inputId===next.$id||cap.outputId===next.$id),false);
});
test('混合日志的最长转义目标超过旧4KiB但处于独立96KiB元数据界内',()=>{
  const sample=cases.find(sample=>sample.name==='mixed3/maximum-escaped-goal');
  const value=contracts.parseContractJson(sample.json);
  assert.equal(contracts.validateContract(contracts.schemaIds.OperationJournalRecordV3,value).valid,true);
  const bytes=Buffer.byteLength(JSON.stringify(value));
  assert(bytes>4096&&bytes<=96*1024);
});
test('控制2的五种最长转义结构仍小于固定4052字节payload',()=>{
  const samples=cases.filter(sample=>sample.name.startsWith('control2/maximum-escaped-'));
  assert.equal(samples.length,5);
  for(const sample of samples)assert(Buffer.byteLength(JSON.stringify(contracts.parseContractJson(sample.json)))<=4052);
});
const simple={ $schema:'https://json-schema.org/draft/2020-12/schema',$id:'urn:skylineslink:contract:invalid-fixture:1',type:'string',minLength:0,maxLength:10 };
test('未知关键字和无界类型在构建时拒绝',()=>{
  assert.throws(()=>compileSchema({...simple,pattern:'.*'}),/不支持关键词/);
  const unbounded={...simple};delete unbounded.maxLength;
  assert.throws(()=>compileSchema(unbounded),/安全整数/);
  assert.throws(()=>compileSchema({$schema:simple.$schema,$id:simple.$id,type:'object',properties:{},required:[],additionalProperties:true}),/闭合/);
  assert.throws(()=>compileSchema({...simple,maxLength:2147483648}),/安全整数/);
  assert.throws(()=>compileSchema({...simple,examples:4}),/examples/);
  assert.throws(()=>compileSchema({...simple,$defs:{Hidden:{$id:'urn:skylineslink:contract:hidden:1',type:'null'}}}),/根文档/);
  assert.throws(()=>compileSchema({...simple,$defs:{Hidden:{type:'null',$defs:{Bad:{type:'string',pattern:'.*'}}}}}),/关键词/);
});
test('nullable null分支同样检查方言、注解和未引用定义',()=>{
  const nullable = branch => ({$schema:simple.$schema,$id:simple.$id,anyOf:[{type:'string',maxLength:10},branch]});
  for (const [branch, expected] of [
    [{type:'null',$defs:{Hidden:{type:'string',pattern:'.*'}}}, /关键词/],
    [{type:'null',$schema:'http://json-schema.org/draft-07/schema#'}, /方言/],
    [{type:'null',$id:'urn:skylineslink:contract:hidden:1'}, /根文档/],
    [{type:'null',description:42}, /字符串/],
    [{type:'null',examples:42}, /examples/],
  ]) assert.throws(()=>compileSchema(nullable(branch)),expected);
  assert.deepEqual(compileSchema(nullable({type:'null',title:'无值',description:'明确为空',examples:[null],$defs:{Unused:{type:'null'}}})),
    {type:'string',minLength:0,maxLength:10,nullable:true});
});
test('直接普通值校验拒绝稀疏数组，显式undefined不当作absent',()=>{
  for (const tags of [Array(1),[undefined]]) {
    const value=contracts.parseContractJson(cases[0].json); value.tags=tags;
    const result=fixtureValidate(fixtureSchema.$id,value);
    assert.equal(result.valid,false);
    assert.equal(result.issues[0]?.code,'TYPE');
    assert.equal(result.issues[0]?.path,'/tags/0');
  }
  const value=contracts.parseContractJson(cases[0].json); value.label=undefined;
  assert.equal(fixtureValidate(fixtureSchema.$id,value).valid,false);
});
test('直接普通值校验不接受序列化丢失的字段或执行getter',()=>{
  const hidden=contracts.parseContractJson(cases[0].json);
  Object.defineProperty(hidden,'amount',{value:hidden.amount,enumerable:false});
  assert.equal(fixtureValidate(fixtureSchema.$id,hidden).valid,false);
  let calls=0;
  const accessor=contracts.parseContractJson(cases[0].json);
  Object.defineProperty(accessor,'amount',{enumerable:true,get(){calls++;return '12.30';}});
  assert.equal(fixtureValidate(fixtureSchema.$id,accessor).valid,false);
  assert.equal(calls,0);
  const arrayAccessor=contracts.parseContractJson(cases[0].json);
  arrayAccessor.tags=['ok'];
  Object.defineProperty(arrayAccessor.tags,'0',{get(){calls++;return 'ok';}});
  assert.equal(fixtureValidate(fixtureSchema.$id,arrayAccessor).valid,false);
  assert.equal(calls,0);
});
test('普通值边界拒绝自定义对象与数组行为，保留null原型数据',()=>{
  const sample=contracts.parseContractJson(cases[0].json);
  for (const value of [new Date(0),Object.assign(new class Fixture {}(),sample)])
    assert.equal(fixtureValidate(fixtureSchema.$id,value).valid,false);
  for (const tags of [new class Tags extends Array {}('ok'),Object.assign(['ok'],{extra:true}),Object.assign(['ok'],{toJSON(){throw new Error('must not execute');}})]) {
    assert.equal(fixtureValidate(fixtureSchema.$id,{...sample,tags}).valid,false);
  }
  const symbolic={...sample,[Symbol('not-json')]:true};
  assert.equal(fixtureValidate(fixtureSchema.$id,symbolic).valid,false);
  const ordinary=Object.assign(Object.create(null),sample);
  assert.equal(fixtureValidate(fixtureSchema.$id,ordinary).valid,true);
  const encoded=JSON.stringify(ordinary);
  assert.equal(fixtureValidate(fixtureSchema.$id,contracts.parseContractJson(encoded)).valid,true);
  assert.equal(JSON.stringify(ordinary),encoded);
});
test('引用及判别联合须可静态解析且无重叠',()=>{
  const base={$schema:simple.$schema,$id:simple.$id};
  assert.throws(()=>compileSchema({...base,$ref:'https://example.invalid/schema'}),/本地/);
  assert.throws(()=>compileSchema({...base,$ref:'#/$defs/X',$defs:{X:{$ref:'#/$defs/X'}}}),/递归/);
  const branch=kind=>({type:'object',properties:{kind:{type:'string',const:kind,minLength:1,maxLength:1}},required:['kind'],additionalProperties:false});
  assert.throws(()=>compileSchema({...base,oneOf:[branch('a'),branch('a')]}),/重叠/);
  const nullable=branch('a');nullable.type=['object','null'];
  assert.throws(()=>compileSchema({...base,oneOf:[nullable,branch('b')]}),/kind常量/);
  assert.throws(()=>compileSchema({...base,anyOf:[branch('a'),branch('b')]}),/null/);
});
test('描述及原schema冻结，默认注解不变成隐式填充值',()=>{
  assert(Object.isFrozen(contracts.schemaById));
  const first=contracts.capabilities[0];
  assert(Object.isFrozen(first.inputSchema));
  const value=contracts.parseContractJson(cases[0].json);
  assert(fixtureValidate(fixtureSchema.$id,value).valid);
  assert(!Object.hasOwn(value,'defaulted'));
  const browse = {};
  assert.equal(contracts.validateContract(contracts.schemaIds.GuidanceSearchQuery,browse).valid,true);
  assert.deepEqual(browse,{},'language and limit defaults belong to the application, not schema mutation');
});
test('词法预算在重复键归并前生效，深度不会绕过边界',()=>{
  assert.throws(()=>contracts.parseContractJson('{'+Array.from({length:257},()=> '"a":0').join(',')+'}'),e=>e.issues[0].code==='BUDGET');
  assert.throws(()=>contracts.parseContractJson('['.repeat(34)+'0'+']'.repeat(34)),e=>e.issues[0].code==='BUDGET');
});

test('只有私有CP2文档获得大文本profile，普通JSON与旧历史预算保持',()=>{
  const sample=cases.find(sample=>sample.schemaId===contracts.schemaIds.OperationHistoryCheckpointV2&&sample.valid);
  const json=' '.repeat(1048576)+sample.json;
  assert.throws(()=>contracts.parseContractJson(json),error=>error.issues[0].code==='BUDGET');
  assert.throws(()=>contracts.parseAndValidateContract(contracts.schemaIds.OperationHistoryCheckpoint,json,contracts.validateContract),error=>error.issues[0].code==='BUDGET');
  const parsed=contracts.parseAndValidateContract(contracts.schemaIds.OperationHistoryCheckpointV2,json,contracts.validateContract);
  assert.equal(parsed.source.formatVersion,2);
  assert.throws(()=>contracts.parseAndValidateContract(contracts.schemaIds.OperationHistoryCheckpointV2,' '.repeat(6557653),contracts.validateContract),error=>error.issues[0].code==='BUDGET');
});

test('Stop与MCP初始化预算只有一份可信源并绑定正式写能力，调用方不能声明优先级',()=>{
  const source=JSON.parse(fs.readFileSync(path.join(root,'contracts/control-transport-profile.json'),'utf8'));
  const checked=requireControlTransportProfile(source,contracts.capabilities);
  assert.deepEqual(contracts.controlTransportProfile,checked);
  assert(Object.isFrozen(contracts.controlTransportProfile));
  assert.equal(checked.maximumRequestBytes,4096);
  assert.equal(checked.maximumResponseBytes,16384);
  assert.equal(checked.maximumOutputMetadataBytes,1024);
  assert.equal(checked.maximumSetupRequests,1);
  assert.equal(checked.maximumSetupRequestBytes,4096);
  assert.equal(checked.maximumSetupResponseBytes,16384);
  const descriptors=checked.maximumSetupRequests+checked.ordinaryActiveRequests+checked.maximumControlRequests;
  const replyBytes=3*(checked.maximumSetupRequests*checked.maximumSetupResponseBytes
    +checked.ordinaryActiveRequests*65536+checked.maximumControlRequests*checked.maximumResponseBytes);
  assert.equal(descriptors,17);
  assert.equal(replyBytes,2015232);
  assert(replyBytes<=2097152);
  const stop=contracts.capabilities.find(item=>item.id===checked.capabilityId&&item.schemaRevision===checked.schemaRevision);
  assert.equal(stop.effect,'write');
  assert.equal(stop.toolName,'skylines_task_stop');
  assert.equal(contracts.capabilities.find(item=>item.id==='tasks.stop.get').effect,'read');
  assert.equal(contracts.capabilities.some(item=>item.id==='tasks.create'),false);
  for(const change of [
    {priority:true},{capabilityId:'tasks.stop.get'},{schemaRevision:'2'},
    {maximumControlRequests:0},{maximumControlRequests:1.5},{windowMilliseconds:Infinity},
    {maximumSetupRequests:0},{maximumSetupRequests:1.5},{maximumSetupRequestBytes:0},
    {maximumSetupRequestBytes:20000},{maximumSetupResponseBytes:1048577},
    {maximumRequestBytes:20000},{maximumResponseBytes:1048577},{maximumOutputMetadataBytes:16384},
    {maximumControlMessagesPerWindow:1},
  ]) assert.throws(()=>requireControlTransportProfile({...source,...change},contracts.capabilities),/INVALID_CONTROL_TRANSPORT_PROFILE/);
  assert.throws(()=>requireControlTransportProfile(source,contracts.capabilities.map(item=>item.id==='tasks.stop'?{...item,effect:'read'}:item)),/INVALID_CONTROL_TRANSPORT_PROFILE/);
});

test('Stop完整4KiB输入与紧凑双投影回复保留metadata和失败预算，不扩大旧Native档',()=>{
  const profile=contracts.controlTransportProfile;
  const bytes=value=>Buffer.byteLength(JSON.stringify(value),'utf8');
  for(const scalar of ['\0','😀','"','\\']) {
    const pair={taskId:scalar.repeat(128),expectedControlVersion:Number.MAX_SAFE_INTEGER-1};
    const input={jsonrpc:'2.0',id:'',method:'tools/call',params:{name:'skylines_task_stop',arguments:pair}};
    const remaining=profile.maximumRequestBytes-bytes(input);
    input.id='\0'.repeat(Math.floor(remaining/6))+'a'.repeat(remaining%6);
    assert.equal(bytes(input),profile.maximumRequestBytes);
    assert(contracts.validateContract(contracts.schemaIds.TaskStopQuery,pair).valid);
    const result={selector:pair,lookup:{kind:'unavailable',receipt:{
      submissionKey:{writeContextId:scalar.repeat(128),requestId:scalar.repeat(128)},
      controlVersion:Number.MAX_SAFE_INTEGER,stopRequested:true,persistenceAttempted:true,
      journal:{storeId:scalar.repeat(128),sequence:Number.MAX_SAFE_INTEGER}
    },reason:'JOURNAL_UNAVAILABLE'}};
    assert(contracts.validateContract(contracts.schemaIds.TaskStopResult,result).valid);
    const reply={jsonrpc:'2.0',id:input.id,result:{structuredContent:result,content:[{type:'text',text:JSON.stringify(result)}]}};
    const before=bytes(reply);
    reply.result._meta={trace:''};
    reply.result._meta.trace='x'.repeat(profile.maximumOutputMetadataBytes-(bytes(reply)-before));
    assert.equal(bytes(reply)-before,profile.maximumOutputMetadataBytes);
    assert(bytes(reply)<=profile.maximumResponseBytes);
  }
  const selector={taskId:'\0'.repeat(128),expectedControlVersion:Number.MAX_SAFE_INTEGER-1};
  const error={error:{code:'\0'.repeat(64),message:'\0'.repeat(512),mayHaveApplied:true,stopSelector:selector}};
  const reply={jsonrpc:'2.0',id:'',result:{isError:true,structuredContent:error,content:[{type:'text',text:JSON.stringify(error)}]}};
  assert(bytes(reply)-2+profile.maximumRequestBytes+profile.maximumOutputMetadataBytes+256<=profile.maximumResponseBytes);
  assert.deepEqual(contracts.nativeHistoryLimits,{MaximumRequestBytes:4096,Version1MaximumResponseBytes:32768,
    Version2MaximumResponseBytes:32768,Version3MaximumResponseBytes:49152});
});
