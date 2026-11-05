import { canonical } from './profile.mjs';

// Emit only models consumed by desktop code; no game or reflection dependency.
export function generateDeployment(entries, sharedTypes, historyLimitsCs) {
  const names=['core-manifest','activation-record','authorization-snapshot','authorization-snapshot-v2','operation-history-checkpoint','operation-history-checkpoint-v2','native-history-request','native-history-capture','native-history-request-v2','native-history-capture-v2','native-history-request-v3','native-history-capture-v3'];
  const selected=entries.filter(e=>names.includes(e.name));
  if(selected.length!==names.length)throw new Error('桌面契约子集不完整');
  const pascal=s=>s[0].toUpperCase()+s.slice(1), title=s=>s.split('-').map(pascal).join('');
  const quote=s=>'@"'+s.replaceAll('"','""')+'"';
  const shape=s=>canonical(JSON.parse(JSON.stringify({...s,nullable:undefined},(k,v)=>['csName','deployName'].includes(k)?undefined:v)));
  const sharedNames=new Map(Object.entries(sharedTypes).map(([name,model])=>[shape(model),name]));
  const classes=[],methods=[],registered=new Set();
  function type(s) {
    const value=s.type==='object'||s.type==='union'?s.deployName:s.type==='array'?'List<'+type(s.items)+'>':s.type==='string'?'string':s.type==='integer'?'long':s.type==='number'?'double':s.type==='boolean'?'bool':null;
    if(!value)throw new Error('桌面子集不支持结构 '+s.type);
    return value+(s.nullable?'?':'');
  }
  function limits(s) { return s.minLength+', '+s.maxLength+', '+(s.const!==undefined?quote(s.const):'null')+', '+(s.enum?'new string[] { '+s.enum.map(quote).join(', ')+' }':'null'); }
  function check(s,v) {
    if(s.boundary||s.decimal)throw new Error('桌面子集出现未支持结构，先扩生成实现');
    if(s.nullable)return 'if('+v+'.ValueKind != JsonValueKind.Null) { '+check({...s,nullable:false},v)+' }';
    if(s.type==='object'||s.type==='union')return 'Validate'+s.deployName+'('+v+');';
    if(s.type==='array')return 'Array('+v+', '+s.minItems+', '+s.maxItems+'); foreach(var item in '+v+'.EnumerateArray()) { '+check(s.items,'item')+' }';
    if(s.type==='string')return 'String('+v+', '+limits(s)+');';
    if(s.type==='integer'||s.type==='number')return 'Number('+v+', '+s.minimum+'d, '+s.maximum+'d, '+(s.type==='integer')+');';
    if(s.type==='boolean')return 'if('+v+'.ValueKind != JsonValueKind.True && '+v+'.ValueKind != JsonValueKind.False) throw Invalid();';
    throw new Error('桌面子集不支持结构 '+s.type);
  }
  function read(s,v) {
    if(s.nullable)return '('+v+'.ValueKind == JsonValueKind.Null ? ('+type(s)+')null : '+read({...s,nullable:false},v)+')';
    return s.type==='object'||s.type==='union'?'Read'+s.deployName+'('+v+')':s.type==='array'?v+'.EnumerateArray().Select(item => '+read(s.items,'item')+').ToList()':s.type==='string'?v+'.GetString()!':s.type==='integer'?'checked((long)'+v+'.GetDouble())':s.type==='number'?v+'.GetDouble()':v+'.GetBoolean()';
  }
  function checkValue(s,v) {
    if(s.nullable)return 'if('+v+' != null) { '+checkValue({...s,nullable:false},['integer','number','boolean'].includes(s.type)?v+'.Value':v)+' }';
    if(s.type==='object'||s.type==='union')return 'CheckValue'+s.deployName+'('+v+');';
    if(s.type==='array')return 'if('+v+' == null || '+v+'.Count < '+s.minItems+' || '+v+'.Count > '+s.maxItems+') throw Invalid(); foreach(var item in '+v+') { '+checkValue(s.items,'item')+' }';
    if(s.type==='string')return 'StringValue('+v+', '+limits(s)+');';
    if(s.type==='integer'||s.type==='number')return 'NumberValue('+v+', '+s.minimum+'d, '+s.maximum+'d, '+(s.type==='integer')+');';
    if(s.type==='boolean')return '';
    throw new Error('桌面值校验不支持结构 '+s.type);
  }
  function write(s,v) {
    if(s.nullable)return 'if('+v+' == null) w.WriteNullValue(); else { '+write({...s,nullable:false},['integer','number','boolean'].includes(s.type)?v+'.Value':v)+' }';
    if(s.type==='object'||s.type==='union')return 'Write'+s.deployName+'(w, '+v+');';
    if(s.type==='array')return 'w.WriteStartArray(); foreach(var item in '+v+') { '+write(s.items,'item')+' } w.WriteEndArray();';
    return 'w.'+(s.type==='string'?'WriteStringValue':s.type==='boolean'?'WriteBooleanValue':'WriteNumberValue')+'('+v+');';
  }
  function register(s,initialName) {
    const name=sharedNames.get(shape(s))??initialName;
    if(s.type==='object'||s.type==='union') {
      s.deployName=name;
      if(registered.has(name))return;
      registered.add(name);
    }
    if(s.type==='object') {
      if(s.required.length!==Object.keys(s.properties).length)throw new Error('桌面子集可选字段须先实现presence表示');
      for(const [k,v] of Object.entries(s.properties))register(v,name+pascal(k));
      classes.push('public sealed class '+name+' { '+Object.entries(s.properties).map(([k,v])=>'public '+type(v)+' '+pascal(k)+' { get; set; } = default!;').join(' ')+' }');
      methods.push('private static void Validate'+name+'(JsonElement t) { Object(t, new string[] { '+Object.keys(s.properties).map(quote).join(', ')+' }, '+s.maxProperties+'); '+Object.entries(s.properties).map(([k,v],i)=>'var p'+i+'=t.GetProperty('+quote(k)+'); '+check(v,'p'+i)).join(' ')+' }');
      methods.push('private static '+name+' Read'+name+'(JsonElement t) => new '+name+' { '+Object.entries(s.properties).map(([k,v])=>pascal(k)+' = '+read(v,'t.GetProperty('+quote(k)+')')).join(', ')+' };');
      methods.push('private static void CheckValue'+name+'('+name+' v) { if(v == null) throw Invalid(); '+Object.entries(s.properties).map(([k,v])=>checkValue(v,'v.'+pascal(k))).join(' ')+' }');
      methods.push('private static void Write'+name+'(Utf8JsonWriter w, '+name+' v) { w.WriteStartObject(); '+Object.entries(s.properties).map(([k,v])=>'w.WritePropertyName('+quote(k)+'); '+write(v,'v.'+pascal(k))).join(' ')+' w.WriteEndObject(); }');
    } else if(s.type==='array')register(s.items,name+'Item');
    else if(s.type==='union') {
      classes.push('public abstract class '+name+' { internal '+name+'() {} }');
      for(const branch of s.branches) {
        register(branch,name+pascal(branch.properties.kind.const));
        const position=classes.findIndex(c=>c.startsWith('public sealed class '+branch.deployName+' {'));
        classes[position]=classes[position].replace('class '+branch.deployName+' {','class '+branch.deployName+' : '+name+' {');
      }
      methods.push('private static void Validate'+name+'(JsonElement t) { if(t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("kind",out var kind) || kind.ValueKind != JsonValueKind.String) throw Invalid(); switch(kind.GetString()) { '+s.branches.map(b=>'case '+quote(b.properties.kind.const)+': Validate'+b.deployName+'(t); return;').join(' ')+' default: throw Invalid(); } }');
      methods.push('private static '+name+' Read'+name+'(JsonElement t) { switch(t.GetProperty("kind").GetString()) { '+s.branches.map(b=>'case '+quote(b.properties.kind.const)+': return Read'+b.deployName+'(t);').join(' ')+' default: throw Invalid(); } }');
      for(const [action,body] of [['CheckValue',b=>'CheckValue'+b.deployName+'(item);'],['Write',b=>'Write'+b.deployName+'(w,item);']])
        methods.push('private static void '+action+name+'('+(action==='Write'?'Utf8JsonWriter w, ':'')+name+' v) { switch(v) { '+s.branches.map(b=>'case '+b.deployName+' item: '+body(b)+' return;').join(' ')+' default: throw Invalid(); } }');
    }
  }
  selected.forEach(e=>register(e.model,title(e.name)));
  // Validate the whole document without materializing its records list.
  const checkpoint=selected.find(e=>e.name==='operation-history-checkpoint');
  const checkpoint2=selected.find(e=>e.name==='operation-history-checkpoint-v2');
  const publicMethods=selected.filter(e=>e!==checkpoint&&e!==checkpoint2).map(e=>'public static '+title(e.name)+' Parse'+title(e.name)+'(string json) { using var doc=ParseDocument(json); Validate'+title(e.name)+'(doc.RootElement); return Read'+title(e.name)+'(doc.RootElement); }');
  publicMethods.push('public static JsonDocument ParseHistoryCheckpoint(string json) { var doc=ParseDocument(json); try { ValidateOperationHistoryCheckpoint(doc.RootElement); return doc; } catch { doc.Dispose(); throw; } }');
  const rootValidate=methods.findIndex(s=>s.startsWith('private static void ValidateOperationHistoryCheckpoint('));
  methods[rootValidate]=methods[rootValidate].replace('private static','public static');
  publicMethods.push('public static JsonDocument ParseHistoryCheckpointV2(string json) { StrictJsonSyntax.ValidateHistoryCheckpointV2(json); var doc=JsonDocument.Parse(json,new JsonDocumentOptions { MaxDepth=33, AllowTrailingCommas=false, CommentHandling=JsonCommentHandling.Disallow }); try { ValidateOperationHistoryCheckpointV2(doc.RootElement); return doc; } catch { doc.Dispose(); throw; } }');
  const rootValidate2=methods.findIndex(s=>s.startsWith('private static void ValidateOperationHistoryCheckpointV2('));
  methods[rootValidate2]=methods[rootValidate2].replace('private static','public static');
  publicMethods.push('public static HistorySourceV2 DecodeHistorySourceV2(JsonElement value) { ValidateHistorySourceV2(value); return ReadHistorySourceV2(value); }');
  publicMethods.push('public static TaskDecision DecodeTaskDecision(JsonElement value) { ValidateTaskDecision(value); return ReadTaskDecision(value); }');
  publicMethods.push('public static void WriteActivationRecord(Utf8JsonWriter writer, ActivationRecord value) { CheckValueActivationRecord(value); WriteActivationRecordCore(writer,value); }');
  for(let i=0;i<methods.length;i++)methods[i]=methods[i].replaceAll('WriteActivationRecord(', 'WriteActivationRecordCore(');
  for(const part of ['Source','Record']) publicMethods.push('public static History'+part+' DecodeHistory'+part+'(JsonElement value) { ValidateHistory'+part+'(value); return ReadHistory'+part+'(value); }');
  publicMethods.push('public static void WriteNativeHistoryCapture(Utf8JsonWriter writer, NativeHistoryCapture value) { CheckValueNativeHistoryCapture(value); WriteNativeHistoryCaptureCore(writer,value); }');
  publicMethods.push('public static void WriteNativeHistoryCaptureV2(Utf8JsonWriter writer, NativeHistoryCaptureV2 value) { CheckValueNativeHistoryCaptureV2(value); WriteNativeHistoryCaptureV2Core(writer,value); }');
  publicMethods.push('public static void WriteNativeHistoryCaptureV3(Utf8JsonWriter writer, NativeHistoryCaptureV3 value) { CheckValueNativeHistoryCaptureV3(value); WriteNativeHistoryCaptureV3Core(writer,value); }');
  for(let i=0;i<methods.length;i++)methods[i]=methods[i].replaceAll('WriteNativeHistoryCapture(', 'WriteNativeHistoryCaptureCore(');
  for(let i=0;i<methods.length;i++)methods[i]=methods[i].replaceAll('WriteNativeHistoryCaptureV2(', 'WriteNativeHistoryCaptureV2Core(');
  for(let i=0;i<methods.length;i++)methods[i]=methods[i].replaceAll('WriteNativeHistoryCaptureV3(', 'WriteNativeHistoryCaptureV3Core(');
  return ['// <auto-generated/>','#nullable enable','using System;','using System.Collections.Generic;','using System.Linq;','using System.Text.Json;',
    'namespace SkylinesLink.Deployment.Generated {',historyLimitsCs,...classes,'public static class DeploymentContract {',
    'private static FormatException Invalid() => new FormatException("桌面契约校验失败");',
    'private static JsonDocument ParseDocument(string json) { StrictJsonSyntax.Validate(json); return JsonDocument.Parse(json,new JsonDocumentOptions { MaxDepth=33, AllowTrailingCommas=false, CommentHandling=JsonCommentHandling.Disallow }); }',
    'private static void Object(JsonElement t, string[] keys, int maximum) { if(t.ValueKind != JsonValueKind.Object) throw Invalid(); var actual=new HashSet<string>(StringComparer.Ordinal); foreach(var p in t.EnumerateObject()) { if(!keys.Contains(p.Name,StringComparer.Ordinal)) throw Invalid(); actual.Add(p.Name); } if(actual.Count>maximum || keys.Any(k=>!actual.Contains(k))) throw Invalid(); }',
    'private static void Array(JsonElement t,int minimum,int maximum) { if(t.ValueKind != JsonValueKind.Array || t.GetArrayLength()<minimum || t.GetArrayLength()>maximum) throw Invalid(); }',
    'private static void String(JsonElement t,int minimum,int maximum,string? constant,string[]? choices) { if(t.ValueKind != JsonValueKind.String) throw Invalid(); StringValue(t.GetString(),minimum,maximum,constant,choices); }',
    'private static void StringValue(string? value,int minimum,int maximum,string? constant,string[]? choices) { if(value==null) throw Invalid(); var length=0; for(var i=0;i<value.Length;i++,length++) { if(char.IsHighSurrogate(value[i])) { if(++i>=value.Length || !char.IsLowSurrogate(value[i])) throw Invalid(); } else if(char.IsLowSurrogate(value[i])) throw Invalid(); } if(length<minimum || length>maximum || constant!=null && value!=constant || choices!=null && !choices.Contains(value,StringComparer.Ordinal)) throw Invalid(); }',
    'private static void Number(JsonElement t,double minimum,double maximum,bool integer) { if(t.ValueKind != JsonValueKind.Number || !t.TryGetDouble(out var n)) throw Invalid(); NumberValue(n,minimum,maximum,integer); }',
    'private static void NumberValue(double n,double minimum,double maximum,bool integer) { if(!double.IsFinite(n) || n<minimum || n>maximum || integer && (Math.Truncate(n)!=n || Math.Abs(n)>9007199254740991d)) throw Invalid(); }',
    ...methods,...publicMethods,'}','}',''].join('\n');
}
