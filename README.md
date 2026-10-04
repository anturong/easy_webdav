# Easy WebDAV

**⚠️ 试验性工具 — 禁止用于实际生产环境**

这是一个个人学习/试验性质的 WebDAV 文件服务器实现，功能不完善，安全性未经审计，**严禁用于任何实际生产环境或公网部署**。

![Easy WebDAV 管理界面与内置网页文件管理器](docs/ui.png)

## 关于密码

本软件不包含任何硬编码密码。首次运行时会自动生成随机管理员密码并写入 `webdav_config.json` 配置文件，控制台或弹窗会显示初始密码。

## 功能

- WebDAV 文件服务（HTTP/HTTPS）
- 多用户管理、读写权限控制
- WPF 图形管理界面
- 自签名 SSL 证书自动生成
- Windows 防火墙规则配置
- 安装/卸载为 Windows 服务
- 内置网页文件管理器：列表/网格视图、搜索、上传、**多选批量打包下载**、图片/视频预览、面包屑「根目录 / 上一级」导航

## 下载

从 [Releases](https://github.com/anturong/easy_webeav/releases) 下载最新版本：

| 包 | 大小 | 说明 |
|---|---|---|
| `Easy_WebDAV_v1.1.1_win-x64.zip` | ~92 MB | **零依赖自包含版**，已内置 .NET 8 运行时，目标机无需安装任何运行时，解压即用 |

解压后目录里只有 `WebDAVService.exe`、`WebDAVConfigurator.exe` 和 `web\` 三项，没有多余的 dll。以管理员身份运行 `WebDAVConfigurator.exe` 即可。

## 自行编译

```bash
# 默认即"零依赖自包含单文件"：产物为 2 个 exe + web\，无任何 dll，目标机无需装运行时
dotnet publish src/WebDAVService/WebDAVService.csproj -c Release -o publish
dotnet publish src/WebDAVConfigurator/WebDAVConfigurator.csproj -c Release -o publish
publish/WebDAVConfigurator.exe

# 想要体积更小的"框架依赖"版可加 -p:SelfContained=false（需目标机装 .NET 8 桌面运行时）
```

## 同类工具对比

| 方案 | 类型 | 图形管理 | 上手难度 | 平台 |
|---|---|---|---|---|
| **Easy WebDAV（本工具）** | 独立服务端 | ✅ WPF 界面 | 极低 | Windows |
| IIS WebDAV | Windows 内置模块 | ❌ 需 IIS 管理器 | 高 | Windows Server |
| Apache mod_dav | 独立服务端 | ❌ 配置文件 | 高 | 跨平台 |
| Nginx dav_module | 独立服务端 | ❌ 配置文件 | 高 | 跨平台 |
| Cerberus FTP Server | 商业产品 | ✅ | 低 | Windows |
| Serv-U | 商业产品 | ✅ | 低 | Windows |

本工具的优势在于：**Windows 原生 .NET 开发、零外部依赖、WPF 图形配置界面、一键安装服务**，适合快速在 Windows 上搭建 WebDAV 服务。劣势是试验性质，未经安全审计，不适合生产环境。

## License

MIT
