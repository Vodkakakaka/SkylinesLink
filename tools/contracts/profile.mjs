import { createHash } from 'node:crypto';

export function canonical(value) {
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']';
  if (value && typeof value === 'object') return '{' + Object.keys(value).sort().map(k => JSON.stringify(k) + ':' + canonical(value[k])).join(',') + '}';
  return JSON.stringify(value);
}
export const schemaHash = value => createHash('sha256').update(canonical(value)).digest('hex');
const own = (obj, key) => Object.prototype.hasOwnProperty.call(obj, key);
const allowed = new Set(['$schema','$id','$defs','$ref','title','description','default','examples','type','properties','required','additionalProperties','maxProperties','items','minItems','maxItems','minLength','maxLength','enum','const','pattern','minimum','maximum','oneOf','anyOf']);
const stringPatterns=new Map([
  ['^[0-9a-f]{32}$','hex32Lower'],
  ['^[0-9]+(?:\\.[0-9]+){0,3}$(?![\\s\\S])','assemblyVersion']
]);
function validStringRule(text,rule) {
  if(rule==='hex32Lower')return text.length===32 && [...text].every(c=>c>='0'&&c<='9'||c>='a'&&c<='f');
  if(rule!=='assemblyVersion'||text.length<1||text.length>32)return false;
  let parts=1,digit=false;
  for(const c of text) {
    if(c>='0'&&c<='9'){digit=true;continue;}
    if(c!=='.'||!digit||parts===4)return false;
    parts++;digit=false;
  }
  return digit;
}
const annotations = new Set(['$schema','$id','$defs','title','description','default','examples']);
const safe = Number.MAX_SAFE_INTEGER;
const fail = (where,message) => { throw new Error(where + ': ' + message); };
const integer = (v, where, min=0) => { if (!Number.isSafeInteger(v) || v < min || v > 2147483647) fail(where,'必须是有限的安全整数'); return v; };
const escape = s => s.replaceAll('~','~0').replaceAll('/','~1');

