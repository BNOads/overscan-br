using System;
using System.Collections.Generic;
using System.Globalization;
using Tizen.Applications;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// The NUI build, for platforms where <c>Tizen.WebView</c> is gone.
    ///
    /// Tizen 10.0 ships no Tizen.WebView.dll at all — the ElmSharp build dies there
    /// with a FileNotFoundException the moment it touches the type. NUI's WebView is
    /// public API from API 9, so this build covers 9.0+ while the ElmSharp build
    /// covers 5.0-8.0. Everything engine-independent (user agents, the injected
    /// page script, storage, diagnostics) is shared between them.
    /// </summary>
    internal sealed class NuiBrowserApp : NUIApplication
    {
        private Window _window;
        private WebView _web;
        private NuiCursor _cursor;
        private NuiKeyboard _keyboard;

        /// <summary>
        /// The heartbeat, held in a field for exactly the reason
        /// <see cref="NuiLater"/> keeps its own list: a NUI Timer with no live
        /// reference is collected, and this one used to be a local in
        /// <see cref="OnCreate"/>. It ticked until the first garbage collection —
        /// which a page load reliably causes — and then stopped, silently, taking
        /// the blank-view watchdog, the memory sampling and the deferred video path
        /// with it. Issue #20's trails are what that looks like from a TV: the
        /// `memory:` lines stop the moment pages start loading and never resume, the
        /// report shows a peak *below* the resident size because nothing is
        /// updating it any more, and a stored video preference is still waiting to
        /// be applied when the run ends. The ElmSharp build has always kept its
        /// equivalent in `_tick`; this one had not.
        /// </summary>
        private Timer _tick;

        private View _bar;
        private TextLabel _host;
        private TextLabel _path;
        private TextLabel _status;
        private View _progress;
        private View _hints;
        private TextLabel _overlay;
        private TextLabel _hintsFooter;

        private RemoteMenu _menu;
        private View _menuPanel;
        private TextLabel[] _menuLabels;
        private TextLabel[] _menuShortcuts;
        private View _menuHighlight;

        /// <summary>
        /// Overscan BR has two menus, the short one and "Avançado". Both are built
        /// once and swapped in by <see cref="UseMenu"/>, so the five fields above
        /// always describe whichever one is on screen and none of the code that
        /// reads them had to learn there are two.
        /// </summary>
        private sealed class MenuSet
        {
            public RemoteMenu Menu;
            public View Panel;
            public TextLabel[] Labels;
            public TextLabel[] Shortcuts;
            public View Highlight;
            public int RowHeight;
        }

        private MenuSet _mainMenu;
        private MenuSet _advancedMenu;

        private NuiFavouritesBar _favBar;

        /// <summary>
        /// The page zoom on the view right now, in percent. Remembered per site
        /// under <c>zoom:&lt;site&gt;</c>, so the size somebody picked for a news
        /// site comes back every time they open it.
        /// </summary>
        private int _zoomPercent = 100;

        /// <summary>
        /// A zoom chosen but not yet handed to the engine. Set from the load
        /// callbacks and the zoom keys, carried out by <see cref="OnTick"/> once
        /// no load is running. Never from inside a PageLoadStarted or
        /// PageLoadFinished notification: that is the re-entry ApplyVideoPath and
        /// ApplySiteRules already keep away from. The first build did set it from
        /// there; it was suspected when that build froze on a video page, and the
        /// trail later showed the TV's media stack aborting instead, so this is a
        /// precaution rather than a proven fix.
        /// </summary>
        private bool _zoomPending;
        private static readonly int[] ZoomSteps = { 80, 90, 100, 110, 125, 150, 175, 200, 250 };

        /// <summary>
        /// An address sent from the Mac to <c>:8081/abrir</c>. Written on
        /// DiagServer's thread, read and cleared by <see cref="OnTick"/> on the main
        /// one, because DALi and the web view may only be touched from there.
        /// </summary>
        private volatile string _remoteOpen;

        /// <summary>
        /// An action sent from the Mac panel to <c>:8081/acao</c>, queued the same
        /// way as <see cref="_remoteOpen"/> and for the same reason.
        /// </summary>
        private volatile string _remoteAction;

        /// <summary>What /acao accepts, and which menu action each name runs.</summary>
        private static readonly Dictionary<string, string> RemoteActions = new Dictionary<string, string>
        {
            { "favbar", RemoteMenu.ActionFavouritesBar },
            { "zoomin", RemoteMenu.ActionZoomIn },
            { "zoomout", RemoteMenu.ActionZoomOut },
            { "zoomreset", RemoteMenu.ActionZoomReset },
            { "videofull", RemoteMenu.ActionVideoFullscreen },
            { "player", RemoteMenu.ActionOpenPlayer },
            { "home", RemoteMenu.ActionHome },
            { "address", RemoteMenu.ActionAddress },
            { "bookmark", RemoteMenu.ActionBookmark },
            { "menu", "menu" },
        };

        /// <summary>
        /// Whether the page has a video stretched over it by
        /// <see cref="ToggleVideoFullscreen"/>. Back undoes that before it goes
        /// back a page, and every new load clears it, since the page that had
        /// the stretched video is gone.
        /// </summary>
        private bool _videoFull;

        /// <summary>
        /// Overscan BR: in fullscreen the pointer goes away after a few seconds
        /// without a pointer key, the way a desktop player hides the mouse, and
        /// takes the page's own video controls with it. The first arrow or OK
        /// after that only brings it back, so waking the pointer can never be
        /// the click that pauses the match.
        /// </summary>
        private bool _pointerIdleHidden;
        private DateTime _lastPointerKey = DateTime.UtcNow;
        private const int PointerIdleSeconds = 3;

        /// <summary>
        /// What a request to <c>:8081/abrir</c> or <c>/acao</c> has to carry.
        /// Without it anybody on the home network could put any page on this TV.
        /// Made once per install on first launch and kept in the store, so every
        /// TV has its own and the public code carries none. It is shown on the TV
        /// itself (Avançado, "Controle pelo computador"): the one place somebody
        /// holding the remote can read it and nobody on the network can.
        /// </summary>
        private static string RemoteKey()
        {
            string key = Store.Get("remoteKey", string.Empty);
            if (key.Length == 0)
            {
                key = NewRemoteKey();
                Store.Set("remoteKey", key);
                DiagLog.Add("remote key made for this install");
            }

            return key;
        }

        /// <summary>
        /// Eight characters in two groups, from an alphabet without the pairs that
        /// read alike on a TV (0/O, 1/I): about 40 bits, typed once on a computer.
        /// Thirty-two letters divide 256 evenly, so every one is equally likely.
        /// </summary>
        private static string NewRemoteKey()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[8];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            var key = new System.Text.StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i == 4)
                {
                    key.Append('-');
                }

                key.Append(alphabet[bytes[i] % alphabet.Length]);
            }

            return key.ToString();
        }

        /// <summary>
        /// This TV's address on the home network, for the card that tells the
        /// computer where to send things. Best-effort: a platform that will not
        /// say gets pointed at the TV's own network settings instead.
        /// </summary>
        private static string LocalAddress()
        {
            try
            {
                foreach (System.Net.NetworkInformation.NetworkInterface nic in
                         System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        continue;
                    }

                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation address in
                             nic.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            !System.Net.IPAddress.IsLoopback(address.Address))
                        {
                            return address.Address.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("local address unreadable: " + ex.Message);
            }

            return "veja em Configurações > Geral > Rede";
        }

        private UserAgentPreset[] _presets = UserAgents.Defaults();
        private int _presetIndex;
        private string _engineUserAgentRaw;
        private string _engineUserAgent = "(not read yet)";
        private string _lastProbe = "(no page probed yet)";
        private string _lastMetrics = "(not measured yet)";
        private string _engineFailure;

        /// <summary>
        /// How long after a frame click the page is asked what it saw. The click
        /// leaves on another thread and has a server to start, a socket to open and
        /// three messages to exchange before the engine has even been asked to
        /// deliver anything, so this waits for the slow version of all of that —
        /// and is still short enough to be an answer about the press you just made.
        /// </summary>
        private const int FrameWitnessDelay = 1200;

        /// <summary>
        /// How long a load may take to *begin* before the view is called dead. Not
        /// how long a page may take to arrive — PageLoadStarted fires as soon as the
        /// engine accepts the navigation, well before a byte is fetched, so this is
        /// generous even on a set that takes seven seconds to open DuckDuckGo.
        /// </summary>
        private const int BlankViewSeconds = 6;

        /// <summary>
        /// What the page reported after the last native tap: whether any real input
        /// arrived at all, and whether a cross-origin frame took focus. The feed
        /// itself cannot tell us either (issue #20), and these two facts are what
        /// separate "the touch never reached the engine" from "it reached the page
        /// and the frame ignored it".
        /// </summary>
        private volatile string _frameWitness = "(no frame clicked yet)";

        /// <summary>
        /// Cached on the main thread: DiagServer answers on its own thread and DALi
        /// objects are not thread-safe, so the report may only read plain strings.
        /// </summary>
        private volatile string _cachedUrl = "-";
        private volatile string _cachedTitle = "(untitled)";
        private volatile string _cachedGeometry = "(unmeasured)";

        private bool _overlayVisible;
        private bool _hintsWanted = true;
        private bool _imagesOn = true;
        private bool _adBlockOn = true;
        private bool _oneVideoOn = true;

        // New windows refused, and the last address one asked for. Written on the
        // engine's callback thread, read by the report.
        private int _windowsRefused;
        private volatile string _lastWindowRefused = "(none yet)";

        // Overscan BR: when the engine is next asked to hand back the memory it is
        // holding and no longer using, and how that has gone.
        private DateTime _freeMemoryAt = DateTime.MinValue;
        private int _memoryFrees;
        private string _memoryState = "(not set)";

        /// <summary>
        /// The browser-wide answers, i.e. what a site with no rule of its own
        /// gets. <see cref="_imagesOn"/> and <see cref="_presetIndex"/> are what
        /// the view has actually been told, which on a site with a rule is not the
        /// same thing (issue #74).
        /// </summary>
        private bool _defaultImagesOn = true;
        private int _defaultPresetIndex;

        /// <summary>
        /// Issue #97. The proxy typed in, or null when there is none; whether a
        /// site with no proxy rule of its own goes through it; and whether the
        /// engine has been told to use it right now. Unlike images and identity
        /// the proxy is one setting for the whole process, so <see cref="_proxyOn"/>
        /// is the engine's state and not only this view's.
        /// </summary>
        private ProxyAddress _proxy;
        private bool _defaultProxyOn;
        private bool _proxyOn;

        /// <summary>
        /// Set when the proxy switch was pressed before any proxy was typed: the
        /// keyboard opens for one, and the press it interrupted is carried out once
        /// it is entered. <see cref="_proxyPendingUrl"/> is null for the start
        /// screen, where the switch is the browser-wide one.
        /// </summary>
        private bool _proxyPending;
        private string _proxyPendingUrl;

        private int _menuRowHeight = 62;

        /// <summary>
        /// The site whose rules are on the view, or null while nothing but our own
        /// generated pages has been shown. Compared against the site of each load
        /// to decide whether anything has to change at all — without it every page
        /// of a site would re-apply and re-reload the same settings.
        /// </summary>
        private string _siteKey;

        /// <summary>
        /// Reloads asked for by a site rule since the last load that settled, and
        /// whether this load asked for one. See <see cref="ApplySiteRules"/>: two
        /// sites whose rules disagree and which redirect to each other would
        /// otherwise reload one another for ever.
        /// </summary>
        private int _ruleReloads;
        private bool _ruleReloadIssued;
        private const int RuleReloadLimit = 3;
        private bool _videoOverlay = true;

        /// <summary>
        /// A stored video path that has been read but not yet handed to the
        /// engine. False on an install where the key has never been chosen, so
        /// such a set is never told anything at all and stays exactly where it
        /// was before the toggle existed.
        /// </summary>
        private volatile bool _videoPathPending;

        /// <summary>
        /// Whether `5` has ever been pressed on this install. Cached rather than
        /// asked of <see cref="Store"/>, because the report is built on
        /// DiagServer's thread and nothing there may touch the main thread's state.
        /// </summary>
        private volatile bool _videoPathChosen;
        private bool _pageEverLoaded;

        /// <summary>
        /// When a load was last asked for, cleared the moment the engine says it
        /// started one. See <see cref="CheckSomethingLoaded"/>: a load that is
        /// never even *begun* is the one failure this app could not see, and the
        /// black screen on issue #20 was it.
        /// </summary>
        private DateTime _loadAskedAt = DateTime.MinValue;

        /// <summary>What that load was for, so a recovery retries it and not home.</summary>
        private string _loadAskedFor;
        private int _blankRecoveries;

        /// <summary>
        /// How big the start screen was the last time it was built, for the report.
        /// Issue #53's black screen was a start screen that had grown past what
        /// the engine will load, and nothing on the report said how large the page
        /// it was refusing had got. Volatile for the same reason as
        /// <see cref="_blankState"/>.
        /// </summary>
        private volatile string _startPageState = "(not built yet)";

        /// <summary>
        /// What the blank-view watchdog has had to do, for the report. Volatile
        /// because DiagServer answers on its own thread, and a plain string is the
        /// only kind of state it is allowed to read.
        /// </summary>
        private volatile string _blankState = "(never blank)";
        private string _cookieState = "(not configured)";
        private bool _keysToPage;
        private bool _viewportFix;
        private bool _atHome;
        private bool _loading;
        private bool _chromeVisible;
        private int _marquee;
        private DateTime _lastActivity = DateTime.UtcNow;

        /// <summary>
        /// When the trail was last told how big this process had become. See
        /// <see cref="ProcessMemory"/>: an app the low-memory killer takes away
        /// and an app that crashes leave the same silence behind them, and this
        /// line is what tells the two apart afterwards.
        /// </summary>
        private DateTime _lastMemoryNote = DateTime.MinValue;
        private long _peakMemoryMb;

        /// <summary>
        /// How many OK presses have arrived in a row without the key being let go.
        /// Tizen delivers a held key as repeated Down events, and counting them is
        /// the only way to notice a hold that cannot also break an ordinary press:
        /// the press still clicks the moment it arrives, exactly as before, and the
        /// menu opens on top of that if the button stays down. Waiting for the Up
        /// event instead would be tidier and would put every click in this browser
        /// at the mercy of a firmware delivering it.
        /// </summary>
        private int _okRepeats;
        private DateTime _lastOk = DateTime.MinValue;

        /// <summary>
        /// Presses of OK in a row before a hold is taken to mean "open the menu".
        /// Tizen's repeat starts after about half a second and runs at roughly ten
        /// a second, so this lands a little over a second into the hold — past any
        /// accidental double-press, short of feeling stuck.
        /// </summary>
        private const int OkHoldRepeats = 6;

        /// <summary>
        /// How often this process's size goes on the trail while a video is playing.
        /// It was five seconds throughout, and five seconds is what cost issue #20
        /// the resolution that mattered: the reading before the death was already
        /// twenty-five megabytes down, so whatever happened had happened by then and
        /// there was nothing in between.
        /// </summary>
        private const int MemorySecondsWithVideo = 2;

        /// <summary>
        /// And how often otherwise. Two seconds everywhere would double the length
        /// of a trail that goes into a report somebody has to copy out of a browser
        /// on their phone, to buy resolution on pages where nothing is happening.
        /// </summary>
        private const int MemorySecondsIdle = 5;

        /// <summary>
        /// How much the engine may write to stdout/stderr before the capture is
        /// given up. Generous for a crash message, small enough that a chatty
        /// firmware cannot fill somebody's television.
        /// </summary>
        private const long StdErrLimitBytes = 1024 * 1024;

        /// <summary>Longest gap that still counts as the same hold.</summary>
        private const int OkRepeatGapMs = 400;
        private DateTime _flashUntil = DateTime.MinValue;

        /// <summary>The page to open at launch, or null for the start screen.</summary>
        private string _startupUrl;

        protected override void OnCreate()
        {
            base.OnCreate();
            DiagServer.ReportProvider = Report;

            _window = GetDefaultWindow();
            _window.BackgroundColor = new Color(0.039f, 0.043f, 0.055f, 1f);
            DiagLog.Add("window " + _window.WindowSize.Width + "x" + _window.WindowSize.Height);
            WatchTheWindow();

            // Before the keyboard is built: it resolves its remembered layout the
            // first time KeyboardLayouts is touched, so initialising the store
            // afterwards silently threw the user's layout choice away.
            Store.Init(DirectoryInfo.Data);
            SiteRules.Init(DirectoryInfo.Data);
            SeedFavourites();

            BuildChrome();
            BuildOverlay();
            BuildHints();
            BuildMenu();

            _favBar = new NuiFavouritesBar(_window);
            _favBar.Chosen += OnFavouriteChosen;
            _favBar.KeepRequested += OnFavouritesBarKeep;

            _keyboard = new NuiKeyboard(_window);
            _keyboard.Committed += OnKeyboardCommitted;
            _keyboard.StartPageSet += OnStartPageSet;
            _window.KeyEvent += OnWindowKey;

            DiagServer.OpenKey = RemoteKey();
            DiagServer.OpenHandler = OnRemoteOpen;
            DiagServer.ActionHandler = OnRemoteAction;

            if (!TryStartEngine())
            {
                _status.Text = "o navegador não iniciou, aperte 3";
                ShowOverlay(true);
                return;
            }

            _engineUserAgentRaw = SafeUserAgent();
            _engineUserAgent = _engineUserAgentRaw ?? "(unavailable)";
            _presets[0] = UserAgents.MatchingEngine(_engineUserAgentRaw);
            DiagLog.Add("engine UA: " + _engineUserAgent);

            _viewportFix = Store.GetBool("viewportFix", false);
            _hintsWanted = Store.GetBool("hints", true);

            // Three states, not two, since issue #79: the start screen, a fixed
            // address, or wherever the last session got to. StartPage owns which.
            _startupUrl = StartPage.Resolve(Store.RecentHistory);
            ShowHints(_hintsWanted);

            _defaultPresetIndex = Math.Min(Store.GetInt("uaPreset", 0), _presets.Length - 1);
            ApplyPreset(_defaultPresetIndex);

            // A start page, if one was set with the keyboard's `start` key (issue
            // #15): otherwise the same address gets typed on a remote every launch.
            if (string.IsNullOrEmpty(_startupUrl))
            {
                ShowHome();
            }
            else
            {
                Navigate(_startupUrl);
            }

            _tick = new Timer(150);
            _tick.Tick += (s, e) => OnTick();
            _tick.Start();
        }

        /// <summary>
        /// Records the things that happen to this app rather than inside it.
        ///
        /// The trail from issue #20's `build-c0cd5ab` shows a run that ends with the
        /// process's memory dropping by twenty-five megabytes in the last reading
        /// before it goes. Something released a lot at once, and there is a reading
        /// of that which has nothing to do with a crash: a window that stops being
        /// visible makes the engine give up its graphics resources, and an app that
        /// has been put in the background is one the platform may then close. From
        /// the sofa "I pressed something and Overscan disappeared" and "Overscan
        /// crashed" are the same sentence, and these lines are what tell them apart.
        /// See <see cref="NuiDeathWatch"/> for the rest of the ladder.
        /// </summary>
        private void WatchTheWindow()
        {
            try
            {
                _window.FocusChanged += delegate (object sender, Window.FocusChangedEventArgs e)
                {
                    Breadcrumbs.DropToTrail("window focus " + (e.FocusGained ? "gained" : "LOST"));
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("focus watch failed: " + ex.Message);
            }

            try
            {
                _window.VisibilityChanged += delegate (object sender, Window.VisibilityChangedEventArgs e)
                {
                    Breadcrumbs.DropToTrail("window " + (e.Visibility ? "visible" : "HIDDEN"));
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("visibility watch failed: " + ex.Message);
            }

            try
            {
                LowMemory += delegate (object sender, Tizen.Applications.LowMemoryEventArgs e)
                {
                    // The platform's own warning, and the last thing an app gets
                    // before the resource manager stops asking. The memory readings
                    // say this is not what is happening here — which is worth being
                    // able to state rather than assume. NUI has a MemoryLow signal of
                    // its own but keeps it internal; this is the app-framework one,
                    // inherited from CoreApplication, and it is public.
                    Breadcrumbs.DropToTrail("PLATFORM SAYS MEMORY IS LOW: " + e.LowMemoryStatus);
                    _freeMemoryAt = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("low-memory watch failed: " + ex.Message);
            }
        }

        protected override void OnPause()
        {
            Breadcrumbs.Drop("OnPause — this app is in the background now");
            base.OnPause();
        }

        protected override void OnResume()
        {
            Breadcrumbs.Drop("OnResume");
            base.OnResume();
        }

        protected override void OnTerminate()
        {
            // The one line that separates "the platform closed us" from "the process
            // died": a termination runs this, a crash never gets here.
            Breadcrumbs.Drop("OnTerminate — closing normally");
            base.OnTerminate();
        }

        private bool TryStartEngine()
        {
            try
            {
                Size2D screen = _window.WindowSize;

                // Full-window: the chrome floats above the page rather than taking
                // layout space, so nothing is letterboxed.
                _web = new WebView
                {
                    Position = new Position(0, 0),
                    Size = new Size(screen.Width, screen.Height),
                };
                _window.Add(_web);
                Breadcrumbs.Drop("NUI WebView created");

                // The chrome is drawn over the page but must never be *hit* by it:
                // DALi delivers a fed touch to the front-most sensitive actor, and
                // the hints card alone covers a corner big enough to hide a captcha.
                // Nothing here is ever touched deliberately — the app is driven
                // entirely by the remote — so none of it needs to be sensitive.
                PassTouchesThrough(_bar);
                PassTouchesThrough(_progress);
                PassTouchesThrough(_overlay);
                PassTouchesThrough(_hints);
                PassTouchesThrough(_mainMenu.Panel);
                PassTouchesThrough(_advancedMenu.Panel);
                PassTouchesThrough(_favBar.Root);

                // Insurance for the frame-click path: the toolkit gates part of its
                // pointer forwarding on this, and the default is not worth guessing.
                _web.MouseEventsEnabled = true;

                _web.EnableJavaScript = true;
                if (_web.Settings != null)
                {
                    _web.Settings.JavaScriptEnabled = true;

                    // The platform IME is what makes a page's autofocus swallow the
                    // remote. NUI exposes the switches for it that API 5 does not.
                    _web.Settings.ImePanelEnabled = false;
                    _web.Settings.KeypadWithoutUserActionUsed = false;

                    // Auto-fitting is the mobile-style "lay out wide, then scale to
                    // fit" behaviour, i.e. the suspected cause of the stretched
                    // rendering on older sets. We want 1:1.
                    _web.Settings.AutoFittingEnabled = false;
                    _imagesOn = _defaultImagesOn = Store.GetBool("images", true);
                    _web.Settings.AutomaticImageLoadingAllowed = _imagesOn;

                    // Nothing this browser does is worth a private window, and the
                    // engine's own default for it is not documented anywhere we can
                    // read. With it on, cookies and local storage live in memory and
                    // die with the process — which is what a site logging you out
                    // every launch (issue #20) looks like from the sofa.
                    _web.Settings.PrivateBrowsingEnabled = false;

                    // Overscan BR: no page opens a window. This browser has one view
                    // and no tabs, so a new window is never something the person on
                    // the sofa asked for; on the sites it is used on it is an ad.
                    // The engine refuses already when nobody answers NewWindowCreated
                    // (measured on the QEF1: window.open returned null, with and
                    // without a click, and a target=_blank link went nowhere). This
                    // says it twice, so an engine update that starts making windows
                    // on its own does not bring them back.
                    _web.Settings.ScriptsOpenWindowsAllowed = false;
                }

                RefuseNewWindows();

                ConfigureCookies();
                ConfigureProxy();
                ConfigureMemory();

                // Before the first load, so the start screen's tiles are the first
                // requests it sees. Default on: a feature that is off by default is
                // a feature only the people who already know about it have.
                _adBlockOn = Store.GetBool("adblock", true);
                Breadcrumbs.Drop("installing request filter");
                NuiAdBlock.Install(_web, _adBlockOn);
                DiagLog.Add("ad block: " + NuiAdBlock.LastResult);

                // Issue #100: the hold that keeps a second reel from opening a
                // pipeline under the one that is playing. Default on for the same
                // reason as the ad block; the menu row is for the site it breaks.
                _oneVideoOn = Store.GetBool("onevideo", true);
                NuiMseHold.Enabled = _oneVideoOn;

                // Read here, applied later. See ApplyVideoPath: on this set a
                // stored overlay handed to a WebView that has never loaded
                // anything gives a view that never loads anything either, so
                // the preference waits for a page to have gone through.
                _videoOverlay = Store.GetBool("videoOverlay", true);
                _videoPathChosen = Store.Get("videoOverlay", null) != null;

                // Applied whether or not anybody chose it, which is the change
                // `build-e78c0bc`'s trail argues for. Leaving the property alone was
                // meant to be the modest option — the engine's own default rather
                // than an opinion about every set — but that trail is the first one
                // showing what the engine's own default actually does here, and it
                // is not a third opinion, it is a broken path: video decodes into a
                // `fakesink` and the render thread segfaults on the fourth decoder,
                // while the two paths we can name both survive. An untouched engine
                // is the only configuration on this set that crashes, so it stops
                // being the one a fresh install lands in.
                _videoPathPending = true;

                _web.PageLoadStarted += (s, e) =>
                {
                    // On the trail, not only in the log: when a page takes the app
                    // down with it, the address it was opening is the first thing
                    // anyone will want to know.
                    Breadcrumbs.Drop("load started: " + SafeUrl());
                    _loading = true;

                    // Cleared here rather than after a reload is asked for: what
                    // the limit counts is loads that could not settle, and a load
                    // nobody had to reload is what settling looks like.
                    _ruleReloadIssued = false;

                    // The engine reports the destination here, which is where a
                    // site's own settings go on the view — see ApplySiteRules for
                    // why that is early enough for the images and one reload late
                    // for the identity. NoteShowing comes first: the rules ask
                    // whether this page is on a site at all, and "the start screen"
                    // is the answer that means no.
                    NoteShowing(SafeUrl());
                    ApplySiteRules(SafeUrl(), true);

                    // Overscan BR: only works out which zoom this site wants.
                    // Handing it to the engine waits for the tick after the load,
                    // see _zoomPending.
                    ApplyZoom();

                    EngineAnswered();
                    ShowChrome();

                    // Whatever needed a debugging port, this is not it any more.
                    // See NuiInspector.Stop: the window it is open for should be
                    // the captcha, not the evening.
                    _videoFull = false;
                    WakePointer();
                    NuiInspectorInput.Reset();
                    NuiInspector.Stop(_web);
                    NuiMediaWatch.Reset();
                    NuiVideoCap.Reset();
                    NuiVideoRect.Reset();
                    NuiMseWatch.Reset();
                    NuiMseHold.Reset();
                    _earlyInstalls = EarlyInstallsPerLoad;
                };

                // Overscan BR: the two guards that only help if they are in before
                // the page asks, sent as soon as the engine reports the new page
                // and not only on load finished. See InstallEarly.
                _web.UrlChanged += (s, e) => InstallEarly();
                _web.PageLoading += (s, e) => InstallEarly();

                _web.PageLoadFinished += (s, e) =>
                {
                    // Issue #100: a finish whose start was never reported still
                    // means the view is alive. Without this the watchdog fired six
                    // seconds after a page that had loaded.
                    EngineAnswered();
                    _loading = false;
                    _pageEverLoaded = true;
                    _progress.Hide();
                    Breadcrumbs.Drop("load finished: " + SafeUrl() + "  [" + ProcessMemory.Summary() + "]");

                    // Only a time here: the engine is not touched from inside its
                    // own load events. OnTick makes the call once the page settles.
                    _freeMemoryAt = DateTime.UtcNow.AddSeconds(FreeMemoryDelaySeconds);
                    _cursor.Reinstall();
                    InstallMediaWatch();
                    Probe();
                    ApplyViewportFix();
                    Store.RecordVisit(SafeUrl(), SafeTitle());
                    NoteShowing(SafeUrl());
                    ApplyZoom();

                    // Backstop, for a load whose start we somehow missed. Normally
                    // a no-op: the site is already the applied one by now.
                    ApplySiteRules(SafeUrl(), true);
                    if (!_ruleReloadIssued)
                    {
                        _ruleReloads = 0;
                    }

                    UpdateStatus();
                };

                _web.PageLoadError += (s, e) =>
                {
                    // A load that failed is a view that tried. Issue #100: a proxy
                    // refusing the connection raised this, the watchdog then read
                    // the silence after it as a dead view, rebuilt it and cleared
                    // the session, so every proxy hiccup signed the reporter out.
                    EngineAnswered();
                    _loading = false;
                    _progress.Hide();
                    string why = DescribeLoadError(e);
                    Breadcrumbs.Drop("load error on " + why);
                    if (_proxyOn)
                    {
                        Flash("Esta página não abriu pelo proxy. O proxy está respondendo?");
                    }
                    UpdateStatus();
                };

                WatchTheEngine();

                // A rebuild brings us here with the previous view's pointer still
                // parented to the window; without this the set collects one more
                // pointer per recovery.
                if (_cursor != null)
                {
                    _cursor.Remove();
                }

                _cursor = new NuiCursor(_window, _web);
                _cursor.Clicked += OnPageClicked;
                _cursor.SetVisual(Store.GetInt("cursorVisual", 1) == 1
                    ? CursorVisual.Native
                    : CursorVisual.Dom);
                Breadcrumbs.Drop("engine ready");

                return true;
            }
            catch (Exception ex)
            {
                _engineFailure = ex.GetType().Name + ": " + ex.Message;
                Breadcrumbs.Drop("ENGINE FAILURE " + _engineFailure);
                return false;
            }
        }

        /// <summary>
        /// Listens to the page's console, which is the one thing the engine says
        /// about itself that this app has never read.
        ///
        /// It is the return channel for <see cref="NuiMediaWatch"/> — see there for
        /// why a <c>console.log</c> is used in preference to an evaluation — and it
        /// also carries the engine's own errors, which is where a media pipeline
        /// says it has failed. The fullscreen signals would have been worth having
        /// here too, and are not available: NUI declares them as internal callbacks
        /// rather than events at API 9.
        /// </summary>
        private void WatchTheEngine()
        {
            try
            {
                _web.ConsoleMessageReceived += delegate (object sender, WebViewConsoleMessageReceivedEventArgs e)
                {
                    try
                    {
                        WebConsoleMessage message = e.ConsoleMessage;
                        if (message != null)
                        {
                            NuiMediaWatch.Console(message.Level.ToString(), message.Text);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A diagnostic channel is not worth an exception on the
                        // engine's own callback thread.
                        DiagLog.Add("console message unreadable: " + ex.Message);
                    }
                };
            }
            catch (Exception ex)
            {
                DiagLog.Add("console watch failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Puts the media census, the decoder cap, the geometry probe, the MSE
        /// probe and, when it is on, the one-video hold into the page. Alongside the
        /// cursor's own script and for the same reason it is re-run on every load: a
        /// navigation takes the previous page's copy with it.
        ///
        /// Separate evaluations rather than one, so that a page which somehow breaks one
        /// of them still gets the others — the census in particular, which is what
        /// would explain the breakage.
        /// </summary>
        private void InstallMediaWatch()
        {
            try
            {
                _web.EvaluateJavaScript(NuiMediaWatch.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("media watch failed: " + ex.Message);
            }

            try
            {
                _web.EvaluateJavaScript(NuiVideoCap.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("video cap failed: " + ex.Message);
            }

            try
            {
                _web.EvaluateJavaScript(NuiVideoRect.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("video rect failed: " + ex.Message);
            }

            try
            {
                _web.EvaluateJavaScript(NuiMseWatch.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("mse watch failed: " + ex.Message);
            }

            // Last, and only when it is on: it is the one script here that changes
            // what the page does, and the probes above are what would explain it
            // if it went wrong.
            if (_oneVideoOn)
            {
                try
                {
                    _web.EvaluateJavaScript(NuiMseHold.Script());
                }
                catch (Exception ex)
                {
                    DiagLog.Add("mse hold failed: " + ex.Message);
                }
            }

            // Overscan BR: outermost, so a page's empty-sound autoplay probe is
            // answered before the probes, the hold or the engine see it. Always on:
            // it only ever stops the engine decoding nothing. See NuiEmptySoundGuard.
            try
            {
                _web.EvaluateJavaScript(NuiEmptySoundGuard.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("empty sound guard failed: " + ex.Message);
            }

            // Overscan BR: and the HLS answer, for a page InstallEarly missed.
            try
            {
                _web.EvaluateJavaScript(NuiHlsAnswer.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("hls answer failed: " + ex.Message);
            }
        }

        /// <summary>
        /// How many early installs one load gets. The engine raises
        /// <c>PageLoading</c> more than once per load, both scripts are idempotent,
        /// and two chances is enough to land one in the new document rather than
        /// the one it replaced.
        /// </summary>
        private const int EarlyInstallsPerLoad = 3;

        private int _earlyInstalls;

        /// <summary>
        /// Overscan BR: the HLS answer and the empty-sound guard, as early in a page
        /// as the engine will tell us there is one. Both answer a question a page
        /// asks while it sets itself up (does native HLS play? may sound start on
        /// its own?), so on load finished they are often a question too late: the
        /// player has already chosen the road that ends in error 4. An evaluation
        /// sent now runs at the next gap in the page's own parsing, which is ahead
        /// of nearly every player.
        ///
        /// Nothing here touches the engine's settings, only the page, which is the
        /// same kind of call load finished has always made.
        /// </summary>
        private void InstallEarly()
        {
            if (_earlyInstalls <= 0 || _web == null)
            {
                return;
            }

            _earlyInstalls--;
            try
            {
                _web.EvaluateJavaScript(NuiHlsAnswer.Script());
                _web.EvaluateJavaScript(NuiEmptySoundGuard.Script());
            }
            catch (Exception ex)
            {
                DiagLog.Add("early install failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Takes a chrome element out of hit-testing without hiding it. Best-effort:
        /// on a build where the property is missing the frame click simply has the
        /// odds it had before.
        /// </summary>
        private static void PassTouchesThrough(View view)
        {
            try
            {
                if (view != null)
                {
                    view.Sensitive = false;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("could not clear Sensitive: " + ex.Message);
            }
        }

        /// <summary>
        /// Gives the session somewhere to live that outlasts the process.
        ///
        /// Without this the engine keeps cookies in memory only, so closing
        /// Overscan logs you out of everything and every launch is a browser that
        /// has never been anywhere (issue #20). The ElmSharp build has done this
        /// since its first release, by way of <c>Context.GetCookieManager</c>; the
        /// NUI WebView hangs the same manager off the view itself and this build
        /// simply never asked it for one.
        ///
        /// The path is the app's own data directory — the only place a sideloaded
        /// app on a retail TV is allowed to write, and where the trail and the
        /// favourites already are.
        /// </summary>
        private void ConfigureCookies()
        {
            try
            {
                WebCookieManager cookies = _web.CookieManager;
                if (cookies == null)
                {
                    _cookieState = "engine offered no cookie manager";
                    DiagLog.Add("cookies: " + _cookieState);
                    return;
                }

                cookies.CookieAcceptPolicy = WebCookieManager.CookieAcceptPolicyType.Always;
                cookies.SetPersistentStorage(DirectoryInfo.Data, WebCookieManager.CookiePersistentStorageType.SqlLite);

                _cookieState = "kept in " + DirectoryInfo.Data;
                Breadcrumbs.Drop("cookies persistent: " + DirectoryInfo.Data);
            }
            catch (Exception ex)
            {
                _cookieState = "FAILED — " + ex.GetType().Name + ": " + ex.Message;
                Breadcrumbs.Drop("cookies: " + _cookieState);
            }
        }

        /// <summary>
        /// Chooses how video reaches the screen, and remembers the choice.
        ///
        /// A TV decodes video on hardware and shows it on an overlay plane, with a
        /// transparent hole punched through the page where the picture belongs.
        /// That is the right arrangement for the set's own browser, which owns the
        /// screen, and a questionable one for an app whose window DALi composites
        /// itself — so this was made a toggle, with the in-page path as the default,
        /// on the theory that a reel feed asking for one plane per video is what
        /// killed Overscan on Instagram (issue #20).
        ///
        /// **The theory was wrong and the default was a mistake.** The reporter's
        /// reels stopped crashing in the same build, but they came back *black*, and
        /// pressing `5` for the overlay is what made them play. The crash it was
        /// meant to fix was the memory-only profile beside it — see
        /// <see cref="ConfigureCookies"/>: with private browsing on, an endless feed
        /// accumulates its storage in RAM until the low-memory killer arrives, which
        /// fits "reels close the app, other video is fine" better than planes ever
        /// did. So the default is the engine's own again, and this is a toggle for
        /// the set that needs the other one rather than an opinion about all of them.
        ///
        /// **Not called on a view that has not loaded a page — and that is now a
        /// habit rather than a finding.** It used to be called during start-up,
        /// straight after the WebView was constructed, and a Tizen 10 set that then
        /// never began a load at all made this the suspect: the black screen
        /// appeared right after that reporter's own `5` put a preference on disk,
        /// and it survived an app restart and a TV restart the way a file does. The
        /// deferral shipped in `build-274157b` and the black screen came back
        /// unchanged, with the trail showing a dead launch under the overlay path
        /// and another under the in-page one. So the setting is not what does it.
        /// The deferral stays because it costs nothing and the start-up ordering is
        /// still the one thing about this property nobody has evidence *for*; what
        /// does not stay is treating it as the explanation for a view that will not
        /// navigate. That is <see cref="CheckSomethingLoaded"/>'s problem now.
        /// </summary>
        private void ApplyVideoPath(bool overlay)
        {
            _videoOverlay = overlay;
            _videoPathPending = false;

            try
            {
                // This one bool is the whole video-path API, which is worth knowing
                // before anyone goes looking for a second knob to fix the in-page
                // black screen with. Reflecting over WebView, WebSettings and the
                // Interop layer of Samsung.Tizen.Ref 9.0.104 turns up no
                // render-rectangle setter and no way to hand the view a native
                // window; WebSettings.EnableExtraFeature is a fixed table of browser
                // UI toggles with nothing about video in it. See *There is no second
                // lever* in docs/INTERNALS.md — in-page is a dead path on the 2025
                // sets and `overlay` is the only branch that puts a picture on screen.
                _web.VideoHoleEnabled = overlay;
                Breadcrumbs.Drop("video path: " + (overlay ? "hardware overlay" : "in page"));
            }
            catch (Exception ex)
            {
                // Best-effort like everything else that reaches past the managed
                // surface: an engine without the property plays video the way it
                // always did.
                DiagLog.Add("video path could not be set: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- chrome

        private void BuildChrome()
        {
            Size2D screen = _window.WindowSize;

            _bar = new View
            {
                Position2D = new Position2D(0, 0),
                Size2D = new Size2D(screen.Width, NuiTheme.BarHeight),
                BackgroundColor = NuiTheme.Panel,
            };
            _window.Add(_bar);

            // Two labels rather than markup: the host has to read from the sofa, the
            // rest of the URL only has to be present.
            // Overscan BR: all three centred on the bar's height rather than placed
            // by hand at the upstream sizes, so the larger text cannot sit low.
            _host = new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, 0),
                Size2D = new Size2D(screen.Width / 3, NuiTheme.BarHeight),
                PointSize = NuiTheme.Pt(15),
                TextColor = NuiTheme.Ink,
                VerticalAlignment = VerticalAlignment.Center,
                Ellipsis = true,
                Text = "Overscan BR",
            };
            _bar.Add(_host);

            _path = new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad + (screen.Width / 3) + 12, 0),
                Size2D = new Size2D(screen.Width / 4, NuiTheme.BarHeight),
                PointSize = NuiTheme.Pt(11),
                TextColor = NuiTheme.InkMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Ellipsis = true,
                Text = string.Empty,
            };
            _bar.Add(_path);

            _status = new TextLabel
            {
                Position2D = new Position2D(screen.Width - (screen.Width / 3) - NuiTheme.Pad, 0),
                Size2D = new Size2D(screen.Width / 3, NuiTheme.BarHeight),
                PointSize = NuiTheme.Pt(11),
                TextColor = NuiTheme.InkMuted,
                HorizontalAlignment = HorizontalAlignment.End,
                VerticalAlignment = VerticalAlignment.Center,
                Ellipsis = true,
                Text = "abrindo…",
            };
            _bar.Add(_status);

            _progress = new View
            {
                Position2D = new Position2D(0, NuiTheme.BarHeight - 4),
                Size2D = new Size2D(0, 4),
                BackgroundColor = NuiTheme.Accent,
            };
            _window.Add(_progress);

            ShowChrome();
        }

        private void ShowChrome()
        {
            _lastActivity = DateTime.UtcNow;
            if (_chromeVisible)
            {
                return;
            }

            _chromeVisible = true;
            _bar.Show();
            _bar.RaiseToTop();
        }

        private void HideChrome()
        {
            _chromeVisible = false;
            _bar.Hide();
            _progress.Hide();
        }

        /// <summary>
        /// Animates the loading marquee and hides the chrome once idle. An
        /// indeterminate bar rather than a real one, to match the ElmSharp build
        /// where LoadProgress is not available.
        /// </summary>
        private bool OnTick()
        {
            FreeUnusedMemoryWhenSettled();

            if (_loading && _chromeVisible)
            {
                int width = _window.WindowSize.Width;
                int span = width / 5;
                _marquee = (_marquee + (span / 4)) % (width + span);
                int x = Math.Max(0, _marquee - span);
                _progress.Position2D = new Position2D(x, NuiTheme.BarHeight - 4);
                _progress.Size2D = new Size2D(Math.Min(span, width - x), 4);
                _progress.Show();
                _progress.RaiseToTop();
            }

            if (_flashUntil != DateTime.MinValue && DateTime.UtcNow > _flashUntil)
            {
                _flashUntil = DateTime.MinValue;
                UpdateStatus();
            }

            // Where a held D-pad key turns into one hover update instead of twenty
            // — see NuiCursor and issue #78. Cheap when nothing moved.
            if (_cursor != null)
            {
                _cursor.FlushPending();
            }

            NoteMemory();

            // Deliberately here and not in the load-finished callback: the engine
            // is still inside its own notification there, and the one thing this
            // property has already been shown to do is upset a view being set up.
            if (_videoPathPending && _pageEverLoaded)
            {
                ApplyVideoPath(_videoOverlay);
            }

            CheckSomethingLoaded();
            TakeRemoteOpen();
            TakeRemoteAction();

            // Here and not in the load callbacks, for the reason _zoomPending
            // gives. Between loads only: a page still loading is the engine
            // still busy with the navigation the zoom would land in.
            if (_zoomPending && !_loading)
            {
                PushZoom();
            }

            if (_videoFull && !_pointerIdleHidden && !_menu.Visible && !_keyboard.IsVisible &&
                DateTime.UtcNow - _lastPointerKey > TimeSpan.FromSeconds(PointerIdleSeconds))
            {
                SleepPointer();
            }

            // The bar is read from a cache that only a finished load refreshes, and
            // a site that moves between pages without finishing one (a reel, a
            // playlist) would leave the address and the star showing the page
            // before. Only while the bar is up, which is a few seconds at a time,
            // and UpdateStatus leaves a flash alone.
            if (_chromeVisible)
            {
                UpdateStatus();
            }

            // The menu joins the list now that the pointer no longer keeps the bar
            // alive: arrowing through the menu is pointer keys, and without this the
            // chrome would time out underneath an open menu.
            bool busy = _loading || _keyboard.IsVisible || _overlayVisible ||
                        (_menu != null && _menu.Visible) ||
                        (_favBar != null && _favBar.IsVisible);
            if (_chromeVisible && !busy && DateTime.UtcNow - _lastActivity > TimeSpan.FromSeconds(4))
            {
                HideChrome();
            }

            return true;
        }

        /// <summary>
        /// Notices a web view that never even starts the load it was given, and
        /// tries the things that could still change the answer.
        ///
        /// Every other failure this app can have leaves a line somewhere: a load
        /// that fails raises PageLoadError, a page that kills the process ends the
        /// trail on its address, a launch that dies never gets here at all. A view
        /// that silently declines to navigate leaves nothing — the app is alive,
        /// the menu opens, the report answers, and the screen is black. Issue #20
        /// spent a round trip on exactly that, and the only reason it was readable
        /// afterwards is that the *absence* of "load started" happened to be
        /// visible in a log that had room for it.
        ///
        /// **The first version of this recovered by putting video back in the page,
        /// and that was worth nothing.** The theory was that a stored overlay
        /// applied to a fresh view is the cause; issue #20's fourth report killed
        /// it twice over. A launch died with the overlay in force and another died
        /// with the in-page path in force, so the setting is not what does it — and
        /// because the recovery applied a fixed value rather than the *other* one,
        /// in every trail we have it re-applied the path that was already in force.
        /// It was a no-op that then declared the view dead. The one thing a rung of
        /// this ladder must do is change something.
        ///
        /// So each rung now does, and each is named on the trail before it is tried,
        /// which makes the report say which one worked:
        ///
        /// 1. **Rebuild the view.** A WebView is a managed object over an engine
        ///    that outlives it; if what is wrong is this view rather than the
        ///    engine's state, a new one is the whole fix and costs nothing.
        /// 2. **Clear the stored session, then rebuild again.** The only thing on
        ///    disk that a dead view could be reading is the profile
        ///    <see cref="ConfigureCookies"/> gave it — the same profile that keeps
        ///    a site logged in, and the only candidate left once the video path is
        ///    out. Clearing it costs the sign-ins, which is why it is the second
        ///    rung and not the first, and why it says so on screen.
        /// 3. **Say the view is not starting loads at all** rather than retry in
        ///    silence, because at that point nothing this app owns is left to try.
        /// </summary>
        private void CheckSomethingLoaded()
        {
            if (_loadAskedAt == DateTime.MinValue ||
                DateTime.UtcNow - _loadAskedAt < TimeSpan.FromSeconds(BlankViewSeconds))
            {
                return;
            }

            _loadAskedAt = DateTime.MinValue;
            _blankRecoveries++;

            Breadcrumbs.Drop("no load began within " + BlankViewSeconds + "s (attempt " +
                             _blankRecoveries + ")");

            // The start screen gets a rung of its own in front of the others, and
            // issue #53 is why. Its set walked the whole ladder — new view, cleared
            // session, new view again — and every rung asked for the *same* page,
            // which was the one thing wrong: a start screen that had swallowed
            // eight copies of itself and outgrown what the engine will load. A
            // typed address loaded first time on the "dead" view. So when what
            // failed is the page this app generates, the first thing to change is
            // the page, and <see cref="ShowHome"/> builds it bare while a recovery
            // is in progress; the report then carries its size either way.
            bool startScreen = _loadAskedFor == null;
            int rung = startScreen ? _blankRecoveries : _blankRecoveries + 1;

            if (rung == 1)
            {
                _blankState = "start screen would not load — shown without its tiles";
                Flash("A tela inicial não abriu. Mostrando sem os seus quadros");
            }
            else if (rung == 2)
            {
                _blankState = (startScreen ? "bare start screen" : "no load") +
                              " would not load either — rebuilt the view";
                Flash("Nada carregou. Reiniciando o motor do navegador");
                if (!RebuildWebView())
                {
                    return;
                }
            }
            else if (rung == 3)
            {
                _blankState = "still nothing after a rebuild — cleared the session and rebuilt";
                Flash("Ainda nada. Limpando os logins salvos e tentando de novo");
                ClearStoredSession();
                if (!RebuildWebView())
                {
                    return;
                }
            }
            else
            {
                // Not "at all": issue #53's set reached this rung and then loaded a
                // typed address at the first ask. What is known here is only that
                // *this* load will not begin, so that is what gets said.
                _blankState = "the web view is not starting this load";
                Breadcrumbs.Drop(_blankState);
                Flash("Esta página não carrega. Tente digitar um endereço (0)");
                return;
            }

            RetryLastLoad();
        }

        /// <summary>
        /// The engine has said something about the load it was given, so the view
        /// is not the dead kind <see cref="CheckSomethingLoaded"/> looks for.
        /// Started, finished and failed all count: the ladder exists for a view
        /// that says nothing at all.
        /// </summary>
        private void EngineAnswered()
        {
            _loadAskedAt = DateTime.MinValue;
            if (_blankRecoveries > 0)
            {
                // Kept, not cleared: which rung got this view loading again
                // is the whole answer issue #20 is waiting for, and it would
                // otherwise be erased by the load that proves it worked.
                _blankState = "recovered after " + _blankRecoveries +
                              (_blankRecoveries == 1 ? " attempt" : " attempts");
                _blankRecoveries = 0;
            }
        }

        /// <summary>
        /// The failed address, the engine's reason and whether the proxy was in
        /// the way, for the trail. The view's own URL is often empty by then
        /// (issue #100's trail was a column of bare "load error on"), so the
        /// error's URL comes first and the one we asked for after it.
        /// </summary>
        private string DescribeLoadError(WebViewPageLoadErrorEventArgs e)
        {
            string url = null, code = null, description = null;
            try
            {
                WebPageLoadError error = e == null ? null : e.PageLoadError;
                if (error != null)
                {
                    url = error.Url;
                    code = error.Code.ToString();
                    description = error.Description;
                }
            }
            catch (Exception ex)
            {
                description = "(error unreadable: " + ex.Message + ")";
            }

            if (string.IsNullOrEmpty(url)) { url = SafeUrl(); }
            if (string.IsNullOrEmpty(url)) { url = _loadAskedFor ?? "(start screen)"; }

            return url + ": " + (code ?? "no code") +
                   (string.IsNullOrEmpty(description) ? string.Empty : ", " + description) +
                   (_proxyOn ? "  (via proxy)" : string.Empty);
        }

        /// <summary>Asks again for whatever the dead view was given, home included.</summary>
        private void RetryLastLoad()
        {
            if (_loadAskedFor == null)
            {
                ShowHome();
            }
            else
            {
                Navigate(_loadAskedFor);
            }
        }

        /// <summary>
        /// Throws this WebView away and builds another one.
        ///
        /// The engine is a process-wide thing that outlives any one view, so this
        /// is not "restart the browser" — it is the cheapest way to find out
        /// whether a view that will not navigate is broken in itself or is reading
        /// something broken underneath it. Nothing else in the app has to know: the
        /// chrome, the keyboard and the menu are DALi objects that never touched
        /// the old view, and everything the new one needs is re-applied here.
        ///
        /// The video preference deliberately goes back to pending rather than being
        /// carried over — see <see cref="ApplyVideoPath"/>. A rebuilt view has not
        /// loaded a page, and the one rule that survives from the old theory is
        /// that this property is only ever handed to a view that has.
        /// </summary>
        private bool RebuildWebView()
        {
            Breadcrumbs.Drop("rebuilding the web view");

            try
            {
                if (_web != null)
                {
                    _window.Remove(_web);
                    _web.Dispose();
                    _web = null;
                }
            }
            catch (Exception ex)
            {
                // Best-effort like everything else past the managed surface: a view
                // that will not go away quietly is still worth replacing.
                Breadcrumbs.Drop("old view would not close: " + ex.GetType().Name + ": " + ex.Message);
            }

            _pageEverLoaded = false;

            if (!TryStartEngine())
            {
                _blankState = "rebuild failed — " + (_engineFailure ?? "no engine");
                Flash("O motor do navegador não inicia. Aperte 3.");
                return false;
            }

            ApplyPreset(_presetIndex);
            Breadcrumbs.Drop("web view rebuilt");
            return true;
        }

        /// <summary>
        /// Empties everything the engine keeps between launches.
        ///
        /// This is the reinstall issue #20 was told it might need, without the
        /// reinstall: the sign-ins go, the favourites and history do not, because
        /// those are ours (<see cref="Store"/>) and live in files the engine has
        /// never heard of. Every call is named on the trail *before* it is made,
        /// for the usual reason — if one of them is what takes the process down,
        /// the last line is the answer.
        /// </summary>
        private void ClearStoredSession()
        {
            try
            {
                WebCookieManager cookies = _web == null ? null : _web.CookieManager;
                if (cookies != null)
                {
                    Breadcrumbs.Drop("clearing session: cookies");
                    cookies.ClearCookies();
                }
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("cookies would not clear: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                WebContext context = _web == null ? null : _web.Context;
                if (context == null)
                {
                    Breadcrumbs.Drop("clearing session: engine offered no context");
                    return;
                }

                Breadcrumbs.Drop("clearing session: cache");
                context.ClearCache();

                Breadcrumbs.Drop("clearing session: web storage");
                context.DeleteAllWebStorage();

                Breadcrumbs.Drop("clearing session: web databases");
                context.DeleteAllWebDatabase();

                Breadcrumbs.Drop("clearing session: application cache");
                context.DeleteAllApplicationCache();

                Breadcrumbs.Drop("session cleared");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("session would not clear: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Writes this process's size to the trail every few seconds. Trail-only
        /// and never to the on-screen log, which keeps 60 lines and would lose
        /// everything worth reading inside a minute — see <see cref="Heartbeat"/>
        /// for the same reasoning about a line that repeats.
        ///
        /// The interval is the point: an app being evicted for memory is not a
        /// moment, it is a slope, and a slope only exists if something was writing
        /// numbers down before anybody knew there was a problem.
        /// </summary>
        private void NoteMemory()
        {
            int seconds = NuiMediaWatch.VideoPlaying ? MemorySecondsWithVideo : MemorySecondsIdle;
            if (DateTime.UtcNow - _lastMemoryNote < TimeSpan.FromSeconds(seconds))
            {
                return;
            }

            _lastMemoryNote = DateTime.UtcNow;

            // Piggy-backed on the one thing that already runs on a known interval,
            // and it is a single stat of an open file. See
            // NativeStdErr.StartSession: the capture runs for the whole session so
            // that a crashing render process gets to say something, and a capture
            // that runs for a whole session has to be bounded.
            NativeStdErr.TrimSession(StdErrLimitBytes);

            long mb = ProcessMemory.ResidentMb();
            if (mb < 0)
            {
                return;
            }

            if (mb > _peakMemoryMb)
            {
                _peakMemoryMb = mb;
            }

            Breadcrumbs.DropToTrail("memory: " + mb + " MB resident (peak " + _peakMemoryMb + ")");
        }

        private void BuildOverlay()
        {
            Size2D screen = _window.WindowSize;
            _overlay = new TextLabel
            {
                Position2D = new Position2D(48, NuiTheme.BarHeight + 24),
                Size2D = new Size2D(screen.Width - 96, screen.Height - NuiTheme.BarHeight - 72),
                PointSize = 9,
                TextColor = NuiTheme.Ink,
                BackgroundColor = NuiTheme.PanelDeep,
                MultiLine = true,
                Text = string.Empty,
            };
            _overlay.Hide();
            _window.Add(_overlay);
        }

        /// <summary>
        /// Every key this build answers to, one line each. "hold OK" comes first
        /// and is not a number, deliberately: on a slim remote it is the only row
        /// here the user can act on, and a list that opens with nine things they
        /// cannot do reads as "this app is not for your remote" (issue #27).
        /// </summary>
        /// <remarks>
        /// Overscan BR keeps only the rows somebody uses every evening. At the
        /// larger text size all fourteen upstream rows made a card taller than
        /// most of the screen, and the rest are in the menu anyway, each with its
        /// key beside it.
        /// </remarks>
        private static readonly string[][] RemoteRows =
        {
            new[] { "segurar OK", "menu com todas as opções" },
            new[] { "CH + / CH -", "rolar a página" },
            new[] { "vermelho", "barra de favoritos" },
            new[] { "verde / amarelo", "letra maior / menor" },
            new[] { "0", "digitar um endereço" },
            new[] { "8", "guardar nos favoritos" },
            new[] { "9", "tela inicial" },
            new[] { "7", "esconder este cartão" },
        };

        /// <summary>
        /// The remote card is the surface that teaches the keys (issue #38): every
        /// key this build answers to, each with the one line that says what it is
        /// for, because it is the only surface visible while a page is open, which
        /// is when a question about a key actually occurs. The start screen says
        /// where this card is and little else; the menu rows stay labels.
        /// </summary>
        private void BuildHints()
        {
            Size2D screen = _window.WindowSize;

            // Overscan BR: the card is sized to its own text rather than to fixed
            // columns. The text stays at the size it was asked for from the sofa;
            // what was spare is the space around it, and that is what a card in the
            // corner of a page being watched should give back. Measured on the set
            // because the engine's points-to-pixels is the set's, not ours, and a
            // TextLabel clips what does not fit. The fixed sizes are the fallback
            // for a toolkit that answers a natural size of zero.
            int pad = NuiTheme.Px(12);
            int gap = NuiTheme.Px(14);
            int rowGap = NuiTheme.Px(4);
            string[][] rows = RemoteRows;

            var title = new TextLabel
            {
                PointSize = NuiTheme.Pt(12),
                TextColor = NuiTheme.Accent,
                Text = "CONTROLE",
            };

            var keys = new TextLabel[rows.Length];
            var labels = new TextLabel[rows.Length];
            int keyWidth = 0;
            int labelWidth = 0;
            int rowHeight = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                keys[i] = new TextLabel
                {
                    PointSize = NuiTheme.Pt(12),
                    TextColor = NuiTheme.Ink,
                    Text = rows[i][0],
                };
                labels[i] = new TextLabel
                {
                    PointSize = NuiTheme.Pt(11),
                    TextColor = NuiTheme.InkMuted,
                    Text = rows[i][1],
                };
                Size2D k = NaturalOr(keys[i], 0, 0);
                Size2D l = NaturalOr(labels[i], 0, 0);
                keyWidth = Math.Max(keyWidth, k.Width);
                labelWidth = Math.Max(labelWidth, l.Width);
                rowHeight = Math.Max(rowHeight, Math.Max(k.Height, l.Height));
            }

            if (keyWidth <= 0 || labelWidth <= 0 || rowHeight <= 0)
            {
                keyWidth = NuiTheme.Px(150);
                labelWidth = NuiTheme.Px(300);
                rowHeight = NuiTheme.Px(30);
            }

            // A little slack on the measured widths: the natural size is the
            // shaped run, and a label exactly that wide can still lose its last
            // glyph to rounding.
            keyWidth += NuiTheme.Px(4);
            labelWidth += NuiTheme.Px(4);
            rowHeight += rowGap;

            int titleHeight = Math.Max(NaturalOr(title, 0, NuiTheme.Px(30)).Height, NuiTheme.Px(24));
            int footerHeight = NuiTheme.Px(24);
            int width = pad + keyWidth + gap + labelWidth + pad;
            int top = pad + titleHeight + rowGap;

            // Title, one row per key, then room for the unrecognised-button line
            // that sits on the bottom edge. Derived rather than a constant, because
            // a card that grows by a key and not by a row draws the last key on top
            // of that line.
            int height = top + (rows.Length * rowHeight) + footerHeight + pad;

            _hints = new View
            {
                Position2D = new Position2D(screen.Width - width - 32, screen.Height - height - 32),
                Size2D = new Size2D(width, height),
                BackgroundColor = NuiTheme.PanelDeep,
                CornerRadius = NuiTheme.Radius,
            };

            title.Position2D = new Position2D(pad, pad);
            title.Size2D = new Size2D(width - (pad * 2), titleHeight);
            _hints.Add(title);

            for (int i = 0; i < rows.Length; i++)
            {
                int y = top + (i * rowHeight);
                keys[i].Position2D = new Position2D(pad, y);
                keys[i].Size2D = new Size2D(keyWidth, rowHeight);
                labels[i].Position2D = new Position2D(pad + keyWidth + gap, y);
                labels[i].Size2D = new Size2D(labelWidth, rowHeight);
                labels[i].VerticalAlignment = VerticalAlignment.Center;
                keys[i].VerticalAlignment = VerticalAlignment.Center;
                _hints.Add(keys[i]);
                _hints.Add(labels[i]);
            }

            // Where an unrecognised button reports itself. A remote we have never
            // seen sends a name we do not answer to, and the user cannot open the
            // diagnostics screen to find out what it was: that screen is behind a
            // number key too. So the name lands here, on the card they can see.
            _hintsFooter = new TextLabel
            {
                Position2D = new Position2D(pad, height - pad - footerHeight),
                Size2D = new Size2D(width - (pad * 2), footerHeight),
                PointSize = NuiTheme.Pt(9),
                TextColor = NuiTheme.InkMuted,
                Text = string.Empty,
            };
            _hints.Add(_hintsFooter);

            _window.Add(_hints);
        }

        private const int FreeMemoryDelaySeconds = 5;

        /// <summary>
        /// Overscan BR: the engine's cache sized for a TV rather than a desktop.
        /// The set has little memory to share with the system, and the browser is
        /// used for one page at a time; the largest model keeps pages and decoded
        /// resources around for a back button that is pressed rarely here. The
        /// middle one still caches, so a site's own files are not fetched twice
        /// on the same page.
        /// </summary>
        private void ConfigureMemory()
        {
            try
            {
                WebContext context = _web == null ? null : _web.Context;
                if (context == null)
                {
                    _memoryState = "engine offered no context";
                    return;
                }

                context.CacheModel = WebContext.CacheModelType.DocumentBrowser;
                _memoryState = "cache " + context.CacheModel;
            }
            catch (Exception ex)
            {
                _memoryState = "cache left alone (" + ex.GetType().Name + ")";
                DiagLog.Add("cache model not set: " + ex.Message);
            }
        }

        /// <summary>
        /// Asks the engine to give back what it holds and is not using, a few
        /// seconds after a page has finished and whenever the platform says memory
        /// is low. Called from the tick, between loads, never from a load event.
        /// </summary>
        private void FreeUnusedMemoryWhenSettled()
        {
            if (_freeMemoryAt == DateTime.MinValue || DateTime.UtcNow < _freeMemoryAt || _loading)
            {
                return;
            }

            _freeMemoryAt = DateTime.MinValue;
            try
            {
                WebContext context = _web == null ? null : _web.Context;
                if (context != null && context.FreeUnusedMemory())
                {
                    _memoryFrees++;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("free unused memory failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Overscan BR: every request for a new window is refused at the engine,
        /// whatever asked for it: a script, a link with target=_blank, a form. The
        /// page that asked stays where it is.
        /// </summary>
        private void RefuseNewWindows()
        {
            try
            {
                // Tizen 9's toolkit has the event and the 8.0 reference this package
                // compiles against does not, so it is found at run time. A set
                // without it keeps the engine's own refusal and the setting above.
                System.Reflection.EventInfo policy = typeof(WebView).GetEvent("NewWindowPolicyDecided");
                if (policy == null)
                {
                    Breadcrumbs.Drop("new windows: engine has no policy event");
                    return;
                }

                EventHandler<WebViewPolicyDecidedEventArgs> refuse = (s, e) =>
                {
                    try
                    {
                        // Same story as the event: 9.0 names the maker PolicyDecisionMaker,
                        // the 8.0 reference only knows the response one.
                        object maker = e == null ? null
                            : e.GetType().GetProperty("PolicyDecisionMaker")?.GetValue(e);
                        WebPolicyDecisionMaker decision = maker as WebPolicyDecisionMaker ?? e?.ResponsePolicyDecisionMaker;
                        if (decision == null)
                        {
                            return;
                        }

                        string url = decision.Url;
                        decision.Ignore();
                        System.Threading.Interlocked.Increment(ref _windowsRefused);
                        _lastWindowRefused = string.IsNullOrEmpty(url) ? "(no address)" : url;
                    }
                    catch (Exception ex)
                    {
                        // On the engine's own callback thread: a diagnostic line,
                        // never an exception.
                        DiagLog.Add("new window not refused: " + ex.Message);
                    }
                };
                policy.AddEventHandler(_web, refuse);
                Breadcrumbs.Drop("new windows: refused");
            }
            catch (Exception ex)
            {
                DiagLog.Add("new-window guard failed: " + ex.Message);
            }
        }

        /// <summary>
        /// A label's natural size, or the fallback when the toolkit cannot say.
        /// Best-effort like everything else that reaches past the managed surface.
        /// </summary>
        private static Size2D NaturalOr(TextLabel label, int width, int height)
        {
            try
            {
                Size2D size = label.NaturalSize2D;
                if (size != null && size.Width > 0 && size.Height > 0)
                {
                    return new Size2D(size.Width, size.Height);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("text size unreadable: " + ex.Message);
            }

            return new Size2D(width, height);
        }

        private void ShowHints(bool visible)
        {
            if (visible)
            {
                _hints.Show();
                _hints.RaiseToTop();
            }
            else
            {
                _hints.Hide();
            }
        }

        // ----------------------------------------------------------------- input

        /// <summary>
        /// The on-screen menu: every function this browser has, in a list the
        /// D-pad can walk. It exists for remotes without a numpad (issue #27),
        /// where it is the only way in, and it shows each entry's key beside it so
        /// it also teaches the shortcut to anyone who has one.
        /// </summary>
        /// <remarks>
        /// Overscan BR splits it in two. Upstream's twenty-one rows only fitted a
        /// 1080-line screen by shrinking every row, which is the opposite of what
        /// this build is for, so the first menu holds what gets used every evening
        /// and "Avançado" holds the rest, untouched. Both are built here, once.
        /// </remarks>
        private void BuildMenu()
        {
            _mainMenu = BuildMenuSet("MENU", new[]
            {
                new RemoteMenu.Item(RemoteMenu.ActionFavouritesBar, "Favoritos", "vermelho"),
                new RemoteMenu.Item(RemoteMenu.ActionAddress, "Ir para um endereço…", "0"),
                new RemoteMenu.Item(RemoteMenu.ActionBookmark, "Guardar esta página", "8"),
                new RemoteMenu.Item(RemoteMenu.ActionHome, "Tela inicial", "9"),
                new RemoteMenu.Item(RemoteMenu.ActionZoomIn, "Letra maior", "verde"),
                new RemoteMenu.Item(RemoteMenu.ActionZoomOut, "Letra menor", "amarelo"),
                new RemoteMenu.Item(RemoteMenu.ActionZoomReset, "Letra no tamanho normal", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionVideoFullscreen, "Vídeo em tela cheia", "azul"),
                new RemoteMenu.Item(RemoteMenu.ActionOpenPlayer, "Abrir só o player", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionTypeInField, "Digitar no campo clicado…", "2"),
                new RemoteMenu.Item(RemoteMenu.ActionAdBlock, "Bloquear anúncios: liga/desliga", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionAdvanced, "Avançado…", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionQuit, "Fechar o navegador", string.Empty),
            });

            _advancedMenu = BuildMenuSet("AVANÇADO", new[]
            {
                new RemoteMenu.Item(RemoteMenu.ActionMainMenu, "‹ Voltar ao menu", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionKeepAddress, "Guardar um endereço digitado…", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionSwitchSite, "Trocar de site", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionResumeLast, "Abrir onde parei: liga/desliga", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionImages, "Imagens: liga/desliga", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionFitPage, "Ajustar a página à tela", "6"),
                new RemoteMenu.Item(RemoteMenu.ActionKeysToPage, "Mandar as teclas para a página", "4"),
                new RemoteMenu.Item(RemoteMenu.ActionIdentity, "Abrir como outro navegador…", "1"),
                new RemoteMenu.Item(RemoteMenu.ActionVideoPath, "Vídeo: na página / da TV", "5"),
                new RemoteMenu.Item(RemoteMenu.ActionOneVideo, "Um vídeo por vez: liga/desliga", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionProxy, "Proxy: liga/desliga", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionProxyAddress, "Endereço do proxy…", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionForgetSite, "Esquecer os ajustes deste site", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionHints, "Cartão do controle: mostra/esconde", "7"),
                new RemoteMenu.Item(RemoteMenu.ActionRemoteCard, "Controle pelo computador…", string.Empty),
                new RemoteMenu.Item(RemoteMenu.ActionDiagnostics, "Diagnóstico", "3"),
            });

            UseMenu(_mainMenu);
        }

        private MenuSet BuildMenuSet(string heading, RemoteMenu.Item[] items)
        {
            var set = new MenuSet { Menu = new RemoteMenu(items) };
            Size2D screen = _window.WindowSize;

            int header = NuiTheme.Px(56);
            int footer = NuiTheme.Px(44);
            int shortcutWidth = NuiTheme.Px(150);

            // Rows shrink rather than run off the screen. A row past the bottom
            // edge is still picked out by the highlight, so the menu would be
            // choosing things nobody could read. The floor is the label's own
            // height: below it the text would be clipped instead.
            // A long menu drops to the smaller of the two upstream text sizes
            // before its rows are allowed to get shorter than that text.
            int room = screen.Height - (NuiTheme.Pad * 4) - header - footer;
            int fit = room / set.Menu.Count;
            bool big = fit >= NuiTheme.Px(56);
            int labelSize = big ? 15 : 13;
            int floor = NuiTheme.Px(big ? 42 : 36) + 6;
            set.RowHeight = Math.Max(floor, Math.Min(NuiTheme.Px(62), fit));
            int rowHeight = set.RowHeight;
            int width = NuiTheme.Px(760);
            int height = (NuiTheme.Pad * 2) + header + (set.Menu.Count * rowHeight) + footer;

            set.Panel = new View
            {
                Position2D = new Position2D((screen.Width - width) / 2, (screen.Height - height) / 2),
                Size2D = new Size2D(width, height),
                BackgroundColor = NuiTheme.PanelDeep,
                CornerRadius = NuiTheme.Radius,
            };

            set.Panel.Add(new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, NuiTheme.Pad),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2), NuiTheme.Px(36)),
                PointSize = NuiTheme.Pt(13),
                TextColor = NuiTheme.Accent,
                Text = heading,
            });

            // Behind the rows, so a row's own text is never covered by it.
            set.Highlight = new View
            {
                Position2D = new Position2D(NuiTheme.Pad - 8, NuiTheme.Pad + header),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2) + 16, rowHeight - 6),
                BackgroundColor = NuiTheme.Accent,
                CornerRadius = 8f,
            };
            set.Panel.Add(set.Highlight);

            set.Labels = new TextLabel[set.Menu.Count];
            set.Shortcuts = new TextLabel[set.Menu.Count];

            for (int i = 0; i < set.Menu.Count; i++)
            {
                RemoteMenu.Item item = set.Menu.ItemAt(i);
                int y = NuiTheme.Pad + header + (i * rowHeight);

                set.Labels[i] = new TextLabel
                {
                    Position2D = new Position2D(NuiTheme.Pad, y),
                    Size2D = new Size2D(width - (NuiTheme.Pad * 2) - shortcutWidth, rowHeight - 6),
                    PointSize = NuiTheme.Pt(labelSize),
                    TextColor = NuiTheme.Ink,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = item.Label,
                };
                set.Panel.Add(set.Labels[i]);

                set.Shortcuts[i] = new TextLabel
                {
                    Position2D = new Position2D(width - NuiTheme.Pad - shortcutWidth, y),
                    Size2D = new Size2D(shortcutWidth, rowHeight - 6),
                    PointSize = NuiTheme.Pt(11),
                    HorizontalAlignment = HorizontalAlignment.End,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextColor = NuiTheme.InkMuted,
                    Text = item.Shortcut,
                };
                set.Panel.Add(set.Shortcuts[i]);
            }

            set.Panel.Add(new TextLabel
            {
                Position2D = new Position2D(NuiTheme.Pad, height - NuiTheme.Pad - NuiTheme.Px(30)),
                Size2D = new Size2D(width - (NuiTheme.Pad * 2), NuiTheme.Px(28)),
                PointSize = NuiTheme.Pt(10),
                TextColor = NuiTheme.InkMuted,
                Text = "cima/baixo escolhem  ·  OK confirma  ·  Voltar fecha",
            });

            _window.Add(set.Panel);
            set.Panel.Hide();
            return set;
        }

        /// <summary>
        /// Points the menu fields at one of the two menus. The one being left is
        /// closed and hidden first, so the two can never be up at once.
        /// </summary>
        private void UseMenu(MenuSet set)
        {
            if (_menu != null && _menu != set.Menu)
            {
                _menu.Close();
                _menuPanel.Hide();
            }

            _menu = set.Menu;
            _menuPanel = set.Panel;
            _menuLabels = set.Labels;
            _menuShortcuts = set.Shortcuts;
            _menuHighlight = set.Highlight;
            _menuRowHeight = set.RowHeight;
        }

        private void ShowMenu(bool visible)
        {
            if (visible)
            {
                if (_favBar != null && _favBar.IsVisible)
                {
                    _favBar.Close();
                }

                _menu.Open();
                DrawMenuSelection();
                _menuPanel.Show();
                _menuPanel.RaiseToTop();
                DiagLog.Add("menu opened");
            }
            else
            {
                _menu.Close();
                _menuPanel.Hide();

                // Every opening starts at the short menu, for the reason
                // RemoteMenu.Open gives for starting at the first row.
                UseMenu(_mainMenu);
            }
        }

        private void DrawMenuSelection()
        {
            int rowHeight = _menuRowHeight;
            _menuHighlight.Position2D = new Position2D(
                _menuHighlight.Position2D.X,
                NuiTheme.Pad + NuiTheme.Px(56) + (_menu.SelectedIndex * rowHeight));

            for (int i = 0; i < _menuLabels.Length; i++)
            {
                bool picked = i == _menu.SelectedIndex;

                // On the accent bar, muted grey is unreadable and the accent itself
                // is invisible: both rows swap to the panel colour instead.
                _menuLabels[i].TextColor = picked ? NuiTheme.PanelDeep : NuiTheme.Ink;
                _menuShortcuts[i].TextColor = picked ? NuiTheme.PanelDeep : NuiTheme.InkMuted;
            }
        }

        /// <summary>
        /// Everything the browser can be asked to do, in one place, so a number key
        /// and a menu row cannot drift apart.
        /// </summary>
        private void RunAction(string id)
        {
            // A number key can fire an action with the menu still up, and two
            // panels fighting over the middle of the screen reads as a glitch.
            // Reaching here from the menu has already closed it, so this is only
            // ever the shortcut's doing.
            if (_menu.Visible)
            {
                ShowMenu(false);
            }

            switch (id)
            {
                case RemoteMenu.ActionFavouritesBar:
                    OpenFavouritesBar();
                    break;

                case RemoteMenu.ActionZoomIn:
                    ChangeZoom(1);
                    break;

                case RemoteMenu.ActionZoomOut:
                    ChangeZoom(-1);
                    break;

                case RemoteMenu.ActionZoomReset:
                    ChangeZoom(0);
                    break;

                case RemoteMenu.ActionVideoFullscreen:
                    ToggleVideoFullscreen();
                    break;

                case RemoteMenu.ActionOpenPlayer:
                    OpenPlayerFrame();
                    break;

                case RemoteMenu.ActionAdvanced:
                    UseMenu(_advancedMenu);
                    ShowMenu(true);
                    break;

                case RemoteMenu.ActionMainMenu:
                    UseMenu(_mainMenu);
                    ShowMenu(true);
                    break;

                case RemoteMenu.ActionAddress:
                    _keyboard.Open(KeyboardTarget.Address, _atHome ? string.Empty : _cachedUrl);
                    break;

                case RemoteMenu.ActionHome:
                    ShowHome();
                    break;

                case RemoteMenu.ActionBookmark:
                    ToggleFavourite();
                    break;

                case RemoteMenu.ActionKeepAddress:
                    // Prefilled with where we are, because the address somebody
                    // wants to keep is usually the one they are looking at with a
                    // few segments taken off the end (issue #80).
                    _keyboard.Open(KeyboardTarget.Favourite,
                                   _atHome || _cachedUrl == "-" ? string.Empty : _cachedUrl);
                    break;

                case RemoteMenu.ActionResumeLast:
                    ToggleResumeLast();
                    break;

                case RemoteMenu.ActionTypeInField:
                    _keyboard.Open(KeyboardTarget.PageField, string.Empty);
                    break;

                case RemoteMenu.ActionIdentity:
                    CycleIdentity();
                    break;

                case RemoteMenu.ActionSwitchSite:
                    SwitchSite();
                    break;

                case RemoteMenu.ActionForgetSite:
                    ForgetSite();
                    break;

                case RemoteMenu.ActionKeysToPage:
                    _keysToPage = !_keysToPage;
                    DiagLog.Add(_keysToPage ? "keys -> page" : "keys -> cursor");

                    // The pointer goes with the keys. It used to stay on screen
                    // under the page's own drawing, where it was merely odd; drawn
                    // by us it would sit over the page as a thing that no longer
                    // moves, which reads as a freeze.
                    if (_keysToPage)
                    {
                        _cursor.Hide();
                    }
                    else
                    {
                        _cursor.Show();
                    }

                    Flash(_keysToPage ? "As teclas vão para a página" : "As teclas movem o ponteiro");
                    UpdateStatus();
                    break;

                case RemoteMenu.ActionPointer:
                    _cursor.SetVisual(_cursor.Visual == CursorVisual.Native
                        ? CursorVisual.Dom
                        : CursorVisual.Native);
                    Store.Set("cursorVisual", _cursor.Visual == CursorVisual.Native ? 1 : 0);
                    Flash(_cursor.Visual == CursorVisual.Native
                        ? "Ponteiro do navegador (mais rápido em páginas pesadas)"
                        : "Ponteiro da página (uma seta, tão rápida quanto a página)");
                    break;

                case RemoteMenu.ActionFitPage:
                    _viewportFix = !_viewportFix;
                    Store.Set("viewportFix", _viewportFix);
                    DiagLog.Add("viewport fix " + (_viewportFix ? "ON" : "OFF"));
                    ApplyViewportFix();
                    UpdateStatus();
                    break;

                case RemoteMenu.ActionImages:
                    ToggleImages();
                    break;

                case RemoteMenu.ActionAdBlock:
                    ToggleAdBlock();
                    break;

                case RemoteMenu.ActionOneVideo:
                    ToggleOneVideo();
                    break;

                case RemoteMenu.ActionProxy:
                    ToggleProxy();
                    break;

                case RemoteMenu.ActionProxyAddress:
                    _proxyPending = false;
                    _keyboard.Open(KeyboardTarget.Proxy, _proxy == null ? string.Empty : _proxy.Entry);
                    break;

                case RemoteMenu.ActionVideoPath:
                    ApplyVideoPath(!_videoOverlay);
                    _videoPathChosen = true;
                    Store.Set("videoOverlay", _videoOverlay);
                    Flash(_videoOverlay
                        ? "Vídeo pelo caminho da própria TV"
                        : "Vídeo dentro da página (tente se o vídeo fechar o app)");

                    // The engine reads this when a video element is created, so the
                    // page that is already open keeps whatever it started with.
                    _web.Reload();
                    break;

                case RemoteMenu.ActionHints:
                    _hintsWanted = !_hintsWanted;
                    ShowHints(_hintsWanted);
                    Store.Set("hints", _hintsWanted);
                    break;

                case RemoteMenu.ActionDiagnostics:
                    ShowOverlay(!_overlayVisible);
                    break;

                case RemoteMenu.ActionRemoteCard:
                    ShowRemoteCard();
                    break;

                case RemoteMenu.ActionQuit:
                    Exit();
                    break;
            }
        }

        /// <summary>
        /// The menu's own key handling, while it is up. Returns false for a key the
        /// menu does not want, which the caller then treats as normal — a number
        /// key still does its job with the menu open rather than being swallowed.
        /// </summary>
        private bool HandleMenuKey(string key)
        {
            switch (key)
            {
                case RemoteKeys.Up:
                    _menu.Move(-1);
                    DrawMenuSelection();
                    return true;

                case RemoteKeys.Down:
                    _menu.Move(1);
                    DrawMenuSelection();
                    return true;

                case RemoteKeys.Ok:
                case RemoteKeys.OkKeypad:
                    string id = _menu.Selected.Id;
                    ShowMenu(false);
                    RunAction(id);
                    return true;

                case RemoteKeys.Back:
                case RemoteKeys.Left:
                    // From "Avançado", back means the short menu, not out of the
                    // menu altogether: that is where the person came from.
                    bool advanced = _menu == _advancedMenu.Menu;
                    ShowMenu(false);
                    if (advanced)
                    {
                        ShowMenu(true);
                    }

                    return true;
            }

            if (RemoteKeys.IsMenuKey(key) || RemoteKeys.IsMediaKey(key))
            {
                ShowMenu(false);
                return true;
            }

            return false;
        }

        private void OnWindowKey(object sender, Window.KeyEventArgs e)
        {
            if (e.Key.State != Key.StateType.Down)
            {
                return;
            }

            string key = e.Key.KeyPressedName;

            // Overscan BR: a key while the pointer is asleep in fullscreen wakes
            // it. Arrows and OK stop there; anything else (Back, blue, the menu)
            // still does its own job on the same press.
            _lastPointerKey = DateTime.UtcNow;
            if (_pointerIdleHidden)
            {
                WakePointer();
                if (key == RemoteKeys.Left || key == RemoteKeys.Right || key == RemoteKeys.Up ||
                    key == RemoteKeys.Down || key == RemoteKeys.Ok || key == RemoteKeys.OkKeypad)
                {
                    return;
                }
            }

            // Only the buttons that earn the bar its four seconds: going back, the
            // menu, a numbered shortcut. The bar used to come up on every key down,
            // each repeat re-arming its own idle timer, so it sat on screen the
            // whole time anyone was moving the cursor (issue #20). Taking the
            // pointer keys out was not enough — the reporter pages through a feed
            // with the channel rocker, and that brought the bar back on every
            // page — so the rule is now the short list of buttons that do show it,
            // and nothing a person holds down to get through a page is on it.
            if (RemoteKeys.IsChromeKey(key))
            {
                ShowChrome();
            }

            if (_keyboard != null && _keyboard.IsVisible && _keyboard.HandleKey(key))
            {
                return;
            }

            if (_web == null)
            {
                // The engine never started, so there is no page and no menu — but
                // the diagnostics screen is the whole reason someone is still
                // pressing buttons, and on a remote with no numpad key 3 cannot
                // get them there. Any menu button does instead.
                if (key == RemoteKeys.Num3 || RemoteKeys.IsMenuKey(key) || RemoteKeys.IsMediaKey(key))
                {
                    ShowOverlay(!_overlayVisible);
                }
                else if (key == RemoteKeys.Back)
                {
                    Exit();
                }

                return;
            }

            if (OkHoldConsumed(key))
            {
                return;
            }

            if (_menu.Visible && HandleMenuKey(key))
            {
                return;
            }

            if (_favBar.IsVisible && _favBar.HandleKey(key))
            {
                return;
            }

            if (RemoteKeys.IsMenuKey(key) || (RemoteKeys.IsMediaKey(key) && !_keysToPage))
            {
                ShowMenu(true);
                return;
            }

            switch (key)
            {
                case RemoteKeys.Left:
                    if (!_keysToPage) { _cursor.Move(-1, 0); }
                    break;
                case RemoteKeys.Right:
                    if (!_keysToPage) { _cursor.Move(1, 0); }
                    break;
                case RemoteKeys.Up:
                    if (!_keysToPage) { _cursor.Move(0, -1); }
                    break;
                case RemoteKeys.Down:
                    if (!_keysToPage) { _cursor.Move(0, 1); }
                    break;

                case RemoteKeys.Ok:
                case RemoteKeys.OkKeypad:
                    if (!_keysToPage) { _cursor.Click(); }
                    break;

                case RemoteKeys.ChannelUp:
                    _cursor.ScrollPage(-1);
                    break;
                case RemoteKeys.ChannelDown:
                    _cursor.ScrollPage(1);
                    break;

                case RemoteKeys.Back:
                    if (_overlayVisible)
                    {
                        ShowOverlay(false);
                    }
                    else if (_videoFull)
                    {
                        ToggleVideoFullscreen();
                    }
                    else if (_web.CanGoBack())
                    {
                        _web.GoBack();
                    }
                    else if (!_atHome)
                    {
                        ShowHome();
                    }
                    else
                    {
                        Exit();
                    }

                    break;

                case RemoteKeys.Num0:
                    RunAction(RemoteMenu.ActionAddress);
                    break;

                case RemoteKeys.Num1:
                    RunAction(RemoteMenu.ActionIdentity);
                    break;

                case RemoteKeys.Num2:
                    RunAction(RemoteMenu.ActionTypeInField);
                    break;

                case RemoteKeys.Num3:
                    RunAction(RemoteMenu.ActionDiagnostics);
                    break;

                case RemoteKeys.Num4:
                    RunAction(RemoteMenu.ActionKeysToPage);
                    break;

                case RemoteKeys.Num5:
                    RunAction(RemoteMenu.ActionVideoPath);
                    break;

                case RemoteKeys.Num6:
                    RunAction(RemoteMenu.ActionFitPage);
                    break;

                case RemoteKeys.Num7:
                    RunAction(RemoteMenu.ActionHints);
                    break;

                case RemoteKeys.Num8:
                    RunAction(RemoteMenu.ActionBookmark);
                    break;

                case RemoteKeys.Num9:
                    RunAction(RemoteMenu.ActionHome);
                    break;

                case RemoteKeys.Info:
                    // Overscan BR: Info only brings the bar up (IsChromeKey already
                    // did). Upstream made it the images switch, kept per site, and
                    // on this remote it was pressed by accident: a site came back
                    // with images off and its video player stopped working, with
                    // nothing on screen to say why. Images stay in "Avançado".
                    break;

                default:
                    if (RemoteKeys.IsSwitchKey(key))
                    {
                        // Overscan BR: red is the favourites bar. Switching site
                        // is still in "Avançado".
                        if (_favBar.IsVisible)
                        {
                            _favBar.Close();
                        }
                        else
                        {
                            RunAction(RemoteMenu.ActionFavouritesBar);
                        }
                    }
                    else if (RemoteKeys.IsZoomInKey(key))
                    {
                        RunAction(RemoteMenu.ActionZoomIn);
                    }
                    else if (RemoteKeys.IsZoomOutKey(key))
                    {
                        RunAction(RemoteMenu.ActionZoomOut);
                    }
                    else if (RemoteKeys.IsFullscreenKey(key))
                    {
                        RunAction(RemoteMenu.ActionVideoFullscreen);
                    }
                    else
                    {
                        NoteUnknownKey(key);
                    }

                    break;
            }
        }

        /// <summary>
        /// Watches OK for a hold, and opens the menu on one. Returns true when the
        /// press belongs to a hold and must go no further.
        /// </summary>
        /// <remarks>
        /// Two things have to stay true here. A single press must behave exactly as
        /// it always did — a hold is detected from the repeats that follow, never by
        /// delaying the press itself, so no click in this browser waits on a Up
        /// event some firmware may not send. And once the menu is up, the rest of
        /// the hold has to be thrown away: the presses still arriving are the same
        /// button the user has not let go of yet, and letting them through would
        /// pick the first row of the menu they were only just opening.
        /// </remarks>
        private bool OkHoldConsumed(string key)
        {
            if (key != RemoteKeys.Ok && key != RemoteKeys.OkKeypad)
            {
                _okRepeats = 0;
                return false;
            }

            DateTime now = DateTime.UtcNow;
            bool sameHold = (now - _lastOk).TotalMilliseconds <= OkRepeatGapMs;
            _lastOk = now;

            if (!sameHold)
            {
                // A press in its own right: it clicks, or it picks a menu row.
                _okRepeats = 0;
                return false;
            }

            if (_menu.Visible)
            {
                return true;
            }

            _okRepeats++;
            if (_okRepeats < OkHoldRepeats)
            {
                return false;
            }

            _okRepeats = 0;
            ShowMenu(true);
            return true;
        }

        /// <summary>
        /// A button we have no name for. It goes to the diagnostics log as before,
        /// and now also onto the hints card — the user who most needs to report an
        /// unknown key is the one whose remote cannot reach the diagnostics screen.
        /// </summary>
        private void NoteUnknownKey(string key)
        {
            DiagLog.Add("unhandled key: " + key);

            if (_hintsFooter != null)
            {
                _hintsFooter.Text = "botão desconhecido: " + key;
            }
        }

        /// <summary>
        /// What the page script reported about the click. A FIELD: prefix means a
        /// text field: it cannot be focused (that raises the IME), so opening the
        /// grid is how text gets in. FRAME: means the click landed on a frame from
        /// another origin, which no script can reach into.
        /// </summary>
        private void OnPageClicked(string result)
        {
            if (result == null)
            {
                return;
            }

            if (result.StartsWith("FIELD:", StringComparison.Ordinal))
            {
                _keyboard.Open(KeyboardTarget.PageField, string.Empty);
            }
            else if (result.StartsWith("FRAME:", StringComparison.Ordinal))
            {
                // A cross-origin frame — a captcha, an embedded sign-in. Nothing in
                // the page script can reach inside it, so the click has to be a real
                // one. Issue #20 is this, on Instagram's reCAPTCHA.
                DiagLog.Add("click landed on a cross-origin frame: " + result);
                ClickThroughFrame(result);
            }
        }

        /// <summary>
        /// Clicks inside a cross-origin frame, through the engine rather than
        /// through the page or the platform under it.
        ///
        /// The point comes back with the script's own report — see
        /// <see cref="PageScript"/> — and is in the page's CSS pixels, which is the
        /// space the engine hit-tests in. Deriving it from the window instead would
        /// be right today and wrong the moment a page is zoomed.
        ///
        /// <see cref="NuiInspectorInput"/> is the way in; <see cref="NuiNativeTouch"/>
        /// stays as the fallback for a set where no inspector will start, on the
        /// principle that a click that probably does nothing still beats no click at
        /// all — but on the set in issue #20 it is the one that does nothing, four
        /// builds over.
        /// </summary>
        private void ClickThroughFrame(string report)
        {
            try
            {
                int x;
                int y;
                bool havePoint = PointIn(report, out x, out y);

                // Wiped before anything goes out, and not after: the click leaves on
                // another thread, and a witness cleared behind it would erase the
                // very event it was there to catch.
                _cursor.ClearNativeWitness();

                bool inspector = havePoint &&
                                 NuiInspector.Ensure(_web) != 0 &&
                                 NuiInspectorInput.Click(x, y);

                if (!inspector)
                {
                    // Window pixels, because the platform below the engine knows
                    // nothing about a page's coordinate space.
                    Size2D screen = _window.WindowSize;
                    int windowX = (int)Math.Round(_cursor.FractionX * screen.Width);
                    int windowY = (int)Math.Round(_cursor.FractionY * screen.Height);

                    if (!NuiNativeTouch.Click(_window, windowX, windowY))
                    {
                        Flash("Não dá para clicar dentro deste quadro");
                        return;
                    }
                }

                // And then ask the page what actually arrived. The click itself is
                // blind from out here — issue #20 reported "fed tap at 705,126" and
                // an unmoved captcha, which narrows nothing down — so the answer has
                // to come from the only place that can see real input: the page.
                _cursor.ReportNativeWitness(FrameWitnessDelay, witness =>
                {
                    _frameWitness = witness;
                    DiagLog.Add("frame witness: " + witness);

                    if (inspector && !NuiInspectorInput.Succeeded)
                    {
                        Flash("Não dá para clicar dentro deste quadro");
                    }
                });
            }
            catch (Exception ex)
            {
                DiagLog.Add("frame click failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The point out of a <c>FRAME:IFRAME@660,124</c> report. False for a build
        /// of the script old enough not to carry one, which then takes the native
        /// path and its window coordinates.
        /// </summary>
        private static bool PointIn(string report, out int x, out int y)
        {
            x = 0;
            y = 0;

            int at = report.IndexOf('@');
            int comma = report.IndexOf(',', at + 1);
            if (at < 0 || comma < 0)
            {
                return false;
            }

            return int.TryParse(report.Substring(at + 1, comma - at - 1),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
                   int.TryParse(report.Substring(comma + 1),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
        }

        /// <summary>
        /// Remembers what was typed as the page to open at launch (issue #15). An
        /// empty entry clears it and the start screen comes back.
        /// </summary>
        private void OnStartPageSet(string text)
        {
            if (string.IsNullOrEmpty((text ?? string.Empty).Trim()))
            {
                _startupUrl = null;
                StartPage.SetAddress(null);
                DiagLog.Add("start page cleared");
                Flash("Página inicial apagada");
                return;
            }

            _startupUrl = Urls.Normalize(text);
            StartPage.SetAddress(_startupUrl);
            DiagLog.Add("start page = " + _startupUrl);
            Flash("A partir de agora abre aqui");
            Navigate(_startupUrl);
        }

        private void OnKeyboardCommitted(string text, KeyboardTarget target)
        {
            if (target == KeyboardTarget.Address)
            {
                Navigate(Urls.Normalize(text));
                return;
            }

            if (target == KeyboardTarget.Favourite)
            {
                KeepAddress(text);
                return;
            }

            if (target == KeyboardTarget.Proxy)
            {
                OnProxyEntered(text);
                return;
            }

            // One evaluation, not two: NUI keeps a single pending result handler, so
            // overlapping calls deliver both replies to the last one registered.
            try
            {
                _web.EvaluateJavaScript(
                    "(function(){var t=String(window." + PageScript.Namespace + ".type(" +
                    Urls.JsString(text) + "));var s=String(window." + PageScript.Namespace +
                    ".submit());return 'typed into '+t+', submit -> '+s;})()",
                    result =>
                    {
                        if (!string.IsNullOrEmpty(result))
                        {
                            DiagLog.Add(result);
                        }
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("typing failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ navigation

        private void ShowHome()
        {
            try
            {
                _atHome = true;
                _loadAskedAt = DateTime.UtcNow;
                _loadAskedFor = null;

                // Bare while the blank-view ladder is climbing — see
                // CheckSomethingLoaded. The tiles are the only part of this page
                // that changes from one launch to the next, so they are the part a
                // recovery has to take away to learn anything.
                bool bare = _blankRecoveries > 0;
                IList<Bookmark> favourites = bare ? new List<Bookmark>() : Store.AllFavourites;
                IList<Bookmark> history = bare ? new List<Bookmark>() : Store.RecentHistory;
                string html = HomePage.Build(favourites, history, Urls.Home);

                // The engine carries this page as a percent-encoded data: URL, so
                // what it is asked to load is a few times this long, and Chromium
                // refuses any URL past 2 MB in silence. The report says how close.
                _startPageState = (html.Length / 1024) + " KB of HTML, " +
                                  Math.Min(favourites.Count, 12) + " favourite + " +
                                  Math.Min(history.Count, 8) + " recent tiles" +
                                  (bare ? "  (bare — recovering)" : string.Empty);

                _web.LoadHtmlString(html);
                _cursor.Center();
                DiagLog.Add("home screen shown" + (bare ? " (bare)" : string.Empty));
            }
            catch (Exception ex)
            {
                DiagLog.Add("home screen failed: " + ex.Message);
                Navigate(Urls.Home);
            }
        }

        private void Navigate(string url)
        {
            _atHome = false;
            _loadAskedAt = DateTime.UtcNow;
            _loadAskedFor = url;
            Breadcrumbs.Drop("navigate: " + url);

            // Before the request, so a site we are opening ourselves is asked with
            // its own identity the first time and needs no reload at all.
            ApplySiteRules(url, false);
            _web.LoadUrl(url);
            _cursor.Center();
            UpdateStatus();
        }

        /// <summary>
        /// The switch is read on the request thread, so flipping it takes effect on
        /// the next request; the reload is so the page that is open shows the
        /// difference rather than the next one. Menu only — every digit and Info
        /// are spoken for, and the menu is what a slim remote uses anyway.
        /// </summary>
        private void ToggleAdBlock()
        {
            _adBlockOn = !_adBlockOn;
            NuiAdBlock.Enabled = _adBlockOn;
            Store.Set("adblock", _adBlockOn);
            DiagLog.Add("ad block " + (_adBlockOn ? "ON" : "OFF"));
            Flash(_adBlockOn ? "Bloqueio de anúncios ligado" : "Bloqueio de anúncios desligado");
            UpdateStatus();
            try
            {
                _web.Reload();
            }
            catch (Exception ex)
            {
                DiagLog.Add("reload after ad block toggle failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Issue #100's fix, switchable. The script lives in the page, so like the ad
        /// block this reloads to make the page that is open show the difference.
        /// Menu only: no digit is free. Off is for a site whose player will not wait
        /// for its preload; the report says which it was.
        /// </summary>
        private void ToggleOneVideo()
        {
            _oneVideoOn = !_oneVideoOn;
            NuiMseHold.Enabled = _oneVideoOn;
            Store.Set("onevideo", _oneVideoOn);
            DiagLog.Add("one video at a time " + (_oneVideoOn ? "ON" : "OFF"));
            Flash(_oneVideoOn
                ? "Um vídeo por vez: o próximo só carrega quando tocar"
                : "Vídeos carregam livres: um pode travar quando o próximo é preparado");
            UpdateStatus();
            try
            {
                _web.Reload();
            }
            catch (Exception ex)
            {
                DiagLog.Add("reload after one-video toggle failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The one real speed-up available: the engine cannot be made faster, but it
        /// can be given much less to do.
        ///
        /// Remembered for the site it was pressed on, not for the browser (issue
        /// #74): the reporter turns images off on Instagram and needs them on for
        /// Spotify, which will not play without them, and one switch for both means
        /// flipping it by hand on every crossing. Pressed on the start screen —
        /// which is on no site — it moves the browser-wide setting instead, so
        /// there is still a way to say "everywhere".
        /// </summary>
        private void ToggleImages()
        {
            bool on = !_imagesOn;
            SiteRule rule = SiteRules.SetImages(PageUrl(), on);
            if (rule == null)
            {
                _defaultImagesOn = on;
                Store.Set("images", on);

                // The browser-wide answer moved, so what is on the view is no
                // longer what any site was last worked out to want. Forgetting
                // which site that was is what makes the next load work it out
                // again — otherwise a site with a rule, left through the start
                // screen and come back to, keeps the setting made while away.
                _siteKey = null;
                Flash(on ? "Imagens ligadas" : "Imagens desligadas (mais rápido)");
            }
            else
            {
                Flash((on ? "Imagens ligadas em " : "Imagens desligadas em ") + rule.Site);
            }

            ApplyImages(on);
            Reload("images");
        }

        /// <summary>
        /// The next identity in the list, remembered for this site. Same split as
        /// the images switch: on a site it is that site's answer, on the start
        /// screen it is the browser's.
        /// </summary>
        private void CycleIdentity()
        {
            int next = (_presetIndex + 1) % _presets.Length;
            ApplyPreset(next);

            SiteRule rule = SiteRules.SetUa(PageUrl(), next);
            if (rule == null)
            {
                _defaultPresetIndex = next;
                Store.Set("uaPreset", next);

                // See ToggleImages: a moved browser-wide answer invalidates the
                // site the view was last set up for.
                _siteKey = null;
                Flash(ShortPreset(_presets[next].Label));
            }
            else
            {
                Flash(ShortPreset(_presets[next].Label) + " em " + rule.Site);
            }

            Reload("identity");
        }

        /// <summary>
        /// Goes round the favourites — issue #75, and #100's note that the first
        /// shape only ever reached two sites. From a site with a favourite it opens
        /// the next favourite's site, at the last page seen there; with no round to
        /// go it falls back to the site the user just came from. SiteRules.OtherSite
        /// has the reasoning.
        /// </summary>
        private void SwitchSite()
        {
            Bookmark other = SiteRules.OtherSite(Store.AllFavourites, Store.RecentHistory, PageUrl());
            if (other == null)
            {
                Flash("Ainda não tem outro site para trocar");
                return;
            }

            Flash("\u2192 " + SiteRules.KeyFor(other.Url));
            Navigate(other.Url);
        }

        /// <summary>
        /// Takes this site back to the browser-wide answers. Without it a rule can
        /// only be undone by flipping it back to whatever the default happens to
        /// be, which is not the same thing and leaves the site on the report as
        /// remembered.
        /// </summary>
        private void ForgetSite()
        {
            string url = PageUrl();
            SiteRule rule = SiteRules.Forget(url);
            if (rule == null)
            {
                Flash(url == null ? "Abra um site primeiro" : "Não há ajustes guardados para este site");
                return;
            }

            DiagLog.Add("site rules: forgot " + rule.Site);
            Flash("Ajustes esquecidos para " + rule.Site);

            // A change of settings like any other, so it is applied and reloaded
            // like any other. The forgotten site is no longer the applied one.
            _siteKey = null;
            ApplySiteRules(url, true);
        }

        /// <summary>
        /// Puts a site's own answers on the view when the load is for a different
        /// site from the one already applied.
        ///
        /// The images switch takes effect from here on, which at the start of a
        /// load is nearly all of the page. The identity cannot: the document was
        /// already asked for with whatever was applied before, and a site serves
        /// its mobile or desktop layout off that request. So a crossing into a site
        /// whose identity differs costs one reload — which is exactly the reload
        /// the user was doing by hand, once per crossing, before this existed.
        ///
        /// <paramref name="mayReload"/> is false on our own navigations, where the
        /// load has not gone out yet and the settings are simply right from the
        /// first request.
        /// </summary>
        private bool ApplySiteRules(string url, bool mayReload)
        {
            // Our own generated pages are on no site (KeyFor returns null for them)
            // and leave the settings alone, so a trip through the start screen does
            // not reset the site on either side of it.
            string site = SiteRules.KeyFor(url);
            if (site == null || site == _siteKey)
            {
                return false;
            }

            _siteKey = site;

            int ua;
            bool images;
            SiteRules.Effective(url, _defaultPresetIndex, _defaultImagesOn, out ua, out images);
            if (ua < 0 || ua >= _presets.Length)
            {
                // A rule written by a build with more presets than this one.
                ua = _defaultPresetIndex;
            }

            // With no proxy typed there is nothing to go through, whatever the
            // rules say; a rule made before the address was cleared waits for one.
            bool proxy = _proxy != null && SiteRules.ProxyFor(url, _defaultProxyOn);

            bool uaChanged = ua != _presetIndex;
            bool imagesChanged = images != _imagesOn;
            bool proxyChanged = proxy != _proxyOn;
            if (!uaChanged && !imagesChanged && !proxyChanged)
            {
                return false;
            }

            if (uaChanged)
            {
                ApplyPreset(ua);
            }

            if (imagesChanged)
            {
                ApplyImages(images);
            }

            // Like the identity, and for the same reason, one reload late on a
            // load the engine started by itself: the document was asked for over
            // whichever route was in force, and for a region-blocked site that is
            // the refusal the proxy is there to get past.
            if (proxyChanged)
            {
                ApplyProxy(proxy);
            }

            DiagLog.Add("site rules for " + site + ": " + ShortPreset(_presets[ua].Label) +
                        ", images " + (images ? "on" : "off") +
                        ", proxy " + (proxy ? "on" : "off"));

            if (!mayReload)
            {
                return true;
            }

            // Two sites whose rules disagree and which redirect to each other would
            // reload one another for ever, and an unusable browser is a far worse
            // outcome than a page carrying the wrong identity. The limit is per run
            // of loads that never settle: an ordinary crossing reloads once and the
            // load after it resets the count.
            if (_ruleReloads >= RuleReloadLimit)
            {
                DiagLog.Add("site rules: not reloading again for " + site +
                            " — " + _ruleReloads + " reloads without a page settling");
                return true;
            }

            _ruleReloads++;
            _ruleReloadIssued = true;

            // Not from here: this is normally reached from inside the engine's own
            // PageLoadStarted notification, and asking a web engine to start a new
            // load while it is telling us about the one it just started is the kind
            // of re-entry this platform does not forgive. A tick of the main loop
            // later is still long before any of the page has been painted. If no
            // timer can be armed the reload is worth more than the caution.
            if (!NuiLater.Once(1, delegate { Reload("site rules"); }))
            {
                Reload("site rules");
            }

            return true;
        }

        /// <summary>
        /// Reads the proxy settings when a view is built. A view rebuilt by the
        /// blank-view ladder may or may not share the old one's context, so an
        /// engine that has been told anything in this process is told again what
        /// we believe it has, rather than left to agree with us by luck.
        /// </summary>
        private void ConfigureProxy()
        {
            string entry = Store.Get("proxy", string.Empty);
            string problem = null;
            _proxy = entry.Length == 0 ? null : ProxyAddress.Parse(entry, out problem);
            _defaultProxyOn = Store.GetBool("proxyAll", false);
            if (problem != null)
            {
                DiagLog.Add("proxy: the stored address does not read (" + problem + "), ignored");
            }

            DiagLog.Add("proxy: " + (_proxy == null ? "none set" : _proxy.Display) +
                        ", " + (_defaultProxyOn ? "on" : "off") + " by default");

            if (NuiProxy.EverSet)
            {
                ApplyProxy(_proxyOn && _proxy != null);
            }
        }

        /// <summary>
        /// The proxy switch, remembered for the site it was pressed on (issue #97),
        /// or on the start screen for every site without a rule of its own, the
        /// same split as images and identity. Pressed before any proxy has been
        /// typed, it asks for one first and then does what was pressed.
        /// </summary>
        private void ToggleProxy()
        {
            string url = PageUrl();
            bool on = url == null ? !_defaultProxyOn : !(_proxy != null && SiteRules.ProxyFor(url, _defaultProxyOn));

            if (on && _proxy == null)
            {
                _proxyPending = true;
                _proxyPendingUrl = url;
                _keyboard.Open(KeyboardTarget.Proxy, string.Empty);
                Flash("Digite o proxy primeiro, tipo http://1.2.3.4:8080");
                return;
            }

            SetProxySwitch(url, on);
        }

        private void SetProxySwitch(string url, bool on)
        {
            SiteRule rule = SiteRules.SetProxy(url, on);
            if (rule == null)
            {
                _defaultProxyOn = on;
                Store.Set("proxyAll", on);
                Flash(on ? "Proxy ligado em todos os sites" : "Proxy desligado, menos nos sites em que você ligou");
            }
            else
            {
                Flash((on ? "Proxy ligado em " : "Proxy desligado em ") + rule.Site);
            }

            DiagLog.Add("proxy " + (on ? "ON" : "OFF") + " for " + (rule == null ? "every site" : rule.Site));

            // A change like forgetting a site: the applied site is no longer
            // what its rules say, so it is worked out again and reloaded.
            _siteKey = null;
            ApplySiteRules(url, true);
            UpdateStatus();
        }

        /// <summary>
        /// A typed proxy. Empty clears it; one that does not read reopens the
        /// keyboard on what was typed, since the mistake is usually one character.
        /// </summary>
        private void OnProxyEntered(string text)
        {
            bool pending = _proxyPending;
            string pendingUrl = _proxyPendingUrl;
            _proxyPending = false;
            _proxyPendingUrl = null;
            bool wasOn = _proxyOn;

            if ((text ?? string.Empty).Trim().Length == 0)
            {
                _proxy = null;
                Store.Set("proxy", string.Empty);
                DiagLog.Add("proxy cleared");
                Flash("Proxy apagado, todos os sites vão direto");
                if (wasOn)
                {
                    ApplyProxy(false);
                    Reload("proxy cleared");
                }

                UpdateStatus();
                return;
            }

            string problem;
            ProxyAddress address = ProxyAddress.Parse(text, out problem);
            if (address == null)
            {
                Flash(problem);
                _proxyPending = pending;
                _proxyPendingUrl = pendingUrl;
                _keyboard.Open(KeyboardTarget.Proxy, text);
                return;
            }

            _proxy = address;
            Store.Set("proxy", address.Entry);
            DiagLog.Add("proxy set: " + address.Display);

            if (pending)
            {
                SetProxySwitch(pendingUrl, true);
                return;
            }

            Flash("Proxy " + address.EngineUri);

            // The switches did not move, so if the proxy was in use it still is,
            // just at the new address, and the page is reloaded to go through it.
            if (wasOn)
            {
                ApplyProxy(true);
                Reload("proxy address");
            }
            else
            {
                // A site already switched on, or proxy-by-default, may have been
                // waiting for an address.
                _siteKey = null;
                ApplySiteRules(PageUrl(), true);
            }

            UpdateStatus();
        }

        /// <summary>Tells the engine to go through the proxy or direct.</summary>
        private void ApplyProxy(bool on)
        {
            _proxyOn = on && _proxy != null;
            NuiProxy.Apply(_web, _proxyOn ? _proxy : null);
            DiagLog.Add("proxy: " + NuiProxy.LastResult);
        }

        private string ProxyLine()
        {
            return (_proxy == null ? "none set" : _proxy.Display) +
                   "   ·   " + (_defaultProxyOn ? "on" : "off") + " by default" +
                   "   ·   this page " + (_proxyOn ? "through it" : "direct") + "\n" +
                   "            engine: " + NuiProxy.LastResult;
        }

        /// <summary>Tells the view about the images switch. Best-effort, like everything past it.</summary>
        private void ApplyImages(bool on)
        {
            _imagesOn = on;
            try
            {
                if (_web.Settings != null)
                {
                    _web.Settings.AutomaticImageLoadingAllowed = on;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("image switch failed: " + ex.Message);
            }
        }

        private void Reload(string why)
        {
            try
            {
                _web.Reload();
            }
            catch (Exception ex)
            {
                DiagLog.Add("reload after " + why + " failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Records what the view has actually got on it, which is the only thing
        /// that may decide whether we are on the start screen.
        /// </summary>
        /// <remarks>
        /// <c>_atHome</c> used to be set by <c>ShowHome</c> and cleared by
        /// <c>Navigate</c> — that is, by whoever *asked* for a page, which misses
        /// every navigation the app does not perform itself. **Opening a favourite
        /// is one of those**: a tile is an ordinary link, so the engine follows it
        /// and nothing tells the app it left. So the browser sat on Instagram still
        /// believing it was showing the start screen, and issue #80's reporter found
        /// what that costs once things started keying off it: the images and
        /// identity switches wrote the browser-wide setting instead of the site's
        /// (because "no site" is what the start screen is), key 8 looked for a tile
        /// under the pointer, the address bar said "start screen", and turning
        /// images back on flashed them for one frame before the site's own rule
        /// reloaded them away again. Every one of those is the same wrong bit.
        ///
        /// A page is the start screen if it *is* the start screen. Both shapes of
        /// that are <see cref="Store.IsGenerated"/>, and this is called at every
        /// load boundary, so a navigation nobody in this app initiated still moves
        /// the flag.
        /// </remarks>
        private void NoteShowing(string url)
        {
            _atHome = Store.IsGenerated(url);
        }

        /// <summary>
        /// The address the page is on, or null when it is one of ours. Everything
        /// per-site keys off this, and "no site" is a real answer rather than a
        /// missing one — see <see cref="SiteRules.KeyFor"/>.
        /// </summary>
        private string PageUrl()
        {
            return _web == null || _atHome ? null : SafeUrl();
        }

        /// <summary>
        /// Keeps the page that is open, or takes it out again.
        ///
        /// Reads the address from the engine rather than from <c>_cachedUrl</c>,
        /// and issue #80's reporter is why. That cache is refreshed by
        /// <c>UpdateStatus</c>, which runs when a load finishes — and a site like
        /// Instagram moves between reels without ever finishing another load, so
        /// the cache could be several pages behind what was on the screen. Pressing
        /// 8 then kept, or removed, a page the user was not looking at.
        ///
        /// The start screen has no page of its own to keep, and said so by doing
        /// nothing at all, which from a sofa is indistinguishable from a key that
        /// does not work — and the start screen, where the tiles are, is exactly
        /// where somebody goes to get rid of one.
        /// </summary>
        private void ToggleFavourite()
        {
            if (_atHome)
            {
                // On the start screen the answer is a tile, and the page has to be
                // asked which one.
                try
                {
                    _web.EvaluateJavaScript(
                        "String(window." + PageScript.Namespace + " && window." +
                        PageScript.Namespace + ".linkAt())",
                        result => ForgetTile(result));
                }
                catch (Exception ex)
                {
                    DiagLog.Add("asking for the tile failed: " + ex.Message);
                }

                return;
            }

            string url = PageUrl();
            if (string.IsNullOrEmpty(url) || url == "-")
            {
                Flash("Abra uma página primeiro, ou use \u201cGuardar um endereço digitado\u201d em Avançado");
                return;
            }

            bool kept = Store.ToggleFavourite(url, PageTitle());
            DiagLog.Add((kept ? "kept " : "removed ") + url);
            Flash(kept ? "Página guardada nos favoritos" : "Página tirada dos favoritos");

            // The star on the bar is read from the same cache, so it would
            // otherwise keep showing the answer from before the press.
            _cachedUrl = url;
        }

        /// <summary>
        /// Removes the tile the pointer is on. On the start screen, that is all 8
        /// does — it never adds.
        /// </summary>
        /// <remarks>
        /// Issue #80 arrived three times in one afternoon, each time as a different
        /// symptom of one thing: <b>a gesture that meant two opposite things
        /// depending on state the screen did not show.</b> First 8 kept or removed
        /// depending on an address the tile never displayed; then it kept or removed
        /// depending on which of two identical-looking grids the pointer happened to
        /// be over, so pointing at a page already kept and pressing 8 added a second
        /// copy of it. Every fix that kept the toggle bought one more shape of the
        /// same report.
        ///
        /// So the gestures are separated by meaning instead, and each one has
        /// exactly one:
        /// <list type="bullet">
        /// <item>8 <em>on a page</em> — keep this page, or drop it. You are looking
        /// at the thing, so a toggle is honest.</item>
        /// <item>8 <em>on a tile</em> — get rid of this tile. Never adds; you can
        /// see it is already there.</item>
        /// <item><em>Keep an address…</em> — keeps. A menu row with the word keep in
        /// it must not sometimes delete.</item>
        /// </list>
        /// There is now no sequence of presses that makes a second tile for
        /// something already on the screen.
        ///
        /// The tile carries which grid it is in (<c>data-kind</c>, written by
        /// <see cref="HomePage"/>), because the same page is very often a favourite
        /// and a recent visit both, and removing it from the list the pointer was
        /// not on looks exactly like a key that did nothing.
        /// </remarks>
        private void ForgetTile(string answer)
        {
            string kind = string.Empty;
            string url = answer ?? string.Empty;
            int space = url.IndexOf(' ');
            if (space > 0)
            {
                kind = url.Substring(0, space);
                url = url.Substring(space + 1);
            }

            if (string.IsNullOrEmpty(url) || url == "null" || Store.IsGenerated(url))
            {
                Flash("Aponte para um quadro e aperte 8 para tirá-lo");
                return;
            }

            bool gone = kind == "recent" ? Store.ForgetVisit(url) : Store.RemoveFavourite(url);
            if (!gone)
            {
                // Nothing was there to remove, which on this screen means the tile
                // is not what we were told it is. Say so rather than reporting a
                // removal that did not happen.
                DiagLog.Add("tile not found in " + (kind == "recent" ? "history" : "favourites") + ": " + url);
                Flash("Esse quadro já não está mais na lista");
                ShowHome();
                return;
            }

            DiagLog.Add("removed " + kind + " tile " + url);
            Flash("Tirado: " + Urls.Readable(url));

            // Rebuilt, or the tile just removed is still on the screen — which is
            // exactly the "it did not work" this whole thread is about.
            ShowHome();
        }

        /// <summary>The page's title now, not as of the last status refresh.</summary>
        private string PageTitle()
        {
            string title = SafeTitle();
            return string.IsNullOrEmpty(title) ? _cachedTitle : title;
        }

        /// <summary>
        /// Keeps a typed address as a favourite without going to it — issue #80.
        ///
        /// Key 8 keeps <em>the page you are on</em>, which is no help at all when
        /// the address you want is one that redirects: the reporter wanted
        /// <c>instagram.com/reel</c> in his tiles, and opening it hands you one
        /// particular reel, so the tile he could make played the same clip for
        /// ever. There was no way to keep an address you cannot land on.
        ///
        /// It toggles, exactly as key 8 does, so typing one that is already kept
        /// takes it out again — and says which of the two happened, because
        /// otherwise the two are indistinguishable from the sofa.
        /// </summary>
        private void KeepAddress(string text)
        {
            string url = Urls.Normalize(text);

            // The address, not the bare host. Two favourites on one site would
            // otherwise be one name twice, and the tiles are all somebody has to
            // tell them apart by — issue #80's follow-up.
            string title = Urls.Readable(url);

            // Keeps. Never removes, and never a second copy of something already
            // there: a row with the word keep in it that sometimes deletes is how
            // this issue got to its third report. Removing is 8 on the tile.
            if (Store.Keep(url, title))
            {
                DiagLog.Add("kept address " + url);
                Flash("Guardado: " + title);
            }
            else
            {
                DiagLog.Add("already kept: " + url);
                Flash(title + " já está nos favoritos (8 no quadro tira)");
            }
        }

        /// <summary>
        /// Turns "open where I left off" on and off — issue #79. Off goes back to
        /// the fixed address if one was ever set, so nobody has to type it again to
        /// get it back; see <see cref="StartPage.ToggleLast"/>.
        /// </summary>
        private void ToggleResumeLast()
        {
            bool on = StartPage.ToggleLast();
            DiagLog.Add("start page mode: " + StartPage.Mode());
            Flash(on
                ? "Abre onde você parou"
                : "Abre em " + (string.IsNullOrEmpty(StartPage.Address)
                    ? "a tela inicial"
                    : StartPage.Address));
        }

        // ----------------------------------------------------------- Overscan BR

        /// <summary>
        /// What the favourites start as, on this build's very first launch and
        /// never again: a removed starter must stay removed. Edit this list to
        /// change what a fresh install shows.
        /// </summary>
        private static readonly string[][] StarterFavourites =
        {
            new[] { "https://ge.globo.com/", "ge" },
            new[] { "https://www.google.com.br/", "Google" },
        };

        private static void SeedFavourites()
        {
            if (Store.GetBool("brSeeded", false))
            {
                return;
            }

            // Keep puts each one at the front, so the list goes in backwards to
            // come out in the order it is written above.
            for (int i = StarterFavourites.Length - 1; i >= 0; i--)
            {
                Store.Keep(StarterFavourites[i][0], StarterFavourites[i][1]);
            }

            Store.Set("brSeeded", true);
            DiagLog.Add("starter favourites kept: " + StarterFavourites.Length);
        }

        private void OpenFavouritesBar()
        {
            if (_keyboard.IsVisible)
            {
                _keyboard.Close();
            }

            _favBar.Open(Store.AllFavourites, PageUrl() != null);
            ShowChrome();
        }

        private void OnFavouriteChosen(Bookmark favourite)
        {
            Flash("Abrindo " + Urls.Readable(favourite.Url));
            Navigate(favourite.Url);
        }

        /// <summary>
        /// The bar's last tile. On a page it keeps that page; on the start screen
        /// there is no page, so it opens the keyboard for an address instead.
        /// It only ever adds: a "keep" that sometimes removes is upstream issue
        /// #80's lesson, and removing stays on 8.
        /// </summary>
        private void OnFavouritesBarKeep()
        {
            string url = PageUrl();
            if (url == null)
            {
                RunAction(RemoteMenu.ActionKeepAddress);
            }
            else if (Store.IsFavourite(url))
            {
                Flash("Esta página já está nos favoritos");
            }
            else
            {
                ToggleFavourite();
            }
        }

        /// <summary>
        /// Where a zoom choice is remembered: per site, and on the start screen
        /// the default for every site without one of its own. The same split the
        /// images and identity switches use, so it behaves the way they do.
        /// </summary>
        private string ZoomKey()
        {
            string site = SiteRules.KeyFor(PageUrl());
            return site == null ? "zoomDefault" : "zoom:" + site;
        }

        /// <summary>
        /// Works out the zoom the page now showing should have. Safe from inside
        /// the engine's callbacks: it reads our own store and nothing else.
        /// </summary>
        private void ApplyZoom()
        {
            int fallback = Store.GetInt("zoomDefault", 100);
            string site = SiteRules.KeyFor(PageUrl());
            SetZoom(site == null ? fallback : Store.GetInt("zoom:" + site, fallback));
        }

        /// <summary>
        /// The engine's own page zoom, which scales text, images and layout
        /// together the way a desktop browser's Ctrl-plus does. Text-only zoom
        /// would keep the layout and is the gentler option on paper, but it is
        /// hidden API at this level and breaks pages that size boxes for their
        /// text. Best-effort like everything past the managed surface.
        /// </summary>
        private void SetZoom(int percent)
        {
            _zoomPercent = percent;
            _zoomPending = true;
        }

        /// <summary>
        /// Hands the pending zoom to the engine, from the tick and only between
        /// loads. Asks what the engine has first, so a page that kept the factor
        /// across a navigation is not told again, and names the native call on
        /// the trail before making it: if this is ever what stops the app, the
        /// last line of the previous run says so.
        /// </summary>
        private void PushZoom()
        {
            _zoomPending = false;
            if (_web == null)
            {
                return;
            }

            float wanted = _zoomPercent / 100f;
            try
            {
                float now = _web.PageZoomFactor;
                if (Math.Abs(now - wanted) < 0.005f)
                {
                    return;
                }

                Breadcrumbs.Drop("zoom: setting " + _zoomPercent + "% (engine had " +
                                 Math.Round(now * 100) + "%)");
                _web.PageZoomFactor = wanted;
                Breadcrumbs.Drop("zoom: set");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Drop("page zoom failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>One step bigger (+1), smaller (-1), or back to 100% (0).</summary>
        private void ChangeZoom(int direction)
        {
            int next = 100;
            if (direction != 0)
            {
                int i = Array.IndexOf(ZoomSteps, _zoomPercent);
                if (i < 0)
                {
                    i = Array.IndexOf(ZoomSteps, 100);
                }

                next = ZoomSteps[Math.Max(0, Math.Min(ZoomSteps.Length - 1, i + direction))];
            }

            SetZoom(next);
            Store.Set(ZoomKey(), next);

            string site = SiteRules.KeyFor(PageUrl());
            bool limit = direction != 0 && next == ZoomSteps[direction > 0 ? ZoomSteps.Length - 1 : 0];
            Flash("Letra " + next + "%" + (limit ? " (no limite)" : string.Empty) +
                  (site == null ? " em todos os sites" : " em " + site));
        }

        /// <summary>
        /// Called by <see cref="DiagServer"/> on its own thread when the Mac asks
        /// for a page. Nothing here may touch DALi or the web view: it only leaves
        /// the address for <see cref="TakeRemoteOpen"/> on the main thread.
        /// </summary>
        private string OnRemoteOpen(string url)
        {
            _remoteOpen = url;
            return "ok, abrindo " + url;
        }

        /// <summary>
        /// Called by <see cref="DiagServer"/> on its own thread for /acao. Checks
        /// the name against <see cref="RemoteActions"/> (a read-only table, safe
        /// from any thread) and leaves it for <see cref="TakeRemoteAction"/>.
        /// </summary>
        private string OnRemoteAction(string id)
        {
            if (!RemoteActions.ContainsKey(id))
            {
                return "ação desconhecida: " + id;
            }

            _remoteAction = id;
            return "ok, " + id;
        }

        private void TakeRemoteAction()
        {
            string id = _remoteAction;
            if (id == null || _web == null)
            {
                return;
            }

            _remoteAction = null;
            Breadcrumbs.Drop("action from the network: " + id);

            // As if a key had been pressed: the panel is a remote, so a press
            // from it wakes a sleeping fullscreen pointer like any other.
            _lastPointerKey = DateTime.UtcNow;

            if (id == "menu")
            {
                ShowMenu(!_menu.Visible);
                return;
            }

            if (_keyboard.IsVisible)
            {
                _keyboard.Close();
            }

            RunAction(RemoteActions[id]);
        }

        private void TakeRemoteOpen()
        {
            string url = _remoteOpen;
            if (url == null || _web == null)
            {
                return;
            }

            _remoteOpen = null;

            // Whatever was open on screen was for the page being left.
            if (_keyboard.IsVisible)
            {
                _keyboard.Close();
            }

            if (_menu.Visible)
            {
                ShowMenu(false);
            }

            if (_favBar.IsVisible)
            {
                _favBar.Close();
            }

            string target = Urls.Normalize(url);
            Breadcrumbs.Drop("opened from the network: " + target);
            Navigate(target);
            Flash("Aberto pelo Mac: " + Urls.Readable(target));
        }

        /// <summary>
        /// The page's video to the whole screen, or back, the way F does on a
        /// desktop player.
        ///
        /// A stylesheet that pins the element over the whole viewport and hides
        /// everything else on the page (fixed headers and side bars included,
        /// whatever their z-index), and nothing more. Every ancestor of the
        /// element loses its transform, filter and containment while it lasts,
        /// because any of those makes "fixed" mean "fixed inside that box" and
        /// the video stays the size of its column. Pressing again removes the
        /// sheet and every mark, so the page is exactly as it was. The first version also asked for the real Fullscreen
        /// API, which on this TV hands the video to the engine's own fullscreen
        /// path, the media stack that aborted the process during video playback
        /// on 2026-10-17. Not proven to be this call; staying out of that path
        /// is the precaution. The NUI view keeps the engine's fullscreen signals
        /// internal at API 9 (see <see cref="WatchTheEngine"/>), so that path
        /// was never one this app could follow or undo anyway. The view already fills the screen, so a video filling the
        /// view is a fullscreen video. A player inside another site's frame is
        /// out of reach of any script, so the frame itself is what gets
        /// stretched when the page has no video of its own.
        ///
        /// The element is never moved in the DOM: taking a media element out of
        /// the document pauses it, which is the one thing this must not do.
        /// </summary>
        /// <summary>
        /// Overscan BR: opens the frame the video is in as the page itself, the
        /// "open frame" every desktop browser has.
        ///
        /// A player embedded from another origin is out of reach of everything
        /// this browser does to a page: the HLS answer, the empty-sound guard, the
        /// clean fullscreen and the click beside a button all stop at the frame's
        /// edge, because no script of ours runs inside it. Opened on its own, the
        /// same player is the main document and gets all of it.
        ///
        /// Which frame: the one under the pointer if it is on one, otherwise the
        /// largest on screen, and only if it covers a real part of it (a player,
        /// not a like button). A page whose video is its own is told so, since the
        /// blue key already does the right thing there.
        /// </summary>
        private void OpenPlayerFrame()
        {
            if (_web == null || _atHome)
            {
                Flash("Abra uma página com vídeo primeiro");
                return;
            }

            string script =
                "(function(fx,fy){var d=document,w=window.innerWidth,h=window.innerHeight;" +
                "function seen(e){var r=e.getBoundingClientRect();" +
                "return Math.max(0,Math.min(r.right,w)-Math.max(r.left,0))*Math.max(0,Math.min(r.bottom,h)-Math.max(r.top,0));}" +
                "function src(f){var s='';try{s=String(f.src||'');}catch(e){}return /^https?:/i.test(s)?s:'';}" +
                "var hit=null;try{hit=d.elementFromPoint(fx*w,fy*h);}catch(e){}" +
                "if(hit&&(hit.tagName==='IFRAME'||hit.tagName==='FRAME')&&src(hit)){return 'frame '+src(hit);}" +
                "var best='',most=w*h*0.06,f=d.querySelectorAll('iframe,frame');" +
                "for(var i=0;i<f.length;i++){var a=seen(f[i]),s=src(f[i]);if(s&&a>most){most=a;best=s;}}" +
                "if(best){return 'frame '+best;}" +
                "var v=d.querySelectorAll('video');for(var j=0;j<v.length;j++){if(seen(v[j])>0){return 'own';}}" +
                "return 'none';})(" +
                _cursor.FractionX.ToString("0.#####", CultureInfo.InvariantCulture) + "," +
                _cursor.FractionY.ToString("0.#####", CultureInfo.InvariantCulture) + ")";

            try
            {
                _web.EvaluateJavaScript(script, result =>
                {
                    string answer = (result ?? string.Empty).Trim().Trim('"');
                    Breadcrumbs.Drop("open player: " + (answer.Length > 160 ? answer.Substring(0, 160) : answer));

                    if (answer.StartsWith("frame ", StringComparison.Ordinal))
                    {
                        Flash("Abrindo só o player");
                        Navigate(answer.Substring("frame ".Length));
                    }
                    else if (answer == "own")
                    {
                        Flash("O vídeo já é desta página: use Vídeo em tela cheia (azul)");
                    }
                    else
                    {
                        Flash("Não achei um player em quadro nesta página");
                    }
                });
            }
            catch (Exception ex)
            {
                DiagLog.Add("open player failed: " + ex.Message);
            }
        }

        private void ToggleVideoFullscreen()
        {
            if (_web == null || _atHome)
            {
                Flash("Abra uma página com vídeo primeiro");
                return;
            }

            const string script =
                "(function(){var d=document,id='__ovsbr_full',st=d.getElementById(id);" +
                "if(st){var on=d.querySelectorAll('[data-ovsbr-full],[data-ovsbr-anc]');" +
                "for(var k=0;k<on.length;k++){on[k].removeAttribute('data-ovsbr-full');on[k].removeAttribute('data-ovsbr-anc');}" +
                "var c=d.querySelector('[data-ovsbr-ctl]');if(c){c.controls=true;c.removeAttribute('data-ovsbr-ctl');}" +
                "st.parentNode.removeChild(st);" +
                "try{if(d.fullscreenElement&&d.exitFullscreen){d.exitFullscreen();}else if(d.webkitFullscreenElement&&d.webkitExitFullscreen){d.webkitExitFullscreen();}}catch(e){}" +
                "return 'off';}" +
                "function area(e){var r=e.getBoundingClientRect();return r.width*r.height;}" +
                "var best=null,score=0,v=d.querySelectorAll('video');" +
                "for(var i=0;i<v.length;i++){var a=area(v[i]);if(a<=0){continue;}var s=a+(v[i].paused?0:1e9);if(s>score){best=v[i];score=s;}}" +
                "if(!best){var f=d.querySelectorAll('iframe');for(var j=0;j<f.length;j++){var b=area(f[j]);if(b>score){best=f[j];score=b;}}}" +
                "if(!best){return 'none';}" +
                "best.setAttribute('data-ovsbr-full','1');" +
                "for(var p=best.parentElement;p&&p!==d.documentElement;p=p.parentElement){p.setAttribute('data-ovsbr-anc','1');}" +
                "st=d.createElement('style');st.id=id;" +
                "st.textContent='html,body{background:#000!important;overflow:hidden!important}'+" +
                "'body *{visibility:hidden!important}'+" +
                "'[data-ovsbr-full],[data-ovsbr-full] *{visibility:visible!important}'+" +
                "'[data-ovsbr-anc]{transform:none!important;filter:none!important;perspective:none!important;contain:none!important;will-change:auto!important;backdrop-filter:none!important;clip-path:none!important}'+" +
                "'[data-ovsbr-full]{position:fixed!important;top:0!important;left:0!important;right:auto!important;bottom:auto!important;width:100vw!important;height:100vh!important;max-width:none!important;max-height:none!important;min-width:0!important;min-height:0!important;margin:0!important;padding:0!important;border:0!important;transform:none!important;z-index:2147483647!important;background:#000!important;object-fit:contain!important}';" +
                "(d.head||d.documentElement).appendChild(st);return best.tagName.toLowerCase();})()";

            try
            {
                // On the trail before the call, the way every native step here is:
                // the first Overscan BR crash on this set (2026-10-17) was the TV's
                // own audio decoder aborting the process on a video page, and the
                // trail could not say whether this key had been pressed.
                Breadcrumbs.Drop("video fullscreen: asking the page");
                _web.EvaluateJavaScript(script, result =>
                {
                    // Fires once empty before the real result arrives, as Probe notes.
                    if (string.IsNullOrEmpty(result))
                    {
                        return;
                    }

                    string answer = result.Trim('"');
                    Breadcrumbs.Drop("video fullscreen: " + answer);
                    switch (answer)
                    {
                        case "off":
                            _videoFull = false;
                            WakePointer();
                            Flash("Saiu da tela cheia");
                            break;
                        case "none":
                            _videoFull = false;
                            WakePointer();
                            Flash("Não achei vídeo nesta página");
                            break;
                        default:
                            _videoFull = true;
                            _lastPointerKey = DateTime.UtcNow;
                            HideChrome();
                            if (_hintsWanted)
                            {
                                ShowHints(false);
                            }

                            break;
                    }

                    if (!_videoFull && _hintsWanted)
                    {
                        ShowHints(true);
                    }
                });
            }
            catch (Exception ex)
            {
                DiagLog.Add("video fullscreen failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Puts the pointer away for fullscreen video: our drawing of it, the
        /// page's own video controls, and the engine's mouse, which goes to the
        /// upper middle of the picture so a player in another site's frame stops
        /// seeing it rest on its control bar.
        /// </summary>
        private void SleepPointer()
        {
            _pointerIdleHidden = true;
            _cursor.Hide();
            SetPageVideoControls(false);

            Size2D screen = _window.WindowSize;
            double scale = Math.Max(0.1, _zoomPercent / 100.0);
            int x = (int)Math.Round(screen.Width / 2.0 / scale);
            int y = (int)Math.Round(screen.Height / 4.0 / scale);
            bool moved = NuiInspectorInput.Move(x, y);
            DiagLog.Add("pointer asleep" + (moved ? ", engine mouse moved off the controls" : string.Empty));
        }

        /// <summary>Undoes <see cref="SleepPointer"/>. Harmless when it is not asleep.</summary>
        private void WakePointer()
        {
            if (!_pointerIdleHidden)
            {
                return;
            }

            _pointerIdleHidden = false;
            if (!_keysToPage)
            {
                _cursor.Show();
            }

            SetPageVideoControls(true);
        }

        /// <summary>
        /// Hides or restores the controls of the fullscreen video when it is a
        /// video of this page. A player inside another site's frame is out of
        /// reach; its controls are its own business, see SleepPointer.
        /// </summary>
        private void SetPageVideoControls(bool show)
        {
            if (_web == null)
            {
                return;
            }

            string script = show
                ? "(function(){var v=document.querySelector('[data-ovsbr-ctl]');" +
                  "if(v){v.controls=true;v.removeAttribute('data-ovsbr-ctl');}})()"
                : "(function(){var v=document.querySelector('video[data-ovsbr-full]');" +
                  "if(v&&v.controls){v.setAttribute('data-ovsbr-ctl','1');v.controls=false;}})()";
            try
            {
                _web.EvaluateJavaScript(script);
            }
            catch (Exception ex)
            {
                DiagLog.Add("video controls " + (show ? "back" : "away") + " failed: " + ex.Message);
            }
        }

        private void Flash(string message)
        {
            ShowChrome();
            _flashUntil = DateTime.UtcNow.AddSeconds(2.5);
            _status.TextColor = NuiTheme.Accent;
            _status.Text = message;
        }

        private void ApplyPreset(int index)
        {
            _presetIndex = index;
            UserAgentPreset preset = _presets[index];
            try
            {
                string ua = preset.Value ?? _engineUserAgentRaw;
                if (!string.IsNullOrEmpty(ua))
                {
                    _web.UserAgent = ua;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Add("UA set failed: " + ex.Message);
            }

            DiagLog.Add("UA preset: " + preset.Label);
            UpdateStatus();
        }

        private void ApplyViewportFix()
        {
            try
            {
                string script = _viewportFix
                    ? "window." + PageScript.Namespace + ".setViewport(" +
                      _window.WindowSize.Width.ToString(CultureInfo.InvariantCulture) + ")"
                    : "window." + PageScript.Namespace + ".clearViewport()";
                _web.EvaluateJavaScript(
                    "(function(){var a=String(" + script + ");var b=String(window." +
                    PageScript.Namespace + ".metrics());return a+' | '+b;})()",
                    result =>
                    {
                        if (!string.IsNullOrEmpty(result))
                        {
                            _lastMetrics = result;
                            DiagLog.Add("viewport: " + result);
                        }
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("viewport fix failed: " + ex.Message);
            }
        }

        private void Probe()
        {
            try
            {
                _web.EvaluateJavaScript(
                    "[navigator.userAgent, window.innerWidth + 'x' + window.innerHeight," +
                    " String(window.devicePixelRatio), document.title].join('  |  ')",
                    result =>
                    {
                        // Fires once empty before the real result arrives.
                        if (string.IsNullOrEmpty(result))
                        {
                            return;
                        }

                        _lastProbe = result;
                        DiagLog.Add("probe: " + _lastProbe);
                        UpdateStatus();
                    });
            }
            catch (Exception ex)
            {
                DiagLog.Add("probe failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------- reporting

        /// <summary>
        /// Overscan BR: what a computer needs to drive this TV, on the overlay the
        /// diagnostics use, at reading size. Back closes it like the diagnostics.
        /// </summary>
        private void ShowRemoteCard()
        {
            _overlayVisible = true;
            _overlay.PointSize = NuiTheme.Pt(14);
            _overlay.Text = "CONTROLE PELO COMPUTADOR\n\n" +
                            "IP desta TV:   " + LocalAddress() + "\n" +
                            "Chave:   " + RemoteKey() + "\n\n" +
                            "No computador, na mesma rede de casa, guarde a chave em tv_chave.txt\n" +
                            "e o IP em tv_ip.txt, ao lado do tv.py. Depois:\n\n" +
                            "    python3 tv.py site ge.globo.com\n\n" +
                            "A chave é só desta TV. Voltar fecha este cartão.";
            _overlay.Show();
            _overlay.RaiseToTop();
        }

        private void ShowOverlay(bool visible)
        {
            _overlayVisible = visible;
            if (visible)
            {
                _overlay.PointSize = 9;
                _overlay.Text = Report();
                _overlay.Show();
                _overlay.RaiseToTop();
            }
            else
            {
                _overlay.Hide();
            }
        }

        private void UpdateStatus()
        {
            if (_web == null)
            {
                _status.TextColor = NuiTheme.Negative;
                _status.Text = "navegador indisponível";
                return;
            }

            _cachedUrl = SafeUrl();
            _cachedTitle = SafeTitle();
            _cachedGeometry = _web.Size.Width + "x" + _web.Size.Height +
                              "   (window " + _window.WindowSize.Width + "x" +
                              _window.WindowSize.Height + ")";

            string url = _atHome ? string.Empty : _cachedUrl;
            _host.Text = _atHome ? "Overscan BR" : HostOf(url);
            _path.Text = _atHome ? "tela inicial" : PathOf(url);

            if (_flashUntil != DateTime.MinValue)
            {
                return;
            }

            _status.TextColor = NuiTheme.InkMuted;
            // Overscan BR drops the identity name from the bar: "Desktop Chrome"
            // means nothing from a sofa, and it is still on the diagnostics page.
            _status.Text = (!_atHome && Store.IsFavourite(_cachedUrl) ? "★ favorito   ·   " : string.Empty) +
                           "letra " + _zoomPercent + "%" +
                           (_keysToPage ? "   ·   teclas na página" : string.Empty) +
                           (_viewportFix ? "   ·   ajustada" : string.Empty) +
                           (_imagesOn ? string.Empty : "   ·   sem imagens") +
                           (_adBlockOn ? string.Empty : "   ·   com anúncios") +
                           (_proxyOn ? "   ·   via proxy" : string.Empty);
        }

        private string Report()
        {
            string engine = _web == null
                ? "engine    : FAILED — " + (_engineFailure ?? "(unknown)")
                : "engine UA : " + _engineUserAgent + "\n" +
                  "forced UA : " + (_presets[_presetIndex].Value ?? "(engine default)") + "\n" +
                  "page sees : " + _lastProbe + "\n" +
                  "view geom : " + _cachedGeometry + "\n" +
                  "page metr : " + _lastMetrics + "\n" +
                  "vp fix    : " + (_viewportFix ? "ON" : "off") + "  (key 6)\n" +
                  "frame click: " + NuiInspectorInput.LastResult + "\n" +
                  "frame saw  : " + _frameWitness + "\n" +
                  "native tap : " + NuiNativeTouch.LastResult + "\n" +
                  "inspector  : " + NuiInspector.LastResult + "\n" +
                  "cookies   : " + _cookieState + "\n" +
                  "video     : " + VideoPathLine() + "\n" +
                  "media     : " + NuiMediaWatch.LastCensus + "\n" +
                  "video cap : " + NuiVideoCap.LastAction + "\n" +
                  "video rect: " + NuiVideoRect.LastBox + "\n" +
                  "mse format: " + NuiMseWatch.LastFormat + "\n" +
                  "mse stall : " + NuiMseWatch.LastStall + "\n" +
                  "one video : " + NuiMseHold.Summary() + "\n" +
                  "blank view: " + _blankState + "\n" +
                  "start page: " + _startPageState + "\n" +
                  "opens at  : " + StartPage.Describe(Store.RecentHistory) + "\n" +
                  "pointer   : " + (_cursor == null
                      ? "(not built)"
                      : (_cursor.Visual == CursorVisual.Native ? "drawn by Overscan" : "drawn by the page")) + "\n" +
                  "site rules: " + (SiteRules.For(_cachedUrl) == null
                      ? "nothing remembered for this site"
                      : "in force for " + SiteRules.For(_cachedUrl).Site) + "\n" +
                  "ad block  : " + NuiAdBlock.Summary() + "\n" +
                  "new window: refused " + _windowsRefused + " | last: " + _lastWindowRefused + "\n" +
                  "proxy     : " + ProxyLine() + "\n" +
                  "memory    : " + ProcessMemory.Summary() + ", peak " + _peakMemoryMb + " MB | " + _memoryState +
                  ", freed " + _memoryFrees + "x\n" +
                  "last words: " + NuiDeathWatch.LastWord + "\n" +
                  "stderr    : " + NativeStdErr.SessionState + "\n" +
                  "url       : " + _cachedUrl;

            // The previous run's trail, exactly as the ElmSharp report carries it.
            // A page that closes the app leaves nothing to read in this run: the
            // launch that died is the one with the answer in it.
            //
            // And this run's, read back from disk (issue #100, 2026-10-04). The
            // `log` block at the end is DiagLog's sixty on-screen lines, which the
            // trail-only lines — `media:`, `mse:`, `hold:`, `page error:`, the
            // memory readings — never enter. A reporter asked for the page "while
            // the app is still open" sent exactly that page, with TikTok refusing
            // every video on screen, and it carried the header's one-line snapshot
            // of each probe and not one line of the sequence behind it. The trail
            // was on disk the whole time. Breadcrumbs.Current explains the read.
            return "Overscan diagnostics (NUI build)\n\n" +
                   "build     : " + BuildInfo.Tag + "\n" +
                   "platform  : NUI WebView, api-version 9.0+\n" +
                   "trail file : " + Breadcrumbs.Location + "\n" +
                   "trail write: " + Breadcrumbs.Status + "\n" +
                   engine + "\n\n" +
                   // Issue #37. dest is the engine's own Sec-Fetch-Dest for the
                   // request, and build-f295172's report answered what it was put
                   // here to ask: it is a dash on every line, so that header is not
                   // on the request where Tizen's hook sits, though Sec-Fetch-Mode
                   // and Range both are. The column stays because it costs nothing
                   // and a firmware that starts sending it would be worth knowing
                   // about; the section itself is what found the ad's host.
                   "sites remembered (issue 74)\n" + SiteRules.Dump() + "\n" +
                   "requests this run (one line per host and first path segment, most first)\n" +
                   RequestTrail.Dump() + "\n\n" +
                   "this run (the trail so far — the media, mse, hold and page error lines are here and not in the log)\n" +
                   Breadcrumbs.Current + "\n\n" +
                   "previous run (last line is where it died)\n" + Breadcrumbs.Previous + "\n\n" +
                   "previous run's native output\n" + Breadcrumbs.PreviousStdErr + "\n\n" +
                   "log\n" + DiagLog.Dump();
        }

        /// <summary>
        /// What the engine has actually been told, which is not the same thing as
        /// what the setting says. A report reading "hardware overlay" while the
        /// property had never been set is a large part of why the black screen on
        /// issue #20 took a round trip to find, so an untouched engine says so.
        /// </summary>
        private string VideoPathLine()
        {
            string path = _videoOverlay ? "hardware overlay" : "in page";

            // Still worth three states rather than two. "Not applied yet" and
            // "applied" are the difference between a report that says what the
            // engine was told and one that says what we intend to tell it, and the
            // gap between them is a whole page load wide.
            if (_videoPathPending)
            {
                return path + "  (key 5 — applies after the first page)";
            }

            return _videoPathChosen
                ? path + "  (key 5)"
                : path + "  (key 5 — our default, nobody chose it)";
        }

        private static string ShortPreset(string label)
        {
            int bracket = label.IndexOf(" (", StringComparison.Ordinal);
            return bracket < 0 ? label : label.Substring(0, bracket);
        }

        private static string HostOf(string url)
        {
            string rest = url ?? string.Empty;
            int scheme = rest.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                rest = rest.Substring(scheme + 3);
            }

            int slash = rest.IndexOf('/');
            return slash > 0 ? rest.Substring(0, slash) : rest;
        }

        private static string PathOf(string url)
        {
            string rest = url ?? string.Empty;
            int scheme = rest.IndexOf("://", StringComparison.Ordinal);
            if (scheme > 0)
            {
                rest = rest.Substring(scheme + 3);
            }

            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return string.Empty;
            }

            string path = rest.Substring(slash);
            return path.Length > 48 ? path.Substring(0, 45) + "…" : path;
        }

        private string SafeUserAgent()
        {
            try
            {
                string ua = _web.UserAgent;
                return string.IsNullOrEmpty(ua) ? null : ua;
            }
            catch (Exception ex)
            {
                DiagLog.Add("reading engine UA failed: " + ex.Message);
                return null;
            }
        }

        private string SafeUrl()
        {
            try
            {
                return _web.Url ?? "-";
            }
            catch (Exception)
            {
                return "(unreadable)";
            }
        }

        private string SafeTitle()
        {
            try
            {
                return string.IsNullOrEmpty(_web.Title) ? "(untitled)" : _web.Title;
            }
            catch (Exception)
            {
                return "(untitled)";
            }
        }
    }
}
