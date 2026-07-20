# RoxyBuildTool 文档

RoxyBuildTool 是通用的多平台 C++/.NET 构建系统；大型游戏和引擎代码库只是用于检验其扩展性的一类高要求场景，而不是内置的产品类型。它以 NuGet 库发布，项目使用普通的 .NET Console App 编写强类型 C# 规则，并以 `dotnet run` 作为统一入口，把同一份构建定义转换为 Visual Studio、`compile_commands.json`、FASTBuild 等后端所需的文件。平台支持由普通 .NET 插件包提供，core 不预设 Game、Editor 或 Client 等领域枚举。

当前文档：

- [总体架构设计](architecture.md)：目标、核心模型、fragment、配置矩阵、依赖图、后端、平台插件、主机适配和分阶段实现计划。

文档状态为 **Proposed**。首个实现阶段应以架构文档中的 MVP 和验收条件为准；公开 API 尚未冻结。
