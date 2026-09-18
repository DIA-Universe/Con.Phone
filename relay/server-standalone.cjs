const http = require("node:http");
const crypto = require("node:crypto");

const PORT = Number(process.env.PORT || 8080);
const MAX_PAYLOAD = 2 * 1024 * 1024;
const MAX_ROOMS = Number(process.env.MAX_ROOMS || 5000);
const MAX_CLIENTS_PER_ROOM = Number(process.env.MAX_CLIENTS_PER_ROOM || 4);
const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
const rooms = new Map();
let totalClients = 0;

function validToken(value, min = 32, max = 128) {
  return typeof value === "string" &&
    value.length >= min &&
    value.length <= max &&
    /^[A-Za-z0-9_-]+$/.test(value);
}

function encodeFrame(opcode, payload = Buffer.alloc(0)) {
  const length = payload.length;
  let header;
  if (length < 126) {
    header = Buffer.from([0x80 | opcode, length]);
  } else if (length <= 0xffff) {
    header = Buffer.alloc(4);
    header[0] = 0x80 | opcode;
    header[1] = 126;
    header.writeUInt16BE(length, 2);
  } else {
    header = Buffer.alloc(10);
    header[0] = 0x80 | opcode;
    header[1] = 127;
    header.writeBigUInt64BE(BigInt(length), 2);
  }
  return Buffer.concat([header, payload]);
}

function closeRoomIfEmpty(roomId) {
  const room = rooms.get(roomId);
  if (room && room.clients.size === 0) rooms.delete(roomId);
}

function removeClient(client) {
  if (client.removed) return;
  client.removed = true;
  if (client.room.clients.delete(client)) {
    totalClients = Math.max(0, totalClients - 1);
  }
  closeRoomIfEmpty(client.roomId);
}

function destroyClient(client) {
  removeClient(client);
  try { client.socket.destroy(); } catch {}
}

function forwardBinary(client, payload) {
  for (const peer of client.room.clients) {
    if (peer === client || peer.side === client.side || peer.removed) continue;
    if (!peer.socket.destroyed && peer.socket.writable) {
      peer.socket.write(encodeFrame(0x2, payload));
    }
  }
}

function processMessage(client, opcode, payload) {
  if (opcode === 0x2) {
    if (payload.length > MAX_PAYLOAD) return destroyClient(client);
    forwardBinary(client, payload);
    return;
  }
  // Text messages are intentionally not part of the relay protocol.
  destroyClient(client);
}

function parseFrames(client) {
  while (!client.removed) {
    const data = client.buffer;
    if (data.length < 2) return;

    const b0 = data[0];
    const b1 = data[1];
    const fin = (b0 & 0x80) !== 0;
    const opcode = b0 & 0x0f;
    const masked = (b1 & 0x80) !== 0;
    let length = b1 & 0x7f;
    let offset = 2;

    if (!masked) return destroyClient(client);

    if (length === 126) {
      if (data.length < offset + 2) return;
      length = data.readUInt16BE(offset);
      offset += 2;
    } else if (length === 127) {
      if (data.length < offset + 8) return;
      const big = data.readBigUInt64BE(offset);
      if (big > BigInt(MAX_PAYLOAD)) return destroyClient(client);
      length = Number(big);
      offset += 8;
    }

    const isControl = opcode >= 0x8;
    if (isControl && (!fin || length > 125)) return destroyClient(client);
    if (!isControl && length > MAX_PAYLOAD) return destroyClient(client);

    if (data.length < offset + 4 + length) return;
    const mask = data.subarray(offset, offset + 4);
    offset += 4;
    const payload = Buffer.from(data.subarray(offset, offset + length));
    for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i & 3];
    client.buffer = data.subarray(offset + length);

    if (opcode === 0x8) {
      if (client.socket.writable) client.socket.write(encodeFrame(0x8, payload.subarray(0, 125)));
      return destroyClient(client);
    }
    if (opcode === 0x9) {
      if (client.socket.writable) client.socket.write(encodeFrame(0xA, payload));
      continue;
    }
    if (opcode === 0xA) {
      client.isAlive = true;
      continue;
    }

    if (opcode === 0x0) {
      if (client.fragmentOpcode == null) return destroyClient(client);
      client.fragments.push(payload);
      client.fragmentLength += payload.length;
      if (client.fragmentLength > MAX_PAYLOAD) return destroyClient(client);
      if (fin) {
        const complete = Buffer.concat(client.fragments, client.fragmentLength);
        const originalOpcode = client.fragmentOpcode;
        client.fragments = [];
        client.fragmentLength = 0;
        client.fragmentOpcode = null;
        processMessage(client, originalOpcode, complete);
      }
      continue;
    }

    if (opcode === 0x1 || opcode === 0x2) {
      if (client.fragmentOpcode != null) return destroyClient(client);
      if (fin) {
        processMessage(client, opcode, payload);
      } else {
        client.fragmentOpcode = opcode;
        client.fragments = [payload];
        client.fragmentLength = payload.length;
      }
      continue;
    }

    return destroyClient(client);
  }
}

