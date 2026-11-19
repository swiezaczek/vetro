'use strict';
const assert = require('assert').strict;
const EventEmitter = require('events');
const crypto = require('crypto');
const {RootAgentServer} = require('../service/root-agent.js');
class Socket extends EventEmitter {
  constructor() { super(); this.writes = []; this.destroyed = false; }
  write(data) { this.writes.push(data); }
  destroy() { this.destroyed = true; this.emit('close'); }
}
function frame(server, socket, payload) {
  const nonce = Buffer.from(socket.writes[0].trim().split('\t')[1], 'hex');
  const mac = crypto.createHmac('sha256', server.secret).update(nonce).update('\x00').update(payload).digest('hex');
  return payload + '\t' + mac + '\n';
}
function authenticate(server, socket) {
  socket.emit('data', Buffer.from(frame(server, socket, 'AUTH\t42\t0\t0\t0\t0\t3fffffffff\tVXNlcg==')));
}
async function main() {
  let failures = 0;
  async function check(name, fn) {
    try { await fn(); console.log('PASS ' + name); }
    catch (e) { failures++; console.error('FAIL ' + name + ': ' + e.message); }
  }
  await check('disconnect rejects pending commands immediately', async () => {
    const server = new RootAgentServer(); const socket = new Socket(); server._handle(socket); authenticate(server, socket);
    const pending = server.execute('id', 1000).then(() => 'resolved', () => 'rejected');
    socket.destroy();
    assert.equal(await Promise.race([pending, new Promise(r => setImmediate(() => r('still pending')))]), 'rejected');
    server.close();
  });
  await check('a second peer cannot replace an authenticated agent', async () => {
    const server = new RootAgentServer(); const first = new Socket(); const second = new Socket();
    server._handle(first); server._handle(second); authenticate(server, first); authenticate(server, second);
    assert.equal(server._conn._socket, first); assert.equal(second.destroyed, true);
    first.destroy(); second.destroy(); server.close();
  });
  await check('AUTH and RESULT in one TCP chunk are both processed', async () => {
    let pending;
    const server = new RootAgentServer({onConnect: () => { pending = server.execute('id', 1000); }});
    const socket = new Socket(); server._handle(socket);
    socket.emit('data', Buffer.from(frame(server, socket, 'AUTH\t42\t0\t0\t0\t0\t3fffffffff\tVXNlcg==') + frame(server, socket, 'RESULT\t1\t0\t0\tb2s=\t')));
    const result = await Promise.race([pending, new Promise(r => setImmediate(() => r(null)))]);
    socket.destroy(); pending.catch(() => {}); server.close();
    assert.ok(result); assert.equal(result.stdout, 'ok');
  });
  await check('malformed RESULT with <6 fields rejects immediately and destroys socket', async () => {
    const server = new RootAgentServer(); const socket = new Socket(); server._handle(socket); authenticate(server, socket);
    const pending = server.execute('id', 1000).then(() => 'resolved', e => e.message);
    socket.emit('data', Buffer.from(frame(server, socket, 'RESULT\t1\t0')));
    assert.equal(await Promise.race([pending, new Promise(r => setImmediate(() => r('still pending')))]), 'invalid root agent result');
    assert.equal(socket.destroyed, true);
    server.close();
  });
  await check('server.close() destroys pending unauthenticated sockets and ES5 timeout validation works', async () => {
    const server = new RootAgentServer(); const unauth = new Socket(); server._handle(unauth);
    assert.equal(server._unauth.size, 1);
    server.close();
    assert.equal(unauth.destroyed, true);
    assert.equal(server._unauth.size, 0);
    const s2 = new RootAgentServer(); const sock2 = new Socket(); s2._handle(sock2); authenticate(s2, sock2);
    await assert.rejects(s2.execute('id', 1.5), /invalid command timeout/);
    await assert.rejects(s2.execute('id', 50), /invalid command timeout/);
    s2.close();
  });
  await check('listen failure clears _server and delivers error to onClose', async () => {
    const net = require('net');
    const blocker = net.createServer();
    await new Promise(resolve => blocker.listen(0, '127.0.0.1', resolve));
    const port = blocker.address().port;
    let errSeen = null;
    const server = new RootAgentServer({ port, onClose: e => { errSeen = e; } });
    await new Promise(resolve => {
      const orig = server.onClose;
      server.onClose = e => { orig(e); resolve(); };
      server.start();
    });
    blocker.close();
    assert.ok(errSeen);
    assert.equal(server.listeningPort(), 0);
    assert.equal(server._server, null);
  });
  process.exitCode = failures ? 1 : 0;
}
main().catch(e => { console.error(e); process.exitCode = 1; });
