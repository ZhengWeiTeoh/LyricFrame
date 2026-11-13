function queueRenderer(item) {
  return item?.playlistPanelVideoRenderer ||
    item?.playlistPanelVideoWrapperRenderer?.primaryRenderer?.playlistPanelVideoRenderer || item;
}

function queueTitle(item) {
  const title = queueRenderer(item)?.title;
  return (typeof title === "string" ? title :
    title?.runs?.map(run => run.text).join("") || title?.simpleText || "").trim();
}

const titleKey = title => title.normalize("NFKC").trim().replace(/\s+/g, " ").toLowerCase();

function matchesTrack(itemTitle, itemId, title, videoId) {
  return itemId && videoId ? itemId === videoId :
    Boolean(itemTitle) && titleKey(itemTitle) === titleKey(title);
}

function nextSong(state, title, videoId, rows) {
  const rowTitle = row => row?.querySelector(".song-title")?.textContent.trim() || "";
  const rowId = row => queueRenderer(row?.data)?.videoId;
  const upcomingTitle = (name, id) => {
    if (!name || (id && id === videoId)) return "";
    if (titleKey(name) === titleKey(title) && !(id && videoId)) return "";
    return name;
  };
  const selectedRows = rows.filter(row => row.hasAttribute("selected") || row.selected === true);
  if (selectedRows.length === 1) {
    const row = selectedRows[0];
    if (matchesTrack(rowTitle(row), rowId(row), title, videoId)) {
      const following = rows[rows.indexOf(row) + 1];
      const next = upcomingTitle(rowTitle(following), rowId(following));
      if (next) return next;
    }
  }

  const items = state?.items || [];
  const matches = item => matchesTrack(queueTitle(item), queueRenderer(item)?.videoId, title, videoId);
  let index = state?.selectedItemIndex;
  if (!Number.isInteger(index) || index < 0 || !matches(items[index])) {
    const matching = items.map((item, i) => matches(item) ? i : -1).filter(i => i >= 0);
    if (matching.length === 1) index = matching[0];
    else {
      const following = selectedRows.length === 1 ? rows[rows.indexOf(selectedRows[0]) + 1] : null;
      const visibleNext = upcomingTitle(rowTitle(following), rowId(following));
      if (visibleNext) return visibleNext;
      if (!Number.isInteger(index) || index < 0 || queueTitle(items[index])) return "";
    }
  }
  const following = items[index + 1] || state?.automixItems?.[0];
  return upcomingTitle(queueTitle(following), queueRenderer(following)?.videoId);
}

function report() {
  const player = document.querySelector("#movie_player");
  const video = player?.querySelector("video");
  const bar = document.querySelector("ytmusic-player-bar");
  const metadata = navigator.mediaSession?.metadata;
  const title = metadata?.title || bar?.querySelector(".title")?.textContent.trim();
  if (!title || !video || video.ended || video.paused || player.classList.contains("ad-showing")) {
    window.postMessage({type: "music-desktop", track: {
      title: "", artist: "", album: "", next: "", duration: 0, position: 0, playing: false, rate: 1
    }}, location.origin);
    return;
  }

  const queue = bar?.queue;
  let state = queue;
  try {
    state = queue?.store?.getState?.() || queue?.getState?.() || queue;
  } catch {
    // The DOM queue can still provide the next title.
  }
  const rows = [...document.querySelectorAll("ytmusic-player-queue-item")];
  const videoId = player.getVideoData?.()?.video_id || new URLSearchParams(location.search).get("v");
  const next = nextSong(state, title, videoId, rows);
  window.postMessage({type: "music-desktop", track: {
    title, artist: metadata?.artist || bar?.querySelector(".byline a")?.textContent.trim() || "",
    album: metadata?.album || "", next,
    duration: Number.isFinite(video.duration) ? video.duration : 0,
    position: video.currentTime, playing: true,
    rate: video.playbackRate
  }}, location.origin);
}
setInterval(report, 1000);
for (const event of ["playing", "pause", "ended", "emptied", "seeked", "ratechange"]) document.addEventListener(event, report, true);
report();
