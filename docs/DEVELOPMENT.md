# MicSwitcher 🎤

Windows 麦克风切换工具（个人自用 + 学习），C# + WPF 实现。

常驻系统托盘，支持**手动切换 / 锁定 / 自动按声音切换**麦克风，以及静音、轮流切换、开机自启。

## 功能

| 功能 | 说明 |
|---|---|
| ✋ 手动切换 | 托盘右键 / 设置窗口里点选任意麦克风设为默认 |
| 🔒 锁定模式 | 锁定当前默认麦，Windows 或别的程序改了会被自动改回 |
| 🤖 自动模式 | 两种策略二选一：按声音大小切换 / 按新设备接入切换 |
| 🔄 轮流切换 | 在多个麦克风间轮流设为默认 |
| 🎤 静音 | 全局静音 / 取消静音当前默认麦 |
| 🚀 开机自启 | 写当前用户 `Run` 键，启动后最小化进托盘 |
| 🖥 单实例 | 重复启动会**直接把已运行的窗口唤到前台**（先发唤醒事件，失败再找窗口置前），不弹「已在运行」的提示 |
| ⌨️ 全局热键 | 默认 `Ctrl+Alt+M` 静音、`Ctrl+Alt+N` 下一个、`Ctrl+Alt+P` 上一个；**可录制自定义**，也可从预设里选 |
| 🔊 通信角色同步 | 切默认麦时同时改「通信设备」角色，Teams / 会议软件跟着变 |

## 界面结构

分成**主窗口**和**详细设置**两个窗口，主窗口只放日常操作。

### 主窗口（日常用）

```
┌──────────────────────────────────────────────────────────────┐
│ 🎤 MicSwitcher                                                │
├──────────────┬───────────────────────────────────────────────┤
│ 模式          │ 麦克风设备                                    │
│  ✋ 手动  [●] │ ┌─────────────────────────────────┐  ▲ ▼ ⠿  │
│  🤖 自动  [○] │ │ 麦克风名称                       │          │
│ ──────────── │ │ [默认] 🔇已静音                  │          │
│ 自动策略      │ └─────────────────────────────────┘          │
│  ○ 按声音大小 │ ┌─────────────────────────────────┐  ▲ ▼ ⠿  │
│  ○ 按新设备   │ │ 另一个麦克风                     │          │
│ ──────────── │ └─────────────────────────────────┘          │
│  🔒 锁定当前麦│                                               │
├──────────────┴───────────────────────────────────────────────┤
│ ⚙ 设置                                            刷新        │
└──────────────────────────────────────────────────────────────┘
```

- 左侧两个拨动开关**互斥**：开手动自动关自动，反之亦然；两个都不允许全关（会自动兜底回手动）。
- 设备卡片右侧：`▲ ▼` 上下移动一位，**两张卡片会像黑板上下两半一样对推交换**（被移动的滑向目标位、另一张反向让位，210ms 缓动后才落定）。
- **长按拖拽排序**：在卡片上按住约 0.2 秒，卡片会「浮起来」——用 `Popup` 承载的幽灵卡片，
  圆角裁剪 + 柔和投影、跟手无延迟，**可以拖出窗口也不会被裁切**；
  拖到别的卡片缝隙时会出现一条带绿色光晕的插入指示线（换位置时轻轻弹一下），
  松手时幽灵卡片先滑入槽位，**这时才真正写入优先级**；按 Esc 可取消。
- 顺序存进 `settings.json` 的 `DevicePriority`。
- 双击卡片 = 设为默认；底部按钮对选中的设备生效。
- 左下角 **⚙ 设置** 打开详细设置窗口。

### 详细设置（低频用）

**左侧分类导航 + 右侧可折叠分组**（点左边某一类，右边只展开那一段）：

| 分类 | 内容 |
|---|---|
| 启动 | 开机自启动、启动时最小化到托盘、**开机自启延迟启动**（等音频服务就绪，可调 1–60 秒） |
| 窗口与托盘 | 关闭时最小化、显示托盘图标、**托盘图标反映静音状态**、同步通信设备角色 |
| 通知 | 自动切换时弹提示 |
| 自动模式 | **静音阈值 / 防抖次数 / 采样间隔** 三个滑杆，改完立即生效 |
| 快捷键 | 总开关 + **每个动作独立开关** + 下拉预设 / 录制 + 冲突检测 |
| 设备管理 | 给设备起**别名**、勾选**排除**（排除的设备不参与自动切换、不进托盘菜单） |
| 关于 | 版本信息 |

### 自动模式有两种策略（二选一）

