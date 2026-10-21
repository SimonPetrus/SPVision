// SPVision güncelleme vekili (Cloudflare Worker) — bkz. README.md ve CLAUDE_REHBERI.md › "Uygulama güncelleme".
//
// GitHub deposu ÖZEL kalır. Uygulama yalnız bu Worker'a konuşur; Worker en son (taslak / ön sürüm olmayan) GitHub
// sürümünün dosyalarını kendi GITHUB_TOKEN'ı ile okur (ince ayarlı PAT: yalnız SimonPetrus/SPVision, Contents: Read-only;
// `wrangler secret put GITHUB_TOKEN` ile verilir, kodda/yapılandırmada YOKTUR).
//
//   GET /manifest.json      → sürümdeki manifest.json (imzalı; uygulama imzayı kendisi doğrular)
//   GET /manifest.json.sig  → sürümdeki manifest.json.sig
//   GET /download           → manifest'te adı geçen kurulum dosyası: GitHub'ın kısa ömürlü imzalı CDN adresine 302
//                             (yönlendirme gelmezse dosya akıtılır)
//   başka her şey           → 404
//
// Güvenlik: Worker hiçbir şeye "güvenilir" demez — bütünlüğü uygulama sağlar (ECDSA P-256 imzası + SHA-256). Worker'ın
// işi yalnız özel depodaki dosyaları belirteci açığa çıkarmadan vermek. Hata gövdeleri kısadır; belirteç asla yanıta,
// günlüğe ya da yönlendirme adresine girmez. Sürüm bilgisi ~60 sn önbelleklenir (isolate belleği + Cache API).

const API = "https://api.github.com";
const USER_AGENT = "spvision-update-proxy";
const CACHE_SECONDS = 60;
const MAX_MANIFEST_BYTES = 64 * 1024;
const MAX_SIGNATURE_BYTES = 512;
const MANIFEST = "manifest.json";
const SIGNATURE = "manifest.json.sig";
const ASSET_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.exe$/;
const REPO_NAME = /^[A-Za-z0-9-]{1,39}\/[A-Za-z0-9._-]{1,100}$/;
const CACHE_KEY = "https://spvision-update.cache/latest-release-v1";

let memory = null; // { at, data } — per isolate
let redirectMemory = null; // { at, assetId, location } — GitHub's signed URL lives ~5 min; reused for at most 60 s

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (request.method !== "GET") {
      return notFound();
    }
    try {
      switch (url.pathname) {
        case "/manifest.json":
          return await small(env, ctx, "manifest", "application/json; charset=utf-8");
        case "/manifest.json.sig":
          return await small(env, ctx, "signature", "text/plain; charset=utf-8");
        case "/download":
          return await download(env, ctx);
        default:
          return notFound();
      }
    } catch (error) {
      const status = error instanceof ProxyError ? error.status : 502;
      return text(status, status === 404 ? "Bulunamadı" : status === 503 ? "Yapılandırılmamış" : "Güncelleme sunucusu hatası");
    }
  },
};

class ProxyError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

async function small(env, ctx, field, contentType) {
  const release = await latest(env, ctx);
  if (!release || !release[field]) {
    return notFound();
  }
  return new Response(fromBase64(release[field]), {
    status: 200,
    headers: { "Content-Type": contentType, "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" },
  });
}

async function download(env, ctx) {
  const release = await latest(env, ctx);
  if (!release || !release.installer) {
    return notFound();
  }
  const { id, name, size } = release.installer;
  if (redirectMemory && redirectMemory.assetId === id && Date.now() - redirectMemory.at < CACHE_SECONDS * 1000) {
    return redirect(redirectMemory.location);
  }
  const response = await fetch(`${API}/repos/${repo(env)}/releases/assets/${id}`, {
    headers: githubHeaders(env, "application/octet-stream"),
    redirect: "manual",
  });
  const location = response.headers.get("Location");
  if ([301, 302, 303, 307, 308].includes(response.status) && isHttps(location)) {
    redirectMemory = { at: Date.now(), assetId: id, location };
    return redirect(location);
  }
  if (response.status === 200 && response.body) {
    const headers = {
      "Content-Type": "application/octet-stream",
      "Content-Disposition": `attachment; filename="${name}"`,
      "Cache-Control": "no-store",
      "X-Content-Type-Options": "nosniff",
    };
    const length = response.headers.get("Content-Length");
    if (length) {
      headers["Content-Length"] = length;
    } else if (Number.isSafeInteger(size)) {
      headers["Content-Length"] = String(size);
    }
    return new Response(response.body, { status: 200, headers });
  }
  throw new ProxyError(response.status === 404 ? 404 : 502, `asset ${response.status}`);
}

/**
 * The latest release, cached ~60 s: { manifest, signature } (base64 of the files) and { installer: { id, name, size } } of
 * the asset named in manifest.json. Null when the repository has no published release yet.
 */
