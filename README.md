# PCL CE-AI

> 基于 [PCL CE](https://github.com/PCL-Community/PCL-CE) 的纯 AI 启动页改造版 Minecraft 启动器。
> 启动页只保留 AI 对话、账号、版本和启动按钮，其余功能全部收进设置页。

PCL CE-AI 把原本分散在主界面的操作交给 AI 助手：你可以直接说“启动游戏”“下载 1.21.1 并装 Fabric”“把不兼容的 Mod 关掉”，由 AI 调用启动器内置工具完成；游戏崩溃后，崩溃报告会自动提交给 AI 分析。

## 主要特性

- **AI 对话主界面**：流式输出、工具调用、取消回复、清空对话。
- **多会话**：新建对话、对话列表切换，历史保存在本地。
- **上下文压缩**：手动一键压缩，或上下文过长时自动生成摘要继续对话。
- **底部只留四个操作**：设置（左下角）、账号、选择版本、启动游戏。
- **一切交给 AI**：
  - 启动游戏、切换版本
  - 打开游戏安装页并预选版本
  - 列出 / 启用 / 禁用 / 删除 Mod
  - 读取游戏日志、最近一次崩溃报告
  - 打开启动器内的任意页面
- **崩溃自动分析**：游戏崩溃报告生成后自动提交给 AI，回到启动页即可看到分析结果。
- **DeepSeek harness**：兼容 OpenAI `/chat/completions` 的任意接口，默认 `deepseek-chat`，API Key 本地加密保存。
- **首次启动引导**：未配置 API Key 时自动进入「设置 → AI 接入」，内置获取 Key 与测试连接的步骤。
- **设置页整合**：下载、Mod、整合包、资源包、光影、世界、数据包、实例设置、日志、任务管理、联机工具全部收进设置。
- **微软正版登录**：支持编译期注入 Azure 客户端 ID，产物在任何设备上都能直接正版登录。
- **更新走 GitHub Releases**：不再连接官方更新源，避免被自动更新回官方版本。

## 会话与上下文

- 标题栏右侧（对话区右上角）：新建对话、对话列表、压缩上下文、清空对话。
- 对话保存在 `PCL/AiChat/conversations.json`，重启后可以继续。
- 上下文超过阈值（默认 16000 字符）时会自动压缩成摘要；也可以随时点「压缩上下文」手动压缩。

## AI 接入

1. 打开启动器 → 左下角齿轮 → **AI 接入**。
2. 填写：
   - API 地址：`https://api.deepseek.com`（或任意兼容中转）
   - 模型：`deepseek-chat`
   - API Key：DeepSeek 控制台创建的 Key
3. 点「保存并测试」，成功后回到启动页直接对话。

Key 使用本地加密配置保存，只会发送到你填写的 API 地址。

## 构建

> 仓库使用 `LangVersion 15.0` 与 `net10.0`，需要 **.NET 11 预览版 SDK** 编译；运行需要 .NET 10 运行时。

```powershell
dotnet build PCL.Launcher/PCL.Launcher.csproj -c Debug
```

### 带微软正版登录构建

微软 OAuth 需要你自己的 Azure 应用客户端 ID（公共客户端、允许设备代码流、`XboxLive.signin offline_access` 权限）。把 ID 在编译期嵌入：

```powershell
./scripts/build-with-ms-login.ps1 -ClientId "<你的 Azure 客户端 ID>"
```

脚本会关闭共享编译并强制 Rebuild，构建后自动校验客户端 ID 是否写入 `PCL.Core.dll`。

> 第三方应用调用 Minecraft API 前需要通过 Mojang 的 [AppID 审核](https://aka.ms/mce-reviewappid)；审核通过前微软登录会在最后一步返回 403。审核期间可以使用离线登录或第三方验证登录。

## 更新

「设置 → 更新」检查本仓库的 GitHub Releases。发布一个 tag 形如 `v2.15.2` 的 Release 后，启动器就会自动识别并提供下载入口。

## 反馈

- Issues：<https://github.com/aodianjun/PCL-CE-AI/issues>
- 「设置 → 反馈」会直接读取本仓库的 Issues 并按标签分组；没有标签的 feedback 默认归入「等待处理」。

## 致谢与许可

- 上游项目：[PCL-Community/PCL-CE](https://github.com/PCL-Community/PCL-CE)、[PCL2](https://github.com/Meloong-Git/PCL)
- 原作者：LTCat（龙腾猫跃）与 PCL Community 贡献者
- 本构建维护者：aodianjun
- 许可证：[Apache License 2.0](LICENSE)

本项目是社区个人构建，与 Mojang Studios、Microsoft 无隶属关系。Minecraft 是 Mojang Studios 的商标。
