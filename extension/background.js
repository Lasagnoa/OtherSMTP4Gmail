const HOST = 'jp.local_smtp_gmail';
chrome.runtime.onMessage.addListener((message, sender, respond) => {
  const extensionPage = !sender.tab && sender.url === chrome.runtime.getURL('popup.html');
  const gmailPage = sender.tab && sender.url && /^https:\/\/mail\.google\.com\//.test(sender.url) && sender.frameId === 0;
  if (!extensionPage && !gmailPage) return;
  if (!message || !['status', 'send', 'getSettings', 'saveSettings'].includes(message.action)) return;
  if (message.action === 'send' && !gmailPage) return;
  if (['getSettings', 'saveSettings'].includes(message.action) && !extensionPage) return;
  chrome.runtime.sendNativeMessage(HOST, message, (result) => {
    if (chrome.runtime.lastError) {
      respond({ok: false, uncertain: message.action === 'send', error: 'ローカル連携プログラムと通信できません。送信操作中の場合は送信済みの可能性があります。再送前に履歴を確認してください。'});
    } else {
      respond(result || {ok: false, uncertain: message.action === 'send', error: '連携プログラムから応答がありません。'});
      if (message.action === 'saveSettings' && result?.ok) {
        chrome.tabs.query({url: 'https://mail.google.com/*'}, tabs => {
          for (const tab of tabs) if (tab.id != null) chrome.tabs.sendMessage(tab.id, {type:'localSmtpSettingsChanged'}, () => void chrome.runtime.lastError);
        });
      }
    }
  });
  return true;
});
