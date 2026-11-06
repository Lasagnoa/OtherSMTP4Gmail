(() => {
  'use strict';
  const sendSelector = 'div[role="button"][data-tooltip*="Enter)"],div[role="button"][aria-label*="Enter)"],div[role="button"][data-tooltip^="Send"],div[role="button"][aria-label^="Send"],div[role="button"][data-tooltip^="送信"],div[role="button"][aria-label^="送信"]';
  const maxBytes = 10 * 1024 * 1024;
  const choices = new Map();
  const rootChoices = new WeakMap();
  let config = null, busy = false, pending = false, settingsDirty = false;

  function notify(message, error = false) {
    const el = document.createElement('div');
    el.className = 'lsmtp-toast' + (error ? ' error' : '');
    el.setAttribute('role', 'status'); el.textContent = message;
    document.body.append(el); setTimeout(() => el.remove(), error ? 12000 : 6000);
  }
  function request(message) {
    return new Promise(resolve => chrome.runtime.sendMessage(message, r => {
      if (chrome.runtime.lastError) resolve({ok:false, uncertain:message.action === 'send', error:'拡張機能との通信が切断されました。送信中の場合は履歴を確認してください。'});
      else resolve(r || {ok:false, error:'応答がありません。'});
    }));
  }
  function roots() {
    const found = new Set();
    for (const button of document.querySelectorAll(sendSelector)) {
      const root = button.closest('div[role="dialog"]') || button.closest('form') || button.closest('td.I5') || button.closest('div.iN');
      if (root && root.querySelector('div[contenteditable="true"][role="textbox"]')) found.add(root);
    }
    const all = [...found]; return all.filter(r => !all.some(o => o !== r && r.contains(o)));
  }
  function nativeButton(root) { return root.querySelector(sendSelector); }
  function bodyBox(root) { return root.querySelector('div[contenteditable="true"][role="textbox"]'); }
  function draftKey(root) { return root.querySelector('input[name="draft"]')?.value || ''; }
  function selected(root) {
    const id = root.querySelector('.lsmtp-select')?.value;
    return config?.accounts.find(account => account.id === id) || null;
  }
  function hide(el) { if (el && !el.hasAttribute('data-lsmtp-old-display')) { el.setAttribute('data-lsmtp-old-display', el.style.display); el.style.display = 'none'; } }
  function restore(el) { if (el && el.hasAttribute('data-lsmtp-old-display')) { el.style.display = el.getAttribute('data-lsmtp-old-display'); el.removeAttribute('data-lsmtp-old-display'); } }
  function schedule(native) {
    const next = native?.nextElementSibling;
    return next?.getAttribute('role') === 'button' ? next : null;
  }
  function decorate(root) {
    const native = nativeButton(root); if (!native || !config) return;
    let row = root.querySelector('.lsmtp-row');
    if (!row) {
      row = document.createElement('div'); row.className = 'lsmtp-row';
      const label = document.createElement('span'); label.textContent = '送信元';
      const select = document.createElement('select'); select.className = 'lsmtp-select'; select.setAttribute('aria-label','ローカルSMTPの送信元');
      const order = Array.isArray(config.order) ? config.order : [...config.accounts.map(a=>a.id),'gmail'];
      for (const id of order) {
        if (id === 'gmail') select.add(new Option('Gmail（通常）','gmail'));
        else { const account = config.accounts.find(a=>a.id===id); if(account) select.add(new Option(account.from + '（ローカルSMTP）',account.id)); }
      }
      const saved = rootChoices.get(root) || choices.get(draftKey(root));
      if (saved && [...select.options].some(option => option.value === saved)) select.value = saved;
      select.addEventListener('change', () => { rootChoices.set(root, select.value); if (draftKey(root)) choices.set(draftKey(root), select.value); apply(root); });
      row.append(label, select); root.prepend(row);
    }
    apply(root);
  }
  function apply(root) {
    const native = nativeButton(root); if (!native) return;
    const arrow = schedule(native); let local = root.querySelector('.lsmtp-button');
    const select = root.querySelector('.lsmtp-select'); if (select) select.disabled = busy;
    const account = selected(root);
    if (!account) { restore(native); restore(arrow); local?.remove(); return; }
    if (!local) {
      local = document.createElement('button'); local.type = 'button'; local.className = 'lsmtp-button';
      local.addEventListener('click', e => { e.preventDefault(); send(root); });
      native.parentElement.insertBefore(local, native);
    }
    const label = busy ? '送信処理中…' : account.from + ' から送信';
    if (local.textContent !== label) local.textContent = label;
    local.disabled = busy;
    hide(native); hide(arrow);
  }
  function shown(el) { return !!el && el.getClientRects().length > 0; }
  function recipientFields(root) {
    const fields = {};
    const entries = Array.from(root.querySelectorAll('input[peoplekit-id]'));
    if (!entries.length) {
      for (const name of ['to','cc','bcc']) {
        const label = {to:'To',cc:'Cc',bcc:'Bcc'}[name];
        const field = root.querySelector(`input[name="${name}"],textarea[name="${name}"],[aria-label="${label} recipients"]`);
        if (field) fields[name] = field;
      }
    } else {
      if (entries.length > 3) throw Error('宛先欄を判別できません。');
      fields.to = entries[0];
      if (entries.length === 3) { fields.cc = entries[1]; fields.bcc = entries[2]; }
      if (entries.length === 2) {
        const description = (entries[1].getAttribute('aria-label') || '').toLowerCase();
        let named = null;
        if (/\b(?:bcc|cco|cci)\b/.test(description)) named = 'bcc';
        else if (/\bcc\b/.test(description)) named = 'cc';
        const linkFor = key => root.querySelector(`[data-tooltip*="Shift-${key})"],[aria-label*="Shift-${key})"]`);
        const c = linkFor('C'), b = linkFor('B');
        let visible = null;
        if (c && b && shown(c) !== shown(b)) visible = shown(c) ? 'bcc' : 'cc';
        if ((named && visible && named !== visible) || (!named && !visible)) throw Error('CcとBccを区別できないため送信しません。');
        fields[named || visible] = entries[1];
      }
    }
    if (!fields.to) throw Error('宛先欄を読み取れません。');
    return fields;
  }
  function recipients(root, fields, kind) {
    const f = fields[kind]; if (!f) return [];
    const peers = Object.values(fields).filter(x => x !== f);
    let area = f;
    for (let parent = f.parentElement; parent && parent !== root; parent = parent.parentElement) {
      if (peers.some(peer => parent.contains(peer))) break;
      area = parent;
    }
    const raw = Array.from(area.querySelectorAll('[data-hovercard-id]'), e => e.getAttribute('data-hovercard-id'));
    for (const e of root.querySelectorAll(`input[name="${kind}"],textarea[name="${kind}"]`)) raw.push(...e.value.split(/[,;\s]+/));
    if (f.value) raw.push(...f.value.split(/[,;\s]+/));
    const result = [];
    for (let item of raw) {
      item = (item || '').trim(); if (!item) continue;
      const match = item.match(/<([^<>]+)>/); if (match) item = match[1];
      if (!/^[A-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Z0-9.-]+\.[A-Z]{2,}$/i.test(item)) throw Error('宛先を確認してください: ' + item);
      if (!result.some(x => x.toLowerCase() === item.toLowerCase())) result.push(item);
    }
    return result;
  }
  function scopeFor(root) {
    const main = root.closest('[role="main"]'); if (main) return main;
    const rt = root.querySelector('input[name="rt"]')?.value || '';
    const id = /thread-f:(\d+)/.exec(rt); if (!id) return null;
    let hex; try { hex = BigInt(id[1]).toString(16); } catch { return null; }
    return [...document.querySelectorAll('[role="main"]')].find(m => m.querySelector('h2[data-legacy-thread-id]')?.getAttribute('data-legacy-thread-id')?.toLowerCase() === hex) || null;
  }
  function isReply(root) {
    const subject = root.querySelector('input[name="subjectbox"]');
    if (subject) return /^\s*re\s*:/i.test(subject.value);
    const attr = bodyBox(root)?.querySelector('.gmail_attr');
    return !(attr && /^-{5,}/.test(attr.textContent.trim()));
  }
  function subjectFor(root) {
    const field = root.querySelector('input[name="subjectbox"]'); if (field) return field.value;
    const title = scopeFor(root)?.querySelector('h2[data-thread-perm-id],h2[data-legacy-thread-id]')?.textContent.trim();
    if (!title) throw Error('返信の件名を読み取れません。');
    return /^re:/i.test(title) ? title : 'Re: ' + title;
  }
  async function replyHeaders(root) {
    if (!isReply(root)) return {inReplyTo:'', references:''};
    const scope = scopeFor(root); const token = document.documentElement.dataset.localSmtpIk || (document.documentElement.innerHTML.match(/[?&]ik=([0-9a-f]{6,})/i) || [])[1];
    if (!scope || !token) throw Error('返信元を特定できないため送信しません。');
    const nodes = [...scope.querySelectorAll('[data-legacy-message-id]')];
    const above = nodes.filter(n => n.compareDocumentPosition(root) & Node.DOCUMENT_POSITION_FOLLOWING);
    const parent = above.length ? above[above.length - 1] : nodes[nodes.length - 1];
    const hex = parent?.getAttribute('data-legacy-message-id');
    if (!hex || !/^[0-9a-f]+$/i.test(hex)) throw Error('返信元のIDを読み取れません。');
    const user = (/\/mail\/u\/(\d+)/.exec(location.pathname) || [,'0'])[1];
    const url = `${location.origin}/mail/u/${user}/?ik=${encodeURIComponent(token)}&view=om&permmsgid=msg-f:${BigInt('0x'+hex).toString(10)}`;
    const response = await fetch(url, {credentials:'include', signal:AbortSignal.timeout(30000)});
    if (!response.ok) throw Error('返信元のメールを読み取れません。');
    let raw = await response.text();
    if (/^\s*</.test(raw)) { const doc = new DOMParser().parseFromString(raw,'text/html'); raw = doc.querySelector('pre')?.textContent || doc.body?.textContent || ''; }
    const header = name => { const match = raw.match(new RegExp('^'+name+':[ \\t]*([^\\r\\n]*(?:\\r?\\n[ \\t]+[^\\r\\n]*)*)','im')); return match ? match[1].replace(/\r?\n[ \t]+/g,' ').trim() : ''; };
    const parentId = header('Message-ID');
    if (!/^<[^\s<>@]+@[^\s<>@]+>$/.test(parentId)) throw Error('返信元のMessage-IDを確認できません。');
    const ids = (header('References').match(/<[^\s<>@]+@[^\s<>@]+>/g) || []);
    if (ids[ids.length - 1] !== parentId) ids.push(parentId);
    while (ids.join(' ').length > 4000 && ids.length > 1) ids.shift();
    return {inReplyTo:parentId, references:ids.join(' ')};
  }
  function attachmentState(root) {
    const body = bodyBox(root);
    const isAttachmentUI = node => !body || !body.contains(node);
    const links = root.querySelectorAll('a[href*="view=att"]');
    const urls = Array.from(new Set(Array.from(links).filter(isAttachmentUI).map(link => link.href)));
    let count = 0;
    for (const selector of ['div.dL','div.vq','[data-tooltip^="Remove attachment"]']) {
      const visibleItems = Array.from(root.querySelectorAll(selector)).filter(isAttachmentUI);
      count = Math.max(count,visibleItems.length);
    }
    const uploading = Array.from(root.querySelectorAll('[role="progressbar"]')).some(isAttachmentUI);
    return {urls, uploading, unreadable:count > urls.length};
  }
  function decodeName(s) {
    return s.replace(/=\?([^?]+)\?([bq])\?([^?]*)\?=(\s+(?==\?))?/gi, (original, charset, encoding, encoded) => {
      try {
        let bytes;
        if (encoding.toUpperCase() === 'B') bytes = Uint8Array.from(atob(encoded),ch => ch.charCodeAt(0));
        else {
          const values = [];
          for (let i=0; i<encoded.length; i++) {
            if (encoded[i] === '=' && /^[0-9a-f]{2}$/i.test(encoded.slice(i+1,i+3))) { values.push(Number.parseInt(encoded.slice(i+1,i+3),16)); i+=2; }
            else values.push(encoded[i] === '_' ? 32 : encoded.charCodeAt(i));
          }
          bytes = Uint8Array.from(values);
        }
        return new TextDecoder(charset).decode(bytes);
      } catch { return original; }
    });
  }
  function filename(disposition) {
    const star = /filename\*\s*=\s*utf-8''([^;]+)/i.exec(disposition);
    if (star) { try { return decodeURIComponent(star[1].trim()); } catch {} }
    const plain = /filename\s*=\s*"((?:[^"\\]|\\.)*)"/i.exec(disposition) || /filename\s*=\s*([^;\s]+)/i.exec(disposition);
    return plain ? decodeName(plain[1].replace(/\\(.)/g,'$1')) : '';
  }
  function base64(blob) { return new Promise((resolve,reject) => { const r = new FileReader(); r.onload = () => resolve(String(r.result).split(',')[1]); r.onerror = () => reject(Error('添付ファイルを変換できません。')); r.readAsDataURL(blob); }); }
  async function attachments(urls) {
    if (urls.length > 10) throw Error('添付ファイルは10個までです。');
    let total = 0; const result = [];
    for (const source of urls) {
      const url = new URL(source, location.href); if (url.origin !== location.origin) throw Error('添付ファイルのURLを確認できません。');
      const r = await fetch(url.href,{credentials:'include',signal:AbortSignal.timeout(30000)});
      const host = new URL(r.url || url.href).hostname;
      if (!r.ok || !['mail.google.com','mail-attachment.googleusercontent.com'].includes(host)) throw Error('添付ファイルを読み取れません。');
      const name = filename(r.headers.get('content-disposition') || '').replace(/[\x00-\x1f\x7f]/g,'').trim();
      if (!name) throw Error('添付ファイル名を読み取れません。');
      const size = Number(r.headers.get('content-length')); if (size && total + size > maxBytes) throw Error('添付ファイルは合計10 MiBまでです。');
      const blob = await r.blob(); total += blob.size; if (total > maxBytes) throw Error('添付ファイルは合計10 MiBまでです。');
      result.push({name,content:await base64(blob)});
    }
    return result;
  }
  function snapshot(root) {
    const fields = recipientFields(root), body = bodyBox(root), attached = attachmentState(root);
    const account = selected(root);
    if (!account) throw Error('送信元が変更されました。内容を確認してください。');
    if (!body) throw Error('本文を読み取れません。');
    if (body.querySelector('img:not(.gmail_quote img),.gmail_chip')) throw Error('本文内の画像やDriveファイルは非対応です。通常の添付ファイルとして追加してください。');
    if (attached.uploading || attached.unreadable) throw Error('添付ファイルのアップロード完了を待つか、添付を確認してください。');
    let to=recipients(root,fields,'to'), cc=recipients(root,fields,'cc');
    const bcc=recipients(root,fields,'bcc');
    const reply = isReply(root);
    if (reply && to.some(a => a.toLowerCase() === account.from.toLowerCase())) {
      const othersTo = to.filter(a => a.toLowerCase() !== account.from.toLowerCase());
      const othersCc = cc.filter(a => a.toLowerCase() !== account.from.toLowerCase());
      if (othersTo.length) { to = othersTo; cc = othersCc; }
      else if (othersCc.length) { to = othersCc; cc = []; }
      else if (!bcc.length) throw Error('返信先が送信元アドレスだけです。宛先を確認してください。');
    }
    if (!to.length) throw Error('Toに宛先を指定してください。');
    const value = {accountId:account.id,expectedFrom:account.from,to,cc,bcc,subject:subjectFor(root),body:body.innerText,urls:attached.urls,reply};
    if (!value.body.length) throw Error('本文が空です。mailsend-goの仕様により、1文字以上入力してください。');
    if (value.body.includes('\\n')) throw Error('本文に文字列「\\n」があります。送信ツールで改行に変換されるため、送信を中止しました。');
    return value;
  }
  async function expandQuotedText(root) {
    const selector = 'div[role="button"].ajR,div[role="button"][aria-label="Show trimmed content"]';
    const toggle = root.querySelector(selector);
    if (!shown(toggle)) return;

    const body = bodyBox(root);
    if (!body) throw Error('引用した本文を読み取れません。');
    const originalText = body.innerText;
    let edited = false, previousText = '', stableTicks = 0;
    const onInput = event => { if (event.isTrusted && event.target === bodyBox(root)) edited = true; };
    root.addEventListener('input', onInput, true);
    try {
      toggle.click();
      // Gmail may insert quoted text without a .gmail_quote element, and the
      // body can appear after the toggle itself disappears. Wait for both the
      // toggle to disappear and the visible body to change and settle.
      for (let i = 0; i < 80; i++) {
        await new Promise(resolve => setTimeout(resolve, 100));
        if (edited) throw Error('引用の展開中に本文が変更されました。内容を確認して再度送信してください。');
        if (!root.isConnected) throw Error('作成画面が変更されました。内容を確認してください。');
        const currentBody = bodyBox(root);
        if (!currentBody) throw Error('引用した本文を読み取れません。');
        const text = currentBody.innerText;
        if (!shown(root.querySelector(selector)) && text !== originalText) {
          stableTicks = text === previousText ? stableTicks + 1 : 0;
          if (stableTicks >= 4) return;
        } else {
          stableTicks = 0;
        }
        previousText = text;
      }
      throw Error('引用した本文を展開できません。内容を確認してください。');
    } finally {
      root.removeEventListener('input', onInput, true);
    }
  }
  async function send(root) {
    if (busy || !selected(root)) return;
    busy = true; roots().forEach(apply);
    try {
      await expandQuotedText(root);
      const before = snapshot(root);
      const headers = before.reply ? await replyHeaders(root) : {inReplyTo:'',references:''};
      const files = await attachments(before.urls);
      if (!selected(root) || JSON.stringify(snapshot(root)) !== JSON.stringify(before)) throw Error('送信準備中に内容が変更されました。内容を確認して再度送信してください。');
      const result = await request({action:'send',accountId:before.accountId,expectedFrom:before.expectedFrom,to:before.to,cc:before.cc,bcc:before.bcc,subject:before.subject,body:before.body,attachments:files,...headers});
      if (!result.ok) throw Error(result.error || '送信結果を確認できません。再送前に履歴を確認してください。');
      let unchanged = false; try { unchanged = root.isConnected && selected(root) && JSON.stringify(snapshot(root)) === JSON.stringify(before); } catch {}
      if (unchanged) {
        const discard = root.querySelector('div[role="button"][data-tooltip*="Shift-D)"],div[role="button"][aria-label*="Shift-D)"],div[role="button"][data-tooltip^="Discard"],div[role="button"][aria-label^="Discard"],div[role="button"][data-tooltip^="下書きを破棄"],div[role="button"][aria-label^="下書きを破棄"]');
        if (discard) { choices.delete(draftKey(root)); discard.click(); notify('送信しました。履歴用Bccへの配送状況も確認してください。'); }
        else notify('送信しました。下書きが残っています。再送しないよう内容を確認してください。',true);
      } else notify('送信しました。送信中に内容が変わったため、下書きを残しました。再送に注意してください。',true);
    } catch (error) { notify(error.message || '送信できませんでした。',true); }
    finally { busy = false; roots().forEach(apply); if (settingsDirty) refreshSettings(); }
  }
  window.addEventListener('keydown', event => {
    if (event.key !== 'Enter' || !(event.ctrlKey || event.metaKey) || !(event.target instanceof Element)) return;
    const root = roots().find(r => r.contains(event.target)); if (!root || !selected(root)) return;
    event.preventDefault(); event.stopImmediatePropagation(); send(root);
  }, true);
  const observer = new MutationObserver(() => { if (!pending) { pending = true; setTimeout(() => { pending = false; roots().forEach(decorate); }, 200); } });
  let observing = false;
  async function refreshSettings() {
    if (busy) { settingsDirty = true; return; }
    settingsDirty = false;
    const result = await request({action:'status'});
    if (!result.ok || !Array.isArray(result.accounts)) { if (config) notify('送信元設定を更新できません。Gmailを再読み込みしてください。',true); return; }
    if (busy) { settingsDirty = true; return; }
    const previous = config;
    const open = previous ? roots().map(root => {
      const id = root.querySelector('.lsmtp-select')?.value;
      const account = previous.accounts.find(a=>a.id===id);
      return {root,id,from:account?.from};
    }) : [];
    config = result;
    for (const item of open) {
      const native = nativeButton(item.root); restore(native); restore(schedule(native));
      item.root.querySelector('.lsmtp-button')?.remove(); item.root.querySelector('.lsmtp-row')?.remove();
      let choice = item.id === 'gmail' ? 'gmail' : null;
      if (item.from) {
        const same = config.accounts.find(a=>a.id===item.id && a.from.toLowerCase()===item.from.toLowerCase());
        const matches = config.accounts.filter(a=>a.from.toLowerCase()===item.from.toLowerCase());
        choice = same?.id || (matches.length === 1 ? matches[0].id : null);
        if (!choice) { choice = 'gmail'; notify('開いている下書きの送信元が削除または変更されたため、Gmailに切り替えました。送信元を確認してください。',true); }
      }
      if (choice) { rootChoices.set(item.root,choice); if (draftKey(item.root)) choices.set(draftKey(item.root),choice); }
      decorate(item.root);
    }
    if (!observing) { observer.observe(document.body,{childList:true,subtree:true}); observing = true; }
    roots().forEach(decorate);
  }
  chrome.runtime.onMessage.addListener(message => { if (message?.type === 'localSmtpSettingsChanged') refreshSettings(); });
  refreshSettings();
})();
