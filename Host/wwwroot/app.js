'use strict';
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
const names = {monitor:'モニター',scenes:'シーン',audio:'音声ミキサー',sources:'ソース',connections:'送信PC',output:'配信・録画',settings:'接続設定'};
let connected = false, config, state, sceneKey = '', inputKey = '', sourceKey = '', preview = '', program = '', streamActive = false, recordActive = false, busyState = false, busyImages = false;
let toastTimer, imageTimer, pollTimer, ancillary = 0, connectionRevision = 0, connectingObs = false;
const imageInFlight = new WeakSet();
const panelRoot = $('#panels');
const layoutKey = 'ocs-link-layout-v1';
function notify(message, error = false) { const t = $('#toast'); t.textContent = message; t.hidden = false; t.classList.toggle('error',error); clearTimeout(toastTimer); toastTimer=setTimeout(()=>t.hidden=true,error?7000:3000); }
async function api(path, data) {
  const r = await fetch('/api/'+path,{method:data===undefined?'GET':'POST',headers:data===undefined?{}:{'Content-Type':'application/json','X-OCS-Control':'1'},body:data===undefined?undefined:JSON.stringify(data)});
  if (r.status===401) { showLogin(); throw new Error('ログインしてください'); }
  let body={}; try { body=await r.json(); } catch {}
  if(!r.ok) throw new Error(body.error||`接続エラー ${r.status}`); return body;
}
function action(action,data={}) { return api('obs/action',{action,data}); }
function guarded(fn) { return async(...args)=>{try{return await fn(...args);}catch(e){notify(e.message,true);}}; }
function showLogin(){ $('#login').hidden=false; $('#app').hidden=true; connected=false; clearInterval(pollTimer);clearInterval(imageTimer); }
function saveLayout(){ localStorage.setItem(layoutKey,JSON.stringify($$('.panel').map(p=>({id:p.dataset.panel,hidden:p.hidden,wide:p.classList.contains('wide')})))); renderNav(); }
function renderNav(){ const nav=$('#modules');nav.replaceChildren(); for(const [id,name] of Object.entries(names)){const b=document.createElement('button');b.classList.toggle('active',!$(`[data-panel="${id}"]`).hidden);const i=document.createElement('i');b.append(i,document.createTextNode(name));b.onclick=()=>{const p=$(`[data-panel="${id}"]`);p.hidden=!p.hidden;saveLayout();if(!p.hidden)p.scrollIntoView({behavior:'smooth',block:'nearest'});};nav.append(b);} }
function loadLayout(){try{const saved=JSON.parse(localStorage.getItem(layoutKey)||'null');if(Array.isArray(saved))for(const s of saved){const p=$(`[data-panel="${s.id}"]`);if(p){p.hidden=!!s.hidden;p.classList.toggle('wide',!!s.wide);panelRoot.append(p);}}else{$('[data-panel="settings"]').hidden=true;$('[data-panel="output"]').hidden=true;}}catch{}renderNav();}
let drag;
for(const panel of $$('.panel')){
 panel.querySelector('[data-hide]').onclick=()=>{panel.hidden=true;saveLayout();};
 panel.querySelector('[data-size]')?.addEventListener('click',()=>{panel.classList.toggle('wide');saveLayout();});
 for(const button of panel.querySelectorAll('[data-move]'))button.onclick=()=>{const panels=[...panelRoot.children],index=panels.indexOf(panel);if(button.dataset.move==='up'&&index>0)panelRoot.insertBefore(panel,panels[index-1]);else if(button.dataset.move==='down'&&index<panels.length-1)panelRoot.insertBefore(panels[index+1],panel);saveLayout();};
 panel.querySelector('.panel-head').ondragstart=e=>{drag=panel;e.dataTransfer.setData('text/plain',panel.dataset.panel);};
 panel.ondragover=e=>{if(drag&&drag!==panel){e.preventDefault();panel.classList.add('drag-over');}};
 panel.ondragleave=()=>panel.classList.remove('drag-over');
 panel.ondrop=e=>{e.preventDefault();panel.classList.remove('drag-over');if(drag&&drag!==panel){panelRoot.insertBefore(drag,panel);saveLayout();}drag=null;};
 panel.ondragend=()=>{drag=null;$$('.drag-over').forEach(p=>p.classList.remove('drag-over'));};
}
$('#reset-layout').onclick=()=>{localStorage.removeItem(layoutKey);location.reload();};
$('#login-form').onsubmit=async e=>{e.preventDefault();try{await api('login',{password:$('#password').value});$('#password').value='';$('#login-error').textContent='';await boot();}catch(err){$('#login-error').textContent=err.message;}};
$('#logout').onclick=guarded(async()=>{await api('logout',{});showLogin();});
async function boot(){config=await api('config');$('#login').hidden=true;$('#app').hidden=false;loadLayout();fillSettings();renderSlots([]);await refresh();clearInterval(pollTimer);pollTimer=setInterval(refresh,2000);setImageRate();}
function fillSettings(){ $('#public-url').value=config.publicUrl;$('#obs-url').value=config.obsUrl;$('#ice-servers').value=JSON.stringify(config.iceServers,null,2); }
$('#settings-form').onsubmit=guarded(async e=>{e.preventDefault();const iceServers=JSON.parse($('#ice-servers').value);if(!Array.isArray(iceServers))throw new Error('ICEサーバーはJSON配列で入力してください');await api('config',{publicUrl:$('#public-url').value,obsUrl:$('#obs-url').value,obsPassword:$('#obs-password').value,iceServers});config=await api('config');$('#obs-password').value='';renderSlots(state?.slots||[]);notify('設定を保存しました。送受信を再接続してください');});
function showConnection(ok,error='',endpoint='') {
 connected=ok;$('#obs-status').textContent=ok?'OBS 接続済み':'OBS 未接続';$('#obs-status').classList.toggle('online',ok);$('#connect').textContent=ok?'再接続':'OBS接続';
 const detail=$('#obs-error');detail.textContent=error+(error&&endpoint?' / 接続先: '+endpoint:'');detail.hidden=!error;
}
$('#connect').onclick=guarded(async()=>{
 const b=$('#connect');b.disabled=true;connectingObs=true;connectionRevision++;
 $('#obs-status').textContent='OBS 接続確認中';$('#obs-status').classList.remove('online');$('#obs-error').hidden=true;
 try {
  const result=await api('obs/connect',{url:$('#obs-url').value,password:$('#obs-password').value});
  if(result.connected!==true)throw new Error(result.error||'OBS接続を確認できませんでした');
  const check=await api('state');
  if(check.connected!==true)throw new Error(check.error||'確認中にOBS接続が切れました');
  connectionRevision++;showConnection(true,check.error||'',check.endpoint||'');$('#obs-password').value='';
  inputKey='';sceneKey='';sourceKey='';connectingObs=false;await refresh();
  if(!connected)throw new Error(state?.error||'OBSとの接続が切れました');
  notify('OBSのシーン一覧を取得して接続を確認しました');
 } catch(e) {connectionRevision++;showConnection(false,e.message,$('#obs-url').value);throw e;}
 finally {connectingObs=false;b.disabled=false;}
});
$('#refresh').onclick=guarded(async()=>{sceneKey=inputKey=sourceKey='';await refresh();await images();});
$('#refresh-receivers').onclick=guarded(async()=>{const b=$('#refresh-receivers');b.disabled=true;try{const result=await api('obs/refresh-receivers',{});notify(result.refreshed?result.refreshed+'枠の受信ページを更新しました':'更新対象がありません。先に受信用シーンを追加してください');}finally{b.disabled=false;}});
$('#setup').onclick=guarded(async()=>{const b=$('#setup');b.disabled=true;try{await api('obs/setup',{});await refresh();notify('受信用シーンを追加しました');}finally{b.disabled=false;}});
function renderSlots(slots){const root=$('#slot-list');root.replaceChildren();for(const s of config.slots){const live=slots.find(x=>x.id===s.id);const row=document.createElement('div');row.className='row';const main=document.createElement('div');main.className='row-main';const title=document.createElement('strong');const dot=document.createElement('i');dot.className='dot'+(live?.online?' online':'');title.append(dot,document.createTextNode(s.name));const small=document.createElement('small');small.textContent=live?.online?`接続済み / 受信 ${live.viewers}`:'オフライン';main.append(title,small);const b=document.createElement('button');b.textContent='接続コード';b.onclick=()=>{$('#code-title').textContent=s.name+' / 接続コード';$('#send-code').value=s.code;$('#code-dialog').showModal();};row.append(main,b);root.append(row);}}
$('#close-dialog').onclick=()=>$('#code-dialog').close();
$('#copy-code').onclick=guarded(async()=>{try{await navigator.clipboard.writeText($('#send-code').value);notify('コピーしました');}catch{$('#send-code').select();document.execCommand('copy');notify('コードを選択しました。コピーできない場合は手動でコピーしてください');}});
async function refresh(){if(busyState||connectingObs||$('#app').hidden)return;busyState=true;const revision=connectionRevision;try{
 const result=await api('state');if(revision!==connectionRevision||connectingObs)return;state=result;showConnection(state.connected,state.error||'',state.endpoint||'');
 for(const id of ['take','cut','setup','refresh-receivers','stream-toggle','record-toggle','apply-transition'])$('#'+id).disabled=!connected;
 renderSlots(state.slots);
 if(!connected){$$('.ready').forEach(i=>i.classList.remove('ready'));return;}
 const sceneData=state.scenes||{};program=sceneData.currentProgramSceneName||'';preview=sceneData.currentPreviewSceneName||preview;
 const scenes=sceneData.scenes||[];const key=JSON.stringify(scenes.map(s=>s.sceneName));if(key!==sceneKey){sceneKey=key;renderScenes(scenes);}
 $('#preview-name').textContent=preview||'未選択';$('#program-name').textContent=program||'未選択';
 for(const [img,name] of [[$('#preview-image'),preview],[$('#program-image'),program]]){if(img.dataset.desiredScene!==name){img.classList.remove('ready');img.dataset.desiredScene=name;}}
 $$('.scene-card').forEach(c=>{c.classList.toggle('program',c.dataset.scene===program);c.classList.toggle('preview',c.dataset.scene===preview);c.querySelector('em').textContent=c.dataset.scene===program?(c.dataset.scene===preview?'PGM / PVW':'PROGRAM'):c.dataset.scene===preview?'PREVIEW':'';});
 const inputs=state.inputs?.inputs||[];const ik=JSON.stringify(inputs.map(i=>i.inputName));if(ik!==inputKey){inputKey=ik;await renderAudio(inputs);}
 if(preview!==sourceKey){sourceKey=preview;await renderSources();}
 if(ancillary++%3===0)await outputStatus();
 }catch(e){notify(e.message,true);}finally{busyState=false;}}
