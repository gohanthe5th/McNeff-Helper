using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal sealed partial class TrainerForm
{
    private static readonly Lazy<BuildRecipe[]> BuildCatalog = new Lazy<BuildRecipe[]>(ReadBuildCatalog);

    private static string VerifiedSavePath(WalletEditor editor, SaveSlot selected)
    {
        SaveSlot current = Array.Find(editor.FindSaveSlots(), slot => slot.Number == selected.Number);
        if (current == null || !string.Equals(current.Path, selected.Path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save slots changed. Click Refresh in the trainer and select a slot.");
        return current.Path;
    }

    private void OnProjectManager(object sender, EventArgs args)
    {
        try
        {
            var selected = savePicker.SelectedItem as SaveSlot;
            using (var projects = new ProjectManagerForm(editor, selected))
            {
                projects.ShowDialog(this);
                if (projects.InventoryChanged)
                {
                    var active = savePicker.SelectedItem as SaveSlot;
                    if (active != null && string.Equals(active.Path, selected.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        var brick = brickPicker.SelectedItem as ColoredBrick;
                        RefreshBricks(active.Path, brick == null ? null : brick.Id);
                        ShowStatus(active + "   Project bricks were added. Backup saved.", Good);
                    }
                }
                if (projects.BuildAdded)
                    ShowStatus(selected + "   Build added. Open the lair playmat to place it. Backup saved.", Good);
                else if (projects.RecipeChanged)
                    ShowStatus(selected + "   Recipe book updated. Backup saved.", Good);
            }
        }
        catch (Exception error)
        {
            ShowStatus(error.Message, Bad);
        }
    }

    private static BuildRecipe[] ReadBuildCatalog()
    {
        var catalog = new Dictionary<int, BuildRecipe>();
        using (Stream asset = Assembly.GetExecutingAssembly().GetManifestResourceStream("BuildRecipes.tsv"))
        {
            if (asset == null)
                throw new InvalidDataException("The build recipe catalog is missing.");
            using (var reader = new StreamReader(asset))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] fields = line.Split('\t');
                    if (fields.Length != 5 || string.IsNullOrWhiteSpace(fields[1]))
                        throw new InvalidDataException("The build recipe catalog contains an invalid entry.");
                    int id = int.Parse(fields[0], CultureInfo.InvariantCulture);
                    int shape = int.Parse(fields[2], CultureInfo.InvariantCulture);
                    int color = int.Parse(fields[3], CultureInfo.InvariantCulture);
                    int count = int.Parse(fields[4], CultureInfo.InvariantCulture);
                    if (count <= 0)
                        throw new InvalidDataException("A build recipe has an invalid brick count.");
                    var brick = WalletEditor.DescribeColoredBrick(shape, color);
                    ulong brickId = ulong.Parse(brick.Id, CultureInfo.InvariantCulture);
                    BuildRecipe recipe;
                    if (!catalog.TryGetValue(id, out recipe))
                    {
                        recipe = new BuildRecipe(id, fields[1]);
                        catalog.Add(id, recipe);
                    }
                    recipe.Ingredients.Add(new RecipeIngredient(brickId, brick.Name, count));
                }
            }
        }
        LoadBuildPlacements(catalog);
        LoadBuildEffects(catalog);
        LoadBuildMetadata(catalog);
        LoadBuildRarities(catalog);
        return catalog.Values.OrderBy(recipe => recipe.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static StreamReader OpenCatalog(string resource)
    {
        Stream asset = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (asset == null)
            throw new InvalidDataException("A build catalog resource is missing.");
        return new StreamReader(asset);
    }

    private static void LoadBuildPlacements(Dictionary<int, BuildRecipe> catalog)
    {
        using (var reader = OpenCatalog("BuildPlacements.tsv"))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 7)
                    throw new InvalidDataException("A build layout contains an invalid entry.");
                int id = int.Parse(fields[0], CultureInfo.InvariantCulture);
                var values = fields.Skip(1).Select(field => int.Parse(field, CultureInfo.InvariantCulture)).ToArray();
                catalog[id].Placements.Add(new BuildPlacement(values[0], values[1], values[2], values[3], values[4], values[5]));
            }
        }

        foreach (BuildRecipe recipe in catalog.Values)
        {
            if (recipe.Ingredients.Sum(brick => brick.Count) != recipe.Placements.Count)
                throw new InvalidDataException("A build layout does not match its brick requirements.");
        }
    }

    private static void LoadBuildEffects(Dictionary<int, BuildRecipe> catalog)
    {
        using (var reader = OpenCatalog("BuildEffects.tsv"))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 4)
                    throw new InvalidDataException("A build effect contains an invalid entry.");
                BuildRecipe recipe = catalog[int.Parse(fields[0], CultureInfo.InvariantCulture)];
                recipe.EffectTitle = string.IsNullOrEmpty(fields[3]) ? fields[1] : fields[3] + " " + fields[1] + "  •  In the safe";
                recipe.EffectDescription = fields[2];
            }
        }
    }

    private static void LoadBuildMetadata(Dictionary<int, BuildRecipe> catalog)
    {
        using (var reader = OpenCatalog("BuildMetadata.tsv"))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 2 || (fields[1] != "0" && fields[1] != "1"))
                    throw new InvalidDataException("Build availability contains an invalid entry.");
                catalog[int.Parse(fields[0], CultureInfo.InvariantCulture)].Enabled = fields[1] == "1";
            }
        }
    }

    private static void LoadBuildRarities(Dictionary<int, BuildRecipe> catalog)
    {
        using (var reader = OpenCatalog("BuildRarities.tsv"))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] fields = line.Split('\t');
                int rarity;
                if (fields.Length != 2 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out rarity) ||
                    rarity < 0 || rarity > 4)
                    throw new InvalidDataException("A recipe rarity contains an invalid entry.");
                catalog[int.Parse(fields[0], CultureInfo.InvariantCulture)].Rarity = rarity;
            }
        }
        if (catalog.Values.Any(recipe => recipe.Rarity < 0))
            throw new InvalidDataException("The recipe rarity catalog is incomplete.");
    }

    private sealed class BuildRecipe
    {
        internal readonly int Id;
        internal readonly string Name;
        internal readonly List<RecipeIngredient> Ingredients = new List<RecipeIngredient>();
        internal readonly List<BuildPlacement> Placements = new List<BuildPlacement>();
        internal bool Enabled;
        internal int Rarity = -1;
        internal string EffectTitle;
        internal string EffectDescription;

        public BuildRecipe(int id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    private sealed class RecipeIngredient
    {
        internal readonly ulong Id;
        internal readonly string Name;
        internal readonly int Count;

        public RecipeIngredient(ulong id, string name, int count)
        {
            Id = id;
            Name = name;
            Count = count;
        }
    }

    private sealed class ProjectManagerForm : Form
    {
        private enum RecipeFilter { All, Locked, Unlocked, Unavailable }
        private enum RecipeSort { Name, HighestRarity, LowestRarity }

        private const string PassiveIncome = "Passive Income";
        private static readonly string[] RarityNames = { "Common", "Uncommon", "Rare", "Epic", "Legendary" };
        private static readonly int[] EarningsByRarity = { 14, 18, 22, 26, 30 };
        private static readonly Color[] RarityColors = {
            Muted, Color.FromArgb(157, 231, 145), Color.FromArgb(145, 202, 255),
            Color.FromArgb(220, 170, 255), Accent
        };
        private readonly WalletEditor editor;
        private readonly SaveSlot save;
        private readonly TextBox search;
        private readonly ComboBox recipeFilter;
        private readonly ComboBox recipeSort;
        private readonly Label results;
        private readonly ListView recipes;
        private readonly HashSet<int> queuedRecipes = new HashSet<int>();
        private readonly Label heading;
        private readonly Label rarity;
        private readonly Label earnings;
        private readonly Panel recipePreview;
        private BuildRecipe previewRecipe;
        private Bitmap previewBitmap;
        private Image effectArt;
        private readonly Label effectTitle;
        private readonly Label effectDescription;
        private readonly Label recipeStatus;
        private readonly Label ownershipLabel;
        private readonly ListView subStats;
        private readonly ImageList subStatIcons;
        private readonly Label readiness;
        private readonly DataGridView materials;
        private readonly Button unlockRecipe;
        private readonly Button selectAllRecipes;
        private readonly Button addMissing;
        private readonly Button addBuild;
        private readonly Label note;
        private readonly Timer gameTimer;
        private Dictionary<ulong, int> owned;
        private BuildOwnership builds;
        private HashSet<int> unlocked;
        private bool gameRunning;
        private bool updatingRecipeList;
        private int missing;

        internal bool InventoryChanged { get; private set; }
        internal bool BuildAdded { get; private set; }
        internal bool RecipeChanged { get; private set; }

        public ProjectManagerForm(WalletEditor editor, SaveSlot save)
        {
            this.editor = editor;
            this.save = save;
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(950, 688);
            MinimumSize = new Size(834, 602);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            Text = "Project Manager | " + AppName;
            Icon = GameArt.WindowIcon;
            BackColor = Background;
            ForeColor = TextColor;

            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 1
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            var browser = MakeProjectPanel(8);
            browser.Margin = new Padding(0, 0, 12, 0);
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            browser.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.Controls.Add(browser, 0, 0);

            browser.Controls.Add(MakeRecipeBookHeader(), 0, 0);
            browser.Controls.Add(ProjectLabel("Search by name", Muted), 0, 1);
            search = new TextBox {
                Dock = DockStyle.Top,
                AccessibleName = "Search recipes by name",
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = InputBackground,
                ForeColor = TextColor,
                Margin = new Padding(0)
            };
            search.TextChanged += (sender, args) => FilterRecipes();
            browser.Controls.Add(search, 0, 2);

            var recipePickers = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                ColumnCount = 2,
                RowCount = 1
            };
            recipePickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
            recipePickers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
            recipePickers.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            recipeFilter = MakeRecipePicker("Filter recipes by unlock status", "All recipes", "Locked", "Unlocked", "Unavailable");
            recipeFilter.Margin = new Padding(0, 2, 6, 0);
            recipeFilter.SelectedIndexChanged += (sender, args) => FilterRecipes();
            recipePickers.Controls.Add(recipeFilter, 0, 0);
            recipeSort = MakeRecipePicker("Sort recipes", "Name A–Z", "Rarity highest first", "Rarity lowest first");
            recipeSort.SelectedIndexChanged += (sender, args) => FilterRecipes();
            recipePickers.Controls.Add(recipeSort, 1, 0);
            browser.Controls.Add(recipePickers, 0, 3);

            results = ProjectLabel("", Muted);
            browser.Controls.Add(results, 0, 4);
            recipes = new ListView {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = InputBackground,
                ForeColor = TextColor,
                View = View.Details,
                HeaderStyle = ColumnHeaderStyle.None,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                AccessibleName = "Build recipes",
                AccessibleDescription = "Unlocked recipes stay checked. Check locked recipes to unlock them together.",
                Margin = new Padding(0)
            };
            recipes.Columns.Add("Recipe", 151);
            recipes.Columns.Add("Status", 80, HorizontalAlignment.Right);
            recipes.ClientSizeChanged += (sender, args) => SizeRecipeColumns();
            recipes.ItemCheck += OnRecipeCheck;
            recipes.SelectedIndexChanged += (sender, args) => {
                if (!updatingRecipeList)
                    RenderRecipe();
            };
            browser.Controls.Add(recipes, 0, 5);

            var recipeActions = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                Padding = new Padding(0, 8, 0, 0),
                ColumnCount = 2,
                RowCount = 1
            };
            recipeActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            recipeActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            recipeActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            selectAllRecipes = MakeButton("Select All", 0, 0, 90, 34, false);
            selectAllRecipes.Dock = DockStyle.Fill;
            selectAllRecipes.Margin = new Padding(0, 0, 8, 0);
            selectAllRecipes.AccessibleDescription = "Check every locked recipe matching the current search and filter.";
            selectAllRecipes.Enabled = false;
            selectAllRecipes.Click += OnSelectAllRecipes;
            recipeActions.Controls.Add(selectAllRecipes, 0, 0);

            unlockRecipe = MakeButton("Quick Unlock", 0, 0, 132, 34, true);
            unlockRecipe.Dock = DockStyle.Fill;
            unlockRecipe.Margin = new Padding(0);
            unlockRecipe.Enabled = false;
            unlockRecipe.Click += OnUnlockRecipe;
            recipeActions.Controls.Add(unlockRecipe, 1, 0);
            browser.Controls.Add(recipeActions, 0, 6);

            var browserActions = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 8, 0, 0)
            };
            var refresh = MakeButton("Refresh", 0, 0, 84, 34, false);
            refresh.Margin = new Padding(0, 0, 8, 0);
            refresh.Click += (sender, args) => RefreshOwned();
            browserActions.Controls.Add(refresh);

            var close = MakeButton("Close", 0, 0, 72, 34, false);
            close.Margin = new Padding(0);
            close.DialogResult = DialogResult.Cancel;
            CancelButton = close;
            browserActions.Controls.Add(close);
            browser.Controls.Add(browserActions, 0, 7);

            var details = MakeProjectPanel(8);
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 154));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 114));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(details, 1, 0);

            var card = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                ColumnCount = 2,
                RowCount = 1
            };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            details.Controls.Add(card, 0, 0);

            recipePreview = new Panel {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 14, 6),
                AccessibleRole = AccessibleRole.Graphic
            };
            recipePreview.Paint += DrawRecipePreview;
            card.Controls.Add(recipePreview, 0, 0);

            var summary = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                ColumnCount = 1,
                RowCount = 4
            };
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
            summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            summary.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            summary.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.Controls.Add(summary, 1, 0);

            heading = ProjectLabel("", TextColor);
            heading.Font = GameArt.RecipeTitleFont(27);
            heading.UseCompatibleTextRendering = true;
            heading.AutoEllipsis = true;
            summary.Controls.Add(heading, 0, 0);

            var metadata = new TableLayoutPanel {
                Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 3, RowCount = 1
            };
            metadata.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            metadata.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            metadata.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rarity = ProjectLabel("", Muted);
            rarity.Font = new Font(Font, FontStyle.Bold);
            rarity.AccessibleName = "Recipe rarity";
            rarity.AutoSize = true;
            rarity.Margin = new Padding(0, 0, 10, 0);
            metadata.Controls.Add(rarity, 0, 0);
            earnings = ProjectLabel("", TextColor);
            earnings.Font = GameArt.RecipeTitleFont(13);
            earnings.UseCompatibleTextRendering = true;
            earnings.TextAlign = ContentAlignment.MiddleCenter;
            earnings.AccessibleName = "Safe earnings per payout";
            earnings.BackgroundImage = GameArt.RecipeEarningsTag(new Size(110, 27));
            earnings.BackgroundImageLayout = ImageLayout.Stretch;
            earnings.Visible = false;
            metadata.Controls.Add(earnings, 1, 0);
            summary.Controls.Add(metadata, 0, 1);
            effectTitle = ProjectLabel("", Accent);
            effectTitle.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
            effectTitle.Padding = new Padding(29, 0, 0, 0);
            effectTitle.Paint += DrawEffectIcon;
            summary.Controls.Add(effectTitle, 0, 2);
            effectDescription = ProjectLabel("", Muted);
            effectDescription.TextAlign = ContentAlignment.TopLeft;
            effectDescription.Padding = new Padding(29, 0, 0, 0);
            summary.Controls.Add(effectDescription, 0, 3);
            recipeStatus = ProjectLabel("", Muted);
            recipeStatus.Font = new Font(Font, FontStyle.Bold);
            details.Controls.Add(recipeStatus, 0, 1);
            ownershipLabel = ProjectLabel("", Muted);
            details.Controls.Add(ownershipLabel, 0, 2);

            var subStatPanel = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 1, 0, 4),
                ColumnCount = 1,
                RowCount = 2
            };
            subStatPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            subStatPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            subStatPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var subStatTitle = ProjectLabel("Sub-stats", TextColor);
            subStatTitle.Font = new Font(Font, FontStyle.Bold);
            subStatPanel.Controls.Add(subStatTitle, 0, 0);
            subStatIcons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(20, 20) };
            foreach (string icon in BuildSubStats.Icons)
                subStatIcons.Images.Add(icon, GameArt.ButtonImage(icon, 20));
            subStats = new ListView {
                Dock = DockStyle.Fill,
                View = View.Details,
                HeaderStyle = ColumnHeaderStyle.None,
                FullRowSelect = true,
                MultiSelect = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = InputBackground,
                ForeColor = TextColor,
                SmallImageList = subStatIcons,
                AccessibleName = "Build sub-stats",
                Margin = new Padding(0)
            };
            subStats.Columns.Add("Sub-stat", 350);
            subStats.Columns.Add("Amount", 94, HorizontalAlignment.Right);
            subStats.ClientSizeChanged += (sender, args) => SizeSubStatColumns();
            subStatPanel.Controls.Add(subStats, 0, 1);
            details.Controls.Add(subStatPanel, 0, 3);

            readiness = ProjectLabel("", Muted);
            details.Controls.Add(readiness, 0, 4);
            materials = MakeMaterialsGrid();
            details.Controls.Add(materials, 0, 5);

            var actions = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 8, 0, 0)
            };
            addBuild = MakeButton("Add Build", 0, 0, 111, 34, true);
            addBuild.Margin = new Padding(0);
            addBuild.Enabled = false;
            addBuild.Click += OnAddBuild;
            actions.Controls.Add(addBuild);

            addMissing = MakeButton("Add Missing Bricks", 0, 0, 155, 34, false);
            addMissing.Margin = new Padding(0, 0, 8, 0);
            addMissing.Enabled = false;
            addMissing.Click += OnAddMissing;
            actions.Controls.Add(addMissing);

            details.Controls.Add(actions, 0, 6);

            note = ProjectLabel("", Muted);
            note.AccessibleName = "Project status";
            details.Controls.Add(note, 0, 7);

            gameTimer = new Timer { Interval = 1000 };
            gameTimer.Tick += OnGameStateChanged;
            Shown += (sender, args) => {
                RefreshOwned();
                gameTimer.Start();
            };
            FormClosed += (sender, args) => gameTimer.Dispose();
            Disposed += (sender, args) => {
                if (previewBitmap != null)
                    previewBitmap.Dispose();
                heading.Font.Dispose();
                earnings.Font.Dispose();
                earnings.BackgroundImage.Dispose();
                subStatIcons.Dispose();
            };
        }

        private void SizeSubStatColumns()
        {
            subStats.Columns[0].Width = Math.Max(0, subStats.ClientSize.Width - subStats.Columns[1].Width - 1);
        }

        private void RenderSubStats(BuildRecipe recipe)
        {
            subStats.BeginUpdate();
            try
            {
                subStats.Items.Clear();
                if (recipe != null)
                {
                    OwnedBuild copy = builds == null ? null : builds.GetOwnedCopies(recipe.Id).FirstOrDefault();
                    IEnumerable<BuildPlacement> placements = copy == null
                        ? (IEnumerable<BuildPlacement>)recipe.Placements : copy.Placements;
                    foreach (BuildSubStat stat in BuildSubStats.Calculate(placements))
                    {
                        var row = new ListViewItem(stat.Name) { ImageKey = stat.Icon, UseItemStyleForSubItems = false };
                        row.SubItems.Add(stat.Value).ForeColor = Accent;
                        subStats.Items.Add(row);
                    }
                    if (subStats.Items.Count == 0)
                        subStats.Items.Add(new ListViewItem("No sub-stats") { ForeColor = Muted });
                }
            }
            finally { subStats.EndUpdate(); }
            SizeSubStatColumns();
        }

        private void SetRecipePreview(BuildRecipe recipe)
        {
            if (previewRecipe == recipe)
                return;
            previewRecipe = recipe;
            if (previewBitmap != null)
            {
                previewBitmap.Dispose();
                previewBitmap = null;
            }
            recipePreview.AccessibleName = recipe == null ? "" : recipe.Name + " build preview";
            recipePreview.Invalidate();
        }

        private void DrawRecipePreview(object sender, PaintEventArgs args)
        {
            if (previewRecipe == null || recipePreview.Width < 42 || recipePreview.Height < 42)
                return;
            int width = Math.Min(recipePreview.Width - 8, (recipePreview.Height - 8) * 492 / 428);
            int height = width * 428 / 492;
            var frame = new Rectangle((recipePreview.Width - width) / 2, (recipePreview.Height - height) / 2, width, height);
            var modelSize = new Size((width - 24) * 2, (height - 22) * 2);
            if (previewBitmap == null || previewBitmap.Size != modelSize)
            {
                Bitmap rendered = RecipeArtwork.Render(previewRecipe.Placements, modelSize);
                Bitmap previous = previewBitmap;
                previewBitmap = rendered;
                if (previous != null)
                    previous.Dispose();
            }
            Image rarityBadge = GameArt.Image("RecipeRarity" + previewRecipe.Rarity + ".png");
            GameArt.DrawRecipeBlueprint(args.Graphics, frame, previewBitmap, rarityBadge);
        }

        private void DrawEffectIcon(object sender, PaintEventArgs args)
        {
            if (effectArt == null)
                return;
            int size = Math.Min(effectTitle.Padding.Left - 8, effectTitle.Height - 4);
            if (size > 0)
            {
                args.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                GameArt.DrawFit(args.Graphics, effectArt,
                    new RectangleF(0, (effectTitle.Height - size) / 2f, size, size));
            }
        }

        private Panel MakeRecipeBookHeader()
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 4) };
            panel.Paint += (sender, args) => {
                using (var pattern = new TextureBrush(GameArt.Image("BlueprintGrid.png")))
                using (var tint = new SolidBrush(Color.FromArgb(216, 31, 30, 64)))
                {
                    args.Graphics.FillRectangle(pattern, panel.ClientRectangle);
                    args.Graphics.FillRectangle(tint, panel.ClientRectangle);
                }
            };
            panel.Controls.Add(new PictureBox {
                Bounds = new Rectangle(8, 7, 28, 28),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = GameArt.Image("RecipeBookIcon.png"),
                BackColor = Color.Transparent,
                TabStop = false
            });
            var label = MakeLabel("Build recipes", 46, 0, 184, 42, TextColor);
            label.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
            label.BackColor = Color.Transparent;
            panel.Controls.Add(label);
            return panel;
        }

        private static ComboBox MakeRecipePicker(string accessibleName, params string[] options)
        {
            var picker = new ComboBox {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = InputBackground,
                ForeColor = TextColor,
                AccessibleName = accessibleName,
                Margin = new Padding(0, 2, 0, 0)
            };
            picker.Items.AddRange(options);
            picker.SelectedIndex = 0;
            return picker;
        }

        private static TableLayoutPanel MakeProjectPanel(int rows)
        {
            var panel = new TableLayoutPanel {
                Dock = DockStyle.Fill,
                BackColor = Surface,
                Padding = new Padding(14),
                Margin = new Padding(0),
                ColumnCount = 1,
                RowCount = rows
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return panel;
        }

        private static Label ProjectLabel(string text, Color color)
        {
            var label = MakeLabel(text, 0, 0, 0, 0, color);
            label.Dock = DockStyle.Fill;
            label.Margin = new Padding(0);
            return label;
        }

        private static DataGridView MakeMaterialsGrid()
        {
            var grid = new DataGridView {
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = InputBackground,
                GridColor = Edge,
                BorderStyle = BorderStyle.FixedSingle,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                RowHeadersVisible = false,
                ColumnHeadersHeight = 32,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                EnableHeadersVisualStyles = false,
                ReadOnly = true,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AccessibleName = "Build brick requirements"
            };
            grid.RowTemplate.Height = 27;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextColor;
            grid.DefaultCellStyle.BackColor = InputBackground;
            grid.DefaultCellStyle.ForeColor = TextColor;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(74, 54, 99);
            grid.DefaultCellStyle.SelectionForeColor = TextColor;
            grid.DefaultCellStyle.NullValue = "—";

            grid.Columns.Add(MaterialColumn("Brick", 52, 138, false));
            grid.Columns.Add(MaterialColumn("Need", 15, 51, true));
            grid.Columns.Add(MaterialColumn("Owned", 16, 54, true));
            grid.Columns.Add(MaterialColumn("Missing", 17, 61, true));
            grid.CellPainting += PaintBrickMaterial;
            return grid;
        }

        private static void PaintBrickMaterial(object sender, DataGridViewCellPaintingEventArgs args)
        {
            if (args.RowIndex < 0 || args.ColumnIndex != 0)
                return;
            var grid = (DataGridView)sender;
            object material = grid[args.ColumnIndex, args.RowIndex].Tag;
            if (!(material is ulong))
                return;

            args.Paint(args.ClipBounds, DataGridViewPaintParts.Background |
                DataGridViewPaintParts.SelectionBackground | DataGridViewPaintParts.Border);
            Rectangle bounds = args.CellBounds;
            var swatch = new Rectangle(bounds.X + 8, bounds.Y + (bounds.Height - 13) / 2, 14, 13);
            GameArt.DrawSwatch(args.Graphics, (ulong)material, swatch);
            bool selected = (args.State & DataGridViewElementStates.Selected) != 0;
            TextRenderer.DrawText(args.Graphics, Convert.ToString(args.FormattedValue, CultureInfo.CurrentCulture),
                args.CellStyle.Font ?? grid.Font, new Rectangle(bounds.X + 28, bounds.Y, bounds.Width - 31, bounds.Height),
                selected ? args.CellStyle.SelectionForeColor : args.CellStyle.ForeColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            args.Handled = true;
        }

        private static DataGridViewTextBoxColumn MaterialColumn(string title, float weight, int minimum, bool numeric)
        {
            var column = new DataGridViewTextBoxColumn {
                Name = title,
                HeaderText = title,
                FillWeight = weight,
                MinimumWidth = minimum,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            if (numeric)
            {
                column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                column.DefaultCellStyle.Padding = new Padding(0, 0, 6, 0);
                column.DefaultCellStyle.Format = "N0";
            }
            return column;
        }

        private BuildRecipe SelectedRecipe
        {
            get { return recipes.SelectedItems.Count == 0 ? null : (BuildRecipe)recipes.SelectedItems[0].Tag; }
        }

        private void SizeRecipeColumns()
        {
            int width = recipes.ClientSize.Width - 1;
            int statusWidth = TextRenderer.MeasureText("Unavailable", recipes.Font).Width + 11;
            recipes.Columns[0].Width = Math.Max(0, width - statusWidth);
            recipes.Columns[1].Width = statusWidth;
        }

        private void FilterRecipes()
        {
            string query = search.Text.Trim();
            BuildRecipe selected = SelectedRecipe;
            var filter = (RecipeFilter)recipeFilter.SelectedIndex;
            IEnumerable<BuildRecipe> matching = BuildCatalog.Value.Where(recipe =>
                recipe.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 &&
                MatchesRecipeFilter(recipe, filter));
            // Stable ordering keeps the catalog's name order within each rarity.
            switch ((RecipeSort)recipeSort.SelectedIndex)
            {
                case RecipeSort.HighestRarity:
                    matching = matching.OrderByDescending(recipe => recipe.Rarity);
                    break;
                case RecipeSort.LowestRarity:
                    matching = matching.OrderBy(recipe => recipe.Rarity);
                    break;
            }
            BuildRecipe[] filtered = matching.ToArray();
            updatingRecipeList = true;
            recipes.BeginUpdate();
            try
            {
                recipes.Items.Clear();
                recipes.Items.AddRange(filtered.Select(MakeRecipeRow).ToArray());
                results.Text = unlocked == null && (filter == RecipeFilter.Locked || filter == RecipeFilter.Unlocked)
                    ? "Recipe book status unavailable"
                    : filtered.Length.ToString("N0", CultureInfo.CurrentCulture) +
                    (filtered.Length == 1 ? " recipe" : " recipes");
                if (filtered.Length > 0)
                {
                    int index = Math.Max(0, Array.IndexOf(filtered, selected));
                    recipes.Items[index].Selected = true;
                    recipes.Items[index].Focused = true;
                    recipes.EnsureVisible(index);
                }
            }
            finally
            {
                recipes.EndUpdate();
                updatingRecipeList = false;
            }
            SizeRecipeColumns();
            RenderRecipe();
        }

        private bool MatchesRecipeFilter(BuildRecipe recipe, RecipeFilter filter)
        {
            if (filter == RecipeFilter.All)
                return true;
            if (filter == RecipeFilter.Unavailable)
                return !recipe.Enabled;
            return recipe.Enabled && unlocked != null &&
                unlocked.Contains(recipe.Id) == (filter == RecipeFilter.Unlocked);
        }

        private ListViewItem MakeRecipeRow(BuildRecipe recipe)
        {
            bool known = unlocked != null && unlocked.Contains(recipe.Id);
            string state = !recipe.Enabled ? "Unavailable" : unlocked == null ? "" : known ? "Unlocked" : "Locked";
            var row = new ListViewItem(recipe.Name) {
                Tag = recipe,
                UseItemStyleForSubItems = false,
                Checked = recipe.Enabled && unlocked != null && (known || queuedRecipes.Contains(recipe.Id))
            };
            row.SubItems.Add(state).ForeColor = recipe.Enabled && known ? Good : Muted;
            return row;
        }

        private bool CanUnlock(BuildRecipe recipe)
        {
            return recipe != null && recipe.Enabled && unlocked != null && !unlocked.Contains(recipe.Id);
        }

        private void OnRecipeCheck(object sender, ItemCheckEventArgs args)
        {
            if (updatingRecipeList)
                return;
            var recipe = (BuildRecipe)recipes.Items[args.Index].Tag;
            if (!CanUnlock(recipe))
            {
                args.NewValue = args.CurrentValue;
                return;
            }
            if (args.NewValue == CheckState.Checked)
                queuedRecipes.Add(recipe.Id);
            else
                queuedRecipes.Remove(recipe.Id);
            UpdateQuickUnlock(SelectedRecipe);
        }

        private void OnSelectAllRecipes(object sender, EventArgs args)
        {
            updatingRecipeList = true;
            recipes.BeginUpdate();
            try
            {
                foreach (ListViewItem row in recipes.Items)
                {
                    var recipe = (BuildRecipe)row.Tag;
                    if (CanUnlock(recipe) && queuedRecipes.Add(recipe.Id))
                        row.Checked = true;
                }
            }
            finally
            {
                recipes.EndUpdate();
                updatingRecipeList = false;
            }
            UpdateQuickUnlock(SelectedRecipe);
        }

        private void RefreshOwned()
        {
            gameRunning = WalletEditor.IsGameRunning();
            owned = null;
            builds = null;
            unlocked = null;
            if (editor == null || save == null || save.Path == null)
            {
                ShowNote("Select an existing save in the trainer to compare your inventory.", Muted);
            }
            else
            {
                ShowNote(gameRunning ? "Close the game to edit " + save + "." :
                    "Editing " + save + " creates a backup.", Muted);
                try { LoadOwned(); }
                catch (Exception error) { ShowNote(error.Message, Bad); }
                try { builds = editor.ReadBuildOwnership(VerifiedSavePath(editor, save)); }
                catch (Exception error) { ShowNote(error.Message, Bad); }
                try { unlocked = editor.ReadUnlockedRecipeIds(VerifiedSavePath(editor, save)); }
                catch (Exception error) { ShowNote(error.Message, Bad); }
            }
            if (unlocked != null)
                queuedRecipes.ExceptWith(unlocked);
            FilterRecipes();
        }

        private void LoadOwned()
        {
            string path = VerifiedSavePath(editor, save);
            owned = editor.ReadColoredBricks(path).ToDictionary(
                brick => ulong.Parse(brick.Id, CultureInfo.InvariantCulture), brick => brick.Count);
        }

        private void RenderRecipe()
        {
            BuildRecipe selected = SelectedRecipe;
            SetRecipePreview(selected);
            RenderSubStats(selected);
            materials.Rows.Clear();
            missing = 0;
            if (selected == null)
            {
                heading.Text = "No matching recipes";
                rarity.Text = "";
                earnings.Visible = false;
                effectArt = null;
                effectTitle.Text = "";
                effectDescription.Text = "";
                recipeStatus.Text = "";
                ownershipLabel.Text = "";
                readiness.Text = "";
                UpdateActionButtons(null);
                return;
            }

            heading.Text = selected.Name;
            rarity.Text = RarityNames[selected.Rarity];
            rarity.ForeColor = RarityColors[selected.Rarity];
            earnings.Text = selected.Enabled ? "EARNS  $" + EarningsByRarity[selected.Rarity] : "";
            earnings.Visible = selected.Enabled;
            effectTitle.Text = selected.EffectTitle ?? PassiveIncome;
            effectArt = GameArt.Image(string.Equals(effectTitle.Text, PassiveIncome, StringComparison.Ordinal)
                ? "Cash.png" : "RecipeModifier.png");
            effectTitle.Invalidate();
            effectDescription.Text = selected.EffectDescription ?? "Produces cash when placed in the lair safe.";
            ShowRecipeStatus(selected);
            ShowOwnership(selected);
            int total = 0;
            foreach (RecipeIngredient ingredient in selected.Ingredients)
            {
                total += ingredient.Count;
                if (owned == null)
                {
                    int unownedRow = materials.Rows.Add(ingredient.Name, ingredient.Count, null, null);
                    materials.Rows[unownedRow].Cells["Brick"].Tag = ingredient.Id;
                    continue;
                }

                int count;
                owned.TryGetValue(ingredient.Id, out count);
                int shortage = Math.Max(0, ingredient.Count - Math.Max(0, count));
                missing += shortage;
                int row = materials.Rows.Add(ingredient.Name, ingredient.Count, count, shortage);
                materials.Rows[row].Cells["Brick"].Tag = ingredient.Id;
                materials.Rows[row].Cells["Missing"].Style.ForeColor = shortage > 0 ? Accent : Good;
            }
            materials.ClearSelection();
            readiness.ForeColor = owned != null && missing == 0 ? Good : Muted;
            readiness.Text = owned == null ? total + " bricks required" : missing == 0 ?
                "All " + total + " required bricks in inventory" :
                missing + " of " + total + " bricks missing  •  " + save;
            UpdateActionButtons(selected);
        }

        private void ShowRecipeStatus(BuildRecipe recipe)
        {
            bool known = unlocked != null && unlocked.Contains(recipe.Id);
            recipeStatus.ForeColor = recipe.Enabled && known ? Good : Muted;
            recipeStatus.Text = !recipe.Enabled ? "Unavailable in this game version" :
                unlocked == null ? "Recipe status unavailable" : known ? "Recipe unlocked" : "Recipe locked";
        }

        private void ShowOwnership(BuildRecipe recipe)
        {
            ownershipLabel.ForeColor = Muted;
            if (!recipe.Enabled)
            {
                ownershipLabel.Text = "";
                return;
            }
            if (builds == null)
            {
                ownershipLabel.Text = "Ownership unavailable";
                return;
            }
            if (builds.HasPendingBuild && builds.PendingRecipeId == recipe.Id)
            {
                ownershipLabel.Text = "Awaiting placement at the lair playmat";
                ownershipLabel.ForeColor = Accent;
                return;
            }
            if (builds.OwnedRecipeIds.Contains(recipe.Id))
            {
                ownershipLabel.Text = "Owned in " + save;
                ownershipLabel.ForeColor = Good;
                return;
            }
            ownershipLabel.Text = builds.HasPendingBuild
                ? "Not owned  •  Place or sell the pending build first"
                : "Not owned in " + save;
        }

        private void UpdateActionButtons(BuildRecipe recipe)
        {
            UpdateQuickUnlock(recipe);
            addMissing.Enabled = recipe != null && owned != null && missing > 0 && !gameRunning;
            addBuild.Enabled = recipe != null && recipe.Enabled && builds != null && !builds.OwnedRecipeIds.Contains(recipe.Id) &&
                !builds.HasPendingBuild && !gameRunning;
        }

        private void UpdateQuickUnlock(BuildRecipe selected)
        {
            selectAllRecipes.Enabled = unlocked != null && recipes.Items.Cast<ListViewItem>()
                .Select(row => (BuildRecipe)row.Tag)
                .Any(recipe => CanUnlock(recipe) && !queuedRecipes.Contains(recipe.Id));
            int count = queuedRecipes.Count;
            unlockRecipe.Text = count == 0 ? "Quick Unlock" : "Unlock " + count + " Checked";
            unlockRecipe.Enabled = !gameRunning && unlocked != null && (count > 0 || CanUnlock(selected));
        }

        private void OnGameStateChanged(object sender, EventArgs args)
        {
            bool active = WalletEditor.IsGameRunning();
            if (active != gameRunning)
                RefreshOwned();
        }

        private void OnUnlockRecipe(object sender, EventArgs args)
        {
            if (unlocked == null || gameRunning)
                return;
            BuildRecipe selected = SelectedRecipe;
            int[] requested = queuedRecipes.Count > 0 ? queuedRecipes.OrderBy(id => id).ToArray() :
                CanUnlock(selected) ? new[] { selected.Id } : new int[0];
            if (requested.Length == 0)
                return;
            try
            {
                string path = VerifiedSavePath(editor, save);
                string backup = editor.UnlockRecipes(path, requested);
                RecipeChanged |= backup != null;
                unlocked = editor.ReadUnlockedRecipeIds(path);
                queuedRecipes.ExceptWith(unlocked);
                if ((RecipeFilter)recipeFilter.SelectedIndex == RecipeFilter.Locked)
                    recipeFilter.SelectedIndex = (int)RecipeFilter.All;
                else
                    FilterRecipes();
                if (!requested.All(unlocked.Contains))
                    throw new IOException("The progression save changed. Refresh to check the recipe status.");
                string count = requested.Length == 1 ? "Recipe" : requested.Length + " recipes";
                ShowNote(count + (backup == null ? " already unlocked in " + save + "." :
                    " unlocked in " + save + ". Backup saved."), Good);
            }
            catch (Exception error)
            {
                ShowNote(error.Message, Bad);
            }
        }

        private void OnAddMissing(object sender, EventArgs args)
        {
            BuildRecipe selected = SelectedRecipe;
            if (selected == null || owned == null)
                return;
            try
            {
                string path = VerifiedSavePath(editor, save);
                var requirements = selected.Ingredients.ToDictionary(brick => brick.Id, brick => brick.Count);
                string backup = editor.EnsureColoredBricks(path, requirements);
                InventoryChanged |= backup != null;
                LoadOwned();
                RenderRecipe();
                if (missing != 0)
                    throw new IOException("The inventory changed after the edit. Refresh to check the remaining bricks.");
                ShowNote(backup == null ? save + " already has every brick for this build." :
                    "Added the missing bricks to " + save + ". Backup saved.", Good);
            }
            catch (Exception error)
            {
                ShowNote(error.Message, Bad);
            }
        }

        private void OnAddBuild(object sender, EventArgs args)
        {
            BuildRecipe selected = SelectedRecipe;
            if (selected == null || !selected.Enabled || builds == null)
                return;
            try
            {
                string path = VerifiedSavePath(editor, save);
                string backup = editor.AddBuild(path, selected.Id, selected.Placements.ToArray(), null);
                BuildAdded |= backup != null;
                builds = editor.ReadBuildOwnership(path);
                RenderRecipe();
                if (!builds.OwnedRecipeIds.Contains(selected.Id))
                    throw new IOException("The build save changed. Refresh to check ownership.");
                ShowNote(backup == null ? save + " already owns this build." :
                    "Build added. Open the lair playmat to place it. Backup saved.", Good);
            }
            catch (Exception error)
            {
                ShowNote(error.Message, Bad);
            }
        }

        private void ShowNote(string message, Color color)
        {
            note.Text = message;
            note.ForeColor = color;
        }
    }
}
