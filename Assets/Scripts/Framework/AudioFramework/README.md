# 音频框架

一棵常驻的 `[Audio]` 根节点 + 1 个 BGM 通道 + N 个音效通道。不做 AudioMixer，
总音量走 `AudioListener.volume`，BGM / 音效音量直接乘到各自 `AudioSource.volume`，手机小游戏够用。

## 一眼看懂

```
GameController.Awake     audio = new AudioManager()                     只创建，不初始化
ProcedureInitAudio       GameController.Instance.Audio.Init(...)        建 [Audio] 根节点 + 铺通道
                         InitAudio 成功后用存档里的音量覆盖默认值
业务                     AudioKit.PlayBgm("lobby") / AudioKit.PlaySfx("click")
切场景                   [Audio] 根节点是 DontDestroyOnLoad，BGM 不会断
改音量                   AudioKit.BgmVolume = 0.5f 等，自动写回 settings.json
退出                     GameController.OnDestroy -> audio.Shutdown()
```

音频文件放 `Assets/Audio/Bgm` 和 `Assets/Audio/Sfx`，代码里只写短名。

## 用法

统一走静态入口 `AudioKit`（就是 `GameController.Instance.Audio` 的转发，方便少写一长串）。
传**短名**时按 `SfxFolder` / `BgmFolder` + 后缀自动补成真实地址，后缀不用写：

```csharp
AudioKit.PlayBgm("lobby");      // -> Assets/Audio/Bgm/lobby.ogg
AudioKit.PlaySfx("click");      // -> Assets/Audio/Sfx/click.wav

AudioKit.PlaySfx("Assets/Audio/Sfx/UI/click.wav");   // 带 / 就按完整地址用，不再拼目录
```

BGM：

```csharp
AudioKit.PlayBgm("lobby");                 // 默认淡入
AudioKit.PlayBgm("battle", true, 1f);      // loop + 淡入 1 秒

AudioKit.StopBgm();                        // 淡出
AudioKit.PauseBgm();
AudioKit.ResumeBgm();

bool playing = AudioKit.IsBgmPlaying;
string current = AudioKit.CurrentBgm;      // 没在播时是 null
```

正在放别的曲子时调 `PlayBgm`，会先淡出旧的再淡入新的（淡出占一半时长）。

音效：

```csharp
// 2D：界面点击、系统提示
AudioKit.PlaySfx("click");

// 3D：世界坐标
AudioKit.PlaySfxAt("hit", hitPoint);

// 3D 跟随：引擎声这种要跟着物体走的
AudioHandle handle = AudioKit.PlaySfxOn("engine", car.transform);

handle.IsPlaying;              // 通道被抢占后变 false
handle.Stop();                 // 停
handle.SetVolumeScale(0.5f);   // 这一次单独压音量

AudioKit.StopAllSfx();
```

一次播放返回 `AudioHandle`。像引擎声这种循环音效，记得留着句柄，不用时 `Stop()`。
短音效不用管，播完通道自己回收。

音量（会写回 `settings.json`，下次启动自动套用）：

```csharp
AudioKit.MasterVolume = 0.8f;   // 总音量，写 AudioListener.volume
AudioKit.BgmVolume    = 0.5f;
AudioKit.SfxVolume    = 0.7f;
AudioKit.MuteAll      = true;   // 静音，但不改上面那三个值
```

## 配置

`GameController` Inspector 的「音频」就是 `AudioInitOptions`：

| 字段 | 作用 |
| --- | --- |
| `RootName` | 根节点名字，默认 `[Audio]` |
| `DontDestroyOnLoad` | 切场景保留，默认开 |
| `MaxSfxVoices` | 同时能播的音效数，默认 16，超了抢最早开始的通道 |
| `MasterVolume` / `BgmVolume` / `SfxVolume` | 初始音量（启动后会被存档里的值覆盖） |
| `SfxMinDistance` / `SfxMaxDistance` | 3D 音效衰减范围，默认 1 ~ 30 米 |
| `DefaultFade` | BGM 默认淡入淡出时长，默认 0.4 秒 |
| `SfxFolder` / `BgmFolder` | 短名从哪个目录找，默认 `Assets/Audio/Sfx/`、`Assets/Audio/Bgm/` |
| `SearchExtensions` | 短名依次尝试的后缀，默认 `.wav` / `.ogg` / `.mp3` / `.aiff` |
| `LogOnInit` | 初始化完打一条日志 |

## 目录

| 文件 | 说明 |
| --- | --- |
| `AudioOptions.cs` | `AudioInitOptions` 配置 + `AudioHandle` 句柄 + `AudioVoice` 通道 |
| `AudioManager.cs` | 管理器本体：BGM / 音效 / 音量 / 资源引用计数 / 短名补路径 |
| `AudioKit.cs` | 全局快捷入口，业务代码用这个 |

音频文件目录见 `Assets/Audio/README.md`。

## 注意

- **音频资源必须进资源收集器**，地址就是 `LoadAssetAsync` 用的 location（跟 UI 预制体一个规矩）。
- 短名会依次试 `SearchExtensions` 里的后缀，**同名的不同后缀只会用到排在前面的那个**，别放重名文件。
- `AudioKit` 在音频初始化之前只会打一条警告，不抛异常，方便流程早期的代码照常写。
- 同一个地址的 `AudioClip` 只加载一次，按引用计数复用：BGM 在播期间、音效在响期间各占一次引用，播完自动释放。
- **音量只有一套**（AudioListener + 每个通道的 scale），没有 AudioMixer。以后要做总线滤波 / 混音，再在这一层接 Mixer。
- `MuteAll` 不影响 `MasterVolume` 的值，只是把 `AudioListener.volume` 压到 0。
- 淡入淡出按 `Time.unscaledDeltaTime` 走，暂停（`timeScale = 0`）时照样完成。