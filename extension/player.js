function report() {
  const player = document.querySelector("#movie_player");
  const video = player?.querySelector("video");
  const bar = document.querySelector("ytmusic-player-bar");
  const metadata = navigator.mediaSession?.metadata;
  const playerTitle = bar?.querySelector(".title")?.textContent.trim() || "";
  const title = metadata?.title || playerTitle;
  if (!title || !video || video.ended || video.paused || player.classList.contains("ad-showing")) {
    window.postMessage({type: "music-desktop", track: {
      title: "", artist: "", album: "", duration: 0, position: 0, playing: false, rate: 1
    }}, location.origin);
    return;
  }

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

