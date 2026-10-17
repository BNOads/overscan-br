namespace Overscan
{
    /// <summary>
    /// Overscan BR: keeps a page's "can I autoplay?" probe away from the TV's
    /// audio decoder.
    ///
    /// Players and ad libraries test whether sound may start on its own by
    /// calling <c>play()</c> on a throwaway <c>Audio</c> whose source is an empty
    /// or near-empty <c>data:</c> URI. On the 2025 set this was found on
    /// (QN55QEF1, Tizen 9.0, 2026-10-17) that probe is what takes the app down:
    /// the trail shows ge.globo.com doing exactly that to two detached elements,
    /// the native output shows the TV's DRM audio pipeline asserting on a buffer
    /// that is not one (<c>gst_buffer_map_range: assertion 'GST_IS_BUFFER</c>), and
    /// the process ends in SIGABRT a second later. The same assertion is in the
    /// native output of the first crash on a video page. The engine's own crash
    /// is not ours to catch
    /// (see "It is the engine's decoder" in INTERNALS), so the probe is answered
    /// here instead: the promise resolves, as it would for a sound that played,
    /// and the engine is never asked to decode nothing.
    ///
    /// Only <c>data:</c> audio or video sources shorter than a sound anyone could
    /// hear are touched. Everything else goes to the <c>play()</c> that was there
    /// before, so it is installed last and wraps the probes and the hold.
    /// </summary>
    internal static class NuiEmptySoundGuard
    {
        public const string Prefix = "__ovsbr guard: ";

        /// <summary>
        /// Payload length, in characters after the comma, below which a data:
        /// source is treated as a probe. 2 KB of base64 is about a tenth of a
        /// second of MP3: nothing a page plays on purpose.
        /// </summary>
        private const int ProbeLimit = 2048;

        public static string Script()
        {
            return @"(function () {
  var ME = window.HTMLMediaElement;
  if (!ME || !ME.prototype || typeof ME.prototype.play !== 'function') { return 'no media'; }
  if (ME.prototype.play.__ovsbrGuard) { return 'already'; }
  var inner = ME.prototype.play;
  function probe(el) {
    var s = '';
    try { s = String(el.currentSrc || el.src || (el.getAttribute && el.getAttribute('src')) || ''); } catch (e) {}
    if (!/^data:(audio|video)\//i.test(s)) { return -1; }
    var comma = s.indexOf(',');
    var size = comma < 0 ? 0 : s.length - comma - 1;
    return size < " + ProbeLimit + @" ? size : -1;
  }
  var guarded = function () {
    var size = probe(this);
    if (size >= 0) {
      try {
        console.log('" + Prefix + @"answered ' + (this.isConnected ? 'attached' : 'detached') + ' ' +
                    String(this.tagName).toLowerCase() + ' data: source, ' + size + ' chars, not decoded');
      } catch (e) {}
      return Promise.resolve();
    }
    return inner.apply(this, arguments);
  };
  guarded.__ovsbrGuard = true;
  ME.prototype.play = guarded;
  return 'installed';
})()";
        }
    }
}
