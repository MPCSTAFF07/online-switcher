'use strict';
(() => {
  const slot = Number(new URLSearchParams(location.search).get('slot'));
  const token = location.hash.slice(1);
  const video = document.getElementById('video');
  const label = document.getElementById('status');
  let ws, pc, sender, candidates = [], retry, heartbeat, generation = 0, statsTimer;
  function status(s) { label.textContent = s; document.body.classList.remove('live'); }
  function clearPeer() { clearInterval(statsTimer); if (pc) { pc.onconnectionstatechange = null; pc.close(); } pc = null; sender = null; candidates = []; video.srcObject = null; document.body.classList.remove('live'); }
  function send(x) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(x)); }
  function reconnect() { if (retry) return; clearPeer(); status('再接続中'); retry = setTimeout(() => { retry = null; connect(); }, 2500); }
  function connect() {
    if (!(slot >= 1 && slot <= 6) || !token) { status('受信用URLを確認してください'); return; }
    const epoch = ++generation;
    ws?.close(); clearInterval(heartbeat);
    ws = new WebSocket(`${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/signal`);
    let iceServers = [];
    let chain = Promise.resolve();
    ws.onopen = () => { send({role:'viewer',slot,token}); heartbeat = setInterval(() => send({type:'ping'}),15000); };
    ws.onmessage = event => { chain = chain.then(async () => {
      if (epoch !== generation) return;
      const m = JSON.parse(event.data);
      if (m.type === 'ready') { iceServers = m.iceServers; status('送信PCの接続待ち'); }
      else if (m.type === 'error') { status(m.message); ws.close(); }
      else if (m.type === 'offer') {
        clearPeer(); sender = m.from;
        pc = new RTCPeerConnection({iceServers}); const current = pc;
        pc.onicecandidate = e => { if(e.candidate) send({type:'ice',to:sender,candidate:e.candidate.toJSON()}); };
        pc.ontrack = e => {
          if (!video.srcObject) video.srcObject = new MediaStream();
          video.srcObject.addTrack(e.track);
          video.play().catch(() => status('音声の自動再生が許可されていません'));
        };
        pc.onconnectionstatechange = () => {
          if (current !== pc) return;
          if (pc.connectionState === 'connected') document.body.classList.add('live');
          else if (pc.connectionState === 'failed') { ws.close(); reconnect(); }
          else if (pc.connectionState === 'disconnected') status('映像の接続を回復中');
        };
        await pc.setRemoteDescription({type:'offer',sdp:m.sdp});
        for (const c of candidates) await pc.addIceCandidate(c); candidates = [];
        const answer = await pc.createAnswer(); await pc.setLocalDescription(answer);
        send({type:'answer',to:sender,sdp:answer.sdp});
        let lastFrames = -1, stalled = 0;
        statsTimer = setInterval(async () => {
          if (pc !== current || pc.connectionState !== 'connected') return;
          const stats = await pc.getStats(); let frames = 0;
          stats.forEach(s => { if (s.type === 'inbound-rtp' && s.kind === 'video') frames += s.framesDecoded || 0; });
          stalled = frames === lastFrames ? stalled + 1 : 0; lastFrames = frames;
          if (stalled >= 5) { status('映像が停止しています。再接続中'); ws.close(); reconnect(); }
        }, 2000);
      } else if (m.type === 'ice' && m.candidate) {
        if (pc?.remoteDescription) await pc.addIceCandidate(m.candidate); else candidates.push(m.candidate);
      } else if (m.type === 'left') { clearPeer(); status('送信PCが切断しました'); }
    }).catch(e => { status(e.message); ws.close(); reconnect(); }); };
    ws.onclose = () => { if (epoch === generation) { clearInterval(heartbeat); reconnect(); } };
    ws.onerror = () => ws.close();
  }
  window.addEventListener('beforeunload',()=>{generation++;clearInterval(heartbeat);clearPeer();ws?.close();});
  connect();
})();
