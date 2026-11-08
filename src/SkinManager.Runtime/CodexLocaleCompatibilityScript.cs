using System.Text.Json;

namespace SkinManager.Runtime;

internal static class CodexLocaleCompatibilityScript
{
    private const string StateProperty = "__skinManagerLocaleCompatibility";
    private const string LayerId = "72216192";

    public static string BuildStateScript(string generation, bool enabled)
    {
        var stateProperty = JsonSerializer.Serialize(StateProperty);
        var generationValue = JsonSerializer.Serialize(generation);
        var enabledValue = enabled ? "true" : "false";
        var layerId = JsonSerializer.Serialize(LayerId);

        var script = """
            /*SM:locale-hook*/
            (async()=>{
              if(window.top!==window||location.protocol!=='app:'||(location.hostname!=='codex'&&location.hostname!=='-'))
                return {state:'unsupported'};
              const stateKey=@@STATE_PROPERTY@@, generation=@@GENERATION@@, enabled=@@ENABLED@@, layerId=@@LAYER_ID@@, globalKey='__STATSIG__';
              let state=window[stateKey];
              if(!state||typeof state!=='object'){
                state={generation,enabled:false,patches:[],globalHook:null,timerIds:[]};
                window[stateKey]=state;
              }
              state.generation=generation;
              state.enabled=enabled;
              const restorePatch=patch=>{
                try{
                  if(patch.target[patch.key]!==patch.wrapper)return;
                  if(patch.hadOwn)Object.defineProperty(patch.target,patch.key,patch.descriptor);
                  else delete patch.target[patch.key];
                }catch{}
              };
              const restoreHooks=()=>{
                for(const timer of state.timerIds||[])clearTimeout(timer);
                state.timerIds=[];
                for(const patch of (state.patches||[]).slice().reverse())restorePatch(patch);
                state.patches=[];
                const hook=state.globalHook;
                if(hook){
                  try{
                    const current=hook.getValue();
                    if(Object.getOwnPropertyDescriptor(window,globalKey)?.get===hook.getter){
                      if(hook.descriptor){
                        const descriptor={...hook.descriptor};
                        if(Object.prototype.hasOwnProperty.call(descriptor,'value'))descriptor.value=current;
                        Object.defineProperty(window,globalKey,descriptor);
                        if(!Object.prototype.hasOwnProperty.call(descriptor,'value')&&typeof descriptor.set==='function')descriptor.set.call(window,current);
                      }else{
                        delete window[globalKey];
                        window[globalKey]=current;
                      }
                    }
                  }catch{}
                  state.globalHook=null;
                }
              };
              const installPatch=(target,key,wrapper)=>{
                const descriptor=Object.getOwnPropertyDescriptor(target,key);
                const patch={target,key,wrapper,hadOwn:!!descriptor,descriptor};
                state.patches.push(patch);
                try{
                  target[key]=wrapper;
                  if(target[key]===wrapper)return true;
                }catch{}
                state.patches.pop();
                return false;
              };
              const patchLayer=layer=>{
                if(!state.enabled||!layer||typeof layer.get!=='function'||layer.get.__skinManagerLocaleWrapped)return;
                const original=layer.get;
                const wrapped=function(key,...args){
                  if(state.enabled&&key==='enable_i18n')return true;
                  return Reflect.apply(original,this,[key,...args]);
                };
                try{Object.defineProperty(wrapped,'__skinManagerLocaleWrapped',{value:true});installPatch(layer,'get',wrapped)}catch{}
              };
              const patchClient=client=>{
                if(!state.enabled||!client||typeof client.getLayer!=='function')return;
                const current=client.getLayer;
                if(!current.__skinManagerLocaleWrapped){
                  const wrapped=function(name,...args){
                    const layer=Reflect.apply(current,this,[name,...args]);
                    if(name===layerId)patchLayer(layer);
                    return layer;
                  };
                  try{Object.defineProperty(wrapped,'__skinManagerLocaleWrapped',{value:true});installPatch(client,'getLayer',wrapped)}catch{}
                }
                try{patchLayer(client.getLayer(layerId))}catch{}
              };
              const patchStatsig=statsig=>{
                if(!state.enabled||!statsig||typeof statsig!=='object')return;
                patchClient(statsig);
                if(statsig.firstInstance&&typeof statsig.firstInstance==='object')patchClient(statsig.firstInstance);
                if(typeof statsig.instance==='function'){try{const first=statsig.instance();if(first&&typeof first==='object')patchClient(first)}catch{}}
                const instances=statsig.instances;
                if(instances instanceof Map){for(const client of instances.values())patchClient(client)}
                else if(instances&&typeof instances==='object'){for(const client of Object.values(instances))patchClient(client)}
              };
              if(!enabled){restoreHooks();return {state:'disabled'};}
              if(!window.electronBridge)return {state:'pending'};
              if(!state.globalHook){
                try{
                  const descriptor=Object.getOwnPropertyDescriptor(window,globalKey);
                  if(!descriptor||descriptor.configurable){
                    let current;
                    try{current=window[globalKey]}catch{}
                    const getter=function(){return current};
                    const setter=function(value){current=value;patchStatsig(value)};
                    Object.defineProperty(window,globalKey,{configurable:true,enumerable:descriptor?.enumerable??true,get:getter,set:setter});
                    state.globalHook={descriptor,getter,getValue:()=>current};
                    patchStatsig(current);
                  }
                }catch{}
              }
              let attempts=0;
              while(state.enabled&&state.generation===generation&&(state.patches||[]).length===0&&attempts++<20){
                patchStatsig(window[globalKey]);
                if((state.patches||[]).length>0)break;
                await new Promise(resolve=>setTimeout(resolve,100));
              }
              return {state:'hooked',hookInstalled:!!state.globalHook||(state.patches||[]).length>0};
            })()
            """;
        return script
            .Replace("@@STATE_PROPERTY@@", stateProperty, StringComparison.Ordinal)
            .Replace("@@GENERATION@@", generationValue, StringComparison.Ordinal)
            .Replace("@@ENABLED@@", enabledValue, StringComparison.Ordinal)
            .Replace("@@LAYER_ID@@", layerId, StringComparison.Ordinal);
    }

