import http from "node:http";
import { WebSocket, WebSocketServer } from "ws";

const port = Number(process.env.PORT || 8080);
const maxPayload = 2 * 1024 * 1024;
const rooms = new Map();

function validToken(value, min = 32, max = 128) {
  return typeof value === "string" &&
    value.length >= min &&
    value.length <= max &&
    /^[A-Za-z0-9_-]+$/.test(value);
}

function closeRoomIfEmpty(roomId) {
  const room = rooms.get(roomId);
  if (room && room.clients.size === 0) rooms.delete(roomId);
}

const server = http.createServer((req, res) => {
  if (req.url === "/health") {
    res.writeHead(200, { "content-type": "application/json", "cache-control": "no-store" });
    res.end(JSON.stringify({ ok: true, rooms: rooms.size }));
    return;
  }

  res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
  res.end("not found");
});

const wss = new WebSocketServer({
  noServer: true,
  maxPayload,
  perMessageDeflate: false,
  clientTracking: false
});

server.on("upgrade", (req, socket, head) => {
  let url;
  try {
    url = new URL(req.url, "http://relay.invalid");
  } catch {
    socket.destroy();
    return;
  }

  if (url.pathname !== "/relay") {
    socket.destroy();
    return;
  }

  const roomId = url.searchParams.get("room");
  const auth = url.searchParams.get("auth");
  const side = url.searchParams.get("side");

  if (!validToken(roomId, 40, 100) ||
      !validToken(auth, 40, 100) ||
      (side !== "pc" && side !== "phone")) {
    socket.destroy();
    return;
  }

  let room = rooms.get(roomId);
  if (!room) {
    room = { auth, clients: new Set() };
    rooms.set(roomId, room);
  } else if (room.auth !== auth) {
    socket.destroy();
    return;
  }

  wss.handleUpgrade(req, socket, head, ws => {
    ws.roomId = roomId;
    ws.side = side;
    room.clients.add(ws);

    ws.on("message", (data, isBinary) => {
      if (!isBinary || data.length > maxPayload) {
        ws.close(1003, "binary encrypted frames only");
        return;
      }

      for (const peer of room.clients) {
        if (peer !== ws && peer.readyState === WebSocket.OPEN && peer.side !== ws.side) {
          peer.send(data, { binary: true });
        }
      }
    });

    ws.on("close", () => {
      room.clients.delete(ws);
      closeRoomIfEmpty(roomId);
    });

    ws.on("error", () => {
      room.clients.delete(ws);
      closeRoomIfEmpty(roomId);
    });
  });
});

server.listen(port, "0.0.0.0", () => {
  console.log(`Con.Phone relay listening on :${port}`);
});
