# <img src="Assets/app-icon.png" width="32" height="32" align="absmiddle" alt="MioHardwareMonitor" /> MioHardwareMonitor · 星澪硬件监控 `v1.6.0`

专为副屏、机箱长条屏与桌面角落打造的 Windows 旗舰级硬件与系统遥测仪表盘，基于 **.NET 10 (WPF)** + **WebView2**（纯原生 HTML/CSS/ES Modules，零 Node.js/npm 构建依赖）+ **LibreHardwareMonitorLib** + **Windows 11 Direct Display (`Windows.Devices.Display.Core` + Direct3D 11)**。

采用**监控画面与管理面板彻底分离**的双窗口架构：监控窗口不占任务栏、只留纯粹数据；管理面板与系统托盘右键菜单集中控制一切参数，并针对 Windows 11 脱离桌面副屏、竖装/倒装机箱屏与笔记本核显/独显异构多屏做了深度底层优化。

---

## 核心特性一览

### 1. 硬件独占直出（Hardware Exclusive Direct Display）

突破传统桌面窗口在副屏上易受切虚拟桌面、`Win+D`、全屏游戏与鼠标误闯干扰的痛点，提供三种显示模式：

- **Windows 11 硬件独占直出（Direct Display）**：
  - 直接接入 `Windows.Devices.Display.Core` 与零依赖 `D3D11DirectPresenter`（Direct3D 11 + `IDisplayDeviceInterop`），配合 Windows 11「设置 → 显示 → 高级显示器设置 → **从桌面删除显示器**」开关，绕过 DWM 桌面窗口管理器将画面双缓冲直接扫描输出至硬件 Primary Surface。
  - **0° / 90° / 180° / 270° 实时画面旋转**：支持倒装或竖装副屏，`90° / 270°` 采用 **32×32 Cache-Tiled 缓存分块旋转内核**并自动重排离屏渲染尺寸；自动适配 BGRA/RGBA 面板像素格式（支持 `Vector128` SIMD 通道转换）。
  - **SIMD 同帧跳过优化**：通过 `ReadOnlySpan<byte>.SequenceEqual` 对比相邻帧，静止画面自动跳过上传，零无效 GPU 传输；支持 `5 / 10 / 15 / 30 FPS` 四档扫描帧率。
  - **实时管线诊断**：管理面板实时显示硬件独占会话的**目标/实际 FPS**（2 秒滑动窗口）、**最近/平均上传耗时（ms）**、**已呈现 / 已跳过 / 失败帧计数**及路径健康状态。
- **桌面整屏占用 & 桌面固定窗口**：
  - 支持 9 方位快速停靠、精确像素坐标（X/Y/W/H/边距）、Win32 级位置与尺寸锁定，默认开启**跨虚拟桌面常驻**（`WS_EX_TOOLWINDOW`）与**置顶守护**（`Win+D` 不隐藏、切桌面不消失）。
  - **跨显卡渲染兼容**：针对笔记本核显 + 独显异构双屏，提供「跨显卡安全模式（默认）/ 禁用 DirectComposition / 原生 GPU 硬件加速」三档切换，并支持跨卡拖拽自动重建。

---

### 2. 多页面仪表盘：硬件总览 ⇄ 进程与线程树

单套 HUD 内置两大专业页面，按 `Ctrl + Tab` 或 `Alt + 1 / Alt + 2` 毫秒切换；停留在「硬件总览」页时，进程树采集完全休眠（**零后台线程与零额外 CPU 开销**）：

- **Process Hacker 风格进程与线程树（`ProcessTreeService`）**：
  - 基于原生 Win32 API（Toolhelp32、`GetProcessTimes`、`GetProcessMemoryInfo`、`GetProcessIoCounters`、`GetProcessHandleCount`、`QueryFullProcessImageNameW`）构建**树状父子层级**与**扁平排行**双视图。
  - 支持按 **CPU / 内存 / 磁盘 I/O / 线程数 / 句柄数 / PID / 名称** 7 种维度排序，支持进程名 / PID / 路径实时过滤。
  - **语义化生命周期与类别高亮**：自动标示**新建进程**（3 秒高亮）、**刚退出进程幽灵行**（2 秒残留提示），并区分**系统 / 服务 / 管理员提权 / 自身**进程类别。
  - **实时线程入口符号解析**：调用 `NtQueryInformationThread (ThreadQuerySetWin32StartAddress)` 结合进程模块基址表二分查找，将线程起始地址精确解析为 **`module.dll+0xOFFSET`**；逐线程展示 TID、**用户态 / 内核态 CPU 拆分占用**、优先级、累计 CPU 时间与运行状态（`Running / Active / Wait`）。
- **双模式交互体验**：
  - **桌面窗口模式**：可直接在监控画面中点击切页、搜索筛选、全展/全收树节点、点选任意进程检视实时线程树，并支持「打开目录 / 复制路径 / 结束进程」（内置关键系统进程保护，防止误杀）。
  - **硬件独占模式（无鼠标交互）**：监控画面自动切换为无控件的纯净只读状态栏，支持通过**管理面板顶栏 / 进程树远程控制卡片 / 系统托盘菜单**远程翻页、切换视图与排序，并默认启用 **Auto-Follow Top CPU（自动追踪最高负载进程）** 或远程锁定指定 PID 剖析线程。

