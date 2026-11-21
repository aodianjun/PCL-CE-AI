# PCL CE-AI · 纯 AI 启动页改造说明

本次改造把 PCL CE-AI 的启动页重构成「AI 对话为主 + 最少操作按钮」，并把原启动器分散的功能收进设置页。
所有改动都在本仓库内，未引入新的第三方包。

## 一、界面变化

### 1. 启动页（`PageLaunchRight`）

- 左侧原来的实例/账号栏不再挂载，整页只有一个 AI 对话区。
- 对话框支持流式输出、取消回复、清空对话，以及模型名/状态显示。
- 底部操作栏只保留：
  - 左下角 **设置**（齿轮）→ 打开设置页
  - **账号** → 打开账号抽屉（复用原有登录/档案/皮肤界面）
  - **选择版本** → 版本选择页
  - **启动游戏**（没有实例时自动变成「下载游戏」，跳到安装页）
- 启动过程中的阶段、进度、取消按钮直接显示在对话输入框的位置。
- 首次运行（未配置 API Key 且未完成引导）会自动打开「设置 → AI 接入」。

### 2. 标题栏

- 去掉了「启动 / 下载 / 工具 / 设置」四个标签，只保留 Logo 与窗口按钮。
- 下载、工具、设置都以二级页面打开，标题栏显示返回箭头与页面标题。

### 3. 设置页（`PageIds.Setup`）

新增两个子页，设置默认打开「功能入口」：

- **功能入口**（`PageSetupFeatures`）
  - 下载与安装：游戏安装、Mod、整合包、资源包、光影、世界、数据包
  - 版本与运行：选择版本、实例设置、游戏日志、任务管理
  - 工具与联机：联机大厅、实用工具
  - AI 助手：跳转 AI 接入、回到启动页
- **AI 接入**（`PageSetupAi`）
  - 内置接入引导：DeepSeek 注册/创建 Key、填写、保存并测试的完整步骤
  - API 地址 / 模型 / API Key 输入，允许 AI 操作启动器、崩溃自动分析开关
  - 「保存并测试」「获取 API Key」「跳过引导」
  - 隐私说明

原「启动 / Java / 游戏管理 / 联机 / 界面 / 语言 / 杂项 / 关于 / 更新 / 反馈 / 日志」子页全部保留。

## 二、DeepSeek harness（`PCL.Launcher/Ai/`）

- `AiChat.cs`
  - `AiChatService`：OpenAI 兼容 Chat Completions 客户端，`stream=true` SSE 解析、历史裁剪、取消、错误气泡。
  - 支持工具调用循环（最多 8 轮），自动把工具结果回传给模型继续回答。
  - `SubmitCrashReport()`：把崩溃报告作为隐藏系统消息提交，并在聊天里自动发起分析。
  - `TestConnectionAsync()`：设置页「保存并测试」使用。
  - 兼容只返回普通 JSON 的接口，以及 `https://api.deepseek.com` 或带 `/v1` 的基址。
- `AiTools.cs`
  - `get_launcher_status`、`list_instances`、`select_instance`
  - `launch_game`
  - `list_mods`、`set_mod_enabled`、`delete_mod`（操作当前实例 `mods` 目录）
  - `open_page`（下载/工具/设置/实例设置/日志/任务管理等）
  - `search_resource`（按版本与加载器打开资源下载页）
  - `get_game_log`、`get_last_crash`
  - `install_version_note`（打开安装页并预选版本号）
- 配置项（`Config.Ai`）：
  - `BaseUrl`、`ApiKey`（本地加密保存）、`Model`
  - `EnableTools`、`AutoAnalyzeCrash`、`OnboardingDone`、`HistoryLimit`、`SystemPromptExtra`

## 三、崩溃报错自动交给 AI

`CrashPresenter.OutputAsync()` 在生成崩溃报告后，如果 `Config.Ai.AutoAnalyzeCrash` 为真：

1. 调用 `AiChatService.SubmitCrashReport(报告全文)`；
2. 自动切回启动页；
3. 把报告作为隐藏上下文提交给模型，聊天里自动出现一条分析请求与回复。

没有配置 API Key 时只保存报告，等接入后再分析。

## 四、改动文件

新增：

- `PCL.Launcher/Ai/AiChat.cs`
- `PCL.Launcher/Ai/AiTools.cs`
- `PCL.Launcher/Views/Pages/Setup/PageSetupAi.axaml(.cs)`
- `PCL.Launcher/Views/Pages/Setup/PageSetupFeatures.axaml(.cs)`

修改：

