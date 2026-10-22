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
        private Button _chooseButton;
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
            // 尺寸策略：宽度固定合理值；高度在 Shown（完成布局）后按 root 实际渲染高度收紧。
            // 不用 Form AutoSize：它在 TLP(AutoSize 列/Dock) 嵌套下首选尺寸测量不可靠，
            // 实测先后出现高度塌陷（Dock.Fill+百分比列）与宽度塌陷（AutoSize 列）
            ClientSize = new Size(540, 400);
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
            MainMenuStrip = menu;   // Controls.Add(menu) 移至 root 之后：后加者先布局、贴顶

            /*
             * 相对定位布局（全 WinForms 内建，无一处像素坐标）：
             *   TableLayoutPanel  ≈ CSS Grid   （行/列 + AutoSize/百分比尺寸）
             *   Dock / Anchor     ≈ 对齐与伸展  （随窗口/单元格伸缩）
             *   Margin / Padding  ≈ 外/内边距   （DPI 变化自动缩放）
             * 行高全部 AutoSize（内容撑开），DPI、字体缩放、窗口拉伸都自适应；
             * 以后增删控件只需要在流里加一行，不用重算任何坐标。
             */
            var root = new TableLayoutPanel
            {
                // Dock=Top + AutoSize=true：行高按内容撑出，root.Height 即真实内容高度
                //（在 Shown 时读取它来收紧窗体高度；宽度不受 AutoSize 测量影响）
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(22, 8, 22, 12),
            };
            // 列宽由最宽子控件决定；居中控件用 Anchor=None 在列内居中
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(root);
            Controls.Add(menu);           // 后加 → 先布局 → 菜单贴顶，root 从菜单下沿开始

            int rowIndex = 0;
            void AddRow(Control control)
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(control, 0, rowIndex++);
            }

            // ── 标题区（Anchor=None → 在 100% 宽单元格里水平居中）──
            _titleLabel = new Label
            {
                Text = "Alice In Cradle",
                Font = bigFont,
                ForeColor = Color.Black,
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 10, 0, 0),
            };
            AddRow(_titleLabel);

            var subtitle = new Label
            {
                Text = "全版本通用补丁（C# 重写版）",
                ForeColor = Color.DimGray,
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0),
            };
            AddRow(subtitle);

            // ── ① 第一步：选游戏目录 ──
            _chooseButton = new Button
            {
                Text = "① 选择游戏目录（或把游戏 exe 拖进来）",
                AutoSize = true,
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 12, 0, 6),
            };
            _chooseButton.Click += (s, e) => ChooseGameFolder();
            AddRow(_chooseButton);

            // ── ② 信息区：嵌套 Grid（标签列固定 96px + 数值列 100%）──
            _versionLabel = InfoValue("未知", baseFont);
            _pathLabel = InfoValue("", smallFont);
            _pathLabel.ForeColor = Color.DimGray;
            _stateLabel = InfoValue("未选择游戏目录", baseFont);

            var infoGrid = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                Margin = new Padding(0),
            };
            // 全 Absolute 列：百分比列在无约束测宽下退化；数值列固定宽 + Dock.Fill + AutoEllipsis 效果不变
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
            infoGrid.Controls.Add(InfoKey("游戏版本：", baseFont), 0, 0);
            infoGrid.Controls.Add(_versionLabel, 1, 0);
            infoGrid.Controls.Add(InfoKey("游戏路径：", baseFont), 0, 1);
            infoGrid.Controls.Add(_pathLabel, 1, 1);
            infoGrid.Controls.Add(InfoKey("补丁状态：", baseFont), 0, 2);
            infoGrid.Controls.Add(_stateLabel, 1, 2);
            AddRow(infoGrid);

            // ── ③ 操作按钮：嵌套 Grid 两列各 50%，按钮 Dock 撑满单元格 ──
            _installButton = new Button
            {
                Text = "② 安装去码补丁",
                Dock = DockStyle.Fill,
                Height = 42,
                Margin = new Padding(0, 8, 8, 0),
                Enabled = false,
            };
            _uninstallButton = new Button
            {
                Text = "③ 卸载去码补丁",
                Dock = DockStyle.Fill,
                Height = 42,
                Margin = new Padding(8, 8, 0, 0),
                Enabled = false,
            };
            var buttonGrid = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                Margin = new Padding(0),
            };
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            buttonGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            buttonGrid.Controls.Add(_installButton, 0, 0);
            buttonGrid.Controls.Add(_uninstallButton, 1, 0);
            AddRow(buttonGrid);

            // ── ④ 动态状态（安装中/完成/失败），紧贴操作区，随窗宽伸缩 ──
            // AutoSize=false 才能让 AutoEllipsis 生效；Dock=Fill 占满 AutoSize 列宽
            _statusLabel = new Label
            {
                Text = "",
                Font = smallFont,
                ForeColor = Color.DimGray,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 20,
                AutoEllipsis = true,
                Margin = new Padding(0, 10, 0, 0),
            };
            AddRow(_statusLabel);

            // ── ⑤ 注意事项（流末尾）──
            var tip = new Label
            {
                Text = "安装/卸载均需约 5~10 秒，请勿重复操作\n" +
                    "安装时自动备份原始 DLL（Assembly-CSharp.dll.bak）",
                Font = smallFont,
                ForeColor = Color.Firebrick,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0),
            };
            AddRow(tip);

            // 高度自适应：Shown 时内容已完成布局，用 root 实际渲染高度收紧客户区
            //（杜绝对高度导致的底部空白，也比 Form AutoSize 可靠——宽度不参与自测）
            Shown += (s, e) =>
            {
                ClientSize = new Size(540, menu.Height + root.Height);
                CenterToScreen();
            };
        }

        private static Label InfoKey(string text, Font font)
        {
            return new Label
            {
                Text = text,
                Font = font,
                AutoSize = true,
                Margin = new Padding(0, 7, 0, 2),
            };
        }

        private static Label InfoValue(string text, Font font)
        {
            return new Label
            {
                Text = text,
                Font = font,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(4, 7, 0, 2),
            };
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
            _versionLabel.Text = string.IsNullOrEmpty(GameVersion.FromFolder(dir))
                ? "未知"
                : GameVersion.FromFolder(dir);
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

        /// <summary>补丁执行结果（工作函数纯返回值，UI 层据此弹窗/更新状态栏）。</summary>
        private enum PatchOutcome
        {
            Succeeded,           // dnlib 修改 + 自校验通过 + 已原子替换
            RestoredFromBackup,  // 从 .bak 直接还原
            AlreadyApplied,      // 已是目标状态
            NotFound,            // 未找到补丁位置
            VerifyOrIoFailed,    // 内部校验/IO 失败
            BlockedInUse,        // 游戏文件被占用
            NoGameRoot,          // 未选择游戏目录
        }

        private void InstallPatch()
        {
            RunPatchingAsync(restoreBackup: false, install: true, verb: "安装");
        }

        private void UninstallPatch()
        {
            RunPatchingAsync(restoreBackup: true, install: false, verb: "卸载");
        }

        /// <summary>
        /// 补丁执行（async）：dnlib 加载/改 IL/写盘的 5~10 秒放在线程池上执行，
        /// UI 线程保持响应（窗口可拖动/重绘；上游"窗口卡顿属正常现象"的根源
        /// 就是这段同步跑在 UI 线程上）。UI 副作用（弹窗/状态栏）全部留在
        /// await 之后的主线程，工作函数 PerformPatchWork 纯计算/IO 无弹窗。
        /// </summary>
        private async void RunPatchingAsync(bool restoreBackup, bool install, string verb)
        {
            SetButtonsEnabled(false);
            _statusLabel.Text = "正在" + verb + "…（约 5~10 秒，窗口可自由拖动）";
            Cursor = Cursors.WaitCursor;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                PatchOutcome outcome = await Task.Run(() => PerformPatchWork(restoreBackup, install));
                stopwatch.Stop();
                string elapsed = verb + "完成，耗时 " + stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " 秒";

                switch (outcome)
                {
                    case PatchOutcome.Succeeded:
                        MessageBox.Show(this, install ? "安装补丁成功！" : "卸载补丁成功！",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        _statusLabel.Text = elapsed;
                        break;

                    case PatchOutcome.RestoredFromBackup:
                        MessageBox.Show(this, "已从备份还原原始游戏文件！",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        _statusLabel.Text = elapsed;
                        break;

                    case PatchOutcome.AlreadyApplied:
                        MessageBox.Show(this, install ? "补丁已安装，请勿重复安装！" : "未找到已安装的补丁！",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _statusLabel.Text = verb + "中止（已是目标状态）";
                        break;

                    case PatchOutcome.NotFound:
                        MessageBox.Show(this, "游戏文件异常！找不到补丁安装位置。\n" +
                            "这可能不是对应的游戏本体，或版本结构已变化。",
                            "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _statusLabel.Text = verb + "失败（未找到补丁位置）";
                        break;

                    case PatchOutcome.VerifyOrIoFailed:
                        MessageBox.Show(this, "补丁生成失败\n错误信息：内部校验未通过，请重新安装。",
                            "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        _statusLabel.Text = verb + "失败（校验未通过）";
                        break;

                    case PatchOutcome.BlockedInUse:
                        MessageBox.Show(this, "游戏文件被占用，请先关闭游戏。",
                            "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _statusLabel.Text = verb + "中止（游戏文件被占用）";
                        break;

                    case PatchOutcome.NoGameRoot:
                        MessageBox.Show(this, "请先选择游戏目录。",
                            "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        _statusLabel.Text = "";
                        break;
                }
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
                _statusLabel.Text = verb + "失败（无写入权限）";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, verb + "补丁失败\n错误信息：" + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = verb + "失败";
            }
            finally
            {
                Cursor = Cursors.Default;
                SetButtonsEnabled(true);   // 先全部恢复，随后 RefreshState 按实际状态校正
                RefreshState();
            }
        }

        /// <summary>
        /// 补丁工作（纯 IO/dnlib，无 UI 副作用；在线程池上执行）。
        /// 注意 install 语义：true=「安装去码」→ 马赛克禁用（mosaicEnabled=false）；卸载反之。
        /// </summary>
        private PatchOutcome PerformPatchWork(bool restoreBackup, bool install)
        {
            if (!GameFiles.IsGameRoot(_gameRoot)) return PatchOutcome.NoGameRoot;

            string dll = GameFiles.AssemblyPath(_gameRoot);
            string backup = GameFiles.BackupPath(_gameRoot);
            string temp = dll + ".tmp_aic";
            TryDeleteFile(temp);

            if (GameFiles.IsFileInUse(dll)) return PatchOutcome.BlockedInUse;

            // ---- 优先走备份还原路径 ----
            if (restoreBackup && File.Exists(backup))
            {
                File.Copy(backup, temp, true);
                ReplaceAtomically(temp, dll);      // temp 被消费
                TryDeleteFile(backup);
                return PatchOutcome.RestoredFromBackup;
            }

            // ---- dnlib 进程内改 IL，输出到同目录临时文件 ----
            var status = MosaicPatcher.Apply(dll, !install, temp);
            switch (status)
            {
                case MosaicPatcher.PatchStatus.NotFound:
                    TryDeleteFile(temp);
                    return PatchOutcome.NotFound;
                case MosaicPatcher.PatchStatus.AlreadyApplied:
                    TryDeleteFile(temp);
                    return PatchOutcome.AlreadyApplied;
                case MosaicPatcher.PatchStatus.IoError:
                case MosaicPatcher.PatchStatus.VerifyFailed:
                    TryDeleteFile(temp);
                    return PatchOutcome.VerifyOrIoFailed;
            }

            // Success：先备份原始 DLL（幂等，只备份一次），再原子替换。
            // 只在「确实是原版状态」时备份，避免把已打补丁的文件误存为 .bak。
            if (!restoreBackup && !File.Exists(backup)
                && MosaicPatcher.Inspect(dll) == MosaicPatcher.InspectState.MosaicOn)
            {
                File.Copy(dll, backup, false);
            }

            ReplaceAtomically(temp, dll);
            return PatchOutcome.Succeeded;
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
            _chooseButton.Enabled = enabled;   // 避免执行中切换游戏目录
        }

        /// <summary>
        /// 按补丁实际状态启用/禁用对应按钮：已补丁→只能卸载，原版→只能安装，
        /// 未知→两个都允许尝试。修复：已安装时安装按钮仍然可点（点了必弹"已安装"）。
        /// </summary>
        private void ApplyButtonStates(MosaicPatcher.InspectState state)
        {
            switch (state)
            {
                case MosaicPatcher.InspectState.MosaicOff:    // 已补丁
                    _installButton.Enabled = false;
                    _uninstallButton.Enabled = true;
                    break;
                case MosaicPatcher.InspectState.MosaicOn:     // 原版
                    _installButton.Enabled = true;
                    _uninstallButton.Enabled = false;
                    break;
                default:                                      // 未知 → 不预判
                    _installButton.Enabled = true;
                    _uninstallButton.Enabled = true;
                    break;
            }
        }

        // ----------------------------- 状态/杂项 -----------------------------

        /// <summary>刷新补丁状态与按钮可用性（dnlib 检测在线程池上，await 后回 UI 线程）。</summary>
        private async void RefreshState()
        {
            if (!GameFiles.IsGameRoot(_gameRoot))
            {
                _stateLabel.Text = "未找到游戏";
                return;
            }
            _stateLabel.Text = "检测中…";
            string dll = GameFiles.AssemblyPath(_gameRoot);

            MosaicPatcher.InspectState state;
            try
            {
                state = await Task.Run(() => MosaicPatcher.Inspect(dll));
            }
            catch
            {
                state = MosaicPatcher.InspectState.Error;
            }

            string text;
            switch (state)
            {
                case MosaicPatcher.InspectState.MosaicOff: text = "已安装补丁（去码生效）"; break;
                case MosaicPatcher.InspectState.MosaicOn: text = "未安装（原始状态）"; break;
                case MosaicPatcher.InspectState.NotFound: text = "未知（未找到补丁位置）"; break;
                default: text = "未知（读取失败）"; break;
            }
            _stateLabel.Text = text;
            ApplyButtonStates(state);   // 状态和按钮可用性同步
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
