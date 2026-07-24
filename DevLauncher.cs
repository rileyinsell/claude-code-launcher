using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Standalone Dev Launcher. The window belongs to THIS exe (not powershell),
// so it pins to the taskbar as "Dev Launcher" and relaunches correctly.
// Tiles are read from apps.txt next to the exe -> no recompile to edit them.

class AppEntry
{
    public string Name = "", Path = "", Prompt = "";
    public string Model = "";     // claude --model id; empty = CLI default
    public string TabTitle = "";  // terminal tab name override; empty = Name
    public DateTime Modified;
}

// What the launch dialog hands back. Null prompt/model/tab all have defaults.
// LoopMinutes 0 = no loop; ReadClaudeMd prepends a "Read CLAUDE.md first." lead-in.
class LaunchOptions
{
    public string Prompt = "", Model = "", TabTitle = "";
    public bool ReadClaudeMd;
    public int LoopMinutes;
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new LauncherForm());
    }
}

class LauncherForm : Form
{
    static readonly Color[] Palette = {
        ColorTranslator.FromHtml("#2563EB"), ColorTranslator.FromHtml("#0891B2"),
        ColorTranslator.FromHtml("#7C3AED"), ColorTranslator.FromHtml("#DB2777"),
        ColorTranslator.FromHtml("#059669"), ColorTranslator.FromHtml("#D97706"),
        ColorTranslator.FromHtml("#DC2626"), ColorTranslator.FromHtml("#4F46E5"),
        ColorTranslator.FromHtml("#0D9488"), ColorTranslator.FromHtml("#C026D3"),
    };

    // Where new projects are created, and the default prompt new tiles get.
    const string ProjectsRoot = @"C:\Dev";
    const string NewProjectPrompt =
        "Read CLAUDE.md and the latest file in sessions/ to catch up on history, then continue work. "
        + "Track this session in sessions/ per the rules in CLAUDE.md, updating it after every turn.";

    FlowLayoutPanel flow;   // tile area, so new tiles can be appended after creation
    int colorIndex;         // next palette color to hand out
    Label emptyHint;        // "no apps" placeholder, removed once a tile exists
    readonly ToolTip tip = new ToolTip();  // shared tooltip for tile buttons
    TextBox searchBox;      // header filter; tiles hide/show as you type
    Control newProjectTile; // kept last; hidden while a search is active
    Dictionary<string, DateTime> lastUsed;  // app name -> last launch (recent.txt)
    FlowLayoutPanel favBar; // starred-projects strip under the header
    HashSet<string> favorites;  // starred folder paths (favorites.txt)
    List<AppEntry> allApps;     // every tile's entry, sorted by Modified desc
    bool rowsMode;              // ☰ rows vs ▦ tiles; persisted in view.txt
    Label tilesBtn, rowsBtn;    // header view-mode toggle

    public LauncherForm()
    {
        Text = "Dev Launcher";
        BackColor = ColorTranslator.FromHtml("#070D1A");
        StartPosition = FormStartPosition.CenterScreen;
        // Open big: most of the working area (capped), not the old 720x560 postage stamp.
        var wa = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(1280, wa.Width - 80), Math.Min(820, wa.Height - 100));
        MinimumSize = new Size(480, 360);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // header
        // Width must be set BEFORE anchored children are added: Anchor=Right captures
        // the distance to the parent's right edge at add time, and a docked panel
        // still has the 200px default width until it's added to the form.
        var header = new Panel { Dock = DockStyle.Top, Height = 70, Width = ClientSize.Width,
            BackColor = ColorTranslator.FromHtml("#0A1428") };
        var title = new Label {
            Text = "⚡ DEV LAUNCHER", ForeColor = ColorTranslator.FromHtml("#F8FAFC"),
            Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true,
            Location = new Point(18, 12), BackColor = Color.Transparent };
        var sub = new Label {
            Text = "Click an app — opens a terminal in the folder and starts Claude on it.",
            ForeColor = ColorTranslator.FromHtml("#64748B"), Font = new Font("Segoe UI", 9F),
            AutoSize = true, Location = new Point(22, 44), BackColor = Color.Transparent };
        // Neon divider: a cyan-to-transparent gradient line along the header's bottom.
        var divider = new Panel { Dock = DockStyle.Bottom, Height = 2 };
        divider.Paint += (s, e) => {
            if (divider.Width <= 0) return;
            using (var g = new LinearGradientBrush(divider.ClientRectangle,
                ColorTranslator.FromHtml("#22D3EE"), Color.FromArgb(0, 34, 211, 238),
                LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(g, divider.ClientRectangle);
        };
        divider.Resize += (s, e) => divider.Invalidate();
        header.Controls.Add(sub);
        header.Controls.Add(title);
        header.Controls.Add(divider);

        // Search box: filters tiles by name as you type. Enter launches the
        // first (= most recently used) match; Esc clears. Nested panels fake a
        // rounded neon border (a borderless TextBox can't draw its own).
        var searchWrap = new Panel {
            Location = new Point(ClientSize.Width - 240, 20), Size = new Size(224, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = ColorTranslator.FromHtml("#164E63") };
        RoundCorners(searchWrap, 15);
        var searchInner = new Panel {
            Location = new Point(1, 1), Size = new Size(222, 28),
            BackColor = ColorTranslator.FromHtml("#0D1526") };
        RoundCorners(searchInner, 14);
        searchBox = new TextBox {
            Location = new Point(12, 6), Size = new Size(198, 18),
            BackColor = ColorTranslator.FromHtml("#0D1526"),
            ForeColor = ColorTranslator.FromHtml("#F8FAFC"),
            BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10F) };
        searchBox.TextChanged += (s, e) => ApplyFilter();
        searchBox.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter)  { LaunchFirstVisible(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) { searchBox.Text = "";  e.SuppressKeyPress = true; }
        };
        searchBox.HandleCreated += (s, e) =>
            SendMessage(searchBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "Search apps…");
        searchInner.Controls.Add(searchBox);
        searchWrap.Controls.Add(searchInner);
        header.Controls.Add(searchWrap);

        // View toggle: ▦ tiles / ☰ rows, left of the search box.
        tilesBtn = MakeViewButton("▦", new Point(ClientSize.Width - 312, 20));
        tip.SetToolTip(tilesBtn, "Tile view");
        tilesBtn.Click += (s, e) => SetViewMode(false);
        rowsBtn = MakeViewButton("☰", new Point(ClientSize.Width - 278, 20));
        tip.SetToolTip(rowsBtn, "Row view");
        rowsBtn.Click += (s, e) => SetViewMode(true);
        header.Controls.Add(tilesBtn);
        header.Controls.Add(rowsBtn);

        // favorites bar: pill per starred project, newest-modified first.
        // Hidden until something is starred. AutoSize so pills can wrap to a
        // second row without clipping.
        favBar = new FlowLayoutPanel {
            Dock = DockStyle.Top, AutoSize = true, WrapContents = true,
            Padding = new Padding(10, 6, 10, 2), Visible = false,
            BackColor = ColorTranslator.FromHtml("#081226") };

        // tile area
        flow = new FlowLayoutPanel {
            Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10),
            BackColor = ColorTranslator.FromHtml("#070D1A") };

        // Dock order is reverse of add order: header docks Top first,
        // then favBar docks Top beneath it, then flow fills the rest.
        Controls.Add(flow);
        Controls.Add(favBar);
        Controls.Add(header);

        // Focus the search box on open so you can just start typing.
        ActiveControl = searchBox;
        Shown += (s, e) => searchBox.Focus();

        lastUsed = LoadRecent();
        favorites = LoadFavorites();
        rowsMode = LoadViewMode();
        // Entries come pre-sorted by folder modified date (newest first).
        allApps = LoadApps();
        BuildGrid();
        StyleViewButtons();
        RebuildFavBar();

