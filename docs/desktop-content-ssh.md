# 桌面剪藏的可选 SSH 内容处理

Windows 桌面仍负责捕获、原内容校验、草稿、审批和导出。开启后，桌面把语言、翻译草稿原来发出的请求通过 SSH 送到指定 Mac。官方 OpenAI Responses 和 DeepSeek Chat Completions 仍使用原地址、模型、提示、输入和校验重试。快捷「问」、聊天、词典、语音、OCR 和摘录沿用各自的原入口。

## 启用

1. 在 Mac 的剪藏应用数据目录部署与桌面同一提交的 `helpers/desktop_content_ssh.py`。使用现有 SSH 账号及已验证的主机密钥。工作进程只在桌面请求时运行。
2. 创建该应用的独立 spool 目录，权限为 `0700`。生成一个小写、去连字符的 UUID 写入该目录的 `authority` 文件，同时在专用配置写入 `spool`（该目录的绝对路径）和 `authority`（同一个 UUID）。两个文件都只允许该账号读写。确认备份和恢复同时保留 authority、已占用的请求和已存的回执。
3. 在这个 helper 的 Mac 进程环境配置该应用获准使用的提供方凭据。OpenAI 使用 `OPENAI_KNOWLEDGECLIPPER_API_KEY`，DeepSeek 使用 `DEEPSEEK_KNOWLEDGECLIPPER_API_KEY`。用同一 SSH 启动方式检查所需变量确实存在，并用独立样本验证提供方允许原模型。Windows 继续使用原有的快捷「问」配置。
4. 在 Windows 剪藏工作目录创建 `desktop-content-ssh.json`，填入下列字段。示例中的路径是占位符。仅受支持的提供方地址会启用远端处理。

```json
{
  "host": "known-ssh-host",
  "remotePython": "/opt/homebrew/bin/python3",
  "remoteHelper": "/path/to/desktop_content_ssh.py",
  "remoteConfig": "/path/to/desktop-content-worker.json",
  "authority": "0123456789abcdef0123456789abcdef"
}
```

使用 RemNote 时先核对 Windows 配置内固定的根节点 ID 和本地导出目标。插件在目标根 ID 不存在或与固定标题、位置不符时退出，保留原审阅的无写入结果。

## 中断和回退

同一草稿、同一请求字节和校验次数对应同一个请求身份。Mac 先持久化该身份，再调用提供方。已保存的回执可由原预览重试直接取回。若第一次连接尚未交到 Mac，恢复连接后同一请求可正常执行。若 Mac 已占用身份但尚无回执，原草稿显示待核对，自动重试不会再次调用提供方。请保留对应的两端记录并核实这一次调用。

缺少指定提供方的 Mac 应用凭据时，原预览会显示尚未提交的原因。Mac 不会占用该请求身份，配置完成后可重试同一草稿。已有回执可以在没有凭据时读回。

移走 Windows 的 `desktop-content-ssh.json` 即可让新请求恢复原本机路径。已经发起的草稿仍保留首次请求的远端绑定，可在原预览中读回。每份请求和响应上限为 4 MiB。Windows 请求、两端回执可能含草稿内容，放在应用私有数据目录。保留未决草稿对应的完整状态，避免把既有 authority 指向空目录。
