# Jay 仓库改名与品牌说明

日期：2026-11-06（新西兰时间）。这是项目所有者确认的命名与视觉决定，供各开发、审查和发布工作线同步。

## 已确认的名称与地址

- 品牌名：**Jay**；完整产品称呼可用 **Jay Launcher**。以英文名为中心，不另设主中文品牌名。
- 名字来源：《猫武士》中的松鸦羽 **Jayfeather**。
- GitHub 仓库已由 `warriorleavestar/amber-launcher` 更名为 [`warriorleavestar/jay-launcher`](https://github.com/warriorleavestar/jay-launcher)。
- 原仓库网页和 Git 地址由 GitHub 重定向。现有提交、Issue、PR 和 Release 保留；后续链接和操作使用新地址。

独立 clone 请更新远程地址：

```sh
git remote set-url origin https://github.com/warriorleavestar/jay-launcher.git
```

同一主仓库下的 Git worktree 共享远程配置，不需要逐个重复修改。不要重新创建旧仓库名，以免破坏旧链接重定向。

## Logo 方向与素材

图标只使用**一根羽毛**，保留灰蓝色主体，加入少量琥珀色花纹。不加入猫、字母或文字。当前素材为带透明通道的 PNG，作为后续应用图标接入的源图。

![Jay 单羽毛 Logo](../brand/jay-feather-amber-v1.png)

- 素材：[jay-feather-amber-v1.png](../brand/jay-feather-amber-v1.png)
- 生成方式和提示词：[品牌素材说明](../brand/README.md)

## 本次变更范围

本次完成仓库名称调整，更新 README、克隆命令和节点部署文档中的仓库链接，并存档 Logo 与本说明。

现有发布包、应用窗口名称、可执行文件、项目目录和命名空间仍可能显示 `Amber`。应用内显示名称、ICO、平台图标和发布产物的正式切换应由相应 UI/发布工作线在后续改动中完成，验证小尺寸显示及各平台打包后再发布。本说明不代表已发布更名版安装包。

## 兼容要求

不得用全局替换把所有 `Amber` / `AMBER` / `amber` 改成 Jay。以下属于存量数据或协议，需要保留或制定明确迁移方案：

- 已有账号库、设置目录、游戏目录及 `.amber-*` 文件和标记。
- 账号加密 entropy、HKDF 的上下文字符串和其他加密用途标识。
- `AMB-`、`AMBDIRECT`、`AMBNODE` 等房间码、节点码、握手和联机协议标识。
- 已使用的环境变量、服务名称、安装路径和脚本接口。

显示品牌更新与兼容标识迁移应分开实施。不要让已有账号无法解密、实例失联或新旧客户端无法互联。

各工作线继续原有任务；已有 PR 编号、审核要求和安全验收保持有效，后续 GitHub 操作统一使用新仓库地址。
