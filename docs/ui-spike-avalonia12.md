# 界面改版第 0 批：Avalonia 12 技术验证

改版规格第 0.5 项。每一项都在真窗口里试过一次，代码在 `src/Amber.App/Views/Spike.cs`。

怎么跑：

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/spike-live-check.ps1 -Report <报告目录>
```

脚本用 `AMBER_SPIKE` 启动 Amber，同时在外面监听 UI 自动化事件。跑完后报告目录里有 `spike.txt` 和截图。

| 项目 | 结论 | 依据 | 以后怎么写 |
|---|---|---|---|
| 实时播报（`AutomationProperties.LiveSetting`） | 可行 | 设成 `Polite` 后改 8 次文字，外部监听收到 7 次 LiveRegionChanged（20024），名字依次是 status 1…7；监听挂上之前的那一次没数到。.NET Framework 的托管 UIA 客户端读不了 LiveSetting 属性（报 Unsupported Property），这是客户端太旧，事件本身是发了的 | 状态栏、下载进度摘要、联机检查清单设 `LiveSetting=Polite`；同时更新 `AutomationProperties.Name`，因为事件带的是元素名字 |
| Toast 自定义内容（带按钮） | 可行 | `WindowNotificationManager.Show(控件, …)` 直接显示任意控件，里面的按钮在可视树里，点击事件能触发（截图 toast.png） | 自定义内容模板加内边距和样式；撤销、查看这类按钮放在内容里，不另写通知层 |
| 拖放（`DataTransfer`） | 可行，待人手确认 | `DragDrop.SetAllowDrop` + `DragEnter`/`Drop` 事件，`e.DataTransfer.TryGetFiles()` 取到文件；`DragEnter` 能用来显示放置高亮层。用代码构造的事件走通了，从资源管理器真拖一次还要人试 | 全窗口放置层：DragEnter 显示，DragLeave/Drop 收起；按扩展名分派（.mrpack/.zip 导入，.jar 加模组） |
| `NumericUpDown` 全角数字 | 可行（要转换器） | 输入“１９２０”：带 `TextConverter` 得到 1920，不带得到 null（输入被丢掉） | 所有数字框用 `FullWidthDigits` 转换器（全角数字、全角负号和小数点换成半角） |
| `TransitioningContentControl` 状态保留 | 可行（状态放 VM） | 切走再切回来 View 会重建；Tab 选择绑在 VM 上，回来后还是原来的那一页 | 页内的 Tab、滚动位置、展开状态这类要保留的，都存在页面的 VM 里 |
| 托盘图标（`TrayIcon`） | 可行，待人手确认 | 建立、显示、收起都没出错；任务栏通知区里看不看得见要人看一眼 | 要用托盘时（比如游戏运行中收起窗口）用 `TrayIcon`，菜单放显示窗口和退出 |

没有需要“删除验收项”的。两项“待人手确认”的都不影响第 1 批开工，做到对应功能时再手动验一次。
