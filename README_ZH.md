<div align="center">

# Codex Swap Account

**安全、纯本地的 Codex 桌面版账户切换器。**

在 Codex 窗口中直接显示的面板上切换多个 Codex 账户——支持托盘控制、全局快捷键、共享聊天记录、多显示器，以及切换失败时自动回滚。

[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white)](https://github.com/ZOONGG/codex-swap-account)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Build](https://github.com/ZOONGG/codex-swap-account/actions/workflows/windows-ci.yml/badge.svg)](https://github.com/ZOONGG/codex-swap-account/actions/workflows/windows-ci.yml)
[![Latest release](https://img.shields.io/github/v/release/ZOONGG/codex-swap-account?display_name=tag&sort=semver)](https://github.com/ZOONGG/codex-swap-account/releases/latest)
[![License](https://img.shields.io/github/license/ZOONGG/codex-swap-account)](LICENSE)
[![No telemetry](https://img.shields.io/badge/telemetry-none-2DD4A3)](#隐私与安全)

[下载最新版本](https://github.com/ZOONGG/codex-swap-account/releases/latest) ·
[反馈 Bug](https://github.com/ZOONGG/codex-swap-account/issues/new?template=bug_report.md) ·
[功能建议](https://github.com/ZOONGG/codex-swap-account/issues/new?template=feature_request.md)

<p align="center">
  <a href="README.md">English</a> ·
  <a href="README_RU.md">Русский</a> ·
  <strong>简体中文</strong>
</p>

</div>

<p align="center">
  <img src="docs/images/en/hero.jpg" alt="Codex Swap Account 面板显示在 Codex 桌面应用中" width="100%">
</p>

> [!IMPORTANT]
> Codex Swap Account 是**非官方社区工具**，与 OpenAI 没有隶属关系，也未获得其认可或赞助。  
> 请勿公开、上传或提交你的 `auth.json` 文件。

---

## 为什么需要它

使用多个 Codex 账户时，通常意味着反复退出登录、打开浏览器、完成认证，然后重新开始工作。

Codex Swap Account 把这一过程变成一次点击：

1. 在面板、托盘菜单或通过快捷键选择配置；
2. 应用安全地保存当前授权；
3. Codex 使用所选账户重启；
4. 真实的工作目录和聊天记录在所有账户之间保持共享。

应用安静地运行在系统托盘中，只有在检测到已验证的 Codex 窗口时才会显示面板。

## 主要特性

| | 特性 |
|---|---|
| **快速切换** | 从面板、托盘菜单或 `Ctrl + Alt + 1…9` 切换账户。 |
| **共享 Codex 历史** | 聊天、项目、会话历史和本地 Codex 数据库在所有账户中可用；只有授权发生变化。 |
| **紧凑与展开模式** | 可使用极简下拉菜单，也可使用一键分段式配置栏。 |
| **安全事务** | 切换前备份当前授权，切换失败时自动回滚。 |
| **多显示器支持** | 面板会跟随 Codex 跨显示器移动、缩放和 DPI 变化。 |
| **纯本地运行** | 无遥测、无云同步、无反向代理、不上传凭证。 |
| **托盘助手** | 从托盘启动 Codex、显示/隐藏面板、管理配置和修改设置。 |
| **多语言界面** | 支持英语、俄语和简体中文界面。 |
| **无需管理员权限** | 按用户运行，不需要 Windows 服务。 |
| **开源** | 使用 C#、.NET 8、WPF 构建，并配以注重安全的仓库流程。 |

## 界面截图

### 展开模式

一键切换配置。

<p align="center">
  <img src="docs/images/en/expanded-mode.jpg" alt="展开式账户切换器" width="820">
</p>

### 紧凑模式

更小的控件，通过下拉菜单访问配置和常用操作。

<p align="center">
  <img src="docs/images/en/compact-mode.jpg" alt="紧凑式账户切换器" width="760">
</p>

### 配置菜单

添加配置、打开配置管理、修改设置或隐藏切换器。

<p align="center">
  <img src="docs/images/en/profile-menu.jpg" alt="配置操作菜单" width="900">
</p>

### 外观设置

选择显示模式、位置、缩放和动画行为。

<p align="center">
  <img src="docs/images/en/settings-appearance.jpg" alt="外观设置" width="900">
</p>

### 配置管理

创建、整理和管理本地 Codex 配置。

<p align="center">
  <img src="docs/images/en/settings-profiles.jpg" alt="配置管理设置" width="900">
</p>

---

## 下载

前往[最新的 GitHub 发布页](https://github.com/ZOONGG/codex-swap-account/releases/latest)。

当前稳定版：**v1.0.0**（2026 年 9 月 20 日）。

推荐下载：

```text
CodexProfileOverlay-win-x64-portable.zip
```

独立可执行文件：

```text
CodexProfileOverlay.exe
```

完整性校验：

```text
SHA256SUMS.txt
```

Windows 安装程序提供英语、俄语和简体中文三种语言，默认以英语启动，可在语言对话框中选择其它语言。

### 系统要求

- Windows 10 或 Windows 11
- x64 系统
- Codex 桌面应用
- 添加新配置时，`PATH` 中需要有 Codex CLI

> [!NOTE]
> Windows SmartScreen 可能会提示该可执行文件未签名。对于独立分发的开源构建来说这是正常现象。请核对发布页中的 SHA-256 哈希，或自行审阅/构建源码。

## 快速开始

1. 下载 `CodexProfileOverlay-win-x64-portable.zip`。
2. 解压到一个普通文件夹。
3. 运行 `CodexProfileOverlay.exe`。
4. 打开 Codex。
5. 使用**添加配置**为你的账户授权。
6. 从面板、托盘菜单或快捷键选择配置。

应用会驻留在系统托盘。关闭设置窗口不会退出后台进程。

## 默认快捷键

| 操作 | 默认快捷键 |
|---|---|
| 显示或隐藏面板 | `Ctrl + Alt + C` |
| 切换到配置 1 | `Ctrl + Alt + 1` |
| 切换到配置 2 | `Ctrl + Alt + 2` |
| 切换到配置 3 | `Ctrl + Alt + 3` |
| 切换到配置 4–9 | `Ctrl + Alt + 4…9` |

快捷键可在**设置 → 快捷键**中修改或清除。当 Windows 无法注册某个快捷键时，应用会提示冲突。

## 切换原理

Codex 通常将本地状态保存在：

```text
%USERPROFILE%\.codex
```

Codex Swap Account 让该目录和本地状态对所有配置保持不变：

```text
%USERPROFILE%\.codex
├── chats / sessions  ← 所有配置共享
├── projects          ← 所有配置共享
├── settings
├── history           ← 所有配置共享
├── caches
├── databases
└── auth.json         ← 唯一被切换的文件
```

保存的账户配置单独存放：

```text
%USERPROFILE%\.codex-profiles
├── Work
│   └── auth.json
├── Personal
│   └── auth.json
└── Backup
    └── auth.json
```

切换过程中，应用会：

1. 阻止并发的切换操作；
2. 正常关闭 Codex；
3. 升级后的首次切换会把旧版按配置分离的会话合并到共享历史；
4. 把刚更新的 `auth.json` 保存回当前配置；
5. 备份当前授权；
6. 用所选配置原子替换 `auth.json`；
7. 只有替换成功后才会记录当前配置；
8. 正常启动 Codex；
9. 任何关键步骤失败时恢复先前的授权。

应用**不会**在每次切换账户时复制或切换整个 `.codex` 目录。这样重启更快，避免 SQLite 被占用导致的冲突，并让每个配置都能访问所有本地聊天。

### 回滚备份策略

每个新的回滚备份只包含 `previous-auth.json`、先前的当前配置元数据，以及一个小的哈希/时间戳清单。会话、rollout 文件、附件、设置、数据库、日志、缓存和项目数据永远不会被复制。授权文件超过 10 MB 或切换备份超过 25 MB 时，会在账户状态变更前被拒绝。

启动时和切换后，应用最多保留五个已完成的备份，删除超过七天的已完成备份，将总占用限制在 100 MB，并且只清理可明确识别的、超过 24 小时的废弃临时事务。进行中的事务永远不会被删除。**设置 → 高级**会显示备份占用情况，并提供打开文件夹、清理已完成备份、以及检查并显式清理旧版 `state-*` 备份（保留最近两个）的操作。

## 添加配置

**添加配置**流程会：

1. 在 `%USERPROFILE%\.codex-profiles` 下创建目录；
2. 在需要时创建配置内的 `config.toml`；
3. 以仅对该登录进程生效的 `CODEX_HOME` 运行 `codex login`；
4. 等待浏览器认证；
5. 检查配置授权文件是否存在；
6. 将配置加入切换器。

应用永远不会显示或记录凭证内容。

## 显示模式

### 自动

空间充足时使用展开模式，Codex 窗口较窄时使用紧凑模式。迟滞机制可避免调整窗口大小过程中模式反复切换。

### 展开

将所有配置显示为分段按钮，可一键切换。

### 紧凑

在更小的下拉菜单中显示当前配置，并可访问配置和常用操作。

面板可以放在 Codex 菜单之后、居中、右对齐，或按住 `Alt + 鼠标左键` 拖动到自定义位置。

## 配置状态与额度指示

设置 → **状态与额度**为每个配置提供可选的本地元数据：一个表情、一个短标签（24 个字符）、一条备注（120 个字符），以及可选的重置时间。这些数据保存在 `%LOCALAPPDATA%\CodexProfileOverlay\profile-status.json`，从不写入配置目录或凭证文件。手动标签和备注只保留在设置中。手动表情可以单独为面板启用，默认关闭。

自动数据源会以指向所选配置的 `CODEX_HOME` 启动一个独立的 `codex app-server`，并读取结构化的 `account/rateLimits/read` 响应。它不再启动交互式终端，也不再解析渲染出的 `/status` 菜单。**测试来源支持**会立即验证同一条数据链路。

只会缓存以下自动字段：额度窗口名称、剩余百分比、重置时间、采集时间、Codex CLI 版本，以及脱敏后的来源标识 `codex-cli-status`。原始响应、账户邮箱、会话 ID、模型、权限、目录、项目路径和授权内容都不会被保存。

当受支持的数据源可用时，面板仍然只显示表情：

- ⭐ 推荐配置；
- 🟢 达到或高于可配置的高阈值（默认 60%）；
- 🟡 达到或高于中阈值（默认 25%）；
- 🔴 低于中阈值或已耗尽；
- 未知、已禁用、不可用、失败或过期数据不显示表情。

有效容量取所有已报告窗口中最低的剩余百分比。例如 5 小时窗口剩余 99%、每周窗口剩余 0% 时显示 🔴。推荐至少需要两个新鲜的、可比较的配置，先取最低百分比最高者，再以平均容量和最近的重置时间作为决胜条件。完全相同时不产生推荐。未知用量永远不显示为零。禁用该功能或刷新失败时，缓存的自动快照会被保留。

关闭主开关后，不会启动任何后台 CLI 会话，不显示自动表情，并保留缓存快照。**立即刷新**会有意刷新所选配置，而普通面板只在配置名称旁显示一个小表情。详细的百分比和重置时间显示在设置中和指示器的悬浮提示里。

## 系统托盘

托盘菜单提供：

- 打开 Codex
- 显示或隐藏切换器
- 切换配置
- 打开设置
- 启用或禁用“随 Windows 启动”
- 退出应用

Codex 关闭或最小化时，托盘进程会继续运行，并在 Codex 再次可用时自动重新连接。

## 隐私与安全

Codex Swap Account 完全在本地机器上运行。

- 无遥测；
- 无统计分析；
- 无远程账户数据库；
- 不上传凭证；
- 不导出浏览器 Cookie；
- 无反向代理；
- 无 Windows 服务；
- 不需要管理员权限；
- 不解析、不打印、不记录 `auth.json` 内容。

本地应用数据保存在：

```text
%LOCALAPPDATA%\CodexProfileOverlay
```

其中可能包含非机密设置、日志、备份、配置显示元数据和已移除配置的备份。

完整安全策略见 [SECURITY.md](SECURITY.md)。

> [!WARNING]
> 请把每个 `auth.json` 都当作密码对待。不要分享、粘贴到 Issue 中，或提交到 Git。

## 安装方式

### 便携版

下载便携 ZIP，解压后运行：

```powershell
.\CodexProfileOverlay.exe
```

### 从源码按用户安装

```powershell
.\install.ps1 -Launch
```

安装时启用“随 Windows 启动”：

```powershell
.\install.ps1 -StartWithWindows -Launch
```

默认安装位置：

```text
%LOCALAPPDATA%\CodexProfileOverlay
```

## 卸载

在仓库目录中运行：

```powershell
.\uninstall.ps1
```

卸载程序会移除应用、快捷方式和启动项。它**不会**移除：

- `%USERPROFILE%\.codex`；
- `%USERPROFILE%\.codex-profiles`；
- Codex 聊天；
- Codex 项目；
- Codex 设置；
- Codex 历史；
- 账户授权文件。

## 从源码构建

### 前置条件

- Windows 10/11 x64
- PowerShell 5.1 或更高版本
- .NET 8 SDK

克隆仓库：

```powershell
git clone https://github.com/ZOONGG/codex-swap-account.git
cd codex-swap-account
```

运行测试：

```powershell
.\test.ps1
```

构建 Release：

```powershell
.\build.ps1
```

运行仓库安全检查：

```powershell
.\verify-repository-safety.ps1
```

发布自包含的 Windows 构建：

```powershell
.\publish.ps1
```

输出：

```text
artifacts\publish\CodexProfileOverlay.exe
artifacts\CodexProfileOverlay-win-x64-portable.zip
```

## 仓库结构

```text
.
├── .github/                         GitHub Actions 与模板
├── docs/                            架构、隐私与截图
├── src/
│   ├── CodexProfileOverlay/         WPF 桌面应用
│   └── CodexProfileOverlay.Core/    配置、设置、切换与安全逻辑
├── tests/
│   └── CodexProfileOverlay.Tests/   使用假凭证的单元测试
├── build.ps1
├── test.ps1
├── publish.ps1
├── install.ps1
├── uninstall.ps1
└── verify-repository-safety.ps1
```

## 常见问题

### 没有检测到 Codex

- 确认官方 Codex 桌面应用已打开。
- 重启 Codex Swap Account。
- 查看 `%LOCALAPPDATA%\CodexProfileOverlay\logs` 下的日志。
- 确认 Codex 能正常启动。

### 面板被隐藏了

- 按 `Ctrl + Alt + C`。
- 左键单击托盘图标。
- 使用托盘菜单中的**显示切换器**。
- 检查设置中的 **Codex 打开时自动显示**。

### 某个快捷键不生效

可能其他应用已占用该快捷键。打开**设置 → 快捷键**，换一个组合。

### 某个配置无法发送请求

已保存的登录可能已过期或被撤销。移除并重新添加该本地配置，或通过“添加配置”流程重新认证。

### Windows SmartScreen 阻止了应用

只有在从官方仓库发布页下载并核对 SHA-256 哈希之后，才选择**更多信息 → 仍要运行**。

## 已知限制

- 仅支持 Windows x64。
- 切换需要重启 Codex，以便加载所选授权。
- 添加配置需要 Codex CLI。
- 当前安装程序是按用户的 PowerShell 安装程序，而不是 MSI/MSIX。
- 应用依赖普通 Windows 用户进程可获取的窗口/进程元数据。

## 参与贡献

欢迎贡献。

提交 Pull Request 之前：

1. 阅读 [CONTRIBUTING.md](CONTRIBUTING.md)；
2. 不要让凭证文件和本地用户数据进入 Git；
3. 运行 `.\test.ps1`；
4. 运行 `.\build.ps1`；
5. 运行 `.\verify-repository-safety.ps1`；
6. 在相关时说明手动 UI 验证结果。

可复现的 Bug 和聚焦的功能建议请使用 [GitHub Issues](https://github.com/ZOONGG/codex-swap-account/issues)。

## 许可证

基于 [MIT License](LICENSE) 分发。

## 免责声明

Codex Swap Account 是非官方社区项目，与 OpenAI 没有隶属关系，也未获得其认可或赞助。

OpenAI 和 Codex 是其各自所有者的商标。本项目提供本地辅助界面，不会修改官方 Codex 安装。
