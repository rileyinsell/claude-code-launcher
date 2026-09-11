using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Standalone Dev Launcher. The window belongs to THIS exe (not powershell),
// so it pins to the taskbar as "Dev Launcher" and relaunches correctly.
// Tiles are read from apps.txt next to the exe -> no recompile to edit them.

class AppEntry
{
    public string Name = "", Path = "", Prompt = "";
    public string Provider = "Claude"; // Claude default; Codex selected only from the launch dialog
    public string Model = "";     // claude --model id; empty = CLI default
    public string TabTitle = "";  // terminal tab name override; empty = Name
    public DateTime Modified;
}

// What the launch dialog hands back. Null prompt/model/tab all have defaults.
// LoopMinutes 0 = no loop; ReadClaudeMd prepends a "Read CLAUDE.md first." lead-in.
// Handoff prepends a "use the last available handoff" lead-in (after CLAUDE.md).
// SimpleComm prepends an "always report clearly and simply, like to an executive" rule.
class LaunchOptions
{
    public string Prompt = "", Provider = "Claude", Model = "", TabTitle = "";
    public bool ReadClaudeMd;
    public bool Handoff;
    public bool SimpleComm;
    public int LoopMinutes;
}

// Tiny .env reader (KEY=VALUE, # comments, optional surrounding quotes). Keeps
// environment-specific values — repo path, Azure ids, vault name — out of the
// committed source. Reads .env next to the exe once, lazily. Missing file / key
// falls back to the supplied default, so the app still runs without a .env.
static class Env
{
    static Dictionary<string, string> map;

