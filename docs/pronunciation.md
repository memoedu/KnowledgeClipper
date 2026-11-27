# 中英文朗读

桌面新合成按实际读出的文字选模型。含汉字的文本用 MiMo `mimo-v2.5-tts` 和标准普通话，英文用 OpenAI `gpt-realtime-2.1-mini` 和自然美式英语。音色、语速和音量随请求传递。朗读指令和原文分别发送，单词或短语正常读一遍。

自动英文朗读继续保留原先的过滤规则；手动选择音色时，中英混合材料仍按原文顺序朗读。两种路径的指令均依据实际送去合成的文本确定。

新录音缓存按模型路由和朗读参数区分。历史重播继续核验并播放原录音，包括两种旧缓存格式。明确重新朗读才生成新录音。共享 MimoTTS 客户端优先按完整句子切分长材料，其次使用分句标点和空格。

桌面 helper 依赖支持显式 `provider` 的 MimoTTS，沿用已有的 MiMo 凭据和本机 OpenAI 外部环境文件。英文音色可选 `marin` 和 `cedar`，旧的不兼容音色按各模型默认音色处理。模型错误由对应的朗读操作报告。

离线回归：`KnowledgeClipper.SpeechUsage.Tests` 与 `KnowledgeClipper.LanguageHistory.Tests`，均已接入维护检查。

真实合成检查在仓库根目录运行已构建的 `KnowledgeClipper.SpeechUsage.Tests.dll --pronunciation <新的 D 盘产物目录>`。用公开的中文与英文单词、句子核验实际模型，保存可试听 MP3 并验证解码。检查无需播放音频或打开用户草稿。

接口依据：[MiMo 官方语音合成文档](https://mimo.mi.com/docs/en-US/quick-start/usage-guide/audio/speech-synthesis-v2.5)。