| 策略 | 行为 |
|---|---|
| 🎚 按声音大小 | 周期性采样所有麦克风电平，谁在说话就切到谁（带 3 次防抖，静音不动） |
| 🔌 按新设备 | 监听新的录音设备接入，插上麦克风就切过去 |

### 设置项一览

| 分组 | 开关 | 作用 | 存哪 |
|---|---|---|---|
| 启动 | 开机自启动 | 登录 Windows 后自动运行 | 注册表 `Run` 键 |
| 启动 | 启动时最小化到托盘 | 启动后不弹设置窗口，直接进托盘 | `settings.json` |
| 窗口与托盘 | 关闭时最小化到托盘 | 点 ✕ 时缩到托盘而非退出 | `settings.json` |
| 窗口与托盘 | 显示托盘图标 | 关掉后托盘不留图标（再次启动程序可唤出设置窗口） | `settings.json` |
| 窗口与托盘 | 同步通信设备角色 | 走「通信」角色的应用（会议软件）是否跟随 | `settings.json` |
| 通知与快捷键 | 自动切换时弹提示 | 自动切麦时弹气泡，关掉则静默切换 | `settings.json` |
| 通知与快捷键 | 启用全局热键 | 见上表三个热键 | `settings.json` |

配置存在 exe 同目录的 `settings.json`；除「开机自启动」写注册表外，其余都是本地偏好。
`DevicePriority` 字段保存设备优先级顺序，托盘菜单和设置窗口都按它排序。

### 热键可自定义

详细设置里三个动作（静音 / 下一个 / 上一个）都能改：

- **录制**：点「录制」后直接按下想要的组合键，立刻生效并写进 `settings.json`
- **预设**：下拉里内置 `Ctrl+Alt+M`、`Ctrl+Alt+N`、`Ctrl+Alt+P`、`Ctrl+Shift+M`、`Ctrl+Alt+1/2`、`Ctrl+Alt+F9/F10`
- 必须含至少一个修饰键（Ctrl / Alt / Shift / Win）；三个动作不能重复
- **冲突检测**：设热键前会用一个独立隐藏窗口「试注册」一次（注册后立刻注销，不影响现有热键）。
  若返回 `ERROR_HOTKEY_ALREADY_REGISTERED(1409)` 说明被系统或别的程序占用，
  会弹窗说明并回退到原来的组合键；面板底部也会汇总显示「哪些没注册上 / 哪些重复」
- 程序启动时若某个热键注册失败，托盘会弹一次气泡提示去设置里更换

### 托盘右键菜单（精简版）

```
🎤 <当前麦克风>            （只读）
────────────────
切换麦克风          ▸  <设备列表，单选打勾>
下一个麦克风
🎤 静音当前麦 / 🔇 取消静音
────────────────
模式                ▸  手动 / 自动·按声音大小 / 自动·按新设备 / 锁定当前麦
🚀 开机自启              （勾选）
────────────────
⚙ 设置…
❌ 退出
```

## 环境要求（本机特有，重要）