    static void Load()
    {
        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(
                Path.GetDirectoryName(Application.ExecutablePath), ".env");
            if (!File.Exists(path)) return;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                if (val.Length >= 2 &&
                    ((val[0] == '"' && val[val.Length - 1] == '"') ||
                     (val[0] == '\'' && val[val.Length - 1] == '\'')))
                    val = val.Substring(1, val.Length - 2);
                map[key] = val;
            }
        }
        catch { map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    public static string Get(string key, string fallback)
    {
        if (map == null) Load();
        string v;
        return map.TryGetValue(key, out v) && v.Length > 0 ? v : fallback;
    }
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
    Dictionary<string, string> accounts;  // folder path -> claude CLI command (accounts.txt)
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

        // Token manager: gold 🔑 button left of the view toggle. Opens a dialog
        // that GETs a named secret from the production Key Vault and SETs new
        // values back into it via the az CLI.
        var keyBtn = MakeViewButton("🔑", new Point(ClientSize.Width - 352, 20));
        keyBtn.BackColor = ColorTranslator.FromHtml("#CA8A04");
        keyBtn.ForeColor = Color.White;
        tip.SetToolTip(keyBtn, "Token manager — GET / SET Key Vault secrets");
        keyBtn.Click += (s, e) => new SecretGrabberForm().Show(this);
        header.Controls.Add(keyBtn);

        // Logic Apps launcher: azure-blue 🧩 button left of the key. Opens a
        // searchable list of every Standard Logic App workflow; clicking a name
        // opens its designer in the Azure portal.
        var logicBtn = MakeViewButton("🧩", new Point(ClientSize.Width - 392, 20));
        logicBtn.BackColor = ColorTranslator.FromHtml("#2563EB");
        logicBtn.ForeColor = Color.White;
        tip.SetToolTip(logicBtn, "Logic Apps — open a workflow in the Azure portal");
        logicBtn.Click += (s, e) => new LogicAppsForm().Show(this);
        header.Controls.Add(logicBtn);

        // Databricks Secrets browser: red 🧱 button left of the Logic Apps tile.
        // Opens a searchable list of every Databricks secret scope/key; clicking
        // one GETs its value (masked, reveal + copy) via the Databricks REST API.
        var dbxBtn = MakeViewButton("🧱", new Point(ClientSize.Width - 432, 20));
        dbxBtn.BackColor = ColorTranslator.FromHtml("#EE3D2C");
        dbxBtn.ForeColor = Color.White;
        tip.SetToolTip(dbxBtn, "Databricks Secrets — search scopes/keys and GET a value");
        dbxBtn.Click += (s, e) => new DatabricksSecretsForm().Show(this);
        header.Controls.Add(dbxBtn);

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
        accounts = LoadAccounts();
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
            Location = new Point(264, 0), Size = new Size(row.Width - 264 - 258, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                   | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = Color.Transparent, Cursor = Cursors.Hand };
        var mod = new Label {
            Text = app.Modified == DateTime.MinValue
                ? "" : app.Modified.ToString("yyyy-MM-dd HH:mm"),
            ForeColor = ColorTranslator.FromHtml("#64748B"),
            Font = new Font("Consolas", 8.5F), AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(row.Width - 254, 0), Size = new Size(114, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.Transparent, Cursor = Cursors.Hand };

        var acctBtn = MakeAccountButton(app, new Point(row.Width - 132, 10));
        var starBtn = MakeStarButton(app, new Point(row.Width - 100, 10));
        var promptBtn = MakeActionButton("✎", new Point(row.Width - 68, 10));
        tip.SetToolTip(promptBtn, "Launch with a custom prompt…");
        promptBtn.Click += (s, e) => LaunchWithPrompt(app);
        var folderBtn = MakeActionButton("📁", new Point(row.Width - 36, 10));
        tip.SetToolTip(folderBtn, "Browse project files…");
        folderBtn.Click += (s, e) => new FolderViewerForm(app).Show(this);
        acctBtn.Anchor = starBtn.Anchor = promptBtn.Anchor = folderBtn.Anchor =
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
        row.Controls.Add(acctBtn);
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

        var acctBtn = MakeAccountButton(app, new Point(64, 66));
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
        tile.Controls.Add(acctBtn);
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

    // ---- per-project Claude account, persisted in accounts.txt next to the exe ----
    //
    // Which CLI a tile launches. `claude` is the default account; `claudeba` is a
    // PowerShell profile function that points CLAUDE_CONFIG_DIR at the second
    // account and adds --dangerously-skip-permissions (see the envScrub note in
    // Launch()). The choice is per project, keyed by folder path, and only the
    // non-default picks need to be stored — a project with no entry launches
    // `claude`, unless it matches a legacy hardcoded name (kept as its default).
    const string DefaultCli = "claude";
    const string SecondCli  = "claudeba";

    static string AccountsFile()
    {
        return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "accounts.txt");
    }

    // path|cli per line. Ignores blanks / comments / malformed lines so a hand-edit
    // can't block startup.
    static Dictionary<string, string> LoadAccounts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string file = AccountsFile();
            if (!File.Exists(file)) return map;
            foreach (var line in File.ReadAllLines(file))
            {
                var s = line.Trim();
                if (s.Length == 0 || s[0] == '#') continue;
                int bar = s.IndexOf('|');
                if (bar <= 0) continue;
                string path = s.Substring(0, bar).Trim();
                string cli = s.Substring(bar + 1).Trim();
                if (path.Length > 0 && cli.Length > 0) map[path] = cli;
            }
        }
        catch { }   // a broken accounts.txt must never block startup
        return map;
    }

    void SaveAccounts()
    {
        try
        {
            var lines = new List<string>();
            foreach (var kv in accounts) lines.Add(kv.Key + "|" + kv.Value);
            File.WriteAllLines(AccountsFile(), lines.ToArray());
        }
        catch { }   // account overrides are a nicety; never crash over them
    }

    // Effective CLI command for a project: an explicit accounts.txt entry wins;
    // otherwise the legacy hardcoded names default to the second account; otherwise
    // the default `claude`.
    string AccountFor(AppEntry app)
    {
        string cli;
        if (accounts.TryGetValue(app.Path, out cli) && cli.Length > 0) return cli;
        if (string.Equals(app.Name, "mixotrophic", StringComparison.OrdinalIgnoreCase)
            || string.Equals(app.Name, "rileys-orchestrator", StringComparison.OrdinalIgnoreCase))
            return SecondCli;
        return DefaultCli;
    }

    // Flip a project between the two accounts and persist immediately. The explicit
    // choice always wins over the legacy default, so we store it either way.
    string ToggleAccount(AppEntry app)
    {
        string next = AccountFor(app) == SecondCli ? DefaultCli : SecondCli;
        accounts[app.Path] = next;
        SaveAccounts();
        return next;
    }

    static string AccountLabel(string cli) { return cli == SecondCli ? "BA" : "A"; }

    // Default account reads understated; the second account glows amber so a tile
    // launching under it is visible at a glance.
    static void StyleAccountButton(Label b, string cli)
    {
        b.ForeColor = cli == SecondCli
            ? ColorTranslator.FromHtml("#FBBF24") : ColorTranslator.FromHtml("#94A3B8");
    }

    // Account toggle: switches which claude CLI (account) this project launches.
    Label MakeAccountButton(AppEntry app, Point at)
    {
        string cli = AccountFor(app);
        var b = MakeActionButton(AccountLabel(cli), at);
        b.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);   // fit "BA" in the 28px pad
        StyleAccountButton(b, cli);
        tip.SetToolTip(b, "Account: " + cli + "  (click to switch account)");
        b.Click += (s, e) => {
            string next = ToggleAccount(app);
            b.Text = AccountLabel(next);
            StyleAccountButton(b, next);
            tip.SetToolTip(b, "Account: " + next + "  (click to switch account)");
        };
        return b;
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
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
    internal const int EM_SETCUEBANNER = 0x1501;   // native textbox placeholder text

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

        // Lead-ins are prepended to the prompt TEXT (last one added ends up first),
        // and the /loop prefix goes in FRONT of everything — claude only parses a
        // slash command at position 0. Desired final order is:
        //   /loop <N>m  Read CLAUDE.md first.  Use the last available handoff…  <Fable rule>  <prompt>
        // so prepend Fable rule first, then handoff, then CLAUDE.md, then /loop
        // (reverse of display). (all instructions ride inside the looped prompt and survive.)
        // When Fable is the picked model, prepend the orchestration rule so Fable
        // plans + delegates to Opus rather than implementing everything itself.
        // Prepended first so it sits directly in front of the task prompt.
        if (opts.Provider == "Claude"
            && (opts.Model == "claude-fable-5" || opts.Model == "claude-fable-5-1"))
            prompt = FableOrchestratorPreamble + prompt;
        if (opts.Provider == "Codex" && opts.Model == "gpt-6-astra")
            prompt = AstraOrchestratorPreamble + prompt;
        // Executive-style communication rule. Sits after CLAUDE.md/handoff and
        // before the Fable rule in the final text (prepended here, before those).
        if (opts.SimpleComm)
            prompt = ExecCommPreamble + prompt;
        if (opts.Handoff)
            prompt = "Use the last available handoff to catch up on where things left off. " + prompt;
        // Skip the CLAUDE.md prepend if the prompt already mentions CLAUDE.md (the
        // default prompts do) so it doesn't stutter.
        if (opts.ReadClaudeMd
            && prompt.IndexOf("CLAUDE.md", StringComparison.OrdinalIgnoreCase) < 0)
            prompt = "Read CLAUDE.md first. " + prompt;
        if (opts.LoopMinutes > 0)
            prompt = "/loop " + opts.LoopMinutes + "m " + prompt;

        var oneOff = new AppEntry {
            Name = app.Name, Path = app.Path, Prompt = prompt,
            Provider = opts.Provider, Model = opts.Model, TabTitle = opts.TabTitle };
        Launch(oneOff);
    }

    static readonly string[] ProviderLabels = { "Claude", "Codex" };

    // Claude model choices offered in the launch dialog. Labels are what's shown;
    // ids are passed to `claude --model`. Empty id = no flag (CLI default).
    static readonly string[] ClaudeModelLabels = {
        "Default model", "Opus 5", "Opus 4.8", "Fable 5.1", "Fable 5", "Sonnet 5", "Haiku 4.5" };
    static readonly string[] ClaudeModelIds = {
        "", "claude-opus-5", "claude-opus-4-8", "claude-fable-5-1", "claude-fable-5", "claude-sonnet-5", "claude-haiku-4-5" };

    // Codex model choices verified against Codex CLI 0.153.2 (`codex debug models`)
    // and current OpenAI Codex docs. ids are passed to `codex -m`.
    static readonly string[] CodexModelLabels = {
        "Astra", "5.6 Sol", "5.6 Terra", "5.6 Luna", "5.3 Codex Spark", "5.5" };
    static readonly string[] CodexModelIds = {
        "gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.3-codex-spark", "gpt-5.5" };

    // Prepended to the prompt when Fable is the selected model (see LaunchWithPrompt).
    // Fable runs as orchestrator/planner/reviewer and delegates real work to Opus.
    const string FableOrchestratorPreamble =
        "You are running as Fable, the orchestrator, planner, and final reviewer for this task — " +
        "not the primary implementation agent. First understand the request, inspect the relevant " +
        "context, and form the overall plan. Then break the work into well-defined sub-tasks and " +
        "delegate substantive implementation, investigation, coding, testing, and analysis to Opus " +
        "sub-agents, giving each enough context, requirements, constraints, and acceptance criteria " +
        "to finish independently. Delegate independent workstreams in parallel when it's safe to do " +
        "so. Use Sonnet only for small, low-risk, mechanical sub-tasks — never for complex " +
        "implementation, architectural decisions, hard debugging, or work needing deep reasoning. " +
        "Spend your own intelligence on planning, judgment, integration, and quality control rather " +
        "than large implementation you can delegate. Review and integrate what sub-agents return " +
        "instead of accepting it blindly; if delegated work is incomplete or wrong, send it back or " +
        "delegate a fresh agent rather than lowering the bar. Run or delegate the necessary tests and " +
        "verification, then do a final review of the combined work to confirm it satisfies the " +
        "original request and introduces no regressions. Your actual task: ";

    // Prepended to the prompt when Astra is selected for Codex. Astra keeps the
    // main-thread judgment role and delegates substantial parallel work to
    // sub-agents through Codex's native multi-agent tooling when useful.
    const string AstraOrchestratorPreamble =
        "You are running as Astra, the orchestrator, planner, and final reviewer for this task. " +
        "Use your strongest judgment on the main thread to understand the request, inspect the " +
        "relevant context, and form the overall plan. Break substantial work into well-defined " +
        "sub-tasks and delegate implementation, investigation, coding, testing, and analysis to " +
        "Codex sub-agents when that will improve speed or quality. Give each sub-agent enough " +
        "context, requirements, constraints, and acceptance criteria to finish independently. " +
        "Delegate independent workstreams in parallel when it is safe to do so. Keep small, " +
        "low-risk, mechanical tasks on the main thread when delegation would add overhead. Review " +
        "and integrate what sub-agents return instead of accepting it blindly; if delegated work " +
        "is incomplete or wrong, send it back or delegate a fresh agent. Run or delegate the " +
        "necessary tests and verification, then do a final review of the combined work to confirm " +
        "it satisfies the original request and introduces no regressions. Your actual task: ";

    // Prepended to the prompt when the "Simple com" toggle is on (default). Instructs
    // every session to report to Riley the way he approved: plain-English verdict first,
    // short steps, jargon translated inline, plain hyphens only. Kept short but explicit.
    const string ExecCommPreamble =
        "In this whole session, communicate with me (Riley) clearly and simply, the way you " +
        "would report to a busy executive, with no jargon walls. For every report: open with one " +
        "bold plain-English verdict sentence, then short numbered or lettered steps; translate " +
        "any technical term inline in parentheses; put costs, thresholds, and status in the " +
        "sentence itself rather than an appendix; and close with a one-line \"Bottom line:\". " +
        "Use plain hyphens only, never em or en dashes, in anything I read. ";

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
    internal static Label SectionLabel(string text, int x, int y)
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
            dlg.ClientSize = new Size(640, 522);
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

            var providerLabel = SectionLabel("PROVIDER", 24, 70);
            var providerBox = new ComboBox {
                Location = new Point(24, 90), Size = new Size(134, 30),
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = fieldBack, ForeColor = fieldFore,
                Font = new Font("Segoe UI", 10.5F) };
            providerBox.Items.AddRange(ProviderLabels);
            providerBox.SelectedIndex = 0;

            var modelLabel = SectionLabel("MODEL", 176, 70);
            var modelBox = new ComboBox {
                Location = new Point(176, 90), Size = new Size(128, 30),
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = fieldBack, ForeColor = fieldFore,
                Font = new Font("Segoe UI", 10.5F) };

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

            // "Simple com" — on by default. Adds an executive-style communication rule
            // (ExecCommPreamble) so every session reports to Riley clearly and simply.
            var simpleCheck = new CheckBox {
                Text = "Simple com", AutoSize = true, Checked = true,
                Location = new Point(452, 158),
                ForeColor = fieldFore, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5F), Cursor = Cursors.Hand };

            var handoffCheck = new CheckBox {
                Text = "Handoff", AutoSize = true, Checked = false,
                Location = new Point(452, 180),
                ForeColor = fieldFore, BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5F), Cursor = Cursors.Hand };

            Action refreshModels = () => {
                bool codex = providerBox.SelectedItem != null
                    && providerBox.SelectedItem.ToString() == "Codex";
                modelBox.BeginUpdate();
                modelBox.Items.Clear();
                modelBox.Items.AddRange(codex ? CodexModelLabels : ClaudeModelLabels);
                modelBox.SelectedIndex = 0;
                modelBox.EndUpdate();
                loopCheck.Enabled = !codex;
                loopBox.Enabled = !codex && loopCheck.Checked;
                minLabel.Enabled = !codex;
            };
            providerBox.SelectedIndexChanged += (s, e) => refreshModels();
            refreshModels();

            var promptLabel = SectionLabel("INITIAL PROMPT", 24, 194);
            var box = new TextBox {
                Location = new Point(24, 214), Size = new Size(592, 230),
                Multiline = true, AcceptsReturn = true, WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                // 0 = no length cap (a multiline TextBox otherwise defaults to 32767
                // chars, which silently truncates a long pasted prompt). Prompts too
                // big for the command line already ride a temp file — see Launch().
                MaxLength = 0,
                BackColor = ColorTranslator.FromHtml("#0D1526"), ForeColor = fieldFore,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 10.5F) };

            var hint = new Label {
                Text = "Leave the prompt empty to launch with this project's default prompt.",
                ForeColor = ColorTranslator.FromHtml("#64748B"),
                Font = new Font("Segoe UI", 8.5F), AutoSize = true,
                Location = new Point(24, 452), BackColor = Color.Transparent };

            var start = MakeDialogButton("▶  START", ColorTranslator.FromHtml("#0891B2"),
                DialogResult.OK);
            start.Location = new Point(386, 474); start.Size = new Size(128, 36);
            var cancel = MakeDialogButton("CANCEL", ColorTranslator.FromHtml("#1E293B"),
                DialogResult.Cancel);
            cancel.Location = new Point(524, 474); cancel.Size = new Size(92, 36);

            dlg.Controls.Add(header);
            dlg.Controls.Add(divider);
            dlg.Controls.Add(providerLabel);
            dlg.Controls.Add(providerBox);
            dlg.Controls.Add(modelLabel);
            dlg.Controls.Add(modelBox);
            dlg.Controls.Add(tabLabel);
            dlg.Controls.Add(tabBox);
            dlg.Controls.Add(loopCheck);
            dlg.Controls.Add(loopBox);
            dlg.Controls.Add(minLabel);
            dlg.Controls.Add(claudeMdCheck);
            dlg.Controls.Add(simpleCheck);
            dlg.Controls.Add(handoffCheck);
            dlg.Controls.Add(promptLabel);
            dlg.Controls.Add(box);
            dlg.Controls.Add(hint);
            dlg.Controls.Add(start);
            dlg.Controls.Add(cancel);
            // Enter inside the box makes a newline (multiline); Start is clicked explicitly.
            dlg.CancelButton = cancel;
            dlg.ActiveControl = box;   // start typing the prompt immediately

            if (dlg.ShowDialog() != DialogResult.OK) return null;
            bool pickedCodex = providerBox.SelectedItem != null
                && providerBox.SelectedItem.ToString() == "Codex";
            return new LaunchOptions {
                Prompt = box.Text.Trim(),
                Provider = pickedCodex ? "Codex" : "Claude",
                Model = pickedCodex
                    ? CodexModelIds[modelBox.SelectedIndex]
                    : ClaudeModelIds[modelBox.SelectedIndex],
                TabTitle = tabBox.Text.Trim(),
                ReadClaudeMd = claudeMdCheck.Checked,
                Handoff = handoffCheck.Checked,
                SimpleComm = simpleCheck.Checked,
                LoopMinutes = !pickedCodex && loopCheck.Checked ? (int)loopBox.Value : 0 };
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

    // Above this many escaped chars a prompt no longer fits claude.exe's OWN Windows
    // command line (~32767 total for CreateProcess), which fails with Win32 206
    // ("filename or extension is too long") when claude/claudeba is finally invoked.
    // Kept a bit under the hard cap so the profile function's own flags still fit.
    // Oversize prompts are handed to claude through a file instead (see Launch).
    const int CmdLinePromptMax = 31000;

    string LaunchCli(AppEntry a)
    {
        bool codex = string.Equals(a.Provider, "Codex", StringComparison.OrdinalIgnoreCase);
        if (codex)
        {
            string codexModel = (a.Model != null && a.Model.Length > 0)
                ? " -m '" + PsSingleQuote(a.Model) + "'" : "";
            return "codex --dangerously-bypass-approvals-and-sandbox"
                 + codexModel;
        }

        string claudeModel = (a.Model != null && a.Model.Length > 0)
            ? " --model '" + PsSingleQuote(a.Model) + "'" : "";
        return AccountFor(a) + claudeModel;
    }

    void Launch(AppEntry a)
    {
        RecordUsage(a.Name);

        // Tab/window title: the custom name from the launch dialog, else the app name.
        string title = (a.TabTitle != null && a.TabTitle.Length > 0) ? a.TabTitle : a.Name;

        // PowerShell command the new tab runs: name the tab, cd in, start the selected agent.
        string name = PsSingleQuote(title);
        string path = PsSingleQuote(a.Path);

        // Short prompts ride inline (as before). Long ones are written to a temp
        // file holding the WinArgInner-escaped text: PowerShell passes a variable
        // to a native exe without escaping embedded double quotes, so the argv
        // escaping must already be baked into the text — the exact same trick as
        // PsPromptArg, just without the single-quote layer (no PS literal involved).
        string raw = NormalizeQuotes(a.Prompt ?? "");
        string escaped = WinArgInner(raw);
        string promptExpr = null, readCmd = "";

        // If the prompt is too large to ride claude.exe's own command line, write it
        // to a .md file in the project folder and hand claude a short note pointing at
        // it (the -EncodedCommand temp-file path below only shrinks the OUTER
        // PowerShell command, not the argument claude finally receives). Small prompts
        // are still passed inline, verbatim.
        if (escaped.Length > CmdLinePromptMax)
        {
            try
            {
                string pf = Path.Combine(a.Path,
                    ".devlauncher-prompt-" + Guid.NewGuid().ToString("N") + ".md");
                File.WriteAllText(pf, raw, new UTF8Encoding(false));
                string note = "Your initial prompt was too long to pass on the command "
                    + "line, so it was saved next to you. Read the file '"
                    + Path.GetFileName(pf) + "' in the current directory in full — its "
                    + "contents are your instructions for this session — then follow them "
                    + "and delete that file.";
                promptExpr = "'" + WinArgInner(note).Replace("'", "''") + "'";
            }
            catch { promptExpr = null; }   // write failed -> try the normal paths below
        }

        if (promptExpr == null && escaped.Length > InlinePromptMax)
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

        // Which Claude account/CLI this project launches under — the per-tile
        // account toggle (accounts.txt), falling back to the legacy default for a
        // couple of known names. `claudeba` is a PowerShell profile function that
        // points CLAUDE_CONFIG_DIR at the second account and adds
        // --dangerously-skip-permissions. It sets its own config dir when invoked,
        // which is AFTER envScrub wipes CLAUDE* vars in the tab, so the account
        // switch survives. See AccountFor / MakeAccountButton. Codex launches use
        // the same terminal host/profile, but always bypass approvals and sandboxing.
        string cli = LaunchCli(a);

        // `--` ends the agent's option parsing so a prompt that starts with '-'
        // (e.g. a pasted markdown bullet) is taken as the prompt, not a flag.
        string inner = "$Host.UI.RawUI.WindowTitle = '" + name + "'; "
                     + "Set-Location -LiteralPath '" + path + "'; "
                     + envScrub
                     + readCmd
                     + cli + " -- " + promptExpr;
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
    bool flatMode;                 // which pill is active (a search overlays either)
    TextBox findBox;               // header search across all files + folders
    List<FileSystemInfo> index;    // every file/folder under root, newest first (lazy)
    const int MaxSearchResults = 500;

    // right-hand "editor" pane (VS Code style: explorer left, content right)
    RichTextBox code;
    Label previewTitle, previewMeta;
    string previewPath;   // file (or selected folder) currently shown; null = nothing selected
    string previewText;   // shown file's text (no truncation note); null = binary/none
    const int MaxPreviewBytes = 2 * 1024 * 1024;   // preview cap; bigger files truncate

    public FolderViewerForm(AppEntry app)
    {
        root = app.Path;
        Text = app.Name + " — files";
        BackColor = ColorTranslator.FromHtml("#0A0F1E");
        StartPosition = FormStartPosition.CenterParent;
        // Open big, like the main window: most of the working area, capped.
        var wa = Screen.PrimaryScreen.WorkingArea;
        ClientSize = new Size(Math.Min(1400, wa.Width - 60), Math.Min(880, wa.Height - 90));
        MinimumSize = new Size(720, 420);
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

        // Search across the whole project: matches file AND folder names, live as
        // you type, results in the list pane. Esc (or the pills) restores the mode.
        var findWrap = new Panel {
            Location = new Point(ClientSize.Width - 468, 18), Size = new Size(220, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = ColorTranslator.FromHtml("#164E63") };
        LauncherForm.RoundCorners(findWrap, 15);
        var findInner = new Panel {
            Location = new Point(1, 1), Size = new Size(218, 28),
            BackColor = ColorTranslator.FromHtml("#0D1526") };
        LauncherForm.RoundCorners(findInner, 14);
        findBox = new TextBox {
            Location = new Point(12, 6), Size = new Size(194, 18),
            BackColor = ColorTranslator.FromHtml("#0D1526"),
            ForeColor = ColorTranslator.FromHtml("#F8FAFC"),
            BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9.5F) };
        findBox.TextChanged += (s, e) => {
            string q = findBox.Text.Trim();
            if (q.Length == 0) SetMode(flatMode);   // restore the underlying mode
            else RunSearch(q);
        };
        findBox.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Escape) { findBox.Text = ""; e.SuppressKeyPress = true; }
        };
        findBox.HandleCreated += (s, e) => LauncherForm.SendMessage(
            findBox.Handle, LauncherForm.EM_SETCUEBANNER, (IntPtr)1, "Search files & folders…");
        findInner.Controls.Add(findBox);
        findWrap.Controls.Add(findInner);

        // Ctrl+F jumps to the search box from anywhere in the window.
        KeyPreview = true;
        KeyDown += (s, e) => {
            if (e.Control && e.KeyCode == Keys.F) { findBox.Focus(); e.SuppressKeyPress = true; }
        };

        header.Controls.Add(caption);
        header.Controls.Add(title);
        header.Controls.Add(findWrap);
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
        // Items carry FileInfo/DirectoryInfo in Tag (flat list and search results).
        list.SelectedIndexChanged += (s, e) => {
            if (list.SelectedItems.Count == 0) return;
            var f = list.SelectedItems[0].Tag as FileInfo;
            if (f != null) { Preview(f.FullName); return; }
            var d = list.SelectedItems[0].Tag as DirectoryInfo;
            if (d != null)
            {
                previewPath = d.FullName; previewText = null; code.Text = "";
                previewTitle.Text = d.Name;
                previewMeta.Text = RelDir(d.FullName)
                    + "  ·  folder — double-click to open in Explorer";
            }
        };
        list.ItemActivate += (s, e) => {
            if (list.SelectedItems.Count == 0) return;
            var fsi = list.SelectedItems[0].Tag as FileSystemInfo;
            if (fsi != null) OpenFile(fsi.FullName);   // folders open in Explorer
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

        // Copy the viewed file's (or selected folder's) full path to the clipboard.
        var pathBtn = new Label {
            Text = "⧉ PATH", AutoSize = false, Size = new Size(72, 26),
            TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
            ForeColor = ColorTranslator.FromHtml("#E2E8F0"),
            BackColor = ColorTranslator.FromHtml("#1E293B"),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold) };
        LauncherForm.RoundCorners(pathBtn, 13);
        var pathReset = new Timer { Interval = 1200 };
        pathReset.Tick += (s, e) => { pathReset.Stop(); pathBtn.Text = "⧉ PATH"; };
        pathBtn.Click += (s, e) => {
            if (string.IsNullOrEmpty(previewPath)) return;
            try
            {
                Clipboard.SetText(previewPath);
                pathBtn.Text = "✓ COPIED";
                pathReset.Stop(); pathReset.Start();
            }
            catch { }   // clipboard can be locked by another app; just skip
        };

        fileHeader.Resize += (s, e) => {
            openBtn.Location = new Point(fileHeader.Width - 86, 13);
            copyBtn.Location = new Point(fileHeader.Width - 166, 13);
            pathBtn.Location = new Point(fileHeader.Width - 246, 13);
        };
        fileHeader.Controls.Add(previewTitle);
        fileHeader.Controls.Add(previewMeta);
        fileHeader.Controls.Add(openBtn);
        fileHeader.Controls.Add(copyBtn);
        fileHeader.Controls.Add(pathBtn);

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
        flatMode = flat;
        // an active search owns the list pane; clearing the box re-enters here
        if (findBox != null && findBox.Text.Trim().Length > 0) { findBox.Text = ""; return; }
        StyleModeButton(modifiedModeBtn, flat);
        StyleModeButton(folderModeBtn, !flat);
        list.Visible = flat;
        tree.Visible = !flat;
        if (flat) LoadFlat();   // also restores the flat list after a search
        else status.Text = root
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

    // ---- MODIFIED mode + search (both feed off one lazy index) ----

    // One recursive scan, cached for the window's lifetime: every non-hidden file
    // AND folder under root (SkipDirs pruned), sorted newest-modified first.
    void EnsureIndex()
    {
        if (index != null) return;
        index = new List<FileSystemInfo>();
        WalkAll(new DirectoryInfo(root));
        index.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
    }

    void WalkAll(DirectoryInfo dir)
    {
        try
        {
            foreach (var f in dir.GetFiles())
                if ((f.Attributes & FileAttributes.Hidden) == 0) index.Add(f);
            foreach (var d in dir.GetDirectories())
                if ((d.Attributes & FileAttributes.Hidden) == 0 && !SkipDirs.Contains(d.Name))
                { index.Add(d); WalkAll(d); }
        }
        catch { }   // unreadable subtree: show what we can
    }

    string RelDir(string fullName)
    {
        string rel = fullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? fullName.Substring(root.Length).TrimStart('\\') : fullName;
        string d = Path.GetDirectoryName(rel);
        return string.IsNullOrEmpty(d) ? "." : d;
    }

    // recency glow: <24h cyan, <7d normal, older dim
    static Color RecencyColor(DateTime now, DateTime t)
    {
        var age = now - t;
        return age.TotalHours < 24 ? ColorTranslator.FromHtml("#22D3EE")
             : age.TotalDays  < 7  ? FileColor : DimColor;
    }

    void LoadFlat()
    {
        EnsureIndex();
        list.BeginUpdate();
        list.Items.Clear();
        var now = DateTime.Now;
        int total = 0;
        foreach (var e in index)
        {
            var f = e as FileInfo;
            if (f == null) continue;
            total++;
            if (list.Items.Count >= MaxFlatFiles) continue;   // keep counting for the summary
            var item = new ListViewItem(new[] {
                f.Name, RelDir(f.FullName),
                f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), FormatSize(f.Length) });
            item.Tag = f;
            item.ForeColor = RecencyColor(now, f.LastWriteTime);
            list.Items.Add(item);
        }
        list.EndUpdate();

        string summary = total + " files · newest first";
        if (total > MaxFlatFiles) summary += " · showing first " + MaxFlatFiles;
        status.Text = summary + " · skips " + string.Join(", ", SkipDirs.ToArray())
            + "   ·   click a file to view it, double-click to open it";
    }

    // Live name search over files and folders; results land in the list pane
    // (newest first, folders in cyan like the tree).
    void RunSearch(string q)
    {
        EnsureIndex();
        list.BeginUpdate();
        list.Items.Clear();
        var now = DateTime.Now;
        int files = 0, dirs = 0;
        foreach (var e in index)
        {
            if (e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var d = e as DirectoryInfo;
            if (d != null) dirs++; else files++;
            if (list.Items.Count >= MaxSearchResults) continue;   // keep counting
            var item = new ListViewItem(new[] {
                e.Name, RelDir(e.FullName),
                e.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                d != null ? "folder" : FormatSize(((FileInfo)e).Length) });
            item.Tag = e;
            item.ForeColor = d != null ? DirColor : RecencyColor(now, e.LastWriteTime);
            list.Items.Add(item);
        }
        list.EndUpdate();
        tree.Visible = false;
        list.Visible = true;
        StyleModeButton(folderModeBtn, false);
        StyleModeButton(modifiedModeBtn, false);

        int total = files + dirs;
        string txt = total + " matches for \"" + q + "\" (" + files + " files, "
            + dirs + " folders)";
        if (total > MaxSearchResults) txt += " · showing first " + MaxSearchResults;
        status.Text = txt + "   ·   Esc clears";
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

// The 🔑 "secret grabber": type a secret name, and it runs
//   az keyvault secret show --vault-name ins-prod-lg-kv-usw --name <name> --query value -o tsv
// against the Insellerate production Key Vault (see the keyvault-get-secret skill),
// then shows the value MASKED with reveal (👁) and copy (⧉) actions. The value is
// only ever held in memory / put on the clipboard on request — never written to disk.
class SecretGrabberForm : Form
{
    // Production Logic App Key Vault (RBAC mode). Requires an interactive
    // `az login` as a principal with the Key Vault Secrets User/Officer role.
    // Name comes from .env (KEYVAULT_NAME); the literal is only a fallback.
    static readonly string VaultName = Env.Get("KEYVAULT_NAME", "ins-prod-lg-kv-usw");

    TextBox nameBox, valueBox;
    Button getBtn, setBtn, revealBtn, copyBtn, listBtn;
    ListBox secretList;                 // searchable dropdown of secret names
    Label status, countLabel;
    string currentValue;   // last fetched secret, in memory only
    bool revealed;
    // All secret names in the vault (from the last LIST/cache), filtered live by
    // the SECRET NAME box into secretList.
    List<string> secretNames = new List<string>();
    readonly Timer copyReset = new Timer { Interval = 1200 };

    // Cached secret-NAME list (names only, never values) so the vault isn't
    // re-queried on every open. Git-ignored; rebuilt by the ↻ LIST button.
    static string SecretsCacheFile()
    {
        return Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "secrets.txt");
    }

    public SecretGrabberForm()
    {
        Text = "Token Manager";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(600, 560);
        BackColor = ColorTranslator.FromHtml("#0A0F1E");
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Color fieldBack = ColorTranslator.FromHtml("#111C33");
        Color fieldFore = ColorTranslator.FromHtml("#E2E8F0");

        var header = new Label {
            Text = "🔑  TOKEN MANAGER",
            ForeColor = ColorTranslator.FromHtml("#FBBF24"),
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            AutoSize = true, Location = new Point(22, 16), BackColor = Color.Transparent };
        var divider = new Panel {
            Location = new Point(24, 50), Size = new Size(552, 2),
            BackColor = ColorTranslator.FromHtml("#155E75") };

        var vaultLabel = LauncherForm.SectionLabel("VAULT", 24, 64);
        var vaultVal = new Label {
            Text = VaultName, AutoSize = true, Location = new Point(24, 82),
            ForeColor = ColorTranslator.FromHtml("#94A3B8"),
            Font = new Font("Consolas", 10F), BackColor = Color.Transparent };

        // The SECRET NAME box doubles as a search box: type to filter the
        // secretList dropdown below it, click a name to fetch it.
        var nameLabel = LauncherForm.SectionLabel("SECRET NAME  ·  TYPE TO SEARCH", 24, 116);
        nameBox = new TextBox {
            Location = new Point(24, 136), Size = new Size(330, 28),
            BackColor = fieldBack, ForeColor = fieldFore,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5F) };
        nameBox.TextChanged += (s, e) => FilterSecrets();
        nameBox.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter) { DoGet(); e.SuppressKeyPress = true; }
            // Down arrow jumps into the filtered list so you can arrow through it.
            else if (e.KeyCode == Keys.Down && secretList.Items.Count > 0) {
                secretList.SelectedIndex = 0; secretList.Focus();
                e.SuppressKeyPress = true;
            }
        };
        nameBox.HandleCreated += (s, e) =>
            LauncherForm.SendMessage(nameBox.Handle,
                LauncherForm.EM_SETCUEBANNER, (IntPtr)1, "Search secret names…");

        listBtn = FlatButton("↻  LIST", ColorTranslator.FromHtml("#1E293B"),
            new Point(362, 135), new Size(100, 30));
        listBtn.Click += (s, e) => RefreshSecretNames();

        getBtn = FlatButton("▶  GET", ColorTranslator.FromHtml("#0891B2"),
            new Point(476, 135), new Size(100, 30));
        getBtn.Click += (s, e) => DoGet();

        countLabel = new Label {
            AutoSize = true, Location = new Point(24, 170),
            ForeColor = ColorTranslator.FromHtml("#94A3B8"),
            Font = new Font("Segoe UI", 9F), BackColor = Color.Transparent };

        secretList = new ListBox {
            Location = new Point(24, 190), Size = new Size(552, 150),
            BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
            BackColor = ColorTranslator.FromHtml("#0D1526"),
            ForeColor = ColorTranslator.FromHtml("#67E8F9"),
            Font = new Font("Consolas", 10F), Cursor = Cursors.Hand };
        // Single click on a name selects + fetches it (matches the launcher's
        // "click the name to open" feel).
        secretList.MouseClick += (s, e) => {
            int i = secretList.IndexFromPoint(e.Location);
            if (i >= 0 && i < secretList.Items.Count) PickAndGet((string)secretList.Items[i]);
        };
        secretList.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter && secretList.SelectedItem != null) {
                PickAndGet((string)secretList.SelectedItem); e.SuppressKeyPress = true;
            }
        };

        // VALUE is editable now: GET fills it (masked) with the fetched secret,
        // or type/paste a new value and SET writes it back to the vault.
        var valueLabel = LauncherForm.SectionLabel("VALUE  ·  GET FILLS IT, OR TYPE A NEW ONE TO SET", 24, 352);
        valueBox = new TextBox {
            Location = new Point(24, 372), Size = new Size(440, 28),
            UseSystemPasswordChar = true,
            BackColor = fieldBack, ForeColor = fieldFore,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5F) };
        // Editing invalidates the cached "last fetched" value: COPY should copy
        // exactly what's shown, so keep currentValue in sync with the box.
        valueBox.TextChanged += (s, e) => currentValue = valueBox.Text;

        setBtn = FlatButton("⬆  SET", ColorTranslator.FromHtml("#B45309"),
            new Point(476, 371), new Size(100, 30));
        setBtn.Click += (s, e) => DoSet();

        revealBtn = FlatButton("👁", ColorTranslator.FromHtml("#1E293B"),
            new Point(24, 408), new Size(44, 28));
        revealBtn.Click += (s, e) => {
            revealed = !revealed;
            valueBox.UseSystemPasswordChar = !revealed;
            revealBtn.Text = revealed ? "🙈" : "👁";
        };

        copyBtn = FlatButton("⧉  COPY", ColorTranslator.FromHtml("#1E293B"),
            new Point(76, 408), new Size(100, 28));
        copyReset.Tick += (s, e) => { copyReset.Stop(); copyBtn.Text = "⧉  COPY"; };
        copyBtn.Click += (s, e) => {
            if (string.IsNullOrEmpty(currentValue)) return;
            try
            {
                Clipboard.SetText(currentValue);
                copyBtn.Text = "✓  COPIED";
                copyReset.Stop(); copyReset.Start();
            }
            catch { }   // clipboard can be locked by another app; just skip
        };

        status = new Label {
            Text = "Type to search secret names, click one to GET it — or type a "
                 + "value and SET to write it back. Needs an interactive az login "
                 + "with Key Vault Secrets access.",
            AutoSize = false, Location = new Point(24, 448), Size = new Size(552, 96),
            ForeColor = ColorTranslator.FromHtml("#64748B"),
            Font = new Font("Segoe UI", 9F), BackColor = Color.Transparent };

        Controls.Add(header);
        Controls.Add(divider);
        Controls.Add(vaultLabel);
        Controls.Add(vaultVal);
        Controls.Add(nameLabel);
        Controls.Add(nameBox);
        Controls.Add(listBtn);
        Controls.Add(getBtn);
        Controls.Add(countLabel);
        Controls.Add(secretList);
        Controls.Add(valueLabel);
        Controls.Add(valueBox);
        Controls.Add(setBtn);
        Controls.Add(revealBtn);
        Controls.Add(copyBtn);
        Controls.Add(status);
        ActiveControl = nameBox;

        // Load cached secret names; if there are none yet, list once from the vault.
        secretNames = LoadSecretNamesCache();
        if (secretNames.Count > 0)
        {
            FilterSecrets();
            SetStatus("Loaded " + secretNames.Count + " secret names from cache. "
                + "Type to search, click one to GET — ↻ LIST to refresh.",
                ColorTranslator.FromHtml("#38BDF8"));
        }
        else
        {
            RefreshSecretNames();
        }
    }

    // Load the cached secret names (one per line). Values are never cached.
    static List<string> LoadSecretNamesCache()
    {
        try
        {
            if (File.Exists(SecretsCacheFile()))
                return File.ReadAllLines(SecretsCacheFile())
                    .Select(l => l.Trim()).Where(l => l.Length > 0)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
        catch { }
        return new List<string>();
    }

    // Rebuild secretList from secretNames, keeping only names that contain the
    // current query (case-insensitive substring). Updates the count label.
    void FilterSecrets()
    {
        if (secretList == null) return;
        string q = (nameBox.Text ?? "").Trim();
        var matches = (q.Length == 0
            ? secretNames
            : secretNames.Where(n => n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        secretList.BeginUpdate();
        secretList.Items.Clear();
        foreach (var n in matches) secretList.Items.Add(n);
        secretList.EndUpdate();

        if (secretNames.Count == 0)
            countLabel.Text = "";
        else if (q.Length == 0)
            countLabel.Text = secretNames.Count + " secrets";
        else
            countLabel.Text = matches.Count + " of " + secretNames.Count + " secrets";
    }

    // Fill the name box from a clicked/entered list item and fetch it.
    void PickAndGet(string name)
    {
        nameBox.Text = name;   // TextChanged refilters the list to this name
        DoGet();
    }

    // List every secret NAME in the vault (names only, no values) on a background
    // thread, cache them, and repopulate the dropdown.
    void RefreshSecretNames()
    {
        listBtn.Enabled = false; getBtn.Enabled = false; setBtn.Enabled = false;
        listBtn.Text = "…";
        SetStatus("Listing secret names in " + VaultName + " …",
            ColorTranslator.FromHtml("#38BDF8"));

        string args = "/c az keyvault secret list --vault-name " + VaultName
                    + " --query \"[].name\" -o tsv";

        var t = new System.Threading.Thread(() =>
        {
            string outp = "", err = "";
            int code = -1;
            bool timedOut = false;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    outp = p.StandardOutput.ReadToEnd();
                    err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000)) { timedOut = true; try { p.Kill(); } catch { } }
                    else code = p.ExitCode;
                }
            }
            catch (Exception ex) { err = ex.Message; }

            try { BeginInvoke((Action)(() => ListDone(outp, err, code, timedOut))); }
            catch { }   // form closed before the call returned
        }) { IsBackground = true };
        t.Start();
    }

    void ListDone(string outp, string err, int code, bool timedOut)
    {
        listBtn.Enabled = true; getBtn.Enabled = true; setBtn.Enabled = true;
        listBtn.Text = "↻  LIST";

        if (timedOut)
        {
            SetStatus("Timed out after 60s waiting for az. Is the CLI installed and logged in?",
                ColorTranslator.FromHtml("#F87171"));
            return;
        }

        if (code == 0)
        {
            secretNames = (outp ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            try { File.WriteAllLines(SecretsCacheFile(), secretNames.ToArray()); } catch { }
            FilterSecrets();
            SetStatus("✓ Loaded " + secretNames.Count + " secret names from " + VaultName
                + ". Type to search, click one to GET.",
                ColorTranslator.FromHtml("#34D399"));
        }
        else
        {
            string msg = (err ?? "").Trim();
            if (msg.Length == 0) msg = "Couldn't list secrets (exit " + code + ").";
            SetStatus("✗ " + msg, ColorTranslator.FromHtml("#F87171"));
        }
    }

    // Flat dark-theme button (the default WinForms button is black-on-grey and
    // unreadable on these dark dialogs).
    static Button FlatButton(string text, Color back, Point at, Size size)
    {
        var b = new Button {
            Text = text, Location = at, Size = size,
            ForeColor = Color.White, BackColor = back,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Semibold", 9.5F) };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.25f);
        return b;
    }

    void SetStatus(string text, Color color)
    {
        status.ForeColor = color;
        status.Text = text;
    }

    // Kick off the az CLI call on a background thread so the UI never freezes,
    // then marshal the result back with BeginInvoke.
    void DoGet()
    {
        string name = (nameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            SetStatus("Enter a secret name first.", ColorTranslator.FromHtml("#F87171"));
            return;
        }
        // Key Vault secret names are restricted to [0-9a-zA-Z-]. Enforcing that here
        // also makes it safe to pass the name straight through cmd.exe (no injection).
        foreach (char c in name)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z') || c == '-'))
            {
                SetStatus("Invalid name: Key Vault secret names may only contain "
                    + "letters, digits, and hyphens.", ColorTranslator.FromHtml("#F87171"));
                return;
            }

        currentValue = null;
        valueBox.Text = "";
        revealed = false;
        valueBox.UseSystemPasswordChar = true;
        revealBtn.Text = "👁";
        getBtn.Enabled = false; setBtn.Enabled = false;
        getBtn.Text = "…";
        SetStatus("Fetching " + name + " from " + VaultName + " …",
            ColorTranslator.FromHtml("#38BDF8"));

        string args = "/c az keyvault secret show --vault-name " + VaultName
                    + " --name " + name + " --query value -o tsv";

        var t = new System.Threading.Thread(() =>
        {
            string outp = "", err = "";
            int code = -1;
            bool timedOut = false;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    // Secret payloads are tiny, so a sequential read won't deadlock.
                    outp = p.StandardOutput.ReadToEnd();
                    err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000)) { timedOut = true; try { p.Kill(); } catch { } }
                    else code = p.ExitCode;
                }
            }
            catch (Exception ex) { err = ex.Message; }

            try { BeginInvoke((Action)(() => Done(name, outp, err, code, timedOut))); }
            catch { }   // form closed before the call returned
        }) { IsBackground = true };
        t.Start();
    }

    void Done(string name, string outp, string err, int code, bool timedOut)
    {
        getBtn.Enabled = true; setBtn.Enabled = true;
        getBtn.Text = "▶  GET";

        if (timedOut)
        {
            SetStatus("Timed out after 60s waiting for az. Is the CLI installed and logged in?",
                ColorTranslator.FromHtml("#F87171"));
            return;
        }

        string val = (outp ?? "").TrimEnd('\r', '\n');
        if (code == 0 && val.Length > 0)
        {
            currentValue = val;
            valueBox.Text = val;   // stays masked until 👁
            SetStatus("✓ Retrieved \"" + name + "\".  Hidden — click 👁 to reveal, "
                + "⧉ COPY to copy to the clipboard.", ColorTranslator.FromHtml("#34D399"));
        }
        else
        {
            string msg = (err ?? "").Trim();
            if (msg.Length == 0) msg = val.Length == 0 ? "No value returned." : val;
            SetStatus("✗ " + msg, ColorTranslator.FromHtml("#F87171"));
        }
    }

    // Write the VALUE box back to the vault under SECRET NAME (creates the secret
    // or adds a new version). Writing a production secret is destructive and hard
    // to reverse, so confirm first. The value is passed to az via a temp --file
    // (NOT the command line) so arbitrary characters can't break quoting or inject.
    void DoSet()
    {
        string name = (nameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            SetStatus("Enter a secret name first.", ColorTranslator.FromHtml("#F87171"));
            return;
        }
        // Same [0-9a-zA-Z-] rule as GET — keeps the name safe to pass through cmd.exe.
        foreach (char c in name)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z') || c == '-'))
            {
                SetStatus("Invalid name: Key Vault secret names may only contain "
                    + "letters, digits, and hyphens.", ColorTranslator.FromHtml("#F87171"));
                return;
            }

        string value = valueBox.Text ?? "";
        if (value.Length == 0)
        {
            SetStatus("Enter a value to set first.", ColorTranslator.FromHtml("#F87171"));
            return;
        }

        if (MessageBox.Show(
                "Set secret \"" + name + "\" in vault " + VaultName + "?\n\n"
                + "This creates the secret or adds a new version, overwriting the "
                + "value the vault currently returns for this name.",
                "Confirm SET", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
            != DialogResult.OK)
            return;

        getBtn.Enabled = false; setBtn.Enabled = false;
        setBtn.Text = "…";
        SetStatus("Setting " + name + " in " + VaultName + " …",
            ColorTranslator.FromHtml("#38BDF8"));

        // Stage the value in a temp file so az reads it via --file: no shell quoting
        // of the (arbitrary) secret value, so it can't be corrupted or injected.
        string tmp;
        try
        {
            tmp = Path.Combine(Path.GetTempPath(),
                "devlauncher-secret-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(tmp, value, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            getBtn.Enabled = true; setBtn.Enabled = true; setBtn.Text = "⬆  SET";
            SetStatus("✗ Couldn't stage the value: " + ex.Message,
                ColorTranslator.FromHtml("#F87171"));
            return;
        }

        string args = "/c az keyvault secret set --vault-name " + VaultName
                    + " --name " + name + " --file \"" + tmp
                    + "\" --encoding utf-8 --query id -o tsv";

        var t = new System.Threading.Thread(() =>
        {
            string outp = "", err = "";
            int code = -1;
            bool timedOut = false;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    outp = p.StandardOutput.ReadToEnd();
                    err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(60000)) { timedOut = true; try { p.Kill(); } catch { } }
                    else code = p.ExitCode;
                }
            }
            catch (Exception ex) { err = ex.Message; }
            finally { try { File.Delete(tmp); } catch { } }   // never leave the secret on disk

            try { BeginInvoke((Action)(() => SetDone(name, outp, err, code, timedOut))); }
            catch { }   // form closed before the call returned
        }) { IsBackground = true };
        t.Start();
    }

    void SetDone(string name, string outp, string err, int code, bool timedOut)
    {
        getBtn.Enabled = true; setBtn.Enabled = true;
        setBtn.Text = "⬆  SET";

        if (timedOut)
        {
            SetStatus("Timed out after 60s waiting for az. Is the CLI installed and logged in?",
                ColorTranslator.FromHtml("#F87171"));
            return;
        }

        if (code == 0)
        {
            SetStatus("✓ Set \"" + name + "\" in " + VaultName
                + ".  GET it back to confirm the new value.",
                ColorTranslator.FromHtml("#34D399"));
        }
        else
        {
            string msg = (err ?? "").Trim();
            if (msg.Length == 0) msg = "Set failed (exit " + code + ").";
            SetStatus("✗ " + msg, ColorTranslator.FromHtml("#F87171"));
        }
    }
}