---

### 3. HWiNFO 级全栈硬件与网络遥测

- **CPU 监控（单路与多路）**：
  - 默认启用**紧凑核心格（Compact Per-Core Grid）**，在紧凑高度内清晰呈现 8–64+ 逻辑处理器实时负载与峰值刻度，大幅节省副屏垂直空间；可一键切换**完整多核曲线**（Windows 任务管理器风格 60 秒实时曲线 + 悬停查看单核读数）或**仅总体曲线**。
  - 自动检测双路 / 多路物理 CPU，支持分路独立展示每颗处理器的核心、频率、温度与功耗，或合并总览 / 单路聚焦。
- **GPU 监控**：核心占用率、核心 / 显存频率、核心温度 / **热点温度（Hotspot）** / **显存结温（MemTemp）**、功耗、风扇转速、专用显存（VRAM）容量与进度条，以及辅助核显/多卡摘要。
- **内存与存储**：物理内存容量、内存频率（MT/s）、已用 / 可用 / 已提交内存（Commit）；磁盘实时读写速率、各分区可用空间与硬盘温度。
- **散热与系统传感器**：主板型号识别、主板 / **VRM 供电温度**、多路**风扇转速（RPM / %）**、**NVMe / SSD SMART 温度与剩余寿命健康度**、笔记本**电池电量 / 充放电功率 / 剩余时间 / 损耗率**，以及**平台估算总功耗（W）**。
- **会话 Min / Avg / Max 峰值追踪**：自动记录本轮会话的 CPU/GPU 占用、温度、热点、功耗、频率、内存占用与网络上下行速率峰值，支持随时一键重置。
- **可配置温控与负载告警**：内置「静音办公 / 游戏常规 / 超频严苛」三套阈值预设，可分别设定 CPU / GPU 警告与危险温度、内存 / 磁盘占比阈值及通知冷却周期（15–600 秒），配备防抖机制、监控窗顶栏告警徽章与系统托盘通知。
- **实时网络延迟 + 双出口 IPv4 纯净度**：
  - **ICMP Ping / Jitter / 丢包监测**：基于 20 样本滑动窗口实时计算往返延迟、抖动与丢包率，内置 `223.5.5.5`（阿里）/ `119.29.29.29`（腾讯）/ `1.1.1.1`（Cloudflare）快捷预设，支持自定义目标主机。
  - **国内站 / 国外站双出口 IPv4 检测**：严格过滤 IPv6，分别显示归属地、ASN / ISP、IP 类型与**纯净度 / 欺诈风险评分（Purity / Fraud Score）**，托盘菜单悬停可看详情、点击直接复制。
  - **IP 隐私遮罩**：一键隐藏公网 IPv4 中间段，直播、录屏与截图时防泄露。
- **CSV 遥测会话录制**：一键将 **31 列**完整遥测数据（含时间戳、CPU/GPU/热点/显存温、内存、网速、Ping/Jitter/丢包、磁盘读写、平台功耗、最高风扇转速、告警数与自身开销）以 UTF-8 (BOM) 格式缓冲写入 `%USERPROFILE%\Documents\MioHardwareMonitor\Logs\MioTelemetry_*.csv`，可直接用 Excel 或 Python 绘图分析。
- **实时自身开销诊断 & 低占用分级调度**：实时展示**宿主进程 CPU% / WebView2 子进程树 CPU% / 总内存 MB / 进程数 / 单次采集耗时 ms**；支持三档传感器性能模式（节能 6s / 均衡 3s / 实时 1s）与 4 档采样周期（`0.5s / 1.0s / 1.5s / 3.0s`），默认配置下极致轻盈。

---

### 4. 旗舰视觉系统与专业工作流

- **双画面风格**：
  - **精密仪器（Precision Instrument，默认）**：240° 高 DPI 向量圆弧仪表盘（CPU / GPU / RAM）+ 25 道外环精密刻度，零 `shadowBlur` 开销确保安全模式与硬件独占直出同样锐利；全画布采用纯事件驱动重绘（零 `requestAnimationFrame` 空转）。
  - **星澪扁平（Flat）**：克制极简的经典数据卡片风，与精密仪器风格随时热切换。
- **浅色 / 深色主题 + 6 套配色皮肤 + OLED 护屏**：
  - 内置 6 套配色皮肤：**星澪标准蓝**、**午夜靛蓝**、**石墨橙**、**极光青**、**樱粉**、**琥珀金**，均提供浅色 / 深色完整 Token，实时同步至监控窗口、WPF 管理面板与托盘右键菜单。
  - 支持深色主题下的 **OLED 纯黑模式（`#000000` 背景）** 与 **OLED 防烧屏微位移（Pixel Shift）**。
