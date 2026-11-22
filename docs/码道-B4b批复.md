【批复】批次 4a 通过。先纠正我批复里的一处错误，然后放 4b

## 一、我写错的地方

我批复里写的是 `new System.Drawing.Icon(path, 32, 32)`，而仓库里的实际代码一直是
`new System.Drawing.Icon(path)`（无尺寸参数）。**是我凭印象写错了，你的现状描述是对的。**

你没有照着我那句错误措辞去改成带参数的构造，而是按「加载方式不变」保留原样并主动标注
这个差异——这个处理是对的。带尺寸参数会改变图标源的选取，属于独立的行为变更，
确实不该混在纯搬移里做。

**本批不做这个改动**，保持 `new Icon(path)`。

## 二、批次 4a 复核结果

我方独立复跑：

```
build.cmd  →  0 警告 0 错误
test.cmd   →  103/103，479 ms
smoke.cmd  →  [PASS] startup smoke ok
```

日志五行顺序我亲自看了，是对的：

```
=== BuildTrayIcon 开始 ===
SessionId=1
图标: 32x32
创建后: IsCreated=False Visibility=Visible
```

代码核对四条约束全部落实：`_currentMenu` + `SetContextMenu` + `CloseMenu()` 是同一实例
（`ExitApp` 调 `_trayHost?.CloseMenu()`，不会关错菜单）；`ApplicationIdle` 保留；
`UpdateTrayIcon` 的早退条件逐字保留；`_trayIconKeepAlive` 仍是实例字段并保持强引用。

App.xaml.cs 909 → 336 行，累计减 573。

## 三、本批范围（4b）

搬到 `TrayCommands`（或你定的类名）：

- `SetManualMode` / `SetAutoMode` / `SetLockedMode`
- `SafeSwitchTo` / `SafeCycle` / `SafeToggleMute`
- `OnAutoSwitched`
- 托盘菜单里内联的「开机自启」点击逻辑

## 四、4b 的四条硬约束

**1. 事件订阅顺序必须与 `InitializeApp` 完全一致。**

原顺序是「先订阅 `AutoSwitched` / `Error` / `Trace`，再建托盘」。如果改成在 VM 构造函数里
订阅，而那时托盘还没创建，`AutoSwitched` 触发 `RefreshTrayMenu` 时 `_tray.ContextMenu`
是 null，直接 NRE。

做法：订阅仍写在 `InitializeApp` 里，只是回调目标从 App 的方法换成 VM 的方法。

**2. `OnAutoSwitched` 外层的 `Dispatcher.BeginInvoke` 必须保留。**

它把回调从采样线程切回 UI 线程。去掉它会在非 UI 线程碰 WPF 控件，抛
「调用线程无法访问此对象」；改成 `Invoke` 会让采样线程等 UI，退出时可能死锁。

**3. `Error` 的订阅只能挂一次。**

如果原来挂的是匿名 lambda，改成具名方法后若在 `ApplyConfig`、`Reload` 等处重复挂载，
会一次错误弹多个气泡。要求：订阅点唯一，且位置与原实现一致。

**4. VM 必须在 `_services` 已初始化之后创建。**

延迟启动（`--minimized` + `DelayedAutostartSeconds`）期间 `_services` 是 null。
如果 VM 在 `OnStartup` 就构造并缓存 `_services`，延迟期访问会 NRE。
保持「VM 与 trayHost 同在 `InitializeApp` 里创建」这个前提。

## 五、验收

三条命令逐字输出，加日志证据：

- `tray-diag.log` 仍要有 `=== BuildTrayIcon 开始 ===`、`SessionId=`、`图标: 32x32`、
  `创建后: IsCreated=False`、`ForceCreate 后: IsCreated=True` 五行且顺序不变
- 若能在无 UI 的情况下触发一次自动切换（例如等 10 秒看是否有 `电平跟踪` 行），
  确认 `判定=` 字段仍正常输出

## 六、这一批之后必须真机验收

4a 已经把托盘与菜单换成新代码，4b 再换状态与命令。这两块合起来就是「图标 / 菜单 / 退出」
三件事的全部实现。

所以 4b 完成后，请**停止继续拆分**，等我方真机验收通过再排批次 5。
批次 5（AppBootstrapper / SettingsWindowCoordinator / ShutdownCoordinator）涉及启动编排与
退出流程，一旦在未验收的托盘代码上继续改，出问题时无法判断是哪一批引入的。

## 七、仍然不要做

- 不改 UI 文案、菜单项顺序、热键默认值、`Local\` 前缀
- 不改退出流程的「后台释放 + 3 秒超时 + 无论超时都 Shutdown」
- 不改 Core 各 Policy / `DevicePeakReader` / `AppConfig`
- 不 `git commit` / `git push`
- 临时脚本放 `scripts/_scratch/`
