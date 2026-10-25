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

// Native Messaging Connection
function getNativePort() {
  if (nativePort) return nativePort;

  try {
    nativePort = chrome.runtime.connectNative(NATIVE_HOST);

    nativePort.onMessage.addListener((msg) => {
      handleNativeMessage(msg);
    });

    nativePort.onDisconnect.addListener(() => {
      const err = chrome.runtime.lastError?.message || "Disconnected";
      console.warn("WDM Native Host disconnected:", err);
      nativePort = null;
      updateBadge();
    });

    return nativePort;
  } catch (err) {
    console.error("Failed to connect to WDM Native Host:", err);
    return null;
  }
}

function sendNativeMessage(msg) {
  return new Promise((resolve, reject) => {
    const port = getNativePort();
    if (!port) {
      return reject(new Error("WDM Native Host is not registered or not running. Please run WDM.NativeHost --install."));
    }

    const requestId = msg.requestId || ("req_" + Math.random().toString(36).substring(2, 10));
    msg.requestId = requestId;

    pendingRequests.set(requestId, { resolve, reject, timeout: setTimeout(() => {
      pendingRequests.delete(requestId);
      reject(new Error("Native host request timed out"));
    }, 15000) });

    try {
      port.postMessage(msg);
    } catch (e) {
      pendingRequests.delete(requestId);
      reject(e);
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
      activeDownloads.set(msg.downloadId, {
        id: msg.downloadId,
        url: msg.url,
        percentage: 0,
        speed: "0 B/s",
        status: "Downloading"
      });
      updateBadge();
      notifyPopup({ type: "download_started", data: msg });
      break;

    case "download_progress":
      if (activeDownloads.has(msg.downloadId)) {
        const item = activeDownloads.get(msg.downloadId);
        item.percentage = msg.percentage;
        item.speed = msg.speed;
        item.downloaded = msg.downloaded;
        item.total = msg.total;
      }
      updateBadge();
      notifyPopup({ type: "download_progress", data: msg });
      break;

    case "download_completed":
      activeDownloads.delete(msg.downloadId);
      updateBadge();
      notifyPopup({ type: "download_completed", data: msg });
      break;

    case "download_failed":
      activeDownloads.delete(msg.downloadId);
      updateBadge();
      notifyPopup({ type: "download_failed", data: msg });
      break;
  }
}

function updateBadge() {
  const count = activeDownloads.size;
  if (count > 0) {
    chrome.action.setBadgeText({ text: count.toString() });
    chrome.action.setBadgeBackgroundColor({ color: "#00d2ff" });
  } else {
    chrome.action.setBadgeText({ text: "" });
  }
}

function notifyPopup(message) {
  chrome.runtime.sendMessage(message).catch(() => {
    // Popup might not be open, safe to ignore
  });
}

// Download Interception
chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  chrome.storage.local.get(["interceptDownloads", "connections"], async (settings) => {
    if (!settings.interceptDownloads) {
      return; // Interception disabled
    }

    // Ignore blob/data/internal downloads
    if (item.url.startsWith("blob:") || item.url.startsWith("data:") || item.url.startsWith("chrome://")) {
      return;
    }

    // Cancel native browser download
    chrome.downloads.cancel(item.id, () => {
      chrome.downloads.erase({ id: item.id });
    });

    // Gather domain cookies to authenticate download
    let cookieHeader = "";
    try {
      const cookies = await chrome.cookies.getAll({ url: item.url });
      cookieHeader = cookies.map(c => `${c.name}=${c.value}`).join("; ");
    } catch (e) {
      console.warn("Could not retrieve cookies:", e);
    }

    // Dispatch to WDM Native Host
    try {
      await sendNativeMessage({
        action: "start_download",
        url: item.url,
        fileName: item.filename,
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
});

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
    sendNativeMessage({ action: "open_app" })
      .then(res => sendResponse({ success: true }))
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
