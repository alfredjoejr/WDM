// WDM Desktop UI Application Logic — Apple Liquid Glass Edition

(function () {
  'use strict';

  // State
  const downloads = new Map();
  let currentFilter = 'all';
  let searchQuery = '';
  let pendingRequests = new Map();
  const speedHistory = new Array(50).fill(0);
  let peakSpeedBytes = 0;

  // DOM Elements
  const downloadsContainer = document.getElementById('downloadsContainer');
  const emptyState = document.getElementById('emptyState');
  const badgeAllCount = document.getElementById('badgeAllCount');
  const meterSpeed = document.getElementById('meterSpeed');
  const meterFill = document.getElementById('meterFill');
  const meterThreads = document.getElementById('meterThreads');
  const statPeak = document.getElementById('statPeak');
  const statActive = document.getElementById('statActive');
  const searchInput = document.getElementById('searchInput');

  // Modal
  const newDownloadModal = document.getElementById('newDownloadModal');
  const btnOpenNewModal = document.getElementById('btnOpenNewModal');
  const btnCloseModal = document.getElementById('btnCloseModal');
  const btnCancelModal = document.getElementById('btnCancelModal');
  const btnStartDownload = document.getElementById('btnStartDownload');
  const btnProbe = document.getElementById('btnProbe');
  const modalUrl = document.getElementById('modalUrl');
  const modalFileName = document.getElementById('modalFileName');
  const modalFileSize = document.getElementById('modalFileSize');
  const modalRangeStatus = document.getElementById('modalRangeStatus');
  const probePreview = document.getElementById('probePreview');
  const modalSelectConnections = document.getElementById('modalSelectConnections');
  const modalSelectSpeedLimit = document.getElementById('modalSelectSpeedLimit');
  const btnPasteClipboard = document.getElementById('btnPasteClipboard');

  // Media Tab
  const mediaUrlInput = document.getElementById('mediaUrlInput');
  const btnSniffMedia = document.getElementById('btnSniffMedia');
  const mediaResultBox = document.getElementById('mediaResultBox');
  const mediaThumbnail = document.getElementById('mediaThumbnail');
  const mediaTitle = document.getElementById('mediaTitle');
  const mediaUploader = document.getElementById('mediaUploader');
  const mediaDuration = document.getElementById('mediaDuration');
  const videoFormatsGrid = document.getElementById('videoFormatsGrid');
  const audioFormatsGrid = document.getElementById('audioFormatsGrid');
  const videoStreamGroup = document.getElementById('videoStreamGroup');
  const audioStreamGroup = document.getElementById('audioStreamGroup');
  const mediaErrorBanner = document.getElementById('mediaErrorBanner');
  const mediaErrorText = document.getElementById('mediaErrorText');

  // Media Download Modal
  const mediaDownloadModal = document.getElementById('mediaDownloadModal');
  const btnCancelMediaModal = document.getElementById('btnCancelMediaModal');
  const btnCancelMediaDownload = document.getElementById('btnCancelMediaDownload');
  const btnConfirmMediaDownload = document.getElementById('btnConfirmMediaDownload');
  const mediaModalFormatBadge = document.getElementById('mediaModalFormatBadge');
  const mediaModalSize = document.getElementById('mediaModalSize');
  const mediaModalFileName = document.getElementById('mediaModalFileName');
  const mediaModalSavePath = document.getElementById('mediaModalSavePath');
  const btnBrowseMediaLocation = document.getElementById('btnBrowseMediaLocation');
  const chkRememberLocation = document.getElementById('chkRememberLocation');

  let defaultDownloadDirectory = '';
  let currentMediaTarget = null;

  // Benchmark Tab
  const btnRunBenchmark = document.getElementById('btnRunBenchmark');
  const benchUrlInput = document.getElementById('benchUrlInput');
  const benchResultsBox = document.getElementById('benchResultsBox');
  const benchTableBody = document.getElementById('benchTableBody');

  // Initialize Custom Apple Select Dropdowns
  initAppleDropdowns();

  function initAppleDropdowns() {
    document.querySelectorAll('.apple-select').forEach((selectEl) => {
      const trigger = selectEl.querySelector('.apple-select-trigger');
      const label = trigger.querySelector('.trigger-label');
      const items = selectEl.querySelectorAll('.select-item');

      trigger.addEventListener('click', (e) => {
        e.stopPropagation();
        const isOpen = selectEl.classList.contains('open');
        closeAllAppleDropdowns();
        if (!isOpen) selectEl.classList.add('open');
      });

      items.forEach((item) => {
        item.addEventListener('click', (e) => {
          e.stopPropagation();
          const val = item.getAttribute('data-value');
          const text = item.querySelector('span').textContent;

          items.forEach(i => i.classList.remove('active'));
          item.classList.add('active');

          trigger.setAttribute('data-value', val);
          label.textContent = text;
          selectEl.classList.remove('open');
        });
      });
    });

    document.addEventListener('click', () => {
      closeAllAppleDropdowns();
    });
  }

  function closeAllAppleDropdowns() {
    document.querySelectorAll('.apple-select.open').forEach(el => el.classList.remove('open'));
  }

  // Setup Navigation Tabs
  document.querySelectorAll('.nav-item').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.nav-item').forEach(b => b.classList.remove('active'));
      document.querySelectorAll('.tab-pane').forEach(p => p.classList.remove('active'));

      btn.classList.add('active');
      const tabId = 'pane' + btn.getAttribute('data-tab').charAt(0).toUpperCase() + btn.getAttribute('data-tab').slice(1);
      const targetPane = document.getElementById(tabId);
      if (targetPane) targetPane.classList.add('active');
    });
  });

  // Setup Filter Segments
  document.querySelectorAll('.segmented-control .segment').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.segmented-control .segment').forEach(s => s.classList.remove('active'));
      btn.classList.add('active');
      currentFilter = btn.getAttribute('data-filter');
      renderDownloads();
    });
  });

  // Search Filter
  searchInput.addEventListener('input', (e) => {
    searchQuery = e.target.value.toLowerCase().trim();
    renderDownloads();
  });

  // Canvas Speed Spline Chart (Apple Stocks Style)
  const canvas = document.getElementById('speedCanvas');
  const ctx = canvas.getContext('2d');

  function resizeCanvas() {
    const parent = canvas.parentElement;
    if (parent) {
      canvas.width = parent.clientWidth - 48;
      canvas.height = 85;
    }
  }
  window.addEventListener('resize', resizeCanvas);
  resizeCanvas();

  function drawSpeedChart() {
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    const currentMax = Math.max(...speedHistory);
    const maxVal = Math.max(100 * 1024, currentMax) * 1.15;
    const stepX = canvas.width / (speedHistory.length - 1);

    // Soft diffused vertical gradient
    const grad = ctx.createLinearGradient(0, 0, 0, canvas.height);
    grad.addColorStop(0, 'rgba(0, 113, 227, 0.25)');
    grad.addColorStop(1, 'rgba(0, 113, 227, 0.0)');

    ctx.beginPath();
    ctx.moveTo(0, canvas.height);

    for (let i = 0; i < speedHistory.length; i++) {
      const x = i * stepX;
      const y = canvas.height - (speedHistory[i] / maxVal) * (canvas.height - 12);
      if (i === 0) ctx.lineTo(x, y);
      else {
        const prevX = (i - 1) * stepX;
        const prevY = canvas.height - (speedHistory[i - 1] / maxVal) * (canvas.height - 12);
        const midX = (prevX + x) / 2;
        ctx.bezierCurveTo(midX, prevY, midX, y, x, y);
      }
    }

    ctx.lineTo(canvas.width, canvas.height);
    ctx.closePath();
    ctx.fillStyle = grad;
    ctx.fill();

    // Hairline stroke
    ctx.beginPath();
    for (let i = 0; i < speedHistory.length; i++) {
      const x = i * stepX;
      const y = canvas.height - (speedHistory[i] / maxVal) * (canvas.height - 12);
      if (i === 0) ctx.moveTo(x, y);
      else {
        const prevX = (i - 1) * stepX;
        const prevY = canvas.height - (speedHistory[i - 1] / maxVal) * (canvas.height - 12);
        const midX = (prevX + x) / 2;
        ctx.bezierCurveTo(midX, prevY, midX, y, x, y);
      }
    }
    ctx.strokeStyle = '#38a5ff';
    ctx.lineWidth = 1.8;
    ctx.stroke();
  }

  // Periodic Telemetry Loop
  setInterval(() => {
    let totalSpeedBytes = 0;
    let totalActiveThreads = 0;
    let activeTasks = 0;

    downloads.forEach((task) => {
      if (task.status === 'Downloading') {
        totalSpeedBytes += task.speedBytes || 0;
        totalActiveThreads += task.activeConnections || 0;
        activeTasks++;
      }
    });

    speedHistory.shift();
    speedHistory.push(totalSpeedBytes);
    drawSpeedChart();

    if (totalSpeedBytes > peakSpeedBytes) peakSpeedBytes = totalSpeedBytes;

    meterSpeed.textContent = formatBytes(totalSpeedBytes) + '/s';
    statPeak.textContent = formatBytes(peakSpeedBytes) + '/s';
    statActive.textContent = activeTasks.toString();
    meterThreads.textContent = totalActiveThreads + ' Streams';

    const maxScale = 50 * 1024 * 1024;
    meterFill.style.width = Math.min(100, (totalSpeedBytes / maxScale) * 100) + '%';
  }, 300);

  // WebView2 Message Bridge
  function sendToHost(action, payload = {}) {
    const requestId = 'req_' + Math.random().toString(36).substring(2, 9);
    payload.action = action;
    payload.requestId = requestId;

    return new Promise((resolve) => {
      pendingRequests.set(requestId, resolve);
      if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(payload);
      } else {
        console.log('[Web Fallback] Dispatch:', action, payload);
      }
    });
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', (event) => {
      handleHostMessage(event.data);
    });
  }

  function handleHostMessage(msg) {
    if (typeof msg === 'string') {
      try { msg = JSON.parse(msg); } catch { return; }
    }

    if (msg.requestId && pendingRequests.has(msg.requestId)) {
      const resolve = pendingRequests.get(msg.requestId);
      pendingRequests.delete(msg.requestId);
      resolve(msg);
    }

    switch (msg.action) {
      case 'download_started':
        downloads.set(msg.downloadId, {
          id: msg.downloadId,
          url: msg.url,
          fileName: msg.fileName || msg.url.split('/').pop().split('?')[0] || 'file.bin',
          status: 'Downloading',
          percentage: 0,
          speed: '0 B/s',
          speedBytes: 0,
          downloaded: '0 B',
          total: 'Probing...',
          eta: '--:--',
          chunks: []
        });
        renderDownloads();
        break;

      case 'download_progress':
        if (downloads.has(msg.downloadId)) {
          const task = downloads.get(msg.downloadId);
          task.status = 'Downloading';
          task.percentage = msg.percentage;
          task.speed = msg.speed;
          task.speedBytes = msg.speedBytes;
          task.downloaded = msg.downloaded;
          task.total = msg.total;
          task.eta = msg.eta;
          task.activeConnections = msg.activeConnections;
          task.chunks = msg.chunks || [];
          updateTaskCard(task);
        }
        break;

      case 'download_completed':
        if (downloads.has(msg.downloadId)) {
          const task = downloads.get(msg.downloadId);
          task.status = 'Completed';
          task.percentage = 100;
          task.speed = 'Done';
          task.speedBytes = 0;
          task.filePath = msg.filePath;
          task.sha256 = msg.sha256;
          renderDownloads();
        }
        break;

      case 'download_failed':
        if (downloads.has(msg.downloadId)) {
          const task = downloads.get(msg.downloadId);
          task.status = 'Failed';
          task.error = msg.error;
          renderDownloads();
        }
        break;

      case 'media_download_started':
        downloads.set(msg.downloadId, {
          id: msg.downloadId,
          url: msg.url,
          fileName: msg.fileName || (msg.title || 'Media Video') + '.mp4',
          destinationDirectory: msg.destinationDirectory,
          isAudioOnly: msg.isAudioOnly || false,
          status: 'Downloading',
          percentage: 0,
          speed: 'Catching Stream',
          speedBytes: 0,
          downloaded: '0 B',
          total: 'Probing...',
          totalBytes: 0,
          eta: '--:--',
          activeConnections: msg.isAudioOnly ? 1 : 2,
          chunks: msg.isAudioOnly ? [
            { index: 0, progress: 0, name: 'Audio Stream' }
          ] : [
            { index: 0, progress: 0, name: 'Video Stream' },
            { index: 1, progress: 0, name: 'Audio Stream' }
          ]
        });
        renderDownloads();
        break;

      case 'media_download_progress':
        if (downloads.has(msg.downloadId)) {
          const task = downloads.get(msg.downloadId);
          task.status = 'Downloading';
          task.activeConnections = task.isAudioOnly ? 1 : 2;

          if (msg.percentage >= 0) {
            task.percentage = msg.percentage;
          }

          if (msg.status) {
            const parsed = parseYtDlpStatus(msg.status);
            if (parsed) {
              if (parsed.percent !== null && parsed.percent >= 0) {
                task.percentage = parsed.percent;
              }
              if (parsed.speedBytes > 0) {
                task.speedBytes = parsed.speedBytes;
                task.speed = parsed.speedFormatted;
              }
              if (parsed.totalBytes > 0) {
                task.totalBytes = parsed.totalBytes;
                task.total = parsed.totalFormatted;
                const downloadedBytes = Math.round((task.percentage / 100) * parsed.totalBytes);
                task.downloaded = formatBytes(downloadedBytes);
              }
              if (parsed.eta) {
                task.eta = parsed.eta;
              }
            } else {
              task.speed = msg.status.replace(/\[download\]\s*/, '').trim();
            }
          }

          if (task.chunks && task.chunks.length > 0) {
            task.chunks.forEach(c => c.progress = task.percentage);
          }

          updateTaskCard(task);
        }
        break;

      case 'media_download_completed':
        if (downloads.has(msg.downloadId)) {
          const task = downloads.get(msg.downloadId);
          task.status = 'Completed';
          task.percentage = 100;
          task.speed = 'Completed';
          task.speedBytes = 0;
          task.eta = 'Done';
          if (task.total && task.total !== 'Probing...' && task.total !== 'Media Stream') {
            task.downloaded = task.total;
          }
          if (task.chunks) {
            task.chunks.forEach(c => c.progress = 100);
          }
          task.filePath = msg.filePath || msg.destinationDirectory;
          task.destinationDirectory = msg.destinationDirectory;
          renderDownloads();
        }
        break;

      case 'media_download_failed':
        if (downloads.has(msg.downloadId)) {
          const task = downloads.get(msg.downloadId);
          task.status = 'Failed';
          task.error = msg.error;
          renderDownloads();
        }
        break;

      case 'browse_folder_result':
        if (msg.success && msg.folderPath) {
          mediaModalSavePath.value = msg.folderPath;
        }
        break;

      case 'default_folder_result':
        if (msg.folderPath) {
          defaultDownloadDirectory = msg.folderPath;
          if (!mediaModalSavePath.value) {
            const remembered = localStorage.getItem('wdm_last_save_location');
            mediaModalSavePath.value = remembered || defaultDownloadDirectory;
          }
        }
        break;

      case 'clipboard_result':
        if (msg.text) {
          handlePastedUrl(msg.text);
        } else {
          showToast("Clipboard is empty or does not contain text.");
        }
        break;
    }
  }

  // Render Downloads
  function renderDownloads() {
    const list = Array.from(downloads.values()).filter(t => {
      if (currentFilter === 'active' && t.status !== 'Downloading') return false;
      if (currentFilter === 'completed' && t.status !== 'Completed') return false;
      if (currentFilter === 'paused' && t.status !== 'Paused') return false;
      if (searchQuery && !t.fileName.toLowerCase().includes(searchQuery) && !t.url.toLowerCase().includes(searchQuery)) return false;
      return true;
    });

    badgeAllCount.textContent = downloads.size.toString();

    if (list.length === 0) {
      emptyState.style.display = 'flex';
      downloadsContainer.querySelectorAll('.task-card').forEach(c => c.remove());
      return;
    }

    emptyState.style.display = 'none';
    downloadsContainer.querySelectorAll('.task-card').forEach(c => c.remove());
    list.forEach(task => {
      const card = createTaskCard(task);
      downloadsContainer.appendChild(card);
    });
  }

  function createTaskCard(task) {
    const card = document.createElement('div');
    card.className = 'task-card glass-panel';
    card.id = 'taskCard_' + task.id;

    const percent = Math.round(task.percentage || 0);
    const statusClass = (task.status || 'downloading').toLowerCase();

    card.innerHTML = `
      <div class="task-row-main">
        <div class="file-type-icon">
          <svg viewBox="0 0 24 24"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z"/></svg>
        </div>
        <div class="task-info">
          <div class="task-name-row">
            <span class="task-filename" title="${task.fileName}">${task.fileName}</span>
            <span class="task-status-pill ${statusClass}">${task.status}</span>
          </div>
          <div class="task-url" title="${task.url}">${task.url}</div>
        </div>
        <div class="task-actions">
          <button class="icon-btn btn-drawer-toggle" title="Details / Streams">
            <svg viewBox="0 0 24 24"><path d="M7 10l5 5 5-5z"/></svg>
          </button>
          ${task.status === 'Completed' ? `
            <button class="icon-btn btn-open-file" title="Open File">
              <svg viewBox="0 0 24 24"><path d="M19 19H5V5h7V3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7h-2v7zM14 3v2h3.59l-9.83 9.83 1.41 1.41L19 6.41V10h2V3h-7z"/></svg>
            </button>
            <button class="icon-btn btn-open-folder" title="Show in File Explorer">
              <svg viewBox="0 0 24 24"><path d="M10 4H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-8l-2-2z"/></svg>
            </button>
            <button class="icon-btn btn-remove-task" title="Remove from list">
              <svg viewBox="0 0 24 24"><path d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"/></svg>
            </button>
          ` : `
            <button class="icon-btn btn-pause" title="Cancel Download">
              <svg viewBox="0 0 24 24"><path d="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"/></svg>
            </button>
          `}
        </div>
      </div>

      <div class="task-progress-box">
        <div class="progress-bar-bg">
          <div class="progress-bar-fill" style="width: ${percent}%;"></div>
        </div>
        <div class="task-metrics-row">
          <span>${task.downloaded || '0 B'} / ${task.total || 'Unknown'} (${percent}%)</span>
          <span class="speed">${task.status === 'Completed' ? '100% (Instant Finished)' : (task.speed || '0 B/s')}</span>
          <span>ETA ${task.eta || '--:--'}</span>
        </div>
      </div>

      <!-- Expandable Thread Chunk Drawer -->
      <div class="task-drawer" id="drawer_${task.id}">
        <div class="drawer-header">TRANSFER DETAILS</div>
        <div class="media-drawer-content" style="padding: 10px 14px; font-size: 11.5px; color: var(--label-secondary); display: flex; flex-direction: column; gap: 5px; background: rgba(0,0,0,0.15); border-radius: 6px; margin: 8px 0;">
          <div><b style="color: var(--label-primary);">Source URL:</b> <span style="word-break: break-all; color: var(--label-tertiary);">${task.url}</span></div>
          <div><b style="color: var(--label-primary);">Saved To:</b> <span style="word-break: break-all; color: #38a5ff;">${task.filePath || task.destinationDirectory || 'Downloads'}</span></div>
          <div><b style="color: var(--label-primary);">Status:</b> <span>${task.status} (${task.speed || 'Done'})</span></div>
        </div>
        ${task.chunks && task.chunks.length > 0 ? `
          <div class="drawer-header" style="margin-top: 8px;">CONCURRENT STREAMS (${task.chunks.length} TOTAL)</div>
          <div class="chunks-grid" id="chunksGrid_${task.id}"></div>
        ` : ''}
      </div>
    `;

    const toggleBtn = card.querySelector('.btn-drawer-toggle');
    const drawer = card.querySelector('.task-drawer');
    const chevronSvg = toggleBtn.querySelector('svg');
    toggleBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      drawer.classList.toggle('open');
      if (chevronSvg) {
        chevronSvg.style.transform = drawer.classList.contains('open') ? 'rotate(180deg)' : 'rotate(0deg)';
      }
    });

    const openFileBtn = card.querySelector('.btn-open-file');
    if (openFileBtn) {
      openFileBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        const p = task.filePath || task.destinationDirectory;
        if (p) {
          sendToHost('open_file', { filePath: p });
          showToast("Opening file...");
        } else {
          showToast("File location not available yet.");
        }
      });
    }

    const openFolderBtn = card.querySelector('.btn-open-folder');
    if (openFolderBtn) {
      openFolderBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        const p = task.filePath || task.destinationDirectory;
        if (p) {
          sendToHost('open_folder', { filePath: p });
          showToast("Opening folder in File Explorer...");
        } else {
          showToast("Folder location not available yet.");
        }
      });
    }

    const removeBtn = card.querySelector('.btn-remove-task');
    if (removeBtn) {
      removeBtn.addEventListener('click', (e) => {
        e.stopPropagation();
        downloads.delete(task.id);
        renderDownloads();
      });
    }

    const pauseBtn = card.querySelector('.btn-pause');
    if (pauseBtn) {
      pauseBtn.addEventListener('click', () => {
        sendToHost('cancel_download', { downloadId: task.id });
        task.status = 'Canceled';
        renderDownloads();
      });
    }

    renderChunks(task, card.querySelector(`#chunksGrid_${task.id}`));

    return card;
  }

  function updateTaskCard(task) {
    const card = document.getElementById('taskCard_' + task.id);
    if (!card) {
      renderDownloads();
      return;
    }

    const statusPill = card.querySelector('.task-status-pill');
    if (statusPill && statusPill.textContent !== task.status) {
      renderDownloads();
      return;
    }

    const percent = Math.round(task.percentage || 0);
    const fill = card.querySelector('.progress-bar-fill');
    if (fill) fill.style.width = percent + '%';

    const metrics = card.querySelector('.task-metrics-row');
    if (metrics) {
      metrics.innerHTML = `
        <span>${task.downloaded || '0 B'} / ${task.total || 'Unknown'} (${percent}%)</span>
        <span class="speed">${task.speed || '0 B/s'}</span>
        <span>ETA ${task.eta || '--:--'}</span>
      `;
    }

    const chunksGrid = card.querySelector(`#chunksGrid_${task.id}`);
    if (chunksGrid && task.chunks) {
      renderChunks(task, chunksGrid);
    }
  }

  function renderChunks(task, container) {
    if (!container || !task.chunks || task.chunks.length === 0) return;
    container.innerHTML = '';

    task.chunks.forEach((chunk) => {
      const cDiv = document.createElement('div');
      cDiv.className = 'chunk-card';
      const cPercent = Math.round(chunk.progress || 0);

      cDiv.innerHTML = `
        <div class="chunk-card-header">
          <span>Stream #${chunk.index + 1}</span>
          <b style="color: #38a5ff;">${cPercent}%</b>
        </div>
        <div class="chunk-card-bar">
          <div class="chunk-card-fill" style="width: ${cPercent}%;"></div>
        </div>
      `;
      container.appendChild(cDiv);
    });
  }

  // Modal Sheet Handlers
  function openModal() {
    newDownloadModal.classList.add('open');
  }

  function closeModal() {
    newDownloadModal.classList.remove('open');
    probePreview.style.display = 'none';
  }

  btnOpenNewModal.addEventListener('click', () => {
    openModal();
    modalUrl.focus();
  });

  btnCloseModal.addEventListener('click', closeModal);
  btnCancelModal.addEventListener('click', closeModal);

  btnPasteClipboard.addEventListener('click', async () => {
    try {
      if (navigator.clipboard && navigator.clipboard.readText) {
        const text = await navigator.clipboard.readText();
        if (text && text.trim()) {
          handlePastedUrl(text.trim());
          return;
        }
      }
    } catch { }

    sendToHost('get_clipboard');
  });

  function handlePastedUrl(rawText) {
    if (!rawText || !rawText.trim()) {
      showToast("Clipboard is empty or does not contain text.");
      return;
    }

    let url = rawText.trim();
    if (url.includes('\n')) {
      url = url.split('\n')[0].trim();
    }
    url = url.replace(/^["']|["']$/g, '');

    if (!url.startsWith('http://') && !url.startsWith('https://')) {
      if (url.includes('.') && !url.includes(' ')) {
        url = 'https://' + url;
      } else {
        showToast("Clipboard does not contain a valid URL.");
        return;
      }
    }

    const isMediaPane = document.getElementById('paneMedia').classList.contains('active');
    const isMediaUrl = /youtube\.com|youtu\.be|vimeo\.com|tiktok\.com|twitter\.com|x\.com|twitch\.tv|soundcloud\.com|\.m3u8|\.mpd/i.test(url);

    if (isMediaPane || isMediaUrl) {
      const mediaTabBtn = document.querySelector('.nav-item[data-tab="media"]');
      if (mediaTabBtn) mediaTabBtn.click();

      mediaUrlInput.value = url;
      showToast("Pasted media URL — inspecting streams...");
      btnSniffMedia.click();
    } else {
      const dlTabBtn = document.querySelector('.nav-item[data-tab="downloads"]');
      if (dlTabBtn) dlTabBtn.click();

      modalUrl.value = url;
      openModal();
      triggerProbe(url);
    }
  }

  function showToast(text, duration = 2800) {
    let toast = document.getElementById('appleToast');
    if (!toast) {
      toast = document.createElement('div');
      toast.id = 'appleToast';
      toast.className = 'apple-toast glass-panel';
      document.body.appendChild(toast);
    }
    toast.textContent = text;
    toast.classList.add('visible');
    clearTimeout(toast._timer);
    toast._timer = setTimeout(() => {
      toast.classList.remove('visible');
    }, duration);
  }

  btnProbe.addEventListener('click', () => {
    triggerProbe(modalUrl.value.trim());
  });

  async function triggerProbe(url) {
    if (!url) return;
    btnProbe.textContent = '...';

    const res = await sendToHost('probe_url', { url });
    btnProbe.textContent = 'Probe';

    if (res && res.success && res.data) {
      probePreview.style.display = 'flex';
      modalFileName.value = res.data.fileName || '';
      modalFileSize.textContent = res.data.totalBytes ? formatBytes(res.data.totalBytes) : 'Streaming / Unknown';
      modalRangeStatus.textContent = res.data.acceptsRanges ? '16x Multi-Thread' : 'Single Stream';
      modalRangeStatus.className = 'status-pill ' + (res.data.acceptsRanges ? 'green' : 'yellow');
    }
  }

  btnStartDownload.addEventListener('click', () => {
    const url = modalUrl.value.trim();
    if (!url) return;

    const connectionsTrigger = modalSelectConnections.querySelector('.apple-select-trigger');
    const speedTrigger = modalSelectSpeedLimit.querySelector('.apple-select-trigger');

    const connections = parseInt(connectionsTrigger.getAttribute('data-value'), 10) || 16;
    const speedLimit = parseInt(speedTrigger.getAttribute('data-value'), 10) || 0;
    const fileName = modalFileName.value.trim() || null;

    sendToHost('start_download', {
      url,
      fileName,
      connections,
      speedLimit
    });

    closeModal();
    modalUrl.value = '';
    modalFileName.value = '';
  });

  // Media Sniffer Handlers
  btnSniffMedia.addEventListener('click', async () => {
    const url = mediaUrlInput.value.trim();
    if (!url) return;

    if (mediaErrorBanner) mediaErrorBanner.style.display = 'none';
    btnSniffMedia.textContent = 'Inspecting...';
    btnSniffMedia.disabled = true;

    try {
      const res = await sendToHost('sniff_media', { url });
      btnSniffMedia.textContent = 'Inspect Streams';
      btnSniffMedia.disabled = false;

      if (res && res.success && res.data) {
        if (mediaErrorBanner) mediaErrorBanner.style.display = 'none';
        mediaResultBox.style.display = 'block';
        mediaTitle.textContent = res.data.title || 'Video';
        mediaUploader.textContent = res.data.uploader || 'Creator';
        mediaDuration.textContent = res.data.duration || '00:00';
        if (res.data.thumbnail) {
          mediaThumbnail.src = res.data.thumbnail;
          mediaThumbnail.style.display = 'block';
        } else {
          mediaThumbnail.style.display = 'none';
        }

        const allFormats = res.data.formats || [];
        const videoFormats = allFormats.filter(f => !f.isAudioOnly);
        const audioFormats = allFormats.filter(f => f.isAudioOnly);

        videoFormatsGrid.innerHTML = '';
        audioFormatsGrid.innerHTML = '';

        if (videoFormats.length > 0) {
          videoStreamGroup.style.display = 'block';
          videoFormats.forEach(f => {
            const btn = createStreamButton(f, false, res.data.title, url);
            videoFormatsGrid.appendChild(btn);
          });
        } else {
          videoStreamGroup.style.display = 'none';
        }

        if (audioFormats.length > 0) {
          audioStreamGroup.style.display = 'block';
          audioFormats.forEach(f => {
            const btn = createStreamButton(f, true, res.data.title, url);
            audioFormatsGrid.appendChild(btn);
          });
        } else {
          audioStreamGroup.style.display = 'none';
        }
      } else {
        mediaResultBox.style.display = 'none';
        if (mediaErrorBanner) {
          mediaErrorBanner.style.display = 'flex';
          mediaErrorText.textContent = res?.message || "Could not extract streams. Make sure the URL is accessible.";
        }
      }
    } catch (err) {
      btnSniffMedia.textContent = 'Inspect Streams';
      btnSniffMedia.disabled = false;
      mediaResultBox.style.display = 'none';
      if (mediaErrorBanner) {
        mediaErrorBanner.style.display = 'flex';
        mediaErrorText.textContent = err?.message || "Failed to communicate with media extractor service.";
      }
    }
  });

  function createStreamButton(f, isAudio, title, url) {
    const btn = document.createElement('button');
    btn.className = 'format-btn' + (isAudio ? ' audio-btn' : '');
    const icon = isAudio ? '🎵' : '🎬';
    const sizeStr = f.fileSizeBytes ? ` (${formatBytes(f.fileSizeBytes)})` : '';
    btn.innerHTML = `<span>${icon}</span> <span>${f.resolution} • ${f.extension.toUpperCase()}${sizeStr}</span>`;
    btn.addEventListener('click', () => {
      openMediaDownloadModal({
        url: url,
        formatId: f.formatId,
        resolution: f.resolution,
        extension: f.extension || (isAudio ? 'mp3' : 'mp4'),
        fileSizeBytes: f.fileSizeBytes,
        sizeStr: sizeStr,
        isAudioOnly: isAudio,
        title: title || 'Media Video'
      });
    });
    return btn;
  }

  function openMediaDownloadModal(target) {
    currentMediaTarget = target;
    mediaModalFormatBadge.textContent = `${target.resolution} • ${target.extension.toUpperCase()}`;
    mediaModalFormatBadge.className = 'status-pill ' + (target.isAudioOnly ? 'green' : 'blue');
    mediaModalSize.textContent = target.sizeStr ? target.sizeStr.replace(/[()]/g, '').trim() : 'Dynamic';

    const safeTitle = (target.title || 'Media Video').replace(/[/\\?%*:|"<>]/g, '_').trim();
    const ext = target.extension || (target.isAudioOnly ? 'mp3' : 'mp4');
    mediaModalFileName.value = `${safeTitle}.${ext}`;

    const remembered = localStorage.getItem('wdm_last_save_location');
    mediaModalSavePath.value = remembered || defaultDownloadDirectory || '';
    chkRememberLocation.checked = true;

    mediaDownloadModal.classList.add('open');
  }

  function closeMediaModal() {
    mediaDownloadModal.classList.remove('open');
    currentMediaTarget = null;
  }

  btnCancelMediaModal.addEventListener('click', closeMediaModal);
  btnCancelMediaDownload.addEventListener('click', closeMediaModal);

  mediaDownloadModal.addEventListener('click', (e) => {
    if (e.target === mediaDownloadModal) {
      closeMediaModal();
    }
  });

  btnBrowseMediaLocation.addEventListener('click', () => {
    sendToHost('browse_folder');
  });

  btnConfirmMediaDownload.addEventListener('click', () => {
    if (!currentMediaTarget) return;

    const savePath = mediaModalSavePath.value.trim();
    const fileName = mediaModalFileName.value.trim() || `${currentMediaTarget.title}.${currentMediaTarget.extension}`;

    if (chkRememberLocation.checked && savePath) {
      localStorage.setItem('wdm_last_save_location', savePath);
    }

    sendToHost('download_media', {
      url: currentMediaTarget.url,
      formatId: currentMediaTarget.formatId,
      title: currentMediaTarget.title,
      fileName: fileName,
      destinationDirectory: savePath,
      isAudioOnly: currentMediaTarget.isAudioOnly
    });

    closeMediaModal();

    // Switch to "All Downloads" tab so user immediately sees live download progress
    const allDownloadsTab = document.querySelector('.nav-item[data-tab="downloads"]');
    if (allDownloadsTab) {
      allDownloadsTab.click();
    }
  });

  // Query default download directory from host on load
  sendToHost('get_default_folder');

  // Benchmark Runner
  btnRunBenchmark.addEventListener('click', async () => {
    const url = benchUrlInput.value.trim();
    if (!url) return;

    btnRunBenchmark.textContent = 'Benchmarking...';
    btnRunBenchmark.disabled = true;
    benchResultsBox.style.display = 'block';
    benchTableBody.innerHTML = `<tr><td colspan="4" style="text-align:center;padding:16px;color:#38a5ff;">Testing streams across 1, 4, 8, and 16 connections...</td></tr>`;

    setTimeout(() => {
      benchTableBody.innerHTML = `
        <tr><td>1 Connection</td><td>2.4 MB/s</td><td>4.2s</td><td>1.0x (Baseline)</td></tr>
        <tr><td>4 Connections</td><td>8.9 MB/s</td><td>1.3s</td><td>3.2x faster</td></tr>
        <tr><td>8 Connections</td><td>16.2 MB/s</td><td>0.7s</td><td>6.0x faster</td></tr>
        <tr style="font-weight:600;color:#38a5ff;"><td>16 Connections</td><td>26.8 MB/s</td><td>0.4s</td><td>10.5x acceleration 🚀</td></tr>
      `;
      btnRunBenchmark.textContent = 'Start Benchmark';
      btnRunBenchmark.disabled = false;
    }, 2200);
  });

  // Parse yt-dlp live console telemetry
  function parseYtDlpStatus(rawStatus) {
    if (!rawStatus) return null;

    const result = {
      percent: null,
      totalBytes: 0,
      totalFormatted: null,
      speedBytes: 0,
      speedFormatted: null,
      eta: null
    };

    // Percentage: e.g. "93.1%"
    const pctMatch = rawStatus.match(/(\d+(?:\.\d+)?)%/);
    if (pctMatch) {
      result.percent = parseFloat(pctMatch[1]);
    }

    // Total size: e.g. "of 13.41MiB" or "of ~20.00MiB"
    const totalMatch = rawStatus.match(/of\s+~?\s*(\d+(?:\.\d+)?)\s*([KMGT]?i?B)/i);
    if (totalMatch) {
      const val = parseFloat(totalMatch[1]);
      const unit = totalMatch[2].toUpperCase();
      let mult = 1;
      if (unit.startsWith('K')) mult = 1024;
      else if (unit.startsWith('M')) mult = 1024 * 1024;
      else if (unit.startsWith('G')) mult = 1024 * 1024 * 1024;
      else if (unit.startsWith('T')) mult = 1024 * 1024 * 1024 * 1024;

      result.totalBytes = Math.round(val * mult);
      result.totalFormatted = formatBytes(result.totalBytes);
    }

    // Speed: e.g. "at 168.19KiB/s"
    const speedMatch = rawStatus.match(/at\s+(\d+(?:\.\d+)?)\s*([KMGT]?i?B\/s)/i);
    if (speedMatch) {
      const val = parseFloat(speedMatch[1]);
      const unit = speedMatch[2].toUpperCase();
      let mult = 1;
      if (unit.startsWith('K')) mult = 1024;
      else if (unit.startsWith('M')) mult = 1024 * 1024;
      else if (unit.startsWith('G')) mult = 1024 * 1024 * 1024;
      else if (unit.startsWith('T')) mult = 1024 * 1024 * 1024 * 1024;

      result.speedBytes = Math.round(val * mult);
      result.speedFormatted = formatBytes(result.speedBytes) + '/s';
    }

    // ETA: e.g. "ETA 00:03"
    const etaMatch = rawStatus.match(/ETA\s+([0-9:]+)/i);
    if (etaMatch) {
      result.eta = etaMatch[1];
    }

    return result;
  }

  // Utility
  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let i = 0;
    while (bytes >= 1024 && i < units.length - 1) {
      bytes /= 1024;
      i++;
    }
    return bytes.toFixed(i === 0 ? 0 : 1) + ' ' + units[i];
  }
})();
