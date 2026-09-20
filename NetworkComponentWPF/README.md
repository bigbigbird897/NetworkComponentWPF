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

## 部署
把发布后的 WPF 程序和后端按下面结构放：
```
程序目录/
├─ NetworkComponentWPF.exe
├─ config.json
└─ backend/
   ├─ NetworkComponent.exe        ← 后端 publish 输出
   ├─ appsettings.json
   └─ wwwroot/ ...
```
客户机需安装 **WebView2 Runtime**（Win11 自带）。

## 变更记录
| 日期 | 变更内容 |
|---|---|
| 2026-09-20 | 初版：WebView2 内嵌网页；config.json 配置网址与后端路径；启动拉起后端；关闭驻留托盘，右键彻底退出才关后端。 |
