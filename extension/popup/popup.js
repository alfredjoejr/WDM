// WDM Extension Popup Logic

document.addEventListener('DOMContentLoaded', async () => {
  const hostStatus = document.getElementById('hostStatus');
  const statusText = document.getElementById('statusText');
  const urlInput = document.getElementById('urlInput');
  const btnDownload = document.getElementById('btnDownload');
  const downloadsList = document.getElementById('downloadsList');
  const downloadCount = document.getElementById('downloadCount');
  const chkIntercept = document.getElementById('chkIntercept');
  const chkSniffer = document.getElementById('chkSniffer');
  const connSelector = document.getElementById('connSelector');
  const lnkTestHost = document.getElementById('lnkTestHost');
  const lnkOpenApp = document.getElementById('lnkOpenApp');

  let activeConnections = 16;

  // 1. Load preferences from chrome.storage
  chrome.storage.local.get(['interceptDownloads', 'mediaSniffer', 'connections'], (res) => {
    chkIntercept.checked = res.interceptDownloads !== false;
    chkSniffer.checked = res.mediaSniffer !== false;

    if (res.connections) {
      activeConnections = res.connections;
      updateSegmentUI(activeConnections);
    }
  });

  // 2. Check Native Host Connection
  checkHostConnection();

  async function checkHostConnection() {
    hostStatus.className = 'status-badge checking';
    statusText.textContent = 'Checking...';

    chrome.runtime.sendMessage({ action: 'ping_host' }, (res) => {
      if (res && res.success) {
        hostStatus.className = 'status-badge connected';
        statusText.textContent = 'Connected (.NET 10)';
      } else {
        hostStatus.className = 'status-badge disconnected';
        statusText.textContent = 'Host Disconnected';
      }
    });

    // Also get active downloads
    chrome.runtime.sendMessage({ action: 'get_status' }, (res) => {
      if (res && res.activeDownloads) {
        renderDownloads(res.activeDownloads);
      }
    });
  }

  // 3. Settings Toggles
  chkIntercept.addEventListener('change', () => {
    chrome.storage.local.set({ interceptDownloads: chkIntercept.checked });
  });

  chkSniffer.addEventListener('change', () => {
    chrome.storage.local.set({ mediaSniffer: chkSniffer.checked });
  });

  // 4. Segmented Control for Connections
  connSelector.querySelectorAll('.segment').forEach((btn) => {
    btn.addEventListener('click', () => {
      const val = parseInt(btn.getAttribute('data-val'), 10);
      activeConnections = val;
      updateSegmentUI(val);
      chrome.storage.local.set({ connections: val });
    });
  });

  function updateSegmentUI(val) {
    connSelector.querySelectorAll('.segment').forEach((btn) => {
      btn.classList.toggle('active', parseInt(btn.getAttribute('data-val'), 10) === val);
    });
  }

  // 5. Quick Download Start
  btnDownload.addEventListener('click', startManualDownload);
  urlInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') startManualDownload();
  });

  function startManualDownload() {
    const url = urlInput.value.trim();
    if (!url) return;

    btnDownload.disabled = true;
    btnDownload.textContent = 'Starting...';

    chrome.runtime.sendMessage({
      action: 'start_manual_download',
      url: url,
      connections: activeConnections
    }, (res) => {
      btnDownload.disabled = false;
      btnDownload.innerHTML = `
        <svg viewBox="0 0 24 24" class="btn-icon"><path d="M5 20h14v-2H5v2zM19 9h-4V3H9v6H5l7 7 7-7z"/></svg>
        <span>Start</span>
      `;

      if (res && res.downloadId) {
        urlInput.value = '';
      } else if (res && res.error) {
        alert("Error starting download: " + res.error);
      }
    });
  }

  // 6. Test Host & Open Dashboard Links
  lnkTestHost.addEventListener('click', (e) => {
    e.preventDefault();
    checkHostConnection();
  });

  if (lnkOpenApp) {
    lnkOpenApp.addEventListener('click', (e) => {
      e.preventDefault();
      chrome.runtime.sendMessage({ action: 'open_app' });
    });
  }

  // 7. Render Active Downloads
  function renderDownloads(list) {
    downloadCount.textContent = list.length.toString();

    if (!list || list.length === 0) {
      downloadsList.innerHTML = `
        <div class="empty-state">
          <svg viewBox="0 0 24 24"><path d="M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM12 18l5-5h-3V9h-4v4H7l5 5z"/></svg>
          <p>No active downloads</p>
          <span>Downloads caught from your browser appear here</span>
        </div>
      `;
      return;
    }

    downloadsList.innerHTML = '';
    list.forEach((item) => {
      const card = document.createElement('div');
      card.className = 'download-item-card';
      const percent = Math.round(item.percentage || 0);

      card.innerHTML = `
        <div class="info-row">
          <span class="file-name" title="${item.url}">${item.url.split('/').pop().split('?')[0] || 'file'}</span>
          <span class="speed-pill">${item.speed || '0 B/s'}</span>
        </div>
        <div class="progress-track">
          <div class="progress-fill" style="width: ${percent}%;"></div>
        </div>
        <div class="info-row" style="font-size: 10px; color: #94a3b8;">
          <span>${item.downloaded || '0 B'} / ${item.total || 'Unknown'}</span>
          <span>${percent}%</span>
        </div>
      `;

      downloadsList.appendChild(card);
    });
  }

  // 8. Listen for realtime progress broadcast from background.js
  chrome.runtime.onMessage.addListener((msg) => {
    if (msg.type === 'download_progress' || msg.type === 'download_started' || msg.type === 'download_completed') {
      chrome.runtime.sendMessage({ action: 'get_status' }, (res) => {
        if (res && res.activeDownloads) {
          renderDownloads(res.activeDownloads);
        }
      });
    }
  });
});