// The 🧱 Databricks Secrets browser: lists EVERY secret scope/key in the
// workspace and GETs a value on demand (masked, reveal + copy). Read-only — it
// never writes a secret. Talks to the Databricks REST API directly with a PAT
// from .env (DATABRICKS_HOST + DATABRICKS_TOKEN), so it needs no CLI/login.
//
// Search is client-side over a cached scope/key list (databricks-secrets.txt next
// to the exe), so typing never hits the network — only ↻ LIST (enumerate) and
// GET (fetch one value) do. A one-line audit entry (user + UTC timestamp + action)
// is appended to databricks-secrets-audit.log on every LIST and GET.
class DatabricksSecretsForm : Form
{
    // One secret's coordinates. Databricks secrets are two-level (scope + key);
    // ToString() is what the ListBox renders.
    class DbxSecret
    {
        public string Scope = "", Key = "";
        public override string ToString() { return Scope + "   /   " + Key; }
    }

    // Workspace host + PAT from .env. The literals are non-secret empty fallbacks
    // so a missing .env degrades to a clear "configure .env" status, never a leak.
    static readonly string Host  = Env.Get("DATABRICKS_HOST", "").TrimEnd('/');
    static readonly string Token = Env.Get("DATABRICKS_TOKEN", "");

    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    TextBox nameBox, valueBox;
    Button getBtn, revealBtn, copyBtn, nameCopyBtn, listBtn;
    ListBox secretList;
    Label status, countLabel;
    string currentValue;          // last fetched value, in memory only
    bool revealed;
    List<DbxSecret> secrets = new List<DbxSecret>();   // every scope/key (cache or LIST)
    readonly Timer copyReset = new Timer { Interval = 1200 };

