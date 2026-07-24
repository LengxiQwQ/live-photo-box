# Copilot Instructions / Copilot 指令

## Project Guidelines / 项目指南

- User prefers code to stay clean, clear, non-redundant, and avoid bloated or messy implementations. / 代码应干净、清晰、无冗余，避免臃肿或混乱的实现。
- All user interface text in the project should use RESW multilingual resources instead of hardcoded strings; implementations should remain clean and minimal, avoiding bloat. / 所有界面文字应使用 RESW 多语言资源，不得硬编码字符串；实现应保持简洁，避免臃肿。
- Crash log settings page should adopt a minimalist, user-centric expression: display "Last Crash Time/Not Crashed," avoiding explanatory fluff. The crash log directory should use the more conventional `Logs` path instead of an additional `Logs/Crash` subdirectory. / 崩溃日志设置页应采用简洁、以用户为中心的表达：显示"上次崩溃时间 / 未崩溃"，避免过度解释。崩溃日志目录应使用更常规的 `Logs` 路径，而非额外创建 `Logs/Crash` 子目录。
