# EMoneyMod

EMoneyMod 是一个 MelonLoader 插件，用来模拟电子支付流程。

本项目不是官方项目，与任何游戏厂商、发行商、平台或相关权利方无关联，也未获得授权或背书。

本仓库只包含 Mod 自身代码与必要的第三方运行库，不包含游戏本体、游戏资源、曲目、谱面、图像、音频、商标文件或其他受版权保护的素材。

使用者应自行确认所在地区的法律、服务条款和设备使用政策。请勿将本项目用于绕过真实收费、商业经营或其他未经授权的用途。

## 功能

- 解锁电子支付入口和品牌列表
- 支持兼容读卡器刷卡
- 支持外部程序通过本机 HTTP 请求触发刷卡
- 兼容游戏自身电子支付提示音和点数入账音
- 点数写入游戏自己的本地备份记录
- 不生成旁路点数文件

## 构建

需要：

- Windows
- .NET Framework 4.8 目标框架
- .NET SDK
- MelonLoader
- `libs\HidSharp.dll`

构建：

```powershell
dotnet build .\EMoneyMod.csproj -c Release -p:GameDir="D:\YourGame\Package"
```

## 部署

```powershell
.\deploy.ps1 -GameDir "D:\YourGame\Package"
```

只部署已经构建好的 DLL：

```powershell
.\deploy.ps1 -GameDir "D:\YourGame\Package" -SkipBuild
```

部署后 `Mods` 目录里只需要 `EMoneyMod.dll`，`HidSharp.dll` 会作为嵌入资源打包进去。

## 使用

1. 启动目标游戏。
2. 进入电子支付界面。
3. 选择品牌和点数。
4. 使用兼容读卡器刷卡，或通过本机 HTTP 接口触发刷卡。

外部收卡服务默认监听：

```text
http://127.0.0.1:7666/
```

## 日志

默认只输出启动、刷卡开始、刷卡完成、点数读回和错误。

需要排查问题时，设置环境变量后启动游戏：

```powershell
$env:EMONEYMOD_VERBOSE = "1"
```

详细日志会带上 `[Debug]` 前缀。

## 兼容性

当前实现依赖目标游戏版本的本地备份记录布局。若游戏版本升级导致本地备份结构变化，需要重新确认保留槽布局。

## 许可

MIT
