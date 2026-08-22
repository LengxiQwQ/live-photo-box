using Xunit;

// 历史详细度测试会临时修改全局 AppSettings（IsDetailedHistoryEnabled），
// 与其它历史写入测试串行执行，避免全局设置被并行测试互相干扰。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
