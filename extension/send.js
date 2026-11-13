chrome.runtime.onMessage.addListener((track, sender, reply) => {
  if (!sender.url?.startsWith("https://music.youtube.com/")) return;
  fetch("http://127.0.0.1:17847/track", {
    method: "POST", headers: {"Content-Type": "application/json"},
    signal: AbortSignal.timeout(3000), body: JSON.stringify({...track, tab: sender.tab.id})
  }).then(response => response.ok, () => false).then(connected => {
    chrome.action.setBadgeText({text: connected ? "ON" : "!"}).catch(() => {});
    chrome.action.setTitle({title: connected ? "Music Desktop · Connected" : "Music Desktop · Open the desktop app"}).catch(() => {});
    reply(connected);
  });
  return true;
});