async function latest(env, ctx) {
  if (memory && Date.now() - memory.at < CACHE_SECONDS * 1000) {
    return memory.data;
  }
  const cache = typeof caches !== "undefined" ? caches.default : null;
  if (cache) {
    const hit = await cache.match(CACHE_KEY);
    if (hit) {
      const data = await hit.json();
      memory = { at: Date.now(), data };
      return data;
    }
  }

  const data = await readLatest(env);
  memory = { at: Date.now(), data };
  if (cache) {
    const stored = new Response(JSON.stringify(data), {
      headers: { "Content-Type": "application/json", "Cache-Control": `max-age=${CACHE_SECONDS}` },
    });
    const put = cache.put(CACHE_KEY, stored);
    if (ctx && typeof ctx.waitUntil === "function") {
      ctx.waitUntil(put);
    } else {
      await put;
    }
  }
  return data;
}

async function readLatest(env) {
  const response = await fetch(`${API}/repos/${repo(env)}/releases/latest`, { headers: githubHeaders(env, "application/vnd.github+json") });
  if (response.status === 404) {
    return null; // no published (non-draft, non-prerelease) release
  }
  if (!response.ok) {
    throw new ProxyError(502, `releases/latest ${response.status}`);
  }
  const release = await response.json();
  const assets = Array.isArray(release.assets) ? release.assets : [];
  const byName = (name) => assets.find((a) => a && a.name === name && Number.isSafeInteger(a.id));

  const manifestAsset = byName(MANIFEST);
  const signatureAsset = byName(SIGNATURE);
  if (!manifestAsset || !signatureAsset) {
    return null; // a release without the update files is not an update
  }
  const manifest = await readAsset(env, manifestAsset, MAX_MANIFEST_BYTES);
  const signature = await readAsset(env, signatureAsset, MAX_SIGNATURE_BYTES);

  let installer = null;
  try {
    const name = JSON.parse(new TextDecoder().decode(manifest)).asset;
    const asset = typeof name === "string" && ASSET_NAME.test(name) && !name.includes("..") ? byName(name) : null;
    if (asset) {
      installer = { id: asset.id, name: asset.name, size: asset.size };
    }
  } catch {
    installer = null; // the app rejects such a manifest anyway
  }
  return { manifest: toBase64(manifest), signature: toBase64(signature), installer };
}

/** Downloads a small release asset: API (token) → 302 → CDN (no token), size-capped. */
async function readAsset(env, asset, maxBytes) {
  if (Number.isSafeInteger(asset.size) && asset.size > maxBytes) {
    throw new ProxyError(502, "asset too large");
  }
  let response = await fetch(`${API}/repos/${repo(env)}/releases/assets/${asset.id}`, {
    headers: githubHeaders(env, "application/octet-stream"),
    redirect: "manual",
  });
  const location = response.headers.get("Location");
  if ([301, 302, 303, 307, 308].includes(response.status)) {
    if (!isHttps(location)) {
      throw new ProxyError(502, "bad redirect");
    }
    // The signed CDN URL needs no credentials; never send the token there.
    response = await fetch(location, { headers: { "User-Agent": USER_AGENT }, redirect: "follow" });
  }
  if (!response.ok) {
    throw new ProxyError(502, `asset ${response.status}`);
  }
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.length === 0 || bytes.length > maxBytes) {
    throw new ProxyError(502, "asset size");
  }
  return bytes;
}

function repo(env) {
  const name = env.GITHUB_REPO || "SimonPetrus/SPVision";
  if (!REPO_NAME.test(name)) {
    throw new ProxyError(503, "repo");
  }
  return name;
}

function githubHeaders(env, accept) {
  if (!env.GITHUB_TOKEN) {
    throw new ProxyError(503, "token");
  }
  return {
    Accept: accept,
    Authorization: `Bearer ${env.GITHUB_TOKEN}`,
    "User-Agent": USER_AGENT,
    "X-GitHub-Api-Version": "2022-11-28",
  };
}

function isHttps(location) {
  try {
    return typeof location === "string" && new URL(location).protocol === "https:";
  } catch {
    return false;
  }
}

function redirect(location) {
  return new Response(null, { status: 302, headers: { Location: location, "Cache-Control": "no-store" } });
}

function notFound() {
  return text(404, "Bulunamadı");
}

function text(status, body) {
  return new Response(body, {
    status,
    headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" },
  });
}

function toBase64(bytes) {
  let binary = "";
  for (const b of bytes) {
    binary += String.fromCharCode(b);
  }
  return btoa(binary);
}

function fromBase64(text) {
  const binary = atob(text);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i);
  }
  return bytes;
}

/** Tests only: forget the per-isolate caches. */
export function resetForTests() {
  memory = null;
  redirectMemory = null;
}
