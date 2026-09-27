# EMoneyMod

EMoneyMod 是一个给 SDEZ 1.70 / Sinmai 使用的 MelonLoader 插件，用来模拟电子支付流程。

它的目标不是改真实余额，而是让游戏以为电子支付终端可用，并在刷卡后把对应点数加到游戏点数里。

## 功能

- 解锁电子支付入口和品牌列表
- 支持 HINATA / HINATA Go 读卡器刷卡
- 支持外部程序通过本机 HTTP 请求触发刷卡
- 兼容游戏自身电子支付提示音和点数入账音
- 点数写入游戏自己的 `appdata\SDEZ\appfile.dat` 备份记录
- 不生成 `EMoneyMod.credit.txt` 之类的旁路存档

点数使用 `BackupLocalParameterRecord` 的保留槽保存。删除 `appdata` 会连同游戏数据一起清掉这些点数。

## 构建

需要：

- Windows
- .NET Framework 4.8 目标框架
- .NET SDK
- MelonLoader 版 SDEZ 1.70
- `libs\HidSharp.dll`

构建：

```powershell
dotnet build .\EMoneyMod.csproj -c Release -p:GameDir="H:\SDEZ1.70\Package"
```

如果游戏目录在环境变量 `SDEZ_PACKAGE_DIR` 中，也可以直接用默认配置：

```powershell
$env:SDEZ_PACKAGE_DIR = "H:\SDEZ1.70\Package"
dotnet build .\EMoneyMod.csproj -c Release
```

## 部署

```powershell
.\deploy.ps1 -GameDir "H:\SDEZ1.70\Package"
```

只部署已经构建好的 DLL：

```powershell
.\deploy.ps1 -GameDir "H:\SDEZ1.70\Package" -SkipBuild
```

部署后 `Mods` 目录里只需要 `EMoneyMod.dll`，`HidSharp.dll` 会作为嵌入资源打包进去。

## 使用

1. 启动游戏。
2. 进入电子支付界面。
3. 选择品牌和点数。
4. 使用 HINATA 读卡器刷卡，或通过本机 HTTP 接口触发刷卡。

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

当前实现针对 SDEZ 1.70 的 `BackupLocalParameterRecord` 布局。若游戏版本升级导致本地备份结构变化，需要重新确认保留槽布局。

## 许可

MIT
