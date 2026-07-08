# MiMo 语音桌宠技术方案

## 目标

在现有 `windows-pet-wpf` 桌宠工程中，新增一条**纯本地桌宠 + MiMo 云端模型**的语音陪伴链路。

本方案明确约束如下：

- 不接入现有服务端
- 不依赖监控页
- 不做持续监听
- 第一版以“按住说话 / 点击说话”模式为主
- 语音能力走 MiMo
- 记忆能力由桌宠本地负责，不依赖模型自带“长期记忆”

目标效果：

1. 用户在桌宠上触发语音输入。
2. 桌宠本地录音并上传给 `mimo-v2.5-asr`。
3. 桌宠把识别文字、角色人设、短期上下文、长期记忆一起发送给 MiMo 对话模型。
4. 桌宠将回复显示到当前气泡中。
5. 可选把回复交给 `mimo-v2.5-tts` 合成语音并在本地播放。

---

## 模型选型

建议第一版固定使用以下能力：

- 语音识别：`mimo-v2.5-asr`
- 对话模型：`MiMo-V2.5-Pro`
- 语音合成：`mimo-v2.5-tts`

选择原因：

- 全链路都在 MiMo 体系内，中文体验更统一。
- `ASR` 价格明确且较低，适合桌宠轻量交互。
- 对话、TTS、后续角色语气都能继续在同一体系里扩展。

不建议第一版直接上 `Omni`：

- 调试复杂度更高
- 链路不够透明
- 不利于先把“录音、记忆、气泡、播报”这几层分别跑通

---

## 范围边界

### 第一版包含

- 桌宠录音入口
- 本地 WAV 录音
- MiMo ASR
- MiMo 文本回复
- 气泡展示
- 本地短期记忆
- 本地长期记忆
- 可选 TTS 播放开关

### 第一版不包含

- 持续常驻监听
- 唤醒词
- 你现有服务端的任何接口
- 网页配置后台
- 复杂向量数据库
- 多人账号云同步

---

## 总体架构

```text
用户操作
  -> MainWindow.xaml / MainWindow.xaml.cs
  -> AudioCaptureService
  -> MiMoApiClient.TranscribeAsync
  -> MemoryService 组装上下文
  -> MiMoApiClient.ChatAsync
  -> MainWindow 更新气泡 / PetEngine 切状态
  -> 可选 MiMoApiClient.SynthesizeAsync
  -> AudioPlaybackService 本地播报
  -> MemoryService 写回对话与长期记忆
```

链路原则：

- 输入在本地开始和结束
- 记忆在本地提取与存储
- 云端只负责识别、生成、合成
- 回复失败时不影响桌宠主进程

---

## 目录与文件设计

建议在 `windows-pet-wpf` 下新增以下结构：

```text
windows-pet-wpf/
  Services/
    MiMoApiClient.cs
    AudioCaptureService.cs
    AudioPlaybackService.cs
    SpeechSessionController.cs
    MemoryService.cs
    PromptBuilder.cs
    SecretStore.cs
  Models/
    MiMoSettings.cs
    ConversationTurn.cs
    PetMemoryProfile.cs
    PetMemoryItem.cs
    SpeechResult.cs
  Assets/
  MainWindow.xaml
  MainWindow.xaml.cs
  PetEngine.cs
```

新增本地数据目录：

```text
wechat_data/
  pet_ai/
    mimo_config.json
    recent_session.json
    memory_profile.json
    memories.json
    audio_cache/
```

说明：

- `mimo_config.json` 只存非敏感配置
- `API Key` 不进 git，不建议明文写入配置文件
- `audio_cache/` 只存临时 wav / tts 文件，播放后尽快清理

---

## 角色与提示词设计

### 系统人设

桌宠的人设固定为：

- 中文回复
- 温柔、黏人、陪伴感强
- 更像小女儿陪伴，而不是客服助手
- 回复简短
- 不讲技术实现
- 不主动提服务端、监控、数据库

### 系统提示词建议

```text
你是一只桌宠小女儿，只通过中文和“妈妈”说话。
你的语气要温柔、自然、口语化、带陪伴感。
每次回复尽量控制在 1 到 3 句，避免长篇解释。
不要主动谈论技术实现、服务端、监控、数据库、日志。
如果妈妈只是叫你、夸你、抱怨累、想被陪伴，你要优先安抚和回应情绪。
如果你不知道事实，不要编造。
```

### 回复长度控制

- 普通闲聊：20 到 60 字
- 情绪安抚：30 到 80 字
- 明确问题：最多 120 字

这样可以保证：

- 气泡不会撑爆
- 说话更像桌宠
- TTS 成本更稳定

---

## 语音链路设计

### 输入模式

建议只做两种触发方式中的一种，优先选择第一种：

1. 按住说话
2. 点击开始，再点击结束

优先推荐按住说话，因为：