        // Rows stretch to the grid width; keep them in sync when the window resizes.
        flow.ClientSizeChanged += (s, e) => { if (rowsMode) ResizeRows(); };
    }

    // ---- view mode (tiles / rows), persisted in view.txt next to the exe ----

    static string ViewFile()
    {
        return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "view.txt");
    }

    static bool LoadViewMode()
    {
        try { return File.Exists(ViewFile()) && File.ReadAllText(ViewFile()).Trim() == "rows"; }
        catch { return false; }
    }

    void SetViewMode(bool rows)
    {
        if (rowsMode == rows) return;
        rowsMode = rows;
        try { File.WriteAllText(ViewFile(), rows ? "rows" : "tiles"); } catch { }
        StyleViewButtons();
        BuildGrid();
    }

    void StyleViewButtons()
    {
        tilesBtn.BackColor = !rowsMode
            ? ColorTranslator.FromHtml("#0891B2") : Color.FromArgb(40, 255, 255, 255);
        tilesBtn.ForeColor = !rowsMode ? Color.White : ColorTranslator.FromHtml("#94A3B8");
        rowsBtn.BackColor = rowsMode
            ? ColorTranslator.FromHtml("#0891B2") : Color.FromArgb(40, 255, 255, 255);
        rowsBtn.ForeColor = rowsMode ? Color.White : ColorTranslator.FromHtml("#94A3B8");
    }

    static Label MakeViewButton(string glyph, Point at)
    {
        var b = new Label {
            Text = glyph, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Location = at, Size = new Size(30, 30), Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
        RoundCorners(b, 8);
        return b;
    }

    // Rebuild the whole grid in the current view mode. Tiles and rows carry their
    // AppEntry in Tag either way, so search/filter/MRU logic is mode-agnostic.
    void BuildGrid()
    {
        flow.SuspendLayout();
        flow.Controls.Clear();
        emptyHint = null;
        colorIndex = 0;
        foreach (var app in allApps)
        {
            Color accent = Palette[colorIndex++ % Palette.Length];
            flow.Controls.Add(rowsMode ? MakeRow(app, accent) : MakeTile(app, accent));
        }
        newProjectTile = rowsMode ? MakeNewProjectRow() : MakeNewProjectTile();
        flow.Controls.Add(newProjectTile);
        if (allApps.Count == 0)
        {
            emptyHint = new Label {
                Text = "No apps yet. Click ➕ New Project to create one,\nor edit apps.txt next to the launcher.",
                ForeColor = ColorTranslator.FromHtml("#94A3B8"), AutoSize = true,
                Margin = new Padding(12) };
            flow.Controls.Add(emptyHint);
        }
        flow.ResumeLayout();
        ApplyFilter();
    }

    int RowWidth() { return Math.Max(320, flow.ClientSize.Width - 34); }

    void ResizeRows()
    {
        foreach (Control c in flow.Controls)
            if (c.Tag is AppEntry || c == newProjectTile) c.Width = RowWidth();
    }

    // Full-width one-line row: accent bar + name, path, modified date, and the
    // same ★ / ✎ / 📁 actions as a tile. Click anywhere else to launch.
    Control MakeRow(AppEntry app, Color accent)
    {
        var row = new Panel {
            Width = RowWidth(), Height = 44, Margin = new Padding(4, 3, 4, 3),
            BackColor = ColorTranslator.FromHtml("#0E1830"), Cursor = Cursors.Hand, Tag = app };
        RoundCorners(row, 10);

        bool[] hot = { false };
        row.Paint += (s, e) => {
            var r = row.ClientRectangle;
            using (var wash = new LinearGradientBrush(r,
                Color.FromArgb(hot[0] ? 110 : 45, accent),
                Color.FromArgb(hot[0] ? 25 : 5, accent), LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(wash, r);
            using (var bar = new SolidBrush(accent))
                e.Graphics.FillRectangle(bar, 0, 0, 4, row.Height);
        };
        row.Resize += (s, e) => { RoundCorners(row, 10); row.Invalidate(); };

        var name = new Label {
            Text = app.Name, ForeColor = Color.White,
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold), AutoSize = false,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(16, 0), Size = new Size(240, 44),
            BackColor = Color.Transparent, Cursor = Cursors.Hand };
        var path = new Label {
            Text = app.Path, ForeColor = ColorTranslator.FromHtml("#8FA3C0"),
            Font = new Font("Segoe UI", 8.5F), AutoEllipsis = true, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(264, 0), Size = new Size(row.Width - 264 - 226, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                   | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.Transparent, Cursor = Cursors.Hand };
        var mod = new Label {
            Text = app.Modified == DateTime.MinValue
                ? "" : app.Modified.ToString("yyyy-MM-dd HH:mm"),
            ForeColor = ColorTranslator.FromHtml("#64748B"),
            Font = new Font("Consolas", 8.5F), AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(row.Width - 222, 0), Size = new Size(114, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.Transparent, Cursor = Cursors.Hand };

        var starBtn = MakeStarButton(app, new Point(row.Width - 100, 10));
        var promptBtn = MakeActionButton("✎", new Point(row.Width - 68, 10));
        tip.SetToolTip(promptBtn, "Launch with a custom prompt…");
        promptBtn.Click += (s, e) => LaunchWithPrompt(app);
        var folderBtn = MakeActionButton("📁", new Point(row.Width - 36, 10));
        tip.SetToolTip(folderBtn, "Browse project files…");
        folderBtn.Click += (s, e) => new FolderViewerForm(app).Show(this);
        starBtn.Anchor = promptBtn.Anchor = folderBtn.Anchor =
            AnchorStyles.Top | AnchorStyles.Right;

        EventHandler click = (s, e) => Launch(app);
        row.Click += click; name.Click += click; path.Click += click; mod.Click += click;

        EventHandler enter = (s, e) => { if (!hot[0]) { hot[0] = true; row.Invalidate(true); } };
        EventHandler leave = (s, e) => { hot[0] = false; row.Invalidate(true); };
        row.MouseEnter += enter; row.MouseLeave += leave;
        name.MouseEnter += enter; path.MouseEnter += enter; mod.MouseEnter += enter;

        row.Controls.Add(name);
        row.Controls.Add(path);
        row.Controls.Add(mod);
        row.Controls.Add(starBtn);
        row.Controls.Add(promptBtn);
        row.Controls.Add(folderBtn);
        return row;
    }

    // Row-mode counterpart of the dashed New Project tile.
    Control MakeNewProjectRow()
    {
        var row = new Panel {
            Width = RowWidth(), Height = 40, Margin = new Padding(4, 3, 4, 3),
            BackColor = ColorTranslator.FromHtml("#0A1428"), Cursor = Cursors.Hand };
        RoundCorners(row, 10);

        var cyan = ColorTranslator.FromHtml("#22D3EE");
        bool[] hot = { false };
        row.Paint += (s, e) => {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var gp = RoundedPath(new Rectangle(3, 3, row.Width - 7, row.Height - 7), 8))
            using (var pen = new Pen(Color.FromArgb(hot[0] ? 255 : 110, cyan), 1.6f)
                { DashStyle = DashStyle.Dash })
                e.Graphics.DrawPath(pen, gp);
        };
        row.Resize += (s, e) => { RoundCorners(row, 10); row.Invalidate(); };

        var label = new Label {
            Text = "＋  NEW PROJECT", ForeColor = cyan,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold), AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill,
            BackColor = Color.Transparent, Cursor = Cursors.Hand };

        EventHandler click = (s, e) => CreateNewProject();
        row.Click += click; label.Click += click;

        EventHandler enter = (s, e) => { if (!hot[0]) { hot[0] = true; row.Invalidate(true); } };
        EventHandler leave = (s, e) => { hot[0] = false; row.Invalidate(true); };
        row.MouseEnter += enter; row.MouseLeave += leave;
        label.MouseEnter += enter;

        row.Controls.Add(label);
        return row;
    }

    Control MakeTile(AppEntry app, Color color)
    {
        var tile = new Panel {
            Width = 200, Height = 96, Margin = new Padding(8),
            BackColor = ColorTranslator.FromHtml("#0E1830"), Cursor = Cursors.Hand, Tag = app };
        RoundCorners(tile, 14);

        // Neon skin: the palette color is now an ACCENT over a dark base — a
        // vertical wash (stronger while hovered) plus a solid bar on the left
        // edge. Painted, not BackColor, so transparent child labels pick it up.
        bool[] hot = { false };   // captured by Paint + hover handlers
        tile.Paint += (s, e) => {
            var r = tile.ClientRectangle;
            using (var wash = new LinearGradientBrush(r,
                Color.FromArgb(hot[0] ? 120 : 60, color),
                Color.FromArgb(hot[0] ? 30 : 8, color), LinearGradientMode.Vertical))
                e.Graphics.FillRectangle(wash, r);
            using (var bar = new SolidBrush(color))
                e.Graphics.FillRectangle(bar, 0, 0, 4, tile.Height);
        };

        // Name gets the full tile width and can wrap to two lines — the action
        // buttons live in a row along the bottom edge instead of the top corner,
        // so long project names aren't cut off anymore.
        var name = new Label {
            Text = app.Name, ForeColor = Color.White,
            Font = FitTileFont(app.Name, new Size(170, 38)),   // shrink-to-fit, no clipping
            AutoSize = false,
            AutoEllipsis = true, Location = new Point(16, 8), Size = new Size(170, 38),
            BackColor = Color.Transparent, Cursor = Cursors.Hand };
        var path = new Label {
            Text = app.Path, ForeColor = ColorTranslator.FromHtml("#8FA3C0"),
            Font = new Font("Segoe UI", 7.5F), AutoEllipsis = true, AutoSize = false,
            Location = new Point(16, 47), Size = new Size(170, 14),
            BackColor = Color.Transparent, Cursor = Cursors.Hand };

        var starBtn = MakeStarButton(app, new Point(96, 66));
        var promptBtn = MakeActionButton("✎", new Point(128, 66));
        tip.SetToolTip(promptBtn, "Launch with a custom prompt…");
        promptBtn.Click += (s, e) => LaunchWithPrompt(app);
        var folderBtn = MakeActionButton("📁", new Point(160, 66));
        tip.SetToolTip(folderBtn, "Browse project files…");
        folderBtn.Click += (s, e) => new FolderViewerForm(app).Show(this);

        EventHandler click = (s, e) => Launch(app);
        tile.Click += click; name.Click += click; path.Click += click;

        // hover feedback: brighten the painted accent wash
        EventHandler enter = (s, e) => { if (!hot[0]) { hot[0] = true; tile.Invalidate(true); } };
        EventHandler leave = (s, e) => { hot[0] = false; tile.Invalidate(true); };
        tile.MouseEnter += enter; tile.MouseLeave += leave;
        name.MouseEnter += enter; path.MouseEnter += enter;

        tile.Controls.Add(name);
        tile.Controls.Add(path);
        tile.Controls.Add(promptBtn);   // buttons added last -> sit on top
        tile.Controls.Add(starBtn);
        tile.Controls.Add(folderBtn);
        return tile;
    }

    // Largest bold Segoe UI (11.5 down to 8) whose word-wrapped text fits the box,
    // so long project names show in full on the tile instead of being cut off.
    static Font FitTileFont(string text, Size box)
    {
        for (float size = 11.5f; size > 8f; size -= 0.5f)
        {
            var f = new Font("Segoe UI", size, FontStyle.Bold);
            var need = TextRenderer.MeasureText(text, f,
                new Size(box.Width, int.MaxValue), TextFormatFlags.WordBreak);
            if (need.Height <= box.Height) return f;
            f.Dispose();
        }
        return new Font("Segoe UI", 8f, FontStyle.Bold);
    }

    // Translucent 28x24 action button used on tiles and rows.
    static Label MakeActionButton(string text, Point at)
    {
        var b = new Label {
            Text = text, ForeColor = Color.White,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold), AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter, Size = new Size(28, 24),
            Location = at, BackColor = Color.FromArgb(48, 255, 255, 255),
            Cursor = Cursors.Hand };
        RoundCorners(b, 7);
        Color baseC = b.BackColor, hoverC = Color.FromArgb(110, 255, 255, 255);
        b.MouseEnter += (s, e) => b.BackColor = hoverC;
        b.MouseLeave += (s, e) => b.BackColor = baseC;
        return b;
    }

    // Star toggle: pins/unpins this project on the favorites bar.
    Label MakeStarButton(AppEntry app, Point at)
    {
        var b = MakeActionButton(IsFavorite(app) ? "★" : "☆", at);
        b.ForeColor = IsFavorite(app) ? ColorTranslator.FromHtml("#FBBF24") : Color.White;
        tip.SetToolTip(b, "Star: pin to the favorites bar");
        b.Click += (s, e) => {
            ToggleFavorite(app);
            b.Text = IsFavorite(app) ? "★" : "☆";
            b.ForeColor = IsFavorite(app)
                ? ColorTranslator.FromHtml("#FBBF24") : Color.White;
        };
        return b;
    }

    // ---- favorites, persisted in favorites.txt next to the exe ----

    static string FavoritesFile()
    {
        return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "favorites.txt");
    }

    static HashSet<string> LoadFavorites()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string file = FavoritesFile();
            if (!File.Exists(file)) return set;
            foreach (var line in File.ReadAllLines(file))
            {
                var p = line.Trim();
                if (p.Length > 0) set.Add(p);
            }
        }
        catch { }   // a broken favorites.txt must never block startup
        return set;
    }

    void SaveFavorites()
    {
        try { File.WriteAllLines(FavoritesFile(), favorites.ToArray()); }
        catch { }   // favorites are a nicety; never crash over them
    }

    bool IsFavorite(AppEntry app) { return favorites.Contains(app.Path); }

    void ToggleFavorite(AppEntry app)
    {
        if (!favorites.Remove(app.Path)) favorites.Add(app.Path);
        SaveFavorites();
        RebuildFavBar();
    }

    // Repopulate the favorites strip: one pill per starred project,
    // ordered by folder modified date (newest first), same as the grid.
    void RebuildFavBar()
    {
        favBar.SuspendLayout();
        favBar.Controls.Clear();
        var favs = allApps.Where(IsFavorite)
                          .OrderByDescending(a => a.Modified).ToList();
        if (favs.Count > 0)
            favBar.Controls.Add(new Label {
                Text = "◈ PINNED", AutoSize = true, Margin = new Padding(8, 14, 6, 0),
                ForeColor = ColorTranslator.FromHtml("#38BDF8"),
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                BackColor = Color.Transparent });
        foreach (var app in favs) favBar.Controls.Add(MakePill(app));
        favBar.Visible = favs.Count > 0;
        favBar.ResumeLayout();
    }

    // Compact click-to-launch pill for the favorites bar: neon cyan capsule with
    // an amber star, the project name, and ✎ / 📁 buttons on the right (custom
    // prompt launch and folder viewer — same as the tile corner buttons).
    Control MakePill(AppEntry app)
    {
        var font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        int nameW = TextRenderer.MeasureText(app.Name, font).Width;
        var pill = new Panel {
            Size = new Size(nameW + 92, 32), Margin = new Padding(5),
            BackColor = ColorTranslator.FromHtml("#0C1930"),
            Cursor = Cursors.Hand, Tag = app };
        RoundCorners(pill, 16);

        // capsule skin: cyan wash + glowing outline, brighter while hovered
        var cyan = ColorTranslator.FromHtml("#22D3EE");
        bool[] hot = { false };
        pill.Paint += (s, e) => {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = pill.ClientRectangle;
            using (var wash = new LinearGradientBrush(r,
                Color.FromArgb(hot[0] ? 90 : 40, cyan),
                Color.FromArgb(hot[0] ? 25 : 6, cyan), LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(wash, r);
            using (var gp = RoundedPath(new Rectangle(1, 1, pill.Width - 3, pill.Height - 3), 14))
            using (var pen = new Pen(Color.FromArgb(hot[0] ? 230 : 110, cyan), 1.5f))
                e.Graphics.DrawPath(pen, gp);
        };

        var star = new Label {
            Text = "★", ForeColor = ColorTranslator.FromHtml("#FBBF24"),
            Font = font, AutoSize = false, Size = new Size(18, 32),
            TextAlign = ContentAlignment.MiddleCenter, Location = new Point(8, 0),
            BackColor = Color.Transparent, Cursor = Cursors.Hand };
        var label = new Label {
            Text = app.Name, Font = font, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(26, 0), Size = new Size(nameW + 6, 32),
            ForeColor = ColorTranslator.FromHtml("#F8FAFC"),
            BackColor = Color.Transparent, Cursor = Cursors.Hand };
        tip.SetToolTip(label, app.Path);

        int btnX = nameW + 38;
        var promptBtn = MakePillButton("✎", new Point(btnX, 5));
        tip.SetToolTip(promptBtn, "Launch with a custom prompt…");
        promptBtn.Click += (s, e) => LaunchWithPrompt(app);
        var folderBtn = MakePillButton("📁", new Point(btnX + 26, 5));
        tip.SetToolTip(folderBtn, "Browse project files…");
        folderBtn.Click += (s, e) => new FolderViewerForm(app).Show(this);

        EventHandler click = (s, e) => Launch(app);
        pill.Click += click; label.Click += click; star.Click += click;

        EventHandler enter = (s, e) => { if (!hot[0]) { hot[0] = true; pill.Invalidate(true); } };
        EventHandler leave = (s, e) => { hot[0] = false; pill.Invalidate(true); };
        pill.MouseEnter += enter; pill.MouseLeave += leave;
        label.MouseEnter += enter; star.MouseEnter += enter;

        pill.Controls.Add(star);
        pill.Controls.Add(label);
        pill.Controls.Add(promptBtn);
        pill.Controls.Add(folderBtn);
        return pill;
    }

    // Small translucent action button used inside favorites pills.
    static Label MakePillButton(string text, Point at)
    {
        var b = new Label {
            Text = text, ForeColor = ColorTranslator.FromHtml("#E2E8F0"),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter, Size = new Size(22, 22),
            Location = at, BackColor = Color.FromArgb(40, 255, 255, 255),
            Cursor = Cursors.Hand };
        RoundCorners(b, 7);
        Color baseC = b.BackColor, hoverC = Color.FromArgb(110, 255, 255, 255);
        b.MouseEnter += (s, e) => b.BackColor = hoverC;
        b.MouseLeave += (s, e) => b.BackColor = baseC;
        return b;
    }

    // A distinct tile that creates a brand-new project: dark base with a dashed
    // cyan outline that lights up on hover.
    Control MakeNewProjectTile()
    {
        var tile = new Panel {
            Width = 200, Height = 96, Margin = new Padding(8),
            BackColor = ColorTranslator.FromHtml("#0A1428"), Cursor = Cursors.Hand };
        RoundCorners(tile, 14);

        var cyan = ColorTranslator.FromHtml("#22D3EE");
        bool[] hot = { false };
        tile.Paint += (s, e) => {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var gp = RoundedPath(new Rectangle(3, 3, tile.Width - 7, tile.Height - 7), 11))
            using (var pen = new Pen(Color.FromArgb(hot[0] ? 255 : 110, cyan), 1.6f)
                { DashStyle = DashStyle.Dash })
                e.Graphics.DrawPath(pen, gp);
        };

        var label = new Label {
            Text = "＋  NEW PROJECT", ForeColor = cyan,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold), AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill,
            BackColor = Color.Transparent, Cursor = Cursors.Hand };

        EventHandler click = (s, e) => CreateNewProject();
        tile.Click += click; label.Click += click;

        EventHandler enter = (s, e) => { if (!hot[0]) { hot[0] = true; tile.Invalidate(true); } };
        EventHandler leave = (s, e) => { hot[0] = false; tile.Invalidate(true); };
        tile.MouseEnter += enter; tile.MouseLeave += leave;
        label.MouseEnter += enter;

        tile.Controls.Add(label);
        return tile;
    }

    // Prompt for a name, scaffold C:\Dev\<name> with CLAUDE.md + sessions\,
    // persist it to apps.txt, add a live tile, and offer to open it.
    void CreateNewProject()
    {
        string name = PromptForName();
        if (name == null) return;                 // cancelled
        name = name.Trim();
        if (name.Length == 0) return;

        // Reject characters that aren't valid in a Windows folder name.
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show("That name has characters Windows doesn't allow in a folder.",
                "Invalid name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string projectPath = Path.Combine(ProjectsRoot, name);
        if (Directory.Exists(projectPath))
        {
            MessageBox.Show("A folder already exists at:\n" + projectPath,
                "Already exists", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(projectPath);
            Directory.CreateDirectory(Path.Combine(projectPath, "sessions"));
            File.WriteAllText(Path.Combine(projectPath, "CLAUDE.md"), ClaudeMdFor(name));
            File.WriteAllText(Path.Combine(projectPath, "sessions", ".gitkeep"), "");
            AppendToAppsTxt(name, projectPath, NewProjectPrompt);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't create the project:\n" + ex.Message,
                "Create error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Add it live: newest-modified goes first, then rebuild in the current view.
        var app = new AppEntry { Name = name, Path = projectPath, Prompt = NewProjectPrompt,
            Modified = DateTime.Now };
        allApps.Insert(0, app);
        BuildGrid();

        if (MessageBox.Show("Created " + name + " at:\n" + projectPath
                + "\n\nOpen it in a terminal with Claude now?",
                "Project created", MessageBoxButtons.YesNo, MessageBoxIcon.Information)
            == DialogResult.Yes)
            Launch(app);
    }

    // The starter CLAUDE.md, with the session-tracking rules the user asked for.
    static string ClaudeMdFor(string name)
    {
        return
"# CLAUDE.md — " + name + "\n" +
"\n" +
"New project scaffolded by Dev Launcher.\n" +
"\n" +
"## Session tracking (required)\n" +
"Every working session MUST be tracked in the `sessions/` folder so progress survives a\n" +
"shutdown or crash and there is a durable historical record of how this project evolved.\n" +
"\n" +
"Rules:\n" +
"- At the **start** of a session, create a new file `sessions/SESSION-<YYYY-MM-DD>-<NN>.md`\n" +
"  (NN = the next number for that day). Begin it with the date/time, the goal of the\n" +
"  session, and a one-line summary of where the project currently stands.\n" +
"- **After every turn**, append what just happened: what was attempted, what changed\n" +
"  (files, commands run, decisions made), the result, and the next intended step. Write\n" +
"  this incrementally as you go — never batch it to the end — so an unexpected shutdown\n" +
"  loses nothing.\n" +
"- At the **start** of any later session, read the most recent file in `sessions/` (and\n" +
"  skim earlier ones as needed) to restore context before doing any work.\n" +
"- Treat `sessions/` as append-only history: start a new file per session, don't rewrite\n" +
"  or delete past session logs.\n" +
"\n" +
"## What this project is\n" +
"_(Fill this in as the project takes shape.)_\n";
    }

    // Persist the new tile to apps.txt so it survives a relaunch.
    static void AppendToAppsTxt(string name, string path, string prompt)
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string file = Path.Combine(dir, "apps.txt");
        string line = name + " | " + path + " | " + prompt + Environment.NewLine;
        File.AppendAllText(file, line);
    }

    // Minimal modal text-input dialog (WinForms has no built-in InputBox).
    static string PromptForName()
    {
        using (var dlg = new Form())
        {
            dlg.Text = "New Project";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(380, 130);
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = ColorTranslator.FromHtml("#0F172A");

            var prompt = new Label {
                Text = "Project name (a folder is created under " + ProjectsRoot + "):",
                ForeColor = ColorTranslator.FromHtml("#E2E8F0"), AutoSize = false,
                Location = new Point(16, 14), Size = new Size(348, 36) };
            var box = new TextBox {
                Location = new Point(16, 52), Size = new Size(348, 24),
                BackColor = ColorTranslator.FromHtml("#111C33"),
                ForeColor = ColorTranslator.FromHtml("#E2E8F0"),
                BorderStyle = BorderStyle.FixedSingle };
            var ok = MakeDialogButton("Create", ColorTranslator.FromHtml("#0891B2"),
                DialogResult.OK);
            ok.Location = new Point(180, 88); ok.Size = new Size(92, 30);
            var cancel = MakeDialogButton("Cancel", ColorTranslator.FromHtml("#1E293B"),
                DialogResult.Cancel);
            cancel.Location = new Point(280, 88); cancel.Size = new Size(84, 30);

            dlg.Controls.Add(prompt);
            dlg.Controls.Add(box);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            return dlg.ShowDialog() == DialogResult.OK ? box.Text : null;
        }
    }

    // Shared with FolderViewerForm.
    internal static void RoundCorners(Control c, int radius)
    {
        c.Region = new Region(RoundedPath(new Rectangle(0, 0, c.Width, c.Height), radius));
    }

    // Rounded-rect outline path — used both for control regions and for drawing
    // the neon borders (a Region clips, so borders are drawn inset via Paint).
    internal static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var gp = new GraphicsPath();
        int d = radius * 2;
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    // Prompt used for folders that have no apps.txt entry.
    const string DefaultPrompt =
        "Read CLAUDE.md and/or the README if present to understand this project, "
        + "then help me continue working on it.";

    // Every folder directly under C:\Dev gets a tile, ordered by folder modified
    // date (newest first). apps.txt is an OVERRIDES file: an entry whose path
    // matches a folder supplies its display name and initial prompt; folders
    // without an entry get the folder name and DefaultPrompt.
    List<AppEntry> LoadApps()
    {
        var overrides = LoadAppsTxt();
        var list = new List<AppEntry>();
        try
        {
            foreach (var d in new DirectoryInfo(ProjectsRoot).GetDirectories()
                         .Where(d => (d.Attributes & FileAttributes.Hidden) == 0)
                         .OrderByDescending(d => d.LastWriteTime))
            {
                AppEntry o;
                overrides.TryGetValue(d.FullName, out o);
                list.Add(new AppEntry {
                    Name   = (o != null && o.Name.Length   > 0) ? o.Name   : d.Name,
                    Path   = d.FullName,
                    Prompt = (o != null && o.Prompt.Length > 0) ? o.Prompt : DefaultPrompt,
                    Modified = d.LastWriteTime
                });
            }
        }
        catch { }   // an unreadable ProjectsRoot must never block startup

        // apps.txt entries pointing somewhere else (outside C:\Dev) still get tiles.
        foreach (var o in overrides.Values)
            if (!list.Any(a => a.Path.Equals(o.Path, StringComparison.OrdinalIgnoreCase)))
            {
                try { o.Modified = Directory.GetLastWriteTime(o.Path); } catch { }
                list.Add(o);
            }
        return list;
    }

    // apps.txt entries keyed by normalized full path (case-insensitive).
    static Dictionary<string, AppEntry> LoadAppsTxt()
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        string file = Path.Combine(dir, "apps.txt");
        if (!File.Exists(file)) return map;

        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split(new[] { '|' }, 3);
            if (parts.Length < 2) continue;
            var entry = new AppEntry {
                Name = parts[0].Trim(),
                Path = parts[1].Trim(),
                Prompt = parts.Length >= 3 ? parts[2].Trim() : ""
            };
            string key;
            try { key = Path.GetFullPath(entry.Path).TrimEnd('\\'); }
            catch { continue; }   // skip malformed paths rather than crash
            map[key] = entry;
        }
        return map;
    }

    // ---- search ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    const int EM_SETCUEBANNER = 0x1501;   // native textbox placeholder text

    // Show only tiles whose name contains the query. App tiles carry their
    // AppEntry in Tag; the New Project tile (Tag == null) hides during a search.
    void ApplyFilter()
    {
        string q = searchBox.Text.Trim();
        flow.SuspendLayout();
        foreach (Control c in flow.Controls)
        {
            var app = c.Tag as AppEntry;
            if (app != null)
                c.Visible = q.Length == 0
                    || app.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            else if (c == newProjectTile)
                c.Visible = q.Length == 0;
        }
        flow.ResumeLayout();
    }

    // Enter in the search box: launch the top visible tile (MRU order = best match first-ish).
    void LaunchFirstVisible()
    {
        foreach (Control c in flow.Controls)
        {
            var app = c.Tag as AppEntry;
            if (app != null && c.Visible) { Launch(app); return; }
        }
    }

    // ---- most-recently-used ordering, persisted in recent.txt next to the exe ----

    static string RecentFile()
    {
        return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "recent.txt");
    }

    DateTime GetLastUsed(string name)
    {
        DateTime t;
        return lastUsed.TryGetValue(name, out t) ? t : DateTime.MinValue;
    }

    static Dictionary<string, DateTime> LoadRecent()
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string file = RecentFile();
            if (!File.Exists(file)) return map;
            foreach (var line in File.ReadAllLines(file))
            {
                int bar = line.LastIndexOf('|');   // name can't contain | (apps.txt rule)
                if (bar <= 0) continue;
                long ticks;
                if (long.TryParse(line.Substring(bar + 1).Trim(), out ticks))
                    map[line.Substring(0, bar).Trim()] = new DateTime(ticks);
            }
        }
        catch { }   // a broken recent.txt must never block startup
        return map;
    }

    // Stamp the launch time, persist it, and move the tile to the front
    // so the MRU order is visible immediately (not just on next open).
    void RecordUsage(string name)
    {
        lastUsed[name] = DateTime.Now;
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in lastUsed)
                sb.Append(kv.Key).Append('|').Append(kv.Value.Ticks).Append(Environment.NewLine);
            File.WriteAllText(RecentFile(), sb.ToString());
        }
        catch { }   // ordering is a nicety; never fail a launch over it

        foreach (Control c in flow.Controls)
        {
            var app = c.Tag as AppEntry;
            if (app != null && app.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            { flow.Controls.SetChildIndex(c, 0); break; }
        }
    }

    // Pop the launch dialog (prompt + model + tab name), then launch with it.
    // An empty prompt falls back to the app's default prompt, so you can use
    // the dialog just to pick a model or rename the tab.
    void LaunchWithPrompt(AppEntry app)
    {
        var opts = PromptForLaunchOptions(app.Name);
        if (opts == null) return;                   // cancelled
        string prompt = opts.Prompt.Length > 0 ? opts.Prompt : app.Prompt;

        // "Read CLAUDE.md" is prepended to the prompt TEXT, and the /loop prefix
        // goes in FRONT of that — claude only parses a slash command at position 0,
        // so with both on the result is: /loop <N>m Read CLAUDE.md first. <prompt>
        // (the CLAUDE.md instruction rides inside the looped prompt and survives).
        // Skip the prepend if the prompt already mentions CLAUDE.md (the default
        // prompts do) so it doesn't stutter.
        if (opts.ReadClaudeMd
            && prompt.IndexOf("CLAUDE.md", StringComparison.OrdinalIgnoreCase) < 0)
            prompt = "Read CLAUDE.md first. " + prompt;
        if (opts.LoopMinutes > 0)
            prompt = "/loop " + opts.LoopMinutes + "m " + prompt;

        var oneOff = new AppEntry {
            Name = app.Name, Path = app.Path, Prompt = prompt,
            Model = opts.Model, TabTitle = opts.TabTitle };
        Launch(oneOff);
    }

    // Model choices offered in the launch dialog. Labels are what's shown;
    // ids are passed to `claude --model`. Empty id = no flag (CLI default).
    static readonly string[] ModelLabels = {
        "Default model", "Opus 4.8", "Fable 5", "Sonnet 5", "Haiku 4.5" };
    static readonly string[] ModelIds = {
        "", "claude-opus-4-8", "claude-fable-5", "claude-sonnet-5", "claude-haiku-4-5" };

    // Flat dark-theme dialog button. The default WinForms button renders black
    // text on the system grey and is unreadable on these dark dialogs.
    static Button MakeDialogButton(string text, Color back, DialogResult result)
    {
        var b = new Button {
            Text = text, DialogResult = result,
            ForeColor = Color.White, BackColor = back,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Semibold", 10F) };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.25f);
        return b;
    }

    // Small cyan section caption for the launch dialog.
    static Label SectionLabel(string text, int x, int y)
    {
        return new Label {
            Text = text, AutoSize = true, Location = new Point(x, y),
            ForeColor = ColorTranslator.FromHtml("#38BDF8"),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold),
            BackColor = Color.Transparent };
    }

    // Modal launch dialog: model dropdown, optional tab name, multiline prompt.
    // Returns null if cancelled.
    static LaunchOptions PromptForLaunchOptions(string appName)
    {
        using (var dlg = new Form())
        {
            dlg.Text = "Launch " + appName;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.ClientSize = new Size(640, 500);
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.BackColor = ColorTranslator.FromHtml("#0A0F1E");

            Color fieldBack = ColorTranslator.FromHtml("#111C33");
            Color fieldFore = ColorTranslator.FromHtml("#E2E8F0");

            var header = new Label {
                Text = "⚡ LAUNCH  ·  " + appName,
                ForeColor = ColorTranslator.FromHtml("#22D3EE"),
                Font = new Font("Segoe UI", 14F, FontStyle.Bold),
                AutoSize = true, Location = new Point(22, 16),
                BackColor = Color.Transparent };
            var divider = new Panel {
                Location = new Point(24, 52), Size = new Size(592, 2),
                BackColor = ColorTranslator.FromHtml("#155E75") };

            var modelLabel = SectionLabel("MODEL", 24, 70);
            var modelBox = new ComboBox {
                Location = new Point(24, 90), Size = new Size(280, 30),
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = fieldBack, ForeColor = fieldFore,
                Font = new Font("Segoe UI", 10.5F) };
            modelBox.Items.AddRange(ModelLabels);
            modelBox.SelectedIndex = 0;

            var tabLabel = SectionLabel("TAB NAME  ·  OPTIONAL", 324, 70);
            var tabBox = new TextBox {
                Location = new Point(324, 92), Size = new Size(292, 28),
                BackColor = fieldBack, ForeColor = fieldFore,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10.5F) };
            tabBox.HandleCreated += (s, e) =>
                SendMessage(tabBox.Handle, EM_SETCUEBANNER, (IntPtr)1, appName);

            // Options row: loop (numeric, minutes) + read-CLAUDE.md toggle.
            var loopCheck = new CheckBox {
                Text = "Loop every", AutoSize = true, Location = new Point(24, 136),
                ForeColor = fieldFore, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5F), Cursor = Cursors.Hand };
            var loopBox = new NumericUpDown {
                Location = new Point(122, 133), Size = new Size(56, 26),
                Minimum = 1, Maximum = 999, Value = 5, Enabled = false,
                BackColor = fieldBack, ForeColor = fieldFore,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9.5F) };
            var minLabel = new Label {
                Text = "min   (runs the prompt as  /loop <N>m …)", AutoSize = true,
                Location = new Point(184, 136),
                ForeColor = ColorTranslator.FromHtml("#64748B"),
                Font = new Font("Segoe UI", 9F), BackColor = Color.Transparent };
            loopCheck.CheckedChanged += (s, e) => loopBox.Enabled = loopCheck.Checked;

            var claudeMdCheck = new CheckBox {
                Text = "Read CLAUDE.md first", AutoSize = true, Checked = true,
                Location = new Point(452, 136),
                ForeColor = fieldFore, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5F), Cursor = Cursors.Hand };

            var promptLabel = SectionLabel("INITIAL PROMPT", 24, 172);
            var box = new TextBox {
                Location = new Point(24, 192), Size = new Size(592, 230),
                Multiline = true, AcceptsReturn = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = ColorTranslator.FromHtml("#0D1526"), ForeColor = fieldFore,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 10.5F) };

            var hint = new Label {
                Text = "Leave the prompt empty to launch with this project's default prompt.",
                ForeColor = ColorTranslator.FromHtml("#64748B"),
                Font = new Font("Segoe UI", 8.5F), AutoSize = true,
                Location = new Point(24, 430), BackColor = Color.Transparent };

            var start = MakeDialogButton("▶  START", ColorTranslator.FromHtml("#0891B2"),
                DialogResult.OK);
            start.Location = new Point(386, 452); start.Size = new Size(128, 36);
            var cancel = MakeDialogButton("CANCEL", ColorTranslator.FromHtml("#1E293B"),
                DialogResult.Cancel);
            cancel.Location = new Point(524, 452); cancel.Size = new Size(92, 36);

            dlg.Controls.Add(header);
            dlg.Controls.Add(divider);
            dlg.Controls.Add(modelLabel);
            dlg.Controls.Add(modelBox);
            dlg.Controls.Add(tabLabel);
            dlg.Controls.Add(tabBox);
            dlg.Controls.Add(loopCheck);
            dlg.Controls.Add(loopBox);
            dlg.Controls.Add(minLabel);
            dlg.Controls.Add(claudeMdCheck);
            dlg.Controls.Add(promptLabel);
            dlg.Controls.Add(box);
            dlg.Controls.Add(hint);
            dlg.Controls.Add(start);
            dlg.Controls.Add(cancel);
            // Enter inside the box makes a newline (multiline); Start is clicked explicitly.
            dlg.CancelButton = cancel;
            dlg.ActiveControl = box;   // start typing the prompt immediately

            if (dlg.ShowDialog() != DialogResult.OK) return null;
            return new LaunchOptions {
                Prompt = box.Text.Trim(),
                Model = ModelIds[modelBox.SelectedIndex],
                TabTitle = tabBox.Text.Trim(),
                ReadClaudeMd = claudeMdCheck.Checked,
                LoopMinutes = loopCheck.Checked ? (int)loopBox.Value : 0 };
        }
    }

    // Normalize the curly "smart" single-quotes (U+2018/2019/201A/201B) to ASCII '.
    // PowerShell treats the curly variants as string delimiters too, NOT just ASCII ',
    // so a prompt pasted from Outlook/Word (e.g. "I've", "don't") would otherwise close
    // the surrounding PowerShell single-quoted string early.
    static string NormalizeQuotes(string s)
    {
        const char SQ = '\'';
        return s.Replace((char)0x2018, SQ).Replace((char)0x2019, SQ)
                .Replace((char)0x201A, SQ).Replace((char)0x201B, SQ);
    }

    // Escape a string for embedding inside a PowerShell single-quoted literal: ' -> ''.
    // Used for fields consumed by PowerShell itself (tab title, Set-Location path).
    static string PsSingleQuote(string s)
    {
        if (s == null) return "";
        return NormalizeQuotes(s).Replace("'", "''");
    }

    // Escape a string so it survives as ONE argv element through claude.exe's native
    // CommandLineToArgvW parsing. Windows PowerShell 5.1 wraps a native-command arg that
    // contains spaces in double quotes but does NOT escape the arg's own double quotes —
    // so a JSON/quoted prompt loses its " and then word-splits on the now-unquoted spaces,
    // and claude only receives the first chunk (the prompt looks "cut off"). Per the
    // standard argv rules: double any backslash run that precedes a ", escape each " as \",
    // and double a trailing backslash run (it would precede PowerShell's closing wrap ").
    static string WinArgInner(string s)
    {
        var sb = new StringBuilder();
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            if (slashes > 0) { sb.Append('\\', slashes); slashes = 0; }
            sb.Append(c);
        }
        if (slashes > 0) sb.Append('\\', slashes * 2);
        return sb.ToString();
    }

    // The prompt is the ONLY field passed to claude.exe as a native argument, so it needs
    // BOTH layers: native-arg escaping (for claude.exe) then single-quote escaping (for the
    // PowerShell -EncodedCommand script that wraps it).
    static string PsPromptArg(string s)
    {
        if (s == null) return "";
        return WinArgInner(NormalizeQuotes(s)).Replace("'", "''");
    }

    // Windows command lines cap out around 32K chars. A big pasted prompt, encoded
    // UTF-16LE + Base64 into -EncodedCommand, blows straight past that and
    // Process.Start fails with Win32 error 206 ("The filename or extension is too
    // long"). Prompts whose escaped form exceeds this go through a temp file that
    // the new tab reads (and deletes) before starting claude.
    const int InlinePromptMax = 1500;

    void Launch(AppEntry a)
    {
        RecordUsage(a.Name);

        // Tab/window title: the custom name from the launch dialog, else the app name.
        string title = (a.TabTitle != null && a.TabTitle.Length > 0) ? a.TabTitle : a.Name;

        // PowerShell command the new tab runs: name the tab, cd in, start claude.
        string name = PsSingleQuote(title);
        string path = PsSingleQuote(a.Path);
        // Optional model override from the launch dialog -> `claude --model <id>`.
        string model = (a.Model != null && a.Model.Length > 0)
            ? " --model '" + PsSingleQuote(a.Model) + "'" : "";

        // Short prompts ride inline (as before). Long ones are written to a temp
        // file holding the WinArgInner-escaped text: PowerShell passes a variable
        // to a native exe without escaping embedded double quotes, so the argv
        // escaping must already be baked into the text — the exact same trick as
        // PsPromptArg, just without the single-quote layer (no PS literal involved).
        string escaped = WinArgInner(NormalizeQuotes(a.Prompt ?? ""));
        string promptExpr = null, readCmd = "";
        if (escaped.Length > InlinePromptMax)
        {
            try
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "DevLauncher");
                Directory.CreateDirectory(tmpDir);
                string tmp = Path.Combine(tmpDir,
                    "prompt-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(tmp, escaped, new UTF8Encoding(false));
                readCmd = "$__p = [IO.File]::ReadAllText('" + PsSingleQuote(tmp) + "'); "
                        + "Remove-Item -LiteralPath '" + PsSingleQuote(tmp)
                        + "' -ErrorAction SilentlyContinue; ";
                promptExpr = "$__p";
            }
            catch { promptExpr = null; }   // temp write failed -> fall back to inline
        }
        if (promptExpr == null) promptExpr = "'" + escaped.Replace("'", "''") + "'";

        // If the launcher itself was started from inside a Claude Code session (or
        // any process that was), the new tab inherits that session's environment:
        // CLAUDE_CODE_CHILD_SESSION makes the new claude think it's a nested child
        // (transcript saving off), and the color-suppressing vars Claude Code sets
        // for subprocesses (NO_COLOR/FORCE_COLOR) kill its colored UI. Scrub them
        // in the tab before starting claude so launches are always clean.
        const string envScrub =
            "foreach ($__v in @(Get-ChildItem Env:).Name) { if ($__v -like 'CLAUDE*') "
            + "{ Remove-Item ('Env:' + $__v) -ErrorAction SilentlyContinue } }; "
            + "Remove-Item Env:NO_COLOR, Env:FORCE_COLOR -ErrorAction SilentlyContinue; ";

        // `--` ends claude's option parsing so a prompt that starts with '-'
        // (e.g. a pasted markdown bullet) is taken as the prompt, not a flag.
        string inner = "$Host.UI.RawUI.WindowTitle = '" + name + "'; "
                     + "Set-Location -LiteralPath '" + path + "'; "
                     + envScrub
                     + readCmd
                     + "claude" + model + " -- " + promptExpr;
        string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(inner));

        // Prefer Windows Terminal (named, suppressed-title tab); fall back to PowerShell.
        try
        {
            var psi = new ProcessStartInfo("wt.exe",
                "-w 0 new-tab --title \"" + WinArgInner(title) + "\" --suppressApplicationTitle "
                + "-d \"" + a.Path + "\" "
                + "powershell.exe -NoExit -ExecutionPolicy Bypass -EncodedCommand " + enc)
            { UseShellExecute = true };
            Process.Start(psi);
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoExit -ExecutionPolicy Bypass -EncodedCommand " + enc)
                { UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't launch " + a.Name + ":\n" + ex.Message, "Launch error");
            }
        }
    }
}

