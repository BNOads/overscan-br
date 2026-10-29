namespace Overscan
{
    /// <summary>
    /// JavaScript injected into every page. Chromium-EFL exposes no API to feed
    /// synthetic mouse events into a view (only ewk's real input path and
    /// FeedMouseWheel in the NUI binding), so the D-pad cursor is implemented
    /// inside the page: we position an overlay element and dispatch the
    /// mouse/pointer events ourselves. Anchors and buttons activate normally,
    /// because a dispatched click still runs the element's default action.
    ///
    /// The one place a dispatched event cannot reach is inside a cross-origin
    /// <c>&lt;iframe&gt;</c> — a captcha, an embedded sign-in. A same-origin frame
    /// is entered here (see <c>descend</c>); a cross-origin one is reported back as
    /// <c>FRAME:</c>, with the point it happened at, so the app can push a real
    /// event in from outside the page instead — <see cref="NativeMouse"/> on the
    /// ewk builds, and <c>NuiInspectorInput</c> on the NUI one.
    ///
    /// That feed is blind: from the app's side it either arrives or it does not, and
    /// both look the same. So the script watches for the two things that can only be
    /// true if it did — an event with <c>isTrusted</c>, and a cross-origin frame
    /// taking focus — and reports them through <c>native()</c>. Issue #20 is the
    /// case where the feed goes out and nothing happens, which without those two
    /// facts is unanswerable.
    /// </summary>
    internal static class PageScript
    {
        public const string Namespace = "__ovs";

        /// <summary>Separator used by the probe payload (never appears in a UA).</summary>
        public const char FieldSeparator = '\u0001';

        /// <summary>
        /// Installs the cursor + helper API. Idempotent, so it is safe to re-run on
        /// every load-finished and after in-page navigation.
        /// </summary>
        public static string Install(string bridgeName)
        {
            return Raw.Replace("__BRIDGE__", bridgeName);
        }

        /// <summary>
        /// Reports what the page actually sees, over the JS message bridge. This is
        /// the measurement that proves the UA override took effect.
        /// </summary>
        public static string Probe(string bridgeName)
        {
            return @"(function(){try{
  var p=[navigator.userAgent,String(window.innerWidth),String(window.innerHeight),
         String(window.devicePixelRatio),document.title||'',location.href];
  window.__BRIDGE__.postMessage('probe'+'\u0001'+p.join('\u0001'));
}catch(e){}})();".Replace("__BRIDGE__", bridgeName);
        }

        private const string Raw = @"
