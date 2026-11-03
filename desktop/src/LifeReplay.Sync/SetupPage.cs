namespace LifeReplay.Sync;

/// <summary>
/// The phone's setup page (GET /setup?k=token, opened by scanning the QR code in the PC's pairing window).
/// It builds each automation's URL from the page's own address and token and copies it with one tap.
/// Static HTML; the token is read from the address bar by the script and never put into the page by the server.
/// </summary>
internal static class SetupPage
{
    public const string Html = """
<!doctype html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>LifeReplay 단축어 설정</title>
<style>
  :root { --bg:#ffffff; --fg:#1d1d1f; --muted:#6e6e73; --card:#f2f2f7; --accent:#0a84ff; --ok:#1f9d55; }
  @media (prefers-color-scheme: dark) { :root { --bg:#000; --fg:#f5f5f7; --muted:#98989d; --card:#1c1c1e; --ok:#30d158; } }
  body { margin:0; padding:16px; background:var(--bg); color:var(--fg); font:16px/1.5 -apple-system, system-ui, sans-serif; }
  h1 { font-size:22px; margin:4px 0 4px; }
  h2 { font-size:17px; margin:22px 0 8px; }
  p, li { color:var(--fg); }
  .muted { color:var(--muted); font-size:14px; }
  .card { background:var(--card); border-radius:12px; padding:12px 14px; margin:8px 0; }
  ol { padding-left:22px; margin:6px 0; }
  li { margin:4px 0; }
  b.key { background:var(--card); border-radius:6px; padding:0 5px; }
  .app { display:flex; align-items:center; gap:8px; flex-wrap:wrap; }
  .app .name { font-weight:600; min-width:90px; flex:1; }
  button { font:inherit; font-size:15px; border:0; border-radius:10px; padding:9px 12px; background:var(--accent); color:#fff; }
  button.done { background:var(--ok); }
  input { font:inherit; padding:9px 10px; border-radius:10px; border:1px solid var(--muted); background:var(--bg); color:var(--fg); width:100%; box-sizing:border-box; }
  #status { font-weight:600; }
</style>
</head>
<body>
<h1>LifeReplay 단축어 설정</h1>
<p id="status" class="muted">PC 연결 확인 중…</p>

<div class="card">
  <b>앱 하나당 자동화 2개</b> (열릴 때 1개, 닫힐 때 1개). 자동화마다 <b>동작은 1개</b>: 아래에서 복사한 주소를 붙여넣기만 하면 돼요.
</div>

<h2>방법 (예: YouTube 열림)</h2>
<ol>
  <li>아래 목록에서 <b>YouTube</b>의 <b>열림 복사</b>를 누르세요.</li>
  <li><b>단축어</b> 앱 → 아래쪽 <b class="key">자동화</b> 탭 → 오른쪽 위 <b class="key">+</b></li>
  <li><b class="key">앱</b>을 누르고 → <b class="key">선택</b>에서 YouTube 고르기 → <b class="key">완료</b></li>
  <li><b class="key">열림</b>만 체크(닫힘은 끄기), <b class="key">즉시 실행</b> 선택 → <b class="key">다음</b></li>
  <li><b class="key">새로운 빈 자동화</b> → <b class="key">동작 추가</b> → 검색창에 <b>URL</b> 입력 → <b class="key">URL의 콘텐츠 가져오기</b></li>
  <li>파란 글씨 <b class="key">URL</b>을 누르고 → <b>붙여넣기</b> → 오른쪽 위 <b class="key">완료</b></li>
</ol>
<p>그 다음 <b>닫힘 복사</b>를 누르고 똑같이 하되, 4번에서 <b>닫힘</b>만 체크하세요.</p>
<p class="muted">처음 실행될 때 "…ts.net에 연결하도록 허용할까요?"가 뜨면 <b>항상 허용</b>을 누르세요.</p>

<h2>앱 목록</h2>
<div id="apps"></div>

<h2>다른 앱</h2>
<div class="card">
  <input id="custom" placeholder="앱 이름 (예: Naver Webtoon)">
  <div class="app" style="margin-top:8px">
    <button onclick="copyCustom('opened', this)">열림 복사</button>
    <button onclick="copyCustom('closed', this)">닫힘 복사</button>
  </div>
</div>

<p class="muted">이 주소에는 이 아이폰의 접속 키가 들어 있어요. 다른 사람에게 보내지 마세요.
분실했을 때는 PC 트레이 → iPhone app tracking → Revoke all paired iPhones.</p>

<script>
  const k = new URLSearchParams(location.search).get('k') || '';
  const apps = ['YouTube', 'Instagram', 'KakaoTalk', 'TikTok', 'Safari', 'Chrome', 'Netflix', '치지직', 'X', 'Discord'];

  function urlFor(app, ev) {
    return location.origin + '/api/v1/e?app=' + encodeURIComponent(app) + '&ev=' + ev + '&k=' + encodeURIComponent(k);
  }

  async function copy(text, button, label) {
    try { await navigator.clipboard.writeText(text); }
    catch (e) { window.prompt('복사가 안 되면 이 주소를 길게 눌러 복사하세요', text); }
    button.textContent = '✓ 복사됨 · ' + label;
    button.classList.add('done');
  }

  function copyCustom(ev, button) {
    const name = document.getElementById('custom').value.trim();
    if (!name) { alert('앱 이름을 먼저 입력하세요'); return; }
    copy(urlFor(name, ev), button, ev === 'opened' ? '열림' : '닫힘');
  }

  const list = document.getElementById('apps');
  for (const app of apps) {
    const row = document.createElement('div');
    row.className = 'card app';
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = app;
    row.appendChild(name);
    for (const [ev, label] of [['opened', '열림'], ['closed', '닫힘']]) {
      const b = document.createElement('button');
      b.textContent = label + ' 복사';
      b.onclick = () => copy(urlFor(app, ev), b, label);
      row.appendChild(b);
    }
    list.appendChild(row);
  }

  fetch('/api/v1/hello', { headers: { Authorization: 'Bearer ' + k } })
    .then(r => { const s = document.getElementById('status');
      if (r.ok) { s.textContent = '✓ PC와 연결됨'; s.style.color = 'var(--ok)'; }
      else { s.textContent = '⚠ 접속 키가 맞지 않아요. PC에서 QR 코드를 다시 만들어 주세요.'; } })
    .catch(() => { document.getElementById('status').textContent = '⚠ PC에 연결할 수 없어요 (PC와 휴대폰의 Tailscale이 켜져 있는지 확인)'; });
</script>
</body>
</html>
""";
}
