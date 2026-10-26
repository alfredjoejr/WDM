// WDM (Windows Download Manager) - Service Worker Background Script
// Manifest V3

const NATIVE_HOST = "com.wdm.downloader";
let nativePort = null;
let pendingRequests = new Map();
let activeDownloads = new Map();

// Default settings
const DEFAULT_SETTINGS = {
  interceptDownloads: true,
  mediaSniffer: true,
  connections: 16,
  minFileSizeMB: 1 // Only intercept files >= 1MB (or all if 0)
};

// Initialize settings
chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.local.get(DEFAULT_SETTINGS, (settings) => {
    chrome.storage.local.set(settings);
  });

  // Setup Context Menus
  chrome.contextMenus.create({
    id: "wdm_download_link",
    title: "Download with WDM (High Speed)",
    contexts: ["link"]
  });

  chrome.contextMenus.create({
    id: "wdm_download_media",
    title: "Download Media with WDM",
    contexts: ["video", "audio"]
  });
});

// Native Messaging Connection (Supports Chrome, Edge, Brave, and Firefox)
function getRuntimeApi() {
  if (typeof browser !== 'undefined' && browser.runtime) {
    return browser.runtime;
  }
  return chrome.runtime;
}

function getNativePort() {
  if (nativePort) return nativePort;

  try {
    const api = getRuntimeApi();
    nativePort = api.connectNative(NATIVE_HOST);

    nativePort.onMessage.addListener((msg) => {
      handleNativeMessage(msg);
    });

    nativePort.onDisconnect.addListener((p) => {
      const api = getRuntimeApi();
      const err = p?.error?.message || api.lastError?.message || "Host process disconnected";
      console.warn("[WDM] Native Host port disconnected:", err);
      nativePort = null;
      for (const [id, req] of pendingRequests.entries()) {
        clearTimeout(req.timeout);
        // Attempt one-shot fallback for in-flight requests before giving up
        tryOneShotNativeMessage(req.msg)
          .then(req.resolve)
          .catch(() => req.reject(new Error("WDM Native Host disconnected: " + err)));
      }
      pendingRequests.clear();
      updateBadge();
    });

    return nativePort;
  } catch (err) {
    console.error("[WDM] Failed to connect to Native Host:", err);
    return null;
  }
}

function tryOneShotNativeMessage(msg) {
  return new Promise((resolve, reject) => {
    const api = getRuntimeApi();
    if (!api || !api.sendNativeMessage) {
      return reject(new Error("Native messaging API not available"));
    }

    try {
      const maybePromise = api.sendNativeMessage(NATIVE_HOST, msg, (response) => {
        const lastErr = api.lastError?.message;
        if (lastErr) {
          reject(new Error("WDM Native Host disconnected: " + lastErr));
        } else if (response) {
          resolve(response);
        } else {
          resolve({ success: true });
        }
      });

      if (maybePromise && typeof maybePromise.then === 'function') {
        maybePromise.then(resolve).catch((err) => {
          reject(new Error("WDM Native Host disconnected: " + (err?.message || String(err))));
        });
      }
    } catch (err) {
      reject(err);
    }
  });
}

function sendNativeMessage(msg) {
  return new Promise((resolve, reject) => {
    const requestId = msg.requestId || ("req_" + Math.random().toString(36).substring(2, 10));
    msg.requestId = requestId;

    const port = getNativePort();
    if (!port) {
      // Fallback directly to one-shot native message
      return tryOneShotNativeMessage(msg).then(resolve).catch(reject);
    }

    const timeout = setTimeout(() => {
      pendingRequests.delete(requestId);
      tryOneShotNativeMessage(msg).then(resolve).catch(reject);
    }, 20000);

    pendingRequests.set(requestId, { resolve, reject, msg, timeout });

    try {
      port.postMessage(msg);
    } catch (e) {
      pendingRequests.delete(requestId);
      clearTimeout(timeout);
      tryOneShotNativeMessage(msg).then(resolve).catch(reject);
    }
  });
}

function handleNativeMessage(msg) {
  // 1. Resolve pending RPC request if matched
  if (msg.requestId && pendingRequests.has(msg.requestId)) {
    const { resolve, timeout } = pendingRequests.get(msg.requestId);
    clearTimeout(timeout);
    pendingRequests.delete(msg.requestId);
    resolve(msg);
  }

  // 2. Handle streaming push notifications
  switch (msg.action) {
    case "download_started":
    case "media_download_started":
      activeDownloads.set(msg.downloadId, {
        id: msg.downloadId,
        url: msg.fileName || msg.url || "Media Stream",
        percentage: 0,
        speed: msg.action === "media_download_started" ? "Catching..." : "0 B/s",
        status: "Downloading"
      });
      updateBadge();
      notifyPopup({ type: "download_started", data: msg });
      break;

    case "download_progress":
    case "media_download_progress":
      if (activeDownloads.has(msg.downloadId)) {
        const item = activeDownloads.get(msg.downloadId);
        if (typeof msg.percentage === "number" && msg.percentage >= 0) {
          item.percentage = msg.percentage;
        }
        if (msg.speed) {
          item.speed = msg.speed;
        } else if (msg.statusText) {
          const speedMatch = msg.statusText.match(/at\s+(\S+\/s)/i);
          if (speedMatch) item.speed = speedMatch[1];
        }
        if (msg.downloaded) item.downloaded = msg.downloaded;
        if (msg.total) item.total = msg.total;
      }
      updateBadge();
      notifyPopup({ type: "download_progress", data: msg });
      break;

    case "download_completed":
    case "media_download_completed":
      activeDownloads.delete(msg.downloadId);
      updateBadge();
      notifyPopup({ type: "download_completed", data: msg });
      break;

    case "download_failed":
    case "media_download_failed":
      activeDownloads.delete(msg.downloadId);
      updateBadge();
      notifyPopup({ type: "download_failed", data: msg });
      break;
  }
}