    static string CacheFile()
    {
        return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),
            "databricks-secrets.txt");
    }
    static string AuditFile()
    {
        return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),
            "databricks-secrets-audit.log");
    }

    // One-line, best-effort audit trail. Never throws into the caller.
    static void Audit(string action)
    {
        try
        {
            string line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                + "\tuser=" + Environment.UserName + "\t" + action + Environment.NewLine;
            File.AppendAllText(AuditFile(), line, new UTF8Encoding(false));
        }
        catch { }
    }

    public DatabricksSecretsForm()
    {
        Text = "Databricks Secrets";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(600, 560);
        BackColor = ColorTranslator.FromHtml("#0A0F1E");
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        Color fieldBack = ColorTranslator.FromHtml("#111C33");
        Color fieldFore = ColorTranslator.FromHtml("#E2E8F0");

        var header = new Label {
            Text = "🧱  DATABRICKS SECRETS",
            ForeColor = ColorTranslator.FromHtml("#F87171"),
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            AutoSize = true, Location = new Point(22, 16), BackColor = Color.Transparent };
        var divider = new Panel {
            Location = new Point(24, 50), Size = new Size(552, 2),
            BackColor = ColorTranslator.FromHtml("#7F1D1D") };

        var hostLabel = LauncherForm.SectionLabel("WORKSPACE", 24, 64);
        var hostVal = new Label {
            Text = Host.Length > 0 ? Host : "(set DATABRICKS_HOST in .env)",
            AutoSize = true, Location = new Point(24, 82),
            ForeColor = ColorTranslator.FromHtml("#94A3B8"),
            Font = new Font("Consolas", 10F), BackColor = Color.Transparent };

        // The search box filters the cached scope/key list live (no network).
        var nameLabel = LauncherForm.SectionLabel("SCOPE / KEY  ·  TYPE TO SEARCH", 24, 116);
        nameBox = new TextBox {
            Location = new Point(24, 136), Size = new Size(330, 28),
            BackColor = fieldBack, ForeColor = fieldFore,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5F) };
        nameBox.TextChanged += (s, e) => FilterSecrets();
        nameBox.KeyDown += (s, e) => {
            // Enter GETs the top filtered match; Down jumps into the list.
            if (e.KeyCode == Keys.Enter) {
                if (secretList.Items.Count > 0) PickAndGet((DbxSecret)secretList.Items[0]);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Down && secretList.Items.Count > 0) {
                secretList.SelectedIndex = 0; secretList.Focus();
                e.SuppressKeyPress = true;
            }
        };
        nameBox.HandleCreated += (s, e) =>
            LauncherForm.SendMessage(nameBox.Handle,
                LauncherForm.EM_SETCUEBANNER, (IntPtr)1, "Search scopes & keys…");

        listBtn = FlatButton("↻  LIST", ColorTranslator.FromHtml("#1E293B"),
            new Point(362, 135), new Size(100, 30));
        listBtn.Click += (s, e) => RefreshSecrets();

        getBtn = FlatButton("▶  GET", ColorTranslator.FromHtml("#0891B2"),
            new Point(476, 135), new Size(100, 30));
        getBtn.Click += (s, e) => {
            if (secretList.SelectedItem is DbxSecret) PickAndGet((DbxSecret)secretList.SelectedItem);
            else if (secretList.Items.Count > 0) PickAndGet((DbxSecret)secretList.Items[0]);
            else SetStatus("Type to search, then pick a scope/key to GET.",
                ColorTranslator.FromHtml("#F87171"));
        };

        countLabel = new Label {
            AutoSize = true, Location = new Point(24, 170),
            ForeColor = ColorTranslator.FromHtml("#94A3B8"),
            Font = new Font("Segoe UI", 9F), BackColor = Color.Transparent };

        secretList = new ListBox {
            Location = new Point(24, 190), Size = new Size(552, 150),
            BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false,
            BackColor = ColorTranslator.FromHtml("#0D1526"),
            ForeColor = ColorTranslator.FromHtml("#67E8F9"),
            Font = new Font("Consolas", 10F), Cursor = Cursors.Hand };
        secretList.MouseClick += (s, e) => {
            int i = secretList.IndexFromPoint(e.Location);
            if (i >= 0 && i < secretList.Items.Count) PickAndGet((DbxSecret)secretList.Items[i]);
        };
        secretList.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter && secretList.SelectedItem is DbxSecret) {
                PickAndGet((DbxSecret)secretList.SelectedItem); e.SuppressKeyPress = true;
            }
        };

        var valueLabel = LauncherForm.SectionLabel("VALUE  ·  GET FILLS IT (READ-ONLY)", 24, 352);
        valueBox = new TextBox {
            Location = new Point(24, 372), Size = new Size(552, 28),
            ReadOnly = true, UseSystemPasswordChar = true,
            BackColor = fieldBack, ForeColor = fieldFore,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 10.5F) };

        revealBtn = FlatButton("👁", ColorTranslator.FromHtml("#1E293B"),
            new Point(24, 408), new Size(44, 28));
        revealBtn.Click += (s, e) => {
            revealed = !revealed;
            valueBox.UseSystemPasswordChar = !revealed;
            revealBtn.Text = revealed ? "🙈" : "👁";
        };

        copyBtn = FlatButton("⧉  COPY VALUE", ColorTranslator.FromHtml("#1E293B"),
            new Point(76, 408), new Size(140, 28));
        copyReset.Tick += (s, e) => { copyReset.Stop(); copyBtn.Text = "⧉  COPY VALUE"; };
        copyBtn.Click += (s, e) => {
            if (string.IsNullOrEmpty(currentValue)) return;
            try { Clipboard.SetText(currentValue); copyBtn.Text = "✓  COPIED";
                  copyReset.Stop(); copyReset.Start(); }
            catch { }
        };

        nameCopyBtn = FlatButton("⧉  scope/key", ColorTranslator.FromHtml("#1E293B"),
            new Point(224, 408), new Size(140, 28));
        nameCopyBtn.Click += (s, e) => {
            var sel = secretList.SelectedItem as DbxSecret;
            if (sel == null && secretList.Items.Count > 0) sel = secretList.Items[0] as DbxSecret;
            if (sel == null) return;
            try { Clipboard.SetText(sel.Scope + "/" + sel.Key); } catch { }
        };

        status = new Label {
            Text = "Read-only. ↻ LIST enumerates every scope/key (values are never "
                 + "cached); type to search, click one to GET its value. Uses the "
                 + "Databricks token in .env.",
            AutoSize = false, Location = new Point(24, 448), Size = new Size(552, 96),
            ForeColor = ColorTranslator.FromHtml("#64748B"),
            Font = new Font("Segoe UI", 9F), BackColor = Color.Transparent };

        Controls.Add(header);
        Controls.Add(divider);
        Controls.Add(hostLabel);
        Controls.Add(hostVal);
        Controls.Add(nameLabel);
        Controls.Add(nameBox);
        Controls.Add(listBtn);
        Controls.Add(getBtn);
        Controls.Add(countLabel);
        Controls.Add(secretList);
        Controls.Add(valueLabel);
        Controls.Add(valueBox);
        Controls.Add(revealBtn);
        Controls.Add(copyBtn);
        Controls.Add(nameCopyBtn);
        Controls.Add(status);
        ActiveControl = nameBox;

        if (Host.Length == 0 || Token.Length == 0)
        {
            SetStatus("✗ Set DATABRICKS_HOST and DATABRICKS_TOKEN in .env next to the "
                + "exe, then reopen this window.", ColorTranslator.FromHtml("#F87171"));
            listBtn.Enabled = false; getBtn.Enabled = false;
            return;
        }

        // Load cached scope/key list; if empty, enumerate once from the workspace.
        secrets = LoadCache();
        if (secrets.Count > 0)
        {
            FilterSecrets();
            SetStatus("Loaded " + secrets.Count + " scope/key entries from cache. "
                + "Type to search, click one to GET — ↻ LIST to refresh.",
                ColorTranslator.FromHtml("#38BDF8"));
        }
        else
        {
            RefreshSecrets();
        }
    }

    // Load the cached scope\tkey lines (names only, never values).
    static List<DbxSecret> LoadCache()
    {
        var list = new List<DbxSecret>();
        try
        {
            if (!File.Exists(CacheFile())) return list;
            foreach (string raw in File.ReadAllLines(CacheFile()))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                list.Add(new DbxSecret {
                    Scope = line.Substring(0, tab), Key = line.Substring(tab + 1) });
            }
        }
        catch { }
        return list
            .OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Rebuild the ListBox from `secrets`, keeping entries whose scope OR key
    // contains the query (case-insensitive substring). Updates the count label.
    void FilterSecrets()
    {
        if (secretList == null) return;
        string q = (nameBox.Text ?? "").Trim();
        var matches = (q.Length == 0
            ? secrets
            : secrets.Where(x =>
                x.Scope.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                x.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
            .ToList();

        secretList.BeginUpdate();
        secretList.Items.Clear();
        foreach (var x in matches) secretList.Items.Add(x);
        secretList.EndUpdate();

        if (secrets.Count == 0) countLabel.Text = "";
        else if (q.Length == 0) countLabel.Text = secrets.Count + " secrets";
        else countLabel.Text = matches.Count + " of " + secrets.Count + " secrets";
    }

    void PickAndGet(DbxSecret sec)
    {
        if (sec == null) return;
        // Reflect the pick in the list selection without refiltering it away.
        int idx = secretList.Items.IndexOf(sec);
        if (idx >= 0) secretList.SelectedIndex = idx;
        DoGet(sec);
    }

    // ---- enumerate every scope/key on a background thread ----

    void RefreshSecrets()
    {
        listBtn.Enabled = false; getBtn.Enabled = false;
        listBtn.Text = "…";
        SetStatus("Enumerating secret scopes in the workspace…",
            ColorTranslator.FromHtml("#38BDF8"));
        Audit("LIST");

        var t = new System.Threading.Thread(() =>
        {
            var found = new List<DbxSecret>();
            var noAccess = new List<string>();
            int scopeCount = 0;
            string fatal = null;

            try
            {
                int st; string err;
                string body = HttpGet(Host + "/api/2.0/secrets/scopes/list", out st, out err);
                if (body == null) { fatal = err; }
                else
                {
                    var scopes = ParseScopes(body);
                    scopeCount = scopes.Count;
                    if (scopeCount == 0 && st >= 400)
                        fatal = ErrorText(body, st);
                    else
                    {
                        int n = 0;
                        foreach (var sc in scopes)
                        {
                            n++;
                            try { BeginInvoke((Action)(() => SetStatus(
                                "Listing keys… scope " + n + " of " + scopeCount
                                + "  (" + sc.Name + ")", ColorTranslator.FromHtml("#38BDF8")))); }
                            catch { }

                            int st2; string err2;
                            string b2 = HttpGet(Host + "/api/2.0/secrets/list?scope="
                                + Uri.EscapeDataString(sc.Name), out st2, out err2);
                            if (b2 == null) { noAccess.Add(sc.Name); continue; }
                            if (st2 >= 400) { noAccess.Add(sc.Name); continue; }
                            foreach (string key in ParseKeys(b2))
                                found.Add(new DbxSecret { Scope = sc.Name, Key = key });
                        }
                    }
                }
            }
            catch (Exception ex) { fatal = ex.Message; }

            try { BeginInvoke((Action)(() =>
                ListDone(found, scopeCount, noAccess, fatal))); }
            catch { }
        }) { IsBackground = true };
        t.Start();
    }

    void ListDone(List<DbxSecret> found, int scopeCount, List<string> noAccess, string fatal)
    {
        listBtn.Enabled = true; getBtn.Enabled = true;
        listBtn.Text = "↻  LIST";

        if (fatal != null)
        {
            SetStatus("✗ " + fatal, ColorTranslator.FromHtml("#F87171"));
            return;
        }

        secrets = found
            .OrderBy(x => x.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        try
        {
            File.WriteAllLines(CacheFile(),
                secrets.Select(x => x.Scope + "\t" + x.Key).ToArray());
        }
        catch { }
        FilterSecrets();

        string msg = "✓ " + secrets.Count + " keys across " + scopeCount + " scopes.";
        if (noAccess.Count > 0)
            msg += "  " + noAccess.Count + " scope(s) not readable by this token: "
                 + string.Join(", ", noAccess.Take(6).ToArray())
                 + (noAccess.Count > 6 ? " …" : "");
        SetStatus(msg, ColorTranslator.FromHtml("#34D399"));
    }

    // ---- GET one secret value on a background thread ----

    void DoGet(DbxSecret sec)
    {
        currentValue = null;
        valueBox.Text = "";
        revealed = false;
        valueBox.UseSystemPasswordChar = true;
        revealBtn.Text = "👁";
        getBtn.Enabled = false; listBtn.Enabled = false;
        getBtn.Text = "…";
        SetStatus("Fetching " + sec.Scope + "/" + sec.Key + " …",
            ColorTranslator.FromHtml("#38BDF8"));
        Audit("GET " + sec.Scope + "/" + sec.Key);

        var t = new System.Threading.Thread(() =>
        {
            string val = null, err = null;
            try
            {
                int st; string herr;
                string body = HttpGet(Host + "/api/2.0/secrets/get?scope="
                    + Uri.EscapeDataString(sec.Scope) + "&key="
                    + Uri.EscapeDataString(sec.Key), out st, out herr);
                if (body == null) err = herr;
                else if (st >= 400) err = ErrorText(body, st);
                else
                {
                    string b64 = ParseValue(body);
                    if (b64 == null) err = "No value in response.";
                    else val = DecodeValue(b64);
                }
            }
            catch (Exception ex) { err = ex.Message; }

            try { BeginInvoke((Action)(() => GetDone(sec, val, err))); }
            catch { }
        }) { IsBackground = true };
        t.Start();
    }

    void GetDone(DbxSecret sec, string val, string err)
    {
        getBtn.Enabled = true; listBtn.Enabled = true;
        getBtn.Text = "▶  GET";

        if (err != null)
        {
            SetStatus("✗ " + err, ColorTranslator.FromHtml("#F87171"));
            return;
        }
        currentValue = val;
        valueBox.Text = val;   // stays masked until 👁
        SetStatus("✓ Retrieved \"" + sec.Scope + "/" + sec.Key + "\".  Hidden — click "
            + "👁 to reveal, ⧉ COPY VALUE to copy.", ColorTranslator.FromHtml("#34D399"));
    }

    // ---- HTTP + JSON helpers ----

    // GET with Bearer auth. Returns the response body (even for 4xx/5xx, so the
    // caller can parse the Databricks error JSON) and the status code; returns
    // null only on a transport-level failure, with the reason in `err`.
    static string HttpGet(string url, out int status, out string err)
    {
        status = -1; err = "";
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Headers["Authorization"] = "Bearer " + Token;
            req.Accept = "application/json";
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
            {
                status = (int)resp.StatusCode;
                return sr.ReadToEnd();
            }
        }
        catch (WebException wex)
        {
            var hr = wex.Response as HttpWebResponse;
            if (hr != null)
            {
                status = (int)hr.StatusCode;
                try { using (var sr = new StreamReader(hr.GetResponseStream())) return sr.ReadToEnd(); }
                catch { }
            }
            err = wex.Message;
            return null;
        }
        catch (Exception ex) { err = ex.Message; return null; }
    }

    class DbxScope { public string Name = "", Backend = ""; }

    static List<DbxScope> ParseScopes(string body)
    {
        var list = new List<DbxScope>();
        try
        {
            var root = Json.Deserialize<Dictionary<string, object>>(body);
            object arr;
            if (root != null && root.TryGetValue("scopes", out arr) && arr is object[])
                foreach (object o in (object[])arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    list.Add(new DbxScope {
                        Name = name,
                        Backend = d.ContainsKey("backend_type") ? Convert.ToString(d["backend_type"]) : "" });
                }
        }
        catch { }
        return list;
    }

    static List<string> ParseKeys(string body)
    {
        var list = new List<string>();
        try
        {
            var root = Json.Deserialize<Dictionary<string, object>>(body);
            object arr;
            if (root != null && root.TryGetValue("secrets", out arr) && arr is object[])
                foreach (object o in (object[])arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string key = d.ContainsKey("key") ? Convert.ToString(d["key"]) : null;
                    if (!string.IsNullOrEmpty(key)) list.Add(key);
                }
        }
        catch { }
        return list;
    }

    static string ParseValue(string body)
    {
        try
        {
            var root = Json.Deserialize<Dictionary<string, object>>(body);
            object v;
            if (root != null && root.TryGetValue("value", out v)) return Convert.ToString(v);
        }
        catch { }
        return null;
    }

    // Pull a readable message out of a Databricks error body, else fall back to
    // the HTTP status.
    static string ErrorText(string body, int status)
    {
        try
        {
            var root = Json.Deserialize<Dictionary<string, object>>(body);
            string code = root != null && root.ContainsKey("error_code")
                ? Convert.ToString(root["error_code"]) : "";
            string msg = root != null && root.ContainsKey("message")
                ? Convert.ToString(root["message"]) : "";
            if (msg.Length > 0)
                return (code.Length > 0 ? code + ": " : "") + msg;
            if (code.Length > 0) return code;
        }
        catch { }
        return "HTTP " + status;
    }

    // Databricks returns secret values base64-encoded; decode to text.
    static string DecodeValue(string b64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch { return b64; }   // not valid base64 (shouldn't happen) — show as-is
    }

    static Button FlatButton(string text, Color back, Point at, Size size)
    {
        var b = new Button {
            Text = text, Location = at, Size = size,
            ForeColor = Color.White, BackColor = back,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Semibold", 9.5F) };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.25f);
        return b;
    }

    void SetStatus(string text, Color color)
    {
        status.ForeColor = color;
        status.Text = text;
    }
}

// The 🧩 Logic Apps launcher: lists every Standard Logic App workflow (one per
// immediate subfolder of the logic-apps repo) with a live search box; clicking a
// name opens that workflow's designer in the Azure portal in the default browser.
//
// The workflow list is CACHED in logic-apps.txt next to the exe so the repo is
// NOT rescanned on every open — the ↻ REPULL button rescans the repo on demand.
// All environment-specific values (repo path, subscription, resource group, site,
// location) come from .env, so nothing sensitive lives in the committed source.
class LogicAppsForm : Form
{
    // One entry in the list. Standard workflows are subfolders of the repo and
    // open the EMA designer blade under the shared site; Consumption logic apps
    // are standalone Azure resources (each in its own resource group) and open
    // the classic resource designer blade. The two build DIFFERENT deep-links.
    class LaItem
    {
        public string Name = "";
        public bool Consumption;      // false = Standard workflow, true = Consumption
        public string Rg = "";        // Consumption only: the app's own resource group
        public override string ToString() { return Name; }
    }

    readonly string repoPath = Env.Get("LOGIC_APPS_REPO", "");
    readonly string subId    = Env.Get("AZURE_SUBSCRIPTION_ID", "");
    readonly string resGroup = Env.Get("AZURE_RESOURCE_GROUP", "");
    readonly string site     = Env.Get("AZURE_LOGIC_APP_SITE", "");
    readonly string location = Env.Get("AZURE_LOGIC_APP_LOCATION", "West US");

    TextBox searchBox;
    ListBox list;
    Label countLabel, status;
    Button repullBtn;
    // Standard workflows (from the repo) + Consumption logic apps (from az),
    // merged and sorted by name for display.
    List<LaItem> items = new List<LaItem>();

    // Set by GitPull() when a repo pull was skipped/failed; appended to the final
    // REPULL status so a stale Standard list is never silent. Empty = pull was fine.
    string gitPullNote = "";

    static readonly Color Red      = ColorTranslator.FromHtml("#F87171");
    static readonly Color Green    = ColorTranslator.FromHtml("#34D399");
    static readonly Color Cyan     = ColorTranslator.FromHtml("#38BDF8");
    static readonly Color StdText  = ColorTranslator.FromHtml("#67E8F9");   // Standard
    static readonly Color ConsText = ColorTranslator.FromHtml("#FBBF24");   // Consumption
    static readonly Color BadgeDim = ColorTranslator.FromHtml("#475569");

    static string CacheFile()
    {
        return Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "logic-apps.txt");
    }

    // Separate cache for Consumption logic apps (name|resourceGroup per line),
    // since those come from an az query rather than the repo folder scan.
    static string ConsumptionCacheFile()
    {
        return Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "logic-apps-consumption.txt");
    }

    public LogicAppsForm()
    {
        Text = "Logic Apps";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = true; MinimizeBox = false;
        ClientSize = new Size(640, 660);
        MinimumSize = new Size(480, 420);
        BackColor = ColorTranslator.FromHtml("#0A0F1E");
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        int right = ClientSize.Width - 24;   // inner right edge (x=616)

        var header = new Label {
            Text = "🧩  LOGIC APPS",
            ForeColor = ColorTranslator.FromHtml("#60A5FA"),
            Font = new Font("Segoe UI", 14F, FontStyle.Bold),
            AutoSize = true, Location = new Point(22, 16), BackColor = Color.Transparent };
        var divider = new Panel {
            Location = new Point(24, 50), Size = new Size(right - 24, 2),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = ColorTranslator.FromHtml("#1E3A8A") };

        var envInfo = new Label {
            Text = site.Length > 0 ? site + "  ·  " + resGroup : "(configure .env)",
            AutoSize = true, Location = new Point(24, 60),
            ForeColor = ColorTranslator.FromHtml("#64748B"),
            Font = new Font("Consolas", 9F), BackColor = Color.Transparent };

        repullBtn = FlatButton("↻  REPULL", ColorTranslator.FromHtml("#1E293B"),
            new Point(right - 120, 90), new Size(120, 30));
        repullBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        repullBtn.Click += (s, e) => Repull();

        searchBox = new TextBox {
            Location = new Point(24, 91), Size = new Size(right - 24 - 130, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = ColorTranslator.FromHtml("#111C33"),
            ForeColor = ColorTranslator.FromHtml("#E2E8F0"),
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 10.5F) };
        searchBox.TextChanged += (s, e) => ApplyFilter();
        searchBox.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter && list.Items.Count > 0) {
                OpenWorkflow((LaItem)list.Items[Math.Max(0, list.SelectedIndex)]);
                e.SuppressKeyPress = true;
            }
            if (e.KeyCode == Keys.Escape) { searchBox.Text = ""; e.SuppressKeyPress = true; }
        };
        searchBox.HandleCreated += (s, e) =>
            LauncherForm.SendMessage(searchBox.Handle,
                LauncherForm.EM_SETCUEBANNER, (IntPtr)1, "Search workflows…");

        countLabel = new Label {
            AutoSize = true, Location = new Point(24, 128),
            ForeColor = ColorTranslator.FromHtml("#94A3B8"),
            Font = new Font("Segoe UI", 9F), BackColor = Color.Transparent };

        list = new ListBox {
            Location = new Point(24, 150),
            Size = new Size(right - 24, ClientSize.Height - 150 - 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                   | AnchorStyles.Left | AnchorStyles.Right,
            DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26,
            BorderStyle = BorderStyle.None, IntegralHeight = false,
            BackColor = ColorTranslator.FromHtml("#0D1526"),
            ForeColor = ColorTranslator.FromHtml("#67E8F9"),
            Font = new Font("Consolas", 10F), Cursor = Cursors.Hand };
        list.DrawItem += OnDrawItem;
        // Single click on a name launches it (matches "click the name to open").
        list.MouseClick += (s, e) => {
            int i = list.IndexFromPoint(e.Location);
            if (i >= 0 && i < list.Items.Count) OpenWorkflow((LaItem)list.Items[i]);
        };
        list.KeyDown += (s, e) => {
            if (e.KeyCode == Keys.Enter && list.SelectedItem != null) {
                OpenWorkflow((LaItem)list.SelectedItem); e.SuppressKeyPress = true;
            }
        };

        status = new Label {
            AutoSize = false, Location = new Point(24, ClientSize.Height - 34),
            Size = new Size(right - 24, 24),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = ColorTranslator.FromHtml("#64748B"),
            Font = new Font("Segoe UI", 9F), BackColor = Color.Transparent };

        Controls.Add(header);
        Controls.Add(divider);
        Controls.Add(envInfo);
        Controls.Add(repullBtn);
        Controls.Add(searchBox);
        Controls.Add(countLabel);
        Controls.Add(list);
        Controls.Add(status);
        ActiveControl = searchBox;

        // Load both caches (Standard from the repo scan, Consumption from az).
        // If neither has anything yet, pull once.
        items = LoadCache();
        if (items.Count == 0)
        {
            Repull();
        }
        else
        {
            ApplyFilter();
            SetStatus("Loaded " + Describe() + " from cache. "
                + "Click a name to open it — ↻ REPULL to refresh.", Cyan);
        }
    }

    // "N logic apps (X standard · Y consumption)" for status/count text.
    string Describe()
    {
        int std = items.Count(i => !i.Consumption), con = items.Count(i => i.Consumption);
        return items.Count + " logic apps (" + std + " standard · " + con + " consumption)";
    }

    // Load and merge both caches into one sorted list.
    static List<LaItem> LoadCache()
    {
        var list = new List<LaItem>();
        try
        {
            if (File.Exists(CacheFile()))
                list.AddRange(File.ReadAllLines(CacheFile())
                    .Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Select(l => new LaItem { Name = l, Consumption = false }));
        }
        catch { }
        try
        {
            if (File.Exists(ConsumptionCacheFile()))
                list.AddRange(File.ReadAllLines(ConsumptionCacheFile())
                    .Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Select(ParseConsumptionLine).Where(i => i != null));
        }
        catch { }
        return Sort(list);
    }

    // Consumption cache line is "name|resourceGroup".
    static LaItem ParseConsumptionLine(string line)
    {
        int bar = line.IndexOf('|');
        if (bar <= 0) return null;
        return new LaItem {
            Name = line.Substring(0, bar).Trim(),
            Rg = line.Substring(bar + 1).Trim(),
            Consumption = true };
    }

    static List<LaItem> Sort(List<LaItem> l)
    {
        return l.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Consumption).ToList();
    }

    // Rescan the repo's immediate subfolders (Standard, synchronous + fast) and
    // rewrite that cache, then kick off the az query for Consumption logic apps
    // on a background thread so the UI never freezes.
    // REPULL entry point. Always pulls the latest from git FIRST (so the Standard
    // list can't go stale just because the local repo is behind), then rescans the
    // folder. The pull is off the UI thread because it hits the network; when it
    // returns we scan on the UI thread and continue into the Consumption query.
    void Repull()
    {
        gitPullNote = "";
        if (repoPath.Length == 0 || !Directory.Exists(repoPath)) { RepullScan(); return; }

        repullBtn.Enabled = false;
        SetStatus("Pulling latest from git…", Cyan);
        var t = new System.Threading.Thread(() =>
        {
            GitPull();   // synchronous; sets gitPullNote on skip/failure
            try { BeginInvoke((Action)(() => { repullBtn.Enabled = true; RepullScan(); })); }
            catch { }
        }) { IsBackground = true };
        t.Start();
    }

    // Fast-forward pull of the Logic Apps repo. --ff-only never creates a merge or
    // rewrites history, and --autostash sets aside any uncommitted work before the
    // pull and restores it after — so a pull can NEVER wipe local changes. If it
    // can't fast-forward (diverged commits) it fails without changing anything.
    // Best-effort: on any failure we note it and still scan whatever is on disk.
    void GitPull()
    {
        try
        {
            string outp, err;
            int code = RunGit("pull --ff-only --autostash", 60000, out outp, out err);
            if (code == 0) return;   // pulled or already up to date
            string m = (err ?? "").Trim();
            if (m.Length == 0) m = (outp ?? "").Trim();
            m = m.Split('\n')[0].Trim();
            if (m.Length > 120) m = m.Substring(0, 120) + "…";
            gitPullNote = "  (git pull skipped: " + (m.Length == 0 ? "code " + code : m) + ")";
        }
        catch (Exception ex) { gitPullNote = "  (git pull skipped: " + ex.Message + ")"; }
    }

    // Run git in the repo folder and capture its output. repoPath is the working
    // directory so git finds the repo root even though repoPath is a subfolder.
    int RunGit(string args, int timeoutMs, out string outp, out string err)
    {
        outp = ""; err = "";
        var psi = new ProcessStartInfo("git", args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = repoPath
        };
        using (var p = Process.Start(psi))
        {
            outp = p.StandardOutput.ReadToEnd();
            err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
    }

    void RepullScan()
    {
        var standard = new List<LaItem>();
        if (repoPath.Length == 0)
            SetStatus("Set LOGIC_APPS_REPO in .env to list Standard workflows.", Red);
        else if (!Directory.Exists(repoPath))
            SetStatus("Repo folder not found: " + repoPath, Red);
        else
        {
            try
            {
                var found = Directory.GetDirectories(repoPath)
                    .Select(d => Path.GetFileName(d))
                    .Where(n => n.Length > 0 && n[0] != '.')
                    .Select(n => new LaItem { Name = n, Consumption = false })
                    .ToList();
                try { File.WriteAllLines(CacheFile(),
                    found.Select(i => i.Name).ToArray()); } catch { }
                standard = found;
            }
            catch (Exception ex) { SetStatus("Repo scan failed: " + ex.Message, Red); }
        }

        // Keep any cached Consumption entries visible while the az query runs.
        var consumption = items.Where(i => i.Consumption).ToList();
        items = Sort(standard.Concat(consumption).ToList());
        ApplyFilter();
        PullConsumption(standard.Count);
    }

    // Enumerate every Consumption logic app in the subscription via az, rewrite
    // the consumption cache, and merge into the list. Runs off the UI thread.
    void PullConsumption(int standardCount)
    {
        if (subId.Length == 0)
        {
            SetStatus("Repulled " + standardCount + " Standard workflows. "
                + "Set AZURE_SUBSCRIPTION_ID in .env to also list Consumption apps."
                + gitPullNote,
                standardCount > 0 ? Green : Red);
            return;
        }

        repullBtn.Enabled = false;
        SetStatus("Repulled " + standardCount + " Standard workflows. "
            + "Querying Consumption logic apps via az…", Cyan);

        // Tab-separated name<TAB>resourceGroup, one per line, across the subscription.
        string args = "/c az resource list --subscription " + subId
            + " --resource-type Microsoft.Logic/workflows "
            + "--query \"[].{n:name,g:resourceGroup}\" -o tsv";

        var t = new System.Threading.Thread(() =>
        {
            string outp = "", err = "";
            int code = -1;
            bool timedOut = false;
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    outp = p.StandardOutput.ReadToEnd();
                    err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000)) { timedOut = true; try { p.Kill(); } catch { } }
                    else code = p.ExitCode;
                }
            }
            catch (Exception ex) { err = ex.Message; }

            try { BeginInvoke((Action)(() =>
                ConsumptionDone(standardCount, outp, err, code, timedOut))); }
            catch { }
        }) { IsBackground = true };
        t.Start();
    }

    void ConsumptionDone(int standardCount, string outp, string err, int code, bool timedOut)
    {
        repullBtn.Enabled = true;

        if (timedOut)
        {
            SetStatus("Repulled " + standardCount + " Standard workflows. Consumption "
                + "query timed out after 120s — is az installed and logged in?"
                + gitPullNote, Red);
            return;
        }
        if (code != 0)
        {
            string msg = (err ?? "").Trim();
            if (msg.Length == 0) msg = "az exited " + code + ".";
            SetStatus("Repulled " + standardCount + " Standard workflows. "
                + "Consumption query failed: " + msg + gitPullNote, Red);
            return;
        }

        var consumption = new List<LaItem>();
        foreach (var raw in (outp ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var cols = line.Split('\t');
            string nm = cols[0].Trim();
            if (nm.Length == 0) continue;
            consumption.Add(new LaItem {
                Name = nm, Consumption = true,
                Rg = cols.Length > 1 ? cols[1].Trim() : "" });
        }

        try { File.WriteAllLines(ConsumptionCacheFile(),
            consumption.Select(i => i.Name + "|" + i.Rg).ToArray()); } catch { }

        var standard = items.Where(i => !i.Consumption).ToList();
        items = Sort(standard.Concat(consumption).ToList());
        ApplyFilter();
        SetStatus("Repulled " + Describe() + " from the repo and az." + gitPullNote, Green);
    }

    // Case-insensitive substring filter; repopulates the ListBox and the count.
    void ApplyFilter()
    {
        string q = (searchBox.Text ?? "").Trim();
        list.BeginUpdate();
        list.Items.Clear();
        int shown = 0;
        foreach (var it in items)
            if (q.Length == 0 ||
                it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                list.Items.Add(it); shown++;
            }
        list.EndUpdate();
        countLabel.Text = shown + (shown == 1 ? " logic app" : " logic apps")
            + (q.Length > 0 && items.Count > 0 ? "  (of " + items.Count + ")" : "");
    }

    void OnDrawItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var it = (LaItem)list.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        Color back = sel
            ? ColorTranslator.FromHtml("#123047") : ColorTranslator.FromHtml("#0D1526");
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);

        // Right-aligned type badge, then the name in the type's color to its left.
        string badge = it.Consumption ? "consumption" : "standard";
        Size badgeSz = TextRenderer.MeasureText(badge, list.Font);
        TextRenderer.DrawText(e.Graphics, badge, list.Font,
            new Rectangle(e.Bounds.Right - badgeSz.Width - 12, e.Bounds.Y,
                badgeSz.Width, e.Bounds.Height),
            BadgeDim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, it.Name, list.Font,
            new Rectangle(e.Bounds.X + 12, e.Bounds.Y,
                e.Bounds.Width - 16 - badgeSz.Width - 16, e.Bounds.Height),
            it.Consumption ? ConsText : StdText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    // Build the portal deep-link for the item and hand it to the default browser.
    // Standard workflows open the EMA designer blade under the shared site;
    // Consumption logic apps open the classic resource designer in their own RG.
    void OpenWorkflow(LaItem item)
    {
        string url;
        if (item.Consumption)
        {
            if (subId.Length == 0 || item.Rg.Length == 0)
            {
                SetStatus("Missing config for " + item.Name
                    + " (subscription / resource group).", Red);
                return;
            }
            url =
                "https://portal.azure.com/#@/resource"
                + "/subscriptions/" + subId
                + "/resourceGroups/" + Uri.EscapeDataString(item.Rg)
                + "/providers/Microsoft.Logic/workflows/" + Uri.EscapeDataString(item.Name)
                + "/logicApp";
        }
        else
        {
            if (subId.Length == 0 || resGroup.Length == 0 || site.Length == 0)
            {
                SetStatus("Missing Azure config in .env "
                    + "(subscription / resource group / site).", Red);
                return;
            }
            url =
                "https://portal.azure.com/#view/Microsoft_Azure_EMA/WorkflowMenuBlade/~/"
                + "designer/resourceId/"
                + "%2Fsubscriptions%2F" + subId
                + "%2FresourceGroups%2F" + resGroup
                + "%2Fproviders%2FMicrosoft.Web%2Fsites%2F" + site
                + "%2Fworkflows%2F" + Uri.EscapeDataString(item.Name)
                + "/location/" + Uri.EscapeDataString(location)
                + "/isReadOnly~/false/kind/Stateful/defaultBlade/designer/isCodeful~/false";
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            SetStatus("Opening " + item.Name + " in the browser…", Cyan);
        }
        catch (Exception ex)
        {
            SetStatus("Couldn't open browser: " + ex.Message, Red);
        }
    }

    void SetStatus(string text, Color color)
    {
        status.ForeColor = color;
        status.Text = text;
    }

    // Flat dark-theme button (default WinForms buttons are unreadable on dark forms).
    static Button FlatButton(string text, Color back, Point at, Size size)
    {
        var b = new Button {
            Text = text, Location = at, Size = size,
            ForeColor = Color.White, BackColor = back,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Semibold", 9.5F) };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.25f);
        return b;
    }
}