const server = http.createServer((req, res) => {
  if (req.url === "/health") {
    res.writeHead(200, {
      "content-type": "application/json",
      "cache-control": "no-store"
    });
    res.end(JSON.stringify({ ok: true, rooms: rooms.size, clients: totalClients }));
    return;
  }
  res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
  res.end("not found");
});

server.on("upgrade", (req, socket, head) => {
  let url;
  try {
    url = new URL(req.url, "http://relay.invalid");
  } catch {
    socket.destroy();
    return;
  }

  if (url.pathname !== "/relay") return socket.destroy();

  const roomId = url.searchParams.get("room");
  const auth = url.searchParams.get("auth");
  const side = url.searchParams.get("side");
  const key = req.headers["sec-websocket-key"];

  if (!validToken(roomId, 40, 100) ||
      !validToken(auth, 40, 100) ||
      (side !== "pc" && side !== "phone") ||
      typeof key !== "string") {
    socket.destroy();
    return;
  }

  let room = rooms.get(roomId);
  if (!room) {
    if (rooms.size >= MAX_ROOMS) return socket.destroy();
    room = { auth, clients: new Set() };
    rooms.set(roomId, room);
  } else if (room.auth !== auth) {
    socket.destroy();
    return;
  }
  if (room.clients.size >= MAX_CLIENTS_PER_ROOM) return socket.destroy();

  const accept = crypto
    .createHash("sha1")
    .update(key + GUID)
    .digest("base64");

  socket.write(
    "HTTP/1.1 101 Switching Protocols\r\n" +
    "Upgrade: websocket\r\n" +
    "Connection: Upgrade\r\n" +
    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n"
  );

  const client = {
    socket,
    room,
    roomId,
    side,
    isAlive: true,
    removed: false,
    buffer: head && head.length ? Buffer.from(head) : Buffer.alloc(0),
    fragments: [],
    fragmentLength: 0,
    fragmentOpcode: null
  };
  room.clients.add(client);
  totalClients += 1;

  socket.on("data", chunk => {
    if (client.removed) return;
    client.buffer = client.buffer.length
      ? Buffer.concat([client.buffer, chunk])
      : Buffer.from(chunk);
    if (client.buffer.length > MAX_PAYLOAD + 64 * 1024) return destroyClient(client);
    parseFrames(client);
  });

  socket.on("close", () => removeClient(client));
  socket.on("error", () => removeClient(client));

  if (client.buffer.length) parseFrames(client);
});

const heartbeat = setInterval(() => {
  for (const room of rooms.values()) {
    for (const client of room.clients) {
      if (!client.isAlive) {
        destroyClient(client);
        continue;
      }
      client.isAlive = false;
      if (client.socket.writable) client.socket.write(encodeFrame(0x9));
    }
  }
}, 30000);
heartbeat.unref();

server.listen(PORT, "0.0.0.0", () => {
  console.log("Con.Phone zero-knowledge relay listening on :" + PORT);
});