// Dark "futuristic" browser for one project folder, opened by a tile's 📁 button.
// Two modes, toggled by the pills in the header:
//   FOLDER   — the normal directory tree. Lazy-loaded (children are read only when
//              a node expands) so giant trees like node_modules can't stall startup.
//   MODIFIED — every file in the project, flat, newest-modified first, with the
//              noise dirs (.git, node_modules, bin, obj, …) skipped and a hard cap
//              so a huge repo can't hang the scan.
// Double-clicking a file in either mode opens it with its default app.
class FolderViewerForm : Form
{
    static readonly HashSet<string> SkipDirs = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", ".vs", "__pycache__", "bin", "obj", ".idea" };
    const int MaxFlatFiles = 2000;

    static readonly Color DirColor  = ColorTranslator.FromHtml("#38BDF8");
    static readonly Color FileColor = ColorTranslator.FromHtml("#CBD5E1");
    static readonly Color DimColor  = ColorTranslator.FromHtml("#64748B");

    readonly string root;
    TreeView tree;
    ListView list;
    Label folderModeBtn, modifiedModeBtn, status;
    bool flatLoaded;   // MODIFIED scans the disk once, on first switch

    // right-hand "editor" pane (VS Code style: explorer left, content right)
    RichTextBox code;
    Label previewTitle, previewMeta;
    string previewPath;   // file currently shown; null = nothing selected
    string previewText;   // shown file's text (no truncation note); null = binary/none
    const int MaxPreviewBytes = 2 * 1024 * 1024;   // preview cap; bigger files truncate

