using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AicNoMosaic
{
    /// <summary>
    /// 主窗口：选择游戏目录 → 安装/卸载补丁 → 调试设置。
    /// 与上游 C++ 版功能对齐（自动识别/exe 选择/调试编辑），并新增：
    /// 拖拽支持、记住上次目录、原子写回、自动备份、补丁状态显示。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const string AppVersion = "v2.0.0";
        private const string ReleasesUrl = "https://github.com/LhyCloud/AliceInCradle_nomosaic_patch/releases";
        private const string ReadmeUrl = "https://github.com/LhyCloud/AliceInCradle_nomosaic_patch/blob/main/README.md";

        private Label _titleLabel;
        private Label _versionLabel;
        private Label _pathLabel;
        private Label _stateLabel;
        private Label _statusLabel;
        private Button _installButton;
        private Button _uninstallButton;
        private string _gameRoot = "";

        public MainForm()
        {
            InitUi();
            InitWiring();
            LoadOrDetectGame();
        }

        // ================================ UI ================================

        private void InitUi()
        {
            Font baseFont = new Font("Microsoft YaHei", 10F);
            Font bigFont = new Font("Microsoft YaHei", 17F, FontStyle.Bold);
            Font smallFont = new Font("Microsoft YaHei", 8.5F);

            Text = "Alice In Cradle 补丁工具 " + AppVersion;
            ClientSize = new Size(520, 420);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = baseFont;
            AllowDrop = true;

            var menu = new MenuStrip();
            var fileMenu = new ToolStripMenuItem("文件(&F)");
            fileMenu.DropDownItems.Add(new ToolStripMenuItem("退出(&X)", null, (s, e) => Close()));
            var toolsMenu = new ToolStripMenuItem("工具(&T)");
            toolsMenu.DropDownItems.Add(new ToolStripMenuItem("游戏调试设置(&D)", null, (s, e) => ShowDebugSettings()));
            var helpMenu = new ToolStripMenuItem("帮助(&H)");
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("教程文档（工具说明）(&A)", null, (s, e) => OpenUrl(ReadmeUrl)));
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("更新本工具(&U)", null, (s, e) => OpenUrl(ReleasesUrl)));
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("版本信息(&V)", null, (s, e) => ShowAbout()));
            menu.Items.Add(fileMenu);
            menu.Items.Add(toolsMenu);
            menu.Items.Add(helpMenu);
            MainMenuStrip = menu;
            Controls.Add(menu);

            _titleLabel = MakeLabel("Alice In Cradle", bigFont, new Point(170, 34), new Size(300, 40));
            _titleLabel.ForeColor = Color.Black;

            var subtitle = MakeLabel("全版本通用补丁（C# 重写版）", baseFont, new Point(178, 78), new Size(300, 24));
            subtitle.ForeColor = Color.DimGray;

            MakeLabel("游戏版本：", baseFont, new Point(60, 116), new Size(92, 20));
            _versionLabel = MakeLabel("未知", baseFont, new Point(160, 116), new Size(330, 20));
            MakeLabel("游戏路径：", baseFont, new Point(60, 142), new Size(92, 20));
            _pathLabel = MakeLabel("", smallFont, new Point(160, 141), new Size(340, 44));
            _pathLabel.ForeColor = Color.DimGray;
            MakeLabel("补丁状态：", baseFont, new Point(60, 190), new Size(92, 20));
            _stateLabel = MakeLabel("未选择游戏目录", baseFont, new Point(160, 190), new Size(330, 20));

            _installButton = MakeButton("安装去码补丁", new Point(57, 240), new Size(180, 42));
            _installButton.Enabled = false;
            _uninstallButton = MakeButton("卸载去码补丁", new Point(283, 240), new Size(180, 42));
            _uninstallButton.Enabled = false;

            var chooseButton = MakeButton("选择游戏目录（或把游戏 exe 拖进来）", new Point(130, 292), new Size(260, 34));

            _statusLabel = MakeLabel("", smallFont, new Point(60, 338), new Size(410, 20));
            _statusLabel.ForeColor = Color.DimGray;

            var tip = MakeLabel(
                "安装/卸载均需约 5~10 秒，请勿重复操作\n" +
                "安装时自动备份原始 DLL（Assembly-CSharp.dll.bak）",
                smallFont, new Point(60, 366), new Size(420, 44));
            tip.ForeColor = Color.Firebrick;

            chooseButton.Click += (s, e) => ChooseGameFolder();
        }

        private Label MakeLabel(string text, Font font, Point location, Size size)
        {
            var lb = new Label
            {
                Text = text,
                Font = font,
                Location = location,
                Size = size,
                AutoEllipsis = true,
            };
            Controls.Add(lb);
            return lb;
        }

        private Button MakeButton(string text, Point location, Size size)
        {
            var btn = new Button
            {
                Text = text,
                Location = location,
                Size = size,
                Font = Font,
            };
            Controls.Add(btn);
            return btn;
        }

        private void InitWiring()
        {
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            _installButton.Click += (s, e) => InstallPatch();
            _uninstallButton.Click += (s, e) => UninstallPatch();
        }

        // ============================== 逻辑 ================================

        private void LoadOrDetectGame()
        {
            // 1) 记住的上次目录
            AppSettings.Load();
            if (TrySelectGameRoot(AppSettings.GamePath)) return;

            // 2) 工具所在目录即游戏目录
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                if (TrySelectGameRoot(exeDir)) return;
            }
            catch { }
        }

        private bool TrySelectGameRoot(string dir)
        {
            if (!GameFiles.IsGameRoot(dir)) return false;
            SelectGameRoot(Path.GetFullPath(dir));
            return true;
        }

        private void SelectGameRoot(string dir)
        {
            _gameRoot = dir;
            AppSettings.GamePath = dir;
            AppSettings.Save();

            _pathLabel.Text = dir;
            _versionLabel.Text = GameVersion.FromFolderWithFallback(dir);
            _installButton.Enabled = true;
            _uninstallButton.Enabled = true;
            _statusLabel.Text = "";

            if (GameFiles.IsFileInUse(GameFiles.AssemblyPath(_gameRoot)))
                MessageBox.Show("游戏文件被占用，请先关闭游戏。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            RefreshState();
        }

        private void ChooseGameFolder()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "请选择游戏主程序",
                Filter = "可执行文件 (*.exe)|*.exe",
                FileName = "AliceInCradle.exe",
                CheckFileExists = true,
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string root;
                try { root = Path.GetDirectoryName(dialog.FileName); }
                catch { return; }
                if (!GameFiles.IsGameRoot(root))
                {
                    MessageBox.Show("未找到游戏文件，请确认选择的是游戏主程序。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                SelectGameRoot(root);
            }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string first = files[0];
            try
            {
                string root = Directory.Exists(first)
                    ? first
                    : Path.GetDirectoryName(first);
                if (!GameFiles.IsGameRoot(root))
                {
                    MessageBox.Show("未找到游戏文件，请拖入游戏主程序或游戏目录。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                SelectGameRoot(Path.GetFullPath(root));
            }
            catch { }
        }

        // ----------------------------- 安装/卸载 -----------------------------

        private void InstallPatch()
        {
            RunPatching(() => InstallOrUninstallCore(restoreBackup: false, install: true), "安装");
        }

        private void UninstallPatch()
        {
            RunPatching(() => InstallOrUninstallCore(restoreBackup: true, install: false), "卸载");
        }

        private void RunPatching(Action core, string verb)
        {
            SetButtonsEnabled(false);
            _statusLabel.Text = "正在" + verb + "…";
            Cursor = Cursors.WaitCursor;
            try
            {
                core();
            }
            catch (UnauthorizedAccessException)
            {
                if (MessageBox.Show(
                        "写入游戏目录被拒绝（通常游戏在 Program Files 等受保护目录）。\n" +
                        "是否以管理员身份重新运行本工具？",
                        "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    if (Elevation.RestartAsAdmin()) Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(verb + "补丁失败\n错误信息：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = verb + "失败";
            }
            finally
            {
                Cursor = Cursors.Default;
                SetButtonsEnabled(true);
            }
        }

        private void InstallOrUninstallCore(bool restoreBackup, bool install)
        {
            if (!GameFiles.IsGameRoot(_gameRoot))
            {
                MessageBox.Show("请先选择游戏目录。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string dll = GameFiles.AssemblyPath(_gameRoot);
            string backup = GameFiles.BackupPath(_gameRoot);
            string temp = dll + ".tmp_aic";
            TryDeleteFile(temp);

            if (GameFiles.IsFileInUse(dll))
            {
                MessageBox.Show("游戏文件被占用，请先关闭游戏。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // ---- 优先走备份还原路径 ----
            if (restoreBackup && File.Exists(backup))
            {
                File.Copy(backup, temp, true);
                ReplaceAtomically(temp, dll);      // temp 被消费
                TryDeleteFile(backup);
                MessageBox.Show("已从备份还原原始游戏文件！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshState();
                return;
            }

            // ---- dnlib 进程内改 IL，输出到同目录临时文件 ----
            var status = MosaicPatcher.Apply(dll, install, temp);

            switch (status)
            {
                case MosaicPatcher.PatchStatus.NotFound:
                    TryDeleteFile(temp);
                    MessageBox.Show("游戏文件异常！找不到补丁安装位置。\n" +
                        "这可能不是对应的游戏本体，或版本结构已变化。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;

                case MosaicPatcher.PatchStatus.AlreadyApplied:
                    TryDeleteFile(temp);
                    MessageBox.Show(install ? "补丁已安装，请勿重复安装！" : "未找到已安装的补丁！",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;

                case MosaicPatcher.PatchStatus.IoError:
                case MosaicPatcher.PatchStatus.VerifyFailed:
                    TryDeleteFile(temp);
                    MessageBox.Show("补丁生成失败\n错误信息：内部校验未通过，请重新安装。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
            }

            // Success：先备份原始 DLL（幂等，只备份一次），再原子替换
            if (!restoreBackup && !File.Exists(backup))
                File.Copy(dll, backup, false);

            ReplaceAtomically(temp, dll);

            MessageBox.Show(install ? "安装补丁成功！" : "卸载补丁成功！",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshState();
        }

        /// <summary>关键优化 #5：用 File.Replace 做同卷近原子替换，中途断电也不会损坏游戏文件。</summary>
        private static void ReplaceAtomically(string tempFile, string targetFile)
        {
            if (File.Exists(targetFile))
                File.Replace(tempFile, targetFile, null, true);
            else
                File.Move(tempFile, targetFile);
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            _installButton.Enabled = enabled;
            _uninstallButton.Enabled = enabled;
        }

        // ----------------------------- 状态/杂项 -----------------------------

        private void RefreshState()
        {
            if (!GameFiles.IsGameRoot(_gameRoot))
            {
                _stateLabel.Text = "未找到游戏";
                return;
            }
            _stateLabel.Text = "检测中…";
            string dll = GameFiles.AssemblyPath(_gameRoot);
            Task<MosaicPatcher.InspectState> task = Task.Run(() =>
            {
                try { return MosaicPatcher.Inspect(dll); }
                catch { return MosaicPatcher.InspectState.Error; }
            });
            task.ContinueWith(t =>
            {
                string text;
                switch (t.Result)
                {
                    case MosaicPatcher.InspectState.MosaicOff: text = "已安装补丁（去码生效）"; break;
                    case MosaicPatcher.InspectState.MosaicOn: text = "未安装（原始状态）"; break;
                    case MosaicPatcher.InspectState.NotFound: text = "未知（未找到补丁位置）"; break;
                    default: text = "未知（读取失败）"; break;
                }
                _stateLabel.Text = text;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowDebugSettings()
        {
            if (string.IsNullOrEmpty(_gameRoot) || !GameFiles.IsGameRoot(_gameRoot))
            {
                MessageBox.Show("请先选择游戏目录。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using (var form = new DebugSettingsForm(GameFiles.DebugConfigPath(_gameRoot)))
            {
                form.ShowDialog(this);
            }
        }

        private static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(url); } catch { }
        }

        private void ShowAbout()
        {
            MessageBox.Show(
                "补丁工具版本：" + AppVersion + "（C# WinForms 单文件重写）\n" +
                "上游项目：LhyCloud/AliceInCradle_nomosaic_patch\n" +
                "补丁方式：dnlib 进程内修改 MosaicShower.FnDrawMosaic 返回值\n" +
                "兼容游戏版本：0.21 及之后的版本（自动检测）",
                "版本信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
