using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

internal sealed partial class TrainerForm
{
    private EnemyTypesForm enemyTypes;

    private void OnEnemyTypes(object sender, EventArgs args)
    {
        using (var dialog = new EnemyTypesForm(this))
        {
            enemyTypes = dialog;
            try { dialog.ShowDialog(this); }
            finally { enemyTypes = null; }
        }
    }

    private sealed class EnemyTypesForm : Form
    {
        private readonly TrainerForm owner;
        private readonly Label hint;
        private readonly Label status;
        private readonly Dictionary<EnemySuppression, CheckBox> switches = new Dictionary<EnemySuppression, CheckBox>();
        private bool syncing;

        public EnemyTypesForm(TrainerForm owner)
        {
            this.owner = owner;
            Font = owner.Font;
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(372, 455);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "Enemy Types | " + AppName;
            Icon = GameArt.WindowIcon;
            BackColor = Background;
            ForeColor = TextColor;

            hint = MakeLabel("", 16, 12, 340, 44, Muted);
            Controls.Add(hint);

            AddType(EnemySuppression.AllEnemies, "No enemies", "EnemyAlert.png", owner.trainer.SetNoEnemies);
            AddType(EnemySuppression.Fans, "No Fans", "Fan.png", owner.trainer.SetNoFans);
            AddType(EnemySuppression.YouTubers, "No YouTubers", "YouTuber.png", owner.trainer.SetNoYouTubers);
            AddType(EnemySuppression.Lawyers, "No Lawyers", "Lawyer.png", owner.trainer.SetNoLawyers);
            AddType(EnemySuppression.Investigators, "No Internet Investigators", "Investigator.png", owner.trainer.SetNoInvestigators);
            AddType(EnemySuppression.Elites, "No Elites", "Elite.png", owner.trainer.SetNoElites);

            status = MakeLabel("", 16, 365, 340, 35, Muted);
            status.AccessibleName = "Enemy cheat status";
            Controls.Add(status);
            var close = MakeButton("Close", 272, 406, 84, 34, false);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;
            RefreshState();
        }

        private void AddType(EnemySuppression filter, string title, string icon, Func<bool, string> apply)
        {
            bool pixelArt = filter != EnemySuppression.Elites && filter != EnemySuppression.AllEnemies;
            var toggle = MakeToggle(title, 16, 62 + switches.Count * 50, 340, icon, pixelArt);
            switches.Add(filter, toggle);
            toggle.CheckedChanged += (sender, args) => {
                if (syncing)
                    return;
                syncing = true;
                try
                {
                    owner.ChangeCheat(toggle, apply, (message, color) => {
                        status.Text = message;
                        status.ForeColor = color;
                        owner.ShowStatus(message, color);
                    });
                }
                finally { syncing = false; }
                RefreshHint();
            };
            Controls.Add(toggle);
        }

        private void RefreshHint()
        {
            hint.Text = !owner.gameRunning ? "Start the game to enable these switches." :
                owner.trainer.IsEnemyFilterSelected(EnemySuppression.AllEnemies)
                    ? "No enemies is on. Individual choices apply when it’s off."
                    : "Turn on a switch to block new spawns of that type.";
        }

        internal void RefreshState()
        {
            RefreshHint();
            syncing = true;
            try
            {
                foreach (var entry in switches)
                {
                    CheckBox toggle = entry.Value;
                    toggle.Enabled = owner.gameRunning;
                    bool selected = owner.trainer.IsEnemyFilterSelected(entry.Key);
                    if (toggle.Checked != selected)
                    {
                        toggle.Checked = selected;
                        toggle.Text = toggle.AccessibleName + (selected ? "   On" : "   Off");
                        toggle.Invalidate();
                    }
                }
                if (!owner.gameRunning)
                    status.Text = "";
            }
            finally { syncing = false; }
        }
    }
}
