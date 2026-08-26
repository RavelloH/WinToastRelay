# WinToastRelay

<!-- auto-readme-i18n-switcher start -->
| [English](/.github/readme/README.en.md) | 中文 |
<!-- auto-readme-i18n-switcher end -->

> 原生 Windows 通知实时转发桥接应用，支持 Bark 和 JSON Webhook。

WinToastRelay 使用 `Windows.UI.Notifications.Management.UserNotificationListener` 及其 `NotificationChanged` 事件工作，不使用定时轮询。应用启动时只枚举一次通知中心建立基线，后续变化通过通知 ID 处理。

## 功能

- 基于 WinUI 3 / Windows App SDK 的 Windows 原生视觉风格。
- 支持中文和英文界面，可在设置中切换。
- 支持 Bark JSON POST，可自定义标题、正文和任意 Bark 参数。
- 支持通用 HTTPS JSON Webhook，可选 Bearer Token。
- 支持按应用筛选通知。
- 持久化本地发送队列，并对临时失败进行指数退避重试。
- 查看最近传递记录、发送状态和 HTTP 响应详情。
- 支持系统托盘运行，关闭窗口后仍可继续转发。
- 支持通过 Windows 启动任务在登录时启动。
- 配置有效后自动启动原生通知监听，无需手动轮询。
- 不使用官方 WinToastRelay 中转服务器、分析服务、广告 SDK 或 AI 模型服务。

## 系统要求

- Windows 10 版本 1809 或更高版本，推荐 Windows 11。
- 从源代码构建需要 .NET 9 SDK。
- 兼容 Windows App SDK 的开发环境。

## 构建和测试

```powershell
dotnet restore .\WinToastRelay.csproj -r win-x64
dotnet build .\WinToastRelay.csproj -r win-x64 -p:Platform=x64
dotnet test .\tests\WinToastRelay.Tests\WinToastRelay.Tests.csproj
```

应用采用 MSIX 打包，因为 Windows 通知访问需要交互式包身份。首次使用时，请按照 Windows 提示授予通知访问权限。

## Microsoft Store 发布

当前预留的 Store 包身份如下：

```text
名称：      RavelloH.WinToastRelay
发布者：    CN=184C7048-0661-4259-8EE3-39EFE462DFBE
Store ID：  9MV8SL6JLV2D
```

提交到 Microsoft Store 后，Store 会重新签名。上传到 Partner Center 的本地包只需要一个发布者匹配的临时证书：

```powershell
.\scripts\New-DevCertificate.ps1 `
  -Subject "CN=184C7048-0661-4259-8EE3-39EFE462DFBE" `
  -OutputName "WinToastRelay-store-upload" `
  -Password "choose-a-temporary-password" `
  -ValidYears 1

dotnet publish .\WinToastRelay.csproj -r win-x64 -p:Platform=x64 -p:Configuration=Release `
  -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Always -p:AppxBundlePlatforms=x64 `
  -p:PackageCertificateKeyFile="$PWD\certs\WinToastRelay-store-upload.pfx" `
  -p:PackageCertificatePassword="choose-a-temporary-password"
```

将 `AppPackages` 中生成的 `.msixbundle` 上传到 Partner Center 提交。不要上传 `.cer`，Microsoft Store 会替换应用包签名。应用请求了 `runFullTrust`，这是打包 WinUI 3 桌面程序和启动任务集成所需的能力，可能需要在 Partner Center 中申请批准。

## 本地侧载

本地测试时，请使用同一输出目录中的 `.msix` 和匹配的 `.cer`。安装前将证书导入 **本地计算机 → 受信任的人**。如果出现 `0x800B0109` 或 `0x87e80034`，请确认安装包和证书来自同一次构建。

不要将临时 PFX 作为公开签名身份发布。

## 传递方式

### Bark（默认）

输入 Bark 服务器地址和设备密钥。WinToastRelay 会向服务器的 `/push` 端点发送 JSON POST：

```json
{
  "device_key": "your-device-key",
  "title": "Mail: Build passed",
  "body": "Notification body",
  "sound": "bell",
  "group": "work"
}
```

标题和正文模板支持 `{app}`、`{title}`、`{body}`、`{id}`、`{eventType}` 和 `{createdAt}`。在设置中每行填写一个 `key=value`，即可添加任意 Bark 参数；这些参数会作为 JSON 字段发送，长标题和长正文不会进入 URL。

### 通用 JSON Webhook

```json
{
  "eventType": "notification.added",
  "deliveryId": "a-generated-id",
  "notification": {
    "id": 123,
    "app": "示例应用",
    "title": "通知标题",
    "body": "通知正文",
    "createdAt": "2026-08-20T00:00:00Z"
  }
}
```

`X-WinToastRelay-Delivery` 请求头包含相同的传递 ID，便于接收端去重。超时、429 和 5xx 响应会持久化并使用指数退避重试，其他失败响应会记录为死信。

## 隐私

WinToastRelay 会处理通知应用名称、图标、标题、正文、标识符、事件类型和创建时间，并且只将这些数据发送到用户配置的 Bark 或 JSON Webhook 地址。应用不会将通知数据发送到 RavelloH 或官方 WinToastRelay 云服务。

可选的 Bearer Token 存储在 Windows 凭据管理器中。其他设置、发送队列和最近记录保存在 Windows 本地应用数据目录中。

请阅读完整的[隐私政策](/PRIVACY.md)。

## 项目状态

WinToastRelay 是一款开源 Windows 应用，当前已准备进行 Microsoft Store 分发。欢迎提交贡献和问题反馈。

## 许可证

[MIT](/LICENSE) Copyright 2026 RavelloH
