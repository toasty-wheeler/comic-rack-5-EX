// ComicRack Downloader: sends the comic on the current page to ComicRack Community Edition.
//
// The toolbar icon is green when ComicRack has a site module for the page, gray otherwise and
// shows a red "!" when ComicRack isn't running. Clicking it:
//   1. reads the page (HTML, metadata tags, the large images shown) in the tab
//   2. ComicRack's site module turns that into metadata and the list of page images
//   3. the browser downloads the images and passes them to ComicRack, which lists the job under
//      Downloads, packs the book in the chosen format with ComicInfo.xml and adds it to the library

const API = "http://127.0.0.1:17385/api/";
const PARALLEL = 4;
const RETRIES = 3;
const REFERER_RULE_ID = 1;

const COLORS = {
  ready: "#2e9e4f",   // supported site
  idle: "#8a8f98",    // no site module (clicking still saves the page's images)
  busy: "#3478e5",    // downloading
  error: "#c94040"
};

const supportCache = new Map();   // host -> { supported, site, time }
const activeTabs = new Set();     // tabs with a download in progress

// ---------------------------------------------------------------------------------------------
// Icon

function drawIcon(size, color) {
  const canvas = new OffscreenCanvas(size, size);
  const g = canvas.getContext("2d");
  const r = size * 0.22;
  g.fillStyle = color;
  g.beginPath();
  g.moveTo(r, 0);
  g.arcTo(size, 0, size, size, r);
  g.arcTo(size, size, 0, size, r);
  g.arcTo(0, size, 0, 0, r);
  g.arcTo(0, 0, size, 0, r);
  g.fill();
  // Open book
  const m = size * 0.18, top = size * 0.26, bottom = size * 0.78, mid = size / 2;
  g.fillStyle = "#ffffff";
  g.beginPath();
  g.moveTo(m, top);
  g.lineTo(mid - size * 0.03, top + size * 0.06);
  g.lineTo(mid - size * 0.03, bottom);
  g.lineTo(m, bottom - size * 0.06);
  g.closePath();
  g.fill();
  g.beginPath();
  g.moveTo(size - m, top);
  g.lineTo(mid + size * 0.03, top + size * 0.06);
  g.lineTo(mid + size * 0.03, bottom);
  g.lineTo(size - m, bottom - size * 0.06);
  g.closePath();
  g.fill();
  return g.getImageData(0, 0, size, size);
}

const iconCache = {};

function iconFor(state) {
  if (!iconCache[state]) {
    iconCache[state] = { 16: drawIcon(16, COLORS[state]), 32: drawIcon(32, COLORS[state]) };
  }
  return iconCache[state];
}

async function setState(tabId, state, title, badge = "", badgeColor = COLORS.busy) {
  try {
    await chrome.action.setIcon({ tabId, imageData: iconFor(state) });
    await chrome.action.setTitle({ tabId, title });
    await chrome.action.setBadgeText({ tabId, text: badge });
    if (badge) {
      await chrome.action.setBadgeBackgroundColor({ tabId, color: badgeColor });
    }
  } catch (e) {
    // Tab was closed
  }
}

// ---------------------------------------------------------------------------------------------
// ComicRack API

async function api(path, options = {}) {
  const response = await fetch(API + path, options);
  let body = {};
  try {
    body = await response.json();
  } catch (e) {
    // empty reply
  }
  if (!response.ok) {
    const error = new Error(body.error || `ComicRack answered ${response.status}`);
    error.status = response.status;
    throw error;
  }
  return body;
}

function postJson(path, data) {
  return api(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(data)
  });
}

// ---------------------------------------------------------------------------------------------
// Icon color for the current page

function isWebPage(url) {
  return /^https?:\/\//i.test(url || "");
}

async function updateTab(tabId, url) {
  if (activeTabs.has(tabId)) {
    return;
  }
  if (!isWebPage(url)) {
    await setState(tabId, "idle", "ComicRack Downloader: not a web page");
    return;
  }
  const host = new URL(url).host;
  let result = supportCache.get(host);
  if (!result || Date.now() - result.time > 5 * 60 * 1000) {
    try {
      const check = await api("check?url=" + encodeURIComponent(url));
      result = { supported: !!check.supported, site: check.site, time: Date.now() };
      supportCache.set(host, result);
    } catch (e) {
      supportCache.delete(host);
      await setState(tabId, "idle", "ComicRack isn't running. Start ComicRack to download comics.", "!", COLORS.error);
      return;
    }
  }
  if (result.supported) {
    await setState(tabId, "ready", `Download this comic with ComicRack (${result.site})`);
  } else {
    await setState(tabId, "idle", "No ComicRack site module for this page. Click to save the images shown on it anyway.");
  }
}

chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  try {
    const tab = await chrome.tabs.get(tabId);
    updateTab(tabId, tab.url);
  } catch (e) {
    // closed
  }
});

chrome.tabs.onUpdated.addListener((tabId, change, tab) => {
  if (change.url || change.status === "complete") {
    updateTab(tabId, tab.url);
  }
});

// ---------------------------------------------------------------------------------------------
// Reading the page (runs inside the tab)

async function collectPage() {
  const absolute = (u) => {
    try {
      return u ? new URL(u, location.href).href : null;
    } catch (e) {
      return null;
    }
  };
  const meta = (...names) => {
    for (const n of names) {
      const el = document.querySelector(`meta[property="${n}"], meta[name="${n}"], meta[itemprop="${n}"]`);
      const v = el && (el.content || "").trim();
      if (v) {
        return v;
      }
    }
    return "";
  };
  const isPlaceholder = (u) => !u || /^data:image\/(gif|svg)/i.test(u) || /(placeholder|loading|blank|spacer|lazy)\.(gif|png|svg|jpe?g|webp)/i.test(u);

  // Large images in document order; lazy-loaded ones by their real source
  const images = [];
  const seen = new Set();
  for (const img of document.querySelectorAll("img")) {
    const lazy = img.getAttribute("data-src") || img.getAttribute("data-original") || img.getAttribute("data-lazy-src") || img.getAttribute("data-url") || img.getAttribute("data-srcset")?.split(/\s+/)[0];
    let src = img.currentSrc || img.src;
    if (isPlaceholder(src) && lazy) {
      src = lazy;
    }
    src = absolute(src);
    if (!src || seen.has(src)) {
      continue;
    }
    const w = img.naturalWidth || 0, h = img.naturalHeight || 0;
    const loadedAndLarge = w >= 300 && h >= 300 && w * h >= 200000;
    const notLoadedYet = w === 0 && !!lazy;
    if (!loadedAndLarge && !notLoadedYet) {
      continue;
    }
    seen.add(src);
    images.push(src);
  }
  // Images some readers only keep as blob: URLs can't be fetched outside the page
  for (let i = 0; i < images.length; i++) {
    if (images[i].startsWith("blob:")) {
      try {
        const blob = await (await fetch(images[i])).blob();
        images[i] = await new Promise((resolve) => {
          const reader = new FileReader();
          reader.onload = () => resolve(reader.result);
          reader.readAsDataURL(blob);
        });
      } catch (e) {
        images[i] = null;
      }
    }
  }

  // Structured data (schema.org ComicIssue / Book / ...) when the site provides it
  let ld = {};
  for (const script of document.querySelectorAll('script[type="application/ld+json"]')) {
    try {
      const data = JSON.parse(script.textContent);
      const items = Array.isArray(data) ? data : data["@graph"] || [data];
      const found = items.find((x) => /Comic|Book|Manga|CreativeWork/i.test([].concat(x["@type"] || []).join(" ")));
      if (found) {
        ld = found;
        break;
      }
    } catch (e) {
      // invalid JSON-LD
    }
  }
  const name = (v) => [].concat(v || []).map((x) => (typeof x === "string" ? x : x && x.name)).filter(Boolean).join(", ");

  return {
    url: location.href,
    title: document.title,
    html: document.documentElement.outerHTML,
    meta: {
      title: name(ld.name) || meta("og:title", "twitter:title"),
      description: (typeof ld.description === "string" ? ld.description : "") || meta("og:description", "description", "twitter:description"),
      author: name(ld.author) || meta("author", "article:author", "book:author"),
      keywords: name(ld.genre) || name(ld.keywords) || meta("keywords"),
      siteName: meta("og:site_name", "application-name"),
      series: name(ld.isPartOf),
      language: ld.inLanguage || document.documentElement.lang || ""
    },
    images: images.filter(Boolean)
  };
}

// ---------------------------------------------------------------------------------------------
// Downloading

