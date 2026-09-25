using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
using WinForms = System.Windows.Forms;

namespace NetworkComponentWPF
{
    /// <summary>
    /// 主窗口：内嵌 WebView2 显示 Web 控制台，启动时拉起后端接口服务，
    /// 点关闭按钮只隐藏到托盘，右键托盘“退出”才真正关闭后端并退出程序。
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>运行配置（来自 exe 同目录 config.json）</summary>
        private AppConfig _config = new();

        /// <summary>拉起的后端进程引用，退出时负责关闭</summary>
        private Process? _backendProcess;

        /// <summary>系统托盘图标</summary>
        private WinForms.NotifyIcon _notifyIcon = new();

        /// <summary>是否正在真正退出（用于绕过关闭事件的隐藏逻辑）</summary>
        private bool _isExiting;

        /// <summary>后端最近一次启动时间，用于判断是否"秒退"（启动失败）</summary>
        private DateTime _backendStartTime = DateTime.MinValue;

        /// <summary>连续重启/重试次数，防止配置错误导致无限重启循环</summary>
        private int _restartRetries;

        /// <summary>后端监听端口（与 config.json 中 WebViewUrl 对应），重启前等待其释放</summary>
        private const int BackendPort = 5000;

        public MainWindow()
        {
            InitializeComponent();
            // 设置窗口图标（WPF 用 ImageSource，从内嵌资源加载）
            try
            {
                this.Icon = new System.Windows.Media.Imaging.BitmapImage(
                    new Uri("pack://application:,,,/Resources/Icon/app.ico", UriKind.Absolute));
            }
            catch { /* 图标缺失时用默认 */ }
            Loaded += MainWindow_Loaded;
        }

