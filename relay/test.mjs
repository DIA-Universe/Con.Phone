import { spawn } from "node:child_process";
import { WebSocket } from "ws";

const port = 18081;
const child = spawn(process.execPath, ["server.js"], {
  cwd: new URL(".", import.meta.url),
  env: { ...process.env, PORT: String(port) },
  stdio: ["ignore", "pipe", "pipe"]
});

function delay(ms) {
  return new Promise(resolve => setTimeout(resolve, ms));
}

async function waitHealth() {
  for (let i = 0; i < 50; i++) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/health`);
      if (res.ok) return;
    } catch {}
    await delay(100);
  }
  throw new Error("relay did not start");
}

function connect(side, room = "R".repeat(48), auth = "A".repeat(48)) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(
      `ws://127.0.0.1:${port}/relay?room=${room}&auth=${auth}&side=${side}`
    );
    ws.once("open", () => resolve(ws));
    ws.once("error", reject);
  });
}

try {
  await waitHealth();

  const pc = await connect("pc");
  const phone = await connect("phone");

  const payload = Buffer.from("encrypted-frame-fixture");
  const received = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("relay timeout")), 3000);
    phone.once("message", data => {
      clearTimeout(timer);
      resolve(Buffer.from(data));
    });
  });

  pc.send(payload);
  const result = await received;
  if (!result.equals(payload)) throw new Error("relay changed payload");

  const bad = new WebSocket(
    `ws://127.0.0.1:${port}/relay?room=${"R".repeat(48)}&auth=${"B".repeat(48)}&side=phone`
  );
  const rejected = await new Promise(resolve => {
    bad.once("open", () => resolve(false));
    bad.once("error", () => resolve(true));
    bad.once("close", () => resolve(true));
  });
  if (!rejected) throw new Error("wrong room auth was accepted");

  pc.close();
  phone.close();
  console.log("relay integration test passed");
} finally {
  child.kill("SIGTERM");
}
