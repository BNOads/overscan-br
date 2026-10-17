using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Overscan
{
    /// <summary>
    /// Serves <see cref="DiagLog"/> over plain HTTP on <see cref="Port"/>.
    ///
    /// A locked-down TV (and the Tizen 10.0 TV emulator, which reports
    /// intershell_support:disabled) gives no `sdb shell`, no `dlogutil`, and
    /// refuses `sdb pull` outside the sdk_tools path — so when the app fails
    /// before it can draw anything, there is no way to find out why. `sdb forward`
    /// still works, so a socket is the one channel left:
    ///
    ///     sdb forward tcp:8081 tcp:8081
    ///     curl http://localhost:8081
    ///
    /// It is started as the very first statement in Main, before anything that
    /// could throw, so "nothing is listening" is itself a result: it means the
    /// process never got as far as running managed code.
    /// </summary>
    internal static class DiagServer
    {
        public const int Port = 8081;

        private static Thread _thread;

        /// <summary>Set by the app so the report can include live state.</summary>
        public static Func<string> ReportProvider;

        /// <summary>
        /// Overscan BR: <c>GET /abrir?chave=…&amp;url=…</c> opens a page on the TV
        /// from a Mac or phone on the same network. The handler is called on this
        /// server's thread and must not touch the UI; null leaves the server
        /// exactly as upstream, a report and nothing else.
        /// </summary>
        public static Func<string, string> OpenHandler;

        /// <summary>The word a request to /abrir must carry. Empty refuses them all.</summary>
        public static string OpenKey = string.Empty;

        public static void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        private static void Loop()
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                DiagLog.Add("diag server listening on :" + Port);

                while (true)
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        string requestLine = string.Empty;
                        try
                        {
                            // Drain the request. Upstream has exactly one page; the
                            // first line now also decides whether this is /abrir.
                            stream.ReadTimeout = 2000;
                            var scratch = new byte[2048];
                            int read = stream.Read(scratch, 0, scratch.Length);
                            string received = Encoding.UTF8.GetString(scratch, 0, Math.Max(0, read));
                            int end = received.IndexOf('\r');
                            requestLine = end < 0 ? received : received.Substring(0, end);
                        }
                        catch (Exception)
                        {
                            // A client that sends nothing still gets the report.
                        }

                        string opened = TryOpen(requestLine, client);
                        byte[] payload = Encoding.UTF8.GetBytes(opened ?? Report());
                        byte[] head = Encoding.UTF8.GetBytes(
                            "HTTP/1.0 200 OK\r\n" +
                            "Content-Type: text/plain; charset=utf-8\r\n" +
                            "Content-Length: " + payload.Length + "\r\n" +
                            "Connection: close\r\n\r\n");

                        stream.Write(head, 0, head.Length);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("diag server died: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The answer to an /abrir request, or null when the request is anything
        /// else and the report should be served as always.
        ///
        /// Two locks, because this puts a page on somebody's television: the
        /// request has to come from a private address (a home network, never the
        /// internet even if a router forwards the port), and it has to carry
        /// <see cref="OpenKey"/>.
        /// </summary>
        private static string TryOpen(string requestLine, TcpClient client)
        {
            if (!requestLine.StartsWith("GET /abrir?", StringComparison.Ordinal))
            {
                return null;
            }

            Func<string, string> handler = OpenHandler;
            if (handler == null || string.IsNullOrEmpty(OpenKey))
            {
                return "este navegador não abre páginas pela rede";
            }

            if (!FromHomeNetwork(client))
            {
                DiagLog.Add("abrir refused: not a private address");
                return "recusado: só aceito pedidos da rede de casa";
            }

            string query = requestLine.Substring("GET /abrir?".Length);
            int space = query.IndexOf(' ');
            if (space >= 0)
            {
                query = query.Substring(0, space);
            }

            string key = null;
            string url = null;
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string name = pair.Substring(0, eq);
                string value = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                if (name == "chave")
                {
                    key = value;
                }
                else if (name == "url")
                {
                    url = value;
                }
            }

            if (key != OpenKey)
            {
                DiagLog.Add("abrir refused: wrong key");
                return "recusado: chave errada";
            }

            if (string.IsNullOrEmpty(url))
            {
                return "faltou o endereço (url=...)";
            }

            try
            {
                return handler(url);
            }
            catch (Exception ex)
            {
                return "não consegui abrir: " + ex.Message;
            }
        }

        private static bool FromHomeNetwork(TcpClient client)
        {
            try
            {
                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote == null)
                {
                    return false;
                }

                IPAddress address = remote.Address;
                if (address.IsIPv4MappedToIPv6)
                {
                    address = address.MapToIPv4();
                }

                byte[] b = address.GetAddressBytes();
                if (b.Length != 4)
                {
                    return IPAddress.IsLoopback(address);
                }

                return b[0] == 10 || b[0] == 127 ||
                       (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                       (b[0] == 192 && b[1] == 168);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string Report()
        {
            Func<string> provider = ReportProvider;
            if (provider == null)
            {
                // The previous run's evidence does not need the UI to exist, and
                // this page is what a reporter gets when they are quick off the
                // mark — issue #17's first fetch showed three lines and nothing
                // else. It is the same trail the full report carries.
                // The probe's ledger outlives a launch, and the header line is the
                // one a reporter copies first — so it is here too, on the page they
                // get when the UI is not up yet.
                //
                // The build tag and the import census too (issue #105): on the AU7200
                // every page has been this one, because the first launch of a build
                // dies within seconds of the engine failing and the second never
                // reaches the UI. The `build-283f3e9` reply told the reporter the
                // page would start with `build :`, and it did not — this page had no
                // such line — which is a round trip spent on doubt about which
                // build was installed. What this page cannot show is what the
                // *current* launch has not yet established; the census it carries
                // is this launch's if it has run, and the previous run's lines are
                // in the trail below.
                return "Overscan — no report provider yet (UI has not started)\n\n" +
                       "build     : " + BuildInfo.Tag + "\n" +
                       "own native : " + NativeProbe.Summary + "\n" +
                       "engine imports: " + EngineImports.Summary + "\n" +
                       "engine stub: " + EngineStub.Summary + "\n" +
                       "trail write: " + Breadcrumbs.Status + "\n\n" +
                       "this run\n" + DiagLog.Dump() +
                       "\nwhat a stub must provide (the implementation's imports)\n" + EngineImports.Dump() +
                       "\nprevious run (last line is where it died)\n" +
                       Breadcrumbs.Previous +
                       "\nengine stdout/stderr (previous run)\n" +
                       Breadcrumbs.PreviousStdErr + "\n";
            }

            try
            {
                return provider();
            }
            catch (Exception ex)
            {
                return "report provider threw " + ex.GetType().Name + ": " + ex.Message + "\n\n" +
                       DiagLog.Dump();
            }
        }
    }
}
