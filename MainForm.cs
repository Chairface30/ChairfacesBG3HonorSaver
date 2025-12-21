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
        private const int VK_F5 = 0x74;
        private const int VK_F6 = 0x75;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private LowLevelKeyboardProc _keyboardProc = null!;
        private IntPtr _hookID = IntPtr.Zero;

        private Label lblPlaythroughValue = null!;
        private ComboBox cboBackups = null!;
        private Button btnBackup = null!;
        private Button btnRestore = null!;
        private Button btnDeleteBackup = null!;
        private Label lblPlaythrough = null!;
        private Label lblBackup = null!;
        private Label lblPlaythroughStatus = null!;
        private Label lblBackupStatus = null!;
        private LinkLabel lnkDonate = null!;
        private GroupBox grpBackup = null!;
        private GroupBox grpRestore = null!;
        private Button btnRestoreAfterDeath = null!;

        private string bg3SavePath = string.Empty;
        private string backupRootPath = string.Empty;
        private string bg3ProfilePath = string.Empty;
        private Dictionary<string, PlaythroughInfo> playthroughs = new();
        private Dictionary<string, BackupInfo> backups = new();
        private FileSystemWatcher? saveWatcher;
        private string lastSelectedProfile = string.Empty;
        private string lastSelectedBackup = string.Empty;
        private DateTime? quickSaveTimestamp = null;
        private Label lblQuickSave = null!;
        private Label lblQuickRestore = null!;
        private PlaythroughInfo? selectedCharacter = null;
        private Button btnChooseCharacter = null!;

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
            
            // Set up global keyboard hook for F5 quicksave
            _keyboardProc = HookCallback;
            _hookID = SetHook(_keyboardProc);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            
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
                        return; // Skip the selection dialog
                    }
                }
            }
            catch
            {
                // Setting doesn't exist yet - will show selection dialog
            }
            
            // No saved character or it no longer exists - show selection dialog
            // Hide the main form until character is selected
            this.Opacity = 0;
            this.ShowInTaskbar = false;
            
            // Show startup character selection
            if (!ShowStartupCharacterSelection())
            {
                // User cancelled or closed - exit the application
                this.Close();
                Application.Exit();
                return;
            }
            
            // Show the main form
            this.Opacity = 1;
            this.ShowInTaskbar = true;
        }

        private bool ShowStartupCharacterSelection()
        {
            bool isDark = Properties.Settings.Default.DarkMode;
            
            using (var startupDialog = new Form())
            {
                startupDialog.Text = "Select Character - Chairface's BG3 Honor Saver";
                startupDialog.Size = new Size(550, 480);
                startupDialog.StartPosition = FormStartPosition.CenterScreen;
                startupDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                startupDialog.MaximizeBox = false;
                startupDialog.MinimizeBox = false;
                startupDialog.BackColor = isDark ? darkBackground : Color.White;
                startupDialog.Font = new Font("Segoe UI", 10F);
                
                // Try to set the icon
                try
                {
                    var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                    if (File.Exists(iconPath))
                    {
                        startupDialog.Icon = new Icon(iconPath);
                    }
                }
                catch { }

                var lblPrompt = new Label
                {
                    Text = "Welcome! Select a character to manage backups for:",
                    Location = new Point(20, 20),
                    Size = new Size(500, 30),
                    Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32)
                };

                var listCharacters = new ListBox
                {
                    Location = new Point(20, 60),
                    Size = new Size(500, 280),
                    Font = new Font("Segoe UI", 11F),
                    BackColor = isDark ? darkControlBg : Color.White,
                    ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32)
                };

                // Populate with available characters
                var characterMap = new Dictionary<string, PlaythroughInfo>();
                foreach (var pt in playthroughs.Values.OrderByDescending(p => p.LastModified))
                {
                    var displayName = !string.IsNullOrEmpty(pt.CharacterName)
                        ? pt.CharacterName
                        : "[Unnamed Character]";
                    var timeStr = Properties.Settings.Default.Use24HourTime
                        ? pt.LastModified.ToString("M/d/yyyy HH:mm")
                        : pt.LastModified.ToString("M/d/yyyy h:mm tt");
                    var item = $"{displayName} - Last played: {timeStr}";
                    listCharacters.Items.Add(item);
                    characterMap[item] = pt;
                }

                var lblHint = new Label
                {
                    Text = "Character names are automatically detected from save files.",
                    Location = new Point(20, 350),
                    Size = new Size(500, 25),
                    Font = new Font("Segoe UI", 9F, FontStyle.Italic),
                    ForeColor = isDark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(128, 128, 128)
                };

                var btnSelect = new Button
                {
                    Text = "Select Character",
                    Location = new Point(270, 390),
                    Size = new Size(150, 40),
                    BackColor = Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    Enabled = false
                };
                btnSelect.FlatAppearance.BorderSize = 0;

                var btnExit = new Button
                {
                    Text = "Exit",
                    Location = new Point(430, 390),
                    Size = new Size(90, 40),
                    BackColor = isDark ? darkControlBg : Color.FromArgb(243, 243, 243),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F),
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.Cancel
                };
                btnExit.FlatAppearance.BorderSize = 1;
                btnExit.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);

                listCharacters.SelectedIndexChanged += (s, ev) =>
                {
                    btnSelect.Enabled = listCharacters.SelectedIndex >= 0;
                };

                listCharacters.DoubleClick += (s, ev) =>
                {
                    if (listCharacters.SelectedIndex >= 0)
                    {
                        btnSelect.PerformClick();
                    }
                };

                PlaythroughInfo? selectedPlaythrough = null;

                btnSelect.Click += (s, ev) =>
                {
                    if (listCharacters.SelectedIndex >= 0)
                    {
                        var selectedItem = listCharacters.SelectedItem?.ToString();
                        if (selectedItem != null && characterMap.TryGetValue(selectedItem, out var pt))
                        {
                            selectedPlaythrough = pt;
                            
                            // If still unnamed after auto-detection, use a default name
                            if (string.IsNullOrEmpty(pt.CharacterName))
                            {
                                pt.CharacterName = "Honor Mode Character";
                                SaveProfileNames();
                            }
                            
                            startupDialog.DialogResult = DialogResult.OK;
                            startupDialog.Close();
                        }
                    }
                };

                startupDialog.Controls.Add(lblPrompt);
                startupDialog.Controls.Add(listCharacters);
                startupDialog.Controls.Add(lblHint);
                startupDialog.Controls.Add(btnSelect);
                startupDialog.Controls.Add(btnExit);
                startupDialog.CancelButton = btnExit;

                // Select first item by default if available
                if (listCharacters.Items.Count > 0)
                {
                    listCharacters.SelectedIndex = 0;
                }

                // Hide main window while dialog is open
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                
                if (startupDialog.ShowDialog() == DialogResult.OK && selectedPlaythrough != null)
                {
                    // Restore main window visibility
                    this.Opacity = 1;
                    this.ShowInTaskbar = true;
                    
                    // Store selected character
                    selectedCharacter = selectedPlaythrough;
                    
                    // Save the selection for next time
                    Properties.Settings.Default.LastSelectedCharacter = selectedPlaythrough.FolderName;
                    Properties.Settings.Default.Save();
                    
                    // Enable backup and restore groups
                    grpBackup.Enabled = true;
                    grpRestore.Enabled = true;
                    
                    // Load filtered data for this character
                    LoadCharacterData(selectedPlaythrough);
                    
                    return true;
                }
                
                // Restore main window visibility
                this.Opacity = 1;
                this.ShowInTaskbar = true;
                
                return false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                saveWatcher?.Dispose();
                
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
            this.Size = new Size(750, 475);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimumSize = new Size(750, 475);
            this.MaximumSize = new Size(750, 475);
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
                Size = new Size(715, 150),
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
                Font = new Font("Segoe UI", 10.5F),
                ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                TextAlign = ContentAlignment.MiddleLeft
            };

            lblPlaythroughValue = new Label
            {
                Location = new Point(140, 26),
                Size = new Size(560, 30),
                Font = new Font("Segoe UI", 10.5F),
                ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                BackColor = isDark ? darkControlBg : Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(8, 4, 8, 4),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Select a character to view saves.",
                AutoEllipsis = true
            };


