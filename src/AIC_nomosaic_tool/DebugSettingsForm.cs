using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AicNoMosaic
{
    /// <summary>
    /// 游戏调试设置窗口：编辑 &lt;游戏目录&gt;\AliceInCradle_Data\StreamingAssets\_debug.txt。
    /// 文件里缺失的键对应复选框禁用（不回写）；其余行逐字节保留。
    /// </summary>
    internal sealed class DebugSettingsForm : Form
    {
        private readonly string _configPath;
        private readonly CheckBox[] _boxes = new CheckBox[DebugConfig.KeyCount];

        public DebugSettingsForm(string configPath)
        {
            _configPath = configPath;

            Text = "游戏调试设置";
            // 尺寸策略同主窗：宽度固定、高度在 Shown 后按 root 实际渲染高度收紧
            ClientSize = new Size(446, 360);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei", 9F);

            // 与主窗口同一套相对布局：TableLayoutPanel（≈CSS Grid），行高 AutoSize。
            // Dock=Top + AutoSize 列而非 Dock.Fill：否则无约束测宽退化、窗体堑陷
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 1,
                Padding = new Padding(26, 14, 26, 14),
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(root);

            int rowIndex = 0;
            void AddRow(Control control)
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(control, 0, rowIndex++);
            }

            AddRow(new Label
            {
                Text = "_debug.txt（游戏自带调试开关）",
                Font = new Font("Microsoft YaHei", 9.5F, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0),
            });

            // 分两组渲染：作弊开关 / 进阶（排障/功能）开关；每行复选框自带文字
            //（框与文字同一控件渲染，结构上不可能错位）
            for (int i = 0; i < DebugConfig.KeyCount; i++)
            {
                if (i == DebugConfig.CheatGroupCount)
                {
                    AddRow(new Label
                    {
                        Text = "── 进阶（排障 / 功能开关）──",
                        ForeColor = Color.DimGray,
                        Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                        AutoSize = true,
                        Margin = new Padding(0, 10, 0, 0),
                    });
                }
                else if (i == 1)
                {
                    AddRow(new Label
                    {
                        Text = "── 作弊开关 ──",
                        ForeColor = Color.DimGray,
                        Font = new Font("Microsoft YaHei", 8.5F, FontStyle.Bold),
                        AutoSize = true,
                        Margin = new Padding(0, 2, 0, 0),
                    });
                }

                var box = new CheckBox
                {
                    Text = DebugConfig.KeyNames[i] + "    " + DebugConfig.KeyTexts[i],
                    AutoSize = true,
                    Margin = new Padding(0, 6, 0, 0),
                    Tag = i,
                };
                AddRow(box);
                _boxes[i] = box;
            }

            // 保存/放弃：嵌套 Grid 两列各 50%，按钮 Dock 撑满
            var saveButton = new Button
            {
                Text = "保存设置",
                Dock = DockStyle.Fill,
                Height = 32,
                Margin = new Padding(0, 18, 8, 0),
            };
            saveButton.Click += (s, e) => Save();
            var cancelButton = new Button
            {
                Text = "放弃更改",
                Dock = DockStyle.Fill,
                Height = 32,
                Margin = new Padding(8, 18, 0, 0),
            };
            cancelButton.Click += (s, e) => Close();

            var buttonGrid = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
                Anchor = AnchorStyles.None,       // 与主窗同一套参考系，中轴对齐
            };
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            buttonGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            buttonGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            buttonGrid.Controls.Add(saveButton, 0, 0);
            buttonGrid.Controls.Add(cancelButton, 1, 0);
            AddRow(buttonGrid);

            AddRow(new Label
            {
                Text = "只修改数值本身，注释与其它行原样保留；\n缺失的键会被禁用且不会写入文件。",
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei", 8.5F),
                AutoSize = true,
                Margin = new Padding(0, 10, 0, 0),
            });

            // 高度自适应：Shown 时按 root 实际渲染高度收紧客户区
            Shown += (s, e) =>
            {
                ClientSize = new Size(446, root.Height);
                CenterToParent();
            };

            Load += (s, e) => LoadConfig();
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK) return;
                if (MessageBox.Show("你还未保存设置，确定放弃更改？", "提示",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    e.Cancel = true;
            };
        }

        private void LoadConfig()
        {
            var entries = new DebugConfig.Entry[DebugConfig.KeyCount];
            if (!DebugConfig.TryRead(_configPath, entries))
            {
                MessageBox.Show("无法读取 _debug.txt（文件不存在或被占用）。",
                    "_debug.txt 出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
                BeginInvoke(new MethodInvoker(Close));
                return;
            }

            for (int i = 0; i < DebugConfig.KeyCount; i++)
            {
                if (!entries[i].Valid)
                {
                    _boxes[i].Enabled = false;
                    _boxes[i].Checked = false;
                    continue;
                }
                _boxes[i].Enabled = true;
                _boxes[i].Checked = entries[i].Value != 0;
            }
        }

        private void Save()
        {
            var entries = new DebugConfig.Entry[DebugConfig.KeyCount];
            for (int i = 0; i < DebugConfig.KeyCount; i++)
            {
                // 禁用的复选框 = 文件里本来就没有这一项，不参与写入
                if (!_boxes[i].Enabled) continue;
                entries[i].Valid = true;
                entries[i].Value = _boxes[i].Checked ? 1 : 0;
            }

            if (!DebugConfig.TryWrite(_configPath, entries))
            {
                MessageBox.Show("写入 _debug.txt 失败（文件可能被占用或只读）。",
                    "_debug.txt 出错", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show("设置保存成功!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