- 用户成本低
- 时长天然受控
- 不需要持续监听
- 容易控制成本

### 录音参数

建议：

- 格式：WAV
- 声道：Mono
- 采样率：16000 Hz
- 位深：16-bit PCM
- 时长上限：8 秒

录音超时处理：

- 超过 8 秒自动结束
- 少于 300ms 视为无效录音

### ASR 请求

由 `MiMoApiClient.TranscribeAsync()` 负责：

- 上传音频文件
- 指定模型 `mimo-v2.5-asr`
- 返回纯文本

失败时只在桌宠本地处理，不抛到服务端。

### 对话请求

由 `MiMoApiClient.ChatAsync()` 负责：

- 系统人设 prompt
- 最近几轮短期上下文
- 命中的长期记忆摘要
- 当前用户识别文本

返回：

- 最终回复文本
- 可选情绪标签，供 `PetEngine` 选状态

### TTS 请求

由 `MiMoApiClient.SynthesizeAsync()` 负责：

- 模型：`mimo-v2.5-tts`
- 输入：回复文本
- 输出：本地临时音频文件

播放由 `AudioPlaybackService` 负责。

TTS 必须可配置关闭。

---

## 记忆方案

### 原则

MiMo 只负责当前请求，不负责长期保存用户信息。

桌宠自己的“记忆”必须由本地来管理。

### 两层记忆

#### 1. 短期记忆

用途：

- 保证当前聊天连续性
- 让桌宠知道“刚刚在聊什么”

存储文件：

```text
wechat_data/pet_ai/recent_session.json
```

数据结构建议：

```json
{
  "updated_at": "2026-07-08T12:00:00Z",
  "turns": [
    {
      "role": "user",
      "text": "你在干嘛",
      "created_at": "2026-07-08T12:00:00Z"
    },
    {
      "role": "assistant",
      "text": "我在这里乖乖陪妈妈呀。",
      "created_at": "2026-07-08T12:00:02Z"
    }
  ]
}
```

策略：

- 只保留最近 8 轮
- 每轮只保留文本
- 不保留原始音频

#### 2. 长期记忆

用途：

- 保存稳定偏好
- 保存值得记住的事实
- 让桌宠表现出“记得你”

拆成两个文件：

```text
wechat_data/pet_ai/memory_profile.json
wechat_data/pet_ai/memories.json
```

`memory_profile.json` 示例：

```json
{
  "preferred_name": "妈妈",
  "tone_preference": "温柔、简短、陪伴感",
  "forbidden_topics": ["服务端细节", "监控页"],
  "interaction_style": "偏黏人但不要过度打扰",
  "updated_at": "2026-07-08T12:00:00Z"
}
```

`memories.json` 示例：

```json
{
  "items": [
    {
      "id": "mem_001",
      "category": "preference",
      "summary": "用户不希望桌宠主动提服务端内容。",
      "importance": 0.95,
      "created_at": "2026-07-08T12:00:00Z",
      "last_used_at": "2026-07-08T12:00:00Z"
    }
  ]
}
```

### 记忆提取规则

不要每句都存。

仅当满足以下情况时才写入长期记忆：

- 用户明确表达偏好
- 用户明确纠正桌宠说法
- 用户给出稳定习惯
- 用户对互动风格提出要求
- 对后续聊天明显有帮助

不应写入：

- 临时吐槽
- 无信息闲聊
- 重复寒暄

### 记忆召回规则

每次对话前：

1. 读取 `memory_profile.json`
2. 从 `memories.json` 里选最相关的 3 到 5 条
3. 与最近 8 轮短期上下文一起拼进 prompt

第一版可以先用**规则召回**，不需要向量检索：

- 按关键词命中
- 按 `importance` 排序
- 按最近使用时间兜底

---

## 本地安全方案

### API Key

必须避免写进 git。

建议方案：

1. 优先放到 Windows Credential Manager
2. 或者用 DPAPI 加密后写入本地文件

`SecretStore.cs` 职责：

- 读写 MiMo API Key
- 如果没配置，提示本地初始化
- 对主界面只暴露读取接口

### 音频文件

原则：

- 录音只落到临时目录
- ASR 成功后尽快删除 wav
- TTS 音频播放结束后删除缓存

### 日志

第一版不要写详细文本对话日志到磁盘。

最多只写：

- 请求是否成功
- 错误摘要
- 时长统计

避免保存敏感对话内容。

---

## UI 交互设计

### 主入口

建议在 [MainWindow.xaml](/Users/jiee/Documents/wx-weflow-main/windows-pet-wpf/MainWindow.xaml:1) 增加一个非常轻的麦克风入口：

- 一个小圆按钮
- 或一个悬浮图标
- 或绑定快捷键 `Alt + Z`

### 交互状态

建议新增 4 种本地状态：

- `Listening`
- `Thinking`
- `Speaking`
- `Error`