        /// <summary>加载托盘图标（System.Drawing.Icon）：优先内嵌资源，回退 exe 同目录 app.ico，最后用系统默认</summary>
        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/Resources/Icon/app.ico", UriKind.Absolute);
                var info = System.Windows.Application.GetResourceStream(uri);
                if (info != null) return new System.Drawing.Icon(info.Stream);
            }
            catch { }
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(path)) return new System.Drawing.Icon(path);
            }
            catch { }
            return System.Drawing.SystemIcons.Application;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. 读取配置（网页地址、后端程序路径）
            _config = LoadConfig();

            // 2. 初始化托盘图标
            InitTray();

            // 3. 启动后端接口服务
            StartBackend();

            // 4. 初始化 WebView2 并打开配置的网页
            try
            {
                // 后端刚启动需要一点时间监听端口，稍作等待再导航
                await System.Threading.Tasks.Task.Delay(1500);
                // CreateAsync 第一个参数 browserExecutableFolder 传 null = 使用系统已安装的 Evergreen 运行时；
                // 第二个参数才是本程序的用户数据目录（Cookie/缓存）。
                // 注意：之前误把用户数据目录传给了第一个参数，导致 WebView2 去空文件夹里找运行时而报错。
                var userDataDir = Path.Combine(AppContext.BaseDirectory, "WebView2_Data");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .CreateAsync(null, userDataDir);
                await webView.EnsureCoreWebView2Async(env);
                webView.CoreWebView2.Navigate(_config.WebViewUrl);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("WebView2 初始化失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>读取 exe 同目录下的 config.json，读不到则用默认值</summary>
        private static AppConfig LoadConfig()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "config.json");
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<AppConfig>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
                }
            }
            catch { /* 配置损坏时用默认值 */ }
            return new AppConfig();
        }

        /// <summary>初始化系统托盘图标和右键菜单</summary>
        private void InitTray()
        {
            _notifyIcon.Icon = LoadAppIcon();
            _notifyIcon.Text = "工业通信中台";
            _notifyIcon.Visible = true;

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("显示主界面", null, (s, e) => ShowMain());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("彻底退出", null, (s, e) => ExitApplication());
            _notifyIcon.ContextMenuStrip = menu;

            // 双击托盘图标 = 显示主界面
            _notifyIcon.DoubleClick += (s, e) => ShowMain();
        }

        /// <summary>启动后端接口服务（按配置中的 exe 路径）</summary>
        private void StartBackend()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_config.BackendExePath)) return;

                string exePath = Path.IsPathRooted(_config.BackendExePath)
                    ? _config.BackendExePath
                    : Path.Combine(AppContext.BaseDirectory, _config.BackendExePath);

                if (!File.Exists(exePath))
                {
                    _notifyIcon.ShowBalloonTip(2000, "提示",
                        "未找到后端程序：" + exePath, WinForms.ToolTipIcon.Warning);
                    return;
                }

                string workDir = string.IsNullOrWhiteSpace(_config.BackendWorkingDirectory)
                    ? Path.GetDirectoryName(exePath)!
                    : Path.Combine(AppContext.BaseDirectory, _config.BackendWorkingDirectory);

                _backendProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        WorkingDirectory = workDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,      // 不弹出后端控制台黑窗
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    },
                    EnableRaisingEvents = true
                };
                _backendProcess.Exited += OnBackendExited;
                _backendStartTime = DateTime.Now;
                _backendProcess.Start();
            }
            catch (Exception ex)
            {
                _notifyIcon.ShowBalloonTip(2000, "后端启动失败", ex.Message, WinForms.ToolTipIcon.Error);
            }
        }

        /// <summary>后端进程退出时触发：退出码 42 表示"配置已保存，请重启"，自动重新拉起。
        /// 若新进程刚起来就秒退（如端口被占/配置错误），也会自动重试几次。</summary>
        private async void OnBackendExited(object? sender, EventArgs e)
        {
            if (_isExiting) return; // WPF 主动退出，不重启

            int code = -1;
            try { code = _backendProcess?.ExitCode ?? -1; } catch { }

            // 正常稳定运行超过 15 秒，清零重试计数
            if ((DateTime.Now - _backendStartTime).TotalSeconds > 15) _restartRetries = 0;

            bool wantRestart = false;
            if (code == 42)
            {
                // 用户保存配置请求重启
                wantRestart = true;
                try { _notifyIcon.ShowBalloonTip(2000, "配置已更新", "后端即将自动重启...", WinForms.ToolTipIcon.Info); } catch { }
            }
            else if (_restartRetries < 3 && (DateTime.Now - _backendStartTime).TotalSeconds < 12)
            {
                // 非 42 且启动后很快就退出：多半是端口被占或新配置导致启动失败，重试
                _restartRetries++;
                wantRestart = true;
                try { _notifyIcon.ShowBalloonTip(2000, "后端启动异常",
                    $"检测到后端退出（码 {code}），将在 3 秒后第 {_restartRetries} 次重试…", WinForms.ToolTipIcon.Warning); } catch { }
            }

            if (!wantRestart)
            {
                if (code != 42)
                {
                    try { _notifyIcon.ShowBalloonTip(3000, "后端已停止",
                        $"后端退出码 {code}，未自动重启。", WinForms.ToolTipIcon.Error); } catch { }
                }
                return;
            }

            // 先等端口真正释放（最多 8 秒），再重新拉起
            await WaitPortFreeAsync(BackendPort, TimeSpan.FromSeconds(8));
            await System.Threading.Tasks.Task.Delay(1000);
            try { Dispatcher.Invoke(StartBackend); } catch { }
        }

        /// <summary>轮询等待本地端口释放（连接不上即视为已释放）</summary>
        private static async System.Threading.Tasks.Task WaitPortFreeAsync(int port, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                if (!IsPortInUse(port)) return;
                await System.Threading.Tasks.Task.Delay(300);
            }
        }

        /// <summary>尝试连接 localhost:port，连得上说明端口仍被占用</summary>
        private static bool IsPortInUse(int port)
        {
            try
            {
                using var cli = new TcpClient();
                var ar = cli.BeginConnect("127.0.0.1", port, null, null);
                if (ar.AsyncWaitHandle.WaitOne(300))
                {
                    cli.EndConnect(ar);
                    return true; // 连接成功 = 有进程在监听
                }
                return false; // 超时连不上 = 基本空闲
            }
            catch { return false; }
        }

        /// <summary>关闭后端进程</summary>
        private void StopBackend()
        {
            try
            {
                if (_backendProcess != null && !_backendProcess.HasExited)
                {
                    // 先尝试优雅关闭，3 秒不退再强杀
                    try { _backendProcess.CloseMainWindow(); } catch { }
                    if (!_backendProcess.WaitForExit(3000))
                    {
                        try { _backendProcess.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { }
        }

        /// <summary>显示并激活主窗口</summary>
        private void ShowMain()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>真正退出：关后端、关托盘、关程序</summary>
        private void ExitApplication()
        {
            _isExiting = true;
            _notifyIcon.Visible = false;
            StopBackend();
            System.Windows.Application.Current.Shutdown();
        }

        /// <summary>点右上角关闭：不退出，只隐藏到托盘</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon.ShowBalloonTip(2000, "工业通信中台",
                    "程序已驻留后台，接口服务仍在运行。双击托盘图标可重新打开。",
                    WinForms.ToolTipIcon.Info);
                return;
            }
            base.OnClosing(e);
        }

        /// <summary>运行配置模型</summary>
        private class AppConfig
        {
            /// <summary>WebView2 要打开的网页地址</summary>
            public string WebViewUrl { get; set; } = "http://localhost:5000";

            /// <summary>后端接口服务 exe 路径（相对 exe 目录或绝对路径）</summary>
            public string? BackendExePath { get; set; }

            /// <summary>后端工作目录</summary>
            public string? BackendWorkingDirectory { get; set; }
        }
    }
}