    public FolderViewerForm(AppEntry app)
    {
        root = app.Path;
        Text = app.Name + " — files";
        BackColor = ColorTranslator.FromHtml("#0A0F1E");
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1080, 640);
        MinimumSize = new Size(640, 400);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // header (width set before anchored children — same rule as the main form)
        var header = new Panel { Dock = DockStyle.Top, Height = 64, Width = ClientSize.Width,
            BackColor = ColorTranslator.FromHtml("#0D1526") };
        var caption = new Label {
            Text = "◇ PROJECT EXPLORER", AutoSize = true, Location = new Point(20, 10),
            ForeColor = ColorTranslator.FromHtml("#38BDF8"),
            Font = new Font("Segoe UI", 8F, FontStyle.Bold), BackColor = Color.Transparent };
        var title = new Label {
            Text = app.Name, AutoSize = true, Location = new Point(18, 26),
            ForeColor = ColorTranslator.FromHtml("#F8FAFC"),
            Font = new Font("Segoe UI", 14F, FontStyle.Bold), BackColor = Color.Transparent };

        folderModeBtn = MakeModeButton("FOLDER", new Point(ClientSize.Width - 232, 18));
        modifiedModeBtn = MakeModeButton("MODIFIED", new Point(ClientSize.Width - 128, 18));
        folderModeBtn.Click += (s, e) => SetMode(false);
        modifiedModeBtn.Click += (s, e) => SetMode(true);