- `PCL.Core/App/Config.cs`（新增 `Ai` 配置组）
- `PCL.Launcher/Views/Pages/Launch/PageLaunchRight.axaml(.cs)`（重写为 AI 对话页）
- `PCL.Launcher/Views/Pages/Launch/PageLaunchHost.cs`（不再挂载左栏）
- `PCL.Launcher/UI/Crash/CrashRunner.cs`（崩溃报告提交给 AI）
- `PCL.Launcher/Navigation/Catalog/*PageModule.cs`（去掉下载/工具/设置标签，新增设置子页）
- `PCL.Launcher/Navigation/SubPageIds.cs`
- `PCL.Launcher/Views/Pages/{Download,Setup,Tools}/*Host.cs`（改为二级页面，带返回）
- `PCL.Launcher/Views/Shell/TitleBar.axaml.cs`（只有一个主入口时隐藏标签栏）
- `PCL.Localization/Languages/*.axaml`（新增 65 个本地化键，7 种语言）

## 五、构建与验证

仓库使用 `LangVersion 15.0` 与 `.NET 10` 目标框架，需要 **.NET 11 预览版 SDK** 才能编译（.NET 10 SDK 会报 `CS1617: /langversion 15.0 无效`）。

```bash
dotnet build PCL.Launcher/PCL.Launcher.csproj -c Debug
dotnet test PCL.Core.Test/PCL.Core.Test.csproj --filter "FullyQualifiedName~Localization"
```

已在本机验证：

- `PCL.Launcher` Debug 构建 0 错误；
- 本地化测试 9/9 通过（键齐全、无重复、命名规范）；
- 实机启动：启动页正常渲染，首次运行自动进入「设置 → AI 接入」，无 XAML/绑定异常。

## 六、关于正版（微软）登录

- 仓库本身不包含微软 OAuth 客户端 ID；官方发布版由 CI 通过 `PCL_MS_CLIENT_ID` 注入（见 `.github/workflows/reusable-build.yml`）。
- 本地 Debug 构建没有该密钥时，微软登录会失败，日志中为 `Microsoft OAuth client id is not configured.`。现在点“开始正版验证”会直接提示缺少客户端 ID，并建议改用离线/第三方登录。
- 想在本地临时使用正版登录：注册一个 Azure 应用（公共客户端、允许设备代码流、勾选 Xbox Live 相关权限），启动前设置环境变量 `PCL_MS_CLIENT_ID=<客户端 ID>`。Debug 构建会在运行时读取该环境变量。

### 让所有设备直接可用正版登录

Release 构建默认不读运行时环境变量，必须在**编译期**把客户端 ID 写进产物：

1. 注册 Azure 应用（用你自己的 Microsoft 账号，免费）：
   - 打开 portal.azure.com → Microsoft Entra ID → 应用注册 → 新注册；
   - 支持的账户类型选「仅个人 Microsoft 账户」（或「任何组织目录中的账户和个人 Microsoft 账户」）；
   - 「身份验证」→「允许公共客户端流」设为「是」（设备代码流必需）；
   - 「API 权限」→ 添加权限 → 搜索 Xbox Live → 委托权限 → `XboxLive.signin`；再添加 Microsoft Graph 委托权限 `offline_access`（`openid`、`profile` 通常默认已有）；
   - 复制「应用程序(客户端) ID」。
2. 用仓库脚本构建：

   ```powershell
   ./scripts/build-with-ms-login.ps1 -ClientId "<你的客户端 ID>"
   ```

   等价的手动命令（注意 `-t:Rebuild` 与 `UseSharedCompilation=false`，避免复用的编译器进程读不到本次环境变量）：

   ```powershell
   $env:PCL_WRITE_SECRET = "1"
   $env:PCL_MS_CLIENT_ID = "<你的客户端 ID>"
   dotnet build PCL.Launcher/PCL.Launcher.csproj -c Release -p:UseSharedCompilation=false -t:Rebuild
   ```
3. **向 Mojang 提交应用审核**：在 <https://aka.ms/mce-reviewappid> 填写应用名称、客户端 ID 与用途说明。第三方应用调用 `api.minecraftservices.com` 前必须通过该审核，否则登录会在最后一步返回 `403 Invalid app registration`。审核通常需要数天到数周。
4. 分发 `PCL.Launcher/bin/Release` 下的产物。目标设备无需任何环境变量，直接正版登录（前提是审核已通过）。

说明：公共客户端的客户端 ID 会随二进制公开，这不是密钥；不要往应用里放 client secret。所有使用该构建的玩家都会共享你注册的这个 Azure 应用的配额。官方 PCL CE-AI 的客户端 ID 属于其发布方，本项目不会去提取或借用。

## 七、软件信息与更新

- 「设置 → 关于」产品名改为 PCL CE-AI，GitHub 主页、源代码、贡献者链接均指向 `https://github.com/aodianjun/PCL-CE-AI`；原作者的署名与许可证保持不变。
- 「设置 → 更新」改为 GitHub Releases 更新源：检查 `aodianjun/PCL-CE-AI` 的最新 Release，有更新时显示版本与更新日志，按钮直接打开 Release 页面。
- 启动时不再连接 PCL CE 官方更新源（MirrorChyan / pysio / naids），避免把 AI 构建自动替换成官方版本。原来的更新通道、自动更新、MirrorChyan CDK 设置卡片已隐藏。

