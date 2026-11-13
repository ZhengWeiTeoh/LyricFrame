function report() {
  const player = document.querySelector("#movie_player");
  const video = player?.querySelector("video");
  const bar = document.querySelector("ytmusic-player-bar");
  const metadata = navigator.mediaSession?.metadata;
  const title = metadata?.title || bar?.querySelector(".title")?.textContent.trim();
  if (!title || !video || video.ended || video.paused || player.classList.contains("ad-showing")) {
    window.postMessage({type: "music-desktop", track: {
      title: "", artist: "", album: "", duration: 0, position: 0, playing: false, rate: 1
    }}, location.origin);
    return;
  }

  window.postMessage({type: "music-desktop", track: {
    title, artist: metadata?.artist || bar?.querySelector(".byline a")?.textContent.trim() || "",
    album: metadata?.album || "",
    duration: Number.isFinite(video.duration) ? video.duration : 0,
    position: video.currentTime, playing: true,
    rate: video.playbackRate
  }}, location.origin);
}
setInterval(report, 1000);
for (const event of ["playing", "pause", "ended", "emptied", "seeked", "ratechange"]) document.addEventListener(event, report, true);
report();

