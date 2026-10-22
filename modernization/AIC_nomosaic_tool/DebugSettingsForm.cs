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
            ClientSize = new Size(420, 330);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei", 9F);

            MakeLabel("_debug.txt（游戏自带调试开关）", 9.5F, true, new Point(30, 16), new Size(360, 20));

            // 每行复选框自带文字（键名+说明），框与文字由同一控件渲染，结构上不可能错位。
            // 旧实现：独立 Label 渲染 6 行文字 + 复选框按硬编码 21px 行距摆放——
            // 文字行高由字体/DPI 决定，两者各自为政，行距一错位最后一个复选框就会
            // 漂到文字末尾的下一行，看起来像"多了一个空选项"（其实它是 announce 的框）。
            for (int i = 0; i < DebugConfig.KeyCount; i++)
            {
                var box = new CheckBox
                {
                    Text = DebugConfig.KeyNames[i] + "    " + DebugConfig.KeyTexts[i],
                    AutoSize = true,
                    Location = new Point(30, 46 + i * 27),
                    Tag = i,
                };
                Controls.Add(box);
                _boxes[i] = box;
            }

            var saveButton = new Button
            {
                Text = "保存设置",
                Location = new Point(60, 230),
                Size = new Size(130, 32),
            };
            saveButton.Click += (s, e) => Save();
            Controls.Add(saveButton);

            var cancelButton = new Button
            {
                Text = "放弃更改",
                Location = new Point(230, 230),
                Size = new Size(130, 32),
            };
            cancelButton.Click += (s, e) => Close();
            Controls.Add(cancelButton);

            var hint = new Label
            {
                Text = "只修改数值本身，注释与其它行原样保留；\n缺失的键会被禁用且不会写入文件。",
                Location = new Point(60, 272),
                Size = new Size(320, 40),
                ForeColor = Color.DimGray,
                Font = new Font("Microsoft YaHei", 8.5F),
            };
            Controls.Add(hint);

            Load += (s, e) => LoadConfig();
            FormClosing += (s, e) =>
            {
                if (DialogResult == DialogResult.OK) return;
                if (MessageBox.Show("你还未保存设置，确定放弃更改？", "提示",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    e.Cancel = true;
            };
        }

        private void MakeLabel(string text, float size, bool bold, Point location, Size dimensions)
        {
            var lb = new Label
            {
                Text = text,
                Font = new Font("Microsoft YaHei", size, bold ? FontStyle.Bold : FontStyle.Regular),
                Location = location,
                Size = dimensions,
            };
            Controls.Add(lb);
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