function updateBadge() {
  const count = activeDownloads.size;
  const actionApi = chrome.action || chrome.browserAction;
  if (!actionApi) return;
  if (count > 0) {
    actionApi.setBadgeText({ text: count.toString() });
    actionApi.setBadgeBackgroundColor({ color: "#00d2ff" });
  } else {
    actionApi.setBadgeText({ text: "" });
  }
}

function notifyPopup(message) {
  chrome.runtime.sendMessage(message).catch(() => {
    // Popup might not be open, safe to ignore
  });
}

// Download Interception (Chrome: onDeterminingFilename, Firefox: onCreated)
function handleDownloadIntercept(item) {
  if (!item || !item.url) return;

  chrome.storage.local.get(["interceptDownloads", "connections"], async (settings) => {
    if (!settings.interceptDownloads) {
      return; // Interception disabled
    }

    // Ignore blob/data/internal downloads
    if (item.url.startsWith("blob:") || item.url.startsWith("data:") || item.url.startsWith("chrome://") || item.url.startsWith("about:")) {
      return;
    }

    // Cancel native browser download
    try {
      chrome.downloads.cancel(item.id, () => {
        try {
          chrome.downloads.erase({ id: item.id });
        } catch { }
      });
    } catch { }

    // Gather domain cookies to authenticate download
    let cookieHeader = "";
    try {
      if (chrome.cookies && chrome.cookies.getAll) {
        const cookies = await chrome.cookies.getAll({ url: item.url });
        cookieHeader = cookies.map(c => `${c.name}=${c.value}`).join("; ");
      }
    } catch (e) {
      console.warn("Could not retrieve cookies:", e);
    }

    // Dispatch to WDM Native Host
    try {
      await sendNativeMessage({
        action: "start_download",
        url: item.url,
        fileName: item.filename || "",
        connections: settings.connections || 16,
        headers: {
          "Cookie": cookieHeader,
          "Referer": item.referrer || "",
          "User-Agent": navigator.userAgent
        }
      });
    } catch (err) {
      console.error("Failed to forward download to WDM:", err);
    }
  });
}

if (chrome.downloads) {
  if (chrome.downloads.onDeterminingFilename) {
    chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
      handleDownloadIntercept(item);
    });
  } else if (chrome.downloads.onCreated) {
    chrome.downloads.onCreated.addListener((item) => {
      handleDownloadIntercept(item);
    });
  }
}

// Context Menu Click Handling
chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === "wdm_download_link" && info.linkUrl) {
    let cookieHeader = "";
    try {
      const cookies = await chrome.cookies.getAll({ url: info.linkUrl });
      cookieHeader = cookies.map(c => `${c.name}=${c.value}`).join("; ");
    } catch { }

    sendNativeMessage({
      action: "start_download",
      url: info.linkUrl,
      headers: {
        "Cookie": cookieHeader,
        "Referer": tab?.url || ""
      }
    });
  } else if (info.menuItemId === "wdm_download_media" && (info.srcUrl || tab?.url)) {
    const targetUrl = info.srcUrl || tab?.url;
    sendNativeMessage({
      action: "download_media",
      url: targetUrl
    });
  }
});

// Messages from Content Script & Popup
chrome.runtime.onMessage.addListener((req, sender, sendResponse) => {
  if (req.action === "get_status") {
    const isConnected = !!getNativePort();
    sendResponse({
      connected: isConnected,
      activeDownloads: Array.from(activeDownloads.values())
    });
    return true;
  }

  if (req.action === "ping_host") {
    sendNativeMessage({ action: "ping" })
      .then(res => sendResponse({ success: true, res }))
      .catch(err => sendResponse({ success: false, error: err.message }));
    return true; // async
  }

  if (req.action === "sniff_media") {
    sendNativeMessage({ action: "sniff_media", url: req.url })
      .then(res => sendResponse(res))
      .catch(err => sendResponse({ success: false, message: err.message }));
    return true;
  }

  if (req.action === "download_media") {
    sendNativeMessage({
      action: "download_media",
      url: req.url,
      formatId: req.formatId,
      fileName: req.fileName,
      isAudioOnly: req.isAudioOnly || false
    })
      .then(res => sendResponse(res))
      .catch(err => sendResponse({ success: false, message: err.message }));
    return true;
  }

  if (req.action === "open_app") {
    sendNativeMessage({ action: "open_app", url: req.url || "" })
      .then(res => sendResponse({ success: res?.success !== false, message: res?.message || res?.error }))
      .catch(err => sendResponse({ success: false, message: err.message }));
    return true;
  }

  if (req.action === "cancel_download") {
    activeDownloads.delete(req.downloadId);
    updateBadge();
    sendNativeMessage({ action: "cancel_download", downloadId: req.downloadId })
      .then(res => sendResponse(res))
      .catch(err => sendResponse({ success: false, message: err.message }));
    return true;
  }

  if (req.action === "start_manual_download") {
    sendNativeMessage({ action: "start_download", url: req.url, connections: req.connections || 16 })
      .then(res => sendResponse(res))
      .catch(err => sendResponse({ success: false, message: err.message }));
    return true;
  }
});
