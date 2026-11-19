using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class TrainerForm
{
    private void ConfigureResponsiveLayout(Control banner, Button refresh, Button projects,
        Panel greyPanel, Panel coloredPanel, Panel live, Button enemyPicker)
    {
        const AnchorStyles stretch = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        const AnchorStyles alignRight = AnchorStyles.Top | AnchorStyles.Right;
        greyCount.Anchor = stretch;
        setGrey.Anchor = alignRight;
        maxGrey.Anchor = alignRight;
        brickPicker.Anchor = stretch;
        setBrick.Anchor = stretch;
        fillBricks.Anchor = stretch;
        maxBricks.Anchor = alignRight;
        foreach (Panel section in new[] { greyPanel, coloredPanel })
            foreach (Label label in section.Controls.OfType<Label>())
                if (label.Right == section.Width - 16)
                    label.Anchor = stretch;
        foreach (Control child in live.Controls)
            if (child is Label || child is CheckBox)
                child.Anchor = stretch;
        enemyPicker.Anchor = alignRight;
        status.AutoEllipsis = true;

        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var contents = new Panel { Size = ClientSize };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = ClientSize.Height - status.Top };
        int defaultWidth = ClientSize.Width;
        int margin = greyPanel.Left;
        int columnGap = live.Left - greyPanel.Right;
        int sectionGap = coloredPanel.Top - greyPanel.Bottom;
        int bottomGap = status.Top - Math.Max(coloredPanel.Bottom, live.Bottom);
        int leftColumn = greyPanel.Width;
        int bannerHeight = banner.Height;
        int panelsTop = greyPanel.Top;
        int toolbarTop = refresh.Top;
        int pickerTop = savePicker.Top;
        int pickerWidth = savePicker.Width;
        int refreshGap = refresh.Left - savePicker.Right;
        int projectGap = projects.Left - refresh.Right;
        int toolbarRight = projects.Right;
        int rowHeight = refresh.Height + 8;
        Label saveLabel = Controls.OfType<Label>().Single(label => label.Text == "Save slot");

        SuspendLayout();
        try
        {
            foreach (Control child in Controls.Cast<Control>().ToArray())
                if (child != status)
                    contents.Controls.Add(child);
            viewport.Controls.Add(contents);
            footer.Controls.Add(status);
            Controls.Add(viewport);
            Controls.Add(footer);
        }
        finally { ResumeLayout(); }

        // Scrollbars take space away from the controls. Measure without one first,
        // then leave space for it if the current height requires vertical scrolling.
        Action update = () =>
        {
            int width = viewport.Width;
            if (width < 1 || viewport.Height < 1)
                return;
            TrainerLayoutSize size = MeasureTrainerLayout(width, defaultWidth, margin, leftColumn,
                bannerHeight, panelsTop, toolbarRight, rowHeight, gameState.Width, greyPanel.Height,
                coloredPanel.Height, live.Height, sectionGap, bottomGap);
            if (size.Height > viewport.Height)
            {
                width -= SystemInformation.VerticalScrollBarWidth;
                size = MeasureTrainerLayout(width, defaultWidth, margin, leftColumn,
                    bannerHeight, panelsTop, toolbarRight, rowHeight, gameState.Width, greyPanel.Height,
                    coloredPanel.Height, live.Height, sectionGap, bottomGap);
            }

            int x = Math.Max(0, (width - size.Width) / 2);
            int right = x + size.Width - margin;
            int top = toolbarTop + size.BannerHeight - bannerHeight;
            int panelWidth = size.StackSections ? size.Width - 2 * margin :
                leftColumn + (size.Width - defaultWidth) * 2 / 3;

            contents.SuspendLayout();
            try
            {
                banner.SetBounds(x, 0, size.Width, size.BannerHeight);
                saveLabel.Location = new Point(x + margin, top);
                savePicker.SetBounds(x + margin + saveLabel.Width + 4, top + pickerTop - toolbarTop,
                    pickerWidth, savePicker.Height);
                refresh.Location = new Point(size.ActionsBelow ? x + margin : savePicker.Right + refreshGap,
                    top + (size.ActionsBelow ? rowHeight : 0));
                projects.Location = new Point(refresh.Right + projectGap, refresh.Top);
                gameState.Location = new Point(right - gameState.Width, top + (size.StatusBelow ? rowHeight : 0));
                greyPanel.SetBounds(x + margin, size.PanelsTop, panelWidth, greyPanel.Height);
                coloredPanel.SetBounds(greyPanel.Left, greyPanel.Bottom + sectionGap, panelWidth, coloredPanel.Height);
                int liveLeft = size.StackSections ? greyPanel.Left : greyPanel.Right + columnGap;
                int liveTop = size.StackSections ? coloredPanel.Bottom + sectionGap : greyPanel.Top;
                live.SetBounds(liveLeft, liveTop, right - liveLeft, live.Height);
                contents.Size = new Size(x + size.Width, size.Height);
                status.SetBounds(x + margin, 0, Math.Max(1, Math.Min(right, width - margin) - x - margin), status.Height);
            }
            finally { contents.ResumeLayout(); }
        };
        viewport.Resize += (sender, args) => update();
        update();
    }

    private struct TrainerLayoutSize
    {
        internal int Width;
        internal int Height;
        internal int BannerHeight;
        internal int PanelsTop;
        internal bool ActionsBelow;
        internal bool StatusBelow;
        internal bool StackSections;
    }

    private static TrainerLayoutSize MeasureTrainerLayout(int width, int defaultWidth, int margin, int leftColumn,
        int bannerHeight, int panelsTop, int toolbarRight, int rowHeight, int gameStateWidth,
        int greyHeight, int coloredHeight, int liveHeight, int sectionGap, int bottomGap)
    {
        // Cap wide content to keep related controls within comfortable reach.
        int contentWidth = Math.Max(leftColumn + 2 * margin, Math.Min(defaultWidth + 350, width));
        bool actionsBelow = contentWidth < toolbarRight + margin;
        bool statusBelow = !actionsBelow && contentWidth < toolbarRight + gameStateWidth + margin + 8;
        bool stack = contentWidth < defaultWidth - 40;
        int artHeight = bannerHeight + Math.Max(-34, Math.Min(40, (contentWidth - defaultWidth) / 7));
        int top = panelsTop + artHeight - bannerHeight + (actionsBelow || statusBelow ? rowHeight : 0);
        int inventoryHeight = greyHeight + sectionGap + coloredHeight;
        int panelHeight = stack ? inventoryHeight + sectionGap + liveHeight : Math.Max(inventoryHeight, liveHeight);
        return new TrainerLayoutSize {
            Width = contentWidth,
            Height = top + panelHeight + bottomGap,
            BannerHeight = artHeight,
            PanelsTop = top,
            ActionsBelow = actionsBelow,
            StatusBelow = statusBelow,
            StackSections = stack
        };
    }
}
