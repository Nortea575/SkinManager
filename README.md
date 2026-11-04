<p align="center">
  <img src="assets/icons/SkinManager.svg" width="88" alt="SkinManager 图标" />
</p>

<h1 align="center">SkinManager</h1>

<p align="center">为 Windows 上的 Codex 桌面版制作、预览和管理皮肤。</p>

<p align="center">
  <a href="#快速开始">下载与使用</a> ·
  <a href="https://blog.ancimen.top/?page_id=39">皮肤市场</a> ·
  <a href="CHANGELOG.md">更新日志</a>
</p>

SkinManager 让你为 Codex 桌面版更换背景、颜色与透明度，并在应用前预览效果。你可以从皮肤市场下载现成主题，也可以用可视化制作器创建自己的皮肤。

## 效果展示

![SkinManager 主界面：皮肤库、效果预览和连接控制](docs/images/manager.png)

皮肤库、预览和连接控制集中在一个窗口中，选择皮肤即可查看效果。

<details>
<summary>查看浅色与深色皮肤示例</summary>

| Ayanami · 浅色 | Saya · 深色 |
| --- | --- |
| ![Ayanami 浅色皮肤预览](docs/images/ayanami-light.png) | ![Saya 深色皮肤预览](docs/images/saya-dark.png) |
| 蓝白配色，默认面板透明度 75% | 墨蓝与浅绿配色，默认面板透明度 60% |

</details>

## 主要功能

- **皮肤管理**：搜索皮肤，导入文件夹或 ZIP，导出备份并删除不再使用的皮肤。
- **可视化制作**：编辑背景、颜色、组件样式和浅深色变体，支持撤销与重做。
- **效果预览**：切换首页与对话场景、浅深色外观和侧栏，对照原生界面查看变化。
- **透明度调整**：直接输入 0–100 的数值，也可跟随皮肤默认设置。
- **皮肤市场**：搜索、筛选、查看详情和预览，一键下载并安装到皮肤库。
- **连接与恢复**：选择 Codex 实例应用皮肤，随时恢复原生外观。
- **软件更新**：自动检查和下载新版本，提示后由你点击安装。

### 可视化制作器

编辑属性时即时查看草稿效果，完成后保存或导出为独立皮肤包。

![可视化制作器：左侧编辑皮肤属性，右侧预览效果](docs/images/editor.png)

### 皮肤市场

点击卡片大图进入皮肤效果预览，展开详情可查看作者与版权声明。点击“下载并安装”后保存到皮肤库，再自行选择应用。

![皮肤市场：搜索、明暗筛选与皮肤大图卡片](docs/images/market.png)

市场提供 Ayanami、Saya、Claude、DeepSeek 和 GPT 等官方皮肤。皮肤独立下载，安装包不内置皮肤。

## 快速开始

### 运行要求

- Windows x64。
- 通过 Microsoft Store 安装的 Codex 桌面版。
- Microsoft Edge WebView2 Runtime，用于界面预览。

安装包已包含所需的 .NET 运行时，无需另装 .NET SDK。

当前安装包未进行代码签名。

### 安装与应用皮肤

1. 在本仓库的 **Releases** 中下载 `SkinManager-Setup-1.0.0.exe` 并安装。
2. 打开“皮肤市场”下载皮肤，或通过“导入皮肤”导入已有文件夹或 ZIP。
3. 从 Codex 托盘菜单完全退出 Codex，再点击 SkinManager 的“启动调试版”。
4. 选择皮肤，点击“连接并应用 / 更新”。
5. 需要恢复原生外观时，点击“恢复原生 / 断开”。

如果已有开启调试连接的 Codex，可在“管理实例”中选择对应目标。默认调试端口为 `9335`。

## 使用说明

### 皮肤保存与备份

皮肤保存在程序同路径的 `skin` 文件夹中，每款皮肤使用独立目录，包含 `skin.json` 和背景图片等资源。导入会复制完整皮肤包，导入后不依赖原文件所在位置；导出 ZIP 可用于备份和分享。

升级安装会保留皮肤库，卸载时可选择保留或删除。

### 透明度与预览

面板透明度可输入 `0–100`：`0` 为不透明，`100` 为完全透明。启用“跟随主题”时使用皮肤的默认值。

预览使用静态示例界面与示例内容。实际效果可能随 Codex 版本和页面结构变化；发现异常时，请在反馈中注明所用版本和皮肤。无需预览时，可点击“关闭预览”释放相应资源。

### 连接与更新

关闭 Codex 窗口后，程序可能仍驻留托盘。启动调试版前可通过“管理实例”检查运行状态，并按提示退出对应实例。

皮肤连接由独立后台维持，关闭管理器后可继续使用已应用的皮肤。需要移除皮肤时，请使用“恢复原生 / 断开”。

在“版本与更新”中查看更新状态。下载完成不会立即安装，点击“安装更新”后才会开始；安装后需重新应用皮肤。

## 从源代码构建

在 Windows 安装 .NET 9 SDK 后，从仓库根目录运行：

```powershell
dotnet restore .\SkinManager.sln
dotnet build .\SkinManager.sln -c Release --no-restore
```

构建安装包时使用 [build/Build-Installer.ps1](build/Build-Installer.ps1)。

## 反馈与贡献

欢迎通过本仓库的 **Issues** 反馈问题和提出建议，或通过 **Pull Request** 贡献改进。

反馈问题时，请提供 SkinManager 版本、Codex 版本、皮肤名称、复现步骤和相关截图，便于定位。

## 作者与版权

作者：**Ancimen**。

官方皮肤由 Ancimen 发布，**保留所有权利**。第三方皮肤及素材遵循各自的授权声明。

SkinManager 是独立项目，并非 OpenAI 官方产品。预览界面使用的 Codex CSS 与字体归各自权利方所有；安装器简体中文翻译来自 [Inno Setup Chinese Simplified Translation](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation)。