(function(){
  if (window.__ovs) { window.__ovs.install(); return; }

  /* hidden is whether the app has asked for the arrow to stay off the screen
     because it is drawing the pointer itself. install() has to honour it, or
     every re-install puts the arrow back — see the visibilitychange hook at the
     bottom and issue #91. */
  var st = { x: 0, y: 0, over: null, trusted: null, frameFocus: null, hidden: false };

  function clamp(v, hi) { return v < 0 ? 0 : (v > hi ? hi : v); }

  function makeCursor() {
    var c = document.createElement('div');
    c.id = '__ovs_cursor';
    /* An arrow, not a dot: on a TV a dot gets lost in page content, and an arrow
       reads as 'this is a pointer' immediately. Drawn with borders so it needs no
       image and no clip-path (the 2017 engines are fussy about both), and outlined
       via drop-shadow so it stays visible on dark and light pages alike. */
    c.style.cssText =
      'position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;' +
      'pointer-events:none;border-style:solid;' +
      'border-width:26px 15px 0 0;' +
      'border-color:#ffffff transparent transparent transparent;' +
      '-webkit-filter:drop-shadow(1px 1px 0 rgba(0,0,0,0.9)) drop-shadow(0 3px 5px rgba(0,0,0,0.45));' +
      'filter:drop-shadow(1px 1px 0 rgba(0,0,0,0.9)) drop-shadow(0 3px 5px rgba(0,0,0,0.45));' +
      '-webkit-transition:transform 70ms linear;transition:transform 70ms linear;';
    return c;
  }

  /* A ring that expands and fades where the click landed, so a press is not
     silent when a page takes a moment to react. */
  function pulse() {
    try {
      var p = document.createElement('div');
      p.style.cssText =
        'position:fixed;left:0;top:0;width:14px;height:14px;margin:-7px 0 0 -7px;' +
        'border:3px solid rgba(72,160,255,0.95);border-radius:50%;' +
        'z-index:2147483646;pointer-events:none;' +
        'transform:translate(' + st.x + 'px,' + st.y + 'px) scale(1);' +
        '-webkit-transition:all 320ms ease-out;transition:all 320ms ease-out;';
      (document.body || document.documentElement).appendChild(p);
      setTimeout(function () {
        p.style.opacity = '0';
        p.style.transform = 'translate(' + st.x + 'px,' + st.y + 'px) scale(3.2)';
      }, 10);
      setTimeout(function () { if (p.parentNode) { p.parentNode.removeChild(p); } }, 420);
    } catch (_) {}
  }

  function root() { return document.body || document.documentElement; }

  function place() {
    if (!st.el || !st.el.parentNode) { return; }
    st.el.style.transform = 'translate(' + st.x + 'px,' + st.y + 'px)';
  }

  /* x and y default to the cursor, but a click inside a same-origin frame has to
     report coordinates in *that* frame's space or the page reads them wrongly.
     Returns false when the page cancelled the event. */
  function fire(type, target, x, y) {
    if (!target) { return true; }
    if (x === undefined) { x = st.x; }
    if (y === undefined) { y = st.y; }
    var e;
    var init = { bubbles: true, cancelable: true, view: window,
                 clientX: x, clientY: y, button: 0, buttons: type === 'mousedown' ? 1 : 0 };
    try {
      e = new MouseEvent(type, init);
    } catch (_) {
      e = document.createEvent('MouseEvents');
      e.initMouseEvent(type, true, true, window, type === 'click' ? 1 : 0,
                       0, 0, x, y, false, false, false, false, 0, null);
    }
    try { return target.dispatchEvent(e); } catch (_) { return true; }
  }

  /* Overscan BR: a real mouse reaches a page as a pointer event first and a mouse
     event second, and a growing share of pages only listen for the first. Menus
     that open on pointerdown, players whose controls answer pointerup, sliders
     that track pointermove: to those, mousedown/mouseup/click alone is a pointer
     that never pressed anything, and from the sofa that is a button that does
     not work. The pair goes out in the order chromium uses for a real mouse. */
  var POINTER = { mousedown: 'pointerdown', mouseup: 'pointerup', mousemove: 'pointermove',
                  mouseover: 'pointerover', mouseout: 'pointerout',
                  mouseenter: 'pointerenter', mouseleave: 'pointerleave' };

  function firePointer(type, target, x, y) {
    if (!target || !window.PointerEvent) { return true; }
    if (x === undefined) { x = st.x; }
    if (y === undefined) { y = st.y; }
    var flat = type === 'pointerenter' || type === 'pointerleave';
    var press = type === 'pointerdown' || type === 'pointerup';
    var e;
    try {
      e = new PointerEvent(type, {
        bubbles: !flat, cancelable: !flat, composed: true, view: window,
        clientX: x, clientY: y, button: press ? 0 : -1, buttons: type === 'pointerdown' ? 1 : 0,
        pointerId: 1, pointerType: 'mouse', isPrimary: true, width: 1, height: 1,
        pressure: type === 'pointerdown' ? 0.5 : 0
      });
    } catch (_) { return true; }
    try { return target.dispatchEvent(e); } catch (_) { return true; }
  }

  /* The mouse event and the pointer event in front of it, for everything but the
     press itself (see press()). */
  function hover(type, target) {
    firePointer(POINTER[type], target);
    fire(type, target);
  }

  /* One press: pointerdown, mousedown, pointerup, mouseup, click. A page that
     cancels pointerdown gets no mousedown or mouseup, as with a real mouse, so a
     widget that handles the press on pointerdown and on mousedown alike is not
     pressed twice. The click goes out regardless, as it does in chromium. */
  function press(target, x, y) {
    var mouse = firePointer('pointerdown', target, x, y);
    if (mouse) { fire('mousedown', target, x, y); }
    firePointer('pointerup', target, x, y);
    if (mouse) { fire('mouseup', target, x, y); }
    fire('click', target, x, y);
  }

  function at() {
    try { return document.elementFromPoint(st.x, st.y); } catch (_) { return null; }
  }

  function isFrame(node) {
    var tag = node && node.tagName;
    return tag === 'IFRAME' || tag === 'FRAME' || tag === 'OBJECT' || tag === 'EMBED';
  }

  /* Hit-tests inside a frame the page is allowed to touch, translating the
     cursor into the frame's own coordinates. Returns null for a cross-origin
     frame — reading contentDocument there throws, and nothing in this script can
     reach inside it. That case is handled natively instead (see NativeMouse).
     Loops, because a captcha is often a frame inside a frame. */
  function descend(frame, x, y) {
    for (var depth = 0; depth < 4; depth++) {
      var doc;
      try { doc = frame.contentDocument; } catch (_) { return null; }
      if (!doc || !doc.elementFromPoint) { return null; }

      var box;
      try { box = frame.getBoundingClientRect(); } catch (_) { return null; }
      x = x - box.left - (frame.clientLeft || 0);
      y = y - box.top - (frame.clientTop || 0);

      var el;
      try { el = doc.elementFromPoint(x, y); } catch (_) { return null; }
      if (!el) { return null; }
      if (isFrame(el)) { frame = el; continue; }
      return { el: el, x: x, y: y };
    }

    return null;
  }

  var CLICKABLE = 'a,button,input,select,textarea,summary,label,' +
                  '[role=button],[role=link],[role=menuitem],[role=tab],[onclick],[tabindex]';

  /* Nearest ancestor (or self) that actually responds to a click. */
  function interactive(node) {
    try {
      if (node.closest) { return node.closest(CLICKABLE); }
    } catch (_) {}

    /* Fallback for engines without closest(), and for SVG nodes on old ones. */
    var n = node;
    while (n && n !== document.body) {
      try {
        if (n.matches && n.matches(CLICKABLE)) { return n; }
      } catch (_) {}
      n = n.parentElement || n.parentNode;
      if (n && n.nodeType !== 1) { return null; }
    }
    return null;
  }

  /* Overscan BR: whether a node *looks* clickable, which is how a sighted user
     decides. Pages put their click handlers on plain divs and spans all the time
     (a player's play overlay, a card, a tab strip), none of which the selector
     above can name, and nearly all of them say so with cursor:pointer. */
  function looksClickable(node) {
    for (var n = node, depth = 0; n && n.nodeType === 1 && depth < 6; depth++) {
      var s;
      try { s = window.getComputedStyle(n); } catch (_) { s = null; }
      if (s && s.cursor === 'pointer') { return true; }
      n = n.parentElement;
    }
    return false;
  }

  /* A video, a canvas or a frame is a surface, not a miss: the page answers
     clicks on it however it likes, and a press there must stay there. */
  function isSurface(node) {
    var tag = node && node.tagName;
    return tag === 'VIDEO' || tag === 'CANVAS' || tag === 'AUDIO' || isFrame(node);
  }

  function pressable(node) {
    if (!node) { return null; }
    var t = interactive(node);
    if (t) { return t; }
    return looksClickable(node) ? node : null;
  }

  /* Overscan BR: the nearest thing worth pressing, when the pointer is just beside
     one. The pointer moves 13 px at a time, a page's close button can be 16 px
     across, and a press that stops a few pixels short of it lands on nothing at
     all, which from the sofa reads as a button that will not press. So a press on
     empty page looks a short way around itself, closest ring first, and goes to
     what it finds there. Never from a surface (see isSurface), and never into a
     frame: what is inside another origin's frame is not ours to guess at. */
  var NEAR = [12, 24];
  var AROUND = [[1, 0], [-1, 0], [0, 1], [0, -1], [0.7, 0.7], [-0.7, 0.7], [0.7, -0.7], [-0.7, -0.7]];

  function nearby(x, y) {
    for (var r = 0; r < NEAR.length; r++) {
      for (var d = 0; d < AROUND.length; d++) {
        var px = clamp(x + AROUND[d][0] * NEAR[r], window.innerWidth - 1);
        var py = clamp(y + AROUND[d][1] * NEAR[r], window.innerHeight - 1);
        var el;
        try { el = document.elementFromPoint(px, py); } catch (_) { el = null; }
        if (!el || isSurface(el)) { continue; }
        var t = pressable(el);
        if (t) { return { el: t, x: px, y: py }; }
      }
    }
    return null;
  }

  /* What a press here would do, for the pointer to show before it is pressed:
     'c' presses something, 'n' presses something just beside it (see nearby),
     'f' goes into another origin's frame, '' does nothing we can see. */
  function kindAt(hit) {
    if (!hit) { return ''; }
    if (isFrame(hit)) { return 'f'; }
    if (pressable(hit)) { return 'c'; }
    if (isSurface(hit)) { return ''; }
    return nearby(st.x, st.y) ? 'n' : '';
  }

  function scrollableAncestor(node) {
    while (node && node !== document.body && node !== document.documentElement) {
      var s;
      try { s = window.getComputedStyle(node); } catch (_) { s = null; }
      if (s && (s.overflowY === 'auto' || s.overflowY === 'scroll') &&
          node.scrollHeight > node.clientHeight + 4) {
        return node;
      }
      node = node.parentElement;
    }
    return null;
  }

  window.__ovs = {
    /* Puts the arrow in the DOM, or back in it. It does NOT decide whether the
       arrow is showing: that is hide()/show()'s, and it is remembered, because
       install() is re-run by whatever wipes the overlay out of the document
       (single-page navigations, and the visibilitychange hook below) and none of
       those callers know who is drawing the pointer. Issue #91 was the arrow
       coming back on a visibilitychange while the app was drawing its own dot on
       the same spot: two pointers, one on top of the other, on a set where
       nothing had gone wrong. */
    install: function () {
      if (!st.el) { st.el = makeCursor(); }
      if (!st.el.parentNode && root()) { root().appendChild(st.el); }
      if (!st.x && !st.y) { st.x = window.innerWidth / 2; st.y = window.innerHeight / 2; }
      st.el.style.display = st.hidden ? 'none' : 'block';
      place();
      return 1;
    },

    hide: function () {
      st.hidden = true;
      if (st.el) { st.el.style.display = 'none'; }
    },

    /* The only way the arrow comes back once hidden: the app asking for it. */
    show: function () {
      st.hidden = false;
      return window.__ovs.install();
    },

    /* The address of the link under the pointer, or ''. The start screen's tiles
       are plain links (see HomePage), and this is how the app acts on the tile
       somebody is pointing at without following it.

       Issue #80 is why it exists. A favourite kept by typing its address could be
       added and never taken away: removing one meant typing the same address
       again, and the tile does not show an address — it shows a host and a title,
       neither of which is what the favourite is stored as. So the reporter had two
       tiles that read identically, pointed at different pages, and could not be
       removed by any spelling he could see. Pointing at the thing you want gone is
       the gesture that cannot go wrong, and the pointer already knows how to
       hit-test. */
    linkAt: function () {
      var n = at();
      for (var depth = 0; n && depth < 8; depth++) {
        if (n.tagName === 'A' && n.getAttribute && n.getAttribute('href')) {
          /* Which list the tile is in, then the address. The same page is very
             often a favourite *and* a recent visit, so a caller told only the
             address cannot know which of the two tiles the pointer was on — and
             taking a page out of the wrong list is indistinguishable, from a sofa,
             from a key that did nothing.

             .href is resolved against the page's base, which is what the app
             stored; the attribute is the fallback for engines that do not give a
             resolved one. */
          return String(n.getAttribute('data-kind') || 'link') + ' ' +
                 String(n.href || n.getAttribute('href'));
        }

        n = n.parentElement || n.parentNode;
        if (n && n.nodeType !== 1) { return ''; }
      }

      return '';
    },

    /* fx, fy are fractions of the viewport, so the native side never needs to
       know the page's CSS pixel size or zoom level. */
    move: function (fx, fy) {
      st.x = clamp(fx * window.innerWidth, window.innerWidth - 1);
      st.y = clamp(fy * window.innerHeight, window.innerHeight - 1);
      place();
      var t = at();
      if (t !== st.over) {
        if (st.over) { hover('mouseout', st.over); hover('mouseleave', st.over); }
        if (t) { hover('mouseover', t); hover('mouseenter', t); }
        st.over = t;
      }
      hover('mousemove', t);

      /* Overscan BR: what a press would do here, so the app can show it on the
         pointer before anything is pressed (see kindAt). */
      return kindAt(t);
    },

    click: function () {
      var hit = at();
      if (!hit) { return 'nothing under cursor'; }

      /* An embedded frame is checked before anything else: elementFromPoint stops
         at the frame element, so a captcha, an embedded sign-in or a payment
         widget would otherwise get a click dispatched on its container and
         nothing at all would happen (issue #15). */
      var x = st.x;
      var y = st.y;
      var framed = false;
      if (isFrame(hit)) {
        var inner = descend(hit, st.x, st.y);
        if (!inner) {
          /* Cross-origin: unreachable from script. Ask the native side to push a
             real mouse event in, which chromium routes into the frame itself.
             The point goes back with it, in this page's own CSS pixels: that is
             the space the engine hit-tests in, and it is not the window's the
             moment a page is zoomed or the viewport has been forced (key 6). */
          pulse();
          return 'FRAME:' + hit.tagName + '@' + Math.round(st.x) + ',' + Math.round(st.y);
        }

        hit = inner.el;
        x = inner.x;
        y = inner.y;
        framed = true;
      }

      /* elementFromPoint returns the topmost node, which on icon buttons is a
         decorative <svg>/<path>/<span> whose parent carries the behaviour.
         Dispatching on that child does nothing, so climb to the real target. */
      var t = interactive(hit);

      /* Overscan BR: a press on empty page goes to the button just beside it, if
         there is one (see nearby). Only in this document: the ring is hit-tested
         in this page's coordinates. */
      var near = null;
      if (!t && !framed && !isSurface(hit) && !looksClickable(hit)) {
        near = nearby(x, y);
        if (near) {
          t = near.el;
          x = near.x;
          y = near.y;
        }
      }
      t = t || hit;

      pulse();
      press(t, x, y);

      /* A text field is deliberately NOT focused here — focusing raises the TV's
         IME. It is marked instead: outlined so the user can see where text will
         land, and remembered so the on-screen grid types into it. */
      var isField = t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable;
      if (isField) {
        if (st.field && st.field !== t) {
          try { st.field.style.outline = st.fieldOutline || ''; } catch (_) {}
        }
        st.field = t;
        try {
          st.fieldOutline = t.style.outline || '';
          t.style.outline = '3px solid #48a0ff';
        } catch (_) {}
      }

      var d = (isField ? 'FIELD:' : '') + (t.tagName || '?');
      if (t.id) { d += '#' + t.id; }
      if (t !== hit) { d += ' (' + (near ? 'near' : 'from') + ' ' + (hit.tagName || '?') + ')'; }
      if (t.href) { d += ' -> ' + t.href; }
      return d;
    },

    /* What the platform itself delivered to this page, if anything.
       The frame-click path feeds a real touch in from outside the page, because a
       cross-origin frame is the one thing a dispatched event cannot reach (issue
       #20). From out here that feed is entirely blind: it either arrives or it does
       not, and both look identical. These two facts tell them apart.

       `trusted` is any event the page saw with isTrusted set — nothing we dispatch
       ever has it, so a value here means the platform delivered real input to the
       engine. `frame` is the parent-side proof that it landed *inside* the frame:
       clicking into a cross-origin frame focuses the frame element, and only a real
       click does. So: neither set means the touch never reached the engine; trusted
       but no frame means it arrived and was routed elsewhere; both means it went in
       and the frame's own content is what did not react.

       Those readings are for the native touch feed. **For the CDP path they do not
       hold, and `none/none` there proves nothing.** A CDP click that lands inside an
       out-of-process frame is delivered to that frame's own process: this document
       is not on the path and never sees the event, trusted or otherwise, and site
       isolation need not fire `focusin` on the frame element in the parent either.
       So `none/none` after an inspector click covers ""it never arrived"" and ""it
       arrived and went exactly where it was aimed"" equally well. Issue #20's
       `build-5490157` trail reads `none/none` on ten clicks that walked a reCAPTCHA
       grid and were followed by Instagram's post-login page. Only the site's own
       behaviour settles that one. */
    native: function () {
      return 'trusted=' + (st.trusted || 'none') + ' frame=' + (st.frameFocus || 'none');
    },

    /* Called just before a feed, so what comes back afterwards is only about it. */
    clearNative: function () {
      st.trusted = null;
      st.frameFocus = null;
      return 'cleared';
    },

    /* Scrolls the innermost scrollable element under the cursor, falling back to
       the document. Without this, sites that scroll a div (most SPAs) ignore us. */
    scroll: function (dx, dy) {
      var target = scrollableAncestor(at());
      if (target) { target.scrollTop += dy; target.scrollLeft += dx; return 'el'; }
      window.scrollBy(dx, dy);
      return 'doc';
    },

    page: function (dir) {
      return window.__ovs.scroll(0, dir * Math.round(window.innerHeight * 0.85));
    },

    /* ewk on an older TV lays the page out at a width of its own choosing and
       paints it into the view, which stretches everything. Forcing the layout
       width to the view's real pixel width is the only lever available on 5.0
       (WebView.SetScale and the zoom settings are API 6+). */
    setViewport: function (px) {
      try {
        var m = document.querySelector('meta[name=viewport]');
        if (!m) {
          m = document.createElement('meta');
          m.setAttribute('name', 'viewport');
          (document.head || document.documentElement).appendChild(m);
        }
        if (m.getAttribute('data-ovs-original') === null) {
          m.setAttribute('data-ovs-original', m.getAttribute('content') || '');
        }
        m.setAttribute('content', 'width=' + px + ', initial-scale=1');
        return 'viewport=' + px;
      } catch (e) { return 'viewport failed: ' + e; }
    },

    clearViewport: function () {
      try {
        var m = document.querySelector('meta[name=viewport]');
        if (!m) { return 'no meta'; }
        var original = m.getAttribute('data-ovs-original');
        if (original) { m.setAttribute('content', original); } else { m.removeAttribute('content'); }
        return 'viewport restored';
      } catch (e) { return 'restore failed: ' + e; }
    },

    metrics: function () {
      var d = document.documentElement;
      return [window.innerWidth + 'x' + window.innerHeight,
              'client ' + d.clientWidth + 'x' + d.clientHeight,
              'outer ' + window.outerWidth + 'x' + window.outerHeight,
              'screen ' + screen.width + 'x' + screen.height,
              'dpr ' + window.devicePixelRatio].join(', ');
    },

    /* The field the on-screen keyboard is typing into: whatever the cursor last
       focused, or the page's own search box as a fallback. */
    field: function () {
      if (st.field && st.field.parentNode) { return st.field; }
      var el = document.activeElement;
      if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable)) {
        return el;
      }
      return document.querySelector('input[type=search],input[type=text],input:not([type])');
    },

    /* Sets a field's value so frameworks notice. Assigning .value directly is
       invisible to React and friends, which track the native setter. */
    type: function (text) {
      var el = window.__ovs.field();
      if (!el) { return 'no field'; }
      /* Deliberately NOT calling focus(): that is what raises the platform IME.
         Values are set programmatically, so the field does not need focus. */

      if (el.isContentEditable) {
        el.textContent = text;
      } else {
        var set = null;
        try {
          var proto = Object.getPrototypeOf(el);
          var desc = Object.getOwnPropertyDescriptor(proto, 'value');
          if (desc && desc.set) { set = desc.set; }
        } catch (_) {}
        if (set) { set.call(el, text); } else { el.value = text; }
      }

      try {
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
      } catch (_) {}

      try { if (el.blur) { el.blur(); } } catch (_) {}

      return (el.tagName || '?') + (el.id ? '#' + el.id : '');
    },

    /* Enter on the focused field: real key events first (search-as-you-type UIs
       listen for them), then the owning form as a fallback. */
    submit: function () {
      var el = window.__ovs.field();
      if (!el) { return 'no field'; }

      ['keydown', 'keypress', 'keyup'].forEach(function (type) {
        try {
          el.dispatchEvent(new KeyboardEvent(type, {
            bubbles: true, cancelable: true, key: 'Enter', code: 'Enter', keyCode: 13, which: 13
          }));
        } catch (_) {}
      });

      try {
        if (el.form) {
          if (el.form.requestSubmit) { el.form.requestSubmit(); } else { el.form.submit(); }
          return 'form submitted';
        }
      } catch (_) {}

      return 'enter sent';
    }
  };

  /* A page that focuses a text field (DuckDuckGo autofocuses its search box)
     makes ewk raise the TV's on-screen keyboard, which then swallows the remote.
     Blur anything the page focuses unless we asked for it. */
  window.__ovs.allowFocus = false;
  /* Belt and braces for autofocus: neutralise programmatic focus() on text
     fields while the user has not asked to type. */
  try {
    var proto = window.HTMLElement && HTMLElement.prototype;
    if (proto && proto.focus && !proto.__ovsFocusGuard) {
      var realFocus = proto.focus;
      proto.focus = function () {
        var tag = this.tagName;
        if (!window.__ovs.allowFocus && (tag === 'INPUT' || tag === 'TEXTAREA' || this.isContentEditable)) {
          return;
        }
        return realFocus.apply(this, arguments);
      };
      proto.__ovsFocusGuard = true;
    }
  } catch (_) {}
  try {
    document.addEventListener('focusin', function (e) {
      /* A frame is not a text field and cannot raise the IME, so the guard has no
         business here — and a cross-origin frame taking focus is the one signal
         from out here that a fed touch actually landed inside it. Blurring it would
         be this guard undoing the frame-click path it sits next to. */
      if (e.target && isFrame(e.target)) {
        st.frameFocus = e.target.tagName || 'FRAME';
        return;
      }
      if (window.__ovs.allowFocus) { return; }
      try { if (e.target && e.target.blur) { e.target.blur(); } } catch (_) {}
    }, true);
    if (document.activeElement && document.activeElement.blur) { document.activeElement.blur(); }
  } catch (_) {}

  /* Real input, if any ever arrives. Capture phase so nothing in the page can stop
     it being seen, and it only ever reads — a watcher that changed the outcome of
     the thing it is watching would be worthless. */
  try {
    ['pointerdown', 'mousedown', 'touchstart', 'click'].forEach(function (type) {
      document.addEventListener(type, function (e) {
        try {
          if (!e || !e.isTrusted) { return; }
          var p = e.touches && e.touches.length ? e.touches[0] : e;
          var x = Math.round(p.clientX || 0);
          var y = Math.round(p.clientY || 0);
          st.trusted = type + ' on ' + ((e.target && e.target.tagName) || '?') +
                       ' at ' + x + ',' + y;
        } catch (_) {}
      }, true);
    });
  } catch (_) {}

  window.__ovs.install();
  if (document.addEventListener) {
    /* Single-page navigations wipe the overlay out of the DOM. This fires on a
       great deal more than that — the app coming to the front at launch, the
       set's own menus going over it, the screen blanking — which is why install()
       must not be the thing that decides whether the arrow shows. */
    document.addEventListener('visibilitychange', function () { window.__ovs.install(); }, false);
  }
})();
";
    }
}
