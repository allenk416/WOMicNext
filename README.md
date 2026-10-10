# WO Mic Next

[简体中文](#简体中文) | [English](#english)

## 简体中文

一个面向 Windows 的 WO Mic Wi-Fi 客户端，重新设计了界面和连接流程，保留连接与音频传输等核心功能，并提供中英文界面、本地监听、麦克风静音开关、连接状态、断线提醒、丢包统计和后台重连。

### 使用发行版

1. 从 GitHub 仓库的 **Releases** 页面下载 Windows 发行包并解压。
2. 在手机 WO Mic 中选择 Wi-Fi 并启动服务。
3. 确认手机和电脑处于可互通的网络，在应用中填写手机显示的 IP 地址，然后点击“连接”。
4. 按需启用本地播放、断线重连和提醒方式。

运行需要 Windows 10/11、手机端 WO Mic Wi-Fi 服务，以及单独安装并正常工作的 WO Mic Windows 驱动。本项目不包含驱动。若某个发行包依赖 .NET Desktop Runtime，具体要求会写在该版本的 Release 说明中；自包含发行包无需另行安装 .NET。

### 功能

- 在主界面管理 Wi-Fi 连接、断开和连接状态。
- 将手机音频传入 Windows WO Mic 驱动；可单独静音麦克风传输而保持网络连接，也可选本地监听。
- 显示音频包和近期丢包统计。
- 意外断开后按设置尝试后台重连，并可选提示音或 Windows 通知。
- 关闭窗口时可最小化到系统托盘。
- 托盘图标用颜色显示连接状态；静音时在麦克风图标上显示斜线。
- 界面可在简体中文和英语之间切换。

### 来源与许可

网络协议实现改编自 MIT 许可的 [YuMic](https://github.com/Excez0/yumic-server)。音频解码依赖 Concentus 2.2.2，许可文本见 [CONCENTUS-LICENSE.txt](CONCENTUS-LICENSE.txt)；YuMic 的版权和许可声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。自包含 Release 还附带 .NET 运行时许可和第三方通知。

开发者从源码构建的步骤见 [BUILDING.md](BUILDING.md)。项目代码采用 MIT License，见 [LICENSE](LICENSE)。

## English

A Windows WO Mic Wi-Fi client with a redesigned interface and connection flow. It retains core connection and audio streaming features, with Chinese and English UI, local monitoring, a microphone mute switch, connection status, disconnect alerts, packet-loss statistics, and background reconnection.

### Using a release

1. Download and extract a Windows package from the repository's **Releases** page.
2. Start the Wi-Fi service in the WO Mic app on your phone.
3. Make sure the phone and PC can reach each other over the network. Enter the IP address shown on the phone, then select **Connect**.
4. Configure local monitoring, reconnect behavior, and alerts as desired.

The app requires Windows 10/11, the phone's WO Mic Wi-Fi service, and a separately installed, working WO Mic Windows driver. The driver is not included. If a release requires the .NET Desktop Runtime, its release notes will say so; a self-contained release does not require a separate .NET installation.

### Features

- Manage Wi-Fi connections and view connection status from the main window.
- Send phone audio to the Windows WO Mic driver; mute transmission without disconnecting, or monitor locally.
- View audio packet and recent packet-loss statistics.
- Retry unexpected disconnects in the background and optionally play an alert sound or show a Windows notification.
- Minimize to the system tray when the window is closed.
- The tray icon uses color for connection status and a slash when the microphone is muted.
- Switch the interface between Simplified Chinese and English.

### Sources and licenses

The network protocol implementation is adapted from [YuMic](https://github.com/Excez0/yumic-server), which is licensed under MIT. Audio decoding uses Concentus 2.2.2; see [CONCENTUS-LICENSE.txt](CONCENTUS-LICENSE.txt) for its license and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for YuMic's copyright and license notice. Self-contained releases also include the .NET runtime license and third-party notices.

See [BUILDING.md](BUILDING.md) to build from source. The project code is licensed under MIT; see [LICENSE](LICENSE).
