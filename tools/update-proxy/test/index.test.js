// Worker tests without wrangler: global fetch is mocked; no Cache API (as on *.workers.dev). Run: npm test (node --test)
import { test, beforeEach } from "node:test";
import assert from "node:assert/strict";
import worker, { resetForTests } from "../src/index.js";

const TOKEN = "github_pat_TEST_TOKEN_should_never_leak";
const env = { GITHUB_TOKEN: TOKEN, GITHUB_REPO: "SimonPetrus/SPVision" };
const manifestJson = JSON.stringify({ version: "1.2.0", asset: "SPVision-Kurulum-1.2.0.exe" });
const signature = "c2lnbmF0dXJl\n";

let calls;
let routes;

function release(assets) {
  return new Response(JSON.stringify({ tag_name: "v1.2.0", assets }), { status: 200, headers: { "Content-Type": "application/json" } });
}

const defaultAssets = [
  { id: 11, name: "manifest.json", size: manifestJson.length },
  { id: 12, name: "manifest.json.sig", size: signature.length },
  { id: 13, name: "SPVision-Kurulum-1.2.0.exe", size: 123456 },
];

function defaultRoutes() {
  return {
    "https://api.github.com/repos/SimonPetrus/SPVision/releases/latest": () => release(defaultAssets),
    "https://api.github.com/repos/SimonPetrus/SPVision/releases/assets/11": () =>
      new Response(null, { status: 302, headers: { Location: "https://cdn.example/manifest?sig=1" } }),
    "https://api.github.com/repos/SimonPetrus/SPVision/releases/assets/12": () =>
      new Response(null, { status: 302, headers: { Location: "https://cdn.example/sig?sig=1" } }),
    "https://api.github.com/repos/SimonPetrus/SPVision/releases/assets/13": () =>
      new Response(null, { status: 302, headers: { Location: "https://cdn.example/installer?sig=1" } }),
    "https://cdn.example/manifest?sig=1": () => new Response(manifestJson),
    "https://cdn.example/sig?sig=1": () => new Response(signature),
  };
}

beforeEach(() => {
  resetForTests();
  calls = [];
  routes = defaultRoutes();
  globalThis.fetch = async (url, init = {}) => {
    calls.push({ url: String(url), init });
    const route = routes[String(url)];
    if (!route) {
      return new Response("not found", { status: 404 });
    }
    return route();
  };
});

const get = (path, method = "GET") => worker.fetch(new Request(`https://spvision-update.example.workers.dev${path}`, { method }), env, {});

async function assertNoToken(response) {
  const body = await response.clone().text();
  assert.ok(!body.includes(TOKEN), "token in body");
  for (const [, value] of response.headers) {
    assert.ok(!value.includes(TOKEN), "token in header");
  }
}

test("manifest and signature come from the latest release", async () => {
  const manifest = await get("/manifest.json");
  await assertNoToken(manifest);
  assert.equal(manifest.status, 200);
  assert.equal(await manifest.text(), manifestJson);
  assert.equal(manifest.headers.get("Cache-Control"), "no-store");
  const sig = await get("/manifest.json.sig");
  await assertNoToken(sig);
  assert.equal(sig.status, 200);
  assert.equal(await sig.text(), signature);
});

test("GitHub API calls carry the token and a User-Agent; the CDN never gets the token", async () => {
  await get("/manifest.json");
  for (const call of calls) {
    const headers = call.init.headers || {};
    if (call.url.startsWith("https://api.github.com/")) {
      assert.equal(headers.Authorization, `Bearer ${TOKEN}`);
      assert.ok(headers["User-Agent"]);
    } else {
      assert.equal(headers.Authorization, undefined);
      assert.ok(headers["User-Agent"]);
    }
  }
  const assetCall = calls.find((c) => c.url.endsWith("/releases/assets/11"));
  assert.equal(assetCall.init.redirect, "manual");
  assert.equal(assetCall.init.headers.Accept, "application/octet-stream");
});

