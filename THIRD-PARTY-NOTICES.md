# 第三方组件与厂商软件

前端使用 Three.js（MIT）、Blockly（Apache-2.0），开发与验证使用 TypeScript、esbuild 和 Playwright。具体版本见 package.json 和 package-lock.json。桌面外壳使用 Microsoft WebView2；.NET 运行时及 WebView2 按微软各自许可分发。

VisionMaster 是海康机器人提供的独立商业软件。本仓库仅提供调用其公开 SDK 的适配源码，不包含厂商 SDK DLL、算法模块、安装包、授权文件或官方示例 .sol。使用者须自行安装并按厂商要求获得相应授权。本项目不代表厂商官方产品。

发布便携包时，scripts/publish-desktop.ps1 会收集所用运行时、Three.js、Blockly 和 WebView2 的许可文件。不要把本机完整 VM 安装目录、用户方案、访问令牌或采样数据直接提交到 Git。

本仓库尚未指定项目自有源码的开放源代码许可证；公开可见不等于已授予任意再许可或再分发权利。后续由项目所有者决定许可证。
