using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle(TrainerForm.AppName)]
[assembly: AssemblyProduct(TrainerForm.AppName)]

internal sealed partial class TrainerForm : Form
{
    internal const string AppName = "McNeff Helper";
    private const int BannerHeight = 204;
    private const int WindowWidth = 866;
    private static readonly Color Background = Color.FromArgb(21, 17, 30);
    private static readonly Color Surface = Color.FromArgb(34, 27, 48);
    private static readonly Color InputBackground = Color.FromArgb(44, 35, 62);
    private static readonly Color Edge = Color.FromArgb(66, 52, 85);
    private static readonly Color TextColor = Color.FromArgb(255, 248, 241);
    private static readonly Color Muted = Color.FromArgb(190, 179, 205);
    private static readonly Color Accent = Color.FromArgb(255, 190, 119);
    private static readonly Color Good = Color.FromArgb(169, 228, 173);
    private static readonly Color Bad = Color.FromArgb(255, 169, 168);
    private readonly ComboBox savePicker;
    private readonly ComboBox colorPicker;
    private readonly ComboBox brickPicker;
    private readonly Label gameState;
    private readonly Label liveHint;
    private readonly Label greyBalance;
    private readonly Label brickBalance;
    private readonly TextBox greyCount;
    private readonly NumericUpDown brickCount;
    private readonly NumericUpDown allBrickCount;
    private readonly Button setGrey;
    private readonly Button maxGrey;
    private readonly Button setBrick;
    private readonly Button fillBricks;
    private readonly Button maxBricks;
    private readonly CheckBox health;
    private readonly CheckBox stamina;
    private readonly CheckBox oldPeople;
    private readonly NumericUpDown spawnMultiplier;
    private readonly CheckBox overrideSpawnCaps;
    private readonly Button applySpawnSettings;
    private readonly Label status;
    private readonly Timer connectionTimer;
    private readonly GameTrainer trainer;
    private WalletEditor editor;
    private ColoredBrick[] inventory = new ColoredBrick[0];
    private bool greyAvailable;
    private bool gameRunning;
    private bool updatingColors;
    private bool syncing;