function renderScenes(scenes){const root=$('#scene-grid');root.replaceChildren();$('#scene-count').textContent=scenes.length;for(const [i,s]of scenes.entries()){const b=document.createElement('button');b.className='scene-card';b.dataset.scene=s.sceneName;b.setAttribute('aria-label',s.sceneName+'をプレビュー');const thumb=document.createElement('div');thumb.className='scene-thumb';const img=document.createElement('img');img.alt=s.sceneName;img.dataset.scene=s.sceneName;const number=document.createElement('span');number.textContent=String(i+1).padStart(2,'0');thumb.append(img,number);const line=document.createElement('div');line.className='scene-name';const name=document.createElement('strong');name.textContent=s.sceneName;line.append(name,document.createElement('em'));b.append(thumb,line);b.onclick=guarded(()=>selectScene(s.sceneName));root.append(b);}let prime=0;const thumbs=[...root.querySelectorAll('img')];Promise.all([0,1].map(async()=>{while(prime<thumbs.length){const img=thumbs[prime++];await image(img,img.dataset.scene);}}));if(!scenes.length){const p=document.createElement('p');p.className='placeholder';p.textContent='シーンがありません';root.append(p);}}
async function selectScene(name){await action('SetCurrentPreviewScene',{sceneName:name});preview=name;sourceKey='';await refresh();await images();}
$('#take').onclick=guarded(async()=>{await action('TriggerStudioModeTransition');await refresh();});
$('#cut').onclick=guarded(async()=>{if(preview)await action('SetCurrentProgramScene',{sceneName:preview});await refresh();});
document.addEventListener('keydown',e=>{if(!connected||/INPUT|TEXTAREA|SELECT/.test(e.target.tagName)||$('#code-dialog').open||e.ctrlKey||e.metaKey||e.altKey||e.repeat)return;if(e.key==='Enter'){e.preventDefault();$('#take').click();}else if(/^[1-9]$/.test(e.key)){const c=$$('.scene-card')[Number(e.key)-1];if(c)c.click();}});
async function image(img,scene,width=320){if(!scene||imageInFlight.has(img))return;imageInFlight.add(img);try{const r=await api('preview?'+new URLSearchParams({scene,width}));if(img.isConnected&&(!img.dataset.desiredScene||img.dataset.desiredScene===scene)){img.src=r.image;img.classList.add('ready');}}catch{img.classList.remove('ready');}finally{imageInFlight.delete(img);}}
async function images(){if(!connected||busyImages||document.hidden)return;busyImages=true;try{const tasks=[];if(!$('[data-panel="monitor"]').hidden){tasks.push(()=>image($('#preview-image'),preview,640));tasks.push(()=>image($('#program-image'),program,640));}if(!$('[data-panel="scenes"]').hidden)for(const img of $$('.scene-thumb img')){const r=img.parentElement.getBoundingClientRect();if(r.bottom>0&&r.top<innerHeight)tasks.push(()=>image(img,img.dataset.scene));}let next=0;await Promise.all([0,1].map(async()=>{while(next<tasks.length)await tasks[next++]();}));}finally{busyImages=false;}}
function setImageRate(){clearInterval(imageTimer);const rate=Number($('#preview-rate').value);localStorage.setItem('ocs-preview-rate',String(rate));imageTimer=setInterval(images,rate);images();}
$('#preview-rate').value=localStorage.getItem('ocs-preview-rate')||'1000';$('#preview-rate').onchange=setImageRate;
async function renderAudio(inputs){const root=$('#audio-list');root.replaceChildren();for(const i of inputs){let volume,mute;try{[volume,mute]=await Promise.all([action('GetInputVolume',{inputName:i.inputName}),action('GetInputMute',{inputName:i.inputName})]);}catch{continue;}const row=document.createElement('div');row.className='audio-row';const top=document.createElement('div');top.className='audio-top';const text=document.createElement('span');text.textContent=i.inputName;const button=document.createElement('button');let muted=mute.inputMuted;function showMute(){button.classList.toggle('muted',muted);button.textContent=muted?'MUTED':'MUTE';}showMute();button.onclick=guarded(async()=>{await action('SetInputMute',{inputName:i.inputName,inputMuted:!muted});muted=!muted;showMute();});top.append(text,button);const bottom=document.createElement('div');bottom.className='audio-bottom';const slider=document.createElement('input');slider.type='range';slider.min='0';slider.max='150';slider.value=String(Math.round(volume.inputVolumeMul*100));slider.setAttribute('aria-label',i.inputName+'の音量');const out=document.createElement('output');out.textContent=slider.value+'%';slider.oninput=()=>out.textContent=slider.value+'%';slider.onchange=guarded(()=>action('SetInputVolume',{inputName:i.inputName,inputVolumeMul:Number(slider.value)/100}));bottom.append(slider,out);row.append(top,bottom);root.append(row);}if(!root.children.length){const p=document.createElement('p');p.className='placeholder';p.textContent='音声入力がありません';root.append(p);}}
async function renderSources(){const root=$('#source-list');root.replaceChildren();$('#source-scene').textContent=preview;if(!preview)return;const data=await action('GetSceneItemList',{sceneName:preview});for(const item of(data.sceneItems||[])){const row=document.createElement('label');row.className='row';const text=document.createElement('span');text.textContent=item.sourceName;const check=document.createElement('input');check.type='checkbox';check.className='source-toggle';check.checked=item.sceneItemEnabled;check.onchange=guarded(()=>action('SetSceneItemEnabled',{sceneName:preview,sceneItemId:item.sceneItemId,sceneItemEnabled:check.checked}));row.append(text,check);root.append(row);}}
async function outputStatus(){if($('[data-panel="output"]').hidden)return;const [s,r,t]=await Promise.all([action('GetStreamStatus'),action('GetRecordStatus'),action('GetSceneTransitionList')]);streamActive=s.outputActive;recordActive=r.outputActive;$('#stream-status').textContent=streamActive?'配信中':'配信 停止中';$('#record-status').textContent=recordActive?'録画中':'録画 停止中';$('#stream-toggle').textContent=streamActive?'配信停止':'配信開始';$('#record-toggle').textContent=recordActive?'録画停止':'録画開始';if(document.activeElement!==$('#transition')){const sel=$('#transition');sel.replaceChildren();for(const x of t.transitions||[]){const o=document.createElement('option');o.value=x.transitionName;o.textContent=x.transitionName;sel.append(o);}sel.value=t.currentSceneTransitionName;}}
$('#stream-toggle').onclick=guarded(async()=>{await action(streamActive?'StopStream':'StartStream');await outputStatus();});
$('#record-toggle').onclick=guarded(async()=>{await action(recordActive?'StopRecord':'StartRecord');await outputStatus();});
$('#apply-transition').onclick=guarded(async()=>{await action('SetCurrentSceneTransition',{transitionName:$('#transition').value});await action('SetCurrentSceneTransitionDuration',{transitionDuration:Number($('#transition-duration').value)});notify('トランジションを設定しました');});
loadLayout();boot().catch(()=>showLogin());
