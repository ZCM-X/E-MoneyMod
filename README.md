# EMoneyMod

EMoneyMod 是一个 MelonLoader 插件，用来模拟电子支付流程。

本仓库只包含 Mod 自身代码与必要的第三方运行库，不包含游戏资源或其他受版权保护的素材。

使用者应自行确认所在地区的法律、服务条款和设备使用政策。请勿将本项目用于绕过真实收费、商业经营或其他未经授权的用途。

## 功能

- 解锁电子支付入口和品牌列表
- 支持兼容读卡器刷卡
- 兼容游戏自身电子支付提示音和点数入账音
- 任何卡即可完成购买


## 构建

需要：

- Windows
- .NET Framework 4.8 目标框架
- .NET SDK
- MelonLoader

构建：

```powershell
dotnet build .\EMoneyMod.csproj -c Release -p:GameDir="D:\YourGame\Package"
```

## 使用

1. 启动目标游戏。
2. 进入电子支付界面。
3. 选择品牌和点数。
4. 使用兼容读卡器刷卡



## 许可

MIT
