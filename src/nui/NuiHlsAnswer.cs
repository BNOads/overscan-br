namespace Overscan
{
    /// <summary>
    /// Overscan BR: tells pages the truth about HLS, which the engine does not.
    ///
    /// Asked <c>canPlayType('application/vnd.apple.mpegurl')</c>, the engine on the
    /// 2025 set this was measured on (QN55QEF1, Tizen 9.0, 2026-10-29) answers
    /// <c>maybe</c>, and then cannot play one: Apple's own bipbop test stream, set
    /// as a plain <c>src</c>, ends in <c>error code 4</c> (no supported source)
    /// two tenths of a second in, every time. The same stream through hls.js, on
    /// the same page a few seconds later, plays: Media Source works here, native
    /// HLS does not. So every player written the usual way round ("if the browser
    /// says it plays HLS itself, let it; otherwise use hls.js") takes the one road
    /// that is closed, and the person on the sofa sees "error 4".
    ///
    /// The answer handed back is the one desktop Chrome 120 gives, which is also
    /// the browser this app tells sites it is (see the forced user agent): no.
    /// Those players then take their Media Source path, which is the one that
    /// works. A player with no such path did not have one on desktop Chrome
    /// either, so nothing that played before stops playing.
    ///
    /// Timing is the whole of it: a player asks once, while it sets itself up. So
    /// this goes in as soon as the engine reports the new page (see
    /// <c>InstallEarly</c> in the app), and again on load finished for the page
    /// that slipped past. The first answer it gives on a page goes to the trail
    /// with how far into the page it came, which is how a report says whether it
    /// was in time.
    ///
    /// Only the main document. Another origin's frame is out of reach of any
    /// script of ours, which is what "Abrir só o player" is for.
    /// </summary>
    internal static class NuiHlsAnswer
    {
        public static string Script()
        {
            return @"(function () {
  var ME = window.HTMLMediaElement;
  if (!ME || !ME.prototype || typeof ME.prototype.canPlayType !== 'function') { return 'no media'; }
  if (ME.prototype.canPlayType.__ovsbrHls) { return 'already'; }
  var inner = ME.prototype.canPlayType;
  var told = false;
  var answer = function (type) {
    if (/mpegurl/i.test(String(type))) {
      if (!told) {
        told = true;
        try {
          console.log('" + NuiEmptySoundGuard.Prefix + @"canPlayType(' + String(type).slice(0, 60) +
                      ') answered no (native HLS does not play here), ' +
                      Math.round(window.performance ? performance.now() : 0) + ' ms into the page');
        } catch (e) {}
      }
      return '';
    }
    return inner.apply(this, arguments);
  };
  answer.__ovsbrHls = true;
  ME.prototype.canPlayType = answer;
  return 'installed';
})()";
        }
    }
}
