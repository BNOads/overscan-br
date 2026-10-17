using System;
using System.Collections.Generic;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace Overscan
{
    /// <summary>
    /// Overscan BR's favourites bar: a strip across the top of the screen with one
    /// big tile per favourite, walked with left/right and opened with OK.
    ///
    /// The start screen already had the favourites, as a grid the pointer has to
    /// be steered onto. That works at a desk and is slow from a sofa, and from
    /// inside a page it costs a trip to the start screen first. This is the same
    /// list one button away from anywhere, driven by the D-pad alone, so nobody
    /// has to aim at anything.
    ///
    /// The last tile is always "keep this page", because the moment somebody
    /// opens the bar on a page that is not in it is exactly when they want it to
    /// be.
    /// </summary>
    internal sealed class NuiFavouritesBar
    {
        /// <summary>
        /// Tiles on screen at once. Six at a third of 1920 each would not fit, and
        /// fewer than six makes a list of ten feel like it scrolls for ever.
        /// </summary>
        private const int TilesShown = 6;

        private readonly View _root;
        private readonly TextLabel _title;
        private readonly View[] _tiles = new View[TilesShown];
        private readonly TextLabel[] _hosts = new TextLabel[TilesShown];
        private readonly TextLabel[] _names = new TextLabel[TilesShown];

        private IList<Bookmark> _items = new List<Bookmark>();
        private bool _onPage;
        private int _index;
        private int _first;

        public NuiFavouritesBar(Window window)
        {
            Size2D screen = window.WindowSize;
            int pad = NuiTheme.Pad;
            int gap = 16;
            int width = screen.Width - 80;
            int titleHeight = NuiTheme.Px(40);
            int tileHeight = NuiTheme.Px(104);
            int tileWidth = (width - (pad * 2) - (gap * (TilesShown - 1))) / TilesShown;

            _root = new View
            {
                Position2D = new Position2D(40, NuiTheme.BarHeight + 12),
                Size2D = new Size2D(width, pad + titleHeight + 8 + tileHeight + pad),
                BackgroundColor = NuiTheme.PanelDeep,
                CornerRadius = NuiTheme.Radius,
            };

            _title = new TextLabel
            {
                Position2D = new Position2D(pad, pad),
                Size2D = new Size2D(width - (pad * 2), titleHeight),
                PointSize = NuiTheme.Pt(12),
                TextColor = NuiTheme.Accent,
                Text = "FAVORITOS",
            };
            _root.Add(_title);

            for (int i = 0; i < TilesShown; i++)
            {
                var tile = new View
                {
                    Position2D = new Position2D(pad + (i * (tileWidth + gap)), pad + titleHeight + 8),
                    Size2D = new Size2D(tileWidth, tileHeight),
                    BackgroundColor = NuiTheme.KeyFill,
                    CornerRadius = 10f,
                };

                // The host is the big line, as on the start screen's tiles: it is
                // what somebody recognises a site by. The page title is the small
                // line under it, for telling two pages of one site apart.
                _hosts[i] = new TextLabel
                {
                    Position2D = new Position2D(16, 12),
                    Size2D = new Size2D(tileWidth - 32, NuiTheme.Px(44)),
                    PointSize = NuiTheme.Pt(15),
                    TextColor = NuiTheme.Ink,
                    Ellipsis = true,
                };
                _names[i] = new TextLabel
                {
                    Position2D = new Position2D(16, 12 + NuiTheme.Px(46)),
                    Size2D = new Size2D(tileWidth - 32, NuiTheme.Px(34)),
                    PointSize = NuiTheme.Pt(10),
                    TextColor = NuiTheme.InkMuted,
                    Ellipsis = true,
                };

                tile.Add(_hosts[i]);
                tile.Add(_names[i]);
                _tiles[i] = tile;
                _root.Add(tile);
            }

            _root.Hide();
            window.Add(_root);
        }

        public bool IsVisible { get; private set; }

        /// <summary>The root view, so the app can take it out of hit-testing.</summary>
        public View Root
        {
            get { return _root; }
        }

        /// <summary>Raised with the favourite to open.</summary>
        public event Action<Bookmark> Chosen;

        /// <summary>Raised when the last tile, "keep", is picked.</summary>
        public event Action KeepRequested;

        /// <summary>
        /// Shows the bar on the first favourite. <paramref name="onPage"/> decides
        /// what the last tile offers: keeping the page that is open, or typing an
        /// address when there is no page (the start screen).
        /// </summary>
        public void Open(IList<Bookmark> items, bool onPage)
        {
            _items = items ?? new List<Bookmark>();
            _onPage = onPage;
            _index = 0;
            _first = 0;
            IsVisible = true;
            Paint();
            _root.Show();
            _root.RaiseToTop();
            DiagLog.Add("favourites bar opened, " + _items.Count + " tiles");
        }

        public void Close()
        {
            IsVisible = false;
            _root.Hide();
        }

        /// <summary>
        /// The bar's keys while it is up. Every D-pad key, OK and Back belong to
        /// it; anything else closes it and is handed back, so a number key still
        /// does its own job instead of being eaten by a strip of tiles.
        /// </summary>
        public bool HandleKey(string key)
        {
            int count = _items.Count + 1;

            switch (key)
            {
                case RemoteKeys.Left:
                    _index = (_index + count - 1) % count;
                    Paint();
                    return true;

                case RemoteKeys.Right:
                    _index = (_index + 1) % count;
                    Paint();
                    return true;

                case RemoteKeys.Ok:
                case RemoteKeys.OkKeypad:
                    Pick();
                    return true;

                case RemoteKeys.Back:
                case RemoteKeys.Up:
                case RemoteKeys.Down:
                    Close();
                    return true;
            }

            Close();
            return false;
        }

        private void Pick()
        {
            Close();

            if (_index < _items.Count)
            {
                Action<Bookmark> chosen = Chosen;
                if (chosen != null)
                {
                    chosen(_items[_index]);
                }

                return;
            }

            Action keep = KeepRequested;
            if (keep != null)
            {
                keep();
            }
        }

        private void Paint()
        {
            int count = _items.Count + 1;

            // Keep the picked tile on screen: slide the window just far enough,
            // never further, so the strip does not jump under the person's eyes.
            if (_index < _first)
            {
                _first = _index;
            }
            else if (_index >= _first + TilesShown)
            {
                _first = _index - TilesShown + 1;
            }

            _title.Text = "FAVORITOS   " + (_index + 1) + " de " + count +
                          "      setas escolhem  ·  OK abre  ·  Voltar fecha";

            for (int i = 0; i < TilesShown; i++)
            {
                int item = _first + i;
                if (item >= count)
                {
                    _tiles[i].Hide();
                    continue;
                }

                bool picked = item == _index;
                _tiles[i].Show();
                _tiles[i].BackgroundColor = picked ? NuiTheme.Accent : NuiTheme.KeyFill;

                if (item < _items.Count)
                {
                    _hosts[i].Text = HostOf(_items[item].Url);
                    _names[i].Text = _items[item].Title ?? string.Empty;
                }
                else
                {
                    _hosts[i].Text = "+ Guardar";
                    _names[i].Text = _onPage ? "esta página nos favoritos" : "um endereço digitado";
                }

                // On the accent fill the muted grey disappears, the same swap the
                // menu makes for its picked row.
                _hosts[i].TextColor = picked ? NuiTheme.PanelDeep : NuiTheme.Ink;
                _names[i].TextColor = picked ? NuiTheme.PanelDeep : NuiTheme.InkMuted;
            }
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
            if (slash > 0)
            {
                rest = rest.Substring(0, slash);
            }

            return rest.StartsWith("www.", StringComparison.Ordinal) ? rest.Substring(4) : rest;
        }
    }
}
