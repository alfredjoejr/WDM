// WDM (Windows Download Manager) — Video Stream Hooker (IDM Experience)
// Automatically hooks onto YouTube, Vimeo, Facebook, Twitter, TikTok, and HTML5 video players.

(function () {
  'use strict';

  const ATTACHED_ATTR = 'data-wdm-hooked';
  let activeHooker = null;
  let currentVideoUrl = '';
  let cachedStreams = null;
  let isSniffing = false;
  let mouseIdleTimer = null;

  function init() {
    chrome.storage.local.get(['mediaSniffer'], (res) => {
      if (res.mediaSniffer === false) return;

      scanAndHookVideos();

      // Listen for YouTube Single Page Application (SPA) navigation
      window.addEventListener('yt-navigate-finish', () => {
        setTimeout(handleUrlOrVideoChange, 600);
      });

      window.addEventListener('spfdone', () => {
        setTimeout(handleUrlOrVideoChange, 600);
      });

      // General URL change polling for dynamic websites
      let lastHref = window.location.href;
      setInterval(() => {
        if (window.location.href !== lastHref) {
          lastHref = window.location.href;
          handleUrlOrVideoChange();
        }
      }, 1000);

      // DOM Mutation observer to catch dynamically inserted video players
      const observer = new MutationObserver(() => {
        scanAndHookVideos();
      });

      observer.observe(document.documentElement, {
        childList: true,
        subtree: true
      });
    });
  }

  function handleUrlOrVideoChange() {
    currentVideoUrl = '';
    cachedStreams = null;
    isSniffing = false;
    scanAndHookVideos(true);
  }

  function scanAndHookVideos(forceRehook = false) {
    // 1. YouTube specialized player container
    const ytPlayer = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
    if (ytPlayer) {
      if (forceRehook || !ytPlayer.hasAttribute(ATTACHED_ATTR)) {
        ytPlayer.setAttribute(ATTACHED_ATTR, 'true');
        mountHooker(ytPlayer, true);
      }
      return;
    }

    // 2. Generic HTML5 video players across all websites
    const videos = document.querySelectorAll('video');
    videos.forEach((video) => {
      if (!forceRehook && video.hasAttribute(ATTACHED_ATTR)) return;
      if (video.offsetWidth < 160 || video.offsetHeight < 120) return; // Skip tiny background loops / audio previews

      video.setAttribute(ATTACHED_ATTR, 'true');
      const targetContainer = findBestMountContainer(video);
      mountHooker(targetContainer, false, video);
    });
  }

  function findBestMountContainer(video) {
    let parent = video.parentElement;
    while (parent && parent !== document.body) {
      const style = window.getComputedStyle(parent);
      if (style.position === 'relative' || style.position === 'absolute' || style.position === 'fixed') {
        return parent;
      }
      if (parent.classList.contains('player') || parent.id.includes('player') || parent.classList.contains('video-container')) {
        parent.style.position = 'relative';
        return parent;
      }
      parent = parent.parentElement;
    }

    // Fallback: make direct parent relative
    if (video.parentElement) {
      video.parentElement.style.position = 'relative';
      return video.parentElement;
    }
    return document.body;
  }

  function mountHooker(container, isYouTube = false, videoElement = null) {
    // Prevent duplicate hookers in the same container
    const existing = container.querySelector('.wdm-hooker-wrapper');
    if (existing) {
      activeHooker = existing;
      triggerStreamSniffer();
      return;
    }

    const wrapper = document.createElement('div');
    wrapper.className = 'wdm-hooker-wrapper';

    wrapper.innerHTML = `
      <div class="wdm-hooker-bar" title="Click to view all download formats">
        <div class="wdm-hooker-main">
          <span class="wdm-play-icon">
            <svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg>
          </span>
          <span class="wdm-hooker-label">Download this video</span>
          <span class="wdm-file-count" style="display:none;"></span>
          <span class="wdm-chevron">▾</span>
        </div>
        <div class="wdm-hooker-divider"></div>
        <button class="wdm-hooker-btn wdm-btn-help" title="WDM High-Speed Downloader">?</button>
        <button class="wdm-hooker-btn wdm-btn-close" title="Hide this bar">✕</button>
      </div>

      <!-- Dropdown Formats Menu -->
      <div class="wdm-formats-menu">
        <div class="wdm-menu-header">
          <span class="wdm-menu-title">Detecting video...</span>
        </div>
        <div class="wdm-menu-body">
          <div class="wdm-loading-streams">
            <div class="wdm-spinner"></div>
            <span>Inspecting video streams...</span>
          </div>
        </div>
        <div class="wdm-menu-footer">
          <div class="wdm-footer-item wdm-quick-dl" data-action="quick_download">
            <span>⚡ Quick Download (Best Quality)</span>
          </div>
          <div class="wdm-footer-item wdm-audio-dl" data-action="audio_only">
            <span>🎵 Audio Only (MP3)</span>
          </div>
          <div class="wdm-footer-item wdm-open-app" data-action="open_app">
            <span>🚀 Open WDM Dashboard</span>
          </div>
        </div>
      </div>
    `;

    const mainBtn = wrapper.querySelector('.wdm-hooker-main');
    const closeBtn = wrapper.querySelector('.wdm-btn-close');
    const helpBtn = wrapper.querySelector('.wdm-btn-help');
    const menu = wrapper.querySelector('.wdm-formats-menu');

    // Toggle dropdown menu
    mainBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      e.preventDefault();
      wrapper.classList.toggle('open');
      if (wrapper.classList.contains('open')) {
        if (!cachedStreams) {
          showLoadingSpinner();
          if (!isSniffing) {
            triggerStreamSniffer(true);
          }
        }
      }
    });

    // Close button dismisses bar for this session
    closeBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      wrapper.style.display = 'none';
    });

    helpBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      showToast("WDM Video Downloader is active and catching streams.");
    });

    // Close menu when clicking elsewhere on the page
    document.addEventListener('click', (e) => {
      if (!wrapper.contains(e.target)) {
        wrapper.classList.remove('open');
      }
    });

    // Footer actions
    wrapper.querySelectorAll('.wdm-footer-item').forEach((item) => {
      item.addEventListener('click', (e) => {
        e.stopPropagation();
        wrapper.classList.remove('open');
        const action = item.getAttribute('data-action');

        if (action === 'open_app') {
          showToast("Opening WDM Dashboard...");
          sendExtensionMessage({ action: 'open_app', url: window.location.href }).then((res) => {
            if (!res || res.success === false) {
              showToast("⚠ Could not open WDM Dashboard: " + (res?.message || "Host disconnected"));
            }
          });
        } else if (action === 'audio_only') {
          startDownload(null, true);
        } else {
          startDownload(null, false);
        }
      });
    });

    // Auto-dimming on mouse idle so it doesn't obstruct video playback
    const resetIdleTimer = () => {
      wrapper.classList.remove('wdm-dimmed');
      clearTimeout(mouseIdleTimer);
      mouseIdleTimer = setTimeout(() => {
        if (!wrapper.classList.contains('open') && !wrapper.matches(':hover')) {
          wrapper.classList.add('wdm-dimmed');
        }
      }, 4000);
    };

    container.addEventListener('mousemove', resetIdleTimer);
    container.addEventListener('mouseenter', resetIdleTimer);
    container.addEventListener('mouseleave', () => {
      if (!wrapper.classList.contains('open')) {
        wrapper.classList.add('wdm-dimmed');
      }
    });

    container.appendChild(wrapper);
    activeHooker = wrapper;

    // Begin sniffing streams in background
    triggerStreamSniffer();
  }

  function showLoadingSpinner() {
    if (!activeHooker) return;
    const menuBody = activeHooker.querySelector('.wdm-menu-body');
    if (menuBody) {
      menuBody.innerHTML = `
        <div class="wdm-loading-streams">
          <div class="wdm-spinner"></div>
          <span>Sniffing video & audio streams...</span>
        </div>
      `;
    }
  }

  function sendExtensionMessage(msg) {
    return new Promise((resolve) => {
      try {
        let settled = false;
        const res = chrome.runtime.sendMessage(msg, (response) => {
          if (!settled) {
            settled = true;
            if (chrome.runtime.lastError) {
              resolve({ success: false, message: chrome.runtime.lastError.message });
            } else {
              resolve(response || { success: false, message: "No response from extension" });
            }
          }
        });

        if (res && typeof res.then === 'function') {
          res.then((response) => {
            if (!settled) {
              settled = true;
              resolve(response || { success: false, message: "No response from extension" });
            }
          }).catch((err) => {
            if (!settled) {
              settled = true;
              resolve({ success: false, message: err?.message || String(err) });
            }
          });
        }
      } catch (err) {
        resolve({ success: false, message: err.message });
      }
    });
  }

  function triggerStreamSniffer(force = false) {
    const pageUrl = window.location.href;
    if (!force && pageUrl === currentVideoUrl && cachedStreams) return;

    if (force) cachedStreams = null;
    currentVideoUrl = pageUrl;
    isSniffing = true;

    if (activeHooker) {
      const countBadge = activeHooker.querySelector('.wdm-file-count');
      if (countBadge) countBadge.style.display = 'none';
      if (activeHooker.classList.contains('open')) {
        showLoadingSpinner();
      }
    }

    sendExtensionMessage({
      action: "sniff_media",
      url: pageUrl
    }).then((res) => {
      isSniffing = false;

      if (!activeHooker) return;

      if (res && res.success && res.data && res.data.formats && res.data.formats.length > 0) {
        cachedStreams = res.data;
        updateHookerUI(res.data);
      } else {
        const errorMsg = res?.message || res?.error || (res ? JSON.stringify(res) : 'Unknown response');
        console.warn("[WDM] Stream sniff result:", errorMsg);
        fallbackHtml5Detection(errorMsg);
      }
    });
  }

  function fallbackHtml5Detection(errorMsg = '') {
    const video = document.querySelector('video');
    const directSrc = video ? (video.currentSrc || video.src) : null;

    if (directSrc && !directSrc.startsWith('blob:')) {
      const fallbackData = {
        title: document.title || "HTML5 Web Video",
        formats: [
          { formatId: null, resolution: "Direct Stream", extension: "mp4", fileSizeBytes: null, isAudioOnly: false }
        ]
      };
      cachedStreams = fallbackData;
      updateHookerUI(fallbackData);
    } else if (activeHooker) {
      const menuBody = activeHooker.querySelector('.wdm-menu-body');
      if (menuBody) {
        menuBody.innerHTML = `
          <div style="padding: 14px 10px; text-align: center; color: #94a3b8; font-size: 11px;">
            <span>Ready to download with WDM.</span><br>
            ${errorMsg ? `<div style="color: #f87171; font-size: 10px; margin: 6px 0; word-break: break-all;">(${errorMsg})</div>` : ''}
            <button class="wdm-retry-sniff-btn" style="margin-top: 8px; padding: 4px 12px; background: rgba(0,113,227,0.2); border: 1px solid rgba(0,113,227,0.5); color: #38bdf8; border-radius: 4px; cursor: pointer; font-size: 11px; font-weight: 500;">↻ Retry Sniffing Formats</button>
          </div>
        `;
        const retryBtn = menuBody.querySelector('.wdm-retry-sniff-btn');
        if (retryBtn) {
          retryBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            showLoadingSpinner();
            triggerStreamSniffer(true);
          });
        }
      }
    }
  }

  function updateHookerUI(data) {
    if (!activeHooker) return;

    const formats = data.formats || [];
    const videoCount = formats.length;

    // Update count badge on bar
    const countBadge = activeHooker.querySelector('.wdm-file-count');
    if (countBadge && videoCount > 0) {
      countBadge.textContent = `(${videoCount})`;
      countBadge.style.display = 'inline-block';
    }

    // Update menu title
    const menuTitle = activeHooker.querySelector('.wdm-menu-title');
    if (menuTitle) {
      menuTitle.textContent = data.title || document.title || 'Video Stream';
      menuTitle.title = menuTitle.textContent;
    }

    // Populate formats list
    const menuBody = activeHooker.querySelector('.wdm-menu-body');
    if (!menuBody) return;

    menuBody.innerHTML = '';

    const videoFormats = formats.filter(f => !f.isAudioOnly);
    const audioFormats = formats.filter(f => f.isAudioOnly);

    if (videoFormats.length > 0) {
      const groupLabel = document.createElement('div');
      groupLabel.className = 'wdm-menu-group-label';
      groupLabel.textContent = 'Video Streams';
      menuBody.appendChild(groupLabel);

      videoFormats.forEach((f) => {
        const item = createFormatMenuItem(f, data.title);
        menuBody.appendChild(item);
      });
    }

    if (audioFormats.length > 0) {
      const groupLabel = document.createElement('div');
      groupLabel.className = 'wdm-menu-group-label';
      groupLabel.textContent = 'Audio Streams';
      menuBody.appendChild(groupLabel);

      audioFormats.forEach((f) => {
        const item = createFormatMenuItem(f, data.title);
        menuBody.appendChild(item);
      });
    }
  }

  function createFormatMenuItem(f, videoTitle) {
    const item = document.createElement('div');
    item.className = 'wdm-stream-item';

    const tagClass = f.isAudioOnly ? 'wdm-tag-audio' : 'wdm-tag-video';
    const tagText = f.isAudioOnly ? 'AUDIO' : (f.extension ? f.extension.toUpperCase() : 'VIDEO');
    const sizeText = f.fileSizeBytes ? formatBytes(f.fileSizeBytes) : (f.note || '');

    item.innerHTML = `
      <div class="wdm-stream-left">
        <span class="wdm-stream-tag ${tagClass}">${tagText}</span>
        <span class="wdm-stream-res">${f.resolution}</span>
      </div>
      <span class="wdm-stream-size">${sizeText}</span>
    `;

    item.addEventListener('click', (e) => {
      e.stopPropagation();
      if (activeHooker) activeHooker.classList.remove('open');
      startDownload(f.formatId, f.isAudioOnly, videoTitle, f.extension);
    });

    return item;
  }

  function startDownload(formatId = null, isAudio = false, title = null, ext = null) {
    const pageUrl = window.location.href;
    const safeTitle = (title || document.title || 'Video').replace(/[/\\?%*:|"<>]/g, '_').trim();
    const finalExt = ext || (isAudio ? 'mp3' : 'mp4');
    const fileName = `${safeTitle}.${finalExt}`;

    showToast(`✓ Starting high-speed download: ${fileName}`);

    sendExtensionMessage({
      action: "download_media",
      url: pageUrl,
      formatId: formatId,
      fileName: fileName,
      isAudioOnly: isAudio
    }).then((res) => {
      if (res && (res.action === 'media_download_started' || res.downloadId)) {
        showToast(`✓ WDM is downloading at accelerated speed!`);
      }
    });
  }

  function showToast(message) {
    const existing = document.querySelector('.wdm-extension-toast');
    if (existing) existing.remove();

    const toast = document.createElement('div');
    toast.className = 'wdm-extension-toast';
    toast.textContent = message;
    document.body.appendChild(toast);

    setTimeout(() => {
      if (toast.parentElement) toast.remove();
    }, 3200);
  }

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB'];
    let i = 0;
    while (bytes >= 1024 && i < units.length - 1) {
      bytes /= 1024;
      i++;
    }
    return bytes.toFixed(i === 0 ? 0 : 1) + ' ' + units[i];
  }

  // Launch on DOM ready
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