- **可视化拖拽自定义排版编辑器**：
  - 支持自定义网格列数（4 / 6 / 8 / 12）、行数（4 / 6 / 8 / 10 / 12）与间距（4–20 px），在画布中直接拖拽调整模块位置、拉动右下角手柄缩放宽高，并可单独开关 7 个核心硬件模块（CPU / GPU / 内存 / 网络 / 存储 / 散热 / 进程）。
  - 内置「**纯硬件大屏 / 竖屏副屏 / 机箱长条屏**」布局模板、冲突重叠检测与「一键防重叠整理」，修改实时同步监控窗口。
- **`.miohud.json` 配置方案导入导出**：
  - 支持将皮肤、OLED 纯黑、画面风格、自定义网格排版与温控告警规则打包导出为带版本校验的 `.miohud.json` 配置文件；跨机分享时可取消勾选本机显示器/窗口坐标，导入前支持方案摘要预览，并兼容直接导入 `settings.json`。
- **开箱即用的日常体验**：
  - 支持开机静默自启（`--autostart`）自动恢复上次退出前的显示模式、屏幕与排版。
  - 系统托盘图标悬停实时显示 CPU / GPU 占用与温度及内存占用；右键菜单覆盖常用全部开关。
  - 内置 GitHub Releases 自动检查更新、流式下载、SHA-256 校验与一键安装重启（支持便携版与安装版）。

---

## 下载与安装

前往 [Releases](https://github.com/Klop233/MioHardwareMonitor/releases) 下载对应架构的发行包（推送 `main` 后由 GitHub Actions 自动构建并附带 `SHA256SUMS.txt`）：

| 架构 | 便携版（解压即用） | 安装包（支持桌面快捷方式与开机自启） |
|---|---|---|
| Windows x64 | `SideScreenHUD-v*-win-x64.zip` | `SideScreenHUD-v*-win-x64-Setup.exe` |
| Windows ARM64 | `SideScreenHUD-v*-win-arm64.zip` | `SideScreenHUD-v*-win-arm64-Setup.exe` |

- 已打包自包含 .NET 10 运行时，**无需单独安装 .NET Desktop Runtime**。
- 仅需系统具备 **Microsoft Edge WebView2 Runtime**（Windows 10/11 默认自带）。
- 硬件独占直出（Direct Display）需 **Windows 11** 及支持「从桌面删除显示器」的显卡驱动。

> **提示**：如需读取 CPU 封装温度、主板 VRM 温度、风扇转速与完整进程线程符号信息，可在管理面板顶部横幅或托盘菜单中点击「**以管理员身份重启**」。

---

## 快捷键

监控窗口聚焦时可用：

| 快捷键 | 功能 |
|---|---|
| `F11` | 在「占用整块屏幕」与「固定窗口」之间切换 |
| `Esc` | 从整屏模式退回固定窗口 |
| `Ctrl + Tab` | 在「硬件监控总览」与「进程与线程树」之间快速切页 |
| `Alt + 1` | 切换到「硬件监控总览」页 |
| `Alt + 2` | 切换到「进程与线程树」页 |
| `Ctrl + ,` | 打开管理面板（设置窗口） |
| `Ctrl + 1~9` | 将监控窗口投送到第 1~9 块显示器 |

---

## 从源码编译与一键打包

**环境要求**：Windows 10/11、[.NET 10 SDK](https://dotnet.microsoft.com/)（前端无 Node.js / npm 依赖，修改 `web/` 下文件即可直接生效）。

### 本地开发与运行

```powershell
# 编译并启动 Release（或直接双击根目录 start.bat）
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.22621.0\SideScreenHUD.exe
```

> 若本地已运行 `SideScreenHUD.exe` 导致默认输出目录被占用，可指定独立输出目录安全验证编译：
> ```powershell
> dotnet build -c Debug -o "$env:TEMP\mio_build"
> ```

### 一键打包发行版（便携 ZIP + Inno Setup 安装包）

双击根目录 **`build.bat`**（或在终端执行），自动构建 `win-x64` 与 `win-arm64` 的自包含单文件便携 ZIP、Inno Setup 6 安装包（`*-Setup.exe`）与 `SHA256SUMS.txt`，产物输出至 `dist\release\`：

```powershell
# 一键全架构打包（若未安装 Inno Setup 6，脚本会自动尝试通过 winget 安装）
.\build.bat

# 或直接调用 PowerShell 脚本并按需传参（例如仅构建 x64 或跳过安装包）
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -Archs @("win-x64")
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 -SkipInstaller
```

---

## 架构与依赖

| 模块 / 依赖 | 作用 |
|---|---|
| **.NET 10 WPF + `Windows.Devices.Display.Core` + D3D11** | 宿主窗口、Win32 位置锁与硬件独占扫描直出（`D3D11DirectPresenter`） |
| **[Microsoft Edge WebView2](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)** | 仪表盘前端渲染（`web/` 下零构建原生 HTML/CSS/ES Modules + 高 DPI Canvas） |
| **[LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)** | CPU / GPU / 主板 VRM / 风扇 / NVMe SMART / 电池底层硬件传感器采集 |
| **[Inno Setup 6](https://jrsoftware.org/isinfo.php)** | Windows `x64` / `arm64` 安装包打包（`installer/SideScreenHUD.iss`） |
