function localizedTitle(title, bar, tabTitle, rows, videoId) {
  const hasNativeScript = value => /[\u3041-\u30fa\u3400-\u9fff]/u.test(value);
  const key = value => value.normalize("NFKC").toLowerCase().replace(/[\p{P}\s]/gu, "");
  const current = key(title);
  const matchesTitle = value => key(value) === current ||
    value.split(/\s+[-–—|/]\s+|[()（）【】]/u).some(part => key(part) === current);
  const candidates = [bar, tabTitle].filter(value => hasNativeScript(value) && matchesTitle(value));
  for (const row of rows) {
    const data = row.data;
    const renderer = data?.playlistPanelVideoRenderer ||
      data?.playlistPanelVideoWrapperRenderer?.primaryRenderer?.playlistPanelVideoRenderer || data;
    const rowTitle = row.querySelector(".song-title")?.textContent.trim() || "";
    if (!hasNativeScript(rowTitle)) continue;
    if (renderer?.videoId && videoId ? renderer.videoId === videoId : matchesTitle(rowTitle)) {
      candidates.push(rowTitle);
    }
  }
  return candidates.sort((a, b) => b.length - a.length)[0] || bar;
}

function report() {
  const player = document.querySelector("#movie_player");
  const video = player?.querySelector("video");
  const bar = document.querySelector("ytmusic-player-bar");
  const metadata = navigator.mediaSession?.metadata;
  const barTitle = bar?.querySelector(".title")?.textContent.trim() || "";
  const title = metadata?.title || barTitle;
  if (!title || !video || video.ended || video.paused || player.classList.contains("ad-showing")) {
    window.postMessage({type: "music-desktop", track: {
      title: "", artist: "", album: "", duration: 0, position: 0, playing: false, rate: 1
    }}, location.origin);
    return;
  }

  const tabTitle = document.title.replace(/\s*[-–—|]\s*YouTube Music\s*$/i, "").trim();
  const rows = document.querySelectorAll("ytmusic-player-queue-item[selected], ytmusic-player-queue-item[aria-selected='true']");
  const videoId = player.getVideoData?.()?.video_id || new URLSearchParams(location.search).get("v");
  const playerTitle = localizedTitle(title, barTitle, tabTitle, rows, videoId);
  window.postMessage({type: "music-desktop", track: {
    title, artist: metadata?.artist || bar?.querySelector(".byline a")?.textContent.trim() || "",
    album: metadata?.album || "", playerTitle,
    duration: Number.isFinite(video.duration) ? video.duration : 0,
    position: video.currentTime, playing: true, advancing: !video.seeking && video.readyState >= 3,
    rate: video.playbackRate
  }}, location.origin);
}
setInterval(report, 1000);
for (const event of ["playing", "pause", "ended", "emptied", "seeking", "seeked", "waiting", "ratechange"]) document.addEventListener(event, report, true);
report();

