# NetworkComponentWPF · 桌面壳

把「.NET 后端接口服务 + Web 控制台」包成一个 Windows 桌面程序：用 Edge WebView2 内嵌网页，启动时自动拉起后端接口，关闭时驻留系统托盘。

## 技术栈
- .NET 10（net10.0-windows）WPF
- Microsoft.Web.WebView2（内嵌 Chromium Edge）
- WinForms NotifyIcon（系统托盘）
- System.Text.Json 读取运行配置

## 运行行为
1. 启动 WPF → 读取 `config.json` → 按 `BackendExePath` 启动后端接口服务（无黑窗）。
2. 等待约 1.5 秒后，WebView2 打开 `WebViewUrl` 指向的网页（默认 `http://localhost:5000`）。
3. 点窗口右上角 **×**：不退出，窗口隐藏到系统托盘，后端继续运行。
4. **双击托盘图标** / 右键「显示主界面」：重新打开窗口。
5. 右键托盘「彻底退出」：先优雅关闭后端进程（3 秒不退则强杀整个进程树），再退出 WPF。

## 配置文件（exe 同目录 config.json，可随时改）
```json
{
  "WebViewUrl": "http://localhost:5000",
  "BackendExePath": "backend\\NetworkComponent.exe",
  "BackendWorkingDirectory": "backend"
}
```
- `WebViewUrl`：WebView2 打开的网页地址。
- `BackendExePath`：后端 exe 路径，相对 WPF 程序目录，也可写绝对路径。
- `BackendWorkingDirectory`：后端工作目录（决定后端 appsettings / license / wwwroot 从哪读）。

## 部署结构
```
程序目录/
├─ NetworkComponentWPF.exe
├─ config.json
└─ backend/
   ├─ NetworkComponent.exe        ← 后端 publish 输出
   ├─ appsettings.json
   └─ wwwroot/ ...
```
客户机需安装 **WebView2 Runtime**（Win11 自带，Win10 可装 Evergreen Runtime）。

## 变更记录
| 日期 | 变更内容 |
|---|---|
| 2026-09-20 | 初版：csproj 引入 Microsoft.Web.WebView2、开启 UseWindowsForms（托盘）；新增 config.json 配置网页地址与后端路径；MainWindow 用 WebView2 占满窗口显示网页；启动时按配置拉起后端接口服务；点关闭按钮只隐藏到托盘、后端继续运行；右键托盘“彻底退出”才关闭后端并退出程序；App.xaml.cs 基类补全命名空间消除 WPF/WinForms 的 Application、MessageBox 歧义。 |
| 2026-09-20 | csproj 增加 `PublishBackend` 目标（AfterTargets=Build）：每次生成 WPF 时自动 `dotnet publish` 后端工程到 `$(OutDir)backend\`，与 config.json 的 `backend\NetworkComponent.exe` 相对路径对应；后端工程路径可用 `-p:BackendProjectPath=` 覆盖，加 `-p:PublishBackend=false` 可跳过。 |
| 2026-09-20 | 支持显示打包后的 Web 控制台 dist：后端 Program.cs 增加 `UseDefaultFiles/UseStaticFiles/MapFallbackToFile("index.html")`，由后端在根路径直接托管 SPA；csproj 增加 `CopyWebDist` 目标（在 PublishBackend 之后），自动把 `NetworkComponentWeb\dist\**` 拷到 `backend\wwwroot\`。WebViewUrl 默认 `http://localhost:5000` 即打开控制台；想显示任意其他网址，改 config.json 的 `WebViewUrl` 即可（http/https 任意地址）。 |
| 2026-09-20 | csproj 增加 `BuildWeb` 目标：WPF 构建时先在 `NetworkComponentWeb` 目录执行 `npm run build` 生成最新 dist，再执行 `CopyWebDist` 拷入后端 wwwroot。可用 `-p:BuildWeb=false` 跳过前端构建、`-p:WebProjectPath=` 改前端目录。整条链：BuildWeb → PublishBackend → CopyWebDist。 |
| 2026-09-20 | 工程目录扁平化：csproj 由三层嵌套改为两层（`NetworkComponentWPF\NetworkComponentWPF\`），csproj 中相对后端/Web 的路径由 `..\..\..\` 修正为 `..\..\`，已在新位置重新构建验证整条链通过。 |
| 2026-09-20 | 后端自动重启加固：原逻辑只在退出码 42 时固定等 2 秒就重启，端口未释放/新进程秒退时不会再拉起。改为：重启前先轮询等待 5000 端口真正释放（最多 8 秒）；新进程若启动后 12 秒内退出（端口被占或新配置导致启动失败）自动重试，最多 3 次，稳定运行 15 秒后清零计数；非 42 的正常退出弹气泡提示，不再静默。 |
