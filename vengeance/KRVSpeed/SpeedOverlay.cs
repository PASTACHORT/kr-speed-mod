using System.Drawing.Drawing2D;
using System.Globalization;

namespace KRVSpeed;

/// <summary>
/// Kingdom Rush Vengeance speed slider (x1 - x8).
/// A small always-on-top panel over the game's battle screen. It never takes focus from the game.
/// - drag/click the track: speed x1..x8
/// - click the badge: toggle x1 / last fast speed; drag the badge: move the panel
/// - every new level starts at x1; outside battles the game clock is set back to x1
/// </summary>
sealed class SpeedOverlay : Form
{
    const int MaxSpeed = 8;

    // Vengeance colours: dark purple and toxic green
    static readonly Color Bg = Color.FromArgb(24, 12, 34);
    static readonly Color Border = Color.FromArgb(150, 230, 90);
    static readonly Color BadgeOn = Color.FromArgb(120, 40, 170);
    static readonly Color BadgeOff = Color.FromArgb(52, 32, 70);
    static readonly Color TextCol = Color.FromArgb(240, 255, 225);
    static readonly Color Track = Color.FromArgb(70, 45, 95);
    static readonly Color Fill = Color.FromArgb(140, 235, 70);
    static readonly Color NotchOn = Color.FromArgb(205, 255, 170);
    static readonly Color NotchOff = Color.FromArgb(140, 115, 165);
    static readonly Color KnobRim = Color.FromArgb(20, 8, 30);
    static readonly Color Knob = Color.FromArgb(185, 120, 255);
    static readonly Color Key = Color.FromArgb(1, 2, 3); // transparent corners

    readonly System.Windows.Forms.Timer _tick = new() { Interval = 100 };
    readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kingdom Rush Vengeance", "speedmod_settings.txt");

    GameMemory? _game;
    IntPtr _gameHwnd;
    string _lastLog = "";
    readonly string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kingdom Rush Vengeance", "speedmod_log.txt");