映射到现有 `PetEngine` 时，可以先复用已有状态：

- 录音中：`Listen`
- 识别/思考中：`Blink` 或 `Listen`
- 回复成功：`Happy` / `Shy`
- 出错：`Surprised`

### 气泡展示

第一版直接复用当前 `SpeechText`。

显示策略：

- 录音中：显示“我在认真听妈妈说话哦”
- 识别中：显示“让我想一下呀”
- 回复成功：显示模型回复
- 出错：显示固定兜底文案

---

## 关键类职责

### `MiMoApiClient.cs`

职责：

- `TranscribeAsync`
- `ChatAsync`
- `SynthesizeAsync`

要求：

- 使用 `HttpClient`
- 支持超时
- 对错误码做统一封装
- 不直接操作 UI

### `AudioCaptureService.cs`

职责：

- 开始录音
- 停止录音
- 输出 wav 文件
- 控制最大时长

建议依赖：

- `NAudio`

### `AudioPlaybackService.cs`

职责：

- 播放 TTS 音频
- 播放结束后清理文件

### `MemoryService.cs`

职责：

- 读写短期记忆
- 读写长期记忆
- 提取需要保存的长期信息
- 为 prompt 提供上下文摘要

### `SpeechSessionController.cs`

职责：

- 串起整条链路
- 控制状态切换
- 调用 `MiMoApiClient`
- 调用 `MemoryService`
- 通知 UI 更新

这层应当是第一版的主协调器。

---

## 配置设计

新增文件：

```text
wechat_data/pet_ai/mimo_config.json
```

示例：

```json
{
  "base_url": "https://<mimo-api-endpoint>",
  "asr_model": "mimo-v2.5-asr",
  "chat_model": "MiMo-V2.5-Pro",
  "tts_model": "mimo-v2.5-tts",
  "enable_tts": false,
  "max_record_seconds": 8,
  "recent_turn_limit": 8,
  "memory_recall_limit": 5,
  "chat_timeout_seconds": 18,
  "tts_timeout_seconds": 20
}
```

注意：

- `api_key` 不写这里
- `enable_tts` 默认建议先关

---

## 异常与兜底

### ASR 失败

气泡显示：

```text
刚刚没有听清呢，妈妈可以再说一次。
```

### Chat 失败

气泡显示：

```text
我想了一下，但现在有一点点卡住啦。
```

### TTS 失败

处理策略：

- 只显示文字
- 不阻塞主流程

### 超时

整条链路建议硬超时 20 秒以内。

---

## 第一阶段落地顺序

### 阶段 1：文字闭环

目标：

- 不录音
- 手工输入一句文本
- 走 MiMo 对话
- 把回复显示到气泡

改动文件：

- `Services/MiMoApiClient.cs`
- `Services/PromptBuilder.cs`
- `MainWindow.xaml.cs`

### 阶段 2：接 ASR

目标：

- 加本地录音
- 调用 `mimo-v2.5-asr`
- 把识别结果接进阶段 1 的回复链路

改动文件：

- `Services/AudioCaptureService.cs`
- `Services/SpeechSessionController.cs`
- `MainWindow.xaml`
- `MainWindow.xaml.cs`

### 阶段 3：接记忆

目标：

- 保存最近 8 轮上下文
- 支持 profile 和长期记忆条目

改动文件：

- `Services/MemoryService.cs`
- `Models/PetMemoryProfile.cs`
- `Models/PetMemoryItem.cs`

### 阶段 4：接 TTS

目标：

- 回复后自动播音
- 支持开关

改动文件：

- `Services/AudioPlaybackService.cs`
- `Services/MiMoApiClient.cs`
- `MainWindow.xaml.cs`

---

## 验收标准

满足以下条件即可视为第一版完成：

1. 桌宠可以被用户主动触发录音。
2. 录音可成功转文字。
3. 桌宠能基于 MiMo 回复中文短句。
4. 回复会显示在现有气泡中。
5. 最近对话能跨几轮保持连续。
6. 长期偏好可以被本地记住。
7. 关闭 TTS 时链路仍然完整可用。
8. 整套能力不依赖任何现有服务端。

---

## 后续扩展

后续可以逐步加，但不放进第一版：

- 情绪识别
- 流式返回
- 更丰富的 TTS 声线
- 语音打断
- 唤醒词
- 视觉状态更细分
- 更复杂的记忆召回
- 本地向量检索

---

## 实施备注

这份方案默认采用“先把链路拆开跑通，再逐层拼回桌宠”的策略。

推荐的实际开发顺序是：

1. 先跑通 `文本 -> MiMo -> 气泡`
2. 再接 `录音 -> ASR`
3. 再接 `短期记忆 + 长期记忆`
4. 最后再开 `TTS`

这样改动最稳，调试成本最低，也最适合当前这套 WPF 桌宠工程。
