let last = 0;
let disconnected = false;
const send = chrome.runtime.sendMessage.bind(chrome.runtime);
const pageOrigin = location.origin;
function relay(event) {
  if (disconnected) return;
  try {
    if (event.source !== window || event.origin !== pageOrigin || event.data?.type !== "music-desktop") return;
    if (Date.now() - last < 200) return;
    last = Date.now();
    send(event.data.track).catch(handleError);
  } catch (error) {
    handleError(error);
  }
}
function handleError(error) {
  if (!String(error).includes("Extension context invalidated")) return;
  disconnected = true;
  try {
    window.removeEventListener("message", relay);
  } catch (cleanupError) {
    if (!String(cleanupError).includes("Extension context invalidated")) throw cleanupError;
  }
}
window.addEventListener("message", relay);