    void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss ") + msg + Environment.NewLine);
        }
        catch { }
    }

    // logs only when the state changes
    void LogState(bool battle, bool front, IntPtr hwnd)
    {
        var st = $"battle={battle} gameInFront={front} hwnd={hwnd} speed=x{_speed} clock={_game?.TimeScale}";
        if (st != _lastLog) { _lastLog = st; Log(st); }
    }
    bool _attaching;
    DateTime _nextAttach = DateTime.MinValue;

    int _speed = 1, _lastFast = 2;
    float _written = 1f;
    ulong _lastBattleScene;
    double _px = 0.5, _py = 0.055; // panel centre as a fraction of the game's client area

    string? _drag; // "knob" or "panel"
    Point _pressPt, _dragOffset;
    bool _moved;
    Rectangle _gameRect;

    public SpeedOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Key;
        TransparencyKey = Key;
        DoubleBuffered = true;
        Text = "KR Vengeance speed";
        LoadSettings();
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        Opacity = 0.96;
    }

    // never steal focus from the game
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000008 /*TOPMOST*/;
            return cp;
        }
    }
    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    // ---- game loop -----------------------------------------------------------------------

    void OnTick()
    {
        if (_game != null && !_game.Alive) { Log("game exited"); _game.Dispose(); _game = null; _lastBattleScene = 0; }
        if (_game == null)
        {
            HidePanel();
            if (!_attaching && DateTime.Now >= _nextAttach)
            {
                _attaching = true;
                Task.Run(GameMemory.TryAttach).ContinueWith(t =>
                {
                    _game = t.Result;
                    _attaching = false;
                    _gameHwnd = IntPtr.Zero;
                    Log(_game != null ? $"attached to pid {_game.Process.Id}" : "attach failed (game not running or not found)");
                    _nextAttach = DateTime.Now.AddSeconds(3);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            return;
        }

        var (scene, battle) = _game.Scene();
        var current = _game.TimeScale;

        if (battle)
        {
            if (scene != _lastBattleScene)
            {
                _lastBattleScene = scene; // a new level: start at x1
                _speed = 1;
                SaveSettings();
            }
            // only touch the clock when it holds our value (or the default); leave the game's own
            // pauses / slow-motion alone
            if (current is float c && (Math.Abs(c - _written) < 0.001f || Math.Abs(c - 1f) < 0.001f) && Math.Abs(c - _speed) > 0.001f)
            {
                _game.TimeScale = _speed;
                _written = _speed;
            }
        }
        else
        {
            _lastBattleScene = 0;
            if (current is float c && Math.Abs(c - _written) < 0.001f && Math.Abs(c - 1f) > 0.001f)
                _game.TimeScale = 1f;
            _written = 1f;
        }

        UpdatePlacement(battle);
        if (_drag != null) DragStep();
    }

    void UpdatePlacement(bool battle)
    {
        // the game window = the foreground window when it belongs to the game's process
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == (uint)_game!.Process.Id) _gameHwnd = fg;
        var hwnd = _gameHwnd;
        bool gameInFront = fgPid == (uint)_game.Process.Id || fg == Handle || _drag != null;
        bool show = battle && hwnd != IntPtr.Zero && !Native.IsIconic(hwnd) && gameInFront;
        LogState(battle, gameInFront, hwnd);
        if (!show) { HidePanel(); return; }

        Native.GetClientRect(hwnd, out var rc);
        var p = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(hwnd, ref p);
        _gameRect = new Rectangle(p.X, p.Y, rc.Right, rc.Bottom);

        float s = Math.Max(0.5f, _gameRect.Height / 1080f);
        var size = new Size((int)(330 * s), (int)(46 * s));
        int cx = _gameRect.X + (int)(_px * _gameRect.Width), cy = _gameRect.Y + (int)(_py * _gameRect.Height);
        int x = Math.Clamp(cx - size.Width / 2, _gameRect.Left, Math.Max(_gameRect.Left, _gameRect.Right - size.Width));
        int y = Math.Clamp(cy - size.Height / 2, _gameRect.Top, Math.Max(_gameRect.Top, _gameRect.Bottom - size.Height));
        var bounds = new Rectangle(x, y, size.Width, size.Height);
        if (Bounds != bounds) Bounds = bounds;
        if (!Visible) Show();
        // the game window is itself always-on-top, so keep re-raising the panel above it
        Native.SetWindowPos(Handle, (IntPtr)(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0,
            0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ | 0x0010 /*NOACTIVATE*/ | 0x0200 /*NOOWNERZORDER*/);
        Invalidate();
    }

    void HidePanel() { if (Visible) Hide(); }

    // ---- input ---------------------------------------------------------------------------

    float Ui => Math.Max(0.5f, Height / 46f);
    Rectangle BadgeRect => new((int)(6 * Ui), (int)(6 * Ui), (int)(62 * Ui), Height - (int)(12 * Ui));
    float TrackX0 => BadgeRect.Right + 22 * Ui;
    float TrackX1 => Width - 14 * Ui;
    float TrackY => Height * 0.56f;
    float KnobX(int sp) => TrackX0 + (TrackX1 - TrackX0) * (sp - 1) / (MaxSpeed - 1);
    int SpeedAt(int x) => (int)Math.Round(1 + Math.Clamp((x - TrackX0) / (TrackX1 - TrackX0), 0, 1) * (MaxSpeed - 1));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (BadgeRect.Contains(e.Location))
        {
            _drag = "panel"; _moved = false;
            _pressPt = Cursor.Position;
            _dragOffset = new Point(e.X - Width / 2, e.Y - Height / 2);
        }
        else
        {
            _drag = "knob";
            SetSpeed(SpeedAt(e.X));
        }
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (_drag != null) DragStep(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag == "panel" && !_moved)
        {
            if (_speed > 1) { _lastFast = _speed; SetSpeed(1); }
            else SetSpeed(_lastFast);
        }
        if (_drag != null) SaveSettings();
        _drag = null;
        Capture = false;
    }

    void DragStep()
    {
        var c = Cursor.Position;
        if (_drag == "knob")
        {
            SetSpeed(SpeedAt(PointToClient(c).X));
        }
        else if (_drag == "panel" && _gameRect.Width > 0)
        {
            if (Math.Abs(c.X - _pressPt.X) + Math.Abs(c.Y - _pressPt.Y) > 6) _moved = true;
            if (_moved)
            {
                _px = Math.Clamp((c.X - _dragOffset.X - _gameRect.X) / (double)_gameRect.Width, 0, 1);
                _py = Math.Clamp((c.Y - _dragOffset.Y - _gameRect.Y) / (double)_gameRect.Height, 0, 1);
                UpdatePlacement(true);
            }
        }
    }

    void SetSpeed(int n)
    {
        n = Math.Clamp(n, 1, MaxSpeed);
        if (n == _speed) return;
        _speed = n;
        if (n > 1) _lastFast = n;
        Invalidate();
    }

    // ---- drawing -------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float s = Ui;

        using (var path = Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10 * s))
        {
            using var bg = new SolidBrush(Bg);
            using var pen = new Pen(Border, Math.Max(1.5f, 2 * s));
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
        }

        var b = BadgeRect;
        using (var path = Rounded(b, 7 * s))
        using (var br = new SolidBrush(_speed > 1 ? BadgeOn : BadgeOff))
            g.FillPath(br, path);
        using (var f = new Font("Segoe UI", 15 * s, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var tb = new SolidBrush(TextCol))
        {
            var label = "x" + _speed;
            var sz = g.MeasureString(label, f);
            g.DrawString(label, f, tb, b.X + (b.Width - sz.Width) / 2, b.Y + (b.Height - sz.Height) / 2);
        }

        float ty = TrackY;
        using (var tp = new Pen(Track, Math.Max(2, 6 * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(tp, TrackX0, ty, TrackX1, ty);
        using (var fp = new Pen(Fill, Math.Max(2, 6 * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(fp, TrackX0, ty, KnobX(_speed), ty);

        using (var nf = new Font("Segoe UI", 9 * s, GraphicsUnit.Pixel))
            for (int i = 1; i <= MaxSpeed; i++)
            {
                float nx = KnobX(i);
                using var nb = new SolidBrush(i <= _speed ? NotchOn : NotchOff);
                float r = Math.Max(2, 3 * s);
                g.FillEllipse(nb, nx - r, ty - r, 2 * r, 2 * r);
                var t = i.ToString(CultureInfo.InvariantCulture);
                var sz = g.MeasureString(t, nf);
                g.DrawString(t, nf, nb, nx - sz.Width / 2, 1 * s);
            }

        float kx = KnobX(_speed);
        using (var rim = new SolidBrush(KnobRim)) g.FillEllipse(rim, kx - 11 * s, ty - 11 * s, 22 * s, 22 * s);
        using (var kb = new SolidBrush(Knob)) g.FillEllipse(kb, kx - 8.5f * s, ty - 8.5f * s, 17 * s, 17 * s);
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---- settings ------------------------------------------------------------------------

    void LoadSettings()
    {
        try
        {
            foreach (var line in File.ReadAllLines(_settingsPath))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                var v = kv[1].Trim();
                switch (kv[0].Trim())
                {
                    case "last": _lastFast = Math.Clamp(int.Parse(v, CultureInfo.InvariantCulture), 2, MaxSpeed); break;
                    case "px": _px = Math.Clamp(double.Parse(v, CultureInfo.InvariantCulture), 0, 1); break;
                    case "py": _py = Math.Clamp(double.Parse(v, CultureInfo.InvariantCulture), 0, 1); break;
                }
            }
        }
        catch { /* first run */ }
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, string.Format(CultureInfo.InvariantCulture,
                "last={0}\npx={1:0.####}\npy={2:0.####}\n", _lastFast, _px, _py));
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // leave the game at normal speed
        try { if (_game != null && _game.Alive && Math.Abs(_written - 1f) > 0.001f) _game.TimeScale = 1f; } catch { }
        base.OnFormClosing(e);
    }
}
