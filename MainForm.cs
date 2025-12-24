using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BG3BackupManager
{
    public partial class MainForm : Form
    {
        // Windows API for global keyboard hook
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int VK_S = 0x53;
        private const int VK_L = 0x4C;
        private const int VK_CONTROL = 0x11;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _keyboardProc = null!;
        private IntPtr _hookID = IntPtr.Zero;

        private ComboBox cboChooseCharacter = null!;
        private Label lblCurrentSave = null!;
        private ComboBox cboBackups = null!;
        private Button btnBackup = null!;
        private Button btnRestore = null!;
        private Button btnDeleteBackup = null!;
        private Label lblPlaythrough = null!;
        private Label lblBackup = null!;
        private Label lblBackupStatus = null!;
        private LinkLabel lnkDonate = null!;
        private GroupBox grpChooseCharacter = null!;
        private GroupBox grpBackup = null!;
        private GroupBox grpRestore = null!;
        private Button btnRestoreAfterDeath = null!;
        private Button btnSettings = null!;
        private PictureBox picBackupThumbnail = null!;
        
        // Steam Cloud warning panel
        private Panel pnlSteamCloudWarning = null!;
        private System.Windows.Forms.Timer steamCloudCheckTimer = null!;
        
        // Form width constants
        private const int NormalFormWidth = 630;
        private const int ExpandedFormWidth = 945; // 630 * 1.5

        private string bg3SavePath = string.Empty;
        private string backupRootPath = string.Empty;
        private string bg3ProfilePath = string.Empty;
        private Dictionary<string, PlaythroughInfo> playthroughs = new();
        private Dictionary<string, BackupInfo> backups = new();


        // ComboBox item wrapper so we don't rely on fragile display-string matching
        private sealed class BackupComboItem
        {
            public BackupInfo Backup { get; }
            public string Display { get; }

            public BackupComboItem(BackupInfo backup, string display)
            {
                Backup = backup;
                Display = display;
            }

            public override string ToString() => Display;
        }
        private FileSystemWatcher? saveWatcher;
        private string lastSelectedProfile = string.Empty;
        private string lastSelectedBackup = string.Empty;
        private Label lblQuickSave = null!;
        private Label lblQuickSaveKey = null!;
        private Label lblQuickRestore = null!;
        private PlaythroughInfo? selectedCharacter = null;
        
        // Restoration tracking - per character
        private DateTime? lastRestorationTimestamp = null;
        private string? lastRestoredBackupId = null;

        // Dark mode colors
        private Color lightBackground = Color.FromArgb(243, 243, 243);
        private Color darkBackground = Color.FromArgb(32, 32, 32);
        private Color lightForeground = Color.FromArgb(32, 32, 32);
        private Color darkForeground = Color.FromArgb(240, 240, 240);
        private Color lightControlBg = Color.White;
        private Color darkControlBg = Color.FromArgb(45, 45, 45);
        private Color lightBorder = Color.FromArgb(200, 200, 200);
        private Color darkBorder = Color.FromArgb(80, 80, 80);

        public MainForm()
        {
            InitializeComponent();
            InitializePaths();
            LoadPlaythroughs();
            InitializeSaveWatcher();
            
            // Set up global keyboard hook for Ctrl+S quicksave / Ctrl+L quick-restore
            _keyboardProc = HookCallback;
            _hookID = SetHook(_keyboardProc);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            
            // Check if user has acknowledged Steam Cloud warning
            CheckSteamCloudStatus();
            
            // Check if we have a saved last character selection
            try
            {
                var lastCharacterFolder = Properties.Settings.Default.LastSelectedCharacter;
                
                if (!string.IsNullOrEmpty(lastCharacterFolder))
                {
                    // Try to find the saved character
                    var savedCharacter = playthroughs.Values
                        .FirstOrDefault(pt => pt.FolderName == lastCharacterFolder);
                    
                    if (savedCharacter != null)
                    {
                        // Character still exists - load it directly
                        selectedCharacter = savedCharacter;
                        grpBackup.Enabled = true;
                        grpRestore.Enabled = true;
                        LoadCharacterData(savedCharacter);
                        return;
                    }
                }
            }
            catch
            {
                // Setting doesn't exist yet
            }
            
            // No saved character - auto-select first one from dropdown if available
            if (playthroughs.Count > 0 && cboChooseCharacter.Items.Count > 0)
            {
                // The dropdown SelectedIndexChanged event will handle loading the character
                cboChooseCharacter.SelectedIndex = 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                saveWatcher?.Dispose();
                steamCloudCheckTimer?.Stop();
                steamCloudCheckTimer?.Dispose();
                
                // Unhook global keyboard hook
                if (_hookID != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookID);
                    _hookID = IntPtr.Zero;
                }
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            bool isDark = Properties.Settings.Default.DarkMode;
            
            this.Text = "Chairface's Baldurs Gate 3 Honor Saver";
            this.Size = new Size(630, 420);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimumSize = new Size(630, 420);
            this.MaximumSize = new Size(630, 420);
            this.BackColor = isDark ? darkBackground : Color.FromArgb(243, 243, 243);
            this.Font = new Font("Segoe UI", 10.5F);
            this.Padding = new Padding(0);
            
            // Set the form icon - The icon.ico is embedded in the application via ApplicationIcon in .csproj
            // It's automatically set, but we can also load from file if user provides custom one
            try
            {
                var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    // User has provided a custom icon in the output directory
                    this.Icon = new Icon(iconPath);
                }
                // Otherwise, the embedded icon from ApplicationIcon is used automatically
            }
            catch 
            { 
                // If loading custom icon fails, the embedded icon will be used
            }

            // Backup Group - Modern card style (moved up since character group removed)
            grpBackup = new GroupBox
            {
                Text = "  Create Heroic Save  ",
                Location = new Point(15, 15),
                Size = new Size(715, 110),
                Font = new Font("Segoe UI", 11.5F, FontStyle.Regular),
                ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32),
                FlatStyle = FlatStyle.Flat,
                Enabled = false // Disabled until character selected
            };

            lblPlaythrough = new Label
            {
                Text = "Heroic Save:",
                Location = new Point(15, 28),
                Size = new Size(120, 25),
                Font = new Font("Segoe UI", 12F),
                ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                TextAlign = ContentAlignment.MiddleLeft
            };

            lblCurrentSave = new Label
            {
                Location = new Point(140, 26),
                Size = new Size(560, 28),
                Font = new Font("Segoe UI", 12F),
                ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32),
                BorderStyle = BorderStyle.None,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "No save loaded"
            };

            btnBackup = new Button
            {
                Text = "Backup",
                Location = new Point(475, 60),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(16, 137, 62),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnBackup.FlatAppearance.BorderSize = 0;
            btnBackup.FlatAppearance.MouseOverBackColor = Color.FromArgb(14, 120, 54);
            btnBackup.FlatAppearance.MouseDownBackColor = Color.FromArgb(12, 105, 47);
            btnBackup.Click += BtnBackup_Click;

            // Quicksave timestamp label
            lblQuickSaveKey = new Label
            {
                Text = "[Ctrl+S] ",
                Location = new Point(140, 62),
                Size = new Size(75, 25),
                Font = new Font("Segoe UI", 12F),
                ForeColor = isDark ? Color.FromArgb(100, 200, 100) : Color.FromArgb(16, 137, 62),
                TextAlign = ContentAlignment.MiddleLeft
            };
			
            lblQuickSave = new Label
            {
                Text = "Quicksave: None",
                Location = new Point(210, 65),
                Size = new Size(250, 25),
                Font = new Font("Segoe UI", 11F),
                ForeColor = isDark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(96, 96, 96),
                TextAlign = ContentAlignment.MiddleLeft
            };

            grpBackup.Controls.Add(lblPlaythrough);
            grpBackup.Controls.Add(lblCurrentSave);
            grpBackup.Controls.Add(btnBackup);
            grpBackup.Controls.Add(lblQuickSaveKey);
            grpBackup.Controls.Add(lblQuickSave);

            // Restore Group - Modern card style (moved up since character group removed)
            grpRestore = new GroupBox
            {
                Text = "  Restore Heroic Save  ",
                Location = new Point(15, 130),
                Size = new Size(715, 150),
                Font = new Font("Segoe UI", 11.5F, FontStyle.Regular),
                ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32),
                FlatStyle = FlatStyle.Flat,
                Enabled = false // Disabled until character selected
            };

            lblBackup = new Label
            {
                Text = "Select Backup:",
                Location = new Point(15, 28),
                Size = new Size(120, 25),
                Font = new Font("Segoe UI", 10.5F),
                ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                TextAlign = ContentAlignment.MiddleLeft
            };

            cboBackups = new ComboBox
            {
                Location = new Point(140, 26),
                Size = new Size(390, 30),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10.5F),
                FlatStyle = FlatStyle.Flat
            };


			// Backup Status Bar (under Select Backup)
			lblBackupStatus = new Label
			{
				Location = new Point(140, 60),
				Size = new Size(390, 30),
				BorderStyle = BorderStyle.None,
				BackColor = Color.White,
				Text = "✓ Ready",
				Padding = new Padding(10, 6, 10, 6),
				Font = new Font("Segoe UI", 9.5F),
				ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
				TextAlign = ContentAlignment.MiddleLeft
			};
            cboBackups.SelectedIndexChanged += CboBackups_SelectedIndexChanged;

            btnDeleteBackup = new Button
            {
                Text = "Delete",
                Location = new Point(255, 100),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(196, 43, 28),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnDeleteBackup.FlatAppearance.BorderSize = 0;
            btnDeleteBackup.FlatAppearance.MouseOverBackColor = Color.FromArgb(176, 38, 25);
            btnDeleteBackup.FlatAppearance.MouseDownBackColor = Color.FromArgb(156, 33, 22);
            btnDeleteBackup.Click += BtnDeleteBackup_Click;

            btnRestore = new Button
            {
                Text = "Restore",
                Location = new Point(365, 100),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRestore.FlatAppearance.BorderSize = 0;
            btnRestore.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 102, 180);
            btnRestore.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 90, 158);
            btnRestore.Click += BtnRestore_Click;

            btnRestoreAfterDeath = new Button
            {
                Text = "Restore from\nParty Wipe",
                Location = new Point(475, 100),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(128, 0, 128),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.2F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRestoreAfterDeath.FlatAppearance.BorderSize = 0;
            btnRestoreAfterDeath.FlatAppearance.MouseOverBackColor = Color.FromArgb(108, 0, 108);
            btnRestoreAfterDeath.FlatAppearance.MouseDownBackColor = Color.FromArgb(88, 0, 88);
            btnRestoreAfterDeath.Click += BtnRestoreAfterDeath_Click;

            // Quick-restore label with warning
            lblQuickRestore = new Label
            {
                Text = "[Ctrl+L] Quick-restore: Overwrites current save with quicksave. Back up first!",
                Location = new Point(15, 100),
                Size = new Size(240, 40),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = isDark ? Color.FromArgb(180, 140, 140) : Color.FromArgb(160, 80, 80),
                TextAlign = ContentAlignment.MiddleLeft
            };

            grpRestore.Controls.Add(lblBackup);
            grpRestore.Controls.Add(cboBackups);
            grpRestore.Controls.Add(lblBackupStatus);
            grpRestore.Controls.Add(btnRestore);
            grpRestore.Controls.Add(btnDeleteBackup);
            grpRestore.Controls.Add(btnRestoreAfterDeath);
            grpRestore.Controls.Add(lblQuickRestore);
			
			
            grpChooseCharacter = new GroupBox
            {
                Text = "  Change Character  ",
                Location = new Point(15, 285),
                Size = new Size(350, 70),
                Font = new Font("Segoe UI", 11.5F, FontStyle.Regular),
                ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32),
                FlatStyle = FlatStyle.Flat,
            };
            // Choose Character Button - styled like restore button

            cboChooseCharacter = new ComboBox
            {
                Location = new Point(140, 26),
                Size = new Size(175, 30),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10.5F),
                FlatStyle = FlatStyle.Flat,
				BackColor = isDark ? darkControlBg : Color.White,
				ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32),

            };
            cboChooseCharacter.SelectedIndexChanged += CboChooseCharacter_SelectedIndexChanged;
						
            grpChooseCharacter.Controls.Add(cboChooseCharacter);
			
            // Settings Button (Gear icon) - Bottom right
            btnSettings = new Button
            {
                Text = "⚙️",
                Location = new Point(530, 290),
                Size = new Size(70, 70),
                ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 32),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            btnSettings.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);
            btnSettings.FlatAppearance.BorderSize = 0;
            btnSettings.FlatAppearance.MouseDownBackColor = isDark ? Color.FromArgb(75, 75, 75) : Color.FromArgb(223, 223, 223);
            btnSettings.Click += BtnSettings_Click;

            // Donation Link - Modern subtle style
            lnkDonate = new LinkLabel
            {
                Location = new Point(0, 353),
                Size = new Size(630, 25),
                Text = "☕ Support development - Buy me a coffee",
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                LinkColor = Color.FromArgb(0, 120, 212),
                ActiveLinkColor = Color.FromArgb(0, 90, 158),
                VisitedLinkColor = Color.FromArgb(0, 120, 212),
                LinkBehavior = LinkBehavior.HoverUnderline,
                Cursor = Cursors.Hand
            };
            lnkDonate.LinkClicked += LnkDonate_LinkClicked;

            // Backup thumbnail preview (shown when backup selected)
            picBackupThumbnail = new PictureBox
            {
                Location = new Point(NormalFormWidth, 15),
                Size = new Size(ExpandedFormWidth - NormalFormWidth - 30, 370),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = isDark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(230, 230, 230),
                BorderStyle = BorderStyle.FixedSingle,
                Visible = false
            };

            // Add all controls to form
            this.Controls.Add(grpBackup);
            this.Controls.Add(grpRestore);
            this.Controls.Add(grpChooseCharacter);			
            this.Controls.Add(btnSettings);
            this.Controls.Add(lnkDonate);
            this.Controls.Add(picBackupThumbnail);
            
            // Create Steam Cloud warning panel (hidden by default)
            CreateSteamCloudWarningPanel();
        }
        
        private void CreateSteamCloudWarningPanel()
        {
            bool isDark = Properties.Settings.Default.DarkMode;
            
            pnlSteamCloudWarning = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(this.ClientSize.Width, 500),
                BackColor = isDark ? darkBackground : lightBackground,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            
            var warningIcon = new Label
            {
                Text = "⚠️",
                Font = new Font("Segoe UI", 48F),
                Size = new Size(100, 80),
                Location = new Point((pnlSteamCloudWarning.Width - 100) / 2, 10),
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.Top
            };
            
            var lblWarningTitle = new Label
            {
                Text = "Steam Cloud Sync is ENABLED",
                Font = new Font("Segoe UI", 18F, FontStyle.Bold),
                ForeColor = Color.FromArgb(220, 53, 69), // Red warning color
                Size = new Size(500, 40),
                Location = new Point((pnlSteamCloudWarning.Width - 500) / 2, 100),
                TextAlign = ContentAlignment.MiddleCenter,
                Anchor = AnchorStyles.Top
            };
            
            var lblWarningMessage = new Label
            {
                Text = "Steam Cloud Sync will interfere with save backup and restoration.\n\n" +
                       "To use this tool, you must disable Steam Cloud for Baldur's Gate 3:\n\n" +
                       "1. Open Steam and go to your Library\n" +
                       "2. Right-click on Baldur's Gate 3\n" +
                       "3. Select 'Properties'\n" +
                       "4. Go to the 'General' tab\n" +
                       "5. Uncheck 'Keep game saves in the Steam Cloud'",
                Font = new Font("Segoe UI", 11F),
                ForeColor = isDark ? darkForeground : lightForeground,
                Size = new Size(550, 200),
                Location = new Point((pnlSteamCloudWarning.Width - 550) / 2, 150),
                TextAlign = ContentAlignment.TopCenter,
                Anchor = AnchorStyles.Top
            };
            
            var btnOpenSteam = new Button
            {
                Text = "Open Steam",
                Size = new Size(140, 40),
                Location = new Point((pnlSteamCloudWarning.Width - 300) / 2, 360),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top
            };
            btnOpenSteam.FlatAppearance.BorderSize = 0;
            btnOpenSteam.Click += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "steam://nav/games/details/1086940",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            
            var btnAcknowledge = new Button
            {
                Text = "I've Disabled It",
                Size = new Size(140, 40),
                Location = new Point((pnlSteamCloudWarning.Width + 20) / 2, 360),
                BackColor = Color.FromArgb(40, 167, 69), // Green
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Top
            };
            btnAcknowledge.FlatAppearance.BorderSize = 0;
            btnAcknowledge.Click += (s, e) =>
            {
                AcknowledgeSteamCloudWarning();
                CheckSteamCloudStatus();
            };
            
            pnlSteamCloudWarning.Controls.Add(warningIcon);
            pnlSteamCloudWarning.Controls.Add(lblWarningTitle);
            pnlSteamCloudWarning.Controls.Add(lblWarningMessage);
            pnlSteamCloudWarning.Controls.Add(btnOpenSteam);
            pnlSteamCloudWarning.Controls.Add(btnAcknowledge);
            
            this.Controls.Add(pnlSteamCloudWarning);
            pnlSteamCloudWarning.BringToFront();
        }
        
        private void CheckSteamCloudStatus()
        {
            // Check if we need to show the warning (first launch only)
            if (ShouldShowSteamCloudWarning())
            {
                // Resize form to fit warning panel
                this.Size = new Size(NormalFormWidth, 500);
                this.MinimumSize = new Size(NormalFormWidth, 500);
                this.MaximumSize = new Size(NormalFormWidth, 500);
                
                pnlSteamCloudWarning.Visible = true;
                grpBackup.Visible = false;
                grpRestore.Visible = false;
                grpChooseCharacter.Visible = false;
                btnSettings.Visible = false;
                lnkDonate.Visible = false;
                picBackupThumbnail.Visible = false;
            }
            else
            {
                // Normal form size (will be expanded if backup is selected)
                SetFormWidth(false);
                
                pnlSteamCloudWarning.Visible = false;
                grpBackup.Visible = true;
                grpRestore.Visible = true;
                grpChooseCharacter.Visible = true;
                btnSettings.Visible = true;
                lnkDonate.Visible = true;
            }
        }
        
        private void SetFormWidth(bool expanded)
        {
            int width = expanded ? ExpandedFormWidth : NormalFormWidth;
            this.Size = new Size(width, 420);
            this.MinimumSize = new Size(width, 420);
            this.MaximumSize = new Size(width, 420);
        }
        
        private bool ShouldShowSteamCloudWarning()
        {
            // Show warning only on first launch (one-time disclaimer)
            try
            {
                return !Properties.Settings.Default.SteamCloudWarningAcknowledged;
            }
            catch
            {
                return true;
            }
        }
        
        private void AcknowledgeSteamCloudWarning()
        {
            try
            {
                Properties.Settings.Default.SteamCloudWarningAcknowledged = true;
                Properties.Settings.Default.Save();
            }
            catch { }
        }

        private void BtnSettings_Click(object? sender, EventArgs e)
        {
            ShowSettingsWindow();
        }

        private void LoadCharacterData(PlaythroughInfo character)
        {
            // Suspend layout to prevent flicker
            this.SuspendLayout();
            cboBackups.BeginUpdate();
            
            try
            {
                // Refresh playthroughs data from disk first
                LoadPlaythroughs();
            
            // Find the updated character info
            var updatedCharacter = playthroughs.Values
                .FirstOrDefault(pt => pt.FolderName == character.FolderName);
            
            if (updatedCharacter == null)
            {
                UpdateBackupStatus("Character not found", Color.Red);
                return;
            }
            
            // Update the stored selected character with fresh data
            selectedCharacter = updatedCharacter;
            
            // Use the updated character for display
            character = updatedCharacter;
            
            // Save current backup selection before clearing
            BackupInfo? previouslySelectedBackup = null;
            if (cboBackups.SelectedItem is BackupComboItem prevItem)
            {
                previouslySelectedBackup = prevItem.Backup;
            }
            cboBackups.Items.Clear();

            // Load restoration tracking for this character
            LoadRestorationTracking();

            // Determine what to show in lblCurrentSave
            UpdateCurrentSaveLabel(character);

            // Load only this character's backups
            LoadBackups();
            
            // Filter backups to only show this character's (excluding quicksaves for the dropdown)
            var characterBackups = backups.Values
                .Where(b => b.CharacterName.Equals(selectedCharacter.CharacterName, StringComparison.OrdinalIgnoreCase))
                .Where(b => !b.IsQuicksave) // Don't show quicksaves in backup dropdown
                .OrderByDescending(b => b.SaveTimestamp)
                .ToList();

            cboBackups.Items.Clear();
            foreach (var backup in characterBackups)
            {
                var saveName = !string.IsNullOrEmpty(backup.UserSaveName)
                    ? backup.UserSaveName
                    : "[Unnamed Save]";
                
                // Format backup time with 12/24 hour setting
                var backupTimeStr = Properties.Settings.Default.Use24HourTime
                    ? backup.SaveTimestamp.ToString("M/d/yyyy HH:mm")
                    : backup.SaveTimestamp.ToString("M/d/yyyy h:mm tt");
                
                // Show: "Save Name - Time"
                var backupDisplay = $"{saveName} - {backupTimeStr}";
                cboBackups.Items.Add(new BackupComboItem(backup, backupDisplay));
            }

            if (cboBackups.Items.Count > 0)
            {
                // Try to restore previous selection by ID
                bool selectionRestored = false;
                if (previouslySelectedBackup != null)
                {
                    for (int i = 0; i < cboBackups.Items.Count; i++)
                    {
                        if (cboBackups.Items[i] is BackupComboItem item && item.Backup.Id == previouslySelectedBackup.Id)
                        {
                            cboBackups.SelectedIndex = i;
                            selectionRestored = true;
                            break;
                        }
                    }
                }
                
                // If couldn't restore, select first item
                if (!selectionRestored)
                {
                    cboBackups.SelectedIndex = 0;
                }
                
                UpdateBackupStatus($"Found {cboBackups.Items.Count} backup(s)", Color.Green);
            }
            else
            {
                UpdateBackupStatus("No backups found", Color.Orange);
            }
            
            // Load quicksave info for this character
            LoadQuickSaveInfo();
            }
            finally
            {
                // Resume layout to apply all changes at once
                cboBackups.EndUpdate();
                this.ResumeLayout();
            }
        }

        private void ShowSettingsWindow()
        {
            using (var settingsForm = new Form())
            {
                bool isDark = Properties.Settings.Default.DarkMode;
                
                settingsForm.Text = "Settings";
                settingsForm.Size = new Size(600, 220);
                settingsForm.StartPosition = FormStartPosition.CenterParent;
                settingsForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                settingsForm.MaximizeBox = false;
                settingsForm.MinimizeBox = false;
                settingsForm.BackColor = isDark ? darkBackground : Color.FromArgb(243, 243, 243);
                settingsForm.Font = new Font("Segoe UI", 10.5F);

                // Create tooltip for showing full paths
                var pathTooltip = new ToolTip
                {
                    AutoPopDelay = 10000,
                    InitialDelay = 300,
                    ReshowDelay = 100,
                    ShowAlways = true
                };

                // Helper function to shorten path if needed
                Func<string, Label, string> shortenPath = (fullPath, label) =>
                {
                    using (var g = label.CreateGraphics())
                    {
                        var size = g.MeasureString(fullPath, label.Font);
                        if (size.Width <= label.Width - 5)
                        {
                            return fullPath;
                        }
                        
                        // Need to shorten - try to keep drive and end
                        var parts = fullPath.Split(Path.DirectorySeparatorChar);
                        if (parts.Length <= 2)
                        {
                            return fullPath; // Too short to shorten meaningfully
                        }
                        
                        // Keep first part (drive) and progressively add from end
                        string shortened = parts[0] + Path.DirectorySeparatorChar + "...";
                        for (int i = parts.Length - 1; i >= 1; i--)
                        {
                            string test = parts[0] + Path.DirectorySeparatorChar + "..." + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar.ToString(), parts.Skip(i));
                            var testSize = g.MeasureString(test, label.Font);
                            if (testSize.Width <= label.Width - 5)
                            {
                                shortened = test;
                            }
                            else
                            {
                                break;
                            }
                        }
                        return shortened;
                    }
                };

                // BG3 Save Path
                var lblSavePathTitle = new Label
                {
                    Text = "BG3 Save Folder:",
                    Location = new Point(20, 25),
                    Size = new Size(130, 25),
                    Font = new Font("Segoe UI", 10.5F),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    TextAlign = ContentAlignment.MiddleLeft
                };

                var lblSavePathValue = new Label
                {
                    Location = new Point(155, 25),
                    Size = new Size(265, 25),
                    Font = new Font("Segoe UI", 10.5F),
                    ForeColor = isDark ? darkForeground : lightForeground,
                    BackColor = isDark ? darkControlBg : Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = false,
                    Cursor = Cursors.Hand
                };
                // Set text after control is created so we can measure
                lblSavePathValue.Text = shortenPath(bg3SavePath, lblSavePathValue);
                pathTooltip.SetToolTip(lblSavePathValue, bg3SavePath);

                var btnOpenSaveFolder = new Button
                {
                    Text = "📁",
                    Location = new Point(430, 23),
                    Size = new Size(35, 35),
                    BackColor = isDark ? darkControlBg : Color.FromArgb(243, 243, 243),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 14F),
                    Cursor = Cursors.Hand,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                btnOpenSaveFolder.FlatAppearance.BorderSize = 1;
                btnOpenSaveFolder.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);
                btnOpenSaveFolder.Click += (s, e) =>
                {
                    try
                    {
                        if (Directory.Exists(bg3SavePath))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", bg3SavePath);
                        }
                        else
                        {
                            MessageBox.Show("Folder does not exist.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                var btnChangeSavePath = new Button
                {
                    Text = "Browse",
                    Location = new Point(475, 23),
                    Size = new Size(95, 35),
                    BackColor = Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F),
                    Cursor = Cursors.Hand
                };
                btnChangeSavePath.FlatAppearance.BorderSize = 0;
                btnChangeSavePath.Click += (s, e) =>
                {
                    using (var folderDialog = new FolderBrowserDialog())
                    {
                        folderDialog.Description = "Select BG3 Save Folder (Story folder)";
                        folderDialog.SelectedPath = bg3SavePath;

                        if (folderDialog.ShowDialog() == DialogResult.OK)
                        {
                            bg3SavePath = folderDialog.SelectedPath;
                            lblSavePathValue.Text = shortenPath(bg3SavePath, lblSavePathValue);
                            pathTooltip.SetToolTip(lblSavePathValue, bg3SavePath);
                            Properties.Settings.Default.BG3SavePath = bg3SavePath;
                            Properties.Settings.Default.Save();
                            UpdateBackupStatus("BG3 save folder changed", Color.FromArgb(0, 120, 212));
                        }
                    }
                };

                // Backup Path
                var lblBackupPathTitle = new Label
                {
                    Text = "Backup Location:",
                    Location = new Point(20, 75),
                    Size = new Size(130, 25),
                    Font = new Font("Segoe UI", 10.5F),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    TextAlign = ContentAlignment.MiddleLeft
                };

                var lblBackupPathValue = new Label
                {
                    Location = new Point(155, 75),
                    Size = new Size(265, 25),
                    Font = new Font("Segoe UI", 10.5F),
                    ForeColor = isDark ? darkForeground : lightForeground,
                    BackColor = isDark ? darkControlBg : Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = ContentAlignment.MiddleLeft,
                    AutoEllipsis = false,
                    Cursor = Cursors.Hand
                };
                lblBackupPathValue.Text = shortenPath(backupRootPath, lblBackupPathValue);
                pathTooltip.SetToolTip(lblBackupPathValue, backupRootPath);

                var btnOpenBackupFolder = new Button
                {
                    Text = "📁",
                    Location = new Point(430, 73),
                    Size = new Size(35, 35),
                    BackColor = isDark ? darkControlBg : Color.FromArgb(243, 243, 243),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 14F),
                    Cursor = Cursors.Hand,
                    TextAlign = ContentAlignment.MiddleCenter
                };
                btnOpenBackupFolder.FlatAppearance.BorderSize = 1;
                btnOpenBackupFolder.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);
                btnOpenBackupFolder.Click += (s, e) =>
                {
                    try
                    {
                        if (Directory.Exists(backupRootPath))
                        {
                            System.Diagnostics.Process.Start("explorer.exe", backupRootPath);
                        }
                        else
                        {
                            MessageBox.Show("Folder does not exist.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };

                var btnChangeBackupPath = new Button
                {
                    Text = "Browse",
                    Location = new Point(475, 73),
                    Size = new Size(95, 35),
                    BackColor = Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F),
                    Cursor = Cursors.Hand
                };
                btnChangeBackupPath.FlatAppearance.BorderSize = 0;
                btnChangeBackupPath.Click += (s, e) =>
                {
                    using (var folderDialog = new FolderBrowserDialog())
                    {
                        folderDialog.Description = "Select backup folder location";
                        folderDialog.SelectedPath = backupRootPath;

                        if (folderDialog.ShowDialog() == DialogResult.OK)
                        {
                            backupRootPath = folderDialog.SelectedPath;
                            lblBackupPathValue.Text = shortenPath(backupRootPath, lblBackupPathValue);
                            pathTooltip.SetToolTip(lblBackupPathValue, backupRootPath);
                            if (!Directory.Exists(backupRootPath))
                            {
                                Directory.CreateDirectory(backupRootPath);
                            }
                            Properties.Settings.Default.BackupPath = backupRootPath;
                            Properties.Settings.Default.Save();
                            LoadBackups();
                            UpdateBackupStatus("Backup location changed", Color.FromArgb(0, 120, 212));
                        }
                    }
                };

                // Time Format Checkbox
                var chk24Hour = new CheckBox
                {
                    Text = "Use 24-hour time format",
                    Location = new Point(20, 125),
                    Size = new Size(200, 25),
                    Font = new Font("Segoe UI", 10.5F),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    Checked = Properties.Settings.Default.Use24HourTime
                };
                chk24Hour.CheckedChanged += (s, e) =>
                {
                    Properties.Settings.Default.Use24HourTime = chk24Hour.Checked;
                    Properties.Settings.Default.Save();
                    
                    // Reload the currently selected character's data to refresh time displays
                    if (selectedCharacter != null)
                    {
                        // Reload data for selected character
                        LoadCharacterData(selectedCharacter);
                    }
                };

                // Dark Mode Checkbox
                var chkDarkMode = new CheckBox
                {
                    Text = "Dark mode",
                    Location = new Point(250, 125),
                    Size = new Size(120, 25),
                    Font = new Font("Segoe UI", 10.5F),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    Checked = Properties.Settings.Default.DarkMode
                };
                chkDarkMode.CheckedChanged += (s, e) =>
                {
                    Properties.Settings.Default.DarkMode = chkDarkMode.Checked;
                    Properties.Settings.Default.Save();
                    ApplyTheme();
                };


                // Close Button - aligned with Browse buttons
                var btnClose = new Button
                {
                    Text = "Close",
                    Location = new Point(475, 123),
                    Size = new Size(95, 35),
                    BackColor = isDark ? darkControlBg : Color.FromArgb(243, 243, 243),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F),
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.OK
                };
                btnClose.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);
                btnClose.FlatAppearance.BorderSize = 1;

                settingsForm.Controls.Add(lblSavePathTitle);
                settingsForm.Controls.Add(lblSavePathValue);
                settingsForm.Controls.Add(btnOpenSaveFolder);
                settingsForm.Controls.Add(btnChangeSavePath);
                settingsForm.Controls.Add(lblBackupPathTitle);
                settingsForm.Controls.Add(lblBackupPathValue);
                settingsForm.Controls.Add(btnOpenBackupFolder);
                settingsForm.Controls.Add(btnChangeBackupPath);
                settingsForm.Controls.Add(chk24Hour);
                settingsForm.Controls.Add(chkDarkMode);
                settingsForm.Controls.Add(btnClose);

                // Hide main window while settings is open
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                
                settingsForm.ShowDialog();
                
                // Restore main window visibility
                this.Opacity = 1;
                this.ShowInTaskbar = true;
                
                // Refresh - reload selected character if one is selected
                if (selectedCharacter != null)
                {
                    // Character is selected - reload just that character's data
                    LoadCharacterData(selectedCharacter);
                }
                else
                {
                    // No character selected - just refresh the data
                    LoadPlaythroughs();
                }
            }
        }

        private void InitializePaths()
        {
            // First check if there's an initial config from installer
            var appPath = AppDomain.CurrentDomain.BaseDirectory;
            var initialConfigPath = Path.Combine(appPath, "initial_config.txt");
            
            if (File.Exists(initialConfigPath))
            {
                try
                {
                    var lines = File.ReadAllLines(initialConfigPath);
                    if (lines.Length >= 2)
                    {
                        bg3SavePath = lines[0].Trim();
                        backupRootPath = lines[1].Trim();
                        
                        // Save to settings
                        Properties.Settings.Default.BG3SavePath = bg3SavePath;
                        Properties.Settings.Default.BackupPath = backupRootPath;
                        Properties.Settings.Default.Save();
                        
                        // Delete the initial config file so we don't read it again
                        File.Delete(initialConfigPath);
                    }
                }
                catch { }
            }
            
            // Load saved paths from user settings
            if (string.IsNullOrEmpty(bg3SavePath))
            {
                bg3SavePath = Properties.Settings.Default.BG3SavePath;
            }
            
            if (string.IsNullOrEmpty(backupRootPath))
            {
                backupRootPath = Properties.Settings.Default.BackupPath;
            }

            // If no saved path, use default
            if (string.IsNullOrEmpty(bg3SavePath))
            {
                bg3SavePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Larian Studios", "Baldur's Gate 3", "PlayerProfiles", "Public", "Savegames", "Story"
                );
            }

            if (string.IsNullOrEmpty(backupRootPath))
            {
                backupRootPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CBG3BackupManager", "Backups"
                );
            }

            // Set profile path for profile8.lsf (honor mode flag)
            bg3ProfilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Larian Studios", "Baldur's Gate 3", "PlayerProfiles", "Public", "profile8.lsf"
            );

            // Apply dark mode if enabled
            ApplyTheme();

            // Check if backup path exists
            if (!Directory.Exists(backupRootPath))
            {
                var result = MessageBox.Show(
                    $"Backup folder does not exist:\n{backupRootPath}\n\nWould you like to create it?",
                    "Create Backup Folder",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );

                if (result == DialogResult.Yes)
                {
                    try
                    {
                        Directory.CreateDirectory(backupRootPath);
                        UpdateBackupStatus("Backup folder created successfully.", Color.Green);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error creating backup folder: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                else
                {
                    // User declined to create folder, show settings to select custom path
                    ShowSettingsWindow();
                }
            }
        }

        private void LoadPlaythroughs()
        {
            playthroughs = new Dictionary<string, PlaythroughInfo>();

            if (!Directory.Exists(bg3SavePath))
            {
                UpdateBackupStatus($"BG3 save folder not found: {bg3SavePath}", Color.Red);
                return;
            }

            // Ensure backup directory exists before loading profile names
            if (!Directory.Exists(backupRootPath))
            {
                try
                {
                    Directory.CreateDirectory(backupRootPath);
                }
                catch { }
            }

            try
            {
                var saveFolders = Directory.GetDirectories(bg3SavePath);
                
                foreach (var folder in saveFolders)
                {
                    var folderName = Path.GetFileName(folder);
                    
                    // Only process Honor Mode saves (folders ending with __HonourMode)
                    if (!folderName.EndsWith("__HonourMode", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var saveFiles = Directory.GetFiles(folder, "*.lsv")
                        .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                        .ToList();

                    if (saveFiles.Any())
                    {
                        var mostRecent = saveFiles.First();
                        var saveInfo = new FileInfo(mostRecent);

                        var playthroughInfo = new PlaythroughInfo
                        {
                            FolderPath = folder,
                            FolderName = folderName,
                            CharacterName = string.Empty,
                            UserDefinedName = string.Empty,
                            MostRecentSave = mostRecent,
                            LastModified = saveInfo.LastWriteTime
                        };

                        playthroughs[folderName] = playthroughInfo;
                    }
                }

                // Load saved profile names
                LoadProfileNames();

                // NOTE: Label population is handled by LoadCharacterData after character selection
                // NOTE: Restoration tracking is now loaded per-character in LoadCharacterData
                
                if (playthroughs.Count > 0)
                {
                }
                else
                {
                    UpdateBackupStatus("No Honor Mode playthroughs found.", Color.Orange);
                }
            }
            catch (Exception ex)
            {
                UpdateBackupStatus($"Error loading playthroughs: {ex.Message}", Color.Red);
            }

            // Populate the character selection dropdown
            PopulateCharacterDropdown();

            LoadBackups();
        }

        private bool isPopulatingCharacterDropdown = false;

        private void PopulateCharacterDropdown()
        {
            if (cboChooseCharacter == null)
                return;
                
            isPopulatingCharacterDropdown = true;
            
            try
            {
                cboChooseCharacter.Items.Clear();
                
                // Sort by character name, then by last modified
                var sortedPlaythroughs = playthroughs.Values
                    .OrderBy(p => string.IsNullOrEmpty(p.CharacterName) ? "zzz" : p.CharacterName)
                    .ThenByDescending(p => p.LastModified)
                    .ToList();
                
                foreach (var pt in sortedPlaythroughs)
                {
                    var displayName = !string.IsNullOrEmpty(pt.CharacterName) 
                        ? pt.CharacterName 
                        : "[Unnamed Character]";
                    cboChooseCharacter.Items.Add(displayName);
                }
                
                // If there's a selected character, select it in the dropdown
                if (selectedCharacter != null && !string.IsNullOrEmpty(selectedCharacter.CharacterName))
                {
                    var index = cboChooseCharacter.Items.IndexOf(selectedCharacter.CharacterName);
                    if (index >= 0)
                    {
                        cboChooseCharacter.SelectedIndex = index;
                    }
                }
                else if (cboChooseCharacter.Items.Count > 0)
                {
                    // Select first item if no character selected
                    cboChooseCharacter.SelectedIndex = 0;
                }
            }
            finally
            {
                isPopulatingCharacterDropdown = false;
            }
        }

        private void CboChooseCharacter_SelectedIndexChanged(object? sender, EventArgs e)
        {
            // Skip if we're populating the dropdown
            if (isPopulatingCharacterDropdown)
                return;
                
            if (cboChooseCharacter.SelectedIndex < 0)
                return;
            
            var selectedName = cboChooseCharacter.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedName))
                return;
            
            // Find the playthrough matching this name
            var selectedPlaythrough = playthroughs.Values
                .FirstOrDefault(p => p.CharacterName == selectedName || 
                    (string.IsNullOrEmpty(p.CharacterName) && selectedName == "[Unnamed Character]"));
            
            if (selectedPlaythrough != null && selectedPlaythrough != selectedCharacter)
            {
                selectedCharacter = selectedPlaythrough;
                
                // Save the selection for next time
                Properties.Settings.Default.LastSelectedCharacter = selectedPlaythrough.FolderName;
                Properties.Settings.Default.Save();
                
                // Enable backup and restore groups
                grpBackup.Enabled = true;
                grpRestore.Enabled = true;
                
                // Load filtered data for this character
                LoadCharacterData(selectedPlaythrough);
            }
        }

        private void LoadBackups()
        {
            cboBackups.Items.Clear();
            backups = new Dictionary<string, BackupInfo>();

            if (!Directory.Exists(backupRootPath))
            {
                return;
            }

            try
            {
                // Load backup data from our data file
                LoadBackupData();
                
                // NOTE: Dropdown population is handled by LoadCharacterData after character selection
            }
            catch (Exception ex)
            {
                UpdateBackupStatus($"Error loading backups: {ex.Message}", Color.Red);
            }
        }

        private void BtnBackup_Click(object? sender, EventArgs e)
        {
            // Get the selected character
            if (selectedCharacter == null)
            {
                MessageBox.Show("Please select a character first.", "No Character Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            var playthroughInfo = selectedCharacter;

            // Check if this character needs a name (first time only)
            if (string.IsNullOrEmpty(playthroughInfo.CharacterName))
            {
                using (var nameDialog = new Form())
                {
                    nameDialog.Text = "Name Your Character";
                    nameDialog.Size = new Size(500, 220);
                    nameDialog.StartPosition = FormStartPosition.CenterParent;
                    nameDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                    nameDialog.MaximizeBox = false;
                    nameDialog.MinimizeBox = false;
                    nameDialog.BackColor = Color.White;
                    nameDialog.Font = new Font("Segoe UI", 10.5F);

                    var lblPrompt = new Label
                    {
                        Text = "Enter your character's name for this Honor Mode run:",
                        Location = new Point(20, 20),
                        Size = new Size(450, 40),
                        Font = new Font("Segoe UI", 11F),
                        ForeColor = Color.FromArgb(32, 32, 32)
                    };

                    var txtName = new TextBox
                    {
                        Location = new Point(20, 70),
                        Size = new Size(450, 30),
                        Font = new Font("Segoe UI", 11F),
                        BorderStyle = BorderStyle.FixedSingle
                    };
                    txtName.Focus();

                    var lblHint = new Label
                    {
                        Text = "Only letters, numbers, and spaces allowed (1-20 characters)",
                        Location = new Point(20, 108),
                        Size = new Size(450, 22),
                        Font = new Font("Segoe UI", 8.5F),
                        ForeColor = Color.FromArgb(128, 128, 128)
                    };

                    var btnOK = new Button
                    {
                        Text = "Save",
                        DialogResult = DialogResult.None,
                        Location = new Point(280, 145),
                        Size = new Size(90, 38),
                        BackColor = Color.FromArgb(0, 120, 212),
                        ForeColor = Color.White,
                        FlatStyle = FlatStyle.Flat,
                        Font = new Font("Segoe UI", 10.5F),
                        Cursor = Cursors.Hand,
                        Enabled = false
                    };
                    btnOK.FlatAppearance.BorderSize = 0;
                    
                    // Enter key support
                    txtName.KeyPress += (s, ev) =>
                    {
                        if (ev.KeyChar == (char)Keys.Return && btnOK.Enabled)
                        {
                            ev.Handled = true;
                            btnOK.PerformClick();
                        }
                    };

                    // Real-time validation
                    txtName.TextChanged += (s, ev) =>
                    {
                        var text = txtName.Text.Trim();
                        bool isValid = !string.IsNullOrEmpty(text) && 
                                      text.Length <= 20 && 
                                      Regex.IsMatch(text, @"^[a-zA-Z0-9\s]+$");
                        
                        if (isValid)
                        {
                            btnOK.Enabled = true;
                            btnOK.BackColor = Color.FromArgb(0, 120, 212);
                            btnOK.ForeColor = Color.White;
                        }
                        else
                        {
                            btnOK.Enabled = false;
                            btnOK.BackColor = Color.FromArgb(243, 243, 243);
                            btnOK.ForeColor = Color.FromArgb(196, 43, 28);
                        }
                    };

                    btnOK.Click += (s, ev) =>
                    {
                        var text = txtName.Text.Trim();
                        if (!string.IsNullOrEmpty(text) && 
                            text.Length <= 20 && 
                            Regex.IsMatch(text, @"^[a-zA-Z0-9\s]+$"))
                        {
                            nameDialog.Tag = text;
                            nameDialog.DialogResult = DialogResult.OK;
                            nameDialog.Close();
                        }
                    };

                    var btnCancel = new Button
                    {
                        Text = "Cancel",
                        DialogResult = DialogResult.Cancel,
                        Location = new Point(380, 145),
                        Size = new Size(90, 38),
                        BackColor = Color.FromArgb(243, 243, 243),
                        ForeColor = Color.FromArgb(64, 64, 64),
                        FlatStyle = FlatStyle.Flat,
                        Font = new Font("Segoe UI", 10.5F),
                        Cursor = Cursors.Hand
                    };
                    btnCancel.FlatAppearance.BorderSize = 1;
                    btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);

                    nameDialog.Controls.Add(lblPrompt);
                    nameDialog.Controls.Add(txtName);
                    nameDialog.Controls.Add(lblHint);
                    nameDialog.Controls.Add(btnOK);
                    nameDialog.Controls.Add(btnCancel);
                    nameDialog.CancelButton = btnCancel;

                    // Hide main window while dialog is open
                    this.Opacity = 0;
                    this.ShowInTaskbar = false;
                    
                    var dialogResult = nameDialog.ShowDialog();
                    
                    // Restore main window visibility
                    this.Opacity = 1;
                    this.ShowInTaskbar = true;
                    
                    if (dialogResult == DialogResult.OK)
                    {
                        var characterName = nameDialog.Tag?.ToString() ?? string.Empty;
                        
                        if (!string.IsNullOrEmpty(characterName))
                        {
                            playthroughInfo.CharacterName = characterName;
                            SaveProfileNames();
                            
                            // Refresh the display
                            LoadPlaythroughs(); 
                            
                            // Reload character data to update the label
                            if (selectedCharacter != null)
                            {
                                LoadCharacterData(selectedCharacter);
                            }
                        }
                        else
                        {
                            return;
                        }
                    }
                    else
                    {
                        return; // User cancelled
                    }
                }
            }

            // Now prompt for save name (every time)
            string saveName = string.Empty;
            using (var saveNameDialog = new Form())
            {
                saveNameDialog.Text = "Name This Save";
                saveNameDialog.Size = new Size(500, 220);
                saveNameDialog.StartPosition = FormStartPosition.CenterParent;
                saveNameDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                saveNameDialog.MaximizeBox = false;
                saveNameDialog.MinimizeBox = false;
                saveNameDialog.BackColor = Color.White;
                saveNameDialog.Font = new Font("Segoe UI", 10.5F);

                var lblPrompt = new Label
                {
                    Text = $"Enter a name for this save of {playthroughInfo.CharacterName}:",
                    Location = new Point(20, 20),
                    Size = new Size(450, 40),
                    Font = new Font("Segoe UI", 11F),
                    ForeColor = Color.FromArgb(32, 32, 32)
                };

                var txtSaveName = new TextBox
                {
                    Location = new Point(20, 70),
                    Size = new Size(450, 30),
                    Font = new Font("Segoe UI", 11F),
                    BorderStyle = BorderStyle.FixedSingle
                };
                txtSaveName.Focus();

                var lblHint = new Label
                {
                    Text = "Only letters, numbers, and spaces allowed (1-20 characters)",
                    Location = new Point(20, 108),
                    Size = new Size(450, 22),
                    Font = new Font("Segoe UI", 8.5F),
                    ForeColor = Color.FromArgb(128, 128, 128)
                };

                var btnOK = new Button
                {
                    Text = "Create Heroic Save",
                    DialogResult = DialogResult.None,
                    Location = new Point(280, 130),
                    Size = new Size(190, 38),
                    BackColor = Color.FromArgb(16, 137, 62),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    Enabled = false
                };
                btnOK.FlatAppearance.BorderSize = 0;
                
                // Enter key support
                txtSaveName.KeyPress += (s, ev) =>
                {
                    if (ev.KeyChar == (char)Keys.Return && btnOK.Enabled)
                    {
                        ev.Handled = true;
                        btnOK.PerformClick();
                    }
                };

                // Real-time validation
                txtSaveName.TextChanged += (s, ev) =>
                {
                    var text = txtSaveName.Text.Trim();
                    bool isValid = !string.IsNullOrEmpty(text) && 
                                  text.Length <= 20 && 
                                  Regex.IsMatch(text, @"^[a-zA-Z0-9\s]+$");
                    
                    if (isValid)
                    {
                        btnOK.Enabled = true;
                        btnOK.BackColor = Color.FromArgb(16, 137, 62);
                        btnOK.ForeColor = Color.White;
                    }
                    else
                    {
                        btnOK.Enabled = false;
                        btnOK.BackColor = Color.FromArgb(243, 243, 243);
                        btnOK.ForeColor = Color.FromArgb(196, 43, 28);
                    }
                };

                btnOK.Click += (s, ev) =>
                {
                    var text = txtSaveName.Text.Trim();
                    if (!string.IsNullOrEmpty(text) && 
                        text.Length <= 20 && 
                        Regex.IsMatch(text, @"^[a-zA-Z0-9\s]+$"))
                    {
                        saveNameDialog.Tag = text;
                        saveNameDialog.DialogResult = DialogResult.OK;
                        saveNameDialog.Close();
                    }
                };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new Point(180, 130),
                    Size = new Size(90, 38),
                    BackColor = Color.FromArgb(243, 243, 243),
                    ForeColor = Color.FromArgb(64, 64, 64),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F),
                    Cursor = Cursors.Hand
                };
                btnCancel.FlatAppearance.BorderSize = 1;
                btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);

                saveNameDialog.Controls.Add(lblPrompt);
                saveNameDialog.Controls.Add(txtSaveName);
                saveNameDialog.Controls.Add(lblHint);
                saveNameDialog.Controls.Add(btnCancel);
                saveNameDialog.Controls.Add(btnOK);
                saveNameDialog.CancelButton = btnCancel;

                // Hide main window while dialog is open
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                
                var dialogResult = saveNameDialog.ShowDialog();
                
                // Restore main window visibility
                this.Opacity = 1;
                this.ShowInTaskbar = true;
                
                if (dialogResult == DialogResult.OK)
                {
                    saveName = saveNameDialog.Tag?.ToString() ?? string.Empty;
                    
                    if (string.IsNullOrEmpty(saveName))
                    {
                        return;
                    }

                    // Check if a backup with this character name and save name already exists
                    var existingBackup = backups.Values.FirstOrDefault(b => 
                        b.CharacterName.Equals(playthroughInfo.CharacterName, StringComparison.OrdinalIgnoreCase) &&
                        b.UserSaveName.Equals(saveName, StringComparison.OrdinalIgnoreCase) &&
                        !b.IsQuicksave
                    );

                    if (existingBackup != null)
                    {
                        var result = MessageBox.Show(
                            $"A backup named '{saveName}' already exists for {playthroughInfo.CharacterName}.\n\nCreated: {FormatDateTime(existingBackup.SaveTimestamp)}\n\nDo you want to overwrite it with this new backup?",
                            "Duplicate Save Name",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning
                        );

                        if (result == DialogResult.Yes)
                        {
                            // Delete the old backup folder
                            try
                            {
                                var oldBackupPath = Path.Combine(backupRootPath, existingBackup.RealFolderName);
                                if (Directory.Exists(oldBackupPath))
                                {
                                    Directory.Delete(oldBackupPath, true);
                                }
                                // Remove from dictionary by ID
                                backups.Remove(existingBackup.Id);
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Error deleting old backup: {ex.Message}\n\nWill create new backup anyway.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                        }
                        else
                        {
                            return; // User chose not to overwrite
                        }
                    }
                }
                else
                {
                    return; // User cancelled
                }
            }

            // Proceed with backup
            try
            {
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var backupFolderName = $"{playthroughInfo.FolderName}_{timestamp}";
                var backupPath = Path.Combine(backupRootPath, backupFolderName);

                Directory.CreateDirectory(backupPath);

                // Copy the entire playthrough folder
                CopyDirectory(playthroughInfo.FolderPath, Path.Combine(backupPath, playthroughInfo.FolderName));

                // Backup profile8.lsf (honor mode flag file) - CRITICAL
                if (File.Exists(bg3ProfilePath))
                {
                    try
                    {
                        var profileBackupPath = Path.Combine(backupPath, "profile8.lsf");
                        // Remove read-only if present on source
                        var attributes = File.GetAttributes(bg3ProfilePath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(bg3ProfilePath, attributes & ~FileAttributes.ReadOnly);
                        }
                        File.Copy(bg3ProfilePath, profileBackupPath, true);
                    }
                    catch (Exception profileEx)
                    {
                        // This is important - warn user but allow backup to continue
                        MessageBox.Show(
                            $"Warning: Could not backup profile8.lsf (Honor Mode flag)\n\nError: {profileEx.Message}\n\nThe save files were backed up, but restoring this backup may not preserve Honor Mode status.",
                            "Backup Warning",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning
                        );
                        UpdateBackupStatus($"Warning: Backup created without Honor Mode flag - {profileEx.Message}", Color.Orange);
                    }
                }
                else
                {
                    // File doesn't exist - warn user
                    MessageBox.Show(
                        $"Warning: profile8.lsf not found at:\n{bg3ProfilePath}\n\nThe save files were backed up, but Honor Mode status may not be preserved when restoring.",
                        "Missing Honor Mode Flag",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }

                // Save backup metadata
                var backup = new BackupInfo
                {
                    Id = Guid.NewGuid().ToString(),
                    RealFolderName = backupFolderName,
                    RealTimestamp = DateTime.Now,
                    CharacterName = playthroughInfo.CharacterName,
                    PlaythroughFolderName = playthroughInfo.FolderName,
                    UserSaveName = saveName,
                    SaveTimestamp = DateTime.Now,
                    IsQuicksave = false
                };
                backups[backup.Id] = backup;
                SaveBackupData();

                UpdateBackupStatus($"Backup '{saveName}' created for '{playthroughInfo.CharacterName}' successfully!", Color.Green);
                LoadBackups();
                
                // Reload the selected character's data to show the new backup
                LoadCharacterData(playthroughInfo);
            }
            catch (Exception ex)
            {
                UpdateBackupStatus($"Error creating backup: {ex.Message}", Color.Red);
                MessageBox.Show($"Error creating backup: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnRestore_Click(object? sender, EventArgs e)
        {
            PerformRestore(isAfterDeath: false);
        }

        private void BtnRestoreAfterDeath_Click(object? sender, EventArgs e)
        {
            PerformRestore(isAfterDeath: true);
        }

        private void PerformRestore(bool isAfterDeath)
        {
            if (selectedCharacter == null)
            {
                MessageBox.Show("Please select a character first.", "No Character Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (cboBackups.SelectedIndex == -1)
            {
                MessageBox.Show("Please select a backup to restore.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Resolve the selected backup
            BackupInfo? selectedBackup = null;

            // Preferred path: the ComboBox item carries the BackupInfo
            if (cboBackups.SelectedItem is BackupComboItem item)
            {
                selectedBackup = item.Backup;
            }

            if (selectedBackup == null)
            {
                UpdateBackupStatus("Error: Backup not found.", Color.Red);
                return;
            }

            var backupPath = Path.Combine(backupRootPath, selectedBackup.RealFolderName);
            var backupSubFolders = Directory.GetDirectories(backupPath);
            
            if (backupSubFolders.Length == 0)
            {
                UpdateBackupStatus("Error: Backup folder is empty.", Color.Red);
                return;
            }

            var playthroughFolderName = Path.GetFileName(backupSubFolders[0]);
            var targetPath = Path.Combine(bg3SavePath, playthroughFolderName);
            var profileDisplayName = !string.IsNullOrEmpty(selectedBackup.CharacterName) 
                ? selectedBackup.CharacterName 
                : "[Unnamed Character]";
            var saveDisplayName = !string.IsNullOrEmpty(selectedBackup.UserSaveName) 
                ? selectedBackup.UserSaveName 
                : "[Unnamed Save]";
            
            // Get current save display text for the confirmation dialog
            var currentSaveDisplay = lblCurrentSave.Text;

            // Only warn if folder actually exists
            bool folderExists = Directory.Exists(targetPath);
            
            // Create custom dialog for restore confirmation
            using (var restoreDialog = new Form())
            {
                bool isDark = Properties.Settings.Default.DarkMode;
                
                restoreDialog.Text = isAfterDeath ? "Restore After Death" : "Confirm Restore";
				restoreDialog.Size = isAfterDeath ? new Size(450, 260) : new Size(450, 220);
                restoreDialog.StartPosition = FormStartPosition.CenterParent;
                restoreDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                restoreDialog.MaximizeBox = false;
                restoreDialog.MinimizeBox = false;
                restoreDialog.BackColor = isDark ? darkBackground : Color.White;
                restoreDialog.Font = new Font("Segoe UI", 10F);


                // Build the message based on context
                string message;
                var backupTimeStr = FormatDateTime(selectedBackup.SaveTimestamp);
                if (isAfterDeath)
                {
                    message = folderExists
                        ? $"⚠️ Game will be CLOSED and RELAUNCHED ⚠️\n\nThis will DELETE and replace:\n{currentSaveDisplay}\n\nWith backup:\n{saveDisplayName} - {backupTimeStr}"
                        : $"Restore:\n{profileDisplayName}\n\nFrom backup:\n{saveDisplayName} - {backupTimeStr}";
                }
                else
                {
                    message = folderExists
                        ? $"This will DELETE and replace:\n{currentSaveDisplay}\n\nWith backup:\n{saveDisplayName} - {backupTimeStr}"
                        : $"Restore:\n{profileDisplayName}\n\nFrom backup:\n{saveDisplayName} - {backupTimeStr}";
                }

                var lblMessage = new Label
                {
                    Text = message,
                    Location = new Point(20, 20),
                    Size = isAfterDeath ? new Size(410, 140) : new Size(410, 100),
                    Font = new Font("Segoe UI", 10F),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32)
                };

                var btnYes = new Button
                {
                    Text = "Restore",
                    Location = isAfterDeath ? new Point(200, 165) : new Point(200, 130),
                    Size = new Size(105, 38),
                    BackColor = isAfterDeath ? Color.FromArgb(128, 0, 128) : Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.OK
                };
                btnYes.FlatAppearance.BorderSize = 0;

                var btnNo = new Button
                {
                    Text = "Cancel",
                    Location = isAfterDeath ? new Point(315, 165) : new Point(315, 130),
                    Size = new Size(105, 38),
                    BackColor = isDark ? darkControlBg : Color.FromArgb(243, 243, 243),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F),
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.Cancel
                };
                btnNo.FlatAppearance.BorderSize = 1;
                btnNo.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);

                // Enter key support
                restoreDialog.KeyPreview = true;
                restoreDialog.KeyPress += (s, e) =>
                {
                    if (e.KeyChar == (char)Keys.Return)
                    {
                        e.Handled = true;
                        btnYes.PerformClick();
                    }
                    else if (e.KeyChar == (char)Keys.Escape)
                    {
                        e.Handled = true;
                        btnNo.PerformClick();
                    }
                };

                restoreDialog.Controls.Add(lblMessage);
                restoreDialog.Controls.Add(btnYes);
                restoreDialog.Controls.Add(btnNo);

                // Hide main window while dialog is open
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                
                var dialogResult = restoreDialog.ShowDialog();
                
                // Restore main window visibility
                this.Opacity = 1;
                this.ShowInTaskbar = true;
                
                if (dialogResult != DialogResult.OK)
                {
                    return;
                }

                // If restoring after death, close the game first
                if (isAfterDeath)
                {
                    try
                    {
                        UpdateBackupStatus("Closing Baldur's Gate 3...", Color.Orange);
                        
                        var bg3Processes = System.Diagnostics.Process.GetProcessesByName("bg3");
                        var bg3_dxProcesses = System.Diagnostics.Process.GetProcessesByName("bg3_dx11");
                        
                        foreach (var proc in bg3Processes.Concat(bg3_dxProcesses))
                        {
                            try
                            {
                                proc.Kill();
                                proc.WaitForExit(5000); // Wait up to 5 seconds
                            }
                            catch { }
                        }
                        
                        // Give it a moment to fully close
                        System.Threading.Thread.Sleep(2000);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Warning: Could not close game automatically.\n\n{ex.Message}\n\nPlease close it manually and click OK to continue.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }

            try
            {
                // Delete existing save if it exists
                if (Directory.Exists(targetPath))
                {
                    // Take ownership and grant full permissions before deleting
                    TakeOwnershipAndDelete(targetPath);
                }

                // Restore from backup
                CopyDirectory(backupSubFolders[0], targetPath);

                // Restore profile8.lsf (honor mode flag file) - CRITICAL
                var profileBackupPath = Path.Combine(backupPath, "profile8.lsf");
                if (File.Exists(profileBackupPath) && !string.IsNullOrEmpty(bg3ProfilePath))
                {
                    try
                    {
                        // Ensure the directory exists
                        var profileDir = Path.GetDirectoryName(bg3ProfilePath);
                        if (!string.IsNullOrEmpty(profileDir) && !Directory.Exists(profileDir))
                        {
                            Directory.CreateDirectory(profileDir);
                        }
                        
                        // Remove read-only attribute if present
                        if (File.Exists(bg3ProfilePath))
                        {
                            File.SetAttributes(bg3ProfilePath, FileAttributes.Normal);
                        }
                        
                        // Copy with overwrite
                        File.Copy(profileBackupPath, bg3ProfilePath, true);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        MessageBox.Show(
                            $"Permission denied when restoring profile8.lsf (Honor Mode flag).\n\nThis file is critical for Honor Mode to work correctly.\n\nTry running the application as Administrator?",
                            "Permission Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error
                        );
                        UpdateBackupStatus("Error: Failed to restore Honor Mode flag - Permission denied", Color.Red);
                        return;
                    }
                    catch (Exception profileEx)
                    {
                        var profileResult = MessageBox.Show(
                            $"CRITICAL: Failed to restore profile8.lsf (Honor Mode flag)\n\nError: {profileEx.Message}\n\nThe save files were restored, but Honor Mode may not function correctly without this file.\n\nDo you want to continue anyway?",
                            "Critical Restore Error",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning
                        );
                        
                        if (profileResult == DialogResult.No)
                        {
                            UpdateBackupStatus("Restore cancelled - Honor Mode flag could not be restored", Color.Red);
                            return;
                        }
                        else
                        {
                            UpdateBackupStatus($"Warning: Restored without Honor Mode flag - {profileEx.Message}", Color.Orange);
                        }
                    }
                }
                else if (!File.Exists(profileBackupPath))
                {
                    // Old backup without profile8.lsf
                    var missingProfileResult = MessageBox.Show(
                        $"This backup does not contain profile8.lsf (Honor Mode flag).\n\nThis was likely created before profile backup was implemented.\n\nHonor Mode may not work correctly after restore.\n\nDo you want to continue?",
                        "Missing Honor Mode Flag",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    );
                    
                    if (missingProfileResult == DialogResult.No)
                    {
                        UpdateBackupStatus("Restore cancelled - Backup missing Honor Mode flag", Color.Red);
                        return;
                    }
                }

                // Record the restore with our new tracking system
                lastRestorationTimestamp = DateTime.Now;
                lastRestoredBackupId = selectedBackup.Id;
                SaveRestorationTracking();

                UpdateBackupStatus($"Restore completed for '{profileDisplayName}'", Color.Green);
                
                // Reload the selected character's data (reuse existing selectedCharacter variable)
                LoadCharacterData(selectedCharacter);

                // If after death restore was used, launch the game
                if (isAfterDeath)
                {
                    try
                    {
                        UpdateBackupStatus("Launching Baldur's Gate 3...", Color.FromArgb(0, 120, 212));
                        
                        // Try to launch via Steam client executable
                        // This is more reliable than steam:// protocol
                        var steamPath = Microsoft.Win32.Registry.GetValue(
                            @"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Valve\Steam",
                            "InstallPath",
                            null) as string;
                        
                        if (steamPath == null)
                        {
                            // Try 64-bit registry path
                            steamPath = Microsoft.Win32.Registry.GetValue(
                                @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
                                "InstallPath",
                                null) as string;
                        }
                        
                        if (!string.IsNullOrEmpty(steamPath))
                        {
                            var steamExe = Path.Combine(steamPath, "steam.exe");
                            if (File.Exists(steamExe))
                            {
                                // Launch BG3 via Steam with app ID
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = steamExe,
                                    Arguments = "-applaunch 1086940",
                                    UseShellExecute = true,
                                    WorkingDirectory = steamPath
                                });
                                
                                UpdateBackupStatus("Restore complete - BG3 launching...", Color.Green);
                            }
                            else
                            {
                                throw new Exception("Steam executable not found at expected location.");
                            }
                        }
                        else
                        {
                            throw new Exception("Steam installation not found in registry.");
                        }
                    }
                    catch (Exception launchEx)
                    {
                        MessageBox.Show($"Restore completed but could not auto-launch game:\n{launchEx.Message}\n\nPlease launch BG3 manually.", "Launch Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                UpdateBackupStatus($"Error restoring backup: {ex.Message}", Color.Red);
                MessageBox.Show($"Error restoring backup: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                DoQuickSave();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.L)
            {
                DoQuickRestore();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            // For WH_KEYBOARD_LL, hMod can be IntPtr.Zero and dwThreadId must be 0
            // This works for global hooks in .NET
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, IntPtr.Zero, 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                
                // Check if Ctrl is held down
                bool ctrlPressed = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                
                if (ctrlPressed && vkCode == VK_S)
                {
                    // Must invoke on UI thread since hook runs on a different thread
                    if (InvokeRequired)
                    {
                        this.Invoke(new Action(() => DoQuickSave()));
                    }
                    else
                    {
                        DoQuickSave();
                    }
                }
                else if (ctrlPressed && vkCode == VK_L)
                {
                    // Must invoke on UI thread since hook runs on a different thread
                    if (InvokeRequired)
                    {
                        this.Invoke(new Action(() => DoQuickRestore()));
                    }
                    else
                    {
                        DoQuickRestore();
                    }
                }
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void DoQuickSave()
        {
            // Get selected character
            if (selectedCharacter == null)
            {
                // No character selected - do nothing
                return;
            }

            // Validate we have a valid save to backup
            if (string.IsNullOrEmpty(selectedCharacter.FolderPath) || !Directory.Exists(selectedCharacter.FolderPath))
            {
                return;
            }

            try
            {
                // Build quicksave path: backupRootPath\CharacterFolderName_quicksave
                var quicksaveFolderName = $"{selectedCharacter.FolderName}_quicksave";
                var quicksavePath = Path.Combine(backupRootPath, quicksaveFolderName);

                // Find existing quicksave backup for this character
                var existingQuicksave = backups.Values.FirstOrDefault(b => 
                    b.IsQuicksave && b.CharacterName.Equals(selectedCharacter.CharacterName, StringComparison.OrdinalIgnoreCase));

                // Delete existing quicksave folder if it exists (silent overwrite)
                if (Directory.Exists(quicksavePath))
                {
                    try
                    {
                        Directory.Delete(quicksavePath, true);
                    }
                    catch
                    {
                        // If we can't delete, try to force it
                        TakeOwnershipAndDelete(quicksavePath);
                    }
                }

                // Create new quicksave
                Directory.CreateDirectory(quicksavePath);

                // Copy the entire playthrough folder
                CopyDirectory(selectedCharacter.FolderPath, Path.Combine(quicksavePath, selectedCharacter.FolderName));

                // Backup profile8.lsf (honor mode flag file) - CRITICAL
                if (File.Exists(bg3ProfilePath))
                {
                    try
                    {
                        var profileBackupPath = Path.Combine(quicksavePath, "profile8.lsf");
                        var attributes = File.GetAttributes(bg3ProfilePath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        {
                            File.SetAttributes(bg3ProfilePath, attributes & ~FileAttributes.ReadOnly);
                        }
                        File.Copy(bg3ProfilePath, profileBackupPath, true);
                    }
                    catch
                    {
                        // Silent fail for quicksave - profile backup is best effort
                    }
                }

                // Update or create quicksave backup entry
                var now = DateTime.Now;
                if (existingQuicksave != null)
                {
                    // Update existing quicksave entry
                    existingQuicksave.RealTimestamp = now;
                    existingQuicksave.SaveTimestamp = now;
                }
                else
                {
                    // Create new quicksave entry
                    var quicksaveBackup = new BackupInfo
                    {
                        Id = Guid.NewGuid().ToString(),
                        RealFolderName = quicksaveFolderName,
                        RealTimestamp = now,
                        CharacterName = selectedCharacter.CharacterName,
                        PlaythroughFolderName = selectedCharacter.FolderName,
                        UserSaveName = "[Quicksave]",
                        SaveTimestamp = now,
                        IsQuicksave = true
                    };
                    backups[quicksaveBackup.Id] = quicksaveBackup;
                }
                
                SaveBackupData();
                UpdateQuickSaveDisplay();
            }
            catch
            {
                // Silent fail - quicksave should never interrupt the user
            }
        }

        private void DoQuickRestore()
        {
            // Get selected character
            if (selectedCharacter == null)
            {
                // No character selected - do nothing
                return;
            }

            // Find the quicksave backup for this character
            var quicksaveBackup = backups.Values.FirstOrDefault(b => 
                b.IsQuicksave && b.CharacterName.Equals(selectedCharacter.CharacterName, StringComparison.OrdinalIgnoreCase));

            if (quicksaveBackup == null)
            {
                return;
            }

            var quicksavePath = Path.Combine(backupRootPath, quicksaveBackup.RealFolderName);

            // Check if quicksave folder exists
            if (!Directory.Exists(quicksavePath))
            {
                return;
            }

            try
            {
                // Get the playthrough folder inside the quicksave
                var backupPlaythroughPath = Path.Combine(quicksavePath, selectedCharacter.FolderName);
                if (!Directory.Exists(backupPlaythroughPath))
                {
                    return;
                }

                // Delete current save folder
                if (Directory.Exists(selectedCharacter.FolderPath))
                {
                    try
                    {
                        Directory.Delete(selectedCharacter.FolderPath, true);
                    }
                    catch
                    {
                        TakeOwnershipAndDelete(selectedCharacter.FolderPath);
                    }
                }

                // Copy quicksave back to save location
                CopyDirectory(backupPlaythroughPath, selectedCharacter.FolderPath);

                // Restore profile8.lsf (honor mode flag file) - CRITICAL
                var profileBackupPath = Path.Combine(quicksavePath, "profile8.lsf");
                if (File.Exists(profileBackupPath) && !string.IsNullOrEmpty(bg3ProfilePath))
                {
                    try
                    {
                        var profileDir = Path.GetDirectoryName(bg3ProfilePath);
                        if (!string.IsNullOrEmpty(profileDir) && !Directory.Exists(profileDir))
                        {
                            Directory.CreateDirectory(profileDir);
                        }
                        
                        if (File.Exists(bg3ProfilePath))
                        {
                            File.SetAttributes(bg3ProfilePath, FileAttributes.Normal);
                        }
                        
                        File.Copy(profileBackupPath, bg3ProfilePath, true);
                    }
                    catch
                    {
                        // Silent fail for quick-restore - profile restore is best effort
                    }
                }

                // Record the quick-restore using the new tracking system
                lastRestorationTimestamp = DateTime.Now;
                lastRestoredBackupId = quicksaveBackup.Id;
                SaveRestorationTracking();

                // Update the label to show "Quicksave" and the timestamp
                UpdateCurrentSaveLabel(selectedCharacter);
            }
            catch
            {
                // Silent fail - quick-restore should never interrupt the user
            }
        }

        private void UpdatePlaythroughDropdownForQuicksave()
        {
            // This function is no longer needed - UpdateCurrentSaveLabel handles this
        }

        private void UpdateQuickSaveDisplay()
        {
            if (lblQuickSave == null || selectedCharacter == null)
            {
                if (lblQuickSave != null)
                {
                    bool isDark = Properties.Settings.Default.DarkMode;
                    lblQuickSave.Text = "Quicksave: None";
                    lblQuickSave.ForeColor = isDark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(96, 96, 96);
                }
                return;
            }

            bool isDarkMode = Properties.Settings.Default.DarkMode;

            // Find quicksave for this character
            var quicksaveBackup = backups.Values.FirstOrDefault(b => 
                b.IsQuicksave && b.CharacterName.Equals(selectedCharacter.CharacterName, StringComparison.OrdinalIgnoreCase));

            if (quicksaveBackup != null)
            {
                var timeStr = Properties.Settings.Default.Use24HourTime
                    ? quicksaveBackup.SaveTimestamp.ToString("M/d/yyyy HH:mm:ss")
                    : quicksaveBackup.SaveTimestamp.ToString("M/d/yyyy h:mm:ss tt");
                lblQuickSave.Text = $"⚡ Quicksave: {timeStr}";
                lblQuickSave.ForeColor = isDarkMode ? Color.FromArgb(100, 200, 100) : Color.FromArgb(16, 137, 62);
            }
            else
            {
                lblQuickSave.Text = "Quicksave: None";
                lblQuickSave.ForeColor = isDarkMode ? Color.FromArgb(140, 140, 140) : Color.FromArgb(96, 96, 96);
            }
        }

        private void LoadQuickSaveInfo()
        {
            // Simply update the quicksave display - the backup system handles the data
            UpdateQuickSaveDisplay();
        }

        private void TakeOwnershipAndDelete(string path)
        {
            try
            {
                // Use robocopy to mirror an empty directory (effectively deleting)
                // This handles permission issues better than Directory.Delete
                var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                Directory.CreateDirectory(tempDir);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "robocopy",
                    Arguments = $"\"{tempDir}\" \"{path}\" /MIR /R:0 /W:0",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    process?.WaitForExit();
                }

                // Clean up temp directory
                try { Directory.Delete(tempDir); } catch { }

                // Final cleanup - remove the now-empty target directory
                try { Directory.Delete(path, true); } catch { }
            }
            catch
            {
                // Fallback to standard delete with force
                try
                {
                    var dir = new DirectoryInfo(path);
                    SetAttributesNormal(dir);
                    Directory.Delete(path, true);
                }
                catch
                {
                    throw;
                }
            }
        }

        private void SetAttributesNormal(DirectoryInfo dir)
        {
            foreach (var subDir in dir.GetDirectories())
            {
                SetAttributesNormal(subDir);
            }
            foreach (var file in dir.GetFiles())
            {
                file.Attributes = FileAttributes.Normal;
            }
            dir.Attributes = FileAttributes.Normal;
        }

        private void BtnDeleteBackup_Click(object? sender, EventArgs e)
        {
            if (selectedCharacter == null)
            {
                MessageBox.Show("Please select a character first.", "No Character Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (cboBackups.SelectedIndex == -1)
            {
                MessageBox.Show("Please select a backup to delete.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Get the selected backup from ComboBox
            BackupInfo? selectedBackup = null;
            if (cboBackups.SelectedItem is BackupComboItem item)
            {
                selectedBackup = item.Backup;
            }

            if (selectedBackup == null)
            {
                UpdateBackupStatus("Error: Backup not found.", Color.Red);
                return;
            }

            var profileDisplayName = !string.IsNullOrEmpty(selectedBackup.CharacterName)
                ? selectedBackup.CharacterName
                : "[Unnamed Character]";
            var saveDisplayName = !string.IsNullOrEmpty(selectedBackup.UserSaveName)
                ? selectedBackup.UserSaveName
                : "[Unnamed Save]";

            var result = MessageBox.Show(
                $"Are you sure you want to DELETE this backup?\n\nCharacter: {profileDisplayName}\nSave: {saveDisplayName}\nCreated: {FormatDateTime(selectedBackup.SaveTimestamp)}\n\nThis action cannot be undone!",
                "Confirm Delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result != DialogResult.Yes)
                return;

            try
            {
                var backupFolderPath = Path.Combine(backupRootPath, selectedBackup.RealFolderName);
                if (Directory.Exists(backupFolderPath))
                {
                    Directory.Delete(backupFolderPath, true);
                }

                // Remove from dictionary by ID
                backups.Remove(selectedBackup.Id);

                SaveBackupData();

                UpdateBackupStatus($"Backup '{saveDisplayName}' for '{profileDisplayName}' deleted successfully!", Color.Green);

                // Reload lists and keep UI consistent
                LoadBackups();
                LoadCharacterData(selectedCharacter);

                MessageBox.Show("Backup deleted successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                UpdateBackupStatus($"Error deleting backup: {ex.Message}", Color.Red);
                MessageBox.Show($"Error deleting backup: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnRefresh_Click(object? sender, EventArgs e)
        {
            // Refresh - reload selected character if one is selected
            if (selectedCharacter != null)
            {
                // Character is selected - reload just that character's data
                LoadCharacterData(selectedCharacter);
            }
            else
            {
                // No character selected - just refresh the data
                LoadPlaythroughs();
            }
        }

        private void LnkDonate_LinkClicked(object? sender, LinkLabelLinkClickedEventArgs e)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://www.paypal.com/donate/?business=U2MLEA6CEHNTJ&no_recurring=0&item_name=Your+donation+helps+fund+ongoing+software+development%2C+maintenance%2C+and+improvements.+&currency_code=USD",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                MessageBox.Show("Unable to open donation link. Please visit PayPal manually.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void CboBackups_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (cboBackups.SelectedIndex == -1)
                return;

            try
            {
                if (selectedCharacter == null)
                    return;

                // Preferred path: the ComboBox item carries the BackupInfo
                if (cboBackups.SelectedItem is BackupComboItem item)
                {
                    var backup = item.Backup;
                    var backupFolderPath = Path.Combine(backupRootPath, backup.RealFolderName);
                    if (Directory.Exists(backupFolderPath))
                    {
                        var size = GetDirectorySize(backupFolderPath);
                        var sizeInMB = size / (1024.0 * 1024.0);
                        UpdateBackupStatus($"Backup: {backup.UserSaveName} ({FormatDateTime(backup.SaveTimestamp)}) | Size: {sizeInMB:F2} MB", Color.FromArgb(0, 120, 212));
                        
                        // Show backup thumbnail
                        ShowBackupThumbnail(backupFolderPath, backup.PlaythroughFolderName);
                    }
                    return;
                }
            }
            catch { }
        }
        
        private void ShowBackupThumbnail(string backupFolderPath, string playthroughFolderName)
        {
            try
            {
                // Look for WebP thumbnail in the backup folder
                // Path: backupFolderPath\playthroughFolderName\*.WebP
                var playthroughPath = Path.Combine(backupFolderPath, playthroughFolderName);
                if (!Directory.Exists(playthroughPath))
                {
                    ClearThumbnailImage();
                    return;
                }
                
                // Find WebP file (usually named like "HonourMode.WebP")
                var webpFiles = Directory.GetFiles(playthroughPath, "*.WebP", SearchOption.TopDirectoryOnly);
                if (webpFiles.Length == 0)
                {
                    // Try lowercase extension
                    webpFiles = Directory.GetFiles(playthroughPath, "*.webp", SearchOption.TopDirectoryOnly);
                }
                
                if (webpFiles.Length == 0)
                {
                    ClearThumbnailImage();
                    return;
                }
                
                var thumbnailPath = webpFiles[0];
                
                // Dispose of previous image to prevent memory leaks
                ClearThumbnailImage();
                
                // Load WebP image
                var image = LoadImageFromFile(thumbnailPath);
                if (image != null)
                {
                    picBackupThumbnail.Image = image;
                    
                    // Expand form and show thumbnail
                    SetFormWidth(true);
                    picBackupThumbnail.Visible = true;
                }
            }
            catch
            {
                ClearThumbnailImage();
            }
        }
        
        private Image? LoadImageFromFile(string filePath)
        {
            try
            {
                // First try: Load using Image.FromFile which uses GDI+ and Windows codecs
                // On Windows 10 1809+ with WebP Image Extensions installed, this works
                try
                {
                    // Create a copy in memory to avoid file locking
                    using (var original = Image.FromFile(filePath))
                    {
                        return new Bitmap(original);
                    }
                }
                catch (OutOfMemoryException)
                {
                    // GDI+ throws OutOfMemoryException for unsupported formats
                }
                
                // Second try: Read bytes and try MemoryStream
                try
                {
                    var bytes = File.ReadAllBytes(filePath);
                    using (var ms = new MemoryStream(bytes))
                    {
                        using (var original = Image.FromStream(ms))
                        {
                            return new Bitmap(original);
                        }
                    }
                }
                catch (ArgumentException)
                {
                    // Image format not supported
                }
                
                // Fallback: Show placeholder with install instructions
                return CreatePlaceholderImage();
            }
            catch
            {
                return CreatePlaceholderImage();
            }
        }
        
        private Image CreatePlaceholderImage()
        {
            var placeholder = new Bitmap(285, 370);
            using (var g = Graphics.FromImage(placeholder))
            {
                bool isDark = Properties.Settings.Default.DarkMode;
                g.Clear(isDark ? Color.FromArgb(45, 45, 45) : Color.FromArgb(230, 230, 230));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                
                using (var font = new Font("Segoe UI", 9F))
                using (var brush = new SolidBrush(isDark ? Color.FromArgb(180, 180, 180) : Color.FromArgb(100, 100, 100)))
                {
                    var text = "WebP Preview Unavailable\n\nInstall 'WebP Image Extensions'\nfrom Microsoft Store\nto view thumbnails";
                    var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(text, font, brush, new RectangleF(10, 10, 265, 350), format);
                }
            }
            return placeholder;
        }
        
        private void ClearThumbnailImage()
        {
            // Dispose of image but keep form expanded
            if (picBackupThumbnail.Image != null)
            {
                picBackupThumbnail.Image.Dispose();
                picBackupThumbnail.Image = null;
            }
        }
        
        private void HideBackupThumbnail()
        {
            ClearThumbnailImage();
            picBackupThumbnail.Visible = false;
            SetFormWidth(false);
        }

        private void SyncProfileToBackup(string characterName)
        {
            // No longer needed since character is already selected
            // Keeping method for compatibility but it does nothing
            return;
        }



private void UpdateBackupStatus(string message, Color color)
{
    ApplyStatus(lblBackupStatus, message, color);
}

private void ApplyStatus(Label target, string message, Color color)
{
    bool isDark = Properties.Settings.Default.DarkMode;
    
    // Modern status with emoji icons
    string icon = "ℹ️";
    if (color == Color.Green || color == Color.FromArgb(16, 137, 62))
    {
        icon = "✓";
        target.BackColor = Color.FromArgb(240, 255, 240);
        target.ForeColor = Color.FromArgb(16, 137, 62);
    }
    else if (color == Color.Red)
    {
        icon = "✗";
        target.BackColor = Color.FromArgb(255, 240, 240);
        target.ForeColor = Color.FromArgb(196, 43, 28);
    }
    else if (color == Color.Blue || color == Color.FromArgb(0, 120, 212))
    {
        icon = "ℹ️";
        target.BackColor = Color.FromArgb(240, 248, 255);
        target.ForeColor = Color.FromArgb(0, 120, 212);
    }
    else if (color == Color.Orange)
    {
        icon = "⚠️";
        target.BackColor = Color.FromArgb(255, 248, 240);
        target.ForeColor = Color.FromArgb(196, 89, 17);
    }
    else
    {
        target.BackColor = Color.White;
        target.ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64);
    }

    target.Text = $"{icon} {message}";
}
        private void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private long GetDirectorySize(string path)
        {
            var dirInfo = new DirectoryInfo(path);
            return dirInfo.GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        }

        private void SaveProfileNames()
        {
            try
            {
                var configPath = Path.Combine(backupRootPath, "profile_names.txt");
                using (var writer = new StreamWriter(configPath, false))
                {
                    foreach (var pt in playthroughs.Values)
                    {
                        if (!string.IsNullOrEmpty(pt.CharacterName))
                        {
                            writer.WriteLine($"{pt.FolderName}|{pt.CharacterName}");
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveBackupData()
        {
            try
            {
                var configPath = Path.Combine(backupRootPath, "backup_data.txt");
                using (var writer = new StreamWriter(configPath, false))
                {
                    foreach (var backup in backups.Values)
                    {
                        // Format: Id|RealFolderName|RealTimestamp|CharacterName|PlaythroughFolderName|UserSaveName|SaveTimestamp|IsQuicksave
                        var realTimestamp = backup.RealTimestamp.ToString("o");
                        var saveTimestamp = backup.SaveTimestamp.ToString("o");
                        writer.WriteLine($"{backup.Id}|{backup.RealFolderName}|{realTimestamp}|{backup.CharacterName}|{backup.PlaythroughFolderName}|{backup.UserSaveName}|{saveTimestamp}|{backup.IsQuicksave}");
                    }
                }
            }
            catch { }
        }

        private void LoadProfileNames()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] LoadProfileNames called, {playthroughs.Count} playthroughs");
                
                // Load the list of profiles that have already been scanned
                var scannedProfilesPath = Path.Combine(backupRootPath, "scanned_profiles.txt");
                var scannedProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                if (File.Exists(scannedProfilesPath))
                {
                    var lines = File.ReadAllLines(scannedProfilesPath);
                    foreach (var line in lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            scannedProfiles.Add(line.Trim());
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"[DEBUG] {scannedProfiles.Count} profiles already scanned");
                
                // Load saved profile names
                var configPath = Path.Combine(backupRootPath, "profile_names.txt");
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 2 && playthroughs.ContainsKey(parts[0]))
                        {
                            playthroughs[parts[0]].CharacterName = parts[1];
                        }
                    }
                }
                
                // Auto-detect names for profiles that haven't been scanned yet
                bool anyNewScans = false;
                foreach (var pt in playthroughs.Values)
                {
                    // Skip if already scanned
                    if (scannedProfiles.Contains(pt.FolderName))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] Skipping already scanned: {pt.FolderName}");
                        continue;
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Scanning new profile: {pt.FolderName}");
                    
                    var autoName = GetAutoNameForProfile(pt.FolderPath);
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Auto-detected name: {autoName ?? "(null)"}");
                    
                    if (!string.IsNullOrEmpty(autoName))
                    {
                        pt.CharacterName = autoName;
                    }
                    else if (string.IsNullOrEmpty(pt.CharacterName))
                    {
                        // Fallback if divine.exe fails
                        pt.CharacterName = "Honor Mode Character";
                    }
                    
                    // Mark as scanned
                    scannedProfiles.Add(pt.FolderName);
                    anyNewScans = true;
                }
                
                // Save if we scanned any new profiles
                if (anyNewScans)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Saving updated profile names and scanned list");
                    SaveProfileNames();
                    
                    // Save scanned profiles list
                    try
                    {
                        File.WriteAllLines(scannedProfilesPath, scannedProfiles);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Exception in LoadProfileNames: {ex.Message}");
            }
        }

        private void LoadBackupData()
        {
            try
            {
                var configPath = Path.Combine(backupRootPath, "backup_data.txt");
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 8)
                        {
                            var id = parts[0];
                            var realFolderName = parts[1];
                            DateTime.TryParse(parts[2], out var realTimestamp);
                            var characterName = parts[3];
                            var playthroughFolderName = parts[4];
                            var userSaveName = parts[5];
                            DateTime.TryParse(parts[6], out var saveTimestamp);
                            bool.TryParse(parts[7], out var isQuicksave);
                            
                            // Check if folder still exists
                            var folderPath = Path.Combine(backupRootPath, realFolderName);
                            if (Directory.Exists(folderPath))
                            {
                                var backup = new BackupInfo
                                {
                                    Id = id,
                                    RealFolderName = realFolderName,
                                    RealTimestamp = realTimestamp,
                                    CharacterName = characterName,
                                    PlaythroughFolderName = playthroughFolderName,
                                    UserSaveName = userSaveName,
                                    SaveTimestamp = saveTimestamp,
                                    IsQuicksave = isQuicksave
                                };
                                backups[id] = backup;
                            }
                        }
                    }
                }
                
                // Also scan for any backup folders not in the data file (migration from old system)
                MigrateOldBackups();
            }
            catch { }
        }
        
        private void MigrateOldBackups()
        {
            if (!Directory.Exists(backupRootPath))
                return;
                
            var backupFolders = Directory.GetDirectories(backupRootPath);
            foreach (var folder in backupFolders)
            {
                var folderName = Path.GetFileName(folder);
                
                // Skip if already in our system (check by RealFolderName)
                if (backups.Values.Any(b => b.RealFolderName == folderName))
                    continue;
                
                // Get the playthrough folder inside
                var subFolders = Directory.GetDirectories(folder);
                if (subFolders.Length > 0)
                {
                    var playthroughFolderName = Path.GetFileName(subFolders[0]);
                    var characterName = GetCharacterNameForFolder(playthroughFolderName);
                    var creationTime = Directory.GetCreationTime(folder);
                    var isQuicksave = folderName.EndsWith("_quicksave", StringComparison.OrdinalIgnoreCase);
                    
                    var backup = new BackupInfo
                    {
                        Id = Guid.NewGuid().ToString(),
                        RealFolderName = folderName,
                        RealTimestamp = creationTime,
                        CharacterName = characterName,
                        PlaythroughFolderName = playthroughFolderName,
                        UserSaveName = isQuicksave ? "[Quicksave]" : "Migrated Save",
                        SaveTimestamp = creationTime,
                        IsQuicksave = isQuicksave
                    };
                    backups[backup.Id] = backup;
                }
            }
            
            // Check if we need to migrate from old backup_names.txt
            var oldConfigPath = Path.Combine(backupRootPath, "backup_names.txt");
            if (File.Exists(oldConfigPath))
            {
                try
                {
                    var lines = File.ReadAllLines(oldConfigPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 3)
                        {
                            var oldFolderName = parts[0];
                            var charName = parts[1];
                            var saveName = parts[2];
                            DateTime savedTimestamp = DateTime.MinValue;
                            if (parts.Length >= 4)
                            {
                                DateTime.TryParse(parts[3], out savedTimestamp);
                            }
                            
                            // Find matching backup by folder name and update it
                            var matchingBackup = backups.Values.FirstOrDefault(b => b.RealFolderName == oldFolderName);
                            if (matchingBackup != null)
                            {
                                matchingBackup.CharacterName = charName;
                                matchingBackup.UserSaveName = saveName;
                                if (savedTimestamp != DateTime.MinValue)
                                {
                                    matchingBackup.SaveTimestamp = savedTimestamp;
                                }
                            }
                        }
                    }
                    
                    // Rename old file so we don't migrate again
                    File.Move(oldConfigPath, oldConfigPath + ".migrated");
                }
                catch { }
            }
            
            // Save the migrated data
            SaveBackupData();
        }

        private string GetCharacterNameForFolder(string folderName)
        {
            if (playthroughs.ContainsKey(folderName))
            {
                return playthroughs[folderName].CharacterName;
            }
            return string.Empty;
        }

        private void SaveRestorationTracking()
        {
            if (selectedCharacter == null)
                return;
                
            try
            {
                var configPath = Path.Combine(backupRootPath, "restoration_tracking.txt");
                
                // Load existing data
                var trackingData = new Dictionary<string, (DateTime timestamp, string backupId)>();
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 3)
                        {
                            DateTime.TryParse(parts[1], out var ts);
                            trackingData[parts[0]] = (ts, parts[2]);
                        }
                    }
                }
                
                // Update current character's data
                if (lastRestorationTimestamp.HasValue && !string.IsNullOrEmpty(lastRestoredBackupId))
                {
                    trackingData[selectedCharacter.FolderName] = (lastRestorationTimestamp.Value, lastRestoredBackupId);
                }
                
                // Save all data
                using (var writer = new StreamWriter(configPath, false))
                {
                    foreach (var kvp in trackingData)
                    {
                        writer.WriteLine($"{kvp.Key}|{kvp.Value.timestamp:o}|{kvp.Value.backupId}");
                    }
                }
            }
            catch { }
        }

        private void LoadRestorationTracking()
        {
            lastRestorationTimestamp = null;
            lastRestoredBackupId = null;
            
            if (selectedCharacter == null)
                return;
                
            try
            {
                var configPath = Path.Combine(backupRootPath, "restoration_tracking.txt");
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 3 && parts[0] == selectedCharacter.FolderName)
                        {
                            if (DateTime.TryParse(parts[1], out var timestamp))
                            {
                                lastRestorationTimestamp = timestamp;
                            }
                            lastRestoredBackupId = parts[2];
                            break;
                        }
                    }
                }
            }
            catch { }
        }
        
        private void UpdateCurrentSaveLabel(PlaythroughInfo character)
        {
            var displayText = "Current Save";
            
            // Check if we have a restoration and the game file hasn't been modified since
            if (lastRestorationTimestamp.HasValue && !string.IsNullOrEmpty(lastRestoredBackupId))
            {
                // If the game's save file is newer than our restoration timestamp, show "Current Save"
                if (character.LastModified > lastRestorationTimestamp.Value.AddMinutes(1))
                {
                    // Game has saved since restoration - show current save with file timestamp
                    var timeStr = Properties.Settings.Default.Use24HourTime
                        ? character.LastModified.ToString("M/d/yyyy HH:mm")
                        : character.LastModified.ToString("M/d/yyyy h:mm tt");
                    displayText = $"Current Save - {timeStr}";
                }
                else
                {
                    // Show the restored backup's name and timestamp
                    var restoredBackup = backups.Values.FirstOrDefault(b => b.Id == lastRestoredBackupId);
                    if (restoredBackup != null)
                    {
                        var timeStr = Properties.Settings.Default.Use24HourTime
                            ? restoredBackup.SaveTimestamp.ToString("M/d/yyyy HH:mm")
                            : restoredBackup.SaveTimestamp.ToString("M/d/yyyy h:mm tt");
                        displayText = $"{restoredBackup.UserSaveName} - {timeStr}";
                    }
                    else
                    {
                        // Backup no longer exists, show restoration time
                        var timeStr = Properties.Settings.Default.Use24HourTime
                            ? lastRestorationTimestamp.Value.ToString("M/d/yyyy HH:mm")
                            : lastRestorationTimestamp.Value.ToString("M/d/yyyy h:mm tt");
                        displayText = $"Restored Save - {timeStr}";
                    }
                }
            }
            else
            {
                // No restoration tracking - check if we can match to a backup
                var matchingBackup = FindMatchingBackup(character);
                if (matchingBackup != null)
                {
                    var timeStr = Properties.Settings.Default.Use24HourTime
                        ? matchingBackup.SaveTimestamp.ToString("M/d/yyyy HH:mm")
                        : matchingBackup.SaveTimestamp.ToString("M/d/yyyy h:mm tt");
                    displayText = $"{matchingBackup.UserSaveName} - {timeStr}";
                }
                else
                {
                    var timeStr = Properties.Settings.Default.Use24HourTime
                        ? character.LastModified.ToString("M/d/yyyy HH:mm")
                        : character.LastModified.ToString("M/d/yyyy h:mm tt");
                    displayText = $"Current Save - {timeStr}";
                }
            }
            
            lblCurrentSave.Text = displayText;
        }

        private void ApplyTheme()
        {
            bool isDark = Properties.Settings.Default.DarkMode;
            
            // Main form
            this.BackColor = isDark ? darkBackground : lightBackground;
            
            // Group boxes
            grpBackup.ForeColor = isDark ? darkForeground : lightForeground;
            grpRestore.ForeColor = isDark ? darkForeground : lightForeground;
            grpChooseCharacter.ForeColor = isDark ? darkForeground : lightForeground;
            
            // Labels
            lblPlaythrough.ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64);
            lblBackup.ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64);
            lblBackupStatus.ForeColor = isDark ? Color.FromArgb(180, 180, 180) : Color.FromArgb(96, 96, 96);
            
            // Current save label
            lblCurrentSave.ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32);
            
            // Dropdowns
            cboBackups.BackColor = isDark ? darkControlBg : lightControlBg;
            cboBackups.ForeColor = isDark ? darkForeground : lightForeground;
            cboChooseCharacter.BackColor = isDark ? darkControlBg : lightControlBg;
            cboChooseCharacter.ForeColor = isDark ? darkForeground : lightForeground;
            
            // Settings button
            btnSettings.ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64);
            btnSettings.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);
            
            // Donation link
            if (lnkDonate != null)
            {
                lnkDonate.LinkColor = isDark ? Color.FromArgb(100, 180, 255) : Color.FromArgb(0, 120, 212);
                lnkDonate.ForeColor = isDark ? darkForeground : lightForeground;
            }
            
            // Quicksave label - update display to apply theme colors
            UpdateQuickSaveDisplay();
        }

        private void InitializeSaveWatcher()
        {
            if (!Directory.Exists(bg3SavePath))
                return;

            try
            {
                saveWatcher = new FileSystemWatcher
                {
                    Path = bg3SavePath,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                saveWatcher.Changed += OnSaveChanged;
                saveWatcher.Created += OnSaveChanged;
                saveWatcher.Deleted += OnSaveChanged;
                saveWatcher.Renamed += OnSaveChanged;
            }
            catch
            {
                // Silent fail if watcher can't be created
            }
        }

        private void OnSaveChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce - wait a bit for file operations to complete
            System.Threading.Thread.Sleep(500);

            // Must invoke on UI thread
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => RefreshPlaythroughs()));
            }
            else
            {
                RefreshPlaythroughs();
            }
        }

        private void RefreshPlaythroughs()
        {
            // Refresh - reload selected character if one is selected
            if (selectedCharacter != null)
            {
                // Character is selected - reload just that character's data
                LoadCharacterData(selectedCharacter);
            }
            else
            {
                // No character selected - just refresh the data
                LoadPlaythroughs();
            }
        }

        private BackupInfo? FindMatchingBackup(PlaythroughInfo playthrough)
        {
            if (backups == null || backups.Count == 0)
                return null;

            // Get the most recent save file info
            if (string.IsNullOrEmpty(playthrough.MostRecentSave) || !File.Exists(playthrough.MostRecentSave))
                return null;

            var currentSaveInfo = new FileInfo(playthrough.MostRecentSave);

            // Look for backup with matching character (excluding quicksaves)
            foreach (var backup in backups.Values.Where(b => 
                !b.IsQuicksave && b.CharacterName.Equals(playthrough.CharacterName, StringComparison.OrdinalIgnoreCase)))
            {
                // Get the backup's folder path
                var backupFolderPath = Path.Combine(backupRootPath, backup.RealFolderName);
                if (!Directory.Exists(backupFolderPath))
                    continue;

                // Get the backup's save folder
                var backupSaveFolder = Directory.GetDirectories(backupFolderPath)
                    .FirstOrDefault(d => Path.GetFileName(d).EndsWith("__HonourMode", StringComparison.OrdinalIgnoreCase));

                if (backupSaveFolder == null)
                    continue;

                // Get the most recent save from backup
                var backupSaveFiles = Directory.GetFiles(backupSaveFolder, "*.lsv")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .ToList();

                if (!backupSaveFiles.Any())
                    continue;

                var backupSaveInfo = new FileInfo(backupSaveFiles.First());

                // Compare timestamps (within 2 seconds) and file sizes
                var timeDiff = Math.Abs((currentSaveInfo.LastWriteTime - backupSaveInfo.LastWriteTime).TotalSeconds);
                if (timeDiff <= 2 && currentSaveInfo.Length == backupSaveInfo.Length)
                {
                    return backup;
                }
            }

            return null;
        }

        private string FormatDateTime(DateTime dateTime)
        {
            if (Properties.Settings.Default.Use24HourTime)
            {
                return dateTime.ToString("yyyy-MM-dd HH:mm:ss");
            }
            else
            {
                return dateTime.ToString("yyyy-MM-dd hh:mm:ss tt");
            }
        }

        /// <summary>
        /// Extracts character name from a BG3 save file (.lsv) using divine.exe
        /// </summary>
        private string? ExtractCharacterNameFromSave(string savePath)
        {
            try
            {
                if (!File.Exists(savePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Save file not found: {savePath}");
                    return null;
                }

                string divineExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "divine.exe");
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Looking for divine.exe at: {divineExePath}");
                
                if (!File.Exists(divineExePath))
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] divine.exe NOT FOUND!");
                    MessageBox.Show($"divine.exe not found at:\n{divineExePath}\n\nPlease ensure divine.exe is in the tools folder.", 
                        "Debug: divine.exe Missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"[DEBUG] divine.exe found, extracting save...");

                // Create a temporary directory for extraction
                string tempDir = Path.Combine(Path.GetTempPath(), $"bg3save_{Guid.NewGuid()}");
                Directory.CreateDirectory(tempDir);
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Temp dir: {tempDir}");

                try
                {
                    // Step 1: Extract the .lsv save file using divine.exe
                    var extractProcess = new ProcessStartInfo
                    {
                        FileName = divineExePath,
                        Arguments = $"--action extract-package --game bg3 --source \"{savePath}\" --destination \"{tempDir}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Running: {extractProcess.FileName} {extractProcess.Arguments}");

                    string extractOutput = "";
                    string extractError = "";
                    using (var process = Process.Start(extractProcess))
                    {
                        if (process == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] Failed to start extract process");
                            return null;
                        }
                        extractOutput = process.StandardOutput.ReadToEnd();
                        extractError = process.StandardError.ReadToEnd();
                        process.WaitForExit(30000);
                        
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] Extract exit code: {process.ExitCode}");
                        if (!string.IsNullOrEmpty(extractError))
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] Extract stderr: {extractError}");
                        
                        if (process.ExitCode != 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] Extract failed with code {process.ExitCode}");
                            return null;
                        }
                    }

                    // Step 2: Find and convert meta.lsf to meta.lsx
                    string metaLsfPath = Path.Combine(tempDir, "meta.lsf");
                    string metaLsxPath = Path.Combine(tempDir, "meta.lsx");

                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Looking for meta.lsf at: {metaLsfPath}");
                    
                    if (!File.Exists(metaLsfPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] meta.lsf NOT FOUND after extraction!");
                        // List what files ARE there
                        var files = Directory.GetFiles(tempDir);
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] Files in temp dir: {string.Join(", ", files.Select(Path.GetFileName))}");
                        return null;
                    }

                    var convertProcess = new ProcessStartInfo
                    {
                        FileName = divineExePath,
                        Arguments = $"--action convert-resource --game bg3 --source \"{metaLsfPath}\" --destination \"{metaLsxPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Running: {convertProcess.FileName} {convertProcess.Arguments}");

                    using (var process = Process.Start(convertProcess))
                    {
                        if (process == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] Failed to start convert process");
                            return null;
                        }
                        var convertError = process.StandardError.ReadToEnd();
                        process.WaitForExit(30000);
                        
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] Convert exit code: {process.ExitCode}");
                        if (!string.IsNullOrEmpty(convertError))
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] Convert stderr: {convertError}");
                        
                        if (process.ExitCode != 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] Convert failed with code {process.ExitCode}");
                            return null;
                        }
                    }

                    // Step 3: Parse the XML to find character name
                    if (!File.Exists(metaLsxPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG] meta.lsx NOT FOUND after conversion!");
                        return null;
                    }

                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Parsing meta.lsx...");
                    string? characterName = ParseCharacterNameFromMetaLsx(metaLsxPath);
                    System.Diagnostics.Debug.WriteLine($"[DEBUG] Extracted character name: {characterName ?? "(null)"}");
                    
                    return characterName;
                }
                finally
                {
                    // Clean up temporary directory
                    try
                    {
                        if (Directory.Exists(tempDir))
                            Directory.Delete(tempDir, true);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] Exception in ExtractCharacterNameFromSave: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parse the meta.lsx XML file to extract character name
        /// </summary>
        private string? ParseCharacterNameFromMetaLsx(string metaLsxPath)
        {
            try
            {
                string xmlContent = File.ReadAllText(metaLsxPath);
                
                var doc = new System.Xml.XmlDocument();
                doc.LoadXml(xmlContent);

                // The character name is in: attribute id="LeaderName" type="LSString" value="CharacterName"
                var leaderNameNode = doc.SelectSingleNode("//attribute[@id='LeaderName']");
                if (leaderNameNode != null)
                {
                    var valueAttr = leaderNameNode.Attributes?["value"];
                    if (valueAttr != null && !string.IsNullOrWhiteSpace(valueAttr.Value))
                    {
                        return valueAttr.Value;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Try to auto-name a profile based on save file contents
        /// </summary>
        private string? GetAutoNameForProfile(string profileFolderPath)
        {
            try
            {
                // Check if divine.exe exists
                string divineExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", "divine.exe");
                if (!File.Exists(divineExePath))
                    return null;

                // Find any .lsv file in the profile folder
                var lsvFiles = Directory.GetFiles(profileFolderPath, "*.lsv", SearchOption.TopDirectoryOnly);
                if (lsvFiles.Length == 0)
                    return null;

                // Use the most recent save file
                var mostRecent = lsvFiles
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();

                if (mostRecent == null)
                    return null;

                return ExtractCharacterNameFromSave(mostRecent.FullName);
            }
            catch
            {
                return null;
            }
        }
    }

    public class PlaythroughInfo
    {
        public string FolderPath { get; set; } = string.Empty;
        public string FolderName { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string UserDefinedName { get; set; } = string.Empty;
        public string MostRecentSave { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
    }

    public class BackupInfo
    {
        public string Id { get; set; } = string.Empty;              // Unique identifier (GUID)
        public string RealFolderName { get; set; } = string.Empty;  // Actual folder name on disk
        public DateTime RealTimestamp { get; set; }                  // Filesystem timestamp
        public string CharacterName { get; set; } = string.Empty;   // Character this backup belongs to
        public string PlaythroughFolderName { get; set; } = string.Empty; // The playthrough folder inside backup
        public string UserSaveName { get; set; } = string.Empty;    // User-provided display name
        public DateTime SaveTimestamp { get; set; }                  // When backup was created (our timestamp)
        public bool IsQuicksave { get; set; } = false;              // True if this is a quicksave
    }
}