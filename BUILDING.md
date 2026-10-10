# 从源码构建 / Build from source

## 简体中文

本说明面向开发者或希望自行构建程序的用户。普通使用者下载 Releases 中的发行包即可，无需安装 SDK 或执行构建步骤。

### 环境

- Windows 10/11
- .NET 10 SDK（含 Windows 桌面开发支持）
- 可访问 NuGet.org，用于还原 Concentus 依赖

### 调试构建

在仓库根目录运行：

```powershell
dotnet restore .\WOMicNext.csproj
dotnet build .\WOMicNext.csproj -c Release --no-restore
```

这会生成依赖已安装 .NET Desktop Runtime 的构建输出。它适合本机开发验证，不应直接当作自包含发行包。

### 发行构建

例如，为 64 位 Windows 生成自包含发行输出：

```powershell
dotnet publish .\WOMicNext.csproj -c Release -r win-x64 --self-contained true
```

自包含包会包含 .NET 运行时，体积较大，但使用者无需另装 .NET Desktop Runtime。将完整 `publish` 输出目录打包，并把第三方许可文件一并放入发行包。发布前应在未安装 .NET Desktop Runtime 的 Windows 环境验证发行包，并核对版本号、包内容和 Release 说明一致。

## English

This guide is for developers and users who want to build the app themselves. Most users can download a release package and do not need the SDK or build steps.

### Requirements

- Windows 10/11
- .NET 10 SDK with Windows desktop support
- Access to NuGet.org to restore the Concentus dependency

### Development build

Run from the repository root:

```powershell
dotnet restore .\WOMicNext.csproj
dotnet build .\WOMicNext.csproj -c Release --no-restore
```

This produces build output that depends on an installed .NET Desktop Runtime. It is suitable for local development and verification, but is not a self-contained release package.

### Release build

For example, create a self-contained release for 64-bit Windows:

```powershell
dotnet publish .\WOMicNext.csproj -c Release -r win-x64 --self-contained true
```

A self-contained package includes the .NET runtime. It is larger, but users do not need to install the .NET Desktop Runtime separately. Package the complete `publish` output and include the third-party license files. Before publishing, verify the package on Windows without the .NET Desktop Runtime installed, and make sure its version, contents, and Release notes agree.
