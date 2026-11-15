const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn, spawnSync } = require('node:child_process');
const { chromium } = require('playwright');

const repo = path.resolve(__dirname, '../..');
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'ocs-media-test-'));
const children = [];
const logs = [];
let browser, page;
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
function child(exe, args, options = {}) {
  const process = spawn(exe, args, { cwd: repo, ...options, stdio: ['ignore', 'pipe', 'pipe'] });
  children.push(process);
  for (const stream of [process.stdout, process.stderr]) stream.on('data', data => { const text = data.toString(); logs.push(text); console.log(text.trimEnd()); });
  process.on('error', error => logs.push(error.message));
  return process;
}

(async () => {
  try {
    assert.equal(process.platform, 'win32', 'This native transport test must run on Windows');
    assert.equal(spawnSync('ffmpeg', ['-version'], { encoding: 'utf8' }).status, 0, 'FFmpeg must be installed for test signals');
    const host = path.join(temp, 'host');
    fs.cpSync(path.join(repo, 'dist/OCS-Link/host'), host, { recursive: true });
    const base = 'http://127.0.0.1:17640';
    const token = () => crypto.randomBytes(24).toString('hex');
    const slots = Array.from({ length: 6 }, (_, i) => ({ Id: i + 1, Name: `Test ${i + 1}`, SendToken: token(), ViewToken: token() }));
    fs.writeFileSync(path.join(host, 'config.json'), JSON.stringify({ AdminPassword: token(), PublicUrl: base, ObsUrl: 'ws://127.0.0.1:4455', ObsPassword: '', IceServers: [], Slots: slots }));
    child(path.join(host, 'OCS-Host.exe'), [], { cwd: host, env: { ...process.env, OCS_URLS: base } });
    let ready = false;
    for (let i = 0; i < 100; i++) { try { if ((await fetch(base)).ok) { ready = true; break; } } catch {} await pause(100); }
    assert.ok(ready, 'isolated receiver host starts');
    const code = Buffer.from(JSON.stringify({ url: base, slot: 1, token: slots[0].SendToken })).toString('base64url');
    child('dotnet', [path.join(repo, 'Tests/bin/Release/net8.0/Tests.dll'), code]);
    browser = await chromium.launch({ channel: 'chrome', headless: true, args: ['--autoplay-policy=no-user-gesture-required'] });
    page = await browser.newPage();
    page.on('console', message => { logs.push(message.text()); console.log('BROWSER', message.type(), message.text()); });
    page.on('pageerror', error => { logs.push(error.message); console.log('BROWSER ERROR', error.message); });
    await page.goto(`${base}/view.html?slot=1#${slots[0].ViewToken}`);
    await page.waitForFunction(() => {
      const v = document.querySelector('video');
      return v.videoWidth === 640 && v.videoHeight === 360 && v.getVideoPlaybackQuality().totalVideoFrames >= 30;
    }, null, { timeout: 45000 });
    const first = await page.evaluate(() => document.querySelector('video').getVideoPlaybackQuality().totalVideoFrames);
    await page.waitForTimeout(5000);
    const result = await page.evaluate(() => {
      const v = document.querySelector('video');
      return { width: v.videoWidth, frames: v.getVideoPlaybackQuality().totalVideoFrames,
        audio: v.srcObject.getAudioTracks().length, debug: window.ocsReceiverStatus };
    });
    console.log('MEDIA RESULT', JSON.stringify(result));
    assert.ok(result.frames > first + 60, 'video continues rendering rather than reconnecting');
    assert.equal(result.audio, 1, 'Opus audio track is present');
    assert.ok(result.debug.audioBytes > 0 && result.debug.audioSamples > 0, 'Opus audio packets and decoded samples arrive');
    assert.ok(!logs.some(line => line.includes('Error processing ICE candidate')), 'native ICE candidates are accepted by Chrome');
    assert.equal(result.debug.connection, 'connected');
    console.log('PASS native H264/Opus offer-answer, ICE, decoded video, audio track, and stable playback');
    // Exercise receiver recreation against the same continuously running sender.
    await page.reload();
    await page.waitForFunction(() => document.querySelector('video').videoWidth === 640, null, { timeout: 45000 });
    console.log('PASS receiver reload reconnects to native sender');
  } catch (error) {
    console.error('MEDIA TEST FAILED', error);
    if (page) try { console.error('RECEIVER STATE', await page.evaluate(() => ({debug:window.ocsReceiverStatus,width:document.querySelector('video').videoWidth,frames:document.querySelector('video').getVideoPlaybackQuality()}))); } catch {}
    console.error(logs.slice(-35).join('\n'));
    process.exitCode = 1;
  } finally {
    await browser?.close();
    for (const process of children.reverse()) if (process.pid) spawnSync('taskkill.exe', ['/PID', String(process.pid), '/T', '/F'], { stdio: 'ignore' });
    fs.rmSync(temp, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
  }
})();
