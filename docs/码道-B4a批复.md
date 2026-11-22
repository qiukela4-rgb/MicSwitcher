【批复】批次 3 通过。本批只做 4a（TrayIconHost + TrayMenuBuilder），4b 等验完再放

## 一、批次 3 复核

我方独立复跑：`build` 0 警告 0 错误、`test` 103/103（481 ms）、`smoke` [PASS]。

代码核对：`Local\` 前缀未改、`TryAcquire` / `StartListening` 仍在 OnStartup 主线程、
两个 typed catch 保留、置前仍走 `FindWindow` + `ShowWindow` + `SetForegroundWindow`、
`Dispose` 只释放 Mutex 不 ReleaseMutex。

**双开我也独立做了一次**，证据链比你的更直接：

```
[22:44:23.757] 启动延迟：8 秒后再初始化          ← 第一实例
[22:44:27.884] 单实例：已通知已有实例显示窗口    ← 第二实例
[22:44:27.892] 退出：OnExit                      ← 第二实例自行退出
[22:44:31.766] === BuildTrayIcon 开始 ===        ← 第一实例 8 秒后正常初始化
```

`taskkill` 只终止了 **1 个 PID**。这说明第一实例不但活着，还继续走完了延迟初始化——
比你那次的证据强，因为你只能看到"进程数 1"，看不到第一实例是否真的在跑。

## 二、本批范围（只做 4a）

搬这几块到 `TrayIconHost` 与 `TrayMenuBuilder`：

- `BuildTrayIcon`（148–189）
- `LoadTrayIcon` / `CreateTrayIcon` / `UpdateTrayIcon`（300–378）
- `RefreshTrayMenu` / `AddRadialItem`（381–475）
- `ShowBalloon`（894–898）
- 字段 `_tray`、`_trayIconKeepAlive`、`_trayIconIsMuted`

**不要动**：`TrayViewModel`、`SetManualMode` / `SetAutoMode` / `SetLockedMode`、
`SafeSwitchTo` / `SafeCycle` / `SafeToggleMute`、`OnAutoSwitched`、`_services` 的订阅。
这些留 4b。

## 三、4a 的四条硬约束

**1. 菜单必须是同一个实例引用。**

`ExitApp` 现在是 `_tray.ContextMenu.IsOpen = false`。菜单构建搬到 Builder 之后，
退出时必须关掉**当前显示的那个**菜单实例。如果 Builder 每次 new 一个新菜单、
而 Host 持有的还是旧的那个，退出时菜单不会消失——真机验收清单第 1 项会直接失败，
而且这正是昨天那个 bug 的复现形态。

要求：Host 暴露 `CloseMenu()`（或 `CurrentMenu` 属性），`ExitApp` 改调它。
不要在新旧两个菜单之间出现"关闭了旧实例"的情况。

**2. `ApplicationIdle` 的延迟刷新时机不变。**

`BuildTrayIcon` 用 `Dispatcher.BeginInvoke(..., DispatcherPriority.ApplicationIdle)`
做延迟刷新，保证日志里 `创建后: IsCreated=False` 出现在 `ForceCreate 后: IsCreated=True`
之前。改成 `Normal` 优先级会让这两行顺序变化，`smoke.cmd` 的判定条件就失效了。

**3. 图标强引用与去重判断不能失效。**

`_trayIconKeepAlive` 与 `_trayIconIsMuted` 从 static 搬到实例字段后，单实例下行为等价。
但 `UpdateTrayIcon` 里的早退条件：

```csharp
if (wantMuted == _trayIconIsMuted && _trayIconKeepAlive is not null) return;
```

不能失效——否则每次刷新都重建 HICON，托盘图标会闪，且句柄越积越多。

**4. 图标加载方式不变。**

仍从 `Assets\app.ico` / `app-muted.ico` 加载（`new System.Drawing.Icon(path, 32, 32)` 这条路）。
不要改成 XAML 资源或 `BitmapSource`——那会改变 DPI 处理，托盘可能变模糊或空白，
而且真机清单第 0 项就是查这个。

## 四、验收

三条命令逐字输出，加上日志证据：

`tray-diag.log` 必须仍有这五行，**顺序不变**：

```
=== BuildTrayIcon 开始 ===
SessionId=
图标: 32x32
创建后: IsCreated=False
ForceCreate 后: IsCreated=True
```

能看到托盘图标的话，确认不是空白方块；做不了就标「未执行 + 原因」。

## 五、4b 等验完再放

4b 涉及事件订阅顺序与退出流程边缘（`OnAutoSwitched`、`Error`、`_services` 迁移），
等 4a 验收结果出来我再单独批复。**不要连着做 4b。**
