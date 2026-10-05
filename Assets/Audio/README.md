# 音频资源目录

音频按用途分两个文件夹放，代码里传**短名**就行，后缀不用写（框架会自动按
`AudioInitOptions.SearchExtensions` 依次去找 `.wav / .ogg / .mp3 / .aiff`）：

| 文件夹 | 放什么 | 代码 |
| --- | --- | --- |
| `Assets/Audio/Bgm/` | 背景音乐（长、循环） | `AudioKit.PlayBgm("lobby")` → `Assets/Audio/Bgm/lobby.ogg` |
| `Assets/Audio/Sfx/` | 音效（短、可叠加） | `AudioKit.PlaySfx("click")` → `Assets/Audio/Sfx/click.wav` |

也可以直接写完整地址（带 `/` 就原样使用，不再拼目录）：

```csharp
AudioKit.PlaySfx("Assets/Audio/Sfx/UI/click.wav");   // 分子目录也行
```

## 注意

- 音频文件要**加进 YooAsset 资源收集器**，否则运行时加载不到。
- 两个文件夹的路径可以在 `GameController` 的「音频」面板里改（`SfxFolder` / `BgmFolder`）。
- 名字别重名：同名的 `click.wav` 和 `click.ogg` 只会用到排在前面的那个。
- 格式建议：BGM 用 `.ogg`（体积小、可循环），短音效用 `.wav`。