// Playthrough Status Bar (under Select Profile)
lblPlaythroughStatus = new Label
{
    Location = new Point(140, 60),
    Size = new Size(560, 28),
    BorderStyle = BorderStyle.None,
    BackColor = Color.White,
    Text = "✓ Ready",
    Padding = new Padding(10, 6, 10, 6),
    Font = new Font("Segoe UI", 9.5F),
    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
    TextAlign = ContentAlignment.MiddleLeft
};

            btnBackup = new Button
            {
                Text = "Backup",
                Location = new Point(595, 92),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(16, 137, 62),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnBackup.FlatAppearance.BorderSize = 0;
            btnBackup.FlatAppearance.MouseOverBackColor = Color.FromArgb(14, 120, 54);
            btnBackup.FlatAppearance.MouseDownBackColor = Color.FromArgb(12, 105, 47);
            btnBackup.Click += BtnBackup_Click;

            // Quicksave timestamp label
            lblQuickSave = new Label
            {
                Text = "[F5] Quicksave: None",
                Location = new Point(15, 100),
                Size = new Size(250, 25),
                Font = new Font("Segoe UI", 9F),
                ForeColor = isDark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(96, 96, 96),
                TextAlign = ContentAlignment.MiddleLeft
            };

            grpBackup.Controls.Add(lblPlaythrough);
            grpBackup.Controls.Add(lblPlaythroughValue);
            grpBackup.Controls.Add(lblPlaythroughStatus);
            grpBackup.Controls.Add(btnBackup);
            grpBackup.Controls.Add(lblQuickSave);

            // Restore Group - Modern card style (moved up since character group removed)
            grpRestore = new GroupBox
            {
                Text = "  Restore Heroic Save  ",
                Location = new Point(15, 180),
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
                Size = new Size(560, 30),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10.5F),
                FlatStyle = FlatStyle.Flat
            };


