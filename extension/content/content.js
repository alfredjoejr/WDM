// WDM (Windows Download Manager) — Video & Social Media Stream Hooker (IDM Experience)
// Supports YouTube, Instagram (Stories, Story Highlights, Reels, Video & Photo Posts, Carousels),
// TikTok, Twitter/X, Facebook, Vimeo, and generic HTML5 video players via a top-level portal
// that is never trapped underneath site click-shields or story overlays.

(function () {
  'use strict';

  const isInstagram = /(^|\.)instagram\.com$/i.test(window.location.hostname);

  let activeHooker = null;
  let trackedAnchorEl = null;
  let currentTargetUrl = '';
  let currentSlideIndex = null;
  let cachedStreams = null;
  let isSniffing = false;
  let dismissedForUrl = '';
  let mouseIdleTimer = null;

  function init() {
    chrome.storage.local.get(['mediaSniffer'], (res) => {
      if (res.mediaSniffer === false) return;

      ensurePortal();
      scanActiveMedia();

      // YouTube SPA navigation hooks
      window.addEventListener('yt-navigate-finish', () => setTimeout(handleNavigationChange, 450));
      window.addEventListener('spfdone', () => setTimeout(handleNavigationChange, 450));

      // Reposition portal cleanly on scroll, resize, and fullscreen transitions
      window.addEventListener('scroll', updatePortalPosition, { passive: true, capture: true });
      window.addEventListener('resize', updatePortalPosition, { passive: true });
      document.addEventListener('fullscreenchange', () => {
        reattachPortalRoot();
        updatePortalPosition();
      });

      // Poll URL, active video/story target, and story slide transitions
      let lastHref = window.location.href;
      setInterval(() => {
        if (window.location.href !== lastHref) {
          lastHref = window.location.href;
          dismissedForUrl = '';
          handleNavigationChange();
          return;
        }
        scanActiveMedia();
        updatePortalPosition();
      }, 450);

      const observer = new MutationObserver(() => {
        scanActiveMedia();
      });

      observer.observe(document.documentElement, {
        childList: true,
        subtree: true
      });
    });
  }

  function handleNavigationChange() {
    currentTargetUrl = '';
    currentSlideIndex = null;
    cachedStreams = null;
    isSniffing = false;
    scanActiveMedia(true);
  }

  function createSvgPlayIcon() {
    const svgNS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(svgNS, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    const path = document.createElementNS(svgNS, 'path');
    path.setAttribute('d', 'M8 5v14l11-7z');
    svg.appendChild(path);
    return svg;
  }

  function ensurePortal() {
    if (activeHooker && document.documentElement.contains(activeHooker)) {
      return activeHooker;
    }

    const wrapper = document.createElement('div');
    wrapper.className = 'wdm-hooker-wrapper';
    wrapper.style.display = 'none';

    // Top bar
    const bar = document.createElement('div');
    bar.className = 'wdm-hooker-bar';
    bar.title = 'Click to view all download formats';

    const mainBtn = document.createElement('div');
    mainBtn.className = 'wdm-hooker-main';

    const playSpan = document.createElement('span');
    playSpan.className = 'wdm-play-icon';
    playSpan.appendChild(createSvgPlayIcon());

    const labelSpan = document.createElement('span');
    labelSpan.className = 'wdm-hooker-label';
    labelSpan.textContent = 'Download this video';

    const countSpan = document.createElement('span');
    countSpan.className = 'wdm-file-count';
    countSpan.style.display = 'none';

    const chevronSpan = document.createElement('span');
    chevronSpan.className = 'wdm-chevron';
    chevronSpan.textContent = '\u25BE';

    mainBtn.appendChild(playSpan);
    mainBtn.appendChild(labelSpan);
    mainBtn.appendChild(countSpan);
    mainBtn.appendChild(chevronSpan);

    const divider = document.createElement('div');
    divider.className = 'wdm-hooker-divider';

    const helpBtn = document.createElement('button');
    helpBtn.className = 'wdm-hooker-btn wdm-btn-help';
    helpBtn.title = 'WDM High-Speed Downloader';
    helpBtn.textContent = '?';

    const closeBtn = document.createElement('button');
    closeBtn.className = 'wdm-hooker-btn wdm-btn-close';
    closeBtn.title = 'Hide this bar';
    closeBtn.textContent = '\u2715';

    bar.appendChild(mainBtn);
    bar.appendChild(divider);
    bar.appendChild(helpBtn);
    bar.appendChild(closeBtn);

    // Dropdown menu
    const menu = document.createElement('div');
    menu.className = 'wdm-formats-menu';

    const menuHeader = document.createElement('div');
    menuHeader.className = 'wdm-menu-header';
    const menuTitle = document.createElement('span');
    menuTitle.className = 'wdm-menu-title';
    menuTitle.textContent = 'Detecting media...';
    menuHeader.appendChild(menuTitle);

    const menuBody = document.createElement('div');
    menuBody.className = 'wdm-menu-body';

    const menuFooter = document.createElement('div');
    menuFooter.className = 'wdm-menu-footer';

    const footerActions = [
      { cls: 'wdm-quick-dl', action: 'quick_download', text: '\u26A1 Quick Download (Best Quality)' },
      { cls: 'wdm-audio-dl', action: 'audio_only', text: '\uD83C\uDFB5 Audio Only (MP3)' },
      { cls: 'wdm-open-app', action: 'open_app', text: '\uD83D\uDE80 Open WDM Dashboard' }
    ];

    footerActions.forEach((cfg) => {
      const fItem = document.createElement('div');
      fItem.className = `wdm-footer-item ${cfg.cls}`;
      fItem.setAttribute('data-action', cfg.action);
      const s = document.createElement('span');
      s.textContent = cfg.text;
      fItem.appendChild(s);
      fItem.addEventListener('click', (e) => {
        e.stopPropagation();
        e.preventDefault();
        wrapper.classList.remove('open');
        const resolvedUrl = resolveEffectiveMediaUrl();
        if (cfg.action === 'open_app') {
          showToast('Opening WDM Dashboard...');
          sendExtensionMessage({ action: 'open_app', url: resolvedUrl }).then((res) => {
            if (!res || res.success === false) {
              showToast('\u26A0 Could not open WDM Dashboard: ' + (res?.message || 'Host disconnected'));
            }
          });
        } else if (cfg.action === 'audio_only') {
          startDownload({ formatId: null, isAudioOnly: true });
        } else {
          // Pick current/best format if cached
          const bestFmt = cachedStreams && Array.isArray(cachedStreams.formats) && cachedStreams.formats.length > 0
            ? cachedStreams.formats[0]
            : null;
          if (bestFmt) {
            startDownload({
              formatId: bestFmt.formatId,
              directUrl: bestFmt.directUrl || null,
              isAudioOnly: !!bestFmt.isAudioOnly,
              title: cachedStreams.title,
              ext: bestFmt.extension,
              resolution: bestFmt.resolution,
              fileSizeBytes: bestFmt.fileSizeBytes
            });
          } else {
            startDownload({ formatId: null, isAudioOnly: false });
          }
        }
      });
      menuFooter.appendChild(fItem);
    });

    menu.appendChild(menuHeader);
    menu.appendChild(menuBody);
    menu.appendChild(menuFooter);

    wrapper.appendChild(bar);
    wrapper.appendChild(menu);

    // Shield all pointer/mouse/touch events from bubbling into Instagram's story pause/next overlay!
    ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click', 'dblclick', 'touchstart', 'touchend', 'contextmenu'].forEach((evtName) => {
      wrapper.addEventListener(evtName, (e) => {
        e.stopPropagation();
      });
    });

    mainBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      e.preventDefault();
      wrapper.classList.toggle('open');
      wrapper.classList.remove('wdm-dimmed');
      if (wrapper.classList.contains('open')) {
        const freshUrl = resolveEffectiveMediaUrl();
        if (!cachedStreams || freshUrl !== currentTargetUrl) {
          showLoadingSpinner();
          triggerStreamSniffer(true);
        }
      }
    });

    closeBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      e.preventDefault();
      dismissedForUrl = window.location.href;
      wrapper.classList.remove('open');
      wrapper.style.display = 'none';
    });

    helpBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      e.preventDefault();
      showToast('WDM Downloader is active — supports Videos, Stories, Highlights, Reels & Posts.');
    });

    document.addEventListener('click', (e) => {
      if (wrapper && !wrapper.contains(e.target)) {
        wrapper.classList.remove('open');
      }
    }, true);

    wrapper.addEventListener('mouseenter', () => {
      wrapper.classList.remove('wdm-dimmed');
      clearTimeout(mouseIdleTimer);
    });

    activeHooker = wrapper;
    reattachPortalRoot();
    showLoadingSpinner();
    return wrapper;
  }

  function reattachPortalRoot() {
    if (!activeHooker) return;
    const hostRoot = document.fullscreenElement || document.body || document.documentElement;
    if (hostRoot && activeHooker.parentElement !== hostRoot) {
      hostRoot.appendChild(activeHooker);
    }
  }

  function isElementVisibleInViewport(el, minW = 160, minH = 120) {
    if (!el || !el.isConnected) return false;
    const rect = el.getBoundingClientRect();
    if (rect.width < minW || rect.height < minH) return false;
    const visTop = Math.max(0, rect.top);
    const visBottom = Math.min(window.innerHeight, rect.bottom);
    const visLeft = Math.max(0, rect.left);
    const visRight = Math.min(window.innerWidth, rect.right);
    return (visBottom - visTop) >= 80 && (visRight - visLeft) >= 80;
  }

  function pickBestViewportElement(candidates, minW = 180, minH = 150) {
    const viewCenterY = window.innerHeight / 2;
    const viewCenterX = window.innerWidth / 2;
    let best = null;
    let bestScore = -Infinity;

    candidates.forEach((el) => {
      if (!isElementVisibleInViewport(el, minW, minH)) return;
      const r = el.getBoundingClientRect();
      const centerX = r.left + r.width / 2;
      const centerY = r.top + r.height / 2;
      const dist = Math.hypot(centerX - viewCenterX, centerY - viewCenterY);
      const area = r.width * r.height;
      const playingBonus = (el.tagName === 'VIDEO' && !el.paused) ? 250000 : 0;
      const score = area + playingBonus - (dist * 450);
      if (score > bestScore) {
        bestScore = score;
        best = el;
      }
    });

    return best;
  }

  function findInstagramActiveMedia() {
    const path = window.location.pathname;

    // 1. Instagram Stories & Story Highlights (/stories/...)
    if (path.startsWith('/stories/')) {
      const vids = Array.from(document.querySelectorAll('video'));
      const activeVid = pickBestViewportElement(vids, 180, 220);
      if (activeVid) {
        return { anchor: findInstagramStoryCard(activeVid) || activeVid, kind: 'story_video', mediaEl: activeVid };
      }

      // Photo Story slide (no <video>, large story <img>)
      const imgs = Array.from(document.querySelectorAll('img')).filter((img) => {
        const r = img.getBoundingClientRect();
        return r.width >= 200 && r.height >= 260;
      });
      const activeImg = pickBestViewportElement(imgs, 200, 260);
      if (activeImg) {
        return { anchor: findInstagramStoryCard(activeImg) || activeImg, kind: 'story_photo', mediaEl: activeImg };
      }
      return null;
    }

    // 2. Instagram Reels, Posts, Modal Dialogs, and Home Feed
    const scopeRoot = document.querySelector('div[role="dialog"]') || document;
    const vids = Array.from(scopeRoot.querySelectorAll('video'));
    const bestVid = pickBestViewportElement(vids, 180, 180);
    if (bestVid) {
      return { anchor: bestVid, kind: 'ig_video', mediaEl: bestVid };
    }

    // Photo Post / Carousel on /p/, modal dialog, or feed <article>
    if (path.startsWith('/p/') || path.startsWith('/reel/') || path.startsWith('/reels/') || document.querySelector('div[role="dialog"]') || document.querySelector('article')) {
      const postImgs = Array.from(scopeRoot.querySelectorAll('article img, div[role="dialog"] img, main img')).filter((img) => {
        const r = img.getBoundingClientRect();
        if (r.width < 240 || r.height < 240) return false;
        // Exclude tiny profile avatars
        const alt = (img.alt || '').toLowerCase();
        if (alt.includes('profile picture')) return false;
        return true;
      });
      const bestImg = pickBestViewportElement(postImgs, 240, 240);
      if (bestImg) {
        return { anchor: bestImg, kind: 'ig_photo', mediaEl: bestImg };
      }
    }

    return null;
  }

  function findInstagramStoryCard(el) {
    let cur = el.parentElement;
    let best = el;
    while (cur && cur !== document.body) {
      const r = cur.getBoundingClientRect();
      // Story player card has vertical phone aspect ratio (~300-600px wide, 500-950px tall)
      if (r.width >= 240 && r.width <= Math.min(680, window.innerWidth * 0.92) && r.height >= 360 && r.height <= window.innerHeight) {
        best = cur;
      }
      cur = cur.parentElement;
    }
    return best;
  }

  function detectInstagramSlideIndex(anchorEl) {
    if (!isInstagram) return null;
    const path = window.location.pathname;

    // Check URL query parameter ?img_index=K first (Instagram carousels)
    const params = new URLSearchParams(window.location.search);
    const imgIdx = parseInt(params.get('img_index') || '', 10);
    if (!Number.isNaN(imgIdx) && imgIdx >= 1) return imgIdx;

    // Story / Highlight progress bar detection:
    // At the top of the story card, Instagram renders 1 horizontal segment per slide.
    // Completed slides have an inner bar at ~100% width; active slide has >0% width; upcoming slides have 0% width.
    if (path.startsWith('/stories/') && anchorEl) {
      const cardRect = anchorEl.getBoundingClientRect();
      // Find candidate progress track containers near the top 45px of the story card
      const allDivs = anchorEl.querySelectorAll('div');
      for (const container of allDivs) {
        const children = container.children;
        if (children.length < 2 || children.length > 100) continue;
        const cRect = container.getBoundingClientRect();
        if (Math.abs(cRect.top - cardRect.top) > 50 || cRect.height > 14 || cRect.width < cardRect.width * 0.6) {
          continue;
        }

        // Verify children sit horizontally side-by-side (progress segments)
        const firstRect = children[0].getBoundingClientRect();
        const secondRect = children[1].getBoundingClientRect();
        if (secondRect.left <= firstRect.left || firstRect.height > 10) continue;

        let activeSlide = 1;
        for (let i = 0; i < children.length; i++) {
          const seg = children[i];
          const segRect = seg.getBoundingClientRect();
          const inner = seg.firstElementChild;
          if (!inner) continue;
          const innerRect = inner.getBoundingClientRect();
          const fillRatio = segRect.width > 0 ? (innerRect.width / segRect.width) : 0;
          if (fillRatio > 0.02) {
            activeSlide = i + 1;
          }
        }
        return activeSlide;
      }
    }

    return null;
  }

  function resolveEffectiveMediaUrl() {
    let base = window.location.href.split('#')[0];

    if (isInstagram) {
      const path = window.location.pathname;
      // If browsing Home Feed (/), Profile, or Explore modal, locate permalink (/p/... or /reel/...) of tracked media
      if (!path.startsWith('/stories/') && !path.startsWith('/p/') && !path.startsWith('/reel/') && !path.startsWith('/reels/') && !path.startsWith('/tv/') && trackedAnchorEl) {
        const container = trackedAnchorEl.closest('article') || trackedAnchorEl.closest('div[role="dialog"]');
        if (container) {
          const link = container.querySelector('a[href*="/reel/"], a[href*="/reels/"], a[href*="/p/"], a[href*="/tv/"]');
          if (link && link.href) {
            base = link.href.split('#')[0];
          }
        }
      }

      const slideIdx = detectInstagramSlideIndex(trackedAnchorEl);
      currentSlideIndex = slideIdx;
      if (slideIdx && slideIdx >= 1) {
        return `${base}#wdm-slide=${slideIdx}`;
      }
    }

    return base;
  }

  function scanActiveMedia(forceRefresh = false) {
    ensurePortal();
    if (!activeHooker) return;

    if (dismissedForUrl && dismissedForUrl === window.location.href) {
      activeHooker.style.display = 'none';
      return;
    }

    let nextAnchor = null;
    let barLabelText = 'Download this video';

    if (isInstagram) {
      const igTarget = findInstagramActiveMedia();
      if (igTarget) {
        nextAnchor = igTarget.anchor;
        if (igTarget.kind === 'story_photo') barLabelText = 'Download Story Photo';
        else if (igTarget.kind === 'story_video') barLabelText = 'Download Story';
        else if (igTarget.kind === 'ig_photo') barLabelText = 'Download Photo / Post';
        else barLabelText = 'Download Reel / Video';
      }
    } else {
      const ytPlayer = document.querySelector('#movie_player') || document.querySelector('.html5-video-player');
      if (ytPlayer && isElementVisibleInViewport(ytPlayer, 200, 140)) {
        nextAnchor = ytPlayer;
      } else {
        const vids = Array.from(document.querySelectorAll('video'));
        nextAnchor = pickBestViewportElement(vids, 160, 120);
      }
    }

    if (!nextAnchor) {
      if (!activeHooker.classList.contains('open')) {
        activeHooker.style.display = 'none';
      }
      trackedAnchorEl = null;
      return;
    }

    const anchorChanged = trackedAnchorEl !== nextAnchor;
    trackedAnchorEl = nextAnchor;

    // Bind mouse wake listener on tracked container once
    if (!nextAnchor.hasAttribute('data-wdm-mouse-bound')) {
      nextAnchor.setAttribute('data-wdm-mouse-bound', '1');
      const wakeBar = () => {
        if (!activeHooker) return;
        activeHooker.classList.remove('wdm-dimmed');
        clearTimeout(mouseIdleTimer);
        mouseIdleTimer = setTimeout(() => {
          if (activeHooker && !activeHooker.classList.contains('open') && !activeHooker.matches(':hover')) {
            activeHooker.classList.add('wdm-dimmed');
          }
        }, 4500);
      };
      nextAnchor.addEventListener('mousemove', wakeBar, { passive: true });
      nextAnchor.addEventListener('mouseenter', wakeBar, { passive: true });
    }

    const labelEl = activeHooker.querySelector('.wdm-hooker-label');
    if (labelEl && !labelEl.textContent.startsWith('Downloading') && !labelEl.textContent.startsWith('\u2713') && !labelEl.textContent.startsWith('Opening')) {
      labelEl.textContent = barLabelText;
    }

    activeHooker.style.display = 'block';
    updatePortalPosition();

    const effectiveUrl = resolveEffectiveMediaUrl();
    if (forceRefresh || anchorChanged || effectiveUrl !== currentTargetUrl) {
      triggerStreamSniffer(forceRefresh || effectiveUrl !== currentTargetUrl);
    }
  }

  function updatePortalPosition() {
    if (!activeHooker || activeHooker.style.display === 'none') return;
    if (!trackedAnchorEl || !trackedAnchorEl.isConnected) {
      if (!activeHooker.classList.contains('open')) {
        activeHooker.style.display = 'none';
      }
      return;
    }

    const rect = trackedAnchorEl.getBoundingClientRect();
    if (rect.width < 100 || rect.height < 80 || rect.bottom < 60 || rect.top > window.innerHeight - 40) {
      if (!activeHooker.classList.contains('open')) {
        activeHooker.style.display = 'none';
      }
      return;
    }

    // On Instagram Stories/Highlights, place the bar cleanly below the story progress segments
    // and Mute/Pause/... header controls (62px down from top of story card) so neither UI overlaps!
    const isIgStory = isInstagram && window.location.pathname.startsWith('/stories/');
    const topOffset = isIgStory ? 62 : 12;
    const rightOffset = isIgStory ? 12 : 16;

    const computedTop = Math.max(10, Math.min(window.innerHeight - 48, Math.round(rect.top + topOffset)));
    const computedRight = Math.max(10, Math.min(window.innerWidth - 190, Math.round(window.innerWidth - rect.right + rightOffset)));

    activeHooker.style.top = `${computedTop}px`;
    activeHooker.style.right = `${computedRight}px`;
    activeHooker.style.left = 'auto';
  }

  function showLoadingSpinner() {
    if (!activeHooker) return;
    const menuBody = activeHooker.querySelector('.wdm-menu-body');
    if (!menuBody) return;
    menuBody.textContent = '';

    const wrap = document.createElement('div');
    wrap.className = 'wdm-loading-streams';
    const spinner = document.createElement('div');
    spinner.className = 'wdm-spinner';
    const txt = document.createElement('span');
    txt.textContent = 'Sniffing streams & slides...';
    wrap.appendChild(spinner);
    wrap.appendChild(txt);
    menuBody.appendChild(wrap);
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
              resolve(response || { success: false, message: 'No response from extension' });
            }
          }
        });

        if (res && typeof res.then === 'function') {
          res.then((response) => {
            if (!settled) {
              settled = true;
              resolve(response || { success: false, message: 'No response from extension' });
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
    const targetUrl = resolveEffectiveMediaUrl();
    if (!force && targetUrl === currentTargetUrl && (cachedStreams || isSniffing)) return;

    // If only the #wdm-slide=K hash changed on the same Instagram Story Highlight/Carousel,
    // re-highlight the active slide immediately without even needing a round-trip if already cached!
    const prevBase = currentTargetUrl.split('#')[0];
    const nextBase = targetUrl.split('#')[0];
    currentTargetUrl = targetUrl;

    if (!force && prevBase === nextBase && cachedStreams && Array.isArray(cachedStreams.formats) && cachedStreams.formats.length > 1 && currentSlideIndex) {
      reorderInstagramFormatsForSlide(cachedStreams, currentSlideIndex);
      updateHookerUI(cachedStreams);
      return;
    }

    if (force) cachedStreams = null;
    isSniffing = true;

    if (activeHooker) {
      const countBadge = activeHooker.querySelector('.wdm-file-count');
      if (countBadge) countBadge.style.display = 'none';
      if (activeHooker.classList.contains('open')) {
        showLoadingSpinner();
      }
    }

    sendExtensionMessage({
      action: 'sniff_media',
      url: targetUrl
    }).then((res) => {
      isSniffing = false;
      if (!activeHooker) return;

      if (res && res.success && res.data && res.data.formats && res.data.formats.length > 0) {
        cachedStreams = res.data;
        updateHookerUI(res.data);
      } else {
        const errorMsg = res?.message || res?.error || 'Could not inspect stream';
        fallbackDomMediaDetection(errorMsg);
      }
    });
  }

  function reorderInstagramFormatsForSlide(data, slideNum) {
    if (!data || !Array.isArray(data.formats)) return;
    const targetFid = `ig_slide_${slideNum}`;
    const targetFmt = data.formats.find(f => f.formatId === targetFid);
    if (!targetFmt) return;

    // Clean previous "Current" star label and promote the newly active slide to top
    data.formats.forEach((f) => {
      const m = /^ig_slide_(\d+)$/.exec(f.formatId || '');
      if (m) {
        const num = m[1];
        f.resolution = `Slide #${num} \u2022 ${f.isImage ? 'Photo' : 'Video'}`;
      }
    });

    targetFmt.resolution = `\u2605 Current (#${slideNum})`;
    const rest = data.formats.filter(f => f !== targetFmt);
    data.formats = [targetFmt, ...rest];
  }

  function pickHighestResFromImg(img) {
    if (!img) return null;
    const srcset = img.getAttribute('srcset');
    if (srcset) {
      let bestUrl = null;
      let bestW = -1;
      srcset.split(',').forEach((entry) => {
        const parts = entry.trim().split(/\s+/);
        if (parts.length >= 1 && parts[0].startsWith('http')) {
          const w = parts[1] ? parseInt(parts[1], 10) || 0 : 0;
          if (w >= bestW) {
            bestW = w;
            bestUrl = parts[0];
          }
        }
      });
      if (bestUrl) return bestUrl;
    }
    return (img.currentSrc || img.src || '').startsWith('http') ? (img.currentSrc || img.src) : null;
  }

  function fallbackDomMediaDetection(errorMsg = '') {
    // 1. Direct HTML5 <video src="https://...">
    const video = (trackedAnchorEl && trackedAnchorEl.tagName === 'VIDEO')
      ? trackedAnchorEl
      : (trackedAnchorEl ? trackedAnchorEl.querySelector('video') : document.querySelector('video'));
    const directVideoSrc = video ? (video.currentSrc || video.src) : null;

    if (directVideoSrc && directVideoSrc.startsWith('http')) {
      const fallbackData = {
        title: document.title || 'Web Video',
        formats: [
          { formatId: 'direct_video', directUrl: directVideoSrc, resolution: 'Direct Stream', extension: 'mp4', fileSizeBytes: null, isAudioOnly: false }
        ]
      };
      cachedStreams = fallbackData;
      updateHookerUI(fallbackData);
      return;
    }

    // 2. Direct Instagram Photo Story / Photo Post fallback from live DOM <img>
    if (isInstagram && trackedAnchorEl) {
      const img = trackedAnchorEl.tagName === 'IMG' ? trackedAnchorEl : trackedAnchorEl.querySelector('img');
      const bestImgUrl = pickHighestResFromImg(img);
      if (bestImgUrl && !video) {
        const fallbackPhoto = {
          title: document.title || 'Instagram Photo',
          formats: [
            { formatId: 'direct_photo', directUrl: bestImgUrl, resolution: 'HD Photo', extension: 'jpg', fileSizeBytes: null, isAudioOnly: false, isImage: true }
          ]
        };
        cachedStreams = fallbackPhoto;
        updateHookerUI(fallbackPhoto);
        return;
      }
    }

    if (!activeHooker) return;
    const menuBody = activeHooker.querySelector('.wdm-menu-body');
    if (!menuBody) return;

    menuBody.textContent = '';
    const wrap = document.createElement('div');
    wrap.style.cssText = 'padding: 14px 10px; text-align: center; color: #94a3b8; font-size: 11px;';

    const readyText = document.createElement('span');
    readyText.textContent = 'Ready to download with WDM.';
    wrap.appendChild(readyText);
    wrap.appendChild(document.createElement('br'));

    if (errorMsg) {
      const errDiv = document.createElement('div');
      errDiv.style.cssText = 'color: #f87171; font-size: 10px; margin: 6px 0; word-break: break-all;';
      errDiv.textContent = `(${errorMsg})`;
      wrap.appendChild(errDiv);
    }

    const retryBtn = document.createElement('button');
    retryBtn.className = 'wdm-retry-sniff-btn';
    retryBtn.style.cssText = 'margin-top: 8px; padding: 4px 12px; background: rgba(0,113,227,0.2); border: 1px solid rgba(0,113,227,0.5); color: #38bdf8; border-radius: 4px; cursor: pointer; font-size: 11px; font-weight: 500;';
    retryBtn.textContent = '\u21BB Retry Sniffing Formats';
    retryBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      showLoadingSpinner();
      triggerStreamSniffer(true);
    });
    wrap.appendChild(retryBtn);
    menuBody.appendChild(wrap);
  }

  function updateHookerUI(data) {
    if (!activeHooker) return;

    const formats = data.formats || [];
    const countBadge = activeHooker.querySelector('.wdm-file-count');
    if (countBadge) {
      if (formats.length > 0) {
        countBadge.textContent = `(${formats.length})`;
        countBadge.style.display = 'inline-block';
      } else {
        countBadge.style.display = 'none';
      }
    }

    const menuTitle = activeHooker.querySelector('.wdm-menu-title');
    if (menuTitle) {
      menuTitle.textContent = data.title || document.title || 'Media Stream';
      menuTitle.title = menuTitle.textContent;
    }

    const menuBody = activeHooker.querySelector('.wdm-menu-body');
    if (!menuBody) return;
    menuBody.textContent = '';

    const primaryFormats = formats.filter(f => !f.isAudioOnly);
    const audioFormats = formats.filter(f => f.isAudioOnly);

    if (primaryFormats.length > 0) {
      const groupLabel = document.createElement('div');
      groupLabel.className = 'wdm-menu-group-label';
      groupLabel.textContent = isInstagram ? 'Stories / Posts / Streams' : 'Video Streams';
      menuBody.appendChild(groupLabel);

      primaryFormats.forEach((f) => {
        menuBody.appendChild(createFormatMenuItem(f, data.title));
      });
    }

    if (audioFormats.length > 0) {
      const groupLabel = document.createElement('div');
      groupLabel.className = 'wdm-menu-group-label';
      groupLabel.textContent = 'Audio Streams';
      menuBody.appendChild(groupLabel);

      audioFormats.forEach((f) => {
        menuBody.appendChild(createFormatMenuItem(f, data.title));
      });
    }
  }

  function createFormatMenuItem(f, mediaTitle) {
    const item = document.createElement('div');
    item.className = 'wdm-stream-item';

    let tagClass = 'wdm-tag-video';
    let tagText = f.extension ? f.extension.toUpperCase() : 'VIDEO';
    if (f.isAudioOnly) {
      tagClass = 'wdm-tag-audio';
      tagText = 'AUDIO';
    } else if (f.formatId === 'ig_all_slides') {
      tagClass = 'wdm-tag-batch';
      tagText = 'BATCH';
    } else if (f.isImage) {
      tagClass = 'wdm-tag-photo';
      tagText = 'PHOTO';
    }

    const sizeText = f.fileSizeBytes ? formatBytes(f.fileSizeBytes) : (f.note || '');

    const left = document.createElement('div');
    left.className = 'wdm-stream-left';

    const tag = document.createElement('span');
    tag.className = `wdm-stream-tag ${tagClass}`;
    tag.textContent = tagText;

    const res = document.createElement('span');
    res.className = 'wdm-stream-res';
    res.textContent = f.resolution || '';

    left.appendChild(tag);
    left.appendChild(res);

    const size = document.createElement('span');
    size.className = 'wdm-stream-size';
    size.textContent = sizeText;

    item.appendChild(left);
    item.appendChild(size);

    item.addEventListener('click', (e) => {
      e.stopPropagation();
      e.preventDefault();
      if (activeHooker) activeHooker.classList.remove('open');
      startDownload({
        formatId: f.formatId,
        directUrl: f.directUrl || null,
        isAudioOnly: !!f.isAudioOnly,
        title: mediaTitle,
        ext: f.extension,
        resolution: f.resolution,
        fileSizeBytes: f.fileSizeBytes
      });
    });

    return item;
  }

  function startDownload(opts = {}) {
    const targetUrl = resolveEffectiveMediaUrl();
    const rawTitle = opts.title || (cachedStreams && cachedStreams.title) || document.title || 'Media';
    const safeTitle = rawTitle.replace(/[/\\?%*:|"<>]/g, '_').trim();
    const finalExt = opts.ext || (opts.isAudioOnly ? 'mp3' : 'mp4');
    const slideTag = (opts.formatId && /^ig_slide_\d+$/.test(opts.formatId) && cachedStreams && cachedStreams.formats && cachedStreams.formats.length > 3)
      ? `_${opts.formatId.replace('ig_', '')}`
      : '';
    const fileName = opts.formatId === 'ig_all_slides'
      ? `${safeTitle}_All_Slides`
      : `${safeTitle}${slideTag}.${finalExt}`;

    showToast(`\u2713 Opening WDM Quick Download: ${fileName}`);

    if (activeHooker) {
      const label = activeHooker.querySelector('.wdm-hooker-label');
      if (label) {
        label.textContent = 'Opening WDM Popup...';
        setTimeout(() => {
          if (label) label.textContent = isInstagram ? 'Download with WDM' : 'Download this video';
        }, 2500);
      }
    }

    sendExtensionMessage({
      action: 'download_media',
      url: targetUrl,
      formatId: opts.formatId || null,
      directUrl: opts.directUrl || null,
      title: safeTitle,
      fileName: fileName,
      isAudioOnly: !!opts.isAudioOnly,
      resolution: opts.resolution || (opts.isAudioOnly ? 'Audio Stream' : 'Best Quality'),
      extension: finalExt,
      fileSizeBytes: opts.fileSizeBytes || null
    });
  }

  function showToast(message) {
    const existing = document.querySelector('.wdm-extension-toast');
    if (existing) existing.remove();

    const toast = document.createElement('div');
    toast.className = 'wdm-extension-toast';
    toast.textContent = message;
    (document.fullscreenElement || document.body).appendChild(toast);

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

  if (typeof chrome !== 'undefined' && chrome.runtime && chrome.runtime.onMessage) {
    chrome.runtime.onMessage.addListener((msg) => {
      if (!msg || !activeHooker) return;
      const label = activeHooker.querySelector('.wdm-hooker-label');
      if (!label) return;

      if (msg.type === 'download_progress' || msg.type === 'media_download_progress') {
        const data = msg.data || msg;
        const pct = Math.round(data.percentage || 0);
        let speedText = '';
        const statusStr = data.status || data.statusText || '';
        if (statusStr) {
          const m = statusStr.match(/at\s+(\S+\/s)/i);
          if (m) speedText = ` (${m[1]})`;
        }
        label.textContent = `Downloading ${pct}%${speedText}`;
      } else if (msg.type === 'download_completed' || msg.type === 'media_download_completed') {
        label.textContent = '\u2713 Download Complete!';
        setTimeout(() => {
          label.textContent = isInstagram ? 'Download with WDM' : 'Download this video';
        }, 5000);
        showToast('\u2713 Media downloaded successfully!');
      } else if (msg.type === 'download_failed' || msg.type === 'media_download_failed') {
        label.textContent = isInstagram ? 'Download with WDM' : 'Download this video';
      }
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
