// WDM Content Script - Video Stream Sniffer & Floating Badge Injector

(function () {
  'use strict';

  const ATTACHED_CLASS = 'wdm-video-attached';

  function initMediaObserver() {
    chrome.storage.local.get(['mediaSniffer'], (res) => {
      if (res.mediaSniffer === false) return;

      scanForMediaElements();

      const observer = new MutationObserver((mutations) => {
        scanForMediaElements();
      });

      observer.observe(document.body, { childList: true, subtree: true });
    });
  }

  function scanForMediaElements() {
    const videos = document.querySelectorAll('video');
    videos.forEach((video) => {
      if (video.classList.contains(ATTACHED_CLASS)) return;
      if (video.offsetWidth < 120 || video.offsetHeight < 120) return; // Skip tiny background or audio icons

      video.classList.add(ATTACHED_CLASS);
      attachDownloadBadge(video);
    });
  }

  function attachDownloadBadge(video) {
    const parent = video.parentElement;
    if (!parent) return;

    const computedStyle = window.getComputedStyle(parent);
    if (computedStyle.position === 'static') {
      parent.style.position = 'relative';
    }

    const container = document.createElement('div');
    container.className = 'wdm-media-overlay-container';

    container.innerHTML = `
      <button class="wdm-badge-btn" title="Download this video with WDM">
        <svg viewBox="0 0 24 24"><path d="M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM17 13l-5 5-5-5h3V9h4v4h3z"/></svg>
        <span>Download Video</span>
      </button>
      <div class="wdm-dropdown-menu">
        <div class="wdm-dropdown-item" data-action="quick_download">
          <span class="quality">⚡ Best Quality (MP4)</span>
          <span class="size">Auto</span>
        </div>
        <div class="wdm-dropdown-item" data-action="audio_only">
          <span class="quality">🎵 Audio Only (MP3)</span>
          <span class="size">Audio</span>
        </div>
        <div class="wdm-dropdown-item" data-action="inspect_formats">
          <span class="quality">🔍 Inspect All Formats...</span>
        </div>
      </div>
    `;

    const badgeBtn = container.querySelector('.wdm-badge-btn');
    const menu = container.querySelector('.wdm-dropdown-menu');

    badgeBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      e.preventDefault();
      menu.classList.toggle('active');
    });

    document.addEventListener('click', () => {
      menu.classList.remove('active');
    });

    menu.querySelectorAll('.wdm-dropdown-item').forEach((item) => {
      item.addEventListener('click', (e) => {
        e.stopPropagation();
        menu.classList.remove('active');

        const actionType = item.getAttribute('data-action');
        handleMediaDownload(actionType, video);
      });
    });

    parent.appendChild(container);
  }

  function handleMediaDownload(actionType, video) {
    const pageUrl = window.location.href;
    const directSrc = video.currentSrc || video.src;

    showToast("Forwarding video to WDM high-speed downloader...");

    if (actionType === "audio_only") {
      chrome.runtime.sendMessage({
        action: "download_media",
        url: pageUrl,
        formatId: "bestaudio/best"
      }, (res) => {
        showToast("Download started in WDM!");
      });
    } else if (actionType === "inspect_formats") {
      showToast("Inspecting available streams...");
      chrome.runtime.sendMessage({
        action: "sniff_media",
        url: pageUrl
      }, (res) => {
        if (res?.success && res.data?.formats) {
          showToast(`Found ${res.data.formats.length} streams for "${res.data.title}"`);
        } else {
          showToast("Could not inspect streams. Check yt-dlp installation.");
        }
      });
    } else {
      // Default: Best Quality
      chrome.runtime.sendMessage({
        action: "download_media",
        url: pageUrl
      }, (res) => {
        showToast("Download started in WDM!");
      });
    }
  }

  function showToast(message) {
    const existing = document.querySelector('.wdm-toast');
    if (existing) existing.remove();

    const toast = document.createElement('div');
    toast.className = 'wdm-toast';
    toast.textContent = message;
    document.body.appendChild(toast);

    setTimeout(() => {
      if (toast.parentElement) toast.remove();
    }, 3200);
  }

  // Start media observer once DOM is ready
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initMediaObserver);
  } else {
    initMediaObserver();
  }
})();
