# GitHub 源码交付

仓库地址名：`dotnet-robot-arm-simulator`；项目中文标题：基于.net开发的仿真机械臂平台。仓库按所有者要求公开。

源码仓库根目录即包含 `AxisLab.slnx` 的目录，仿真平台与独立 WPF 都放在 `src/` 中。外层旧 WPF 发布目录、历史压缩包、依赖目录、生成的 EXE、用户工程、采样数据和厂商 .sol 不进入源码提交。

## 从源码运行

安装 Windows、.NET 10 SDK、Node.js 24 和 WebView2 Runtime。克隆后在仓库根目录运行：

```powershell
npm ci --ignore-scripts --no-audit --no-fund
npm run typecheck
npm run build
dotnet build AxisLab.slnx -c Release
.\start.ps1 -NoBuild
```

浏览器打开脚本显示的本地地址。另一个终端可运行 WPF：

```powershell
dotnet run --project src/WpfDemo/AxisLab.WpfDemo.csproj -c Release
```

要使用 VM SDK，再安装本机 VisionMaster 4.3.0、有效 SDK 授权和 .NET Framework 4.8 开发引用，运行 `src/WpfDemo/build.ps1` 构建配套 Worker。默认安装目录为 `C:\Program Files\VisionMaster4.3.0`。

## 后续提交

```powershell
git status
git add src docs samples scripts tests README.md
git commit -m "Describe the change"
git push
```

推送后 GitHub Actions 会检查前端、核心/通信/标定测试，以及 Windows 解决方案和主 WPF 编译。CI 不包含厂商 VM 安装和授权，真实 SDK 联调在本机进行。

## 便携程序

运行 `scripts/publish-desktop.ps1` 与 `scripts/publish-wpf-framework.ps1` 生成 ZIP。ZIP 适合作为 GitHub Release 附件，避免提交到 Git 历史；厂商 SDK 与授权仍由使用者单独安装。发布前应复核新版本 EXE 的隔离验收结果。

`scripts/publish-github.ps1` 可复用本机 Git Credential Manager 账户创建指定公开仓库并推送 main，过程中不会输出或保存访问令牌。项目许可证尚由所有者决定，第三方许可见仓库根目录的 THIRD-PARTY-NOTICES.md。