        header.Controls.Add(caption);
        header.Controls.Add(title);
        header.Controls.Add(folderModeBtn);
        header.Controls.Add(modifiedModeBtn);

        // status strip along the bottom: path / scan summary / open hint
        status = new Label {
            Dock = DockStyle.Bottom, Height = 28, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(20, 0, 0, 0),
            BackColor = ColorTranslator.FromHtml("#0D1526"), ForeColor = DimColor,
            Font = new Font("Segoe UI", 8.5F) };

        // FOLDER mode: lazy tree
        tree = new TreeView {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
            BackColor = ColorTranslator.FromHtml("#0D1526"), ForeColor = FileColor,
            ShowLines = false, FullRowSelect = true, HideSelection = false,
            ItemHeight = 24, Indent = 20, Font = new Font("Segoe UI", 10F) };
        tree.BeforeExpand += (s, e) => PopulateChildren(e.Node);
        tree.AfterSelect += (s, e) => {
            var f = e.Node.Tag as FileInfo;
            if (f != null) Preview(f.FullName);
        };
        tree.NodeMouseDoubleClick += (s, e) => {
            var f = e.Node.Tag as FileInfo;
            if (f != null) OpenFile(f.FullName);
        };

        // MODIFIED mode: flat list, newest first
        list = new ListView {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            BorderStyle = BorderStyle.None, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = ColorTranslator.FromHtml("#0D1526"), ForeColor = FileColor,
            Font = new Font("Segoe UI", 9.75F), Visible = false };
        list.Columns.Add("FILE", 170);
        list.Columns.Add("IN FOLDER", 150);
        list.Columns.Add("MODIFIED", 110);
        list.Columns.Add("SIZE", 70, HorizontalAlignment.Right);
        list.SelectedIndexChanged += (s, e) => {
            if (list.SelectedItems.Count > 0)
                Preview((string)list.SelectedItems[0].Tag);
        };
        list.ItemActivate += (s, e) => {
            if (list.SelectedItems.Count > 0)
                OpenFile((string)list.SelectedItems[0].Tag);
        };

