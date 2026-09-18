import { spawn } from "node:child_process";

const port = 18081;
const child = spawn(process.execPath, ["server-standalone.cjs"], {
  cwd: new URL(".", import.meta.url),
  env: { ...process.env, PORT: String(port) },
  stdio: ["ignore", "pipe", "pipe"]
});

const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

async function waitHealth() {
  for (let i = 0; i < 80; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/health`);
      if (res.ok) {
        const body = await res.json();
        if (body.ok === true) return;
      }
    } catch {}
    await delay(50);
  }
  throw new Error("standalone relay did not become healthy");
}

function openSocket(side, room = "R".repeat(48), auth = "A".repeat(48)) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(
      `ws://127.0.0.1:${port}/relay?room=${room}&auth=${auth}&side=${side}`
    );
    const timer = setTimeout(() => reject(new Error("websocket open timeout")), 3000);
    ws.addEventListener("open", () => {
      clearTimeout(timer);
      resolve(ws);
    }, { once: true });
    ws.addEventListener("error", event => {
      clearTimeout(timer);
      reject(event.error ?? new Error("websocket open error"));
    }, { once: true });
  });
}

function receiveBinary(ws) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("relay receive timeout")), 3000);
    ws.addEventListener("message", async event => {
      clearTimeout(timer);
      if (typeof event.data === "string") {
        reject(new Error("expected binary relay frame"));
        return;
      }
      const data = event.data instanceof Blob
        ? new Uint8Array(await event.data.arrayBuffer())
        : new Uint8Array(event.data);
      resolve(data);
    }, { once: true });
  });
}

try {
  await waitHealth();
  const pc = await openSocket("pc");
  const phone = await openSocket("phone");

  const payload = new TextEncoder().encode("encrypted-frame-fixture");
  const received = receiveBinary(phone);
  pc.send(payload);
  const result = await received;
  if (result.length !== payload.length ||
      result.some((byte, index) => byte !== payload[index])) {
    throw new Error("standalone relay changed encrypted payload");
  }

  let rejected = false;
  try {
    const bad = await openSocket("phone", "R".repeat(48), "B".repeat(48));
    await delay(150);
    rejected = bad.readyState !== WebSocket.OPEN;
    bad.close();
  } catch {
    rejected = true;
  }
  if (!rejected) throw new Error("standalone relay accepted wrong room auth");

  pc.close();
  phone.close();
  console.log("standalone relay integration test passed");
} finally {
  child.kill("SIGTERM");
}
