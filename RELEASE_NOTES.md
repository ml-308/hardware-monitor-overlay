# v1.1.0 — 统一安装程序（Inno Setup 6）

## 变更
* 安装程序整体重做：改用 **Inno Setup 6** 的现代向导，与项目内其它应用保持同一套安装体验。
* 页面顺序固定为 7 步：**欢迎 → 许可协议 → 安装位置 → 附加任务 → 准备安装 → 安装进度 → 完成**。
* 统一选项：安装位置可改（默认 `%LOCALAPPDATA%\Programs\HardwareMonitor`，无需管理员权限）、
  桌面快捷方式、开始菜单快捷方式、**登录时自动启动**（常驻型工具特有）、完成页「立即运行」。
* 界面简体中文，系统语言非中文时自动 English（`installer/ChineseSimplified.isl`）。
* 目录页增加保护：路径不可创建时给出具体原因；目标目录已有其它文件时先确认再覆盖。
* 静默安装改走 Inno 标准参数：`/VERYSILENT /SUPPRESSMSGBOXES /TASKS="desktopicon,startmenuicon,autostart"`。
* 卸载统一为 `unins000.exe`（控制面板「已安装的应用」同步登记）。

## 升级
* 沿用原 `AppId` 与安装目录，可直接覆盖安装 1.0.x；安装完成后自动删除旧版 `uninstall.exe`
  与旧卸载注册项，不会在「已安装的应用」里留下两条记录。

## 产物
* `HardwareMonitor-Setup-1.1.0.exe` — 安装包（新增）
* `HardwareMonitor.exe` / `HardwareMonitor-1.0.0-portable.zip` / `SHA256SUMS.txt` — 与上版一致

---

# v1.0.1 — 代码签名（彻底消除 SmartScreen 拦截）

## 变更
* 接入 Authenticode 代码签名流程：`sign.ps1` 在 `build.cmd` / `build-all.cmd` 编译后自动对
  `HardwareMonitor.exe` 与 `setup.exe` 签名（SHA256 + RFC3161 时间戳）。
* 删除以往"关闭 SmartScreen"或"绕开外壳检查"的临时做法——代码签名才是 Windows 官方认可、
  对所有用户生效的彻底解法，且**不改动任何系统安全设置**。
* 发布产物（`HardwareMonitor.exe` / `setup.exe` / 便携 zip）均带数字签名；`SHA256SUMS.txt` 已更新。

## 注意
* 要让下载你程序的其他人**不再**看到 SmartScreen 提示，签名证书必须来自受公众信任的 CA
  （**EV 证书**即时生效，**OV/IV 证书**在积累下载量后生效）。仓库提供的二进制使用自签名开发
  证书签名仅用于验证流程；正式发布前请按 `sign.ps1` 说明替换为你的 CA 证书后重新构建。

---

# v1.0.0 — 首个版本

## 亮点

* **单文件 99 KB**，零第三方依赖，只用 Windows 自带的 .NET Framework 编译器/运行时。
* **逐像素透明浮窗**（Layered Window + `UpdateLayeredWindow`，预乘 Alpha 合成），
  圆角外完全透明、背景可透过半透明面板看到，而不是简单的整体半透明。
* **可拖动**（任意位置按住拖动，靠近屏幕边缘自动吸附）与**可缩放**（边缘/四角），
  整个界面按窗口尺寸等比缩放，**任何尺寸下元素都完整显示**（20 组尺寸×模式自动化验证 clip=0）。
* **PerMonitorV2 DPI 感知**：高分屏下文字清晰不模糊；默认尺寸自动按系统 DPI 放大。
* **托盘常驻** + 完整右键菜单（置顶 / 显示项目 / 不透明度 / 刷新间隔 / 迷你模式 / 重置 / 开机自启 / 隐藏 / 退出）。
* **低占用**：后台线程采样，界面每秒只绘制一次；实测 ≈ 51 MB 工作集、单核约 3% CPU。

## 监控指标

CPU 总利用率、每逻辑核占用、内存占用（已用/总量）、GPU 利用率、
各固定盘容量与剩余、磁盘活动、网络上下行速率与累计流量、
进程/线程数、电池与电源状态、系统运行时间与时钟、CPU/内存/GPU/磁盘历史曲线（240 点）。

## 交互

* 拖动任意位置移动；拖动边缘/四角缩放
* 双击切换迷你模式
* 右键打开菜单；右上角 ✕ 退出
* 托盘双击显示/隐藏

## 安装

* 便携：直接运行 `HardwareMonitor.exe`
* 安装：运行 `setup.exe`（当前用户安装，无需管理员权限，含桌面/开始菜单快捷方式与标准卸载项）

## 发布产物

| 文件 | 说明 |
| --- | --- |
| `HardwareMonitor.exe` | 主程序（便携版，101 KB，单文件） |
| `setup.exe` | 安装程序（内嵌主程序，148 KB） |
| `HardwareMonitor-1.0.0-portable.zip` | 便携版压缩包（主程序 + 说明 + 许可） |
| `SHA256SUMS.txt` | 全部产物的 SHA-256 校验值 |

## 验证

* `--selftest`：真实采集全部指标并输出报告
* `--render`：2 种模式 × 10 种尺寸布局自检，全部 clip=0 / minShrink=1.00 / 无溢出
* `--verify`：真机显示 → 截图 → 隐藏 → 再截图逐像素比对，实测圆角外色差 6.89（阈值 23.3）、
  背景相关性 0.238、覆盖 99.1%，结论「通过」

## 已知限制

* 不提供 CPU 温度（需管理员权限且不同主板 WMI 支持不一致）
* GPU 利用率依赖 `GPU Engine` 性能计数器，不可用时该行自动隐藏
* 需要 .NET Framework 4.5+（Windows 8/10/11 内置）

**完整详情见 [README.md](README.md)。**
