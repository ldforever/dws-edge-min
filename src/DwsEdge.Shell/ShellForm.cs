using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace DwsEdge.Shell
{
    /// <summary>
    /// 外壳的状态窗口：平台没起来（或中途断了）时显示它，起来了就自动收起来。
    ///
    /// 现场价值：操作员看到的永远是一个明确的界面 ——
    ///   * 正在连接平台…（已等 12 秒，第 2 次重试）
    ///   * 平台断开，正在重连…（SSE 那头的"重连中"是页面级提示，这里是进程级提示）
    ///   * 一直连不上 → 告诉你去看哪条日志、点哪里重试
    /// 而不是白屏 + "是不是坏了"。
    /// </summary>
    internal sealed class ShellForm : Form
    {
        private readonly Program.Options _options;
        private readonly string _edge;

        private Label _title;
        private Label _status;
        private Label _detail;
        private Label _hint;
        private Button _retry;
        private Button _openBrowser;
        private Button _exit;
        private System.Windows.Forms.Timer _ticker;

        private Thread _worker;
        private volatile bool _working;
        private int _elapsedSeconds;
        private int _retryCount;
        private string _lastError = "";
        private DateTime _downSince = DateTime.MinValue;
        private DateTime _hostRestartAt = DateTime.MinValue;

        public ShellForm(Program.Options options, string edge)
        {
            _options = options;
            _edge = edge;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = "DWS 物流解码平台";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 260);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(18, 24, 31);
            ForeColor = Color.FromArgb(201, 209, 217);

            _title = new Label();
            _title.Text = "DWS 物流解码平台";
            _title.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);
            _title.AutoSize = true;
            _title.Location = new Point(24, 22);

            _status = new Label();
            _status.Text = "正在启动…";
            _status.Font = new Font("Microsoft YaHei UI", 10F);
            _status.AutoSize = true;
            _status.Location = new Point(24, 64);

            _detail = new Label();
            _detail.Text = "平台地址：" + _options.Url;
            _detail.ForeColor = Color.FromArgb(139, 152, 169);
            _detail.AutoSize = true;
            _detail.Location = new Point(24, 96);

            _hint = new Label();
            _hint.ForeColor = Color.FromArgb(139, 152, 169);
            _hint.Size = new Size(512, 60);
            _hint.Location = new Point(24, 122);
            _hint.Text = "采集宿主与平台会自动拉起；日志在 runtime\\logs\\ 下。";

            _retry = new Button();
            _retry.Text = "重试";
            _retry.Size = new Size(88, 30);
            _retry.Location = new Point(24, 196);
            _retry.Click += delegate { RestartWorker(); };

            _openBrowser = new Button();
            _openBrowser.Text = "用浏览器打开";
            _openBrowser.Size = new Size(110, 30);
            _openBrowser.Location = new Point(122, 196);
            _openBrowser.Click += delegate
            {
                try { Process.Start(_options.Url); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "打开浏览器失败"); }
            };

            _exit = new Button();
            _exit.Text = "退出";
            _exit.Size = new Size(88, 30);
            _exit.Location = new Point(448, 196);
            _exit.Click += delegate { Close(); };

            Controls.Add(_title);
            Controls.Add(_status);
            Controls.Add(_detail);
            Controls.Add(_hint);
            Controls.Add(_retry);
            Controls.Add(_openBrowser);
            Controls.Add(_exit);

            _ticker = new System.Windows.Forms.Timer();
            _ticker.Interval = 1000;
            _ticker.Tick += delegate { RefreshStatusLine(); };
            _ticker.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RestartWorker();
        }

        /// <summary>起（或重启）一个后台线程去等平台、开页面、看守连接。</summary>
        private void RestartWorker()
        {
            if (_working)
            {
                return;
            }
            _working = true;
            _elapsedSeconds = 0;
            _retryCount++;
            _downSince = DateTime.MinValue;
            _status.Text = "正在连接平台…";

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "shell-worker";
            _worker.Start();
        }

        private void WorkerLoop()
        {
            try
            {
                if (!WaitForPlatform())
                {
                    Ui(delegate
                    {
                        _status.Text = "平台没有起来（已等 " + _elapsedSeconds + " 秒）";
                        // 把 start-all 的输出尾巴显示出来：是端口被占、还是脚本没跑起来，一眼能看到
                        _hint.Text = "最后错误：" + _lastError + Environment.NewLine +
                                     "start-all 输出（logs\\shell-start.log 末尾）：" +
                                     Program.ReadStartLogTail(_options.RuntimeDir, 5) + Environment.NewLine +
                                     "排查：跑一次 runtime\\tools\\self-check.ps1（加密狗 / 相机 / 端口占用），或点「重试」。";
                        _working = false;
                    });
                    return;
                }

                Ui(delegate
                {
                    _status.Text = "平台已就绪，正在打开界面…";
                    _hint.Text = _edge == null
                        ? "没有找到 Edge，将用系统默认浏览器打开。"
                        : "界面窗口打开后本窗口会自动收起；平台掉线时会自动弹回来。";
                });

                Process browser = null;
                Ui(delegate { browser = Program.LaunchBrowser(_options, _edge); });
                Thread.Sleep(1500);

                if (browser == null)
                {
                    // 没有 Edge：页面已在默认浏览器里打开，外壳到此为止
                    Thread.Sleep(2000);
                    Ui(Close);
                    return;
                }

                Ui(Hide);

                // 看守：页面开着的时候盯着平台，掉了就把窗口弹回来
                while (!browser.HasExited)
                {
                    Thread.Sleep(5000);
                    string error;
                    if (!Program.IsPlatformHealthy(_options.Url, out error))
                    {
                        if (_downSince == DateTime.MinValue)
                        {
                            _downSince = DateTime.Now;
                        }
                        else if ((DateTime.Now - _downSince).TotalSeconds >= 10)
                        {
                            string detail = error;
                            Ui(delegate
                            {
                                _status.Text = "平台断开，正在重连…（已断 " +
                                               (int)(DateTime.Now - _downSince).TotalSeconds + " 秒）";
                                _hint.Text = "最后错误：" + detail + Environment.NewLine +
                                             "页面里的实时数据会停住；恢复后自动继续，不用重启。";
                                if (!Visible)
                                {
                                    Show();
                                    Activate();
                                }
                            });
                        }
                        continue;
                    }

                    // 平台是好的 → 再看采集宿主在不在（以前这里没人管：平台活着、宿主死了，界面只是"没数据"）
                    if (_downSince != DateTime.MinValue)
                    {
                        _downSince = DateTime.MinValue;
                        Ui(Hide);
                    }

                    string hostError;
                    if (Program.IsHostRunning(_options.Url, out hostError))
                    {
                        if (_hostRestartAt != DateTime.MinValue)
                        {
                            _hostRestartAt = DateTime.MinValue;
                            Ui(Hide);
                        }
                        continue;
                    }

                    if (_hostRestartAt == DateTime.MinValue)
                    {
                        _hostRestartAt = DateTime.Now;   // 先观察 15 秒，避免和外部手动启动打架
                        continue;
                    }
                    if ((DateTime.Now - _hostRestartAt).TotalSeconds < 15)
                    {
                        continue;
                    }

                    _hostRestartAt = DateTime.Now;
                    Program.StartRuntime(_options.RuntimeDir);
                    string hostDetail = hostError;
                    Ui(delegate
                    {
                        _status.Text = "采集宿主未运行，正在拉起…";
                        _hint.Text = "原因：" + hostDetail + Environment.NewLine +
                                     "已执行 runtime\\tools\\start-all.ps1（输出见 logs\\shell-start.log）；" +
                                     "如果一直起不来，多半是相机/加密狗不在线——宿主会自己重试，界面顶部会显示「正在等相机」。";
                        if (!Visible)
                        {
                            Show();
                            Activate();
                        }
                    });
                }

                Ui(Close);
            }
            catch (Exception ex)
            {
                Ui(delegate
                {
                    _status.Text = "外壳异常：" + ex.Message;
                    _working = false;
                });
            }
        }

        /// <summary>等平台就绪：探健康检查，超时前会自己拉起一遍 start-all.ps1。</summary>
        private bool WaitForPlatform()
        {
            DateTime deadline = DateTime.Now.AddSeconds(_options.WaitSeconds);
            int startAttempts = 0;
            while (DateTime.Now < deadline)
            {
                if (IsDisposed)
                {
                    return false;
                }

                string error;
                if (Program.IsPlatformHealthy(_options.Url, out error))
                {
                    return true;
                }
                _lastError = error;

                // 等 3 秒还没起来就先拉一遍；25 秒还没起来再补一次（可能是上一次刚好在抢端口/被占用）。
                // 拉起动作会写 logs\shell-start.log，失败原因能从这里看到。
                int nextAttemptAt = startAttempts == 0 ? 3 : 25;
                if (!_options.NoStart && startAttempts < 2 && _elapsedSeconds >= nextAttemptAt)
                {
                    startAttempts++;
                    Program.StartRuntime(_options.RuntimeDir);
                    int attempt = startAttempts;
                    Ui(delegate
                    {
                        _hint.Text = "第 " + attempt + " 次自动拉起采集宿主与平台（runtime\\tools\\start-all.ps1）…" +
                                     Environment.NewLine + "输出写到 logs\\shell-start.log";
                    });
                }

                Thread.Sleep(1000);
                _elapsedSeconds++;
            }
            return false;
        }

        private void RefreshStatusLine()
        {
            if (_working && _status.Text.StartsWith("正在连接平台", StringComparison.Ordinal))
            {
                _status.Text = "正在连接平台…（已等 " + _elapsedSeconds + " 秒）";
                _detail.Text = "平台地址：" + _options.Url + "　runtime：" + _options.RuntimeDir;
            }
        }

        /// <summary>把动作丢回 UI 线程执行（后台线程不碰控件）。</summary>
        private void Ui(Action action)
        {
            if (IsDisposed)
            {
                return;
            }
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch (Exception)
            {
                // 窗口已销毁：忽略
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _ticker.Stop();
            _working = false;
            base.OnFormClosed(e);
        }
    }
}