export function compileSchema(document, { boundaries = [], decimalFields = [] } = {}) {
  if (document.$schema !== 'https://json-schema.org/draft/2020-12/schema') fail('$schema','只接受已选定2020-12方言');
  if (typeof document.$id !== 'string' || !/^urn:skylineslink:contract:[a-z][a-z0-9-]*:[1-9][0-9]*$/.test(document.$id)) fail('$id','需要不可变产品契约身份');
  const boundary = new Map(boundaries.map(x=>[x.path,x.kind]));
  const decimal = new Map(decimalFields.map(x=>[x.path,x]));
  const used = new Set();
  const usedDecimals = new Set();
  const resolve = pointer => {
    if (!pointer.startsWith('#/$defs/')) fail(pointer,'只允许本地$defs引用');
    let v=document;
    for(const s of pointer.slice(2).split('/')) {
      const k=s.replaceAll('~1','/').replaceAll('~0','~');
      if (!v || !own(v,k)) fail(pointer,'引用目标不存在');
      v=v[k];
    }
    return v;
  };
  const compile = (s,path,stack=[]) => {
    if (!s || typeof s!=='object' || Array.isArray(s)) fail(path,'schema须是对象');
    for (const key of Object.keys(s)) if (!allowed.has(key)) fail(path,'不支持关键词 '+key);
    if (own(s,'pattern') && !stringPatterns.has(s.pattern)) fail(path,'不支持关键词 pattern 的值');
    if (own(s,'$id') && path !== '') fail(path,'$id只允许根文档，子定义不能改变引用基准');
    if (own(s,'$schema') && s.$schema !== document.$schema) fail(path,'子定义不能切换方言');
    for (const annotation of ['title','description']) if (own(s,annotation) && typeof s[annotation] !== 'string') fail(path,'注解必须为字符串');
    if (own(s,'examples') && !Array.isArray(s.examples)) fail(path,'examples必须为数组');
    if (own(s,'$defs') && (!s.$defs || typeof s.$defs!=='object'||Array.isArray(s.$defs))) fail(path,'$defs须是对象');
    for (const [name,def] of Object.entries(s.$defs??{})) compile(def,path+'/$defs/'+escape(name),stack);

    const relevant=Object.keys(s).filter(k=>!annotations.has(k));
    const only=keys=>{for(const k of relevant) if(!keys.includes(k)) fail(path,'该结构不能包含 '+k);};
    if (own(s,'$ref')) {
      only(['$ref']);
      if(typeof s.$ref!=='string'||stack.includes(s.$ref)) fail(path,'禁止远程或递归引用');
      return compile(resolve(s.$ref),s.$ref.slice(1),[...stack,s.$ref]);
    }
    if (own(s,'anyOf')) {
      only(['anyOf']);
      if (!Array.isArray(s.anyOf)||s.anyOf.length!==2) fail(path,'anyOf只支持二分nullable');
      const nulls=s.anyOf.filter(v=>v && v.type==='null' && Object.keys(v).every(k=>k==='type'||annotations.has(k)));
      if(nulls.length!==1) fail(path,'nullable必须恰有一个null分支');
      const other=s.anyOf.find(v=>v!==nulls[0]);
      if(!other || own(other,'anyOf') || (Array.isArray(other.type)&&other.type.includes('null'))) fail(path,'nullable分支重复');
      // Null contributes no value fields, but its schema and definitions still
      // have to satisfy the same finite profile as the non-null branch.
      compile(nulls[0],path+'/anyOf/'+s.anyOf.indexOf(nulls[0]),stack);
      return {...compile(other,path+'/anyOf/'+s.anyOf.indexOf(other),stack),nullable:true};
    }
    if (own(s,'oneOf')) {
      only(['oneOf']);
      if (!Array.isArray(s.oneOf)||s.oneOf.length<2||s.oneOf.length>64) fail(path,'判别联合分支数量无效');
      const branches=s.oneOf.map((v,i)=>compile(v,path+'/oneOf/'+i,stack));
      const tags=new Set();
      for(const b of branches) {
        if(b.type!=='object'||b.nullable||b.boundary||!b.required.includes('kind')||b.properties.kind?.type!=='string'||b.properties.kind.nullable||typeof b.properties.kind.const!=='string') fail(path,'联合分支必须是闭对象并要求kind常量');
        if(tags.has(b.properties.kind.const)) fail(path,'联合分支kind重叠');
        tags.add(b.properties.kind.const);
      }
      return {type:'union',branches};
    }
    let type=s.type, nullable=false;
    if(Array.isArray(type)) {
      if(type.length!==2||!type.includes('null')||new Set(type).size!==2) fail(path,'只支持type与null的二元联合');
      type=type.find(v=>v!=='null'); nullable=true;
    }
    const node={type, ...(nullable?{nullable:true}:{})};
    switch(type) {
      case 'object': {
        only(['type','properties','required','additionalProperties','maxProperties']);
        if(boundary.has(path)) {
          const kind=boundary.get(path);
          if(!['registeredInput','registeredOutput','schemaDocument'].includes(kind)) fail(path,'未知边界槽');
          if(s.additionalProperties!==true||s.properties||s.required) fail(path,'边界槽必须是明确有界的原始object');
          node.boundary=kind;node.maxProperties=integer(s.maxProperties,path+'/maxProperties',1);used.add(path);
        } else {
          if(s.additionalProperties!==false||!s.properties||typeof s.properties!=='object'||Array.isArray(s.properties)) fail(path,'领域object必须闭合并声明properties');
          if(!Array.isArray(s.required)||new Set(s.required).size!==s.required.length||s.required.some(k=>typeof k!=='string'||!own(s.properties,k))) fail(path,'required字段无效');
          node.properties=Object.fromEntries(Object.entries(s.properties).map(([k,v])=>[k,compile(v,path+'/properties/'+escape(k),stack)]));
          node.required=[...s.required];
          node.maxProperties=own(s,'maxProperties')?integer(s.maxProperties,path+'/maxProperties'):Object.keys(s.properties).length;
          if(node.maxProperties<s.required.length) fail(path,'maxProperties不能小于required字段数');
        }
        break;
      }
      case 'array':
        only(['type','items','minItems','maxItems']);
        node.minItems=own(s,'minItems')?integer(s.minItems,path+'/minItems'):0;
        node.maxItems=integer(s.maxItems,path+'/maxItems');
        if(node.maxItems<node.minItems) fail(path,'数组范围颠倒');
        node.items=compile(s.items,path+'/items',stack);break;
      case 'string':
        only(['type','minLength','maxLength','enum','const','pattern']);
        node.minLength=own(s,'minLength')?integer(s.minLength,path+'/minLength'):0;
        node.maxLength=integer(s.maxLength,path+'/maxLength');
        if(node.maxLength<node.minLength) fail(path,'字符串范围颠倒');
        if(own(s,'pattern')) {
          const rule=stringPatterns.get(s.pattern);
          if(!rule||node.minLength!==(rule==='hex32Lower'?32:1)||node.maxLength!==32)
            fail(path,'不支持关键词 pattern 的值或边界');
          node.stringRule=rule;
        }
        if(own(s,'enum')) {
          if(!Array.isArray(s.enum)||s.enum.length<1||new Set(s.enum).size!==s.enum.length||s.enum.some(v=>typeof v!=='string')) fail(path,'enum只接受唯一字符串');
          node.enum=[...s.enum];
        }
        if(own(s,'const')) {if(typeof s.const!=='string') fail(path,'const只接受字符串');node.const=s.const;}
        for(const v of [...(node.enum??[]),...(own(node,'const')?[node.const]:[])]) {
          if([...v].length<node.minLength||[...v].length>node.maxLength||!validUnicode(v)) fail(path,'字符串常量不符合范围');
          if(node.stringRule&&!validStringRule(v,node.stringRule))fail(path,'字符串常量不符合有限pattern');
        }
        if(node.enum && own(node,'const')&&!node.enum.includes(node.const))fail(path,'const与enum无交集');
        // enum/const约束所有类型；type包含null不能绕过字符串枚举/常量的交集。
        if(node.nullable && (node.enum || own(node,'const'))) delete node.nullable;
        if(decimal.has(path)) {
          const d=decimal.get(path);
          if(typeof d.signed!=='boolean') fail(path,'decimal必须声明signed');
          node.decimal={signed:d.signed,scale:integer(d.scale,path+'/scale'),maxDigits:integer(d.maxDigits,path+'/maxDigits',1)};
          if(node.decimal.scale>node.decimal.maxDigits)fail(path,'decimal scale过大');usedDecimals.add(path);
        }
        break;
      case 'number': case 'integer':
        only(['type','minimum','maximum']);
        if(typeof s.minimum!=='number'||!Number.isFinite(s.minimum)||typeof s.maximum!=='number'||!Number.isFinite(s.maximum)||s.minimum>s.maximum)fail(path,'数字必须有有限有序上下界');
        if(type==='integer'&&(!Number.isSafeInteger(s.minimum)||!Number.isSafeInteger(s.maximum)||s.minimum< -safe||s.maximum>safe))fail(path,'integer超安全范围');
        node.minimum=s.minimum;node.maximum=s.maximum;break;
      case 'boolean': case 'null':only(['type']);break;
      default:fail(path,'未知或缺少type');
    }
    return node;
  };
  const model=compile(document,'');
  // 每一层$defs均在compile中校验，即使当前没有引用也不能隐藏非法定义。
  for(const p of boundary.keys()) if(!used.has(p))fail(p,'边界声明未命中schema');
  for(const p of decimal.keys()) if(!usedDecimals.has(p))fail(p,'decimal声明未命中string');
  return model;
}
export function validUnicode(s) {
  for(let i=0;i<s.length;i++) {
    const c=s.charCodeAt(i);
    if(c>=0xd800&&c<=0xdbff) {const d=s.charCodeAt(++i);if(!(d>=0xdc00&&d<=0xdfff))return false;}
    else if(c>=0xdc00&&c<=0xdfff)return false;
  }
  return true;
}
