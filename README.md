# 上课录制

在指定课程期间使用麦克风自动录制课堂音频。

## 功能

- 在【应用设置】→【上课录制】中勾选需要录制的科目与录音保存目录（支持全选/清空）
- 录音自动保存为 `保存目录/年/月/日/课程名称-上课时间(HH_mm).m4a`（Opus 为 `.opus`）
- 可设置上课前提前 X 分钟开始录制、下课后继续录制 Y 分钟；若下一节课已开始，上一段录音会被提前结束并正常保存
- 滚动存储：录音目录超过设定容量（最小 256 MB）后自动删除最早的录音
- 设置页显示录音目录当前占用与文件数量
- 可选择麦克风设备；未选择时使用系统默认麦克风
- 麦克风实时电平指示
- 试录 10 秒：不必等到上课即可验证麦克风与编码设置，结果保存到 `录音目录/test/`
- 可选 AAC / Opus 编码与高 / 中 / 低质量预设，并显示按容量估算的可录时长
- 可选在开始录制前弹出提示浮窗
- 默认保持麦克风常开，可关闭
- 意外恢复：应用在录制过程中退出（含手动退出）后，重新启动会把未完成的片段保存到
  `保存目录/recovery/年-月-日-课程名称-上课时间(HH_mm)-片段编号.m4a`，并在未到结束时间时继续录制

## 实现说明

- 仅支持 Windows（AAC 编码依赖系统 Media Foundation）。
- 采集统一转换为单声道 48 kHz：优先让 WASAPI 直接输出 48 kHz 单声道（由系统做重采样），
  设备不支持时回退到设备默认格式，编码时再转换。
- 录音期间每 10 秒把 PCM 临时文件刷盘，因此意外退出最多丢失约 10 秒音频。
- 麦克风在录制过程中失效时，当前片段会按意外恢复规则保存到 `recovery` 目录，之后持续重试打开麦克风。
- 系统没有任何可用麦克风时，设置页会显示「未找到可用麦克风」，点击「重试/刷新列表」才会重新枚举设备
  （不会自动探测新设备）。

## 质量预设

| 编码 | 高 | 中 | 低 | 容器 |
| --- | --- | --- | --- | --- |
| Opus | 64 kbps | 48 kbps | 24 kbps | Ogg（`.opus`） |
| AAC | 160 kbps | 128 kbps | 96 kbps | MPEG-4（`.m4a`） |

AAC 的码率下限受 Windows Media Foundation 限制（48 kHz 下仅支持 96/128/160/192 kbps），
这也是 AAC 体积明显大于 Opus 的原因。默认使用 Opus / 中质量。

## 调试

Debug 构建内置了两个测试开关（Release 构建中不存在），便于在没有麦克风或课表数据的环境中验证录音流程：

| 环境变量 | 作用 |
| --- | --- |
| `CLASSISLAND_CLASSRECORDER_SYNTHETIC_MIC=1` | 用 440 Hz 合成正弦波代替麦克风输入，并在设备列表中显示「（测试）合成麦克风」 |
| `CLASSISLAND_CLASSRECORDER_TEST_WINDOW="HH:mm-HH:mm\|课程名"` | 忽略课表，直接注入一个录音时间窗口 |

示例：

```powershell
$env:CLASSISLAND_CLASSRECORDER_SYNTHETIC_MIC = "1"
$env:CLASSISLAND_CLASSRECORDER_TEST_WINDOW = "08:00-08:45|语文"
dotnet run --project ClassIsland.Desktop\ClassIsland.Desktop.csproj -c Debug -- `
  -epp E:\Documents\ClassIsland\ClassIsland.ClassRecorderPlugin\bin\Debug\net8.0-windows
```

注意：应用的「精确时间」可能与系统时间存在偏差（NTP 校时），验证定时停止时需要留意。
