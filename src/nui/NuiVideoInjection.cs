using System;
using System.IO;

namespace Overscan
{
    /// <summary>
    /// Overscan BR: the universal video injection, <c>src/nui/injection.js</c>,
    /// embedded in the package and sent into every page the same way the HLS
    /// answer and the empty-sound guard are: as early as the engine reports the
    /// new page (InstallEarly) and again on load finished. The NUI WebView has no
    /// document-start user script, so "early" is the next gap in the page's own
    /// parsing, which is ahead of nearly every player. The script is idempotent
    /// (window.__ovsbrVideo), so the repeats cost nothing.
    ///
    /// It works on the page's &lt;video&gt; only: picks the largest visible one,
    /// starts it, and when it stalls walks a fallback cascade (other source button,
    /// reload of the src, reload of the page). Its P2P policy is at the top of the
    /// file. It reaches the main document and same-origin frames; another origin's
    /// frame is out of reach of any script of ours, as for every other injection.
    /// </summary>
    internal static class NuiVideoInjection
    {
        /// <summary>Every line the script logs starts with this.</summary>
        public const string Prefix = "OVERSCAN-VIDEO: ";

        public const string ResourceName = "Overscan.injection.js";

        private static string _script;

        /// <summary>The script, read once. Empty if the resource is missing.</summary>
        public static string Script()
        {
            if (_script != null)
            {
                return _script;
            }

            try
            {
                using (Stream stream = typeof(NuiVideoInjection).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        _script = string.Empty;
                        DiagLog.Add("video injection: resource missing");
                    }
                    else
                    {
                        using (StreamReader reader = new StreamReader(stream))
                        {
                            _script = reader.ReadToEnd();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _script = string.Empty;
                DiagLog.Add("video injection unreadable: " + ex.Message);
            }

            return _script;
        }
    }
}
