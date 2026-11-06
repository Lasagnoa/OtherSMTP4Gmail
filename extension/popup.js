'use strict';
const $ = id => document.getElementById(id);
let settings = null, busy = false, editing = null, deleteId = null;
const form = $('account-form');
const fields = ['from','host','security','port','username','password','display_name','bcc'];

function request(message) {
  return new Promise(resolve => chrome.runtime.sendMessage(message, response => {
    if (chrome.runtime.lastError) resolve({ok:false,error:'ローカル連携プログラムと通信できません。'});
    else resolve(response || {ok:false,error:'連携プログラムから応答がありません。'});
  }));
}
function status(text, isError = false) {
  $('status').hidden = !text; $('status').classList.toggle('error',isError); $('status').textContent = text || '';
}
function node(tag, className, text) {
  const el = document.createElement(tag); if (className) el.className = className; if (text != null) el.textContent = text; return el;
}
function button(text, label, cls, action, disabled = false) {
  const el = node('button',cls,text); el.type = 'button'; el.setAttribute('aria-label',label); el.title=label; el.disabled=disabled || busy; el.addEventListener('click',action); return el;
}
function render() {
  const list = $('accounts'); list.replaceChildren();
  $('count').textContent = `${settings.accounts.length} / 10`;
  $('add').disabled = busy || settings.accounts.length >= 10;
  $('save-path').disabled = busy;
  const byId = new Map(settings.accounts.map(a=>[a.id,a]));
  settings.order.forEach((id,index) => {
    const account = byId.get(id), isGmail = id === 'gmail';
    const row = node('div','account-row' + (index === 0 ? ' is-default' : ''));
    if (!isGmail && deleteId !== id) row.append(button('−',`${account.from} を削除`,'icon-button remove',()=>{deleteId=id;render()}));
    if (isGmail) { const spacer=node('span','icon-button'); spacer.textContent='G'; spacer.setAttribute('aria-hidden','true'); row.append(spacer); }
    const detail=node('div','account-detail'), name=node('div','account-name',isGmail?'Gmail（通常）':account.from);
    name.title=isGmail?'Gmailの通常の送信機能':account.from; detail.append(name);
    detail.append(node('div','account-sub'+(index===0?' default':''),index===0?'● デフォルト':isGmail?'Gmailで送信':account.display_name || 'ローカルSMTP')); row.append(detail);
    if (!isGmail && deleteId === id) {
      const confirm=node('div','confirm'); confirm.append(node('span','', '削除しますか？'));
      confirm.append(button('削除','削除を確定','danger',()=>remove(id)));
      confirm.append(button('戻る','削除を取り消す','',()=>{deleteId=null;render()})); row.append(confirm);
    } else if (!isGmail) row.append(button('設定',`${account.from} の設定`,'edit',()=>openEditor(account)));
    const move=node('div','move');
    move.append(button('▲',`${isGmail?'Gmail':account.from} を上へ`,'icon-button',()=>reorder(index,-1),index===0));
    move.append(button('▼',`${isGmail?'Gmail':account.from} を下へ`,'icon-button',()=>reorder(index,1),index===settings.order.length-1));
    row.append(move); list.append(row);
  });
}
function serialAccounts(accounts) { return accounts.map(a => ({id:a.id,from:a.from,host:a.host,port:String(a.port),security:a.security,verify_certificate:!!a.verify_certificate,username:a.username || '',password:a.password || '',display_name:a.display_name || '',bcc:a.bcc || ''})); }
async function persist(accounts,order,programPath = settings.mailsend_path) {
  busy=true; render(); $('save').disabled=true;
  const response=await request({action:'saveSettings',revision:settings.revision,accounts:serialAccounts(accounts),order,mailsend_path:programPath});
  busy=false; $('save').disabled=false;
  if (!response.ok) { render(); return response; }
  settings=response; deleteId=null; render(); status(response.warning || '',!!response.warning); return response;
}
async function reorder(index, delta) {
  if (busy) return;
  const order=[...settings.order], other=index+delta; if(other<0 || other>=order.length)return;
  [order[index],order[other]]=[order[other],order[index]];
  const result=await persist(settings.accounts,order); if(!result.ok)status(result.error,true);
}
async function remove(id) {
  if (busy) return;
  const result=await persist(settings.accounts.filter(a=>a.id!==id),settings.order.filter(item=>item!==id));
  if(!result.ok)status(result.error,true);
}
function showTransportNote() {
  const mode=$('security').value;
  $('verify_certificate').disabled=mode==='none';
  $('transport-note').hidden=mode!=='none';
  $('transport-note').textContent=mode==='none'?'通信が暗号化されず、メールや認証情報がネットワーク上で見える可能性があります。':'';
}
function openEditor(account) {
  if (busy) return;
  const isNew=!account;
  if(isNew){const used=new Set(settings.accounts.map(a=>a.id));let id='';for(let i=1;i<=10;i++)if(!used.has(`smtp${i}`)){id=`smtp${i}`;break}if(!id)return;account={id,from:'',host:'',security:'starttls',port:'587',verify_certificate:true,username:'',display_name:'',bcc:'',hasPassword:false};}
  editing={id:account.id,isNew}; deleteId=null;
  for (const key of fields) $(key).value = key==='password'?'':String(account[key] || '');
  $('verify_certificate').checked=!!account.verify_certificate;
  $('password').placeholder=account.hasPassword?'未入力なら現在のパスワードを維持':'SMTP認証パスワード';
  $('password').type='password'; $('show-password').textContent='表示'; $('show-password').setAttribute('aria-pressed','false');
  $('dialog-title').textContent=isNew?'登録する送信サーバー（SMTP）設定':'送信サーバー（SMTP）設定';
  $('save').textContent=isNew?'登録':'保存'; $('form-error').hidden=true; showTransportNote();
  $('overlay').hidden=false; $('from').focus();
}
function closeEditor() { if(busy)return; $('password').value=''; $('overlay').hidden=true; editing=null; $('add').focus(); }
$('add').addEventListener('click',()=>openEditor(null));
$('save-path').addEventListener('click',async()=>{
  if(busy||!settings)return;
  const response=await persist(settings.accounts,settings.order,$('mailsend-path').value.trim());
  if(!response.ok){status(response.error || '実行ファイルの場所を保存できませんでした。',true);return;}
  $('mailsend-path').value=response.mailsend_path;status(response.warning || '送信プログラムの場所を保存しました。',!!response.warning);
});
$('cancel').addEventListener('click',closeEditor); $('close').addEventListener('click',closeEditor);
$('security').addEventListener('change',()=>{ $('port').value={starttls:'587',ssl:'465',none:'25'}[$('security').value];showTransportNote(); });
$('show-password').addEventListener('click',()=>{const visible=$('password').type==='text';$('password').type=visible?'password':'text';$('show-password').textContent=visible?'表示':'隠す';$('show-password').setAttribute('aria-pressed',String(!visible));});
document.addEventListener('keydown',event=>{if(event.key==='Escape'&&!$('overlay').hidden)closeEditor();});
form.addEventListener('submit',async event=>{
  event.preventDefault(); if(busy||!editing||!form.reportValidity())return;
  const values={id:editing.id}; for(const key of fields)values[key]=key==='password'?$(key).value:$(key).value.trim();
  values.verify_certificate=$('verify_certificate').checked;
  if(!values.username && values.password){$('form-error').textContent='パスワードを指定する場合はユーザー名も入力してください。';$('form-error').hidden=false;return;}
  const accounts=editing.isNew?[...settings.accounts,values]:settings.accounts.map(a=>a.id===editing.id?values:a);
  const order=editing.isNew?[...settings.order,editing.id]:[...settings.order];
  const response=await persist(accounts,order);
  values.password='';
  if(!response.ok){$('form-error').textContent=response.error || '設定を保存できませんでした。';$('form-error').hidden=false;return;}
  closeEditor();
});
(async()=>{const result=await request({action:'getSettings'});if(!result.ok){status(result.error || '設定を読み取れません。',true);$('add').disabled=true;$('save-path').disabled=true;return}settings=result;$('mailsend-path').value=result.mailsend_path;status('');render()})();