        // VS Code layout: explorer (tree/list) on the left, file content on the
        // right, draggable splitter between them.
        var split = new SplitContainer {
            Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
            BackColor = BackColor, SplitterWidth = 6 };
        split.Panel1.BackColor = BackColor;
        split.Panel1.Padding = new Padding(14, 12, 0, 12);
        split.Panel2.BackColor = BackColor;
        split.Panel2.Padding = new Padding(0, 12, 14, 12);
        split.Panel1.Controls.Add(tree);
        split.Panel1.Controls.Add(list);

        // right pane: filename strip on top of the read-only code view
        var fileHeader = new Panel { Dock = DockStyle.Top, Height = 52,
            BackColor = ColorTranslator.FromHtml("#0A1428") };
        previewTitle = new Label {
            Text = "No file selected", AutoSize = true, Location = new Point(16, 8),
            ForeColor = ColorTranslator.FromHtml("#F8FAFC"),
            Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
            BackColor = Color.Transparent };
        previewMeta = new Label {
            Text = "Click a file on the left to view it here.", AutoSize = true,
            Location = new Point(17, 30), ForeColor = DimColor,
            Font = new Font("Segoe UI", 8F), BackColor = Color.Transparent };
        var openBtn = new Label {
            Text = "OPEN ↗", AutoSize = false, Size = new Size(72, 26),
            TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            ForeColor = Color.White, BackColor = ColorTranslator.FromHtml("#0891B2"),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        LauncherForm.RoundCorners(openBtn, 13);
        openBtn.Click += (s, e) => { if (previewPath != null) OpenFile(previewPath); };

        // Copy the viewed file's full text to the clipboard (flashes ✓ COPIED).
        var copyBtn = new Label {
            Text = "⧉ COPY", AutoSize = false, Size = new Size(72, 26),
            TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            ForeColor = ColorTranslator.FromHtml("#E2E8F0"),
            BackColor = ColorTranslator.FromHtml("#1E293B"),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        LauncherForm.RoundCorners(copyBtn, 13);
        var copyReset = new Timer { Interval = 1200 };
        copyReset.Tick += (s, e) => { copyReset.Stop(); copyBtn.Text = "⧉ COPY"; };
        copyBtn.Click += (s, e) => {
            if (string.IsNullOrEmpty(previewText)) return;
            try
            {
                Clipboard.SetText(previewText);
                copyBtn.Text = "✓ COPIED";
                copyReset.Stop(); copyReset.Start();
            }
            catch { }   // clipboard can be locked by another app; just skip
        };

        fileHeader.Resize += (s, e) => {
            openBtn.Location = new Point(fileHeader.Width - 86, 13);
            copyBtn.Location = new Point(fileHeader.Width - 166, 13);
        };
        fileHeader.Controls.Add(previewTitle);
        fileHeader.Controls.Add(previewMeta);
        fileHeader.Controls.Add(openBtn);
        fileHeader.Controls.Add(copyBtn);

        code = new RichTextBox {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = ColorTranslator.FromHtml("#0B1220"),
            ForeColor = ColorTranslator.FromHtml("#D6E2F5"),
            Font = new Font("Consolas", 10F), WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both, DetectUrls = false };

        split.Panel2.Controls.Add(code);
        split.Panel2.Controls.Add(fileHeader);

        // dock order is reverse of add order: split fills, status bottom, header top
        Controls.Add(split);
        Controls.Add(status);
        Controls.Add(header);
        // sized only once docked — setting these earlier throws (default width 150)
        split.Panel1MinSize = 220;
        split.SplitterDistance = 340;

        var rootNode = MakeDirNode(new DirectoryInfo(root));
        tree.Nodes.Add(rootNode);
        rootNode.Expand();   // triggers BeforeExpand -> first level loads

        SetMode(false);
    }

    Label MakeModeButton(string text, Point at)
    {
        var b = new Label {
            Text = text, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
            Location = at, Size = new Size(96, 28), Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
        LauncherForm.RoundCorners(b, 14);
        return b;
    }

    void SetMode(bool flat)
    {
        list.Visible = flat;
        tree.Visible = !flat;
        StyleModeButton(modifiedModeBtn, flat);
        StyleModeButton(folderModeBtn, !flat);
        if (flat && !flatLoaded) { flatLoaded = true; LoadFlat(); }
        if (!flat) status.Text = root
            + "   ·   click a file to view it, double-click to open it";
    }

    static void StyleModeButton(Label b, bool active)
    {
        b.BackColor = active
            ? ColorTranslator.FromHtml("#0891B2") : ColorTranslator.FromHtml("#1E293B");
        b.ForeColor = active ? Color.White : ColorTranslator.FromHtml("#94A3B8");
    }

    // ---- FOLDER mode ----

    // Dir nodes get a "…" dummy child so the expand glyph shows; the real children
    // are read in PopulateChildren the first time the node opens.
    static TreeNode MakeDirNode(DirectoryInfo d)
    {
        var n = new TreeNode(d.Name) { Tag = d, ForeColor = DirColor };
        n.Nodes.Add(new TreeNode("…"));   // dummy (Tag == null) = not loaded yet
        return n;
    }

    static void PopulateChildren(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Tag != null) return;  // already loaded
        node.Nodes.Clear();
        var di = node.Tag as DirectoryInfo;
        if (di == null) return;
        try
        {
            foreach (var d in di.GetDirectories()
                         .Where(x => (x.Attributes & FileAttributes.Hidden) == 0)
                         .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                node.Nodes.Add(MakeDirNode(d));
            foreach (var f in di.GetFiles()
                         .Where(x => (x.Attributes & FileAttributes.Hidden) == 0)
                         .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                node.Nodes.Add(new TreeNode(f.Name) { Tag = f, ForeColor = FileColor });
        }
        catch
        {
            node.Nodes.Add(new TreeNode("(unreadable)") { Tag = "err", ForeColor = DimColor });
        }
    }

    // ---- MODIFIED mode ----

    void LoadFlat()
    {
        var files = new List<FileInfo>();
        Walk(new DirectoryInfo(root), files);
        var newest = files.OrderByDescending(f => f.LastWriteTime)
                          .Take(MaxFlatFiles).ToList();

        list.BeginUpdate();
        list.Items.Clear();
        var now = DateTime.Now;
        foreach (var f in newest)
        {
            string rel = f.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? f.FullName.Substring(root.Length).TrimStart('\\') : f.FullName;
            string relDir = Path.GetDirectoryName(rel);
            var item = new ListViewItem(new[] {
                f.Name,
                string.IsNullOrEmpty(relDir) ? "." : relDir,
                f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                FormatSize(f.Length) });
            item.Tag = f.FullName;
            // recency glow: <24h cyan, <7d normal, older dim
            var age = now - f.LastWriteTime;
            item.ForeColor = age.TotalHours < 24 ? ColorTranslator.FromHtml("#22D3EE")
                           : age.TotalDays  < 7  ? FileColor : DimColor;
            list.Items.Add(item);
        }
        list.EndUpdate();

        string summary = files.Count + " files · newest first";
        if (files.Count > MaxFlatFiles) summary += " · showing first " + MaxFlatFiles;
        status.Text = summary + " · skips " + string.Join(", ", SkipDirs.ToArray())
            + "   ·   click a file to view it, double-click to open it";
    }

    static void Walk(DirectoryInfo dir, List<FileInfo> acc)
    {
        try
        {
            foreach (var f in dir.GetFiles())
                if ((f.Attributes & FileAttributes.Hidden) == 0) acc.Add(f);
            foreach (var d in dir.GetDirectories())
                if ((d.Attributes & FileAttributes.Hidden) == 0 && !SkipDirs.Contains(d.Name))
                    Walk(d, acc);
        }
        catch { }   // unreadable subtree: show what we can
    }

    // ---- file preview (right pane) ----

    // Show a file in the editor pane: header gets name + metadata, body gets the
    // text. Binary files (NUL byte in the sample) and >2 MB tails are not dumped
    // into the RichTextBox — binaries get a note, big files a truncated preview.
    void Preview(string fullPath)
    {
        previewPath = fullPath;
        try
        {
            var fi = new FileInfo(fullPath);
            string rel = fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length).TrimStart('\\') : fullPath;
            previewTitle.Text = fi.Name;

            byte[] bytes;
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                int n = (int)Math.Min(fs.Length, MaxPreviewBytes);
                bytes = new byte[n];
                int off = 0;
                while (off < n)
                {
                    int k = fs.Read(bytes, off, n - off);
                    if (k <= 0) break;
                    off += k;
                }
            }

            bool binary = false;
            foreach (byte b in bytes) if (b == 0) { binary = true; break; }
            if (binary)
            {
                code.Text = "";
                previewText = null;
                previewMeta.Text = rel + "  ·  " + FormatSize(fi.Length)
                    + "  ·  binary file — use OPEN ↗";
                return;
            }

            string text;
            using (var sr = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
                text = sr.ReadToEnd();
            bool truncated = fi.Length > bytes.Length;
            previewText = text;   // what ⧉ COPY puts on the clipboard
            code.Text = truncated
                ? text + "\r\n\r\n… (preview truncated at 2 MB — use OPEN ↗ for the full file)"
                : text;
            int lines = text.Length == 0 ? 0 : text.Split('\n').Length;
            previewMeta.Text = rel + "  ·  " + FormatSize(fi.Length) + "  ·  " + lines
                + " lines  ·  " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
        }
        catch (Exception ex)
        {
            code.Text = "";
            previewText = null;
            previewMeta.Text = "Couldn't read the file: " + ex.Message;
        }
    }

    // ---- shared ----

    static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't open the file:\n" + ex.Message, "Open error");
        }
    }

    static string FormatSize(long b)
    {
        if (b < 1024) return b + " B";
        if (b < 1048576) return (b / 1024.0).ToString("0.#") + " KB";
        if (b < 1073741824) return (b / 1048576.0).ToString("0.#") + " MB";
        return (b / 1073741824.0).ToString("0.##") + " GB";
    }
}