## 八、会话管理与上下文压缩

- 对话区右上角只保留：新建对话、对话列表、压缩上下文、清空对话；原先右上角的设置按钮已移除，设置统一从启动页左下角进入。
- 会话保存在 `PCL/AiChat/conversations.json`，支持新建、切换、自动标题与重启恢复；最多保留 40 个会话。
- 手动「压缩上下文」会把较早消息交给模型生成摘要，只保留最近两条；当未压缩内容超过 16000 字符时也会在发送前自动压缩。
- 压缩后的摘要以隐藏系统消息参与后续请求，不会丢失上下文；界面会显示「上下文已压缩」提示。
- 对话标题栏实时显示上下文用量（估算 tokens / 上限 / 百分比），点击可查看消息数、字符数、估算 tokens、上限、压缩阈值与摘要状态。
- 「设置 → AI 接入」支持一键拉取 `/models`：选择模型后自动填写上下文上限与最大输出；若接口返回 `context_length`、`max_completion_tokens` 等字段则优先使用，否则按常见模型名回退。
- AI 工具执行、流式解析与压缩全部在线程池运行，不阻塞界面；单个工具 60 秒超时、模型首字节 60 秒超时、流式 90 秒无新数据自动中断并提示，避免“跑一半卡死”。
- 自动压缩阈值改为按模型上下文计算：达到上下文上限的 95%（至少 4000 tokens）才压缩，不再使用固定的 16000 字符；上下文详情里显示的是 token 阈值。
- 请求前会校验工具调用配对：没有完整 tool 结果的 assistant `tool_calls` 会被降级为普通文本，孤立的 tool 消息会被丢弃，避免接口返回 400。
- 忙碌时标题栏显示 3 个不同速度旋转的草方块；模型的 `reasoning_content` 思考过程与每次工具调用（名称、参数、结果）都会以气泡展示，执行过程完全可见。
- 思考与工具气泡在执行完成后自动折叠成一行标题，点击标题可展开/收起；推理模型在只输出思考时保持展开，正文开始后自动收起。
- AI 工具默认不切换页面：`open_page`、`search_resource`、`install_version_note` 新增 `navigate` 参数（默认 false），只有用户明确要求打开页面时才为 true；`launch_game` 与崩溃分析也不再强制跳转启动页。
- 「设置 → AI 接入」新增工作目录与允许访问文件夹。AI 工具新增 `get_work_directory`、`list_files`、`read_file`、`search_files`、`open_folder`，只能访问工作目录、允许文件夹、当前 Minecraft 目录与实例目录；越界路径直接拒绝。
- 输入框支持图片与文本文件附件：图片以 data URL 走多模态 `image_url`，文本文件并入上下文；附件随用户消息显示文件名，最多 6 个。
- AI 新增下载能力：`download_minecraft`（原版 / Fabric，后台安装并支持 `get_install_status` 查询进度）、`download_mod`（Modrinth 搜索并下载到当前实例 mods 目录）。Forge/NeoForge 仍引导到安装页。
- 支持 Ctrl+V 直接粘贴：剪贴板里是文件时按文件路径读取附件，是位图时转成 PNG 附件；没有附件时保持默认文本粘贴。

## 九、仓库、反馈与作者

- 「设置 → 关于」新增维护者卡片：aodianjun（本构建作者），头像取自 GitHub，按钮跳转 <https://github.com/aodianjun>；原有作者与社区署名保留。
- 「设置 → 关于」的 GitHub 主页、源代码、贡献者链接全部指向 `aodianjun/PCL-CE-AI`。
- 「设置 → 反馈」的 Issues 数据源与「前往反馈」按钮改为 `aodianjun/PCL-CE-AI/issues`；没有标签的 issue 默认归入「等待处理」，也支持按标签名自动归类。
- 其他残留的上游链接（语言贡献 PR、讨论、调试版下载页）同步指向该仓库。
- 关闭了 `BuildProvenance` 的官方构建校验（fork 不带官方 Sentry，按上游注释建议关闭）。

## 十、回滚

所有改动都在工作区，没有数据库迁移、没有改存档。回滚步骤：

```bash
cd PCL-CE
git checkout -- PCL.Core PCL.Launcher PCL.Localization
git clean -fd PCL.Launcher/Ai PCL.Launcher/Views/Pages/Setup
```

如果只想关闭 AI：

- 设置 → AI 接入 → 清空 API Key；
- 或在 `config.v1.yml` 中删除 `Ai*` 键（`AiBaseUrl` / `AiApiKey` / `AiModel` / `AiEnableTools` / `AiAutoAnalyzeCrash` / `AiOnboardingDone`）。

启动页与旧版差异仅在于界面组织，启动器原有功能（下载、实例、工具、设置）全部保留在设置页入口。