- **.NET 8 SDK 装在 `D:\TOOLS\dotnet-sdk`**（不是系统默认 `C:\Program Files\dotnet`，那里面只有运行时、没 SDK）。
- 本机 `C:\Users\qiuke\` 下的 `.dotnet`、`AppData\Roaming\NuGet` 目录**无法写入**，
  所以构建时必须把 `DOTNET_CLI_HOME`、`NUGET_PACKAGES`、`APPDATA` 等重定向到 `D:\TOOLS\dotnet-home`。
- 这些环境变量已经封装进 `build.cmd` / `run.cmd` / `test.cmd`，**直接跑脚本即可，不要裸敲 `dotnet build`**。

## 快速开始

```bat
build.cmd          :: 编译整个解决方案（Debug）
run.cmd            :: 编译并启动（托盘常驻，右键托盘操作）
test.cmd           :: 跑单元/集成测试
build.cmd Release  :: 编译 Release 版
```

```powershell
.\部署到桌面.ps1    :: ⭐ 推荐：复制到桌面 + 修正完整性级别 + 启动（托盘图标才会显示）
```

编译产物：`MicSwitcher.App\bin\Debug\net8.0-windows\MicSwitcher.App.exe`

首次运行后，任务栏右下角会多一个图标，右键即可切换麦克风、切换模式、开关自启。

## ⚠️ 必须在「普通目录」运行（托盘图标不显示的唯一原因）

**不要直接从 `D:\works-dsh` 里双击 exe。** 这个工作区被打了 **Low 完整性标签**
（`Mandatory Label\Low Mandatory Level`），从里面启动的进程会继承 Low 权限，导致：

| 现象 | 原因 |
|---|---|
| 托盘图标不显示 | Low 进程向 Medium 的任务栏注册图标 → `Shell_NotifyIcon` 返回 `ERROR_ACCESS_DENIED(5)` |
| 开机自启失败（`Requested registry access is not allowed`）| 同样是 UIPI 拦截 |

**诊断办法**：`icacls "D:\works-dsh"`，若看到 `Mandatory Label\Low Mandatory Level` 即中招。

**正确做法**：把编译产物复制到普通目录（桌面 / `D:\MicSwitcher` 等）再运行。
仓库根目录的 `部署到桌面.ps1` 会自动完成「复制到桌面 + `icacls /setintegritylevel Medium /T` + 启动」：

```powershell
# 右键「使用 PowerShell 运行」，或在该目录下执行：
powershell -ExecutionPolicy Bypass -File .\部署到桌面.ps1
```

手动等价命令（PowerShell 里 `%USERPROFILE%` 不生效，要用 `$env:USERPROFILE`）：

```powershell
robocopy "D:\works-dsh\works\working\MicSwitcher\MicSwitcher.App\bin\Debug\net8.0-windows" "$env:USERPROFILE\Desktop\MicSwitcher" /E
icacls "$env:USERPROFILE\Desktop\MicSwitcher" /setintegritylevel Medium /T /C
Start-Process "$env:USERPROFILE\Desktop\MicSwitcher\MicSwitcher.App.exe"
```

`Shell_NotifyIcon` 的完整性级别规则（Windows UIPI）：**低完整性进程不能向中/高完整性进程发消息**，
而任务栏 `explorer.exe` 固定是 Medium。所以托盘程序必须以 Medium 及以上运行。

### 托盘图标必须是真实 .ico 文件

即使 `Shell_NotifyIcon` 调用成功（托盘里出现一个空槽位），**运行时用 GDI+ 现画的 HICON 也可能显示为空白**。
本项目改用随程序发布的真实多尺寸图标 `MicSwitcher.App/Assets/app.ico`（16/24/32/48/64/128，BMP 格式），
构建时自动复制到输出目录的 `Assets\app.ico`，运行时用 `new Icon(path)` 从磁盘加载，
并用静态字段持有引用防止句柄被 GC 回收。Cherry Studio 等成熟项目也是「发布真实 .ico」这一做法。


## 架构

```
MicSwitcher/
├── MicSwitcher.Core/            # 核心逻辑（无 UI，面向 .NET Standard 可测）
│   ├── Audio/
│   │   ├── Native/PolicyConfig.cs   # 非公开 COM 接口 IPolicyConfig 封装（设默认设备）
│   │   ├── AudioDeviceInfo.cs        # 设备快照模型
│   │   └── AudioDeviceService.cs     # 枚举/静音/切默认/读电平（基于 NAudio）
│   ├── AutoSwitch/LevelMonitor.cs    # 自动策略①：按电平选最佳麦
│   ├── AutoSwitch/DeviceWatcher.cs   # 自动策略②：监听新麦克风接入
│   ├── Locking/DefaultGuard.cs       # 锁定守护（IMMNotificationClient + 轮询兜底）
│   └── Startup/AutoStart.cs          # 开机自启（Run 注册表键）
├── MicSwitcher.App/             # WPF 外壳（托盘 + 设置窗口）
│   ├── App.xaml(.cs)             # 托盘 + 单实例 + 热键 + 右键菜单
│   ├── AppServices.cs            # 服务容器 + 三模式互斥
│   └── SettingsWindow.xaml(.cs)  # 设置窗口
└── MicSwitcher.Tests/           # xunit 测试
```

## 核心技术 API

| 做什么 | 用什么 |
|---|---|
| 设为默认设备 | 非公开 COM 接口 `IPolicyConfig.SetDefaultEndpoint()`（GUID `F8679F50-850A-41CF-9C72-430F290290C8`） |
| 静音 | `IAudioEndpointVolume.SetMute()`（经 NAudio 封装） |
| 枚举设备 | `IMMDeviceEnumerator.EnumAudioEndpoints()`（经 NAudio） |
| 读输入电平 | `IAudioMeterInformation.GetPeakValue()`（经 NAudio `MasterPeakValue`） |
| 监听默认设备变动 | `IMMNotificationClient.OnDefaultDeviceChanged`（经 NAudio） |

> `IPolicyConfig` 是 Windows 非公开接口，SoundSwitch、AudioDeviceSwitcher 等工具都用同一机制；
> 它稳定但不受官方担保，界面上把切换封装在 try/catch 里以防未来 Windows 变更。

## 电平自动切换的工作原理

`LevelMonitor` 每 ~300ms 采样一次所有麦克风的 `MasterPeakValue`（0~1），
维护一个滑动窗口（默认 6 个样本 ≈ 1.8 秒）算平均电平：

1. 最强设备的窗口平均电平 **低于静音阈值**（默认 0.01）→ 视为没人说话，不动。
2. 最强设备持续达标 **3 个采样周期**（防抖）→ 才自动 `SetDefaultEndpoint` 切过去。
3. 切完触发 `Switched` 事件，托盘弹气泡 + 刷新菜单。

这些参数（采样间隔 / 静音阈值 / 防抖次数）可以在「设置 → 自动模式」里用滑杆调，改完立即生效。

**为什么不用系统峰值表**：`IAudioMeterInformation.MasterPeakValue` 只反映「有没有客户端在使用该端点」，
虚拟声卡（VB-Cable / Voicemeeter 的 CABLE Output）在没有程序采集时会**恒为 0**，
自动切换就永远看不到它的声音。所以 `DevicePeakReader` 改成**对每个设备开一条共享模式
`WasapiCapture`，直接从音频缓冲算峰值**（打不开时自动回退到系统峰值表）。

**相对电平判据**：除了绝对静音阈值，还会比较「最响设备」和「当前设备」的电平——
只要最响的那台明显更响（≥1.5 倍且超过阈值一半），即使它没到绝对阈值也会切过去，
避免虚拟声卡这类电平偏小的设备被漏掉。默认阈值也调低到 0.4%，设置里可调（0.5%–50%）。

**虚拟声卡优先让位**：虚拟声卡（Mic You / VB-Cable / Voicemeeter 等，按设备名关键字识别）
在「都没声音」时不会一直占着默认麦克风——程序会自动切到一个真实设备待命；
一旦虚拟声卡真的收到声音（比如手机连上了），又会切过去。
可在「设置 → 自动模式」里用「优先使用真实设备」关掉这个行为。

**默认设备失效的兜底**：每次采样会先检查「当前默认麦克风是否还在可用设备里」。
如果默认设备被禁用/拔掉（此时剩下的麦克风因为不是默认设备，电平可能一直读到 0），
就立刻切到优先级最高的可用麦克风并提示「麦克风已恢复」，
避免出现「默认设备已死 → 新麦读不到电平 → 永远不切换 → 整机没输入」的死锁。

## 参考项目

- [SoundSwitch](https://github.com/Belphemur/SoundSwitch) — 架构与 `IPolicyConfig` 用法参考
- [MicMute](https://github.com/itsnateai/MicMute) — 托盘 + 热键静音 + Mute Lock 思路参考
- [AudioDeviceSwitcher](https://github.com/josetr/AudioDeviceSwitcher) — 干净的热键切换实现
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) — 当前使用的 WPF 托盘库（Hardcodet 的现代 fork，支持 .NET 8）
- [CC Switch](https://github.com/farion1231/cc-switch) / [Cherry Studio](https://github.com/CherryHQ/cherry-studio) — 「开机自启 + 关闭最小化托盘 + 托盘图标」三件套的产品形态参考

## 排查记录：托盘图标为什么不显示

排查过程中逐一排除并留下结论，避免重复踩坑：

| 怀疑点 | 结论 |
|---|---|
| 图标是 GDI+ 现画的无效 | ⚠️ 部分成立：`Shell_NotifyIcon` 成功但图标空白，换成真实 `.ico` 才正常 |
| 托盘库有 bug | ❌ Hardcodet / H.NotifyIcon / WinForms NotifyIcon 表现一致 |
| 应用以管理员权限运行被 UIPI 挡 | ❌ 实际相反：管理员反而能显示（High→Medium 允许） |
| explorer 没运行 / 托盘被隐藏 | ❌ `Shell_TrayWnd`、`Progman` 都在，其他程序图标正常 |
| **进程完整性级别是 Low** | ✅ **根因**：`Shell_NotifyIcon` 返回 `ERROR_ACCESS_DENIED(5)`，托盘槽位都建不起来 |
| **HICON 无效** | ✅ **第二个坑**：槽位有了但图标空白，必须用真实 `.ico` 文件 |

关键诊断代码在 `MicSwitcher.App/App.xaml.cs` 的 `BuildTrayIcon()` / `LogEnvironment()`：
启动时把 `SessionId`、本进程完整性级别、任务栏进程完整性级别、`IsCreated` 写入
exe 同目录的 `tray-diag.log`。