    public TrainerForm()
    {
        trainer = new GameTrainer();
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(WindowWidth, 672);
        MinimumSize = new Size(600, 480);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        Text = AppName;
        Icon = GameArt.WindowIcon;
        BackColor = Background;
        ForeColor = TextColor;

        var hero = new ArtworkBanner { Bounds = new Rectangle(0, 0, WindowWidth, BannerHeight) };
        Controls.Add(hero);
        Controls.Add(MakeLabel("Save slot", 22, 218, 72, 34, Muted));
        savePicker = MakePicker("Save slot", 98, 223, 204);
        savePicker.SelectedIndexChanged += OnSaveChanged;
        Controls.Add(savePicker);

        var refresh = MakeButton("Refresh", 312, 218, 102, 34, false);
        refresh.Click += OnRefreshSaves;
        Controls.Add(refresh);

        var projects = MakeButton("Project Manager", 424, 218, 179, 34, false);
        projects.Image = GameArt.ButtonImage("RecipeBookIcon.png", 20);
        projects.TextImageRelation = TextImageRelation.ImageBeforeText;
        projects.Click += OnProjectManager;
        Controls.Add(projects);

        gameState = MakeLabel("", 637, 218, 207, 34, Muted);
        gameState.TextAlign = ContentAlignment.MiddleCenter;
        gameState.BackColor = Surface;
        gameState.AccessibleName = "Game status";
        Controls.Add(gameState);

        var greyPanel = MakeSection("Grey Blocks", 22, 264, 520, 130, "GreyBlock.png");
        greyBalance = MakeLabel("—", 16, 44, 145, 44, TextColor);
        greyBalance.Font = new Font(Font.FontFamily, 21, FontStyle.Bold);
        greyPanel.Controls.Add(greyBalance);
        greyPanel.Controls.Add(MakeLabel("Saved balance", 16, 88, 145, 23, Muted));
        greyPanel.Controls.Add(MakeLabel("New balance", 170, 44, 135, 21, Muted));
        greyCount = new TextBox {
            Bounds = new Rectangle(170, 77, 123, 25),
            AccessibleName = "New Grey Blocks balance",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = InputBackground,
            ForeColor = TextColor,
            TextAlign = HorizontalAlignment.Center
        };
        greyPanel.Controls.Add(greyCount);

        setGrey = MakeButton("Set Grey", 304, 71, 92, 36, false);
        setGrey.Click += OnSetGrey;
        greyPanel.Controls.Add(setGrey);

        maxGrey = MakeButton("Max Grey", 407, 71, 97, 36, true);
        maxGrey.Click += OnMaxGrey;
        greyPanel.Controls.Add(maxGrey);

        var coloredPanel = MakeSection("Colored Bricks", 22, 406, 520, 188, "ColorBlock.png");
        coloredPanel.Controls.Add(MakeLabel("Color filter", 16, 44, 141, 20, Muted));
        colorPicker = MakePicker("Color filter", 16, 66, 141);
        colorPicker.SelectedIndexChanged += OnColorChanged;
        coloredPanel.Controls.Add(colorPicker);

        coloredPanel.Controls.Add(MakeLabel("Brick variant", 169, 44, 335, 20, Muted));
        brickPicker = MakePicker("Colored brick variant", 169, 66, 335);
        brickPicker.DisplayMember = "Name";
        brickPicker.DrawMode = DrawMode.OwnerDrawFixed;
        brickPicker.ItemHeight = 22;
        brickPicker.DrawItem += DrawBrickItem;
        brickPicker.SelectedIndexChanged += OnBrickChanged;
        coloredPanel.Controls.Add(brickPicker);

        brickBalance = MakeLabel("Owned —", 16, 100, 141, 33, Muted);
        coloredPanel.Controls.Add(brickBalance);
        brickCount = MakeBrickCount("Selected brick count", 169, 104, 76, 0);
        coloredPanel.Controls.Add(brickCount);
        setBrick = MakeButton("Set Selected Brick", 256, 99, 248, 35, false);
        setBrick.Click += OnSetBrick;
        coloredPanel.Controls.Add(setBrick);

        coloredPanel.Controls.Add(MakeLabel("All 100 variants", 16, 142, 141, 34, Muted));
        allBrickCount = MakeBrickCount("Count for all colored bricks", 169, 146, 76, WalletEditor.MaxColoredBricks);
        coloredPanel.Controls.Add(allBrickCount);
        fillBricks = MakeButton("Set All Colors", 256, 141, 135, 35, false);
        fillBricks.Click += OnFillBricks;
        coloredPanel.Controls.Add(fillBricks);

        maxBricks = MakeButton("Max All ×" + WalletEditor.MaxColoredBricks, 402, 141, 102, 35, true);
        maxBricks.Click += OnMaxBricks;
        coloredPanel.Controls.Add(maxBricks);

        var live = MakeSection("Live Cheats", 556, 264, 288, 350, titleRightPadding: 137);
        var enemyPicker = MakeButton("Enemy Types", 159, 11, 113, 30, false);
        enemyPicker.Click += OnEnemyTypes;
        live.Controls.Add(enemyPicker);
        liveHint = MakeLabel("", 16, 42, 256, 29, Muted);
        live.Controls.Add(liveHint);
        health = MakeToggle("Unlimited health", 16, 74, 256, "Health.png");
        stamina = MakeToggle("Unlimited stamina", 16, 121, 256, "Stamina.png");
        oldPeople = MakeToggle("More old people", 16, 168, 256, "OldPerson.png", true);
        health.CheckedChanged += OnHealthChanged;
        stamina.CheckedChanged += OnStaminaChanged;
        oldPeople.CheckedChanged += OnOldPeopleChanged;
        live.Controls.Add(health);
        live.Controls.Add(stamina);
        live.Controls.Add(oldPeople);
        live.Controls.Add(MakeLabel("Multiplier (×)", 16, 224, 88, 27, TextColor));
        spawnMultiplier = new NumericUpDown {
            AccessibleName = "Old people spawn multiplier",
            Bounds = new Rectangle(121, 224, 76, 27),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Minimum = 1,
            Maximum = GameTrainer.MaxOldPeopleMultiplier,
            Value = GameTrainer.DefaultOldPeopleMultiplier,
            BackColor = InputBackground,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Center
        };
        live.Controls.Add(spawnMultiplier);
        applySpawnSettings = MakeButton("Apply", 204, 222, 68, 29, true);
        applySpawnSettings.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        applySpawnSettings.AccessibleName = "Apply old people spawner settings";
        applySpawnSettings.Click += OnApplySpawnSettings;
        live.Controls.Add(applySpawnSettings);
        overrideSpawnCaps = new CheckBox {
            Text = "Override spawn caps",
            AccessibleName = "Override old people spawn caps",
            Bounds = new Rectangle(16, 263, 256, 27),
            ForeColor = TextColor,
            UseVisualStyleBackColor = false
        };
        live.Controls.Add(overrideSpawnCaps);
        live.Controls.Add(MakeLabel("Keep the trainer open to keep cheats on.",
            16, 312, 256, 30, Muted));

        status = MakeLabel("Reading save…", 22, 626, 822, 33, Muted);
        status.BackColor = Surface;
        status.Padding = new Padding(12, 0, 12, 0);
        status.AccessibleName = "Status";
        Controls.Add(status);
        ConfigureResponsiveLayout(hero, refresh, projects, greyPanel, coloredPanel, live, enemyPicker);

        UpdateControlStates();
        connectionTimer = new Timer { Interval = 1000 };
        connectionTimer.Tick += OnConnectionTick;
        Shown += OnShown;
        FormClosed += OnFormClosed;
    }

