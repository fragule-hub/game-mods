# Berserk Mode 简体中文汉化补丁

将《Berserk Mode》（Steam）的游戏文本完整翻译为简体中文的运行时补丁。成品下载见 [Releases · v1.2](https://github.com/fragule-hub/game-mods/releases/tag/v1.2)，安装与卸载说明见 [使用说明.md](./使用说明.md)。

## 工程结构

```
├── CNFix.cs          # 插件源码（单文件，BepInEx 插件）
├── CNFix.csproj      # 构建配置（net48，引用游戏目录的 Unity/BepInEx 程序集）
├── zh.tsv            # 翻译表（约 690 条，格式：英文<Tab>中文，多行用 \n 转义）
├── 使用说明.md        # 面向玩家的安装/卸载说明
└── 卸载汉化补丁.bat    # 一键卸载脚本（纯 ASCII，删除全部补丁文件）
```

字体（阿里巴巴普惠体 3.0，阿里巴巴免费商用授权）不入库，随 Release 包的 `BepInEx/plugins/CNFix/fonts/` 分发。

## 实现原理

插件基于 BepInEx 5 + HarmonyX，运行时拦截文本而不修改任何游戏文件。源码按职责分五层：

| 类 | 职责 |
|---|---|
| `CNFixPlugin` | 入口装配：注册字体（GDI `AddFontResourceExW` + `Font.CreateDynamicFontFromOSFont`）、加载词表、挂钩子、场景加载扫描、F9 诊断转储 |
| `TextHooks` | 三个 Harmony 钩子：`Text.set_text`（翻译总闸，覆盖所有动态赋值）、`Text.OnEnable`（运行时实例化面板）、`Text.OnPopulateMesh`（排版后修正窄条裁剪） |
| `TextTuner` | 字体替换与窄条裁剪放宽（中文字体行高较大，截断式文本框会整行不可见） |
| `Translation` | 翻译引擎：CJK 快速否决 → 缓存（含负缓存）→ 精确词条 → 空白归一化 → 预编译正则规则表 → 前缀规则（尾部递归查表） |
| `Diagnostics` | F9 手动转储全部 Text 状态（层级/字体/裁剪/渲染层详情），用于排查漏翻 |

构建：`dotnet build -c Release`（先把 csproj 顶部的 `GameDir` 属性改成你的游戏根目录，引用会自动从那里解析）。

## License

补丁代码随仓库 License 发布。游戏文本翻译由 AI 生成，经实测修正；问题反馈：yaoguang_2026@163.com（附截图）。