// Backup Status Bar (under Select Backup)
lblBackupStatus = new Label
{
    Location = new Point(140, 60),
    Size = new Size(560, 28),
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
                Location = new Point(375, 92),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(196, 43, 28),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnDeleteBackup.FlatAppearance.BorderSize = 0;
            btnDeleteBackup.FlatAppearance.MouseOverBackColor = Color.FromArgb(176, 38, 25);
            btnDeleteBackup.FlatAppearance.MouseDownBackColor = Color.FromArgb(156, 33, 22);
            btnDeleteBackup.Click += BtnDeleteBackup_Click;

            btnRestore = new Button
            {
                Text = "Restore",
                Location = new Point(485, 92),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRestore.FlatAppearance.BorderSize = 0;
            btnRestore.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 102, 180);
            btnRestore.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 90, 158);
            btnRestore.Click += BtnRestore_Click;

            btnRestoreAfterDeath = new Button
            {
                Text = "☠ After Death",
                Location = new Point(595, 92),
                Size = new Size(105, 35),
                BackColor = Color.FromArgb(128, 0, 128),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnRestoreAfterDeath.FlatAppearance.BorderSize = 0;
            btnRestoreAfterDeath.FlatAppearance.MouseOverBackColor = Color.FromArgb(108, 0, 108);
            btnRestoreAfterDeath.FlatAppearance.MouseDownBackColor = Color.FromArgb(88, 0, 88);
            btnRestoreAfterDeath.Click += BtnRestoreAfterDeath_Click;

            // Quick-restore label with warning
            lblQuickRestore = new Label
            {
                Text = "[F6] Quick-restore: Overwrites current save with quicksave. Back up first!",
                Location = new Point(15, 100),
                Size = new Size(355, 40),
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

            // Choose Character Button - styled like restore button
            btnChooseCharacter = new Button
            {
                Text = "Choose Character",
                Location = new Point(15, 345),
                Size = new Size(160, 38),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Name = "btnChooseCharacter"
            };
            btnChooseCharacter.FlatAppearance.BorderSize = 0;
            btnChooseCharacter.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 102, 180);
            btnChooseCharacter.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 90, 158);
            btnChooseCharacter.Click += BtnSelectCharacter_Click;

            // Settings Button (Gear icon) - Bottom right
            var btnSettings = new Button
            {
                Text = "⚙️",
                Location = new Point(685, 345),
                Size = new Size(45, 38),
                BackColor = isDark ? darkControlBg : Color.FromArgb(243, 243, 243),
                ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 16F),
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter
            };
            btnSettings.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);
            btnSettings.FlatAppearance.BorderSize = 1;
            btnSettings.FlatAppearance.MouseOverBackColor = isDark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(233, 233, 233);
            btnSettings.FlatAppearance.MouseDownBackColor = isDark ? Color.FromArgb(75, 75, 75) : Color.FromArgb(223, 223, 223);
            btnSettings.Click += BtnSettings_Click;

            // Donation Link - Modern subtle style
            lnkDonate = new LinkLabel
            {
                Location = new Point(15, 395),
                Size = new Size(715, 25),
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

            // Add all controls to form
            this.Controls.Add(grpBackup);
            this.Controls.Add(grpRestore);
            this.Controls.Add(btnChooseCharacter);
            this.Controls.Add(btnSettings);
            this.Controls.Add(lnkDonate);
        }

        private void BtnSelectCharacter_Click(object? sender, EventArgs e)
        {
            // Show character selection dialog
            using (var characterDialog = new Form())
            {
                bool isDark = Properties.Settings.Default.DarkMode;
                
                characterDialog.Text = "Select Character";
                characterDialog.Size = new Size(500, 400);
                characterDialog.StartPosition = FormStartPosition.CenterParent;
                characterDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                characterDialog.MaximizeBox = false;
                characterDialog.MinimizeBox = false;
                characterDialog.BackColor = isDark ? darkBackground : Color.White;
                characterDialog.Font = new Font("Segoe UI", 10F);

                var lblPrompt = new Label
                {
                    Text = "Select a character to manage backups for:",
                    Location = new Point(20, 20),
                    Size = new Size(450, 30),
                    Font = new Font("Segoe UI", 11F),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32)
                };

                var listCharacters = new ListBox
                {
                    Location = new Point(20, 60),
                    Size = new Size(450, 250),
                    Font = new Font("Segoe UI", 11F),
                    BackColor = isDark ? darkControlBg : Color.White,
                    ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32)
                };

                // Populate with available characters
                LoadPlaythroughs(); // Refresh data
                foreach (var pt in playthroughs.Values.OrderByDescending(p => p.LastModified))
                {
                    var displayName = !string.IsNullOrEmpty(pt.CharacterName)
                        ? pt.CharacterName
                        : "[Unnamed Character]";
                    var item = $"{displayName} ({pt.LastModified.ToString("yyyy-MM-dd HH:mm")})";
                    listCharacters.Items.Add(item);
                    listCharacters.Tag = listCharacters.Tag ?? new Dictionary<string, PlaythroughInfo>();
                    ((Dictionary<string, PlaythroughInfo>)listCharacters.Tag)[item] = pt;
                }

                var btnSelect = new Button
                {
                    Text = "Select",
                    Location = new Point(260, 320),
                    Size = new Size(100, 35),
                    BackColor = Color.FromArgb(0, 120, 212),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    Enabled = false
                };
                btnSelect.FlatAppearance.BorderSize = 0;

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    Location = new Point(370, 320),
                    Size = new Size(100, 35),
                    BackColor = isDark ? darkControlBg : Color.FromArgb(243, 243, 243),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64),
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 10.5F),
                    Cursor = Cursors.Hand,
                    DialogResult = DialogResult.Cancel
                };
                btnCancel.FlatAppearance.BorderSize = 1;
                btnCancel.FlatAppearance.BorderColor = isDark ? darkBorder : Color.FromArgb(200, 200, 200);

                listCharacters.SelectedIndexChanged += (s, ev) =>
                {
                    btnSelect.Enabled = listCharacters.SelectedIndex >= 0;
                };

                listCharacters.DoubleClick += (s, ev) =>
                {
                    if (listCharacters.SelectedIndex >= 0)
                    {
                        btnSelect.PerformClick();
                    }
                };

                btnSelect.Click += (s, ev) =>
                {
                    if (listCharacters.SelectedIndex >= 0)
                    {
                        var selectedItem = listCharacters.SelectedItem?.ToString();
                        if (selectedItem != null && listCharacters.Tag is Dictionary<string, PlaythroughInfo> dict)
                        {
                            characterDialog.Tag = dict[selectedItem];
                            characterDialog.DialogResult = DialogResult.OK;
                            characterDialog.Close();
                        }
                    }
                };

                characterDialog.Controls.Add(lblPrompt);
                characterDialog.Controls.Add(listCharacters);
                characterDialog.Controls.Add(btnSelect);
                characterDialog.Controls.Add(btnCancel);

                // Hide main window while dialog is open
                this.Opacity = 0;
                this.ShowInTaskbar = false;
                
                var result = characterDialog.ShowDialog();
                
                // Restore main window visibility
                this.Opacity = 1;
                this.ShowInTaskbar = true;
                
                if (result == DialogResult.OK && characterDialog.Tag is PlaythroughInfo selectedPlaythrough)
                {
                    // If still unnamed after auto-detection, use a default name
                    if (string.IsNullOrEmpty(selectedPlaythrough.CharacterName))
                    {
                        selectedPlaythrough.CharacterName = "Honor Mode Character";
                        SaveProfileNames();
                    }
                    
                    // Store selected character
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
                UpdatePlaythroughStatus("Character not found", Color.Red);
                return;
            }
            
            // Update the stored selected character with fresh data
            selectedCharacter = updatedCharacter;
            
            // Use the updated character for display
            character = updatedCharacter;
            
            // Save current backup selection before clearing
            var previousBackupSelection = cboBackups.SelectedItem?.ToString();
            cboBackups.Items.Clear();

            // Load only this character's save states
            var timeStr = Properties.Settings.Default.Use24HourTime
                ? character.LastModified.ToString("M/d/yyyy HH:mm")
                : character.LastModified.ToString("M/d/yyyy h:mm tt");

            var displayText = "";
            // Show restored save name only if the file hasn't been modified significantly after the restore
            // (i.e., no new game save has occurred since the restore)
            if (!string.IsNullOrEmpty(character.LastRestoredSave) &&
                character.LastModified <= character.LastRestoreTime.AddMinutes(1))
            {
                // Show: "Save Name - Time"
                displayText = $"{character.LastRestoredSave} - {timeStr}";
            }
            else
            {
                var matchingBackup = FindMatchingBackup(selectedCharacter);
                if (matchingBackup != null)
                {
                    // Show: "Save Name - Time"
                    displayText = $"{matchingBackup.SaveName} - {timeStr}";
                }
                else
                {
                    // Show: "Current Save - Time"
                    displayText = $"Current Save - {timeStr}";
                }
            }

            lblPlaythroughValue.Text = displayText;

            var charName = !string.IsNullOrEmpty(selectedCharacter.CharacterName)
                ? selectedCharacter.CharacterName
                : "[Unnamed Character]";
            
            UpdatePlaythroughStatus($"Showing saves for: {charName}", Color.Green);

            // Load only this character's backups
            LoadBackups();
            
            // Filter backups to only show this character's
            var characterBackups = backups.Values
                .Where(b => b.CharacterName.Equals(selectedCharacter.CharacterName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.CreatedDate)
                .ToList();

            cboBackups.Items.Clear();
            foreach (var backup in characterBackups)
            {
                var saveName = !string.IsNullOrEmpty(backup.SaveName)
                    ? backup.SaveName
                    : "[Unnamed Save]";
                
                // Format backup time with 12/24 hour setting
                var backupTimeStr = Properties.Settings.Default.Use24HourTime
                    ? backup.CreatedDate.ToString("M/d/yyyy HH:mm")
                    : backup.CreatedDate.ToString("M/d/yyyy h:mm tt");
                
                // Show: "Save Name - Time"
                var backupDisplay = $"{saveName} - {backupTimeStr}";
                cboBackups.Items.Add(backupDisplay);
            }

            if (cboBackups.Items.Count > 0)
            {
                // Try to restore previous selection
                bool selectionRestored = false;
                if (!string.IsNullOrEmpty(previousBackupSelection))
                {
                    for (int i = 0; i < cboBackups.Items.Count; i++)
                    {
                        if (cboBackups.Items[i]?.ToString() == previousBackupSelection)
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
                
                UpdateBackupStatus($"Found {cboBackups.Items.Count} backup(s) for {charName}", Color.Green);
            }
            else
            {
                UpdateBackupStatus($"No backups found for {charName}", Color.Orange);
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
                            UpdatePlaythroughStatus("BG3 save folder changed", Color.FromArgb(0, 120, 212));
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
                UpdatePlaythroughStatus($"BG3 save folder not found: {bg3SavePath}", Color.Red);
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
                
                // Load restore tracking
                LoadRestoreTracking();

                // NOTE: Playthrough display is now handled by LoadCharacterData after character selection.
                
                if (playthroughs.Count > 0)
                {
                    UpdatePlaythroughStatus($"Found {playthroughs.Count} Honor Mode playthrough(s). Select a character to continue.", Color.Green);
                }
                else
                {
                    UpdatePlaythroughStatus("No Honor Mode playthroughs found.", Color.Orange);
                }
            }
            catch (Exception ex)
            {
                UpdatePlaythroughStatus($"Error loading playthroughs: {ex.Message}", Color.Red);
            }

            LoadBackups();
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
                var backupFolders = Directory.GetDirectories(backupRootPath)
                    .Where(d => !Path.GetFileName(d).EndsWith("_quicksave", StringComparison.OrdinalIgnoreCase)) // Exclude quicksaves
                    .OrderByDescending(d => Directory.GetCreationTime(d))
                    .ToList();

                foreach (var folder in backupFolders)
                {
                    var folderName = Path.GetFileName(folder);
                    var creationTime = Directory.GetCreationTime(folder);
                    
                    // Get the playthrough folder inside the backup
                    var subFolders = Directory.GetDirectories(folder);
                    if (subFolders.Length > 0)
                    {
                        var playthroughFolderName = Path.GetFileName(subFolders[0]);
                        var characterName = GetCharacterNameForFolder(playthroughFolderName);
                        
                        var backupInfo = new BackupInfo
                        {
                            BackupFolderPath = folder,
                            PlaythroughFolderName = playthroughFolderName,
                            CharacterName = characterName,
                            SaveName = string.Empty,
                            CreatedDate = creationTime
                        };
                        
                        backups[folderName] = backupInfo;
                    }
                }

                // Load saved backup names
                LoadBackupNames();

                // Group backups by character and display
                var groupedBackups = backups.Values
                    .GroupBy(b => string.IsNullOrEmpty(b.CharacterName) ? "[Unnamed Character]" : b.CharacterName)
                    .OrderBy(g => g.Key);

                foreach (var group in groupedBackups)
                {
                    // Sort saves within character group by date (most recent first)
                    var sortedSaves = group.OrderByDescending(b => b.CreatedDate);
                    
                    foreach (var backup in sortedSaves)
                    {
                        var saveName = !string.IsNullOrEmpty(backup.SaveName) 
                            ? backup.SaveName 
                            : "[Unnamed Save]";
                        var displayText = $"{group.Key} - {saveName}";
                        cboBackups.Items.Add(displayText);
                    }
                }

                if (cboBackups.Items.Count > 0)
                {
                    cboBackups.SelectedIndex = 0;
                }
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
                            
                            LoadCharacterData(playthroughInfo);
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
                    Location = new Point(280, 145),
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
                    Location = new Point(180, 145),
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
                        b.SaveName.Equals(saveName, StringComparison.OrdinalIgnoreCase)
                    );

                    if (existingBackup != null)
                    {
                        var result = MessageBox.Show(
                            $"A backup named '{saveName}' already exists for {playthroughInfo.CharacterName}.\n\nCreated: {FormatDateTime(existingBackup.CreatedDate)}\n\nDo you want to overwrite it with this new backup?",
                            "Duplicate Save Name",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning
                        );

                        if (result == DialogResult.Yes)
                        {
                            // Delete the old backup folder
                            try
                            {
                                if (Directory.Exists(existingBackup.BackupFolderPath))
                                {
                                    Directory.Delete(existingBackup.BackupFolderPath, true);
                                }
                                // Remove from dictionary
                                var oldBackupKey = Path.GetFileName(existingBackup.BackupFolderPath);
                                if (!string.IsNullOrEmpty(oldBackupKey))
                                {
                                    backups.Remove(oldBackupKey);
                                }
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
                    BackupFolderPath = backupPath,
                    PlaythroughFolderName = playthroughInfo.FolderName,
                    CharacterName = playthroughInfo.CharacterName,
                    SaveName = saveName,
                    CreatedDate = DateTime.Now
                };
                backups[backupFolderName] = backup;
                SaveBackupNames();

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
            if (cboBackups.SelectedIndex == -1)
            {
                MessageBox.Show("Please select a backup to restore.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedText = cboBackups.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedText))
            {
                UpdateBackupStatus("Error: Invalid selection.", Color.Red);
                return;
            }

            // Get selected character
            if (selectedCharacter == null)
            {
                MessageBox.Show("Please select a character first.", "No Character Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Find the matching backup (format is now "SaveName - Time")
            BackupInfo? selectedBackup = null;
            foreach (var backup in backups.Values)
            {
                // Only check backups for this character
                if (!backup.CharacterName.Equals(selectedCharacter.CharacterName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var saveName = !string.IsNullOrEmpty(backup.SaveName) 
                    ? backup.SaveName 
                    : "[Unnamed Save]";
                
                var backupTimeStr = Properties.Settings.Default.Use24HourTime
                    ? backup.CreatedDate.ToString("M/d/yyyy HH:mm")
                    : backup.CreatedDate.ToString("M/d/yyyy h:mm tt");
                
                var backupDisplay = $"{saveName} - {backupTimeStr}";
                if (backupDisplay == selectedText)
                {
                    selectedBackup = backup;
                    break;
                }
            }

            if (selectedBackup == null)
            {
                UpdateBackupStatus("Error: Backup not found.", Color.Red);
                return;
            }

            var backupPath = selectedBackup.BackupFolderPath;
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
            var saveDisplayName = !string.IsNullOrEmpty(selectedBackup.SaveName) 
                ? selectedBackup.SaveName 
                : "[Unnamed Save]";

            // Only warn if folder actually exists
            bool folderExists = Directory.Exists(targetPath);
            
            // Create custom dialog for restore confirmation
            using (var restoreDialog = new Form())
            {
                bool isDark = Properties.Settings.Default.DarkMode;
                
                restoreDialog.Text = isAfterDeath ? "Restore After Death" : "Confirm Restore";
                restoreDialog.Size = new Size(450, 220);
                restoreDialog.StartPosition = FormStartPosition.CenterParent;
                restoreDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                restoreDialog.MaximizeBox = false;
                restoreDialog.MinimizeBox = false;
                restoreDialog.BackColor = isDark ? darkBackground : Color.White;
                restoreDialog.Font = new Font("Segoe UI", 10F);

                // Build the message based on context
                string message;
                if (isAfterDeath)
                {
                    message = folderExists
                        ? $"⚠️ Game will be CLOSED and RELAUNCHED\n\nThis will DELETE and replace:\n{profileDisplayName}\n\nWith backup:\n{saveDisplayName} ({FormatDateTime(selectedBackup.CreatedDate)})"
                        : $"⚠️ Game will be CLOSED and RELAUNCHED\n\nRestore:\n{profileDisplayName}\n\nFrom backup:\n{saveDisplayName} ({FormatDateTime(selectedBackup.CreatedDate)})";
                }
                else
                {
                    message = folderExists
                        ? $"This will DELETE and replace:\n{profileDisplayName}\n\nWith backup:\n{saveDisplayName} ({FormatDateTime(selectedBackup.CreatedDate)})"
                        : $"Restore:\n{profileDisplayName}\n\nFrom backup:\n{saveDisplayName} ({FormatDateTime(selectedBackup.CreatedDate)})";
                }

                var lblMessage = new Label
                {
                    Text = message,
                    Location = new Point(20, 20),
                    Size = new Size(410, 100),
                    Font = new Font("Segoe UI", 10F),
                    ForeColor = isDark ? darkForeground : Color.FromArgb(32, 32, 32)
                };

                var btnYes = new Button
                {
                    Text = "Restore",
                    Location = new Point(200, 130),
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
                    Location = new Point(315, 130),
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
                var profileBackupPath = Path.Combine(selectedBackup.BackupFolderPath, "profile8.lsf");
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

                // Record the restore
                if (playthroughs.ContainsKey(playthroughFolderName))
                {
                    playthroughs[playthroughFolderName].LastRestoredSave = saveDisplayName;
                    playthroughs[playthroughFolderName].LastRestoreTime = DateTime.Now;
                    SaveRestoreTracking();
                }

                UpdateBackupStatus($"Restore completed for '{profileDisplayName}' successfully!", Color.Green);
                
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
                                
                                MessageBox.Show($"Restore completed successfully!\n\nBaldur's Gate 3 is launching.\nThe Honor Mode flag has been restored.", "Success - Game Launching", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                        MessageBox.Show($"Restore completed successfully!\n\nHowever, could not auto-launch game:\n{launchEx.Message}\n\nPlease launch BG3 manually.", "Success - Manual Launch Required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else
                {
                    MessageBox.Show($"Restore completed successfully for {profileDisplayName}!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (e.KeyCode == Keys.F5)
            {
                DoQuickSave();
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
                if (vkCode == VK_F5)
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
                else if (vkCode == VK_F6)
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

                // Delete existing quicksave for this character if it exists (silent overwrite)
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

                // Update quicksave timestamp
                quickSaveTimestamp = DateTime.Now;
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

            // Build quicksave path
            var quicksaveFolderName = $"{selectedCharacter.FolderName}_quicksave";
            var quicksavePath = Path.Combine(backupRootPath, quicksaveFolderName);

            // Check if quicksave exists
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

                // Record the quick-restore using the existing tracking system
                if (playthroughs.ContainsKey(selectedCharacter.FolderName))
                {
                    playthroughs[selectedCharacter.FolderName].LastRestoredSave = "[Quicksave]";
                    playthroughs[selectedCharacter.FolderName].LastRestoreTime = DateTime.Now;
                    SaveRestoreTracking();
                }

                // Update the dropdown to show "Quicksave" and the timestamp
                UpdatePlaythroughDropdownForQuicksave();
            }
            catch
            {
                // Silent fail - quick-restore should never interrupt the user
            }
        }

        private void UpdatePlaythroughDropdownForQuicksave()
        {
            if (quickSaveTimestamp.HasValue)
            {
                var timeStr = Properties.Settings.Default.Use24HourTime
                    ? quickSaveTimestamp.Value.ToString("M/d/yyyy HH:mm")
                    : quickSaveTimestamp.Value.ToString("M/d/yyyy h:mm tt");
                
                lblPlaythroughValue.Text = $"[Quicksave] - {timeStr}";
            }
        }

        private void UpdateQuickSaveDisplay()
        {
            if (lblQuickSave == null)
                return;

            bool isDark = Properties.Settings.Default.DarkMode;

            if (quickSaveTimestamp.HasValue)
            {
                var timeStr = Properties.Settings.Default.Use24HourTime
                    ? quickSaveTimestamp.Value.ToString("M/d/yyyy HH:mm:ss")
                    : quickSaveTimestamp.Value.ToString("M/d/yyyy h:mm:ss tt");
                lblQuickSave.Text = $"[F5] ⚡ Quicksave: {timeStr}";
                lblQuickSave.ForeColor = isDark ? Color.FromArgb(100, 200, 100) : Color.FromArgb(16, 137, 62);
            }
            else
            {
                lblQuickSave.Text = "[F5] Quicksave: None";
                lblQuickSave.ForeColor = isDark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(96, 96, 96);
            }
        }

        private void LoadQuickSaveInfo()
        {
            // Get selected character
            if (selectedCharacter == null)
            {
                quickSaveTimestamp = null;
                UpdateQuickSaveDisplay();
                return;
            }

            // Look for existing quicksave for this character
            var quicksaveFolderName = $"{selectedCharacter.FolderName}_quicksave";
            var quicksavePath = Path.Combine(backupRootPath, quicksaveFolderName);

            if (Directory.Exists(quicksavePath))
            {
                try
                {
                    // Get the folder's last write time as the quicksave timestamp
                    quickSaveTimestamp = Directory.GetLastWriteTime(quicksavePath);
                }
                catch
                {
                    quickSaveTimestamp = null;
                }
            }
            else
            {
                quickSaveTimestamp = null;
            }

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
            if (cboBackups.SelectedIndex == -1)
            {
                MessageBox.Show("Please select a backup to delete.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedText = cboBackups.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(selectedText))
            {
                UpdateBackupStatus("Error: Invalid selection.", Color.Red);
                return;
            }

            // Find the matching backup (will match the most recent one if duplicates exist)
            BackupInfo? selectedBackup = null;
            foreach (var backup in backups.Values.OrderByDescending(b => b.CreatedDate))
            {
                var characterName = !string.IsNullOrEmpty(backup.CharacterName) 
                    ? backup.CharacterName 
                    : "[Unnamed Character]";
                var saveName = !string.IsNullOrEmpty(backup.SaveName) 
                    ? backup.SaveName 
                    : "[Unnamed Save]";
                var backupDisplay = $"{characterName} - {saveName}";
                if (backupDisplay == selectedText)
                {
                    selectedBackup = backup;
                    break;
                }
            }

            if (selectedBackup == null)
            {
                UpdateBackupStatus("Error: Backup not found.", Color.Red);
                return;
            }

            var profileDisplayName = !string.IsNullOrEmpty(selectedBackup.CharacterName) 
                ? selectedBackup.CharacterName 
                : "[Unnamed Character]";
            var saveDisplayName = !string.IsNullOrEmpty(selectedBackup.SaveName) 
                ? selectedBackup.SaveName 
                : "[Unnamed Save]";

            var result = MessageBox.Show(
                $"Are you sure you want to DELETE this backup?\n\nCharacter: {profileDisplayName}\nSave: {saveDisplayName}\nCreated: {FormatDateTime(selectedBackup.CreatedDate)}\n\nThis action cannot be undone!",
                "Confirm Delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning
            );

            if (result != DialogResult.Yes)
            {
                return;
            }

            try
            {
                // Delete the backup folder
                if (Directory.Exists(selectedBackup.BackupFolderPath))
                {
                    Directory.Delete(selectedBackup.BackupFolderPath, true);
                }

                // Remove from dictionary
                var backupKey = Path.GetFileName(selectedBackup.BackupFolderPath);
                if (!string.IsNullOrEmpty(backupKey))
                {
                    backups.Remove(backupKey);
                }

                // Update backup names file
                SaveBackupNames();

                UpdateBackupStatus($"Backup '{saveDisplayName}' for '{profileDisplayName}' deleted successfully!", Color.Green);
                LoadBackups();

                MessageBox.Show($"Backup deleted successfully!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                var selectedText = cboBackups.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(selectedText))
                    return;

                // Get selected character
                if (selectedCharacter == null)
                    return;

                // Find the matching backup (format is now "SaveName - Time")
                foreach (var backup in backups.Values)
                {
                    // Only check backups for this character
                    if (!backup.CharacterName.Equals(selectedCharacter.CharacterName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var saveName = !string.IsNullOrEmpty(backup.SaveName) 
                        ? backup.SaveName 
                        : "[Unnamed Save]";
                    
                    var backupTimeStr = Properties.Settings.Default.Use24HourTime
                        ? backup.CreatedDate.ToString("M/d/yyyy HH:mm")
                        : backup.CreatedDate.ToString("M/d/yyyy h:mm tt");
                    
                    var backupDisplay = $"{saveName} - {backupTimeStr}";
                    
                    if (backupDisplay == selectedText)
                    {
                        if (Directory.Exists(backup.BackupFolderPath))
                        {
                            var size = GetDirectorySize(backup.BackupFolderPath);
                            var sizeInMB = size / (1024.0 * 1024.0);
                            UpdateBackupStatus($"Backup: {FormatDateTime(backup.CreatedDate)} | Size: {sizeInMB:F2} MB", Color.FromArgb(0, 120, 212));
                        }
                        break;
                    }
                }
            }
            catch { }
        }

        private void SyncProfileToBackup(string characterName)
        {
            // No longer needed since character is already selected
            // Keeping method for compatibility but it does nothing
            return;
        }


private void UpdatePlaythroughStatus(string message, Color color)
{
    ApplyStatus(lblPlaythroughStatus, message, color);
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

        private void SaveBackupNames()
        {
            try
            {
                var configPath = Path.Combine(backupRootPath, "backup_names.txt");
                using (var writer = new StreamWriter(configPath, false))
                {
                    foreach (var backup in backups.Values)
                    {
                        if (!string.IsNullOrEmpty(backup.SaveName))
                        {
                            var backupFolderName = Path.GetFileName(backup.BackupFolderPath);
                            writer.WriteLine($"{backupFolderName}|{backup.CharacterName}|{backup.SaveName}");
                        }
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

        private void LoadBackupNames()
        {
            try
            {
                var configPath = Path.Combine(backupRootPath, "backup_names.txt");
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 3)
                        {
                            var backup = backups.Values.FirstOrDefault(b => 
                                Path.GetFileName(b.BackupFolderPath) == parts[0]);
                            if (backup != null)
                            {
                                backup.CharacterName = parts[1];
                                backup.SaveName = parts[2];
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private string GetCharacterNameForFolder(string folderName)
        {
            if (playthroughs.ContainsKey(folderName))
            {
                return playthroughs[folderName].CharacterName;
            }
            return string.Empty;
        }

        private void SaveRestoreTracking()
        {
            try
            {
                var configPath = Path.Combine(backupRootPath, "restore_tracking.txt");
                using (var writer = new StreamWriter(configPath, false))
                {
                    foreach (var pt in playthroughs.Values)
                    {
                        if (!string.IsNullOrEmpty(pt.LastRestoredSave))
                        {
                            writer.WriteLine($"{pt.FolderName}|{pt.LastRestoredSave}|{pt.LastRestoreTime:yyyy-MM-dd HH:mm:ss}");
                        }
                    }
                }
            }
            catch { }
        }

        private void LoadRestoreTracking()
        {
            try
            {
                var configPath = Path.Combine(backupRootPath, "restore_tracking.txt");
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 3 && playthroughs.ContainsKey(parts[0]))
                        {
                            playthroughs[parts[0]].LastRestoredSave = parts[1];
                            if (DateTime.TryParse(parts[2], out DateTime restoreTime))
                            {
                                playthroughs[parts[0]].LastRestoreTime = restoreTime;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void ApplyTheme()
        {
            bool isDark = Properties.Settings.Default.DarkMode;
            
            // Main form
            this.BackColor = isDark ? darkBackground : lightBackground;
            
            // Group boxes
            grpBackup.ForeColor = isDark ? darkForeground : lightForeground;
            grpRestore.ForeColor = isDark ? darkForeground : lightForeground;
            
            // Labels
            lblPlaythrough.ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64);
            lblBackup.ForeColor = isDark ? darkForeground : Color.FromArgb(64, 64, 64);
            lblPlaythroughStatus.ForeColor = isDark ? Color.FromArgb(180, 180, 180) : Color.FromArgb(96, 96, 96);
            lblBackupStatus.ForeColor = isDark ? Color.FromArgb(180, 180, 180) : Color.FromArgb(96, 96, 96);
            
            // Dropdowns
            lblPlaythroughValue.BackColor = isDark ? darkControlBg : lightControlBg;
            lblPlaythroughValue.ForeColor = isDark ? darkForeground : lightForeground;
            cboBackups.BackColor = isDark ? darkControlBg : lightControlBg;
            cboBackups.ForeColor = isDark ? darkForeground : lightForeground;
            
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

            // Look for backup with matching character and timestamp
            foreach (var backup in backups.Values.Where(b => 
                b.CharacterName.Equals(playthrough.CharacterName, StringComparison.OrdinalIgnoreCase)))
            {
                // Get the backup's save folder
                var backupSaveFolder = Directory.GetDirectories(backup.BackupFolderPath)
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
        public string LastRestoredSave { get; set; } = string.Empty;
        public DateTime LastRestoreTime { get; set; }
    }

    public class BackupInfo
    {
        public string BackupFolderPath { get; set; } = string.Empty;
        public string PlaythroughFolderName { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string SaveName { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
    }
}