// Image hosts often check the Referer; requests from this extension get the page's
async function setReferer(referer) {
  await chrome.declarativeNetRequest.updateSessionRules({
    removeRuleIds: [REFERER_RULE_ID],
    addRules: [{
      id: REFERER_RULE_ID,
      priority: 1,
      action: {
        type: "modifyHeaders",
        requestHeaders: [{ header: "Referer", operation: "set", value: referer }]
      },
      condition: {
        tabIds: [chrome.tabs.TAB_ID_NONE],
        initiatorDomains: [chrome.runtime.id],
        resourceTypes: ["xmlhttprequest", "other"]
      }
    }]
  });
}

async function clearReferer() {
  await chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds: [REFERER_RULE_ID] });
}

async function fetchImage(url) {
  let lastError;
  for (let attempt = 0; attempt <= RETRIES; attempt++) {
    try {
      const response = await fetch(url, { credentials: "include", cache: "force-cache" });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      return { data: await response.arrayBuffer(), type: response.headers.get("Content-Type") || "" };
    } catch (e) {
      lastError = e;
      await new Promise((r) => setTimeout(r, 500 * (attempt + 1)));
    }
  }
  throw lastError;
}

async function download(tab) {
  if (activeTabs.has(tab.id)) {
    return;
  }
  activeTabs.add(tab.id);
  let jobId = null;
  try {
    await setState(tab.id, "busy", "Reading the page…", "…");
    try {
      await api("status");
    } catch (e) {
      throw new Error("ComicRack isn't running. Start ComicRack and try again.");
    }

    const [{ result: page }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: collectPage });
    await setState(tab.id, "busy", "ComicRack is reading the comic's details…", "…");
    const prepared = await postJson("prepare", page);
    jobId = prepared.jobId;

    if (prepared.mode === "queued") {
      // Series page: ComicRack downloads the chapters
      await finish(tab.id, `ComicRack is downloading the chapters of "${prepared.title}"`, "✓", COLORS.ready);
      notify("Series sent to ComicRack", `"${prepared.title}": the chapters are being downloaded by ComicRack.`);
      jobId = null;
      return;
    }

    const pages = prepared.pages;
    let done = 0;
    let failed = 0;
    let next = 0;
    let stopped = null;
    try {
      await setReferer(pages[0].referer || page.url);
    } catch (e) {
      console.warn("Referer rule not available:", e);
    }

    const worker = async () => {
      while (next < pages.length && !stopped) {
        const index = next++;
        const p = pages[index];
        try {
          const image = await fetchImage(p.url);
          await api(`page?job=${jobId}&index=${index}`, {
            method: "POST",
            headers: { "Content-Type": image.type || "application/octet-stream", "X-Source-Url": p.url },
            body: image.data
          });
        } catch (e) {
          if (e.status === 409 || e.status === 410) {
            stopped = e;   // canceled or removed in ComicRack
          } else {
            failed++;
            console.warn(`Page ${index + 1} failed:`, p.url, e);
          }
        }
        done++;
        const percent = Math.floor((done * 100) / pages.length);
        await setState(tab.id, "busy", `Downloading "${prepared.title}": ${done} of ${pages.length} pages`, `${percent}%`);
      }
    };
    await Promise.all(Array.from({ length: Math.min(PARALLEL, pages.length) }, worker));
    await clearReferer();
    if (stopped) {
      jobId = null;
      throw stopped;
    }

    await setState(tab.id, "busy", "ComicRack is packing the book…", "…");
    const result = await postJson(`complete?job=${jobId}`, { failed });
    jobId = null;
    await finish(tab.id, `Saved "${result.title}" to ComicRack`, "✓", COLORS.ready);
    notify("Saved to ComicRack", `"${result.title}" (${pages.length - failed} pages)`);
  } catch (e) {
    await clearReferer().catch(() => {});
    if (jobId) {
      await postJson(`fail?job=${jobId}`, { message: e.message }).catch(() => {});
    }
    await finish(tab.id, "ComicRack download failed: " + e.message, "!", COLORS.error);
    notify("ComicRack download failed", e.message);
  } finally {
    activeTabs.delete(tab.id);
  }
}

async function finish(tabId, title, badge, color) {
  await setState(tabId, color === COLORS.error ? "error" : "ready", title, badge, color);
  setTimeout(async () => {
    try {
      const tab = await chrome.tabs.get(tabId);
      await chrome.action.setBadgeText({ tabId, text: "" });
      updateTab(tabId, tab.url);
    } catch (e) {
      // closed
    }
  }, 8000);
}

function notify(title, message) {
  chrome.notifications.create({
    type: "basic",
    iconUrl: "icons/icon128.png",
    title,
    message: message || ""
  });
}

chrome.action.onClicked.addListener(download);
