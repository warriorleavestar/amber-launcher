# Jay 品牌素材

- 品牌：Jay / Jay Launcher，名称取自《猫武士》的松鸦羽 Jayfeather。
- 当前图标：单根灰蓝色羽毛，少量琥珀色花纹，透明背景。
- 文件：`jay-feather-amber-v1.png`，透明 PNG 源图。应用使用由此等比缩放导出的 `src/Amber.App/Assets/jay.png` 与多尺寸 `jay.ico`；此前发布包不受源码改名影响。
- 生成方式：内置 imagegen，以已确认的单羽毛图案为编辑目标。未使用 API/CLI fallback。

![Jay 羽毛](jay-feather-amber-v1.png)

## 本次编辑提示词

```text
Edit this exact single-feather Jay launcher logo. Preserve the original outer silhouette, proportions, diagonal angle, central split, short barb cuts, slate-blue base color, centered composition, and fully transparent background. The ONLY design change is to add a small amount of tasteful AMBER/GOLD-ORANGE patterned accents INSIDE the feather: two or three broad, clean, gently curved bands following the feather's barbs, placed as deliberate restrained inlays, roughly 15 percent of the feather area, with the slate-blue remaining dominant. Keep the pattern clear at small app-icon sizes. Flat solid colors, clean vector-like edges, no gradients or texture, no glow. Exactly one feather, no letters, no text, no cat, no extra object, no enclosing shape. Clean any edge speckles while preserving all geometry. Return only the edited transparent logo.
```

在 Windows 上运行 `powershell -NoProfile -File tools/export-brand-icons.ps1` 可重新导出应用素材。脚本保留透明通道和完整画布，只缩放、不重绘。ICO 包含 16、20、24、32、40、48、64、128、256 像素；16–24 像素时细纹会融合，32 像素起更清晰。
