(function overscanVideo() {
  "use strict";
  // Overscan BR: injeção universal de vídeo. Roda em toda página (e em iframes do
  // mesmo site) e trabalha só em cima da tag <video>, sem nada de site específico.

  // Política P2P por domínio. "block" desliga o WebRTC da página para o loader P2P
  // cair no HLS puro. Casa por sufixo: "ay267.com" vale para "*.ay267.com".
  const P2P_POLICY = { default: "block", allow: [], block: [] };

  if (window.__ovsbrVideo) { return "already"; }
  window.__ovsbrVideo = true;

  const TAG = "OVERSCAN-VIDEO: ";
  const host = String(location.hostname || "").toLowerCase();
  const casa = (lista) => lista.some((d) => {
    d = String(d).toLowerCase();
    return host === d || host.endsWith("." + d);
  });
  const p2pMode = casa(P2P_POLICY.allow) ? "allow" : casa(P2P_POLICY.block) ? "block" : P2P_POLICY.default;
  const log = (msg) => { try { console.log(TAG + msg); } catch (e) { /* nada */ } };

  // d) Política P2P. Feito primeiro, antes de qualquer script do player que ainda
  // não tenha rodado. Sem RTCPeerConnection.prototype.createDataChannel o loader
  // se declara sem suporte e usa o HLS normal.
  if (p2pMode === "block") {
    const recusa = function () { throw new Error("p2p bloqueado por overscan"); };
    ["RTCPeerConnection", "webkitRTCPeerConnection", "mozRTCPeerConnection"].forEach((n) => {
      try { Object.defineProperty(window, n, { value: recusa, writable: true, configurable: true }); } catch (e) { /* nada */ }
    });
  }

  // Estado da página, no closure.
  let video = null;          // o <video> adotado
  let ultimoSrc = "";        // para a etapa 2 da cascata
  let tentativas = 0;        // retries do play
  let tocou = false;         // já houve 'playing' neste video
  let usuarioPausou = false; // não brigar com quem apertou pause
  let soMudo = false;        // o motor já recusou play com som neste video
  let esperaDesde = 0;       // início do 'waiting' atual
  let etapa = 0;             // próxima etapa da cascata (1 a 3)
  let ultimaEtapa = 0;       // quando a última etapa rodou
  let desistiu = false;      // etapa 4: depois da falha total, não mexe mais
  const fontesUsadas = new Set();
  const inicio = Date.now();

  // Ciclos da cascata sobrevivem ao reload (etapa 3), senão viraria loop.
  const chaveCiclos = "__ovsbrCiclos:" + location.pathname;
  const lerCiclos = () => { try { return Number(sessionStorage.getItem(chaveCiclos)) || 0; } catch (e) { return 0; } };
  const gravarCiclos = (n) => { try { sessionStorage.setItem(chaveCiclos, String(n)); } catch (e) { /* nada */ } };

  const areaVisivel = (el) => {
    const r = el.getBoundingClientRect();
    const w = Math.min(r.right, innerWidth) - Math.max(r.left, 0);
    const h = Math.min(r.bottom, innerHeight) - Math.max(r.top, 0);
    return w > 0 && h > 0 ? w * h : 0;
  };

  // a) Detecção: o <video> de maior área visível. Compara por referência.
  function procurar() {
    let melhor = null;
    let maior = 0;
    document.querySelectorAll("video").forEach((v) => {
      const a = areaVisivel(v);
      if (a > maior) { maior = a; melhor = v; }
    });
    if (melhor && melhor !== video) { adotar(melhor); }
    if (video) { esconderSecundarios(); limparOverlays(); }
    entrarEmIframes();
  }

  function adotar(v) {
    video = v;
    ultimoSrc = v.currentSrc || v.src || "";
    tentativas = 0;
    soMudo = false;
    tocou = !v.paused && v.readyState > 2;
    usuarioPausou = false;
    esperaDesde = 0;
    log("adotado " + Math.round(areaVisivel(v)) + " px2, src " + ultimoSrc.slice(0, 80));
    v.addEventListener("playing", () => {
      if (v !== video) { return; }
      tocou = true; esperaDesde = 0; etapa = 0; gravarCiclos(0);
      if (v.muted && v.__ovsbrMudo) { v.muted = false; v.__ovsbrMudo = false; }
    });
    v.addEventListener("pause", () => { if (v === video && tocou && !v.ended) { usuarioPausou = true; } });
    v.addEventListener("play", () => { if (v === video) { usuarioPausou = false; } });
    v.addEventListener("waiting", () => { if (v === video && !esperaDesde) { esperaDesde = Date.now(); } });
    v.addEventListener("stalled", () => { if (v === video && !esperaDesde) { esperaDesde = Date.now(); } });
    v.addEventListener("error", () => { if (v === video) { recuperar("error " + (v.error ? v.error.code : "?")); } });
    // Só dá play sozinho em video grande; prévia pequena (home do YouTube) fica quieta.
    if (areaVisivel(v) >= innerWidth * innerHeight * 0.25) { iniciar(); }
  }

  // b) Início da reprodução, com som; se o motor recusar, mudo e desmuta no 'playing'.
  function cutucar(v) {
    const r = v.getBoundingClientRect();
    const alvos = [v];
    document.querySelectorAll("[aria-label*='play' i], [class*='play' i]").forEach((el) => {
      if (el === v || el.contains(v)) { return; }
      const o = el.getBoundingClientRect();
      const dentro = o.width > 0 && o.left < r.right && o.right > r.left && o.top < r.bottom && o.bottom > r.top;
      if (dentro && alvos.length < 6) { alvos.push(el); }
    });
    alvos.forEach((el) => {
      ["pointerdown", "mousedown", "pointerup", "mouseup", "click"].forEach((tipo) => {
        try {
          const Ev = tipo.startsWith("pointer") && window.PointerEvent ? PointerEvent : MouseEvent;
          el.dispatchEvent(new Ev(tipo, { bubbles: true, cancelable: true, view: window }));
        } catch (e) { /* nada */ }
      });
    });
  }

  function iniciar() {
    const v = video;
    if (!v || tocou || usuarioPausou || tentativas >= 20) { return; }
    tentativas++;
    if (tentativas > 1) { cutucar(v); }
    let p;
    if (soMudo) { v.muted = true; v.__ovsbrMudo = true; } else { v.muted = false; }
    try { p = v.play(); } catch (e) { p = Promise.reject(e); }
    Promise.resolve(p).catch((e) => {
      if (e && e.name === "NotAllowedError" && v === video) {
        soMudo = true; v.muted = true; v.__ovsbrMudo = true;
        try { return v.play(); } catch (e2) { return null; }
      }
      return null;
    }).catch(() => null);
    setTimeout(() => { if (v === video && !tocou) { iniciar(); } }, 500);
  }

  // c) Recuperação: cascata de fallback, uma etapa por vez, 5 s entre elas.
  function recuperar(motivo) {
    if (!video || desistiu || Date.now() - ultimaEtapa < 5000) { return; }
    ultimaEtapa = Date.now();
    esperaDesde = 0;
    etapa = etapa >= 3 ? 1 : etapa + 1;
    log("travou (" + motivo + "), etapa " + etapa);
    if (etapa === 1) {
      if (!trocarFonte()) { ultimaEtapa = 0; recuperar("sem outra fonte"); } // segue para a etapa 2
    } else if (etapa === 2) {
      const agora = video.currentSrc || video.src || "";
      if (agora && agora !== ultimoSrc) {
        ultimoSrc = agora; // o player já trocou; espera o 'playing'
      } else {
        try { video.load(); } catch (e) { /* nada */ }
        tocou = false; tentativas = 0; iniciar();
      }
    } else {
      const ciclos = lerCiclos() + 1;
      gravarCiclos(ciclos);
      if (ciclos >= 2) {
        const info = { src: video.currentSrc, erro: video.error ? video.error.code + " " + (video.error.message || "") : motivo };
        try { console.warn(TAG + "falha total " + JSON.stringify(info), info); } catch (e) { /* nada */ }
        desistiu = true;
        return; // e para aqui: sem reload infinito
      }
      location.reload();
    }
  }

  // Etapa 1: botões de fonte redundante (Opção 1/2/3, servidor, mirror...).
  function trocarFonte() {
    const re = /op(?:c|\u00e7)(?:a|\u00e3)o|fonte|server|mirror|servidor/i; // escapes: a página pode não ser UTF-8
    const candidatos = Array.from(document.querySelectorAll("button, a, [role='button'], li, span"))
      .filter((el) => {
        const t = (el.textContent || "").trim();
        return t.length > 0 && t.length <= 30 && re.test(t) && el.getClientRects().length > 0 &&
          !el.querySelector("button, a");
      });
    for (const el of candidatos) {
      const t = el.textContent.trim().toLowerCase();
      if (fontesUsadas.has(t)) { continue; }
      fontesUsadas.add(t);
      log("trocando para a fonte '" + t + "'");
      el.click();
      tocou = false; tentativas = 0;
      setTimeout(procurar, 1500);
      return true;
    }
    return false;
  }

  // d) Videos secundários pequenos e parados (o video escondido do loader P2P).
  function esconderSecundarios() {
    if (p2pMode !== "block") { return; }
    const base = areaVisivel(video);
    document.querySelectorAll("video").forEach((v) => {
      if (v === video || v.style.display === "none") { return; }
      if (v.paused && areaVisivel(v) < base * 0.05) { v.style.display = "none"; }
    });
  }

  // e) Higiene de overlay, conservadora: só fixed/absolute/sticky com texto de aviso
  // que cubra mais de 30% do video adotado.
  function limparOverlays() {
    const r = video.getBoundingClientRect();
    const areaVideo = r.width * r.height;
    if (areaVideo <= 0) { return; }
    const re = /travou|clique aqui|aviso|publicidade/i;
    // Só o que está desenhado em cima do video: cinco pontos dele.
    const pontos = [[0.5, 0.5], [0.25, 0.25], [0.75, 0.25], [0.25, 0.75], [0.75, 0.75]];
    const acima = new Set();
    pontos.forEach(([fx, fy]) => {
      const x = r.left + r.width * fx;
      const y = r.top + r.height * fy;
      if (x < 0 || y < 0 || x >= innerWidth || y >= innerHeight) { return; }
      for (const el of document.elementsFromPoint(x, y)) {
        if (el === video) { break; }
        acima.add(el);
      }
    });
    acima.forEach((el) => {
      if (el.contains(video) || el.__ovsbrVisto || el === document.body || el === document.documentElement) { return; }
      const pos = getComputedStyle(el).position;
      if (pos !== "fixed" && pos !== "absolute" && pos !== "sticky") { return; }
      const t = el.textContent || "";
      if (t.length > 300 || !re.test(t)) { return; }
      const o = el.getBoundingClientRect();
      const w = Math.min(o.right, r.right) - Math.max(o.left, r.left);
      const h = Math.min(o.bottom, r.bottom) - Math.max(o.top, r.top);
      if (w > 0 && h > 0 && w * h > areaVideo * 0.3) {
        el.__ovsbrVisto = true;
        el.style.setProperty("display", "none", "important");
        log("overlay escondido: " + t.trim().slice(0, 40));
      }
    });
  }

  // Iframes do mesmo site recebem a mesma injeção; os de outro site o navegador não alcança.
  function entrarEmIframes() {
    const entrar = (f) => {
      try {
        const w = f.contentWindow;
        if (w && !w.__ovsbrVideo && w.document) { w.eval("(" + overscanVideo.toString() + ")()"); }
      } catch (e) { /* outra origem */ }
    };
    document.querySelectorAll("iframe").forEach((f) => {
      // O iframe troca de documento ao carregar: entra de novo a cada load.
      if (!f.__ovsbrOuve) { f.__ovsbrOuve = true; f.addEventListener("load", () => entrar(f)); }
      entrar(f);
    });
  }

  // Relógio único: travamento acima de 8 s dispara a cascata.
  setInterval(() => {
    if (video && !video.isConnected) { video = null; }
    if (video && esperaDesde && !usuarioPausou && Date.now() - esperaDesde > 8000) { recuperar("waiting > 8 s"); }
    // Erro que aconteceu antes de a gente ouvir também conta.
    if (video && video.error && !tocou) { recuperar("error " + video.error.code); }
    if (Date.now() - inicio < 15000 || !video) { procurar(); }
  }, 1000);

  try {
    new MutationObserver(() => {
      if (!video || !video.isConnected) { procurar(); }
    }).observe(document.documentElement, { childList: true, subtree: true });
  } catch (e) { /* nada */ }

  if (document.readyState !== "loading") { procurar(); }
  else { document.addEventListener("DOMContentLoaded", procurar); }
  log("instalado em " + host + ", p2p " + p2pMode);
  return "installed";
})();