    public static string BuildReadLocaleOverrideScript(string generation) => BuildHostScript(
        "read", generation, originalValue: null, expectedEnabled: true);

    public static string BuildWriteSimplifiedChineseScript(string generation) => BuildHostScript(
        "write", generation, originalValue: null, expectedEnabled: true);

    public static string BuildRestoreLocaleOverrideScript(string generation, string? originalValue) => BuildHostScript(
        "restore", generation, originalValue, expectedEnabled: false);

    private static string BuildHostScript(string operation, string generation, string? originalValue, bool expectedEnabled)
    {
        var operationValue = JsonSerializer.Serialize(operation);
        var generationValue = JsonSerializer.Serialize(generation);
        var originalValueJson = JsonSerializer.Serialize(originalValue);
        var stateProperty = JsonSerializer.Serialize(StateProperty);
        var lastWrittenValue = JsonSerializer.Serialize("zh-CN");
        var expectedEnabledValue = expectedEnabled ? "true" : "false";
        var marker = operation switch
        {
            "read" => "locale-read",
            "write" => "locale-write",
            _ => "locale-restore"
        };

        var script = """
            /*SM:@@MARKER@@*/
            (async()=>{
              if(window.top!==window||location.protocol!=='app:'||(location.hostname!=='codex'&&location.hostname!=='-'))
                return {state:'unsupported'};
              if(!window.electronBridge)return {state:'pending'};
              const operation=@@OPERATION@@, generation=@@GENERATION@@, stateKey=@@STATE_PROPERTY@@, expectedEnabled=@@EXPECTED_ENABLED@@;
              const state=window[stateKey];
              const current=()=>state&&window[stateKey]===state&&state.generation===generation&&state.enabled===expectedEnabled;
              if(!current())return {state:'cancelled'};
              const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
              const readScript=async(isCurrent=current)=>{
                const started=Date.now();
                let sawRuntime=false,sawScript=false;
                while(Date.now()-started<1600){
                  if(!isCurrent())return {state:'cancelled'};
                  const sources=Array.from(document.scripts).filter(script=>script.type==='module'&&script.src.startsWith('app://-/assets/')).map(script=>script.src);
                  sawScript=sawScript||sources.length>0;
                  let runtime=null;
                  for(const source of sources){
                    if(!isCurrent())return {state:'cancelled'};
                    try{const module=await import(source);if(typeof module.__webpack_require__==='function'){runtime=module.__webpack_require__;break;}}catch{}
                  }
                  if(runtime){
                    sawRuntime=true;
                    let shared=null;
                    try{
                      const cache=runtime.c||runtime.cache;
                      if(cache){for(const entry of Object.values(cache)){const candidate=entry?.exports?.appServices;if(candidate?.settings&&typeof candidate.settings.read==='function'&&typeof candidate.settings.write==='function'){shared=entry.exports;break;}}}
                    }catch{}
                    if(!shared){try{shared=runtime('GaA7')}catch{}}
                    const settings=shared?.appServices?.settings;
                    if(settings&&typeof settings.read==='function'&&typeof settings.write==='function'){
                      let task;
                      try{task=settings.read('localeOverride')}catch{return {state:'failed'}}
                      try{
                        const value=await runTask(runtime,task,isCurrent);
                        if(!value||typeof value!=='object'||!Object.prototype.hasOwnProperty.call(value,'effective'))return {state:'unsupported'};
                        const locale=value.effective;
                        if(locale!==null&&typeof locale!=='string')return {state:'unsupported'};
                        return {state:'ready',value:locale};
                      }catch(error){return isCurrent()?(error?.name==='TimeoutError'?{state:'pending'}:{state:'failed'}):{state:'cancelled'}}
                    }
                  }
                  await wait(80);
                }
                return {state:sawRuntime||!sawScript?'pending':'unsupported'};
              };
              const readCurrent=()=>readScript(current);
              const first=await readCurrent();
              if(first.state!=='ready')return first;
              if(operation==='read')return first;
              const settingsResult=await findSettings();
              if(settingsResult.state!=='ready')return settingsResult;
              const settings=settingsResult.settings,runtime=settingsResult.runtime;
              if(operation==='restore'&&first.value!==@@LAST_WRITTEN@@)
                return {state:'preserved',value:first.value};
              if(!current())return {state:'cancelled'};
              const value=operation==='write'?@@LAST_WRITTEN@@:@@ORIGINAL_VALUE@@;
              let task;
              try{task=settings.write('localeOverride',value)}catch{return {state:'failed'}}
              try{await runTask(runtime,task,current)}catch(error){return current()?(error?.name==='TimeoutError'?{state:'pending'}:{state:'failed'}):{state:'cancelled'}}
              if(!current())return {state:'cancelled'};
              const verified=await readCurrent();
              if(verified.state!=='ready')return verified;
              if(verified.value!==value)return {state:'failed'};
              return {state:operation==='write'?'applied':'restored',value:verified.value};

              async function findSettings(){
                const sources=Array.from(document.scripts).filter(script=>script.type==='module'&&script.src.startsWith('app://-/assets/')).map(script=>script.src);
                for(const source of sources){if(!current())return {state:'cancelled'};try{const module=await import(source);if(typeof module.__webpack_require__==='function'){
                  const runtime=module.__webpack_require__;
                  let shared=null;
                  try{const cache=runtime.c||runtime.cache;if(cache){for(const entry of Object.values(cache)){const candidate=entry?.exports?.appServices;if(candidate?.settings&&typeof candidate.settings.read==='function'&&typeof candidate.settings.write==='function'){shared=entry.exports;break;}}}}catch{}
                  if(!shared){try{shared=runtime('GaA7')}catch{}}
                  const settings=shared?.appServices?.settings;
                  if(settings&&typeof settings.read==='function'&&typeof settings.write==='function')return {state:'ready',settings,runtime};
                }}catch{}}
                return {state:'pending'};
              }
              async function runTask(runtime,task,isCurrent,timeoutMs=1500){
                if(!isCurrent())throw Object.assign(new Error('Cancelled'),{name:'AbortError'});
                const controller=new AbortController();
                const timer=setTimeout(()=>controller.abort(),timeoutMs);
                let rejectAbort;
                const aborted=new Promise((_,reject)=>{rejectAbort=()=>reject(Object.assign(new Error('Timed out'),{name:'TimeoutError'}));controller.signal.addEventListener('abort',rejectAbort,{once:true})});
                try{
                  let utility;
                  try{utility=runtime('q77y')}catch{}
                  let managed;
                  if(typeof utility?.P==='function')managed=utility.P(task,controller.signal);
                  else managed=(async()=>{const dispose=()=>{try{task?.[Symbol.dispose]?.()}catch{}};const onAbort=()=>dispose();controller.signal.addEventListener('abort',onAbort,{once:true});try{controller.signal.throwIfAborted();return await task}finally{controller.signal.removeEventListener('abort',onAbort);dispose()}})();
                  return await Promise.race([managed,aborted]);
                }catch(error){
                  if(controller.signal.aborted)throw Object.assign(new Error('Timed out'),{name:'TimeoutError'});
                  throw error;
                }finally{controller.signal.removeEventListener('abort',rejectAbort);clearTimeout(timer)}
              }
            })()
            """;
        return script
            .Replace("@@MARKER@@", marker, StringComparison.Ordinal)
            .Replace("@@OPERATION@@", operationValue, StringComparison.Ordinal)
            .Replace("@@GENERATION@@", generationValue, StringComparison.Ordinal)
            .Replace("@@STATE_PROPERTY@@", stateProperty, StringComparison.Ordinal)
            .Replace("@@EXPECTED_ENABLED@@", expectedEnabledValue, StringComparison.Ordinal)
            .Replace("@@LAST_WRITTEN@@", lastWrittenValue, StringComparison.Ordinal)
            .Replace("@@ORIGINAL_VALUE@@", originalValueJson, StringComparison.Ordinal);
    }
}