test("download redirects to the signed CDN URL of the asset named in the manifest", async () => {
  const response = await get("/download");
  assert.equal(response.status, 302);
  assert.equal(response.headers.get("Location"), "https://cdn.example/installer?sig=1");
  assert.equal(response.headers.get("Cache-Control"), "no-store");
  await assertNoToken(response);
  const call = calls.find((c) => c.url.endsWith("/releases/assets/13"));
  assert.equal(call.init.redirect, "manual");
});

test("download streams the file when GitHub answers without a redirect", async () => {
  routes["https://api.github.com/repos/SimonPetrus/SPVision/releases/assets/13"] = () =>
    new Response("MZ-installer", { status: 200, headers: { "Content-Length": "12" } });
  const response = await get("/download");
  assert.equal(response.status, 200);
  assert.equal(await response.text(), "MZ-installer");
  assert.equal(response.headers.get("Content-Type"), "application/octet-stream");
});

test("a redirect to a non-https address is refused", async () => {
  routes["https://api.github.com/repos/SimonPetrus/SPVision/releases/assets/13"] = () =>
    new Response(null, { status: 302, headers: { Location: "http://cdn.example/installer" } });
  const response = await get("/download");
  assert.equal(response.status, 502);
});

test("release info is cached: the second request does not ask GitHub for the release again", async () => {
  await get("/manifest.json");
  await get("/manifest.json.sig");
  await get("/download");
  await get("/download");
  assert.equal(calls.filter((c) => c.url.endsWith("/releases/latest")).length, 1);
  assert.equal(calls.filter((c) => c.url.endsWith("/releases/assets/13")).length, 1); // signed URL reused briefly
});

test("everything else is 404", async () => {
  for (const path of ["/", "/manifest", "/download/x", "/releases/latest", "/../manifest.json.sig.x"]) {
    assert.equal((await get(path)).status, 404, path);
  }
  assert.equal((await get("/manifest.json", "POST")).status, 404);
  assert.equal((await get("/download", "HEAD")).status, 404);
});

test("no published release: 404", async () => {
  routes["https://api.github.com/repos/SimonPetrus/SPVision/releases/latest"] = () => new Response("{}", { status: 404 });
  assert.equal((await get("/manifest.json")).status, 404);
  assert.equal((await get("/download")).status, 404);
});

test("a release without update files is not offered", async () => {
  routes["https://api.github.com/repos/SimonPetrus/SPVision/releases/latest"] = () => release([defaultAssets[2]]);
  assert.equal((await get("/manifest.json")).status, 404);
});

test("an asset name with a path is never looked up", async () => {
  const evil = JSON.stringify({ asset: "../SPVision-Kurulum-1.2.0.exe" });
  routes["https://cdn.example/manifest?sig=1"] = () => new Response(evil);
  assert.equal((await get("/download")).status, 404);
  assert.ok(!calls.some((c) => c.url.endsWith("/releases/assets/13")));
});

test("oversized manifest is refused", async () => {
  routes["https://api.github.com/repos/SimonPetrus/SPVision/releases/latest"] = () =>
    release([{ ...defaultAssets[0], size: 70000 }, defaultAssets[1], defaultAssets[2]]);
  assert.equal((await get("/manifest.json")).status, 502);
});

test("missing token: 503 without details", async () => {
  const response = await worker.fetch(new Request("https://x.workers.dev/manifest.json"), { GITHUB_REPO: "SimonPetrus/SPVision" }, {});
  assert.equal(response.status, 503);
  assert.equal(await response.text(), "Yapılandırılmamış");
});

test("GitHub errors give a short 502 without the token", async () => {
  routes["https://api.github.com/repos/SimonPetrus/SPVision/releases/latest"] = () => new Response(`bad credentials ${TOKEN}`, { status: 401 });
  const response = await get("/manifest.json");
  assert.equal(response.status, 502);
  await assertNoToken(response);
});
