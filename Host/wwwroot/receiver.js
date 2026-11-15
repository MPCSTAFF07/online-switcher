'use strict';
(() => {
  const slot = Number(new URLSearchParams(location.search).get('slot'));
  const token = location.hash.slice(1);
  const video = document.getElementById('video');
  const label = document.getElementById('status');
  let active, retry, failures = 0, stopped = false;
  const debug = window.ocsReceiverStatus = { phase: 'waiting', error: '', connection: '', ice: '', frames: 0 };
  const current = s => !stopped && active === s && !s.ended;
  function status(s, text) { if (current(s)) { label.textContent = text; document.body.classList.remove('live'); } }
  function send(s, value) { if (current(s) && s.ws.readyState === WebSocket.OPEN) s.ws.send(JSON.stringify(value)); }
  function report(s, phase, message, level = 'info') {
    if (!current(s)) return;
    debug.phase = phase; if (level === 'error') debug.error = message;
    console[level === 'error' ? 'error' : 'info'](`[OCS ${phase}] ${message}`);
    if (s.sender) send(s, {type: 'diagnostic', to: s.sender, phase, message: String(message).slice(0, 1024), level});
  }
  function clearPeer(s) {
    clearInterval(s.stats); clearTimeout(s.disconnect); clearTimeout(s.handshake);
    if (s.pc) { s.pc.onconnectionstatechange = null; s.pc.onicecandidate = null; s.pc.ontrack = null; s.pc.close(); }
    s.pc = null; s.candidates = [];
    if (active === s) { video.srcObject = null; document.body.classList.remove('live'); }
  }
  function end(s, reason, fatal = false) {
    if (!current(s)) return;
    report(s, 'disconnect', reason, 'error'); status(s, reason);
    s.ended = true; clearInterval(s.heartbeat); clearPeer(s); s.ws.close();
    if (!fatal && !retry && !stopped) {
      const delay = Math.min(15000, 2500 * Math.pow(2, failures++));
      label.textContent = `${reason} / ${delay / 1000}秒後に再接続`;
      retry = setTimeout(() => { retry = null; connect(); }, delay);
    }
  }
  async function offer(s, message) {
    clearPeer(s); s.sender = message.from;
    report(s, 'offer', '映像形式の交渉を開始');
    const pc = s.pc = new RTCPeerConnection({iceServers: s.iceServers});
    pc.onicecandidate = e => { if (current(s) && s.pc === pc && e.candidate) send(s, {type:'ice',to:s.sender,candidate:e.candidate.toJSON()}); };
    pc.oniceconnectionstatechange = () => {
      if (current(s) && s.pc === pc) { debug.ice = pc.iceConnectionState; report(s, 'ice', pc.iceConnectionState); }
    };
    pc.ontrack = e => {
      if (!current(s) || s.pc !== pc) return;
      if (!video.srcObject) video.srcObject = new MediaStream();
      video.srcObject.addTrack(e.track);
      video.play().catch(err => report(s, 'playback', err.message, 'error'));
    };
    pc.onconnectionstatechange = () => {
      if (!current(s) || s.pc !== pc) return;
      debug.connection = pc.connectionState; report(s, 'peer', pc.connectionState);
      if (pc.connectionState === 'connected') {
        clearTimeout(s.handshake); clearTimeout(s.disconnect); failures = 0; debug.error = '';
        document.body.classList.add('live');
      } else if (pc.connectionState === 'failed') end(s, `映像接続に失敗（ICE: ${pc.iceConnectionState}）。回線・TURN設定を確認してください`);
      else if (pc.connectionState === 'disconnected') {
        status(s, '映像接続を回復中'); clearTimeout(s.disconnect);
        s.disconnect = setTimeout(() => { if (current(s) && s.pc === pc && pc.connectionState !== 'connected') end(s, '映像接続が切れました'); }, 8000);
      }
    };
    s.handshake = setTimeout(() => { if (current(s) && s.pc === pc && pc.connectionState !== 'connected') end(s, `接続待ちが30秒を超えました（ICE: ${pc.iceConnectionState}）。回線・TURN設定を確認してください`); }, 30000);
    await pc.setRemoteDescription({type:'offer',sdp:message.sdp});
    if (!current(s) || s.pc !== pc) return;
    for (const candidate of s.candidates) await pc.addIceCandidate(candidate); s.candidates = [];
    const answer = await pc.createAnswer();
    if (!current(s) || s.pc !== pc) return;
    await pc.setLocalDescription(answer);
    if (!current(s) || s.pc !== pc) return;
    send(s, {type:'answer',to:s.sender,sdp:answer.sdp}); report(s, 'answer', '映像形式の応答を送信');
    let lastFrames = -1, stalled = 0;
    s.stats = setInterval(async () => {
      if (!current(s) || s.pc !== pc || pc.connectionState !== 'connected') return;
      try {
        const stats = await pc.getStats(); let frames = 0;
        stats.forEach(r => { if (r.type === 'inbound-rtp' && (r.kind || r.mediaType) === 'video') frames += r.framesDecoded || 0; });
        debug.frames = frames;
        stalled = frames === lastFrames ? stalled + 1 : 0; lastFrames = frames;
        if (stalled >= 5) end(s, '映像フレームを受信できないため再接続します');
      } catch (error) { if (current(s)) report(s, 'stats', error.message, 'error'); }
    }, 2000);
  }
  function connect() {
    if (stopped) return;
    if (!(slot >= 1 && slot <= 6) || !token) { label.textContent = '受信用URLを確認してください'; return; }
    if (typeof RTCPeerConnection === 'undefined') { label.textContent = 'このブラウザーではWebRTCを利用できません。OBSを更新してください'; return; }
    const s = active = {ws:new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/signal`),sender:null,iceServers:[],candidates:[],ended:false};
    let chain = Promise.resolve(); status(s, '接続中');
    s.ws.onopen = () => { send(s, {role:'viewer',slot,token}); s.heartbeat = setInterval(() => send(s,{type:'ping'}),15000); };
    s.ws.onmessage = event => { chain = chain.then(async () => {
      if (!current(s)) return;
      const message = JSON.parse(event.data);
      if (message.type === 'ready') { s.iceServers = message.iceServers || []; status(s, '送信PCの接続待ち'); }
      else if (message.type === 'error') end(s, message.message, true);
      else if (message.type === 'offer') {
        try { await offer(s, message); }
        catch (error) { if (current(s)) { report(s, 'negotiation', `${error.name}: ${error.message}`, 'error'); end(s, `映像形式の交渉に失敗: ${error.message}`); } }
      } else if (message.type === 'ice' && message.candidate) {
        if (s.pc?.remoteDescription) await s.pc.addIceCandidate(message.candidate); else s.candidates.push(message.candidate);
      } else if (message.type === 'left') { clearPeer(s); status(s, '送信PCが切断しました'); s.sender = null; }
    }).catch(error => { if (current(s)) end(s, `${error.name}: ${error.message}`); }); };
    s.ws.onclose = e => { if (current(s)) end(s, `接続受付との通信が終了（${e.code}）${e.reason ? ': ' + e.reason : ''}`); };
    s.ws.onerror = () => { if (current(s)) end(s, '接続受付への通信に失敗しました'); };
  }
  window.addEventListener('beforeunload', () => { stopped = true; clearTimeout(retry); if (active) { active.ended = true; clearInterval(active.heartbeat); clearPeer(active); active.ws.close(); } });
  connect();
})();