    private Panel MakeSection(string title, int x, int y, int width, int height, string art = null, int titleRightPadding = 16)
    {
        var panel = new Panel { Bounds = new Rectangle(x, y, width, height), BackColor = Surface };
        panel.Paint += (sender, args) => {
            using (var pen = new Pen(Edge))
                args.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        int titleX = 16;
        if (art != null)
        {
            var picture = new PictureBox {
                Bounds = new Rectangle(16, 12, 25, 25),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = GameArt.Image(art),
                TabStop = false
            };
            panel.Controls.Add(picture);
            titleX = 50;
        }
        var label = MakeLabel(title, titleX, 10, width - titleX - titleRightPadding, 31, TextColor);
        label.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
        panel.Controls.Add(label);
        Controls.Add(panel);
        return panel;
    }

    private static Label MakeLabel(string text, int x, int y, int width, int height, Color color)
    {
        return new Label {
            Text = text,
            Bounds = new Rectangle(x, y, width, height),
            ForeColor = color,
            UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private static ComboBox MakePicker(string name, int x, int y, int width)
    {
        return new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = InputBackground,
            ForeColor = TextColor,
            AccessibleName = name,
            Bounds = new Rectangle(x, y, width, 27),
            DropDownHeight = 280,
            IntegralHeight = false
        };
    }

    private static NumericUpDown MakeBrickCount(string name, int x, int y, int width, int value)
    {
        return new NumericUpDown {
            Maximum = WalletEditor.MaxColoredBricks,
            Value = value,
            Bounds = new Rectangle(x, y, width, 26),
            AccessibleName = name,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = InputBackground,
            ForeColor = TextColor,
            TextAlign = HorizontalAlignment.Center
        };
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter)
        {
            Button action = null;
            if (greyCount.ContainsFocus)
                action = setGrey;
            else if (brickCount.ContainsFocus)
                action = setBrick;
            else if (allBrickCount.ContainsFocus)
                action = fillBricks;

            if (action != null && action.Enabled)
            {
                action.PerformClick();
                return true;
            }
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private static Button MakeButton(string text, int x, int y, int width, int height, bool primary)
    {
        return new GameButton(primary) {
            Text = text,
            Bounds = new Rectangle(x, y, width, height),
            Cursor = Cursors.Hand,
            UseMnemonic = false
        };
    }

    private static CheckBox MakeToggle(string name, int x, int y, int width, string art, bool pixelArt = false)
    {
        return new ToggleSwitch(GameArt.Image(art), pixelArt) {
            Text = name + "   Off",
            AccessibleName = name,
            Bounds = new Rectangle(x, y, width, 44),
            Cursor = Cursors.Hand
        };
    }

    private void OnShown(object sender, EventArgs args)
    {
        OnConnectionTick(sender, args);
        connectionTimer.Start();
        OnRefreshSaves(sender, args);
    }

    private void OnRefreshSaves(object sender, EventArgs args)
    {
        try
        {
            if (editor == null)
            {
                string dll = WalletEditor.FindGameDll();
                if (string.IsNullOrEmpty(dll))
                    throw new InvalidOperationException("The Steam installation was not found.");
                editor = new WalletEditor(dll);
            }

            SaveSlot previous = savePicker.SelectedItem as SaveSlot;
            SaveSlot[] slots = editor.FindSaveSlots();
            int index = previous == null ? -1 : Array.FindIndex(slots, slot =>
                slot.Path != null && string.Equals(slot.Path, previous.Path, StringComparison.OrdinalIgnoreCase));
            if (index < 0 && previous != null)
                index = Array.FindIndex(slots, slot => slot.Number == previous.Number);
            if (index < 0)
                index = Array.FindIndex(slots, slot => slot.Path != null);

            savePicker.BeginUpdate();
            try
            {
                savePicker.Items.Clear();
                savePicker.Items.AddRange(slots);
                savePicker.SelectedIndex = Math.Max(0, index);
            }
            finally
            {
                savePicker.EndUpdate();
            }
        }
        catch (Exception error)
        {
            savePicker.Items.Clear();
            ClearGreyControls();
            ClearColoredControls();
            UpdateControlStates();
            ShowStatus(error.Message, Bad);
        }
    }

    private void OnSaveChanged(object sender, EventArgs args)
    {
        var selected = savePicker.SelectedItem as SaveSlot;
        if (selected == null)
            return;

        if (selected.Path == null)
        {
            ClearGreyControls();
            ClearColoredControls();
            UpdateControlStates();
            ShowStatus(selected.Exists
                ? "Load " + selected + " in the game, then close the game and click Refresh."
                : "Create " + selected.ToString().Replace(" (empty)", "") + " in the game, then close it and click Refresh.", Muted);
            return;
        }

        ShowStatus(gameRunning ? "Close the game to edit " + selected + "." :
            "Edits apply to " + selected + ". Backups are automatic.", Muted);
        try
        {
            RefreshGrey(selected.Path);
            greyAvailable = true;
        }
        catch (Exception error)
        {
            ClearGreyControls();
            ShowStatus(error.Message, Bad);
        }

        try
        {
            RefreshBricks(selected.Path, null);
        }
        catch (Exception error)
        {
            ClearColoredControls();
            ShowStatus(error.Message, Bad);
        }
        UpdateControlStates();
    }

    private void ClearGreyControls()
    {
        greyAvailable = false;
        greyBalance.Text = "—";
        greyCount.Clear();
    }

    private void ClearColoredControls()
    {
        inventory = new ColoredBrick[0];
        colorPicker.Items.Clear();
        brickPicker.Items.Clear();
        brickBalance.Text = "Owned —";
        brickCount.Value = 0;
    }

    private void UpdateControlStates()
    {
        bool canEdit = !gameRunning;
        bool hasColors = inventory.Length > 0;
        bool hasVariant = canEdit && brickPicker.SelectedItem != null;
        greyCount.Enabled = canEdit && greyAvailable;
        setGrey.Enabled = greyCount.Enabled;
        maxGrey.Enabled = greyCount.Enabled;
        colorPicker.Enabled = hasColors;
        brickPicker.Enabled = hasColors;
        brickCount.Enabled = hasVariant;
        setBrick.Enabled = hasVariant;
        allBrickCount.Enabled = canEdit && hasColors;
        fillBricks.Enabled = allBrickCount.Enabled;
        maxBricks.Enabled = allBrickCount.Enabled;
        health.Enabled = gameRunning;
        stamina.Enabled = gameRunning;
        oldPeople.Enabled = gameRunning;
        applySpawnSettings.Enabled = gameRunning;
    }

    private int RefreshGrey(string path)
    {
        int count = editor.Read(path).Count;
        string amount = count.ToString("N0", CultureInfo.CurrentCulture);
        greyBalance.Text = amount;
        greyCount.Text = amount;
        return count;
    }

    private void RefreshBricks(string path, string keepId)
    {
        inventory = editor.ReadColoredBricks(path);
        string selectedColor = colorPicker.SelectedItem as string;
        string[] colors = new[] { "All colors" }.Concat(inventory.Select(brick => brick.ColorName)
            .Distinct(StringComparer.Ordinal).OrderBy(color => color, StringComparer.CurrentCulture)).ToArray();
        if (!colorPicker.Items.Cast<string>().SequenceEqual(colors))
        {
            updatingColors = true;
            colorPicker.BeginUpdate();
            try
            {
                colorPicker.Items.Clear();
                colorPicker.Items.AddRange(colors);
                colorPicker.SelectedIndex = Math.Max(0, Array.IndexOf(colors, selectedColor));
            }
            finally
            {
                colorPicker.EndUpdate();
                updatingColors = false;
            }
        }
        FilterBricks(keepId);
    }

    private void OnColorChanged(object sender, EventArgs args)
    {
        if (updatingColors)
            return;
        var selected = brickPicker.SelectedItem as ColoredBrick;
        FilterBricks(selected == null ? null : selected.Id);
    }

    private void FilterBricks(string keepId)
    {
        string color = colorPicker.SelectedItem as string;
        ColoredBrick[] visible = colorPicker.SelectedIndex <= 0 ? inventory :
            inventory.Where(brick => brick.ColorName == color).ToArray();
        brickPicker.BeginUpdate();
        try
        {
            brickPicker.Items.Clear();
            brickPicker.Items.AddRange(visible);
            if (visible.Length > 0)
            {
                int index = Array.FindIndex(visible, brick => brick.Id == keepId);
                brickPicker.SelectedIndex = Math.Max(0, index);
            }
        }
        finally
        {
            brickPicker.EndUpdate();
        }
        UpdateControlStates();
    }

    private void DrawBrickItem(object sender, DrawItemEventArgs args)
    {
        bool selected = (args.State & DrawItemState.Selected) != 0;
        using (var surface = new SolidBrush(selected ? Color.FromArgb(80, 57, 110) : InputBackground))
            args.Graphics.FillRectangle(surface, args.Bounds);

        if (args.Index < 0)
            return;
        var item = (ColoredBrick)brickPicker.Items[args.Index];
        var swatch = new Rectangle(args.Bounds.X + 8, args.Bounds.Y + (args.Bounds.Height - 12) / 2, 14, 12);
        GameArt.DrawSwatch(args.Graphics, ulong.Parse(item.Id, CultureInfo.InvariantCulture), swatch);
        TextRenderer.DrawText(args.Graphics, item.Name, args.Font,
            new Rectangle(args.Bounds.X + 28, args.Bounds.Y, args.Bounds.Width - 31, args.Bounds.Height),
            TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        args.DrawFocusRectangle();
    }

    private void OnBrickChanged(object sender, EventArgs args)
    {
        var brick = brickPicker.SelectedItem as ColoredBrick;
        brickBalance.Text = brick == null ? "Owned —" : "Owned " + brick.Count.ToString("N0", CultureInfo.CurrentCulture);
        brickCount.Value = brick == null ? 0 : Math.Max(0, Math.Min(WalletEditor.MaxColoredBricks, brick.Count));
        UpdateControlStates();
    }

    private void OnSetGrey(object sender, EventArgs args)
    {
        int count;
        if (!int.TryParse(greyCount.Text.Trim(), NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out count) ||
            count < 0 || count > WalletEditor.MaxGreyBlocks)
        {
            ShowStatus("Enter a whole Grey Blocks count from 0 to " +
                WalletEditor.MaxGreyBlocks.ToString("N0", CultureInfo.CurrentCulture) + ".", Bad);
            greyCount.Focus();
            greyCount.SelectAll();
            return;
        }
        SetGreyCount(count);
    }

    private void OnMaxGrey(object sender, EventArgs args)
    {
        SetGreyCount(WalletEditor.MaxGreyBlocks);
    }

    private void SetGreyCount(int count)
    {
        RunSaveAction(path => {
            string backup = editor.Set(editor.Read(path), count);
            int savedCount = RefreshGrey(path);
            string amount = savedCount.ToString("N0", CultureInfo.CurrentCulture);
            if (savedCount != count)
                throw new IOException("The wallet changed after saving. It now holds " + amount + " Grey Blocks.");
            return backup == null ? "The wallet already holds " + amount + " Grey Blocks." :
                "Verified " + amount + " Grey Blocks in the saved wallet. Backup saved.";
        });
    }

    private void OnSetBrick(object sender, EventArgs args)
    {
        var brick = brickPicker.SelectedItem as ColoredBrick;
        if (brick == null)
            return;
        int count = (int)brickCount.Value;
        RunSaveAction(path => {
            string backup = editor.SetColoredBrick(path, brick.Id, count);
            RefreshBricks(path, brick.Id);
            return backup == null ? "This brick already has that count." :
                brick.Name + " set to ×" + count + ". Backup saved.";
        });
    }

    private void OnFillBricks(object sender, EventArgs args)
    {
        SetAllBrickCounts((int)allBrickCount.Value);
    }

    private void OnMaxBricks(object sender, EventArgs args)
    {
        SetAllBrickCounts(WalletEditor.MaxColoredBricks);
    }

    private void SetAllBrickCounts(int count)
    {
        var brick = brickPicker.SelectedItem as ColoredBrick;
        RunSaveAction(path => {
            string backup = editor.SetColoredBricks(path, count);
            RefreshBricks(path, brick == null ? null : brick.Id);
            allBrickCount.Value = count;
            return backup == null ? "All colored bricks are already at ×" + count + "." :
                "All colored bricks set to ×" + count + ". Backup saved.";
        });
    }

    private void RunSaveAction(Func<string, string> action)
    {
        var selected = savePicker.SelectedItem as SaveSlot;
        if (selected == null || selected.Path == null)
            return;
        try
        {
            ShowStatus(selected + "   " + action(VerifiedSavePath(editor, selected)), Good);
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, Bad);
        }
    }

    private void OnHealthChanged(object sender, EventArgs args)
    {
        ChangeCheat(health, trainer.SetUnlimitedHealth);
    }

    private void OnStaminaChanged(object sender, EventArgs args)
    {
        ChangeCheat(stamina, trainer.SetUnlimitedStamina);
    }

    private void OnOldPeopleChanged(object sender, EventArgs args)
    {
        ChangeCheat(oldPeople, ApplyOldPeopleSettings);
    }

    private string ApplyOldPeopleSettings(bool enabled)
    {
        return trainer.SetMoreOldPeople(enabled, (int)spawnMultiplier.Value, overrideSpawnCaps.Checked);
    }

    private void OnApplySpawnSettings(object sender, EventArgs args)
    {
        if (!gameRunning)
            return;
        if (!oldPeople.Checked)
        {
            oldPeople.Checked = true;
            return;
        }
        try
        {
            ShowStatus(ApplyOldPeopleSettings(true), Good);
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, Bad);
        }
    }

    private void ChangeCheat(CheckBox toggle, Func<bool, string> action, Action<string, Color> showStatus = null)
    {
        if (showStatus == null)
            showStatus = ShowStatus;
        if (!syncing)
        {
            try
            {
                showStatus(action(toggle.Checked), toggle.Checked ? Good : Muted);
            }
            catch (Exception error)
            {
                syncing = true;
                toggle.Checked = !toggle.Checked;
                syncing = false;
                showStatus(error.Message, Bad);
            }
        }
        toggle.Text = toggle.AccessibleName + (toggle.Checked ? "   On" : "   Off");
        toggle.Invalidate();
    }

    private void OnConnectionTick(object sender, EventArgs args)
    {
        bool wasRunning = gameRunning;
        gameRunning = WalletEditor.IsGameRunning();
        if (wasRunning != gameRunning || gameState.Text.Length == 0)
        {
            gameState.Text = gameRunning ? "●  Game running" : "○  Game closed";
            gameState.ForeColor = gameRunning ? Good : Muted;
            liveHint.Text = gameRunning ? "Choose a cheat to turn it on." : "Start the game to enable live cheats.";
            UpdateControlStates();
            if (wasRunning && !gameRunning && editor != null)
                OnRefreshSaves(sender, args);
        }

        if ((health.Checked || stamina.Checked || oldPeople.Checked || wasRunning && !gameRunning) && !trainer.IsAttached)
        {
            syncing = true;
            health.Checked = false;
            stamina.Checked = false;
            oldPeople.Checked = false;
            syncing = false;
            ShowStatus("The game closed. Live cheats are off.", Muted);
        }
        if (enemyTypes != null)
            enemyTypes.RefreshState();
    }

    private void OnFormClosed(object sender, FormClosedEventArgs args)
    {
        connectionTimer.Dispose();
        try
        {
            trainer.Dispose();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowStatus(string message, Color color)
    {
        status.Text = message;
        status.ForeColor = color;
    }

    private sealed class ArtworkBanner : Control
    {
        private readonly Bitmap banner = GameArt.Image("GameHero.jpg");
        private readonly Bitmap logo = GameArt.Image("GameLogo.png");

        public ArtworkBanner()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AccessibleName = "Official game artwork";
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            var canvas = args.Graphics;
            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Max((float)Width / banner.Width, (float)Height / banner.Height);
            float scaledWidth = banner.Width * scale;
            float scaledHeight = banner.Height * scale;
            var placement = new RectangleF((Width - scaledWidth) / 2,
                (Height - scaledHeight) * 0.48f, scaledWidth, scaledHeight);
            canvas.DrawImage(banner, placement);

            var fade = new Rectangle(0, 0, Math.Max(1, Width * 48 / 100), Height);
            using (var gradient = new LinearGradientBrush(fade, Color.FromArgb(205, 32, 12, 54),
                Color.FromArgb(0, 32, 12, 54), LinearGradientMode.Horizontal))
                canvas.FillRectangle(gradient, fade);

            float ratio = (float)Height / BannerHeight;
            GameArt.DrawFit(canvas, logo, new RectangleF(22 * ratio, 25 * ratio, 248 * ratio, 148 * ratio));
        }
    }

    [STAThread]
    public static void Main()
    {
        using (var singleInstance = new System.Threading.Mutex(false, @"Local\WSFOPTrainer"))
        {
            bool owned;
            try { owned = singleInstance.WaitOne(0); }
            catch (System.Threading.AbandonedMutexException) { owned = true; }
            if (!owned)
            {
                MessageBox.Show("McNeff Helper is already open.", AppName);
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrainerForm());
            }
            finally
            {
                singleInstance.ReleaseMutex();
            }
        }
    }
}
